// Line end markers of ODT drawings (draw:marker in office:styles, named by a shape's draw:marker-start / -end) as the
// model's arrowheads, and the markers the ODT writer gives them.
//
// A marker is a path (svg:d in its svg:viewBox) pointing up: its top is drawn at the line's end. Its name is free text
// (LibreOffice's are translated and users make their own), so the kind is told from the path's outline: how much of its
// convex hull it fills (a chevron little, a notched arrow some, a convex mark all), then how much of its box (a square
// all of it, an ellipse about π/4, a triangle or diamond half) and where its weight sits (a triangle pointing up has
// it two thirds down, a diamond in the middle). Curves are followed through points along them; arcs by their ends.
// Its size: draw:marker-*-width across the line, and the path's height for that width along it, against the line's
// width (sizes as LineArrows measures them, rounded to the nearest of small, medium and large).

using System.Globalization;
using System.Xml.Linq;
using Filee.Engines.Typesetting;

namespace Filee.Engines.Hwp.Hwpx.Odt;

internal static class OdfMarkers
{
    /// <summary>Path numbers a marker is read up to; LibreOffice's own have a few hundred.</summary>
    private const int MaxNumbers = 4000;

    /// <summary>Path characters a marker is read up to (a damaged file can have megabytes of them on every line).</summary>
    private const int MaxChars = 65536;

    /// <summary>Points along each curve piece.</summary>
    private const int CurveSteps = 8;

    /// <summary>
    /// The arrowhead a draw:marker draws at a line's end, <paramref name="markerWidth"/> wide (ODF length) on a line
    /// <paramref name="lineWidthPt"/> points wide; null when the marker has no outline.
    /// </summary>
    public static HArrow? Arrow(XElement marker, string? markerWidth, double lineWidthPt)
    {
        var points = Points((string?)marker.Attribute(Odf.Svg + "d"));
        if (points.Count < 3)
            return null;
        var kind = Kind(points);
        if (kind is null)
            return null;
        var box = Bounds(points);
        // The width the marker is drawn at (LibreOffice's default is 0.3 cm), its length in the path's proportions.
        var width = Odf.Length(markerWidth) is > 0 and var hwp ? hwp / 100.0 : 0.3 / 2.54 * 72;
        var length = box.Width > 0 ? width * box.Height / box.Width : width;
        var unit = Math.Max(lineWidthPt, LineArrows.SmallestBase);
        return new HArrow(kind.Value, Size(width / unit), Size(length / unit));
    }

    /// <summary>The nearest of DrawingML's sizes (2, 3 and 5 line widths) to <paramref name="multiple"/> line widths.</summary>
    private static HArrowSize Size(double multiple) => multiple < 2.5 ? HArrowSize.Small : multiple < 4 ? HArrowSize.Medium : HArrowSize.Large;

    /// <summary>The kind of mark an outline is (see the file comment); null when it has no area.</summary>
    internal static HArrowKind? Kind(IReadOnlyList<(double X, double Y)> points)
    {
        var box = Bounds(points);
        var area = Math.Abs(Area(points));
        var hull = ConvexHull(points);
        var hullArea = Math.Abs(Area(hull));
        if (box.Width <= 0 || box.Height <= 0 || hullArea <= 0)
            return null;
        var solid = area / hullArea;
        if (solid < 0.6)
            return HArrowKind.Open;
        if (solid < 0.93)
            return HArrowKind.Stealth;
        var filled = area / (box.Width * box.Height);
        if (filled > 0.9)
            return HArrowKind.Square;
        if (filled > 0.68)
            return HArrowKind.Oval;
        // A triangle pointing up has its centre of area two thirds of the way down; a diamond halfway.
        var middle = (Centroid(points) - box.Top) / box.Height;
        return middle > 0.6 ? HArrowKind.Triangle : HArrowKind.Diamond;
    }

    /// <summary>
    /// The draw:marker the ODT writer gives an arrowhead: its name, and the element (named so) whose path is as long
    /// for its width as the arrowhead (so the width alone, draw:marker-*-width, sizes it).
    /// </summary>
    public static (string Name, string Xml) Definition(HArrow arrow)
    {
        // Outlines in a 20 × 20 box pointing up, their y stretched to the arrowhead's length.
        var (name, path) = arrow.Kind switch
        {
            HArrowKind.Stealth => ("Arrow concave", "M10 0 L20 20 L10 14 L0 20 Z"),
            HArrowKind.Diamond => ("Square 45", "M10 0 L20 10 L10 20 L0 10 Z"),
            HArrowKind.Oval => ("Circle", "M10 0 C15.5 0 20 4.5 20 10 C20 15.5 15.5 20 10 20 C4.5 20 0 15.5 0 10 C0 4.5 4.5 0 10 0 Z"),
            HArrowKind.Square => ("Square", "M0 0 L20 0 L20 20 L0 20 Z"),
            HArrowKind.Open => ("Line Arrow", "M10 0 L20 18 L18 20 L10 6 L2 20 L0 18 Z"),
            _ => ("Arrow", "M10 0 L20 20 L0 20 Z"),
        };
        var aspect = LineArrows.Factor(arrow.Length) / LineArrows.Factor(arrow.Width);
        if (arrow.Width != arrow.Length)
            name += $" {arrow.Width} {arrow.Length}".ToLowerInvariant();
        var height = 20 * aspect;
        var stretched = new System.Text.StringBuilder();
        var y = false;
        foreach (var token in path.Split(' '))
        {
            if (stretched.Length > 0)
                stretched.Append(' ');
            var command = token.Length > 0 && char.IsLetter(token[0]) ? token[..1] : "";
            var number = token[command.Length..];
            stretched.Append(command);
            if (number.Length == 0)
                continue;
            var value = double.Parse(number, CultureInfo.InvariantCulture);
            stretched.Append((y ? value * aspect : value).ToString("0.###", CultureInfo.InvariantCulture));
            y = !y;
        }
        var viewBox = $"0 0 20 {height.ToString("0.###", CultureInfo.InvariantCulture)}";
        // A style name has no spaces: LibreOffice writes them as "_20_" and keeps the name to show.
        var styleName = name.Replace(" ", "_20_", StringComparison.Ordinal);
        return (styleName, $"<draw:marker draw:name=\"{styleName}\" draw:display-name=\"{name}\" svg:viewBox=\"{viewBox}\" svg:d=\"{stretched}\"/>");
    }

    /// <summary>
    /// The outline of an SVG path's first figure (and the figures after it, joined) as points; curves are followed
    /// through points along them, arcs by their end points. Stops at <see cref="MaxNumbers"/> numbers or
    /// <see cref="MaxChars"/> characters.
    /// </summary>
    internal static List<(double X, double Y)> Points(string? d)
    {
        var points = new List<(double X, double Y)>();
        if (string.IsNullOrEmpty(d))
            return points;
        var numbers = new List<double>();
        var (x, y) = (0.0, 0.0);
        var (startX, startY) = (0.0, 0.0);
        var command = ' ';
        var i = 0;
        var count = 0;
        var length = Math.Min(d.Length, MaxChars);
        while (i < length && count < MaxNumbers)
        {
            var c = d[i];
            if (char.IsLetter(c) && c is not ('e' or 'E'))
            {
                command = c;
                i++;
                if (command is 'Z' or 'z')
                {
                    (x, y) = (startX, startY);
                    command = ' ';
                }
                continue;
            }
            if (!Number(d, ref i, out var value))
            {
                i++;
                continue;
            }
            count++;
            numbers.Add(value);
            var relative = char.IsLower(command);
            var (ox, oy) = relative ? (x, y) : (0.0, 0.0);
            switch (char.ToUpperInvariant(command))
            {
                case 'M' when numbers.Count == 2:
                    (x, y) = (ox + numbers[0], oy + numbers[1]);
                    (startX, startY) = (x, y);
                    points.Add((x, y));
                    numbers.Clear();
                    command = relative ? 'l' : 'L'; // pairs after a moveto are lines
                    break;
                case 'L' or 'T' when numbers.Count == 2:
                    (x, y) = (ox + numbers[0], oy + numbers[1]);
                    points.Add((x, y));
                    numbers.Clear();
                    break;
                case 'H' when numbers.Count == 1:
                    x = (relative ? x : 0) + numbers[0];
                    points.Add((x, y));
                    numbers.Clear();
                    break;
                case 'V' when numbers.Count == 1:
                    y = (relative ? y : 0) + numbers[0];
                    points.Add((x, y));
                    numbers.Clear();
                    break;
                case 'C' when numbers.Count == 6:
                    Cubic(points, (x, y), (ox + numbers[0], oy + numbers[1]), (ox + numbers[2], oy + numbers[3]), (ox + numbers[4], oy + numbers[5]));
                    (x, y) = (ox + numbers[4], oy + numbers[5]);
                    numbers.Clear();
                    break;
                case 'S' or 'Q' when numbers.Count == 4:
                    Cubic(points, (x, y), (ox + numbers[0], oy + numbers[1]), (ox + numbers[0], oy + numbers[1]), (ox + numbers[2], oy + numbers[3]));
                    (x, y) = (ox + numbers[2], oy + numbers[3]);
                    numbers.Clear();
                    break;
                case 'A' when numbers.Count == 7:
                    (x, y) = (ox + numbers[5], oy + numbers[6]);
                    points.Add((x, y));
                    numbers.Clear();
                    break;
                case 'M' or 'L' or 'T' or 'H' or 'V' or 'C' or 'S' or 'Q' or 'A':
                    break;
                default:
                    numbers.Clear();
                    break;
            }
        }
        return points;
    }

    /// <summary>Reads a number at <paramref name="i"/> ("-1.5e3", ".5"; "1.5.5" is two numbers).</summary>
    private static bool Number(string d, ref int i, out double value)
    {
        var start = i;
        if (i < d.Length && d[i] is '+' or '-')
            i++;
        var dot = false;
        var digits = false;
        while (i < d.Length && (char.IsAsciiDigit(d[i]) || d[i] == '.' && !dot))
        {
            dot |= d[i] == '.';
            digits |= char.IsAsciiDigit(d[i]);
            i++;
        }
        if (digits && i < d.Length && d[i] is 'e' or 'E')
        {
            var exponent = i + 1;
            if (exponent < d.Length && d[exponent] is '+' or '-')
                exponent++;
            if (exponent < d.Length && char.IsAsciiDigit(d[exponent]))
            {
                i = exponent;
                while (i < d.Length && char.IsAsciiDigit(d[i]))
                    i++;
            }
        }
        if (!digits || !double.TryParse(d.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !double.IsFinite(value))
        {
            i = digits ? i : start;
            value = 0;
            return false;
        }
        return true;
    }

    private static void Cubic(List<(double X, double Y)> points, (double X, double Y) p0, (double X, double Y) p1, (double X, double Y) p2, (double X, double Y) p3)
    {
        for (var step = 1; step <= CurveSteps; step++)
        {
            var t = step / (double)CurveSteps;
            var u = 1 - t;
            points.Add((u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X,
                u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y));
        }
    }

    private static (double Left, double Top, double Width, double Height) Bounds(IReadOnlyList<(double X, double Y)> points)
    {
        var (left, top, right, bottom) = (points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
        return (left, top, right - left, bottom - top);
    }

    /// <summary>The signed area of a closed outline (shoelace).</summary>
    private static double Area(IReadOnlyList<(double X, double Y)> points)
    {
        var sum = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            var (a, b) = (points[i], points[(i + 1) % points.Count]);
            sum += a.X * b.Y - b.X * a.Y;
        }
        return sum / 2;
    }

    /// <summary>The height of an outline's centre of area.</summary>
    private static double Centroid(IReadOnlyList<(double X, double Y)> points)
    {
        var (sum, area) = (0.0, 0.0);
        for (var i = 0; i < points.Count; i++)
        {
            var (a, b) = (points[i], points[(i + 1) % points.Count]);
            var cross = a.X * b.Y - b.X * a.Y;
            area += cross;
            sum += (a.Y + b.Y) * cross;
        }
        return Math.Abs(area) < 1e-12 ? points.Average(p => p.Y) : sum / (3 * area);
    }

    /// <summary>The convex hull of the points (monotone chain), counterclockwise.</summary>
    private static List<(double X, double Y)> ConvexHull(IReadOnlyList<(double X, double Y)> points)
    {
        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (sorted.Count < 3)
            return sorted;
        static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) =>
            (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var hull = new List<(double X, double Y)>();
        foreach (var pass in new[] { sorted, Enumerable.Reverse(sorted).ToList() })
        {
            var start = hull.Count;
            foreach (var p in pass)
            {
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], p) <= 0)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        return hull;
    }
}
