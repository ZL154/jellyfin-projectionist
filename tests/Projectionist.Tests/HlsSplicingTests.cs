using System;
using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Projectionist.Native;
using Xunit;

namespace Jellyfin.Plugin.Projectionist.Tests;

public class HlsSplicingTests
{
    // Shape of Jellyfin 12's main.m3u8 for a TS remux (trimmed query strings).
    private const string TsEpisode =
        "#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:3\n#EXT-X-MEDIA-SEQUENCE:0\n" +
        "#EXTINF:3.000000, nodesc\nhls1/main/0.ts?PlaySessionId=abc&runtimeTicks=0\n" +
        "#EXTINF:3.000000, nodesc\nhls1/main/1.ts?PlaySessionId=abc&runtimeTicks=30000000\n" +
        "#EXT-X-ENDLIST\n";

    private const string Fmp4Episode =
        "#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:0\n" +
        "#EXT-X-MAP:URI=\"hls1/main/-1.mp4?PlaySessionId=abc\"\n" +
        "#EXTINF:6.000000, nodesc\nhls1/main/0.mp4?PlaySessionId=abc\n#EXT-X-ENDLIST\n";

    private static PrerollHlsEntry Preroll(string key, string? init, params double[] durations)
        => new(key, durations.Select((d, i) => new PrerollHlsSegment(i + (init is null ? ".ts" : ".m4s"), d)).ToList(), init);

    [Fact]
    public void Ts_PrerollGoesBeforeEpisodeWithDiscontinuity()
    {
        var outText = HlsSplicing.Splice(TsEpisode, new[] { Preroll("k1", null, 4.0, 2.5) }, "/Plugins/Projectionist/Hls/");
        var lines = outText.TrimEnd('\n').Split('\n');

        Assert.Equal("#EXTM3U", lines[0]);
        var firstInf = Array.FindIndex(lines, l => l.StartsWith("#EXTINF", StringComparison.Ordinal));
        Assert.Equal("/Plugins/Projectionist/Hls/k1/0.ts", lines[firstInf + 1]);
        var disc = Array.IndexOf(lines, "#EXT-X-DISCONTINUITY");
        Assert.StartsWith("hls1/main/0.ts", lines[disc + 2], StringComparison.Ordinal);
        Assert.Equal("#EXT-X-ENDLIST", lines[^1]);
        // Target duration must cover the longest preroll segment.
        Assert.Contains("#EXT-X-TARGETDURATION:4\n", outText, StringComparison.Ordinal);
        // Episode segment lines are untouched.
        Assert.Contains("hls1/main/1.ts?PlaySessionId=abc&runtimeTicks=30000000\n", outText, StringComparison.Ordinal);
    }

    [Fact]
    public void Fmp4_EachBlockHasItsOwnMapAndEpisodeMapIsRepeated()
    {
        var outText = HlsSplicing.Splice(Fmp4Episode, new[] { Preroll("k2", "init.mp4", 3.0) }, "/p");
        var lines = outText.TrimEnd('\n').Split('\n');

        var maps = lines.Select((l, i) => (l, i)).Where(x => x.l.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, maps.Count);
        Assert.Equal("#EXT-X-MAP:URI=\"/p/k2/init.mp4\"", maps[0].l);
        Assert.Contains("hls1/main/-1.mp4", maps[1].l, StringComparison.Ordinal);
        Assert.Equal("#EXT-X-DISCONTINUITY", lines[maps[1].i - 1]);
        Assert.StartsWith("#EXTINF", lines[maps[1].i + 1], StringComparison.Ordinal);
    }

    [Fact]
    public void TwoPrerolls_AreSeparatedByDiscontinuity()
    {
        var outText = HlsSplicing.Splice(TsEpisode, new[] { Preroll("a", null, 2.0), Preroll("b", null, 2.0) }, "/p");
        Assert.Equal(2, outText.Split('\n').Count(l => l == "#EXT-X-DISCONTINUITY"));
    }

    [Fact]
    public void NoPrerolls_ReturnsPlaylistUnchanged()
        => Assert.Equal(TsEpisode, HlsSplicing.Splice(TsEpisode, Array.Empty<PrerollHlsEntry>(), "/p"));

    [Theory]
    // Source codec allowed -> copied.
    [InlineData("hevc", "ac3", "h264,hevc", "copy", "ts", "ts-hevc-ac3")]
    [InlineData("h264", "aac", "h264", "aac,ac3", "ts", "ts-h264-aac")]
    // Source not allowed -> first allowed.
    [InlineData("hevc", "truehd", "h264", "aac,mp3", "ts", "ts-h264-aac")]
    [InlineData("h264", "aac", "h264,hevc", "aac", "mp4", "fmp4-h264-aac")]
    public void ResolveTarget_FollowsJellyfinCopyRules(string v, string a, string vp, string ap, string container, string expectedPrefix)
    {
        var target = HlsSplicing.ResolveTarget(v, a, 1280, 720, 24f, false, vp, ap, container);
        Assert.NotNull(target);
        Assert.StartsWith(expectedPrefix, target!.Key, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveTarget_SkipsHdrAndUnsupportedCodecs()
    {
        Assert.Null(HlsSplicing.ResolveTarget("hevc", "aac", 3840, 2160, 24f, true, "hevc", "aac", "mp4"));
        Assert.Null(HlsSplicing.ResolveTarget("av1", "aac", 1920, 1080, 24f, false, "av1", "aac", "mp4"));
        Assert.Null(HlsSplicing.ResolveTarget("h264", "truehd", 1920, 1080, 24f, false, "h264", "copy", "ts"));
    }

    [Fact]
    public void ResolveTarget_RoundsOddDimensionsDown()
    {
        var t = HlsSplicing.ResolveTarget("h264", "aac", 1279, 533, 23.976f, false, "h264", "aac", "ts")!;
        Assert.Equal(1278, t.Width);
        Assert.Equal(532, t.Height);
        Assert.Equal("23.976", t.FrameRate);
    }

    [Fact]
    public void Srt_CueTimesMoveButTextDoesNot()
    {
        const string srt = "1\n00:00:01,000 --> 00:00:09,500\nThe meeting is at 00:00:05,000\n\n2\n00:59:58,900 --> 01:00:02,000\nLate\n";
        var shifted = HlsSplicing.ShiftSubtitles(srt, "srt", TimeSpan.FromSeconds(30.25).Ticks);
        Assert.Contains("00:00:31,250 --> 00:00:39,750", shifted, StringComparison.Ordinal);
        Assert.Contains("The meeting is at 00:00:05,000", shifted, StringComparison.Ordinal);
        Assert.Contains("01:00:29,150 --> 01:00:32,250", shifted, StringComparison.Ordinal);
    }

    [Fact]
    public void Vtt_UsesDotSeparator()
    {
        var shifted = HlsSplicing.ShiftSubtitles("WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nHi\n", "vtt", TimeSpan.FromSeconds(10).Ticks);
        Assert.Contains("00:00:11.000 --> 00:00:12.000", shifted, StringComparison.Ordinal);
    }

    [Fact]
    public void Ass_DialogueTimesMove()
    {
        const string ass = "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n" +
                           "Dialogue: 0,0:00:01.50,0:00:04.00,Default,,0,0,0,,Hello\n";
        var shifted = HlsSplicing.ShiftSubtitles(ass, "ass", TimeSpan.FromSeconds(20).Ticks);
        Assert.Contains("Dialogue: 0,0:00:21.50,0:00:24.00,Default", shifted, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaybackReport_SubtractsPrerollAndClampsAtZero()
    {
        long? Lookup(string s) => s == "sess" ? 300_000_000L : null;

        var during = HlsSplicing.CorrectPlaybackReport("{\"PlaySessionId\":\"sess\",\"PositionTicks\":120000000}", Lookup);
        Assert.Equal(0, JsonNode.Parse(during!)!["PositionTicks"]!.GetValue<long>());

        var after = HlsSplicing.CorrectPlaybackReport("{\"PlaySessionId\":\"sess\",\"PositionTicks\":900000000,\"ItemId\":\"x\"}", Lookup);
        var node = JsonNode.Parse(after!)!;
        Assert.Equal(600_000_000, node["PositionTicks"]!.GetValue<long>());
        Assert.Equal("x", node["ItemId"]!.GetValue<string>());

        Assert.Null(HlsSplicing.CorrectPlaybackReport("{\"PlaySessionId\":\"other\",\"PositionTicks\":5}", Lookup));
        Assert.Null(HlsSplicing.CorrectPlaybackReport("not json", Lookup));
    }

    [Fact]
    public void MediaSegments_AreShifted()
    {
        const string json = "{\"Items\":[{\"Type\":\"Intro\",\"StartTicks\":100,\"EndTicks\":200}],\"TotalRecordCount\":1}";
        var node = JsonNode.Parse(HlsSplicing.ShiftMediaSegments(json, 1000)!)!;
        Assert.Equal(1100, node["Items"]![0]!["StartTicks"]!.GetValue<long>());
        Assert.Equal(1200, node["Items"]![0]!["EndTicks"]!.GetValue<long>());
    }
}

public class RemuxProfileTests
{
    [Fact]
    public void OnlyDirectPlayIsCleared_AndInManifestSubsYieldToExternal()
    {
        var profile = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("""
            {
              "DirectPlayProfiles": [{ "Type": "Video", "Container": "mkv" }],
              "TranscodingProfiles": [{ "Protocol": "hls", "Container": "ts", "VideoCodec": "h264,hevc" }],
              "CodecProfiles": [{ "Type": "Video" }],
              "SubtitleProfiles": [
                { "Format": "srt", "Method": "External" },
                { "Format": "srt", "Method": "Hls" },
                { "Format": "pgssub", "Method": "Hls" },
                { "Format": "pgssub", "Method": "Encode" }
              ]
            }
            """)!;

        Jellyfin.Plugin.Projectionist.Native.NativePrerollMiddleware.PrepareProfileForRemux(profile);

        Assert.Empty(profile["DirectPlayProfiles"]!.AsArray());
        Assert.Single(profile["TranscodingProfiles"]!.AsArray());
        Assert.Single(profile["CodecProfiles"]!.AsArray());
        var subs = profile["SubtitleProfiles"]!.AsArray().Select(s => s!["Format"] + "/" + s["Method"]).ToList();
        // srt/Hls dropped (srt can go external and be shifted); pgssub/Hls kept (no external option).
        Assert.Equal(new[] { "srt/External", "pgssub/Hls", "pgssub/Encode" }, subs);
    }
}

public class VideoCopyTests
{
    [Theory]
    [InlineData("hevc", "hevc", true)]
    [InlineData("hevc", "h265", true)]
    [InlineData("h264", "avc", true)]
    [InlineData("h264", "av1", false)]   // AV1 source on an h264,hevc HLS profile = full re-encode
    [InlineData("h264", "hevc", false)]
    [InlineData("h264", null, false)]
    public void IsSameVideoCodec(string target, string? source, bool expected)
        => Assert.Equal(expected, Jellyfin.Plugin.Projectionist.Native.HlsSplicing.IsSameVideoCodec(target, source));
}
