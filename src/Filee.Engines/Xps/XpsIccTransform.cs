// ICC profiles Little CMS can't be handed through ImageMagick's colour spaces, read by Filee (ICC.1:2001-04 and
// ISO 15076-1 tag layouts): N-channel profiles ('2CLR' … '8CLR' in the header's colour space field, ECMA-388 §15.2.5)
// and Named Color profiles ('nmcl' class, §15.2.6).
//
// - N-channel: the AToB1 tag (relative colorimetric, else AToB0) of type lut8 ('mft1'), lut16 ('mft2') or lutAtoB
//   ('mAB '): input curves, the colour lookup table (multilinear between its grid points), output curves (and for
//   lutAtoB the M curves, the matrix and the B curves), into the PCS (Lab or XYZ, D50).
// - Named colours: the namedColor2Tag's PCS value of each colour. A tint dilutes its colour towards the paper's white
//   (in Lab), and the colours of one ContextColor (a duotone, …) print over each other: their sRGB reflectances multiply.
//   ECMA-388 lets a consumer approximate named colours from these PCS values.
// - PCS → sRGB: D50 XYZ through sRGB's Bradford-adapted matrix (PdfA's SrgbProfile primaries), then sRGB's curve.
//
// Every offset and count is checked against the profile's bytes; lookup tables of more than MaxLutEntries values and
// more than MaxNamedColors colours make the profile unusable (null), so the colour follows ECMA-388's fallback rules.

using System.Buffers.Binary;
using SkiaSharp;

namespace Filee.Engines.Xps;

/// <summary>An N-channel or Named Color ICC profile turned into a function from its channels to sRGB.</summary>
internal sealed class XpsIccTransform
{
    /// <summary>Values one lookup table may hold at most (grid points × output channels).</summary>
    internal const int MaxLutEntries = 4_000_000;

    /// <summary>Named colours one profile may list at most.</summary>
    internal const int MaxNamedColors = 4096;

    private readonly Func<ReadOnlySpan<float>, (double L, double A, double B)>? _lab;
    private readonly (double L, double A, double B)[]? _named;

    private XpsIccTransform(int channels, Func<ReadOnlySpan<float>, (double, double, double)>? lab, (double, double, double)[]? named)
    {
        Channels = channels;
        _lab = lab;
        _named = named;
    }

    /// <summary>Channels (or named colours' tints) a colour gives.</summary>
    public int Channels { get; }

    /// <summary>Whether the header says the profile is an N-channel or Named Color one (this class reads it, not ImageMagick).</summary>
    public static bool Handles(ReadOnlySpan<byte> icc) =>
        icc.Length >= 132 && (Space(icc) is >= 2 and <= 8 || icc.Slice(12, 4).SequenceEqual("nmcl"u8));

    /// <summary>The channels of a '2CLR' … '8CLR' colour space signature; 0 for others.</summary>
    private static int Space(ReadOnlySpan<byte> icc) =>
        icc.Slice(17, 3).SequenceEqual("CLR"u8) && icc[16] is >= (byte)'2' and <= (byte)'8' ? icc[16] - '0' : 0;

    /// <summary>The profile read; null when it is damaged, too large or of a layout this class doesn't read.</summary>
    public static XpsIccTransform? Create(byte[] icc)
    {
        try
        {
            if (!Handles(icc))
                return null;
            var lab = icc.AsSpan(20, 4).SequenceEqual("Lab "u8);
            if (!lab && !icc.AsSpan(20, 4).SequenceEqual("XYZ "u8))
                return null;
            var version = icc[8];
            if (icc.AsSpan(12, 4).SequenceEqual("nmcl"u8))
                return Tag(icc, "ncl2") is { } named ? Named(icc, named, lab, version) : null;
            var channels = Space(icc);
            if (channels is < 2 or > 8)
                return null;
            var tag = Tag(icc, "A2B1") ?? Tag(icc, "A2B0");
            if (tag is not { } t)
                return null;
            var function = Lut(icc, t.Offset, t.Size, channels, lab, version);
            return function is null ? null : new XpsIccTransform(channels, function, null);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidDataException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>The sRGB colour of <paramref name="channels"/> (0–1 each, <see cref="Channels"/> of them).</summary>
    public SKColor Convert(float alpha, ReadOnlySpan<float> channels)
    {
        var a = (byte)Math.Clamp(MathF.Round(Clamp(alpha) * 255), 0, 255);
        if (_lab is not null)
        {
            Span<float> clamped = stackalloc float[Channels];
            for (var i = 0; i < Channels; i++)
                clamped[i] = Clamp(channels[i]);
            var (r, g, b) = LinearRgb(_lab(clamped));
            return new SKColor(Encode(r), Encode(g), Encode(b), a);
        }
        // Named colours printed over each other: each tint diluted towards white, reflectances multiplied.
        double red = 1, green = 1, blue = 1;
        for (var i = 0; i < Channels; i++)
        {
            var tint = Clamp(channels[i]);
            var (l, ca, cb) = _named![i];
            var (r, g, b) = LinearRgb((100 + tint * (l - 100), tint * ca, tint * cb));
            red *= Math.Clamp(r, 0, 1);
            green *= Math.Clamp(g, 0, 1);
            blue *= Math.Clamp(b, 0, 1);
        }
        return new SKColor(Encode(red), Encode(green), Encode(blue), a);
    }

    private static float Clamp(float value) => float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    // ───────────────────────── Tags ─────────────────────────

    private static (int Offset, int Size)? Tag(byte[] icc, string signature)
    {
        var count = U32(icc, 128);
        if (count > 1000 || 132 + count * 12 > icc.Length)
            throw new InvalidDataException("The profile's tag table is damaged.");
        for (var i = 0; i < count; i++)
        {
            var at = 132 + (int)i * 12;
            if (icc[at] != signature[0] || icc[at + 1] != signature[1] || icc[at + 2] != signature[2] || icc[at + 3] != signature[3])
                continue;
            var (offset, size) = (U32(icc, at + 4), U32(icc, at + 8));
            if (offset < 128 || size < 8 || offset + size > (uint)icc.Length)
                throw new InvalidDataException("A tag lies outside the profile.");
            return ((int)offset, (int)size);
        }
        return null;
    }

    private static uint U32(byte[] bytes, int at) =>
        at >= 0 && at + 4 <= bytes.Length ? BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at)) : throw new InvalidDataException("The profile ends early.");

    private static int U16(byte[] bytes, int at) =>
        at >= 0 && at + 2 <= bytes.Length ? BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at)) : throw new InvalidDataException("The profile ends early.");

    private static double S15(byte[] bytes, int at) => (int)U32(bytes, at) / 65536.0;

    private static bool Is(byte[] bytes, int at, ReadOnlySpan<byte> type) => at + 4 <= bytes.Length && bytes.AsSpan(at, 4).SequenceEqual(type);

    // ───────────────────────── Named colours ─────────────────────────

    private static XpsIccTransform? Named(byte[] icc, (int Offset, int Size) tag, bool lab, int version)
    {
        var at = tag.Offset;
        if (!Is(icc, at, "ncl2"u8))
            return null;
        var count = U32(icc, at + 12);
        var device = U32(icc, at + 16);
        if (count is 0 or > MaxNamedColors || device > 15)
            return null;
        var entry = 32 + 6 + 2 * (int)device;
        if (at + 84 + (long)count * entry > tag.Offset + tag.Size)
            return null;
        var colors = new (double, double, double)[count];
        for (var i = 0; i < count; i++)
        {
            var pcs = at + 84 + i * entry + 32;
            colors[i] = ToLab(lab, U16(icc, pcs) / 65535.0, U16(icc, pcs + 2) / 65535.0, U16(icc, pcs + 4) / 65535.0, legacy16: version < 4);
        }
        return new XpsIccTransform((int)count, null, colors);
    }

    // ───────────────────────── Lookup tables ─────────────────────────

    /// <summary>A PCS value given as shares of its 16-bit (or 8-bit) range, as Lab.</summary>
    private static (double L, double A, double B) ToLab(bool lab, double x, double y, double z, bool legacy16)
    {
        if (lab)
        {
            // Version 2's 16-bit Lab puts 100 (and +127) at 0xFF00, not at 0xFFFF.
            var scale = legacy16 ? 65535.0 / 65280.0 : 1;
            return (x * scale * 100, y * scale * 255 - 128, z * scale * 255 - 128);
        }
        // XYZ as u1Fixed15: 0x8000 is 1.0.
        var factor = 65535.0 / 32768.0;
        return XyzToLab(x * factor, y * factor, z * factor);
    }

    private static Func<ReadOnlySpan<float>, (double, double, double)>? Lut(byte[] icc, int offset, int size, int channels, bool lab, int version)
    {
        if (Is(icc, offset, "mft1"u8) || Is(icc, offset, "mft2"u8))
            return Legacy(icc, offset, size, channels, lab, version, Is(icc, offset, "mft2"u8));
        if (Is(icc, offset, "mAB "u8))
            return AToB(icc, offset, size, channels, lab);
        return null;
    }

    /// <summary>lut8 / lut16: input tables, CLUT, output tables (the matrix is for XYZ input only and unused here).</summary>
    private static Func<ReadOnlySpan<float>, (double, double, double)>? Legacy(byte[] icc, int offset, int size, int channels, bool lab, int version, bool wide)
    {
        int inputs = icc[offset + 8], outputs = icc[offset + 9], grid = icc[offset + 10];
        if (inputs != channels || outputs != 3 || grid < 2)
            return null;
        int inEntries = 256, outEntries = 256, at = offset + 48;
        if (wide)
        {
            inEntries = U16(icc, offset + 48);
            outEntries = U16(icc, offset + 50);
            at = offset + 52;
            if (inEntries is < 2 or > 4096 || outEntries is < 2 or > 4096)
                return null;
        }
        var width = wide ? 2 : 1;
        var points = Points(grid, inputs);
        if (points is null)
            return null;
        long needed = (long)inputs * inEntries * width + points.Value * outputs * width + (long)outputs * outEntries * width;
        if (at + needed > offset + size)
            return null;
        double Read(int position) => wide ? U16(icc, position) / 65535.0 : icc[position] / 255.0;
        var inputTables = Tables(inputs, inEntries, at, width, Read);
        at += inputs * inEntries * width;
        var clut = new double[points.Value * outputs];
        for (var i = 0; i < clut.Length; i++)
            clut[i] = Read(at + i * width);
        at += clut.Length * width;
        var outputTables = Tables(outputs, outEntries, at, width, Read);
        var grids = Enumerable.Repeat(grid, inputs).ToArray();
        return values =>
        {
            Span<double> input = stackalloc double[inputs];
            for (var i = 0; i < inputs; i++)
                input[i] = Table(inputTables[i], values[i]);
            Span<double> output = stackalloc double[3];
            Interpolate(clut, grids, outputs, input, output);
            for (var o = 0; o < 3; o++)
                output[o] = Table(outputTables[o], output[o]);
            return ToLab(lab, output[0], output[1], output[2], legacy16: wide && version < 4);
        };
    }

    private static double[][] Tables(int count, int entries, int at, int width, Func<int, double> read)
    {
        var tables = new double[count][];
        for (var t = 0; t < count; t++)
        {
            tables[t] = new double[entries];
            for (var e = 0; e < entries; e++)
                tables[t][e] = read(at + (t * entries + e) * width);
        }
        return tables;
    }

    /// <summary>A sampled curve at <paramref name="x"/> (0–1), linear between its entries.</summary>
    private static double Table(double[] table, double x)
    {
        var at = Math.Clamp(x, 0, 1) * (table.Length - 1);
        var low = Math.Min((int)at, table.Length - 2);
        var fraction = at - low;
        return table[low] + (table[low + 1] - table[low]) * fraction;
    }

    private static long? Points(int grid, int inputs)
    {
        long points = 1;
        for (var i = 0; i < inputs; i++)
        {
            points *= grid;
            if (points * 3 > MaxLutEntries)
                return null;
        }
        return points;
    }

    /// <summary>
    /// Multilinear lookup in <paramref name="clut"/> (the first input varying slowest), <paramref name="outputs"/>
    /// values per grid point.
    /// </summary>
    private static void Interpolate(double[] clut, int[] grids, int outputs, ReadOnlySpan<double> input, Span<double> output)
    {
        var inputs = grids.Length;
        Span<int> low = stackalloc int[inputs];
        Span<double> fraction = stackalloc double[inputs];
        for (var i = 0; i < inputs; i++)
        {
            var at = Math.Clamp(double.IsFinite(input[i]) ? input[i] : 0, 0, 1) * (grids[i] - 1);
            low[i] = Math.Min((int)at, grids[i] - 2);
            fraction[i] = at - low[i];
        }
        output.Clear();
        for (var corner = 0; corner < 1 << inputs; corner++)
        {
            var weight = 1.0;
            long index = 0;
            for (var i = 0; i < inputs; i++)
            {
                var high = (corner >> (inputs - 1 - i) & 1) == 1;
                weight *= high ? fraction[i] : 1 - fraction[i];
                index = index * grids[i] + low[i] + (high ? 1 : 0);
            }
            if (weight == 0)
                continue;
            for (var o = 0; o < output.Length && o < outputs; o++)
                output[o] += weight * clut[index * outputs + o];
        }
    }

    /// <summary>lutAtoB: A curves → CLUT → M curves → matrix → B curves (each but the B curves optional).</summary>
    private static Func<ReadOnlySpan<float>, (double, double, double)>? AToB(byte[] icc, int offset, int size, int channels, bool lab)
    {
        int inputs = icc[offset + 8], outputs = icc[offset + 9];
        if (inputs != channels || outputs != 3)
            return null;
        var end = offset + size;
        int Offset(int at) => (int)U32(icc, offset + at) is var value && value > 0 && offset + (long)value < end ? offset + value : 0;
        var (bAt, matrixAt, mAt, clutAt, aAt) = (Offset(12), Offset(16), Offset(20), Offset(24), Offset(28));
        if (bAt == 0)
            return null;
        var b = Curves(icc, bAt, 3, end);
        var m = mAt == 0 ? null : Curves(icc, mAt, 3, end);
        var a = aAt == 0 ? null : Curves(icc, aAt, inputs, end);
        if (b is null || (mAt != 0 && m is null) || (aAt != 0 && a is null))
            return null;
        double[]? matrix = null;
        if (matrixAt != 0)
        {
            if (matrixAt + 48 > end)
                return null;
            matrix = [.. Enumerable.Range(0, 12).Select(i => S15(icc, matrixAt + 4 * i))];
        }
        double[]? clut = null;
        int[]? grids = null;
        if (clutAt != 0)
        {
            if (clutAt + 20 > end)
                return null;
            grids = [.. Enumerable.Range(0, inputs).Select(i => (int)icc[clutAt + i])];
            if (grids.Any(g => g < 2))
                return null;
            var precision = icc[clutAt + 16];
            if (precision is not (1 or 2))
                return null;
            long points = 1;
            foreach (var g in grids)
            {
                points *= g;
                if (points * 3 > MaxLutEntries)
                    return null;
            }
            if (clutAt + 20 + points * 3 * precision > end)
                return null;
            clut = new double[points * 3];
            for (var i = 0; i < clut.Length; i++)
                clut[i] = precision == 2 ? U16(icc, clutAt + 20 + 2 * i) / 65535.0 : icc[clutAt + 20 + i] / 255.0;
        }
        else if (inputs != 3)
        {
            return null; // without a CLUT the channels go straight on: only three can
        }
        return values =>
        {
            Span<double> input = stackalloc double[inputs];
            for (var i = 0; i < inputs; i++)
                input[i] = a is null ? values[i] : a[i](values[i]);
            Span<double> pcs = stackalloc double[3];
            if (clut is not null)
                Interpolate(clut, grids!, 3, input, pcs);
            else
                input[..3].CopyTo(pcs);
            if (m is not null)
            {
                for (var o = 0; o < 3; o++)
                    pcs[o] = m[o](pcs[o]);
            }
            if (matrix is not null)
            {
                var (x, y, z) = (pcs[0], pcs[1], pcs[2]);
                for (var o = 0; o < 3; o++)
                    pcs[o] = matrix[3 * o] * x + matrix[3 * o + 1] * y + matrix[3 * o + 2] * z + matrix[9 + o];
            }
            for (var o = 0; o < 3; o++)
                pcs[o] = b[o](pcs[o]);
            // Version 4 PCS: Lab with 100 (and +127) at 1.0; XYZ with 1.0 at 32768 / 65535.
            return lab ? (pcs[0] * 100, pcs[1] * 255 - 128, pcs[2] * 255 - 128) : XyzToLab(pcs[0] * 65535 / 32768, pcs[1] * 65535 / 32768, pcs[2] * 65535 / 32768);
        };
    }

    /// <summary><paramref name="count"/> curves ('curv' or 'para', each padded to four bytes) from <paramref name="at"/>.</summary>
    private static Func<double, double>[]? Curves(byte[] icc, int at, int count, int end)
    {
        var curves = new Func<double, double>[count];
        for (var c = 0; c < count; c++)
        {
            if (at + 12 > end)
                return null;
            if (Is(icc, at, "curv"u8))
            {
                var entries = (int)Math.Min(U32(icc, at + 8), 65536);
                if (at + 12 + 2L * entries > end)
                    return null;
                if (entries == 0)
                {
                    curves[c] = x => x;
                }
                else if (entries == 1)
                {
                    var gamma = U16(icc, at + 12) / 256.0;
                    curves[c] = x => Math.Pow(Math.Clamp(x, 0, 1), gamma);
                }
                else
                {
                    var table = new double[entries];
                    for (var e = 0; e < entries; e++)
                        table[e] = U16(icc, at + 12 + 2 * e) / 65535.0;
                    curves[c] = x => Table(table, x);
                }
                at += 12 + 2 * entries;
            }
            else if (Is(icc, at, "para"u8))
            {
                var function = U16(icc, at + 8);
                var parameters = function switch { 0 => 1, 1 => 3, 2 => 4, 3 => 5, 4 => 7, _ => -1 };
                if (parameters < 0 || at + 12 + 4L * parameters > end)
                    return null;
                var p = Enumerable.Range(0, parameters).Select(i => S15(icc, at + 12 + 4 * i)).ToArray();
                curves[c] = x => Parametric(function, p, Math.Clamp(x, 0, 1));
                at += 12 + 4 * parameters;
            }
            else
            {
                return null;
            }
            at = (at + 3) & ~3;
        }
        return curves;
    }

    private static double Parametric(int function, double[] p, double x)
    {
        static double Pow(double value, double gamma) => value <= 0 ? 0 : Math.Pow(value, gamma);
        return function switch
        {
            0 => Pow(x, p[0]),
            1 => x >= -p[2] / p[1] ? Pow(p[1] * x + p[2], p[0]) : 0,
            2 => x >= -p[2] / p[1] ? Pow(p[1] * x + p[2], p[0]) + p[3] : p[3],
            3 => x >= p[4] ? Pow(p[1] * x + p[2], p[0]) : p[3] * x,
            _ => x >= p[4] ? Pow(p[1] * x + p[2], p[0]) + p[5] : p[3] * x + p[6],
        };
    }

    // ───────────────────────── PCS to sRGB ─────────────────────────

    private static readonly double[] White = [0.9642, 1.0, 0.8249];

    private static (double L, double A, double B) XyzToLab(double x, double y, double z)
    {
        static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : (24389.0 / 27 * t + 16) / 116;
        var (fx, fy, fz) = (F(x / White[0]), F(y / White[1]), F(z / White[2]));
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    /// <summary>Lab (D50) as linear sRGB (not clipped).</summary>
    private static (double R, double G, double B) LinearRgb((double L, double A, double B) lab)
    {
        var fy = (lab.L + 16) / 116;
        var (fx, fz) = (fy + lab.A / 500, fy - lab.B / 200);
        static double Finv(double f) => f * f * f > 216.0 / 24389 ? f * f * f : (116 * f - 16) * 27 / 24389;
        var (x, y, z) = (Finv(fx) * White[0], Finv(fy) * White[1], Finv(fz) * White[2]);
        // D50 XYZ → linear sRGB (Bradford-adapted).
        return (3.1338561 * x - 1.6168667 * y - 0.4906146 * z,
            -0.9787684 * x + 1.9161415 * y + 0.0334540 * z,
            0.0719453 * x - 0.2289914 * y + 1.4052427 * z);
    }

    private static byte Encode(double linear)
    {
        linear = Math.Clamp(double.IsFinite(linear) ? linear : 0, 0, 1);
        var encoded = linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        return (byte)Math.Clamp(Math.Round(encoded * 255), 0, 255);
    }
}
