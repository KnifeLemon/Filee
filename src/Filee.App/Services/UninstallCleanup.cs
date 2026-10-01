// Runs as "Filee.exe --uninstall-cleanup" from the uninstaller (installer/Filee.iss), after the running Filee exited:
// removes registry entries, the Windows 11 Explorer menu package, downloaded engines and other integration Filee
// created for the current user. Settings in %APPDATA%\Filee stay, so reinstalling brings them back.

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
            // The uninstaller runs with administrator rights already, so no UAC prompt can appear here.
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
