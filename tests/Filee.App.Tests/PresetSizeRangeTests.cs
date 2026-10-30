// A picture's file size range in the preset editor (#662): both ends in KB or MB, kept in megabytes in presets.json, a
// minimum over the maximum not saved while it can be seen, and a picture under the minimum noted on the result.

using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public class PresetSizeRangeTests
{
    private static void Pump()
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>Opens <paramref name="preset"/> in the editor window and hands it to <paramref name="use"/>.</summary>
    private static void InEditor(Preset preset, Action<PresetEditorViewModel, Window> use)
    {
        var editor = new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>());
        var window = new PresetEditorWindow { DataContext = editor, Height = 900 };
        window.Show();
        Pump();
        try
        {
            use(editor, window);
        }
        finally
        {
            window.Close();
        }
    }

    private static NumericUpDown Box(Window window, string name) =>
        window.GetVisualDescendants().OfType<NumericUpDown>().Single(b => b.Name == name);

    private static Choice<FileSizeUnit> Unit(PresetEditorViewModel editor, FileSizeUnit unit) => editor.SizeUnits.Single(u => u.Value == unit);

    [AvaloniaFact]
    public void Kilobytes_are_saved_as_megabytes_and_read_back_in_kilobytes()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-size-range-").FullName;
        try
        {
            var preset = new Preset { Id = "size-range", Name = "Upload", TargetFormat = "jpg" };
            InEditor(preset, (editor, window) =>
            {
                var unit = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ImageSizeUnit");
                Assert.Equal(FileSizeUnit.MB, ((Choice<FileSizeUnit>)unit.SelectedItem!).Value);
                unit.SelectedItem = Unit(editor, FileSizeUnit.KB);
                Box(window, "ImageMinSize").Value = 50;
                Pump();
                Assert.True(editor.Apply());
            });
            Assert.Equal(0.05, preset.Image.MinSizeMb);

            var store = new UserDataStore(folder);
            store.Load();
            store.Presets.Add(preset);
            store.SaveLibrary();
            var reloaded = new UserDataStore(folder);
            reloaded.Load();
            var read = reloaded.FindPreset("size-range")!;
            Assert.Equal(0.05, read.Image.MinSizeMb);
            InEditor(read, (editor, window) =>
            {
                Assert.Equal(FileSizeUnit.KB, editor.ImageSizeUnit.Value);
                Assert.Equal("50", Box(window, "ImageMinSize").Text);

                // A part of a kilobyte shows as it is and is kept.
                Box(window, "ImageMinSize").Value = 50.5m;
                Pump();
                Assert.Equal("50.5", Box(window, "ImageMinSize").Text);
                Assert.True(editor.Apply());
            });
            Assert.Equal(0.0505, read.Image.MinSizeMb);

            // A presets.json from before the minimum: none.
            var path = Path.Combine(folder, "presets.json");
            File.WriteAllText(path, Regex.Replace(File.ReadAllText(path), "\"MinSizeMb\":\\s*[^,}\\s]+,?", "", RegexOptions.IgnoreCase));
            var older = new UserDataStore(folder);
            older.Load();
            Assert.Equal(0, older.FindPreset("size-range")!.Image.MinSizeMb);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [AvaloniaTheory]
    [InlineData(0.5, FileSizeUnit.KB, "500")]
    [InlineData(2, FileSizeUnit.MB, "2")]
    [InlineData(0.1000006, FileSizeUnit.KB, "100.0006")]
    public void A_saved_limit_opens_in_its_unit_and_is_saved_unchanged(double megabytes, FileSizeUnit unit, string shown)
    {
        TestServices.EnsureInitialized("en");
        var preset = new Preset { Name = "Upload", TargetFormat = "jpg", Image = { MaxSizeMb = megabytes } };
        InEditor(preset, (editor, window) =>
        {
            Assert.Equal(unit, editor.ImageSizeUnit.Value);
            Assert.Equal(shown, Box(window, "ImageMaxSize").Text);

            // To the other unit and back: the same size.
            var other = unit == FileSizeUnit.KB ? FileSizeUnit.MB : FileSizeUnit.KB;
            editor.ImageSizeUnit = Unit(editor, other);
            Assert.Equal((decimal)megabytes * (other == FileSizeUnit.KB ? 1000 : 1), editor.ImageMaxSize);
            editor.ImageSizeUnit = Unit(editor, unit);
            Assert.True(editor.Apply());
        });
        Assert.Equal(megabytes, preset.Image.MaxSizeMb);
    }

    [AvaloniaFact]
    public void The_boxes_go_up_to_4096_mb_in_either_unit_and_a_change_of_unit_cuts_nothing()
    {
        TestServices.EnsureInitialized("en");
        var preset = new Preset { Name = "Upload", TargetFormat = "jpg" };
        InEditor(preset, (editor, window) =>
        {
            var box = Box(window, "ImageMaxSize");
            Assert.Equal(4096m, box.Maximum);
            editor.ImageSizeUnit = Unit(editor, FileSizeUnit.KB);
            Pump();
            Assert.Equal(4_096_000m, box.Maximum);

            // 5,000 KB is over 4096 as a number: shown and kept as 5 MB, not cut to 4096 on the way.
            box.Value = 5000;
            Pump();
            editor.ImageSizeUnit = Unit(editor, FileSizeUnit.MB);
            Pump();
            Assert.Equal((5m, 4096m), (box.Value, box.Maximum));
            editor.ImageSizeUnit = Unit(editor, FileSizeUnit.KB);
            Pump();
            Assert.Equal(5000m, box.Value);
            Assert.True(editor.Apply());
        });
        Assert.Equal(5, preset.Image.MaxSizeMb);
    }

    [AvaloniaFact]
    public void Each_end_can_be_set_alone_or_cleared()
    {
        TestServices.EnsureInitialized("en");
        foreach (var (min, max) in new (decimal?, decimal?)[] { (60, null), (null, 500), (60, 500), (null, null), (0, 0) })
        {
            var preset = new Preset { Name = "Upload", TargetFormat = "jpg" };
            InEditor(preset, (editor, _) =>
            {
                editor.ImageSizeUnit = Unit(editor, FileSizeUnit.KB);
                (editor.ImageMinSize, editor.ImageMaxSize) = (min, max);
                Assert.True(editor.Apply());
            });
            Assert.Equal(((double)(min ?? 0) / 1000, (double)(max ?? 0) / 1000), (preset.Image.MinSizeMb, preset.Image.MaxSizeMb));
            InEditor(preset, (editor, _) =>
                Assert.Equal((min ?? 0, max ?? 0), (editor.ImageMinSize ?? 0, editor.ImageMaxSize ?? 0)));
        }
    }

    [AvaloniaFact]
    public void A_minimum_over_the_maximum_is_not_saved_while_it_can_be_seen()
    {
        TestServices.EnsureInitialized("en");
        var preset = new Preset { Name = "Upload", TargetFormat = "jpg" };
        InEditor(preset, (editor, window) =>
        {
            var save = window.GetVisualDescendants().OfType<Button>().Single(b => b.IsDefault);
            editor.ImageSizeUnit = Unit(editor, FileSizeUnit.KB);
            (editor.ImageMinSize, editor.ImageMaxSize) = (600, 500);
            Pump();
            Assert.NotNull(editor.SizeRangeError);
            Assert.False(editor.IsValid);
            Assert.False(save.IsEnabled);
            Assert.False(editor.Apply());

            // A format without a quality hides the boxes: nothing in the way, and no limit saved.
            editor.Target = editor.Targets.Single(t => t.Value == "png");
            Pump();
            Assert.True(editor.IsValid);
            Assert.True(save.IsEnabled);
            Assert.True(editor.Apply());
            Assert.Equal((0.0, 0.0), (preset.Image.MinSizeMb, preset.Image.MaxSizeMb));

            editor.Target = editor.Targets.Single(t => t.Value == "jpg");
            (editor.ImageMinSize, editor.ImageMaxSize) = (600, 500);
            Pump();
            Assert.False(editor.IsValid);

            // A Live / Motion Photo hides the image card: savable, without the minimum; the maximum as before.
            editor.LivePhotoChoice = editor.LivePhotoKinds.First(k => k.Value != LivePhotoKind.None);
            Pump();
            Assert.True(editor.IsValid);
            Assert.True(save.IsEnabled);
            Assert.True(editor.Apply());
            Assert.Equal((0.0, 0.5), (preset.Image.MinSizeMb, preset.Image.MaxSizeMb));

            editor.LivePhotoChoice = editor.LivePhotoKinds.Single(k => k.Value == LivePhotoKind.None);
            Pump();
            Assert.False(editor.IsValid);
            Assert.NotNull(editor.SizeRangeError);
        });
    }

    [AvaloniaFact]
    public void A_picture_under_the_minimum_is_noted_on_the_result_on_its_own()
    {
        TestServices.EnsureInitialized("en");
        var conversion = new ConversionJob(["C:/photo.jpg", "C:/other.jpg"], new Preset { TargetFormat = "jpg" }, "Upload");
        conversion.Files[0].State = FileState.Done;
        conversion.Files[0].UnderMinimumSize = true;
        conversion.Files[1].State = FileState.Done;
        var loc = AppHost.Get<ILocalizer>();
        var job = new JobViewModel(conversion, loc, AppHost.Get<IPlatformServices>(), _ => { });

        Assert.True(job.HasNotices);
        Assert.Equal($"photo.jpg — {loc["notice.under_min_size"]}", job.Notices);
    }
}
