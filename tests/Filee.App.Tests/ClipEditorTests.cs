// The clip window's editor with a painted stand-in for FFmpeg: opening it from the quick cut and going back, files
// added (videos on the timeline, a sound as the music), its keys, playing, the properties, the Export window's
// conversion, "Join videos", and Ctrl + wheel zoom.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Presets;

namespace Filee.App.Tests;

public class ClipEditorTests
{
    private static void Pump() => RenderTests.Pump();

    [AvaloniaFact]
    public void The_editor_starts_from_the_quick_cut_and_goes_back_to_it()
    {
        ClipWindowTests.WithClipWindow("en", false, ["match.mp4"], (window, vm, _, _) =>
        {
            vm.MoveHandle(ClipHandle.Start, 10);
            vm.SetLength(20);
            vm.MovePlayhead(15);
            vm.Split();
            Assert.Equal(2, vm.Pieces.Count);

            vm.OpenEditor();
            Pump();
            var editor = Assert.IsType<ClipEditorViewModel>(vm.Editor);
            Assert.True(vm.IsEditorMode);
            Assert.False(vm.IsQuickMode);
            Assert.Equal(20, editor.Length, 6);
            Assert.Equal(2, editor.Edit.Parts.Count);

            // Still a quick cut: back at once, with its pieces.
            editor.Split();
            editor.Back();
            Assert.Null(vm.Editor);
            Assert.Equal(20, vm.Length, 6);
            Assert.True(vm.Pieces.Count >= 2);
        });
    }

    [AvaloniaFact]
    public void Going_back_asks_when_the_editor_has_more_than_a_quick_cut()
    {
        ClipWindowTests.WithClipWindow("en", false, ["match.mp4"], (window, vm, _, _) =>
        {
            vm.OpenEditor();
            var editor = vm.Editor!;
            editor.SelectPart(0, ClipSelect.Only);
            editor.PartReverse = true;
            editor.Back();
            Assert.True(editor.IsBackConfirmOpen);
            Assert.Same(editor, vm.Editor);
            editor.StayInEditorCommand.Execute(null);
            Assert.False(editor.IsBackConfirmOpen);
            editor.Back();
            editor.ConfirmBackCommand.Execute(null);
            Assert.Null(vm.Editor);
            Assert.True(vm.IsQuickMode);
        });
    }

    [AvaloniaFact]
    public void Files_added_go_on_the_timeline_and_under_it()
    {
        ClipWindowTests.WithClipWindow("en", false, ["match.mp4", "phone tall.mp4", "song.mp3", "notes.txt"], (window, vm, frames, _) =>
        {
            vm.OpenEditor();
            var editor = vm.Editor!;
            var before = editor.Length;
            IReadOnlyList<EditorMedia>? flown = null;
            editor.FilesAdded += media => flown = media;

            var added = editor.AddFilesAsync(ClipWindowTests.OtherFiles(vm), toTimeline: true).GetAwaiter().GetResult();

            Assert.Equal(2, added.Count); // the text file isn't a video or a sound
            Assert.Equal(3, editor.Files.Count);
            Assert.Same(added, flown);
            // Marked new while their cards fly in; with less motion (an OS setting) the mark goes at once.
            Assert.True(added.All(m => m.JustAdded == Motion.Enabled));
            Assert.Equal(2, editor.Edit.Parts.Count);
            Assert.EndsWith("phone tall.mp4", editor.Edit.Parts[1].Source);
            Assert.Equal(before + ClipWindowTests.Length / 2, editor.Length, 6);
            Assert.True(editor.HasSounds);
            Assert.EndsWith("song.mp3", Assert.Single(editor.Edit.Sounds).Path);
            Assert.Contains("1", editor.Message); // "Not a video or a sound: 1"
            Assert.True(editor.Files.All(f => f.IsUsed));
            // One undo takes them all off again.
            editor.Undo();
            Assert.Single(editor.Edit.Parts);
            Assert.False(editor.HasSounds);
        });
    }

    /// <summary>A real picture file next to the window's video.</summary>
    private static string MakePicture(ClipWindowViewModel vm, string name, int width = 200, int height = 100)
    {
        var path = Path.Combine(Path.GetDirectoryName(vm.FilePaths[0])!, name);
        using var image = new ImageMagick.MagickImage(ImageMagick.MagickColors.OrangeRed, (uint)width, (uint)height);
        image.Write(path, ImageMagick.MagickFormat.Png);
        return path;
    }

    [AvaloniaFact]
    public void Sounds_go_in_a_row_on_tracks_and_are_moved_trimmed_and_removed()
    {
        ClipWindowTests.WithClipWindow("en", false, ["match.mp4", "one.mp3", "two.mp3", "three.mp3"], (window, vm, _, _) =>
        {
            vm.SetLength(120);
            vm.OpenEditor();
            var editor = vm.Editor!;
            var files = ClipWindowTests.OtherFiles(vm);
            editor.AddFilesAsync(files.Take(2).ToList(), toTimeline: true).GetAwaiter().GetResult();
            Assert.Equal(2, editor.Edit.Sounds.Count);
            Assert.Equal((0.0, 40.0), (editor.Edit.Sounds[0].Offset, editor.Edit.Sounds[1].Offset)); // one after the other on A2
            Assert.Equal(2, editor.SoundTracks); // A2 and an empty A3 to drag to

            // The third onto a track of its own, from its card's menu.
            editor.AddFilesAsync([files[2]], toTimeline: false).GetAwaiter().GetResult();
            var card = editor.Files.Single(f => f.Name == "three.mp3");
            editor.Seek(5);
            editor.MenuForMedia(card).Single(m => m.Header == "On a new sound track").Run!();
            var third = editor.Edit.Sounds[2];
            Assert.Equal((1, 5.0), (third.Track, third.Offset));
            Assert.True(editor.HasSoundSelection);
            Assert.Equal("A3", editor.SoundTrackText);

            // Dragged on the timeline: along and down, then its start trimmed; one undo step per drag.
            editor.MoveSound(0, 10, 2);
            editor.MoveSound(0, 12, 2);
            editor.EndDrag();
            Assert.Equal((12.0, 2), (editor.Edit.Sounds[0].Offset, editor.Edit.Sounds[0].Track));
            editor.TrimSound(0, 14, 2, 20);
            editor.EndDrag();
            Assert.Equal((14.0, 2.0, 20.0), (editor.Edit.Sounds[0].Offset, editor.Edit.Sounds[0].From, editor.Edit.Sounds[0].Play));
            editor.Undo();
            editor.Undo();
            Assert.Equal((0.0, 0), (editor.Edit.Sounds[0].Offset, editor.Edit.Sounds[0].Track));

            // Removed from the project: its sound goes with its card.
            editor.RemoveMedia(editor.Files.Single(f => f.Name == "one.mp3"));
            Assert.Equal(2, editor.Edit.Sounds.Count);
            Assert.DoesNotContain(editor.Files, f => f.Name == "one.mp3");
            editor.Select(new EditorItem(EditorItemKind.Sound, 0));
            editor.Delete(closeUp: false);
            Assert.Single(editor.Edit.Sounds);
        });
    }

    [AvaloniaFact]
    public void Pictures_texts_and_the_part_are_moved_sized_and_turned()
    {
        ClipWindowTests.WithClipWindow("en", false, ["match.mp4"], (window, vm, _, _) =>
        {
            vm.OpenEditor();
            var editor = vm.Editor!;
            var logo = MakePicture(vm, "logo.png");
            editor.Seek(3);
            editor.AddFilesAsync([logo], toTimeline: true).GetAwaiter().GetResult();
            var image = Assert.Single(editor.Edit.Images);
            Assert.Equal((3.0, ClipEditorViewModel.PictureSeconds), (image.Start, image.Length));
            Assert.NotNull(editor.ImageOf(logo));
            Assert.True(editor.Files.Single(f => f.IsPicture).IsUsed);
            Assert.Single(editor.VisibleImages);

            // The picture's frame in the preview: moved, sized and turned as one drag.
            editor.Select(new EditorItem(EditorItemKind.Image, 0));
            Assert.Equal(new EditorItem(EditorItemKind.Image, 0), editor.Framed);
            editor.Transform(new EditorItem(EditorItemKind.Image, 0), 0.8, 0.2, 0.5, 30);
            editor.Transform(new EditorItem(EditorItemKind.Image, 0), 0.85, 0.2, 0.5, 45);
            editor.EndDrag();
            image = editor.Edit.Images[0];
            Assert.Equal((0.85, 0.2, 0.5, 45.0), (image.X, image.Y, image.Width, image.Rotation));
            editor.ImageOpacity = 0.5;
            editor.TurnRight();
            Assert.Equal((0.5, 135.0), (editor.Edit.Images[0].Opacity, editor.Edit.Images[0].Rotation));
            editor.Undo();
            editor.Undo();
            editor.Undo();
            Assert.Equal(0, editor.Edit.Images[0].Rotation);

            // A text: font, see-through and turn.
            editor.AddText("Title");
            editor.TextFont = editor.Fonts[0];
            editor.TextOpacity = 0.4;
            editor.TextRotation = -15;
            var text = editor.Edit.Texts[0];
            Assert.Equal((null, 0.4, -15.0), (text.Font, text.Opacity, text.Rotation));

            // The part shown: framed when it alone is selected; smaller and turned.
            editor.SelectPart(0, ClipSelect.Only);
            Assert.Equal(new EditorItem(EditorItemKind.Part, 0), editor.Framed);
            editor.Transform(new EditorItem(EditorItemKind.Part, 0), 0.3, 0.5, 0.6, 90);
            var part = editor.Edit.Parts[0];
            Assert.Equal((0.3, 0.6, 90.0), (part.FocusX, part.Zoom, part.Rotation));
            Assert.True(editor.PartRotation == 90);

            // Its menu: the turn and the reset are there and work.
            var menu = editor.MenuFor(new EditorItem(EditorItemKind.Part, 0), 4);
            menu.Single(m => m.Header == "Reset").Run!();
            part = editor.Edit.Parts[0];
            Assert.Equal((0.5, 1.0, 0.0), (part.FocusX, part.Zoom, part.Rotation));

            // The picture's card leaves the project with its picture.
            editor.RemoveMedia(editor.Files.Single(f => f.IsPicture));
            Assert.Empty(editor.Edit.Images);
        });
    }

    [AvaloniaFact]
    public void Export_draws_the_texts_and_pictures_for_the_conversion()
    {
        ClipWindowTests.WithClipWindow("en", false, ["match.mp4"], (window, vm, _, exports) =>
        {
            vm.OpenEditor();
            var editor = vm.Editor!;
            editor.AddFilesAsync([MakePicture(vm, "logo.png")], toTimeline: true).GetAwaiter().GetResult();
            editor.AddText("Caption");
            editor.TextRotation = 20;
            editor.SetAspect("9:16");
            editor.ExportResolution = editor.ExportResolutions.First(r => r.Value == 720);
            editor.Export();
            var edit = Assert.Single(exports).Preset.Media.Clip!;
            var text = Assert.Single(edit.Texts);
            var image = Assert.Single(edit.Images);
            Assert.True(File.Exists(text.Rendered));
            Assert.True(File.Exists(image.Rendered));
            using var drawn = new ImageMagick.MagickImage(text.Rendered!);
            Assert.Equal((720u, 1280u), (drawn.Width, drawn.Height)); // the written frame
            Assert.True(drawn.HasAlpha);
        });
    }

    [AvaloniaFact]
    public void The_editors_keys_cut_copy_add_text_and_undo()
    {
        ClipWindowTests.WithClipWindow("en", false, ["match.mp4"], (window, vm, _, _) =>
        {
            vm.OpenEditor();
            var editor = vm.Editor!;
            Pump();
            editor.Seek(10);
            ClipWindowTests.Press(window, Key.C);
            Assert.Equal(2, editor.Edit.Parts.Count);
            editor.SelectPart(0, ClipSelect.Only);
            ClipWindowTests.Press(window, Key.D, RawInputModifiers.Control);
            Assert.Equal(3, editor.Edit.Parts.Count);
            Assert.Equal(40, editor.Length, 6);
            ClipWindowTests.Press(window, Key.Delete);
            Assert.True(editor.HasGaps);
            ClipWindowTests.Press(window, Key.Delete, RawInputModifiers.Shift); // the gap selected closes
            Assert.False(editor.HasGaps);
            Assert.Equal(30, editor.Length, 6);
            ClipWindowTests.Press(window, Key.T);
            Assert.Single(editor.Edit.Texts);
            Assert.True(editor.HasTextSelection);
            ClipWindowTests.Press(window, Key.Escape); // leaves the text box
            ClipWindowTests.Press(window, Key.Escape);
            Assert.Equal(EditorSelection.None, editor.Selection);
            ClipWindowTests.Press(window, Key.Z, RawInputModifiers.Control);
            Assert.Empty(editor.Edit.Texts);
            ClipWindowTests.Press(window, Key.L);
            Assert.Equal(15, editor.Playhead, 6);
            ClipWindowTests.Press(window, Key.J);
            Assert.Equal(10, editor.Playhead, 6);
            ClipWindowTests.Press(window, Key.OemPeriod);
            Assert.Equal(10 + 1 / 30.0, editor.Playhead, 6);
            ClipWindowTests.Press(window, Key.F);
            Assert.True(editor.IsFullPreview);
            ClipWindowTests.Press(window, Key.Escape);
            Assert.False(editor.IsFullPreview);
        });
    }

    [AvaloniaFact]
    public void Playing_plays_the_editors_clip_from_the_playhead()
    {
        ClipWindowTests.WithClipWindow("en", false, ["match.mp4"], (window, vm, frames, _) =>
        {
            vm.OpenEditor();
            var editor = vm.Editor!;
            editor.Seek(5);
            ClipWindowTests.Press(window, Key.Space);
            ClipWindowTests.WaitFor(() => !editor.IsPlaying);
            var played = Assert.Single(frames.PlayedEditor);
            Assert.True(played.Editor);
            Assert.Equal(25, played.Length, 6);
            Assert.True(editor.Playhead > 5);
        });
    }

    [AvaloniaFact]
    public void The_properties_change_the_selection_and_the_clip()
    {
        ClipWindowTests.WithClipWindow("en", false, ["match.mp4"], (window, vm, _, _) =>
        {
            vm.OpenEditor();
            var editor = vm.Editor!;
            editor.Seek(10);
            editor.Split();
            editor.SelectPart(0, ClipSelect.Only);
            Assert.True(editor.HasPartSelection);
            Assert.True(editor.HasTransitionSection);
            editor.PartSpeed = editor.Speeds.First(s => s.Value == 2);
            editor.PartSpeedEnd = editor.SpeedEnds.First(s => s.Value == 0.5);
            editor.TransitionKind = editor.Transitions.First(t => t.Value == ClipTransitionKind.Crossfade);
            editor.PartVolume = 1.5;
            var part = editor.Edit.Parts[0];
            Assert.True(part.IsRamp);
            Assert.Equal((2.0, 0.5, 1.5), (part.PlaySpeed, part.PlaySpeedEnd, part.Volume));
            Assert.Equal(ClipTransitionKind.Crossfade, part.Transition);

            editor.ClearSelection();
            Assert.True(editor.HasNoSelection);
            editor.SetAspect("9:16");
            editor.LevelLoudness = true;
            editor.DuckMusic = true;
            Assert.Equal(("9:16", true, true), (editor.Edit.Aspect, editor.Edit.LevelLoudness, editor.Edit.DuckMusic));
            Assert.Equal((1080, 1920), editor.Canvas);
            Assert.True(editor.HasOtherShapes);
            Assert.True(editor.Is9x16);
        });
    }

    [AvaloniaFact]
    public void Export_starts_the_conversion_with_the_choices_and_moves_on()
    {
        ClipWindowTests.WithClipWindow("en", false, ["match.mp4"], (window, vm, frames, exports) =>
        {
            var closed = false;
            vm.CloseRequested += () => closed = true;
            vm.OpenEditor();
            var editor = vm.Editor!;
            editor.Seek(10);
            editor.Split();
            ClipWindowTests.WaitFor(() => editor.HasGpu);
            Assert.Contains("NVIDIA", editor.GpuText);
            editor.OpenExport();
            editor.ExportPreset = editor.ExportPresets.First(p => p.Value == "shorts");
            Assert.Equal("9:16", editor.Edit.Aspect); // Shorts are upright
            editor.ExportFormat = editor.ExportFormats.First(f => f.Value == "mp4-hevc");
            editor.ExportLimit = editor.ExportLimits.First(l => l.Value == 25);
            editor.ExportSeparate = true;
            Assert.Contains("1080×1920", editor.ExportSummary);

            ClipWindowTests.Press(window, Key.Enter);

            var (path, conversion) = Assert.Single(exports);
            Assert.EndsWith("match.mp4", path);
            Assert.Equal("mp4", conversion.TargetFormat);
            Assert.Equal(VideoCodec.Hevc, conversion.Media.VideoCodec);
            Assert.True(conversion.Media.HardwareEncoding);
            var edit = conversion.Media.Clip!;
            Assert.True(edit.Editor);
            Assert.Equal((1080, 25.0, true), (edit.Resolution, edit.MaxMegabytes, edit.Separate));
            Assert.Equal(2, edit.Parts.Count);
            // The last video: the window closes.
            Assert.True(closed);
        });
    }

    [AvaloniaFact]
    public void Joining_videos_opens_the_editor_with_all_of_them()
    {
        TestServices.EnsureInitialized("en");
        var folder = Directory.CreateTempSubdirectory("filee-join-").FullName;
        try
        {
            var files = new[] { "one.mp4", "two.mp4", "three tall.mp4" }.Select(n => Path.Combine(folder, n)).ToList();
            foreach (var file in files)
                File.WriteAllBytes(file, [0]);
            var frames = new ClipWindowTests.PaintedFrames(ClipWindowTests.Length);
            var tool = BuiltInData.CreatePresets().Single(p => p.Id == "tool-clip");
            var vm = new ClipWindowViewModel([files[0]], tool, AppHost.Get<ILocalizer>(), AppHost.Get<Filee.Core.Settings.UserDataStore>(), frames,
                _ => "mp4", (_, _) => { }, joining: files.Skip(1).ToList());
            ClipWindowTests.WaitFor(() => vm.Editor is { Edit.Parts.Count: 3 });
            var editor = vm.Editor!;
            Assert.Equal(ClipWindowTests.Length * 2.5, editor.Length, 6); // whole, whole, and the upright one of half the length
            Assert.Null(editor.Edit.Parts[0].Source);
            vm.Dispose();

            // The order window: sorted the way people count, different shapes asked about.
            var join = new JoinVideosViewModel(files, AppHost.Get<ILocalizer>(), frames);
            ClipWindowTests.WaitFor(() => join.IsReady);
            Assert.Equal(["one.mp4", "three tall.mp4", "two.mp4"], join.Files.Select(f => f.Name));
            Assert.True(join.Differ);
            join.SelectedFile = join.Files[2];
            join.MoveUpCommand.Execute(null);
            join.Fit = join.Fits.First(f => f.Value == ClipFit.Crop);
            join.JoinCommand.Execute(null);
            var choice = join.Result!;
            Assert.False(choice.OpenInEditor);
            Assert.Equal(["one.mp4", "two.mp4", "three tall.mp4"], choice.Files.Select(Path.GetFileName));
            Assert.Equal(ClipFit.Crop, choice.Edit.Fit);
            Assert.Equal([null, files[1], files[2]], choice.Edit.Parts.Select(p => p.Source));
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public void Ctrl_and_the_wheel_or_plus_minus_zoom_every_window()
    {
        TestServices.EnsureInitialized("en");
        var window = new Window { Content = new Border { Width = 100, Height = 100 }, Width = 300, Height = 300 };
        UiZoom.Track(window);
        window.Show();
        try
        {
            UiZoom.Set(1, remember: false);
            var scaled = Assert.IsType<LayoutTransformControl>(window.Content);
            window.KeyPress(Key.OemPlus, RawInputModifiers.Control, PhysicalKey.None, null);
            Assert.Equal(1.1, UiZoom.Scale, 6);
            Assert.Equal(1.1, Assert.IsType<ScaleTransform>(scaled.LayoutTransform).ScaleX, 6);
            window.MouseWheel(new Point(50, 50), new Vector(0, 1), RawInputModifiers.Control);
            Assert.Equal(1.2, UiZoom.Scale, 6);
            window.KeyPress(Key.D0, RawInputModifiers.Control, PhysicalKey.None, null);
            Assert.Equal(1, UiZoom.Scale, 6);
            Assert.Null(scaled.LayoutTransform);
            UiZoom.Set(99, remember: false);
            Assert.Equal(UiZoom.Max, UiZoom.Scale);
        }
        finally
        {
            UiZoom.Set(1, remember: false);
            window.Close();
        }
    }
}
