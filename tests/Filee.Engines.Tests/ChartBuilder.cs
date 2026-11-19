// DrawingML chart parts (c:chartSpace) written in code for the chart tests: plots of series with cached categories and
// values, two axes, a title and a legend, in the shape Excel, Word and PowerPoint save them.

using System.Globalization;
using System.Text;

namespace Filee.Engines.Tests;

internal static class ChartBuilder
{
    public const string Namespaces =
        "xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";

    public const string ContentType = "application/vnd.openxmlformats-officedocument.drawingml.chart+xml";

    public const string GraphicUri = "http://schemas.openxmlformats.org/drawingml/2006/chart";

    private static string N(double value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Solid(string rgb) => $"<c:spPr><a:solidFill><a:srgbClr val=\"{rgb}\"/></a:solidFill></c:spPr>";

    /// <summary>A series: name, categories (text) and values (null = blank), with more elements (c:spPr, c:dLbls, ...).</summary>
    public static string Series(int index, string name, string[] categories, double?[] values, string extra = "", string valueFormat = "General")
    {
        var sb = new StringBuilder();
        sb.Append($"<c:ser><c:idx val=\"{index}\"/><c:order val=\"{index}\"/>");
        sb.Append($"<c:tx><c:strRef><c:f>Sheet1!$B${index + 1}</c:f><c:strCache><c:ptCount val=\"1\"/><c:pt idx=\"0\"><c:v>{System.Security.SecurityElement.Escape(name)}</c:v></c:pt></c:strCache></c:strRef></c:tx>");
        sb.Append(extra);
        if (categories.Length > 0)
        {
            sb.Append($"<c:cat><c:strRef><c:f>Sheet1!$A$2:$A${categories.Length + 1}</c:f><c:strCache><c:ptCount val=\"{categories.Length}\"/>");
            for (var i = 0; i < categories.Length; i++)
                sb.Append($"<c:pt idx=\"{i}\"><c:v>{System.Security.SecurityElement.Escape(categories[i])}</c:v></c:pt>");
            sb.Append("</c:strCache></c:strRef></c:cat>");
        }
        sb.Append($"<c:val><c:numRef><c:f>Sheet1!$B$2:$B${values.Length + 1}</c:f><c:numCache><c:formatCode>{valueFormat}</c:formatCode><c:ptCount val=\"{values.Length}\"/>");
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] is { } v)
                sb.Append($"<c:pt idx=\"{i}\"><c:v>{N(v)}</c:v></c:pt>");
        }
        sb.Append("</c:numCache></c:numRef></c:val></c:ser>");
        return sb.ToString();
    }

    /// <summary>A scatter series: X and Y numbers.</summary>
    public static string ScatterSeries(int index, string name, double[] x, double[] y, string extra = "")
    {
        static string Cache(double[] values) =>
            $"<c:numRef><c:f>x</c:f><c:numCache><c:formatCode>General</c:formatCode><c:ptCount val=\"{values.Length}\"/>" +
            string.Concat(values.Select((v, i) => $"<c:pt idx=\"{i}\"><c:v>{N(v)}</c:v></c:pt>")) + "</c:numCache></c:numRef>";
        return $"<c:ser><c:idx val=\"{index}\"/><c:order val=\"{index}\"/><c:tx><c:v>{name}</c:v></c:tx>{extra}<c:xVal>{Cache(x)}</c:xVal><c:yVal>{Cache(y)}</c:yVal></c:ser>";
    }

    /// <summary>A bar or column plot (c:barChart) against axes 1 and 2.</summary>
    public static string Bars(string series, string direction = "col", string grouping = "clustered", string extra = "") =>
        $"<c:barChart><c:barDir val=\"{direction}\"/><c:grouping val=\"{grouping}\"/><c:varyColors val=\"0\"/>{series}{extra}<c:gapWidth val=\"150\"/>" +
        (grouping == "clustered" ? "" : "<c:overlap val=\"100\"/>") + "<c:axId val=\"1\"/><c:axId val=\"2\"/></c:barChart>";

    public static string Lines(string series, string grouping = "standard", bool markers = true) =>
        $"<c:lineChart><c:grouping val=\"{grouping}\"/><c:varyColors val=\"0\"/>{series}<c:marker val=\"{(markers ? 1 : 0)}\"/><c:axId val=\"1\"/><c:axId val=\"2\"/></c:lineChart>";

    public static string Areas(string series, string grouping = "standard") =>
        $"<c:areaChart><c:grouping val=\"{grouping}\"/><c:varyColors val=\"0\"/>{series}<c:axId val=\"1\"/><c:axId val=\"2\"/></c:areaChart>";

    public static string Pie(string series, string extra = "") =>
        $"<c:pieChart><c:varyColors val=\"1\"/>{series}{extra}<c:firstSliceAng val=\"0\"/></c:pieChart>";

    public static string Doughnut(string series, int hole = 50) =>
        $"<c:doughnutChart><c:varyColors val=\"1\"/>{series}<c:firstSliceAng val=\"0\"/><c:holeSize val=\"{hole}\"/></c:doughnutChart>";

    public static string Scatter(string series, string style = "lineMarker") =>
        $"<c:scatterChart><c:scatterStyle val=\"{style}\"/><c:varyColors val=\"0\"/>{series}<c:axId val=\"1\"/><c:axId val=\"2\"/></c:scatterChart>";

    /// <summary>A category axis (id 1) and a value axis (id 2) with gridlines; value and X axes for scatter plots.</summary>
    public static string Axes(bool horizontal = false, bool scatter = false, string valueExtra = "", string categoryExtra = "", string categoryTitle = "", string valueTitle = "") =>
        (scatter ? "<c:valAx>" : "<c:catAx>") + "<c:axId val=\"1\"/><c:scaling><c:orientation val=\"minMax\"/></c:scaling><c:delete val=\"0\"/>" +
        $"<c:axPos val=\"{(horizontal ? "l" : "b")}\"/>{Title(categoryTitle)}<c:numFmt formatCode=\"General\" sourceLinked=\"1\"/><c:majorTickMark val=\"out\"/><c:minorTickMark val=\"none\"/>" +
        $"<c:tickLblPos val=\"nextTo\"/>{categoryExtra}<c:crossAx val=\"2\"/><c:crosses val=\"autoZero\"/>" + (scatter ? "<c:crossBetween val=\"midCat\"/></c:valAx>" : "<c:auto val=\"1\"/><c:lblAlgn val=\"ctr\"/><c:lblOffset val=\"100\"/></c:catAx>") +
        "<c:valAx><c:axId val=\"2\"/><c:scaling><c:orientation val=\"minMax\"/></c:scaling><c:delete val=\"0\"/>" +
        $"<c:axPos val=\"{(horizontal ? "b" : "l")}\"/><c:majorGridlines/>{Title(valueTitle)}<c:numFmt formatCode=\"General\" sourceLinked=\"1\"/><c:majorTickMark val=\"out\"/><c:minorTickMark val=\"none\"/>" +
        $"<c:tickLblPos val=\"nextTo\"/>{valueExtra}<c:crossAx val=\"1\"/><c:crosses val=\"autoZero\"/><c:crossBetween val=\"between\"/></c:valAx>";

    public static string Title(string text) =>
        text.Length == 0 ? "" : $"<c:title><c:tx><c:rich><a:bodyPr/><a:p><a:r><a:t>{System.Security.SecurityElement.Escape(text)}</a:t></a:r></a:p></c:rich></c:tx><c:overlay val=\"0\"/></c:title>";

    /// <summary>A whole chart part.</summary>
    public static string Chart(string plots, string axes = "", string title = "", string legend = "r", string spPr = "", string extra = "") =>
        $"<c:chartSpace {Namespaces}><c:roundedCorners val=\"0\"/><c:chart>" +
        (title.Length > 0 ? Title(title) + "<c:autoTitleDeleted val=\"0\"/>" : "<c:autoTitleDeleted val=\"1\"/>") +
        $"<c:plotArea><c:layout/>{plots}{axes}</c:plotArea>" +
        (legend.Length > 0 ? $"<c:legend><c:legendPos val=\"{legend}\"/><c:overlay val=\"0\"/></c:legend>" : "") +
        $"<c:plotVisOnly val=\"1\"/><c:dispBlanksAs val=\"gap\"/>{extra}</c:chart>{spPr}</c:chartSpace>";

    /// <summary>A Word run with a chart (wp:inline around c:chart) of <paramref name="cx"/> × <paramref name="cy"/> EMU.</summary>
    public static string InlineChart(string relationshipId, long cx, long cy) =>
        $"<w:r><w:drawing><wp:inline><wp:extent cx=\"{cx}\" cy=\"{cy}\"/><wp:docPr id=\"3\" name=\"Chart 1\"/><a:graphic><a:graphicData uri=\"{GraphicUri}\">" +
        $"<c:chart xmlns:c=\"{GraphicUri}\" r:id=\"{relationshipId}\"/></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>";

    /// <summary>Three series of four quarters as a clustered column chart, red, green and blue.</summary>
    public static string Columns(string grouping = "clustered", string direction = "col", string title = "Sales by quarter")
    {
        string[] quarters = ["Q1", "Q2", "Q3", "Q4"];
        var series = Series(0, "North", quarters, [40, 55, 70, 62], Solid("C00000")) +
                     Series(1, "South", quarters, [30, 45, 35, 50], Solid("00B050")) +
                     Series(2, "West", quarters, [20, 25, 40, 30], Solid("0070C0"));
        return Chart(Bars(series, direction, grouping), Axes(horizontal: direction == "bar", valueTitle: "Units", categoryTitle: "Quarter"), title);
    }

    /// <summary>A pie of four slices in red, green, blue and yellow (c:dPt fills).</summary>
    public static string Slices(string labels = "")
    {
        string[] names = ["Rent", "Food", "Travel", "Other"];
        var points = string.Concat(new[] { "FF0000", "00C000", "0000FF", "FFC000" }.Select((rgb, i) =>
            $"<c:dPt><c:idx val=\"{i}\"/><c:bubble3D val=\"0\"/><c:spPr><a:solidFill><a:srgbClr val=\"{rgb}\"/></a:solidFill></c:spPr></c:dPt>"));
        return Chart(Pie(Series(0, "Budget", names, [40, 30, 20, 10], points + labels)), title: "Budget");
    }
}
