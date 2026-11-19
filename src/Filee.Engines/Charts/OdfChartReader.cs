// An OpenDocument chart (an embedded object whose content is office:chart, ODF 1.3 Part 3 §11) → the chart model
// ChartDrawer draws, so ODT, ODP and ODG charts are drawn the way Word and PowerPoint charts are. The values come from
// the chart's own table (the table:table inside chart:chart, which LibreOffice writes with every chart, its "local
// table"): a series' cell range address is looked up there, and a range into another table (a Calc chart's sheet)
// falls back to the local table's column (or row) of that series. No formula is evaluated.
//
// Read: bar and column (stacked, percent, horizontal with chart:vertical), line (symbols, cubic or B-spline curves),
// area, pie (circle), doughnut (ring) and scatter (X from chart:domain) plots, a series' own chart:class in a
// combination and its attached axis (primary or secondary Y); the title and subtitle, axis titles, axes (visible,
// labels shown, logarithmic, minimum, maximum, major interval, reversed, tick marks, label angle, gridlines), the
// legend's position, data labels (value, percent, category, series, position), fills and strokes of the chart area,
// the wall (plot area), series and data points (chart:repeated runs), pie offsets, and text properties. Series
// without a fill take LibreOffice's default palette. 3D charts, radar, bubble, stock, surface and Gantt charts are
// left out ("charts").
//
// Hostile input: the object comes through its package's XML limits; series per chart and points per chart are bounded
// as in ChartReader (refused with error.document_too_complex), the local table's cells (repeats counted before they
// are expanded), style chains and texts too, and values are clamped to ±ChartReader.MaxValue.

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
    };

    private readonly Dictionary<string, XElement> _styles = new(StringComparer.Ordinal);
    private readonly List<List<Cell>> _table = [];
    private string? _tableName;
    private long _points;

    /// <summary>A cell of the chart's table: its text and, for a number, its value.</summary>
    private readonly record struct Cell(string Text, double? Value);

    private OdfChartReader()
    {
    }

    /// <summary>Whether <paramref name="content"/> (an object's content.xml root, or an inline office:document) is a chart.</summary>
    public static bool IsChart(XElement? content) => ChartElement(content) is not null;

    private static XElement? ChartElement(XElement? content) =>
        content?.Element(Office + "body")?.Element(Office + "chart")?.Element(C + "chart");

    /// <summary>
    /// Reads the chart object <paramref name="content"/> (its content.xml root, or an inline office:document) with
    /// <paramref name="styles"/> (its styles.xml root, or null).
    /// </summary>
    /// <exception cref="Core.Conversion.ConversionRefusedException">The chart has more series, points or cells than Filee draws.</exception>
    public static ChartRead Read(XElement? content, XElement? styles = null)
    {
        if (ChartElement(content) is not { } chart)
            return new(null, "charts");
        var reader = new OdfChartReader();
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
        if (On(plotProps, "three-dimensional"))
            return new(null, "charts");

        var text = TextStyle(Props(chart, "text-properties"), new ChartTextStyle(null, 10, false, false, SKColors.Black));
        var axes = plotArea.Elements(C + "axis").ToList();
        var xAxis = axes.FirstOrDefault(a => (string?)a.Attribute(C + "dimension") == "x");
        // One list of category labels, shared by every series.
        IReadOnlyList<string?>? categories = Range((string?)xAxis?.Element(C + "categories")?.Attribute(Table + "cell-range-address"), 0, byColumn: true)
            ?.Select(c => (string?)Cut(c.Text)).ToList();
        var horizontal = On(plotProps, "vertical");
        var seriesSource = Attr(plotProps, C + "series-source") ?? "columns";

        // Plots by kind and axis, in the order their first series comes.
        var plots = new List<(ChartKind Kind, bool Secondary, ChartPlot Plot)>();
        var position = 0;
        foreach (var element in plotArea.Elements(C + "series"))
        {
            var seriesKind = Kinds.TryGetValue((string?)element.Attribute(C + "class") ?? "", out var own) ? own
                : element.Attribute(C + "class") is null ? kind : (ChartKind?)null;
            if (seriesKind is null)
                return new(null, "charts"); // a series of a kind not drawn (stock, bubble, ...)
            if (position >= ChartReader.MaxSeries)
                throw DocumentLimits.Exceeded($"A chart has more than {DocumentLimits.Count(ChartReader.MaxSeries)} series.");
            var secondary = (string?)element.Attribute(C + "attached-axis") == "secondary-y" && seriesKind is not (ChartKind.Pie or ChartKind.Doughnut);
            var entry = plots.FirstOrDefault(p => p.Kind == seriesKind && p.Secondary == secondary);
            if (entry.Plot is null)
            {
                entry = (seriesKind.Value, secondary, Plot(seriesKind.Value, plotProps, horizontal, secondary, text));
                plots.Add(entry);
            }
            entry.Plot.Series.Add(Series(element, seriesKind.Value, categories, position, seriesSource, text));
            position++;
        }
        if (plots.Count == 0 || plots.All(p => p.Plot.Series.All(s => s.Values.Count == 0)))
            return new(null, "charts");

        var wall = plotArea.Element(C + "wall");
        var result = new Chart
        {
            Title = Title(chart.Element(C + "title"), chart.Element(C + "subtitle"), text with { Size = 13 }),
            Legend = Legend(chart.Element(C + "legend"), text),
            Area = Shape(Props(chart, "graphic-properties")) is { Fill: not null } area ? area : new ChartShapeStyle { Fill = SKColors.White, Line = ChartLine.Hidden },
            PlotArea = wall is null ? new ChartShapeStyle { Fill = SKColors.Transparent, Line = ChartLine.Hidden } : Shape(Props(wall, "graphic-properties")),
            RoundedCorners = false,
            Text = text,
            Palette = Palette,
        };
        result.Plots.AddRange(plots.Select(p => p.Plot));
        if (plots.Any(p => p.Kind is not (ChartKind.Pie or ChartKind.Doughnut)))
            AddAxes(result, axes, horizontal, plots.Any(p => p.Kind == ChartKind.Scatter), plots.Any(p => p.Secondary), text);
        return new(result, null);
    }

    // ───────────────────────── Plots and series ─────────────────────────

    private ChartPlot Plot(ChartKind kind, XElement? props, bool horizontal, bool secondary, ChartTextStyle text) => new()
    {
        Kind = kind,
        Grouping = kind is ChartKind.Pie or ChartKind.Doughnut or ChartKind.Scatter ? ChartGrouping.Standard
            : On(props, "percentage") ? ChartGrouping.PercentStacked : On(props, "stacked") ? ChartGrouping.Stacked : ChartGrouping.Standard,
        Horizontal = kind == ChartKind.Bar && horizontal,
        VaryColors = kind is ChartKind.Pie or ChartKind.Doughnut,
        GapWidth = (int)Math.Clamp(Number(Attr(props, C + "gap-width")) ?? 100, 0, 500),
        Overlap = (int)Math.Clamp(Number(Attr(props, C + "overlap")) ?? (On(props, "stacked") || On(props, "percentage") ? 100 : 0), -100, 100),
        HoleSize = 50,
        Lines = kind != ChartKind.Scatter || Attr(props, C + "lines") is not "false",
        Markers = Attr(props, C + "symbol-type") is { } symbols && symbols != "none" && kind is ChartKind.Line or ChartKind.Scatter
            || kind == ChartKind.Scatter && Attr(props, C + "lines") == "false",
        Labels = Labels(props, text),
        AxisIds = secondary ? [3u, 4u] : [1u, 2u],
    };

    private ChartSeries Series(XElement element, ChartKind kind, IReadOnlyList<string?>? categories, int position, string seriesSource, ChartTextStyle text)
    {
        var byColumn = seriesSource != "rows";
        var values = Range((string?)element.Attribute(C + "values-cell-range-address"), position + 1, byColumn) ?? [];
        Count(values.Count);
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
        return new ChartSeries
        {
            Name = label is [var cell, ..] ? Cut(cell.Text) : null,
            Index = position,
            Categories = kind == ChartKind.Scatter ? null : categories,
            XValues = xValues,
            Values = [.. values.Select(c => c.Value)],
            // A pie's or ring's slices vary by point: a slice without a style of its own takes the palette, not the series' fill.
            Style = kind is ChartKind.Pie or ChartKind.Doughnut ? shape with { Fill = null } : shape,
            Points = points,
            Explosion = explosion,
            Marker = symbol is null ? null : new ChartMarker(symbol == "none" ? "none" : Symbol(Attr(props, C + "symbol-name")),
                (float)Math.Clamp(Points(Attr(props, C + "symbol-width")) ?? 5, 2, 72), shape),
            Labels = Labels(props, text),
            Smooth = Attr(props, C + "interpolation") is "cubic-spline" or "b-spline",
        };
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

    private ChartLegend? Legend(XElement? legend, ChartTextStyle text) => legend is null ? null : new ChartLegend
    {
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
                    throw DocumentLimits.Exceeded($"A chart's table has more than {DocumentLimits.Count(MaxCells)} cells.");
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
        if (string.IsNullOrWhiteSpace(address) || _table.Count == 0)
            return null;
        var cells = new List<Cell>();
        foreach (var part in address.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var ends = part.Split(':');
            if (ends.Length > 2 || Reference(ends[0]) is not { } start)
                return null;
            var end = ends.Length == 2 ? Reference(ends[1]) ?? start : start;
            var tableName = start.Table ?? end.Table;
            if (tableName is not null && _tableName is not null && tableName != _tableName)
            {
                // A range into a sheet: the series' place in the chart's own table instead.
                return label ? Fallback(fallback, byColumn, header: true) : Fallback(fallback, byColumn, header: false);
            }
            var (top, bottom) = (Math.Min(start.Row, end.Row), Math.Max(start.Row, end.Row));
            var (left, right) = (Math.Min(start.Column, end.Column), Math.Max(start.Column, end.Column));
            if ((long)(bottom - top + 1) * (right - left + 1) + cells.Count > ChartReader.MaxPoints)
                throw DocumentLimits.Exceeded($"A chart has more than {DocumentLimits.Count(ChartReader.MaxPoints)} points.");
            for (var r = top; r <= bottom && r < _table.Count; r++)
            {
                for (var c = left; c <= right; c++)
                    cells.Add(At(r, c) ?? new Cell("", null));
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

    private static ChartShapeStyle Shape(XElement? graphic)
    {
        if (graphic is null)
            return ChartShapeStyle.Auto;
        SKColor? fill = Attr(graphic, Draw + "fill") switch
        {
            "none" => SKColors.Transparent,
            "gradient" or "hatch" or "bitmap" => Color(Attr(graphic, Draw + "fill-color")),
            _ => Color(Attr(graphic, Draw + "fill-color")),
        };
        var stroke = Attr(graphic, Draw + "stroke");
        ChartLine? line = stroke == "none" ? ChartLine.Hidden
            : Color(Attr(graphic, Svg + "stroke-color")) is { } color || Points(Attr(graphic, Svg + "stroke-width")) is > 0
                ? new ChartLine(Color(Attr(graphic, Svg + "stroke-color")), Points(Attr(graphic, Svg + "stroke-width")) is > 0 and var width ? Math.Min(width, 100) : null,
                    stroke == "dash" ? [4, 3] : null)
                : null;
        return new ChartShapeStyle { Fill = fill, Line = line };
    }

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
