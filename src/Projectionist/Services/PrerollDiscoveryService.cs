using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Projectionist.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Projectionist.Services;

/// <summary>
/// Scans the configured preroll folder for video files. Cached briefly to
/// avoid hitting the disk on every playback request.
/// </summary>
public sealed class PrerollDiscoveryService
{
    private readonly ILogger<PrerollDiscoveryService> _logger;
    private readonly object _cacheLock = new();
    private List<PrerollItem> _cache = new();
    private DateTime _cacheExpiresUtc = DateTime.MinValue;
    private string _cachedFolder = string.Empty;
    private string _cachedExtensions = string.Empty;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public PrerollDiscoveryService(ILogger<PrerollDiscoveryService> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<PrerollItem> Discover(string folderPath, string allowedExtensions)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return Array.Empty<PrerollItem>();
        }

        if (!Directory.Exists(folderPath))
        {
            _logger.LogWarning("Projectionist preroll folder does not exist: {Folder}", folderPath);
            return Array.Empty<PrerollItem>();
        }

        lock (_cacheLock)
        {
            var now = DateTime.UtcNow;
            if (now < _cacheExpiresUtc &&
                string.Equals(folderPath, _cachedFolder, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(allowedExtensions, _cachedExtensions, StringComparison.OrdinalIgnoreCase))
            {
                return _cache;
            }

            var exts = ParseExtensions(allowedExtensions);
            var items = new List<PrerollItem>();
            try
            {
                foreach (var path in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(path);
                    if (string.IsNullOrEmpty(ext) || !exts.Contains(ext.ToLowerInvariant()))
                    {
                        continue;
                    }

                    FileInfo info;
                    try
                    {
                        info = new FileInfo(path);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Could not stat preroll candidate {Path}", path);
                        continue;
                    }

                    items.Add(new PrerollItem
                    {
                        Path = info.FullName,
                        FileName = info.Name,
                        FileSizeBytes = info.Length,
                        LastModifiedUtc = info.LastWriteTimeUtc,
                        DeterministicId = DeterministicGuid(info.FullName),
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enumerate preroll folder {Folder}", folderPath);
                return Array.Empty<PrerollItem>();
            }

            _cache = items;
            _cacheExpiresUtc = now.Add(CacheTtl);
            _cachedFolder = folderPath;
            _cachedExtensions = allowedExtensions;
            return _cache;
        }
    }

    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cacheExpiresUtc = DateTime.MinValue;
        }
    }

    private static HashSet<string> ParseExtensions(string raw)
    {
        var parts = (raw ?? string.Empty)
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.StartsWith('.') ? p.ToLowerInvariant() : "." + p.ToLowerInvariant());
        return new HashSet<string>(parts, StringComparer.OrdinalIgnoreCase);
    }

    private static Guid DeterministicGuid(string input)
    {
        Span<byte> hash = stackalloc byte[16];
        using var md5 = MD5.Create();
        md5.TryComputeHash(Encoding.UTF8.GetBytes(input), hash, out _);
        return new Guid(hash);
    }
}
