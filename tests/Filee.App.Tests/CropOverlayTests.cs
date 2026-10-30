// The crop overlay's window: the stage with the frame and its size chip, the aspect dial, the strip of several files,
// the end summary (screenshots in light and dark, short and long languages), and its keys and ratio choices.

using System.ComponentModel;
using System.IO;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
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
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using static Filee.App.Tests.RenderTests;

namespace Filee.App.Tests;

public class CropOverlayTests
{
    public static TheoryData<string, bool> Looks => new()
    {
        { "en", false }, { "en", true }, { "ko", false }, { "ko", true }, { "ru", false }, { "hi", true }, { "ar", false }, { "ar", true },
        { "pt-PT", false }, { "pt-PT", true }, { "pt-BR", false }, { "pt-BR", true },
        { "fr", false },
        // Long one-word ratio names on eight slices (#542): "Oryginalne", "स्वतन्त्र", "Original".
        { "pl", false }, { "ne", false }, { "de", false },
    };

    [AvaloniaTheory]
    [MemberData(nameof(Looks))]
    public void One_picture_shows_the_frame_its_size_and_the_aspect_dial(string language, bool dark)
    {
        WithOverlay(language, dark, ["harbour.jpg"], (window, vm, suffix) =>
        {
            Assert.False(vm.HasMultipleFiles);
            Assert.Equal(AppHost.Get<ILocalizer>()["crop.save"], vm.SaveText);

            vm.SetAspect("4:3");
            window.CropCanvas.NormalizedCropRect = new Rect(0.30, 0.18, 0.5, 0.6);
            Settle(window);
            Assert.Equal("800 × 600 px", vm.DimensionsText);
            Save(window, $"crop-single-{suffix}.png");

            vm.ToggleAspectDial();
            Settle(window);
            Assert.True(window.AspectDial.IsVisible);
            Assert.Equal("4:3", window.AspectDial.Segments.Single(s => s.IsSelected).Mode);
            Save(window, $"crop-dial-{suffix}.png");
        });
    }

    [AvaloniaTheory]
    [MemberData(nameof(Looks))]
    public void Several_files_show_a_strip_with_how_each_went(string language, bool dark)
    {
        WithOverlay(language, dark, ["one.jpg", "two.jpg", "spinner.gif", "four.jpg", "five.jpg"], (window, vm, suffix) =>
        {
            Assert.True(vm.HasMultipleFiles);
            Assert.Equal(AppHost.Get<ILocalizer>()["crop.save_next"], vm.SaveText);

            _ = vm.CropAndSaveAsync();
            WaitFor(() => vm.CurrentIndex == 1 && !vm.IsBusy);
            _ = vm.SkipAsync();
            // The animated GIF can't be cropped: it fails on its own and the fourth file opens.
            WaitFor(() => vm.CurrentIndex == 3 && !vm.IsBusy && vm.PreviewImage is not null);

            Assert.Equal(
                [CropItemState.Done, CropItemState.Skipped, CropItemState.Failed, CropItemState.Current, CropItemState.Waiting],
                vm.Queue.Select(q => q.State));
            Assert.All(vm.Queue.Take(2).Append(vm.Queue[3]), q => Assert.NotNull(q.Thumbnail));
            Assert.Null(vm.Queue[4].Thumbnail);
            Assert.True(vm.HasStatusNotice);

            window.CropCanvas.NormalizedCropRect = new Rect(0.12, 0.2, 0.6, 0.55);
            Settle(window);
            Save(window, $"crop-multi-{suffix}.png");

            // Several problems: the summary stays until closed, with a slice per file.
            _ = vm.CropAndSaveAsync();
            WaitFor(() => vm.CurrentIndex == 4 && !vm.IsBusy);
            _ = vm.CropAndSaveAsync();
            WaitFor(() => vm.IsFinished);
            Assert.Equal((3, 1, 1), (vm.DoneCount, vm.SkippedCount, vm.FailedCount));
            Settle(window);
            Save(window, $"crop-summary-{suffix}.png");
        });
    }

    [AvaloniaFact]
    public void Dragging_shows_the_grid_and_the_size_chip_follows_the_frame()
    {
        WithOverlay("en", false, ["harbour.jpg"], (window, vm, _suffix) =>
        {
            var canvas = window.CropCanvas;
            var image = canvas.CalculateImageBounds();
            var origin = canvas.TranslatePoint(default, window)!.Value;
            var corner = canvas.GetScreenCropRect(image).BottomRight + (Vector)origin;
            window.MouseDown(corner, MouseButton.Left);
            window.MouseMove(corner - new Vector(image.Width * 0.3, image.Height * 0.25));
            Pump();
            canvas.FinishAnimations();
            var size = CropMath.PixelSize(canvas.NormalizedCropRect, 1600, 1000);
            Assert.InRange(size.Width, 1100, 1140);
            Assert.Equal($"{size.Width} × {size.Height} px", vm.DimensionsText);
            Save(window, "crop-drag-en.png");
            window.MouseUp(corner, MouseButton.Left);
        });
    }

    [AvaloniaFact]
    public void A_frame_moved_near_the_middle_snaps_to_the_centre_lines()
    {
        WithOverlay("en", false, ["harbour.jpg"], (window, vm, _suffix) =>
        {
            var canvas = window.CropCanvas;
            canvas.NormalizedCropRect = new Rect(0.1, 0.1, 0.5, 0.5); // centre at 35 %, 35 %
            Settle(window);
            var image = canvas.CalculateImageBounds();
            var origin = canvas.TranslatePoint(default, window)!.Value;
            var start = canvas.GetScreenCropRect(image).Center + (Vector)origin;
            // To 3 screen pixels short of the picture's centre on both axes: inside the snap distance.
            var target = image.Center + (Vector)origin - new Vector(3, 3);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + (target - start) / 2);
            window.MouseMove(target);
            Pump();
            Save(window, "crop-snap-en.png");
            window.MouseUp(target, MouseButton.Left);

            var frame = canvas.NormalizedCropRect;
            Assert.Equal(0.5, frame.X + frame.Width / 2, 3);
            Assert.Equal(0.5, frame.Y + frame.Height / 2, 3);
        });
    }

    [AvaloniaFact]
    public void Keys_nudge_the_frame_open_the_dial_reset_and_close()
    {
        WithOverlay("en", false, ["harbour.jpg"], (window, vm, _suffix) =>
        {
            var canvas = window.CropCanvas;
            canvas.NormalizedCropRect = new Rect(0.25, 0.25, 0.5, 0.5); // 400, 250, 800 × 500
            Press(window, Key.Right);
            Press(window, Key.Right);
            Press(window, Key.Down, RawInputModifiers.Shift);
            Assert.Equal(new Rect(402 / 1600.0, 260 / 1000.0, 0.5, 0.5), canvas.NormalizedCropRect);
            Assert.Equal(canvas.NormalizedCropRect, vm.CurrentCropRect);

            // Stops at the picture's edge.
            for (var i = 0; i < 50; i++)
                Press(window, Key.Left, RawInputModifiers.Shift);
            Assert.Equal(0, canvas.NormalizedCropRect.X);

            // A opens the dial: arrows then go round its slices instead of moving the frame, Esc closes only the dial.
            Press(window, Key.A);
            Assert.True(vm.IsAspectDialOpen);
            Pump();
            var before = canvas.NormalizedCropRect;
            Press(window, Key.Right);
            Assert.Equal(before, canvas.NormalizedCropRect);
            Press(window, Key.Escape);
            Assert.False(vm.IsAspectDialOpen);
            Assert.True(window.IsVisible);

            // X swaps a fixed ratio; R resets the ratio and the frame.
            vm.SetAspect("16:9");
            Press(window, Key.X);
            Assert.True(vm.IsPortrait);
            Assert.Equal(9.0 / 16.0, canvas.AspectRatio, 6);
            Press(window, Key.R);
            Assert.Equal("free", vm.SelectedAspect);
            Assert.False(vm.IsPortrait);
            Assert.Equal(0, canvas.AspectRatio);
            Assert.Equal(new Rect(0, 0, 1, 1), canvas.NormalizedCropRect);

            var closed = false;
            window.Closed += (_, _) => closed = true;
            Press(window, Key.Escape);
            Assert.True(closed);
        });
    }

    [AvaloniaTheory]
    [InlineData("en", false)]
    [InlineData("en", true)]
    [InlineData("ar", false)]
    public void A_over_the_picture_opens_the_dial_around_the_pointer(string language, bool dark)
    {
        WithOverlay(language, dark, ["harbour.jpg", "two.jpg"], (window, vm, suffix) =>
        {
            var canvas = window.CropCanvas;
            var dial = window.AspectDial;
            var origin = (Vector)canvas.TranslatePoint(default, window)!.Value;
            var image = canvas.CalculateImageBounds().Translate(origin);
            Point DialCentre() => dial.TranslatePoint(new Point(dial.Bounds.Width / 2, dial.Bounds.Height / 2), window)!.Value;

            // No pointer over the stage yet (the keyboard): over its button, as before.
            Press(window, Key.A);
            Settle(window);
            Assert.True(vm.IsAspectDialOpen);
            var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DialButton");
            var buttonTop = button.TranslatePoint(new Point(button.Bounds.Width / 2, 0), window)!.Value;
            Assert.Equal(buttonTop.X, DialCentre().X, 0.5);
            Assert.Equal(buttonTop.Y - CropMath.DialGap, dial.TranslatePoint(new Point(0, dial.Bounds.Height), window)!.Value.Y, 0.5);
            Press(window, Key.Escape);
            Assert.False(vm.IsAspectDialOpen);
            Assert.True(window.IsVisible);

            // The pointer over the picture: A opens the dial around it.
            var at = new Point(image.X + image.Width * 0.62, image.Y + image.Height * 0.4);
            window.MouseMove(at);
            Press(window, Key.A);
            Settle(window);
            Assert.True(vm.IsAspectDialOpen);
            Assert.Equal(at.X, DialCentre().X, 0.5);
            Assert.Equal(at.Y, DialCentre().Y, 0.5);
            Save(window, $"crop-dial-pointer-{suffix}.png");

            // The arrows go round the slices, not the frame; A again closes it.
            var frame = canvas.NormalizedCropRect;
            Press(window, Key.Right);
            Assert.Equal(frame, canvas.NormalizedCropRect);
            Press(window, Key.A);
            Assert.False(vm.IsAspectDialOpen);

            // Picked with a click: the slice under the pointer.
            Press(window, Key.A);
            Settle(window);
            var square = dial.Segments.ToList().FindIndex(s => s.Mode == "1:1");
            var onSquare = dial.TranslatePoint(DonutGeometry.PointAt(new Point(dial.Bounds.Width / 2, dial.Bounds.Height / 2),
                (dial.InnerRadius + dial.OuterRadius) / 2, DonutGeometry.MidAngle(square, dial.Segments.Count())), window)!.Value;
            window.MouseMove(onSquare);
            window.MouseDown(onSquare, MouseButton.Left);
            window.MouseUp(onSquare, MouseButton.Left);
            Pump();
            Assert.Equal("1:1", vm.SelectedAspect);
            Assert.False(vm.IsAspectDialOpen);

            // Near a corner of the window: moved in until the whole dial shows.
            var corner = new Point(window.Bounds.Width - 30, 30);
            window.MouseMove(corner);
            Press(window, Key.A);
            Settle(window);
            var stage = dial.GetVisualParent<Visual>()!;
            var bounds = new Rect(dial.TranslatePoint(default, stage)!.Value, dial.Bounds.Size);
            Assert.True(new Rect(stage.Bounds.Size).Deflate(CropMath.DialMargin - 0.5).Contains(bounds), $"{bounds} outside {stage.Bounds}");
            Press(window, Key.Escape);

            // The toolbar button opens it over the button again, whatever A did before.
            window.MouseMove(at);
            vm.ToggleAspectDial();
            Settle(window);
            Assert.Equal(buttonTop.X, DialCentre().X, 0.5);
        });
    }

    [AvaloniaFact]
    public void Fixed_ratios_follow_the_pictures_orientation_and_swap()
    {
        WithOverlay("en", false, ["tall.jpg"], (window, vm, _suffix) =>
        {
            Assert.True(vm.IsPortrait); // a portrait picture starts with the portrait forms
            vm.SetAspect("4:3");
            Assert.Equal(3.0 / 4.0, vm.AspectRatio, 6);
            Assert.Equal("3:4", vm.AspectLabel);
            Assert.True(vm.CanSwapOrientation);

            vm.SwapOrientation();
            Assert.False(vm.IsPortrait);
            Assert.Equal(4.0 / 3.0, window.CropCanvas.AspectRatio, 6);
            Assert.Equal("4:3", vm.AspectLabel);
            var frame = window.CropCanvas.NormalizedCropRect;
            Assert.Equal(4.0 / 3.0, frame.Width * 1000 / (frame.Height * 1600), 2);

            // Square, free and the picture's own ratio have no second form.
            vm.SetAspect("1:1");
            Assert.False(vm.CanSwapOrientation);
            vm.SwapOrientation();
            Assert.Equal(1.0, vm.AspectRatio);
            vm.SetAspect("original");
            Assert.Equal(1000.0 / 1600.0, vm.AspectRatio, 6);
            Assert.Equal("Original", vm.AspectLabel);
        });
    }

    [AvaloniaFact]
    public void Every_language_fits_its_longest_one_word_ratio_name_on_a_slice_without_breaking_it()
    {
        var dial = new AspectDial();
        var width = RingSegment.LabelWidth(dial.InnerRadius, dial.OuterRadius, CropMath.AspectModes.Count);
        var i18n = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Filee.App", "Assets", "i18n");
        foreach (var language in LocalizationService.Languages.Select(l => l.Code))
        {
            var strings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(i18n, language + ".json")))!;
            var typeface = new Avalonia.Media.Typeface(SoftPalette.FontFamilyFor(language), Avalonia.Media.FontStyle.Normal, Avalonia.Media.FontWeight.SemiBold);
            foreach (var key in new[] { "crop.aspect_free", "crop.aspect_original" })
            {
                if (!strings.TryGetValue(key, out var label))
                    continue;
                var longest = label.Split(' ').Max(word => new Avalonia.Media.FormattedText(word, System.Globalization.CultureInfo.InvariantCulture,
                    Avalonia.Media.FlowDirection.LeftToRight, typeface, 12, Avalonia.Media.Brushes.Black).Width);
                Assert.True(12 * width / longest >= RingSegment.MinLabelSize, $"{language} {key} \"{label}\" needs {12 * width / longest:F2}");
            }
        }
    }

    [AvaloniaFact]
    public void An_upright_picture_crops_at_instagrams_4_to_5_from_the_5_to_4_slice()
    {
        WithOverlay("en", false, ["tall.jpg"], (window, vm, _suffix) =>
        {
            Settle(window);
            var slice = window.AspectDial.Segments.Single(s => s.Mode == "5:4");
            slice.Command!.Execute(slice.CommandParameter);
            Settle(window);
            Assert.Equal("4:5", vm.AspectLabel);
            Assert.Equal(0.8, window.CropCanvas.AspectRatio, 6);
            var frame = window.CropCanvas.NormalizedCropRect;
            Assert.Equal(0.8, frame.Width * 1000 / (frame.Height * 1600), 2);
            vm.ToggleAspectDial();
            Settle(window);
            RenderTests.Save(window, "crop-dial-4-5.png");
        });
    }

    [AvaloniaFact]
    public void A_custom_ratio_crops_at_the_typed_w_by_h_and_keeps_it_for_the_batch()
    {
        // #542: "a custom ratio such as 4:5 or 1080 × 1350": only the ratio counts.
        WithOverlay("en", false, ["harbour.jpg", "tall.jpg"], (window, vm, _suffix) =>
        {
            Settle(window);
            Assert.False(window.CustomWidthBox.IsEffectivelyVisible);
            var slice = window.AspectDial.Segments.Single(s => s.Mode == "custom");
            slice.Command!.Execute(slice.CommandParameter);
            vm.CustomWidth = 1080;
            vm.CustomHeight = 1350;
            Settle(window);

            // As typed, though the picture is landscape: upright 4:5, shown on the button and the slice.
            Assert.True(window.CustomWidthBox.IsEffectivelyVisible);
            Assert.Equal("4:5", vm.AspectLabel);
            Assert.Equal("4:5", slice.Label);
            Assert.Equal(0.8, vm.AspectRatio, 6);
            Assert.Equal(0.8, window.CropCanvas.AspectRatio, 6);
            var frame = window.CropCanvas.NormalizedCropRect;
            Assert.True(Math.Abs(frame.Width * 1600 / (frame.Height * 1000) - 0.8) < 0.01, $"frame {frame}, vm {vm.CurrentCropRect}, {vm.DimensionsText}");
            Assert.Equal("800 × 1000 px", vm.DimensionsText);

            // The hub turns it, as the other ratios.
            vm.SwapOrientation();
            Assert.Equal("5:4", vm.AspectLabel);
            Assert.Equal(1.25, vm.AspectRatio, 6);
            vm.SwapOrientation();
            vm.ToggleAspectDial();
            Settle(window);
            RenderTests.Save(window, "crop-dial-custom.png");
            vm.ToggleAspectDial();

            // The next picture keeps it, as large as it fits.
            _ = vm.CropAndSaveAsync();
            WaitFor(() => vm.CurrentIndex == 1 && !vm.IsBusy && vm.PreviewImage is not null);
            Pump();
            Assert.Equal("custom", vm.SelectedAspect);
            Assert.Equal((1080, 1350), (vm.CustomWidth, vm.CustomHeight));
            Assert.Equal(0.8, vm.AspectRatio, 6);
            AssertClose(CropMath.Centered(0.8, 1000, 1600), vm.CurrentCropRect);
        });
    }

    [AvaloniaFact]
    public void Typing_in_the_custom_boxes_leaves_the_shortcuts_alone()
    {
        WithOverlay("en", false, ["harbour.jpg"], (window, vm, _suffix) =>
        {
            vm.SetAspect("custom");
            Settle(window);
            var before = window.CropCanvas.NormalizedCropRect;
            window.Activate();
            Assert.True(window.CustomWidthBox.GetVisualDescendants().OfType<TextBox>().First().Focus());
            Pump();
            Press(window, Key.R);
            Press(window, Key.Right);
            Pump();

            // R and the arrows belong to the box: no reset to Free, no nudged frame.
            Assert.Equal("custom", vm.SelectedAspect);
            Assert.Equal(before, window.CropCanvas.NormalizedCropRect);
        });
    }

    [AvaloniaFact]
    public void The_next_file_keeps_the_chosen_ratio_as_large_as_it_fits_centred_in_view_and_canvas_alike()
    {
        // #542: a batch for Instagram picks 4:5 once, not for every picture.
        WithOverlay("en", false, ["harbour.jpg", "tall.jpg"], (window, vm, _suffix) =>
        {
            vm.SetAspect("16:9");
            vm.SwapOrientation(); // 9:16
            _ = vm.CropAndSaveAsync();
            WaitFor(() => vm.CurrentIndex == 1 && !vm.IsBusy && vm.PreviewImage is not null);
            Pump();

            Assert.Equal("16:9", vm.SelectedAspect);
            Assert.True(vm.IsPortrait);
            Assert.Equal("9:16", vm.AspectLabel);
            Assert.Equal(9.0 / 16, vm.AspectRatio, 6);
            Assert.Equal(9.0 / 16, window.CropCanvas.AspectRatio, 6);
            // 1000 × 1600: the whole height, 900 wide, in the middle.
            var frame = new Rect(0.05, 0, 0.9, 1);
            AssertClose(frame, window.CropCanvas.NormalizedCropRect);
            AssertClose(frame, vm.CurrentCropRect);
            Assert.Equal("900 × 1600 px", vm.DimensionsText);
            Assert.Equal(AppHost.Get<ILocalizer>()["crop.save"], vm.SaveText);
        });
    }

    [AvaloniaFact]
    public void A_batch_starts_free_until_a_ratio_is_picked_and_again_after_reset()
    {
        WithOverlay("en", false, ["harbour.jpg", "tall.jpg", "two.jpg"], (window, vm, _suffix) =>
        {
            // Nothing picked: the next picture starts free, on the whole picture.
            _ = vm.CropAndSaveAsync();
            WaitFor(() => vm.CurrentIndex == 1 && !vm.IsBusy && vm.PreviewImage is not null);
            Pump();
            Assert.Equal("free", vm.SelectedAspect);
            Assert.Equal(0, window.CropCanvas.AspectRatio);
            Assert.True(vm.IsPortrait); // the second picture is upright
            Assert.Equal(new Rect(0, 0, 1, 1), window.CropCanvas.NormalizedCropRect);
            Assert.Equal("1000 × 1600 px", vm.DimensionsText);

            // Picked, then R (Reset): back to free for this picture and the next.
            vm.SetAspect("4:3");
            vm.Reset();
            _ = vm.CropAndSaveAsync();
            WaitFor(() => vm.CurrentIndex == 2 && !vm.IsBusy && vm.PreviewImage is not null);
            Pump();
            Assert.Equal("free", vm.SelectedAspect);
            Assert.False(vm.IsPortrait);
            Assert.Equal(new Rect(0, 0, 1, 1), window.CropCanvas.NormalizedCropRect);
            Assert.Equal("1600 × 1000 px", vm.DimensionsText);
        });
    }

    [AvaloniaFact]
    public async Task Perspective_starts_off_sits_after_the_ratio_button_and_starts_on_the_picture_corners()
    {
        await WithOverlayAsync("en", false, ["harbour.jpg"], (window, vm, _) =>
        {
            var button = window.FindControl<ToggleButton>("PerspectiveButton")!;
            var canvas = window.CropCanvas;
            Assert.False(button.IsChecked);
            Assert.False(canvas.IsPerspective);
            var toolbar = (StackPanel)button.Parent!;
            Assert.Same(button, toolbar.Children[toolbar.Children.IndexOf(window.FindControl<Button>("DialButton")!) + 1]);
            canvas.NormalizedCropRect = new Rect(0.3, 0.2, 0.4, 0.5);
            Assert.Equal("640 × 500 px", vm.DimensionsText);
            ClickPerspective(window);
            Assert.True(canvas.IsPerspective);
            Assert.Equal(CropQuad.Full, canvas.NormalizedCorners);
            Assert.Equal("1600 × 1000 px", vm.DimensionsText);
            canvas.NormalizedCorners = CropQuad.Full.With(0, new Point(0.2, 0.1));
            ClickPerspective(window);
            ClickPerspective(window);
            Assert.Equal(CropQuad.Full, canvas.NormalizedCorners);
            Assert.Equal("1600 × 1000 px", vm.DimensionsText);
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task Each_corner_drags_on_its_own()
    {
        await WithOverlayAsync("en", false, ["harbour.jpg"], (window, vm, _) =>
        {
            ClickPerspective(window);
            var canvas = window.CropCanvas;
            canvas.NormalizedCorners = new CropQuad(new Point(0.15, 0.12), new Point(0.82, 0.20), new Point(0.90, 0.85), new Point(0.10, 0.78));
            var image = canvas.CalculateImageBounds();
            for (var i = 0; i < 4; i++)
            {
                var before = canvas.NormalizedCorners;
                // Grabbed a few pixels off the dot: the corner follows the pointer's travel, it doesn't jump to it.
                var start = canvas.TranslatePoint(canvas.CornerScreenPoint(i), window)!.Value + new Vector(6, -5);
                Drag(window, start, start + new Vector(20, 15));
                Assert.InRange(canvas.NormalizedCorners[i].X, before[i].X + 20 / image.Width - 0.002, before[i].X + 20 / image.Width + 0.002);
                Assert.InRange(canvas.NormalizedCorners[i].Y, before[i].Y + 15 / image.Height - 0.002, before[i].Y + 15 / image.Height + 0.002);
                foreach (var other in Enumerable.Range(0, 4).Where(c => c != i))
                    Assert.Equal(before[other], canvas.NormalizedCorners[other]);
                Assert.True(CornerButtons(window)[i].IsFocused);
                Assert.Equal(i, canvas.SelectedCorner);
            }
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task Tab_during_a_drag_leaves_the_dragged_corner_in_hand()
    {
        await WithOverlayAsync("en", false, ["harbour.jpg"], (window, vm, _) =>
        {
            ClickPerspective(window);
            var canvas = window.CropCanvas;
            var image = canvas.CalculateImageBounds();
            var start = canvas.TranslatePoint(canvas.CornerScreenPoint(0), window)!.Value;
            Pump();
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + new Vector(20, 15));
            Pump();
            Press(window, Key.Tab);
            Assert.Equal(1, canvas.SelectedCorner);
            window.MouseMove(start + new Vector(40, 25));
            window.MouseUp(start + new Vector(40, 25), MouseButton.Left);
            Pump();
            Assert.InRange(canvas.NormalizedCorners[0].X, 40 / image.Width - 0.002, 40 / image.Width + 0.002);
            Assert.InRange(canvas.NormalizedCorners[0].Y, 25 / image.Height - 0.002, 25 / image.Height + 0.002);
            foreach (var other in new[] { 1, 2, 3 })
                Assert.Equal(CropQuad.Full[other], canvas.NormalizedCorners[other]);
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task Switching_perspective_mid_drag_lets_go_of_the_drag()
    {
        // Frame held, perspective turned on from the keyboard: the held frame body moves no corner.
        await WithOverlayAsync("en", false, ["harbour.jpg"], (window, vm, _) =>
        {
            var canvas = window.CropCanvas;
            var toggle = window.FindControl<ToggleButton>("PerspectiveButton")!;
            var image = canvas.CalculateImageBounds();
            var r = canvas.NormalizedCropRect;
            var start = canvas.TranslatePoint(new Point(image.X + (r.X + r.Width / 2) * image.Width, image.Y + (r.Y + r.Height / 2) * image.Height), window)!.Value;
            Pump();
            toggle.Focus(NavigationMethod.Tab);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + new Vector(10, 8));
            Pump();
            Assert.True(toggle.IsFocused);
            Press(window, Key.Space);
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.None, null);
            Pump();
            Assert.True(canvas.IsPerspective);
            // Up and right: read as a corner's travel, it would be a move the bottom-left corner is allowed to make.
            window.MouseMove(start + new Vector(60, -40));
            window.MouseUp(start + new Vector(60, -40), MouseButton.Left);
            Pump();
            Assert.Equal(CropQuad.Full, canvas.NormalizedCorners);
            return Task.CompletedTask;
        });
        // A corner held, perspective turned off: the frame that comes back is the one from before.
        await WithOverlayAsync("en", false, ["harbour.jpg"], (window, vm, _) =>
        {
            var canvas = window.CropCanvas;
            var toggle = window.FindControl<ToggleButton>("PerspectiveButton")!;
            var before = canvas.NormalizedCropRect;
            ClickPerspective(window);
            var start = canvas.TranslatePoint(canvas.CornerScreenPoint(0), window)!.Value;
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + new Vector(20, 15));
            Pump();
            toggle.Focus(NavigationMethod.Tab);
            Assert.True(toggle.IsFocused);
            Press(window, Key.Space);
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.None, null);
            Pump();
            Assert.False(canvas.IsPerspective);
            window.MouseMove(start + new Vector(60, 40));
            window.MouseUp(start + new Vector(60, 40), MouseButton.Left);
            Pump();
            Assert.Equal(before, canvas.NormalizedCropRect);
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task A_corner_held_while_the_next_picture_opens_doesnt_move_its_corners()
    {
        await WithOverlayAsync("en", false, ["harbour.jpg", "two.jpg"], async (window, vm, _) =>
        {
            ClickPerspective(window);
            var canvas = window.CropCanvas;
            var start = canvas.TranslatePoint(canvas.CornerScreenPoint(0), window)!.Value;
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + new Vector(20, 15));
            Pump();
            var first = canvas.SourceImage;
            await vm.CropAndSaveAsync();
            Pump();
            Assert.NotSame(first, canvas.SourceImage);
            Assert.Equal(CropQuad.Full, canvas.NormalizedCorners);
            window.MouseMove(start + new Vector(60, 40));
            window.MouseUp(start + new Vector(60, 40), MouseButton.Left);
            Pump();
            Assert.Equal(CropQuad.Full, canvas.NormalizedCorners);
        });
    }

    [AvaloniaFact]
    public async Task A_corner_isnt_dragged_or_nudged_into_a_broken_shape_or_off_the_picture()
    {
        await WithOverlayAsync("en", false, ["harbour.jpg"], (window, vm, _) =>
        {
            ClickPerspective(window);
            var canvas = window.CropCanvas;
            var rectangle = new CropQuad(new Point(320, 200), new Point(960, 200), new Point(960, 800), new Point(320, 800));
            var cases = new[]
            {
                (Kind: "twisted", Keys: false, Quad: rectangle, Target: new Point(1120, 500), Key: Key.Right, Shift: false),
                (Kind: "concave", Keys: false, Quad: rectangle, Target: new Point(700, 650), Key: Key.Right, Shift: false),
                (Kind: "line", Keys: false, Quad: rectangle, Target: new Point(640, 500), Key: Key.Right, Shift: false),
                (Kind: "outside", Keys: false, Quad: rectangle.With(0, new Point(0, 200)), Target: new Point(-50, 200), Key: Key.Left, Shift: false),
                (Kind: "twisted", Keys: true, Quad: new CropQuad(new Point(950, 200), new Point(955, 200), new Point(955, 800), new Point(950, 800)), Target: new Point(960, 200), Key: Key.Right, Shift: true),
                (Kind: "concave", Keys: true, Quad: rectangle.With(0, new Point(580, 550)), Target: new Point(590, 550), Key: Key.Right, Shift: true),
                (Kind: "line", Keys: true, Quad: rectangle.With(0, new Point(639, 500)), Target: new Point(640, 500), Key: Key.Right, Shift: false),
                (Kind: "outside", Keys: true, Quad: rectangle.With(0, new Point(0, 200)), Target: new Point(-1, 200), Key: Key.Left, Shift: false),
            };
            foreach (var test in cases)
            {
                var q = test.Quad;
                var normalized = new CropQuad(new Point(q[0].X / 1600, q[0].Y / 1000), new Point(q[1].X / 1600, q[1].Y / 1000),
                    new Point(q[2].X / 1600, q[2].Y / 1000), new Point(q[3].X / 1600, q[3].Y / 1000));
                Assert.True(CropMath.IsValidQuad(normalized, 1600, 1000), test.Kind);
                AssertForbiddenKind(test.Kind, q.With(0, test.Target));
                canvas.NormalizedCorners = normalized;
                CornerButtons(window)[0].Focus(NavigationMethod.Tab);
                Pump();
                if (test.Keys)
                    Press(window, test.Key, test.Shift ? RawInputModifiers.Shift : RawInputModifiers.None);
                else
                {
                    var image = canvas.CalculateImageBounds();
                    var target = new Point(image.X + test.Target.X / 1600 * image.Width, image.Y + test.Target.Y / 1000 * image.Height);
                    Drag(window, canvas.TranslatePoint(canvas.CornerScreenPoint(0), window)!.Value, canvas.TranslatePoint(target, window)!.Value);
                }
                Assert.Equal(normalized, canvas.NormalizedCorners);
            }
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task A_corner_dragged_or_nudged_past_the_edge_stops_on_it()
    {
        await WithOverlayAsync("en", false, ["harbour.jpg"], (window, vm, _) =>
        {
            ClickPerspective(window);
            var canvas = window.CropCanvas;
            var start = new CropQuad(new Point(5 / 1600.0, 0.2), new Point(0.6, 0.2), new Point(0.6, 0.8), new Point(0.2, 0.8));

            canvas.NormalizedCorners = start;
            Pump();
            var image = canvas.CalculateImageBounds();
            var from = canvas.TranslatePoint(canvas.CornerScreenPoint(0), window)!.Value;
            Drag(window, from, from + new Vector(-55 / 1600.0 * image.Width, 0));
            Assert.Equal(0, canvas.NormalizedCorners.TopLeft.X);
            Assert.Equal(0.2, canvas.NormalizedCorners.TopLeft.Y, 3);

            canvas.NormalizedCorners = start.With(0, new Point(3 / 1600.0, 0.2));
            CornerButtons(window)[0].Focus(NavigationMethod.Tab);
            Pump();
            Press(window, Key.Left, RawInputModifiers.Shift);
            Assert.Equal(start.With(0, new Point(0, 0.2)), canvas.NormalizedCorners);
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task Only_the_ratio_and_perspective_buttons_fade_while_disabled()
    {
        await WithOverlayAsync("en", false, ["harbour.jpg", "tall.jpg"], (window, vm, _) =>
        {
            ClickPerspective(window);
            var dial = window.FindControl<Button>("DialButton")!;
            Assert.False(dial.IsEffectivelyEnabled);
            Assert.Equal(0.45, dial.Opacity, 3);

            var skip = window.GetVisualDescendants().OfType<Button>().Single(b => b.Command == vm.SkipCommand);
            skip.IsEnabled = false;
            Pump();
            Assert.False(skip.IsEffectivelyEnabled);
            Assert.Equal(1, skip.Opacity);
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task Tab_picks_a_corner_and_arrows_move_it()
    {
        await WithOverlayAsync("en", false, ["harbour.jpg", "tall.jpg"], async (window, vm, _) =>
        {
            ClickPerspective(window);
            var canvas = window.CropCanvas;
            var buttons = CornerButtons(window);
            Assert.True(buttons[0].IsFocused);
            Assert.Equal(0, canvas.SelectedCorner);
            for (var i = 1; i < 4; i++)
            {
                Press(window, Key.Tab);
                Assert.True(buttons[i].IsFocused);
                Assert.Equal(i, canvas.SelectedCorner);
            }
            for (var i = 2; i >= 0; i--)
            {
                Press(window, Key.Tab, RawInputModifiers.Shift);
                Assert.True(buttons[i].IsFocused);
                Assert.Equal(i, canvas.SelectedCorner);
            }
            Press(window, Key.Tab);
            Press(window, Key.Right);
            Assert.Equal(CropQuad.Full, canvas.NormalizedCorners);
            Press(window, Key.Left);
            Press(window, Key.Left);
            Press(window, Key.Down, RawInputModifiers.Shift);
            Assert.Equal(CropQuad.Full.With(1, new Point(1598.0 / 1600, 10.0 / 1000)), canvas.NormalizedCorners);
            Press(window, Key.Tab);
            Press(window, Key.Tab);
            Press(window, Key.R);
            Assert.True(buttons[3].IsFocused);
            Assert.Equal(3, canvas.SelectedCorner);
            Press(window, Key.Up);
            Assert.Equal(CropQuad.Full.With(3, new Point(0, 999.0 / 1000)), canvas.NormalizedCorners);
            var loaded = UntilAsync(vm, () => vm.CurrentIndex == 1 && !vm.IsBusy);
            Press(window, Key.Enter);
            await loaded;
            Pump();
            Assert.True(buttons[3].IsFocused);
            Assert.Equal(3, canvas.SelectedCorner);
            Press(window, Key.Up);
            Assert.Equal(CropQuad.Full.With(3, new Point(0, 1599.0 / 1600)), canvas.NormalizedCorners);
            Settle(window);
            Save(window, "crop-perspective-keys-en.png");
        });
    }

    [AvaloniaFact]
    public async Task Perspective_turns_off_the_ratio_the_frame_moves_and_the_wheel()
    {
        await WithOverlayAsync("en", false, ["harbour.jpg"], (window, vm, _) =>
        {
            vm.SetAspect("4:3");
            ClickPerspective(window);
            var canvas = window.CropCanvas;
            Assert.False(window.FindControl<Button>("DialButton")!.IsEnabled);
            Assert.False(window.CustomWidthBox.IsEnabled);
            Assert.False(window.CustomHeightBox.IsEnabled);
            var portrait = vm.IsPortrait;
            Press(window, Key.A);
            Assert.False(vm.IsAspectDialOpen);
            Press(window, Key.X);
            Assert.Equal(portrait, vm.IsPortrait);
            Assert.Equal("4:3", vm.SelectedAspect);
            canvas.NormalizedCorners = new CropQuad(new Point(0.2, 0.2), new Point(0.8, 0.2), new Point(0.8, 0.8), new Point(0.2, 0.8));
            var before = canvas.NormalizedCorners;
            var rect = canvas.NormalizedCropRect;
            var image = canvas.CalculateImageBounds();
            foreach (var p in new[] { new Point(0.5, 0.2), new Point(0.5, 0.5), new Point(0.1, 0.5) })
            {
                var start = canvas.TranslatePoint(new Point(image.X + image.Width * p.X, image.Y + image.Height * p.Y), window)!.Value;
                Drag(window, start, start + new Vector(20, 15));
                window.MouseWheel(start, new Vector(0, -1));
                Pump();
                Assert.Equal(before, canvas.NormalizedCorners);
                Assert.Equal(rect, canvas.NormalizedCropRect);
            }
            var size = CropMath.PerspectiveSize(before, 1600, 1000);
            Assert.Equal($"{size.Width} × {size.Height} px", vm.DimensionsText);
            canvas.NormalizedCropRect = new Rect(0.1, 0.1, 0.3, 0.3);
            Assert.Equal($"{size.Width} × {size.Height} px", vm.DimensionsText);
            Press(window, Key.R);
            Assert.Equal(CropQuad.Full, canvas.NormalizedCorners);
            Assert.True(canvas.IsPerspective);
            Assert.Equal("4:3", vm.SelectedAspect);
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task Turning_perspective_off_brings_back_the_frame_or_the_next_pictures_first_frame()
    {
        await WithOverlayAsync("en", false, ["harbour.jpg"], (window, vm, _) =>
        {
            vm.SetAspect("4:3");
            window.CropCanvas.NormalizedCropRect = new Rect(0.2, 0.15, 0.5, 0.6);
            var before = window.CropCanvas.NormalizedCropRect;
            var dimensions = vm.DimensionsText;
            var portrait = vm.IsPortrait;
            ClickPerspective(window);
            window.CropCanvas.NormalizedCorners = CropQuad.Full.With(0, new Point(0.2, 0.1));
            ClickPerspective(window);
            Assert.Equal(before, window.CropCanvas.NormalizedCropRect);
            Assert.Equal("4:3", vm.SelectedAspect);
            Assert.Equal(portrait, vm.IsPortrait);
            Assert.Equal(dimensions, vm.DimensionsText);
            return Task.CompletedTask;
        });
        foreach (var custom in new[] { false, true })
        {
            await WithOverlayAsync("en", false, ["harbour.jpg", "tall.jpg"], async (window, vm, _) =>
            {
                vm.SetAspect(custom ? "custom" : "16:9");
                if (custom)
                    (vm.CustomWidth, vm.CustomHeight) = (4, 5);
                else
                    vm.SwapOrientation();
                ClickPerspective(window);
                window.CropCanvas.NormalizedCorners = new CropQuad(new Point(0.2, 0.1), new Point(0.8, 0.1), new Point(0.8, 0.9), new Point(0.2, 0.9));
                var size = CropMath.PerspectiveSize(window.CropCanvas.NormalizedCorners, 1600, 1000);
                var folder = (string)window.Tag!;
                Assert.True(vm.IsPerspectiveOn);
                await vm.CropAndSaveAsync();
                Pump();
                using (var output = new MagickImage(Path.Combine(folder, "harbour_cropped.jpg")))
                    Assert.Equal(((uint)size.Width, (uint)size.Height), (output.Width, output.Height));
                ClickPerspective(window);
                AssertClose(CropMath.Centered(custom ? 4.0 / 5 : 9.0 / 16, 1000, 1600), window.CropCanvas.NormalizedCropRect);
                Assert.Equal(custom ? "custom" : "16:9", vm.SelectedAspect);
                Assert.Equal(!custom, vm.IsPortrait);
                if (custom)
                    Assert.Equal((4, 5), (vm.CustomWidth, vm.CustomHeight));
            });
        }
    }

    [AvaloniaFact]
    public async Task A_batch_in_perspective_keeps_it_on_with_fresh_corners_and_the_usual_outcomes()
    {
        await WithOverlayAsync("en", false, ["one.jpg", "tall.jpg", "five.jpg", "spinner.gif", "three.jpg", "four.jpg"], async (window, vm, _) =>
        {
            ClickPerspective(window);
            await vm.CropAndSaveAsync();
            Pump();
            Assert.True(window.CropCanvas.IsPerspective);
            Assert.Equal(CropQuad.Full, window.CropCanvas.NormalizedCorners);
            Assert.Equal("1000 × 1600 px", vm.DimensionsText);
            for (var i = 0; i < 4; i++)
            {
                var p = window.CropCanvas.CornerScreenPoint(i);
                Assert.Equal(p.X, Avalonia.Controls.Canvas.GetLeft(CornerButtons(window)[i]) + 12, 1);
                Assert.Equal(p.Y, Avalonia.Controls.Canvas.GetTop(CornerButtons(window)[i]) + 12, 1);
            }
            window.CropCanvas.NormalizedCorners = CropQuad.Full.With(0, new Point(0.2, 0.1));
            await vm.CropAndSaveAsync();
            Pump();
            Assert.Equal(2, vm.CurrentIndex);
            Assert.Equal(CropQuad.Full, window.CropCanvas.NormalizedCorners);
            await vm.CropAndSaveAsync();
            Pump();
            Assert.Equal(4, vm.CurrentIndex);
            Assert.Equal(CropItemState.Failed, vm.Queue[3].State);
            var loc = AppHost.Get<ILocalizer>();
            Assert.Contains($"spinner.gif: {loc["error.unsupported_source"]}", vm.Problems);
            await vm.CropAndSaveAsync();
            Assert.Equal(CropItemState.Skipped, vm.Queue[4].State);
            Assert.Contains($"three.jpg: {loc["toast.skipped"]}", vm.Problems);
            await vm.SkipAsync();
            Assert.True(vm.IsFinished);
            Assert.Equal([CropItemState.Done, CropItemState.Done, CropItemState.Done, CropItemState.Failed, CropItemState.Skipped, CropItemState.Skipped],
                vm.Queue.Select(q => q.State));
            Assert.Equal((3, 2, 1), (vm.DoneCount, vm.SkippedCount, vm.FailedCount));
            Assert.Equal(loc.Format("toast.done_errors", 1, 6), vm.Summary);
        }, preset => preset.Output.Conflict = ConflictPolicy.Skip,
        folder => Picture(folder, "three_cropped.jpg"));
    }

    [AvaloniaFact]
    public async Task A_pdf_opens_rectangular_with_perspective_unavailable_and_it_comes_back()
    {
        await WithOverlayAsync("en", false, ["one.jpg", "pages.pdf", "two.jpg"], async (window, vm, _) =>
        {
            ClickPerspective(window);
            await vm.CropAndSaveAsync();
            Pump();
            var button = window.FindControl<ToggleButton>("PerspectiveButton")!;
            var loc = AppHost.Get<ILocalizer>();
            Assert.False(button.IsEnabled);
            Assert.Equal(loc["crop.perspective_pdf"], ToolTip.GetTip(button));
            Assert.False(window.CropCanvas.IsPerspective);
            var folder = (string)window.Tag!;
            await vm.CropAndSaveAsync();
            Pump();
            using var pdf = PdfReader.Open(Path.Combine(folder, "pages_cropped.pdf"), PdfDocumentOpenMode.Import);
            Assert.Equal(2, pdf.PageCount);
            Assert.True(window.CropCanvas.IsPerspective);
            Assert.True(button.IsEnabled);
            Assert.Equal(loc["crop.perspective_tip"], ToolTip.GetTip(button));
            Assert.True(CornerButtons(window)[window.CropCanvas.SelectedCorner].IsFocused);
        });
    }

    [AvaloniaFact]
    public async Task A_press_on_the_picture_away_from_the_corners_doesnt_move_the_window()
    {
        await WithOverlayAsync("en", false, ["harbour.jpg"], (window, vm, _) =>
        {
            ClickPerspective(window);
            var canvas = window.CropCanvas;
            var handled = new List<bool>();
            window.AddHandler(InputElement.PointerPressedEvent, (_, e) => handled.Add(e.Handled), RoutingStrategies.Bubble, handledEventsToo: true);
            var centre = canvas.TranslatePoint(canvas.CalculateImageBounds().Center, window)!.Value;
            window.MouseDown(centre, MouseButton.Left);
            window.MouseUp(centre, MouseButton.Left);
            Pump();
            Assert.Equal([true], handled);
            return Task.CompletedTask;
        });
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    [InlineData("ar")]
    public async Task Perspective_controls_are_named_and_placed_in_the_language(string language)
    {
        await WithOverlayAsync(language, false, ["harbour.jpg"], (window, vm, _) =>
        {
            ClickPerspective(window);
            var loc = AppHost.Get<ILocalizer>();
            Assert.Equal(loc["crop.perspective"], AutomationProperties.GetName(window.FindControl<ToggleButton>("PerspectiveButton")!));
            string[] keys = ["crop.corner_top_left", "crop.corner_top_right", "crop.corner_bottom_right", "crop.corner_bottom_left"];
            var canvas = window.CropCanvas;
            canvas.NormalizedCorners = CropQuad.Full.With(0, new Point(0.2, 0.1));
            Pump();
            for (var i = 0; i < 4; i++)
            {
                var button = CornerButtons(window)[i];
                Assert.Equal(loc[keys[i]], AutomationProperties.GetName(button));
                var actual = button.TranslatePoint(new Point(12, 12), window)!.Value;
                var expected = canvas.TranslatePoint(canvas.CornerScreenPoint(i), window)!.Value;
                Assert.InRange(Math.Abs(actual.X - expected.X), 0, 1);
                Assert.InRange(Math.Abs(actual.Y - expected.Y), 0, 1);
            }
            Assert.True(window.FindControl<TextBlock>("PerspectiveHint")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<TextBlock>("RectangularHint")!.IsEffectivelyVisible);
            return Task.CompletedTask;
        });
    }

    [AvaloniaTheory]
    [InlineData("en", false)]
    [InlineData("en", true)]
    [InlineData("ko", false)]
    [InlineData("ko", true)]
    public async Task Perspective_mode_looks(string language, bool dark)
    {
        await WithOverlayAsync(language, dark, ["tilted.png"], (window, vm, suffix) =>
        {
            ClickPerspective(window);
            var q = new CropQuad(new Point(120.0 / 800, 80.0 / 600), new Point(690.0 / 800, 140.0 / 600),
                new Point(650.0 / 800, 520.0 / 600), new Point(90.0 / 800, 470.0 / 600));
            window.CropCanvas.NormalizedCorners = q;
            Settle(window);
            // Q's longer top and left edges, rounded: hypot(570, 60) and hypot(30, 390) on the 800 × 600 picture.
            Assert.Equal("573 × 391 px", vm.DimensionsText);
            Assert.True(window.FindControl<Avalonia.Controls.Canvas>("CornerLayer")!.IsEffectivelyVisible);
            Save(window, $"crop-perspective-{suffix}.png");
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task Turning_perspective_on_disables_the_dial_at_once_even_while_it_closes()
    {
        await WithOverlayAsync("en", false, ["harbour.jpg"], (window, vm, _) =>
        {
            vm.SetAspect("4:3");
            vm.ToggleAspectDial();
            Pump();
            Assert.True(window.AspectDial.IsEffectivelyEnabled);
            vm.IsPerspective = true;
            Assert.False(window.AspectDial.IsEffectivelyEnabled);
            Assert.All(window.AspectDial.Segments, s => Assert.False(s.IsEffectivelyEnabled));
            var hubs = window.AspectDial.Children.OfType<Button>().Where(b => b.Classes.Contains("hub")).ToArray();
            Assert.Single(hubs);
            Assert.All(hubs, hub => Assert.False(hub.IsEffectivelyEnabled));
            Pump();
            Press(window, Key.Tab);
            Assert.False(window.AspectDial.IsKeyboardFocusWithin);
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task The_whole_toolbar_shows_in_the_smallest_window_with_a_custom_ratio_and_several_files()
    {
        await WithOverlayAsync("en", false, ["harbour.jpg", "two.jpg"], (window, vm, _) =>
        {
            vm.SetAspect("custom");
            (window.Width, window.Height) = (window.MinWidth, window.MinHeight);
            Settle(window);
            Assert.Equal(window.MinWidth, window.Bounds.Width);
            var toolbar = (StackPanel)window.FindControl<ToggleButton>("PerspectiveButton")!.Parent!;
            var pill = (Border)toolbar.Parent!;
            var loc = AppHost.Get<ILocalizer>();
            Control[] controls =
            [
                window.FindControl<Button>("DialButton")!, window.FindControl<ToggleButton>("PerspectiveButton")!,
                window.FindControl<NumericUpDown>("CustomWidthBox")!, window.FindControl<NumericUpDown>("CustomHeightBox")!,
                ..toolbar.Children.OfType<Button>().Where(b => AutomationProperties.GetName(b) is var name
                    && (name == loc["crop.reset"] || name == loc["crop.skip"])),
                window.FindControl<Button>("PrimaryButton")!,
            ];
            Assert.Equal(7, controls.Length);
            var shell = new Rect(pill.TranslatePoint(default, window)!.Value, pill.Bounds.Size);
            foreach (var control in controls)
            {
                Assert.True(control.IsEffectivelyVisible, control.Name);
                var bounds = new Rect(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
                Assert.True(shell.Contains(bounds), $"{control.Name ?? AutomationProperties.GetName(control)} {bounds} outside {shell}");
            }
            return Task.CompletedTask;
        });
    }

    private static void AssertForbiddenKind(string kind, CropQuad q)
    {
        static double Cross(Point a, Point b, Point c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        static bool Intersects(Point a, Point b, Point c, Point d) =>
            Cross(a, b, c) * Cross(a, b, d) < 0 && Cross(c, d, a) * Cross(c, d, b) < 0;
        var turns = Enumerable.Range(0, 4).Select(i => Cross(q[i], q[(i + 1) % 4], q[(i + 2) % 4])).ToArray();
        switch (kind)
        {
            case "twisted":
                Assert.True(Intersects(q[0], q[1], q[2], q[3]) || Intersects(q[1], q[2], q[3], q[0]));
                break;
            case "concave":
                Assert.Single(turns, c => c < 0);
                break;
            case "line":
                Assert.Contains(0.0, turns);
                break;
            case "outside":
                Assert.True(q[0].X < 0 || q[0].X > 1600 || q[0].Y < 0 || q[0].Y > 1000);
                break;
        }
    }

    private static Button[] CornerButtons(CropOverlayWindow window) =>
        new[] { "CornerTopLeft", "CornerTopRight", "CornerBottomRight", "CornerBottomLeft" }
            .Select(name => window.FindControl<Button>(name)!).ToArray();

    private static void ClickPerspective(CropOverlayWindow window)
    {
        Pump();
        var button = window.FindControl<ToggleButton>("PerspectiveButton")!;
        var p = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        window.MouseMove(new Point(30, window.Bounds.Height / 2));
        Pump();
    }

    private static void Drag(CropOverlayWindow window, Point start, Point target)
    {
        Pump();
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(target);
        window.MouseUp(target, MouseButton.Left);
        Pump();
    }

    private static void AssertClose(Rect expected, Rect actual)
    {
        Assert.Equal(expected.X, actual.X, 3);
        Assert.Equal(expected.Y, actual.Y, 3);
        Assert.Equal(expected.Width, actual.Width, 3);
        Assert.Equal(expected.Height, actual.Height, 3);
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static async Task UntilAsync(CropOverlayViewModel vm, Func<bool> condition)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, PropertyChangedEventArgs e)
        {
            if (condition())
                ready.TrySetResult();
        }
        vm.PropertyChanged += Changed;
        try
        {
            Changed(vm, new PropertyChangedEventArgs(null));
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            vm.PropertyChanged -= Changed;
        }
    }

    private static async Task WithOverlayAsync(string language, bool dark, string[] names,
        Func<CropOverlayWindow, CropOverlayViewModel, string, Task> act,
        Action<Preset>? configure = null, Action<string>? prepare = null)
    {
        TestServices.EnsureInitialized(language);
        var theme = AppHost.Get<ThemeService>();
        if (dark)
            theme.Apply(new ThemeSettings { Mode = ThemeMode.Dark }, language);
        var folder = Directory.CreateTempSubdirectory("filee-crop-perspective-").FullName;
        CropOverlayWindow? window = null;
        try
        {
            var files = names.Select(name =>
            {
                if (name == "tilted.png")
                    return Tilted(folder, name);
                if (!name.EndsWith(".pdf", StringComparison.Ordinal))
                    return Picture(folder, name);
                var path = Path.Combine(folder, name);
                using var document = new PdfDocument();
                foreach (var rotation in new[] { 0, 90 })
                {
                    var page = document.AddPage();
                    page.Width = PdfSharp.Drawing.XUnit.FromPoint(600);
                    page.Height = PdfSharp.Drawing.XUnit.FromPoint(800);
                    page.Rotate = rotation;
                }
                document.Save(path);
                return path;
            }).ToList();
            prepare?.Invoke(folder);
            var preset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-crop").Clone();
            configure?.Invoke(preset);
            var vm = new CropOverlayViewModel(files, preset, AppHost.Get<ILocalizer>(), AppHost.Get<UserDataStore>());
            window = new CropOverlayWindow { DataContext = vm, Width = 1040, Height = 720, Tag = folder };
            window.Show();
            await UntilAsync(vm, () => vm.PreviewImage is not null && !vm.IsBusy);
            Pump();
            window.CropCanvas.FinishAnimations();
            await act(window, vm, $"{language}-{(dark ? "dark" : "light")}");
        }
        finally
        {
            if (window?.IsVisible == true)
                window.Close();
            if (dark)
                theme.Apply(new ThemeSettings(), language);
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    private static string Tilted(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        using var image = new MagickImage(MagickColors.Red, 400, 300);
        using (var green = new MagickImage(MagickColors.Lime, 200, 150))
            image.Composite(green, 200, 0, CompositeOperator.Over);
        using (var blue = new MagickImage(MagickColors.Blue, 200, 150))
            image.Composite(blue, 200, 150, CompositeOperator.Over);
        using (var yellow = new MagickImage(MagickColors.Yellow, 200, 150))
            image.Composite(yellow, 0, 150, CompositeOperator.Over);
        image.BackgroundColor = MagickColors.Gray;
        image.VirtualPixelMethod = VirtualPixelMethod.Background;
        image.Distort(new DistortSettings(DistortMethod.Perspective) { Viewport = new MagickGeometry(0, 0, 800, 600) },
            [0, 0, 120, 80, 400, 0, 690, 140, 400, 300, 650, 520, 0, 300, 90, 470]);
        image.ResetPage();
        image.Write(path, MagickFormat.Png);
        return path;
    }

    private static void WithOverlay(string language, bool dark, string[] names, Action<CropOverlayWindow, CropOverlayViewModel, string> act)
    {
        TestServices.EnsureInitialized(language);
        var theme = AppHost.Get<ThemeService>();
        if (dark)
            theme.Apply(new ThemeSettings { Mode = ThemeMode.Dark }, language);
        var folder = Directory.CreateTempSubdirectory("filee-crop-overlay-").FullName;
        CropOverlayWindow? window = null;
        try
        {
            var files = names.Select(name => Picture(folder, name)).ToList();
            var preset = BuiltInData.CreatePresets().Single(p => p.Id == "tool-crop");
            var vm = new CropOverlayViewModel(files, preset, AppHost.Get<ILocalizer>(), AppHost.Get<UserDataStore>());
            window = new CropOverlayWindow { DataContext = vm, Width = 1040, Height = 720 };
            window.Show();
            WaitFor(() => vm.PreviewImage is not null && !vm.IsBusy);
            Pump();
            window.CropCanvas.FinishAnimations();
            act(window, vm, $"{language}-{(dark ? "dark" : "light")}");
        }
        finally
        {
            if (window?.IsVisible == true)
                window.Close();
            if (dark)
                theme.Apply(new ThemeSettings(), language);
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    /// <summary>A landscape (1600 × 1000), an upright picture ("tall", 1000 × 1600) or a two-frame GIF ("…gif").</summary>
    private static string Picture(string folder, string name)
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
        var tall = name.StartsWith("tall", StringComparison.Ordinal);
        var hue = name.Sum(c => c) % 5;
        string[] skies = ["#8EC5FC-#F8D2A6", "#A1C4FD-#C2E9FB", "#FBC2EB-#A6C1EE", "#84FAB0-#8FD3F4", "#FFECD2-#FCB69F"];
        using var image = new MagickImage($"gradient:{skies[hue]}", tall ? 1000u : 1600u, tall ? 1600u : 1000u);
        var w = (double)image.Width;
        var h = (double)image.Height;
        new Drawables()
            .FillColor(new MagickColor("#FFF2C2")).Circle(w * 0.68, h * 0.36, w * 0.68, h * 0.36 + Math.Min(w, h) * 0.1)
            .FillColor(new MagickColor("#4F7CAC")).Rectangle(0, h * 0.64, w, h)
            .FillColor(new MagickColor("#6C9A5B")).Polygon(new PointD(0, h * 0.7), new PointD(w * 0.24, h * 0.47), new PointD(w * 0.48, h * 0.69), new PointD(0, h * 0.76))
            .FillColor(new MagickColor("#3F6E4A")).Polygon(new PointD(w * 0.38, h * 0.7), new PointD(w * 0.62, h * 0.52), new PointD(w, h * 0.7), new PointD(w, h * 0.76), new PointD(w * 0.38, h * 0.76))
            .Draw(image);
        image.Write(path, MagickFormat.Jpeg);
        return path;
    }

    private static void Settle(CropOverlayWindow window)
    {
        Pump();
        window.SettleForCapture();
        Pump();
    }

    private static void Press(CropOverlayWindow window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
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
