using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using DiscUtils.Iso9660;
using DiscUtils.Udf;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.App.Tests;

[Collection("MainWindow")]
public sealed class AskIsoLabelTests
{
    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void Validation_never_moves_the_window_input_or_buttons_in_any_catalog(double zoom)
    {
        TestServices.EnsureInitialized("en");
        var loc = Assert.IsType<LocalizationService>(AppHost.Get<ILocalizer>());
        var language = loc.Language;
        var previousZoom = UiZoom.Scale;
        try
        {
            UiZoom.Set(zoom, remember: false);
            foreach (var catalog in LocalizationService.Languages)
            {
                loc.SetLanguage(catalog.Code);
                foreach (var filesystem in new[] { IsoFileSystem.Joliet, IsoFileSystem.Udf201 })
                {
                    var preset = new Preset { TargetFormat = "iso", Archive = { IsoFileSystem = filesystem } };
                    var vm = new AskIsoLabelViewModel("source.zip", preset, loc);
                    var window = new AskIsoLabelWindow { DataContext = vm };
                    window.StartIso.Content = "ISO";
                    try
                    {
                        window.Show();
                        RenderTests.Pump();
                        var size = window.ClientSize;
                        var position = window.Position;
                        var input = window.FindControl<TextBox>("LabelBox")!;
                        var inputPosition = input.TranslatePoint(default, window);
                        var buttonPosition = window.StartIso.TranslatePoint(default, window);
                        foreach (var label in new[] { new string('a', 33), " invalid", "abcdefghi", "abcdefghij", "字123456789012345", "" })
                        {
                            vm.Label = label;
                            RenderTests.Pump();
                            Assert.True(size == window.ClientSize, $"{catalog.Code} / {filesystem} / {label}: {size} -> {window.ClientSize}");
                            Assert.Equal(position, window.Position);
                            Assert.Equal(inputPosition, input.TranslatePoint(default, window));
                            Assert.Equal(buttonPosition, window.StartIso.TranslatePoint(default, window));
                        }
                    }
                    finally { window.Close(); }
                }
            }
        }
        finally
        {
            loc.SetLanguage(language);
            UiZoom.Set(previousZoom, remember: false);
        }
    }

    [AvaloniaTheory]
    [InlineData(false, false, IsoFileSystem.Joliet, false)]
    [InlineData(true, false, IsoFileSystem.Joliet, false)]
    [InlineData(false, true, IsoFileSystem.Udf201, false)]
    [InlineData(true, true, IsoFileSystem.Udf201, false)]
    [InlineData(false, false, IsoFileSystem.Joliet, true)]
    public async Task Batch_labels_are_confirmed_before_any_job_and_cancel_starts_nothing(bool cancel, bool archive, IsoFileSystem filesystem, bool cancelSave)
    {
        TestServices.EnsureInitialized("en");
        var root = Directory.CreateTempSubdirectory("filee-ask-label-").FullName;
        var store = AppHost.Get<UserDataStore>();
        var profile = store.Profiles.Single(p => p.Id == (archive ? "archives" : "folders"));
        var previousIds = profile.PresetIds;
        var preset = store.FindPreset("to-iso")!;
        var previousArchive = preset.Archive.Clone();
        var previousOutput = preset.Output;
        var windows = AppHost.Get<WindowService>();
        var conversions = AppHost.Get<ConversionService>();
        var radial = AppHost.Get<RadialController>();
        var before = conversions.Jobs.Select(j => j.Job.Id).ToHashSet();
        try
        {
            var sources = new[] { "First-folder", "Second-folder" }.Select(n => Directory.CreateDirectory(Path.Combine(root, n)).FullName).ToArray();
            foreach (var folder in sources) File.WriteAllText(Path.Combine(folder, "data.txt"), "contents");
            if (archive)
                sources = sources.Select(folder =>
                {
                    var zip = folder + ".zip";
                    ZipFile.CreateFromDirectory(folder, zip);
                    return zip;
                }).ToArray();
            profile.PresetIds = ["to-iso"];
            preset.Archive = new() { AskIsoVolumeLabel = true, IsoFileSystem = filesystem };
            preset.Output = new() { Location = OutputLocation.CustomFolder, CustomFolder = root, AskEveryTime = cancelSave };
            radial.ShowForFiles(450, 400, sources);
            var donut = radial.Window!.DonutControl;
            Assert.True(donut.HandleKey(Key.Right));
            Assert.True(donut.HandleKey(Key.Enter));
            var first = await WaitForWindow(windows);
            var vm = Assert.IsType<AskIsoLabelViewModel>(first.DataContext);
            Assert.Equal("First-folder", vm.Label);
            Assert.Equal("File 1 of 2", vm.Position);
            vm.Label = new string('a', 31);
            RenderTests.Pump();
            Assert.False(first.StartIso.IsEffectivelyEnabled);
            vm.Label = "First disc";
            RenderTests.Pump();
            first.StartIso.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            var second = await WaitForWindow(windows, first);
            var secondVm = Assert.IsType<AskIsoLabelViewModel>(second.DataContext);
            Assert.Equal("Second-folder", secondVm.Label);
            Assert.Equal("File 2 of 2", secondVm.Position);
            Assert.DoesNotContain(conversions.Jobs, j => !before.Contains(j.Job.Id));
            secondVm.Label = "Second disc";
            RenderTests.Pump();
            if (cancel)
                second.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            else
                second.StartIso.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 300; i++)
            {
                Dispatcher.UIThread.RunJobs();
                if (cancel || cancelSave || Directory.GetFiles(root, "*.iso").Length == 2) break;
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
            RenderTests.Pump();
            Assert.Equal(cancel || cancelSave ? 0 : 2, Directory.GetFiles(root, "*.iso").Length);
            Assert.Equal("", preset.Archive.IsoVolumeLabel);
            Assert.True(preset.Archive.AskIsoVolumeLabel);
            if (cancel || cancelSave) Assert.DoesNotContain(conversions.Jobs, j => !before.Contains(j.Job.Id));
            else
            {
                foreach (var (source, label) in sources.Zip(new[] { "First disc", "Second disc" }))
                {
                    var name = archive ? Path.GetFileNameWithoutExtension(source) : Path.GetFileName(source);
                    using var stream = File.OpenRead(Path.Combine(root, name + ".iso"));
                    using DiscUtils.DiscFileSystem reader = filesystem == IsoFileSystem.Joliet ? new CDReader(stream, true) : new UdfReader(stream);
                    Assert.Equal(label, reader.VolumeLabel);
                }
                Assert.All(conversions.Jobs.Where(j => !before.Contains(j.Job.Id)), j => Assert.False(j.Job.Preset.Archive.AskIsoVolumeLabel));
            }
        }
        finally
        {
            windows.AskIsoLabel?.Close();
            radial.Close(animate: false);
            preset.Archive = previousArchive;
            preset.Output = previousOutput;
            profile.PresetIds = previousIds;
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task Dialog_prefills_custom_or_archive_name_and_empty_means_automatic()
    {
        TestServices.EnsureInitialized("en");
        var loc = AppHost.Get<ILocalizer>();
        var preset = new Preset { TargetFormat = "iso", Archive = { IsoVolumeLabel = "Saved label", AskIsoVolumeLabel = true } };
        var editor = new PresetEditorViewModel(preset, loc);
        Assert.True(editor.AskIsoVolumeLabel);
        Assert.True(editor.Apply());
        Assert.True(new PresetEditorViewModel(preset.Clone(), loc).AskIsoVolumeLabel);
        var windows = AppHost.Get<WindowService>();
        var pending = windows.AskIsoLabelAsync("holiday.tar.gz", preset);
        var dialog = await WaitForWindow(windows);
        try
        {
            var vm = Assert.IsType<AskIsoLabelViewModel>(dialog.DataContext);
            Assert.Equal("Saved label", vm.Label);
            vm.Label = "";
            RenderTests.Pump();
            dialog.StartIso.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("", await pending);
            Assert.Equal("Saved label", preset.Archive.IsoVolumeLabel);
            preset.Archive.IsoVolumeLabel = "";
            Assert.Equal("holiday", new AskIsoLabelViewModel("holiday.tar.gz", preset, loc).Label);
            preset.Archive.IsoFileSystem = IsoFileSystem.Udf201;
            var udf = new AskIsoLabelViewModel("archive.zip", preset, loc) { Label = new string('a', 30) };
            Assert.True(udf.IsValid);
            udf.Label += "a";
            Assert.False(udf.IsValid);
        }
        finally { dialog.Close(); }
    }

    private static async Task<AskIsoLabelWindow> WaitForWindow(WindowService windows, AskIsoLabelWindow? previous = null)
    {
        for (var i = 0; i < 300; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (windows.AskIsoLabel is { IsVisible: true } dialog && dialog != previous)
            {
                RenderTests.Pump();
                return dialog;
            }
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("The ISO label window did not open");
    }

    [AvaloniaFact]
    public void Label_dialog_renders_in_dark_theme()
    {
        TestServices.EnsureInitialized("en");
        var theme = AppHost.Get<UserDataStore>().Settings.Theme;
        var motion = Motion.Enabled;
        var preset = new Preset { TargetFormat = "iso" };
        var window = new AskIsoLabelWindow
        {
            DataContext = new AskIsoLabelViewModel("Holiday photos.zip", preset, AppHost.Get<ILocalizer>(), 0, 2),
        };
        window.StartIso.Content = "Create ISO";
        try
        {
            AppHost.Get<ThemeService>().Apply(new ThemeSettings { Mode = ThemeMode.Dark, ReduceMotion = true }, "en");
            window.Show();
            RenderTests.Pump();
            RenderTests.Save(window, "iso-label-prompt-dark.png");
            Assert.True(window.StartIso.IsEffectivelyEnabled);
        }
        finally
        {
            window.Close();
            AppHost.Get<ThemeService>().Apply(theme, "en");
            Motion.Set(motion);
        }
    }

    [AvaloniaTheory]
    [InlineData(IsoFileSystem.Joliet, "abcdefghijklmnopq", "17 / 16", "Joliet")]
    [InlineData(IsoFileSystem.Udf201, "1234567890123456789012345678901", "31 / 30", "UDF 2.01")]
    [InlineData(IsoFileSystem.Udf201, "字123456789012345", "16 / 15", "UDF 2.01")]
    public void Counter_and_error_follow_the_selected_filesystem(IsoFileSystem filesystem, string label, string counter, string format)
    {
        TestServices.EnsureInitialized("en");
        var loc = AppHost.Get<ILocalizer>();
        var preset = new Preset { TargetFormat = "iso", Archive = { IsoFileSystem = filesystem } };
        var vm = new AskIsoLabelViewModel("source.zip", preset, loc) { Label = label };
        Assert.Equal(counter, vm.Counter);
        Assert.Equal($"Volume label is too long for {format}.", vm.Error);
        Assert.False(vm.IsValid);
        var editor = new PresetEditorViewModel(preset, loc) { IsoVolumeLabel = label };
        Assert.Equal(vm.Counter, editor.IsoLabelCounter);
        Assert.Equal(vm.Error, editor.IsoLabelError);
        Assert.Equal(vm.Hint, editor.IsoLabelHint);
        Assert.DoesNotContain(filesystem == IsoFileSystem.Joliet ? "Latin-1" : "BMP", vm.Hint);
        vm.Label = " valid";
        Assert.Equal("Remove unsupported characters or leading/trailing spaces.", vm.Error);
        vm.Label = "";
        Assert.Null(vm.Error);
        Assert.Equal(filesystem == IsoFileSystem.Joliet ? "0 / 16" : "0 / 30", vm.Counter);
        editor.IsoFileSystem = editor.IsoFileSystems.Single(c => c.Value != filesystem);
        Assert.NotEqual(vm.Hint, editor.IsoLabelHint);
    }
}
