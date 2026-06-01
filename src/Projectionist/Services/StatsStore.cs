using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Projectionist.Services;

/// <summary>
/// Persists per-file play counts + per-user plays to a small JSON file in the
/// plugin's data directory. Survives Jellyfin restarts.
/// </summary>
public sealed class StatsStore
{
    private readonly ILogger<StatsStore> _logger;
    private readonly IApplicationPaths _appPaths;
    private readonly object _saveLock = new();
    private readonly ConcurrentDictionary<string, FileStat> _byFile = new();
    private readonly ConcurrentDictionary<Guid, long> _byUser = new();
    private DateTime _lastFlushUtc = DateTime.MinValue;
    private bool _loaded;

    public StatsStore(ILogger<StatsStore> logger, IApplicationPaths appPaths)
    {
        _logger = logger;
        _appPaths = appPaths;
    }

    public sealed class FileStat
    {
        public long PlayCount { get; set; }
        public DateTime LastPlayedUtc { get; set; }
        public long SkipCount { get; set; }
        public double TotalSkipSeconds { get; set; }
    }

    public sealed class StatsSnapshot
    {
        public long TotalPlays { get; set; }
        public List<FileEntry> Files { get; set; } = new();
        public List<UserEntry> Users { get; set; } = new();
    }

    public sealed class FileEntry
    {
        public string FileName { get; set; } = string.Empty;
        public long PlayCount { get; set; }
        public DateTime LastPlayedUtc { get; set; }
        public long SkipCount { get; set; }
        public double AvgSkipSeconds { get; set; }
        public double SkipRate { get; set; }
    }

    public sealed class UserEntry
    {
        public Guid UserId { get; set; }
        public long PlayCount { get; set; }
    }

    public void Record(string prerollPath, Guid userId)
    {
        EnsureLoaded();
        var key = Path.GetFileName(prerollPath ?? string.Empty);
        if (string.IsNullOrEmpty(key)) return;
        var stat = _byFile.GetOrAdd(key, _ => new FileStat());
        lock (stat)
        {
            stat.PlayCount++;
            stat.LastPlayedUtc = DateTime.UtcNow;
        }
        _byUser.AddOrUpdate(userId, 1, (_, n) => n + 1);
        TrySave();
    }

    public void RecordSkip(string prerollFileName, double secondsBeforeSkip)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(prerollFileName)) return;
        var stat = _byFile.GetOrAdd(prerollFileName, _ => new FileStat());
        lock (stat)
        {
            stat.SkipCount++;
            stat.TotalSkipSeconds += Math.Max(0, secondsBeforeSkip);
        }
        TrySave();
    }

    public StatsSnapshot Snapshot()
    {
        EnsureLoaded();
        var files = _byFile
            .Select(kv =>
            {
                var avg = kv.Value.SkipCount > 0 ? kv.Value.TotalSkipSeconds / kv.Value.SkipCount : 0;
                var rate = kv.Value.PlayCount > 0 ? (double)kv.Value.SkipCount / kv.Value.PlayCount : 0;
                return new FileEntry
                {
                    FileName = kv.Key,
                    PlayCount = kv.Value.PlayCount,
                    LastPlayedUtc = kv.Value.LastPlayedUtc,
                    SkipCount = kv.Value.SkipCount,
                    AvgSkipSeconds = Math.Round(avg, 2),
                    SkipRate = Math.Round(rate, 3),
                };
            })
            .OrderByDescending(f => f.PlayCount)
            .ToList();
        var users = _byUser
            .Select(kv => new UserEntry { UserId = kv.Key, PlayCount = kv.Value })
            .OrderByDescending(u => u.PlayCount)
            .ToList();
        return new StatsSnapshot
        {
            TotalPlays = files.Sum(f => f.PlayCount),
            Files = files,
            Users = users,
        };
    }

    public void Reset()
    {
        _byFile.Clear();
        _byUser.Clear();
        TrySave(force: true);
    }

    private string FilePath() =>
        Path.Combine(_appPaths.PluginConfigurationsPath, "Projectionist.stats.json");

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
                    var data = JsonSerializer.Deserialize<PersistShape>(stream);
                    if (data is not null)
                    {
                        if (data.Files is not null)
                        {
                            foreach (var f in data.Files)
                            {
                                if (string.IsNullOrEmpty(f.FileName)) continue;
                                _byFile[f.FileName] = new FileStat
                                {
                                    PlayCount = f.PlayCount,
                                    LastPlayedUtc = f.LastPlayedUtc,
                                    SkipCount = f.SkipCount,
                                    TotalSkipSeconds = f.TotalSkipSeconds,
                                };
                            }
                        }
                        if (data.Users is not null)
                        {
                            foreach (var u in data.Users)
                            {
                                _byUser[u.UserId] = u.PlayCount;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Projectionist] could not load stats");
            }
            _loaded = true;
        }
    }

    private void TrySave(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - _lastFlushUtc) < TimeSpan.FromSeconds(15)) return;
        lock (_saveLock)
        {
            if (!force && (now - _lastFlushUtc) < TimeSpan.FromSeconds(15)) return;
            try
            {
                var path = FilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var data = new PersistShape
                {
                    Files = _byFile.Select(kv => new StoredFile
                    {
                        FileName = kv.Key,
                        PlayCount = kv.Value.PlayCount,
                        LastPlayedUtc = kv.Value.LastPlayedUtc,
                        SkipCount = kv.Value.SkipCount,
                        TotalSkipSeconds = kv.Value.TotalSkipSeconds,
                    }).ToList(),
                    Users = _byUser.Select(kv => new UserEntry { UserId = kv.Key, PlayCount = kv.Value }).ToList(),
                };
                using var stream = File.Create(path);
                JsonSerializer.Serialize(stream, data, new JsonSerializerOptions { WriteIndented = true });
                _lastFlushUtc = now;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Projectionist] could not persist stats");
            }
        }
    }

    private sealed class StoredFile
    {
        public string FileName { get; set; } = string.Empty;
        public long PlayCount { get; set; }
        public DateTime LastPlayedUtc { get; set; }
        public long SkipCount { get; set; }
        public double TotalSkipSeconds { get; set; }
    }

    private sealed class PersistShape
    {
        public List<StoredFile>? Files { get; set; }
        public List<UserEntry>? Users { get; set; }
    }
}
