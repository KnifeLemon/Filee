// The image a Gerber file describes, as GerberReader builds it: graphical objects in file order, each dark (adds) or
// clear (erases what came before it). Coordinates are millimetres with Y up. Every object can write itself as SVG and
// give its outline as a Skia path, which GerberSvg needs to subtract clear objects from the dark ones under them.

using System.Globalization;
using System.Text;
using SkiaSharp;

namespace Filee.Engines.Vector.Gerber;

/// <summary>A point in millimetres, Y up.</summary>
internal readonly record struct GerberPoint(double X, double Y);

/// <summary>An axis-aligned box in millimetres.</summary>
internal readonly record struct GerberBounds(double MinX, double MinY, double MaxX, double MaxY)
{
    public static readonly GerberBounds Empty = new(double.PositiveInfinity, double.PositiveInfinity, double.NegativeInfinity, double.NegativeInfinity);

    public bool IsEmpty => MinX > MaxX || MinY > MaxY;

    public GerberBounds Union(GerberBounds other) =>
        new(Math.Min(MinX, other.MinX), Math.Min(MinY, other.MinY), Math.Max(MaxX, other.MaxX), Math.Max(MaxY, other.MaxY));

    public GerberBounds Include(double x, double y) => new(Math.Min(MinX, x), Math.Min(MinY, y), Math.Max(MaxX, x), Math.Max(MaxY, y));

    public GerberBounds Inflate(double by) => IsEmpty ? this : new(MinX - by, MinY - by, MaxX + by, MaxY + by);

    public bool Intersects(GerberBounds other) =>
        !IsEmpty && !other.IsEmpty && MinX <= other.MaxX && other.MinX <= MaxX && MinY <= other.MaxY && other.MinY <= MaxY;

    /// <summary>The box around this one's corners mapped by <paramref name="matrix"/>.</summary>
    public GerberBounds Transform(SKMatrix matrix)
    {
        if (IsEmpty)
            return this;
        var result = Empty;
        foreach (var (x, y) in new[] { (MinX, MinY), (MaxX, MinY), (MaxX, MaxY), (MinX, MaxY) })
        {
            var p = matrix.MapPoint((float)x, (float)y);
            result = result.Include(p.X, p.Y);
        }
        return result;
    }

    public static GerberBounds Of(SKRect rect) => rect.IsEmpty ? Empty : new(rect.Left, rect.Top, rect.Right, rect.Bottom);
}

/// <summary>A piece of a contour, from the previous piece's end: a straight line, or an arc about a centre.</summary>
internal readonly record struct GerberSegment(GerberPoint End, bool IsArc = false, GerberPoint Center = default, bool CounterClockwise = false, bool FullCircle = false);

/// <summary>A sequence of segments from a start point: a stroked track, or a region's boundary.</summary>
internal sealed class GerberContour(GerberPoint start)
{
    public GerberPoint Start { get; } = start;

    public List<GerberSegment> Segments { get; } = [];

    public GerberPoint End => Segments.Count == 0 ? Start : Segments[^1].End;

    /// <summary>The angle an arc turns through, in radians, positive.</summary>
    public static double Sweep(GerberPoint from, GerberSegment arc)
    {
        if (arc.FullCircle)
            return 2 * Math.PI;
        var a0 = Math.Atan2(from.Y - arc.Center.Y, from.X - arc.Center.X);
        var a1 = Math.Atan2(arc.End.Y - arc.Center.Y, arc.End.X - arc.Center.X);
        var sweep = arc.CounterClockwise ? a1 - a0 : a0 - a1;
        while (sweep < 0)
            sweep += 2 * Math.PI;
        while (sweep > 2 * Math.PI)
            sweep -= 2 * Math.PI;
        return sweep;
    }

    public GerberBounds Bounds()
    {
        var bounds = GerberBounds.Empty.Include(Start.X, Start.Y);
        var from = Start;
        foreach (var segment in Segments)
        {
            bounds = bounds.Include(segment.End.X, segment.End.Y);
            if (segment.IsArc)
            {
                // The arc's extreme points where it crosses an axis through its centre.
                var r = Radius(from, segment);
                var a0 = Math.Atan2(from.Y - segment.Center.Y, from.X - segment.Center.X);
                var sweep = Sweep(from, segment);
                for (var k = 0; k < 4; k++)
                {
                    var axis = k * Math.PI / 2;
                    var turn = segment.CounterClockwise ? axis - a0 : a0 - axis;
                    turn = ((turn % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);
                    if (turn <= sweep)
                        bounds = bounds.Include(segment.Center.X + r * Math.Cos(axis), segment.Center.Y + r * Math.Sin(axis));
                }
            }
            from = segment.End;
        }
        return bounds;
    }

    /// <summary>Twice the signed area (positive counter-clockwise), arcs measured along their chord and sagitta.</summary>
    public double SignedArea()
    {
        double area = 0;
        var from = Start;
        foreach (var segment in Segments)
        {
            if (segment.IsArc)
            {
                // Walk the arc in a few steps; the orientation is all that's needed.
                var steps = 8;
                var r = Radius(from, segment);
                var a0 = Math.Atan2(from.Y - segment.Center.Y, from.X - segment.Center.X);
                var sweep = Sweep(from, segment) * (segment.CounterClockwise ? 1 : -1);
                var prev = from;
                for (var i = 1; i <= steps; i++)
                {
                    var a = a0 + sweep * i / steps;
                    var p = i == steps ? segment.End : new GerberPoint(segment.Center.X + r * Math.Cos(a), segment.Center.Y + r * Math.Sin(a));
                    area += prev.X * p.Y - p.X * prev.Y;
                    prev = p;
                }
            }
            else
            {
                area += from.X * segment.End.Y - segment.End.X * from.Y;
            }
            from = segment.End;
        }
        return area + from.X * Start.Y - Start.X * from.Y;
    }

    /// <summary>The same boundary walked the other way.</summary>
    public GerberContour Reversed()
    {
        var reversed = new GerberContour(End);
        for (var i = Segments.Count - 1; i >= 0; i--)
        {
            var to = i == 0 ? Start : Segments[i - 1].End;
            var s = Segments[i];
            reversed.Segments.Add(s with { End = to, CounterClockwise = !s.CounterClockwise });
        }
        return reversed;
    }

    public static double Radius(GerberPoint from, GerberSegment arc)
    {
        var r0 = Math.Sqrt((from.X - arc.Center.X) * (from.X - arc.Center.X) + (from.Y - arc.Center.Y) * (from.Y - arc.Center.Y));
        var r1 = Math.Sqrt((arc.End.X - arc.Center.X) * (arc.End.X - arc.Center.X) + (arc.End.Y - arc.Center.Y) * (arc.End.Y - arc.Center.Y));
        return (r0 + r1) / 2;
    }

    /// <summary>SVG path data; arcs as elliptical-arc commands (a full circle as two halves).</summary>
    public void AppendSvg(StringBuilder d, bool close)
    {
        d.Append('M').Append(Svg.N(Start.X)).Append(' ').Append(Svg.N(Start.Y));
        var from = Start;
        foreach (var segment in Segments)
        {
            if (!segment.IsArc)
            {
                d.Append('L').Append(Svg.N(segment.End.X)).Append(' ').Append(Svg.N(segment.End.Y));
            }
            else
            {
                var r = Radius(from, segment);
                var sweepFlag = segment.CounterClockwise ? '1' : '0';
                var sweep = Sweep(from, segment);
                if (segment.FullCircle || sweep > 2 * Math.PI - 1e-9)
                {
                    // Opposite point first: SVG can't draw a whole circle in one arc command.
                    var opposite = new GerberPoint(2 * segment.Center.X - from.X, 2 * segment.Center.Y - from.Y);
                    AppendArc(d, r, false, sweepFlag, opposite);
                    AppendArc(d, r, false, sweepFlag, segment.End);
                }
                else
                {
                    AppendArc(d, r, sweep > Math.PI, sweepFlag, segment.End);
                }
            }
            from = segment.End;
        }
        if (close)
            d.Append('Z');
    }

    private static void AppendArc(StringBuilder d, double r, bool large, char sweep, GerberPoint to) =>
        d.Append('A').Append(Svg.N(r)).Append(' ').Append(Svg.N(r)).Append(" 0 ").Append(large ? '1' : '0').Append(' ').Append(sweep)
            .Append(' ').Append(Svg.N(to.X)).Append(' ').Append(Svg.N(to.Y));

    /// <summary>Adds the contour to a Skia path.</summary>
    public void AddTo(SKPath path, bool close)
    {
        path.MoveTo((float)Start.X, (float)Start.Y);
        var from = Start;
        foreach (var segment in Segments)
        {
            if (!segment.IsArc)
            {
                path.LineTo((float)segment.End.X, (float)segment.End.Y);
            }
            else
            {
                var r = Radius(from, segment);
                var oval = new SKRect((float)(segment.Center.X - r), (float)(segment.Center.Y - r), (float)(segment.Center.X + r), (float)(segment.Center.Y + r));
                var start = Math.Atan2(from.Y - segment.Center.Y, from.X - segment.Center.X) * 180 / Math.PI;
                var sweep = Sweep(from, segment) * 180 / Math.PI * (segment.CounterClockwise ? 1 : -1);
                // Halves, so a full circle is not read as "no turn".
                path.ArcTo(oval, (float)start, (float)(sweep / 2), false);
                path.ArcTo(oval, (float)(start + sweep / 2), (float)(sweep / 2), false);
                path.LineTo((float)segment.End.X, (float)segment.End.Y);
            }
            from = segment.End;
        }
        if (close)
            path.Close();
    }
}

/// <summary>How an aperture is placed: mirrored (LM), then rotated (LR, degrees counter-clockwise), then scaled (LS).</summary>
internal readonly record struct GerberPlacement(bool MirrorX, bool MirrorY, double Rotation, double Scale)
{
    public static readonly GerberPlacement Identity = new(false, false, 0, 1);

    public bool IsIdentity => !MirrorX && !MirrorY && Rotation == 0 && Scale == 1;

    public SKMatrix Matrix(GerberPoint at)
    {
        var m = SKMatrix.CreateTranslation((float)at.X, (float)at.Y);
        m = m.PreConcat(SKMatrix.CreateScale((float)Scale, (float)Scale));
        m = m.PreConcat(SKMatrix.CreateRotationDegrees((float)Rotation));
        return m.PreConcat(SKMatrix.CreateScale(MirrorX ? -1 : 1, MirrorY ? -1 : 1));
    }

    public string SvgTransform(GerberPoint at)
    {
        var s = new StringBuilder();
        s.Append("translate(").Append(Svg.N(at.X)).Append(' ').Append(Svg.N(at.Y)).Append(')');
        if (Scale != 1)
            s.Append(" scale(").Append(Svg.N(Scale)).Append(')');
        if (Rotation != 0)
            s.Append(" rotate(").Append(Svg.N(Rotation)).Append(')');
        if (MirrorX || MirrorY)
            s.Append(" scale(").Append(MirrorX ? "-1" : "1").Append(' ').Append(MirrorY ? "-1" : "1").Append(')');
        return s.ToString();
    }

    /// <summary>A point of the aperture's own outline, placed.</summary>
    public GerberPoint Apply(double x, double y)
    {
        if (MirrorX) x = -x;
        if (MirrorY) y = -y;
        var a = Rotation * Math.PI / 180;
        double c = Math.Cos(a), s = Math.Sin(a);
        return new GerberPoint((x * c - y * s) * Scale, (x * s + y * c) * Scale);
    }
}

/// <summary>A flashable shape: a standard aperture or a macro, its outline centred on the flash point.</summary>
internal sealed class GerberAperture(int code, SKPath outline, string svg, double circleDiameter, IReadOnlyList<(double X, double Y)>? convexOutline)
{
    public int Code { get; } = code;

    /// <summary>The shape, in millimetres about the flash point.</summary>
    public SKPath Outline { get; } = outline;

    /// <summary>An SVG element drawing the shape without a colour of its own (it inherits the layer's fill).</summary>
    public string SvgElement { get; } = svg;

    /// <summary>Diameter of a plain circle aperture (draws are strokes this wide), else 0.</summary>
    public double CircleDiameter { get; } = circleDiameter;

    /// <summary>Corners of a convex aperture other than a circle (rectangle, obround, polygon), for draws that sweep it.</summary>
    public IReadOnlyList<(double X, double Y)>? ConvexOutline { get; } = convexOutline;

    public GerberBounds Bounds { get; } = GerberBounds.Of(outline.Bounds);

    public bool IsEmpty => Outline.IsEmpty;
}

/// <summary>An aperture block (%AB…%) or a step-and-repeat block (%SR…%): objects placed again as a whole.</summary>
internal sealed class GerberBlock(int id)
{
    public int Id { get; } = id;

    public List<GerberObject> Objects { get; } = [];

    /// <summary>The block split where the polarity changes, each run defined once in the SVG and placed by reference.</summary>
    public List<Run> Runs { get; } = [];

    public sealed class Run(GerberBlock block, int index, bool clear, List<GerberObject> objects)
    {
        public string SvgId => $"b{block.Id}r{index}";

        public bool Clear { get; } = clear;

        public List<GerberObject> Objects { get; } = objects;

        public GerberBounds Bounds { get; } = objects.Aggregate(GerberBounds.Empty, (b, o) => b.Union(o.Bounds));

        /// <summary>The run's size with nested blocks counted in full, saturating instead of overflowing.</summary>
        public long Weight { get; } = objects.Aggregate(0L, (sum, o) => sum > long.MaxValue / 4 || o.Weight > long.MaxValue / 4 ? long.MaxValue / 2 : sum + o.Weight);

        private SKPath? _outline;

        /// <summary>The union of the run's outlines, made once.</summary>
        public SKPath Outline()
        {
            if (_outline is not null)
                return _outline;
            using var builder = new SKPath.OpBuilder();
            foreach (var o in Objects)
            {
                using var path = o.Outline();
                builder.Add(path, SKPathOp.Union);
            }
            _outline = new SKPath();
            if (!builder.Resolve(_outline))
            {
                // Overlapping outlines still fill correctly with the non-zero rule.
                foreach (var o in Objects)
                {
                    using var path = o.Outline();
                    _outline.AddPath(path);
                }
            }
            return _outline;
        }
    }

    /// <summary>Splits the objects into runs of one polarity; called once the block is closed.</summary>
    public void Seal()
    {
        var start = 0;
        for (var i = 1; i <= Objects.Count; i++)
        {
            if (i < Objects.Count && Objects[i].Clear == Objects[start].Clear)
                continue;
            Runs.Add(new Run(this, Runs.Count, Objects[start].Clear, Objects.GetRange(start, i - start)));
            start = i;
        }
    }
}

/// <summary>A graphical object of the image.</summary>
internal abstract class GerberObject(bool clear)
{
    /// <summary>Clear polarity: erases the objects before it.</summary>
    public bool Clear { get; set; } = clear;

    public abstract GerberBounds Bounds { get; }

    /// <summary>A rough measure of the outline's size, for the path-operation budget.</summary>
    public abstract long Weight { get; }

    /// <summary>The object as an SVG element without colour (the layer's fill and stroke are inherited).</summary>
    public abstract void WriteSvg(StringBuilder svg);

    /// <summary>The object's filled outline.</summary>
    public abstract SKPath Outline();
}

/// <summary>Draws with a circle aperture: tracks of one width as stroked polylines and arcs, round ends.</summary>
internal sealed class GerberStroke(bool clear, double width) : GerberObject(clear)
{
    public double Width { get; } = width;

    public List<GerberContour> Contours { get; } = [];

    public int SegmentCount { get; set; }

    private GerberBounds? _bounds;

    public override GerberBounds Bounds => _bounds ??= Contours.Aggregate(GerberBounds.Empty, (b, c) => b.Union(c.Bounds())).Inflate(Width / 2);

    public override long Weight => SegmentCount + Contours.Count;

    public override void WriteSvg(StringBuilder svg)
    {
        svg.Append("<path d=\"");
        foreach (var contour in Contours)
            contour.AppendSvg(svg, false);
        svg.Append("\" fill=\"none\" stroke-width=\"").Append(Svg.N(Width)).Append("\"/>");
    }

    public override SKPath Outline()
    {
        using var centre = new SKPath();
        foreach (var contour in Contours)
            contour.AddTo(centre, false);
        using var paint = new SKPaint { IsStroke = true, StrokeWidth = (float)Width, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        var outline = new SKPath();
        paint.GetFillPath(centre, outline);
        return outline;
    }
}

/// <summary>A filled area: a region (G36/G37), a draw swept by a rectangle, or a dot.</summary>
internal sealed class GerberRegion(bool clear, List<GerberContour> contours) : GerberObject(clear)
{
    /// <summary>Closed contours, all counter-clockwise, so the non-zero rule fills their union.</summary>
    public List<GerberContour> Contours { get; } = [.. contours.Select(c => c.SignedArea() < 0 ? c.Reversed() : c)];

    private GerberBounds? _bounds;

    public override GerberBounds Bounds => _bounds ??= Contours.Aggregate(GerberBounds.Empty, (b, c) => b.Union(c.Bounds()));

    public override long Weight => Contours.Sum(c => c.Segments.Count + 1);

    public override void WriteSvg(StringBuilder svg)
    {
        svg.Append("<path d=\"");
        foreach (var contour in Contours)
            contour.AppendSvg(svg, true);
        svg.Append("\" stroke=\"none\"/>");
    }

    public override SKPath Outline()
    {
        var path = new SKPath { FillType = SKPathFillType.Winding };
        foreach (var contour in Contours)
            contour.AddTo(path, true);
        return path;
    }
}

/// <summary>A flash (D03) of an aperture.</summary>
internal sealed class GerberFlash(bool clear, GerberAperture aperture, GerberPoint at, GerberPlacement placement) : GerberObject(clear)
{
    public GerberAperture Aperture { get; } = aperture;

    public override GerberBounds Bounds { get; } = aperture.Bounds.Transform(placement.Matrix(at));

    public override long Weight => Math.Max(4, Aperture.Outline.PointCount);

    public override void WriteSvg(StringBuilder svg) =>
        svg.Append("<use xlink:href=\"#a").Append(Aperture.Code).Append("\" transform=\"").Append(placement.SvgTransform(at)).Append("\"/>");

    public override SKPath Outline()
    {
        var path = new SKPath(Aperture.Outline);
        path.Transform(placement.Matrix(at));
        return path;
    }
}

/// <summary>One polarity run of a block placed at a point: a block aperture's flash or a step-and-repeat copy.</summary>
internal sealed class GerberBlockFlash(bool clear, GerberBlock.Run run, GerberPoint at, GerberPlacement placement) : GerberObject(clear)
{
    public GerberBlock.Run Run { get; } = run;

    public override GerberBounds Bounds { get; } = run.Bounds.Transform(placement.Matrix(at));

    public override long Weight => Run.Weight;

    public override void WriteSvg(StringBuilder svg) =>
        svg.Append("<use xlink:href=\"#").Append(Run.SvgId).Append("\" transform=\"").Append(placement.SvgTransform(at)).Append("\"/>");

    public override SKPath Outline()
    {
        var path = new SKPath(Run.Outline());
        path.Transform(placement.Matrix(at));
        return path;
    }
}

/// <summary>A dark outline left after clear objects were subtracted from a dark object.</summary>
internal sealed class GerberCutOut(SKPath outline) : GerberObject(false)
{
    private readonly SKPath _outline = outline;

    public override GerberBounds Bounds { get; } = GerberBounds.Of(outline.Bounds);

    public override long Weight => _outline.PointCount;

    public override void WriteSvg(StringBuilder svg)
    {
        svg.Append("<path d=\"");
        Svg.AppendPathData(svg, _outline);
        svg.Append("\" stroke=\"none\"");
        if (_outline.FillType is SKPathFillType.EvenOdd or SKPathFillType.InverseEvenOdd)
            svg.Append(" fill-rule=\"evenodd\"");
        svg.Append("/>");
    }

    public override SKPath Outline() => new(_outline);
}

/// <summary>Number formatting and Skia path serialisation for the SVG.</summary>
internal static class Svg
{
    /// <summary>Millimetres to 0.01 µm, invariant culture, no exponent.</summary>
    public static string N(double value)
    {
        var rounded = Math.Round(value, 5);
        return (rounded == 0 ? 0 : rounded).ToString("0.#####", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A Skia path as SVG path data. Conics (Skia's circles and arcs) become eight quadratic pieces each, so a circle
    /// stays round to well under a micrometre; SKPath.ToSvgPathData would split them far more coarsely.
    /// </summary>
    public static void AppendPathData(StringBuilder d, SKPath path)
    {
        using var iterator = path.CreateRawIterator();
        var points = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = iterator.Next(points)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    Point(d, 'M', points[0]);
                    break;
                case SKPathVerb.Line:
                    Point(d, 'L', points[1]);
                    break;
                case SKPathVerb.Quad:
                    Point(d, 'Q', points[1]);
                    Point(d, ' ', points[2]);
                    break;
                case SKPathVerb.Conic:
                    var quads = SKPath.ConvertConicToQuads(points[0], points[1], points[2], iterator.ConicWeight(), 3);
                    for (var i = 1; i + 1 < quads.Length; i += 2)
                    {
                        Point(d, 'Q', quads[i]);
                        Point(d, ' ', quads[i + 1]);
                    }
                    break;
                case SKPathVerb.Cubic:
                    Point(d, 'C', points[1]);
                    Point(d, ' ', points[2]);
                    Point(d, ' ', points[3]);
                    break;
                case SKPathVerb.Close:
                    d.Append('Z');
                    break;
            }
        }

        static void Point(StringBuilder d, char command, SKPoint p) => d.Append(command).Append(N(p.X)).Append(' ').Append(N(p.Y));
    }
}
