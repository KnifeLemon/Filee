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

    private string Picture(string name)
    {
        var path = Path.Combine(_folder, name);
        using var image = new MagickImage(new MagickColor("#FFFFFF"), 800, 500);
        new ImageMagick.Drawing.Drawables()
            .FillColor(new MagickColor("#F3F3F5")).Rectangle(0, 0, 800, 42)
            .FillColor(new MagickColor("#3E63DD")).RoundRectangle(560, 400, 760, 460, 10, 10)
            .FillColor(new MagickColor("#ECECEC")).Rectangle(40, 120, 560, 134)
            .Draw(image);
        image.Write(path, MagickFormat.Png);
        return path;
    }

    private (AnnotateWindow Window, AnnotateViewModel Vm, List<(string Path, Preset Conversion)> Exported) Open(int files = 1)
    {
        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var tool = store.FindPreset(BuiltInData.AnnotateId) ?? BuiltInData.AnnotatePresets().Single();
        AnnotateTool.Remember(tool, AnnotateTool.Colors[0], AnnotationWidth.Medium);
        var exported = new List<(string, Preset)>();
        var paths = Enumerable.Range(1, files).Select(i => Picture($"shot{i}.png")).ToList();
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
            var tag = AnnotationCanvas.BoundsOf(mark, window.AnnotationCanvas.CalculateImageBounds());
            var inside = window.AnnotationCanvas.TranslatePoint(tag.Center, window)!.Value;
            window.MouseDown(inside, MouseButton.Left);
            window.MouseUp(inside, MouseButton.Left);
            window.MouseDown(inside, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(inside, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            if (!window.Editor.IsVisible)
                return; // the headless platform counts no double-click: typing again is covered by EditText below
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
            frame.Save(framePath);
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

    [AvaloniaFact]
    public void A_label_typed_again_follows_the_text_and_the_old_one_does_not_show_behind()
    {
        var (window, vm, _) = Open();
        try
        {
            vm.AddText(new AnnotationPoint { X = 0.2, Y = 0.6 }, "Click Save when you are done here");
            var picture = window.AnnotationCanvas.CalculateImageBounds();
            var mark = vm.Marks.Marks[0];
            var oldTag = AnnotationCanvas.BoundsOf(mark, picture);
            vm.Mode = AnnotateMode.Select;
            window.AnnotationCanvas.TypeAgain(0);
            RenderTests.Pump();
            Assert.True(window.Editor.IsFocused);
            window.KeyTextInput("OK");    // the text is selected: typing replaces it
            RenderTests.Pump();
            Assert.Equal("OK", window.Editor.Text);

            // The box is the tag "OK" will be, not the old one.
            var box = (Control)window.Editor.Parent!;
            var shortTag = AnnotationCanvas.BoundsOf(new Annotation { Kind = AnnotationKind.Text, Width = mark.Width, Points = [mark.Points[0]], Text = "OK" }, picture);
            Assert.True(Math.Abs(box.Bounds.Width - shortTag.Width) < 8, $"box {box.Bounds.Width:0} wide, the tag {shortTag.Width:0}");

            // Nothing of the old tag right of the box.
            using var frame = window.CaptureRenderedFrame()!;
            var framePath = Path.Combine(_folder, "retype.png");
            frame.Save(framePath);
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

            // A picture without marks writes nothing.
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.Single(exported);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void What_the_window_draws_is_what_the_file_gets()
    {
        var (window, vm, _) = Open();
        try
        {
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
            frame.Save(framePath);
            var picture = window.AnnotationCanvas.CalculateImageBounds();
            var topLeft = window.AnnotationCanvas.TranslatePoint(picture.TopLeft, window)!.Value;
            using var drawn = new MagickImage(framePath);
            var scale = frame.PixelSize.Width / window.Bounds.Width;
            drawn.Crop(new MagickGeometry((int)Math.Round(topLeft.X * scale), (int)Math.Round(topLeft.Y * scale),
                (uint)Math.Round(picture.Width * scale), (uint)Math.Round(picture.Height * scale)));
            drawn.ResetPage();
            drawn.Alpha(AlphaOption.Remove);

            // The file: the same marks drawn by the engine, brought to that size.
            using var file = new MagickImage(Path.Combine(_folder, "shot1.png"));
            Filee.Engines.Annotations.Annotator.Apply(file, new ImageOptions { Annotations = [.. vm.Marks.Marks] });
            file.Resize(new MagickGeometry(drawn.Width, drawn.Height) { IgnoreAspectRatio = true });

            var a = file.GetPixels().ToByteArray(PixelMapping.RGB)!;
            var b = drawn.GetPixels().ToByteArray(PixelMapping.RGB)!;
            var difference = a.Zip(b, (x, y) => Math.Abs(x - y)).Average();
            Assert.True(difference < 4, $"mean difference {difference:0.00} of 255");
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
