using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Projectionist.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Projectionist.Services;

/// <summary>
/// For an episode, look in the series folder for a per-series preroll
/// (e.g. theme-preroll.mp4). Wins over the global folder selection.
/// </summary>
public sealed class SeriesPrerollFinder
{
    private static readonly string[] VideoExtensions =
        { ".mp4", ".mkv", ".mov", ".webm", ".avi", ".m4v" };

    // Jellyfin scans every loose video in a series folder as an episode, so a
    // preroll left there shows up in the season list and "Up Next" (#8).
    // Inside one of these folders it's a series extra instead: still a
    // playable library item, but not an episode.
    private static readonly string[] ExtrasFolderNames = { "extras", "other" };

    private readonly ILogger<SeriesPrerollFinder> _logger;

    public SeriesPrerollFinder(ILogger<SeriesPrerollFinder> logger)
    {
        _logger = logger;
    }

    public PrerollItem? FindForEpisode(BaseItem item, string baseFileName)
    {
        if (item is not Episode episode) return null;
        var series = episode.Series;
        if (series is null || string.IsNullOrEmpty(series.Path)) return null;

        try
        {
            var path = FindInSeriesFolder(series.Path, baseFileName);
            if (path is null) return null;

            var info = new FileInfo(path);
            return new PrerollItem
            {
                Path = info.FullName,
                FileName = info.Name,
                FileSizeBytes = info.Length,
                LastModifiedUtc = info.LastWriteTimeUtc,
                DeterministicId = DeterministicGuid(info.FullName),
                Tags = new() { "series", "per-series" },
                Weight = 1.0,
                SourceFolder = series.Path,
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Projectionist] series-preroll lookup failed for {Series}", series.Name);
        }
        return null;
    }

    /// <summary>
    /// The per-series preroll under <paramref name="seriesPath"/>: an extras
    /// or other subfolder first, then the series folder itself (where older
    /// versions told people to put it).
    /// </summary>
    internal static string? FindInSeriesFolder(string seriesPath, string baseFileName)
    {
        if (string.IsNullOrWhiteSpace(baseFileName) || !Directory.Exists(seriesPath)) return null;

        var folders = Directory.EnumerateDirectories(seriesPath)
            .Where(d => ExtrasFolderNames.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            .OrderBy(d => Array.FindIndex(ExtrasFolderNames, n => string.Equals(n, Path.GetFileName(d), StringComparison.OrdinalIgnoreCase)))
            .Append(seriesPath);

        foreach (var folder in folders)
        {
            foreach (var ext in VideoExtensions)
            {
                var candidate = Path.Combine(folder, baseFileName + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// True when <paramref name="path"/> is named like a per-series preroll,
    /// e.g. theme-preroll.mkv. A preroll in the series root gets scanned as an
    /// episode, and must not get a preroll of its own when played.
    /// </summary>
    internal static bool IsSeriesPrerollFile(string? path, string baseFileName)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrWhiteSpace(baseFileName)) return false;
        return string.Equals(Path.GetFileNameWithoutExtension(path), baseFileName, StringComparison.OrdinalIgnoreCase)
            && VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    private static Guid DeterministicGuid(string input) =>
        new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(input)));
}
