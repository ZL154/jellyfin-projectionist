using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Projectionist.Services;

/// <summary>
/// Tracks per-user playback state to support FirstOfSession, OncePerDay, and
/// FirstOfBinge modes. A "binge" is consecutive episodes of the same series.
/// </summary>
public sealed class SessionTracker
{
    /// <summary>Idle window after which a new "session" begins for a user.</summary>
    private static readonly TimeSpan SessionGap = TimeSpan.FromMinutes(20);

    private readonly ConcurrentDictionary<Guid, DateTime> _lastPlaybackUtc = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _lastPrerollUtc = new();
    /// <summary>Per-user, per-series last episode-played timestamp. Used by FirstOfBinge.</summary>
    private readonly ConcurrentDictionary<(Guid UserId, Guid SeriesId), DateTime> _lastEpisodeUtc = new();

    /// <summary>
    /// Returns true if a preroll should be played for this user given the current
    /// session-mode rules. seriesId is non-empty only for episodes — used for
    /// FirstOfBinge mode (don't replay during episode 2+ of same series).
    /// </summary>
    public bool ShouldPlay(Guid userId, Configuration.SessionMode mode, Guid seriesId = default)
    {
        var now = DateTime.UtcNow;
        var hadRecentPlayback = _lastPlaybackUtc.TryGetValue(userId, out var last) && (now - last) < SessionGap;
        return mode switch
        {
            Configuration.SessionMode.FirstOfSession => !hadRecentPlayback,
            Configuration.SessionMode.OncePerDay =>
                !_lastPrerollUtc.TryGetValue(userId, out var lastPre) ||
                (now - lastPre) >= TimeSpan.FromHours(20),
            Configuration.SessionMode.FirstOfBinge =>
                seriesId == default ||
                !_lastEpisodeUtc.TryGetValue((userId, seriesId), out var lastEp) ||
                (now - lastEp) >= SessionGap,
            _ => true,
        };
    }

    public void RecordEpisodePlayback(Guid userId, Guid seriesId)
    {
        if (seriesId == default) return;
        _lastEpisodeUtc[(userId, seriesId)] = DateTime.UtcNow;
    }

    /// <summary>
    /// Mark that we played a preroll for this user. Also bumps last-playback.
    /// Call from the IIntroProvider when prerolls are returned.
    /// </summary>
    public void RecordPrerollPlayed(Guid userId)
    {
        var now = DateTime.UtcNow;
        _lastPlaybackUtc[userId] = now;
        _lastPrerollUtc[userId] = now;
    }

    /// <summary>
    /// Update the last-playback timestamp for any feature playback (whether or not
    /// a preroll was returned).
    /// </summary>
    public void RecordPlayback(Guid userId)
    {
        _lastPlaybackUtc[userId] = DateTime.UtcNow;
    }
}
