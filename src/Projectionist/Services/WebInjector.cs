using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Projectionist.Web;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Projectionist.Services;

/// <summary>
/// On plugin startup, asks the FileTransformation plugin (if installed) to inject
/// our playback hook script into Jellyfin's web client index.html. If
/// FileTransformation isn't installed, episodes won't get prerolls but everything
/// else still works — we just log a one-time warning.
/// </summary>
public sealed class WebInjector : IHostedService
{
    private readonly ILogger<WebInjector> _logger;
    private static readonly Guid TransformationId = new("ce4b7c7a-2b1f-4f0d-8e9d-2c44b3aa0f01");

    public WebInjector(ILogger<WebInjector> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Defer to background so we don't block startup if FT loads slowly.
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                if (cancellationToken.IsCancellationRequested) return;
                if (TryRegister()) return;
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            _logger.LogWarning(
                "[Projectionist] FileTransformation plugin not detected. " +
                "Episodes will not receive prerolls until you install it from the " +
                "unofficial plugin repo. Movies still work normally.");
        }, cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private bool TryRegister()
    {
        try
        {
            // Locate Jellyfin.Plugin.FileTransformation.PluginInterface in any loaded assembly.
            var ftAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a =>
                {
                    var name = a.GetName().Name;
                    return string.Equals(name, "Jellyfin.Plugin.FileTransformation",
                        StringComparison.OrdinalIgnoreCase);
                });
            if (ftAssembly is null)
            {
                return false;
            }

            var pluginInterface = ftAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
            var registerMethod = pluginInterface?.GetMethod(
                "RegisterTransformation",
                BindingFlags.Public | BindingFlags.Static);
            if (registerMethod is null)
            {
                _logger.LogWarning("[Projectionist] FileTransformation found but RegisterTransformation not present");
                return true; // give up, retrying won't help
            }

            // FileTransformation expects a Newtonsoft.Json.Linq.JObject. Build it via
            // reflection from the Newtonsoft.Json assembly already loaded by FT.
            var newtonsoftAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "Newtonsoft.Json",
                    StringComparison.OrdinalIgnoreCase));
            if (newtonsoftAssembly is null)
            {
                _logger.LogWarning("[Projectionist] Newtonsoft.Json assembly not loaded yet");
                return false;
            }

            var jObjectType = newtonsoftAssembly.GetType("Newtonsoft.Json.Linq.JObject");
            var parseMethod = jObjectType?.GetMethod("Parse", new[] { typeof(string) });
            if (parseMethod is null)
            {
                _logger.LogWarning("[Projectionist] could not resolve JObject.Parse");
                return true;
            }

            var assemblyName = typeof(IndexHtmlTransformer).Assembly.FullName ?? string.Empty;
            var className = typeof(IndexHtmlTransformer).FullName ?? string.Empty;

            var json = $@"{{
                ""id"": ""{TransformationId}"",
                ""fileNamePattern"": ""index\\.html"",
                ""callbackAssembly"": ""{assemblyName}"",
                ""callbackClass"": ""{className}"",
                ""callbackMethod"": ""Transform""
            }}";

            var jObj = parseMethod.Invoke(null, new object[] { json });
            if (jObj is null)
            {
                _logger.LogWarning("[Projectionist] JObject.Parse returned null");
                return true;
            }

            registerMethod.Invoke(null, new[] { jObj });
            _logger.LogInformation(
                "[Projectionist] registered index.html transformation with FileTransformation. " +
                "Episodes will now receive prerolls.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Projectionist] failed to register transformation");
            return true; // don't keep retrying after a hard error
        }
    }
}
