// Draws XPS elements (XpsModel) onto a Skia canvas, in the order and with the compositing XPS 1.0 §18 and ECMA-388
// describe: an element's RenderTransform, then its clip, then its content drawn in a layer when it has an opacity
// below 1 or an opacity mask (the mask's alpha is applied to the whole layer, DstIn). Paths are filled, then stroked.
//
// Brushes: solid colours and gradients are Skia paints; an image or visual brush that isn't tiled is drawn into the
// clipped geometry directly (so a PDF keeps the JPEG as it is, and a visual stays vector). A tiled image brush is an
// image shader (a PDF tiling pattern of the picture). A tiled visual brush is drawn tile by tile, each a copy of the
// visual clipped to its viewport and flipped as TileMode says, so it stays vector in a PDF (Skia's PDF backend has no
// vector tiling pattern: a picture shader would become a picture of the whole area at its raster resolution). When
// that would draw too much (more than MaxVectorTiles tiles or MaxVectorElements elements, or a tiled brush inside a
// tile), one tile is drawn into a picture of at most MaxTilePixels a side and repeated as an image shader.
//
// Strokes take XPS's caps: Skia's own when both ends have the same flat, round or square one, else a flat-ended outline
// with each end's cap (triangles among them) joined to it (OwnCaps). Strokes with image or visual brushes fill the
// stroke's outline.
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
            case XpsVisualBrush visual:
                canvas.Save();
                canvas.ClipPath(path, SKClipOperation.Intersect, antialias);
                canvas.Concat(visual.Transform);
                Tiles(canvas, visual);
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
        // Skia has one cap for both ends and no triangle: other caps are drawn onto the outline (OwnCaps).
        var cap = style.Dashes is null ? style.StartCap : style.DashCap;
        var skiaCaps = cap != XpsLineCap.Triangle && (style.Dashes is not null || style.EndCap == style.StartCap);
        using var stroke = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = style.Thickness,
            StrokeJoin = style.Join,
            StrokeMiter = Math.Max(1, style.MiterLimit),
            StrokeCap = skiaCaps ? SkiaCap(cap) : SKStrokeCap.Butt,
            IsAntialias = antialias,
        };
        using var dash = style.Dashes is { } dashes ? SKPathEffect.CreateDash(dashes, style.DashOffset) : null;
        stroke.PathEffect = dash;
        if (skiaCaps && brush is XpsSolidBrush or XpsGradientBrush)
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
        if (outline is null)
            return;
        if (skiaCaps)
        {
            Fill(canvas, outline, brush, antialias);
            return;
        }
        using var capped = OwnCaps(path, outline, style, dash);
        Fill(canvas, capped, brush, antialias);
    }

    private static SKStrokeCap SkiaCap(XpsLineCap cap) => cap switch
    {
        XpsLineCap.Round => SKStrokeCap.Round,
        XpsLineCap.Square => SKStrokeCap.Square,
        _ => SKStrokeCap.Butt,
    };

    /// <summary>
    /// A flat-ended stroke <paramref name="outline"/> with the caps of <paramref name="style"/> added at the ends of
    /// every open figure (of every dash when dashed): the start cap at its start, the end cap at its end, the dash cap
    /// at both ends of a dash. The caps are joined to the outline, so a translucent stroke stays even.
    /// </summary>
    internal static SKPath OwnCaps(SKPath centerline, SKPath outline, XpsStrokeStyle style, SKPathEffect? dash)
    {
        using var dashed = new SKPath();
        var line = centerline;
        if (dash is not null)
        {
            // A hairline paint applies the dash pattern only, which gives the dashes' centre lines.
            using var hairline = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 0, PathEffect = dash };
            if (!hairline.GetFillPath(centerline, dashed))
                return new SKPath(outline);
            line = dashed;
        }
        var half = style.Thickness / 2;
        using var caps = new SKPath { FillType = SKPathFillType.Winding };
        using var measure = new SKPathMeasure(line, forceClosed: false);
        var figures = 0;
        do
        {
            // An outline of a few million figures is no picture anyone made; their ends stay flat.
            if (measure.IsClosed || measure.Length <= 0 || ++figures > MaxCappedFigures)
                continue;
            if (!measure.GetPositionAndTangent(0, out var start, out var startTangent)
                || !measure.GetPositionAndTangent(measure.Length, out var end, out var endTangent))
                continue;
            AddCap(caps, start, new SKPoint(-startTangent.X, -startTangent.Y), half, dash is null ? style.StartCap : style.DashCap);
            AddCap(caps, end, endTangent, half, dash is null ? style.EndCap : style.DashCap);
        }
        while (measure.NextContour());
        if (caps.IsEmpty)
            return new SKPath(outline);
        if (outline.Op(caps, SKPathOp.Union) is { } joined)
            return joined;
        var both = new SKPath(outline) { FillType = SKPathFillType.Winding };
        both.AddPath(caps);
        return both;
    }

    /// <summary>Open figures whose ends get caps of their own.</summary>
    private const int MaxCappedFigures = 100_000;

    /// <summary>A cap at <paramref name="at"/>, pointing along the unit vector <paramref name="outward"/>.</summary>
    private static void AddCap(SKPath caps, SKPoint at, SKPoint outward, float half, XpsLineCap cap)
    {
        var across = new SKPoint(-outward.Y * half, outward.X * half);
        var ahead = new SKPoint(outward.X * half, outward.Y * half);
        switch (cap)
        {
            case XpsLineCap.Round:
                caps.AddCircle(at.X, at.Y, half);
                break;
            case XpsLineCap.Square:
                caps.MoveTo(at + across);
                caps.LineTo(at + across + ahead);
                caps.LineTo(at - across + ahead);
                caps.LineTo(at - across);
                caps.Close();
                break;
            case XpsLineCap.Triangle:
                caps.MoveTo(at + across);
                caps.LineTo(at + ahead);
                caps.LineTo(at - across);
                caps.Close();
                break;
        }
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

    /// <summary>Tiles of a tiled visual brush drawn, at most, as vectors.</summary>
    internal const int MaxVectorTiles = 4096;

    /// <summary>Elements of a tiled visual brush's tiles drawn, at most, as vectors (tiles × the visual's elements).</summary>
    internal const long MaxVectorElements = 100_000;

    /// <summary>Largest side, in pixels, of the picture of one tile of a visual brush repeated as an image.</summary>
    internal const int MaxTilePixels = 1024;

    /// <summary>Tiled visual brushes being drawn as vectors on this thread (a tiled brush inside a tile is a picture).</summary>
    [ThreadStatic]
    private static int _vectorTiling;

    /// <summary>
    /// A tiled visual brush over the canvas's clip: the canvas is in the brush's coordinates (after its transform).
    /// </summary>
    private static void Tiles(SKCanvas canvas, XpsVisualBrush visual)
    {
        var viewport = visual.Viewport;
        var visible = canvas.LocalClipBounds;
        if (visible.IsEmpty || !float.IsFinite(visible.Width + visible.Height))
            return;
        var firstColumn = Math.Floor((visible.Left - viewport.Left) / viewport.Width);
        var lastColumn = Math.Ceiling((visible.Right - viewport.Left) / viewport.Width);
        var firstRow = Math.Floor((visible.Top - viewport.Top) / viewport.Height);
        var lastRow = Math.Ceiling((visible.Bottom - viewport.Top) / viewport.Height);
        var tiles = (lastColumn - firstColumn) * (lastRow - firstRow);
        if (_vectorTiling == 0 && tiles <= MaxVectorTiles && tiles * visual.Elements <= MaxVectorElements)
        {
            _vectorTiling++;
            try
            {
                for (var row = firstRow; row < lastRow; row++)
                {
                    for (var column = firstColumn; column < lastColumn; column++)
                    {
                        canvas.Save();
                        canvas.Translate((float)(column * viewport.Width), (float)(row * viewport.Height));
                        canvas.ClipRect(viewport, SKClipOperation.Intersect, antialias: false);
                        var flipX = visual.Tile is XpsTile.FlipX or XpsTile.FlipXY && Math.Abs(column % 2) == 1;
                        var flipY = visual.Tile is XpsTile.FlipY or XpsTile.FlipXY && Math.Abs(row % 2) == 1;
                        if (flipX || flipY)
                            canvas.Scale(flipX ? -1 : 1, flipY ? -1 : 1, viewport.MidX, viewport.MidY);
                        Tile(canvas, visual);
                        canvas.Restore();
                    }
                }
            }
            finally
            {
                _vectorTiling--;
            }
            return;
        }
        // Too many to draw one by one: one tile as a picture, repeated.
        var device = canvas.TotalMatrix.MapRect(viewport);
        var width = Math.Clamp((int)MathF.Ceiling(device.Width * 2), 4, MaxTilePixels);
        var height = Math.Clamp((int)MathF.Ceiling(device.Height * 2), 4, MaxTilePixels);
        if (visual.Picture(width, height, DrawTile) is not { } picture)
            return;
        var (x, y) = visual.TileModes;
        var matrix = SKMatrix.CreateTranslation(viewport.Left, viewport.Top)
            .PreConcat(SKMatrix.CreateScale(viewport.Width / picture.Width, viewport.Height / picture.Height));
        using var shader = picture.ToShader(x, y, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), matrix);
        using var paint = new SKPaint { Shader = shader, IsAntialias = true };
        canvas.DrawRect(visible, paint);
    }

    /// <summary>One tile of <paramref name="visual"/> drawn into a canvas of <paramref name="width"/> × <paramref name="height"/> pixels.</summary>
    private static void DrawTile(SKCanvas canvas, XpsVisualBrush visual, int width, int height)
    {
        var viewport = visual.Viewport;
        canvas.Scale(width / viewport.Width, height / viewport.Height);
        canvas.Translate(-viewport.Left, -viewport.Top);
        canvas.ClipRect(viewport);
        _vectorTiling++;
        try
        {
            Tile(canvas, visual);
        }
        finally
        {
            _vectorTiling--;
        }
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
        // A gradient of a sideways run lies in the element's coordinates, not in the turned frame: it fills outlines.
        if (brush is XpsSolidBrush || (brush is XpsGradientBrush && !glyphs.Sideways))
        {
            using var paint = Paint(brush, !glyphs.Aliased);
            if (paint is null)
                return;
            using var builder = new SKTextBlobBuilder();
            var run = builder.AllocatePositionedRun(font, glyphs.Glyphs.Length);
            run.SetGlyphs(glyphs.Glyphs);
            run.SetPositions(glyphs.Positions);
            using var blob = builder.Build();
            if (blob is null)
                return;
            canvas.Save();
            canvas.Concat(glyphs.Frame);
            canvas.DrawText(blob, 0, 0, paint);
            canvas.Restore();
            return;
        }
        using var outlines = Outlines(glyphs, font);
        Fill(canvas, outlines, brush, !glyphs.Aliased);
    }

    /// <summary>The outlines of a run's glyphs, placed (in the element's coordinates).</summary>
    public static SKPath Outlines(XpsGlyphs glyphs, SKFont font)
    {
        var outlines = new SKPath();
        for (var i = 0; i < glyphs.Glyphs.Length; i++)
        {
            using var glyph = font.GetGlyphPath(glyphs.Glyphs[i]);
            if (glyph is not null && !glyph.IsEmpty)
                outlines.AddPath(glyph, glyphs.Positions[i].X, glyphs.Positions[i].Y);
        }
        if (glyphs.Sideways)
            outlines.Transform(glyphs.Frame);
        return outlines;
    }
}
