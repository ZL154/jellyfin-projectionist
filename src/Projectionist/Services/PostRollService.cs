using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Projectionist.Configuration;
using Jellyfin.Plugin.Projectionist.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Projectionist.Services;

public sealed class PostRollService
{
    private readonly ILogger<PostRollService> _logger;

    public PostRollService(ILogger<PostRollService> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<PrerollItem> Discover(PluginConfiguration config)
    {
        var folder = config.PostRollFolderPath;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return Array.Empty<PrerollItem>();
        }

        var exts = (config.AllowedExtensions ?? string.Empty)
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.StartsWith('.') ? p.ToLowerInvariant() : "." + p.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var items = new List<PrerollItem>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (parts.Any(p => p.Length > 0 && p[0] == '.')) continue;
                var ext = Path.GetExtension(path);
                if (string.IsNullOrEmpty(ext) || !exts.Contains(ext.ToLowerInvariant())) continue;
                var info = new FileInfo(path);
                items.Add(new PrerollItem
                {
                    Path = info.FullName,
                    FileName = info.Name,
                    FileSizeBytes = info.Length,
                    LastModifiedUtc = info.LastWriteTimeUtc,
                    DeterministicId = Guid.NewGuid(),
                    Tags = new List<string> { "postroll" },
                    Weight = 1.0,
                    SourceFolder = folder,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Projectionist] post-roll discovery failed at {Folder}", folder);
        }
        return items;
    }

    public IReadOnlyList<PrerollItem> Pick(PluginConfiguration config, int count)
    {
        var pool = Discover(config);
        if (pool.Count == 0 || count <= 0) return Array.Empty<PrerollItem>();
        count = Math.Min(count, pool.Count);
        return pool.OrderBy(_ => Random.Shared.Next()).Take(count).ToList();
    }
}
