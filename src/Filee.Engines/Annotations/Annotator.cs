// "Annotate" in the file: the marks painted (AnnotationPainter, as the window draws them) on transparent layers that
// are laid over the picture, only over the part the marks cover and a band at a time, so a huge picture needs no second
// copy of itself. The picture as it is seen: before it is turned or resized.

using Filee.Core.Presets;
using Filee.Engines.Magick;
using ImageMagick;
using SkiaSharp;

namespace Filee.Engines.Annotations;

public static class Annotator
{
    /// <summary>
    /// A band of the layer holds at most this many pixels (16 MB as RGBA), however large the picture.
    /// </summary>
    private const int BandPixels = 4 << 20;

    /// <summary>
    /// Draws <see cref="ImageOptions.Annotations"/> on <paramref name="image"/> (upright); does nothing without marks.
    /// </summary>
    public static void Apply(IMagickImage<byte> image, ImageOptions options)
    {
        if (!options.HasAnnotations)
            return;
        int width = (int)image.Width, height = (int)image.Height;
        var marks = options.Annotations!;
        if (Covered(marks, width, height) is not { } area)
            return;

        // The marks are sRGB. A greyscale picture can't show them, so it becomes a colour one (through its own profile
        // when it has one).
        var profile = image.GetColorProfile();
        var space = image.ColorSpace;
        if (space is ColorSpace.Gray or ColorSpace.LinearGray)
        {
            if (profile?.ColorSpace is ColorSpace.Gray or ColorSpace.LinearGray)
            {
                image.TransformColorSpace(profile, ColorProfiles.SRGB);
                profile = null;
            }
            image.ColorSpace = ColorSpace.sRGB;
            image.ColorType = image.HasAlpha ? ColorType.TrueColorAlpha : ColorType.TrueColor;
            space = ColorSpace.sRGB;
            // An RGB profile stays, as the picture is now read through it.
            if (profile is not null && profile.ColorSpace == ColorSpace.sRGB && image.GetColorProfile() is null)
                image.SetProfile(profile);
        }
        // Each layer is converted to the picture's own colours: through its profile when it has one of its kind that
        // isn't sRGB (a phone's Display P3, a print CMYK), else by formula (CMYK without one). Copied channel by
        // channel, red came out teal on CMYK, and too vivid where a viewer applies a wide-gamut profile.
        var through = profile is not null && profile.ColorSpace == space
                      && profile.Description?.Contains("sRGB", StringComparison.OrdinalIgnoreCase) != true ? profile : null;
        var bandHeight = Math.Max(1, BandPixels / area.Width);
        for (var top = area.Top; top < area.Bottom; top += bandHeight)
        {
            using var band = Paint(marks, width, height, new SKRectI(area.Left, top, area.Right, Math.Min(area.Bottom, top + bandHeight)));
            if (through is not null)
            {
                band.RenderingIntent = RenderingIntent.Relative;
                band.TransformColorSpace(ColorProfiles.SRGB, through);
            }
            else if (space != ColorSpace.sRGB)
            {
                band.ColorSpace = space;
            }
            image.Composite(band, area.Left, top, CompositeOperator.Over);
        }
        // The small previews a file carries (EXIF's thumbnail) still show the picture without its marks.
        AreaHider.DropPreviews(image);
    }

    /// <summary>
    /// The part of the picture the marks can touch, in whole pixels, or null when they touch none of it.
    /// </summary>
    private static SKRectI? Covered(IEnumerable<Annotation> marks, int width, int height)
    {
        var (left, top, right, bottom) = (double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
        foreach (var mark in marks)
        {
            var extent = AnnotationPainter.ExtentOf(mark, width, height);
            if (extent.IsEmpty)
                continue;
            (left, top) = (Math.Min(left, extent.Left), Math.Min(top, extent.Top));
            (right, bottom) = (Math.Max(right, extent.Right), Math.Max(bottom, extent.Bottom));
        }
        var area = new SKRectI(Math.Max(0, (int)Math.Floor(left)), Math.Max(0, (int)Math.Floor(top)),
            Math.Min(width, (int)Math.Ceiling(right)), Math.Min(height, (int)Math.Ceiling(bottom)));
        return area.Width > 0 && area.Height > 0 ? area : null;
    }

    /// <summary>
    /// The marks over <paramref name="band"/>, as on the whole <paramref name="width"/> × <paramref name="height"/>.
    /// </summary>
    private static MagickImage Paint(IEnumerable<Annotation> marks, int width, int height, SKRectI band)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(band.Width, band.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            // Moved by whole pixels, so the bands meet without a seam.
            canvas.Translate(-band.Left, -band.Top);
            AnnotationPainter.Paint(canvas, marks, width, height);
        }
        // Skia draws premultiplied; ImageMagick reads straight alpha.
        var info = new SKImageInfo(band.Width, band.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
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
        var layer = new MagickImage();
        layer.ReadPixels(pixels, new PixelReadSettings((uint)band.Width, (uint)band.Height, StorageType.Char, PixelMapping.RGBA));
        return layer;
    }
}
