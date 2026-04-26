using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Projectionist.Services;

/// <summary>
/// Tracks per-user playback state to support FirstOfSession and OncePerDay modes.
/// </summary>
public sealed class SessionTracker
{
    /// <summary>Idle window after which a new "session" begins for a user.</summary>
    private static readonly TimeSpan SessionGap = TimeSpan.FromMinutes(20);

    private readonly ConcurrentDictionary<Guid, DateTime> _lastPlaybackUtc = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _lastPrerollUtc = new();

    /// <summary>
    /// Returns true if a preroll should be played for this user given the current
    /// session-mode rules.
    /// </summary>
    public bool ShouldPlay(Guid userId, Configuration.SessionMode mode)
    {
        var now = DateTime.UtcNow;
        var hadRecentPlayback = _lastPlaybackUtc.TryGetValue(userId, out var last) && (now - last) < SessionGap;
        var allow = mode switch
        {
            Configuration.SessionMode.FirstOfSession => !hadRecentPlayback,
            Configuration.SessionMode.OncePerDay =>
                !_lastPrerollUtc.TryGetValue(userId, out var lastPre) ||
                (now - lastPre) >= TimeSpan.FromHours(20),
            _ => true,
        };
        return allow;
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
