// Radar, bubble, stock, surface (contour) and bar-of-pie / pie-of-pie plots.
//
// Radar: a spoke per category from the centre, the value axis' gridlines as polygons round them, its labels up the
// first spoke; series as closed lines, with markers, or filled. Bubble: scatter points as circles whose area (or
// width) is their size, the largest a quarter of the plot's smaller side times c:bubbleScale; 3D bubbles shaded.
// Stock and line plots: high-low lines, up and down bars between the first and last series, drop lines. Surface seen
// from above (contour): the grid of series over categories coloured by value band, each cell shaded in smaller steps
// between its corners, or as lines for a wireframe. Bar or pie of pie: the points c:splitType sends away form one
// "Other" slice facing a second pie or a stacked bar beside the first, joined to it by the series lines.

using SkiaSharp;

namespace Filee.Engines.Charts;

internal sealed partial class ChartDrawer
{
    // ───────────────────────── Radar ─────────────────────────

    private void Radar(SKRect area)
    {
        var plots = _chart.Plots;
        var series = plots.SelectMany(p => p.Series).ToList();
        if (series.Count == 0)
            return;
        var n = Math.Min(series.Max(s => Math.Max(s.Values.Count, s.Categories?.Count ?? 0)), ChartReader.MaxPoints);
        if (n == 0)
            return;
        var first = plots[0];
        var categoryDefinition = _chart.Axis(first.AxisIds.ElementAtOrDefault(0));
        var valueDefinition = _chart.Axis(first.AxisIds.ElementAtOrDefault(1)) ?? new ChartAxis { IsValue = true, Position = "l", MajorGridlines = new ChartLine() };
        var value = new Axis(valueDefinition with { IsValue = true }, horizontal: false);
        foreach (var s in series)
        {
            foreach (var v in s.Values)
            {
                if (v is { } number)
                    value.Include(number);
            }
        }
        value.Format = valueDefinition.Format ?? series.FirstOrDefault(s => s.Format is not null)?.Format;

        var style = categoryDefinition?.Style ?? _chart.Text;
        var showCategories = categoryDefinition is null or { Deleted: false, LabelPosition: not "none" };
        var step = Math.Max(1, (int)Math.Ceiling(n / (double)Math.Min(MaxMeasured, 360)));
        var labels = showCategories ? Enumerable.Range(0, (n + step - 1) / step).Select(k => (Index: k * step, Line: Text(Category(series, k * step), style))).ToList() : [];
        var labelWidth = labels.Count == 0 ? 0 : labels.Max(l => l.Line.Width);
        var labelHeight = labels.Count == 0 ? 0 : labels.Max(l => l.Line.Height);
        var radius = Math.Min(area.Width / 2 - labelWidth - 8 * _s, area.Height / 2 - labelHeight - 8 * _s);
        radius = Math.Max(radius, Math.Min(area.Width, area.Height) * 0.2f);
        if (radius <= 2)
            return;
        var center = new SKPoint(area.MidX, area.MidY);
        Prepare(value, radius);
        if (value.Scale is not { } scale)
            return;
        double Angle(int i) => (-90 + 360.0 * i / n) * Math.PI / 180;
        SKPoint At(int i, double fraction)
        {
            var r = radius * (float)Math.Clamp(fraction, 0, 1);
            return new SKPoint(center.X + (float)Math.Cos(Angle(i)) * r, center.Y + (float)Math.Sin(Angle(i)) * r);
        }
        var corners = Math.Min(n, MaxTicks);

        // The value axis' gridlines as polygons, the spokes, the outline.
        var ticks = scale.Ticks();
        var grid = valueDefinition.MajorGridlines is { } major ? PenOf(major, GridGray, 0.75f) : null;
        var spokes = categoryDefinition?.MajorGridlines is { } spokeLine ? PenOf(spokeLine, GridGray, 0.75f)
            : categoryDefinition is null ? PenOf(null, GridGray, 0.75f) : null;
        var outline = categoryDefinition is null or { Deleted: false } ? PenOf(categoryDefinition?.Line, GridGray, 0.75f) : null;
        Shape(canvas =>
        {
            void Ring(double fraction, Pen pen)
            {
                using var path = new SKPath();
                if (corners < n)
                {
                    path.AddCircle(center.X, center.Y, radius * (float)Math.Clamp(fraction, 0, 1));
                }
                else
                {
                    path.MoveTo(At(0, fraction));
                    for (var i = 1; i < n; i++)
                        path.LineTo(At(i, fraction));
                    path.Close();
                }
                StrokePath(canvas, path, pen);
            }
            if (grid is { } g)
            {
                foreach (var t in ticks)
                {
                    if (scale.Fraction(t) is > 1e-6 and var f)
                        Ring(f, g);
                }
            }
            if (spokes is { } sp && corners == n)
            {
                using var path = new SKPath();
                for (var i = 0; i < n; i++)
                {
                    path.MoveTo(center);
                    path.LineTo(At(i, 1));
                }
                StrokePath(canvas, path, sp);
            }
            if (outline is { } o)
                Ring(1, o);
        });

        // Value labels up the first spoke, category labels beyond each spoke's end.
        if (value.Shown && valueDefinition.LabelPosition != "none")
        {
            foreach (var t in ticks.Take(MaxTicks))
            {
                var text = Text(Format(t, value.Format), valueDefinition.Style);
                var at = At(0, scale.Fraction(t));
                TextAt(text, at.X - text.Width - 4 * _s, at.Y - text.Height / 2);
            }
        }
        foreach (var (index, line) in labels)
        {
            var angle = Angle(index);
            var (cos, sin) = ((float)Math.Cos(angle), (float)Math.Sin(angle));
            var reach = radius + 5 * _s;
            TextCentred(line, center.X + cos * (reach + line.Width / 2 * Math.Abs(cos)), center.Y + sin * (reach + line.Height / 2 * Math.Abs(sin)));
        }

        foreach (var plot in plots)
        {
            foreach (var s in plot.Series)
            {
                var auto = _chart.Palette.Color(s.Index);
                var points = new List<(int Index, SKPoint Point)>();
                for (var i = 0; i < Math.Min(n, s.Values.Count); i++)
                {
                    if (s.Values[i] is { } v)
                        points.Add((i, At(i, scale.Fraction(v))));
                }
                if (points.Count == 0)
                    continue;
                var fill = plot.Filled ? FillOf(s.Style, auto) : null;
                var pen = plot.Filled ? PenOf(s.Style.Line, null, 0.75f) : plot.Lines ? PenOf(s.Style.Line, auto, 2.25f) : null;
                var polygon = points.Select(p => p.Point).ToArray();
                Shape(canvas =>
                {
                    using var path = new SKPath();
                    path.AddPoly(polygon, close: true);
                    FillPath(canvas, path, fill);
                    StrokePath(canvas, path, pen, round: true);
                });
                if (!plot.Filled && MarkerOf(plot, s) is { } marker)
                {
                    foreach (var (index, point) in points)
                        Marker(marker with { Style = s.Points.GetValueOrDefault(index) ?? marker.Style }, auto, point, counted: true);
                }
                foreach (var (index, point) in points)
                {
                    if (LabelsAt(plot, s, index) is { Any: true } options && s.Values[index] is { } v && DataLabel(plot, s, index, v, options) is { } text)
                    {
                        var angle = Angle(index);
                        TextCentred(text, point.X + (float)Math.Cos(angle) * (text.Width / 2 + 4 * _s), point.Y + (float)Math.Sin(angle) * (text.Height / 2 + 4 * _s));
                    }
                }
            }
        }
    }

    // ───────────────────────── Bubbles ─────────────────────────

    private void Bubbles(ChartPlot plot, Axis x, Axis y, SKRect inner)
    {
        if (x.Scale is not { } xScale || y.Scale is not { } yScale)
            return;
        var sizes = plot.Series.SelectMany(s => s.BubbleSizes ?? []).OfType<double>().Where(v => plot.NegativeBubbles || v > 0).Select(Math.Abs).DefaultIfEmpty(0).Max();
        var largest = 0.25f * Math.Min(inner.Width, inner.Height) / 2 * plot.BubbleScale / 100f;
        var bubbles = new List<(SKPoint Center, float Radius, Brush? Fill, Pen? Line)>();
        foreach (var series in plot.Series)
        {
            var auto = _chart.Palette.Color(series.Index);
            for (var i = 0; i < series.Values.Count; i++)
            {
                if (series.Values[i] is not { } v)
                    continue;
                var xv = series.XValues is { } xs ? i < xs.Count ? xs[i] : null : i + 1;
                var size = series.BubbleSizes is { } bs ? i < bs.Count ? bs[i] : null : 1;
                if (xv is not { } xValue || size is not { } s || s == 0 || s < 0 && !plot.NegativeBubbles || ++_markers > MaxMarkers)
                    continue;
                var share = sizes > 0 ? Math.Abs(s) / sizes : 1;
                var radius = (float)(largest * (plot.BubbleWidth ? share : Math.Sqrt(share)));
                var along = Along(x, xScale.Fraction(xValue), inner);
                var across = Along(y, yScale.Fraction(v), inner);
                var center = x.Horizontal ? new SKPoint(along, across) : new SKPoint(across, along);
                var style = series.Points.GetValueOrDefault(i) ?? series.Style;
                var color = style.Fill is { Alpha: > 0 } own ? own : auto;
                Brush? fill = s < 0 ? null
                    : series.Bubble3D && style.Gradient is null && style.Hatch is null
                        ? new Brush(color, Gradient: new ChartGradient([(0, Lighter(color, 0.55f)), (1, color)], Radial: true, CenterX: 0.35f, CenterY: 0.3f))
                        : FillOf(style, auto);
                var line = PenOf(style.Line, s < 0 ? color : null, 0.75f);
                bubbles.Add((center, Math.Max(radius, 0.5f), fill, line));
                if (LabelsAt(plot, series, i) is { Any: true } options && DataLabel(plot, series, i, v, options) is { } text)
                    TextCentred(text, center.X, center.Y);
            }
        }
        Clipped(inner, canvas =>
        {
            foreach (var (center, radius, fill, line) in bubbles)
            {
                using var path = new SKPath();
                path.AddCircle(center.X, center.Y, radius);
                FillPath(canvas, path, fill);
                StrokePath(canvas, path, line);
            }
        });
    }

    // ───────────────────────── Stock ─────────────────────────

    /// <summary>A line plot's high-low lines, up and down bars (between the first and the last series) and drop lines.</summary>
    private void StockLines(ChartPlot plot, Axis x, Axis y, SKRect inner)
    {
        if (plot.HighLowLines is null && plot.UpDownBars is null && plot.DropLines is null || y.Scale is not { } scale || plot.Series.Count == 0)
            return;
        var count = Math.Min(plot.Series.Max(s => s.Values.Count), Math.Max(x.Count, 1));
        SKPoint P(float along, float across) => x.Horizontal ? new SKPoint(along, across) : new SKPoint(across, along);
        float Value(double v) => Along(y, scale.Fraction(v), inner);
        var slot = (x.Horizontal ? inner.Width : inner.Height) / Math.Max(1, x.Count);
        var drops = new List<(SKPoint, SKPoint)>();
        var highLow = new List<(SKPoint, SKPoint)>();
        var bars = new List<(SKRect Rect, bool Up)>();
        var baseline = Value(scale.Crossing(x.Definition));
        for (var i = 0; i < count && _markers < MaxMarkers; i++, _markers++)
        {
            var at = Along(x, x.Fraction(i), inner);
            var values = plot.Series.Where(s => i < s.Values.Count && s.Values[i] is not null).Select(s => s.Values[i]!.Value).ToList();
            if (plot.DropLines is not null)
                drops.AddRange(values.Select(v => (P(at, Value(v)), P(at, baseline))));
            if (plot.HighLowLines is not null && values.Count >= 2)
                highLow.Add((P(at, Value(values.Max())), P(at, Value(values.Min()))));
            if (plot.UpDownBars is { } upDown && plot.Series.Count >= 2 && i < plot.Series[0].Values.Count && i < plot.Series[^1].Values.Count
                && plot.Series[0].Values[i] is { } open && plot.Series[^1].Values[i] is { } close)
            {
                var half = slot / (1 + upDown.GapWidth / 100f) / 2;
                var (a, b) = (P(at - half, Value(open)), P(at + half, Value(close)));
                bars.Add((new SKRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)), close >= open));
            }
        }
        var dropPen = PenOf(plot.DropLines, SKColors.Black, 0.75f);
        var highLowPen = PenOf(plot.HighLowLines, SKColors.Black, 0.75f);
        var up = plot.UpDownBars?.Up ?? ChartShapeStyle.Auto;
        var down = plot.UpDownBars?.Down ?? ChartShapeStyle.Auto;
        var (upFill, upLine) = (FillOf(up, SKColors.White), PenOf(up.Line, new SKColor(0x40, 0x40, 0x40), 0.75f));
        var (downFill, downLine) = (FillOf(down, new SKColor(0x40, 0x40, 0x40)), PenOf(down.Line, new SKColor(0x40, 0x40, 0x40), 0.75f));
        Clipped(inner, canvas =>
        {
            foreach (var (segments, pen) in new[] { (drops, dropPen), (highLow, highLowPen) })
            {
                using var path = new SKPath();
                foreach (var (from, to) in segments)
                {
                    path.MoveTo(from);
                    path.LineTo(to);
                }
                StrokePath(canvas, path, pen);
            }
            foreach (var (rect, isUp) in bars)
            {
                using var path = new SKPath();
                path.AddRect(rect);
                FillPath(canvas, path, isUp ? upFill : downFill);
                StrokePath(canvas, path, isUp ? upLine : downLine);
            }
        });
    }

    // ───────────────────────── Surfaces ─────────────────────────

    /// <summary>The value bands of a surface: its value axis' bounds and unit, or ones picked for about five bands; null without values.</summary>
    private Scale? SurfaceScale(ChartPlot plot, ChartAxis? axis)
    {
        var values = plot.Series.SelectMany(s => s.Values).OfType<double>().ToList();
        if (values.Count == 0)
            return null;
        return AutoScale(values.Min(), values.Max(), axis ?? new ChartAxis { IsValue = true }, 5);
    }

    private static int Bands(Scale scale) => (int)Math.Clamp(Math.Round((scale.Max - scale.Min) / scale.Unit), 1, 256);

    private static int Band(Scale scale, double value) =>
        (int)Math.Clamp(Math.Floor((value - scale.Min) / scale.Unit + 1e-9), 0, Bands(scale) - 1);

    /// <summary>A band's fill and outline: its c:bandFmt, or the automatic colour of its place.</summary>
    private (Brush? Fill, Pen? Line) BandFill(ChartPlot plot, int band)
    {
        var style = plot.Bands.GetValueOrDefault(band) ?? ChartShapeStyle.Auto;
        var auto = _chart.Palette.Color(band);
        return (FillOf(style, auto), PenOf(style.Line, null, 0.75f));
    }

    /// <summary>A surface seen from above: series up, categories across, each cell coloured by its band in steps between its corners.</summary>
    private void Contour(ChartPlot plot, SKRect area)
    {
        if (plot.Series.Count == 0 || SurfaceScale(plot, _chart.Axis(plot.AxisIds.ElementAtOrDefault(1))) is not { } scale)
            return;
        var grid = plot.Series.Count == 1 ? [plot.Series[0], plot.Series[0]] : plot.Series;
        var rows = grid.Count;
        var n = plot.Series.Max(s => s.Values.Count);
        if (n == 0)
            return;
        var categoryDefinition = _chart.Axis(plot.AxisIds.ElementAtOrDefault(0));
        var seriesDefinition = _chart.Axes.FirstOrDefault(a => a.IsSeries);
        var style = categoryDefinition?.Style ?? _chart.Text;
        var names = seriesDefinition is { Deleted: true } or { LabelPosition: "none" } ? [] : plot.Series.Take(MaxMeasured).Select(s => Text(s.Name ?? $"Series{s.Index + 1}", seriesDefinition?.Style ?? style)).ToList();
        var lineHeight = Text("Ag", style).Height;
        var left = names.Count == 0 ? 0 : names.Max(t => t.Width) + 6 * _s;
        var showCategories = categoryDefinition is null or { Deleted: false, LabelPosition: not "none" };
        var box = new SKRect(area.Left + left, area.Top + 4 * _s, area.Right - 8 * _s, area.Bottom - (showCategories ? lineHeight + 6 * _s : 4 * _s));
        if (box.Width <= 2 || box.Height <= 2)
            return;
        float X(double i) => n == 1 ? box.MidX : box.Left + box.Width * (float)(i / (n - 1));
        float Y(double j) => box.Bottom - box.Height * (float)(j / (rows - 1));
        double? V(int i, int j) => i < grid[j].Values.Count ? grid[j].Values[i] : null;
        var cellCount = (long)Math.Max(1, n - 1) * (rows - 1);
        var steps = (int)Math.Clamp(Math.Round(Math.Sqrt(20000.0 / cellCount)), 1, 8);
        var cells = new List<(SKRect Rect, int Band)>();
        var wires = new List<(SKPoint From, SKPoint To, int Band)>();
        for (var j = 0; j + 1 < rows; j++)
        {
            for (var i = 0; i < Math.Max(1, n - 1); i++)
            {
                if (++_markers > MaxMarkers)
                    break;
                var i1 = Math.Min(i + 1, n - 1);
                if (V(i, j) is not { } a || V(i1, j) is not { } b || V(i1, j + 1) is not { } c || V(i, j + 1) is not { } d)
                    continue;
                if (plot.Wireframe)
                {
                    wires.Add((new SKPoint(X(i), Y(j)), new SKPoint(X(i1), Y(j)), Band(scale, (a + b) / 2)));
                    wires.Add((new SKPoint(X(i), Y(j)), new SKPoint(X(i), Y(j + 1)), Band(scale, (a + d) / 2)));
                    if (j + 2 == rows)
                        wires.Add((new SKPoint(X(i), Y(j + 1)), new SKPoint(X(i1), Y(j + 1)), Band(scale, (c + d) / 2)));
                    if (i + 2 >= n)
                        wires.Add((new SKPoint(X(i1), Y(j)), new SKPoint(X(i1), Y(j + 1)), Band(scale, (b + c) / 2)));
                    continue;
                }
                for (var u = 0; u < steps; u++)
                {
                    for (var w = 0; w < steps; w++)
                    {
                        var (fu, fw) = ((u + 0.5) / steps, (w + 0.5) / steps);
                        var value = a * (1 - fu) * (1 - fw) + b * fu * (1 - fw) + c * fu * fw + d * (1 - fu) * fw;
                        var rect = new SKRect(X(i + (double)u / steps * (i1 - i)), Y(j + (w + 1.0) / steps), X(i + (u + 1.0) / steps * (i1 - i)), Y(j + (double)w / steps));
                        if (n == 1)
                            rect = new SKRect(box.Left, rect.Top, box.Right, rect.Bottom);
                        cells.Add((rect, Band(scale, value)));
                    }
                }
            }
        }
        var bands = cells.Select(c => c.Band).Concat(wires.Select(w => w.Band)).Distinct().ToDictionary(b => b, b => BandFill(plot, b));
        Shape(canvas =>
        {
            using var paint = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
            foreach (var (rect, band) in cells)
            {
                using var path = new SKPath();
                // A hair wider, so neighbouring cells leave no seam.
                path.AddRect(SKRect.Inflate(rect, 0.15f, 0.15f));
                FillPath(canvas, path, bands[band].Fill);
            }
            foreach (var (from, to, band) in wires)
            {
                using var path = new SKPath();
                path.MoveTo(from);
                path.LineTo(to);
                StrokePath(canvas, path, bands[band].Line ?? new Pen(bands[band].Fill?.Color ?? SKColors.Gray, 1.5f * _s, null));
            }
        });
        if (showCategories)
        {
            var labels = Enumerable.Range(0, Math.Min(n, MaxMeasured)).Select(i => Text(Category(plot.Series, i), style)).ToList();
            var widest = labels.Count == 0 ? 0 : labels.Max(l => l.Width);
            var every = Math.Max(1, (int)Math.Ceiling(widest * 1.2 / Math.Max(box.Width / Math.Max(1, n - 1), 0.01)));
            for (var i = 0; i < labels.Count; i += every)
                TextAt(labels[i], X(i) - labels[i].Width / 2, box.Bottom + 4 * _s);
        }
        if (names.Count > 0)
        {
            var every = Math.Max(1, (int)Math.Ceiling(lineHeight * 1.1 / Math.Max(box.Height / Math.Max(1, plot.Series.Count - 1), 0.01)));
            for (var j = 0; j < names.Count; j += every)
                TextAt(names[j], box.Left - 6 * _s - names[j].Width + 2 * _s, (plot.Series.Count == 1 ? box.MidY : Y(j)) - names[j].Height / 2);
        }
    }

    // ───────────────────────── Bar of pie, pie of pie ─────────────────────────

    /// <summary>The points a bar or pie of pie sends to its second plot (c:splitType and c:splitPos, c:custSplit).</summary>
    internal static HashSet<int> SecondPlot(ChartPlot plot, ChartSeries series)
    {
        var points = Enumerable.Range(0, series.Values.Count).Where(i => series.Values[i] is { } v && Math.Abs(v) > 0).ToList();
        var total = points.Sum(i => Math.Abs(series.Values[i]!.Value));
        return plot.SplitType switch
        {
            "cust" => [.. points.Where(plot.CustomSplit.Contains)],
            "val" => [.. points.Where(i => Math.Abs(series.Values[i]!.Value) < (plot.SplitPosition ?? total / Math.Max(1, points.Count) / 2))],
            "percent" => [.. points.Where(i => total > 0 && Math.Abs(series.Values[i]!.Value) / total * 100 < (plot.SplitPosition ?? 10))],
            _ => [.. points.Skip(Math.Max(0, points.Count - (int)Math.Clamp(plot.SplitPosition ?? Math.Clamp(points.Count / 3, 1, 3), 0, points.Count)))],
        };
    }

    private void OfPie(ChartPlot plot, SKRect area)
    {
        if (plot.Series.Count == 0)
            return;
        var series = plot.Series[0];
        var second = SecondPlot(plot, series);
        var points = Enumerable.Range(0, series.Values.Count).Where(i => series.Values[i] is { } v && Math.Abs(v) > 0).ToList();
        var main = points.Where(i => !second.Contains(i)).ToList();
        var other = second.Sum(i => Math.Abs(series.Values[i]!.Value));
        var mainTotal = main.Sum(i => Math.Abs(series.Values[i]!.Value)) + other;
        if (!(mainTotal > 0))
            return;
        var labelled = (series.Labels ?? plot.Labels) is { Any: true, Position: "outEnd" } || series.PointLabels.Values.Any(l => l is { Any: true, Position: "outEnd" });
        var size = plot.SecondPieSize / 100f;
        var gap = 0.3f * plot.GapWidth / 100f;
        var diameter = Math.Min(area.Height * (labelled ? 0.72f : 0.9f), area.Width * (labelled ? 0.8f : 0.95f) / (1 + gap + size));
        if (diameter <= 2)
            return;
        var r1 = diameter / 2;
        var r2 = r1 * size;
        var left = area.MidX - (diameter * (1 + gap + size)) / 2;
        var c1 = new SKPoint(left + r1, area.MidY);
        var c2 = new SKPoint(left + diameter * (1 + gap) + r2, area.MidY);
        var options = series.Labels ?? plot.Labels;

        // The first pie: its own points, then "Other" facing the second plot.
        var otherSweep = (float)(other / mainTotal * 360);
        var angle = other > 0 ? otherSweep / 2 : -90 + (float)plot.FirstSliceAngle;
        foreach (var i in main)
        {
            var v = Math.Abs(series.Values[i]!.Value);
            var sweep = (float)(v / mainTotal * 360);
            Slice(c1, r1, angle, sweep, i, PointLabel(i, v / mainTotal), LabelsAt(plot, series, i)?.Position);
            angle += sweep;
        }
        if (other > 0)
        {
            var text = options is { Any: true } ? OtherLabel(series, other, other / mainTotal, options) : null;
            Slice(c1, r1, angle, otherSweep, -1, text, options?.Position);
        }

        // The second plot: a pie of the points sent there, or a stacked bar of them.
        SKPoint upper, lower;
        if (plot.OfPieBar)
        {
            var bar = new SKRect(c2.X - r2 * 0.45f, c2.Y - r2, c2.X + r2 * 0.45f, c2.Y + r2);
            var y = bar.Top;
            foreach (var i in second.Order())
            {
                var v = Math.Abs(series.Values[i]!.Value);
                var height = (float)(v / other) * bar.Height;
                var rect = new SKRect(bar.Left, y, bar.Right, y + height);
                var style = series.Points.GetValueOrDefault(i) ?? series.Style;
                var (fill, line) = (FillOf(style, _chart.Palette.Color(i)), PenOf(style.Line, SKColors.White, 0.75f));
                Shape(canvas =>
                {
                    using var path = new SKPath();
                    path.AddRect(rect);
                    FillPath(canvas, path, fill);
                    StrokePath(canvas, path, line);
                });
                if (LabelsAt(plot, series, i) is { Any: true } own && DataLabel(plot, series, i, series.Values[i]!.Value, own, v / mainTotal) is { } text)
                    TextCentred(text, rect.MidX, rect.MidY);
                y += height;
            }
            (upper, lower) = (new SKPoint(bar.Left, bar.Top), new SKPoint(bar.Left, bar.Bottom));
        }
        else
        {
            var ordered = second.Order().ToList();
            var firstSweep = ordered.Count == 0 ? 0 : (float)(Math.Abs(series.Values[ordered[0]]!.Value) / other * 360);
            var start = 180 - firstSweep / 2;
            foreach (var i in ordered)
            {
                var v = Math.Abs(series.Values[i]!.Value);
                var sweep = (float)(v / other * 360);
                Slice(c2, r2, start, sweep, i, PointLabel(i, v / mainTotal), LabelsAt(plot, series, i)?.Position);
                start += sweep;
            }
            (upper, lower) = (new SKPoint(c2.X, c2.Y - r2), new SKPoint(c2.X, c2.Y + r2));
        }
        if (other > 0 && second.Count > 0 && PenOf(plot.SeriesLines, plot.SeriesLines is null ? null : new SKColor(0x40, 0x40, 0x40), 0.75f) is { } pen)
        {
            var half = otherSweep / 2 * (float)Math.PI / 180;
            var top = new SKPoint(c1.X + r1 * (float)Math.Cos(-half), c1.Y + r1 * (float)Math.Sin(-half));
            var bottom = new SKPoint(c1.X + r1 * (float)Math.Cos(half), c1.Y + r1 * (float)Math.Sin(half));
            Shape(canvas =>
            {
                using var path = new SKPath();
                path.MoveTo(top);
                path.LineTo(upper);
                path.MoveTo(bottom);
                path.LineTo(lower);
                StrokePath(canvas, path, pen);
            });
        }

        TextBlock? PointLabel(int index, double share) =>
            LabelsAt(plot, series, index) is { Any: true } own ? DataLabel(plot, series, index, series.Values[index]!.Value, own, share) : null;

        // A slice of point index (−1: "Other", in the colour after the last point's), with its label.
        void Slice(SKPoint center, float radius, float start, float sweep, int index, TextBlock? text, string? position)
        {
            var style = index >= 0 ? series.Points.GetValueOrDefault(index) ?? series.Style : ChartShapeStyle.Auto;
            var auto = _chart.Palette.Color(index >= 0 ? index : series.Values.Count);
            var (fill, line) = (FillOf(style, auto), PenOf(style.Line, SKColors.White, 0.75f));
            Shape(canvas =>
            {
                using var path = new SKPath();
                var box = new SKRect(center.X - radius, center.Y - radius, center.X + radius, center.Y + radius);
                if (sweep >= 359.999f)
                {
                    path.AddOval(box);
                }
                else
                {
                    path.MoveTo(center);
                    path.ArcTo(box, start, sweep, false);
                    path.Close();
                }
                FillPath(canvas, path, fill);
                StrokePath(canvas, path, line);
            });
            if (text is null)
                return;
            var middle = (start + sweep / 2) * Math.PI / 180;
            var distance = position == "outEnd" ? radius + 6 * _s + Math.Max(text.Width, text.Height) / 2 : position == "ctr" ? radius / 2 : radius * 0.62f;
            TextCentred(text, center.X + (float)Math.Cos(middle) * distance, center.Y + (float)Math.Sin(middle) * distance);
        }
    }

    /// <summary>The label of a bar or pie of pie's "Other" slice: its name, the points' sum, its share.</summary>
    private TextBlock? OtherLabel(ChartSeries series, double value, double share, ChartLabels options)
    {
        if (++_labels > MaxLabels)
            return null;
        var parts = new List<string>(4);
        if (options.Series && series.Name is { Length: > 0 } name)
            parts.Add(name);
        if (options.Category)
            parts.Add("Other");
        if (options.Value)
            parts.Add(Format(value, options.Format ?? series.Format));
        if (options.Percent)
            parts.Add(Format(share, "0%"));
        return parts.Count == 0 ? null : Block([.. string.Join(options.Separator ?? ", ", parts).Split('\n').Take(ChartReader.MaxLines).Select(l => Text(l, options.Style))]);
    }
}
