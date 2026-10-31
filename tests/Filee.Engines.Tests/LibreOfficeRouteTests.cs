// Settings → Engines → "When LibreOffice is installed": presentations → PDF go through LibreOffice by default (slide
// backgrounds and shapes as in PowerPoint). HWP and HWPX never need it: Filee reads them itself.

using Filee.Core.Conversion;
using Filee.Core.Settings;
using Filee.Engines.Office;

namespace Filee.Engines.Tests;

public class LibreOfficeRouteTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>The built-in engines with a LibreOffice that is installed, whatever this PC has.</summary>
    /// <param name="without">An engine left out, as if it weren't installed.</param>
    private RoutePlanner Planner(bool slides, bool documents = false, bool sheets = false, string? without = null)
    {
        var libreOffice = new InstalledLibreOffice(LibreOfficeConverter.BuildEdges(slides, documents, sheets));
        var converters = fx.Converters.Where(c => c.Id != "libreoffice" && c.Id != without).Append(libreOffice).ToList();
        var priority = new AppSettings().EnginePriority;
        return new RoutePlanner(converters, c => Math.Min(priority.IndexOf(c.Id) is >= 0 and var i ? i : priority.Count, 9));
    }

    [Theory]
    [InlineData("pptx")]
    [InlineData("ppt")]
    [InlineData("odp")]
    public void Presentations_go_to_pdf_through_libreoffice_when_it_is_chosen(string from)
    {
        var route = Planner(slides: true).Plan(from, "pdf");

        Assert.Equal("libreoffice", Assert.Single(route!.Steps).Converter.Id);
    }

    [Fact]
    public void Without_the_choice_pptx_goes_the_built_in_way()
    {
        var route = Planner(slides: false).Plan("pptx", "pdf");

        Assert.Equal(["hwpx-writer", "rhwp"], route!.Steps.Select(s => s.Converter.Id));
    }

    [Theory]
    [InlineData("hwp")]
    [InlineData("hwpx")]
    public void Hwp_routes_never_need_libreoffice(string from)
    {
        // Whatever is chosen for LibreOffice, and with every optional engine there.
        var planner = Planner(slides: true, documents: true, sheets: true);
        Assert.DoesNotContain(fx.Converters.Single(c => c.Id == "libreoffice").Edges, e => e.From is "hwp" or "hwpx");
        Assert.Equal("rhwp", Assert.Single(planner.Plan(from, "pdf")!.Steps).Converter.Id);
        Assert.Equal("docx-writer", Assert.Single(planner.Plan(from, "docx")!.Steps).Converter.Id);
        Assert.Equal("odt-writer", Assert.Single(planner.Plan(from, "odt")!.Steps).Converter.Id);
        foreach (var to in new[] { "pdf", "png", "docx", "odt", "hwp", "hwpx", "txt", "md", "html", "epub" }.Where(to => to != from))
            Assert.DoesNotContain(planner.Plan(from, to)!.Steps, s => s.Converter.Id == "libreoffice");

        // RTF has no built-in writer: Filee writes Markdown that Pandoc turns into RTF, or DOCX / ODT that LibreOffice does.
        Assert.Equal(["hwp-text", "pandoc"], planner.Plan(from, "rtf")!.Steps.Select(s => s.Converter.Id));
        var withoutPandoc = Planner(slides: true, documents: true, sheets: true, without: "pandoc");
        var rtf = withoutPandoc.Plan(from, "rtf")!.Steps.Select(s => s.Converter.Id).ToList();
        Assert.Equal(2, rtf.Count);
        Assert.Contains(rtf[0], new[] { "docx-writer", "odt-writer" });
        Assert.Equal("libreoffice", rtf[1]);
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("xlsx")]
    public void Word_and_excel_go_the_built_in_way_unless_libreoffice_is_chosen(string from)
    {
        // Issue #66 (Chinese punctuation in DOCX) and #67 (Excel print setup): a choice in Settings → Engines.
        Assert.Equal(["hwpx-writer", "rhwp"], Planner(slides: true).Plan(from, "pdf")!.Steps.Select(s => s.Converter.Id));

        var chosen = from == "docx" ? Planner(slides: true, documents: true) : Planner(slides: true, sheets: true);
        Assert.Equal("libreoffice", Assert.Single(chosen.Plan(from, "pdf")!.Steps).Converter.Id);
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("txt")]
    [InlineData("md")]
    [InlineData("html")]
    [InlineData("xlsx")]
    [InlineData("pptx")]
    [InlineData("hwpx")]
    public void Text_that_needs_shaping_goes_to_libreoffice_whatever_is_chosen(string from)
    {
        // Issues #185 and #202: rhwp draws Gujarati and Arabic unshaped, so such a document never takes a route through it.
        var route = Planner(slides: false).Plan(from, "pdf", shaping: true);

        Assert.NotNull(route);
        Assert.DoesNotContain(route.Steps, s => s.Converter.Id == "rhwp");
        Assert.Contains(route.Steps, s => s.Converter.Id == "libreoffice");
        Assert.DoesNotContain(Planner(slides: false).Plan(from, "png", shaping: true)!.Steps, s => s.Converter.Id == "rhwp");
    }

    [Fact]
    public void Each_choice_moves_only_its_own_formats()
    {
        var documents = Planner(slides: false, documents: true);
        Assert.Equal(["hwpx-writer", "rhwp"], documents.Plan("xlsx", "pdf")!.Steps.Select(s => s.Converter.Id));
        Assert.Equal(["hwpx-writer", "rhwp"], documents.Plan("pptx", "pdf")!.Steps.Select(s => s.Converter.Id));
        Assert.Equal("rhwp", Assert.Single(documents.Plan("hwp", "pdf")!.Steps).Converter.Id);
        Assert.Equal(["hwpx-writer", "rhwp"], documents.Plan("md", "pdf")!.Steps.Select(s => s.Converter.Id));

        var sheets = Planner(slides: false, sheets: true);
        Assert.Equal(["hwpx-writer", "rhwp"], sheets.Plan("docx", "pdf")!.Steps.Select(s => s.Converter.Id));
        Assert.Equal(["hwpx-writer", "rhwp"], sheets.Plan("tsv", "pdf")!.Steps.Select(s => s.Converter.Id));
    }

    private sealed class InstalledLibreOffice(IReadOnlyList<ConversionEdge> edges) : IConverter
    {
        public string Id => "libreoffice";
        public string DisplayName => "LibreOffice";
        public IReadOnlyList<ConversionEdge> Edges => edges;
        public int MaxParallelism => 1;
        public EngineStatus GetStatus() => EngineStatus.Available("test");

        public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
