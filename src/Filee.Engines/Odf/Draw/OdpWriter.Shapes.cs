// The elements of a slide as ODF drawing shapes. Every element's own coordinates are mapped to the page through its
// box, turn and mirroring (as SlideRenderer draws them) and its groups' child spaces, then written back as a box with a
// turn: svg:x / svg:y / svg:width / svg:height, or draw:transform "rotate translate" for a turned one; a mirrored
// element is mirrored top to bottom (draw:mirror-vertical), a left-to-right mirror being that and half a turn.
//
// - Shapes are draw:custom-shape with their outlines as draw:enhanced-path in a svg:viewBox of the box in 1/100 pt:
//   M L C Q Z per contour, N between outlines, F / S for outlines without fill or line and LibreOffice's H I J K for
//   the darker and lighter parts of 3D-looking presets. draw:type names a plain rectangle or ellipse; the text area of
//   the geometry is draw:text-areas.
// - A straight line without fill is a draw:line between its two ends; pictures in a plain box are draw:frame with
//   draw:image; other picture fills are bitmap fills.
// - Tables are table:table in a draw:frame, with column widths, row heights, spans and covered cells, cell fills,
//   borders, insets and anchoring.
// - Groups are draw:g; their children are written in page coordinates, as ODF has them.

using System.Globalization;
using System.Text;
using Filee.Engines.Documents;
using Filee.Engines.Slides;
using SkiaSharp;

namespace Filee.Engines.Odf.Draw;

internal sealed partial class OdpWriter
{
    /// <summary>Path coordinates per point (the viewBox is the box in 1/100 pt).</summary>
    private const float PathScale = 100;

    /// <summary>Coordinates are cut at ±10 km, in 1/100 pt.</summary>
    private const double MaxCoordinate = 2_800_000_000;

    /// <summary>Where an element is on the page: its box before turning, the turn (degrees clockwise) and a top-to-bottom mirror.</summary>
    private readonly record struct Place(float X, float Y, float Width, float Height, float Rotation, bool Mirrored, SKPoint Center);

    /// <summary>The draw:layer attribute of the shapes being written (master shapes are background objects).</summary>
    private string _layer = "";

    private void Element(StringBuilder sb, SlideElement element, SKMatrix parent, int depth, bool onMaster)
    {
        _token.ThrowIfCancellationRequested();
        if (depth > _limits.MaxNestingDepth)
            throw DocumentLimits.Exceeded($"The slide's groups are nested more than {_limits.MaxNestingDepth} deep.");
        var box = element.Bounds;
        if (!(box.Width >= 0 && box.Height >= 0) || !float.IsFinite(box.Left) || !float.IsFinite(box.Top) || !float.IsFinite(box.Right) || !float.IsFinite(box.Bottom))
            return;
        _layer = onMaster ? " draw:layer=\"backgroundobjects\"" : "";
        var matrix = parent.PreConcat(Local(element));
        switch (element)
        {
            case SlideGroup group:
                {
                    var child = group.ChildBounds;
                    var scaleX = child.Width > 0 ? box.Width / child.Width : 1;
                    var scaleY = child.Height > 0 ? box.Height / child.Height : 1;
                    var inner = matrix.PreConcat(SKMatrix.CreateScale(scaleX, scaleY)).PreConcat(SKMatrix.CreateTranslation(-child.Left, -child.Top));
                    var children = new StringBuilder();
                    foreach (var item in group.Children)
                        Element(children, item, inner, depth + 1, onMaster);
                    _layer = onMaster ? " draw:layer=\"backgroundobjects\"" : "";
                    if (children.Length > 0)
                        sb.Append($"<draw:g{_layer}>").Append(children).Append("</draw:g>");
                    break;
                }
            case SlideShape shape:
                Shape(sb, shape, matrix);
                break;
            case SlideTable table:
                Table(sb, table, matrix);
                break;
        }
    }

    /// <summary>The element's own coordinates (its box from 0, 0) → its parent's, as SlideRenderer has them.</summary>
    private static SKMatrix Local(SlideElement element)
    {
        var box = element.Bounds;
        var (cx, cy) = (box.Width / 2, box.Height / 2);
        var matrix = SKMatrix.CreateTranslation(box.Left + cx, box.Top + cy);
        if (element.Rotation != 0)
            matrix = matrix.PreConcat(SKMatrix.CreateRotationDegrees(element.Rotation));
        if (element.FlipHorizontal || element.FlipVertical)
            matrix = matrix.PreConcat(SKMatrix.CreateScale(element.FlipHorizontal ? -1 : 1, element.FlipVertical ? -1 : 1));
        return matrix.PreConcat(SKMatrix.CreateTranslation(-cx, -cy));
    }

    /// <summary>The page box, turn and mirroring of a <paramref name="width"/> × <paramref name="height"/> box mapped by <paramref name="matrix"/>.</summary>
    private static Place? Decompose(SKMatrix matrix, float width, float height)
    {
        var u = matrix.MapVector(1, 0);
        var v = matrix.MapVector(0, 1);
        var center = matrix.MapPoint(width / 2, height / 2);
        var (sx, sy) = (u.Length, v.Length);
        if (!float.IsFinite(sx) || !float.IsFinite(sy) || !float.IsFinite(center.X) || !float.IsFinite(center.Y))
            return null;
        var w = width * sx;
        var h = height * sy;
        var mirrored = u.X * v.Y - u.Y * v.X < 0;
        var rotation = sx > 0 ? MathF.Atan2(u.Y, u.X) * 180 / MathF.PI
            : sy > 0 ? MathF.Atan2(v.Y, v.X) * 180 / MathF.PI - (mirrored ? -90 : 90)
            : 0;
        rotation %= 360;
        if (rotation < 0)
            rotation += 360;
        if (rotation < 1e-3f || rotation > 360 - 1e-3f)
            rotation = 0;
        return new Place(center.X - w / 2, center.Y - h / 2, w, h, rotation, mirrored, center);
    }

    /// <summary>Size and position, with draw:transform for a turned box (ODF turns counter-clockwise, about the box's corner).</summary>
    private static string Placement(Place place)
    {
        var size = $"svg:width=\"{Pt(place.Width)}\" svg:height=\"{Pt(place.Height)}\"";
        if (place.Rotation == 0)
            return $"{size} svg:x=\"{Pt(place.X)}\" svg:y=\"{Pt(place.Y)}\"";
        var radians = place.Rotation * Math.PI / 180;
        var (cos, sin) = (Math.Cos(radians), Math.Sin(radians));
        var (hx, hy) = (-place.Width / 2.0, -place.Height / 2.0);
        var corner = (X: place.Center.X + hx * cos - hy * sin, Y: place.Center.Y + hx * sin + hy * cos);
        var angle = (2 * Math.PI - radians) % (2 * Math.PI);
        return $"{size} draw:transform=\"rotate ({angle.ToString("0.#########", CultureInfo.InvariantCulture)}) translate ({Pt(corner.X)} {Pt(corner.Y)})\"";
    }

    // ───────────────────────── Shapes ─────────────────────────

    private void Shape(StringBuilder sb, SlideShape shape, SKMatrix matrix)
    {
        var (w, h) = (shape.Bounds.Width, shape.Bounds.Height);
        var text = shape.Text is { IsEmpty: false } frame ? frame : null;
        if (shape.Fill is null && shape.Line is null && text is null)
            return;

        // A straight line: its two ends on the page.
        if (text is null && shape.Line is not null && (shape.Fill is null || shape.Paths.All(p => p.Fill == PathFill.None)) && Segment(shape.Paths) is var (a, b))
        {
            var (p1, p2) = (matrix.MapPoint(a), matrix.MapPoint(b));
            if (float.IsFinite(p1.X) && float.IsFinite(p1.Y) && float.IsFinite(p2.X) && float.IsFinite(p2.Y))
            {
                var lineStyle = Graphic(" draw:fill=\"none\"" + Stroke(shape.Line));
                sb.Append($"<draw:line draw:style-name=\"{lineStyle}\"{_layer} svg:x1=\"{Pt(p1.X)}\" svg:y1=\"{Pt(p1.Y)}\" svg:x2=\"{Pt(p2.X)}\" svg:y2=\"{Pt(p2.Y)}\"/>");
                return;
            }
        }

        if (Decompose(matrix, w, h) is not { } place)
            return;

        // A picture in its box: a frame with the picture (cropped, recoloured or mirrored ones drawn out once).
        if (shape.IsPicture && text is null && shape.Fill is PictureFill { Tile: null } picture && Box(shape.Paths, w, h))
        {
            var mirrored = place.Mirrored;
            if (Picture(picture, mirrored) is { } media)
            {
                if (media.Flipped)
                    place = place with { Mirrored = false };
                var frameStyle = Graphic(" draw:fill=\"none\"" + Stroke(shape.Line) + (place.Mirrored ? " style:mirror=\"vertical\"" : "") +
                                         " draw:auto-grow-height=\"false\" draw:auto-grow-width=\"false\"");
                sb.Append($"<draw:frame draw:style-name=\"{frameStyle}\"{_layer} {Placement(place)}>")
                  .Append($"<draw:image xlink:href=\"{OdfXml.Escape(media.Path)}\" xlink:type=\"simple\" xlink:show=\"embed\" xlink:actuate=\"onLoad\"/>")
                  .Append("</draw:frame>");
                return;
            }
        }

        var path = PathData(shape.Paths);
        if (path.Length == 0 && text is null)
            return;
        var viewWidth = Math.Max(1, (long)Math.Round(w * PathScale));
        var viewHeight = Math.Max(1, (long)Math.Round(h * PathScale));
        var style = Graphic(Fill(shape.Fill, w, h) + Stroke(shape.Line) + TextFrame(text), Columns(text));
        sb.Append($"<draw:custom-shape draw:style-name=\"{style}\"{_layer} {Placement(place)}>");
        if (text is not null)
            Paragraphs(sb, text.Paragraphs);
        sb.Append($"<draw:enhanced-geometry svg:viewBox=\"0 0 {viewWidth.ToString(CultureInfo.InvariantCulture)} {viewHeight.ToString(CultureInfo.InvariantCulture)}\"")
          .Append($" draw:type=\"{GeometryType(shape.Paths, w, h)}\"");
        if (place.Mirrored)
            sb.Append(" draw:mirror-vertical=\"true\"");
        if (text?.Area is { } area && float.IsFinite(area.Left) && float.IsFinite(area.Top) && float.IsFinite(area.Right) && float.IsFinite(area.Bottom))
            sb.Append($" draw:text-areas=\"{Coordinate(area.Left)} {Coordinate(area.Top)} {Coordinate(area.Right)} {Coordinate(area.Bottom)}\"");
        if (text is not null)
        {
            // ODF turns text counter-clockwise; text running up is horizontal text turned three quarters.
            var turn = (-(text.Rotation + (text.Direction == TextDirection.Up ? 270 : 0)) % 360 + 360) % 360;
            if (float.IsFinite(turn) && Math.Abs(turn) > 1e-3 && Math.Abs(turn - 360) > 1e-3)
                sb.Append($" draw:text-rotate-angle=\"{turn.ToString("0.###", CultureInfo.InvariantCulture)}\"");
        }
        sb.Append($" draw:enhanced-path=\"{(path.Length > 0 ? path : "N")}\"/></draw:custom-shape>");
    }

    /// <summary>The two ends of a single straight open outline; null for anything else.</summary>
    private static (SKPoint A, SKPoint B)? Segment(IReadOnlyList<SlidePath> paths)
    {
        if (paths is not [{ Stroke: true } only] || only.Path.PointCount != 2 || only.Path.VerbCount != 2)
            return null;
        using var iterator = only.Path.CreateRawIterator();
        var points = new SKPoint[4];
        if (iterator.Next(points) != SKPathVerb.Move)
            return null;
        var a = points[0];
        if (iterator.Next(points) != SKPathVerb.Line)
            return null;
        return (a, points[1]);
    }

    /// <summary>A single outline that is the element's whole box.</summary>
    private static bool Box(IReadOnlyList<SlidePath> paths, float width, float height) =>
        paths is [{ Fill: PathFill.Normal } only] && only.Path.IsRect && Near(only.Path.Bounds, width, height);

    private static bool Near(SKRect bounds, float width, float height) =>
        Math.Abs(bounds.Left) < 0.5f && Math.Abs(bounds.Top) < 0.5f && Math.Abs(bounds.Right - width) < 0.5f && Math.Abs(bounds.Bottom - height) < 0.5f;

    /// <summary>draw:type: a plain rectangle or ellipse by name, anything else as a shape of its own path.</summary>
    private static string GeometryType(IReadOnlyList<SlidePath> paths, float width, float height)
    {
        if (paths is [{ Fill: PathFill.Normal, Stroke: true } only] && Near(only.Path.Bounds, width, height))
        {
            if (only.Path.IsRect)
                return "rectangle";
            if (only.Path.IsOval)
                return "ellipse";
        }
        return "non-primitive";
    }

    private static string Coordinate(double value) =>
        Math.Round(Math.Clamp(value * PathScale, -MaxCoordinate, MaxCoordinate)).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>The outlines as a draw:enhanced-path in the box's 1/100 pt.</summary>
    private static string PathData(IReadOnlyList<SlidePath> paths)
    {
        var sb = new StringBuilder();
        var points = new SKPoint[4];
        void Point(SKPoint point) => sb.Append(' ').Append(Coordinate(point.X)).Append(' ').Append(Coordinate(point.Y));
        foreach (var slidePath in paths)
        {
            if (slidePath.Fill == PathFill.None && !slidePath.Stroke)
                continue;
            var start = sb.Length;
            if (slidePath.Fill == PathFill.None)
                sb.Append("F ");
            if (!slidePath.Stroke)
                sb.Append("S ");
            sb.Append(slidePath.Fill switch
            {
                PathFill.Darken => "H ",
                PathFill.DarkenLess => "I ",
                PathFill.Lighten => "J ",
                PathFill.LightenLess => "K ",
                _ => "",
            });
            var drawn = false;
            using var iterator = slidePath.Path.CreateRawIterator();
            SKPathVerb verb;
            while ((verb = iterator.Next(points)) != SKPathVerb.Done)
            {
                switch (verb)
                {
                    case SKPathVerb.Move:
                        sb.Append('M');
                        Point(points[0]);
                        sb.Append(' ');
                        break;
                    case SKPathVerb.Line:
                        sb.Append('L');
                        Point(points[1]);
                        sb.Append(' ');
                        drawn = true;
                        break;
                    case SKPathVerb.Quad:
                        sb.Append('Q');
                        Point(points[1]);
                        Point(points[2]);
                        sb.Append(' ');
                        drawn = true;
                        break;
                    case SKPathVerb.Conic:
                        {
                            var quads = SKPath.ConvertConicToQuads(points[0], points[1], points[2], iterator.ConicWeight(), 2);
                            sb.Append('Q');
                            for (var i = 1; i + 1 < quads.Length; i += 2)
                            {
                                Point(quads[i]);
                                Point(quads[i + 1]);
                            }
                            sb.Append(' ');
                            drawn = true;
                            break;
                        }
                    case SKPathVerb.Cubic:
                        sb.Append('C');
                        Point(points[1]);
                        Point(points[2]);
                        Point(points[3]);
                        sb.Append(' ');
                        drawn = true;
                        break;
                    case SKPathVerb.Close:
                        sb.Append("Z ");
                        break;
                }
            }
            if (!drawn)
            {
                sb.Length = start;
                continue;
            }
            sb.Append("N ");
        }
        return sb.ToString().TrimEnd();
    }

    // ───────────────────────── Tables ─────────────────────────

    private void Table(StringBuilder sb, SlideTable table, SKMatrix matrix)
    {
        if (table.Rows.Count == 0 || table.Columns.Length == 0 || Decompose(matrix, table.Bounds.Width, table.Bounds.Height) is not { } place)
            return;
        var scaleX = table.Bounds.Width > 0 ? place.Width / table.Bounds.Width : 1;
        var scaleY = table.Bounds.Height > 0 ? place.Height / table.Bounds.Height : 1;
        var frameStyle = Graphic(" draw:fill=\"none\" draw:stroke=\"none\"");
        sb.Append($"<draw:frame draw:style-name=\"{frameStyle}\"{_layer} {Placement(place)}><table:table>");
        foreach (var width in table.Columns)
        {
            var column = _styles.Add("table-column", "co", "", $"<style:table-column-properties style:column-width=\"{Pt(Math.Max(0, width) * scaleX)}\"/>");
            sb.Append($"<table:table-column table:style-name=\"{column}\"/>");
        }
        foreach (var row in table.Rows)
        {
            _token.ThrowIfCancellationRequested();
            var rowStyle = _styles.Add("table-row", "ro", "", $"<style:table-row-properties style:row-height=\"{Pt(Math.Max(0, row.Height) * scaleY)}\"/>");
            sb.Append($"<table:table-row table:style-name=\"{rowStyle}\">");
            foreach (var cell in row.Cells.Take(table.Columns.Length))
            {
                if (cell.Covered)
                {
                    sb.Append("<table:covered-table-cell/>");
                    continue;
                }
                var frame = cell.Text;
                var graphic = Fill(cell.Fill, 1, 1) +
                              $" fo:padding-left=\"{Pt(frame.InsetLeft)}\" fo:padding-right=\"{Pt(frame.InsetRight)}\" fo:padding-top=\"{Pt(frame.InsetTop)}\" fo:padding-bottom=\"{Pt(frame.InsetBottom)}\"" +
                              $" draw:textarea-vertical-align=\"{Anchor(frame.Anchor)}\"";
                var borders = $" fo:border-left=\"{Border(cell.Left)}\" fo:border-right=\"{Border(cell.Right)}\" fo:border-top=\"{Border(cell.Top)}\" fo:border-bottom=\"{Border(cell.Bottom)}\"";
                var cellStyle = _styles.Add("table-cell", "ce", "", $"<style:graphic-properties{graphic}/><style:paragraph-properties{borders}/>");
                sb.Append($"<table:table-cell table:style-name=\"{cellStyle}\"");
                if (cell.ColumnSpan > 1)
                    sb.Append($" table:number-columns-spanned=\"{N(cell.ColumnSpan)}\"");
                if (cell.RowSpan > 1)
                    sb.Append($" table:number-rows-spanned=\"{N(cell.RowSpan)}\"");
                sb.Append('>');
                if (frame.Paragraphs.Count == 0)
                    sb.Append("<text:p/>");
                else
                    Paragraphs(sb, frame.Paragraphs);
                sb.Append("</table:table-cell>");
            }
            sb.Append("</table:table-row>");
        }
        sb.Append("</table:table></draw:frame>");
    }

    /// <summary>A cell border as fo:border writes it ("0.75pt solid #000000"), or none.</summary>
    private static string Border(SlideLine? line)
    {
        if (line is null)
            return "none";
        var color = SlidePaint.Representative(line.Fill);
        return $"{Pt(Math.Max(line.Width, 0.05f))} solid {Hex(color)}";
    }

    private static string Anchor(TextAnchor anchor) => anchor switch
    {
        TextAnchor.Middle => "middle",
        TextAnchor.Bottom => "bottom",
        _ => "top",
    };
}
