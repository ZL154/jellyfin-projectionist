using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Projectionist.Services;

/// <summary>
/// Tracks per-feature opt-outs: "never play a preroll before this item."
/// Persisted to plugin config dir as Projectionist.optouts.json.
///
/// As of v1.2.0, a single entry can cover an entire series — adding a
/// Series GUID opts out every Episode (and Season) under it via the
/// <see cref="IsOptedOut(BaseItem)"/> overload, which walks ancestors.
/// </summary>
public sealed class FeatureOptOutStore
{
    private readonly ILogger<FeatureOptOutStore> _logger;
    private readonly IApplicationPaths _appPaths;
    private readonly ConcurrentDictionary<Guid, byte> _ids = new();
    private readonly object _saveLock = new();
    private bool _loaded;

    public FeatureOptOutStore(ILogger<FeatureOptOutStore> logger, IApplicationPaths appPaths)
    {
        _logger = logger;
        _appPaths = appPaths;
    }

    public bool IsOptedOut(Guid itemId)
    {
        EnsureLoaded();
        return _ids.ContainsKey(itemId);
    }

    /// <summary>
    /// Check if this item is opted-out either directly OR via an ancestor.
    /// For Episodes we walk: Episode -> Season -> Series, so a single
    /// entry for a Series GUID suppresses prerolls on every episode and
    /// season under it. Backward compatible with old per-item entries.
    /// </summary>
    public bool IsOptedOut(BaseItem? item)
    {
        if (item is null) return false;
        EnsureLoaded();
        if (_ids.ContainsKey(item.Id)) return true;
        if (item is Episode ep)
        {
            if (ep.SeasonId != Guid.Empty && _ids.ContainsKey(ep.SeasonId)) return true;
            if (ep.SeriesId != Guid.Empty && _ids.ContainsKey(ep.SeriesId)) return true;
        }
        else if (item is Season season)
        {
            if (season.SeriesId != Guid.Empty && _ids.ContainsKey(season.SeriesId)) return true;
        }
        return false;
    }

    public void Add(Guid itemId)
    {
        EnsureLoaded();
        if (_ids.TryAdd(itemId, 0)) Save();
    }

    public void Remove(Guid itemId)
    {
        EnsureLoaded();
        if (_ids.TryRemove(itemId, out _)) Save();
    }

    public IReadOnlyList<Guid> List()
    {
        EnsureLoaded();
        return _ids.Keys.ToList();
    }

    private string FilePath() =>
        Path.Combine(_appPaths.PluginConfigurationsPath, "Projectionist.optouts.json");

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_saveLock)
        {
            if (_loaded) return;
            try
            {
                var path = FilePath();
                if (File.Exists(path))
                {
                    using var stream = File.OpenRead(path);
                    var ids = JsonSerializer.Deserialize<List<Guid>>(stream) ?? new List<Guid>();
                    foreach (var id in ids) _ids[id] = 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Projectionist] could not load opt-outs");
            }
            _loaded = true;
        }
    }

    private void Save()
    {
        lock (_saveLock)
        {
            try
            {
                var path = FilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var stream = File.Create(path);
                JsonSerializer.Serialize(stream, _ids.Keys.ToList());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Projectionist] could not persist opt-outs");
            }
        }
    }
}
