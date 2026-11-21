// Draws a chart (ChartModel.cs) into a box of a page: fills and lines with Skia, text as glyph runs (ChartText), so
// the sheet printer, the document renderer and the slide renderer all draw charts the same way, as vectors.
//
// Layout follows Office's automatic one: the chart area less a margin; the title at the top; the legend on its side
// (right, left, top, bottom or the top-right corner) unless it overlays the plot; the plot area what is left, less its
// axes' labels and titles, or where a manual layout puts it. Value axes scale themselves as Excel does when the file
// sets no bounds: zero is kept unless the values sit far from it, the unit is 1, 2 or 5 times a power of ten giving
// a readable number of gridlines for the axis' length, and the top gets a little room above the highest value.
// Category labels are drawn level, wrapped at spaces, turned 45° or thinned out, whichever fits first; outer category
// levels beyond them, each group's label centred on its categories between dividers (wrapped to the group's width). Each legend entry's text follows
// its own key (a bar's square, a line's stroke).
//
// Plots: bars and columns (clustered with gap width and overlap, stacked, 100 %), lines (stacked, smooth, markers),
// areas, scatter (lines and markers), pies (first slice angle, either way round, pulled-out slices) and doughnuts (one
// ring per series), with data labels (a point's own over its series', moved where its layout says, a pie's joined to
// its slice by a leader line) where the file asks for them, trendlines with their equation and R² (ChartTrend fits
// them; named in the legend after the series), and error bars along the value axis or a scatter's X axis (areas too).
// Combined plots share the plot area, each against its own axes. Fills are colours, gradients (linear or out from a
// centre) or hatching; titles and labels keep the look of each run of rich text. 3D plots: ChartDrawer.ThreeD.cs;
// radar, bubble, stock, surface and bar or pie of pie: ChartDrawer.Kinds.cs; Office 2016 kinds: ChartDrawer.ChartEx.cs.
//
// Bounds: data labels and markers (and error bars and trendline points) drawn per chart are capped (MaxLabels,
// MaxMarkers), points fitted by trendlines too (MaxFitted), category labels are measured for at most MaxMeasured of
// them (the rest thinned out), and gridlines, ticks and hatch lines per axis or fill at most MaxTicks.

using System.Globalization;
using Filee.Engines.Office.Sheets;
using Filee.Engines.Rendering;
using SkiaSharp;

namespace Filee.Engines.Charts;

internal sealed partial class ChartDrawer
{
    private const int MaxLabels = 4096;
    private const int MaxMarkers = 100_000;
    private const int MaxMeasured = 2000;
    private const int MaxTicks = 1000;

    /// <summary>Points trendlines fit per chart at most (each trendline counts its series' points).</summary>
    private const long MaxFitted = 2L * ChartReader.MaxPoints;

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
    private SKRect _box;

    /// <summary>Leader lines of moved labels, drawn over the plots.</summary>
    private readonly List<(SKPoint From, SKPoint To, Pen Pen)> _leaders = [];
    private long _fitted;

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

    /// <summary>A fill: a colour, or a gradient or hatch (their lines <see cref="Scale"/> times their size).</summary>
    private readonly record struct Brush(SKColor Color, ChartGradient? Gradient = null, ChartHatch? Hatch = null, float Scale = 1);

    private static void FillPath(SKCanvas canvas, SKPath path, Brush? fill)
    {
        if (fill is not { } brush)
            return;
        if (brush.Hatch is { } hatch)
        {
            HatchPath(canvas, path, hatch, brush.Scale);
            return;
        }
        if (brush.Color.Alpha == 0 && brush.Gradient is null)
            return;
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = brush.Color };
        using var shader = brush.Gradient is { } gradient ? Shader(gradient, path.Bounds) : null;
        if (shader is not null)
        {
            paint.Color = SKColors.Black;
            paint.Shader = shader;
        }
        canvas.DrawPath(path, paint);
    }

    /// <summary>A gradient over <paramref name="box"/>: along its angle through the box's centre, or out from its centre to the farthest corner.</summary>
    private static SKShader? Shader(ChartGradient gradient, SKRect box)
    {
        if (gradient.Stops.Count < 2 || box.Width <= 0 || box.Height <= 0)
            return null;
        var colors = gradient.Stops.Select(s => s.Color).ToArray();
        var positions = gradient.Stops.Select(s => Math.Clamp(s.Position, 0, 1)).ToArray();
        if (gradient.Radial)
        {
            var center = new SKPoint(box.Left + gradient.CenterX * box.Width, box.Top + gradient.CenterY * box.Height);
            var radius = new[] { new SKPoint(box.Left, box.Top), new SKPoint(box.Right, box.Top), new SKPoint(box.Left, box.Bottom), new SKPoint(box.Right, box.Bottom) }
                .Max(corner => SKPoint.Distance(center, corner));
            return SKShader.CreateRadialGradient(center, Math.Max(radius, 0.01f), colors, positions, SKShaderTileMode.Clamp);
        }
        var radians = gradient.Angle * Math.PI / 180;
        var (dx, dy) = ((float)Math.Cos(radians), (float)Math.Sin(radians));
        var half = Math.Max(Math.Abs(box.Width / 2 * dx) + Math.Abs(box.Height / 2 * dy), 0.01f);
        var mid = new SKPoint(box.MidX, box.MidY);
        return SKShader.CreateLinearGradient(new SKPoint(mid.X - dx * half, mid.Y - dy * half), new SKPoint(mid.X + dx * half, mid.Y + dy * half),
            colors, positions, SKShaderTileMode.Clamp);
    }

    /// <summary>Hatching: the background, then lines across the shape (crossed for a grid), or the two colours mixed.</summary>
    private static void HatchPath(SKCanvas canvas, SKPath path, ChartHatch hatch, float scale)
    {
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        if (hatch.Blend is { } blend)
        {
            var back = hatch.Background ?? SKColors.White;
            paint.Color = new SKColor(Mix(back.Red, hatch.Color.Red, blend), Mix(back.Green, hatch.Color.Green, blend), Mix(back.Blue, hatch.Color.Blue, blend),
                hatch.Background is null ? (byte)Math.Round(hatch.Color.Alpha * blend) : Mix(back.Alpha, hatch.Color.Alpha, blend));
            canvas.DrawPath(path, paint);
            return;
        }
        if (hatch.Background is { } background)
        {
            paint.Color = background;
            canvas.DrawPath(path, paint);
        }
        var bounds = path.Bounds;
        var spacing = Math.Max(hatch.Spacing * scale, 1f);
        var reach = (float)Math.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height) / 2 + spacing;
        if (reach / spacing > MaxTicks)
            spacing = reach / MaxTicks;
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = hatch.Color, StrokeWidth = Math.Max(0.5f * scale, 0.25f) };
        canvas.Save();
        canvas.ClipPath(path, antialias: true);
        var turns = hatch.Crossings switch { >= 3 => new[] { 0.0, 90, 45 }, 2 => [0.0, 90], _ => [0.0] };
        foreach (var turn in turns)
        {
            canvas.Save();
            canvas.RotateDegrees((float)-(hatch.Angle + turn), bounds.MidX, bounds.MidY);
            for (var y = -reach; y <= reach; y += spacing)
                canvas.DrawLine(bounds.MidX - reach, bounds.MidY + y, bounds.MidX + reach, bounds.MidY + y, line);
            canvas.Restore();
        }
        canvas.Restore();
    }

    private static byte Mix(byte from, byte to, float amount) => (byte)Math.Round(from + (to - from) * Math.Clamp(amount, 0, 1));

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

    /// <summary>The fill of a shape: its gradient, hatch or colour, or <paramref name="auto"/> colour; null for none.</summary>
    private Brush? FillOf(ChartShapeStyle style, SKColor? auto)
    {
        if (style.Gradient is { Stops.Count: >= 2 } gradient)
            return new Brush(gradient.Stops[0].Color, Gradient: gradient);
        if (style.Hatch is { } hatch)
            return new Brush(hatch.Color, Hatch: hatch, Scale: _s);
        return style.Fill is { } fill ? fill.Alpha == 0 ? null : new Brush(fill) : auto is { } color ? new Brush(color) : null;
    }

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

    /// <summary>Lines of text, each of pieces (shaped in their own styles, raised by some points) side by side.</summary>
    private sealed class TextBlock
    {
        public List<TextRow> Rows { get; } = [];
        public float Width => Rows.Count == 0 ? 0 : Rows.Max(r => r.Width);
        public float Height => Rows.Sum(r => r.Height);
    }

    private sealed class TextRow(List<(ChartTextLine Line, float Raise)> pieces)
    {
        public List<(ChartTextLine Line, float Raise)> Pieces { get; } = pieces;
        public float Width { get; } = pieces.Sum(p => p.Line.Width);
        public float Ascent { get; } = pieces.Count == 0 ? 0 : pieces.Max(p => p.Line.Ascent + p.Raise);
        public float Descent { get; } = pieces.Count == 0 ? 0 : pieces.Max(p => p.Line.Descent - p.Raise);
        public float Height => Ascent + Descent;
    }

    private static TextBlock Block(params ChartTextLine[] lines)
    {
        var block = new TextBlock();
        foreach (var line in lines)
            block.Rows.Add(new TextRow([(line, 0)]));
        return block;
    }

    /// <summary>Lines of runs as a block; a raised run is lifted by that part of its own size.</summary>
    private TextBlock Block(IEnumerable<IReadOnlyList<ChartRun>> lines)
    {
        var block = new TextBlock();
        foreach (var runs in lines)
        {
            var pieces = runs.Take(64).Select(r => (Text(r.Text, r.Style), r.Raise * r.Style.Size * _s)).ToList();
            if (pieces.Count > 0)
                block.Rows.Add(new TextRow(pieces));
        }
        return block;
    }

    /// <summary>A title's lines: its runs, or its text in its style.</summary>
    private TextBlock Block(ChartTitle title) =>
        title.Runs is { Count: > 0 } runs ? Block(runs) : Block([.. title.Lines.Select(l => Text(l, title.Style))]);

    /// <summary>
    /// A block with its top at <paramref name="top"/>, each line centred on <paramref name="x"/> (or starting at it when
    /// not <paramref name="centred"/>), in <paramref name="local"/>'s frame.
    /// </summary>
    private void BlockAt(TextBlock block, float x, float top, bool centred, SKMatrix? local = null)
    {
        var y = top;
        foreach (var row in block.Rows)
        {
            var pen = centred ? x - row.Width / 2 : x;
            foreach (var (line, raise) in row.Pieces)
            {
                _texts.Add((line, pen, y + row.Ascent - raise, local));
                pen += line.Width;
            }
            y += row.Height;
        }
    }

    /// <summary>A block centred on (cx, cy), turned by <paramref name="degrees"/> clockwise about that point.</summary>
    private void TextCentred(TextBlock block, float cx, float cy, float degrees = 0)
    {
        if (degrees == 0)
        {
            BlockAt(block, cx, cy - block.Height / 2, centred: true);
            return;
        }
        BlockAt(block, 0, -block.Height / 2, centred: true, SKMatrix.CreateTranslation(cx, cy).PreConcat(SKMatrix.CreateRotationDegrees(degrees)));
    }

    private string Format(double value, string? format)
    {
        var text = _formatter.Format(value, format);
        return text.Length > ChartReader.MaxText ? text[..ChartReader.MaxText] : text;
    }

    // ───────────────────────── Chart ─────────────────────────

    private void Run(SKRect box)
    {
        _box = box;
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
        if (_leaders.Count > 0)
        {
            var leaders = _leaders.ToArray();
            Shape(canvas =>
            {
                foreach (var (from, to, pen) in leaders)
                {
                    using var path = new SKPath();
                    path.MoveTo(from);
                    path.LineTo(to);
                    StrokePath(canvas, path, pen);
                }
            });
        }
    }

    private SKRect Title(SKRect content, SKRect box)
    {
        if (_chart.Title is not { Lines.Count: > 0 } title)
            return content;
        var block = Block(title);
        var height = block.Height;
        if (title.Layout is { } layout)
            BlockAt(block, box.Left + (float)layout.X * box.Width, box.Top + (float)layout.Y * box.Height, centred: false);
        else
            BlockAt(block, content.MidX, content.Top, centred: true);
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

    private sealed record LegendEntry(string Text, KeyKind Kind, Brush? Fill, Pen? Line, ChartMarker? Marker, SKColor MarkerColor);

    private List<LegendEntry> LegendEntries()
    {
        var entries = new List<LegendEntry>();
        var plots = _chart.Plots;
        if (plots.Count >= 1 && plots[0] is { Series.Count: > 0 } kind && SpecialEntries(kind) is { } special)
            return special;
        // One pie, doughnut or other plot of one series in varied colours: an entry per category.
        if (plots.Count == 1 && plots[0] is { Series.Count: > 0 } only
            && (only.Kind is ChartKind.Pie or ChartKind.Doughnut or ChartKind.OfPie
                || only.VaryColors && only.Series.Count == 1 && only.Kind == ChartKind.Bar))
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
                if (series.LegendKey is { } key)
                {
                    entries.Add(key.Fill is { } fill && fill.Alpha > 0
                        ? new LegendEntry(name, KeyKind.Box, FillOf(key, auto), PenOf(key.Line, null, 0.75f), null, auto)
                        : new LegendEntry(name, KeyKind.Line, null, PenOf(key.Line, auto, 0.75f), MarkerOf(plot, series), auto));
                }
                else if (plot.Kind is ChartKind.Line or ChartKind.Scatter && !plot.ThreeD || plot is { Kind: ChartKind.Radar, Filled: false })
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
        // Trendlines after the series, named as Office names them unless they have a name of their own.
        foreach (var plot in plots.Where(p => !p.ThreeD && p.Grouping == ChartGrouping.Standard))
        {
            foreach (var series in plot.Series)
            {
                foreach (var trend in series.Trendlines.Where(t => t.InLegend))
                {
                    if (entries.Count >= MaxLabels)
                        return entries;
                    var auto = SeriesColor(plot, series, 0);
                    entries.Add(new LegendEntry(trend.Name ?? TrendName(trend, series), KeyKind.Line, null, PenOf(trend.Line, auto, 1.5f), null, auto));
                }
            }
        }
        return entries;
    }

    /// <summary>Office's legend name of a trendline: its kind and the series', as "Linear (North)".</summary>
    private static string TrendName(ChartTrendline trend, ChartSeries series)
    {
        var kind = trend.Kind switch
        {
            TrendKind.Exponential => "Expon.",
            TrendKind.Logarithmic => "Log.",
            TrendKind.Polynomial => "Poly.",
            TrendKind.Power => "Power",
            TrendKind.MovingAverage => $"{trend.Period.ToString(CultureInfo.InvariantCulture)} per. Mov. Avg.",
            _ => "Linear",
        };
        return $"{kind} ({series.Name ?? $"Series{series.Index + 1}"})";
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
        // Each entry's text follows its own key: a line's is wider than a bar's square.
        float KeyWidth(int i) => entries[i].Kind == KeyKind.Line ? 20 * s : 7 * s;
        var gap = 4 * s;
        var lineHeight = Math.Max(texts.Max(t => t.Height), 7 * s);
        var vertical = legend.Position is "r" or "l" or "tr";
        var maxText = Math.Min(texts.Max(t => t.Width), (vertical ? 0.45f : 0.9f) * content.Width);
        var entryWidth = Enumerable.Range(0, entries.Count).Max(i => KeyWidth(i) + gap + Math.Min(texts[i].Width, maxText));

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
                var w = KeyWidth(i) + gap + Math.Min(texts[i].Width, maxText);
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
            var keyWidth = KeyWidth(index);
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

        /// <summary>The outer levels of multi-level categories, inside out: each a list of groups of categories with their label.</summary>
        public List<List<(int Start, int End, string Text)>> Levels { get; } = [];

        /// <summary>Points between the category ticks (bars always are).</summary>
        public bool Between { get; set; } = true;

        /// <summary>The other axis of the pair.</summary>
        public Axis? Cross { get; set; }

        public bool Shown => !Definition.Deleted && !Hidden;

        /// <summary>Not drawn whatever the file says (a funnel's value axis).</summary>
        public bool Hidden { get; set; }

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
        if (_chart.Plots.All(p => p.Kind is ChartKind.Pie or ChartKind.Doughnut or ChartKind.OfPie or ChartKind.Treemap or ChartKind.Sunburst))
        {
            var area = Manual(content, box);
            PlotBackground(area, plotStyle);
            foreach (var plot in _chart.Plots)
            {
                switch (plot.Kind)
                {
                    case ChartKind.OfPie:
                        OfPie(plot, area);
                        break;
                    case ChartKind.Treemap:
                        Treemap(plot, area);
                        break;
                    case ChartKind.Sunburst:
                        Sunburst(plot, area);
                        break;
                    case ChartKind.Pie when plot.ThreeD:
                        Pie3D(plot, area);
                        break;
                    default:
                        Round(plot, area);
                        break;
                }
            }
            return;
        }
        if (_chart.Plots.All(p => p.Kind == ChartKind.Radar))
        {
            var area = Manual(content, box);
            PlotBackground(area, plotStyle);
            Radar(area);
            return;
        }
        if (_chart.Plots.All(p => p is { Kind: ChartKind.Surface, ThreeD: false }))
        {
            Contour(_chart.Plots[0], Manual(content, box));
            return;
        }

        // Axes in pairs, each plot against its own.
        var axes = new Dictionary<uint, Axis>();
        var pairs = new List<(ChartPlot Plot, Axis Category, Axis Value)>();
        foreach (var plot in _chart.Plots)
        {
            if (plot.Kind is ChartKind.Pie or ChartKind.Doughnut or ChartKind.OfPie or ChartKind.Treemap or ChartKind.Sunburst or ChartKind.Radar
                || plot is { Kind: ChartKind.Surface, ThreeD: false })
                continue; // a round plot among others: not drawn (Office can't combine them either)
            var horizontal = plot is { Kind: ChartKind.Bar, Horizontal: true } || plot.Kind == ChartKind.Funnel;
            var first = AxisOf(axes, plot.AxisIds.ElementAtOrDefault(0), plot.AxisIds.Count > 0, value: plot.Kind is ChartKind.Scatter or ChartKind.Bubble, horizontal: !horizontal, fallbackId: uint.MaxValue - 1);
            var second = AxisOf(axes, plot.AxisIds.ElementAtOrDefault(1), plot.AxisIds.Count > 1, value: true, horizontal: horizontal, fallbackId: uint.MaxValue);
            first.Cross = second;
            second.Cross = first;
            // A funnel's widths have no axis to read them from.
            second.Hidden |= plot.Kind == ChartKind.Funnel;
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
        foreach (var (plot, _, value) in pairs)
        {
            if (plot.Kind == ChartKind.Surface && SurfaceScale(plot, value.Definition) is { } bands)
                value.Scale = bands;
        }

        if (_chart.View3D is not null)
        {
            ThreeD(pairs, axes, inner, plotStyle);
            return;
        }
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
                case ChartKind.Bubble:
                    Bubbles(plot, category, value, inner);
                    break;
                case ChartKind.Waterfall:
                    Waterfall(plot, category, value, inner);
                    break;
                case ChartKind.Funnel:
                    Funnel(plot, category, inner);
                    break;
                case ChartKind.BoxWhisker:
                    Boxes(plot, category, value, inner);
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
        if (plot.Kind is ChartKind.Waterfall or ChartKind.BoxWhisker or ChartKind.Funnel)
        {
            ExExtents(plot, first, second);
            return;
        }
        if (plot.Kind is ChartKind.Scatter or ChartKind.Bubble)
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
            if (first.Levels.Count == 0 && series.FirstOrDefault(s => s.CategoryLevels is { Count: > 0 })?.CategoryLevels is { } levels)
            {
                foreach (var level in levels)
                    first.Levels.Add(Groups(level, count));
            }
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

    /// <summary>The groups of an outer category level: each runs from a label to the category before the next one.</summary>
    private static List<(int Start, int End, string Text)> Groups(IReadOnlyList<string?> level, int count)
    {
        var groups = new List<(int Start, int End, string Text)>();
        count = Math.Min(count, ChartReader.MaxPoints);
        for (var i = 0; i < count; i++)
        {
            var label = i < level.Count ? level[i] : null;
            if (label is null && groups.Count > 0)
                continue;
            if (groups.Count > 0)
                groups[^1] = groups[^1] with { End = i - 1 };
            groups.Add((i, count - 1, label ?? ""));
        }
        return groups;
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
        if (definition.LabelPosition != "none")
            need += LevelSizes(axis, length).Sum(size => size + LevelPad);
        if (definition.Title is { Lines.Count: > 0 } title)
            need += Block(title).Height + 4 * _s;
        return Math.Min(need, cap);
    }

    /// <summary>The space between one category level and the next, in points (scaled).</summary>
    private float LevelPad => 4 * _s;

    /// <summary>
    /// How far each outer category level reaches out from the one inside it, for an axis <paramref name="length"/>
    /// long: its tallest (wrapped) label, or a column's width.
    /// </summary>
    private List<float> LevelSizes(Axis axis, float length)
    {
        var sizes = new List<float>(axis.Levels.Count);
        foreach (var level in axis.Levels)
        {
            var labels = level.Take(MaxMeasured).Select(g => LevelLines(axis, g, length)).ToList();
            sizes.Add(labels.Count == 0 ? 0 : axis.Horizontal ? labels.Max(l => l.Sum(t => t.Height)) : labels.Max(l => l.Max(t => t.Width)));
        }
        return sizes;
    }

    /// <summary>An outer level's group label: on a level axis wrapped at spaces to its group's width (three lines at most), else one line.</summary>
    private List<ChartTextLine> LevelLines(Axis axis, (int Start, int End, string Text) group, float length)
    {
        var whole = Text(group.Text, axis.Definition.Style);
        if (!axis.Horizontal)
            return [whole];
        var width = (float)Math.Abs(axis.Edge(group.End + 1) - axis.Edge(group.Start)) * length * 0.95f;
        return whole.Width <= width ? [whole] : Wrap(group.Text, axis.Definition.Style, width, 3) ?? [whole];
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

        // Outer category levels beyond the labels, each group's label centred on its categories and divided from the
        // next by a line from the axis out through its level.
        if (definition.LabelPosition != "none" && axis.Levels.Count > 0)
        {
            var length = axis.Horizontal ? inner.Width : inner.Height;
            var sizes = LevelSizes(axis, length);
            var dividers = new List<(float Position, float Reach)>();
            for (var l = 0; l < axis.Levels.Count; l++)
            {
                var from = reach + LevelPad;
                foreach (var (start, end, label) in axis.Levels[l].Take(MaxMeasured))
                {
                    var a = axis.Edge(start);
                    var b = axis.Edge(end + 1);
                    var middle = Along(axis, (a + b) / 2, inner);
                    if (label.Length > 0)
                    {
                        var lines = LevelLines(axis, (start, end, label), length);
                        var total = lines.Sum(t => t.Height);
                        var y = 0f;
                        foreach (var line in lines)
                        {
                            // Wrapped lines one under the other, the block away from the axis.
                            Place(line, axis.Horizontal, middle, labelsAt, outward > 0 ? from + y : from + total - y - line.Height, outward);
                            y += line.Height;
                        }
                    }
                    dividers.Add((Along(axis, a, inner), from + sizes[l]));
                    dividers.Add((Along(axis, b, inner), from + sizes[l]));
                }
                reach = from + sizes[l];
            }
            if (pen is { } divider)
            {
                var horizontal = axis.Horizontal;
                var start = labelsAt;
                Shape(canvas =>
                {
                    using var path = new SKPath();
                    foreach (var (position, length) in dividers)
                    {
                        if (horizontal)
                        {
                            path.MoveTo(position, start);
                            path.LineTo(position, start + outward * length);
                        }
                        else
                        {
                            path.MoveTo(start, position);
                            path.LineTo(start + outward * length, position);
                        }
                    }
                    StrokePath(canvas, path, divider);
                });
            }
        }

        // The title beyond the labels, turned for a vertical axis.
        if (definition.Title is { Lines.Count: > 0 } title)
        {
            var block = Block(title);
            var height = block.Height;
            var edge = axis.Horizontal ? (outward > 0 ? Math.Max(labelsAt, inner.Bottom) : Math.Min(labelsAt, inner.Top)) : (outward > 0 ? Math.Max(labelsAt, inner.Right) : Math.Min(labelsAt, inner.Left));
            var distance = reach + 4 * _s;
            if (axis.Horizontal)
                TextCentred(block, inner.MidX, edge + outward * (distance + height / 2));
            else
                TextCentred(block, edge + outward * (distance + height / 2), inner.MidY, outward > 0 ? 90 : -90);
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

    private void Bars(ChartPlot plot, Axis category, Axis value, SKRect inner, Depth? depth = null)
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
        // 3D bars "standard" stand one series behind the other, each in its own row.
        var rows = depth is not null && plot.SeriesInDepth;
        var overlap = stacked ? 1 : plot.Overlap / 100.0;
        var bars = stacked || rows ? 1 : n;
        // In slot units: bars of width w overlapping by overlap, a gap of gap × w shared out on both sides.
        var width = 1 / (bars - (bars - 1) * overlap + gap);
        var baseline = scale.Crossing(category.Definition);
        var count = Math.Min(category.Count, ChartReader.MaxPoints);
        var totals = percent ? Totals(series, count) : null;
        var rects = new List<(SKRect Rect, Brush? Fill, Pen? Line, int Row)>();
        var labels = new List<(ChartSeries Series, int Index, double Value, SKRect Rect, bool Negative)>();
        var anchors = new Dictionary<ChartSeries, List<Anchor>>();
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
                var start = gap * width / 2 + (stacked || rows ? 0 : k * width * (1 - overlap));
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
                var row = rows ? k : 0;
                rects.Add((rect, FillOf(style, auto), PenOf(style.Line, null, 0.75f), row));
                labels.Add((s, i, v, depth is { } d ? Shifted(rect, d.Offset(d.Row(row, rows ? n : 1).Z0)) : rect, to < from));
                if (s.ErrorBars.Count > 0 && depth is null)
                {
                    if (!anchors.TryGetValue(s, out var list))
                        anchors[s] = list = [];
                    var end = Along(value, v1, inner);
                    list.Add(new Anchor(i, category.Horizontal ? new SKPoint(rect.MidX, end) : new SKPoint(end, rect.MidY), to, v, null));
                }
            }
        }
        if (depth is { } view)
        {
            Boxes3D([.. rects.Select(r => (r.Rect, r.Fill, r.Line, view.Row(r.Row, rows ? n : 1)))], view);
        }
        else
        {
            Clipped(inner, canvas =>
            {
                foreach (var (rect, fill, line, _) in rects)
                {
                    using var path = new SKPath();
                    path.AddRect(rect);
                    FillPath(canvas, path, fill);
                    StrokePath(canvas, path, line);
                }
            });
        }
        foreach (var s in series)
        {
            if (anchors.TryGetValue(s, out var list))
                ErrorBars(s, list, value, null, inner);
            if (!stacked && depth is null)
                Trendlines(s, category, value, inner, SeriesColor(plot, s, 0));
        }
        foreach (var (s, i, v, rect, negative) in labels)
        {
            if (LabelsAt(plot, s, i) is not { Any: true } options || DataLabel(plot, s, i, v, options) is not { } text)
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
                LabelAt(text, rect.MidX, cy, options);
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
                LabelAt(text, cx, rect.MidY, options);
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
    /// <summary>What point <paramref name="index"/>'s label shows: its own (c:dLbl), the series' or the plot's.</summary>
    private static ChartLabels? LabelsAt(ChartPlot plot, ChartSeries series, int index) =>
        series.PointLabels.GetValueOrDefault(index) ?? series.Labels ?? plot.Labels;

    private TextBlock? DataLabel(ChartPlot plot, ChartSeries series, int index, double value, ChartLabels options, double? percent = null)
    {
        if (++_labels > MaxLabels)
            return null;
        if (options.Text is { Count: > 0 } own)
            return Block(own);
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
        // A separator may be a line break.
        return parts.Count == 0 ? null : Block([.. string.Join(options.Separator ?? ", ", parts).Split('\n').Take(ChartReader.MaxLines).Select(l => Text(l, options.Style))]);
    }

    /// <summary>
    /// A data label centred on (cx, cy), or moved from there as far as its layout says (c:dLbl c:layout), with a leader
    /// line from <paramref name="anchor"/> (a pie slice's edge) when it is moved and the labels ask for them.
    /// </summary>
    private void LabelAt(TextBlock text, float cx, float cy, ChartLabels options, SKPoint? anchor = null)
    {
        if (options.Offset is { } moved)
        {
            (cx, cy) = (cx + (float)moved.X * _box.Width, cy + (float)moved.Y * _box.Height);
            if (options.LeaderLines && anchor is { } from)
            {
                var label = SKRect.Create(cx - text.Width / 2, cy - text.Height / 2, text.Width, text.Height);
                // To the nearest point of the label's box, unless the slice reaches into it.
                var to = new SKPoint(Math.Clamp(from.X, label.Left, label.Right), Math.Clamp(from.Y, label.Top, label.Bottom));
                if (SKPoint.Distance(from, to) > 4 * _s && PenOf(null, new SKColor(0x59, 0x59, 0x59), 0.75f) is { } pen)
                    _leaders.Add((from, to, pen));
            }
        }
        TextCentred(text, cx, cy);
    }

    // ───────────────────────── Error bars and trendlines ─────────────────────────

    /// <summary>A point error bars start from: where it's drawn, its value as drawn (stacked) and as given, and its X (scatter).</summary>
    private readonly record struct Anchor(int Index, SKPoint At, double Value, double Raw, double? X);

    /// <summary>Error bars of <paramref name="series"/> at its points, along the value axis (or X axis), clipped to the plot.</summary>
    private void ErrorBars(ChartSeries series, List<Anchor> anchors, Axis value, Axis? x, SKRect inner)
    {
        foreach (var bars in series.ErrorBars)
        {
            var alongX = bars.AlongX && x?.Scale is not null;
            var axis = alongX ? x! : value;
            if (axis.Scale is not { } scale || PenOf(bars.Line, new SKColor(0x40, 0x40, 0x40), 0.75f) is not { } pen)
                continue;
            var (mean, deviation, error) = ChartTrend.Statistics(alongX
                ? anchors.Select(a => a.X ?? double.NaN)
                : series.Values.Where(v => v is not null).Select(v => v!.Value), bars.Office);
            var cap = bars.EndCap ? 3 * _s : 0;
            var segments = new List<(SKPoint From, SKPoint To)>();
            foreach (var anchor in anchors)
            {
                if (++_markers > MaxMarkers)
                    break;
                var own = alongX ? anchor.X ?? double.NaN : anchor.Raw;
                // Standard deviation bars stand around the series' mean, as Excel draws them.
                var centre = bars is { Amount: ErrorAmount.StandardDeviation, Office: true } ? mean : alongX ? own : anchor.Value;
                double Custom(IReadOnlyList<double?>? values) =>
                    values is null || values.Count == 0 ? 0 : values.Count == 1 ? values[0] ?? 0 : anchor.Index < values.Count ? values[anchor.Index] ?? 0 : 0;
                var (plus, minus) = bars.Amount switch
                {
                    ErrorAmount.Percentage => (Math.Abs(own) * bars.Value / 100, Math.Abs(own) * bars.Value / 100),
                    ErrorAmount.StandardDeviation => (bars.Value * deviation, bars.Value * deviation),
                    ErrorAmount.StandardError => (error, error),
                    ErrorAmount.Variance => (deviation * deviation, deviation * deviation),
                    ErrorAmount.Custom => (Math.Abs(Custom(bars.PlusValues)), Math.Abs(Custom(bars.MinusValues))),
                    _ => (bars.Value, bars.Value),
                };
                if (!double.IsFinite(centre) || !double.IsFinite(plus) || !double.IsFinite(minus))
                    continue;
                var middle = Along(axis, scale.Fraction(centre), inner);
                var high = bars.Plus ? Along(axis, scale.Fraction(centre + plus), inner) : middle;
                var low = bars.Minus ? Along(axis, scale.Fraction(centre - minus), inner) : middle;
                var across = axis.Horizontal ? anchor.At.Y : anchor.At.X;
                SKPoint P(float along, float side) => axis.Horizontal ? new SKPoint(along, across + side) : new SKPoint(across + side, along);
                segments.Add((P(low, 0), P(high, 0)));
                if (cap > 0)
                {
                    if (bars.Plus)
                        segments.Add((P(high, -cap), P(high, cap)));
                    if (bars.Minus)
                        segments.Add((P(low, -cap), P(low, cap)));
                }
            }
            if (segments.Count == 0)
                continue;
            Clipped(inner, canvas =>
            {
                using var path = new SKPath();
                foreach (var (from, to) in segments)
                {
                    path.MoveTo(from);
                    path.LineTo(to);
                }
                StrokePath(canvas, path, pen);
            });
        }
    }

    /// <summary>Points of a trendline's curve drawn at most.</summary>
    private const int TrendSamples = 200;

    /// <summary>Trendlines of <paramref name="series"/> (categories count 1, 2, 3, ... along X), each with its label.</summary>
    private void Trendlines(ChartSeries series, Axis x, Axis y, SKRect inner, SKColor auto)
    {
        if (series.Trendlines.Count == 0 || y.Scale is not { } yScale)
            return;
        var data = new List<(double X, double Y)>();
        for (var i = 0; i < series.Values.Count; i++)
        {
            if (series.Values[i] is not { } v)
                continue;
            double? xv = x.Scale is not null && series.XValues is { } xs ? i < xs.Count ? xs[i] : null : i + 1;
            if (xv is { } xValue)
                data.Add((xValue, v));
        }
        // Fitting costs a pass over the points per trendline: a chart fits so many points at most.
        if (data.Count < 2 || (_fitted += (long)data.Count * series.Trendlines.Count) > MaxFitted)
            return;
        SKPoint Point(double xv, double yv)
        {
            var along = Along(x, x.Scale is { } xScale ? xScale.Fraction(xv) : x.Fraction(xv - 1), inner);
            var across = Along(y, yScale.Fraction(yv), inner);
            return x.Horizontal ? new SKPoint(along, across) : new SKPoint(across, along);
        }
        foreach (var trend in series.Trendlines)
        {
            if ((_markers += TrendSamples) > MaxMarkers)
                return;
            var pen = PenOf(trend.Line, auto, 1.5f);
            var runs = new List<SKPoint[]>();
            TrendFit? fit = null;
            if (trend.Kind == TrendKind.MovingAverage)
            {
                runs.Add([.. ChartTrend.MovingAverage(data, trend.Period).Select(p => Point(p.X, p.Y))]);
            }
            else if ((fit = ChartTrend.Fit(trend, data)) is { } fitted)
            {
                var low = data.Min(p => p.X) - trend.Backward;
                var high = data.Max(p => p.X) + trend.Forward;
                if (trend.Kind is TrendKind.Logarithmic or TrendKind.Power && low <= 0)
                    low = data.Where(p => p.X > 0).Select(p => p.X).DefaultIfEmpty(1).Min() / 100;
                // Sampled (also a straight line, so its label finds the last point inside the plot).
                const int samples = TrendSamples;
                var current = new List<SKPoint>();
                for (var k = 0; k < samples; k++)
                {
                    var xv = low + (high - low) * k / (samples - 1);
                    var yv = fitted.Curve(xv);
                    if (double.IsFinite(yv) && (yScale.Log is null || yv > 0))
                    {
                        current.Add(Point(xv, Math.Clamp(yv, -ChartReader.MaxValue, ChartReader.MaxValue)));
                        continue;
                    }
                    if (current.Count > 1)
                        runs.Add([.. current]);
                    current.Clear();
                }
                if (current.Count > 1)
                    runs.Add([.. current]);
            }
            if (pen is { } line && runs.Any(r => r.Length > 1))
            {
                Clipped(inner, canvas =>
                {
                    foreach (var run in runs.Where(r => r.Length > 1))
                    {
                        using var path = Polyline(run, smooth: false);
                        StrokePath(canvas, path, line, round: true);
                    }
                });
            }
            // The equation and R² by the end of the line, inside the plot.
            if (fit is not null && (trend.ShowEquation || trend.ShowRSquared) && runs.LastOrDefault(r => r.Length > 0) is { } last)
            {
                var text = Block(ChartTrend.Label(trend, fit, trend.Style, v => trend.Format is { } code ? Format(v, code) : ChartTrend.Number(v)));
                var visible = SKRect.Inflate(inner, 0.5f, 0.5f);
                var end = last.LastOrDefault(visible.Contains, last[^1]);
                var pad = 4 * _s;
                var cx = Math.Clamp(end.X - text.Width / 2, inner.Left + text.Width / 2, Math.Max(inner.Left + text.Width / 2, inner.Right - text.Width / 2));
                var cy = Math.Clamp(end.Y - pad - text.Height / 2, inner.Top + text.Height / 2, Math.Max(inner.Top + text.Height / 2, inner.Bottom - text.Height / 2));
                // Moved where the file says (c:trendlineLbl c:layout), from that place.
                if (trend.LabelOffset is { } moved)
                    (cx, cy) = (cx + (float)moved.X * _box.Width, cy + (float)moved.Y * _box.Height);
                TextCentred(text, cx, cy);
            }
        }
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

    private void Lines(ChartPlot plot, Axis x, Axis y, SKRect inner, Depth? depth = null)
    {
        if (y.Scale is not { } scale)
            return;
        var scatter = plot.Kind == ChartKind.Scatter;
        var percent = plot.Grouping == ChartGrouping.PercentStacked;
        var stacked = plot.Grouping != ChartGrouping.Standard;
        var count = plot.Series.Count == 0 ? 0 : plot.Series.Max(s => s.Values.Count);
        var totals = percent ? Totals(plot.Series, count) : null;
        var sums = new double[count];
        if (!scatter && depth is null)
            StockLines(plot, x, y, inner);
        var ribbons = new List<(SKPoint[] Points, Brush? Fill, Pen? Line, int Row)>();
        foreach (var series in plot.Series)
        {
            var points = new List<SKPoint?>(series.Values.Count);
            var anchors = new List<Anchor>();
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
                if (series.ErrorBars.Count > 0)
                {
                    double? xValue = scatter ? series.XValues is { } xv ? xv[i] : i + 1 : null;
                    anchors.Add(new Anchor(i, points[^1]!.Value, value, series.Values[i] ?? 0, xValue));
                }
            }
            var auto = _chart.Palette.Color(series.Index);
            var pen = plot.Lines ? PenOf(series.Style.Line, auto, scatter ? 1.5f : 2.25f) : null;
            var span = _chart.BlanksAs == "span" || scatter;
            if (depth is { } view)
            {
                // A ribbon in the series' row; its labels on the ribbon's front edge.
                var row = plot.Series.IndexOf(series);
                var (z0, _) = view.Row(row, plot.Series.Count);
                // A ribbon takes the series' fill (c:spPr), else its line's colour.
                var ribbon = series.Style.Fill is { Alpha: > 0 } own ? own : series.Style.Line?.Color ?? auto;
                foreach (var run in Runs(points, span))
                    ribbons.Add((run, new Brush(ribbon), PenOf(null, Shade(ribbon, 0.75f), 0.5f), row));
                for (var i = 0; i < points.Count; i++)
                {
                    if (LabelsAt(plot, series, i) is { Any: true } options && points[i] is { } point && series.Values[i] is { } v && DataLabel(plot, series, i, v, options) is { } text)
                        TextCentred(text, point.X + view.Offset(z0).X, point.Y + view.Offset(z0).Y - 4 * _s - text.Height / 2);
                }
                continue;
            }
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
            if (anchors.Count > 0)
                ErrorBars(series, anchors, y, scatter ? x : null, inner);
            if (!stacked)
                Trendlines(series, x, y, inner, auto);
            if (MarkerOf(plot, series) is { } marker)
            {
                for (var i = 0; i < points.Count; i++)
                {
                    if (points[i] is { } point && SKRect.Inflate(inner, 1, 1).Contains(point))
                        Marker(marker with { Style = series.Points.GetValueOrDefault(i) is { } own ? own : marker.Style }, auto, point, counted: true);
                }
            }
            for (var i = 0; i < points.Count; i++)
            {
                if (LabelsAt(plot, series, i) is not { Any: true } options || points[i] is not { } point || series.Values[i] is not { } v
                    || DataLabel(plot, series, i, v, options) is not { } text)
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
                LabelAt(text, cx, cy, options);
            }
        }
        if (depth is { } ribbonView && ribbons.Count > 0)
            Ribbons3D(ribbons, plot.Series.Count, ribbonView);
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
            StrokePath(canvas, path, open ? line ?? (fill is { } f ? new Pen(f.Color, 0.75f, null) : null) : line);
        });
    }

    private void Areas(ChartPlot plot, Axis category, Axis value, SKRect inner, Depth? depth = null)
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
        var slabs = new List<(SKPoint[] Top, SKPoint[] Bottom, Brush? Fill, Pen? Line, int Row)>();
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
            if (depth is { } view)
            {
                var row = plot.SeriesInDepth ? plot.Series.IndexOf(series) : 0;
                slabs.Add((top, bottom, fill, line, row));
                var shift = view.Offset(view.Row(row, plot.SeriesInDepth ? plot.Series.Count : 1).Z0);
                for (var i = 0; i < count; i++)
                {
                    if (LabelsAt(plot, series, i) is { Any: true } options && i < series.Values.Count && series.Values[i] is { } v && DataLabel(plot, series, i, v, options) is { } text)
                        TextCentred(text, (top[i].X + bottom[i].X) / 2 + shift.X, (top[i].Y + bottom[i].Y) / 2 + shift.Y);
                }
                continue;
            }
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
            if (!stacked)
                Trendlines(series, category, value, inner, auto);
            if (series.ErrorBars.Count > 0)
            {
                // Error bars stand on each point of the area's top edge.
                var anchors = new List<Anchor>();
                for (var i = 0; i < count; i++)
                {
                    if (i < series.Values.Count && series.Values[i] is { } raw)
                        anchors.Add(new Anchor(i, top[i], stacked ? below[i] : raw, raw, null));
                }
                ErrorBars(series, anchors, value, null, inner);
            }
            for (var i = 0; i < count; i++)
            {
                if (LabelsAt(plot, series, i) is { Any: true } options && i < series.Values.Count && series.Values[i] is { } v && DataLabel(plot, series, i, v, options) is { } text)
                    LabelAt(text, (top[i].X + bottom[i].X) / 2, (top[i].Y + bottom[i].Y) / 2, options);
            }
        }
        if (depth is { } slabView && slabs.Count > 0)
            Slabs3D(slabs, plot.SeriesInDepth ? plot.Series.Count : 1, slabView);
    }

    // ───────────────────────── Pies and doughnuts ─────────────────────────

    private void Round(ChartPlot plot, SKRect area)
    {
        var rings = plot.Kind == ChartKind.Doughnut ? plot.Series : plot.Series.Take(1).ToList();
        if (rings.Count == 0)
            return;
        var labelled = rings.Any(s => (s.Labels ?? plot.Labels) is { Any: true, Position: "outEnd" } || s.PointLabels.Values.Any(l => l is { Any: true, Position: "outEnd" }));
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
            var slices = new List<(float Start, float Sweep, SKPoint Center, Brush? Fill, Pen? Line)>();
            for (var i = 0; i < series.Values.Count; i++)
            {
                var v = Math.Abs(series.Values[i] ?? 0);
                if (v <= 0)
                    continue;
                var sweep = (float)(v / total * 360);
                var style = series.Points.GetValueOrDefault(i) ?? series.Style;
                var auto = plot.VaryColors ? _chart.Palette.Color(i) : _chart.Palette.Color(series.Index);
                var explosion = series.Explosion.TryGetValue(i, out var own) ? own : series.Explosion.GetValueOrDefault(-1);
                // Anticlockwise slices end where the one before starts.
                var start = plot.Clockwise ? angle : angle - sweep;
                var middle = (start + sweep / 2) * Math.PI / 180;
                var shift = plot.Kind == ChartKind.Pie ? radius * explosion / 100f : 0;
                var c = new SKPoint(center.X + (float)Math.Cos(middle) * shift, center.Y + (float)Math.Sin(middle) * shift);
                slices.Add(((float)start, sweep, c, FillOf(style, auto), PenOf(style.Line, SKColors.White, 0.75f)));
                if (LabelsAt(plot, series, i) is { Any: true } options && DataLabel(plot, series, i, series.Values[i] ?? 0, options, v / total) is { } text)
                {
                    var position = options.Position ?? "bestFit";
                    var distance = position switch
                    {
                        "outEnd" => outer + 6 * _s + Math.Max(text.Width, text.Height) / 2,
                        "inEnd" => outer - Math.Max(text.Width, text.Height) / 2 - 3 * _s,
                        "ctr" when plot.Kind == ChartKind.Pie => outer / 2,
                        _ => plot.Kind == ChartKind.Pie ? outer * 0.62f : (inner + outer) / 2,
                    };
                    LabelAt(text, c.X + (float)Math.Cos(middle) * distance, c.Y + (float)Math.Sin(middle) * distance, options,
                        new SKPoint(c.X + (float)Math.Cos(middle) * outer, c.Y + (float)Math.Sin(middle) * outer));
                }
                angle += plot.Clockwise ? sweep : -sweep;
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
