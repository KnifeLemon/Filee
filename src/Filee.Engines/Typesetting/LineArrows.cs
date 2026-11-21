// Arrowheads at the ends of lines, open curves and arcs (HShape.Head / Tail) for the layout engine's drawing.
//
// Sizes follow DrawingML's line end width and length (ECMA-376 Part 1, ST_LineEndLength and ST_LineEndWidth: sm,
// med, lg), which name the sizes but not how big they are: taken here as 2, 3 and 5 times the
// line's width, as the slide renderer draws them. Lines thinner than 2 pt count as 2 pt, so an arrowhead on a hairline
// or Word's 0.75 pt default line stays big enough to see. The line stops short of a pointed head (under its back, or
// under the notch of a stealth head) so its end doesn't poke out of the tip, and short of a hollow mark's outline so
// the mark stays empty; a diamond, ellipse or square is centred on the end. The marks are solid in the line's colour,
// whatever the line's dashes.

using Filee.Engines.Hwp.Hwpx;
using SkiaSharp;

namespace Filee.Engines.Typesetting;

internal static class LineArrows
{
    /// <summary>The thinnest line width (points) an arrowhead's size is measured from.</summary>
    internal const float SmallestBase = 2f;

    /// <summary>How many line widths an arrowhead's width or length is.</summary>
    internal static float Factor(HArrowSize size) => size switch
    {
        HArrowSize.Small => 2,
        HArrowSize.Large => 5,
        _ => 3,
    };

    /// <summary>Whether a path can take arrowheads: one open contour with some length.</summary>
    internal static bool Open(SKPath path)
    {
        using var measure = new SKPathMeasure(path, false);
        return measure.Length > 0 && !measure.IsClosed && !measure.NextContour();
    }

    /// <summary>Strokes an open path with <paramref name="line"/> and draws its arrowheads.</summary>
    /// <param name="stroke">Strokes a path with the line's style (dashes, double lines).</param>
    internal static void Draw(SKCanvas canvas, HBorder line, SKPath path, HArrow? head, HArrow? tail, Action<SKPath> stroke)
    {
        var width = Math.Max(0.25f, (float)(line.WidthMm * 72 / 25.4));
        using var measure = new SKPathMeasure(path, false);
        var length = measure.Length;
        var (start, end) = (head is null ? 0 : Setback(head, width), tail is null ? 0 : Setback(tail, width));
        if (start + end < length - 0.01f)
        {
            using var shortened = new SKPath();
            if (start + end > 0 && measure.GetSegment(start, length - end, shortened, true))
                stroke(shortened);
            else
                stroke(path);
        }
        var color = ParagraphText.Color(line.Color) ?? SKColors.Black;
        if (head is not null && measure.GetPositionAndTangent(0, out var first, out var firstTangent))
            Mark(canvas, head, first, new SKPoint(-firstTangent.X, -firstTangent.Y), width, color);
        if (tail is not null && measure.GetPositionAndTangent(Math.Max(0, length - 0.001f), out var last, out var lastTangent))
            Mark(canvas, tail, last, lastTangent, width, color);
    }

    /// <summary>An arrowhead's width and length (points) on a line <paramref name="width"/> wide.</summary>
    internal static (float Width, float Length) Size(HArrow arrow, float width)
    {
        var unit = Math.Max(width, SmallestBase);
        return (Factor(arrow.Width) * unit, Factor(arrow.Length) * unit);
    }

    /// <summary>How far the line stops short of its end under an arrowhead.</summary>
    private static float Setback(HArrow arrow, float width)
    {
        var (_, length) = Size(arrow, width);
        return arrow.Kind switch
        {
            HArrowKind.Triangle => length * 0.9f,
            HArrowKind.Stealth => length * 0.6f,
            HArrowKind.Spear => length * 0.8f,
            HArrowKind.Diamond or HArrowKind.Oval or HArrowKind.Square when arrow.Hollow => length / 2,
            HArrowKind.Open => width / 2,
            _ => 0,
        };
    }

    /// <summary>Draws a mark at <paramref name="point"/>, pointing along <paramref name="direction"/> (away from the line).</summary>
    private static void Mark(SKCanvas canvas, HArrow arrow, SKPoint point, SKPoint direction, float width, SKColor color)
    {
        var (headWidth, headLength) = Size(arrow, width);
        var half = headWidth / 2;
        var normal = new SKPoint(-direction.Y, direction.X);
        SKPoint At(float along, float across) =>
            new(point.X + direction.X * along + normal.X * across, point.Y + direction.Y * along + normal.Y * across);
        var outline = arrow.Kind == HArrowKind.Open || arrow.Hollow;
        using var paint = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            Style = outline ? SKPaintStyle.Stroke : SKPaintStyle.Fill,
            StrokeWidth = width,
            StrokeJoin = SKStrokeJoin.Round,
            StrokeCap = SKStrokeCap.Round,
        };
        using var mark = new SKPath();
        switch (arrow.Kind)
        {
            case HArrowKind.Stealth:
                mark.AddPoly([At(0, 0), At(-headLength, half), At(-headLength * 0.6f, 0), At(-headLength, -half)]);
                break;
            case HArrowKind.Spear:
                mark.AddPoly([At(0, 0), At(-headLength / 2, half), At(-headLength, 0), At(-headLength / 2, -half)]);
                break;
            case HArrowKind.Diamond:
                mark.AddPoly([At(headLength / 2, 0), At(0, half), At(-headLength / 2, 0), At(0, -half)]);
                break;
            case HArrowKind.Square:
                mark.AddPoly([At(headLength / 2, half), At(-headLength / 2, half), At(-headLength / 2, -half), At(headLength / 2, -half)]);
                break;
            case HArrowKind.Oval:
                {
                    // An ellipse along the line: drawn turned to the line's direction.
                    canvas.Save();
                    canvas.Translate(point.X, point.Y);
                    canvas.RotateDegrees(MathF.Atan2(direction.Y, direction.X) * 180 / MathF.PI);
                    mark.AddOval(new SKRect(-headLength / 2, -half, headLength / 2, half));
                    canvas.DrawPath(mark, paint);
                    canvas.Restore();
                    return;
                }
            case HArrowKind.Open:
                mark.MoveTo(At(-headLength, half));
                mark.LineTo(At(0, 0));
                mark.LineTo(At(-headLength, -half));
                break;
            default:
                mark.AddPoly([At(0, 0), At(-headLength, half), At(-headLength, -half)]);
                break;
        }
        canvas.DrawPath(mark, paint);
    }
}
