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
    private static (FolderWatcher Watcher, FakeTime Time, List<ConversionJob> Jobs, JobQueue Queue) Create(WatchRule rule)
    {
        var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "png"))]));
        var jobs = new List<ConversionJob>();
        var time = new FakeTime();
        var watcher = new FolderWatcher(rule, async (files, _) =>
        {
            var preset = new Preset { TargetFormat = "png", Output = { Location = OutputLocation.CustomFolder, CustomFolder = rule.ResolvedOutputFolder } };
            var job = new ConversionJob(files, preset, "Test");
            await queue.RunAsync(job);
            jobs.Add(job);
            return job;
        }, time: time);
        return (watcher, time, jobs, queue);
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

    [Theory]
    [InlineData("photo.jpg.crdownload")]
    [InlineData("~$report.docx")]
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
    public async Task A_missing_folder_is_reported_instead_of_watched()
    {
        var (watcher, _, _, queue) = Create(new WatchRule { Folder = Path.Combine(Path.GetTempPath(), "filee-missing-" + Guid.NewGuid().ToString("N")) });
        await using var _ = queue;
        await using var __ = watcher;

        Assert.False(watcher.Start());
        Assert.NotNull(watcher.Error);
    }
}
