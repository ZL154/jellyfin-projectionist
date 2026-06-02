using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Projectionist.Services;

/// <summary>
/// v1.2.0 Milestone B — owns the FFmpeg subprocess that concatenates a
/// preroll and a feature (episode) into a single MP4 stream piped over
/// HTTP. Used by <c>StreamConcatStartupFilter</c> to deliver "one video"
/// to any Jellyfin client, with the preroll plays-first behavior baked
/// into the bytes themselves — no client-side cooperation needed.
///
/// MILESTONE 2: forces full re-encode for maximum compatibility (any
/// container / codec / resolution combo works). Slow on the server but
/// always produces a playable stream. Milestone 7 will add a direct-copy
/// fast path when codecs already match.
/// </summary>
public sealed class StreamConcatService
{
    private readonly ILogger<StreamConcatService> _logger;
    private readonly IApplicationPaths _appPaths;

    private static readonly string[] FfmpegSearchPaths =
    {
        "/usr/lib/jellyfin-ffmpeg/ffmpeg",
        "/usr/lib/jellyfin/ffmpeg",
        "/opt/jellyfin-ffmpeg/ffmpeg",
        "/usr/bin/ffmpeg",
        "/usr/local/bin/ffmpeg",
    };

    private string? _resolvedFfmpeg;

    public StreamConcatService(ILogger<StreamConcatService> logger, IApplicationPaths appPaths)
    {
        _logger = logger;
        _appPaths = appPaths;
    }

    public string? FfmpegPath => _resolvedFfmpeg ??= ResolveFfmpegPath();

    /// <summary>
    /// Spawn FFmpeg with a concat-filter pipeline that emits a fragmented
    /// MP4 stream of preroll(s) + episode, written to <paramref name="output"/>.
    /// Returns when FFmpeg exits or the cancellation token fires. Throws if
    /// FFmpeg can't be found.
    /// </summary>
    public async Task ConcatToStreamAsync(
        IReadOnlyList<string> prerollPaths,
        string episodePath,
        Stream output,
        CancellationToken cancellationToken)
    {
        var ffmpeg = FfmpegPath;
        if (ffmpeg is null)
        {
            throw new InvalidOperationException("ffmpeg binary not found");
        }

        if (prerollPaths is null || prerollPaths.Count == 0)
        {
            throw new ArgumentException("At least one preroll path required", nameof(prerollPaths));
        }
        if (string.IsNullOrWhiteSpace(episodePath) || !File.Exists(episodePath))
        {
            throw new ArgumentException("Episode file not found: " + episodePath, nameof(episodePath));
        }

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-nostats");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("warning");

        foreach (var p in prerollPaths)
        {
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(p);
        }
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(episodePath);

        var filterGraph = BuildConcatFilterGraph(prerollPaths.Count + 1);
        psi.ArgumentList.Add("-filter_complex");
        psi.ArgumentList.Add(filterGraph);
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("[v]");
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("[a]");

        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add("libx264");
        psi.ArgumentList.Add("-preset");
        psi.ArgumentList.Add("veryfast");
        psi.ArgumentList.Add("-crf");
        psi.ArgumentList.Add("22");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("yuv420p");

        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("aac");
        psi.ArgumentList.Add("-b:a");
        psi.ArgumentList.Add("192k");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("2");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add("48000");

        psi.ArgumentList.Add("-movflags");
        psi.ArgumentList.Add("+frag_keyframe+empty_moov+default_base_moof");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("mp4");
        psi.ArgumentList.Add("pipe:1");

        _logger.LogInformation(
            "[Projectionist] Starting concat: {NPreroll} preroll(s) + {Episode}",
            prerollPaths.Count, Path.GetFileName(episodePath));

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            throw new InvalidOperationException("Failed to start ffmpeg");
        }

        var stderrTask = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    if (line.Length > 0)
                    {
                        _logger.LogDebug("[Projectionist][ffmpeg] {Line}", line);
                    }
                }
            }
            catch
            {
                // process likely terminated
            }
        }, CancellationToken.None);

        try
        {
            await proc.StandardOutput.BaseStream
                .CopyToAsync(output, 81920, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[Projectionist] concat cancelled (client disconnected)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Projectionist] concat stream copy failed");
        }
        finally
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Projectionist] error killing ffmpeg");
            }
            try { await stderrTask.ConfigureAwait(false); } catch { }
        }
    }

    private static string BuildConcatFilterGraph(int inputCount)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < inputCount; i++)
        {
            sb.Append('[').Append(i).Append(":v]")
              .Append("scale=1920:1080:force_original_aspect_ratio=decrease,")
              .Append("pad=1920:1080:(ow-iw)/2:(oh-ih)/2:color=black,")
              .Append("setsar=1,fps=24")
              .Append("[v").Append(i).Append("];");
        }
        for (var i = 0; i < inputCount; i++)
        {
            sb.Append("[v").Append(i).Append(']').Append('[').Append(i).Append(":a]");
        }
        sb.Append("concat=n=").Append(inputCount).Append(":v=1:a=1[v][a]");
        return sb.ToString();
    }

    private string? ResolveFfmpegPath()
    {
        foreach (var c in FfmpegSearchPaths)
        {
            if (File.Exists(c)) return c;
        }
        try
        {
            using var test = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                ArgumentList = { "-version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (test is not null)
            {
                test.WaitForExit(3000);
                if (test.ExitCode == 0) return "ffmpeg";
            }
        }
        catch
        {
        }
        return null;
    }
}
