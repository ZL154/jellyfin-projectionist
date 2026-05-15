namespace Jellyfin.Plugin.Projectionist.Web;

/// <summary>
/// Static callback invoked by the FileTransformation plugin whenever Jellyfin's
/// web client requests index.html. We inject a script tag pointing at our
/// embedded playback-hook.js. The hook then patches playbackManager so episodes
/// also fetch /Items/{id}/Intros (which Jellyfin's vanilla web client only does
/// for movies).
/// </summary>
public static class IndexHtmlTransformer
{
    /// <summary>Shape FileTransformation hands us. Just `contents`.</summary>
    public sealed class Payload
    {
        public string? contents { get; set; }
    }

    private static readonly string ScriptVersion =
        typeof(IndexHtmlTransformer).Module.ModuleVersionId.ToString("N");
    private static readonly string ScriptTag =
        $"<script src=\"../Plugins/Projectionist/Hook.js?v={ScriptVersion}\" defer></script>";
    private const string Marker = "Plugins/Projectionist/Hook.js";

    public static string Transform(Payload payload)
    {
        var html = payload?.contents ?? string.Empty;
        if (string.IsNullOrEmpty(html) || html.Contains(Marker))
        {
            return html;
        }
        var idx = html.LastIndexOf("</body>", System.StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return html + "\n" + ScriptTag;
        }
        return html.Substring(0, idx) + ScriptTag + html.Substring(idx);
    }
}
