// The point lists and path data of ODF's polygons, polylines and paths: draw:points ("x,y x,y ...") and svg:d (SVG 1.1
// path data: M L H V C S Q T A Z, absolute and relative), in a shape's view box. Coordinates are mapped into the shape's
// box by the caller's map; the count of numbers read is limited, and what can't be read ends the path there.

using System.Globalization;
using SkiaSharp;

namespace Filee.Engines.Odf.Draw;

internal static class OdfPathData
{
    /// <summary>The points of a draw:points list, at most <paramref name="max"/>.</summary>
    public static List<(double X, double Y)> Points(string? text, int max)
    {
        var result = new List<(double, double)>();
        if (string.IsNullOrWhiteSpace(text))
            return result;
        var scanner = new Scanner(text);
        while (result.Count < max && scanner.Number() is { } x && scanner.Number() is { } y)
            result.Add((x, y));
        return result;
    }

    /// <summary>
    /// The outline of SVG path data, each point through <paramref name="map"/> (view box → box); radii of arcs are
    /// scaled by <paramref name="sx"/> and <paramref name="sy"/>.
    /// </summary>
    /// <param name="commands">Numbers read (each counts as one against the deck).</param>
    public static SKPath Parse(string d, Func<double, double, SKPoint> map, double sx, double sy, int max, out int commands)
    {
        var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        var scanner = new Scanner(d);
        double x = 0, y = 0, startX = 0, startY = 0;
        double controlX = 0, controlY = 0;
        var previous = ' ';
        var command = ' ';
        var open = false;
        void Start()
        {
            if (!open)
            {
                path.MoveTo(map(x, y));
                open = true;
            }
        }
        while (scanner.Count < max)
        {
            if (scanner.Command() is { } next)
                command = next;
            else if (command is ' ' or 'Z' or 'z' || !scanner.HasNumber)
                break;
            var relative = char.IsLower(command);
            double ox = relative ? x : 0, oy = relative ? y : 0;
            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    if (scanner.Number() is not { } mx || scanner.Number() is not { } my)
                        goto done;
                    (x, y) = (ox + mx, oy + my);
                    (startX, startY) = (x, y);
                    path.MoveTo(map(x, y));
                    open = true;
                    // Pairs after the first are lines.
                    command = relative ? 'l' : 'L';
                    break;
                case 'L':
                    if (scanner.Number() is not { } lx || scanner.Number() is not { } ly)
                        goto done;
                    Start();
                    (x, y) = (ox + lx, oy + ly);
                    path.LineTo(map(x, y));
                    break;
                case 'H':
                    if (scanner.Number() is not { } hx)
                        goto done;
                    Start();
                    x = ox + hx;
                    path.LineTo(map(x, y));
                    break;
                case 'V':
                    if (scanner.Number() is not { } vy)
                        goto done;
                    Start();
                    y = oy + vy;
                    path.LineTo(map(x, y));
                    break;
                case 'C':
                    {
                        if (scanner.Number() is not { } x1 || scanner.Number() is not { } y1 || scanner.Number() is not { } x2 || scanner.Number() is not { } y2
                            || scanner.Number() is not { } cx || scanner.Number() is not { } cy)
                            goto done;
                        Start();
                        path.CubicTo(map(ox + x1, oy + y1), map(ox + x2, oy + y2), map(ox + cx, oy + cy));
                        (controlX, controlY) = (ox + x2, oy + y2);
                        (x, y) = (ox + cx, oy + cy);
                        break;
                    }
                case 'S':
                    {
                        if (scanner.Number() is not { } x2 || scanner.Number() is not { } y2 || scanner.Number() is not { } cx || scanner.Number() is not { } cy)
                            goto done;
                        Start();
                        var (rx, ry) = char.ToUpperInvariant(previous) is 'C' or 'S' ? (2 * x - controlX, 2 * y - controlY) : (x, y);
                        path.CubicTo(map(rx, ry), map(ox + x2, oy + y2), map(ox + cx, oy + cy));
                        (controlX, controlY) = (ox + x2, oy + y2);
                        (x, y) = (ox + cx, oy + cy);
                        break;
                    }
                case 'Q':
                    {
                        if (scanner.Number() is not { } x1 || scanner.Number() is not { } y1 || scanner.Number() is not { } qx || scanner.Number() is not { } qy)
                            goto done;
                        Start();
                        path.QuadTo(map(ox + x1, oy + y1), map(ox + qx, oy + qy));
                        (controlX, controlY) = (ox + x1, oy + y1);
                        (x, y) = (ox + qx, oy + qy);
                        break;
                    }
                case 'T':
                    {
                        if (scanner.Number() is not { } qx || scanner.Number() is not { } qy)
                            goto done;
                        Start();
                        var (rx, ry) = char.ToUpperInvariant(previous) is 'Q' or 'T' ? (2 * x - controlX, 2 * y - controlY) : (x, y);
                        path.QuadTo(map(rx, ry), map(ox + qx, oy + qy));
                        (controlX, controlY) = (rx, ry);
                        (x, y) = (ox + qx, oy + qy);
                        break;
                    }
                case 'A':
                    {
                        if (scanner.Number() is not { } rx || scanner.Number() is not { } ry || scanner.Number() is not { } rotation
                            || scanner.Flag() is not { } large || scanner.Flag() is not { } sweep || scanner.Number() is not { } ax || scanner.Number() is not { } ay)
                            goto done;
                        Start();
                        (x, y) = (ox + ax, oy + ay);
                        var end = map(x, y);
                        path.ArcTo(new SKPoint((float)Math.Abs(rx * sx), (float)Math.Abs(ry * sy)), (float)rotation,
                            large ? SKPathArcSize.Large : SKPathArcSize.Small, sweep ? SKPathDirection.Clockwise : SKPathDirection.CounterClockwise, end);
                        break;
                    }
                case 'Z':
                    if (open)
                        path.Close();
                    open = false;
                    (x, y) = (startX, startY);
                    break;
                default:
                    goto done;
            }
            previous = command;
        }
    done:
        commands = scanner.Count;
        return path;
    }

    /// <summary>Numbers, flags and command letters of path data, separated by white space and commas.</summary>
    private sealed class Scanner(string text)
    {
        private int _at;

        /// <summary>Numbers and flags read.</summary>
        public int Count { get; private set; }

        private void Skip()
        {
            while (_at < text.Length && (char.IsWhiteSpace(text[_at]) || text[_at] == ','))
                _at++;
        }

        public bool HasNumber
        {
            get
            {
                Skip();
                return _at < text.Length && (char.IsAsciiDigit(text[_at]) || text[_at] is '-' or '+' or '.');
            }
        }

        public char? Command()
        {
            Skip();
            if (_at < text.Length && char.IsAsciiLetter(text[_at]) && text[_at] is not ('e' or 'E'))
                return text[_at++];
            return null;
        }

        public double? Number()
        {
            Skip();
            var start = _at;
            if (_at < text.Length && text[_at] is '-' or '+')
                _at++;
            var dot = false;
            while (_at < text.Length && (char.IsAsciiDigit(text[_at]) || text[_at] == '.' && !dot))
            {
                dot |= text[_at] == '.';
                _at++;
            }
            if (_at < text.Length && text[_at] is 'e' or 'E' && _at + 1 < text.Length && (char.IsAsciiDigit(text[_at + 1]) || text[_at + 1] is '-' or '+'))
            {
                _at += 2;
                while (_at < text.Length && char.IsAsciiDigit(text[_at]))
                    _at++;
            }
            if (_at == start || !double.TryParse(text.AsSpan(start, _at - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || !double.IsFinite(value))
            {
                _at = start;
                return null;
            }
            Count++;
            return Math.Clamp(value, -1e12, 1e12);
        }

        /// <summary>An arc flag: a single 0 or 1, which may stand without a separator ("a1 1 0 01 2 3").</summary>
        public bool? Flag()
        {
            Skip();
            if (_at < text.Length && text[_at] is '0' or '1')
            {
                Count++;
                return text[_at++] == '1';
            }
            return null;
        }
    }
}
