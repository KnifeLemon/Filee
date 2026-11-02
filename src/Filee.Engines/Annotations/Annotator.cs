// "Annotate" in the file: the marks painted (AnnotationPainter, as the window draws them) on a transparent layer the
// size of the picture, then laid over it. The picture as it is seen: before it is turned or resized.

using Filee.Core.Presets;
using ImageMagick;
using SkiaSharp;

namespace Filee.Engines.Annotations;

public static class Annotator
{
    /// <summary>
    /// Draws <see cref="ImageOptions.Annotations"/> on <paramref name="image"/> (upright); does nothing without marks.
    /// </summary>
    public static void Apply(IMagickImage<byte> image, ImageOptions options)
    {
        if (!options.HasAnnotations)
            return;
        int width = (int)image.Width, height = (int)image.Height;
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            AnnotationPainter.Paint(canvas, options.Annotations!, width, height);
        }
        // Skia draws premultiplied; ImageMagick reads straight alpha.
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pixels = new byte[info.BytesSize];
        var pinned = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            bitmap.PeekPixels().ReadPixels(info, pinned.AddrOfPinnedObject(), info.RowBytes);
        }
        finally
        {
            pinned.Free();
        }
        using var layer = new MagickImage();
        layer.ReadPixels(pixels, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGBA));
        image.Composite(layer, 0, 0, CompositeOperator.Over);
    }
}
