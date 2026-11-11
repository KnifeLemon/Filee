using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Core.Watching;

namespace Filee.Core.Tests;

public class FolderWatcherTests
{
    /// <summary>A clock the test moves by hand; timers never fire (the test calls ProcessPendingAsync).</summary>
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoTimer();
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>A watcher whose conversions run on a JobQueue with a fake jpg → png converter into the rule's output folder.</summary>
    private static (FolderWatcher Watcher, FakeTime Time, List<ConversionJob> Jobs, JobQueue Queue) Create(WatchRule rule, FakeConverter? converter = null)
    {
        var queue = new JobQueue(new ConverterCatalog([converter ?? new FakeConverter("magick", new ConversionEdge("jpg", "png"))]));
        var jobs = new List<ConversionJob>();
        var time = new FakeTime();
        var watcher = new FolderWatcher(rule, async (files, _) =>
        {
            var preset = new Preset { TargetFormat = "png", Output = { Location = OutputLocation.CustomFolder, CustomFolder = rule.ResolvedOutputFolder } };
            var job = new ConversionJob(files, preset, "Test");
            await queue.RunAsync(job);
            jobs.Add(job);
            return job;
        }, settleTime: TimeSpan.FromSeconds(2), time: time);
        return (watcher, time, jobs, queue);
    }

    [Fact]
    public async Task An_atomically_published_folder_is_scanned_after_copying()
    {
        using var source = new TempDir(); using var target = new TempDir();
        var folder = Path.Combine(source.Path, "incoming"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.jpg"), "bytes");
        var rule = new WatchRule { Folder = target.Path, IncludeSubfolders = true };
        var (watcher, time, jobs, queue) = Create(rule);
        await using var watcherLifetime = watcher; await using var queueLifetime = queue;
        var output = await FolderCopy.CopyAsync(folder, new OutputRule { Location = OutputLocation.CustomFolder, CustomFolder = target.Path }, TestContext.Current.CancellationToken);
        watcher.Notice(output!); time.Advance(3);
        Assert.Equal(1, await watcher.ProcessPendingAsync());
        Assert.Equal(JobState.Completed, Assert.Single(jobs).State);
        Assert.True(File.Exists(Path.Combine(folder, "a.jpg")));
    }

    [Fact]
    public void Default_wait_is_longer_where_files_being_written_are_not_locked() =>
        Assert.Equal(TimeSpan.FromSeconds(OperatingSystem.IsWindows() ? 2 : 5), FolderWatcher.DefaultSettleTime);

    [Theory]
    [InlineData(AfterConversion.Keep, false)]
    [InlineData(AfterConversion.MoveToOriginals, false)]
    [InlineData(AfterConversion.Keep, true)]
    [InlineData(AfterConversion.MoveToOriginals, true)]
    public async Task Output_can_share_the_watched_folder_without_converting_its_own_results(AfterConversion originals, bool trailingSeparator)
    {
        using var dir = new TempDir();
        var rule = new WatchRule { Folder = dir.Path, OutputFolder = dir.Path + (trailingSeparator ? Path.DirectorySeparatorChar : ""), Originals = originals };
        var (watcher, time, jobs, queue) = Create(rule);
        await using var queueLifetime = queue;
        await using var watcherLifetime = watcher;
        var source = dir.File("photo.jpg");

        watcher.Notice(source);
        time.Advance(3);
        Assert.Equal(1, await watcher.ProcessPendingAsync());
        var output = Assert.Single(Assert.Single(jobs).Outputs);
        Assert.Equal(Path.Combine(dir.Path, "photo.png"), output);
        Assert.True(File.Exists(output));
        Assert.Equal(originals == AfterConversion.Keep, File.Exists(source));
        if (originals == AfterConversion.MoveToOriginals)
            Assert.True(File.Exists(Path.Combine(rule.OriginalsFolder, "photo.jpg")));

        watcher.Notice(output);
        time.Advance(3);
        Assert.Equal(0, await watcher.ProcessPendingAsync());
        Assert.Single(jobs);

        // A new incoming file still works after the result has been excluded.
        watcher.Notice(dir.File("next.jpg"));
        time.Advance(3);
        Assert.Equal(1, await watcher.ProcessPendingAsync());
        Assert.Equal(2, jobs.Count);
    }

    [Fact]
    public async Task Shared_folder_results_survive_a_restart_but_a_replaced_file_is_new_input()
    {
        using var dir = new TempDir();
        var rule = new WatchRule { Folder = dir.Path, OutputFolder = dir.Path, Originals = AfterConversion.MoveToOriginals };
        var (first, firstTime, firstJobs, firstQueue) = Create(rule);
        await using (firstQueue)
        await using (first)
        {
            first.Notice(dir.File("photo.jpg"));
            firstTime.Advance(3);
            Assert.Equal(1, await first.ProcessPendingAsync());
            Assert.Single(firstJobs);
        }

        var (restarted, time, jobs, queue) = Create(rule);
        await using var queueLifetime = queue;
        await using var watcherLifetime = restarted;
        Assert.True(restarted.Start()); // inbox mode scans existing files on startup
        time.Advance(3);
        Assert.Equal(0, await restarted.ProcessPendingAsync());
        Assert.Empty(jobs);
        Assert.True(File.Exists(Path.Combine(dir.Path, "photo.png")));

        // An incoming replacement with the old output's name must not be permanently blacklisted.
        var replaced = Path.Combine(dir.Path, "photo.png");
        File.WriteAllText(replaced, "a different incoming file, not the generated result");
        restarted.Notice(replaced);
        time.Advance(3);
        Assert.Equal(1, await restarted.ProcessPendingAsync());
        Assert.Single(jobs);
    }

    [Fact]
    public void An_output_folder_above_the_watch_root_does_not_exclude_the_inputs()
    {
        using var dir = new TempDir();
        var input = dir.File("photo.jpg");
        Assert.False(FolderWatcher.ShouldIgnore(input, new WatchRule
        {
            Folder = dir.Path,
            OutputFolder = Path.GetDirectoryName(dir.Path)!,
        }));
    }

    [Fact]
    public async Task A_file_is_converted_once_it_has_settled()
    {
        using var dir = new TempDir();
        var rule = new WatchRule { Folder = dir.Path };
        var (watcher, time, jobs, queue) = Create(rule);
        await using var _ = queue;
        await using var __ = watcher;

        var photo = dir.File("photo.jpg");
        watcher.Notice(photo);
        time.Advance(1);
        Assert.Equal(0, await watcher.ProcessPendingAsync()); // still within the settle time

        time.Advance(2);
        Assert.Equal(1, await watcher.ProcessPendingAsync());
        Assert.True(File.Exists(Path.Combine(dir.Path, "converted", "photo.png")));
        Assert.True(File.Exists(photo), "Keep leaves the source where it is");

        // Another event for the unchanged file does not convert it again.
        watcher.Notice(photo);
        time.Advance(3);
        Assert.Equal(0, await watcher.ProcessPendingAsync());
        Assert.Single(jobs);
    }

    [Fact]
    public async Task A_file_that_is_still_growing_waits()
    {
        using var dir = new TempDir();
        var (watcher, time, jobs, queue) = Create(new WatchRule { Folder = dir.Path });
        await using var _ = queue;
        await using var __ = watcher;

        var photo = dir.File("photo.jpg", "part");
        watcher.Notice(photo);
        time.Advance(3);
        File.AppendAllText(photo, " and more"); // the download continues
        Assert.Equal(0, await watcher.ProcessPendingAsync());

        time.Advance(3);
        Assert.Equal(1, await watcher.ProcessPendingAsync());
        Assert.Single(jobs);
    }

    [Fact]
    public async Task A_file_another_program_holds_open_waits()
    {
        using var dir = new TempDir();
        var (watcher, time, jobs, queue) = Create(new WatchRule { Folder = dir.Path });
        await using var _ = queue;
        await using var __ = watcher;

        var photo = dir.File("photo.jpg");
        watcher.Notice(photo);
        time.Advance(3);
        using (new FileStream(photo, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            Assert.Equal(0, await watcher.ProcessPendingAsync());

        Assert.Equal(1, await watcher.ProcessPendingAsync());
        Assert.Single(jobs);
    }

    [Fact]
    public async Task A_file_that_holds_no_data_yet_waits_for_it()
    {
        // A program reserved the file's full size and will write later: it keeps its size and time and opens without
        // sharing, but the engine would see zeros ("improper image header", issue #767).
        using var dir = new TempDir();
        var (watcher, time, jobs, queue) = Create(new WatchRule { Folder = dir.Path });
        await using var _ = queue;
        await using var __ = watcher;

        var photo = Path.Combine(dir.Path, "photo.jpg");
        File.WriteAllBytes(photo, new byte[8192]);
        watcher.Notice(photo);
        time.Advance(3);
        Assert.Equal(0, await watcher.ProcessPendingAsync());
        Assert.Empty(jobs);

        // Later, so with a later time: right after the first write the same size can still read the same time.
        var reserved = File.GetLastWriteTimeUtc(photo);
        File.WriteAllBytes(photo, [.. new byte[100], .. new byte[] { 0xFF, 0xD8, 0xFF }, .. new byte[8089]]);
        File.SetLastWriteTimeUtc(photo, reserved.AddSeconds(1));
        time.Advance(3);
        Assert.Equal(0, await watcher.ProcessPendingAsync()); // it changed: the wait starts over
        time.Advance(3);
        Assert.Equal(1, await watcher.ProcessPendingAsync());
        Assert.Equal(FileState.Done, Assert.Single(Assert.Single(jobs).Files).State);
    }

    [Fact]
    public async Task A_file_of_zeros_is_converted_in_the_end_and_an_empty_one_right_away()
    {
        // Neither may wait forever without a word: the conversion says what is wrong with them.
        using var dir = new TempDir();
        var (watcher, time, jobs, queue) = Create(new WatchRule { Folder = dir.Path });
        await using var _ = queue;
        await using var __ = watcher;

        var empty = Path.Combine(dir.Path, "empty.jpg");
        File.WriteAllBytes(empty, []);
        var zeros = Path.Combine(dir.Path, "zeros.jpg");
        File.WriteAllBytes(zeros, new byte[8192]);
        watcher.Notice(empty);
        watcher.Notice(zeros);
        time.Advance(3);
        Assert.Equal(1, await watcher.ProcessPendingAsync());
        Assert.Equal(empty, Assert.Single(Assert.Single(jobs).Files).SourcePath);

        time.Advance(30);
        await watcher.ProcessPendingAsync();
        Assert.Contains(zeros, Assert.Single(jobs.Skip(1)).Files.Select(file => file.SourcePath));
    }

    [Fact]
    public async Task A_file_that_failed_is_tried_once_more()
    {
        // Something kept the file from being read when its turn came, and nothing else looks at it before it changes.
        using var dir = new TempDir();
        var rule = new WatchRule { Folder = dir.Path, Originals = AfterConversion.MoveToOriginals };
        var converter = new FakeConverter("magick", new ConversionEdge("jpg", "png")) { Failure = new InvalidOperationException("improper image header") };
        var (watcher, time, jobs, queue) = Create(rule, converter);
        await using var _ = queue;
        await using var __ = watcher;

        var photo = dir.File("photo.jpg");
        watcher.Notice(photo);
        time.Advance(3);
        Assert.Equal(1, await watcher.ProcessPendingAsync());
        Assert.Equal(FileState.Failed, Assert.Single(Assert.Single(jobs).Files).State);
        Assert.True(File.Exists(photo));

        // Not right away: whatever held it needs a moment.
        time.Advance(3);
        Assert.Equal(0, await watcher.ProcessPendingAsync());

        converter.Failure = null;
        time.Advance(10);
        Assert.Equal(1, await watcher.ProcessPendingAsync());
        Assert.Equal(FileState.Done, Assert.Single(jobs[1].Files).State);
        Assert.True(File.Exists(Path.Combine(rule.OriginalsFolder, "photo.jpg")));
    }

    [Fact]
    public async Task A_file_that_fails_again_is_left_alone_until_it_changes()
    {
        using var dir = new TempDir();
        var converter = new FakeConverter("magick", new ConversionEdge("jpg", "png")) { Failure = new InvalidOperationException("improper image header") };
        var (watcher, time, jobs, queue) = Create(new WatchRule { Folder = dir.Path }, converter);
        await using var _ = queue;
        await using var __ = watcher;

        var photo = dir.File("photo.jpg");
        watcher.Notice(photo);
        time.Advance(3);
        Assert.Equal(1, await watcher.ProcessPendingAsync());
        time.Advance(13);
        Assert.Equal(1, await watcher.ProcessPendingAsync());
        time.Advance(60);
        Assert.Equal(0, await watcher.ProcessPendingAsync());
        watcher.Notice(photo);
        time.Advance(60);
        Assert.Equal(0, await watcher.ProcessPendingAsync());
        Assert.Equal(2, jobs.Count);

        // Saved anew it is new input, with a retry of its own.
        converter.Failure = null;
        File.AppendAllText(photo, " saved again");
        watcher.Notice(photo);
        time.Advance(3);
        Assert.Equal(1, await watcher.ProcessPendingAsync());
        Assert.Equal(FileState.Done, Assert.Single(jobs[2].Files).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_batch_that_could_not_run_is_not_forgotten(bool throws)
    {
        using var dir = new TempDir();
        var calls = new List<IReadOnlyList<string>>();
        var time = new FakeTime();
        await using var watcher = new FolderWatcher(new WatchRule { Folder = dir.Path }, (files, _) =>
        {
            calls.Add(files.ToList());
            if (calls.Count == 1 && throws)
                throw new InvalidOperationException("no window yet");
            return Task.FromResult<ConversionJob?>(null);
        }, settleTime: TimeSpan.FromSeconds(2), time: time);

        var photo = dir.File("photo.jpg");
        watcher.Notice(photo);
        time.Advance(3);
        await watcher.ProcessPendingAsync();
        Assert.Single(calls);

        time.Advance(13);
        await watcher.ProcessPendingAsync();
        Assert.Equal(2, calls.Count);
        Assert.Equal(photo, Assert.Single(calls[1]));

        // The retry is the only one: a preset that is gone must not ask every few seconds for ever.
        time.Advance(60);
        await watcher.ProcessPendingAsync();
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task A_burst_converts_every_file_also_when_some_fail_for_a_moment()
    {
        using var dir = new TempDir();
        var rule = new WatchRule { Folder = dir.Path, Originals = AfterConversion.MoveToOriginals };
        var converter = new FakeConverter("magick", new ConversionEdge("jpg", "png")) { Failure = new InvalidOperationException("improper image header") };
        var (watcher, time, jobs, queue) = Create(rule, converter);
        await using var _ = queue;
        await using var __ = watcher;

        foreach (var name in Enumerable.Range(1, 120).Select(i => $"photo{i:D3}.jpg"))
            watcher.Notice(dir.File(name));
        time.Advance(3);
        Assert.Equal(120, await watcher.ProcessPendingAsync());
        Assert.All(jobs[0].Files, file => Assert.Equal(FileState.Failed, file.State));

        converter.Failure = null;
        time.Advance(13);
        Assert.Equal(120, await watcher.ProcessPendingAsync());
        Assert.All(jobs[1].Files, file => Assert.Equal(FileState.Done, file.State));
        Assert.Equal(120, Directory.GetFiles(rule.OriginalsFolder).Length);
    }

    [Theory]
    [InlineData("photo.jpg.crdownload")]
    [InlineData("~$report.docx")]
    [InlineData("sketch.kra~")] // Krita's backup of the previous save, written next to sketch.kra
    [InlineData("setup.exe")] // programs only when dropped or named (#1188): a downloaded installer is no picture
    [InlineData("plugin.DLL")]
    [InlineData(".hidden.jpg")]
    [InlineData("notes.unknownformat")]
    [InlineData("converted/photo.jpg")]
    [InlineData("originals/photo.jpg")]
    [InlineData("sub/photo.jpg")]
    public void Temporary_unknown_and_own_files_are_ignored(string relative)
    {
        var rule = new WatchRule { Folder = Path.Combine(Path.GetTempPath(), "inbox") };
        Assert.True(FolderWatcher.ShouldIgnore(Path.Combine(rule.Folder, relative), rule));
    }

    [Fact]
    public void Subfolders_count_only_when_the_rule_includes_them()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "sub", "photo.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");

        Assert.True(FolderWatcher.ShouldIgnore(file, new WatchRule { Folder = dir.Path }));
        Assert.False(FolderWatcher.ShouldIgnore(file, new WatchRule { Folder = dir.Path, IncludeSubfolders = true }));
    }

    [Fact]
    public async Task Move_to_originals_converts_what_is_already_there_and_empties_the_folder()
    {
        using var dir = new TempDir();
        var photo = dir.File("photo.jpg");
        var rule = new WatchRule { Folder = dir.Path, Originals = AfterConversion.MoveToOriginals };
        var (watcher, time, _, queue) = Create(rule);
        await using var __ = queue;
        await using var ___ = watcher;

        Assert.True(watcher.Start()); // picks up the file that was already waiting
        time.Advance(3);
        Assert.Equal(1, await watcher.ProcessPendingAsync());

        Assert.False(File.Exists(photo));
        Assert.True(File.Exists(Path.Combine(dir.Path, "originals", "photo.jpg")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "converted", "photo.png")));
    }

    [Fact]
    public async Task The_scan_of_what_is_already_there_does_not_follow_links_out_of_the_folder()
    {
        using var dir = new TempDir();
        using var elsewhere = new TempDir();
        var mine = dir.File("photo.jpg");
        var foreign = elsewhere.File("other.jpg");
        var link = Path.Combine(dir.Path, "linked");
        LinkFolder(link, elsewhere.Path);
        try
        {
            var rule = new WatchRule { Folder = dir.Path, IncludeSubfolders = true, Originals = AfterConversion.MoveToOriginals };
            var (watcher, time, jobs, queue) = Create(rule);
            await using var __ = queue;
            await using var ___ = watcher;

            Assert.True(watcher.Start());
            time.Advance(3);
            Assert.Equal(1, await watcher.ProcessPendingAsync());

            Assert.Equal([mine], Assert.Single(jobs).Files.Select(f => f.SourcePath));
            Assert.True(File.Exists(foreign)); // neither converted nor moved to the originals folder
        }
        finally
        {
            Directory.Delete(link); // the link only
        }
    }

    [Fact]
    public async Task An_arriving_folder_is_read_without_following_links_and_a_link_itself_is_not_read()
    {
        using var dir = new TempDir();
        using var elsewhere = new TempDir();
        var incoming = Directory.CreateDirectory(Path.Combine(dir.Path, "incoming")).FullName;
        var mine = Path.Combine(Directory.CreateDirectory(Path.Combine(incoming, "sub")).FullName, "photo.jpg");
        File.WriteAllText(mine, "bytes");
        elsewhere.File("other.jpg");
        var inner = Path.Combine(incoming, "linked");
        var outer = Path.Combine(dir.Path, "linked");
        LinkFolder(inner, elsewhere.Path);
        LinkFolder(outer, elsewhere.Path);
        try
        {
            var (watcher, time, jobs, queue) = Create(new WatchRule { Folder = dir.Path, IncludeSubfolders = true });
            await using var __ = queue;
            await using var ___ = watcher;

            watcher.Notice(incoming); // a folder moved in at once, with a link inside it
            watcher.Notice(outer);    // a link that appeared in the folder
            time.Advance(3);
            Assert.Equal(1, await watcher.ProcessPendingAsync());

            Assert.Equal([mine], Assert.Single(jobs).Files.Select(f => f.SourcePath));
        }
        finally
        {
            Directory.Delete(inner);
            Directory.Delete(outer);
        }
    }

    [Fact]
    public async Task A_symbolic_link_to_a_file_is_not_converted_but_the_files_beside_it_are()
    {
        using var dir = new TempDir();
        using var elsewhere = new TempDir();
        var mine = dir.File("photo.jpg");
        var foreign = elsewhere.File("other.jpg");
        var link = Path.Combine(dir.Path, "link.jpg");
        try
        {
            File.CreateSymbolicLink(link, foreign);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This machine doesn't let the test make a symbolic link.");
        }
        var rule = new WatchRule { Folder = dir.Path, Originals = AfterConversion.MoveToOriginals };
        Assert.Equal([mine], FolderWatcher.FilesInside(dir.Path, recurse: true));
        Assert.True(FolderWatcher.ShouldIgnore(link, rule));
        var (watcher, time, jobs, queue) = Create(rule);
        await using var __ = queue;
        await using var ___ = watcher;

        Assert.True(watcher.Start()); // the scan of what is there
        watcher.Notice(link);         // and the change event for it
        time.Advance(3);
        Assert.Equal(1, await watcher.ProcessPendingAsync());

        Assert.Equal([mine], Assert.Single(jobs).Files.Select(f => f.SourcePath));
        Assert.True(File.Exists(foreign));
    }

    /// <summary>A junction on Windows (no rights needed), a symbolic link elsewhere; skips when neither can be made.</summary>
    /// <summary>Replaces a file with a symbolic link to <paramref name="target"/>; skips where links can't be made.</summary>
    private static void SwapForLink(string file, string target)
    {
        File.Delete(file);
        try
        {
            File.CreateSymbolicLink(file, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This machine doesn't let the test make a symbolic link.");
        }
    }

    [Fact]
    public async Task A_file_swapped_for_a_link_while_it_waits_is_not_converted()
    {
        using var dir = new TempDir();
        using var elsewhere = new TempDir();
        var foreign = elsewhere.File("other.jpg");
        var photo = dir.File("photo.jpg");
        var (watcher, time, jobs, queue) = Create(new WatchRule { Folder = dir.Path });
        await using var _ = queue;
        await using var __ = watcher;

        watcher.Notice(photo);
        SwapForLink(photo, foreign);
        time.Advance(3);

        Assert.Equal(0, await watcher.ProcessPendingAsync());
        time.Advance(3);
        Assert.Equal(0, await watcher.ProcessPendingAsync());
        Assert.Empty(jobs);
    }

    [Fact]
    public async Task A_file_swapped_for_a_link_before_its_second_turn_is_not_converted()
    {
        using var dir = new TempDir();
        using var elsewhere = new TempDir();
        var foreign = elsewhere.File("other.jpg");
        var photo = dir.File("photo.jpg");
        var (watcher, time, jobs, queue) = Create(new WatchRule { Folder = dir.Path }, new FakeConverter("magick", new ConversionEdge("jpg", "png")) { Failure = new InvalidOperationException("no") });
        await using var _ = queue;
        await using var __ = watcher;

        watcher.Notice(photo);
        time.Advance(3);
        Assert.Equal(1, await watcher.ProcessPendingAsync());
        Assert.Equal(FileState.Failed, Assert.Single(Assert.Single(jobs).Files).State);

        SwapForLink(photo, foreign);
        time.Advance(30);

        Assert.Equal(0, await watcher.ProcessPendingAsync());
        Assert.Single(jobs);
    }

    private static void LinkFolder(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Assert.Skip("This machine doesn't let the test make a symbolic link.");
            }
            return;
        }
        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        mklink.WaitForExit();
        Assert.SkipUnless(mklink.ExitCode == 0, "This drive can't hold a junction.");
    }

    [Fact]
    public async Task A_missing_folder_is_reported_instead_of_watched()
    {
        var (watcher, _, _, queue) = Create(new WatchRule { Folder = Path.Combine(Path.GetTempPath(), "filee-missing-" + Guid.NewGuid().ToString("N")) });
        await using var _ = queue;
        await using var __ = watcher;

        Assert.False(watcher.Start());
        Assert.NotNull(watcher.Error);
    }

    [Fact]
    public async Task Case_distinct_files_with_identical_metadata_are_both_converted()
    {
        using var dir = new TempDir();
        var lower = dir.File("photo.jpg", "one");
        var upper = Path.Combine(dir.Path, "PHOTO.jpg");
        if (File.Exists(upper))
        {
            Assert.Skip("The temporary volume is case-insensitive.");
            return;
        }
        File.WriteAllText(upper, "two");
        File.SetLastWriteTimeUtc(upper, File.GetLastWriteTimeUtc(lower));
        var (watcher, time, jobs, queue) = Create(new WatchRule { Folder = dir.Path });
        await using var queueLifetime = queue;
        await using var watcherLifetime = watcher;

        watcher.Notice(lower);
        watcher.Notice(upper);
        time.Advance(3);

        Assert.Equal(2, await watcher.ProcessPendingAsync());
        var job = Assert.Single(jobs);
        Assert.Equal(2, job.Files.Count);
        Assert.Equal(2, job.Outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var result in job.Files)
        {
            Assert.Equal(FileState.Done, result.State);
            Assert.Contains(Path.GetFileName(result.SourcePath), File.ReadAllText(Assert.Single(result.Outputs)));
        }

        watcher.Notice(lower);
        watcher.Notice(upper);
        time.Advance(3);
        Assert.Equal(0, await watcher.ProcessPendingAsync());
        Assert.Single(jobs);
    }

    [Fact]
    public void A_case_distinct_folder_is_not_mistaken_for_the_output_folder()
    {
        using var dir = new TempDir();
        var rule = new WatchRule { Folder = dir.Path, IncludeSubfolders = true };
        Directory.CreateDirectory(rule.ResolvedOutputFolder);
        var alternate = Path.Combine(dir.Path, "Converted");
        if (Directory.Exists(alternate))
        {
            Assert.Skip("The temporary volume is case-insensitive.");
            return;
        }
        Directory.CreateDirectory(alternate);
        var input = Path.Combine(alternate, "photo.jpg");
        var output = Path.Combine(rule.ResolvedOutputFolder, "photo.jpg");
        File.WriteAllText(input, "input");
        File.WriteAllText(output, "output");

        Assert.False(FolderWatcher.ShouldIgnore(input, rule));
        Assert.True(FolderWatcher.ShouldIgnore(output, rule));
    }
}
