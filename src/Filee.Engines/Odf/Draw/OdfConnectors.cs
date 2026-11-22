// The route of an ODF connector (draw:connector, OpenDocument 1.3 Part 3 §10.3.10) from svg:x1 / svg:y1 to svg:x2 /
// svg:y2, for the slide reader (OdfDrawReader) and the text document reader (OdtReader):
// - With svg:d, its path, scaled alike in both directions and moved so its first point is the connector's start and
//   its last the end: LibreOffice writes it in 1/100 mm where the connector is, others relative to a view box; when the
//   path's ends coincide it is taken as 1/100 mm.
// - Without one (ODF leaves the route to the application), by draw:type: "curve" an S-curve leaving and reaching its
//   ends along the longer direction, "lines" a slanted middle between short straight ends, "standard" right angles
//   turning halfway (horizontal-vertical-horizontal, or the other way round when it is taller than wide), "line"
//   straight. Glue point directions and draw:line-skew are not followed.

using SkiaSharp;

namespace Filee.Engines.Odf.Draw;

internal static class OdfConnectors
{
    /// <summary>Path characters a connector's svg:d is read up to (a damaged file can have megabytes of them).</summary>
    public const int MaxPathChars = 65536;

    /// <summary>
    /// The connector's svg:d <paramref name="d"/> placed between <paramref name="start"/> and <paramref name="end"/>;
    /// null when there is none, it is too long or its ends can't be matched.
    /// </summary>
    /// <param name="hundredthMm">1/100 mm in the units of the ends, for a path whose ends coincide.</param>
    /// <param name="commands">Numbers read.</param>
    public static SKPath? PathData(string? d, SKPoint start, SKPoint end, double hundredthMm, int maxNumbers, out int commands)
    {
        commands = 0;
        if (d is not { Length: > 0 and <= MaxPathChars })
            return null;
        var path = OdfPathData.Parse(d, (x, y) => new SKPoint((float)x, (float)y), 1, 1, maxNumbers, out commands);
        if (path.PointCount >= 2)
        {
            var (first, last) = (path.GetPoint(0), path.LastPoint);
            var pathLength = Math.Sqrt(Math.Pow(last.X - first.X, 2) + Math.Pow(last.Y - first.Y, 2));
            var length = Math.Sqrt(Math.Pow(end.X - (double)start.X, 2) + Math.Pow(end.Y - (double)start.Y, 2));
            var scale = pathLength > 0 && length > 0 ? length / pathLength : hundredthMm;
            if (double.IsFinite(scale) && scale is > 0 and <= 1e6)
            {
                var s = (float)scale;
                path.Transform(new SKMatrix(s, 0, start.X - first.X * s, 0, s, start.Y - first.Y * s, 0, 0, 1));
                return path;
            }
        }
        path.Dispose();
        return null;
    }

    /// <summary>The connector's route by its draw:type when the file gives no path (see the header).</summary>
    public static SKPath Route(string? type, SKPoint start, SKPoint end)
    {
        var across = Math.Abs(end.X - start.X) >= Math.Abs(end.Y - start.Y);
        SKPoint At(float tx, float ty) => new(start.X + (end.X - start.X) * tx, start.Y + (end.Y - start.Y) * ty);
        var path = new SKPath();
        path.MoveTo(start);
        switch (type ?? "standard")
        {
            case "line":
                break;
            case "curve" when across:
                path.CubicTo(At(0.5f, 0), At(0.5f, 1), end);
                return path;
            case "curve":
                path.CubicTo(At(0, 0.5f), At(1, 0.5f), end);
                return path;
            case "lines" when across:
                path.LineTo(At(0.25f, 0));
                path.LineTo(At(0.75f, 1));
                break;
            case "lines":
                path.LineTo(At(0, 0.25f));
                path.LineTo(At(1, 0.75f));
                break;
            default:
                path.LineTo(across ? At(0.5f, 0) : At(0, 0.5f));
                path.LineTo(across ? At(0.5f, 1) : At(1, 0.5f));
                break;
        }
        path.LineTo(end);
        return path;
    }
}
