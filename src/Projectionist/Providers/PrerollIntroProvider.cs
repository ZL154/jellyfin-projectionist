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
        => GetIntrosCore(item, user, forNativeHls: false);

    /// <summary>
    /// Preroll selection for a native app's episode, which gets its prerolls
    /// spliced into the HLS stream instead of through /Intros. Same rules,
    /// stats and cooldowns as GetIntros, minus the web-client gate.
    /// </summary>
    internal Task<IEnumerable<IntroInfo>> GetIntrosForNativeHls(BaseItem item, User user)
        => GetIntrosCore(item, user, forNativeHls: true);

    private Task<IEnumerable<IntroInfo>> GetIntrosCore(BaseItem item, User user, bool forNativeHls)
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
        // The hidden library also holds post-rolls, which must not get
        // prerolls of their own either.
        if (IsKnownPrerollPath(item.Path, config)
            || _hiddenLibrary.IsManagedItem(item)
            || (item is Episode && config.EnableSeriesPrerolls
                && SeriesPrerollFinder.IsSeriesPrerollFile(item.Path, config.SeriesPrerollFileName)))
        {
            _logger.LogDebug("[Projectionist] item {Item} is itself a preroll/post-roll, skipping recursion", item.Name);
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

        if (item is Episode && !forNativeHls && !IsEpisodeIntroSupportedClient())
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
                // The file sits in the TV library, not our hidden one, so
                // Jellyfin only plays it under the id its own scan gave it.
                // An id made up from the path is dropped by Jellyfin with
                // "Unable to locate item" and nothing plays (#8).
                if (_libraryManager.FindByPath(seriesPick.Path, false) is Video seriesItem)
                {
                    picks.Add((seriesItem.Path, seriesItem.Id));
                    _logger.LogInformation("[Projectionist] using per-series preroll for {Series}",
                        (item as Episode)?.Series?.Name);
                }
                else
                {
                    _logger.LogWarning(
                        "[Projectionist] per-series preroll {Path} is not in the library yet (scan the library it lives in); using the global prerolls instead",
                        seriesPick.Path);
                }
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

        // ---- Build IntroInfo with real library item IDs (via HiddenLibraryManager) ----
        // Jellyfin silently drops an intro whose id it can't find, so a clip
        // that isn't a library item yet is left out here rather than counted
        // as played.
        var intros = new List<IntroInfo>(picks.Count);
        var playedPaths = new List<string>(picks.Count);
        foreach (var (path, prerollId) in picks)
        {
            var libraryItem = _hiddenLibrary.FindItem(path)
                ?? (prerollId == Guid.Empty ? null : _libraryManager.GetItemById(prerollId));
            if (libraryItem is Video)
            {
                intros.Add(new IntroInfo { Path = libraryItem.Path, ItemId = libraryItem.Id });
                playedPaths.Add(path);
            }
            else
            {
                _logger.LogWarning(
                    "[Projectionist] preroll {Path} is not a library item yet (scan the {Library} library); skipping it",
                    path, HiddenLibraryManager.LibraryName);
            }
        }

        if (intros.Count == 0)
        {
            _sessions.RecordPlayback(user.Id);
            if (item is Episode) _sessions.RecordEpisodePlayback(user.Id, seriesId);
            return Task.FromResult(Enumerable.Empty<IntroInfo>());
        }

        // ---- Stats + session bookkeeping ----
        if (config.EnableStatsTracking)
        {
            foreach (var path in playedPaths) _stats.Record(path, user.Id);
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

    internal static bool IsContentTypeEnabled(BaseItem item, PluginConfiguration config) => item switch
    {
        Movie => config.EnableForMovies,
        Episode => config.EnableForEpisodes,
        MusicVideo => config.EnableForMusicVideos,
        _ => false,
    };

    internal static bool UserAllowed(Guid userId, PluginConfiguration config) => config.UserMode switch
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

        return IsWebClient(m.Groups[1].Value);
    }

    /// <summary>
    /// Clients that run jellyfin-web and so get the playback hook: browsers
    /// and Jellyfin Media Player (Qt-wrapped jellyfin-web). Everything else
    /// is a native app.
    /// </summary>
    internal static bool IsWebClient(string? client)
    {
        var lower = (client ?? string.Empty).ToLowerInvariant();
        return lower.Contains("web", StringComparison.Ordinal)
            || lower.Contains("media player", StringComparison.Ordinal);
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
