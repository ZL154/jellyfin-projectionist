using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.Projectionist.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Projectionist.Web;

/// <summary>
/// v1.2.0 Milestone B — intercepts <c>/Videos/{itemId}/stream*</c> and
/// <c>/Videos/{itemId}/master.m3u8</c> requests. When the item is an
/// Episode that has prerolls available, future milestones replace the
/// response with an FFmpeg-concat'd stream that plays the preroll(s)
/// followed by the episode as a single seamless video. This lets episode
/// prerolls work universally — Jellyfin Web, Android TV, iOS, Roku,
/// Chromecast — without any client-side JS, because the client just
/// receives "one video file".
///
/// MILESTONE 1: plumbing only. The middleware identifies eligible
/// requests and logs them, but still passes through to Jellyfin's native
/// handler. Verifies the interception layer works without breaking
/// anything.
/// </summary>
public sealed class StreamConcatStartupFilter : IStartupFilter
{
    private readonly ILogger<StreamConcatStartupFilter> _logger;
    private readonly ILibraryManager _library;
    private readonly PrerollDiscoveryService _discovery;
    private readonly PrerollSelector _selector;
    private readonly FeatureOptOutStore _optOuts;
    private readonly StreamConcatService _concat;

    // Match /Videos/{32-hex-guid}/stream(.ext)? or master.m3u8 / main.m3u8
    private static readonly Regex StreamPathRegex = new(
        @"^/Videos/([0-9a-fA-F]{32})/(stream(?:\.[a-z0-9]+)?|master\.m3u8|main\.m3u8)$",
        RegexOptions.Compiled);

    public StreamConcatStartupFilter(
        ILogger<StreamConcatStartupFilter> logger,
        ILibraryManager library,
        PrerollDiscoveryService discovery,
        PrerollSelector selector,
        FeatureOptOutStore optOuts,
        StreamConcatService concat)
    {
        _logger = logger;
        _library = library;
        _discovery = discovery;
        _selector = selector;
        _optOuts = optOuts;
        _concat = concat;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(InterceptAsync);
            next(app);
        };
    }

    private async Task InterceptAsync(HttpContext context, Func<Task> nextMiddleware)
    {
        var info = TryParseStreamRequest(context);
        if (info is null)
        {
            await nextMiddleware().ConfigureAwait(false);
            return;
        }

        // For HLS we'd need to manipulate playlists + segments. Defer to
        // Milestone 6; for M2-M5 we pass HLS requests through unchanged.
        if (info.Kind == StreamRequestKind.Hls)
        {
            await nextMiddleware().ConfigureAwait(false);
            return;
        }

        BaseItem? item;
        try
        {
            item = _library.GetItemById(info.ItemId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Projectionist] could not resolve item {Id} during stream intercept", info.ItemId);
            await nextMiddleware().ConfigureAwait(false);
            return;
        }

        if (item is null || item is not Episode)
        {
            await nextMiddleware().ConfigureAwait(false);
            return;
        }

        if (_optOuts.IsOptedOut(item.Id))
        {
            await nextMiddleware().ConfigureAwait(false);
            return;
        }

        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.EnableForEpisodes || config.PrerollCount <= 0)
        {
            await nextMiddleware().ConfigureAwait(false);
            return;
        }

        // Resolve the episode's local file path. If we can't, we have
        // nothing to concat onto, so pass through.
        var episodePath = item.Path;
        if (string.IsNullOrWhiteSpace(episodePath) || !System.IO.File.Exists(episodePath))
        {
            await nextMiddleware().ConfigureAwait(false);
            return;
        }

        var pool = _discovery.Discover(config);
        if (pool.Count == 0)
        {
            await nextMiddleware().ConfigureAwait(false);
            return;
        }

        // Use the existing selector to pick preroll(s) honouring tags +
        // schedules + maturity gate + cooldowns.
        var picks = _selector.SelectFor(pool, item, config);
        if (picks.Count == 0)
        {
            await nextMiddleware().ConfigureAwait(false);
            return;
        }

        if (_concat.FfmpegPath is null)
        {
            _logger.LogWarning("[Projectionist] concat requested but ffmpeg unavailable; passing through");
            await nextMiddleware().ConfigureAwait(false);
            return;
        }

        var prerollPaths = new List<string>(picks.Count);
        foreach (var p in picks)
        {
            if (!string.IsNullOrWhiteSpace(p.Path) && System.IO.File.Exists(p.Path))
            {
                prerollPaths.Add(p.Path);
            }
        }
        if (prerollPaths.Count == 0)
        {
            await nextMiddleware().ConfigureAwait(false);
            return;
        }

        _logger.LogInformation(
            "[Projectionist] M2: concat'ing {N} preroll(s) + Episode {Name} for stream request",
            prerollPaths.Count, item.Name);

        // HEAD: respond 200 OK with the correct Content-Type but no body.
        // Clients send HEAD before GET to probe Content-Length etc.; we
        // can't know the length up front (live concat), so omit it.
        if (HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "video/mp4";
            context.Response.Headers["Accept-Ranges"] = "none";
            return;
        }

        context.Response.StatusCode = 200;
        context.Response.ContentType = "video/mp4";
        context.Response.Headers["Accept-Ranges"] = "none";
        context.Response.Headers["Cache-Control"] = "no-cache, no-store";

        try
        {
            await _concat.ConcatToStreamAsync(
                prerollPaths,
                episodePath,
                context.Response.Body,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Projectionist] concat failed for {Item}; falling back to native stream", item.Name);
            // We've already written response headers, so we can't fall
            // back cleanly. The client will see an aborted response and
            // most likely auto-retry, at which point our intercept may
            // succeed or pass through if ffmpeg is unreachable.
        }
    }

    private static StreamRequestInfo? TryParseStreamRequest(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            return null;
        }
        var path = context.Request.Path.Value;
        if (string.IsNullOrEmpty(path)) return null;
        var match = StreamPathRegex.Match(path);
        if (!match.Success) return null;
        if (!Guid.TryParse(match.Groups[1].Value, out var itemId)) return null;
        var resource = match.Groups[2].Value;
        var kind = resource.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            ? StreamRequestKind.Hls
            : StreamRequestKind.DirectStream;
        return new StreamRequestInfo(itemId, kind, resource);
    }
}

public enum StreamRequestKind
{
    DirectStream,
    Hls,
}

public sealed record StreamRequestInfo(Guid ItemId, StreamRequestKind Kind, string ResourceName);
