using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Projectionist.Services;

public sealed class LoudnessAnalyzer
{
    private readonly ILogger<LoudnessAnalyzer> _logger;
    private readonly IApplicationPaths _appPaths;
    private readonly ConcurrentDictionary<string, LoudnessResult> _cache = new();
    private bool _loaded;
    private readonly object _saveLock = new();

    public LoudnessAnalyzer(ILogger<LoudnessAnalyzer> logger, IApplicationPaths appPaths)
    {
        _logger = logger;
        _appPaths = appPaths;
    }

    public sealed class LoudnessResult
    {
        public string FileName { get; set; } = string.Empty;
        public double MeanDb { get; set; }
        public double MaxDb { get; set; }
        public DateTime AnalyzedUtc { get; set; }
        public long FileMTimeUtcTicks { get; set; }
    }

    public LoudnessResult? GetCached(string path)
    {
        EnsureLoaded();
        var key = path.ToLowerInvariant();
        if (!_cache.TryGetValue(key, out var hit)) return null;
        try
        {
            var info = new FileInfo(path);
            if (info.LastWriteTimeUtc.Ticks != hit.FileMTimeUtcTicks) return null;
        }
        catch { return null; }
        return hit;
    }

    public async Task<LoudnessResult?> AnalyzeAsync(string path)
    {
        var cached = GetCached(path);
        if (cached is not null) return cached;
        if (!File.Exists(path)) return null;

        var ffmpegPath = ResolveFfmpegPath();
        if (ffmpegPath is null)
        {
            _logger.LogWarning("[Projectionist] ffmpeg binary not found; loudness analysis disabled. Tried jellyfin-ffmpeg, /usr/bin/ffmpeg, PATH.");
            return null;
        }
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                ArgumentList =
                {
                    "-hide_banner", "-nostats",
                    "-i", path,
                    "-af", "volumedetect",
                    "-vn", "-sn", "-dn",
                    "-f", "null", "-",
                },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                _logger.LogWarning("[Projectionist] Process.Start({Bin}) returned null for {File}", ffmpegPath, path);
                return null;
            }
            var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync().ConfigureAwait(false);
            var (mean, max) = ParseVolumeDetect(stderr);
            if (double.IsNaN(mean) && double.IsNaN(max))
            {
                _logger.LogWarning("[Projectionist] ffmpeg ({Bin}) produced no volumedetect output for {File}. ExitCode={Code}. stderr head: {Err}",
                    ffmpegPath, path, proc.ExitCode, stderr.Length > 200 ? stderr.Substring(0, 200) : stderr);
                return null;
            }
            var info = new FileInfo(path);
            var result = new LoudnessResult
            {
                FileName = info.Name,
                MeanDb = mean,
                MaxDb = max,
                AnalyzedUtc = DateTime.UtcNow,
                FileMTimeUtcTicks = info.LastWriteTimeUtc.Ticks,
            };
            _cache[path.ToLowerInvariant()] = result;
            Save();
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Projectionist] loudness analysis failed for {Path}", path);
            return null;
        }
    }

    public IReadOnlyList<LoudnessResult> Snapshot()
    {
        EnsureLoaded();
        return _cache.Values.ToList();
    }

    private static readonly string[] FfmpegSearchPaths =
    {
        "/usr/lib/jellyfin-ffmpeg/ffmpeg",
        "/usr/lib/jellyfin/ffmpeg",
        "/opt/jellyfin-ffmpeg/ffmpeg",
        "/usr/bin/ffmpeg",
        "/usr/local/bin/ffmpeg",
    };

    private string? _resolvedFfmpeg;

    private string? ResolveFfmpegPath()
    {
        if (_resolvedFfmpeg is not null) return _resolvedFfmpeg;
        foreach (var candidate in FfmpegSearchPaths)
        {
            if (File.Exists(candidate))
            {
                _logger.LogDebug("[Projectionist] using ffmpeg at {Path}", candidate);
                _resolvedFfmpeg = candidate;
                return candidate;
            }
        }
        // Try PATH-resolved 'ffmpeg' as a final fallback. Process.Start can
        // find it via PATH on Linux but we test by spawning a quick -version.
        try
        {
            using var test = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                ArgumentList = { "-version" },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (test is not null)
            {
                test.WaitForExit(3000);
                if (test.ExitCode == 0)
                {
                    _resolvedFfmpeg = "ffmpeg";
                    return _resolvedFfmpeg;
                }
            }
        }
        catch
        {
            // PATH lookup failed
        }
        return null;
    }

    private static (double mean, double max) ParseVolumeDetect(string stderr)
    {
        double mean = double.NaN, max = double.NaN;
        foreach (var line in stderr.Split('\n'))
        {
            var idx = line.IndexOf("mean_volume:", StringComparison.OrdinalIgnoreCase);
            if (idx > 0 && TryParseDb(line, idx + "mean_volume:".Length, out var m)) mean = m;
            idx = line.IndexOf("max_volume:", StringComparison.OrdinalIgnoreCase);
            if (idx > 0 && TryParseDb(line, idx + "max_volume:".Length, out var x)) max = x;
        }
        return (mean, max);
    }

    private static bool TryParseDb(string line, int from, out double value)
    {
        value = double.NaN;
        var slice = line[from..].Trim();
        var dbIdx = slice.IndexOf("dB", StringComparison.OrdinalIgnoreCase);
        if (dbIdx <= 0) return false;
        var num = slice[..dbIdx].Trim();
        return double.TryParse(num, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private string CacheFilePath() =>
        Path.Combine(_appPaths.PluginConfigurationsPath, "Projectionist.loudness.json");

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_saveLock)
        {
            if (_loaded) return;
            try
            {
                var path = CacheFilePath();
                if (File.Exists(path))
                {
                    using var stream = File.OpenRead(path);
                    var data = JsonSerializer.Deserialize<List<LoudnessResult>>(stream);
                    if (data is not null)
                    {
                        foreach (var r in data)
                        {
                            _cache[r.FileName.ToLowerInvariant()] = r;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Projectionist] could not load loudness cache");
            }
            _loaded = true;
        }
    }

    private void Save()
    {
        lock (_saveLock)
        {
            try
            {
                var path = CacheFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var stream = File.Create(path);
                JsonSerializer.Serialize(stream, _cache.Values.ToList());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Projectionist] could not persist loudness cache");
            }
        }
    }
}
