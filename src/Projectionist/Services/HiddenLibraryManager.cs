using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Projectionist.Services;

/// <summary>
/// Owns a hidden internal Jellyfin library called "Projectionist Prerolls".
/// Created on demand at the user's configured folder path. Hidden from every
/// user via UserPolicy.BlockedMediaFolders so it never shows up on home screens
/// or search, but the LibraryManager can still query its items so the player
/// can resolve MediaSourceInfo (which is what makes the preroll actually stream).
/// </summary>
public sealed class HiddenLibraryManager
{
    public const string LibraryName = "Projectionist Prerolls";

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<HiddenLibraryManager> _logger;
    private readonly object _lock = new();

    public HiddenLibraryManager(
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<HiddenLibraryManager> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>
    /// Returns the VirtualFolderInfo for our managed library if it currently
    /// exists, else null.
    /// </summary>
    public VirtualFolderInfo? GetExisting()
    {
        try
        {
            return _libraryManager.GetVirtualFolders()
                .FirstOrDefault(vf => string.Equals(vf.Name, LibraryName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Projectionist] could not enumerate virtual folders");
            return null;
        }
    }

    public LibraryStatus GetStatus(string? configuredFolder)
    {
        var existing = GetExisting();
        var hasFolder = !string.IsNullOrWhiteSpace(configuredFolder) && Directory.Exists(configuredFolder);
        return new LibraryStatus
        {
            LibraryName = LibraryName,
            LibraryExists = existing is not null,
            LibraryLocations = existing?.Locations?.ToList() ?? new List<string>(),
            ConfiguredFolder = configuredFolder ?? string.Empty,
            ConfiguredFolderExists = hasFolder,
            LibraryMatchesFolder = existing is not null && hasFolder &&
                (existing.Locations ?? Array.Empty<string>())
                    .Any(loc => PathsEqual(loc, configuredFolder!)),
        };
    }

    /// <summary>
    /// Ensure the hidden library exists and points at the supplied folder.
    /// Creates / removes / recreates as needed. Triggers a library scan after
    /// any change. Re-applies the user-policy hiding for every user.
    /// </summary>
    public async Task EnsureAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            // No folder configured — remove our library if we have one
            await RemoveIfExistsAsync();
            return;
        }

        if (!Directory.Exists(folderPath))
        {
            _logger.LogWarning("[Projectionist] cannot create hidden library — folder does not exist: {Folder}", folderPath);
            return;
        }

        var existing = GetExisting();
        if (existing is not null)
        {
            var locations = existing.Locations ?? Array.Empty<string>();
            if (locations.Length == 1 && PathsEqual(locations[0], folderPath))
            {
                _logger.LogInformation("[Projectionist] hidden library already correct at {Folder}", folderPath);
                await HideFromAllUsersAsync();
                return;
            }
            _logger.LogInformation("[Projectionist] hidden library exists at wrong path, recreating");
            try
            {
                await _libraryManager.RemoveVirtualFolder(LibraryName, refreshLibrary: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Projectionist] failed to remove old hidden library");
            }
        }

        var options = BuildLibraryOptions(folderPath);
        try
        {
            _logger.LogInformation("[Projectionist] creating hidden library at {Folder}", folderPath);
            await _libraryManager.AddVirtualFolder(
                LibraryName,
                CollectionTypeOptions.movies,
                options,
                refreshLibrary: true);
            _logger.LogInformation("[Projectionist] hidden library created");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Projectionist] failed to create hidden library at {Folder}", folderPath);
            return;
        }

        await HideFromAllUsersAsync();
    }

    public async Task RemoveIfExistsAsync()
    {
        var existing = GetExisting();
        if (existing is null) return;
        try
        {
            await _libraryManager.RemoveVirtualFolder(LibraryName, refreshLibrary: false);
            _logger.LogInformation("[Projectionist] removed hidden library");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Projectionist] failed to remove hidden library");
        }
    }

    /// <summary>
    /// Find the BaseItem in our hidden library matching the given absolute path.
    /// </summary>
    public BaseItem? FindItem(string fullPath)
    {
        var folder = GetLibraryRootFolder();
        if (folder is null) return null;

        var fileName = Path.GetFileName(fullPath);
        try
        {
            var children = folder.GetRecursiveChildren(c => c?.Path is not null);
            if (children is null) return null;
            foreach (var c in children)
            {
                if (string.Equals(c.Path, fullPath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(c.Path), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return c;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Projectionist] error walking hidden library children");
        }
        return null;
    }

    public Folder? GetLibraryRootFolder()
    {
        var existing = GetExisting();
        if (existing is null) return null;
        if (!Guid.TryParse(existing.ItemId, out var id)) return null;
        return _libraryManager.GetItemById(id) as Folder;
    }

    /// <summary>
    /// Re-apply BlockedMediaFolders + remove from OrderedViews for every user so the
    /// library stays fully hidden. We use the User entity's preference helpers and call
    /// UpdateUserAsync (the documented path). UpdatePolicyAsync was tried first but
    /// silently fails to persist BlockedMediaFolders.
    /// </summary>
    public async Task HideFromAllUsersAsync()
    {
        var existing = GetExisting();
        if (existing is null) return;
        if (!Guid.TryParse(existing.ItemId, out var folderId)) return;

        foreach (var user in _userManager.Users.ToList())
        {
            if (user is null) continue;
            try
            {
                var changed = false;

                // CRITICAL: do NOT add to BlockedMediaFolders. Doing so prevents the
                // user from accessing items in this library, which means the player
                // can't fetch MediaSourceInfo for our prerolls and playback fails
                // with "Unable to find a valid media source to play".
                // We only hide via the per-user home/section excludes below.
                //
                // Cleanup: if a previous version of this plugin added our folder to
                // BlockedMediaFolders, remove it so playback starts working again.
                var blocked = user.GetPreferenceValues<Guid>(PreferenceKind.BlockedMediaFolders).ToList();
                var unblocked = blocked.RemoveAll(g => g == folderId);
                if (unblocked > 0)
                {
                    user.SetPreference(PreferenceKind.BlockedMediaFolders, blocked.ToArray());
                    changed = true;
                }

                // 1. OrderedViews — strip in case it was added (controls library tile order on home)
                var ordered = user.GetPreferenceValues<Guid>(PreferenceKind.OrderedViews).ToList();
                if (ordered.RemoveAll(g => g == folderId) > 0)
                {
                    user.SetPreference(PreferenceKind.OrderedViews, ordered.ToArray());
                    changed = true;
                }

                // 2. GroupedFolders — strip in case it was auto-grouped
                var grouped = user.GetPreferenceValues<Guid>(PreferenceKind.GroupedFolders).ToList();
                if (grouped.RemoveAll(g => g == folderId) > 0)
                {
                    user.SetPreference(PreferenceKind.GroupedFolders, grouped.ToArray());
                    changed = true;
                }

                // 3. LatestItemExcludes — keep our prerolls out of "Latest" rows
                var latest = user.GetPreferenceValues<Guid>(PreferenceKind.LatestItemExcludes).ToList();
                if (!latest.Contains(folderId))
                {
                    latest.Add(folderId);
                    user.SetPreference(PreferenceKind.LatestItemExcludes, latest.ToArray());
                    changed = true;
                }

                // 4. MyMediaExcludes — exclude from "My Media" home tile section
                var myMedia = user.GetPreferenceValues<Guid>(PreferenceKind.MyMediaExcludes).ToList();
                if (!myMedia.Contains(folderId))
                {
                    myMedia.Add(folderId);
                    user.SetPreference(PreferenceKind.MyMediaExcludes, myMedia.ToArray());
                    changed = true;
                }

                if (changed)
                {
                    await _userManager.UpdateUserAsync(user);
                    _logger.LogInformation("[Projectionist] hid library from user {User}", user.Username);
                }
                else
                {
                    _logger.LogDebug("[Projectionist] library already hidden from user {User}", user.Username);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Projectionist] could not hide library from user {User}", user?.Username);
            }
        }
    }

    private static LibraryOptions BuildLibraryOptions(string folderPath) => new()
    {
        Enabled = true,
        EnableRealtimeMonitor = false,
        EnableChapterImageExtraction = false,
        ExtractChapterImagesDuringLibraryScan = false,
        EnableTrickplayImageExtraction = false,
        ExtractTrickplayImagesDuringLibraryScan = false,
        EnableLUFSScan = false,
        EnableInternetProviders = false,
        EnableAutomaticSeriesGrouping = false,
        EnableEmbeddedTitles = false,
        EnableEmbeddedExtrasTitles = false,
        EnableEmbeddedEpisodeInfos = false,
        EnablePhotos = false,
        SaveLocalMetadata = false,
        AutomaticRefreshIntervalDays = 0,
        PathInfos = new[] { new MediaPathInfo(folderPath) },
        DisabledLocalMetadataReaders = Array.Empty<string>(),
        DisabledSubtitleFetchers = Array.Empty<string>(),
        SubtitleFetcherOrder = Array.Empty<string>(),
        DisabledMediaSegmentProviders = Array.Empty<string>(),
        MediaSegmentProviderOrder = Array.Empty<string>(),
    };

    private static bool PathsEqual(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        var na = a.Replace('\\', '/').TrimEnd('/');
        var nb = b.Replace('\\', '/').TrimEnd('/');
        return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class LibraryStatus
{
    public string LibraryName { get; set; } = string.Empty;
    public bool LibraryExists { get; set; }
    public List<string> LibraryLocations { get; set; } = new();
    public string ConfiguredFolder { get; set; } = string.Empty;
    public bool ConfiguredFolderExists { get; set; }
    public bool LibraryMatchesFolder { get; set; }
}
