using System;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Projectionist.Services;

/// <summary>
/// Hides the preroll library from accounts created while the server is
/// running. Hiding otherwise only happened at startup and on config save,
/// so a new user saw "Projectionist Prerolls" in their libraries until the
/// next restart.
/// </summary>
public sealed class HideLibraryOnUserCreated : IEventConsumer<UserCreatedEventArgs>
{
    private readonly HiddenLibraryManager _hiddenLibrary;
    private readonly ILogger<HideLibraryOnUserCreated> _logger;

    public HideLibraryOnUserCreated(HiddenLibraryManager hiddenLibrary, ILogger<HideLibraryOnUserCreated> logger)
    {
        _hiddenLibrary = hiddenLibrary;
        _logger = logger;
    }

    public async Task OnEvent(UserCreatedEventArgs eventArgs)
    {
        try
        {
            await _hiddenLibrary.HideFromAllUsersAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Projectionist] could not hide the preroll library from a new user");
        }
    }
}
