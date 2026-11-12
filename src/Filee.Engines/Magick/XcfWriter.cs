// Writes GIMP's own file (.xcf): 8-bit RGB with alpha, RLE tiles, one layer per layer with its name (UTF-8), offsets,
// opacity, visibility and layer mode, and no groups. ImageMagick reads XCF but can't write it. Like GIMP, the writer takes
// the lowest XCF version that holds the layers: 0 ("gimp xcf file") for most, 2 for Soft light, 9 or 10 for the modes of
// GIMP 2.10, all with 32-bit pointers, so GIMP 2.10 and 3 (and older GIMPs, for version 0 and 2) open it. Layers from
// Paint.NET and Photoshop get GIMP's legacy modes, which blend and composite in perceptual RGB as those programs do;
// GIMP 2.10's own modes would mix in linear light and change how the picture looks.

using Filee.Core.Conversion;
using ImageMagick;

namespace Filee.Engines.Magick;

internal static class XcfWriter
{
    /// <summary>A layer to write: its pixels (BGRA, not premultiplied, top row first) at its place on the canvas.</summary>
    internal sealed record Layer(string Name, int X, int Y, int Width, int Height, byte[] Bgra, bool Visible = true,
        byte Opacity = 255, int Mode = XcfBlend.NormalLegacy);

    /// <summary>The largest canvas GIMP opens.</summary>
    public const int MaxSide = Xcf.MaxSide;

    private const int Tile = 64;

    /// <summary>One picture as an .xcf of a single layer named "Background".</summary>
    public static void Write(IMagickImage<byte> image, string path)
    {
        var (width, height) = ((int)image.Width, (int)image.Height);
        Write(path, width, height, [new Layer("Background", 0, 0, width, height, PaintNetWriter.ToBgra(image))]);
    }

    /// <summary>A Paint.NET file as an .xcf with its layers, each cut to its pixels.</summary>
    public static IReadOnlyList<string> SaveFromPdn(ConversionStep step, IProgress<double>? progress, CancellationToken ct)
    {
        var (width, height, read) = PaintNet.Open(step.InputPath);
        var layers = new List<Layer>();
        foreach (var layer in read)
        {
            ct.ThrowIfCancellationRequested();
            layers.Add(Cropped(layer.Name, layer.Bgra, width, height, layer.Visible, layer.Opacity, ModeOf(layer.Blend)));
        }
        progress?.Report(0.6);
        return Save(step, width, height, layers, progress);
    }

    /// <summary>A PSD (or PSB) as an .xcf with its pixel layers, each cut to its pixels.</summary>
    public static IReadOnlyList<string> SaveFromPsd(ConversionStep step, IProgress<double>? progress, CancellationToken ct)
    {
        var (width, height, read) = PaintNetWriter.ReadPsdLayers(step, ct);
        var layers = read.Select(l => Cropped(l.Name, l.Bgra, width, height, l.Visible, l.Opacity, ModeOf(l.Compose))).ToList();
        progress?.Report(0.6);
        return Save(step, width, height, layers, progress);
    }

    private static IReadOnlyList<string> Save(ConversionStep step, int width, int height, List<Layer> layers, IProgress<double>? progress)
    {
        var path = step.Output.Allocate("xcf");
        if (path is null)
            return [];
        Write(path, width, height, layers);
        progress?.Report(1);
        return [path];
    }

    /// <summary>
    /// A canvas-sized layer cut to the box around its pixels that aren't fully transparent (GIMP layers have their own
    /// size and offsets); a layer with none stays the size of the canvas.
    /// </summary>
    internal static Layer Cropped(string name, byte[] bgra, int width, int height, bool visible, byte opacity, int mode)
    {
        int left = width, top = height, right = -1, bottom = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (bgra[(y * width + x) * 4 + 3] == 0)
                    continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }
        if (right < 0)
            return new Layer(name, 0, 0, width, height, bgra, visible, opacity, mode);
        var (w, h) = (right - left + 1, bottom - top + 1);
        if (w == width && h == height)
            return new Layer(name, 0, 0, width, height, bgra, visible, opacity, mode);
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            Array.Copy(bgra, ((top + y) * width + left) * 4, pixels, y * w * 4, w * 4);
        return new Layer(name, left, top, w, h, pixels, visible, opacity, mode);
    }

    /// <summary>The GIMP mode of a Paint.NET blend mode (Reflect, Glow, Negation and Xor have none: Normal).</summary>
    internal static int ModeOf(PaintNet.BlendMode blend) => blend switch
    {
        PaintNet.BlendMode.Multiply => XcfBlend.MultiplyLegacy,
        PaintNet.BlendMode.Additive => XcfBlend.AdditionLegacy,
        PaintNet.BlendMode.ColorBurn => XcfBlend.BurnLegacy,
        PaintNet.BlendMode.ColorDodge => XcfBlend.DodgeLegacy,
        PaintNet.BlendMode.Overlay => XcfBlend.Overlay,
        PaintNet.BlendMode.Difference => XcfBlend.DifferenceLegacy,
        PaintNet.BlendMode.Lighten => XcfBlend.LightenLegacy,
        PaintNet.BlendMode.Darken => XcfBlend.DarkenLegacy,
        PaintNet.BlendMode.Screen => XcfBlend.ScreenLegacy,
        _ => XcfBlend.NormalLegacy,
    };

    /// <summary>The GIMP mode of a Photoshop blend mode, as ImageMagick names it (Normal where GIMP has none).</summary>
    internal static int ModeOf(CompositeOperator compose) => compose switch
    {
        CompositeOperator.Dissolve => XcfBlend.Dissolve,
        CompositeOperator.Multiply => XcfBlend.MultiplyLegacy,
        CompositeOperator.Screen => XcfBlend.ScreenLegacy,
        CompositeOperator.Overlay => XcfBlend.Overlay,
        CompositeOperator.Difference => XcfBlend.DifferenceLegacy,
        CompositeOperator.LinearDodge => XcfBlend.AdditionLegacy,
        CompositeOperator.Darken => XcfBlend.DarkenLegacy,
        CompositeOperator.Lighten => XcfBlend.LightenLegacy,
        CompositeOperator.Hue => XcfBlend.HueLegacy,
        CompositeOperator.Saturate => XcfBlend.SaturationLegacy,
        CompositeOperator.Colorize => XcfBlend.ColorLegacy,
        CompositeOperator.Luminize => XcfBlend.ValueLegacy,
        CompositeOperator.ColorDodge => XcfBlend.DodgeLegacy,
        CompositeOperator.ColorBurn => XcfBlend.BurnLegacy,
        CompositeOperator.HardLight => XcfBlend.HardLightLegacy,
        CompositeOperator.SoftLight => XcfBlend.SoftLightLegacy,
        CompositeOperator.VividLight => XcfBlend.VividLight,
        CompositeOperator.PinLight => XcfBlend.PinLight,
        CompositeOperator.LinearLight => XcfBlend.LinearLight,
        CompositeOperator.HardMix => XcfBlend.HardMix,
        CompositeOperator.Exclusion => XcfBlend.Exclusion,
        CompositeOperator.LinearBurn => XcfBlend.LinearBurn,
        _ => XcfBlend.NormalLegacy,
    };

    /// <summary>The lowest XCF version holding these modes, as GIMP chooses it.</summary>
    private static int VersionFor(IEnumerable<int> modes) => modes.Select(mode => mode switch
    {
        >= XcfBlend.Normal => 10,
        >= XcfBlend.Overlay => 9,
        >= XcfBlend.SoftLightLegacy => 2,
        _ => 0,
    }).DefaultIfEmpty(0).Max();

    /// <summary>Writes an .xcf of <paramref name="layers"/>, bottom first, on a <paramref name="width"/> × <paramref name="height"/> canvas.</summary>
    public static void Write(string path, int width, int height, IReadOnlyList<Layer> layers)
    {
        if (width is < 1 or > MaxSide || height is < 1 or > MaxSide)
            throw new InvalidOperationException(
                $"GIMP opens pictures of at most {MaxSide:N0} pixels on a side; this one is {width:N0} × {height:N0}.");
        if (layers.Count == 0 || layers.Any(l => l.Width < 1 || l.Height < 1 || l.Bgra.Length != l.Width * l.Height * 4))
            throw new ArgumentException("Every layer must have its pixels.", nameof(layers));

        var version = VersionFor(layers.Select(l => l.Mode));
        using var file = new BinaryBigEndian(File.Create(path));
        file.Bytes(version == 0 ? "gimp xcf file\0"u8 : System.Text.Encoding.ASCII.GetBytes($"gimp xcf v{version:D3}\0"));
        file.U32((uint)width);
        file.U32((uint)height);
        file.U32(0); // RGB
        if (version >= 4)
            file.U32(150); // 8-bit, sRGB-encoded
        file.U32(17); // PROP_COMPRESSION: RLE
        file.U32(1);
        file.Byte(1);
        file.U32(0); // PROP_END
        file.U32(0);

        // The layer pointers, top layer first, then the (empty) list of channels.
        var table = file.Position;
        for (var i = 0; i <= layers.Count; i++)
            file.U32(0);
        file.U32(0);

        for (var i = 0; i < layers.Count; i++)
        {
            file.Patch(table + 4 * (layers.Count - 1 - i), file.Position);
            WriteLayer(file, layers[i]);
        }
    }

    private static void WriteLayer(BinaryBigEndian file, Layer layer)
    {
        file.U32((uint)layer.Width);
        file.U32((uint)layer.Height);
        file.U32(1); // RGB with alpha
        var name = System.Text.Encoding.UTF8.GetBytes(layer.Name);
        file.U32((uint)name.Length + 1);
        file.Bytes(name);
        file.Byte(0);
        file.U32(6); file.U32(4); file.U32(layer.Opacity);               // PROP_OPACITY
        file.U32(8); file.U32(4); file.U32(layer.Visible ? 1u : 0);      // PROP_VISIBLE
        file.U32(15); file.U32(8); file.U32(unchecked((uint)layer.X)); file.U32(unchecked((uint)layer.Y)); // PROP_OFFSETS
        file.U32(7); file.U32(4); file.U32((uint)layer.Mode);            // PROP_MODE
        file.U32(0); file.U32(0);                                        // PROP_END
        var hierarchy = file.Position;
        file.U32(0);
        file.U32(0); // no layer mask

        // The hierarchy: its size and bytes per pixel, the first level and no smaller ones.
        file.Patch(hierarchy, file.Position);
        file.U32((uint)layer.Width);
        file.U32((uint)layer.Height);
        file.U32(4);
        var level = file.Position;
        file.U32(0);
        file.U32(0);

        file.Patch(level, file.Position);
        file.U32((uint)layer.Width);
        file.U32((uint)layer.Height);
        var columns = (layer.Width + Tile - 1) / Tile;
        var rows = (layer.Height + Tile - 1) / Tile;
        var tiles = file.Position;
        for (var t = 0; t <= columns * rows; t++)
            file.U32(0);

        var pixels = new byte[Tile * Tile * 4];
        var packed = new List<byte>(Tile * Tile * 5);
        for (var t = 0; t < columns * rows; t++)
        {
            file.Patch(tiles + 4 * t, file.Position);
            var x0 = t % columns * Tile;
            var y0 = t / columns * Tile;
            var w = Math.Min(Tile, layer.Width - x0);
            var h = Math.Min(Tile, layer.Height - y0);
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var source = ((y0 + y) * layer.Width + x0 + x) * 4;
                    var target = (y * w + x) * 4;
                    pixels[target] = layer.Bgra[source + 2];
                    pixels[target + 1] = layer.Bgra[source + 1];
                    pixels[target + 2] = layer.Bgra[source];
                    pixels[target + 3] = layer.Bgra[source + 3];
                }
            }
            packed.Clear();
            for (var plane = 0; plane < 4; plane++)
                PackRle(pixels, plane, w * h, packed);
            file.Bytes(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(packed));
        }
    }

    /// <summary>
    /// One byte of every pixel as an RLE stream. As XCF's notes advise, so a tile never grows past what GIMP reads: no
    /// one-byte runs (a literal instead), long forms only past 127, never two literals in a row.
    /// </summary>
    internal static void PackRle(byte[] pixels, int plane, int count, List<byte> output)
    {
        var i = 0;
        var literalStart = -1;
        while (i < count)
        {
            var value = pixels[i * 4 + plane];
            var run = 1;
            while (i + run < count && run < 65_535 && pixels[(i + run) * 4 + plane] == value)
                run++;
            if (run >= 3)
            {
                FlushLiteral(i);
                if (run <= 127)
                {
                    output.Add((byte)(run - 1));
                }
                else
                {
                    output.Add(127);
                    output.Add((byte)(run >> 8));
                    output.Add((byte)run);
                }
                output.Add(value);
                i += run;
                continue;
            }
            if (literalStart < 0)
                literalStart = i;
            i += run;
        }
        FlushLiteral(count);

        void FlushLiteral(int end)
        {
            if (literalStart < 0)
                return;
            var length = end - literalStart;
            if (length <= 127)
            {
                output.Add((byte)(256 - length));
            }
            else
            {
                output.Add(128);
                output.Add((byte)(length >> 8));
                output.Add((byte)length);
            }
            for (var k = literalStart; k < end; k++)
                output.Add(pixels[k * 4 + plane]);
            literalStart = -1;
        }
    }

    /// <summary>A file written big-endian, with 32-bit pointers patched in once their target is written.</summary>
    private sealed class BinaryBigEndian(Stream stream) : IDisposable
    {
        private readonly BufferedStream _stream = new(stream, 1 << 16);

        public long Position => _stream.Position;

        public void Byte(byte value) => _stream.WriteByte(value);

        public void Bytes(ReadOnlySpan<byte> bytes) => _stream.Write(bytes);

        public void U32(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            _stream.Write(bytes);
        }

        /// <summary>Writes <paramref name="target"/> at <paramref name="at"/>, a pointer slot written earlier.</summary>
        public void Patch(long at, long target)
        {
            if (target > uint.MaxValue)
                throw new InvalidOperationException("This picture is too large for a GIMP file of this kind (4 GB).");
            var position = _stream.Position;
            _stream.Position = at;
            U32((uint)target);
            _stream.Position = position;
        }

        public void Dispose() => _stream.Dispose();
    }
}
