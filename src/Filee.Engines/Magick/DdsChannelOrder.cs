// Puts red and blue back where ImageMagick swaps them in one uncompressed DDS layout: 32-bit pixels stored R, G, B, A
// (masks 0xFF, 0xFF00, 0xFF0000, 0xFF000000, as DirectXTex writes R8G8B8A8 without a DX10 header). ImageMagick's coder
// compares the alpha mask with a mistyped constant and reads these bytes as B, G, R, A. Whether the bundled ImageMagick
// still does is checked once on a 1 × 1 picture of that layout, so a fixed coder is left alone.

using System.Buffers.Binary;
using ImageMagick;

namespace Filee.Engines.Magick;

/// <summary>Corrects the red/blue order of the DDS pictures ImageMagick reads swapped.</summary>
internal static class DdsChannelOrder
{
    private const int HeaderLength = 128;
    private const uint RgbFlag = 0x40;
    private const uint FourCcFlag = 0x4;
    private const uint AlphaPixelsFlag = 0x1;

    private enum Layout { Other, Alpha, NoAlpha }

    private static readonly Lazy<bool> AlphaReadsSwapped = new(() => ReadsSwapped(Layout.Alpha));
    private static readonly Lazy<bool> NoAlphaReadsSwapped = new(() => ReadsSwapped(Layout.NoAlpha));

    /// <summary>Swaps red and blue back in every frame ImageMagick read from the DDS at <paramref name="path"/>, when needed.</summary>
    public static void Correct(string path, IMagickImageCollection<byte> images)
    {
        var swapped = NeedsCorrection(path);
        foreach (var image in images)
            Swap(image, swapped);
    }

    /// <summary>Swaps red and blue back in the picture ImageMagick read from the DDS at <paramref name="path"/>, when needed.</summary>
    public static void Correct(string path, IMagickImage<byte> image) => Swap(image, NeedsCorrection(path));

    /// <summary>Swaps red and blue of <paramref name="image"/> when <paramref name="readsSwapped"/>.</summary>
    internal static void Swap(IMagickImage<byte> image, bool readsSwapped)
    {
        if (!readsSwapped)
            return;
        var (read, written) = image.HasAlpha ? (PixelMapping.RGBA, PixelMapping.BGRA) : (PixelMapping.RGB, PixelMapping.BGR);
        var pixels = image.GetPixels().ToByteArray(read)!;
        image.ImportPixels(pixels, new PixelImportSettings(image.Width, image.Height, StorageType.Char, written));
    }

    private static bool NeedsCorrection(string path) => ReadLayout(path) switch
    {
        Layout.Alpha => AlphaReadsSwapped.Value,
        Layout.NoAlpha => NoAlphaReadsSwapped.Value,
        _ => false,
    };

    /// <summary>The layout of the file's header, read after ImageMagick has read the file; only its first 128 bytes.</summary>
    private static Layout ReadLayout(string path)
    {
        Span<byte> header = stackalloc byte[HeaderLength];
        try
        {
            using var file = File.OpenRead(path);
            if (file.ReadAtLeast(header, HeaderLength, throwOnEndOfStream: false) < HeaderLength)
                return Layout.Other;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Layout.Other;
        }
        return LayoutOf(header);
    }

    private static Layout LayoutOf(ReadOnlySpan<byte> header)
    {
        var flags = At(header, 80);
        var matches = header[..4].SequenceEqual("DDS "u8) && At(header, 4) == 124 && At(header, 76) == 32
            && (flags & RgbFlag) != 0 && (flags & FourCcFlag) == 0 && At(header, 88) == 32
            && At(header, 92) == 0x000000FF && At(header, 96) == 0x0000FF00 && At(header, 100) == 0x00FF0000
            && At(header, 104) == 0xFF000000;
        if (!matches)
            return Layout.Other;
        return (flags & AlphaPixelsFlag) != 0 ? Layout.Alpha : Layout.NoAlpha;
    }

    private static uint At(ReadOnlySpan<byte> header, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(header[offset..]);

    /// <summary>Whether the bundled ImageMagick reads a red pixel of <paramref name="layout"/> as blue.</summary>
    private static bool ReadsSwapped(Layout layout)
    {
        var data = new byte[HeaderLength + 4];
        var header = data.AsSpan();
        "DDS "u8.CopyTo(header);
        Put(header, 4, 124);
        Put(header, 8, 0x100F); // caps, height, width, pitch and pixel format are set
        Put(header, 12, 1);
        Put(header, 16, 1);
        Put(header, 20, 4);
        Put(header, 76, 32);
        Put(header, 80, layout == Layout.Alpha ? RgbFlag | AlphaPixelsFlag : RgbFlag);
        Put(header, 88, 32);
        Put(header, 92, 0x000000FF);
        Put(header, 96, 0x0000FF00);
        Put(header, 100, 0x00FF0000);
        Put(header, 104, 0xFF000000);
        Put(header, 108, 0x1000); // a texture
        byte[] opaqueRed = [0xFF, 0, 0, 0xFF];
        opaqueRed.CopyTo(data, HeaderLength);
        try
        {
            using var image = new MagickImage(data, new MagickReadSettings { Format = MagickFormat.Dds });
            var color = image.GetPixels().GetPixel(0, 0).ToColor()!;
            return color.B > color.R;
        }
        catch (MagickException)
        {
            return false;
        }
    }

    private static void Put(Span<byte> header, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(header[offset..], value);
}
