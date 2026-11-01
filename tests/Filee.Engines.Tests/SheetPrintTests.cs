// Spreadsheets printed to PDF by Filee's own engine (SheetPrinter): Excel's page logic for workbooks written here in
// code. The expected page counts and breaks follow from Excel's units: with Calibri 11 (the Normal font) a default
// column is 64 px (48 pt) and a default row 20 px (15 pt); A4 with the Normal margins (0.7 in, 0.75 in) leaves
// 494.5 × 733.9 pt, so a page holds 10 default columns and 48 default rows. Set FILEE_RENDER_OUT to a folder to keep
// the PDFs and pictures of their pages.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Documents;
using Filee.Engines.Office.Sheets;
using SkiaSharp;
using UglyToad.PdfPig;
using static Filee.Engines.Tests.XlsxBuilder;

namespace Filee.Engines.Tests;

public sealed class SheetPrintTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>A4 portrait as Excel's paperSize 9, so the expectations don't depend on this PC's region.</summary>
    private const string A4 = "<pageSetup paperSize=\"9\" orientation=\"portrait\"/>";

    private static readonly DateTime Now = new(2026, 11, 1, 9, 30, 0);

    // Cell formats (cellXfs index) of the Calibri style sheet.
    private const int Plain = 0;
    private const int Boxed = 1;     // bold on a yellow fill, thin borders all round
    private const int Wrapped = 2;
    private const int Rotated = 3;   // 45° counter-clockwise
    private const int Stacked = 4;
    private const int Shrunk = 5;
    private const int Indented = 6;  // left, indent 2
    private const int Across = 7;    // centre across selection
    private const int Decimals = 8;  // 0.00
    private const int Large = 9;     // Calibri 20
    private const int Pattern = 10;  // dark grey pattern, red on white
    private const int Diagonal = 11; // both diagonals
    private const int Centered = 12;
    private const int Gradient = 13; // white to blue, left to right

    /// <summary>A style sheet with Calibri 11 as the Normal font and the formats above, plus two differential formats.</summary>
    private const string CalibriStyles =
        "<numFmts count=\"0\"/>" +
        "<fonts count=\"3\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
        "<font><sz val=\"20\"/><name val=\"Calibri\"/></font></fonts>" +
        "<fills count=\"5\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFFF00\"/></patternFill></fill>" +
        "<fill><patternFill patternType=\"darkGray\"><fgColor rgb=\"FFFF0000\"/><bgColor rgb=\"FFFFFFFF\"/></patternFill></fill>" +
        "<fill><gradientFill degree=\"0\"><stop position=\"0\"><color rgb=\"FFFFFFFF\"/></stop><stop position=\"1\"><color rgb=\"FF0000FF\"/></stop></gradientFill></fill></fills>" +
        "<borders count=\"3\"><border><left/><right/><top/><bottom/><diagonal/></border>" +
        "<border><left style=\"thin\"><color auto=\"1\"/></left><right style=\"thin\"><color auto=\"1\"/></right><top style=\"thin\"><color auto=\"1\"/></top><bottom style=\"thin\"><color auto=\"1\"/></bottom><diagonal/></border>" +
        "<border diagonalUp=\"1\" diagonalDown=\"1\"><left/><right/><top/><bottom/><diagonal style=\"thin\"><color rgb=\"FF0000FF\"/></diagonal></border></borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"14\"><xf fontId=\"0\" fillId=\"0\" borderId=\"0\"/>" +
        "<xf fontId=\"1\" fillId=\"2\" borderId=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"/>" +
        "<xf fontId=\"0\" fillId=\"0\" borderId=\"0\" applyAlignment=\"1\"><alignment wrapText=\"1\" vertical=\"top\"/></xf>" +
        "<xf fontId=\"0\" fillId=\"0\" borderId=\"0\" applyAlignment=\"1\"><alignment textRotation=\"45\"/></xf>" +
        "<xf fontId=\"0\" fillId=\"0\" borderId=\"0\" applyAlignment=\"1\"><alignment textRotation=\"255\"/></xf>" +
        "<xf fontId=\"0\" fillId=\"0\" borderId=\"0\" applyAlignment=\"1\"><alignment shrinkToFit=\"1\"/></xf>" +
        "<xf fontId=\"0\" fillId=\"0\" borderId=\"0\" applyAlignment=\"1\"><alignment horizontal=\"left\" indent=\"2\"/></xf>" +
        "<xf fontId=\"0\" fillId=\"0\" borderId=\"0\" applyAlignment=\"1\"><alignment horizontal=\"centerContinuous\"/></xf>" +
        "<xf numFmtId=\"2\" fontId=\"0\" fillId=\"0\" borderId=\"0\" applyNumberFormat=\"1\"/>" +
        "<xf fontId=\"2\" fillId=\"0\" borderId=\"0\" applyFont=\"1\"/>" +
        "<xf fontId=\"0\" fillId=\"3\" borderId=\"0\" applyFill=\"1\"/>" +
        "<xf fontId=\"0\" fillId=\"0\" borderId=\"2\" applyBorder=\"1\"/>" +
        "<xf fontId=\"0\" fillId=\"0\" borderId=\"0\" applyAlignment=\"1\"><alignment horizontal=\"center\"/></xf>" +
        "<xf fontId=\"0\" fillId=\"4\" borderId=\"0\" applyFill=\"1\"/></cellXfs>" +
        "<dxfs count=\"2\"><dxf><font><color rgb=\"FF9C0006\"/></font><fill><patternFill><bgColor rgb=\"FFFFC7CE\"/></patternFill></fill></dxf>" +
        "<dxf><font><b/></font><fill><patternFill><bgColor rgb=\"FF00B050\"/></patternFill></fill></dxf></dxfs>";

    private static XlsxBuilder Book() => new() { Styles = CalibriStyles };

    /// <summary>Rows <paramref name="firstRow"/>.. with "r-c" text in columns A.. (1-based numbers).</summary>
    internal static string Grid(XlsxBuilder book, int rows, int columns, int firstRow = 1, int style = Plain)
    {
        var sb = new System.Text.StringBuilder();
        for (var r = firstRow; r < firstRow + rows; r++)
        {
            var cells = new List<string>();
            for (var c = 0; c < columns; c++)
                cells.Add(book.Text($"{SheetPageRenderer.ColumnName(c)}{r}", $"{r}-{c + 1}", style));
            sb.Append(Row(r, [.. cells]));
        }
        return sb.ToString();
    }

    private string Save(XlsxBuilder book, string name = "book.xlsx") => book.Save(Path.Combine(fx.NewFolder(), name));

    /// <summary>The printed pages' words, one string per page (and the PDF and pictures when FILEE_RENDER_OUT is set).</summary>
    private static List<string> Print(Workbook book, string name)
    {
        using var document = PdfDocument.Open(Pdf(book, name));
        return [.. document.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text)))];
    }

    private static byte[] Pdf(Workbook book, string name)
    {
        var pdf = SheetPrinter.ToPdf(book, new SheetPrintSource(name, "C:\\Reports\\"), now: Now);
        if (Environment.GetEnvironmentVariable("FILEE_RENDER_OUT") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, Path.ChangeExtension(name, ".pdf")), pdf);
            using var printed = Printed(book, name);
            for (var i = 0; i < printed.Pages.Count; i++)
            {
                using var bitmap = printed.Pages[i].Render(100);
                using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)}-{i + 1}.png"), png.ToArray());
            }
        }
        return pdf;
    }

    private static SheetPrinter.Printed Printed(Workbook book, string name = "book.xlsx") =>
        SheetPrinter.Print(book, new SheetPrintSource(name, "C:\\Reports\\"), DocumentLimits.Default, Now, null, default);

    private List<string> Print(string path) => Print(SpreadsheetConverter(path), Path.GetFileName(path));

    private static Workbook SpreadsheetConverter(string path) =>
        Filee.Engines.Office.SpreadsheetConverter.Read(path, Path.GetExtension(path).TrimStart('.').ToLowerInvariant());

    /// <summary>The colour of page <paramref name="page"/> at a point in PDF points from its top left.</summary>
    private static SKColor Pixel(Workbook book, int page, float x, float y)
    {
        using var printed = Printed(book);
        using var bitmap = printed.Pages[page].Render(72);
        return bitmap.GetPixel((int)x, (int)y);
    }

    // ───────────────────────── Page breaks ─────────────────────────

    [Fact]
    public void A_sheet_of_default_cells_breaks_after_ten_columns_and_48_rows()
    {
        var book = Book();
        book.Sheet("Data", Grid(book, 100, 12), after: A4);

        var pages = Print(Save(book, "grid.xlsx"));

        // Down, then over: rows 1–48, 49–96, 97–100 of columns A–J, then the same of K–L.
        Assert.Equal(6, pages.Count);
        Assert.StartsWith("1-1 1-2", pages[0]);
        Assert.Contains("48-10", pages[0]);
        Assert.DoesNotContain("49-1 ", pages[0]);
        Assert.DoesNotContain("1-11", pages[0]);
        Assert.StartsWith("49-1 ", pages[1]);
        Assert.StartsWith("97-1 ", pages[2]);
        Assert.StartsWith("1-11 1-12", pages[3]);
        Assert.StartsWith("97-11 97-12", pages[5]);
    }

    [Fact]
    public void Over_then_down_prints_the_columns_of_a_row_of_pages_first()
    {
        var book = Book();
        book.Sheet("Data", Grid(book, 60, 12), after: "<pageSetup paperSize=\"9\" pageOrder=\"overThenDown\"/>");

        var pages = Print(Save(book));

        Assert.Equal(4, pages.Count);
        Assert.StartsWith("1-1 ", pages[0]);
        Assert.StartsWith("1-11 ", pages[1]);
        Assert.StartsWith("49-1 ", pages[2]);
        Assert.StartsWith("49-11 ", pages[3]);
    }

    [Fact]
    public void Manual_breaks_start_new_pages()
    {
        var book = Book();
        book.Sheet("Data", Grid(book, 30, 8), after: A4 +
            "<rowBreaks count=\"1\" manualBreakCount=\"1\"><brk id=\"10\" max=\"16383\" man=\"1\"/></rowBreaks>" +
            "<colBreaks count=\"1\" manualBreakCount=\"1\"><brk id=\"3\" max=\"1048575\" man=\"1\"/></colBreaks>");

        var pages = Print(Save(book));

        // Rows 1–10 | 11–30 and columns A–C | D–H.
        Assert.Equal(4, pages.Count);
        Assert.Contains("10-3", pages[0]);
        Assert.DoesNotContain("10-4", pages[0]);
        Assert.DoesNotContain("11-1 ", pages[0]);
        Assert.StartsWith("11-1 ", pages[1]);
        Assert.StartsWith("1-4 ", pages[2]);
    }

    [Fact]
    public void A_landscape_letter_page_holds_14_columns_and_33_rows()
    {
        var book = Book();
        book.Sheet("Data", Grid(book, 34, 15), after: "<pageSetup paperSize=\"1\" orientation=\"landscape\"/>");

        using var printed = Printed(XlsxReader.Read(Save(book)));
        var pages = Print(XlsxReader.Read(Save(book)), "letter.xlsx");

        Assert.Equal(792, printed.Pages[0].Width, 1);
        Assert.Equal(612, printed.Pages[0].Height, 1);
        Assert.Equal(4, pages.Count);
        Assert.Contains("33-14", pages[0]);
        Assert.DoesNotContain("1-15", pages[0]);
        Assert.StartsWith("34-1 ", pages[1]);
    }

    [Fact]
    public void Scale_and_fit_to_pages_change_how_much_a_page_holds()
    {
        // 50 %: 20 columns of 24 pt and 97 rows of 7.5 pt.
        var half = Book();
        half.Sheet("Data", Grid(half, 100, 21), after: "<pageSetup paperSize=\"9\" scale=\"50\"/>");
        var halfPages = Print(Save(half));
        Assert.Equal(4, halfPages.Count);
        Assert.Contains("97-20", halfPages[0]);
        Assert.DoesNotContain("98-1 ", halfPages[0]);

        // Fit 30 columns to one page wide: 34 % (30 × 48 × 0.34 = 489.6 ≤ 494.5), so 143 rows of 5.1 pt a page.
        var wide = Book();
        wide.Sheet("Data", Grid(wide, 200, 30), before: "<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>",
            after: "<pageSetup paperSize=\"9\" fitToWidth=\"1\" fitToHeight=\"0\"/>");
        var widePages = Print(Save(wide));
        Assert.Equal(2, widePages.Count);
        Assert.Contains("143-30", widePages[0]);
        Assert.StartsWith("144-1 ", widePages[1]);

        // Fit to one page: everything on it, manual breaks ignored.
        var one = Book();
        one.Sheet("Data", Grid(one, 120, 14), before: "<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>",
            after: "<pageSetup paperSize=\"9\" fitToWidth=\"1\" fitToHeight=\"1\"/><rowBreaks count=\"1\"><brk id=\"5\" man=\"1\"/></rowBreaks>");
        var onePage = Assert.Single(Print(Save(one)));
        Assert.Contains("120-14", onePage);
    }

    [Fact]
    public void Print_areas_print_only_their_cells_each_from_a_new_page()
    {
        var book = Book();
        book.Sheet("Data", Grid(book, 20, 6), after: A4);
        book.DefinedNames = "<definedNames><definedName name=\"_xlnm.Print_Area\" localSheetId=\"0\">Data!$B$2:$C$4,Data!$E$10:$F$11</definedName></definedNames>";

        var pages = Print(Save(book));

        Assert.Equal(2, pages.Count);
        Assert.Equal("2-2 2-3 3-2 3-3 4-2 4-3", pages[0]);
        Assert.Equal("10-5 10-6 11-5 11-6", pages[1]);
    }

    [Fact]
    public void The_used_range_starts_at_the_first_used_cell()
    {
        var book = Book();
        book.Sheet("Data", Grid(book, 40, 8, firstRow: 30), after: A4);

        var pages = Print(Save(book));

        // Rows 30–69: 40 rows fit one page; nothing above row 30 is printed.
        Assert.Single(pages);
    }

    [Fact]
    public void Print_titles_repeat_on_the_pages_after_them()
    {
        var book = Book();
        var header = Row(1, book.Text("A1", "Name"), book.Text("B1", "Amount"));
        book.Sheet("Data", header + Grid(book, 120, 12, firstRow: 2), after: A4);
        book.DefinedNames = "<definedNames><definedName name=\"_xlnm.Print_Titles\" localSheetId=\"0\">Data!$A:$A,Data!$1:$1</definedName></definedNames>";

        var pages = Print(Save(book));

        // The title row takes 15 pt of every page after the first: 48 rows, then 47 + the title.
        Assert.StartsWith("Name Amount", pages[0]);
        Assert.Contains("48-1 ", pages[0]);
        Assert.DoesNotContain("49-1 ", pages[0]);
        Assert.StartsWith("Name Amount 49-1 ", pages[1]);
        Assert.Contains("95-1 ", pages[1]);
        Assert.DoesNotContain("96-1 ", pages[1]);
        // Pages to the right repeat column A before their own columns (title column: 48 pt less room, 9 columns).
        var right = pages.First(p => p.Contains("2-11", StringComparison.Ordinal));
        Assert.StartsWith("Name", right);
        Assert.Contains("2-1 2-11", right);
    }

    [Fact]
    public void Hidden_rows_and_columns_are_left_out()
    {
        var book = Book();
        var rows = Grid(book, 50, 11).Replace("<row r=\"2\">", "<row r=\"2\" hidden=\"1\">", StringComparison.Ordinal);
        book.Sheet("Data", rows, before: "<cols><col min=\"3\" max=\"3\" width=\"9.140625\" hidden=\"1\"/></cols>", after: A4);

        var pages = Print(Save(book));

        // One row and one column fewer: 49 rows and 10 columns fit the first page.
        Assert.DoesNotMatch("(^| )2-1 ", pages[0]);
        Assert.DoesNotMatch("-3( |$)", pages[0]);
        Assert.Contains("49-11", pages[0]);
        Assert.Equal(2, pages.Count);
    }

    [Fact]
    public void Column_widths_and_row_heights_come_in_excel_s_units()
    {
        var book = Book();
        // 20 characters of Calibri 11: Truncate((256 × 20 + 18) / 256 × 7) = 140 px = 105 pt; four fit 494.5 pt.
        var rows = Grid(book, 30, 6).Replace("<row r=\"1\">", "<row r=\"1\" ht=\"300\" customHeight=\"1\">", StringComparison.Ordinal);
        book.Sheet("Data", rows, before: "<cols><col min=\"1\" max=\"6\" width=\"20\" customWidth=\"1\"/></cols>", after: A4);

        var pages = Print(Save(book));

        Assert.Contains("1-4", pages[0]);
        Assert.DoesNotContain("1-5", pages[0]);
        // Row 1 is 300 pt: 733.9 − 300 leaves room for 28 more rows.
        Assert.Contains("29-1 ", pages[0]);
        Assert.DoesNotContain("30-1 ", pages[0]);
    }

    [Fact]
    public void Wrapped_text_makes_its_row_taller()
    {
        var book = Book();
        var text = string.Join(' ', Enumerable.Repeat("word", 40));
        var workbook = XlsxReader.Read(SaveSheet(book, Row(1, book.Text("A1", text, Wrapped)) + Grid(book, 60, 2, firstRow: 2)));

        using var printed = Printed(workbook);
        var plan = SheetPagination.Plan(workbook, workbook.Sheets[0], new SheetIndex(workbook.Sheets[0]), printed.Fonts!, DocumentLimits.Default, 0, TestContext.Current.CancellationToken);

        // 40 words in a 64 px column: many lines of 15 pt each.
        Assert.True(plan.Grid.Height(0) >= 15 * 8, $"row 1 is {plan.Grid.Height(0)} pt");
        Assert.Equal(15, plan.Grid.Height(1), 3);
    }

    private string SaveSheet(XlsxBuilder book, string rows, string before = "", string after = A4)
    {
        book.Sheet("Data", rows, before, after);
        return Save(book);
    }

    // ───────────────────────── Page setup ─────────────────────────

    [Fact]
    public void Margins_and_centring_place_the_cells()
    {
        var book = Book();
        book.Sheet("Data", Row(1, book.Text("A1", "Corner")), after:
            "<printOptions horizontalCentered=\"1\" verticalCentered=\"1\"/><pageMargins left=\"1\" right=\"1\" top=\"2\" bottom=\"1\" header=\"0.3\" footer=\"0.3\"/>" + A4);

        using var document = PdfDocument.Open(Pdf(XlsxReader.Read(Save(book)), "centred.pdf"));
        var word = Assert.Single(document.GetPage(1).GetWords());

        // One 48 × 15 pt cell centred in the area inside 72 / 72 / 144 / 72 pt margins.
        var expectedLeft = 72 + (595.28 - 144 - 48) / 2 + 1.5;
        var expectedBottom = 841.89 - (144 + (841.89 - 216 - 15) / 2) - 15;
        Assert.InRange(word.BoundingBox.Left, expectedLeft - 1, expectedLeft + 2);
        Assert.InRange(word.BoundingBox.Bottom, expectedBottom, expectedBottom + 6);
    }

    [Fact]
    public void Headers_and_footers_fill_in_their_fields_and_number_on_across_sheets()
    {
        var book = Book();
        const string Footer = "<headerFooter><oddHeader>&amp;L&amp;A&amp;R&amp;F</oddHeader><oddFooter>&amp;CPage &amp;P of &amp;N&amp;R&amp;D</oddFooter></headerFooter>";
        book.Sheet("Costs", Grid(book, 60, 2), after: A4 + Footer);
        book.Sheet("Totals", Grid(book, 10, 2), after: A4 + Footer);

        var pages = Print(Save(book, "budget.xlsx"));

        Assert.Equal(3, pages.Count);
        Assert.StartsWith("Costs budget.xlsx", pages[0]);
        Assert.EndsWith("Page 1 of 3 " + Now.ToString("d", System.Globalization.CultureInfo.CurrentCulture), pages[0]);
        Assert.Contains("Page 2 of 3", pages[1]);
        Assert.StartsWith("Totals budget.xlsx", pages[2]);
        Assert.Contains("Page 3 of 3", pages[2]);
    }

    [Fact]
    public void A_first_page_number_and_a_first_page_header_are_kept()
    {
        var book = Book();
        book.Sheet("Data", Grid(book, 60, 2), after:
            "<pageSetup paperSize=\"9\" firstPageNumber=\"7\" useFirstPageNumber=\"1\"/>" +
            "<headerFooter differentFirst=\"1\"><oddHeader>&amp;CPage &amp;P</oddHeader><firstHeader>&amp;C&amp;BCover</firstHeader></headerFooter>");

        var pages = Print(Save(book));

        Assert.StartsWith("Cover", pages[0]);
        Assert.StartsWith("Page 8", pages[1]);
    }

    [Fact]
    public void Gridlines_and_headings_print_when_the_sheet_asks()
    {
        var book = Book();
        book.Sheet("Data", Grid(book, 3, 2), after: "<printOptions gridLines=\"1\" headings=\"1\"/>" + A4);

        var words = Assert.Single(Print(Save(book))).Split(' ');

        Assert.Equal(["1", "1-1", "1-2", "2", "2-1", "2-2", "3", "3-1", "3-2", "A", "B"], words.Order(StringComparer.Ordinal));
    }

    // ───────────────────────── Cells ─────────────────────────

    [Fact]
    public void Merged_cells_centre_their_text_over_the_whole_area()
    {
        var book = Book();
        book.Sheet("Data", Row(1, book.Text("A1", "Title", Centered)) + Grid(book, 3, 4, firstRow: 2),
            after: "<mergeCells count=\"1\"><mergeCell ref=\"A1:D1\"/></mergeCells>" + A4);

        using var document = PdfDocument.Open(Pdf(XlsxReader.Read(Save(book)), "merged.pdf"));
        var title = document.GetPage(1).GetWords().Single(w => w.Text == "Title");

        var middle = 0.7 * 72 + 4 * 48 / 2.0;
        Assert.InRange((title.BoundingBox.Left + title.BoundingBox.Right) / 2, middle - 2, middle + 2);
    }

    [Fact]
    public void Numbers_too_wide_for_their_column_show_hashes_or_fewer_decimals()
    {
        var book = Book();
        book.Sheet("Data", Row(1, Number("A1", 123456789012.5, Decimals), Number("B1", 3.14159265358979)),
            before: "<cols><col min=\"1\" max=\"2\" width=\"6\" customWidth=\"1\"/></cols>", after: A4);

        var page = Assert.Single(Print(Save(book)));

        Assert.Matches(@"^#+ 3\.14\d*$", page);
    }

    [Fact]
    public void Text_runs_over_empty_neighbours_and_stops_at_filled_ones()
    {
        var book = Book();
        book.Sheet("Data", Row(1, book.Text("A1", "A long heading that needs three columns")) +
                           Row(2, book.Text("A2", "Clipped by its neighbour"), book.Text("B2", "B")) +
                           Row(3, book.Text("F3", "F")), after: A4);
        var workbook = XlsxReader.Read(Save(book));

        using var document = PdfDocument.Open(Pdf(workbook, "overflow.pdf"));
        var words = document.GetPage(1).GetWords().ToList();

        Assert.Contains(words, w => w.Text == "columns" && w.BoundingBox.Left > 0.7 * 72 + 96);
        // Row 2 is clipped at column B: Skia still writes the clipped glyphs, but none of them starts past it.
        Assert.DoesNotContain(words, w => w.BoundingBox.Bottom < 841.89 - 69 && w.BoundingBox.Bottom > 841.89 - 86 && w.BoundingBox.Left > 0.7 * 72 + 48 + 1 && w.Text != "B");
        Assert.Contains(words, w => w.Text == "Clipped");
    }

    [Fact]
    public void Fills_patterns_gradients_and_conditional_formats_colour_the_cells()
    {
        var book = Book();
        book.Sheet("Data", Row(1, book.Text("A1", "", Boxed), book.Text("B1", "", Pattern), book.Text("C1", "", Gradient)) +
                           Row(2, Number("A2", 5), Number("B2", 50), Number("C2", 500)),
            after: "<conditionalFormatting sqref=\"A2:C2\"><cfRule type=\"cellIs\" dxfId=\"1\" priority=\"1\" operator=\"greaterThan\"><formula>100</formula></cfRule></conditionalFormatting>" +
                   "<conditionalFormatting sqref=\"A2:B2\"><cfRule type=\"cellIs\" dxfId=\"0\" priority=\"2\" operator=\"lessThan\"><formula>10</formula></cfRule></conditionalFormatting>" + A4);
        var workbook = XlsxReader.Read(Save(book));
        Pdf(workbook, "fills.pdf");

        float X(int column) => (float)(0.7 * 72 + column * 48 + 24);
        var yellow = Pixel(workbook, 0, X(0), 54 + 7);
        var pattern = Pixel(workbook, 0, X(1), 54 + 7);
        var gradientLeft = Pixel(workbook, 0, X(2) - 20, 54 + 7);
        var gradientRight = Pixel(workbook, 0, X(2) + 20, 54 + 7);
        var pink = Pixel(workbook, 0, X(0) - 20, 69 + 3);
        var plain = Pixel(workbook, 0, X(1) - 20, 69 + 3);
        var green = Pixel(workbook, 0, X(2) - 20, 69 + 3);

        Assert.Equal(new SKColor(255, 255, 0), yellow);
        Assert.Equal(new SKColor(255, 64, 64), pattern); // 75 % red over white
        Assert.True(gradientLeft.Blue > gradientRight.Red && gradientRight.Red < 128 && gradientLeft.Red > 128);
        Assert.Equal(new SKColor(0xFF, 0xC7, 0xCE), pink);
        Assert.Equal(SKColors.White, plain);
        Assert.Equal(new SKColor(0x00, 0xB0, 0x50), green);
    }

    [Fact]
    public void Colour_scales_shade_numbers_between_their_colours()
    {
        var sheet = new Worksheet("Data");
        for (var r = 0; r < 3; r++)
            sheet.Set(r, 0, new SheetCell((r * 50).ToString(System.Globalization.CultureInfo.InvariantCulture), CellStyle.Default, IsNumber: true) { Value = r * 50 });
        sheet.ConditionalFormats.Add(new ConditionalFormat([new CellRange(0, 0, 2, 0)], "colorScale", 1)
        {
            Scale = [new ScalePoint("min", 0, "#FF0000"), new ScalePoint("max", 0, "#0000FF")],
        });

        var styles = ConditionalFormatting.Apply(sheet, new CellRange(0, 0, 2, 0));

        Assert.Equal("#FF0000", styles[(0, 0)].Fill);
        Assert.Equal("#800080", styles[(1, 0)].Fill);
        Assert.Equal("#0000FF", styles[(2, 0)].Fill);
    }

    [Fact]
    public void Alignment_rotation_stacking_shrinking_and_rich_text_are_drawn()
    {
        var book = Book();
        book.Sheet("Data",
            Row(1, book.Text("A1", "Turned", Rotated), book.Text("B1", "ABC", Stacked), book.Text("C1", "Shrunk to fit its cell width", Shrunk),
                book.Text("D1", "Indented", Indented), book.Text("E1", "Across", Across), $"<c r=\"F1\" s=\"{Across}\"/>") +
            Row(2, $"<c r=\"A2\" t=\"inlineStr\"><is><r><t>Plain </t></r><r><rPr><b/><sz val=\"14\"/><color rgb=\"FFC00000\"/><rFont val=\"Calibri\"/></rPr><t>Bold</t></r></is></c>",
                book.Text("B2", "", Diagonal), book.Text("C2", "Big", Large)),
            after: A4);
        var workbook = XlsxReader.Read(Save(book));

        using var document = PdfDocument.Open(Pdf(workbook, "alignment.pdf"));
        var page = document.GetPage(1);
        var letters = page.Letters;

        Assert.Contains(letters, l => l.Value == "T" && l.TextOrientation == UglyToad.PdfPig.Content.TextOrientation.Other);
        // The rich text's second run in its own size.
        Assert.Contains(page.GetWords(), w => w.Text.EndsWith("Bold", StringComparison.Ordinal) && w.Letters[^1].PointSize > 13);
        // Stacked letters are one under another in column B.
        var stacked = letters.Where(l => l.Value is "A" or "B" or "C" && l.StartBaseLine.X > 0.7 * 72 + 48 && l.StartBaseLine.X < 0.7 * 72 + 96).OrderByDescending(l => l.StartBaseLine.Y).ToList();
        Assert.True(stacked.Count >= 3 && stacked[0].StartBaseLine.Y - stacked[^1].StartBaseLine.Y > 20);
        // Shrunk text stays inside its cell; the font is smaller than 11 pt.
        var shrunk = letters.Where(l => l.Value == "S" && l.StartBaseLine.X > 0.7 * 72 + 96 && l.StartBaseLine.X < 0.7 * 72 + 144).ToList();
        Assert.Contains(shrunk, l => l.PointSize < 11);
        // The indent moves "Indented" 20 px (15 pt) right of the cell's padding (and E1 cuts it off); "Across" is
        // centred over E and F.
        var indented = page.GetWords().Single(w => w.Text.StartsWith("Indent", StringComparison.Ordinal));
        Assert.InRange(indented.BoundingBox.Left - (0.7 * 72 + 144 + 1.5), 14, 17);
        var across = page.GetWords().Single(w => w.Text == "Across");
        Assert.InRange((across.BoundingBox.Left + across.BoundingBox.Right) / 2, 0.7 * 72 + 240 - 2, 0.7 * 72 + 240 + 2);
    }

    [Fact]
    public void A_right_to_left_sheet_starts_at_the_right_margin()
    {
        var book = Book();
        book.Sheet("Data", Row(1, book.Text("A1", "First"), book.Text("B1", "Second")),
            before: "<sheetViews><sheetView rightToLeft=\"1\" workbookViewId=\"0\"/></sheetViews>", after: A4);

        using var document = PdfDocument.Open(Pdf(XlsxReader.Read(Save(book)), "rtl.pdf"));
        var words = document.GetPage(1).GetWords().ToList();

        // Two columns from the left margin, A on the right of B.
        Assert.True(words.Single(w => w.Text == "First").BoundingBox.Left > words.Single(w => w.Text == "Second").BoundingBox.Left);
    }

    [Fact]
    public void Pictures_print_where_they_are_anchored()
    {
        var book = Book();
        book.Sheet("Data", Row(1, book.Text("A1", "Logo")) + Row(10, book.Text("A10", "End")), before: "", after: A4 + "<drawing r:id=\"rIdD\"/>");
        book.SheetRelationships[1] = [("rIdD", "drawing", "../drawings/drawing1.xml")];
        book.Parts["xl/drawings/drawing1.xml"] =
            "<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<xdr:twoCellAnchor><xdr:from><xdr:col>1</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>1</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from>" +
            "<xdr:to><xdr:col>3</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>5</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:to>" +
            "<xdr:pic><xdr:nvPicPr><xdr:cNvPr id=\"2\" name=\"Picture 1\"/><xdr:cNvPicPr/></xdr:nvPicPr><xdr:blipFill><a:blip r:embed=\"rIdP\"/></xdr:blipFill><xdr:spPr/></xdr:pic><xdr:clientData/></xdr:twoCellAnchor></xdr:wsDr>";
        book.Parts["xl/drawings/_rels/drawing1.xml.rels"] = OpcWriter.Relationships([("rIdP", "image", "../media/image1.png")]);
        book.Media["xl/media/image1.png"] = RedPng();
        var workbook = XlsxReader.Read(Save(book));

        // B2:C5 → 96 × 60 pt from (0.7 in + 48, 0.75 in + 15).
        var inside = Pixel(workbook, 0, (float)(0.7 * 72 + 48 + 48), 54 + 15 + 30);
        var outside = Pixel(workbook, 0, (float)(0.7 * 72 + 48 + 48), 54 + 15 + 70);
        Assert.Equal(SKColors.Red, inside);
        Assert.Equal(SKColors.White, outside);
    }

    private static byte[] RedPng()
    {
        using var bitmap = new SKBitmap(4, 4);
        bitmap.Erase(SKColors.Red);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // ───────────────────────── XLS and ODS ─────────────────────────

    [Fact]
    public void An_xls_keeps_its_page_setup_print_area_header_and_cell_formats()
    {
        var book = new XlsBuilder();
        var sheet = book.Sheet("Report");
        for (var r = 0; r < 80; r++)
            for (var c = 0; c < 4; c++)
                sheet.Text(r, c, $"{r + 1}-{c + 1}");
        sheet.Text(0, 5, "Boxed", XlsBuilder.Boxed);
        var setup = new byte[34];
        BitConverter.GetBytes((ushort)9).CopyTo(setup, 0);    // A4
        BitConverter.GetBytes((ushort)100).CopyTo(setup, 2);  // 100 %
        BitConverter.GetBytes((ushort)0x0000).CopyTo(setup, 10); // landscape (fPortrait off)
        sheet.Record(0x00A1, setup)
            .Record(0x0028, BitConverter.GetBytes(0.5))         // top margin 0.5 in
            .Record(0x0029, BitConverter.GetBytes(0.5))         // bottom margin 0.5 in
            .Record(0x0014, [.. XlsBuilder.U16(8), 0x01, .. System.Text.Encoding.Unicode.GetBytes("&CReport")])
            .Record(0x001B, [.. XlsBuilder.U16(1), .. XlsBuilder.U16(20), .. XlsBuilder.U16(0), .. XlsBuilder.U16(255)]); // break above row 21
        book.Names.Add((1, 0x06, XlsBuilder.Area(0, 0, 59, 5)));
        var path = book.Save(Path.Combine(fx.NewFolder(), "report.xls"));
        var workbook = SpreadsheetConverter(path);
        using (var document = PdfDocument.Open(Pdf(workbook, "report.xls")))
            Assert.StartsWith("Report", document.GetPage(1).Text);

        var read = workbook.Sheets[0];
        Assert.True(read.PageSetup.Landscape);
        Assert.Equal(36, read.PageSetup.TopMargin, 3);
        Assert.Equal(new CellRange(0, 0, 59, 5), Assert.Single(read.PrintArea));
        Assert.Equal("#FFFF00", read.Get(0, 5)!.Style.Fill);
        Assert.True(read.Get(0, 5)!.Style.Bold);
        Assert.Equal("thin", read.Get(0, 5)!.Style.Left.Style);
        Assert.Equal(90, read.Get(0, 5)!.Style.Rotation);

        var pages = Print(workbook, "report.xls");

        // Landscape A4 with 0.5 in top and bottom margins: 595.3 − 72 = 523.3 pt, 34 rows of 15 pt; rows 1–20 | 21–54 | 55–60.
        Assert.Equal(3, pages.Count);
        Assert.Contains("20-4", pages[0]);
        Assert.DoesNotContain("21-1", pages[0]);
        Assert.Contains("54-4", pages[1]);
        Assert.DoesNotContain("61-1", pages[2]);
    }

    [Fact]
    public void An_ods_keeps_its_page_style_header_rows_and_print_ranges()
    {
        var rows = string.Concat(Enumerable.Range(1, 70).Select(r =>
            $"<table:table-row>{string.Concat(Enumerable.Range(1, 3).Select(c => $"<table:table-cell office:value-type=\"string\"><text:p>{r}-{c}</text:p></table:table-cell>"))}</table:table-row>"));
        var table = "<table:table table:name=\"Plan\" table:style-name=\"ta1\" table:print-ranges=\"Plan.A1:Plan.C70\">" +
                    "<table:table-column table:number-columns-repeated=\"3\"/>" +
                    $"<table:table-header-rows>{rows[..rows.IndexOf("</table:table-row>", StringComparison.Ordinal)]}</table:table-row></table:table-header-rows>" +
                    rows[(rows.IndexOf("</table:table-row>", StringComparison.Ordinal) + "</table:table-row>".Length)..] + "</table:table>";
        var path = OdsBuilder.Save(Path.Combine(fx.NewFolder(), "plan.ods"),
            "<style:style style:name=\"ta1\" style:family=\"table\" style:master-page-name=\"Report\"/>", table,
            pageStyles: "<style:page-layout style:name=\"pm1\"><style:page-layout-properties fo:page-width=\"21cm\" fo:page-height=\"29.7cm\" " +
                        "style:print-orientation=\"portrait\" fo:margin-top=\"1in\" fo:margin-bottom=\"1in\" fo:margin-left=\"0.75in\" fo:margin-right=\"0.75in\"/>" +
                        "<style:header-style><style:header-footer-properties fo:min-height=\"0.5in\" fo:margin-bottom=\"0.1in\"/></style:header-style></style:page-layout>",
            masterStyles: "<style:master-page style:name=\"Report\" style:page-layout-name=\"pm1\"><style:header><style:region-left><text:p><text:sheet-name>???</text:sheet-name></text:p></style:region-left>" +
                          "<style:region-right><text:p>Page <text:page-number>1</text:page-number> / <text:page-count>1</text:page-count></text:p></style:region-right></style:header></style:master-page>");
        var workbook = SpreadsheetConverter(path);

        Assert.Equal((0, 0), workbook.Sheets[0].TitleRows);
        Assert.Equal("&L&A&RPage &P / &N", workbook.Sheets[0].PageSetup.HeaderFooter.OddHeader);
        var pages = Print(workbook, "plan.ods");

        // Calc's header (0.5 in + 0.1 in) moves Excel's top margin to 1.6 in: 654.7 pt holds 43 rows of 15 pt (the
        // rows have no height of their own and the file no default font: Calibri 11); the header row repeats on
        // page 2, which holds rows 44–70.
        Assert.Equal(2, pages.Count);
        Assert.Contains("43-3", pages[0]);
        Assert.DoesNotContain("44-1", pages[0]);
        Assert.StartsWith("Plan Page 1 / 2", pages[0]);
        Assert.StartsWith("Plan Page 2 / 2 1-1 1-2 1-3", pages[1]);
    }

    // ───────────────────────── Fuzz seeds ─────────────────────────

    /// <summary>
    /// A workbook with most of what the print engine reads: styles, rich text, merges, page setup, margins, print
    /// options, a header and footer, print areas and titles, breaks, conditional formats and a picture.
    /// </summary>
    internal static string RichXlsx(string folder)
    {
        var book = Book();
        var rows = Grid(book, 40, 6, style: Boxed) +
                   Row(41, book.Text("A41", "Turned", Rotated), book.Text("B41", "AB", Stacked), book.Text("C41", "Shrink me please", Shrunk),
                       book.Text("D41", string.Join(' ', Enumerable.Repeat("wrap", 12)), Wrapped), Number("E41", 3.14159, Decimals), book.Text("F41", "", Diagonal));
        book.Sheet("Report", rows,
            before: "<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr><cols><col min=\"1\" max=\"2\" width=\"12\" customWidth=\"1\"/></cols>",
            after: "<mergeCells count=\"1\"><mergeCell ref=\"A1:B2\"/></mergeCells>" +
                   "<conditionalFormatting sqref=\"E41\"><cfRule type=\"cellIs\" dxfId=\"0\" priority=\"1\" operator=\"between\"><formula>1</formula><formula>5</formula></cfRule></conditionalFormatting>" +
                   "<printOptions gridLines=\"1\" headings=\"1\" horizontalCentered=\"1\"/><pageMargins left=\"0.5\" right=\"0.5\" top=\"1\" bottom=\"1\" header=\"0.4\" footer=\"0.4\"/>" +
                   "<pageSetup paperSize=\"9\" orientation=\"landscape\" fitToWidth=\"1\" fitToHeight=\"2\" pageOrder=\"overThenDown\"/>" +
                   "<headerFooter differentFirst=\"1\"><oddHeader>&amp;L&amp;A&amp;C&amp;\"Arial,Bold\"&amp;14Title&amp;R&amp;D</oddHeader><oddFooter>&amp;CPage &amp;P of &amp;N</oddFooter><firstHeader>&amp;CFirst</firstHeader></headerFooter>" +
                   "<rowBreaks count=\"1\"><brk id=\"20\" man=\"1\"/></rowBreaks><drawing r:id=\"rIdD\"/>");
        book.Sheet("Second", Grid(book, 5, 3), after: A4);
        book.DefinedNames = "<definedNames><definedName name=\"_xlnm.Print_Titles\" localSheetId=\"0\">Report!$1:$1</definedName>" +
                            "<definedName name=\"_xlnm.Print_Area\" localSheetId=\"1\">Second!$A$1:$C$5</definedName></definedNames>";
        book.SheetRelationships[1] = [("rIdD", "drawing", "../drawings/drawing1.xml")];
        book.Parts["xl/drawings/drawing1.xml"] =
            "<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<xdr:oneCellAnchor><xdr:from><xdr:col>4</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>2</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from><xdr:ext cx=\"381000\" cy=\"381000\"/>" +
            "<xdr:pic><xdr:nvPicPr><xdr:cNvPr id=\"2\" name=\"Picture 1\"/><xdr:cNvPicPr/></xdr:nvPicPr><xdr:blipFill><a:blip r:embed=\"rIdP\"/></xdr:blipFill><xdr:spPr/></xdr:pic><xdr:clientData/></xdr:oneCellAnchor></xdr:wsDr>";
        book.Parts["xl/drawings/_rels/drawing1.xml.rels"] = OpcWriter.Relationships([("rIdP", "image", "../media/image1.png")]);
        book.Media["xl/media/image1.png"] = RedPng();
        return book.Save(Path.Combine(folder, "rich.xlsx"));
    }

    /// <summary>An ODS with a page style, header and footer regions, header rows, a print range and a page break.</summary>
    internal static string RichOds(string folder)
    {
        var rows = string.Concat(Enumerable.Range(1, 30).Select(r =>
            $"<table:table-row{(r == 15 ? " table:style-name=\"ro1\"" : "")}>" +
            $"{string.Concat(Enumerable.Range(1, 4).Select(c => $"<table:table-cell office:value-type=\"float\" office:value=\"{r * c}\" table:style-name=\"ce1\"><text:p>{r * c}</text:p></table:table-cell>"))}</table:table-row>"));
        return OdsBuilder.Save(Path.Combine(folder, "rich.ods"),
            "<style:style style:name=\"ta1\" style:family=\"table\" style:master-page-name=\"Report\"/>" +
            "<style:style style:name=\"ro1\" style:family=\"table-row\"><style:table-row-properties fo:break-before=\"page\" style:row-height=\"0.2in\"/></style:style>" +
            "<style:style style:name=\"ce1\" style:family=\"table-cell\"><style:table-cell-properties fo:background-color=\"#ffff00\" fo:border=\"0.06pt solid #000000\" style:rotation-angle=\"30\"/></style:style>",
            $"<table:table table:name=\"Plan\" table:style-name=\"ta1\" table:print-ranges=\"Plan.A1:Plan.D30\"><table:table-column table:number-columns-repeated=\"4\"/><table:table-header-rows>{rows[..(rows.IndexOf("</table:table-row>", StringComparison.Ordinal) + 18)]}</table:table-header-rows>{rows[(rows.IndexOf("</table:table-row>", StringComparison.Ordinal) + 18)..]}</table:table>",
            pageStyles: "<style:page-layout style:name=\"pm1\"><style:page-layout-properties fo:page-width=\"21cm\" fo:page-height=\"29.7cm\" style:print-orientation=\"portrait\" style:print=\"grid headers\" style:scale-to=\"80%\" style:table-centering=\"both\"/>" +
                        "<style:header-style><style:header-footer-properties fo:min-height=\"0.3in\"/></style:header-style><style:footer-style><style:header-footer-properties fo:min-height=\"0.3in\"/></style:footer-style></style:page-layout>",
            masterStyles: "<style:master-page style:name=\"Report\" style:page-layout-name=\"pm1\"><style:header><style:region-center><text:p><text:sheet-name>?</text:sheet-name> &amp; <text:title>?</text:title></text:p></style:region-center></style:header>" +
                          "<style:footer><text:p>Page <text:page-number>1</text:page-number></text:p></style:footer></style:master-page>");
    }

    [Fact]
    public void The_fuzz_seeds_print()
    {
        var folder = fx.NewFolder();
        Assert.True(Print(XlsxReader.Read(RichXlsx(folder)), "rich.xlsx").Count >= 2);
        var ods = Print(OdsReader.Read(RichOds(folder)), "rich.ods");
        Assert.Equal(2, ods.Count);
        Assert.Contains("Plan & rich.ods", ods[0]);
    }

    // ───────────────────────── Engine and route ─────────────────────────

    [Theory]
    [InlineData("xlsx")]
    [InlineData("xls")]
    [InlineData("ods")]
    public void Spreadsheets_go_to_pdf_through_the_print_engine(string from)
    {
        var planner = fx.Catalog.CreatePlanner();

        Assert.Equal("sheet-print", Assert.Single(planner.Plan(from, "pdf")!.Steps).Converter.Id);
        Assert.Equal(["sheet-print", "pdfium"], planner.Plan(from, "png")!.Steps.Select(s => s.Converter.Id));
    }

    [Fact]
    public void Other_routes_keep_their_engines()
    {
        var planner = fx.Catalog.CreatePlanner(["rhwp"]);

        // CSV has no page setup: it keeps the HWPX writer's fitted tables. Spreadsheet ↔ spreadsheet stays as it was.
        Assert.Equal(["hwpx-writer", "rhwp"], planner.Plan("csv", "pdf")!.Steps.Select(s => s.Converter.Id));
        Assert.Equal(["hwpx-writer", "rhwp"], planner.Plan("tsv", "pdf")!.Steps.Select(s => s.Converter.Id));
        Assert.Equal(["hwpx-writer", "rhwp"], planner.Plan("pptx", "pdf")!.Steps.Select(s => s.Converter.Id));
        Assert.Equal("spreadsheet", Assert.Single(planner.Plan("xlsx", "ods")!.Steps).Converter.Id);
        Assert.Equal(["hwpx-writer"], planner.Plan("xlsx", "hwpx")!.Steps.Select(s => s.Converter.Id));
        Assert.Equal(["hwpx-writer", "rhwp"], planner.Plan("docx", "pdf")!.Steps.Select(s => s.Converter.Id));
    }

    [Fact]
    public async Task An_xlsx_converts_to_pdf_in_a_job()
    {
        var book = Book();
        book.Sheet("Data", Grid(book, 60, 3), after: A4);
        var path = Save(book, "job.xlsx");

        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "pdf" });

        Assert.Equal(JobState.Completed, job.State);
        using var pdf = PdfDocument.Open(job.Outputs.Single());
        Assert.Equal(2, pdf.NumberOfPages);
        Assert.Contains("Filee", pdf.Information.Producer);
    }

    // ───────────────────────── Hostile input ─────────────────────────

    [Fact]
    public void A_sheet_that_would_print_on_too_many_pages_is_refused()
    {
        var book = Book();
        book.Sheet("Data", Row(1, book.Text("A1", "first")) + Row(1048576, book.Text("XFD1048576", "last")), after: A4);
        var workbook = XlsxReader.Read(Save(book));

        var refused = Assert.Throws<ConversionRefusedException>(() => SheetPrinter.ToPdf(workbook, new SheetPrintSource("big.xlsx", ""), TestContext.Current.CancellationToken));
        Assert.Equal(DocumentLimits.ErrorKey, refused.ErrorKey);
    }

    [Fact]
    public void Damaged_and_oversized_workbooks_are_refused_cleanly()
    {
        var folder = fx.NewFolder();
        var broken = Path.Combine(folder, "broken.xlsx");
        File.WriteAllBytes(broken, [0x50, 0x4B, 0x03, 0x04, 1, 2, 3]);
        Assert.ThrowsAny<InvalidDataException>(() => XlsxReader.Read(broken));

        var malformed = Book();
        malformed.Sheet("Data", Row(1, "<c r=\"A1\"><v>1</v>"));
        Assert.ThrowsAny<InvalidDataException>(() => XlsxReader.Read(Save(malformed)));

        // Column ranges over the whole sheet repeated thousands of times.
        var columns = Book();
        columns.Sheet("Data", Row(1, columns.Text("A1", "x")),
            before: "<cols>" + string.Concat(Enumerable.Repeat("<col min=\"1\" max=\"16384\" width=\"5\"/>", 300)) + "</cols>");
        var refused = Assert.Throws<ConversionRefusedException>(() => XlsxReader.Read(Save(columns)));
        Assert.Equal(DocumentLimits.ErrorKey, refused.ErrorKey);
    }

    [Fact]
    public void Header_codes_are_parsed_without_trusting_them()
    {
        var sections = HeaderFooterText.Parse("&L&\"Arial,Bold Italic\"&14Left&R&KFF0000Right &P+2 &&&C&");

        Assert.Equal("Left", Assert.Single(sections.Left).Text);
        Assert.True(sections.Left[0].Bold && sections.Left[0].Italic);
        Assert.Equal(14, sections.Left[0].Size);
        Assert.Equal("#FF0000", sections.Right[0].Color);
        Assert.Contains(sections.Right, p => p.Field == HeaderField.Page && p.Offset == 2);
        Assert.Contains(sections.Right, p => p.Text.Contains('&'));
        Assert.True(HeaderFooterText.Parse(new string('&', 100_000)).Center.Sum(p => p.Text.Length) <= 4096);
    }
}
