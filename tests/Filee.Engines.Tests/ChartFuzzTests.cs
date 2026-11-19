// Mutation fuzzing of DrawingML charts (Charts/): the seeds are ChartTests' column chart, a pie with labels and a
// chart with what costs the reader and the drawer the most (several plots on two pairs of axes, stacked bars, smooth
// lines with markers and data labels, a doughnut's rings, scatter, a log axis, manual layouts, turned labels, dates),
// written in code, as a chart part alone (XmlFuzzer), inside a workbook the sheet printer prints and inside a chart
// template (.crtx) the drawing engine draws (DocumentFuzzer).
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
    ];

    [Fact]
    public void ChartReader_reads_or_refuses_every_mutated_chart_part() =>
        new FuzzHarness(fx, QuickIterations, 20261120).Data("chart", "xml",
            [.. new[] { Columns(), Slices(Labels), Costly(), Rings(), Points() }.Select(Encoding.UTF8.GetBytes)], FuzzHarness.Xml(Tokens), DrawPart);

    [Fact]
    public void A_workbook_with_charts_is_read_or_refused_whatever_its_parts_say() =>
        new FuzzHarness(fx, QuickIterations, 20261120).Files("chart-xlsx", "xlsx", [Workbook(Columns()), Workbook(Costly()), Workbook(Rings())], FuzzHarness.Packages, Print);

    [Fact]
    public void A_chart_template_is_drawn_or_refused_whatever_its_parts_say() =>
        new FuzzHarness(fx, QuickIterations, 20261120).Files("chart-crtx", "crtx",
            [Template(Columns()), Template(Chart(Bars(""), Axes())), Template(Rings())], FuzzHarness.Packages, (path, _) =>
            {
                using var template = ChartTemplate.Open(path);
                template.Page(0).Render(12).Dispose();
            });

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
}
