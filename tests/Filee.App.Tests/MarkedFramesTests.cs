// The frames "Mark this frame" takes (#1205): named after the video and the frame's time, each in a work folder of its
// own, saved by Annotate next to the video rather than in the work folder, and deleted once their marked copy is saved,
// or when Annotate leaves them unsaved.

using Filee.App.Services;
using Filee.Core.Conversion;
using Filee.Core.Presets;

namespace Filee.App.Tests;

public sealed class MarkedFramesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("filee-marked-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(0, "0m00.000s")]
    [InlineData(5.25, "0m05.250s")]
    [InlineData(95.3, "1m35.300s")]
    [InlineData(103.4006, "1m43.401s")] // rounded to the millisecond
    [InlineData(3723.456, "1h02m03.456s")]
    [InlineData(59.9996, "1m00.000s")] // rounding carries into the minutes
    public void A_frame_is_named_after_its_video_and_its_time(double seconds, string time)
    {
        Assert.Equal($"Site walk {time}.png", MarkedFrames.NameFor(Path.Combine("videos", "Site walk.mp4"), seconds));
    }

    [Fact]
    public void Each_frame_gets_a_work_folder_the_sweep_after_a_crash_knows()
    {
        using var marks = new MarkedFrames(_root);
        var video = Path.Combine(_root, "videos", "clip.mov");
        var first = marks.NewPath(video, 12.5);
        var second = marks.NewPath(video, 12.5); // the same frame twice: two files, the first left alone
        Assert.NotEqual(first, second);
        foreach (var frame in new[] { first, second })
        {
            var folder = Path.GetDirectoryName(frame)!;
            Assert.Equal(_root, Path.GetDirectoryName(folder));
            Assert.Matches("^[0-9a-f]{10}$", Path.GetFileName(folder)); // as JobQueue.DeleteStaleWorkFolders expects
            Assert.True(Directory.Exists(folder));
            Assert.Equal("clip 0m12.500s.png", Path.GetFileName(frame));
            Assert.Equal(video, marks.VideoOf(frame));
        }
    }

    [Fact]
    public void Annotate_saves_a_frame_next_to_its_video_and_never_trashes_it()
    {
        using var marks = new MarkedFrames(_root);
        var video = Path.Combine(_root, "videos", "clip.mov");
        var frame = marks.NewPath(video, 1);
        var annotate = BuiltInData.AnnotatePresets().Single();
        annotate.Output.ReplaceOriginal = true;
        annotate.Output.TrashOriginalAfterConvert = true;
        var conversion = AnnotateTool.ConversionOf(annotate, "png", []);

        var applied = marks.Apply(frame, conversion);
        Assert.Equal(Path.GetDirectoryName(video), applied.Output.SourceFolder);
        Assert.False(applied.Output.ReplaceOriginal);
        Assert.False(applied.Output.TrashOriginalAfterConvert);
        Assert.Equal("{name} Annotated", applied.Output.FileNamePattern);
        Assert.True(conversion.Output.ReplaceOriginal); // the conversion itself is left as it was

        // A picture of one's own is saved as Annotate always saves it.
        var picture = Path.Combine(_root, "photo.png");
        Assert.Same(conversion, marks.Apply(picture, conversion));
    }

    [Theory]
    [InlineData(ConflictPolicy.Skip, ConflictPolicy.Rename)] // the same frame marked again: a second copy, not lost marks
    [InlineData(ConflictPolicy.Rename, ConflictPolicy.Rename)]
    [InlineData(ConflictPolicy.Overwrite, ConflictPolicy.Overwrite)]
    public void A_frame_marked_again_is_never_skipped_for_the_copy_already_there(ConflictPolicy chosen, ConflictPolicy used)
    {
        using var marks = new MarkedFrames(_root);
        var frame = marks.NewPath(Path.Combine(_root, "videos", "clip.mov"), 1);
        var conversion = AnnotateTool.ConversionOf(BuiltInData.AnnotatePresets().Single(), "png", []);
        conversion.Output.Conflict = chosen;
        Assert.Equal(used, marks.Apply(frame, conversion).Output.Conflict);
    }

    [Fact]
    public void A_name_too_long_for_a_file_is_shortened_and_keeps_its_time()
    {
        var video = Path.Combine("videos", new string('a', 250) + ".mp4");
        var name = MarkedFrames.NameFor(video, 95.3);
        Assert.EndsWith(" 1m35.300s.png", name);
        // Room left for Annotate's " Annotated" and a " (2)", within the 255 bytes a file name may have.
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(name) + " Annotated (99)".Length <= 255);
        var korean = Path.Combine("videos", new string('가', 120) + ".mp4"); // 3 bytes each in UTF-8
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(MarkedFrames.NameFor(korean, 95.3)) + 15 <= 255);
        Assert.Equal("Site walk 1m35.300s.png", MarkedFrames.NameFor("Site walk.mp4", 95.3)); // short names whole
    }

    [Fact]
    public void A_frame_goes_once_its_marked_copy_is_saved_or_when_it_is_left_unsaved()
    {
        using var marks = new MarkedFrames(_root);
        var saved = marks.NewPath("a.mp4", 1);
        var failed = marks.NewPath("a.mp4", 2);
        var left = marks.NewPath("a.mp4", 3);
        var cancelled = marks.NewPath("a.mp4", 4);
        var skipped = marks.NewPath("a.mp4", 5);
        var other = Path.Combine(_root, "other.png");
        File.WriteAllBytes(other, [1]);
        var job = new ConversionJob([saved, failed, other, cancelled, skipped], BuiltInData.AnnotatePresets().Single(), "Annotate");
        job.Files[0].State = FileState.Done;
        job.Files[1].State = FileState.Failed;
        job.Files[2].State = FileState.Done;
        job.Files[3].State = FileState.Cancelled;
        job.Files[4].State = FileState.Skipped;

        marks.OnJobFinished(job);
        foreach (var gone in new[] { saved, cancelled, skipped })
        {
            Assert.False(Directory.Exists(Path.GetDirectoryName(gone)));
            Assert.Null(marks.VideoOf(gone));
        }
        Assert.True(File.Exists(other)); // a picture of one's own, saved by the same job, stays
        // A failed one stays for "Try again".
        Assert.True(Directory.Exists(Path.GetDirectoryName(failed)));
        Assert.Equal("a.mp4", marks.VideoOf(failed));

        marks.Discard(left);
        Assert.False(Directory.Exists(Path.GetDirectoryName(left)));
        marks.Discard(left); // twice is harmless
        marks.Discard(other); // not a frame: left alone
        Assert.True(File.Exists(other));

        marks.Dispose();
        Assert.False(Directory.Exists(Path.GetDirectoryName(failed)));
    }

    /// <summary>
    /// A frame whose folder can't be deleted yet (on Windows, while Annotate or a virus scanner still has the file
    /// open) stays known, so the next try, at the latest when Filee quits, takes it; after quitting, a frame is not
    /// wanted.
    /// </summary>
    [Fact]
    public void A_frame_whose_folder_cannot_be_deleted_yet_is_tried_again()
    {
        if (!OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess)
            Assert.Skip("root may delete anything, so the folder can't be held here");
        var marks = new MarkedFrames(_root);
        var frame = marks.NewPath("a.mp4", 1);
        File.WriteAllBytes(frame, [1]);
        var folder = Path.GetDirectoryName(frame)!;
        var held = Hold(folder, frame);
        try
        {
            marks.Discard(frame);
            Assert.True(Directory.Exists(folder));
            Assert.True(marks.IsWanted(frame));
        }
        finally
        {
            held.Dispose();
        }
        marks.Dispose(); // quitting tries again
        Assert.False(Directory.Exists(folder));
        Assert.False(marks.IsWanted(frame));
    }

    /// <summary>Keeps <paramref name="folder"/> from being deleted until disposed.</summary>
    private IDisposable Hold(string folder, string file)
    {
        if (OperatingSystem.IsWindows())
            return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        var mode = File.GetUnixFileMode(_root);
        File.SetUnixFileMode(_root, mode & ~(UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
        return new Restore(() =>
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_root, mode);
        });
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
