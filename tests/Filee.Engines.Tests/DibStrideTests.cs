// Metafile bitmaps (Metafiles/Dib) decoded straight into the SKBitmap Skia draws from: every depth at widths that
// leave each kind of row padding, bottom-up and top-down, checked pixel by pixel, and run-length bitmaps whose runs,
// deltas and literal groups point past the bitmap, which must land nowhere. A wrong row stride or an index past the
// pixels would show here as wrong colours or an exception instead of memory written outside the bitmap.

using System.Buffers.Binary;
using Filee.Engines.Metafiles;
using SkiaSharp;

namespace Filee.Engines.Tests;

public class DibStrideTests
{
    private static byte[] Header(int width, int height, int bpp, uint compression = 0, int colors = 0)
    {
        var header = new byte[40];
        BinaryPrimitives.WriteInt32LittleEndian(header, 40);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), height);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), (ushort)bpp);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), compression);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(32), colors);
        return header;
    }

    /// <summary>A colour table of <paramref name="count"/> entries, each telling its index apart.</summary>
    private static byte[] Table(int count) =>
        [.. Enumerable.Range(0, count).SelectMany(i => new byte[] { (byte)i, (byte)(255 - i), (byte)(i * 7), 0 })];

    private static SKColor Entry(int i) => new((byte)(i * 7), (byte)(255 - i), (byte)i);

    /// <summary>The colour the test puts at (x, y), as the decoder should read it back.</summary>
    private static SKColor Expected(int bpp, int x, int y) => bpp switch
    {
        1 => Entry((x ^ y) & 1),
        2 => Entry((x + y) & 3),
        4 => Entry((x + 2 * y) & 15),
        8 => Entry((x + 3 * y) & 255),
        16 => new SKColor(Scale5(y), Scale5(x), Scale5(x + y)),
        _ => new SKColor(0x80, (byte)y, (byte)x),
    };

    private static byte Scale5(int value) => (byte)(((value & 31) * 255 + 15) / 31);

    /// <summary>Rows padded to 32 bits, bottom row first unless <paramref name="topDown"/>.</summary>
    private static byte[] Pixels(int width, int height, int bpp, bool topDown)
    {
        var stride = (width * bpp + 31) / 32 * 4;
        var bits = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var row = bits.AsSpan((topDown ? y : height - 1 - y) * stride, stride);
            for (var x = 0; x < width; x++)
            {
                switch (bpp)
                {
                    case <= 8:
                        var index = bpp switch { 1 => (x ^ y) & 1, 2 => (x + y) & 3, 4 => (x + 2 * y) & 15, _ => (x + 3 * y) & 255 };
                        var perByte = 8 / bpp;
                        row[x / perByte] |= (byte)(index << (8 - bpp * (x % perByte + 1)));
                        break;
                    case 16:
                        BinaryPrimitives.WriteUInt16LittleEndian(row[(x * 2)..], (ushort)((y & 31) << 10 | (x & 31) << 5 | ((x + y) & 31)));
                        break;
                    default:
                        var at = x * (bpp / 8);
                        (row[at], row[at + 1], row[at + 2]) = ((byte)x, (byte)y, 0x80);
                        break;
                }
            }
        }
        return bits;
    }

    private static SKBitmap Decoded(byte[] info, byte[] bits)
    {
        using var image = Dib.Decode(info, bits, DibOptions.Default, new MetafileBudget(MetafileLimits.Default));
        Assert.NotNull(image);
        return SKBitmap.FromImage(image.Image);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void Every_depth_reads_back_pixel_by_pixel_at_every_row_padding(int bpp)
    {
        foreach (var width in new[] { 1, 2, 3, 5, 7, 9, 31, 33 })
        {
            foreach (var topDown in new[] { false, true })
            {
                const int height = 5;
                var colors = bpp <= 8 ? 1 << bpp : 0;
                var info = (byte[])[.. Header(width, topDown ? -height : height, bpp, colors: colors), .. Table(colors)];
                using var bitmap = Decoded(info, Pixels(width, height, bpp, topDown));
                Assert.Equal((width, height), (bitmap.Width, bitmap.Height));
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                        Assert.True(Expected(bpp, x, y) == bitmap.GetPixel(x, y), $"{bpp} bpp, {width} wide, top-down {topDown}: ({x}, {y}) is {bitmap.GetPixel(x, y)}");
                }
            }
        }
    }

    [Fact]
    public void Run_length_runs_and_deltas_past_the_bitmap_land_nowhere()
    {
        const int width = 6, height = 4;
        var info = (byte[])[.. Header(width, height, 8, compression: 1, colors: 4), .. Table(4)];
        byte[] runs =
        [
            255, 1, // a run of 255 on a 6-pixel row: the rest falls off its end
            0, 0, // end of line
            0, 2, 250, 0, // a delta far past the row's end
            0, 255, .. Enumerable.Repeat((byte)3, 256), // 255 literal pixels there (padded to an even count)
            3, 2, // a run there
            0, 2, 0, 250, // a delta far past the last row
            3, 2,
            0, 1, // end of bitmap
        ];
        using var bitmap = Decoded(info, runs);
        // Bottom-up: the first run fills the bottom row; everything else is outside and left transparent.
        for (var x = 0; x < width; x++)
            Assert.Equal(Entry(1), bitmap.GetPixel(x, height - 1));
        for (var y = 0; y < height - 1; y++)
        {
            for (var x = 0; x < width; x++)
                Assert.Equal(0, bitmap.GetPixel(x, y).Alpha);
        }
    }

    [Fact]
    public void A_run_length_bitmap_cut_off_in_a_literal_group_is_refused()
    {
        var info = (byte[])[.. Header(4, 4, 4, compression: 2, colors: 16), .. Table(16)];
        Assert.Throws<DibFormatException>(() => Dib.Decode(info, [0, 9, 0x12, 0x34], DibOptions.Default, new MetafileBudget(MetafileLimits.Default)));
    }
}
