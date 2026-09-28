using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.Projectionist.Configuration;
using Jellyfin.Plugin.Projectionist.Providers;
using Jellyfin.Plugin.Projectionist.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Projectionist.Api;

/// <summary>
/// Endpoints the injected playback hook calls for every user. They live
/// here, not on <see cref="ProjectionistController"/>, because that class
/// requires elevation: a method-level [Authorize] only adds to a class
/// policy, so non-admin viewers got 403 there (skip reports were only ever
/// recorded for admins).
/// </summary>
[ApiController]
[Route("Plugins/Projectionist")]
public sealed class HookController : ControllerBase
{
    private readonly ILogger<HookController> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly HiddenLibraryManager _hiddenLibrary;
    private readonly PrerollDiscoveryService _discovery;
    private readonly PostRollService _postRoll;
    private readonly FeatureOptOutStore _optOuts;
    private readonly StatsStore _stats;

    public HookController(
        ILogger<HookController> logger,
        ILibraryManager libraryManager,
        HiddenLibraryManager hiddenLibrary,
        PrerollDiscoveryService discovery,
        PostRollService postRoll,
        FeatureOptOutStore optOuts,
        StatsStore stats)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _hiddenLibrary = hiddenLibrary;
        _discovery = discovery;
        _postRoll = postRoll;
        _optOuts = optOuts;
        _stats = stats;
    }

    /// <summary>
    /// Serves the embedded playback-hook.js. Anonymous because the script tag
    /// is in index.html, which the login page loads too.
    /// </summary>
    [HttpGet("Hook.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult GetHook()
    {
        var asm = typeof(HookController).Assembly;
        var resourceName = $"{typeof(Plugin).Namespace}.Web.playback-hook.js";
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return NotFound("hook resource not embedded");
        }
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        Response.Headers["Cache-Control"] = "public, max-age=300";
        return Content(content, "application/javascript");
    }

    [HttpGet("HookSettings")]
    [Authorize]
    public ActionResult<HookSettingsResponse> GetHookSettings()
    {
        var cfg = Plugin.Instance?.Configuration;
        var preloadMode = cfg?.FeaturePreloadMode ?? FeaturePreloadMode.Off;
        if (cfg?.EnableFeaturePreload == true && preloadMode == FeaturePreloadMode.Off)
        {
            preloadMode = FeaturePreloadMode.Warm;
        }

        return Ok(new HookSettingsResponse
        {
            EnableSkippablePrerolls = cfg?.EnableSkippablePrerolls ?? true,
            SkippableAfterSeconds = cfg?.SkippableAfterSeconds ?? 0,
            EnableFeaturePreload = preloadMode != FeaturePreloadMode.Off,
            FeaturePreloadMode = preloadMode,
        });
    }

    /// <summary>
    /// Is this library item one of our clips? Lets the skip button recognise
    /// a preroll without the admin-only preroll list (which also exposes
    /// server paths).
    /// </summary>
    [HttpGet("Clip/{itemId:guid}")]
    [Authorize]
    public ActionResult<ClipInfo> GetClip(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null || !_hiddenLibrary.IsManagedItem(item))
        {
            return Ok(new ClipInfo { IsClip = false });
        }

        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var isPostRoll = !string.IsNullOrWhiteSpace(cfg.PostRollFolderPath)
            && PathIsUnder(item.Path, cfg.PostRollFolderPath);
        return Ok(new ClipInfo
        {
            IsClip = true,
            Kind = isPostRoll ? "postroll" : "preroll",
            FileName = Path.GetFileName(item.Path ?? string.Empty),
        });
    }

    [HttpPost("SkipReport")]
    [Authorize]
    public ActionResult RecordSkip([FromBody] ProjectionistController.SkipReportRequest req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.FileName)) return BadRequest("FileName required");

        // Any signed-in user can call this, so only accept real preroll file
        // names; otherwise the stats file could be filled with junk entries.
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var known = _discovery.Discover(cfg).Any(p =>
            string.Equals(p.FileName, req.FileName, StringComparison.OrdinalIgnoreCase));
        if (!known) return NotFound();

        if (req.SecondsBeforeSkip < 0) req.SecondsBeforeSkip = 0;
        _stats.RecordSkip(req.FileName, req.SecondsBeforeSkip);
        return NoContent();
    }

    /// <summary>
    /// Post-rolls to play after <paramref name="featureId"/> finished.
    /// Returns library item ids the client can queue, and nothing when the
    /// feature is not eligible (content type off, opted out, user excluded)
    /// or is itself a preroll/post-roll, which is what stops a post-roll
    /// from triggering another one.
    /// </summary>
    [HttpGet("PostRoll/Picks")]
    [Authorize]
    public ActionResult<IEnumerable<PostRollPick>> PickPostRolls([FromQuery] Guid featureId)
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var none = Ok(Array.Empty<PostRollPick>());
        if (cfg.PostRollCount <= 0 || featureId == Guid.Empty) return none;

        var feature = _libraryManager.GetItemById(featureId);
        if (feature is null || _hiddenLibrary.IsManagedItem(feature)) return none;
        if (!PrerollIntroProvider.IsContentTypeEnabled(feature, cfg)) return none;
        if (_optOuts.IsOptedOut(feature)) return none;
        if (Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var userId)
            && !PrerollIntroProvider.UserAllowed(userId, cfg))
        {
            return none;
        }

        var picks = new List<PostRollPick>();
        foreach (var file in _postRoll.Pick(cfg, cfg.PostRollCount))
        {
            // Only clips the hidden library has indexed are playable.
            var item = _hiddenLibrary.FindItem(file.Path);
            if (item is not null)
            {
                picks.Add(new PostRollPick { ItemId = item.Id, Name = item.Name });
            }
        }

        if (picks.Count == 0)
        {
            _logger.LogDebug("[Projectionist] no indexed post-roll clips for {Feature}", feature.Name);
        }

        return Ok(picks);
    }

    private static bool PathIsUnder(string? path, string folder)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var root = folder.TrimEnd('/', '\\');
        return path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class HookSettingsResponse
    {
        public bool EnableSkippablePrerolls { get; set; }
        public int SkippableAfterSeconds { get; set; }
        public bool EnableFeaturePreload { get; set; }
        public FeaturePreloadMode FeaturePreloadMode { get; set; }
    }

    public sealed class ClipInfo
    {
        public bool IsClip { get; set; }
        public string? Kind { get; set; }
        public string? FileName { get; set; }
    }

    public sealed class PostRollPick
    {
        public Guid ItemId { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
