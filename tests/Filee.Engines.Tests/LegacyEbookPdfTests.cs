// The older e-book formats → PDF without calibre: through the Filee renderer, which needs no engine outside the app
// (the default route, HWPX writer → rhwp, is checked in LegacyEbookTests when rhwp is installed).

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Filee.Engines.Tests;

[Collection("EngineEnvironment")] // FileeRenderer is process-wide
public sealed class LegacyEbookPdfTests(EngineFixture fx) : IClassFixture<EngineFixture>, IDisposable
{
    private static readonly string[] OptionalEngines = ["pandoc", "libreoffice", "calibre", "rhwp"];

    public void Dispose()
    {
        EngineEnvironment.FileeRenderer = false;
        fx.Catalog.Refresh();
    }

    [Theory]
    [MemberData(nameof(LegacyEbookTests.Formats), MemberType = typeof(LegacyEbookTests))]
    public async Task Converts_to_pdf_with_the_filee_renderer(string kind)
    {
        EngineEnvironment.FileeRenderer = true;
        var catalog = new ConverterCatalog(fx.Converters.Where(c => !OptionalEngines.Contains(c.Id))) { Priority = fx.Catalog.Priority };
        var book = LegacyEbookTests.Book(fx.NewFolder(), kind);
        await using var queue = new JobQueue(catalog);
        var job = new ConversionJob([book], new Preset { TargetFormat = "pdf" }, "pdf");

        await queue.RunAsync(job);

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        using var pdf = PigDocument.Open(job.Outputs.Single());
        var text = string.Concat(pdf.GetPages().Select(p => p.Text));
        Assert.Contains(LegacyEbookBuilders.Line1.Replace(" ", ""), text.Replace(" ", ""));
        Assert.Contains(LegacyEbookBuilders.Line2.Replace(" ", ""), text.Replace(" ", ""));
    }
}
