// Draws "Annotate" marks with SkiaSharp: the window draws its live preview with it (through Avalonia's Skia canvas) and
// Annotator draws the file with it, so what one sees is what one gets. Points are parts of the picture; every size is a
// part of the picture's shorter side, so marks look the same on a small preview and the full-size file.

using Filee.Core.Presets;
using SkiaSharp;

namespace Filee.Engines.Annotations;

public static class AnnotationPainter
{
    /// <summary>Line thickness by width, in parts of the picture's shorter side.</summary>
    private static readonly double[] StrokeParts = [0.004, 0.007, 0.011];

    /// <summary>Text size by width.</summary>
    private static readonly double[] FontParts = [0.028, 0.036, 0.048];

    /// <summary>A number's circle by width.</summary>
    private static readonly double[] NumberParts = [0.04, 0.05, 0.065];

    /// <summary>How see-through a highlight is: its colour at this opacity.</summary>
    internal const byte HighlightAlpha = 102;

    public static float StrokeOf(AnnotationWidth width, float pictureWidth, float pictureHeight) =>
        (float)Math.Max(2, Math.Min(pictureWidth, pictureHeight) * StrokeParts[Index(width)]);

    public static float FontSizeOf(AnnotationWidth width, float pictureWidth, float pictureHeight) =>
        (float)Math.Max(10, Math.Min(pictureWidth, pictureHeight) * FontParts[Index(width)]);

    public static float NumberSizeOf(AnnotationWidth width, float pictureWidth, float pictureHeight) =>
        (float)Math.Max(14, Math.Min(pictureWidth, pictureHeight) * NumberParts[Index(width)]);

    private static int Index(AnnotationWidth width) => Math.Clamp((int)width, 0, 2);

    /// <summary>A mark's point in pixels of a <paramref name="width"/> × <paramref name="height"/> picture.</summary>
    public static SKPoint At(AnnotationPoint point, float width, float height) => new((float)(point.X * width), (float)(point.Y * height));

    /// <summary>
    /// Draws <paramref name="marks"/>, in order (the last on top), on a <paramref name="width"/> ×
    /// <paramref name="height"/> picture.
    /// </summary>
    public static void Paint(SKCanvas canvas, IEnumerable<Annotation> marks, float width, float height)
    {
        foreach (var mark in marks)
            Paint(canvas, mark, width, height);
    }

    public static void Paint(SKCanvas canvas, Annotation mark, float width, float height)
    {
        if (mark.Points.Count == 0)
            return;
        var color = ColorOf(mark.Color);
        var stroke = StrokeOf(mark.Width, width, height);
        switch (mark.Kind)
        {
            case AnnotationKind.Pen when mark.Points.Count >= 2:
                {
                    using var path = new SKPath();
                    path.MoveTo(At(mark.Points[0], width, height));
                    foreach (var point in mark.Points.Skip(1))
                        path.LineTo(At(point, width, height));
                    using var paint = Line(color, stroke);
                    canvas.DrawPath(path, paint);
                    break;
                }
            case AnnotationKind.Arrow when mark.Points.Count >= 2:
                DrawArrow(canvas, At(mark.Points[0], width, height), At(mark.Points[1], width, height), color, stroke);
                break;
            case AnnotationKind.Box when mark.Points.Count >= 2:
                {
                    var rect = RectOf(mark, width, height);
                    using var paint = Line(color, stroke);
                    canvas.DrawRoundRect(rect, stroke * 1.2f, stroke * 1.2f, paint);
                    break;
                }
            case AnnotationKind.Highlight when mark.Points.Count >= 2:
                {
                    using var paint = new SKPaint { Color = color.WithAlpha(HighlightAlpha), IsAntialias = true, Style = SKPaintStyle.Fill };
                    canvas.DrawRect(RectOf(mark, width, height), paint);
                    break;
                }
            case AnnotationKind.Text when !string.IsNullOrEmpty(mark.Text):
                DrawTag(canvas, mark, width, height, color);
                break;
            case AnnotationKind.Number:
                DrawNumber(canvas, mark, width, height, color);
                break;
        }
    }

    /// <summary>The rectangle between a box's or highlight's two corners, in pixels.</summary>
    public static SKRect RectOf(Annotation mark, float width, float height)
    {
        var a = At(mark.Points[0], width, height);
        var b = At(mark.Points[1], width, height);
        return new SKRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }

    /// <summary>
    /// The tag a text sits on, in pixels: its point is the tag's top left corner. Also what selecting and moving a text
    /// go by.
    /// </summary>
    public static SKRect TagOf(Annotation mark, float width, float height)
    {
        using var font = FontFor(mark.Text ?? "", FontSizeOf(mark.Width, width, height));
        var size = font.Size;
        var textWidth = font.MeasureText(string.IsNullOrEmpty(mark.Text) ? " " : mark.Text);
        var at = At(mark.Points[0], width, height);
        return new SKRect(at.X, at.Y, at.X + textWidth + size * 0.9f, at.Y + size * 1.5f);
    }

    /// <summary>A number's circle, in pixels: its point is the centre.</summary>
    public static SKRect CircleOf(Annotation mark, float width, float height)
    {
        var radius = NumberSizeOf(mark.Width, width, height) / 2;
        var at = At(mark.Points[0], width, height);
        return new SKRect(at.X - radius, at.Y - radius, at.X + radius, at.Y + radius);
    }

    private static void DrawArrow(SKCanvas canvas, SKPoint tail, SKPoint tip, SKColor color, float stroke)
    {
        var dx = tip.X - tail.X;
        var dy = tip.Y - tail.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 0.5f)
            return;
        var (ux, uy) = (dx / length, dy / length);
        // The head: as long as four and a half lines are thick, but never more than half the arrow.
        var headLength = Math.Min(stroke * 4.5f, length * 0.5f);
        var headHalf = headLength * 0.58f;
        var neck = new SKPoint(tip.X - ux * headLength, tip.Y - uy * headLength);
        using (var paint = Line(color, stroke))
            canvas.DrawLine(tail, new SKPoint(neck.X + ux * stroke * 0.5f, neck.Y + uy * stroke * 0.5f), paint);
        using var head = new SKPath();
        head.MoveTo(tip);
        head.LineTo(neck.X - uy * headHalf, neck.Y + ux * headHalf);
        head.LineTo(neck.X + uy * headHalf, neck.Y - ux * headHalf);
        head.Close();
        using var fill = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
        canvas.DrawPath(head, fill);
    }

    private static void DrawTag(SKCanvas canvas, Annotation mark, float width, float height, SKColor color)
    {
        var tag = TagOf(mark, width, height);
        var size = FontSizeOf(mark.Width, width, height);
        using (var fill = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill })
            canvas.DrawRoundRect(tag, size * 0.3f, size * 0.3f, fill);
        using var font = FontFor(mark.Text!, size);
        using var ink = new SKPaint { Color = InkOn(color), IsAntialias = true };
        font.GetFontMetrics(out var metrics);
        var baseline = tag.MidY - (metrics.Ascent + metrics.Descent) / 2;
        canvas.DrawText(mark.Text!, tag.Left + size * 0.45f, baseline, SKTextAlign.Left, font, ink);
    }

    private static void DrawNumber(SKCanvas canvas, Annotation mark, float width, float height, SKColor color)
    {
        var circle = CircleOf(mark, width, height);
        var radius = circle.Width / 2;
        using (var ring = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill })
            canvas.DrawCircle(circle.MidX, circle.MidY, radius, ring);
        using (var fill = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill })
            canvas.DrawCircle(circle.MidX, circle.MidY, radius * 0.86f, fill);
        var text = mark.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var font = FontFor(text, radius * (text.Length > 1 ? 0.95f : 1.15f));
        using var ink = new SKPaint { Color = InkOn(color), IsAntialias = true };
        font.GetFontMetrics(out var metrics);
        canvas.DrawText(text, circle.MidX, circle.MidY - (metrics.Ascent + metrics.Descent) / 2, SKTextAlign.Center, font, ink);
    }

    private static SKPaint Line(SKColor color, float stroke) => new()
    {
        Color = color,
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = stroke,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round,
    };

    /// <summary>
    /// A bold font that has the text's letters: the system's own, else one the system finds for them (Korean, Thai …).
    /// </summary>
    private static SKFont FontFor(string text, float size)
    {
        var typeface = SKTypeface.FromFamilyName(null, SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                       ?? SKTypeface.Default;
        var missing = text.EnumerateRunes().FirstOrDefault(r => !char.IsWhiteSpace((char)Math.Min(r.Value, char.MaxValue))
                                                                && typeface.GetGlyph(r.Value) == 0);
        if (missing.Value != 0 && SKFontManager.Default.MatchCharacter(null, SKFontStyleWeight.Bold, SKFontStyleWidth.Normal,
                SKFontStyleSlant.Upright, null, missing.Value) is { } fallback)
            typeface = fallback;
        return new SKFont(typeface, size) { Edging = SKFontEdging.Antialias, Subpixel = true };
    }

    /// <summary>White on a dark colour, near-black on a light one (yellow), so text and numbers read.</summary>
    internal static SKColor InkOn(SKColor color) =>
        0.2126 * color.Red + 0.7152 * color.Green + 0.0722 * color.Blue > 170 ? new SKColor(0x1C, 0x1C, 0x1C) : SKColors.White;

    internal static SKColor ColorOf(string? text) =>
        text is not null && SKColor.TryParse(text, out var color) ? color.WithAlpha(255) : new SKColor(0xE5, 0x48, 0x4D);
}
