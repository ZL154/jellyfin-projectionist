using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Projectionist.Configuration;
using Jellyfin.Plugin.Projectionist.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Projectionist.Api;

/// <summary>
/// REST endpoints powering the custom admin UI.
/// All endpoints live under /Plugins/Projectionist/.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Plugins/Projectionist")]
[Produces("application/json")]
public sealed class ProjectionistController : ControllerBase
{
    private readonly ILogger<ProjectionistController> _logger;
    private readonly PrerollDiscoveryService _discovery;
    private readonly PrerollSelector _selector;
    private readonly IServerConfigurationManager _serverConfig;

    public ProjectionistController(
        ILogger<ProjectionistController> logger,
        PrerollDiscoveryService discovery,
        PrerollSelector selector,
        IServerConfigurationManager serverConfig)
    {
        _logger = logger;
        _discovery = discovery;
        _selector = selector;
        _serverConfig = serverConfig;
    }

    /// <summary>Return the live plugin configuration.</summary>
    [HttpGet("Config")]
    public ActionResult<PluginConfiguration> GetConfig()
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        return Ok(cfg);
    }

    /// <summary>Persist the supplied configuration.</summary>
    [HttpPost("Config")]
    public ActionResult SaveConfig([FromBody] PluginConfiguration cfg)
    {
        if (cfg is null)
        {
            return BadRequest("Body required");
        }
        if (Plugin.Instance is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Plugin not initialized");
        }
        Plugin.Instance.UpdateConfiguration(cfg);
        _discovery.InvalidateCache();
        return NoContent();
    }

    /// <summary>List all preroll files currently discoverable in the configured folder.</summary>
    [HttpGet("Prerolls")]
    public ActionResult<DiscoveryResult> ListPrerolls()
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var folder = cfg.PrerollFolderPath;
        var folderExists = !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder);
        var items = _discovery.Discover(folder, cfg.AllowedExtensions);

        return Ok(new DiscoveryResult
        {
            FolderPath = folder,
            FolderExists = folderExists,
            Count = items.Count,
            TotalSizeBytes = items.Sum(i => i.FileSizeBytes),
            Files = items.Select(i => new DiscoveryFile
            {
                FileName = i.FileName,
                Path = i.Path,
                SizeBytes = i.FileSizeBytes,
                LastModifiedUtc = i.LastModifiedUtc,
            }).ToList(),
        });
    }

    /// <summary>Force a re-scan of the preroll folder (busts the cache).</summary>
    [HttpPost("Rescan")]
    public ActionResult Rescan()
    {
        _discovery.InvalidateCache();
        return NoContent();
    }

    /// <summary>
    /// Simulate the selection process — returns which file(s) WOULD be picked
    /// right now under the current config. Useful for UI preview.
    /// </summary>
    [HttpGet("Preview")]
    public ActionResult<IEnumerable<DiscoveryFile>> Preview()
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var pool = _discovery.Discover(cfg.PrerollFolderPath, cfg.AllowedExtensions);
        if (pool.Count == 0)
        {
            return Ok(Array.Empty<DiscoveryFile>());
        }
        var picks = _selector.Select(pool, Math.Max(1, cfg.PrerollCount), cfg.SelectionMode);
        return Ok(picks.Select(p => new DiscoveryFile
        {
            FileName = p.FileName,
            Path = p.Path,
            SizeBytes = p.FileSizeBytes,
            LastModifiedUtc = p.LastModifiedUtc,
        }));
    }

    public sealed class DiscoveryResult
    {
        public string FolderPath { get; set; } = string.Empty;
        public bool FolderExists { get; set; }
        public int Count { get; set; }
        public long TotalSizeBytes { get; set; }
        public List<DiscoveryFile> Files { get; set; } = new();
    }

    public sealed class DiscoveryFile
    {
        public string FileName { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public DateTime LastModifiedUtc { get; set; }
    }
}
