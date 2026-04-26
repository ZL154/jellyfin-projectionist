using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Projectionist.Configuration;
using Jellyfin.Plugin.Projectionist.Models;

namespace Jellyfin.Plugin.Projectionist.Services;

/// <summary>
/// Picks which preroll(s) to play given the discovered set + config.
/// </summary>
public sealed class PrerollSelector
{
    private static readonly Random _rng = new();
    private static int _sequentialCursor;
    private static readonly object _cursorLock = new();

    /// <summary>
    /// Select up to <paramref name="count"/> prerolls from the candidate pool
    /// according to the configured selection mode. Will not pick the same item
    /// twice within a single call.
    /// </summary>
    public IReadOnlyList<PrerollItem> Select(
        IReadOnlyList<PrerollItem> pool,
        int count,
        SelectionMode mode)
    {
        if (pool.Count == 0 || count <= 0)
        {
            return Array.Empty<PrerollItem>();
        }

        count = Math.Min(count, pool.Count);

        return mode switch
        {
            SelectionMode.Sequential => PickSequential(pool, count),
            SelectionMode.Weighted => PickWeighted(pool, count),
            _ => PickRandom(pool, count),
        };
    }

    private IReadOnlyList<PrerollItem> PickRandom(IReadOnlyList<PrerollItem> pool, int count)
    {
        if (count == pool.Count)
        {
            return pool.OrderBy(_ => _rng.Next()).ToList();
        }

        var indices = new HashSet<int>();
        var result = new List<PrerollItem>(count);
        while (result.Count < count)
        {
            var idx = _rng.Next(pool.Count);
            if (indices.Add(idx))
            {
                result.Add(pool[idx]);
            }
        }
        return result;
    }

    private IReadOnlyList<PrerollItem> PickSequential(IReadOnlyList<PrerollItem> pool, int count)
    {
        var result = new List<PrerollItem>(count);
        lock (_cursorLock)
        {
            for (int i = 0; i < count; i++)
            {
                result.Add(pool[_sequentialCursor % pool.Count]);
                _sequentialCursor++;
            }
        }
        return result;
    }

    /// <summary>
    /// Weighted picks bias toward older / less-recently-modified files so newly-added
    /// prerolls don't dominate the rotation. Lightly randomized.
    /// </summary>
    private IReadOnlyList<PrerollItem> PickWeighted(IReadOnlyList<PrerollItem> pool, int count)
    {
        var now = DateTime.UtcNow;
        var weighted = pool
            .Select(p =>
            {
                var ageDays = Math.Max(0.5, (now - p.LastModifiedUtc).TotalDays);
                var weight = Math.Sqrt(ageDays) + _rng.NextDouble() * 2;
                return (item: p, weight);
            })
            .OrderByDescending(x => x.weight)
            .Take(count)
            .Select(x => x.item)
            .ToList();
        return weighted;
    }
}
