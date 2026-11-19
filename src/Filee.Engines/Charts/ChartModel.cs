// A DrawingML chart as ChartDrawer draws it (ECMA-376 Part 1, 21.2): the plots of the plot area with their series
// (the values cached in the chart part), the axes they use, the title, the legend and the chart's and plot area's
// fills. ChartReader builds it from a c:chartSpace part; the sheet printer, the document renderer and the slide
// renderer draw it.

using SkiaSharp;

namespace Filee.Engines.Charts;

/// <summary>The kinds of plot drawn.</summary>
internal enum ChartKind
{
    Bar,
    Line,
    Area,
    Pie,
    Doughnut,
    Scatter,
}

/// <summary>How a plot's series stand to one another (c:grouping).</summary>
internal enum ChartGrouping
{
    /// <summary>Side by side (bars) or each on its own (lines, areas): clustered or standard.</summary>
    Standard,
    Stacked,
    PercentStacked,
}

/// <summary>
/// A line: colour (null = the automatic one), width in points (null = the automatic one) and dashes (multiples of the
/// width, null = solid); <see cref="None"/> = no line.
/// </summary>
internal sealed record ChartLine(SKColor? Color = null, float? Width = null, float[]? Dash = null, bool None = false)
{
    public static readonly ChartLine Hidden = new(None: true);
}

/// <summary>How a bar, slice, area, marker or box is filled and outlined; null parts are the automatic ones.</summary>
internal sealed record ChartShapeStyle
{
    /// <summary>The fill; null = automatic (the series colour), <see cref="SKColors.Transparent"/> = none.</summary>
    public SKColor? Fill { get; init; }

    /// <summary>The outline; null = automatic.</summary>
    public ChartLine? Line { get; init; }

    public static readonly ChartShapeStyle Auto = new();
}

/// <summary>How text looks: font family (null = the theme's), size in points, weight, slant and colour.</summary>
internal sealed record ChartTextStyle(string? Family, float Size, bool Bold, bool Italic, SKColor Color)
{
    public static readonly ChartTextStyle Default = new(null, 10, false, false, SKColors.Black);
}

/// <summary>A title (chart or axis): its lines and how they look.</summary>
internal sealed record ChartTitle(IReadOnlyList<string> Lines, ChartTextStyle Style, bool Overlay = false)
{
    /// <summary>Where the file places the title (fractions of the chart), or null for the automatic place.</summary>
    public ChartLayout? Layout { get; init; }
}

/// <summary>c:manualLayout: a box as fractions of the chart area; <see cref="Inner"/> = the plot without its axis labels.</summary>
internal readonly record struct ChartLayout(double X, double Y, double? Width, double? Height, bool Inner);

/// <summary>What data labels show and where (c:dLbls).</summary>
internal sealed record ChartLabels(bool Value, bool Category, bool Series, bool Percent, string? Position, string? Format, ChartTextStyle Style)
{
    public bool Any => Value || Category || Series || Percent;
}

/// <summary>A marker of a line or scatter series: symbol (none, circle, square, ...), size in points, style.</summary>
internal sealed record ChartMarker(string Symbol, float Size, ChartShapeStyle Style);

/// <summary>One series: its name, categories and values (null = blank), and how it looks.</summary>
internal sealed class ChartSeries
{
    public string? Name { get; init; }

    /// <summary>c:idx: the series' place in the automatic colours.</summary>
    public int Index { get; init; }

    /// <summary>Category labels (c:cat), or null for 1, 2, 3, ....</summary>
    public IReadOnlyList<string?>? Categories { get; init; }

    /// <summary>X values of a scatter series (c:xVal numbers), or null for 1, 2, 3, ....</summary>
    public IReadOnlyList<double?>? XValues { get; init; }

    public IReadOnlyList<double?> Values { get; init; } = [];

    /// <summary>The number format of the cached values (c:numCache c:formatCode), or null for General.</summary>
    public string? Format { get; init; }

    public ChartShapeStyle Style { get; init; } = ChartShapeStyle.Auto;

    /// <summary>Points styled on their own (c:dPt), by index.</summary>
    public IReadOnlyDictionary<int, ChartShapeStyle> Points { get; init; } = new Dictionary<int, ChartShapeStyle>();

    /// <summary>Slices pulled out of a pie, in percent of the radius (c:explosion), by index; -1 for the whole series.</summary>
    public IReadOnlyDictionary<int, int> Explosion { get; init; } = new Dictionary<int, int>();

    /// <summary>The series' marker, or null for the plot's automatic one.</summary>
    public ChartMarker? Marker { get; init; }

    public ChartLabels? Labels { get; init; }

    /// <summary>A line or scatter series drawn as a smooth curve.</summary>
    public bool Smooth { get; init; }
}

/// <summary>One plot of the plot area (c:barChart, c:lineChart, ...): its series, drawn against two axes.</summary>
internal sealed class ChartPlot
{
    public ChartKind Kind { get; init; }
    public ChartGrouping Grouping { get; init; }

    /// <summary>Bars run left to right (c:barDir bar) instead of upwards.</summary>
    public bool Horizontal { get; init; }

    /// <summary>Each point in its own colour (c:varyColors), as pie slices are.</summary>
    public bool VaryColors { get; init; }

    /// <summary>Space between bar groups, in percent of a bar's width.</summary>
    public int GapWidth { get; init; } = 150;

    /// <summary>How far the bars of a group overlap, in percent of a bar's width (negative: a gap).</summary>
    public int Overlap { get; init; }

    /// <summary>The hole of a doughnut, in percent of its radius.</summary>
    public int HoleSize { get; init; } = 50;

    /// <summary>Where a pie's first slice starts, in degrees clockwise from the top.</summary>
    public double FirstSliceAngle { get; init; }

    /// <summary>Lines (and markers) of a line or scatter plot: c:marker of c:lineChart, c:scatterStyle.</summary>
    public bool Lines { get; init; } = true;

    public bool Markers { get; init; }

    public ChartLabels? Labels { get; init; }

    /// <summary>The axes the plot is drawn against (c:axId): category (or X) first, value (or Y) second.</summary>
    public IReadOnlyList<uint> AxisIds { get; init; } = [];

    public List<ChartSeries> Series { get; } = [];
}

/// <summary>Where an axis crosses the other (c:crosses, c:crossesAt).</summary>
internal enum ChartCrossing
{
    AutoZero,
    Min,
    Max,
    At,
}

/// <summary>An axis: c:catAx, c:dateAx (categories) or c:valAx (values).</summary>
internal sealed record ChartAxis
{
    public uint Id { get; init; }

    /// <summary>A value axis (c:valAx): numbers along it, scaled; else categories.</summary>
    public bool IsValue { get; init; }

    /// <summary>c:axPos: b, l, r or t.</summary>
    public string Position { get; init; } = "b";

    public bool Deleted { get; init; }

    /// <summary>Runs from its maximum to its minimum (c:orientation maxMin).</summary>
    public bool Reversed { get; init; }

    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? MajorUnit { get; init; }

    /// <summary>A logarithmic axis' base (c:logBase), or null.</summary>
    public double? LogBase { get; init; }

    public ChartTitle? Title { get; init; }

    /// <summary>The labels' number format, or null for the values' own (c:numFmt sourceLinked).</summary>
    public string? Format { get; init; }

    public ChartLine? MajorGridlines { get; init; }
    public ChartLine? MinorGridlines { get; init; }

    /// <summary>The axis line; null = automatic.</summary>
    public ChartLine? Line { get; init; }

    /// <summary>c:tickLblPos: nextTo, low, high or none.</summary>
    public string LabelPosition { get; init; } = "nextTo";

    /// <summary>c:majorTickMark: out, in, cross or none.</summary>
    public string MajorTicks { get; init; } = "out";

    public ChartCrossing Crosses { get; init; }
    public double CrossesAt { get; init; }

    /// <summary>The other axis of the pair (c:crossAx).</summary>
    public uint CrossAxis { get; init; }

    /// <summary>Points sit between the category ticks (c:crossBetween between) rather than on them (midCat).</summary>
    public bool Between { get; init; } = true;

    /// <summary>Category labels skipped: every n-th is drawn (c:tickLblSkip), or 0 for automatic.</summary>
    public int LabelSkip { get; init; }

    /// <summary>Turn of the labels in degrees (c:txPr a:bodyPr rot), or null for automatic.</summary>
    public double? LabelRotation { get; init; }

    public ChartTextStyle Style { get; init; } = ChartTextStyle.Default;
}

/// <summary>The legend: where it is, which entries are left out, how its text looks.</summary>
internal sealed class ChartLegend
{
    /// <summary>c:legendPos: r, l, t, b or tr.</summary>
    public string Position { get; init; } = "r";

    public bool Overlay { get; init; }

    /// <summary>Entries left out (c:legendEntry c:delete), by index.</summary>
    public IReadOnlySet<int> Deleted { get; init; } = new HashSet<int>();

    public ChartLayout? Layout { get; init; }

    public ChartTextStyle Style { get; init; } = ChartTextStyle.Default;
}

/// <summary>
/// Office's automatic chart colours: the theme's six accents, then the same six darker and lighter in turn (Office's
/// colour variations: 60 %, 80 % + 20 %, 80 %, 60 % + 40 %, 50 %, 70 % + 30 %, 70 %, 50 % + 50 % luminance).
/// </summary>
internal sealed class ChartPalette(IReadOnlyList<SKColor> accents)
{
    private static readonly (double Mod, double Off)[] Variations =
        [(1, 0), (0.6, 0), (0.8, 0.2), (0.8, 0), (0.6, 0.4), (0.5, 0), (0.7, 0.3), (0.7, 0), (0.5, 0.5)];

    public static readonly ChartPalette Default = new(
    [
        new SKColor(0x44, 0x72, 0xC4), new SKColor(0xED, 0x7D, 0x31), new SKColor(0xA5, 0xA5, 0xA5),
        new SKColor(0xFF, 0xC0, 0x00), new SKColor(0x5B, 0x9B, 0xD5), new SKColor(0x70, 0xAD, 0x47),
    ]);

    public SKColor Color(int index)
    {
        index = Math.Max(0, index);
        var accent = accents.Count == 0 ? SKColors.Gray : accents[index % accents.Count];
        var (mod, off) = Variations[index / Math.Max(1, accents.Count) % Variations.Length];
        if (mod == 1 && off == 0)
            return accent;
        accent.ToHsl(out var h, out var s, out var l);
        return SKColor.FromHsl(h, s, (float)Math.Clamp(l * mod + off * 100, 0, 100));
    }
}

/// <summary>A chart part, read.</summary>
internal sealed class Chart
{
    public ChartTitle? Title { get; init; }
    public List<ChartPlot> Plots { get; } = [];
    public List<ChartAxis> Axes { get; } = [];
    public ChartLegend? Legend { get; init; }

    /// <summary>The chart area's fill and outline (c:chartSpace c:spPr).</summary>
    public ChartShapeStyle Area { get; init; } = ChartShapeStyle.Auto;

    /// <summary>The plot area's fill and outline (c:plotArea c:spPr).</summary>
    public ChartShapeStyle PlotArea { get; init; } = ChartShapeStyle.Auto;

    public ChartLayout? PlotLayout { get; init; }

    /// <summary>The chart area has rounded corners (c:roundedCorners).</summary>
    public bool RoundedCorners { get; init; }

    /// <summary>How blank values are drawn (c:dispBlanksAs): gap, zero or span (the line goes on over them).</summary>
    public string BlanksAs { get; init; } = "gap";

    /// <summary>Dates in the cached values count from 1904 (c:date1904).</summary>
    public bool Date1904 { get; init; }

    /// <summary>The chart's text when nothing else says (c:chartSpace c:txPr).</summary>
    public ChartTextStyle Text { get; init; } = ChartTextStyle.Default;

    /// <summary>The automatic colours of series and points.</summary>
    public ChartPalette Palette { get; init; } = ChartPalette.Default;

    /// <summary>The theme's body font (+mn-lt), for text without a family.</summary>
    public string? ThemeFont { get; init; }

    /// <summary>Points of all series together (what drawing the chart costs).</summary>
    public long PointCount => Plots.Sum(p => p.Series.Sum(s => (long)s.Values.Count));

    public ChartAxis? Axis(uint id) => Axes.FirstOrDefault(a => a.Id == id);
}
