// Entry point. Order matters:
//  1. Velopack hooks (install/update/uninstall) must run before anything else.
//  2. Single instance: a second launch (e.g. from the Explorer context menu) forwards its arguments
//     to the running instance and exits.
//  3. Start Avalonia. The app lives in the tray, so it only exits via the tray "Quit" command.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Filee.App.Services;
using Velopack;

namespace Filee.App;

internal static class Program
{
    /// <summary>Parsed command line of this process.</summary>
    public static CommandLine Options { get; private set; } = CommandLine.Parse([]);

    /// <summary>Owned by the first instance for its whole lifetime.</summary>
    public static SingleInstance? Instance { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        var velopack = VelopackApp.Build();
        if (OperatingSystem.IsWindows())
            velopack = velopack.OnBeforeUninstallFastCallback(_ => UninstallCleanup.Run());
        velopack.Run();

        Options = CommandLine.Parse(args);
        Instance = SingleInstance.Acquire();
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
        finally
        {
            Instance.Dispose();
        }
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
