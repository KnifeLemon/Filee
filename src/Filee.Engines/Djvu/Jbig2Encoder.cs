// JBIG2 (ITU-T T.88) encoding of a page mask for PDF's /JBIG2Decode filter, lossless, in the embedded stream
// organisation PDF uses (segments without a file header): a page information segment, a symbol dictionary holding
// the mask's shapes and an immediate text region placing them. A DjVu mask (JB2) already is a set of shapes and their
// places, so they go across one for one; that keeps the PDF's masks close to the DjVu file's size.
//
// Everything is coded with the MQ arithmetic coder (T.88 Annex E): the symbols' heights and widths with the integer
// coders IADH / IADW and their bitmaps with generic region coding (template 0, its nominal adaptive pixels), the export
// flags with IAEX; in the text region, strips of four rows (IADT, IAIT), each symbol's place along its strip (IAFS,
// IADS) and its id (IAID). No Huffman tables, no refinement.

namespace Filee.Engines.Djvu;

internal static class Jbig2Encoder
{
    /// <summary>Rows per text region strip (log2).</summary>
    private const int LogStrips = 2;

    /// <summary>
    /// The mask drawn by <paramref name="placements"/> of <paramref name="shapes"/> (DjVu shapes, row 0 at the bottom,
    /// placed by their bottom-left pixel from the page's bottom-left corner) on a page of
    /// <paramref name="width"/> × <paramref name="height"/>, as JBIG2 segments.
    /// </summary>
    public static byte[] Encode(int width, int height, IReadOnlyList<Jb2Shape> shapes, IReadOnlyList<Jb2Blit> placements, CancellationToken cancellationToken = default)
    {
        // The shapes placed that have black pixels, as symbols ordered by height then width (small deltas between
        // them); a symbol's id is its place in that order.
        var ids = new Dictionary<int, int>();
        var symbols = new List<int>();
        foreach (var blit in placements)
        {
            var shape = shapes[blit.Shape];
            if (shape.Width > 0 && shape.Height > 0 && ids.TryAdd(blit.Shape, 0) && shape.Bits.AsSpan().ContainsAnyExcept((byte)0))
                symbols.Add(blit.Shape);
        }
        symbols.Sort((a, b) => shapes[a].Height != shapes[b].Height ? shapes[a].Height.CompareTo(shapes[b].Height) : shapes[a].Width.CompareTo(shapes[b].Width));
        ids.Clear();
        for (var i = 0; i < symbols.Count; i++)
            ids[symbols[i]] = i;

        var output = new List<byte>();
        // Page information: size, no resolution, lossless, white by default, shapes combined with OR.
        var page = new List<byte>();
        page.AddRange(U32(width));
        page.AddRange(U32(height));
        page.AddRange(U32(0));
        page.AddRange(U32(0));
        page.Add(0x01);
        page.AddRange([0, 0]);
        Segment(output, 0, 48, [], page);
        if (symbols.Count == 0)
            return [.. output];

        Segment(output, 1, 0, [], SymbolDictionary(shapes, symbols, cancellationToken));
        Segment(output, 2, 6, [1], TextRegion(width, height, shapes, placements, ids, symbols.Count, cancellationToken));
        return [.. output];
    }

    private static void Segment(List<byte> output, int number, int type, int[] referred, List<byte> data)
    {
        output.AddRange(U32(number));
        output.Add((byte)type);
        output.Add((byte)(referred.Length << 5));
        foreach (var segment in referred)
            output.Add((byte)segment);
        output.Add(1); // page 1
        output.AddRange(U32(data.Count));
        output.AddRange(data);
    }

    private static byte[] U32(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    /// <summary>The nominal adaptive pixels of generic template 0: (3, −1), (−3, −1), (2, −2), (−2, −2).</summary>
    private static readonly byte[] TemplateZero = [3, unchecked((byte)-1), unchecked((byte)-3), unchecked((byte)-1), 2, unchecked((byte)-2), unchecked((byte)-2), unchecked((byte)-2)];

    private static List<byte> SymbolDictionary(IReadOnlyList<Jb2Shape> shapes, List<int> symbols, CancellationToken cancellationToken)
    {
        var data = new List<byte> { 0, 0 }; // arithmetic coding, no aggregation, template 0
        data.AddRange(TemplateZero);
        data.AddRange(U32(symbols.Count)); // exported
        data.AddRange(U32(symbols.Count)); // new
        var coder = new MqEncoder();
        var deltaHeight = new IntegerCoder();
        var deltaWidth = new IntegerCoder();
        var export = new IntegerCoder();
        var generic = new byte[1 << 16];
        var classHeight = 0;
        for (var i = 0; i < symbols.Count;)
        {
            var height = shapes[symbols[i]].Height;
            deltaHeight.Encode(coder, height - classHeight);
            classHeight = height;
            var symbolWidth = 0;
            for (; i < symbols.Count && shapes[symbols[i]].Height == height; i++)
            {
                if ((i & 0xFF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var shape = shapes[symbols[i]];
                deltaWidth.Encode(coder, shape.Width - symbolWidth);
                symbolWidth = shape.Width;
                Generic(coder, generic, shape);
            }
            deltaWidth.EncodeOutOfBand(coder);
        }
        // Export flags: a run of none not exported, then all exported.
        export.Encode(coder, 0);
        export.Encode(coder, symbols.Count);
        data.AddRange(coder.Finish());
        return data;
    }

    /// <summary>Codes a shape's bitmap (top row first) with generic region template 0 in the shared contexts.</summary>
    private static void Generic(MqEncoder coder, byte[] contexts, Jb2Shape shape)
    {
        int w = shape.Width, h = shape.Height;
        // The row being coded and the two above it (blank above the first), with margins of four columns.
        byte[] row = new byte[w + 8], up = new byte[w + 8], up2 = new byte[w + 8];
        for (var y = 0; y < h; y++)
        {
            (up2, up, row) = (up, row, up2);
            Array.Copy(shape.Bits, (long)(h - 1 - y) * w, row, 4, w);
            for (var x = 4; x < w + 4; x++)
            {
                var c = row[x - 1] | (row[x - 2] << 1) | (row[x - 3] << 2) | (row[x - 4] << 3)
                    | (up[x + 2] << 4) | (up[x + 1] << 5) | (up[x] << 6) | (up[x - 1] << 7) | (up[x - 2] << 8)
                    | (up2[x + 1] << 9) | (up2[x] << 10) | (up2[x - 1] << 11)
                    | (up[x + 3] << 12) | (up[x - 3] << 13) | (up2[x + 2] << 14) | (up2[x - 2] << 15);
                coder.Encode(contexts, c, row[x]);
            }
        }
    }

    private static List<byte> TextRegion(int width, int height, IReadOnlyList<Jb2Shape> shapes, IReadOnlyList<Jb2Blit> placements, Dictionary<int, int> ids, int symbolCount, CancellationToken cancellationToken)
    {
        // Each placement by its bottom-left pixel in JBIG2's coordinates (rows from the top), in strips of rows.
        var instances = new List<(int Strip, int Row, int S, int Id, int Width)>();
        foreach (var blit in placements)
        {
            if (!ids.TryGetValue(blit.Shape, out var id))
                continue;
            var t = height - 1 - blit.Bottom;
            instances.Add((t >> LogStrips, t & ((1 << LogStrips) - 1), blit.Left, id, shapes[blit.Shape].Width));
        }
        instances.Sort((a, b) => a.Strip != b.Strip ? a.Strip.CompareTo(b.Strip) : a.S.CompareTo(b.S));

        var data = new List<byte>();
        data.AddRange(U32(width));
        data.AddRange(U32(height));
        data.AddRange(U32(0));
        data.AddRange(U32(0));
        data.Add(0); // OR onto the page
        // Arithmetic coding, no refinement, strips of 2^LogStrips rows, reference corner bottom left, OR, white.
        var flags = LogStrips << 2;
        data.AddRange([(byte)(flags >> 8), (byte)flags]);
        data.AddRange(U32(instances.Count));

        var coder = new MqEncoder();
        IntegerCoder deltaT = new(), firstS = new(), deltaS = new(), rowInStrip = new();
        var idCoder = new IdCoder(symbolCount);
        deltaT.Encode(coder, 0); // STRIPT starts at 0
        int stripT = 0, firsts = 0;
        for (var i = 0; i < instances.Count;)
        {
            if ((i & 0xFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var strip = instances[i].Strip;
            deltaT.Encode(coder, strip - stripT);
            stripT = strip;
            var curs = 0;
            for (var first = true; i < instances.Count && instances[i].Strip == strip; i++, first = false)
            {
                var (_, row, s, id, symbolWidth) = instances[i];
                if (first)
                {
                    firstS.Encode(coder, s - firsts);
                    firsts = s;
                }
                else
                {
                    deltaS.Encode(coder, s - curs);
                }
                rowInStrip.Encode(coder, row);
                idCoder.Encode(coder, id);
                curs = s + symbolWidth - 1;
            }
            deltaS.EncodeOutOfBand(coder);
        }
        data.AddRange(coder.Finish());
        return data;
    }

    /// <summary>T.88's integer arithmetic coder (Annex A.2) in reverse: a sign, a range prefix, then the offset bits.</summary>
    private sealed class IntegerCoder
    {
        private readonly byte[] _contexts = new byte[512];

        public void Encode(MqEncoder coder, int value)
        {
            var prev = 1;
            Bit(coder, value < 0 ? 1 : 0, ref prev);
            var v = Math.Abs((long)value);
            (int Prefix, int Bits, long Offset) range = v switch
            {
                <= 3 => (0, 2, 0),
                <= 19 => (1, 4, 4),
                <= 83 => (2, 6, 20),
                <= 339 => (3, 8, 84),
                <= 4435 => (4, 12, 340),
                _ => (5, 32, 4436),
            };
            for (var i = 0; i < range.Prefix; i++)
                Bit(coder, 1, ref prev);
            if (range.Prefix < 5)
                Bit(coder, 0, ref prev);
            var offset = v - range.Offset;
            for (var i = range.Bits - 1; i >= 0; i--)
                Bit(coder, (int)((offset >> i) & 1), ref prev);
        }

        /// <summary>OOB: a negative zero.</summary>
        public void EncodeOutOfBand(MqEncoder coder)
        {
            var prev = 1;
            Bit(coder, 1, ref prev);
            Bit(coder, 0, ref prev);
            Bit(coder, 0, ref prev);
            Bit(coder, 0, ref prev);
        }

        private void Bit(MqEncoder coder, int bit, ref int prev)
        {
            coder.Encode(_contexts, prev, bit);
            prev = prev < 256 ? (prev << 1) | bit : (((prev << 1) | bit) & 511) | 256;
        }
    }

    /// <summary>IAID: a symbol id in SBSYMCODELEN bits, each in the context of the bits before it.</summary>
    private sealed class IdCoder(int symbols)
    {
        /// <summary>SBSYMCODELEN: the bits that tell <c>symbols</c> ids apart (none for one).</summary>
        private readonly int _length = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)Math.Max(symbols - 1, 0));
        private byte[]? _contexts;

        public void Encode(MqEncoder coder, int id)
        {
            _contexts ??= new byte[1 << _length];
            var prev = 1;
            for (var i = _length - 1; i >= 0; i--)
            {
                var bit = (id >> i) & 1;
                coder.Encode(_contexts, prev, bit);
                prev = (prev << 1) | bit;
            }
        }
    }

    /// <summary>The MQ arithmetic encoder (T.88 Annex E.2); a context is a byte: its state index × 2 + its MPS.</summary>
    private sealed class MqEncoder
    {
        private static readonly ushort[] Qe =
        [
            0x5601, 0x3401, 0x1801, 0x0AC1, 0x0521, 0x0221, 0x5601, 0x5401, 0x4801, 0x3801, 0x3001, 0x2401, 0x1C01, 0x1601,
            0x5601, 0x5401, 0x5101, 0x4801, 0x3801, 0x3401, 0x3001, 0x2801, 0x2401, 0x2201, 0x1C01, 0x1801, 0x1601, 0x1401,
            0x1201, 0x1101, 0x0AC1, 0x09C1, 0x08A1, 0x0521, 0x0441, 0x02A1, 0x0221, 0x0141, 0x0111, 0x0085, 0x0049, 0x0025,
            0x0015, 0x0009, 0x0005, 0x0001, 0x5601,
        ];

        private static readonly byte[] NextMps =
        [
            1, 2, 3, 4, 5, 38, 7, 8, 9, 10, 11, 12, 13, 29, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31,
            32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 45, 46,
        ];

        private static readonly byte[] NextLps =
        [
            1, 6, 9, 12, 29, 33, 6, 14, 14, 14, 17, 18, 20, 21, 14, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 23, 24, 25, 26, 27,
            28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 46,
        ];

        private static readonly bool[] Switch = [true, false, false, false, false, false, true, false, false, false, false, false, false, false, true];

        private byte[] _out = new byte[4096];
        private int _bp; // _out[0] stands for the byte before the data (BPST − 1)
        private uint _c;
        private uint _a = 0x8000;
        private int _ct = 12;

        public void Encode(byte[] contexts, int cx, int bit)
        {
            var state = contexts[cx] >> 1;
            var mps = contexts[cx] & 1;
            uint qe = Qe[state];
            _a -= qe;
            if (bit == mps)
            {
                if ((_a & 0x8000) != 0)
                {
                    _c += qe;
                    return;
                }
                if (_a < qe)
                    _a = qe;
                else
                    _c += qe;
                contexts[cx] = (byte)((NextMps[state] << 1) | mps);
            }
            else
            {
                if (_a < qe)
                    _c += qe;
                else
                    _a = qe;
                if (state < Switch.Length && Switch[state])
                    mps = 1 - mps;
                contexts[cx] = (byte)((NextLps[state] << 1) | mps);
            }
            do
            {
                _a <<= 1;
                _c <<= 1;
                if (--_ct == 0)
                    ByteOut();
            }
            while ((_a & 0x8000) == 0);
        }

        private void ByteOut()
        {
            if (_out[_bp] == 0xFF)
            {
                Put((byte)(_c >> 20));
                _c &= 0xFFFFF;
                _ct = 7;
            }
            else if (_c < 0x8000000)
            {
                Put((byte)(_c >> 19));
                _c &= 0x7FFFF;
                _ct = 8;
            }
            else
            {
                _out[_bp]++;
                if (_out[_bp] == 0xFF)
                {
                    _c &= 0x7FFFFFF;
                    Put((byte)(_c >> 20));
                    _c &= 0xFFFFF;
                    _ct = 7;
                }
                else
                {
                    Put((byte)(_c >> 19));
                    _c &= 0x7FFFF;
                    _ct = 8;
                }
            }
        }

        private void Put(byte value)
        {
            if (++_bp == _out.Length)
                Array.Resize(ref _out, _out.Length * 2);
            _out[_bp] = value;
        }

        /// <summary>Flushes the coder and ends the data with the marker 0xFF 0xAC.</summary>
        public byte[] Finish()
        {
            var temp = _c + _a;
            _c |= 0xFFFF;
            if (_c >= temp)
                _c -= 0x8000;
            _c <<= _ct;
            ByteOut();
            _c <<= _ct;
            ByteOut();
            if (_out[_bp] != 0xFF)
                Put(0xFF);
            Put(0xAC);
            return _out[1..(_bp + 1)];
        }
    }
}
