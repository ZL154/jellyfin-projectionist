using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Projectionist.Configuration;
using Jellyfin.Plugin.Projectionist.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Projectionist.Providers;

public sealed class PrerollIntroProvider : IIntroProvider
{
    // Matches Client="..." inside an X-Emby-Authorization / Authorization
    // header value, e.g. MediaBrowser Client="Jellyfin Web", Device="Chrome", ...
    private static readonly Regex ClientNameRegex = new(
        "Client\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled);

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
    private readonly FeatureOptOutStore _optOuts;
    private readonly ComingSoonPicker _comingSoon;
    private readonly IHttpContextAccessor? _httpContext;

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
        StatsStore stats,
        FeatureOptOutStore optOuts,
        ComingSoonPicker comingSoon,
        IHttpContextAccessor? httpContext = null)
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
        _optOuts = optOuts;
        _comingSoon = comingSoon;
        _httpContext = httpContext;
    }

    public string Name => "Projectionist";

    public Task<IEnumerable<IntroInfo>> GetIntros(BaseItem item, User user)
    {
        if (item is null || user is null)
            return Task.FromResult(Enumerable.Empty<IntroInfo>());

        var config = Plugin.Instance?.Configuration;
        if (config is null)
            return Task.FromResult(Enumerable.Empty<IntroInfo>());

        if (_optOuts.IsOptedOut(item))
        {
            _logger.LogDebug("[Projectionist] item {Item} is opted-out (direct or via ancestor), skipping preroll", item.Name);
            return Task.FromResult(Enumerable.Empty<IntroInfo>());
        }

        // ---- Recursion guard ----
        // If the item being played IS one of our discovered prerolls (e.g.
        // because the v1.2.0 web hook prepended it to the play queue, and
        // Jellyfin's native playWithIntros is now calling /Intros on that
        // intro item too), return empty. Otherwise PrerollIntroProvider
        // would chain "intro of intro of intro" indefinitely.
        if (IsKnownPrerollPath(item.Path, config))
        {
            _logger.LogDebug("[Projectionist] item {Item} is itself a preroll, skipping recursion", item.Name);
            return Task.FromResult(Enumerable.Empty<IntroInfo>());
        }

        // ---- Client-type gate for Episodes ----
        // Native TV/mobile clients (Jellyfin Android TV, iOS, Roku, etc.)
        // either ignore the /Intros response for Episodes or, worse, try
        // to play the returned intro items and fail with a brief
        // "Playback Error" toast before falling through to the actual
        // episode. Only the Web client (and Web-based derivatives like
        // Jellyfin Media Player) reliably handle episode intros — partly
        // because we drive them client-side via the playback-hook.
        // Movies still go to every client (universal support).
        var clientName = GetClientNameForLog();
        _logger.LogDebug(
            "[Projectionist] /Intros called: item={Name} type={Type} client={Client}",
            item.Name, item.GetType().Name, clientName ?? "(unknown)");

        if (item is Episode && !IsEpisodeIntroSupportedClient())
        {
            _logger.LogDebug(
                "[Projectionist] /Intros skipped (non-Web client): item={Name} client={Client}",
                item.Name, clientName ?? "(unknown)");
            return Task.FromResult(Enumerable.Empty<IntroInfo>());
        }

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
        // Only treat a non-zero playback position as a "resume" when the
        // item is NOT already marked Played. Jellyfin keeps the last-known
        // position on items even after the user finishes them, so the
        // unrestricted check used to false-trigger on every rewatch of a
        // previously-watched episode. A genuine resume is in-progress
        // (Played=false, position>0); a played item with a stale position
        // is a rewatch and should still get a preroll.
        if (config.SkipOnResume)
        {
            try
            {
                var data = _userData.GetUserData(user, item);
                if (data is not null
                    && data.PlaybackPositionTicks > 0
                    && !data.Played)
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
        var sessionMode = GetSessionModeForItem(item, config);
        if (!_sessions.ShouldPlay(user.Id, sessionMode, seriesId))
        {
            _logger.LogInformation(
                "[Projectionist] session-mode filter {Mode} rejected user {User}",
                sessionMode,
                user.Username);
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

        if (config.EnableComingSoonTrailers && config.ComingSoonTrailerCount > 0)
        {
            var comingSoon = _comingSoon.PickTrailers(user, item, config.ComingSoonTrailerCount);
            var comingSoonIntros = comingSoon.Select(t => (t.Path, t.Id)).ToList();
            picks.InsertRange(0, comingSoonIntros);
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
        if (item is Episode)
        {
            _sessions.RecordEpisodePlayback(user.Id, seriesId);
            _sessions.RecordSeriesPrerollPlayed(user.Id, seriesId);
        }

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

    private static SessionMode GetSessionModeForItem(BaseItem item, PluginConfiguration config)
    {
        if (!config.UseSeparateSessionModes)
        {
            return config.SessionMode;
        }

        return item is Episode ? config.EpisodeSessionMode : config.MovieSessionMode;
    }

    private string? GetClientNameForLog()
    {
        try
        {
            var ctx = _httpContext?.HttpContext;
            if (ctx is null) return null;
            var auth = ctx.Request.Headers["X-Emby-Authorization"].ToString();
            if (string.IsNullOrEmpty(auth)) auth = ctx.Request.Headers["Authorization"].ToString();
            if (string.IsNullOrEmpty(auth)) return null;
            var m = ClientNameRegex.Match(auth);
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    private bool IsEpisodeIntroSupportedClient()
    {
        // Default-allow when there's no HTTP context — happens when other
        // server-side code asks IIntroProvider directly (e.g. for offline
        // pre-generation). Better to err on returning intros than to
        // silently drop them.
        var ctx = _httpContext?.HttpContext;
        if (ctx is null) return true;

        var auth = ctx.Request.Headers["X-Emby-Authorization"].ToString();
        if (string.IsNullOrEmpty(auth)) auth = ctx.Request.Headers["Authorization"].ToString();
        if (string.IsNullOrEmpty(auth)) return true;

        var m = ClientNameRegex.Match(auth);
        if (!m.Success) return true;

        var client = m.Groups[1].Value ?? string.Empty;
        var lower = client.ToLowerInvariant();
        // Whitelist: clients whose play queue we KNOW handles intros
        // correctly. Jellyfin Web for browsers + Jellyfin Media Player
        // (Qt-wrapped jellyfin-web). Everything else is blocked for
        // episode-only intros — movies are still served to all clients.
        if (lower.Contains("web")) return true;
        if (lower.Contains("media player")) return true;
        return false;
    }

    private bool IsKnownPrerollPath(string? itemPath, PluginConfiguration config)
    {
        if (string.IsNullOrEmpty(itemPath)) return false;
        try
        {
            var pool = _discovery.Discover(config);
            foreach (var p in pool)
            {
                if (!string.IsNullOrEmpty(p.Path)
                    && string.Equals(p.Path, itemPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Projectionist] preroll-recursion check failed for {Path}", itemPath);
        }
        return false;
    }
}
