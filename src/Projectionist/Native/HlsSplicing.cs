using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Projectionist.Native;

/// <summary>
/// The format a preroll has to be converted to so it can sit in front of a
/// particular episode stream. Matching the episode's codecs keeps the
/// master playlist's CODECS attribute truthful, which strict players
/// (AVPlayer) enforce, and avoids asking decoders to switch codec mid-stream.
/// </summary>
public sealed record HlsTarget(string Container, string VideoCodec, string AudioCodec, int Width, int Height, string FrameRate)
{
    /// <summary>Stable text used in the cache key.</summary>
    public string Key => $"{Container}-{VideoCodec}-{AudioCodec}-{Width}x{Height}-{FrameRate}";

    public bool IsFmp4 => Container == "fmp4";
}

/// <summary>One cached, converted preroll.</summary>
public sealed record PrerollHlsEntry(string CacheKey, IReadOnlyList<PrerollHlsSegment> Segments, string? InitFile)
{
    public long DurationTicks => (long)Math.Round(Segments.Sum(s => s.Duration) * TimeSpan.TicksPerSecond);
}

public sealed record PrerollHlsSegment(string File, double Duration);

public static class HlsSplicing
{
    private static readonly Regex SrtTime = new(
        @"(?<h>\d{1,2}):(?<m>\d{2}):(?<s>\d{2})(?<sep>[,.])(?<ms>\d{3})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AssDialogue = new(
        @"^(?<head>(?:Dialogue|Comment):\s*[^,]*,)(?<start>\d+:\d{2}:\d{2}\.\d{2}),(?<end>\d+:\d{2}:\d{2}\.\d{2})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>
    /// Works out what the episode's HLS stream will carry, from the source
    /// streams and the codec lists in the stream URL. Jellyfin copies a
    /// source codec when the client allows it and otherwise encodes to the
    /// first allowed one. Returns null when the preroll can't be matched
    /// safely: HDR sources, or codecs we don't convert to.
    /// </summary>
    public static HlsTarget? ResolveTarget(
        string? sourceVideoCodec,
        string? sourceAudioCodec,
        int width,
        int height,
        float? frameRate,
        bool isHdr,
        string? videoCodecParam,
        string? audioCodecParam,
        string? segmentContainerParam)
    {
        if (isHdr) return null;

        var container = string.Equals(segmentContainerParam, "mp4", StringComparison.OrdinalIgnoreCase) ? "fmp4" : "ts";

        var video = Pick(NormalizeVideo(sourceVideoCodec), videoCodecParam, NormalizeVideo);
        if (video is not ("h264" or "hevc")) return null;

        var audio = Pick(NormalizeAudio(sourceAudioCodec), audioCodecParam, NormalizeAudio);
        if (audio is not ("aac" or "ac3" or "eac3" or "mp3")) return null;

        // Even dimensions keep 4:2:0 encoders happy.
        var w = width > 0 ? width & ~1 : 1920;
        var h = height > 0 ? height & ~1 : 1080;
        var fps = frameRate is > 1 and < 121
            ? Math.Round(frameRate.Value, 3).ToString("0.###", CultureInfo.InvariantCulture)
            : "23.976";
        return new HlsTarget(container, video, audio, w, h, fps);
    }

    private static string? Pick(string? source, string? param, Func<string?, string?> normalize)
    {
        var allowed = (param ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.Equals("copy", StringComparison.OrdinalIgnoreCase) ? "copy" : normalize(c))
            .Where(c => c is not null)
            .ToList();
        if (allowed.Count == 0 || allowed.Contains("copy")) return source;
        if (source is not null && allowed.Contains(source)) return source;
        return allowed[0];
    }

    private static string? NormalizeVideo(string? codec) => codec?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "h264" or "avc" or "avc1" => "h264",
        "hevc" or "h265" or "hvc1" or "hev1" => "hevc",
        var other => other,
    };

    private static string? NormalizeAudio(string? codec) => codec?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "aac" or "mp4a" => "aac",
        "ac3" or "ac-3" => "ac3",
        "eac3" or "ec-3" => "eac3",
        "mp3" => "mp3",
        var other => other,
    };

    /// <summary>
    /// Puts the preroll segments in front of the episode's own segments.
    /// The episode's lines are left as they are, so Jellyfin keeps serving,
    /// seeking and throttling them normally; only the playlist grows.
    /// For fMP4 each block carries its own EXT-X-MAP and the episode's map
    /// is repeated after the discontinuity, since a map applies to every
    /// segment that follows it.
    /// </summary>
    public static string Splice(string playlist, IReadOnlyList<PrerollHlsEntry> prerolls, string segmentUrlPrefix)
    {
        if (prerolls.Count == 0) return playlist;

        var lines = playlist.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var firstSegment = Array.FindIndex(lines, l => l.StartsWith("#EXTINF:", StringComparison.Ordinal));
        if (firstSegment < 0) return playlist;

        var maxPreroll = prerolls.SelectMany(p => p.Segments).Select(s => s.Duration).DefaultIfEmpty(0).Max();
        var prefix = segmentUrlPrefix.TrimEnd('/');
        string? episodeMap = null;
        var sb = new StringBuilder(playlist.Length + 2048);

        for (var i = 0; i < firstSegment; i++)
        {
            var line = lines[i];
            if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                episodeMap = line;
                continue;
            }

            if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
            {
                var current = int.TryParse(line.AsSpan(22), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
                sb.Append("#EXT-X-TARGETDURATION:")
                  .Append(Math.Max(current, (int)Math.Ceiling(maxPreroll)).ToString(CultureInfo.InvariantCulture))
                  .Append('\n');
                continue;
            }

            if (line.Length > 0) sb.Append(line).Append('\n');
        }

        for (var p = 0; p < prerolls.Count; p++)
        {
            var entry = prerolls[p];
            if (p > 0) sb.Append("#EXT-X-DISCONTINUITY\n");
            if (entry.InitFile is not null)
            {
                sb.Append("#EXT-X-MAP:URI=\"").Append(prefix).Append('/').Append(entry.CacheKey).Append('/')
                  .Append(entry.InitFile).Append("\"\n");
            }

            foreach (var seg in entry.Segments)
            {
                sb.Append("#EXTINF:").Append(seg.Duration.ToString("0.000###", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append(prefix).Append('/').Append(entry.CacheKey).Append('/').Append(seg.File).Append('\n');
            }
        }

        sb.Append("#EXT-X-DISCONTINUITY\n");
        if (episodeMap is not null) sb.Append(episodeMap).Append('\n');

        for (var i = firstSegment; i < lines.Length; i++)
        {
            if (lines[i].Length > 0 || i < lines.Length - 1) sb.Append(lines[i]).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Moves every cue later by <paramref name="offsetTicks"/>. Native apps
    /// time external subtitles against the player timeline, which now starts
    /// with the preroll. Handles SRT/WebVTT timestamps and ASS/SSA dialogue
    /// lines; anything else comes back unchanged.
    /// </summary>
    public static string ShiftSubtitles(string text, string format, long offsetTicks)
    {
        if (offsetTicks <= 0 || string.IsNullOrEmpty(text)) return text;
        var fmt = (format ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        var offset = TimeSpan.FromTicks(offsetTicks);

        if (fmt is "ass" or "ssa")
        {
            return AssDialogue.Replace(text, m =>
                m.Groups["head"].Value + ShiftAss(m.Groups["start"].Value, offset) + "," + ShiftAss(m.Groups["end"].Value, offset));
        }

        if (fmt is "srt" or "subrip" or "vtt" or "webvtt")
        {
            // Only timing lines contain "-->"; cue text is left alone even if
            // it happens to contain something that looks like a time.
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("-->", StringComparison.Ordinal)) continue;
                lines[i] = SrtTime.Replace(lines[i], m =>
                {
                    var t = new TimeSpan(0, int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture),
                        int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture),
                        int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture),
                        int.Parse(m.Groups["ms"].Value, CultureInfo.InvariantCulture)) + offset;
                    var sep = m.Groups["sep"].Value;
                    return string.Create(CultureInfo.InvariantCulture,
                        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{sep}{t.Milliseconds:000}");
                });
            }

            return string.Join('\n', lines);
        }

        return text;
    }

    private static string ShiftAss(string value, TimeSpan offset)
    {
        var parts = value.Split(':', '.');
        var t = new TimeSpan(0, int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture),
            int.Parse(parts[2], CultureInfo.InvariantCulture), int.Parse(parts[3], CultureInfo.InvariantCulture) * 10) + offset;
        return string.Create(CultureInfo.InvariantCulture,
            $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}");
    }

    /// <summary>
    /// Takes the preroll out of a playback report: while the preroll plays
    /// the app is at 0 in the episode, afterwards it is <c>offset</c> behind
    /// what it reports. Without this, resume points land late and episodes
    /// are marked watched early. Returns null when nothing needs changing.
    /// </summary>
    public static string? CorrectPlaybackReport(string json, Func<string, long?> prerollTicksForSession)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException) { return null; }
        if (node is not JsonObject obj) return null;

        var sid = obj["PlaySessionId"]?.GetValue<string>();
        if (string.IsNullOrEmpty(sid)) return null;
        var offset = prerollTicksForSession(sid);
        if (offset is null or <= 0) return null;

        var position = obj["PositionTicks"];
        if (position is null) return null;
        long ticks;
        try { ticks = position.GetValue<long>(); }
        catch (FormatException) { return null; }
        catch (InvalidOperationException) { return null; }

        obj["PositionTicks"] = Math.Max(0, ticks - offset.Value);
        return obj.ToJsonString();
    }

    /// <summary>
    /// Moves media segments (intro/credits markers, e.g. from Intro Skipper)
    /// later by the preroll length for an app that is playing a spliced
    /// stream, so "Skip intro" appears at the right moment.
    /// </summary>
    public static string? ShiftMediaSegments(string json, long offsetTicks)
    {
        if (offsetTicks <= 0) return null;
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException) { return null; }
        if (node?["Items"] is not JsonArray items) return null;

        foreach (var seg in items.OfType<JsonObject>())
        {
            foreach (var key in new[] { "StartTicks", "EndTicks" })
            {
                if (seg[key] is JsonValue v && v.TryGetValue<long>(out var t))
                {
                    seg[key] = t + offsetTicks;
                }
            }
        }

        return node.ToJsonString();
    }
}
