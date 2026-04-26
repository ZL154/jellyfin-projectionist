using System;
using System.IO;
using System.Linq;
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
            foreach (var ext in VideoExtensions)
            {
                var candidate = Path.Combine(series.Path, baseFileName + ext);
                if (File.Exists(candidate))
                {
                    var info = new FileInfo(candidate);
                    return new PrerollItem
                    {
                        Path = info.FullName,
                        FileName = info.Name,
                        FileSizeBytes = info.Length,
                        LastModifiedUtc = info.LastWriteTimeUtc,
                        DeterministicId = Guid.NewGuid(),
                        Tags = new() { "series", "per-series" },
                        Weight = 1.0,
                        SourceFolder = series.Path,
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Projectionist] series-preroll lookup failed for {Series}", series.Name);
        }
        return null;
    }
}
