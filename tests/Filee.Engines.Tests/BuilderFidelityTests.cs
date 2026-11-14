// The decks and drawings the reader tests build in code (PptBuilder, OdpBuilder, VsdxBuilder) must be files real
// readers take, or the reader tests could pass on input PowerPoint, Impress and Visio never save. LibreOffice converts
// one file of each builder to PDF: it must load it ("source file could not be loaded" is how it refuses a damaged
// container or package), give the pages Filee reads from it, and draw the same text on them. Skipped where LibreOffice
// isn't installed (pwsh build/fetch-engines.ps1 -Only libreoffice), on any OS.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Odf.Draw;
using Filee.Engines.Office;
using Filee.Engines.Slides;
using Filee.Engines.Visio;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Filee.Engines.Tests;

public sealed class BuilderFidelityTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_ppt_deck_from_the_builder_opens_in_libreoffice_with_its_slides_and_text()
    {
        var path = Path.Combine(fx.NewFolder(), "deck.ppt");
        File.WriteAllBytes(path, PptReaderTests.RichDeck().Build());

        // The hidden third slide is left out by both.
        await AssertSameAsync(path, "ppt", PptxSlideReader.Read(path, cancellationToken: Token),
            ["Hello PPT", "First point", "Second point", "Plain text box"], ["Title slide", "Subtitle here"]);
    }

    [Fact]
    public async Task An_odp_deck_and_an_odg_drawing_from_the_builder_open_in_libreoffice_with_their_pages_and_text()
    {
        var deck = new OdpBuilder
        {
            // As Impress saves a deck: a default graphic style, and presentation styles on the title and outline frames.
            Styles = "<style:default-style style:family=\"graphic\"><style:graphic-properties svg:stroke-color=\"#3465a4\" draw:fill-color=\"#729fcf\"/>" +
                     "<style:text-properties fo:font-size=\"18pt\" fo:color=\"#000000\"/></style:default-style>",
            ContentStyles = OdpBuilder.GraphicStyle("blue", "draw:fill=\"solid\" draw:fill-color=\"#3366CC\" draw:stroke=\"none\"") +
                            OdpBuilder.GraphicStyle("title", "draw:fill=\"none\" draw:stroke=\"none\"", "fo:font-size=\"40pt\"", "presentation") +
                            OdpBuilder.GraphicStyle("outline", "draw:fill=\"none\" draw:stroke=\"none\"", "fo:font-size=\"28pt\"", "presentation"),
            Pages = OdpBuilder.Page(
                "<draw:frame presentation:style-name=\"title\" presentation:class=\"title\" svg:x=\"2cm\" svg:y=\"1cm\" svg:width=\"24cm\" svg:height=\"2.5cm\"><draw:text-box><text:p>Fidelity check</text:p></draw:text-box></draw:frame>" +
                "<draw:frame presentation:style-name=\"outline\" presentation:class=\"outline\" svg:x=\"2cm\" svg:y=\"4cm\" svg:width=\"14cm\" svg:height=\"8cm\"><draw:text-box>" +
                "<text:list><text:list-item><text:p>First item</text:p></text:list-item><text:list-item><text:p>Second item</text:p></text:list-item></text:list></draw:text-box></draw:frame>" +
                "<draw:rect draw:style-name=\"blue\" svg:x=\"18cm\" svg:y=\"4cm\" svg:width=\"6cm\" svg:height=\"4cm\"/>" +
                "<draw:frame svg:x=\"18cm\" svg:y=\"9cm\" svg:width=\"4cm\" svg:height=\"4cm\"><draw:image xlink:href=\"Pictures/teal.png\"/></draw:frame>") +
                OdpBuilder.Page("<draw:frame presentation:style-name=\"outline\" presentation:class=\"outline\" svg:x=\"2cm\" svg:y=\"2cm\" svg:width=\"20cm\" svg:height=\"3cm\"><draw:text-box><text:p>Closing words</text:p></draw:text-box></draw:frame>"),
        };
        // A picture part: the manifest lists it.
        deck.Parts["Pictures/teal.png"] = OdpBuilder.Png(8, 8, SkiaSharp.SKColors.Teal);
        var odp = deck.Save(Path.Combine(fx.NewFolder(), "deck.odp"));
        await AssertSameAsync(odp, "odp", OdfDrawReader.Read(odp, cancellationToken: Token), ["Fidelity check", "First item", "Second item"], ["Closing words"]);

        var drawing = new OdpBuilder
        {
            Drawing = true,
            Pages = "<draw:page draw:name=\"one\" draw:master-page-name=\"Default\"><draw:custom-shape svg:x=\"2cm\" svg:y=\"2cm\" svg:width=\"8cm\" svg:height=\"4cm\"><text:p>Drawn box</text:p>" +
                    "<draw:enhanced-geometry draw:type=\"rectangle\" draw:enhanced-path=\"M 0 0 L 21600 0 21600 21600 0 21600 Z N\"/></draw:custom-shape></draw:page>" +
                    "<draw:page draw:name=\"two\" draw:master-page-name=\"Default\"><draw:frame svg:x=\"2cm\" svg:y=\"2cm\" svg:width=\"12cm\" svg:height=\"2cm\"><draw:text-box><text:p>Second page</text:p></draw:text-box></draw:frame></draw:page>",
        };
        var odg = drawing.Save(Path.Combine(fx.NewFolder(), "drawing.odg"));
        await AssertSameAsync(odg, "odg", OdfDrawReader.Read(odg, cancellationToken: Token), ["Drawn box"], ["Second page"]);
    }

    [Fact]
    public async Task A_vsdx_drawing_from_the_builder_opens_in_libreoffice_with_its_page_and_master_text()
    {
        // The fuzz seed: masters, a background page, a picture, layers and text rows.
        var path = Path.Combine(fx.NewFolder(), "drawing.vsdx");
        File.WriteAllBytes(path, VisioFuzzTests.Seed());
        // "Master" is the text of a master shape, drawn only when the instance finds its master. LibreOffice also
        // gives the background page a page of its own after the drawing's page; Filee draws it only behind that page.
        await AssertSameAsync(path, "vsd", VisioReader.Read(path, Token), extraPages: 1, ["Master"]);
    }

    /// <summary>
    /// Found by comparing the fuzz seed with LibreOffice: [MS-VSDX] FillPattern 3 is a grid, 6 horizontal lines and 7
    /// vertical lines, but VisioReader's pattern table runs in another order (7 draws a grid).
    /// </summary>
    [Fact(Skip = "Reader gap: VisioReader.Patterns doesn't follow the [MS-VSDX] FillPattern table; left to the Visio reader's owner")]
    public void Visio_fill_patterns_follow_the_ms_vsdx_table()
    {
        var builder = new VsdxBuilder();
        string Box(int id, double x, int pattern) => VsdxBuilder.Shape(id, x, 1, 1, 1,
            VsdxBuilder.Rectangle(1, 1) + VsdxBuilder.Cell("FillForegnd", "#0000FF") + VsdxBuilder.Cell("FillPattern", pattern));
        builder.Page(Box(1, 1, 3) + Box(2, 2.5, 6) + Box(3, 4, 7), 5, 2);
        var deck = VisioReader.Read(builder.Save(Path.Combine(fx.NewFolder(), "patterns.vsdx")), Token);
        Assert.Equal(["cross", "horz", "vert"], deck.Slides[0].Elements.OfType<SlideShape>().Select(s => Assert.IsType<PatternFill>(s.Fill).Preset));
    }

    /// <summary>
    /// Converts <paramref name="source"/> with LibreOffice (as format <paramref name="from"/>, which picks its PDF
    /// export) and checks it has a page for each of Filee's slides, with each slide's <paramref name="texts"/> in
    /// both Filee's slide and LibreOffice's page. LibreOffice may add <paramref name="extraPages"/> after them.
    /// </summary>
    private Task AssertSameAsync(string source, string from, SlideDeck filee, params string[][] texts) =>
        AssertSameAsync(source, from, filee, 0, texts);

    private async Task AssertSameAsync(string source, string from, SlideDeck filee, int extraPages, params string[][] texts)
    {
        var folder = fx.NewFolder();
        var libreOffice = new LibreOfficeConverter(new EngineEnvironment(Path.Combine(folder, "data")));
        Assert.SkipUnless(libreOffice.GetStatus().IsAvailable, "LibreOffice not found (pwsh build/fetch-engines.ps1 -Only libreoffice)");

        var written = await libreOffice.ConvertAsync(new ConversionStep(source, from, "pdf", new Preset(), new FolderOutput(folder), folder), null, Token);
        using var pdf = PigDocument.Open(Assert.Single(written));
        Assert.Equal(texts.Length, filee.Slides.Count);
        Assert.Equal(filee.Slides.Count + extraPages, pdf.NumberOfPages);
        for (var i = 0; i < texts.Length; i++)
        {
            var fileeText = Squeeze(string.Concat(TextOf(filee.Slides[i].Elements)));
            var pageText = Squeeze(pdf.GetPage(i + 1).Text);
            foreach (var text in texts[i])
            {
                Assert.Contains(Squeeze(text), fileeText, StringComparison.Ordinal);
                Assert.Contains(Squeeze(text), pageText, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>The text without white space (PDF text comes without the spaces between words).</summary>
    private static string Squeeze(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    private static IEnumerable<string> TextOf(IEnumerable<SlideElement> elements) => elements.SelectMany(element => element switch
    {
        SlideGroup group => TextOf(group.Children),
        SlideShape { Text: { } frame } => frame.Paragraphs.SelectMany(p => p.Inlines).OfType<HText>().Select(t => t.Text),
        _ => [],
    });

    private sealed class FolderOutput(string folder) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => Path.Combine(folder, $"out{suffix}.{extension}");
    }
}
