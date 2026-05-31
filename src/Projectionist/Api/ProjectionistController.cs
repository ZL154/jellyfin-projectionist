using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Projectionist.Configuration;
using Jellyfin.Plugin.Projectionist.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
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
    private readonly ILibraryManager _libraryManager;
    private readonly HiddenLibraryManager _hiddenLibrary;
    private readonly StatsStore _stats;
    private readonly IUserManager _userManager;

    public ProjectionistController(
        ILogger<ProjectionistController> logger,
        PrerollDiscoveryService discovery,
        PrerollSelector selector,
        ILibraryManager libraryManager,
        HiddenLibraryManager hiddenLibrary,
        StatsStore stats,
        IUserManager userManager)
    {
        _logger = logger;
        _discovery = discovery;
        _selector = selector;
        _libraryManager = libraryManager;
        _hiddenLibrary = hiddenLibrary;
        _stats = stats;
        _userManager = userManager;
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
    public async Task<ActionResult> SaveConfig([FromBody] PluginConfiguration cfg)
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
        // Make sure the hidden library tracks the configured folder. This is what
        // actually makes the prerolls streamable.
        await _hiddenLibrary.EnsureAsync(cfg);
        return NoContent();
    }

    /// <summary>Manually (re)build the hidden Jellyfin library at the configured folder.</summary>
    [HttpPost("SetupLibrary")]
    public async Task<ActionResult<LibraryStatus>> SetupLibrary()
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        await _hiddenLibrary.EnsureAsync(cfg);
        return Ok(_hiddenLibrary.GetStatus(cfg));
    }

    /// <summary>Remove the hidden Jellyfin library if it exists.</summary>
    [HttpPost("RemoveLibrary")]
    public async Task<ActionResult> RemoveLibrary()
    {
        await _hiddenLibrary.RemoveIfExistsAsync();
        return NoContent();
    }

    /// <summary>Returns the status of the hidden Jellyfin library.</summary>
    [HttpGet("LibraryStatus")]
    public ActionResult<LibraryStatus> GetLibraryStatus()
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        return Ok(_hiddenLibrary.GetStatus(cfg));
    }

    /// <summary>List all preroll files currently discoverable across all configured folders.</summary>
    [HttpGet("Prerolls")]
    public ActionResult<DiscoveryResult> ListPrerolls()
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var items = _discovery.Discover(cfg);
        var folders = PrerollDiscoveryService.ResolveFolders(cfg);
        var folder = !string.IsNullOrWhiteSpace(cfg.PrerollFolderPath)
            ? cfg.PrerollFolderPath
            : string.Join(", ", folders.Select(f => f.Path));
        var folderExists = folders.Count > 0 && folders.All(f => Directory.Exists(f.Path));

        return Ok(new DiscoveryResult
        {
            FolderPath = folder,
            FolderExists = folderExists,
            Count = items.Count,
            TotalSizeBytes = items.Sum(i => i.FileSizeBytes),
            Folders = folders.Select(f => f.Path).ToList(),
            Files = items.Select(i => new DiscoveryFile
            {
                FileName = i.FileName,
                Path = i.Path,
                SizeBytes = i.FileSizeBytes,
                LastModifiedUtc = i.LastModifiedUtc,
                Tags = i.Tags,
                Weight = i.Weight,
                Rating = i.Rating,
                SourceFolder = i.SourceFolder,
            }).ToList(),
        });
    }

    /// <summary>Stream a preroll for in-browser preview. Filename comes from the list endpoint.</summary>
    [HttpGet("Preview/{fileName}")]
    [Produces("video/mp4")]
    public ActionResult PreviewFile([FromRoute] string fileName)
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var items = _discovery.Discover(cfg);
        var match = items.FirstOrDefault(i => string.Equals(i.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        if (match is null) return NotFound();
        if (!System.IO.File.Exists(match.Path)) return NotFound();
        var stream = System.IO.File.OpenRead(match.Path);
        var ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
        var mime = ext switch
        {
            ".mp4" or ".m4v" => "video/mp4",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".mov" => "video/quicktime",
            _ => "application/octet-stream",
        };
        return File(stream, mime, enableRangeProcessing: true);
    }

    /// <summary>Get aggregated playback stats.</summary>
    [HttpGet("Stats")]
    public ActionResult<StatsResponse> GetStats()
    {
        var snap = _stats.Snapshot();
        var users = _userManager.EnumerateAll().ToDictionary(u => u.Id, u => u.Username);
        return Ok(new StatsResponse
        {
            TotalPlays = snap.TotalPlays,
            Files = snap.Files.Select(f => new StatsFileEntry
            {
                FileName = f.FileName,
                PlayCount = f.PlayCount,
                LastPlayedUtc = f.LastPlayedUtc,
            }).ToList(),
            Users = snap.Users.Select(u => new StatsUserEntry
            {
                UserId = u.UserId,
                UserName = users.GetValueOrDefault(u.UserId, "(deleted)"),
                PlayCount = u.PlayCount,
            }).ToList(),
        });
    }

    /// <summary>Reset all stats counters.</summary>
    [HttpPost("Stats/Reset")]
    public ActionResult ResetStats()
    {
        _stats.Reset();
        return NoContent();
    }

    /// <summary>List all Jellyfin users (for the per-user rule UI).</summary>
    [HttpGet("Users")]
    public ActionResult<IEnumerable<UserBrief>> ListUsers()
    {
        var users = _userManager.EnumerateAll()
            .Select(u => new UserBrief { Id = u.Id, Name = u.Username })
            .OrderBy(u => u.Name)
            .ToList();
        return Ok(users);
    }

    /// <summary>List Jellyfin libraries (for per-library rule UI).</summary>
    [HttpGet("Libraries")]
    public ActionResult<IEnumerable<LibraryBrief>> ListLibraries()
    {
        var vfolders = _libraryManager.GetVirtualFolders()
            .Where(v => !string.Equals(v.Name, HiddenLibraryManager.LibraryName, StringComparison.OrdinalIgnoreCase))
            .Select(v => new LibraryBrief { Name = v.Name })
            .OrderBy(v => v.Name)
            .ToList();
        return Ok(vfolders);
    }

    /// <summary>Validate the current configuration and return user-actionable problems.</summary>
    [HttpGet("Validate")]
    public ActionResult<ValidationResult> Validate()
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var problems = new List<string>();
        var folders = PrerollDiscoveryService.ResolveFolders(cfg);
        if (folders.Count == 0)
        {
            problems.Add("No preroll folders configured. Set the Preroll folder path.");
        }
        else
        {
            foreach (var f in folders)
            {
                if (!Directory.Exists(f.Path))
                    problems.Add($"Folder does not exist or is unreadable: {f.Path}");
            }
        }
        var items = _discovery.Discover(cfg);
        if (folders.Count > 0 && items.Count == 0)
        {
            problems.Add("Preroll folder exists but contains no playable video files. Check Allowed extensions.");
        }
        if (cfg.PrerollCount <= 0)
        {
            problems.Add("Preroll count is 0; no prerolls will play. Increase to at least 1.");
        }
        if (!cfg.EnableForMovies && !cfg.EnableForEpisodes && !cfg.EnableForMusicVideos)
        {
            problems.Add("All content types are disabled — prerolls will never trigger.");
        }
        var libStatus = _hiddenLibrary.GetStatus(cfg);
        if (folders.Count > 0 && !libStatus.LibraryExists)
        {
            problems.Add("Hidden library is not set up. Click 'Set up library' so playback can resolve preroll items.");
        }
        else if (folders.Count > 0 && !libStatus.LibraryMatchesFolder)
        {
            problems.Add("Hidden library locations do not match the configured preroll folders. Click 'Set up library' to rebuild it.");
        }
        return Ok(new ValidationResult
        {
            Ok = problems.Count == 0,
            Problems = problems,
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
        var pool = _discovery.Discover(cfg);
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
        public List<string> Folders { get; set; } = new();
        public List<DiscoveryFile> Files { get; set; } = new();
    }

    public sealed class DiscoveryFile
    {
        public string FileName { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public DateTime LastModifiedUtc { get; set; }
        public List<string> Tags { get; set; } = new();
        public double Weight { get; set; } = 1.0;
        public string? Rating { get; set; }
        public string SourceFolder { get; set; } = string.Empty;
    }

    public sealed class StatsResponse
    {
        public long TotalPlays { get; set; }
        public List<StatsFileEntry> Files { get; set; } = new();
        public List<StatsUserEntry> Users { get; set; } = new();
    }

    public sealed class StatsFileEntry
    {
        public string FileName { get; set; } = string.Empty;
        public long PlayCount { get; set; }
        public DateTime LastPlayedUtc { get; set; }
    }

    public sealed class StatsUserEntry
    {
        public Guid UserId { get; set; }
        public string UserName { get; set; } = string.Empty;
        public long PlayCount { get; set; }
    }

    public sealed class UserBrief
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public sealed class LibraryBrief
    {
        public string Name { get; set; } = string.Empty;
    }

    public sealed class ValidationResult
    {
        public bool Ok { get; set; }
        public List<string> Problems { get; set; } = new();
    }
}
