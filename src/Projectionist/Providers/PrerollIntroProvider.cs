using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Entities;
using Jellyfin.Plugin.Projectionist.Configuration;
using Jellyfin.Plugin.Projectionist.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Projectionist.Providers;

/// <summary>
/// Hooks into Jellyfin's official IIntroProvider. Returns preroll files to be
/// queued before the requested feature.
/// </summary>
public sealed class PrerollIntroProvider : IIntroProvider
{
    private readonly ILogger<PrerollIntroProvider> _logger;
    private readonly PrerollDiscoveryService _discovery;
    private readonly PrerollSelector _selector;
    private readonly SessionTracker _sessions;
    private readonly ILibraryManager _libraryManager;

    public PrerollIntroProvider(
        ILogger<PrerollIntroProvider> logger,
        PrerollDiscoveryService discovery,
        PrerollSelector selector,
        SessionTracker sessions,
        ILibraryManager libraryManager)
    {
        _logger = logger;
        _discovery = discovery;
        _selector = selector;
        _sessions = sessions;
        _libraryManager = libraryManager;
    }

    public string Name => "Projectionist";

    public Task<IEnumerable<IntroInfo>> GetIntros(BaseItem item, User user)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return Task.FromResult(Enumerable.Empty<IntroInfo>());
        }

        if (!IsContentTypeEnabled(item, config))
        {
            return Task.FromResult(Enumerable.Empty<IntroInfo>());
        }

        if (config.MinFeatureRuntimeSeconds > 0 &&
            item.RunTimeTicks.HasValue &&
            item.RunTimeTicks.Value / TimeSpan.TicksPerSecond < config.MinFeatureRuntimeSeconds)
        {
            return Task.FromResult(Enumerable.Empty<IntroInfo>());
        }

        if (!_sessions.ShouldPlay(user.Id, config.SessionMode))
        {
            _sessions.RecordPlayback(user.Id);
            return Task.FromResult(Enumerable.Empty<IntroInfo>());
        }

        var pool = _discovery.Discover(config.PrerollFolderPath, config.AllowedExtensions);
        if (pool.Count == 0)
        {
            _sessions.RecordPlayback(user.Id);
            return Task.FromResult(Enumerable.Empty<IntroInfo>());
        }

        var picks = _selector.Select(pool, Math.Max(1, config.PrerollCount), config.SelectionMode);
        if (picks.Count == 0)
        {
            _sessions.RecordPlayback(user.Id);
            return Task.FromResult(Enumerable.Empty<IntroInfo>());
        }

        var intros = new List<IntroInfo>(picks.Count);
        foreach (var p in picks)
        {
            intros.Add(BuildIntroInfo(p.Path, p.DeterministicId));
        }

        _sessions.RecordPrerollPlayed(user.Id);
        _logger.LogInformation("Projectionist queued {Count} preroll(s) before {ItemName}", intros.Count, item.Name);
        return Task.FromResult<IEnumerable<IntroInfo>>(intros);
    }

    private static bool IsContentTypeEnabled(BaseItem item, PluginConfiguration config) => item switch
    {
        Movie => config.EnableForMovies,
        Episode => config.EnableForEpisodes,
        MusicVideo => config.EnableForMusicVideos,
        _ => false,
    };

    /// <summary>
    /// Build the IntroInfo. Tries to resolve the file path to an existing library
    /// BaseItem first (best client compatibility). Falls back to a path-only entry
    /// with the deterministic Guid if nothing matches.
    /// </summary>
    private IntroInfo BuildIntroInfo(string path, Guid deterministicId)
    {
        try
        {
            var libraryItem = _libraryManager.FindByPath(path, isFolder: false);
            if (libraryItem is not null)
            {
                return new IntroInfo
                {
                    Path = libraryItem.Path,
                    ItemId = libraryItem.Id,
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FindByPath failed for {Path}; falling back to path-only intro", path);
        }

        return new IntroInfo
        {
            Path = path,
            ItemId = deterministicId,
        };
    }
}
