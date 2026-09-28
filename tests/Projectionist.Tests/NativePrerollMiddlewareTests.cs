using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Projectionist.Native;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Projectionist.Tests;

public class NativePrerollMiddlewareTests
{
    private const string Playlist = "#EXTM3U\n#EXT-X-TARGETDURATION:3\n#EXTINF:3.0,\nhls1/main/0.ts\n#EXT-X-ENDLIST\n";

    [Fact]
    public async Task FailureWhilePreparingPreroll_SendsJellyfinsPlaylistAndSettlesSession()
    {
        var sessions = new NativePrerollSessions();
        var session = new NativePrerollSession("sess1", "dev", Guid.Parse("4eb8a206dc388eb4165f7be0faf1cb4e"), new[] { "/nope.mp4" });
        sessions.Add(session);
        // No Jellyfin services registered: resolving the episode throws inside the handler.
        var middleware = new NativePrerollMiddleware(NullLogger<NativePrerollMiddleware>.Instance, sessions, encoder: null!);

        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/videos/4eb8a206dc388eb4165f7be0faf1cb4e/main.m3u8";
        ctx.Request.QueryString = new QueryString("?PlaySessionId=sess1");
        ctx.Response.Body = new MemoryStream();
        var calls = 0;

        await middleware.InvokeAsync(ctx, async () =>
        {
            calls++;
            ctx.Response.ContentType = "application/vnd.apple.mpegurl";
            await ctx.Response.WriteAsync(Playlist);
        });

        Assert.Equal(1, calls); // Jellyfin's pipeline ran exactly once
        ctx.Response.Body.Position = 0;
        Assert.Equal(Playlist, await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync());
        Assert.True(session.IsDecided);
        Assert.Equal(0, await session.SplicedTicks);
        Assert.False(sessions.AnyPlayingWithOffset);
    }

    [Fact]
    public async Task UnrelatedRequests_PassStraightThrough()
    {
        var middleware = new NativePrerollMiddleware(NullLogger<NativePrerollMiddleware>.Instance, new NativePrerollSessions(), encoder: null!);
        foreach (var (method, path) in new[] { ("POST", "/Sessions/Playing/Progress"), ("GET", "/videos/x/main.m3u8"), ("GET", "/Items") })
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Method = method;
            ctx.Request.Path = path;
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"PositionTicks\":5}"));
            var ran = false;
            await middleware.InvokeAsync(ctx, () => { ran = true; return Task.CompletedTask; });
            Assert.True(ran);
            Assert.False(ctx.Request.Body.CanSeek && ctx.Request.Body.Position != 0);
        }
    }
}
