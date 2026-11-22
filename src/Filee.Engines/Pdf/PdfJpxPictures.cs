// JPEG 2000 pictures of a PDF (/JPXDecode) through Filee's own decoder (Jpeg2000/), for what PdfPig can't decode:
// the backdrops of PDF → PPTX blends (PdfOwnPicture) and pictures the PostScript writer draws (PsImages).
//
// The picture's colours follow PDF's rules for JPXDecode (ISO 32000-2, 7.4.9): a /ColorSpace in the image
// dictionary is used and the file's own colour specification ignored; without one the JPEG 2000 data says (its
// colour space, palette and channel definitions). Grey, RGB and CMYK spaces (device, calibrated or ICC-based) take
// the decoded channels as they are, CMYK becoming RGB without a profile; Indexed, Separation, DeviceN and Lab ones
// map each pixel's samples through PdfPig's colour space. /SMaskInData 1 takes the file's opacity channel as the
// picture's alpha. /Decode is ignored, as the standard asks for pictures that aren't image masks.
//
// The picture may have at most PdfDocumentReader.MaxGraphicsPixels pixels; past that, or damaged, it is left out.

using Filee.Engines.Jpeg2000;
using SkiaSharp;
using UglyToad.PdfPig.Graphics.Colors;

namespace Filee.Engines.Pdf;

internal static class PdfJpxPictures
{
    /// <summary>Colours of a multi-channel space looked up through PdfPig at most per picture; past that they are approximated.</summary>
    private const int MaxLookups = 65_536;

    /// <summary>What a PDF's JPEG 2000 picture may make the decoder build.</summary>
    internal static Jpeg2000Limits Limits { get; } = new()
    {
        MaxPixels = (long)PdfDocumentReader.MaxGraphicsPixels,
        MaxSamples = 4 * (long)PdfDocumentReader.MaxGraphicsPixels,
    };

    /// <summary>A /JPXDecode stream's data decoded within <see cref="Limits"/>.</summary>
    public static Jpeg2000Image Decode(byte[] data) => Jpeg2000Decoder.Decode(data, Limits);

    /// <summary>
    /// The picture as 8-bit RGBA (alpha not premultiplied) in <paramref name="space"/>, the image dictionary's colour
    /// space (null when it has none); <paramref name="alphaInData"/>: /SMaskInData.
    /// </summary>
    public static byte[] Rgba(Jpeg2000Image picture, ColorSpaceDetails? space, bool alphaInData)
    {
        switch (space)
        {
            case null:
                return picture.Rgba(alpha: alphaInData);
            case { Type: ColorSpace.DeviceGray or ColorSpace.CalGray }:
                return picture.Rgba(1, alphaInData, palette: false);
            case { Type: ColorSpace.DeviceRGB or ColorSpace.CalRGB }:
                return picture.Rgba(3, alphaInData, palette: false);
            case { Type: ColorSpace.DeviceCMYK }:
                return picture.Rgba(4, alphaInData, palette: false);
            case ICCBasedColorSpaceDetails icc when icc.NumberOfColorComponents is 1 or 3 or 4:
                return picture.Rgba(icc.NumberOfColorComponents, alphaInData, palette: false);
        }
        return Mapped(picture, space, alphaInData);
    }

    /// <summary>Each pixel's samples through PdfPig's colour space (Indexed, Separation, DeviceN, Lab …).</summary>
    private static byte[] Mapped(Jpeg2000Image picture, ColorSpaceDetails space, bool alphaInData)
    {
        var indexed = space.Type == ColorSpace.Indexed;
        var count = Math.Clamp(space.NumberOfColorComponents, 1, 32);
        var planes = picture.Planes(usePalette: false, raw: indexed);
        if (planes.Count < count)
            return picture.Rgba(alpha: alphaInData);
        var opacity = alphaInData ? picture.Opacity(count) : null;
        var alpha = opacity is { } o && o < planes.Count ? planes[o] : null;
        var pixels = (long)picture.Width * picture.Height;
        var rgba = new byte[pixels * 4];
        var cache = new Dictionary<long, (byte R, byte G, byte B)>();
        var raw = new byte[count];
        var values = new double[count];
        for (long i = 0; i < pixels; i++)
        {
            long key = 0;
            for (var c = 0; c < count; c++)
            {
                raw[c] = planes[c][i];
                key = key * 257 + raw[c];
            }
            if (!cache.TryGetValue(key, out var color))
            {
                color = (128, 128, 128);
                if (cache.Count < MaxLookups)
                {
                    try
                    {
                        for (var c = 0; c < count; c++)
                        {
                            // Samples span each component's range: [0, 1], a colour table's indices, Lab's L* 0–100 and a*, b* −100–100.
                            values[c] = indexed ? raw[c]
                                : space.Type == ColorSpace.Lab ? (c == 0 ? raw[c] * 100.0 / 255 : raw[c] * 200.0 / 255 - 100)
                                : raw[c] / 255.0;
                        }
                        space.GetRgb(values, out var r, out var g, out var b);
                        color = (PdfShadings.Byte(r), PdfShadings.Byte(g), PdfShadings.Byte(b));
                    }
                    catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidOperationException or NotSupportedException
                                                   or InvalidCastException or NullReferenceException)
                    {
                        // A colour PdfPig can't tell: grey.
                    }
                    cache[key] = color;
                }
                else if (count >= 3)
                {
                    color = (raw[0], raw[1], raw[2]);
                }
            }
            var at = i * 4;
            (rgba[at], rgba[at + 1], rgba[at + 2], rgba[at + 3]) = (color.R, color.G, color.B, alpha?[i] ?? 255);
        }
        return rgba;
    }

    /// <summary>RGBA pixels as a Skia bitmap (unpremultiplied).</summary>
    public static SKBitmap Bitmap(byte[] rgba, int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        rgba.AsSpan(0, width * height * 4).CopyTo(bitmap.GetPixelSpan());
        return bitmap;
    }
}
