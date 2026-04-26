using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Projectionist.Models;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Projectionist.Services;

/// <summary>
/// Pulls local trailers for the feature item (when present) so we can chain
/// "and now our feature presentation"-style trailers in front of the movie/episode.
/// Local trailers live in the LocalTrailers ICollection on movies/series.
/// </summary>
public sealed class TrailerFetcher
{
    private readonly ILogger<TrailerFetcher> _logger;
    private static readonly Random _rng = new();

    public TrailerFetcher(ILogger<TrailerFetcher> logger)
    {
        _logger = logger;
    }

    public List<BaseItem> GetTrailersFor(BaseItem feature, int count)
    {
        if (feature is null || count <= 0) return new();
        try
        {
            // BaseItem.GetExtras returns ALL extras (trailers, behind-the-scenes, etc.)
            // We filter by ExtraType == Trailer.
            var extras = feature.GetExtras() ?? Array.Empty<BaseItem>();
            var trailers = extras
                .Where(e => e?.ExtraType == MediaBrowser.Model.Entities.ExtraType.Trailer)
                .ToList();
            if (trailers.Count == 0) return new();
            // shuffle + take
            return trailers.OrderBy(_ => _rng.Next()).Take(count).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Projectionist] trailer fetch failed for {Item}", feature.Name);
            return new();
        }
    }
}
