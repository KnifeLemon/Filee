// Runs from the Velopack uninstall hook: removes registry entries, the Windows 11 Explorer menu package and other
// integration Filee created for the current user.

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
            // Deletes the classic verb, Send To and the title file (which already hides the top-level entry).
            platform.SetContextMenu(false, "", "");
            platform.SetStartWithSystem(false, "");
            // Removing the package needs no administrator rights; there is no UI for a UAC prompt here, and Velopack
            // gives the hook about a minute.
            if (ExplorerMenuRegistration.IsSupportedOs)
                ExplorerMenuRegistration.RemoveAsync(allowElevation: false).Wait(TimeSpan.FromSeconds(40));
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
