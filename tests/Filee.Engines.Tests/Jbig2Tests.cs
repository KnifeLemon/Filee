// Filee's JBIG2 decoder (Djvu/Jbig2Decoder.cs), which the PostScript writer uses to draw JBIG2 pictures and which reads
// back what Jbig2Encoder writes: masks round-trip exactly (alone and with shared /JBIG2Globals, also with the shared
// shapes no page drew left empty), generic regions of every template (with typical prediction and adaptive pixels,
// and MMR), refinement regions and striped pages decode as the test drew them, features Filee doesn't read are
// refused as NotSupportedException, and hostile streams are refused quickly through Jbig2Limits.
//
// The generic and refinement streams here are written by a small encoder in this file with the same contexts, so they
// test the plumbing (segments, regions, prediction, combination); the context order itself was checked against an
// independent decoder (pdf.js) by hand, see docs/ENGINES.md.

using Filee.Core.Conversion;
using Filee.Engines.Djvu;
using Filee.Engines.Documents;

namespace Filee.Engines.Tests;

public sealed class Jbig2Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A shape of <paramref name="width"/> × <paramref name="height"/> with a pattern of black pixels.</summary>
    private static Jb2Shape Shape(int width, int height, int seed)
    {
        var shape = new Jb2Shape(width, height);
        var random = new Random(seed);
        for (var i = 0; i < shape.Bits.Length; i++)
            shape.Bits[i] = (byte)(random.Next(3) == 0 ? 1 : 0);
        shape.ComputeBox();
        return shape;
    }

    private static byte[] Expected(int width, int height, IReadOnlyList<Jb2Shape> shapes, IReadOnlyList<Jb2Blit> blits) =>
        DjvuRenderer.MaskBits(new DjvuPage { Width = width, Height = height, Dpi = 300, Mask = new Jb2Image(width, height, [.. shapes], [.. blits]) }, null, Token);

    [Fact]
    public void Encoded_masks_decode_to_the_same_bits()
    {
        var shapes = Enumerable.Range(0, 40).Select(i => Shape(3 + i % 17, 2 + i % 13, i)).ToList();
        shapes.Add(new Jb2Shape(5, 5)); // empty: never a symbol
        var random = new Random(3);
        var blits = Enumerable.Range(0, 900).Select(_ => new Jb2Blit(random.Next(shapes.Count), random.Next(-10, 700), random.Next(-10, 400))).ToList();
        var data = Jbig2Encoder.Encode(700, 400, shapes, blits, Token);
        var page = Jbig2Decoder.Decode(data, cancellationToken: Token);
        Assert.Equal((700, 400), (page.Width, page.Height));
        Assert.Equal(Expected(700, 400, shapes, blits), page.PackedBits());
    }

    [Fact]
    public void Masks_built_on_shared_shapes_decode_with_their_globals()
    {
        // Shapes 0–29 are a shared dictionary; page one uses some of them, page two others and shapes of its own.
        var shapes = Enumerable.Range(0, 45).Select(i => Shape(14 + i % 9, 12 + i % 3, 100 + i)).ToList();
        var globals = new Jbig2Globals(shapes, 30);
        Assert.Equal(30, globals.Count);
        var pageOne = Enumerable.Range(0, 200).Select(i => new Jb2Blit(i % 10, i * 7 % 500, i * 13 % 300)).ToList();
        var pageTwo = Enumerable.Range(0, 200).Select(i => new Jb2Blit(i % 2 == 0 ? 20 + i % 5 : 30 + i % 15, i * 11 % 500, i * 3 % 300)).ToList();
        var one = Jbig2Encoder.Encode(500, 300, shapes, pageOne, globals, out var usesOne, Token);
        var two = Jbig2Encoder.Encode(500, 300, shapes, pageTwo, globals, out var usesTwo, Token);
        Assert.True(usesOne && usesTwo);
        globals.Use(pageOne.Select(b => b.Shape));
        globals.Use(pageTwo.Select(b => b.Shape));
        var shared = globals.Encode(Token);
        Assert.Equal(Expected(500, 300, shapes, pageOne), Jbig2Decoder.Decode(one, shared, cancellationToken: Token).PackedBits());
        Assert.Equal(Expected(500, 300, shapes, pageTwo), Jbig2Decoder.Decode(two, shared, cancellationToken: Token).PackedBits());

        // The shared shapes no page drew (15 of 30) cost a few bits each, not their bitmaps.
        var all = new Jbig2Globals(shapes, 30);
        all.Use(Enumerable.Range(0, 30));
        Assert.True(shared.Length * 3 < all.Encode(Token).Length * 2, $"{shared.Length} bytes vs {all.Encode(Token).Length}");

        // A page that draws none of the shared shapes doesn't refer to them.
        var own = Jbig2Encoder.Encode(500, 300, shapes, [new Jb2Blit(40, 5, 5)], globals, out var usesOwn, Token);
        Assert.False(usesOwn);
        Assert.Equal(Expected(500, 300, shapes, [new Jb2Blit(40, 5, 5)]), Jbig2Decoder.Decode(own, cancellationToken: Token).PackedBits());
    }

    // ---- a small test-side writer for the segment kinds Jbig2Encoder doesn't write ----

    private static void Segment(List<byte> output, int number, int type, byte[] data, int[]? referred = null, int page = 1)
    {
        output.AddRange(BigEndian(number));
        output.Add((byte)type);
        referred ??= [];
        output.Add((byte)(referred.Length << 5));
        foreach (var r in referred)
            output.Add((byte)r);
        output.Add((byte)page);
        output.AddRange(BigEndian(data.Length));
        output.AddRange(data);
    }

    private static byte[] BigEndian(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] PageInfo(int width, int height, int flags = 0, bool striped = false, int stripe = 0) =>
        [.. BigEndian(width), .. BigEndian(striped ? -1 : height), .. BigEndian(0), .. BigEndian(0), (byte)flags,
            (byte)(striped ? 0x80 | (stripe >> 8) : 0), (byte)(striped ? stripe : 0)];

    private static byte[] RegionInfo(int width, int height, int x, int y, int op = 0) =>
        [.. BigEndian(width), .. BigEndian(height), .. BigEndian(x), .. BigEndian(y), (byte)op];

    private static readonly int[] TypicalContexts = [0x9B25, 0x0795, 0x00E5, 0x0195];

    /// <summary>Generic region coding of <paramref name="pixels"/> (rows top first) as the decoder reads it.</summary>
    private static byte[] Generic(byte[,] pixels, int template, bool typical, sbyte[] at)
    {
        int h = pixels.GetLength(0), w = pixels.GetLength(1);
        int P(int x, int y) => (uint)x < (uint)w && (uint)y < (uint)h ? pixels[y, x] : 0;
        var coder = new Jbig2Encoder.MqEncoder();
        var contexts = new byte[1 << 16];
        var ltp = 0;
        for (var y = 0; y < h; y++)
        {
            if (typical)
            {
                var same = true;
                for (var x = 0; x < w && same; x++)
                    same = pixels[y, x] == P(x, y - 1);
                var now = same ? 1 : 0;
                coder.Encode(contexts, TypicalContexts[template], now ^ ltp);
                ltp = now;
                if (same)
                    continue;
            }
            for (var x = 0; x < w; x++)
            {
                var c = template switch
                {
                    0 => P(x - 1, y) | (P(x - 2, y) << 1) | (P(x - 3, y) << 2) | (P(x - 4, y) << 3)
                        | (P(x + at[0], y + at[1]) << 4) | (P(x + 2, y - 1) << 5) | (P(x + 1, y - 1) << 6) | (P(x, y - 1) << 7)
                        | (P(x - 1, y - 1) << 8) | (P(x - 2, y - 1) << 9) | (P(x + at[2], y + at[3]) << 10) | (P(x + at[4], y + at[5]) << 11)
                        | (P(x + 1, y - 2) << 12) | (P(x, y - 2) << 13) | (P(x - 1, y - 2) << 14) | (P(x + at[6], y + at[7]) << 15),
                    1 => P(x - 1, y) | (P(x - 2, y) << 1) | (P(x - 3, y) << 2) | (P(x + at[0], y + at[1]) << 3)
                        | (P(x + 2, y - 1) << 4) | (P(x + 1, y - 1) << 5) | (P(x, y - 1) << 6) | (P(x - 1, y - 1) << 7) | (P(x - 2, y - 1) << 8)
                        | (P(x + 2, y - 2) << 9) | (P(x + 1, y - 2) << 10) | (P(x, y - 2) << 11) | (P(x - 1, y - 2) << 12),
                    2 => P(x - 1, y) | (P(x - 2, y) << 1) | (P(x + at[0], y + at[1]) << 2)
                        | (P(x + 1, y - 1) << 3) | (P(x, y - 1) << 4) | (P(x - 1, y - 1) << 5) | (P(x - 2, y - 1) << 6)
                        | (P(x + 1, y - 2) << 7) | (P(x, y - 2) << 8) | (P(x - 1, y - 2) << 9),
                    _ => P(x - 1, y) | (P(x - 2, y) << 1) | (P(x - 3, y) << 2) | (P(x - 4, y) << 3) | (P(x + at[0], y + at[1]) << 4)
                        | (P(x + 1, y - 1) << 5) | (P(x, y - 1) << 6) | (P(x - 1, y - 1) << 7) | (P(x - 2, y - 1) << 8) | (P(x - 3, y - 1) << 9),
                };
                coder.Encode(contexts, c, pixels[y, x]);
            }
        }
        return coder.Finish();
    }

    /// <summary>Refinement coding (template 1, no prediction) of <paramref name="pixels"/> against <paramref name="reference"/>.</summary>
    private static byte[] Refinement(byte[,] pixels, byte[,] reference)
    {
        int h = pixels.GetLength(0), w = pixels.GetLength(1);
        int P(int x, int y) => (uint)x < (uint)w && (uint)y < (uint)h ? pixels[y, x] : 0;
        int R(int x, int y) => (uint)x < (uint)reference.GetLength(1) && (uint)y < (uint)reference.GetLength(0) ? reference[y, x] : 0;
        var coder = new Jbig2Encoder.MqEncoder();
        var contexts = new byte[1 << 13];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var c = (P(x - 1, y - 1) << 9) | (P(x, y - 1) << 8) | (P(x + 1, y - 1) << 7) | (P(x - 1, y) << 6)
                    | (R(x, y - 1) << 5) | (R(x - 1, y) << 4) | (R(x, y) << 3) | (R(x + 1, y) << 2) | (R(x, y + 1) << 1) | R(x + 1, y + 1);
                coder.Encode(contexts, c, pixels[y, x]);
            }
        }
        return coder.Finish();
    }

    private static byte[,] Picture(int width, int height, int seed)
    {
        // Blocks and stripes with repeated rows, so typical prediction has rows to skip.
        var random = new Random(seed);
        var pixels = new byte[height, width];
        for (var y = 0; y < height; y++)
        {
            if (y > 0 && random.Next(3) == 0)
            {
                for (var x = 0; x < width; x++)
                    pixels[y, x] = pixels[y - 1, x];
                continue;
            }
            for (var x = 0; x < width; x++)
                pixels[y, x] = (byte)((x / 5 + y / 3) % 4 == 0 || random.Next(20) == 0 ? 1 : 0);
        }
        return pixels;
    }

    private static byte[] Packed(byte[,] pixels)
    {
        int h = pixels.GetLength(0), w = pixels.GetLength(1);
        var stride = (w + 7) / 8;
        var bits = new byte[stride * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (pixels[y, x] != 0)
                    bits[y * stride + x / 8] |= (byte)(0x80 >> (x % 8));
            }
        }
        return bits;
    }

    public static TheoryData<int, bool> Templates() => new() { { 0, false }, { 0, true }, { 1, false }, { 1, true }, { 2, true }, { 3, false }, { 3, true } };

    [Theory]
    [MemberData(nameof(Templates))]
    public void Generic_regions_decode_with_every_template(int template, bool typical)
    {
        var pixels = Picture(77, 41, template * 10 + (typical ? 1 : 0));
        sbyte[] at = template == 0 ? [3, -1, -3, -1, 2, -2, -2, -2] : template == 1 ? [3, -1] : [2, -1];
        if (template == 0)
            at[0] = 4; // an adaptive pixel off its nominal place
        if (template == 1 && !typical)
            at = [-12, -5]; // one far from the pixel (read through the bitmap, not the rows kept)
        var data = new List<byte>();
        Segment(data, 0, 48, PageInfo(90, 50));
        byte flags = (byte)((template << 1) | (typical ? 8 : 0));
        Segment(data, 1, 38, [.. RegionInfo(77, 41, 6, 4), flags, .. at.Select(v => unchecked((byte)v)), .. Generic(pixels, template, typical, at)]);
        var page = Jbig2Decoder.Decode([.. data], cancellationToken: Token);
        for (var y = 0; y < 50; y++)
        {
            for (var x = 0; x < 90; x++)
            {
                var inside = x >= 6 && x < 83 && y >= 4 && y < 45;
                Assert.Equal(inside ? pixels[y - 4, x - 6] : 0, page.Pixels[y * 90 + x]);
            }
        }
    }

    [Fact]
    public void Mmr_generic_regions_refinement_and_striped_pages_decode()
    {
        var first = Picture(64, 20, 7);
        var second = Picture(64, 20, 8);
        // MMR is G4 without its end-of-block, as CcittG4 writes it.
        var mmr = CcittG4.Encode(Packed(first), 64, 20, Token);
        var data = new List<byte>();
        Segment(data, 0, 48, PageInfo(64, 0, striped: true, stripe: 20));
        Segment(data, 1, 38, [.. RegionInfo(64, 20, 0, 0), 1, .. mmr]);
        Segment(data, 2, 50, BigEndian(19));
        // The second stripe: a refinement of the page under it (white) to the second picture, then XOR of a block.
        sbyte[] nominal = [3, -1, -3, -1, 2, -2, -2, -2];
        Segment(data, 3, 38, [.. RegionInfo(64, 20, 0, 20), 0, .. nominal.Select(v => unchecked((byte)v)), .. Generic(second, 0, false, nominal)]);
        var block = new byte[4, 8];
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 8; x++)
                block[y, x] = 1;
        }
        var under = new byte[4, 8];
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 8; x++)
                under[y, x] = second[10 + y, 30 + x];
        }
        var refined = new byte[4, 8];
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 8; x++)
                refined[y, x] = (byte)(1 - under[y, x]);
        }
        Segment(data, 4, 42, [.. RegionInfo(8, 4, 30, 30), 1, .. Refinement(refined, under)]);
        Segment(data, 5, 50, BigEndian(39));
        var page = Jbig2Decoder.Decode([.. data], cancellationToken: Token);
        Assert.Equal((64, 40), (page.Width, page.Height));
        for (var y = 0; y < 40; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                int expected = y < 20 ? first[y, x] : second[y - 20, x];
                if (y is >= 30 and < 34 && x is >= 30 and < 38)
                    expected = 1 - expected;
                Assert.Equal(expected, page.Pixels[y * 64 + x]);
            }
        }
    }

    [Fact]
    public void Features_filee_does_not_decode_are_refused_as_not_supported()
    {
        byte[] Stream(int type, byte[] body)
        {
            var data = new List<byte>();
            Segment(data, 0, 48, PageInfo(10, 10));
            Segment(data, 1, type, body);
            return [.. data];
        }
        // A Huffman-coded symbol dictionary, a halftone region, an intermediate generic region.
        Assert.Throws<NotSupportedException>(() => Jbig2Decoder.Decode(Stream(0, [0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]), cancellationToken: Token));
        Assert.Throws<NotSupportedException>(() => Jbig2Decoder.Decode(Stream(22, new byte[30]), cancellationToken: Token));
        Assert.Throws<NotSupportedException>(() => Jbig2Decoder.Decode(Stream(36, new byte[30]), cancellationToken: Token));
        // Damage is damage.
        Assert.Throws<InvalidDataException>(() => Jbig2Decoder.Decode([0, 0, 0, 0, 38, 0, 1, 0, 0, 1, 0, 1, 2], cancellationToken: Token));
        Assert.Throws<InvalidDataException>(() => Jbig2Decoder.Decode([], cancellationToken: Token));
    }

    [Fact]
    public void Hostile_streams_are_refused_quickly()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // A page of 4 billion × 4 billion pixels.
        var huge = new List<byte>();
        Segment(huge, 0, 48, PageInfo(-2, 0x7FFFFFFF));
        Assert.Equal(DocumentLimits.TooLargeKey, Assert.Throws<ConversionRefusedException>(() => Jbig2Decoder.Decode([.. huge], cancellationToken: Token)).ErrorKey);

        // A generic region of 100,000² pixels on a small page.
        var region = new List<byte>();
        Segment(region, 0, 48, PageInfo(10, 10));
        Segment(region, 1, 38, [.. RegionInfo(100_000, 100_000, 0, 0), 0, 3, 255, 253, 255, 2, 254, 254, 254]);
        Assert.Equal(DocumentLimits.TooLargeKey, Assert.Throws<ConversionRefusedException>(() => Jbig2Decoder.Decode([.. region], cancellationToken: Token)).ErrorKey);

        // A dictionary of 2 billion symbols, and a text region placing 4 billion symbols, from a few bytes.
        var symbols = new List<byte>();
        Segment(symbols, 0, 48, PageInfo(10, 10));
        Segment(symbols, 1, 0, [0, 0, 3, 255, 253, 255, 2, 254, 254, 254, .. BigEndian(0x7FFFFFFF), .. BigEndian(0x7FFFFFFF)]);
        Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(() => Jbig2Decoder.Decode([.. symbols], cancellationToken: Token)).ErrorKey);
        var text = new List<byte>();
        Segment(text, 0, 48, PageInfo(10, 10));
        Segment(text, 1, 6, [.. RegionInfo(10, 10, 0, 0), 0, 0, .. BigEndian(-1)]);
        Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(() => Jbig2Decoder.Decode([.. text], cancellationToken: Token)).ErrorKey);

        // Many large symbols placed again and again: the drawing is bounded too.
        var big = new Jb2Shape(2000, 2000);
        big.Bits[0] = 1;
        var blits = Enumerable.Range(0, 400).Select(i => new Jb2Blit(0, i, i)).ToList();
        var placed = Jbig2Encoder.Encode(3000, 3000, [big], blits, Token);
        var limits = Jbig2Limits.Default with { MaxTotalPixels = 50_000_000 };
        Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(() => Jbig2Decoder.Decode(placed, null, limits, Token)).ErrorKey);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"{watch.Elapsed}");
    }
}
