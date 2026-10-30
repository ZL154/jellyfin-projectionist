using System;
using System.IO;
using Jellyfin.Plugin.Projectionist.Services;
using Xunit;

namespace Jellyfin.Plugin.Projectionist.Tests;

public sealed class SeriesPrerollFinderTests : IDisposable
{
    private readonly string _series = Path.Combine(Path.GetTempPath(), "pj-series-" + Guid.NewGuid().ToString("N"));

    public SeriesPrerollFinderTests() => Directory.CreateDirectory(_series);

    public void Dispose() => Directory.Delete(_series, recursive: true);

    private string Touch(params string[] parts)
    {
        var path = Path.Combine(_series, Path.Combine(parts));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 0 });
        return path;
    }

    [Fact]
    public void FindsPrerollInSeriesRoot()
    {
        var path = Touch("theme-preroll.mkv");
        Assert.Equal(path, SeriesPrerollFinder.FindInSeriesFolder(_series, "theme-preroll"));
    }

    [Fact]
    public void FindsPrerollInExtrasFolder()
    {
        var path = Touch("extras", "theme-preroll.mp4");
        Assert.Equal(path, SeriesPrerollFinder.FindInSeriesFolder(_series, "theme-preroll"));
    }

    [Fact]
    public void ExtrasFolderMatchIsCaseInsensitive()
    {
        var path = Touch("Other", "theme-preroll.mp4");
        Assert.Equal(path, SeriesPrerollFinder.FindInSeriesFolder(_series, "theme-preroll"));
    }

    [Fact]
    public void ExtrasFolderWinsOverSeriesRoot()
    {
        Touch("theme-preroll.mkv");
        var extras = Touch("extras", "theme-preroll.mkv");
        Assert.Equal(extras, SeriesPrerollFinder.FindInSeriesFolder(_series, "theme-preroll"));
    }

    [Fact]
    public void IgnoresSeasonFoldersAndOtherNames()
    {
        Touch("Season 1", "theme-preroll.mkv");
        Touch("extras", "something-else.mkv");
        Touch("theme-preroll.txt");
        Assert.Null(SeriesPrerollFinder.FindInSeriesFolder(_series, "theme-preroll"));
    }

    [Fact]
    public void MissingFolderOrEmptyNameFindsNothing()
    {
        Touch("theme-preroll.mkv");
        Assert.Null(SeriesPrerollFinder.FindInSeriesFolder(_series, " "));
        Assert.Null(SeriesPrerollFinder.FindInSeriesFolder(Path.Combine(_series, "nope"), "theme-preroll"));
    }

    [Theory]
    [InlineData("/tv/Supernatural/preroll.mkv", "preroll", true)]
    [InlineData("/tv/Supernatural/PREROLL.MP4", "preroll", true)]
    [InlineData("/tv/Supernatural/Season 1/S01E01.mkv", "preroll", false)]
    [InlineData("/tv/Supernatural/preroll.nfo", "preroll", false)]
    [InlineData(null, "preroll", false)]
    [InlineData("/tv/Supernatural/preroll.mkv", "", false)]
    public void RecognisesSeriesPrerollFiles(string? path, string name, bool expected)
    {
        Assert.Equal(expected, SeriesPrerollFinder.IsSeriesPrerollFile(path, name));
    }
}
