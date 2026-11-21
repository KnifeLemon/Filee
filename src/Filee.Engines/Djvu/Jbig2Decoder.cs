// JBIG2 (ITU-T T.88) decoding of an embedded stream as PDF's /JBIG2Decode filter holds it (segments without a file
// header, the shared segments of /JBIG2Globals first), so the PostScript writer can draw JBIG2 pictures as image masks
// and tests can read back what Jbig2Encoder writes.
//
// Read: page information (also striped pages of unknown height, ended by end-of-stripe segments), symbol dictionaries
// and immediate text regions with arithmetic coding (refinement and aggregation included), immediate generic regions
// (arithmetic templates 0–3 with adaptive pixels and typical prediction, or MMR through CcittG4) and immediate generic
// refinement regions. Left out, refused with NotSupportedException so the caller can fall back: Huffman-coded
// dictionaries and text regions, pattern and halftone regions, intermediate regions, extended templates and
// dictionaries that reuse another's coding contexts.
//
// Bitmaps are a byte per pixel (1 = black), rows top first. Every bitmap is checked against Jbig2Limits before it is
// made, and the symbols and drawing a stream asks for are counted, so a hostile stream is refused with the usual
// "too large" / "too complex" errors instead of exhausting memory or time.

using Filee.Engines.Documents;

namespace Filee.Engines.Djvu;

/// <summary>What one JBIG2 stream may make Filee build.</summary>
internal sealed record Jbig2Limits
{
    public static Jbig2Limits Default { get; } = new();

    /// <summary>Pixels of the page or of one region or symbol.</summary>
    public long MaxBitmapPixels { get; init; } = 150_000_000;

    /// <summary>Pixels of all bitmaps a stream holds at once (page, regions, symbols).</summary>
    public long MaxTotalPixels { get; init; } = 400_000_000;

    /// <summary>Symbols of all dictionaries together.</summary>
    public int MaxSymbols { get; init; } = 2_000_000;

    /// <summary>Symbol instances of one text region.</summary>
    public long MaxInstances { get; init; } = 4_000_000;

    /// <summary>Segments of a stream.</summary>
    public int MaxSegments { get; init; } = 1_000_000;
}

/// <summary>A decoded JBIG2 page: its size and its pixels, a byte each (1 = black), rows top first.</summary>
internal sealed record Jbig2Page(int Width, int Height, byte[] Pixels)
{
    /// <summary>The page as 1-bit rows, each padded to a byte, a set bit for black.</summary>
    public byte[] PackedBits()
    {
        var stride = (Width + 7) / 8;
        var bits = new byte[(long)stride * Height];
        for (var y = 0; y < Height; y++)
        {
            var row = Pixels.AsSpan((int)((long)y * Width), Width);
            var target = (long)y * stride;
            var x = 0;
            for (; x + 8 <= Width; x += 8)
            {
                bits[target + (x >> 3)] = (byte)((row[x] << 7) | (row[x + 1] << 6) | (row[x + 2] << 5) | (row[x + 3] << 4)
                    | (row[x + 4] << 3) | (row[x + 5] << 2) | (row[x + 6] << 1) | row[x + 7]);
            }
            for (; x < Width; x++)
            {
                if (row[x] != 0)
                    bits[target + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            }
        }
        return bits;
    }
}

internal sealed class Jbig2Decoder
{
    private sealed class Bitmap(int width, int height)
    {
        public int Width { get; } = width;
        public int Height { get; private set; } = height;
        public byte[] Pixels { get; private set; } = new byte[(long)width * height];

        public int this[int x, int y] => (uint)x < (uint)Width && (uint)y < (uint)Height ? Pixels[(long)y * Width + x] : 0;

        /// <summary>Grows a striped page to <paramref name="height"/> rows filled with <paramref name="fill"/>.</summary>
        public void Grow(int height, byte fill)
        {
            if (height <= Height)
                return;
            var pixels = new byte[(long)Width * height];
            Pixels.CopyTo(pixels, 0);
            if (fill != 0)
                pixels.AsSpan(Pixels.Length).Fill(fill);
            Pixels = pixels;
            Height = height;
        }

        /// <summary>Draws <paramref name="source"/> with its top-left pixel at (<paramref name="left"/>, <paramref name="top"/>).</summary>
        public void Combine(Bitmap source, long left, long top, int op)
        {
            // The columns of the source that land on this bitmap.
            var from = Math.Max(0, -left);
            var to = Math.Min(source.Width, Width - left);
            if (from >= to)
                return;
            var count = (int)(to - from);
            for (var y = 0; y < source.Height; y++)
            {
                var ty = top + y;
                if (ty < 0 || ty >= Height)
                    continue;
                var s = source.Pixels.AsSpan((int)((long)y * source.Width + from), count);
                var d = Pixels.AsSpan((int)(ty * Width + left + from), count);
                switch (op)
                {
                    case 0:
                        Or(s, d);
                        break;
                    case 4:
                        s.CopyTo(d);
                        break;
                    default:
                        for (var x = 0; x < count; x++)
                        {
                            d[x] = op switch
                            {
                                1 => (byte)(d[x] & s[x]),
                                2 => (byte)(d[x] ^ s[x]),
                                _ => (byte)(1 - (d[x] ^ s[x])),
                            };
                        }
                        break;
                }
            }
        }

        private static void Or(ReadOnlySpan<byte> source, Span<byte> target)
        {
            var x = 0;
            if (System.Numerics.Vector.IsHardwareAccelerated)
            {
                var size = System.Numerics.Vector<byte>.Count;
                for (; x + size <= source.Length; x += size)
                    (new System.Numerics.Vector<byte>(source[x..]) | new System.Numerics.Vector<byte>(target[x..])).CopyTo(target[x..]);
            }
            for (; x < source.Length; x++)
                target[x] |= source[x];
        }

        /// <summary>Takes <paramref name="other"/>'s pixels (same size) as its own.</summary>
        public void Adopt(Bitmap other) => Pixels = other.Pixels;
    }

    private sealed record Segment(int Number, int Type, int[] Referred, int Start, int Length, bool Global);

    /// <summary>The coding state of one segment: the MQ decoder and every context set it uses.</summary>
    private sealed class Coding(byte[] data, int start, int end)
    {
        public MqDecoder Mq { get; } = new(data, start, end);
        public byte[] Generic { get; } = new byte[1 << 16];
        public byte[] Refinement { get; } = new byte[1 << 13];
        public IntegerDecoder Dh { get; } = new();
        public IntegerDecoder Dw { get; } = new();
        public IntegerDecoder Ex { get; } = new();
        public IntegerDecoder Ai { get; } = new();
        public IntegerDecoder Dt { get; } = new();
        public IntegerDecoder Fs { get; } = new();
        public IntegerDecoder Ds { get; } = new();
        public IntegerDecoder It { get; } = new();
        public IntegerDecoder Ri { get; } = new();
        public IntegerDecoder Rdw { get; } = new();
        public IntegerDecoder Rdh { get; } = new();
        public IntegerDecoder Rdx { get; } = new();
        public IntegerDecoder Rdy { get; } = new();
        public IdDecoder? Id { get; set; }
    }

    private readonly Jbig2Limits _limits;
    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<int, List<Bitmap>> _dictionaries = [];
    private readonly Dictionary<int, List<Bitmap>> _globalDictionaries = [];
    private long _pixels;
    private int _symbols;
    private Bitmap? _page;
    private bool _pageStriped;
    private bool _pageDrawn;
    private byte _pageDefault;

    private Jbig2Decoder(Jbig2Limits limits, CancellationToken cancellationToken)
    {
        _limits = limits;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Decodes an embedded JBIG2 stream <paramref name="data"/> after the shared segments <paramref name="globals"/>.
    /// Damage throws InvalidDataException, a feature Filee doesn't read NotSupportedException, a stream past
    /// <paramref name="limits"/> ConversionRefusedException.
    /// </summary>
    public static Jbig2Page Decode(byte[] data, byte[]? globals = null, Jbig2Limits? limits = null, CancellationToken cancellationToken = default)
    {
        var decoder = new Jbig2Decoder(limits ?? Jbig2Limits.Default, cancellationToken);
        if (globals is { Length: > 0 })
            decoder.Run(globals, global: true);
        decoder.Run(data, global: false);
        var page = decoder._page ?? throw new InvalidDataException("The JBIG2 stream has no page.");
        return new Jbig2Page(page.Width, page.Height, page.Pixels);
    }

    private void Run(byte[] data, bool global)
    {
        foreach (var segment in Segments(data, global))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            switch (segment.Type)
            {
                case 0:
                    (global ? _globalDictionaries : _dictionaries)[segment.Number] = SymbolDictionary(data, segment);
                    break;
                case 6 or 7:
                    Place(TextRegion(data, segment, out var info), info);
                    break;
                case 38 or 39:
                    Place(GenericRegion(data, segment, out var genericInfo), genericInfo);
                    break;
                case 42 or 43:
                    RefinementRegion(data, segment);
                    break;
                case 48:
                    PageInformation(data, segment);
                    break;
                case 50:
                    EndOfStripe(data, segment);
                    break;
                case 49 or 51 or 52 or 53 or 62:
                    // End of page / file, profiles, tables (for Huffman coding, refused where used), extensions.
                    break;
                case 4 or 16 or 20 or 22 or 23 or 36 or 40:
                    throw new NotSupportedException($"JBIG2 segments of type {segment.Type} (intermediate, pattern or halftone regions) aren't read.");
                default:
                    // Reserved types carry nothing a page needs.
                    break;
            }
        }
    }

    private List<Segment> Segments(byte[] data, bool global)
    {
        var segments = new List<Segment>();
        var at = 0;
        while (at < data.Length)
        {
            if (data.Length - at < 11)
            {
                // Padding after the last segment.
                break;
            }
            var number = (int)Math.Min(U32(data, at), int.MaxValue);
            var flags = data[at + 4];
            at += 5;
            var type = flags & 0x3F;
            int count = data[at] >> 5;
            if (count == 7)
            {
                Need(data, at, 4);
                var longCount = U32(data, at) & 0x1FFFFFFF;
                if (longCount > 100_000)
                    throw new InvalidDataException("A JBIG2 segment refers to too many segments.");
                count = (int)longCount;
                at += 4 + (count + 8) / 8;
            }
            else
            {
                if (count > 4)
                    throw new InvalidDataException("A JBIG2 segment header is damaged.");
                at++;
            }
            var size = number <= 256 ? 1 : number <= 65536 ? 2 : 4;
            var referred = new int[count];
            Need(data, at, (long)count * size);
            for (var i = 0; i < count; i++, at += size)
                referred[i] = size == 1 ? data[at] : size == 2 ? (data[at] << 8) | data[at + 1] : (int)Math.Min(U32(data, at), int.MaxValue);
            at += (flags & 0x40) != 0 ? 4 : 1;
            Need(data, at, 4);
            var length = U32(data, at);
            at += 4;
            if (length == 0xFFFFFFFF)
                throw new NotSupportedException("JBIG2 segments of unknown length aren't read.");
            if (length > data.Length - at)
                throw new InvalidDataException("A JBIG2 segment is cut short.");
            segments.Add(new Segment(number, type, referred, at, (int)length, global));
            if (segments.Count > _limits.MaxSegments)
                throw DocumentLimits.Exceeded($"A JBIG2 picture has more than {DocumentLimits.Count(_limits.MaxSegments)} segments.");
            at += (int)length;
        }
        return segments;
    }

    private static long U32(byte[] data, int at)
    {
        Need(data, at, 4);
        return ((long)data[at] << 24) | ((long)data[at + 1] << 16) | ((long)data[at + 2] << 8) | data[at + 3];
    }

    private static int U16(byte[] data, int at)
    {
        Need(data, at, 2);
        return (data[at] << 8) | data[at + 1];
    }

    private static int U8(byte[] data, int at)
    {
        Need(data, at, 1);
        return data[at];
    }

    private static void Need(byte[] data, int at, long count)
    {
        if (at < 0 || count < 0 || at + count > data.Length)
            throw new InvalidDataException("A JBIG2 segment ends early.");
    }

    private Bitmap NewBitmap(long width, long height)
    {
        if (width < 0 || height < 0)
            throw new InvalidDataException("A JBIG2 bitmap has an impossible size.");
        // Each side is at most the limit, so their product can't overflow.
        var pixels = width > _limits.MaxBitmapPixels || height > _limits.MaxBitmapPixels ? long.MaxValue : width * height;
        if (pixels > _limits.MaxBitmapPixels)
            throw DocumentLimits.TooLarge($"A JBIG2 picture has more than {DocumentLimits.Count(_limits.MaxBitmapPixels)} pixels.");
        _pixels += pixels;
        if (_pixels > _limits.MaxTotalPixels)
            throw DocumentLimits.TooLarge($"A JBIG2 picture holds more than {DocumentLimits.Count(_limits.MaxTotalPixels)} pixels.");
        return new Bitmap((int)width, (int)height);
    }

    private void PageInformation(byte[] data, Segment segment)
    {
        if (_page is not null)
            throw new InvalidDataException("A JBIG2 stream has more than one page.");
        var at = segment.Start;
        Need(data, at, 19);
        var width = U32(data, at);
        var height = U32(data, at + 4);
        var flags = data[at + 16];
        _pageDefault = (byte)((flags >> 2) & 1);
        _pageStriped = height == 0xFFFFFFFF;
        _page = NewBitmap(width, _pageStriped ? 0 : height);
        if (_pageDefault != 0)
            _page.Pixels.AsSpan().Fill(1);
    }

    private void EndOfStripe(byte[] data, Segment segment)
    {
        if (_page is null || !_pageStriped)
            return;
        var end = U32(data, segment.Start);
        Grow(end + 1);
    }

    private void Grow(long height)
    {
        if (_page is null || height <= _page.Height)
            return;
        var added = (height - _page.Height) * (long)_page.Width;
        if (height * _page.Width > _limits.MaxBitmapPixels || _pixels + added > _limits.MaxTotalPixels)
            throw DocumentLimits.TooLarge($"A JBIG2 picture has more than {DocumentLimits.Count(_limits.MaxBitmapPixels)} pixels.");
        _pixels += added;
        _page.Grow((int)height, _pageDefault);
    }

    private sealed record RegionInfo(long Width, long Height, long X, long Y, int Op);

    private static RegionInfo ReadRegionInfo(byte[] data, ref int at)
    {
        Need(data, at, 17);
        var info = new RegionInfo(U32(data, at), U32(data, at + 4), U32(data, at + 8), U32(data, at + 12), data[at + 16] & 7);
        at += 17;
        return info;
    }

    private void Place(Bitmap region, RegionInfo info)
    {
        if (_page is null)
            throw new InvalidDataException("A JBIG2 region comes before the page information.");
        if (_pageStriped)
            Grow(info.Y + region.Height);
        // The first region covering a white page is the page (no copy of a page-sized bitmap).
        if (!_pageDrawn && _pageDefault == 0 && info.X == 0 && info.Y == 0 && region.Width == _page.Width && region.Height == _page.Height && info.Op is 0 or 2 or 4)
            _page.Adopt(region);
        else
            _page.Combine(region, info.X, info.Y, info.Op);
        _pageDrawn = true;
    }

    /// <summary>Adaptive template pixels (dx, dy pairs) after a region or dictionary's flags.</summary>
    private static sbyte[] ReadAt(byte[] data, ref int at, int pairs)
    {
        Need(data, at, 2L * pairs);
        var result = new sbyte[2 * pairs];
        for (var i = 0; i < result.Length; i++)
            result[i] = unchecked((sbyte)data[at + i]);
        at += 2 * pairs;
        return result;
    }

    private Bitmap GenericRegion(byte[] data, Segment segment, out RegionInfo info)
    {
        var at = segment.Start;
        var end = segment.Start + segment.Length;
        info = ReadRegionInfo(data, ref at);
        var flags = U8(data, at++);
        var mmr = (flags & 1) != 0;
        var template = (flags >> 1) & 3;
        var typical = (flags & 8) != 0;
        if ((flags & 0x10) != 0)
            throw new NotSupportedException("JBIG2 extended templates aren't read.");
        var region = NewBitmap(info.Width, info.Height);
        if (mmr)
        {
            var stride = (region.Width + 7) / 8;
            var bits = new byte[(long)stride * region.Height];
            CcittG4.Decode(data.AsSpan(at, Math.Max(0, end - at)), region.Width, region.Height, bits, 0, _cancellationToken);
            for (var y = 0; y < region.Height; y++)
            {
                for (var x = 0; x < region.Width; x++)
                    region.Pixels[(long)y * region.Width + x] = (byte)((bits[(long)y * stride + (x >> 3)] >> (7 - (x & 7))) & 1);
            }
            return region;
        }
        var adaptive = ReadAt(data, ref at, template == 0 ? 4 : 1);
        var coding = new Coding(data, at, end);
        DecodeGeneric(coding, region, template, typical, adaptive);
        return region;
    }

    /// <summary>The fixed context value of typical prediction's SLTP bit per template.</summary>
    private static readonly int[] TypicalContexts = [0x9B25, 0x0795, 0x00E5, 0x0195];

    /// <summary>Columns of margin the generic decoder keeps around its rows.</summary>
    private const int Margin = 8;

    /// <summary>Generic region decoding (T.88 6.2.5) into <paramref name="bitmap"/>.</summary>
    private void DecodeGeneric(Coding coding, Bitmap bitmap, int template, bool typical, sbyte[] at)
    {
        int w = bitmap.Width, h = bitmap.Height;
        var pixels = bitmap.Pixels;
        var mq = coding.Mq;
        var contexts = coding.Generic;
        var ltp = 0;
        // The rows being decoded and the two above, with margins: adaptive pixels within them are read from there,
        // others (allowed, but no encoder writes them) through the bitmap.
        var near = true;
        for (var k = 0; k < at.Length; k += 2)
            near &= at[k + 1] is >= -2 and <= 0 && Math.Abs((int)at[k]) <= Margin && (at[k + 1] < 0 || at[k] < 0);
        var stride = w + 2 * Margin;
        byte[] row = new byte[stride], up = new byte[stride], up2 = new byte[stride];
        int Px(int x, int y) => (uint)x < (uint)w && (uint)y < (uint)h ? pixels[(long)y * w + x] : 0;
        byte[] RowAt(int dy) => dy == 0 ? row : dy == -1 ? up : up2;
        for (var y = 0; y < h; y++)
        {
            if ((y & 0x3F) == 0)
                _cancellationToken.ThrowIfCancellationRequested();
            (up2, up, row) = (up, row, up2);
            Array.Clear(row);
            if (typical)
            {
                ltp ^= mq.Decode(contexts, TypicalContexts[template]);
                if (ltp != 0)
                {
                    // The row is the row above it.
                    Array.Copy(up, row, stride);
                    Array.Copy(row, Margin, pixels, (long)y * w, w);
                    continue;
                }
            }
            byte[] a0 = near ? RowAt(at[1]) : row, a1 = near && at.Length > 2 ? RowAt(at[3]) : row;
            byte[] a2 = near && at.Length > 2 ? RowAt(at[5]) : row, a3 = near && at.Length > 2 ? RowAt(at[7]) : row;
            for (var x = 0; x < w; x++)
            {
                var i = x + Margin;
                int A(int k, byte[] r) => near ? r[i + at[2 * k]] : Px(x + at[2 * k], y + at[2 * k + 1]);
                var c = template switch
                {
                    0 => row[i - 1] | (row[i - 2] << 1) | (row[i - 3] << 2) | (row[i - 4] << 3) | (A(0, a0) << 4)
                        | (up[i + 2] << 5) | (up[i + 1] << 6) | (up[i] << 7) | (up[i - 1] << 8) | (up[i - 2] << 9)
                        | (A(1, a1) << 10) | (A(2, a2) << 11) | (up2[i + 1] << 12) | (up2[i] << 13) | (up2[i - 1] << 14) | (A(3, a3) << 15),
                    1 => row[i - 1] | (row[i - 2] << 1) | (row[i - 3] << 2) | (A(0, a0) << 3)
                        | (up[i + 2] << 4) | (up[i + 1] << 5) | (up[i] << 6) | (up[i - 1] << 7) | (up[i - 2] << 8)
                        | (up2[i + 2] << 9) | (up2[i + 1] << 10) | (up2[i] << 11) | (up2[i - 1] << 12),
                    2 => row[i - 1] | (row[i - 2] << 1) | (A(0, a0) << 2)
                        | (up[i + 1] << 3) | (up[i] << 4) | (up[i - 1] << 5) | (up[i - 2] << 6)
                        | (up2[i + 1] << 7) | (up2[i] << 8) | (up2[i - 1] << 9),
                    _ => row[i - 1] | (row[i - 2] << 1) | (row[i - 3] << 2) | (row[i - 4] << 3) | (A(0, a0) << 4)
                        | (up[i + 1] << 5) | (up[i] << 6) | (up[i - 1] << 7) | (up[i - 2] << 8) | (up[i - 3] << 9),
                };
                var bit = (byte)mq.Decode(contexts, c);
                row[i] = bit;
                if (!near)
                    pixels[(long)y * w + x] = bit;
            }
            Array.Copy(row, Margin, pixels, (long)y * w, w);
        }
    }

    /// <summary>
    /// Generic refinement decoding (T.88 6.3.5): <paramref name="bitmap"/> from <paramref name="reference"/>, whose
    /// pixel (x − dx, y − dy) sits under the bitmap's (x, y).
    /// </summary>
    private void DecodeRefinement(Coding coding, Bitmap bitmap, Bitmap reference, int dx, int dy, int template, bool typical, sbyte[] at)
    {
        int w = bitmap.Width, h = bitmap.Height;
        var pixels = bitmap.Pixels;
        var mq = coding.Mq;
        var contexts = coding.Refinement;
        var ltp = 0;
        int Px(int x, int y) => (uint)x < (uint)w && (uint)y < (uint)h ? pixels[(long)y * w + x] : 0;
        for (var y = 0; y < h; y++)
        {
            if ((y & 0x3F) == 0)
                _cancellationToken.ThrowIfCancellationRequested();
            if (typical)
                ltp ^= mq.Decode(contexts, template == 0 ? 0x0020 : 0x0008);
            var row = (long)y * w;
            for (var x = 0; x < w; x++)
            {
                int rx = x - dx, ry = y - dy;
                if (ltp != 0)
                {
                    // Typical prediction: where the reference's 3×3 neighbourhood is one colour, so is the pixel.
                    var centre = reference[rx, ry];
                    var same = true;
                    for (var j = -1; j <= 1 && same; j++)
                    {
                        for (var i = -1; i <= 1 && same; i++)
                            same = reference[rx + i, ry + j] == centre;
                    }
                    if (same)
                    {
                        pixels[row + x] = (byte)centre;
                        continue;
                    }
                }
                var c = template == 0
                    ? (Px(x, y - 1) << 12) | (Px(x + 1, y - 1) << 11) | (Px(x - 1, y) << 10) | (Px(x + at[0], y + at[1]) << 9)
                        | (reference[rx, ry - 1] << 8) | (reference[rx + 1, ry - 1] << 7) | (reference[rx - 1, ry] << 6) | (reference[rx, ry] << 5)
                        | (reference[rx + 1, ry] << 4) | (reference[rx - 1, ry + 1] << 3) | (reference[rx, ry + 1] << 2) | (reference[rx + 1, ry + 1] << 1)
                        | reference[rx + at[2], ry + at[3]]
                    : (Px(x - 1, y - 1) << 9) | (Px(x, y - 1) << 8) | (Px(x + 1, y - 1) << 7) | (Px(x - 1, y) << 6)
                        | (reference[rx, ry - 1] << 5) | (reference[rx - 1, ry] << 4) | (reference[rx, ry] << 3) | (reference[rx + 1, ry] << 2)
                        | (reference[rx, ry + 1] << 1) | reference[rx + 1, ry + 1];
                pixels[row + x] = (byte)mq.Decode(contexts, c);
            }
        }
    }

    private void RefinementRegion(byte[] data, Segment segment)
    {
        var at = segment.Start;
        var end = segment.Start + segment.Length;
        var info = ReadRegionInfo(data, ref at);
        var flags = U8(data, at++);
        var template = flags & 1;
        var typical = (flags & 2) != 0;
        var adaptive = template == 0 ? ReadAt(data, ref at, 2) : [];
        if (segment.Referred.Length > 0)
            throw new NotSupportedException("JBIG2 refinements of intermediate regions aren't read.");
        if (_page is null)
            throw new InvalidDataException("A JBIG2 region comes before the page information.");
        if (_pageStriped)
            Grow(info.Y + info.Height);
        // The reference is what the page holds under the region.
        var reference = NewBitmap(info.Width, info.Height);
        for (var y = 0; y < reference.Height; y++)
        {
            for (var x = 0; x < reference.Width; x++)
                reference.Pixels[(long)y * reference.Width + x] = (byte)_page[(int)Math.Min(info.X + x, int.MaxValue), (int)Math.Min(info.Y + y, int.MaxValue)];
        }
        var region = NewBitmap(info.Width, info.Height);
        DecodeRefinement(new Coding(data, at, end), region, reference, 0, 0, template, typical, adaptive);
        _page.Combine(region, info.X, info.Y, 4);
        _pageDrawn = true;
    }

    /// <summary>The symbols the dictionaries <paramref name="segment"/> refers to export, in order.</summary>
    private List<Bitmap> InputSymbols(Segment segment)
    {
        var symbols = new List<Bitmap>();
        foreach (var number in segment.Referred)
        {
            // A page segment's references look among the page's segments first, then the shared ones.
            if (!segment.Global && _dictionaries.TryGetValue(number, out var own))
                symbols.AddRange(own);
            else if (_globalDictionaries.TryGetValue(number, out var shared))
                symbols.AddRange(shared);
        }
        return symbols;
    }

    private static int CodeLength(long symbols) => symbols <= 1 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)(symbols - 1));

    private List<Bitmap> SymbolDictionary(byte[] data, Segment segment)
    {
        var at = segment.Start;
        var end = segment.Start + segment.Length;
        var flags = U16(data, at);
        at += 2;
        var huffman = (flags & 1) != 0;
        var aggregate = (flags & 2) != 0;
        var template = (flags >> 10) & 3;
        var refinementTemplate = (flags >> 12) & 1;
        if (huffman)
            throw new NotSupportedException("Huffman-coded JBIG2 symbol dictionaries aren't read.");
        if ((flags & 0x100) != 0)
            throw new NotSupportedException("JBIG2 symbol dictionaries that reuse coding contexts aren't read.");
        var adaptive = ReadAt(data, ref at, template == 0 ? 4 : 1);
        var refinementAt = aggregate && refinementTemplate == 0 ? ReadAt(data, ref at, 2) : [];
        var exported = U32(data, at);
        var newCount = U32(data, at + 4);
        at += 8;
        var input = InputSymbols(segment);
        if (_symbols + newCount > _limits.MaxSymbols || exported > input.Count + newCount)
        {
            if (exported > input.Count + newCount)
                throw new InvalidDataException("A JBIG2 dictionary exports more symbols than it has.");
            throw DocumentLimits.Exceeded($"A JBIG2 picture has more than {DocumentLimits.Count(_limits.MaxSymbols)} symbols.");
        }
        _symbols += (int)newCount;
        var coding = new Coding(data, at, end);
        var mq = coding.Mq;
        var codeLength = CodeLength(input.Count + newCount);
        var created = new List<Bitmap>((int)Math.Min(newCount, 65_536));
        long height = 0;
        while (created.Count < newCount)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var deltaHeight = coding.Dh.Decode(mq) ?? throw new InvalidDataException("A JBIG2 dictionary's height is missing.");
            height += deltaHeight;
            if (height < 0)
                throw new InvalidDataException("A JBIG2 symbol has a negative height.");
            long width = 0;
            while (true)
            {
                if (coding.Dw.Decode(mq) is not { } deltaWidth)
                    break;
                if (created.Count >= newCount)
                    throw new InvalidDataException("A JBIG2 dictionary has more symbols than it says.");
                width += deltaWidth;
                if (width < 0)
                    throw new InvalidDataException("A JBIG2 symbol has a negative width.");
                var symbol = NewBitmap(width, height);
                if (!aggregate)
                {
                    DecodeGeneric(coding, symbol, template, false, adaptive);
                }
                else
                {
                    var instances = coding.Ai.Decode(mq) ?? throw new InvalidDataException("A JBIG2 aggregate symbol is damaged.");
                    var all = new List<Bitmap>(input.Count + created.Count);
                    all.AddRange(input);
                    all.AddRange(created);
                    if (instances == 1)
                    {
                        coding.Id ??= new IdDecoder(codeLength);
                        var id = coding.Id.Decode(mq);
                        var dx = coding.Rdx.Decode(mq) ?? 0;
                        var dy = coding.Rdy.Decode(mq) ?? 0;
                        if (id >= all.Count)
                            throw new InvalidDataException("A JBIG2 symbol refines a symbol that doesn't exist.");
                        DecodeRefinement(coding, symbol, all[id], Clamp(dx), Clamp(dy), refinementTemplate, false, refinementAt);
                    }
                    else
                    {
                        coding.Id ??= new IdDecoder(codeLength);
                        DrawText(coding, symbol, instances, all, 1, 0, 1, false, 0, 0, true, refinementTemplate, refinementAt);
                    }
                }
                created.Add(symbol);
            }
        }
        // Export flags: runs of not exported and exported, starting with not exported.
        var flagsOut = new List<bool>(input.Count + created.Count);
        var exportedNow = false;
        while (flagsOut.Count < input.Count + created.Count)
        {
            var run = coding.Ex.Decode(mq) ?? throw new InvalidDataException("A JBIG2 dictionary's export flags are damaged.");
            if (run < 0 || run > input.Count + created.Count - flagsOut.Count)
                throw new InvalidDataException("A JBIG2 dictionary's export flags are damaged.");
            for (var i = 0; i < run; i++)
                flagsOut.Add(exportedNow);
            exportedNow = !exportedNow;
        }
        var result = new List<Bitmap>();
        for (var i = 0; i < flagsOut.Count; i++)
        {
            if (flagsOut[i])
                result.Add(i < input.Count ? input[i] : created[i - input.Count]);
        }
        if (result.Count != exported)
            throw new InvalidDataException("A JBIG2 dictionary exports another number of symbols than it says.");
        return result;
    }

    private static int Clamp(long value) => (int)Math.Clamp(value, -1_000_000, 1_000_000);

    private Bitmap TextRegion(byte[] data, Segment segment, out RegionInfo info)
    {
        var at = segment.Start;
        var end = segment.Start + segment.Length;
        info = ReadRegionInfo(data, ref at);
        var flags = U16(data, at);
        at += 2;
        if ((flags & 1) != 0)
            throw new NotSupportedException("Huffman-coded JBIG2 text regions aren't read.");
        var refine = (flags & 2) != 0;
        var logStrips = (flags >> 2) & 3;
        var corner = (flags >> 4) & 3;
        var transposed = (flags & 0x40) != 0;
        var op = (flags >> 7) & 3;
        var defaultPixel = (flags >> 9) & 1;
        var dsOffset = (flags >> 10) & 0x1F;
        if (dsOffset > 15)
            dsOffset -= 32;
        var refinementTemplate = (flags >> 15) & 1;
        var refinementAt = refine && refinementTemplate == 0 ? ReadAt(data, ref at, 2) : [];
        var instances = U32(data, at);
        at += 4;
        if (instances > _limits.MaxInstances)
            throw DocumentLimits.Exceeded($"A JBIG2 text region places more than {DocumentLimits.Count(_limits.MaxInstances)} symbols.");
        var symbols = InputSymbols(segment);
        var region = NewBitmap(info.Width, info.Height);
        if (defaultPixel != 0)
            region.Pixels.AsSpan().Fill(1);
        var coding = new Coding(data, at, end) { Id = new IdDecoder(CodeLength(symbols.Count)) };
        DrawText(coding, region, instances, symbols, 1 << logStrips, dsOffset, corner, transposed, op, defaultPixel, refine, refinementTemplate, refinementAt);
        return region;
    }

    /// <summary>Text region decoding (T.88 6.4.5): places <paramref name="count"/> symbol instances on <paramref name="region"/>.</summary>
    private void DrawText(Coding coding, Bitmap region, long count, List<Bitmap> symbols, int strips, int dsOffset, int corner, bool transposed, int op, int defaultPixel, bool refine, int refinementTemplate, sbyte[] refinementAt)
    {
        _ = defaultPixel;
        var mq = coding.Mq;
        var id = coding.Id!;
        long stripT = -(coding.Dt.Decode(mq) ?? 0) * strips;
        long firstS = 0;
        long placed = 0;
        // Pixels drawn, bounded so that a few huge symbols placed millions of times can't run for hours.
        long drawn = 0;
        while (placed < count)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            stripT += (coding.Dt.Decode(mq) ?? throw new InvalidDataException("A JBIG2 text region is damaged.")) * strips;
            firstS += coding.Fs.Decode(mq) ?? throw new InvalidDataException("A JBIG2 text region is damaged.");
            var curS = firstS;
            var first = true;
            while (true)
            {
                if (!first)
                {
                    if (coding.Ds.Decode(mq) is not { } deltaS)
                        break;
                    curS += deltaS + dsOffset;
                }
                first = false;
                if (placed >= count)
                    break;
                var currentT = strips > 1 ? coding.It.Decode(mq) ?? 0 : 0;
                var t = stripT + currentT;
                var symbolId = id.Decode(mq);
                if (symbolId >= symbols.Count)
                    throw new InvalidDataException("A JBIG2 text region places a symbol that doesn't exist.");
                var symbol = symbols[symbolId];
                if (refine && (coding.Ri.Decode(mq) ?? 0) != 0)
                {
                    var rdw = coding.Rdw.Decode(mq) ?? 0;
                    var rdh = coding.Rdh.Decode(mq) ?? 0;
                    var rdx = coding.Rdx.Decode(mq) ?? 0;
                    var rdy = coding.Rdy.Decode(mq) ?? 0;
                    var refined = NewBitmap(symbol.Width + rdw, symbol.Height + rdh);
                    DecodeRefinement(coding, refined, symbol, Clamp((rdw >> 1) + rdx), Clamp((rdh >> 1) + rdy), refinementTemplate, false, refinementAt);
                    _pixels -= (long)refined.Width * refined.Height; // a refined instance is drawn, not kept
                    symbol = refined;
                }
                int wi = symbol.Width, hi = symbol.Height;
                if (!transposed && corner >= 2)
                    curS += wi - 1;
                else if (transposed && (corner & 1) == 0)
                    curS += hi - 1;
                long x, y;
                if (!transposed)
                {
                    x = corner >= 2 ? curS - wi + 1 : curS;
                    y = (corner & 1) != 0 ? t : t - hi + 1;
                }
                else
                {
                    x = corner >= 2 ? t - wi + 1 : t;
                    y = (corner & 1) != 0 ? curS : curS - hi + 1;
                }
                region.Combine(symbol, x, y, op);
                drawn += (long)wi * hi + 1;
                if (drawn > 4 * _limits.MaxTotalPixels)
                    throw DocumentLimits.Exceeded("A JBIG2 text region draws too many pixels.");
                if (!transposed && corner < 2)
                    curS += wi - 1;
                else if (transposed && (corner & 1) != 0)
                    curS += hi - 1;
                placed++;
            }
        }
    }

    /// <summary>T.88's integer arithmetic decoder (Annex A.2); null is OOB.</summary>
    private sealed class IntegerDecoder
    {
        private readonly byte[] _contexts = new byte[512];

        public long? Decode(MqDecoder mq)
        {
            var prev = 1;
            int Bit()
            {
                var bit = mq.Decode(_contexts, prev);
                prev = prev < 256 ? (prev << 1) | bit : (((prev << 1) | bit) & 511) | 256;
                return bit;
            }
            var sign = Bit();
            (int Bits, long Offset) range = Bit() == 0 ? (2, 0)
                : Bit() == 0 ? (4, 4)
                : Bit() == 0 ? (6, 20)
                : Bit() == 0 ? (8, 84)
                : Bit() == 0 ? (12, 340)
                : (32, 4436);
            long value = 0;
            for (var i = 0; i < range.Bits; i++)
                value = (value << 1) | (uint)Bit();
            value += range.Offset;
            if (sign != 0 && value == 0)
                return null;
            if (value > int.MaxValue)
                throw new InvalidDataException("A JBIG2 number is out of range.");
            return sign != 0 ? -value : value;
        }
    }

    /// <summary>IAID: a symbol id of a fixed number of bits.</summary>
    private sealed class IdDecoder(int length)
    {
        private readonly byte[] _contexts = new byte[1 << Math.Min(length, 24)];

        public int Decode(MqDecoder mq)
        {
            if (length > 24)
                throw new InvalidDataException("A JBIG2 text region has too many symbols.");
            var prev = 1;
            for (var i = 0; i < length; i++)
                prev = (prev << 1) | mq.Decode(_contexts, prev);
            return prev - (1 << length);
        }
    }

    /// <summary>The MQ arithmetic decoder (T.88 Annex E.3); past its data it reads 0xFF bytes, as the standard says.</summary>
    private sealed class MqDecoder
    {
        private readonly byte[] _data;
        private readonly int _end;
        private int _bp;
        private uint _c;
        private uint _a;
        private int _ct;

        public MqDecoder(byte[] data, int start, int end)
        {
            _data = data;
            _end = Math.Clamp(end, 0, data.Length);
            _bp = start;
            _c = (uint)Byte(_bp) << 16;
            ByteIn();
            _c <<= 7;
            _ct -= 7;
            _a = 0x8000;
        }

        private int Byte(int at) => at >= 0 && at < _end ? _data[at] : 0xFF;

        private void ByteIn()
        {
            if (Byte(_bp) == 0xFF)
            {
                if (Byte(_bp + 1) > 0x8F)
                {
                    _c += 0xFF00;
                    _ct = 8;
                }
                else
                {
                    _bp++;
                    _c += (uint)Byte(_bp) << 9;
                    _ct = 7;
                }
            }
            else
            {
                _bp++;
                _c += (uint)Byte(_bp) << 8;
                _ct = 8;
            }
        }

        public int Decode(byte[] contexts, int cx)
        {
            var state = contexts[cx] >> 1;
            var mps = contexts[cx] & 1;
            uint qe = MqTables.Qe[state];
            int d;
            _a -= qe;
            if ((_c >> 16) < qe)
            {
                // LPS exchange.
                if (_a < qe)
                {
                    d = mps;
                    contexts[cx] = (byte)((MqTables.NextMps[state] << 1) | mps);
                }
                else
                {
                    d = 1 - mps;
                    contexts[cx] = (byte)((MqTables.NextLps[state] << 1) | (MqTables.Switch[state] ? 1 - mps : mps));
                }
                _a = qe;
            }
            else
            {
                _c -= qe << 16;
                if ((_a & 0x8000) != 0)
                    return mps;
                // MPS exchange.
                if (_a < qe)
                {
                    d = 1 - mps;
                    contexts[cx] = (byte)((MqTables.NextLps[state] << 1) | (MqTables.Switch[state] ? 1 - mps : mps));
                }
                else
                {
                    d = mps;
                    contexts[cx] = (byte)((MqTables.NextMps[state] << 1) | mps);
                }
            }
            do
            {
                if (_ct == 0)
                    ByteIn();
                _a <<= 1;
                _c <<= 1;
                _ct--;
            }
            while ((_a & 0x8000) == 0);
            return d;
        }
    }
}
