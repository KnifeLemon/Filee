// One JPEG 2000 tile (Jpeg2000Decoder): its tile-components, their resolution levels, sub-bands, precincts and
// code-blocks (Annex B), the packets in the order of the tile's progression and its progression order changes, the
// packet headers (tag trees, coding passes, code-word segment lengths) and bodies, and then the code-blocks decoded
// (Jpeg2000Blocks), dequantized and transformed back (Jpeg2000Wavelet) into the picture's components.

using Filee.Engines.Documents;

namespace Filee.Engines.Jpeg2000;

internal static partial class Jpeg2000Decoder
{
    /// <summary>Bit-planes a coefficient may have at most (its value and half a step must fit in 31 bits).</summary>
    internal const int MaxPlanes = 30;

    private sealed class TileComponent
    {
        public int X0, Y0, X1, Y1;
        public required CodingStyle Style;
        public required Resolution[] Resolutions;
        public int Precision;
        public bool Signed;
        public int Roi;
    }

    private sealed class Resolution
    {
        public int X0, Y0, X1, Y1;
        public int PrecinctWidth, PrecinctHeight;
        public int PrecinctsWide, PrecinctsHigh;
        public int BlockWidth, BlockHeight;
        public required Band[] Bands;
        public required Precinct?[] Precincts;
    }

    private sealed class Band
    {
        /// <summary>0 LL, 1 HL, 2 LH, 3 HH.</summary>
        public int Orientation;
        public int X0, Y0, X1, Y1;

        /// <summary>Mb: magnitude bit-planes of its coefficients (guard bits, exponent, ROI shift).</summary>
        public int Planes;
        public float Step;
        public int[]? Ints;
        public float[]? Floats;

        public int Width => X1 - X0;
        public int Height => Y1 - Y0;
    }

    private sealed class Precinct(PrecinctBand[] bands)
    {
        public PrecinctBand[] Bands { get; } = bands;
    }

    private sealed class PrecinctBand(Band band, CodeBlock[] blocks, int wide, int high)
    {
        public Band Band { get; } = band;
        public CodeBlock[] Blocks { get; } = blocks;
        public TagTree Inclusion { get; } = new(wide, high);
        public TagTree ZeroPlanes { get; } = new(wide, high);
    }

    internal sealed class CodeBlock(int x0, int y0, int x1, int y1)
    {
        public int X0 { get; } = x0;
        public int Y0 { get; } = y0;
        public int X1 { get; } = x1;
        public int Y1 { get; } = y1;
        public bool Included;
        public int Lblock = 3;
        public int ZeroPlanes;

        /// <summary>Coding passes the packets so far brought.</summary>
        public int Passes;
        public int SegmentStart;
        public int SegmentPasses;
        public List<Segment> Segments { get; } = [];
    }

    /// <summary>A code-word segment: the passes it codes and where its bytes lie in the tile's data (one piece a layer).</summary>
    internal sealed class Segment(int start)
    {
        public int Start { get; } = start;
        public int Passes;
        public List<(int Offset, int Length)> Pieces { get; } = [];
    }

    /// <summary>A tag tree (B.10.2): a value per leaf, coded from the root down against rising thresholds.</summary>
    private sealed class TagTree
    {
        private readonly int[] _lower;
        private readonly bool[] _known;
        private readonly int[] _parent;

        public TagTree(int width, int height)
        {
            var sizes = new List<(int W, int H)>();
            var (w, h) = (Math.Max(width, 1), Math.Max(height, 1));
            while (true)
            {
                sizes.Add((w, h));
                if (w == 1 && h == 1)
                    break;
                (w, h) = ((w + 1) / 2, (h + 1) / 2);
            }
            var total = sizes.Sum(s => s.W * s.H);
            _lower = new int[total];
            _known = new bool[total];
            _parent = new int[total];
            var offset = 0;
            for (var level = 0; level < sizes.Count; level++)
            {
                var (lw, lh) = sizes[level];
                var next = offset + lw * lh;
                for (var y = 0; y < lh; y++)
                {
                    for (var x = 0; x < lw; x++)
                        _parent[offset + y * lw + x] = level + 1 < sizes.Count ? next + (y / 2) * sizes[level + 1].W + x / 2 : -1;
                }
                offset = next;
            }
        }

        /// <summary>Reads what the threshold asks; whether the leaf's value is below it.</summary>
        public bool Decode(int leaf, int threshold, Bits bits)
        {
            Span<int> path = stackalloc int[40];
            var n = 0;
            for (var node = leaf; node >= 0 && n < path.Length; node = _parent[node])
                path[n++] = node;
            var low = 0;
            for (var i = n - 1; i >= 0; i--)
            {
                var node = path[i];
                if (_lower[node] < low)
                    _lower[node] = low;
                while (!_known[node] && _lower[node] < threshold)
                {
                    if (bits.Read() == 1)
                        _known[node] = true;
                    else
                        _lower[node]++;
                    if (bits.Overrun)
                        return false;
                }
                low = _lower[node];
            }
            return _known[leaf] && _lower[leaf] < threshold;
        }

        public int Value(int leaf) => _lower[leaf];
    }

    /// <summary>Packet header bits (B.10.1): after a 0xFF byte only seven bits of the next count.</summary>
    internal sealed class Bits(byte[] data, int position, int end)
    {
        private int _position = position;
        private int _byte;
        private int _left;
        private bool _afterFF;

        /// <summary>Bits were asked for past the end.</summary>
        public bool Overrun { get; private set; }

        public int Read()
        {
            if (_left == 0)
            {
                int b;
                if (_position < end)
                {
                    b = data[_position++];
                }
                else
                {
                    b = 0;
                    Overrun = true;
                }
                _left = _afterFF ? 7 : 8;
                _afterFF = b == 0xFF;
                _byte = b;
            }
            _left--;
            return (_byte >> _left) & 1;
        }

        public int Read(int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++)
                value = value << 1 | Read();
            return value;
        }

        /// <summary>Ends the header at a byte (a 0xFF last byte is followed by its stuffed one); the next byte's position.</summary>
        public int Align()
        {
            _left = 0;
            if (_afterFF)
            {
                _position++;
                _afterFF = false;
            }
            return _position;
        }
    }

    private sealed class Tile
    {
        private readonly Size _size;
        private readonly Budget _budget;
        private readonly CancellationToken _cancellationToken;
        private readonly long _tx0, _ty0, _tx1, _ty1;
        private readonly TileComponent[] _components;
        private readonly int _layers;
        private readonly int _order;
        private readonly int _mct;
        private readonly List<Progression>? _progressions;
        private readonly byte[] _body;
        private int _bodyPosition;
        private readonly byte[]? _headers;
        private int _headerPosition;
        private bool _stopped;

        public Tile(Size size, Parameters main, TileParts parts, int index, Budget budget, byte[] data, CancellationToken cancellationToken)
        {
            _size = size;
            _budget = budget;
            _cancellationToken = cancellationToken;
            var tile = parts.Parameters;
            _layers = tile.Layers ?? main.Layers ?? 1;
            _order = tile.Order ?? main.Order ?? 0;
            _mct = tile.Mct ?? main.Mct ?? 0;
            _progressions = tile.Progressions ?? main.Progressions;
            if (_layers > budget.Limits.MaxLayers)
                throw DocumentLimits.Exceeded($"A JPEG 2000 tile has more than {DocumentLimits.Count(budget.Limits.MaxLayers)} quality layers.");

            var (p, q) = (index % size.TilesAcross, index / size.TilesAcross);
            _tx0 = Math.Max(size.TileX0 + (long)p * size.TileWidth, size.X0);
            _ty0 = Math.Max(size.TileY0 + (long)q * size.TileHeight, size.Y0);
            _tx1 = Math.Min(size.TileX0 + (long)(p + 1) * size.TileWidth, size.Width);
            _ty1 = Math.Min(size.TileY0 + (long)(q + 1) * size.TileHeight, size.Height);

            _components = new TileComponent[size.Components.Length];
            for (var c = 0; c < _components.Length; c++)
            {
                var style = tile.ComponentStyles[c] ?? tile.Style ?? main.ComponentStyles[c] ?? main.Style!;
                var quantization = tile.ComponentQuantizations[c] ?? tile.Quantization ?? main.ComponentQuantizations[c] ?? main.Quantization!;
                var roi = tile.Roi[c] ?? main.Roi[c] ?? 0;
                _components[c] = Component(size.Components[c], style, quantization, roi);
            }

            // The tile's data: its tile-parts one after the other (packets may run across them).
            _body = new byte[parts.Data.Sum(d => (long)(d.End - d.Start))];
            var at = 0;
            foreach (var (start, end) in parts.Data)
            {
                Array.Copy(data, start, _body, at, end - start);
                at += end - start;
            }
            if (parts.PackedHeaders.Count > 0)
                _headers = [.. parts.PackedHeaders.SelectMany(h => h)];
        }

        private TileComponent Component(ComponentSize info, CodingStyle style, Quantization quantization, int roi)
        {
            var tc = new TileComponent
            {
                X0 = Ceil(_tx0, info.Dx),
                Y0 = Ceil(_ty0, info.Dy),
                X1 = Ceil(_tx1, info.Dx),
                Y1 = Ceil(_ty1, info.Dy),
                Style = style,
                Resolutions = new Resolution[style.Levels + 1],
                Precision = info.Precision,
                Signed = info.Signed,
                Roi = roi,
            };
            var levels = style.Levels;
            for (var r = 0; r <= levels; r++)
            {
                var n = levels - r;
                var res = new Resolution
                {
                    X0 = Shift(tc.X0, n),
                    Y0 = Shift(tc.Y0, n),
                    X1 = Shift(tc.X1, n),
                    Y1 = Shift(tc.Y1, n),
                    PrecinctWidth = style.PrecinctWidth[r],
                    PrecinctHeight = style.PrecinctHeight[r],
                    Bands = new Band[r == 0 ? 1 : 3],
                    Precincts = [],
                };
                res.BlockWidth = Math.Min(style.BlockWidth, res.PrecinctWidth - (r > 0 ? 1 : 0));
                res.BlockHeight = Math.Min(style.BlockHeight, res.PrecinctHeight - (r > 0 ? 1 : 0));
                res.PrecinctsWide = res.X1 > res.X0 ? (int)((((long)res.X1 + (1L << res.PrecinctWidth) - 1) >> res.PrecinctWidth) - (res.X0 >> res.PrecinctWidth)) : 0;
                res.PrecinctsHigh = res.Y1 > res.Y0 ? (int)((((long)res.Y1 + (1L << res.PrecinctHeight) - 1) >> res.PrecinctHeight) - (res.Y0 >> res.PrecinctHeight)) : 0;
                var precincts = (long)res.PrecinctsWide * res.PrecinctsHigh;
                _budget.Packets += precincts * _layers;
                if (_budget.Packets > _budget.Limits.MaxPackets)
                    throw DocumentLimits.Exceeded($"A JPEG 2000 picture has more than {DocumentLimits.Count(_budget.Limits.MaxPackets)} packets.");
                res.Precincts = new Precinct?[precincts];
                for (var b = 0; b < res.Bands.Length; b++)
                {
                    var orientation = r == 0 ? 0 : b + 1;
                    var highX = orientation is 1 or 3;
                    var highY = orientation is 2 or 3;
                    var band = new Band
                    {
                        Orientation = orientation,
                        X0 = r == 0 ? res.X0 : highX ? res.X0 >> 1 : (res.X0 + 1) >> 1,
                        X1 = r == 0 ? res.X1 : highX ? res.X1 >> 1 : (res.X1 + 1) >> 1,
                        Y0 = r == 0 ? res.Y0 : highY ? res.Y0 >> 1 : (res.Y0 + 1) >> 1,
                        Y1 = r == 0 ? res.Y1 : highY ? res.Y1 >> 1 : (res.Y1 + 1) >> 1,
                    };
                    // Quantization (E.1): the band's exponent and mantissa, its gain, its bit-planes.
                    var bandIndex = r == 0 ? 0 : 3 * (r - 1) + orientation;
                    int exponent, mantissa;
                    if (quantization.Style == 1)
                        (exponent, mantissa) = (quantization.Exponents[0] - levels + (r == 0 ? levels : levels - r + 1), quantization.Mantissas[0]);
                    else
                        (exponent, mantissa) = (quantization.Exponents[Math.Min(bandIndex, quantization.Exponents.Length - 1)],
                            quantization.Mantissas[Math.Min(bandIndex, quantization.Mantissas.Length - 1)]);
                    var gain = orientation switch { 0 => 0, 3 => 2, _ => 1 };
                    band.Planes = quantization.Guard + exponent - 1 + roi;
                    if (band.Planes > MaxPlanes)
                        throw new NotSupportedException($"JPEG 2000 coefficients of more than {MaxPlanes} bit-planes aren't read.");
                    band.Step = (float)(Math.Pow(2, info.Precision + gain - exponent) * (1 + mantissa / 2048.0));
                    res.Bands[b] = band;
                    if (band.X1 > band.X0 && band.Y1 > band.Y0)
                    {
                        var blocks = ((((long)band.X1 + (1L << res.BlockWidth) - 1) >> res.BlockWidth) - (band.X0 >> res.BlockWidth))
                            * ((((long)band.Y1 + (1L << res.BlockHeight) - 1) >> res.BlockHeight) - (band.Y0 >> res.BlockHeight));
                        _budget.CodeBlocks += blocks;
                        if (_budget.CodeBlocks > _budget.Limits.MaxCodeBlocks)
                            throw DocumentLimits.Exceeded($"A JPEG 2000 picture has more than {DocumentLimits.Count(_budget.Limits.MaxCodeBlocks)} code-blocks.");
                    }
                }
                tc.Resolutions[r] = res;
            }
            return tc;
        }

        private static int Shift(int value, int by) => (int)(((long)value + (1L << by) - 1) >> by);

        public void Decode(Jpeg2000Component[] output)
        {
            Packets();
            var transformed = new object[_components.Length];
            for (var c = 0; c < _components.Length; c++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                Blocks(_components[c]);
                transformed[c] = Jpeg2000Wavelet.Inverse(_components[c].Style.Reversible, _components[c].Resolutions.Select(r => Levels(r)).ToArray());
            }
            if (_mct == 1 && _components.Length >= 3 && Same(0, 1) && Same(0, 2))
                Jpeg2000Wavelet.ComponentTransform(transformed);
            for (var c = 0; c < _components.Length; c++)
                Store(_components[c], transformed[c], output[c]);
        }

        private bool Same(int a, int b) =>
            _components[a].X0 == _components[b].X0 && _components[a].X1 == _components[b].X1 && _components[a].Y0 == _components[b].Y0 && _components[a].Y1 == _components[b].Y1;

        private static Jpeg2000Wavelet.Level Levels(Resolution res) =>
            new(res.X0, res.Y0, res.X1, res.Y1, [.. res.Bands.Select(b => new Jpeg2000Wavelet.SubBand(b.X0, b.Y0, b.X1, b.Y1, b.Ints, b.Floats))]);

        /// <summary>DC level shift, clipping, and the samples into the component.</summary>
        private static void Store(TileComponent tc, object samples, Jpeg2000Component output)
        {
            var width = tc.X1 - tc.X0;
            var height = tc.Y1 - tc.Y0;
            if (width <= 0 || height <= 0)
                return;
            var shift = tc.Signed ? 0 : 1 << (tc.Precision - 1);
            var (low, high) = tc.Signed ? (-(1 << (tc.Precision - 1)), (1 << (tc.Precision - 1)) - 1) : (0, (1 << tc.Precision) - 1);
            var ints = samples as int[];
            var floats = samples as float[];
            for (var y = 0; y < height; y++)
            {
                var oy = tc.Y0 + y - output.Y0;
                if (oy < 0 || oy >= output.Height)
                    continue;
                for (var x = 0; x < width; x++)
                {
                    var ox = tc.X0 + x - output.X0;
                    if (ox < 0 || ox >= output.Width)
                        continue;
                    var i = y * width + x;
                    var value = ints is not null ? ints[i] : floats is not null ? (int)Math.Clamp(MathF.Round(floats[i]), int.MinValue / 2, int.MaxValue / 2) : 0;
                    output.Samples[(long)oy * output.Width + ox] = Math.Clamp(value + shift, low, high);
                }
            }
        }

        // ───────────────────────── Tier 1 ─────────────────────────

        private void Blocks(TileComponent tc)
        {
            var decoder = new Jpeg2000Blocks();
            var reversible = tc.Style.Reversible;
            foreach (var res in tc.Resolutions)
            {
                foreach (var band in res.Bands)
                {
                    if (band.Width <= 0 || band.Height <= 0)
                        continue;
                    if (reversible)
                        band.Ints = new int[(long)band.Width * band.Height];
                    else
                        band.Floats = new float[(long)band.Width * band.Height];
                }
                foreach (var precinct in res.Precincts)
                {
                    if (precinct is null)
                        continue;
                    foreach (var pb in precinct.Bands)
                    {
                        var band = pb.Band;
                        foreach (var block in pb.Blocks)
                        {
                            if (block.Segments.Count == 0)
                                continue;
                            _cancellationToken.ThrowIfCancellationRequested();
                            var planes = band.Planes - block.ZeroPlanes;
                            if (planes <= 0)
                                continue;
                            var (w, h) = (block.X1 - block.X0, block.Y1 - block.Y0);
                            var values = decoder.Decode(_body, block, w, h, band.Orientation, tc.Style.BlockStyle, planes);
                            Place(values, w, h, block, band, tc.Roi, reversible);
                        }
                    }
                }
            }
        }

        /// <summary>A code-block's coefficients (twice their magnitude, signed) into its band, dequantized.</summary>
        private static void Place(int[] values, int w, int h, CodeBlock block, Band band, int roi, bool reversible)
        {
            var threshold = roi > 0 ? 1 << (roi + 1) : int.MaxValue;
            for (var y = 0; y < h; y++)
            {
                var at = (long)(block.Y0 - band.Y0 + y) * band.Width + (block.X0 - band.X0);
                for (var x = 0; x < w; x++)
                {
                    var v = values[y * w + x];
                    if (v == 0)
                        continue;
                    var magnitude = Math.Abs(v);
                    // Max-shift ROI: coefficients above every background one were scaled up by the shift.
                    if (magnitude >= threshold)
                        magnitude >>= roi;
                    if (reversible)
                        band.Ints![at + x] = v < 0 ? -(magnitude >> 1) : magnitude >> 1;
                    else
                        band.Floats![at + x] = (v < 0 ? -magnitude : magnitude) * 0.5f * band.Step;
                }
            }
        }

        // ───────────────────────── Tier 2 ─────────────────────────

        private void Packets()
        {
            var count = _components.Length;
            var maxResolutions = _components.Max(c => c.Resolutions.Length);
            var done = _components.Select(c => c.Resolutions.Select(r => new int[r.Precincts.Length]).ToArray()).ToArray();
            var progressions = _progressions ?? [new Progression(0, 0, _layers, maxResolutions, count, _order)];

            void Visit(int l, int r, int c, int p)
            {
                if (++_budget.Visits > 4 * _budget.Limits.MaxPackets)
                    throw DocumentLimits.Exceeded("A JPEG 2000 picture's progression goes over its packets too many times.");
                if (_stopped || done[c][r][p] != l)
                    return;
                done[c][r][p]++;
                Packet(l, r, c, p);
            }
            void Step()
            {
                if (++_budget.Visits > 4 * _budget.Limits.MaxPackets)
                    throw DocumentLimits.Exceeded("A JPEG 2000 picture's progression goes over its packets too many times.");
            }

            foreach (var progression in progressions)
            {
                var (rs, re) = (progression.ResolutionStart, Math.Min(progression.ResolutionEnd, maxResolutions));
                var (cs, ce) = (progression.ComponentStart, Math.Min(progression.ComponentEnd, count));
                var lye = Math.Min(progression.LayerEnd, _layers);
                if (rs >= re || cs >= ce || lye <= 0)
                    continue;
                switch (progression.Order)
                {
                    case 0: // layer, resolution, component, position
                        for (var l = 0; l < lye && !_stopped; l++)
                        {
                            for (var r = rs; r < re; r++)
                            {
                                for (var c = cs; c < ce; c++)
                                {
                                    Step();
                                    if (r >= _components[c].Resolutions.Length)
                                        continue;
                                    for (var p = 0; p < _components[c].Resolutions[r].Precincts.Length; p++)
                                        Visit(l, r, c, p);
                                }
                            }
                        }
                        break;
                    case 1: // resolution, layer, component, position
                        for (var r = rs; r < re && !_stopped; r++)
                        {
                            for (var l = 0; l < lye; l++)
                            {
                                for (var c = cs; c < ce; c++)
                                {
                                    Step();
                                    if (r >= _components[c].Resolutions.Length)
                                        continue;
                                    for (var p = 0; p < _components[c].Resolutions[r].Precincts.Length; p++)
                                        Visit(l, r, c, p);
                                }
                            }
                        }
                        break;
                    case 2: // resolution, position, component, layer
                        for (var r = rs; r < re && !_stopped; r++)
                        {
                            foreach (var (_, _, c, rr, p) in Positions(cs, ce, r, r + 1))
                            {
                                for (var l = 0; l < lye; l++)
                                    Visit(l, rr, c, p);
                            }
                        }
                        break;
                    case 3: // position, component, resolution, layer
                        foreach (var (_, _, c, r, p) in Positions(cs, ce, rs, re))
                        {
                            for (var l = 0; l < lye; l++)
                                Visit(l, r, c, p);
                        }
                        break;
                    default: // component, position, resolution, layer
                        for (var c = cs; c < ce && !_stopped; c++)
                        {
                            foreach (var (_, _, cc, r, p) in Positions(c, c + 1, rs, re))
                            {
                                for (var l = 0; l < lye; l++)
                                    Visit(l, r, cc, p);
                            }
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// The precincts of components [cs, ce) and resolutions [rs, re) in the order a position progression meets
        /// them (B.12.1.3–5): by where on the reference grid each starts (its top-left corner, or the tile's where it
        /// starts before the tile), then by component and resolution.
        /// </summary>
        private List<(long Y, long X, int C, int R, int P)> Positions(int cs, int ce, int rs, int re)
        {
            var list = new List<(long Y, long X, int C, int R, int P)>();
            for (var c = cs; c < ce; c++)
            {
                var tc = _components[c];
                var info = _size.Components[c];
                var levels = tc.Style.Levels;
                for (var r = rs; r < Math.Min(re, tc.Resolutions.Length); r++)
                {
                    var res = tc.Resolutions[r];
                    var n = levels - r;
                    for (var ky = 0; ky < res.PrecinctsHigh; ky++)
                    {
                        var y = Math.Max(_ty0, ((((long)res.Y0 >> res.PrecinctHeight) + ky) << res.PrecinctHeight << n) * info.Dy);
                        for (var kx = 0; kx < res.PrecinctsWide; kx++)
                        {
                            var x = Math.Max(_tx0, ((((long)res.X0 >> res.PrecinctWidth) + kx) << res.PrecinctWidth << n) * info.Dx);
                            list.Add((y, x, c, r, ky * res.PrecinctsWide + kx));
                            if (list.Count > _budget.Limits.MaxPackets)
                                throw DocumentLimits.Exceeded($"A JPEG 2000 picture has more than {DocumentLimits.Count(_budget.Limits.MaxPackets)} packets.");
                        }
                    }
                }
            }
            list.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X != b.X ? a.X.CompareTo(b.X)
                : a.C != b.C ? a.C.CompareTo(b.C) : a.R.CompareTo(b.R));
            return list;
        }

        private Precinct MakePrecinct(Resolution res, int r, int p)
        {
            var kx = p % res.PrecinctsWide;
            var ky = p / res.PrecinctsWide;
            var px0 = (((long)res.X0 >> res.PrecinctWidth) + kx) << res.PrecinctWidth;
            var py0 = (((long)res.Y0 >> res.PrecinctHeight) + ky) << res.PrecinctHeight;
            var px1 = px0 + (1L << res.PrecinctWidth);
            var py1 = py0 + (1L << res.PrecinctHeight);
            if (r > 0)
                (px0, py0, px1, py1) = (px0 >> 1, py0 >> 1, px1 >> 1, py1 >> 1);
            var bands = new PrecinctBand[res.Bands.Length];
            for (var b = 0; b < bands.Length; b++)
            {
                var band = res.Bands[b];
                var x0 = (int)Math.Max(px0, band.X0);
                var y0 = (int)Math.Max(py0, band.Y0);
                var x1 = (int)Math.Min(px1, band.X1);
                var y1 = (int)Math.Min(py1, band.Y1);
                if (x0 >= x1 || y0 >= y1)
                {
                    bands[b] = new PrecinctBand(band, [], 0, 0);
                    continue;
                }
                var (cw, ch) = (res.BlockWidth, res.BlockHeight);
                var firstX = x0 >> cw;
                var firstY = y0 >> ch;
                var wide = ((x1 - 1) >> cw) - firstX + 1;
                var high = ((y1 - 1) >> ch) - firstY + 1;
                var blocks = new CodeBlock[wide * high];
                for (var j = 0; j < high; j++)
                {
                    for (var i = 0; i < wide; i++)
                    {
                        blocks[j * wide + i] = new CodeBlock(Math.Max(x0, (firstX + i) << cw), Math.Max(y0, (firstY + j) << ch),
                            Math.Min(x1, (firstX + i + 1) << cw), Math.Min(y1, (firstY + j + 1) << ch));
                    }
                }
                bands[b] = new PrecinctBand(band, blocks, wide, high);
            }
            return new Precinct(bands);
        }

        /// <summary>Passes one code-word segment starting at <paramref name="start"/> may hold (D.4.1, D.6).</summary>
        internal static int SegmentCapacity(int start, int style)
        {
            if ((style & 0x04) != 0)
                return 1; // terminated on every pass
            if ((style & 0x01) == 0)
                return int.MaxValue;
            // Bypass: the first ten passes arithmetic-coded, then raw significance + refinement, arithmetic cleanup.
            if (start < 10)
                return 10 - start;
            return start % 3 == 1 ? 2 : 1;
        }

        private void Packet(int layer, int r, int c, int p)
        {
            var tc = _components[c];
            var res = tc.Resolutions[r];
            var precinct = res.Precincts[p] ??= MakePrecinct(res, r, p);
            var style = tc.Style.BlockStyle;
            // A start of packet marker (SOP) may come before it in the data.
            if (_bodyPosition + 6 <= _body.Length && _body[_bodyPosition] == 0xFF && _body[_bodyPosition + 1] == 0x91)
                _bodyPosition += 6;
            var source = _headers ?? _body;
            var bits = new Bits(source, _headers is null ? _bodyPosition : _headerPosition, source.Length);
            var pieces = new List<(Segment Segment, int Length)>();
            if (bits.Read() == 1)
            {
                foreach (var pb in precinct.Bands)
                {
                    for (var i = 0; i < pb.Blocks.Length; i++)
                    {
                        var block = pb.Blocks[i];
                        bool included;
                        var first = !block.Included;
                        if (first)
                            included = pb.Inclusion.Decode(i, layer + 1, bits);
                        else
                            included = bits.Read() == 1;
                        if (bits.Overrun)
                            break;
                        if (!included)
                            continue;
                        if (first)
                        {
                            var threshold = 1;
                            while (!pb.ZeroPlanes.Decode(i, threshold, bits))
                            {
                                if (bits.Overrun || ++threshold > MaxPlanes + 40)
                                    throw new InvalidDataException("A JPEG 2000 code-block has an impossible number of missing bit-planes.");
                            }
                            block.ZeroPlanes = pb.ZeroPlanes.Value(i);
                            block.Included = true;
                        }
                        var passes = Passes(bits);
                        while (bits.Read() == 1)
                        {
                            if (++block.Lblock > 31 || bits.Overrun)
                                throw new InvalidDataException("A JPEG 2000 packet header gives an impossible code-word length.");
                        }
                        if (block.Passes + passes > 3 * MaxPlanes + 30)
                            throw new InvalidDataException("A JPEG 2000 code-block has more coding passes than it can have.");
                        while (passes > 0)
                        {
                            if (block.Segments.Count == 0 || block.SegmentPasses >= SegmentCapacity(block.SegmentStart, style))
                            {
                                block.SegmentStart = block.Passes;
                                block.SegmentPasses = 0;
                                block.Segments.Add(new Segment(block.Passes));
                            }
                            var take = Math.Min(passes, SegmentCapacity(block.SegmentStart, style) - block.SegmentPasses);
                            var lengthBits = block.Lblock + Log2(take);
                            if (lengthBits > 31)
                                throw new InvalidDataException("A JPEG 2000 packet header gives an impossible code-word length.");
                            var length = bits.Read(lengthBits);
                            var segment = block.Segments[^1];
                            segment.Passes += take;
                            pieces.Add((segment, length));
                            block.SegmentPasses += take;
                            block.Passes += take;
                            passes -= take;
                        }
                    }
                    if (bits.Overrun)
                        break;
                }
            }
            var after = bits.Align();
            if (bits.Overrun)
            {
                _stopped = true; // cut short: what came so far is decoded
                return;
            }
            // An end of packet header marker (EPH) may follow the header.
            if (after + 2 <= source.Length && source[after] == 0xFF && source[after + 1] == 0x92)
                after += 2;
            if (_headers is null)
                _bodyPosition = after;
            else
                _headerPosition = after;
            foreach (var (segment, length) in pieces)
            {
                var available = Math.Min(length, _body.Length - _bodyPosition);
                if (available > 0)
                    segment.Pieces.Add((_bodyPosition, available));
                _bodyPosition += available;
                if (available < length)
                    _stopped = true;
            }
        }

        private static int Passes(Bits bits)
        {
            if (bits.Read() == 0)
                return 1;
            if (bits.Read() == 0)
                return 2;
            var v = bits.Read(2);
            if (v != 3)
                return 3 + v;
            v = bits.Read(5);
            return v != 31 ? 6 + v : 37 + bits.Read(7);
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
    }
}
