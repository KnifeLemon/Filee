// Paths of the document model (HShapeKind.Path, HPath): bent and curved connectors, open free-form lines and closed
// free-form shapes, as the readers make them and the writers take them apart.
//
// - DrawingML's connector presets (ECMA-376 Part 1, presetShapeDefinitions: bentConnector2..5, curvedConnector2..5)
//   are drawn by the slide renderer's preset definitions (Slides.PresetShapes) at the shape's size with its adjust
//   values, and the outline read back from the path they give; the preset's name, adjust values and flips go with it
//   (HConnector), so DOCX can write the connector back as itself.
// - A Skia path becomes an HPath when it is one contour (open, or closed when the reader takes closed ones);
//   quadratic pieces and conics (Skia's arcs) become cubics.
// - Writers that only have polylines flatten the curves; 한글's curved connector stores two controls per cubic and
//   takes the joint between two cubics halfway between their neighbouring controls (Smooth tells when a path is so).

using Filee.Engines.Slides;
using SkiaSharp;

namespace Filee.Engines.Hwp.Hwpx;

internal static class OpenPaths
{
    /// <summary>Pieces of one path at most (a damaged file can have millions of points); a longer one isn't read.</summary>
    public const int MaxSegments = 4096;

    /// <summary>Points each curve is flattened into for writers without curves.</summary>
    public const int FlattenSteps = 8;

    /// <summary>The DrawingML presets that are bent or curved connectors.</summary>
    public static bool IsConnectorPreset(string? name) =>
        name is "bentConnector2" or "bentConnector3" or "bentConnector4" or "bentConnector5"
            or "curvedConnector2" or "curvedConnector3" or "curvedConnector4" or "curvedConnector5";

    /// <summary>
    /// The geometry of connector preset <paramref name="name"/> in a box <paramref name="width"/> × <paramref name="height"/>
    /// with its adjust values, mirrored by the flips, and the preset itself (<see cref="HGeometry.Connector"/>); null for
    /// another preset.
    /// </summary>
    public static HGeometry? PresetGeometry(string? name, int width, int height, IReadOnlyDictionary<string, string>? adjust, bool flipHorizontal, bool flipVertical)
    {
        if (Preset(name, width, height, adjust) is not { } path)
            return null;
        // The adjust values written back: the preset's own (adj1 .. adj3), as plain numbers.
        var kept = new List<HAdjustValue>();
        foreach (var guide in (string[])["adj1", "adj2", "adj3"])
        {
            if (adjust?.GetValueOrDefault(guide)?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is ["val", var number]
                && long.TryParse(number, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value))
                kept.Add(new HAdjustValue(guide, Math.Clamp(value, -10_000_000, 10_000_000)));
        }
        return new HGeometry(width, height, Path: Mirrored(path, width, height, flipHorizontal, flipVertical),
            Connector: new HConnector(name!, kept, flipHorizontal, flipVertical));
    }

    /// <summary>
    /// The outline of connector preset <paramref name="name"/> in a box <paramref name="width"/> × <paramref name="height"/>
    /// (model units) with the shape's adjust values (a:gd name / fmla, as "val 50000"); null for another preset.
    /// </summary>
    public static HPath? Preset(string? name, int width, int height, IReadOnlyDictionary<string, string>? adjust)
    {
        if (!IsConnectorPreset(name) || PresetShapes.Find(name) is not { } definition)
            return null;
        var result = ShapeGeometry.Evaluate(definition, Math.Max(0, width), Math.Max(0, height), adjust, 1);
        try
        {
            return result.Paths.Where(p => p.Stroke).Select(p => FromSkia(p.Path)).FirstOrDefault(p => p is not null);
        }
        finally
        {
            foreach (var path in result.Paths)
                path.Path.Dispose();
        }
    }

    /// <summary>
    /// The one open contour of <paramref name="path"/>, each point through <paramref name="map"/> (rounded to model
    /// units without it); null when the path is empty, has more than one contour or more than
    /// <see cref="MaxSegments"/> pieces, or is closed unless <paramref name="closed"/> takes closed ones too.
    /// </summary>
    public static HPath? FromSkia(SKPath path, Func<SKPoint, HPoint>? map = null, bool closed = false)
    {
        map ??= p => new HPoint(Round(p.X), Round(p.Y));
        using var iterator = path.CreateRawIterator();
        var points = new SKPoint[4];
        HPoint? start = null;
        var current = default(SKPoint);
        var segments = new List<HPathSegment>();
        void Cubic(SKPoint c1, SKPoint c2, SKPoint end)
        {
            segments.Add(new HPathSegment(map(end), map(c1), map(c2)));
            current = end;
        }
        SKPathVerb verb;
        var shut = false;
        while ((verb = iterator.Next(points)) != SKPathVerb.Done)
        {
            if (segments.Count > MaxSegments)
                return null;
            // Something after the close: a second contour.
            if (shut && verb != SKPathVerb.Close)
                return null;
            switch (verb)
            {
                case SKPathVerb.Move:
                    // A second contour (a move after something was drawn) is not one open line.
                    if (segments.Count > 0)
                        return null;
                    start = map(points[0]);
                    current = points[0];
                    break;
                case SKPathVerb.Line:
                    segments.Add(new HPathSegment(map(points[1])));
                    current = points[1];
                    break;
                case SKPathVerb.Quad:
                    Cubic(Lerp(points[0], points[1], 2 / 3f), Lerp(points[2], points[1], 2 / 3f), points[2]);
                    break;
                case SKPathVerb.Conic:
                    {
                        var quads = SKPath.ConvertConicToQuads(points[0], points[1], points[2], iterator.ConicWeight(), 2);
                        for (var i = 1; i + 1 < quads.Length; i += 2)
                            Cubic(Lerp(current, quads[i], 2 / 3f), Lerp(quads[i + 1], quads[i], 2 / 3f), quads[i + 1]);
                        break;
                    }
                case SKPathVerb.Cubic:
                    Cubic(points[1], points[2], points[3]);
                    break;
                case SKPathVerb.Close:
                    if (!closed)
                        return null;
                    shut = true;
                    break;
            }
        }
        if (shut && segments.Count > 1 && start is { } from && segments[^1] is { Curved: false } last && last.End == from)
            segments.RemoveAt(segments.Count - 1); // the straight piece back to the start: the close draws it
        // A closed path of one straight piece has no inside: a line drawn there and back.
        if (shut && segments is [{ Curved: false }])
            return null;
        return start is { } first && segments.Count is > 0 and <= MaxSegments ? new HPath(first, segments, shut) : null;
    }

    /// <summary>The path as a Skia path, each point through <paramref name="map"/>.</summary>
    public static SKPath ToSkia(HPath path, Func<HPoint, SKPoint> map)
    {
        var result = new SKPath();
        result.MoveTo(map(path.Start));
        foreach (var segment in path.Segments)
        {
            if (segment is { Control1: { } c1, Control2: { } c2 })
                result.CubicTo(map(c1), map(c2), map(segment.End));
            else
                result.LineTo(map(segment.End));
        }
        if (path.Closed)
            result.Close();
        return result;
    }

    /// <summary>The path mirrored in a box <paramref name="width"/> × <paramref name="height"/>.</summary>
    public static HPath Mirrored(HPath path, int width, int height, bool horizontal, bool vertical)
    {
        if (!horizontal && !vertical)
            return path;
        HPoint Flip(HPoint p) => new(horizontal ? width - p.X : p.X, vertical ? height - p.Y : p.Y);
        HPoint? FlipOptional(HPoint? p) => p is { } point ? Flip(point) : null;
        return new HPath(Flip(path.Start), [.. path.Segments.Select(s => new HPathSegment(Flip(s.End), FlipOptional(s.Control1), FlipOptional(s.Control2)))], path.Closed);
    }

    /// <summary>The path moved by (<paramref name="dx"/>, <paramref name="dy"/>).</summary>
    public static HPath Moved(HPath path, int dx, int dy)
    {
        if (dx == 0 && dy == 0)
            return path;
        HPoint Move(HPoint p) => new(p.X + dx, p.Y + dy);
        HPoint? MoveOptional(HPoint? p) => p is { } point ? Move(point) : null;
        return new HPath(Move(path.Start), [.. path.Segments.Select(s => new HPathSegment(Move(s.End), MoveOptional(s.Control1), MoveOptional(s.Control2)))], path.Closed);
    }

    /// <summary>The smallest box around the path's points and controls (a Bézier stays inside its controls' hull).</summary>
    public static (int Left, int Top, int Right, int Bottom) Bounds(HPath path)
    {
        var points = Points(path).ToList();
        return (points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
    }

    /// <summary>The corners of the path with each curve flattened into <see cref="FlattenSteps"/> straight pieces.</summary>
    public static List<HPoint> Flattened(HPath path)
    {
        var result = new List<HPoint> { path.Start };
        var current = path.Start;
        foreach (var segment in path.Segments)
        {
            if (segment is { Control1: { } c1, Control2: { } c2 })
            {
                for (var i = 1; i <= FlattenSteps; i++)
                {
                    var t = i / (double)FlattenSteps;
                    var u = 1 - t;
                    double At(int a, int b, int c, int d) => u * u * u * a + 3 * u * u * t * b + 3 * u * t * t * c + t * t * t * d;
                    result.Add(new HPoint(Round(At(current.X, c1.X, c2.X, segment.End.X)), Round(At(current.Y, c1.Y, c2.Y, segment.End.Y))));
                }
            }
            else
            {
                result.Add(segment.End);
            }
            current = segment.End;
        }
        return result;
    }

    /// <summary>
    /// Every piece is a cubic and every joint lies halfway between the controls on either side of it (within
    /// <paramref name="tolerance"/> units), as in DrawingML's curved connectors: the controls alone then say the path.
    /// </summary>
    public static bool Smooth(HPath path, int tolerance = 2)
    {
        if (path.Segments.Count == 0 || path.Segments.Any(s => !s.Curved))
            return false;
        for (var i = 0; i + 1 < path.Segments.Count; i++)
        {
            var (before, after, joint) = (path.Segments[i].Control2!.Value, path.Segments[i + 1].Control1!.Value, path.Segments[i].End);
            if (Math.Abs(before.X + after.X - 2 * joint.X) > 2 * tolerance || Math.Abs(before.Y + after.Y - 2 * joint.Y) > 2 * tolerance)
                return false;
        }
        return true;
    }

    /// <summary>
    /// A path from its controls in 한글's way: two controls make a cubic; with more, each pair makes one, joined halfway
    /// between neighbouring controls; one control is a quadratic curve, none a straight line.
    /// </summary>
    public static HPath FromControls(HPoint start, HPoint end, IReadOnlyList<HPoint> controls)
    {
        if (controls.Count == 0)
            return new HPath(start, [new HPathSegment(end)]);
        if (controls.Count == 1)
        {
            var q = controls[0];
            return new HPath(start, [new HPathSegment(end, Third(start, q), Third(end, q))]);
        }
        var segments = new List<HPathSegment>();
        for (var i = 0; i + 1 < controls.Count && segments.Count < MaxSegments; i += 2)
        {
            var (c1, c2) = (controls[i], controls[i + 1]);
            // An odd last control: the last cubic ends at the end through it.
            var last = i + 3 >= controls.Count;
            var to = last ? end : Middle(c2, controls[i + 2]);
            segments.Add(new HPathSegment(to, c1, c2));
            if (last && i + 2 < controls.Count)
            {
                var q = controls[i + 2];
                segments[^1] = new HPathSegment(Middle(c2, q), c1, c2);
                segments.Add(new HPathSegment(end, Third(segments[^1].End, q), Third(end, q)));
            }
        }
        return new HPath(start, segments);
    }

    /// <summary>All the points of the path in order: the start, then each piece's controls (of a curve) and end.</summary>
    public static IEnumerable<HPoint> Points(HPath path)
    {
        yield return path.Start;
        foreach (var segment in path.Segments)
        {
            if (segment is { Control1: { } c1, Control2: { } c2 })
            {
                yield return c1;
                yield return c2;
            }
            yield return segment.End;
        }
    }

    private static HPoint Middle(HPoint a, HPoint b) => new(Round((a.X + (double)b.X) / 2), Round((a.Y + (double)b.Y) / 2));

    /// <summary>The cubic control of a quadratic curve: two thirds of the way from its end to its control.</summary>
    private static HPoint Third(HPoint end, HPoint control) => new(Round(end.X + (control.X - end.X) * 2 / 3.0), Round(end.Y + (control.Y - end.Y) * 2 / 3.0));

    private static SKPoint Lerp(SKPoint from, SKPoint to, float t) => new(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);

    internal static int Round(double value) => (int)Math.Round(Math.Clamp(value, -1e9, 1e9));
}
