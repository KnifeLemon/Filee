// GIMP's own file (.xcf), read by Filee for every version GIMP writes: the header (canvas, colour mode, precision), the
// layers and layer groups with their properties, and each layer's pixels in 64 × 64 tiles, uncompressed, RLE or zlib,
// at 8, 16 or 32-bit integer, half, float or double precision (ImageMagick reads only 8-bit files without zlib). Layers
// are merged as GIMP shows them (XcfBlend: every layer mode, opacity, layer masks, groups and pass-through), or kept as
// layers in a PSD or a Paint.NET file. Pixels are kept at 8 bits per channel; the merge itself is in floating point.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using ImageMagick;

namespace Filee.Engines.Magick;

internal static class Xcf
{
    /// <summary>A layer, or a layer group with its layers bottom first.</summary>
    internal sealed class Layer
    {
        public string Name { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Visible { get; set; } = true;
        public float Opacity { get; set; } = 1;
        public XcfBlend.Mode Mode { get; set; } = XcfBlend.Resolve(XcfBlend.NormalLegacy, 0, 0, 0);

        /// <summary>The pixels, BGRA (not premultiplied, sRGB-encoded) top row first; null for a group.</summary>
        public byte[]? Bgra { get; set; }

        /// <summary>The layer mask GIMP applies, one byte per pixel of the layer; null when there is none.</summary>
        public byte[]? Mask { get; set; }

        /// <summary>The layers of a group, bottom first; null for a layer.</summary>
        public List<Layer>? Children { get; set; }
    }

    /// <summary>
    /// A GIMP picture: canvas size, its layers bottom first, whether it is grayscale, and the space a layer group's
    /// result is stored in at 8 bits (GIMP keeps a group at the picture's precision); null at a higher precision.
    /// </summary>
    internal sealed record Picture(int Width, int Height, bool Grayscale, XcfBlend.Space? GroupsAtEightBits, List<Layer> Layers)
    {
        /// <summary>The space of the picture's own pixels: linear for GIMP's linear precisions, else perceptual.</summary>
        public XcfBlend.Space StoredSpace { get; init; } = XcfBlend.Space.Perceptual;
    }

    /// <summary>The largest canvas GIMP makes (GIMP_MAX_IMAGE_SIZE).</summary>
    public const int MaxSide = 524_288;

    /// <summary>More layers and groups than GIMP users make, as for Paint.NET; a file claiming more is damaged.</summary>
    private const int MaxLayers = 4_096;

    /// <summary>
    /// The most merging Filee does for one picture, counted as canvas pixels × shown layers and groups (merging goes
    /// over every canvas row for each): sixteen times the pixel budget, a 6,000 × 4,000 picture of 180 layers.
    /// </summary>
    internal const long MaxMergeWork = 16 * MaxPixels;

    /// <summary>Bytes of a layer name Filee keeps; the rest is skipped.</summary>
    private const int MaxNameBytes = 1_024;

    /// <summary>The most pixels Filee reads from an .xcf, every layer counted, as for Paint.NET: 1 GB at 8 bits.</summary>
    internal const long MaxPixels = PaintNet.MaxPixels;

    /// <summary>Nesting deeper than this is damage (GIMP users nest a few groups).</summary>
    private const int MaxDepth = 64;

    private const int Tile = 64;

    private static readonly InvalidDataException Damaged =
        new("This GIMP file is damaged or of an unknown kind. Open it in GIMP and save it again.");

    // ───────────────────────── Reading ─────────────────────────

    /// <summary>The picture with every layer's pixels.</summary>
    /// <exception cref="InvalidDataException">The file is not a GIMP picture Filee can read.</exception>
    public static Picture Open(string path)
    {
        using var file = File.OpenRead(path);
        try
        {
            return new Reader(file).Read();
        }
        catch (Exception ex) when (ex is EndOfStreamException or OverflowException or IndexOutOfRangeException
                                       or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException(Damaged.Message, ex);
        }
    }

    /// <summary>Precision of the pixel values: bytes per channel, how they are stored and whether colour is linear.</summary>
    private readonly record struct Precision(int Bytes, bool Float, bool Linear)
    {
        public static Precision Of(uint value, int version)
        {
            // XCF 4 to 6 were GIMP 2.9 development versions with their own numbers; XCF 3 and older are 8-bit.
            if (version == 4)
            {
                return value switch
                {
                    0 => new(1, false, false),
                    1 => new(2, false, false),
                    2 => new(4, false, true),
                    3 => new(2, true, true),
                    4 => new(4, true, true),
                    _ => throw Damaged,
                };
            }
            if (version is 5 or 6)
            {
                return value switch
                {
                    100 => new(1, false, true),
                    150 => new(1, false, false),
                    200 => new(2, false, true),
                    250 => new(2, false, false),
                    300 => new(4, false, true),
                    350 => new(4, false, false),
                    400 => new(2, true, true),
                    450 => new(2, true, false),
                    500 => new(4, true, true),
                    550 => new(4, true, false),
                    _ => throw Damaged,
                };
            }
            // 100 linear, 150 non-linear, 175 perceptual (GIMP 3), and so on for each type.
            var linear = value % 100 == 0;
            if (!linear && value % 100 is not (50 or 75))
                throw Damaged;
            return (value / 100) switch
            {
                1 => new(1, false, linear),
                2 => new(2, false, linear),
                3 => new(4, false, linear),
                5 => new(2, true, linear),
                6 => new(4, true, linear),
                7 => new(8, true, linear),
                _ => throw Damaged,
            };
        }
    }

    // Property ids (app/xcf/xcf-private.h).
    private const uint PropEnd = 0, PropColormap = 1, PropFloatingSelection = 5, PropOpacity = 6, PropMode = 7,
        PropVisible = 8, PropApplyMask = 11, PropOffsets = 15, PropCompression = 17, PropGroupItem = 29,
        PropItemPath = 30, PropFloatOpacity = 33, PropCompositeMode = 35, PropCompositeSpace = 36, PropBlendSpace = 37;

    private sealed class Reader(FileStream file)
    {
        private int _version;
        private int _baseType;
        private Precision _precision;
        private int _compression;
        private byte[] _colormap = [];

        public Picture Read()
        {
            Span<byte> magic = stackalloc byte[14];
            file.ReadExactly(magic);
            if (!magic[..9].SequenceEqual("gimp xcf "u8) || magic[13] != 0)
                throw Damaged;
            var tag = Encoding.ASCII.GetString(magic[9..13]);
            if (tag == "file")
                _version = 0;
            else if (tag[0] != 'v' || !int.TryParse(tag[1..], System.Globalization.NumberStyles.None, null, out _version))
                throw Damaged;
            // Versions from 100 up were CinePaint's, a different format.
            if (_version >= 100)
                throw new InvalidDataException("This XCF file was made by CinePaint, not GIMP; Filee can't read it.");

            var width = U32();
            var height = U32();
            _baseType = (int)U32();
            if (width is < 1 or > MaxSide || height is < 1 or > MaxSide || _baseType > 2)
                throw Damaged;
            _precision = _version >= 4 ? Precision.Of(U32(), _version) : new Precision(1, false, false);
            ReadImageProperties();
            if ((long)width * height > MaxPixels)
                throw new InvalidDataException("This GIMP picture is too large to convert.");

            // GIMP lists each layer once: a pointer listed again would have its layer read (and its name decoded) again.
            var pointers = new List<long>();
            var seen = new HashSet<long>();
            while (Pointer() is var pointer and not 0)
            {
                if (pointers.Count == MaxLayers || !seen.Add(pointer))
                    throw Damaged;
                pointers.Add(pointer);
            }

            var headers = pointers.Select(ReadLayerHeader).Where(h => h is not null).Select(h => h!).ToList();
            CheckBudget(headers, (long)width * height);
            var layers = BuildTree(headers);
            foreach (var header in headers)
                ReadPixels(header);
            XcfBlend.Space? groups = _precision.Bytes == 1 ? _precision.Linear ? XcfBlend.Space.Linear : XcfBlend.Space.Perceptual : null;
            return new Picture((int)width, (int)height, _baseType == 1, groups, layers)
            {
                StoredSpace = _precision.Linear ? XcfBlend.Space.Linear : XcfBlend.Space.Perceptual,
            };
        }

        private void ReadImageProperties()
        {
            while (true)
            {
                var (type, length) = (U32(), U32());
                var start = file.Position;
                switch (type)
                {
                    case PropEnd:
                        return;
                    case PropColormap:
                        {
                            // The length word can't be trusted here: some GIMP versions wrote n + 4.
                            var count = U32();
                            if (count > 256)
                                throw Damaged;
                            _colormap = Bytes((int)count * 3);
                            continue;
                        }
                    case PropCompression:
                        _compression = Bytes(1)[0];
                        if (_compression > 2)
                            throw Damaged;
                        break;
                }
                Seek(start + length);
            }
        }

        /// <summary>A layer as the file describes it, before its pixels are read.</summary>
        private sealed record Header(Layer Layer, int Type, long Hierarchy, long Mask, bool ApplyMask, List<uint> Path);

        private Header? ReadLayerHeader(long pointer)
        {
            Seek(pointer);
            var width = U32();
            var height = U32();
            var type = (int)U32();
            var name = Text();
            if (width is < 1 or > MaxSide || height is < 1 or > MaxSide || type > 5)
                throw Damaged;
            var layer = new Layer { Name = name, Width = (int)width, Height = (int)height };
            int mode = XcfBlend.NormalLegacy, blendSpace = 0, compositeSpace = 0, composite = 0;
            bool applyMask = true, group = false, floating = false;
            var path = new List<uint>();
            while (true)
            {
                var (property, length) = (U32(), U32());
                var start = file.Position;
                // GIMP reads the payload it knows a property to have, whatever the length says; so does Filee.
                switch (property)
                {
                    case PropEnd:
                        break;
                    case PropOpacity:
                        layer.Opacity = Math.Min(U32(), 255) / 255f;
                        continue;
                    case PropFloatOpacity:
                        var opacity = F32();
                        layer.Opacity = float.IsFinite(opacity) ? Math.Clamp(opacity, 0, 1) : 1;
                        continue;
                    case PropVisible:
                        layer.Visible = U32() != 0;
                        continue;
                    case PropOffsets:
                        layer.X = I32();
                        layer.Y = I32();
                        continue;
                    case PropMode:
                        mode = (int)Math.Min(U32(), int.MaxValue);
                        continue;
                    case PropBlendSpace:
                        blendSpace = I32();
                        continue;
                    case PropCompositeSpace:
                        compositeSpace = I32();
                        continue;
                    case PropCompositeMode:
                        composite = I32();
                        continue;
                    case PropApplyMask:
                        applyMask = U32() != 0;
                        continue;
                    case PropGroupItem:
                        group = true;
                        Seek(start + length);
                        continue;
                    case PropItemPath:
                        if (length % 4 != 0 || length / 4 > MaxDepth)
                            throw Damaged;
                        for (var i = 0; i < length / 4; i++)
                            path.Add(U32());
                        continue;
                    case PropFloatingSelection:
                        floating = true;
                        Seek(start + length);
                        continue;
                    default:
                        Seek(start + length);
                        continue;
                }
                break;
            }
            var hierarchy = Pointer();
            var mask = Pointer();

            // A floating selection pasted into a channel or a mask is grayscale whatever the picture; it isn't drawn.
            if (type / 2 != _baseType)
                return floating ? null : throw Damaged;
            layer.Mode = XcfBlend.Resolve(mode, blendSpace, compositeSpace, composite);
            if (group)
                layer.Children = [];
            return new Header(layer, type, hierarchy, mask, applyMask, path);
        }

        /// <summary>
        /// Refuses a picture whose layers and masks claim more pixels than Filee reads, more tiles than the file has room
        /// to point at, or more merging than Filee does, before any pixel is allocated: a small file can't claim a huge
        /// canvas, thousands of large layers, a huge group mask or thousands of empty groups over a large canvas.
        /// </summary>
        private void CheckBudget(List<Header> headers, long canvas)
        {
            long pixels = 0, tiles = 0, shown = 0;
            foreach (var header in headers)
            {
                var layer = header.Layer;
                var area = (long)layer.Width * layer.Height;
                var layerTiles = (long)((layer.Width + Tile - 1) / Tile) * ((layer.Height + Tile - 1) / Tile);
                // A layer's pixels take four bytes each, a mask's one; a group's own pixels aren't read, its mask is.
                if (layer.Children is null)
                {
                    pixels += area;
                    tiles += layerTiles;
                }
                if (header.Mask != 0)
                {
                    pixels += area / 4;
                    tiles += layerTiles;
                }
                if (layer.Visible)
                    shown++;
            }
            if (pixels > MaxPixels || canvas * shown > MaxMergeWork)
                throw new InvalidDataException("This GIMP picture is too large to convert.");
            if (tiles * PointerSize > file.Length)
                throw Damaged;
        }

        /// <summary>
        /// The layers as GIMP nests them, each list bottom first. The file lists every layer top first, a group before
        /// its layers, each with its path of positions from the top level (XCF 3 and later).
        /// </summary>
        private static List<Layer> BuildTree(List<Header> headers)
        {
            var top = new List<Layer>();
            var groups = new Dictionary<string, Layer>();
            foreach (var header in headers)
            {
                // A top-level layer may have no path: its place among the top-level layers.
                var path = header.Path.Count > 0 ? header.Path : [(uint)top.Count];
                var parent = path.Count == 1 ? top
                    : groups.TryGetValue(string.Join('/', path.Take(path.Count - 1)), out var group) ? group.Children!
                    : throw Damaged;
                parent.Add(header.Layer);
                if (header.Layer.Children is not null)
                    groups[string.Join('/', path)] = header.Layer;
            }
            Reverse(top);
            return top;

            static void Reverse(List<Layer> layers)
            {
                layers.Reverse();
                foreach (var layer in layers)
                {
                    if (layer.Children is { } children)
                        Reverse(children);
                }
            }
        }

        private void ReadPixels(Header header)
        {
            var layer = header.Layer;
            // A group's pixels are GIMP's cache of its result, made again from its layers.
            if (layer.Children is null)
            {
                var channels = header.Type switch { 0 => 3, 1 => 4, 2 => 1, 3 => 2, 4 => 1, _ => 2 };
                var bgra = new byte[layer.Width * layer.Height * 4];
                ReadHierarchy(header.Hierarchy, layer.Width, layer.Height, channels, (tile, x0, y0, w, h) =>
                    StorePixels(tile, x0, y0, w, h, channels, layer.Width, bgra));
                layer.Bgra = bgra;
            }
            if (header.Mask != 0 && header.ApplyMask)
                layer.Mask = ReadMask(header.Mask, layer.Width, layer.Height);
        }

        /// <summary>A layer mask: a channel the size of its layer, read as one byte per pixel (masks are linear).</summary>
        private byte[] ReadMask(long pointer, int width, int height)
        {
            Seek(pointer);
            if (U32() != width || U32() != height)
                throw Damaged;
            Text(keep: 0);
            while (true)
            {
                var (property, length) = (U32(), U32());
                if (property == PropEnd)
                    break;
                Seek(file.Position + length);
            }
            var hierarchy = Pointer();
            var mask = new byte[width * height];
            ReadHierarchy(hierarchy, width, height, 1, (tile, x0, y0, w, h) =>
            {
                var step = _precision.Bytes;
                for (var y = 0; y < h; y++)
                {
                    for (var x = 0; x < w; x++)
                        mask[(y0 + y) * width + x0 + x] = ToByte(Value(tile, ((y * w) + x) * step), false);
                }
            });
            return mask;
        }

        private delegate void TileSink(byte[] tile, int x, int y, int width, int height);

        /// <summary>
        /// The pixels of a hierarchy's first level, tile by tile, each in the order the file stores its pixels, with
        /// <paramref name="channels"/> values of the picture's precision per pixel.
        /// </summary>
        private void ReadHierarchy(long pointer, int width, int height, int channels, TileSink sink)
        {
            Seek(pointer);
            var bpp = channels * _precision.Bytes;
            if (U32() != width || U32() != height || U32() != bpp)
                throw Damaged;
            Seek(Pointer());
            if (U32() != width || U32() != height)
                throw Damaged;
            var columns = (width + Tile - 1) / Tile;
            var rows = (height + Tile - 1) / Tile;
            var tiles = new long[columns * rows];
            for (var i = 0; i < tiles.Length; i++)
            {
                tiles[i] = Pointer();
                if (tiles[i] == 0 || tiles[i] >= file.Length)
                    throw Damaged;
            }
            var next = Pointer();

            // GIMP never stores a compressed tile larger than one and a half times its pixels.
            var maxPacked = Tile * Tile * bpp * 3 / 2;
            var tile = new byte[Tile * Tile * bpp];
            var packed = _compression == 0 ? [] : new byte[maxPacked];
            for (var i = 0; i < tiles.Length; i++)
            {
                var x0 = i % columns * Tile;
                var y0 = i / columns * Tile;
                var w = Math.Min(Tile, width - x0);
                var h = Math.Min(Tile, height - y0);
                var size = w * h * bpp;
                Seek(tiles[i]);
                if (_compression == 0)
                {
                    file.ReadExactly(tile, 0, size);
                }
                else
                {
                    var end = i + 1 < tiles.Length ? tiles[i + 1] : next;
                    var available = end > tiles[i] ? end - tiles[i] : file.Length - tiles[i];
                    var length = (int)Math.Min(Math.Min(available, maxPacked), file.Length - tiles[i]);
                    file.ReadExactly(packed, 0, length);
                    if (_compression == 1)
                        UnpackRle(packed, length, tile, w * h, bpp);
                    else
                        Inflate(packed, length, tile, size);
                }
                sink(tile, x0, y0, w, h);
            }
        }

        /// <summary>
        /// XCF's RLE: one stream per byte of a pixel (all the first bytes, then all the second, …), each a series of
        /// runs of one value and of literal bytes, with short and long forms.
        /// </summary>
        private static void UnpackRle(byte[] packed, int length, byte[] tile, int pixels, int bpp)
        {
            var p = 0;
            for (var plane = 0; plane < bpp; plane++)
            {
                var i = 0;
                while (i < pixels)
                {
                    if (p >= length)
                        throw Damaged;
                    int op = packed[p++];
                    int count;
                    if (op >= 128)
                    {
                        if (op == 128)
                        {
                            if (p + 2 > length)
                                throw Damaged;
                            count = packed[p] << 8 | packed[p + 1];
                            p += 2;
                        }
                        else
                        {
                            count = 256 - op;
                        }
                        if (count > pixels - i || count > length - p)
                            throw Damaged;
                        for (var k = 0; k < count; k++)
                            tile[(i + k) * bpp + plane] = packed[p + k];
                        p += count;
                    }
                    else
                    {
                        if (op == 127)
                        {
                            if (p + 2 > length)
                                throw Damaged;
                            count = packed[p] << 8 | packed[p + 1];
                            p += 2;
                        }
                        else
                        {
                            count = op + 1;
                        }
                        if (count > pixels - i || p >= length)
                            throw Damaged;
                        var value = packed[p++];
                        for (var k = 0; k < count; k++)
                            tile[(i + k) * bpp + plane] = value;
                    }
                    i += count;
                }
            }
        }

        /// <summary>A zlib tile: exactly <paramref name="size"/> bytes, no more.</summary>
        private static void Inflate(byte[] packed, int length, byte[] tile, int size)
        {
            try
            {
                using var zlib = new ZLibStream(new MemoryStream(packed, 0, length), CompressionMode.Decompress);
                zlib.ReadExactly(tile, 0, size);
                if (zlib.ReadByte() != -1)
                    throw Damaged;
            }
            catch (InvalidDataException ex) when (ex != Damaged)
            {
                throw new InvalidDataException(Damaged.Message, ex);
            }
        }

        /// <summary>One tile of a layer into its BGRA pixels, from RGB, grayscale or indexed values.</summary>
        private void StorePixels(byte[] tile, int x0, int y0, int w, int h, int channels, int layerWidth, byte[] bgra)
        {
            var step = _precision.Bytes;
            var bpp = channels * step;
            var linear = _precision.Linear;
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var source = (y * w + x) * bpp;
                    var target = ((y0 + y) * layerWidth + x0 + x) * 4;
                    byte r, g, b, a = 255;
                    if (_baseType == 2)
                    {
                        // Indexed: an index into the colormap; GIMP treats alpha below half as transparent.
                        var index = tile[source] * 3;
                        (r, g, b) = index + 2 < _colormap.Length ? (_colormap[index], _colormap[index + 1], _colormap[index + 2]) : ((byte)0, (byte)0, (byte)0);
                        if (channels == 2)
                            a = tile[source + 1] >= 128 ? (byte)255 : (byte)0;
                    }
                    else if (channels <= 2)
                    {
                        r = g = b = ToByte(Value(tile, source), linear);
                        if (channels == 2)
                            a = ToByte(Value(tile, source + step), false);
                    }
                    else
                    {
                        r = ToByte(Value(tile, source), linear);
                        g = ToByte(Value(tile, source + step), linear);
                        b = ToByte(Value(tile, source + 2 * step), linear);
                        if (channels == 4)
                            a = ToByte(Value(tile, source + 3 * step), false);
                    }
                    bgra[target] = b;
                    bgra[target + 1] = g;
                    bgra[target + 2] = r;
                    bgra[target + 3] = a;
                }
            }
        }

        /// <summary>
        /// One stored value, 0 to 1. Pixels are big-endian from XCF 12; the GIMP 2.9 development versions that wrote
        /// more than 8 bits before that used the computer's order, little-endian on every PC.
        /// </summary>
        private float Value(byte[] tile, int offset)
        {
            var bigEndian = _version >= 12;
            var span = tile.AsSpan(offset);
            return (_precision.Bytes, _precision.Float) switch
            {
                (1, _) => tile[offset] / 255f,
                (2, false) => (bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(span) : BinaryPrimitives.ReadUInt16LittleEndian(span)) / 65535f,
                (2, true) => (float)(bigEndian ? BinaryPrimitives.ReadHalfBigEndian(span) : BinaryPrimitives.ReadHalfLittleEndian(span)),
                (4, false) => (float)((bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(span) : BinaryPrimitives.ReadUInt32LittleEndian(span)) / 4294967295.0),
                (4, true) => bigEndian ? BinaryPrimitives.ReadSingleBigEndian(span) : BinaryPrimitives.ReadSingleLittleEndian(span),
                _ => (float)(bigEndian ? BinaryPrimitives.ReadDoubleBigEndian(span) : BinaryPrimitives.ReadDoubleLittleEndian(span)),
            };
        }

        /// <summary>A value as an 8-bit sRGB-encoded byte; linear colour is encoded first, alpha and masks are not.</summary>
        private static byte ToByte(float value, bool linearColour)
        {
            if (linearColour)
                value = XcfBlend.ToPerceptual(value);
            return float.IsNaN(value) ? (byte)0 : (byte)(Math.Clamp(value, 0, 1) * 255 + 0.5f);
        }

        private int PointerSize => _version >= 11 ? 8 : 4;

        private long Pointer()
        {
            var pointer = _version >= 11 ? (long)Math.Min(U64(), long.MaxValue) : U32();
            return pointer <= file.Length ? pointer : throw Damaged;
        }

        private void Seek(long position)
        {
            if (position < 0 || position > file.Length)
                throw Damaged;
            file.Position = position;
        }

        private byte[] Bytes(int count)
        {
            if (count > file.Length - file.Position)
                throw Damaged;
            var bytes = new byte[count];
            file.ReadExactly(bytes);
            return bytes;
        }

        private uint U32()
        {
            Span<byte> bytes = stackalloc byte[4];
            file.ReadExactly(bytes);
            return BinaryPrimitives.ReadUInt32BigEndian(bytes);
        }

        private ulong U64()
        {
            Span<byte> bytes = stackalloc byte[8];
            file.ReadExactly(bytes);
            return BinaryPrimitives.ReadUInt64BigEndian(bytes);
        }

        private int I32() => unchecked((int)U32());

        private float F32() => BitConverter.Int32BitsToSingle(I32());

        /// <summary>
        /// A string: its byte count with the closing zero, then UTF-8. Only the first <paramref name="keep"/> bytes are
        /// decoded (a whole character at the end), the rest is skipped.
        /// </summary>
        private string Text(int keep = MaxNameBytes)
        {
            var length = U32();
            if (length == 0)
                return "";
            if (length > 64 * 1024 || length > file.Length - file.Position)
                throw Damaged;
            var end = file.Position + length;
            var bytes = Bytes((int)Math.Min(length - 1, keep));
            Seek(end);
            var count = bytes.Length;
            // When the name was cut, drop a last character whose bytes were not all kept (UTF-8 self-synchronises).
            if (count > 0 && count < length - 1)
            {
                var lead = count - 1;
                while (lead > 0 && (bytes[lead] & 0xC0) == 0x80)
                    lead--;
                var expected = bytes[lead] switch { < 0x80 => 1, < 0xE0 => 2, < 0xF0 => 3, _ => 4 };
                if (lead + expected > count)
                    count = lead;
            }
            return Encoding.UTF8.GetString(bytes, 0, count).TrimEnd('\0');
        }
    }

    // ───────────────────────── Merging ─────────────────────────

    /// <summary>The picture as GIMP shows it: every visible layer merged.</summary>
    public static MagickImage ReadFlattened(string path, CancellationToken ct = default)
    {
        var picture = Open(path);
        var image = FromBgra(Merge(picture, picture.Layers, ct), picture.Width, picture.Height);
        if (picture.Grayscale)
            image.ColorType = ColorType.GrayscaleAlpha;
        return image;
    }

    /// <summary>
    /// <paramref name="layers"/> (bottom first) merged on a transparent canvas as GIMP merges a picture or a group, as
    /// BGRA.
    /// </summary>
    internal static byte[] Merge(Picture picture, List<Layer> layers, CancellationToken ct = default)
    {
        var width = picture.Width;
        var canvas = new byte[width * picture.Height * 4];
        var merger = new Merger(picture, layers);
        var row = new float[width * 4];
        for (var y = 0; y < picture.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            Array.Clear(row);
            var space = XcfBlend.Space.Perceptual;
            merger.Draw(layers, y, row, ref space, depth: 0);
            XcfBlend.Convert(row, space, XcfBlend.Space.Perceptual);
            var offset = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var i = x * 4;
                canvas[offset + i] = ToByte(row[i + 2]);
                canvas[offset + i + 1] = ToByte(row[i + 1]);
                canvas[offset + i + 2] = ToByte(row[i]);
                canvas[offset + i + 3] = ToByte(row[i + 3]);
            }
        }
        return canvas;
    }

    private static byte ToByte(float value) => float.IsNaN(value) ? (byte)0 : (byte)(Math.Clamp(value, 0, 1) * 255 + 0.5f);

    /// <summary>A rectangle on the canvas, right and bottom excluded; empty when it has no area.</summary>
    private readonly record struct Box(long Left, long Top, long Right, long Bottom)
    {
        public static readonly Box None = new(0, 0, 0, 0);

        public bool IsEmpty => Right <= Left || Bottom <= Top;

        public bool Contains(Box other) => other.IsEmpty
            || !IsEmpty && Left <= other.Left && Top <= other.Top && Right >= other.Right && Bottom >= other.Bottom;

        public static Box Of(Layer layer) => new(layer.X, layer.Y, (long)layer.X + layer.Width, (long)layer.Y + layer.Height);

        public Box Intersect(Box other) => IsEmpty || other.IsEmpty ? None
            : new Box(Math.Max(Left, other.Left), Math.Max(Top, other.Top), Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom)) is var box && !box.IsEmpty ? box : None;

        public Box Union(Box other) => IsEmpty ? other : other.IsEmpty ? this
            : new Box(Math.Min(Left, other.Left), Math.Min(Top, other.Top), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
    }

    /// <summary>
    /// How GIMP draws a layer in its stack, worked out once for every row: GIMP keeps the box of what has been drawn so
    /// far, and a layer over an empty box is drawn as it is, at its opacity, whatever its mode (the bottom layer, or the
    /// first one over layers that drew nothing). Nothing is drawn outside the box after a layer.
    /// </summary>
    private readonly record struct Step(bool OverNothing, Box After, bool Clips);

    /// <summary>Merges layers one canvas row at a time, a row per group being merged.</summary>
    private sealed class Merger
    {
        private readonly Picture _picture;
        private readonly int _width;
        private readonly XcfBlend.Scratch _scratch;
        private readonly float[] _layer;
        private readonly float[] _mask;
        private readonly List<float[]> _groups = [];
        private readonly Dictionary<Layer, Step> _steps = new(ReferenceEqualityComparer.Instance);

        public Merger(Picture picture, List<Layer> layers)
        {
            _picture = picture;
            _width = picture.Width;
            _scratch = new XcfBlend.Scratch(_width);
            _layer = new float[_width * 4];
            _mask = new float[_width];
            Reduce(layers);
            Plan(layers, Box.None);
        }

        private readonly Dictionary<Layer, XcfBlend.Mode> _modes = new(ReferenceEqualityComparer.Instance);

        /// <summary>The mode a layer is drawn in: its own, or the one a pass-through group is reduced to.</summary>
        private XcfBlend.Mode ModeOf(Layer layer) => _modes.TryGetValue(layer, out var mode) ? mode : layer.Mode;

        /// <summary>
        /// GIMP draws a pass-through group as an ordinary group, in the mode of its layers, when it has no shown layer,
        /// one, or only Normal ones drawn alike, and is opaque without a mask or composites as they do ("strength
        /// reduction"). It looks the same in theory, but the group is then merged on its own (at the picture's precision)
        /// and a legacy mode mixes alpha with the group's.
        /// </summary>
        private void Reduce(List<Layer> layers)
        {
            foreach (var layer in layers)
            {
                if (layer.Children is not { } children)
                    continue;
                Reduce(children);
                if (layer.Mode.Id != XcfBlend.PassThrough)
                    continue;
                var shown = children.Where(c => c.Visible).Select(c => Plain(ModeOf(c))).ToList();
                var mode = layer.Mode with { Id = XcfBlend.Normal };
                if (shown.Count > 0)
                {
                    mode = shown[0];
                    if (shown.Count > 1 && (mode.Id != XcfBlend.Normal || mode.Composite != XcfBlend.Composite.Union
                                                                       || shown.Skip(1).Any(m => m != mode)))
                        continue;
                    if ((layer.Opacity < 1 || layer.Mask is not null) && mode.CompositeSpace != layer.Mode.CompositeSpace)
                        continue;
                }
                _modes[layer] = mode;
            }

            static XcfBlend.Mode Plain(XcfBlend.Mode mode) => mode.Id == XcfBlend.NormalLegacy ? mode with { Id = XcfBlend.Normal } : mode;
        }

        /// <summary>The steps of a stack drawn over <paramref name="box"/>; returns the box after its top layer.</summary>
        private Box Plan(List<Layer> layers, Box box)
        {
            foreach (var layer in layers.Where(l => l.Visible))
            {
                var overNothing = box.IsEmpty;
                Box source;
                if (layer.Children is { } children)
                    source = ModeOf(layer).Id == XcfBlend.PassThrough && !overNothing ? Plan(children, box) : PlanIsolated(children, layer);
                else
                    source = Box.Of(layer);

                // Which of the layer and the backdrop the result covers (GIMP's included region).
                var (withLayer, withBackdrop) = overNothing ? (true, false)
                    : ModeOf(layer).Id == XcfBlend.PassThrough ? (true, true)
                    : ModeOf(layer).Composite switch
                    {
                        XcfBlend.Composite.Union => (true, true),
                        XcfBlend.Composite.ClipToBackdrop => (false, true),
                        XcfBlend.Composite.ClipToLayer => (true, false),
                        _ => (false, false),
                    };
                if (layer.Opacity <= 0)
                    withLayer = false;
                var after = source.Intersect(box);
                if (withLayer)
                    after = after.Union(source);
                if (withBackdrop)
                    after = after.Union(box);
                // What was drawn lies in the box before and, where the layer adds to it, in the layer's box: only a box
                // that leaves some of that out (clip to layer, intersection) has anything to clear.
                var clips = !after.Contains(box) || withLayer && !after.Contains(source);
                _steps[layer] = new Step(overNothing, after, clips);
                box = after;
            }
            return box;
        }

        private Box PlanIsolated(List<Layer> children, Layer group)
        {
            Plan(children, Box.None);
            return Box.Of(group);
        }

        public void Draw(List<Layer> layers, int y, float[] row, ref XcfBlend.Space space, int depth)
        {
            foreach (var layer in layers)
            {
                if (!layer.Visible)
                    continue;
                var step = _steps[layer];
                DrawLayer(layer, step.OverNothing, y, row, ref space, depth);
                if (step.Clips)
                    Clip(row, step.After, y);
            }
        }

        /// <summary>
        /// Draws a layer and leaves the row in the space GIMP leaves the layer's result in, on every row whether the
        /// layer covers it or not: its mode's composite space; over nothing, a Normal layer's own pixels' space, and a
        /// pass-through group's the one its layers left.
        /// </summary>
        private void DrawLayer(Layer layer, bool overNothing, int y, float[] row, ref XcfBlend.Space space, int depth)
        {
            DrawPixels(layer, overNothing, y, row, ref space, depth);
            if (overNothing && ModeOf(layer).Id == XcfBlend.PassThrough)
                return;
            var result = overNothing && ModeOf(layer).Id is XcfBlend.Normal or XcfBlend.NormalLegacy
                ? _picture.StoredSpace
                : ModeOf(layer).CompositeSpace;
            XcfBlend.Convert(row, space, result);
            space = result;
        }

        private void DrawPixels(Layer layer, bool overNothing, int y, float[] row, ref XcfBlend.Space space, int depth)
        {
            // Over nothing, a layer is copied at its opacity: Normal over a transparent row gives the same.
            var mode = overNothing ? XcfBlend.Resolve(XcfBlend.Normal, 0, 0, 0) : ModeOf(layer);
            if (y < layer.Y || y >= (long)layer.Y + layer.Height)
                return;
            var from = Math.Max(0, layer.X);
            var to = (int)Math.Min(_width, (long)layer.X + layer.Width);
            if (to <= from)
                return;
            var mask = MaskRow(layer, y, from, to);

            if (layer.Children is { } children)
            {
                var group = GroupRow(depth);
                var groupSpace = XcfBlend.Space.Perceptual;
                if (ModeOf(layer).Id == XcfBlend.PassThrough && overNothing)
                {
                    // Nothing under it: its layers are drawn straight onto the empty row, then take its opacity.
                    Draw(children, y, row, ref space, depth + 1);
                    for (var x = from; x < to; x++)
                        row[x * 4 + 3] *= layer.Opacity * (mask.IsEmpty ? 1 : mask[x - from]);
                    return;
                }
                if (ModeOf(layer).Id == XcfBlend.PassThrough)
                {
                    // Its layers are drawn onto what is under the group, then mixed back at the group's opacity, in
                    // the group's composite space.
                    Array.Copy(row, group, row.Length);
                    groupSpace = space;
                    Draw(children, y, group, ref groupSpace, depth + 1);
                    var mix = ModeOf(layer).CompositeSpace;
                    XcfBlend.Convert(group, groupSpace, mix);
                    XcfBlend.Convert(row, space, mix);
                    space = mix;
                    XcfBlend.Replace(row, group, layer.Opacity, mask, from, to);
                    return;
                }
                Array.Clear(group);
                Draw(children, y, group, ref groupSpace, depth + 1);
                // GIMP keeps a group's result at the picture's precision: at 8 bits, it is rounded to 8 bits.
                if (_picture.GroupsAtEightBits is { } stored)
                {
                    var span = group.AsSpan(from * 4, (to - from) * 4);
                    XcfBlend.Convert(span, groupSpace, stored);
                    groupSpace = stored;
                    for (var i = 0; i < span.Length; i++)
                        span[i] = ToByte(span[i]) / 255f;
                }
                XcfBlend.Draw(mode, row, ref space, group, groupSpace, layer.Opacity, mask, from, to, _scratch);
                return;
            }

            var pixels = layer.Bgra!;
            var source = ((y - layer.Y) * layer.Width + from - layer.X) * 4;
            for (var x = from; x < to; x++, source += 4)
            {
                var i = x * 4;
                _layer[i] = pixels[source + 2] / 255f;
                _layer[i + 1] = pixels[source + 1] / 255f;
                _layer[i + 2] = pixels[source] / 255f;
                _layer[i + 3] = pixels[source + 3] / 255f;
            }
            XcfBlend.Draw(mode, row, ref space, _layer, XcfBlend.Space.Perceptual, layer.Opacity, mask, from, to, _scratch);
        }

        /// <summary>Clears the row outside <paramref name="box"/>.</summary>
        private void Clip(float[] row, Box box, int y)
        {
            if (box.IsEmpty || y < box.Top || y >= box.Bottom)
            {
                Array.Clear(row);
                return;
            }
            var left = (int)Math.Clamp(box.Left, 0, _width);
            var right = (int)Math.Clamp(box.Right, 0, _width);
            Array.Clear(row, 0, left * 4);
            Array.Clear(row, right * 4, (_width - right) * 4);
        }

        /// <summary>The layer mask's values for the row's pixels <paramref name="from"/> to <paramref name="to"/>.</summary>
        private ReadOnlySpan<float> MaskRow(Layer layer, int y, int from, int to)
        {
            if (layer.Mask is not { } mask)
                return [];
            var source = (y - layer.Y) * layer.Width + from - layer.X;
            for (var x = 0; x < to - from; x++)
                _mask[x] = mask[source + x] / 255f;
            return _mask.AsSpan(0, to - from);
        }

        private float[] GroupRow(int depth)
        {
            while (_groups.Count <= depth)
                _groups.Add(new float[_width * 4]);
            return _groups[depth];
        }
    }

    private static MagickImage FromBgra(byte[] bgra, int width, int height)
    {
        var image = new MagickImage();
        image.ReadPixels(bgra, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.BGRA));
        return image;
    }

    // ───────────────────────── Layers for PSD and Paint.NET ─────────────────────────

    /// <summary>A layer for a format without groups: canvas-sized BGRA with its layer mask applied.</summary>
    internal sealed record FlatLayer(string Name, byte[] Bgra, bool Visible, float Opacity, int Mode);

    /// <summary>
    /// The layers for PSD and Paint.NET, which have no groups, bottom first. A group whose layers look the same drawn
    /// straight onto the picture (a pass-through group, or a Normal one of Normal layers, at full opacity and without a
    /// mask) gives its layers, named "Group / Layer"; any other group becomes one layer of its merged result, so the
    /// picture stays as GIMP shows it.
    /// </summary>
    internal static List<FlatLayer> FlatLayers(Picture picture, CancellationToken ct = default)
    {
        var flat = new List<FlatLayer>();
        Add(picture.Layers, "", true, 1);
        return flat;

        void Add(List<Layer> layers, string prefix, bool visible, float opacity)
        {
            foreach (var layer in layers)
            {
                ct.ThrowIfCancellationRequested();
                var shown = visible && layer.Visible;
                if (layer.Children is { } children)
                {
                    if (KeepsLook(layer))
                    {
                        Add(children, prefix + layer.Name + " / ", shown, opacity * layer.Opacity);
                        continue;
                    }
                    var merged = Merge(picture, [Alone(layer)], ct);
                    ApplyMask(picture, layer, merged);
                    var mode = layer.Mode.Id == XcfBlend.PassThrough ? XcfBlend.Normal : layer.Mode.Id;
                    flat.Add(new FlatLayer(prefix + layer.Name, merged, shown, opacity * layer.Opacity, mode));
                    continue;
                }
                flat.Add(new FlatLayer(prefix + layer.Name, OnCanvas(picture, layer), shown, opacity * layer.Opacity, layer.Mode.Id));
            }
        }

        // The group alone, merged as on a picture of its own: shown, opaque, Normal, without its mask.
        static Layer Alone(Layer group) => new()
        {
            Name = group.Name,
            X = group.X,
            Y = group.Y,
            Width = group.Width,
            Height = group.Height,
            Children = group.Children,
        };
    }

    /// <summary>How many layers <see cref="FlatLayers"/> gives, counted without making them.</summary>
    private static long FlatCount(List<Layer> layers) =>
        layers.Sum(l => l.Children is { } children && KeepsLook(l) ? FlatCount(children) : 1L);

    /// <summary>
    /// The layers for PSD and Paint.NET, or when each on a canvas of its own would take more than Paint.NET's pixel
    /// budget (canvas × layers, as <see cref="PaintNet"/> reads them), the merged picture as one layer named
    /// Background: the conversion still works, and Filee can read the result back.
    /// </summary>
    private static List<FlatLayer> LayersOrMerged(Picture picture, Func<byte[]> merged, CancellationToken ct) =>
        (long)picture.Width * picture.Height * Math.Max(1, FlatCount(picture.Layers)) <= PaintNet.MaxPixels
            ? FlatLayers(picture, ct)
            : [new FlatLayer("Background", merged(), true, 1, XcfBlend.Normal)];

    /// <summary>
    /// Whether a group's layers, drawn without it, give the same picture: a pass-through group at full opacity, or a
    /// Normal one whose layers are all Normal, at full opacity unless it holds one layer. Neither may have a mask.
    /// </summary>
    private static bool KeepsLook(Layer group)
    {
        if (group.Mask is not null)
            return false;
        if (group.Mode.Id == XcfBlend.PassThrough)
            return group.Opacity >= 1;
        var shown = group.Children!.Where(l => l.Visible).ToList();
        return IsNormal(group.Mode) && AllNormal(shown) && (group.Opacity >= 1 || shown is [{ Children: null }]);

        // Every shown layer drawn Normal, in groups that keep their look themselves.
        // KeepsLook already checks a Normal group's layers; a pass-through group's are checked here, each layer once.
        static bool AllNormal(IEnumerable<Layer> layers) => layers.Where(l => l.Visible).All(l =>
            l.Children is null ? IsNormal(l.Mode) : KeepsLook(l) && (l.Mode.Id != XcfBlend.PassThrough || AllNormal(l.Children)));

        static bool IsNormal(XcfBlend.Mode mode) =>
            mode.Id is XcfBlend.Normal or XcfBlend.NormalLegacy && mode.Composite == XcfBlend.Composite.Union;
    }

    /// <summary>A layer's pixels at its place on the canvas, its mask applied; what lies outside the canvas is cut off.</summary>
    internal static byte[] OnCanvas(Picture picture, Layer layer)
    {
        var canvas = new byte[picture.Width * picture.Height * 4];
        var pixels = layer.Bgra!;
        var (from, to) = (Math.Max(0, -layer.X), (int)Math.Min(layer.Width, (long)picture.Width - layer.X));
        for (var y = Math.Max(0, -layer.Y); to > from && y < layer.Height && (long)layer.Y + y < picture.Height; y++)
            Array.Copy(pixels, (y * layer.Width + from) * 4, canvas, ((layer.Y + y) * picture.Width + layer.X + from) * 4, (to - from) * 4);
        ApplyMask(picture, layer, canvas);
        return canvas;
    }

    /// <summary>
    /// Multiplies the alpha of <paramref name="canvas"/> (the layer's pixels on the canvas) by the layer's mask; nothing
    /// shows outside the mask's extent.
    /// </summary>
    private static void ApplyMask(Picture picture, Layer layer, byte[] canvas)
    {
        if (layer.Mask is not { } mask)
            return;
        for (var y = 0; y < picture.Height; y++)
        {
            for (var x = 0; x < picture.Width; x++)
            {
                var i = (y * picture.Width + x) * 4 + 3;
                long mx = x - layer.X, my = y - layer.Y;
                var value = mx >= 0 && mx < layer.Width && my >= 0 && my < layer.Height ? mask[my * layer.Width + mx] : 0;
                canvas[i] = (byte)((canvas[i] * value + 127) / 255);
            }
        }
    }

    /// <summary>
    /// Writes the .xcf as a PSD (or PSB) with its layers: names (as Unicode too), opacity, visibility and the blend
    /// modes Photoshop shares; groups as <see cref="FlatLayers"/> gives them. The merged picture goes first.
    /// </summary>
    public static IReadOnlyList<string> SaveLayeredPsd(ConversionStep step, IProgress<double>? progress, CancellationToken ct)
    {
        var picture = Open(step.InputPath);
        var merged = Merge(picture, picture.Layers, ct);
        progress?.Report(0.3);
        var layers = LayersOrMerged(picture, () => merged, ct).Select(l =>
        {
            ct.ThrowIfCancellationRequested();
            return new PaintNet.PsdLayer(l.Name, l.Bgra, l.Visible, OpacityByte(l.Opacity), ComposeOf(l.Mode));
        });
        return PaintNet.WritePsd(step, picture.Width, picture.Height, layers, () => merged, progress);
    }

    /// <summary>
    /// Writes the .xcf as a Paint.NET file with its layers: names, opacity, visibility and the blend modes Paint.NET
    /// shares (others are Normal); groups as <see cref="FlatLayers"/> gives them.
    /// </summary>
    public static IReadOnlyList<string> SaveLayeredPdn(ConversionStep step, IProgress<double>? progress, CancellationToken ct)
    {
        var picture = Open(step.InputPath);
        if (picture.Width > PaintNetWriter.MaxSide || picture.Height > PaintNetWriter.MaxSide)
            throw new InvalidOperationException(
                $"Paint.NET opens pictures of at most {PaintNetWriter.MaxSide:N0} pixels on a side; this one is {picture.Width:N0} × {picture.Height:N0}.");
        progress?.Report(0.3);
        var layers = new List<PaintNetWriter.Layer>();
        foreach (var layer in LayersOrMerged(picture, () => Merge(picture, picture.Layers, ct), ct))
        {
            ct.ThrowIfCancellationRequested();
            layers.Add(new PaintNetWriter.Layer(layer.Name, layer.Bgra, layer.Visible, OpacityByte(layer.Opacity), BlendModeOf(layer.Mode)));
        }
        if (layers.Count == 0)
            layers.Add(new PaintNetWriter.Layer("Background", new byte[picture.Width * picture.Height * 4]));
        progress?.Report(0.7);
        var path = step.Output.Allocate("pdn");
        if (path is null)
            return [];
        PaintNetWriter.Write(path, picture.Width, picture.Height, layers);
        progress?.Report(1);
        return [path];
    }

    private static byte OpacityByte(float opacity) => (byte)(Math.Clamp(opacity, 0, 1) * 255 + 0.5f);

    /// <summary>The Photoshop blend mode of a GIMP layer mode, as ImageMagick names it; Over where Photoshop has none.</summary>
    internal static CompositeOperator ComposeOf(int mode) => mode switch
    {
        XcfBlend.Dissolve => CompositeOperator.Dissolve,
        XcfBlend.MultiplyLegacy or XcfBlend.Multiply => CompositeOperator.Multiply,
        XcfBlend.ScreenLegacy or XcfBlend.Screen => CompositeOperator.Screen,
        XcfBlend.Overlay => CompositeOperator.Overlay,
        XcfBlend.DifferenceLegacy or XcfBlend.Difference => CompositeOperator.Difference,
        XcfBlend.AdditionLegacy or XcfBlend.Addition => CompositeOperator.LinearDodge,
        XcfBlend.DarkenLegacy or XcfBlend.Darken => CompositeOperator.Darken,
        XcfBlend.LightenLegacy or XcfBlend.Lighten => CompositeOperator.Lighten,
        XcfBlend.HueLegacy or XcfBlend.HsvHue or XcfBlend.LchHue => CompositeOperator.Hue,
        XcfBlend.SaturationLegacy or XcfBlend.HsvSaturation or XcfBlend.LchChroma => CompositeOperator.Saturate,
        XcfBlend.ColorLegacy or XcfBlend.HslColor or XcfBlend.LchColor => CompositeOperator.Colorize,
        XcfBlend.ValueLegacy or XcfBlend.HsvValue or XcfBlend.LchLightness or XcfBlend.Luminance => CompositeOperator.Luminize,
        XcfBlend.DodgeLegacy or XcfBlend.Dodge => CompositeOperator.ColorDodge,
        XcfBlend.BurnLegacy or XcfBlend.Burn => CompositeOperator.ColorBurn,
        XcfBlend.HardLightLegacy or XcfBlend.HardLight => CompositeOperator.HardLight,
        XcfBlend.OverlayLegacy or XcfBlend.SoftLightLegacy or XcfBlend.SoftLight => CompositeOperator.SoftLight,
        XcfBlend.VividLight => CompositeOperator.VividLight,
        XcfBlend.PinLight => CompositeOperator.PinLight,
        XcfBlend.LinearLight => CompositeOperator.LinearLight,
        XcfBlend.HardMix => CompositeOperator.HardMix,
        XcfBlend.Exclusion => CompositeOperator.Exclusion,
        XcfBlend.LinearBurn => CompositeOperator.LinearBurn,
        _ => CompositeOperator.Over,
    };

    /// <summary>The Paint.NET blend mode of a GIMP layer mode; Normal where Paint.NET has none.</summary>
    internal static PaintNet.BlendMode BlendModeOf(int mode) => mode switch
    {
        XcfBlend.MultiplyLegacy or XcfBlend.Multiply => PaintNet.BlendMode.Multiply,
        XcfBlend.AdditionLegacy or XcfBlend.Addition => PaintNet.BlendMode.Additive,
        XcfBlend.BurnLegacy or XcfBlend.Burn => PaintNet.BlendMode.ColorBurn,
        XcfBlend.DodgeLegacy or XcfBlend.Dodge => PaintNet.BlendMode.ColorDodge,
        XcfBlend.Overlay => PaintNet.BlendMode.Overlay,
        XcfBlend.DifferenceLegacy or XcfBlend.Difference => PaintNet.BlendMode.Difference,
        XcfBlend.LightenLegacy or XcfBlend.Lighten => PaintNet.BlendMode.Lighten,
        XcfBlend.DarkenLegacy or XcfBlend.Darken => PaintNet.BlendMode.Darken,
        XcfBlend.ScreenLegacy or XcfBlend.Screen => PaintNet.BlendMode.Screen,
        _ => PaintNet.BlendMode.Normal,
    };
}
