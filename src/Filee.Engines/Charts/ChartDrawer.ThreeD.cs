// 3D charts (c:view3D) as an oblique projection, the way Excel draws them with right-angled axes: the plot's front
// plane stays flat and its depth runs back and up (or down, seen from below) at the angle the view's rotations give,
// so the back wall is the front plane moved by the depth. Bars are boxes (front, top and side faces in shades of
// their fill), lines ribbons, areas slabs, surfaces a mesh coloured by value band; series stand one behind the other
// where the plot says so ("standard" bars and areas, 3D lines and surfaces). Walls and floor take their fills and the
// gridlines run over them. A 3D pie is an ellipse as tilted as the view (c:rotX) with its sides drawn below the
// slices, turned by c:rotY. Perspective is drawn as the oblique view. Faces are drawn back to front.

using SkiaSharp;

namespace Filee.Engines.Charts;

internal sealed partial class ChartDrawer
{
    /// <summary>
    /// The depth of a 3D plot on the page: (<see cref="Dx"/>, <see cref="Dy"/>) is how far the back of the plot (depth
    /// 1) is moved from its front (depth 0); each series row keeps a share of its slot, the rest is the gap between rows.
    /// </summary>
    private sealed class Depth(float dx, float dy, int gapDepth)
    {
        public float Dx { get; } = dx;
        public float Dy { get; } = dy;

        /// <summary>How far depth <paramref name="z"/> (0 to 1) is moved on the page.</summary>
        public SKPoint Offset(float z) => new(Dx * z, Dy * z);

        public SKPoint At(SKPoint point, float z) => new(point.X + Dx * z, point.Y + Dy * z);

        /// <summary>The depths row <paramref name="row"/> of <paramref name="count"/> takes: its slot less the gap.</summary>
        public (float Z0, float Z1) Row(int row, int count)
        {
            count = Math.Max(1, count);
            var share = 1 / (1 + gapDepth / 100.0);
            var pad = (1 - share) / 2;
            return ((float)((row + pad) / count), (float)((row + 1 - pad) / count));
        }
    }

    private static SKRect Shifted(SKRect rect, SKPoint by) => new(rect.Left + by.X, rect.Top + by.Y, rect.Right + by.X, rect.Bottom + by.Y);

    /// <summary>A colour darkened to <paramref name="factor"/> of its brightness.</summary>
    private static SKColor Shade(SKColor color, float factor) =>
        new((byte)Math.Clamp(color.Red * factor, 0, 255), (byte)Math.Clamp(color.Green * factor, 0, 255), (byte)Math.Clamp(color.Blue * factor, 0, 255), color.Alpha);

    /// <summary>A colour mixed with white by <paramref name="amount"/>.</summary>
    private static SKColor Lighter(SKColor color, float amount) =>
        new(Mix(color.Red, 255, amount), Mix(color.Green, 255, amount), Mix(color.Blue, 255, amount), color.Alpha);

    /// <summary>
    /// Where the plot's front plane lies in <paramref name="inner"/> and how its depth runs: back and to the side the
    /// chart is turned to (c:rotY), up when seen from above (c:rotX), as deep as c:depthPercent and the series rows ask.
    /// </summary>
    private (SKRect Front, Depth Depth) Projection(SKRect inner, int rows, int gapDepth)
    {
        var view = _chart.View3D ?? new ChartView3D();
        // Seen from behind (rotY past 90°) is drawn as seen from the front, turned as far the other way.
        var turn = view.RotY > 180 ? view.RotY - 360 : view.RotY;
        if (Math.Abs(turn) > 90)
            turn = Math.Sign(turn) * (180 - Math.Abs(turn));
        var ux = Math.Sin(Math.Clamp(turn, -80, 80) * Math.PI / 180);
        var uy = Math.Sin(Math.Clamp(view.RotX, -80, 80) * Math.PI / 180);
        var magnitude = Math.Sqrt(ux * ux + uy * uy);
        var side = Math.Min(inner.Width, inner.Height);
        // Series one behind the other need more depth to be seen.
        var length = side * 0.22 * Math.Clamp(view.DepthPercent / 100.0, 0.2, 3) * Math.Min(Math.Pow(rows, 0.8), 3);
        length = Math.Min(length, side * 0.6);
        if (magnitude < 1e-3)
            return (inner, new Depth(0, 0, gapDepth));
        var dx = (float)(length * ux / Math.Max(magnitude, 0.5));
        var dy = (float)(-length * uy / Math.Max(magnitude, 0.5));
        var front = new SKRect(inner.Left + Math.Max(0, -dx), inner.Top + Math.Max(0, -dy), inner.Right - Math.Max(0, dx), inner.Bottom - Math.Max(0, dy));
        return (front, new Depth(dx, dy, gapDepth));
    }

    /// <summary>A 3D plot area: walls and floor, gridlines over them, the plots and their axes on the front plane.</summary>
    private void ThreeD(List<(ChartPlot Plot, Axis Category, Axis Value)> pairs, Dictionary<uint, Axis> axes, SKRect inner, ChartShapeStyle plotStyle)
    {
        var rows = pairs.Max(p => p.Plot.SeriesInDepth ? Math.Max(1, p.Plot.Series.Count) : 1);
        var (front, depth) = Projection(inner, rows, pairs[0].Plot.GapDepth);
        if (front.Width <= 2 || front.Height <= 2)
            return;
        foreach (var axis in axes.Values)
            Prepare(axis, axis.Horizontal ? front.Width : front.Height);
        foreach (var (plot, _, value) in pairs)
        {
            if (plot.Kind == ChartKind.Surface && SurfaceScale(plot, value.Definition) is { } bands)
                value.Scale = bands;
        }
        PlotBackground(inner, plotStyle);
        Walls(front, depth);
        foreach (var axis in axes.Values)
            Gridlines3D(axis, front, depth);
        foreach (var (plot, category, value) in pairs)
        {
            switch (plot.Kind)
            {
                case ChartKind.Bar:
                    Bars(plot, category, value, front, depth);
                    break;
                case ChartKind.Area:
                    Areas(plot, category, value, front, depth);
                    break;
                case ChartKind.Line:
                    Lines(plot, category, value, front, depth);
                    break;
                case ChartKind.Surface:
                    Surface3D(plot, category, value, front, depth);
                    break;
            }
        }
        foreach (var axis in axes.Values)
            AxisLine(axis, front);
        SeriesNames(pairs, front, depth);
    }

    /// <summary>The back wall, the side wall on the far side and the floor, in their fills and outlines (none unless the file gives them).</summary>
    private void Walls(SKRect front, Depth depth)
    {
        var back = Shifted(front, depth.Offset(1));
        SKPoint[] floor = depth.Dy <= 0
            ? [new(front.Left, front.Bottom), new(front.Right, front.Bottom), new(back.Right, back.Bottom), new(back.Left, back.Bottom)]
            : [new(front.Left, front.Top), new(front.Right, front.Top), new(back.Right, back.Top), new(back.Left, back.Top)];
        SKPoint[] wall = depth.Dx >= 0
            ? [new(front.Left, front.Top), new(back.Left, back.Top), new(back.Left, back.Bottom), new(front.Left, front.Bottom)]
            : [new(front.Right, front.Top), new(back.Right, back.Top), new(back.Right, back.Bottom), new(front.Right, front.Bottom)];
        SKPoint[] backWall = [new(back.Left, back.Top), new(back.Right, back.Top), new(back.Right, back.Bottom), new(back.Left, back.Bottom)];
        var faces = new List<(SKPoint[] Points, Brush? Fill, Pen? Line)>
        {
            (backWall, FillOf(_chart.BackWall, null), PenOf(_chart.BackWall.Line, null, 0.75f)),
            (wall, FillOf(_chart.SideWall, null), PenOf(_chart.SideWall.Line, null, 0.75f)),
            (floor, FillOf(_chart.Floor, null), PenOf(_chart.Floor.Line, null, 0.75f)),
        };
        Shape(canvas =>
        {
            foreach (var (points, fill, line) in faces)
            {
                if (fill is null && line is null)
                    continue;
                using var path = new SKPath();
                path.AddPoly(points);
                FillPath(canvas, path, fill);
                StrokePath(canvas, path, line);
            }
        });
    }

    /// <summary>An axis' gridlines over the floor or side wall (from the front to the back) and up the back wall.</summary>
    private void Gridlines3D(Axis axis, SKRect front, Depth depth)
    {
        var definition = axis.Definition;
        if (definition.MajorGridlines is not { } major || PenOf(major, GridGray, 0.75f) is not { } pen)
            return;
        var fractions = axis.Scale is { } scale ? scale.Ticks().Select(scale.Fraction).ToList()
            : axis.Count > 0 ? axis.Between ? [.. Enumerable.Range(0, Math.Min(axis.Count, MaxTicks) + 1).Select(i => axis.Edge(i))]
                : [.. Enumerable.Range(0, Math.Min(axis.Count, MaxTicks)).Select(i => axis.Fraction(i))]
            : [];
        var back = depth.Offset(1);
        var segments = new List<(SKPoint, SKPoint)>();
        foreach (var f in fractions)
        {
            if (f is < -1e-6 or > 1 + 1e-6)
                continue;
            var at = Along(axis, f, front);
            if (axis.Horizontal)
            {
                var edge = depth.Dy <= 0 ? front.Bottom : front.Top;
                var far = depth.Dy <= 0 ? front.Top : front.Bottom;
                segments.Add((new SKPoint(at, edge), new SKPoint(at + back.X, edge + back.Y)));
                segments.Add((new SKPoint(at + back.X, edge + back.Y), new SKPoint(at + back.X, far + back.Y)));
            }
            else
            {
                var edge = depth.Dx >= 0 ? front.Left : front.Right;
                segments.Add((new SKPoint(edge, at), new SKPoint(edge + back.X, at + back.Y)));
                segments.Add((new SKPoint(front.Left + back.X, at + back.Y), new SKPoint(front.Right + back.X, at + back.Y)));
            }
        }
        if (segments.Count == 0)
            return;
        Shape(canvas =>
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

    /// <summary>The series' names along the floor's edge, one per row, when the series axis (c:serAx) is shown.</summary>
    private void SeriesNames(List<(ChartPlot Plot, Axis Category, Axis Value)> pairs, SKRect front, Depth depth)
    {
        if (_chart.Axes.FirstOrDefault(a => a.IsSeries) is not { Deleted: false, LabelPosition: not "none" } axis
            || pairs.FirstOrDefault(p => p.Plot.SeriesInDepth).Plot is not { } plot)
            return;
        var count = plot.Series.Count;
        var step = Math.Max(1, (int)Math.Ceiling(count / (double)MaxMeasured));
        for (var r = 0; r < count; r += step)
        {
            var (z0, z1) = depth.Row(r, count);
            var at = depth.At(new SKPoint(depth.Dx >= 0 ? front.Right : front.Left, depth.Dy <= 0 ? front.Bottom : front.Top), (z0 + z1) / 2);
            var text = Text(plot.Series[r].Name ?? $"Series{plot.Series[r].Index + 1}", axis.Style);
            TextAt(text, depth.Dx >= 0 ? at.X + 4 * _s : at.X - 4 * _s - text.Width, at.Y - text.Height / 2);
        }
    }

    /// <summary>Boxes (bars in 3D): each its front face at its row's front depth and the top and side faces back to the row's far depth.</summary>
    private void Boxes3D(List<(SKRect Rect, Brush? Fill, Pen? Line, (float Z0, float Z1) Row)> boxes, Depth depth)
    {
        var fromAbove = depth.Dy <= 0;
        var fromRight = depth.Dx >= 0;
        var ordered = boxes
            .OrderByDescending(b => b.Row.Z0)
            .ThenBy(b => fromAbove ? -b.Rect.Bottom : b.Rect.Top)
            .ThenBy(b => fromRight ? b.Rect.Left : -b.Rect.Right)
            .Take(MaxMarkers)
            .ToList();
        Shape(canvas =>
        {
            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            foreach (var (rect, fill, line, (z0, z1)) in ordered)
            {
                var (o0, o1) = (depth.Offset(z0), depth.Offset(z1));
                var y = fromAbove ? rect.Top : rect.Bottom;
                var x = fromRight ? rect.Right : rect.Left;
                SKPoint[] top = [new(rect.Left + o0.X, y + o0.Y), new(rect.Right + o0.X, y + o0.Y), new(rect.Right + o1.X, y + o1.Y), new(rect.Left + o1.X, y + o1.Y)];
                SKPoint[] side = [new(x + o0.X, rect.Top + o0.Y), new(x + o0.X, rect.Bottom + o0.Y), new(x + o1.X, rect.Bottom + o1.Y), new(x + o1.X, rect.Top + o1.Y)];
                var faceLine = line ?? (fill is { } f ? new Pen(Shade(f.Color, 0.85f), 0.25f, null) : null);
                foreach (var (points, factor) in new[] { (side, 0.78f), (top, 1.2f) })
                {
                    using var path = new SKPath();
                    path.AddPoly(points);
                    if (fill is { } brush && brush.Color.Alpha > 0)
                    {
                        paint.Color = factor > 1 ? Lighter(brush.Color, 0.25f) : Shade(brush.Color, factor);
                        canvas.DrawPath(path, paint);
                    }
                    StrokePath(canvas, path, faceLine);
                }
                using var face = new SKPath();
                face.AddRect(Shifted(rect, o0));
                FillPath(canvas, face, fill);
                StrokePath(canvas, face, line);
            }
        });
    }

    /// <summary>Lines in 3D: each a ribbon in its series' row, the rows drawn back to front.</summary>
    private void Ribbons3D(List<(SKPoint[] Points, Brush? Fill, Pen? Line, int Row)> ribbons, int rows, Depth depth)
    {
        var fromRight = depth.Dx >= 0;
        var ordered = ribbons.OrderByDescending(r => r.Row).ToList();
        Shape(canvas =>
        {
            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            foreach (var (points, fill, line, row) in ordered)
            {
                var (z0, z1) = depth.Row(row, rows);
                var (o0, o1) = (depth.Offset(z0), depth.Offset(z1));
                var indices = Enumerable.Range(0, Math.Max(0, points.Length - 1));
                foreach (var i in fromRight ? indices : indices.Reverse())
                {
                    var (p, q) = (points[i], points[i + 1]);
                    using var path = new SKPath();
                    path.AddPoly([new(p.X + o0.X, p.Y + o0.Y), new(q.X + o0.X, q.Y + o0.Y), new(q.X + o1.X, q.Y + o1.Y), new(p.X + o1.X, p.Y + o1.Y)]);
                    if (fill is { } brush)
                    {
                        // Rising segments face the viewer less: a little darker.
                        paint.Color = q.Y < p.Y ? Shade(brush.Color, 0.88f) : Lighter(brush.Color, 0.12f);
                        canvas.DrawPath(path, paint);
                    }
                    StrokePath(canvas, path, line);
                }
                if (fill is { } edge && points.Length > 1)
                {
                    using var front = new SKPath();
                    front.MoveTo(points[0].X + o0.X, points[0].Y + o0.Y);
                    foreach (var p in points.Skip(1))
                        front.LineTo(p.X + o0.X, p.Y + o0.Y);
                    StrokePath(canvas, front, new Pen(Shade(edge.Color, 0.8f), 1f * _s, null), round: true);
                }
            }
        });
    }

    /// <summary>Areas in 3D: slabs whose top strips and end face run back to the row's far depth, the rows back to front.</summary>
    private void Slabs3D(List<(SKPoint[] Top, SKPoint[] Bottom, Brush? Fill, Pen? Line, int Row)> slabs, int rows, Depth depth)
    {
        var fromRight = depth.Dx >= 0;
        var ordered = slabs.Select((s, i) => (Slab: s, Order: i)).OrderByDescending(s => s.Slab.Row).ThenBy(s => s.Order).Select(s => s.Slab).ToList();
        Shape(canvas =>
        {
            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            foreach (var (top, bottom, fill, line, row) in ordered)
            {
                if (top.Length == 0)
                    continue;
                var (z0, z1) = depth.Row(row, rows);
                var (o0, o1) = (depth.Offset(z0), depth.Offset(z1));
                var color = fill?.Color ?? SKColors.Transparent;
                var end = fromRight ? top.Length - 1 : 0;
                var faces = new List<(SKPoint[] Points, SKColor Color)>
                {
                    ([new(top[end].X + o0.X, top[end].Y + o0.Y), new(bottom[end].X + o0.X, bottom[end].Y + o0.Y), new(bottom[end].X + o1.X, bottom[end].Y + o1.Y), new(top[end].X + o1.X, top[end].Y + o1.Y)], Shade(color, 0.78f)),
                };
                for (var i = 0; i + 1 < top.Length; i++)
                {
                    var (p, q) = (top[i], top[i + 1]);
                    faces.Add(([new(p.X + o0.X, p.Y + o0.Y), new(q.X + o0.X, q.Y + o0.Y), new(q.X + o1.X, q.Y + o1.Y), new(p.X + o1.X, p.Y + o1.Y)], Lighter(color, 0.22f)));
                }
                foreach (var (points, shade) in faces)
                {
                    using var path = new SKPath();
                    path.AddPoly(points);
                    if (shade.Alpha > 0)
                    {
                        paint.Color = shade;
                        canvas.DrawPath(path, paint);
                    }
                    StrokePath(canvas, path, line ?? (color.Alpha > 0 ? new Pen(Shade(color, 0.85f), 0.25f, null) : null));
                }
                using var front = new SKPath();
                front.MoveTo(top[0].X + o0.X, top[0].Y + o0.Y);
                for (var i = 1; i < top.Length; i++)
                    front.LineTo(top[i].X + o0.X, top[i].Y + o0.Y);
                for (var i = bottom.Length - 1; i >= 0; i--)
                    front.LineTo(bottom[i].X + o0.X, bottom[i].Y + o0.Y);
                front.Close();
                FillPath(canvas, front, fill);
                StrokePath(canvas, front, line);
            }
        });
    }

    /// <summary>
    /// A 3D surface: the series one behind the other, each value a point of a mesh whose cells take the colour of
    /// their value band (c:bandFmts or the automatic colours), or only their outline for a wireframe; back to front.
    /// </summary>
    private void Surface3D(ChartPlot plot, Axis category, Axis value, SKRect front, Depth depth)
    {
        if (value.Scale is not { } scale || plot.Series.Count == 0)
            return;
        var rows = plot.Series.Count;
        var n = Math.Min(plot.Series.Max(s => s.Values.Count), category.Count > 0 ? category.Count : int.MaxValue);
        if (n < 1)
            return;
        // A single series is a strip from the front to the back.
        var grid = rows == 1 ? [plot.Series[0], plot.Series[0]] : plot.Series;
        var depthRows = grid.Count;
        SKPoint? Node(int i, int j)
        {
            var values = grid[j].Values;
            if (i >= values.Count || values[i] is not { } v)
                return null;
            var x = n == 1 ? front.MidX : Along(category, category.Fraction(i), front);
            var y = Along(value, scale.Fraction(Math.Clamp(v, scale.Min, scale.Max)), front);
            return depth.At(new SKPoint(x, y), depthRows == 1 ? 0 : j / (float)(depthRows - 1));
        }
        var cells = new List<(SKPoint[] Points, int Band)>();
        var columns = Math.Max(1, n - 1);
        for (var j = depthRows - 2; j >= 0; j--)
        {
            for (var k = 0; k < columns; k++)
            {
                var i = depth.Dx >= 0 ? k : columns - 1 - k;
                if (++_markers > MaxMarkers)
                    break;
                var i1 = Math.Min(i + 1, n - 1);
                if (Node(i, j) is not { } a || Node(i1, j) is not { } b || Node(i1, j + 1) is not { } c || Node(i, j + 1) is not { } d)
                    continue;
                var mean = new[] { grid[j].Values[i], grid[j].Values[i1], grid[j + 1].Values[i1], grid[j + 1].Values[i] }.Average(v => v ?? 0);
                cells.Add(([a, b, c, d], Band(scale, mean)));
            }
        }
        var wireframe = plot.Wireframe;
        var colors = cells.Select(c => c.Band).Distinct().ToDictionary(b => b, b => BandFill(plot, b));
        Shape(canvas =>
        {
            foreach (var (points, band) in cells)
            {
                using var path = new SKPath();
                path.AddPoly(points);
                var (fill, line) = colors[band];
                if (wireframe)
                {
                    StrokePath(canvas, path, line ?? new Pen(fill?.Color ?? SKColors.Gray, 0.75f * _s, null));
                    continue;
                }
                FillPath(canvas, path, fill);
                StrokePath(canvas, path, line ?? (fill is { } f ? new Pen(Shade(f.Color, 0.7f), 0.3f * _s, null) : null));
            }
        });
    }

    /// <summary>
    /// A 3D pie: the slices on an ellipse as flat as the view is tilted (c:rotX), turned by c:rotY, their sides below the
    /// front half and pulled-out slices' cut faces, drawn from the back slice to the front one.
    /// </summary>
    private void Pie3D(ChartPlot plot, SKRect area)
    {
        if (plot.Series.Count == 0)
            return;
        var series = plot.Series[0];
        var view = _chart.View3D ?? new ChartView3D(30, 0);
        var tilt = (float)Math.Clamp(Math.Sin(Math.Abs(view.RotX) * Math.PI / 180), 0.15, 1);
        var thickness = 0.2f * (float)Math.Clamp(view.HeightPercent / 100.0, 0.05, 5) * (float)Math.Sqrt(1 - tilt * tilt);
        var labelled = (series.Labels ?? plot.Labels) is { Any: true, Position: "outEnd" } || series.PointLabels.Values.Any(l => l is { Any: true, Position: "outEnd" });
        var room = labelled ? 0.75f : 0.95f;
        var pulled = series.Explosion.Values.DefaultIfEmpty(0).Max();
        var rx = Math.Min(area.Width / 2, area.Height / (2 * tilt + thickness)) * room / (1 + pulled / 100f);
        if (rx <= 1)
            return;
        var ry = rx * tilt;
        var h = rx * thickness;
        var center = new SKPoint(area.MidX, area.MidY - h / 2);
        var total = series.Values.Sum(v => Math.Abs(v ?? 0));
        if (!(total > 0))
            return;
        var angle = -90 + view.RotY + plot.FirstSliceAngle;
        var slices = new List<(float Start, float Sweep, SKPoint Center, Brush? Fill, Pen? Line, bool Pulled)>();
        for (var i = 0; i < series.Values.Count; i++)
        {
            var v = Math.Abs(series.Values[i] ?? 0);
            if (v <= 0)
                continue;
            var sweep = (float)(v / total * 360);
            var style = series.Points.GetValueOrDefault(i) ?? series.Style;
            var auto = plot.VaryColors ? _chart.Palette.Color(i) : _chart.Palette.Color(series.Index);
            var explosion = series.Explosion.TryGetValue(i, out var own) ? own : series.Explosion.GetValueOrDefault(-1);
            var start = (float)angle;
            var middle = (start + sweep / 2) * Math.PI / 180;
            var shift = rx * explosion / 100f;
            var c = new SKPoint(center.X + (float)Math.Cos(middle) * shift, center.Y + (float)Math.Sin(middle) * shift * tilt);
            slices.Add((start, sweep, c, FillOf(style, auto), PenOf(style.Line, SKColors.White, 0.5f), shift > 0));
            if (LabelsAt(plot, series, i) is { Any: true } options && DataLabel(plot, series, i, series.Values[i] ?? 0, options, v / total) is { } text)
            {
                var distance = (options.Position ?? "bestFit") switch
                {
                    "outEnd" => 1 + (6 * _s + Math.Max(text.Width, text.Height) / 2) / rx,
                    "inEnd" => 0.8f,
                    "ctr" => 0.5f,
                    _ => 0.62f,
                };
                TextCentred(text, c.X + (float)Math.Cos(middle) * rx * distance, c.Y + (float)Math.Sin(middle) * ry * distance);
            }
            angle += sweep;
        }
        // Back to front: the slice whose middle is farthest up the ellipse first.
        var ordered = slices.OrderBy(s => Math.Sin((s.Start + s.Sweep / 2) * Math.PI / 180)).ToList();
        Shape(canvas =>
        {
            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            foreach (var (start, sweep, c, fill, line, cut) in ordered)
            {
                var oval = new SKRect(c.X - rx, c.Y - ry, c.X + rx, c.Y + ry);
                var below = new SKRect(oval.Left, oval.Top + h, oval.Right, oval.Bottom + h);
                var color = fill?.Color ?? SKColors.Transparent;
                SKPoint On(float degrees, float drop = 0)
                {
                    var r = degrees * Math.PI / 180;
                    return new SKPoint(c.X + rx * (float)Math.Cos(r), c.Y + ry * (float)Math.Sin(r) + drop);
                }
                if (cut && h > 0 && sweep < 359.9f)
                {
                    foreach (var edge in new[] { start, start + sweep })
                    {
                        using var face = new SKPath();
                        face.AddPoly([c, On(edge), On(edge, h), new SKPoint(c.X, c.Y + h)]);
                        paint.Color = Shade(color, 0.7f);
                        canvas.DrawPath(face, paint);
                    }
                }
                if (h > 0)
                {
                    // The outer side shows where the slice is on the front half of the ellipse (0° to 180° on the page).
                    foreach (var (from, to) in FrontParts(start, sweep))
                    {
                        using var wall = new SKPath();
                        wall.ArcTo(oval, from, to - from, true);
                        wall.LineTo(On(to, h));
                        wall.ArcTo(below, to, from - to, false);
                        wall.Close();
                        paint.Color = Shade(color, 0.78f);
                        if (color.Alpha > 0)
                            canvas.DrawPath(wall, paint);
                        StrokePath(canvas, wall, line);
                    }
                }
                using var topFace = new SKPath();
                if (sweep >= 359.999f)
                {
                    topFace.AddOval(oval);
                }
                else
                {
                    topFace.MoveTo(c);
                    topFace.ArcTo(oval, start, sweep, false);
                    topFace.Close();
                }
                FillPath(canvas, topFace, fill);
                StrokePath(canvas, topFace, line);
            }
        });
    }

    /// <summary>The parts of the arc from <paramref name="start"/> through <paramref name="sweep"/> degrees on the front half (0° to 180°, and again past 360°).</summary>
    private static List<(float From, float To)> FrontParts(float start, float sweep)
    {
        var parts = new List<(float, float)>();
        var a = start % 360;
        if (a < 0)
            a += 360;
        var b = a + sweep;
        foreach (var (lo, hi) in new[] { (0f, 180f), (360f, 540f) })
        {
            var from = Math.Max(a, lo);
            var to = Math.Min(b, hi);
            if (to - from > 0.05f)
                parts.Add((from, to));
        }
        return parts;
    }
}
