// Documents → XLSX (#1016): each table of a Word (DOCX, DOC), OpenDocument, RTF, 한글 (HWP, HWPX) document or web page
// on its own sheet, tables in text boxes and shapes too, with merged cells, numbers and dates as real values, equations
// as plain text, the cells' own borders, fill, bold and alignment and frozen header rows; rows tall enough for a merged
// cell's text as measured in its font; one-cell boxes left out beside real tables; a clear failure when there is no
// table; no route that reaches spreadsheets through a document made from something else; and hostile tables (spans
// over millions of cells, thousands of tables, deep nesting) refused or written quickly.

using System.Diagnostics;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Office;
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

    /// <summary>A floating DrawingML shape of <paramref name="geometry"/> holding <paramref name="content"/> (w:p and w:tbl).</summary>
    private static string ShapeWith(string geometry, string content) =>
        Anchor(ShapeUri,
            $"<wps:wsp><wps:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"3657600\" cy=\"914400\"/></a:xfrm><a:prstGeom prst=\"{geometry}\"/></wps:spPr>" +
            $"<wps:txbx><w:txbxContent>{content}</w:txbxContent></wps:txbx><wps:bodyPr/></wps:wsp>",
            3657600, 914400, "<wp:positionH relativeFrom=\"column\"><wp:posOffset>0</wp:posOffset></wp:positionH>",
            "<wp:positionV relativeFrom=\"paragraph\"><wp:posOffset>0</wp:posOffset></wp:positionV>");

    [Fact]
    public async Task Tables_in_text_boxes_and_shapes_get_sheets_where_they_stand()
    {
        // A table in the body, one in a text box and one in a callout shape (a shape with text), in this order.
        var docx = new DocxBuilder();
        docx.Paragraph(Table([2000, 2000], Row(Cell("Body") + Cell("1")) + Row(Cell("b") + Cell("2"))));
        docx.Paragraph(P(ShapeWith("rect", P(R("Prices")) + Table([2000, 2000], Row(Cell("Boxed") + Cell("3")) + Row(Cell("c") + Cell("4"))) + P("")) + R("Beside the box.")));
        docx.Paragraph(P(ShapeWith("wedgeRectCallout", Table([2000, 2000], Row(Cell("Callout") + Cell("5")) + Row(Cell("d") + Cell("6"))) + P(""))));
        var word = docx.Save(Path.Combine(fx.NewFolder(), "boxes.docx"));

        var book = await ConvertAsync(word);
        Assert.Equal(["Body", "Boxed", "Callout"], book.Sheets.Select(s => s.Get(0, 0)!.Text));
        Assert.Equal(6, book.Sheets[2].Get(1, 1)!.Value);

        // The same through 한글: Filee's HWPX keeps the text boxes as 글상자 with their tables.
        var hwpx = Assert.Single((await fx.ConvertAsync([word], new Preset { TargetFormat = "hwpx" })).Outputs);
        Assert.Equal(["Body", "Boxed", "Callout"], (await ConvertAsync(hwpx)).Sheets.Select(s => s.Get(0, 0)!.Text));
    }

    [Fact]
    public void One_cell_boxes_get_no_sheet_beside_real_tables_and_share_one_when_alone()
    {
        // A 보기 box, a two-cell table and a source tag: only the table is data.
        var mixed = DocumentTablesConverter.Build(Document(Box("<보기> ㄱ. ㄴ."), Pair("Score", "80"), Box("출처: 2022년")),
            TestContext.Current.CancellationToken);
        var sheet = Assert.Single(mixed.Sheets);
        Assert.Equal("Score", sheet.Get(0, 0)!.Text);
        Assert.Equal(80, sheet.Get(0, 1)!.Value);

        // Only boxes: their text still arrives, one sheet, a box a row in reading order.
        var boxes = DocumentTablesConverter.Build(Document(Box("Title"), Box("Note"), Box("42")), TestContext.Current.CancellationToken);
        var only = Assert.Single(boxes.Sheets);
        Assert.Equal(["Title", "Note", "42"], Enumerable.Range(0, 3).Select(r => only.Get(r, 0)!.Text));
        Assert.Equal(42, only.Get(2, 0)!.Value);
    }

    /// <summary>A one-row table whose first cell holds the 한글 equation <paramref name="script"/>.</summary>
    private static HTable EquationRow(string script)
    {
        var table = Pair("", "x");
        table.Rows[0].Cells[0].Blocks.OfType<HParagraph>().Single().Inlines.Add(new HEquation(EquationParser.Parse(script), default));
        return table;
    }

    [Theory]
    [InlineData("20 ^{이상}", "20 이상")] // 한글 frequency tables raise 이상 / 미만 after the class bound
    [InlineData("{9} over {2} ` rm cm ^{3}", "9/2 cm³")]
    [InlineData("x ^{2} -4x-12=0", "x²-4x-12=0")]
    [InlineData("f ^{-1} (4)", "f⁻¹(4)")]
    [InlineData("a _{n+1}", "aₙ₊₁")]
    [InlineData("x ^{a+b}", "x^(a+b)")] // no superscript a or b in the set Filee uses: the linear form
    [InlineData("40~~~", "40")]
    public void Equations_in_cells_are_plain_text(string script, string text)
    {
        var sheet = Assert.Single(DocumentTablesConverter.Build(Document(EquationRow(script)), TestContext.Current.CancellationToken).Sheets);
        Assert.Equal(text, sheet.Get(0, 0)!.Text);
        // Formats without equations keep the linear form as before.
        Assert.Equal(EquationScript.Linear(EquationParser.Parse(script)), new HEquation(EquationParser.Parse(script), default).Text);
    }

    [Fact]
    public void Rows_under_a_merged_cell_fit_its_wrapped_text_and_keep_their_own()
    {
        // Three columns of 1000 twips (1764 HWPUNIT, about 9 characters each). Row 1: one long line over two columns
        // that wraps at words, beside a short cell. Rows 2-3: six short lines over both rows, beside a cell of four
        // lines in row 2.
        var table = new HTable { ColumnCount = 3, ColumnWidths = [1764, 1764, 1764] };
        table.Rows.Add(RowOf(Spanned(string.Join(' ', Enumerable.Repeat("word", 40)), columns: 2), Spanned("x")));
        table.Rows.Add(RowOf(Spanned("1\n2\n3\n4\n5\n6", rows: 2), Spanned("a\nb\nc\nd")));
        table.Rows.Add(RowOf(Spanned("y")));
        var sheet = Assert.Single(DocumentTablesConverter.Build(Document(table), TestContext.Current.CancellationToken).Sheets);

        // 40 words of 4 letters in about 18 characters: at least 8 lines of 15 pt (a line of Excel's Calibri 11).
        Assert.True(sheet.RowHeights.GetValueOrDefault(0) >= 8 * 15, $"row 1: {sheet.RowHeights.GetValueOrDefault(0)}");
        // Six lines over two rows: row 2 keeps the four lines its own cell needs, row 3 gets the rest.
        var (second, third) = (sheet.RowHeights.GetValueOrDefault(1), sheet.RowHeights.GetValueOrDefault(2));
        Assert.True(second >= 4 * 15 && second + third >= 6 * 15, $"rows 2, 3: {second}, {third}");
    }

    [Fact]
    public void Many_merged_cells_are_measured_quickly()
    {
        // 50,000 merged cells of a few lines each: every one is measured in its font, once.
        var table = new HTable { ColumnCount = 3, ColumnWidths = [1764, 1764, 1764] };
        for (var r = 0; r < 50_000; r++)
            table.Rows.Add(RowOf(Spanned($"row {r} of the table\nsecond line\nthird line", columns: 2), Spanned("x")));
        var watch = Stopwatch.StartNew();
        var sheet = Assert.Single(DocumentTablesConverter.Build(Document(table), TestContext.Current.CancellationToken).Sheets);

        Assert.True(sheet.RowHeights.GetValueOrDefault(49_999) >= 3 * 15);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
    }

    private static HRow RowOf(params HCell[] cells)
    {
        var row = new HRow();
        row.Cells.AddRange(cells);
        return row;
    }

    /// <summary>A cell of lines (a paragraph each) over <paramref name="rows"/> rows and <paramref name="columns"/> columns.</summary>
    private static HCell Spanned(string lines, int rows = 1, int columns = 1)
    {
        var cell = new HCell { RowSpan = rows, ColSpan = columns };
        foreach (var line in lines.Split('\n'))
        {
            var paragraph = new HParagraph();
            paragraph.Inlines.Add(new HText(line, default));
            cell.Blocks.Add(paragraph);
        }
        return cell;
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

    private const int Day0 = 693593; // new DateOnly(1899, 12, 30).DayNumber: Excel's day 0

    /// <summary>
    /// A Word table with what each format below keeps: a header row, a horizontal and a vertical merge, a shaded bold
    /// centred number, a right-aligned date, a code with a leading zero and a merged cell of three lines.
    /// </summary>
    private static string WordTable(string percentCell = "") =>
        Table([2000, 2000, 2000],
            Row(Cell("Region", "<w:gridSpan w:val=\"2\"/>") + Cell("Sales"), header: true) +
            Row(Cell("North", "<w:vMerge w:val=\"restart\"/>") + Cell("Q1") +
                $"<w:tc><w:tcPr><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"FFFF00\"/></w:tcPr>{P(R("1,234.50", "<w:b/>"), "<w:jc w:val=\"center\"/>")}</w:tc>") +
            Row(Cell("", "<w:vMerge/>") + Cell("Q2") + $"<w:tc><w:tcPr/>{P(R("2026-03-31"), "<w:jc w:val=\"right\"/>")}</w:tc>") +
            Row(Cell("007") + (percentCell.Length > 0 ? percentCell : Cell("12.5%")) + Cell("x")) +
            Row($"<w:tc><w:tcPr><w:gridSpan w:val=\"3\"/></w:tcPr>{P(R("First line of a note"))}{P(R("second line"))}{P(R("third line"))}</w:tc>"));

    /// <summary>What <see cref="WordTable"/> looks like on its sheet, whichever format carried it.</summary>
    private static void AssertWordTable(Worksheet sheet, string format)
    {
        Assert.Equal("Region", sheet.Get(0, 0)!.Text);
        Assert.Contains(new CellRange(0, 0, 0, 1), sheet.Merges); // across columns
        Assert.Contains(new CellRange(1, 0, 2, 0), sheet.Merges); // down rows
        Assert.Contains(new CellRange(4, 0, 4, 2), sheet.Merges);
        var amount = sheet.Get(1, 2)!;
        Assert.Equal((1234.5, "#,##0.00"), (amount.Value!.Value, amount.NumberFormat));
        Assert.True(amount.Style.Bold, format);
        Assert.Equal("#FFFF00", amount.Style.Fill, ignoreCase: true);
        Assert.Equal(CellAlign.Center, amount.Style.Align);
        Assert.Equal(new DateOnly(2026, 3, 31).DayNumber - Day0, sheet.Get(2, 2)!.Value);
        Assert.Equal(CellAlign.Right, sheet.Get(2, 2)!.Style.Align);
        Assert.Equal(CellAlign.General, sheet.Get(1, 1)!.Style.Align); // left text keeps Excel's own alignment
        Assert.Equal("007", sheet.Get(3, 0)!.Text);
        Assert.Null(sheet.Get(3, 0)!.Value);
        Assert.Equal("thin", sheet.Get(1, 1)!.Style.Left.Style);
        // Excel doesn't fit a row to a merged cell's text: the three lines get their height, other rows fit themselves.
        Assert.True(sheet.RowHeights.GetValueOrDefault(4) >= 45, $"{format}: row height {sheet.RowHeights.GetValueOrDefault(4)}");
        Assert.False(sheet.RowHeights.ContainsKey(1));
        Assert.Equal(1, sheet.FrozenRows);
    }

    [Fact]
    public async Task Hwpx_and_hwp_tables_become_sheets_with_an_equation_number_as_a_number()
    {
        // A number typed as an equation, as 한글 users often do in tables: its text, not its script, is the value.
        var docx = new DocxBuilder();
        docx.Paragraph(P(R("Before the table.")));
        docx.Paragraph(WordTable("<w:tc><w:tcPr/><w:p><m:oMath><m:r><m:t>40</m:t></m:r></m:oMath></w:p></w:tc>"));
        var word = docx.Save(Path.Combine(fx.NewFolder(), "sales.docx"));
        var hwpx = Assert.Single((await fx.ConvertAsync([word], new Preset { TargetFormat = "hwpx" })).Outputs);
        var hwp = Assert.Single((await fx.ConvertAsync([hwpx], new Preset { TargetFormat = "hwp" })).Outputs);

        foreach (var (path, format) in new[] { (hwpx, "hwpx"), (hwp, "hwp") })
        {
            var sheet = Assert.Single((await ConvertAsync(path)).Sheets);
            AssertWordTable(sheet, format);
            Assert.Equal(40, sheet.Get(3, 1)!.Value);
        }
    }

    [Fact]
    public async Task Odt_tables_keep_spans_styles_and_header_rows()
    {
        const string ruled = "fo:border=\"0.5pt solid #000000\"";
        var odt = new OdtBuilder
        {
            ContentStyles =
                "<style:style style:name=\"Mid\" style:family=\"paragraph\"><style:paragraph-properties fo:text-align=\"center\"/>" +
                "<style:text-properties fo:font-weight=\"bold\"/></style:style>" +
                "<style:style style:name=\"End\" style:family=\"paragraph\"><style:paragraph-properties fo:text-align=\"end\"/></style:style>" +
                $"<style:style style:name=\"Ruled\" style:family=\"table-cell\"><style:table-cell-properties {ruled}/></style:style>" +
                $"<style:style style:name=\"Yellow\" style:family=\"table-cell\"><style:table-cell-properties fo:background-color=\"#ffff00\" {ruled}/></style:style>",
            Body =
                "<text:p>Sales by region.</text:p>" +
                "<table:table table:name=\"Sales\"><table:table-column table:number-columns-repeated=\"3\"/>" +
                "<table:table-header-rows><table:table-row>" +
                "<table:table-cell table:style-name=\"Ruled\" table:number-columns-spanned=\"2\"><text:p>Region</text:p></table:table-cell><table:covered-table-cell/>" +
                "<table:table-cell table:style-name=\"Ruled\"><text:p>Sales</text:p></table:table-cell></table:table-row></table:table-header-rows>" +
                "<table:table-row><table:table-cell table:style-name=\"Ruled\" table:number-rows-spanned=\"2\"><text:p>North</text:p></table:table-cell>" +
                "<table:table-cell table:style-name=\"Ruled\"><text:p>Q1</text:p></table:table-cell>" +
                "<table:table-cell table:style-name=\"Yellow\" office:value-type=\"float\" office:value=\"1234.5\"><text:p text:style-name=\"Mid\">1,234.50</text:p></table:table-cell></table:table-row>" +
                "<table:table-row><table:covered-table-cell/><table:table-cell table:style-name=\"Ruled\"><text:p>Q2</text:p></table:table-cell>" +
                "<table:table-cell table:style-name=\"Ruled\"><text:p text:style-name=\"End\">2026-03-31</text:p></table:table-cell></table:table-row>" +
                "<table:table-row><table:table-cell table:style-name=\"Ruled\"><text:p>007</text:p></table:table-cell>" +
                "<table:table-cell table:style-name=\"Ruled\"><text:p>12.5%</text:p></table:table-cell><table:table-cell table:style-name=\"Ruled\"><text:p>x</text:p></table:table-cell></table:table-row>" +
                "<table:table-row><table:table-cell table:style-name=\"Ruled\" table:number-columns-spanned=\"3\"><text:p>First line of a note</text:p><text:p>second line</text:p><text:p>third line</text:p></table:table-cell>" +
                "<table:covered-table-cell/><table:covered-table-cell/></table:table-row></table:table>" +
                "<text:p>After the table.</text:p>",
        };
        var sheet = Assert.Single((await ConvertAsync(odt.Save(Path.Combine(fx.NewFolder(), "sales.odt")))).Sheets);

        AssertWordTable(sheet, "odt");
        Assert.Equal(0.125, sheet.Get(3, 1)!.Value!.Value, 9);
        Assert.DoesNotContain(sheet.Rows.Values.SelectMany(r => r.Values), c => c.Text.Contains("Sales by region"));
    }

    [Fact]
    public async Task Rtf_tables_keep_spans_shading_and_alignment()
    {
        const string ruled = @"\clbrdrt\brdrs\clbrdrl\brdrs\clbrdrb\brdrs\clbrdrr\brdrs";
        var rtf = @"{\rtf1\ansi\deff0{\fonttbl{\f0 Arial;}}{\colortbl;\red255\green255\blue0;}"
                  + @"\pard Sales by region.\par"
                  + $@"\trowd\trhdr{ruled}\clmgf\cellx2000{ruled}\clmrg\cellx4000{ruled}\cellx6000"
                  + @"\pard\intbl Region\cell\cell Sales\cell\row"
                  + $@"\trowd{ruled}\clvmgf\cellx2000{ruled}\cellx4000{ruled}\clcbpat1\cellx6000"
                  + @"\pard\intbl North\cell Q1\cell\pard\intbl\qc{\b 1,234.50}\cell\row"
                  + $@"\trowd{ruled}\clvmrg\cellx2000{ruled}\cellx4000{ruled}\cellx6000"
                  + @"\pard\intbl\cell Q2\cell\pard\intbl\qr 2026-03-31\cell\row"
                  + $@"\trowd{ruled}\cellx2000{ruled}\cellx4000{ruled}\cellx6000"
                  + @"\pard\intbl 007\cell 12.5%\cell x\cell\row"
                  + $@"\trowd{ruled}\clmgf\cellx2000{ruled}\clmrg\cellx4000{ruled}\clmrg\cellx6000"
                  + @"\pard\intbl First line of a note\par second line\par third line\cell\cell\cell\row"
                  + @"\pard After the table.\par}";
        var path = Path.Combine(fx.NewFolder(), "sales.rtf");
        File.WriteAllText(path, rtf);
        var sheet = Assert.Single((await ConvertAsync(path)).Sheets);

        AssertWordTable(sheet, "rtf");
        Assert.Equal(0.125, sheet.Get(3, 1)!.Value!.Value, 9);
    }

    [Fact]
    public async Task Doc_tables_become_sheets_too()
    {
        var single = DocBuilder.Brc80(4, 1);
        var borders = DocBuilder.Sprm(0xD605, single, single, single, single, single, single);
        short[] grid = [0, 2000, 4000, 6000];
        // Cell flags: 1 / 2 start and continue a merge across, 3 << 5 / 1 << 5 one down.
        var header = (byte[])[.. DocBuilder.DefTable(grid, [1, 2, 0]), .. borders, .. DocBuilder.Sprm(0x3404, (byte)1)];
        var first = (byte[])[.. DocBuilder.DefTable(grid, [3 << 5, 0, 0]), .. borders,
            // SHD per cell (foreground, background, pattern): automatic for the first two, yellow behind the third.
            .. DocBuilder.Sprm(0xD612, (byte[])[0, 0, 0, 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0x00, 0, 0, 0])];
        var second = (byte[])[.. DocBuilder.DefTable(grid, [1 << 5, 0, 0]), .. borders];
        var plain = (byte[])[.. DocBuilder.DefTable(grid), .. borders];
        var builder = new DocBuilder()
            .Paragraph(DocBuilder.R("Sales by region."))
            .Row(header, [DocBuilder.R("Region")], [], [DocBuilder.R("Sales")])
            .Row(first, [DocBuilder.R("North")], [DocBuilder.R("Q1")], [DocBuilder.R("1,234.50", DocBuilder.Sprm(0x0835, (byte)1))])
            .Row(second, [], [DocBuilder.R("Q2")], [DocBuilder.R("2026-03-31")])
            .Row(plain, [DocBuilder.R("007")], [DocBuilder.R("12.5%")], [DocBuilder.R("x")])
            .Paragraph(DocBuilder.R("After the table."));
        var path = Path.Combine(fx.NewFolder(), "sales.doc");
        File.WriteAllBytes(path, builder.Build());
        var sheet = Assert.Single((await ConvertAsync(path)).Sheets);

        Assert.Equal("Region", sheet.Get(0, 0)!.Text);
        Assert.Contains(new CellRange(0, 0, 0, 1), sheet.Merges);
        Assert.Contains(new CellRange(1, 0, 2, 0), sheet.Merges);
        var amount = sheet.Get(1, 2)!;
        Assert.Equal(1234.5, amount.Value);
        Assert.True(amount.Style.Bold);
        Assert.Equal("#FFFF00", amount.Style.Fill, ignoreCase: true);
        Assert.Null(sheet.Get(1, 1)!.Style.Fill);
        Assert.Equal(new DateOnly(2026, 3, 31).DayNumber - Day0, sheet.Get(2, 2)!.Value);
        Assert.Null(sheet.Get(3, 0)!.Value);
        Assert.Equal(0.125, sheet.Get(3, 1)!.Value!.Value, 9);
        Assert.Equal("thin", sheet.Get(3, 2)!.Style.Bottom.Style);
        Assert.Equal(1, sheet.FrozenRows);
        Assert.True(sheet.ColumnWidth(0) > Worksheet.StandardColumnWidth); // 2000 twips from the cell boundaries
        Assert.DoesNotContain(sheet.Rows.Values.SelectMany(r => r.Values), c => c.Text.Contains("After the table"));
    }

    [Fact]
    public void Only_the_document_itself_reaches_a_spreadsheet_through_its_tables()
    {
        var planner = fx.Catalog.CreatePlanner();
        foreach (var source in new[] { "docx", "docm", "doc", "dot", "wps", "odt", "rtf", "hwp", "hwpx", "html", "mhtml" })
            foreach (var target in new[] { "xlsx", "ods", "csv" })
                Assert.Equal("document-tables", Assert.Single(planner.Plan(source, target)!.Steps).Converter.Id);
        // A Word 6.0 / 95 file with a .doc name is looked at first, as on DOC's other routes.
        Assert.True(Assert.Single(planner.Plan("doc", "xlsx")!.Steps).Checked);
        Assert.False(Assert.Single(planner.Plan("hwp", "xlsx")!.Steps).Checked);
        // Files that become a DOCX or HTML on the way (a PDF, a picture, Markdown, an e-book, slides) don't, even with
        // every optional engine installed, and a document's tables don't go on to JSON.
        var everything = fx.Catalog.CreatePlanner([.. fx.Catalog.All.Select(c => c.Id)]);
        foreach (var source in new[] { "pdf", "jpg", "md", "epub", "pptx" })
            foreach (var target in new[] { "xlsx", "ods", "csv", "json" })
                Assert.DoesNotContain(everything.Plan(source, target)?.Steps ?? [], s => s.Converter.Id == "document-tables");
        foreach (var source in new[] { "docx", "hwpx", "odt" })
            foreach (var target in new[] { "json", "xml", "tsv" })
                Assert.DoesNotContain(everything.Plan(source, target)?.Steps ?? [], s => s.Converter.Id == "document-tables");
    }

    // ───────────────────────── Hostile tables ─────────────────────────

    [Fact]
    public async Task A_span_over_millions_of_cells_is_refused_at_once()
    {
        // 700 rows of one cell spanning the widest grid: 11.5 million positions, from a file of a few kilobytes.
        var docx = new DocxBuilder();
        docx.Paragraph(Table([2000], string.Concat(Enumerable.Range(0, 700).Select(i => Row(Cell($"row {i}", "<w:gridSpan w:val=\"16384\"/>"))))));
        var path = docx.Save(Path.Combine(fx.NewFolder(), "wide.docx"));
        var watch = Stopwatch.StartNew();
        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "xlsx" });

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("error.document_too_large", job.Files[0].ErrorKey);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
    }

    /// <summary>A table of one cell holding <paramref name="text"/> and then <paramref name="inner"/>.</summary>
    private static HTable Box(string text, HTable? inner = null)
    {
        var paragraph = new HParagraph();
        paragraph.Inlines.Add(new HText(text, default));
        var cell = new HCell();
        cell.Blocks.Add(paragraph);
        if (inner is not null)
            cell.Blocks.Add(inner);
        var row = new HRow();
        row.Cells.Add(cell);
        var table = new HTable { ColumnCount = 1 };
        table.Rows.Add(row);
        return table;
    }

    /// <summary>A table of one row: a cell of <paramref name="first"/> (then <paramref name="inner"/>) and one of <paramref name="second"/>.</summary>
    private static HTable Pair(string first, string second, HBlock? inner = null)
    {
        var table = Box(first);
        if (inner is not null)
            table.Rows[0].Cells[0].Blocks.Add(inner);
        table.Rows[0].Cells.Add(Spanned(second));
        table.ColumnCount = 2;
        return table;
    }

    /// <summary>A paragraph holding a text box of <paramref name="blocks"/>.</summary>
    private static HParagraph InTextBox(params HBlock[] blocks)
    {
        var box = new HTextBox();
        box.Blocks.AddRange(blocks);
        var paragraph = new HParagraph();
        paragraph.Inlines.Add(box);
        return paragraph;
    }

    private static HDocument Document(params HBlock[] blocks)
    {
        var section = new HSection();
        section.Blocks.AddRange(blocks);
        var document = new HDocument();
        document.Sections.Add(section);
        return document;
    }

    [Fact]
    public void Deeply_nested_tables_are_walked_without_recursion()
    {
        // Far deeper than any reader lets through (DocumentLimits.MaxNestingDepth): each level is a sheet, every other
        // one in a text box in its cell.
        HTable? table = null;
        for (var level = 5000; level > 0; level--)
            table = Pair($"level {level}", "x", table is null ? null : level % 2 == 0 ? InTextBox(table) : table);
        var watch = Stopwatch.StartNew();
        var book = DocumentTablesConverter.Build(Document(table!), TestContext.Current.CancellationToken);

        Assert.Equal(5000, book.Sheets.Count);
        Assert.Equal("level 1", book.Sheets[0].Get(0, 0)!.Text);
        Assert.Equal("level 5000", book.Sheets[^1].Get(0, 0)!.Text);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
    }

    [Fact]
    public void More_tables_or_cells_than_a_workbook_takes_are_refused()
    {
        var tables = Enumerable.Range(0, DocumentTablesConverter.MaxSheets + 1).Select(i => (HBlock)Pair($"t{i}", "x")).ToArray();
        Assert.Equal("error.document_too_large",
            Assert.Throws<ConversionRefusedException>(() => DocumentTablesConverter.Build(Document(tables), TestContext.Current.CancellationToken)).ErrorKey);

        // The tables share one budget of cells: eleven tables of a million positions each, none too large alone.
        var wide = Enumerable.Range(0, 11).Select(_ =>
        {
            var table = Box("wide");
            table.Rows[0].Cells[0].ColSpan = 16_384;
            for (var r = 1; r < 62; r++)
            {
                var row = new HRow();
                row.Cells.Add(new HCell { ColSpan = 16_384 });
                table.Rows.Add(row);
            }
            return (HBlock)table;
        }).ToArray();
        var watch = Stopwatch.StartNew();
        Assert.Equal("error.document_too_large",
            Assert.Throws<ConversionRefusedException>(() => DocumentTablesConverter.Build(Document(wide), TestContext.Current.CancellationToken)).ErrorKey);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Cells_past_the_sheet_edge_are_cut_not_refused()
    {
        // A cell naming a column two billion along goes to the sheet's last column; one with no free column before the
        // edge is left out, as the spreadsheet readers leave out cells past it.
        var table = Box("first");
        var far = Box("far").Rows[0].Cells[0];
        far.Column = 2_000_000_000;
        table.Rows[0].Cells.Add(far);
        table.Rows[0].Cells.Add(Box("beyond").Rows[0].Cells[0]);
        var sheet = DocumentTablesConverter.Build(Document(table)).Sheets.Single();

        Assert.Equal("first", sheet.Get(0, 0)!.Text);
        Assert.Equal("far", sheet.Get(0, SheetPagination.MaxColumn)!.Text);
        Assert.DoesNotContain(sheet.Rows.Values.SelectMany(r => r.Values), c => c.Text == "beyond");

        // Rows past the sheet's last row are left out and a merge ends there (placed near the end of the sheet).
        var tall = Box("a");
        tall.Rows[0].Cells[0].RowSpan = 3;
        for (var r = 1; r < 5; r++)
            tall.Rows.Add(Box($"row {r}").Rows[0]);
        var end = new Worksheet("end");
        var next = TableSheets.Place(end, tall, SheetPagination.MaxRow - 1, 0, drawn: false);

        Assert.Equal(SheetPagination.MaxRow + 1, next);
        Assert.Equal(new CellRange(SheetPagination.MaxRow - 1, 0, SheetPagination.MaxRow, 0), Assert.Single(end.Merges));
        Assert.True(end.Rows.Keys.All(r => r <= SheetPagination.MaxRow));
    }
}
