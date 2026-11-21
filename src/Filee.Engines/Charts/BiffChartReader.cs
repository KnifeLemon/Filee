// An Excel 97–2003 chart ([MS-XLS] 2.1.7.20.1, the chart sheet substream: a chart sheet's, or an embedded chart's
// after its OBJ record) → the chart model, by way of the DrawingML chart part (c:chartSpace) that says the same, so
// ChartReader reads it as it reads an XLSX chart and ChartDrawer draws it the same way. The records nest between
// Begin and End; the values are the ones the chart caches after its formats (SERIESDATA: SIIndex, Number, Label), so
// no formula is evaluated.
//
// Read: chart groups (ChartFormat) of bar and column (Bar: clustered, stacked, 100 %, horizontal), line (stacked,
// with high-low lines, up and down bars and drop lines: stock charts), pie and doughnut (Pie), bar or pie of pie
// (BopPop, BopPopCustom), area, scatter and bubble (Scatter), radar and filled radar, and surface (Surf) plots, on the
// primary and secondary axis groups (AxisParent), in 3D (Chart3d: rotation, elevation, depth, height, gap, walls and
// floor); series (Series) with their names, values, categories (text, or numbers in their cells' format), X values,
// bubble sizes, the formats of the series and single points (DataFormat: LineFormat, AreaFormat with its pattern,
// PieFormat, MarkerFormat, SerFmt smoothing and 3D bubbles), data labels (AttachedLabel, and the Text records linked
// to series and points: position, font, deleted, own text), trendlines (SerAuxTrend) and error bars (SerAuxErrBar,
// custom values too); axes (Axis: CatSerRange, AxcExt date axes, ValueRange scaling, Tick marks, label position and
// rotation, AxisLine lines and gridlines, IFmtRecord number formats), axis and chart titles (Text with ObjectLink),
// the legend (position, font, LegendException deleted entries), the chart and plot area frames (Frame), how blanks
// are drawn (ShtProps) and the fonts (FontX: the workbook's, or the chart's own after FrtFontList).
// Automatic colours are Excel 97–2003's: chart fills from the workbook palette's 25th colour on, lines from its 33rd
// (LibreOffice takes the same entries); automatic chart and plot area frames are Excel 97's white chart area with a
// black hairline and grey plot area. Not read: GelFrame gradients and pictures (the pattern's colours are drawn),
// ShapePropsStream / RichTextStream (Office 2007's own formatting written into an XLS), CrtLayout12 manual layouts,
// moved labels, data tables (Dat) and text boxes drawn on the chart.
//
// Hostile input: records nest at most MaxDepth deep (deeper ones are read as if flat); series per chart are bounded
// by ChartReader.MaxSeries and points of all series by MaxPoints (Excel 2003's own limit for one chart), refused with
// error.document_too_complex; cached cells are counted before they are kept; texts are cut at ChartReader.MaxText
// characters and numbers that aren't finite are blanks. Short records read as zeros.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Filee.Engines.Documents;
using Filee.Engines.Slides;

namespace Filee.Engines.Charts;

/// <summary>A font of the workbook (FONT record): name, size in points, bold, italic and colour (an icv).</summary>
internal sealed record BiffFont(string Name, double Size, bool Bold, bool Italic, int Color);

/// <summary>What a chart substream refers to in the workbook globals.</summary>
internal sealed class BiffChartBook
{
    /// <summary>The workbook's fonts by BIFF font index (there is no font 4), or null.</summary>
    public Func<int, BiffFont?> Font { get; init; } = _ => null;

    /// <summary>How many FONT records the globals hold (FontX indexes above them are the chart's own fonts).</summary>
    public int FontCount { get; init; }

    /// <summary>A colour of the palette by icv ("#RRGGBB"), or null for the system colours.</summary>
    public Func<int, string?> Color { get; init; } = _ => null;

    /// <summary>A number format by its id (the workbook's FORMAT records and the built-in ones), or null.</summary>
    public Func<int, string?> Format { get; init; } = _ => null;

    /// <summary>The number format of a cell format (XF index), or null for General.</summary>
    public Func<int, string?> CellFormat { get; init; } = _ => null;

    public bool Date1904 { get; init; }
}

internal sealed class BiffChartReader
{
    private static readonly XNamespace C = ChartReader.C;
    private static readonly XNamespace A = DrawingColors.A;

    /// <summary>Points of all series of one chart at most: Excel 97–2003's limit (32,000 a series, 256,000 a chart).</summary>
    internal const int MaxPoints = 256_000;

    /// <summary>Levels of Begin and End records followed; deeper records are read as if they were one level up.</summary>
    internal const int MaxDepth = 32;

    // Record types ([MS-XLS] 2.3).
    private const int ChartId = 0x1002, SeriesId = 0x1003, DataFormatId = 0x1006, LineFormatId = 0x1007, MarkerFormatId = 0x1009,
        AreaFormatId = 0x100A, PieFormatId = 0x100B, AttachedLabelId = 0x100C, SeriesTextId = 0x100D, ChartFormatId = 0x1014,
        LegendId = 0x1015, BarId = 0x1017, LineId = 0x1018, PieId = 0x1019, AreaId = 0x101A, ScatterId = 0x101B,
        CrtLineId = 0x101C, AxisId = 0x101D, TickId = 0x101E, ValueRangeId = 0x101F, CatSerRangeId = 0x1020, AxisLineId = 0x1021,
        DefaultTextId = 0x1024, TextId = 0x1025, FontXId = 0x1026, ObjectLinkId = 0x1027, FrameId = 0x1032, BeginId = 0x1033,
        EndId = 0x1034, PlotAreaId = 0x1035, Chart3dId = 0x103A, DropBarId = 0x103D, RadarId = 0x103E, SurfId = 0x103F,
        RadarAreaId = 0x1040, AxisParentId = 0x1041, LegendExceptionId = 0x1043, ShtPropsId = 0x1044, SerToCrtId = 0x1045,
        SerParentId = 0x104A, SerAuxTrendId = 0x104B, IFmtRecordId = 0x104E, BraiId = 0x1051, SerAuxErrBarId = 0x105B,
        SerFmtId = 0x105D, BopPopId = 0x1061, AxcExtId = 0x1062, SIIndexId = 0x1065, BopPopCustomId = 0x1067,
        FontId = 0x0031, NumberId = 0x0203, LabelId = 0x0204, BoolErrId = 0x0205, BlankId = 0x0201;

    /// <summary>Records that only wrap others (future records, blocks, objects) or carry nothing here.</summary>
    private static readonly HashSet<int> Skipped = [0x0809, 0x000A, 0x003C, 0x0850, 0x0851, 0x0852, 0x0853, 0x0854, 0x0855];

    private readonly BiffChartBook _book;
    private readonly List<BiffFont> _chartFonts = [];
    private readonly Dictionary<(int Kind, int Series), Dictionary<int, Cell>> _cells = [];

    /// <summary>A cached cell of SERIESDATA: a number or a text, and its cell format.</summary>
    private readonly record struct Cell(double? Number, string? Text, int Xf);

    /// <summary>A record and the records between the Begin and End that follow it.</summary>
    private sealed class Node(int id, byte[] data)
    {
        public int Id { get; } = id;
        public byte[] Data { get; } = data;
        public List<Node> Children { get; } = [];
        public Node? Child(int id) => Children.Find(c => c.Id == id);
        public IEnumerable<Node> All(int id) => Children.Where(c => c.Id == id);
    }

    /// <summary>A series of the chart, as its Series record and the records under it say.</summary>
    private sealed class Series(int index, Node node)
    {
        public int Index { get; } = index;
        public Node Node { get; } = node;
        public string? Name { get; set; }
        public int Group { get; set; } = -1;

        /// <summary>The series a trendline or error bar belongs to (SerParent, 0-based), or -1.</summary>
        public int Parent { get; set; } = -1;

        public Node? Trend { get; set; }
        public Node? ErrorBar { get; set; }

        /// <summary>The series' own format (DataFormat xi 0xFFFF) and its points' formats.</summary>
        public Node? Format { get; set; }

        public Dictionary<int, Node> PointFormats { get; } = [];
        public List<Node> LegendExceptions { get; } = [];
        public int Order { get; set; }
        public bool TextCategories { get; set; }
        public int CategoryCount { get; set; }
        public int ValueCount { get; set; }
        public int BubbleCount { get; set; }
        public int? ValueFormat { get; set; }
    }

    /// <summary>A chart group: its ChartFormat, the record of its type and the axis group it is drawn on.</summary>
    private sealed class Group(int index, int axes, Node format, Node type)
    {
        public int Index { get; } = index;
        public int Axes { get; } = axes;
        public Node Format { get; } = format;
        public Node Type { get; } = type;
        public List<Series> Series { get; } = [];
        public bool ThreeD => Format.Child(Chart3dId) is not null;

        /// <summary>Its series are drawn as lines with markers (lines, scatter, radar), else filled.</summary>
        public bool Lines => Type.Id is LineId or RadarId || Type.Id == ScatterId && (U16(Type.Data, 4) & 1) == 0;
    }

    private BiffChartReader(BiffChartBook book) => _book = book;

    /// <summary>
    /// Reads the chart substream <paramref name="records"/> (from its BOF through its EOF) with the workbook's fonts,
    /// colours and formats.
    /// </summary>
    /// <exception cref="Core.Conversion.ConversionRefusedException">The chart has more series or points than Filee draws.</exception>
    public static ChartRead Read(IReadOnlyList<(int Id, byte[] Data)> records, BiffChartBook book) =>
        ChartReader.Read(Part(records, book), DrawingColors.FromTheme(null), book.Font(0)?.Name is { Length: > 0 } font ? font : null);

    /// <summary>The DrawingML chart part that says what <paramref name="records"/> say; null when they hold no chart.</summary>
    internal static XElement? Part(IReadOnlyList<(int Id, byte[] Data)> records, BiffChartBook book) => new BiffChartReader(book).Build(records);

    // ───────────────────────── Records ─────────────────────────

    /// <summary>The records as a tree: each record that a Begin follows holds the records up to the matching End.</summary>
    private static List<Node> Tree(IReadOnlyList<(int Id, byte[] Data)> records)
    {
        var root = new Node(0, []);
        var stack = new List<Node> { root };
        var flat = 0; // Begins past MaxDepth, read as if they weren't there
        foreach (var (id, data) in records)
        {
            if (id == BeginId)
            {
                var parent = stack[^1];
                if (stack.Count > MaxDepth || parent.Children.Count == 0)
                    flat++;
                else
                    stack.Add(parent.Children[^1]);
            }
            else if (id == EndId)
            {
                if (flat > 0)
                    flat--;
                else if (stack.Count > 1)
                    stack.RemoveAt(stack.Count - 1);
            }
            else if (!Skipped.Contains(id))
            {
                stack[^1].Children.Add(new Node(id, data));
            }
        }
        return root.Children;
    }

    private XElement? Build(IReadOnlyList<(int Id, byte[] Data)> records)
    {
        var top = Tree(records);
        if (top.Find(n => n.Id == ChartId) is not { } chart)
            return null;
        ReadFonts(top, chart);
        ReadCells(top);

        // Chart groups in the order of their ChartFormat records, on the primary (0) or secondary (1) axis group.
        var groups = new List<Group>();
        var axisGroups = new List<(int Index, Node Node)>();
        foreach (var parent in chart.All(AxisParentId))
        {
            var axes = Math.Clamp(U16(parent.Data, 0), 0, 1);
            axisGroups.Add((axes, parent));
            foreach (var format in parent.All(ChartFormatId))
            {
                if (format.Children.FirstOrDefault(c => c.Id is BarId or LineId or PieId or AreaId or ScatterId or RadarId or RadarAreaId or SurfId or BopPopId) is { } type)
                    groups.Add(new Group(groups.Count, axes, format, type));
                else
                    groups.Add(new Group(groups.Count, axes, format, new Node(BarId, []))); // a group of no known type: columns
            }
        }
        if (groups.Count == 0)
            return null;

        var series = ReadSeries(chart);
        foreach (var s in series.Where(s => s.Parent < 0))
            (groups.ElementAtOrDefault(s.Group) ?? groups[0]).Series.Add(s);
        var used = groups.Where(g => g.Series.Count > 0).ToList();
        if (used.Count == 0)
            return null;
        // Data labels: the Text records linked to series and points. (A 3D group beside a flat one is named by ChartReader.)
        var labels = LinkedTexts(chart);

        var plotArea = new XElement(C + "plotArea", new XElement(C + "layout"));
        foreach (var group in used.OrderBy(g => U16(g.Format.Data, 18)))
            plotArea.Add(GroupElement(group, series, labels));
        foreach (var (index, node) in axisGroups)
        {
            var groupsOnIt = used.Where(g => g.Axes == index).ToList();
            if (groupsOnIt.Count > 0)
                AddAxes(plotArea, node, index, groupsOnIt, axisGroups.Count > 1 && index == 1);
        }
        var primary = axisGroups.Find(a => a.Index == 0).Node ?? axisGroups[0].Node;
        var threeD = used.Any(g => g.ThreeD);
        if (FrameAfter(primary, PlotAreaId) is { } plotFrame)
            plotArea.Add(Frame(plotFrame, threeD ? null : "C0C0C0", "808080"));
        else
            plotArea.Add(new XElement(C + "spPr", new XElement(A + "noFill"), new XElement(A + "ln", new XElement(A + "noFill"))));

        var chartElement = new XElement(C + "chart");
        if (chart.All(TextId).FirstOrDefault(t => Link(t) is (1, _, _)) is { } titleText)
            chartElement.Add(Title(titleText, 12, true));
        else
            chartElement.Add(new XElement(C + "autoTitleDeleted", Val(1)));
        if (used.FirstOrDefault(g => g.ThreeD) is { } solid)
            AddView3D(chartElement, solid, primary);
        chartElement.Add(plotArea);
        if (used.Select(g => g.Format.Child(LegendId)).FirstOrDefault(l => l is not null) is { } legend)
            chartElement.Add(Legend(legend, series, used));
        var shtProps = chart.Child(ShtPropsId);
        chartElement.Add(new XElement(C + "plotVisOnly", Val(1)));
        chartElement.Add(new XElement(C + "dispBlanksAs", Val((shtProps?.Data.ElementAtOrDefault(2) ?? 0) switch { 1 => "zero", 2 => "span", _ => "gap" })));

        var space = new XElement(C + "chartSpace",
            new XElement(C + "date1904", Val(_book.Date1904 ? 1 : 0)),
            new XElement(C + "roundedCorners", Val(0)),
            chartElement);
        var area = chart.Child(FrameId) is { } frame ? Frame(frame, "FFFFFF", "000000") : null;
        if (area is not null)
            space.Add(area);
        if (DefaultFont(chart) is { } font)
            space.Add(new XElement(C + "txPr", new XElement(A + "bodyPr"), new XElement(A + "p", new XElement(A + "pPr", RunProperties("defRPr", font, null)), new XElement(A + "endParaRPr"))));
        return space;
    }

    /// <summary>The chart's own fonts (FONT records after FrtFontList), which FontX indexes past the workbook's name.</summary>
    private void ReadFonts(List<Node> top, Node chart)
    {
        foreach (var node in top.Concat(chart.Children).Where(n => n.Id == FontId && n.Data.Length >= 16).Take(1024))
            _chartFonts.Add(FontRecord(node.Data));
    }

    private static BiffFont FontRecord(byte[] data) => new(ShortString(data, 14), U16(data, 0) / 20.0, U16(data, 6) >= 600, (U16(data, 2) & 0x02) != 0, U16(data, 4));

    /// <summary>The cached values: SIIndex says what the Number, Label and blank records after it hold (1 values, 2 categories, 3 bubble sizes).</summary>
    private void ReadCells(List<Node> top)
    {
        var kind = 0;
        var count = 0L;
        foreach (var node in top)
        {
            if (node.Id == SIIndexId)
            {
                kind = U16(node.Data, 0);
                continue;
            }
            if (node.Id is not (NumberId or LabelId or BoolErrId or BlankId) || kind is < 1 or > 3 || node.Data.Length < 6)
                continue;
            if (++count > 3L * MaxPoints)
                throw TooMany();
            var (row, column, xf) = (U16(node.Data, 0), U16(node.Data, 2), U16(node.Data, 4));
            var cell = node.Id switch
            {
                NumberId => new Cell(F64(node.Data, 6), null, xf),
                LabelId => new Cell(null, Cut(LongString(node.Data, 6)), xf),
                _ => new Cell(null, null, xf),
            };
            if (!_cells.TryGetValue((kind, column), out var cells))
                _cells[(kind, column)] = cells = [];
            cells[row] = cell;
        }
    }

    private List<Series> ReadSeries(Node chart)
    {
        var result = new List<Series>();
        long points = 0;
        foreach (var node in chart.All(SeriesId))
        {
            if (result.Count >= ChartReader.MaxSeries)
                throw DocumentLimits.Exceeded($"A chart has more than {DocumentLimits.Count(ChartReader.MaxSeries)} series.");
            var s = new Series(result.Count, node)
            {
                TextCategories = U16(node.Data, 0) == 3,
                CategoryCount = Math.Min(U16(node.Data, 4), 32_767),
                ValueCount = Math.Min(U16(node.Data, 6), 32_767),
                BubbleCount = Math.Min(U16(node.Data, 10), 32_767),
                Order = result.Count,
            };
            points += Math.Max(s.ValueCount, s.CategoryCount) + s.BubbleCount;
            if (points > MaxPoints)
                throw TooMany();
            var brai = -1;
            foreach (var child in node.Children)
            {
                switch (child.Id)
                {
                    case BraiId:
                        brai = child.Data.ElementAtOrDefault(0);
                        if (brai == 1 && (U16(child.Data, 2) & 1) != 0)
                            s.ValueFormat = U16(child.Data, 4);
                        break;
                    case SeriesTextId when brai == 0:
                        s.Name = Cut(ShortString(child.Data, 2));
                        break;
                    case DataFormatId:
                        var point = U16(child.Data, 0);
                        if (point == 0xFFFF)
                        {
                            s.Format = child;
                            s.Order = U16(child.Data, 4);
                        }
                        else if (point < 32_000)
                        {
                            s.PointFormats[point] = child;
                        }
                        break;
                    case SerToCrtId:
                        s.Group = U16(child.Data, 0);
                        break;
                    case SerParentId:
                        s.Parent = U16(child.Data, 0) - 1;
                        break;
                    case SerAuxTrendId:
                        s.Trend = child;
                        break;
                    case SerAuxErrBarId:
                        s.ErrorBar = child;
                        break;
                    case LegendExceptionId:
                        s.LegendExceptions.Add(child);
                        break;
                }
            }
            result.Add(s);
        }
        return result;
    }

    /// <summary>The Text records of the chart linked to a series or point (data labels), by series and point (-1 for the series).</summary>
    private static Dictionary<(int Series, int Point), Node> LinkedTexts(Node chart)
    {
        var result = new Dictionary<(int Series, int Point), Node>();
        foreach (var text in chart.All(TextId))
        {
            if (Link(text) is (4, var series, var point))
                result[(series, point == 0xFFFF ? -1 : point)] = text;
        }
        return result;
    }

    /// <summary>What a Text record is attached to (ObjectLink: 1 chart, 2 value axis, 3 category axis, 4 series or point, 7 series axis).</summary>
    private static (int Object, int Series, int Point)? Link(Node text) =>
        text.Child(ObjectLinkId) is { } link ? (U16(link.Data, 0), U16(link.Data, 2), U16(link.Data, 4)) : null;

    // ───────────────────────── Chart groups and series ─────────────────────────

    private XElement GroupElement(Group group, List<Series> all, Dictionary<(int Series, int Point), Node> labels)
    {
        var type = group.Type;
        var data = type.Data;
        var threeD = group.ThreeD;
        // fVaried counts for a group of one series; pies and doughnuts colour their slices whatever their series.
        var varied = (U16(group.Format.Data, 16) & 1) != 0 && (group.Series.Count == 1 || type.Id is PieId or BopPopId);
        XElement element;
        switch (type.Id)
        {
            case BarId:
                {
                    var flags = U16(data, 4);
                    var (stacked, percent) = ((flags & 2) != 0, (flags & 4) != 0);
                    var cluster = (U16(group.Format.Child(Chart3dId)?.Data ?? [], 12) & 2) != 0;
                    element = new XElement(C + (threeD ? "bar3DChart" : "barChart"),
                        new XElement(C + "barDir", Val((flags & 1) != 0 ? "bar" : "col")),
                        new XElement(C + "grouping", Val(percent ? "percentStacked" : stacked ? "stacked" : threeD && !cluster ? "standard" : "clustered")),
                        new XElement(C + "varyColors", Val(varied ? 1 : 0)));
                    AddSeries(element, group, all, labels, varied);
                    element.Add(new XElement(C + "gapWidth", Val(Math.Clamp(U16(data, 2), 0, 500))));
                    if (!threeD)
                        element.Add(new XElement(C + "overlap", Val(Math.Clamp(I16(data, 0), -100, 100))));
                    if (CrtLine(group, 2) is { } seriesLines)
                        element.Add(new XElement(C + "serLines", seriesLines));
                    break;
                }
            case LineId:
                {
                    var flags = U16(data, 0);
                    element = new XElement(C + (threeD ? "line3DChart" : "lineChart"),
                        new XElement(C + "grouping", Val((flags & 2) != 0 ? "percentStacked" : (flags & 1) != 0 ? "stacked" : "standard")),
                        new XElement(C + "varyColors", Val(varied ? 1 : 0)));
                    AddSeries(element, group, all, labels, varied);
                    if (CrtLine(group, 0) is { } drop)
                        element.Add(new XElement(C + "dropLines", drop));
                    if (CrtLine(group, 1) is { } hiLow)
                        element.Add(new XElement(C + "hiLowLines", hiLow));
                    if (group.Format.All(DropBarId).ToList() is [var up, var down, ..])
                    {
                        element.Add(new XElement(C + "upDownBars",
                            new XElement(C + "gapWidth", Val(Math.Clamp(U16(up.Data, 0), 0, 500))),
                            new XElement(C + "upBars", Shape(up, "FFFFFF", "000000")),
                            new XElement(C + "downBars", Shape(down, "000000", "000000"))));
                    }
                    if (!threeD)
                        element.Add(new XElement(C + "marker", Val(1)));
                    break;
                }
            case PieId:
                {
                    var hole = U16(data, 2);
                    element = new XElement(C + (threeD ? "pie3DChart" : hole > 0 ? "doughnutChart" : "pieChart"),
                        new XElement(C + "varyColors", Val(varied ? 1 : 0)));
                    AddSeries(element, group, all, labels, varied);
                    if (!threeD)
                        element.Add(new XElement(C + "firstSliceAng", Val(Math.Clamp(U16(data, 0), 0, 360))));
                    if (hole > 0 && !threeD)
                        element.Add(new XElement(C + "holeSize", Val(Math.Clamp(hole, 10, 90))));
                    break;
                }
            case AreaId:
                {
                    var flags = U16(data, 0);
                    element = new XElement(C + (threeD ? "area3DChart" : "areaChart"),
                        new XElement(C + "grouping", Val((flags & 2) != 0 ? "percentStacked" : (flags & 1) != 0 ? "stacked" : "standard")),
                        new XElement(C + "varyColors", Val(0)));
                    AddSeries(element, group, all, labels, false);
                    if (CrtLine(group, 0) is { } drop)
                        element.Add(new XElement(C + "dropLines", drop));
                    break;
                }
            case ScatterId when (U16(data, 4) & 1) != 0:
                {
                    element = new XElement(C + "bubbleChart", new XElement(C + "varyColors", Val(varied ? 1 : 0)));
                    AddSeries(element, group, all, labels, varied);
                    element.Add(new XElement(C + "bubbleScale", Val(Math.Clamp(U16(data, 0), 0, 300))));
                    element.Add(new XElement(C + "showNegBubbles", Val((U16(data, 4) & 2) != 0 ? 1 : 0)));
                    element.Add(new XElement(C + "sizeRepresents", Val(U16(data, 2) == 2 ? "w" : "area")));
                    break;
                }
            case ScatterId:
                element = new XElement(C + "scatterChart", new XElement(C + "scatterStyle", Val("lineMarker")), new XElement(C + "varyColors", Val(varied ? 1 : 0)));
                AddSeries(element, group, all, labels, varied);
                break;
            case RadarId or RadarAreaId:
                element = new XElement(C + "radarChart", new XElement(C + "radarStyle", Val(type.Id == RadarAreaId ? "filled" : "marker")),
                    new XElement(C + "varyColors", Val(0)));
                AddSeries(element, group, all, labels, false);
                break;
            case SurfId:
                element = new XElement(C + (threeD ? "surface3DChart" : "surfaceChart"), new XElement(C + "wireframe", Val((U16(data, 0) & 1) == 0 ? 1 : 0)));
                AddSeries(element, group, all, labels, false);
                break;
            default: // BopPop
                {
                    var split = data.ElementAtOrDefault(1) != 0 ? 0 : U16(data, 2);
                    element = new XElement(C + "ofPieChart",
                        new XElement(C + "ofPieType", Val(data.ElementAtOrDefault(0) == 2 ? "bar" : "pie")),
                        new XElement(C + "varyColors", Val(varied ? 1 : 0)));
                    AddSeries(element, group, all, labels, varied);
                    element.Add(new XElement(C + "gapWidth", Val(Math.Clamp(I16(data, 10), 0, 500))));
                    element.Add(new XElement(C + "splitType", Val(split switch { 1 => "val", 2 => "percent", 3 => "cust", _ => "pos" })));
                    double? at = split switch
                    {
                        0 => Math.Clamp(I16(data, 4), 0, 32_000),
                        1 => F64(data, 12),
                        2 => Math.Clamp(I16(data, 6), 0, 100),
                        _ => null,
                    };
                    if (at is { } position)
                        element.Add(new XElement(C + "splitPos", Val(position)));
                    if (split == 3 && group.Format.Child(BopPopCustomId) is { } custom)
                        element.Add(CustomSplit(custom.Data));
                    element.Add(new XElement(C + "secondPieSize", Val(Math.Clamp(I16(data, 8), 5, 200))));
                    if (CrtLine(group, 2) is { } seriesLines)
                        element.Add(new XElement(C + "serLines", seriesLines));
                    break;
                }
        }
        // The gap between the series rows of a 3D group (Chart3d pcGap).
        if (threeD && type.Id is BarId or LineId or AreaId)
            element.Add(new XElement(C + "gapDepth", Val(Math.Clamp(U16(group.Format.Child(Chart3dId)!.Data, 10), 0, 500))));
        if (type.Id is PieId or BopPopId)
            return element;
        element.Add(new XElement(C + "axId", Val(AxisNumber(group.Axes, 0))), new XElement(C + "axId", Val(AxisNumber(group.Axes, 1))));
        if (threeD && (type.Id is SurfId || type.Id is LineId or AreaId or BarId && Series3D(group)))
            element.Add(new XElement(C + "axId", Val(AxisNumber(group.Axes, 2))));
        return element;
    }

    /// <summary>A 3D group whose series stand one behind the other (it has a series axis).</summary>
    private static bool Series3D(Group group)
    {
        if (group.Type.Id == LineId)
            return true;
        var flags = group.Type.Id == BarId ? U16(group.Type.Data, 4) : U16(group.Type.Data, 0);
        var stacked = group.Type.Id == BarId ? (flags & 2) != 0 : (flags & 1) != 0;
        var cluster = (U16(group.Format.Child(Chart3dId)?.Data ?? [], 12) & 2) != 0;
        return !stacked && !(group.Type.Id == BarId && cluster);
    }

    /// <summary>The axis ids of axis group <paramref name="axes"/>: category (or X) 0, value (or Y) 1, series 2.</summary>
    private static int AxisNumber(int axes, int which) => 100 * (axes + 1) + which + 1;

    /// <summary>c:custSplit from BopPopCustom's bits: padding in the high bits of the first byte, then a bit a point from the high end.</summary>
    private static XElement CustomSplit(byte[] data)
    {
        var count = Math.Min(U16(data, 0), 32_000);
        var split = new XElement(C + "custSplit");
        var bytes = 1 + count / 8;
        var bits = bytes * 8;
        var padding = bits - count;
        for (var point = 0; point < count - 1; point++)
        {
            // The bits from the most significant one down: padding first, then the points, the last bit an extra one.
            var bit = padding + point;
            var at = 2 + bit / 8;
            if (at < data.Length && (data[at] & (0x80 >> (bit % 8))) != 0)
                split.Add(new XElement(C + "secondPiePt", Val(point)));
        }
        return split;
    }

    /// <summary>The line after the CrtLine record of <paramref name="kind"/> in a group (0 drop, 1 high-low, 2 series, 3 leader lines), as c:spPr.</summary>
    private XElement? CrtLine(Group group, int kind)
    {
        var children = group.Format.Children;
        for (var i = 0; i < children.Count; i++)
        {
            if (children[i].Id == CrtLineId && U16(children[i].Data, 0) == kind)
                return new XElement(C + "spPr", Line(i + 1 < children.Count && children[i + 1].Id == LineFormatId ? children[i + 1] : null, "000000", 0.75));
        }
        return null;
    }

    private void AddSeries(XElement element, Group group, List<Series> all, Dictionary<(int Series, int Point), Node> labels, bool varied)
    {
        var kind = group.Type.Id;
        var scatter = kind == ScatterId;
        var bubble = scatter && (U16(group.Type.Data, 4) & 1) != 0;
        var round = kind is PieId or BopPopId;
        foreach (var s in group.Series.OrderBy(s => s.Order))
        {
            var auto = s.Order;
            var ser = new XElement(C + "ser", new XElement(C + "idx", Val(s.Index)), new XElement(C + "order", Val(s.Order)));
            ser.Add(new XElement(C + "tx", new XElement(C + "v", s.Name ?? $"Series{s.Index + 1}")));
            ser.Add(SeriesShape(s.Format, group, auto));
            if (round && s.Format?.Child(PieFormatId) is { } explode)
                ser.Add(new XElement(C + "explosion", Val(Math.Clamp(I16(explode.Data, 0), 0, 400))));
            if (group.Lines)
                ser.Add(Marker(s.Format, auto));
            var values = Numbers(s, 1, s.ValueCount, s.ValueFormat);
            var count = s.ValueCount;
            // Points of their own format; a group in varied colours colours every point.
            var styled = new SortedSet<int>(s.PointFormats.Keys);
            if (varied)
                styled.UnionWith(Enumerable.Range(0, count));
            foreach (var point in styled)
            {
                var format = s.PointFormats.GetValueOrDefault(point);
                var dPt = new XElement(C + "dPt", new XElement(C + "idx", Val(point)));
                if (round && format?.Child(PieFormatId) is { } pulled)
                    dPt.Add(new XElement(C + "explosion", Val(Math.Clamp(I16(pulled.Data, 0), 0, 400))));
                dPt.Add(SeriesShape(format ?? s.Format, group, varied ? point : auto, varied && (format?.Child(AreaFormatId) is null || Auto(format.Child(AreaFormatId)!))));
                ser.Add(dPt);
            }
            if (DataLabels(s, group, labels) is { } dLbls)
                ser.Add(dLbls);
            foreach (var trend in all.Where(t => t.Parent == s.Index && t.Trend is not null).Take(ChartReader.MaxTrendlines))
                ser.Add(Trendline(trend, labels));
            foreach (var bars in ErrorBars(s, all, scatter))
                ser.Add(bars);
            var categories = Categories(s, 2, s.CategoryCount);
            if (scatter)
            {
                if (categories is { Name.LocalName: "numLit" })
                    ser.Add(new XElement(C + "xVal", categories));
                ser.Add(new XElement(C + "yVal", values));
            }
            else
            {
                if (categories is not null)
                    ser.Add(new XElement(C + "cat", categories));
                ser.Add(new XElement(C + "val", values));
            }
            if (group.Lines && kind != RadarId)
                ser.Add(new XElement(C + "smooth", Val((U16(s.Format?.Child(SerFmtId)?.Data ?? [], 0) & 1) != 0 ? 1 : 0)));
            if (bubble)
            {
                ser.Add(new XElement(C + "bubbleSize", Numbers(s, 3, s.BubbleCount, null)));
                ser.Add(new XElement(C + "bubble3D", Val((U16(s.Format?.Child(SerFmtId)?.Data ?? [], 0) & 2) != 0 ? 1 : 0)));
            }
            element.Add(ser);
        }
    }

    /// <summary>The cached numbers of one kind (1 values, 3 bubble sizes) of a series as c:numLit, with their format.</summary>
    private XElement Numbers(Series s, int kind, int count, int? format)
    {
        var cells = _cells.GetValueOrDefault((kind, s.Index));
        var length = Math.Max(count, 0);
        var literal = new XElement(C + "numLit");
        var code = format is { } id ? _book.Format(id) : cells?.Values.Select(c => _book.CellFormat(c.Xf)).FirstOrDefault(f => f is not null);
        if (code is { Length: > 0 })
            literal.Add(new XElement(C + "formatCode", code));
        literal.Add(new XElement(C + "ptCount", Val(length)));
        foreach (var (point, cell) in (cells ?? []).Where(c => c.Key < length).OrderBy(c => c.Key))
        {
            if (cell.Number is { } value)
                literal.Add(Point(point, value.ToString("R", CultureInfo.InvariantCulture)));
        }
        return literal;
    }

    /// <summary>A series' categories (kind 2) as text (c:strLit) or numbers in their cells' format (c:numLit); null when it has none.</summary>
    private XElement? Categories(Series s, int kind, int count)
    {
        if (!_cells.TryGetValue((kind, s.Index), out var cells) || cells.Count == 0 || count <= 0)
            return null;
        var text = s.TextCategories || cells.Values.Any(c => c.Text is not null);
        var literal = new XElement(C + (text ? "strLit" : "numLit"));
        if (!text && cells.Values.Select(c => _book.CellFormat(c.Xf)).FirstOrDefault(f => f is not null) is { } code)
            literal.Add(new XElement(C + "formatCode", code));
        literal.Add(new XElement(C + "ptCount", Val(count)));
        foreach (var (point, cell) in cells.Where(c => c.Key < count).OrderBy(c => c.Key))
        {
            var value = cell.Text ?? cell.Number?.ToString("R", CultureInfo.InvariantCulture);
            if (value is not null)
                literal.Add(Point(point, value));
        }
        return literal;
    }

    private static XElement Point(int index, string value) => new(C + "pt", new XAttribute("idx", index), new XElement(C + "v", value));

    /// <summary>A series' or point's fill and outline: its own (AreaFormat, LineFormat), else Excel's automatic ones.</summary>
    private XElement SeriesShape(Node? format, Group group, int auto, bool autoFill = false)
    {
        var line = format?.Child(LineFormatId);
        var area = format?.Child(AreaFormatId);
        var properties = new XElement(C + "spPr");
        if (group.Lines)
        {
            var color = AutoColor(auto, lines: true);
            properties.Add(new XElement(A + "solidFill", Rgb(color)));
            properties.Add(Line(line, color, 1));
            return properties;
        }
        properties.Add(autoFill || area is null || Auto(area) ? new XElement(A + "solidFill", Rgb(AutoColor(auto, lines: false))) : Fill(area));
        properties.Add(Line(line, "000000", 0.75));
        return properties;
    }

    /// <summary>A line or scatter series' marker: its MarkerFormat, else Excel's automatic one for the series.</summary>
    private XElement Marker(Node? format, int auto)
    {
        var marker = format?.Child(MarkerFormatId);
        if (marker is null || (U16(marker.Data, 10) & 1) != 0)
        {
            var color = AutoColor(auto, lines: true);
            return new XElement(C + "marker", new XElement(C + "symbol", Val(AutoMarkers[auto % AutoMarkers.Length])), new XElement(C + "size", Val(5)),
                new XElement(C + "spPr", new XElement(A + "solidFill", Rgb(color)), new XElement(A + "ln", new XElement(A + "solidFill", Rgb(color)))));
        }
        var data = marker.Data;
        var symbol = U16(data, 8) switch
        {
            0 => "none",
            1 => "square",
            2 => "diamond",
            3 => "triangle",
            4 => "x",
            5 => "star",
            6 or 7 => "dash",
            8 => "circle",
            9 => "plus",
            _ => "square",
        };
        var flags = U16(data, 10);
        var size = Math.Clamp(BinaryPrimitives.ReadUInt32LittleEndian(Pad(data, 16)), 40u, 1440u) / 20.0;
        return new XElement(C + "marker", new XElement(C + "symbol", Val(symbol)), new XElement(C + "size", Val(Math.Clamp((int)Math.Round(size), 2, 72))),
            new XElement(C + "spPr",
                (flags & 0x10) != 0 ? new XElement(A + "noFill") : new XElement(A + "solidFill", Rgb(Hex(data, 4))),
                new XElement(A + "ln", (flags & 0x20) != 0 ? new XElement(A + "noFill") : new XElement(A + "solidFill", Rgb(Hex(data, 0))))));
    }

    /// <summary>Excel's automatic markers, series by series.</summary>
    private static readonly string[] AutoMarkers = ["diamond", "square", "triangle", "x", "star", "circle", "plus", "dash"];

    /// <summary>
    /// The automatic colour of series (or point) <paramref name="index"/>: the palette's chart fills (icv 24 on) for
    /// filled series, its chart lines (icv 32 on) for lines, then round the rest of the palette.
    /// </summary>
    private string AutoColor(int index, bool lines)
    {
        var start = lines ? 32 : 24;
        var icv = 8 + (start - 8 + index % 56) % 56;
        return _book.Color(icv)?.TrimStart('#') ?? Office.Sheets.XlsPalette.Default[icv - 8].TrimStart('#');
    }

    // ───────────────────────── Labels, trendlines, error bars ─────────────────────────

    private XElement? DataLabels(Series s, Group group, Dictionary<(int Series, int Point), Node> labels)
    {
        var own = s.Format?.Child(AttachedLabelId);
        var text = labels.GetValueOrDefault((s.Index, -1));
        var points = new List<XElement>();
        foreach (var ((series, point), pointText) in labels.Where(l => l.Key.Series == s.Index && l.Key.Point >= 0).OrderBy(l => l.Key.Point).Take(32_000))
        {
            var label = new XElement(C + "dLbl", new XElement(C + "idx", Val(point)));
            var flags = U16(pointText.Data, 24);
            if ((flags & 0x40) != 0)
            {
                label.Add(new XElement(C + "delete", Val(1)));
                points.Add(label);
                continue;
            }
            if ((flags & 0x10) == 0 && OwnText(pointText) is { } rich)
                label.Add(new XElement(C + "tx", rich));
            if (TextProperties(pointText) is { } txPr)
                label.Add(txPr);
            if (Position(pointText, group) is { } position)
                label.Add(new XElement(C + "dLblPos", Val(position)));
            var shows = s.PointFormats.GetValueOrDefault(point)?.Child(AttachedLabelId) is { } attached ? Shows(attached.Data, 0, group) : TextShows(flags, group);
            label.Add(shows);
            points.Add(label);
        }
        foreach (var (point, format) in s.PointFormats.OrderBy(p => p.Key))
        {
            if (format.Child(AttachedLabelId) is { } attached && !labels.ContainsKey((s.Index, point)))
                points.Add(new XElement(C + "dLbl", new XElement(C + "idx", Val(point)), Shows(attached.Data, 0, group)));
        }
        if (own is null && text is null && points.Count == 0)
            return null;
        var dLbls = new XElement(C + "dLbls", points);
        if (text is not null && TextProperties(text) is { } style)
            dLbls.Add(style);
        if (text is not null && Position(text, group) is { } at)
            dLbls.Add(new XElement(C + "dLblPos", Val(at)));
        dLbls.Add(own is not null ? Shows(own.Data, 0, group) : text is not null ? TextShows(U16(text.Data, 24), group) : Shows([], 0, group));
        return dLbls;
    }

    /// <summary>What an AttachedLabel record shows, as the c:show* elements.</summary>
    private static IEnumerable<XElement> Shows(byte[] data, int at, Group group)
    {
        var flags = U16(data, at);
        // On an area chart fShowLabel is the series name; elsewhere the category (with the percent: fShowLabelAndPerc).
        var area = group.Type.Id is AreaId or RadarAreaId;
        return Show((flags & 0x01) != 0, !area && (flags & 0x14) != 0, (flags & 0x40) != 0 || area && (flags & 0x10) != 0, (flags & 0x06) != 0, (flags & 0x20) != 0);
    }

    /// <summary>What a Text record's own flags show (fShowValue, fShowLabelAndPerc, fShowPercent, fShowBubbleSizes, fShowLabel).</summary>
    private static IEnumerable<XElement> TextShows(int flags, Group group)
    {
        var area = group.Type.Id is AreaId or RadarAreaId;
        var label = (flags & 0x4000) != 0;
        return Show((flags & 0x04) != 0, (flags & 0x0800) != 0 || label && !area, label && area, (flags & 0x1800) != 0, (flags & 0x2000) != 0);
    }

    private static IEnumerable<XElement> Show(bool value, bool category, bool series, bool percent, bool bubble) =>
    [
        new XElement(C + "showLegendKey", Val(0)),
        new XElement(C + "showVal", Val(value ? 1 : 0)),
        new XElement(C + "showCatName", Val(category ? 1 : 0)),
        new XElement(C + "showSerName", Val(series ? 1 : 0)),
        new XElement(C + "showPercent", Val(percent ? 1 : 0)),
        new XElement(C + "showBubbleSize", Val(bubble ? 1 : 0)),
    ];

    /// <summary>A data label's place (Text dlp) as c:dLblPos, or null for the automatic one.</summary>
    private static string? Position(Node text, Group group)
    {
        var place = U16(text.Data, 28) & 0x0F;
        var round = group.Type.Id is PieId or BopPopId;
        var bars = group.Type.Id == BarId;
        return place switch
        {
            1 => round || bars ? "outEnd" : null,
            2 => round || bars ? "inEnd" : null,
            3 => "ctr",
            4 => bars ? "inBase" : null,
            5 => bars || round ? null : "t",
            6 => bars || round ? null : "b",
            7 => bars || round ? null : "l",
            8 => bars || round ? null : "r",
            9 => round ? "bestFit" : null,
            _ => null,
        };
    }

    private XElement Trendline(Series trend, Dictionary<(int Series, int Point), Node> labels)
    {
        var data = trend.Trend!.Data;
        var order = data.ElementAtOrDefault(1);
        var type = data.ElementAtOrDefault(0) switch
        {
            0 when order <= 1 => "linear",
            0 => "poly",
            1 => "exp",
            2 => "log",
            3 => "power",
            4 => "movingAvg",
            _ => "linear",
        };
        var element = new XElement(C + "trendline");
        if (trend.Name is { Length: > 0 } name)
            element.Add(new XElement(C + "name", name));
        element.Add(new XElement(C + "spPr", Line(trend.Format?.Child(LineFormatId), "000000", 1)));
        element.Add(new XElement(C + "trendlineType", Val(type)));
        if (type == "poly")
            element.Add(new XElement(C + "order", Val(Math.Clamp((int)order, 2, 6))));
        if (type == "movingAvg")
            element.Add(new XElement(C + "period", Val(Math.Clamp((int)order, 2, 255))));
        if (F64(data, 12) is > 0 and var forward)
            element.Add(new XElement(C + "forward", Val(forward)));
        if (F64(data, 20) is > 0 and var backward)
            element.Add(new XElement(C + "backward", Val(backward)));
        // ChartNumNillable: 0xFFFF in its two most significant bytes is "none".
        if (U16(data, 8) != 0xFFFF && F64(data, 2) is { } intercept)
            element.Add(new XElement(C + "intercept", Val(intercept)));
        element.Add(new XElement(C + "dispRSqr", Val(data.ElementAtOrDefault(11) != 0 ? 1 : 0)));
        element.Add(new XElement(C + "dispEq", Val(data.ElementAtOrDefault(10) != 0 ? 1 : 0)));
        if (labels.GetValueOrDefault((trend.Index, -1)) is { } text && TextProperties(text) is { } txPr)
            element.Add(new XElement(C + "trendlineLbl", txPr));
        return element;
    }

    /// <summary>
    /// The error bars of a series: one Series record (with SerAuxErrBar) a direction and side; a side's pair becomes
    /// one c:errBars with both, a lone side or two that differ c:errBars of their own.
    /// </summary>
    private IEnumerable<XElement> ErrorBars(Series s, List<Series> all, bool scatter)
    {
        var bars = all.Where(b => b.Parent == s.Index && b.ErrorBar is not null).ToList();
        foreach (var alongX in new[] { false, true })
        {
            if (alongX && !scatter)
                continue;
            var plus = bars.FirstOrDefault(b => b.ErrorBar!.Data.ElementAtOrDefault(0) == (alongX ? 1 : 3));
            var minus = bars.FirstOrDefault(b => b.ErrorBar!.Data.ElementAtOrDefault(0) == (alongX ? 2 : 4));
            if (plus is null && minus is null)
                continue;
            var same = plus is not null && minus is not null && plus.ErrorBar!.Data.AsSpan(1, 1).SequenceEqual(minus.ErrorBar!.Data.AsSpan(1, 1))
                && F64(plus.ErrorBar.Data, 4) == F64(minus.ErrorBar.Data, 4);
            if (same || plus is null || minus is null)
            {
                yield return ErrorBar(plus ?? minus!, alongX, plus is not null && minus is not null ? "both" : plus is not null ? "plus" : "minus", plus, minus);
            }
            else
            {
                yield return ErrorBar(plus, alongX, "plus", plus, null);
                yield return ErrorBar(minus, alongX, "minus", null, minus);
            }
        }
    }

    private XElement ErrorBar(Series bar, bool alongX, string side, Series? plus, Series? minus)
    {
        var data = bar.ErrorBar!.Data;
        var type = data.ElementAtOrDefault(1) switch
        {
            1 => "percentage",
            3 => "stdDev",
            4 => "cust",
            5 => "stdErr",
            _ => "fixedVal",
        };
        var element = new XElement(C + "errBars",
            new XElement(C + "errDir", Val(alongX ? "x" : "y")),
            new XElement(C + "errBarType", Val(side)),
            new XElement(C + "errValType", Val(type)),
            new XElement(C + "noEndCap", Val(data.ElementAtOrDefault(2) != 0 ? 0 : 1)));
        if (type == "cust")
        {
            if (plus is not null)
                element.Add(new XElement(C + "plus", Numbers(plus, 1, Math.Max(U16(plus.ErrorBar!.Data, 12), plus.ValueCount), null)));
            if (minus is not null)
                element.Add(new XElement(C + "minus", Numbers(minus, 1, Math.Max(U16(minus.ErrorBar!.Data, 12), minus.ValueCount), null)));
        }
        else if (F64(data, 4) is { } value)
        {
            element.Add(new XElement(C + "val", Val(Math.Abs(value))));
        }
        element.Add(new XElement(C + "spPr", Line(bar.Format?.Child(LineFormatId), "000000", 0.75)));
        return element;
    }

    // ───────────────────────── Axes ─────────────────────────

    private void AddAxes(XElement plotArea, Node parent, int index, List<Group> groups, bool secondary)
    {
        var axes = parent.All(AxisId).ToList();
        if (axes.Count == 0)
            return;
        var scatter = groups.Any(g => g.Type.Id == ScatterId);
        var horizontal = groups.Any(g => g.Type.Id == BarId && (U16(g.Type.Data, 4) & 1) != 0);
        var titles = parent.All(TextId).ToList();
        foreach (var axis in axes)
        {
            var type = Math.Clamp(U16(axis.Data, 0), 0, 2);
            var id = AxisNumber(index, type);
            var cross = AxisNumber(index, type == 1 ? 0 : 1);
            var tick = axis.Child(TickId);
            var date = axis.Child(AxcExtId) is { } ext && (U16(ext.Data, 16) & 0x10) != 0;
            var name = type == 2 ? "serAx" : type == 1 || scatter ? "valAx" : date ? "dateAx" : "catAx";
            var position = type switch
            {
                0 when horizontal => secondary ? "r" : "l",
                0 => secondary ? "t" : "b",
                1 when horizontal => secondary ? "t" : "b",
                1 => secondary ? "r" : "l",
                _ => "b",
            };
            var range = axis.Child(ValueRangeId);
            var catRange = axis.Child(CatSerRangeId);
            var rangeFlags = U16(range?.Data ?? [], 40);
            var reversed = range is not null ? (rangeFlags & 0x40) != 0 : (U16(catRange?.Data ?? [], 6) & 4) != 0;
            var scaling = new XElement(C + "scaling");
            if (range is not null && (rangeFlags & 0x20) != 0)
                scaling.Add(new XElement(C + "logBase", Val(10)));
            scaling.Add(new XElement(C + "orientation", Val(reversed ? "maxMin" : "minMax")));
            if (range is not null && (rangeFlags & 0x02) == 0 && F64(range.Data, 8) is { } max)
                scaling.Add(new XElement(C + "max", Val(max)));
            if (range is not null && (rangeFlags & 0x01) == 0 && F64(range.Data, 0) is { } min)
                scaling.Add(new XElement(C + "min", Val(min)));

            var element = new XElement(C + name, new XElement(C + "axId", Val(id)), scaling);
            var hidden = tick is not null && tick.Data.ElementAtOrDefault(2) == 0 && tick.Data.ElementAtOrDefault(0) == 0 && !AxisLineShown(axis);
            element.Add(new XElement(C + "delete", Val(hidden ? 1 : 0)));
            element.Add(new XElement(C + "axPos", Val(position)));
            var link = type switch { 0 => 3, 1 => 2, _ => 7 };
            if (titles.FirstOrDefault(t => Link(t) is var (o, _, _) && o == link) is { } title)
                element.Add(Title(title, 10, true));
            if (axis.Child(IFmtRecordId) is { } numFmt && _book.Format(U16(numFmt.Data, 0)) is { } code)
                element.Add(new XElement(C + "numFmt", new XAttribute("formatCode", code), new XAttribute("sourceLinked", 0)));
            if (AxisLine(axis, 1) is { } major)
                element.Add(new XElement(C + "majorGridlines", new XElement(C + "spPr", Line(major, "000000", 0.75))));
            if (AxisLine(axis, 2) is { } minor)
                element.Add(new XElement(C + "minorGridlines", new XElement(C + "spPr", Line(minor, "000000", 0.75))));
            element.Add(new XElement(C + "majorTickMark", Val(TickMark(tick?.Data.ElementAtOrDefault(0) ?? 2))));
            element.Add(new XElement(C + "minorTickMark", Val(TickMark(tick?.Data.ElementAtOrDefault(1) ?? 0))));
            element.Add(new XElement(C + "tickLblPos", Val((tick?.Data.ElementAtOrDefault(2) ?? 3) switch { 0 => "none", 1 => "low", 2 => "high", _ => "nextTo" })));
            element.Add(new XElement(C + "spPr", AxisLine(axis, 0) is { } line ? Line(line, "000000", 0.75) : Line(null, "000000", 0.75)));
            if (AxisText(axis, tick) is { } txPr)
                element.Add(txPr);
            element.Add(new XElement(C + "crossAx", Val(cross)));
            // Where the other axis crosses this one is said by the other axis' record: ValueRange for a category axis, CatSerRange for a value axis.
            var other = axes.FirstOrDefault(a => a != axis && Math.Clamp(U16(a.Data, 0), 0, 2) == (type == 1 ? 0 : 1));
            element.Add(Crossing(other, scatter || type == 0));
            if (name == "valAx")
            {
                var between = other?.Child(CatSerRangeId) is not { } otherRange || (U16(otherRange.Data, 6) & 1) != 0;
                element.Add(new XElement(C + "crossBetween", Val(between && !scatter ? "between" : "midCat")));
                if (range is not null && (rangeFlags & 0x04) == 0 && F64(range.Data, 16) is > 0 and var unit)
                    element.Add(new XElement(C + "majorUnit", Val(unit)));
            }
            else if (catRange is not null && U16(catRange.Data, 2) > 1)
            {
                element.Add(new XElement(C + "tickLblSkip", Val(Math.Min(U16(catRange.Data, 2), 31_999))));
            }
            plotArea.Add(element);
        }
        // A 3D group with its series in depth needs a series axis; one the file leaves out is there but not shown.
        if (!axes.Any(a => U16(a.Data, 0) == 2) && groups.Any(g => g.ThreeD && (g.Type.Id is SurfId || g.Type.Id is LineId or AreaId or BarId && Series3D(g))))
        {
            plotArea.Add(new XElement(C + "serAx", new XElement(C + "axId", Val(AxisNumber(index, 2))), new XElement(C + "scaling", new XElement(C + "orientation", Val("minMax"))),
                new XElement(C + "delete", Val(1)), new XElement(C + "axPos", Val("b")), new XElement(C + "crossAx", Val(AxisNumber(index, 1)))));
        }
    }

    /// <summary>Where this axis crosses the other one, from the other axis' range record.</summary>
    private static XElement Crossing(Node? other, bool otherIsValue)
    {
        if (other?.Child(ValueRangeId) is { } range && otherIsValue)
        {
            var flags = U16(range.Data, 40);
            if ((flags & 0x80) != 0)
                return new XElement(C + "crosses", Val("max"));
            if ((flags & 0x10) == 0 && F64(range.Data, 32) is { } at)
                return new XElement(C + "crossesAt", Val(at));
        }
        else if (other?.Child(CatSerRangeId) is { } categories)
        {
            if ((U16(categories.Data, 6) & 2) != 0)
                return new XElement(C + "crosses", Val("max"));
            if (I16(categories.Data, 0) is > 1 and var at)
                return new XElement(C + "crossesAt", Val(at));
        }
        return new XElement(C + "crosses", Val("autoZero"));
    }

    private static string TickMark(int kind) => kind switch { 0 => "none", 1 => "in", 3 => "cross", _ => "out" };

    /// <summary>The LineFormat after an axis' AxisLine record of <paramref name="part"/> (0 the line, 1 major gridlines, 2 minor ones, 3 walls), or null.</summary>
    private static Node? AxisLine(Node axis, int part)
    {
        var children = axis.Children;
        for (var i = 0; i + 1 < children.Count; i++)
        {
            if (children[i].Id == AxisLineId && U16(children[i].Data, 0) == part && children[i + 1].Id == LineFormatId)
                return children[i + 1];
        }
        return null;
    }

    private static bool AxisLineShown(Node axis) =>
        AxisLine(axis, 0) is not { } line || (U16(line.Data, 8) & 0x04) != 0 || U16(line.Data, 4) != 5;

    /// <summary>An axis' label text: its font (FontX), colour and turn (Tick), as c:txPr.</summary>
    private XElement? AxisText(Node axis, Node? tick)
    {
        var font = axis.Child(FontXId) is { } fontX ? FontOf(U16(fontX.Data, 0)) : null;
        string? color = null;
        double? rotation = null;
        if (tick is not null)
        {
            var flags = U16(tick.Data, 24);
            if ((flags & 1) == 0)
                color = Hex(tick.Data, 4);
            if ((flags & 0x20) == 0)
            {
                var turn = U16(tick.Data, 28);
                var rot = (flags >> 2) & 0x07;
                rotation = rot switch
                {
                    2 => -90,
                    3 => 90,
                    _ => turn switch { <= 90 => -turn, <= 180 => turn - 90, _ => (double?)null },
                };
            }
        }
        if (font is null && color is null && rotation is null)
            return null;
        var body = new XElement(A + "bodyPr");
        if (rotation is { } r && r != 0)
            body.Add(new XAttribute("rot", (int)(r * 60000)));
        return new XElement(C + "txPr", body, new XElement(A + "p", new XElement(A + "pPr", RunProperties("defRPr", font, color)), new XElement(A + "endParaRPr")));
    }

    // ───────────────────────── 3D, legend, titles, frames ─────────────────────────

    private void AddView3D(XElement chart, Group group, Node primary)
    {
        var data = group.Format.Child(Chart3dId)!.Data;
        var flags = U16(data, 12);
        var pie = group.Type.Id == PieId;
        var view = new XElement(C + "view3D",
            new XElement(C + "rotX", Val(Math.Clamp(I16(data, 2), -90, 90))),
            new XElement(C + "rotY", Val(Math.Clamp(I16(data, 0), 0, 360))));
        if (!pie && (flags & 0x04) == 0)
            view.Add(new XElement(C + "hPercent", Val(Math.Clamp(I16(data, 6), 5, 500))));
        view.Add(new XElement(C + "depthPercent", Val(Math.Clamp(I16(data, 8), 20, 2000))));
        view.Add(new XElement(C + "rAngAx", Val(pie || (flags & 0x01) != 0 ? 0 : 1)));
        view.Add(new XElement(C + "perspective", Val(Math.Clamp(I16(data, 4), 0, 200))));
        chart.Add(view);
        // The walls take the category axis' area, the floor the value axis' (AXS AreaFormat, AxisLine 3).
        var axes = primary.All(AxisId).ToList();
        XElement Surface(string name, Node? axis)
        {
            var element = new XElement(C + name);
            var properties = new XElement(C + "spPr");
            properties.Add(axis?.Child(AreaFormatId) is { } area && !Auto(area) ? Fill(area) : name == "floor" ? new XElement(A + "noFill") : new XElement(A + "solidFill", Rgb("C0C0C0")));
            properties.Add(Line(axis is null ? null : AxisLine(axis, 3), "000000", 0.75));
            element.Add(properties);
            return element;
        }
        var walls = axes.FirstOrDefault(a => U16(a.Data, 0) == 0);
        var floor = axes.FirstOrDefault(a => U16(a.Data, 0) == 1);
        chart.Add(Surface("floor", floor), Surface("sideWall", walls), Surface("backWall", walls));
    }

    private XElement Legend(Node legend, List<Series> series, List<Group> groups)
    {
        var place = legend.Data.ElementAtOrDefault(16) switch
        {
            0 => "b",
            1 => "tr",
            2 => "t",
            4 => "l",
            _ => "r",
        };
        var element = new XElement(C + "legend", new XElement(C + "legendPos", Val(place)));
        // Entries deleted: a series' own (iss 0xFFFF), or a point's of a group in varied colours.
        var varied = groups.Any(g => (U16(g.Format.Data, 16) & 1) != 0 && (g.Series.Count == 1 || g.Type.Id is PieId or BopPopId));
        foreach (var s in series.Where(s => s.Parent < 0))
        {
            foreach (var exception in s.LegendExceptions)
            {
                if ((U16(exception.Data, 2) & 1) == 0)
                    continue;
                var entry = U16(exception.Data, 0);
                var index = entry == 0xFFFF ? s.Index : varied ? entry : -1;
                if (index >= 0)
                    element.Add(new XElement(C + "legendEntry", new XElement(C + "idx", Val(index)), new XElement(C + "delete", Val(1))));
            }
        }
        element.Add(new XElement(C + "overlay", Val(0)));
        if (legend.Child(TextId) is { } text && TextProperties(text) is { } txPr)
            element.Add(txPr);
        return element;
    }

    /// <summary>A chart or axis title from its Text record: its own text (AI's SeriesText) in its font.</summary>
    private XElement Title(Node text, double size, bool bold)
    {
        var title = new XElement(C + "title");
        var font = text.Child(FontXId) is { } fontX ? FontOf(U16(fontX.Data, 0)) : null;
        font ??= new BiffFont(_book.Font(0)?.Name ?? "Arial", size, bold, false, 0x7FFF);
        if (OwnText(text) is { } rich)
            title.Add(new XElement(C + "tx", rich));
        title.Add(new XElement(C + "overlay", Val(0)));
        title.Add(new XElement(C + "txPr", Body(text), new XElement(A + "p", new XElement(A + "pPr", RunProperties("defRPr", font, TextColor(text))), new XElement(A + "endParaRPr"))));
        return title;
    }

    /// <summary>A Text record's own text (its SeriesText) as c:rich, in its font; null when it has none.</summary>
    private XElement? OwnText(Node text)
    {
        if (text.Child(SeriesTextId) is not { } seriesText || Cut(ShortString(seriesText.Data, 2)) is not { Length: > 0 } value)
            return null;
        var font = text.Child(FontXId) is { } fontX ? FontOf(U16(fontX.Data, 0)) : null;
        var color = TextColor(text);
        var rich = new XElement(C + "rich", Body(text));
        foreach (var line in value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').Take(ChartReader.MaxLines))
            rich.Add(new XElement(A + "p", new XElement(A + "pPr", RunProperties("defRPr", font, color)), new XElement(A + "r", RunProperties("rPr", font, color), new XElement(A + "t", line))));
        return rich;
    }

    /// <summary>A Text record's turn (trot) as a:bodyPr.</summary>
    private static XElement Body(Node text)
    {
        var turn = U16(text.Data, 30);
        var body = new XElement(A + "bodyPr");
        var degrees = turn switch { > 0 and <= 90 => -turn, > 90 and <= 180 => turn - 90, _ => 0 };
        if (degrees != 0)
            body.Add(new XAttribute("rot", degrees * 60000));
        return body;
    }

    /// <summary>A Text record's colour (rgbText) unless it is automatic.</summary>
    private static string? TextColor(Node text) => text.Data.Length >= 26 && (U16(text.Data, 24) & 1) == 0 ? Hex(text.Data, 4) : null;

    /// <summary>A Text record's look (FontX, colour) as c:txPr, or null when it says nothing.</summary>
    private XElement? TextProperties(Node text)
    {
        var font = text.Child(FontXId) is { } fontX ? FontOf(U16(fontX.Data, 0)) : null;
        var color = TextColor(text);
        if (font is null && color is null)
            return null;
        return new XElement(C + "txPr", Body(text), new XElement(A + "p", new XElement(A + "pPr", RunProperties("defRPr", font, color)), new XElement(A + "endParaRPr")));
    }

    /// <summary>The chart's default text font: the Text after DefaultText 2 (all text not scaled), else the workbook's first font.</summary>
    private BiffFont? DefaultFont(Node chart)
    {
        var children = chart.Children;
        for (var i = 0; i + 1 < children.Count; i++)
        {
            if (children[i].Id == DefaultTextId && U16(children[i].Data, 0) is 2 or 3 && children[i + 1].Id == TextId
                && children[i + 1].Child(FontXId) is { } fontX && FontOf(U16(fontX.Data, 0)) is { } font)
                return font;
        }
        return _book.Font(0);
    }

    /// <summary>A FontX index: 0 the chart's default, up to the workbook's count a workbook font, past it the chart's own.</summary>
    private BiffFont? FontOf(int index)
    {
        if (index == 0)
            return null;
        if (index > _book.FontCount && index - _book.FontCount - 1 < _chartFonts.Count)
            return _chartFonts[index - _book.FontCount - 1];
        return _book.Font(index);
    }

    private XElement RunProperties(string name, BiffFont? font, string? color)
    {
        var run = new XElement(A + name);
        if (font is not null)
        {
            if (font.Size is > 0 and <= 409)
                run.Add(new XAttribute("sz", (int)Math.Round(font.Size * 100)));
            run.Add(new XAttribute("b", font.Bold ? 1 : 0), new XAttribute("i", font.Italic ? 1 : 0));
        }
        var fill = color ?? (font is not null && font.Color != 0x7FFF ? _book.Color(font.Color)?.TrimStart('#') : null);
        if (fill is not null)
            run.Add(new XElement(A + "solidFill", Rgb(fill)));
        if (font?.Name is { Length: > 0 } family)
            run.Add(new XElement(A + "latin", new XAttribute("typeface", family)));
        return run;
    }

    /// <summary>The Frame that follows record <paramref name="marker"/> (PlotArea) among a node's records.</summary>
    private static Node? FrameAfter(Node parent, int marker)
    {
        var children = parent.Children;
        for (var i = 0; i + 1 < children.Count; i++)
        {
            if (children[i].Id == marker && children[i + 1].Id == FrameId)
                return children[i + 1];
        }
        return null;
    }

    /// <summary>A Frame's fill and outline as c:spPr; automatic ones are <paramref name="autoFill"/> (null: none) and <paramref name="autoLine"/>.</summary>
    private XElement Frame(Node frame, string? autoFill, string autoLine)
    {
        var properties = new XElement(C + "spPr");
        var area = frame.Child(AreaFormatId);
        properties.Add(area is null || Auto(area)
            ? autoFill is null ? new XElement(A + "noFill") : new XElement(A + "solidFill", Rgb(autoFill))
            : Fill(area));
        properties.Add(Line(frame.Child(LineFormatId), autoLine, 0.75));
        return properties;
    }

    /// <summary>A DropBar's (or any record's) AreaFormat and LineFormat as c:spPr.</summary>
    private XElement Shape(Node node, string autoFill, string autoLine)
    {
        var area = node.Child(AreaFormatId);
        return new XElement(C + "spPr",
            area is null || Auto(area) ? new XElement(A + "solidFill", Rgb(autoFill)) : Fill(area),
            Line(node.Child(LineFormatId), autoLine, 0.75));
    }

    private static bool Auto(Node area) => (U16(area.Data, 10) & 1) != 0;

    /// <summary>An AreaFormat's fill: none, solid, or one of the cell patterns in its two colours.</summary>
    private static XElement Fill(Node area)
    {
        var data = area.Data;
        var pattern = U16(data, 8);
        var fore = Hex(data, 0);
        var back = Hex(data, 4);
        return pattern switch
        {
            0 => new XElement(A + "noFill"),
            1 => new XElement(A + "solidFill", Rgb(fore)),
            _ => new XElement(A + "pattFill", new XAttribute("prst", Patterns[Math.Clamp(pattern, 2, 18) - 2]),
                new XElement(A + "fgClr", Rgb(fore)), new XElement(A + "bgClr", Rgb(back))),
        };
    }

    /// <summary>The DrawingML presets of the patterns 2 to 18 (Excel's cell patterns).</summary>
    private static readonly string[] Patterns =
    [
        "pct50", "pct75", "pct25", "dkHorz", "dkVert", "dkDnDiag", "dkUpDiag", "smCheck", "trellis", "ltHorz", "ltVert",
        "ltDnDiag", "ltUpDiag", "smGrid", "diagCross", "pct10", "pct5",
    ];

    /// <summary>A LineFormat as a:ln (automatic: <paramref name="autoColor"/>, <paramref name="autoWidth"/> points).</summary>
    private static XElement Line(Node? format, string autoColor, double autoWidth)
    {
        var data = format?.Data;
        if (data is null || (U16(data, 8) & 1) != 0)
            return new XElement(A + "ln", new XAttribute("w", (int)(autoWidth * 12700)), new XElement(A + "solidFill", Rgb(autoColor)));
        var style = U16(data, 4);
        if (style == 5)
            return new XElement(A + "ln", new XElement(A + "noFill"));
        var width = I16(data, 6) switch { 0 => 1, 1 => 2, 2 => 3, _ => 0.5 };
        var color = Hex(data, 0);
        // The grey patterns are drawn as the colour lightened.
        if (style is >= 6 and <= 8)
            color = Mix(color, style == 6 ? 0.25 : style == 7 ? 0.5 : 0.75);
        var line = new XElement(A + "ln", new XAttribute("w", (int)(width * 12700)), new XElement(A + "solidFill", Rgb(color)));
        if (style is >= 1 and <= 4)
            line.Add(new XElement(A + "prstDash", Val(style switch { 1 => "dash", 2 => "sysDot", 3 => "dashDot", _ => "lgDashDotDot" })));
        return line;
    }

    /// <summary><paramref name="hex"/> mixed with white by <paramref name="white"/>.</summary>
    private static string Mix(string hex, double white)
    {
        var value = Convert.ToInt32(hex, 16);
        int Part(int shift) => (int)Math.Round(((value >> shift) & 0xFF) * (1 - white) + 255 * white);
        return $"{Part(16):X2}{Part(8):X2}{Part(0):X2}";
    }

    private static XElement Rgb(string hex) => new(A + "srgbClr", Val(hex));

    // ───────────────────────── Values ─────────────────────────

    private static XAttribute Val(object value) => new("val", Convert.ToString(value, CultureInfo.InvariantCulture)!);

    private static Core.Conversion.ConversionRefusedException TooMany() =>
        DocumentLimits.Exceeded($"A chart has more than {DocumentLimits.Count(MaxPoints)} points.");

    private static int U16(byte[] data, int at) => at >= 0 && at + 2 <= data.Length ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)) : 0;

    private static int I16(byte[] data, int at) => at >= 0 && at + 2 <= data.Length ? BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(at)) : 0;

    /// <summary>An Xnum at <paramref name="at"/>; null when the record is short or the number isn't finite.</summary>
    private static double? F64(byte[] data, int at) =>
        at >= 0 && at + 8 <= data.Length && BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(at)) is var value && double.IsFinite(value)
            ? Math.Clamp(value, -ChartReader.MaxValue, ChartReader.MaxValue)
            : null;

    /// <summary>A LongRGB (red, green, blue, reserved) at <paramref name="at"/> as "RRGGBB".</summary>
    private static string Hex(byte[] data, int at) =>
        $"{data.ElementAtOrDefault(at):X2}{data.ElementAtOrDefault(at + 1):X2}{data.ElementAtOrDefault(at + 2):X2}";

    /// <summary>Four bytes at <paramref name="at"/>, zeros past the record's end.</summary>
    private static byte[] Pad(byte[] data, int at) => [data.ElementAtOrDefault(at), data.ElementAtOrDefault(at + 1), data.ElementAtOrDefault(at + 2), data.ElementAtOrDefault(at + 3)];

    /// <summary>A ShortXLUnicodeString (1-byte length, flags, characters).</summary>
    private static string ShortString(byte[] data, int at)
    {
        if (at + 2 > data.Length)
            return "";
        var length = data[at];
        var wide = (data[at + 1] & 0x01) != 0;
        var bytes = Math.Min(length * (wide ? 2 : 1), data.Length - at - 2);
        return wide ? Encoding.Unicode.GetString(data, at + 2, bytes & ~1) : Encoding.Latin1.GetString(data, at + 2, bytes);
    }

    /// <summary>An XLUnicodeString (2-byte length, flags, characters).</summary>
    private static string LongString(byte[] data, int at)
    {
        if (at + 3 > data.Length)
            return "";
        var length = U16(data, at);
        var wide = (data[at + 2] & 0x01) != 0;
        var bytes = Math.Min(length * (wide ? 2 : 1), data.Length - at - 3);
        return wide ? Encoding.Unicode.GetString(data, at + 3, bytes & ~1) : Encoding.Latin1.GetString(data, at + 3, bytes);
    }

    private static string Cut(string text) => text.Length > ChartReader.MaxText ? text[..ChartReader.MaxText] : text;
}
