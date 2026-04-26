using System.IO;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
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
[AllowAnonymous]
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
}
