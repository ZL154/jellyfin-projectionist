using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Projectionist.Configuration;
using Jellyfin.Plugin.Projectionist.Models;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Projectionist.Services;

/// <summary>
/// Picks which preroll(s) to play given the discovered set + config + the
/// feature item being played. Applies all eligibility filters before scoring.
/// </summary>
public sealed class PrerollSelector
{
    private static readonly Random _rng = new();
    private static int _sequentialCursor;
    private static readonly object _cursorLock = new();
    // Per-rotation-group cursor for EqualRotation mode
    private static readonly Dictionary<string, int> _equalCursors = new(StringComparer.Ordinal);
    private readonly CooldownStore? _cooldown;

    public PrerollSelector(CooldownStore? cooldown = null)
    {
        _cooldown = cooldown;
    }

    public IReadOnlyList<PrerollItem> SelectFor(
        IReadOnlyList<PrerollItem> pool,
        BaseItem? feature,
        PluginConfiguration config)
    {
        if (pool.Count == 0 || config.PrerollCount <= 0)
        {
            return Array.Empty<PrerollItem>();
        }

        var rule = MatchLibraryRule(feature, config);
        if (rule is { Disabled: true })
        {
            return Array.Empty<PrerollItem>();
        }

        var nowLocal = DateTime.Now;
        var maturityScore = MaturityRanker.Score(feature?.OfficialRating);
        var featureId = feature?.Id ?? Guid.Empty;

        var candidates = pool.Where(p =>
        {
            // Schedule filter (e.g. holiday-only prerolls)
            if (p.Schedule is not null && !p.Schedule.Matches(nowLocal)) return false;

            // Maturity gate: preroll rating must NOT exceed feature rating
            if (config.MaturityGated)
            {
                var prerollScore = MaturityRanker.Score(p.Rating);
                if (prerollScore > maturityScore) return false;
            }

            // Library rule: tag matching
            if (rule is not null)
            {
                if (rule.RequireTags is { Count: > 0 } &&
                    !rule.RequireTags.Any(t => p.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                {
                    return false;
                }
                if (rule.ExcludeTags is { Count: > 0 } &&
                    rule.ExcludeTags.Any(t => p.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }

            // Per-feature cooldown
            if (_cooldown is not null && featureId != Guid.Empty &&
                _cooldown.IsCooling(featureId, p.DeterministicId, config.CooldownHoursPerItem))
            {
                return false;
            }

            return true;
        }).ToList();

        if (candidates.Count == 0) return Array.Empty<PrerollItem>();

        var count = Math.Min(config.PrerollCount, candidates.Count);
        var picks = config.SelectionMode switch
        {
            SelectionMode.Sequential => PickSequential(candidates, count),
            SelectionMode.Weighted => PickWeighted(candidates, count, weighByAge: false),
            SelectionMode.RecencyBoost => PickWeighted(candidates, count, weighByAge: true),
            SelectionMode.EqualRotation => PickEqualRotation(candidates, count, key: rule?.LibraryName ?? "_"),
            _ => PickRandom(candidates, count),
        };

        // Record cooldown so subsequent plays of this feature avoid these prerolls
        if (_cooldown is not null && featureId != Guid.Empty)
        {
            foreach (var p in picks) _cooldown.Record(featureId, p.DeterministicId);
        }

        return picks;
    }

    /// <summary>Backwards-compatible overload kept for the controller's preview button.</summary>
    public IReadOnlyList<PrerollItem> Select(IReadOnlyList<PrerollItem> pool, int count, SelectionMode mode)
    {
        if (pool.Count == 0 || count <= 0) return Array.Empty<PrerollItem>();
        count = Math.Min(count, pool.Count);
        return mode switch
        {
            SelectionMode.Sequential => PickSequential(pool, count),
            SelectionMode.Weighted => PickWeighted(pool, count, weighByAge: false),
            SelectionMode.RecencyBoost => PickWeighted(pool, count, weighByAge: true),
            SelectionMode.EqualRotation => PickEqualRotation(pool, count, "_preview"),
            _ => PickRandom(pool, count),
        };
    }

    private static LibraryRule? MatchLibraryRule(BaseItem? feature, PluginConfiguration config)
    {
        if (feature is null || config.LibraryRules is null || config.LibraryRules.Count == 0) return null;
        var libName = TryGetLibraryName(feature);
        var typeName = feature.GetType().Name;
        foreach (var r in config.LibraryRules)
        {
            var libMatch = string.IsNullOrEmpty(r.LibraryName) ||
                           string.Equals(r.LibraryName, libName, StringComparison.OrdinalIgnoreCase);
            var typeMatch = string.IsNullOrEmpty(r.ItemType) ||
                            string.Equals(r.ItemType, typeName, StringComparison.OrdinalIgnoreCase);
            if (libMatch && typeMatch) return r;
        }
        return null;
    }

    private static string TryGetLibraryName(BaseItem item)
    {
        // Walk up to the topmost named ancestor
        var node = item;
        while (node is not null)
        {
            if (node is Folder f && !string.IsNullOrEmpty(f.Name) && f.IsRoot is false)
            {
                if (f.GetParent() is null || (f.GetParent() is Folder pf && pf.IsRoot))
                    return f.Name;
            }
            node = node.GetParent();
        }
        return string.Empty;
    }

    // ---------- Picking strategies ----------

    private IReadOnlyList<PrerollItem> PickRandom(IReadOnlyList<PrerollItem> pool, int count)
    {
        if (count >= pool.Count) return pool.OrderBy(_ => _rng.Next()).ToList();
        var indices = new HashSet<int>();
        var result = new List<PrerollItem>(count);
        while (result.Count < count)
        {
            var idx = _rng.Next(pool.Count);
            if (indices.Add(idx)) result.Add(pool[idx]);
        }
        return result;
    }

    private IReadOnlyList<PrerollItem> PickSequential(IReadOnlyList<PrerollItem> pool, int count)
    {
        var result = new List<PrerollItem>(count);
        lock (_cursorLock)
        {
            for (var i = 0; i < count; i++)
            {
                result.Add(pool[_sequentialCursor % pool.Count]);
                _sequentialCursor++;
            }
        }
        return result;
    }

    private IReadOnlyList<PrerollItem> PickWeighted(IReadOnlyList<PrerollItem> pool, int count, bool weighByAge)
    {
        var now = DateTime.UtcNow;
        var weighted = pool.Select(p =>
        {
            var w = Math.Max(0.05, p.Weight);
            if (weighByAge)
            {
                var ageDays = Math.Max(0.5, (now - p.LastModifiedUtc).TotalDays);
                // newer files get a 2x boost in the first week, fading over a month
                var recencyFactor = ageDays < 7 ? 2.0 : Math.Max(1.0, 30.0 / ageDays);
                w *= recencyFactor;
            }
            return (item: p, weight: w * (0.5 + _rng.NextDouble()));
        })
        .OrderByDescending(x => x.weight)
        .Take(count)
        .Select(x => x.item)
        .ToList();
        return weighted;
    }

    private IReadOnlyList<PrerollItem> PickEqualRotation(IReadOnlyList<PrerollItem> pool, int count, string key)
    {
        // Walk all items, keeping a per-key cursor. Ensures even play across the pool
        // even when count > 1 — picks N consecutive from the cursor and advances.
        var sorted = pool.OrderBy(p => p.FileName, StringComparer.OrdinalIgnoreCase).ToList();
        var result = new List<PrerollItem>(count);
        lock (_equalCursors)
        {
            _equalCursors.TryGetValue(key, out var cursor);
            for (var i = 0; i < count; i++)
            {
                result.Add(sorted[cursor % sorted.Count]);
                cursor++;
            }
            _equalCursors[key] = cursor % sorted.Count;
        }
        return result;
    }

    // ---------- Maturity ranker (rough but useful) ----------
    internal static class MaturityRanker
    {
        public static int Score(string? rating)
        {
            if (string.IsNullOrWhiteSpace(rating)) return 100; // unknown = treat as adult
            var r = rating.Trim().ToUpperInvariant();
            // US MPAA
            if (r.Contains("NC-17")) return 100;
            if (r is "X" or "AO") return 100;
            if (r is "R" or "M" or "MA") return 80;
            if (r is "PG-13" or "TV-14") return 60;
            if (r is "PG" or "TV-PG") return 40;
            if (r is "G" or "TV-G" or "TV-Y" or "TV-Y7") return 20;
            // UK BBFC
            if (r is "U" or "UC") return 20;
            if (r is "12" or "12A") return 60;
            if (r is "15") return 80;
            if (r is "18") return 100;
            return 50;
        }
    }
}
