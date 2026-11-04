// Signed envelopes (.p7m, #1010): a read-only document format converted as the file inside it. The route opens the
// envelope to the format the engine finds in it (IEnvelopeOpener) and goes on from there; the outputs are named after
// the file inside.

using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class EnvelopeTests
{
    /// <summary>Opens .p7m files to the format its test says is inside, like the real engine writes the inner file.</summary>
    private sealed class FakeOpener(string inner, params string[] formats) : IConverter, IEnvelopeOpener
    {
        private readonly FakeConverter _writer = new("p7m", [.. formats.Select(f => new ConversionEdge("p7m", f))]);
        public string Id => "p7m";
        public string DisplayName => "p7m";
        public IReadOnlyList<ConversionEdge> Edges => _writer.Edges;
        public int MaxParallelism => 0;
        public EngineStatus GetStatus() => EngineStatus.Available();
        public bool Opens(string format) => format == "p7m";

        public string InnerFormat(string path, string format) =>
            inner == "refuse" ? throw new ConversionRefusedException("error.p7m_detached") : inner;

        public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
            _writer.ConvertAsync(step, progress, cancellationToken);
    }

    [Theory]
    [InlineData("invoice.pdf.p7m", "invoice")]
    [InlineData("IT01234567890_FPA01.xml.P7M", "IT01234567890_FPA01")]
    [InlineData("contract.pdf.p7m.p7m", "contract")]
    [InlineData("signed.p7m", "signed")]
    [InlineData("report.final.p7m", "report.final")]
    [InlineData("backup.tar.gz", "backup")]
    [InlineData("photo.jpg", "photo")]
    public void An_envelope_is_named_after_the_file_inside(string file, string name) =>
        Assert.Equal(name, FormatRegistry.NameWithoutExtension(file));

    [Fact]
    public void P7m_is_a_read_only_document_format_on_the_documents_donut()
    {
        var format = FormatRegistry.Detect("invoice.pdf.p7m");

        Assert.NotNull(format);
        Assert.Equal(("p7m", FormatCategory.Document, false), (format.Id, format.Category, format.Writable));
        Assert.Equal("office", ProfileSelector.Select(BuiltInData.CreateProfiles(), ["invoice.xml.p7m"])?.Id);
    }

    [Fact]
    public void Version_66_libraries_learn_p7m_once()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var office = profiles.Single(p => p.Id == "office");
        office.Extensions.Remove("p7m");

        Assert.True(SettingsMigrations.ApplyToLibrary(65, presets, profiles));

        Assert.Contains("p7m", office.Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(65, presets, profiles));
    }

    [Fact]
    public void The_route_opens_the_envelope_to_the_format_inside_then_goes_on()
    {
        var opener = new FakeOpener("pdf", "pdf", "xml", "png");
        var pdfium = new FakeConverter("pdfium", new ConversionEdge("pdf", "png"));
        var planner = new RoutePlanner([opener, pdfium]);

        var route = planner.PlanThrough("p7m", "pdf", "png");

        Assert.NotNull(route);
        Assert.Equal([("p7m", "pdf"), ("pdf", "png")], route.Steps.Select(s => (s.From, s.To)));
        Assert.Equal(["p7m", "pdfium"], route.Steps.Select(s => s.Converter.Id));
        // The direct p7m → png edge is for a picture inside, not a PDF.
        Assert.Equal([("p7m", "pdf")], planner.PlanThrough("p7m", "pdf", "pdf")!.Steps.Select(s => (s.From, s.To)));
        Assert.Null(planner.PlanThrough("p7m", "xml", "png"));
        Assert.Null(planner.PlanThrough("p7m", "docx", "pdf"));
    }

    [Fact]
    public async Task A_signed_pdf_becomes_a_picture_named_after_the_pdf()
    {
        using var dir = new TempDir();
        var source = dir.File("invoice.pdf.p7m");
        var pdfium = new FakeConverter("pdfium", new ConversionEdge("pdf", "png"));
        await using var queue = new JobQueue(new ConverterCatalog([new FakeOpener("pdf", "pdf", "png"), pdfium]));

        var job = new ConversionJob([source], new Preset { TargetFormat = "png" }, "Test");
        await queue.RunAsync(job);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(Path.Combine(dir.Path, "invoice.png"), job.Outputs.Single());
        Assert.Contains("→png by pdfium", await File.ReadAllTextAsync(job.Outputs.Single(), TestContext.Current.CancellationToken));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task An_envelope_the_engine_refuses_fails_with_its_reason()
    {
        using var dir = new TempDir();
        await using var queue = new JobQueue(new ConverterCatalog([new FakeOpener("refuse", "pdf")]));

        var job = new ConversionJob([dir.File("signature.p7m")], new Preset { TargetFormat = "pdf" }, "Test");
        await queue.RunAsync(job);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("error.p7m_detached", job.Files.Single().ErrorKey);
    }

    [Fact]
    public async Task A_file_inside_that_cant_become_the_target_has_no_route_from_its_own_format()
    {
        using var dir = new TempDir();
        await using var queue = new JobQueue(new ConverterCatalog([new FakeOpener("xml", "xml", "png")]));

        var job = new ConversionJob([dir.File("invoice.xml.p7m")], new Preset { TargetFormat = "png" }, "Test");
        await queue.RunAsync(job);

        Assert.Equal("error.no_route", job.Files.Single().ErrorKey);
        Assert.Equal("xml → png", job.Files.Single().ErrorDetail);
    }
}
