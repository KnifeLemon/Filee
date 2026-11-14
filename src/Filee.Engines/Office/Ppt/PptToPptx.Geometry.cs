// Custom geometry ([MS-ODRAW] 2.3.6): a freeform, scribble or other shape that isn't a preset (MSOSPT 0, "not
// primitive") keeps its outline as vertices (pVertices, an IMsoArray of POINTs) in the geometry space geoLeft / geoTop /
// geoRight / geoBottom, read by its segments (pSegmentInfo, an IMsoArray of MSOPATHINFO: type in the top 3 bits, count in
// the other 13). It becomes DrawingML custom geometry (a:custGeom) with one a:path per msopathEnd, in the same space.
//
// - Segments: lineTo (one point each), curveTo (three each, a cubic Bézier), moveTo, close, end.
// - Escapes (MSOPATHESCAPEINFO: code in bits 8–12, point count in the low byte): noFill and noLine for the path they
//   are in; elliptical quadrants (a quarter ellipse to each point, starting along x or y and turning between the two);
//   arcTo / clockwiseArcTo / arc / clockwiseArc (bounding box, start and end point) as Béziers; quadratic Béziers. The
//   editing hints (auto, corner, smooth, symmetric line or curve, freeform, fill and line colour) are skipped with their
//   points. An escape Filee doesn't draw (angle ellipses, client escapes) or geometry through formulas (pGuides) keeps
//   the shape out of the translation (Dropped "freeform shapes").
// - Without segment information, shapePath says what the vertices are: lines or Béziers, open or closed.
//
// Hostile input: every count comes from the arrays, which lie inside their property table; points are read only while
// there are any left, and all the points written for a deck are capped (MaxPathPoints).

using System.Globalization;
using System.Text;
using Filee.Engines.Documents;
using Filee.Engines.Office.OfficeArt;

namespace Filee.Engines.Office.Ppt;

internal sealed partial class PptToPptx
{
    private const int ShapePath = 0x0144;
    private const int Guides = 0x0156;

    /// <summary>Path points written for a whole deck at most (an arc counts as the Béziers it becomes).</summary>
    public const int MaxPathPoints = 1_000_000;

    /// <summary>The Bézier handle length of a quarter circle, as a fraction of its radius.</summary>
    private const double Kappa = 0.5522847498307936;

    private int _pathPoints;

    /// <summary>A complex property of the shape or, when it has none, of the master shape it inherits from.</summary>
    private ArraySegment<byte>? ComplexOf(OfficeArtShape shape, int pid)
    {
        var current = shape;
        for (var level = 0; level <= MaxMasterChain; level++)
        {
            if (current.Complex.TryGetValue(pid, out var at))
                return new ArraySegment<byte>(_file.Stream, at.Start, at.Length);
            if ((current.Flags & HaveMaster) == 0 || !current.Properties.TryGetValue(MasterShape, out var spid)
                || MasterShapeOf((int)spid) is not { } master || master == current)
                return null;
            current = master;
        }
        return null;
    }

    /// <summary>The elements of an IMsoArray (nElems, nElemsAlloc, cbElem, data), as many as fit; cbElem 0xFFF0 means 4 bytes.</summary>
    private static List<ArraySegment<byte>> ArrayElements(ArraySegment<byte> data)
    {
        var elements = new List<ArraySegment<byte>>();
        if (data.Count < 6)
            return elements;
        var span = data.AsSpan();
        var count = span[0] | span[1] << 8;
        var size = span[4] | span[5] << 8;
        if (size == 0xFFF0)
            size = 4;
        if (size is <= 0 or > 64)
            return elements;
        for (var i = 0; i < count && 6 + (i + 1) * size <= data.Count; i++)
            elements.Add(data.Slice(6 + i * size, size));
        return elements;
    }

    /// <summary>
    /// The a:custGeom of a shape's vertices; null when it has none, or a part of it Filee can't draw (the shape is then
    /// left out and named).
    /// </summary>
    private string? CustomGeometry(OfficeArtShape shape, Dictionary<int, uint> properties)
    {
        if (ComplexOf(shape, OfficeArtProperties.Vertices) is not { } vertexData || ComplexOf(shape, Guides) is not null)
            return null;
        var points = new List<(double X, double Y)>();
        foreach (var element in ArrayElements(vertexData))
        {
            var e = element.AsSpan();
            points.Add(e.Length >= 8
                ? (BitConverter.ToInt32(e), BitConverter.ToInt32(e[4..]))
                : (e[0] | e[1] << 8, e[2] | e[3] << 8));
        }
        if (points.Count == 0)
            return null;
        var left = (double)(int)properties.GetValueOrDefault(OfficeArtProperties.GeoLeft, 0u);
        var top = (double)(int)properties.GetValueOrDefault(OfficeArtProperties.GeoTop, 0u);
        var width = Math.Max(1, (int)properties.GetValueOrDefault(OfficeArtProperties.GeoRight, 21600u) - left);
        var height = Math.Max(1, (int)properties.GetValueOrDefault(OfficeArtProperties.GeoBottom, 21600u) - top);

        var segments = ComplexOf(shape, OfficeArtProperties.SegmentInfo) is { } segmentData
            ? ArrayElements(segmentData).Select(s => (ushort)(s[0] | s[1] << 8)).ToList()
            : DefaultSegments(properties.GetValueOrDefault(ShapePath, 4u), points.Count);
        var builder = new PathBuilder(this, left, top, width, height);
        return builder.Build(points, segments) ? builder.ToXml() : Drop("freeform shapes", null);
    }

    private string? Drop(string feature, string? result)
    {
        Drop(feature);
        return result;
    }

    /// <summary>The segments shapePath means when there is no segment information: lines or Béziers, open or closed.</summary>
    private static List<ushort> DefaultSegments(uint shapePath, int points)
    {
        var curves = shapePath is 2 or 3;
        var closed = shapePath is 1 or 3;
        var rest = points - 1;
        var list = new List<ushort> { 0x4000 }; // moveTo
        // Segments of at most 0x1FFF each (13 bits) until the points run out.
        for (var left = curves ? rest / 3 : rest; left > 0; left -= 0x1FFF)
            list.Add((ushort)((curves ? 0x2000 : 0) | Math.Min(left, 0x1FFF)));
        if (closed)
            list.Add(0x6001);
        list.Add(0x8000);
        return list;
    }

    /// <summary>Builds the a:path elements of a custom geometry from its points and segments.</summary>
    private sealed class PathBuilder(PptToPptx owner, double left, double top, double width, double height)
    {
        private readonly List<StringBuilder> _paths = [];
        private StringBuilder _current = new();
        private bool _noFill;
        private bool _noLine;
        private bool _open;
        private (double X, double Y) _at;
        private (double X, double Y) _start;

        public bool Build(List<(double X, double Y)> points, List<ushort> segments)
        {
            var next = 0;
            bool Take(int count) => next + count <= points.Count;
            foreach (var segment in segments)
            {
                var type = segment >> 13;
                var count = segment & 0x1FFF;
                switch (type)
                {
                    case 0: // lineTo
                        for (var i = 0; i < Math.Max(1, count) && Take(1); i++)
                            LineTo(points[next++]);
                        break;
                    case 1: // curveTo
                        for (var i = 0; i < Math.Max(1, count) && Take(3); i++)
                        {
                            CubicTo(points[next], points[next + 1], points[next + 2]);
                            next += 3;
                        }
                        break;
                    case 2: // moveTo
                        if (Take(1))
                            MoveTo(points[next++]);
                        break;
                    case 3: // close
                        if (_open)
                        {
                            _current.Append("<a:close/>");
                            _at = _start;
                        }
                        break;
                    case 4: // end
                        EndPath();
                        break;
                    case 5: // escape
                        var code = (segment >> 8) & 0x1F;
                        var used = segment & 0xFF;
                        if (!Escape(code, used, points, ref next))
                            return false;
                        break;
                    default: // client escapes
                        return false;
                }
            }
            EndPath();
            return _paths.Count > 0;
        }

        private bool Escape(int code, int used, List<(double X, double Y)> points, ref int next)
        {
            var available = Math.Min(used, points.Count - next);
            switch (code)
            {
                case 0x7 or 0x8: // elliptical quadrants, starting along x (7) or y (8), turning each time
                    var alongX = code == 0x7;
                    for (var i = 0; i < available; i++, alongX = !alongX)
                        Quadrant(points[next + i], alongX);
                    break;
                case 0x3 or 0x4 or 0x5 or 0x6: // arcTo, arc, clockwiseArcTo, clockwiseArc: box corners, start, end
                    for (var i = 0; i + 4 <= available; i += 4)
                        Arc(points[next + i], points[next + i + 1], points[next + i + 2], points[next + i + 3], lineTo: code is 0x3 or 0x5, clockwise: code is 0x5 or 0x6);
                    break;
                case 0x9: // quadratic Béziers: control, end
                    for (var i = 0; i + 2 <= available; i += 2)
                        QuadTo(points[next + i], points[next + i + 1]);
                    break;
                case 0xA:
                    _noFill = true;
                    break;
                case 0xB:
                    _noLine = true;
                    break;
                case 0x0 or (>= 0xC and <= 0x16): // extension and editing hints
                    break;
                default: // angle ellipses
                    return false;
            }
            next += available;
            return true;
        }

        private string Point((double X, double Y) p)
        {
            if (++owner._pathPoints > MaxPathPoints)
                throw DocumentLimits.Exceeded($"The presentation's freeform shapes have more than {DocumentLimits.Count(MaxPathPoints)} points.");
            var x = (long)Math.Round(Math.Clamp(p.X - left, -1e9, 1e9));
            var y = (long)Math.Round(Math.Clamp(p.Y - top, -1e9, 1e9));
            return string.Create(CultureInfo.InvariantCulture, $"<a:pt x=\"{x}\" y=\"{y}\"/>");
        }

        private void MoveTo((double X, double Y) p)
        {
            _current.Append("<a:moveTo>").Append(Point(p)).Append("</a:moveTo>");
            _at = _start = p;
            _open = true;
        }

        private void EnsureOpen()
        {
            if (!_open)
                MoveTo(_at);
        }

        private void LineTo((double X, double Y) p)
        {
            EnsureOpen();
            _current.Append("<a:lnTo>").Append(Point(p)).Append("</a:lnTo>");
            _at = p;
        }

        private void CubicTo((double X, double Y) c1, (double X, double Y) c2, (double X, double Y) end)
        {
            EnsureOpen();
            _current.Append("<a:cubicBezTo>").Append(Point(c1)).Append(Point(c2)).Append(Point(end)).Append("</a:cubicBezTo>");
            _at = end;
        }

        private void QuadTo((double X, double Y) control, (double X, double Y) end)
        {
            EnsureOpen();
            _current.Append("<a:quadBezTo>").Append(Point(control)).Append(Point(end)).Append("</a:quadBezTo>");
            _at = end;
        }

        /// <summary>A quarter ellipse from the current point to <paramref name="to"/>, leaving along x or along y.</summary>
        private void Quadrant((double X, double Y) to, bool alongX)
        {
            EnsureOpen();
            var from = _at;
            var (dx, dy) = (to.X - from.X, to.Y - from.Y);
            if (alongX)
                CubicTo((from.X + dx * Kappa, from.Y), (to.X, to.Y - dy * Kappa), to);
            else
                CubicTo((from.X, from.Y + dy * Kappa), (to.X - dx * Kappa, to.Y), to);
        }

        /// <summary>
        /// The arc of the ellipse in the box (<paramref name="a"/>, <paramref name="b"/>) from the ray to
        /// <paramref name="start"/> to the ray to <paramref name="end"/>, counter-clockwise (as seen) or clockwise, joined to the
        /// current point by a line or starting a new figure.
        /// </summary>
        private void Arc((double X, double Y) a, (double X, double Y) b, (double X, double Y) start, (double X, double Y) end, bool lineTo, bool clockwise)
        {
            var (cx, cy) = ((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            var (rx, ry) = (Math.Abs(b.X - a.X) / 2, Math.Abs(b.Y - a.Y) / 2);
            if (rx < 1e-9 || ry < 1e-9)
            {
                if (lineTo)
                    LineTo(end);
                else
                    MoveTo(end);
                return;
            }
            // Parameter angles (y down: growing angles turn clockwise as seen).
            var from = Math.Atan2((start.Y - cy) / ry, (start.X - cx) / rx);
            var to = Math.Atan2((end.Y - cy) / ry, (end.X - cx) / rx);
            var sweep = to - from;
            if (clockwise)
            {
                while (sweep <= 0)
                    sweep += 2 * Math.PI;
            }
            else
            {
                while (sweep >= 0)
                    sweep -= 2 * Math.PI;
            }
            (double X, double Y) On(double t) => (cx + rx * Math.Cos(t), cy + ry * Math.Sin(t));
            if (lineTo)
                LineTo(On(from));
            else
                MoveTo(On(from));
            var pieces = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 2) - 1e-9));
            var step = sweep / pieces;
            var k = 4.0 / 3 * Math.Tan(step / 4);
            for (var i = 0; i < pieces; i++)
            {
                var t0 = from + i * step;
                var t1 = t0 + step;
                var p0 = On(t0);
                var p1 = On(t1);
                CubicTo((p0.X - k * rx * Math.Sin(t0), p0.Y + k * ry * Math.Cos(t0)),
                    (p1.X + k * rx * Math.Sin(t1), p1.Y - k * ry * Math.Cos(t1)), p1);
            }
        }

        private void EndPath()
        {
            if (_current.Length > 0)
            {
                var path = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
                    $"<a:path w=\"{(long)Math.Round(width)}\" h=\"{(long)Math.Round(height)}\"{(_noFill ? " fill=\"none\"" : "")}{(_noLine ? " stroke=\"0\"" : "")}>"));
                _paths.Add(path.Append(_current).Append("</a:path>"));
            }
            _current = new StringBuilder();
            _noFill = _noLine = _open = false;
        }

        public string ToXml()
        {
            var xml = new StringBuilder("<a:custGeom><a:avLst/><a:gdLst/><a:ahLst/><a:cxnLst/><a:rect l=\"l\" t=\"t\" r=\"r\" b=\"b\"/><a:pathLst>");
            foreach (var path in _paths)
                xml.Append(path);
            return xml.Append("</a:pathLst></a:custGeom>").ToString();
        }
    }
}
