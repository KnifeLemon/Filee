// Visio Geometry sections → outlines of the slide model, in the shape's box (points, y down).
//
// Each Geometry section (matched by index across the shape and its masters, VisioCellChain) is one or more subpaths in
// the shape's local coordinates (inches, y up). Rows: MoveTo, LineTo, ArcTo (a circular arc by its bow), EllipticalArcTo
// (through a control point, the ellipse's axis angle and ratio), Ellipse (centre and two axis ends), RelMoveTo /
// RelLineTo / RelCubBezTo / RelQuadBezTo / RelEllipticalArcTo (as fractions of the shape's width and height), NURBSTo
// (the NURBS() data of cell E, sampled as a polyline), PolylineTo (the POLYLINE() data of cell A), SplineStart /
// SplineKnot (straight to their points). InfiniteLine rows are left out. A section with NoShow is not drawn; NoFill and
// NoLine keep it out of the fill or the line. Closed subpaths (ending where they began) of the sections that fill are
// filled together, even-odd, so a section inside another makes a hole, as Visio draws it; every section with a line is
// stroked on its own.
//
// Values inherited from a master shape are that master's: when the shape has another size than its master, they are
// scaled to it (the master's geometry is almost always proportional to its size).
//
// Hostile input: rows in the whole drawing (each use of a master's geometry counted again) and NURBS / polyline points
// are capped (error.document_too_complex); arcs through nearly collinear points are lines.

using System.Globalization;
using Filee.Engines.Documents;
using Filee.Engines.Slides;
using SkiaSharp;

namespace Filee.Engines.Visio;

/// <summary>Where a shape's local coordinates go in its box, and its size against its masters'.</summary>
/// <param name="Width">The shape's width (inches; may be negative).</param>
/// <param name="Height">The shape's height.</param>
/// <param name="Unit">Points per inch of the drawing (72 × the page's scale).</param>
/// <param name="Scale">For a level of the cell chain (1 = first master), how much larger the shape is than that master.</param>
internal sealed record VisioFrame(double Width, double Height, double Unit, Func<int, (double X, double Y)> Scale)
{
    /// <summary>The box's left edge in local coordinates.</summary>
    public double Left => Math.Min(0, Width);

    /// <summary>The box's top edge in local coordinates (y up).</summary>
    public double Top => Math.Max(0, Height);

    public float BoxWidth => (float)(Math.Abs(Width) * Unit);

    public float BoxHeight => (float)(Math.Abs(Height) * Unit);

    /// <summary>A local point (inches, y up) in the box (points, y down).</summary>
    public SKPoint Box(double x, double y) => new((float)((x - Left) * Unit), (float)((Top - y) * Unit));
}

internal sealed class VisioGeometry
{
    /// <summary>Geometry sections, rows and NURBS / polyline points in one drawing at most.</summary>
    public const long MaxCommands = 2_000_000;

    /// <summary>Points of one NURBS or polyline row at most.</summary>
    public const int MaxSplinePoints = 10_000;

    /// <summary>Line segments one NURBS row is sampled into at most.</summary>
    private const int MaxSamples = 4_096;

    /// <summary>Distance (inches) under which a subpath's end meets its start.</summary>
    private const double Closing = 1e-6;

    private long _commands;

    /// <summary>Counts <paramref name="count"/> more commands against <see cref="MaxCommands"/>.</summary>
    private void Count(long count)
    {
        _commands += count;
        if (_commands > MaxCommands)
            throw DocumentLimits.Exceeded($"The drawing has more than {DocumentLimits.Count(MaxCommands)} geometry rows.");
    }

    /// <summary>The shape's outlines: the fill of its closed subpaths first, then each section's line.</summary>
    public List<SlidePath> Paths(VisioCellChain chain, VisioFrame frame)
    {
        var result = new List<SlidePath>();
        var fill = new SKPath { FillType = SKPathFillType.EvenOdd };
        var indexes = chain.Sheets.SelectMany(s => s.Sections.TryGetValue("Geometry", out var sections) ? sections.Keys : (IEnumerable<int>)[]).Distinct().Order().ToList();
        foreach (var index in indexes)
        {
            var sections = chain.Sheets.Select((sheet, level) => (Section: sheet.Section("Geometry", index), Level: level)).Where(s => s.Section is not null).ToList();
            if (sections[0].Section!.Deleted)
                continue;
            bool Flag(string name) =>
                sections.Select(s => s.Section!.Cells.TryGetValue(name, out var cell) ? VisioValues.Flag((string?)cell.Attribute("V")) : null).FirstOrDefault(v => v is not null) ?? false;
            if (Flag("NoShow"))
                continue;
            var section = new Section(this, frame);
            var rows = sections.SelectMany(s => s.Section!.Rows.Keys).Distinct().Order().ToList();
            Count(rows.Count + 1);
            foreach (var rowIndex in rows)
            {
                var chainRows = sections.Where(s => s.Section!.Rows.ContainsKey(rowIndex)).Select(s => (Row: s.Section!.Rows[rowIndex], s.Level)).ToList();
                if (chainRows[0].Row.Deleted)
                    continue;
                var type = chainRows.Select(r => r.Row.Type).FirstOrDefault(t => t is not null);
                section.Row(type, new RowCells(chainRows, frame));
            }
            section.Finish();
            if (!Flag("NoFill"))
            {
                foreach (var closed in section.Closed)
                    fill.AddPath(closed);
            }
            if (!Flag("NoLine") && !section.Outline.IsEmpty)
                result.Add(new SlidePath(section.Outline, PathFill.None, Stroke: true));
            else
                section.Outline.Dispose();
            foreach (var closed in section.Closed)
                closed.Dispose();
        }
        if (fill.IsEmpty)
            fill.Dispose();
        else
            result.Insert(0, new SlidePath(fill, PathFill.Normal, Stroke: false));
        return result;
    }

    /// <summary>The cells of one geometry row through the shape and its masters, inherited values scaled to the shape.</summary>
    private readonly struct RowCells(List<(VisioRow Row, int Level)> rows, VisioFrame frame)
    {
        /// <summary>The value and the level it came from.</summary>
        private (string Value, int Level)? Find(string name)
        {
            foreach (var (row, level) in rows)
            {
                if (row.Cells.TryGetValue(name, out var cell) && (string?)cell.Attribute("V") is { } value && VisioValues.IsNumber(value))
                    return (value, level);
            }
            return null;
        }

        /// <summary>A plain number (a knot, a weight, an angle, a ratio, a fraction).</summary>
        public double Plain(string name, double fallback = 0) => Find(name) is { } found ? VisioValues.Number(found.Value)!.Value : fallback;

        /// <summary>An x coordinate in local inches.</summary>
        public double X(string name) => Find(name) is { } found ? VisioValues.Number(found.Value)!.Value * (found.Level > 0 ? frame.Scale(found.Level).X : 1) : 0;

        /// <summary>A y coordinate in local inches.</summary>
        public double Y(string name) => Find(name) is { } found ? VisioValues.Number(found.Value)!.Value * (found.Level > 0 ? frame.Scale(found.Level).Y : 1) : 0;

        /// <summary>A length that is neither x nor y (an arc's bow).</summary>
        public double Length(string name)
        {
            if (Find(name) is not { } found)
                return 0;
            var value = VisioValues.Number(found.Value)!.Value;
            if (found.Level <= 0)
                return value;
            var (sx, sy) = frame.Scale(found.Level);
            return value * Math.Sqrt(Math.Abs(sx * sy));
        }

        /// <summary>A formula cell's data (NURBS(...), POLYLINE(...)): the formula, else the value; with the level it came from.</summary>
        public (string Text, int Level)? Data(string name)
        {
            foreach (var (row, level) in rows)
            {
                if (!row.Cells.TryGetValue(name, out var cell))
                    continue;
                var text = (string?)cell.Attribute("F") is { } f && f.Contains('(') ? f : (string?)cell.Attribute("V");
                if (text is not null && text.Contains('('))
                    return (text, level);
            }
            return null;
        }
    }

    /// <summary>One geometry section being built: its outline and its closed subpaths.</summary>
    private sealed class Section(VisioGeometry owner, VisioFrame frame)
    {
        private SKPath? _current;
        private (double X, double Y) _start;
        private (double X, double Y)? _point;

        /// <summary>Every subpath, closed ones closed.</summary>
        public SKPath Outline { get; } = new();

        /// <summary>The closed subpaths, for the fill.</summary>
        public List<SKPath> Closed { get; } = [];

        private SKPoint Box(double x, double y) => frame.Box(x, y);

        private void MoveTo(double x, double y)
        {
            EndSubpath();
            _current = new SKPath();
            _current.MoveTo(Box(x, y));
            _start = (x, y);
            _point = (x, y);
        }

        private void LineTo(double x, double y)
        {
            if (_point is null)
            {
                MoveTo(x, y);
                return;
            }
            _current!.LineTo(Box(x, y));
            _point = (x, y);
        }

        private void CubicTo(double x1, double y1, double x2, double y2, double x, double y)
        {
            _current!.CubicTo(Box(x1, y1), Box(x2, y2), Box(x, y));
            _point = (x, y);
        }

        public void Finish() => EndSubpath();

        private void EndSubpath()
        {
            if (_current is null)
                return;
            var closed = _point is var (x, y) && Math.Abs(x - _start.X) < Closing && Math.Abs(y - _start.Y) < Closing && _current.PointCount > 1;
            if (closed)
            {
                _current.Close();
                Closed.Add(new SKPath(_current));
            }
            Outline.AddPath(_current);
            _current.Dispose();
            _current = null;
            _point = null;
        }

        public void Row(string? type, RowCells cells)
        {
            var (w, h) = (frame.Width, frame.Height);
            switch (type)
            {
                case "MoveTo":
                    MoveTo(cells.X("X"), cells.Y("Y"));
                    break;
                case "RelMoveTo":
                    MoveTo(cells.Plain("X") * w, cells.Plain("Y") * h);
                    break;
                case "LineTo":
                case "SplineStart":
                case "SplineKnot":
                    LineTo(cells.X("X"), cells.Y("Y"));
                    break;
                case "RelLineTo":
                    LineTo(cells.Plain("X") * w, cells.Plain("Y") * h);
                    break;
                case "ArcTo":
                    ArcTo(cells.X("X"), cells.Y("Y"), cells.Length("A"));
                    break;
                case "EllipticalArcTo":
                    EllipticalArcTo(cells.X("X"), cells.Y("Y"), cells.X("A"), cells.Y("B"), cells.Plain("C"), cells.Plain("D", 1));
                    break;
                case "RelEllipticalArcTo":
                    EllipticalArcTo(cells.Plain("X") * w, cells.Plain("Y") * h, cells.Plain("A") * w, cells.Plain("B") * h, cells.Plain("C"), cells.Plain("D", 1));
                    break;
                case "RelCubBezTo":
                    {
                        var (x, y) = (cells.Plain("X") * w, cells.Plain("Y") * h);
                        if (_point is null)
                        {
                            MoveTo(x, y);
                            break;
                        }
                        CubicTo(cells.Plain("A") * w, cells.Plain("B") * h, cells.Plain("C") * w, cells.Plain("D") * h, x, y);
                        break;
                    }
                case "RelQuadBezTo":
                    {
                        var (x, y) = (cells.Plain("X") * w, cells.Plain("Y") * h);
                        if (_point is not var (x0, y0))
                        {
                            MoveTo(x, y);
                            break;
                        }
                        var (qx, qy) = (cells.Plain("A") * w, cells.Plain("B") * h);
                        CubicTo(x0 + 2.0 / 3 * (qx - x0), y0 + 2.0 / 3 * (qy - y0), x + 2.0 / 3 * (qx - x), y + 2.0 / 3 * (qy - y), x, y);
                        break;
                    }
                case "Ellipse":
                    Ellipse(cells.X("X"), cells.Y("Y"), cells.X("A"), cells.Y("B"), cells.X("C"), cells.Y("D"));
                    break;
                case "NURBSTo":
                    Nurbs(cells);
                    break;
                case "PolylineTo":
                    Polyline(cells);
                    break;
            }
        }

        /// <summary>A circular arc to (x, y) whose middle is <paramref name="bow"/> from the chord's (positive: counterclockwise).</summary>
        private void ArcTo(double x, double y, double bow)
        {
            if (_point is not var (x0, y0))
            {
                MoveTo(x, y);
                return;
            }
            var (dx, dy) = (x - x0, y - y0);
            var chord = Math.Sqrt(dx * dx + dy * dy);
            if (chord < 1e-12 || Math.Abs(bow) < 1e-12)
            {
                LineTo(x, y);
                return;
            }
            // Counterclockwise from start to end (y up) bulges to the right of the chord.
            var middle = ((x0 + x) / 2 + dy / chord * bow, (y0 + y) / 2 - dx / chord * bow);
            Through((x0, y0), middle, (x, y), 0, 1);
        }

        /// <summary>An arc of an ellipse (axis at <paramref name="angle"/>, axes in <paramref name="ratio"/>) through (a, b) to (x, y).</summary>
        private void EllipticalArcTo(double x, double y, double a, double b, double angle, double ratio)
        {
            if (_point is not var (x0, y0))
            {
                MoveTo(x, y);
                return;
            }
            if (!(Math.Abs(ratio) > 1e-9) || !double.IsFinite(angle))
            {
                LineTo(x, y);
                return;
            }
            Through((x0, y0), (a, b), (x, y), angle, ratio);
        }

        /// <summary>
        /// The arc from <paramref name="p0"/> through <paramref name="pm"/> to <paramref name="p1"/> on the ellipse that is a
        /// circle once its axis at <paramref name="angle"/> is shrunk by <paramref name="ratio"/>; a line when the points are in a line.
        /// </summary>
        private void Through((double X, double Y) p0, (double X, double Y) pm, (double X, double Y) p1, double angle, double ratio)
        {
            var (cos, sin) = (Math.Cos(angle), Math.Sin(angle));
            (double X, double Y) Into((double X, double Y) p) => ((p.X * cos + p.Y * sin) / ratio, -p.X * sin + p.Y * cos);
            (double X, double Y) Back(double qx, double qy)
            {
                qx *= ratio;
                return (qx * cos - qy * sin, qx * sin + qy * cos);
            }
            var (a, b, c) = (Into(p0), Into(pm), Into(p1));
            var d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
            var span = Math.Max(Math.Abs(c.X - a.X) + Math.Abs(c.Y - a.Y), Math.Abs(b.X - a.X) + Math.Abs(b.Y - a.Y));
            if (Math.Abs(d) < 1e-9 * Math.Max(span * span, 1e-12))
            {
                LineTo(p1.X, p1.Y);
                return;
            }
            double Square((double X, double Y) p) => p.X * p.X + p.Y * p.Y;
            var ux = (Square(a) * (b.Y - c.Y) + Square(b) * (c.Y - a.Y) + Square(c) * (a.Y - b.Y)) / d;
            var uy = (Square(a) * (c.X - b.X) + Square(b) * (a.X - c.X) + Square(c) * (b.X - a.X)) / d;
            var radius = Math.Sqrt(Square((a.X - ux, a.Y - uy)));
            if (!double.IsFinite(radius) || radius > 1e7)
            {
                LineTo(p1.X, p1.Y);
                return;
            }
            var a0 = Math.Atan2(a.Y - uy, a.X - ux);
            static double Turn(double from, double to)
            {
                var delta = (to - from) % (2 * Math.PI);
                return delta < 0 ? delta + 2 * Math.PI : delta;
            }
            var toEnd = Turn(a0, Math.Atan2(c.Y - uy, c.X - ux));
            var toMiddle = Turn(a0, Math.Atan2(b.Y - uy, b.X - ux));
            var sweep = toMiddle <= toEnd ? toEnd : toEnd - 2 * Math.PI;
            var pieces = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 2) - 1e-9));
            var step = sweep / pieces;
            var k = 4.0 / 3 * Math.Tan(step / 4) * radius;
            for (var i = 0; i < pieces; i++)
            {
                var (s, e) = (a0 + step * i, a0 + step * (i + 1));
                var c1 = Back(ux + radius * Math.Cos(s) - k * Math.Sin(s), uy + radius * Math.Sin(s) + k * Math.Cos(s));
                var c2 = Back(ux + radius * Math.Cos(e) + k * Math.Sin(e), uy + radius * Math.Sin(e) - k * Math.Cos(e));
                var end = i == pieces - 1 ? p1 : Back(ux + radius * Math.Cos(e), uy + radius * Math.Sin(e));
                CubicTo(c1.X, c1.Y, c2.X, c2.Y, end.X, end.Y);
            }
        }

        /// <summary>A closed ellipse of its own: centre (x, y), the ends of its two axes (a, b) and (c, d).</summary>
        private void Ellipse(double x, double y, double a, double b, double c, double d)
        {
            EndSubpath();
            var (ux, uy, vx, vy) = (a - x, b - y, c - x, d - y);
            (double X, double Y) At(double cos, double sin) => (x + cos * ux + sin * vx, y + cos * uy + sin * vy);
            const double K = 0.5522847498307936;
            var start = At(1, 0);
            MoveTo(start.X, start.Y);
            (double Cos, double Sin)[] quarters = [(1, 0), (0, 1), (-1, 0), (0, -1), (1, 0)];
            for (var i = 0; i < 4; i++)
            {
                var (c0, s0) = quarters[i];
                var (c1, s1) = quarters[i + 1];
                var p1 = At(c0 - K * s0, s0 + K * c0);
                var p2 = At(c1 + K * s1, s1 - K * c1);
                var p3 = At(c1, s1);
                CubicTo(p1.X, p1.Y, p2.X, p2.Y, p3.X, p3.Y);
            }
            _point = start;
            EndSubpath();
        }

        /// <summary>A polyline: the points of POLYLINE(xType, yType, x1, y1, ...) in cell A, then (X, Y).</summary>
        private void Polyline(RowCells cells)
        {
            var (x, y) = (cells.X("X"), cells.Y("Y"));
            if (_point is null)
            {
                MoveTo(x, y);
                return;
            }
            if (cells.Data("A") is var (text, level) && Arguments(text, "POLYLINE") is { Count: >= 2 } data && data.Count % 2 == 0)
            {
                var points = (data.Count - 2) / 2;
                if (points > MaxSplinePoints)
                    throw DocumentLimits.Exceeded($"A Visio polyline has more than {DocumentLimits.Count(MaxSplinePoints)} points.");
                owner.Count(points);
                var (sx, sy) = level > 0 ? frame.Scale(level) : (1, 1);
                for (var i = 2; i + 1 < data.Count; i += 2)
                    LineTo(data[0] == 0 ? data[i] * frame.Width : data[i] * sx, data[1] == 0 ? data[i + 1] * frame.Height : data[i + 1] * sy);
            }
            LineTo(x, y);
        }

        /// <summary>
        /// A NURBS from the current point (knot C, weight D) through the control points of NURBS(knotLast, degree,
        /// xType, yType, x1, y1, knot1, weight1, ...) in cell E to (X, Y) (knot A, weight B), sampled as a polyline.
        /// </summary>
        private void Nurbs(RowCells cells)
        {
            var (x, y) = (cells.X("X"), cells.Y("Y"));
            if (_point is not var (x0, y0))
            {
                MoveTo(x, y);
                return;
            }
            if (cells.Data("E") is not var (text, level) || Arguments(text, "NURBS") is not { Count: >= 8 } data || (data.Count - 4) % 4 != 0)
            {
                LineTo(x, y);
                return;
            }
            var inner = (data.Count - 4) / 4;
            if (inner + 2 > MaxSplinePoints)
                throw DocumentLimits.Exceeded($"A Visio NURBS has more than {DocumentLimits.Count(MaxSplinePoints)} control points.");
            var degree = (int)Math.Clamp(Math.Round(data[1]), 1, 9);
            var (sx, sy) = level > 0 ? frame.Scale(level) : (1, 1);
            var points = new List<(double X, double Y, double W)>(inner + 2) { (x0, y0, cells.Plain("D", 1)) };
            var knots = new List<double>(inner + 2 + 2 * degree) { cells.Plain("C") };
            for (var i = 0; i < inner; i++)
            {
                var at = 4 + i * 4;
                points.Add((data[2] == 0 ? data[at] * frame.Width : data[at] * sx, data[3] == 0 ? data[at + 1] * frame.Height : data[at + 1] * sy, data[at + 3]));
                knots.Add(data[at + 2]);
            }
            points.Add((x, y, cells.Plain("B", 1)));
            knots.Add(cells.Plain("A"));
            knots.Add(data[0]);
            var samples = Math.Min(MaxSamples, 8 * points.Count);
            owner.Count(samples);
            foreach (var (px, py) in Sample(points, knots, Math.Min(degree, points.Count - 1), samples))
                LineTo(px, py);
            LineTo(x, y);
        }

        /// <summary>The numbers in NAME(a, b, ...); null when the text isn't that call of plain numbers.</summary>
        private static List<double>? Arguments(string text, string name)
        {
            var open = text.IndexOf('(');
            var close = text.LastIndexOf(')');
            if (open < 0 || close < open || !text[..open].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return null;
            var result = new List<double>();
            foreach (var part in text[(open + 1)..close].Split(','))
            {
                if (!double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                    return null;
                result.Add(Math.Clamp(value, -1e7, 1e7));
                if (result.Count > MaxSplinePoints * 4 + 4)
                    throw DocumentLimits.Exceeded($"A Visio NURBS or polyline has more than {DocumentLimits.Count(MaxSplinePoints)} points.");
            }
            return result;
        }

        /// <summary>
        /// Points along a rational B-spline of <paramref name="degree"/>: the knots Visio stores (one per control point
        /// and the last) are clamped at the start and filled up at the end; knots that go backwards give the control polygon.
        /// </summary>
        private static IEnumerable<(double X, double Y)> Sample(List<(double X, double Y, double W)> points, List<double> stored, int degree, int samples)
        {
            var n = points.Count - 1;
            var needed = n + degree + 2;
            var knots = new List<double>(needed);
            var front = stored.TakeWhile(k => k == stored[0]).Count();
            for (var i = front; i < degree + 1; i++)
                knots.Add(stored[0]);
            knots.AddRange(stored);
            while (knots.Count < needed)
                knots.Add(stored[^1]);
            if (knots.Count > needed)
                knots.RemoveRange(needed, knots.Count - needed);
            var monotonic = true;
            for (var i = 1; i < knots.Count; i++)
                monotonic &= knots[i] >= knots[i - 1];
            var (from, to) = (knots[degree], knots[n + 1]);
            if (!monotonic || !(to > from) || points.Any(p => !(p.W > 0)))
            {
                foreach (var point in points.Skip(1))
                    yield return (point.X, point.Y);
                yield break;
            }
            var work = new (double X, double Y, double W)[degree + 1];
            for (var s = 1; s <= samples; s++)
            {
                var t = from + (to - from) * s / samples;
                var span = degree;
                while (span < n && knots[span + 1] <= t)
                    span++;
                for (var j = 0; j <= degree; j++)
                {
                    var p = points[span - degree + j];
                    work[j] = (p.X * p.W, p.Y * p.W, p.W);
                }
                for (var r = 1; r <= degree; r++)
                {
                    for (var j = degree; j >= r; j--)
                    {
                        var i = span - degree + j;
                        var denominator = knots[i + degree + 1 - r] - knots[i];
                        var alpha = denominator > 0 ? (t - knots[i]) / denominator : 0;
                        work[j] = ((1 - alpha) * work[j - 1].X + alpha * work[j].X, (1 - alpha) * work[j - 1].Y + alpha * work[j].Y,
                            (1 - alpha) * work[j - 1].W + alpha * work[j].W);
                    }
                }
                var result = work[degree];
                if (result.W > 0)
                    yield return (result.X / result.W, result.Y / result.W);
            }
        }
    }
}
