// Device-independent bitmaps as metafiles carry them ([MS-WMF] 2.2.2.9 DeviceIndependentBitmap): a BITMAPCOREHEADER
// or BITMAPINFOHEADER (and its V4 / V5 extensions), a colour table, and the pixels in rows padded to 32 bits, bottom-up
// unless the height is negative. 1, 4, 8, 16, 24 and 32 bits per pixel, uncompressed, RLE4 / RLE8 and bit fields;
// JPEG and PNG pixels are decoded by Skia. The pixel count is checked against the metafile's budget before anything is
// allocated.

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Filee.Engines.Metafiles;

/// <summary>How a bitmap's pixels are turned into colours.</summary>
/// <param name="PaletteIndices">DIB_PAL_COLORS: the colour table holds 16-bit indices into <paramref name="Palette"/>.</param>
/// <param name="Alpha">32-bit pixels carry premultiplied alpha (AlphaBlend with AC_SRC_ALPHA); otherwise it is ignored.</param>
/// <param name="TransparentColor">Pixels of this colour become transparent (TransparentBlt).</param>
internal sealed record DibOptions(bool PaletteIndices = false, SKColor[]? Palette = null, bool Alpha = false, SKColor? TransparentColor = null)
{
    public static readonly DibOptions Default = new();
}

/// <summary>A decoded bitmap.</summary>
/// <param name="Monochrome">One bit per pixel (pattern brushes draw these in the text and background colours).</param>
internal sealed record DibImage(SKImage Image, bool Monochrome) : IDisposable
{
    public int Width => Image.Width;

    public int Height => Image.Height;

    public void Dispose() => Image.Dispose();
}

/// <summary>A bitmap whose header or pixels don't fit together (the players report it as a damaged record).</summary>
internal sealed class DibFormatException(string message) : Exception(message);

/// <summary>Decodes DIBs.</summary>
internal static class Dib
{
    private const uint Rgb = 0, Rle8 = 1, Rle4 = 2, BitFields = 3, Jpeg = 4, Png = 5, AlphaBitFields = 6;

    /// <summary>Decodes a packed DIB: header, colour table and pixels one after the other (WMF records).</summary>
    public static DibImage? DecodePacked(ReadOnlySpan<byte> dib, DibOptions options, MetafileBudget budget)
    {
        var header = Header.Read(dib);
        var start = header.InfoSize(options.PaletteIndices);
        if (start > dib.Length)
            throw new DibFormatException("has a bitmap whose colour table is cut off");
        return Decode(dib[..start], dib[start..], options, budget);
    }

    /// <summary>Decodes a DIB whose header (with its colour table) and pixels are stored apart (EMF records).</summary>
    public static DibImage? Decode(ReadOnlySpan<byte> info, ReadOnlySpan<byte> bits, DibOptions options, MetafileBudget budget)
    {
        var header = Header.Read(info);
        if (header.Width <= 0 || header.Height is 0 or int.MinValue)
            return null;
        var width = header.Width;
        var height = Math.Abs(header.Height);
        if (header.Compression is Jpeg or Png)
            return Encoded(bits, budget);

        budget.Bitmap(width, height);
        var colors = ColorTable(info, header, options);
        var pixels = new uint[(long)width * height]; // BGRA, top-down
        var bottomUp = header.Height > 0;
        switch (header.Compression)
        {
            case Rle8 when header.BitCount == 8:
            case Rle4 when header.BitCount == 4:
                RunLength(bits, header.Compression == Rle4, width, height, bottomUp, colors, pixels);
                break;
            case Rgb:
            case BitFields:
            case AlphaBitFields:
                Uncompressed(bits, header, info, colors, options, width, height, bottomUp, pixels);
                break;
            default:
                return null; // CMYK or an unknown compression: nothing GDI would draw either
        }

        if (options.TransparentColor is { } key)
        {
            var match = Bgra(key) & 0x00FFFFFF;
            for (var i = 0; i < pixels.Length; i++)
            {
                if ((pixels[i] & 0x00FFFFFF) == match)
                    pixels[i] = 0;
            }
        }

        var alphaType = options.Alpha && header.BitCount == 32 ? SKAlphaType.Premul : SKAlphaType.Unpremul;
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, alphaType));
        var bytes = MemoryMarshal.AsBytes(pixels.AsSpan());
        if (bitmap.RowBytes != width * 4)
            throw new InvalidOperationException("Skia gave the bitmap unexpected row bytes.");
        Marshal.Copy(bytes.ToArray(), 0, bitmap.GetPixels(), bytes.Length);
        bitmap.SetImmutable();
        var image = SKImage.FromBitmap(bitmap) ?? throw new InvalidOperationException("Skia could not make an image of the bitmap.");
        return new DibImage(image, header.BitCount == 1);
    }

    /// <summary>JPEG or PNG pixels (BI_JPEG, BI_PNG), decoded by Skia once their size is known to be within the budget.</summary>
    private static DibImage? Encoded(ReadOnlySpan<byte> bits, MetafileBudget budget)
    {
        using var data = SKData.CreateCopy(bits);
        using var codec = SKCodec.Create(data);
        if (codec is null)
            throw new DibFormatException("has a JPEG or PNG bitmap that can't be read");
        budget.Bitmap(codec.Info.Width, codec.Info.Height);
        var image = SKImage.FromEncodedData(data);
        return image is null ? null : new DibImage(image, false);
    }

    private static SKColor[] ColorTable(ReadOnlySpan<byte> info, Header header, DibOptions options)
    {
        if (header.BitCount > 8)
            return [];
        var count = header.TableEntries;
        var entrySize = options.PaletteIndices ? 2 : header.Core ? 3 : 4;
        var start = header.Size + header.MaskBytes;
        if ((long)start + (long)count * entrySize > info.Length)
            throw new DibFormatException("has a bitmap whose colour table is cut off");
        var colors = new SKColor[1 << header.BitCount];
        for (var i = 0; i < colors.Length; i++)
            colors[i] = SKColors.Black;
        for (var i = 0; i < Math.Min(count, colors.Length); i++)
        {
            var entry = info.Slice(start + i * entrySize, entrySize);
            if (options.PaletteIndices)
            {
                var index = BinaryPrimitives.ReadUInt16LittleEndian(entry);
                colors[i] = options.Palette is { Length: > 0 } palette ? palette[index % palette.Length]
                    : new SKColor((byte)index, (byte)index, (byte)index);
            }
            else
            {
                colors[i] = new SKColor(entry[2], entry[1], entry[0]);
            }
        }
        return colors;
    }

    private static void Uncompressed(ReadOnlySpan<byte> bits, Header header, ReadOnlySpan<byte> info, SKColor[] colors, DibOptions options,
        int width, int height, bool bottomUp, uint[] pixels)
    {
        var bpp = header.BitCount;
        if (bpp is not (1 or 2 or 4 or 8 or 16 or 24 or 32))
            throw new DibFormatException($"has a bitmap of {bpp} bits per pixel");
        var stride = ((long)width * bpp + 31) / 32 * 4;
        if (stride * height > bits.Length)
            throw new DibFormatException($"has a bitmap of {width} × {height} pixels whose pixel data is cut off");
        var palette = new uint[colors.Length];
        for (var i = 0; i < colors.Length; i++)
            palette[i] = Bgra(colors[i]);

        var (red, green, blue, alpha) = Masks(header, info);
        var keepAlpha = options.Alpha && bpp == 32;
        for (var y = 0; y < height; y++)
        {
            var row = bits.Slice((int)(stride * y), (int)stride);
            var target = (bottomUp ? height - 1 - y : y) * width;
            for (var x = 0; x < width; x++)
            {
                uint pixel;
                switch (bpp)
                {
                    case <= 8:
                        var perByte = 8 / bpp;
                        var shift = 8 - bpp * (x % perByte + 1);
                        pixel = palette[(row[x / perByte] >> shift) & ((1 << bpp) - 1)];
                        break;
                    case 24:
                        pixel = 0xFF000000 | (uint)row[x * 3 + 2] << 16 | (uint)row[x * 3 + 1] << 8 | row[x * 3];
                        break;
                    case 16:
                        pixel = Masked(BinaryPrimitives.ReadUInt16LittleEndian(row[(x * 2)..]), red, green, blue, alpha, false);
                        break;
                    default:
                        pixel = Masked(BinaryPrimitives.ReadUInt32LittleEndian(row[(x * 4)..]), red, green, blue, alpha, keepAlpha);
                        break;
                }
                pixels[target + x] = pixel;
            }
        }
    }

    /// <summary>Colour masks of 16- and 32-bit pixels: from the header or after it for bit fields, else 5-5-5 and 8-8-8.</summary>
    private static (uint Red, uint Green, uint Blue, uint Alpha) Masks(Header header, ReadOnlySpan<byte> info)
    {
        if (header.Compression is BitFields or AlphaBitFields)
        {
            var at = header.Size >= 52 ? 40 : header.Size;
            if (at + 12 > info.Length)
                throw new DibFormatException("has a bitmap whose colour masks are cut off");
            var alpha = header.Compression == AlphaBitFields || header.Size >= 56
                ? (at + 16 <= info.Length ? BinaryPrimitives.ReadUInt32LittleEndian(info[(at + 12)..]) : 0)
                : 0;
            return (BinaryPrimitives.ReadUInt32LittleEndian(info[at..]), BinaryPrimitives.ReadUInt32LittleEndian(info[(at + 4)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(info[(at + 8)..]), alpha);
        }
        return header.BitCount == 16 ? (0x7C00u, 0x03E0u, 0x001Fu, 0u) : (0x00FF0000u, 0x0000FF00u, 0x000000FFu, 0xFF000000u);
    }

    private static uint Masked(uint value, uint red, uint green, uint blue, uint alpha, bool keepAlpha) =>
        (keepAlpha && alpha != 0 ? Channel(value, alpha) : 0xFFu) << 24 | Channel(value, red) << 16 | Channel(value, green) << 8 | Channel(value, blue);

    /// <summary>The bits of <paramref name="mask"/> in <paramref name="value"/>, scaled to 0–255.</summary>
    private static uint Channel(uint value, uint mask)
    {
        if (mask == 0)
            return 0;
        var shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        var bits = System.Numerics.BitOperations.PopCount(mask);
        var max = (1u << Math.Min(bits, 31)) - 1;
        var level = (value & mask) >> shift;
        return bits >= 8 ? level >> (bits - 8) & 0xFF : (uint)((level * 255 + max / 2) / max);
    }

    /// <summary>RLE8 and RLE4 ([MS-WMF] 3.1.6): runs and literal groups, end of line, end of bitmap and deltas.</summary>
    private static void RunLength(ReadOnlySpan<byte> bits, bool four, int width, int height, bool bottomUp, SKColor[] colors, uint[] pixels)
    {
        var palette = new uint[colors.Length];
        for (var i = 0; i < colors.Length; i++)
            palette[i] = Bgra(colors[i]);
        int x = 0, y = 0, at = 0;
        void Put(int index)
        {
            if (x < width && y < height)
                pixels[(bottomUp ? height - 1 - y : y) * width + x] = palette[index % palette.Length];
            x++;
        }
        while (at + 1 < bits.Length && y < height)
        {
            int count = bits[at], value = bits[at + 1];
            at += 2;
            if (count > 0)
            {
                for (var i = 0; i < count; i++)
                    Put(four ? (i % 2 == 0 ? value >> 4 : value & 0x0F) : value);
                continue;
            }
            switch (value)
            {
                case 0: // end of line
                    x = 0;
                    y++;
                    break;
                case 1: // end of bitmap
                    return;
                case 2: // delta
                    if (at + 1 >= bits.Length)
                        return;
                    x += bits[at];
                    y += bits[at + 1];
                    at += 2;
                    break;
                default: // literal pixels, padded to a 16-bit boundary
                    var bytes = four ? (value + 1) / 2 : value;
                    if (at + bytes > bits.Length)
                        throw new DibFormatException("has a run-length bitmap that is cut off");
                    for (var i = 0; i < value; i++)
                        Put(four ? (i % 2 == 0 ? bits[at + i / 2] >> 4 : bits[at + i / 2] & 0x0F) : bits[at + i]);
                    at += (bytes + 1) & ~1;
                    break;
            }
        }
    }

    private static uint Bgra(SKColor color) => (uint)color.Alpha << 24 | (uint)color.Red << 16 | (uint)color.Green << 8 | color.Blue;

    /// <summary>The fields of a BITMAPCOREHEADER or BITMAPINFOHEADER the decoder uses.</summary>
    private readonly record struct Header(int Size, int Width, int Height, int BitCount, uint Compression, uint ColorsUsed)
    {
        public bool Core => Size == 12;

        /// <summary>Bytes of bit-field masks stored after a 40-byte header.</summary>
        public int MaskBytes => Size == 40 ? Compression switch { BitFields => 12, AlphaBitFields => 16, _ => 0 } : 0;

        /// <summary>Entries of the colour table.</summary>
        public int TableEntries => (int)(ColorsUsed > 0 ? Math.Min(ColorsUsed, 65_536u) : BitCount <= 8 ? 1u << BitCount : 0u);

        /// <summary>Bytes from the start of a packed DIB to its pixels.</summary>
        public int InfoSize(bool paletteIndices) => Size + MaskBytes + TableEntries * (paletteIndices ? 2 : Core ? 3 : 4);

        public static Header Read(ReadOnlySpan<byte> info)
        {
            if (info.Length < 12)
                throw new DibFormatException("has a bitmap without a header");
            var size = BinaryPrimitives.ReadInt32LittleEndian(info);
            if (size == 12)
            {
                return new Header(12, BinaryPrimitives.ReadUInt16LittleEndian(info[4..]), BinaryPrimitives.ReadUInt16LittleEndian(info[6..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(info[10..]), Rgb, 0);
            }
            if (size < 40 || size > info.Length)
                throw new DibFormatException($"has a bitmap header of {size} bytes");
            return new Header(size, BinaryPrimitives.ReadInt32LittleEndian(info[4..]), BinaryPrimitives.ReadInt32LittleEndian(info[8..]),
                BinaryPrimitives.ReadUInt16LittleEndian(info[14..]), BinaryPrimitives.ReadUInt32LittleEndian(info[16..]),
                BinaryPrimitives.ReadUInt32LittleEndian(info[32..]));
        }
    }
}
