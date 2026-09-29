// In-app updates from GitHub Releases via Velopack. Only works in installed builds (not when run from the IDE).

using System.Reflection;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Filee.App.Services;

public sealed class UpdateService(ILogger<UpdateService> log)
{
    private UpdateManager? _manager;
    private UpdateInfo? _pending;

    /// <summary>GitHub repository from Directory.Build.props (FileeRepositoryUrl).</summary>
    public static string RepositoryUrl =>
        typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepositoryUrl")?.Value ?? "https://github.com/KnifeLemon/Filee";

    public static string CurrentVersion =>
        typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>False when running from source (no Velopack installation).</summary>
    public bool IsInstalled => Manager?.IsInstalled == true;

    private UpdateManager? Manager
    {
        get
        {
            try
            {
                return _manager ??= new UpdateManager(new GithubSource(RepositoryUrl, null, false));
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Update manager unavailable");
                return null;
            }
        }
    }

    /// <summary>Returns the new version string, or null if up to date / not installed / offline.</summary>
    public async Task<string?> CheckAsync()
    {
        if (!IsInstalled)
            return null;
        try
        {
            _pending = await Manager!.CheckForUpdatesAsync();
            return _pending?.TargetFullRelease.Version.ToString();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Update check failed");
            return null;
        }
    }

    /// <summary>Downloads the pending update and restarts into it.</summary>
    public async Task ApplyAsync()
    {
        if (_pending is null || Manager is null)
            return;
        await Manager.DownloadUpdatesAsync(_pending);
        Manager.ApplyUpdatesAndRestart(_pending);
    }
}
