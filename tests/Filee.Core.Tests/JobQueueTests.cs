using Filee.Core.Conversion;
using Filee.Core.Presets;

namespace Filee.Core.Tests;

public class JobQueueTests
{
    private static async Task<ConversionJob> Run(JobQueue queue, IReadOnlyList<string> sources, Preset preset)
    {
        var job = new ConversionJob(sources, preset, "Test");
        await queue.RunAsync(job);
        return job;
    }

    [Fact]
    public async Task Converts_every_file()
    {
        using var dir = new TempDir();
        var a = dir.File("a.jpg");
        var b = dir.File("b.jpg");
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "png"))]));

        var job = await Run(queue, [a, b], new Preset { TargetFormat = "png" });

        Assert.Equal(JobState.Completed, job.State);
        Assert.True(File.Exists(Path.Combine(dir.Path, "a.png")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "b.png")));
        Assert.Equal(1, job.Progress);
    }

    [Fact]
    public async Task Multi_step_route_keeps_intermediates_out_of_the_output_folder()
    {
        using var dir = new TempDir();
        var source = dir.File("doc.hwpx");
        var catalog = new ConverterCatalog([
            new FakeConverter("rhwp", new ConversionEdge("hwpx", "pdf")),
            new FakeConverter("pdfium", new ConversionEdge("pdf", "png")) { OutputsPerInput = 2 },
        ]);
        await using var queue = new JobQueue(catalog);

        var job = await Run(queue, [source], new Preset { TargetFormat = "png" });

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(["doc.hwpx", "doc_p1.png", "doc_p2.png"], Directory.GetFiles(dir.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Failures_are_isolated_per_file()
    {
        using var dir = new TempDir();
        var ok = dir.File("a.jpg");
        var unsupported = dir.File("b.xyz");
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "png"))]));

        var job = await Run(queue, [ok, unsupported], new Preset { TargetFormat = "png" });

        Assert.Equal(JobState.CompletedWithErrors, job.State);
        Assert.Equal("error.unsupported_source", job.Files[1].ErrorKey);
    }

    [Fact]
    public async Task No_route_is_reported()
    {
        using var dir = new TempDir();
        await using var queue = new JobQueue(new ConverterCatalog([]));

        var job = await Run(queue, [dir.File("a.docx")], new Preset { TargetFormat = "hwpx" });

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("error.no_route", job.Files[0].ErrorKey);
    }

    [Fact]
    public async Task Engine_exceptions_mark_the_file_failed()
    {
        using var dir = new TempDir();
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("x", new ConversionEdge("jpg", "png")) { Throw = true }]));

        var job = await Run(queue, [dir.File("a.jpg")], new Preset { TargetFormat = "png" });

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("error.conversion_failed", job.Files[0].ErrorKey);
    }

    [Fact]
    public async Task Same_as_source_target_reencodes_in_place_format()
    {
        using var dir = new TempDir();
        var source = dir.File("a.jpg");
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "jpg"))]));

        var job = await Run(queue, [source], new Preset { TargetFormat = BuiltInData.SameAsSource, Output = { FileNamePattern = "{name}_50" } });

        Assert.Equal(JobState.Completed, job.State);
        Assert.True(File.Exists(Path.Combine(dir.Path, "a_50.jpg")));
        Assert.Equal("x", File.ReadAllText(source)); // source untouched
    }

    [Fact]
    public async Task Merge_converts_to_pdf_then_merges_in_order()
    {
        using var dir = new TempDir();
        var first = dir.File("1.jpg");
        var second = dir.File("2.pdf");
        var third = dir.File("3.png");
        var merger = new FakeMerger();
        var catalog = new ConverterCatalog([new FakeConverter("pdfsharp", new ConversionEdge("jpg", "pdf"), new ConversionEdge("png", "pdf"))]);
        await using var queue = new JobQueue(catalog, merger);

        var job = await Run(queue, [first, second, third],
            new Preset { TargetFormat = "pdf", Pdf = { MergeIntoSingle = true }, Output = { FileNamePattern = "{name}_merged" } });

        Assert.Equal(JobState.Completed, job.State);
        var merged = Path.Combine(dir.Path, "1_merged.pdf");
        Assert.Equal("1.pdf|2.pdf|3.pdf", File.ReadAllText(merged));
        Assert.Equal(second, merger.Inputs[1]); // existing PDFs are merged as-is
    }

    [Fact]
    public async Task Combine_packs_every_file_as_is_even_one_or_of_unknown_type()
    {
        using var dir = new TempDir();
        var a = dir.File("report.docx");
        var b = dir.File("data.xyz");
        var combiner = new FakeCombiner();
        // No converter at all: combining must not need a route.
        await using var queue = new JobQueue(new ConverterCatalog([]), combiner: combiner);
        var preset = new Preset { TargetFormat = "zip", Archive = { CombineIntoOne = true }, Output = { FileNamePattern = "{name}_files" } };

        var job = await Run(queue, [a, b], preset);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal([a, b], combiner.Inputs);
        Assert.Equal(Path.Combine(dir.Path, "report_files.zip"), job.Outputs.Single());
        Assert.All(job.Files, f => Assert.Equal(FileState.Done, f.State));

        var single = await Run(queue, [b], preset);
        Assert.Equal(Path.Combine(dir.Path, "data_files.zip"), single.Outputs.Single());
    }

    [Fact]
    public async Task Combine_uses_the_two_part_extension_and_reports_failures_for_every_file()
    {
        using var dir = new TempDir();
        var a = dir.File("a.txt");
        await using var queue = new JobQueue(new ConverterCatalog([]), combiner: new FakeCombiner());

        var job = await Run(queue, [a], new Preset { TargetFormat = "tgz", Archive = { CombineIntoOne = true } });
        Assert.Equal(Path.Combine(dir.Path, "a.tar.gz"), job.Outputs.Single());

        await using var failing = new JobQueue(new ConverterCatalog([]), combiner: new FakeCombiner { Throw = true });
        var failed = await Run(failing, [a, dir.File("b.txt")], new Preset { TargetFormat = "zip", Archive = { CombineIntoOne = true } });
        Assert.Equal(JobState.Failed, failed.State);
        Assert.All(failed.Files, f => Assert.Equal("boom", f.ErrorDetail));
    }

    [Fact]
    public async Task Without_combine_archive_targets_convert_file_by_file()
    {
        using var dir = new TempDir();
        var combiner = new FakeCombiner();
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("archive", new ConversionEdge("rar", "zip"))]), combiner: combiner);

        var job = await Run(queue, [dir.File("a.rar"), dir.File("b.rar")], new Preset { TargetFormat = "zip" });

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(2, job.Outputs.Count());
        Assert.Empty(combiner.Inputs);
    }

    [Fact]
    public async Task Parallel_files_with_the_same_name_do_not_collide()
    {
        using var dir = new TempDir();
        var a = dir.File("same.jpg");
        var b = dir.File("same.png");
        var catalog = new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "webp"), new ConversionEdge("png", "webp"))]);
        await using var queue = new JobQueue(catalog);

        var job = await Run(queue, [a, b], new Preset { TargetFormat = "webp" });

        Assert.Equal(2, job.Outputs.Distinct().Count());
    }

    [Fact]
    public async Task Enqueue_runs_in_background_and_raises_finished()
    {
        using var dir = new TempDir();
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "png"))]));
        var finished = new TaskCompletionSource<ConversionJob>();
        queue.JobFinished += (_, job) => finished.TrySetResult(job);

        queue.Enqueue([dir.File("a.jpg")], new Preset { TargetFormat = "png" }, "PNG");
        var done = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(JobState.Completed, done.State);
    }
}
