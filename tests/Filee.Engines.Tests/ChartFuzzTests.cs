// Mutation fuzzing of DrawingML charts (Charts/): the seeds are ChartTests' column chart, a pie with labels, a chart
// with what costs the reader and the drawer the most (several plots on two pairs of axes, stacked bars, smooth lines
// with markers and data labels, a doughnut's rings, scatter, a log axis, manual layouts, turned labels, dates) and one
// with trendlines, error bars, point labels, two category levels, a rich title and gradient and pattern fills,
// written in code, as a chart part alone (XmlFuzzer), inside a workbook the sheet printer prints and inside a chart
// template (.crtx) the drawing engine draws (DocumentFuzzer); and the 3D, radar, bubble, stock, surface, pie-of-pie and
// Office 2016 (chartex) samples of ChartSamples as parts alone; Excel 97–2003 chart substreams (XlsChartBuilder's
// samples) as records alone (BiffChartReader) and inside .xls workbooks the sheet printer prints.
// Every input must be read or refused with InvalidDataException / ConversionRefusedException, in time and on a 1 MB
// stack; what is read is drawn small. Run by FuzzHarness: a short fixed run normally, FILEE_FUZZ_SECONDS for a longer
// random one, FILEE_FUZZ_SEED to repeat, FILEE_FUZZ_OUT to keep failing inputs.

using System.Text;
using System.Xml;
using Filee.Engines.Charts;
using Filee.Engines.Documents;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office.Sheets;
using Filee.Engines.Rendering;
using Filee.Engines.Slides;
using SkiaSharp;
using static Filee.Engines.Tests.ChartBuilder;

namespace Filee.Engines.Tests;

[Trait("Category", "Fuzz")]
public class ChartFuzzTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>Inputs per seed in a normal test run.</summary>
    private const int QuickIterations = 40;

    private static readonly string[] Tokens =
    [
        "<c:ptCount val=\"99999999\"/>", "<c:pt idx=\"-1\"><c:v>NaN</c:v></c:pt>", "<c:pt idx=\"1048575\"><c:v>1e309</c:v></c:pt>",
        "<c:logBase val=\"0.5\"/>", "<c:logBase val=\"10\"/>", "<c:majorUnit val=\"1e-300\"/>", "<c:crossesAt val=\"1e308\"/>",
        "<c:holeSize val=\"1000\"/>", "<c:explosion val=\"-5\"/>", "<c:firstSliceAng val=\"1e9\"/>", "<c:gapWidth val=\"-1\"/>",
        "<c:overlap val=\"100000\"/>", "<c:tickLblSkip val=\"0\"/>", "<c:orientation val=\"maxMin\"/>", "<c:barDir val=\"bar\"/>",
        "<c:grouping val=\"percentStacked\"/>", "<c:smooth val=\"1\"/>", "<c:delete val=\"1\"/>", "<c:crosses val=\"max\"/>",
        "<c:manualLayout><c:x val=\"-1e9\"/><c:y val=\"NaN\"/><c:w val=\"1e9\"/><c:h val=\"0\"/></c:manualLayout>",
        "<c:txPr><a:bodyPr rot=\"-5400000\"/><a:p><a:pPr><a:defRPr sz=\"40000\" b=\"1\"/></a:pPr></a:p></c:txPr>",
        "<c:marker><c:symbol val=\"star\"/><c:size val=\"72\"/></c:marker>", "<c:dLbls><c:showVal/><c:showPercent/><c:dLblPos val=\"outEnd\"/></c:dLbls>",
        "<c:dispBlanksAs val=\"span\"/>", "<c:min val=\"5\"/><c:max val=\"-5\"/>", "&amp;", "<![CDATA[x]]>",
        "<c:trendline><c:trendlineType val=\"poly\"/><c:order val=\"99\"/><c:forward val=\"1e308\"/><c:intercept val=\"NaN\"/><c:dispEq/><c:dispRSqr/></c:trendline>",
        "<c:trendline><c:trendlineType val=\"movingAvg\"/><c:period val=\"-3\"/></c:trendline>", "<c:trendlineType val=\"power\"/>",
        "<c:errBars><c:errDir val=\"x\"/><c:errValType val=\"cust\"/><c:plus><c:numLit><c:ptCount val=\"99999999\"/></c:numLit></c:plus></c:errBars>",
        "<c:errValType val=\"stdDev\"/>", "<c:val val=\"1e308\"/>", "<c:noEndCap val=\"1\"/>",
        "<c:dLbl><c:idx val=\"1048575\"/><c:tx><c:rich><a:p><a:r><a:rPr baseline=\"-9999999\"/><a:t>x</a:t></a:r></a:p></c:rich></c:tx></c:dLbl>",
        "<c:separator></c:separator>", "<c:style val=\"48\"/>", "<a:pattFill prst=\"pct90\"/>",
        "<a:gradFill><a:gsLst><a:gs pos=\"-5\"><a:srgbClr val=\"FF0000\"/></a:gs><a:gs pos=\"1e9\"><a:srgbClr val=\"0000FF\"/></a:gs></a:gsLst><a:path path=\"circle\"><a:fillToRect l=\"1e9\"/></a:path></a:gradFill>",
        "<c:lvl><c:pt idx=\"99999\"><c:v>x</c:v></c:pt></c:lvl>",
        "<c:view3D><c:rotX val=\"-1e9\"/><c:rotY val=\"NaN\"/><c:depthPercent val=\"999999\"/><c:hPercent val=\"0\"/><c:rAngAx val=\"0\"/></c:view3D>",
        "<c:gapDepth val=\"-9\"/>", "<c:grouping val=\"standard\"/>", "<c:bubbleScale val=\"1e9\"/>", "<c:showNegBubbles val=\"1\"/>",
        "<c:bubbleSize><c:numLit><c:ptCount val=\"3\"/><c:pt idx=\"0\"><c:v>-1e308</c:v></c:pt><c:pt idx=\"2\"><c:v>0</c:v></c:pt></c:numLit></c:bubbleSize>",
        "<c:radarStyle val=\"filled\"/>", "<c:hiLowLines/>", "<c:upDownBars><c:gapWidth val=\"-50\"/></c:upDownBars>", "<c:dropLines/>", "<c:wireframe val=\"1\"/>",
        "<c:bandFmts><c:bandFmt><c:idx val=\"999999\"/></c:bandFmt></c:bandFmts>", "<c:splitType val=\"cust\"/>", "<c:splitPos val=\"-1e308\"/>",
        "<c:custSplit><c:secondPiePt val=\"1048575\"/></c:custSplit>", "<c:secondPieSize val=\"0\"/>", "<c:ofPieType val=\"bar\"/>",
        "<c:showLeaderLines val=\"1\"/>", "<c:layout><c:manualLayout><c:x val=\"1e9\"/><c:y val=\"-1e9\"/></c:manualLayout></c:layout>",
    ];

    /// <summary>Tokens of Office 2016 chart parts (chartex).</summary>
    private static readonly string[] ExTokens =
    [
        "<cx:pt idx=\"1048575\">1e309</cx:pt>", "<cx:pt idx=\"-1\">NaN</cx:pt>", "ptCount=\"99999999\"", "<cx:binCount val=\"0\"/>",
        "<cx:binSize val=\"1e-308\"/>", "<cx:binning underflow=\"1e308\" overflow=\"-1e308\"/>", "<cx:subtotals><cx:idx val=\"-5\"/></cx:subtotals>",
        "<cx:statistics quartileMethod=\"inclusive\"/>", "<cx:visibility meanLine=\"1\" nonoutliers=\"1\"/>", "<cx:parentLabelLayout val=\"banner\"/>",
        "<cx:catScaling gapWidth=\"-1e9\"/>", "<cx:valScaling max=\"-1\" min=\"1e308\"/>", "layoutId=\"regionMap\"", "layoutId=\"sunburst\"",
        "<cx:lvl ptCount=\"3\"><cx:pt idx=\"2\"></cx:pt></cx:lvl>", "hidden=\"1\"", "<cx:aggregation/>",
    ];

    [Fact]
    public void ChartReader_reads_or_refuses_every_mutated_chart_part() =>
        new FuzzHarness(fx, QuickIterations, 20261120).Data("chart", "xml",
            [.. new[] { Columns(), Slices(Labels), Costly(), Rings(), Points(), Polished() }.Select(Encoding.UTF8.GetBytes)], FuzzHarness.Xml(Tokens), DrawPart);

    [Fact]
    public void ChartReader_reads_or_refuses_every_mutated_3D_radar_bubble_stock_surface_and_pie_of_pie_part() =>
        new FuzzHarness(fx, QuickIterations, 20261121).Data("chart-kinds", "xml",
            [.. new[] { ChartSamples.Columns3D("standard"), ChartSamples.Pie3D(), ChartSamples.Areas3D(), ChartSamples.Radar("filled"), ChartSamples.Bubbles(true),
                ChartSamples.Stock(), ChartSamples.Surface(threeD: true), ChartSamples.Surface(threeD: false, wireframe: true), ChartSamples.OfPie(bar: true) }
                .Select(Encoding.UTF8.GetBytes)], FuzzHarness.Xml(Tokens), DrawPart);

    [Fact]
    public void ChartReader_reads_or_refuses_every_mutated_chartex_part() =>
        new FuzzHarness(fx, QuickIterations, 20261121).Data("chartex", "xml",
            [.. new[] { ChartSamples.Waterfall(), ChartSamples.Funnel(), ChartSamples.Treemap("banner"), ChartSamples.Sunburst(), ChartSamples.Histogram(),
                ChartSamples.Pareto(), ChartSamples.BoxWhisker() }.Select(Encoding.UTF8.GetBytes)], FuzzHarness.Xml(ExTokens), DrawPart);

    [Fact]
    public void A_workbook_with_charts_is_read_or_refused_whatever_its_parts_say() =>
        new FuzzHarness(fx, QuickIterations, 20261120).Files("chart-xlsx", "xlsx", [Workbook(Columns()), Workbook(Costly()), Workbook(Rings()),
            File.ReadAllBytes(ChartTests.ChartSheetBook(Columns()).Save(Path.Combine(fx.NewFolder(), "chartsheet.xlsx")))], FuzzHarness.Packages, Print);

    [Fact]
    public void A_chart_template_is_drawn_or_refused_whatever_its_parts_say() =>
        new FuzzHarness(fx, QuickIterations, 20261120).Files("chart-crtx", "crtx",
            [Template(Columns()), Template(Chart(Bars(""), Axes())), Template(Rings())], FuzzHarness.Packages, (path, _) =>
            {
                using var template = ChartTemplate.Open(path);
                template.Page(0).Render(12).Dispose();
            });

    [Fact]
    public void BiffChartReader_reads_or_refuses_every_mutated_chart_substream() =>
        new FuzzHarness(fx, QuickIterations, 20261121).Data("chart-biff", "bin",
            [.. XlsChartBuilder.Samples().Select(s => Substream(s.Chart.Build()))], FuzzHarness.Bytes, DrawBiff);

    [Fact]
    public void An_xls_workbook_with_charts_is_read_or_refused_whatever_its_records_say() =>
        new FuzzHarness(fx, QuickIterations, 20261121).Files("chart-xls", "xls",
            [.. XlsChartBuilder.Samples().Take(4).Select(s => XlsWorkbook(s.Chart))], FuzzHarness.Bytes, PrintXls);

    /// <summary>Records as they stand in a workbook stream: type, length, data.</summary>
    private static byte[] Substream(List<(int Id, byte[] Data)> records)
    {
        var bytes = new List<byte>();
        foreach (var (id, data) in records)
            bytes.AddRange([.. XlsBuilder.U16(id), .. XlsBuilder.U16(data.Length), .. data]);
        return [.. bytes];
    }

    private static void DrawBiff(byte[] bytes)
    {
        var records = new List<(int Id, byte[] Data)>();
        for (var at = 0; at + 4 <= bytes.Length;)
        {
            var length = BitConverter.ToUInt16(bytes, at + 2);
            if (at + 4 + length > bytes.Length)
                break;
            records.Add((BitConverter.ToUInt16(bytes, at), bytes[(at + 4)..(at + 4 + length)]));
            at += 4 + length;
        }
        var book = new BiffChartBook { Font = i => new BiffFont("Arial", 10, false, false, 0x7FFF), FontCount = 6, Color = _ => "#808080", Format = _ => "0.0", CellFormat = _ => "m/d/yyyy" };
        if (BiffChartReader.Read(records, book).Chart is not { } chart)
            return;
        var page = new PageContent(240, 160);
        using (var fonts = new ChartFonts())
            ChartDrawer.Draw(page, chart, new SKRect(0, 0, 240, 160), fonts);
        page.Render(24).Dispose();
    }

    private byte[] XlsWorkbook(XlsChartBuilder chart)
    {
        var book = new XlsBuilder();
        book.Sheet("Data").Text(0, 0, "Sales").After(chart.Embedded(1, 1, 12, 6));
        book.ChartSheet("Chart1", chart.Build());
        return File.ReadAllBytes(book.Save(Path.Combine(fx.NewFolder(), "seed.xls")));
    }

    private static void PrintXls(string path, string media)
    {
        _ = media;
        SheetCapabilities.FirstUnsupported(path, "xls");
        var book = XlsReader.Read(path);
        using var printed = SheetPrinter.Print(book, new SheetPrintSource("book.xls", "C:\\"), DocumentLimits.Default, DateTime.Now, null, default);
        if (printed.Pages.Count > 0)
            printed.Pages[0].Render(12).Dispose();
    }

    private byte[] Template(string chart) =>
        File.ReadAllBytes(ChartTemplateBuilder.Save(Path.Combine(fx.NewFolder(), "seed.crtx"), chart));

    private const string Labels = "<c:dLbls><c:showVal val=\"1\"/><c:showCatName val=\"1\"/><c:showPercent val=\"1\"/><c:dLblPos val=\"outEnd\"/></c:dLbls>";

    private static void DrawPart(byte[] bytes)
    {
        System.Xml.Linq.XDocument xml;
        try
        {
            xml = BoundedXml.Bytes(bytes, "chart1.xml", System.Xml.Linq.LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
        if (ChartReader.Read(xml.Root, DrawingColors.FromTheme(null)).Chart is not { } chart)
            return;
        var page = new PageContent(240, 160);
        using (var fonts = new ChartFonts())
            ChartDrawer.Draw(page, chart, new SKRect(0, 0, 240, 160), fonts);
        page.Render(24).Dispose();
    }

    private static void Print(string path, string media)
    {
        _ = media;
        SheetCapabilities.FirstUnsupported(path, "xlsx");
        var book = XlsxReader.Read(path);
        using var printed = SheetPrinter.Print(book, new SheetPrintSource("book.xlsx", "C:\\"), DocumentLimits.Default, DateTime.Now, null, default);
        if (printed.Pages.Count > 0)
            printed.Pages[0].Render(12).Dispose();
    }

    private byte[] Workbook(string chart)
    {
        var book = new XlsxBuilder();
        book.Sheet("Data", XlsxBuilder.Row(1, book.Text("A1", "Sales")), after: "<drawing r:id=\"rIdD\"/>");
        book.SheetRelationships[1] = [("rIdD", "drawing", "../drawings/drawing1.xml")];
        book.Parts["xl/drawings/drawing1.xml"] =
            "<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><xdr:twoCellAnchor><xdr:from><xdr:col>1</xdr:col><xdr:colOff>0</xdr:colOff>" +
            "<xdr:row>1</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from><xdr:to><xdr:col>6</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>12</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:to>" +
            "<xdr:graphicFrame macro=\"\"><xdr:nvGraphicFramePr><xdr:cNvPr id=\"2\" name=\"Chart 1\"/><xdr:cNvGraphicFramePr/></xdr:nvGraphicFramePr><xdr:xfrm/>" +
            $"<a:graphic><a:graphicData uri=\"{GraphicUri}\"><c:chart xmlns:c=\"{GraphicUri}\" r:id=\"rIdC\"/></a:graphicData></a:graphic></xdr:graphicFrame>" +
            "<xdr:clientData/></xdr:twoCellAnchor></xdr:wsDr>";
        book.Parts["xl/drawings/_rels/drawing1.xml.rels"] = OpcWriter.Relationships([("rIdC", "chart", "../charts/chart1.xml")]);
        book.Parts["xl/charts/chart1.xml"] = chart;
        return File.ReadAllBytes(book.Save(Path.Combine(fx.NewFolder(), "seed.xlsx")));
    }

    /// <summary>What costs the chart reader and drawer the most, in one chart.</summary>
    internal static string Costly()
    {
        string[] months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug"];
        double?[] up = [1, 4, 9, 16, null, 36, 49, 64];
        double?[] down = [-3, 2, -1, 5, 8, -2, 4, 0];
        var marker = "<c:marker><c:symbol val=\"triangle\"/><c:size val=\"7\"/><c:spPr><a:solidFill><a:schemeClr val=\"accent2\"><a:lumMod val=\"75000\"/></a:schemeClr></a:solidFill></c:spPr></c:marker>";
        var point = "<c:dPt><c:idx val=\"2\"/><c:invertIfNegative val=\"0\"/><c:bubble3D val=\"0\"/><c:spPr><a:gradFill><a:gsLst><a:gs pos=\"0\"><a:srgbClr val=\"FF0000\"/></a:gs></a:gsLst></a:gradFill></c:spPr></c:dPt>";
        var bars = Bars(Series(0, "Up", months, up, point + Labels) + Series(1, "Down", months, down), grouping: "stacked");
        var lines = "<c:lineChart><c:grouping val=\"standard\"/><c:varyColors val=\"0\"/>" +
                    Series(2, "Trend", months, up, marker + "<c:dLbls><c:showVal val=\"1\"/><c:dLblPos val=\"t\"/></c:dLbls><c:smooth val=\"1\"/>", "0.0%") +
                    "<c:marker val=\"1\"/><c:axId val=\"3\"/><c:axId val=\"4\"/></c:lineChart>";
        var axes = Axes(categoryExtra: "<c:txPr><a:bodyPr rot=\"-2700000\"/><a:p><a:pPr><a:defRPr sz=\"900\"/></a:pPr></a:p></c:txPr><c:tickLblSkip val=\"2\"/>",
                       valueExtra: "<c:minorGridlines/>", categoryTitle: "Month", valueTitle: "Units") +
                   "<c:dateAx><c:axId val=\"3\"/><c:scaling><c:orientation val=\"maxMin\"/></c:scaling><c:delete val=\"1\"/><c:axPos val=\"t\"/><c:crossAx val=\"4\"/></c:dateAx>" +
                   "<c:valAx><c:axId val=\"4\"/><c:scaling><c:logBase val=\"10\"/><c:orientation val=\"minMax\"/></c:scaling><c:delete val=\"0\"/><c:axPos val=\"r\"/>" +
                   "<c:numFmt formatCode=\"0.0E+00\" sourceLinked=\"0\"/><c:tickLblPos val=\"high\"/><c:crossAx val=\"3\"/><c:crosses val=\"max\"/></c:valAx>";
        var layout = "<c:layout><c:manualLayout><c:layoutTarget val=\"inner\"/><c:xMode val=\"edge\"/><c:yMode val=\"edge\"/><c:x val=\"0.1\"/><c:y val=\"0.15\"/><c:w val=\"0.7\"/><c:h val=\"0.6\"/></c:manualLayout></c:layout>";
        var chart = Chart(bars + lines, axes, "Costly\nchart", legend: "b",
            spPr: "<c:spPr><a:solidFill><a:schemeClr val=\"bg1\"/></a:solidFill><a:ln w=\"9525\"><a:solidFill><a:schemeClr val=\"tx1\"><a:lumMod val=\"15000\"/><a:lumOff val=\"85000\"/></a:schemeClr></a:solidFill><a:prstDash val=\"dash\"/></a:ln></c:spPr>");
        return chart.Replace("<c:plotArea><c:layout/>", "<c:plotArea>" + layout, StringComparison.Ordinal);
    }

    /// <summary>A doughnut of three rings with a pulled-out slice and labels outside.</summary>
    internal static string Rings()
    {
        string[] parts = ["a", "b", "c", "d", "e"];
        var rings = string.Concat(Enumerable.Range(0, 3).Select(i => Series(i, $"Ring {i}", parts, [.. parts.Select((_, k) => (double?)(k + i + 1))],
            "<c:explosion val=\"12\"/>" + Labels)));
        return Chart(Doughnut(rings, 35), title: "Rings", legend: "tr");
    }

    /// <summary>Two scatter series, smooth with markers and markers only, on a logarithmic X axis.</summary>
    internal static string Points() =>
        Chart(Scatter(ScatterSeries(0, "Smooth", [1, 10, 100, 1000], [2, 3, 1, 4], "<c:marker><c:symbol val=\"x\"/></c:marker><c:smooth val=\"1\"/>") +
                      ScatterSeries(1, "Dots", [5, 50, 500], [1, 2, 3], "<c:spPr><a:ln><a:noFill/></a:ln></c:spPr>"), "smoothMarker"),
            Axes(scatter: true).Replace("<c:orientation val=\"minMax\"/></c:scaling>", "<c:logBase val=\"10\"/><c:orientation val=\"minMax\"/></c:scaling>", StringComparison.Ordinal),
            "Points", legend: "l");

    /// <summary>Trendlines, error bars, a point's own label, two category levels, a rich title and gradient and pattern fills.</summary>
    internal static string Polished()
    {
        var levels = MultiLevel(["Q1", "Q2", "Q1", "Q2"], [(0, "2025"), (2, "2026")]);
        var labels = "<c:dLbls><c:dLbl><c:idx val=\"1\"/><c:tx><c:rich><a:bodyPr/><a:p><a:r><a:t>Own</a:t></a:r></a:p></c:rich></c:tx><c:showVal val=\"1\"/></c:dLbl>" +
                     "<c:dLbl><c:idx val=\"2\"/><c:delete val=\"1\"/></c:dLbl><c:showVal val=\"1\"/><c:separator>; </c:separator></c:dLbls>";
        var first = Series(0, "Up", [], [1, 4, 9, 16], Gradient("FF0000", "0000FF") + labels + Trendline("poly", "<c:order val=\"3\"/><c:forward val=\"2\"/><c:dispEq val=\"1\"/><c:dispRSqr val=\"1\"/>") +
                                                       Trendline("movingAvg", "<c:period val=\"2\"/>") + ErrorBars("percentage", 10) + levels);
        var second = Series(1, "Down", [], [5, 3, 4, 2], Pattern("dkDnDiag", "00B050") + Trendline("exp", "<c:dispEq val=\"1\"/>") +
                                                         ErrorBars("cust", extra: Literal("plus", 1, 2) + Literal("minus", 0.5)) + levels);
        var line = Series(2, "Line", [], [3, 3, 5, 6], LineOf("7030A0") + Trendline("log") + ErrorBars("stdErr", type: "minus") + levels);
        return Chart(Bars(first + second) + Lines(line), Axes(), "x", legend: "b").Replace(Title("x"), RichTitle("Rich ", "title", "2"), StringComparison.Ordinal);
    }
}
