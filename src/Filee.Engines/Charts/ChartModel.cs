// A DrawingML chart as ChartDrawer draws it (ECMA-376 Part 1, 21.2): the plots of the plot area with their series
// (the values cached in the chart part, their trendlines, error bars and labels), the axes they use, the title, the
// legend, the chart's and plot area's fills and a 3D chart's view, floor and walls; Office 2016 charts (chartex) in
// the same model. ChartReader builds it from a c:chartSpace part and OdfChartReader from
// an OpenDocument chart object; the sheet printer, the document renderer and the slide renderer draw it.

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

    /// <summary>A radar (spider) chart: categories as spokes, values out from the centre.</summary>
    Radar,

    /// <summary>A bubble chart: X, Y and a size per point.</summary>
    Bubble,

    /// <summary>A surface: series across categories as a grid coloured by value bands.</summary>
    Surface,

    /// <summary>A pie with some slices pulled into a second pie or a bar beside it (c:ofPieChart).</summary>
    OfPie,

    /// <summary>Office 2016 chart types (cx:chartSpace): running totals as floating columns.</summary>
    Waterfall,

    /// <summary>Bars centred on one line, narrowing stage by stage.</summary>
    Funnel,

    /// <summary>A hierarchy as nested rectangles, each the size of its value.</summary>
    Treemap,

    /// <summary>A hierarchy as rings, the root in the middle.</summary>
    Sunburst,

    /// <summary>A box and whisker plot: quartiles, whiskers, mean and outliers per category.</summary>
    BoxWhisker,
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

/// <summary>
/// A gradient fill: colour stops (positions 0 to 1) along a line turned <see cref="Angle"/> degrees clockwise from
/// left to right, or out from a centre (fractions of the shape's box) when <see cref="Radial"/>.
/// </summary>
internal sealed record ChartGradient(IReadOnlyList<(float Position, SKColor Color)> Stops, double Angle = 0, bool Radial = false, float CenterX = 0.5f, float CenterY = 0.5f);

/// <summary>
/// A hatch or pattern fill: lines of <see cref="Color"/> every <see cref="Spacing"/> points, turned <see cref="Angle"/>
/// degrees anticlockwise from level, <see cref="Crossings"/> sets of them (1 single, 2 crossed, 3 triple), over
/// <see cref="Background"/> (null = clear). <see cref="Blend"/> instead fills with the colour mixed into the
/// background by that much (a dotted pattern).
/// </summary>
internal sealed record ChartHatch(SKColor Color, SKColor? Background, float Spacing, double Angle, int Crossings = 1, float? Blend = null);

/// <summary>How a bar, slice, area, marker or box is filled and outlined; null parts are the automatic ones.</summary>
internal sealed record ChartShapeStyle
{
    /// <summary>The fill; null = automatic (the series colour), <see cref="SKColors.Transparent"/> = none. A gradient's or hatch's main colour.</summary>
    public SKColor? Fill { get; init; }

    /// <summary>A gradient drawn instead of <see cref="Fill"/>, or null.</summary>
    public ChartGradient? Gradient { get; init; }

    /// <summary>A hatch or pattern drawn instead of <see cref="Fill"/>, or null.</summary>
    public ChartHatch? Hatch { get; init; }

    /// <summary>The outline; null = automatic.</summary>
    public ChartLine? Line { get; init; }

    public static readonly ChartShapeStyle Auto = new();
}

/// <summary>How text looks: font family (null = the theme's), size in points, weight, slant and colour.</summary>
internal sealed record ChartTextStyle(string? Family, float Size, bool Bold, bool Italic, SKColor Color)
{
    public static readonly ChartTextStyle Default = new(null, 10, false, false, SKColors.Black);
}

/// <summary>A piece of rich text in its own style; <see cref="Raise"/> lifts it (a superscript) by that part of its size.</summary>
internal sealed record ChartRun(string Text, ChartTextStyle Style, float Raise = 0);

/// <summary>A title (chart or axis): its lines and how they look.</summary>
internal sealed record ChartTitle(IReadOnlyList<string> Lines, ChartTextStyle Style, bool Overlay = false)
{
    /// <summary>Where the file places the title (fractions of the chart), or null for the automatic place.</summary>
    public ChartLayout? Layout { get; init; }

    /// <summary>The lines as runs in their own styles (rich text), or null when all of it is in <see cref="Style"/>.</summary>
    public IReadOnlyList<IReadOnlyList<ChartRun>>? Runs { get; init; }
}

/// <summary>c:manualLayout: a box as fractions of the chart area; <see cref="Inner"/> = the plot without its axis labels.</summary>
internal readonly record struct ChartLayout(double X, double Y, double? Width, double? Height, bool Inner);

/// <summary>What data labels show and where (c:dLbls, or one point's c:dLbl).</summary>
internal sealed record ChartLabels(bool Value, bool Category, bool Series, bool Percent, string? Position, string? Format, ChartTextStyle Style)
{
    /// <summary>A label's own text (c:dLbl c:tx), lines of runs, shown instead of what the flags say; or null.</summary>
    public IReadOnlyList<IReadOnlyList<ChartRun>>? Text { get; init; }

    /// <summary>What goes between the parts (c:separator), or null for ", ".</summary>
    public string? Separator { get; init; }

    /// <summary>How far a label is moved from where it would stand (c:dLbl c:layout), in fractions of the chart's size; or null.</summary>
    public (double X, double Y)? Offset { get; init; }

    /// <summary>A moved pie label is joined to its slice by a line (c:showLeaderLines).</summary>
    public bool LeaderLines { get; init; }

    public bool Any => Value || Category || Series || Percent || Text is { Count: > 0 };
}

/// <summary>The curves a trendline fits (c:trendlineType, chart:regression-type).</summary>
internal enum TrendKind
{
    Linear,
    Exponential,
    Logarithmic,
    Polynomial,
    Power,
    MovingAverage,
}

/// <summary>A trendline of a series (c:trendline): the curve, how far it runs on past the points, and its label.</summary>
internal sealed record ChartTrendline(TrendKind Kind)
{
    /// <summary>The polynomial's order (2 to 6).</summary>
    public int Order { get; init; } = 2;

    /// <summary>The moving average's period (2 or more points).</summary>
    public int Period { get; init; } = 2;

    /// <summary>How far the line runs on past the last point and before the first, in X units (categories count 1).</summary>
    public double Forward { get; init; }

    public double Backward { get; init; }

    /// <summary>Where the curve must cross the Y axis (c:intercept), or null to fit it.</summary>
    public double? Intercept { get; init; }

    public bool ShowEquation { get; init; }

    public bool ShowRSquared { get; init; }

    /// <summary>The line; null = automatic (the series colour, thin).</summary>
    public ChartLine? Line { get; init; }

    /// <summary>The label's number format (c:trendlineLbl c:numFmt), or null for the automatic one.</summary>
    public string? Format { get; init; }

    /// <summary>How far the label is moved from where it would stand (c:trendlineLbl c:layout), in fractions of the chart's size; or null.</summary>
    public (double X, double Y)? LabelOffset { get; init; }

    /// <summary>The trendline's own name for the legend (c:name), or null for Office's ("Linear (North)").</summary>
    public string? Name { get; init; }

    /// <summary>Listed in the legend after the series, as Office lists trendlines.</summary>
    public bool InLegend { get; init; }

    public ChartTextStyle Style { get; init; } = ChartTextStyle.Default;
}

/// <summary>How long error bars are (c:errValType, chart:error-category).</summary>
internal enum ErrorAmount
{
    Fixed,
    Percentage,
    StandardDeviation,
    StandardError,
    Variance,
    Custom,
}

/// <summary>Error bars of a series (c:errBars): along X or Y, which side, how long, with caps or not.</summary>
internal sealed record ChartErrorBars(ErrorAmount Amount, double Value)
{
    /// <summary>Along the X axis (a scatter series' c:errDir x) instead of the value axis.</summary>
    public bool AlongX { get; init; }

    public bool Plus { get; init; } = true;

    public bool Minus { get; init; } = true;

    public bool EndCap { get; init; } = true;

    /// <summary>Custom lengths per point (c:plus, c:minus), or null.</summary>
    public IReadOnlyList<double?>? PlusValues { get; init; }

    public IReadOnlyList<double?>? MinusValues { get; init; }

    /// <summary>The line; null = automatic.</summary>
    public ChartLine? Line { get; init; }

    /// <summary>
    /// Excel's statistics: standard deviation bars around the series' mean and its standard error √(Σy² / (n(n − 1)));
    /// false for LibreOffice's (around each point, s / √n).
    /// </summary>
    public bool Office { get; init; } = true;
}

/// <summary>
/// How a 3D chart is seen (c:view3D): turned <see cref="RotX"/> degrees up and <see cref="RotY"/> degrees round,
/// its depth and height in percent of its width, with right-angled axes (an oblique view) or in perspective.
/// </summary>
internal sealed record ChartView3D(double RotX = 15, double RotY = 20, int DepthPercent = 100, int HeightPercent = 100, bool RightAngleAxes = true, int Perspective = 30);

/// <summary>Up and down bars of a line or stock plot (c:upDownBars): between the first and the last series.</summary>
internal sealed record ChartUpDownBars(int GapWidth, ChartShapeStyle Up, ChartShapeStyle Down);

/// <summary>
/// How a histogram sorts its values into bins (cx:binning): bins of a width or a count of them (else Scott's rule),
/// closed on the right (Excel's default) or the left, with values below or above a limit in a bin of their own.
/// </summary>
internal sealed record ChartBinning(double? Size, int? Count, bool ClosedRight = true, double? Underflow = null, double? Overflow = null);

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

    /// <summary>
    /// The outer levels of multi-level categories (c:multiLvlStrCache), from the one next to <see cref="Categories"/>
    /// outwards; a label stands at the first category of its group, blanks for the rest. Null when there's one level.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string?>>? CategoryLevels { get; init; }

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

    /// <summary>
    /// The key the legend shows for the series when it isn't drawn as a line or box of its own (an ODF stock chart's
    /// entry: its candlestick, or its range line with the close tick); null for the series' own look.
    /// </summary>
    public ChartShapeStyle? LegendKey { get; set; }

    public ChartLabels? Labels { get; init; }

    /// <summary>Labels of single points (c:dLbl), by index, over <see cref="Labels"/>.</summary>
    public IReadOnlyDictionary<int, ChartLabels> PointLabels { get; init; } = new Dictionary<int, ChartLabels>();

    /// <summary>A line or scatter series drawn as a smooth curve.</summary>
    public bool Smooth { get; init; }

    public IReadOnlyList<ChartTrendline> Trendlines { get; init; } = [];

    public IReadOnlyList<ChartErrorBars> ErrorBars { get; init; } = [];

    /// <summary>Bubble sizes (c:bubbleSize), or null.</summary>
    public IReadOnlyList<double?>? BubbleSizes { get; init; }

    /// <summary>Bubbles drawn with a 3D look (c:bubble3D).</summary>
    public bool Bubble3D { get; init; }

    /// <summary>A chartex series drawn on the plot's second value axis (a Pareto line).</summary>
    public bool Secondary { get; init; }
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

    /// <summary>A pie's slices follow one another clockwise (Office's always do; LibreOffice's unless told otherwise).</summary>
    public bool Clockwise { get; init; } = true;

    /// <summary>Lines (and markers) of a line or scatter plot: c:marker of c:lineChart, c:scatterStyle.</summary>
    public bool Lines { get; init; } = true;

    public bool Markers { get; init; }

    public ChartLabels? Labels { get; init; }

    /// <summary>The axes the plot is drawn against (c:axId): category (or X) first, value (or Y) second.</summary>
    public IReadOnlyList<uint> AxisIds { get; init; } = [];

    public List<ChartSeries> Series { get; } = [];

    /// <summary>Drawn in 3D (bar3DChart, line3DChart, area3DChart, pie3DChart, surface3DChart).</summary>
    public bool ThreeD { get; init; }

    /// <summary>A 3D plot whose series stand one behind the other (c:grouping standard; 3D lines always do).</summary>
    public bool SeriesInDepth { get; init; }

    /// <summary>Space between series rows of a 3D plot, in percent of a bar's depth (c:gapDepth).</summary>
    public int GapDepth { get; init; } = 150;

    /// <summary>A radar's areas are filled (c:radarStyle filled).</summary>
    public bool Filled { get; init; }

    /// <summary>The largest bubble in percent of Office's default size (c:bubbleScale).</summary>
    public int BubbleScale { get; init; } = 100;

    /// <summary>Bubbles of negative size are drawn (hollow), else left out (c:showNegBubbles).</summary>
    public bool NegativeBubbles { get; init; }

    /// <summary>A bubble's width is its size (c:sizeRepresents w), else its area.</summary>
    public bool BubbleWidth { get; init; }

    /// <summary>Lines from the highest to the lowest value at each category (c:hiLowLines), or null.</summary>
    public ChartLine? HighLowLines { get; init; }

    /// <summary>Lines from each point down to the category axis (c:dropLines), or null.</summary>
    public ChartLine? DropLines { get; init; }

    public ChartUpDownBars? UpDownBars { get; init; }

    /// <summary>A surface drawn as lines only (c:wireframe).</summary>
    public bool Wireframe { get; init; }

    /// <summary>Fills of a surface's value bands (c:bandFmts), by band.</summary>
    public IReadOnlyDictionary<int, ChartShapeStyle> Bands { get; init; } = new Dictionary<int, ChartShapeStyle>();

    /// <summary>A bar of pie (c:ofPieType bar), else a pie of pie.</summary>
    public bool OfPieBar { get; init; }

    /// <summary>Which points go to the second plot of a bar or pie of pie (c:splitType): pos, val, percent or cust.</summary>
    public string SplitType { get; init; } = "pos";

    /// <summary>The split's position, value or percent (c:splitPos), or null for Office's default.</summary>
    public double? SplitPosition { get; init; }

    /// <summary>The points a custom split sends to the second plot (c:custSplit).</summary>
    public IReadOnlySet<int> CustomSplit { get; init; } = new HashSet<int>();

    /// <summary>The second plot's size in percent of the first (c:secondPieSize).</summary>
    public int SecondPieSize { get; init; } = 75;

    /// <summary>Lines joining the pie to its second plot (c:serLines), or null for none.</summary>
    public ChartLine? SeriesLines { get; init; }

    /// <summary>A waterfall's points that are totals (cx:subtotals), standing on the axis.</summary>
    public IReadOnlySet<int> Subtotals { get; init; } = new HashSet<int>();

    /// <summary>A waterfall's lines from each column's end to the next one.</summary>
    public bool ConnectorLines { get; init; } = true;

    /// <summary>A treemap's parent labels (cx:parentLabelLayout): overlapping, banner or none.</summary>
    public string ParentLabels { get; init; } = "overlapping";

    /// <summary>Box and whisker: quartiles exclusive of the median (Excel's default) or inclusive, and what is shown.</summary>
    public bool ExclusiveQuartiles { get; init; } = true;

    public bool MeanLine { get; init; }

    public bool MeanMarker { get; init; } = true;

    public bool InnerPoints { get; init; }

    public bool Outliers { get; init; } = true;
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

    /// <summary>A 3D chart's series axis (c:serAx): the series' names along the depth.</summary>
    public bool IsSeries { get; init; }

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

    /// <summary>How a 3D chart is seen, or null for a flat one.</summary>
    public ChartView3D? View3D { get; init; }

    /// <summary>The floor and walls of a 3D chart (c:floor, c:sideWall, c:backWall).</summary>
    public ChartShapeStyle Floor { get; init; } = ChartShapeStyle.Auto;

    public ChartShapeStyle SideWall { get; init; } = ChartShapeStyle.Auto;

    public ChartShapeStyle BackWall { get; init; } = ChartShapeStyle.Auto;

    /// <summary>Points of all series together (what drawing the chart costs).</summary>
    public long PointCount => Plots.Sum(p => p.Series.Sum(s => (long)s.Values.Count));

    public ChartAxis? Axis(uint id) => Axes.FirstOrDefault(a => a.Id == id);
}
