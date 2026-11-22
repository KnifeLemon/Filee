// JPEG 2000 tier-1 decoding (T.800 Annex D): a code-block's bit-planes from its coding passes — significance
// propagation, magnitude refinement and cleanup (with its run-length mode) — through the MQ arithmetic decoder of
// Annex C (the same coder as JBIG2's, its states from Djvu.MqTables), with the code-block styles of Table A.19:
// selective arithmetic coding bypass (raw significance and refinement passes after the first ten), context reset
// after each pass, termination on each pass, vertically causal contexts, predictable termination (nothing to do when
// decoding) and segmentation symbols.
//
// Each coefficient is kept as twice its magnitude so that half of the lowest decoded plane can be added (the
// reconstruction point of T.800 E.1.1.2 at r = 1/2): a coefficient that becomes significant in plane p is
// 3 × 2^p, a refinement adds or takes away 2^p. Neighbour significance and signs are kept in each sample's flags
// and updated when a neighbour becomes significant, so a context is one table lookup.

using System.Runtime.CompilerServices;
using Filee.Engines.Djvu;

namespace Filee.Engines.Jpeg2000;

internal sealed class Jpeg2000Blocks
{
    // A sample's flags: the significance of its eight neighbours, the signs of the four next to it, its own state.
    private const int SigW = 1, SigE = 2, SigN = 4, SigS = 8, SigNW = 16, SigNE = 32, SigSW = 64, SigSE = 128;
    private const int SignW = 256, SignE = 512, SignN = 1024, SignS = 2048;
    private const int Significant = 4096, Negative = 8192, Visited = 16384, Refined = 32768;
    private const int Neighbours = 0xFF;

    /// <summary>What a sample in the last row of a stripe doesn't see under vertically causal contexts.</summary>
    private const int Causal = ~(SigS | SigSW | SigSE | SignS);

    private const int RunContext = 17, UniformContext = 18;

    private const int StyleBypass = 0x01, StyleReset = 0x02, StyleCausal = 0x08, StyleSymbols = 0x20;

    /// <summary>Zero coding contexts by orientation (LL, HL, LH, HH) and neighbour significance (Table D.1).</summary>
    private static readonly byte[][] ZeroContexts = [.. Enumerable.Range(0, 4).Select(ZeroTable)];

    /// <summary>Sign coding context × 2 + the bit to xor, by the four neighbours' significance and signs (Table D.3).</summary>
    private static readonly byte[] SignContexts = SignTable();

    /// <summary>Each context's state × 2 + its MPS (an index into the tables below).</summary>
    private readonly byte[] _contexts = new byte[19];

    // The MQ states with their MPS (index state × 2 + MPS): Qe, the next index after an MPS and after an LPS.
    private static readonly uint[] StateQe = [.. Enumerable.Range(0, 94).Select(i => (uint)MqTables.Qe[i >> 1])];
    private static readonly byte[] StateMps = [.. Enumerable.Range(0, 94).Select(i => (byte)(MqTables.NextMps[i >> 1] << 1 | (i & 1)))];
    private static readonly byte[] StateLps = [.. Enumerable.Range(0, 94).Select(i => (byte)(MqTables.NextLps[i >> 1] << 1 | (MqTables.Switch[i >> 1] ? 1 - (i & 1) : i & 1)))];
    private int[] _flags = [];
    private int[] _values = [];
    private int _stride;

    // The MQ decoder (C.3) or the raw reader of a bypassed pass, over one segment's bytes.
    private byte[] _data = [];
    private int _end;
    private int _bp;
    private uint _c;
    private uint _a;
    private int _ct;
    private int _rawByte;
    private int _rawLeft;
    private bool _rawAfterFF;

    private static byte[] ZeroTable(int orientation)
    {
        var table = new byte[256];
        for (var f = 0; f < 256; f++)
        {
            var h = ((f & SigW) != 0 ? 1 : 0) + ((f & SigE) != 0 ? 1 : 0);
            var v = ((f & SigN) != 0 ? 1 : 0) + ((f & SigS) != 0 ? 1 : 0);
            var d = ((f & SigNW) != 0 ? 1 : 0) + ((f & SigNE) != 0 ? 1 : 0) + ((f & SigSW) != 0 ? 1 : 0) + ((f & SigSE) != 0 ? 1 : 0);
            if (orientation == 1)
                (h, v) = (v, h); // HL: horizontally high-pass, the vertical neighbours count first
            int context;
            if (orientation == 3)
            {
                var hv = h + v;
                context = d >= 3 ? 8
                    : d == 2 ? (hv >= 1 ? 7 : 6)
                    : d == 1 ? (hv >= 2 ? 5 : hv == 1 ? 4 : 3)
                    : hv >= 2 ? 2 : hv == 1 ? 1 : 0;
            }
            else
            {
                context = h == 2 ? 8
                    : h == 1 ? (v >= 1 ? 7 : d >= 1 ? 6 : 5)
                    : v == 2 ? 4 : v == 1 ? 3 : d >= 2 ? 2 : d == 1 ? 1 : 0;
            }
            table[f] = (byte)context;
        }
        return table;
    }

    private static byte[] SignTable()
    {
        var table = new byte[256];
        for (var f = 0; f < 256; f++)
        {
            // Bits 0–3: W, E, N, S significant; bits 4–7: their signs (negative).
            int Contribution(int sig, int neg) => (f & sig) == 0 ? 0 : (f & neg) != 0 ? -1 : 1;
            var h = Math.Clamp(Contribution(1, 16) + Contribution(2, 32), -1, 1);
            var v = Math.Clamp(Contribution(4, 64) + Contribution(8, 128), -1, 1);
            var (context, xor) = (h, v) switch
            {
                (1, 1) => (13, 0),
                (1, 0) => (12, 0),
                (1, -1) => (11, 0),
                (0, 1) => (10, 0),
                (0, 0) => (9, 0),
                (0, -1) => (10, 1),
                (-1, 1) => (11, 1),
                (-1, 0) => (12, 1),
                _ => (13, 1),
            };
            table[f] = (byte)(context << 1 | xor);
        }
        return table;
    }

    private void ResetContexts()
    {
        Array.Clear(_contexts);
        _contexts[0] = 4 << 1;
        _contexts[RunContext] = 3 << 1;
        _contexts[UniformContext] = 46 << 1;
    }

    /// <summary>
    /// The code-block's coefficients (w × h, rows top first) as twice their magnitude with their sign, from its
    /// segments in <paramref name="body"/>; <paramref name="planes"/> bit-planes are coded (Mb less the missing ones).
    /// </summary>
    internal int[] Decode(byte[] body, Jpeg2000Decoder.CodeBlock block, int w, int h, int orientation, int style, int planes)
    {
        _stride = w + 2;
        var size = _stride * (h + 2);
        if (_flags.Length < size)
            _flags = new int[size];
        else
            Array.Clear(_flags, 0, size);
        var values = new int[w * h];
        _values = values;
        ResetContexts();
        var zero = ZeroContexts[orientation];
        var causal = (style & StyleCausal) != 0;
        var bypass = (style & StyleBypass) != 0;
        var passes = Math.Min(block.Passes, 3 * planes - 2);
        var segment = 0;
        byte[]? joined = null;
        for (var pass = 0; pass < passes; pass++)
        {
            // A segment starts here: the decoder (or the raw reader) takes its bytes.
            while (segment < block.Segments.Count && block.Segments[segment].Start + block.Segments[segment].Passes <= pass)
                segment++;
            if (segment >= block.Segments.Count)
                break;
            var current = block.Segments[segment];
            var type = pass == 0 ? 2 : (pass - 1) % 3; // 0 significance, 1 refinement, 2 cleanup
            var raw = bypass && pass >= 10 && type != 2;
            if (pass == current.Start)
            {
                (var data, var start, var length) = Bytes(body, current, ref joined);
                if (raw)
                    StartRaw(data, start, start + length);
                else
                    StartMq(data, start, start + length);
            }
            var plane = planes - 1 - (pass + 2) / 3;
            var one = 1 << (plane + 1);
            switch (type)
            {
                case 0:
                    Significance(w, h, zero, causal, raw, one | one >> 1);
                    break;
                case 1:
                    Refinement(w, h, causal, raw, one >> 1);
                    break;
                default:
                    Cleanup(w, h, zero, causal, one | one >> 1, (style & StyleSymbols) != 0);
                    break;
            }
            if ((style & StyleReset) != 0)
                ResetContexts();
        }
        // Signs into the values.
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if ((_flags[(y + 1) * _stride + x + 1] & Negative) != 0)
                    values[y * w + x] = -values[y * w + x];
            }
        }
        return values;
    }

    /// <summary>A segment's bytes: in the tile's data when they lie in one piece, else joined.</summary>
    private static (byte[] Data, int Start, int Length) Bytes(byte[] body, Jpeg2000Decoder.Segment segment, ref byte[]? joined)
    {
        if (segment.Pieces.Count == 0)
            return (body, 0, 0);
        if (segment.Pieces.Count == 1)
            return (body, segment.Pieces[0].Offset, segment.Pieces[0].Length);
        var total = segment.Pieces.Sum(p => p.Length);
        joined = new byte[total];
        var at = 0;
        foreach (var (offset, length) in segment.Pieces)
        {
            Array.Copy(body, offset, joined, at, length);
            at += length;
        }
        return (joined, 0, total);
    }

    private void Significant1(int index, bool negative)
    {
        var flags = _flags;
        var s = _stride;
        flags[index] |= Significant | (negative ? Negative : 0);
        flags[index - 1] |= SigE | (negative ? SignE : 0);
        flags[index + 1] |= SigW | (negative ? SignW : 0);
        flags[index - s] |= SigS | (negative ? SignS : 0);
        flags[index + s] |= SigN | (negative ? SignN : 0);
        flags[index - s - 1] |= SigSE;
        flags[index - s + 1] |= SigSW;
        flags[index + s - 1] |= SigNE;
        flags[index + s + 1] |= SigNW;
    }

    private bool Sign(int f, bool raw)
    {
        if (raw)
            return RawBit() == 1;
        var entry = SignContexts[(f & 0x0F) | ((f >> 4) & 0xF0)];
        return (MqDecode(entry >> 1) ^ (entry & 1)) == 1;
    }

    private void Significance(int w, int h, byte[] zero, bool causal, bool raw, int value)
    {
        var flags = _flags;
        var stride = _stride;
        var values = _values;
        for (var y0 = 0; y0 < h; y0 += 4)
        {
            var rows = Math.Min(4, h - y0);
            for (var x = 0; x < w; x++)
            {
                for (var dy = 0; dy < rows; dy++)
                {
                    var y = y0 + dy;
                    var index = (y + 1) * stride + x + 1;
                    var f = flags[index];
                    if (causal && dy == 3)
                        f &= Causal;
                    if ((f & (Significant | Visited)) != 0 || (f & Neighbours) == 0)
                        continue;
                    var bit = raw ? RawBit() : MqDecode(zero[f & Neighbours]);
                    if (bit == 1)
                    {
                        var negative = Sign(f, raw);
                        values[y * w + x] = value;
                        Significant1(index, negative);
                    }
                    flags[index] |= Visited;
                }
            }
        }
    }

    private void Refinement(int w, int h, bool causal, bool raw, int half)
    {
        var flags = _flags;
        var stride = _stride;
        var values = _values;
        for (var y0 = 0; y0 < h; y0 += 4)
        {
            var rows = Math.Min(4, h - y0);
            for (var x = 0; x < w; x++)
            {
                for (var dy = 0; dy < rows; dy++)
                {
                    var y = y0 + dy;
                    var index = (y + 1) * stride + x + 1;
                    var f = flags[index];
                    if ((f & (Significant | Visited)) != Significant)
                        continue;
                    if (causal && dy == 3)
                        f &= Causal;
                    var context = (f & Refined) != 0 ? 16 : (f & Neighbours) != 0 ? 15 : 14;
                    var bit = raw ? RawBit() : MqDecode(context);
                    values[y * w + x] += bit == 1 ? half : -half;
                    flags[index] |= Refined;
                }
            }
        }
    }

    private void Cleanup(int w, int h, byte[] zero, bool causal, int value, bool symbols)
    {
        var flags = _flags;
        var stride = _stride;
        var values = _values;
        for (var y0 = 0; y0 < h; y0 += 4)
        {
            var rows = Math.Min(4, h - y0);
            for (var x = 0; x < w; x++)
            {
                var top = (y0 + 1) * stride + x + 1;
                var dy = 0;
                if (rows == 4)
                {
                    // Run-length mode: four samples not yet significant, not visited, with no significant neighbour.
                    var quiet = true;
                    for (var k = 0; k < 4 && quiet; k++)
                    {
                        var f = flags[top + k * stride];
                        if (causal && k == 3)
                            f &= Causal;
                        quiet = (f & (Significant | Visited | Neighbours)) == 0;
                    }
                    if (quiet)
                    {
                        if (MqDecode(RunContext) == 0)
                            continue; // all four stay insignificant
                        dy = MqDecode(UniformContext) << 1 | MqDecode(UniformContext);
                        var index = top + dy * stride;
                        var f = flags[index];
                        if (causal && dy == 3)
                            f &= Causal;
                        var negative = Sign(f, raw: false);
                        values[(y0 + dy) * w + x] = value;
                        Significant1(index, negative);
                        dy++;
                    }
                }
                for (; dy < rows; dy++)
                {
                    var index = top + dy * stride;
                    var f = flags[index];
                    if (causal && dy == 3)
                        f &= Causal;
                    if ((f & (Significant | Visited)) == 0 && MqDecode(zero[f & Neighbours]) == 1)
                    {
                        var negative = Sign(f, raw: false);
                        values[(y0 + dy) * w + x] = value;
                        Significant1(index, negative);
                    }
                }
                for (var k = 0; k < rows; k++)
                    flags[top + k * stride] &= ~Visited;
            }
        }
        if (symbols)
        {
            // The segmentation symbol 1010: read and let be (it only tells a damaged block).
            for (var i = 0; i < 4; i++)
                MqDecode(UniformContext);
        }
    }

    // ───────────────────────── MQ decoder (C.3) ─────────────────────────

    private int Byte(int at) => at < _end ? _data[at] : 0xFF;

    private void StartMq(byte[] data, int start, int end)
    {
        _data = data;
        _end = end;
        _bp = start;
        _c = (uint)Byte(_bp) << 16;
        ByteIn();
        _c <<= 7;
        _ct -= 7;
        _a = 0x8000;
    }

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int MqDecode(int cx)
    {
        int index = _contexts[cx];
        var qe = StateQe[index];
        int d;
        var a = _a - qe;
        var c = _c;
        if ((c >> 16) < qe)
        {
            // LPS exchange.
            if (a < qe)
            {
                d = index & 1;
                _contexts[cx] = StateMps[index];
            }
            else
            {
                d = 1 - (index & 1);
                _contexts[cx] = StateLps[index];
            }
            a = qe;
        }
        else
        {
            c -= qe << 16;
            if ((a & 0x8000) != 0)
            {
                _a = a;
                _c = c;
                return index & 1;
            }
            // MPS exchange.
            if (a < qe)
            {
                d = 1 - (index & 1);
                _contexts[cx] = StateLps[index];
            }
            else
            {
                d = index & 1;
                _contexts[cx] = StateMps[index];
            }
        }
        _a = a;
        _c = c;
        Renormalize();
        return d;
    }

    private void Renormalize()
    {
        var a = _a;
        var c = _c;
        var ct = _ct;
        do
        {
            if (ct == 0)
            {
                _c = c;
                ByteIn();
                c = _c;
                ct = _ct;
            }
            a <<= 1;
            c <<= 1;
            ct--;
        }
        while ((a & 0x8000) == 0);
        _a = a;
        _c = c;
        _ct = ct;
    }

    // ───────────────────────── Raw passes (D.6) ─────────────────────────

    private void StartRaw(byte[] data, int start, int end)
    {
        _data = data;
        _end = end;
        _bp = start;
        _rawLeft = 0;
        _rawAfterFF = false;
    }

    private int RawBit()
    {
        if (_rawLeft == 0)
        {
            var b = Byte(_bp++);
            _rawLeft = _rawAfterFF ? 7 : 8;
            _rawAfterFF = b == 0xFF;
            _rawByte = b;
        }
        _rawLeft--;
        return (_rawByte >> _rawLeft) & 1;
    }
}
