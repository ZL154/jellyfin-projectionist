using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Projectionist.Configuration;

public enum SelectionMode
{
    Random = 0,
    Sequential = 1,
    Weighted = 2,
}

public enum SessionMode
{
    EveryPlayback = 0,
    FirstOfSession = 1,
    OncePerDay = 2,
}

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Absolute path to a folder containing preroll video files.
    /// No Jellyfin library required — this folder is scanned directly.
    /// </summary>
    public string PrerollFolderPath { get; set; } = string.Empty;

    /// <summary>Play prerolls before movies.</summary>
    public bool EnableForMovies { get; set; } = true;

    /// <summary>Play prerolls before TV episodes.</summary>
    public bool EnableForEpisodes { get; set; } = true;

    /// <summary>Play prerolls before music videos.</summary>
    public bool EnableForMusicVideos { get; set; } = false;

    /// <summary>How many prerolls to chain before each playback.</summary>
    public int PrerollCount { get; set; } = 1;

    /// <summary>How prerolls are picked when there are multiple files.</summary>
    public SelectionMode SelectionMode { get; set; } = SelectionMode.Random;

    /// <summary>Behaviour controlling how often the same session sees a preroll.</summary>
    public SessionMode SessionMode { get; set; } = SessionMode.EveryPlayback;

    /// <summary>
    /// Comma-separated list of file extensions to consider as preroll candidates.
    /// </summary>
    public string AllowedExtensions { get; set; } = ".mp4,.mkv,.mov,.webm,.avi,.m4v";

    /// <summary>
    /// If a runtime is below this threshold (in seconds), the preroll is skipped.
    /// Lets you flag tiny clips you don't want to play before a feature.
    /// </summary>
    public int MinPrerollDurationSeconds { get; set; } = 0;

    /// <summary>
    /// If a runtime is above this threshold (in seconds), the preroll is skipped.
    /// Default 60 seconds — anything longer is probably not a preroll.
    /// Set to 0 to disable.
    /// </summary>
    public int MaxPrerollDurationSeconds { get; set; } = 0;

    /// <summary>
    /// If enabled, the plugin will skip prerolls when the feature is below
    /// this runtime (seconds). Useful to avoid prerolling short clips/extras.
    /// </summary>
    public int MinFeatureRuntimeSeconds { get; set; } = 0;
}
