// The editor's editing without the window (ClipProject), and what its clip means (ClipPart, ClipEdit, ClipCanvas):
// parts from several videos in any order, the blade, gaps, moving and trimming parts, stills, playing backwards, smooth
// speed changes, transitions, texts, the frame, and undo.

using System.Text.Json;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class ClipProjectTests
{
    private const string Second = @"D:\Videos\second.mp4";

    /// <summary>The window's video (100 s) whole, then a second video (40 s) whole.</summary>
    private static ClipProject Project()
    {
        var project = new ClipProject(new ClipEdit(), source => source is null ? 100 : 40);
        project.AddVideo(null);
        project.AddVideo(Second);
        return project;
    }

    private static void Near(double expected, double actual, double tolerance = 1e-6) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"Expected {expected}, got {actual}");

    [Fact]
    public void Videos_play_one_after_the_other()
    {
        var project = Project();
        Assert.Equal(140, project.Length);
        Assert.Equal([0, 100], project.Starts());
        Assert.Equal((null, Second), (project.Parts[0].Source, project.Parts[1].Source));
        Assert.Equal(1, project.IndexAt(100));
        Assert.Equal(0, project.IndexAt(99.9));
        Assert.True(project.Edit.Editor);
        Assert.False(project.ToEdit().IsPlainCut); // the editor's clip is always written by its own graph
    }

    [Fact]
    public void The_blade_and_undo()
    {
        var project = Project();
        Assert.True(project.Split(30));
        Assert.True(project.Split(120)); // 20 s into the second video
        Assert.Equal(4, project.Parts.Count);
        Assert.Equal((0.0, 30.0), (project.Parts[0].Start, project.Parts[0].End));
        Assert.Equal((30.0, 100.0), (project.Parts[1].Start, project.Parts[1].End));
        Assert.Equal((Second, 20.0), (project.Parts[3].Source, project.Parts[3].Start));
        Assert.False(project.Split(30.05)); // next to a cut
        Assert.Equal(140, project.Length);
        Assert.True(project.Undo());
        Assert.Equal(3, project.Parts.Count);
        Assert.True(project.Redo());
        Assert.Equal(4, project.Parts.Count);
    }

    [Fact]
    public void Cutting_out_leaves_a_gap_or_closes_up_and_never_everything()
    {
        var project = Project();
        project.Split(30);
        Assert.True(project.Remove([1], closeUp: false));
        Assert.True(project.Parts[1].Blank);
        Assert.Equal(140, project.Length); // the pause keeps the time
        Assert.True(project.HasGaps);
        Assert.True(project.CloseGap(1));
        Assert.Equal(70, project.Length);
        Assert.True(project.Remove([1], closeUp: true));
        Assert.Equal(30, project.Length);
        Assert.False(project.Remove([0], closeUp: true)); // the last part that plays stays
    }

    [Fact]
    public void Parts_move_and_copies_go_in()
    {
        var project = Project();
        Assert.True(project.Move(1, 0));
        Assert.Equal(Second, project.Parts[0].Source);
        Assert.Equal(1, project.Insert([project.Parts[0]], 1));
        Assert.Equal([Second, Second, null], project.Parts.Select(p => p.Source));
        Assert.Equal(180, project.Length);
    }

    [Fact]
    public void Edges_take_more_or_less_of_the_video_as_far_as_it_goes()
    {
        var project = new ClipProject(new ClipEdit(), _ => 100);
        project.AddVideo(null, start: 20, end: 50);
        project.AddVideo(Second, start: 0, end: 10);
        // The end out by 10 s: 10 s more of the video; the second part moves along.
        Assert.Equal(40, project.TrimEdge(0, end: true, 40));
        Assert.Equal((20.0, 60.0), (project.Parts[0].Start, project.Parts[0].End));
        Assert.Equal(40, project.Starts()[1]);
        // The start 5 s in: 5 s less from the start, the part still starts the clip.
        project.Seal();
        Assert.Equal(35, project.TrimEdge(0, end: false, 35));
        Assert.Equal(25, project.Parts[0].Start);
        // Out past the video's start: as far as it goes.
        project.Seal();
        project.TrimEdge(0, end: false, 1000);
        Assert.Equal(0, project.Parts[0].Start);
        // One drag is one undo step.
        Assert.True(project.Undo());
        Assert.Equal(25, project.Parts[0].Start);
    }

    [Fact]
    public void A_backwards_part_trims_from_the_other_end_of_its_video()
    {
        var project = new ClipProject(new ClipEdit(), _ => 100);
        project.AddVideo(null, start: 20, end: 50);
        project.ChangePart(0, p => p.Reverse = true);
        project.TrimEdge(0, end: true, 40); // plays 10 s longer: further back into the video
        Assert.Equal((10.0, 50.0), (project.Parts[0].Start, project.Parts[0].End));
        Near(50, project.Parts[0].SourceAt(0));
        Near(10, project.Parts[0].SourceAt(40));
    }

    [Fact]
    public void A_still_holds_one_picture_between_two_halves()
    {
        var project = Project();
        var index = project.Freeze(30);
        Assert.Equal(1, index);
        var still = project.Parts[1];
        Assert.True(still.IsStill);
        Assert.Equal((30.0, ClipProject.FreezeSeconds), (still.Start, still.Length));
        Assert.Equal(142, project.Length);
        Assert.Equal((30.0, 30.0), (project.Parts[2].Start, still.SourceAt(1)));
        // Its edge sets how long it lasts.
        project.TrimEdge(1, end: true, 5);
        Assert.Equal(5, project.Parts[1].Length);
        // The blade cuts it in two stills.
        Assert.True(project.Split(project.Starts()[1] + 1));
        Assert.Equal([1.0, 4.0], project.Parts.Skip(1).Take(2).Select(p => p.Length));
    }

    [Fact]
    public void A_smooth_speed_change_plays_as_long_as_its_steps()
    {
        var part = new ClipPart { Start = 0, End = 10, Speed = 1, SpeedEnd = 0.25 };
        Assert.True(part.IsRamp);
        // 10 s of video slowing evenly from 1 to 0.25: 10 / 0.75 × ln 4 seconds.
        Near(10 / 0.75 * Math.Log(4), part.Length, 1e-9);
        var steps = part.Steps();
        Assert.Equal(ClipPart.RampSteps, steps.Count);
        Near(part.Length, steps.Sum(s => (s.To - s.From) / s.Speed), 1e-9);
        Assert.True(steps[0].Speed > steps[^1].Speed);
        // Cut in two, each half keeps its share of the change.
        var (before, after) = part.SplitAt(part.Length / 2)!.Value;
        Near(part.Length, before.Length + after.Length, 1e-6);
        Near(before.PlaySpeedEnd, after.PlaySpeed);
        Near(before.End, after.Start);
        Assert.False(new ClipPart { Start = 0, End = 10, Speed = 2, SpeedEnd = 2 }.IsRamp);
    }

    [Fact]
    public void Transitions_overlap_the_parts_they_join()
    {
        var project = Project();
        project.ChangePart(0, p =>
        {
            p.Transition = ClipTransitionKind.Crossfade;
            p.TransitionSeconds = 1;
        });
        Assert.Equal(139, project.Length);
        Assert.Equal([0, 99], project.Starts());
        Assert.Equal(1, project.IndexAt(99.5)); // the part coming in
        // The sound alone doesn't overlap; the last part has no transition.
        project.ChangePart(0, p => p.Transition = ClipTransitionKind.Sound);
        Assert.Equal(140, project.Length);
        project.ChangePart(1, p => p.Transition = ClipTransitionKind.Black);
        Assert.Equal(ClipTransitionKind.Cut, project.Parts[1].Transition);
        // At most half of a short part.
        var shortOne = new ClipEdit
        {
            Editor = true,
            Parts = [new() { End = 1, Transition = ClipTransitionKind.Black, TransitionSeconds = 2 }, new() { End = 10 }],
        };
        Assert.Equal(0.5, shortOne.Overlap(0));
    }

    [Fact]
    public void Playing_from_the_middle_starts_inside_the_part_and_its_texts()
    {
        var project = Project();
        project.AddText(ClipText.Of(ClipTextStyle.Title, "Hi", 95, 10));
        var rest = project.Edit.From(98);
        Assert.Equal(2, rest.Parts.Count);
        Assert.Equal(98, rest.Parts[0].Start);
        Assert.Equal(42, rest.Length);
        var text = Assert.Single(rest.Texts);
        Assert.Equal((0.0, 7.0), (text.Start, text.Length));
        Assert.Equal(Second, project.Edit.From(120).Parts[0].Source);
    }

    [Fact]
    public void Texts_and_settings_change_and_undo()
    {
        var project = Project();
        var index = project.AddText(ClipText.Of(ClipTextStyle.Caption, "Goal!", 10));
        project.ChangeText(index, t => t.Y = 0.5, join: "move");
        project.ChangeText(index, t => t.Y = 0.6, join: "move");
        Assert.Equal(0.6, project.Texts[index].Y);
        project.Undo(); // one step for the whole drag
        Assert.Equal(0.86, project.Texts[index].Y);
        project.ChangeClip(c =>
        {
            c.Aspect = "9:16";
            c.Fit = ClipFit.Blur;
            c.LevelLoudness = true;
        });
        Assert.Equal(("9:16", ClipFit.Blur, true), (project.Edit.Aspect, project.Edit.Fit, project.Edit.LevelLoudness));
        Assert.Single(project.Texts); // the clip's settings leave the texts be
        project.Undo();
        Assert.Null(project.Edit.Aspect);
        Assert.True(project.RemoveTexts([index]));
        Assert.Empty(project.Texts);
    }

    [Fact]
    public void Silences_come_out_of_parts_that_play_forwards()
    {
        var project = Project();
        var removed = project.RemoveSilences(source => source is null ? [new ClipRange(10, 20), new ClipRange(98, 100)] : [new ClipRange(0, 5)]);
        Assert.Equal(3, removed);
        // 10.12–19.88 out, 98.12 to the end out, the second video's first 4.88 s out.
        Near(140 - 9.76 - 1.88 - 4.88, project.Length, 1e-9);
        Assert.Equal(3, project.Parts.Count);
        project.Undo();
        Assert.Equal(140, project.Length);
    }

    [Fact]
    public void Clamped_keeps_an_editor_clip_in_its_order_inside_each_video()
    {
        var edit = new ClipEdit
        {
            Editor = true,
            Aspect = "9:16",
            Fit = ClipFit.Crop,
            Resolution = 1080,
            Parts =
            [
                new() { Source = Second, Start = 30, End = 60, Zoom = 9, FocusX = 2 },
                new() { Start = 10, End = 20, Hold = 99 },
                ClipProject.Pause(3),
                new() { Start = 5, End = 15, Transition = ClipTransitionKind.Crossfade },
            ],
            Texts = [ClipText.Of(ClipTextStyle.Title, "  ", 0), ClipText.Of(ClipTextStyle.Label, "Mine", 1, 999)],
        };
        var clamped = edit.Clamped(source => source is null ? 100 : 40);
        Assert.Equal(4, clamped.Parts.Count);
        Assert.Equal((30.0, 40.0), (clamped.Parts[0].Start, clamped.Parts[0].End)); // inside the 40 s video
        Assert.Equal((ClipPart.MaxZoom, 1.0), (clamped.Parts[0].Zoom, clamped.Parts[0].FocusX));
        Assert.Equal(ClipPart.MaxHold, clamped.Parts[1].Hold);
        Assert.Equal(ClipTransitionKind.Cut, clamped.Parts[^1].Transition);
        var text = Assert.Single(clamped.Texts); // an empty one is left out
        Assert.Equal(clamped.Length - 1, text.Length);
        Assert.Equal(("9:16", ClipFit.Crop, 1080), (clamped.Aspect, clamped.Fit, clamped.Resolution));
        // A quick cut ignores the editor's settings.
        var quick = new ClipEdit { Parts = [new() { Start = 1, End = 5, Reverse = true, Zoom = 2, Source = Second }] }.Clamped(100);
        Assert.True(quick.Parts[0] is { Reverse: false, Zoom: 1, Source: null });
    }

    [Fact]
    public void The_editor_clip_survives_json()
    {
        var project = Project();
        project.ChangePart(1, p =>
        {
            p.Reverse = true;
            p.Transition = ClipTransitionKind.Black;
        });
        project.ChangePart(0, p => p.Transition = ClipTransitionKind.Black);
        project.AddText(ClipText.Of(ClipTextStyle.Label, "Mine", 1));
        project.ChangeClip(c => c.Aspect = "1:1");
        var preset = new Preset { TargetFormat = "mp4", Media = { Clip = project.ToEdit() } };
        var json = JsonSerializer.Serialize(preset, FileeJsonContext.Default.Preset);
        Assert.Contains("\"Black\"", json);
        var read = JsonSerializer.Deserialize(json, FileeJsonContext.Default.Preset)!.Media.Clip!;
        Assert.True(read.Editor);
        Assert.Equal(Second, read.Parts[1].Source);
        Assert.True(read.Parts[1].Reverse);
        Assert.Equal(ClipTransitionKind.Black, read.Parts[0].Transition);
        Assert.Equal(("Mine", ClipTextStyle.Label, "1:1"), (read.Texts[0].Text, read.Texts[0].Style, read.Aspect));
    }

    [Theory]
    [InlineData(null, 0, 1920, 1080, 1920, 1080)]
    [InlineData("9:16", 0, 1920, 1080, 1080, 1920)]
    [InlineData("9:16", 720, 1920, 1080, 720, 1280)]
    [InlineData("1:1", 0, 1080, 1920, 1080, 1080)]
    [InlineData("4:5", 1080, 1920, 1080, 1080, 1350)]
    [InlineData(null, 720, 1920, 1080, 1280, 720)]
    public void The_frame_takes_its_shape_and_shorter_side(string? aspect, int resolution, double width, double height, int expectedWidth, int expectedHeight) =>
        Assert.Equal((expectedWidth, expectedHeight), ClipCanvas.Size(aspect, resolution, width, height));

    [Fact]
    public void A_picture_is_fitted_cropped_zoomed_and_moved_freely()
    {
        // 16:9 into 9:16: fitted it is as wide as the frame, in the middle.
        var fit = ClipCanvas.Place(1920, 1080, 1080, 1920, ClipFit.Bars);
        Near(1080, fit.Width);
        Near((1920 - 607.5) / 2, fit.Y);
        // Cropped it fills the height, its middle where the focus puts it.
        var middle = ClipCanvas.Place(1920, 1080, 1080, 1920, ClipFit.Crop);
        Near(1920, middle.Height);
        Near((1080 - middle.Width) / 2, middle.X);
        var left = ClipCanvas.Place(1920, 1080, 1080, 1920, ClipFit.Crop, focusX: 0);
        Near(-left.Width / 2, left.X);
        // Twice as large in the same shape, or half as large in the middle.
        Assert.Equal(new ClipPlacement(3840, 2160, -960, -540), ClipCanvas.Place(1920, 1080, 1920, 1080, ClipFit.Bars, zoom: 2));
        Assert.Equal(new ClipPlacement(960, 540, 480, 270), ClipCanvas.Place(1920, 1080, 1920, 1080, ClipFit.Bars, zoom: 0.5));
        // A drag moves the middle with the pointer, kept inside the frame.
        Assert.Equal((0.75, 0.5), ClipCanvas.Drag(1920, 1080, 0.5, 0.5, 480, 0));
        Assert.Equal((1.0, 0.5), ClipCanvas.Drag(1920, 1080, 0.5, 0.5, 5000, 0));
        Assert.Equal(((byte)255, (byte)212, (byte)59), ClipCanvas.ParseColor("#FFD43B"));
        Assert.Null(ClipCanvas.ParseColor("red"));
        // Angles as written.
        Assert.Equal(-90, ClipCanvas.Angle(270));
        Assert.Equal(180, ClipCanvas.Angle(-180));
        Assert.Equal(0, ClipCanvas.Angle(double.NaN));
    }

    [Fact]
    public void Sounds_go_in_a_row_on_their_tracks_and_change()
    {
        var project = Project();
        Assert.Equal(0, project.AddSound(@"D:\Music\one.mp3", 30));
        Assert.Equal(1, project.AddSound(@"D:\Music\two.mp3", 20)); // right after the first, on A2
        Assert.Equal(30, project.Sounds[1].Offset);
        Assert.Equal(2, project.AddSound(@"D:\Music\three.mp3", 10, track: 1, offset: 5));
        Assert.Equal(2, project.SoundTracks);
        project.ChangeSound(1, s =>
        {
            s.From = 4;
            s.Play = 8;
            s.Volume = 1.5;
        });
        Assert.Equal(8, project.Sounds[1].PlayLength(project.Length));
        var rest = project.Edit.From(33);
        // The first and the one on A3 have played; the second plays on from where it is.
        var left = Assert.Single(rest.Sounds);
        Assert.Equal((7.0, 5.0, 0.0), (left.From, left.Play, left.Offset));
        Assert.True(project.RemoveSounds([0, 2]));
        Assert.Single(project.Sounds);
        project.Undo();
        Assert.Equal(3, project.Sounds.Count);
    }

    [Fact]
    public void The_quick_cuts_music_becomes_the_first_sound()
    {
        var edit = ClipEdit.Of(new ClipRange(0, 10));
        edit.Music = new ClipMusic { Path = "song.mp3", Length = 40, Offset = 2 };
        var project = new ClipProject(edit, _ => 100);
        Assert.Null(project.Edit.Music);
        Assert.Equal(("song.mp3", 0, 2.0), (project.Sounds[0].Path, project.Sounds[0].Track, project.Sounds[0].Offset));
        // Back to a quick cut it is the music again.
        var cutter = ClipCutter.FromEdit(100, project.ToEdit());
        Assert.Equal("song.mp3", cutter!.Music!.Path);
    }

    [Fact]
    public void Pictures_over_the_video_and_turned_parts()
    {
        var project = Project();
        var index = project.AddImage(new ClipImage { Path = "logo.png", Start = 1, Length = 4 });
        project.ChangeImage(index, i =>
        {
            i.Width = 9;
            i.Rotation = 450;
            i.Opacity = 0;
        });
        var image = project.Images[index];
        Assert.Equal((ClipImage.MaxWidth, 90.0, 0.05), (image.Width, image.Rotation, image.Opacity));
        project.ChangePart(0, p =>
        {
            p.Rotation = -90;
            p.Zoom = 0.5;
        });
        Assert.Equal((-90.0, 0.5), (project.Parts[0].Rotation, project.Parts[0].Zoom));
        Assert.False(project.Parts[0].IsPlain);
        Assert.Null(ClipCutter.FromEdit(100, project.ToEdit()));
        var clamped = project.ToEdit().Clamped(_ => 100);
        Assert.Single(clamped.Images);
        Assert.Equal(-90, clamped.Parts[0].Rotation);
        // A file taken out of the project takes its parts and pictures along.
        Assert.True(project.RemoveFile(Second, Second));
        Assert.Single(project.Parts);
        Assert.False(project.RemoveFile("whatever", null)); // nothing would be left to play
    }
}
