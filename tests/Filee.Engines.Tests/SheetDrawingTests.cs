// Pictures and shapes on printed sheets (XlsxDrawing, SheetPageRenderer): turned, mirrored and cropped pictures (#1315:
// a portrait photo turned 90° in Excel), preset shapes, groups; and what the printer names instead of drawing
// (SheetCapabilities), which hands the workbook to LibreOffice when it is installed. Workbooks are built here in code.
// Drawings are placed with absolute anchors, so their place on the page is the margins (0.7 in, 0.75 in) plus the
// anchor's position, whatever the column widths.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Documents;
using Filee.Engines.Office;
using Filee.Engines.Office.Sheets;
using SkiaSharp;
using static Filee.Engines.Tests.XlsxBuilder;

namespace Filee.Engines.Tests;

public sealed class SheetDrawingTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string Xdr = "xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";

    private const long Emu = 12700;

    /// <summary>The page's top-left printable corner in points: Excel's Normal margins.</summary>
    private const float Left = 0.7f * 72, Top = 0.75f * 72;

    /// <summary>An absolute anchor at (20, 20) pt of 80 × 40 pt around <paramref name="content"/>.</summary>
    private static string Anchor(string content, double x = 20, double y = 20, double width = 80, double height = 40, string clientData = "<xdr:clientData/>") =>
        $"<xdr:absoluteAnchor><xdr:pos x=\"{(long)(x * Emu)}\" y=\"{(long)(y * Emu)}\"/><xdr:ext cx=\"{(long)(width * Emu)}\" cy=\"{(long)(height * Emu)}\"/>" +
        content + clientData + "</xdr:absoluteAnchor>";

    private static string Picture(string transform = "", string crop = "") =>
        "<xdr:pic><xdr:nvPicPr><xdr:cNvPr id=\"2\" name=\"Picture 1\"/><xdr:cNvPicPr/></xdr:nvPicPr>" +
        $"<xdr:blipFill><a:blip r:embed=\"rIdP\"/>{crop}<a:stretch><a:fillRect/></a:stretch></xdr:blipFill><xdr:spPr>{transform}<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></xdr:spPr></xdr:pic>";

    private static string Xfrm(string attributes, double width, double height) =>
        $"<a:xfrm{attributes}><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{(long)(width * Emu)}\" cy=\"{(long)(height * Emu)}\"/></a:xfrm>";

    private static string Shape(string geometry, string fill, string line = "<a:ln><a:noFill/></a:ln>", string text = "", string style = "") =>
        "<xdr:sp><xdr:nvSpPr><xdr:cNvPr id=\"3\" name=\"Shape 1\"/><xdr:cNvSpPr/></xdr:nvSpPr>" +
        $"<xdr:spPr><a:prstGeom prst=\"{geometry}\"><a:avLst/></a:prstGeom>{fill}{line}</xdr:spPr>{style}" +
        (text.Length > 0 ? $"<xdr:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>{text}</a:t></a:r></a:p></xdr:txBody>" : "") + "</xdr:sp>";

    private static string Solid(string rgb) => $"<a:solidFill><a:srgbClr val=\"{rgb}\"/></a:solidFill>";

    /// <summary>A one-sheet workbook with "End" in F20 (so the printed area takes the drawings in) and a drawing part.</summary>
    private static XlsxBuilder Book(string anchors, byte[]? picture = null, bool hidden = false)
    {
        var book = new XlsxBuilder();
        book.Sheet("Data", Row(20, book.Text("F20", "End")), after: "<pageSetup paperSize=\"9\" orientation=\"portrait\"/><drawing r:id=\"rIdD\"/>", hidden: hidden);
        book.SheetRelationships[1] = [("rIdD", "drawing", "../drawings/drawing1.xml")];
        book.Parts["xl/drawings/drawing1.xml"] = $"<xdr:wsDr {Xdr}>{anchors}</xdr:wsDr>";
        if (picture is not null)
        {
            book.Parts["xl/drawings/_rels/drawing1.xml.rels"] = OpcWriter.Relationships([("rIdP", "image", "../media/image1.png")]);
            book.Media["xl/media/image1.png"] = picture;
        }
        return book;
    }

    private string Save(XlsxBuilder book, string name = "book.xlsx") => book.Save(Path.Combine(fx.NewFolder(), name));

    /// <summary>A picture in two colours: the first half (top, or left when <paramref name="sideBySide"/>) red, the other blue.</summary>
    private static byte[] TwoColours(int width, int height, bool sideBySide)
    {
        using var bitmap = new SKBitmap(width, height);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                bitmap.SetPixel(x, y, (sideBySide ? x < width / 2 : y < height / 2) ? SKColors.Red : SKColors.Blue);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>The printed first page at 72 dpi (a pixel per point).</summary>
    private static SKBitmap Page(string path)
    {
        var book = XlsxReader.Read(path);
        using var printed = SheetPrinter.Print(book, new SheetPrintSource("book.xlsx", "C:\\"), DocumentLimits.Default, DateTime.Now, null, default);
        return printed.Pages[0].Render(72);
    }

    private static SKColor At(SKBitmap page, float x, float y) => page.GetPixel((int)(Left + x), (int)(Top + y));

    private static void Near(SKColor expected, SKColor actual)
    {
        Assert.True(Math.Abs(expected.Red - actual.Red) <= 24 && Math.Abs(expected.Green - actual.Green) <= 24 && Math.Abs(expected.Blue - actual.Blue) <= 24,
            $"expected {expected}, got {actual}");
    }

    // ───────────────────────── Pictures ─────────────────────────

    [Theory]
    // #1315: a portrait photo turned 90° clockwise covers a landscape box; its top (red) is on the right.
    [InlineData(5_400_000, "blue", "red")]
    [InlineData(16_200_000, "red", "blue")]
    public void A_turned_picture_fills_its_anchor_turned(int rotation, string left, string right)
    {
        // The anchor is the landscape box the turned picture covers; a:xfrm keeps the portrait size.
        var anchors = Anchor(Picture(Xfrm($" rot=\"{rotation}\"", 40, 80)));
        using var page = Page(Save(Book(anchors, TwoColours(20, 40, sideBySide: false))));

        Near(left == "red" ? SKColors.Red : SKColors.Blue, At(page, 30, 40));
        Near(right == "red" ? SKColors.Red : SKColors.Blue, At(page, 90, 40));
        // Drawn upright, the portrait picture would reach above and below the box.
        Near(SKColors.White, At(page, 60, 5));
        Near(SKColors.White, At(page, 60, 75));
    }

    [Fact]
    public void A_mirrored_picture_is_drawn_mirrored()
    {
        var anchors = Anchor(Picture(Xfrm(" flipH=\"1\"", 80, 40)));
        using var page = Page(Save(Book(anchors, TwoColours(40, 20, sideBySide: true))));

        Near(SKColors.Blue, At(page, 30, 40));
        Near(SKColors.Red, At(page, 90, 40));
    }

    [Fact]
    public void A_cropped_picture_shows_only_what_is_left()
    {
        // The left half (red) is cut off: the blue half fills the whole box.
        var anchors = Anchor(Picture(Xfrm("", 80, 40), "<a:srcRect l=\"50000\"/>"));
        using var page = Page(Save(Book(anchors, TwoColours(40, 20, sideBySide: true))));

        Near(SKColors.Blue, At(page, 25, 40));
        Near(SKColors.Blue, At(page, 95, 40));
    }

    [Theory]
    [InlineData(90, 80, 40, 40, 80, true)]   // the anchor is the covered box: swapped
    [InlineData(90, 40, 80, 40, 80, false)]  // the anchor is the unturned box already
    [InlineData(270, 80, 40, 0, 0, true)]    // no unturned size: by the turn
    [InlineData(30, 80, 40, 40, 80, false)]  // less than 45°: never
    [InlineData(180, 80, 40, 40, 80, false)]
    public void A_quarter_turned_anchor_is_told_apart(double rotation, float width, float height, double unturnedWidth, double unturnedHeight, bool swapped)
    {
        var picture = new SheetPicture([], 0, 0, 0, 0, width, height)
        {
            Rotation = rotation,
            Size = unturnedWidth > 0 ? (unturnedWidth, unturnedHeight) : null,
        };
        Assert.Equal(swapped, SheetPageRenderer.AnchoredTurned(picture, width, height));
    }

    [Fact]
    public void The_reader_keeps_turn_mirroring_crop_and_unturned_size()
    {
        var anchors = Anchor(Picture(Xfrm(" rot=\"5400000\" flipV=\"1\"", 40, 80), "<a:srcRect l=\"10000\" t=\"20000\" r=\"-5000\" b=\"0\"/>"));
        var sheet = Assert.Single(XlsxReader.Read(Save(Book(anchors, TwoColours(4, 4, false)))).Sheets);
        var picture = Assert.IsType<SheetPicture>(Assert.Single(sheet.Drawings));
        Assert.Equal(90, picture.Rotation);
        Assert.False(picture.FlipH);
        Assert.True(picture.FlipV);
        Assert.Equal((0.1, 0.2, -0.05, 0.0), picture.Crop);
        Assert.Equal((40 * Emu, 80 * Emu), picture.Size!.Value);
    }

    // ───────────────────────── Shapes ─────────────────────────

    [Fact]
    public void Preset_shapes_are_filled_and_outlined()
    {
        var anchors =
            Anchor(Shape("rect", Solid("00B050"))) +
            Anchor(Shape("ellipse", Solid("FF0000")), y: 80) +
            // No fill of its own: the theme style's (accent1 of Office's default theme).
            Anchor(Shape("rect", "", line: "", style: "<xdr:style><a:lnRef idx=\"0\"><a:srgbClr val=\"000000\"/></a:lnRef>" +
                "<a:fillRef idx=\"1\"><a:schemeClr val=\"accent1\"/></a:fillRef><a:effectRef idx=\"0\"><a:srgbClr val=\"000000\"/></a:effectRef>" +
                "<a:fontRef idx=\"minor\"><a:srgbClr val=\"FFFFFF\"/></a:fontRef></xdr:style>"), y: 140) +
            // A horizontal line has no height.
            Anchor("<xdr:cxnSp><xdr:nvCxnSpPr><xdr:cNvPr id=\"5\" name=\"Line\"/><xdr:cNvCxnSpPr/></xdr:nvCxnSpPr><xdr:spPr><a:prstGeom prst=\"line\"><a:avLst/></a:prstGeom>" +
                "<a:ln w=\"38100\"><a:solidFill><a:srgbClr val=\"000000\"/></a:solidFill></a:ln></xdr:spPr></xdr:cxnSp>", y: 200, height: 0);
        using var page = Page(Save(Book(anchors)));

        Near(new SKColor(0x00, 0xB0, 0x50), At(page, 60, 40));
        Near(SKColors.Red, At(page, 60, 100));
        Near(SKColors.White, At(page, 23, 82)); // the oval's corner
        Near(new SKColor(0x44, 0x72, 0xC4), At(page, 60, 160));
        Near(SKColors.Black, At(page, 60, 200));
        Near(SKColors.White, At(page, 60, 205));
    }

    [Fact]
    public void A_group_places_each_child_in_its_share_of_the_anchor()
    {
        // Child space 0..1000 × 0..1000 over the 80 × 40 box: the red rectangle takes the right half.
        var group = "<xdr:grpSp><xdr:nvGrpSpPr><xdr:cNvPr id=\"4\" name=\"Group\"/><xdr:cNvGrpSpPr/></xdr:nvGrpSpPr>" +
                    "<xdr:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"1016000\" cy=\"508000\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"1000\" cy=\"1000\"/></a:xfrm></xdr:grpSpPr>" +
                    "<xdr:sp><xdr:nvSpPr><xdr:cNvPr id=\"5\" name=\"Half\"/><xdr:cNvSpPr/></xdr:nvSpPr><xdr:spPr><a:xfrm><a:off x=\"500\" y=\"0\"/><a:ext cx=\"500\" cy=\"1000\"/></a:xfrm>" +
                    "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>" + Solid("FF0000") + "<a:ln><a:noFill/></a:ln></xdr:spPr></xdr:sp></xdr:grpSp>";
        using var page = Page(Save(Book(Anchor(group))));

        Near(SKColors.White, At(page, 30, 40));
        Near(SKColors.Red, At(page, 90, 40));
    }

    [Fact]
    public void A_drawing_built_to_be_slow_is_cut_short()
    {
        // More shapes than one sheet's drawing draws, and groups nested deeper than they are followed.
        var many = string.Concat(Enumerable.Repeat(Anchor(Shape("rect", Solid("FF0000")), width: 1, height: 1), XlsxDrawing.MaxDrawings + 500));
        const string Group = "<xdr:grpSp><xdr:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"100\" cy=\"100\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"100\" cy=\"100\"/></a:xfrm></xdr:grpSpPr>";
        var nested = string.Concat(Enumerable.Repeat(Group, 60)) + Shape("rect", Solid("FF0000")) + string.Concat(Enumerable.Repeat("</xdr:grpSp>", 60));
        var path = Save(Book(many + Anchor(nested)));

        var sheet = Assert.Single(XlsxReader.Read(path).Sheets);
        Assert.Equal(XlsxDrawing.MaxDrawings, sheet.Drawings.Count);
        Assert.Equal("grouped shapes", SheetCapabilities.FirstUnsupported(path, "xlsx"));
    }

    // ───────────────────────── What isn't drawn ─────────────────────────

    private const string Chart =
        "<xdr:graphicFrame macro=\"\"><xdr:nvGraphicFramePr><xdr:cNvPr id=\"2\" name=\"Chart 1\"/><xdr:cNvGraphicFramePr/></xdr:nvGraphicFramePr>" +
        "<xdr:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/></xdr:xfrm><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\">" +
        "<c:chart xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" r:id=\"rIdC\"/></a:graphicData></a:graphic></xdr:graphicFrame>";

    [Fact]
    public void Charts_and_text_in_shapes_are_named()
    {
        Assert.Equal("charts", SheetCapabilities.FirstUnsupported(Save(Book(Anchor(Chart))), "xlsx"));
        Assert.Equal("text in shapes", SheetCapabilities.FirstUnsupported(Save(Book(Anchor(Shape("rect", Solid("FFFFFF"), text: "Note")))), "xlsx"));
        Assert.Equal("arrowheads", SheetCapabilities.FirstUnsupported(Save(Book(Anchor(Shape("rect", "",
            line: "<a:ln><a:solidFill><a:srgbClr val=\"000000\"/></a:solidFill><a:tailEnd type=\"triangle\"/></a:ln>")))), "xlsx"));
        // An .xls name on an XLSX package, as web apps save them.
        Assert.Equal("charts", SheetCapabilities.FirstUnsupported(Save(Book(Anchor(Chart)), "web.xls"), "xls"));
    }

    [Fact]
    public void What_the_printer_draws_or_doesnt_print_is_not_named()
    {
        Assert.Null(SheetCapabilities.FirstUnsupported(Save(Book(Anchor(Picture(Xfrm(" rot=\"5400000\"", 40, 80))), TwoColours(4, 4, false))), "xlsx"));
        Assert.Null(SheetCapabilities.FirstUnsupported(Save(Book(Anchor(Shape("roundRect", Solid("FF0000"))))), "xlsx"));
        // On a hidden sheet, or set not to print.
        Assert.Null(SheetCapabilities.FirstUnsupported(Save(Book(Anchor(Chart), hidden: true)), "xlsx"));
        Assert.Null(SheetCapabilities.FirstUnsupported(Save(Book(Anchor(Chart, clientData: "<xdr:clientData fPrintsWithSheet=\"0\"/>"))), "xlsx"));
        // A damaged file gets no answer here: the reader refuses it with its own reason.
        var broken = Path.Combine(fx.NewFolder(), "broken.xlsx");
        File.WriteAllBytes(broken, [0x50, 0x4B, 0x03, 0x04, 1, 2, 3]);
        Assert.Null(SheetCapabilities.FirstUnsupported(broken, "xlsx"));
    }

    [Fact]
    public void Chart_sheets_and_form_controls_are_named()
    {
        var charts = Book("");
        charts.Sheet("Chart1", "");
        charts.Parts["xl/_rels/workbook.xml.rels"] = OpcWriter.Relationships([
            ("rId1", "worksheet", "worksheets/sheet1.xml"), ("rId2", "chartsheet", "chartsheets/sheet1.xml"),
            ("rIdS", "sharedStrings", "sharedStrings.xml"), ("rIdT", "styles", "styles.xml")]);
        Assert.Equal("charts", SheetCapabilities.FirstUnsupported(Save(charts), "xlsx"));

        const string Vml = "<xml xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:x=\"urn:schemas-microsoft-com:office:excel\"><v:shape id=\"_x0000_s1025\">" +
                           "<x:ClientData ObjectType=\"{0}\"><x:Anchor>1, 15, 0, 2, 3, 15, 3, 16</x:Anchor></x:ClientData></v:shape></xml>";
        foreach (var (type, expected) in new[] { ("Checkbox", "form controls"), ("Note", null) })
        {
            var book = Book("");
            book.SheetRelationships[1] = [("rIdD", "drawing", "../drawings/drawing1.xml"), ("rIdV", "vmlDrawing", "../drawings/vmlDrawing1.vml")];
            book.Parts["xl/drawings/vmlDrawing1.vml"] = string.Format(System.Globalization.CultureInfo.InvariantCulture, Vml, type);
            Assert.Equal(expected, SheetCapabilities.FirstUnsupported(Save(book), "xlsx"));
        }
    }

    [Fact]
    public void Objects_of_an_xls_are_named_by_their_type()
    {
        string? Check(int type, bool hidden = false)
        {
            var book = new XlsBuilder();
            var sheet = book.Sheet("Data", hidden);
            if (hidden)
                book.Sheet("Shown").Text(0, 0, "x");
            sheet.Text(0, 0, "x");
            // OBJ: ftCmo (type, id, flags) and ftEnd.
            sheet.Record(0x005D, [0x15, 0x00, 0x12, 0x00, (byte)type, 0x00, 0x01, 0x00, 0x11, 0x60, .. new byte[12], 0x00, 0x00, 0x00, 0x00]);
            return SheetCapabilities.FirstUnsupported(book.Save(Path.Combine(fx.NewFolder(), "book.xls")), "xls");
        }

        Assert.Equal("charts", Check(0x05));
        Assert.Equal("pictures", Check(0x08));
        Assert.Equal("form controls", Check(0x0B));
        Assert.Equal("shapes", Check(0x02));
        Assert.Null(Check(0x19)); // a comment
        Assert.Null(Check(0x05, hidden: true));
    }

    [Fact]
    public void Drawings_of_an_ods_are_named()
    {
        const string DrawNamespaces = "xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" " +
                                      "xmlns:svg=\"urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"";
        const string Hidden = "<style:style style:name=\"ta2\" style:family=\"table\"><style:table-properties table:display=\"false\"/></style:style>";
        string Table(string name, string drawing, string style = "ta1") =>
            $"<table:table table:name=\"{name}\" table:style-name=\"{style}\"><table:table-row><table:table-cell {DrawNamespaces}>{drawing}<text:p>x</text:p></table:table-cell></table:table-row></table:table>";
        const string Image = "<draw:frame svg:width=\"2cm\" svg:height=\"1cm\"><draw:image xlink:href=\"Pictures/1.png\"/></draw:frame>";
        string Ods(string tables) => OdsBuilder.Save(Path.Combine(fx.NewFolder(), "book.ods"), Hidden, tables);

        Assert.Equal("pictures", SheetCapabilities.FirstUnsupported(Ods(Table("A", Image)), "ods"));
        Assert.Equal("shapes", SheetCapabilities.FirstUnsupported(Ods(Table("A", "<draw:rect svg:width=\"2cm\" svg:height=\"1cm\"/>")), "ods"));
        Assert.Null(SheetCapabilities.FirstUnsupported(Ods(Table("A", "")), "ods"));
        // On a hidden table, before a shown one.
        Assert.Null(SheetCapabilities.FirstUnsupported(Ods(Table("A", Image, "ta2") + Table("B", "")), "ods"));
        Assert.Equal("pictures", SheetCapabilities.FirstUnsupported(Ods(Table("A", "", "ta2") + Table("B", Image)), "ods"));

        // A chart is an embedded object the manifest calls a chart.
        var chart = Ods(Table("A", "<draw:frame svg:width=\"2cm\" svg:height=\"1cm\"><draw:object xlink:href=\"./Object 1\"/><draw:image xlink:href=\"./ObjectReplacements/Object 1\"/></draw:frame>"));
        Assert.Equal("embedded objects", SheetCapabilities.FirstUnsupported(chart, "ods"));
        using (var zip = System.IO.Compression.ZipFile.Open(chart, System.IO.Compression.ZipArchiveMode.Update))
        {
            zip.GetEntry("META-INF/manifest.xml")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("META-INF/manifest.xml").Open());
            writer.Write("<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.3\">" +
                         "<manifest:file-entry manifest:full-path=\"/\" manifest:media-type=\"application/vnd.oasis.opendocument.spreadsheet\"/>" +
                         "<manifest:file-entry manifest:full-path=\"Object 1/\" manifest:media-type=\"application/vnd.oasis.opendocument.chart\"/></manifest:manifest>");
        }
        Assert.Equal("charts", SheetCapabilities.FirstUnsupported(chart, "ods"));
    }

    // ───────────────────────── Handing over to LibreOffice ─────────────────────────

    private static async Task<(IReadOnlyList<string> Outputs, List<string> Notices, bool HandedOver)> ConvertAsync(string input, bool installed)
    {
        var handedOver = false;
        var printer = new SheetPrintConverter
        {
            Fallback = (_, _, _) =>
            {
                handedOver = true;
                return installed ? Task.FromResult<IReadOnlyList<string>>(["by LibreOffice"]) : null;
            },
        };
        var notices = new List<string>();
        var output = Path.ChangeExtension(input, ".pdf");
        var outputs = await printer.ConvertAsync(new ConversionStep(input, "xlsx", "pdf", new Preset(), new HwpxAssert.FixedPath(output), Path.GetDirectoryName(input)!,
            Notice: notices.Add), null, TestContext.Current.CancellationToken);
        return (outputs, notices, handedOver);
    }

    [Fact]
    public async Task A_workbook_with_a_chart_goes_to_libreoffice_when_it_is_installed()
    {
        var (outputs, notices, handedOver) = await ConvertAsync(Save(Book(Anchor(Chart))), installed: true);
        Assert.True(handedOver);
        Assert.Equal(["by LibreOffice"], outputs);
        Assert.Empty(notices);
    }

    [Fact]
    public async Task Without_libreoffice_it_is_printed_with_a_notice()
    {
        var (outputs, notices, _) = await ConvertAsync(Save(Book(Anchor(Chart))), installed: false);
        Assert.True(File.Exists(Assert.Single(outputs)));
        Assert.Equal([SheetPrintConverter.LeftOutNotice], notices);
    }

    [Fact]
    public async Task A_workbook_the_printer_draws_whole_stays_here()
    {
        var (outputs, notices, handedOver) = await ConvertAsync(Save(Book(Anchor(Shape("rect", Solid("FF0000"))))), installed: true);
        Assert.False(handedOver);
        Assert.True(File.Exists(Assert.Single(outputs)));
        Assert.Empty(notices);
    }

    [Fact]
    public void The_registry_gives_the_printer_libreoffice()
    {
        Assert.NotNull(fx.Converters.OfType<SheetPrintConverter>().Single().Fallback);
    }
}
