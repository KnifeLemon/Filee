// Geometry of XPS paths and clips: the abbreviated syntax ("F1 M 0,0 L 10,0 A 5,5 0 0 1 20,0 Z", XPS 1.0 §5.2 /
// ECMA-388 §15.2.2) and PathGeometry elements with PathFigure and its segments. A figure that isn't filled
// (IsFilled="false") is only stroked, a segment that isn't stroked (IsStroked="false") only filled, so a geometry is
// two Skia paths: what is filled and what is stroked (the same one when everything is both).
//
// Data that breaks the syntax ends the geometry where it breaks: what was read before is drawn, as viewers do.
// Points count against the document's budget, and one path's data is limited in length (XpsLimits).

using System.Globalization;
using System.Xml.Linq;
using SkiaSharp;

namespace Filee.Engines.Xps;

/// <summary>A geometry: the path that is filled and the path that is stroked, in the element's coordinates.</summary>
internal sealed class XpsGeometry(SKPath fill, SKPath stroke) : IDisposable
{
    public SKPath Fill { get; } = fill;

    public SKPath Stroke { get; } = stroke;

    public void Dispose()
    {
        Fill.Dispose();
        if (!ReferenceEquals(Stroke, Fill))
            Stroke.Dispose();
    }

    /// <summary>The geometry of an abbreviated path string ("F1 M 0,0 L …").</summary>
    public static XpsGeometry Parse(string data, XpsBudget budget, SKMatrix? transform = null)
    {
        if (data.Length > budget.Limits.MaxPathData)
            throw Documents.DocumentLimits.TooLarge($"A path's data is longer than {Documents.DocumentLimits.Count(budget.Limits.MaxPathData)} characters.");
        var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        new Abbreviated(data, path, budget).Read();
        if (transform is { IsIdentity: false } matrix)
            path.Transform(matrix);
        return new XpsGeometry(path, path);
    }

    /// <summary>The geometry of a PathGeometry element (its namespace's PathFigure children and Figures attribute).</summary>
    public static XpsGeometry FromElement(XElement element, XpsBudget budget, SKMatrix? transform)
    {
        var ns = element.Name.Namespace;
        var nonZero = string.Equals((string?)element.Attribute("FillRule"), "NonZero", StringComparison.OrdinalIgnoreCase);
        var fill = new SKPath { FillType = nonZero ? SKPathFillType.Winding : SKPathFillType.EvenOdd };
        var stroke = new SKPath { FillType = fill.FillType };
        var split = false;
        if ((string?)element.Attribute("Figures") is { Length: > 0 } figures)
        {
            if (figures.Length > budget.Limits.MaxPathData)
                throw Documents.DocumentLimits.TooLarge($"A path's data is longer than {Documents.DocumentLimits.Count(budget.Limits.MaxPathData)} characters.");
            new Abbreviated(figures, fill, budget, fillRule: false).Read();
            stroke.AddPath(fill);
        }
        foreach (var figure in element.Elements(ns + "PathFigure"))
            split |= Figure(figure, fill, stroke, budget);
        if (transform is { IsIdentity: false } matrix)
        {
            fill.Transform(matrix);
            stroke.Transform(matrix);
        }
        if (split)
            return new XpsGeometry(fill, stroke);
        stroke.Dispose();
        return new XpsGeometry(fill, fill);
    }

    /// <summary>Adds a PathFigure; true when its fill and stroke differ.</summary>
    private static bool Figure(XElement figure, SKPath fill, SKPath stroke, XpsBudget budget)
    {
        if (XpsValues.Point((string?)figure.Attribute("StartPoint")) is not { } start)
            return false;
        var filled = XpsValues.Bool((string?)figure.Attribute("IsFilled"), true);
        var closed = XpsValues.Bool((string?)figure.Attribute("IsClosed"), false);
        var split = !filled;
        using var own = new SKPath();
        using var stroked = new SKPath();
        own.MoveTo(start);
        stroked.MoveTo(start);
        var current = start;
        budget.Points(1);
        foreach (var segment in figure.Elements())
        {
            var isStroked = XpsValues.Bool((string?)segment.Attribute("IsStroked"), true);
            split |= !isStroked;
            var from = current;
            Action<SKPath>? apply = null;
            switch (segment.Name.LocalName)
            {
                case "PolyLineSegment":
                    {
                        var points = Points(segment, budget);
                        if (points.Count > 0)
                            current = points[^1];
                        apply = path =>
                        {
                            foreach (var point in points)
                                path.LineTo(point);
                        };
                        break;
                    }
                case "PolyBezierSegment":
                    {
                        var points = Points(segment, budget);
                        var count = points.Count / 3 * 3;
                        if (count > 0)
                            current = points[count - 1];
                        apply = path =>
                        {
                            for (var i = 0; i + 2 < count; i += 3)
                                path.CubicTo(points[i], points[i + 1], points[i + 2]);
                        };
                        break;
                    }
                case "PolyQuadraticBezierSegment":
                    {
                        var points = Points(segment, budget);
                        var count = points.Count / 2 * 2;
                        if (count > 0)
                            current = points[count - 1];
                        apply = path =>
                        {
                            for (var i = 0; i + 1 < count; i += 2)
                                path.QuadTo(points[i], points[i + 1]);
                        };
                        break;
                    }
                case "ArcSegment":
                    if (XpsValues.Point((string?)segment.Attribute("Point")) is { } end && XpsValues.Point((string?)segment.Attribute("Size")) is { } size)
                    {
                        budget.Points(4);
                        var large = XpsValues.Bool((string?)segment.Attribute("IsLargeArc"), false);
                        var clockwise = string.Equals((string?)segment.Attribute("SweepDirection"), "Clockwise", StringComparison.OrdinalIgnoreCase);
                        var rotation = XpsValues.Number((string?)segment.Attribute("RotationAngle")) ?? 0;
                        current = end;
                        apply = path => Arc(path, from, size, rotation, large, clockwise, end);
                    }
                    break;
            }
            if (apply is null)
                continue;
            apply(own);
            // The stroke takes the segment, or moves past it.
            if (isStroked)
                apply(stroked);
            else
                stroked.MoveTo(current);
        }
        if (closed)
        {
            own.Close();
            stroked.Close();
        }
        if (filled)
            fill.AddPath(own);
        stroke.AddPath(stroked);
        return split;
    }

    private static List<SKPoint> Points(XElement segment, XpsBudget budget)
    {
        var points = XpsValues.Points((string?)segment.Attribute("Points"));
        budget.Points(points.Count);
        return points;
    }

    /// <summary>An elliptical arc as SVG and XPS define it; radii of 0 make a line.</summary>
    internal static void Arc(SKPath path, SKPoint from, SKPoint size, float rotation, bool large, bool clockwise, SKPoint to)
    {
        if (from == to)
            return;
        var rx = Math.Abs(size.X);
        var ry = Math.Abs(size.Y);
        if (rx == 0 || ry == 0)
        {
            path.LineTo(to);
            return;
        }
        path.ArcTo(new SKPoint(rx, ry), rotation, large ? SKPathArcSize.Large : SKPathArcSize.Small,
            clockwise ? SKPathDirection.Clockwise : SKPathDirection.CounterClockwise, to);
    }

    /// <summary>Reads the abbreviated syntax into a path.</summary>
    private sealed class Abbreviated(string text, SKPath path, XpsBudget budget, bool fillRule = true)
    {
        private int _at;
        private SKPoint _current;
        private SKPoint _start;
        private SKPoint? _lastControl;
        private bool _open;

        public void Read()
        {
            var command = '\0';
            while (true)
            {
                SkipSpace();
                if (_at >= text.Length)
                    return;
                var c = text[_at];
                if (char.IsAsciiLetter(c))
                {
                    command = c;
                    _at++;
                    if (!Command(command, first: true))
                        return;
                    continue;
                }
                // More numbers repeat the command (after M they are lines).
                if (command is '\0' or 'Z' or 'z' or 'F' or 'f')
                    return;
                if (command is 'M')
                    command = 'L';
                else if (command is 'm')
                    command = 'l';
                if (!Command(command, first: false))
                    return;
            }
        }

        private bool Command(char command, bool first)
        {
            var relative = char.IsAsciiLetterLower(command);
            var origin = relative ? _current : default;
            switch (char.ToUpperInvariant(command))
            {
                case 'F':
                    if (!fillRule || !first || Number() is not { } rule)
                        return false;
                    path.FillType = rule == 1 ? SKPathFillType.Winding : SKPathFillType.EvenOdd;
                    return true;
                case 'M':
                    if (Point() is not { } move)
                        return false;
                    _current = _start = origin + move;
                    path.MoveTo(_current);
                    _open = true;
                    _lastControl = null;
                    budget.Points(1);
                    return true;
                case 'L':
                    if (Point() is not { } line)
                        return false;
                    LineTo(origin + line);
                    return true;
                case 'H':
                    if (Number() is not { } x)
                        return false;
                    LineTo(new SKPoint((relative ? _current.X : 0) + x, _current.Y));
                    return true;
                case 'V':
                    if (Number() is not { } y)
                        return false;
                    LineTo(new SKPoint(_current.X, (relative ? _current.Y : 0) + y));
                    return true;
                case 'C':
                    {
                        if (Point() is not { } c1 || Point() is not { } c2 || Point() is not { } end)
                            return false;
                        EnsureOpen();
                        path.CubicTo(origin + c1, origin + c2, origin + end);
                        _lastControl = origin + c2;
                        _current = origin + end;
                        budget.Points(3);
                        return true;
                    }
                case 'S':
                    {
                        if (Point() is not { } c2 || Point() is not { } end)
                            return false;
                        EnsureOpen();
                        var c1 = _lastControl is { } last ? new SKPoint(2 * _current.X - last.X, 2 * _current.Y - last.Y) : _current;
                        path.CubicTo(c1, origin + c2, origin + end);
                        _lastControl = origin + c2;
                        _current = origin + end;
                        budget.Points(3);
                        return true;
                    }
                case 'Q':
                    {
                        if (Point() is not { } c || Point() is not { } end)
                            return false;
                        EnsureOpen();
                        path.QuadTo(origin + c, origin + end);
                        _current = origin + end;
                        _lastControl = null;
                        budget.Points(2);
                        return true;
                    }
                case 'A':
                    {
                        if (Point() is not { } size || Number() is not { } rotation || Flag() is not { } large || Flag() is not { } sweep || Point() is not { } end)
                            return false;
                        EnsureOpen();
                        Arc(path, _current, size, rotation, large, sweep, origin + end);
                        _current = origin + end;
                        _lastControl = null;
                        budget.Points(4);
                        return true;
                    }
                case 'Z':
                    if (_open)
                        path.Close();
                    _current = _start;
                    _open = false;
                    _lastControl = null;
                    return true;
                default:
                    return false;
            }
        }

        private void LineTo(SKPoint point)
        {
            EnsureOpen();
            path.LineTo(point);
            _current = point;
            _lastControl = null;
            budget.Points(1);
        }

        /// <summary>A drawing command after Z starts a new figure at the current point.</summary>
        private void EnsureOpen()
        {
            if (_open)
                return;
            path.MoveTo(_current);
            _start = _current;
            _open = true;
        }

        private SKPoint? Point() =>
            Number() is { } x && Number() is { } y ? new SKPoint(x, y) : null;

        private bool? Flag() => Number() switch
        {
            0 => false,
            1 => true,
            _ => null,
        };

        private float? Number()
        {
            SkipSpace();
            var start = _at;
            if (_at < text.Length && text[_at] is '+' or '-')
                _at++;
            while (_at < text.Length && (char.IsAsciiDigit(text[_at]) || text[_at] == '.'))
                _at++;
            if (_at < text.Length && text[_at] is 'e' or 'E' && _at > start)
            {
                var mark = _at++;
                if (_at < text.Length && text[_at] is '+' or '-')
                    _at++;
                if (_at < text.Length && char.IsAsciiDigit(text[_at]))
                {
                    while (_at < text.Length && char.IsAsciiDigit(text[_at]))
                        _at++;
                }
                else
                {
                    _at = mark;
                }
            }
            if (_at == start || !float.TryParse(text.AsSpan(start, _at - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !float.IsFinite(value))
            {
                _at = start;
                return null;
            }
            return value;
        }

        private void SkipSpace()
        {
            while (_at < text.Length && (char.IsWhiteSpace(text[_at]) || text[_at] == ','))
                _at++;
        }
    }
}
