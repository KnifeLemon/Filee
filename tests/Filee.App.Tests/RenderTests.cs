// Renders real windows headlessly (Skia) to catch XAML / binding errors and to produce screenshots
// in bin/.../screenshots for visual review.

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.ViewModels.Pages;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Profiles;
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

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    public void Watch_folders_page_renders_a_rule(string language)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<Filee.Core.Settings.UserDataStore>();
        var rule = new Filee.Core.Watching.WatchRule { Folder = @"C:\Scans\Inbox", PresetId = "to-pdf", Enabled = false };
        store.Settings.WatchFolders.Add(rule);
        try
        {
            var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<UpdateService>());
            vm.Navigate("watch");
            var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
            window.Show();
            Pump();

            var page = (Filee.App.ViewModels.Pages.WatchFoldersPageViewModel)vm.CurrentPage;
            var item = Assert.Single(page.Rules);
            Assert.Equal("to-pdf", item.Preset?.Value);
            Assert.False(string.IsNullOrWhiteSpace(item.Status));
            Save(window, $"page-watch-{language}.png");
            window.Close();
        }
        finally
        {
            store.Settings.WatchFolders.Remove(rule);
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    public void Home_page_lists_failed_files_with_a_retry_button(string language)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<Filee.Core.Settings.UserDataStore>();
        var folder = Directory.CreateTempSubdirectory("filee-home-").FullName;
        var scan = Path.Combine(folder, "scan-0412.heic");
        File.WriteAllBytes(scan, [0]);
        var entry = new Filee.Core.History.HistoryEntry
        {
            FinishedAt = DateTime.Now,
            PresetName = "PDF",
            State = Filee.Core.Conversion.JobState.CompletedWithErrors,
            Sources = [Path.Combine(folder, "report.docx"), scan, Path.Combine(folder, "notes.xyz")],
            Failures =
            [
                new() { Source = scan, ErrorKey = "error.conversion_failed", ErrorDetail = "HEIC decoder: unexpected end of file" },
                new() { Source = Path.Combine(folder, "notes.xyz"), ErrorKey = "error.unsupported_source", ErrorDetail = "xyz" },
            ],
            Preset = new Filee.Core.Presets.Preset { TargetFormat = "pdf" },
        };
        store.AddHistory(entry);
        try
        {
            var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<UpdateService>());
            vm.Navigate("home");
            var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
            window.Show();
            Pump();

            var item = ((Filee.App.ViewModels.Pages.HomePageViewModel)vm.CurrentPage).History[0];
            Assert.True(item.CanRetry);
            item.ToggleFailuresCommand.Execute(null);
            Pump();
            Save(window, $"page-home-failed-{language}.png");
            window.Close();
        }
        finally
        {
            store.ClearHistory(); // also on disk: the next test's application loads the history again
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public void Engines_page_shows_versions_descriptions_and_download_speed()
    {
        TestServices.EnsureInitialized("ko");
        var downloads = AppHost.Get<EngineDownloadService>();
        var package = downloads.Packages[0];
        var before = (package.Status, package.Progress, package.ProgressDetail);
        package.Status = EnginePackageStatus.Downloading;
        package.Progress = 42;
        package.ProgressDetail = "176 MB / 422 MB · 8.4 MB/s · 30초 남음";
        try
        {
            var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<UpdateService>());
            vm.Navigate("engines");
            var window = new MainWindow { DataContext = vm, Width = 1080, Height = 1400 };
            window.Show();
            Pump();

            var engines = ((Filee.App.ViewModels.Pages.EnginesPageViewModel)vm.CurrentPage).Engines;
            Assert.All(engines.Where(e => e.IsAvailable), e => Assert.False(string.IsNullOrWhiteSpace(e.Version)));
            Assert.All(engines, e => Assert.False(string.IsNullOrWhiteSpace(e.Description)));
            Save(window, "page-engines-downloading-ko.png");
            window.Close();
        }
        finally
        {
            (package.Status, package.Progress, package.ProgressDetail) = before;
        }
    }

    [AvaloniaFact]
    public void Dragging_a_preset_opens_a_gap_with_a_preview_slice()
    {
        TestServices.EnsureInitialized("ko");
        var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<Filee.Core.Localization.ILocalizer>(), AppHost.Get<UpdateService>());
        vm.Navigate("toolbar");
        var window = new MainWindow { DataContext = vm, Width = 1080, Height = 820 };
        window.Show();
        Pump();
        var donut = window.GetVisualDescendants().OfType<DonutMenu>().Single(d => d.IsEditMode);
        var count = donut.Items!.Count;

        // A chip over the third slice: the slices from there on slide clockwise and a preview takes slot 2.
        donut.SetDropPreview(2, "TIFF 300dpi");
        for (var i = 0; i < 120; i++)
            donut.Advance(1 / 60.0);
        donut.InvalidateVisual();
        Pump();
        Assert.Equal(2, donut.SlotAt(DonutPoint(donut, 2, count + 1), count + 1));
        Save(window, "toolbar-drop-preview-ko.png");

        // Moving the first slice to slot 3: it leaves its place, the preview shows it at the new one.
        donut.SetDropPreview(3, donut.Items[0].Label, removed: 0);
        for (var i = 0; i < 120; i++)
            donut.Advance(1 / 60.0);
        donut.InvalidateVisual();
        Pump();
        Save(window, "toolbar-move-preview-ko.png");

        donut.SetDropPreview(-1);
        window.Close();
    }

    /// <summary>The middle of slot <paramref name="slot"/> of a donut with <paramref name="slots"/> slices.</summary>
    private static Avalonia.Point DonutPoint(DonutMenu donut, int slot, int slots)
    {
        var center = new Avalonia.Point(donut.Bounds.Width / 2, donut.Bounds.Height / 2);
        var radius = donut.OuterRadius * (1 + donut.HoleRatio) / 2;
        return DonutGeometry.PointAt(center, radius, DonutGeometry.MidAngle(slot, slots));
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

        // Everything not installed is offered, nothing is ticked: installing needs a choice first.
        Assert.Contains(vm.Packages, p => !p.IsInstalled && p.CanInstall);
        Assert.DoesNotContain(vm.Packages, p => p.Selected);
        Assert.False(vm.InstallCommand.CanExecute(null));
        vm.Packages.First(p => p.CanInstall && !p.IsInstalled).Selected = true;
        Assert.True(vm.InstallCommand.CanExecute(null));
        vm.Packages.First(p => p.Selected).Selected = false;
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
        // Nothing shows until files are dragged in (a Ctrl + drag without files must not open anything).
        Assert.Equal(0, vm.DonutOpacity);
        Save(window, "donut-drag-waiting.png");
        vm.Load([@"C:\photos\a.jpg"], clickMode: false);
        Assert.Equal(1, vm.DonutOpacity);
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

            var loc = AppHost.Get<Filee.Core.Localization.ILocalizer>();
            var notice = new UpdateNoticeWindow(updates, loc, loc.Format("update.notice_title", "9.9.9"));
            notice.Show();
            Pump();
            Save(notice, $"update-notice-{language}.png");

            // While the installer downloads, the card, the sidebar button and the tray show the progress.
            updates.Step = UpdateStep.Downloading;
            updates.DownloadPercent = 42;
            Pump();
            Assert.Contains("42", vm.UpdateText);
            Save(notice, $"update-notice-downloading-{language}.png");
            Save(window, $"sidebar-update-downloading-{language}.png");
            updates.Step = UpdateStep.Idle;
            notice.Close();

            var feedback = new FeedbackNoticeWindow();
            feedback.Show();
            Pump();
            Save(feedback, $"feedback-notice-{language}.png");
            feedback.Close();
        }
        finally
        {
            updates.Step = UpdateStep.Idle;
            updates.LatestVersion = null;
            window.Close();
        }
    }

    /// <summary>
    /// The extension chips of a profile that shares extensions with "Images" (warning chips), has one Filee doesn't
    /// know (muted chip) and shows suggestions for what is being typed (dark: a refused duplicate instead).
    /// </summary>
    [AvaloniaTheory]
    [InlineData("en", false)]
    [InlineData("ko", false)]
    [InlineData("zh-CN", false)]
    [InlineData("en", true)]
    [InlineData("ko", true)]
    public void Toolbar_extension_editor_renders(string language, bool dark)
    {
        var photos = new ToolbarProfile
        {
            Id = "render-photos",
            Name = language == "ko" ? "사진" : "Photos",
            Extensions = ["jpg", "png", "heic", "dng", "webp", "xyz"],
            PresetIds = ["to-jpg", "to-webp", "half-size", "to-pdf"],
        };
        WithProfiles(language, dark, 1240, profiles => profiles.Insert(profiles.FindIndex(p => p.IsFallback), photos), (window, vm) =>
        {
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile == photos);
            Pump();
            var editor = window.GetVisualDescendants().OfType<TagEditor>().Single();
            editor.FocusInput();
            Pump();
            if (dark)
            {
                window.KeyTextInput("PNG");
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
                Assert.NotNull(vm.ExtensionTags!.InputError);
            }
            else
            {
                window.KeyTextInput("we");
                Pump();
                Assert.True(editor.IsSuggestionListOpen);
            }
            Pump();

            Assert.Equal(6, vm.ExtensionTags!.Tags.Count);
            Assert.Equal(2, vm.ExtensionTags.Notes.Count); // shared with "Images" + unknown
            Save(window, $"toolbar-extensions-{language}{(dark ? "-dark" : "")}.png");
        });
    }

    /// <summary>A second mixed-selection donut limited to images and PDF, two groups expanded.</summary>
    [AvaloniaTheory]
    [InlineData("en", false, "render-scans")]
    [InlineData("ko", false, "render-scans")]
    [InlineData("zh-CN", false, "render-scans")]
    [InlineData("ko", true, "render-scans")]
    [InlineData("en", false, "mixed")]
    [InlineData("en", true, "mixed")]
    public void Toolbar_mixed_selection_editor_renders(string language, bool dark, string profileId)
    {
        var scans = new ToolbarProfile
        {
            Id = "render-scans",
            Name = language == "ko" ? "스캔 모음" : "Scans",
            IsFallback = true,
            Extensions = ["jpg", "png", "tiff", "pdf"],
            PresetIds = ["to-pdf", "merge-pdf", "zip-all"],
        };
        WithProfiles(language, dark, 1600, profiles => profiles.Add(scans), (window, vm) =>
        {
            vm.SelectedProfile = vm.Profiles.Single(p => p.Profile.Id == profileId);
            Pump();
            var mixed = vm.MixedExtensions!;
            if (profileId != "mixed")
                mixed.Groups[0].IsExpanded = true; // Images: partly checked, many chips
            mixed.Groups[1].IsExpanded = true; // PDF: all checked (Scans)
            Pump();

            Assert.True(vm.IsMixed);
            Assert.Null(vm.ExtensionTags);
            Assert.Equal(profileId == "mixed" ? false : null, mixed.Groups[0].State);
            Assert.True(vm.CanDelete); // two mixed profiles: either may go
            Save(window, $"toolbar-mixed-{profileId.Replace("render-", "")}-{language}{(dark ? "-dark" : "")}.png");
        });
    }

    /// <summary>The preset popup with the video/audio and archive sections, and the target list.</summary>
    [AvaloniaTheory]
    [InlineData("en", false)]
    [InlineData("ko", false)]
    [InlineData("zh-CN", false)]
    [InlineData("ko", true)]
    public void Preset_editor_shows_media_and_archive_options(string language, bool dark)
    {
        TestServices.EnsureInitialized(language);
        var theme = AppHost.Get<ThemeService>();
        if (dark)
            theme.Apply(new ThemeSettings { Mode = ThemeMode.Dark }, language);
        try
        {
            var store = AppHost.Get<UserDataStore>();
            var loc = AppHost.Get<ILocalizer>();
            var suffix = $"{language}{(dark ? "-dark" : "")}";

            var media = new PresetEditorViewModel(store.FindPreset("mp4-720")!, loc);
            Assert.True(media.ShowMedia && media.ShowMaxHeight && media.ShowRemoveAudio);
            Assert.False(media.ShowPdf || media.ShowImage || media.ShowArchive);
            Assert.Equal(720, media.MaxHeight.Value);
            var window = new PresetEditorWindow { DataContext = media, Height = 900 };
            window.Show();
            Pump();
            Save(window, $"preset-media-{suffix}.png");

            // The target list: categories next to the names. (Headless popups live in the window's overlay layer,
            // so the open list is part of the window's frame.)
            var box = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "TargetBox");
            box.IsDropDownOpen = true;
            Pump();
            Assert.True(box.GetVisualDescendants().OfType<Popup>().Single().IsOpen);
            Save(window, $"preset-targets-{suffix}.png");
            box.IsDropDownOpen = false;
            window.Close();

            var archive = new PresetEditorViewModel(store.FindPreset("zip-all")!, loc);
            Assert.True(archive.ShowArchive && archive.ShowArchiveOptions && archive.CombineIntoOne);
            Assert.False(archive.ShowMedia || archive.ShowPdf);
            window = new PresetEditorWindow { DataContext = archive, Height = 760 };
            window.Show();
            Pump();
            Save(window, $"preset-archive-{suffix}.png");
            window.Close();

            // Audio targets have no picture options; lossless ones only channels, WAV also sample rate and bit depth (#37);
            // AMR is fixed mono 8 kHz; "Extract" only explains.
            archive.Target = archive.Targets.Single(t => t.Value == "mp3");
            Assert.True(archive.ShowMedia && archive.ShowAudioBitrate && archive.ShowAudioChannels && !archive.ShowMaxHeight && !archive.ShowRemoveAudio);
            Assert.False(archive.ShowWav);
            archive.Target = archive.Targets.Single(t => t.Value == "flac");
            Assert.True(archive.ShowMedia && archive.ShowAudioChannels && !archive.ShowAudioBitrate && !archive.ShowWav);
            archive.Target = archive.Targets.Single(t => t.Value == "wav");
            Assert.True(archive.ShowMedia && archive.ShowAudioChannels && archive.ShowWav);
            window = new PresetEditorWindow { DataContext = archive, Height = 760 };
            window.Show();
            Pump();
            Save(window, $"preset-wav-{suffix}.png");
            window.Close();
            archive.Target = archive.Targets.Single(t => t.Value == "amr");
            Assert.False(archive.ShowAudioChannels || archive.ShowWav);
            archive.Target = archive.Targets.Single(t => t.Value == Filee.Core.Formats.FormatRegistry.Folder);
            Assert.True(archive.ShowArchive && archive.ShowExtractNote && !archive.ShowArchiveOptions);
        }
        finally
        {
            if (dark)
                theme.Apply(new ThemeSettings(), language);
        }
    }

    [AvaloniaFact]
    public void Preset_editor_saves_media_and_archive_options()
    {
        TestServices.EnsureInitialized("en");
        var preset = new Filee.Core.Presets.Preset { TargetFormat = "mp4", Name = "Clip" };
        var vm = new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>());
        Assert.Equal("*", vm.Targets[0].Value); // "Same as source" stays first
        Assert.Equal([0, 32, 48, 64, 96, 128, 192, 256, 320], vm.AudioBitrates.Select(b => b.Value)); // 32-64 for speech (#37)
        Assert.All(vm.Targets.Skip(1), t => Assert.True(Filee.Core.Formats.FormatRegistry.Get(t.Value).Writable));
        Assert.Equal("Video", vm.Target.Detail);

        vm.MediaQuality = vm.MediaQualities.Single(q => q.Value == Filee.Core.Presets.MediaQuality.Small);
        vm.MaxHeight = vm.MaxHeights.Single(h => h.Value == 1080);
        vm.AudioBitrate = vm.AudioBitrates.Single(b => b.Value == 192);
        vm.RemoveAudio = true;
        vm.AudioChannels = vm.AudioChannelChoices.Single(c => c.Value == 1);
        vm.SampleRate = vm.SampleRates.Single(r => r.Value == 16000);
        vm.WavFormat = vm.WavFormats.Single(f => f.Value == Filee.Core.Presets.WavSampleFormat.Pcm24);
        Assert.Equal("16 kHz", vm.SampleRate.Label);
        vm.ArchiveLevel = vm.ArchiveLevels.Single(l => l.Value == Filee.Core.Presets.ArchiveLevel.Maximum);
        vm.CombineIntoOne = true;
        Assert.True(vm.Apply());

        Assert.Equal(Filee.Core.Presets.MediaQuality.Small, preset.Media.Quality);
        Assert.Equal(1080, preset.Media.MaxHeight);
        Assert.Equal(192, preset.Media.AudioBitrateKbps);
        Assert.True(preset.Media.RemoveAudio);
        Assert.Equal((1, 16000, Filee.Core.Presets.WavSampleFormat.Pcm24),
            (preset.Media.AudioChannels, preset.Media.AudioSampleRate, preset.Media.WavFormat));
        Assert.Equal(Filee.Core.Presets.ArchiveLevel.Maximum, preset.Archive.Level);
        Assert.True(preset.Archive.CombineIntoOne);

        // A value set outside the editor is offered instead of being lost.
        preset.Media.MaxHeight = 600;
        Assert.Equal("600p", new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>()).MaxHeight.Label);
    }

    /// <summary>Shows the toolbar page (tall, so the profile card is visible) with temporarily changed profiles.</summary>
    private static void WithProfiles(string language, bool dark, double height, Action<List<ToolbarProfile>> change, Action<Window, ToolbarPageViewModel> act)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<UserDataStore>();
        var theme = AppHost.Get<ThemeService>();
        var snapshot = store.Profiles.Select(p => p.Clone()).ToList();
        Window? window = null;
        try
        {
            change(store.Profiles);
            if (dark)
                theme.Apply(new ThemeSettings { Mode = ThemeMode.Dark }, language);
            var vm = new MainWindowViewModel(AppHost.Services, AppHost.Get<ILocalizer>(), AppHost.Get<UpdateService>());
            vm.Navigate("toolbar");
            window = new MainWindow { DataContext = vm, Width = 1080, Height = height };
            window.Show();
            Pump();
            act(window, (ToolbarPageViewModel)vm.CurrentPage);
        }
        finally
        {
            window?.Close();
            if (dark)
                theme.Apply(new ThemeSettings(), language);
            store.Profiles.Clear();
            store.Profiles.AddRange(snapshot);
            store.SaveLibrary();
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    public void Excluded_apps_render_as_tags(string language)
    {
        TestServices.EnsureInitialized(language);
        var store = AppHost.Get<UserDataStore>();
        var before = store.Settings.ExcludedProcesses.ToList();
        try
        {
            store.Settings.ExcludedProcesses.Clear();
            store.Settings.ExcludedProcesses.AddRange(["Photoshop", "code"]);
            var vm = new TriggersPageViewModel(store, AppHost.Get<Filee.Core.Localization.ILocalizer>());
            var window = new Window { Content = new Filee.App.Views.Pages.TriggersPage { DataContext = vm }, Width = 820, Height = 1500 };
            window.Show();
            Pump();
            Assert.Equal(["Photoshop", "code"], vm.ExcludedApps.Tags.Select(t => t.Value));
            Save(window, $"triggers-excluded-{language}.png");
            window.Close();
        }
        finally
        {
            store.Settings.ExcludedProcesses.Clear();
            store.Settings.ExcludedProcesses.AddRange(before);
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

    private static void Save(TopLevel window, string name)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var stream = File.Create(Path.Combine(TestServices.ScreenshotDirectory, name));
        frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
