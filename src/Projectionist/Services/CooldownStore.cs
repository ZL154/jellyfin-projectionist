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
    private long _recordCount;

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
        var n = System.Threading.Interlocked.Increment(ref _recordCount);
        if (n % 1024 == 0)
        {
            Prune(TimeSpan.FromDays(30));
        }
    }

    public int Prune(TimeSpan olderThan)
    {
        var cutoff = DateTime.UtcNow - olderThan;
        var removed = 0;
        foreach (var kvp in _last)
        {
            if (kvp.Value < cutoff && _last.TryRemove(kvp.Key, out _)) removed++;
        }
        return removed;
    }
}
