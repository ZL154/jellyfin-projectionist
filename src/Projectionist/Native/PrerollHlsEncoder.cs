using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Projectionist.Native;

/// <summary>
/// Converts a preroll into HLS segments that match an episode stream and
/// caches them under Jellyfin's cache folder. One conversion per (clip,
/// format) pair; later plays read from disk.
/// </summary>
public sealed class PrerollHlsEncoder
{
    private const int SegmentSeconds = 3;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly ILogger<PrerollHlsEncoder> _logger;
    private readonly IApplicationPaths _appPaths;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ConcurrentDictionary<string, Lazy<Task<PrerollHlsEntry?>>> _inFlight = new();
    private static readonly TimeSpan UnusedLifetime = TimeSpan.FromDays(60);
    private int _swept;

    private readonly IServerConfigurationManager? _serverConfig;
    private readonly ConcurrentDictionary<string, byte> _warmedTargets = new();

    public PrerollHlsEncoder(ILogger<PrerollHlsEncoder> logger, IApplicationPaths appPaths, IMediaEncoder mediaEncoder, IServerConfigurationManager? serverConfig = null)
    {
        _serverConfig = serverConfig;
        _logger = logger;
        _appPaths = appPaths;
        _mediaEncoder = mediaEncoder;
    }

    public string CacheRoot => Path.Combine(_appPaths.CachePath, "projectionist-hls");

    /// <summary>
    /// Returns the converted preroll, converting it first if needed.
    /// Concurrent callers for the same pair share one ffmpeg run, and a
    /// caller that gives up (timeout) doesn't cancel it: the result is still
    /// cached for the next play.
    /// </summary>
    public Task<PrerollHlsEntry?> GetOrEncodeAsync(string prerollPath, HlsTarget target)
    {
        if (string.IsNullOrWhiteSpace(prerollPath) || !File.Exists(prerollPath))
        {
            return Task.FromResult<PrerollHlsEntry?>(null);
        }

        if (Interlocked.Exchange(ref _swept, 1) == 0)
        {
            _ = Task.Run(SweepCache);
        }

        var key = CacheKey(prerollPath, target);
        var cached = TryLoad(key);
        if (cached is not null) return Task.FromResult<PrerollHlsEntry?>(cached);

        var lazy = _inFlight.GetOrAdd(key, k => new Lazy<Task<PrerollHlsEntry?>>(
            () => Task.Run(() => EncodeAsync(prerollPath, target, k))));
        return lazy.Value.ContinueWith(
            t =>
            {
                _inFlight.TryRemove(key, out _);
                return t.IsCompletedSuccessfully ? t.Result : null;
            },
            TaskScheduler.Default);
    }

    /// <summary>Path of a cached file, or null if the name isn't one we produce.</summary>
    public string? ResolveFile(string cacheKey, string fileName)
    {
        if (cacheKey.Length != 16 || !cacheKey.All(Uri.IsHexDigit)) return null;
        var ok = fileName == "init.mp4"
            || (fileName.EndsWith(".ts", StringComparison.Ordinal) || fileName.EndsWith(".m4s", StringComparison.Ordinal))
               && fileName[..fileName.LastIndexOf('.')].All(char.IsAsciiDigit)
               && fileName.LastIndexOf('.') > 0;
        if (!ok) return null;
        var path = Path.Combine(CacheRoot, cacheKey, fileName);
        return File.Exists(path) ? path : null;
    }

    private PrerollHlsEntry? TryLoad(string key)
    {
        var dir = Path.Combine(CacheRoot, key);
        var meta = Path.Combine(dir, "entry.json");
        if (!File.Exists(meta)) return null;
        try
        {
            var entry = JsonSerializer.Deserialize<PrerollHlsEntry>(File.ReadAllText(meta), JsonOpts);
            if (entry is null || entry.Segments.Count == 0) return null;
            if (entry.InitFile is not null && !File.Exists(Path.Combine(dir, entry.InitFile))) return null;
            if (!entry.Segments.All(s => File.Exists(Path.Combine(dir, s.File)))) return null;

            // Mark as used so the sweep keeps it.
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(meta) > TimeSpan.FromDays(1))
            {
                File.SetLastWriteTimeUtc(meta, DateTime.UtcNow);
            }

            return entry;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private async Task<PrerollHlsEntry?> EncodeAsync(string prerollPath, HlsTarget target, string key)
    {
        var ffmpeg = _mediaEncoder.EncoderPath;
        if (string.IsNullOrEmpty(ffmpeg))
        {
            _logger.LogWarning("[Projectionist] ffmpeg path unknown; cannot convert prerolls for native apps");
            return null;
        }

        var dir = Path.Combine(CacheRoot, key);
        var work = dir + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(work);
        var ext = target.IsFmp4 ? "m4s" : "ts";
        var sw = Stopwatch.StartNew();

        try
        {
            var hasAudio = await HasAudioAsync(prerollPath).ConfigureAwait(false);

            // Jellyfin's own hardware encoder first (a 9 s 1080p clip: ~1.7 s
            // on NVENC vs ~19 s with x265 "fast" on a typical server CPU), then
            // software if the hardware attempt fails for any reason.
            var hw = HardwareEncoderFor(target.VideoCodec);
            var encoders = hw is null ? new[] { (string?)null } : new[] { hw, null };
            string? usedEncoder = null;
            foreach (var encoder in encoders)
            {
                foreach (var f in Directory.EnumerateFiles(work)) File.Delete(f);
                var (code, err) = await RunFfmpegAsync(ffmpeg, work, BuildArgs(prerollPath, target, ext, hasAudio, encoder)).ConfigureAwait(false);
                if (code == 0)
                {
                    usedEncoder = encoder ?? (target.VideoCodec == "hevc" ? "libx265" : "libx264");
                    break;
                }

                _logger.Log(
                    encoder is null ? LogLevel.Error : LogLevel.Warning,
                    "[Projectionist] converting {File} for {Target} with {Encoder} failed (exit {Code}){Next}: {Err}",
                    Path.GetFileName(prerollPath),
                    target.Key,
                    encoder ?? "software",
                    code,
                    encoder is null ? string.Empty : ", retrying in software",
                    err.Length > 400 ? err[^400..] : err);
            }

            if (usedEncoder is null) return null;

            var segments = ParsePlaylist(Path.Combine(work, "index.m3u8"));
            if (segments.Count == 0) return null;
            var entry = new PrerollHlsEntry(key, segments, target.IsFmp4 ? "init.mp4" : null);
            File.WriteAllText(Path.Combine(work, "entry.json"), JsonSerializer.Serialize(entry, JsonOpts));

            // Publish atomically so a half-written folder is never served.
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(work, dir);
            _logger.LogInformation("[Projectionist] converted {File} for native apps ({Target}, {Count} segments, {Secs:0.0}s) with {Encoder} in {Ms} ms",
                Path.GetFileName(prerollPath), target.Key, segments.Count, segments.Sum(s => s.Duration), usedEncoder, sw.ElapsedMilliseconds);
            return entry;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Projectionist] converting {File} for {Target} failed", Path.GetFileName(prerollPath), target.Key);
            return null;
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Conversions are keyed per clip and per episode format, so the cache
    /// only grows. Once per server run, drop entries nobody has played in
    /// <see cref="UnusedLifetime"/> and work folders a crash left behind.
    /// </summary>
    private void SweepCache()
    {
        try
        {
            if (!Directory.Exists(CacheRoot)) return;
            var removed = 0;
            foreach (var dir in Directory.EnumerateDirectories(CacheRoot))
            {
                var name = Path.GetFileName(dir);
                var isWork = name.Contains(".tmp-", StringComparison.Ordinal);
                var meta = Path.Combine(dir, "entry.json");
                var lastUsed = isWork || !File.Exists(meta) ? Directory.GetLastWriteTimeUtc(dir) : File.GetLastWriteTimeUtc(meta);
                var maxAge = isWork ? TimeSpan.FromDays(1) : UnusedLifetime;
                if (DateTime.UtcNow - lastUsed < maxAge || _inFlight.ContainsKey(name.Split('.')[0])) continue;
                Directory.Delete(dir, true);
                removed++;
            }

            if (removed > 0) _logger.LogInformation("[Projectionist] removed {Count} unused native preroll conversion(s)", removed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "[Projectionist] native preroll cache sweep failed");
        }
    }

    /// <summary>
    /// Convert every preroll for <paramref name="target"/> in the background,
    /// once per format per server run. The first episode in a new format
    /// waits for its own preroll; after that, whichever clip gets picked is
    /// already on disk.
    /// </summary>
    public void WarmUp(IEnumerable<string> prerollPaths, HlsTarget target)
    {
        if (!_warmedTargets.TryAdd(target.Key, 0)) return;
        var paths = prerollPaths.Where(File.Exists).Distinct(StringComparer.Ordinal).ToList();
        _ = Task.Run(async () =>
        {
            foreach (var p in paths)
            {
                await GetOrEncodeAsync(p, target).ConfigureAwait(false);
            }
        });
    }

    private static async Task<(int Code, string Err)> RunFfmpegAsync(string ffmpeg, string work, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = work,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        var stderr = proc.StandardError.ReadToEndAsync();
        _ = proc.StandardOutput.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        return (proc.ExitCode, await stderr.ConfigureAwait(false));
    }

    /// <summary>
    /// The hardware encoder matching Jellyfin's transcoding settings, or null
    /// for software. VAAPI/V4L2/RKMPP are left to software: they need device
    /// setup and upload filters that differ per machine.
    /// </summary>
    private string? HardwareEncoderFor(string videoCodec)
    {
        if (_serverConfig?.GetConfiguration("encoding") is not EncodingOptions o || !o.EnableHardwareEncoding) return null;
        var hevc = videoCodec == "hevc";
        return o.HardwareAccelerationType.ToString().ToLowerInvariant() switch
        {
            "nvenc" => hevc ? "hevc_nvenc" : "h264_nvenc",
            "amf" => hevc ? "hevc_amf" : "h264_amf",
            "qsv" => hevc ? "hevc_qsv" : "h264_qsv",
            "videotoolbox" => hevc ? "hevc_videotoolbox" : "h264_videotoolbox",
            _ => null,
        };
    }

    internal static IReadOnlyList<string> BuildArgs(string input, HlsTarget t, string ext, bool hasAudio, string? hwEncoder = null)
    {
        var args = new List<string> { "-hide_banner", "-nostats", "-loglevel", "error", "-y", "-i", input };

        // A clip without sound still needs an audio track: the episode has
        // one, and some players stall when a stream loses its audio at a
        // discontinuity. Generate silence for the clip's length.
        if (!hasAudio)
        {
            args.AddRange(new[] { "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo" });
        }

        // Letterbox into the episode's frame, at the episode's frame rate.
        args.AddRange(new[]
        {
            "-map", "0:v:0", "-map", hasAudio ? "0:a:0" : "1:a:0",
            "-vf", $"scale={t.Width}:{t.Height}:force_original_aspect_ratio=decrease,pad={t.Width}:{t.Height}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps={t.FrameRate},format=yuv420p",
            "-force_key_frames", $"expr:gte(t,n_forced*{SegmentSeconds})",
        });

        var hevc = t.VideoCodec == "hevc";
        switch (hwEncoder)
        {
            case null when hevc:
                // "ultrafast": a short clip, and speed decides whether the first
                // play waits. Measured 6.5 s vs 19 s ("fast") for 9 s of 1080p.
                args.AddRange(new[] { "-c:v", "libx265", "-preset", "ultrafast", "-crf", "22", "-x265-params", "log-level=error" });
                break;
            case null:
                args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-profile:v", "high" });
                break;
            case var e when e.EndsWith("_nvenc", StringComparison.Ordinal):
                // forced-idr: segment boundaries must be IDR frames.
                args.AddRange(new[] { "-c:v", e, "-preset", "p4", "-rc", "vbr", "-cq", hevc ? "23" : "21", "-b:v", "0", "-forced-idr", "1" });
                break;
            case var e when e.EndsWith("_qsv", StringComparison.Ordinal):
                args.AddRange(new[] { "-c:v", e, "-global_quality", "23", "-look_ahead", "0" });
                break;
            case var e when e.EndsWith("_amf", StringComparison.Ordinal):
                args.AddRange(new[] { "-c:v", e, "-rc", "cqp", "-qp_i", "22", "-qp_p", "24" });
                break;
            default:
                args.AddRange(new[] { "-c:v", hwEncoder, "-q:v", "60" });
                break;
        }

        if (hevc) args.AddRange(new[] { "-tag:v", "hvc1" });

        var audio = t.AudioCodec switch
        {
            "ac3" => new[] { "-c:a", "ac3", "-b:a", "384k" },
            "eac3" => new[] { "-c:a", "eac3", "-b:a", "384k" },
            "mp3" => new[] { "-c:a", "libmp3lame", "-b:a", "192k" },
            _ => new[] { "-c:a", "aac", "-b:a", "192k" },
        };
        args.AddRange(audio);
        args.AddRange(new[] { "-ar", "48000", "-ac", "2" });

        args.AddRange(new[]
        {
            "-f", "hls", "-hls_time", SegmentSeconds.ToString(CultureInfo.InvariantCulture),
            "-hls_playlist_type", "vod", "-hls_list_size", "0",
            "-hls_segment_type", t.IsFmp4 ? "fmp4" : "mpegts",
            "-hls_segment_filename", "%d." + ext,
        });
        if (!hasAudio) args.Add("-shortest");
        if (t.IsFmp4) args.AddRange(new[] { "-hls_fmp4_init_filename", "init.mp4" });
        args.Add("index.m3u8");
        return args;
    }

    private async Task<bool> HasAudioAsync(string input)
    {
        var probe = _mediaEncoder.ProbePath;
        if (string.IsNullOrEmpty(probe)) return true;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = probe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-v", "error", "-select_streams", "a", "-show_entries", "stream=index", "-of", "csv=p=0", input })
            {
                psi.ArgumentList.Add(a);
            }

            using var proc = Process.Start(psi);
            if (proc is null) return true;
            var output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync().ConfigureAwait(false);
            return proc.ExitCode != 0 || output.Trim().Length > 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    private static List<PrerollHlsSegment> ParsePlaylist(string path)
    {
        var result = new List<PrerollHlsSegment>();
        if (!File.Exists(path)) return result;
        double? duration = null;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var comma = line.IndexOf(',', StringComparison.Ordinal);
                var num = comma > 8 ? line[8..comma] : line[8..];
                duration = double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
            }
            else if (line.Length > 0 && line[0] != '#' && duration is not null)
            {
                result.Add(new PrerollHlsSegment(line, duration.Value));
                duration = null;
            }
        }

        return result;
    }

    private static string CacheKey(string prerollPath, HlsTarget target)
    {
        var info = new FileInfo(prerollPath);
        // Size + mtime make an edited clip re-convert instead of serving a stale copy.
        var raw = $"{prerollPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{target.Key}|v1";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16].ToLowerInvariant();
    }
}
