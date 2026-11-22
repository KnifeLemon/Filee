// The frames "Mark this frame" takes (#1205): named after the video and the frame's time, each in a work folder of its
// own, saved by Annotate next to the video rather than in the work folder, and deleted once their marked copy is saved,
// or when Annotate leaves them unsaved.

using Filee.App.Services;
using Filee.Core.Conversion;
using Filee.Core.Platform;
using Filee.Core.Presets;
using ImageMagick;

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

    // ───────── The tray's store (#1205 step 3) ─────────

    private const double Length = 312.4;

    /// <summary>A video file of <paramref name="bytes"/> bytes in <paramref name="folder"/>.</summary>
    private string Video(string folder, string name = "Site walk.mp4", int bytes = 1000)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    /// <summary>
    /// A marked copy of <paramref name="video"/>'s frame at <paramref name="seconds"/> in <paramref name="folder"/>.
    /// </summary>
    private static string Copy(string folder, string name, string video, double seconds, string note = "", double length = Length)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        using var image = new MagickImage(MagickColors.White, 8, 8);
        image.SetAttribute(FrameTags.Key, FrameTags.ToText(new FrameFacts(Path.GetFileName(video), length, new FileInfo(video).Length, seconds,
            (long)(seconds * 30), note)));
        image.Write(path, MagickFormat.Png);
        return path;
    }

    private static Preset Tool() => BuiltInData.AnnotatePresets().Single();

    /// <summary>
    /// <paramref name="path"/> as written <paramref name="minutes"/> minutes ago: a copy saved earlier.
    /// </summary>
    private static string Aged(string path, int minutes = 10)
    {
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutes));
        return path;
    }

    /// <summary>
    /// The job Annotate's save of <paramref name="frame"/> ends with, its copy at <paramref name="output"/>.
    /// </summary>
    private static async Task<ConversionJob> SavedJob(MarkedFrames marks, string frame, string output)
    {
        var job = new ConversionJob([frame], await marks.ApplyAsync(frame, AnnotateTool.ConversionOf(Tool(), "png", []), "",
            TestContext.Current.CancellationToken), "Annotate");
        job.Files[0].State = FileState.Done;
        job.Files[0].Outputs.Add(output);
        return job;
    }

    private string Data => Path.Combine(_root, "data");

    /// <summary>Records what goes to the Trash, and refuses when told to.</summary>
    private sealed class FakeTrash
    {
        public List<string> Trashed { get; } = [];
        public TrashRefusal? Refuse { get; set; }

        public TrashRefusal? Move(string path)
        {
            if (Refuse is { } refusal)
                return refusal;
            Trashed.Add(path);
            File.Delete(path);
            return null;
        }
    }

    [Fact]
    public async Task ApplyAsync_tags_the_copy_with_its_facts_and_note()
    {
        using var marks = new MarkedFrames(_root);
        var video = Video(Path.Combine(_root, "videos"));
        var frame = marks.NewPath(video, 124.7, Length, _ => Task.FromResult<long?>(3741));
        var conversion = await marks.ApplyAsync(frame, AnnotateTool.ConversionOf(Tool(), "png", []), "Loose cable", TestContext.Current.CancellationToken);
        Assert.Equal(new FrameFacts("Site walk.mp4", Length, 1000, 124.7, 3741, "Loose cable"), conversion.Image.Frame);
        Assert.Equal(Path.GetDirectoryName(video), conversion.Output.SourceFolder); // step 2's rule still applies
        // Any other picture: as it is, no tags.
        var other = await marks.ApplyAsync(Path.Combine(_root, "photo.png"), AnnotateTool.ConversionOf(Tool(), "png", []), "x", TestContext.Current.CancellationToken);
        Assert.Null(other.Image.Frame);
    }

    [Fact]
    public async Task ApplyAsync_waits_for_the_number_and_does_without_one_that_fails()
    {
        using var marks = new MarkedFrames(_root);
        var video = Video(Path.Combine(_root, "videos"));
        var counting = new TaskCompletionSource<long?>();
        var frame = marks.NewPath(video, 1, Length, _ => counting.Task);
        var applying = marks.ApplyAsync(frame, AnnotateTool.ConversionOf(Tool(), "png", []), "", TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(applying.IsCompleted);
        counting.SetResult(30);
        Assert.Equal(30, (await applying).Image.Frame!.FrameNumber);

        var failing = marks.NewPath(video, 2, Length, _ => Task.FromException<long?>(new IOException("ffprobe")));
        var tagged = await marks.ApplyAsync(failing, AnnotateTool.ConversionOf(Tool(), "png", []), "", TestContext.Current.CancellationToken);
        Assert.Null(tagged.Image.Frame!.FrameNumber);
    }

    [Fact]
    public void The_tray_finds_a_videos_copies_by_their_text_in_time_order()
    {
        using var marks = new MarkedFrames(_root);
        var folder = Path.Combine(_root, "videos");
        var video = Video(folder);
        var late = Copy(folder, "Site walk 4m07.267s Annotated.png", video, 247.267);
        var renamed = Copy(folder, "the cable.png", video, 124.7, "Loose cable"); // renamed by hand: still counts
        Copy(folder, "photo.png", Video(Path.Combine(_root, "other"), "Holiday.mp4"), 3); // another video's
        File.WriteAllText(Path.Combine(folder, "broken.png"), "not a picture");
        var copies = marks.CopiesOf(video, Length, Tool(), TestContext.Current.CancellationToken);
        Assert.Equal([renamed, late], copies.Select(c => c.Path));
        Assert.Equal("Loose cable", copies[0].Facts.Note);
        // Deleted by hand: gone from the tray.
        File.Delete(late);
        Assert.Equal([renamed], marks.CopiesOf(video, Length, Tool(), TestContext.Current.CancellationToken).Select(c => c.Path));
    }

    [Fact]
    public void Copies_of_a_same_named_video_stay_apart()
    {
        using var marks = new MarkedFrames(_root);
        var shared = Path.Combine(_root, "all copies");
        var first = Video(Path.Combine(_root, "monday"), "clip.mp4", 1000);
        var second = Video(Path.Combine(_root, "tuesday"), "clip.mp4", 2000);
        var tool = Tool();
        tool.Output.Location = OutputLocation.CustomFolder;
        tool.Output.CustomFolder = shared;
        var mine = Copy(shared, "a.png", first, 10);
        var theirs = Copy(shared, "b.png", second, 10);
        Assert.Equal([mine], marks.CopiesOf(first, Length, tool, TestContext.Current.CancellationToken).Select(c => c.Path));
        Assert.Equal([theirs], marks.CopiesOf(second, Length, tool, TestContext.Current.CancellationToken).Select(c => c.Path));
        Assert.Empty(marks.CopiesOf(first, 100, tool, TestContext.Current.CancellationToken)); // same name and size, another length
    }

    [Fact]
    public void A_folder_that_cant_be_read_is_skipped()
    {
        using var marks = new MarkedFrames(_root);
        var video = Video(Path.Combine(_root, "videos"));
        var tool = Tool();
        tool.Output.Location = OutputLocation.CustomFolder;
        tool.Output.CustomFolder = Path.Combine(_root, "no such folder");
        Assert.Empty(marks.CopiesOf(video, Length, tool, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Copies_in_a_folder_the_rules_no_longer_name_are_still_found_and_marking_again_replaces()
    {
        var trash = new FakeTrash();
        Directory.CreateDirectory(Data);
        using var marks = new MarkedFrames(_root, dataDirectory: Data, trash: trash.Move);
        var video = Video(Path.Combine(_root, "videos"));
        var dated = Path.Combine(_root, "videos", "2026-11-18");
        var earlier = Aged(Copy(dated, "Site walk 2m04.700s Annotated.png", video, 124.7, "old note"));
        var otherFrame = Aged(Copy(dated, "Site walk 4m07.267s Annotated.png", video, 247.267));

        // A copy of the same frame saved today, by rules that name another folder.
        var frame = marks.NewPath(video, 124.7, Length, _ => Task.FromResult<long?>(3741));
        var conversion = await marks.ApplyAsync(frame, AnnotateTool.ConversionOf(Tool(), "png", []), "new note", TestContext.Current.CancellationToken);
        var today = Path.Combine(_root, "videos", "2026-11-19");
        var saved = Copy(today, "Site walk 2m04.700s Annotated.png", video, 124.7, "new note");
        var job = new ConversionJob([frame], conversion, "Annotate");
        job.Files[0].State = FileState.Done;
        job.Files[0].Outputs.Add(saved);
        // The earlier folder was remembered when its copies were saved.
        marks.Remember(video, dated);
        var changed = new List<string>();
        marks.CopiesChanged += changed.Add;

        marks.OnJobFinished(job);
        await marks.LastSaved;

        Assert.Equal([earlier], trash.Trashed); // the same frame's earlier copy, not another frame's
        Assert.True(File.Exists(otherFrame));
        Assert.Equal([video], changed);
        Assert.Contains(today, marks.Remembered(video));
        Assert.Equal([saved, otherFrame], marks.CopiesOf(video, Length, Tool(), TestContext.Current.CancellationToken).Select(c => c.Path));
    }

    [Fact]
    public void Remove_moves_the_copy_to_the_trash_or_says_why_it_couldnt()
    {
        var trash = new FakeTrash();
        using var marks = new MarkedFrames(_root, trash: trash.Move);
        var folder = Path.Combine(_root, "videos");
        var video = Video(folder);
        var copy = Copy(folder, "a.png", video, 1);
        var found = Assert.Single(marks.CopiesOf(video, Length, Tool(), TestContext.Current.CancellationToken));
        var changed = new List<string>();
        marks.CopiesChanged += changed.Add;

        trash.Refuse = new TrashRefusal("trash.no_recycle_bin");
        Assert.Equal("trash.no_recycle_bin", marks.Remove(found, video)?.ReasonKey);
        Assert.True(File.Exists(copy));

        trash.Refuse = null;
        Assert.Null(marks.Remove(found, video));
        Assert.Equal([copy], trash.Trashed);
        Assert.Null(marks.Remove(found, video)); // already gone: nothing to say, and the tray reads again
        Assert.Equal([video, video, video], changed);
        // A file of that name that is no longer that frame's copy isn't touched.
        var other = Copy(folder, "a.png", video, 99);
        Assert.Null(marks.Remove(found, video));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public async Task The_copy_just_saved_is_never_trashed_under_another_spelling_of_its_name()
    {
        var trash = new FakeTrash();
        Directory.CreateDirectory(Data);
        using var marks = new MarkedFrames(_root, dataDirectory: Data, trash: trash.Move);
        var folder = Path.Combine(_root, "videos");
        var video = Video(folder);
        // As a case-insensitive disk keeps it: the old name's case, overwritten with the new copy.
        var onDisk = Copy(folder, "site walk 0m01.000s annotated.PNG", video, 1, "new marks");
        var frame = marks.NewPath(video, 1, Length, _ => Task.FromResult<long?>(30));
        var job = new ConversionJob([frame], await marks.ApplyAsync(frame, AnnotateTool.ConversionOf(Tool(), "png", []), "", TestContext.Current.CancellationToken), "Annotate");
        job.Files[0].State = FileState.Done;
        job.Files[0].Outputs.Add(Path.Combine(folder, "Site walk 0m01.000s Annotated.png"));
        marks.OnJobFinished(job);
        await marks.LastSaved;
        Assert.Empty(trash.Trashed);
        Assert.True(File.Exists(onDisk));
    }

    [Fact]
    public async Task A_cancelled_count_saves_without_a_number()
    {
        using var marks = new MarkedFrames(_root);
        var video = Video(Path.Combine(_root, "videos"));
        var frame = marks.NewPath(video, 2, Length, _ => Task.FromCanceled<long?>(new CancellationToken(true)));
        var tagged = await marks.ApplyAsync(frame, AnnotateTool.ConversionOf(Tool(), "png", []), "", TestContext.Current.CancellationToken);
        Assert.Null(tagged.Image.Frame!.FrameNumber);
    }

    [Fact]
    public void Remembered_folders_last_across_a_restart_and_a_damaged_file_reads_as_none()
    {
        Directory.CreateDirectory(Data);
        var gone = Path.Combine(_root, "gone");
        using (var marks = new MarkedFrames(_root, dataDirectory: Data))
        {
            marks.Remember(Path.Combine(_root, "a.mp4"), Path.Combine(_root, "copies"));
            marks.Remember(Path.Combine(_root, "b.mp4"), gone);
        }
        using (var again = new MarkedFrames(_root, dataDirectory: Data))
        {
            Assert.Equal([Path.Combine(_root, "copies")], again.Remembered(Path.Combine(_root, "a.mp4")));
            // Another b.mp4 elsewhere: that one's folders aren't looked in.
            Assert.Empty(again.CopiesOf(Video(Path.Combine(_root, "videos"), "b.mp4"), Length, Tool(), TestContext.Current.CancellationToken));
            Assert.Equal([gone], again.Remembered(Path.Combine(_root, "b.mp4")));
            // A remembered folder that is gone is forgotten once looked in.
            Assert.Empty(again.CopiesOf(Video(_root, "b.mp4"), Length, Tool(), TestContext.Current.CancellationToken));
            Assert.Empty(again.Remembered(Path.Combine(_root, "b.mp4")));
        }
        File.WriteAllText(Path.Combine(Data, "marked-frames.json"), "{ not json");
        using var damaged = new MarkedFrames(_root, dataDirectory: Data);
        Assert.Empty(damaged.Remembered(Path.Combine(_root, "a.mp4")));
        damaged.Remember(Path.Combine(_root, "a.mp4"), Path.Combine(_root, "copies"));
        Assert.Single(damaged.Remembered(Path.Combine(_root, "a.mp4")));
    }

    [Fact]
    public void Remembered_folders_are_capped_and_the_newest_kept()
    {
        Directory.CreateDirectory(Data);
        using var marks = new MarkedFrames(_root, dataDirectory: Data);
        string VideoAt(int i) => Path.Combine(_root, "videos", $"{i}.mp4");
        for (var i = 0; i < MarkedFrames.FolderLimit + 5; i++)
            marks.Remember(VideoAt(i), Path.Combine(_root, "copies", $"{i}"));
        Assert.Empty(marks.Remembered(VideoAt(1))); // among the oldest
        Assert.Single(marks.Remembered(VideoAt(MarkedFrames.FolderLimit + 4)));
        marks.Remember(VideoAt(5), Path.Combine(_root, "copies", "5")); // seen again: once, now the newest
        Assert.Single(marks.Remembered(VideoAt(5)));
        marks.Remember(VideoAt(9999), Path.Combine(_root, "copies", "new"));
        Assert.Single(marks.Remembered(VideoAt(5)));
    }

    [Fact]
    public async Task Marking_again_leaves_a_users_own_file_of_that_frame_alone()
    {
        var trash = new FakeTrash();
        Directory.CreateDirectory(Data);
        using var marks = new MarkedFrames(_root, dataDirectory: Data, trash: trash.Move);
        var folder = Path.Combine(_root, "videos");
        var video = Video(folder);
        marks.Remember(video, folder);
        var earlier = Aged(Copy(folder, "Site walk 0m01.000s Annotated.png", video, 1));
        var compressed = Aged(Copy(folder, "Site walk 0m01.000s Annotated_compressed.png", video, 1)); // Compress image kept the text
        var mine = Aged(Copy(folder, "for the report.png", video, 1)); // renamed, or edited with ExifTool
        var frame = marks.NewPath(video, 1, Length, _ => Task.FromResult<long?>(30));
        var job = new ConversionJob([frame], await marks.ApplyAsync(frame, AnnotateTool.ConversionOf(Tool(), "png", []), "", TestContext.Current.CancellationToken), "Annotate");
        job.Files[0].State = FileState.Done;
        job.Files[0].Outputs.Add(Copy(folder, "Site walk 0m01.000s Annotated (2).png", video, 1));
        marks.OnJobFinished(job);
        await marks.LastSaved;
        Assert.Equal([earlier], trash.Trashed);
        Assert.True(File.Exists(compressed));
        Assert.True(File.Exists(mine));
    }

    /// <summary>
    /// The same frame saved twice back to back, the first save's clean-up running late (a slow folder): the second
    /// copy, newer, is left alone, and the second save's clean-up then replaces the first.
    /// </summary>
    [Fact]
    public async Task A_later_saves_copy_is_never_replaced_by_an_earlier_saves_late_clean_up()
    {
        var trash = new FakeTrash();
        Directory.CreateDirectory(Data);
        using var marks = new MarkedFrames(_root, dataDirectory: Data, trash: trash.Move);
        var folder = Path.Combine(_root, "videos");
        var video = Video(folder);
        marks.Remember(video, folder);
        var first = Aged(Copy(folder, "Site walk 0m01.000s Annotated.png", video, 1, "first"), minutes: 1);
        var second = Copy(folder, "Site walk 0m01.000s Annotated (2).png", video, 1, "second");
        var firstJob = await SavedJob(marks, marks.NewPath(video, 1, Length, _ => Task.FromResult<long?>(30)), first);
        var secondJob = await SavedJob(marks, marks.NewPath(video, 1, Length, _ => Task.FromResult<long?>(30)), second);

        marks.OnJobFinished(firstJob);
        await marks.LastSaved;
        Assert.Empty(trash.Trashed);
        marks.OnJobFinished(secondJob);
        await marks.LastSaved;
        Assert.Equal([first], trash.Trashed);
        Assert.Equal("second", Assert.Single(marks.CopiesOf(video, Length, Tool(), TestContext.Current.CancellationToken)).Facts.Note);
    }

    /// <summary>
    /// Two videos of one name saved to one folder: marking a frame of one again leaves the other's copy of that time
    /// (named as Annotate names it) alone.
    /// </summary>
    [Fact]
    public async Task Marking_again_leaves_another_videos_copy_of_that_time_alone()
    {
        var trash = new FakeTrash();
        Directory.CreateDirectory(Data);
        using var marks = new MarkedFrames(_root, dataDirectory: Data, trash: trash.Move);
        var shared = Path.Combine(_root, "all copies");
        var mine = Video(Path.Combine(_root, "monday"), "clip.mp4", 1000);
        var theirs = Video(Path.Combine(_root, "tuesday"), "clip.mp4", 2000);
        marks.Remember(mine, shared);
        var theirCopy = Aged(Copy(shared, "clip 0m10.000s Annotated.png", theirs, 10));
        var myEarlier = Aged(Copy(shared, "clip 0m10.000s Annotated (2).png", mine, 10));
        var saved = Copy(shared, "clip 0m10.000s Annotated (3).png", mine, 10);
        marks.OnJobFinished(await SavedJob(marks, marks.NewPath(mine, 10, Length, _ => Task.FromResult<long?>(300)), saved));
        await marks.LastSaved;
        Assert.Equal([myEarlier], trash.Trashed);
        Assert.True(File.Exists(theirCopy));
    }

    /// <summary>
    /// Two copies of one frame time (the Trash refused the earlier one, or a file of one's own carries its text): the
    /// list shows the newest.
    /// </summary>
    [Fact]
    public void One_copy_per_frame_time_the_newest()
    {
        using var marks = new MarkedFrames(_root);
        var folder = Path.Combine(_root, "videos");
        var video = Video(folder);
        Copy(folder, "b.png", video, 5, "newer");
        Aged(Copy(folder, "a.png", video, 5, "older"));
        Aged(Copy(folder, "c.png", video, 5, "oldest"), minutes: 20);
        var copy = Assert.Single(marks.CopiesOf(video, Length, Tool(), TestContext.Current.CancellationToken));
        Assert.Equal("newer", copy.Facts.Note);
    }

    /// <summary>
    /// A remembered folder whose drive or share is away for now (its parent isn't there either) is kept for when it is
    /// back; one gone from a folder that is there is forgotten.
    /// </summary>
    [Fact]
    public void A_folder_on_a_drive_that_is_away_is_kept_and_one_deleted_is_forgotten()
    {
        Directory.CreateDirectory(Data);
        using var marks = new MarkedFrames(_root, dataDirectory: Data);
        var video = Video(Path.Combine(_root, "videos"));
        var away = Path.Combine(_root, "unplugged drive", "2026-11-18");
        var deleted = Path.Combine(_root, "videos", "2026-11-17");
        marks.Remember(video, away);
        marks.Remember(video, deleted);
        Assert.Empty(marks.CopiesOf(video, Length, Tool(), TestContext.Current.CancellationToken));
        Assert.Equal([away], marks.Remembered(video));
        // Back again: its copies are listed.
        Copy(away, "Site walk 0m03.000s Annotated.png", video, 3);
        Assert.Single(marks.CopiesOf(video, Length, Tool(), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The list looks where a frame's save writes it, with nothing remembered: a frame is saved alone, so a subfolder
    /// "only for several files" isn't used for it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_list_looks_where_a_frame_saved_alone_goes(bool onlyForSeveral)
    {
        using var marks = new MarkedFrames(_root);
        var folder = Path.Combine(_root, "videos");
        var video = Video(folder);
        var tool = Tool();
        tool.Output.Location = OutputLocation.Subfolder;
        tool.Output.SubfolderName = "frames";
        tool.Output.SubfolderOnlyForSeveralFiles = onlyForSeveral;
        var copy = Copy(onlyForSeveral ? folder : Path.Combine(folder, "frames"), "Site walk 0m03.000s Annotated.png", video, 3);
        Assert.Equal([copy], marks.CopiesOf(video, Length, tool, TestContext.Current.CancellationToken).Select(c => c.Path));
    }

    /// <summary>
    /// A video whose name starts with a dot (hidden on macOS and Linux) has its copies listed all the same.
    /// </summary>
    [Fact]
    public void A_video_named_with_a_dot_has_its_copies_listed()
    {
        using var marks = new MarkedFrames(_root);
        var folder = Path.Combine(_root, "videos");
        var video = Video(folder, ".site walk.mp4");
        var copy = Copy(folder, ".site walk 0m03.000s Annotated.png", video, 3);
        Assert.Equal([copy], marks.CopiesOf(video, Length, Tool(), TestContext.Current.CancellationToken).Select(c => c.Path));
    }

    /// <summary>
    /// macOS's file flags, where an online-only cloud file says it is one, are read where they are: a file's size
    /// matches and the "hidden" flag chflags sets is seen (the dataless flag itself only a cloud provider can set).
    /// </summary>
    [Fact]
    public void A_files_macOS_flags_are_read_without_opening_it()
    {
        if (!OperatingSystem.IsMacOS())
            Assert.Skip("Online-only files are marked by attributes on Windows; Linux has no such mark.");
        var path = Path.Combine(_root, "flags.png");
        File.WriteAllBytes(path, new byte[1234]);
        var plain = Assert.NotNull(MacFileFlags.Read(path));
        Assert.Equal(1234, plain.Size);
        Assert.Equal(0u, plain.Flags & MacFileFlags.Hidden);
        Assert.False(MacFileFlags.IsDataless(path));
        using (var chflags = System.Diagnostics.Process.Start("/usr/bin/chflags", ["hidden", path]))
            chflags.WaitForExit();
        Assert.NotEqual(0u, Assert.NotNull(MacFileFlags.Read(path)).Flags & MacFileFlags.Hidden);
        Assert.Null(MacFileFlags.Read(Path.Combine(_root, "no such file.png")));
    }

    /// <summary>
    /// Where macOS and Linux mount a drive: a remembered folder there that is gone may be a drive that is away, so it
    /// is kept; elsewhere it isn't.
    /// </summary>
    [Fact]
    public void A_drives_own_folder_counts_as_away_where_drives_are_mounted()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.False(MarkedFrames.IsMountArea(@"C:\Volumes"));
            return;
        }
        Assert.True(MarkedFrames.IsMountArea("/Volumes"));
        Assert.True(MarkedFrames.IsMountArea("/media/someone"));
        Assert.True(MarkedFrames.IsMountArea("/run/media/someone/"));
        Assert.True(MarkedFrames.IsMountArea("/mnt"));
        Assert.False(MarkedFrames.IsMountArea("/Users/someone/Videos"));
        Assert.False(MarkedFrames.IsMountArea("/Volumes/USB"));

        // Through a look: a drive's own folder that isn't plugged in now stays remembered.
        if (new[] { "/Volumes", "/mnt", "/media" }.FirstOrDefault(Directory.Exists) is not { } mounts)
            return;
        Directory.CreateDirectory(Data);
        using var marks = new MarkedFrames(_root, dataDirectory: Data);
        var video = Video(Path.Combine(_root, "videos"));
        var unplugged = Path.Combine(mounts, "filee-test-" + Guid.NewGuid().ToString("N"));
        marks.Remember(video, unplugged);
        Assert.Empty(marks.CopiesOf(video, Length, Tool(), TestContext.Current.CancellationToken));
        Assert.Equal([unplugged], marks.Remembered(video));
    }

    /// <summary>
    /// Two saves of one frame written at the same time to the disk's precision (FAT keeps 2 s) can't be told apart:
    /// neither clean-up trashes the other, so nothing newer is lost, and the list shows one of them by a fixed order
    /// (its path), not by the order the folders are looked through.
    /// </summary>
    [Fact]
    public async Task Copies_written_at_the_same_time_both_stay_and_the_list_shows_one()
    {
        var trash = new FakeTrash();
        Directory.CreateDirectory(Data);
        using var marks = new MarkedFrames(_root, dataDirectory: Data, trash: trash.Move);
        var folder = Path.Combine(_root, "videos");
        var video = Video(folder);
        marks.Remember(video, folder);
        var at = DateTime.UtcNow.AddMinutes(-1);
        var other = Path.Combine(_root, "aaa other folder"); // before "videos" by path, looked through after it
        marks.Remember(video, other);
        var first = Copy(folder, "Site walk 0m01.000s Annotated (2).png", video, 1, "first");
        var second = Copy(other, "Site walk 0m01.000s Annotated.png", video, 1, "second");
        File.SetLastWriteTimeUtc(first, at);
        File.SetLastWriteTimeUtc(second, at);
        foreach (var output in new[] { first, second })
        {
            marks.OnJobFinished(await SavedJob(marks, marks.NewPath(video, 1, Length, _ => Task.FromResult<long?>(30)), output));
            await marks.LastSaved;
        }
        Assert.Empty(trash.Trashed);
        Assert.Equal(second, Assert.Single(marks.CopiesOf(video, Length, Tool(), TestContext.Current.CancellationToken)).Path);
    }
}
