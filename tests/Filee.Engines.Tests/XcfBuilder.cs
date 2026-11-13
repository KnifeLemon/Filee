// Builds GIMP .xcf files for the tests as GIMP 2.10 and 3 lay them out: the header (with the precision from XCF 4),
// layers and groups top first with their item paths, layer masks, and 64 × 64 tiles uncompressed, RLE or zlib, 8-bit or
// 16-bit and 32-bit float linear, big-endian (XCF 12 and later), with 64-bit pointers from XCF 11.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Filee.Engines.Tests;

internal static class XcfBuilder
{
    /// <summary>
    /// A layer (RGBA pixels, sRGB-encoded, top row first) or, with <paramref name="Children"/> (top first), a group.
    /// <paramref name="Mask"/> is one byte per pixel. Modes are GIMP's numbers: 28 Normal, 30 Multiply, 3 Multiply
    /// (legacy), 61 Pass through.
    /// </summary>
    public sealed record Layer(string Name, int Width, int Height, byte[] Rgba, int X = 0, int Y = 0, float Opacity = 1,
        bool Visible = true, int Mode = 28, byte[]? Mask = null, IReadOnlyList<Layer>? Children = null);

    public enum Precision { Gamma8 = 150, Linear16 = 200, LinearFloat = 600 }

    public enum Compression { None = 0, Rle = 1, Zlib = 2 }

    /// <summary>Pixels of one colour (RGBA order) over a whole width × height layer.</summary>
    public static byte[] Fill(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (r, g, b, a);
        return pixels;
    }

    /// <summary>A group of <paramref name="children"/> (top first) at the box around them.</summary>
    public static Layer Group(string name, IReadOnlyList<Layer> children, int mode = 28, float opacity = 1, bool visible = true)
    {
        var left = children.Min(c => c.X);
        var top = children.Min(c => c.Y);
        var right = children.Max(c => c.X + c.Width);
        var bottom = children.Max(c => c.Y + c.Height);
        return new Layer(name, right - left, bottom - top, new byte[(right - left) * (bottom - top) * 4], left, top,
            opacity, visible, mode, Children: children);
    }

    /// <summary>The bytes of an .xcf of <paramref name="layers"/>, top first, as GIMP lists them.</summary>
    /// <param name="tilesWritten">False ends the file after the layer headers: tiles claimed, none stored.</param>
    public static byte[] Build(int width, int height, IReadOnlyList<Layer> layers, int version = 12,
        Precision precision = Precision.Gamma8, Compression compression = Compression.Rle, bool tilesWritten = true)
    {
        var w = new Writer(version >= 11);
        w.Bytes(Encoding.ASCII.GetBytes(version == 0 ? "gimp xcf file\0" : $"gimp xcf v{version:D3}\0"));
        w.U32(width);
        w.U32(height);
        w.U32(0); // RGB
        if (version >= 4)
            w.U32((int)precision);
        w.U32(17); w.U32(1); w.Byte((byte)compression); // PROP_COMPRESSION
        w.U32(0); w.U32(0);                               // PROP_END

        // Every layer, groups before their layers, with its path of positions.
        var all = new List<(Layer Layer, int[] Path)>();
        void Walk(IReadOnlyList<Layer> list, int[] path)
        {
            for (var i = 0; i < list.Count; i++)
            {
                all.Add((list[i], [.. path, i]));
                if (list[i].Children is { } children)
                    Walk(children, [.. path, i]);
            }
        }
        Walk(layers, []);

        var table = w.Position;
        foreach (var _ in all)
            w.Pointer(0);
        w.Pointer(0); // end of layers
        w.Pointer(0); // no channels

        for (var i = 0; i < all.Count; i++)
        {
            var (layer, path) = all[i];
            w.PatchPointer(table + i * w.PointerSize, w.Position);
            w.U32(layer.Width);
            w.U32(layer.Height);
            w.U32(1); // RGB with alpha
            w.Text(layer.Name);
            // GIMP makes the layer a group as it reads PROP_GROUP_ITEM, so that comes first, as GIMP writes it.
            if (layer.Children is not null)
            {
                w.U32(29); w.U32(0);                                       // PROP_GROUP_ITEM
            }
            if (path.Length > 1)
            {
                w.U32(30); w.U32(path.Length * 4);                         // PROP_ITEM_PATH
                foreach (var index in path)
                    w.U32(index);
            }
            w.U32(6); w.U32(4); w.U32((int)Math.Round(layer.Opacity * 255)); // PROP_OPACITY
            if (version >= 9)
            {
                w.U32(33); w.U32(4); w.F32(layer.Opacity);                 // PROP_FLOAT_OPACITY
            }
            w.U32(8); w.U32(4); w.U32(layer.Visible ? 1 : 0);              // PROP_VISIBLE
            w.U32(15); w.U32(8); w.U32(layer.X); w.U32(layer.Y);          // PROP_OFFSETS
            w.U32(7); w.U32(4); w.U32(layer.Mode);                         // PROP_MODE
            w.U32(0); w.U32(0);                                            // PROP_END
            var hierarchy = w.Position;
            w.Pointer(0);
            var mask = w.Position;
            w.Pointer(0);
            if (!tilesWritten)
            {
                // A mask is still pointed at (here, at the next layer's header): the reader checks the budget first.
                if (layer.Mask is not null)
                    w.PatchPointer(mask, w.Position);
                continue;
            }

            w.PatchPointer(hierarchy, w.Position);
            Hierarchy(w, layer.Width, layer.Height, 4, layer.Rgba, precision, compression);
            if (layer.Mask is { } values)
            {
                w.PatchPointer(mask, w.Position);
                w.U32(layer.Width);
                w.U32(layer.Height);
                w.Text(layer.Name + " mask");
                w.U32(0); w.U32(0);
                var channelHierarchy = w.Position;
                w.Pointer(0);
                w.PatchPointer(channelHierarchy, w.Position);
                Hierarchy(w, layer.Width, layer.Height, 1, values, precision, compression);
            }
        }
        return w.ToArray();
    }

    /// <summary>A hierarchy, its one level and its tiles.</summary>
    private static void Hierarchy(Writer w, int width, int height, int channels, byte[] values, Precision precision,
        Compression compression)
    {
        var bytesPerValue = precision switch { Precision.Gamma8 => 1, Precision.Linear16 => 2, _ => 4 };
        var bpp = channels * bytesPerValue;
        w.U32(width);
        w.U32(height);
        w.U32(bpp);
        var level = w.Position;
        w.Pointer(0);
        w.Pointer(0); // no smaller levels
        w.PatchPointer(level, w.Position);
        w.U32(width);
        w.U32(height);
        var columns = (width + 63) / 64;
        var rows = (height + 63) / 64;
        var tiles = w.Position;
        for (var t = 0; t <= columns * rows; t++)
            w.Pointer(0);
        for (var t = 0; t < columns * rows; t++)
        {
            w.PatchPointer(tiles + t * w.PointerSize, w.Position);
            var (x0, y0) = (t % columns * 64, t / columns * 64);
            var (tw, th) = (Math.Min(64, width - x0), Math.Min(64, height - y0));
            var tile = new byte[tw * th * bpp];
            for (var y = 0; y < th; y++)
            {
                for (var x = 0; x < tw; x++)
                {
                    for (var c = 0; c < channels; c++)
                    {
                        var value = values[((y0 + y) * width + x0 + x) * channels + c];
                        // Colour is stored linear at the linear precisions; alpha and masks are always linear.
                        var colour = channels == 4 && c < 3;
                        var target = tile.AsSpan(((y * tw + x) * channels + c) * bytesPerValue);
                        switch (precision)
                        {
                            case Precision.Gamma8:
                                target[0] = value;
                                break;
                            case Precision.Linear16:
                                BinaryPrimitives.WriteUInt16BigEndian(target, (ushort)Math.Round(Linear(value, colour) * 65535));
                                break;
                            default:
                                BinaryPrimitives.WriteSingleBigEndian(target, Linear(value, colour));
                                break;
                        }
                    }
                }
            }
            w.Bytes(compression switch
            {
                Compression.None => tile,
                Compression.Zlib => Zlib(tile),
                _ => Rle(tile, tw * th, bpp),
            });
        }
    }

    private static float Linear(byte value, bool colour)
    {
        var v = value / 255.0;
        return (float)(!colour ? v : v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4));
    }

    private static byte[] Zlib(byte[] tile)
    {
        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(tile);
        return packed.ToArray();
    }

    /// <summary>XCF's RLE, one stream per byte of a pixel: runs where a value repeats, literals elsewhere.</summary>
    private static byte[] Rle(byte[] tile, int pixels, int bpp)
    {
        var output = new List<byte>();
        for (var plane = 0; plane < bpp; plane++)
        {
            var i = 0;
            while (i < pixels)
            {
                var run = 1;
                while (i + run < pixels && tile[(i + run) * bpp + plane] == tile[i * bpp + plane])
                    run++;
                if (run > 1)
                {
                    if (run <= 127)
                        output.Add((byte)(run - 1));
                    else
                        output.AddRange([127, (byte)(run >> 8), (byte)run]);
                    output.Add(tile[i * bpp + plane]);
                    i += run;
                    continue;
                }
                var start = i;
                while (i < pixels && (i + 1 >= pixels || tile[(i + 1) * bpp + plane] != tile[i * bpp + plane]))
                    i++;
                var count = i - start;
                if (count <= 127)
                    output.Add((byte)(256 - count));
                else
                    output.AddRange([128, (byte)(count >> 8), (byte)count]);
                for (var k = start; k < i; k++)
                    output.Add(tile[k * bpp + plane]);
            }
        }
        return [.. output];
    }

    private sealed class Writer(bool wide)
    {
        private readonly MemoryStream _stream = new();

        public int PointerSize => wide ? 8 : 4;

        public int Position => (int)_stream.Position;

        public void Byte(byte value) => _stream.WriteByte(value);

        public void Bytes(ReadOnlySpan<byte> value) => _stream.Write(value);

        public void U32(int value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(buffer, value);
            _stream.Write(buffer);
        }

        public void F32(float value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteSingleBigEndian(buffer, value);
            _stream.Write(buffer);
        }

        public void Pointer(long value)
        {
            Span<byte> buffer = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(buffer, value);
            _stream.Write(wide ? buffer : buffer[4..]);
        }

        public void Text(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            U32(bytes.Length + 1);
            Bytes(bytes);
            Byte(0);
        }

        public void PatchPointer(int position, long value)
        {
            var end = _stream.Position;
            _stream.Position = position;
            Pointer(value);
            _stream.Position = end;
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
