using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Projectionist.Services;

public sealed class ComingSoonPicker
{
    private readonly ILibraryManager _library;
    private readonly IUserDataManager _userData;
    private readonly ILogger<ComingSoonPicker> _logger;

    private readonly object _cacheLock = new();
    private List<Movie> _unwatchedCache = new();
    private DateTime _cacheExpiresUtc = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    public ComingSoonPicker(
        ILibraryManager library,
        IUserDataManager userData,
        ILogger<ComingSoonPicker> logger)
    {
        _library = library;
        _userData = userData;
        _logger = logger;
    }

    public IReadOnlyList<BaseItem> PickTrailers(User user, BaseItem? currentFeature, int count)
    {
        if (user is null || count <= 0) return Array.Empty<BaseItem>();
        try
        {
            var unwatched = GetUnwatchedMovies(user, currentFeature?.Id ?? Guid.Empty);
            if (unwatched.Count == 0) return Array.Empty<BaseItem>();

            var trailers = new List<BaseItem>();
            var seen = new HashSet<Guid>();
            var attempts = 0;
            while (trailers.Count < count && attempts < count * 5)
            {
                attempts++;
                var pick = unwatched[Random.Shared.Next(unwatched.Count)];
                if (!seen.Add(pick.Id)) continue;
                var extras = pick.GetExtras() ?? Array.Empty<BaseItem>();
                var trailer = extras.FirstOrDefault(e => e?.ExtraType == ExtraType.Trailer);
                if (trailer is null) continue;
                trailers.Add(trailer);
            }
            return trailers;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Projectionist] coming-soon trailer pick failed");
            return Array.Empty<BaseItem>();
        }
    }

    private List<Movie> GetUnwatchedMovies(User user, Guid excludeId)
    {
        lock (_cacheLock)
        {
            var now = DateTime.UtcNow;
            if (now < _cacheExpiresUtc && _unwatchedCache.Count > 0)
            {
                return _unwatchedCache
                    .Where(m => m.Id != excludeId &&
                                _userData.GetUserData(user, m) is { Played: false })
                    .ToList();
            }
        }

        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie },
            IsPlayed = false,
            Recursive = true,
            Limit = 500,
        };
        var fresh = _library.GetItemList(query).OfType<Movie>().ToList();

        lock (_cacheLock)
        {
            _unwatchedCache = fresh;
            _cacheExpiresUtc = DateTime.UtcNow.Add(CacheTtl);
        }
        return fresh.Where(m => m.Id != excludeId).ToList();
    }

    public void InvalidateCache()
    {
        lock (_cacheLock) { _cacheExpiresUtc = DateTime.MinValue; }
    }
}
