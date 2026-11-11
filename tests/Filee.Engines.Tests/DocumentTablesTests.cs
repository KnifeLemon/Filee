// DOCX / HTML → XLSX (#1016): each table of a Word document or web page on its own sheet, with merged cells, numbers
// and dates as real values, the cells' own borders and frozen header rows; a clear failure when there is no table; and
// no route that reaches spreadsheets through a document made from something else.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Office.Sheets;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

public class DocumentTablesTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static string Cell(string text, string properties = "") => $"<w:tc><w:tcPr>{properties}</w:tcPr>{P(R(text))}</w:tc>";

    private static string Row(string cells, bool header = false) =>
        $"<w:tr>{(header ? "<w:trPr><w:tblHeader/></w:trPr>" : "")}{cells}</w:tr>";

    /// <summary>A table with the "Table Grid" style (thin lines) and the given grid column widths in twips.</summary>
    private static string Table(int[] grid, string rows, string style = "TableGrid") =>
        $"<w:tbl><w:tblPr><w:tblStyle w:val=\"{style}\"/></w:tblPr><w:tblGrid>" +
        string.Concat(grid.Select(w => $"<w:gridCol w:w=\"{w}\"/>")) + $"</w:tblGrid>{rows}</w:tbl>";

    private async Task<Workbook> ConvertAsync(string path, string target = "xlsx")
    {
        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return XlsxReader.Read(job.Outputs.Single());
    }

    private string Html(string name, string body)
    {
        var path = Path.Combine(fx.NewFolder(), name);
        File.WriteAllText(path, $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>t</title></head><body>{body}</body></html>");
        return path;
    }

    [Fact]
    public async Task Each_word_table_becomes_a_sheet_with_merged_cells_and_real_numbers()
    {
        var docx = new DocxBuilder();
        var link = docx.Hyperlink("https://example.com/paper");
        docx.Paragraph(P(R("Sales by quarter, as reported.")));
        docx.Paragraph(Table([1500, 3000, 2000],
            Row(Cell("Item") + Cell("Amount") + Cell("Code"), header: true) +
            Row($"<w:tc><w:tcPr/><w:p><w:hyperlink r:id=\"{link}\">{R("Paper")}</w:hyperlink></w:p></w:tc>" + Cell("1,234.50") + Cell("007")) +
            Row(Cell("Ink", "<w:vMerge w:val=\"restart\"/>") + Cell("(120)") + Cell("2026-10-23")) +
            Row(Cell("", "<w:vMerge/>") + Cell("12.5%") + Cell("A-1")) +
            Row(Cell("Total for the year", "<w:gridSpan w:val=\"2\"/>") + Cell("$9.99"))));
        docx.Paragraph(P(R("The next table lists the shops.")));
        docx.Paragraph(Table([2000, 2000], Row(Cell("Shop") + Cell("City")) + Row(Cell("North") + Cell("Seoul")), style: "Plain"));
        var book = await ConvertAsync(docx.Save(Path.Combine(fx.NewFolder(), "sales.docx")));

        Assert.Equal(2, book.Sheets.Count);
        var sales = book.Sheets[0];
        Assert.Equal("Item", sales.Get(0, 0)!.Text);
        Assert.Equal("Paper", sales.Get(1, 0)!.Text); // the text of a link
        Assert.Equal((1234.5, "#,##0.00"), (sales.Get(1, 1)!.Value!.Value, sales.Get(1, 1)!.NumberFormat));
        Assert.Null(sales.Get(1, 2)!.Value); // a code with a leading zero stays text
        Assert.Equal("007", sales.Get(1, 2)!.Text);
        Assert.Equal(-120, sales.Get(2, 1)!.Value);
        Assert.Equal(new DateOnly(2026, 10, 23).DayNumber - new DateOnly(1899, 12, 30).DayNumber, sales.Get(2, 2)!.Value);
        Assert.Equal(0.125, sales.Get(3, 1)!.Value!.Value, 9);
        Assert.Equal(9.99, sales.Get(4, 2)!.Value);
        Assert.Contains(new CellRange(2, 0, 3, 0), sales.Merges); // vMerge
        Assert.Contains(new CellRange(4, 0, 4, 1), sales.Merges); // gridSpan
        Assert.DoesNotContain(sales.Rows.Values.SelectMany(r => r.Values), c => c.Text.Contains("Sales by quarter"));
        // Column widths follow the document's grid; the "Table Grid" lines are kept and the header row stays in view.
        Assert.True(sales.ColumnWidth(1) > sales.ColumnWidth(0));
        Assert.Equal("thin", sales.Get(1, 1)!.Style.Left.Style);
        Assert.Equal(1, sales.FrozenRows);

        var shops = book.Sheets[1];
        Assert.Equal("Seoul", shops.Get(1, 1)!.Text);
        Assert.False(shops.Get(1, 1)!.Style.HasBorders); // a table without lines gets none
        Assert.Equal(0, shops.FrozenRows);
    }

    [Fact]
    public async Task Html_tables_keep_spans_and_a_layout_table_gets_no_sheet()
    {
        var html = Html("page.html", """
            <p>Prices of this week.</p>
            <table border="0"><tr><td>
              <table>
                <thead><tr><th>Fruit</th><th colspan="2">Price</th></tr></thead>
                <tbody>
                  <tr><td rowspan="1000">Apple</td><td>1,200</td><td>2026/1/5</td></tr>
                  <tr><td>990</td><td><a href="https://example.com">see shop</a></td></tr>
                </tbody>
              </table>
            </td></tr></table>
            <table border="1"><tr><td>사과</td><td>배</td></tr></table>
            """);
        var book = await ConvertAsync(html);

        Assert.Equal(2, book.Sheets.Count);
        var prices = book.Sheets[0];
        Assert.Equal("Fruit", prices.Get(0, 0)!.Text);
        Assert.True(prices.Get(0, 0)!.Style.Bold); // <th>
        Assert.Contains(new CellRange(0, 1, 0, 2), prices.Merges);
        // A rowspan larger than the table ends at its last row.
        Assert.Contains(new CellRange(1, 0, 2, 0), prices.Merges);
        Assert.Equal(1200, prices.Get(1, 1)!.Value);
        Assert.Equal(new DateOnly(2026, 1, 5).DayNumber - new DateOnly(1899, 12, 30).DayNumber, prices.Get(1, 2)!.Value);
        Assert.Equal(990, prices.Get(2, 1)!.Value);
        Assert.Equal("see shop", prices.Get(2, 2)!.Text);
        Assert.Null(prices.Get(3, 0));
        Assert.Equal(1, prices.FrozenRows);
        Assert.Equal("배", book.Sheets[1].Get(0, 1)!.Text);
    }

    [Fact]
    public async Task Csv_gets_one_file_per_table()
    {
        var docx = new DocxBuilder();
        docx.Paragraph(Table([2000, 2000], Row(Cell("Name") + Cell("Size")) + Row(Cell("a.pdf") + Cell("1,234"))) + P(""));
        docx.Paragraph(Table([2000, 2000], Row(Cell("Code") + Cell("City")) + Row(Cell("SEL") + Cell("Seoul"))));
        var job = await fx.ConvertAsync([docx.Save(Path.Combine(fx.NewFolder(), "two.docx"))], new Preset { TargetFormat = "csv" });

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(2, job.Outputs.Count());
        Assert.Contains(job.Outputs, o => File.ReadAllText(o).Contains("Seoul"));
    }

    [Fact]
    public async Task A_document_without_tables_fails_with_a_message()
    {
        var docx = new DocxBuilder().Paragraph(P(R("Only a letter, no table here.")));
        var job = await fx.ConvertAsync([docx.Save(Path.Combine(fx.NewFolder(), "letter.docx"))], new Preset { TargetFormat = "xlsx" });

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("error.document_no_tables", job.Files[0].ErrorKey);
    }

    [Fact]
    public void Only_the_document_itself_reaches_a_spreadsheet_through_its_tables()
    {
        var planner = fx.Catalog.CreatePlanner();
        foreach (var source in new[] { "docx", "docm", "html", "mhtml" })
            foreach (var target in new[] { "xlsx", "ods", "csv" })
                Assert.Equal("document-tables", Assert.Single(planner.Plan(source, target)!.Steps).Converter.Id);
        // Files that become a DOCX or HTML on the way (a PDF, a picture, Markdown, HWPX, an e-book) don't, even with every
        // optional engine installed, and a document's tables don't go on to JSON.
        var everything = fx.Catalog.CreatePlanner([.. fx.Catalog.All.Select(c => c.Id)]);
        foreach (var source in new[] { "pdf", "jpg", "md", "hwpx", "epub", "odt", "doc" })
            foreach (var target in new[] { "xlsx", "ods", "csv", "json" })
                Assert.DoesNotContain(everything.Plan(source, target)?.Steps ?? [], s => s.Converter.Id == "document-tables");
        foreach (var target in new[] { "json", "xml", "tsv" })
            Assert.DoesNotContain(everything.Plan("docx", target)?.Steps ?? [], s => s.Converter.Id == "document-tables");
    }
}
