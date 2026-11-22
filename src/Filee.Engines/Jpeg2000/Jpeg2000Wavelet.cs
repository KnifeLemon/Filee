// The inverse transforms of JPEG 2000 (T.800 Annexes F and G): the sub-bands of each resolution level interleaved
// (2D_INTERLEAVE) and filtered back, rows first and then columns (HOR_SR, VER_SR), with the 5/3 reversible filter
// on integers or the 9/7 irreversible one on floats, both by lifting over a periodic symmetric extension of each
// line; a line of one sample at an odd position is halved. Then the reversible (RCT) or irreversible (ICT)
// component transform of the first three components.

namespace Filee.Engines.Jpeg2000;

internal static class Jpeg2000Wavelet
{
    /// <summary>A sub-band's area (its own coordinates) and coefficients; null coefficients are all 0.</summary>
    internal readonly record struct SubBand(int X0, int Y0, int X1, int Y1, int[]? Ints, float[]? Floats);

    /// <summary>A resolution level's area and sub-bands (LL alone at level 0; HL, LH, HH above).</summary>
    internal sealed record Level(int X0, int Y0, int X1, int Y1, SubBand[] Bands);

    private const int Pad = 4;

    // The 9/7 lifting constants (Table F.4).
    private const float Alpha = -1.586134342059924f;
    private const float Beta = -0.052980118572961f;
    private const float Gamma = 0.882911075530934f;
    private const float Delta = 0.443506852043971f;
    private const float K = 1.230174104914001f;

    /// <summary>The tile-component's samples from its levels: an int[] (reversible) or a float[].</summary>
    public static object Inverse(bool reversible, Level[] levels)
    {
        var first = levels[0];
        var ll = first.Bands[0];
        if (reversible)
        {
            var current = ll.Ints ?? new int[(long)Math.Max(0, first.X1 - first.X0) * Math.Max(0, first.Y1 - first.Y0)];
            for (var r = 1; r < levels.Length; r++)
                current = Level53(current, levels[r]);
            return current;
        }
        var samples = ll.Floats ?? new float[(long)Math.Max(0, first.X1 - first.X0) * Math.Max(0, first.Y1 - first.Y0)];
        for (var r = 1; r < levels.Length; r++)
            samples = Level97(samples, levels[r]);
        return samples;
    }

    private static int[] Level53(int[] low, Level level)
    {
        var (w, h) = (level.X1 - level.X0, level.Y1 - level.Y0);
        var a = new int[(long)Math.Max(0, w) * Math.Max(0, h)];
        if (w <= 0 || h <= 0)
            return a;
        Interleave(level, low, level.Bands[0].Ints, level.Bands[1].Ints, level.Bands[2].Ints, a);
        // Rows, then columns, each line on its own: on all cores for large levels.
        var size = Math.Max(w, h) + 2 * Pad;
        Lines(h, (long)w * h, () => new int[size], (y, buffer) => Line53(a.AsSpan(y * w, w), level.X0, buffer));
        Lines(w, (long)w * h, () => new int[2 * size], (x, buffer) =>
        {
            var column = buffer.AsSpan(size, h);
            for (var y = 0; y < h; y++)
                column[y] = a[y * w + x];
            Line53(column, level.Y0, buffer);
            for (var y = 0; y < h; y++)
                a[y * w + x] = column[y];
        });
        return a;
    }

    private static float[] Level97(float[] low, Level level)
    {
        var (w, h) = (level.X1 - level.X0, level.Y1 - level.Y0);
        var a = new float[(long)Math.Max(0, w) * Math.Max(0, h)];
        if (w <= 0 || h <= 0)
            return a;
        Interleave(level, low, level.Bands[0].Floats, level.Bands[1].Floats, level.Bands[2].Floats, a);
        // Rows, then columns, each line on its own: on all cores for large levels.
        var size = Math.Max(w, h) + 2 * Pad;
        Lines(h, (long)w * h, () => new float[size], (y, buffer) => Line97(a.AsSpan(y * w, w), level.X0, buffer));
        Lines(w, (long)w * h, () => new float[2 * size], (x, buffer) =>
        {
            var column = buffer.AsSpan(size, h);
            for (var y = 0; y < h; y++)
                column[y] = a[y * w + x];
            Line97(column, level.Y0, buffer);
            for (var y = 0; y < h; y++)
                a[y * w + x] = column[y];
        });
        return a;
    }

    /// <summary>Runs <paramref name="line"/> for each of <paramref name="count"/> lines, in parallel when the level is large.</summary>
    private static void Lines<T>(int count, long samples, Func<T[]> buffer, Action<int, T[]> line)
    {
        if (samples < 1 << 16)
        {
            var own = buffer();
            for (var i = 0; i < count; i++)
                line(i, own);
            return;
        }
        Parallel.For(0, count, buffer, (i, _, own) =>
        {
            line(i, own);
            return own;
        }, _ => { });
    }

    /// <summary>
    /// 2D_INTERLEAVE: the level below (LL) and the HL, LH and HH bands into the level's samples <paramref name="a"/>;
    /// even positions (absolute) are low-pass, odd ones high-pass. A missing band is all 0.
    /// </summary>
    private static void Interleave<T>(Level level, T[] low, T[]? hl, T[]? lh, T[]? hh, T[] a)
        where T : unmanaged
    {
        var (w, h) = (level.X1 - level.X0, level.Y1 - level.Y0);
        var lowX0 = (level.X0 + 1) >> 1;
        var highX0 = level.X0 >> 1;
        var lowY0 = (level.Y0 + 1) >> 1;
        var highY0 = level.Y0 >> 1;
        var lowWidth = ((level.X1 + 1) >> 1) - lowX0;
        var highWidth = (level.X1 >> 1) - highX0;
        // The first sample of the row is low-pass when the level starts at an even position.
        var firstLow = (level.X0 & 1) == 0;
        for (var y = 0; y < h; y++)
        {
            var v = level.Y0 + y;
            var highRow = (v & 1) == 1;
            var row = highRow ? (v >> 1) - highY0 : (v >> 1) - lowY0;
            var lows = highRow ? lh : low;
            var highs = highRow ? hh : hl;
            var target = y * w;
            int lowAt = row * lowWidth, highAt = row * highWidth;
            for (var x = 0; x < w; x++)
            {
                if (((x & 1) == 0) == firstLow)
                    a[target + x] = lows is null ? default : lows[lowAt++];
                else
                    a[target + x] = highs is null ? default : highs[highAt++];
            }
        }
    }

    /// <summary>The position of <paramref name="k"/> in a line of <paramref name="n"/> samples, mirrored at its ends without repeating them.</summary>
    private static int Mirror(int k, int n)
    {
        var period = 2 * (n - 1);
        var m = k % period;
        if (m < 0)
            m += period;
        return m >= n ? period - m : m;
    }

    /// <summary>1D_SR with the 5/3 filter (F.3.8.1): <paramref name="i0"/> is the line's first absolute position.</summary>
    internal static void Line53(Span<int> x, int i0, int[] buffer)
    {
        var n = x.Length;
        if (n == 0)
            return;
        if (n == 1)
        {
            if ((i0 & 1) == 1)
                x[0] /= 2;
            return;
        }
        for (var k = 0; k < n; k++)
            buffer[Pad + k] = x[k];
        for (var k = 1; k <= Pad; k++)
        {
            buffer[Pad - k] = x[Mirror(-k, n)];
            buffer[Pad + n - 1 + k] = x[Mirror(n - 1 + k, n)];
        }
        for (var k = First(i0, 1 - Pad, 0); k <= n + Pad - 2; k += 2)
            buffer[Pad + k] -= (buffer[Pad + k - 1] + buffer[Pad + k + 1] + 2) >> 2;
        for (var k = First(i0, 0, 1); k < n; k += 2)
            buffer[Pad + k] += (buffer[Pad + k - 1] + buffer[Pad + k + 1]) >> 1;
        for (var k = 0; k < n; k++)
            x[k] = buffer[Pad + k];
    }

    /// <summary>1D_SR with the 9/7 filter (F.3.8.2).</summary>
    internal static void Line97(Span<float> x, int i0, float[] buffer)
    {
        var n = x.Length;
        if (n == 0)
            return;
        if (n == 1)
        {
            if ((i0 & 1) == 1)
                x[0] /= 2;
            return;
        }
        for (var k = 0; k < n; k++)
            buffer[Pad + k] = x[k];
        for (var k = 1; k <= Pad; k++)
        {
            buffer[Pad - k] = x[Mirror(-k, n)];
            buffer[Pad + n - 1 + k] = x[Mirror(n - 1 + k, n)];
        }
        for (var k = -Pad; k < n + Pad; k++)
            buffer[Pad + k] *= ((i0 + k) & 1) == 0 ? K : 1 / K;
        Lift(buffer, i0, 0, -3, n + 3, Delta);
        Lift(buffer, i0, 1, -2, n + 2, Gamma);
        Lift(buffer, i0, 0, -1, n + 1, Beta);
        Lift(buffer, i0, 1, 0, n, Alpha);
        for (var k = 0; k < n; k++)
            x[k] = buffer[Pad + k];
    }

    /// <summary>One lifting step: samples of <paramref name="parity"/> in [from, to) less <paramref name="factor"/> times their neighbours.</summary>
    private static void Lift(float[] buffer, int i0, int parity, int from, int to, float factor)
    {
        for (var k = First(i0, from, parity); k < to; k += 2)
            buffer[Pad + k] -= factor * (buffer[Pad + k - 1] + buffer[Pad + k + 1]);
    }

    /// <summary>The first k from <paramref name="from"/> on whose absolute position i0 + k has <paramref name="parity"/>.</summary>
    private static int First(int i0, int from, int parity) => ((i0 + from) & 1) == parity ? from : from + 1;

    /// <summary>The reversible (integers) or irreversible component transform of the first three components, in place.</summary>
    public static void ComponentTransform(object[] components)
    {
        if (components[0] is int[] y && components[1] is int[] cb && components[2] is int[] cr)
        {
            if (y.Length != cb.Length || y.Length != cr.Length)
                return;
            for (var i = 0; i < y.Length; i++)
            {
                var g = y[i] - ((cb[i] + cr[i]) >> 2);
                (y[i], cb[i], cr[i]) = (cr[i] + g, g, cb[i] + g);
            }
            return;
        }
        var f = new float[3][];
        for (var c = 0; c < 3; c++)
            f[c] = components[c] as float[] ?? (components[c] as int[] ?? []).Select(v => (float)v).ToArray();
        if (f[0].Length != f[1].Length || f[0].Length != f[2].Length)
            return;
        for (var i = 0; i < f[0].Length; i++)
        {
            var (luma, blue, red) = (f[0][i], f[1][i], f[2][i]);
            f[0][i] = luma + 1.402f * red;
            f[1][i] = luma - 0.34413f * blue - 0.71414f * red;
            f[2][i] = luma + 1.772f * blue;
        }
        for (var c = 0; c < 3; c++)
            components[c] = f[c];
    }
}
