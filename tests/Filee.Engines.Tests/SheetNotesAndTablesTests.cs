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
        var pages = Words(xlsb, "notes-at-end");
        Assert.Equal(2, pages.Count);
        Assert.Contains("Rent", pages[0]);
        Assert.DoesNotContain("Check", pages[0]);
        // By row, then column; numbered on with the sheet's pages.
        Assert.Equal("Cell: B2 Comment: Ann: Check the total Cell: A3 Comment: Bo: Due on Friday 2 of 2", pages[1]);
        Assert.EndsWith("1 of 2", pages[0]);
        Assert.Equal(pages, Words(xlsx, "notes-at-end-xlsx"));

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
        var pages = Words(atEnd, "ods-notes-at-end");
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

    /// <summary>A theme colour tinted as Filee tints every SpreadsheetML colour (within a level or two of Excel's).</summary>
    private static string Tinted(int theme, double tint) => Office.Ooxml.OoxmlColors.FromTheme(null).Spreadsheet(new System.Xml.Linq.XElement("color",
        new System.Xml.Linq.XAttribute("theme", theme), new System.Xml.Linq.XAttribute("tint", tint.ToString(System.Globalization.CultureInfo.InvariantCulture))))!;

    [Fact]
    public void A_table_prints_in_its_built_in_style()
    {
        // Accent 1 at 80 % and 40 % tint: Excel shows #D9E1F2 and #8EA9DB.
        var (band, line) = (Tinted(4, 0.8), Tinted(4, 0.4));
        Assert.True(Near(SheetColors.Parse(band, SKColors.Black), new SKColor(0xD9, 0xE1, 0xF2), 3), band);
        Assert.True(Near(SheetColors.Parse(line, SKColors.Black), new SKColor(0x8E, 0xA9, 0xDB), 3), line);
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
        for (var i = 0; i < 40; i++)
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
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), clock.Elapsed.ToString());
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
