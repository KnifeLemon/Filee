// An Office 2016 chart part (cx:chartSpace, the chartex schema of [MS-ODRAWXML] 2.24) → the same chart model: waterfall,
// funnel, treemap, sunburst and box and whisker plots of their own kind, histograms and Pareto charts as columns (the
// bins counted here, or the categories added up) with the Pareto line on a percentage axis. The values come from the
// part's cached data (cx:chartData cx:data: cx:strDim and cx:numDim levels), so no formula is evaluated. Read: the
// title, legend, axes (scaling, gridlines, number format, hidden), series names, fills and outlines of series and
// points (cx:dataPt), data labels (what they show, position, format) and each type's layout properties (subtotals,
// binning, quartile method and what a box shows, parent label layout). Region maps (geography) and kinds Filee
// doesn't know are not read ("charts"): the host then draws the picture or shape Office saved as the fallback.
//
// Hostile input: as for c:chartSpace (points per chart counted against MaxPoints, series against MaxSeries, texts cut,
// numbers clamped); bins are at most MaxBins, hierarchy levels at most MaxLevels.

using System.Globalization;
using System.Xml.Linq;
using Filee.Engines.Documents;
using Filee.Engines.Office.Sheets;
using SkiaSharp;

namespace Filee.Engines.Charts;

internal sealed partial class ChartReader
{
    public static readonly XNamespace Cx = "http://schemas.microsoft.com/office/drawing/2014/chartex";

    /// <summary>Bins of one histogram at most.</summary>
    internal const int MaxBins = 1000;

    /// <summary>The layouts (cx:series layoutId) read, by name.</summary>
    private static readonly Dictionary<string, ChartKind> ExKinds = new(StringComparer.Ordinal)
    {
        ["waterfall"] = ChartKind.Waterfall,
        ["funnel"] = ChartKind.Funnel,
        ["treemap"] = ChartKind.Treemap,
        ["sunburst"] = ChartKind.Sunburst,
        ["boxWhisker"] = ChartKind.BoxWhisker,
        ["clusteredColumn"] = ChartKind.Bar,
        ["paretoLine"] = ChartKind.Line,
    };

    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>
    /// The mc:Choice of mc:AlternateContent <paramref name="alternate"/> that holds an Office 2016 chart (a cx:chart, the
    /// hosts' way of saving chartex with a picture or shape to fall back on), and the chart's relationship id; null when
    /// none does. The hosts draw the choice when <see cref="Read"/> reads the chart part, else the fallback.
    /// </summary>
    internal static (XElement Choice, string? RelationshipId)? ChartExChoice(XElement alternate)
    {
        foreach (var choice in alternate.Elements())
        {
            if (choice.Name.LocalName == "Choice" && choice.Descendants(Cx + "chart").FirstOrDefault() is { } chart)
                return (choice, (string?)chart.Attribute(Relationships + "id"));
        }
        return null;
    }

    private ChartRead ReadChartEx(XElement root)
    {
        var chart = root.Element(Cx + "chart");
        var plotArea = chart?.Element(Cx + "plotArea");
        var region = plotArea?.Element(Cx + "plotAreaRegion");
        if (chart is null || plotArea is null || region is null)
            return new(null, "charts");
        var text = Style(new ChartTextStyle(_themeFont, 10, false, false, SKColors.Black), DefaultRun(root.Element(Cx + "txPr")));
        var data = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var set in root.Element(Cx + "chartData")?.Elements(Cx + "data") ?? [])
            data.TryAdd((string?)set.Attribute("id") ?? "", set);

        var elements = region.Elements(Cx + "series").Where(s => !Bool(s.Attribute("hidden"), false)).ToList();
        if (elements.Count == 0 || elements.Count > MaxSeries)
            return elements.Count == 0 ? new(null, "charts") : throw DocumentLimits.Exceeded($"A chart has more than {DocumentLimits.Count(MaxSeries)} series.");
        var layouts = elements.Select(e => (string?)e.Attribute("layoutId") ?? "").ToList();
        if (layouts.Any(l => !ExKinds.ContainsKey(l)))
            return new(null, "charts");
        var main = ExKinds[layouts.First(l => l != "paretoLine")];
        if (layouts.Any(l => l != "paretoLine" && ExKinds[l] != main))
            return new(null, "charts");

        var axes = plotArea.Elements(Cx + "axis").Take(8).ToList();
        var categoryAxis = axes.FirstOrDefault(a => a.Element(Cx + "catScaling") is not null);
        var valueAxes = axes.Where(a => a.Element(Cx + "valScaling") is not null).ToList();
        var gap = Number(categoryAxis?.Element(Cx + "catScaling")?.Attribute("gapWidth"));
        var plot = new ChartPlot
        {
            Kind = main,
            VaryColors = main is ChartKind.Treemap or ChartKind.Sunburst,
            GapWidth = (int)Math.Clamp((gap ?? (main == ChartKind.Bar ? 0 : main == ChartKind.Funnel ? 0.06 : 1.0)) * 100, 0, 500),
            Horizontal = main == ChartKind.Funnel,
            AxisIds = AxisIds(elements.First(e => (string?)e.Attribute("layoutId") != "paretoLine"), categoryAxis, valueAxes.FirstOrDefault()),
        };
        var layout = elements[0].Element(Cx + "layoutPr");
        plot = Configured(plot, layout);
        var series = new List<ChartSeries>();
        for (var i = 0; i < elements.Count; i++)
        {
            if (layouts[i] != "paretoLine")
                series.Add(ExSeries(elements[i], data, text, i));
        }
        plot.Series.AddRange(series);

        var result = new Chart
        {
            Title = ExTitle(chart.Element(Cx + "title"), text with { Size = text.Size * 1.4f }),
            Legend = ExLegend(chart.Element(Cx + "legend"), text),
            Area = Shape(root.Element(Cx + "spPr")),
            PlotArea = Shape(plotArea.Element(Cx + "spPr")),
            RoundedCorners = false,
            Text = text,
            Palette = new ChartPalette([.. Enumerable.Range(1, 6).Select(i => _colors.Scheme($"accent{i}"))]),
            ThemeFont = _themeFont,
        };
        if (main == ChartKind.Bar)
        {
            // A histogram (bins counted) or a Pareto chart (categories added up, largest first) with its line.
            var pareto = elements.FirstOrDefault(e => (string?)e.Attribute("layoutId") == "paretoLine");
            var (columns, line) = Histogram(series[0], layout, pareto is not null);
            plot.Series.Clear();
            plot.Series.Add(columns);
            result.Plots.Add(plot);
            if (pareto is not null && line is not null)
            {
                var percentAxis = valueAxes.Skip(1).FirstOrDefault();
                var lines = new ChartPlot
                {
                    Kind = ChartKind.Line,
                    Markers = false,
                    AxisIds = [plot.AxisIds[0], AxisId(percentAxis, 2)],
                };
                lines.Series.Add(new ChartSeries
                {
                    Name = columns.Name,
                    Index = 1,
                    Categories = columns.Categories,
                    Values = line,
                    Format = "0%",
                    Style = Shape(pareto.Element(Cx + "spPr")),
                });
                result.Plots.Add(lines);
                if (percentAxis is not null)
                    result.Axes.Add(ExAxis(percentAxis, text, value: true, "r") with { Format = "0%", Min = 0, Max = 1 });
            }
        }
        else
        {
            result.Plots.Add(plot);
        }
        if (categoryAxis is not null)
        {
            // A funnel's first stage is at the top, its labels beside the bars without an axis line.
            var axis = ExAxis(categoryAxis, text, value: false, main == ChartKind.Funnel ? "l" : "b");
            result.Axes.Add(main == ChartKind.Funnel ? axis with { Reversed = true, Line = axis.Line ?? ChartLine.Hidden, MajorTicks = "none" } : axis);
        }
        if (valueAxes.FirstOrDefault() is { } valueAxis)
            result.Axes.Add(ExAxis(valueAxis, text, value: true, main == ChartKind.Funnel ? "b" : "l"));
        return new(result, null);
    }

    private static uint AxisId(XElement? axis, uint fallback) =>
        Number(axis?.Attribute("id")) is { } id && id is >= 0 and < uint.MaxValue ? (uint)id : fallback;

    /// <summary>The axes a chartex series is drawn against: the category axis, then its own value axis (cx:axisId) or the first.</summary>
    private static List<uint> AxisIds(XElement series, XElement? category, XElement? value)
    {
        var own = series.Elements(Cx + "axisId").Select(a => Number(a)).OfType<double>().Where(v => v is >= 0 and < uint.MaxValue).Select(v => (uint)v).ToList();
        var categoryId = AxisId(category, 0);
        var valueId = own.FirstOrDefault(id => id != categoryId, AxisId(value, 1));
        return [categoryId, valueId];
    }

    /// <summary>The plot with its type's layout properties (cx:layoutPr).</summary>
    private static ChartPlot Configured(ChartPlot plot, XElement? layout)
    {
        var visibility = layout?.Element(Cx + "visibility");
        var copy = new ChartPlot
        {
            Kind = plot.Kind,
            VaryColors = plot.VaryColors,
            GapWidth = plot.GapWidth,
            Horizontal = plot.Horizontal,
            AxisIds = plot.AxisIds,
            Subtotals = layout?.Element(Cx + "subtotals")?.Elements(Cx + "idx").Take(MaxPoints).Select(i => Number(i)).OfType<double>()
                .Where(i => i is >= 0 and < MaxPoints).Select(i => (int)i).ToHashSet() ?? [],
            ConnectorLines = Bool(visibility?.Attribute("connectorLines"), true),
            ParentLabels = Val(layout?.Element(Cx + "parentLabelLayout")) is ("none" or "banner" or "overlapping") and var labels ? labels : "overlapping",
            ExclusiveQuartiles = (string?)layout?.Element(Cx + "statistics")?.Attribute("quartileMethod") != "inclusive",
            MeanLine = Bool(visibility?.Attribute("meanLine"), false),
            MeanMarker = Bool(visibility?.Attribute("meanMarker"), true),
            InnerPoints = Bool(visibility?.Attribute("nonoutliers"), false),
            Outliers = Bool(visibility?.Attribute("outliers"), true),
        };
        return copy;
    }

    private ChartSeries ExSeries(XElement element, Dictionary<string, XElement> data, ChartTextStyle text, int position)
    {
        var set = data.GetValueOrDefault(Val(element.Element(Cx + "dataId")) ?? "");
        var dims = set?.Elements().Where(e => e.Name == Cx + "strDim" || e.Name == Cx + "numDim").ToList() ?? [];
        var values = dims.FirstOrDefault(d => d.Name == Cx + "numDim" && (string?)d.Attribute("type") is "val" or "size")
                     ?? dims.FirstOrDefault(d => d.Name == Cx + "numDim" && (string?)d.Attribute("type") != "cat");
        var category = dims.FirstOrDefault(d => (string?)d.Attribute("type") == "cat");
        var numbers = values?.Element(Cx + "lvl") is { } level ? ExPoints(level, Finite) : [];
        var format = (string?)values?.Element(Cx + "lvl")?.Attribute("formatCode") is { Length: > 0 } code && !NumberFormatter.IsGeneral(code) ? code : null;
        IReadOnlyList<string?>? categories = null;
        List<IReadOnlyList<string?>>? levels = null;
        if (category is not null)
        {
            var all = category.Elements(Cx + "lvl").Take(MaxLevels).ToList();
            var count = Math.Max(numbers.Count, all.Count == 0 ? 0 : ExCount(all[0]));
            var read = all.Select(l => (IReadOnlyList<string?>)(category.Name == Cx + "numDim"
                ? ExPoints(l, v => Finite(v) is { } n ? Cut(_formatter.Format(n, (string?)l.Attribute("formatCode"))) : Cut(v), count)
                : ExPoints(l, v => Cut(v), count))).ToList();
            if (read.Count > 0)
            {
                categories = read[0];
                levels = read.Count > 1 ? read.Skip(1).ToList() : null;
            }
        }
        var points = new Dictionary<int, ChartShapeStyle>();
        foreach (var point in element.Elements(Cx + "dataPt").Take(MaxPoints))
        {
            if (Number(point.Attribute("idx")) is { } index && index is >= 0 and < MaxPoints)
                points[(int)index] = Shape(point.Element(Cx + "spPr"));
        }
        return new ChartSeries
        {
            Name = ExText(element.Element(Cx + "tx")),
            Index = position,
            Categories = categories,
            CategoryLevels = levels,
            Values = numbers,
            Format = format,
            Style = Shape(element.Element(Cx + "spPr")),
            Points = points,
            Labels = ExLabels(element.Element(Cx + "dataLabels"), text),
        };
    }

    /// <summary>A level's points by idx, as many as its ptCount says; each counts against <see cref="MaxPoints"/>.</summary>
    private List<T?> ExPoints<T>(XElement level, Func<string, T?> value, int? count = null)
    {
        var points = new List<(int Index, string Value)>();
        var highest = -1;
        foreach (var pt in level.Elements(Cx + "pt"))
        {
            if (Number(pt.Attribute("idx")) is not { } index || index < 0)
                continue;
            if (index >= MaxPoints)
                throw TooMany();
            points.Add(((int)index, pt.Value));
            highest = Math.Max(highest, (int)index);
        }
        var length = Math.Max(highest + 1, Math.Min(count ?? ExCount(level), MaxPoints + 1));
        if ((_points += Math.Min(length, MaxPoints + 1)) > MaxPoints)
            throw TooMany();
        var result = new List<T?>(length);
        for (var i = 0; i < length; i++)
            result.Add(default);
        foreach (var (index, text) in points)
            result[index] = value(text);
        return result;
    }

    private static int ExCount(XElement level) => (int)Math.Clamp(Number(level.Attribute("ptCount")) ?? 0, 0, MaxPoints + 1L);

    /// <summary>A cx:tx: its cached text (cx:txData cx:v) or rich text, or null.</summary>
    private string? ExText(XElement? tx)
    {
        if (tx?.Element(Cx + "txData")?.Element(Cx + "v")?.Value is { } cached)
            return Cut(cached);
        if (tx?.Element(Cx + "rich") is { } rich)
            return Cut(string.Join("\n", rich.Elements(A + "p").Take(MaxLines).Select(p => string.Concat(p.Descendants(A + "t").Select(t => t.Value)))));
        return null;
    }

    private ChartTitle? ExTitle(XElement? title, ChartTextStyle style)
    {
        if (title is null)
            return null;
        style = Style(style, DefaultRun(title.Element(Cx + "txPr")));
        var overlay = Bool(title.Attribute("overlay"), false);
        if (title.Element(Cx + "tx")?.Element(Cx + "rich") is { } rich)
        {
            var (lines, runs, first) = RichLines(rich, style);
            var mixed = runs.Any(line => line.Any(run => run.Style != first || run.Raise != 0));
            return new ChartTitle(lines, first, overlay) { Runs = mixed ? runs : null };
        }
        return ExText(title.Element(Cx + "tx")) is { } text ? new ChartTitle([.. text.Split('\n').Take(MaxLines)], style, overlay) : null;
    }

    private ChartLegend? ExLegend(XElement? legend, ChartTextStyle text) => legend is null ? null : new ChartLegend
    {
        Position = (string?)legend.Attribute("pos") is ("r" or "l" or "t" or "b") and var position ? position : "t",
        Overlay = Bool(legend.Attribute("overlay"), false),
        Style = Style(text, DefaultRun(legend.Element(Cx + "txPr"))),
    };

    private ChartLabels? ExLabels(XElement? labels, ChartTextStyle text)
    {
        if (labels is null)
            return null;
        var visibility = labels.Element(Cx + "visibility");
        var numFmt = labels.Element(Cx + "numFmt");
        return new ChartLabels(
            Bool(visibility?.Attribute("value"), true),
            Bool(visibility?.Attribute("categoryName"), false),
            Bool(visibility?.Attribute("seriesName"), false),
            false,
            (string?)labels.Attribute("pos"),
            numFmt is not null && !Bool(numFmt.Attribute("sourceLinked"), false) ? (string?)numFmt.Attribute("formatCode") : null,
            Style(text, DefaultRun(labels.Element(Cx + "txPr"))))
        {
            Separator = labels.Element(Cx + "separator")?.Value is { } separator ? separator.Length == 0 ? "\n" : Cut(separator) : null,
        };
    }

    private ChartAxis ExAxis(XElement axis, ChartTextStyle text, bool value, string position)
    {
        var scaling = axis.Element(value ? Cx + "valScaling" : Cx + "catScaling");
        double? Bound(string name) => Number(scaling?.Attribute(name)) is { } v ? Math.Clamp(v, -MaxValue, MaxValue) : null;
        var numberFormat = axis.Element(Cx + "numFmt");
        var labelStyle = Style(text, DefaultRun(axis.Element(Cx + "txPr")));
        return new ChartAxis
        {
            Id = AxisId(axis, value ? 1u : 0u),
            IsValue = value,
            Position = position,
            Deleted = Bool(axis.Attribute("hidden"), false),
            Min = value ? Bound("min") : null,
            Max = value ? Bound("max") : null,
            MajorUnit = value && Bound("majorUnit") is > 0 and var unit ? unit : null,
            Title = axis.Element(Cx + "title") is { } title ? ExTitle(title, text with { Bold = true }) : null,
            Format = numberFormat is not null && !Bool(numberFormat.Attribute("sourceLinked"), false) ? (string?)numberFormat.Attribute("formatCode") : null,
            MajorGridlines = axis.Element(Cx + "majorGridlines") is { } major ? Line(major.Element(Cx + "spPr")?.Element(A + "ln")) ?? new ChartLine() : null,
            MinorGridlines = axis.Element(Cx + "minorGridlines") is { } minor ? Line(minor.Element(Cx + "spPr")?.Element(A + "ln")) ?? new ChartLine() : null,
            Line = Line(axis.Element(Cx + "spPr")?.Element(A + "ln")) ?? (value ? ChartLine.Hidden : null),
            LabelPosition = axis.Element(Cx + "tickLabels") is null ? "none" : "nextTo",
            MajorTicks = (string?)axis.Element(Cx + "majorTickMarks")?.Attribute("type") is ("in" or "out" or "cross" or "none") and var ticks ? ticks : "none",
            Style = labelStyle with { Color = labelStyle.Color == SKColors.Black ? new SKColor(0x59, 0x59, 0x59) : labelStyle.Color },
            // A second value axis (a Pareto line's) stands at the far end of the categories.
            Crosses = position == "r" ? ChartCrossing.Max : ChartCrossing.AutoZero,
        };
    }

    // ───────────────────────── Histograms and Pareto charts ─────────────────────────

    /// <summary>
    /// The columns of a histogram (its values counted into bins, cx:binning) or of a Pareto chart of categories (the
    /// values added up per category, cx:aggregation), and the Pareto line's running share (largest bins first) when asked.
    /// </summary>
    private (ChartSeries Columns, List<double?>? Line) Histogram(ChartSeries series, XElement? layout, bool pareto)
    {
        var binning = layout?.Element(Cx + "binning");
        List<(string Label, double Value)> bins;
        if (binning is null && series.Categories is { } names)
        {
            // Categories added up, in the order they first appear.
            var sums = new Dictionary<string, double>(StringComparer.Ordinal);
            var order = new List<string>();
            for (var i = 0; i < series.Values.Count; i++)
            {
                if (series.Values[i] is not { } v)
                    continue;
                var name = i < names.Count ? names[i] ?? "" : "";
                if (sums.TryAdd(name, 0))
                    order.Add(name);
                sums[name] += v;
            }
            bins = [.. order.Select(n => (n, sums[n]))];
        }
        else
        {
            bins = Bins([.. series.Values.OfType<double>()], Binning(binning), series.Format);
        }
        if (pareto)
            bins = [.. bins.OrderByDescending(b => b.Value)];
        var columns = new ChartSeries
        {
            Name = series.Name,
            Index = 0,
            Categories = [.. bins.Select(b => (string?)b.Label)],
            Values = [.. bins.Select(b => (double?)b.Value)],
            Format = binning is null ? series.Format : null,
            Style = series.Style,
            Points = series.Points,
            Labels = series.Labels,
        };
        if (!pareto)
            return (columns, null);
        var total = bins.Sum(b => Math.Abs(b.Value));
        var running = 0.0;
        var line = new List<double?>(bins.Count);
        foreach (var (_, value) in bins)
        {
            running += Math.Abs(value);
            line.Add(total > 0 ? running / total : 0);
        }
        return (columns, line);
    }

    private static ChartBinning Binning(XElement? binning)
    {
        static double? Limit(XAttribute? attribute) =>
            (string?)attribute is { } text && text != "auto" && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
                ? Math.Clamp(v, -MaxValue, MaxValue)
                : null;
        return new ChartBinning(
            Number(binning?.Element(Cx + "binSize")) is > 0 and var size ? Math.Min(size, MaxValue) : null,
            Number(binning?.Element(Cx + "binCount")) is >= 1 and var count ? (int)Math.Min(count, MaxBins) : null,
            (string?)binning?.Attribute("intervalClosed") != "l",
            Limit(binning?.Attribute("underflow")),
            Limit(binning?.Attribute("overflow")));
    }

    /// <summary>
    /// Values counted into bins as Excel's histogram does: of a width, a count, or Scott's rule (3.5 σ / ∛n) from the
    /// lowest value (or the underflow limit), each closed on its right (the first on both sides) unless told otherwise.
    /// </summary>
    internal List<(string Label, double Count)> Bins(List<double> values, ChartBinning binning, string? format)
    {
        var bins = new List<(string, double)>();
        if (values.Count == 0)
            return bins;
        string N(double v) => Cut(_formatter.Format(v, format));
        var low = values.Min();
        var high = values.Max();
        var under = binning.Underflow is { } u && u > low ? u : (double?)null;
        var over = binning.Overflow is { } o && o < high ? o : (double?)null;
        var from = under ?? low;
        var to = over ?? high;
        if (to < from)
            to = from;
        double width;
        if (binning.Size is { } size)
            width = size;
        else if (binning.Count is { } count)
            width = (to - from) / count;
        else
        {
            var n = values.Count;
            var mean = values.Average();
            var deviation = n > 1 ? Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (n - 1)) : 0;
            width = 3.5 * deviation / Math.Cbrt(n);
        }
        var bands = width > 0 && double.IsFinite(width) ? (int)Math.Clamp(Math.Ceiling((to - from) / width - 1e-9), 1, MaxBins) : 1;
        if (!(width > 0) || !double.IsFinite(width) || to - from <= 0)
            width = Math.Max(to - from, 1);
        if ((to - from) / width > MaxBins)
            width = (to - from) / MaxBins;
        var counts = new double[bands];
        double underCount = 0, overCount = 0;
        foreach (var v in values)
        {
            if (under is { } ul && (binning.ClosedRight ? v <= ul : v < ul))
            {
                underCount++;
                continue;
            }
            if (over is { } ol && (binning.ClosedRight ? v > ol : v >= ol))
            {
                overCount++;
                continue;
            }
            var position = (v - from) / width;
            var index = binning.ClosedRight ? (int)Math.Ceiling(position - 1e-9) - 1 : (int)Math.Floor(position + 1e-9);
            counts[Math.Clamp(index, 0, bands - 1)]++;
        }
        if (under is { } underLimit)
            bins.Add(((binning.ClosedRight ? "≤" : "<") + N(underLimit), underCount));
        for (var i = 0; i < bands; i++)
        {
            var a = from + i * width;
            var b = from + (i + 1) * width;
            var label = binning.ClosedRight
                ? (i == 0 && under is null ? "[" : "(") + N(a) + ", " + N(b) + "]"
                : "[" + N(a) + ", " + N(b) + (i == bands - 1 && over is null ? "]" : ")");
            bins.Add((label, counts[i]));
        }
        if (over is { } overLimit)
            bins.Add(((binning.ClosedRight ? ">" : "≥") + N(overLimit), overCount));
        return bins;
    }
}
