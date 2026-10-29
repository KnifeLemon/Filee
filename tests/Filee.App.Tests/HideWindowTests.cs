// The "Pixelate / blur" window: the empty picture's hint, frames showing the picture already pixelated or blurred, the
// strip of several files and the end summary (screenshots in light, dark, Material You and a theme.json, short, long
// and right-to-left languages), drawing and dragging frames with the mouse, its keys, the conversion it starts and the
// choice it remembers.

using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using ImageMagick;
using ImageMagick.Drawing;
using static Filee.App.Tests.RenderTests;

namespace Filee.App.Tests;

public class HideWindowTests
{
    public static TheoryData<string, string> Looks => new()
    {
        { "en", "light" }, { "en", "dark" }, { "ko", "light" }, { "ko", "dark" }, { "de", "light" }, { "de", "dark" },
        { "ar", "light" }, { "ar", "dark" }, { "ru", "light" }, { "hi", "light" }, { "ne", "dark" }, { "ja", "light" },
        { "en", "material" }, { "en", "custom" },
    };

    /// <summary>The frames of the screenshots: a name, a phone number, a face.</summary>
    private static readonly Rect[] Frames = [new(0.062, 0.148, 0.16, 0.07), new(0.07, 0.62, 0.38, 0.08), new(0.63, 0.19, 0.24, 0.39)];

    [AvaloniaTheory]
    [MemberData(nameof(Looks))]
    public void A_picture_shows_the_hint_then_its_frames_pixelated_or_blurred(string language, string look)
    {
        WithWindow(language, look, ["chat.png"], (window, vm, suffix, _) =>
        {
            Assert.False(vm.HasMultipleFiles);
            Assert.Equal(AppHost.Get<ILocalizer>()["hide.save"], vm.SaveText);
            Assert.True(vm.ShowEmptyHint);
            Assert.False(vm.CanSave);
            Assert.False(vm.SaveCommand.CanExecute(null));
            Settle(window);
            Save(window, $"hide-empty-{suffix}.png");

            vm.Strength = 5;
            vm.SetStyle(HideStyle.Pixelate);
            foreach (var frame in Frames)
                vm.Areas.Add(frame);
            vm.Areas.Select(1);
            WaitForCover(vm);
            Settle(window);
            Assert.False(vm.ShowEmptyHint);
            Assert.True(vm.CanSave);
            Save(window, $"hide-pixelate-{suffix}.png");

            vm.SetStyle(HideStyle.Blur);
            vm.Strength = 7;
            vm.Areas.Select(2);
            WaitForCover(vm);
            Settle(window);
            Save(window, $"hide-blur-{suffix}.png");
        });
    }

    [AvaloniaTheory]
    [InlineData("en", "light")]
    [InlineData("en", "dark")]
    [InlineData("de", "light")]
    [InlineData("ar", "dark")]
    [InlineData("ko", "light")]
    [InlineData("ru", "dark")]
    public void Several_files_show_a_strip_and_the_frames_carry_on_to_the_next(string language, string look)
    {
        WithWindow(language, look, ["one.png", "two.png", "spinner.gif", "four.png"], (window, vm, suffix, exported) =>
        {
            Assert.True(vm.HasMultipleFiles);
            Assert.Equal(AppHost.Get<ILocalizer>()["hide.save_next"], vm.SaveText);

            vm.Areas.Add(Frames[0]);
            vm.Areas.Add(Frames[2]);
            _ = vm.SaveAsync();
            WaitFor(() => vm.CurrentIndex == 1 && !vm.IsBusy && vm.CoverImage is not null);

            // The second picture starts with the first one's frames, none selected.
            Assert.Equal(2, vm.Areas.Count);
            AssertClose(Frames[0], vm.Areas.Areas[0]);
            AssertClose(Frames[2], vm.Areas.Areas[1]);
            Assert.Equal(-1, vm.Areas.Selected);
            Assert.False(vm.CanUndo);
            vm.Areas.Select(0);
            WaitForCover(vm);
            Settle(window);
            Save(window, $"hide-multi-{suffix}.png");

            // Skipped; the animated GIF can't be opened and fails on its own; the last one is saved.
            _ = vm.SkipAsync();
            WaitFor(() => vm.CurrentIndex == 3 && !vm.IsBusy && vm.PreviewImage is not null);
            Assert.Equal(
                [CropItemState.Done, CropItemState.Skipped, CropItemState.Failed, CropItemState.Current],
                vm.Queue.Select(q => q.State));
            _ = vm.SaveAsync();
            WaitFor(() => vm.IsFinished);
            Assert.Equal((2, 1, 1), (vm.DoneCount, vm.SkippedCount, vm.FailedCount));
            Assert.Equal(2, exported.Count);
            Settle(window);
            Save(window, $"hide-summary-{suffix}.png");
        });
    }

    [AvaloniaFact]
    public void Dragging_on_the_picture_draws_a_frame_that_moves_sizes_and_shows_the_cover_at_once()
    {
        WithWindow("en", "light", ["chat.png"], (window, vm, _, _) =>
        {
            var canvas = window.HideCanvas;
            var picture = canvas.CalculateImageBounds();
            var origin = (Vector)canvas.TranslatePoint(default, window)!.Value;
            Point At(double x, double y) => new Point(picture.X + picture.Width * x, picture.Y + picture.Height * y) + origin;

            // A click beside every frame draws nothing.
            window.MouseDown(At(0.5, 0.5), MouseButton.Left);
            window.MouseUp(At(0.5, 0.5), MouseButton.Left);
            Assert.Equal(0, vm.Areas.Count);

            // A drag draws one, selected, already covered while the button is still down.
            window.MouseDown(At(0.1, 0.1), MouseButton.Left);
            window.MouseMove(At(0.2, 0.15));
            window.MouseMove(At(0.4, 0.3));
            Pump();
            Assert.Equal(1, vm.Areas.Count);
            Assert.Equal(0, vm.Areas.Selected);
            WaitForCover(vm);
            Settle(window);
            Save(window, "hide-drag-en.png");
            window.MouseUp(At(0.4, 0.3), MouseButton.Left);
            AssertClose(new Rect(0.1, 0.1, 0.3, 0.2), vm.Areas.Areas[0]);

            // Moved by its middle, sized by its corner; each drag is one undo step.
            window.MouseDown(At(0.25, 0.2), MouseButton.Left);
            window.MouseMove(At(0.3, 0.25));
            window.MouseMove(At(0.35, 0.3));
            window.MouseUp(At(0.35, 0.3), MouseButton.Left);
            AssertClose(new Rect(0.2, 0.2, 0.3, 0.2), vm.Areas.Areas[0]);
            var corner = At(0.5, 0.4);
            window.MouseDown(corner, MouseButton.Left);
            window.MouseMove(corner + new Vector(10, 10));
            window.MouseMove(At(0.6, 0.5));
            window.MouseUp(At(0.6, 0.5), MouseButton.Left);
            AssertClose(new Rect(0.2, 0.2, 0.4, 0.3), vm.Areas.Areas[0]);
            vm.Undo();
            AssertClose(new Rect(0.2, 0.2, 0.3, 0.2), vm.Areas.Areas[0]);
            vm.Undo();
            AssertClose(new Rect(0.1, 0.1, 0.3, 0.2), vm.Areas.Areas[0]);
            vm.Redo();
            AssertClose(new Rect(0.2, 0.2, 0.3, 0.2), vm.Areas.Areas[0]);

            // A second frame; a click on the first selects it again, a click beside both selects none.
            window.MouseDown(At(0.7, 0.7), MouseButton.Left);
            window.MouseMove(At(0.8, 0.8));
            window.MouseMove(At(0.9, 0.9));
            window.MouseUp(At(0.9, 0.9), MouseButton.Left);
            Assert.Equal((2, 1), (vm.Areas.Count, vm.Areas.Selected));
            window.MouseDown(At(0.3, 0.3), MouseButton.Left);
            window.MouseUp(At(0.3, 0.3), MouseButton.Left);
            Assert.Equal(0, vm.Areas.Selected);
            window.MouseDown(At(0.05, 0.9), MouseButton.Left);
            window.MouseUp(At(0.05, 0.9), MouseButton.Left);
            Assert.Equal(-1, vm.Areas.Selected);
            Assert.False(vm.HasSelection);

            // Under the pointer a frame that isn't selected shows its full outline.
            vm.Areas.Select(1);
            window.MouseMove(At(0.3, 0.3));
            Settle(window);
            Save(window, "hide-hover-en.png");
        });
    }

    [AvaloniaFact]
    public void Keys_pick_move_remove_and_undo_frames_change_the_cover_save_and_close()
    {
        WithWindow("en", "light", ["chat.png"], (window, vm, _, exported) =>
        {
            // Without frames Enter saves nothing, and Tab leaves the frames alone.
            Press(window, Key.Enter);
            Assert.Empty(exported);

            vm.Areas.Add(new Rect(0.25, 0.25, 0.5, 0.5)); // 400, 250, 800 × 500 of 1600 × 1000
            vm.Areas.Add(new Rect(0.1, 0.1, 0.1, 0.1));
            vm.Areas.Select(-1);

            // Tab goes from frame to frame, Shift+Tab back.
            Press(window, Key.Tab);
            Assert.Equal(0, vm.Areas.Selected);
            Press(window, Key.Tab);
            Assert.Equal(1, vm.Areas.Selected);
            Press(window, Key.Tab);
            Assert.Equal(0, vm.Areas.Selected);
            Press(window, Key.Tab, RawInputModifiers.Shift);
            Assert.Equal(1, vm.Areas.Selected);
            Press(window, Key.Tab, RawInputModifiers.Shift);
            Assert.Equal(0, vm.Areas.Selected);

            // Arrows move it by pixels of the picture, Shift by ten.
            Press(window, Key.Right);
            Press(window, Key.Right);
            Press(window, Key.Down, RawInputModifiers.Shift);
            Assert.Equal(new Rect(402 / 1600.0, 260 / 1000.0, 0.5, 0.5), vm.Areas.Areas[0]);

            // Delete removes it, Ctrl+Z brings it back, Ctrl+Y removes it again.
            Press(window, Key.Delete);
            Assert.Equal(1, vm.Areas.Count);
            Press(window, Key.Z, RawInputModifiers.Control);
            Assert.Equal(2, vm.Areas.Count);
            Press(window, Key.Y, RawInputModifiers.Control);
            Assert.Equal(1, vm.Areas.Count);
            Press(window, Key.Z, RawInputModifiers.Control);

            // R clears every frame, undoably.
            Press(window, Key.R);
            Assert.Equal(0, vm.Areas.Count);
            Press(window, Key.Z, RawInputModifiers.Control);
            Assert.Equal(2, vm.Areas.Count);

            // P, B and + / − choose the cover.
            Press(window, Key.B);
            Assert.Equal(HideStyle.Blur, vm.Style);
            Press(window, Key.P);
            Assert.Equal(HideStyle.Pixelate, vm.Style);
            var strength = vm.Strength;
            Press(window, Key.OemPlus);
            Assert.Equal(strength + 1, vm.Strength);
            Press(window, Key.OemMinus);
            Press(window, Key.OemMinus);
            Assert.Equal(strength - 1, vm.Strength);
            for (var i = 0; i < 20; i++)
                Press(window, Key.OemMinus);
            Assert.Equal(HideTool.MinStrength, vm.Strength);

            // Enter saves: one conversion of the picture with both frames, as chosen, and the window closes.
            vm.SetStyle(HideStyle.Blur);
            vm.Strength = 8;
            var closed = false;
            window.Closed += (_, _) => closed = true;
            Press(window, Key.Enter);
            WaitFor(() => closed);
            var (path, conversion) = Assert.Single(exported);
            Assert.EndsWith("chat.png", path);
            Assert.Equal(PresetKind.Convert, conversion.Kind);
            Assert.Equal(BuiltInData.SameAsSource, conversion.TargetFormat);
            Assert.Equal(HideStyle.Blur, conversion.Image.HideStyle);
            Assert.Equal(8, conversion.Image.HideStrength);
            Assert.Equal(2, conversion.Image.HiddenAreas!.Count);
            Assert.Equal(402 / 1600.0, conversion.Image.HiddenAreas[0].X, 6);
        });
    }

    [AvaloniaFact]
    public void Escape_closes_without_saving_and_the_choice_is_remembered_in_the_tool()
    {
        WithWindow("en", "light", ["chat.png"], (window, vm, _, exported) =>
        {
            vm.Areas.Add(Frames[0]);
            vm.SetStyle(HideStyle.Blur);
            vm.Strength = 9;
            var closed = false;
            window.Closed += (_, _) => closed = true;
            Press(window, Key.Escape);
            Assert.True(closed);
            Assert.Empty(exported);
            var saved = AppHost.Get<UserDataStore>().FindPreset("tool-hide")!;
            Assert.Equal((HideStyle.Blur, 9), (saved.Image.HideStyle, saved.Image.HideStrength));
            Assert.Null(saved.Image.HiddenAreas);
        });

        // The next window opens with it.
        WithWindow("en", "light", ["chat.png"], (_, vm, _, _) => Assert.Equal((HideStyle.Blur, 9), (vm.Style, vm.Strength)),
            resetTool: false);
        var store = AppHost.Get<UserDataStore>();
        HideTool.Remember(store.FindPreset("tool-hide")!, HideStyle.Pixelate, HideTool.DefaultStrength);
        store.SaveLibrary();
    }

    [AvaloniaTheory]
    [InlineData("de", "light")]
    [InlineData("ru", "dark")]
    [InlineData("ne", "light")]
    [InlineData("ar", "light")]
    [InlineData("en", "light")]
    public void At_its_smallest_the_toolbar_still_fits_the_window(string language, string look)
    {
        WithWindow(language, look, ["one.png", "two.png"], (window, vm, suffix, _) =>
        {
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            vm.Areas.Add(Frames[0]);
            WaitForCover(vm);
            Settle(window);
            // Every button of the toolbar is whole inside the window (also in a right-to-left one), Hide & Next too.
            var buttons = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "Toolbar")
                .GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
            Assert.Contains(buttons, b => b.Name == "PrimaryButton");
            foreach (var button in buttons)
            {
                var a = button.TranslatePoint(default, window)!.Value;
                var b = button.TranslatePoint(new Point(button.Bounds.Width, button.Bounds.Height), window)!.Value;
                var bounds = new Rect(a, b).Normalize();
                Assert.True(bounds.Left >= 12 && bounds.Right <= window.Bounds.Width - 12, $"{button.Name}: {bounds} outside {window.Bounds}");
            }
            AssertFittedLevel(window);
            Assert.NotNull(window.ToolbarLevel); // two files' names never fit 680 px, in any font
            Save(window, $"hide-narrow-{suffix}.png");

            // The window as it opens: as many names as fit there with this system's fonts.
            window.Width = 1040;
            Settle(window);
            AssertFittedLevel(window);

            // Wide: every name is back.
            window.Width = 1900;
            Settle(window);
            AssertFittedLevel(window);
            Assert.Null(window.ToolbarLevel);
            Assert.DoesNotContain("tight", window.Classes);
            Assert.DoesNotContain("compact", window.Classes);
        });
    }

    /// <summary>
    /// The toolbar gave up no more names than it had to: every level before the one in use is wider than the room, and the
    /// one in use fits (the last fits by giving up all it can). Measured, not assumed: fonts differ between systems.
    /// </summary>
    private static void AssertFittedLevel(HideWindow window)
    {
        var level = window.ToolbarLevel;
        var room = window.ToolbarRoom;
        var at = Array.IndexOf(HideWindow.ToolbarLevels, level);
        var widths = HideWindow.ToolbarLevels.Select(window.ToolbarWidthAt).ToList();
        window.FitToolbar();
        Assert.Equal(level, window.ToolbarLevel);
        var detail = $"room {room:0}, widths {string.Join(" / ", widths.Select(w => w.ToString("0")))}, level {level ?? "none"}";
        for (var i = 0; i < at; i++)
            Assert.True(widths[i] > room, detail);
        Assert.True(at == HideWindow.ToolbarLevels.Length - 1 || widths[at] <= room, detail);
        Assert.Equal(level is "tight" or "compact", window.Classes.Contains("tight"));
        Assert.Equal(level == "compact", window.Classes.Contains("compact"));
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static void AssertClose(Rect expected, Rect actual)
    {
        Assert.Equal(expected.X, actual.X, 2);
        Assert.Equal(expected.Y, actual.Y, 2);
        Assert.Equal(expected.Width, actual.Width, 2);
        Assert.Equal(expected.Height, actual.Height, 2);
    }

    private static void WithWindow(string language, string look, string[] names,
        Action<HideWindow, HideWindowViewModel, string, List<(string Path, Preset Conversion)>> act, bool resetTool = true)
    {
        TestServices.EnsureInitialized(language);
        var theme = AppHost.Get<ThemeService>();
        switch (look)
        {
            case "dark":
                theme.Apply(new ThemeSettings { Mode = ThemeMode.Dark }, language);
                break;
            case "material":
                theme.Apply(new ThemeSettings { Style = ThemeStyle.MaterialYou, Accent = "#2E7D5B" }, language);
                break;
            case "custom":
                theme.Apply(new ThemeSettings { Mode = ThemeMode.Dark }, language, new CustomTheme
                {
                    Dark = { ["page"] = "#101820", ["surface"] = "#18222C", ["surfaceAlt"] = "#22303C", ["text"] = "#E8E4D8" },
                });
                break;
        }
        var store = AppHost.Get<UserDataStore>();
        var tool = store.FindPreset("tool-hide");
        if (tool is null)
        {
            tool = BuiltInData.HidePresets().Single();
            store.Presets.Add(tool);
        }
        // Each test starts from the built-in choice, unless it looks at what the last one kept.
        if (resetTool)
            HideTool.Remember(tool, HideStyle.Pixelate, HideTool.DefaultStrength);
        var folder = Directory.CreateTempSubdirectory("filee-hide-").FullName;
        HideWindow? window = null;
        var exported = new List<(string, Preset)>();
        try
        {
            var files = names.Select(name => Picture(folder, name)).ToList();
            var vm = new HideWindowViewModel(files, tool, AppHost.Get<ILocalizer>(), store,
                _ => BuiltInData.SameAsSource, (path, conversion) => exported.Add((path, conversion)));
            window = new HideWindow { DataContext = vm, Width = 1040, Height = 720 };
            theme.ApplyWindowClass(window);
            window.Show();
            WaitFor(() => vm.PreviewImage is not null && !vm.IsBusy);
            WaitForCover(vm);
            Pump();
            window.HideCanvas.FinishAnimations();
            act(window, vm, $"{language}-{look}", exported);
        }
        finally
        {
            if (window?.IsVisible == true)
                window.Close();
            if (look != "light")
                theme.Apply(new ThemeSettings(), language);
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// A 1600 × 1000 chat screenshot: a name, a message, a phone number and a face-like circle; or a two-frame GIF
    /// ("…gif").
    /// </summary>
    internal static string Picture(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        if (name.EndsWith(".gif", StringComparison.Ordinal))
        {
            using var frames = new MagickImageCollection();
            frames.Add(new MagickImage(MagickColors.Coral, 40, 40));
            frames.Add(new MagickImage(MagickColors.Teal, 40, 40));
            frames.Write(path, MagickFormat.Gif);
            return path;
        }
        var hue = name.Sum(c => c) % 3;
        string[] skies = ["#F4F1FB-#E3ECFA", "#FDF4EC-#F6E7F1", "#EEF7F1-#E2EEF8"];
        using var image = new MagickImage($"gradient:{skies[hue]}", 1600, 1000);
        var draw = new Drawables()
            .FillColor(new MagickColor("#FFFFFF")).RoundRectangle(80, 110, 760, 260, 28, 28)
            .FillColor(new MagickColor("#7F77DD")).RoundRectangle(80, 560, 860, 740, 28, 28)
            .FillColor(new MagickColor("#F2C9A0")).Circle(1200, 380, 1200, 560)
            .FillColor(new MagickColor("#3A3530")).Circle(1135, 350, 1135, 372).Circle(1265, 350, 1265, 372)
            .StrokeColor(new MagickColor("#3A3530")).StrokeWidth(10).FillColor(MagickColors.Transparent)
            .Arc(1120, 380, 1280, 480, 20, 160)
            .StrokeWidth(0);
        draw.Draw(image);
        try
        {
            new Drawables().FontPointSize(54).FillColor(new MagickColor("#2B2930"))
                .Text(110, 205, "Alex Kim")
                .FontPointSize(40).FillColor(new MagickColor("#4A4650")).Text(110, 250, "See you at 7, near the station")
                .FontPointSize(58).FillColor(new MagickColor("#FFFFFF")).Text(120, 680, "+82 10-1234-5678")
                .Draw(image);
        }
        catch (MagickException)
        {
            // No font here: bars in place of the text are enough to see the frames.
            new Drawables().FillColor(new MagickColor("#2B2930")).Rectangle(110, 160, 400, 210)
                .FillColor(new MagickColor("#FFFFFF")).Rectangle(120, 630, 700, 690).Draw(image);
        }
        image.Write(path, MagickFormat.Png);
        return path;
    }

    private static void WaitForCover(HideWindowViewModel vm) => WaitFor(() => vm.IsCoverCurrent);

    private static void Settle(HideWindow window)
    {
        Pump();
        window.SettleForCapture();
        Pump();
    }

    private static void Press(HideWindow window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.True(condition());
    }
}
