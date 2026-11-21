// Office 2016 chart types (read by ChartReader.ChartEx.cs): waterfall, funnel, treemap, sunburst and box and whisker.
//
// Waterfall: each point a column floating from the running total before it to the one after (a subtotal stands on
// the axis), increases, decreases and totals in the first three automatic colours, joined by connector lines.
// Funnel: bars centred on one line, as wide as their value against the largest, the first at the top. Treemap: the
// hierarchy (the category levels, outermost first) as nested rectangles laid out squarified, largest first, each
// top-level branch in its colour, parent labels over their children or in a banner above them. Sunburst: the same
// hierarchy as rings round the middle, the top level innermost. Box and whisker: per category and series the box from
// the first to the third quartile (exclusive or inclusive, as Excel's QUARTILE.EXC and .INC), the median, whiskers to
// the farthest values within 1.5 interquartile ranges, outliers beyond as circles, the mean as a cross (and a line).

using SkiaSharp;

namespace Filee.Engines.Charts;

internal sealed partial class ChartDrawer
{
    /// <summary>The categories and values a waterfall, funnel or box and whisker plot puts on its axes.</summary>
    private static void ExExtents(ChartPlot plot, Axis first, Axis second)
    {
        var series = plot.Series;
        if (series.Count == 0)
            return;
        first.HasBars = true;
        first.Between = true;
        second.Format ??= second.Definition.Format ?? series.FirstOrDefault(s => s.Format is not null)?.Format;
        if (plot.Kind == ChartKind.BoxWhisker)
        {
            var groups = BoxGroups(plot);
            first.Count = Math.Max(first.Count, groups.Labels.Count);
            first.Labels = groups.Labels;
            foreach (var v in series.SelectMany(s => s.Values).OfType<double>())
                second.Include(v);
            return;
        }
        var only = series[0];
        var count = Math.Min(only.Values.Count, ChartReader.MaxPoints);
        first.Count = Math.Max(first.Count, count);
        if (first.Labels.Count < count)
            first.Labels = [.. Enumerable.Range(0, count).Select(i => Category(series, i))];
        second.Include(0);
        if (plot.Kind == ChartKind.Funnel)
        {
            second.Include(only.Values.OfType<double>().Select(Math.Abs).DefaultIfEmpty(1).Max());
            return;
        }
        foreach (var (from, to, _) in Steps(plot, only))
        {
            second.Include(from);
            second.Include(to);
        }
    }

    /// <summary>A waterfall's columns: from and to (running totals) and kind (0 increase, 1 decrease, 2 total), by point.</summary>
    private static List<(double From, double To, int Kind)> Steps(ChartPlot plot, ChartSeries series)
    {
        var steps = new List<(double, double, int)>(series.Values.Count);
        var running = 0.0;
        for (var i = 0; i < series.Values.Count; i++)
        {
            if (series.Values[i] is not { } v)
            {
                steps.Add((running, running, -1));
                continue;
            }
            if (plot.Subtotals.Contains(i))
            {
                steps.Add((0, v, 2));
                running = v;
                continue;
            }
            var to = Math.Clamp(running + v, -ChartReader.MaxValue, ChartReader.MaxValue);
            steps.Add((running, to, v >= 0 ? 0 : 1));
            running = to;
        }
        return steps;
    }

    /// <summary>Legend entries of their own: a waterfall's kinds of column, a treemap's or sunburst's branches, a surface's bands; null for the usual ones.</summary>
    private List<LegendEntry>? SpecialEntries(ChartPlot plot)
    {
        switch (plot.Kind)
        {
            case ChartKind.Waterfall:
                return [.. new[] { "Increase", "Decrease", "Total" }.Select((name, i) => new LegendEntry(name, KeyKind.Box, new Brush(_chart.Palette.Color(i)), null, null, _chart.Palette.Color(i)))];
            case ChartKind.Treemap or ChartKind.Sunburst:
                var root = Hierarchy(plot.Series[0]);
                return [.. root.Children.Take(MaxLabels).Select((node, i) => new LegendEntry(node.Label, KeyKind.Box, BranchFill(plot.Series[0], node, i), null, null, _chart.Palette.Color(i)))];
            case ChartKind.Surface:
                var definition = _chart.Axis(plot.AxisIds.ElementAtOrDefault(1));
                if (SurfaceScale(plot, definition) is not { } scale)
                    return [];
                var format = definition?.Format ?? plot.Series.FirstOrDefault(s => s.Format is not null)?.Format;
                return [.. Enumerable.Range(0, Bands(scale)).Select(b =>
                {
                    var (fill, line) = BandFill(plot, b);
                    var text = Format(scale.Min + b * scale.Unit, format) + "-" + Format(scale.Min + (b + 1) * scale.Unit, format);
                    return new LegendEntry(text, KeyKind.Box, fill, line, null, fill?.Color ?? SKColors.Gray);
                })];
            default:
                return null;
        }
    }

    // ───────────────────────── Waterfall ─────────────────────────

    private void Waterfall(ChartPlot plot, Axis category, Axis value, SKRect inner)
    {
        if (value.Scale is not { } scale || plot.Series.Count == 0 || category.Count == 0)
            return;
        var series = plot.Series[0];
        var steps = Steps(plot, series);
        var gap = plot.GapWidth / 100.0;
        var width = 1 / (1 + gap);
        var columns = new List<(SKRect Rect, Brush? Fill, Pen? Line)>();
        var connectors = new List<(SKPoint, SKPoint)>();
        SKRect? previous = null;
        var previousEnd = 0f;
        for (var i = 0; i < Math.Min(steps.Count, category.Count); i++)
        {
            var (from, to, kind) = steps[i];
            if (kind < 0)
                continue;
            var a = category.Edge(i);
            var b = category.Edge(i + 1);
            var c0 = a + (b - a) * gap * width / 2;
            var c1 = c0 + (b - a) * width;
            var (v0, v1) = (scale.Fraction(from), scale.Fraction(to));
            var rect = category.Horizontal
                ? new SKRect(Along(category, Math.Min(c0, c1), inner), Along(value, Math.Max(v0, v1), inner), Along(category, Math.Max(c0, c1), inner), Along(value, Math.Min(v0, v1), inner))
                : new SKRect(Along(value, Math.Min(v0, v1), inner), Along(category, Math.Max(c0, c1), inner), Along(value, Math.Max(v0, v1), inner), Along(category, Math.Min(c0, c1), inner));
            var style = series.Points.GetValueOrDefault(i) ?? ChartShapeStyle.Auto;
            columns.Add((rect, FillOf(style, _chart.Palette.Color(kind)), PenOf(style.Line, null, 0.75f)));
            if (plot.ConnectorLines && previous is { } last)
            {
                connectors.Add(category.Horizontal
                    ? (new SKPoint(last.Right, previousEnd), new SKPoint(rect.Left, previousEnd))
                    : (new SKPoint(previousEnd, last.Bottom), new SKPoint(previousEnd, rect.Top)));
            }
            previous = rect;
            previousEnd = Along(value, v1, inner);
            if (LabelsAt(plot, series, i) is { Any: true } options && series.Values[i] is { } v && DataLabel(plot, series, i, kind == 2 ? to : v, options) is { } text)
            {
                var pad = 3 * _s;
                var up = to >= from;
                if (category.Horizontal)
                    TextCentred(text, rect.MidX, options.Position is "ctr" or "inEnd" or "inBase" ? rect.MidY : up ? rect.Top - pad - text.Height / 2 : rect.Bottom + pad + text.Height / 2);
                else
                    TextCentred(text, up ? rect.Right + pad + text.Width / 2 : rect.Left - pad - text.Width / 2, rect.MidY);
            }
        }
        var connector = PenOf(null, new SKColor(0x80, 0x80, 0x80), 0.75f);
        Clipped(inner, canvas =>
        {
            foreach (var (rect, fill, line) in columns)
            {
                using var path = new SKPath();
                path.AddRect(rect);
                FillPath(canvas, path, fill);
                StrokePath(canvas, path, line);
            }
            using var lines = new SKPath();
            foreach (var (from, to) in connectors)
            {
                lines.MoveTo(from);
                lines.LineTo(to);
            }
            StrokePath(canvas, lines, connector);
        });
    }

    // ───────────────────────── Funnel ─────────────────────────

    private void Funnel(ChartPlot plot, Axis category, SKRect inner)
    {
        if (plot.Series.Count == 0 || category.Count == 0)
            return;
        var series = plot.Series[0];
        var largest = series.Values.OfType<double>().Select(Math.Abs).DefaultIfEmpty(0).Max();
        if (!(largest > 0))
            return;
        var share = 1 / (1 + plot.GapWidth / 100.0);
        var bars = new List<(SKRect Rect, Brush? Fill, Pen? Line)>();
        for (var i = 0; i < Math.Min(series.Values.Count, category.Count); i++)
        {
            if (series.Values[i] is not { } v)
                continue;
            var (a, b) = (category.Edge(i), category.Edge(i + 1));
            var middle = (a + b) / 2;
            var half = (b - a) * share / 2;
            var (y0, y1) = (Along(category, middle - half, inner), Along(category, middle + half, inner));
            var width = (float)(Math.Abs(v) / largest) * inner.Width;
            var rect = new SKRect(inner.MidX - width / 2, Math.Min(y0, y1), inner.MidX + width / 2, Math.Max(y0, y1));
            var style = series.Points.GetValueOrDefault(i) ?? series.Style;
            bars.Add((rect, FillOf(style, SeriesColor(plot, series, i)), PenOf(style.Line, null, 0.75f)));
            if (LabelsAt(plot, series, i) is { Any: true } options && DataLabel(plot, series, i, v, options) is { } text)
                TextCentred(text, rect.MidX, rect.MidY);
        }
        Clipped(inner, canvas =>
        {
            foreach (var (rect, fill, line) in bars)
            {
                using var path = new SKPath();
                path.AddRect(rect);
                FillPath(canvas, path, fill);
                StrokePath(canvas, path, line);
            }
        });
    }

    // ───────────────────────── Treemap and sunburst ─────────────────────────

    /// <summary>A node of a hierarchy: its label, value (its own and its children's), point (a leaf's) and children, largest first.</summary>
    private sealed class Node(string label)
    {
        public string Label { get; } = label;
        public double Value { get; set; }
        public int Point { get; set; } = -1;
        public List<Node> Children { get; } = [];
        public Dictionary<string, Node> ByLabel { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// The hierarchy of a treemap or sunburst series: each point's path from its outermost category level to its own
    /// label (blank outer labels take the one above; a path ends at its last label), values added up the tree.
    /// </summary>
    private static Node Hierarchy(ChartSeries series)
    {
        var root = new Node("");
        var levels = new List<IReadOnlyList<string?>>();
        if (series.CategoryLevels is { } outer)
            levels.AddRange(outer.Reverse());
        levels.Add(series.Categories ?? []);
        var last = new string?[levels.Count];
        for (var i = 0; i < series.Values.Count; i++)
        {
            if (series.Values[i] is not { } v || !(v > 0))
                continue;
            var path = new List<string>(levels.Count);
            for (var l = 0; l < levels.Count; l++)
            {
                var label = i < levels[l].Count ? levels[l][i] : null;
                if (string.IsNullOrEmpty(label) && l < levels.Count - 1)
                    label = last[l];
                last[l] = label;
                path.Add(label ?? "");
            }
            while (path.Count > 1 && path[^1].Length == 0)
                path.RemoveAt(path.Count - 1);
            var node = root;
            foreach (var label in path)
            {
                if (!node.ByLabel.TryGetValue(label, out var child))
                {
                    child = new Node(label);
                    node.ByLabel[label] = child;
                    node.Children.Add(child);
                }
                node = child;
            }
            node.Point = node.Point < 0 ? i : node.Point;
            node.Value += v;
        }
        Total(root);
        return root;

        static double Total(Node node)
        {
            foreach (var child in node.Children)
                node.Value += Total(child);
            node.Children.Sort((a, b) => b.Value.CompareTo(a.Value));
            return node.Value;
        }
    }

    /// <summary>A branch's fill: its point's own (c:dataPt), else the automatic colour of its place.</summary>
    private Brush? BranchFill(ChartSeries series, Node node, int branch)
    {
        var style = node.Point >= 0 ? series.Points.GetValueOrDefault(node.Point) : null;
        return FillOf(style ?? ChartShapeStyle.Auto, _chart.Palette.Color(branch));
    }

    /// <summary>Text that reads on <paramref name="fill"/>: white on dark colours, black on light ones.</summary>
    private static ChartTextStyle On(ChartTextStyle style, Brush? fill)
    {
        if (fill is not { } brush || style.Color != SKColors.Black && style.Color != new SKColor(0x59, 0x59, 0x59))
            return style;
        var c = brush.Color;
        var luminance = (0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue) / 255;
        return style with { Color = luminance < 0.6 ? SKColors.White : SKColors.Black };
    }

    private void Treemap(ChartPlot plot, SKRect area)
    {
        if (plot.Series.Count == 0)
            return;
        var series = plot.Series[0];
        var root = Hierarchy(series);
        if (!(root.Value > 0))
            return;
        var options = series.Labels ?? plot.Labels ?? new ChartLabels(false, true, false, false, null, null, _chart.Text);
        var rects = new List<(SKRect Rect, Brush? Fill)>();
        var texts = new List<(TextBlock Text, SKRect Rect, bool Parent)>();
        var parentStyle = options.Style with { Bold = true, Size = options.Style.Size * 1.2f };
        var bannerHeight = Text("Ag", parentStyle).Height + 4 * _s;
        void Lay(Node node, SKRect rect, int branch, int depth)
        {
            if (rects.Count >= MaxMarkers)
                return;
            var fill = BranchFill(series, node, branch);
            if (node.Children.Count == 0)
            {
                rects.Add((rect, fill));
                var parts = new List<string>(2);
                if (options.Category)
                    parts.Add(node.Label);
                if (options.Value)
                    parts.Add(Format(node.Value, options.Format ?? series.Format));
                if (parts.Count > 0 && _labels++ < MaxLabels)
                    texts.Add((Block([.. parts.Select(p => Text(p, On(options.Style, fill)))]), rect, false));
                return;
            }
            var inside = rect;
            if (plot.ParentLabels == "banner" && depth >= 0 && rect.Height > bannerHeight * 2)
            {
                rects.Add((new SKRect(rect.Left, rect.Top, rect.Right, rect.Top + bannerHeight), fill));
                texts.Add((Block(Text(node.Label, On(parentStyle, fill))), new SKRect(rect.Left, rect.Top, rect.Right, rect.Top + bannerHeight), true));
                inside = new SKRect(rect.Left, rect.Top + bannerHeight, rect.Right, rect.Bottom);
            }
            foreach (var (child, place, index) in Squarify(node.Children, inside))
                Lay(child, place, depth < 0 ? index : branch, depth + 1);
            if (plot.ParentLabels == "overlapping" && depth >= 0)
                texts.Add((Block(Text(node.Label, On(parentStyle, fill))), rect, true));
        }
        Lay(root, SKRect.Inflate(area, -2 * _s, -2 * _s), 0, -1);
        var border = new Pen(SKColors.White, 1.25f * _s, null);
        Shape(canvas =>
        {
            foreach (var (rect, fill) in rects)
            {
                using var path = new SKPath();
                path.AddRect(rect);
                FillPath(canvas, path, fill);
                StrokePath(canvas, path, border);
            }
        });
        var pad = 3 * _s;
        // Labels from the rectangle's top-left corner, only where they fit; a leaf's below a parent's label it would meet.
        var parents = texts.Where(t => t.Parent).Select(t => SKRect.Create(t.Rect.Left + pad, t.Rect.Top + pad / 2, t.Text.Width, t.Text.Height)).ToList();
        foreach (var (text, rect, parent) in texts)
        {
            var top = rect.Top + pad / 2;
            if (!parent)
            {
                foreach (var label in parents)
                {
                    if (label.IntersectsWith(SKRect.Create(rect.Left + pad, top, text.Width, text.Height)))
                        top = label.Bottom;
                }
            }
            if (text.Width <= rect.Width - 2 * pad && top + text.Height <= rect.Bottom - pad / 2)
                BlockAt(text, rect.Left + pad, top, centred: false);
        }
    }

    /// <summary>
    /// Rectangles for <paramref name="nodes"/> (largest first) filling <paramref name="box"/> by their values, laid out
    /// in rows along the box's shorter side, each row kept while it makes its rectangles squarer; with each node's place
    /// in the list. A row's sum and its largest and smallest values are kept as it grows, so the layout is linear.
    /// </summary>
    private static List<(Node Node, SKRect Rect, int Index)> Squarify(List<Node> nodes, SKRect box)
    {
        var placed = new List<(Node, SKRect, int)>(nodes.Count);
        var total = nodes.Sum(n => Math.Max(n.Value, 0));
        if (!(total > 0) || box.Width <= 0 || box.Height <= 0)
            return placed;
        var scale = box.Width * box.Height / total;
        var rest = box;
        var row = new List<(Node Node, int Index)>();
        double sum = 0, largest = 0, smallest = 0;
        static double Worst(double sum, double largest, double smallest, double side) =>
            Math.Max(side * side * largest / (sum * sum), sum * sum / (side * side * Math.Max(smallest, 1e-12)));
        void Flush()
        {
            if (row.Count == 0)
                return;
            var wide = rest.Width >= rest.Height;
            var thickness = (float)(sum / (wide ? rest.Height : rest.Width));
            var offset = 0f;
            foreach (var (node, index) in row)
            {
                var length = (float)(node.Value * scale / thickness);
                placed.Add((node, wide
                    ? new SKRect(rest.Left, rest.Top + offset, rest.Left + thickness, rest.Top + offset + length)
                    : new SKRect(rest.Left + offset, rest.Top, rest.Left + offset + length, rest.Top + thickness), index));
                offset += length;
            }
            rest = wide ? new SKRect(rest.Left + thickness, rest.Top, rest.Right, rest.Bottom) : new SKRect(rest.Left, rest.Top + thickness, rest.Right, rest.Bottom);
            row.Clear();
            sum = 0;
        }
        for (var i = 0; i < nodes.Count; i++)
        {
            var area = nodes[i].Value * scale;
            if (!(area > 0))
                continue;
            var side = Math.Min(rest.Width, rest.Height);
            if (row.Count > 0 && Worst(sum + area, largest, area, side) > Worst(sum, largest, smallest, side))
                Flush();
            if (row.Count == 0)
                largest = area;
            row.Add((nodes[i], i));
            sum += area;
            smallest = area;
        }
        Flush();
        return placed;
    }

    private void Sunburst(ChartPlot plot, SKRect area)
    {
        if (plot.Series.Count == 0)
            return;
        var series = plot.Series[0];
        var root = Hierarchy(series);
        if (!(root.Value > 0))
            return;
        static int Levels(Node node) => node.Children.Count == 0 ? 0 : 1 + node.Children.Max(Levels);
        var levels = Math.Max(1, Levels(root));
        var radius = Math.Min(area.Width, area.Height) / 2 * 0.95f;
        var ring = radius / (levels + 0.5f);
        var hole = ring / 2;
        var center = new SKPoint(area.MidX, area.MidY);
        var options = series.Labels ?? plot.Labels ?? new ChartLabels(false, true, false, false, null, null, _chart.Text);
        var segments = new List<(float Inner, float Outer, float Start, float Sweep, Brush? Fill)>();
        void Lay(Node node, float start, float sweep, int level, int branch)
        {
            var angle = start;
            for (var k = 0; k < node.Children.Count && segments.Count < MaxMarkers; k++)
            {
                var child = node.Children[k];
                var share = (float)(child.Value / node.Value * sweep);
                var own = level == 0 ? k : branch;
                var fill = BranchFill(series, child, own);
                var (r0, r1) = (hole + level * ring, hole + (level + 1) * ring);
                segments.Add((r0, r1, angle, share, fill));
                var middle = (angle + share / 2) * Math.PI / 180;
                var mid = (r0 + r1) / 2;
                if (options.Category || options.Value)
                {
                    var parts = new List<string>(2);
                    if (options.Category)
                        parts.Add(child.Label);
                    if (options.Value)
                        parts.Add(Format(child.Value, options.Format ?? series.Format));
                    if (_labels++ < MaxLabels)
                    {
                        var text = Block([.. parts.Select(p => Text(p, On(options.Style, fill)))]);
                        var arc = mid * share * Math.PI / 180;
                        if (text.Width <= Math.Min(arc, 2 * mid) * 0.9 && text.Height <= ring * 0.9 || text.Height <= arc * 0.9 && text.Width <= ring * 0.9)
                            TextCentred(text, center.X + (float)Math.Cos(middle) * mid, center.Y + (float)Math.Sin(middle) * mid);
                    }
                }
                Lay(child, angle, share, level + 1, own);
                angle += share;
            }
        }
        Lay(root, -90, 360, 0, 0);
        var border = new Pen(SKColors.White, 1.25f * _s, null);
        Shape(canvas =>
        {
            foreach (var (r0, r1, start, sweep, fill) in segments)
            {
                using var path = new SKPath();
                var outer = new SKRect(center.X - r1, center.Y - r1, center.X + r1, center.Y + r1);
                var inner = new SKRect(center.X - r0, center.Y - r0, center.X + r0, center.Y + r0);
                var s = Math.Min(sweep, 359.99f);
                path.ArcTo(outer, start, s, true);
                path.ArcTo(inner, start + s, -s, false);
                path.Close();
                FillPath(canvas, path, fill);
                StrokePath(canvas, path, border);
            }
        });
    }

    // ───────────────────────── Box and whisker ─────────────────────────

    /// <summary>A box and whisker plot's categories (in the order they first appear) and, per series, each category's values.</summary>
    private static (List<string> Labels, List<List<List<double>>> Values) BoxGroups(ChartPlot plot)
    {
        var labels = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var values = new List<List<List<double>>>();
        foreach (var series in plot.Series)
        {
            var groups = new List<List<double>>();
            for (var i = 0; i < series.Values.Count; i++)
            {
                if (series.Values[i] is not { } v)
                    continue;
                var label = series.Categories is { } c && i < c.Count ? c[i] ?? "" : "";
                if (!index.TryGetValue(label, out var g))
                {
                    index[label] = g = labels.Count;
                    labels.Add(label);
                }
                while (groups.Count <= g)
                    groups.Add([]);
                groups[g].Add(v);
            }
            values.Add(groups);
        }
        return (labels, values);
    }

    /// <summary>The <paramref name="p"/> quantile of sorted values: Excel's QUARTILE.EXC (between the points' n + 1 places) or QUARTILE.INC.</summary>
    internal static double Quantile(IReadOnlyList<double> sorted, double p, bool exclusive)
    {
        var n = sorted.Count;
        if (n == 0)
            return double.NaN;
        if (n == 1)
            return sorted[0];
        var h = exclusive ? (n + 1) * p - 1 : (n - 1) * p;
        h = Math.Clamp(h, 0, n - 1);
        var low = (int)Math.Floor(h);
        var high = Math.Min(low + 1, n - 1);
        return sorted[low] + (h - low) * (sorted[high] - sorted[low]);
    }

    private void Boxes(ChartPlot plot, Axis category, Axis value, SKRect inner)
    {
        if (value.Scale is not { } scale || plot.Series.Count == 0)
            return;
        var (labels, groups) = BoxGroups(plot);
        var m = plot.Series.Count;
        var gap = plot.GapWidth / 100.0;
        var width = 1 / (m + gap);
        var shapes = new List<Action<SKCanvas>>();
        for (var k = 0; k < m; k++)
        {
            var series = plot.Series[k];
            var color = series.Style.Fill is { Alpha: > 0 } own ? own : _chart.Palette.Color(series.Index);
            var fill = FillOf(series.Style, color);
            var edge = PenOf(series.Style.Line, Shade(color, 0.6f), 0.75f);
            var means = new List<SKPoint>();
            for (var g = 0; g < Math.Min(labels.Count, groups[k].Count); g++)
            {
                var sorted = groups[k][g].Order().ToList();
                if (sorted.Count == 0 || ++_markers > MaxMarkers)
                    continue;
                var (q1, median, q3) = (Quantile(sorted, 0.25, plot.ExclusiveQuartiles), Quantile(sorted, 0.5, plot.ExclusiveQuartiles), Quantile(sorted, 0.75, plot.ExclusiveQuartiles));
                var iqr = q3 - q1;
                var inside = sorted.Where(v => v >= q1 - 1.5 * iqr && v <= q3 + 1.5 * iqr).ToList();
                var (low, high) = inside.Count > 0 ? (inside[0], inside[^1]) : (q1, q3);
                var mean = sorted.Average();
                var a = category.Edge(g);
                var b = category.Edge(g + 1);
                var c0 = a + (b - a) * (gap * width / 2 + k * width);
                var c1 = c0 + (b - a) * width;
                var (x0, x1) = (Along(category, Math.Min(c0, c1), inner), Along(category, Math.Max(c0, c1), inner));
                if (!category.Horizontal)
                    (x0, x1) = (Math.Min(x0, x1), Math.Max(x0, x1));
                float Y(double v) => Along(value, scale.Fraction(v), inner);
                SKPoint P(float across, float along) => category.Horizontal ? new SKPoint(across, along) : new SKPoint(along, across);
                var mid = (x0 + x1) / 2;
                var capHalf = (x1 - x0) / 4;
                var box = category.Horizontal
                    ? new SKRect(x0, Math.Min(Y(q1), Y(q3)), x1, Math.Max(Y(q1), Y(q3)))
                    : new SKRect(Math.Min(Y(q1), Y(q3)), x0, Math.Max(Y(q1), Y(q3)), x1);
                var outliers = plot.Outliers ? sorted.Where(v => v < low || v > high).Select(v => P(mid, Y(v))).ToList() : [];
                var points = plot.InnerPoints ? inside.Select(v => P(mid, Y(v))).ToList() : [];
                // Points drawn count against the chart's markers.
                if ((_markers += outliers.Count + points.Count) > MaxMarkers)
                    (outliers, points) = ([], []);
                var meanAt = P(mid, Y(mean));
                means.Add(meanAt);
                var showMean = plot.MeanMarker;
                shapes.Add(canvas =>
                {
                    using var whiskers = new SKPath();
                    whiskers.MoveTo(P(mid, Y(q3)));
                    whiskers.LineTo(P(mid, Y(high)));
                    whiskers.MoveTo(P(mid - capHalf, Y(high)));
                    whiskers.LineTo(P(mid + capHalf, Y(high)));
                    whiskers.MoveTo(P(mid, Y(q1)));
                    whiskers.LineTo(P(mid, Y(low)));
                    whiskers.MoveTo(P(mid - capHalf, Y(low)));
                    whiskers.LineTo(P(mid + capHalf, Y(low)));
                    StrokePath(canvas, whiskers, edge);
                    using var rect = new SKPath();
                    rect.AddRect(box);
                    FillPath(canvas, rect, fill);
                    StrokePath(canvas, rect, edge);
                    using var middle = new SKPath();
                    middle.MoveTo(P(x0, Y(median)));
                    middle.LineTo(P(x1, Y(median)));
                    StrokePath(canvas, middle, edge is { } e ? e with { Width = e.Width * 1.5f } : null);
                    var dot = 2.5f * _s;
                    foreach (var o in outliers.Concat(points))
                    {
                        using var circle = new SKPath();
                        circle.AddCircle(o.X, o.Y, dot);
                        StrokePath(canvas, circle, edge);
                    }
                    if (showMean)
                    {
                        using var cross = new SKPath();
                        cross.MoveTo(meanAt.X - dot, meanAt.Y - dot);
                        cross.LineTo(meanAt.X + dot, meanAt.Y + dot);
                        cross.MoveTo(meanAt.X + dot, meanAt.Y - dot);
                        cross.LineTo(meanAt.X - dot, meanAt.Y + dot);
                        StrokePath(canvas, cross, new Pen(Shade(color, 0.45f), 1.25f * _s, null));
                    }
                });
            }
            if (plot.MeanLine && means.Count > 1)
            {
                var line = means.ToArray();
                shapes.Add(canvas =>
                {
                    using var path = Polyline(line, smooth: false);
                    StrokePath(canvas, path, edge);
                });
            }
        }
        Clipped(inner, canvas =>
        {
            foreach (var shape in shapes)
                shape(canvas);
        });
    }
}
