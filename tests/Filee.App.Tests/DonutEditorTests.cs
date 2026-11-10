// Settings → Donut toolbar as an editor (#873): the sliders save the donut's look without applying every setting again.

using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.App.Views.Pages;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class DonutEditorTests
{
    internal static (MainWindow Window, ToolbarPage Page, ToolbarPageViewModel Vm) Open(double width = 1280, double height = 900)
    {
        TestServices.EnsureInitialized("en");
        var main = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>(), AppHost.Get<UserDataStore>());
        main.Navigate("toolbar");
        var window = new MainWindow { DataContext = main, Width = width, Height = height };
        // As WindowService does in the app: a page left open would go on reacting to every library change after the test.
        window.Closed += (_, _) => main.Dispose();
        window.Show();
        RenderTests.Pump();
        var page = window.GetVisualDescendants().OfType<ToolbarPage>().Single();
        return (window, page, (ToolbarPageViewModel)page.DataContext!);
    }

    [AvaloniaFact]
    public void The_look_sliders_save_quietly_on_this_page_and_on_the_theme_page()
    {
        var (window, _, vm) = Open();
        var store = AppHost.Get<UserDataStore>();
        var saved = (store.Settings.Donut.OuterRadius, store.Settings.Donut.Opacity, store.Settings.Donut.GroupRingSize);
        var applied = 0;
        EventHandler count = (_, _) => applied++;
        store.SettingsChanged += count;
        try
        {
            foreach (var value in new[] { 150.4, 171.0, 188.6 })
                vm.OuterRadius = value;
            vm.SliceOpacity = 70;
            vm.GroupRingSize = 125;
            RenderTests.Pump();
            // Written as it moves (the donut reads it when it opens), without the theme, triggers and integration that
            // a settings change applies again: that is what made the sliders stick.
            Assert.Equal(189, store.Settings.Donut.OuterRadius);
            Assert.Contains("\"outerRadius\": 189", File.ReadAllText(Path.Combine(store.Directory, "settings.json")));
            Assert.Equal(0, applied);

            ((MainWindowViewModel)window.DataContext!).Navigate("theme");
            RenderTests.Pump();
            var theme = Assert.IsType<ThemePageViewModel>(((MainWindowViewModel)window.DataContext!).CurrentPage);
            theme.DonutOpacity = 55;
            Assert.Equal(0.55, store.Settings.Donut.Opacity, 3);
            Assert.Contains("\"opacity\": 0.55", File.ReadAllText(Path.Combine(store.Directory, "settings.json")));
            Assert.Equal(0, applied);
        }
        finally
        {
            store.SettingsChanged -= count;
            (store.Settings.Donut.OuterRadius, store.Settings.Donut.Opacity, store.Settings.Donut.GroupRingSize) = saved;
            store.SaveDonutLook();
            window.Close();
        }
    }
}
