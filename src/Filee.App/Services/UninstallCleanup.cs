// Runs from the Velopack uninstall hook: removes registry entries Filee created for the current user.

using Filee.Platform.Windows;

namespace Filee.App.Services;

internal static class UninstallCleanup
{
    public static void Run()
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            var platform = new WindowsPlatformServices();
            platform.SetContextMenu(false, "", "");
            platform.SetStartWithSystem(false, "");
            // Engines downloaded later live outside the app folder (see EngineEnvironment.DownloadRoot).
            if (Directory.Exists(Filee.Engines.Infrastructure.EngineEnvironment.DownloadRoot))
                Directory.Delete(Filee.Engines.Infrastructure.EngineEnvironment.DownloadRoot, recursive: true);
        }
        catch (Exception)
        {
            // Uninstall must never fail because of cleanup.
        }
    }
}
