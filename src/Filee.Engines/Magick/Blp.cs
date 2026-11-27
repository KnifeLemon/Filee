// Reads Blizzard's texture file (.blp), used by Warcraft III (BLP1) and World of Warcraft (BLP2): mipmap 0 as one
// picture. BLP1 supports palette-indexed and JPEG-compressed textures; BLP2 supports palette-indexed, DXT1/DXT3/DXT5
// (reusing Vtf's block decoder) and uncompressed BGRA textures. BLP0 (Warcraft III beta, external mipmaps) and damaged
// or oversized files are refused before allocating pixel buffers.

using System.Buffers.Binary;
using Filee.Core.Conversion;
using ImageMagick;

namespace Filee.Engines.Magick;

internal static class Blp
{
    private const int Blp1HeaderSize = 156;
    private const int Blp2HeaderSize = 148;
    private const int PaletteBytes = 256 * 4;
    private const int Blp2HeaderAndPaletteSize = Blp2HeaderSize + PaletteBytes;

    /// <summary>The longest side Filee reads from a BLP texture.</summary>
    internal const int MaxSide = 65_535;

    /// <summary>
    /// The most pixels Filee reads from one texture (width × height), as for a Paint.NET picture or VTF texture: 1 GB
    /// of RGBA. Checked before anything is allocated, so a small file claiming a huge texture asks for nothing.
    /// </summary>
    internal const long MaxPixels = PaintNet.MaxPixels;

    /// <summary>True when a <paramref name="width"/> × <paramref name="height"/> texture fits <see cref="MaxPixels"/>.</summary>
    internal static bool FitsBudget(int width, int height) => (long)width * height <= MaxPixels;

    /// <summary>Reads the largest mipmap (level 0) of the BLP texture at <paramref name="path"/>.</summary>
    public static MagickImageCollection Read(string path)
    {
        var data = File.ReadAllBytes(path);
        var (width, height, rgba) = Decode(data);
        var images = new MagickImageCollection();
        var image = new MagickImage();
        try
        {
            image.ReadPixels(rgba, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGBA));
            images.Add(image);
        }
        catch
        {
            image.Dispose();
            images.Dispose();
            throw;
        }
        return images;
    }

    /// <summary>Decodes mipmap 0 of a BLP1 or BLP2 file into 8-bit RGBA pixels, top row first.</summary>
    internal static (int Width, int Height, byte[] Rgba) Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            throw Refuse("This is not a Blizzard texture (BLP) file.");
        var magic = data[..4];
        if (magic.SequenceEqual("BLP0"u8))
            throw Refuse("BLP0 textures (Warcraft III beta, separate mipmap files) are not supported.");
        if (magic.SequenceEqual("BLP1"u8))
            return DecodeBlp1(data);
        if (magic.SequenceEqual("BLP2"u8))
            return DecodeBlp2(data);
        throw Refuse("This is not a Blizzard texture (BLP) file.");
    }

    private static (int Width, int Height, byte[] Rgba) DecodeBlp1(ReadOnlySpan<byte> data)
    {
        if (data.Length < Blp1HeaderSize)
            throw Refuse("The BLP1 header is cut off.");

        var compression = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        var alphaBits = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        var rawWidth = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        var rawHeight = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        var mipOffset0 = BinaryPrimitives.ReadUInt32LittleEndian(data[28..]);
        var mipSize0 = BinaryPrimitives.ReadUInt32LittleEndian(data[92..]);

        var (width, height) = ValidateDimensions(rawWidth, rawHeight);
        if (alphaBits is not (0 or 1 or 4 or 8))
            throw Refuse($"Unsupported BLP1 alpha bit depth ({alphaBits}).");

        return compression switch
        {
            0 => DecodeBlp1Jpeg(data, (int)alphaBits, mipOffset0, mipSize0),
            1 => DecodeBlp1Palette(data, width, height, (int)alphaBits, mipOffset0, mipSize0),
            _ => throw Refuse($"Unsupported BLP1 compression ({compression})."),
        };
    }

    private static (int Width, int Height, byte[] Rgba) DecodeBlp1Palette(
        ReadOnlySpan<byte> data, int width, int height, int alphaBits, uint mipOffset0, uint mipSize0)
    {
        if (data.Length < Blp1HeaderSize + PaletteBytes)
            throw Refuse("The BLP1 palette is cut off.");
        var palette = data.Slice(Blp1HeaderSize, PaletteBytes);
        var mip0 = SliceMip0(data, mipOffset0, mipSize0);
        return (width, height, DecodePalette(palette, mip0, width, height, alphaBits));
    }

    private static (int Width, int Height, byte[] Rgba) DecodeBlp1Jpeg(
        ReadOnlySpan<byte> data, int alphaBits, uint mipOffset0, uint mipSize0)
    {
        if (data.Length < Blp1HeaderSize + 4)
            throw Refuse("The BLP1 JPEG header size is missing.");
        long headerSize = BinaryPrimitives.ReadUInt32LittleEndian(data[Blp1HeaderSize..]);
        if (Blp1HeaderSize + 4L + headerSize > data.Length)
            throw Refuse("The BLP1 JPEG header extends past the end of the file.");

        var mip0 = SliceMip0(data, mipOffset0, mipSize0);
        var totalBytes = headerSize + mip0.Length;
        if (totalBytes == 0 || totalBytes > Array.MaxLength)
            throw Refuse("The BLP1 JPEG stream size is invalid.");

        var jpeg = new byte[(int)totalBytes];
        data.Slice(Blp1HeaderSize + 4, (int)headerSize).CopyTo(jpeg);
        mip0.CopyTo(jpeg.AsSpan((int)headerSize));

        // Check the JPEG header before decoding pixels so a small file cannot expand past the budget.
        MagickImageInfo info;
        try
        {
            info = new MagickImageInfo(jpeg);
        }
        catch (MagickException)
        {
            throw Refuse("The BLP1 JPEG stream could not be decoded.");
        }
        if (info.Format is not (MagickFormat.Jpeg or MagickFormat.Jpg or MagickFormat.Pjpeg))
            throw Refuse("The BLP1 JPEG stream is not valid JPEG data.");
        ValidateDimensions(info.Width, info.Height);

        using var decoded = DecodeJpeg(jpeg);
        var (width, height) = ValidateDimensions(decoded.Width, decoded.Height);
        var pixelCount = (long)width * height;
        var rgba = new byte[pixelCount * 4];

        if (decoded.ColorSpace == ColorSpace.CMYK)
        {
            // A 4-channel BLP1 JPEG stores raw B, G, R, A across the four channels without CMYK conversion.
            // ImageMagick reads any 4-channel JPEG as Adobe-style inverted CMYK and hands back 255 - sample for every
            // channel, with or without an Adobe marker, so each value is flipped back to the stored sample.
            var raw = decoded.GetPixels().ToByteArray("CMYK")
                ?? throw Refuse("The BLP1 JPEG pixels could not be read.");
            var opaque = alphaBits == 0;
            for (var i = 0; i < (int)pixelCount; i++)
            {
                var o = i * 4;
                rgba[o] = (byte)(255 - raw[o + 2]);
                rgba[o + 1] = (byte)(255 - raw[o + 1]);
                rgba[o + 2] = (byte)(255 - raw[o]);
                rgba[o + 3] = opaque ? (byte)255 : (byte)(255 - raw[o + 3]);
            }
        }
        else
        {
            // A 3-channel BLP1 JPEG stores blue first: swap red and blue back.
            var raw = decoded.GetPixels().ToByteArray(PixelMapping.RGB)
                ?? throw Refuse("The BLP1 JPEG pixels could not be read.");
            for (var i = 0; i < (int)pixelCount; i++)
            {
                var src = i * 3;
                var dst = i * 4;
                rgba[dst] = raw[src + 2];
                rgba[dst + 1] = raw[src + 1];
                rgba[dst + 2] = raw[src];
                rgba[dst + 3] = 255;
            }
        }

        return (width, height, rgba);
    }

    private static MagickImage DecodeJpeg(byte[] jpeg)
    {
        try
        {
            return new MagickImage(jpeg, new MagickReadSettings { Format = MagickFormat.Jpeg });
        }
        catch (MagickException)
        {
            throw Refuse("The BLP1 JPEG stream could not be decoded.");
        }
    }

    private static (int Width, int Height, byte[] Rgba) DecodeBlp2(ReadOnlySpan<byte> data)
    {
        if (data.Length < Blp2HeaderAndPaletteSize)
            throw Refuse("The BLP2 header is cut off.");

        var type = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (type != 1)
            throw Refuse($"Unsupported BLP2 type ({type}).");

        int encoding = data[8];
        int alphaDepth = data[9];
        int alphaType = data[10];
        var rawWidth = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        var rawHeight = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        var mipOffset0 = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
        var mipSize0 = BinaryPrimitives.ReadUInt32LittleEndian(data[84..]);

        var (width, height) = ValidateDimensions(rawWidth, rawHeight);
        if (alphaDepth is not (0 or 1 or 4 or 8))
            throw Refuse($"Unsupported BLP2 alpha depth ({alphaDepth}).");

        var mip0 = SliceMip0(data, mipOffset0, mipSize0);
        return encoding switch
        {
            1 => (width, height, DecodePalette(data.Slice(Blp2HeaderSize, PaletteBytes), mip0, width, height, alphaDepth)),
            2 => (width, height, DecodeBlp2Dxt(mip0, width, height, alphaDepth, alphaType)),
            3 => (width, height, DecodeBlp2Bgra(mip0, width, height, alphaDepth)),
            _ => throw Refuse($"Unsupported BLP2 encoding ({encoding})."),
        };
    }

    private static byte[] DecodeBlp2Dxt(ReadOnlySpan<byte> mip0, int width, int height, int alphaDepth, int alphaType)
    {
        Vtf.Format dxtFormat;
        if (alphaDepth is 0 or 1)
            dxtFormat = Vtf.Format.Dxt1;
        else if (alphaType == 7)
            dxtFormat = Vtf.Format.Dxt5;
        else if (alphaType == 1 || alphaDepth == 4)
            dxtFormat = Vtf.Format.Dxt3;
        else
            throw Refuse($"Unsupported BLP2 DXT alpha type ({alphaType}, depth {alphaDepth}).");

        var needed = Vtf.DataSize(dxtFormat, width, height);
        if (mip0.Length < needed)
            throw Refuse("The BLP2 file is cut off: mipmap 0 holds less DXT data than its header claims.");

        var rgba = new byte[(long)width * height * 4];
        Vtf.DecodeBlocks(mip0, dxtFormat, width, height, rgba);
        if (alphaDepth == 0)
        {
            for (var i = 3; i < rgba.Length; i += 4)
                rgba[i] = 255;
        }
        return rgba;
    }

    private static byte[] DecodeBlp2Bgra(ReadOnlySpan<byte> mip0, int width, int height, int alphaDepth)
    {
        var pixelCount = (long)width * height;
        var needed = pixelCount * 4;
        if (mip0.Length < needed)
            throw Refuse("The BLP2 file is cut off: mipmap 0 holds less pixel data than its header claims.");

        var rgba = new byte[needed];
        var opaque = alphaDepth == 0;
        for (var i = 0; i < (int)pixelCount; i++)
        {
            var o = i * 4;
            rgba[o] = mip0[o + 2];
            rgba[o + 1] = mip0[o + 1];
            rgba[o + 2] = mip0[o];
            rgba[o + 3] = opaque ? (byte)255 : mip0[o + 3];
        }
        return rgba;
    }

    private static byte[] DecodePalette(
        ReadOnlySpan<byte> palette, ReadOnlySpan<byte> mip0, int width, int height, int alphaBits)
    {
        var pixelCount = (long)width * height;
        long alphaBytes = alphaBits switch
        {
            0 => 0,
            1 => (pixelCount + 7) / 8,
            4 => (pixelCount + 1) / 2,
            8 => pixelCount,
            _ => throw Refuse($"Unsupported BLP alpha bit depth ({alphaBits})."),
        };
        if (mip0.Length < pixelCount + alphaBytes)
            throw Refuse("The BLP file is cut off: mipmap 0 holds less palette data than its header claims.");

        var rgba = new byte[pixelCount * 4];
        var alphaData = alphaBytes > 0 ? mip0.Slice((int)pixelCount, (int)alphaBytes) : ReadOnlySpan<byte>.Empty;
        for (var i = 0; i < (int)pixelCount; i++)
        {
            var p = mip0[i] * 4;
            var o = i * 4;
            rgba[o] = palette[p + 2];
            rgba[o + 1] = palette[p + 1];
            rgba[o + 2] = palette[p];
            rgba[o + 3] = alphaBits switch
            {
                8 => alphaData[i],
                4 => (byte)(((alphaData[i >> 1] >> ((i & 1) * 4)) & 0xF) * 17),
                1 => ((alphaData[i >> 3] >> (i & 7)) & 1) != 0 ? (byte)255 : (byte)0,
                _ => 255,
            };
        }
        return rgba;
    }

    private static (int Width, int Height) ValidateDimensions(uint rawWidth, uint rawHeight)
    {
        if (rawWidth == 0 || rawHeight == 0)
            throw Refuse("The BLP file has no pixels.");
        if (rawWidth > MaxSide || rawHeight > MaxSide)
            throw Refuse($"The texture is too large to read ({rawWidth} × {rawHeight}; Filee reads up to {MaxSide} px a side).");
        var width = (int)rawWidth;
        var height = (int)rawHeight;
        if (!FitsBudget(width, height))
            throw Refuse($"The texture is too large to read ({width} × {height}).");
        return (width, height);
    }

    private static ReadOnlySpan<byte> SliceMip0(ReadOnlySpan<byte> data, uint rawOffset, uint rawSize)
    {
        long offset = rawOffset;
        long size = rawSize;
        if (size == 0)
            throw Refuse("The BLP file has no mipmap data.");
        if (offset + size > data.Length)
            throw Refuse("The BLP file is cut off: mipmap 0 extends past the end of the file.");
        return data.Slice((int)offset, (int)size);
    }

    private static ConversionRefusedException Refuse(string reason) =>
        new("error.unsupported_source", reason);
}
