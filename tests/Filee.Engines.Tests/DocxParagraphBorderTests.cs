// Word paragraph borders in HWPX and PDF (issues #510 and #1700).

using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using static Filee.Engines.Tests.DocxBuilder;
using static Filee.Engines.Tests.HwpxAssert;

namespace Filee.Engines.Tests;

public class DocxParagraphBorderTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string Rule = "<w:pBdr><w:bottom w:val=\"single\" w:sz=\"12\" w:space=\"1\" w:color=\"FF0000\"/></w:pBdr>";

    private (List<HParagraph> Paragraphs, string Hwpx) Convert(DocxBuilder docx)
    {
        var dir = fx.NewFolder();
        var document = DocxReader.Read(docx.Save(Path.Combine(dir, "source.docx")), Path.Combine(dir, "media"));
        var hwpx = Path.Combine(dir, "out.hwpx");
        HwpxWriter.Write(document, hwpx);
        ValidPackage(hwpx);
        return (document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().ToList(), hwpx);
    }

    [Fact]
    public void A_rule_under_a_paragraph_is_read_from_its_style_and_turned_off_with_nil()
    {
        var styled = new DocxBuilder
        {
            Styles = new DocxBuilder().Styles + $"<w:style w:type=\"paragraph\" w:styleId=\"Ruled\"><w:name w:val=\"Ruled\"/><w:pPr>{Rule}</w:pPr></w:style>",
        };
        var (paragraphs, _) = Convert(styled
            .Paragraph(P(R("own"), Rule))
            .Paragraph(P(R("between")))
            .Paragraph(P(R("style"), "<w:pStyle w:val=\"Ruled\"/>"))
            .Paragraph(P(R("off"), "<w:pStyle w:val=\"Ruled\"/><w:pBdr><w:bottom w:val=\"nil\"/></w:pBdr>"))
            .Paragraph(P(R("none"))));

        var line = new HBorder(HBorderStyle.Solid, 12 / 8.0 * 25.4 / 72, "#FF0000");
        Assert.Equal(new HBorders(HBorder.None, HBorder.None, HBorder.None, line), paragraphs[0].Format.Borders);
        Assert.Equal(paragraphs[0].Format.Borders, paragraphs[2].Format.Borders);
        Assert.Null(paragraphs[3].Format.Borders);
        Assert.Null(paragraphs[4].Format.Borders);
    }

    [Fact]
    public void Paragraphs_with_the_same_borders_share_one_box()
    {
        var box = "<w:pBdr><w:top w:val=\"single\" w:sz=\"4\"/><w:left w:val=\"single\" w:sz=\"4\"/><w:bottom w:val=\"single\" w:sz=\"4\"/><w:right w:val=\"single\" w:sz=\"4\"/></w:pBdr>";
        var spacedBox = box.Replace("<w:bottom ", "<w:bottom w:space=\"4\" ", StringComparison.Ordinal);
        var (paragraphs, _) = Convert(new DocxBuilder()
            .Paragraph(P(R("first"), box))
            .Paragraph(P(R("middle"), box))
            .Paragraph(P(R("last"), box))
            .Paragraph(P(R("more space"), spacedBox))
            .Paragraph(P(R("indented"), spacedBox + "<w:ind w:left=\"720\"/>"))
            .Paragraph(P(R("other"), Rule)));

        var line = new HBorder(HBorderStyle.Solid, 4 / 8.0 * 25.4 / 72, "#000000");
        Assert.Equal(new HBorders(line, line, line, HBorder.None), paragraphs[0].Format.Borders);
        Assert.Equal(new HBorders(line, line, HBorder.None, HBorder.None), paragraphs[1].Format.Borders);
        Assert.Equal(new HBorders(line, line, HBorder.None, line), paragraphs[2].Format.Borders);
        Assert.Equal(new HBorders(line, line, line, line), paragraphs[3].Format.Borders);
        Assert.Equal(400, paragraphs[3].Format.BorderOffsets?.Bottom);
        Assert.Equal(new HBorders(line, line, line, line), paragraphs[4].Format.Borders); // another indent: a box of its own
        Assert.Equal(HBorder.None, paragraphs[5].Format.Borders!.Value.Top);
    }

    [Fact]
    public void The_border_reaches_the_paragraph_shape()
    {
        var box = "<w:pBdr><w:top w:val=\"single\" w:sz=\"4\"/><w:left w:val=\"single\" w:sz=\"4\"/><w:bottom w:val=\"single\" w:sz=\"4\"/><w:right w:val=\"single\" w:sz=\"4\"/></w:pBdr>";
        var (_, hwpx) = Convert(new DocxBuilder().Paragraph(P(R("ruled"), Rule)).Paragraph(P(R("boxed"), box)).Paragraph(P(R("plain"))));
        var section = Xml(hwpx);
        XElement Border(string text) => HeadItem(hwpx, "paraPr", (string)ParagraphWith(section, text).Attribute("paraPrIDRef")!).Element(Hh + "border")!;
        XElement Fill(XElement border) => HeadItem(hwpx, "borderFill", (string)border.Attribute("borderFillIDRef")!);

        var ruled = Border("ruled");
        Assert.Equal(("SOLID", "#FF0000"), ((string)Fill(ruled).Element(Hh + "bottomBorder")!.Attribute("type")!, (string)Fill(ruled).Element(Hh + "bottomBorder")!.Attribute("color")!));
        Assert.Equal("NONE", (string)Fill(ruled).Element(Hh + "topBorder")!.Attribute("type")!);
        Assert.Equal(("0", "0", "0"), ((string)ruled.Attribute("offsetLeft")!, (string)ruled.Attribute("offsetRight")!, (string)ruled.Attribute("connect")!));
        Assert.Equal("100", (string)ruled.Attribute("offsetBottom")!);

        var boxed = Border("boxed");
        Assert.Equal("SOLID", (string)Fill(boxed).Element(Hh + "leftBorder")!.Attribute("type")!);
        Assert.Equal("400", (string)boxed.Attribute("offsetLeft")!);

        // No border: the template's shape stays as it was, without a line.
        var plain = Border("plain");
        Assert.Equal("NONE", (string)Fill(plain).Element(Hh + "bottomBorder")!.Attribute("type")!);
    }

    [Theory]
    [InlineData("hwpx", null)]
    [InlineData("pdf", "libreoffice")]
    public async Task Header_rules_stay_editable_paragraph_borders_when_rhwp_is_not_next(string target, string? nextConverter)
    {
        var docx = new DocxBuilder().Paragraph(P(R("Body")));
        var header = docx.Header(P(R("Contact"), Rule));
        docx.SectionProperties = $"<w:headerReference w:type=\"default\" r:id=\"{header}\"/>{DocxBuilder.DefaultPage}";
        var dir = fx.NewFolder();
        var source = docx.Save(Path.Combine(dir, "header.docx"));
        var output = Path.Combine(dir, "header.hwpx");
        var step = new ConversionStep(source, "docx", "hwpx", new Preset { TargetFormat = target }, new FixedPath(output), dir)
        {
            NextConverterId = nextConverter,
        };

        await new HwpxConverter().ConvertAsync(step, null, TestContext.Current.CancellationToken);
        var headerXml = Assert.Single(Xml(output).Descendants(Hp + "header"));
        Assert.Empty(headerXml.Descendants(Hp + "tbl"));
        var paragraph = ParagraphWith(headerXml, "Contact");
        var border = HeadItem(output, "paraPr", (string)paragraph.Attribute("paraPrIDRef")!).Element(Hh + "border")!;
        Assert.Equal("100", (string?)border.Attribute("offsetBottom"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Word_rules_reach_the_pdf_with_their_spacing(bool firstPageHeader)
    {
        var rhwp = fx.Converters.Single(c => c.Id == "rhwp");
        Assert.SkipUnless(fx.Catalog.StatusOf(rhwp).IsAvailable, "rhwp not found (pwsh build/fetch-engines.ps1 -Only rhwp)");

        static string Bottom(string color, int space) => $"<w:bottom w:val=\"single\" w:sz=\"8\" w:space=\"{space}\" w:color=\"{color}\"/>";
        static string Bordered(string text, string edges) => P(R(text), $"<w:pBdr>{edges}</w:pBdr>");
        const string between = "<w:between w:val=\"single\" w:sz=\"8\" w:color=\"FF00FF\"/>";
        var grouped = Bottom("00FFFF", 1) + between;
        var docx = new DocxBuilder();
        docx.Styles += $"<w:style w:type=\"paragraph\" w:styleId=\"Ruled\"><w:name w:val=\"Ruled\"/><w:pPr><w:pBdr>{grouped}</w:pBdr></w:pPr></w:style>";
        var header = docx.Header(Bordered("Contact", Bottom("0000FF", 1)));
        var headerType = firstPageHeader ? "first" : "default";
        docx.SectionProperties = $"<w:headerReference w:type=\"{headerType}\" r:id=\"{header}\"/>{DocxBuilder.DefaultPage}" +
            (firstPageHeader ? "<w:titlePg/>" : "");
        docx.Paragraph(Bordered("Heading", Bottom("FF0000", 1)))
            .Paragraph(P(R("Details")))
            .Paragraph(Bordered("Heading", Bottom("00FF00", 12)))
            .Paragraph(P(R("Content")))
            .Paragraph(Bordered("First", grouped))
            .Paragraph(P(R("Second"), "<w:pStyle w:val=\"Ruled\"/>"));

        var source = docx.Save(Path.Combine(fx.NewFolder(), "rules.docx"));
        var catalog = new ConverterCatalog(fx.Converters.Where(c => c.Id is "hwpx-writer" or "rhwp"));
        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "pdf" }, catalog);
        Assert.Equal(JobState.Completed, job.State);
        using var pdf = PdfDocument.Open(Assert.Single(job.Outputs));
        var page = Assert.Single(pdf.GetPages());
        var words = page.GetWords().ToList();
        PdfRectangle Word(string text) => Assert.Single(words, w => w.Text == text).BoundingBox;
        var headings = words.Where(w => w.Text == "Heading").OrderByDescending(w => w.BoundingBox.Bottom).ToArray();
        Assert.Equal(2, headings.Length);

        Assert.InRange(RuleAt(page, (0, 0, 1)), headings[0].BoundingBox.Top, Word("Contact").Bottom);
        Assert.InRange(RuleAt(page, (1, 0, 1)), Word("Second").Top, Word("First").Bottom);
        Assert.True(RuleAt(page, (0, 1, 1)) < Word("Second").Bottom);

        var near = RuleAt(page, (1, 0, 0));
        var far = RuleAt(page, (0, 1, 0));
        Assert.InRange(near, Word("Details").Top, headings[0].BoundingBox.Bottom);
        Assert.InRange(far, Word("Content").Top, headings[1].BoundingBox.Bottom);
        var difference = (headings[1].BoundingBox.Bottom - far) - (headings[0].BoundingBox.Bottom - near);
        Assert.InRange(difference, 10, 12);
    }

    private static double RuleAt(Page page, (double, double, double) color) =>
        Assert.Single(page.Paths.Where(p => p.IsStroked && p.StrokeColor?.ToRGBValues() == color)
            .Select(p => p.GetBoundingRectangle()).OfType<PdfRectangle>(),
            r => r.Width > page.Width / 3 && r.Height <= 2).Bottom;
}
