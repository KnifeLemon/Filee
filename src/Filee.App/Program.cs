// Entry point. Order matters:
//  1. Single instance: a second launch (e.g. from the Explorer context menu) forwards its arguments
//     to the running instance and exits.
//  2. Installer commands (--quit, --uninstall-cleanup) run without any window.
//  3. Start Avalonia. The app lives in the tray, so it only exits via the tray "Quit" command (or --quit).

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Filee.App.Services;

namespace Filee.App;

internal static class Program
{
    /// <summary>Parsed command line of this process.</summary>
    public static CommandLine Options { get; private set; } = CommandLine.Parse([]);

    /// <summary>Owned by the first instance for its whole lifetime.</summary>
    public static SingleInstance? Instance { get; private set; }

    /// <summary>How long the installer waits for a running Filee to save and exit.</summary>
    private static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(20);

    [STAThread]
    public static int Main(string[] args)
    {
        Options = CommandLine.Parse(args);
        Instance = SingleInstance.Acquire();
        if (Options.Quit || Options.UninstallCleanup)
            return RunInstallerCommand(Instance);
        if (!Instance.IsFirst)
        {
            Instance.ForwardToFirst(args);
            Instance.Dispose();
            return 0;
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        catch (InvalidOperationException ex) when (OperatingSystem.IsMacOS() && ex.Message.Contains("RenderTimer", StringComparison.Ordinal))
        {
            // The window system has no display link for this screen: a Mac in a virtual machine without graphics
            // acceleration (VMware). Avalonia can't draw there; say so instead of quitting without a word.
            StartupFailure.Report(ex, "Filee can't open its window on this Mac: macOS reports no display it can draw on. " +
                                      "This happens in virtual machines without graphics acceleration (such as VMware). " +
                                      "The filee command still works in Terminal.");
            return 1;
        }
        finally
        {
            Instance.Dispose();
        }
    }

    /// <summary>
    /// <c>--quit</c> and <c>--uninstall-cleanup</c> from the installer: ask the running Filee (if any) to exit and wait
    /// for it, so its files can be replaced or removed; then clean up for the uninstaller. Exit code 1 when Filee is
    /// still running.
    /// </summary>
    private static int RunInstallerCommand(SingleInstance instance)
    {
        using (instance)
        {
            var deadline = DateTime.UtcNow + QuitTimeout;
            if (!instance.IsFirst)
            {
                instance.ForwardToFirst(["--quit"]);
                if (!instance.WaitForFirstToExit(QuitTimeout))
                    return 1;
            }
            // The mutex is released just before the process ends: wait for the end, so Filee.exe is no longer in use.
            if (!WaitForOtherProcessesOfThisExe(deadline))
                return 1;
            if (Options.UninstallCleanup)
                return UninstallCleanup.Run() ? 0 : 1;
            return 0;
        }
    }

    private static bool WaitForOtherProcessesOfThisExe(DateTime deadline)
    {
        if (Environment.ProcessPath is not { } exe)
            return true;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId
                        || !string.Equals(process.MainModule?.FileName, exe, Filee.Core.Platform.FileSystemPaths.Comparison))
                        continue;
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero || !process.WaitForExit(left))
                        return false;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Exited meanwhile, or another user's process we may not inspect.
                }
            }
        }
        return true;
    }

    /// <summary>Also used by the XAML previewer.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .ConfigureFonts(fonts => fonts.AddFontCollection(new FileeFontCollection()))
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();
}

/// <summary>Embedded Noto fonts: <c>fonts:Filee#Noto Sans</c>, <c>#Noto Sans KR</c>, <c>#Noto Sans SC</c>.</summary>
internal sealed class FileeFontCollection() : Avalonia.Media.Fonts.EmbeddedFontCollection(
    new Uri("fonts:Filee", UriKind.Absolute),
    new Uri("avares://Filee/Assets/Fonts", UriKind.Absolute));
