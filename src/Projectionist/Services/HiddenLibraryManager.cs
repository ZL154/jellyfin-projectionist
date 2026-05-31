using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Projectionist.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;
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
    private readonly IDbContextFactory<JellyfinDbContext> _dbFactory;
    private readonly ILogger<HiddenLibraryManager> _logger;
    public HiddenLibraryManager(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IDbContextFactory<JellyfinDbContext> dbFactory,
        ILogger<HiddenLibraryManager> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _dbFactory = dbFactory;
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
        return GetStatus(new PluginConfiguration
        {
            PrerollFolderPath = configuredFolder ?? string.Empty,
        });
    }

    public LibraryStatus GetStatus(PluginConfiguration config)
    {
        var existing = GetExisting();
        var configuredFolders = ResolveConfiguredFolders(config);
        var existingLocations = existing?.Locations?.ToList() ?? new List<string>();
        var hasFolders = configuredFolders.Count > 0 && configuredFolders.All(Directory.Exists);
        return new LibraryStatus
        {
            LibraryName = LibraryName,
            LibraryExists = existing is not null,
            LibraryLocations = existingLocations,
            ConfiguredFolder = !string.IsNullOrWhiteSpace(config.PrerollFolderPath)
                ? config.PrerollFolderPath
                : configuredFolders.FirstOrDefault() ?? string.Empty,
            ConfiguredFolders = configuredFolders,
            ConfiguredFolderExists = hasFolders,
            LibraryMatchesFolder = existing is not null && hasFolders &&
                LocationsMatch(existingLocations, configuredFolders),
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

    public async Task EnsureAsync(PluginConfiguration config)
    {
        var configuredFolders = ResolveConfiguredFolders(config);
        if (configuredFolders.Count == 0)
        {
            await RemoveIfExistsAsync();
            return;
        }

        var existingFolders = configuredFolders
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (existingFolders.Count == 0)
        {
            _logger.LogWarning(
                "[Projectionist] cannot create hidden library: no configured folder exists: {Folders}",
                string.Join(", ", configuredFolders));
            return;
        }

        foreach (var missing in configuredFolders.Where(f => !Directory.Exists(f)))
        {
            _logger.LogWarning("[Projectionist] skipping missing preroll folder: {Folder}", missing);
        }

        var existing = GetExisting();
        if (existing is not null)
        {
            var locations = existing.Locations ?? Array.Empty<string>();
            if (LocationsMatch(locations, existingFolders))
            {
                _logger.LogInformation(
                    "[Projectionist] hidden library already correct at {Folders}",
                    string.Join(", ", existingFolders));
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

        var options = BuildLibraryOptions(existingFolders);
        try
        {
            _logger.LogInformation(
                "[Projectionist] creating hidden library at {Folders}",
                string.Join(", ", existingFolders));
            await _libraryManager.AddVirtualFolder(
                LibraryName,
                CollectionTypeOptions.movies,
                options,
                refreshLibrary: true);
            _logger.LogInformation("[Projectionist] hidden library created");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[Projectionist] failed to create hidden library at {Folders}",
                string.Join(", ", existingFolders));
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
                if (PathsEqual(c.Path, fullPath) ||
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

    public bool IsManagedItem(BaseItem item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Path))
        {
            return false;
        }

        var existing = GetExisting();
        var locations = existing?.Locations ?? Array.Empty<string>();
        return locations.Any(loc => PathIsUnder(item.Path, loc));
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

        // Critical: User entities returned by IUserManager are detached from the
        // DbContext that ultimately persists, so SetPreference + UpdateUserAsync
        // doesn't actually save the new Preferences. We use the DbContext directly.
        var userIds = _userManager.EnumerateAll().Select(u => u.Id).ToList();
        await using var ctx = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        var savedAny = false;

        foreach (var userId in userIds)
        {
            try
            {
                var user = await ctx.Users
                    .Include(u => u.Preferences)
                    .FirstOrDefaultAsync(u => u.Id == userId)
                    .ConfigureAwait(false);
                if (user is null) continue;

                var changed = false;

                // Strip from BlockedMediaFolders if present — adding here would
                // prevent the user from streaming items in our library, which
                // breaks playback with "Unable to find a valid media source".
                changed |= RemoveValueFromPreference(user, PreferenceKind.BlockedMediaFolders, folderId);
                // Users with an EnabledFolders allow-list need this hidden
                // library included there, otherwise Jellyfin refuses to build
                // a playable media source for prerolls.
                changed |= AddValueToExistingPreference(user, PreferenceKind.EnabledFolders, folderId);
                // Strip from OrderedViews and GroupedFolders so the library tile
                // doesn't show up in the user's home / sidebar layout.
                changed |= RemoveValueFromPreference(user, PreferenceKind.OrderedViews, folderId);
                changed |= RemoveValueFromPreference(user, PreferenceKind.GroupedFolders, folderId);
                // Add to the "exclude from latest" + "exclude from My Media" sets.
                changed |= AddValueToPreference(user, PreferenceKind.LatestItemExcludes, folderId);
                changed |= AddValueToPreference(user, PreferenceKind.MyMediaExcludes, folderId);

                if (changed)
                {
                    savedAny = true;
                    _logger.LogInformation("[Projectionist] hid library from user {User}", user.Username);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Projectionist] could not hide library from user id {UserId}", userId);
            }
        }

        if (savedAny)
        {
            try
            {
                await ctx.SaveChangesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Projectionist] SaveChangesAsync failed when persisting hide preferences");
            }
        }
    }

    private static bool AddValueToPreference(User user, PreferenceKind kind, Guid value)
    {
        var pref = user.Preferences.FirstOrDefault(p => p.Kind == kind);
        var current = (pref?.Value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
        var asString = value.ToString();
        if (current.Contains(asString, StringComparer.OrdinalIgnoreCase)) return false;
        current.Add(asString);
        var joined = string.Join(",", current);
        if (pref is null)
        {
            user.Preferences.Add(new Preference(kind, joined));
        }
        else
        {
            pref.Value = joined;
        }
        return true;
    }

    private static bool AddValueToExistingPreference(User user, PreferenceKind kind, Guid value)
    {
        var pref = user.Preferences.FirstOrDefault(p => p.Kind == kind);
        if (pref is null || string.IsNullOrWhiteSpace(pref.Value)) return false;
        var current = pref.Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
        var asString = value.ToString();
        if (current.Contains(asString, StringComparer.OrdinalIgnoreCase)) return false;
        current.Add(asString);
        pref.Value = string.Join(",", current);
        return true;
    }

    private static bool RemoveValueFromPreference(User user, PreferenceKind kind, Guid value)
    {
        var pref = user.Preferences.FirstOrDefault(p => p.Kind == kind);
        if (pref is null) return false;
        var current = (pref.Value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
        var asString = value.ToString();
        var removed = current.RemoveAll(s => string.Equals(s, asString, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return false;
        pref.Value = string.Join(",", current);
        return true;
    }

    private static LibraryOptions BuildLibraryOptions(string folderPath) =>
        BuildLibraryOptions(new[] { folderPath });

    private static LibraryOptions BuildLibraryOptions(IReadOnlyList<string> folderPaths) => new()
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
        PathInfos = folderPaths.Select(p => new MediaPathInfo(p)).ToArray(),
        DisabledLocalMetadataReaders = Array.Empty<string>(),
        DisabledSubtitleFetchers = Array.Empty<string>(),
        SubtitleFetcherOrder = Array.Empty<string>(),
        DisabledMediaSegmentProviders = Array.Empty<string>(),
        MediaSegmentProviderOrder = Array.Empty<string>(),
    };

    private static List<string> ResolveConfiguredFolders(PluginConfiguration config)
    {
        return PrerollDiscoveryService.ResolveFolders(config)
            .Select(f => f.Path)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool LocationsMatch(IEnumerable<string> actual, IEnumerable<string> expected)
    {
        var actualSet = new HashSet<string>(
            actual.Where(p => !string.IsNullOrWhiteSpace(p)).Select(NormalizePath),
            StringComparer.OrdinalIgnoreCase);
        var expectedSet = new HashSet<string>(
            expected.Where(p => !string.IsNullOrWhiteSpace(p)).Select(NormalizePath),
            StringComparer.OrdinalIgnoreCase);
        return actualSet.SetEquals(expectedSet);
    }

    private static bool PathsEqual(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        return string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathIsUnder(string path, string parent)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(parent)) return false;
        var normalizedPath = NormalizePath(path);
        var normalizedParent = NormalizePath(parent);
        return string.Equals(normalizedPath, normalizedParent, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedParent + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').TrimEnd('/');
    }
}

public sealed class LibraryStatus
{
    public string LibraryName { get; set; } = string.Empty;
    public bool LibraryExists { get; set; }
    public List<string> LibraryLocations { get; set; } = new();
    public string ConfiguredFolder { get; set; } = string.Empty;
    public List<string> ConfiguredFolders { get; set; } = new();
    public bool ConfiguredFolderExists { get; set; }
    public bool LibraryMatchesFolder { get; set; }
}
