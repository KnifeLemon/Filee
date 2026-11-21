// Chart parts of the kinds drawn in 3D or of their own (radar, bubble, stock, surface, bar and pie of pie, and the
// Office 2016 chartex kinds), written in code for the chart tests the way Excel saves them.

using static Filee.Engines.Tests.ChartBuilder;

namespace Filee.Engines.Tests;

internal static class ChartSamples
{
    private static readonly string[] Quarters = ["Q1", "Q2", "Q3", "Q4"];

    /// <summary>Three series of four quarters, red, green and blue.</summary>
    public static string Three() =>
        Series(0, "North", Quarters, [40, 55, 70, 62], Solid("C00000")) +
        Series(1, "South", Quarters, [30, 45, 35, 50], Solid("00B050")) +
        Series(2, "West", Quarters, [20, 25, 40, 30], Solid("0070C0"));

    public static string Columns3D(string grouping = "clustered", string direction = "col", string view = "")
    {
        var series = grouping == "standard";
        return Chart3D($"<c:bar3DChart><c:barDir val=\"{direction}\"/><c:grouping val=\"{grouping}\"/><c:varyColors val=\"0\"/>{Three()}<c:gapWidth val=\"150\"/><c:shape val=\"box\"/>" +
                       $"<c:axId val=\"1\"/><c:axId val=\"2\"/><c:axId val=\"{(series ? 3 : 0)}\"/></c:bar3DChart>",
            Axes(horizontal: direction == "bar") + (series ? SeriesAxis() : ""), $"3D {grouping} {direction}", view);
    }

    public static string Lines3D() =>
        Chart3D($"<c:line3DChart><c:grouping val=\"standard\"/><c:varyColors val=\"0\"/>{Three()}<c:gapDepth val=\"150\"/><c:axId val=\"1\"/><c:axId val=\"2\"/><c:axId val=\"3\"/></c:line3DChart>",
            Axes() + SeriesAxis(), "3D line");

    public static string Areas3D(string grouping = "standard") =>
        Chart3D($"<c:area3DChart><c:grouping val=\"{grouping}\"/><c:varyColors val=\"0\"/>{Three()}<c:axId val=\"1\"/><c:axId val=\"2\"/><c:axId val=\"3\"/></c:area3DChart>",
            Axes() + (grouping == "standard" ? SeriesAxis() : ""), $"3D area {grouping}");

    public static string Pie3D(string extra = "") =>
        Chart3D($"<c:pie3DChart><c:varyColors val=\"1\"/>{Series(0, "Budget", ["Rent", "Food", "Travel", "Other"], [40, 30, 20, 10], extra)}</c:pie3DChart>",
            title: "3D pie", view: View3D(30, 0, rightAngles: false));

    public static string Radar(string style = "marker")
    {
        string[] spokes = ["Speed", "Power", "Range", "Comfort", "Price"];
        return Chart($"<c:radarChart><c:radarStyle val=\"{style}\"/><c:varyColors val=\"0\"/>{Series(0, "North", spokes, [4, 3, 5, 2, 4]) + Series(1, "South", spokes, [2, 5, 3, 4, 3])}<c:axId val=\"1\"/><c:axId val=\"2\"/></c:radarChart>",
            Axes().Replace("<c:crossAx val=\"2\"/>", "<c:majorGridlines/><c:crossAx val=\"2\"/>", StringComparison.Ordinal), $"Radar {style}");
    }

    public static string Bubbles(bool threeD = false)
    {
        var look = threeD ? "<c:bubble3D val=\"1\"/>" : "";
        return Chart("<c:bubbleChart><c:varyColors val=\"0\"/>" +
                     BubbleSeries(0, "North", [1, 2, 3, 4], [10, 25, 18, 30], [5, 20, 10, 40], look) +
                     BubbleSeries(1, "South", [1.5, 2.5, 3.5], [20, 12, 28], [15, 8, 25], look) +
                     "<c:bubbleScale val=\"100\"/><c:showNegBubbles val=\"0\"/><c:axId val=\"1\"/><c:axId val=\"2\"/></c:bubbleChart>", Axes(scatter: true), threeD ? "3D bubbles" : "Bubbles");
    }

    private static readonly string[] Days = ["Mon", "Tue", "Wed", "Thu", "Fri"];

    private const string NoLine = "<c:spPr><a:ln w=\"19050\"><a:noFill/></a:ln></c:spPr><c:marker><c:symbol val=\"none\"/></c:marker>";

    public static string Stock(bool open = true)
    {
        var close = open ? NoLine : "<c:spPr><a:ln w=\"19050\"><a:noFill/></a:ln></c:spPr><c:marker><c:symbol val=\"dot\"/><c:size val=\"7\"/></c:marker>";
        var series = (open ? Series(0, "Open", Days, [44, 25, 38, 50, 34], NoLine) : "") +
                     Series(1, "High", Days, [55, 57, 57, 58, 58], NoLine) +
                     Series(2, "Low", Days, [11, 12, 13, 11, 25], NoLine) +
                     Series(3, "Close", Days, [25, 38, 50, 35, 43], close);
        var bars = open
            ? "<c:upDownBars><c:gapWidth val=\"150\"/><c:upBars><c:spPr><a:solidFill><a:srgbClr val=\"FFFFFF\"/></a:solidFill><a:ln><a:solidFill><a:srgbClr val=\"000000\"/></a:solidFill></a:ln></c:spPr></c:upBars>" +
              "<c:downBars><c:spPr><a:solidFill><a:srgbClr val=\"404040\"/></a:solidFill></c:spPr></c:downBars></c:upDownBars>"
            : "";
        return Chart($"<c:stockChart>{series}<c:hiLowLines><c:spPr><a:ln w=\"9525\"><a:solidFill><a:srgbClr val=\"000000\"/></a:solidFill></a:ln></c:spPr></c:hiLowLines>{bars}<c:axId val=\"1\"/><c:axId val=\"2\"/></c:stockChart>",
            Axes(), open ? "Open-high-low-close" : "High-low-close");
    }

    private static string SurfaceSeries()
    {
        string[] x = ["0", "1", "2", "3", "4", "5"];
        double?[][] rows = [[10, 20, 30, 40, 30, 20], [20, 35, 50, 60, 45, 25], [30, 50, 75, 90, 60, 30], [20, 40, 55, 65, 50, 25], [10, 20, 35, 40, 30, 15]];
        return string.Concat(rows.Select((r, i) => Series(i, $"Row {i + 1}", x, r)));
    }

    public static string Surface(bool threeD, bool wireframe = false)
    {
        var element = threeD ? "surface3DChart" : "surfaceChart";
        var plot = $"<c:{element}><c:wireframe val=\"{(wireframe ? 1 : 0)}\"/>{SurfaceSeries()}<c:axId val=\"1\"/><c:axId val=\"2\"/><c:axId val=\"3\"/></c:{element}>";
        var axes = Axes().Replace("<c:crossBetween val=\"between\"/>", "<c:crossBetween val=\"midCat\"/>", StringComparison.Ordinal) + SeriesAxis();
        var title = (threeD ? "3D surface" : "Contour") + (wireframe ? " wireframe" : "");
        return threeD ? Chart3D(plot, axes, title) : Chart(plot, axes, title).Replace("<c:plotArea>", View3D(90, 0, rightAngles: false) + "<c:plotArea>", StringComparison.Ordinal);
    }

    public static string OfPie(bool bar) =>
        Chart($"<c:ofPieChart><c:ofPieType val=\"{(bar ? "bar" : "pie")}\"/><c:varyColors val=\"1\"/>" +
              Series(0, "Sales", ["Rent", "Food", "Travel", "Books", "Music", "Games"], [40, 30, 15, 6, 5, 4],
                  "<c:dLbls><c:showLegendKey val=\"0\"/><c:showVal val=\"0\"/><c:showCatName val=\"1\"/><c:showSerName val=\"0\"/><c:showPercent val=\"1\"/><c:showBubbleSize val=\"0\"/></c:dLbls>") +
              "<c:gapWidth val=\"100\"/><c:splitType val=\"pos\"/><c:splitPos val=\"3\"/><c:secondPieSize val=\"75\"/><c:serLines/></c:ofPieChart>", title: bar ? "Bar of pie" : "Pie of pie", legend: "");

    // ───────────────────────── chartex ─────────────────────────

    private static string Layout(string series, string layout, int dataId = 0) =>
        series.Replace($"<cx:dataId val=\"{dataId}\"/>", $"<cx:dataId val=\"{dataId}\"/><cx:layoutPr>{layout}</cx:layoutPr>", StringComparison.Ordinal);

    public static string Waterfall() =>
        ChartEx(ExData(0, [["Start", "Sales", "Refunds", "Costs", "Subtotal", "Tax", "End"]], [100, 60, -15, -30, 115, -20, 95]),
            Layout(ExSeries("waterfall", 0, "Cash", "<cx:dataLabels pos=\"outEnd\"><cx:visibility seriesName=\"0\" categoryName=\"0\" value=\"1\"/></cx:dataLabels>"),
                "<cx:subtotals><cx:idx val=\"0\"/><cx:idx val=\"4\"/><cx:idx val=\"6\"/></cx:subtotals>"),
            ExAxes(), "Waterfall", "t");

    public static string Funnel() =>
        ChartEx(ExData(0, [["Visits", "Sign-ups", "Trials", "Orders", "Repeat"]], [1000, 600, 320, 150, 60]),
            ExSeries("funnel", 0, "Stages", "<cx:dataLabels pos=\"inEnd\"><cx:visibility seriesName=\"0\" categoryName=\"0\" value=\"1\"/></cx:dataLabels>"),
            "<cx:axis id=\"0\"><cx:catScaling gapWidth=\"0.06\"/><cx:tickLabels/></cx:axis>", "Funnel");

    /// <summary>Leaves (innermost level) and their parents.</summary>
    public static readonly string[][] Tree =
    [
        ["Apples", "Pears", "Plums", "Carrots", "Peas", "Leeks", "Bread", "Rolls"],
        ["Fruit", "Fruit", "Fruit", "Vegetables", "Vegetables", "Vegetables", "Bakery", "Bakery"],
    ];

    public static readonly double?[] TreeValues = [30, 20, 10, 25, 12, 8, 18, 9];

    public static string Treemap(string parents = "overlapping") =>
        ChartEx(ExData(0, Tree, TreeValues, "size"),
            Layout(ExSeries("treemap", 0, "Shop", "<cx:dataLabels pos=\"inEnd\"><cx:visibility seriesName=\"0\" categoryName=\"1\" value=\"0\"/></cx:dataLabels>"),
                $"<cx:parentLabelLayout val=\"{parents}\"/>"),
            title: "Treemap " + parents, legend: "t");

    public static string Sunburst() =>
        ChartEx(ExData(0, Tree, TreeValues, "size"),
            ExSeries("sunburst", 0, "Shop", "<cx:dataLabels pos=\"ctr\"><cx:visibility seriesName=\"0\" categoryName=\"1\" value=\"0\"/></cx:dataLabels>"),
            title: "Sunburst");

    public static readonly double?[] Measurements = [12, 15, 17, 18, 18, 19, 21, 22, 22, 23, 24, 25, 25, 26, 28, 31, 33, 36, 41, 52];

    public static string Histogram(string binning = "<cx:binning intervalClosed=\"r\"><cx:binCount val=\"5\"/></cx:binning>") =>
        ChartEx(ExData(0, [], Measurements), Layout(ExSeries("clusteredColumn", 0, "Times"), binning), ExAxes(0), "Histogram");

    public static string Pareto() =>
        ChartEx(ExData(0, [["Late", "Broken", "Wrong size", "Late", "Missing", "Broken", "Late"]], [12, 8, 5, 9, 3, 4, 6]),
            Layout(ExSeries("clusteredColumn", 0, "Complaints"), "<cx:aggregation/>") +
            "<cx:series layoutId=\"paretoLine\" ownerIdx=\"0\" uniqueId=\"{00000009-0000-0000-0000-000000000000}\"><cx:axisId val=\"2\"/></cx:series>",
            ExAxes(0.2, percent: true), "Pareto");

    private static readonly string[] Groups = ["A", "A", "A", "A", "A", "A", "A", "B", "B", "B", "B", "B", "B", "B"];

    public static string BoxWhisker() =>
        ChartEx(ExData(0, [Groups], [1, 3, 4, 5, 6, 7, 20, 10, 12, 13, 15, 16, 18, 19]) + ExData(1, [Groups], [2, 4, 6, 8, 9, 10, 11, 5, 6, 8, 9, 11, 13, 30]),
            ExSeries("boxWhisker", 0, "Class 1", "<cx:layoutPr><cx:visibility meanLine=\"1\" meanMarker=\"1\" nonoutliers=\"0\" outliers=\"1\"/><cx:statistics quartileMethod=\"exclusive\"/></cx:layoutPr>") +
            ExSeries("boxWhisker", 1, "Class 2", "<cx:layoutPr><cx:visibility meanLine=\"0\" meanMarker=\"1\" nonoutliers=\"0\" outliers=\"1\"/><cx:statistics quartileMethod=\"exclusive\"/></cx:layoutPr>"),
            ExAxes(1), "Box and whisker", "t");

    public static string RegionMap() =>
        ChartEx(ExData(0, [["France", "Spain"]], [3, 4], "colorVal"), ExSeries("regionMap", 0, "Map"), title: "Map");

    public static IEnumerable<(string Name, string Xml)> All() =>
    [
        ("01-column-3d-clustered", Columns3D()),
        ("02-column-3d-standard", Columns3D("standard")),
        ("03-bar-3d-stacked", Columns3D("stacked", "bar")),
        ("04-line-3d", Lines3D()),
        ("05-area-3d-standard", Areas3D()),
        ("06-area-3d-stacked", Areas3D("stacked")),
        ("07-pie-3d", Pie3D("<c:dPt><c:idx val=\"1\"/><c:bubble3D val=\"0\"/><c:explosion val=\"15\"/></c:dPt>" +
                            "<c:dLbls><c:showLegendKey val=\"0\"/><c:showVal val=\"0\"/><c:showCatName val=\"0\"/><c:showSerName val=\"0\"/><c:showPercent val=\"1\"/><c:showBubbleSize val=\"0\"/></c:dLbls>")),
        ("08-radar-marker", Radar()),
        ("09-radar-filled", Radar("filled")),
        ("10-bubble", Bubbles()),
        ("11-bubble-3d", Bubbles(threeD: true)),
        ("12-stock-hlc", Stock(open: false)),
        ("13-stock-ohlc", Stock()),
        ("14-surface-contour", Surface(threeD: false)),
        ("15-surface-contour-wireframe", Surface(threeD: false, wireframe: true)),
        ("16-surface-3d", Surface(threeD: true)),
        ("17-pie-of-pie", OfPie(bar: false)),
        ("18-bar-of-pie", OfPie(bar: true)),
        ("19-waterfall", Waterfall()),
        ("20-funnel", Funnel()),
        ("21-treemap", Treemap()),
        ("22-treemap-banner", Treemap("banner")),
        ("23-sunburst", Sunburst()),
        ("24-histogram", Histogram()),
        ("25-pareto", Pareto()),
        ("26-box-whisker", BoxWhisker()),
        ("27-region-map", RegionMap()),
    ];
}
