// An OpenDocument chart (an embedded object whose content is office:chart, ODF 1.3 Part 3 §11) → the chart model
// ChartDrawer draws, so ODT, ODP and ODG charts are drawn the way Word and PowerPoint charts are. The values come from
// the chart's own table (the table:table inside chart:chart, which LibreOffice writes with every chart, its "local
// table"): a series' cell range address is looked up there; a range into another table (a Calc chart's sheet) is
// looked up in the host's sheets when the host hands them over (OdsReader does), else it falls back to the local
// table's column (or row) of that series. No formula is evaluated.
//
// Read: bar and column (stacked, percent, horizontal with chart:vertical), line (symbols, cubic or B-spline curves),
// area, pie (circle), doughnut (ring) and scatter (X from chart:domain) plots, a series' own chart:class in a
// combination and its attached axis (primary or secondary Y); the title and subtitle, axis titles, axes (visible,
// labels shown, logarithmic, minimum, maximum, major interval, reversed, tick marks, label angle, gridlines), the
// legend's position, data labels (value, percent, category, series, position), fills and strokes of the chart area,
// the wall (plot area), series and data points (chart:repeated runs): solid, gradient (draw:gradient) and hatch
// (draw:hatch) fills; pie offsets, the pie's start angle (chart:angle-offset) and direction (anticlockwise unless the
// angle axis is reversed), trendlines (chart:regression-curve, with the equation and R²), error bars
// (chart:error-indicator, or the series' own error properties) and text properties. Series without a fill take
// LibreOffice's default palette. Radar charts (lines or filled) and 3D bar, line, area, pie and surface charts are
// drawn too (chart:three-dimensional, chart:deep), seen from where the plot area's dr3d:transform turns them (its
// rotation about the vertical axis and its tilt), with chart:right-angled-axes; bubble charts (the values are the
// bubbles' sizes, the first chart:domain the Y values, the second the X values, carried over to series that leave
// them out), stock charts (three series low, high, close or four open, low, high, close: chart:stock-range-line
// joins low and high, open and close are ticks, or candlesticks with chart:japanese-candle-stick and the
// chart:stock-gain-marker and chart:stock-loss-marker styles; a volume series of class chart:bar beside them; one
// legend entry for the prices (Calc keeps them as one series): the close series' name keyed by the candlestick or the
// range line),
// surface charts (one series of a block of rows and columns, or a series a row; the z axis' range and interval make
// the bands, the first series' data points fill them, no fill draws the mesh) and Gantt charts (series in pairs of
// start and end: a stacked bar whose first part is left clear, coloured as the second series when it ends before it
// starts).
//
// Hostile input: the object comes through its package's XML limits; series per chart and points per chart are bounded
// as in ChartReader (refused with error.document_too_complex), the local table's cells (repeats counted before they
// are expanded; refused with error.document_too_large), style chains and texts too, and values are clamped to ±ChartReader.MaxValue.

using System.Globalization;
using System.Xml.Linq;
using Filee.Engines.Documents;
using SkiaSharp;
using OdfNames = Filee.Engines.Hwp.Hwpx.Odt.Odf;

namespace Filee.Engines.Charts;

internal sealed class OdfChartReader
{
    private static readonly XNamespace Office = OdfNames.Office, Style = OdfNames.Style, Text = OdfNames.Text, Table = OdfNames.Table,
        Draw = OdfNames.Draw, Fo = OdfNames.Fo, Svg = OdfNames.Svg, C = OdfNames.Chart;

    /// <summary>Cells of a chart's own table at most (repeated cells counted): four full columns of points.</summary>
    internal const long MaxCells = 4L * ChartReader.MaxPoints;

    /// <summary>Parent styles followed at most.</summary>
    private const int MaxStyleDepth = 16;

    /// <summary>LibreOffice's default chart colours, for series and points without a fill of their own.</summary>
    private static readonly ChartPalette Palette = new(
    [
        new SKColor(0x00, 0x45, 0x86), new SKColor(0xFF, 0x42, 0x0E), new SKColor(0xFF, 0xD3, 0x20), new SKColor(0x57, 0x9D, 0x1C),
        new SKColor(0x7E, 0x00, 0x21), new SKColor(0x83, 0xCA, 0xFF), new SKColor(0x31, 0x40, 0x04), new SKColor(0xAE, 0xCF, 0x00),
        new SKColor(0x4B, 0x1F, 0x6F), new SKColor(0xFF, 0x95, 0x0E), new SKColor(0xC5, 0x00, 0x0B), new SKColor(0x00, 0x84, 0xD1),
    ]);

    private static readonly Dictionary<string, ChartKind> Kinds = new(StringComparer.Ordinal)
    {
        ["chart:bar"] = ChartKind.Bar,
        ["chart:line"] = ChartKind.Line,
        ["chart:area"] = ChartKind.Area,
        ["chart:circle"] = ChartKind.Pie,
        ["chart:ring"] = ChartKind.Doughnut,
        ["chart:scatter"] = ChartKind.Scatter,
        ["chart:radar"] = ChartKind.Radar,
        ["chart:filled-radar"] = ChartKind.Radar,
        ["chart:bubble"] = ChartKind.Bubble,
        ["chart:stock"] = ChartKind.Line,
        ["chart:surface"] = ChartKind.Surface,
        ["chart:gantt"] = ChartKind.Bar,
    };

    private readonly Dictionary<string, XElement> _styles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> _gradients = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> _hatches = new(StringComparer.Ordinal);
    private readonly Func<string, Func<int, int, (string Text, double? Value)?>?>? _sheets;
    private readonly List<List<Cell>> _table = [];
    private string? _tableName;
    private long _points;

    /// <summary>The last bubble series' chart:domain ranges (Y, X), for the series that leave them out.</summary>
    private List<string?> _domains = [];

    /// <summary>A cell of the chart's table: its text and, for a number, its value.</summary>
    private readonly record struct Cell(string Text, double? Value);

    private OdfChartReader(Func<string, Func<int, int, (string Text, double? Value)?>?>? sheets)
    {
        _sheets = sheets;
    }

    /// <summary>Whether <paramref name="content"/> (an object's content.xml root, or an inline office:document) is a chart.</summary>
    public static bool IsChart(XElement? content) => ChartElement(content) is not null;

    private static XElement? ChartElement(XElement? content) =>
        content?.Element(Office + "body")?.Element(Office + "chart")?.Element(C + "chart");

    /// <summary>
    /// Reads the chart object <paramref name="content"/> (its content.xml root, or an inline office:document) with
    /// <paramref name="styles"/> (its styles.xml root, or null). <paramref name="sheets"/> gives the cells (by 0-based row
    /// and column) of a sheet of the spreadsheet the chart sits in, by name, for ranges into it (null for a sheet it
    /// doesn't know); null when there is no spreadsheet.
    /// </summary>
    /// <exception cref="Core.Conversion.ConversionRefusedException">The chart has more series, points or cells than Filee draws.</exception>
    public static ChartRead Read(XElement? content, XElement? styles = null, Func<string, Func<int, int, (string Text, double? Value)?>?>? sheets = null)
    {
        if (ChartElement(content) is not { } chart)
            return new(null, "charts");
        var reader = new OdfChartReader(sheets);
        foreach (var root in new[] { styles, content })
        {
            foreach (var group in new[] { "styles", "automatic-styles" })
            {
                foreach (var style in root?.Element(Office + group)?.Elements(Style + "style") ?? [])
                {
                    if ((string?)style.Attribute(Style + "name") is { } name)
                        reader._styles[name] = style;
                }
            }
            // Gradients and hatches a fill names (draw:fill-gradient-name, draw:fill-hatch-name).
            foreach (var element in root?.Element(Office + "styles")?.Elements() ?? [])
            {
                if ((string?)element.Attribute(Draw + "name") is not { } name)
                    continue;
                if (element.Name == Draw + "gradient")
                    reader._gradients[name] = element;
                else if (element.Name == Draw + "hatch")
                    reader._hatches[name] = element;
            }
        }
        return reader.ReadChart(chart);
    }

    private ChartRead ReadChart(XElement chart)
    {
        if (chart.Element(Table + "table") is { } table)
            ReadTable(table);
        var plotArea = chart.Element(C + "plot-area");
        if (plotArea is null || !Kinds.TryGetValue((string?)chart.Attribute(C + "class") ?? "", out var kind))
            return new(null, "charts");
        var plotProps = Props(plotArea, "chart-properties");
        // 3D bars, lines, areas, pies and surfaces are drawn in 3D.
        var threeD = On(plotProps, "three-dimensional");
        if (threeD && kind is not (ChartKind.Bar or ChartKind.Line or ChartKind.Area or ChartKind.Pie or ChartKind.Surface))
            return new(null, "charts");
        var chartClass = (string?)chart.Attribute(C + "class") ?? "";
        var filled = chartClass == "chart:filled-radar";

        var text = TextStyle(Props(chart, "text-properties"), new ChartTextStyle(null, 10, false, false, SKColors.Black));
        var axes = plotArea.Elements(C + "axis").ToList();
        var xAxis = axes.FirstOrDefault(a => (string?)a.Attribute(C + "dimension") == "x");
        // One list of category labels, shared by every series.
        IReadOnlyList<string?>? categories = Range((string?)xAxis?.Element(C + "categories")?.Attribute(Table + "cell-range-address"), 0, byColumn: true)
            ?.Select(c => (string?)Cut(c.Text)).ToList();
        var horizontal = On(plotProps, "vertical");
        var seriesSource = Attr(plotProps, C + "series-source") ?? "columns";

        // Plots by kind (stock and Gantt series apart from lines and bars) and axis, in the order their first series comes.
        var plots = new List<(ChartKind Kind, string Class, bool Secondary, ChartPlot Plot)>();
        var seriesElements = plotArea.Elements(C + "series").ToList();
        string ClassOf(XElement series) => (string?)series.Attribute(C + "class") ?? chartClass;
        var stockSeries = seriesElements.Where(e => ClassOf(e) == "chart:stock").ToList();
        var position = 0;
        foreach (var element in seriesElements)
        {
            var seriesClass = ClassOf(element);
            var seriesKind = Kinds.TryGetValue(seriesClass, out var own) ? own : (ChartKind?)null;
            if (seriesKind is null || threeD && seriesKind != kind)
                return new(null, "charts"); // a series of a kind not drawn, or flat in a 3D chart
            if (position >= ChartReader.MaxSeries)
                throw DocumentLimits.Exceeded($"A chart has more than {DocumentLimits.Count(ChartReader.MaxSeries)} series.");
            var special = seriesClass is "chart:stock" or "chart:gantt" ? seriesClass : "";
            var secondary = (string?)element.Attribute(C + "attached-axis") == "secondary-y" && seriesKind is not (ChartKind.Pie or ChartKind.Doughnut);
            var entry = plots.FirstOrDefault(p => p.Kind == seriesKind && p.Class == special && p.Secondary == secondary);
            if (entry.Plot is null)
            {
                entry = (seriesKind.Value, special, secondary, Plot(seriesKind.Value, plotProps, horizontal, secondary, text, xAxis, threeD, filled, special, plotArea));
                plots.Add(entry);
            }
            if (seriesKind == ChartKind.Surface && entry.Plot.Series.Count == 0 && SurfaceBlock(element, text) is { } rows)
            {
                // A block of rows and columns: a series a row.
                if (rows.Count > ChartReader.MaxSeries)
                    throw DocumentLimits.Exceeded($"A chart has more than {DocumentLimits.Count(ChartReader.MaxSeries)} series.");
                entry.Plot.Series.AddRange(rows);
                position += rows.Count;
                continue;
            }
            var stockRole = special == "chart:stock" ? stockSeries.IndexOf(element) - (stockSeries.Count >= 4 ? 0 : -1) : -1;
            entry.Plot.Series.Add(Series(element, seriesKind.Value, categories, position, seriesSource, text, stockRole, On(plotProps, "japanese-candle-stick")));
            position++;
        }
        if (plots.Count == 0 || plots.All(p => p.Plot.Series.All(s => s.Values.Count == 0)))
            return new(null, "charts");
        // Gantt series pair up as start and end: a stacked bar from the start (left clear) by the length.
        var hidden = new HashSet<int>();
        foreach (var (_, special, _, plot) in plots)
        {
            if (special == "chart:gantt")
                Gantt(plot);
        }
        var entryIndex = 0;
        foreach (var (_, special, _, plot) in plots)
        {
            for (var i = 0; i < plot.Series.Count; i++)
            {
                var series = plot.Series[i];
                if (series.Style.Fill == SKColors.Transparent && series.Style.Line?.None == true && series.Name is null)
                    hidden.Add(entryIndex);
                else if (special == "chart:stock" && plot.Series.Count > 1)
                {
                    // One legend entry for the prices (Calc keeps them as one series), named after the last (close) one and keyed by the
                    // candlestick or the range line; its series draw neither line nor box of their own.
                    if (i < plot.Series.Count - 1)
                        hidden.Add(entryIndex);
                    else
                        series.LegendKey = plot.UpDownBars?.Up ?? new ChartShapeStyle { Fill = SKColors.Transparent, Line = plot.HighLowLines ?? new ChartLine(SKColors.Black) };
                }
                entryIndex++;
            }
        }
        var surface = plots.FirstOrDefault(p => p.Kind == ChartKind.Surface).Plot;
        if (surface is not null && seriesElements.FirstOrDefault(e => ClassOf(e) == "chart:surface") is { } firstSurface)
            surface = SurfaceBands(surface, firstSurface);

        var wall = plotArea.Element(C + "wall");
        var result = new Chart
        {
            Title = Title(chart.Element(C + "title"), chart.Element(C + "subtitle"), text with { Size = 13 }),
            Legend = Legend(chart.Element(C + "legend"), text, hidden),
            Area = Shape(Props(chart, "graphic-properties")) is { Fill: not null } area ? area : new ChartShapeStyle { Fill = SKColors.White, Line = ChartLine.Hidden },
            PlotArea = wall is null ? new ChartShapeStyle { Fill = SKColors.Transparent, Line = ChartLine.Hidden } : Shape(Props(wall, "graphic-properties")),
            RoundedCorners = false,
            Text = text,
            Palette = Palette,
            View3D = threeD ? View3D(plotArea, plotProps, kind) : null,
        };
        result.Plots.AddRange(plots.Select(p => p.Kind == ChartKind.Surface && surface is not null ? surface : p.Plot));
        if (plots.Any(p => p.Kind is not (ChartKind.Pie or ChartKind.Doughnut)))
            AddAxes(result, axes, horizontal, plots.Any(p => p.Kind is ChartKind.Scatter or ChartKind.Bubble), plots.Any(p => p.Secondary), text);
        if (surface is not null)
        {
            // The altitude is the z axis' (its range and interval make the bands); a 3D surface's rows stand along y.
            var z = axes.FirstOrDefault(a => (string?)a.Attribute(C + "dimension") == "z");
            var y = axes.FirstOrDefault(a => (string?)a.Attribute(C + "dimension") == "y");
            var value = result.Axes.FindIndex(a => a.Id == 2);
            if (value >= 0 && z is not null)
                result.Axes[value] = Axis(z, 2, 1, true, horizontal ? "b" : "l", false, text);
            if (threeD)
                result.Axes.Add(Axis(y, 5, 2, false, "b", false, text) with { IsSeries = true, IsValue = false, MajorGridlines = null });
        }
        return new(result, null);
    }

    /// <summary>
    /// How a 3D chart is seen: the plot area's dr3d:transform (the scene's rotation) as Office's rotation about the
    /// vertical axis and tilt, with chart:right-angled-axes; Office's view of a new 3D chart when there is none.
    /// </summary>
    private static ChartView3D View3D(XElement plotArea, XElement? props, ChartKind kind)
    {
        var pie = kind == ChartKind.Pie;
        var rightAngles = Attr(props, C + "right-angled-axes") is { } right ? right == "true" : !pie;
        if (Rotation((string?)plotArea.Attribute(OdfNames.Dr3d + "transform")) is not { } m)
            return pie ? new ChartView3D(30, 0, RightAngleAxes: false) : new ChartView3D(RightAngleAxes: rightAngles);
        // The images of the x axis (a b c), y axis (d e f) and z axis (g h i): the turn about the vertical axis takes
        // x into the depth, the tilt brings y towards the viewer. A pie lies in the x-y plane facing the viewer, so its
        // tilt from the top is what is left of 90°.
        var turn = Math.Atan2(m[6], m[0]) * 180 / Math.PI;
        var tilt = Math.Atan2(m[5], m[4]) * 180 / Math.PI;
        if (!double.IsFinite(turn) || !double.IsFinite(tilt))
            return new ChartView3D(RightAngleAxes: rightAngles);
        var rotY = (turn % 360 + 360) % 360;
        var rotX = pie ? 90 - Math.Abs(tilt) : tilt;
        return new ChartView3D(Math.Clamp(Math.Round(rotX), pie ? 0 : -90, 90), Math.Round(rotY) % 360, RightAngleAxes: rightAngles,
            Perspective: (string?)plotArea.Attribute(OdfNames.Dr3d + "projection") == "perspective" ? 30 : 0);
    }

    /// <summary>
    /// The rotation part (a b c d e f g h i, the images of the three axes) of a dr3d:transform list: matrix(...) and
    /// rotatex / rotatey / rotatez (radians, or with a unit), applied in order; null when there is none it reads.
    /// </summary>
    internal static double[]? Rotation(string? transform)
    {
        if (string.IsNullOrWhiteSpace(transform) || transform.Length > 4096)
            return null;
        double[]? result = null;
        var at = 0;
        for (var steps = 0; steps < 64 && at < transform.Length; steps++)
        {
            var open = transform.IndexOf('(', at);
            var close = open < 0 ? -1 : transform.IndexOf(')', open);
            if (open < 0 || close < 0)
                break;
            var name = transform[at..open].Trim().ToLowerInvariant();
            var values = transform[(open + 1)..close].Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
            at = close + 1;
            double[]? step = null;
            if (name == "matrix" && values.Length >= 9 && values.Take(9).Select(Number).ToList() is var numbers && numbers.All(v => v is not null))
            {
                step = [.. numbers.Select(v => v!.Value)];
            }
            else if (name is "rotatex" or "rotatey" or "rotatez" && values.Length >= 1 && Radians(values[0]) is { } angle)
            {
                var (sin, cos) = Math.SinCos(angle);
                step = name switch
                {
                    "rotatex" => [1, 0, 0, 0, cos, sin, 0, -sin, cos],
                    "rotatey" => [cos, 0, -sin, 0, 1, 0, sin, 0, cos],
                    _ => [cos, sin, 0, -sin, cos, 0, 0, 0, 1],
                };
            }
            if (step is null)
                continue;
            result = result is null ? step : Multiply(step, result);
        }
        return result is not null && result.All(double.IsFinite) ? result : null;

        // A turn in radians, or in deg, grad or rad.
        static double? Radians(string value) =>
            value.EndsWith("deg", StringComparison.Ordinal) ? Number(value[..^3]) * Math.PI / 180
            : value.EndsWith("grad", StringComparison.Ordinal) ? Number(value[..^4]) * Math.PI / 200
            : value.EndsWith("rad", StringComparison.Ordinal) ? Number(value[..^3])
            : Number(value);

        // Column-major 3 × 3 products: (a · b) applies b first.
        static double[] Multiply(double[] a, double[] b)
        {
            var product = new double[9];
            for (var column = 0; column < 3; column++)
            {
                for (var row = 0; row < 3; row++)
                    product[column * 3 + row] = a[row] * b[column * 3] + a[3 + row] * b[column * 3 + 1] + a[6 + row] * b[column * 3 + 2];
            }
            return product;
        }
    }

    // ───────────────────────── Plots and series ─────────────────────────

    private ChartPlot Plot(ChartKind kind, XElement? props, bool horizontal, bool secondary, ChartTextStyle text, XElement? xAxis, bool threeD = false, bool filled = false, string special = "", XElement? plotArea = null) => new()
    {
        Kind = kind,
        // A stock chart's range lines (chart:stock-range-line) and candlesticks (chart:stock-gain-marker / -loss-marker).
        HighLowLines = special == "chart:stock" ? Shape(Props(plotArea?.Element(C + "stock-range-line"), "graphic-properties")).Line ?? new ChartLine(SKColors.Black) : null,
        UpDownBars = special == "chart:stock" && On(props, "japanese-candle-stick")
            ? new ChartUpDownBars(100, Candle(plotArea?.Element(C + "stock-gain-marker"), SKColors.White), Candle(plotArea?.Element(C + "stock-loss-marker"), SKColors.Black))
            : null,
        ThreeD = threeD,
        // chart:deep puts the series one behind the other; 3D lines always stand so.
        SeriesInDepth = threeD && (kind == ChartKind.Line || On(props, "deep")),
        Filled = kind == ChartKind.Radar && filled,
        // chart:angle-offset: where the first slice starts, anticlockwise from three o'clock (90 = the top); the
        // slices follow anticlockwise unless the angle (X) axis is reversed.
        FirstSliceAngle = ((90 - (Number(Attr(props, C + "angle-offset")) ?? 90)) % 360 + 360) % 360,
        Clockwise = On(Props(xAxis, "chart-properties"), "reverse-direction"),
        Grouping = special == "chart:gantt" ? ChartGrouping.Stacked
            : kind is ChartKind.Pie or ChartKind.Doughnut or ChartKind.Scatter or ChartKind.Bubble or ChartKind.Surface or ChartKind.Radar ? ChartGrouping.Standard
            : On(props, "percentage") ? ChartGrouping.PercentStacked : On(props, "stacked") ? ChartGrouping.Stacked : ChartGrouping.Standard,
        Horizontal = kind == ChartKind.Bar && horizontal,
        VaryColors = kind is ChartKind.Pie or ChartKind.Doughnut,
        GapWidth = (int)Math.Clamp(Number(Attr(props, C + "gap-width")) ?? 100, 0, 500),
        Overlap = special == "chart:gantt" ? 100 : (int)Math.Clamp(Number(Attr(props, C + "overlap")) ?? (On(props, "stacked") || On(props, "percentage") ? 100 : 0), -100, 100),
        HoleSize = 50,
        Lines = kind == ChartKind.Radar ? !filled : kind != ChartKind.Scatter || Attr(props, C + "lines") is not "false",
        Markers = special != "chart:stock" && (Attr(props, C + "symbol-type") is { } symbols && symbols != "none" && kind is ChartKind.Line or ChartKind.Scatter or ChartKind.Radar && !filled
            || kind == ChartKind.Scatter && Attr(props, C + "lines") == "false"),
        Labels = Labels(props, text),
        AxisIds = kind == ChartKind.Surface && threeD ? [1u, 2u, 5u] : secondary ? [3u, 4u] : [1u, 2u],
    };

    /// <summary>A candlestick's fill and outline (stock gain or loss marker), <paramref name="fill"/> when its style says none.</summary>
    private ChartShapeStyle Candle(XElement? marker, SKColor fill)
    {
        var shape = Shape(Props(marker, "graphic-properties"));
        return shape with { Fill = shape.Fill ?? fill, Line = shape.Line ?? new ChartLine(SKColors.Black, 0.75f) };
    }

    /// <param name="stockRole">A stock series' part: 0 open, 1 low, 2 high, 3 close; -1 for other series.</param>
    /// <param name="candles">The stock chart draws candlesticks (open and close are its bars, not ticks).</param>
    private ChartSeries Series(XElement element, ChartKind kind, IReadOnlyList<string?>? categories, int position, string seriesSource, ChartTextStyle text,
        int stockRole = -1, bool candles = false)
    {
        var byColumn = seriesSource != "rows";
        var values = Range((string?)element.Attribute(C + "values-cell-range-address"), position + 1, byColumn) ?? [];
        Count(values.Count);
        // A bubble series' values are its sizes; its domains give Y then X (or the last series' domains, when it has none).
        List<Cell>? bubbleY = null;
        if (kind == ChartKind.Bubble)
        {
            // A domain the series names replaces the last series' in its place; the others carry over.
            var domains = element.Elements(C + "domain").Take(2).Select(d => (string?)d.Attribute(Table + "cell-range-address")).ToList();
            for (var i = 0; i < domains.Count; i++)
            {
                if (i < _domains.Count)
                    _domains[i] = domains[i];
                else
                    _domains.Add(domains[i]);
            }
            bubbleY = Range(_domains.ElementAtOrDefault(0), -1, byColumn);
            if (bubbleY is not null)
                Count(bubbleY.Count);
        }
        var label = Range((string?)element.Attribute(C + "label-cell-address"), position + 1, byColumn, label: true);
        var props = Props(element, "chart-properties");
        var shape = Shape(Props(element, "graphic-properties"));
        IReadOnlyList<double?>? xValues = null;
        if (kind == ChartKind.Scatter && element.Element(C + "domain") is { } domain
            && Range((string?)domain.Attribute(Table + "cell-range-address"), 0, byColumn) is { } x)
        {
            Count(x.Count);
            xValues = [.. x.Select(c => c.Value)];
        }
        else if (kind == ChartKind.Bubble && Range(_domains.ElementAtOrDefault(1), -1, byColumn) is { } bubbleX)
        {
            Count(bubbleX.Count);
            xValues = [.. bubbleX.Select(c => c.Value)];
        }

        // Points styled on their own: chart:data-point runs (chart:repeated), in order.
        var points = new Dictionary<int, ChartShapeStyle>();
        var explosion = new Dictionary<int, int>();
        var index = 0;
        foreach (var point in element.Elements(C + "data-point"))
        {
            var repeated = (int)Math.Clamp(Number((string?)point.Attribute(C + "repeated")) ?? 1, 1, ChartReader.MaxPoints);
            if ((string?)point.Attribute(C + "style-name") is { } name && _styles.ContainsKey(name))
            {
                var style = Shape(Props(point, "graphic-properties"));
                var offset = Number(Attr(Props(point, "chart-properties"), C + "pie-offset"));
                for (var i = index; i < Math.Min(index + repeated, values.Count); i++)
                {
                    points[i] = style;
                    if (offset is > 0)
                        explosion[i] = (int)Math.Min(offset.Value, 400);
                }
            }
            index += repeated;
            if (index >= values.Count)
                break;
        }
        if (Number(Attr(props, C + "pie-offset")) is > 0 and var whole)
            explosion[-1] = (int)Math.Min(whole, 400);

        var symbol = Attr(props, C + "symbol-type");
        ChartMarker? marker = symbol is null ? null : new ChartMarker(symbol == "none" ? "none" : Symbol(Attr(props, C + "symbol-name")),
            (float)Math.Clamp(Points(Attr(props, C + "symbol-width")) ?? 5, 2, 72), shape);
        if (stockRole >= 0)
        {
            // A stock series draws no line: its range line joins low and high, open and close are ticks (or candlesticks).
            var tick = !candles && stockRole is 0 or 3;
            var color = shape.Line?.Color ?? SKColors.Black;
            marker = new ChartMarker(tick ? "dash" : "none", 7, new ChartShapeStyle { Fill = color, Line = new ChartLine(color) });
            shape = shape with { Line = ChartLine.Hidden };
        }
        return new ChartSeries
        {
            Name = label is [var cell, ..] ? Cut(cell.Text) : null,
            Index = position,
            Categories = kind is ChartKind.Scatter or ChartKind.Bubble ? null : categories,
            XValues = xValues,
            Values = bubbleY is not null ? [.. bubbleY.Select(c => c.Value)] : [.. values.Select(c => c.Value)],
            BubbleSizes = kind == ChartKind.Bubble ? [.. values.Select(c => c.Value)] : null,
            // A pie's or ring's slices vary by point: a slice without a style of its own takes the palette, not the series' fill.
            Style = kind is ChartKind.Pie or ChartKind.Doughnut ? shape with { Fill = null, Gradient = null, Hatch = null } : shape,
            Points = points,
            Explosion = explosion,
            Marker = marker,
            Labels = Labels(props, text),
            Smooth = Attr(props, C + "interpolation") is "cubic-spline" or "b-spline",
            Trendlines = Trendlines(element, props, text),
            ErrorBars = ErrorBars(element, props, values, kind == ChartKind.Scatter, byColumn),
        };
    }

    /// <summary>
    /// A Gantt plot: its series in pairs of start and end become a clear part from zero to the start and a part from
    /// the start to the end (the first series' style, the second's where the end comes before the start).
    /// </summary>
    private static void Gantt(ChartPlot plot)
    {
        var pairs = plot.Series.ToList();
        plot.Series.Clear();
        for (var i = 0; i + 1 < pairs.Count; i += 2)
        {
            var (start, end) = (pairs[i], pairs[i + 1]);
            var count = Math.Min(start.Values.Count, end.Values.Count);
            var from = new double?[count];
            var length = new double?[count];
            var points = new Dictionary<int, ChartShapeStyle>();
            for (var p = 0; p < count; p++)
            {
                if (start.Values[p] is not { } a || end.Values[p] is not { } b)
                    continue;
                from[p] = Math.Min(a, b);
                length[p] = Math.Abs(b - a);
                if (b < a)
                    points[p] = end.Style;
            }
            plot.Series.Add(new ChartSeries
            {
                Index = start.Index,
                Categories = start.Categories,
                Values = from,
                Style = new ChartShapeStyle { Fill = SKColors.Transparent, Line = ChartLine.Hidden },
            });
            plot.Series.Add(new ChartSeries
            {
                Name = start.Name,
                Index = start.Index,
                Categories = start.Categories,
                Values = length,
                Style = start.Style,
                Points = points,
                Labels = start.Labels,
            });
        }
    }

    /// <summary>
    /// A surface series whose values are a block of several rows and columns: a series a row (named by the first
    /// chart:domain's Y values, else numbered), the columns its categories (the second domain's X values, else
    /// numbered); null for a series of one row or column.
    /// </summary>
    private List<ChartSeries>? SurfaceBlock(XElement element, ChartTextStyle text)
    {
        var address = (string?)element.Attribute(C + "values-cell-range-address");
        if (string.IsNullOrWhiteSpace(address) || address.Contains(' ', StringComparison.Ordinal))
            return null;
        var ends = address.Split(':');
        if (ends.Length != 2 || Reference(ends[0]) is not { } start || Reference(ends[1]) is not { } end)
            return null;
        var (rows, columns) = (Math.Abs(end.Row - start.Row) + 1, Math.Abs(end.Column - start.Column) + 1);
        if (rows < 2 || columns < 2)
            return null;
        var cells = Range(address, -1, byColumn: false);
        if (cells is null || cells.Count != rows * columns)
            return null;
        Count(cells.Count);
        var domains = element.Elements(C + "domain").Take(2).Select(d => Range((string?)d.Attribute(Table + "cell-range-address"), -1, byColumn: true)).ToList();
        var names = domains.ElementAtOrDefault(0);
        var xs = domains.ElementAtOrDefault(1);
        IReadOnlyList<string?> categories = [.. Enumerable.Range(0, columns).Select(c => (string?)(xs?.ElementAtOrDefault(c) is { } x ? Cut(x.Text) : (c + 1).ToString(CultureInfo.InvariantCulture)))];
        var props = Props(element, "chart-properties");
        var series = new List<ChartSeries>();
        for (var r = 0; r < rows; r++)
        {
            series.Add(new ChartSeries
            {
                Name = names?.ElementAtOrDefault(r) is { } name ? Cut(name.Text) : (r + 1).ToString(CultureInfo.InvariantCulture),
                Index = r,
                Categories = categories,
                Values = [.. cells.Skip(r * columns).Take(columns).Select(c => c.Value)],
                Labels = Labels(props, text),
            });
        }
        return series;
    }

    /// <summary>
    /// A surface plot with its bands' fills: the first surface series' data points, in order, fill the value bands
    /// from the lowest; a series without a fill is drawn as a mesh.
    /// </summary>
    private ChartPlot SurfaceBands(ChartPlot plot, XElement first)
    {
        var bands = new Dictionary<int, ChartShapeStyle>();
        var index = 0;
        foreach (var point in first.Elements(C + "data-point"))
        {
            var repeated = (int)Math.Clamp(Number((string?)point.Attribute(C + "repeated")) ?? 1, 1, 256);
            if ((string?)point.Attribute(C + "style-name") is { } name && _styles.ContainsKey(name))
            {
                var style = Shape(Props(point, "graphic-properties"));
                for (var i = index; i < Math.Min(index + repeated, 256); i++)
                    bands[i] = style;
            }
            index += repeated;
            if (index >= 256)
                break;
        }
        var wire = Attr(Props(first, "graphic-properties"), Draw + "fill") == "none";
        var result = new ChartPlot
        {
            Kind = plot.Kind,
            ThreeD = plot.ThreeD,
            SeriesInDepth = plot.ThreeD,
            AxisIds = plot.AxisIds,
            Labels = plot.Labels,
            Wireframe = wire,
            Bands = bands,
        };
        result.Series.AddRange(plot.Series);
        return result;
    }

    /// <summary>
    /// The trendlines of a series: its chart:regression-curve elements (their style's regression type, degree,
    /// period, extrapolation, forced intercept and line, and chart:equation's display flags), or the regression
    /// type of the series' own style (ODF 1.1).
    /// </summary>
    private List<ChartTrendline> Trendlines(XElement series, XElement? seriesProps, ChartTextStyle text)
    {
        var result = new List<ChartTrendline>();
        var curves = series.Elements(C + "regression-curve").Take(ChartReader.MaxTrendlines).ToList();
        if (curves.Count == 0 && Trendline(seriesProps, null, null, text) is { } own)
            result.Add(own);
        foreach (var curve in curves)
        {
            var equation = curve.Element(C + "equation");
            var props = Props(curve, "chart-properties") ?? seriesProps;
            if (Trendline(props, Props(curve, "graphic-properties"), equation, text) is { } trend)
                result.Add(trend);
        }
        return result;
    }

    private ChartTrendline? Trendline(XElement? props, XElement? graphic, XElement? equation, ChartTextStyle text)
    {
        TrendKind? kind = Attr(props, C + "regression-type") switch
        {
            "linear" => TrendKind.Linear,
            "logarithmic" => TrendKind.Logarithmic,
            "exponential" => TrendKind.Exponential,
            "power" => TrendKind.Power,
            "polynomial" => TrendKind.Polynomial,
            "moving-average" => TrendKind.MovingAverage,
            _ => null,
        };
        if (kind is not { } trend)
            return null;
        var force = On(props, "regression-force-intercept");
        bool Shown(string name) => (string?)equation?.Attribute(C + name) == "true";
        return new ChartTrendline(trend)
        {
            Order = (int)Math.Clamp(Number(Attr(props, C + "regression-max-degree")) ?? 2, 2, 6),
            Period = (int)Math.Clamp(Number(Attr(props, C + "regression-period")) ?? 2, 2, 255),
            Forward = Math.Clamp(Number(Attr(props, C + "regression-extrapolate-forward")) ?? 0, 0, ChartReader.MaxValue),
            Backward = Math.Clamp(Number(Attr(props, C + "regression-extrapolate-backward")) ?? 0, 0, ChartReader.MaxValue),
            Intercept = force ? Clamp(Number(Attr(props, C + "regression-intercept-value")) ?? 0) : null,
            ShowEquation = Shown("display-equation"),
            ShowRSquared = Shown("display-r-square"),
            Line = graphic is null ? null : Shape(graphic).Line,
            Style = TextStyle(Props(equation, "text-properties"), text),
        };
    }

    /// <summary>
    /// The error bars of a series: its chart:error-indicator elements (Y, or X for a scatter), or the error properties
    /// of its own style (ODF 1.1, Y): the category (constant, percentage, error margin, standard deviation, variance,
    /// standard error, cell ranges), the limits and which sides are shown.
    /// </summary>
    private List<ChartErrorBars> ErrorBars(XElement series, XElement? seriesProps, List<Cell> values, bool scatter, bool byColumn)
    {
        var result = new List<ChartErrorBars>();
        var indicators = series.Elements(C + "error-indicator").Take(2).ToList();
        if (indicators.Count == 0 && ErrorBars(seriesProps, null, values, byColumn) is { } own)
            result.Add(own);
        foreach (var indicator in indicators)
        {
            var alongX = (string?)indicator.Attribute(C + "dimension") == "x";
            if (alongX && !scatter)
                continue;
            var props = Props(indicator, "chart-properties") ?? seriesProps;
            if (ErrorBars(props, Props(indicator, "graphic-properties"), values, byColumn) is { } bars)
                result.Add(bars with { AlongX = alongX });
        }
        return result;
    }

    private ChartErrorBars? ErrorBars(XElement? props, XElement? graphic, List<Cell> values, bool byColumn)
    {
        var category = Attr(props, C + "error-category");
        if (category is null or "none")
            return null;
        var upper = Clamp(Number(Attr(props, C + "error-upper-limit"))) ?? 0;
        var lower = Clamp(Number(Attr(props, C + "error-lower-limit"))) ?? 0;
        var percentage = Math.Clamp(Number(Attr(props, C + "error-percentage")) ?? 0, 0, ChartReader.MaxValue);
        var margin = Math.Clamp(Number(Attr(props, C + "error-margin")) ?? 0, 0, ChartReader.MaxValue);
        ChartErrorBars? bars = category switch
        {
            "constant" => new ChartErrorBars(ErrorAmount.Custom, 0) { PlusValues = [Math.Abs(upper)], MinusValues = [Math.Abs(lower)] },
            "percentage" => new ChartErrorBars(ErrorAmount.Percentage, percentage),
            // An error margin is a percentage of the series' largest value.
            "error-margin" => new ChartErrorBars(ErrorAmount.Fixed, Math.Min(values.Max(c => Math.Abs(c.Value ?? 0)) * margin / 100, ChartReader.MaxValue)),
            "standard-deviation" => new ChartErrorBars(ErrorAmount.StandardDeviation, 1),
            "variance" => new ChartErrorBars(ErrorAmount.Variance, 1),
            "standard-error" => new ChartErrorBars(ErrorAmount.StandardError, 1),
            "cell-range" => new ChartErrorBars(ErrorAmount.Custom, 0)
            {
                PlusValues = Values(Range(Attr(props, C + "error-upper-range"), -1, byColumn)),
                MinusValues = Values(Range(Attr(props, C + "error-lower-range"), -1, byColumn)),
            },
            _ => null,
        };
        if (bars is null)
            return null;
        return bars with
        {
            Plus = Attr(props, C + "error-upper-indicator") != "false",
            Minus = Attr(props, C + "error-lower-indicator") != "false",
            Line = graphic is null ? null : Shape(graphic).Line,
            Office = false,
        };
    }

    private List<double?>? Values(List<Cell>? cells)
    {
        if (cells is null)
            return null;
        Count(cells.Count);
        return [.. cells.Select(c => c.Value)];
    }

    private void Count(int points)
    {
        if ((_points += points) > ChartReader.MaxPoints)
            throw DocumentLimits.Exceeded($"A chart has more than {DocumentLimits.Count(ChartReader.MaxPoints)} points.");
    }

    private static string Symbol(string? name) => name switch
    {
        "square" => "square",
        "diamond" => "diamond",
        "arrow-up" or "arrow-down" or "arrow-left" or "arrow-right" or "bow-tie" or "hourglass" => "triangle",
        "circle" => "circle",
        "star" => "star",
        "x" => "x",
        "plus" => "plus",
        "asterisk" => "star",
        "horizontal-bar" or "vertical-bar" => "dash",
        _ => "square",
    };

    private ChartLabels? Labels(XElement? props, ChartTextStyle text)
    {
        if (props is null)
            return null;
        var number = Attr(props, C + "data-label-number");
        var labels = new ChartLabels(
            number is "value" or "value-and-percentage",
            On(props, "data-label-text"),
            On(props, "data-label-series"),
            number is "percentage" or "value-and-percentage",
            Attr(props, C + "label-position") switch
            {
                "outside" => "outEnd",
                "inside" => "inEnd",
                "center" => "ctr",
                "top" => "t",
                "bottom" => "b",
                "left" => "l",
                "right" => "r",
                "avoid-overlap" => "bestFit",
                _ => null,
            },
            null,
            TextStyle(props.Parent?.Element(Style + "text-properties"), text));
        return labels.Any ? labels : null;
    }

    // ───────────────────────── Axes, title, legend ─────────────────────────

    private void AddAxes(Chart chart, List<XElement> axes, bool horizontal, bool scatter, bool secondary, ChartTextStyle text)
    {
        XElement? Find(string name, string dimension) =>
            axes.FirstOrDefault(a => (string?)a.Attribute(C + "name") == name)
            ?? (name.StartsWith("primary", StringComparison.Ordinal) ? axes.FirstOrDefault(a => (string?)a.Attribute(C + "dimension") == dimension) : null);
        chart.Axes.Add(Axis(Find("primary-x", "x"), 1, 2, scatter, horizontal ? "l" : "b", !scatter, text));
        chart.Axes.Add(Axis(Find("primary-y", "y"), 2, 1, true, horizontal ? "b" : "l", !scatter, text));
        if (secondary)
        {
            chart.Axes.Add(new ChartAxis { Id = 3, CrossAxis = 4, Deleted = true, IsValue = scatter, Position = horizontal ? "r" : "t" });
            var secondY = axes.FirstOrDefault(a => (string?)a.Attribute(C + "name") == "secondary-y");
            chart.Axes.Add(Axis(secondY, 4, 3, true, horizontal ? "t" : "r", !scatter, text) with { Crosses = ChartCrossing.Max });
        }
    }

    private ChartAxis Axis(XElement? axis, uint id, uint cross, bool value, string position, bool between, ChartTextStyle text)
    {
        var props = axis is null ? null : Props(axis, "chart-properties");
        var textProps = axis is null ? null : Props(axis, "text-properties");
        ChartLine? Grid(string kind) =>
            axis?.Elements(C + "grid").FirstOrDefault(g => ((string?)g.Attribute(C + "class") ?? "major") == kind) is { } grid
                ? Shape(Props(grid, "graphic-properties")).Line ?? new ChartLine(new SKColor(0xB3, 0xB3, 0xB3))
                : null;
        var angle = Number(Attr(props, Style + "rotation-angle"));
        return new ChartAxis
        {
            Id = id,
            CrossAxis = cross,
            IsValue = value,
            Position = position,
            Deleted = axis is null || Attr(props, C + "visible") == "false",
            Reversed = On(props, "reverse-direction"),
            Min = Clamp(Number(Attr(props, C + "minimum"))),
            Max = Clamp(Number(Attr(props, C + "maximum"))),
            MajorUnit = Number(Attr(props, C + "interval-major")) is > 0 and var unit ? Math.Min(unit, ChartReader.MaxValue) : null,
            LogBase = On(props, "logarithmic") ? 10 : null,
            Title = axis?.Element(C + "title") is { } title ? Title(title, null, text with { Bold = true }) : null,
            MajorGridlines = Grid("major"),
            MinorGridlines = Grid("minor"),
            Line = Shape(Props(axis, "graphic-properties")).Line,
            LabelPosition = Attr(props, C + "display-label") == "false" ? "none" : "nextTo",
            MajorTicks = (On(props, "tick-marks-major-outer"), On(props, "tick-marks-major-inner")) switch
            {
                (true, true) => "cross",
                (true, false) => "out",
                (false, true) => "in",
                _ => props is null ? "out" : "none",
            },
            Between = between,
            LabelRotation = angle is { } turn && turn != 0 ? -NormalAngle(turn) : null,
            Style = TextStyle(textProps, text),
        };
    }

    private static double NormalAngle(double degrees)
    {
        degrees %= 360;
        return degrees > 180 ? degrees - 360 : degrees < -180 ? degrees + 360 : degrees;
    }

    private ChartTitle? Title(XElement? title, XElement? subtitle, ChartTextStyle style)
    {
        var lines = new List<string>();
        foreach (var part in new[] { title, subtitle })
        {
            foreach (var p in part?.Elements(Text + "p") ?? [])
            {
                if (lines.Count < ChartReader.MaxLines)
                    lines.Add(Cut(p.Value));
            }
        }
        if (title is null || lines.All(l => l.Length == 0))
            return null;
        return new ChartTitle(lines, TextStyle(Props(title, "text-properties"), style));
    }

    private ChartLegend? Legend(XElement? legend, ChartTextStyle text, IReadOnlySet<int> hidden) => legend is null ? null : new ChartLegend
    {
        Deleted = hidden,
        Position = (string?)legend.Attribute(C + "legend-position") switch
        {
            "start" => "l",
            "top" => "t",
            "bottom" => "b",
            "top-end" => "tr",
            _ => "r",
        },
        Style = TextStyle(Props(legend, "text-properties"), text),
    };

    // ───────────────────────── The chart's table ─────────────────────────

    private void ReadTable(XElement table)
    {
        _tableName = (string?)table.Attribute(Table + "name");
        long cells = 0;
        foreach (var row in table.Descendants(Table + "table-row"))
        {
            var rows = (int)Math.Clamp(Number((string?)row.Attribute(Table + "number-rows-repeated")) ?? 1, 1, ChartReader.MaxPoints);
            var cellsOfRow = new List<Cell>();
            foreach (var cell in row.Elements())
            {
                if (cell.Name != Table + "table-cell" && cell.Name != Table + "covered-table-cell")
                    continue;
                var repeated = (int)Math.Clamp(Number((string?)cell.Attribute(Table + "number-columns-repeated")) ?? 1, 1, ChartReader.MaxPoints);
                if ((cells += (long)repeated * rows) > MaxCells)
                    throw DocumentLimits.TooLarge($"A chart's table has more than {DocumentLimits.Count(MaxCells)} cells.");
                var text = Cut(string.Join("\n", cell.Elements(Text + "p").Select(p => p.Value)));
                double? value = (string?)cell.Attribute(Office + "value-type") is "float" or "percentage" or "currency"
                    ? Clamp(Number((string?)cell.Attribute(Office + "value")))
                    : null;
                for (var i = 0; i < repeated; i++)
                    cellsOfRow.Add(new Cell(text, value));
            }
            for (var i = 0; i < rows; i++)
                _table.Add(cellsOfRow);
        }
    }

    private Cell? At(int row, int column) =>
        row >= 0 && row < _table.Count && column >= 0 && column < _table[row].Count ? _table[row][column] : null;

    /// <summary>
    /// The cells of a range address ("local-table.$B$2:.$B$5", several ranges separated by spaces) in the chart's
    /// table; for a range into another table, the table's column (or row) <paramref name="fallback"/> below (or right
    /// of) the header. Null when there is no address or nothing is found.
    /// </summary>
    private List<Cell>? Range(string? address, int fallback, bool byColumn, bool label = false)
    {
        if (string.IsNullOrWhiteSpace(address) || _table.Count == 0 && _sheets is null)
            return null;
        var cells = new List<Cell>();
        foreach (var part in address.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var ends = part.Split(':');
            if (ends.Length > 2 || Reference(ends[0]) is not { } start)
                return null;
            var end = ends.Length == 2 ? Reference(ends[1]) ?? start : start;
            var tableName = start.Table ?? end.Table;
            var sheet = tableName is not null && tableName != _tableName ? _sheets?.Invoke(tableName) : null;
            if (tableName is not null && tableName != _tableName && sheet is null)
            {
                // A range into a sheet the host doesn't hand over: the series' place in the chart's own table instead.
                if (fallback < 0)
                    return null;
                return label ? Fallback(fallback, byColumn, header: true) : Fallback(fallback, byColumn, header: false);
            }
            var (top, bottom) = (Math.Min(start.Row, end.Row), Math.Max(start.Row, end.Row));
            var (left, right) = (Math.Min(start.Column, end.Column), Math.Max(start.Column, end.Column));
            if ((long)(bottom - top + 1) * (right - left + 1) + cells.Count > ChartReader.MaxPoints)
                throw DocumentLimits.Exceeded($"A chart has more than {DocumentLimits.Count(ChartReader.MaxPoints)} points.");
            for (var r = top; r <= bottom && (sheet is not null || r < _table.Count); r++)
            {
                for (var c = left; c <= right; c++)
                {
                    if (sheet is not null)
                        cells.Add(sheet(r, c) is { } found ? new Cell(Cut(found.Text), Clamp(found.Value)) : new Cell("", null));
                    else
                        cells.Add(At(r, c) ?? new Cell("", null));
                }
            }
        }
        return cells.Count == 0 ? null : cells;
    }

    private List<Cell>? Fallback(int index, bool byColumn, bool header)
    {
        if (header)
            return At(byColumn ? 0 : index, byColumn ? index : 0) is { } cell ? [cell] : null;
        var cells = new List<Cell>();
        if (byColumn)
        {
            for (var r = 1; r < _table.Count && cells.Count < ChartReader.MaxPoints; r++)
                cells.Add(At(r, index) ?? new Cell("", null));
        }
        else if (index < _table.Count)
        {
            for (var c = 1; c < _table[index].Count && cells.Count < ChartReader.MaxPoints; c++)
                cells.Add(_table[index][c]);
        }
        return cells.Count == 0 ? null : cells;
    }

    /// <summary>A cell reference ("local-table.$B$2", "'My table'.B2", ".$B$5"): its table (null when not named) and 0-based row and column.</summary>
    private static (string? Table, int Row, int Column)? Reference(string text)
    {
        var dot = text.LastIndexOf('.');
        var table = dot > 0 ? text[..dot].Trim('$').Trim('\'') : null;
        var cell = (dot >= 0 ? text[(dot + 1)..] : text).Replace("$", "", StringComparison.Ordinal);
        var letters = 0;
        while (letters < cell.Length && char.IsAsciiLetter(cell[letters]))
            letters++;
        if (letters == 0 || letters > 3 || !int.TryParse(cell[letters..], NumberStyles.None, CultureInfo.InvariantCulture, out var row) || row < 1)
            return null;
        var column = 0;
        foreach (var ch in cell[..letters])
            column = column * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        return (table is { Length: > 0 } ? table : null, row - 1, column - 1);
    }

    // ───────────────────────── Styles ─────────────────────────

    /// <summary>The properties element <paramref name="kind"/> of an element's chart:style-name style (its parents' when it has none).</summary>
    private XElement? Props(XElement? element, string kind)
    {
        var name = (string?)element?.Attribute(C + "style-name");
        for (var depth = 0; name is not null && depth < MaxStyleDepth && _styles.TryGetValue(name, out var style); depth++)
        {
            if (style.Element(Style + kind) is { } props)
                return props;
            name = (string?)style.Attribute(Style + "parent-style-name");
        }
        return null;
    }

    private ChartShapeStyle Shape(XElement? graphic)
    {
        if (graphic is null)
            return ChartShapeStyle.Auto;
        var kind = Attr(graphic, Draw + "fill");
        SKColor? fill = kind switch
        {
            "none" => SKColors.Transparent,
            _ => Color(Attr(graphic, Draw + "fill-color")),
        };
        ChartGradient? gradient = null;
        ChartHatch? hatch = null;
        if (kind == "gradient" && Attr(graphic, Draw + "fill-gradient-name") is { } gradientName && _gradients.TryGetValue(gradientName, out var g))
            gradient = Gradient(g);
        else if (kind == "hatch" && Attr(graphic, Draw + "fill-hatch-name") is { } hatchName && _hatches.TryGetValue(hatchName, out var h))
            hatch = Hatch(h, Attr(graphic, Draw + "fill-hatch-solid") == "true" ? fill ?? SKColors.White : null);
        fill = gradient?.Stops[0].Color ?? hatch?.Color ?? fill;
        var stroke = Attr(graphic, Draw + "stroke");
        ChartLine? line = stroke == "none" ? ChartLine.Hidden
            : Color(Attr(graphic, Svg + "stroke-color")) is { } color || Points(Attr(graphic, Svg + "stroke-width")) is > 0
                ? new ChartLine(Color(Attr(graphic, Svg + "stroke-color")), Points(Attr(graphic, Svg + "stroke-width")) is > 0 and var width ? Math.Min(width, 100) : null,
                    stroke == "dash" ? [4, 3] : null)
                : null;
        return new ChartShapeStyle { Fill = fill, Gradient = gradient, Hatch = hatch, Line = line };
    }

    /// <summary>
    /// A draw:gradient: linear (start colour first, along draw:angle anticlockwise from top to bottom), axial (start
    /// colour at both edges), or radial, ellipsoid, square and rectangular (start colour outside, out from draw:cx,
    /// draw:cy); draw:border keeps the start colour over that part, intensities darken the colours.
    /// </summary>
    private static ChartGradient? Gradient(XElement gradient)
    {
        static SKColor Dim(SKColor color, string? intensity) =>
            Percent(intensity) is { } f and < 1 ? new SKColor((byte)(color.Red * f), (byte)(color.Green * f), (byte)(color.Blue * f), color.Alpha) : color;
        if (Color(Attr(gradient, Draw + "start-color")) is not { } start0 || Color(Attr(gradient, Draw + "end-color")) is not { } end0)
            return null;
        var start = Dim(start0, Attr(gradient, Draw + "start-intensity"));
        var end = Dim(end0, Attr(gradient, Draw + "end-intensity"));
        var border = (float)Math.Clamp(Percent(Attr(gradient, Draw + "border")) ?? 0, 0, 0.99);
        var angle = Angle(Attr(gradient, Draw + "angle")) ?? 0;
        switch (Attr(gradient, Draw + "style"))
        {
            case "axial":
                return new ChartGradient([(0, start), (border / 2, start), (0.5f, end), (1 - border / 2, start), (1, start)], 90 - angle);
            case "radial" or "ellipsoid" or "square" or "rectangular":
                return new ChartGradient([(0, end), (1 - border, start), (1, start)], 0, Radial: true,
                    (float)Math.Clamp(Percent(Attr(gradient, Draw + "cx")) ?? 0.5, 0, 1), (float)Math.Clamp(Percent(Attr(gradient, Draw + "cy")) ?? 0.5, 0, 1));
            default:
                return new ChartGradient([(0, start), (border, start), (1, end)], 90 - angle);
        }
    }

    /// <summary>A draw:hatch: lines of draw:color every draw:distance, turned draw:rotation, single, double (crossed) or triple.</summary>
    private static ChartHatch? Hatch(XElement hatch, SKColor? background)
    {
        var color = Color(Attr(hatch, Draw + "color")) ?? SKColors.Black;
        var distance = Points(Attr(hatch, Draw + "distance")) is > 0 and var d ? Math.Clamp(d, 0.5f, 1000) : 4;
        var crossings = Attr(hatch, Draw + "style") switch { "double" => 2, "triple" => 3, _ => 1 };
        return new ChartHatch(color, background, distance, Angle(Attr(hatch, Draw + "rotation")) ?? 0, crossings);
    }

    /// <summary>An ODF angle: degrees with deg, rad or grad, else tenths of a degree (ODF 1.2's integers).</summary>
    private static double? Angle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        value = value.Trim();
        double? degrees = value.EndsWith("deg", StringComparison.Ordinal) ? Number(value[..^3])
            : value.EndsWith("grad", StringComparison.Ordinal) ? Number(value[..^4]) * 0.9
            : value.EndsWith("rad", StringComparison.Ordinal) ? Number(value[..^3]) * 180 / Math.PI
            : Number(value) / 10;
        return degrees is { } a && double.IsFinite(a) ? (a % 360 + 360) % 360 : null;
    }

    /// <summary>A percentage ("20%") as a fraction.</summary>
    private static double? Percent(string? value) =>
        value is { Length: > 1 } && value.EndsWith('%') && Number(value[..^1]) is { } p ? p / 100 : null;

    private static ChartTextStyle TextStyle(XElement? props, ChartTextStyle inherited)
    {
        if (props is null)
            return inherited;
        var family = Attr(props, Style + "font-name") ?? Attr(props, Fo + "font-family");
        return inherited with
        {
            Family = family is { Length: > 0 } ? Cut(family.Trim('\'', '"')) : inherited.Family,
            Size = Points(Attr(props, Fo + "font-size")) is > 0 and var size ? Math.Clamp(size, 1, 400) : inherited.Size,
            Bold = Attr(props, Fo + "font-weight") is { } weight ? weight is "bold" or "600" or "700" or "800" or "900" : inherited.Bold,
            Italic = Attr(props, Fo + "font-style") is { } slant ? slant is "italic" or "oblique" : inherited.Italic,
            Color = Color(Attr(props, Fo + "color")) ?? inherited.Color,
        };
    }

    // ───────────────────────── Values ─────────────────────────

    private static string? Attr(XElement? element, XName name) => (string?)element?.Attribute(name);

    private static bool On(XElement? props, string name) => Attr(props, C + name) == "true";

    private static double? Number(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;

    private static double? Clamp(double? value) => value is { } v ? Math.Clamp(v, -ChartReader.MaxValue, ChartReader.MaxValue) : null;

    private static float? Points(string? value) => OdfNames.Length(value) is { } hwp ? hwp / 100f : null;

    private static SKColor? Color(string? value) => value is { Length: 7 } && value[0] == '#' && SKColor.TryParse(value, out var color) ? color : null;

    private static string Cut(string text) => text.Length > ChartReader.MaxText ? text[..ChartReader.MaxText] : text;
}
