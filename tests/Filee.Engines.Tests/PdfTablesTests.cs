// PDF → XLSX (#267): each table drawn with lines on its own sheet, with numbers and dates as real values, merged
// cells and column widths, a table going on over the next page joined, and a clear failure when there is no table.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Office.Sheets;
using Filee.Engines.Pdf;

namespace Filee.Engines.Tests;

public class PdfTablesTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>A grid of rules with text in its cells; a null cell is covered by the one to its left (no rule between).</summary>
    private static void Table(PdfBuilder pdf, double top, double[] xs, string?[][] rows, bool boldHeader = true)
    {
        var y = top;
        foreach (var row in rows)
        {
            pdf.Rule(xs[0], y, xs[^1], y);
            for (var c = 0; c < row.Length; c++)
            {
                if (c == 0 || row[c] is not null)
                    pdf.Rule(xs[c], y, xs[c], y + 20);
                if (row[c] is { } text)
                    pdf.Line(text, 10, pdf.Font(bold: boldHeader && y == top), xs[c] + 4, y + 14);
            }
            pdf.Rule(xs[^1], y, xs[^1], y + 20);
            y += 20;
        }
        pdf.Rule(xs[0], y, xs[^1], y);
    }

    private async Task<Workbook> ConvertAsync(string pdf)
    {
        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "xlsx" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return XlsxReader.Read(job.Outputs.Single());
    }

    [Fact]
    public async Task Numbers_dates_and_merged_cells_reach_the_sheet()
    {
        var pdf = new PdfBuilder().NewPage();
        pdf.Paragraph("Sales by quarter, as reported.", 11, pdf.Font(), 50, 80, 495, 14);
        Table(pdf, 110, [50, 200, 350, 545],
        [
            ["Item", "Amount", "Date"],
            ["Paper", "1,234.50", "2026-10-23"],
            ["Ink", "(120)", "2026/1/5"],
            ["Share", "12.5%", "007"],
            ["Total for the year", null, "$9.99"],
        ]);
        var book = await ConvertAsync(pdf.Save(Path.Combine(fx.NewFolder(), "sales.pdf")));

        var sheet = Assert.Single(book.Sheets);
        Assert.Equal("Item", sheet.Get(0, 0)!.Text);
        Assert.Equal((1234.5, "#,##0.00"), (sheet.Get(1, 1)!.Value!.Value, sheet.Get(1, 1)!.NumberFormat));
        Assert.Equal(-120, sheet.Get(2, 1)!.Value);
        Assert.Equal(0.125, sheet.Get(3, 1)!.Value!.Value, 9);
        Assert.Equal(new DateOnly(2026, 10, 23).DayNumber - new DateOnly(1899, 12, 30).DayNumber, sheet.Get(1, 2)!.Value);
        Assert.Equal(new DateOnly(2026, 1, 5).DayNumber - new DateOnly(1899, 12, 30).DayNumber, sheet.Get(2, 2)!.Value);
        Assert.Null(sheet.Get(3, 2)!.Value); // a code with a leading zero stays text
        Assert.Equal("007", sheet.Get(3, 2)!.Text);
        Assert.Equal(9.99, sheet.Get(4, 2)!.Value);
        Assert.Contains(new CellRange(4, 0, 4, 1), sheet.Merges);
        Assert.Null(sheet.Get(0, 0)!.Value); // the paragraph above the table isn't on the sheet
        Assert.DoesNotContain(sheet.Rows.Values.SelectMany(r => r.Values), c => c.Text.Contains("Sales by quarter"));
        // Column widths follow the PDF: the last column is the widest.
        Assert.True(sheet.ColumnWidth(2) > sheet.ColumnWidth(0));
    }

    [Fact]
    public async Task A_table_going_on_over_the_next_page_is_one_sheet_and_another_table_its_own()
    {
        double[] xs = [50, 250, 545];
        var pdf = new PdfBuilder().NewPage();
        Table(pdf, 600, xs, [["Name", "Size"], ["a.pdf", "10"], ["b.pdf", "20"]]);
        pdf.NewPage();
        Table(pdf, 60, xs, [["Name", "Size"], ["c.pdf", "30"]]); // header repeated on the new page
        Table(pdf, 300, [50, 150, 300, 545], [["Code", "City", "Country"], ["SEL", "Seoul", "Korea"]]);
        var book = await ConvertAsync(pdf.Save(Path.Combine(fx.NewFolder(), "files.pdf")));

        Assert.Equal(2, book.Sheets.Count);
        var files = book.Sheets[0];
        Assert.Equal(["Name", "a.pdf", "b.pdf", "c.pdf"], Enumerable.Range(0, 4).Select(r => files.Get(r, 0)!.Text));
        Assert.Equal(30, files.Get(3, 1)!.Value);
        Assert.Null(files.Get(4, 0));
        Assert.Equal("Seoul", book.Sheets[1].Get(1, 1)!.Text);
    }

    [Fact]
    public async Task Text_between_two_tables_keeps_them_on_their_own_sheets()
    {
        double[] xs = [50, 250, 545];
        var pdf = new PdfBuilder().NewPage();
        Table(pdf, 60, xs, [["Name", "Size"], ["a.pdf", "10"]]);
        pdf.Paragraph("These are this week's files. The next table lists the files kept from last week.", 11, pdf.Font(), 50, 200, 495, 14);
        pdf.NewPage();
        Table(pdf, 60, xs, [["Kept", "Days"], ["b.pdf", "7"]]);
        var book = await ConvertAsync(pdf.Save(Path.Combine(fx.NewFolder(), "two.pdf")));

        Assert.Equal(2, book.Sheets.Count);
        Assert.Equal("Kept", book.Sheets[1].Get(0, 0)!.Text);
    }

    [Fact]
    public void Only_a_pdf_reaches_a_spreadsheet_through_its_tables()
    {
        var planner = fx.Catalog.CreatePlanner();
        // A picture made into a PDF has no ruled tables: no image → spreadsheet route through PDF, even with every
        // optional engine installed.
        var everything = fx.Catalog.CreatePlanner([.. fx.Catalog.All.Select(c => c.Id)]);
        foreach (var image in new[] { "jpg", "png", "webp", "heic", "psd", "tiff" })
            foreach (var target in new[] { "xlsx", "ods", "csv", "json" })
                Assert.Null(everything.Plan(image, target));
        foreach (var target in new[] { "xlsx", "ods", "csv" })
            Assert.Equal("pdf-tables", Assert.Single(planner.Plan("pdf", target)!.Steps).Converter.Id);
    }

    [Fact]
    public async Task Csv_and_ods_are_written_from_the_same_tables()
    {
        var pdf = new PdfBuilder().NewPage();
        Table(pdf, 60, [50, 250, 545], [["Name", "Size"], ["a.pdf", "1,234"]]);
        var path = pdf.Save(Path.Combine(fx.NewFolder(), "one.pdf"));

        var csv = await fx.ConvertAsync([path], new Preset { TargetFormat = "csv" });
        var ods = await fx.ConvertAsync([path], new Preset { TargetFormat = "ods" });

        Assert.Equal(JobState.Completed, csv.State);
        Assert.Contains("a.pdf", await File.ReadAllTextAsync(csv.Outputs.Single(), TestContext.Current.CancellationToken));
        Assert.Equal(JobState.Completed, ods.State);
        Assert.EndsWith(".ods", ods.Outputs.Single());
    }

    [Fact]
    public async Task A_pdf_without_tables_fails_with_a_message()
    {
        var pdf = new PdfBuilder().NewPage();
        pdf.Paragraph("Only a letter, no table here.", 11, pdf.Font(), 50, 80, 495, 14);

        var job = await fx.ConvertAsync([pdf.Save(Path.Combine(fx.NewFolder(), "letter.pdf"))], new Preset { TargetFormat = "xlsx" });

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("error.pdf_no_tables", job.Files[0].ErrorKey);
    }

    [Theory]
    [InlineData("1,234", 1234d, "#,##0")]
    [InlineData("-7", -7d, "0")]
    [InlineData("0.5", 0.5, "0.0")]
    [InlineData("€1,000.00", 1000d, "\"€\"#,##0.00")]
    [InlineData("(1,200.50)", -1200.5, "#,##0.00;(#,##0.00)")]
    [InlineData("50%", 0.5, "0%")]
    public void Numbers_keep_their_shown_format(string text, double value, string format)
    {
        var number = TableSheets.Number(text);
        Assert.NotNull(number);
        Assert.Equal(value, number.Value.Value, 9);
        Assert.Equal(format, number.Value.Format);
    }

    [Theory]
    [InlineData("012")]
    [InlineData("1,23")]
    [InlineData("1.234,50")]
    [InlineData("12 KB")]
    [InlineData("(5")]
    [InlineData("1234567890123456")]
    [InlineData("10/23/2026")]
    public void Other_text_stays_text(string text)
    {
        Assert.Null(TableSheets.Number(text));
        Assert.Null(TableSheets.Date(text));
    }

    [Theory]
    [InlineData("2026-10-23", "yyyy-mm-dd")]
    [InlineData("2026/1/5", "yyyy/m/d")]
    [InlineData("2026. 10. 23.", "yyyy. mm. dd.")]
    public void Dates_with_the_year_first_become_dates(string text, string format)
    {
        var date = TableSheets.Date(text);
        Assert.NotNull(date);
        Assert.Equal(format, date.Value.Format);
        Assert.Null(TableSheets.Date("2026-02-30")); // not a day
    }

    // ───────────────────────── Merged cells: Smart, Keep, Fill ─────────────────────────

    /// <summary>A cell of <see cref="Spanned"/>: where it starts, how many rows and columns it covers, its text.</summary>
    private sealed record Span(int Row, int Column, int Rows, int Columns, string Text);

    /// <summary>
    /// A ruled table whose cells may span rows and columns: a rule is drawn between two grid positions only where they
    /// belong to different cells, as a PDF made from a spreadsheet or a report draws it.
    /// </summary>
    private static void Spanned(PdfBuilder pdf, double top, double[] xs, int rowCount, Span[] cells, int boldRows = 0)
    {
        const double height = 20;
        var owner = new int[rowCount, xs.Length - 1];
        for (var i = 0; i < cells.Length; i++)
            for (var r = cells[i].Row; r < cells[i].Row + cells[i].Rows; r++)
                for (var c = cells[i].Column; c < cells[i].Column + cells[i].Columns; c++)
                    owner[r, c] = i + 1;
        for (var r = 0; r <= rowCount; r++)
            for (var c = 0; c < xs.Length - 1; c++)
                if (r == 0 || r == rowCount || owner[r - 1, c] != owner[r, c])
                    pdf.Rule(xs[c], top + r * height, xs[c + 1], top + r * height);
        for (var r = 0; r < rowCount; r++)
            for (var c = 0; c < xs.Length; c++)
                if (c == 0 || c == xs.Length - 1 || owner[r, c - 1] != owner[r, c])
                    pdf.Rule(xs[c], top + r * height, xs[c], top + (r + 1) * height);
        foreach (var cell in cells)
            pdf.Line(cell.Text, 9, pdf.Font(bold: cell.Row < boldRows), xs[cell.Column] + 3, top + cell.Row * height + 13);
    }

    /// <summary>
    /// The standard case: a two-level header ("Q1 2026" over Jan, Feb, Mar beside "Category", "Item" and "Total" that
    /// span both header rows), categories spanning their items' rows, and a "Grand total" label across two columns.
    /// </summary>
    private string SalesReport()
    {
        var pdf = new PdfBuilder().NewPage();
        pdf.Paragraph("Branch sales, first quarter.", 11, pdf.Font(), 50, 70, 495, 14);
        Spanned(pdf, 100, [50, 130, 250, 310, 370, 430, 510], 8,
        [
            new(0, 0, 2, 1, "Category"), new(0, 1, 2, 1, "Item"), new(0, 2, 1, 3, "Q1 2026"), new(0, 5, 2, 1, "Total"),
            new(1, 2, 1, 1, "Jan"), new(1, 3, 1, 1, "Feb"), new(1, 4, 1, 1, "Mar"),
            new(2, 0, 3, 1, "Glass"), new(2, 1, 1, 1, "Clear 6mm"), new(2, 2, 1, 1, "120"), new(2, 3, 1, 1, "140"), new(2, 4, 1, 1, "160"), new(2, 5, 1, 1, "420"),
            new(3, 1, 1, 1, "Tinted 8mm"), new(3, 2, 1, 1, "80"), new(3, 3, 1, 1, "75"), new(3, 4, 1, 1, "90"), new(3, 5, 1, 1, "245"),
            new(4, 1, 1, 1, "Mirror 4mm"), new(4, 2, 1, 1, "30"), new(4, 3, 1, 1, "45"), new(4, 4, 1, 1, "40"), new(4, 5, 1, 1, "115"),
            new(5, 0, 2, 1, "Aluminium"), new(5, 1, 1, 1, "Profile A"), new(5, 2, 1, 1, "1,200"), new(5, 3, 1, 1, "1,150"), new(5, 4, 1, 1, "1,300"), new(5, 5, 1, 1, "3,650"),
            new(6, 1, 1, 1, "Profile B"), new(6, 2, 1, 1, "600"), new(6, 3, 1, 1, "640"), new(6, 4, 1, 1, "610"), new(6, 5, 1, 1, "1,850"),
            new(7, 0, 1, 2, "Grand total"), new(7, 2, 1, 1, "2,030"), new(7, 3, 1, 1, "2,050"), new(7, 4, 1, 1, "2,200"), new(7, 5, 1, 1, "6,280"),
        ], boldRows: 2);
        return pdf.Save(Path.Combine(fx.NewFolder(), "sales-report.pdf"));
    }

    private async Task<Worksheet> SheetAsync(string pdf, MergedCells merged)
    {
        var preset = new Preset { TargetFormat = "xlsx", Document = { MergedCells = merged } };
        var job = await fx.ConvertAsync([pdf], preset);
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return Assert.Single(XlsxReader.Read(job.Outputs.Single()).Sheets);
    }

    [Fact]
    public async Task Smart_keeps_the_header_and_across_column_merges_and_fills_the_categories_down()
    {
        var sheet = await SheetAsync(SalesReport(), MergedCells.Smart);

        Assert.Equal(
            new[] { new CellRange(0, 0, 1, 0), new CellRange(0, 1, 1, 1), new CellRange(0, 2, 0, 4), new CellRange(0, 5, 1, 5), new CellRange(7, 0, 7, 1) }
                .OrderBy(r => r.ToString()),
            sheet.Merges.OrderBy(r => r.ToString()));
        // Every item row names its category, so filters and pivot tables read it.
        foreach (var row in new[] { 2, 3, 4 })
            Assert.Equal("Glass", sheet.Get(row, 0)!.Text);
        foreach (var row in new[] { 5, 6 })
            Assert.Equal("Aluminium", sheet.Get(row, 0)!.Text);
        Assert.Equal("Q1 2026", sheet.Get(0, 2)!.Text);
        Assert.Equal("", sheet.Get(0, 3)?.Text ?? "");
        Assert.Equal("Feb", sheet.Get(1, 3)!.Text);
        Assert.Equal(1150, sheet.Get(5, 3)!.Value);
        Assert.Equal(6280, sheet.Get(7, 5)!.Value);
        // The two header rows stay in view.
        Assert.Equal(2, sheet.FrozenRows);
    }

    [Fact]
    public async Task Keep_merges_every_cell_as_the_pdf_draws_it()
    {
        var sheet = await SheetAsync(SalesReport(), MergedCells.Keep);

        Assert.Contains(new CellRange(2, 0, 4, 0), sheet.Merges);
        Assert.Contains(new CellRange(5, 0, 6, 0), sheet.Merges);
        Assert.Contains(new CellRange(0, 2, 0, 4), sheet.Merges);
        Assert.Equal(7, sheet.Merges.Count);
        Assert.Equal("", sheet.Get(3, 0)?.Text ?? "");
    }

    [Fact]
    public async Task Fill_leaves_no_merged_cell_and_repeats_each_value()
    {
        var sheet = await SheetAsync(SalesReport(), MergedCells.Fill);

        Assert.Empty(sheet.Merges);
        Assert.Equal("Q1 2026", sheet.Get(0, 4)!.Text);
        Assert.Equal("Total", sheet.Get(1, 5)!.Text);
        Assert.Equal("Glass", sheet.Get(4, 0)!.Text);
        Assert.Equal("Grand total", sheet.Get(7, 1)!.Text);
    }

    [Fact]
    public async Task Smart_without_a_two_level_header_freezes_one_row_and_a_title_across_the_table_keeps_its_merge()
    {
        var pdf = new PdfBuilder().NewPage();
        Spanned(pdf, 100, [50, 200, 350, 500], 5,
        [
            new(0, 0, 1, 3, "Price list, November 2026"),
            new(1, 0, 1, 1, "Group"), new(1, 1, 1, 1, "Item"), new(1, 2, 1, 1, "Price"),
            new(2, 0, 3, 1, "Hinges"), new(2, 1, 1, 1, "Butt hinge"), new(2, 2, 1, 1, "450.00"),
            new(3, 1, 1, 1, "Bar hinge"), new(3, 2, 1, 1, "1,250.00"),
            new(4, 1, 1, 1, "Pivot hinge"), new(4, 2, 1, 1, "2,100.00"),
        ]);
        var sheet = await SheetAsync(pdf.Save(Path.Combine(fx.NewFolder(), "price-list.pdf")), MergedCells.Smart);

        Assert.Equal(new[] { new CellRange(0, 0, 0, 2) }, sheet.Merges);
        Assert.Equal(2, sheet.FrozenRows); // the title and the column headings
        Assert.Equal("Hinges", sheet.Get(4, 0)!.Text);
        Assert.Equal(2100, sheet.Get(4, 2)!.Value);
    }

    /// <summary>The review's case: a region's total merged down beside its items, which filling would count twice.</summary>
    private static void RegionTotals(PdfBuilder pdf, double top, bool header, string region, string[] items, string[] sales, string total)
    {
        var cells = new List<Span>();
        var first = 0;
        if (header)
        {
            cells.AddRange([new(0, 0, 1, 1, "Region"), new(0, 1, 1, 1, "Item"), new(0, 2, 1, 1, "Sales"), new(0, 3, 1, 1, "Region total")]);
            first = 1;
        }
        cells.Add(new(first, 0, items.Length, 1, region));
        cells.Add(new(first, 3, items.Length, 1, total));
        for (var i = 0; i < items.Length; i++)
            cells.AddRange([new(first + i, 1, 1, 1, items[i]), new(first + i, 2, 1, 1, sales[i])]);
        Spanned(pdf, top, [50, 150, 250, 350, 500], first + items.Length, [.. cells], boldRows: first);
    }

    [Fact]
    public async Task Smart_keeps_a_total_merged_down_so_the_column_adds_up_as_drawn()
    {
        var pdf = new PdfBuilder().NewPage();
        RegionTotals(pdf, 100, header: true, "North", ["A", "B"], ["100", "200"], "300");
        var path = pdf.Save(Path.Combine(fx.NewFolder(), "region-totals.pdf"));

        var smart = await SheetAsync(path, MergedCells.Smart);
        Assert.Contains(new CellRange(1, 3, 2, 3), smart.Merges);
        Assert.Null(smart.Get(2, 3)?.Value);
        Assert.Equal("North", smart.Get(2, 0)!.Text); // the text beside it is still filled
        Assert.Equal(300, ColumnSum(smart, 3));
        Assert.Equal(300, ColumnSum(await SheetAsync(path, MergedCells.Keep), 3));
        // Unmerge and fill says it repeats every value, so it may count it twice.
        Assert.Equal(600, ColumnSum(await SheetAsync(path, MergedCells.Fill), 3));
    }

    [Fact]
    public async Task A_table_going_on_over_the_next_page_keeps_its_totals_merged_too()
    {
        var pdf = new PdfBuilder().NewPage();
        RegionTotals(pdf, 700, header: true, "North", ["A", "B"], ["100", "200"], "300");
        pdf.NewPage();
        RegionTotals(pdf, 60, header: false, "West", ["C", "D", "E"], ["10", "20", "30"], "60");
        var sheet = await SheetAsync(pdf.Save(Path.Combine(fx.NewFolder(), "two-pages.pdf")), MergedCells.Smart);

        // One sheet: the part on the second page has no header rows of its own, and its total stays merged as well.
        Assert.Equal("West", sheet.Get(5, 0)!.Text);
        Assert.Contains(new CellRange(3, 3, 5, 3), sheet.Merges);
        Assert.DoesNotContain(new CellRange(3, 0, 5, 0), sheet.Merges);
        Assert.Equal(360, ColumnSum(sheet, 3));
        Assert.Equal(1, sheet.FrozenRows);
    }

    private static double ColumnSum(Worksheet sheet, int column) =>
        sheet.Rows.Values.Select(r => r.GetValueOrDefault(column)?.Value ?? 0).Sum();

    [Fact]
    public async Task Smart_reads_a_category_in_the_first_row_of_an_unmarked_table_as_body()
    {
        // No heading across columns: the first row is the only header row, even though its category reaches down.
        var pdf = new PdfBuilder().NewPage();
        Spanned(pdf, 100, [50, 150, 300, 400], 5,
        [
            new(0, 0, 3, 1, "Fruit"), new(0, 1, 1, 1, "Apple"), new(0, 2, 1, 1, "3"),
            new(1, 1, 1, 1, "Pear"), new(1, 2, 1, 1, "4"),
            new(2, 1, 1, 1, "Plum"), new(2, 2, 1, 1, "5"),
            new(3, 0, 2, 1, "Veg"), new(3, 1, 1, 1, "Leek"), new(3, 2, 1, 1, "6"),
            new(4, 1, 1, 1, "Kale"), new(4, 2, 1, 1, "7"),
        ]);
        var sheet = await SheetAsync(pdf.Save(Path.Combine(fx.NewFolder(), "unmarked.pdf")), MergedCells.Smart);

        Assert.Equal(1, sheet.FrozenRows); // not the three rows "Fruit" covers
        Assert.Equal("Veg", sheet.Get(4, 0)!.Text);
        Assert.DoesNotContain(new CellRange(3, 0, 4, 0), sheet.Merges);
    }

    [Fact]
    public async Task A_page_ending_with_only_the_header_still_freezes_it()
    {
        // Page 1 ends with the two heading rows ("Sales" over Jan and Feb); the body is on page 2.
        double[] xs = [50, 150, 250, 350, 500];
        var pdf = new PdfBuilder().NewPage();
        Spanned(pdf, 760, xs, 2,
        [
            new(0, 0, 2, 1, "Region"), new(0, 1, 1, 2, "Sales"), new(0, 3, 2, 1, "Total"),
            new(1, 1, 1, 1, "Jan"), new(1, 2, 1, 1, "Feb"),
        ], boldRows: 2);
        pdf.NewPage();
        Spanned(pdf, 60, xs, 2,
        [
            new(0, 0, 1, 1, "West"), new(0, 1, 1, 1, "10"), new(0, 2, 1, 1, "20"), new(0, 3, 1, 1, "30"),
            new(1, 0, 1, 1, "East"), new(1, 1, 1, 1, "5"), new(1, 2, 1, 1, "6"), new(1, 3, 1, 1, "11"),
        ]);
        var sheet = await SheetAsync(pdf.Save(Path.Combine(fx.NewFolder(), "header-alone.pdf")), MergedCells.Smart);

        Assert.Equal("Region", sheet.Get(0, 0)!.Text);
        Assert.Equal("East", sheet.Get(3, 0)!.Text); // one sheet: the body went on from the heading
        Assert.Equal(2, sheet.FrozenRows);
    }
}
