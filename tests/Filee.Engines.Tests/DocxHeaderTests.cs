// Headers of Word documents on their way to PDF (issue #1152).

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using UglyToad.PdfPig;
using static Filee.Engines.Tests.DocxBuilder;
using static Filee.Engines.Tests.HwpxAssert;

namespace Filee.Engines.Tests;

public class DocxHeaderTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>A header as in the report: five rows of labels and values.</summary>
    private static string InfoTable(string first = "Doc Ref") =>
        "<w:tbl><w:tblPr><w:tblW w:w=\"9026\" w:type=\"dxa\"/><w:tblStyle w:val=\"TableGrid\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"4513\"/><w:gridCol w:w=\"4513\"/></w:tblGrid>" +
        string.Concat(new[] { first, "Version", "Classification", "Date", "Page" }.Select(label =>
            $"<w:tr><w:tc><w:tcPr><w:tcW w:w=\"4513\" w:type=\"dxa\"/></w:tcPr>{P(R(label))}</w:tc><w:tc><w:tcPr><w:tcW w:w=\"4513\" w:type=\"dxa\"/></w:tcPr>{P(R(label + " value"))}</w:tc></w:tr>")) +
        "</w:tbl>";

    private static string Margins(int top, int header) =>
        $"<w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"{top}\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"{header}\" w:footer=\"720\" w:gutter=\"0\"/>";

    private static DocxBuilder WithHeader(string header, string page)
    {
        var docx = new DocxBuilder().Paragraph(P(R("Table of Contents")));
        docx.SectionProperties = $"<w:headerReference w:type=\"default\" r:id=\"{docx.Header(header)}\"/>{page}";
        return docx;
    }

    private (int Top, int Header) WrittenMargins(DocxBuilder docx)
    {
        var dir = fx.NewFolder();
        var hwpx = Path.Combine(dir, "out.hwpx");
        HwpxWriter.Write(DocxReader.Read(docx.Save(Path.Combine(dir, "source.docx")), Path.Combine(dir, "media")), hwpx);
        ValidPackage(hwpx);
        var margin = Xml(hwpx).Descendants(Hp + "pagePr").Single().Element(Hp + "margin")!;
        return ((int)margin.Attribute("top")!, (int)margin.Attribute("header")!);
    }

    private async Task<string> PdfAsync(DocxBuilder docx)
    {
        var rhwp = fx.Converters.Single(c => c.Id == "rhwp");
        Assert.SkipUnless(fx.Catalog.StatusOf(rhwp).IsAvailable, "rhwp not found (pwsh build/fetch-engines.ps1 -Only rhwp)");
        var job = await fx.ConvertAsync([docx.Save(Path.Combine(fx.NewFolder(), "source.docx"))], new Preset { TargetFormat = "pdf" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    private static double Top(UglyToad.PdfPig.Content.Page page, string word) => page.GetWords().First(w => w.Text == word).BoundingBox.Top;

    private static double Bottom(UglyToad.PdfPig.Content.Page page, string word) => page.GetWords().First(w => w.Text == word).BoundingBox.Bottom;

    [Fact]
    public void A_header_taller_than_the_room_above_the_body_moves_the_body_down()
    {
        var (top, header) = WrittenMargins(WithHeader(P("") + InfoTable() + P(""), Margins(850, 708)));

        Assert.Equal(HwpxUnits.FromTwips(708), top);
        Assert.True(header > HwpxUnits.FromTwips(850 - 708) + 5 * 1000, $"header area {header}"); // five rows of 10 pt
    }

    [Fact]
    public void A_header_that_fits_keeps_the_margins_of_the_document()
    {
        var (top, header) = WrittenMargins(WithHeader(P(R("One line")), Margins(1440, 720)));

        Assert.Equal((HwpxUnits.FromTwips(720), HwpxUnits.FromTwips(720)), (top, header));
    }

    [Fact]
    public void A_negative_top_margin_keeps_the_body_where_it_is()
    {
        // Word: the body starts exactly there, under or over the header.
        var (top, header) = WrittenMargins(WithHeader(InfoTable(), Margins(-1000, 720)));

        Assert.Equal((HwpxUnits.FromTwips(720), HwpxUnits.FromTwips(280)), (top, header));
    }

    [Fact]
    public async Task A_table_after_an_empty_paragraph_is_drawn_above_the_body()
    {
        // rhwp stops at the end of the header area: the empty paragraph filled it and the table was gone.
        var pdf = await PdfAsync(WithHeader(P("") + InfoTable() + P(""), Margins(850, 708)));

        using var document = PdfDocument.Open(pdf);
        var page = document.GetPage(1);
        Assert.Contains("Classification", page.Text);
        Assert.True(Bottom(page, "Page") > Top(page, "Contents"), "the body starts below the table");
    }

    [Theory]
    [InlineData(850, 708)] // little room
    [InlineData(720, 720)] // none
    [InlineData(567, 720)] // Word's header distance larger than its top margin
    public async Task Everything_after_the_first_lines_of_a_tall_header_is_drawn(int topMargin, int headerDistance)
    {
        var pdf = await PdfAsync(WithHeader(P(R("Above")) + InfoTable() + P(R("Between")) + InfoTable("Second") + P(R("Below")), Margins(topMargin, headerDistance)));

        using var document = PdfDocument.Open(pdf);
        var page = document.GetPage(1);
        foreach (var word in new[] { "Above", "Classification", "Between", "Second", "Below" })
            Assert.Contains(word, page.Text);
        Assert.True(Bottom(page, "Below") > Top(page, "Contents"), "the body starts below the header");
    }
}
