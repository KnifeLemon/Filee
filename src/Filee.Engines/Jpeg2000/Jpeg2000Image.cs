// A decoded JPEG 2000 picture (Jpeg2000Decoder) and what Filee draws of it: the components as the codestream gives
// them (each at its own sampling, precision and signedness), with what the JP2 file says about them — the colour
// space (enumerated or an ICC profile), a palette with its component mapping, and the channel definitions (which
// channel is colour, which is opacity). Rgba turns that into 8-bit RGBA: palettes applied, sub-sampled components
// spread over the picture's grid, precisions scaled to 8 bits, sYCC turned into RGB, CMYK into RGB without a profile
// (as the rest of Filee's PDF drawing does), premultiplied opacity divided out.

namespace Filee.Engines.Jpeg2000;

/// <summary>What one JPEG 2000 codestream may make Filee build (see Jpeg2000Decoder).</summary>
internal sealed record Jpeg2000Limits
{
    public static Jpeg2000Limits Default { get; } = new();

    /// <summary>Pixels of the picture (its area on the reference grid).</summary>
    public long MaxPixels { get; init; } = 16_384L * 16_384;

    /// <summary>Samples of all components together (four bytes each, and as much again while a tile is decoded).</summary>
    public long MaxSamples { get; init; } = 1L << 27;

    /// <summary>Components of the picture (Part 1 allows 16,384; pictures have 1–4, an opacity and a few more).</summary>
    public int MaxComponents { get; init; } = 256;

    /// <summary>Tiles of the picture's tile grid (Part 1 numbers them with 16 bits).</summary>
    public int MaxTiles { get; init; } = 65_535;

    /// <summary>Quality layers of a tile.</summary>
    public int MaxLayers { get; init; } = 4_096;

    /// <summary>Code-blocks of all tiles together.</summary>
    public long MaxCodeBlocks { get; init; } = 2_000_000;

    /// <summary>Packets of all tiles together (precincts times layers), and four times as many visits of the progression.</summary>
    public long MaxPackets { get; init; } = 10_000_000;
}

/// <summary>One component's samples, rows top first, as the codestream gives them (after the DC level shift).</summary>
/// <param name="X0">The component's first column on its own grid: ceil(XOsiz / <paramref name="Dx"/>).</param>
/// <param name="Y0">The component's first row on its own grid.</param>
/// <param name="Dx">Horizontal sub-sampling (XRsiz): one sample every so many columns of the reference grid.</param>
/// <param name="Dy">Vertical sub-sampling (YRsiz).</param>
internal sealed record Jpeg2000Component(int Width, int Height, int X0, int Y0, int Dx, int Dy, int Precision, bool Signed, int[] Samples);

/// <summary>The colour space a JP2 file's colour specification gives.</summary>
internal enum Jpeg2000ColorSpace
{
    /// <summary>None given (a bare codestream) or one Filee doesn't know: told by the number of channels.</summary>
    Unknown,
    Srgb,
    Grey,
    Sycc,
    Cmyk,

    /// <summary>An ICC profile (its colour space signature tells the number of channels).</summary>
    Icc,
}

/// <summary>A JP2 palette (pclr) and the component mapping (cmap) that uses it.</summary>
/// <param name="Columns">Each column's entries (signedness and depth apply to the whole column).</param>
/// <param name="Mapping">Per channel: the component, and the palette column it indexes (-1: the component itself).</param>
internal sealed record Jpeg2000Palette(int[][] Columns, int[] Depths, bool[] Signed, (int Component, int Column)[] Mapping);

/// <summary>A JP2 channel definition (cdef): channel, type (0 colour, 1 opacity, 2 premultiplied opacity), association (1-based colour, 0 the whole picture).</summary>
internal readonly record struct Jpeg2000Channel(int Index, int Type, int Association);

/// <summary>A decoded JPEG 2000 picture.</summary>
/// <param name="X0">The picture's left edge on the reference grid (XOsiz).</param>
/// <param name="Y0">The picture's top edge on the reference grid (YOsiz).</param>
internal sealed record Jpeg2000Image(int Width, int Height, int X0, int Y0, IReadOnlyList<Jpeg2000Component> Components)
{
    public Jpeg2000ColorSpace ColorSpace { get; init; }

    public byte[]? IccProfile { get; init; }

    public Jpeg2000Palette? Palette { get; init; }

    public IReadOnlyList<Jpeg2000Channel>? Channels { get; init; }

    /// <summary>The number of colour channels the file's colour space has; null when it doesn't say.</summary>
    public int? ColorCount => ColorSpace switch
    {
        Jpeg2000ColorSpace.Grey => 1,
        Jpeg2000ColorSpace.Srgb or Jpeg2000ColorSpace.Sycc => 3,
        Jpeg2000ColorSpace.Cmyk => 4,
        Jpeg2000ColorSpace.Icc when IccProfile is { Length: >= 20 } icc => (icc[16], icc[17], icc[18], icc[19]) switch
        {
            ((byte)'G', (byte)'R', (byte)'A', (byte)'Y') => 1,
            ((byte)'C', (byte)'M', (byte)'Y', (byte)'K') => 4,
            _ => 3,
        },
        _ => null,
    };

    /// <summary>
    /// The picture as 8-bit R, G, B, A per pixel (alpha not premultiplied), rows top first.
    /// <paramref name="colors"/>: the number of colour channels when the caller knows it (a PDF's /ColorSpace: 1 grey,
    /// 3 RGB, 4 CMYK), else the file's colour space or the number of channels tells. <paramref name="alpha"/>: use an
    /// opacity channel when there is one (else opaque). <paramref name="palette"/>: apply the file's palette.
    /// </summary>
    public byte[] Rgba(int? colors = null, bool alpha = true, bool palette = true)
    {
        var channels = Planes(palette && Palette is not null);
        var count = channels.Count;
        if (count == 0)
            throw new InvalidDataException("The JPEG 2000 picture has no channels.");
        // Which channels are colours (in order) and which is the opacity.
        var colorCount = Math.Clamp(colors ?? ColorCount ?? (count >= 3 ? (count == 4 && Channels is null && ColorSpace == Jpeg2000ColorSpace.Unknown ? 4 : 3) : 1), 1, 4);
        if (colorCount == 2)
            colorCount = 1;
        var order = new int[colorCount];
        var opacity = -1;
        var premultiplied = false;
        if (Channels is { Count: > 0 } definitions)
        {
            Array.Fill(order, -1);
            foreach (var definition in definitions)
            {
                if (definition.Index < 0 || definition.Index >= count)
                    continue;
                if (definition.Type == 0 && definition.Association is >= 1 && definition.Association <= colorCount && order[definition.Association - 1] < 0)
                    order[definition.Association - 1] = definition.Index;
                else if (definition.Type is 1 or 2 && definition.Association == 0 && opacity < 0)
                    (opacity, premultiplied) = (definition.Index, definition.Type == 2);
            }
            // Colours the definitions leave unsaid: the remaining channels in order.
            var free = Enumerable.Range(0, count).Where(i => !order.Contains(i) && i != opacity).ToList();
            for (var i = 0; i < colorCount; i++)
            {
                if (order[i] < 0)
                    order[i] = free.Count > 0 ? free[0] : order[0] >= 0 ? order[0] : 0;
                free.Remove(order[i]);
            }
        }
        else
        {
            for (var i = 0; i < colorCount; i++)
                order[i] = Math.Min(i, count - 1);
            if (count > colorCount)
                opacity = colorCount;
        }
        if (colorCount == 3 && order.Distinct().Count() < 3)
            colorCount = 1;

        var pixels = (long)Width * Height;
        var rgba = new byte[checked(pixels * 4)];
        var c0 = channels[order[0]];
        var c1 = colorCount >= 3 ? channels[order[1]] : c0;
        var c2 = colorCount >= 3 ? channels[order[2]] : c0;
        var c3 = colorCount == 4 ? channels[order[3]] : null;
        var a = alpha && opacity >= 0 ? channels[opacity] : null;
        var sycc = colorCount == 3 && ColorSpace == Jpeg2000ColorSpace.Sycc;
        for (long i = 0, o = 0; i < pixels; i++, o += 4)
        {
            int r = c0[i], g = c1[i], b = c2[i];
            if (colorCount == 4)
            {
                // CMYK without a profile.
                var k = 255 - c3![i];
                (r, g, b) = ((255 - r) * k / 255, (255 - g) * k / 255, (255 - b) * k / 255);
            }
            else if (sycc)
            {
                double y = r, cb = g - 128, cr = b - 128;
                (r, g, b) = (Clamp(y + 1.402 * cr), Clamp(y - 0.344136 * cb - 0.714136 * cr), Clamp(y + 1.772 * cb));
            }
            var opaque = a is null ? 255 : a[i];
            if (premultiplied && opaque is > 0 and < 255)
                (r, g, b) = (Math.Min(255, r * 255 / opaque), Math.Min(255, g * 255 / opaque), Math.Min(255, b * 255 / opaque));
            rgba[o] = (byte)r;
            rgba[o + 1] = (byte)g;
            rgba[o + 2] = (byte)b;
            rgba[o + 3] = (byte)opaque;
        }
        return rgba;
    }

    private static int Clamp(double value) => (int)Math.Clamp(Math.Round(value), 0, 255);

    /// <summary>
    /// The channel the picture's opacity is in when <paramref name="colors"/> channels are its colours: the channel
    /// definitions' opacity, else the channel after the colours; null when there is none.
    /// </summary>
    public int? Opacity(int colors)
    {
        if (Channels is { Count: > 0 } definitions)
            return definitions.FirstOrDefault(d => d.Type is 1 or 2 && d.Association == 0) is { Type: 1 or 2 } found ? found.Index : null;
        var count = Palette is { } table ? table.Mapping.Length : Components.Count;
        return count > colors ? colors : null;
    }

    /// <summary>
    /// Each channel as 8-bit samples over the picture's grid (palette columns looked up, sub-sampled components
    /// spread). <paramref name="raw"/>: the samples as they are (clipped to 0–255, not scaled to 8 bits), for
    /// indices into a colour table.
    /// </summary>
    public List<byte[]> Planes(bool usePalette, bool raw = false)
    {
        var planes = new List<byte[]>();
        if (usePalette && Palette is { } table)
        {
            foreach (var (component, column) in table.Mapping)
            {
                if (component < 0 || component >= Components.Count)
                    continue;
                var source = Components[component];
                if (column < 0 || column >= table.Columns.Length)
                {
                    planes.Add(Plane(source, null, raw));
                    continue;
                }
                // The component's samples index the column; its values have the column's depth.
                var entries = table.Columns[column];
                var lookup = new byte[entries.Length];
                for (var i = 0; i < entries.Length; i++)
                    lookup[i] = Eight(entries[i], table.Depths[column], table.Signed[column]);
                planes.Add(Plane(source, lookup, raw));
            }
            if (planes.Count > 0)
                return planes;
        }
        foreach (var component in Components)
            planes.Add(Plane(component, null, raw));
        return planes;
    }

    /// <summary>A component over the picture's grid: each pixel takes the sample whose area holds it.</summary>
    private byte[] Plane(Jpeg2000Component component, byte[]? lookup, bool raw)
    {
        var plane = new byte[(long)Width * Height];
        if (component.Width <= 0 || component.Height <= 0)
            return plane;
        var columns = new int[Width];
        for (var x = 0; x < Width; x++)
            columns[x] = Math.Clamp((X0 + x) / component.Dx - component.X0, 0, component.Width - 1);
        var samples = component.Samples;
        for (var y = 0; y < Height; y++)
        {
            var row = (long)Math.Clamp((Y0 + y) / component.Dy - component.Y0, 0, component.Height - 1) * component.Width;
            var at = (long)y * Width;
            for (var x = 0; x < Width; x++)
            {
                var value = samples[row + columns[x]];
                plane[at + x] = lookup is null
                    ? raw ? (byte)Math.Clamp(value, 0, 255) : Eight(value, component.Precision, component.Signed)
                    : lookup[Math.Clamp(component.Signed ? value + (1 << (component.Precision - 1)) : value, 0, lookup.Length - 1)];
            }
        }
        return plane;
    }

    /// <summary>A sample of <paramref name="precision"/> bits as 8 bits (a signed one shifted to start at 0).</summary>
    private static byte Eight(int value, int precision, bool signed)
    {
        if (signed)
            value += 1 << (precision - 1);
        if (precision == 8)
            return (byte)Math.Clamp(value, 0, 255);
        if (precision > 8)
            return (byte)Math.Clamp(value >> (precision - 8), 0, 255);
        var top = (1 << precision) - 1;
        return (byte)(Math.Clamp(value, 0, top) * 255 / top);
    }
}
