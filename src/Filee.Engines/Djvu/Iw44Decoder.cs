// IW44, DjVu's wavelet picture coding (DjVu v3 specification, "IW44"): the background (BG44) and foreground colours
// (FG44) of a page. A picture is a luminance map and, in colour, two chrominance maps (YCbCr, chrominance optionally at
// half resolution and starting a few slices late), each in blocks of 32 × 32 wavelet coefficients. The coefficients
// arrive in slices, from the coarsest band and the highest bit down: each slice tells which coefficients become
// significant at the current threshold, their signs, and one more bit of those already known. A picture may come in
// several chunks, each adding slices.
//
// Reconstruction: the coefficients of all blocks in one array, the lifting transform run backwards from scale 16 to 1
// (scale 2 for half-resolution chrominance, then each value repeated over 2 × 2), each value rounded (six fraction
// bits) to −128..127, then the "Pigeon" YCbCr → RGB transform, or 127 − Y for a grey picture.

using Filee.Engines.Documents;

namespace Filee.Engines.Djvu;

/// <summary>A decoded picture: RGB bytes, three per pixel, top row first.</summary>
internal sealed class DjvuPixmap(int width, int height, byte[] rgb)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Rgb { get; } = rgb;
}

/// <summary>Decodes the chunks of one IW44 picture, in order.</summary>
internal sealed class Iw44Decoder(DjvuLimits limits)
{
    private Iw44Map? _y, _cb, _cr;
    private Iw44Codec? _yCodec, _cbCodec, _crCodec;
    private int _serial;
    private int _slices;
    private int _chromaDelay;
    private bool _chromaHalf;

    public bool HasData => _y is not null;

    /// <summary>Adds one chunk (BG44, FG44, PM44, BM44).</summary>
    public void Decode(byte[] chunk, CancellationToken cancellationToken = default)
    {
        if (chunk.Length < 2)
            throw new InvalidDataException("An IW44 chunk is too short.");
        var serial = chunk[0];
        var slices = chunk[1];
        if (serial != _serial)
            throw new InvalidDataException("IW44 chunks are out of order.");
        var at = 2;
        if (serial == 0)
        {
            if (chunk.Length < 9)
                throw new InvalidDataException("An IW44 chunk is too short.");
            var major = chunk[2];
            var minor = chunk[3];
            if ((major & 0x7F) != 1)
                throw new InvalidDataException("An IW44 picture uses a coder version Filee doesn't know.");
            if (minor > 2)
                throw new InvalidDataException("An IW44 picture uses a newer coder version than Filee knows.");
            var width = (chunk[4] << 8) | chunk[5];
            var height = (chunk[6] << 8) | chunk[7];
            at = 8;
            _chromaDelay = 0;
            _chromaHalf = false;
            if (minor >= 2)
            {
                var delay = chunk[at++];
                _chromaDelay = delay & 0x7F;
                _chromaHalf = (delay & 0x80) == 0;
            }
            if ((major & 0x80) != 0)
                _chromaDelay = -1;
            if (width == 0 || height == 0)
                throw new InvalidDataException("An IW44 picture has no size.");
            if ((long)width * height > limits.MaxIw44Pixels)
                throw DocumentLimits.TooLarge($"A DjVu picture has more than {DocumentLimits.Count(limits.MaxIw44Pixels)} pixels.");
            _y = new Iw44Map(width, height);
            _yCodec = new Iw44Codec(_y);
            if (_chromaDelay >= 0)
            {
                _cb = new Iw44Map(width, height);
                _cr = new Iw44Map(width, height);
                _cbCodec = new Iw44Codec(_cb);
                _crCodec = new Iw44Codec(_cr);
            }
        }
        if (_yCodec is null)
            throw new InvalidDataException("An IW44 picture is missing its first chunk.");
        var total = _slices + slices;
        if (total > limits.MaxSlices)
            throw DocumentLimits.Exceeded($"A DjVu picture has more than {DocumentLimits.Count(limits.MaxSlices)} slices.");
        var zp = new ZpDecoder(chunk, at, chunk.Length - at);
        var more = true;
        while (more && _slices < total)
        {
            cancellationToken.ThrowIfCancellationRequested();
            more = _yCodec.DecodeSlice(zp);
            if (_cbCodec is not null && _crCodec is not null && _chromaDelay <= _slices)
            {
                more |= _cbCodec.DecodeSlice(zp);
                more |= _crCodec.DecodeSlice(zp);
            }
            _slices++;
        }
        _serial++;
    }

    /// <summary>The picture so far.</summary>
    public DjvuPixmap ToPixmap(CancellationToken cancellationToken = default)
    {
        var y = _y ?? throw new InvalidDataException("An IW44 picture has no data.");
        int w = y.Width, h = y.Height;
        var luma = y.Reconstruct(half: false, cancellationToken);
        var rgb = new byte[(long)w * h * 3];
        var color = _cb is not null && _cr is not null && _chromaDelay >= 0;
        var cb = color ? _cb!.Reconstruct(_chromaHalf, cancellationToken) : null;
        var cr = color ? _cr!.Reconstruct(_chromaHalf, cancellationToken) : null;
        // The maps' row 0 is the bottom of the picture (as DjVu draws everything); the pixmap's is the top.
        for (var row = 0; row < h; row++)
        {
            var i = row * w;
            var o = (h - 1 - row) * w * 3;
            for (var x = 0; x < w; x++, i++, o += 3)
            {
                int yy = luma[i];
                if (!color)
                {
                    rgb[o] = rgb[o + 1] = rgb[o + 2] = (byte)(127 - yy);
                    continue;
                }
                int b = cb![i], r = cr![i];
                var t1 = b >> 2;
                var t2 = r + (r >> 1);
                var t3 = yy + 128 - t1;
                rgb[o] = Clamp(yy + 128 + t2);
                rgb[o + 1] = Clamp(t3 - (t2 >> 1));
                rgb[o + 2] = Clamp(t3 + (b << 1));
            }
        }
        return new DjvuPixmap(w, h, rgb);
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}

/// <summary>The coefficients of one IW44 component, block by block, each bucket allocated when first coded.</summary>
internal sealed class Iw44Map
{
    public Iw44Map(int width, int height)
    {
        Width = width;
        Height = height;
        BlockWidth = (width + 31) & ~31;
        BlockHeight = (height + 31) & ~31;
        Blocks = new Iw44Block[BlockWidth / 32 * (BlockHeight / 32)];
    }

    public int Width { get; }
    public int Height { get; }
    public int BlockWidth { get; }
    public int BlockHeight { get; }
    public Iw44Block[] Blocks { get; }

    /// <summary>The component's values (−128..127), row 0 at the bottom of the picture.</summary>
    public sbyte[] Reconstruct(bool half, CancellationToken cancellationToken)
    {
        int bw = BlockWidth, bh = BlockHeight;
        var data = new short[(long)bw * bh];
        var index = 0;
        for (var i = 0; i < bh; i += 32)
        {
            for (var j = 0; j < bw; j += 32)
            {
                var block = Blocks[index++];
                if (block.Coefficients is not { } coefficients)
                    continue;
                for (var n = 0; n < 1024; n++)
                {
                    if (!block.Has(n >> 4))
                    {
                        n |= 15;
                        continue;
                    }
                    var at = ZigZag[n];
                    data[(i + (at >> 5)) * bw + j + (at & 31)] = coefficients[n];
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        Iw44Transform.Backward(data, Width, Height, bw, 32, half ? 2 : 1);
        if (half)
        {
            for (var i = 0; i < bh; i += 2)
            {
                var row = i * bw;
                for (var j = 0; j < bw; j += 2)
                {
                    var v = data[row + j];
                    data[row + j + 1] = v;
                    if (i + 1 < bh)
                        data[row + bw + j] = data[row + bw + j + 1] = v;
                }
            }
        }
        var result = new sbyte[(long)Width * Height];
        for (var i = 0; i < Height; i++)
        {
            var row = i * bw;
            var o = i * Width;
            for (var j = 0; j < Width; j++)
                result[o + j] = (sbyte)Math.Clamp((data[row + j] + 32) >> 6, -128, 127);
        }
        return result;
    }

    /// <summary>
    /// Where coefficient n of a block lies in its 32 × 32 square (row × 32 + column): n's bits, from the lowest, are
    /// alternately the column's and the row's, from their highest bit down.
    /// </summary>
    internal static readonly int[] ZigZag = BuildZigZag();

    private static int[] BuildZigZag()
    {
        var table = new int[1024];
        for (var n = 0; n < 1024; n++)
        {
            int x = 0, y = 0;
            for (var bit = 0; bit < 5; bit++)
            {
                x |= ((n >> (2 * bit)) & 1) << (4 - bit);
                y |= ((n >> (2 * bit + 1)) & 1) << (4 - bit);
            }
            table[n] = y * 32 + x;
        }
        return table;
    }
}

/// <summary>A block's 64 buckets of 16 coefficients; a bucket not yet coded is absent.</summary>
internal struct Iw44Block
{
    private ulong _present;

    public short[]? Coefficients { get; private set; }

    public readonly bool Has(int bucket) => (_present & (1UL << bucket)) != 0;

    /// <summary>Allocates bucket <paramref name="bucket"/> (zeroes) when it is absent.</summary>
    public void Allocate(int bucket)
    {
        Coefficients ??= new short[1024];
        _present |= 1UL << bucket;
    }
}

/// <summary>Decodes slices into one component's coefficients.</summary>
internal sealed class Iw44Codec
{
    private const int Zero = 1, Active = 2, New = 4, Unknown = 8;

    internal static readonly int[] Quantization =
    [
        0x004000, 0x008000, 0x008000, 0x010000, 0x010000, 0x010000, 0x020000, 0x020000,
        0x020000, 0x040000, 0x040000, 0x040000, 0x080000, 0x040000, 0x040000, 0x080000,
    ];

    /// <summary>The first bucket and the number of buckets of each band.</summary>
    internal static readonly (int Start, int Size)[] Bands =
        [(0, 1), (1, 1), (2, 1), (3, 1), (4, 4), (8, 4), (12, 4), (16, 16), (32, 16), (48, 16)];

    private readonly Iw44Map _map;
    private readonly int[] _quantLow = new int[16];
    private readonly int[] _quantHigh = new int[10];
    private readonly byte[] _coefficientState = new byte[256];
    private readonly byte[] _bucketState = new byte[16];
    private readonly byte[] _contextStart = new byte[32];
    private readonly byte[,] _contextBucket = new byte[10, 8];
    private byte _contextMantissa;
    private byte _contextRoot;
    private int _band;
    private int _bit = 1;

    public Iw44Codec(Iw44Map map)
    {
        _map = map;
        var i = 0;
        var q = 0;
        for (; i < 4; i++)
            _quantLow[i] = Quantization[q++];
        for (var group = 0; group < 3; group++, q++)
        {
            for (var j = 0; j < 4; j++)
                _quantLow[i++] = Quantization[q];
        }
        _quantHigh[0] = 0;
        for (var j = 1; j < 10; j++)
            _quantHigh[j] = Quantization[q++];
    }

    /// <summary>Decodes one slice; false once the component has no more.</summary>
    public bool DecodeSlice(ZpDecoder zp)
    {
        if (_bit < 0)
            return false;
        if (!IsNullSlice())
        {
            var (start, size) = Bands[_band];
            var blocks = _map.Blocks;
            for (var b = 0; b < blocks.Length; b++)
                DecodeBuckets(zp, _band, ref blocks[b], start, size);
        }
        return FinishSlice();
    }

    private bool IsNullSlice()
    {
        if (_band == 0)
        {
            var isNull = true;
            for (var i = 0; i < 16; i++)
            {
                var threshold = _quantLow[i];
                _coefficientState[i] = Zero;
                if (threshold is > 0 and < 0x8000)
                {
                    _coefficientState[i] = Unknown;
                    isNull = false;
                }
            }
            return isNull;
        }
        var high = _quantHigh[_band];
        return !(high is > 0 and < 0x8000);
    }

    private bool FinishSlice()
    {
        _quantHigh[_band] >>= 1;
        if (_band == 0)
        {
            for (var i = 0; i < 16; i++)
                _quantLow[i] >>= 1;
        }
        if (++_band >= Bands.Length)
        {
            _band = 0;
            _bit++;
            if (_quantHigh[Bands.Length - 1] == 0)
            {
                _bit = -1;
                return false;
            }
        }
        return true;
    }

    /// <summary>The states of the band's buckets and coefficients before a slice; their union.</summary>
    private int Prepare(int first, int count, ref Iw44Block block)
    {
        var all = 0;
        var coefficients = block.Coefficients;
        if (first != 0)
        {
            for (var bucket = 0; bucket < count; bucket++)
            {
                var state = 0;
                if (!block.Has(first + bucket))
                {
                    state = Unknown;
                }
                else
                {
                    var at = (first + bucket) << 4;
                    for (var i = 0; i < 16; i++)
                    {
                        var c = coefficients![at + i] != 0 ? Active : Unknown;
                        _coefficientState[(bucket << 4) + i] = (byte)c;
                        state |= c;
                    }
                }
                _bucketState[bucket] = (byte)state;
                all |= state;
            }
        }
        else
        {
            if (!block.Has(0))
            {
                all = Unknown;
            }
            else
            {
                for (var i = 0; i < 16; i++)
                {
                    int c = _coefficientState[i];
                    if (c != Zero)
                        c = coefficients![i] != 0 ? Active : Unknown;
                    _coefficientState[i] = (byte)c;
                    all |= c;
                }
            }
            _bucketState[0] = (byte)all;
        }
        return all;
    }

    private void DecodeBuckets(ZpDecoder zp, int band, ref Iw44Block block, int first, int count)
    {
        var all = Prepare(first, count, ref block);
        if (count < 16 || (all & Active) != 0)
            all |= New;
        else if ((all & Unknown) != 0 && zp.Decode(ref _contextRoot) != 0)
            all |= New;

        if ((all & New) != 0)
        {
            for (var bucket = 0; bucket < count; bucket++)
            {
                if ((_bucketState[bucket] & Unknown) == 0)
                    continue;
                var ctx = 0;
                if (band > 0)
                {
                    // How many of the four coefficients of the parent bucket are significant.
                    var k = (first + bucket) << 2;
                    if (block.Has(k >> 4))
                    {
                        var c = block.Coefficients!;
                        var at = ((k >> 4) << 4) + (k & 15);
                        if (c[at] != 0)
                            ctx++;
                        if (c[at + 1] != 0)
                            ctx++;
                        if (c[at + 2] != 0)
                            ctx++;
                        if (ctx < 3 && c[at + 3] != 0)
                            ctx++;
                    }
                }
                if ((all & Active) != 0)
                    ctx |= 4;
                if (zp.Decode(ref _contextBucket[band, ctx]) != 0)
                    _bucketState[bucket] |= New;
            }

            var threshold = _quantHigh[band];
            for (var bucket = 0; bucket < count; bucket++)
            {
                if ((_bucketState[bucket] & New) == 0)
                    continue;
                var state = bucket << 4;
                if (!block.Has(first + bucket))
                {
                    block.Allocate(first + bucket);
                    for (var i = 0; i < 16; i++)
                    {
                        if (first != 0 || _coefficientState[state + i] != Zero)
                            _coefficientState[state + i] = Unknown;
                    }
                }
                var coefficients = block.Coefficients!;
                var at = (first + bucket) << 4;
                var gotcha = 0;
                for (var i = 0; i < 16; i++)
                {
                    if ((_coefficientState[state + i] & Unknown) != 0)
                        gotcha++;
                }
                for (var i = 0; i < 16; i++)
                {
                    if ((_coefficientState[state + i] & Unknown) == 0)
                        continue;
                    if (band == 0)
                        threshold = _quantLow[i];
                    var ctx = Math.Min(gotcha, 7);
                    if ((_bucketState[bucket] & Active) != 0)
                        ctx |= 8;
                    if (zp.Decode(ref _contextStart[ctx]) != 0)
                    {
                        _coefficientState[state + i] |= New;
                        var half = threshold >> 1;
                        var value = threshold + half - (half >> 2);
                        coefficients[at + i] = (short)(zp.DecodeIw() != 0 ? -value : value);
                    }
                    if ((_coefficientState[state + i] & New) != 0)
                        gotcha = 0;
                    else if (gotcha > 0)
                        gotcha--;
                }
            }
        }

        if ((all & Active) != 0)
        {
            var threshold = _quantHigh[band];
            var coefficients = block.Coefficients!;
            for (var bucket = 0; bucket < count; bucket++)
            {
                if ((_bucketState[bucket] & Active) == 0)
                    continue;
                var state = bucket << 4;
                var at = (first + bucket) << 4;
                for (var i = 0; i < 16; i++)
                {
                    if ((_coefficientState[state + i] & Active) == 0)
                        continue;
                    int value = coefficients[at + i];
                    var magnitude = Math.Abs(value);
                    if (band == 0)
                        threshold = _quantLow[i];
                    if (magnitude <= 3 * threshold)
                    {
                        magnitude += threshold >> 2;
                        magnitude += zp.Decode(ref _contextMantissa) != 0 ? threshold >> 1 : -threshold + (threshold >> 1);
                    }
                    else
                    {
                        magnitude += zp.DecodeIw() != 0 ? threshold >> 1 : -threshold + (threshold >> 1);
                    }
                    coefficients[at + i] = (short)(value > 0 ? magnitude : -magnitude);
                }
            }
        }
    }
}

/// <summary>The IW44 lifting transform, backwards (coefficients → pixels).</summary>
internal static class Iw44Transform
{
    /// <summary>Runs scales <paramref name="begin"/>/2 down to <paramref name="end"/> over <paramref name="p"/>.</summary>
    public static void Backward(short[] p, int w, int h, int rowsize, int begin, int end)
    {
        for (var scale = begin >> 1; scale >= end; scale >>= 1)
        {
            Vertical(p, w, h, rowsize, scale);
            Horizontal(p, w, h, rowsize, scale);
        }
    }

    private static void Vertical(short[] d, int w, int h, int rowsize, int scale)
    {
        var y = 0;
        var s = scale * rowsize;
        var s3 = s + s + s;
        h = ((h - 1) / scale) + 1;
        var p = 0;
        while (y - 3 < h)
        {
            // Undoes the prediction of the odd rows.
            var q = p;
            var e = q + w;
            if (y >= 3 && y + 3 < h)
            {
                for (; q < e; q += scale)
                {
                    var a = d[q - s] + d[q + s];
                    var b = d[q - s3] + d[q + s3];
                    d[q] = (short)(d[q] - (((a << 3) + a - b + 16) >> 5));
                }
            }
            else if (y < h)
            {
                var q1 = y + 1 < h ? q + s : -1;
                var q3 = y + 3 < h ? q + s3 : -1;
                for (; q < e; q += scale)
                {
                    var below1 = q1 >= 0 ? d[q1] : 0;
                    var below3 = q3 >= 0 ? d[q3] : 0;
                    int a, b;
                    if (y >= 3)
                    {
                        a = d[q - s] + below1;
                        b = d[q - s3] + below3;
                    }
                    else if (y >= 1)
                    {
                        a = d[q - s] + below1;
                        b = below3;
                    }
                    else
                    {
                        a = below1;
                        b = below3;
                    }
                    d[q] = (short)(d[q] - (((a << 3) + a - b + 16) >> 5));
                    if (q1 >= 0)
                        q1 += scale;
                    if (q3 >= 0)
                        q3 += scale;
                }
            }
            // Undoes the update of the even rows three rows up.
            q = p - s3;
            e = q + w;
            if (y >= 6 && y < h)
            {
                for (; q < e; q += scale)
                {
                    var a = d[q - s] + d[q + s];
                    var b = d[q - s3] + d[q + s3];
                    d[q] = (short)(d[q] + (((a << 3) + a - b + 8) >> 4));
                }
            }
            else if (y >= 3)
            {
                var q1 = y - 2 < h ? q + s : q - s;
                for (; q < e; q += scale, q1 += scale)
                {
                    var a = d[q - s] + d[q1];
                    d[q] = (short)(d[q] + ((a + 1) >> 1));
                }
            }
            y += 2;
            p += s + s;
        }
    }

    private static void Horizontal(short[] d, int w, int h, int rowsize, int scale)
    {
        var s = scale;
        var s3 = s + s + s;
        rowsize *= scale;
        var p = 0;
        for (var y = 0; y < h; y += scale, p += rowsize)
        {
            var q = p;
            var e = p + w;
            int a0 = 0, a1 = 0, a2 = 0, a3 = 0;
            int b0 = 0, b1 = 0, b2 = 0, b3 = 0;
            if (q < e)
            {
                if (q + s < e)
                    a2 = d[q + s];
                if (q + s3 < e)
                    a3 = d[q + s3];
                b2 = b3 = d[q] - ((((a1 + a2) << 3) + (a1 + a2) - a0 - a3 + 16) >> 5);
                d[q] = (short)b3;
                q += s + s;
            }
            if (q < e)
            {
                a0 = a1;
                a1 = a2;
                a2 = a3;
                if (q + s3 < e)
                    a3 = d[q + s3];
                b3 = d[q] - ((((a1 + a2) << 3) + (a1 + a2) - a0 - a3 + 16) >> 5);
                d[q] = (short)b3;
                q += s + s;
            }
            if (q < e)
            {
                b1 = b2;
                b2 = b3;
                a0 = a1;
                a1 = a2;
                a2 = a3;
                if (q + s3 < e)
                    a3 = d[q + s3];
                b3 = d[q] - ((((a1 + a2) << 3) + (a1 + a2) - a0 - a3 + 16) >> 5);
                d[q] = (short)b3;
                d[q - s3] = (short)(d[q - s3] + ((b1 + b2 + 1) >> 1));
                q += s + s;
            }
            while (q + s3 < e)
            {
                a0 = a1;
                a1 = a2;
                a2 = a3;
                a3 = d[q + s3];
                b0 = b1;
                b1 = b2;
                b2 = b3;
                b3 = d[q] - ((((a1 + a2) << 3) + (a1 + a2) - a0 - a3 + 16) >> 5);
                d[q] = (short)b3;
                d[q - s3] = (short)(d[q - s3] + ((((b1 + b2) << 3) + (b1 + b2) - b0 - b3 + 8) >> 4));
                q += s + s;
            }
            while (q < e)
            {
                a0 = a1;
                a1 = a2;
                a2 = a3;
                a3 = 0;
                b0 = b1;
                b1 = b2;
                b2 = b3;
                b3 = d[q] - ((((a1 + a2) << 3) + (a1 + a2) - a0 - a3 + 16) >> 5);
                d[q] = (short)b3;
                d[q - s3] = (short)(d[q - s3] + ((((b1 + b2) << 3) + (b1 + b2) - b0 - b3 + 8) >> 4));
                q += s + s;
            }
            while (q - s3 < e)
            {
                b0 = b1;
                b1 = b2;
                b2 = b3;
                if (q - s3 >= p)
                    d[q - s3] = (short)(d[q - s3] + ((b1 + b2 + 1) >> 1));
                q += s + s;
            }
        }
    }
}
