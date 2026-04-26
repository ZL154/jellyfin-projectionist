using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Projectionist.Services;

/// <summary>
/// On every plugin startup, re-applies the per-user hide for the Projectionist
/// Prerolls library. Catches the case where preferences were cleared (Jellyfin
/// upgrade, plugin reinstall, manual user policy edits) so the hidden library
/// stays hidden without the user having to click "Set up library" again.
/// </summary>
public sealed class HideOnStartupService : IHostedService
{
    private readonly ILogger<HideOnStartupService> _logger;
    private readonly HiddenLibraryManager _hiddenLibrary;

    public HideOnStartupService(
        ILogger<HideOnStartupService> logger,
        HiddenLibraryManager hiddenLibrary)
    {
        _logger = logger;
        _hiddenLibrary = hiddenLibrary;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            // Wait a short moment so user/library managers are fully initialized.
            try { await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false); }
            catch { return; }

            try
            {
                if (_hiddenLibrary.GetExisting() is null)
                {
                    _logger.LogDebug("[Projectionist] no managed library at startup, skipping auto-hide");
                    return;
                }
                await _hiddenLibrary.HideFromAllUsersAsync().ConfigureAwait(false);
                _logger.LogInformation("[Projectionist] auto-applied hide on startup");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Projectionist] startup auto-hide failed");
            }
        }, cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
