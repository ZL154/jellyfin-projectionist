using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Projectionist.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Projectionist.Tests;

/// <summary>
/// [issue #7] Jellyfin 10.11.11+ and 12 gzip/brotli index.html. The filter
/// used to append a plain-text script tag to the compressed bytes, which
/// broke the whole web client with "failed to decode".
/// </summary>
public class IndexHtmlInjectionFilterTests
{
    private const string Page = "<html><head></head><body><div id=\"app\"></div></body></html>";

    private static IndexHtmlInjectionFilter NewFilter()
        => new(NullLogger<IndexHtmlInjectionFilter>.Instance);

    private static DefaultHttpContext NewContext(string path, string? acceptEncoding)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = path;
        if (acceptEncoding is not null) ctx.Request.Headers["Accept-Encoding"] = acceptEncoding;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string ReadBody(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        return new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEnd();
    }

    [Fact]
    public async Task PlainHtml_GetsHookTagBeforeBodyClose()
    {
        var ctx = NewContext("/web/", null);
        await NewFilter().InjectAsync(ctx, async () =>
        {
            ctx.Response.ContentType = "text/html";
            await ctx.Response.WriteAsync(Page);
        });

        var body = ReadBody(ctx);
        Assert.Contains("Plugins/Projectionist/Hook.js?v=", body, StringComparison.Ordinal);
        Assert.True(body.IndexOf("Hook.js", StringComparison.Ordinal) < body.IndexOf("</body>", StringComparison.Ordinal));
        Assert.Equal(Encoding.UTF8.GetByteCount(body), ctx.Response.ContentLength);
    }

    [Fact]
    public async Task BrowserAcceptEncoding_IsRemovedBeforeJellyfinCompresses()
    {
        var ctx = NewContext("/web/", "gzip, deflate, br, zstd");
        string? seenByInner = "unset";
        await NewFilter().InjectAsync(ctx, async () =>
        {
            seenByInner = ctx.Request.Headers["Accept-Encoding"].ToString();
            ctx.Response.ContentType = "text/html";
            await ctx.Response.WriteAsync(Page);
        });

        Assert.Equal(string.Empty, seenByInner);
        Assert.Contains("Plugins/Projectionist/Hook.js", ReadBody(ctx), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompressedBody_IsPassedThroughByteForByte()
    {
        byte[] gz;
        using (var ms = new MemoryStream())
        {
            using (var z = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                z.Write(Encoding.UTF8.GetBytes(Page));
            }
            gz = ms.ToArray();
        }

        var ctx = NewContext("/web/index.html", "gzip");
        await NewFilter().InjectAsync(ctx, async () =>
        {
            ctx.Response.ContentType = "text/html";
            ctx.Response.Headers["Content-Encoding"] = "gzip";
            await ctx.Response.Body.WriteAsync(gz);
        });

        ctx.Response.Body.Position = 0;
        var sent = ((MemoryStream)ctx.Response.Body).ToArray();
        Assert.Equal(gz, sent);

        // And it still decodes, which is exactly what #7 broke.
        using var decoded = new GZipStream(new MemoryStream(sent), CompressionMode.Decompress);
        Assert.Equal(Page, new StreamReader(decoded).ReadToEnd());
    }

    [Theory]
    [InlineData("/web/configurationpage")]
    [InlineData("/Items/abc")]
    [InlineData("/web/main.bundle.js")]
    public async Task NonIndexRequests_AreUntouched(string path)
    {
        var ctx = NewContext(path, "gzip");
        await NewFilter().InjectAsync(ctx, async () =>
        {
            Assert.Equal("gzip", ctx.Request.Headers["Accept-Encoding"].ToString());
            ctx.Response.ContentType = "text/html";
            await ctx.Response.WriteAsync(Page);
        });

        Assert.Equal(Page, ReadBody(ctx));
    }

    [Fact]
    public async Task AlreadyInjectedPage_IsNotInjectedTwice()
    {
        var ctx = NewContext("/web/", null);
        var once = Page.Replace("</body>", "<script src=\"/Plugins/Projectionist/Hook.js?v=x\" defer></script></body>", StringComparison.Ordinal);
        await NewFilter().InjectAsync(ctx, async () =>
        {
            ctx.Response.ContentType = "text/html";
            await ctx.Response.WriteAsync(once);
        });

        Assert.Equal(once, ReadBody(ctx));
    }
}
