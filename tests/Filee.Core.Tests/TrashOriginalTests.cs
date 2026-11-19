// "Move the original to the Recycle Bin / Trash after converting" (#1387): the original goes once its result in another
// format is complete, or stays, with a note saying why.

using System.Text.Json;
using Filee.Core.Conversion;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Core.Watching;

namespace Filee.Core.Tests;

public class TrashOriginalTests
{
    private static Preset Trashing(string target = "png") =>
        new() { TargetFormat = target, Output = { TrashOriginalAfterConvert = true } };

    /// <summary>Moves files into a folder, or refuses with <paramref name="refuse"/>.</summary>
    private sealed class FakeTrash(string folder, TrashRefusal? refuse = null)
    {
        public List<string> Moved { get; } = [];

        public TrashRefusal? Move(string path)
        {
            if (refuse is not null)
                return refuse;
            File.Move(path, Path.Combine(folder, Path.GetFileName(path)));
            Moved.Add(path);
            return null;
        }
    }

    private static readonly ConversionEdge[] Edges =
        [new ConversionEdge("jpg", "png"), new ConversionEdge("jpg", "jpg"), new ConversionEdge("docx", "pdf")];

    private static async Task<ConversionJob> Run(FakeTrash? trash, string source, Preset preset, ISourceFetcher? fetcher = null)
    {
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("fake", Edges)]), moveToTrash: trash is null ? null : trash.Move);
        var job = new ConversionJob([source], preset, "Test", fetcher);
        await queue.RunAsync(job);
        return job;
    }

    [Fact]
    public async Task The_original_goes_to_the_trash_after_a_conversion_to_another_format()
    {
        using var dir = new TempDir();
        using var bin = new TempDir();
        var source = dir.File("report.docx");
        var trash = new FakeTrash(bin.Path);

        var job = await Run(trash, source, Trashing("pdf"));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal([source], trash.Moved);
        Assert.Equal([Path.Combine(dir.Path, "report.pdf")], job.Outputs);
        Assert.True(File.Exists(Path.Combine(dir.Path, "report.pdf")));
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(Path.Combine(bin.Path, "report.docx")));
        Assert.Null(job.Files[0].NoticeKey);
        Assert.True(job.Files[0].OriginalInTrash);
    }

    [Fact]
    public async Task Off_by_default()
    {
        using var dir = new TempDir();
        using var bin = new TempDir();
        var source = dir.File("report.docx");
        var trash = new FakeTrash(bin.Path);

        await Run(trash, source, new Preset { TargetFormat = "pdf" });

        Assert.False(new OutputRule().TrashOriginalAfterConvert);
        Assert.Empty(trash.Moved);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task A_conversion_that_fails_keeps_the_original()
    {
        using var dir = new TempDir();
        using var bin = new TempDir();
        var source = dir.File("report.docx");
        var trash = new FakeTrash(bin.Path);
        await using var queue = new JobQueue(
            new ConverterCatalog([new FakeConverter("fake", new ConversionEdge("docx", "pdf")) { Throw = true }]), moveToTrash: trash.Move);
        var job = new ConversionJob([source], Trashing("pdf"), "Test");

        await queue.RunAsync(job);

        Assert.Empty(trash.Moved);
        Assert.True(File.Exists(source));
        Assert.Equal(FileState.Failed, job.Files[0].State);
    }

    [Fact]
    public async Task A_cancelled_conversion_keeps_the_original()
    {
        using var dir = new TempDir();
        using var bin = new TempDir();
        var source = dir.File("report.docx");
        var trash = new FakeTrash(bin.Path);
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("fake", new ConversionEdge("docx", "pdf"))]), moveToTrash: trash.Move);
        var job = new ConversionJob([source], Trashing("pdf"), "Test");
        job.Cancel(); // before it runs, so nothing converts
        await queue.RunAsync(job);

        Assert.Empty(trash.Moved);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task When_the_trash_refuses_the_original_stays_with_the_reason()
    {
        using var dir = new TempDir();
        var source = dir.File("report.docx");

        var job = await Run(new FakeTrash(dir.Path, new TrashRefusal("trash.in_use")), source, Trashing("pdf"));

        Assert.Equal(JobState.Completed, job.State);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(Path.Combine(dir.Path, "report.pdf")));
        Assert.Equal("notice.trash_original_kept", job.Files[0].NoticeKey);
        Assert.Equal("trash.in_use", job.Files[0].NoticeReasonKey);
        Assert.False(job.Files[0].OriginalInTrash);
    }

    [Fact]
    public async Task Without_a_trash_the_original_stays()
    {
        using var dir = new TempDir();
        var source = dir.File("report.docx");

        var job = await Run(null, source, Trashing("pdf"));

        Assert.True(File.Exists(source));
        Assert.Equal("notice.trash_original_kept", job.Files[0].NoticeKey);
        Assert.Equal("trash.no_recycle_bin", job.Files[0].NoticeReasonKey);
    }

    [Fact]
    public async Task Files_from_a_browser_keep_their_original()
    {
        using var dir = new TempDir();
        using var bin = new TempDir();
        var source = dir.File("a.jpg");
        var trash = new FakeTrash(bin.Path);

        await Run(trash, source, Trashing(), new LocalFetcher());

        Assert.Empty(trash.Moved);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task A_result_in_the_same_format_is_left_to_replace_the_original()
    {
        using var dir = new TempDir();
        using var bin = new TempDir();
        var source = dir.File("a.jpg");
        var trash = new FakeTrash(bin.Path);

        var job = await Run(trash, source, Trashing("jpg"));

        Assert.Empty(trash.Moved);
        Assert.True(File.Exists(source));
        Assert.Equal(2, Directory.GetFiles(dir.Path).Length); // "a.jpg" and the result beside it
        Assert.Null(job.Files[0].NoticeKey);
    }

    [Fact]
    public async Task With_both_options_the_format_decides_which_one_applies()
    {
        using var dir = new TempDir();
        using var bin = new TempDir();
        var trash = new FakeTrash(bin.Path);
        var sameFormat = new Preset { TargetFormat = "jpg", Output = { ReplaceOriginal = true, TrashOriginalAfterConvert = true } };
        var otherFormat = new Preset { TargetFormat = "png", Output = { ReplaceOriginal = true, TrashOriginalAfterConvert = true } };

        // The same format: "Replace the original" does it, the result takes the original's place.
        var same = dir.File("a.jpg");
        var replaced = await Run(trash, same, sameFormat);
        Assert.Equal([same], trash.Moved);
        Assert.Equal([same], replaced.Outputs);
        Assert.Equal("a.jpg→jpg by fake", File.ReadAllText(same));

        // Another format: the new option does it, and the result keeps its own name.
        var other = dir.File("b.jpg");
        var converted = await Run(trash, other, otherFormat);
        Assert.Equal([same, other], trash.Moved);
        Assert.Equal([Path.Combine(dir.Path, "b.png")], converted.Outputs);
        Assert.False(File.Exists(other));
    }

    [Fact]
    public async Task Only_if_smaller_keeps_the_original()
    {
        using var dir = new TempDir();
        using var bin = new TempDir();
        var source = dir.File("a.jpg");
        var trash = new FakeTrash(bin.Path);
        var preset = Trashing("jpg");
        preset.Output.OnlyIfSmaller = true;

        await Run(trash, source, preset); // the fake result is larger than "x", so the source is copied as the result

        Assert.Empty(trash.Moved);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task A_result_packed_into_a_zip_keeps_the_original()
    {
        using var dir = new TempDir();
        using var bin = new TempDir();
        var source = dir.File("a.jpg");
        var trash = new FakeTrash(bin.Path);
        var preset = Trashing();
        preset.Output.ZipResult = true;

        var job = await Run(trash, source, preset);

        Assert.EndsWith(".zip", Assert.Single(job.Outputs));
        Assert.Empty(trash.Moved);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task Merged_pdfs_keep_their_originals()
    {
        using var dir = new TempDir();
        using var bin = new TempDir();
        var first = dir.File("1.jpg");
        var second = dir.File("2.jpg");
        var trash = new FakeTrash(bin.Path);
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("fake", new ConversionEdge("jpg", "pdf"))]), new FakeMerger(),
            moveToTrash: trash.Move);
        var preset = new Preset { TargetFormat = "pdf", Pdf = { MergeIntoSingle = true }, Output = { TrashOriginalAfterConvert = true } };
        var job = new ConversionJob([first, second], preset, "Test");

        await queue.RunAsync(job);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Empty(trash.Moved);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public void Watch_folders_and_asked_locations_clear_it_and_leave_the_preset_as_it_was()
    {
        var preset = Trashing();

        Assert.False(new WatchRule { Folder = "in", OutputFolder = "out" }.Apply(preset).Output.TrashOriginalAfterConvert);
        Assert.False(AskedLocation.ForFolder(preset, "somewhere").Output.TrashOriginalAfterConvert);
        Assert.False(AskedLocation.ForFile(preset, Path.Combine("somewhere", "a.png")).Output.TrashOriginalAfterConvert);
        Assert.True(preset.Output.TrashOriginalAfterConvert);
    }

    [Fact]
    public void An_export_leaves_it_out_and_keeps_it_on_this_pcs_preset()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        var mine = store.Presets.First(p => !p.IsGroup && !p.IsTool);
        mine.Output.TrashOriginalAfterConvert = true;
        var bundle = Path.Combine(dir.Path, "bundle.json");

        store.Export(bundle);

        Assert.True(mine.Output.TrashOriginalAfterConvert);
        Assert.False(mine.ForSharing().Output.TrashOriginalAfterConvert);
        using var other = new TempDir();
        var target = new UserDataStore(other.Path);
        target.Load();
        target.Import(bundle);
        Assert.All(target.Presets, p => Assert.False(p.Output.TrashOriginalAfterConvert));
        Assert.DoesNotContain("\"trashOriginalAfterConvert\":true", File.ReadAllText(bundle).Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_imported_bundle_never_turns_it_on_not_even_for_a_preset_it_replaces()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        var builtIn = store.Presets.First(p => !p.IsGroup && !p.IsTool);
        var theirs = builtIn.Clone();
        theirs.Output.TrashOriginalAfterConvert = true;
        var added = new Preset { Id = "from-a-friend", Name = "From a friend", TargetFormat = "png", Output = { TrashOriginalAfterConvert = true } };
        var bundle = Path.Combine(dir.Path, "bundle.json");
        File.WriteAllText(bundle, JsonSerializer.Serialize(new PresetBundle { Presets = [theirs, added] }, FileeJsonContext.Default.PresetBundle));

        store.Import(bundle);

        Assert.False(store.Presets.Single(p => p.Id == builtIn.Id).Output.TrashOriginalAfterConvert);
        Assert.False(store.Presets.Single(p => p.Id == "from-a-friend").Output.TrashOriginalAfterConvert);
    }

    [Fact]
    public void Survives_saving_and_loading_the_library()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        var mine = store.Presets.First(p => !p.IsGroup && !p.IsTool);
        Assert.False(mine.Output.TrashOriginalAfterConvert);
        mine.Output.TrashOriginalAfterConvert = true;
        store.SaveLibrary();

        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();

        Assert.True(reloaded.Presets.Single(p => p.Id == mine.Id).Output.TrashOriginalAfterConvert);
        Assert.All(reloaded.Presets.Where(p => p.Id != mine.Id), p => Assert.False(p.Output.TrashOriginalAfterConvert));
    }

    [Fact]
    public async Task An_extracted_archive_goes_to_the_trash()
    {
        // Extract's result is a folder: the option promises "after converting", so the archive goes once it is out.
        using var dir = new TempDir();
        using var bin = new TempDir();
        var archive = dir.File("photos.zip");
        var trash = new FakeTrash(bin.Path);
        await using var queue = new JobQueue(
            new ConverterCatalog([new FakeConverter("fake", new ConversionEdge("zip", "folder")) { OutputIsFolder = true }]), moveToTrash: trash.Move);
        var job = new ConversionJob([archive], Trashing("folder"), "Extract");

        await queue.RunAsync(job);

        Assert.Equal(JobState.Completed, job.State);
        Assert.True(Directory.Exists(Assert.Single(job.Outputs)));
        Assert.Equal([archive], trash.Moved);
        Assert.False(File.Exists(archive));
        Assert.True(File.Exists(Path.Combine(bin.Path, "photos.zip")));
    }

    [Fact]
    public async Task A_video_saved_as_a_folder_of_frames_goes_to_the_trash()
    {
        using var dir = new TempDir();
        using var bin = new TempDir();
        var clip = dir.File("clip.mp4");
        var trash = new FakeTrash(bin.Path);
        await using var queue = new JobQueue(
            new ConverterCatalog([new FakeConverter("fake", new ConversionEdge("mp4", "jpg")) { OutputIsFolder = true }]), moveToTrash: trash.Move);
        var job = new ConversionJob([clip], Trashing("jpg"), "Frames");

        await queue.RunAsync(job);

        Assert.True(Directory.Exists(Assert.Single(job.Outputs)));
        Assert.Equal([clip], trash.Moved);
    }

    [Fact]
    public async Task A_source_another_job_still_has_to_convert_is_kept_until_that_one_is_done()
    {
        // The same file dropped on two presets in a row: the first must not take it away from the second.
        using var dir = new TempDir();
        using var bin = new TempDir();
        var source = dir.File("report.docx");
        var trash = new FakeTrash(bin.Path);
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("fake", new ConversionEdge("docx", "pdf"))]), moveToTrash: trash.Move);
        var finished = new TaskCompletionSource();
        var count = 0;
        queue.JobFinished += (_, _) =>
        {
            if (Interlocked.Increment(ref count) == 2)
                finished.TrySetResult();
        };

        var first = queue.Enqueue([source], Trashing("pdf"), "First");
        var second = queue.Enqueue([source], Trashing("pdf"), "Second");
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);


        // Both converted. The first left the source for the second, which was the last one to need it.
        Assert.Equal(FileState.Done, first.Files[0].State);
        Assert.Equal(FileState.Done, second.Files[0].State);
        Assert.Equal("notice.trash_original_busy", first.Files[0].NoticeKey);
        Assert.False(first.Files[0].OriginalInTrash);
        Assert.True(second.Files[0].OriginalInTrash);
        Assert.Equal([source], trash.Moved);
    }

    /// <summary>A browser drop whose files are already on disk.</summary>
    private sealed class LocalFetcher : ISourceFetcher
    {
        public bool Fetches(string source) => false;
        public Task<string> FetchAsync(string source, IProgress<TransferProgress> progress, CancellationToken cancellationToken) => Task.FromResult(source);
        public void Release(ConversionJob job) { }
    }
}
