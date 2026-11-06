// A portable copy with an unwritable data folder shows an error without loading settings or logging elsewhere.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;

namespace Filee.App;

internal static class PortableStartupFailure
{
    public static int Show(string message)
    {
        Console.Error.WriteLine(message);
        if (OperatingSystem.IsWindows())
            Filee.Platform.Windows.Elevation.ShowWarning(message);
        else
            AppBuilder.Configure(() => new FailureApplication(message)).UsePlatformDetect()
                .StartWithClassicDesktopLifetime([], ShutdownMode.OnMainWindowClose);
        return 1;
    }

    private sealed class FailureApplication(string message) : Application
    {
        public override void Initialize() => Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var close = new Button { Content = Services.LocalizationService.EarlyText("common.close"), HorizontalAlignment = HorizontalAlignment.Right };
                var window = new Window
                {
                    Title = "Filee",
                    Width = 520,
                    SizeToContent = SizeToContent.Height,
                    CanResize = false,
                    Content = new StackPanel
                    {
                        Margin = new Thickness(24),
                        Spacing = 18,
                        Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, close },
                    },
                };
                close.Click += (_, _) => window.Close();
                desktop.MainWindow = window;
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
