// Fills of the slide model as Skia paints: solid colours, gradients (linear at an angle, circles and rectangles out from
// a focus), pictures stretched into their fill rectangle with their crop or tiled, and two-colour patterns. Pictures are
// decoded once per deck (SlideImages) and keep their encoded data, so a PDF gets JPEGs as they are and a logo on
// every slide once.

using Filee.Engines.Documents;
using Filee.Engines.Metafiles;
using SkiaSharp;
using MetafileReader = Filee.Engines.Metafiles.Metafile;

namespace Filee.Engines.Slides;

/// <summary>The decoded pictures of one deck, by their bytes; disposed when the pages are written.</summary>
internal sealed class SlideImages : IDisposable
{
    private readonly Dictionary<byte[], SKImage?> _images = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<byte[], MetafileDrawing?> _metafiles = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// True when a picture can be drawn: an EMF or WMF, or a bitmap Skia decodes whose size stays within
    /// <see cref="DocumentLimits.MaxPicturePixels"/>.
    /// </summary>
    public static bool CanDraw(byte[] data, DocumentLimits limits)
    {
        if (data.Length < 8)
            return false;
        if (MetafileReader.Detect(data.AsSpan(0, Math.Min(data.Length, 64))) is not null)
            return true;
        using (var stream = new MemoryStream(data, writable: false))
        {
            if (PictureSize.Read(stream) is var (w, h))
                return w > 0 && h > 0 && (long)w * h <= limits.MaxPicturePixels;
        }
        using var codec = SKCodec.Create(new MemoryStream(data, writable: false));
        return codec is not null && codec.Info.Width > 0 && codec.Info.Height > 0 && (long)codec.Info.Width * codec.Info.Height <= limits.MaxPicturePixels;
    }

    public SKImage? Image(SlideImage image)
    {
        if (!_images.TryGetValue(image.Data, out var decoded))
        {
            // Encoded images stay encoded: Skia's PDF backend writes a JPEG as it is.
            decoded = MetafileReader.Detect(image.Data.AsSpan(0, Math.Min(image.Data.Length, 64))) is null ? SKImage.FromEncodedData(image.Data) : null;
            _images[image.Data] = decoded;
        }
        return decoded;
    }

    public MetafileDrawing? Metafile(SlideImage image)
    {
        if (!_metafiles.TryGetValue(image.Data, out var drawing))
        {
            try
            {
                drawing = MetafileReader.Detect(image.Data.AsSpan(0, Math.Min(image.Data.Length, 64))) is null ? null : MetafileReader.Draw(image.Data);
            }
            catch (Exception ex) when (ex is InvalidDataException or Core.Conversion.ConversionRefusedException or ArgumentException)
            {
                drawing = null;
            }
            _metafiles[image.Data] = drawing;
        }
        return drawing;
    }

    /// <summary>The picture's size in points (pixels at 96 dpi, a metafile's frame); null when it can't be read.</summary>
    public SKSize? Size(SlideImage image) =>
        Image(image) is { } bitmap ? new SKSize(bitmap.Width * 0.75f, bitmap.Height * 0.75f)
        : Metafile(image) is { } drawing ? new SKSize(drawing.Width, drawing.Height)
        : null;

    public void Dispose()
    {
        foreach (var image in _images.Values)
            image?.Dispose();
        foreach (var drawing in _metafiles.Values)
            drawing?.Dispose();
    }
}

internal static class SlidePaint
{
    /// <summary>
    /// Fills <paramref name="path"/> with <paramref name="fill"/> laid over <paramref name="box"/> (the element's own
    /// box), on <paramref name="canvas"/> in the element's coordinates.
    /// </summary>
    public static void Fill(SKCanvas canvas, SKPath path, SlideFill fill, SKRect box, SlideImages images)
    {
        switch (fill)
        {
            case SolidFill solid:
                using (var paint = new SKPaint { Color = solid.Color, IsAntialias = true, Style = SKPaintStyle.Fill })
                    canvas.DrawPath(path, paint);
                break;
            case GradientFill gradient:
                using (var shader = Gradient(gradient, box))
                using (var paint = new SKPaint { Shader = shader, IsAntialias = true, Style = SKPaintStyle.Fill })
                    canvas.DrawPath(path, paint);
                break;
            case PatternFill pattern:
                using (var shader = Pattern(pattern))
                using (var paint = new SKPaint { Shader = shader, IsAntialias = true, Style = SKPaintStyle.Fill })
                    canvas.DrawPath(path, paint);
                break;
            case PictureFill picture:
                canvas.Save();
                canvas.ClipPath(path, antialias: true);
                Picture(canvas, picture, box, images);
                canvas.Restore();
                break;
        }
    }

    /// <summary>A single colour standing for a fill (a gradient's middle, a pattern's foreground), for strokes and marks.</summary>
    public static SKColor Representative(SlideFill fill) => fill switch
    {
        SolidFill solid => solid.Color,
        GradientFill gradient => gradient.Stops[gradient.Stops.Count / 2].Color,
        PatternFill pattern => pattern.Foreground,
        _ => SKColors.Gray,
    };

    /// <summary>A paint for strokes in <paramref name="fill"/>: its colour, or the gradient over the box.</summary>
    public static SKPaint StrokePaint(SlideFill fill, SKRect box)
    {
        var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
        if (fill is GradientFill gradient)
            paint.Shader = Gradient(gradient, box);
        else
            paint.Color = Representative(fill);
        return paint;
    }

    public static SKShader Gradient(GradientFill gradient, SKRect box)
    {
        var colors = gradient.Stops.Select(s => s.Color).ToArray();
        var positions = gradient.Stops.Select(s => s.Position).ToArray();
        var width = Math.Max(box.Width, 0.01f);
        var height = Math.Max(box.Height, 0.01f);
        if (gradient.Shape == GradientShape.Linear)
        {
            var angle = gradient.Angle * MathF.PI / 180;
            var (dx, dy) = (MathF.Cos(angle), MathF.Sin(angle));
            if (gradient.Scaled)
            {
                // The angle of the unit square, stretched with the box.
                var half = (MathF.Abs(dx) + MathF.Abs(dy)) / 2;
                var local = SKMatrix.CreateScale(width, height).PostConcat(SKMatrix.CreateTranslation(box.Left, box.Top));
                return SKShader.CreateLinearGradient(new SKPoint(0.5f - dx * half, 0.5f - dy * half), new SKPoint(0.5f + dx * half, 0.5f + dy * half),
                    colors, positions, SKShaderTileMode.Clamp, local);
            }
            var length = (MathF.Abs(width * dx) + MathF.Abs(height * dy)) / 2;
            var center = new SKPoint(box.MidX, box.MidY);
            return SKShader.CreateLinearGradient(new SKPoint(center.X - dx * length, center.Y - dy * length),
                new SKPoint(center.X + dx * length, center.Y + dy * length), colors, positions, SKShaderTileMode.Clamp);
        }
        // Path gradients start at the focus rectangle's centre and end at the box's farthest corner.
        var focus = gradient.Focus;
        var fx = box.Left + width * (focus.Left + (1 - focus.Left - focus.Right) / 2);
        var fy = box.Top + height * (focus.Top + (1 - focus.Top - focus.Bottom) / 2);
        var rx = Math.Max(Math.Abs(fx - box.Left), Math.Abs(box.Right - fx));
        var ry = Math.Max(Math.Abs(fy - box.Top), Math.Abs(box.Bottom - fy));
        if (gradient.Shape == GradientShape.Circle)
        {
            var radius = MathF.Sqrt(rx * rx + ry * ry);
            return SKShader.CreateRadialGradient(new SKPoint(fx, fy), Math.Max(radius, 0.01f), colors, positions, SKShaderTileMode.Clamp);
        }
        // A rectangle: an ellipse through the box's corners stands in for the rectangles.
        var scaleY = ry / Math.Max(rx, 0.01f);
        var matrix = SKMatrix.CreateScale(1, scaleY, fx, fy);
        return SKShader.CreateRadialGradient(new SKPoint(fx, fy), Math.Max(rx * 1.4142f, 0.01f), colors, positions, SKShaderTileMode.Clamp, matrix);
    }

    /// <summary>Draws a picture fill into <paramref name="box"/> (clipped by the caller).</summary>
    public static void Picture(SKCanvas canvas, PictureFill picture, SKRect box, SlideImages images)
    {
        using var paint = Effects(picture.Effects);
        var stretch = picture.Stretch;
        var target = new SKRect(box.Left + box.Width * stretch.Left, box.Top + box.Height * stretch.Top,
            box.Right - box.Width * stretch.Right, box.Bottom - box.Height * stretch.Bottom);
        var crop = picture.Crop;
        if (images.Metafile(picture.Image) is { } drawing)
        {
            if (paint is not null)
                canvas.SaveLayer(paint);
            drawing.Draw(canvas, target, (crop.Left, crop.Top, crop.Right, crop.Bottom));
            if (paint is not null)
                canvas.Restore();
            return;
        }
        if (images.Image(picture.Image) is not { } image)
            return;
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        if (picture.Tile is { } tile)
        {
            var size = images.Size(picture.Image)!.Value;
            var tileWidth = size.Width * tile.ScaleX;
            var tileHeight = size.Height * tile.ScaleY;
            var (ax, ay) = tile.Align switch
            {
                "t" => (0.5f, 0f),
                "tr" => (1f, 0f),
                "l" => (0f, 0.5f),
                "ctr" => (0.5f, 0.5f),
                "r" => (1f, 0.5f),
                "bl" => (0f, 1f),
                "b" => (0.5f, 1f),
                "br" => (1f, 1f),
                _ => (0f, 0f),
            };
            var origin = new SKPoint(box.Left + (box.Width - tileWidth) * ax + tile.OffsetX, box.Top + (box.Height - tileHeight) * ay + tile.OffsetY);
            var matrix = SKMatrix.CreateScale(tileWidth / image.Width, tileHeight / image.Height).PostConcat(SKMatrix.CreateTranslation(origin.X, origin.Y));
            using var shader = SKShader.CreateImage(image, tile.FlipX ? SKShaderTileMode.Mirror : SKShaderTileMode.Repeat,
                tile.FlipY ? SKShaderTileMode.Mirror : SKShaderTileMode.Repeat, sampling, matrix);
            using var tilePaint = paint ?? new SKPaint();
            tilePaint.Shader = shader;
            tilePaint.IsAntialias = true;
            canvas.DrawRect(box, tilePaint);
            return;
        }
        // The crop insets are fractions of the picture: negative ones put room around it.
        var source = new SKRect(image.Width * crop.Left, image.Height * crop.Top, image.Width * (1 - crop.Right), image.Height * (1 - crop.Bottom));
        if (source.Width <= 0 || source.Height <= 0 || target.Width <= 0 || target.Height <= 0)
            return;
        canvas.Save();
        canvas.ClipRect(target);
        var map = SKMatrix.CreateScale(target.Width / source.Width, target.Height / source.Height);
        map = map.PreConcat(SKMatrix.CreateTranslation(-source.Left, -source.Top)).PostConcat(SKMatrix.CreateTranslation(target.Left, target.Top));
        canvas.Concat(in map);
        canvas.DrawImage(image, 0, 0, sampling, paint);
        canvas.Restore();
    }

    /// <summary>A paint applying a picture's recolouring; null when there is none.</summary>
    private static SKPaint? Effects(PictureEffects? effects)
    {
        if (effects is null || effects.IsNone)
            return null;
        SKColorFilter? filter = null;
        void Then(SKColorFilter next)
        {
            var before = filter;
            filter = before is null ? next : SKColorFilter.CreateCompose(next, before);
        }
        if (effects.Brightness != 0 || effects.Contrast != 0)
        {
            var c = 1 + effects.Contrast;
            var o = effects.Brightness + (1 - c) / 2;
            Then(SKColorFilter.CreateColorMatrix([c, 0, 0, 0, o, 0, c, 0, 0, o, 0, 0, c, 0, o, 0, 0, 0, 1, 0]));
        }
        const float Rw = 0.2126f, Gw = 0.7152f, Bw = 0.0722f;
        if (effects.Grayscale || effects.BiLevel is not null)
            Then(SKColorFilter.CreateColorMatrix([Rw, Gw, Bw, 0, 0, Rw, Gw, Bw, 0, 0, Rw, Gw, Bw, 0, 0, 0, 0, 0, 1, 0]));
        if (effects.BiLevel is { } threshold)
        {
            var table = new byte[256];
            for (var i = 0; i < 256; i++)
                table[i] = i / 255f >= threshold ? (byte)255 : (byte)0;
            Then(SKColorFilter.CreateTable(null, table, table, table));
        }
        if (effects.Duotone is var (dark, light))
        {
            float D(byte v) => v / 255f;
            float Span(byte a, byte b) => (b - a) / 255f;
            Then(SKColorFilter.CreateColorMatrix([
                Span(dark.Red, light.Red) * Rw, Span(dark.Red, light.Red) * Gw, Span(dark.Red, light.Red) * Bw, 0, D(dark.Red),
                Span(dark.Green, light.Green) * Rw, Span(dark.Green, light.Green) * Gw, Span(dark.Green, light.Green) * Bw, 0, D(dark.Green),
                Span(dark.Blue, light.Blue) * Rw, Span(dark.Blue, light.Blue) * Gw, Span(dark.Blue, light.Blue) * Bw, 0, D(dark.Blue),
                0, 0, 0, 1, 0]));
        }
        return new SKPaint
        {
            ColorFilter = filter,
            Color = SKColors.White.WithAlpha((byte)Math.Round(Math.Clamp(effects.Alpha, 0, 1) * 255)),
        };
    }

    // ───────────────────────── Patterns ─────────────────────────

    /// <summary>A pattern as a shader: an 8 × 8 cell, 6 pt across (8 pixels at 96 dpi, as PowerPoint draws it).</summary>
    public static SKShader Pattern(PatternFill pattern)
    {
        var bits = PatternBits(pattern.Preset);
        using var bitmap = new SKBitmap(8, 8, SKColorType.Rgba8888, SKAlphaType.Premul);
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
                bitmap.SetPixel(x, y, bits(x, y) ? pattern.Foreground : pattern.Background);
        using var image = SKImage.FromBitmap(bitmap);
        return image.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, new SKSamplingOptions(SKFilterMode.Nearest), SKMatrix.CreateScale(0.75f, 0.75f));
    }

    /// <summary>8 × 8 Bayer order, for the percentage patterns.</summary>
    private static readonly int[] Bayer =
    [
        0, 32, 8, 40, 2, 34, 10, 42, 48, 16, 56, 24, 50, 18, 58, 26, 12, 44, 4, 36, 14, 46, 6, 38, 60, 28, 52, 20, 62, 30, 54, 22,
        3, 35, 11, 43, 1, 33, 9, 41, 51, 19, 59, 27, 49, 17, 57, 25, 15, 47, 7, 39, 13, 45, 5, 37, 63, 31, 55, 23, 61, 29, 53, 21,
    ];

    /// <summary>Which pixels of the 8 × 8 cell take the foreground, for DrawingML's preset patterns.</summary>
    internal static Func<int, int, bool> PatternBits(string preset)
    {
        if (preset.StartsWith("pct", StringComparison.Ordinal) && int.TryParse(preset.AsSpan(3), out var percent))
            return (x, y) => Bayer[y * 8 + x] < percent * 64 / 100;
        return preset switch
        {
            "horz" => (_, y) => y % 4 == 0,
            "vert" => (x, _) => x % 4 == 0,
            "ltHorz" => (_, y) => y % 4 == 0,
            "ltVert" => (x, _) => x % 4 == 0,
            "dkHorz" => (_, y) => y % 4 < 2,
            "dkVert" => (x, _) => x % 4 < 2,
            "narHorz" => (_, y) => y % 2 == 0,
            "narVert" => (x, _) => x % 2 == 0,
            "dashHorz" => (x, y) => y % 4 == 0 && (x + y / 4 * 4) % 8 < 4,
            "dashVert" => (x, y) => x % 4 == 0 && (y + x / 4 * 4) % 8 < 4,
            "cross" or "smGrid" => (x, y) => x % 4 == 0 || y % 4 == 0,
            "lgGrid" => (x, y) => x == 0 || y == 0,
            "dotGrid" => (x, y) => x % 2 == 0 && y == 0 || y % 2 == 0 && x == 0,
            "dnDiag" or "ltDnDiag" => (x, y) => (x - y + 8) % 4 == 0,
            "upDiag" or "ltUpDiag" => (x, y) => (x + y) % 4 == 0,
            "dkDnDiag" => (x, y) => (x - y + 8) % 4 < 2,
            "dkUpDiag" => (x, y) => (x + y) % 4 < 2,
            "wdDnDiag" => (x, y) => (x - y + 8) % 8 < 3,
            "wdUpDiag" => (x, y) => (x + y) % 8 < 3,
            "dashDnDiag" => (x, y) => (x - y + 8) % 4 == 0 && y % 4 < 2,
            "dashUpDiag" => (x, y) => (x + y) % 4 == 0 && y % 4 < 2,
            "diagCross" => (x, y) => (x - y + 8) % 4 == 0 || (x + y) % 4 == 0,
            "smCheck" => (x, y) => (x / 2 + y / 2) % 2 == 0,
            "lgCheck" => (x, y) => (x / 4 + y / 4) % 2 == 0,
            "openDmnd" => (x, y) => Math.Abs(x - 4) + Math.Abs(y - 4) == 4,
            "solidDmnd" => (x, y) => Math.Abs(x - 4) + Math.Abs(y - 4) <= 3,
            "dotDmnd" => (x, y) => (x + y) % 4 == 0 && (x - y + 8) % 4 == 0,
            "horzBrick" => (x, y) => y % 4 == 0 || (y < 4 ? x == 0 : x == 4),
            "diagBrick" => (x, y) => (x + y) % 8 == 0 || (x - y + 8) % 8 == 0 && x < 4,
            "plaid" => (x, y) => y < 2 || x < 2 && y % 2 == 0,
            "weave" or "trellis" or "zigZag" or "wave" or "shingle" or "divot" or "sphere" or "smConfetti" or "lgConfetti" =>
                (x, y) => Bayer[y * 8 + x] < 24,
            _ => (x, y) => Bayer[y * 8 + x] < 32,
        };
    }
}
