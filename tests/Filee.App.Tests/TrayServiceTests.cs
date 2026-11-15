// Tray visibility and the template icon follow the macOS menu bar preferences without changing other platforms.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Filee.App.Services;
using Filee.Core.Localization;
using Filee.Core.Settings;

namespace Filee.App.Tests;

[Collection("MainWindow")]
public sealed class TrayServiceTests
{
    [AvaloniaFact]
    public void Mac_tray_visibility_follows_preference_and_non_mac_tray_stays_visible()
    {
        TestServices.EnsureInitialized("en");
        var app = Application.Current!;
        var directory = Directory.CreateTempSubdirectory("filee-tray-").FullName;
        try
        {
            var store = new UserDataStore(directory);
            store.Load();
            store.Settings.ShowMenuBarIcon = false;
            store.SaveSettings();
            var platform = new IconPlatform { SupportsDockVisibility = true };
            var service = new TrayService(store, AppHost.Get<ILocalizer>(), platform, AppHost.Get<WindowService>(),
                AppHost.Get<UpdateService>());
            try
            {
                service.Create(app);
                var icon = Assert.Single(TrayIcon.GetIcons(app)!);
                Assert.False(icon.IsVisible);

                store.Settings.ShowMenuBarIcon = true;
                store.SaveSettings();
                Assert.True(icon.IsVisible);
            }
            finally
            {
                service.Dispose();
            }

            var nonMacStore = new UserDataStore(Path.Combine(directory, "non-mac"));
            nonMacStore.Load();
            nonMacStore.Settings.ShowMenuBarIcon = false;
            nonMacStore.SaveSettings();
            var nonMacService = new TrayService(nonMacStore, AppHost.Get<ILocalizer>(), new IconPlatform(),
                AppHost.Get<WindowService>(), AppHost.Get<UpdateService>());
            try
            {
                nonMacService.Create(app);
                Assert.True(Assert.Single(TrayIcon.GetIcons(app)!).IsVisible);
            }
            finally
            {
                nonMacService.Dispose();
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public void Mac_menu_bar_icon_is_a_template_unless_the_colored_one_is_chosen()
    {
        TestServices.EnsureInitialized("en");
        var app = Application.Current!;
        var directory = Directory.CreateTempSubdirectory("filee-tray-template-").FullName;
        try
        {
            var store = new UserDataStore(directory);
            store.Load();
            Assert.False(store.Settings.ColoredMenuBarIcon);
            var service = new TrayService(store, AppHost.Get<ILocalizer>(), new IconPlatform { SupportsDockVisibility = true },
                AppHost.Get<WindowService>(), AppHost.Get<UpdateService>());
            try
            {
                service.Create(app);
                var tray = Assert.Single(TrayIcon.GetIcons(app)!);
                Assert.True(MacOSProperties.GetIsTemplateIcon(tray));
                var template = tray.Icon;

                store.Settings.ColoredMenuBarIcon = true;
                store.SaveSettings();
                Assert.False(MacOSProperties.GetIsTemplateIcon(tray));
                Assert.NotSame(template, tray.Icon);
                var colored = tray.Icon;

                // Saving an unrelated setting keeps the icon instead of loading it again.
                store.Settings.Paused = true;
                store.SaveSettings();
                Assert.Same(colored, tray.Icon);

                store.Settings.ColoredMenuBarIcon = false;
                store.SaveSettings();
                Assert.True(MacOSProperties.GetIsTemplateIcon(tray));
            }
            finally
            {
                service.Dispose();
            }

            // The setting is macOS only: elsewhere the tray keeps the coloured icon.
            var nonMacService = new TrayService(store, AppHost.Get<ILocalizer>(), new IconPlatform(),
                AppHost.Get<WindowService>(), AppHost.Get<UpdateService>());
            try
            {
                nonMacService.Create(app);
                var tray = Assert.Single(TrayIcon.GetIcons(app)!);
                Assert.False(MacOSProperties.GetIsTemplateIcon(tray));
                Assert.NotNull(tray.Icon);
            }
            finally
            {
                nonMacService.Dispose();
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
