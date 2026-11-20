// Writes DjVu files in code for the DjVu reader's tests (DjvuTests, DjvuFuzzTests): small encoders for every piece the
// reader decodes, written from the DjVu v3 specification as the mirror image of the decoders — the ZP-coder, BZZ (a
// plain suffix sort for the block transform), JB2 (new shapes, copies, refinements, non-mark data, dictionaries,
// comments, resets), IW44 (the forward lifting transform and a slice coder that decides each bit from the true
// coefficients), hidden text zones, palettes, INFO and the IFF container, one page or bundled with a directory.

using System.Text;
using Filee.Engines.Djvu;

namespace Filee.Engines.Tests;

/// <summary>The ZP-coder, encoding.</summary>
internal sealed class ZpEncoder
{
    private readonly MemoryStream _output = new();
    private uint _a;
    private uint _subend;
    private uint _buffer = 0xFFFFFF;
    private int _nrun;
    private int _delay = 25;
    private int _byte;
    private int _scount;

    public void Encode(int bit, ref byte context)
    {
        var z = _a + ZpDecoder.P[context];
        if (bit != (context & 1))
            EncodeLps(ref context, z);
        else if (z >= 0x8000)
            EncodeMps(ref context, z);
        else
            _a = z;
    }

    public void EncodeRaw(int bit) => EncodeFixed(bit, 0x8000 + (_a >> 1));

    public void EncodeIw(int bit) => EncodeFixed(bit, 0x8000 + ((_a + _a + _a) >> 3));

    private void EncodeFixed(int bit, uint z)
    {
        if (bit != 0)
        {
            z = 0x10000 - z;
            _subend += z;
            _a += z;
            while (_a >= 0x8000)
                Shift();
        }
        else
        {
            _a = z;
            if (_a >= 0x8000)
                Shift();
        }
    }

    private static uint Clamp(uint z, uint a)
    {
        var d = 0x6000 + ((z + a) >> 2);
        return z > d ? d : z;
    }

    private void EncodeMps(ref byte context, uint z)
    {
        z = Clamp(z, _a);
        if (_a >= ZpDecoder.M[context])
            context = ZpDecoder.Up[context];
        _a = z;
        if (_a >= 0x8000)
            Shift();
    }

    private void EncodeLps(ref byte context, uint z)
    {
        z = Clamp(z, _a);
        context = ZpDecoder.Dn[context];
        z = 0x10000 - z;
        _subend += z;
        _a += z;
        while (_a >= 0x8000)
            Shift();
    }

    private void Shift()
    {
        Emit((int)(1 - (_subend >> 15)));
        _subend = (_subend << 1) & 0xFFFF;
        _a = (_a << 1) & 0xFFFF;
    }

    private void Emit(int b)
    {
        _buffer = (_buffer << 1) + (uint)b;
        b = (int)(_buffer >> 24);
        _buffer &= 0xFFFFFF;
        switch (b)
        {
            case 1:
                OutBit(1);
                while (_nrun-- > 0)
                    OutBit(0);
                _nrun = 0;
                break;
            case 0xFF:
                OutBit(0);
                while (_nrun-- > 0)
                    OutBit(1);
                _nrun = 0;
                break;
            case 0:
                _nrun++;
                break;
            default:
                throw new InvalidOperationException("ZP encoder state");
        }
    }

    private void OutBit(int bit)
    {
        if (_delay > 0)
        {
            if (_delay < 0xFF)
                _delay--;
            return;
        }
        _byte = (_byte << 1) | bit;
        if (++_scount == 8)
        {
            _output.WriteByte((byte)_byte);
            _scount = 0;
            _byte = 0;
        }
    }

    public byte[] Finish()
    {
        if (_subend > 0x8000)
            _subend = 0x10000;
        else if (_subend > 0)
            _subend = 0x8000;
        while (_buffer != 0xFFFFFF || _subend != 0)
        {
            Emit((int)(1 - (_subend >> 15)));
            _subend = (_subend << 1) & 0xFFFF;
        }
        OutBit(1);
        while (_nrun-- > 0)
            OutBit(0);
        _nrun = 0;
        while (_scount > 0)
            OutBit(1);
        _delay = 0xFF;
        return _output.ToArray();
    }
}

/// <summary>BZZ, encoding.</summary>
internal static class BzzEncoder
{
    public static byte[] Encode(byte[] data, int blockSize = 1 << 20)
    {
        var zp = new ZpEncoder();
        var contexts = new byte[300];
        for (var at = 0; at < data.Length; at += blockSize)
            EncodeBlock(zp, contexts, data.AsSpan(at, Math.Min(blockSize, data.Length - at)).ToArray());
        EncodeRaw(zp, 24, 0);
        return zp.Finish();
    }

    private static void EncodeRaw(ZpEncoder zp, int bits, int value)
    {
        for (var bit = bits - 1; bit >= 0; bit--)
            zp.EncodeRaw((value >> bit) & 1);
    }

    private static void EncodeBlock(ZpEncoder zp, byte[] contexts, byte[] block)
    {
        var n = block.Length;
        var size = n + 1;
        // The suffixes of block + marker, sorted (the marker sorts before every byte).
        var order = Enumerable.Range(0, size).ToArray();
        Array.Sort(order, (x, y) =>
        {
            while (x < n && y < n)
            {
                if (block[x] != block[y])
                    return block[x].CompareTo(block[y]);
                x++;
                y++;
            }
            return (x == n ? 0 : 1).CompareTo(y == n ? 0 : 1);
        });
        var last = new byte[size];
        var marker = -1;
        for (var r = 0; r < size; r++)
        {
            if (order[r] == 0)
                marker = r;
            else
                last[r] = block[order[r] - 1];
        }

        EncodeRaw(zp, 24, size);
        zp.EncodeRaw(0); // estimation speed 0
        var mtf = new byte[256];
        var rmtf = new int[256];
        for (var i = 0; i < 256; i++)
        {
            mtf[i] = (byte)i;
            rmtf[i] = i;
        }
        var freq = new uint[4];
        uint fadd = 4;
        var mtfno = 3;
        for (var i = 0; i < size; i++)
        {
            var c = last[i];
            var ctxid = Math.Min(2, mtfno);
            mtfno = i == marker ? 256 : rmtf[c];
            zp.Encode(mtfno == 0 ? 1 : 0, ref contexts[ctxid]);
            if (mtfno != 0)
            {
                zp.Encode(mtfno == 1 ? 1 : 0, ref contexts[3 + ctxid]);
                if (mtfno != 1)
                {
                    var at = 6;
                    var found = false;
                    for (var bits = 1; bits <= 7; bits++)
                    {
                        var inside = mtfno < (2 << bits) ? 1 : 0;
                        zp.Encode(inside, ref contexts[at]);
                        if (inside == 1)
                        {
                            var value = mtfno - (1 << bits);
                            var node = 1;
                            for (var bit = bits - 1; bit >= 0; bit--)
                            {
                                var b = (value >> bit) & 1;
                                zp.Encode(b, ref contexts[at + node]);
                                node = (node << 1) | b;
                            }
                            found = true;
                            break;
                        }
                        at += 1 << bits;
                    }
                    if (!found)
                        continue; // the marker: no rotation
                }
            }
            fadd += fadd >> 0;
            if (fadd > 0x10000000)
            {
                fadd >>= 24;
                for (var k = 0; k < 4; k++)
                    freq[k] >>= 24;
            }
            var fc = fadd;
            if (mtfno < 4)
                fc += freq[mtfno];
            int k2;
            for (k2 = mtfno; k2 >= 4; k2--)
            {
                mtf[k2] = mtf[k2 - 1];
                rmtf[mtf[k2]] = k2;
            }
            for (; k2 > 0 && fc >= freq[k2 - 1]; k2--)
            {
                mtf[k2] = mtf[k2 - 1];
                freq[k2] = freq[k2 - 1];
                rmtf[mtf[k2]] = k2;
            }
            mtf[k2] = c;
            freq[k2] = fc;
            rmtf[c] = k2;
        }
    }
}

/// <summary>A JB2 stream, written record by record.</summary>
internal sealed class Jb2Writer
{
    private readonly ZpEncoder _zp = new();
    private byte[] _bits = new byte[1 << 20];
    private int[] _left = new int[1 << 20];
    private int[] _right = new int[1 << 20];
    private int _cells = 1;
    private int _recordType, _matchIndex, _commentLength, _commentByte;
    private int _absLocX, _absLocY, _absSizeX, _absSizeY, _imageSize, _inheritedCount;
    private int _relLocXCurrent, _relLocXLast, _relLocYCurrent, _relLocYLast, _relSizeX, _relSizeY;
    private byte _refinementFlag, _offsetType;
    private readonly byte[] _direct = new byte[1024];
    private readonly byte[] _cross = new byte[2048];
    private int _lastLeft, _lastRight, _lastBottom, _lastRowLeft, _lastRowBottom;
    private readonly int[] _shortList = new int[3];
    private int _shortListPosition;
    private int _columns, _rows;

    /// <summary>The library: shapes a later record may copy or refine, by library index.</summary>
    public List<Jb2Shape> Library { get; } = [];

    /// <summary>Makes a shape from rows of '#' (black) and '.', top row first.</summary>
    public static Jb2Shape Shape(params string[] rows)
    {
        var shape = new Jb2Shape(rows.Length == 0 ? 0 : rows[0].Length, rows.Length);
        for (var r = 0; r < rows.Length; r++)
        {
            for (var x = 0; x < rows[r].Length; x++)
                shape.Bits[(rows.Length - 1 - r) * shape.Width + x] = rows[r][x] == '#' ? (byte)1 : (byte)0;
        }
        shape.ComputeBox();
        return shape;
    }

    public Jb2Writer RequireDictionary(int shapes, IReadOnlyList<Jb2Shape> dictionary)
    {
        Number(9, 0, 11, ref _recordType);
        Number(shapes, 0, 262142, ref _inheritedCount);
        Library.AddRange(dictionary);
        return this;
    }

    public Jb2Writer Start(int columns, int rows)
    {
        Number(0, 0, 11, ref _recordType);
        Number(columns, 0, 262142, ref _imageSize);
        Number(rows, 0, 262142, ref _imageSize);
        _zp.Encode(0, ref _refinementFlag);
        _columns = columns;
        _rows = rows;
        _lastLeft = columns == 0 ? 1 : 1 + columns;
        _lastRowBottom = rows;
        _lastRowLeft = 0;
        _lastRight = 0;
        FillShortList(_lastRowBottom);
        return this;
    }

    /// <summary>A new shape coded directly: placed and in the library (type 1), library only (2) or placed only (3).</summary>
    public Jb2Writer New(Jb2Shape shape, int left = 0, int bottom = 0, int type = 1)
    {
        Number(type, 0, 11, ref _recordType);
        Number(shape.Width, 0, 262142, ref _absSizeX);
        Number(shape.Height, 0, 262142, ref _absSizeY);
        Direct(shape);
        if (type != 2)
            Location(left, bottom, shape.Height, shape.Width);
        if (type != 3)
            Library.Add(shape);
        return this;
    }

    /// <summary>A refinement of library shape <paramref name="match"/> (types 4, 5, 6 as for <see cref="New"/>).</summary>
    public Jb2Writer Refine(int match, Jb2Shape shape, int left = 0, int bottom = 0, int type = 4)
    {
        Number(type, 0, 11, ref _recordType);
        Number(match, 0, Library.Count - 1, ref _matchIndex);
        var reference = Library[match];
        var box = reference.Box;
        Number(shape.Width - (box.Right - box.Left + 1), -262143, 262142, ref _relSizeX);
        Number(shape.Height - (box.Top - box.Bottom + 1), -262143, 262142, ref _relSizeY);
        Cross(shape, reference);
        if (type != 5)
            Location(left, bottom, shape.Height, shape.Width);
        if (type != 6)
            Library.Add(shape);
        return this;
    }

    /// <summary>A copy of library shape <paramref name="match"/>, its bitmap's bottom-left at (left, bottom).</summary>
    public Jb2Writer Copy(int match, int left, int bottom)
    {
        Number(7, 0, 11, ref _recordType);
        Number(match, 0, Library.Count - 1, ref _matchIndex);
        var box = Library[match].Box;
        Location(left + box.Left, bottom + box.Bottom, box.Top - box.Bottom + 1, box.Right - box.Left + 1);
        return this;
    }

    /// <summary>Non-mark data at an absolute place (its top-left pixel at column left, row top from the bottom, 0-based).</summary>
    public Jb2Writer NonMark(Jb2Shape shape, int left, int top)
    {
        Number(8, 0, 11, ref _recordType);
        Number(shape.Width, 0, 262142, ref _absSizeX);
        Number(shape.Height, 0, 262142, ref _absSizeY);
        Direct(shape);
        Number(left + 1, 1, _columns, ref _absLocX);
        Number(top + 1, 1, _rows, ref _absLocY);
        return this;
    }

    public Jb2Writer Comment(string text)
    {
        Number(10, 0, 11, ref _recordType);
        var bytes = Encoding.UTF8.GetBytes(text);
        Number(bytes.Length, 0, 262142, ref _commentLength);
        foreach (var b in bytes)
            Number(b, 0, 255, ref _commentByte);
        return this;
    }

    /// <summary>Resets the number coder (record 9 after the start).</summary>
    public Jb2Writer Reset()
    {
        Number(9, 0, 11, ref _recordType);
        _recordType = _matchIndex = _commentLength = _commentByte = 0;
        _absLocX = _absLocY = _absSizeX = _absSizeY = _imageSize = _inheritedCount = 0;
        _relLocXCurrent = _relLocXLast = _relLocYCurrent = _relLocYLast = _relSizeX = _relSizeY = 0;
        Array.Clear(_bits);
        Array.Clear(_left);
        Array.Clear(_right);
        _cells = 1;
        return this;
    }

    /// <summary>Codes a raw record type (for hostile streams).</summary>
    public Jb2Writer Record(int type)
    {
        Number(type, 0, 11, ref _recordType);
        return this;
    }

    /// <summary>Codes a raw number with the absolute size context (for hostile streams).</summary>
    public Jb2Writer Size(int width, int height)
    {
        Number(width, 0, 262142, ref _absSizeX);
        Number(height, 0, 262142, ref _absSizeY);
        return this;
    }

    public byte[] End()
    {
        Number(11, 0, 11, ref _recordType);
        return _zp.Finish();
    }

    private void Direct(Jb2Shape shape)
    {
        for (var y = shape.Height - 1; y >= 0; y--)
        {
            for (var x = 0; x < shape.Width; x++)
            {
                var context =
                    (shape[x - 1, y + 2] << 9) | (shape[x, y + 2] << 8) | (shape[x + 1, y + 2] << 7) |
                    (shape[x - 2, y + 1] << 6) | (shape[x - 1, y + 1] << 5) | (shape[x, y + 1] << 4) |
                    (shape[x + 1, y + 1] << 3) | (shape[x + 2, y + 1] << 2) |
                    (shape[x - 2, y] << 1) | shape[x - 1, y];
                _zp.Encode(shape[x, y], ref _direct[context]);
            }
        }
    }

    private void Cross(Jb2Shape shape, Jb2Shape reference)
    {
        var box = reference.Box;
        int w = shape.Width, h = shape.Height;
        var xd2c = (w / 2 - w + 1) - ((box.Right - box.Left + 1) / 2 - box.Right);
        var yd2c = (h / 2 - h + 1) - ((box.Top - box.Bottom + 1) / 2 - box.Top);
        for (var y = h - 1; y >= 0; y--)
        {
            var cy = y + yd2c;
            for (var x = 0; x < w; x++)
            {
                var cx = x + xd2c;
                var context =
                    (shape[x - 1, y + 1] << 10) | (shape[x, y + 1] << 9) | (shape[x + 1, y + 1] << 8) |
                    (shape[x - 1, y] << 7) |
                    (reference[cx, cy + 1] << 6) |
                    (reference[cx - 1, cy] << 5) | (reference[cx, cy] << 4) | (reference[cx + 1, cy] << 3) |
                    (reference[cx - 1, cy - 1] << 2) | (reference[cx, cy - 1] << 1) | reference[cx + 1, cy - 1];
                _zp.Encode(shape[x, y], ref _cross[context]);
            }
        }
    }

    private void Location(int left0, int bottom0, int rows, int columns)
    {
        var left = left0 + 1;
        var bottom = bottom0 + 1;
        var right = left + columns - 1;
        var top = bottom + rows - 1;
        var newRow = left < _lastLeft;
        _zp.Encode(newRow ? 1 : 0, ref _offsetType);
        if (newRow)
        {
            Number(left - _lastRowLeft, -262143, 262142, ref _relLocXLast);
            Number(top - _lastRowBottom, -262143, 262142, ref _relLocYLast);
            _lastLeft = _lastRowLeft = left;
            _lastRight = right;
            _lastBottom = _lastRowBottom = bottom;
            FillShortList(bottom);
        }
        else
        {
            Number(left - _lastRight, -262143, 262142, ref _relLocXCurrent);
            Number(bottom - _lastBottom, -262143, 262142, ref _relLocYCurrent);
            _lastLeft = left;
            _lastRight = right;
            _lastBottom = UpdateShortList(bottom);
        }
    }

    private void FillShortList(int value)
    {
        _shortList[0] = _shortList[1] = _shortList[2] = value;
        _shortListPosition = 0;
    }

    private int UpdateShortList(int value)
    {
        if (++_shortListPosition == 3)
            _shortListPosition = 0;
        var s = _shortList;
        s[_shortListPosition] = value;
        return s[0] >= s[1]
            ? (s[0] > s[2] ? (s[1] >= s[2] ? s[1] : s[2]) : s[0])
            : (s[0] < s[2] ? (s[1] >= s[2] ? s[2] : s[1]) : s[0]);
    }

    private void Number(int v, int low, int high, ref int root)
    {
        var cutoff = 0;
        var phase = 1;
        var range = -1;
        if (root == 0)
            root = _cells++;
        var cell = root;
        while (true)
        {
            bool decision;
            if (low < cutoff && high >= cutoff)
            {
                decision = v >= cutoff;
                _zp.Encode(decision ? 1 : 0, ref _bits[cell]);
            }
            else
            {
                decision = v >= cutoff;
            }
            switch (phase)
            {
                case 1:
                    if (!decision)
                    {
                        v = -v - 1;
                        var temp = -low - 1;
                        low = -high - 1;
                        high = temp;
                    }
                    phase = 2;
                    cutoff = 1;
                    break;
                case 2:
                    if (!decision)
                    {
                        phase = 3;
                        range = (cutoff + 1) / 2;
                        if (range == 1)
                            cutoff = 0;
                        else
                            cutoff -= range / 2;
                    }
                    else
                    {
                        cutoff += cutoff + 1;
                    }
                    break;
                default:
                    range /= 2;
                    if (range != 1)
                        cutoff += decision ? range / 2 : -(range / 2);
                    else if (!decision)
                        cutoff--;
                    break;
            }
            if (range == 1)
                break;
            var next = decision ? _right[cell] : _left[cell];
            if (next == 0)
            {
                next = _cells++;
                if (decision)
                    _right[cell] = next;
                else
                    _left[cell] = next;
            }
            cell = next;
        }
    }
}

/// <summary>IW44, encoding: planes of −128..127 (top row first) to chunks.</summary>
internal static class Iw44Encoder
{
    /// <summary>
    /// Encodes a grey picture (<paramref name="cb"/> and <paramref name="cr"/> null; the decoder shows 127 − y) or a
    /// colour one, in <paramref name="chunks"/> chunks of <paramref name="slices"/> slices each.
    /// </summary>
    public static List<byte[]> Encode(int width, int height, sbyte[] y, sbyte[]? cb = null, sbyte[]? cr = null,
        int slices = 60, int chunks = 1, bool halfChroma = false, int chromaDelay = 0)
    {
        var color = cb is not null && cr is not null;
        var yc = new Coder(width, height, y);
        var cbc = color ? new Coder(width, height, cb!) : null;
        var crc = color ? new Coder(width, height, cr!) : null;
        var result = new List<byte[]>();
        var done = 0;
        for (var chunk = 0; chunk < chunks; chunk++)
        {
            var zp = new ZpEncoder();
            var header = new List<byte> { (byte)chunk, (byte)slices };
            if (chunk == 0)
            {
                header.Add((byte)(color ? 1 : 0x81));
                header.Add(2);
                header.Add((byte)(width >> 8));
                header.Add((byte)width);
                header.Add((byte)(height >> 8));
                header.Add((byte)height);
                header.Add((byte)((halfChroma ? 0 : 0x80) | chromaDelay));
            }
            var more = true;
            var total = done + slices;
            while (more && done < total)
            {
                more = yc.Slice(zp);
                if (cbc is not null && crc is not null && chromaDelay <= done)
                {
                    more |= cbc.Slice(zp);
                    more |= crc.Slice(zp);
                }
                done++;
            }
            result.Add([.. header, .. zp.Finish()]);
        }
        return result;
    }

    /// <summary>The slice coder of one component: the true coefficients and what the decoder holds so far.</summary>
    private sealed class Coder
    {
        private const int Zero = 1, Active = 2, New = 4, Unknown = 8;
        private readonly short[][] _target;
        private readonly short[]?[] _current;
        private readonly ulong[] _present;
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

        public Coder(int width, int height, sbyte[] plane)
        {
            var bw = (width + 31) & ~31;
            var bh = (height + 31) & ~31;
            var data = new short[bw * bh];
            for (var i = 0; i < height; i++)
            {
                // The decoder's row 0 is the bottom.
                var source = (height - 1 - i) * width;
                for (var j = 0; j < width; j++)
                    data[i * bw + j] = (short)(plane[source + j] << 6);
            }
            Forward(data, width, height, bw);
            var blocks = bw / 32 * (bh / 32);
            _target = new short[blocks][];
            _current = new short[blocks][];
            _present = new ulong[blocks];
            var index = 0;
            for (var i = 0; i < bh; i += 32)
            {
                for (var j = 0; j < bw; j += 32)
                {
                    var block = new short[1024];
                    for (var n = 0; n < 1024; n++)
                    {
                        var at = Iw44Map.ZigZag[n];
                        block[n] = data[(i + (at >> 5)) * bw + j + (at & 31)];
                    }
                    _target[index++] = block;
                }
            }
            var q = Iw44Codec.Quantization;
            var k = 0;
            var p = 0;
            for (; k < 4; k++)
                _quantLow[k] = q[p++];
            for (var group = 0; group < 3; group++, p++)
            {
                for (var j = 0; j < 4; j++)
                    _quantLow[k++] = q[p];
            }
            for (var j = 1; j < 10; j++)
                _quantHigh[j] = q[p++];
        }

        public bool Slice(ZpEncoder zp)
        {
            if (_bit < 0)
                return false;
            if (!IsNull())
            {
                var (start, size) = Iw44Codec.Bands[_band];
                for (var b = 0; b < _target.Length; b++)
                    Buckets(zp, b, start, size);
            }
            _quantHigh[_band] >>= 1;
            if (_band == 0)
            {
                for (var i = 0; i < 16; i++)
                    _quantLow[i] >>= 1;
            }
            if (++_band >= 10)
            {
                _band = 0;
                _bit++;
                if (_quantHigh[9] == 0)
                {
                    _bit = -1;
                    return false;
                }
            }
            return true;
        }

        private bool IsNull()
        {
            if (_band == 0)
            {
                var isNull = true;
                for (var i = 0; i < 16; i++)
                {
                    _coefficientState[i] = Zero;
                    if (_quantLow[i] is > 0 and < 0x8000)
                    {
                        _coefficientState[i] = Unknown;
                        isNull = false;
                    }
                }
                return isNull;
            }
            return !(_quantHigh[_band] is > 0 and < 0x8000);
        }

        private bool Has(int block, int bucket) => (_present[block] & (1UL << bucket)) != 0;

        private void Buckets(ZpEncoder zp, int b, int first, int count)
        {
            var target = _target[b];
            var current = _current[b];
            // Prepare, as the decoder does.
            var all = 0;
            if (first != 0)
            {
                for (var bucket = 0; bucket < count; bucket++)
                {
                    var state = 0;
                    if (!Has(b, first + bucket))
                    {
                        state = Unknown;
                        for (var i = 0; i < 16; i++)
                            _coefficientState[(bucket << 4) + i] = Unknown; // what it becomes when allocated
                    }
                    else
                    {
                        for (var i = 0; i < 16; i++)
                        {
                            var c = current![((first + bucket) << 4) + i] != 0 ? Active : Unknown;
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
                if (!Has(b, 0))
                {
                    all = Unknown;
                    // What the states become when the bucket is allocated (the decoder keeps the last block's until then).
                    for (var i = 0; i < 16; i++)
                    {
                        if (_coefficientState[i] != Zero)
                            _coefficientState[i] = Unknown;
                    }
                }
                else
                {
                    for (var i = 0; i < 16; i++)
                    {
                        int c = _coefficientState[i];
                        if (c != Zero)
                            c = current![i] != 0 ? Active : Unknown;
                        _coefficientState[i] = (byte)c;
                        all |= c;
                    }
                }
                _bucketState[0] = (byte)all;
            }

            // Which buckets get a new significant coefficient at this threshold.
            var becomes = new bool[count];
            var any = false;
            for (var bucket = 0; bucket < count; bucket++)
            {
                if ((_bucketState[bucket] & Unknown) == 0)
                    continue;
                for (var i = 0; i < 16; i++)
                {
                    if ((_coefficientState[(bucket << 4) + i] & Unknown) == 0)
                        continue;
                    var threshold = _band == 0 ? _quantLow[i] : _quantHigh[_band];
                    if (Math.Abs((int)target[((first + bucket) << 4) + i]) >= threshold)
                        becomes[bucket] = true;
                }
                any |= becomes[bucket];
            }

            if (count < 16 || (all & Active) != 0)
            {
                all |= New;
            }
            else if ((all & Unknown) != 0)
            {
                zp.Encode(any ? 1 : 0, ref _contextRoot);
                if (any)
                    all |= New;
            }

            if ((all & New) != 0)
            {
                for (var bucket = 0; bucket < count; bucket++)
                {
                    if ((_bucketState[bucket] & Unknown) == 0)
                        continue;
                    var ctx = 0;
                    if (_band > 0)
                    {
                        var k = (first + bucket) << 2;
                        if (Has(b, k >> 4))
                        {
                            if (current![k] != 0)
                                ctx++;
                            if (current[k + 1] != 0)
                                ctx++;
                            if (current[k + 2] != 0)
                                ctx++;
                            if (ctx < 3 && current[k + 3] != 0)
                                ctx++;
                        }
                    }
                    if ((all & Active) != 0)
                        ctx |= 4;
                    zp.Encode(becomes[bucket] ? 1 : 0, ref _contextBucket[_band, ctx]);
                    if (becomes[bucket])
                        _bucketState[bucket] |= New;
                }

                for (var bucket = 0; bucket < count; bucket++)
                {
                    if ((_bucketState[bucket] & New) == 0)
                        continue;
                    var state = bucket << 4;
                    if (!Has(b, first + bucket))
                    {
                        _current[b] ??= new short[1024];
                        current = _current[b];
                        _present[b] |= 1UL << (first + bucket);
                        for (var i = 0; i < 16; i++)
                        {
                            if (first != 0 || _coefficientState[state + i] != Zero)
                                _coefficientState[state + i] = Unknown;
                        }
                    }
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
                        var threshold = _band == 0 ? _quantLow[i] : _quantHigh[_band];
                        var ctx = Math.Min(gotcha, 7);
                        if ((_bucketState[bucket] & Active) != 0)
                            ctx |= 8;
                        var value = target[at + i];
                        var significant = Math.Abs((int)value) >= threshold;
                        zp.Encode(significant ? 1 : 0, ref _contextStart[ctx]);
                        if (significant)
                        {
                            _coefficientState[state + i] |= New;
                            var half = threshold >> 1;
                            var magnitude = threshold + half - (half >> 2);
                            zp.EncodeIw(value < 0 ? 1 : 0);
                            current![at + i] = (short)(value < 0 ? -magnitude : magnitude);
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
                        int value = current![at + i];
                        var magnitude = Math.Abs(value);
                        var threshold = _band == 0 ? _quantLow[i] : _quantHigh[_band];
                        var wanted = Math.Abs((int)target[at + i]);
                        if (magnitude <= 3 * threshold)
                        {
                            magnitude += threshold >> 2;
                            var bit = wanted >= magnitude ? 1 : 0;
                            zp.Encode(bit, ref _contextMantissa);
                            magnitude += bit == 1 ? threshold >> 1 : -threshold + (threshold >> 1);
                        }
                        else
                        {
                            var bit = wanted >= magnitude ? 1 : 0;
                            zp.EncodeIw(bit);
                            magnitude += bit == 1 ? threshold >> 1 : -threshold + (threshold >> 1);
                        }
                        current[at + i] = (short)(value > 0 ? magnitude : -magnitude);
                    }
                }
            }
        }
    }

    /// <summary>The forward lifting transform over scales 1 to 16.</summary>
    private static void Forward(short[] p, int w, int h, int rowsize)
    {
        for (var scale = 1; scale < 32; scale <<= 1)
        {
            Horizontal(p, w, h, rowsize, scale);
            Vertical(p, w, h, rowsize, scale);
        }
    }

    private static void Vertical(short[] d, int w, int h, int rowsize, int scale)
    {
        var s = scale * rowsize;
        var s3 = s + s + s;
        h = h > 0 ? ((h - 1) / scale) + 1 : 0;
        var y = 1;
        var p = s;
        while (y - 3 < h)
        {
            var q = p;
            var e = q + w;
            if (y >= 3 && y + 3 < h)
            {
                for (; q < e; q += scale)
                {
                    var a = d[q - s] + d[q + s];
                    var b = d[q - s3] + d[q + s3];
                    d[q] = (short)(d[q] - (((a << 3) + a - b + 8) >> 4));
                }
            }
            else if (y < h)
            {
                var q1 = y + 1 < h ? q + s : q - s;
                for (; q < e; q += scale, q1 += scale)
                {
                    var a = d[q - s] + d[q1];
                    d[q] = (short)(d[q] - ((a + 1) >> 1));
                }
            }
            q = p - s3;
            e = q + w;
            if (y >= 6 && y < h)
            {
                for (; q < e; q += scale)
                {
                    var a = d[q - s] + d[q + s];
                    var b = d[q - s3] + d[q + s3];
                    d[q] = (short)(d[q] + (((a << 3) + a - b + 16) >> 5));
                }
            }
            else if (y >= 3)
            {
                var q1 = y - 2 < h ? q + s : -1;
                var q3 = y < h ? q + s3 : -1;
                for (; q < e; q += scale)
                {
                    var below1 = q1 >= 0 ? d[q1] : 0;
                    var below3 = q3 >= 0 ? d[q3] : 0;
                    int a, b;
                    if (y >= 6)
                    {
                        a = d[q - s] + below1;
                        b = d[q - s3] + below3;
                    }
                    else if (y >= 4)
                    {
                        a = d[q - s] + below1;
                        b = below3;
                    }
                    else
                    {
                        a = below1;
                        b = below3;
                    }
                    d[q] = (short)(d[q] + (((a << 3) + a - b + 16) >> 5));
                    if (q1 >= 0)
                        q1 += scale;
                    if (q3 >= 0)
                        q3 += scale;
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
            var q = p + s;
            var e = p + w;
            int a0 = 0, a1 = 0, a2 = 0, a3 = 0;
            int b0 = 0, b1 = 0, b2 = 0, b3 = 0;
            if (q < e)
            {
                a1 = a2 = a3 = d[q - s];
                if (q + s < e)
                    a2 = d[q + s];
                if (q + s3 < e)
                    a3 = d[q + s3];
                b3 = d[q] - ((a1 + a2 + 1) >> 1);
                d[q] = (short)b3;
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
                b3 = d[q] - ((((a1 + a2) << 3) + (a1 + a2) - a0 - a3 + 8) >> 4);
                d[q] = (short)b3;
                d[q - s3] = (short)(d[q - s3] + ((((b1 + b2) << 3) + (b1 + b2) - b0 - b3 + 16) >> 5));
                q += s + s;
            }
            while (q < e)
            {
                a1 = a2;
                a2 = a3;
                b0 = b1;
                b1 = b2;
                b2 = b3;
                b3 = d[q] - ((a1 + a2 + 1) >> 1);
                d[q] = (short)b3;
                d[q - s3] = (short)(d[q - s3] + ((((b1 + b2) << 3) + (b1 + b2) - b0 - b3 + 16) >> 5));
                q += s + s;
            }
            while (q - s3 < e)
            {
                b0 = b1;
                b1 = b2;
                b2 = b3;
                b3 = 0;
                if (q - s3 >= p)
                    d[q - s3] = (short)(d[q - s3] + ((((b1 + b2) << 3) + (b1 + b2) - b0 - b3 + 16) >> 5));
                q += s + s;
            }
        }
    }
}

/// <summary>A text zone to write, with its box in page pixels (row 0 at the bottom) and the text it covers.</summary>
internal sealed record TextZone(int Type, int Left, int Bottom, int Width, int Height, int Start, int Length, params TextZone[] Children);

/// <summary>A DjVu file's chunks and forms.</summary>
internal static class DjvuBuilder
{
    public static byte[] Chunk(string id, byte[] data)
    {
        var result = new List<byte>(Encoding.ASCII.GetBytes(id));
        result.AddRange(BigEndian(data.Length, 4));
        result.AddRange(data);
        if ((data.Length & 1) != 0)
            result.Add(0);
        return [.. result];
    }

    /// <summary>A FORM of <paramref name="kind"/> with <paramref name="chunks"/> (each already padded).</summary>
    public static byte[] Form(string kind, params byte[][] chunks)
    {
        var body = new List<byte>(Encoding.ASCII.GetBytes(kind));
        foreach (var chunk in chunks)
            body.AddRange(chunk);
        return [.. Encoding.ASCII.GetBytes("FORM"), .. BigEndian(body.Count, 4), .. body];
    }

    public static byte[] DjvuFile(byte[] form) => [.. "AT&T"u8.ToArray(), .. form];

    public static byte[] Info(int width, int height, int dpi = 300, int flags = 1) =>
        Chunk("INFO", [(byte)(width >> 8), (byte)width, (byte)(height >> 8), (byte)height, 26, 0, (byte)dpi, (byte)(dpi >> 8), 22, (byte)flags]);

    /// <summary>A page: INFO then the other chunks.</summary>
    public static byte[] Page(int width, int height, int dpi, params byte[][] chunks) => Form("DJVU", [Info(width, height, dpi), .. chunks]);

    /// <summary>A palette (FGbz) with a colour index per blit; colours as (r, g, b).</summary>
    public static byte[] Palette((byte R, byte G, byte B)[] colors, int[]? blitColors)
    {
        var data = new List<byte> { (byte)(blitColors is null ? 0 : 0x80) };
        data.AddRange(BigEndian(colors.Length, 2));
        foreach (var (r, g, b) in colors)
            data.AddRange([b, g, r]);
        if (blitColors is not null)
        {
            data.AddRange(BigEndian(blitColors.Length, 3));
            data.AddRange(BzzEncoder.Encode([.. blitColors.SelectMany(i => BigEndian(i, 2))]));
        }
        return Chunk("FGbz", [.. data]);
    }

    /// <summary>TXTz: the text and its zone tree.</summary>
    public static byte[] Text(string text, TextZone page)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var data = new List<byte>();
        data.AddRange(BigEndian(bytes.Length, 3));
        data.AddRange(bytes);
        data.Add(1);
        WriteZone(data, page, null, null);
        return Chunk("TXTz", BzzEncoder.Encode([.. data]));
    }

    private static void WriteZone(List<byte> data, TextZone zone, TextZone? parent, TextZone? previous)
    {
        int x = zone.Left, y = zone.Bottom, start = zone.Start;
        if (previous is not null)
        {
            if (zone.Type is 1 or 4 or 5)
            {
                x -= previous.Left;
                y = previous.Bottom - zone.Bottom - zone.Height;
            }
            else
            {
                x -= previous.Left + previous.Width;
                y -= previous.Bottom;
            }
            start -= previous.Start + previous.Length;
        }
        else if (parent is not null)
        {
            x -= parent.Left;
            y = parent.Bottom + parent.Height - zone.Bottom - zone.Height;
            start -= parent.Start;
        }
        data.Add((byte)zone.Type);
        foreach (var value in new[] { x, y, zone.Width, zone.Height, start })
            data.AddRange(BigEndian(value + 0x8000, 2));
        data.AddRange(BigEndian(zone.Length, 3));
        data.AddRange(BigEndian(zone.Children.Length, 3));
        TextZone? last = null;
        foreach (var child in zone.Children)
        {
            WriteZone(data, child, zone, last);
            last = child;
        }
    }

    /// <summary>
    /// A bundled document: its directory and components, given as (id, form, is a page). Offsets are worked out here.
    /// </summary>
    public static byte[] Bundle(params (string Id, byte[] Form, bool Page)[] components)
    {
        var count = components.Length;
        byte[] Dirm(long[] offsets)
        {
            var data = new List<byte> { 0x81 };
            data.AddRange(BigEndian(count, 2));
            foreach (var offset in offsets)
                data.AddRange(BigEndian((int)offset, 4));
            var table = new List<byte>();
            foreach (var c in components)
                table.AddRange(BigEndian(c.Form.Length - 8, 3));
            foreach (var c in components)
                table.Add((byte)(c.Page ? 1 : 0));
            foreach (var c in components)
            {
                table.AddRange(Encoding.UTF8.GetBytes(c.Id));
                table.Add(0);
            }
            data.AddRange(BzzEncoder.Encode([.. table]));
            return Chunk("DIRM", [.. data]);
        }
        var dirmSize = Dirm(new long[count]).Length;
        var offsets = new long[count];
        long at = 4 + 12 + dirmSize;
        for (var i = 0; i < count; i++)
        {
            offsets[i] = at;
            at += components[i].Form.Length + (components[i].Form.Length & 1);
        }
        var parts = new List<byte[]> { Dirm(offsets) };
        foreach (var c in components)
            parts.Add((c.Form.Length & 1) != 0 ? [.. c.Form, 0] : c.Form);
        return DjvuFile(Form("DJVM", [.. parts]));
    }

    public static byte[] Include(string id) => Chunk("INCL", Encoding.UTF8.GetBytes(id));

    public static byte[] BigEndian(int value, int bytes)
    {
        var result = new byte[bytes];
        for (var i = 0; i < bytes; i++)
            result[i] = (byte)(value >> (8 * (bytes - 1 - i)));
        return result;
    }
}
