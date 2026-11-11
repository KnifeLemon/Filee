// "Choose the compression" (#1353): the real window opened from the donut for "Compress image", which asks every time
// in a new library, its estimate, the quality going into the conversion and kept for the next time, Esc starting
// nothing; the window itself in light and dark and the long languages; and "Ask every time" in the preset editor.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using ImageMagick;

namespace Filee.App.Tests;

[Collection("MainWindow")]
public class AskCompressionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Photo(string folder, string name)
    {
        using var image = new MagickImage("gradient:#C04020-#2060C0", 1600, 1200);
        image.AddNoise(NoiseType.Gaussian, 0.4);
        image.Quality = 95;
        var path = Path.Combine(folder, name);
        image.Write(path, MagickFormat.Jpeg);
        return path;
    }

    private static async Task<AskCompressionWindow> WaitForWindow(WindowService windows)
    {
        for (var i = 0; i < 300; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (windows.AskCompression is { IsVisible: true } open)
            {
                RenderTests.Pump();
                return open;
            }
            await Task.Delay(10, Ct);
        }
        throw new TimeoutException("The compression window didn't open");
    }

    private static async Task WaitForEstimate(AskCompressionViewModel vm)
    {
        for (var i = 0; i < 500 && (vm.IsEstimating || vm.EstimatedBytes is null); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, Ct);
        }
        RenderTests.Pump();
        Assert.False(vm.IsEstimating, vm.EstimateText);
    }

    private static async Task<JobViewModel> WaitForJob(ConversionService conversions, HashSet<string> before)
    {
        for (var i = 0; i < 500; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (conversions.Jobs.FirstOrDefault(j => !before.Contains(j.Job.Id)) is { Job.State: JobState.Completed or JobState.Failed } job)
                return job;
            await Task.Delay(20, Ct);
        }
        throw new TimeoutException("The conversion didn't finish");
    }

    private static void Press(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        RenderTests.Pump();
    }

    /// <summary>
    /// "Compress image" in a new library asks: the window shows the photo, its size and about what it comes to at the
    /// preset's quality, and a lower quality estimates smaller. Enter compresses it at that quality, which the preset
    /// keeps for the next time; Esc starts nothing and changes nothing.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dropping_a_photo_asks_for_the_quality_then_compresses_it_or_esc_starts_nothing(bool cancel)
    {
        TestServices.EnsureInitialized();
        var folder = Directory.CreateTempSubdirectory("filee-ask-compression-").FullName;
        var store = AppHost.Get<UserDataStore>();
        var profile = store.Profiles.Single(p => p.Id == "images");
        var previous = profile.PresetIds;
        var preset = store.FindPreset("compress-image")!;
        var (previousOutput, previousImage) = (preset.Output, preset.Image.Clone());
        var conversions = AppHost.Get<ConversionService>();
        var before = conversions.Jobs.Select(j => j.Job.Id).ToHashSet();
        var radial = AppHost.Get<RadialController>();
        var windows = AppHost.Get<WindowService>();
        try
        {
            Assert.True(preset.AskCompression);
            var photo = Photo(folder, "holiday.jpg");
            profile.PresetIds = ["compress-image"];
            preset.Output = new() { Location = OutputLocation.CustomFolder, CustomFolder = folder, FileNamePattern = "{name}_small", OnlyIfSmaller = true };
            radial.ShowForFiles(450, 400, [photo]);
            var donut = radial.Window!.DonutControl;
            Assert.True(donut.HandleKey(Key.Right));
            Assert.True(donut.HandleKey(Key.Enter));

            var dialog = await WaitForWindow(windows);
            var vm = Assert.IsType<AskCompressionViewModel>(dialog.DataContext);
            Assert.False(radial.IsVisible);
            Assert.DoesNotContain(conversions.Jobs, j => !before.Contains(j.Job.Id));
            Assert.Equal("holiday.jpg", dialog.FindControl<TextBlock>("FilesText")!.Text);
            Assert.Equal($"Size now: {AskedCompression.SizeText(new FileInfo(photo).Length)}", dialog.FindControl<TextBlock>("SizeNow")!.Text);
            Assert.Equal("Compress image", dialog.StartCompression.Content);
            Assert.True(dialog.FindControl<Control>("ImageQuality")!.IsEffectivelyVisible);
            Assert.False(dialog.FindControl<Control>("VideoQuality")!.IsEffectivelyVisible);
            Assert.Equal(75, vm.Quality);

            await WaitForEstimate(vm);
            var atPreset = vm.EstimatedBytes!.Value;
            Assert.StartsWith("About ", dialog.FindControl<TextBlock>("EstimateText")!.Text);
            Assert.Contains("% smaller)", vm.EstimateText);
            vm.Quality = 40;
            await WaitForEstimate(vm);
            var atForty = vm.EstimatedBytes!.Value;
            Assert.True(atForty < atPreset, $"{atForty} vs {atPreset}");

            dialog.FindControl<Slider>("QualitySlider")!.Focus();
            Press(dialog, cancel ? Key.Escape : Key.Enter);
            Assert.False(dialog.IsVisible);

            if (cancel)
            {
                await Task.Delay(50, Ct);
                Dispatcher.UIThread.RunJobs();
                Assert.DoesNotContain(conversions.Jobs, j => !before.Contains(j.Job.Id));
                Assert.Equal([photo], Directory.EnumerateFiles(folder));
                Assert.Equal(75, store.FindPreset("compress-image")!.Image.Quality);
                return;
            }

            var job = await WaitForJob(conversions, before);
            Assert.Equal(JobState.Completed, job.Job.State);
            var output = Assert.Single(job.Job.Outputs);
            using (var written = new MagickImage(output))
                Assert.InRange(written.Quality, 38u, 42u);
            // The estimate was about right for this photo.
            Assert.InRange(new FileInfo(output).Length, atForty * 0.75, atForty * 1.25);
            // The saved preset still asks, now starting at 40.
            Assert.Equal((40, true), (preset.Image.Quality, preset.AskCompression));
        }
        finally
        {
            windows.AskCompression?.Close();
            radial.Close(animate: false);
            profile.PresetIds = previous;
            preset.Output = previousOutput;
            preset.Image = previousImage;
            store.SaveLibrary();
            Directory.Delete(folder, true);
        }
    }

    /// <summary>A drop with nothing to choose (a PNG stays lossless) converts at once, without the window.</summary>
    [AvaloniaFact]
    public void A_png_on_compress_image_converts_without_asking()
    {
        var preset = BuiltInData.CreatePresets().Single(p => p.Id == "compress-image");

        Assert.True(AskedCompression.Asks(preset));
        Assert.Empty(AskedCompression.KindsOf(preset, ["screenshot.png", "scan.tiff"]));
    }

    private static AskCompressionViewModel Model(Preset preset, IReadOnlyList<string> files, long sourceBytes, Func<CompressionChoice, long?> estimate) =>
        new(preset, files, sourceBytes, (choice, _) => Task.FromResult(estimate(choice)), AppHost.Get<ILocalizer>(), TimeSpan.Zero);

    /// <summary>The user's example: 100 MB estimated at 30 MB reads "About 30.0 MB (70% smaller)"; no smaller, or none.</summary>
    [AvaloniaFact]
    public async Task The_estimate_reads_as_about_so_much_and_how_much_smaller()
    {
        TestServices.EnsureInitialized();
        var video = BuiltInData.CreatePresets().Single(p => p.Id == "compress-video");
        long? next = 30_000_000;
        using var vm = Model(video, ["holiday.mp4"], 100_000_000, _ => next);
        await WaitForEstimate(vm);

        Assert.Equal("About 30.0 MB (70% smaller)", vm.EstimateText);
        Assert.Equal("Size now: 100 MB", vm.SizeNowText);
        Assert.True(vm.ShowVideo);
        Assert.Contains("video", vm.HintText);

        next = 100_000_000;
        vm.VideoQuality = vm.VideoQualities.Single(q => q.Value == MediaQuality.High);
        await Task.Delay(20, Ct);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Probably no smaller: the original is kept as it is.", vm.EstimateText);
        Assert.Equal(MediaQuality.High, vm.Result().VideoQuality);

        next = null;
        vm.VideoQuality = vm.VideoQualities.Single(q => q.Value == MediaQuality.Balanced);
        await Task.Delay(20, Ct);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Filee can't estimate the size of these files.", vm.EstimateText);
    }

    /// <summary>Only the newest estimate is shown: one that ends after a later change is dropped.</summary>
    [AvaloniaFact]
    public async Task An_estimate_overtaken_by_a_change_is_not_shown()
    {
        TestServices.EnsureInitialized();
        var preset = BuiltInData.CreatePresets().Single(p => p.Id == "compress-image");
        var slow = new TaskCompletionSource<long?>();
        using var vm = new AskCompressionViewModel(preset, ["a.jpg"], 1_000_000,
            (choice, _) => choice.ImageQuality == 75 ? slow.Task : Task.FromResult<long?>(200_000), AppHost.Get<ILocalizer>(), TimeSpan.Zero);
        Assert.True(vm.IsEstimating);

        vm.Quality = 50;
        await WaitForEstimate(vm);
        slow.SetResult(900_000);
        await Task.Delay(20, Ct);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(200_000, vm.EstimatedBytes);
        Assert.Equal("About 200 KB (80% smaller)", vm.EstimateText);
    }

    public static TheoryData<string, bool> Looks => new()
    {
        { "en", false }, { "en", true }, { "fr", false }, { "fr", true }, { "ko", false },
        { "de", true }, { "ar", false }, { "ru", true }, { "hi", false }, { "ja", true }, { "bn-BD", false },
    };

    /// <summary>
    /// The window in light and dark, long languages and right to left: in light a photo's quality, in dark a video,
    /// sound and PDFs together; the file, the settings, the estimate and the buttons all inside it.
    /// </summary>
    [AvaloniaTheory]
    [MemberData(nameof(Looks))]
    public async Task The_window_fits_in_light_and_dark(string language, bool dark)
    {
        TestServices.EnsureInitialized(language);
        var theme = AppHost.Get<ThemeService>();
        theme.Apply(new ThemeSettings { Mode = dark ? ThemeMode.Dark : ThemeMode.Light }, language);
        var loc = AppHost.Get<ILocalizer>();
        var preset = BuiltInData.CreatePresets().Single(p => p.Id == (dark ? "compress-video" : "compress-image"));
        if (dark)
            preset.TargetFormat = "mp4";
        IReadOnlyList<string> files = dark
            ? [@"C:\Users\me\Videos\Family holiday in the mountains, summer 2026 (full length).mp4", "interview.mov"]
            : [@"C:\Users\me\Pictures\Family holiday in the mountains, summer 2026 (the best one).jpg"];
        using var vm = Model(preset, files, 100_000_000, _ => 30_000_000);
        var window = new AskCompressionWindow { DataContext = vm };
        window.StartCompression.Content = loc.DisplayName(preset);
        try
        {
            window.Show();
            await WaitForEstimate(vm);
            Rect Box(string name)
            {
                var control = window.FindControl<Control>(name)!;
                return new Rect(control.Bounds.Size).TransformToAABB(control.TransformToVisual(window)!.Value);
            }
            var bounds = new Rect(window.Bounds.Size);
            string[] names = ["FilesText", "SizeNow", "EstimateText", "EstimateHint", "StartCompression", "CancelCompression",
                dark ? "VideoQuality" : "QualitySlider"];
            foreach (var name in names)
            {
                var box = Box(name);
                Assert.True(box.Width > 0 && bounds.Contains(box), $"{name} {box} outside {bounds}");
            }
            Assert.Equal(!dark, window.FindControl<Control>("ImageQuality")!.IsEffectivelyVisible);
            Assert.Equal(dark, window.FindControl<Control>("VideoQuality")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<Control>("PdfLevel")!.IsEffectivelyVisible);
            Assert.False(Box("StartCompression").Deflate(1).Intersects(Box("CancelCompression").Deflate(1)));
            if (language is "ar")
                Assert.True(Box("StartCompression").Left < Box("CancelCompression").Left);
            else
                Assert.True(Box("StartCompression").Left >= Box("CancelCompression").Right);
            Assert.Equal(dark ? loc.Format("compression.files", 2) : Path.GetFileName(files[0]), window.FindControl<TextBlock>("FilesText")!.Text);
            RenderTests.Save(window, $"ask-compression-{language}-{(dark ? "dark-video" : "light-image")}.png");
        }
        finally
        {
            window.Close();
            theme.Apply(new ThemeSettings(), language);
        }
    }

    /// <summary>A PDF, in its own window: the levels without "Don't compress", and the PDF hint.</summary>
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("fr")]
    public async Task A_pdf_offers_the_three_levels(string language)
    {
        TestServices.EnsureInitialized(language);
        var preset = BuiltInData.CreatePresets().Single(p => p.Id == "compress-pdf");
        using var vm = Model(preset, ["report.pdf"], 12_400_000, c => c.Pdf == PdfCompression.Screen ? 2_100_000 : 5_300_000);
        var window = new AskCompressionWindow { DataContext = vm };
        window.StartCompression.Content = AppHost.Get<ILocalizer>().DisplayName(preset);
        try
        {
            window.Show();
            await WaitForEstimate(vm);
            Assert.True(window.FindControl<Control>("PdfLevel")!.IsEffectivelyVisible);
            Assert.Equal([PdfCompression.Screen, PdfCompression.Ebook, PdfCompression.Print], vm.PdfLevels.Select(l => l.Value));
            Assert.Equal(PdfCompression.Ebook, vm.PdfLevel.Value);
            vm.PdfLevel = vm.PdfLevels[0];
            await WaitForEstimate(vm);
            Assert.Equal(2_100_000, vm.EstimatedBytes);
            RenderTests.Save(window, $"ask-compression-{language}-pdf.png");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The preset editor: "Ask every time" under the setting it asks for (picture quality, video quality, PDF level),
    /// its hint when on, gone for a merge; saved into the preset.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("en", "compress-image", "AskCompressionImage", false)]
    [InlineData("fr", "compress-video", "AskCompressionMedia", true)]
    [InlineData("en", "compress-pdf", "AskCompressionPdf", false)]
    public void The_editor_turns_asking_on_under_the_setting(string language, string id, string checkbox, bool dark)
    {
        TestServices.EnsureInitialized(language);
        var theme = AppHost.Get<ThemeService>();
        var loc = AppHost.Get<ILocalizer>();
        var preset = BuiltInData.CreatePresets().Single(p => p.Id == id);
        var vm = new PresetEditorViewModel(preset, loc);
        Assert.True(vm.AskCompression);
        if (dark)
            theme.Apply(new ThemeSettings { Mode = ThemeMode.Dark }, language);
        try
        {
            var window = new PresetEditorWindow { DataContext = vm, Height = 1400 };
            window.Show();
            RenderTests.Pump();
            var boxes = window.GetVisualDescendants().OfType<CheckBox>().Where(c => c.Name?.StartsWith("AskCompression", StringComparison.Ordinal) == true).ToList();
            // One checkbox shows, under the setting the preset has.
            Assert.Equal(checkbox, Assert.Single(boxes, b => b.IsEffectivelyVisible).Name);
            Assert.True(boxes.Single(b => b.Name == checkbox).IsChecked);
            RenderTests.Save(window, $"ask-compression-editor-{id}-{language}-{(dark ? "dark" : "light")}.png");
            window.Close();
        }
        finally
        {
            if (dark)
                theme.Apply(new ThemeSettings(), language);
        }
        vm.AskCompression = false;
        Assert.True(vm.Apply());
        Assert.False(preset.AskCompression);
        vm = new PresetEditorViewModel(preset, loc);
        vm.AskCompression = true;
        Assert.True(vm.Apply());
        Assert.True(preset.AskCompression);
    }

    [AvaloniaFact]
    public void A_merge_has_no_asking_in_the_editor()
    {
        TestServices.EnsureInitialized();
        var loc = AppHost.Get<ILocalizer>();
        var merge = BuiltInData.CreatePresets().Single(p => p.Id == "merge-pdf");
        merge.AskCompression = true;
        var vm = new PresetEditorViewModel(merge, loc);

        Assert.False(vm.ShowAskCompressionPdf);
        Assert.True(vm.Apply());
        Assert.False(merge.AskCompression);
    }
}
