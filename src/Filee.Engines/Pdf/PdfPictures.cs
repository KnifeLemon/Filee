// Pictures that go into a PDF through PDFsharp. Its PNG decoder reads 8 bits per channel (grey, grey + alpha, RGB,
// RGBA) and 4- or 8-bit palettes; a black-and-white scan saved as a 1-bit PNG, a grey one with 2 or 4 bits, or any
// 16-bit PNG fails with "Unsupported image format" (#605). ImageMagick writes those on its own for such pictures,
// so Filee writes its PNGs for PDFsharp here, and re-encodes a PNG it is handed when the decoder would refuse it.

using ImageMagick;

namespace Filee.Engines.Pdf;

internal static class PdfPictures
{
    /// <summary>
    /// Writes <paramref name="image"/> as a PNG PDFsharp reads: RGBA with transparency, 8-bit grey for grey and
    /// black-and-white pictures (they stay grey), else RGB. No palette PNGs: PDFsharp rejects them with transparency,
    /// and ImageMagick writes them for pictures with few colours (charts, metafiles, pixel art).
    /// </summary>
    public static void WritePng(IMagickImage<byte> image, Stream stream) => image.Write(stream, Prepare(image));

    /// <inheritdoc cref="WritePng(IMagickImage{byte}, Stream)"/>
    public static void WritePng(IMagickImage<byte> image, string path) => image.Write(path, Prepare(image));

    /// <summary>
    /// <paramref name="png"/> itself when PDFsharp reads it, else a copy in <paramref name="folder"/> written by
    /// <see cref="WritePng(IMagickImage{byte}, string)"/>. Files that aren't PNGs are returned as they are.
    /// </summary>
    public static string Readable(string png, string folder)
    {
        if (!NeedsReencoding(png))
            return png;
        using var image = new MagickImage(png);
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, $"{Guid.NewGuid():N}.png");
        WritePng(image, copy);
        return copy;
    }

    /// <summary>True for a PNG whose header PDFsharp's decoder refuses: a bit depth other than 8, or a palette.</summary>
    internal static bool NeedsReencoding(string path)
    {
        Span<byte> header = stackalloc byte[26];
        using (var file = File.OpenRead(path))
        {
            if (file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
                return false;
        }
        ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        if (!header[..8].SequenceEqual(signature) || !header[12..16].SequenceEqual("IHDR"u8))
            return false;
        var bitDepth = header[24];
        var colorType = header[25];
        return bitDepth != 8 || colorType is not (0 or 2 or 4 or 6);
    }

    private static MagickFormat Prepare(IMagickImage<byte> image)
    {
        if (image.HasAlpha)
            return MagickFormat.Png32;
        if (image.ColorType is not (ColorType.Grayscale or ColorType.Bilevel))
            return MagickFormat.Png24;
        // Plain "png" picks the smallest bit depth: 1 bit for black and white, which PDFsharp can't read.
        image.Settings.SetDefine(MagickFormat.Png, "bit-depth", "8");
        image.Settings.SetDefine(MagickFormat.Png, "color-type", "0");
        return MagickFormat.Png;
    }
}
