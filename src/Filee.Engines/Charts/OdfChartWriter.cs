// The chart model → an OpenDocument chart object's content.xml (OpenDocument 1.3 Part 3, 11), so a chart read from a
// PowerPoint or OpenDocument deck stays a chart in the ODP writer's output (a draw:object beside the picture of it).
// OdfChartReader reads it back into the same model.
//
// Written: the plots' kinds (bar and column, line, area, pie, doughnut, scatter, radar, bubble) with a series' own
// chart:class in a combination and its axis (primary or secondary y), stacking and percent stacking, horizontal bars,
// 3D (chart:three-dimensional, chart:deep), markers and smooth lines, the pie's first slice (chart:angle-offset); the
// title, axis titles, axes (minimum, maximum, major interval, logarithmic, reversed, hidden, gridlines), the legend's
// position, data labels (value, percent, category, series); the colours of series and points as drawn (Office's or
// LibreOffice's palette resolved), the chart and plot area fills; the values in the chart's own table, a column per
// series (X and sizes beside them for scatter and bubble series). Trendlines, error bars, number formats, the 3D view's
// angles and gradients are left out (the picture beside the object shows them as drawn).
//
// Kinds the ODF chart has no counterpart for (surface, bar or pie of pie, the Office 2016 kinds) are not written:
// such a chart stays its picture.

using System.Globalization;
using System.Text;
using Filee.Engines.Odf;
using SkiaSharp;
using OdfNames = Filee.Engines.Hwp.Hwpx.Odt.Odf;

namespace Filee.Engines.Charts;

internal static class OdfChartWriter
{
    /// <summary>The namespaces of a chart object's content.xml.</summary>
    private const string Namespaces =
        OdfNames.Namespaces + " xmlns:chart=\"urn:oasis:names:tc:opendocument:xmlns:chart:1.0\" xmlns:dr3d=\"urn:oasis:names:tc:opendocument:xmlns:dr3d:1.0\"";

    /// <summary>A chart object's media type (its folder's manifest entry).</summary>
    public const string MediaType = "application/vnd.oasis.opendocument.chart";

    /// <summary>The name of the chart's own table, as LibreOffice names it.</summary>
    private const string Table = "local-table";

    /// <summary>Whether the chart can be written as an ODF chart: every plot of a kind ODF charts have.</summary>
    public static bool CanWrite(Chart chart) =>
        chart.Plots.Count > 0 && chart.Plots.All(p => p.Kind is ChartKind.Bar or ChartKind.Line or ChartKind.Area or ChartKind.Pie or ChartKind.Doughnut
            or ChartKind.Scatter or ChartKind.Radar or ChartKind.Bubble) && chart.Plots.Any(p => p.Series.Count > 0);

    /// <summary>The content.xml of the chart object, <paramref name="width"/> × <paramref name="height"/> points; null when it can't be written.</summary>
    public static string? Content(Chart chart, float width, float height)
    {
        if (!CanWrite(chart))
            return null;
        var styles = new StringBuilder();
        var count = 0;
        string Style(string properties)
        {
            var name = "ch" + (++count).ToString(CultureInfo.InvariantCulture);
            styles.Append($"<style:style style:name=\"{name}\" style:family=\"chart\">{properties}</style:style>");
            return name;
        }

        var first = chart.Plots[0];
        var series = chart.Plots.SelectMany(p => p.Series.Select(s => (Plot: p, Series: s))).ToList();
        var scatter = chart.Plots.Any(p => p.Kind is ChartKind.Scatter or ChartKind.Bubble);
        var pie = chart.Plots.All(p => p.Kind is ChartKind.Pie or ChartKind.Doughnut);

        // The table: a header row of names; per row a category, then each series' columns.
        var categories = series.Select(s => s.Series.Categories).FirstOrDefault(c => c is { Count: > 0 });
        var rows = Math.Max(series.Max(s => s.Series.Values.Count), categories?.Count ?? 0);
        var columns = new List<(string Header, Func<int, double?>? Value, Func<int, string?>? Text)>
        {
            ("", null, i => categories is not null && i < categories.Count ? categories[i] : (i + 1).ToString(CultureInfo.InvariantCulture)),
        };
        var addresses = new List<(int Values, int? X, int? Y, int Label)>();
        foreach (var (plot, s) in series)
        {
            int? x = null, y = null;
            if (plot.Kind == ChartKind.Scatter && s.XValues is { } xs)
            {
                columns.Add(($"{s.Name} X", i => i < xs.Count ? xs[i] : null, null));
                x = columns.Count - 1;
            }
            if (plot.Kind == ChartKind.Bubble)
            {
                var xs2 = s.XValues;
                columns.Add(($"{s.Name} X", i => xs2 is not null && i < xs2.Count ? xs2[i] : i + 1, null));
                x = columns.Count - 1;
                columns.Add(($"{s.Name} Y", i => i < s.Values.Count ? s.Values[i] : null, null));
                y = columns.Count - 1;
                var sizes = s.BubbleSizes ?? [];
                columns.Add((s.Name ?? "", i => i < sizes.Count ? sizes[i] : null, null));
            }
            else
            {
                columns.Add((s.Name ?? "", i => i < s.Values.Count ? s.Values[i] : null, null));
            }
            addresses.Add((columns.Count - 1, x, y, columns.Count - 1));
        }

        string Cell(int column, int row) => $".${Letters(column)}${(row + 1).ToString(CultureInfo.InvariantCulture)}";
        string Range(int column) => $"{Table}{Cell(column, 1)}:{Cell(column, Math.Max(1, rows))}";

        var body = new StringBuilder();
        var area = Style($"<style:graphic-properties{Fill(chart.Area, SKColors.White)}{Stroke(chart.Area.Line, hidden: true)}/>" + Text(chart.Text));
        body.Append($"<chart:chart svg:width=\"{Pt(width)}\" svg:height=\"{Pt(height)}\" chart:class=\"{Class(first)}\" chart:style-name=\"{area}\">");
        if (chart.Title is { Lines.Count: > 0 } title)
        {
            var titleStyle = Style(Text(title.Style));
            body.Append($"<chart:title chart:style-name=\"{titleStyle}\">");
            foreach (var line in title.Lines)
                body.Append("<text:p>").Append(OdfXml.Escape(line)).Append("</text:p>");
            body.Append("</chart:title>");
        }
        if (chart.Legend is { } legend)
        {
            var position = legend.Position switch { "l" => "start", "t" => "top", "b" => "bottom", "tr" => "top-end", _ => "end" };
            body.Append($"<chart:legend chart:legend-position=\"{position}\" chart:style-name=\"{Style(Text(legend.Style))}\"/>");
        }

        var plotProperties = new StringBuilder();
        if (first.Horizontal)
            plotProperties.Append(" chart:vertical=\"true\"");
        if (first.Grouping == ChartGrouping.Stacked)
            plotProperties.Append(" chart:stacked=\"true\"");
        else if (first.Grouping == ChartGrouping.PercentStacked)
            plotProperties.Append(" chart:percentage=\"true\"");
        if (first.ThreeD)
            plotProperties.Append(" chart:three-dimensional=\"true\"").Append(first.SeriesInDepth ? " chart:deep=\"true\"" : "");
        if (first.Kind is ChartKind.Line or ChartKind.Scatter or ChartKind.Radar)
            plotProperties.Append(first.Markers ? " chart:symbol-type=\"automatic\"" : " chart:symbol-type=\"none\"");
        if (first.Kind == ChartKind.Scatter && !first.Lines)
            plotProperties.Append(" chart:lines=\"false\"");
        if (first.Kind is ChartKind.Pie or ChartKind.Doughnut)
            plotProperties.Append($" chart:angle-offset=\"{Number(((90 - first.FirstSliceAngle) % 360 + 360) % 360)}\"");
        if (first.Kind == ChartKind.Bar)
            plotProperties.Append($" chart:gap-width=\"{first.GapWidth.ToString(CultureInfo.InvariantCulture)}\" chart:overlap=\"{first.Overlap.ToString(CultureInfo.InvariantCulture)}\"");
        plotProperties.Append(LabelProperties(first.Labels));
        var plotStyle = Style($"<style:chart-properties chart:series-source=\"columns\"{plotProperties}/>");
        body.Append($"<chart:plot-area chart:style-name=\"{plotStyle}\" table:cell-range-address=\"{Table}.$A$1:.${Letters(columns.Count - 1)}${(rows + 1).ToString(CultureInfo.InvariantCulture)}\" chart:data-source-has-labels=\"both\">");

        if (!pie)
        {
            var categoryAxis = chart.Axis(first.AxisIds.ElementAtOrDefault(0));
            var valueAxis = chart.Axis(first.AxisIds.ElementAtOrDefault(1));
            body.Append(Axis("x", "primary-x", categoryAxis, scatter ? null : Range(0), Style));
            body.Append(Axis("y", "primary-y", valueAxis, null, Style));
            if (chart.Plots.FirstOrDefault(p => Secondary(first, p)) is { } second)
                body.Append(Axis("y", "secondary-y", chart.Axis(second.AxisIds[1]), null, Style));
        }
        else
        {
            // A pie's categories go along its x axis too.
            body.Append($"<chart:axis chart:dimension=\"x\" chart:name=\"primary-x\"><chart:categories table:cell-range-address=\"{Range(0)}\"/></chart:axis>");
        }

        for (var i = 0; i < series.Count; i++)
        {
            var (plot, s) = series[i];
            var (values, x, y, label) = addresses[i];
            var color = s.Style.Fill is { } own && own != SKColors.Transparent ? own : chart.Palette.Color(s.Index);
            var properties = new StringBuilder();
            if (plot.Kind is ChartKind.Line or ChartKind.Scatter or ChartKind.Radar)
            {
                var marker = s.Marker ?? (plot.Markers ? new ChartMarker("auto", 5, ChartShapeStyle.Auto) : null);
                properties.Append(marker is null || marker.Symbol == "none" ? " chart:symbol-type=\"none\"" : " chart:symbol-type=\"automatic\"");
            }
            if (s.Smooth)
                properties.Append(" chart:interpolation=\"cubic-spline\"");
            properties.Append(LabelProperties(s.Labels));
            var line = plot.Kind is ChartKind.Line or ChartKind.Scatter or ChartKind.Radar && !plot.Filled
                ? (plot.Kind == ChartKind.Scatter && !plot.Lines ? " draw:stroke=\"none\"" : Stroke(s.Style.Line ?? new ChartLine(color, 2), hidden: false, color))
                : Stroke(s.Style.Line, hidden: false);
            var graphic = plot.Kind is ChartKind.Pie or ChartKind.Doughnut ? "" : $"<style:graphic-properties{Fill(s.Style, color)}{line}/>";
            var seriesStyle = Style($"<style:chart-properties{properties}/>{graphic}");
            body.Append($"<chart:series chart:style-name=\"{seriesStyle}\" chart:values-cell-range-address=\"{Range(values)}\" chart:label-cell-address=\"{Table}{Cell(label, 0)}\" chart:class=\"{Class(plot)}\"");
            if (Secondary(first, plot))
                body.Append(" chart:attached-axis=\"secondary-y\"");
            body.Append('>');
            if (y is { } yColumn)
                body.Append($"<chart:domain table:cell-range-address=\"{Range(yColumn)}\"/>");
            if (x is { } xColumn)
                body.Append($"<chart:domain table:cell-range-address=\"{Range(xColumn)}\"/>");
            // Points: their own fills, a pie's slices each in its colour.
            var points = s.Values.Count;
            if (plot.VaryColors || s.Points.Count > 0 || s.Explosion.Count > 0)
            {
                for (var p = 0; p < points; p++)
                {
                    var style = s.Points.GetValueOrDefault(p);
                    var pointColor = style?.Fill is { } f && f != SKColors.Transparent ? f : plot.VaryColors ? chart.Palette.Color(p) : color;
                    var offset = s.Explosion.TryGetValue(p, out var e) || s.Explosion.TryGetValue(-1, out e) ? $"<style:chart-properties chart:pie-offset=\"{e.ToString(CultureInfo.InvariantCulture)}\"/>" : "";
                    var pointStyle = Style($"{offset}<style:graphic-properties{Fill(style ?? ChartShapeStyle.Auto, pointColor)}{Stroke(style?.Line ?? s.Style.Line, hidden: false)}/>");
                    body.Append($"<chart:data-point chart:style-name=\"{pointStyle}\"/>");
                }
            }
            else if (points > 0)
            {
                body.Append($"<chart:data-point chart:repeated=\"{points.ToString(CultureInfo.InvariantCulture)}\"/>");
            }
            body.Append("</chart:series>");
        }
        var wall = Style($"<style:graphic-properties{Fill(chart.PlotArea, SKColors.Transparent)}{Stroke(chart.PlotArea.Line, hidden: true)}/>");
        body.Append($"<chart:wall chart:style-name=\"{wall}\"/></chart:plot-area>");

        // The chart's own table.
        body.Append($"<table:table table:name=\"{Table}\"><table:table-header-columns><table:table-column/></table:table-header-columns>");
        body.Append($"<table:table-columns><table:table-column table:number-columns-repeated=\"{Math.Max(1, columns.Count - 1).ToString(CultureInfo.InvariantCulture)}\"/></table:table-columns>");
        body.Append("<table:table-header-rows><table:table-row>");
        foreach (var (header, _, _) in columns)
            body.Append(header.Length == 0 ? "<table:table-cell/>" : $"<table:table-cell office:value-type=\"string\"><text:p>{OdfXml.Escape(header)}</text:p></table:table-cell>");
        body.Append("</table:table-row></table:table-header-rows><table:table-rows>");
        for (var row = 0; row < rows; row++)
        {
            body.Append("<table:table-row>");
            foreach (var (_, value, text) in columns)
            {
                if (text is not null)
                {
                    body.Append($"<table:table-cell office:value-type=\"string\"><text:p>{OdfXml.Escape(text(row) ?? "")}</text:p></table:table-cell>");
                }
                else if (value?.Invoke(row) is { } number && double.IsFinite(number))
                {
                    var written = number.ToString("R", CultureInfo.InvariantCulture);
                    body.Append($"<table:table-cell office:value-type=\"float\" office:value=\"{written}\"><text:p>{written}</text:p></table:table-cell>");
                }
                else
                {
                    body.Append("<table:table-cell/>");
                }
            }
            body.Append("</table:table-row>");
        }
        body.Append("</table:table-rows></table:table></chart:chart>");

        return OdfPackageWriter.Xml($"<office:document-content {Namespaces} office:version=\"1.3\"><office:automatic-styles>{styles}</office:automatic-styles>" +
                                    $"<office:body><office:chart>{body}</office:chart></office:body></office:document-content>");
    }

    /// <summary>An empty styles.xml for the chart object.</summary>
    public static string Styles() => OdfPackageWriter.Xml($"<office:document-styles {Namespaces} office:version=\"1.3\"><office:styles/></office:document-styles>");

    /// <summary>A plot drawn against another value axis than the first plot's: the secondary y axis.</summary>
    private static bool Secondary(ChartPlot first, ChartPlot plot) =>
        plot != first && plot.AxisIds.Count > 1 && first.AxisIds.Count > 1 && plot.AxisIds[1] != first.AxisIds[1]
        && plot.Kind is not (ChartKind.Pie or ChartKind.Doughnut);

    private static string Class(ChartPlot plot) => plot.Kind switch
    {
        ChartKind.Line => "chart:line",
        ChartKind.Area => "chart:area",
        ChartKind.Pie => "chart:circle",
        ChartKind.Doughnut => "chart:ring",
        ChartKind.Scatter => "chart:scatter",
        ChartKind.Radar => plot.Filled ? "chart:filled-radar" : "chart:radar",
        ChartKind.Bubble => "chart:bubble",
        _ => "chart:bar",
    };

    private static string Axis(string dimension, string name, ChartAxis? axis, string? categories, Func<string, string> style)
    {
        var properties = new StringBuilder();
        properties.Append(axis is null || axis.Deleted ? " chart:visible=\"false\"" : " chart:visible=\"true\"");
        properties.Append(axis?.LabelPosition == "none" ? " chart:display-label=\"false\"" : " chart:display-label=\"true\"");
        if (axis?.Min is { } min)
            properties.Append($" chart:minimum=\"{Number(min)}\"");
        if (axis?.Max is { } max)
            properties.Append($" chart:maximum=\"{Number(max)}\"");
        if (axis?.MajorUnit is { } unit)
            properties.Append($" chart:interval-major=\"{Number(unit)}\"");
        if (axis?.LogBase is not null)
            properties.Append(" chart:logarithmic=\"true\"");
        if (axis?.Reversed == true)
            properties.Append(" chart:reverse-direction=\"true\"");
        var sb = new StringBuilder($"<chart:axis chart:dimension=\"{dimension}\" chart:name=\"{name}\" chart:style-name=\"{style($"<style:chart-properties{properties}/>" + (axis is null ? "" : Text(axis.Style)))}\">");
        if (axis?.Title is { Lines.Count: > 0 } title)
        {
            sb.Append($"<chart:title chart:style-name=\"{style(Text(title.Style))}\">");
            foreach (var line in title.Lines)
                sb.Append("<text:p>").Append(OdfXml.Escape(line)).Append("</text:p>");
            sb.Append("</chart:title>");
        }
        if (categories is not null)
            sb.Append($"<chart:categories table:cell-range-address=\"{categories}\"/>");
        if (axis?.MajorGridlines is { None: false })
            sb.Append("<chart:grid chart:class=\"major\"/>");
        if (axis?.MinorGridlines is { None: false })
            sb.Append("<chart:grid chart:class=\"minor\"/>");
        return sb.Append("</chart:axis>").ToString();
    }

    private static string LabelProperties(ChartLabels? labels)
    {
        if (labels is not { Any: true })
            return "";
        var number = (labels.Value, labels.Percent) switch
        {
            (true, true) => "value-and-percentage",
            (true, false) => "value",
            (false, true) => "percentage",
            _ => "none",
        };
        return $" chart:data-label-number=\"{number}\" chart:data-label-text=\"{(labels.Category ? "true" : "false")}\" chart:data-label-series=\"{(labels.Series ? "true" : "false")}\"";
    }

    private static string Fill(ChartShapeStyle style, SKColor automatic)
    {
        var color = style.Fill ?? automatic;
        return color == SKColors.Transparent || color.Alpha == 0 ? " draw:fill=\"none\"" : $" draw:fill=\"solid\" draw:fill-color=\"{Hex(color)}\"";
    }

    private static string Stroke(ChartLine? line, bool hidden, SKColor? automatic = null)
    {
        if (line is null)
            return hidden ? " draw:stroke=\"none\"" : automatic is { } a ? $" draw:stroke=\"solid\" svg:stroke-color=\"{Hex(a)}\"" : "";
        if (line.None)
            return " draw:stroke=\"none\"";
        var sb = new StringBuilder(line.Dash is { Length: > 0 } ? " draw:stroke=\"dash\"" : " draw:stroke=\"solid\"");
        if ((line.Color ?? automatic) is { } color)
            sb.Append($" svg:stroke-color=\"{Hex(color)}\"");
        if (line.Width is { } width && float.IsFinite(width))
            sb.Append($" svg:stroke-width=\"{Pt(Math.Clamp(width, 0, 100))}\"");
        return sb.ToString();
    }

    private static string Text(ChartTextStyle style)
    {
        var sb = new StringBuilder($"<style:text-properties fo:font-size=\"{Pt(Math.Clamp(style.Size, 1, 400))}\" fo:color=\"{Hex(style.Color)}\"");
        if (style.Family is { Length: > 0 } family)
            sb.Append($" fo:font-family=\"{OdfXml.Escape(family)}\"");
        if (style.Bold)
            sb.Append(" fo:font-weight=\"bold\"");
        if (style.Italic)
            sb.Append(" fo:font-style=\"italic\"");
        return sb.Append("/>").ToString();
    }

    /// <summary>Column letters of a 0-based column (A, B, …, Z, AA, …).</summary>
    private static string Letters(int column)
    {
        var sb = new StringBuilder();
        for (var n = column + 1; n > 0; n = (n - 1) / 26)
            sb.Insert(0, (char)('A' + (n - 1) % 26));
        return sb.ToString();
    }

    private static string Hex(SKColor color) => $"#{color.Red:x2}{color.Green:x2}{color.Blue:x2}";

    private static string Number(double value) => (double.IsFinite(value) ? value : 0).ToString("R", CultureInfo.InvariantCulture);

    private static string Pt(double points) => (double.IsFinite(points) ? Math.Round(points, 2) : 0).ToString("0.##", CultureInfo.InvariantCulture) + "pt";
}
