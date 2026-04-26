using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Projectionist.Configuration;
using Jellyfin.Plugin.Projectionist.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Projectionist.Providers;

public sealed class PrerollIntroProvider : IIntroProvider
{
    private readonly ILogger<PrerollIntroProvider> _logger;
    private readonly PrerollDiscoveryService _discovery;
    private readonly PrerollSelector _selector;
    private readonly SessionTracker _sessions;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userData;
    private readonly HiddenLibraryManager _hiddenLibrary;
    private readonly SeriesPrerollFinder _seriesFinder;
    private readonly TrailerFetcher _trailerFetcher;
    private readonly StatsStore _stats;

    public PrerollIntroProvider(
        ILogger<PrerollIntroProvider> logger,
        PrerollDiscoveryService discovery,
        PrerollSelector selector,
        SessionTracker sessions,
        ILibraryManager libraryManager,
        IUserDataManager userData,
        HiddenLibraryManager hiddenLibrary,
        SeriesPrerollFinder seriesFinder,
        TrailerFetcher trailerFetcher,
        StatsStore stats)
    {
        _logger = logger;
        _discovery = discovery;
        _selector = selector;
        _sessions = sessions;
        _libraryManager = libraryManager;
        _userData = userData;
        _hiddenLibrary = hiddenLibrary;
        _seriesFinder = seriesFinder;
        _trailerFetcher = trailerFetcher;
        _stats = stats;
    }

    public string Name => "Projectionist";

    public Task<IEnumerable<IntroInfo>> GetIntros(BaseItem item, User user)
    {
        if (item is null || user is null)
            return Task.FromResult(Enumerable.Empty<IntroInfo>());

        var config = Plugin.Instance?.Configuration;
        if (config is null)
            return Task.FromResult(Enumerable.Empty<IntroInfo>());

        // ---- Content-type gate ----
        if (!IsContentTypeEnabled(item, config))
            return Task.FromResult(Enumerable.Empty<IntroInfo>());

        // ---- Per-user inclusion/exclusion ----
        if (!UserAllowed(user.Id, config))
        {
            _logger.LogDebug("[Projectionist] user {User} excluded by config", user.Username);
            return Task.FromResult(Enumerable.Empty<IntroInfo>());
        }

        // ---- Min feature runtime ----
        if (config.MinFeatureRuntimeSeconds > 0 &&
            item.RunTimeTicks.HasValue &&
            item.RunTimeTicks.Value / TimeSpan.TicksPerSecond < config.MinFeatureRuntimeSeconds)
        {
            return Task.FromResult(Enumerable.Empty<IntroInfo>());
        }

        // ---- Skip on resume ----
        if (config.SkipOnResume)
        {
            try
            {
                var data = _userData.GetUserData(user, item);
                if (data is not null && data.PlaybackPositionTicks > 0)
                {
                    _logger.LogInformation("[Projectionist] resume detected for {Item}, skipping preroll", item.Name);
                    _sessions.RecordPlayback(user.Id);
                    return Task.FromResult(Enumerable.Empty<IntroInfo>());
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "user-data lookup failed"); }
        }

        // ---- Session-mode filter (binge for episodes) ----
        var seriesId = (item as Episode)?.Series?.Id ?? Guid.Empty;
        if (!_sessions.ShouldPlay(user.Id, config.SessionMode, seriesId))
        {
            _logger.LogInformation("[Projectionist] session-mode filter rejected user {User}", user.Username);
            _sessions.RecordPlayback(user.Id);
            if (item is Episode) _sessions.RecordEpisodePlayback(user.Id, seriesId);
            return Task.FromResult(Enumerable.Empty<IntroInfo>());
        }

        var picks = new List<(string path, Guid prerollId)>();

        // ---- Per-series preroll (overrides global) ----
        if (config.EnableSeriesPrerolls && item is Episode)
        {
            var seriesPick = _seriesFinder.FindForEpisode(item, config.SeriesPrerollFileName);
            if (seriesPick is not null)
            {
                picks.Add((seriesPick.Path, seriesPick.DeterministicId));
                _logger.LogInformation("[Projectionist] using per-series preroll for {Series}",
                    (item as Episode)?.Series?.Name);
            }
        }

        // ---- Discovery + selection ----
        if (picks.Count == 0)
        {
            var pool = _discovery.Discover(config);
            _logger.LogInformation("[Projectionist] {Count} candidate prerolls for {Item}", pool.Count, item.Name);
            var selected = _selector.SelectFor(pool, item, config);
            foreach (var s in selected) picks.Add((s.Path, s.DeterministicId));
        }

        // ---- Trailer mode: prepend N trailers ----
        if (config.EnableTrailerMode && config.TrailerCount > 0)
        {
            var trailers = _trailerFetcher.GetTrailersFor(item, config.TrailerCount);
            // Trailers are real BaseItems with their own IDs — push them at front
            var trailerIntros = trailers.Select(t => (t.Path, t.Id)).ToList();
            picks.InsertRange(0, trailerIntros);
        }

        if (picks.Count == 0)
        {
            _sessions.RecordPlayback(user.Id);
            if (item is Episode) _sessions.RecordEpisodePlayback(user.Id, seriesId);
            return Task.FromResult(Enumerable.Empty<IntroInfo>());
        }

        // ---- Build IntroInfo with real library item IDs (via HiddenLibraryManager) ----
        var intros = new List<IntroInfo>(picks.Count);
        foreach (var (path, prerollId) in picks)
        {
            var libraryItem = _hiddenLibrary.FindItem(path);
            if (libraryItem is not null)
            {
                intros.Add(new IntroInfo { Path = libraryItem.Path, ItemId = libraryItem.Id });
            }
            else
            {
                intros.Add(new IntroInfo { Path = path, ItemId = prerollId });
            }
        }

        // ---- Stats + session bookkeeping ----
        if (config.EnableStatsTracking)
        {
            foreach (var (path, _) in picks) _stats.Record(path, user.Id);
        }
        _sessions.RecordPrerollPlayed(user.Id);
        if (item is Episode) _sessions.RecordEpisodePlayback(user.Id, seriesId);

        _logger.LogInformation("[Projectionist] returning {Count} preroll(s) before {ItemName}",
            intros.Count, item.Name);
        return Task.FromResult<IEnumerable<IntroInfo>>(intros);
    }

    private static bool IsContentTypeEnabled(BaseItem item, PluginConfiguration config) => item switch
    {
        Movie => config.EnableForMovies,
        Episode => config.EnableForEpisodes,
        MusicVideo => config.EnableForMusicVideos,
        _ => false,
    };

    private static bool UserAllowed(Guid userId, PluginConfiguration config) => config.UserMode switch
    {
        UserMode.OnlyIncluded => config.UserIds is { Count: > 0 } && config.UserIds.Contains(userId),
        UserMode.AllExceptExcluded => config.UserIds is null || !config.UserIds.Contains(userId),
        _ => true,
    };
}
