// DrawingML charts (Charts/): the chart part read into the model (ChartReader), drawn as vectors with text as glyph
// runs (ChartDrawer), and drawn where they appear: on printed sheets (XlsxDrawing, SheetCapabilities), in Word
// documents (DocxReader, FloatingObjects, the renderer's check) and on slides (PptxSlideReader, SlideRenderer,
// SlideCapabilities). Charts are built in code (ChartBuilder); pictures are checked by how much of each series' colour
// they hold, not by where the fonts put the text, so Linux fonts don't move the goal posts.

using System.Diagnostics;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Engines.Charts;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Office;
using Filee.Engines.Office.Sheets;
using Filee.Engines.Rendering;
using Filee.Engines.Slides;
using Filee.Engines.Typesetting;
using SkiaSharp;
using static Filee.Engines.Tests.ChartBuilder;

namespace Filee.Engines.Tests;

public sealed class ChartTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly SKColor Red = new(0xC0, 0x00, 0x00), Green = new(0x00, 0xB0, 0x50), Blue = new(0x00, 0x70, 0xC0);

    private static ChartRead Read(string xml) => ChartReader.Read(XDocument.Parse(xml).Root, DrawingColors.FromTheme(null));

    private static Chart Drawn(string xml) => Read(xml).Chart ?? throw new InvalidOperationException("The chart isn't drawn.");

    /// <summary>The chart drawn alone on a page of its size, and the page.</summary>
    private static (SKBitmap Bitmap, PageContent Page) Picture(string xml, float width = 480, float height = 300)
    {
        var page = new PageContent(width, height);
        using (var fonts = new ChartFonts())
            ChartDrawer.Draw(page, Drawn(xml), new SKRect(0, 0, width, height), fonts);
        return (page.Render(72), page);
    }

    /// <summary>Pixels within a few steps of <paramref name="color"/>, in columns <paramref name="from"/> to <paramref name="to"/> (fractions of the width).</summary>
    private static int Count(SKBitmap bitmap, SKColor color, double from = 0, double to = 1)
    {
        var count = 0;
        for (var x = (int)(from * bitmap.Width); x < (int)(to * bitmap.Width); x++)
        {
            for (var y = 0; y < bitmap.Height; y++)
            {
                var p = bitmap.GetPixel(x, y);
                if (Math.Abs(p.Red - color.Red) <= 12 && Math.Abs(p.Green - color.Green) <= 12 && Math.Abs(p.Blue - color.Blue) <= 12)
                    count++;
            }
        }
        return count;
    }

    private static string Text(PageContent page) =>
        string.Concat(page.Items.OfType<GlyphItem>().Select(g => g.Run.Text));

    // ───────────────────────── Reading ─────────────────────────

    [Fact]
    public void A_column_chart_is_read_from_its_cached_values()
    {
        var chart = Drawn(Columns());

        var plot = Assert.Single(chart.Plots);
        Assert.Equal((ChartKind.Bar, ChartGrouping.Standard, false), (plot.Kind, plot.Grouping, plot.Horizontal));
        Assert.Equal(["North", "South", "West"], plot.Series.Select(s => s.Name));
        Assert.Equal([40.0, 55, 70, 62], plot.Series[0].Values.Select(v => v!.Value));
        Assert.Equal(["Q1", "Q2", "Q3", "Q4"], plot.Series[0].Categories!);
        Assert.Equal(Red, plot.Series[0].Style.Fill);
        Assert.Equal(["Sales by quarter"], chart.Title!.Lines);
        Assert.Equal("r", chart.Legend!.Position);
        Assert.Equal([false, true], chart.Axes.Select(a => a.IsValue));
        Assert.Equal(["Units"], chart.Axes[1].Title!.Lines);
    }

    [Fact]
    public void Series_without_a_colour_take_the_theme_accents_in_Office_order()
    {
        var series = string.Concat(Enumerable.Range(0, 8).Select(i => Series(i, $"S{i}", ["a"], [i + 1])));
        var chart = Drawn(Chart(Bars(series), Axes()));

        Assert.Equal(new SKColor(0x44, 0x72, 0xC4), chart.Palette.Color(0));
        Assert.Equal(new SKColor(0xED, 0x7D, 0x31), chart.Palette.Color(1));
        Assert.Equal(new SKColor(0x70, 0xAD, 0x47), chart.Palette.Color(5));
        // The seventh is the first accent darker (60 % of its luminance).
        chart.Palette.Color(6).ToHsl(out _, out _, out var dark);
        chart.Palette.Color(0).ToHsl(out _, out _, out var light);
        Assert.InRange(dark / light, 0.55, 0.65);
    }

    [Theory]
    [InlineData("bar3DChart")]
    [InlineData("radarChart")]
    [InlineData("stockChart")]
    [InlineData("surfaceChart")]
    public void Other_chart_types_are_named_instead(string kind)
    {
        var xml = Chart($"<c:{kind}>{Series(0, "S", ["a"], [1])}<c:axId val=\"1\"/><c:axId val=\"2\"/></c:{kind}>", Axes());
        Assert.Equal(new ChartRead(null, "charts"), Read(xml));
    }

    [Fact]
    public void A_single_series_takes_its_name_as_title_until_the_title_is_deleted()
    {
        var xml = $"<c:chartSpace {Namespaces}><c:chart><c:plotArea>{Pie(Series(0, "Budget", ["a", "b"], [1, 2]))}</c:plotArea></c:chart></c:chartSpace>";
        Assert.Equal(["Budget"], Drawn(xml).Title!.Lines);
        Assert.Null(Drawn(xml.Replace("<c:chart>", "<c:chart><c:autoTitleDeleted val=\"1\"/>", StringComparison.Ordinal)).Title);
    }

    // ───────────────────────── Drawing ─────────────────────────

    [Fact]
    public void Columns_are_drawn_in_their_colours_as_high_as_their_values()
    {
        var (bitmap, page) = Picture(Columns());
        using (bitmap)
        {
            Assert.True(Count(bitmap, Red) > 500 && Count(bitmap, Green) > 500 && Count(bitmap, Blue) > 500);
            // North's Q3 (70) stands higher than its Q1 (40): more red in the third quarter of the plot than the first.
            var q1 = Count(bitmap, Red, 0.05, 0.3);
            var q3 = Count(bitmap, Red, 0.45, 0.7);
            Assert.InRange(q3 / (double)q1, 1.4, 2.1);
        }
        // Title, axis labels, axis titles and the legend are text in the page, not pixels.
        var text = Text(page);
        foreach (var expected in new[] { "Sales by quarter", "Q1", "Q4", "North", "West", "Units", "Quarter", "40" })
            Assert.Contains(expected, text);
    }

    private const string RedLine = "<c:spPr><a:ln w=\"50800\"><a:solidFill><a:srgbClr val=\"C00000\"/></a:solidFill></a:ln></c:spPr>";

    [Fact]
    public void Stacked_and_horizontal_bars_and_lines_areas_and_scatter_plots_are_drawn()
    {
        string[] q = ["Q1", "Q2", "Q3", "Q4"];
        foreach (var xml in new[]
        {
            Columns("stacked"), Columns("percentStacked"), Columns(direction: "bar"),
            Chart(Lines(Series(0, "A", q, [1, 3, null, 2], RedLine)), Axes()),
            Chart(Areas(Series(0, "A", q, [1, 3, 2, 2], Solid("C00000")), "stacked"), Axes()),
            Chart(Scatter(ScatterSeries(0, "A", [1, 2, 4], [3, 1, 2], RedLine)), Axes(scatter: true)),
        })
        {
            var (bitmap, _) = Picture(xml);
            using (bitmap)
                Assert.True(Count(bitmap, Red) > 100, xml[xml.IndexOf("<c:plotArea>", StringComparison.Ordinal)..][..60]);
        }
    }

    [Fact]
    public void Pie_slices_take_their_share_of_the_circle()
    {
        var (bitmap, page) = Picture(Slices("<c:dLbls><c:showVal val=\"0\"/><c:showPercent val=\"1\"/></c:dLbls>"));
        using (bitmap)
        {
            var shares = new[] { new SKColor(0xFF, 0, 0), new SKColor(0, 0xC0, 0), new SKColor(0, 0, 0xFF), new SKColor(0xFF, 0xC0, 0) }
                .Select(c => (double)Count(bitmap, c)).ToList();
            var total = shares.Sum();
            // 40 %, 30 %, 20 %, 10 % of the pie (labels and the legend's keys take a little).
            Assert.InRange(shares[0] / total, 0.36, 0.44);
            Assert.InRange(shares[3] / total, 0.07, 0.13);
        }
        var text = Text(page);
        Assert.Contains("40%", text);
        Assert.Contains("Travel", text);
    }

    [Fact]
    public void A_picture_of_a_chart_is_a_png()
    {
        var png = ChartPicture.Png(Drawn(Columns()), 240, 150, 96);
        using var bitmap = SKBitmap.Decode(png);
        Assert.Equal((320, 200), (bitmap.Width, bitmap.Height));
    }

    // ───────────────────────── Hostile input ─────────────────────────

    [Fact]
    public void A_cache_that_claims_more_points_than_Filee_draws_is_refused()
    {
        var claimed = Columns().Replace("<c:ptCount val=\"4\"/>", "<c:ptCount val=\"2000000000\"/>", StringComparison.Ordinal);
        Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(() => Read(claimed)).ErrorKey);

        var far = Columns().Replace("<c:pt idx=\"3\">", "<c:pt idx=\"99999999\">", StringComparison.Ordinal);
        Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(() => Read(far)).ErrorKey);

        var series = string.Concat(Enumerable.Range(0, ChartReader.MaxSeries + 1).Select(i => $"<c:ser><c:idx val=\"{i}\"/></c:ser>"));
        Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(() => Read(Chart(Bars(series), Axes()))).ErrorKey);
    }

    [Fact]
    public void Huge_values_long_texts_and_many_categories_are_drawn_in_time()
    {
        var categories = Enumerable.Range(0, 50_000).Select(i => new string('w', 200) + i).ToArray();
        var values = Enumerable.Range(0, 50_000).Select(i => (double?)(i % 3 == 0 ? 1e308 : i % 3 == 1 ? -1e308 : double.Epsilon)).ToArray();
        var labels = "<c:dLbls><c:showVal val=\"1\"/><c:showCatName val=\"1\"/></c:dLbls>";
        var xml = Chart(Lines(Series(0, new string('t', 100_000), categories, values, labels)), Axes(valueExtra: "<c:majorUnit val=\"1e-300\"/>"),
            title: new string('x', 100_000));
        var watch = Stopwatch.StartNew();

        var chart = Drawn(xml);
        Assert.Equal(ChartReader.MaxText, chart.Title!.Lines[0].Length);
        var (bitmap, _) = Picture(xml);
        bitmap.Dispose();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"took {watch.Elapsed}");
    }

    [Fact]
    public void A_chart_placed_over_and_over_is_refused_by_the_workbook_budget()
    {
        var points = (int)(ChartBudget.MaxPoints / 20);
        var big = Chart(Lines(Series(0, "S", [], [.. Enumerable.Range(0, points).Select(i => (double?)i)])), Axes());
        var anchors = string.Concat(Enumerable.Repeat(SheetAnchor(), 21));
        var path = Save(Workbook(anchors, big));

        Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(() => XlsxReader.Read(path)).ErrorKey);
    }

    // ───────────────────────── Sheets ─────────────────────────

    private const string Xdr = "xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";

    /// <summary>A chart from B2 to I20, as Excel anchors one.</summary>
    private static string SheetAnchor() =>
        "<xdr:twoCellAnchor><xdr:from><xdr:col>1</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>1</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from>" +
        "<xdr:to><xdr:col>8</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>19</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:to>" +
        "<xdr:graphicFrame macro=\"\"><xdr:nvGraphicFramePr><xdr:cNvPr id=\"2\" name=\"Chart 1\"/><xdr:cNvGraphicFramePr/></xdr:nvGraphicFramePr>" +
        $"<xdr:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/></xdr:xfrm><a:graphic><a:graphicData uri=\"{GraphicUri}\">" +
        $"<c:chart xmlns:c=\"{GraphicUri}\" r:id=\"rIdC\"/></a:graphicData></a:graphic></xdr:graphicFrame><xdr:clientData/></xdr:twoCellAnchor>";

    private static XlsxBuilder Workbook(string anchors, string chart)
    {
        var book = new XlsxBuilder();
        book.Sheet("Data", XlsxBuilder.Row(1, book.Text("A1", "Sales")), after: "<pageSetup paperSize=\"9\" orientation=\"portrait\"/><drawing r:id=\"rIdD\"/>");
        book.SheetRelationships[1] = [("rIdD", "drawing", "../drawings/drawing1.xml")];
        book.Parts["xl/drawings/drawing1.xml"] = $"<xdr:wsDr {Xdr}>{anchors}</xdr:wsDr>";
        book.Parts["xl/drawings/_rels/drawing1.xml.rels"] = OpcWriter.Relationships([("rIdC", "chart", "../charts/chart1.xml")]);
        book.Parts["xl/charts/chart1.xml"] = chart;
        return book;
    }

    private string Save(XlsxBuilder book) => book.Save(Path.Combine(fx.NewFolder(), "book.xlsx"));

    [Fact]
    public void A_workbook_with_a_chart_stays_on_the_sheet_printer_and_prints_it()
    {
        var path = Save(Workbook(SheetAnchor(), Columns()));
        Assert.Null(SheetCapabilities.FirstUnsupported(path, "xlsx"));

        var book = XlsxReader.Read(path);
        Assert.IsType<SheetChart>(Assert.Single(book.Sheets[0].Drawings));
        using var printed = SheetPrinter.Print(book, new SheetPrintSource("book.xlsx", "C:\\"), DocumentLimits.Default, DateTime.Now, null, TestContext.Current.CancellationToken);
        using var page = printed.Pages[0].Render(72);
        Assert.True(Count(page, Red) > 300 && Count(page, Blue) > 300);
        Assert.Contains("Sales by quarter", Text(printed.Pages[0]));
    }

    [Fact]
    public void A_workbook_with_a_3D_chart_is_still_named()
    {
        var xml = Chart($"<c:bar3DChart><c:barDir val=\"col\"/>{Series(0, "S", ["a"], [1])}<c:axId val=\"1\"/><c:axId val=\"2\"/></c:bar3DChart>", Axes());
        Assert.Equal("charts", SheetCapabilities.FirstUnsupported(Save(Workbook(SheetAnchor(), xml)), "xlsx"));
    }

    /// <summary>
    /// A chart sheet "Chart1" (landscape, a footer with the page number) holding <paramref name="chart"/> on an absolute
    /// anchor, before (or after) a worksheet "Data".
    /// </summary>
    private static XlsxBuilder ChartSheetBook(string chart, bool chartFirst = true, bool hidden = false, string over = "")
    {
        var book = new XlsxBuilder();
        book.Sheet(chartFirst ? "Chart1" : "Data", chartFirst ? "" : XlsxBuilder.Row(1, book.Text("A1", "Sales")), hidden: hidden && chartFirst);
        book.Sheet(chartFirst ? "Data" : "Chart1", chartFirst ? XlsxBuilder.Row(1, book.Text("A1", "Sales")) : "", hidden: hidden && !chartFirst);
        var (chartId, dataId) = chartFirst ? ("rId1", "rId2") : ("rId2", "rId1");
        book.Parts["xl/_rels/workbook.xml.rels"] = OpcWriter.Relationships([
            (chartId, "chartsheet", "chartsheets/sheet1.xml"), (dataId, "worksheet", $"worksheets/sheet{(chartFirst ? 2 : 1)}.xml"),
            ("rIdS", "sharedStrings", "sharedStrings.xml"), ("rIdT", "styles", "styles.xml")]);
        book.Parts["xl/chartsheets/sheet1.xml"] =
            $"<chartsheet {XlsxBuilder.Namespaces}><sheetPr/><sheetViews><sheetView zoomToFit=\"1\" workbookViewId=\"0\"/></sheetViews>" +
            "<pageMargins left=\"0.7\" right=\"0.7\" top=\"0.75\" bottom=\"0.75\" header=\"0.3\" footer=\"0.3\"/><pageSetup paperSize=\"9\" orientation=\"landscape\"/>" +
            "<headerFooter><oddFooter>&amp;CPage &amp;P</oddFooter></headerFooter><drawing r:id=\"rIdD\"/></chartsheet>";
        book.Parts["xl/chartsheets/_rels/sheet1.xml.rels"] = OpcWriter.Relationships([("rIdD", "drawing", "../drawings/drawing1.xml")]);
        book.Parts["xl/drawings/drawing1.xml"] =
            $"<xdr:wsDr {Xdr}><xdr:absoluteAnchor><xdr:pos x=\"0\" y=\"0\"/><xdr:ext cx=\"9309100\" cy=\"6073775\"/>" +
            "<xdr:graphicFrame macro=\"\"><xdr:nvGraphicFramePr><xdr:cNvPr id=\"2\" name=\"Chart 1\"/><xdr:cNvGraphicFramePr/></xdr:nvGraphicFramePr>" +
            $"<xdr:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/></xdr:xfrm><a:graphic><a:graphicData uri=\"{GraphicUri}\">" +
            $"<c:chart xmlns:c=\"{GraphicUri}\" r:id=\"rIdC\"/></a:graphicData></a:graphic></xdr:graphicFrame><xdr:clientData/></xdr:absoluteAnchor>{over}</xdr:wsDr>";
        book.Parts["xl/drawings/_rels/drawing1.xml.rels"] = OpcWriter.Relationships([("rIdC", "chart", "../charts/chart1.xml")]);
        book.Parts["xl/charts/chart1.xml"] = chart;
        return book;
    }

    [Fact]
    public void A_chart_sheet_prints_as_one_page_with_its_chart_filling_the_margins()
    {
        var path = Save(ChartSheetBook(Columns()));
        Assert.Null(SheetCapabilities.FirstUnsupported(path, "xlsx"));

        var book = XlsxReader.Read(path);
        // The writers to other spreadsheet formats don't see it.
        Assert.Equal(["Data"], book.Sheets.Select(s => s.Name));
        Assert.Equal(["Chart1", "Data"], book.Printed().Select(s => s.Name));
        using var printed = SheetPrinter.Print(book, new SheetPrintSource("book.xlsx", "C:\\"), DocumentLimits.Default, DateTime.Now, null, TestContext.Current.CancellationToken);
        Assert.Equal(2, printed.Pages.Count);
        var page = printed.Pages[0];
        Assert.True(page.Width > page.Height); // landscape A4
        Assert.Equal(841.9, page.Width, 0);
        using (var bitmap = page.Render(72))
        {
            Assert.True(Count(bitmap, Red) > 2000 && Count(bitmap, Blue) > 2000);
            // Nothing is drawn inside the left margin (0.7 in), the chart starts there.
            Assert.Equal(0, Count(bitmap, Red, 0, 40 / 842.0));
        }
        Assert.Contains("Sales by quarter", Text(page));
        Assert.Contains("Page 1", Text(page));
        Assert.Contains("Sales", Text(printed.Pages[1]));
        Assert.Equal(["Chart1", "Data"], printed.Outline.Select(o => o.Title));

        // After the worksheet, and hidden.
        Assert.Equal(["Data", "Chart1"], XlsxReader.Read(Save(ChartSheetBook(Columns(), chartFirst: false))).Printed().Select(s => s.Name));
        Assert.Equal(["Data"], XlsxReader.Read(Save(ChartSheetBook(Columns(), hidden: true))).Printed().Select(s => s.Name));
    }

    [Fact]
    public void A_chart_sheet_whose_chart_is_not_drawn_is_named()
    {
        var radar = Chart($"<c:radarChart>{Series(0, "S", ["a"], [1])}<c:axId val=\"1\"/><c:axId val=\"2\"/></c:radarChart>", Axes());
        var path = Save(ChartSheetBook(radar));
        Assert.Equal("charts", SheetCapabilities.FirstUnsupported(path, "xlsx"));
        Assert.Empty(XlsxReader.Read(path).ChartSheets);

        // A shape placed over the chart isn't printed with it.
        var shape = "<xdr:absoluteAnchor><xdr:pos x=\"0\" y=\"0\"/><xdr:ext cx=\"1270000\" cy=\"1270000\"/><xdr:sp><xdr:nvSpPr><xdr:cNvPr id=\"3\" name=\"Box\"/><xdr:cNvSpPr/></xdr:nvSpPr>" +
                    "<xdr:spPr><a:prstGeom prst=\"rect\"/><a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill></xdr:spPr></xdr:sp><xdr:clientData/></xdr:absoluteAnchor>";
        Assert.Equal("shapes", SheetCapabilities.FirstUnsupported(Save(ChartSheetBook(Columns(), over: shape)), "xlsx"));
    }

    [Fact]
    public void Chart_sheets_count_against_the_workbook_chart_budget()
    {
        var points = Series(0, "S", [], [.. Enumerable.Range(0, 20).Select(i => (double?)i)]);
        var big = Chart(Lines(points), Axes()).Replace("<c:ptCount val=\"20\"/>", $"<c:ptCount val=\"{ChartReader.MaxPoints}\"/>", StringComparison.Ordinal);
        // One chart sheet of a full column of points is drawn; eleven of them pass the budget of ten.
        var book = ChartSheetBook(big);
        var sheets = string.Concat(Enumerable.Range(0, 11).Select(i => $"<sheet name=\"C{i}\" sheetId=\"{i + 10}\" r:id=\"rIdC{i}\"/>"));
        book.Parts["xl/workbook.xml"] = $"<workbook {XlsxBuilder.Namespaces}><sheets>{sheets}</sheets></workbook>";
        book.Parts["xl/_rels/workbook.xml.rels"] = OpcWriter.Relationships([.. Enumerable.Range(0, 11).Select(i => ($"rIdC{i}", "chartsheet", "chartsheets/sheet1.xml")),
            ("rIdS", "sharedStrings", "sharedStrings.xml"), ("rIdT", "styles", "styles.xml")]);
        var path = Save(book);
        Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(() => XlsxReader.Read(path)).ErrorKey);
    }

    // ───────────────────────── Word ─────────────────────────

    private string WordWithChart(string chart)
    {
        var docx = new DocxBuilder();
        var id = docx.Chart(chart);
        docx.Paragraph(DocxBuilder.P(DocxBuilder.R("Before the chart.")));
        docx.Paragraph(DocxBuilder.P(InlineChart(id, 432 * 12700, 252 * 12700)));
        return docx.Save(Path.Combine(fx.NewFolder(), "chart.docx"));
    }

    [Fact]
    public void A_Word_chart_is_drawn_by_the_renderer_and_a_picture_of_it_goes_to_the_writers()
    {
        var path = WordWithChart(Columns());
        Assert.Null(new FileeRendererConverter().FirstUnsupported(path, "docx"));

        var document = DocxReader.Read(path, Path.Combine(fx.NewFolder(), "media"));
        Assert.True(document.LeftOut.IsEmpty);
        var picture = document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HImage>().Single();
        Assert.NotNull(picture.Chart);
        using (var png = SKBitmap.Decode(picture.Path))
            Assert.True(Count(png, Red) > 300);

        using var page = SKBitmap.Decode(FileeRenderer.ToImages(document, 72, cancellationToken: TestContext.Current.CancellationToken).First());
        Assert.True(Count(page, Red) > 300 && Count(page, Green) > 300);
    }

    [Fact]
    public void A_Word_chart_Filee_doesnt_draw_is_named_and_left_out()
    {
        var xml = Chart($"<c:radarChart>{Series(0, "S", ["a"], [1])}<c:axId val=\"1\"/><c:axId val=\"2\"/></c:radarChart>", Axes());
        var path = WordWithChart(xml);
        Assert.Equal("OLE objects and charts", new FileeRendererConverter().FirstUnsupported(path, "docx"));

        var document = DocxReader.Read(path, Path.Combine(fx.NewFolder(), "media"));
        Assert.Contains(document.LeftOut.Kinds, k => k.Kind == HLeftOutKind.Chart);
    }

    // ───────────────────────── Slides ─────────────────────────

    private string DeckWithChart(string chart)
    {
        var deck = new SlideDeckBuilder();
        deck.Part("ppt/charts/chart1.xml", chart, "drawingml.chart");
        var frame = "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"4\" name=\"Chart 3\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>" +
                    $"<p:xfrm><a:off x=\"{SlideDeckBuilder.E(100)}\" y=\"{SlideDeckBuilder.E(60)}\"/><a:ext cx=\"{SlideDeckBuilder.E(600)}\" cy=\"{SlideDeckBuilder.E(380)}\"/></p:xfrm>" +
                    $"<a:graphic><a:graphicData uri=\"{GraphicUri}\"><c:chart xmlns:c=\"{GraphicUri}\" r:id=\"rIdC\"/></a:graphicData></a:graphic></p:graphicFrame>";
        deck.Slide(frame, relationships: [("rIdC", "chart", "../charts/chart1.xml")]);
        return deck.Save(Path.Combine(fx.NewFolder(), "deck.pptx"));
    }

    [Fact]
    public void A_slide_chart_is_drawn_and_its_picture_made_for_what_takes_pictures()
    {
        var path = DeckWithChart(Columns());
        Assert.Null(new FileeRendererConverter().FirstUnsupported(path, "pptx"));

        var deck = PptxSlideReader.Read(path, cancellationToken: TestContext.Current.CancellationToken);
        var shape = Assert.Single(deck.Slides[0].Elements.OfType<SlideShape>(), s => s.Chart is not null);
        Assert.NotNull(shape.Chart);
        using (var bitmap = Assert.Single(SlideOutput.ToBitmaps(deck, 72, cancellationToken: TestContext.Current.CancellationToken)))
            Assert.True(Count(bitmap, Red) > 500 && Count(bitmap, Blue) > 500);
        // The ODP writer and the deck's text take the picture.
        using var picture = SKBitmap.Decode(Assert.IsType<PictureFill>(shape.Fill).Image.Data);
        Assert.True(Count(picture, Red) > 500);
    }

    [Fact]
    public void A_slide_chart_Filee_doesnt_draw_is_named()
    {
        var xml = Chart($"<c:pie3DChart>{Series(0, "S", ["a"], [1])}</c:pie3DChart>");
        Assert.Equal("charts", new FileeRendererConverter().FirstUnsupported(DeckWithChart(xml), "pptx"));
    }

    // ───────────────────────── Text ─────────────────────────

    [Fact]
    public void Chart_text_in_other_scripts_is_drawn_as_text()
    {
        var xml = Chart(Bars(Series(0, "매출", ["一月", "二月"], [1, 2])), Axes(), "분기별 매출");
        var (bitmap, page) = Picture(xml);
        bitmap.Dispose();
        var text = Text(page);
        Assert.Contains("분기별 매출", text);
        Assert.Contains("一月", text);
    }
}
