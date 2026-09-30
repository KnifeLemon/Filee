// Renders real windows headlessly (Skia) to catch XAML / binding errors and to produce screenshots
// in bin/.../screenshots for visual review.

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class RenderTests
{
    public static TheoryData<string, string> Pages => new()
    {
        { "home", "en" }, { "general", "en" }, { "triggers", "en" }, { "toolbar", "en" },
        { "presets", "en" }, { "theme", "en" }, { "engines", "en" }, { "about", "en" },
        { "toolbar", "ko" }, { "home", "ko" }, { "presets", "zh-CN" }, { "toolbar", "zh-CN" }, { "home", "zh-CN" },
    };

    [AvaloniaTheory]
    [MemberData(nameof(Pages))]
    public void Main_window_pages_render(string page, string language)
    {
        TestServices.EnsureInitialized(language);
        var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<UpdateService>());
        vm.Navigate(page);
        var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
        window.Show();
        Pump();

        Assert.NotNull(window.FindControl<ScrollViewer>("PageScroller"));
        Save(window, $"page-{page}-{language}.png");
        window.Close();
    }

    [AvaloniaFact]
    public void Donut_toolbar_renders_for_images()
    {
        TestServices.EnsureInitialized("ko");
        var files = new[] { @"C:\photos\a.jpg", @"C:\photos\b.jpg", @"C:\photos\c.jpg" };
        var vm = AppHost.Get<RadialViewModel>();
        vm.Load(files, clickMode: true);
        var window = new RadialWindow { DataContext = vm };
        window.Show();
        window.DonutControl.SetExternalHighlight(1);
        Pump();

        Assert.Equal(8, vm.Items.Count);
        Assert.Equal("JPG 3개", vm.CenterTitle);
        Save(window, "donut-images-ko.png");
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    public void First_run_engine_setup_renders(string language)
    {
        TestServices.EnsureInitialized(language);
        var vm = new EngineSetupViewModel(AppHost.Get<EngineDownloadService>(), AppHost.Get<Filee.Core.Localization.ILocalizer>());
        var window = new EngineSetupWindow { DataContext = vm };
        window.Show();
        Pump();

        // Everything not installed is offered; all but the large LibreOffice and the EPS-only Ghostscript pre-selected.
        Assert.All(vm.Packages.Where(p => !p.IsInstalled), p => Assert.Equal(p.Package.Id is not ("libreoffice" or "ghostscript"), p.Selected));
        Assert.Equal(vm.Packages.Any(p => p.Selected && p.CanInstall), vm.InstallCommand.CanExecute(null));
        Save(window, $"engine-setup-{language}.png");
        window.Close();
    }

    [AvaloniaFact]
    public void Donut_toolbar_waits_for_files_in_drag_mode()
    {
        TestServices.EnsureInitialized("en");
        var vm = AppHost.Get<RadialViewModel>();
        vm.ResetForDrag();
        var window = new RadialWindow { DataContext = vm };
        window.Show();
        Pump();

        Assert.Empty(vm.Items);
        Save(window, "donut-drag-waiting.png");
        window.Close();
    }

    [AvaloniaFact]
    public void Dark_theme_with_custom_accent_renders()
    {
        TestServices.EnsureInitialized("en");
        var theme = new ThemeSettings { Mode = ThemeMode.Dark, Accent = "#1D9E75", CornerRadius = 22 };
        AppHost.Get<ThemeService>().Apply(theme, "en");
        var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<UpdateService>());
        vm.Navigate("toolbar");
        var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
        window.Show();
        Pump();

        Save(window, "page-toolbar-dark.png");
        window.Close();
        AppHost.Get<ThemeService>().Apply(new ThemeSettings(), "en");
    }

    [AvaloniaFact]
    public void Preset_popup_renders()
    {
        TestServices.EnsureInitialized("ko");
        var preset = AppHost.Get<UserDataStore>().FindPreset("jpg-70")!;
        var window = new PresetEditorWindow { DataContext = new PresetEditorViewModel(preset, AppHost.Get<Filee.Core.Localization.ILocalizer>()) };
        window.Show();
        Pump();

        Save(window, "preset-popup-ko.png");
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    public void Sidebar_shows_the_version_and_a_new_release(string language)
    {
        TestServices.EnsureInitialized(language);
        var updates = AppHost.Get<UpdateService>();
        var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), updates);
        var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
        window.Show();
        Pump();
        var button = window.FindControl<Button>("UpdateButton")!;
        var version = window.FindControl<TextBlock>("VersionLabel")!;
        Assert.False(button.IsVisible);
        Assert.Contains(UpdateService.CurrentVersion, version.Text);

        try
        {
            updates.LatestVersion = "9.9.9";
            Pump();
            Assert.True(button.IsVisible);
            Assert.Contains("9.9.9", vm.UpdateText);
            Save(window, $"sidebar-update-{language}.png");

            var notice = new UpdateNoticeWindow(updates, AppHost.Get<Filee.Core.Localization.ILocalizer>().Format("update.notice_title", "9.9.9"));
            notice.Show();
            Pump();
            Save(notice, $"update-notice-{language}.png");
            notice.Close();
        }
        finally
        {
            updates.LatestVersion = null;
            window.Close();
        }
    }

    private static void Pump()
    {
        // Let layout, bindings and a few animation frames run.
        for (var i = 0; i < 40; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Save(Window window, string name)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var stream = File.Create(Path.Combine(TestServices.ScreenshotDirectory, name));
        frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
