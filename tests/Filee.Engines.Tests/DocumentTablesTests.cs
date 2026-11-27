// DOCX / HTML → XLSX (#1016): each table of a Word document or web page on its own sheet, with merged cells, numbers
// and dates as real values, the cells' own borders and frozen header rows; a clear failure when there is no table; and
// no route that reaches spreadsheets through a document made from something else.

using System.Text;
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

    private async Task<Workbook> ConvertAsync(string path, MergedCells merged) => XlsxReader.Read(await OutputAsync(path, "xlsx", merged));

    /// <summary>The one file a conversion of <paramref name="path"/> to <paramref name="target"/> writes.</summary>
    private async Task<string> OutputAsync(string path, string target, MergedCells merged)
    {
        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = target, Document = { MergedCells = merged } });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
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
        var book = await ConvertAsync(docx.Save(Path.Combine(fx.NewFolder(), "sales.docx")), MergedCells.Smart);

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
        // Smart: a vMerge down the body is filled into each row, a gridSpan across columns stays merged.
        Assert.DoesNotContain(new CellRange(2, 0, 3, 0), sales.Merges);
        Assert.Equal("Ink", sales.Get(3, 0)!.Text);
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
        var book = await ConvertAsync(html, MergedCells.Smart);

        Assert.Equal(2, book.Sheets.Count);
        var prices = book.Sheets[0];
        Assert.Equal("Fruit", prices.Get(0, 0)!.Text);
        Assert.True(prices.Get(0, 0)!.Style.Bold); // <th>
        Assert.Contains(new CellRange(0, 1, 0, 2), prices.Merges);
        // Smart: the body's rowspan is filled into each of its rows, and no further than the table's last one.
        Assert.DoesNotContain(new CellRange(1, 0, 2, 0), prices.Merges);
        Assert.Equal("Apple", prices.Get(2, 0)!.Text);
        Assert.Equal(1200, prices.Get(1, 1)!.Value);
        Assert.Equal(new DateOnly(2026, 1, 5).DayNumber - new DateOnly(1899, 12, 30).DayNumber, prices.Get(1, 2)!.Value);
        Assert.Equal(990, prices.Get(2, 1)!.Value);
        Assert.Equal("see shop", prices.Get(2, 2)!.Text);
        Assert.Null(prices.Get(3, 0));
        Assert.Equal(1, prices.FrozenRows);
        Assert.Equal("배", book.Sheets[1].Get(0, 1)!.Text);
        // Keep: merged as the page draws it, a rowspan larger than the table ending at its last row.
        Assert.Contains(new CellRange(1, 0, 2, 0), (await ConvertAsync(html, MergedCells.Keep)).Sheets[0].Merges);
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

    // ───────────────────────── Merged cells: numbers, the header, the fill budget ─────────────────────────

    /// <summary>
    /// Group totals merged down beside their items, mixed in one column with text merged down ("pending"): numbers
    /// (a plain one, a currency, a date) and text, so Smart has to tell them apart by what they read as.
    /// </summary>
    private string Totals() => Html("totals.html", """
        <table border="1">
          <thead><tr><th>Region</th><th>Item</th><th>Sales</th><th>Region total</th></tr></thead>
          <tbody>
            <tr><td rowspan="2">North</td><td>A</td><td>100</td><td rowspan="2">300</td></tr>
            <tr><td>B</td><td>200</td></tr>
            <tr><td rowspan="2">South</td><td>C</td><td>(50)</td><td rowspan="2">pending</td></tr>
            <tr><td>D</td><td>1,250.50</td></tr>
            <tr><td rowspan="3">East</td><td>E</td><td>$9.99</td><td rowspan="3">$1,000.00</td></tr>
            <tr><td>F</td><td>12.5%</td></tr>
            <tr><td>G</td><td rowspan="2">2026-10-23</td></tr>
            <tr><td colspan="2">Grand total</td><td>1,300</td></tr>
          </tbody>
        </table>
        """);

    /// <summary>Per column, the sum of the cells that hold a number.</summary>
    private static Dictionary<int, double> Sums(Worksheet sheet) => sheet.Rows.Values.SelectMany(r => r)
        .Where(c => c.Value.Value is not null).GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.Sum(c => c.Value.Value!.Value));

    [Fact]
    public async Task Smart_keeps_numbers_merged_down_and_fills_text_beside_them()
    {
        var sheet = (await ConvertAsync(Totals(), MergedCells.Smart)).Sheets[0];

        // Numbers and the date stay merged, as the document draws them: the rows under each hold nothing.
        Assert.Contains(new CellRange(1, 3, 2, 3), sheet.Merges);
        Assert.Contains(new CellRange(5, 3, 7, 3), sheet.Merges);
        Assert.Contains(new CellRange(7, 2, 8, 2), sheet.Merges);
        Assert.Equal(300, sheet.Get(1, 3)!.Value);
        Assert.Equal("", sheet.Get(2, 3)?.Text ?? "");
        Assert.Null(sheet.Get(2, 3)?.Value);
        // Text merged down is filled into each row, the category and the "pending" in the totals column alike.
        Assert.DoesNotContain(new CellRange(3, 3, 4, 3), sheet.Merges);
        Assert.Equal("pending", sheet.Get(4, 3)!.Text);
        foreach (var (row, region) in new[] { (1, "North"), (2, "North"), (3, "South"), (4, "South"), (5, "East"), (6, "East"), (7, "East") })
            Assert.Equal(region, sheet.Get(row, 0)!.Text);
        Assert.Contains(new CellRange(8, 0, 8, 1), sheet.Merges); // across columns
    }

    [Fact]
    public async Task Fill_repeats_numbers_too()
    {
        var sheet = (await ConvertAsync(Totals(), MergedCells.Fill)).Sheets[0];

        Assert.Empty(sheet.Merges);
        Assert.Equal(300, sheet.Get(2, 3)!.Value);
        Assert.Equal(1000, sheet.Get(7, 3)!.Value);
        Assert.Equal("East", sheet.Get(7, 0)!.Text);
    }

    [Fact]
    public async Task Smart_adds_every_number_column_up_as_the_document_draws_it()
    {
        var drawn = Sums((await ConvertAsync(Totals(), MergedCells.Keep)).Sheets[0]);
        var smart = Sums((await ConvertAsync(Totals(), MergedCells.Smart)).Sheets[0]);

        // Each merged value counted once, as drawn (the date is a day number, also counted once); the grand total sits in
        // the last column, as the date's merge covers the one before it.
        var date = new DateOnly(2026, 10, 23).DayNumber - new DateOnly(1899, 12, 30).DayNumber;
        Assert.Equal(300 + 1000 + 1300, drawn[3], 6);
        Assert.Equal(100 + 200 - 50 + 1250.5 + 9.99 + 0.125 + date, drawn[2], 6);
        Assert.Equal(drawn.Keys.Order(), smart.Keys.Order());
        foreach (var (column, sum) in drawn)
            Assert.Equal(sum, smart[column], 6);
    }

    [Fact]
    public async Task Csv_and_ods_get_the_same_choice()
    {
        var csv = File.ReadAllLines(await OutputAsync(Totals(), "csv", MergedCells.Smart));
        Assert.StartsWith("North,B,200,", csv[2]); // the category repeated
        Assert.EndsWith(",", csv[2]); // the merged total not repeated
        Assert.EndsWith(",pending", csv[4]);

        var ods = Assert.Single(OdsReader.Read(await OutputAsync(Totals(), "ods", MergedCells.Smart)).Sheets);
        Assert.Contains(new CellRange(1, 3, 2, 3), ods.Merges);
        Assert.DoesNotContain(new CellRange(1, 0, 2, 0), ods.Merges);
        Assert.Equal("North", ods.Get(2, 0)!.Text);
        Assert.Equal(2600, Sums(ods)[3], 6);
    }

    [Fact]
    public async Task Smart_keeps_an_empty_header_corner_merged()
    {
        var html = Html("corner.html", """
            <table border="1">
              <thead>
                <tr><th rowspan="2"></th><th colspan="2">Q1</th></tr>
                <tr><th>Jan</th><th>Feb</th></tr>
              </thead>
              <tbody>
                <tr><td>Glass</td><td>1</td><td>2</td></tr>
                <tr><td rowspan="2"></td><td>3</td><td>4</td></tr>
                <tr><td>5</td><td>6</td></tr>
              </tbody>
            </table>
            """);
        var sheet = (await ConvertAsync(html, MergedCells.Smart)).Sheets[0];

        Assert.Contains(new CellRange(0, 0, 1, 0), sheet.Merges); // the empty corner of a two-level header
        Assert.Contains(new CellRange(0, 1, 0, 2), sheet.Merges);
        Assert.DoesNotContain(new CellRange(3, 0, 4, 0), sheet.Merges); // an empty cell in the body has nothing to keep
        Assert.Equal(2, sheet.FrozenRows);
    }

    /// <summary>A heading row over <paramref name="rows"/> rows, with one cell per text spanning all of them.</summary>
    private string Spanning(int rows, params string[] texts)
    {
        var body = new StringBuilder("<table border=\"1\"><thead><tr>");
        foreach (var i in Enumerable.Range(0, texts.Length))
            body.Append($"<th>Note {i}</th>");
        body.Append("<th>Item</th></tr></thead><tbody><tr>");
        foreach (var text in texts)
            body.Append($"<td rowspan=\"{rows}\">{text}</td>");
        body.Append("<td>0</td></tr>");
        for (var r = 1; r < rows; r++)
            body.Append($"<tr><td>{r}</td></tr>");
        body.Append("</tbody></table>");
        return Html("long.html", body.ToString());
    }

    [Theory]
    [InlineData(MergedCells.Smart)]
    [InlineData(MergedCells.Fill)]
    public async Task A_fill_past_the_budget_stays_merged_counting_the_whole_table(MergedCells merged)
    {
        // 512 copies each (a rowspan reaches 1,000 rows at most): the first text (20,480,000 characters) fits the budget,
        // the second (15,360,000 more) doesn't.
        Assert.True(512L * 40_000 <= TableSheets.MaxFilledCharacters && 512L * 70_000 > TableSheets.MaxFilledCharacters);
        var sheet = (await ConvertAsync(Spanning(513, new string('a', 40_000), new string('b', 30_000)), merged)).Sheets[0];

        Assert.DoesNotContain(new CellRange(1, 0, 513, 0), sheet.Merges);
        Assert.Equal(40_000, sheet.Get(513, 0)!.Text.Length);
        Assert.Contains(new CellRange(1, 1, 513, 1), sheet.Merges);
        Assert.Equal("", sheet.Get(513, 1)?.Text ?? "");
        Assert.Equal(30_000, sheet.Get(1, 1)!.Text.Length);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task The_budget_allows_exactly_its_size(int over, bool filled)
    {
        // 512 copies of 65,536 characters is the budget exactly; one character more is past it.
        Assert.Equal(TableSheets.MaxFilledCharacters, 512L * 65_536);
        var text = new string('x', 65_536 + over);
        var csv = await OutputAsync(Spanning(513, text), "csv", MergedCells.Fill);

        var last = File.ReadLines(csv).Last();
        Assert.Equal(filled, last.StartsWith(text + ",", StringComparison.Ordinal));
        Assert.EndsWith(",512", last);
    }
}
