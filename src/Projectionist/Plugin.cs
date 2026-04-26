using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Projectionist.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Projectionist;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "Projectionist";

    public override Guid Id => Guid.Parse("a51f9a52-7c3b-4b89-9f10-4c8e2b3d1a5e");

    public override string Description =>
        "Plays preroll videos before movies and episodes. Folder-based source — no library required. " +
        "Episode-aware. Custom admin UI.";

    public static Plugin? Instance { get; private set; }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configurationPage.html",
        };
    }
}
