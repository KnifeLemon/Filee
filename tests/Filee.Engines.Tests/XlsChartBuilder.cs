// Builds the chart substream of an Excel 97–2003 chart ([MS-XLS] 2.1.7.20.1) for tests: a chart sheet's records, or
// an embedded chart's with the MSODRAWING and OBJ records that place it on a worksheet (XlsSheet.After). Only what
// readers need is written; the cached values go in SERIESDATA as Excel writes them.

using System.Text;
using static Filee.Engines.Tests.XlsBuilder;

namespace Filee.Engines.Tests;

/// <summary>One series of an <see cref="XlsChartBuilder"/> chart.</summary>
internal sealed class XlsChartSeries(string? name, double?[] values)
{
    public string? Name { get; } = name;
    public double?[] Values { get; } = values;

    /// <summary>X values of a scatter or bubble series, or null.</summary>
    public double[]? X { get; init; }

    public double[]? Bubbles { get; init; }

    /// <summary>The chart group (ChartFormat, 0-based) it is drawn in.</summary>
    public int Group { get; init; }

    /// <summary>Records of the series' own DataFormat (LineFormat, AreaFormat, MarkerFormat, AttachedLabel, ...), or null for none.</summary>
    public List<(int Id, byte[] Data)>? Format { get; init; }

    /// <summary>Points' own formats: point index → the records of its DataFormat.</summary>
    public Dictionary<int, List<(int Id, byte[] Data)>> PointFormats { get; init; } = [];
}

/// <summary>One chart group: its type record (Bar, Line, Pie, ...), the axis group it is on, and its other records.</summary>
internal sealed record XlsChartGroup(int Type, byte[] Data, int Axes = 0)
{
    /// <summary>Records after CrtLink: Chart3d, Legend (written by the builder for group 0), DropBar, CrtLine + LineFormat, ...</summary>
    public List<(int Id, byte[] Data)> Extra { get; init; } = [];

    public bool Varied { get; init; }
}

internal sealed class XlsChartBuilder
{
    public string? Title { get; init; }
    public string[]? Categories { get; init; }
    public double[]? NumberCategories { get; init; }

    /// <summary>The XF of the cached category cells (a date format, ...).</summary>
    public int CategoryXf { get; init; }

    public List<XlsChartSeries> Series { get; init; } = [];
    public List<XlsChartGroup> Groups { get; init; } = [Column()];

    /// <summary>The legend's place (Legend wType: 0 bottom, 1 corner, 2 top, 3 right, 4 left), or null for none.</summary>
    public int? Legend { get; init; } = 3;

    /// <summary>The scatter axes (two value axes) instead of a category and a value axis.</summary>
    public bool ValueAxes { get; init; }

    /// <summary>No axes at all (pies).</summary>
    public bool NoAxes { get; init; }

    /// <summary>Records of the value axis (ValueRange, Tick, AxisLine + LineFormat) written instead of the default gridlines.</summary>
    public List<(int Id, byte[] Data)>? ValueAxis { get; init; }

    /// <summary>Records of the category axis after CatSerRange and AxcExt (Tick, ...).</summary>
    public List<(int Id, byte[] Data)>? CategoryAxis { get; init; }

    /// <summary>Text records (data labels, axis titles) at the chart's level, each with its children.</summary>
    public List<List<(int Id, byte[] Data)>> Texts { get; init; } = [];

    /// <summary>Trendlines and error bars: extra Series records, each the records under its Series (SerParent, SerAuxTrend, ...).</summary>
    public List<List<(int Id, byte[] Data)>> AuxiliarySeries { get; init; } = [];

    /// <summary>Values of the auxiliary series (custom error bars), by auxiliary index.</summary>
    public Dictionary<int, double[]> AuxiliaryValues { get; init; } = [];

    /// <summary>The plot area's Frame (LineFormat, AreaFormat), or null for none.</summary>
    public List<(int Id, byte[] Data)>? PlotFrame { get; init; }

    /// <summary>The chart area's Frame, or null for none.</summary>
    public List<(int Id, byte[] Data)>? ChartFrame { get; init; }

    /// <summary>How blanks are drawn (ShtProps mdBlank: 0 gap, 1 zero, 2 span).</summary>
    public int Blanks { get; init; }

    // ───────────────────────── Records ─────────────────────────

    public const int Begin = 0x1033, End = 0x1034;

    public static XlsChartGroup Column(bool horizontal = false, bool stacked = false, bool percent = false, int gap = 150, int overlap = 0) =>
        new(0x1017, [.. U16(overlap), .. U16(gap), .. U16((horizontal ? 1 : 0) | (stacked || percent ? 2 : 0) | (percent ? 4 : 0))]);

    public static XlsChartGroup Line(bool stacked = false) => new(0x1018, U16(stacked ? 1 : 0));

    public static XlsChartGroup Pie(int start = 0, int hole = 0) => new(0x1019, [.. U16(start), .. U16(hole), .. U16(0)]) { Varied = true };

    public static XlsChartGroup Area(bool stacked = false) => new(0x101A, U16(stacked ? 1 : 0));

    public static XlsChartGroup Scatter(bool bubbles = false) => new(0x101B, [.. U16(100), .. U16(1), .. U16(bubbles ? 1 : 0)]);

    public static XlsChartGroup Radar(bool filled = false) => new(filled ? 0x1040 : 0x103E, [.. U16(1), .. U16(0)]);

    public static XlsChartGroup Surface(bool filled = true) => new(0x103F, U16(filled ? 1 : 0));

    /// <summary>LineFormat: colour, style (0 solid ... 5 none), weight (-1 hairline, 0 single, 1 double, 2 triple).</summary>
    public static (int, byte[]) LineFormat(int rgb, int style = 0, int weight = 0, bool auto = false) =>
        (0x1007, [.. Rgb(rgb), .. U16(style), .. U16(weight), .. U16((auto ? 1 : 0) | 0x04), .. U16(8)]);

    /// <summary>AreaFormat: foreground and background colours and the pattern (0 none, 1 solid, 2–18 patterns).</summary>
    public static (int, byte[]) AreaFormat(int fore, int back = 0xFFFFFF, int pattern = 1, bool auto = false) =>
        (0x100A, [.. Rgb(fore), .. Rgb(back), .. U16(pattern), .. U16(auto ? 1 : 0), .. U16(8), .. U16(9)]);

    /// <summary>MarkerFormat: border and fill colours, type (0 none, 1 square, 2 diamond, ... 8 circle), size in points.</summary>
    public static (int, byte[]) MarkerFormat(int fore, int back, int type, double size = 5) =>
        (0x1009, [.. Rgb(fore), .. Rgb(back), .. U16(type), .. U16(0), .. U16(8), .. U16(9), .. U32((int)(size * 20))]);

    /// <summary>AttachedLabel: 1 value, 2 percent, 4 category and percent, 0x10 category, 0x20 bubble size, 0x40 series name.</summary>
    public static (int, byte[]) AttachedLabel(int flags) => (0x100C, U16(flags));

    public static (int, byte[]) PieFormat(int explode) => (0x100B, U16(explode));

    public static (int, byte[]) SerFmt(bool smooth = false, bool bubbles3D = false) => (0x105D, U16((smooth ? 1 : 0) | (bubbles3D ? 2 : 0)));

    /// <summary>Chart3d: rotation, elevation, distance, height, depth, gap; clustered bars.</summary>
    public static (int, byte[]) Chart3d(int rotation = 20, int elevation = 15, int depth = 100, int height = 100, int gap = 150, bool cluster = false, bool pie = false) =>
        (0x103A, [.. U16(rotation), .. U16(elevation), .. U16(30), .. U16(height), .. U16(depth), .. U16(gap), .. U16((cluster ? 2 : 0) | (pie ? 0 : 0x10))]);

    /// <summary>CrtLine (0 drop lines, 1 high-low lines, 2 series lines) and the LineFormat after it.</summary>
    public static IEnumerable<(int, byte[])> CrtLine(int kind, int rgb) => [(0x101C, U16(kind)), LineFormat(rgb)];

    /// <summary>A DropBar (up or down bars) with its fill.</summary>
    public static IEnumerable<(int, byte[])> DropBar(int rgb, int gap = 150) => [(0x103D, U16(gap)), (Begin, []), LineFormat(0), AreaFormat(rgb), (End, [])];

    /// <summary>ValueRange: minimum, maximum and major unit (null = automatic), logarithmic, reversed.</summary>
    public static (int, byte[]) ValueRange(double? min = null, double? max = null, double? major = null, bool log = false, bool reversed = false, double? cross = null) =>
        (0x101F, [.. F64(min ?? 0), .. F64(max ?? 0), .. F64(major ?? 0), .. F64(0), .. F64(cross ?? 0),
            .. U16((min is null ? 1 : 0) | (max is null ? 2 : 0) | (major is null ? 4 : 0) | 8 | (cross is null ? 0x10 : 0) | (log ? 0x20 : 0) | (reversed ? 0x40 : 0))]);

    /// <summary>Tick: major and minor tick marks (0 none, 1 in, 2 out, 3 cross), label place (0 none, 3 next to the axis), turn.</summary>
    public static (int, byte[]) Tick(int major = 2, int minor = 0, int labels = 3, int rotation = 0, int? rgb = null) =>
        (0x101E, [(byte)major, (byte)minor, (byte)labels, 1, .. Rgb(rgb ?? 0), .. new byte[16], .. U16((rgb is null ? 1 : 0) | 2 | (rotation == 0 ? 0x20 : 0)), .. U16(8), .. U16(rotation)]);

    /// <summary>A Text record linked by ObjectLink (1 title, 2 value axis, 3 category axis, 4 series or point) with its own text and font.</summary>
    public static List<(int Id, byte[] Data)> LinkedText(int link, string? text = null, int series = 0, int point = 0, int labelFlags = 0, int? font = null, int position = 0, bool deleted = false)
    {
        var flags = 0x0001 | (text is null ? 0x0010 : 0) | (deleted ? 0x0040 : 0) | labelFlags;
        List<(int, byte[])> records =
        [
            (0x1025, [2, 2, .. U16(1), .. Rgb(0), .. U32(0), .. U32(0), .. U32(0), .. U32(0), .. U16(flags), .. U16(8), .. U16(position), .. U16(0)]),
            (Begin, []),
            (0x104F, new byte[20]),
        ];
        if (font is { } index)
            records.Add((0x1026, U16(index)));
        records.Add((0x1051, [0, 1, 0, 0, 0, 0, 0, 0]));
        if (text is not null)
            records.Add((0x100D, [0, 0, (byte)text.Length, 1, .. Encoding.Unicode.GetBytes(text)]));
        records.Add((0x1027, [.. U16(link), .. U16(series), .. U16(point)]));
        records.Add((End, []));
        return records;
    }

    /// <summary>A trendline of series <paramref name="parent"/> (0-based): type (0 polynomial/linear, 1 exp, 2 log, 3 power, 4 moving average).</summary>
    public static List<(int Id, byte[] Data)> Trendline(int parent, int type, int order = 1, bool equation = false, bool rSquared = false, double forward = 0, string? name = null)
    {
        List<(int, byte[])> records = [];
        records.Add((0x1051, [0, (byte)(name is null ? 0 : 1), 0, 0, 0, 0, 0, 0]));
        if (name is not null)
            records.Add((0x100D, [0, 0, (byte)name.Length, 1, .. Encoding.Unicode.GetBytes(name)]));
        records.Add((0x104A, U16(parent + 1)));
        records.Add((0x104B, [(byte)type, (byte)order, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, (byte)(equation ? 1 : 0), (byte)(rSquared ? 1 : 0), .. F64(forward), .. F64(0)]));
        return records;
    }

    /// <summary>Error bars of series <paramref name="parent"/>: direction (1 x plus, 2 x minus, 3 y plus, 4 y minus), kind (1 percent, 2 fixed, 3 std dev, 4 custom, 5 std error).</summary>
    public static List<(int Id, byte[] Data)> ErrorBar(int parent, int direction, int kind, double value, bool cap = true, int count = 0) =>
    [
        (0x1051, [0, 0, 0, 0, 0, 0, 0, 0]),
        (0x104A, U16(parent + 1)),
        (0x105B, [(byte)direction, (byte)kind, (byte)(cap ? 1 : 0), 1, .. F64(value), .. U16(count)]),
    ];

    /// <summary>The chart substream, BOF through EOF.</summary>
    public List<(int Id, byte[] Data)> Build()
    {
        var r = new List<(int Id, byte[] Data)>
        {
            (0x0809, Bof(0x0020)),
            (0x1001, U16(0)),
            (0x1002, [.. U32(0), .. U32(0), .. U32(480 << 16), .. U32(300 << 16)]),
            (Begin, []),
        };
        if (ChartFrame is not null)
            r.AddRange([(0x1032, U32(0x0003)), (Begin, []), .. ChartFrame, (End, [])]);
        var all = Series.Count;
        for (var i = 0; i < Series.Count; i++)
        {
            var s = Series[i];
            var categories = s.X?.Length ?? Categories?.Length ?? NumberCategories?.Length ?? 0;
            r.Add((0x1003, [.. U16(Categories is not null ? 3 : 1), .. U16(1), .. U16(categories), .. U16(s.Values.Length), .. U16(1), .. U16(s.Bubbles?.Length ?? 0)]));
            r.Add((Begin, []));
            r.Add((0x1051, [0, (byte)(s.Name is null ? 0 : 1), 0, 0, 0, 0, 0, 0]));
            if (s.Name is not null)
                r.Add((0x100D, [0, 0, (byte)s.Name.Length, 1, .. Encoding.Unicode.GetBytes(s.Name)]));
            r.Add((0x1051, [1, 2, 0, 0, 0, 0, 0, 0]));
            r.Add((0x1051, [2, 2, 0, 0, 0, 0, 0, 0]));
            r.Add((0x1051, [3, 1, 0, 0, 0, 0, 0, 0]));
            if (s.Format is not null)
                r.AddRange([(0x1006, [.. U16(0xFFFF), .. U16(i), .. U16(i), .. U16(0)]), (Begin, []), .. s.Format, (End, [])]);
            foreach (var (point, format) in s.PointFormats)
                r.AddRange([(0x1006, [.. U16(point), .. U16(i), .. U16(i), .. U16(0)]), (Begin, []), .. format, (End, [])]);
            r.Add((0x1045, U16(s.Group)));
            r.Add((End, []));
        }
        foreach (var aux in AuxiliarySeries)
        {
            r.Add((0x1003, [.. U16(1), .. U16(1), .. U16(0), .. U16(0), .. U16(1), .. U16(0)]));
            r.Add((Begin, []));
            r.AddRange(aux);
            r.Add((End, []));
        }
        r.Add((0x1044, [0x0E, 0x00, (byte)Blanks, 0]));
        r.Add((0x1046, U16(Groups.Any(g => g.Axes == 1) ? 2 : 1)));
        foreach (var axes in Groups.Select(g => g.Axes).Distinct().Order())
        {
            r.Add((0x1041, [.. U16(axes), .. new byte[16]]));
            r.Add((Begin, []));
            r.Add((0x104F, new byte[20]));
            if (!NoAxes)
            {
                if (ValueAxes)
                {
                    r.AddRange([(0x101D, [.. U16(0), .. new byte[16]]), (Begin, []), ValueRange(), Tick(), (End, [])]);
                }
                else
                {
                    r.AddRange([(0x101D, [.. U16(0), .. new byte[16]]), (Begin, []), (0x1020, [.. U16(1), .. U16(1), .. U16(1), .. U16(1)]),
                        (0x1062, [.. new byte[16], .. U16(0xEF)]), .. CategoryAxis ?? [Tick()], (End, [])]);
                }
                r.AddRange([(0x101D, [.. U16(1), .. new byte[16]]), (Begin, []), .. ValueAxis ?? [ValueRange(), Tick(), (0x1021, U16(1)), LineFormat(0x000000, weight: -1)], (End, [])]);
                if (axes == 0 && PlotFrame is not null)
                    r.AddRange([(0x1035, []), (0x1032, U32(0x0003)), (Begin, []), .. PlotFrame, (End, [])]);
            }
            for (var g = 0; g < Groups.Count; g++)
            {
                var group = Groups[g];
                if (group.Axes != axes)
                    continue;
                r.Add((0x1014, [.. new byte[16], .. U16(group.Varied ? 1 : 0), .. U16(g)]));
                r.Add((Begin, []));
                r.Add((group.Type, group.Data));
                r.Add((0x1022, new byte[10]));
                r.AddRange(group.Extra);
                if (g == 0 && Legend is { } place)
                    r.AddRange([(0x1015, [.. new byte[16], (byte)place, 1, .. U16(0x1F)]), (Begin, []), (0x104F, new byte[20]), .. LinkedText(0, null), (End, [])]);
                r.Add((End, []));
            }
            r.Add((End, []));
        }
        if (Title is not null)
            r.AddRange(LinkedText(1, Title));
        foreach (var text in Texts)
            r.AddRange(text);
        r.Add((End, []));

        // SERIESDATA: values (1), categories (2), bubble sizes (3), by series (column) and point (row).
        r.Add((0x0200, [.. U32(0), .. U32(0), .. U16(0), .. U16(0), .. U16(0)]));
        r.Add((0x1065, U16(1)));
        for (var i = 0; i < Series.Count; i++)
        {
            for (var p = 0; p < Series[i].Values.Length; p++)
            {
                if (Series[i].Values[p] is { } v)
                    r.Add((0x0203, [.. U16(p), .. U16(i), .. U16(0), .. F64(v)]));
            }
        }
        foreach (var (aux, values) in AuxiliaryValues)
        {
            for (var p = 0; p < values.Length; p++)
                r.Add((0x0203, [.. U16(p), .. U16(all + aux), .. U16(0), .. F64(values[p])]));
        }
        r.Add((0x1065, U16(2)));
        for (var i = 0; i < Series.Count; i++)
        {
            if (Series[i].X is { } x)
            {
                for (var p = 0; p < x.Length; p++)
                    r.Add((0x0203, [.. U16(p), .. U16(i), .. U16(0), .. F64(x[p])]));
            }
            else if (Categories is not null)
            {
                for (var p = 0; p < Categories.Length; p++)
                    r.Add((0x0204, [.. U16(p), .. U16(i), .. U16(CategoryXf), .. UnicodeString(Categories[p], byteLength: false)]));
            }
            else if (NumberCategories is not null)
            {
                for (var p = 0; p < NumberCategories.Length; p++)
                    r.Add((0x0203, [.. U16(p), .. U16(i), .. U16(CategoryXf), .. F64(NumberCategories[p])]));
            }
        }
        r.Add((0x1065, U16(3)));
        for (var i = 0; i < Series.Count; i++)
        {
            for (var p = 0; p < (Series[i].Bubbles?.Length ?? 0); p++)
                r.Add((0x0203, [.. U16(p), .. U16(i), .. U16(0), .. F64(Series[i].Bubbles![p])]));
        }
        r.Add((0x000A, []));
        return r;
    }

    /// <summary>
    /// The records that place the chart on a worksheet: an MSODRAWING whose client anchor runs from cell (row1, col1)
    /// to (row2, col2) with offsets in 1/1024 of a column and 1/256 of a row, the chart's OBJ (printed or not), then the
    /// chart's substream.
    /// </summary>
    public List<(int Id, byte[] Data)> Embedded(int row1, int column1, int row2, int column2, int dx1 = 0, int dy1 = 0, int dx2 = 0, int dy2 = 0, bool prints = true, int objectId = 1)
    {
        byte[] Header(int version, int instance, int type, int length) => [.. U16(version | instance << 4), .. U16(type), .. U32(length)];
        byte[] anchor = [.. Header(0, 0, 0xF010, 18), .. U16(0), .. U16(column1), .. U16(dx1), .. U16(row1), .. U16(dy1), .. U16(column2), .. U16(dx2), .. U16(row2), .. U16(dy2)];
        byte[] shape = [.. Header(2, 201, 0xF00A, 8), .. U32(1024 + objectId), .. U32(0x0A00)];
        byte[] data = [.. Header(0, 0, 0xF011, 0)];
        byte[] container = [.. Header(0xF, 0, 0xF004, shape.Length + anchor.Length + data.Length), .. shape, .. anchor, .. data];
        byte[] obj = [.. U16(0x15), .. U16(0x12), .. U16(0x05), .. U16(objectId), .. U16(prints ? 0x6011 : 0x6001), .. new byte[12], .. U32(0)];
        return [(0x00EC, container), (0x005D, obj), .. Build()];
    }

    /// <summary>Sample charts of every group kind, for pictures and fuzz seeds.</summary>
    internal static IEnumerable<(string Name, XlsChartBuilder Chart)> Samples()
    {
        string[] q = ["Q1", "Q2", "Q3", "Q4"];
        yield return ("columns", new XlsChartBuilder
        {
            Title = "Sales by quarter",
            Categories = q,
            Series = [new XlsChartSeries("North", [10, 20, 30, 40]), new XlsChartSeries("South", [15, 25, 20, 10]), new XlsChartSeries("West", [5, 12, 18, 22])],
            PlotFrame = [LineFormat(0x808080), AreaFormat(0xC0C0C0)],
            ChartFrame = [LineFormat(0), AreaFormat(0xFFFFFF)],
        });
        yield return ("lines", new XlsChartBuilder
        {
            Title = "Visitors",
            Categories = ["Jan", "Feb", "Mar", "Apr", "May", "Jun"],
            Series =
            [
                new XlsChartSeries("Web", [120, 150, 170, 160, 210, 240]) { Format = [AttachedLabel(0x01)] },
                new XlsChartSeries("Shop", [80, 95, null, 120, 130, 125]) { Format = [LineFormat(0xFF0000, weight: 1), MarkerFormat(0xFF0000, 0xFFFFFF, 8, 7), SerFmt(smooth: true)] },
            ],
            Groups = [Line()],
            AuxiliarySeries = [Trendline(0, 0, order: 1, equation: true, rSquared: true), ErrorBar(1, 3, 1, 10), ErrorBar(1, 4, 1, 10)],
            Texts = [LinkedText(4, series: 0, point: 2, deleted: true), LinkedText(2, "Visits")],
            Legend = 0,
            Blanks = 2,
        });
        yield return ("pie", new XlsChartBuilder
        {
            Title = "Share",
            Categories = ["Tea", "Coffee", "Water", "Juice"],
            Series = [new XlsChartSeries("Drinks", [40, 30, 20, 10]) { Format = [AttachedLabel(0x02)], PointFormats = { [0] = [PieFormat(15)] } }],
            Groups = [Pie()],
            NoAxes = true,
        });
        yield return ("3d-columns", new XlsChartBuilder
        {
            Title = "3D columns",
            Categories = q,
            Series = [new XlsChartSeries("North", [10, 20, 30, 40]), new XlsChartSeries("South", [15, 25, 20, 10])],
            Groups = [Column() with { Extra = [Chart3d(rotation: 20, elevation: 15)] }],
        });
        yield return ("stock", new XlsChartBuilder
        {
            Title = "Stock (high-low-close)",
            Categories = ["Mon", "Tue", "Wed", "Thu", "Fri"],
            Series =
            [
                new XlsChartSeries("High", [14, 15, 13, 16, 17]) { Format = [LineFormat(0, style: 5), MarkerFormat(0, 0, 0)] },
                new XlsChartSeries("Low", [9, 11, 10, 12, 13]) { Format = [LineFormat(0, style: 5), MarkerFormat(0, 0, 0)] },
                new XlsChartSeries("Close", [12, 11, 12, 15, 14]) { Format = [LineFormat(0, style: 5), MarkerFormat(0, 0, 6, 7)] },
            ],
            Groups = [new XlsChartGroup(0x1018, XlsBuilder.U16(0)) { Extra = [.. CrtLine(1, 0x000000), .. DropBar(0xFFFFFF), .. DropBar(0)] }],
        });
        yield return ("bubbles", new XlsChartBuilder
        {
            Title = "Bubbles",
            Series = [new XlsChartSeries("Bubbles", [1, 4, 9]) { X = [1, 2, 3], Bubbles = [5, 10, 20], Format = [SerFmt(bubbles3D: true)] }],
            Groups = [Scatter(bubbles: true)],
            ValueAxes = true,
            ValueAxis = [ValueRange(min: 0, max: 12, major: 2, reversed: false), Tick(rotation: 45, rgb: 0xFF0000)],
        });
        yield return ("combo", new XlsChartBuilder
        {
            Categories = q,
            Series = [new XlsChartSeries("Sales", [100, 200, 150, 300]), new XlsChartSeries("Margin", [0.1, 0.15, 0.12, 0.2]) { Group = 1 }],
            Groups = [Column(stacked: true), Line() with { Axes = 1 }],
        });
    }

    internal static byte[] Rgb(int rgb) => [(byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 0];

    internal static byte[] F64(double value) => BitConverter.GetBytes(value);
}
