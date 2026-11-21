// Output formatting of a decoded JPEG XR picture (ITU-T T.832 9.10) into 8-bit BGRA: chroma upsampling (Table 180),
// the conversion from the internal colour format, bias, scaling and post-scaling as the spec specifies them, then
// each output format's samples brought to 8 bits (see JpegXrDecoder for how), inside the window of the picture.

namespace Filee.Engines.JpegXr;

internal static class JpegXrOutput
{
    public static byte[] Bgra(JpegXrHeader header, JpegXrDecoder.Plane primary, JpegXrDecoder.Plane? alpha, bool premultiplied)
    {
        var plane = primary.Coded;
        var (width, height) = (header.Width, header.Height);
        var extended = header.ExtendedWidth;
        var samples = Upsampled(header, plane);
        var color = Formatted(header, plane, samples);
        var output = new byte[width * height * 4];
        // RGBE samples are floats after post-scaling.
        var bits = header.OutputColor == JpegXrHeader.Rgbe ? JpegXrHeader.Bd32F : header.BitDepth;
        var swapped = bits is JpegXrHeader.Bd5 or JpegXrHeader.Bd565 or JpegXrHeader.Bd10 && header.OutputColor == JpegXrHeader.Rgb;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = (y + header.Top) * extended + x + header.Left;
                var o = (y * width + x) * 4;
                byte r, g, b;
                switch (color.Count)
                {
                    case 1:
                    case 2:
                        r = g = b = Eight(color[0][at], bits, 0);
                        break;
                    case 3:
                        // Packed formats keep blue first (Table 185's swap); the others red first.
                        r = Eight(color[swapped ? 2 : 0][at], bits, 0);
                        g = Eight(color[1][at], bits, 1);
                        b = Eight(color[swapped ? 0 : 2][at], bits, 2);
                        break;
                    default:
                        {
                            // CMYK without a profile.
                            var k = 255 - Eight(color[3][at], bits, 0);
                            r = (byte)((255 - Eight(color[0][at], bits, 0)) * k / 255);
                            g = (byte)((255 - Eight(color[1][at], bits, 0)) * k / 255);
                            b = (byte)((255 - Eight(color[2][at], bits, 0)) * k / 255);
                            break;
                        }
                }
                (output[o], output[o + 1], output[o + 2], output[o + 3]) = (b, g, r, 255);
            }
        }
        if (alpha is not null)
        {
            var alphaPlane = alpha.Coded;
            var alphaSamples = Formatted(header, alphaPlane, [alphaPlane.Samples[0]]);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var a = Eight(alphaSamples[0][(y + header.Top) * extended + x + header.Left], header.BitDepth, 0);
                    var o = (y * width + x) * 4;
                    output[o + 3] = a;
                    if (premultiplied && a is > 0 and < 255)
                    {
                        for (var c = 0; c < 3; c++)
                            output[o + c] = (byte)Math.Min(255, (output[o + c] * 255 + a / 2) / a);
                    }
                }
            }
        }
        return output;
    }

    /// <summary>SamplingConversion( ) (Tables 178, 180): every component at full size.</summary>
    private static int[][] Upsampled(JpegXrHeader header, JpegXrPlane plane)
    {
        var result = new int[plane.Components][];
        var (width, height) = (header.ExtendedWidth, header.ExtendedHeight);
        for (var i = 0; i < plane.Components; i++)
        {
            var samples = plane.Samples[i];
            if (plane.Widths[i] == width && plane.Heights[i] == height)
            {
                result[i] = samples;
                continue;
            }
            var (h0, h1, h2, h3) = Filter(plane.ChromaCenteringX);
            var (w, h) = (plane.Widths[i], plane.Heights[i]);
            // Horizontal first into a full-width array of the original height, then vertical (the spec does vertical
            // first; with these two-tap filters the order only moves rounding).
            var wide = new int[width * h];
            for (var y = 0; y < h; y++)
            {
                for (var k = 0; k < w; k++)
                {
                    var row = y * w;
                    wide[y * width + 2 * k] = (h2 * samples[row + Math.Max(0, k - 1)] + h3 * samples[row + k] + 4) >> 3;
                    wide[y * width + 2 * k + 1] = (h0 * samples[row + k] + h1 * samples[row + Math.Min(w - 1, k + 1)] + 4) >> 3;
                }
            }
            if (h == height)
            {
                result[i] = wide;
                continue;
            }
            (h0, h1, h2, h3) = Filter(plane.ChromaCenteringY);
            var full = new int[width * height];
            for (var x = 0; x < width; x++)
            {
                for (var k = 0; k < h; k++)
                {
                    full[2 * k * width + x] = (h2 * wide[Math.Max(0, k - 1) * width + x] + h3 * wide[k * width + x] + 4) >> 3;
                    full[(2 * k + 1) * width + x] = (h0 * wide[k * width + x] + h1 * wide[Math.Min(h - 1, k + 1) * width + x] + 4) >> 3;
                }
            }
            result[i] = full;
        }
        return result;
    }

    /// <summary>Table 181's filter for a chroma centering (unknown positions as 0).</summary>
    private static (int, int, int, int) Filter(int centering) => centering switch
    {
        1 => (5, 3, 1, 7),
        2 => (6, 2, 2, 6),
        3 => (7, 1, 3, 5),
        4 => (8, 0, 4, 4),
        _ => (4, 4, 0, 8),
    };

    /// <summary>
    /// ConvertInternalToOutputClrFmt( ), AddBias( ), ComputeScaling( ) and PostscalingProcess( ) (Tables 182–193): the
    /// output components, in the output bit depth's representation.
    /// </summary>
    private static List<int[]> Formatted(JpegXrHeader header, JpegXrPlane plane, int[][] samples)
    {
        var count = samples[0].Length;
        var output = header.OutputColor;
        var internalColor = plane.Color;
        var bits = header.BitDepth;
        List<int[]> components;
        if (internalColor is JpegXrColor.Yuv420 or JpegXrColor.Yuv422 or JpegXrColor.Yuv444 && samples.Length >= 3)
        {
            // InvColorFmtConvert2 (Table 185); YUV output formats are taken as the colours they code.
            var (r, g, b) = (new int[count], new int[count], new int[count]);
            for (var p = 0; p < count; p++)
            {
                var t = -samples[1][p];
                g[p] = samples[0][p] - (t >> 1);
                r[p] = t + g[p] + ((-samples[2][p]) >> 1);
                b[p] = samples[2][p] + r[p];
            }
            var swap = bits is JpegXrHeader.Bd5 or JpegXrHeader.Bd565 or JpegXrHeader.Bd10 && !header.RedBlueNotSwapped;
            components = swap ? [b, g, r] : [r, g, b];
            output = JpegXrHeader.Rgb;
        }
        else if (internalColor == JpegXrColor.Yuvk && samples.Length >= 4 && output == JpegXrHeader.Cmyk)
        {
            // InvColorFmtConvert3 (Table 186).
            var (c, m, y, k) = (new int[count], new int[count], new int[count], new int[count]);
            for (var p = 0; p < count; p++)
            {
                k[p] = samples[3][p] + (samples[0][p] >> 1);
                m[p] = k[p] - samples[0][p] - (samples[1][p] >> 1);
                c[p] = samples[1][p] + m[p] + (samples[2][p] >> 1);
                y[p] = c[p] - samples[2][p];
            }
            components = [c, m, y, k];
        }
        else if (internalColor == JpegXrColor.Yuvk && samples.Length >= 4)
        {
            // InvColorFmtConvert4 (Table 187): CMYKDIRECT.
            components = [(int[])samples[1].Clone(), (int[])samples[2].Clone(), (int[])samples[3].Clone(), (int[])samples[0].Clone()];
            output = JpegXrHeader.CmykDirect;
        }
        else
        {
            components = [.. samples.Take(Math.Min(samples.Length, 4)).Select(s => (int[])s.Clone())];
            if (components.Count == 3 && output != JpegXrHeader.Rgbe)
                output = JpegXrHeader.Rgb;
        }

        // AddBias (Table 188).
        var scale = plane.Scaled ? 3 : 0;
        var bias = bits switch
        {
            JpegXrHeader.Bd5 => 1 << 4,
            JpegXrHeader.Bd565 => 1 << 5,
            JpegXrHeader.Bd8 => 1 << 7,
            JpegXrHeader.Bd10 => 1 << 9,
            JpegXrHeader.Bd16 => 1 << 15,
            _ => 0,
        };
        if (bits is JpegXrHeader.Bd16 or JpegXrHeader.Bd16S or JpegXrHeader.Bd32S)
            bias >>= Math.Min(plane.ShiftBits, 31);
        if (output == JpegXrHeader.Cmyk && components.Count >= 4)
        {
            for (var i = 0; i < 4; i++)
                Add(components[i], i < 3 ? (bias >> 1) << scale : -((bias >> 1) << scale));
        }
        else if (output != JpegXrHeader.Rgbe)
        {
            foreach (var component in components)
                Add(component, bias << scale);
        }

        // ComputeScaling (Table 189).
        if (plane.Scaled)
        {
            var rounding = bits is JpegXrHeader.Bd1White or JpegXrHeader.Bd1Black or JpegXrHeader.Bd16 ? 4 : 3;
            for (var i = 0; i < components.Count; i++)
            {
                var shift = bits == JpegXrHeader.Bd565 && i != 1 ? 4 : 3;
                var component = components[i];
                for (var p = 0; p < count; p++)
                    component[p] = (component[p] + rounding) >> shift;
            }
        }
        else if (bits == JpegXrHeader.Bd565)
        {
            for (var i = 0; i < components.Count; i++)
            {
                if (i == 1)
                    continue;
                var component = components[i];
                for (var p = 0; p < count; p++)
                    component[p] >>= 1;
            }
        }

        // PostscalingProcess (Table 190): RGBE becomes floats here, the others keep their integers.
        if (output == JpegXrHeader.Rgbe && components.Count >= 3)
        {
            for (var p = 0; p < count; p++)
            {
                for (var i = 0; i < 3; i++)
                    components[i][p] = BitConverter.SingleToInt32Bits(Rgbe(components, p, i));
            }
            components = components.Take(3).ToList();
        }
        else if (bits is JpegXrHeader.Bd16 or JpegXrHeader.Bd16S or JpegXrHeader.Bd32S)
        {
            var shift = Math.Min(plane.ShiftBits, 31);
            foreach (var component in components)
            {
                for (var p = 0; p < count; p++)
                    component[p] <<= shift;
            }
        }
        else if (bits == JpegXrHeader.Bd32F)
        {
            foreach (var component in components)
            {
                for (var p = 0; p < count; p++)
                    component[p] = Float(component[p], plane.LenMantissa, plane.ExpBias);
            }
        }
        return components;
    }

    private static void Add(int[] component, int value)
    {
        for (var p = 0; p < component.Length; p++)
            component[p] += value;
    }

    /// <summary>PostScalingF2 (Table 193) for one channel, as the float it stands for.</summary>
    private static float Rgbe(List<int[]> components, int p, int channel)
    {
        Span<int> mantissa = stackalloc int[3];
        Span<int> exponent = stackalloc int[3];
        for (var i = 0; i < 3; i++)
        {
            var v = components[i][p];
            if (v <= 0)
                (mantissa[i], exponent[i]) = (0, 0);
            else if ((v >> 7) > 1)
                (mantissa[i], exponent[i]) = ((v & 0x7F) + 128, v >> 7);
            else
                (mantissa[i], exponent[i]) = (v, 1);
        }
        var e = Math.Max(exponent[0], Math.Max(exponent[1], exponent[2]));
        var m = mantissa[channel];
        if (e > exponent[channel])
            m = (2 * m + 1) >> Math.Min(31, e - exponent[channel] + 1);
        return e == 0 ? 0 : m * MathF.Pow(2, e - 128 - 8);
    }

    /// <summary>PostScalingFl (Table 192) for BD32F: the float's bits.</summary>
    private static int Float(int x, int lenMantissa, int expBias)
    {
        if (lenMantissa is < 0 or > 23)
            return 0;
        var sign = x < 0 ? 1 : 0;
        long value = Math.Abs((long)x);
        var e = (int)(value >> lenMantissa);
        var m = (int)((value & ((1L << lenMantissa) - 1)) | (1L << lenMantissa));
        if (e == 0)
        {
            m ^= 1 << lenMantissa;
            e = 1;
        }
        e = e - expBias + 127;
        while (m < (1 << lenMantissa) && e > 1 && m > 0)
        {
            e--;
            m <<= 1;
        }
        if (m < (1 << lenMantissa))
            e = 0;
        else
            m ^= 1 << lenMantissa;
        m <<= 23 - lenMantissa;
        return (sign << 31) | (Math.Clamp(e, 0, 255) << 23) | (m & 0x7FFFFF);
    }

    /// <summary>A sample of the output bit depth as 8 bits (channel: 1 is green, for 5-6-5).</summary>
    private static byte Eight(int value, int bits, int channel) => bits switch
    {
        JpegXrHeader.Bd8 => (byte)Math.Clamp(value, 0, 255),
        JpegXrHeader.Bd16 => (byte)(Math.Clamp(value, 0, 65535) >> 8),
        JpegXrHeader.Bd5 => (byte)(Math.Clamp(value, 0, 31) * 255 / 31),
        JpegXrHeader.Bd565 => channel == 1 ? (byte)(Math.Clamp(value, 0, 63) * 255 / 63) : (byte)(Math.Clamp(value, 0, 31) * 255 / 31),
        JpegXrHeader.Bd10 => (byte)(Math.Clamp(value, 0, 1023) * 255 / 1023),
        JpegXrHeader.Bd1White or JpegXrHeader.Bd1Black => value >= 1 ? (byte)255 : (byte)0,
        JpegXrHeader.Bd16S => Linear(Math.Clamp(value, -32768, 32767) / 8192f),
        JpegXrHeader.Bd32S => Linear(value / 16777216f),
        JpegXrHeader.Bd16F => Linear((float)BitConverter.Int16BitsToHalf((short)(value < 0 ? 0x8000 | Math.Min(-value, 32767) : Math.Min(value, 32767)))),
        JpegXrHeader.Bd32F => Linear(BitConverter.Int32BitsToSingle(value)),
        _ => (byte)Math.Clamp(value, 0, 255),
    };

    /// <summary>A linear scRGB value (1 = white) with the sRGB curve, in 8 bits.</summary>
    private static byte Linear(float value)
    {
        if (!float.IsFinite(value) || value <= 0)
            return 0;
        if (value >= 1)
            return 255;
        var encoded = value <= 0.0031308f ? 12.92f * value : 1.055f * MathF.Pow(value, 1 / 2.4f) - 0.055f;
        return (byte)Math.Clamp(MathF.Round(encoded * 255), 0, 255);
    }
}
