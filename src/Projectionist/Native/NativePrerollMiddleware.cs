using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.Projectionist.Configuration;
using Jellyfin.Plugin.Projectionist.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Projectionist.Native;

/// <summary>
/// Episode prerolls for native apps (Android TV, iOS, Roku, ...), which
/// never run the web hook. For an episode that gets a preroll:
/// <list type="number">
/// <item>PlaybackInfo: in ForceRemux mode only the direct-play list is
/// cleared, so Jellyfin streams HLS but still copies the codecs the app
/// supports; external subtitle URLs are pointed at a shifting endpoint.</item>
/// <item>main.m3u8: the preroll, converted to the episode's own format, is
/// spliced in front of the episode's segments.</item>
/// <item>Progress reports and media segments are corrected by the preroll
/// length, so resume points, "watched" and intro-skip markers stay right.</item>
/// </list>
/// Anything unexpected falls through to Jellyfin untouched: a missed
/// preroll is better than a broken episode.
/// </summary>
public sealed class NativePrerollMiddleware : IStartupFilter
{
    private static readonly Regex PlaybackInfoPath = new(
        @"^/(?:Users/[0-9a-fA-F-]{32,36}/)?Items/(?<id>[0-9a-fA-F-]{32,36})/PlaybackInfo/?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MainPlaylistPath = new(
        @"^/Videos/(?<id>[0-9a-fA-F-]{32,36})/main\.m3u8$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ReportPath = new(
        @"^/Sessions/Playing(?:/Progress|/Stopped)?/?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MediaSegmentsPath = new(
        @"^/MediaSegments/(?<id>[0-9a-fA-F-]{32,36})/?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SubtitleDeliveryUrl = new(
        @"^/Videos/(?<item>[0-9a-fA-F-]{32,36})/(?<source>[^/]+)/Subtitles/(?<index>\d+)/\d+/Stream\.(?<fmt>[A-Za-z0-9]+)(?<query>\?.*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly ILogger<NativePrerollMiddleware> _logger;
    private readonly NativePrerollSessions _sessions;
    private readonly PrerollHlsEncoder _encoder;

    // One selection per (user, episode, device) for a couple of minutes:
    // apps often call PlaybackInfo more than once when starting, and each
    // call must not pick a new preroll or count another play.
    private readonly ConcurrentDictionary<string, (DateTime At, IReadOnlyList<string> Paths)> _recentPicks = new();

    public NativePrerollMiddleware(
        ILogger<NativePrerollMiddleware> logger,
        NativePrerollSessions sessions,
        PrerollHlsEncoder encoder)
    {
        _logger = logger;
        _sessions = sessions;
        _encoder = encoder;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        => app =>
        {
            app.Use(InvokeAsync);
            next(app);
        };

    internal Task InvokeAsync(HttpContext ctx, Func<Task> next)
    {
        var path = ctx.Request.Path.Value ?? string.Empty;
        var method = ctx.Request.Method;
        Match m;

        if (HttpMethods.IsPost(method) && (m = PlaybackInfoPath.Match(path)).Success)
        {
            return Guarded(ctx, next, fwd => HandlePlaybackInfoAsync(ctx, fwd, m.Groups["id"].Value));
        }

        if (_sessions.IsEmpty) return next();

        if (HttpMethods.IsGet(method) && (m = MainPlaylistPath.Match(path)).Success)
        {
            return Guarded(ctx, next, fwd => HandleMainPlaylistAsync(ctx, fwd, m.Groups["id"].Value));
        }

        if (_sessions.AnyPlayingWithOffset && HttpMethods.IsPost(method) && ReportPath.IsMatch(path))
        {
            return Guarded(ctx, next, fwd => HandleReportAsync(ctx, fwd));
        }

        if (HttpMethods.IsGet(method) && (m = MediaSegmentsPath.Match(path)).Success)
        {
            return Guarded(ctx, next, fwd => HandleMediaSegmentsAsync(ctx, fwd, m.Groups["id"].Value));
        }

        return next();
    }

    /// <summary>
    /// Runs a handler; if it throws before the response has started, the
    /// request goes to Jellyfin as if we weren't here.
    /// </summary>
    private async Task Guarded(HttpContext ctx, Func<Task> next, Func<Func<Task>, Task> handler)
    {
        // Jellyfin's pipeline must run exactly once: if we fail after it
        // already ran, there's nothing safe to retry.
        var forwarded = false;
        Task Forward()
        {
            forwarded = true;
            return next();
        }

        try
        {
            await handler(Forward).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ctx.Response.HasStarted && ex is not OperationCanceledException)
        {
            if (!forwarded)
            {
                _logger.LogError(ex, "[Projectionist] native preroll handling failed for {Path}; passing through", ctx.Request.Path);
                if (ctx.Request.Body.CanSeek) ctx.Request.Body.Position = 0;
                await next().ConfigureAwait(false);
                return;
            }

            // Jellyfin already answered; send its response as it was.
            if (ctx.Items[PendingKey] is Pending pending)
            {
                _logger.LogError(ex, "[Projectionist] rewriting {Path} failed; sending Jellyfin's response unchanged", ctx.Request.Path);
                await WriteAsync(ctx, pending.Text, pending.ContentType).ConfigureAwait(false);
                return;
            }

            throw;
        }
    }

    private const string PendingKey = "Projectionist.PendingResponse";

    /// <summary>A captured Jellyfin response that hasn't been sent yet.</summary>
    private sealed record Pending(string Text, string ContentType);

    // ------------------------------------------------------------ PlaybackInfo

    private async Task HandlePlaybackInfoAsync(HttpContext ctx, Func<Task> next, string rawId)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || cfg.NativeEpisodePrerollMode == NativeEpisodePrerollMode.Off || !cfg.EnableForEpisodes
            || !Guid.TryParse(rawId, out var itemId))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var services = ctx.RequestServices;
        var library = services.GetRequiredService<ILibraryManager>();
        if (library.GetItemById(itemId) is not Episode episode)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var auth = await services.GetRequiredService<IAuthorizationContext>().GetAuthorizationInfo(ctx.Request).ConfigureAwait(false);
        if (!auth.IsAuthenticated || auth.User is null || PrerollIntroProvider.IsWebClient(auth.Client))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var bodyText = await ReadRequestBodyAsync(ctx.Request).ConfigureAwait(false);
        var body = TryParseObject(bodyText);

        // A resume starts mid-episode; splicing would put the player
        // `preroll` seconds early. Resumes play plain.
        if (ReadLong(ctx.Request.Query["StartTimeTicks"]) > 0 || ReadLong(body?["StartTimeTicks"]) > 0)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var paths = await PickPrerollsAsync(services, library, episode, auth).ConfigureAwait(false);
        if (paths.Count == 0)
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (cfg.NativeEpisodePrerollMode == NativeEpisodePrerollMode.ForceRemux && body?["DeviceProfile"] is JsonObject profile)
        {
            if (IsRemuxSafe(services, episode))
            {
                PrepareProfileForRemux(profile);
                ReplaceRequestBody(ctx.Request, body.ToJsonString());
            }
            else
            {
                // Still spliced below if Jellyfin chooses HLS on its own.
                _logger.LogInformation(
                    "[Projectionist] {Episode}: not forcing a remux, Jellyfin has no keyframe data for {Ext} files (see AllowOnDemandMetadataBasedKeyframeExtractionForExtensions)",
                    episode.Name, Path.GetExtension(episode.Path));
            }
        }

        var (status, text) = await CaptureAsync(ctx, next).ConfigureAwait(false);
        if (status != StatusCodes.Status200OK || text is null)
        {
            return;
        }

        var response = TryParseObject(text);
        var psid = Str(response?["PlaySessionId"]);
        var source = (response?["MediaSources"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var transcodingUrl = Str(source?["TranscodingUrl"]);
        var isHls = string.Equals(Str(source?["TranscodingSubProtocol"]), "hls", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(transcodingUrl);

        if (response is null || string.IsNullOrEmpty(psid) || !isHls)
        {
            await WriteAsync(ctx, text, "application/json").ConfigureAwait(false);
            return;
        }

        // Subtitles carried inside the HLS manifest have their own
        // timeline we don't rewrite; play those episodes plain.
        var streams = (source!["MediaStreams"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        if (streams.Any(s => string.Equals(Str(s["DeliveryMethod"]), "Hls", StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogInformation("[Projectionist] {Episode}: subtitles delivered in the HLS manifest, no native preroll", episode.Name);
            await WriteAsync(ctx, text, "application/json").ConfigureAwait(false);
            return;
        }

        var session = new NativePrerollSession(psid, auth.DeviceId ?? string.Empty, episode.Id, paths);
        _sessions.Add(session);

        foreach (var s in streams)
        {
            var url = Str(s["DeliveryUrl"]);
            var isExternalUrl = s["IsExternalUrl"] is JsonValue ev && ev.TryGetValue<bool>(out var b) && b;
            if (string.IsNullOrEmpty(url) || isExternalUrl) continue;
            var sm = SubtitleDeliveryUrl.Match(url);
            if (!sm.Success) continue;
            s["DeliveryUrl"] = string.Create(CultureInfo.InvariantCulture,
                $"/Plugins/Projectionist/Subtitles/{psid}/{sm.Groups["item"].Value}/{sm.Groups["source"].Value}/{sm.Groups["index"].Value}/Stream.{sm.Groups["fmt"].Value}{sm.Groups["query"].Value}");
        }

        // Start converting now so the playlist request usually finds it ready.
        var predicted = ResolveTarget(services, episode, auth, QueryFromUrl(transcodingUrl!));
        if (predicted is not null)
        {
            foreach (var p in paths) _ = _encoder.GetOrEncodeAsync(p, predicted);
        }

        _logger.LogInformation("[Projectionist] native app {Client}: {Count} preroll(s) queued for {Episode} (session {Session})",
            auth.Client, paths.Count, episode.Name, psid);
        await WriteAsync(ctx, response.ToJsonString(), "application/json").ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> PickPrerollsAsync(IServiceProvider services, ILibraryManager library, Episode episode, AuthorizationInfo auth)
    {
        var key = $"{auth.UserId:N}|{episode.Id:N}|{auth.DeviceId}";
        if (_recentPicks.TryGetValue(key, out var recent) && DateTime.UtcNow - recent.At < TimeSpan.FromMinutes(2))
        {
            return recent.Paths;
        }

        var provider = services.GetServices<IIntroProvider>().OfType<PrerollIntroProvider>().FirstOrDefault();
        if (provider is null) return Array.Empty<string>();

        var intros = await provider.GetIntrosForNativeHls(episode, auth.User!).ConfigureAwait(false);
        var paths = intros
            .Select(i => !string.IsNullOrEmpty(i.Path) ? i.Path : i.ItemId is { } id ? library.GetItemById(id)?.Path : null)
            .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
            .Select(p => p!)
            .ToList();

        _recentPicks[key] = (DateTime.UtcNow, paths);
        foreach (var kv in _recentPicks)
        {
            if (DateTime.UtcNow - kv.Value.At > TimeSpan.FromMinutes(10)) _recentPicks.TryRemove(kv.Key, out _);
        }

        return paths;
    }

    /// <summary>
    /// Whether Jellyfin can remux this file into HLS cleanly. When it copies
    /// the video, segments can only start on keyframes; Jellyfin knows where
    /// they are only for extensions in AllowOnDemandMetadataBasedKeyframe-
    /// ExtractionForExtensions (mkv by default). For anything else it guesses
    /// fixed-length segments, and a file with keyframes further apart than
    /// that (common: ~10 s) makes it restart the job mid-stream; ExoPlayer
    /// then sees audio timestamps jump backwards and hangs ~18 s at the end.
    /// That happens with or without a preroll, but we must not force a file
    /// that direct-plays fine into it.
    /// </summary>
    internal static bool IsRemuxSafe(IServiceProvider services, BaseItem item)
    {
        var ext = Path.GetExtension(item.Path ?? string.Empty).TrimStart('.');
        if (ext.Length == 0) return false;
        var allowed = (services.GetService<IServerConfigurationManager>()
                ?.GetConfiguration("encoding") as EncodingOptions)
            ?.AllowOnDemandMetadataBasedKeyframeExtractionForExtensions
            ?? new[] { "mkv" };
        return allowed.Any(a => string.Equals(a?.TrimStart('.'), ext, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Clears only the direct-play list, so Jellyfin falls back to HLS but
    /// still copies codecs the app allows. Subtitle profiles that would put
    /// subtitles in the manifest are dropped where the app also takes the
    /// same format as an external file, which we can shift.
    /// </summary>
    internal static void PrepareProfileForRemux(JsonObject profile)
    {
        profile["DirectPlayProfiles"] = new JsonArray();

        if (profile["SubtitleProfiles"] is not JsonArray subs) return;
        var external = subs.OfType<JsonObject>()
            .Where(s => string.Equals(Str(s["Method"]), "External", StringComparison.OrdinalIgnoreCase))
            .Select(s => Str(s["Format"])?.ToLowerInvariant())
            .ToHashSet();
        foreach (var s in subs.OfType<JsonObject>().ToList())
        {
            var isHls = string.Equals(Str(s["Method"]), "Hls", StringComparison.OrdinalIgnoreCase);
            if (isHls && external.Contains(Str(s["Format"])?.ToLowerInvariant()))
            {
                subs.Remove(s);
            }
        }
    }

    // ------------------------------------------------------------ main.m3u8

    private async Task HandleMainPlaylistAsync(HttpContext ctx, Func<Task> next, string rawId)
    {
        var session = _sessions.Get(ctx.Request.Query["PlaySessionId"].ToString());
        if (session is null || !Guid.TryParse(rawId, out var itemId) || itemId != session.EpisodeId)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var (status, text) = await CaptureAsync(ctx, next).ConfigureAwait(false);
        if (status != StatusCodes.Status200OK || text is null)
        {
            session.Decide(0);
            return;
        }

        var entries = await GetEntriesAsync(ctx, session).ConfigureAwait(false);
        if (entries.Count == 0)
        {
            await WriteAsync(ctx, text, "application/vnd.apple.mpegurl").ConfigureAwait(false);
            return;
        }

        var prefix = ctx.Request.PathBase.Value?.TrimEnd('/') + "/Plugins/Projectionist/Hls";
        var spliced = HlsSplicing.Splice(text, entries, prefix);
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        await WriteAsync(ctx, spliced, "application/vnd.apple.mpegurl").ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<PrerollHlsEntry>> GetEntriesAsync(HttpContext ctx, NativePrerollSession session)
    {
        await session.Gate.WaitAsync().ConfigureAwait(false);
        var entries = new List<PrerollHlsEntry>();
        try
        {
            if (session.Entries is not null) return session.Entries;

            var services = ctx.RequestServices;
            var library = services.GetRequiredService<ILibraryManager>();
            var auth = await services.GetRequiredService<IAuthorizationContext>().GetAuthorizationInfo(ctx.Request).ConfigureAwait(false);
            var target = library.GetItemById(session.EpisodeId) is Episode ep
                ? ResolveTarget(services, ep, auth, ctx.Request.Query.ToDictionary(k => k.Key, v => v.Value.ToString(), StringComparer.OrdinalIgnoreCase))
                : null;

            if (target is null)
            {
                _logger.LogInformation("[Projectionist] episode stream format not supported for splicing (HDR or codec); no native preroll");
                return entries;
            }

            var timeout = TimeSpan.FromSeconds(Math.Clamp(Plugin.Instance?.Configuration.NativePrerollEncodeTimeoutSeconds ?? 20, 2, 120));
            var work = Task.WhenAll(session.PrerollPaths.Select(p => _encoder.GetOrEncodeAsync(p, target)));
            if (await Task.WhenAny(work, Task.Delay(timeout)).ConfigureAwait(false) == work)
            {
                entries.AddRange(work.Result.Where(e => e is not null)!);
            }
            else
            {
                _logger.LogWarning("[Projectionist] preroll conversion for {Target} took longer than {Secs}s; episode starts without it (it will be ready next time)",
                    target.Key, timeout.TotalSeconds);
            }

            return entries;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[Projectionist] preparing native prerolls failed; episode plays without one");
            entries.Clear();
            return entries;
        }
        finally
        {
            // Always settle the session, even on failure: subtitle and
            // media-segment requests wait on this and must not hang.
            if (session.Entries is null)
            {
                session.Entries = entries;
                var ticks = entries.Sum(e => e.DurationTicks);
                session.Decide(ticks);
                _sessions.MarkPlaying(session.PlaySessionId, ticks);
            }

            session.Gate.Release();
        }
    }

    private static HlsTarget? ResolveTarget(IServiceProvider services, Episode episode, AuthorizationInfo auth, IReadOnlyDictionary<string, string> query)
    {
        var sources = services.GetRequiredService<IMediaSourceManager>().GetStaticMediaSources(episode, false, auth.User);
        query.TryGetValue("MediaSourceId", out var sourceId);
        var source = sources.FirstOrDefault(s => string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase)) ?? sources.FirstOrDefault();
        if (source is null) return null;

        var video = source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        MediaStream? audio = null;
        if (query.TryGetValue("AudioStreamIndex", out var ai) && int.TryParse(ai, NumberStyles.Integer, CultureInfo.InvariantCulture, out var audioIndex))
        {
            audio = source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.Index == audioIndex);
        }

        audio ??= source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.IsDefault)
                  ?? source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Audio);
        if (video is null) return null;

        var range = video.VideoRange.ToString();
        var isHdr = !string.Equals(range, "SDR", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(range, "Unknown", StringComparison.OrdinalIgnoreCase);

        query.TryGetValue("VideoCodec", out var vc);
        query.TryGetValue("AudioCodec", out var ac);
        query.TryGetValue("SegmentContainer", out var container);
        return HlsSplicing.ResolveTarget(video.Codec, audio?.Codec, video.Width ?? 0, video.Height ?? 0,
            video.RealFrameRate ?? video.AverageFrameRate, isHdr, vc, ac, container);
    }

    // ------------------------------------------------------------ reports + segments

    private async Task HandleReportAsync(HttpContext ctx, Func<Task> next)
    {
        var text = await ReadRequestBodyAsync(ctx.Request).ConfigureAwait(false);
        string? stoppedSession = null;
        var corrected = HlsSplicing.CorrectPlaybackReport(text, sid =>
        {
            stoppedSession = sid;
            return _sessions.OffsetForReport(sid);
        });
        if (corrected is not null) ReplaceRequestBody(ctx.Request, corrected);
        await next().ConfigureAwait(false);

        if (stoppedSession is not null
            && ctx.Request.Path.Value!.TrimEnd('/').EndsWith("/Stopped", StringComparison.OrdinalIgnoreCase))
        {
            _sessions.MarkStopped(stoppedSession);
        }
    }

    private async Task HandleMediaSegmentsAsync(HttpContext ctx, Func<Task> next, string rawId)
    {
        if (!Guid.TryParse(rawId, out var itemId))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var auth = await ctx.RequestServices.GetRequiredService<IAuthorizationContext>().GetAuthorizationInfo(ctx.Request).ConfigureAwait(false);
        var session = _sessions.LatestFor(auth.DeviceId, itemId);
        var offset = session is null ? 0 : await session.GetOffsetAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if (offset <= 0)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var (status, text) = await CaptureAsync(ctx, next).ConfigureAwait(false);
        if (status != StatusCodes.Status200OK || text is null) return;
        await WriteAsync(ctx, HlsSplicing.ShiftMediaSegments(text, offset) ?? text, "application/json").ConfigureAwait(false);
    }

    // ------------------------------------------------------------ plumbing

    /// <summary>
    /// Runs the rest of the pipeline with the response captured. Asks for an
    /// uncompressed body (see issue #7). Returns (status, text) when the body
    /// is plain text we may rewrite; otherwise the original bytes have
    /// already been sent and text is null.
    /// </summary>
    private static async Task<(int Status, string? Text)> CaptureAsync(HttpContext ctx, Func<Task> next)
    {
        ctx.Request.Headers.Remove("Accept-Encoding");
        var original = ctx.Response.Body;
        using var buffer = new MemoryStream();
        ctx.Response.Body = buffer;
        try
        {
            await next().ConfigureAwait(false);
        }
        finally
        {
            ctx.Response.Body = original;
        }

        buffer.Position = 0;
        var encoded = !string.IsNullOrEmpty(ctx.Response.Headers["Content-Encoding"].ToString());
        if (ctx.Response.StatusCode != StatusCodes.Status200OK || encoded)
        {
            await buffer.CopyToAsync(original).ConfigureAwait(false);
            return (ctx.Response.StatusCode, null);
        }

        var text = Encoding.UTF8.GetString(buffer.ToArray());
        ctx.Items[PendingKey] = new Pending(text, ctx.Response.ContentType ?? "application/octet-stream");
        return (ctx.Response.StatusCode, text);
    }

    private static async Task WriteAsync(HttpContext ctx, string text, string contentType)
    {
        ctx.Items.Remove(PendingKey);
        var bytes = Encoding.UTF8.GetBytes(text);
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength = bytes.Length;
        await ctx.Response.Body.WriteAsync(bytes).ConfigureAwait(false);
    }

    private static async Task<string> ReadRequestBodyAsync(HttpRequest request)
    {
        request.EnableBuffering();
        request.Body.Position = 0;
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        var text = await reader.ReadToEndAsync().ConfigureAwait(false);
        request.Body.Position = 0;
        return text;
    }

    private static void ReplaceRequestBody(HttpRequest request, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        request.Body = new MemoryStream(bytes);
        request.ContentLength = bytes.Length;
    }

    /// <summary>A string value, or null for anything else (number, object, missing).</summary>
    private static string? Str(JsonNode? node)
        => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static JsonObject? TryParseObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static long ReadLong(Microsoft.Extensions.Primitives.StringValues value)
        => long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static long ReadLong(JsonNode? node)
    {
        if (node is not JsonValue v) return 0;
        if (v.TryGetValue<long>(out var n)) return n;
        return v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0;
    }

    private static Dictionary<string, string> QueryFromUrl(string url)
    {
        var q = url.IndexOf('?', StringComparison.Ordinal);
        var parsed = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(q >= 0 ? url[q..] : string.Empty);
        return parsed.ToDictionary(k => k.Key, v => v.Value.ToString(), StringComparer.OrdinalIgnoreCase);
    }
}
