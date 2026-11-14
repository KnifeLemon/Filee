// "Move the original to the Recycle Bin / Trash after converting" (#1387): the original goes once its result in another
// format is complete, or stays, with a note saying why.

using Filee.Core.Conversion;
using Filee.Core.Platform;
using Filee.Core.Presets;
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
    public void Survives_a_preset_round_trip()
    {
        Assert.True(Trashing().Clone().Output.TrashOriginalAfterConvert);
    }

    /// <summary>A browser drop whose files are already on disk.</summary>
    private sealed class LocalFetcher : ISourceFetcher
    {
        public bool Fetches(string source) => false;
        public Task<string> FetchAsync(string source, IProgress<TransferProgress> progress, CancellationToken cancellationToken) => Task.FromResult(source);
        public void Release(ConversionJob job) { }
    }
}
