// Draws a chart (ChartModel.cs) into a box of a page: fills and lines with Skia, text as glyph runs (ChartText), so
// the sheet printer, the document renderer and the slide renderer all draw charts the same way, as vectors.
//
// Layout follows Office's automatic one: the chart area less a margin; the title at the top; the legend on its side
// (right, left, top, bottom or the top-right corner) unless it overlays the plot; the plot area what is left, less its
// axes' labels and titles, or where a manual layout puts it. Value axes scale themselves as Excel does when the file
// sets no bounds: zero is kept unless the values sit far from it, the unit is 1, 2 or 5 times a power of ten giving
// a readable number of gridlines for the axis' length, and the top gets a little room above the highest value.
// Category labels are drawn level, wrapped at spaces, turned 45° or thinned out, whichever fits first.
//
// Plots: bars and columns (clustered with gap width and overlap, stacked, 100 %), lines (stacked, smooth, markers),
// areas, scatter (lines and markers), pies (first slice angle, pulled-out slices) and doughnuts (one ring per series),
// with data labels where the file asks for them. Combined plots share the plot area, each against its own axes.
//
// Bounds: data labels and markers drawn per chart are capped (MaxLabels, MaxMarkers), category labels are measured
// for at most MaxMeasured of them (the rest thinned out), and gridlines and ticks per axis at most MaxTicks.

using System.Globalization;
using Filee.Engines.Office.Sheets;
using Filee.Engines.Rendering;
using SkiaSharp;

namespace Filee.Engines.Charts;

internal sealed class ChartDrawer
{
    private const int MaxLabels = 4096;
    private const int MaxMarkers = 100_000;
    private const int MaxMeasured = 2000;
    private const int MaxTicks = 1000;

    private static readonly string[] AutoSymbols = ["diamond", "square", "triangle", "x", "star", "circle", "plus", "dash", "dot"];

    /// <summary>Lines of axes and gridlines when the file doesn't say.</summary>
    private static readonly SKColor AxisGray = new(0x86, 0x86, 0x86), GridGray = new(0xD9, 0xD9, 0xD9);

    private readonly Chart _chart;
    private readonly PageContent _page;
    private readonly ChartFonts _fonts;
    private readonly float _s;
    private readonly SKMatrix? _transform;
    private readonly SKRect? _clip;
    private readonly NumberFormatter _formatter;
    private readonly List<Action<SKCanvas>> _shapes = [];
    private readonly List<(ChartTextLine Line, float X, float Baseline, SKMatrix? Local)> _texts = [];
    private int _labels;
    private int _markers;

    private ChartDrawer(Chart chart, PageContent page, ChartFonts fonts, float scale, SKMatrix? transform, SKRect? clip)
    {
        _chart = chart;
        _page = page;
        _fonts = fonts;
        _s = scale;
        _transform = transform is { IsIdentity: false } ? transform : null;
        _clip = clip;
        _formatter = new NumberFormatter(chart.Date1904);
    }

    /// <summary>
    /// Draws <paramref name="chart"/> into <paramref name="box"/>, its text and lines <paramref name="scale"/> times their
    /// size (a sheet printed smaller or larger); <paramref name="transform"/> maps the box's coordinates onto the page and
    /// only what lies in <paramref name="clip"/> (page coordinates) is drawn.
    /// </summary>
    public static void Draw(PageContent page, Chart chart, SKRect box, ChartFonts fonts, float scale = 1, SKMatrix? transform = null, SKRect? clip = null)
    {
        if (!(box.Width >= 2 && box.Height >= 2) || !float.IsFinite(box.Left) || !float.IsFinite(box.Top) || !float.IsFinite(box.Width)
            || !float.IsFinite(box.Height) || !(scale > 0) || !float.IsFinite(scale))
            return;
        var drawer = new ChartDrawer(chart, page, fonts, scale, transform, clip);
        drawer.Run(box);
        drawer.Flush();
    }

    // ───────────────────────── Output ─────────────────────────

    private void Flush()
    {
        var shapes = _shapes.ToArray();
        var (transform, clip) = (_transform, _clip);
        _page.Draw(canvas =>
        {
            if (clip is { } area)
                canvas.ClipRect(area);
            if (transform is { } matrix)
                canvas.Concat(in matrix);
            foreach (var shape in shapes)
                shape(canvas);
        });
        foreach (var (line, x, baseline, local) in _texts)
        {
            SKMatrix? matrix = (_transform, local) switch
            {
                ({ } outer, { } inner) => outer.PreConcat(inner),
                ({ } outer, null) => outer,
                (null, { } inner) => inner,
                _ => null,
            };
            line.Draw(_page, x, baseline, matrix, _clip);
        }
    }

    private void Shape(Action<SKCanvas> draw) => _shapes.Add(draw);

    private void Clipped(SKRect area, Action<SKCanvas> draw) => _shapes.Add(canvas =>
    {
        canvas.Save();
        canvas.ClipRect(area);
        draw(canvas);
        canvas.Restore();
    });

    private static void FillPath(SKCanvas canvas, SKPath path, SKColor? fill)
    {
        if (fill is not { } color || color.Alpha == 0)
            return;
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };
        canvas.DrawPath(path, paint);
    }

    private static void StrokePath(SKCanvas canvas, SKPath path, Pen? pen, bool round = false)
    {
        if (pen is not { } p)
            return;
        using var dash = p.Dash is { Length: >= 2 } pattern
            ? SKPathEffect.CreateDash([.. (pattern.Length % 2 == 0 ? pattern : [.. pattern, .. pattern]).Select(d => Math.Max(0.05f, d * p.Width))], 0)
            : null;
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            Color = p.Color,
            StrokeWidth = p.Width,
            PathEffect = dash,
            StrokeJoin = round ? SKStrokeJoin.Round : SKStrokeJoin.Miter,
            StrokeCap = round ? SKStrokeCap.Round : SKStrokeCap.Butt,
        };
        canvas.DrawPath(path, paint);
    }

    /// <summary>A visible line: colour, width in page points, dashes.</summary>
    private readonly record struct Pen(SKColor Color, float Width, float[]? Dash);

    /// <summary>The pen of a line: the file's, or <paramref name="auto"/> colour and width; null for no line.</summary>
    private Pen? PenOf(ChartLine? line, SKColor? auto, float autoWidth)
    {
        if (line is { None: true })
            return null;
        var color = line?.Color ?? auto;
        var width = line?.Width ?? autoWidth;
        if (color is not { } c || c.Alpha == 0 || !(width > 0))
            return null;
        return new Pen(c, Math.Max(width, 0.25f) * _s, line?.Dash);
    }

    private static SKColor? FillOf(ChartShapeStyle style, SKColor? auto) =>
        style.Fill is { } fill ? fill.Alpha == 0 ? null : fill : auto;

    private ChartTextLine Text(string text, ChartTextStyle style) => _fonts.Line(text, style, style.Size * _s);

    /// <summary>Text with its top-left corner at (x, y).</summary>
    private void TextAt(ChartTextLine line, float x, float y) => _texts.Add((line, x, y + line.Ascent, null));

    /// <summary>Text centred on (cx, cy), turned by <paramref name="degrees"/> clockwise about that point.</summary>
    private void TextCentred(ChartTextLine line, float cx, float cy, float degrees = 0)
    {
        if (degrees == 0)
        {
            _texts.Add((line, cx - line.Width / 2, cy - line.Height / 2 + line.Ascent, null));
            return;
        }
        var local = SKMatrix.CreateTranslation(cx, cy).PreConcat(SKMatrix.CreateRotationDegrees(degrees));
        _texts.Add((line, -line.Width / 2, -line.Height / 2 + line.Ascent, local));
    }

    private string Format(double value, string? format)
    {
        var text = _formatter.Format(value, format);
        return text.Length > ChartReader.MaxText ? text[..ChartReader.MaxText] : text;
    }

    // ───────────────────────── Chart ─────────────────────────

    private void Run(SKRect box)
    {
        // The chart area: white with a light outline unless the file says otherwise.
        var area = _chart.Area;
        var fill = FillOf(area, SKColors.White);
        var outline = PenOf(area.Line, area.Line is null ? GridGray : null, 0.75f);
        var corner = _chart.RoundedCorners && outline is not null ? Math.Min(10 * _s, Math.Min(box.Width, box.Height) / 8) : 0;
        Shape(canvas =>
        {
            using var path = new SKPath();
            var inset = outline is { } pen ? pen.Width / 2 : 0;
            var rect = SKRect.Inflate(box, -inset, -inset);
            if (corner > 0)
                path.AddRoundRect(rect, corner, corner);
            else
                path.AddRect(rect);
            FillPath(canvas, path, fill);
            StrokePath(canvas, path, outline);
        });

        var pad = 7 * _s;
        var content = new SKRect(box.Left + pad, box.Top + pad, box.Right - pad, box.Bottom - pad);
        if (content.Width <= 0 || content.Height <= 0)
            return;
        content = Title(content, box);
        content = Legend(content, box);
        if (content.Width <= 4 || content.Height <= 4)
            return;
        Plots(content, box);
    }

    private SKRect Title(SKRect content, SKRect box)
    {
        if (_chart.Title is not { Lines.Count: > 0 } title)
            return content;
        var lines = title.Lines.Select(l => Text(l, title.Style)).ToList();
        var height = lines.Sum(l => l.Height);
        var y = content.Top;
        float? x = null;
        if (title.Layout is { } layout)
        {
            x = box.Left + (float)layout.X * box.Width;
            y = box.Top + (float)layout.Y * box.Height;
        }
        foreach (var line in lines)
        {
            TextAt(line, x ?? content.MidX - line.Width / 2, y);
            y += line.Height;
        }
        if (title.Overlay || title.Layout is not null)
            return content;
        return new SKRect(content.Left, content.Top + height + 6 * _s, content.Right, content.Bottom);
    }

    // ───────────────────────── Legend ─────────────────────────

    private enum KeyKind
    {
        Box,
        Line,
    }

    private sealed record LegendEntry(string Text, KeyKind Kind, SKColor? Fill, Pen? Line, ChartMarker? Marker, SKColor MarkerColor);

    private List<LegendEntry> LegendEntries()
    {
        var entries = new List<LegendEntry>();
        var plots = _chart.Plots;
        // One pie, doughnut or other plot of one series in varied colours: an entry per category.
        if (plots.Count == 1 && plots[0] is { Series.Count: > 0 } only
            && (only.Kind is ChartKind.Pie || only.Kind == ChartKind.Doughnut || only.VaryColors && only.Series.Count == 1 && only.Kind == ChartKind.Bar))
        {
            var series = only.Series[0];
            var count = Math.Min(series.Values.Count, MaxLabels);
            for (var i = 0; i < count; i++)
            {
                var style = series.Points.GetValueOrDefault(i) ?? series.Style;
                var auto = _chart.Palette.Color(i);
                entries.Add(new LegendEntry(Category(series, i), KeyKind.Box, FillOf(style, auto), PenOf(style.Line, null, 0.75f), null, auto));
            }
            return entries;
        }
        foreach (var plot in plots)
        {
            foreach (var series in plot.Series)
            {
                if (entries.Count >= MaxLabels)
                    return entries;
                var auto = _chart.Palette.Color(series.Index);
                var name = series.Name ?? $"Series{series.Index + 1}";
                if (plot.Kind is ChartKind.Line or ChartKind.Scatter)
                {
                    var line = plot.Lines ? PenOf(series.Style.Line, auto, plot.Kind == ChartKind.Line ? 2.25f : 1.5f) : null;
                    entries.Add(new LegendEntry(name, KeyKind.Line, null, line, MarkerOf(plot, series), auto));
                }
                else
                {
                    entries.Add(new LegendEntry(name, KeyKind.Box, FillOf(series.Style, auto), PenOf(series.Style.Line, null, 0.75f), null, auto));
                }
            }
        }
        return entries;
    }

    private SKRect Legend(SKRect content, SKRect box)
    {
        if (_chart.Legend is not { } legend)
            return content;
        var entries = LegendEntries();
        entries = [.. entries.Where((_, i) => !legend.Deleted.Contains(i))];
        if (entries.Count == 0)
            return content;
        var s = _s;
        var texts = entries.Select(e => Text(e.Text, legend.Style)).ToList();
        var keyWidth = entries.Any(e => e.Kind == KeyKind.Line) ? 20 * s : 7 * s;
        var gap = 4 * s;
        var lineHeight = Math.Max(texts.Max(t => t.Height), 7 * s);
        var vertical = legend.Position is "r" or "l" or "tr";
        var maxText = Math.Min(texts.Max(t => t.Width), (vertical ? 0.45f : 0.9f) * content.Width);
        var entryWidth = keyWidth + gap + maxText;

        // Entries in rows (top, bottom) or one column (right, left).
        var placed = new List<(int Index, float X, float Y)>();
        float width, height;
        if (vertical)
        {
            var rows = Math.Max(1, (int)Math.Min(entries.Count, (content.Height + 2 * s) / (lineHeight + 2 * s)));
            width = entryWidth + 2 * gap;
            height = rows * lineHeight + (rows - 1) * 2 * s + 2 * gap;
            for (var i = 0; i < rows; i++)
                placed.Add((i, gap, gap + i * (lineHeight + 2 * s)));
        }
        else
        {
            var rows = new List<List<(int Index, float Width)>>();
            var row = new List<(int Index, float Width)>();
            var x = 0f;
            for (var i = 0; i < entries.Count; i++)
            {
                var w = keyWidth + gap + Math.Min(texts[i].Width, maxText);
                if (row.Count > 0 && x + w > content.Width - 2 * gap)
                {
                    rows.Add(row);
                    row = [];
                    x = 0;
                    if ((rows.Count + 1) * (lineHeight + 2 * s) > content.Height / 2)
                        break;
                }
                row.Add((i, w));
                x += w + 12 * s;
            }
            if (row.Count > 0 && (rows.Count + 1) * (lineHeight + 2 * s) <= Math.Max(content.Height / 2, lineHeight + 2 * s))
                rows.Add(row);
            float RowWidth(List<(int Index, float Width)> r) => r.Sum(e => e.Width) + 12 * s * (r.Count - 1);
            var widest = rows.Count == 0 ? 0 : rows.Max(RowWidth);
            width = widest + 2 * gap;
            height = rows.Count * lineHeight + Math.Max(0, rows.Count - 1) * 2 * s + 2 * gap;
            for (var r = 0; r < rows.Count; r++)
            {
                var left0 = gap + (widest - RowWidth(rows[r])) / 2;
                foreach (var (index, w) in rows[r])
                {
                    placed.Add((index, left0, gap + r * (lineHeight + 2 * s)));
                    left0 += w + 12 * s;
                }
            }
        }

        float left, top;
        if (legend.Layout is { } layout)
        {
            left = box.Left + (float)layout.X * box.Width;
            top = box.Top + (float)layout.Y * box.Height;
        }
        else
        {
            (left, top) = legend.Position switch
            {
                "l" => (content.Left, content.MidY - height / 2),
                "t" => (content.MidX - width / 2, content.Top),
                "b" => (content.MidX - width / 2, content.Bottom - height),
                "tr" => (content.Right - width, content.Top),
                _ => (content.Right - width, content.MidY - height / 2),
            };
        }
        foreach (var (index, x, y) in placed)
        {
            var entry = entries[index];
            var keyLeft = left + x;
            var middle = top + y + lineHeight / 2;
            LegendKey(entry, new SKRect(keyLeft, middle - 3.5f * s, keyLeft + keyWidth, middle + 3.5f * s));
            var text = texts[index];
            TextAt(text, keyLeft + keyWidth + gap, middle - text.Height / 2);
        }
        if (legend.Overlay || legend.Layout is not null)
            return content;
        var margin = 6 * s;
        return legend.Position switch
        {
            "l" => new SKRect(content.Left + width + margin, content.Top, content.Right, content.Bottom),
            "t" => new SKRect(content.Left, content.Top + height + margin, content.Right, content.Bottom),
            "b" => new SKRect(content.Left, content.Top, content.Right, content.Bottom - height - margin),
            _ => new SKRect(content.Left, content.Top, content.Right - width - margin, content.Bottom),
        };
    }

    private void LegendKey(LegendEntry entry, SKRect key)
    {
        if (entry.Kind == KeyKind.Box)
        {
            var square = SKRect.Create(key.Left, key.MidY - key.Height / 2, key.Height, key.Height);
            var (fill, line) = (entry.Fill, entry.Line);
            Shape(canvas =>
            {
                using var path = new SKPath();
                path.AddRect(square);
                FillPath(canvas, path, fill);
                StrokePath(canvas, path, line);
            });
            return;
        }
        var pen = entry.Line;
        var (y, x0, x1) = (key.MidY, key.Left, key.Right);
        Shape(canvas =>
        {
            using var path = new SKPath();
            path.MoveTo(x0, y);
            path.LineTo(x1, y);
            StrokePath(canvas, path, pen);
        });
        if (entry.Marker is { } marker)
            Marker(marker, entry.MarkerColor, new SKPoint(key.MidX, y), counted: false);
    }

    // ───────────────────────── Plot area ─────────────────────────

    /// <summary>An axis as drawn: which way it runs, its scale (values) or count (categories), and its labels.</summary>
    private sealed class Axis(ChartAxis definition, bool horizontal)
    {
        public ChartAxis Definition { get; } = definition;
        public bool Horizontal { get; } = horizontal;
        public bool IsValue => Definition.IsValue;
        public double Low { get; set; } = double.PositiveInfinity;
        public double High { get; set; } = double.NegativeInfinity;
        public bool Percent { get; set; }
        public string? Format { get; set; }
        public Scale? Scale { get; set; }
        public int Count { get; set; }
        public IReadOnlyList<string> Labels { get; set; } = [];

        /// <summary>Points between the category ticks (bars always are).</summary>
        public bool Between { get; set; } = true;

        /// <summary>The other axis of the pair.</summary>
        public Axis? Cross { get; set; }

        public bool Shown => !Definition.Deleted;

        /// <summary>A bar plot is drawn against the axis (its points sit between the ticks).</summary>
        public bool HasBars { get; set; }

        /// <summary>The category labels last set for a length of the axis.</summary>
        public (float Length, CategoryLabels? Labels)? Measured { get; set; }

        /// <summary>The side the labels and title are on: b or t (horizontal), l or r (vertical).</summary>
        public string Side => Horizontal ? Definition.Position == "t" ? "t" : "b" : Definition.Position == "r" ? "r" : "l";

        public void Include(double value)
        {
            if (!double.IsFinite(value))
                return;
            Low = Math.Min(Low, value);
            High = Math.Max(High, value);
        }

        /// <summary>Where a category (0-based) or value sits along the axis, 0 to 1.</summary>
        public double Fraction(double value)
        {
            double f;
            if (Scale is { } scale)
                return scale.Fraction(value);
            var n = Math.Max(1, Count);
            f = Between ? (value + 0.5) / n : n == 1 ? 0.5 : value / (n - 1);
            return Definition.Reversed ? 1 - f : f;
        }

        /// <summary>The edge of category slot <paramref name="index"/>'s start (0) or end (1) along the axis.</summary>
        public double Edge(double index)
        {
            var n = Math.Max(1, Count);
            var f = Between ? index / n : n == 1 ? index : (index - 0.5) / (n - 1);
            return Definition.Reversed ? 1 - f : f;
        }
    }

    /// <summary>A value axis' range and unit.</summary>
    private sealed class Scale(double min, double max, double unit, double? log, bool reversed)
    {
        public double Min { get; } = min;
        public double Max { get; } = max;
        public double Unit { get; } = unit;
        public double? Log { get; } = log;

        public double Fraction(double value)
        {
            double f;
            if (Log is not null)
            {
                var lo = Math.Log(Min);
                var hi = Math.Log(Max);
                f = value > 0 ? (Math.Log(value) - lo) / (hi - lo) : double.NegativeInfinity;
            }
            else
            {
                f = (value - Min) / (Max - Min);
            }
            if (double.IsNaN(f))
                f = 0;
            f = Math.Clamp(f, -100, 100);
            return reversed ? 1 - f : f;
        }

        public List<double> Ticks()
        {
            var ticks = new List<double>();
            if (Log is { } logBase)
            {
                for (var v = Min; v <= Max * (1 + 1e-9) && ticks.Count < MaxTicks; v *= logBase)
                    ticks.Add(v);
                return ticks;
            }
            for (var k = 0; ticks.Count < MaxTicks; k++)
            {
                var v = Min + k * Unit;
                if (v > Max + Unit * 1e-9)
                    break;
                ticks.Add(Math.Abs(v) < Unit * 1e-9 ? 0 : v);
            }
            return ticks;
        }

        /// <summary>Where the crossing axis stands on this one (c:crosses), as a value.</summary>
        public double Crossing(ChartAxis crossing) => crossing.Crosses switch
        {
            ChartCrossing.Min => Min,
            ChartCrossing.Max => Max,
            ChartCrossing.At => Math.Clamp(crossing.CrossesAt, Min, Max),
            _ => Log is not null ? Min : Math.Clamp(0, Min, Max),
        };
    }

    private void Plots(SKRect content, SKRect box)
    {
        var plotStyle = _chart.PlotArea;
        if (_chart.Plots.All(p => p.Kind is ChartKind.Pie or ChartKind.Doughnut))
        {
            var area = Manual(content, box);
            PlotBackground(area, plotStyle);
            foreach (var plot in _chart.Plots)
                Round(plot, area);
            return;
        }

        // Axes in pairs, each plot against its own.
        var axes = new Dictionary<uint, Axis>();
        var pairs = new List<(ChartPlot Plot, Axis Category, Axis Value)>();
        foreach (var plot in _chart.Plots)
        {
            if (plot.Kind is ChartKind.Pie or ChartKind.Doughnut)
                continue; // a pie among other plots: not drawn (Office can't combine them either)
            var horizontal = plot is { Kind: ChartKind.Bar, Horizontal: true };
            var first = AxisOf(axes, plot.AxisIds.ElementAtOrDefault(0), plot.AxisIds.Count > 0, value: plot.Kind == ChartKind.Scatter, horizontal: !horizontal, fallbackId: uint.MaxValue - 1);
            var second = AxisOf(axes, plot.AxisIds.ElementAtOrDefault(1), plot.AxisIds.Count > 1, value: true, horizontal: horizontal, fallbackId: uint.MaxValue);
            first.Cross = second;
            second.Cross = first;
            pairs.Add((plot, first, second));
            Extents(plot, first, second);
        }
        if (pairs.Count == 0)
            return;

        // The plot area less what its axes need: vertical axes first (their labels don't depend on the width).
        var manual = _chart.PlotLayout;
        var outer = manual is { Inner: false } ? Manual(content, box) : content;
        var inner = outer;
        var vertical = axes.Values.Where(a => !a.Horizontal).ToList();
        var level = axes.Values.Where(a => a.Horizontal).ToList();
        foreach (var axis in vertical)
            Prepare(axis, inner.Height * 0.85f);
        if (manual is not { Inner: true })
        {
            foreach (var axis in vertical)
            {
                var need = Need(axis, inner.Height, 0.4f * outer.Width);
                inner = axis.Side == "r" ? new SKRect(inner.Left, inner.Top, inner.Right - need, inner.Bottom) : new SKRect(inner.Left + need, inner.Top, inner.Right, inner.Bottom);
            }
        }
        foreach (var axis in level)
            Prepare(axis, inner.Width);
        if (manual is not { Inner: true })
        {
            foreach (var axis in level)
            {
                var need = Need(axis, inner.Width, 0.5f * outer.Height);
                inner = axis.Side == "t" ? new SKRect(inner.Left, inner.Top + need, inner.Right, inner.Bottom) : new SKRect(inner.Left, inner.Top, inner.Right, inner.Bottom - need);
            }
            // Room for the top value label's upper half and the right end of a level axis' last label.
            inner = new SKRect(inner.Left, inner.Top + 4 * _s, inner.Right - 4 * _s, inner.Bottom);
        }
        else
        {
            inner = Manual(content, box);
        }
        if (inner.Width <= 2 || inner.Height <= 2)
            return;
        foreach (var axis in vertical)
            Prepare(axis, inner.Height);

        PlotBackground(inner, plotStyle);
        foreach (var axis in axes.Values)
            Gridlines(axis, inner);
        foreach (var (plot, category, value) in pairs)
        {
            switch (plot.Kind)
            {
                case ChartKind.Bar:
                    Bars(plot, category, value, inner);
                    break;
                case ChartKind.Area:
                    Areas(plot, category, value, inner);
                    break;
                case ChartKind.Line or ChartKind.Scatter:
                    Lines(plot, category, value, inner);
                    break;
            }
        }
        foreach (var axis in axes.Values)
            AxisLine(axis, inner);
    }

    private Axis AxisOf(Dictionary<uint, Axis> axes, uint id, bool given, bool value, bool horizontal, uint fallbackId)
    {
        if (given && axes.TryGetValue(id, out var known))
            return known;
        var definition = given ? _chart.Axis(id) : null;
        // An axis the file doesn't define is drawn as Office's default one would be, without labels.
        definition ??= new ChartAxis { Id = given ? id : fallbackId, IsValue = value, Position = horizontal ? "b" : "l", Deleted = !given };
        if (definition.IsValue != value)
            definition = definition with { IsValue = value };
        var axis = new Axis(definition, horizontal);
        axes[given ? id : fallbackId] = axis;
        return axis;
    }

    private SKRect Manual(SKRect content, SKRect box) =>
        _chart.PlotLayout is { } layout
            ? SKRect.Create(box.Left + (float)layout.X * box.Width, box.Top + (float)layout.Y * box.Height,
                (float)(layout.Width ?? 1 - layout.X) * box.Width, (float)(layout.Height ?? 1 - layout.Y) * box.Height)
            : content;

    private void PlotBackground(SKRect area, ChartShapeStyle style)
    {
        var fill = FillOf(style, null);
        var line = PenOf(style.Line, null, 0.75f);
        if (fill is null && line is null)
            return;
        Shape(canvas =>
        {
            using var path = new SKPath();
            path.AddRect(area);
            FillPath(canvas, path, fill);
            StrokePath(canvas, path, line);
        });
    }

    // ───────────────────────── Axes ─────────────────────────

    /// <summary>Gathers the values (and category count) a plot puts on its two axes.</summary>
    private static void Extents(ChartPlot plot, Axis first, Axis second)
    {
        var series = plot.Series;
        var count = series.Count == 0 ? 0 : series.Max(s => Math.Max(s.Values.Count, s.Categories?.Count ?? 0));
        if (plot.Kind == ChartKind.Scatter)
        {
            foreach (var s in series)
            {
                for (var i = 0; i < s.Values.Count; i++)
                {
                    if (s.Values[i] is not { } y)
                        continue;
                    var x = s.XValues is { } xs ? i < xs.Count ? xs[i] : null : i + 1;
                    if (x is not { } xv)
                        continue;
                    first.Include(xv);
                    second.Include(y);
                }
            }
            first.Format ??= first.Definition.Format;
            second.Format ??= second.Definition.Format ?? series.FirstOrDefault(s => s.Format is not null)?.Format;
            return;
        }
        if (!first.IsValue)
        {
            first.Count = Math.Max(first.Count, count);
            if (first.Labels.Count < count)
                first.Labels = Enumerable.Range(0, Math.Min(count, ChartReader.MaxPoints)).Select(i => Category(series, i)).ToList();
            // Bars sit between ticks; lines and areas wherever the value axis says.
            first.HasBars |= plot.Kind == ChartKind.Bar;
            first.Between = first.HasBars || second.Definition.Between;
        }
        else
        {
            for (var i = 0; i < count; i++)
                first.Include(i + 1);
        }
        second.Format ??= second.Definition.Format ?? (plot.Grouping == ChartGrouping.PercentStacked ? "0%" : series.FirstOrDefault(s => s.Format is not null)?.Format);
        if (plot.Grouping == ChartGrouping.Standard)
        {
            foreach (var s in series)
            {
                foreach (var v in s.Values)
                {
                    if (v is { } value)
                        second.Include(value);
                }
            }
            if (plot.Kind is ChartKind.Bar or ChartKind.Area)
                second.Include(0);
            return;
        }
        var percent = plot.Grouping == ChartGrouping.PercentStacked;
        second.Percent |= percent;
        for (var i = 0; i < count; i++)
        {
            double up = 0, down = 0;
            foreach (var s in series)
            {
                if (i < s.Values.Count && s.Values[i] is { } v)
                {
                    if (v >= 0)
                        up += v;
                    else
                        down += v;
                }
            }
            if (percent)
            {
                var total = up - down;
                if (total > 0)
                {
                    second.Include(up / total);
                    second.Include(down / total);
                }
            }
            else
            {
                second.Include(up);
                second.Include(down);
            }
        }
        second.Include(0);
    }

    /// <summary>Scales a value axis for <paramref name="length"/> points, or thins out a category axis' labels.</summary>
    private void Prepare(Axis axis, float length)
    {
        if (!axis.IsValue)
            return;
        var definition = axis.Definition;
        var (low, high) = double.IsFinite(axis.Low) ? (axis.Low, axis.High) : (0.0, 1.0);
        if (axis.Percent && definition.Min is null && definition.Max is null)
        {
            axis.Scale = new Scale(low < -1e-9 ? -1 : 0, 1, definition.MajorUnit ?? (length / _s > 250 ? 0.1 : 0.2), null, definition.Reversed);
            return;
        }
        if (definition.LogBase is { } logBase)
        {
            var positive = low > 0 ? low : high > 0 ? high / 100 : 1;
            var top = high > 0 ? high : 1;
            var min = definition.Min is > 0 and var lo ? lo : Math.Pow(logBase, Math.Floor(Math.Log(positive, logBase)));
            var max = definition.Max is { } hi && hi > min ? hi : Math.Pow(logBase, Math.Ceiling(Math.Log(top, logBase)));
            if (max <= min)
                max = min * logBase;
            axis.Scale = new Scale(min, max, logBase, logBase, definition.Reversed);
            return;
        }
        var label = Text("0", definition.Style).Height;
        var spacing = axis.Horizontal ? Math.Max(50 * _s, label * 4) : Math.Max(label * 2.2f, 22 * _s);
        var target = Math.Clamp((int)(length / spacing), 2, 10);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            axis.Scale = AutoScale(low, high, definition, target);
            if (!axis.Horizontal || !axis.Shown)
                break;
            // Level value labels must not touch: fewer of them when they do.
            var ticks = axis.Scale.Ticks();
            var widest = ticks.Take(MaxMeasured).Select(t => Text(Format(t, axis.Format), definition.Style).Width).DefaultIfEmpty(0).Max();
            if (ticks.Count <= 1 || widest * 1.3f <= length / (ticks.Count - 1) || target <= 2)
                break;
            target = Math.Max(2, (int)(length / (widest * 1.4f)));
        }
    }

    private static Scale AutoScale(double low, double high, ChartAxis axis, int target)
    {
        if (axis.Min is { } fixedMin)
            low = Math.Min(low, fixedMin);
        if (axis.Max is { } fixedMax)
            high = Math.Max(high, fixedMax);
        if (high < low)
            (low, high) = (high, low);
        // Zero is kept unless all values sit far above (or below) it.
        var min = axis.Min ?? (low >= 0 ? (high > 0 && (high - low) / high < 1 / 6.0 ? low : 0) : low);
        var max = axis.Max ?? (high <= 0 ? (low < 0 && (high - low) / -low < 1 / 6.0 ? high : 0) : high);
        if (max <= min)
        {
            var spread = Math.Abs(min) > 0 ? Math.Abs(min) / 10 : 1;
            (min, max) = axis.Min is not null ? (min, min + spread * 10) : axis.Max is not null ? (max - spread * 10, max) : (min - spread, max + spread);
        }
        var unit = axis.MajorUnit ?? Nice((max - min) / target);
        if (!(unit > 0) || !double.IsFinite(unit))
            unit = Math.Max(Math.Abs(max - min), 1e-300) / 5;
        if ((max - min) / unit > MaxTicks)
            unit = Nice((max - min) / 10);
        // A little room above the highest value (and below the lowest), to the next unit.
        if (axis.Max is null)
            max = high <= 0 && max <= 0 ? Math.Ceiling(max / unit - 1e-9) * unit : Math.Ceiling((max + 0.05 * (max - min)) / unit - 1e-9) * unit;
        if (axis.Min is null)
            min = low >= 0 && min >= 0 && min == 0 ? 0 : Math.Floor((min - (min < 0 ? 0.05 * (max - min) : 0)) / unit + 1e-9) * unit;
        if (max <= min)
            max = min + unit;
        return new Scale(min, max, unit, null, axis.Reversed);
    }

    /// <summary>1, 2 or 5 times a power of ten, at least <paramref name="value"/>.</summary>
    private static double Nice(double value)
    {
        if (!(value > 0) || !double.IsFinite(value))
            return 1;
        var power = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var f = value / power;
        return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10) * power;
    }

    /// <summary>The label of category <paramref name="index"/>: the first series' that has one, else its number.</summary>
    private static string Category(IReadOnlyList<ChartSeries> series, int index)
    {
        foreach (var s in series)
        {
            if (s.Categories is { } categories && index < categories.Count && categories[index] is { } label)
                return label;
        }
        return series.Any(s => s.Categories is not null) ? "" : (index + 1).ToString(CultureInfo.InvariantCulture);
    }

    private static string Category(ChartSeries series, int index) => Category([series], index);

    /// <summary>How a category axis' labels are set: every n-th one, level, wrapped or turned.</summary>
    private sealed record CategoryLabels(int Step, float Rotation, List<(int Index, List<ChartTextLine> Lines)> Labels);

    private CategoryLabels? Categories(Axis axis, float length)
    {
        if (axis.Measured is { } cached && cached.Length == length)
            return cached.Labels;
        var labels = MeasureCategories(axis, length);
        axis.Measured = (length, labels);
        return labels;
    }

    private CategoryLabels? MeasureCategories(Axis axis, float length)
    {
        if (axis.IsValue || axis.Definition.LabelPosition == "none" || axis.Count == 0)
            return null;
        var style = axis.Definition.Style;
        var n = axis.Count;
        var slot = length / n;
        var step = axis.Definition.LabelSkip > 0 ? axis.Definition.LabelSkip : Math.Max(1, (int)Math.Ceiling(n / (double)MaxMeasured));
        var lineHeight = Text("Ag", style).Height;
        List<(int, List<ChartTextLine>)> Measure(int every) =>
            [.. Enumerable.Range(0, (n + every - 1) / every).Select(k => k * every).Select(i => (i, new List<ChartTextLine> { Text(Label(axis, i), style) }))];

        if (!axis.Horizontal)
        {
            // Labels beside a vertical axis: as many as fit one above the other.
            if (axis.Definition.LabelSkip == 0)
                step = Math.Max(step, (int)Math.Ceiling(lineHeight * 1.1 / Math.Max(slot, 0.01)));
            return new CategoryLabels(step, 0, Measure(step));
        }
        var first = step;
        var labels = Measure(step);
        var widest = labels.Count == 0 ? 0 : labels.Max(l => l.Item2[0].Width);
        var fixedRotation = axis.Definition.LabelRotation;
        if (fixedRotation is { } turn && turn != 0)
        {
            var across = Math.Abs(Math.Sin(turn * Math.PI / 180)) > 0.5 ? lineHeight : widest;
            if (axis.Definition.LabelSkip == 0)
                step = Math.Max(step, (int)Math.Ceiling(across * 1.1 / Math.Max(slot, 0.01)));
            return new CategoryLabels(step, (float)turn, step == first ? labels : Measure(step));
        }
        if (widest <= slot * step * 0.95f)
            return new CategoryLabels(step, 0, labels);
        // Wrapped at spaces into up to three lines, each within its slot.
        var wrapped = new List<(int, List<ChartTextLine>)>();
        var fits = true;
        foreach (var (index, lines) in labels)
        {
            var broken = Wrap(lines[0].Text, style, slot * step * 0.95f, 3);
            if (broken is null)
            {
                fits = false;
                break;
            }
            wrapped.Add((index, broken));
        }
        if (fits)
            return new CategoryLabels(step, 0, wrapped);
        if (fixedRotation is null)
        {
            // Turned 45°: thinned out until their line heights fit side by side.
            if (axis.Definition.LabelSkip == 0)
                step = Math.Max(step, (int)Math.Ceiling(lineHeight * 1.5 / Math.Max(slot, 0.01)));
            return new CategoryLabels(step, -45, step == first ? labels : Measure(step));
        }
        if (axis.Definition.LabelSkip == 0)
            step = Math.Max(step, (int)Math.Ceiling(widest * 1.1 / Math.Max(slot, 0.01)));
        return new CategoryLabels(step, 0, Measure(step));
    }

    private static string Label(Axis axis, int index) => index < axis.Labels.Count ? axis.Labels[index] : (index + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>The text broken at spaces into lines no wider than <paramref name="width"/>; null when a word is wider or more lines are needed.</summary>
    private List<ChartTextLine>? Wrap(string text, ChartTextStyle style, float width, int maxLines)
    {
        var whole = Text(text, style);
        if (whole.Width <= width)
            return [whole];
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2)
            return null;
        var lines = new List<ChartTextLine>();
        var current = "";
        foreach (var word in words)
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (Text(candidate, style).Width <= width)
            {
                current = candidate;
                continue;
            }
            if (current.Length == 0)
                return null;
            lines.Add(Text(current, style));
            current = word;
            if (Text(word, style).Width > width || lines.Count >= maxLines)
                return null;
        }
        lines.Add(Text(current, style));
        return lines.Count <= maxLines ? lines : null;
    }

    /// <summary>How far an axis' labels, ticks and title reach out from the plot (points).</summary>
    private float Need(Axis axis, float length, float cap)
    {
        if (!axis.Shown)
            return 0;
        var definition = axis.Definition;
        var need = definition.MajorTicks is "out" or "cross" ? 4 * _s : 0;
        if (definition.LabelPosition != "none")
        {
            need += 3 * _s;
            if (axis.IsValue && axis.Scale is { } scale)
            {
                var texts = scale.Ticks().Take(MaxMeasured).Select(t => Text(Format(t, axis.Format), definition.Style)).ToList();
                if (texts.Count > 0)
                    need += axis.Horizontal ? texts.Max(t => t.Height) : texts.Max(t => t.Width);
            }
            else if (Categories(axis, length) is { } categories && categories.Labels.Count > 0)
            {
                if (!axis.Horizontal)
                    need += categories.Labels.Max(l => l.Lines.Max(t => t.Width));
                else if (categories.Rotation != 0)
                {
                    var radians = Math.Abs(categories.Rotation) * Math.PI / 180;
                    var widest = categories.Labels.Max(l => l.Lines.Max(t => t.Width));
                    var height = categories.Labels.Max(l => l.Lines.Max(t => t.Height));
                    need += (float)(widest * Math.Sin(radians) + height * Math.Cos(radians));
                }
                else
                {
                    need += categories.Labels.Max(l => l.Lines.Sum(t => t.Height));
                }
            }
        }
        if (definition.Title is { Lines.Count: > 0 } title)
            need += title.Lines.Sum(l => Text(l, title.Style).Height) + 4 * _s;
        return Math.Min(need, cap);
    }

    private float Along(Axis axis, double fraction, SKRect inner) =>
        axis.Horizontal ? inner.Left + (float)fraction * inner.Width : inner.Bottom - (float)fraction * inner.Height;

    /// <summary>Where an axis' line stands across the plot: at the crossing on the other axis (points).</summary>
    private float LinePosition(Axis axis, SKRect inner)
    {
        if (axis.Cross is not { } other)
            return axis.Horizontal ? inner.Bottom : inner.Left;
        double fraction;
        if (other.Scale is { } scale)
            fraction = scale.Fraction(scale.Crossing(axis.Definition));
        else
        {
            var n = Math.Max(1, other.Count);
            var index = axis.Definition.Crosses switch
            {
                ChartCrossing.Max => other.Between ? n : n - 1,
                ChartCrossing.At => Math.Clamp(axis.Definition.CrossesAt - 1, 0, other.Between ? n : n - 1),
                _ => 0,
            };
            fraction = other.Between ? other.Edge(index) : other.Fraction(index);
        }
        return Along(other, Math.Clamp(fraction, 0, 1), inner);
    }

    private void Gridlines(Axis axis, SKRect inner)
    {
        var definition = axis.Definition;
        if (definition.MajorGridlines is null && definition.MinorGridlines is null)
            return;
        var lines = new List<(float Position, Pen Pen)>();
        void Add(ChartLine? line, IEnumerable<double> fractions)
        {
            if (PenOf(line, GridGray, 0.75f) is not { } pen)
                return;
            foreach (var f in fractions)
            {
                if (f is >= -1e-6 and <= 1 + 1e-6)
                    lines.Add((Along(axis, f, inner), pen));
            }
        }
        if (axis.Scale is { } scale)
        {
            var ticks = scale.Ticks();
            if (definition.MinorGridlines is not null && scale.Log is null && ticks.Count < MaxTicks / 5)
                Add(definition.MinorGridlines, Enumerable.Range(0, (ticks.Count - 1) * 5 + 1).Select(k => scale.Fraction(scale.Min + k * scale.Unit / 5)));
            Add(definition.MajorGridlines, ticks.Select(scale.Fraction));
        }
        else if (axis.Count > 0)
        {
            var count = Math.Min(axis.Count, MaxTicks);
            Add(definition.MajorGridlines, axis.Between
                ? Enumerable.Range(0, count + 1).Select(i => axis.Edge(i))
                : Enumerable.Range(0, count).Select(i => axis.Fraction(i)));
        }
        if (lines.Count == 0)
            return;
        var horizontal = axis.Horizontal;
        Shape(canvas =>
        {
            foreach (var (position, pen) in lines)
            {
                using var path = new SKPath();
                if (horizontal)
                {
                    path.MoveTo(position, inner.Top);
                    path.LineTo(position, inner.Bottom);
                }
                else
                {
                    path.MoveTo(inner.Left, position);
                    path.LineTo(inner.Right, position);
                }
                StrokePath(canvas, path, pen);
            }
        });
    }

    /// <summary>An axis' line, ticks, labels and title.</summary>
    private void AxisLine(Axis axis, SKRect inner)
    {
        if (!axis.Shown)
            return;
        var definition = axis.Definition;
        var at = LinePosition(axis, inner);
        var side = axis.Side;
        var outward = side is "b" or "r" ? 1 : -1;
        var pen = PenOf(definition.Line, AxisGray, 0.75f);
        var tick = 4 * _s;
        var (tickFrom, tickTo) = definition.MajorTicks switch
        {
            "out" => (0f, tick),
            "in" => (-tick, 0f),
            "cross" => (-tick, tick),
            _ => (0f, 0f),
        };
        var fractions = axis.Scale is { } scale ? scale.Ticks().Select(scale.Fraction).ToList()
            : axis.Between ? [.. Enumerable.Range(0, Math.Min(axis.Count, MaxTicks) + 1).Select(i => axis.Edge(i))]
            : [.. Enumerable.Range(0, Math.Min(axis.Count, MaxTicks)).Select(i => axis.Fraction(i))];
        if (pen is { } p)
        {
            var horizontal = axis.Horizontal;
            var marks = tickFrom != tickTo ? fractions.Where(f => f is >= -1e-6 and <= 1 + 1e-6).Select(f => Along(axis, f, inner)).ToList() : [];
            Shape(canvas =>
            {
                using var path = new SKPath();
                if (horizontal)
                {
                    path.MoveTo(inner.Left, at);
                    path.LineTo(inner.Right, at);
                    foreach (var x in marks)
                    {
                        path.MoveTo(x, at + tickFrom * outward);
                        path.LineTo(x, at + tickTo * outward);
                    }
                }
                else
                {
                    path.MoveTo(at, inner.Top);
                    path.LineTo(at, inner.Bottom);
                    foreach (var y in marks)
                    {
                        path.MoveTo(at + tickFrom * outward, y);
                        path.LineTo(at + tickTo * outward, y);
                    }
                }
                StrokePath(canvas, path, p);
            });
        }

        // Labels next to the line, or at the low or high edge of the plot.
        var labelsAt = definition.LabelPosition switch
        {
            "low" => axis.Horizontal ? (axis.Cross?.Definition.Reversed == true ? inner.Top : inner.Bottom) : (axis.Cross?.Definition.Reversed == true ? inner.Right : inner.Left),
            "high" => axis.Horizontal ? (axis.Cross?.Definition.Reversed == true ? inner.Bottom : inner.Top) : (axis.Cross?.Definition.Reversed == true ? inner.Left : inner.Right),
            _ => at,
        };
        var offset = (definition.MajorTicks is "out" or "cross" ? tick : 0) + 3 * _s;
        var reach = offset;
        if (definition.LabelPosition != "none")
        {
            if (axis.Scale is { } valueScale)
            {
                foreach (var value in valueScale.Ticks())
                {
                    var text = Text(Format(value, axis.Format), definition.Style);
                    var position = Along(axis, valueScale.Fraction(value), inner);
                    Place(text, axis.Horizontal, position, labelsAt, offset, outward);
                    reach = Math.Max(reach, offset + (axis.Horizontal ? text.Height : text.Width));
                }
            }
            else if (Categories(axis, axis.Horizontal ? inner.Width : inner.Height) is { } categories)
            {
                foreach (var (index, lines) in categories.Labels)
                {
                    var position = Along(axis, axis.Fraction(index), inner);
                    if (categories.Rotation != 0)
                    {
                        var line = lines[0];
                        // Turned labels end at their tick.
                        var radians = categories.Rotation * (float)Math.PI / 180;
                        var anchor = axis.Horizontal ? new SKPoint(position, labelsAt + offset * outward) : new SKPoint(labelsAt + offset * outward, position);
                        var local = SKMatrix.CreateTranslation(anchor.X, anchor.Y).PreConcat(SKMatrix.CreateRotationDegrees(categories.Rotation));
                        var x = categories.Rotation < 0 ? -line.Width : 0;
                        _texts.Add((line, x, (line.Ascent - line.Descent) / 2, local));
                        reach = Math.Max(reach, offset + (float)Math.Abs(line.Width * Math.Sin(radians)) + line.Height);
                        continue;
                    }
                    var total = lines.Sum(l => l.Height);
                    var y = 0f;
                    foreach (var line in lines)
                    {
                        if (axis.Horizontal)
                        {
                            var top = outward > 0 ? labelsAt + offset + y : labelsAt - offset - total + y;
                            TextAt(line, position - line.Width / 2, top);
                        }
                        else
                        {
                            Place(line, false, position - total / 2 + y + line.Height / 2, labelsAt, offset, outward);
                        }
                        y += line.Height;
                    }
                    reach = Math.Max(reach, offset + (axis.Horizontal ? total : lines.Max(l => l.Width)));
                }
            }
        }

        // The title beyond the labels, turned for a vertical axis.
        if (definition.Title is { Lines.Count: > 0 } title)
        {
            var lines = title.Lines.Select(l => Text(l, title.Style)).ToList();
            var height = lines.Sum(l => l.Height);
            var edge = axis.Horizontal ? (outward > 0 ? Math.Max(labelsAt, inner.Bottom) : Math.Min(labelsAt, inner.Top)) : (outward > 0 ? Math.Max(labelsAt, inner.Right) : Math.Min(labelsAt, inner.Left));
            var distance = reach + 4 * _s;
            var y = -height / 2;
            foreach (var line in lines)
            {
                if (axis.Horizontal)
                    TextCentred(line, inner.MidX, edge + outward * (distance + height / 2) + y + line.Height / 2);
                else
                    TextCentred(line, edge + outward * (distance + height / 2) + (y + line.Height / 2) * -outward, inner.MidY, outward > 0 ? 90 : -90);
                y += line.Height;
            }
        }
    }

    /// <summary>A label beside a level (or vertical) axis at <paramref name="position"/> along it.</summary>
    private void Place(ChartTextLine text, bool horizontal, float position, float line, float offset, int outward)
    {
        if (horizontal)
        {
            var top = outward > 0 ? line + offset : line - offset - text.Height;
            TextAt(text, position - text.Width / 2, top);
        }
        else
        {
            var left = outward > 0 ? line + offset : line - offset - text.Width;
            TextAt(text, left, position - text.Height / 2);
        }
    }

    // ───────────────────────── Bars ─────────────────────────

    private SKColor SeriesColor(ChartPlot plot, ChartSeries series, int point) =>
        plot.VaryColors && plot.Series.Count == 1 && plot.Kind is ChartKind.Bar or ChartKind.Pie or ChartKind.Doughnut
            ? _chart.Palette.Color(point)
            : _chart.Palette.Color(series.Index);

    private void Bars(ChartPlot plot, Axis category, Axis value, SKRect inner)
    {
        if (value.Scale is not { } scale)
            return;
        var series = plot.Series;
        var n = series.Count;
        if (n == 0 || category.Count == 0)
            return;
        var stacked = plot.Grouping != ChartGrouping.Standard;
        var percent = plot.Grouping == ChartGrouping.PercentStacked;
        var gap = plot.GapWidth / 100.0;
        var overlap = stacked ? 1 : plot.Overlap / 100.0;
        var bars = stacked ? 1 : n;
        // In slot units: bars of width w overlapping by overlap, a gap of gap × w shared out on both sides.
        var width = 1 / (bars - (bars - 1) * overlap + gap);
        var baseline = scale.Crossing(category.Definition);
        var count = Math.Min(category.Count, ChartReader.MaxPoints);
        var totals = percent ? Totals(series, count) : null;
        var rects = new List<(SKRect Rect, SKColor? Fill, Pen? Line)>();
        var labels = new List<(ChartSeries Series, int Index, double Value, SKRect Rect, bool Negative)>();
        var up = new double[count];
        var down = new double[count];
        for (var k = 0; k < n; k++)
        {
            var s = series[k];
            for (var i = 0; i < Math.Min(count, s.Values.Count); i++)
            {
                if (s.Values[i] is not { } v)
                    continue;
                var shown = percent ? totals![i] > 0 ? v / totals[i] : 0 : v;
                double from, to;
                if (stacked)
                {
                    if (shown >= 0)
                    {
                        from = up[i];
                        up[i] += shown;
                        to = up[i];
                    }
                    else
                    {
                        from = down[i];
                        down[i] += shown;
                        to = down[i];
                    }
                }
                else
                {
                    (from, to) = (baseline, shown);
                }
                // The slot from its start: the half gap, then the k-th bar.
                var start = gap * width / 2 + (stacked ? 0 : k * width * (1 - overlap));
                var a = category.Edge(i);
                var b = category.Edge(i + 1);
                var c0 = a + (b - a) * start;
                var c1 = a + (b - a) * (start + width);
                var v0 = scale.Fraction(from);
                var v1 = scale.Fraction(to);
                SKRect rect;
                if (category.Horizontal)
                    rect = new SKRect(Along(category, Math.Min(c0, c1), inner), Along(value, Math.Max(v0, v1), inner), Along(category, Math.Max(c0, c1), inner), Along(value, Math.Min(v0, v1), inner));
                else
                    rect = new SKRect(Along(value, Math.Min(v0, v1), inner), Along(category, Math.Max(c0, c1), inner), Along(value, Math.Max(v0, v1), inner), Along(category, Math.Min(c0, c1), inner));
                var style = s.Points.GetValueOrDefault(i) ?? s.Style;
                var auto = SeriesColor(plot, s, i);
                rects.Add((rect, FillOf(style, auto), PenOf(style.Line, null, 0.75f)));
                labels.Add((s, i, v, rect, to < from));
            }
        }
        Clipped(inner, canvas =>
        {
            foreach (var (rect, fill, line) in rects)
            {
                using var path = new SKPath();
                path.AddRect(rect);
                FillPath(canvas, path, fill);
                StrokePath(canvas, path, line);
            }
        });
        foreach (var (s, i, v, rect, negative) in labels)
        {
            if ((s.Labels ?? plot.Labels) is not { Any: true } options || DataLabel(plot, s, i, v, options) is not { } text)
                continue;
            var position = options.Position ?? (stacked ? "ctr" : "outEnd");
            // Along the bar: its far end is the top of a column (or bottom of a negative one), the right of a bar.
            var grows = category.Horizontal ? (negative ^ value.Definition.Reversed ? 1 : -1) : (negative ^ value.Definition.Reversed ? -1 : 1);
            var pad = 3 * _s;
            if (category.Horizontal)
            {
                var end = grows < 0 ? rect.Top : rect.Bottom;
                var baseEnd = grows < 0 ? rect.Bottom : rect.Top;
                var cy = position switch
                {
                    "ctr" => rect.MidY,
                    "inEnd" => end - grows * (pad + text.Height / 2),
                    "inBase" => baseEnd + grows * (pad + text.Height / 2),
                    _ => end + grows * (pad + text.Height / 2),
                };
                TextCentred(text, rect.MidX, cy);
            }
            else
            {
                var end = grows > 0 ? rect.Right : rect.Left;
                var baseEnd = grows > 0 ? rect.Left : rect.Right;
                var cx = position switch
                {
                    "ctr" => rect.MidX,
                    "inEnd" => end - grows * (pad + text.Width / 2),
                    "inBase" => baseEnd + grows * (pad + text.Width / 2),
                    _ => end + grows * (pad + text.Width / 2),
                };
                TextCentred(text, cx, rect.MidY);
            }
        }
    }

    private static double[] Totals(List<ChartSeries> series, int count)
    {
        var totals = new double[count];
        foreach (var s in series)
        {
            for (var i = 0; i < Math.Min(count, s.Values.Count); i++)
                totals[i] += Math.Abs(s.Values[i] ?? 0);
        }
        return totals;
    }

    /// <summary>The text of a data label (series name, category, value, percent), or null when it shows nothing or too many are drawn.</summary>
    private ChartTextLine? DataLabel(ChartPlot plot, ChartSeries series, int index, double value, ChartLabels options, double? percent = null)
    {
        if (++_labels > MaxLabels)
            return null;
        var parts = new List<string>(4);
        if (options.Series && series.Name is { Length: > 0 } name)
            parts.Add(name);
        if (options.Category)
            parts.Add(Category(series, index));
        if (options.Value)
            parts.Add(Format(value, options.Format ?? series.Format));
        if (options.Percent && percent is { } share)
            parts.Add(Format(share, "0%"));
        _ = plot;
        return parts.Count == 0 ? null : Text(string.Join(", ", parts), options.Style);
    }

    // ───────────────────────── Lines, areas, scatter ─────────────────────────

    /// <summary>The marker a line or scatter series draws, or null for none.</summary>
    private static ChartMarker? MarkerOf(ChartPlot plot, ChartSeries series)
    {
        var symbol = series.Marker?.Symbol ?? "auto";
        if (symbol == "none" || series.Marker is null && !plot.Markers)
            return null;
        if (symbol == "auto")
            symbol = AutoSymbols[series.Index % AutoSymbols.Length];
        return new ChartMarker(symbol, series.Marker?.Size ?? 5, series.Marker?.Style ?? ChartShapeStyle.Auto);
    }

    private void Lines(ChartPlot plot, Axis x, Axis y, SKRect inner)
    {
        if (y.Scale is not { } scale)
            return;
        var scatter = plot.Kind == ChartKind.Scatter;
        var percent = plot.Grouping == ChartGrouping.PercentStacked;
        var stacked = plot.Grouping != ChartGrouping.Standard;
        var count = plot.Series.Count == 0 ? 0 : plot.Series.Max(s => s.Values.Count);
        var totals = percent ? Totals(plot.Series, count) : null;
        var sums = new double[count];
        foreach (var series in plot.Series)
        {
            var points = new List<SKPoint?>(series.Values.Count);
            for (var i = 0; i < series.Values.Count; i++)
            {
                var v = series.Values[i];
                if (v is null && !scatter && _chart.BlanksAs == "zero")
                    v = 0;
                if (v is not { } value)
                {
                    points.Add(null);
                    continue;
                }
                if (stacked)
                {
                    sums[i] += percent ? totals![i] > 0 ? value / totals[i] : 0 : value;
                    value = sums[i];
                }
                double position;
                if (scatter)
                {
                    var xv = series.XValues is { } xs ? i < xs.Count ? xs[i] : null : i + 1;
                    if (xv is not { } xValue || x.Scale is not { } xScale)
                    {
                        points.Add(null);
                        continue;
                    }
                    position = xScale.Fraction(xValue);
                }
                else
                {
                    position = x.Fraction(i);
                }
                var px = Along(x, position, inner);
                var py = Along(y, scale.Fraction(value), inner);
                points.Add(x.Horizontal ? new SKPoint(px, py) : new SKPoint(py, px));
            }
            var auto = _chart.Palette.Color(series.Index);
            var pen = plot.Lines ? PenOf(series.Style.Line, auto, scatter ? 1.5f : 2.25f) : null;
            var span = _chart.BlanksAs == "span" || scatter;
            if (pen is not null)
            {
                var runs = Runs(points, span);
                var smooth = series.Smooth;
                Clipped(inner, canvas =>
                {
                    foreach (var run in runs)
                    {
                        using var path = Polyline(run, smooth);
                        StrokePath(canvas, path, pen, round: true);
                    }
                });
            }
            if (MarkerOf(plot, series) is { } marker)
            {
                for (var i = 0; i < points.Count; i++)
                {
                    if (points[i] is { } point && SKRect.Inflate(inner, 1, 1).Contains(point))
                        Marker(marker with { Style = series.Points.GetValueOrDefault(i) is { } own ? own : marker.Style }, auto, point, counted: true);
                }
            }
            if ((series.Labels ?? plot.Labels) is { Any: true } options)
            {
                for (var i = 0; i < points.Count; i++)
                {
                    if (points[i] is not { } point || series.Values[i] is not { } v || DataLabel(plot, series, i, v, options) is not { } text)
                        continue;
                    var pad = 4 * _s + (MarkerOf(plot, series)?.Size ?? 0) * _s / 2;
                    var (cx, cy) = (options.Position ?? "r") switch
                    {
                        "t" => (point.X, point.Y - pad - text.Height / 2),
                        "b" => (point.X, point.Y + pad + text.Height / 2),
                        "l" => (point.X - pad - text.Width / 2, point.Y),
                        "ctr" => (point.X, point.Y),
                        _ => (point.X + pad + text.Width / 2, point.Y),
                    };
                    TextCentred(text, cx, cy);
                }
            }
        }
    }

    /// <summary>The unbroken stretches of a line: blanks break it unless it spans them.</summary>
    private static List<SKPoint[]> Runs(List<SKPoint?> points, bool span)
    {
        var runs = new List<SKPoint[]>();
        var current = new List<SKPoint>();
        foreach (var point in points)
        {
            if (point is { } p)
            {
                current.Add(p);
                continue;
            }
            if (span)
                continue;
            if (current.Count > 0)
                runs.Add([.. current]);
            current.Clear();
        }
        if (current.Count > 0)
            runs.Add([.. current]);
        return runs;
    }

    private static SKPath Polyline(SKPoint[] points, bool smooth)
    {
        var path = new SKPath();
        if (points.Length == 0)
            return path;
        path.MoveTo(points[0]);
        if (points.Length == 1)
        {
            path.LineTo(points[0]);
            return path;
        }
        for (var i = 1; i < points.Length; i++)
        {
            if (!smooth)
            {
                path.LineTo(points[i]);
                continue;
            }
            // Catmull-Rom through the points, as cubic Béziers.
            var p0 = points[Math.Max(0, i - 2)];
            var p1 = points[i - 1];
            var p2 = points[i];
            var p3 = points[Math.Min(points.Length - 1, i + 1)];
            path.CubicTo(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6, p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6, p2.X, p2.Y);
        }
        return path;
    }

    private void Marker(ChartMarker marker, SKColor auto, SKPoint center, bool counted)
    {
        if (counted && ++_markers > MaxMarkers)
            return;
        var size = marker.Size * _s;
        var fill = FillOf(marker.Style, auto);
        var line = PenOf(marker.Style.Line, auto, 0.75f);
        var symbol = marker.Symbol;
        Shape(canvas =>
        {
            using var path = new SKPath();
            var (x, y, h) = (center.X, center.Y, size / 2);
            var open = false;
            switch (symbol)
            {
                case "circle":
                    path.AddCircle(x, y, h);
                    break;
                case "dot":
                    path.AddCircle(x, y, h / 2);
                    break;
                case "diamond":
                    path.AddPoly([new(x, y - h), new(x + h, y), new(x, y + h), new(x - h, y)]);
                    break;
                case "triangle":
                    path.AddPoly([new(x, y - h), new(x + h, y + h), new(x - h, y + h)]);
                    break;
                case "x":
                    path.MoveTo(x - h, y - h);
                    path.LineTo(x + h, y + h);
                    path.MoveTo(x + h, y - h);
                    path.LineTo(x - h, y + h);
                    open = true;
                    break;
                case "plus":
                    path.MoveTo(x - h, y);
                    path.LineTo(x + h, y);
                    path.MoveTo(x, y - h);
                    path.LineTo(x, y + h);
                    open = true;
                    break;
                case "star":
                    path.MoveTo(x - h, y - h);
                    path.LineTo(x + h, y + h);
                    path.MoveTo(x + h, y - h);
                    path.LineTo(x - h, y + h);
                    path.MoveTo(x, y - h);
                    path.LineTo(x, y + h);
                    open = true;
                    break;
                case "dash":
                    path.AddRect(new SKRect(x - h, y - h / 4, x + h, y + h / 4));
                    break;
                default:
                    path.AddRect(new SKRect(x - h, y - h, x + h, y + h));
                    break;
            }
            if (!open)
                FillPath(canvas, path, fill);
            StrokePath(canvas, path, open ? line ?? (fill is { } f ? new Pen(f, 0.75f, null) : null) : line);
        });
    }

    private void Areas(ChartPlot plot, Axis category, Axis value, SKRect inner)
    {
        if (value.Scale is not { } scale)
            return;
        var count = plot.Series.Count == 0 ? 0 : Math.Min(plot.Series.Max(s => s.Values.Count), category.Count);
        if (count == 0)
            return;
        var stacked = plot.Grouping != ChartGrouping.Standard;
        var percent = plot.Grouping == ChartGrouping.PercentStacked;
        var totals = percent ? Totals(plot.Series, count) : null;
        var below = new double[count];
        var baseline = stacked ? 0 : scale.Crossing(category.Definition);
        SKPoint Point(int i, double v)
        {
            var along = Along(category, category.Fraction(i), inner);
            var across = Along(value, scale.Fraction(v), inner);
            return category.Horizontal ? new SKPoint(along, across) : new SKPoint(across, along);
        }
        foreach (var series in plot.Series)
        {
            var top = new SKPoint[count];
            var bottom = new SKPoint[count];
            for (var i = 0; i < count; i++)
            {
                var v = i < series.Values.Count ? series.Values[i] ?? 0 : 0;
                if (percent)
                    v = totals![i] > 0 ? v / totals[i] : 0;
                var from = stacked ? below[i] : baseline;
                var to = stacked ? below[i] + v : v;
                bottom[i] = Point(i, from);
                top[i] = Point(i, to);
                if (stacked)
                    below[i] = to;
            }
            var auto = _chart.Palette.Color(series.Index);
            var fill = FillOf(series.Style, auto);
            var line = PenOf(series.Style.Line, null, 0.75f);
            Clipped(inner, canvas =>
            {
                using var path = new SKPath();
                path.MoveTo(top[0]);
                for (var i = 1; i < top.Length; i++)
                    path.LineTo(top[i]);
                for (var i = bottom.Length - 1; i >= 0; i--)
                    path.LineTo(bottom[i]);
                path.Close();
                FillPath(canvas, path, fill);
                StrokePath(canvas, path, line);
            });
            if ((series.Labels ?? plot.Labels) is { Any: true } options)
            {
                for (var i = 0; i < count; i++)
                {
                    if (i < series.Values.Count && series.Values[i] is { } v && DataLabel(plot, series, i, v, options) is { } text)
                        TextCentred(text, (top[i].X + bottom[i].X) / 2, (top[i].Y + bottom[i].Y) / 2);
                }
            }
        }
    }

    // ───────────────────────── Pies and doughnuts ─────────────────────────

    private void Round(ChartPlot plot, SKRect area)
    {
        var rings = plot.Kind == ChartKind.Doughnut ? plot.Series : plot.Series.Take(1).ToList();
        if (rings.Count == 0)
            return;
        var labelled = rings.Any(s => (s.Labels ?? plot.Labels) is { Any: true, Position: "outEnd" });
        var radius = Math.Min(area.Width, area.Height) / 2 * (labelled ? 0.75f : 0.95f);
        var pulled = rings.SelectMany(s => s.Explosion.Values).DefaultIfEmpty(0).Max();
        radius /= 1 + pulled / 100f;
        if (radius <= 1)
            return;
        var center = new SKPoint(area.MidX, area.MidY);
        var hole = plot.Kind == ChartKind.Doughnut ? radius * plot.HoleSize / 100f : 0;
        var ring = (radius - hole) / rings.Count;
        for (var r = 0; r < rings.Count; r++)
        {
            var series = rings[r];
            var inner = hole + r * ring;
            var outer = inner + ring;
            var total = series.Values.Sum(v => Math.Abs(v ?? 0));
            if (!(total > 0))
                continue;
            var angle = -90 + plot.FirstSliceAngle;
            var slices = new List<(float Start, float Sweep, SKPoint Center, SKColor? Fill, Pen? Line)>();
            for (var i = 0; i < series.Values.Count; i++)
            {
                var v = Math.Abs(series.Values[i] ?? 0);
                if (v <= 0)
                    continue;
                var sweep = (float)(v / total * 360);
                var style = series.Points.GetValueOrDefault(i) ?? series.Style;
                var auto = plot.VaryColors ? _chart.Palette.Color(i) : _chart.Palette.Color(series.Index);
                var explosion = series.Explosion.TryGetValue(i, out var own) ? own : series.Explosion.GetValueOrDefault(-1);
                var middle = (angle + sweep / 2) * Math.PI / 180;
                var shift = plot.Kind == ChartKind.Pie ? radius * explosion / 100f : 0;
                var c = new SKPoint(center.X + (float)Math.Cos(middle) * shift, center.Y + (float)Math.Sin(middle) * shift);
                slices.Add(((float)angle, sweep, c, FillOf(style, auto), PenOf(style.Line, SKColors.White, 0.75f)));
                if ((series.Labels ?? plot.Labels) is { Any: true } options && DataLabel(plot, series, i, series.Values[i] ?? 0, options, v / total) is { } text)
                {
                    var position = options.Position ?? "bestFit";
                    var distance = position switch
                    {
                        "outEnd" => outer + 6 * _s + Math.Max(text.Width, text.Height) / 2,
                        "inEnd" => outer - Math.Max(text.Width, text.Height) / 2 - 3 * _s,
                        "ctr" when plot.Kind == ChartKind.Pie => outer / 2,
                        _ => plot.Kind == ChartKind.Pie ? outer * 0.62f : (inner + outer) / 2,
                    };
                    TextCentred(text, c.X + (float)Math.Cos(middle) * distance, c.Y + (float)Math.Sin(middle) * distance);
                }
                angle += sweep;
            }
            var (innerRadius, outerRadius) = (inner, outer);
            Shape(canvas =>
            {
                foreach (var (start, sweep, c, fill, line) in slices)
                {
                    using var path = new SKPath();
                    var outerBox = new SKRect(c.X - outerRadius, c.Y - outerRadius, c.X + outerRadius, c.Y + outerRadius);
                    if (sweep >= 359.999f && innerRadius <= 0)
                    {
                        path.AddOval(outerBox);
                    }
                    else if (innerRadius <= 0)
                    {
                        path.MoveTo(c);
                        path.ArcTo(outerBox, start, sweep, false);
                        path.Close();
                    }
                    else
                    {
                        var innerBox = new SKRect(c.X - innerRadius, c.Y - innerRadius, c.X + innerRadius, c.Y + innerRadius);
                        path.ArcTo(outerBox, start, Math.Min(sweep, 359.99f), true);
                        path.ArcTo(innerBox, start + Math.Min(sweep, 359.99f), -Math.Min(sweep, 359.99f), false);
                        path.Close();
                    }
                    FillPath(canvas, path, fill);
                    StrokePath(canvas, path, line);
                }
            });
        }
    }
}
