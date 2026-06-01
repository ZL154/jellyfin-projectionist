using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Projectionist.Configuration;
using Jellyfin.Plugin.Projectionist.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Projectionist.Services;

/// <summary>
/// Scans the configured preroll folders for video files. Reads optional sidecar
/// metadata. Cached briefly to avoid hitting disk on every playback.
/// </summary>
public sealed class PrerollDiscoveryService
{
    private readonly ILogger<PrerollDiscoveryService> _logger;
    private readonly object _cacheLock = new();
    private List<PrerollItem> _cache = new();
    private DateTime _cacheExpiresUtc = DateTime.MinValue;
    private string _cacheKey = string.Empty;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public PrerollDiscoveryService(ILogger<PrerollDiscoveryService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Discover all eligible preroll files. Aggregates the legacy single folder
    /// AND any extra <see cref="PrerollFolder"/> entries from config.
    /// </summary>
    public IReadOnlyList<PrerollItem> Discover(PluginConfiguration config)
    {
        var folders = ResolveFolders(config);
        if (folders.Count == 0) return Array.Empty<PrerollItem>();

        var key = string.Join("|", folders.Select(f => $"{f.Path}#{string.Join(',', f.DefaultTags)}")) +
                  "::" + (config.AllowedExtensions ?? string.Empty);

        lock (_cacheLock)
        {
            var now = DateTime.UtcNow;
            if (now < _cacheExpiresUtc && key == _cacheKey) return _cache;

            var exts = ParseExtensions(config.AllowedExtensions ?? string.Empty);
            var items = new List<PrerollItem>();

            foreach (var folder in folders)
            {
                if (string.IsNullOrWhiteSpace(folder.Path) || !Directory.Exists(folder.Path))
                {
                    _logger.LogWarning("[Projectionist] folder missing or invalid: {Folder}", folder.Path);
                    continue;
                }

                // Try to load a folder-level prerolls.json if present
                var folderMap = LoadFolderManifest(folder.Path);

                IEnumerable<string> paths;
                try
                {
                    paths = Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Projectionist] failed to enumerate {Folder}", folder.Path);
                    continue;
                }

                foreach (var path in paths)
                {
                    var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (parts.Any(p => p.Length > 0 && p[0] == '.')) continue;

                    var ext = Path.GetExtension(path);
                    if (string.IsNullOrEmpty(ext) || !exts.Contains(ext.ToLowerInvariant())) continue;

                    FileInfo info;
                    try { info = new FileInfo(path); }
                    catch (Exception ex) { _logger.LogDebug(ex, "stat failed: {Path}", path); continue; }

                    var meta = LoadSidecar(path) ?? folderMap.GetValueOrDefault(info.Name) ?? new PrerollMetadata();

                    var tags = new HashSet<string>(meta.Tags ?? new(), StringComparer.OrdinalIgnoreCase);
                    foreach (var t in folder.DefaultTags ?? new()) tags.Add(t);
                    if (tags.Count == 0) tags.Add("default");

                    items.Add(new PrerollItem
                    {
                        Path = info.FullName,
                        FileName = info.Name,
                        FileSizeBytes = info.Length,
                        LastModifiedUtc = info.LastWriteTimeUtc,
                        DeterministicId = DeterministicGuid(info.FullName),
                        Tags = tags.ToList(),
                        Weight = meta.Weight > 0 ? meta.Weight : 1.0,
                        Rating = meta.Rating,
                        Schedule = meta.Schedule,
                        SourceFolder = folder.Path,
                    });
                }
            }

            _cache = items;
            _cacheKey = key;
            _cacheExpiresUtc = now.Add(CacheTtl);
            return _cache;
        }
    }

    /// <summary>Backwards-compatible overload for callers using only the legacy single folder.</summary>
    public IReadOnlyList<PrerollItem> Discover(string folderPath, string allowedExtensions)
    {
        return Discover(new PluginConfiguration
        {
            PrerollFolderPath = folderPath,
            AllowedExtensions = allowedExtensions,
        });
    }

    public void InvalidateCache()
    {
        lock (_cacheLock) { _cacheExpiresUtc = DateTime.MinValue; }
    }

    /// <summary>Aggregate the legacy single folder + extra Folders config entries.</summary>
    public static List<PrerollFolder> ResolveFolders(PluginConfiguration config)
    {
        var list = new List<PrerollFolder>();
        if (!string.IsNullOrWhiteSpace(config.PrerollFolderPath))
        {
            list.Add(new PrerollFolder
            {
                Path = config.PrerollFolderPath,
                Name = "Default",
                DefaultTags = new List<string> { "default" },
            });
        }
        foreach (var f in config.Folders ?? new())
        {
            if (string.IsNullOrWhiteSpace(f.Path)) continue;
            // Avoid duplicating the legacy folder
            if (list.Any(x => string.Equals(x.Path, f.Path, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(f);
        }
        return list;
    }

    private PrerollMetadata? LoadSidecar(string videoPath)
    {
        var sidecar = videoPath + ".json";
        if (!File.Exists(sidecar)) return null;
        try
        {
            using var stream = File.OpenRead(sidecar);
            return JsonSerializer.Deserialize<PrerollMetadata>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Projectionist] bad sidecar {Path}", sidecar);
            return null;
        }
    }

    private Dictionary<string, PrerollMetadata> LoadFolderManifest(string folderPath)
    {
        var manifest = Path.Combine(folderPath, "prerolls.json");
        if (!File.Exists(manifest)) return new Dictionary<string, PrerollMetadata>();
        try
        {
            using var stream = File.OpenRead(manifest);
            var data = JsonSerializer.Deserialize<Dictionary<string, PrerollMetadata>>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return data ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Projectionist] bad folder manifest {Path}", manifest);
            return new();
        }
    }

    private static HashSet<string> ParseExtensions(string raw)
    {
        var parts = (raw ?? string.Empty)
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.StartsWith('.') ? p.ToLowerInvariant() : "." + p.ToLowerInvariant());
        return new HashSet<string>(parts, StringComparer.OrdinalIgnoreCase);
    }

    private static Guid DeterministicGuid(string input) =>
        new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(input)));
}
