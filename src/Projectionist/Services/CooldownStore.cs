using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Projectionist.Services;

/// <summary>
/// In-memory cooldown tracker. For each (feature item, preroll item) pair we
/// remember the last UTC timestamp it played. The selector consults this to
/// avoid replaying the same preroll for the same feature within the configured
/// cooldown window.
/// </summary>
public sealed class CooldownStore
{
    private readonly ConcurrentDictionary<(Guid FeatureId, Guid PrerollId), DateTime> _last = new();

    public bool IsCooling(Guid featureId, Guid prerollId, int hours)
    {
        if (hours <= 0) return false;
        if (_last.TryGetValue((featureId, prerollId), out var when))
        {
            return (DateTime.UtcNow - when) < TimeSpan.FromHours(hours);
        }
        return false;
    }

    public void Record(Guid featureId, Guid prerollId)
    {
        _last[(featureId, prerollId)] = DateTime.UtcNow;
    }

    /// <summary>
    /// Returns true if ANY preroll has been played for this feature recently.
    /// Used as a fast pre-check before scoring all candidates.
    /// </summary>
    public bool FeatureHasRecent(Guid featureId, int hours)
    {
        if (hours <= 0) return false;
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(hours);
        foreach (var kvp in _last)
        {
            if (kvp.Key.FeatureId == featureId && kvp.Value > cutoff)
                return true;
        }
        return false;
    }
}
