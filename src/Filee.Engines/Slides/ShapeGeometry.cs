// Shape geometry as DrawingML describes it (ECMA-376 Part 1, 20.1.9): adjust values, guides computed by formulas from
// the shape's size, and paths drawn with them. Preset shapes (PresetShapes) and custom geometry (a:custGeom) both end
// up here; ODF's enhanced geometry uses the same kind of formulas and could be translated into this form.
//
// Formulas (a:gd fmla): "*/ x y z" = x·y/z, "+- x y z" = x+y−z, "+/ x y z" = (x+y)/z, "?: x y z" = x>0 ? y : z,
// abs, at2 (atan2(y, x) in 60,000ths of a degree), cat2 x y z = x·cos(atan2(z, y)), sat2 x y z = x·sin(atan2(z, y)),
// cos x y = x·cos(y), sin, tan, max, min, mod x y z = √(x²+y²+z²), pin x y z = y clamped to [x, z], sqrt, val. An
// argument is a guide's name, a built-in (w, h, l, t, r, b, hc, vc, ss, ls, wd2 … hd10, ssd2 … ssd32, cd2, cd4, cd8,
// 3cd4, 3cd8, 5cd8, 7cd8) or a number. Angles are 60,000ths of a degree, clockwise (y points down).
//
// Paths: moveTo, lnTo, arcTo (wR hR stAng swAng from the current point), quadBezTo, cubicBezTo, close; a path with its
// own w / h is in that coordinate space, stretched over the shape. Arc angles are the angles seen on the stretched
// ellipse; they are turned into the ellipse's parameter (t = atan2(wR·sin a, hR·cos a)) before drawing.
//
// Damage stays contained: unknown names count as 0, a division by zero gives 0, results are clamped, and the counts of
// guides and path commands are limited.

using System.Globalization;
using SkiaSharp;

namespace Filee.Engines.Slides;

/// <summary>A path of a geometry definition: its own coordinate space (0 = the shape's), fill and stroke.</summary>
internal sealed record GeometryPathDefinition(double Width, double Height, PathFill Fill, bool Stroke, IReadOnlyList<GeometryCommand> Commands);

/// <summary>One path command: 'M' move, 'L' line, 'A' arc, 'Q' quadratic, 'C' cubic, 'Z' close; arguments are formulas' operands.</summary>
internal sealed record GeometryCommand(char Op, IReadOnlyList<string> Arguments);

/// <summary>A shape's geometry before its size is known.</summary>
internal sealed class GeometryDefinition
{
    /// <summary>Adjust values with their default formulas (a:avLst).</summary>
    public List<(string Name, string Formula)> Adjust { get; } = [];

    /// <summary>Guides in order (a:gdLst).</summary>
    public List<(string Name, string Formula)> Guides { get; } = [];

    public List<GeometryPathDefinition> Paths { get; } = [];

    /// <summary>The text rectangle's left, top, right and bottom operands (a:rect); null = the whole shape.</summary>
    public (string Left, string Top, string Right, string Bottom)? TextRect { get; set; }
}

/// <summary>A geometry evaluated at a size: outlines and the text rectangle, in output units.</summary>
internal sealed record GeometryResult(IReadOnlyList<SlidePath> Paths, SKRect? TextRect);

internal static class ShapeGeometry
{
    /// <summary>Guides of one shape at most.</summary>
    public const int MaxGuides = 4096;

    /// <summary>Path commands of one shape at most.</summary>
    public const int MaxCommands = 100_000;

    private const double Limit = 1e12;
    private const double AngleUnit = 60000;

    /// <summary>
    /// The outlines of <paramref name="definition"/> for a shape <paramref name="width"/> × <paramref name="height"/>
    /// (in the unit the formulas use, EMU for DrawingML), multiplied by <paramref name="scale"/> for output.
    /// </summary>
    /// <param name="adjust">The shape's own adjust values (a:avLst of the shape), by name; others keep their defaults.</param>
    public static GeometryResult Evaluate(GeometryDefinition definition, double width, double height,
        IReadOnlyDictionary<string, string>? adjust, double scale)
    {
        var values = Builtins(width, height);
        foreach (var (name, formula) in definition.Adjust.Take(MaxGuides))
            values[name] = Formula(adjust?.GetValueOrDefault(name) ?? formula, values);
        foreach (var (name, formula) in definition.Guides.Take(MaxGuides))
            values[name] = Formula(formula, values);

        var paths = new List<SlidePath>();
        var commands = 0;
        foreach (var path in definition.Paths)
        {
            if ((commands += path.Commands.Count) > MaxCommands)
                break;
            var sx = path.Width > 0 ? width / path.Width : 1;
            var sy = path.Height > 0 ? height / path.Height : 1;
            paths.Add(new SlidePath(Build(path, values, sx * scale, sy * scale, sx, sy), path.Fill, path.Stroke));
        }

        SKRect? text = null;
        if (definition.TextRect is var (l, t, r, b))
        {
            var rect = new SKRect((float)(Value(l, values) * scale), (float)(Value(t, values) * scale), (float)(Value(r, values) * scale),
                (float)(Value(b, values) * scale));
            text = rect.Standardized;
        }
        return new GeometryResult(paths, text);
    }

    private static SKPath Build(GeometryPathDefinition path, Dictionary<string, double> values, double sx, double sy, double unitX, double unitY)
    {
        var result = new SKPath { FillType = SKPathFillType.EvenOdd };
        double X(string operand) => Value(operand, values) * sx;
        double Y(string operand) => Value(operand, values) * sy;
        var current = new SKPoint(0, 0);
        var started = false;
        foreach (var command in path.Commands)
        {
            var a = command.Arguments;
            switch (command.Op)
            {
                case 'M' when a.Count >= 2:
                    current = Point(X(a[0]), Y(a[1]));
                    result.MoveTo(current);
                    started = true;
                    break;
                case 'L' when a.Count >= 2:
                    current = Point(X(a[0]), Y(a[1]));
                    LineOrMove(result, current, ref started);
                    break;
                case 'Q' when a.Count >= 4:
                    if (!started)
                        result.MoveTo(current);
                    started = true;
                    var control = Point(X(a[0]), Y(a[1]));
                    current = Point(X(a[2]), Y(a[3]));
                    result.QuadTo(control, current);
                    break;
                case 'C' when a.Count >= 6:
                    if (!started)
                        result.MoveTo(current);
                    started = true;
                    var c1 = Point(X(a[0]), Y(a[1]));
                    var c2 = Point(X(a[2]), Y(a[3]));
                    current = Point(X(a[4]), Y(a[5]));
                    result.CubicTo(c1, c2, current);
                    break;
                case 'A' when a.Count >= 4:
                    if (!started)
                        result.MoveTo(current);
                    started = true;
                    current = Arc(result, current, Value(a[0], values) * sx, Value(a[1], values) * sy,
                        Value(a[0], values) * unitX, Value(a[1], values) * unitY, Value(a[2], values) / AngleUnit, Value(a[3], values) / AngleUnit);
                    break;
                case 'Z':
                    result.Close();
                    // The next command starts where the closed figure started.
                    if (result.PointCount > 0)
                        current = result.LastPoint;
                    started = false;
                    break;
            }
        }
        return result;
    }

    private static void LineOrMove(SKPath path, SKPoint point, ref bool started)
    {
        if (started)
        {
            path.LineTo(point);
            return;
        }
        path.MoveTo(point);
        started = true;
    }

    private static SKPoint Point(double x, double y) => new((float)Math.Clamp(x, -Limit, Limit), (float)Math.Clamp(y, -Limit, Limit));

    /// <summary>
    /// An arc from <paramref name="from"/> on an ellipse of radii <paramref name="rx"/> × <paramref name="ry"/> (output
    /// units), from angle <paramref name="start"/> through <paramref name="sweep"/> degrees; the angles are seen on the
    /// ellipse in shape space (radii <paramref name="ux"/> × <paramref name="uy"/>). Returns the arc's end.
    /// </summary>
    private static SKPoint Arc(SKPath path, SKPoint from, double rx, double ry, double ux, double uy, double start, double sweep)
    {
        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        if (rx < 1e-9 || ry < 1e-9 || !double.IsFinite(start) || !double.IsFinite(sweep) || sweep == 0)
            return from;
        sweep = Math.Clamp(sweep, -3600, 3600);
        var t1 = Parameter(start, Math.Abs(ux), Math.Abs(uy));
        var t2 = Parameter(start + sweep, Math.Abs(ux), Math.Abs(uy));
        var turns = Math.Floor(Math.Abs(sweep) / 360);
        double delta;
        if (sweep > 0)
        {
            delta = Positive(t2 - t1);
            if (Math.Abs(sweep - turns * 360) < 1e-7)
                delta = 0;
            delta += turns * 360;
        }
        else
        {
            delta = -Positive(t1 - t2);
            if (Math.Abs(-sweep - turns * 360) < 1e-7)
                delta = 0;
            delta -= turns * 360;
        }
        var cx = from.X - rx * Math.Cos(t1 * Math.PI / 180);
        var cy = from.Y - ry * Math.Sin(t1 * Math.PI / 180);
        var oval = new SKRect((float)(cx - rx), (float)(cy - ry), (float)(cx + rx), (float)(cy + ry));
        // Skia's arcs are drawn in pieces of at most half a turn, so a full ellipse comes out whole.
        var pieces = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / 180));
        var step = delta / pieces;
        for (var i = 0; i < pieces; i++)
            path.ArcTo(oval, (float)(t1 + step * i), (float)step, false);
        var end = t1 + delta;
        return new SKPoint((float)(cx + rx * Math.Cos(end * Math.PI / 180)), (float)(cy + ry * Math.Sin(end * Math.PI / 180)));
    }

    private static double Positive(double degrees)
    {
        degrees %= 360;
        return degrees < 0 ? degrees + 360 : degrees;
    }

    /// <summary>The ellipse parameter (degrees) of the point seen at angle <paramref name="degrees"/>.</summary>
    private static double Parameter(double degrees, double rx, double ry)
    {
        var a = degrees * Math.PI / 180;
        var turns = Math.Floor(degrees / 360) * 360;
        var t = Math.Atan2(rx * Math.Sin(a), ry * Math.Cos(a)) * 180 / Math.PI;
        // Keep the parameter in the same turn as the angle, so sweeps can be measured from it.
        t = Positive(t) + turns;
        if (t - degrees > 180)
            t -= 360;
        else if (degrees - t > 180)
            t += 360;
        return t;
    }

    private static Dictionary<string, double> Builtins(double w, double h)
    {
        var ss = Math.Min(w, h);
        return new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["l"] = 0,
            ["t"] = 0,
            ["r"] = w,
            ["b"] = h,
            ["w"] = w,
            ["h"] = h,
            ["hc"] = w / 2,
            ["vc"] = h / 2,
            ["ss"] = ss,
            ["ls"] = Math.Max(w, h),
            ["wd2"] = w / 2,
            ["wd3"] = w / 3,
            ["wd4"] = w / 4,
            ["wd5"] = w / 5,
            ["wd6"] = w / 6,
            ["wd8"] = w / 8,
            ["wd10"] = w / 10,
            ["wd12"] = w / 12,
            ["wd32"] = w / 32,
            ["hd2"] = h / 2,
            ["hd3"] = h / 3,
            ["hd4"] = h / 4,
            ["hd5"] = h / 5,
            ["hd6"] = h / 6,
            ["hd8"] = h / 8,
            ["hd10"] = h / 10,
            ["hd12"] = h / 12,
            ["hd32"] = h / 32,
            ["ssd2"] = ss / 2,
            ["ssd4"] = ss / 4,
            ["ssd6"] = ss / 6,
            ["ssd8"] = ss / 8,
            ["ssd16"] = ss / 16,
            ["ssd32"] = ss / 32,
            ["cd2"] = 10_800_000,
            ["cd4"] = 5_400_000,
            ["cd8"] = 2_700_000,
            ["3cd4"] = 16_200_000,
            ["3cd8"] = 8_100_000,
            ["5cd8"] = 13_500_000,
            ["7cd8"] = 18_900_000,
        };
    }

    private static double Value(string operand, Dictionary<string, double> values) =>
        values.TryGetValue(operand, out var value) ? value
        : double.TryParse(operand, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? Math.Clamp(number, -Limit, Limit)
        : 0;

    /// <summary>The value of a guide formula.</summary>
    internal static double Formula(string formula, Dictionary<string, double> values)
    {
        var parts = formula.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return 0;
        double Arg(int i) => i < parts.Length ? Value(parts[i], values) : 0;
        static double Radians(double angle) => angle / AngleUnit * Math.PI / 180;
        var x = Arg(1);
        var y = Arg(2);
        var z = Arg(3);
        var result = parts[0] switch
        {
            "*/" => z == 0 ? 0 : x * y / z,
            "+-" => x + y - z,
            "+/" => z == 0 ? 0 : (x + y) / z,
            "?:" => x > 0 ? y : z,
            "abs" => Math.Abs(x),
            "at2" => Math.Atan2(y, x) * 180 / Math.PI * AngleUnit,
            "cat2" => x * Math.Cos(Math.Atan2(z, y)),
            "sat2" => x * Math.Sin(Math.Atan2(z, y)),
            "cos" => x * Math.Cos(Radians(y)),
            "sin" => x * Math.Sin(Radians(y)),
            "tan" => x * Math.Tan(Radians(y)),
            "max" => Math.Max(x, y),
            "min" => Math.Min(x, y),
            "mod" => Math.Sqrt(x * x + y * y + z * z),
            "pin" => y < x ? x : y > z ? z : y,
            "sqrt" => x > 0 ? Math.Sqrt(x) : 0,
            "val" => x,
            _ => 0,
        };
        return double.IsFinite(result) ? Math.Clamp(result, -Limit, Limit) : 0;
    }

    // ───────────────────────── Preset notation ─────────────────────────

    /// <summary>
    /// Reads Filee's compact notation of preset definitions (PresetShapes): one shape per block, its name alone on a
    /// line, then indented lines "av name formula", "gd name formula", "text l t r b" and
    /// "path [w=n] [h=n] [fill=none|lighten|lightenLess|darken|darkenLess] [stroke=0]: commands" with the commands
    /// "M x y", "L x y", "A wR hR stAng swAng", "Q x1 y1 x y", "C x1 y1 x2 y2 x y" and "Z".
    /// </summary>
    internal static Dictionary<string, GeometryDefinition> ParseNotation(string notation)
    {
        var result = new Dictionary<string, GeometryDefinition>(StringComparer.Ordinal);
        GeometryDefinition? current = null;
        foreach (var raw in notation.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
                continue;
            if (!char.IsWhiteSpace(line[0]))
            {
                current = new GeometryDefinition();
                result[line.Trim()] = current;
                continue;
            }
            if (current is null)
                continue;
            line = line.Trim();
            var space = line.IndexOf(' ');
            var keyword = space < 0 ? line : line[..space];
            var rest = space < 0 ? "" : line[(space + 1)..].Trim();
            switch (keyword)
            {
                case "av":
                case "gd":
                    var split = rest.IndexOf(' ');
                    // Adjust values are numbers in the notation, "val n" formulas as DrawingML writes them.
                    var entry = (rest[..split], keyword == "av" ? "val " + rest[(split + 1)..] : rest[(split + 1)..]);
                    (keyword == "av" ? current.Adjust : current.Guides).Add(entry);
                    break;
                case "text":
                    var operands = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    current.TextRect = (operands[0], operands[1], operands[2], operands[3]);
                    break;
                case "path":
                case "path:":
                    current.Paths.Add(PathNotation(line));
                    break;
            }
        }
        return result;
    }

    private static GeometryPathDefinition PathNotation(string line)
    {
        var colon = line.IndexOf(':');
        var attributes = line[4..colon].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double width = 0, height = 0;
        var fill = PathFill.Normal;
        var stroke = true;
        foreach (var attribute in attributes)
        {
            var (name, value) = (attribute[..attribute.IndexOf('=')], attribute[(attribute.IndexOf('=') + 1)..]);
            switch (name)
            {
                case "w":
                    width = double.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "h":
                    height = double.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "fill":
                    fill = FillMode(value);
                    break;
                case "stroke":
                    stroke = value != "0";
                    break;
            }
        }
        var commands = new List<GeometryCommand>();
        var tokens = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length;)
        {
            var op = tokens[i++][0];
            var count = op switch
            {
                'M' or 'L' => 2,
                'A' or 'Q' => 4,
                'C' => 6,
                _ => 0,
            };
            commands.Add(new GeometryCommand(op, tokens.Skip(i).Take(count).ToArray()));
            i += count;
        }
        return new GeometryPathDefinition(width, height, fill, stroke, commands);
    }

    /// <summary>A DrawingML path fill mode (a:path fill).</summary>
    public static PathFill FillMode(string? value) => value switch
    {
        "none" => PathFill.None,
        "lighten" => PathFill.Lighten,
        "lightenLess" => PathFill.LightenLess,
        "darken" => PathFill.Darken,
        "darkenLess" => PathFill.DarkenLess,
        _ => PathFill.Normal,
    };
}
