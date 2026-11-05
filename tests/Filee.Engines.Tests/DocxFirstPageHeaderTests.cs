// Word's "different first page" with a header on that page (issue #1152): a text box on the first page of the section,
// with the section's other header hidden there.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using UglyToad.PdfPig;
using static Filee.Engines.Tests.DocxBuilder;
using static Filee.Engines.Tests.HwpxAssert;

namespace Filee.Engines.Tests;

public class DocxFirstPageHeaderTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string TitlePage = "<w:titlePg/>";

    private static string Reference(string id, string type) => $"<w:headerReference w:type=\"{type}\" r:id=\"{id}\"/>";

    private static string Table(string content) =>
        "<w:tbl><w:tblPr><w:tblW w:w=\"9026\" w:type=\"dxa\"/><w:tblStyle w:val=\"TableGrid\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"9026\"/></w:tblGrid>" +
        string.Concat(Enumerable.Repeat($"<w:tr><w:tc><w:tcPr><w:tcW w:w=\"9026\" w:type=\"dxa\"/></w:tcPr>{content}</w:tc></w:tr>", 4)) + "</w:tbl>";

    private static string Body() => P(R("Contents")) + $"<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>{P(R("Second"))}";

    private async Task<List<(string Text, List<UglyToad.PdfPig.Content.Word> Words)>> PagesAsync(DocxBuilder docx)
    {
        var rhwp = fx.Converters.Single(c => c.Id == "rhwp");
        Assert.SkipUnless(fx.Catalog.StatusOf(rhwp).IsAvailable, "rhwp not found (pwsh build/fetch-engines.ps1 -Only rhwp)");
        var job = await fx.ConvertAsync([docx.Save(Path.Combine(fx.NewFolder(), "source.docx"))], new Preset { TargetFormat = "pdf" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        using var document = PdfDocument.Open(job.Outputs.Single());
        return document.GetPages().Select(p => (p.Text, p.GetWords().ToList())).ToList();
    }

    [Fact]
    public async Task A_header_for_the_first_page_only_is_on_the_first_page_above_the_body()
    {
        var docx = new DocxBuilder();
        var first = docx.Header(Table(P(R("Classification"))) + P(""));
        docx.Paragraph(Body());
        docx.SectionProperties = Reference(first, "first") + DefaultPage + TitlePage;

        var pages = await PagesAsync(docx);

        Assert.Contains("Classification", pages[0].Text);
        Assert.DoesNotContain("Classification", pages[1].Text);
        var label = pages[0].Words.Last(w => w.Text == "Classification");
        var body = pages[0].Words.First(w => w.Text == "Contents");
        Assert.True(label.BoundingBox.Bottom > body.BoundingBox.Top, "the body starts below the table");
    }

    [Fact]
    public async Task The_first_page_has_its_own_header_and_the_others_the_default_one()
    {
        var docx = new DocxBuilder();
        var (first, other) = (docx.Header(P(R("CoverHeader"))), docx.Header(P(R("DefaultHeader"))));
        docx.Paragraph(Body());
        docx.SectionProperties = Reference(other, "default") + Reference(first, "first") + DefaultPage + TitlePage;

        var pages = await PagesAsync(docx);

        Assert.Contains("CoverHeader", pages[0].Text);
        Assert.DoesNotContain("DefaultHeader", pages[0].Text);
        Assert.Contains("DefaultHeader", pages[1].Text);
        Assert.DoesNotContain("CoverHeader", pages[1].Text);
    }

    [Fact]
    public async Task A_later_section_starts_with_its_first_page_header()
    {
        var docx = new DocxBuilder();
        var first = docx.Header(P(R("TocHeader")));
        docx.Paragraph(P(R("Cover"), $"<w:sectPr>{DefaultPage}</w:sectPr>") + Body());
        docx.SectionProperties = Reference(first, "first") + DefaultPage + TitlePage;

        var pages = await PagesAsync(docx);

        Assert.DoesNotContain("TocHeader", pages[0].Text);
        Assert.Contains("TocHeader", pages[1].Text);
        Assert.DoesNotContain("TocHeader", pages[2].Text);
    }

    [Fact]
    public async Task Page_numbers_are_live_in_a_paragraph_and_Words_own_in_a_table()
    {
        // rhwp leaves a page number in a table in a text box blank.
        var docx = new DocxBuilder();
        var first = docx.Header(P(R("Live ") + Field("PAGE", "77")) + Table(P(R("Kept ") + Field("PAGE", "88") + R(" of ") + Field("NUMPAGES", "99"))) + P(""));
        docx.Paragraph(Body());
        docx.SectionProperties = Reference(first, "first") + DefaultPage + TitlePage;

        var words = (await PagesAsync(docx))[0].Words.Select(w => w.Text).ToList();

        Assert.DoesNotContain("77", words);
        Assert.Contains("1", words);
        Assert.Contains("88", words);
        Assert.Contains("99", words);
    }

    [Fact]
    public void Page_fields_stay_fields_in_tables_of_other_headers()
    {
        var dir = fx.NewFolder();
        var docx = new DocxBuilder();
        var other = docx.Header(Table(P(Field("PAGE", "88"))));
        docx.Paragraph(P(R("Body")));
        docx.SectionProperties = Reference(other, "default") + DefaultPage;

        var document = DocxReader.Read(docx.Save(Path.Combine(dir, "source.docx")), Path.Combine(dir, "media"));

        var header = Assert.Single(Assert.Single(document.Sections).Headers);
        var table = Assert.IsType<HTable>(Assert.Single(header.Blocks));
        var paragraph = Assert.IsType<HParagraph>(table.Rows[0].Cells[0].Blocks[0]);
        Assert.IsType<HField>(Assert.Single(paragraph.Inlines));
    }

    [Fact]
    public void The_box_is_written_in_the_first_paragraph_and_the_other_header_hidden_there()
    {
        var dir = fx.NewFolder();
        var docx = new DocxBuilder();
        var (first, other) = (docx.Header(P(R("CoverHeader"))), docx.Header(P(R("DefaultHeader"))));
        docx.Paragraph(P(R("Body")));
        docx.SectionProperties = Reference(other, "default") + Reference(first, "first") + DefaultPage + TitlePage;
        var hwpx = Path.Combine(dir, "out.hwpx");
        HwpxWriter.Write(DocxReader.Read(docx.Save(Path.Combine(dir, "source.docx")), Path.Combine(dir, "media")), hwpx);
        ValidPackage(hwpx);

        var section = Xml(hwpx);
        Assert.Equal("1", (string)section.Descendants(Hp + "visibility").Single().Attribute("hideFirstHeader")!);
        var box = section.Elements(Hp + "p").First().Descendants(Hp + "rect").Single();
        Assert.Contains("CoverHeader", box.Value);
        var position = box.Element(Hp + "pos")!;
        Assert.Equal(("PAPER", "PAPER"), ((string)position.Attribute("vertRelTo")!, (string)position.Attribute("horzRelTo")!));
        Assert.Equal(HwpxUnits.FromTwips(720), (int)position.Attribute("vertOffset")!);
        Assert.Equal(HwpxUnits.FromTwips(1440), (int)position.Attribute("horzOffset")!);
    }

    [Fact]
    public void A_blank_first_page_header_only_hides_the_other_one()
    {
        var dir = fx.NewFolder();
        var docx = new DocxBuilder();
        var (first, other) = (docx.Header(P("")), docx.Header(P(R("DefaultHeader"))));
        docx.Paragraph(P(R("Body")));
        docx.SectionProperties = Reference(other, "default") + Reference(first, "first") + DefaultPage + TitlePage;

        var section = Assert.Single(DocxReader.Read(docx.Save(Path.Combine(dir, "source.docx")), Path.Combine(dir, "media")).Sections);

        Assert.True(section.HideFirstHeader);
        Assert.Null(section.FirstPageHeader);
    }
}
