// Gerber aperture macros (%AM…%): a named list of primitives (circle, vector and centre lines, outline, polygon, moiré,
// thermal) whose modifiers are arithmetic expressions over the parameters an aperture definition passes ($1, $2, …)
// and over variables the macro assigns ($4=$1x2). A primitive with exposure off erases what the macro's earlier
// primitives drew, inside the aperture only, so the primitives are combined with Skia's path operations into one
// outline. Rotations turn a primitive about the macro's origin, not its own centre.
//
// Everything a macro can make the reader do is bounded: the number of primitives and outline points, moiré rings, the
// length and nesting of an expression, so a crafted file can't make it loop or recurse without end.

using System.Globalization;
using SkiaSharp;

namespace Filee.Engines.Vector.Gerber;

/// <summary>A parsed aperture macro, evaluated into an outline for each aperture definition that uses it.</summary>
internal sealed class GerberMacro
{
    private const int MaxStatements = 10_000;
    private const int MaxOutlinePoints = 100_000;
    private const int MaxMoireRings = 100;
    private const int MaxExpressionLength = 4096;
    private const int MaxExpressionDepth = 64;
    private const int MaxVariable = 10_000;

    private readonly List<string> _statements;

    private GerberMacro(string name, List<string> statements)
    {
        Name = name;
        _statements = statements;
    }

    /// <summary>Number of primitives and assignments, evaluated for each aperture that uses the macro.</summary>
    public int StatementCount => _statements.Count;

    /// <summary>The macro's name, which aperture definitions use as their template.</summary>
    public string Name { get; }

    /// <summary>Parses the words of an %AM…% block: "AMname" first, then one primitive or assignment per word.</summary>
    /// <exception cref="InvalidDataException">The macro has no name or too many statements.</exception>
    public static GerberMacro Parse(IReadOnlyList<string> words)
    {
        var name = words[0][2..].Trim();
        if (name.Length == 0)
            throw new InvalidDataException("The Gerber file defines an aperture macro without a name.");
        var statements = new List<string>();
        foreach (var word in words.Skip(1))
        {
            var statement = word.Trim();
            if (statement.Length == 0)
                continue;
            // Comments (primitive 0) may contain anything; the rest has no meaningful whitespace.
            if (statement.StartsWith('0') && (statement.Length == 1 || statement[1] is ' ' or ','))
                continue;
            statements.Add(string.Concat(statement.Where(c => !char.IsWhiteSpace(c))));
            if (statements.Count > MaxStatements)
                throw new InvalidDataException($"The Gerber aperture macro {name} has more than {MaxStatements} primitives.");
        }
        return new GerberMacro(name, statements);
    }

    /// <summary>
    /// The outline of an aperture made from this macro with <paramref name="parameters"/> ($1, $2, …), in file units,
    /// centred on the flash point. Empty when every primitive is off or degenerate.
    /// </summary>
    /// <exception cref="InvalidDataException">A primitive or expression is malformed.</exception>
    public SKPath Evaluate(IReadOnlyList<double> parameters)
    {
        var variables = new Dictionary<int, double>();
        for (var i = 0; i < parameters.Count; i++)
            variables[i + 1] = parameters[i];

        var result = new SKPath();
        var points = 0;
        foreach (var statement in _statements)
        {
            if (statement[0] == '$')
            {
                var equals = statement.IndexOf('=');
                if (equals < 2 || !int.TryParse(statement.AsSpan(1, equals - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var variable)
                    || variable > MaxVariable)
                    throw new InvalidDataException($"The Gerber aperture macro {Name} has a malformed assignment \"{statement}\".");
                variables[variable] = new Expression(statement[(equals + 1)..], variables, Name).Evaluate();
                continue;
            }

            var modifiers = statement.Split(',');
            var values = modifiers.Select(m => new Expression(m, variables, Name).Evaluate()).ToArray();
            var code = (int)values[0];
            using var primitive = Primitive(code, values, ref points);
            if (primitive is null)
                continue;
            var exposureOn = Math.Abs(values.ElementAtOrDefault(1)) > 0.5 || code is 6 or 7;
            if (exposureOn)
            {
                if (!result.Op(primitive, SKPathOp.Union, result))
                    result.AddPath(primitive);
            }
            else if (!result.IsEmpty)
            {
                result.Op(primitive, SKPathOp.Difference, result);
            }
        }
        return result;
    }

    /// <summary>One primitive's outline, rotated about the macro's origin; null for an unknown code or no area.</summary>
    private SKPath? Primitive(int code, double[] v, ref int points)
    {
        double At(int i) => i < v.Length ? v[i] : 0;
        SKPath path;
        double rotation;
        switch (code)
        {
            case 1: // circle: exposure, diameter, x, y[, rotation]
                if (At(2) <= 0)
                    return null;
                path = new SKPath();
                path.AddCircle((float)At(3), (float)At(4), (float)(At(2) / 2));
                rotation = At(5);
                break;
            case 2 or 20: // vector line: exposure, width, start x, y, end x, y, rotation
            {
                double width = At(2), sx = At(3), sy = At(4), ex = At(5), ey = At(6);
                var length = Math.Sqrt((ex - sx) * (ex - sx) + (ey - sy) * (ey - sy));
                if (width <= 0 || length <= 0)
                    return null;
                // The line's width perpendicular to it, with square (butt) ends at both points.
                double nx = -(ey - sy) / length * width / 2, ny = (ex - sx) / length * width / 2;
                path = Polygon([(sx + nx, sy + ny), (ex + nx, ey + ny), (ex - nx, ey - ny), (sx - nx, sy - ny)]);
                rotation = At(7);
                break;
            }
            case 21: // centre line: exposure, width, height, centre x, y, rotation
                if (At(2) <= 0 || At(3) <= 0)
                    return null;
                path = Rectangle(At(4) - At(2) / 2, At(5) - At(3) / 2, At(2), At(3));
                rotation = At(6);
                break;
            case 22: // lower-left line (removed from the specification, still written by old tools)
                if (At(2) <= 0 || At(3) <= 0)
                    return null;
                path = Rectangle(At(4), At(5), At(2), At(3));
                rotation = At(6);
                break;
            case 4: // outline: exposure, n, x0, y0, …, xn, yn, rotation
            {
                var count = At(2);
                if (count < 1 || count > MaxOutlinePoints || (points += (int)count + 1) > MaxOutlinePoints)
                    throw new InvalidDataException($"The Gerber aperture macro {Name} has an outline with too many points.");
                var n = (int)count;
                if (v.Length < 5 + 2 * n)
                    throw new InvalidDataException($"The Gerber aperture macro {Name} has a malformed outline primitive.");
                var vertices = new List<(double, double)>(n + 1);
                for (var i = 0; i <= n; i++)
                    vertices.Add((v[3 + 2 * i], v[4 + 2 * i]));
                path = Polygon(vertices);
                rotation = At(5 + 2 * n);
                break;
            }
            case 5: // polygon: exposure, vertices, centre x, y, diameter, rotation
            {
                var count = Math.Clamp((int)At(2), 3, 12);
                if (At(5) <= 0)
                    return null;
                path = Polygon(RegularPolygon(At(3), At(4), At(5), count, 0));
                rotation = At(6);
                break;
            }
            case 6: // moiré: centre x, y, outer diameter, ring thickness, gap, rings, cross thickness, cross length, rotation
                path = Moire(At(1), At(2), At(3), At(4), At(5), (int)Math.Min(At(6), MaxMoireRings), At(7), At(8));
                rotation = At(9);
                break;
            case 7: // thermal: centre x, y, outer diameter, inner diameter, gap, rotation
                path = Thermal(At(1), At(2), At(3), At(4), At(5));
                rotation = At(6);
                break;
            default:
                throw new InvalidDataException($"The Gerber aperture macro {Name} uses an unknown primitive {code}.");
        }
        if (rotation != 0)
            path.Transform(SKMatrix.CreateRotationDegrees((float)rotation));
        return path;
    }

    private static SKPath Rectangle(double x, double y, double width, double height)
    {
        var path = new SKPath();
        path.AddRect(new SKRect((float)x, (float)y, (float)(x + width), (float)(y + height)));
        return path;
    }

    internal static SKPath Polygon(IReadOnlyList<(double X, double Y)> vertices)
    {
        var path = new SKPath();
        path.MoveTo((float)vertices[0].X, (float)vertices[0].Y);
        for (var i = 1; i < vertices.Count; i++)
            path.LineTo((float)vertices[i].X, (float)vertices[i].Y);
        path.Close();
        return path;
    }

    /// <summary>The vertices of a regular polygon, the first at <paramref name="degrees"/> from the X axis.</summary>
    internal static List<(double X, double Y)> RegularPolygon(double cx, double cy, double diameter, int count, double degrees)
    {
        var vertices = new List<(double, double)>(count);
        for (var i = 0; i < count; i++)
        {
            var angle = (degrees + 360.0 * i / count) * Math.PI / 180;
            vertices.Add((cx + diameter / 2 * Math.Cos(angle), cy + diameter / 2 * Math.Sin(angle)));
        }
        return vertices;
    }

    private static SKPath Moire(double cx, double cy, double outer, double thickness, double gap, int rings, double crossThickness, double crossLength)
    {
        var path = new SKPath();
        for (var i = 0; i < rings; i++)
        {
            var diameter = outer - 2 * i * (thickness + gap);
            if (diameter <= 0 || thickness <= 0)
                break;
            using var ring = new SKPath();
            ring.AddCircle((float)cx, (float)cy, (float)(diameter / 2));
            var inner = diameter / 2 - thickness;
            if (inner > 0)
            {
                using var hole = new SKPath();
                hole.AddCircle((float)cx, (float)cy, (float)inner);
                ring.Op(hole, SKPathOp.Difference, ring);
            }
            path.Op(ring, SKPathOp.Union, path);
        }
        if (crossThickness > 0 && crossLength > 0)
        {
            using var horizontal = Rectangle(cx - crossLength / 2, cy - crossThickness / 2, crossLength, crossThickness);
            using var vertical = Rectangle(cx - crossThickness / 2, cy - crossLength / 2, crossThickness, crossLength);
            path.Op(horizontal, SKPathOp.Union, path);
            path.Op(vertical, SKPathOp.Union, path);
        }
        return path;
    }

    private static SKPath Thermal(double cx, double cy, double outer, double inner, double gap)
    {
        var path = new SKPath();
        if (outer <= 0 || inner >= outer)
            return path;
        path.AddCircle((float)cx, (float)cy, (float)(outer / 2));
        if (inner > 0)
        {
            using var hole = new SKPath();
            hole.AddCircle((float)cx, (float)cy, (float)(inner / 2));
            path.Op(hole, SKPathOp.Difference, path);
        }
        if (gap > 0)
        {
            using var horizontal = Rectangle(cx - outer, cy - gap / 2, 2 * outer, gap);
            using var vertical = Rectangle(cx - gap / 2, cy - outer, gap, 2 * outer);
            path.Op(horizontal, SKPathOp.Difference, path);
            path.Op(vertical, SKPathOp.Difference, path);
        }
        return path;
    }

    /// <summary>
    /// A macro expression: numbers, $n variables (0 when unset), + - x / and parentheses, unary signs. The
    /// specification writes multiplication as lower-case x; upper-case X is accepted too.
    /// </summary>
    private ref struct Expression(string text, Dictionary<int, double> variables, string macro)
    {
        private int _at;
        private int _depth;

        public double Evaluate()
        {
            if (text.Length > MaxExpressionLength)
                throw Malformed();
            if (text.Length == 0)
                return 0;
            var value = Sum();
            if (_at != text.Length)
                throw Malformed();
            return double.IsFinite(value) ? value : 0;
        }

        private double Sum()
        {
            var value = Product();
            while (_at < text.Length && text[_at] is '+' or '-')
                value = text[_at++] == '+' ? value + Product() : value - Product();
            return value;
        }

        private double Product()
        {
            var value = Factor();
            while (_at < text.Length && text[_at] is 'x' or 'X' or '/')
            {
                var multiply = text[_at++] != '/';
                var right = Factor();
                value = multiply ? value * right : right == 0 ? 0 : value / right;
            }
            return value;
        }

        private double Factor()
        {
            if (++_depth > MaxExpressionDepth || _at >= text.Length)
                throw Malformed();
            double value;
            var c = text[_at];
            if (c is '+' or '-')
            {
                _at++;
                value = c == '-' ? -Factor() : Factor();
            }
            else if (c == '(')
            {
                _at++;
                value = Sum();
                if (_at >= text.Length || text[_at] != ')')
                    throw Malformed();
                _at++;
            }
            else if (c == '$')
            {
                var start = ++_at;
                while (_at < text.Length && char.IsAsciiDigit(text[_at])) _at++;
                if (!int.TryParse(text.AsSpan(start, _at - start), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                    throw Malformed();
                value = variables.GetValueOrDefault(n);
            }
            else
            {
                var start = _at;
                while (_at < text.Length && (char.IsAsciiDigit(text[_at]) || text[_at] == '.')) _at++;
                if (!double.TryParse(text.AsSpan(start, _at - start), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
                    throw Malformed();
            }
            _depth--;
            return value;
        }

        private readonly InvalidDataException Malformed() =>
            new($"The Gerber aperture macro {macro} has a malformed expression \"{(text.Length > 80 ? text[..80] + "…" : text)}\".");
    }
}
