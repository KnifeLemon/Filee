// Merge order (issue #268): the real chooser opened from the donut, then the pages of the merged PDF; for "Merge images"
// (#645) the layout below the list, the merged image and the choices remembered.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Settings;
using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.App.Tests;

[Collection("MainWindow")]
public class MergeOrderTests
{
    private static void Click(Window window, string name)
    {
        var button = window.FindControl<Button>(name)!;
        var at = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        RenderTests.Pump();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Donut_merge_waits_for_review_and_uses_the_chosen_order_or_cancels(bool cancel)
    {
        TestServices.EnsureInitialized();
        var folder = Directory.CreateTempSubdirectory("filee-merge-order-").FullName;
        var store = AppHost.Get<UserDataStore>();
        var profile = store.Profiles.Single(p => p.Id == "pdf");
        var previous = profile.PresetIds;
        var preset = store.FindPreset("merge-pdf")!;
        var previousOutput = preset.Output;
        var conversions = AppHost.Get<ConversionService>();
        var before = conversions.Jobs.Select(j => j.Job.Id).ToHashSet();
        var radial = AppHost.Get<RadialController>();
        var windows = AppHost.Get<WindowService>();
        try
        {
            var inputs = new List<string>();
            foreach (var width in new[] { 200, 300, 400 })
            {
                // Identical names in different folders stay distinguishable in the chooser.
                var dir = Directory.CreateDirectory(Path.Combine(folder, width.ToString())).FullName;
                var path = Path.Combine(dir, "source.pdf");
                using var document = new PdfDocument();
                var page = document.AddPage();
                page.Width = PdfSharp.Drawing.XUnit.FromPoint(width);
                page.Height = PdfSharp.Drawing.XUnit.FromPoint(500);
                document.Save(path);
                inputs.Add(path);
            }
            profile.PresetIds = ["merge-pdf"];
            preset.Output = new() { Location = Filee.Core.Presets.OutputLocation.CustomFolder, CustomFolder = folder };
            radial.ShowForFiles(450, 400, inputs);
            var donut = radial.Window!.DonutControl;
            Assert.True(donut.HandleKey(Key.Right));
            Assert.True(donut.HandleKey(Key.Enter));
            RenderTests.Pump();

            var dialog = Assert.IsType<MergeOrderWindow>(windows.MergeOrder);
            // Sorted by name, then folder: 200, 300, 400.
            var vm = Assert.IsType<MergeOrderViewModel>(dialog.DataContext);
            Assert.False(radial.IsVisible);
            Assert.DoesNotContain(conversions.Jobs, j => !before.Contains(j.Job.Id));
            Assert.Equal(inputs, vm.OrderedFiles);
            Assert.False(dialog.FindControl<Button>("MoveUp")!.IsEffectivelyEnabled);
            Click(dialog, "MoveDown"); // 300, 200, 400
            Assert.Equal([inputs[1], inputs[0], inputs[2]], vm.OrderedFiles);
            dialog.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.ArrowDown, null); // 300, 400, 200
            RenderTests.Pump();
            Assert.Equal([inputs[1], inputs[2], inputs[0]], vm.OrderedFiles);
            Assert.False(dialog.FindControl<Button>("MoveDown")!.IsEffectivelyEnabled);
            Click(dialog, cancel ? "CancelMerge" : "StartMerge");

            if (cancel)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
                Dispatcher.UIThread.RunJobs();
                Assert.DoesNotContain(conversions.Jobs, j => !before.Contains(j.Job.Id));
                Assert.Empty(Directory.EnumerateFiles(folder, "*.pdf"));
            }
            else
            {
                JobViewModel? started = null;
                for (var i = 0; i < 200; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    started = conversions.Jobs.FirstOrDefault(j => !before.Contains(j.Job.Id));
                    if (started?.Job.State is JobState.Completed or JobState.Failed) break;
                    await Task.Delay(20, TestContext.Current.CancellationToken);
                }
                Assert.NotNull(started);
                Assert.Equal(JobState.Completed, started.Job.State);
                Assert.Equal([inputs[1], inputs[2], inputs[0]], started.Job.Files.Select(f => f.SourcePath));
                using var merged = PdfReader.Open(Assert.Single(started.Job.Outputs), PdfDocumentOpenMode.Import);
                Assert.Equal([300d, 400d, 200d], merged.Pages.Cast<PdfPage>().Select(p => p.Width.Point));
            }
            Assert.All(inputs, path => Assert.True(File.Exists(path)));
        }
        finally
        {
            windows.MergeOrder?.Close();
            radial.Close(animate: false);
            profile.PresetIds = previous;
            preset.Output = previousOutput;
            Directory.Delete(folder, true);
        }
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ar")]
    [InlineData("es")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("zh-CN")]
    [InlineData("pl")]
    [InlineData("hi")]
    [InlineData("ru")]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    [InlineData("fr")]
    public void Chooser_fits_long_paths_at_its_minimum_width(string language)
    {
        TestServices.EnsureInitialized(language);
        var vm = new MergeOrderViewModel([
            @"C:\Documents\Quarterly reports\2026\This is a very long document name with spaces.pdf",
            @"C:\Documents\Another folder\This is a very long document name with spaces.pdf",
            @"C:\Documents\旅行の記録とملاحظات\中文-한국어-שלום.pdf"]);
        var window = new MergeOrderWindow { DataContext = vm, Width = 440, Height = 360 };
        try
        {
            window.Show();
            RenderTests.Pump();
            Rect Box(string name)
            {
                var control = window.FindControl<Control>(name)!;
                return new Rect(control.Bounds.Size).TransformToAABB(control.TransformToVisual(window)!.Value);
            }
            var (merge, cancel, down) = (Box("StartMerge"), Box("CancelMerge"), Box("MoveDown"));
            // Arabic mirrors the row: the arrows go to the right, Merge to the left edge.
            if (language == "ar")
            {
                Assert.True(merge.Right < down.Left);
                Assert.True(cancel.Left >= merge.Right);
            }
            else
            {
                Assert.True(merge.Left > down.Right);
                Assert.True(cancel.Right <= merge.Left);
            }
            Assert.True(merge.Right <= window.Bounds.Width && merge.Left >= 0);
            Assert.True(window.FindControl<ListBox>("FileOrder")!.Bounds.Height > 80);
            RenderTests.Save(window, $"merge-order-{language}.png");
        }
        finally { window.Close(); }
    }

    private static void ClickControl(Window window, string name)
    {
        var control = window.FindControl<Control>(name)!;
        var at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        RenderTests.Pump();
    }

    private static string Picture(string folder, string name, MagickColor colour, uint width, uint height)
    {
        using var image = new MagickImage(colour, width, height);
        var path = Path.Combine(folder, name);
        image.Write(path, MagickFormat.Png);
        return path;
    }

    /// <summary>
    /// "Merge images" (#645): the same chooser, with the layout below the list. The choices go into the merge and are
    /// remembered for the next one; Cancel starts nothing and remembers nothing.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Donut_merge_of_pictures_takes_the_layout_chosen_and_remembers_it(bool cancel)
    {
        TestServices.EnsureInitialized();
        var folder = Directory.CreateTempSubdirectory("filee-merge-images-").FullName;
        var store = AppHost.Get<UserDataStore>();
        var profile = store.Profiles.Single(p => p.Id == "images");
        var previous = profile.PresetIds;
        var preset = store.FindPreset("merge-images")!;
        var (previousOutput, previousImage) = (preset.Output, preset.Image.Clone());
        var conversions = AppHost.Get<ConversionService>();
        var before = conversions.Jobs.Select(j => j.Job.Id).ToHashSet();
        var radial = AppHost.Get<RadialController>();
        var windows = AppHost.Get<WindowService>();
        try
        {
            var wide = Picture(folder, "1 front.png", MagickColors.Red, 100, 40);
            var tall = Picture(folder, "2 back.png", MagickColors.Blue, 50, 80);
            profile.PresetIds = ["merge-images"];
            preset.Output = new()
            {
                Location = Filee.Core.Presets.OutputLocation.CustomFolder,
                CustomFolder = folder,
                FileNamePattern = previousOutput.FileNamePattern,
            };
            radial.ShowForFiles(450, 400, [tall, wide]);
            var donut = radial.Window!.DonutControl;
            Assert.True(donut.HandleKey(Key.Right));
            Assert.True(donut.HandleKey(Key.Enter));
            RenderTests.Pump();

            var dialog = Assert.IsType<MergeOrderWindow>(windows.MergeOrder);
            var vm = Assert.IsType<MergeOrderViewModel>(dialog.DataContext);
            Assert.True(vm.MergesImages);
            Assert.True(dialog.FindControl<Border>("MergeLayout")!.IsEffectivelyVisible);
            Assert.Equal("Merge images", dialog.StartMerge.Content);
            Assert.Equal([wide, tall], vm.OrderedFiles);
            // As the preset says: one under another, the same width, no gap, white.
            Assert.True(dialog.FindControl<RadioButton>("TopToBottom")!.IsChecked);
            Assert.True(dialog.FindControl<CheckBox>("SameWidth")!.IsEffectivelyVisible);
            Assert.False(dialog.FindControl<CheckBox>("SameHeight")!.IsEffectivelyVisible);

            ClickControl(dialog, "SideBySide");
            Assert.True(vm.SideBySide);
            Assert.False(dialog.FindControl<RadioButton>("TopToBottom")!.IsChecked);
            Assert.True(dialog.FindControl<CheckBox>("SameHeight")!.IsEffectivelyVisible);
            ClickControl(dialog, "SameHeight"); // keep their sizes, centred
            Assert.False(vm.SameSize);
            dialog.FindControl<NumericUpDown>("GapBox")!.Value = 6;
            RenderTests.Pump();
            Assert.Equal(6, vm.Gap);
            ClickControl(dialog, "Transparent");
            Assert.True(vm.Transparent);
            Assert.False(dialog.FindControl<ColorPicker>("BackgroundPicker")!.IsEffectivelyEnabled);
            ClickControl(dialog, cancel ? "CancelMerge" : "StartMerge");

            var saved = store.FindPreset("merge-images")!.Image;
            if (cancel)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
                Dispatcher.UIThread.RunJobs();
                Assert.DoesNotContain(conversions.Jobs, j => !before.Contains(j.Job.Id));
                Assert.Equal(Filee.Core.Presets.ImageJoinDirection.Vertical, saved.JoinDirection);
                Assert.Equal("#FFFFFF", saved.JoinBackground);
                return;
            }

            JobViewModel? started = null;
            for (var i = 0; i < 300; i++)
            {
                Dispatcher.UIThread.RunJobs();
                started = conversions.Jobs.FirstOrDefault(j => !before.Contains(j.Job.Id));
                if (started?.Job.State is JobState.Completed or JobState.Failed) break;
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
            Assert.NotNull(started);
            Assert.Equal(JobState.Completed, started.Job.State);
            var output = Assert.Single(started.Job.Outputs);
            Assert.Equal("1 front_merged.png", Path.GetFileName(output));
            using (var merged = new MagickImage(output))
            {
                // Side by side at their own sizes, 6 px apart; the low one centred on a see-through background.
                Assert.Equal((156u, 80u), (merged.Width, merged.Height));
                using var pixels = merged.GetPixels();
                Assert.Equal(0, pixels.GetPixel(50, 5).ToColor()!.A);
                Assert.Equal(MagickColors.Red, pixels.GetPixel(50, 40).ToColor());
                Assert.Equal(MagickColors.Blue, pixels.GetPixel(130, 5).ToColor());
            }

            // Remembered in the library, also on disk, for the next merge.
            Assert.Equal((Filee.Core.Presets.ImageJoinDirection.Horizontal, Filee.Core.Presets.ImageJoinFit.Original, 6, "transparent"),
                (saved.JoinDirection, saved.JoinFit, saved.JoinGap, saved.JoinBackground));
            var reloaded = new UserDataStore(store.Directory);
            reloaded.Load();
            Assert.Equal(Filee.Core.Presets.ImageJoinDirection.Horizontal, reloaded.FindPreset("merge-images")!.Image.JoinDirection);
            Assert.True(new MergeOrderViewModel([wide, tall], saved).SideBySide);
        }
        finally
        {
            windows.MergeOrder?.Close();
            radial.Close(animate: false);
            profile.PresetIds = previous;
            preset.Output = previousOutput;
            preset.Image = previousImage;
            store.SaveLibrary();
            Directory.Delete(folder, true);
        }
    }

    public static TheoryData<string, bool> MergeImagesLooks => new()
    {
        { "en", false }, { "en", true }, { "de", false }, { "de", true }, { "ar", false }, { "ar", true }, { "ko", false }, { "ko", true },
        { "ru", false }, { "hi", true }, { "ne", false }, { "th", true },
    };

    /// <summary>
    /// The layout of "Merge images" at the smallest size the window opens with for it: every choice whole and inside
    /// the window in long languages, mirrored in Arabic, in light and dark.
    /// </summary>
    [AvaloniaTheory]
    [MemberData(nameof(MergeImagesLooks))]
    public void Merge_images_layout_fits_at_its_minimum_size(string language, bool dark)
    {
        TestServices.EnsureInitialized(language);
        var theme = AppHost.Get<ThemeService>();
        theme.Apply(new ThemeSettings { Mode = dark ? ThemeMode.Dark : ThemeMode.Light }, language);
        var preset = Filee.Core.Presets.BuiltInData.CreatePresets().Single(p => p.Id == "merge-images");
        var vm = new MergeOrderViewModel([
            @"C:\Users\me\Pictures\Passport front.jpg",
            @"C:\Users\me\Pictures\Passport back.jpg",
            @"C:\Users\me\Downloads\Driving licence, both sides, scanned at 600 dpi.png"], preset.Image);
        var window = new MergeOrderWindow { DataContext = vm, Width = 440, Height = 540 };
        window.StartMerge.Content = AppHost.Get<ILocalizer>().DisplayName(preset);
        try
        {
            window.Show();
            RenderTests.Pump();
            Rect Box(string name)
            {
                var control = window.FindControl<Control>(name)!;
                return new Rect(control.Bounds.Size).TransformToAABB(control.TransformToVisual(window)!.Value);
            }
            var layout = Box("MergeLayout");
            string[] choices = ["TopToBottom", "SideBySide", "SameWidth", "GapBox", "BackgroundPicker", "BackgroundHex", "Transparent"];
            var boxes = choices.ToDictionary(name => name, Box);
            foreach (var (name, box) in boxes)
            {
                Assert.True(box.Width > 0 && box.Left >= layout.Left - 0.5 && box.Right <= layout.Right + 0.5 && box.Bottom <= layout.Bottom + 0.5,
                    $"{name} {box} outside {layout}");
                foreach (var (other, otherBox) in boxes.Where(b => b.Key != name))
                    Assert.False(box.Deflate(1).Intersects(otherBox.Deflate(1)), $"{name} {box} overlaps {other} {otherBox}");
            }
            // The checkbox text is shown whole (not cut at the window's edge).
            var sameWidth = window.FindControl<CheckBox>("SameWidth")!;
            Assert.True(sameWidth.Bounds.Width >= sameWidth.DesiredSize.Width - 0.5);
            // Arabic mirrors the switch: "top to bottom" comes first, on the right.
            if (language == "ar")
                Assert.True(boxes["TopToBottom"].Left > boxes["SideBySide"].Left);
            else
                Assert.True(boxes["TopToBottom"].Right <= boxes["SideBySide"].Left + 0.5);
            Assert.True(window.FindControl<ListBox>("FileOrder")!.Bounds.Height > 60);
            Assert.True(Box("StartMerge").Right <= window.Bounds.Width && Box("StartMerge").Left >= 0);
            RenderTests.Save(window, $"merge-images-{language}-{(dark ? "dark" : "light")}.png");

            // The other choices: side by side, see-through.
            vm.SideBySide = true;
            vm.Transparent = true;
            RenderTests.Pump();
            Assert.True(window.FindControl<CheckBox>("SameHeight")!.IsEffectivelyVisible);
            RenderTests.Save(window, $"merge-images-{language}-{(dark ? "dark" : "light")}-side-by-side.png");
        }
        finally
        {
            window.Close();
            theme.Apply(new ThemeSettings(), language);
        }
    }

    [Fact]
    public void The_layout_chosen_is_written_into_the_merge_and_other_merges_show_none()
    {
        var image = new Filee.Core.Presets.ImageOptions { JoinGap = 30, JoinBackground = "#102030" };
        var vm = new MergeOrderViewModel(["a.png", "b.png"], image);
        Assert.True(vm.MergesImages);
        Assert.Equal((false, true, 30, Avalonia.Media.Color.Parse("#102030")), (vm.SideBySide, vm.SameSize, vm.Gap, vm.BackgroundColor));

        vm.TopToBottom = false;
        vm.SameSize = false;
        vm.Gap = 5000;
        vm.BackgroundColor = Avalonia.Media.Colors.Black;
        vm.ApplyTo(image);
        Assert.Equal((Filee.Core.Presets.ImageJoinDirection.Horizontal, Filee.Core.Presets.ImageJoinFit.Original, Filee.Core.Presets.ImageOptions.MaxJoinGap, "#000000"),
            (image.JoinDirection, image.JoinFit, image.JoinGap, image.JoinBackground));
        vm.BackgroundHex = "#336699";
        Assert.Equal(Avalonia.Media.Color.Parse("#336699"), vm.BackgroundColor);
        vm.ApplyTo(image);
        Assert.Equal("#336699", image.JoinBackground);
        vm.BackgroundColor = Avalonia.Media.Colors.Red;
        Assert.Equal("#FF0000", vm.BackgroundHex);
        vm.Transparent = true;
        Assert.False(vm.PicksColor);
        vm.ApplyTo(image);
        Assert.Equal("transparent", image.JoinBackground);

        Assert.False(new MergeOrderViewModel(["a.pdf", "b.pdf"]).MergesImages);
        // A colour that isn't one (a hand-edited file) shows white.
        Assert.Equal(Avalonia.Media.Colors.White, new MergeOrderViewModel(["a.png"], new() { JoinBackground = "blue-ish" }).BackgroundColor);
    }

    [Fact]
    public void Files_start_in_name_order_counting_numbers_and_browser_files_show_no_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "filee-merge-names");
        string In(string folder, string name) => Path.Combine(root, folder, name);
        var browser = Path.Combine(Filee.Core.Web.WebDrop.DefaultWorkRoot, "abc", "drop", "1", "page 1.pdf");
        var vm = new MergeOrderViewModel([In("b", "page 10.pdf"), In("a", "page 2.pdf"), browser, In("a", "page 10.pdf")]);
        Assert.Equal([browser, In("a", "page 2.pdf"), In("a", "page 10.pdf"), In("b", "page 10.pdf")], vm.OrderedFiles);
        Assert.Equal("", vm.Files[0].Folder);
        Assert.Equal(Path.Combine(root, "a"), vm.Files[1].Folder);
        Assert.False(vm.MoveUpCommand.CanExecute(null));
        Assert.True(vm.MoveDownCommand.CanExecute(null));
    }
}
