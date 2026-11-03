// The "Annotate" window through the mouse and keyboard: each tool draws its mark where dragged or clicked, a click that
// drew nothing leaves nothing, numbers count up, Select moves and resizes, colour and width restyle the selected mark,
// text is typed in place, undo / redo / delete work, Save starts the conversion with the marks, and what the window
// draws is what the file gets.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
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

namespace Filee.App.Tests;

public sealed class AnnotateWindowTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("filee-annotate-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private string Picture(string name, uint width = 800, uint height = 500)
    {
        var path = Path.Combine(_folder, name);
        using var image = new MagickImage(new MagickColor("#FFFFFF"), width, height);
        new ImageMagick.Drawing.Drawables()
            .FillColor(new MagickColor("#F3F3F5")).Rectangle(0, 0, 800, 42)
            .FillColor(new MagickColor("#3E63DD")).RoundRectangle(560, 400, 760, 460, 10, 10)
            .FillColor(new MagickColor("#ECECEC")).Rectangle(40, 120, 560, 134)
            .Draw(image);
        image.Write(path, MagickFormat.Png);
        return path;
    }

    private (AnnotateWindow Window, AnnotateViewModel Vm, List<(string Path, Preset Conversion)> Exported) Open(int files = 1, uint width = 800, uint height = 500)
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var tool = store.FindPreset(BuiltInData.AnnotateId) ?? BuiltInData.AnnotatePresets().Single();
        AnnotateTool.Remember(tool, AnnotateTool.Colors[0], AnnotationWidth.Medium);
        var exported = new List<(string, Preset)>();
        var paths = Enumerable.Range(1, files).Select(i => Picture($"shot{i}.png", width, height)).ToList();
        var vm = new AnnotateViewModel(paths, tool, AppHost.Get<ILocalizer>(), store, _ => BuiltInData.SameAsSource,
            (path, conversion) => exported.Add((path, conversion)));
        var window = new AnnotateWindow { DataContext = vm, Width = 1040, Height = 720 };
        window.Show();
        Wait(() => vm.PreviewImage is not null && !vm.IsBusy);
        RenderTests.Pump();
        return (window, vm, exported);
    }

    private static void Wait(Func<bool> done)
    {
        for (var i = 0; i < 400 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(done());
    }

    /// <summary>The window point of (x, y) in parts of the picture.</summary>
    private static Point On(AnnotateWindow window, double x, double y)
    {
        var picture = window.AnnotationCanvas.CalculateImageBounds();
        return window.AnnotationCanvas.TranslatePoint(new Point(picture.X + x * picture.Width, picture.Y + y * picture.Height), window)!.Value;
    }

    private static void Drag(AnnotateWindow window, (double X, double Y) from, (double X, double Y) to, int steps = 6)
    {
        window.MouseDown(On(window, from.X, from.Y), MouseButton.Left);
        for (var i = 1; i <= steps; i++)
            window.MouseMove(On(window, from.X + (to.X - from.X) * i / steps, from.Y + (to.Y - from.Y) * i / steps));
        window.MouseUp(On(window, to.X, to.Y), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(AnnotateWindow window, double x, double y)
    {
        window.MouseDown(On(window, x, y), MouseButton.Left);
        window.MouseUp(On(window, x, y), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Key(AnnotateWindow window, Key key, RawInputModifiers modifiers = RawInputModifiers.None) =>
        window.KeyPress(key, modifiers, PhysicalKey.None, null);

    [AvaloniaFact]
    public void Each_drawing_tool_draws_its_mark_where_dragged_and_a_click_draws_nothing()
    {
        var (window, vm, _) = Open();
        try
        {
            vm.Mode = AnnotateMode.Box;
            Drag(window, (0.2, 0.3), (0.6, 0.5));
            Click(window, 0.8, 0.8);                // no size: no box
            vm.Mode = AnnotateMode.Arrow;
            Drag(window, (0.1, 0.9), (0.7, 0.85));
            vm.Mode = AnnotateMode.Highlight;
            Drag(window, (0.05, 0.22), (0.7, 0.28));
            vm.Mode = AnnotateMode.Pen;
            Drag(window, (0.1, 0.1), (0.4, 0.15), steps: 12);

            Assert.Equal([AnnotationKind.Box, AnnotationKind.Arrow, AnnotationKind.Highlight, AnnotationKind.Pen], vm.Marks.Marks.Select(m => m.Kind));
            var box = vm.Marks.Marks[0];
            Assert.Equal(0.2, box.Points[0].X, 2);
            Assert.Equal(0.5, box.Points[1].Y, 2);
            Assert.True(vm.Marks.Marks[3].Points.Count > 5);
            Assert.All(vm.Marks.Marks, m => Assert.Equal(AnnotateTool.Colors[0], m.Color));
            // A colour picked after drawing goes to the next mark, not to the one just drawn.
            Assert.Equal(-1, vm.Marks.Selected);
            vm.Swatches[2].ChooseCommand.Execute(null);
            Assert.Equal(AnnotateTool.Colors[0], vm.Marks.Marks[^1].Color);
            // One undo step each.
            vm.UndoCommand.Execute(null);
            Assert.Equal(3, vm.Marks.Count);
            vm.RedoCommand.Execute(null);
            Assert.Equal(4, vm.Marks.Count);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_mark_being_drawn_shows_no_outline_or_handles_and_a_picked_one_does()
    {
        var (window, vm, _) = Open();
        try
        {
            foreach (var mode in new[] { AnnotateMode.Pen, AnnotateMode.Arrow, AnnotateMode.Box })
            {
                vm.Mode = mode;
                window.MouseDown(On(window, 0.2, 0.3), MouseButton.Left);
                window.MouseMove(On(window, 0.4, 0.5));
                window.MouseMove(On(window, 0.6, 0.6));
                Dispatcher.UIThread.RunJobs();
                Assert.True(vm.Marks.Selected >= 0, $"{mode}: the drag extends the mark it drew");
                Assert.False(window.AnnotationCanvas.ShowsSelection, $"{mode} while drawing");
                window.MouseUp(On(window, 0.6, 0.6), MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
            }

            Key(window, Avalonia.Input.Key.V);
            Click(window, 0.2, 0.45);
            Assert.True(window.AnnotationCanvas.ShowsSelection);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Numbers_count_up_and_select_moves_resizes_restyles_and_deletes()
    {
        var (window, vm, _) = Open();
        try
        {
            vm.Mode = AnnotateMode.Number;
            Click(window, 0.9, 0.2);
            Click(window, 0.9, 0.4);
            Assert.Equal([1, 2], vm.Marks.Marks.Select(m => m.Number));

            vm.Mode = AnnotateMode.Box;
            Drag(window, (0.2, 0.3), (0.5, 0.6));
            Key(window, Avalonia.Input.Key.V);
            Assert.Equal(AnnotateMode.Select, vm.Mode);

            // Click the box's edge, drag its body by its edge: it moves.
            Click(window, 0.2, 0.45);
            Assert.Equal(2, vm.Marks.Selected);
            Drag(window, (0.2, 0.45), (0.3, 0.45));
            Assert.Equal(0.3, vm.Marks.Marks[2].Points[0].X, 2);
            Assert.Equal(0.6, vm.Marks.Marks[2].Points[1].X, 2);
            // Its bottom right corner handle resizes it.
            Drag(window, (0.6, 0.6), (0.7, 0.8));
            Assert.Equal((0.7, 0.8), (Math.Round(vm.Marks.Marks[2].Points[1].X, 2), Math.Round(vm.Marks.Marks[2].Points[1].Y, 2)));
            Assert.Equal(0.3, vm.Marks.Marks[2].Points[0].X, 2);

            // Colour and width go to the selected mark.
            vm.Swatches[4].ChooseCommand.Execute(null);
            vm.SetWidthCommand.Execute(AnnotationWidth.Thick);
            Assert.Equal((AnnotateTool.Colors[4], AnnotationWidth.Thick), (vm.Marks.Marks[2].Color, vm.Marks.Marks[2].Width));
            Assert.Equal(AnnotateTool.Colors[0], vm.Marks.Marks[0].Color);

            Key(window, Avalonia.Input.Key.Delete);
            Assert.Equal(2, vm.Marks.Count);
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
            Assert.Equal(3, vm.Marks.Count);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Tab_picks_a_mark_and_the_arrows_move_it_by_pixels_of_the_picture()
    {
        var (window, vm, _) = Open();    // 800 × 500
        try
        {
            vm.Mode = AnnotateMode.Box;
            Drag(window, (0.2, 0.2), (0.4, 0.4));
            Drag(window, (0.6, 0.6), (0.8, 0.8));
            Assert.Equal(-1, vm.Marks.Selected);

            Key(window, Avalonia.Input.Key.Tab);
            Assert.Equal((AnnotateMode.Select, 0), (vm.Mode, vm.Marks.Selected));
            Key(window, Avalonia.Input.Key.Tab);
            Assert.Equal(1, vm.Marks.Selected);
            Key(window, Avalonia.Input.Key.Tab, RawInputModifiers.Shift);
            Assert.Equal(0, vm.Marks.Selected);

            var before = vm.Marks.Marks[0].Points[0];
            Key(window, Avalonia.Input.Key.Right);
            Key(window, Avalonia.Input.Key.Down, RawInputModifiers.Shift);
            var after = vm.Marks.Marks[0].Points[0];
            Assert.Equal(1.0 / 800, after.X - before.X, 6);
            Assert.Equal(10.0 / 500, after.Y - before.Y, 6);
            // Each move is one undo step.
            vm.UndoCommand.Execute(null);
            Assert.Equal(before.Y, vm.Marks.Marks[0].Points[0].Y, 6);

            // It stays on the picture: far to the left, it stops at the edge.
            for (var i = 0; i < 30; i++)
                Key(window, Avalonia.Input.Key.Left, RawInputModifiers.Shift);
            Assert.Equal(0, vm.Marks.Marks[0].Points.Min(p => p.X), 6);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Past_the_last_mark_Tab_goes_on_into_the_bar_and_Esc_comes_back_to_the_picture()
    {
        var (window, vm, _) = Open();
        try
        {
            vm.Mode = AnnotateMode.Box;
            Drag(window, (0.2, 0.2), (0.4, 0.4));
            Drag(window, (0.6, 0.6), (0.8, 0.8));

            Key(window, Avalonia.Input.Key.Tab);
            Key(window, Avalonia.Input.Key.Tab);
            Assert.Equal(1, vm.Marks.Selected);
            Key(window, Avalonia.Input.Key.Tab);
            // No mark picked, and the focus on the bar's first tool (not the window's close button, which Enter would
            // press, closing without saving).
            Assert.Equal(-1, vm.Marks.Selected);
            Assert.Same(window.FindControl<RadioButton>("SelectTool"), window.FocusManager!.GetFocusedElement());
            // The arrows go along the tools and choose; Enter there saves nothing, Space chooses.
            Key(window, Avalonia.Input.Key.Right);
            Assert.Same(window.FindControl<RadioButton>("PenTool"), window.FocusManager.GetFocusedElement());
            Assert.Equal(AnnotateMode.Pen, vm.Mode);
            Key(window, Avalonia.Input.Key.Left);
            Assert.Equal(AnnotateMode.Select, vm.Mode);
            // Enter on a tool still saves (Space chooses).
            Assert.True(vm.SaveCommand.CanExecute(null));

            // Esc leaves the bar for the picture (it doesn't close the window), and Tab picks marks again.
            Key(window, Avalonia.Input.Key.Escape);
            Assert.True(window.IsVisible);
            Assert.Null(window.FocusManager.GetFocusedElement());
            Key(window, Avalonia.Input.Key.Tab);
            Assert.Equal(0, vm.Marks.Selected);
            // Shift+Tab before the first mark: into the bar from its end, at Save.
            Key(window, Avalonia.Input.Key.Tab, RawInputModifiers.Shift);
            Assert.Same(window.FindControl<Button>("SaveButton"), window.FocusManager.GetFocusedElement());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The window on <paramref name="paths"/>, its conversions handed to <paramref name="export"/>.</summary>
    private static (AnnotateWindow Window, AnnotateViewModel Vm) OpenOn(IReadOnlyList<string> paths, Action<string, Preset> export)
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var tool = store.FindPreset(BuiltInData.AnnotateId) ?? BuiltInData.AnnotatePresets().Single();
        var vm = new AnnotateViewModel(paths, tool, AppHost.Get<ILocalizer>(), store, _ => BuiltInData.SameAsSource, export);
        var window = new AnnotateWindow { DataContext = vm, Width = 1040, Height = 720 };
        window.Show();
        Wait(() => (vm.PreviewImage is not null || vm.IsFinished) && !vm.IsBusy);
        RenderTests.Pump();
        return (window, vm);
    }

    [AvaloniaFact]
    public void A_picture_that_cannot_be_opened_keeps_its_notice_and_the_run_ends_on_a_summary()
    {
        // The notice was cleared as the next picture opened, and after the last the window closed without a word.
        var broken = Path.Combine(_folder, "broken.png");
        File.WriteAllBytes(broken, [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3]);
        var exported = new List<string>();
        var (window, vm) = OpenOn([broken, Picture("fine.png")], (path, _) => exported.Add(path));
        try
        {
            Assert.Equal(1, vm.CurrentIndex);
            Assert.Contains("broken.png", vm.StatusNotice);
            vm.Mode = AnnotateMode.Box;
            Drag(window, (0.2, 0.2), (0.5, 0.5));
            Key(window, Avalonia.Input.Key.Enter);
            Wait(() => vm.IsFinished);

            Assert.True(window.IsVisible);
            Assert.Equal((1, 0, 1), (vm.DoneCount, vm.SkippedCount, vm.FailedCount));
            Assert.Equal("1 of 2 failed", vm.Summary);
            Assert.Contains(vm.Problems, p => p.StartsWith("broken.png", StringComparison.Ordinal));
            Assert.Single(exported);
            RenderTests.Save(window, "annotate-summary.png");
            Key(window, Avalonia.Input.Key.Enter);
            Assert.False(window.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_save_that_cannot_start_keeps_the_picture_and_its_marks()
    {
        var (window, vm) = OpenOn([Picture("one.png"), Picture("two.png")], (_, _) => throw new IOException("disk full"));
        try
        {
            vm.Mode = AnnotateMode.Box;
            Drag(window, (0.2, 0.2), (0.5, 0.5));
            Key(window, Avalonia.Input.Key.Enter);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal((0, 1), (vm.CurrentIndex, vm.Marks.Count));
            Assert.Contains("one.png", vm.StatusNotice);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_save_that_works_the_second_time_leaves_no_failure_behind()
    {
        // The failure stayed: its notice over the next picture, and a summary of "0 of 2 failed" at the end.
        var tries = 0;
        var exported = new List<string>();
        var (window, vm) = OpenOn([Picture("one.png"), Picture("two.png")], (path, _) =>
        {
            if (tries++ == 0)
                throw new IOException("busy");
            exported.Add(path);
        });
        try
        {
            vm.Mode = AnnotateMode.Box;
            Drag(window, (0.2, 0.2), (0.5, 0.5));
            Key(window, Avalonia.Input.Key.Enter);
            Dispatcher.UIThread.RunJobs();
            Key(window, Avalonia.Input.Key.Enter);
            Wait(() => vm.CurrentIndex == 1 && !vm.IsBusy);
            Assert.Equal("", vm.StatusNotice);
            Assert.Empty(vm.Problems);

            Drag(window, (0.2, 0.2), (0.5, 0.5));
            Key(window, Avalonia.Input.Key.Enter);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, exported.Count);
            Assert.False(vm.IsFinished);            // closed, with nothing to report
            Assert.False(window.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Enter_saves_even_after_a_button_was_clicked()
    {
        // A clicked button keeps the focus: Enter pressed it again (Undo undid another mark) instead of saving.
        var (window, vm, exported) = Open();
        try
        {
            vm.Mode = AnnotateMode.Box;
            Drag(window, (0.1, 0.1), (0.3, 0.3));
            Drag(window, (0.5, 0.5), (0.7, 0.7));
            var undo = window.GetVisualDescendants().OfType<Button>().Single(b => b.Command == vm.UndoCommand);
            var at = undo.TranslatePoint(new Point(undo.Bounds.Width / 2, undo.Bounds.Height / 2), window)!.Value;
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, vm.Marks.Count);

            Key(window, Avalonia.Input.Key.Enter);
            Wait(() => !vm.IsBusy);
            var (_, conversion) = Assert.Single(exported);
            Assert.Single(conversion.Image.Annotations!);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Clearing_a_label_being_typed_again_then_picking_another_types_that_one()
    {
        // Finishing the first (cleared, so removed) moved the second one place down: the box opened on the mark after
        // it, or past the end of the list, which threw.
        var (window, vm, _) = Open();
        try
        {
            vm.AddText(new AnnotationPoint { X = 0.1, Y = 0.2 }, "Alpha");
            vm.AddText(new AnnotationPoint { X = 0.1, Y = 0.6 }, "Beta");
            vm.AddText(new AnnotationPoint { X = 0.6, Y = 0.6 }, "Gamma");
            vm.Mode = AnnotateMode.Select;
            window.AnnotationCanvas.TypeAgain(0);
            window.Editor.Text = "";
            window.AnnotationCanvas.TypeAgain(1);       // "Beta", as the canvas found it before Alpha went
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("Beta", window.Editor.Text);
            window.Editor.Text = "Beta 2";
            window.FinishTyping(keep: true);
            Assert.Equal(["Beta 2", "Gamma"], vm.Marks.Marks.Select(m => m.Text));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_click_elsewhere_with_the_Text_tool_keeps_what_was_typed_and_starts_the_next_label()
    {
        var (window, vm, _) = Open();
        try
        {
            vm.Mode = AnnotateMode.Text;
            Click(window, 0.3, 0.3);
            window.Editor.Text = "First";
            Click(window, 0.6, 0.6);
            window.Editor.Text = "Second";
            Key(window, Avalonia.Input.Key.Enter);

            Assert.Equal(["First", "Second"], vm.Marks.Marks.Select(m => m.Text));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_tool_width_and_colour_chosen_are_reported_as_selected()
    {
        // Radio buttons, so a screen reader says which one is chosen, not just its name.
        var (window, vm, _) = Open();
        try
        {
            RadioButton Find(string name) => window.FindControl<RadioButton>(name)!;
            Assert.True(Find("ArrowTool").IsChecked);
            vm.Mode = AnnotateMode.Pen;
            Assert.Equal((true, false), (Find("PenTool").IsChecked, Find("ArrowTool").IsChecked));
            Find("BoxTool").Command!.Execute(Find("BoxTool").CommandParameter);
            Assert.Equal((AnnotateMode.Box, true), (vm.Mode, Find("BoxTool").IsChecked));

            var widths = window.GetVisualDescendants().OfType<RadioButton>().Where(r => r.GroupName == "AnnotateWidth").ToList();
            Assert.Equal([false, true, false], widths.Select(r => r.IsChecked == true));
            var colours = window.GetVisualDescendants().OfType<RadioButton>().Where(r => r.GroupName == "AnnotateColor").ToList();
            Assert.Equal(6, colours.Count);
            vm.Swatches[3].ChooseCommand.Execute(null);
            Assert.Equal(3, colours.FindIndex(r => r.IsChecked == true));
            Assert.Equal("Green", Avalonia.Automation.AutomationProperties.GetName(colours[3]));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Text_is_typed_in_place_and_typed_again_with_a_double_click()
    {
        var (window, vm, _) = Open();
        try
        {
            vm.Mode = AnnotateMode.Text;
            Click(window, 0.3, 0.7);
            Assert.True(window.Editor.IsVisible);
            window.Editor.Text = "Save when done";
            Key(window, Avalonia.Input.Key.Enter);
            Dispatcher.UIThread.RunJobs();

            var mark = Assert.Single(vm.Marks.Marks);
            Assert.Equal((AnnotationKind.Text, "Save when done"), (mark.Kind, mark.Text));
            Assert.False(window.Editor.IsVisible);

            // A click with nothing typed adds nothing.
            Click(window, 0.6, 0.3);
            window.Editor.Text = "   ";
            window.FinishTyping(keep: true);
            Assert.Single(vm.Marks.Marks);

            vm.Mode = AnnotateMode.Select;
            // A double-click on the tag: the press the canvas gets with a click count of 2 (the headless platform
            // doesn't count clicks itself).
            var canvas = window.AnnotationCanvas;
            var tag = canvas.BoundsOf(mark);
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
            canvas.RaiseEvent(new PointerPressedEventArgs(canvas, pointer, window, canvas.TranslatePoint(tag.Center, window)!.Value, 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, clickCount: 2));
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.Editor.IsVisible);
            Assert.Equal("Save when done", window.Editor.Text);
            window.Editor.Text = "Click Save";
            window.FinishTyping(keep: true);
            Assert.Equal("Click Save", vm.Marks.Marks[0].Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(0)] // red: white ink
    [InlineData(2)] // yellow: dark ink
    public void Text_being_typed_in_place_looks_like_the_tag_it_will_be(int swatch)
    {
        var (window, vm, _) = Open();
        try
        {
            vm.Swatches[swatch].ChooseCommand.Execute(null);
            vm.Mode = AnnotateMode.Text;
            Click(window, 0.3, 0.7);
            RenderTests.Pump();
            Assert.True(window.Editor.IsFocused);
            window.KeyTextInput("Save when done");
            RenderTests.Pump();

            var tag = Avalonia.Media.Color.Parse(AnnotateTool.Colors[swatch]);
            var ink = 0.2126 * tag.R + 0.7152 * tag.G + 0.0722 * tag.B > 170 ? Avalonia.Media.Color.Parse("#1C1C1C") : Avalonia.Media.Colors.White;

            // The box, focused, in a rendered frame: the tag's colour with the text in its ink, as the file gets it.
            using var frame = window.CaptureRenderedFrame()!;
            var framePath = Path.Combine(_folder, $"typing-{swatch}.png");
            using (var stream = File.Create(framePath))
                frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            var scale = frame.PixelSize.Width / window.Bounds.Width;
            var box = new Rect(window.Editor.TranslatePoint(default, window)!.Value, window.Editor.Bounds.Size).Deflate(3);
            using var image = new MagickImage(framePath);
            image.Crop(new MagickGeometry((int)(box.X * scale), (int)(box.Y * scale), (uint)(box.Width * scale), (uint)(box.Height * scale)));
            image.ResetPage();
            var rgb = image.GetPixels().ToByteArray(PixelMapping.RGB)!;
            int fill = 0, text = 0, count = rgb.Length / 3;
            for (var i = 0; i < rgb.Length; i += 3)
            {
                if (Near(rgb, i, tag))
                    fill++;
                else if (Near(rgb, i, ink))
                    text++;
            }
            Assert.True(fill > count * 0.5, $"{fill} of {count} pixels in the tag's colour {tag}");
            Assert.True(text > count * 0.03, $"{text} of {count} pixels in the ink {ink}");
            // The caret in the ink too, so it shows on the tag.
            Assert.Equal(ink, Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(window.Editor.CaretBrush).Color);
        }
        finally
        {
            window.Close();
        }

        static bool Near(byte[] rgb, int i, Avalonia.Media.Color color) =>
            Math.Abs(rgb[i] - color.R) + Math.Abs(rgb[i + 1] - color.G) + Math.Abs(rgb[i + 2] - color.B) < 40;
    }

    public static TheoryData<string> Labels => new()
    {
        "Save when done", "저장하기 완료", "東京 OK", "مرحبا بالعالم", "Save مرحبا بالعالم", "المجموع 1,200 ريال",
        "मेरा नाम हिन्दी", "ภาษาไทย ที่นี่", "Done 👍",
    };

    [AvaloniaTheory]
    [MemberData(nameof(Labels))]
    public void A_label_is_drawn_as_its_box_showed_it_while_it_was_typed(string text)
    {
        // The box is Avalonia's text box, the tag the painter's: the same fonts (the UI's chain, bold), the same order
        // (bidirectional), the same line placement. Before, the box showed Noto and the tag the system's fonts,
        // Arabic's box grew taller than its tag, and right-to-left text sat off by the box's rounding.
        var (window, vm, _) = Open();
        try
        {
            vm.Mode = AnnotateMode.Text;
            Click(window, 0.1, 0.4);
            window.Editor.Text = text;
            window.Editor.CaretBrush = Avalonia.Media.Brushes.Transparent;   // the caret is no part of the label
            Dispatcher.UIThread.RunJobs();
            RenderTests.Pump();
            var box = window.TypingTag;
            var typed = Capture(window, new Rect(box.TranslatePoint(default, window)!.Value, box.Bounds.Size), "typed");

            window.FinishTyping(keep: true);
            RenderTests.Pump();
            var tag = window.AnnotationCanvas.BoundsOf(vm.Marks.Marks[0]);
            var drawn = Capture(window, new Rect(window.AnnotationCanvas.TranslatePoint(tag.TopLeft, window)!.Value, tag.Size), "drawn");

            using (typed)
            using (drawn)
            {
                // Compared by brightness, within a pixel of the crop's rounding: the screen may smooth text another way
                // than a picture is (Linux draws the box with colour fringes, LCD style), which is no difference in the
                // label. A few levels are left of that (dense Korean strokes most).
                typed.Grayscale();
                drawn.Grayscale();
                var (difference, dx, dy) = Aligned(typed, drawn);
                if (difference >= 5.5)
                {
                    // What each side drew with, and the two pictures with the screenshots, for a platform this can't be
                    // run on by hand (a CI runner).
                    var name = $"annotate-label-{string.Concat(text.Where(char.IsAsciiLetterOrDigit))}";
                    typed.Write(Path.Combine(TestServices.ScreenshotDirectory, name + "-box.png"));
                    drawn.Write(Path.Combine(TestServices.ScreenshotDirectory, name + "-label.png"));
                    Assert.Fail($"\"{text}\": the label differs from its box by {difference:0.00} of 255 (at {dx}, {dy}).\n"
                        + $"Box: {Fonts(BoxRuns(window.Editor, text))}\nLabel: {Fonts(LabelRuns(text))}");
                }
                Assert.True(Math.Abs(box.Bounds.Width - tag.Width) < 3, $"\"{text}\": box {box.Bounds.Width:0.0} wide, the tag {tag.Width:0.0}");
            }
        }
        finally
        {
            window.Close();
        }
    }

    public static TheoryData<string> MixedTexts => new()
    {
        "Save مرحبا بالعالم", "مرحبا Filee", "المجموع 1,200 ريال", "Total: 1,200 ريال (approx.)", "خصم 50%", "السعر -5",
        "ا 5%", "ا +5", "ا #5", "a ا-5 b", "ا 1.5.", "ا 5 6", "ا: 10:30", "abc ١٢٣ ٤٥٦", "١٢٣ ٤٥٦", "مرحبا ABC 123 def",
        "שלום 1-2", "abc (שלום) def", "مرحبا (1)", "مرحبا!", "Hi مرحبا!", "abc ا 5", "x = ا 3", "2 ا", "$5 مقابل €4",
        "מחיר (USD) 5", "ש [a] (b) ג", "abc\u200F 12", "ب٬a", "ש١٬", "١٬٢٠٠ ريال", "(a [ب) c]",
    };

    [AvaloniaFact]
    public void Random_mixed_text_is_put_in_the_order_the_text_box_shows()
    {
        // 400 strings drawn from every kind of letter the order depends on (strong either way, numbers of both kinds,
        // signs, separators, neutrals, brackets, marks, the marks that only give a way), most with a pair of brackets
        // around some of it, each against Avalonia's layout.
        TestServices.EnsureInitialized("en");
        string[] pieces = ["a", "Q", "ש", "ب", "ا", "1", "9", "١", "٢", "+", "-", "%", "$", "#", ",", ".", ":", "/", " ", "!",
            "(", ")", "[", "]", "\u0301", "\u064E", "\u200E", "\u200F", "\u061C", "٬", "€", "\u00A0"];
        var random = new Random(1024);
        var mismatches = new List<string>();
        for (var n = 0; n < 400; n++)
        {
            var parts = Enumerable.Range(0, random.Next(1, 10)).Select(_ => pieces[random.Next(pieces.Length)]).ToList();
            // Most with a pair of brackets around some of it, whose way depends on what is inside and before (N0).
            if (random.Next(3) > 0)
            {
                var from = random.Next(parts.Count + 1);
                var to = random.Next(from, parts.Count + 1);
                var (opening, closing) = random.Next(2) == 0 ? ("(", ")") : ("[", "]");
                parts.Insert(to, closing);
                parts.Insert(from, opening);
            }
            var text = string.Concat(parts);
            var label = Seen(Filee.Engines.Annotations.LabelText.Runs(text).Select(r => (r.Text, r.RightToLeft)));
            var box = BoxSeen(text);
            if (label != box)
                mismatches.Add($"{Escape(text)} → box {Escape(box)}, label {Escape(label)}");
        }
        Assert.True(mismatches.Count == 0, $"{mismatches.Count} of 400 differ:\n" + string.Join("\n", mismatches.Take(15)));

        static string Escape(string s) => string.Concat(s.Select(c => c < 0x20 || c is >= '\u0300' and <= '\u036F' || char.GetUnicodeCategory(c) is
            System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.NonSpacingMark ? $"<{(int)c:X4}>" : c.ToString()));
    }

    /// <summary>A run's letters as seen from the left: a right-to-left run reversed.</summary>
    private static string Seen(IEnumerable<(string Text, bool RightToLeft)> runs) => string.Concat(runs.Select(r =>
        r.RightToLeft ? string.Concat(r.Text.EnumerateRunes().Reverse().Select(x => x.ToString())) : r.Text));

    /// <summary>The text as Avalonia's own layout (what the text box shows) puts it, seen from the left.</summary>
    private static string BoxSeen(string text)
    {
        var rightToLeft = Filee.Engines.Annotations.LabelText.IsRightToLeft(text);
        using var layout = new Avalonia.Media.TextFormatting.TextLayout(text, new Avalonia.Media.Typeface("Noto Sans"), 20,
            Avalonia.Media.Brushes.Black, Avalonia.Media.TextAlignment.Left, Avalonia.Media.TextWrapping.NoWrap,
            flowDirection: rightToLeft ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight);
        return Seen(layout.TextLines[0].TextRuns.OfType<Avalonia.Media.TextFormatting.ShapedTextRun>()
            .Select(r => (r.Text.ToString(), r.BidiLevel % 2 == 1)));
    }

    [AvaloniaTheory]
    [MemberData(nameof(MixedTexts))]
    public void Mixed_text_is_put_in_the_order_the_text_box_shows(string text)
    {
        // The label's runs from left to right, against Avalonia's own layout of the text (what the box shows).
        TestServices.EnsureInitialized("en");
        Assert.Equal(BoxSeen(text), Seen(Filee.Engines.Annotations.LabelText.Runs(text).Select(r => (r.Text, r.RightToLeft))));
    }

    private static string Fonts(IEnumerable<(string Text, string Font)> runs) => string.Join(" | ", runs.Select(r => $"\"{r.Text}\" {r.Font}"));

    /// <summary>The fonts Avalonia lays the box's text out in, with the simulations it applies.</summary>
    private static IEnumerable<(string Text, string Font)> BoxRuns(TextBox box, string text)
    {
        using var layout = new Avalonia.Media.TextFormatting.TextLayout(text,
            new Avalonia.Media.Typeface(box.FontFamily, box.FontStyle, box.FontWeight), box.FontSize, Avalonia.Media.Brushes.Black);
        return [.. layout.TextLines.SelectMany(l => l.TextRuns).OfType<Avalonia.Media.TextFormatting.ShapedTextRun>().Select(r =>
            (r.Text.ToString(), $"{r.ShapedBuffer.GlyphTypeface.FamilyName} {r.ShapedBuffer.GlyphTypeface.Weight} {r.ShapedBuffer.GlyphTypeface.FontSimulations}"))];
    }

    /// <summary>The fonts the painter draws the label in, and whether Skia makes them bold.</summary>
    private static IEnumerable<(string Text, string Font)> LabelRuns(string text) =>
        [.. Filee.Engines.Annotations.LabelText.Runs(text).Select(r => (r.Text,
            $"{r.Face.FamilyName} {r.Face.FontWeight}{(Filee.Engines.Annotations.LabelText.MadeBold(r.Face) ? " made bold" : "")}"))];

    /// <summary>
    /// How far apart two captures are, per channel of 255 over their common part, at the best shift of up to two pixels
    /// either way: a crop rounded to whole pixels is no difference.
    /// </summary>
    private static (double Difference, int Dx, int Dy) Aligned(MagickImage a, MagickImage b)
    {
        var pa = a.GetPixels().ToByteArray(PixelMapping.RGB)!;
        var pb = b.GetPixels().ToByteArray(PixelMapping.RGB)!;
        int wa = (int)a.Width, ha = (int)a.Height, wb = (int)b.Width, hb = (int)b.Height;
        var best = (Difference: double.MaxValue, Dx: 0, Dy: 0);
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                long sum = 0, count = 0;
                for (var y = 3; y < Math.Min(ha, hb) - 3; y++)
                {
                    for (var x = 3; x < Math.Min(wa, wb) - 3; x++)
                    {
                        int xb = x + dx, yb = y + dy;
                        if (xb < 0 || yb < 0 || xb >= wb || yb >= hb)
                            continue;
                        for (var c = 0; c < 3; c++)
                            sum += Math.Abs(pa[(y * wa + x) * 3 + c] - pb[(yb * wb + xb) * 3 + c]);
                        count += 3;
                    }
                }
                var mean = count == 0 ? double.MaxValue : (double)sum / count;
                if (mean < best.Difference)
                    best = (mean, dx, dy);
            }
        }
        return best;
    }

    /// <summary>A rendered frame's part at <paramref name="area"/> (window coordinates).</summary>
    private MagickImage Capture(AnnotateWindow window, Rect area, string name)
    {
        using var frame = window.CaptureRenderedFrame()!;
        var path = Path.Combine(_folder, name + ".png");
        using (var stream = File.Create(path))
            frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        var scale = frame.PixelSize.Width / window.Bounds.Width;
        var image = new MagickImage(path);
        image.Crop(new MagickGeometry((int)Math.Round(area.X * scale), (int)Math.Round(area.Y * scale),
            (uint)Math.Round(area.Width * scale), (uint)Math.Round(area.Height * scale)));
        image.ResetPage();
        image.Alpha(AlphaOption.Remove);
        return image;
    }

    [AvaloniaFact]
    public void The_text_box_follows_the_picture_when_the_window_is_resized_while_typing()
    {
        var (window, vm, _) = Open();
        try
        {
            vm.Mode = AnnotateMode.Text;
            Click(window, 0.3, 0.6);
            window.Editor.Text = "Save";
            RenderTests.Pump();
            var fontBefore = window.Editor.FontSize;

            window.Width = 760;
            window.Height = 560;
            RenderTests.Pump();

            // Still on its point of the picture, and at the picture's new scale.
            var box = window.TypingTag;
            var at = box.TranslatePoint(default, window)!.Value;
            var want = On(window, 0.3, 0.6);
            Assert.True(Math.Abs(at.X - want.X) < 2 && Math.Abs(at.Y - want.Y) < 2, $"the box at {at}, its point at {want}");
            Assert.True(window.Editor.FontSize < fontBefore, $"font {window.Editor.FontSize:0.0} after {fontBefore:0.0}");
            Assert.True(window.Editor.IsVisible);
            Assert.Equal("Save", window.Editor.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void In_a_right_to_left_ui_the_text_box_opens_where_the_click_was()
    {
        var (window, vm, _) = Open();
        try
        {
            // The UI in Arabic mirrors the window; the overlay mirrored too opened the box near the right.
            window.FlowDirection = Avalonia.Media.FlowDirection.RightToLeft;
            RenderTests.Pump();
            vm.Mode = AnnotateMode.Text;
            Click(window, 0.15, 0.5);
            RenderTests.Pump();

            var box = window.TypingTag;
            var left = box.TranslatePoint(default, window)!.Value.X;
            Assert.True(Math.Abs(left - On(window, 0.15, 0.5).X) < 2, $"the box at {left:0}, the click at {On(window, 0.15, 0.5).X:0}");
            // Arabic is typed right to left, as its tag is drawn.
            window.Editor.Text = "مرحبا Filee";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Avalonia.Media.FlowDirection.RightToLeft, window.Editor.FlowDirection);
            window.Editor.Text = "Filee مرحبا";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Avalonia.Media.FlowDirection.LeftToRight, window.Editor.FlowDirection);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_label_typed_again_follows_the_text_and_the_old_one_does_not_show_behind()
    {
        var (window, vm, _) = Open();
        try
        {
            vm.AddText(new AnnotationPoint { X = 0.2, Y = 0.6 }, "Click Save when you are done here");
            var mark = vm.Marks.Marks[0];
            var oldTag = window.AnnotationCanvas.BoundsOf(mark);
            vm.Mode = AnnotateMode.Select;
            window.AnnotationCanvas.TypeAgain(0);
            RenderTests.Pump();
            Assert.True(window.Editor.IsFocused);
            window.KeyTextInput("OK");    // the text is selected: typing replaces it
            RenderTests.Pump();
            Assert.Equal("OK", window.Editor.Text);

            // The box is the tag "OK" will be, not the old one.
            var box = window.TypingTag;
            var shortTag = window.AnnotationCanvas.BoundsOf(new Annotation { Kind = AnnotationKind.Text, Width = mark.Width, Points = [mark.Points[0]], Text = "OK" });
            Assert.True(Math.Abs(box.Bounds.Width - shortTag.Width) < 8, $"box {box.Bounds.Width:0} wide, the tag {shortTag.Width:0}");

            // Nothing of the old tag right of the box.
            using var frame = window.CaptureRenderedFrame()!;
            var framePath = Path.Combine(_folder, "retype.png");
            using (var stream = File.Create(framePath))
                frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            var scale = frame.PixelSize.Width / window.Bounds.Width;
            var boxRight = box.TranslatePoint(new Point(box.Bounds.Width, 0), window)!.Value.X + 4;
            var oldRight = window.AnnotationCanvas.TranslatePoint(oldTag.TopRight, window)!.Value.X - 4;
            var top = window.AnnotationCanvas.TranslatePoint(oldTag.TopLeft, window)!.Value.Y + 4;
            using var image = new MagickImage(framePath);
            image.Crop(new MagickGeometry((int)(boxRight * scale), (int)(top * scale), (uint)((oldRight - boxRight) * scale), (uint)((oldTag.Height - 8) * scale)));
            image.ResetPage();
            var rgb = image.GetPixels().ToByteArray(PixelMapping.RGB)!;
            var tag = Avalonia.Media.Color.Parse(mark.Color);
            var left = 0;
            for (var i = 0; i < rgb.Length; i += 3)
            {
                if (Math.Abs(rgb[i] - tag.R) + Math.Abs(rgb[i + 1] - tag.G) + Math.Abs(rgb[i + 2] - tag.B) < 40)
                    left++;
            }
            Assert.True(left == 0, $"{left} of {rgb.Length / 3} pixels of the old tag show right of the box");

            window.FinishTyping(keep: true);
            Assert.Equal("OK", vm.Marks.Marks[0].Text);
            RenderTests.Pump();
            Assert.False(box.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Save_starts_the_conversion_with_the_marks_and_the_next_picture_starts_clean()
    {
        var (window, vm, exported) = Open(files: 2);
        try
        {
            vm.Swatches[3].ChooseCommand.Execute(null);
            vm.SetWidthCommand.Execute(AnnotationWidth.Thin);
            vm.Mode = AnnotateMode.Arrow;
            Drag(window, (0.1, 0.5), (0.6, 0.5));
            vm.EditText(-1, "ignored");
            vm.AddText(new AnnotationPoint { X = 0.1, Y = 0.1 }, " Note ");

            await vm.SaveCommand.ExecuteAsync(null);
            Wait(() => !vm.IsBusy);

            var (path, conversion) = Assert.Single(exported);
            Assert.EndsWith("shot1.png", path);
            Assert.Equal("{name} Annotated", conversion.Output.FileNamePattern);
            Assert.Equal([AnnotationKind.Arrow, AnnotationKind.Text], conversion.Image.Annotations!.Select(m => m.Kind));
            Assert.Equal("Note", conversion.Image.Annotations![1].Text);
            Assert.All(conversion.Image.Annotations!, m => Assert.Equal((AnnotateTool.Colors[3], AnnotationWidth.Thin), (m.Color, m.Width)));
            Assert.Equal(1, vm.CurrentIndex);
            Assert.Equal(0, vm.Marks.Count);
            // The style is kept in the tool for next time.
            var tool = AppHost.Get<UserDataStore>().FindPreset(BuiltInData.AnnotateId)!;
            Assert.Equal((AnnotateTool.Colors[3], AnnotationWidth.Thin), (tool.Image.AnnotationColor, tool.Image.AnnotationWidth));

            // A picture without marks can't be saved (Skip moves on), by the button or by Enter.
            Assert.False(vm.SaveCommand.CanExecute(null));
            Assert.False(window.FindControl<Button>("SaveButton")!.IsEffectivelyEnabled);
            Key(window, Avalonia.Input.Key.Enter);
            Wait(() => !vm.IsBusy);
            Assert.Single(exported);
            Assert.Equal(1, vm.CurrentIndex);
            vm.Mode = AnnotateMode.Number;
            Click(window, 0.5, 0.5);
            Assert.True(vm.SaveCommand.CanExecute(null));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Its_texts_follow_a_language_change_and_closing_with_the_title_bar_keeps_the_style()
    {
        var (window, vm, _) = Open(files: 2);
        var languages = AppHost.Get<LocalizationService>();
        try
        {
            Assert.Equal("Red", vm.Swatches[0].Name);
            languages.SetLanguage("ko");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("빨강", vm.Swatches[0].Name);
            Assert.Equal("저장하고 다음", window.FindControl<Button>("SaveButton")!.Content);
            Assert.Equal(AppHost.Get<ILocalizer>().Format("crop.page_counter", 1, 2), vm.CounterText);

            vm.Swatches[4].ChooseCommand.Execute(null);
            vm.SetWidthCommand.Execute(AnnotationWidth.Thick);
        }
        finally
        {
            languages.SetLanguage("en");
            window.Close();     // as the title bar's close or Alt+F4 does: not the window's own Close command
        }
        Dispatcher.UIThread.RunJobs();
        var tool = AppHost.Get<UserDataStore>().FindPreset(BuiltInData.AnnotateId)!;
        Assert.Equal((AnnotateTool.Colors[4], AnnotationWidth.Thick), (tool.Image.AnnotationColor, tool.Image.AnnotationWidth));
    }

    [AvaloniaTheory]
    [InlineData(800u, 500u)]
    [InlineData(300u, 150u)] // shown about three times its size: the smallest line, text and number are the file's
    public void What_the_window_draws_is_what_the_file_gets(uint width, uint height)
    {
        var (window, vm, _) = Open(width: width, height: height);
        try
        {
            vm.AddText(new AnnotationPoint { X = 0.05, Y = 0.05 }, "Save when done");
            vm.Mode = AnnotateMode.Box;
            Drag(window, (0.2, 0.3), (0.6, 0.6));
            vm.Mode = AnnotateMode.Arrow;
            Drag(window, (0.1, 0.9), (0.7, 0.8));
            vm.Mode = AnnotateMode.Highlight;
            Drag(window, (0.05, 0.23), (0.7, 0.28));
            vm.Mode = AnnotateMode.Number;
            Click(window, 0.85, 0.3);
            vm.Marks.Select(-1);
            RenderTests.Pump();

            // The window: the picture's part of a rendered frame.
            using var frame = window.CaptureRenderedFrame()!;
            var framePath = Path.Combine(_folder, "frame.png");
            using (var stream = File.Create(framePath))
                frame.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            var picture = window.AnnotationCanvas.CalculateImageBounds();
            var topLeft = window.AnnotationCanvas.TranslatePoint(picture.TopLeft, window)!.Value;
            using var drawn = new MagickImage(framePath);
            var scale = frame.PixelSize.Width / window.Bounds.Width;
            drawn.Crop(new MagickGeometry((int)Math.Round(topLeft.X * scale), (int)Math.Round(topLeft.Y * scale),
                (uint)Math.Round(picture.Width * scale), (uint)Math.Round(picture.Height * scale)));
            drawn.ResetPage();
            drawn.Alpha(AlphaOption.Remove);

            // The file: the same marks drawn by the engine. Both at the smaller size: the sharper one brought down, not
            // the other blown up (a 300 px file shown three times larger would blur).
            var shrink = Math.Min(1.0, (double)width / drawn.Width);
            var (compareWidth, compareHeight) = ((uint)Math.Round(drawn.Width * shrink), (uint)Math.Round(drawn.Height * shrink));
            drawn.Resize(new MagickGeometry(compareWidth, compareHeight) { IgnoreAspectRatio = true });
            scale *= shrink;
            byte[] FileWith(IEnumerable<Annotation> marks)
            {
                using var file = new MagickImage(Path.Combine(_folder, "shot1.png"));
                Filee.Engines.Annotations.Annotator.Apply(file, new ImageOptions { Annotations = [.. marks] });
                file.Resize(new MagickGeometry(compareWidth, compareHeight) { IgnoreAspectRatio = true });
                return file.GetPixels().ToByteArray(PixelMapping.RGB)!;
            }
            var shown = drawn.GetPixels().ToByteArray(PixelMapping.RGB)!;
            var unmarked = FileWith([]);
            var saved = FileWith(vm.Marks.Marks);
            var difference = saved.Zip(shown, (x, y) => Math.Abs(x - y)).Average();
            Assert.True(difference < 2.5, $"mean difference {difference:0.00} of 255");

            // Each mark on its own pixels, those the window or the file changed: how many of them the two disagree on.
            // A mark missing from the file, moved, or saying something else disagrees on most; over the whole, mostly
            // white, picture (or the box around a mark) it hardly moved a mean.
            var w = (int)compareWidth;
            double Disagreeing(Annotation mark, byte[] file)
            {
                var area = window.AnnotationCanvas.BoundsOf(mark).Inflate(3);
                int left = Math.Max(0, (int)((area.X - picture.X) * scale)), top = Math.Max(0, (int)((area.Y - picture.Y) * scale));
                int right = Math.Min(w, (int)Math.Ceiling((area.Right - picture.X) * scale));
                int bottom = Math.Min((int)compareHeight, (int)Math.Ceiling((area.Bottom - picture.Y) * scale));
                int Apart(byte[] p, byte[] q, int i) => Math.Max(Math.Abs(p[i] - q[i]), Math.Max(Math.Abs(p[i + 1] - q[i + 1]), Math.Abs(p[i + 2] - q[i + 2])));
                int marked = 0, disagreeing = 0;
                for (var y = top; y < bottom; y++)
                {
                    for (var x = left; x < right; x++)
                    {
                        var i = (y * w + x) * 3;
                        if (Apart(shown, unmarked, i) <= 40 && Apart(file, unmarked, i) <= 40)
                            continue;
                        marked++;
                        if (Apart(shown, file, i) > 64)
                            disagreeing++;
                    }
                }
                return marked == 0 ? 0 : (double)disagreeing / marked;
            }
            // At most a tenth of a mark's pixels apart, at its edges; missing, it is nine tenths, moved by 1 % of the
            // picture a third (a highlight, mostly over itself, excepted). What a label says is the box test's to
            // check.
            foreach (var mark in vm.Marks.Marks)
            {
                var apart = Disagreeing(mark, saved);
                Assert.True(apart < 0.15, $"{mark.Kind}: the file and the window disagree on {apart:P0} of its pixels");
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(ThemeMode.Light)]
    [InlineData(ThemeMode.Dark)]
    public void Screenshot(ThemeMode mode)
    {
        var (window, vm, _) = Open(files: 2);
        try
        {
            // After Open: it initializes the services, which applies the stored theme again.
            var theme = AppHost.Get<ThemeService>();
            theme.Apply(new ThemeSettings { Mode = mode }, "en");
            theme.ApplyWindowClass(window);
            vm.Mode = AnnotateMode.Highlight;
            vm.Swatches[2].ChooseCommand.Execute(null);
            Drag(window, (0.04, 0.22), (0.72, 0.29));
            vm.Swatches[0].ChooseCommand.Execute(null);
            vm.Mode = AnnotateMode.Box;
            Drag(window, (0.03, 0.4), (0.97, 0.55));
            vm.Mode = AnnotateMode.Number;
            Click(window, 0.985, 0.475);
            vm.AddText(new AnnotationPoint { X = 0.3, Y = 0.83 }, "Save when done");
            vm.Mode = AnnotateMode.Arrow;
            Drag(window, (0.52, 0.86), (0.69, 0.82));
            vm.Mode = AnnotateMode.Select;
            vm.Marks.Select(vm.Marks.Count - 1);
            RenderTests.Pump();
            Assert.Equal(mode == ThemeMode.Dark ? ThemeVariant.Dark : ThemeVariant.Light, window.ActualThemeVariant);
            RenderTests.Save(window, $"annotate-{mode.ToString().ToLowerInvariant()}.png");
        }
        finally
        {
            window.Close();
        }
    }
}
