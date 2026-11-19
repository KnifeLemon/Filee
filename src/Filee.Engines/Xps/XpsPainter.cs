// Draws XPS elements (XpsModel) onto a Skia canvas, in the order and with the compositing XPS 1.0 §18 and ECMA-388
// describe: an element's RenderTransform, then its clip, then its content drawn in a layer when it has an opacity
// below 1 or an opacity mask (the mask's alpha is applied to the whole layer, DstIn). Paths are filled, then stroked.
//
// Brushes: solid colours and gradients are Skia paints; an image or visual brush that isn't tiled is drawn into the
// clipped geometry directly (so a PDF keeps the JPEG as it is, and a visual stays vector), a tiled one is a shader
// (image) or a picture shader (visual). Strokes with image or visual brushes fill the stroke's outline.
//
// Glyph runs drawn here (inside layers, with brushes other than a solid colour, or with clips that aren't rectangles)
// are text blobs of the whole font; XpsPageWriter hands the plain ones to PageContent instead, whose PDF writer
// subsets fonts.

using SkiaSharp;

namespace Filee.Engines.Xps;

internal static class XpsPainter
{
    /// <summary>Draws <paramref name="node"/>; the canvas is in the node's parent's coordinates.</summary>
    public static void Paint(SKCanvas canvas, XpsNode node)
    {
        canvas.Save();
        canvas.Concat(node.Transform);
        if (node.Clip is { } clip)
            canvas.ClipPath(clip.Fill, SKClipOperation.Intersect, antialias: !node.Aliased);
        if (node.NeedsLayer)
        {
            using var alpha = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(node.Opacity)) };
            canvas.SaveLayer(alpha);
            Content(canvas, node);
            if (node.OpacityMask is { } mask)
            {
                using var dstIn = new SKPaint { BlendMode = SKBlendMode.DstIn };
                canvas.SaveLayer(dstIn);
                using var everywhere = new SKPath();
                everywhere.AddRect(canvas.LocalClipBounds);
                Fill(canvas, everywhere, mask, antialias: true);
                canvas.Restore();
            }
            canvas.Restore();
        }
        else
        {
            Content(canvas, node);
        }
        canvas.Restore();
    }

    private static void Content(SKCanvas canvas, XpsNode node)
    {
        switch (node)
        {
            case XpsCanvas group:
                foreach (var child in group.Children)
                    Paint(canvas, child);
                break;
            case XpsPath path:
                if (path.Fill is { } fill)
                    Fill(canvas, path.Data.Fill, fill, !path.Aliased);
                if (path.Stroke is { } stroke)
                    Stroke(canvas, path.Data.Stroke, stroke, path.StrokeStyle, !path.Aliased);
                break;
            case XpsGlyphs glyphs:
                Glyphs(canvas, glyphs);
                break;
        }
    }

    public static byte Alpha(float opacity) => (byte)Math.Clamp(MathF.Round(opacity * 255), 0, 255);

    /// <summary>Fills <paramref name="path"/> (in the canvas's coordinates) with <paramref name="brush"/>.</summary>
    public static void Fill(SKCanvas canvas, SKPath path, XpsBrush brush, bool antialias)
    {
        if (path.IsEmpty)
            return;
        switch (brush)
        {
            case XpsTileBrush { IsDrawable: false }:
                return;
            case XpsTileBrush { Tile: XpsTile.None } tile:
                canvas.Save();
                canvas.ClipPath(path, SKClipOperation.Intersect, antialias);
                canvas.Concat(tile.Transform);
                canvas.ClipRect(tile.Viewport, SKClipOperation.Intersect, antialias);
                Tile(canvas, tile);
                canvas.Restore();
                return;
        }
        using var paint = Paint(brush, antialias);
        if (paint is not null)
            canvas.DrawPath(path, paint);
    }

    /// <summary>Strokes <paramref name="path"/> with <paramref name="brush"/>.</summary>
    public static void Stroke(SKCanvas canvas, SKPath path, XpsBrush brush, XpsStrokeStyle style, bool antialias)
    {
        if (path.IsEmpty || style.Thickness <= 0 || !float.IsFinite(style.Thickness))
            return;
        using var stroke = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = style.Thickness,
            StrokeJoin = style.Join,
            StrokeMiter = Math.Max(1, style.MiterLimit),
            StrokeCap = style.Dashes is null ? style.StartCap : style.DashCap,
            IsAntialias = antialias,
        };
        using var dash = style.Dashes is { } dashes ? SKPathEffect.CreateDash(dashes, style.DashOffset) : null;
        stroke.PathEffect = dash;
        if (brush is XpsSolidBrush or XpsGradientBrush)
        {
            using var paint = Paint(brush, antialias);
            if (paint is null)
                return;
            stroke.Color = paint.Color;
            stroke.Shader = paint.Shader;
            canvas.DrawPath(path, stroke);
            return;
        }
        using var outline = stroke.GetFillPath(path);
        if (outline is not null)
            Fill(canvas, outline, brush, antialias);
    }

    /// <summary>
    /// A Skia paint for a solid colour, gradient or tiled brush (in the canvas's coordinates); null for a brush that
    /// draws nothing. Untiled image and visual brushes are drawn by <see cref="Fill"/>.
    /// </summary>
    public static SKPaint? Paint(XpsBrush brush, bool antialias)
    {
        switch (brush)
        {
            case XpsSolidBrush solid:
                return new SKPaint { Color = solid.WithOpacity(1), IsAntialias = antialias };
            case XpsGradientBrush gradient:
                {
                    if (gradient.Colors.Length == 0)
                        return null;
                    var colors = gradient.Colors.Select(c => c.WithAlpha(Alpha(c.Alpha / 255f * gradient.Opacity))).ToArray();
                    if (colors.Length == 1)
                        return new SKPaint { Color = colors[0], IsAntialias = antialias };
                    SKShader shader;
                    if (gradient.Radius is not { } radius)
                    {
                        if (gradient.Start == gradient.End)
                            return new SKPaint { Color = colors[^1], IsAntialias = antialias };
                        shader = SKShader.CreateLinearGradient(gradient.Start, gradient.End, colors, gradient.Offsets, gradient.Spread, gradient.Transform);
                    }
                    else
                    {
                        if (radius.Width <= 0 || radius.Height <= 0)
                            return new SKPaint { Color = colors[^1], IsAntialias = antialias };
                        // An ellipse is a circle of RadiusX squeezed vertically about the centre.
                        var center = gradient.End;
                        var squeeze = radius.Height / radius.Width;
                        var origin = new SKPoint(gradient.Start.X, center.Y + (gradient.Start.Y - center.Y) / squeeze);
                        var matrix = gradient.Transform
                            .PreConcat(SKMatrix.CreateTranslation(center.X, center.Y))
                            .PreConcat(SKMatrix.CreateScale(1, squeeze))
                            .PreConcat(SKMatrix.CreateTranslation(-center.X, -center.Y));
                        shader = SKShader.CreateTwoPointConicalGradient(origin, 0, center, radius.Width, colors, gradient.Offsets, gradient.Spread, matrix);
                    }
                    return new SKPaint { Shader = shader, IsAntialias = antialias };
                }
            case XpsImageBrush image when image.IsDrawable:
                {
                    var (x, y) = image.TileModes;
                    if (Viewbox(image) is not var (tile, offset, owned))
                        return null;
                    // Tile pixels → picture pixels → picture units (the viewbox's) → viewport → brush transform.
                    var matrix = image.Transform.PreConcat(image.ViewboxToViewport).PreConcat(image.PixelsToUnits)
                        .PreConcat(SKMatrix.CreateTranslation(offset.X, offset.Y));
                    var shader = tile.ToShader(x, y, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), matrix);
                    if (owned)
                        tile.Dispose(); // the shader holds its own reference
                    return new SKPaint { Shader = shader, IsAntialias = antialias, Color = SKColors.White.WithAlpha(Alpha(image.Opacity)) };
                }
            case XpsVisualBrush visual when visual.IsDrawable:
                {
                    var (x, y) = visual.TileModes;
                    using var recorder = new SKPictureRecorder();
                    var canvas = recorder.BeginRecording(visual.Viewbox);
                    canvas.ClipRect(visual.Viewbox);
                    Paint(canvas, visual.Visual);
                    using var picture = recorder.EndRecording();
                    var shader = SKShader.CreatePicture(picture, x, y, SKFilterMode.Linear, visual.Transform.PreConcat(visual.ViewboxToViewport), visual.Viewbox);
                    return new SKPaint { Shader = shader, IsAntialias = antialias, Color = SKColors.White.WithAlpha(Alpha(visual.Opacity)) };
                }
            default:
                return null;
        }
    }

    /// <summary>
    /// The pixels of an image brush's viewbox as a picture of their own (the whole picture when the viewbox covers it,
    /// not owned), and the pixel where they start; null when the viewbox lies outside the picture.
    /// </summary>
    private static (SKImage Tile, SKPointI Offset, bool Owned)? Viewbox(XpsImageBrush brush)
    {
        var picture = brush.Image.Image;
        brush.PixelsToUnits.TryInvert(out var toPixels);
        var bounds = SKRectI.Create(0, 0, picture.Width, picture.Height);
        var area = SKRectI.Intersect(bounds, SKRectI.Round(toPixels.MapRect(brush.Viewbox)));
        if (area.IsEmpty)
            return null;
        if (area == bounds)
            return (picture, default, false);
        return picture.Subset(area) is { } subset ? (subset, new SKPointI(area.Left, area.Top), true) : null;
    }

    /// <summary>One untiled tile: the canvas is in the brush's coordinates, clipped to the viewport.</summary>
    private static void Tile(SKCanvas canvas, XpsTileBrush brush)
    {
        switch (brush)
        {
            case XpsImageBrush image:
                {
                    image.PixelsToUnits.TryInvert(out var toPixels);
                    using var paint = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(image.Opacity)), IsAntialias = true };
                    canvas.DrawImage(image.Image.Image, toPixels.MapRect(image.Viewbox), image.Viewport,
                        new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
                    break;
                }
            case XpsVisualBrush visual:
                {
                    canvas.Concat(visual.ViewboxToViewport);
                    if (visual.Opacity < 1)
                    {
                        using var alpha = new SKPaint { Color = SKColors.White.WithAlpha(Alpha(visual.Opacity)) };
                        canvas.SaveLayer(alpha);
                        Paint(canvas, visual.Visual);
                        canvas.Restore();
                    }
                    else
                    {
                        Paint(canvas, visual.Visual);
                    }
                    break;
                }
        }
    }

    /// <summary>A glyph run as a text blob of its whole font, or as outlines filled with an image or visual brush.</summary>
    private static void Glyphs(SKCanvas canvas, XpsGlyphs glyphs)
    {
        if (glyphs.Fill is not { } brush || glyphs.Glyphs.Length == 0)
            return;
        using var font = glyphs.SkiaFont(glyphs.Font.Typeface);
        if (brush is XpsSolidBrush or XpsGradientBrush)
        {
            using var paint = Paint(brush, !glyphs.Aliased);
            if (paint is null)
                return;
            using var builder = new SKTextBlobBuilder();
            var run = builder.AllocatePositionedRun(font, glyphs.Glyphs.Length);
            run.SetGlyphs(glyphs.Glyphs);
            run.SetPositions(glyphs.Positions);
            using var blob = builder.Build();
            if (blob is not null)
                canvas.DrawText(blob, 0, 0, paint);
            return;
        }
        using var outlines = Outlines(glyphs, font);
        Fill(canvas, outlines, brush, !glyphs.Aliased);
    }

    /// <summary>The outlines of a run's glyphs, placed.</summary>
    public static SKPath Outlines(XpsGlyphs glyphs, SKFont font)
    {
        var outlines = new SKPath();
        for (var i = 0; i < glyphs.Glyphs.Length; i++)
        {
            using var glyph = font.GetGlyphPath(glyphs.Glyphs[i]);
            if (glyph is not null && !glyph.IsEmpty)
                outlines.AddPath(glyph, glyphs.Positions[i].X, glyphs.Positions[i].Y);
        }
        return outlines;
    }
}
