// Drawings on printed sheets beyond the XLSX worksheet's own (SheetDrawingTests): pictures and shapes of OpenDocument
// spreadsheets (OdsDrawing), the printed range reaching as far as drawings placed by their size (one-cell and
// absolute anchors), and chart sheets (XLSB chart sheets, pictures, shapes and userShapes over the chart, a chart kept
// at its own size). Every workbook is built here in code.

using Filee.Engines.Documents;
using Filee.Engines.Office.Sheets;
using Filee.Engines.Rendering;
using SkiaSharp;
using static Filee.Engines.Tests.ChartBuilder;

namespace Filee.Engines.Tests;

public sealed class SheetObjectTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const long Emu = 12700;
    private static readonly SKColor Magenta = new(0xFF, 0x00, 0xFF);
    private static readonly SKColor Red = new(0xC0, 0x00, 0x00), Blue = new(0x00, 0x70, 0xC0);

    private const string DrawNamespaces = "xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" " +
                                          "xmlns:svg=\"urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"";

    /// <summary>A picture of one colour.</summary>
    private static byte[] Png(SKColor color, int width = 40, int height = 20)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Pixels within a few steps of <paramref name="color"/> inside <paramref name="area"/> (page points at 72 dpi), or the whole page.</summary>
    private static int Count(SKBitmap bitmap, SKColor color, SKRectI? area = null)
    {
        var box = area ?? new SKRectI(0, 0, bitmap.Width, bitmap.Height);
        var count = 0;
        for (var x = Math.Max(0, box.Left); x < Math.Min(bitmap.Width, box.Right); x++)
        {
            for (var y = Math.Max(0, box.Top); y < Math.Min(bitmap.Height, box.Bottom); y++)
            {
                var p = bitmap.GetPixel(x, y);
                if (Math.Abs(p.Red - color.Red) <= 16 && Math.Abs(p.Green - color.Green) <= 16 && Math.Abs(p.Blue - color.Blue) <= 16)
                    count++;
            }
        }
        return count;
    }

    private static Printed Print(Workbook book) =>
        new(SheetPrinter.Print(book, new SheetPrintSource("book", "C:\\"), DocumentLimits.Default, DateTime.Now, null, TestContext.Current.CancellationToken));

    /// <summary>The printed pages, disposed with the pictures they draw.</summary>
    private sealed class Printed(SheetPrinter.Printed printed) : IDisposable
    {
        public IReadOnlyList<PageContent> Pages => printed.Pages;
        public void Dispose() => printed.Dispose();
    }

    // ───────────────────────── ODS ─────────────────────────

    private const string Green = "<style:style " + DrawNamespaces + " style:name=\"gr1\" style:family=\"graphic\"><style:graphic-properties draw:fill=\"solid\" draw:fill-color=\"#ff00ff\" draw:stroke=\"none\"/></style:style>";

    private string Ods(string tables, string automatic = Green, string styles = "", byte[]? picture = null) =>
        OdsBuilder.Save(Path.Combine(fx.NewFolder(), "book.ods"), automatic, tables, styles,
            media: picture is null ? null : new Dictionary<string, byte[]> { ["Pictures/red.png"] = picture });

    private static string Table(string shapes, string cells = "<table:table-row><table:table-cell/></table:table-row>") =>
        $"<table:table table:name=\"Sheet1\" {DrawNamespaces}>{(shapes.Length > 0 ? $"<table:shapes>{shapes}</table:shapes>" : "")}" +
        $"<table:table-column table:number-columns-repeated=\"5\"/>{cells}</table:table>";

    [Fact]
    public void Ods_pictures_and_shapes_print()
    {
        var shapes = "<draw:frame svg:x=\"1cm\" svg:y=\"1cm\" svg:width=\"4cm\" svg:height=\"2cm\"><draw:image xlink:href=\"Pictures/red.png\"/></draw:frame>" +
                     "<draw:custom-shape draw:style-name=\"gr1\" svg:x=\"7cm\" svg:y=\"1cm\" svg:width=\"3cm\" svg:height=\"2cm\">" +
                     "<draw:enhanced-geometry draw:type=\"rectangle\"/></draw:custom-shape>";
        // Text in A1, so the printed range starts there.
        var path = Ods(Table(shapes, "<table:table-row><table:table-cell office:value-type=\"string\"><text:p>x</text:p></table:table-cell></table:table-row>"), picture: Png(SKColors.Red));
        Assert.Null(SheetCapabilities.FirstUnsupported(path, "ods"));

        var book = OdsReader.Read(path);
        Assert.Collection(book.Sheets[0].Drawings, d => Assert.IsType<SheetPicture>(d), d => Assert.Equal("rect", Assert.IsType<SheetShape>(d).Preset));
        using var printed = Print(book);
        using var page = printed.Pages[0].Render(72);
        var setup = book.Sheets[0].PageSetup;
        var (left, top) = ((int)setup.LeftMargin, (int)setup.TopMargin);
        const double Cm = 72 / 2.54;
        // The picture's 4 × 2 cm and the shape's 3 × 2 cm, each almost wholly in its colour; nothing between them.
        Assert.True(Count(page, SKColors.Red, SKRectI.Create(left + (int)(1 * Cm), top + (int)(1 * Cm), (int)(4 * Cm), (int)(2 * Cm))) > 0.9 * 8 * Cm * Cm,
            $"red {Count(page, SKColors.Red)} magenta {Count(page, Magenta)} margins {left},{top} " + string.Join(";", book.Sheets[0].Drawings.Select(d => $"{d.Row},{d.Column} {d.OffsetX},{d.OffsetY} {d.Width}x{d.Height} {d.To}")));
        Assert.True(Count(page, Magenta, SKRectI.Create(left + (int)(7 * Cm), top + (int)(1 * Cm), (int)(3 * Cm), (int)(2 * Cm))) > 0.9 * 6 * Cm * Cm);
        Assert.Equal(0, Count(page, SKColors.Red, SKRectI.Create(left + (int)(5.2 * Cm), top, (int)(1.5 * Cm), (int)(4 * Cm))));
    }

    [Fact]
    public void Ods_drawings_keep_their_cell_anchor_turn_and_style()
    {
        // In B2: a picture to D5 (moves and sizes with the cells), an ellipse turned a quarter counter-clockwise, a line
        // drawn up to the right, a shape in the default graphic style, and one shown on screen only.
        var cells = "<table:table-row><table:table-cell/></table:table-row><table:table-row><table:table-cell/><table:table-cell>" +
                    "<draw:frame svg:x=\"0.5cm\" svg:y=\"0.25cm\" svg:width=\"4cm\" svg:height=\"2cm\" table:end-cell-address=\"Sheet1.D5\" table:end-x=\"1cm\" table:end-y=\"0.5cm\">" +
                    "<draw:image xlink:href=\"Pictures/red.png\"/></draw:frame>" +
                    "<draw:ellipse draw:style-name=\"gr1\" svg:width=\"2cm\" svg:height=\"1cm\" draw:transform=\"rotate (1.5707963267949) translate (2cm 4cm)\"/>" +
                    "<draw:line svg:x1=\"1cm\" svg:y1=\"2cm\" svg:x2=\"3cm\" svg:y2=\"1cm\"/>" +
                    "<draw:rect svg:x=\"0cm\" svg:y=\"0cm\" svg:width=\"1cm\" svg:height=\"1cm\"/>" +
                    "<draw:rect draw:display=\"screen\" svg:width=\"1cm\" svg:height=\"1cm\"/>" +
                    "<text:p>x</text:p></table:table-cell></table:table-row>";
        var defaults = "<style:default-style " + DrawNamespaces + " style:family=\"graphic\"><style:graphic-properties draw:fill-color=\"#00ff00\" svg:stroke-color=\"#0000ff\" svg:stroke-width=\"0.1cm\"/></style:default-style>";
        var book = OdsReader.Read(Ods(Table("", cells), styles: defaults, picture: Png(SKColors.Red)));
        var drawings = book.Sheets[0].Drawings;
        Assert.True(drawings.Count == 4, string.Join(";", drawings.Select(d => d.GetType().Name + " " + (d as SheetShape)?.Preset + $" {d.Row},{d.Column} {d.OffsetX},{d.OffsetY} {d.Width}x{d.Height}")));
        const double Cm = 72 / 2.54;

        var picture = Assert.IsType<SheetPicture>(drawings[0]);
        Assert.Equal((1, 1), (picture.Row, picture.Column));
        Assert.Equal(0.5 * Cm, picture.OffsetX, 2);
        Assert.Equal((4, 3), (picture.To!.Value.Row, picture.To!.Value.Column));
        Assert.Equal(1 * Cm, picture.To!.Value.OffsetX, 2);

        var ellipse = Assert.IsType<SheetShape>(drawings[1]);
        Assert.Equal("ellipse", ellipse.Preset);
        Assert.Equal(270, ellipse.Rotation, 3);
        Assert.Null(ellipse.To);
        // Turned about its top-left corner and moved there: its centre is at (2 cm + 0.5 cm, 4 cm − 1 cm).
        Assert.Equal(2.5 * Cm, ellipse.OffsetX + ellipse.Width / 2, 1);
        Assert.Equal(3 * Cm, ellipse.OffsetY + ellipse.Height / 2, 1);
        Assert.Equal((2 * Cm, 1 * Cm), (ellipse.Width, ellipse.Height), new Tolerance(0.01));
        Assert.Equal(new SKColor(0xFF, 0x00, 0xFF), ellipse.Fill);
        Assert.Null(ellipse.Line);

        var line = Assert.IsType<SheetShape>(drawings[2]);
        Assert.Equal("line", line.Preset);
        Assert.Equal(new SKColor(0x00, 0x00, 0xFF), line.Line);
        Assert.True(line.FlipH);
        Assert.Equal((1 * Cm, 1 * Cm, 2 * Cm, 1 * Cm), (line.OffsetX, line.OffsetY, line.Width, line.Height), new Tolerance(0.01));

        var plain = Assert.IsType<SheetShape>(drawings[3]);
        Assert.Equal(new SKColor(0x00, 0xFF, 0x00), plain.Fill);
        Assert.Equal(new SKColor(0x00, 0x00, 0xFF), plain.Line);
        Assert.Equal(0.1 * Cm, plain.LineWidth, 2);
    }

    private sealed class Tolerance(double within) : IEqualityComparer<(double, double)>, IEqualityComparer<(double, double, double, double)>
    {
        private bool Near(double a, double b) => Math.Abs(a - b) <= within * Math.Max(1, Math.Abs(b));
        public bool Equals((double, double) x, (double, double) y) => Near(x.Item1, y.Item1) && Near(x.Item2, y.Item2);
        public int GetHashCode((double, double) obj) => 0;
        public bool Equals((double, double, double, double) x, (double, double, double, double) y) =>
            Near(x.Item1, y.Item1) && Near(x.Item2, y.Item2) && Near(x.Item3, y.Item3) && Near(x.Item4, y.Item4);
        public int GetHashCode((double, double, double, double) obj) => 0;
    }

    [Fact]
    public void What_ods_drawings_leave_out_is_named()
    {
        string? Check(string shapes, string automatic = Green) =>
            SheetCapabilities.FirstUnsupported(Ods(Table(shapes), automatic, picture: Png(SKColors.Red)), "ods");
        const string Box = "svg:width=\"2cm\" svg:height=\"1cm\"";

        Assert.Null(Check($"<draw:rect draw:style-name=\"gr1\" {Box}/>"));
        Assert.Null(Check($"<draw:frame {Box}><draw:image xlink:href=\"Pictures/x.svg\"/><draw:image xlink:href=\"Pictures/red.png\"/></draw:frame>"));
        Assert.Equal("SVG pictures", Check($"<draw:frame {Box}><draw:image xlink:href=\"Pictures/x.svg\"/></draw:frame>"));
        Assert.Equal("pictures", Check($"<draw:frame {Box}><draw:image xlink:href=\"Pictures/missing.png\"/></draw:frame>"));
        Assert.Equal("text boxes", Check($"<draw:frame {Box}><draw:text-box><text:p>Note</text:p></draw:text-box></draw:frame>"));
        Assert.Equal("text in shapes", Check($"<draw:custom-shape {Box}><text:p>Label</text:p><draw:enhanced-geometry draw:type=\"ellipse\"/></draw:custom-shape>"));
        Assert.Equal("freeform shapes", Check($"<draw:polygon {Box} draw:points=\"0,0 10,0 5,10\"/>"));
        Assert.Equal("freeform shapes", Check($"<draw:custom-shape {Box}><draw:enhanced-geometry draw:type=\"non-primitive\"/></draw:custom-shape>"));
        Assert.Equal("gradient fills", Check($"<draw:rect draw:style-name=\"gr2\" {Box}/>",
            "<style:style " + DrawNamespaces + " style:name=\"gr2\" style:family=\"graphic\"><style:graphic-properties draw:fill=\"gradient\"/></style:style>"));
        Assert.Equal("arcs", Check($"<draw:circle draw:kind=\"arc\" {Box}/>"));
        Assert.Equal("grouped charts and objects", Check($"<draw:g><draw:frame {Box}><draw:object xlink:href=\"./Object 1\"/></draw:frame></draw:g>"));
        // Inside a group, and on screen only (not printed).
        Assert.Equal("form controls", Check($"<draw:g><draw:rect {Box}/><draw:control draw:control=\"c1\" {Box}/></draw:g>"));
        Assert.Null(Check($"<draw:frame draw:display=\"screen\" {Box}><draw:text-box><text:p>Note</text:p></draw:text-box></draw:frame>"));
    }

    // ───────────────────────── How far drawings reach ─────────────────────────

    private const string Xdr = "xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";

    private static string Picture() =>
        "<xdr:pic><xdr:nvPicPr><xdr:cNvPr id=\"2\" name=\"Picture 1\"/><xdr:cNvPicPr/></xdr:nvPicPr>" +
        "<xdr:blipFill><a:blip r:embed=\"rIdP\"/><a:stretch><a:fillRect/></a:stretch></xdr:blipFill><xdr:spPr><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></xdr:spPr></xdr:pic>";

    /// <summary>A sheet with "End" in A1 only and one drawing anchor holding a red picture.</summary>
    private string ReachBook(string anchor)
    {
        var book = new XlsxBuilder();
        book.Sheet("Data", XlsxBuilder.Row(1, book.Text("A1", "End")), after: "<pageSetup paperSize=\"9\" orientation=\"portrait\"/><drawing r:id=\"rIdD\"/>");
        book.SheetRelationships[1] = [("rIdD", "drawing", "../drawings/drawing1.xml")];
        book.Parts["xl/drawings/drawing1.xml"] = $"<xdr:wsDr {Xdr}>{anchor}</xdr:wsDr>";
        book.Parts["xl/drawings/_rels/drawing1.xml.rels"] = OpcWriter.Relationships([("rIdP", "image", "../media/image1.png")]);
        book.Media["xl/media/image1.png"] = Png(SKColors.Red);
        return book.Save(Path.Combine(fx.NewFolder(), "book.xlsx"));
    }

    [Fact]
    public void A_picture_placed_by_its_size_prints_whole()
    {
        // A one-cell anchor in B2 of 200 × 100 pt and an absolute anchor at (300, 400) pt of 100 × 100 pt: before, the
        // printed range stopped at the cell each starts in (A1:B2), cutting the first off and leaving the second out.
        var oneCell = $"<xdr:oneCellAnchor><xdr:from><xdr:col>1</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>1</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from>" +
                      $"<xdr:ext cx=\"{200 * Emu}\" cy=\"{100 * Emu}\"/>{Picture()}<xdr:clientData/></xdr:oneCellAnchor>";
        var absolute = $"<xdr:absoluteAnchor><xdr:pos x=\"{300 * Emu}\" y=\"{400 * Emu}\"/><xdr:ext cx=\"{100 * Emu}\" cy=\"{100 * Emu}\"/>{Picture()}<xdr:clientData/></xdr:absoluteAnchor>";
        foreach (var (anchor, area) in new[] { (oneCell, 200 * 100), (absolute, 100 * 100) })
        {
            var book = XlsxReader.Read(ReachBook(anchor));
            using var printed = Print(book);
            Assert.Single(printed.Pages);
            using var page = printed.Pages[0].Render(72);
            Assert.InRange(Count(page, SKColors.Red), area * 0.9, area * 1.1);
        }

        // The drawing covers the cells from the one its corner is in to the one its far corner is in; the printed range
        // still starts at the anchor's cell (A1 for an absolute anchor).
        var sheet = XlsxReader.Read(ReachBook(absolute)).Sheets[0];
        using var fonts = new SheetFonts();
        var workbook = new Workbook();
        var reach = new SheetPagination.DrawingReach(workbook, sheet, new SheetIndex(sheet), fonts, fonts.Pixels(workbook.DefaultFontName, workbook.DefaultFontSize), TestContext.Current.CancellationToken);
        var range = reach.Cells(sheet.Drawings[0])!.Value;
        var widths = SheetPagination.Widths(sheet, 100, fonts.Pixels(workbook.DefaultFontName, workbook.DefaultFontSize));
        int Column(double x)
        {
            var at = 0;
            for (var sum = widths[0]; sum <= x; sum += widths[++at]) { }
            return at;
        }
        Assert.Equal((Column(300), Column(399)), (range.Left, range.Right));
        Assert.True(range.Top > 10 && range.Bottom > range.Top);
        var cut = Assert.Single(SheetPagination.Areas(sheet, reach));
        Assert.Equal((0, 0, range.Bottom, range.Right), (cut.Top, cut.Left, cut.Bottom, cut.Right));
    }

    [Fact]
    public void A_drawing_reaching_far_down_measures_rows_quickly()
    {
        // 4 km tall: the reach stops at the sheet's last row without measuring row by row for each drawing.
        var anchor = $"<xdr:oneCellAnchor><xdr:from><xdr:col>0</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>0</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from>" +
                     $"<xdr:ext cx=\"{10 * Emu}\" cy=\"50000000000\"/>{Picture()}<xdr:clientData/></xdr:oneCellAnchor>";
        var sheet = XlsxReader.Read(ReachBook(string.Concat(Enumerable.Repeat(anchor, 500)))).Sheets[0];
        using var fonts = new SheetFonts();
        var workbook = new Workbook();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var reach = new SheetPagination.DrawingReach(workbook, sheet, new SheetIndex(sheet), fonts, fonts.Pixels(workbook.DefaultFontName, workbook.DefaultFontSize), TestContext.Current.CancellationToken);
        foreach (var drawing in sheet.Drawings)
            Assert.InRange(reach.Cells(drawing)!.Value.Bottom, 200_000, SheetPagination.MaxRow);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    // ───────────────────────── Chart sheets ─────────────────────────

    private string Save(XlsxBuilder book, string name = "book.xlsx") => book.Save(Path.Combine(fx.NewFolder(), name));

    private static string Box(double x, double y, double width, double height) =>
        $"<xdr:absoluteAnchor><xdr:pos x=\"{(long)(x * Emu)}\" y=\"{(long)(y * Emu)}\"/><xdr:ext cx=\"{(long)(width * Emu)}\" cy=\"{(long)(height * Emu)}\"/>" +
        "<xdr:sp><xdr:nvSpPr><xdr:cNvPr id=\"3\" name=\"Box\"/><xdr:cNvSpPr/></xdr:nvSpPr><xdr:spPr><a:prstGeom prst=\"rect\"/>" +
        "<a:solidFill><a:srgbClr val=\"FF00FF\"/></a:solidFill><a:ln><a:noFill/></a:ln></xdr:spPr></xdr:sp><xdr:clientData/></xdr:absoluteAnchor>";

    [Fact]
    public void Shapes_over_a_chart_sheet_print_over_its_chart()
    {
        // The chart's anchor (733 × 478 pt) fills the printable area of landscape A4 (741 × 487 pt): what is placed
        // over it scales with it, from the chart's corner.
        var path = Save(ChartTests.ChartSheetBook(Columns(), over: Box(100, 100, 50, 50)));
        Assert.Null(SheetCapabilities.FirstUnsupported(path, "xlsx"));
        var book = XlsxReader.Read(path);
        Assert.Single(book.ChartSheets[0].Sheet.Drawings);
        using var printed = Print(book);
        using var page = printed.Pages[0].Render(72);
        var (sx, sy) = ((841.89 - 100.8) / (9309100 / 12700.0), (595.28 - 108) / (6073775 / 12700.0));
        var box = SKRectI.Create((int)(50.4 + 100 * sx), (int)(54 + 100 * sy), (int)(50 * sx), (int)(50 * sy));
        Assert.True(Count(page, Magenta, box) > 0.9 * box.Width * box.Height);
        Assert.InRange(Count(page, Magenta), 0, box.Width * box.Height * 1.1);
    }

    [Fact]
    public void A_chart_sheet_not_sized_to_the_window_keeps_its_size()
    {
        // 300 × 200 pt at (100, 50) pt: smaller than the page, so it prints there at that size.
        var book = ChartTests.ChartSheetBook(Columns());
        book.Parts["xl/chartsheets/sheet1.xml"] = book.Parts["xl/chartsheets/sheet1.xml"].Replace("zoomToFit=\"1\"", "zoomToFit=\"0\"", StringComparison.Ordinal);
        book.Parts["xl/drawings/drawing1.xml"] = book.Parts["xl/drawings/drawing1.xml"]
            .Replace("<xdr:pos x=\"0\" y=\"0\"/><xdr:ext cx=\"9309100\" cy=\"6073775\"/>", $"<xdr:pos x=\"{100 * Emu}\" y=\"{50 * Emu}\"/><xdr:ext cx=\"{300 * Emu}\" cy=\"{200 * Emu}\"/>", StringComparison.Ordinal);
        var path = Save(book);
        using var printed = Print(XlsxReader.Read(path));
        using var page = printed.Pages[0].Render(72);
        var inside = SKRectI.Create(50 + 100, 54 + 50, 302, 202);
        Assert.True(Count(page, Red, inside) > 500 && Count(page, Blue, inside) > 300);
        Assert.Equal(Count(page, Red), Count(page, Red, inside));
        Assert.Equal(Count(page, Blue), Count(page, Blue, inside));

        // Sized to the window, the same anchor fills the page.
        using var filled = Print(XlsxReader.Read(Save(ChartTests.ChartSheetBook(Columns()))));
        using var full = filled.Pages[0].Render(72);
        Assert.True(Count(full, Red) > 2 * Count(page, Red));
    }

    [Fact]
    public void A_charts_user_shapes_print_over_it()
    {
        // c:userShapes: a box from 10 % to 30 % of the chart's width and height, over the chart.
        var chart = Columns().Replace("</c:chartSpace>", "<c:userShapes r:id=\"rIdU\"/></c:chartSpace>", StringComparison.Ordinal);
        var book = ChartTests.ChartSheetBook(chart);
        book.Parts["xl/charts/_rels/chart1.xml.rels"] = OpcWriter.Relationships([("rIdU", "chartUserShapes", "../drawings/drawing2.xml")]);
        book.Parts["xl/drawings/drawing2.xml"] =
            "<c:userShapes xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" xmlns:cdr=\"http://schemas.openxmlformats.org/drawingml/2006/chartDrawing\" " +
            "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><cdr:relSizeAnchor><cdr:from><cdr:x>0.1</cdr:x><cdr:y>0.1</cdr:y></cdr:from>" +
            "<cdr:to><cdr:x>0.3</cdr:x><cdr:y>0.3</cdr:y></cdr:to><cdr:sp><cdr:nvSpPr><cdr:cNvPr id=\"2\" name=\"Box\"/><cdr:cNvSpPr/></cdr:nvSpPr>" +
            "<cdr:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"1000\" cy=\"1000\"/></a:xfrm><a:prstGeom prst=\"rect\"/><a:solidFill><a:srgbClr val=\"FF00FF\"/></a:solidFill>" +
            "<a:ln><a:noFill/></a:ln></cdr:spPr></cdr:sp></cdr:relSizeAnchor></c:userShapes>";
        var path = Save(book);
        Assert.Null(SheetCapabilities.FirstUnsupported(path, "xlsx"));
        using var printed = Print(XlsxReader.Read(path));
        using var page = printed.Pages[0].Render(72);
        var (width, height) = (841.89 - 100.8, 595.28 - 108);
        var box = SKRectI.Create((int)(50.4 + 0.1 * width) + 1, (int)(54 + 0.1 * height) + 1, (int)(0.2 * width) - 2, (int)(0.2 * height) - 2);
        Assert.True(Count(page, Magenta, box) > 0.95 * box.Width * box.Height);

        // A user shape with text isn't drawn whole: it is named.
        book.Parts["xl/drawings/drawing2.xml"] = book.Parts["xl/drawings/drawing2.xml"]
            .Replace("</cdr:spPr>", "</cdr:spPr><cdr:txBody><a:bodyPr/><a:p><a:r><a:t>Peak</a:t></a:r></a:p></cdr:txBody>", StringComparison.Ordinal);
        Assert.Equal("text in shapes", SheetCapabilities.FirstUnsupported(Save(book), "xlsx"));
    }

    [Fact]
    public void An_xlsb_chart_sheet_prints_like_the_xlsx_one()
    {
        var builder = new XlsbBuilder();
        builder.Sheet("Data").Text(0, 0, "Sales");
        var chart = builder.Sheet("Chart1");
        chart.IsChart = true;
        chart.PageSetup = (9, true, 100, false, 1, 1, false, null);
        chart.Margins = [0.7, 0.7, 0.75, 0.75, 0.3, 0.3];
        chart.DrawingId = "rIdD";
        chart.Relationships.Add(("rIdD", "drawing", "../drawings/drawing1.xml"));
        builder.Parts["xl/drawings/drawing1.xml"] =
            $"<xdr:wsDr {Xdr}><xdr:absoluteAnchor><xdr:pos x=\"0\" y=\"0\"/><xdr:ext cx=\"9309100\" cy=\"6073775\"/>" +
            "<xdr:graphicFrame macro=\"\"><xdr:nvGraphicFramePr><xdr:cNvPr id=\"2\" name=\"Chart 1\"/><xdr:cNvGraphicFramePr/></xdr:nvGraphicFramePr>" +
            $"<xdr:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/></xdr:xfrm><a:graphic><a:graphicData uri=\"{GraphicUri}\">" +
            $"<c:chart xmlns:c=\"{GraphicUri}\" r:id=\"rIdC\"/></a:graphicData></a:graphic></xdr:graphicFrame><xdr:clientData/></xdr:absoluteAnchor>{Box(100, 100, 50, 50)}</xdr:wsDr>";
        builder.Parts["xl/drawings/_rels/drawing1.xml.rels"] = OpcWriter.Relationships([("rIdC", "chart", "../charts/chart1.xml")]);
        builder.Parts["xl/charts/chart1.xml"] = Columns();
        var folder = fx.NewFolder();
        var xlsb = builder.SaveXlsb(Path.Combine(folder, "book.xlsb"));
        var xlsx = builder.SaveXlsx(Path.Combine(folder, "book.xlsx"));
        Assert.Null(SheetCapabilities.FirstUnsupported(xlsb, "xlsb"));

        var binary = XlsbReader.Read(xlsb);
        Assert.Equal(["Data", "Chart1"], binary.Printed().Select(s => s.Name));
        var sheet = binary.ChartSheets[0].Sheet;
        Assert.True(sheet.PageSetup.Landscape);
        Assert.Single(sheet.Drawings);
        using var a = Print(binary);
        using var b = Print(XlsxReader.Read(xlsx));
        Assert.Equal(2, a.Pages.Count);
        using var first = a.Pages[1].Render(72);
        using var twin = b.Pages[1].Render(72);
        Assert.True(Count(first, Red) > 2000 && Count(first, Magenta) > 2000, $"red {Count(first, Red)} magenta {Count(first, Magenta)} twin {Count(twin, Red)} {Count(twin, Magenta)}");
        Assert.Equal(first.Bytes, twin.Bytes);
    }
}
