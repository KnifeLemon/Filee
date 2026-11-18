// Camera RAW tones (#1545): LibRaw writes its 8-bit pixels with its default BT.709 curve (gamm = 0.45, 4.5), but
// ImageMagick's DNG reader has no setting to change it and Filee treats the pixels as sRGB, so mid and dark tones
// came out too dark (18 % grey: 104 instead of 118). The values are turned into sRGB once, when the file is read.

using ImageMagick;

namespace Filee.Engines.Magick;

/// <summary>Turns the BT.709 tone curve LibRaw writes into the sRGB curve.</summary>
internal static class RawTone
{
    private static readonly Lazy<byte[]> Table = new(BuildTable);

    /// <summary>For each 8-bit value written with LibRaw's BT.709 curve, the 8-bit sRGB value of the same light.</summary>
    internal static byte[] Bt709ToSrgbTable() => (byte[])Table.Value.Clone();

    private static byte[] BuildTable()
    {
        var table = new byte[256];
        for (var i = 0; i < table.Length; i++)
        {
            var v = i / 255.0;
            // BT.709 back to linear light (LibRaw: gamm[0] = 0.45, gamm[1] = 4.5).
            var linear = v < 0.081 ? v / 4.5 : Math.Pow((v + 0.099) / 1.099, 1 / 0.45);
            // Linear light to sRGB.
            var srgb = linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
            table[i] = (byte)Math.Clamp((int)Math.Round(srgb * 255.0), 0, 255);
        }
        return table;
    }

    /// <summary>Applies the curve to the colour channels of every frame (alpha, if any, is left as it is).</summary>
    public static void ToSrgb(MagickImageCollection images)
    {
        foreach (var image in images)
            ToSrgb(image);
    }

    private static void ToSrgb(IMagickImage<byte> image)
    {
        var table = Table.Value;
        var pixels = image.GetPixels().ToByteArray(PixelMapping.RGB);
        if (pixels is null)
            return;
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = table[pixels[i]];
        image.ImportPixels(pixels, new PixelImportSettings(image.Width, image.Height, StorageType.Char, PixelMapping.RGB));
    }
}
