using System.IO;
using System.Reflection;
using Jellyfin.Plugin.Projectionist.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Projectionist.Api;

/// <summary>
/// Serves the embedded playback-hook.js to the web client. Anonymous because
/// the script tag is included in index.html which any user (including unauth)
/// loads. The script itself uses ApiClient (already authenticated) for any
/// privileged calls.
/// </summary>
[ApiController]
[Route("Plugins/Projectionist")]
public sealed class HookController : ControllerBase
{
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

    public sealed class HookSettingsResponse
    {
        public bool EnableSkippablePrerolls { get; set; }
        public int SkippableAfterSeconds { get; set; }
        public bool EnableFeaturePreload { get; set; }
        public FeaturePreloadMode FeaturePreloadMode { get; set; }
    }
}
