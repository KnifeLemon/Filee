// A portable copy with an unwritable data folder shows an error without loading settings or logging elsewhere. The
// message also goes to stderr, with the cause in English, since the log lives in that same folder (#1590).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Filee.App.Services;
using Filee.Core.Settings;

namespace Filee.App;

internal static class PortableStartupFailure
{
    public static int Show(string message, PortableStorageException error)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(Cause(error));
        if (OperatingSystem.IsWindows())
            Filee.Platform.Windows.Elevation.ShowWarning(message);
        else
        {
            try
            {
                // Filee's own fonts, as in the app: without them Avalonia drew Chinese and other scripts the system's
                // default font lacks as empty boxes (#1590).
                AppBuilder.Configure(() => new FailureApplication(message)).UsePlatformDetect()
                    .ConfigureFonts(fonts =>
                    {
                        fonts.AddFontCollection(new FileeFontCollection());
                        fonts.AddFontCollection(new SystemUiFontCollection());
                    })
                    .StartWithClassicDesktopLifetime([], ShutdownMode.OnMainWindowClose);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // stderr already contains the storage error; an SSH/headless session may have no display.
                Console.Error.WriteLine(ex.Message);
            }
        }
        return 1;
    }

    /// <summary>
    /// The line for stderr after the message: the folder, why it failed, and the marker that asked for it, in English
    /// for a bug report or a packager's terminal.
    /// </summary>
    internal static string Cause(PortableStorageException error) =>
        $"Filee: {error.Message} ({error.InnerException?.Message ?? "unknown error"}). The {PortableMode.MarkerName} file " +
        "beside Filee turns on portable mode; delete it to use the normal user profile. Installed packages must not include it.";

    /// <summary>The message window, in the UI font chain of <paramref name="language"/> and its reading direction.</summary>
    internal static Window CreateWindow(string message, string language)
    {
        var close = new Button { Content = LocalizationService.EarlyText("common.close"), HorizontalAlignment = HorizontalAlignment.Right };
        var window = new Window
        {
            Title = "Filee",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            FontFamily = SoftPalette.FontFamilyFor(language),
            FlowDirection = SoftPalette.IsRightToLeft(language) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 18,
                Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, close },
            },
        };
        close.Click += (_, _) => window.Close();
        return window;
    }

    private sealed class FailureApplication(string message) : Application
    {
        public override void Initialize() => Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.MainWindow = CreateWindow(message, LocalizationService.Resolve("auto"));
            base.OnFrameworkInitializationCompleted();
        }
    }
}
