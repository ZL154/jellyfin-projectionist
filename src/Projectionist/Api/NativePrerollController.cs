using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Projectionist.Native;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Projectionist.Api;

/// <summary>
/// Files for native-app episode prerolls: the converted preroll segments
/// named in spliced playlists, and external subtitles shifted by the
/// preroll length.
/// </summary>
[ApiController]
[Route("Plugins/Projectionist")]
public sealed class NativePrerollController : ControllerBase
{
    private readonly ILogger<NativePrerollController> _logger;
    private readonly PrerollHlsEncoder _encoder;
    private readonly NativePrerollSessions _sessions;
    private readonly ILibraryManager _libraryManager;
    private readonly ISubtitleEncoder _subtitleEncoder;
    private readonly IUserManager _userManager;

    public NativePrerollController(
        ILogger<NativePrerollController> logger,
        PrerollHlsEncoder encoder,
        NativePrerollSessions sessions,
        ILibraryManager libraryManager,
        ISubtitleEncoder subtitleEncoder,
        IUserManager userManager)
    {
        _userManager = userManager;
        _logger = logger;
        _encoder = encoder;
        _sessions = sessions;
        _libraryManager = libraryManager;
        _subtitleEncoder = subtitleEncoder;
    }

    /// <summary>
    /// A converted preroll segment. Anonymous like the playlist's other
    /// segment URLs are to the player: the key is an opaque hash and the
    /// content is only ever a preroll clip.
    /// </summary>
    [HttpGet("Hls/{cacheKey}/{fileName}")]
    [AllowAnonymous]
    public IActionResult GetSegment(string cacheKey, string fileName)
    {
        var path = _encoder.ResolveFile(cacheKey, fileName);
        if (path is null) return NotFound();

        var type = fileName.EndsWith(".ts", StringComparison.Ordinal) ? "video/mp2t"
            : fileName.EndsWith(".m4s", StringComparison.Ordinal) ? "video/iso.segment"
            : "video/mp4";
        Response.Headers["Cache-Control"] = "public, max-age=86400";
        return PhysicalFile(path, type, enableRangeProcessing: true);
    }

    /// <summary>
    /// An episode's subtitle with every cue moved later by the preroll that
    /// was spliced in front of it. Same source and conversion as Jellyfin's
    /// own subtitle endpoint.
    /// </summary>
    [HttpGet("Subtitles/{playSessionId}/{itemId}/{mediaSourceId}/{index:int}/Stream.{format}")]
    [Authorize]
    public async Task<IActionResult> GetShiftedSubtitle(
        string playSessionId, Guid itemId, string mediaSourceId, int index, string format, CancellationToken ct)
    {
        // Same visibility rules as the rest of the library: a user can only
        // fetch subtitles for items they can see.
        var user = Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var userId) ? _userManager.GetUserById(userId) : null;
        var item = _libraryManager.GetItemById(itemId);
        if (item is null || user is null || !item.IsVisible(user)) return NotFound();

        var session = _sessions.Get(playSessionId);
        if (session is not null && session.EpisodeId != item.Id) session = null;
        var timeout = Math.Clamp(Plugin.Instance?.Configuration.NativePrerollEncodeTimeoutSeconds ?? 20, 2, 120) + 10;
        var offset = session is null ? 0 : await session.GetOffsetAsync(TimeSpan.FromSeconds(timeout)).ConfigureAwait(false);

        string text;
        await using (var stream = await _subtitleEncoder.GetSubtitles(item, mediaSourceId, index, format, 0, 0, false, ct).ConfigureAwait(false))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        _logger.LogDebug("[Projectionist] subtitle {Index} for session {Session} shifted by {Secs:0.00}s",
            index, playSessionId, TimeSpan.FromTicks(offset).TotalSeconds);

        var mime = format.ToLowerInvariant() switch
        {
            "vtt" or "webvtt" => "text/vtt",
            "ass" or "ssa" => "text/x-ssa",
            _ => "application/x-subrip",
        };
        return Content(HlsSplicing.ShiftSubtitles(text, format, offset), mime + "; charset=utf-8");
    }
}
