// Floating objects for the layout engine (GitHub Project 4 item "Layout engine: floating objects"): pictures, shapes
// and text boxes that have an anchor (HAnchor) instead of a place on a line. The page composer finds them in the
// paragraph that holds them (Of), places them where the paragraph starts (Place) and draws them when the page is done,
// behind the text or in front of it (Draw).
//
// Placement, as 한글 places them (measured on 한글's own PDFs of written test files):
// - Horizontally against the paper (PAPER), the text area between the margins (PAGE), the column (COLUMN) or the
//   paragraph within its indents (PARA): LEFT puts the object's left edge the offset right of the reference's left
//   edge, RIGHT its right edge the offset left of the reference's right edge, CENTER its centre the offset right of the
//   reference's centre. INSIDE and OUTSIDE are the binding side and the other one (odd pages bind on the left).
// - Vertically against the paper (PAPER), the body between the header and footer areas (PAGE), the same way down from
//   the top, up from the bottom or from the centre; or against the paragraph (PARA): the offset down from the top of
//   its first line, whatever the alignment, never above it.
//
// Drawing: pictures with their crop inside their inner margin, under their outline; rectangles (rounded by their corner ratio), ellipses, lines, polygons, curves and
// arcs with their fill (a colour or a linear gradient) and outline (solid, dashed, dotted, dash-dot or double, at its
// width), rotated and mirrored about their centre; text boxes as their shape with their paragraphs and tables inside
// their padding, at the top, middle or bottom. Curves go smoothly through their points; arcs run counterclockwise
// from their first axis point to the second. Text in a text box is not rotated, and pictures' effects (shadow, glow,
// soft edge, reflection) are left out.
//
// Text wrap: lines on 한글's stored layout already leave the room 한글 left for the objects beside or above them.
// Wrapping text that is laid out anew around objects is not done yet.

using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Rendering;
using SkiaSharp;

namespace Filee.Engines.Typesetting;

/// <summary>The areas an anchor can be relative to, in points on the page.</summary>
/// <param name="Paper">The whole sheet.</param>
/// <param name="Page">The text area: between the margins across, the body down.</param>
/// <param name="Column">The column the anchoring paragraph is in, as high as the body.</param>
/// <param name="Paragraph">The anchoring paragraph within its indents, from the top of its first line.</param>
/// <param name="Odd">An odd page (bound on its left side).</param>
internal readonly record struct AnchorFrame(SKRect Paper, SKRect Page, SKRect Column, SKRect Paragraph, bool Odd);

/// <summary>A floating object placed on its page.</summary>
/// <param name="Wrap">How text goes around it (HAnchor.Wrap).</param>
/// <param name="Order">Its z-order (higher in front).</param>
/// <param name="Sequence">Its place in the document, for objects of one z-order.</param>
internal sealed record PlacedObject(HInline Object, SKRect Area, string Wrap, int Order, int Sequence)
{
    public bool Behind => Wrap == "BEHIND_TEXT";
}

internal static class FloatingObjects
{
    /// <summary>The objects of a paragraph that float (have an anchor), links' content included, in order.</summary>
    public static IEnumerable<(HInline Object, HAnchor Anchor)> Of(HParagraph paragraph) => Of(paragraph.Inlines);

    private static IEnumerable<(HInline, HAnchor)> Of(IEnumerable<HInline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case HImage { Anchor: { } anchor }:
                    yield return (inline, anchor);
                    break;
                case HShape { Anchor: { } anchor }:
                    yield return (inline, anchor);
                    break;
                case HTextBox { Anchor: { } anchor }:
                    yield return (inline, anchor);
                    break;
                case HLink link:
                    foreach (var inner in Of(link.Content))
                        yield return inner;
                    break;
            }
        }
    }

    /// <summary>An object's size in points; null for a picture whose size can't be known.</summary>
    public static (float Width, float Height)? Size(HInline item) => item switch
    {
        HImage picture => ParagraphText.PictureSize(picture),
        HShape shape => (shape.Width / 100f, shape.Height / 100f),
        HTextBox box => (box.Width / 100f, box.Height / 100f),
        _ => null,
    };

    /// <summary>
    /// Pieces drawing the floating objects of a paragraph in a paragraph list (a table cell, a header or footer, a text
    /// box) whose first line starts at <paramref name="top"/>: placed against the list (its width as the column, the
    /// paragraph within its indents) or, given a <paramref name="frame"/>, against the paper or the page. Each comes
    /// with its z-order and whether it lies behind the text.
    /// </summary>
    public static IEnumerable<(int Order, Piece Piece, bool Behind)> Pieces(HParagraph paragraph, float top, float width, AnchorFrame? frame,
        Func<IReadOnlyList<HBlock>, float, List<Piece>> stack)
    {
        var list = new SKRect(0, 0, width, 0);
        var local = new AnchorFrame(list, list, list,
            new SKRect((paragraph.Format.Left ?? 0) / 100f, 0, width - (paragraph.Format.Right ?? 0) / 100f, 0), frame?.Odd ?? true);
        foreach (var (item, anchor) in Of(paragraph))
        {
            if (Size(item) is not var (w, h))
                continue;
            var inList = Place(anchor, w, h, local);
            var onPage = frame is { } f ? Place(anchor, w, h, f) : inList;
            var pageX = frame is not null && anchor.HorizontalRelativeTo is "PAPER" or "PAGE";
            var pageY = frame is not null && anchor.VerticalRelativeTo is "PAPER" or "PAGE";
            // An object placed at its paragraph moves with the text and stays in the list: a cell grows to hold it and
            // a text box centres it with the text (as 한글 does, whatever the wrap).
            var room = pageY ? 0 : Math.Max(0, inList.Bottom);
            var piece = new Piece(top, room, (page, x, y) =>
            {
                var (left, upper) = (pageX ? onPage.Left : x + inList.Left, pageY ? onPage.Top : y + inList.Top);
                Draw(page, item, new SKRect(left, upper, left + w, upper + h), stack);
            });
            yield return (ZOrder(item), piece, anchor.Wrap == "BEHIND_TEXT");
        }
    }

    /// <summary>An object's z-order (higher in front); 0 when it has none.</summary>
    public static int ZOrder(HInline item) => item switch
    {
        HImage { ZOrder: { } z } => z,
        HShape { ZOrder: { } z } => z,
        HTextBox { ZOrder: { } z } => z,
        _ => 0,
    };

    /// <summary>Where an object <paramref name="width"/> × <paramref name="height"/> goes by its anchor.</summary>
    public static SKRect Place(HAnchor anchor, float width, float height, AnchorFrame frame)
    {
        var across = anchor.HorizontalRelativeTo switch
        {
            "PAPER" => frame.Paper,
            "PAGE" => frame.Page,
            "PARA" => frame.Paragraph,
            _ => frame.Column,
        };
        var x = Aligned(Side(anchor.HorizontalAlign, frame.Odd), across.Left, across.Right, width, anchor.HorizontalOffset / 100f);
        float y;
        if (anchor.VerticalRelativeTo is "PAPER" or "PAGE")
        {
            var down = anchor.VerticalRelativeTo == "PAPER" ? frame.Paper : frame.Page;
            y = Aligned(anchor.VerticalAlign switch
            {
                "BOTTOM" or "OUTSIDE" => "RIGHT",
                "CENTER" => "CENTER",
                _ => "LEFT",
            }, down.Top, down.Bottom, height, anchor.VerticalOffset / 100f);
        }
        else
        {
            y = frame.Paragraph.Top + Math.Max(0, anchor.VerticalOffset / 100f);
        }
        return new SKRect(x, y, x + width, y + height);
    }

    /// <summary>INSIDE and OUTSIDE as the side they are on a page: odd pages bind on the left.</summary>
    private static string Side(string align, bool odd) => align switch
    {
        "INSIDE" => odd ? "LEFT" : "RIGHT",
        "OUTSIDE" => odd ? "RIGHT" : "LEFT",
        _ => align,
    };

    private static float Aligned(string align, float start, float end, float size, float offset) => align switch
    {
        "RIGHT" => end - size - offset,
        "CENTER" => (start + end - size) / 2 + offset,
        _ => start + offset,
    };

    /// <summary>
    /// Draws an object into <paramref name="area"/>; a text box's blocks are stacked by <paramref name="stack"/> (the
    /// blocks and the width they take).
    /// </summary>
    public static void Draw(PageContent page, HInline item, SKRect area, Func<IReadOnlyList<HBlock>, float, List<Piece>> stack)
    {
        switch (item)
        {
            case HImage picture:
                page.Draw(canvas => Transformed(canvas, picture.Transform, area, () =>
                {
                    var margin = picture.Margin;
                    Picture(canvas, picture, new SKRect(area.Left + margin.Left / 100f, area.Top + margin.Top / 100f,
                        area.Right - margin.Right / 100f, area.Bottom - margin.Bottom / 100f));
                    using var outline = new SKPath();
                    outline.AddRect(area);
                    Stroke(canvas, picture.Line, outline);
                }));
                break;
            case HShape shape:
                page.Draw(canvas => Transformed(canvas, shape.Transform, area, () => Shape(canvas, shape.Kind, shape.Geometry, shape.CornerRatio,
                    shape.Fill, shape.Gradient, shape.Line, area, shape.FlipHorizontal, shape.FlipVertical)));
                break;
            case HTextBox box:
                page.Draw(canvas => Transformed(canvas, box.Transform, area, () => Shape(canvas, box.Shape, box.Geometry, box.CornerRatio,
                    box.Fill, box.Gradient, box.Line, area, false, false)));
                var padding = box.Padding;
                var inner = new SKRect(area.Left + padding.Left / 100f, area.Top + padding.Top / 100f,
                    area.Right - padding.Right / 100f, area.Bottom - padding.Bottom / 100f);
                var pieces = stack(box.Blocks, Math.Max(1, inner.Width));
                var height = pieces.Count == 0 ? 0 : pieces.Max(p => p.Top + p.Height - p.Below);
                var top = inner.Top + box.VerticalAlign switch
                {
                    HVerticalAlign.Center => Math.Max(0, (inner.Height - height) / 2),
                    HVerticalAlign.Bottom => Math.Max(0, inner.Height - height),
                    _ => 0,
                };
                foreach (var piece in pieces)
                    piece.Draw(page, inner.Left, top + piece.Top);
                break;
        }
    }

    /// <summary>Runs <paramref name="draw"/> rotated and mirrored about the centre of <paramref name="area"/>.</summary>
    private static void Transformed(SKCanvas canvas, HTransform? transform, SKRect area, Action draw)
    {
        if (transform is null || transform.IsIdentity)
        {
            draw();
            return;
        }
        canvas.Save();
        canvas.Translate(area.MidX, area.MidY);
        if (transform.Rotation != 0)
            canvas.RotateDegrees((float)transform.Rotation);
        canvas.Scale(transform.FlipHorizontal ? -1 : 1, transform.FlipVertical ? -1 : 1);
        canvas.Translate(-area.MidX, -area.MidY);
        draw();
        canvas.Restore();
    }

    private static void Picture(SKCanvas canvas, HImage picture, SKRect area)
    {
        using var image = File.Exists(picture.Path) ? SKImage.FromEncodedData(picture.Path) : null;
        if (image is null)
            return;
        var crop = picture.Crop ?? default;
        var source = new SKRect(
            (float)(crop.Left * image.Width), (float)(crop.Top * image.Height),
            (float)((1 - crop.Right) * image.Width), (float)((1 - crop.Bottom) * image.Height));
        canvas.DrawImage(image, source, area, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    }

    private static void Shape(SKCanvas canvas, HShapeKind kind, HGeometry? geometry, int cornerRatio, string? fill, HGradient? gradient,
        HBorder line, SKRect area, bool flipHorizontal, bool flipVertical)
    {
        using var path = Outline(kind, geometry, cornerRatio, area, flipHorizontal, flipVertical);
        if (kind != HShapeKind.Line && (gradient is not null || ParagraphText.Color(fill) is not null))
        {
            using var paint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
            using var shader = gradient is null ? null : Gradient(gradient, area);
            if (shader is not null)
                paint.Shader = shader;
            else
                paint.Color = ParagraphText.Color(fill)!.Value;
            canvas.DrawPath(path, paint);
        }
        Stroke(canvas, line, path);
    }

    /// <summary>The outline of a shape in <paramref name="area"/>.</summary>
    private static SKPath Outline(HShapeKind kind, HGeometry? geometry, int cornerRatio, SKRect area, bool flipHorizontal, bool flipVertical)
    {
        var path = new SKPath();
        var (sx, sy) = geometry is { Width: > 0, Height: > 0 } ? (area.Width / geometry.Width, area.Height / geometry.Height) : (0.01f, 0.01f);
        SKPoint Point(HPoint p) => new(area.Left + p.X * sx, area.Top + p.Y * sy);
        switch (kind)
        {
            case HShapeKind.Ellipse:
                path.AddOval(area);
                break;
            case HShapeKind.Line:
                path.MoveTo(flipHorizontal ? area.Right : area.Left, flipVertical ? area.Bottom : area.Top);
                path.LineTo(flipHorizontal ? area.Left : area.Right, flipVertical ? area.Top : area.Bottom);
                break;
            case HShapeKind.Polygon when geometry?.Points is { Count: > 1 } points:
                path.AddPoly([.. points.Select(Point)], close: points[0] == points[^1] || points.Count > 2);
                break;
            case HShapeKind.Curve when geometry?.Segments is { Count: > 0 } segments:
                Curve(path, [.. segments.Select(s => (s.Curve, Point(s.Start), Point(s.End)))]);
                break;
            case HShapeKind.Arc when geometry?.Arc is { } arc:
                {
                    var center = Point(arc.Center);
                    var (a, b) = (Point(arc.Axis1), Point(arc.Axis2));
                    var rx = Math.Max(Math.Abs(a.X - center.X), Math.Abs(b.X - center.X));
                    var ry = Math.Max(Math.Abs(a.Y - center.Y), Math.Abs(b.Y - center.Y));
                    if (rx <= 0 || ry <= 0)
                        break;
                    var oval = new SKRect(center.X - rx, center.Y - ry, center.X + rx, center.Y + ry);
                    // 한글 draws the arc from its first axis point to its second counterclockwise.
                    var start = Angle(a, center, rx, ry);
                    var sweep = Angle(b, center, rx, ry) - start;
                    if (sweep >= 0)
                        sweep -= 360;
                    path.AddArc(oval, start, sweep);
                    if (arc.Kind == HArcKind.Pie)
                    {
                        path.LineTo(center);
                        path.Close();
                    }
                    else if (arc.Kind == HArcKind.Chord)
                    {
                        path.Close();
                    }
                    break;
                }
            default:
                var radius = Math.Min(area.Width, area.Height) * Math.Clamp(cornerRatio, 0, 50) / 100f;
                if (radius > 0)
                    path.AddRoundRect(area, radius, radius);
                else
                    path.AddRect(area);
                break;
        }
        return path;
    }

    /// <summary>
    /// A 한글 curve: straight pieces as lines, curved ones as a smooth curve through the points (Catmull-Rom, its
    /// tangent at a point parallel to the line between its neighbours; a closed curve's ends are neighbours).
    /// </summary>
    private static void Curve(SKPath path, IReadOnlyList<(bool Curved, SKPoint Start, SKPoint End)> segments)
    {
        var closed = segments[0].Start == segments[^1].End;
        path.MoveTo(segments[0].Start);
        for (var i = 0; i < segments.Count; i++)
        {
            var (curved, p1, p2) = segments[i];
            if (!curved)
            {
                path.LineTo(p2);
                continue;
            }
            var p0 = i > 0 ? segments[i - 1].Start : closed ? segments[^1].Start : p1;
            var p3 = i + 1 < segments.Count ? segments[i + 1].End : closed ? segments[0].End : p2;
            path.CubicTo(
                new SKPoint(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6),
                new SKPoint(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6),
                p2);
        }
        if (closed)
            path.Close();
    }

    /// <summary>The angle of a point on an ellipse, in degrees clockwise from the right (as Skia measures arcs).</summary>
    private static float Angle(SKPoint point, SKPoint center, float rx, float ry) =>
        (float)(Math.Atan2((point.Y - center.Y) / ry, (point.X - center.X) / rx) * 180 / Math.PI);

    private static SKShader Gradient(HGradient gradient, SKRect area)
    {
        // The angle turns the gradient clockwise from top to bottom (0°).
        var radians = gradient.Angle * Math.PI / 180;
        var (dx, dy) = ((float)Math.Sin(radians) * area.Width / 2, (float)Math.Cos(radians) * area.Height / 2);
        var colors = gradient.Colors.Select(c => ParagraphText.Color(c) ?? SKColors.White).ToArray();
        return SKShader.CreateLinearGradient(new SKPoint(area.MidX - dx, area.MidY - dy), new SKPoint(area.MidX + dx, area.MidY + dy),
            colors, SKShaderTileMode.Clamp);
    }

    /// <summary>Strokes a path with a border's style, width and colour (a double line as two thin strokes).</summary>
    internal static void Stroke(SKCanvas canvas, HBorder border, SKPath path)
    {
        if (border.Style == HBorderStyle.None)
            return;
        var width = Math.Max(0.25f, (float)(border.WidthMm * 72 / 25.4));
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = ParagraphText.Color(border.Color) ?? SKColors.Black,
            IsAntialias = true,
            StrokeWidth = border.Style == HBorderStyle.Double ? Math.Max(0.25f, width / 3) : width,
        };
        float[]? dashes = border.Style switch
        {
            HBorderStyle.Dash => [width * 4, width * 2],
            HBorderStyle.Dot => [width, width * 2],
            HBorderStyle.DashDot => [width * 4, width * 2, width, width * 2],
            _ => null,
        };
        using var effect = dashes is null ? null : SKPathEffect.CreateDash(dashes, 0);
        paint.PathEffect = effect;
        if (border.Style == HBorderStyle.Double)
        {
            // Two strokes a third of the width each, a third apart: the outline drawn widened and its inside cut away.
            using var outer = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = width, IsAntialias = true };
            using var ring = outer.GetFillPath(path);
            using var innerPaint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = width / 3, IsAntialias = true };
            using var middle = innerPaint.GetFillPath(path);
            using var two = ring.Op(middle, SKPathOp.Difference);
            paint.Style = SKPaintStyle.Fill;
            canvas.DrawPath(two ?? ring, paint);
            return;
        }
        canvas.DrawPath(path, paint);
    }
}
