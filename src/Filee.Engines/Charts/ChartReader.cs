// A DrawingML chart part (c:chartSpace, ECMA-376 Part 1, 21.2.2) → the chart model ChartDrawer draws. Everything comes
// from what the part caches (c:strCache, c:numCache, c:multiLvlStrCache, literals), so no formula is evaluated and no
// workbook is opened: what Excel, Word and PowerPoint saved is what is drawn.
//
// Read: bar and column (clustered, stacked, 100 %), line (with markers, stacked), area, pie, doughnut and scatter plots,
// several in one plot area (a column and line combination on two value axes); the title (rich text with each run's
// look, or a cell's text, or the series name Office shows for a lone series), axis titles, category (also several
// levels), date and value axes with their scaling, number format, gridlines, tick marks, label position and crossing;
// the legend with deleted entries; data labels and single points' own labels (c:dLbl: text, what they show, format,
// position, separator); trendlines (c:trendline) and error bars (c:errBars); fills and outlines of the chart area,
// plot area, series and single points (c:spPr: solid, gradient, pattern, none) and the automatic colours the chart
// style (c:style) picks from the theme's accents for the rest; manual layouts of the plot area and legend.
// 3D charts, radar, bubble, stock, surface and bar-of-pie charts are named instead (LeftOut), and their documents say
// "charts" were left out, or take LibreOffice where the capability checks send them.
//
// Hostile input: the part comes through the package's XML limits; series per chart and points per chart (a cache's
// ptCount or highest c:idx, not only the points it holds) are bounded and a part beyond is refused with
// error.document_too_complex (custom error bar values and category levels count as points); texts are cut at MaxText
// characters and titles at MaxLines lines, rich text at MaxRuns runs a line, trendlines at MaxTrendlines a series and
// category levels at MaxLevels; numbers that aren't finite are blanks and the rest are clamped to ±MaxValue, so
// scaling can't overflow.

using System.Globalization;
using System.Xml.Linq;
using Filee.Engines.Documents;
using Filee.Engines.Office.Sheets;
using Filee.Engines.Slides;
using SkiaSharp;

namespace Filee.Engines.Charts;

/// <summary>A chart part read: the chart when it can be drawn, else what is left out ("charts").</summary>
internal readonly record struct ChartRead(Chart? Chart, string? LeftOut);

internal sealed class ChartReader
{
    public static readonly XNamespace C = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private static readonly XNamespace A = DrawingColors.A;

    /// <summary>Series in one chart at most (Excel allows 255).</summary>
    internal const int MaxSeries = 1024;

    /// <summary>Points of all series of one chart together at most (Excel's rows: a column of a whole sheet).</summary>
    internal const int MaxPoints = 1_048_576;

    /// <summary>Characters of one text (a title, a label, a series name) kept; the rest is cut.</summary>
    internal const int MaxText = 1024;

    /// <summary>Lines of one title kept.</summary>
    internal const int MaxLines = 16;

    /// <summary>The largest value magnitude kept; larger ones are clamped (Excel's largest number is about 1e308).</summary>
    internal const double MaxValue = 1e100;

    /// <summary>The plots ChartDrawer draws, by element name.</summary>
    private static readonly Dictionary<string, ChartKind> Kinds = new(StringComparer.Ordinal)
    {
        ["barChart"] = ChartKind.Bar,
        ["lineChart"] = ChartKind.Line,
        ["areaChart"] = ChartKind.Area,
        ["pieChart"] = ChartKind.Pie,
        ["doughnutChart"] = ChartKind.Doughnut,
        ["scatterChart"] = ChartKind.Scatter,
    };

    /// <summary>Plots that are named instead of drawn.</summary>
    private static readonly HashSet<string> Unsupported = new(StringComparer.Ordinal)
    {
        "bar3DChart", "line3DChart", "area3DChart", "pie3DChart", "radarChart", "bubbleChart", "stockChart", "surfaceChart",
        "surface3DChart", "ofPieChart",
    };

    private readonly DrawingColors _colors;
    private readonly string? _themeFont;
    private readonly NumberFormatter _formatter;
    private int _points;

    private ChartReader(DrawingColors colors, string? themeFont, bool date1904)
    {
        _colors = colors;
        _themeFont = themeFont;
        _formatter = new NumberFormatter(date1904);
    }

    /// <summary>The theme's body font (a:minorFont a:latin) of a theme part, or null.</summary>
    public static string? ThemeFont(XElement? theme) =>
        (string?)theme?.Descendants(A + "minorFont").FirstOrDefault()?.Element(A + "latin")?.Attribute("typeface") is { Length: > 0 } font ? font : null;

    /// <summary>
    /// Reads chart part <paramref name="root"/> (its c:chartSpace) with the host's theme colours and body font.
    /// </summary>
    /// <exception cref="Core.Conversion.ConversionRefusedException">The chart has more series or points than Filee draws.</exception>
    public static ChartRead Read(XElement? root, DrawingColors colors, string? themeFont = null)
    {
        if (root is null || root.Name != C + "chartSpace" || root.Element(C + "chart") is not { } chart)
            return new(null, "charts");
        var date1904 = Bool(root.Element(C + "date1904"), false);
        if (root.Element(C + "clrMapOvr") is { } map)
            colors = colors.WithMap(map);
        return new ChartReader(colors, themeFont, date1904).ReadChart(root, chart, date1904);
    }

    private ChartRead ReadChart(XElement root, XElement chart, bool date1904)
    {
        var plotArea = chart.Element(C + "plotArea");
        if (plotArea is null)
            return new(null, "charts");
        var text = Style(new ChartTextStyle(_themeFont, 10, false, false, SKColors.Black), DefaultRun(root.Element(C + "txPr")));
        var plots = new List<ChartPlot>();
        foreach (var element in plotArea.Elements())
        {
            var name = element.Name.LocalName;
            if (element.Name.Namespace != C)
                continue;
            if (Unsupported.Contains(name))
                return new(null, "charts");
            if (Kinds.TryGetValue(name, out var kind))
                plots.Add(Plot(element, kind, text));
        }
        if (plots.Count == 0 || plots.All(p => p.Series.Count == 0))
            return new(null, "charts");

        var series = plots.SelectMany(p => p.Series).ToList();
        var result = new Chart
        {
            Title = ChartTitle(chart, text, series),
            Legend = Legend(chart.Element(C + "legend"), text),
            Area = Shape(root.Element(C + "spPr")),
            PlotArea = Shape(plotArea.Element(C + "spPr")),
            PlotLayout = Layout(plotArea.Element(C + "layout")),
            RoundedCorners = Bool(root.Element(C + "roundedCorners"), true),
            BlanksAs = Val(chart.Element(C + "dispBlanksAs")) is ("zero" or "span") and var blanks ? blanks : "gap",
            Date1904 = date1904,
            Text = text,
            Palette = Palette(root, plots),
            ThemeFont = _themeFont,
        };
        result.Plots.AddRange(plots);
        foreach (var axis in plotArea.Elements())
        {
            if (axis.Name.Namespace == C && axis.Name.LocalName is "catAx" or "valAx" or "dateAx")
                result.Axes.Add(Axis(axis, text));
        }
        return new(result, null);
    }

    private static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private static readonly XNamespace C14 = "http://schemas.microsoft.com/office/drawing/2007/8/2/chart";

    /// <summary>
    /// The automatic colours the chart style (c:style 1 to 48, or c14:style 101 to 148) picks: its column of eight
    /// is greys (1), the theme's accents (2, Office's default) or shades of one accent (3 to 8), from darker to
    /// lighter across the series (or the points of a plot in varied colours).
    /// </summary>
    private ChartPalette Palette(XElement root, List<ChartPlot> plots)
    {
        var accents = Enumerable.Range(1, 6).Select(i => _colors.Scheme($"accent{i}")).ToList();
        var style = Number(root.Element(C + "style"))
            ?? root.Elements(Mc + "AlternateContent").Elements(Mc + "Choice").Elements(C14 + "style").Select(s => Number(s) - 100).FirstOrDefault(s => s is >= 1 and <= 48)
            ?? root.Elements(Mc + "AlternateContent").Elements(Mc + "Fallback").Elements(C + "style").Select(Number).FirstOrDefault();
        var column = style is >= 1 and <= 48 ? ((int)style - 1) % 8 : 1;
        if (column == 1)
            return new ChartPalette(accents);
        var baseColor = column == 0 ? new SKColor(0x80, 0x80, 0x80) : accents[column - 2];
        var count = Math.Clamp(plots.Max(p => p.VaryColors && p.Series.Count == 1 ? p.Series[0].Values.Count : p.Series.Count), 1, 64);
        baseColor.ToHsl(out var h, out var s, out var l);
        var shades = new List<SKColor>(count);
        for (var i = 0; i < count; i++)
        {
            // From half the colour's luminance (a shade) to halfway to white (a tint).
            var t = count == 1 ? 0 : -0.5 + i / (double)(count - 1);
            var luminance = t < 0 ? l * (1 + t) : l + (100 - l) * t;
            shades.Add(SKColor.FromHsl(h, s, (float)Math.Clamp(luminance, 0, 100)));
        }
        return new ChartPalette(shades);
    }

    // ───────────────────────── Plots and series ─────────────────────────

    private ChartPlot Plot(XElement element, ChartKind kind, ChartTextStyle text)
    {
        var grouping = Val(element.Element(C + "grouping")) switch
        {
            "stacked" => ChartGrouping.Stacked,
            "percentStacked" => ChartGrouping.PercentStacked,
            _ => ChartGrouping.Standard,
        };
        var scatterStyle = Val(element.Element(C + "scatterStyle")) ?? "marker";
        var plot = new ChartPlot
        {
            Kind = kind,
            Grouping = kind is ChartKind.Pie or ChartKind.Doughnut or ChartKind.Scatter ? ChartGrouping.Standard : grouping,
            Horizontal = Val(element.Element(C + "barDir")) == "bar",
            // Pies and doughnuts colour their slices unless the file says not to.
            VaryColors = Bool(element.Element(C + "varyColors"), kind is ChartKind.Pie or ChartKind.Doughnut ? element.Element(C + "varyColors") is null : false),
            GapWidth = (int)Math.Clamp(Number(element.Element(C + "gapWidth")) ?? 150, 0, 500),
            Overlap = (int)Math.Clamp(Number(element.Element(C + "overlap")) ?? (grouping == ChartGrouping.Standard ? 0 : 100), -100, 100),
            HoleSize = (int)Math.Clamp(Number(element.Element(C + "holeSize")) ?? 50, 1, 90),
            FirstSliceAngle = Math.Clamp(Number(element.Element(C + "firstSliceAng")) ?? 0, 0, 360),
            Lines = kind != ChartKind.Scatter || scatterStyle is "lineMarker" or "line" or "smooth" or "smoothMarker",
            Markers = kind == ChartKind.Line ? Bool(element.Element(C + "marker"), true)
                : kind == ChartKind.Scatter && scatterStyle is "lineMarker" or "marker" or "smoothMarker",
            Labels = Labels(element.Element(C + "dLbls"), text),
            AxisIds = [.. element.Elements(C + "axId").Select(a => (uint)Math.Clamp(Number(a) ?? 0, 0, uint.MaxValue))],
        };
        foreach (var ser in element.Elements(C + "ser"))
        {
            if (plot.Series.Count >= MaxSeries)
                throw DocumentLimits.Exceeded($"A chart has more than {DocumentLimits.Count(MaxSeries)} series.");
            plot.Series.Add(Series(ser, kind, text, plot.Series.Count));
        }
        // c:order sets the drawing order.
        var order = element.Elements(C + "ser").Select((s, i) => (Order: Number(s.Element(C + "order")) ?? i, Index: i)).ToList();
        if (order.Select(o => o.Order).Distinct().Count() == order.Count)
        {
            var sorted = order.OrderBy(o => o.Order).Select(o => plot.Series[o.Index]).ToList();
            plot.Series.Clear();
            plot.Series.AddRange(sorted);
        }
        return plot;
    }

    private ChartSeries Series(XElement ser, ChartKind kind, ChartTextStyle text, int position)
    {
        var scatter = kind == ChartKind.Scatter;
        var values = Numbers(ser.Element(C + (scatter ? "yVal" : "val")), out var format);
        IReadOnlyList<string?>? categories = null;
        IReadOnlyList<IReadOnlyList<string?>>? levels = null;
        IReadOnlyList<double?>? xValues = null;
        if (ser.Element(C + (scatter ? "xVal" : "cat")) is { } cat)
        {
            if (scatter && (cat.Element(C + "numRef") is not null || cat.Element(C + "numLit") is not null))
                xValues = Numbers(cat, out _);
            else
                (categories, levels) = Labels(cat);
        }
        var points = new Dictionary<int, ChartShapeStyle>();
        var explosion = new Dictionary<int, int>();
        if (Number(ser.Element(C + "explosion")) is { } whole)
            explosion[-1] = (int)Math.Clamp(whole, 0, 400);
        foreach (var point in ser.Elements(C + "dPt"))
        {
            if (Number(point.Element(C + "idx")) is not { } index || index < 0 || index >= MaxPoints)
                continue;
            points[(int)index] = Shape(point.Element(C + "spPr"));
            if (Number(point.Element(C + "explosion")) is { } pulled)
                explosion[(int)index] = (int)Math.Clamp(pulled, 0, 400);
        }
        var labels = Labels(ser.Element(C + "dLbls"), text);
        return new ChartSeries
        {
            Name = SeriesName(ser.Element(C + "tx")),
            Index = (int)Math.Clamp(Number(ser.Element(C + "idx")) ?? position, 0, MaxSeries * 64),
            Categories = categories,
            CategoryLevels = levels,
            XValues = xValues,
            Values = values,
            Format = format,
            Style = Shape(ser.Element(C + "spPr")),
            Points = points,
            Explosion = explosion,
            Marker = ser.Element(C + "marker") is { } marker ? Marker(marker) : null,
            Labels = labels,
            PointLabels = PointLabels(ser.Element(C + "dLbls"), labels, text),
            Smooth = Bool(ser.Element(C + "smooth"), false),
            Trendlines = [.. ser.Elements(C + "trendline").Take(MaxTrendlines).Select(t => Trendline(t, text))],
            ErrorBars = [.. ser.Elements(C + "errBars").Take(2).Select(e => ErrorBars(e, scatter))],
        };
    }

    /// <summary>Trendlines of one series kept at most.</summary>
    internal const int MaxTrendlines = 8;

    private ChartTrendline Trendline(XElement trendline, ChartTextStyle text)
    {
        var kind = Val(trendline.Element(C + "trendlineType")) switch
        {
            "exp" => TrendKind.Exponential,
            "log" => TrendKind.Logarithmic,
            "poly" => TrendKind.Polynomial,
            "power" => TrendKind.Power,
            "movingAvg" => TrendKind.MovingAverage,
            _ => TrendKind.Linear,
        };
        var label = trendline.Element(C + "trendlineLbl");
        var numberFormat = label?.Element(C + "numFmt");
        return new ChartTrendline(kind)
        {
            Order = (int)Math.Clamp(Number(trendline.Element(C + "order")) ?? 2, 2, 6),
            Period = (int)Math.Clamp(Number(trendline.Element(C + "period")) ?? 2, 2, 255),
            Forward = Math.Clamp(Number(trendline.Element(C + "forward")) ?? 0, 0, MaxValue),
            Backward = Math.Clamp(Number(trendline.Element(C + "backward")) ?? 0, 0, MaxValue),
            Intercept = Number(trendline.Element(C + "intercept")) is { } intercept ? Math.Clamp(intercept, -MaxValue, MaxValue) : null,
            ShowEquation = Bool(trendline.Element(C + "dispEq"), false),
            ShowRSquared = Bool(trendline.Element(C + "dispRSqr"), false),
            Line = Line(trendline.Element(C + "spPr")?.Element(A + "ln")),
            Format = numberFormat is not null && !Bool(numberFormat.Attribute("sourceLinked"), false) ? (string?)numberFormat.Attribute("formatCode") : null,
            Style = Style(text, DefaultRun(label?.Element(C + "txPr"))),
        };
    }

    private ChartErrorBars ErrorBars(XElement bars, bool scatter)
    {
        var amount = Val(bars.Element(C + "errValType")) switch
        {
            "percentage" => ErrorAmount.Percentage,
            "stdDev" => ErrorAmount.StandardDeviation,
            "stdErr" => ErrorAmount.StandardError,
            "cust" => ErrorAmount.Custom,
            _ => ErrorAmount.Fixed,
        };
        var type = Val(bars.Element(C + "errBarType")) ?? "both";
        var custom = amount == ErrorAmount.Custom;
        return new ChartErrorBars(amount, Math.Clamp(Number(bars.Element(C + "val")) ?? (amount == ErrorAmount.StandardDeviation ? 1 : amount == ErrorAmount.Percentage ? 5 : 0), 0, MaxValue))
        {
            AlongX = scatter && Val(bars.Element(C + "errDir")) == "x",
            Plus = type is "both" or "plus",
            Minus = type is "both" or "minus",
            EndCap = !Bool(bars.Element(C + "noEndCap"), false),
            PlusValues = custom && bars.Element(C + "plus") is { } plus ? Numbers(plus, out _) : null,
            MinusValues = custom && bars.Element(C + "minus") is { } minus ? Numbers(minus, out _) : null,
            Line = Line(bars.Element(C + "spPr")?.Element(A + "ln")),
        };
    }

    private ChartMarker Marker(XElement marker) => new(
        Val(marker.Element(C + "symbol")) ?? "auto",
        (float)Math.Clamp(Number(marker.Element(C + "size")) ?? 5, 2, 72),
        Shape(marker.Element(C + "spPr")));

    private string? SeriesName(XElement? tx)
    {
        if (tx is null)
            return null;
        if (tx.Element(C + "v") is { } literal)
            return Cut(literal.Value);
        var cache = tx.Element(C + "strRef")?.Element(C + "strCache");
        return cache?.Elements(C + "pt").FirstOrDefault()?.Element(C + "v")?.Value is { } name ? Cut(name) : null;
    }

    /// <summary>Levels of multi-level categories kept (Excel's are rarely more than three).</summary>
    internal const int MaxLevels = 8;

    /// <summary>Category labels of c:cat: text, numbers in their format, or the levels of several (innermost first).</summary>
    private (IReadOnlyList<string?>? Inner, IReadOnlyList<IReadOnlyList<string?>>? Outer) Labels(XElement cat)
    {
        if ((cat.Element(C + "strRef")?.Element(C + "strCache") ?? cat.Element(C + "strLit")) is { } strings)
            return (Points(strings, v => Cut(v)), null);
        if ((cat.Element(C + "numRef")?.Element(C + "numCache") ?? cat.Element(C + "numLit")) is { } numbers)
        {
            var code = (string?)numbers.Element(C + "formatCode");
            return (Points(numbers, v => Finite(v) is { } value ? Cut(_formatter.Format(value, code)) : Cut(v)), null);
        }
        if ((cat.Element(C + "multiLvlStrRef")?.Element(C + "multiLvlStrCache") ?? cat.Element(C + "multiLvlStrLit")) is { } levels
            && levels.Elements(C + "lvl").Take(MaxLevels).ToList() is [var inner, .. var outer])
        {
            var count = Count(levels);
            var first = Points(inner, v => Cut(v), count);
            // The outer levels: as long as the inner one, a label where each group starts.
            List<IReadOnlyList<string?>> rest = [.. outer.Select(level => (IReadOnlyList<string?>)Points(level, v => Cut(v), first.Count))];
            return (first, rest.Count > 0 ? rest : null);
        }
        return (null, null);
    }

    /// <summary>The numbers of c:val, c:yVal or c:xVal (blanks null), with their format code.</summary>
    private IReadOnlyList<double?> Numbers(XElement? element, out string? format)
    {
        format = null;
        var cache = element?.Element(C + "numRef")?.Element(C + "numCache") ?? element?.Element(C + "numLit");
        if (cache is null)
            return [];
        format = (string?)cache.Element(C + "formatCode") is { Length: > 0 } code && !NumberFormatter.IsGeneral(code) ? code : null;
        return Points(cache, Finite);
    }

    /// <summary>A cache's points by c:idx, as many as its c:ptCount or highest index says, counted against <see cref="MaxPoints"/>.</summary>
    private List<T?> Points<T>(XElement cache, Func<string, T?> value, int? count = null)
    {
        var points = new List<(int Index, string Value)>();
        var highest = -1;
        foreach (var pt in cache.Elements(C + "pt"))
        {
            if (Number(pt.Attribute("idx")) is not { } index || index < 0)
                continue;
            if (index >= MaxPoints)
                throw TooMany();
            points.Add(((int)index, pt.Element(C + "v")?.Value ?? ""));
            highest = Math.Max(highest, (int)index);
        }
        var length = (long)Math.Max(highest + 1, Math.Min(count ?? Count(cache), MaxPoints + 1L));
        if ((_points += (int)Math.Min(length, MaxPoints + 1L)) > MaxPoints)
            throw TooMany();
        var result = new List<T?>((int)length);
        for (var i = 0; i < length; i++)
            result.Add(default);
        foreach (var (index, text) in points)
            result[index] = value(text);
        return result;
    }

    private static int Count(XElement cache) => (int)Math.Clamp(Number(cache.Element(C + "ptCount")) ?? 0, 0, MaxPoints + 1L);

    private static Core.Conversion.ConversionRefusedException TooMany() =>
        DocumentLimits.Exceeded($"A chart has more than {DocumentLimits.Count(MaxPoints)} points.");

    private static double? Finite(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? Math.Clamp(value, -MaxValue, MaxValue)
            : null;

    // ───────────────────────── Axes ─────────────────────────

    private ChartAxis Axis(XElement axis, ChartTextStyle text)
    {
        var scaling = axis.Element(C + "scaling");
        var numberFormat = axis.Element(C + "numFmt");
        var txPr = axis.Element(C + "txPr");
        var rotation = Number(txPr?.Element(A + "bodyPr")?.Attribute("rot")) is { } rot && rot is > -5_400_001 and < 5_400_001 ? rot / 60000 : (double?)null;
        var crosses = Val(axis.Element(C + "crosses"));
        var value = axis.Name.LocalName == "valAx";
        return new ChartAxis
        {
            Id = (uint)Math.Clamp(Number(axis.Element(C + "axId")) ?? 0, 0, uint.MaxValue),
            IsValue = value,
            Position = Val(axis.Element(C + "axPos")) is ("l" or "r" or "t" or "b") and var position ? position : "b",
            Deleted = Bool(axis.Element(C + "delete"), false),
            Reversed = Val(scaling?.Element(C + "orientation")) == "maxMin",
            Min = Number(scaling?.Element(C + "min")) is { } min ? Math.Clamp(min, -MaxValue, MaxValue) : null,
            Max = Number(scaling?.Element(C + "max")) is { } max ? Math.Clamp(max, -MaxValue, MaxValue) : null,
            MajorUnit = Number(axis.Element(C + "majorUnit")) is > 0 and var unit ? Math.Min(unit, MaxValue) : null,
            LogBase = Number(scaling?.Element(C + "logBase")) is >= 2 and <= 1000 and var log ? log : null,
            Title = axis.Element(C + "title") is { } title ? Title(title, text with { Bold = true }, null) : null,
            Format = numberFormat is not null && !Bool(numberFormat.Attribute("sourceLinked"), false) ? (string?)numberFormat.Attribute("formatCode") : null,
            MajorGridlines = axis.Element(C + "majorGridlines") is { } major ? Line(major.Element(C + "spPr")?.Element(A + "ln")) ?? new ChartLine() : null,
            MinorGridlines = axis.Element(C + "minorGridlines") is { } minor ? Line(minor.Element(C + "spPr")?.Element(A + "ln")) ?? new ChartLine() : null,
            Line = Line(axis.Element(C + "spPr")?.Element(A + "ln")),
            LabelPosition = Val(axis.Element(C + "tickLblPos")) ?? "nextTo",
            MajorTicks = Val(axis.Element(C + "majorTickMark")) ?? "out",
            Crosses = axis.Element(C + "crossesAt") is not null ? ChartCrossing.At : crosses switch { "min" => ChartCrossing.Min, "max" => ChartCrossing.Max, _ => ChartCrossing.AutoZero },
            CrossesAt = Math.Clamp(Number(axis.Element(C + "crossesAt")) ?? 0, -MaxValue, MaxValue),
            CrossAxis = (uint)Math.Clamp(Number(axis.Element(C + "crossAx")) ?? 0, 0, uint.MaxValue),
            Between = Val(axis.Element(C + "crossBetween")) != "midCat",
            LabelSkip = (int)Math.Clamp(Number(axis.Element(C + "tickLblSkip")) ?? 0, 0, MaxPoints),
            LabelRotation = rotation,
            Style = Style(text, DefaultRun(txPr)),
        };
    }

    // ───────────────────────── Title, legend, labels ─────────────────────────

    private ChartTitle? ChartTitle(XElement chart, ChartTextStyle text, List<ChartSeries> series)
    {
        var deleted = Bool(chart.Element(C + "autoTitleDeleted"), false);
        var style = text with { Size = text.Size * 1.8f, Bold = true };
        var lone = series.Count == 1 && series[0].Name is { Length: > 0 } name ? name : null;
        if (chart.Element(C + "title") is { } title)
            return Title(title, style, lone ?? "Chart Title");
        // Office titles a chart of one named series with its name until the title is deleted.
        return !deleted && lone is not null ? new ChartTitle([lone], style) : null;
    }

    /// <summary>A c:title: its rich text or a cell's text, else <paramref name="automatic"/> (null: no title).</summary>
    private ChartTitle? Title(XElement title, ChartTextStyle style, string? automatic)
    {
        style = Style(style, DefaultRun(title.Element(C + "txPr")));
        var overlay = Bool(title.Element(C + "overlay"), false);
        var layout = Layout(title.Element(C + "layout"));
        var tx = title.Element(C + "tx");
        if (tx?.Element(C + "rich") is { } rich)
        {
            var (lines, runs, first) = RichLines(rich, style);
            // Runs are kept when they don't all look like the first one.
            var mixed = runs.Any(line => line.Any(run => run.Style != first || run.Raise != 0));
            return new ChartTitle(lines, first, overlay) { Layout = layout, Runs = mixed ? runs : null };
        }
        if (tx?.Element(C + "strRef")?.Element(C + "strCache")?.Elements(C + "pt").FirstOrDefault()?.Element(C + "v")?.Value is { } cell)
            return new ChartTitle([.. Cut(cell).Split('\n').Take(MaxLines)], style, overlay) { Layout = layout };
        return automatic is null ? null : new ChartTitle([automatic], style, overlay) { Layout = layout };
    }

    /// <summary>Runs of rich text kept per line at most.</summary>
    private const int MaxRuns = 64;

    /// <summary>
    /// The paragraphs of a c:rich (a:p, broken at a:br) as lines of text and of runs in their own styles, and the
    /// style of the first run (what the text is in when it's all alike).
    /// </summary>
    private (List<string> Lines, List<IReadOnlyList<ChartRun>> Runs, ChartTextStyle First) RichLines(XElement rich, ChartTextStyle style)
    {
        var lines = new List<string>();
        var runs = new List<IReadOnlyList<ChartRun>>();
        style = Style(style, DefaultRun(rich));
        ChartTextStyle? first = null;
        foreach (var paragraph in rich.Elements(A + "p").Take(MaxLines))
        {
            var paragraphStyle = Style(style, paragraph.Element(A + "pPr")?.Element(A + "defRPr"));
            var line = new System.Text.StringBuilder();
            var pieces = new List<ChartRun>();
            void Break()
            {
                lines.Add(Cut(line.ToString()));
                runs.Add(pieces.Count > 0 ? pieces : [new ChartRun("", paragraphStyle)]);
                line.Clear();
                pieces = [];
            }
            foreach (var run in paragraph.Elements())
            {
                if (run.Name == A + "br")
                {
                    if (lines.Count < MaxLines)
                        Break();
                    continue;
                }
                if (run.Name != A + "r" && run.Name != A + "fld")
                    continue;
                var runStyle = Style(paragraphStyle, run.Element(A + "rPr"));
                first ??= runStyle;
                var piece = run.Element(A + "t")?.Value ?? "";
                if (line.Length >= MaxText || pieces.Count >= MaxRuns)
                    continue;
                piece = piece.Length > MaxText - line.Length ? piece[..(MaxText - line.Length)] : piece;
                line.Append(piece);
                // A baseline shift (a:rPr baseline, thousandths of a percent of the size) raises or lowers the run, made smaller.
                var raise = Number(run.Element(A + "rPr")?.Attribute("baseline")) is { } baseline ? (float)Math.Clamp(baseline / 100_000 * 1.5, -1.5, 1.5) : 0;
                if (raise != 0)
                    runStyle = runStyle with { Size = runStyle.Size * 2 / 3 };
                pieces.Add(new ChartRun(piece, runStyle, raise));
            }
            if (lines.Count < MaxLines)
                Break();
        }
        return (lines, runs, first ?? style);
    }

    private ChartLegend? Legend(XElement? legend, ChartTextStyle text)
    {
        if (legend is null)
            return null;
        var deleted = new HashSet<int>();
        foreach (var entry in legend.Elements(C + "legendEntry"))
        {
            if (Bool(entry.Element(C + "delete"), false) && Number(entry.Element(C + "idx")) is { } index && index is >= 0 and < MaxPoints)
                deleted.Add((int)index);
        }
        return new ChartLegend
        {
            Position = Val(legend.Element(C + "legendPos")) is ("r" or "l" or "t" or "b" or "tr") and var position ? position : "r",
            Overlay = Bool(legend.Element(C + "overlay"), false),
            Deleted = deleted,
            Layout = Layout(legend.Element(C + "layout")),
            Style = Style(text, DefaultRun(legend.Element(C + "txPr"))),
        };
    }

    private ChartLabels? Labels(XElement? labels, ChartTextStyle text)
    {
        if (labels is null)
            return null;
        if (Bool(labels.Element(C + "delete"), false))
            return new ChartLabels(false, false, false, false, null, null, text);
        var format = labels.Element(C + "numFmt") is { } numFmt && !Bool(numFmt.Attribute("sourceLinked"), false) ? (string?)numFmt.Attribute("formatCode") : null;
        return new ChartLabels(
            Bool(labels.Element(C + "showVal"), false),
            Bool(labels.Element(C + "showCatName"), false),
            Bool(labels.Element(C + "showSerName"), false),
            Bool(labels.Element(C + "showPercent"), false),
            Val(labels.Element(C + "dLblPos")),
            format,
            Style(text, DefaultRun(labels.Element(C + "txPr"))))
        {
            Separator = Separator(labels),
        };
    }

    /// <summary>
    /// What goes between a label's parts (c:separator), or null. Excel writes a line break as the element's only text,
    /// which a part read without its whitespace loses: an empty separator is a line break.
    /// </summary>
    private static string? Separator(XElement labels) =>
        labels.Element(C + "separator") is { } separator ? separator.Value.Length == 0 ? "\n" : Cut(separator.Value) : null;

    /// <summary>
    /// The labels of single points (c:dLbl in c:dLbls): what each shows (the series' choice where it says nothing),
    /// its own text, position, format and style; a deleted one shows nothing.
    /// </summary>
    private Dictionary<int, ChartLabels> PointLabels(XElement? labels, ChartLabels? series, ChartTextStyle text)
    {
        var result = new Dictionary<int, ChartLabels>();
        if (labels is null)
            return result;
        var inherited = series ?? new ChartLabels(false, false, false, false, null, null, text);
        foreach (var label in labels.Elements(C + "dLbl"))
        {
            if (Number(label.Element(C + "idx")) is not { } index || index < 0 || index >= MaxPoints || result.Count >= MaxPoints)
                continue;
            if (Bool(label.Element(C + "delete"), false))
            {
                result[(int)index] = new ChartLabels(false, false, false, false, null, null, inherited.Style);
                continue;
            }
            var style = Style(inherited.Style, DefaultRun(label.Element(C + "txPr")));
            var numFmt = label.Element(C + "numFmt");
            result[(int)index] = new ChartLabels(
                Bool(label.Element(C + "showVal"), inherited.Value),
                Bool(label.Element(C + "showCatName"), inherited.Category),
                Bool(label.Element(C + "showSerName"), inherited.Series),
                Bool(label.Element(C + "showPercent"), inherited.Percent),
                Val(label.Element(C + "dLblPos")) ?? inherited.Position,
                numFmt is not null ? Bool(numFmt.Attribute("sourceLinked"), false) ? null : (string?)numFmt.Attribute("formatCode") : inherited.Format,
                style)
            {
                Separator = Separator(label) ?? inherited.Separator,
                Text = label.Element(C + "tx")?.Element(C + "rich") is { } rich ? RichLines(rich, style).Runs
                    : label.Element(C + "tx")?.Element(C + "strRef")?.Element(C + "strCache")?.Elements(C + "pt").FirstOrDefault()?.Element(C + "v")?.Value is { } cell
                        ? [.. Cut(cell).Split('\n').Take(MaxLines).Select(l => (IReadOnlyList<ChartRun>)[new ChartRun(l, style)])]
                        : null,
            };
        }
        return result;
    }

    private static ChartLayout? Layout(XElement? layout)
    {
        if (layout?.Element(C + "manualLayout") is not { } manual)
            return null;
        // Only boxes placed from the chart's edges are taken; factors of the automatic place are left to it.
        if (Val(manual.Element(C + "xMode")) is "factor" || Val(manual.Element(C + "yMode")) is "factor"
            || Number(manual.Element(C + "x")) is not { } x || Number(manual.Element(C + "y")) is not { } y)
            return null;
        static double? Fraction(double? value) => value is { } v ? Math.Clamp(v, 0, 1) : null;
        return new ChartLayout(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1), Fraction(Number(manual.Element(C + "w"))), Fraction(Number(manual.Element(C + "h"))),
            Val(manual.Element(C + "layoutTarget")) == "inner");
    }

    // ───────────────────────── Fills, lines, text ─────────────────────────

    private ChartShapeStyle Shape(XElement? properties) =>
        properties is null ? ChartShapeStyle.Auto : new ChartShapeStyle
        {
            Fill = Fill(properties),
            Gradient = Gradient(properties.Element(A + "gradFill")),
            Hatch = Pattern(properties.Element(A + "pattFill")),
            Line = Line(properties.Element(A + "ln")),
        };

    /// <summary>Colour stops of one gradient kept at most.</summary>
    private const int MaxStops = 64;

    /// <summary>An a:gradFill: its stops, along a:lin's angle or out from a:path's centre (a:fillToRect).</summary>
    private ChartGradient? Gradient(XElement? fill)
    {
        if (fill is null || fill.Parent?.Elements().FirstOrDefault(e => e.Name.Namespace == A && e.Name.LocalName is "noFill" or "solidFill" or "gradFill" or "pattFill") != fill)
            return null;
        var stops = new List<(float, SKColor)>();
        foreach (var gs in fill.Element(A + "gsLst")?.Elements(A + "gs").Take(MaxStops) ?? [])
        {
            if (_colors.Color(gs) is { } color)
                stops.Add(((float)Math.Clamp((Number(gs.Attribute("pos")) ?? 0) / 100_000, 0, 1), color));
        }
        if (stops.Count < 2)
            return null;
        stops.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        if (fill.Element(A + "path") is { } path)
        {
            var to = path.Element(A + "fillToRect");
            double Inset(string side) => Math.Clamp((Number(to?.Attribute(side)) ?? 50_000) / 100_000, -1, 2);
            // The stops run from the centre (position 0) out to the edge.
            return new ChartGradient(stops, 0, Radial: true,
                (float)Math.Clamp((Inset("l") + 1 - Inset("r")) / 2, 0, 1), (float)Math.Clamp((Inset("t") + 1 - Inset("b")) / 2, 0, 1));
        }
        var angle = Number(fill.Element(A + "lin")?.Attribute("ang")) is { } ang ? ang / 60_000 % 360 : 90;
        return new ChartGradient(stops, angle);
    }

    /// <summary>An a:pattFill as hatching (lines and grids) or its two colours mixed (dots and the rest).</summary>
    private ChartHatch? Pattern(XElement? fill)
    {
        if (fill is null || fill.Parent?.Elements().FirstOrDefault(e => e.Name.Namespace == A && e.Name.LocalName is "noFill" or "solidFill" or "gradFill" or "pattFill") != fill)
            return null;
        var fore = _colors.Color(fill.Element(A + "fgClr")) ?? SKColors.Black;
        var back = _colors.Color(fill.Element(A + "bgClr")) ?? SKColors.White;
        var preset = (string?)fill.Attribute("prst") ?? "pct5";
        if (preset.StartsWith("pct", StringComparison.Ordinal) && int.TryParse(preset.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var percent))
            return new ChartHatch(fore, back, 4, 0, Blend: Math.Clamp(percent, 0, 100) / 100f);
        var spacing = preset.StartsWith("nar", StringComparison.Ordinal) || preset.StartsWith("sm", StringComparison.Ordinal) || preset.StartsWith("dk", StringComparison.Ordinal) ? 2.5f
            : preset.StartsWith("wd", StringComparison.Ordinal) || preset.StartsWith("lg", StringComparison.Ordinal) ? 6f : 4f;
        return preset switch
        {
            "horz" or "ltHorz" or "dkHorz" or "narHorz" or "dashHorz" => new ChartHatch(fore, back, spacing, 0),
            "vert" or "ltVert" or "dkVert" or "narVert" or "dashVert" => new ChartHatch(fore, back, spacing, 90),
            "upDiag" or "ltUpDiag" or "dkUpDiag" or "wdUpDiag" or "dashUpDiag" => new ChartHatch(fore, back, spacing, 45),
            "dnDiag" or "ltDnDiag" or "dkDnDiag" or "wdDnDiag" or "dashDnDiag" => new ChartHatch(fore, back, spacing, -45),
            "cross" or "smGrid" or "lgGrid" or "dotGrid" => new ChartHatch(fore, back, spacing, 0, Crossings: 2),
            "diagCross" or "smCheck" or "lgCheck" or "openDmnd" or "dotDmnd" or "trellis" => new ChartHatch(fore, back, spacing, 45, Crossings: 2),
            _ => new ChartHatch(fore, back, 4, 0, Blend: 0.5f),
        };
    }

    private SKColor? Fill(XElement properties)
    {
        var fill = properties.Elements().FirstOrDefault(e => e.Name.Namespace == A && e.Name.LocalName is "noFill" or "solidFill" or "gradFill" or "pattFill");
        return fill?.Name.LocalName switch
        {
            "noFill" => SKColors.Transparent,
            "solidFill" => _colors.Color(fill),
            "gradFill" => _colors.Color(fill.Element(A + "gsLst")?.Elements(A + "gs").FirstOrDefault()),
            "pattFill" => _colors.Color(fill.Element(A + "fgClr")),
            _ => null,
        };
    }

    private ChartLine? Line(XElement? line)
    {
        if (line is null)
            return null;
        var fill = line.Elements().FirstOrDefault(e => e.Name.Namespace == A && e.Name.LocalName is "noFill" or "solidFill" or "gradFill" or "pattFill");
        if (fill?.Name.LocalName == "noFill")
            return ChartLine.Hidden;
        var color = fill?.Name.LocalName switch
        {
            "solidFill" => _colors.Color(fill),
            "gradFill" => _colors.Color(fill.Element(A + "gsLst")?.Elements(A + "gs").FirstOrDefault()),
            "pattFill" => _colors.Color(fill.Element(A + "fgClr")),
            _ => null,
        };
        var width = Number(line.Attribute("w")) is { } w ? (float)Math.Clamp(w / 12700, 0, 100) : (float?)null;
        var dash = Val(line.Element(A + "prstDash")) switch
        {
            "dot" or "sysDot" => [1f, 1.5f],
            "dash" or "sysDash" => [4f, 3f],
            "lgDash" => [8f, 3f],
            "dashDot" or "sysDashDot" => [4f, 3f, 1f, 3f],
            "lgDashDot" => [8f, 3f, 1f, 3f],
            "lgDashDotDot" or "sysDashDotDot" => [8f, 3f, 1f, 3f, 1f, 3f],
            _ => (float[]?)null,
        };
        return new ChartLine(color, width, dash);
    }

    /// <summary>The default run properties of a c:txPr (its first paragraph's a:defRPr).</summary>
    private static XElement? DefaultRun(XElement? txPr) => txPr?.Element(A + "p")?.Element(A + "pPr")?.Element(A + "defRPr");

    /// <summary><paramref name="style"/> with what run properties (a:rPr, a:defRPr) set.</summary>
    private ChartTextStyle Style(ChartTextStyle style, XElement? run)
    {
        if (run is null)
            return style;
        if (Number(run.Attribute("sz")) is { } size && size >= 100)
            style = style with { Size = (float)Math.Min(size / 100, 400) };
        if (run.Attribute("b") is { } bold)
            style = style with { Bold = Bool(bold, false) };
        if (run.Attribute("i") is { } italic)
            style = style with { Italic = Bool(italic, false) };
        if (run.Element(A + "solidFill") is { } fill && _colors.Color(fill) is { } color)
            style = style with { Color = color };
        if ((string?)(run.Element(A + "latin") ?? run.Element(A + "ea"))?.Attribute("typeface") is { Length: > 0 } family)
            style = style with { Family = family.StartsWith('+') ? _themeFont : family };
        return style;
    }

    // ───────────────────────── Values ─────────────────────────

    private static string? Val(XElement? element) => (string?)element?.Attribute("val");

    private static double? Number(XElement? element) => Number(element?.Attribute("val"));

    private static double? Number(XAttribute? attribute) =>
        double.TryParse((string?)attribute, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;

    /// <summary>A CT_Boolean: an element without val is true; <paramref name="absent"/> when there is no element.</summary>
    private static bool Bool(XElement? element, bool absent) => element is null ? absent : Bool(element.Attribute("val"), true);

    private static bool Bool(XAttribute? attribute, bool absent) => (string?)attribute switch
    {
        null => absent,
        "1" or "true" or "on" => true,
        _ => false,
    };

    private static string Cut(string text) => text.Length > MaxText ? text[..MaxText] : text;
}

/// <summary>
/// The points one document's charts draw together, each chart counted every time it is placed (a part placed many
/// times draws many times), so a small file can't make drawing take hours; beyond, the document is refused.
/// </summary>
internal sealed class ChartBudget
{
    /// <summary>Points drawn per document at most: ten full-sheet charts.</summary>
    internal const long MaxPoints = 10L * ChartReader.MaxPoints;

    private long _points;

    public void Charge(Chart chart)
    {
        if ((_points += Math.Max(1, chart.PointCount)) > MaxPoints)
            throw DocumentLimits.Exceeded($"The document's charts draw more than {DocumentLimits.Count(MaxPoints)} points.");
    }
}
