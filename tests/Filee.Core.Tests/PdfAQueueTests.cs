// PDF/A (#1367): JobQueue has every PDF a PDF/A preset writes made PDF/A after the finishing, whichever engine wrote it,
// and a merged PDF once. Without the archiver (no Ghostscript) the PDF stays as it is and the file says so.

using Filee.Core.Conversion;
using Filee.Core.Presets;

namespace Filee.Core.Tests;

public class PdfAQueueTests
{
    /// <summary>Converts like <see cref="FakeConverter"/>, finishes PDFs and records the order of both after-steps.</summary>
    private sealed class FakeFinisher(List<string> log, params ConversionEdge[] edges) : IConverter, IPdfFinisher
    {
        private readonly FakeConverter _writer = new("pdfsharp", edges);

        public string Id => "pdfsharp";
        public string DisplayName => "PDFsharp";
        public IReadOnlyList<ConversionEdge> Edges => edges;
        public int MaxParallelism => 0;
        public EngineStatus GetStatus() => EngineStatus.Available();

        public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
            _writer.ConvertAsync(step, progress, cancellationToken);

        public Task FinishAsync(string path, PdfOptions options, CancellationToken cancellationToken)
        {
            lock (log)
                log.Add("finish " + Path.GetFileName(path));
            return Task.CompletedTask;
        }
    }

    /// <summary>Stands in for Ghostscript: records what it was asked to archive and whether it could.</summary>
    private sealed class FakeArchiver(List<string> log, bool installed = true) : IConverter, IPdfArchiver
    {
        public string Id => "ghostscript";
        public string DisplayName => "Ghostscript";
        public IReadOnlyList<ConversionEdge> Edges => [];
        public int MaxParallelism => 0;
        public EngineStatus GetStatus() => EngineStatus.Available();

        public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> ArchiveAsync(string path, bool changed, string workDirectory, CancellationToken cancellationToken)
        {
            lock (log)
                log.Add($"archive {Path.GetFileName(path)}{(changed ? " changed" : "")}");
            return Task.FromResult(installed);
        }
    }

    private static Preset PdfA(Action<PdfOptions>? pdf = null)
    {
        var preset = new Preset { TargetFormat = "pdf", Document = { PdfA = true }, Output = { FileNamePattern = "{name}_pdfa" } };
        pdf?.Invoke(preset.Pdf);
        return preset;
    }

    [Fact]
    public async Task Every_pdf_of_a_pdf_a_job_is_archived_whichever_engine_wrote_it()
    {
        using var dir = new TempDir();
        var log = new List<string>();
        var office = new FakeConverter("ooxml", new ConversionEdge("docx", "pdf"));
        var copier = new FakeConverter("pdfsharp", new ConversionEdge("pdf", "pdf"));
        await using var queue = new JobQueue(new ConverterCatalog([office, copier, new FakeArchiver(log)]));

        var job = new ConversionJob([dir.File("report.docx"), dir.File("scan.pdf")], PdfA(), "Test");
        await queue.RunAsync(job);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(["archive report_pdfa.pdf", "archive scan_pdfa.pdf"], log.Order());
        Assert.All(job.Files, f => Assert.Null(f.NoticeKey));
    }

    [Fact]
    public async Task Nothing_is_archived_without_the_box_or_for_other_targets()
    {
        using var dir = new TempDir();
        var log = new List<string>();
        var writer = new FakeConverter("ooxml", new ConversionEdge("docx", "pdf"), new ConversionEdge("docx", "png"));
        await using var queue = new JobQueue(new ConverterCatalog([writer, new FakeArchiver(log)]));

        await queue.RunAsync(new ConversionJob([dir.File("a.docx")], new Preset { TargetFormat = "pdf" }, "Test"));
        var png = new Preset { TargetFormat = "png", Document = { PdfA = true } };
        await queue.RunAsync(new ConversionJob([dir.File("b.docx")], png, "Test"));

        Assert.Empty(log);
    }

    [Fact]
    public async Task It_comes_after_the_finishing_which_counts_as_a_change()
    {
        using var dir = new TempDir();
        var log = new List<string>();
        var finisher = new FakeFinisher(log, new ConversionEdge("jpg", "pdf"));
        await using var queue = new JobQueue(new ConverterCatalog([finisher, new FakeArchiver(log)]));

        var job = new ConversionJob([dir.File("scan.jpg")], PdfA(pdf => pdf.Watermark = "DRAFT"), "Test");
        await queue.RunAsync(job);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(["finish scan_pdfa.pdf", "archive scan_pdfa.pdf changed"], log);
    }

    [Fact]
    public async Task Without_ghostscript_the_pdf_is_kept_and_the_file_says_it_is_no_pdf_a()
    {
        using var dir = new TempDir();
        var log = new List<string>();
        var office = new FakeConverter("ooxml", new ConversionEdge("docx", "pdf"));

        await using (var queue = new JobQueue(new ConverterCatalog([office, new FakeArchiver(log, installed: false)])))
        {
            var job = new ConversionJob([dir.File("report.docx")], PdfA(), "Test");
            await queue.RunAsync(job);

            Assert.Equal(JobState.Completed, job.State);
            Assert.True(File.Exists(job.Outputs.Single()));
            Assert.Equal("notice.pdfa_needs_ghostscript", job.Files.Single().NoticeKey);
        }

        // No archiver registered at all (a build without the engine): the same.
        await using (var queue = new JobQueue(new ConverterCatalog([office])))
        {
            var job = new ConversionJob([dir.File("memo.docx")], PdfA(), "Test");
            await queue.RunAsync(job);

            Assert.Equal(JobState.Completed, job.State);
            Assert.Equal("notice.pdfa_needs_ghostscript", job.Files.Single().NoticeKey);
        }
    }

    [Fact]
    public async Task A_merged_pdf_is_archived_once_after_merging()
    {
        using var dir = new TempDir();
        var log = new List<string>();
        var writer = new FakeConverter("pdfsharp", new ConversionEdge("jpg", "pdf"));
        await using var queue = new JobQueue(new ConverterCatalog([writer, new FakeArchiver(log)]), new FakeMerger());

        var job = new ConversionJob([dir.File("1.jpg"), dir.File("2.jpg")], PdfA(pdf => pdf.MergeIntoSingle = true), "Test");
        await queue.RunAsync(job);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal([$"archive {Path.GetFileName(job.Outputs.Single())} changed"], log);
    }
}
