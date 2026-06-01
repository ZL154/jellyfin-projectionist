using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Projectionist.Configuration;

public enum SelectionMode
{
    Random = 0,
    Sequential = 1,
    Weighted = 2,
    EqualRotation = 3,
    RecencyBoost = 4,
}

public enum SessionMode
{
    EveryPlayback = 0,
    FirstOfSession = 1,
    OncePerDay = 2,
    FirstOfBinge = 3,
    OncePerHour = 4,
    OncePerSeriesPerDay = 5,
}

public enum UserMode
{
    AllUsers = 0,
    OnlyIncluded = 1,
    AllExceptExcluded = 2,
}

public enum FeaturePreloadMode
{
    Off = 0,
    Warm = 1,
    Hot = 2,
}

public sealed class PrerollFolder
{
    public string Path { get; set; } = string.Empty;
    /// <summary>Optional friendly name for the folder shown in UI.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Tags applied to every preroll under this folder unless its sidecar overrides.</summary>
    public List<string> DefaultTags { get; set; } = new();
}

public sealed class LibraryRule
{
    /// <summary>Jellyfin library name to match (case-insensitive). Empty matches any.</summary>
    public string LibraryName { get; set; } = string.Empty;
    /// <summary>Item type filter: Movie / Episode / MusicVideo (empty = any).</summary>
    public string ItemType { get; set; } = string.Empty;
    /// <summary>Tags required on the preroll to be eligible. Empty = any.</summary>
    public List<string> RequireTags { get; set; } = new();
    /// <summary>Tags that disqualify a preroll for this rule.</summary>
    public List<string> ExcludeTags { get; set; } = new();
    /// <summary>If non-empty, the feature must have at least one genre in this list. Case-insensitive.</summary>
    public List<string> MatchGenres { get; set; } = new();
    /// <summary>If true, no preroll plays before items matching this rule.</summary>
    public bool Disabled { get; set; }
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
    /// If true, movies/music videos and TV episodes use separate session modes.
    /// When false, the legacy SessionMode value is used for every content type.
    /// </summary>
    public bool UseSeparateSessionModes { get; set; } = false;

    /// <summary>Session behaviour for movies and music videos.</summary>
    public SessionMode MovieSessionMode { get; set; } = SessionMode.EveryPlayback;

    /// <summary>Session behaviour for TV episodes.</summary>
    public SessionMode EpisodeSessionMode { get; set; } = SessionMode.EveryPlayback;

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

    // ---------- Phase A: smart selection ----------

    /// <summary>
    /// If true, items being RESUMED (PlaybackPositionTicks > 0) skip the preroll.
    /// Default true — replaying from a resume point shouldn't trigger a brand intro.
    /// </summary>
    public bool SkipOnResume { get; set; } = true;

    /// <summary>
    /// Per-feature cooldown. Don't replay any preroll for the same feature within
    /// this many hours. 0 disables.
    /// </summary>
    public int CooldownHoursPerItem { get; set; } = 12;

    /// <summary>
    /// If true, refuse to play a preroll whose maturity rating exceeds the feature's.
    /// Stops the edgy preroll from playing before a kid's movie.
    /// </summary>
    public bool MaturityGated { get; set; } = true;

    /// <summary>How user inclusion/exclusion is interpreted.</summary>
    public UserMode UserMode { get; set; } = UserMode.AllUsers;

    /// <summary>User IDs that match the UserMode rule.</summary>
    public List<Guid> UserIds { get; set; } = new();

    // ---------- Phase B: multiple folders + rules ----------

    /// <summary>
    /// Additional preroll folders beyond the legacy single folder. Each can have
    /// its own default tags so library rules can target them.
    /// If empty, the legacy <see cref="PrerollFolderPath"/> is used as a single folder.
    /// </summary>
    public List<PrerollFolder> Folders { get; set; } = new();

    /// <summary>
    /// Per-library / per-content-type rules. First matching rule wins.
    /// </summary>
    public List<LibraryRule> LibraryRules { get; set; } = new();

    // ---------- Phase C: stats + telemetry ----------

    /// <summary>If true, write per-file play counts + timestamps to plugin data dir.</summary>
    public bool EnableStatsTracking { get; set; } = true;

    // ---------- Phase D: trailers + per-series ----------

    /// <summary>
    /// If true, also pull from the feature's local trailers (when available)
    /// instead of / in addition to the configured preroll folders.
    /// </summary>
    public bool EnableTrailerMode { get; set; } = false;

    /// <summary>
    /// Number of trailers (in addition to <see cref="PrerollCount"/>) to chain
    /// before the feature when trailer mode is on. 0 = trailer mode off.
    /// </summary>
    public int TrailerCount { get; set; } = 0;

    /// <summary>
    /// If true, look for a `theme-preroll.mp4` (or any video file) in the series'
    /// folder and use it as the preroll for episodes of that series, overriding
    /// the global selection.
    /// </summary>
    public bool EnableSeriesPrerolls { get; set; } = true;

    /// <summary>The filename (without extension) to look for in series folders.</summary>
    public string SeriesPrerollFileName { get; set; } = "theme-preroll";

    /// <summary>If true, allow the user to skip the preroll via a button in the player.</summary>
    public bool EnableSkippablePrerolls { get; set; } = true;

    /// <summary>Min seconds the preroll must play before skip is allowed. 0 = always.</summary>
    public int SkippableAfterSeconds { get; set; } = 0;

    /// <summary>
    /// If true, the web-client hook asks Jellyfin to prepare playback info for the
    /// feature while Projectionist prerolls are running. This is best-effort and
    /// avoids opening a second media stream.
    /// </summary>
    public bool EnableFeaturePreload { get; set; } = false;

    /// <summary>
    /// Web-client preload behavior while prerolls run. Warm only prepares playback
    /// info; Hot also opens a small early stream request where Jellyfin exposes one.
    /// </summary>
    public FeaturePreloadMode FeaturePreloadMode { get; set; } = FeaturePreloadMode.Off;

    // ---------- v1.2.0 features ----------

    /// <summary>Item IDs that should NEVER trigger a preroll.</summary>
    public List<Guid> OptedOutFeatureIds { get; set; } = new();

    /// <summary>Play one or more "coming soon" trailers for an unwatched library item before the feature.</summary>
    public bool EnableComingSoonTrailers { get; set; } = false;

    /// <summary>How many coming-soon trailers to prepend.</summary>
    public int ComingSoonTrailerCount { get; set; } = 1;

    /// <summary>Path to a folder of post-roll videos. Plays AFTER the feature ends.</summary>
    public string PostRollFolderPath { get; set; } = string.Empty;

    /// <summary>Number of post-roll videos to play. 0 = post-roll disabled.</summary>
    public int PostRollCount { get; set; } = 0;

    /// <summary>If true, run ffmpeg volumedetect on each preroll for the admin loudness report.</summary>
    public bool EnableLoudnessAnalysis { get; set; } = false;

    /// <summary>Threshold in dB. Prerolls whose mean dB drifts by more than this from baseline are flagged.</summary>
    public double LoudnessWarningThresholdDb { get; set; } = 6.0;
}
