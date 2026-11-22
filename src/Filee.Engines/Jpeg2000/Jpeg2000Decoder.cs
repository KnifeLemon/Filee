// Filee's own JPEG 2000 decoder, written from ITU-T T.800 | ISO/IEC 15444-1 (Part 1, the core coding system): a JP2
// file (Annex I: the signature, file type and header boxes — image header, colour specification, palette, component
// mapping, channel definition — and the contiguous codestream) or a bare codestream (Annex A), as PDF's /JPXDecode
// filter holds either.
//
// Read: the markers of Part 1 — SIZ; COD, COC, QCD, QCC, RGN and POC in the main header and in tile headers (tile
// ones over main ones, COC over COD, QCC over QCD); tiles in tile-parts in any order; packed packet headers in PPM
// (main header) and PPT (tile headers); SOP and EPH markers; TLM, PLM, PLT, CRG, COM and CPF skipped — the five
// progression orders and progression order changes, precincts, tier-2 packet headers with their tag trees (Annex B),
// tier-1 block decoding (EBCOT, Annex D) with the MQ decoder (Annex C) and every code-block style (selective
// arithmetic coding bypass, context reset, termination on each pass, vertically causal contexts, predictable
// termination, segmentation symbols), scalar quantization (none, derived, expounded) and regions of interest by
// max-shift (Annexes E, H), the 5/3 reversible and 9/7 irreversible inverse wavelet transforms (Annex F), the
// reversible and irreversible component transforms and the DC level shift (Annex G). The whole picture is decoded
// (no resolution reduction).
//
// Left out (NotSupportedException): Part 2 and Part 15 coding (other wavelets and component transforms, the
// high-throughput block coder of HTJ2K) and precisions past 30 bits or bit-planes past 30 a coefficient (a large ROI
// shift on a deep picture). A damaged codestream throws InvalidDataException; one cut short decodes as far as it goes
// (the rest of its coefficients stay zero), as viewers show it.
//
// Bounds: Jpeg2000Limits — pixels and samples (error.document_too_large), components, tiles, layers, code-blocks,
// packets and visits of the progression (error.document_too_complex) — are checked before anything they count is
// made, and the bit-planes, coding passes and code-word lengths a header claims are capped, so a small hostile
// codestream can't exhaust memory or time.

using Filee.Engines.Documents;

namespace Filee.Engines.Jpeg2000;

internal static partial class Jpeg2000Decoder
{
    private const int Soc = 0xFF4F, Cap = 0xFF50, Siz = 0xFF51, Cod = 0xFF52, Coc = 0xFF53, Tlm = 0xFF55, Plm = 0xFF57, Plt = 0xFF58,
        Cpf = 0xFF59, Qcd = 0xFF5C, Qcc = 0xFF5D, Rgn = 0xFF5E, Poc = 0xFF5F, Ppm = 0xFF60, Ppt = 0xFF61, Crg = 0xFF63, Com = 0xFF64,
        Sot = 0xFF90, Sod = 0xFF93, Eoc = 0xFFD9;

    /// <summary>The JP2 signature box: length 12, "jP  ", 0D 0A 87 0A.</summary>
    private static ReadOnlySpan<byte> Jp2Signature => [0, 0, 0, 12, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A];

    /// <summary>A JP2 (or JPX) file, or a bare codestream (SOC, SIZ).</summary>
    public static bool IsJpeg2000(ReadOnlySpan<byte> data) => data.StartsWith(Jp2Signature) || data is [0xFF, 0x4F, 0xFF, 0x51, ..];

    /// <summary>Decodes a JP2 file or a bare codestream.</summary>
    public static Jpeg2000Image Decode(byte[] data, Jpeg2000Limits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= Jpeg2000Limits.Default;
        if (data.AsSpan().StartsWith(Jp2Signature))
            return File(data, limits, cancellationToken);
        if (data is [0xFF, 0x4F, ..])
            return Codestream(data, 0, data.Length, limits, cancellationToken);
        throw new InvalidDataException("The data is not a JPEG 2000 picture.");
    }

    // ───────────────────────── JP2 boxes ─────────────────────────

    private static Jpeg2000Image File(byte[] data, Jpeg2000Limits limits, CancellationToken cancellationToken)
    {
        var color = Jpeg2000ColorSpace.Unknown;
        byte[]? icc = null;
        int[][]? columns = null;
        int[]? depths = null;
        bool[]? signs = null;
        (int, int)[]? mapping = null;
        List<Jpeg2000Channel>? channels = null;
        var colorSeen = false;
        (int Start, int End)? codestream = null;

        void Header(int start, int end)
        {
            foreach (var (type, from, to) in Boxes(data, start, end))
            {
                switch (type)
                {
                    case 0x636F6C72 when !colorSeen && to - from >= 3: // colr: the first one counts
                        colorSeen = true;
                        var method = data[from];
                        if (method == 1 && to - from >= 7)
                        {
                            color = (data[from + 3] << 24 | data[from + 4] << 16 | data[from + 5] << 8 | data[from + 6]) switch
                            {
                                16 => Jpeg2000ColorSpace.Srgb,
                                17 => Jpeg2000ColorSpace.Grey,
                                18 => Jpeg2000ColorSpace.Sycc,
                                12 => Jpeg2000ColorSpace.Cmyk,
                                _ => Jpeg2000ColorSpace.Unknown,
                            };
                        }
                        else if (method is 2 or 3 && to - from > 3)
                        {
                            color = Jpeg2000ColorSpace.Icc;
                            icc = data.AsSpan(from + 3, to - from - 3).ToArray();
                        }
                        break;
                    case 0x70636C72 when to - from >= 3: // pclr
                        (columns, depths, signs) = PaletteBox(data, from, to);
                        break;
                    case 0x636D6170: // cmap
                        mapping = new (int, int)[(to - from) / 4];
                        for (var i = 0; i < mapping.Length; i++)
                        {
                            var at = from + 4 * i;
                            mapping[i] = (data[at] << 8 | data[at + 1], data[at + 2] == 1 ? data[at + 3] : -1);
                        }
                        break;
                    case 0x63646566 when to - from >= 2: // cdef
                        var n = data[from] << 8 | data[from + 1];
                        channels = [];
                        for (var i = 0; i < n && from + 2 + 6 * i + 6 <= to; i++)
                        {
                            var at = from + 2 + 6 * i;
                            channels.Add(new Jpeg2000Channel(data[at] << 8 | data[at + 1], data[at + 2] << 8 | data[at + 3], data[at + 4] << 8 | data[at + 5]));
                        }
                        break;
                }
            }
        }

        foreach (var (type, from, to) in Boxes(data, 0, data.Length))
        {
            if (type is 0x6A703268 or 0x6A706368) // jp2h, and JPX's codestream header jpch
                Header(from, to);
            else if (type == 0x6A703263 && codestream is null) // jp2c
                codestream = (from, to);
        }
        if (codestream is not var (start, end))
            throw new InvalidDataException("The JPEG 2000 file has no codestream.");
        var image = Codestream(data, start, end, limits, cancellationToken);
        Jpeg2000Palette? palette = null;
        if (columns is not null && mapping is { Length: > 0 })
            palette = new Jpeg2000Palette(columns, depths!, signs!, mapping);
        return image with { ColorSpace = color, IccProfile = icc, Palette = palette, Channels = channels };
    }

    /// <summary>The boxes between <paramref name="start"/> and <paramref name="end"/>: type, and where their contents lie.</summary>
    private static IEnumerable<(uint Type, int From, int To)> Boxes(byte[] data, int start, int end)
    {
        var at = start;
        while (at + 8 <= end)
        {
            long length = (uint)(data[at] << 24 | data[at + 1] << 16 | data[at + 2] << 8 | data[at + 3]);
            var type = (uint)(data[at + 4] << 24 | data[at + 5] << 16 | data[at + 6] << 8 | data[at + 7]);
            var header = 8;
            if (length == 1)
            {
                if (at + 16 > end)
                    yield break;
                length = 0;
                for (var i = 0; i < 8; i++)
                    length = length << 8 | data[at + 8 + i];
                header = 16;
            }
            else if (length == 0)
            {
                length = end - at;
            }
            if (length < header || length > end - at)
            {
                // A box that claims more than there is: the codestream box often runs to the end anyway.
                if (type == 0x6A703263)
                    yield return (type, at + header, end);
                yield break;
            }
            yield return (type, at + header, (int)(at + length));
            at += (int)length;
        }
    }

    private static (int[][] Columns, int[] Depths, bool[] Signed) PaletteBox(byte[] data, int from, int to)
    {
        var entries = Math.Min(data[from] << 8 | data[from + 1], 1024);
        var count = data[from + 2];
        if (entries == 0 || count == 0 || from + 3 + count > to)
            throw new InvalidDataException("The JPEG 2000 palette is damaged.");
        var depths = new int[count];
        var signs = new bool[count];
        var sizes = new int[count];
        for (var i = 0; i < count; i++)
        {
            depths[i] = Math.Min((data[from + 3 + i] & 0x7F) + 1, 30);
            signs[i] = (data[from + 3 + i] & 0x80) != 0;
            sizes[i] = (depths[i] + 7) / 8;
        }
        var columns = new int[count][];
        for (var i = 0; i < count; i++)
            columns[i] = new int[entries];
        var at = from + 3 + count;
        for (var e = 0; e < entries; e++)
        {
            for (var i = 0; i < count; i++)
            {
                var value = 0;
                for (var b = 0; b < sizes[i]; b++)
                    value = value << 8 | (at < to ? data[at++] : 0);
                if (signs[i] && depths[i] < 32 && (value & (1 << (depths[i] - 1))) != 0)
                    value -= 1 << depths[i];
                columns[i][e] = value;
            }
        }
        return (columns, depths, signs);
    }

    // ───────────────────────── Codestream ─────────────────────────

    /// <summary>A component of the picture as SIZ describes it.</summary>
    internal readonly record struct ComponentSize(int Precision, bool Signed, int Dx, int Dy);

    /// <summary>The reference grid, the tile grid and the components (SIZ).</summary>
    internal sealed record Size(int Width, int Height, int X0, int Y0, int TileWidth, int TileHeight, int TileX0, int TileY0, ComponentSize[] Components)
    {
        public int TilesAcross => (int)(((long)Width - TileX0 + TileWidth - 1) / TileWidth);

        public int TilesDown => (int)(((long)Height - TileY0 + TileHeight - 1) / TileHeight);
    }

    /// <summary>A component's coding style (COD / COC).</summary>
    internal sealed record CodingStyle(int Levels, int BlockWidth, int BlockHeight, int BlockStyle, bool Reversible, int[] PrecinctWidth, int[] PrecinctHeight);

    /// <summary>A component's quantization (QCD / QCC): style 0 none, 1 derived, 2 expounded.</summary>
    internal sealed record Quantization(int Style, int Guard, int[] Exponents, int[] Mantissas);

    /// <summary>One progression of a POC marker (or the whole tile's, from COD).</summary>
    internal readonly record struct Progression(int ResolutionStart, int ComponentStart, int LayerEnd, int ResolutionEnd, int ComponentEnd, int Order);

    /// <summary>What the main header or a tile's headers set; a tile's fall back to the main header's.</summary>
    internal sealed class Parameters(int components)
    {
        public int? Order { get; set; }
        public int? Layers { get; set; }
        public int? Mct { get; set; }
        public bool Sop { get; set; }
        public bool Eph { get; set; }
        public CodingStyle? Style { get; set; }
        public CodingStyle?[] ComponentStyles { get; } = new CodingStyle?[components];
        public Quantization? Quantization { get; set; }
        public Quantization?[] ComponentQuantizations { get; } = new Quantization?[components];
        public int?[] Roi { get; } = new int?[components];
        public List<Progression>? Progressions { get; set; }
    }

    /// <summary>A tile's tile-parts: their data and packed packet headers, and its headers' parameters.</summary>
    internal sealed class TileParts(int components)
    {
        public List<(int Start, int End)> Data { get; } = [];
        public Parameters Parameters { get; } = new(components);
        public List<byte[]> PackedHeaders { get; } = [];
    }

    private sealed class Reader(byte[] data, int position, int end)
    {
        public int Position = position;
        public readonly int End = end;

        public int Remaining => End - Position;

        public int U8() => Position < End ? data[Position++] : throw Short();

        public int U16() => U8() << 8 | U8();

        public long U32() => (long)U16() << 16 | (uint)U16();

        private static InvalidDataException Short() => new("A JPEG 2000 marker segment is cut short.");
    }

    private static Jpeg2000Image Codestream(byte[] data, int start, int end, Jpeg2000Limits limits, CancellationToken cancellationToken)
    {
        var reader = new Reader(data, start, end);
        if (reader.U16() != Soc || reader.U16() != Siz)
            throw new InvalidDataException("The JPEG 2000 codestream doesn't start with SOC and SIZ.");
        var size = ReadSize(reader, limits);
        var count = size.Components.Length;
        var main = new Parameters(count);
        var ppm = new List<byte[]>();

        // The main header, up to the first tile-part.
        while (true)
        {
            var marker = reader.U16();
            if (marker == Sot)
                break;
            if (marker == Eoc)
                throw new InvalidDataException("The JPEG 2000 codestream has no tiles.");
            if ((marker & 0xFF00) != 0xFF00)
                throw new InvalidDataException("The JPEG 2000 main header is damaged.");
            var segment = MarkerSegment(reader, data);
            switch (marker)
            {
                case Ppm:
                    if (segment.Remaining > 1)
                        ppm.Add(data.AsSpan(segment.Position + 1, segment.Remaining - 1).ToArray());
                    break;
                default:
                    Marker(marker, segment, main, size, data);
                    break;
            }
        }
        if (main.Style is null || main.Quantization is null)
            throw new InvalidDataException("The JPEG 2000 main header has no COD or QCD.");

        // Tile-parts, in the order they come (one tile's in its order).
        var tileCount = (long)size.TilesAcross * size.TilesDown;
        if (tileCount > limits.MaxTiles)
            throw DocumentLimits.Exceeded($"A JPEG 2000 picture has more than {DocumentLimits.Count(limits.MaxTiles)} tiles.");
        var tiles = new Dictionary<int, TileParts>();
        var partsInOrder = new List<TileParts>();
        reader.Position -= 2;
        while (reader.Remaining >= 2)
        {
            var at = reader.Position;
            var marker = reader.U16();
            if (marker == Eoc || marker != Sot || reader.Remaining < 10)
                break;
            var lsot = reader.U16();
            var index = reader.U16();
            var psot = reader.U32();
            reader.U8(); // TPsot
            reader.U8(); // TNsot
            if (lsot != 10)
                throw new InvalidDataException("A JPEG 2000 SOT marker is damaged.");
            var partEnd = psot == 0 ? end : at + psot;
            if (psot != 0 && psot < 14)
                throw new InvalidDataException("A JPEG 2000 tile-part has an impossible length.");
            if (partEnd > end)
                partEnd = end; // cut short: decode what is there
            if (psot == 0 && end - 2 >= reader.Position && data[end - 2] == 0xFF && data[end - 1] == 0xD9)
                partEnd = end - 2;
            if (index >= tileCount)
                throw new InvalidDataException("A JPEG 2000 tile-part names a tile the picture doesn't have.");
            if (!tiles.TryGetValue(index, out var tile))
                tiles[index] = tile = new TileParts(count);
            partsInOrder.Add(tile);
            var part = new Reader(data, reader.Position, (int)partEnd);
            while (true)
            {
                var m = part.Remaining >= 2 ? part.U16() : Sod;
                if (m == Sod)
                    break;
                if ((m & 0xFF00) != 0xFF00)
                    throw new InvalidDataException("A JPEG 2000 tile-part header is damaged.");
                var segment = MarkerSegment(part, data);
                if (m == Ppt)
                {
                    if (segment.Remaining > 1)
                        tile.PackedHeaders.Add(data.AsSpan(segment.Position + 1, segment.Remaining - 1).ToArray());
                }
                else
                {
                    Marker(m, segment, tile.Parameters, size, data);
                }
            }
            tile.Data.Add((part.Position, (int)partEnd));
            reader.Position = (int)partEnd;
        }

        // Packed packet headers of the main header (PPM): each tile-part's in turn, Nppm bytes after its length.
        if (ppm.Count > 0)
        {
            var all = ppm.SelectMany(p => p).ToArray();
            var at = 0;
            foreach (var tile in partsInOrder)
            {
                if (at + 4 > all.Length)
                    break;
                var length = (int)Math.Min((uint)(all[at] << 24 | all[at + 1] << 16 | all[at + 2] << 8 | all[at + 3]), (uint)(all.Length - at - 4));
                tile.PackedHeaders.Add(all.AsSpan(at + 4, length).ToArray());
                at += 4 + length;
            }
        }

        // The picture's components; tiles fill them.
        var components = new Jpeg2000Component[count];
        long samples = 0;
        for (var c = 0; c < count; c++)
        {
            var (dx, dy) = (size.Components[c].Dx, size.Components[c].Dy);
            var x0 = Ceil(size.X0, dx);
            var y0 = Ceil(size.Y0, dy);
            var width = Ceil(size.Width, dx) - x0;
            var height = Ceil(size.Height, dy) - y0;
            samples += (long)width * height;
            if (samples > limits.MaxSamples)
                throw DocumentLimits.TooLarge($"A JPEG 2000 picture has more than {DocumentLimits.Count(limits.MaxSamples)} samples.");
            components[c] = new Jpeg2000Component(width, height, x0, y0, dx, dy, size.Components[c].Precision, size.Components[c].Signed, []);
        }
        for (var c = 0; c < count; c++)
            components[c] = components[c] with { Samples = new int[(long)components[c].Width * components[c].Height] };

        var budget = new Budget(limits);
        foreach (var (index, tile) in tiles.OrderBy(t => t.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            new Tile(size, main, tile, index, budget, data, cancellationToken).Decode(components);
        }
        return new Jpeg2000Image(size.Width - size.X0, size.Height - size.Y0, size.X0, size.Y0, components);
    }

    /// <summary>What the tiles decoded so far have used of the limits.</summary>
    internal sealed class Budget(Jpeg2000Limits limits)
    {
        public Jpeg2000Limits Limits { get; } = limits;
        public long CodeBlocks;
        public long Packets;
        public long Visits;
    }

    internal static int Ceil(long value, long by) => (int)((value + by - 1) / by);

    /// <summary>A marker segment's contents (after its length), skipped in <paramref name="reader"/>.</summary>
    private static Reader MarkerSegment(Reader reader, byte[] data)
    {
        var length = reader.U16();
        if (length < 2 || length - 2 > reader.Remaining)
            throw new InvalidDataException("A JPEG 2000 marker segment is longer than the codestream.");
        var segment = new Reader(data, reader.Position, reader.Position + length - 2);
        reader.Position += length - 2;
        return segment;
    }

    private static Size ReadSize(Reader reader, Jpeg2000Limits limits)
    {
        var length = reader.U16();
        if (length < 41)
            throw new InvalidDataException("The JPEG 2000 SIZ marker is damaged.");
        var capabilities = reader.U16();
        if ((capabilities & 0x4000) != 0)
            throw new NotSupportedException("High-throughput JPEG 2000 (Part 15) isn't read.");
        var (width, height, x0, y0) = (reader.U32(), reader.U32(), reader.U32(), reader.U32());
        var (tileWidth, tileHeight, tileX0, tileY0) = (reader.U32(), reader.U32(), reader.U32(), reader.U32());
        var count = reader.U16();
        if (width <= x0 || height <= y0 || tileWidth == 0 || tileHeight == 0 || tileX0 > x0 || tileY0 > y0
            || tileX0 + tileWidth <= x0 || tileY0 + tileHeight <= y0 || count == 0 || length != 38 + 3 * count)
            throw new InvalidDataException("The JPEG 2000 SIZ marker describes an impossible picture.");
        if (width > int.MaxValue || height > int.MaxValue || tileWidth > int.MaxValue || tileHeight > int.MaxValue)
            throw DocumentLimits.TooLarge("A JPEG 2000 picture's grid is larger than Filee reads.");
        if ((width - x0) * (height - y0) > limits.MaxPixels)
            throw DocumentLimits.TooLarge($"A JPEG 2000 picture has more than {DocumentLimits.Count(limits.MaxPixels)} pixels.");
        if (count > limits.MaxComponents)
            throw DocumentLimits.Exceeded($"A JPEG 2000 picture has more than {DocumentLimits.Count(limits.MaxComponents)} components.");
        var components = new ComponentSize[count];
        for (var c = 0; c < count; c++)
        {
            var s = reader.U8();
            var (dx, dy) = (reader.U8(), reader.U8());
            var precision = (s & 0x7F) + 1;
            if (dx == 0 || dy == 0)
                throw new InvalidDataException("A JPEG 2000 component has no sampling.");
            if (precision > 30)
                throw new NotSupportedException("JPEG 2000 components of more than 30 bits aren't read.");
            components[c] = new ComponentSize(precision, (s & 0x80) != 0, dx, dy);
        }
        return new Size((int)width, (int)height, (int)x0, (int)y0, (int)tileWidth, (int)tileHeight, (int)tileX0, (int)tileY0, components);
    }

    /// <summary>A main or tile header marker that sets coding parameters; others are skipped.</summary>
    private static void Marker(int marker, Reader segment, Parameters parameters, Size size, byte[] data)
    {
        var count = size.Components.Length;
        int Component() => count < 257 ? segment.U8() : segment.U16();
        switch (marker)
        {
            case Cod:
                {
                    var scod = segment.U8();
                    parameters.Order = segment.U8();
                    parameters.Layers = segment.U16();
                    parameters.Mct = segment.U8();
                    parameters.Sop = (scod & 2) != 0;
                    parameters.Eph = (scod & 4) != 0;
                    parameters.Style = Style(segment, (scod & 1) != 0);
                    if (parameters.Order > 4)
                        throw new InvalidDataException("A JPEG 2000 COD marker names an unknown progression order.");
                    if (parameters.Layers == 0)
                        throw new InvalidDataException("A JPEG 2000 COD marker has no layers.");
                    break;
                }
            case Coc:
                {
                    var c = Component();
                    var scoc = segment.U8();
                    var style = Style(segment, (scoc & 1) != 0);
                    if (c < count)
                        parameters.ComponentStyles[c] = style;
                    break;
                }
            case Qcd:
                parameters.Quantization = Quantize(segment);
                break;
            case Qcc:
                {
                    var c = Component();
                    var quantization = Quantize(segment);
                    if (c < count)
                        parameters.ComponentQuantizations[c] = quantization;
                    break;
                }
            case Rgn:
                {
                    var c = Component();
                    var style = segment.U8();
                    var shift = segment.U8();
                    if (style != 0)
                        throw new NotSupportedException("JPEG 2000 regions of interest other than max-shift aren't read.");
                    if (c < count)
                        parameters.Roi[c] = shift;
                    break;
                }
            case Poc:
                {
                    var wide = count >= 257;
                    parameters.Progressions ??= [];
                    while (segment.Remaining >= (wide ? 9 : 7))
                    {
                        var rs = segment.U8();
                        var cs = Component();
                        var lye = segment.U16();
                        var re = segment.U8();
                        var ce = Component();
                        var order = segment.U8();
                        if (!wide && ce == 0)
                            ce = 256;
                        if (order > 4)
                            throw new InvalidDataException("A JPEG 2000 POC marker names an unknown progression order.");
                        parameters.Progressions.Add(new Progression(rs, cs, lye, re, ce, order));
                    }
                    break;
                }
            case Cpf or Tlm or Plm or Plt or Crg or Com or Cap:
                break;
        }
    }

    private static CodingStyle Style(Reader segment, bool precincts)
    {
        var levels = segment.U8();
        var blockWidth = segment.U8() + 2;
        var blockHeight = segment.U8() + 2;
        var blockStyle = segment.U8();
        var transform = segment.U8();
        if (levels > 32 || blockWidth > 10 || blockHeight > 10 || blockWidth + blockHeight > 12)
            throw new InvalidDataException("A JPEG 2000 coding style is impossible.");
        if ((blockStyle & 0x40) != 0)
            throw new NotSupportedException("High-throughput JPEG 2000 code-blocks (Part 15) aren't read.");
        if (transform > 1)
            throw new NotSupportedException("JPEG 2000 wavelets of Part 2 aren't read.");
        var widths = new int[levels + 1];
        var heights = new int[levels + 1];
        for (var r = 0; r <= levels; r++)
        {
            if (precincts)
            {
                var b = segment.U8();
                (widths[r], heights[r]) = (b & 15, b >> 4);
                if (r > 0 && (widths[r] == 0 || heights[r] == 0))
                    throw new InvalidDataException("A JPEG 2000 precinct is impossible.");
            }
            else
            {
                (widths[r], heights[r]) = (15, 15);
            }
        }
        return new CodingStyle(levels, blockWidth, blockHeight, blockStyle, transform == 1, widths, heights);
    }

    private static Quantization Quantize(Reader segment)
    {
        var sq = segment.U8();
        var style = sq & 0x1F;
        var guard = sq >> 5;
        var exponents = new List<int>();
        var mantissas = new List<int>();
        switch (style)
        {
            case 0:
                while (segment.Remaining > 0)
                {
                    exponents.Add(segment.U8() >> 3);
                    mantissas.Add(0);
                }
                break;
            case 1 or 2:
                while (segment.Remaining >= 2)
                {
                    var v = segment.U16();
                    exponents.Add(v >> 11);
                    mantissas.Add(v & 0x7FF);
                    if (style == 1)
                        break;
                }
                break;
            default:
                throw new InvalidDataException("A JPEG 2000 quantization style is unknown.");
        }
        if (exponents.Count == 0)
            throw new InvalidDataException("A JPEG 2000 quantization marker has no step sizes.");
        return new Quantization(style, guard, [.. exponents], [.. mantissas]);
    }
}
