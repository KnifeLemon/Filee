// JPEG 2000 codestreams built in code for Filee's decoder: one grey 8-bit component without wavelet levels (the
// coefficients are the samples less 128), coded losslessly as one code-block per tile with a tier-1 encoder of the
// test's own (T.800 Annex D, through the JBIG2 encoder's MQ coder) in any code-block style — bypass, context reset,
// termination on each pass, vertically causal contexts, segmentation symbols — and with tiles, two quality layers,
// SOP / EPH markers and packed packet headers (PPM, PPT). Also empty-packet codestreams of any geometry (tiles,
// precincts, levels, layers, progression order changes), which decode to mid-grey, for the structure and the limits.

using Filee.Engines.Djvu;

namespace Filee.Engines.Tests;

internal static class Jpeg2000TestFile
{
    internal const int Bypass = 0x01, Reset = 0x02, TermAll = 0x04, Causal = 0x08, Predictable = 0x10, Symbols = 0x20;

    /// <summary>How the coded codestream is laid out.</summary>
    internal sealed record Layout
    {
        public int Style { get; init; }
        public int TileWidth { get; init; } = 64;
        public int TileHeight { get; init; } = 64;

        /// <summary>Two quality layers (the passes split between them; needs <see cref="TermAll"/>).</summary>
        public bool TwoLayers { get; init; }
        public bool SopEph { get; init; }

        /// <summary>Packet headers in the main header (PPM) or in the tile headers (PPT).</summary>
        public bool Ppm { get; init; }
        public bool Ppt { get; init; }
    }

    /// <summary>A grey picture (a byte a pixel, rows top first) coded losslessly as <paramref name="layout"/> says.</summary>
    public static byte[] Grey(int width, int height, byte[] pixels, Layout layout)
    {
        if (layout.TileWidth > 64 || layout.TileHeight > 64 || layout.TileWidth * layout.TileHeight > 4096)
            throw new ArgumentException("A tile is one code-block of 4,096 samples at most.");
        var (tw, th) = (layout.TileWidth, layout.TileHeight);
        var across = (width + tw - 1) / tw;
        var down = (height + th - 1) / th;
        var layers = layout.TwoLayers ? 2 : 1;
        var w = new List<byte>();
        void U8(int v) => w.Add((byte)v);
        void U16(int v)
        {
            w.Add((byte)(v >> 8));
            w.Add((byte)v);
        }
        void U32(long v)
        {
            U16((int)(v >> 16));
            U16((int)(v & 0xFFFF));
        }
        U16(0xFF4F);
        U16(0xFF51); // SIZ
        U16(41);
        U16(0);
        U32(width);
        U32(height);
        U32(0);
        U32(0);
        U32(tw);
        U32(th);
        U32(0);
        U32(0);
        U16(1);
        U8(7);
        U8(1);
        U8(1);
        U16(0xFF52); // COD: LRCP, no wavelet levels, 64 × 64 blocks, reversible
        U16(12);
        U8(layout.SopEph ? 6 : 0);
        U8(0);
        U16(layers);
        U8(0);
        U8(0);
        U8(4);
        U8(4);
        U8(layout.Style);
        U8(1);
        U16(0xFF5C); // QCD: no quantization, two guard bits, exponent 8 (Mb = 9)
        U16(4);
        U8(2 << 5);
        U8(8 << 3);

        var tiles = new List<(byte[] Headers, byte[] Bodies)>();
        for (var t = 0; t < across * down; t++)
        {
            var (x0, y0) = (t % across * tw, t / across * th);
            var (bw, bh) = (Math.Min(tw, width - x0), Math.Min(th, height - y0));
            var coefficients = new int[bw * bh];
            for (var y = 0; y < bh; y++)
            {
                for (var x = 0; x < bw; x++)
                    coefficients[y * bw + x] = pixels[(y0 + y) * width + x0 + x] - 128;
            }
            tiles.Add(Packets(Block.Encode(coefficients, bw, bh, layout.Style), layers, layout.SopEph));
        }
        if (layout.Ppm)
        {
            U16(0xFF60);
            var all = tiles.Select(t => Concat(t.Headers)).SelectMany(h => (byte[])[(byte)(h.Length >> 24), (byte)(h.Length >> 16), (byte)(h.Length >> 8), (byte)h.Length, .. h]).ToArray();
            U16(3 + all.Length);
            U8(0);
            w.AddRange(all);
        }
        for (var t = 0; t < tiles.Count; t++)
        {
            var (packedHeaders, packedBodies) = tiles[t];
            var headers = Concat(packedHeaders);
            var ppt = layout.Ppt ? 5 + headers.Length : 0;
            var data = !layout.Ppm && !layout.Ppt ? Join(packedHeaders, packedBodies) : Concat(packedBodies);
            U16(0xFF90);
            U16(10);
            U16(t);
            U32(12 + ppt + 2 + data.Length);
            U8(0);
            U8(1);
            if (layout.Ppt)
            {
                U16(0xFF61);
                U16(3 + headers.Length);
                U8(0);
                w.AddRange(headers);
            }
            U16(0xFF93);
            w.AddRange(data);
        }
        U16(0xFFD9);
        return [.. w];
    }

    private static byte[] Concat(byte[] packed) => [.. Unpack(packed).SelectMany(p => p)];

    /// <summary>The packets of one tile's single code-block: their headers and bodies, joined in order by <see cref="Join"/>.</summary>
    private static (byte[] Headers, byte[] Bodies) Packets(Block.Coded coded, int layers, bool sopEph)
    {
        var headers = new List<byte[]>();
        var bodies = new List<byte[]>();
        var lblock = 3;
        var passesSoFar = 0;
        var firstLayerPasses = layers == 1 ? coded.Segments.Sum(s => s.Passes) : Math.Max(1, coded.Segments.Sum(s => s.Passes) / 2);
        var segmentIndex = 0;
        for (var layer = 0; layer < layers; layer++)
        {
            var bits = new BitWriter();
            var segments = new List<(int Passes, byte[] Data)>();
            var target = layer == layers - 1 ? coded.Segments.Sum(s => s.Passes) : firstLayerPasses;
            while (segmentIndex < coded.Segments.Count && passesSoFar + coded.Segments[segmentIndex].Passes <= target)
            {
                segments.Add(coded.Segments[segmentIndex]);
                passesSoFar += coded.Segments[segmentIndex].Passes;
                segmentIndex++;
            }
            var passes = segments.Sum(s => s.Passes);
            if (passes == 0)
            {
                bits.Bit(0);
            }
            else
            {
                bits.Bit(1);
                if (layer == 0 || passesSoFar == passes)
                {
                    // First inclusion: the inclusion tag tree (one leaf, value = this layer), then the missing bit-planes.
                    for (var i = 0; i < layer; i++)
                        bits.Bit(0);
                    bits.Bit(1);
                    for (var i = 0; i < coded.ZeroPlanes; i++)
                        bits.Bit(0);
                    bits.Bit(1);
                }
                else
                {
                    bits.Bit(1);
                }
                PassCount(bits, passes);
                // Lblock grows until every segment's length fits its bits.
                var needed = segments.Select(s => Bits(s.Data.Length) - Log2(s.Passes)).Prepend(lblock).Max();
                for (var i = lblock; i < needed; i++)
                    bits.Bit(1);
                bits.Bit(0);
                lblock = needed;
                foreach (var (segmentPasses, data) in segments)
                    bits.Bits(data.Length, lblock + Log2(segmentPasses));
            }
            var header = bits.Finish();
            if (sopEph)
                header = [.. header, 0xFF, 0x92];
            headers.Add(header);
            var body = segments.SelectMany(s => s.Data).ToArray();
            if (sopEph)
                body = [0xFF, 0x91, 0, 4, 0, (byte)layer, .. body];
            bodies.Add(body);
        }
        return (Pack(headers), Pack(bodies));
    }

    // Headers and bodies are kept as length-prefixed lists so Join can put them in packet order.
    private static byte[] Pack(List<byte[]> parts) => [.. parts.SelectMany(p => (byte[])[(byte)(p.Length >> 8), (byte)p.Length, .. p])];

    private static List<byte[]> Unpack(byte[] packed)
    {
        var parts = new List<byte[]>();
        for (var at = 0; at < packed.Length;)
        {
            var length = packed[at] << 8 | packed[at + 1];
            parts.Add(packed[(at + 2)..(at + 2 + length)]);
            at += 2 + length;
        }
        return parts;
    }

    private static byte[] Join(byte[] headers, byte[] bodies)
    {
        var (h, b) = (Unpack(headers), Unpack(bodies));
        var output = new List<byte>();
        for (var i = 0; i < h.Count; i++)
        {
            // SOP comes before the header, the rest of the body after it.
            var body = b[i];
            if (body is [0xFF, 0x91, ..])
            {
                output.AddRange(body[..6]);
                body = body[6..];
            }
            output.AddRange(h[i]);
            output.AddRange(body);
        }
        return [.. output];
    }

    private static int Bits(int value)
    {
        var bits = 1;
        while (value >> bits != 0)
            bits++;
        return bits;
    }

    private static int Log2(int value)
    {
        var log = 0;
        while (value > 1)
        {
            value >>= 1;
            log++;
        }
        return log;
    }

    private static void PassCount(BitWriter bits, int passes)
    {
        switch (passes)
        {
            case 1:
                bits.Bit(0);
                break;
            case 2:
                bits.Bits(0b10, 2);
                break;
            case <= 5:
                bits.Bits(0b1100 | (passes - 3), 4);
                break;
            case <= 36:
                bits.Bits(0b1111, 4);
                bits.Bits(passes - 6, 5);
                break;
            default:
                bits.Bits(0b1111, 4);
                bits.Bits(0b11111, 5);
                bits.Bits(passes - 37, 7);
                break;
        }
    }

    /// <summary>Packet header bits: after a 0xFF byte the next one holds seven.</summary>
    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _byte;
        private int _count;
        private int _capacity = 8;

        public void Bit(int bit)
        {
            _byte = _byte << 1 | bit;
            if (++_count == _capacity)
                Flush();
        }

        public void Bits(int value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
                Bit((value >> i) & 1);
        }

        private void Flush()
        {
            _bytes.Add((byte)_byte);
            _capacity = _byte == 0xFF ? 7 : 8;
            (_byte, _count) = (0, 0);
        }

        public byte[] Finish()
        {
            if (_count > 0)
            {
                _byte <<= _capacity - _count;
                _count = _capacity;
                Flush();
            }
            if (_bytes.Count > 0 && _bytes[^1] == 0xFF)
                _bytes.Add(0); // the stuffed byte after a last 0xFF
            return [.. _bytes];
        }
    }

    /// <summary>The test's own tier-1 encoder, the mirror of the decoder's passes.</summary>
    private sealed class Block
    {
        private const int SigW = 1, SigE = 2, SigN = 4, SigS = 8, SigNW = 16, SigNE = 32, SigSW = 64, SigSE = 128;
        private const int SignW = 256, SignE = 512, SignN = 1024, SignS = 2048;
        private const int Significant = 4096, Negative = 8192, Visited = 16384, Refined = 32768;
        private const int CausalMask = ~(SigS | SigSW | SigSE | SignS);

        internal sealed record Coded(int ZeroPlanes, List<(int Passes, byte[] Data)> Segments);

        private readonly int _w, _h, _stride, _style;
        private readonly int[] _magnitudes;
        private readonly int[] _flags;
        private readonly byte[] _contexts = new byte[19];
        private Jbig2Encoder.MqEncoder _mq = new();
        private readonly List<byte> _raw = [];
        private int _rawByte, _rawCount, _rawCapacity = 8;
        private bool _rawMode;

        private Block(int[] coefficients, int w, int h, int style)
        {
            (_w, _h, _stride, _style) = (w, h, w + 2, style);
            _magnitudes = [.. coefficients.Select(Math.Abs)];
            _flags = new int[(w + 2) * (h + 2)];
            for (var i = 0; i < coefficients.Length; i++)
            {
                if (coefficients[i] < 0)
                    _flags[(i / w + 1) * _stride + i % w + 1] |= Negative;
            }
            ResetContexts();
        }

        private void ResetContexts()
        {
            Array.Clear(_contexts);
            _contexts[0] = 4 << 1;
            _contexts[17] = 3 << 1;
            _contexts[18] = 46 << 1;
        }

        public static Coded Encode(int[] coefficients, int w, int h, int style)
        {
            var block = new Block(coefficients, w, h, style);
            var top = block._magnitudes.Length == 0 ? 0 : block._magnitudes.Max();
            var planes = 0;
            while (top >> planes != 0)
                planes++;
            var segments = new List<(int, byte[])>();
            if (planes == 0)
                return new Coded(9, segments);
            var passes = 3 * planes - 2;
            var start = 0;
            for (var pass = 0; pass < passes; pass++)
            {
                var type = pass == 0 ? 2 : (pass - 1) % 3;
                var raw = (style & Bypass) != 0 && pass >= 10 && type != 2;
                if (pass == start)
                    block.Begin(raw);
                var plane = planes - 1 - (pass + 2) / 3;
                switch (type)
                {
                    case 0:
                        block.Significance(plane, raw);
                        break;
                    case 1:
                        block.Refinement(plane, raw);
                        break;
                    default:
                        block.Cleanup(plane);
                        break;
                }
                if ((style & Reset) != 0)
                    block.ResetContexts();
                var ends = pass == passes - 1 || (style & TermAll) != 0 || ((style & Bypass) != 0 && pass >= 9 && (pass % 3 != 1));
                if (ends)
                {
                    segments.Add((pass - start + 1, block.End()));
                    start = pass + 1;
                }
            }
            return new Coded(9 - planes, segments);
        }

        private void Begin(bool raw)
        {
            _rawMode = raw;
            _mq = new Jbig2Encoder.MqEncoder();
            _raw.Clear();
            (_rawByte, _rawCount, _rawCapacity) = (0, 0, 8);
        }

        private byte[] End()
        {
            if (_rawMode)
            {
                if (_rawCount > 0)
                {
                    // Pad with alternating bits, as D.6 suggests; the decoder reads no further.
                    for (var i = 0; _rawCount != 0; i++)
                        RawBit(i & 1);
                }
                return [.. _raw];
            }
            var data = _mq.Finish();
            // The JBIG2 coder ends with the marker 0xFF 0xAC; JPEG 2000 has none (past its end the decoder reads 0xFF).
            var length = data.Length - 2;
            while (length > 0 && data[length - 1] == 0xFF)
                length--;
            return data[..length];
        }

        private void RawBit(int bit)
        {
            _rawByte = _rawByte << 1 | bit;
            if (++_rawCount == _rawCapacity)
            {
                _raw.Add((byte)_rawByte);
                _rawCapacity = _rawByte == 0xFF ? 7 : 8;
                (_rawByte, _rawCount) = (0, 0);
            }
        }

        private void Code(int context, int bit)
        {
            if (_rawMode)
                RawBit(bit);
            else
                _mq.Encode(_contexts, context, bit);
        }

        private int Flags(int x, int y)
        {
            var f = _flags[(y + 1) * _stride + x + 1];
            return (_style & Causal) != 0 && y % 4 == 3 ? f & CausalMask : f;
        }

        private static int Zero(int f)
        {
            var h = ((f & SigW) != 0 ? 1 : 0) + ((f & SigE) != 0 ? 1 : 0);
            var v = ((f & SigN) != 0 ? 1 : 0) + ((f & SigS) != 0 ? 1 : 0);
            var d = ((f & SigNW) != 0 ? 1 : 0) + ((f & SigNE) != 0 ? 1 : 0) + ((f & SigSW) != 0 ? 1 : 0) + ((f & SigSE) != 0 ? 1 : 0);
            return h == 2 ? 8 : h == 1 ? (v >= 1 ? 7 : d >= 1 ? 6 : 5) : v == 2 ? 4 : v == 1 ? 3 : d >= 2 ? 2 : d == 1 ? 1 : 0; // LL
        }

        private void Sign(int x, int y, int f)
        {
            var negative = (_flags[(y + 1) * _stride + x + 1] & Negative) != 0 ? 1 : 0;
            if (_rawMode)
            {
                RawBit(negative);
                return;
            }
            int Contribution(int sig, int neg) => (f & sig) == 0 ? 0 : (f & neg) != 0 ? -1 : 1;
            var h = Math.Clamp(Contribution(SigW, SignW) + Contribution(SigE, SignE), -1, 1);
            var v = Math.Clamp(Contribution(SigN, SignN) + Contribution(SigS, SignS), -1, 1);
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
            _mq.Encode(_contexts, context, negative ^ xor);
        }

        private void MakeSignificant(int x, int y)
        {
            var i = (y + 1) * _stride + x + 1;
            var negative = (_flags[i] & Negative) != 0;
            _flags[i] |= Significant;
            _flags[i - 1] |= SigE | (negative ? SignE : 0);
            _flags[i + 1] |= SigW | (negative ? SignW : 0);
            _flags[i - _stride] |= SigS | (negative ? SignS : 0);
            _flags[i + _stride] |= SigN | (negative ? SignN : 0);
            _flags[i - _stride - 1] |= SigSE;
            _flags[i - _stride + 1] |= SigSW;
            _flags[i + _stride - 1] |= SigNE;
            _flags[i + _stride + 1] |= SigNW;
        }

        private int BitAt(int x, int y, int plane) => (_magnitudes[y * _w + x] >> plane) & 1;

        private void Significance(int plane, bool raw)
        {
            for (var y0 = 0; y0 < _h; y0 += 4)
            {
                for (var x = 0; x < _w; x++)
                {
                    for (var y = y0; y < Math.Min(y0 + 4, _h); y++)
                    {
                        var f = Flags(x, y);
                        if ((f & (Significant | Visited)) != 0 || (f & 0xFF) == 0)
                            continue;
                        var bit = BitAt(x, y, plane);
                        Code(Zero(f & 0xFF), bit);
                        if (bit == 1)
                        {
                            Sign(x, y, f);
                            MakeSignificant(x, y);
                        }
                        _flags[(y + 1) * _stride + x + 1] |= Visited;
                    }
                }
            }
            _ = raw;
        }

        private void Refinement(int plane, bool raw)
        {
            for (var y0 = 0; y0 < _h; y0 += 4)
            {
                for (var x = 0; x < _w; x++)
                {
                    for (var y = y0; y < Math.Min(y0 + 4, _h); y++)
                    {
                        var f = Flags(x, y);
                        if ((f & (Significant | Visited)) != Significant)
                            continue;
                        var context = (f & Refined) != 0 ? 16 : (f & 0xFF) != 0 ? 15 : 14;
                        Code(context, BitAt(x, y, plane));
                        _flags[(y + 1) * _stride + x + 1] |= Refined;
                    }
                }
            }
            _ = raw;
        }

        private void Cleanup(int plane)
        {
            for (var y0 = 0; y0 < _h; y0 += 4)
            {
                for (var x = 0; x < _w; x++)
                {
                    var y = y0;
                    if (y0 + 4 <= _h && Enumerable.Range(y0, 4).All(k => (Flags(x, k) & (Significant | Visited | 0xFF)) == 0))
                    {
                        var first = Enumerable.Range(y0, 4).FirstOrDefault(k => BitAt(x, k, plane) == 1, -1);
                        Code(17, first >= 0 ? 1 : 0);
                        if (first < 0)
                            continue;
                        Code(18, (first - y0) >> 1);
                        Code(18, (first - y0) & 1);
                        Sign(x, first, Flags(x, first));
                        MakeSignificant(x, first);
                        y = first + 1;
                    }
                    for (; y < Math.Min(y0 + 4, _h); y++)
                    {
                        var f = Flags(x, y);
                        if ((f & (Significant | Visited)) != 0)
                            continue;
                        var bit = BitAt(x, y, plane);
                        Code(Zero(f & 0xFF), bit);
                        if (bit == 1)
                        {
                            Sign(x, y, f);
                            MakeSignificant(x, y);
                        }
                    }
                    for (var k = y0; k < Math.Min(y0 + 4, _h); k++)
                        _flags[(k + 1) * _stride + x + 1] &= ~Visited;
                }
            }
            if ((_style & Symbols) != 0)
            {
                foreach (var bit in (int[])[1, 0, 1, 0])
                    Code(18, bit);
            }
        }
    }

    // ───────────────────────── Empty-packet codestreams ─────────────────────────

    /// <summary>The geometry of an empty-packet codestream.</summary>
    internal sealed record Empty
    {
        public long Width { get; init; } = 64;
        public long Height { get; init; } = 64;
        public long TileWidth { get; init; } = 64;
        public long TileHeight { get; init; } = 64;
        public int Components { get; init; } = 1;
        public int Levels { get; init; }
        public int Layers { get; init; } = 1;

        /// <summary>Code-block exponents (2–10 each).</summary>
        public int BlockWidth { get; init; } = 6;
        public int BlockHeight { get; init; } = 6;

        /// <summary>Precinct exponents for every resolution; null: the largest.</summary>
        public int? Precinct { get; init; }

        public int Order { get; init; }

        /// <summary>Progression order changes: each (resolution start, component start, layer end, resolution end, component end, order).</summary>
        public (int, int, int, int, int, int)[] Changes { get; init; } = [];

        /// <summary>Tile-parts to write (the first ones of the grid); null: every tile.</summary>
        public int? TilesWritten { get; init; }

        /// <summary>Bytes of each tile-part's data (a zero byte is an empty packet).</summary>
        public int PacketBytes { get; init; } = 1;

        /// <summary>The guard bits and the exponent of the quantization.</summary>
        public int Guard { get; init; } = 2;
        public int Exponent { get; init; } = 8;
    }

    /// <summary>A codestream every packet of which is empty (or as many zero bytes as <see cref="Empty.PacketBytes"/>).</summary>
    public static byte[] EmptyPackets(Empty e)
    {
        var w = new List<byte>();
        void U8(int v) => w.Add((byte)v);
        void U16(int v)
        {
            w.Add((byte)(v >> 8));
            w.Add((byte)v);
        }
        void U32(long v)
        {
            U16((int)((v >> 16) & 0xFFFF));
            U16((int)(v & 0xFFFF));
        }
        U16(0xFF4F);
        U16(0xFF51);
        U16(38 + 3 * e.Components);
        U16(0);
        U32(e.Width);
        U32(e.Height);
        U32(0);
        U32(0);
        U32(e.TileWidth);
        U32(e.TileHeight);
        U32(0);
        U32(0);
        U16(e.Components);
        for (var c = 0; c < e.Components; c++)
        {
            U8(7);
            U8(1);
            U8(1);
        }
        U16(0xFF52);
        U16(12 + (e.Precinct is null ? 0 : e.Levels + 1));
        U8(e.Precinct is null ? 0 : 1);
        U8(e.Order);
        U16(e.Layers);
        U8(0);
        U8(e.Levels);
        U8(e.BlockWidth - 2);
        U8(e.BlockHeight - 2);
        U8(0);
        U8(1);
        if (e.Precinct is { } p)
        {
            for (var r = 0; r <= e.Levels; r++)
                U8(p << 4 | p);
        }
        U16(0xFF5C);
        U16(3 + 3 * e.Levels + 1);
        U8(e.Guard << 5);
        for (var b = 0; b < 3 * e.Levels + 1; b++)
            U8(e.Exponent << 3);
        if (e.Changes.Length > 0)
        {
            U16(0xFF5F);
            U16(2 + 7 * e.Changes.Length);
            foreach (var (rs, cs, lye, re, ce, order) in e.Changes)
            {
                U8(rs);
                U8(cs);
                U16(lye);
                U8(re);
                U8(ce);
                U8(order);
            }
        }
        var tiles = (long)((e.Width + e.TileWidth - 1) / e.TileWidth) * ((e.Height + e.TileHeight - 1) / e.TileHeight);
        for (var t = 0; t < Math.Min(tiles, e.TilesWritten ?? tiles); t++)
        {
            U16(0xFF90);
            U16(10);
            U16((int)t);
            U32(14 + e.PacketBytes);
            U8(0);
            U8(1);
            U16(0xFF93);
            w.AddRange(new byte[e.PacketBytes]);
        }
        U16(0xFFD9);
        return [.. w];
    }
}
