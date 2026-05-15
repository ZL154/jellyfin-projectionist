using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Projectionist.Web;

/// <summary>
/// Plugged into the ASP.NET Core pipeline via DI (IStartupFilter is auto-discovered).
/// Intercepts responses for Jellyfin's web client index.html and injects our
/// playback hook script tag. This is the same pattern used by Achievement Badges
/// and StarTrack on this server.
/// </summary>
public sealed class IndexHtmlInjectionFilter : IStartupFilter
{
    private readonly ILogger<IndexHtmlInjectionFilter> _logger;

    // Use a version query so browsers don't cache stale copies of the hook
    // when we ship updates.
    private static readonly string ScriptVersion =
        typeof(IndexHtmlInjectionFilter).Module.ModuleVersionId.ToString("N");
    private const string Marker = "Plugins/Projectionist/Hook.js";

    public IndexHtmlInjectionFilter(ILogger<IndexHtmlInjectionFilter> logger)
    {
        _logger = logger;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(InjectAsync);
            next(app);
        };
    }

    private async Task InjectAsync(HttpContext context, Func<Task> nextMiddleware)
    {
        if (!IsIndexHtmlRequest(context.Request))
        {
            await nextMiddleware().ConfigureAwait(false);
            return;
        }

        var originalBody = context.Response.Body;
        using var captured = new MemoryStream();
        context.Response.Body = captured;

        try
        {
            await nextMiddleware().ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        captured.Seek(0, SeekOrigin.Begin);
        var contentType = context.Response.ContentType ?? string.Empty;
        if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            await captured.CopyToAsync(originalBody).ConfigureAwait(false);
            return;
        }

        string html;
        using (var reader = new StreamReader(captured, Encoding.UTF8, leaveOpen: true))
        {
            html = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        if (string.IsNullOrEmpty(html) || html.Contains(Marker, StringComparison.Ordinal))
        {
            captured.Seek(0, SeekOrigin.Begin);
            await captured.CopyToAsync(originalBody).ConfigureAwait(false);
            return;
        }

        var modified = InjectScriptTag(html, BuildScriptTag(context.Request.PathBase));
        var bytes = Encoding.UTF8.GetBytes(modified);
        context.Response.ContentLength = bytes.Length;
        // Force fresh re-fetch so the injection lands in the browser even if it
        // had a cached pre-Projectionist copy.
        context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        context.Response.Headers["Pragma"] = "no-cache";
        context.Response.Headers["Expires"] = "0";
        await originalBody.WriteAsync(bytes).ConfigureAwait(false);
        _logger.LogInformation("[Projectionist] injected hook script into {Path}", context.Request.Path);
    }

    private static bool IsIndexHtmlRequest(HttpRequest req)
    {
        if (!HttpMethods.IsGet(req.Method)) return false;
        var path = req.Path.Value ?? string.Empty;
        // Match the common entry points for the web client.
        if (path.Equals("/", StringComparison.Ordinal)) return true;
        if (path.Equals("/web", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/web", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/web/", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string BuildScriptTag(PathString pathBase)
    {
        var prefix = pathBase.HasValue ? pathBase.Value!.TrimEnd('/') : string.Empty;
        return $"<script src=\"{prefix}/Plugins/Projectionist/Hook.js?v={ScriptVersion}\" defer></script>";
    }

    private static string InjectScriptTag(string html, string scriptTag)
    {
        var idx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return html + "\n" + scriptTag;
        return html.Substring(0, idx) + scriptTag + html.Substring(idx);
    }
}
