// Notes on cells and tables on printed sheets: notes listed after the sheet or shown in their boxes when the page setup
// asks for it (XLSX pageSetup cellComments, XLSB BrtPageSetup fNotes / fEndNotes, ODS style:print "annotations"),
// table styles (built-in ones from ECMA-376's presetTableStyles.xml and the workbook's own) put on the table's cells,
// and the ODS drawings the printer took on: text in shapes and text boxes, freeform shapes, cropped pictures, gradient
// fills, and charts kept in their place among the other drawings. XLSB and XLSX are written as twins (XlsbBuilder), so
// both readers are held to the same result. Every workbook is built here in code; set FILEE_RENDER_OUT to keep pictures
// of the pages.

using System.Diagnostics;
using System.Text;
using Filee.Engines.Documents;
using Filee.Engines.Office.Sheets;
using Filee.Engines.Rendering;
using SkiaSharp;
using UglyToad.PdfPig;

namespace Filee.Engines.Tests;

public sealed class SheetNotesAndTablesTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly DateTime Now = new(2026, 11, 21, 9, 0, 0);

    /// <summary>A4 portrait, 100 %, so the pages don't depend on this PC's region.</summary>
    private static readonly (int, bool, int, bool, int, int, bool, int?) A4 = (9, false, 100, false, 1, 1, false, null);

    private const string DrawNamespaces = "xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" " +
                                          "xmlns:svg=\"urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" " +
                                          "xmlns:dc=\"http://purl.org/dc/elements/1.1/\"";

    private (Workbook Xlsb, Workbook Xlsx) Twins(XlsbBuilder book)
    {
        var folder = fx.NewFolder();
        return (XlsbReader.Read(book.SaveXlsb(Path.Combine(folder, "book.xlsb"))), XlsxReader.Read(book.SaveXlsx(Path.Combine(folder, "book.xlsx"))));
    }

    private static SheetPrinter.Printed Printed(Workbook book) =>
        SheetPrinter.Print(book, new SheetPrintSource("book.xlsx", "C:\\"), DocumentLimits.Default, Now, null, TestContext.Current.CancellationToken);

    /// <summary>The words of each printed page (and pictures of the pages when FILEE_RENDER_OUT is set).</summary>
    private static List<string> Words(Workbook book, string name)
    {
        var pdf = SheetPrinter.ToPdf(book, new SheetPrintSource("book.xlsx", "C:\\"), TestContext.Current.CancellationToken, now: Now);
        if (Environment.GetEnvironmentVariable("FILEE_RENDER_OUT") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            using var printed = Printed(book);
            for (var i = 0; i < printed.Pages.Count; i++)
            {
                using var bitmap = printed.Pages[i].Render(144);
                using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(Path.Combine(folder, $"{name}-{i + 1}.png"), png.ToArray());
            }
        }
        using var document = PdfDocument.Open(pdf);
        return [.. document.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text)))];
    }

    /// <summary>Page <paramref name="page"/> at 72 dpi: a pixel is a point.</summary>
    private static SKBitmap Page(Workbook book, int page = 0)
    {
        using var printed = Printed(book);
        return printed.Pages[page].Render(72);
    }

    /// <summary>
    /// Where a point of the first page lies (72 dpi pixels): <paramref name="dx"/>, <paramref name="dy"/> points from
    /// the top-left corner of the cell, as the page's plan places its columns and rows (whatever the fonts measure).
    /// </summary>
    private static (int X, int Y) At(Workbook book, int row, int column, double dx = 0, double dy = 0)
    {
        using var fonts = new SheetFonts();
        var sheet = book.Sheets[0];
        var plan = SheetPagination.Plan(book, sheet, new SheetIndex(sheet), fonts, DocumentLimits.Default, 0, TestContext.Current.CancellationToken);
        var page = plan.Pages[0];
        return ((int)(sheet.PageSetup.LeftMargin + (plan.Grid.ColumnSpan(page.Left, column) + dx) * plan.Scale),
            (int)(sheet.PageSetup.TopMargin + (plan.Grid.RowSpan(page.Top, row) + dy) * plan.Scale));
    }

    private static bool Near(SKColor actual, SKColor expected, int slack = 12) =>
        Math.Abs(actual.Red - expected.Red) <= slack && Math.Abs(actual.Green - expected.Green) <= slack && Math.Abs(actual.Blue - expected.Blue) <= slack;

    private static XlsbBuilder Budget()
    {
        var book = new XlsbBuilder();
        book.Sheet("Budget").Text(0, 0, "Item").Text(0, 1, "Total").Text(1, 0, "Rent").Number(1, 1, 1200).Text(2, 0, "Food").Number(2, 1, 450);
        book.Sheets[0].PageSetup = A4;
        return book;
    }

    // ───────────────────────── Notes ─────────────────────────

    [Fact]
    public void Notes_print_after_the_sheet_when_its_page_setup_asks_for_them()
    {
        var book = Budget();
        var sheet = book.Sheets[0];
        sheet.Notes.Add((2, 0, "Bo", "Due on Friday", false));
        sheet.Notes.Add((1, 1, "Ann", "Check the total", false));
        sheet.NotesPrint = 2;
        sheet.HeaderFooter = (null, "&P of &N", null, false);
        var (xlsb, xlsx) = Twins(book);

        Assert.Equal(SheetCommentsPrint.AtEnd, xlsb.Sheets[0].PageSetup.Comments);
        Assert.Equal(xlsx.Sheets[0].Comments, xlsb.Sheets[0].Comments);
        Assert.Equal([(2, 0, "Bo:\nDue on Friday"), (1, 1, "Ann:\nCheck the total")], xlsb.Sheets[0].Comments.Select(c => (c.Row, c.Column, c.Text)));
        var pages = InLanguage("en-US", () => Words(xlsb, "notes-at-end"));
        Assert.Equal(2, pages.Count);
        Assert.Contains("Rent", pages[0]);
        Assert.DoesNotContain("Check", pages[0]);
        // By row, then column; numbered on with the sheet's pages.
        Assert.Equal("Cell: B2 Comment: Ann: Check the total Cell: A3 Comment: Bo: Due on Friday 2 of 2", pages[1]);
        Assert.EndsWith("1 of 2", pages[0]);
        Assert.Equal(pages, InLanguage("en-US", () => Words(xlsx, "notes-at-end-xlsx")));

        // Not asked for: the notes don't print.
        sheet.NotesPrint = 0;
        var (plain, _) = Twins(book);
        Assert.Equal(SheetCommentsPrint.None, plain.Sheets[0].PageSetup.Comments);
        Assert.Single(Words(plain, "notes-none"));
    }

    [Fact]
    public void Shown_notes_print_in_their_boxes_when_they_print_as_displayed()
    {
        var book = Budget();
        var sheet = book.Sheets[0];
        sheet.Notes.Add((1, 1, "Ann", "Check the total", true));
        sheet.Notes.Add((2, 0, "Bo", "Due on Friday", false));
        sheet.NotesPrint = 1;
        var (xlsb, xlsx) = Twins(book);

        var shown = Assert.Single(xlsb.Sheets[0].Comments, c => c.Shown);
        // Excel's default box: from C1 (+15, +10 px) to E5 (+15, +16 px).
        Assert.Equal((0, 2, 11.25, 7.5, (4, 4, 11.25, 12.0)), (shown.Box!.Row, shown.Box.Column, shown.Box.OffsetX, shown.Box.OffsetY, shown.Box.To!.Value));
        var page = Assert.Single(Words(xlsb, "notes-shown"));
        Assert.Contains("Check the total", page);
        Assert.DoesNotContain("Friday", page);

        // The box's fill inside it, the line from B2's top-right corner to the box.
        using var bitmap = Page(xlsb);
        var inside = At(xlsb, 4, 4, 0, 6);
        Assert.True(Near(bitmap.GetPixel(inside.X, inside.Y), new SKColor(0xFF, 0xFF, 0xE1), 2), bitmap.GetPixel(inside.X, inside.Y).ToString());
        var corner = At(xlsb, 1, 2);
        var line = ((corner.X + At(xlsb, 0, 2, 11.25).X) / 2, corner.Y);
        Assert.True(Math.Min(bitmap.GetPixel(line.Item1, line.Y - 1).Red, bitmap.GetPixel(line.Item1, line.Y).Red) < 200, bitmap.GetPixel(line.Item1, line.Y).ToString());
        using var twin = Page(xlsx);
        Assert.Equal(bitmap.Bytes, twin.Bytes);

        // Shown, but the page setup doesn't print notes.
        sheet.NotesPrint = 0;
        Assert.DoesNotContain("Check", Assert.Single(Words(Twins(book).Xlsb, "notes-shown-off")));
    }

    [Fact]
    public void Ods_notes_print_at_the_end_or_shown_with_the_drawings()
    {
        const string Book = "<style:style style:name=\"ta1\" style:family=\"table\" style:master-page-name=\"Default\"/>";
        var layout = (string print) => "<style:page-layout style:name=\"pm1\"><style:page-layout-properties fo:page-width=\"21cm\" fo:page-height=\"29.7cm\" " +
                                       $"style:print=\"{print}\"/></style:page-layout>";
        const string Master = "<style:master-page style:name=\"Default\" style:page-layout-name=\"pm1\"/>";
        const string Tables = $"<table:table table:name=\"Sheet1\" table:style-name=\"ta1\" {DrawNamespaces}><table:table-column table:number-columns-repeated=\"3\"/>" +
                              "<table:table-row><table:table-cell office:value-type=\"string\"><text:p>Item</text:p></table:table-cell>" +
                              "<table:table-cell office:value-type=\"string\"><text:p>Cost</text:p>" +
                              "<office:annotation office:display=\"true\" svg:x=\"4cm\" svg:y=\"0.2cm\" svg:width=\"4cm\" svg:height=\"1.5cm\"><dc:creator>Ann</dc:creator>" +
                              "<text:p>Rounded up</text:p></office:annotation></table:table-cell></table:table-row>" +
                              "<table:table-row><table:table-cell office:value-type=\"string\"><text:p>Rent</text:p>" +
                              "<office:annotation><dc:creator>Bo</dc:creator><text:p>Paid</text:p></office:annotation></table:table-cell></table:table-row></table:table>";
        string Save(string print) => OdsBuilder.Save(Path.Combine(fx.NewFolder(), "book.ods"), Book, Tables, pageStyles: layout(print), masterStyles: Master);

        var atEnd = OdsReader.Read(Save("annotations objects charts drawings"));
        Assert.Equal(SheetCommentsPrint.AtEnd, atEnd.Sheets[0].PageSetup.Comments);
        Assert.Equal([(0, 1, "Rounded up", "Ann", true), (1, 0, "Paid", "Bo", false)],
            atEnd.Sheets[0].Comments.Select(c => (c.Row, c.Column, c.Text, c.Author, c.Shown)));
        var pages = InLanguage("en-US", () => Words(atEnd, "ods-notes-at-end"));
        Assert.Equal(2, pages.Count);
        Assert.Equal("Cell: B1 Comment: Ann: Rounded up Cell: A2 Comment: Bo: Paid", pages[1]);

        // Without "annotations" the shown note prints in its box with the drawings; the other doesn't.
        var shown = OdsReader.Read(Save("objects charts drawings"));
        Assert.Equal(SheetCommentsPrint.AsDisplayed, shown.Sheets[0].PageSetup.Comments);
        var box = shown.Sheets[0].Comments[0].Box!;
        Assert.Equal((4 * 72 / 2.54, 4 * 72 / 2.54), (box.OffsetX, box.Width), new PointsComparer());
        var page = Assert.Single(Words(shown, "ods-notes-shown"));
        Assert.Contains("Rounded up", page);
        Assert.DoesNotContain("Paid", page);
    }

    private sealed class PointsComparer : IEqualityComparer<(double, double)>
    {
        public bool Equals((double, double) a, (double, double) b) => Math.Abs(a.Item1 - b.Item1) < 0.01 && Math.Abs(a.Item2 - b.Item2) < 0.01;
        public int GetHashCode((double, double) obj) => 0;
    }

    /// <summary><paramref name="read"/> with the app's language set to <paramref name="culture"/>.</summary>
    private static T InLanguage<T>(string culture, Func<T> read)
    {
        var language = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
            return read();
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = language;
        }
    }

    /// <summary>The text of the notes list's lines, page by page (the layout's model: no fonts in the way).</summary>
    private static List<string> ListText(Workbook book)
    {
        using var fonts = new SheetFonts();
        var pages = SheetCommentList.Layout(book, book.Sheets[0], fonts, new CellRange(0, 0, 100, 20),
            new Documents.LayoutGuard(DocumentLimits.Default, TestContext.Current.CancellationToken));
        return [.. pages.Select(lines => string.Join(" ", lines.Select(l => l.Text.Text[l.Start..l.End])))];
    }

    [Theory]
    [InlineData("ko-KR", "셀: B2 메모: Ann: Check the total")]
    [InlineData("de-CH", "Zelle: B2 Kommentar: Ann: Check the total")]
    [InlineData("zh-TW", "儲存格: B2 註解: Ann: Check the total")]
    [InlineData("fr-FR", "Cellule : B2 Commentaire : Ann: Check the total")]
    [InlineData("sv-SE", "Cell: B2 Comment: Ann: Check the total")]
    public void The_notes_list_words_follow_the_app_language(string culture, string expected)
    {
        var book = Budget();
        book.Sheets[0].Notes.Add((1, 1, "Ann", "Check the total", false));
        book.Sheets[0].NotesPrint = 2;
        var (xlsb, _) = Twins(book);
        Assert.Equal(expected, Assert.Single(InLanguage(culture, () => ListText(xlsb))));
        // Every language the app ships has its words.
        var folder = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Filee.App", "Assets", "i18n");
        foreach (var language in Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json").Select(Path.GetFileNameWithoutExtension) : [])
            Assert.Equal(Charts.ChartWords.Language(System.Globalization.CultureInfo.GetCultureInfo(language!)) == "en",
                SheetWords.Of(System.Globalization.CultureInfo.GetCultureInfo(language!)).Cell == "Cell:");
    }

    /// <summary>The budget sheet with two threaded comments (one over Excel's legacy copy of it, one without) and a plain note.</summary>
    private static XlsbBuilder Threads()
    {
        var book = Budget();
        book.Persons.Add(("{6B8E5F10-0000-4000-8000-000000000001}", "Ann Lee"));
        book.Persons.Add(("{6B8E5F10-0000-4000-8000-000000000002}", "Bo Kim"));
        var sheet = book.Sheets[0];
        // Excel's legacy copy of the first thread: a notice for older versions, which the thread replaces.
        sheet.Notes.Add((1, 1, "tc={00000000-0000-0000-0000-000000000001}",
            "[Threaded comment]\n\nYour version of Excel allows you to read this threaded comment; however, any edits to it will get removed if the file is opened in a newer version of Excel.\n\nComment:\n    Check the total\nReply:\n    Done", false));
        sheet.Notes.Add((2, 0, "Cy", "Due on Friday", false));
        sheet.Threads.Add((1, 1, "{6B8E5F10-0000-4000-8000-000000000001}", "Check the total",
            [("{6B8E5F10-0000-4000-8000-000000000002}", "Done"), ("{6B8E5F10-0000-4000-8000-00000000FFFF}", "Thanks")]));
        sheet.Threads.Add((2, 1, "{6B8E5F10-0000-4000-8000-000000000002}", "New thread", []));
        sheet.NotesPrint = 2;
        return book;
    }

    [Fact]
    public void Threaded_comments_print_at_the_end_with_their_replies()
    {
        var (xlsb, xlsx) = Twins(Threads());
        foreach (var sheet in new[] { xlsb.Sheets[0], xlsx.Sheets[0] })
        {
            Assert.Equal([(1, 1), (2, 0), (2, 1)], sheet.Comments.Select(c => (c.Row, c.Column)));
            var thread = sheet.Comments[0];
            Assert.Equal(("Check the total", "Ann Lee", true, false), (thread.Text, thread.Author, thread.Threaded, thread.Shown));
            // A reply by a person the persons part doesn't name keeps its text.
            Assert.Equal([("Bo Kim", "Done"), (null, "Thanks")], thread.Replies);
            Assert.Equal(("Cy:\nDue on Friday", false), (sheet.Comments[1].Text, sheet.Comments[1].Threaded));
            Assert.Equal(("New thread", "Bo Kim", true), (sheet.Comments[2].Text, sheet.Comments[2].Author, sheet.Comments[2].Threaded));
        }
        var pages = InLanguage("en-US", () => Words(xlsb, "threads"));
        Assert.Equal(2, pages.Count);
        Assert.Equal("Cell: B2 Comment: Ann Lee: Check the total Reply: Bo Kim: Done Reply: Thanks Cell: A3 Comment: Cy: Due on Friday " +
                     "Cell: B3 Comment: Bo Kim: New thread", pages[1]);
        Assert.Equal(pages, InLanguage("en-US", () => Words(xlsx, "threads-xlsx")));
        Assert.Equal("셀: B2 메모: Ann Lee: Check the total 회신: Bo Kim: Done 회신: Thanks 셀: A3 메모: Cy: Due on Friday 셀: B3 메모: Bo Kim: New thread",
            Assert.Single(InLanguage("ko-KR", () => ListText(xlsb))));

        // Threads aren't drawn in boxes when notes print as displayed (Excel prints them only at the end).
        var shown = Threads();
        shown.Sheets[0].Notes[0] = shown.Sheets[0].Notes[0] with { Shown = true };
        shown.Sheets[0].NotesPrint = 1;
        var (displayed, _) = Twins(shown);
        Assert.DoesNotContain(displayed.Sheets[0].Comments, c => c.Shown);
    }

    [Fact]
    public void Threaded_comments_past_the_budget_or_not_xml_are_left_out_quickly()
    {
        var clock = Stopwatch.StartNew();
        var book = Budget();
        book.Persons.Add(("{P}", "Ann"));
        for (var i = 0; i < SheetComments.MaxComments + 5000; i++)
            book.Sheets[0].Threads.Add((i % 1_000_000, i / 1_000_000, "{P}", "n", []));
        var (xlsb, xlsx) = Twins(book);
        Assert.Equal(SheetComments.MaxComments, xlsb.Sheets[0].Comments.Count);
        Assert.Equal(SheetComments.MaxComments, xlsx.Sheets[0].Comments.Count);

        // A threaded comments part that isn't XML leaves the legacy copy in place.
        var broken = Threads();
        var path = broken.SaveXlsx(Path.Combine(fx.NewFolder(), "broken.xlsx"));
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Update))
        {
            zip.GetEntry("xl/threadedComments/threadedComment1.xml")!.Delete();
            using var stream = zip.CreateEntry("xl/threadedComments/threadedComment1.xml").Open();
            stream.Write("<ThreadedComments><oops"u8);
        }
        var read = XlsxReader.Read(path);
        Assert.StartsWith("tc={", read.Sheets[0].Comments[0].Text, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(120), clock.Elapsed.ToString());
    }

    // ───────────────────────── Tables ─────────────────────────

    /// <summary>Sales A1:C5: a header, three data rows (C4 left blank, B3 in a style with its own fill), a total row.</summary>
    private static XlsbBuilder Sales(string style, bool firstColumn = true)
    {
        var book = new XlsbBuilder();
        var sheet = book.Sheet("Sales").Text(0, 0, "Region").Text(0, 1, "Q1").Text(0, 2, "Q2")
            .Text(1, 0, "North").Number(1, 1, 10).Number(1, 2, 20)
            .Text(2, 0, "South").Number(2, 1, 30, XlsbBuilder.Header).Number(2, 2, 40)
            .Text(3, 0, "West").Number(3, 1, 50)
            .Text(4, 0, "Total").Number(4, 1, 90).Number(4, 2, 60);
        sheet.PageSetup = A4;
        sheet.Tables.Add(((0, 0, 4, 2), style, firstColumn, true, true));
        return book;
    }

    /// <summary>A theme colour tinted as Filee tints every SpreadsheetML colour (as Excel does).</summary>
    private static string Tinted(int theme, double tint) => Office.Ooxml.OoxmlColors.FromTheme(null).Spreadsheet(new System.Xml.Linq.XElement("color",
        new System.Xml.Linq.XAttribute("theme", theme), new System.Xml.Linq.XAttribute("tint", tint.ToString(System.Globalization.CultureInfo.InvariantCulture))))!;

    [Fact]
    public void A_table_prints_in_its_built_in_style()
    {
        // Accent 1 at 80 % and 40 % tint: Excel shows #D9E1F2 and #8EA9DB.
        var (band, line) = (Tinted(4, 0.8), Tinted(4, 0.4));
        Assert.Equal(("#D9E1F2", "#8EA9DB"), (band, line));
        var (xlsb, xlsx) = Twins(Sales("TableStyleMedium2"));
        foreach (var sheet in new[] { xlsb.Sheets[0], xlsx.Sheets[0] })
        {
            CellStyle At(int row, int column) => sheet.Get(row, column)!.Style;
            // Header: white bold on accent 1 (Office's #4472C4).
            Assert.Equal(("#4472C4", "#FFFFFF", true), (At(0, 1).Fill, At(0, 1).Color, At(0, 1).Bold));
            // Banded rows, inner lines; the blank C4 is banded too.
            Assert.Equal(band, At(1, 1).Fill);
            Assert.Null(At(2, 2).Fill);
            Assert.Equal(band, At(3, 2).Fill);
            Assert.Equal(new CellBorder("thin", line), At(1, 1).Bottom);
            Assert.Equal(new CellBorder("thin", line), At(1, 0).Left);
            // A cell's own fill stays; the first column is bold; the total row has a double line above it.
            Assert.Equal("#1F4E79", At(2, 1).Fill);
            Assert.True(At(2, 0).Bold);
            Assert.Equal((new CellBorder("double", "#4472C4"), true), (At(4, 1).Top, At(4, 1).Bold));
        }
        Assert.Equal(Cells(xlsx.Sheets[0]), Cells(xlsb.Sheets[0]));

        // On the page: the header's fill beside its text, a band, the second band left white.
        Words(xlsb, "table-medium2");
        using var bitmap = Page(xlsb);
        SKColor Pixel((int X, int Y) at) => bitmap.GetPixel(at.X, at.Y);
        Assert.True(Near(Pixel(At(xlsb, 0, 2, -3, 12)), new SKColor(0x44, 0x72, 0xC4)), Pixel(At(xlsb, 0, 2, -3, 12)).ToString());
        Assert.True(Near(Pixel(At(xlsb, 1, 1, 3, 12)), SheetColors.Parse(band, SKColors.Black)), Pixel(At(xlsb, 1, 1, 3, 12)).ToString());
        Assert.True(Near(Pixel(At(xlsb, 2, 2, 3, 12)), SKColors.White), Pixel(At(xlsb, 2, 2, 3, 12)).ToString());

        // A style Filee doesn't know leaves the cells as they are.
        var (unknown, _) = Twins(Sales("NoSuchStyle"));
        Assert.Null(unknown.Sheets[0].Get(0, 1)!.Style.Fill);
        Assert.Null(unknown.Sheets[0].Get(3, 2));
    }

    private static List<string> Cells(Worksheet sheet) =>
        [.. sheet.Rows.SelectMany(r => r.Value.Select(c => $"{r.Key},{c.Key} {c.Value.Text} {c.Value.Style}"))];

    [Fact]
    public void A_custom_table_style_of_the_workbook_wins_over_the_built_in_ones()
    {
        var book = Sales("Brand", firstColumn: false);
        book.Dxfs.Add((XlsbColor.Rgb(0xC00000), XlsbColor.Rgb(0xFFFFFF), true));
        book.Dxfs.Add((XlsbColor.Rgb(0xFFF2CC), null, false));
        book.TableStyles.Add(("Brand", [("headerRow", 0), ("firstRowStripe", 1)]));
        var (xlsb, xlsx) = Twins(book);
        foreach (var sheet in new[] { xlsb.Sheets[0], xlsx.Sheets[0] })
        {
            Assert.Equal(("#C00000", "#FFFFFF", true), (sheet.Get(0, 0)!.Style.Fill, sheet.Get(0, 0)!.Style.Color, sheet.Get(0, 0)!.Style.Bold));
            Assert.Equal("#FFF2CC", sheet.Get(1, 0)!.Style.Fill);
            Assert.Null(sheet.Get(2, 0)!.Style.Fill);
            Assert.False(sheet.Get(2, 0)!.Style.Bold);
        }
        Assert.Equal(Cells(xlsx.Sheets[0]), Cells(xlsb.Sheets[0]));
    }

    [Fact]
    public void Tables_over_whole_sheets_and_many_notes_are_read_within_their_budgets_quickly()
    {
        var clock = Stopwatch.StartNew();
        var book = new XlsbBuilder();
        var sheet = book.Sheet("Big").Text(0, 0, "Head").Text(5000, 3, "Far");
        // Eight whole-sheet tables: the first styles blank cells until the budget's cells are spent, the next ones
        // walk the cells the sheet has until its visits are.
        for (var i = 0; i < 8; i++)
            sheet.Tables.Add(((0, 0, 1_048_575, 16_383), "TableStyleMedium2", true, true, true));
        var (xlsb, xlsx) = Twins(book);
        foreach (var read in new[] { xlsb.Sheets[0], xlsx.Sheets[0] })
        {
            // Blank cells only while the budget lasts; the far cell has its look all the same.
            Assert.InRange(read.Rows.Sum(r => r.Value.Count), 2, new TableBudget().Cells + 2);
            Assert.True(read.Get(5000, 3)!.Style.HasBorders);
        }

        // Far more notes than a sheet keeps, without their VML.
        var xml = new StringBuilder("<comments xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><authors><author>A</author></authors><commentList>");
        for (var i = 0; i < SheetComments.MaxComments + 5000; i++)
            xml.Append("<comment ref=\"A").Append(i + 1).Append("\" authorId=\"0\"><text><t>n</t></text></comment>");
        var notes = new XlsxBuilder().Sheet("Notes", XlsxBuilder.Row(1, XlsxBuilder.Number("A1", 1)), after: "<pageSetup cellComments=\"atEnd\"/>");
        notes.Parts["xl/comments1.xml"] = xml.Append("</commentList></comments>").ToString();
        notes.SheetRelationships[1] = [("rIdC", "comments", "../comments1.xml")];
        var many = XlsxReader.Read(notes.Save(Path.Combine(fx.NewFolder(), "notes.xlsx")));
        Assert.Equal(SheetComments.MaxComments, many.Sheets[0].Comments.Count);
        // About 25 s alone on a busy PC (two readers, the budgets spent in full): room for a loaded runner.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(300), clock.Elapsed.ToString());
    }

    [Fact]
    public void A_tables_own_formats_go_over_its_style_and_under_its_cells_own()
    {
        var book = Sales("TableStyleLight1");
        book.Dxfs.Add((XlsbColor.Rgb(0x203764), XlsbColor.Rgb(0xFFFFFF), true)); // 0: header row
        book.Dxfs.Add((null, XlsbColor.Rgb(0xC00000), false));                   // 1: data rows
        book.Dxfs.Add((XlsbColor.Rgb(0xFFF2CC), null, false));                   // 2: total row
        book.Dxfs.Add((null, null, false));                                      // 3: data border, medium green below
        book.DxfBorders[3] = (2, XlsbColor.Rgb(0x00B050));
        book.Dxfs.Add((XlsbColor.Rgb(0xE2EFDA), null, false));                   // 4: column C's data rows
        book.Sheets[0].TableFormats[0] = (0, 1, 2, 3, [(2, -1, 4, -1)]);
        var (xlsb, xlsx) = Twins(book);
        foreach (var sheet in new[] { xlsb.Sheets[0], xlsx.Sheets[0] })
        {
            CellStyle At(int row, int column) => sheet.Get(row, column)!.Style;
            Assert.Equal(("#203764", "#FFFFFF", true), (At(0, 0).Fill, At(0, 0).Color, At(0, 0).Bold));
            Assert.Equal("#C00000", At(1, 0).Color);
            // B3's own style (bold white on #1F4E79) stays over the table's formats.
            Assert.Equal(("#1F4E79", "#FFFFFF"), (At(2, 1).Fill, At(2, 1).Color));
            // The border format is the data rows' outline: below the last data row only.
            Assert.Equal(new CellBorder("medium", "#00B050"), At(3, 0).Bottom);
            Assert.NotEqual(new CellBorder("medium", "#00B050"), At(1, 0).Bottom);
            // Column C's own format, on the blank C4 too; the total row's fill.
            Assert.Equal(("#E2EFDA", "#E2EFDA"), (At(1, 2).Fill, At(3, 2).Fill));
            Assert.Equal(("#FFF2CC", "#FFF2CC"), (At(4, 0).Fill, At(4, 2).Fill));
        }
        Assert.Equal(Cells(xlsx.Sheets[0]), Cells(xlsb.Sheets[0]));

        // Formats on a table with a style Filee doesn't know still apply.
        var unknown = Sales("NoSuchStyle");
        unknown.Dxfs.Add((XlsbColor.Rgb(0xFFF2CC), null, false));
        unknown.Sheets[0].TableFormats[0] = (-1, 0, -1, -1, []);
        Assert.Equal("#FFF2CC", Twins(unknown).Xlsb.Sheets[0].Get(1, 0)!.Style.Fill);
    }

    [Fact]
    public void Column_formats_over_a_whole_sheet_table_are_applied_within_the_budget_quickly()
    {
        var clock = Stopwatch.StartNew();
        var book = new XlsbBuilder();
        var sheet = book.Sheet("Big").Text(0, 0, "Head").Text(5000, 3, "Far");
        book.Dxfs.Add((XlsbColor.Rgb(0xFFF2CC), null, false));
        sheet.Tables.Add(((0, 0, 1_048_575, 16_383), "TableStyleMedium2", true, true, true));
        sheet.TableFormats[0] = (0, 0, 0, 0, [.. Enumerable.Range(0, 16_384).Select(c => (c, 0, 0, 0))]);
        var (xlsb, xlsx) = Twins(book);
        foreach (var read in new[] { xlsb.Sheets[0], xlsx.Sheets[0] })
        {
            Assert.InRange(read.Rows.Sum(r => r.Value.Count), 2, new TableBudget().Cells + 2);
            Assert.Equal("#FFF2CC", read.Get(5000, 3)!.Style.Fill);
        }
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(120), clock.Elapsed.ToString());
    }

    // ───────────────────────── XLSX shapes with text ─────────────────────────

    [Fact]
    public void Text_in_xlsx_shapes_prints_with_its_fonts_and_alignment()
    {
        var book = Budget();
        var sheet = book.Sheets[0];
        sheet.DrawingId = "rIdD";
        sheet.Relationships.Add(("rIdD", "drawing", "../drawings/drawing1.xml"));
        const long Emu = 12700;
        book.Parts["xl/drawings/drawing1.xml"] =
            "<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">" +
            $"<xdr:absoluteAnchor><xdr:pos x=\"{200 * Emu}\" y=\"{60 * Emu}\"/><xdr:ext cx=\"{160 * Emu}\" cy=\"{80 * Emu}\"/>" +
            "<xdr:sp><xdr:nvSpPr><xdr:cNvPr id=\"2\" name=\"Rectangle 1\"/><xdr:cNvSpPr/></xdr:nvSpPr>" +
            "<xdr:spPr><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:solidFill><a:srgbClr val=\"1F4E79\"/></a:solidFill></xdr:spPr>" +
            "<xdr:style><a:lnRef idx=\"2\"><a:schemeClr val=\"accent1\"/></a:lnRef><a:fillRef idx=\"1\"><a:schemeClr val=\"accent1\"/></a:fillRef>" +
            "<a:effectRef idx=\"0\"><a:schemeClr val=\"accent1\"/></a:effectRef><a:fontRef idx=\"minor\"><a:schemeClr val=\"lt1\"/></a:fontRef></xdr:style>" +
            "<xdr:txBody><a:bodyPr rtlCol=\"0\" anchor=\"ctr\" lIns=\"0\"/><a:lstStyle/>" +
            "<a:p><a:pPr algn=\"ctr\"/><a:r><a:rPr lang=\"en-US\" sz=\"1400\" b=\"1\"/><a:t>Total</a:t></a:r></a:p>" +
            "<a:p><a:r><a:rPr lang=\"en-US\"><a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill></a:rPr><a:t>up</a:t></a:r><a:br/><a:r><a:rPr lang=\"en-US\"/><a:t>5 %</a:t></a:r></a:p>" +
            "</xdr:txBody></xdr:sp><xdr:clientData/></xdr:absoluteAnchor></xdr:wsDr>";
        var folder = fx.NewFolder();
        var xlsxPath = book.SaveXlsx(Path.Combine(folder, "shape.xlsx"));
        Assert.Null(SheetCapabilities.FirstUnsupported(xlsxPath, "xlsx"));
        var (xlsb, xlsx) = Twins(book);
        foreach (var read in new[] { xlsb, xlsx })
        {
            var shape = Assert.Single(read.Sheets[0].Drawings.OfType<SheetShape>());
            var text = shape.Text!;
            Assert.Equal((CellVerticalAlign.Center, 0.0, "#FFFFFF", 11.0), (text.Anchor, text.Insets.Left, text.Font.Color, text.Font.FontSize));
            Assert.Equal([CellAlign.Center, CellAlign.Left], text.Paragraphs.Select(p => p.Align));
            var total = Assert.Single(text.Paragraphs[0].Runs);
            Assert.Equal(("Total", true, 14.0), (total.Text, total.Bold, total.FontSize));
            Assert.Equal([("up", "#FF0000"), ("\n5 %", null)], text.Paragraphs[1].Runs.Select(r => (r.Text, r.Color)));
        }
        var page = Assert.Single(InLanguage("en-US", () => Words(xlsb, "shape-text")));
        Assert.Contains("Total", page);
        Assert.Contains("up", page);
        // White text on the shape's dark fill.
        using var bitmap = Page(xlsb);
        var (left, top) = ((int)(xlsb.Sheets[0].PageSetup.LeftMargin + 200), (int)(xlsb.Sheets[0].PageSetup.TopMargin + 60));
        var white = 0;
        for (var y = top + 2; y < top + 78; y++)
            for (var x = left + 2; x < left + 158; x++)
                white += Near(bitmap.GetPixel(x, y), SKColors.White, 40) ? 1 : 0;
        Assert.True(white > 20, white.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // ───────────────────────── Shape text: text areas, bullets, autofit, vertical, linked ─────────────────────────

    private const string DrawingNamespaces = "xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" " +
                                             "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"";

    /// <summary>
    /// A shape at (<paramref name="x"/>, <paramref name="y"/>) points from the sheet's corner, <paramref name="w"/> ×
    /// <paramref name="h"/>, outlined in black, with <paramref name="bodyPr"/> and <paramref name="paragraphs"/> as its text.
    /// </summary>
    private static string Shape(string preset, double x, double y, double w, double h, string bodyPr, string paragraphs, string attributes = "")
    {
        const double Emu = 12700;
        return $"<xdr:absoluteAnchor><xdr:pos x=\"{(long)(x * Emu)}\" y=\"{(long)(y * Emu)}\"/><xdr:ext cx=\"{(long)(w * Emu)}\" cy=\"{(long)(h * Emu)}\"/>" +
               $"<xdr:sp{attributes}><xdr:nvSpPr><xdr:cNvPr id=\"2\" name=\"Shape\"/><xdr:cNvSpPr/></xdr:nvSpPr>" +
               $"<xdr:spPr><a:prstGeom prst=\"{preset}\"><a:avLst/></a:prstGeom><a:noFill/><a:ln w=\"9525\"><a:solidFill><a:srgbClr val=\"000000\"/></a:solidFill></a:ln></xdr:spPr>" +
               $"<xdr:txBody>{bodyPr}<a:lstStyle/>{paragraphs}</xdr:txBody></xdr:sp><xdr:clientData/></xdr:absoluteAnchor>";
    }

    /// <summary>Puts <paramref name="anchors"/> on the first sheet as its drawing.</summary>
    private static void Draw(XlsbBuilder book, params string[] anchors)
    {
        var sheet = book.Sheets[0];
        sheet.DrawingId = "rIdD";
        sheet.Relationships.Add(("rIdD", "drawing", "../drawings/drawing1.xml"));
        book.Parts["xl/drawings/drawing1.xml"] = $"<xdr:wsDr {DrawingNamespaces}>{string.Concat(anchors)}</xdr:wsDr>";
    }

    /// <summary>The letters of the first printed page and the page's height (PDF coordinates: points, y up).</summary>
    private static (List<UglyToad.PdfPig.Content.Letter> Letters, double Height) Letters(Workbook book)
    {
        var pdf = SheetPrinter.ToPdf(book, new SheetPrintSource("book.xlsx", "C:\\"), TestContext.Current.CancellationToken, now: Now);
        using var document = PdfDocument.Open(pdf);
        var page = document.GetPage(1);
        return ([.. page.Letters], page.Height);
    }

    /// <summary>The left edge of the first word <paramref name="word"/> on the first page, in points.</summary>
    private static double LeftOf(Workbook book, string word)
    {
        var pdf = SheetPrinter.ToPdf(book, new SheetPrintSource("book.xlsx", "C:\\"), TestContext.Current.CancellationToken, now: Now);
        using var document = PdfDocument.Open(pdf);
        return document.GetPage(1).GetWords().First(w => w.Text == word).BoundingBox.Left;
    }

    [Fact]
    public void Shape_text_keeps_to_its_presets_text_box_with_bullets_indents_and_the_saved_autofit()
    {
        var book = Budget();
        Draw(book,
            // Left-aligned text in an ellipse starts inside the ellipse's text box (its inscribed rectangle), in a
            // rectangle at the box's edge.
            Shape("ellipse", 40, 40, 200, 100, "<a:bodyPr anchor=\"t\"/>", "<a:p><a:pPr algn=\"l\"/><a:r><a:rPr lang=\"en-US\"/><a:t>Oval</a:t></a:r></a:p>"),
            Shape("rect", 40, 150, 200, 40, "<a:bodyPr anchor=\"t\"/>", "<a:p><a:r><a:rPr lang=\"en-US\"/><a:t>Boxy</a:t></a:r></a:p>"),
            // A hanging bullet in its own font, numbers counted per level and restarted, a symbol font's bullet.
            Shape("rect", 260, 40, 200, 150, "<a:bodyPr anchor=\"t\"/>",
                "<a:p><a:pPr marL=\"285750\" indent=\"-285750\"><a:buFont typeface=\"Arial\"/><a:buChar char=\"•\"/></a:pPr><a:r><a:rPr lang=\"en-US\"/><a:t>Apples</a:t></a:r></a:p>" +
                "<a:p><a:pPr marL=\"285750\" indent=\"-285750\"><a:buAutoNum type=\"arabicPeriod\"/></a:pPr><a:r><a:rPr lang=\"en-US\"/><a:t>Pears</a:t></a:r></a:p>" +
                "<a:p><a:pPr marL=\"285750\" indent=\"-285750\"><a:buAutoNum type=\"arabicPeriod\"/></a:pPr><a:r><a:rPr lang=\"en-US\"/><a:t>Plums</a:t></a:r></a:p>" +
                "<a:p><a:pPr marL=\"285750\" indent=\"-285750\"><a:buClr><a:srgbClr val=\"FF0000\"/></a:buClr><a:buSzPct val=\"150000\"/><a:buAutoNum type=\"alphaLcParenR\" startAt=\"3\"/></a:pPr><a:r><a:rPr lang=\"en-US\"/><a:t>Figs</a:t></a:r></a:p>" +
                "<a:p><a:pPr marL=\"285750\" indent=\"-285750\"><a:buFont typeface=\"Wingdings\"/><a:buChar char=\"§\"/></a:pPr><a:r><a:rPr lang=\"en-US\"/><a:t>Kiwis</a:t></a:r></a:p>" +
                "<a:p><a:pPr><a:buNone/></a:pPr><a:r><a:rPr lang=\"en-US\"/><a:t>Plain</a:t></a:r></a:p>"),
            // "Shrink text on overflow" as Excel saved it: half the size, lines 20 % closer.
            Shape("rect", 40, 220, 200, 40, "<a:bodyPr><a:normAutofit fontScale=\"50000\" lnSpcReduction=\"20000\"/></a:bodyPr>",
                "<a:p><a:r><a:rPr lang=\"en-US\" sz=\"2000\"/><a:t>Large</a:t></a:r></a:p><a:p><a:r><a:rPr lang=\"en-US\"/><a:t>small</a:t></a:r></a:p>"));
        var (xlsb, xlsx) = Twins(book);
        foreach (var read in new[] { xlsb, xlsx })
        {
            var shapes = read.Sheets[0].Drawings.OfType<SheetShape>().ToList();
            var list = shapes[2].Text!.Paragraphs;
            Assert.Equal(["•", "1.", "2.", "c)", Hwp.Hwpx.SymbolFonts.Map("Wingdings", "§"), null], list.Select(p => p.Bullet?.Text));
            Assert.Equal((22.5, -22.5), (list[0].Left, list[0].Indent));
            Assert.Equal(("Arial", "#FF0000", 16.5), (list[0].Bullet!.FontName, list[3].Bullet!.Color, list[3].Bullet!.FontSize));
            var shrunk = shapes[3].Text!;
            Assert.Equal((5.5, 10.0, 5.5), (shrunk.Font.FontSize!.Value, shrunk.Paragraphs[0].Runs[0].FontSize!.Value, shrunk.Paragraphs[1].Runs[0].FontSize!.Value));
            Assert.Equal(0.8, shrunk.Paragraphs[0].LineSpacing, 6);
        }
        var left = xlsb.Sheets[0].PageSetup.LeftMargin;
        // The ellipse's text box starts 14.6 % of its width in (200 × (1 − cos 45°) / 2), then the 7.2 pt inset.
        Assert.InRange(LeftOf(xlsb, "Oval") - left, 40 + 29.3 + 7.2 - 1, 40 + 29.3 + 7.2 + 3);
        Assert.InRange(LeftOf(xlsb, "Boxy") - left, 40 + 7.2 - 1, 40 + 7.2 + 3);
        // The bullet hangs at the box's inset, the text starts at the left margin (22.5 pt further).
        Assert.InRange(LeftOf(xlsb, "Apples") - left, 260 + 7.2 + 22.5 - 1, 260 + 7.2 + 22.5 + 3);
        Assert.InRange(LeftOf(xlsb, "1.") - left, 260 + 7.2 - 1, 260 + 7.2 + 3);
        Assert.InRange(LeftOf(xlsb, "c)") - left, 260 + 7.2 - 1, 260 + 7.2 + 3);
        Assert.InRange(LeftOf(xlsb, "Plain") - left, 260 + 7.2 - 1, 260 + 7.2 + 3);
        var page = Assert.Single(InLanguage("en-US", () => Words(xlsb, "shape-bullets")));
        Assert.Contains("2. Plums", page);
    }

    [Fact]
    public void Vertical_shape_text_runs_down_or_up_and_east_asian_characters_stand_upright()
    {
        var book = Budget();
        Draw(book,
            Shape("rect", 40, 40, 40, 160, "<a:bodyPr vert=\"vert\" anchor=\"t\"/>", "<a:p><a:r><a:rPr lang=\"en-US\"/><a:t>DOWN</a:t></a:r></a:p>"),
            Shape("rect", 120, 40, 40, 160, "<a:bodyPr vert=\"vert270\" anchor=\"t\"/>", "<a:p><a:r><a:rPr lang=\"en-US\"/><a:t>UPWARD</a:t></a:r></a:p>"),
            Shape("rect", 200, 40, 40, 160, "<a:bodyPr vert=\"eaVert\" anchor=\"t\"/>", "<a:p><a:r><a:rPr lang=\"ko-KR\"/><a:t>세로 ok</a:t></a:r></a:p>"),
            Shape("rect", 280, 40, 40, 160, "<a:bodyPr vert=\"wordArtVert\" anchor=\"t\"/>", "<a:p><a:r><a:rPr lang=\"en-US\"/><a:t>TALL</a:t></a:r></a:p>"));
        var (xlsb, xlsx) = Twins(book);
        foreach (var read in new[] { xlsb, xlsx })
        {
            Assert.Equal([ShapeTextFlow.Down, ShapeTextFlow.Up, ShapeTextFlow.EastAsian, ShapeTextFlow.Stacked],
                read.Sheets[0].Drawings.OfType<SheetShape>().Select(s => s.Text!.Flow));
        }
        var (letters, height) = Letters(xlsb);
        var left = xlsb.Sheets[0].PageSetup.LeftMargin;
        var top = xlsb.Sheets[0].PageSetup.TopMargin;
        // (x, y down the page) of each letter's baseline start.
        List<(double X, double Y, bool Across)> Of(string text, double from, double to)
        {
            // The letters of the box in drawing order, matched one by one.
            var inBox = letters.Where(l => l.StartBaseLine.X - left >= from && l.StartBaseLine.X - left <= to).ToList();
            var found = new List<(double, double, bool)>();
            var at = 0;
            foreach (var c in text)
            {
                at = inBox.FindIndex(at, l => l.Value == c.ToString());
                var l = inBox[at++];
                found.Add((l.StartBaseLine.X - left, height - l.StartBaseLine.Y - top,
                    Math.Abs(l.EndBaseLine.Y - l.StartBaseLine.Y) < 0.1 && l.EndBaseLine.X > l.StartBaseLine.X));
            }
            return found;
        }
        // Turned 90° clockwise: the letters go down the box, near its right edge, lying on their side.
        var down = Of("DOWN", 40, 80);
        Assert.All(down, l => Assert.False(l.Across));
        Assert.True(down.Zip(down.Skip(1)).All(p => p.Second.Y > p.First.Y + 3 && Math.Abs(p.Second.X - p.First.X) < 2), string.Join(" ", down));
        Assert.InRange(down[0].Y, 40, 60);
        // Turned 270°: up the box from its bottom.
        var up = Of("UPWARD", 120, 160);
        Assert.True(up.Zip(up.Skip(1)).All(p => p.Second.Y < p.First.Y - 3 && Math.Abs(p.Second.X - p.First.X) < 2), string.Join(" ", up));
        Assert.InRange(up[0].Y, 170, 200);
        // East Asian: the Hangul stands upright, the Latin lies on its side; both go down.
        var hangul = Of("세로", 200, 240);
        Assert.All(hangul, l => Assert.True(l.Across));
        Assert.True(hangul[1].Y > hangul[0].Y + 5, string.Join(" ", hangul));
        Assert.All(Of("ok", 200, 240), l => Assert.False(l.Across));
        // Stacked: every letter upright, one under the other.
        var tall = Of("TALL", 280, 320);
        Assert.All(tall, l => Assert.True(l.Across));
        Assert.True(tall.Zip(tall.Skip(1)).All(p => p.Second.Y > p.First.Y + 3), string.Join(" ", tall));
    }

    [Fact]
    public void Shape_text_linked_to_a_cell_shows_the_cells_text()
    {
        var book = Budget();
        book.Sheets[0].Text(4, 1, "Linked value");
        book.Sheet("Data sheet").Text(0, 0, "From data");
        book.Sheet("Hidden", state: 1).Text(0, 0, "Hidden text");
        Draw(book,
            Shape("rect", 40, 120, 160, 30, "<a:bodyPr/>",
                "<a:p><a:pPr algn=\"ctr\"/><a:fld id=\"{00000000-0000-0000-0000-000000000001}\" type=\"TxLink\"><a:rPr lang=\"en-US\" b=\"1\"/><a:t>old text</a:t></a:fld></a:p>",
                " textlink=\"$B$5\""),
            Shape("rect", 220, 120, 160, 30, "<a:bodyPr/>", "<a:p><a:r><a:rPr lang=\"en-US\"/><a:t>stale</a:t></a:r></a:p>", " textlink=\"'Data sheet'!$A$1\""),
            Shape("rect", 40, 170, 160, 30, "<a:bodyPr/>", "<a:p><a:r><a:rPr lang=\"en-US\"/><a:t>Saved</a:t></a:r></a:p>", " textlink=\"Hidden!A1\""),
            Shape("rect", 220, 170, 160, 30, "<a:bodyPr/>", "<a:p><a:r><a:rPr lang=\"en-US\"/><a:t>gone</a:t></a:r></a:p>", " textlink=\"$D$9\""));
        var folder = fx.NewFolder();
        Assert.Null(SheetCapabilities.FirstUnsupported(book.SaveXlsx(Path.Combine(folder, "linked.xlsx")), "xlsx"));
        var (xlsb, xlsx) = Twins(book);
        foreach (var read in new[] { xlsb, xlsx })
        {
            var texts = read.Sheets[0].Drawings.OfType<SheetShape>().Select(s => s.Text!.Paragraphs.SelectMany(p => p.Runs).ToList()).ToList();
            // The cell's text in the linked run's font and the paragraph's alignment; a hidden sheet's link keeps the
            // saved text; an empty cell leaves the shape empty.
            Assert.Equal(("Linked value", true), (Assert.Single(texts[0]).Text, Assert.Single(texts[0]).Bold));
            Assert.Equal(CellAlign.Center, read.Sheets[0].Drawings.OfType<SheetShape>().First().Text!.Paragraphs[0].Align);
            Assert.Equal("From data", Assert.Single(texts[1]).Text);
            Assert.Equal("Saved", Assert.Single(texts[2]).Text);
            Assert.Empty(texts[3]);
        }
        var page = InLanguage("en-US", () => Words(xlsb, "shape-linked"))[0];
        Assert.Contains("Linked value", page);
        Assert.DoesNotContain("old text", page);
    }

    // ───────────────────────── Cell styles of tables, PivotTable styles ─────────────────────────

    [Fact]
    public void A_tables_named_cell_styles_go_under_its_formats_and_its_cells_own()
    {
        var book = Sales("TableStyleLight1");
        book.CellStyles.Add(("Accent heading", 1, 2, 0)); // bold white on #1F4E79
        book.CellStyles.Add(("Boxed", 0, 0, 1));          // thin borders all round
        book.Sheets[0].TableCellStyles[0] = ("Accent heading", null, null, [(2, null, "Boxed", null)]);
        var (xlsb, xlsx) = Twins(book);
        foreach (var sheet in new[] { xlsb.Sheets[0], xlsx.Sheets[0] })
        {
            CellStyle At(int row, int column) => sheet.Get(row, column)!.Style;
            Assert.Equal(("#1F4E79", "#FFFFFF", true), (At(0, 0).Fill, At(0, 0).Color, At(0, 0).Bold));
            Assert.Equal(("thin", "thin"), (At(1, 2).Left.Style, At(1, 2).Bottom.Style));
            // The blank C4 of column C's data rows gets the borders too; column B's cells don't.
            Assert.Equal("thin", At(3, 2).Top.Style);
            Assert.False(At(1, 1).Left.IsVisible);
        }
        Assert.Equal(Cells(xlsx.Sheets[0]), Cells(xlsb.Sheets[0]));

        // The table's own header format goes over the cell style it names.
        book.Dxfs.Add((XlsbColor.Rgb(0xFFF2CC), null, false));
        book.Sheets[0].TableFormats[0] = (0, -1, -1, -1, []);
        var (formatted, formattedXlsx) = Twins(book);
        Assert.Equal(("#FFF2CC", "#FFFFFF"), (formatted.Sheets[0].Get(0, 0)!.Style.Fill, formatted.Sheets[0].Get(0, 0)!.Style.Color));
        Assert.Equal(Cells(formattedXlsx.Sheets[0]), Cells(formatted.Sheets[0]));
    }

    /// <summary>
    /// A compact PivotTable on A3:B9 with a page field in A1:B1: two row fields (region, city), one data column, the
    /// grand total row; its values in the sheet's cells as Excel saves them.
    /// </summary>
    private static XlsbBuilder Pivot(string style, bool rowHeaders = true)
    {
        var book = new XlsbBuilder();
        var sheet = book.Sheet("Pivot").Text(0, 0, "Year").Text(0, 1, "(All)")
            .Text(2, 0, "Row Labels").Text(2, 1, "Sum of Sales")
            .Text(3, 0, "East").Number(3, 1, 300).Text(4, 0, "Seoul").Number(4, 1, 100).Text(5, 0, "Busan").Number(5, 1, 200)
            .Text(6, 0, "West").Number(6, 1, 50).Text(7, 0, "LA").Number(7, 1, 50)
            .Text(8, 0, "Grand Total").Number(8, 1, 350)
            .Width(0, 1, 16);
        sheet.PageSetup = A4;
        sheet.Pivots.Add(new XlsbPivot((2, 0, 8, 1), 1, 1, 1, style)
        {
            RowFields = 2,
            PageFields = 1,
            RowHeaders = rowHeaders,
            Rows = [(null, 0, 1), (null, 1, 1), (null, 1, 1), (null, 0, 1), (null, 1, 1), ("grand", 0, 1)],
        });
        return book;
    }

    [Fact]
    public void A_pivot_table_prints_in_its_built_in_pivot_style()
    {
        var (xlsb, xlsx) = Twins(Pivot("PivotStyleLight16"));
        var light = Tinted(4, 0.79998168889431442);
        var line = new CellBorder("thin", Tinted(4, 0.39997558519241921));
        foreach (var sheet in new[] { xlsb.Sheets[0], xlsx.Sheets[0] })
        {
            CellStyle At(int row, int column) => sheet.Get(row, column)!.Style;
            // Header row: bold on accent 1 at 80 %, a line below; the grand total row likewise with a line above.
            Assert.Equal((light, true, line), (At(2, 1).Fill, At(2, 1).Bold, At(2, 1).Bottom));
            Assert.Equal((light, true, line), (At(8, 0).Fill, At(8, 0).Bold, At(8, 0).Top));
            // The outer field's items (row subheading 1): bold across the table with a line below; the inner ones plain.
            Assert.Equal((true, true, line), (At(3, 0).Bold, At(6, 1).Bold, At(3, 1).Bottom));
            Assert.Equal((false, null), (At(4, 0).Bold, At(4, 0).Fill));
            // The page field's caption and value.
            Assert.Equal((light, light), (At(0, 0).Fill, At(0, 1).Fill));
        }
        Assert.Equal(Cells(xlsx.Sheets[0]), Cells(xlsb.Sheets[0]));
        Assert.Contains("Grand Total", InLanguage("en-US", () => Words(xlsb, "pivot-light16"))[0]);
        // Without row headers the row subheadings are plain.
        Assert.False(Twins(Pivot("PivotStyleLight16", rowHeaders: false)).Xlsb.Sheets[0].Get(3, 0)!.Style.Bold);
    }

    [Fact]
    public void A_custom_pivot_style_colours_subtotals_by_their_fields_level()
    {
        var book = new XlsbBuilder();
        var sheet = book.Sheet("Pivot").Text(0, 0, "Row Labels").Text(0, 1, "Sum");
        for (var row = 1; row <= 6; row++)
            sheet.Text(row, 0, $"Line {row}").Number(row, 1, row);
        sheet.PageSetup = A4;
        book.Dxfs.Add((XlsbColor.Rgb(0xFF0000), null, false)); // 0: first subtotal rows
        book.Dxfs.Add((XlsbColor.Rgb(0x00FF00), null, false)); // 1: second
        book.Dxfs.Add((XlsbColor.Rgb(0x0000FF), null, false)); // 2: third
        book.Dxfs.Add((XlsbColor.Rgb(0xFFFF00), null, true));  // 3: grand total row
        book.PivotStyles.Add(("Own pivot", [("firstSubtotalRow", 0), ("secondSubtotalRow", 1), ("thirdSubtotalRow", 2), ("totalRow", 3)]));
        // Subtotals of fields 0, 1, 2 and 3, a blank line, the grand total: the first, then the second and third by turns.
        sheet.Pivots.Add(new XlsbPivot((0, 0, 6, 1), 1, 1, 1, "Own pivot")
        {
            RowFields = 4,
            Rows = [("default", 0, 1), ("default", 1, 1), ("default", 2, 1), ("sum", 3, 1), ("blank", 0, 1), ("grand", 0, 1)],
        });
        var (xlsb, xlsx) = Twins(book);
        foreach (var read in new[] { xlsb.Sheets[0], xlsx.Sheets[0] })
        {
            Assert.Equal(["#FF0000", "#00FF00", "#0000FF", "#00FF00", null, "#FFFF00"], Enumerable.Range(1, 6).Select(r => read.Get(r, 1)!.Style.Fill));
            Assert.True(read.Get(6, 0)!.Style.Bold);
        }
        Assert.Equal(Cells(xlsx.Sheets[0]), Cells(xlsb.Sheets[0]));
    }

    [Fact]
    public void A_pivot_table_over_a_whole_sheet_is_styled_within_the_budget_quickly()
    {
        var clock = Stopwatch.StartNew();
        var book = new XlsbBuilder();
        var sheet = book.Sheet("Big").Text(0, 0, "Head").Text(5000, 3, "Far");
        sheet.Pivots.Add(new XlsbPivot((0, 0, 1_048_575, 16_383), 1, 1, 1, "PivotStyleMedium9")
        {
            RowStripes = true,
            ColumnStripes = true,
            Rows = [(null, 0, 1), ("grand", 0, 1)],
            Columns = [.. Enumerable.Range(0, 300).Select(_ => ((string?)null, 0, 1))],
        });
        var (xlsb, xlsx) = Twins(book);
        foreach (var read in new[] { xlsb.Sheets[0], xlsx.Sheets[0] })
        {
            Assert.InRange(read.Rows.Sum(r => r.Value.Count), 2, new TableBudget().Cells + 2);
            Assert.True(read.Get(0, 0)!.Style.Bold);
        }
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(120), clock.Elapsed.ToString());
    }

    // ───────────────────────── ODS drawings ─────────────────────────

    private static byte[] Halves(SKColor left, SKColor right, int width = 40, int height = 20)
    {
        using var bitmap = new SKBitmap(width, height);
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
                bitmap.SetPixel(x, y, x < width / 2 ? left : right);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private const string GraphicStyles =
        "<style:style " + DrawNamespaces + " style:name=\"gr1\" style:family=\"graphic\"><style:graphic-properties draw:fill=\"solid\" draw:fill-color=\"#ffff00\" svg:stroke-color=\"#000000\"/></style:style>" +
        "<style:style " + DrawNamespaces + " style:name=\"gr2\" style:family=\"graphic\"><style:graphic-properties draw:fill=\"solid\" draw:fill-color=\"#ff0000\" draw:stroke=\"none\"/></style:style>" +
        "<style:style " + DrawNamespaces + " style:name=\"gr3\" style:family=\"graphic\"><style:graphic-properties draw:fill=\"gradient\" draw:fill-gradient-name=\"Blue\" draw:stroke=\"none\"/></style:style>" +
        "<style:style " + DrawNamespaces + " style:name=\"gr4\" style:family=\"graphic\"><style:graphic-properties fo:clip=\"rect(0cm, 0.529166cm, 0cm, 0cm)\"/></style:style>" +
        "<style:style " + DrawNamespaces + " style:name=\"gr5\" style:family=\"graphic\"><style:graphic-properties draw:fill=\"solid\" draw:fill-color=\"#00b050\" draw:stroke=\"none\"/></style:style>" +
        "<style:style style:name=\"T1\" style:family=\"text\"><style:text-properties fo:font-weight=\"bold\" fo:color=\"#c00000\"/></style:style>";

    private const string Gradients =
        "<draw:gradient " + DrawNamespaces + " draw:name=\"Blue\" draw:style=\"linear\" draw:start-color=\"#0000ff\" draw:end-color=\"#ffffff\" draw:angle=\"0\" draw:border=\"0%\"/>";

    [Fact]
    public void Ods_text_freeform_shapes_cropped_pictures_and_gradients_print()
    {
        const double Cm = 72 / 2.54;
        var shapes =
            // A yellow rectangle with a line of text in two styles, a text box, a red triangle (polygon), a green
            // triangle of its own path, a gradient from blue at the top to white, and a picture whose right half is cut off.
            "<draw:custom-shape draw:style-name=\"gr1\" svg:x=\"1cm\" svg:y=\"1cm\" svg:width=\"5cm\" svg:height=\"2cm\">" +
            "<text:p>Hello <text:span text:style-name=\"T1\">world</text:span></text:p><draw:enhanced-geometry draw:type=\"rectangle\"/></draw:custom-shape>" +
            "<draw:frame svg:x=\"7cm\" svg:y=\"1cm\" svg:width=\"5cm\" svg:height=\"1cm\"><draw:text-box><text:p>Boxed note</text:p></draw:text-box></draw:frame>" +
            "<draw:polygon draw:style-name=\"gr2\" svg:x=\"1cm\" svg:y=\"4cm\" svg:width=\"2cm\" svg:height=\"2cm\" svg:viewBox=\"0 0 2000 2000\" draw:points=\"0,2000 1000,0 2000,2000\"/>" +
            "<draw:custom-shape draw:style-name=\"gr5\" svg:x=\"4cm\" svg:y=\"4cm\" svg:width=\"2cm\" svg:height=\"2cm\">" +
            "<draw:enhanced-geometry svg:viewBox=\"0 0 21600 21600\" draw:enhanced-path=\"M 0 0 L 21600 0 0 21600 Z N\"/></draw:custom-shape>" +
            "<draw:rect draw:style-name=\"gr3\" svg:x=\"7cm\" svg:y=\"4cm\" svg:width=\"2cm\" svg:height=\"4cm\"/>" +
            "<draw:frame draw:style-name=\"gr4\" svg:x=\"10cm\" svg:y=\"4cm\" svg:width=\"2cm\" svg:height=\"2cm\"><draw:image xlink:href=\"Pictures/halves.png\"/></draw:frame>";
        var tables = $"<table:table table:name=\"Sheet1\" {DrawNamespaces}><table:shapes>{shapes}</table:shapes><table:table-column table:number-columns-repeated=\"5\"/>" +
                     "<table:table-row><table:table-cell office:value-type=\"string\"><text:p>x</text:p></table:table-cell></table:table-row></table:table>";
        var path = OdsBuilder.Save(Path.Combine(fx.NewFolder(), "shapes.ods"), GraphicStyles, tables, Gradients,
            media: new Dictionary<string, byte[]> { ["Pictures/halves.png"] = Halves(SKColors.Red, SKColors.Blue) });
        Assert.Null(SheetCapabilities.FirstUnsupported(path, "ods"));

        var book = OdsReader.Read(path);
        var drawings = book.Sheets[0].Drawings;
        Assert.Equal(6, drawings.Count);
        var text = Assert.IsType<SheetShape>(drawings[0]).Text!;
        Assert.Equal([("Hello ", (bool?)null), ("world", true)], text.Paragraphs.Single().Runs.Select(r => (r.Text, r.Bold)));
        Assert.Equal(CellAlign.Center, text.Paragraphs[0].Align);
        Assert.Equal("Boxed note", Assert.IsType<SheetShape>(drawings[1]).Text!.Paragraphs[0].Runs[0].Text);
        Assert.NotNull(Assert.IsType<SheetShape>(drawings[2]).Geometry);
        Assert.NotNull(Assert.IsType<SheetShape>(drawings[4]).Gradient);
        var picture = Assert.IsType<SheetPicture>(drawings[5]);
        Assert.Equal(0.5, picture.Crop.Right, 2);

        var page = Assert.Single(Words(book, "ods-shapes"));
        Assert.Contains("Hello world", page);
        Assert.Contains("Boxed note", page);
        using var bitmap = Page(book);
        var setup = book.Sheets[0].PageSetup;
        SKColor At(double xCm, double yCm) => bitmap.GetPixel((int)(setup.LeftMargin + xCm * Cm), (int)(setup.TopMargin + yCm * Cm));
        Assert.True(Near(At(1.2, 1.2), SKColors.Yellow), At(1.2, 1.2).ToString());
        // The triangles: filled where they are, empty in the corner they leave out.
        Assert.True(Near(At(2, 5.8), SKColors.Red), At(2, 5.8).ToString());
        Assert.True(Near(At(1.1, 4.1), SKColors.White), At(1.1, 4.1).ToString());
        Assert.True(Near(At(4.2, 4.2), new SKColor(0x00, 0xB0, 0x50)), At(4.2, 4.2).ToString());
        Assert.True(Near(At(5.8, 5.8), SKColors.White), At(5.8, 5.8).ToString());
        // Blue at the top of the gradient, nearly white at its foot.
        Assert.True(At(8, 4.1).Blue > 200 && At(8, 4.1).Red < 40, At(8, 4.1).ToString());
        Assert.True(At(8, 7.9).Red > 220, At(8, 7.9).ToString());
        // The picture's left (red) half fills the frame.
        Assert.True(Near(At(11.8, 5), SKColors.Red), At(11.8, 5).ToString());
    }

    [Fact]
    public void Ods_charts_keep_their_place_among_the_other_drawings()
    {
        var shapes = "<draw:rect draw:style-name=\"gr2\" svg:x=\"0cm\" svg:y=\"0cm\" svg:width=\"2cm\" svg:height=\"2cm\"/>" +
                     "<draw:frame svg:x=\"1cm\" svg:y=\"1cm\" svg:width=\"8cm\" svg:height=\"5cm\"><draw:object xlink:href=\"./Object 1\"/></draw:frame>" +
                     "<draw:rect draw:style-name=\"gr5\" svg:x=\"3cm\" svg:y=\"2cm\" svg:width=\"2cm\" svg:height=\"2cm\"/>";
        var tables = $"<table:table table:name=\"Sheet1\" {DrawNamespaces}><table:shapes>{shapes}</table:shapes><table:table-column/>" +
                     "<table:table-row><table:table-cell office:value-type=\"string\"><text:p>x</text:p></table:table-cell></table:table-row></table:table>";
        var path = OdsBuilder.Save(Path.Combine(fx.NewFolder(), "chart.ods"), GraphicStyles, tables,
            media: new Dictionary<string, byte[]> { ["Object 1/content.xml"] = Encoding.UTF8.GetBytes(OdfChartBuilder.Columns()) });
        var drawings = OdsReader.Read(path).Sheets[0].Drawings;
        // The green square comes after the chart in the file, so it is drawn over it.
        Assert.Collection(drawings, d => Assert.IsType<SheetShape>(d), d => Assert.IsType<SheetChart>(d),
            d => Assert.Equal(new SKColor(0x00, 0xB0, 0x50), Assert.IsType<SheetShape>(d).Fill));
    }
}
