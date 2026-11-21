// ICC profiles of XPS colours (ECMA-388 §15): ContextColor names a profile and gives the colour's channels in its
// colour space. A grey, RGB or CMYK profile is applied by sampling it once: a lattice of its colour space (every grey
// level, 16 steps per channel for RGB and CMYK, steps that are exact in 8 bits) goes through the profile into sRGB in
// one ImageMagick (Little CMS) transform, and each colour is interpolated between the lattice points around it.
// N-channel ('2CLR' … '8CLR') and Named Color profiles are read by Filee itself (XpsIccTransform). A profile that can't
// be read, or whose colour space doesn't match the colour's channels, is not usable, and the colour follows ECMA-388's
// rules for that case (XpsValues.ContextFallback).

using ImageMagick;
using SkiaSharp;

namespace Filee.Engines.Xps;

/// <summary>An ICC profile sampled into sRGB.</summary>
internal sealed class XpsColorProfile
{
    /// <summary>Lattice steps per channel of RGB and CMYK profiles (255 / 15 = 17: exact 8-bit values).</summary>
    private const int Steps = 16;

    private readonly int _grid;

    /// <summary>sRGB (8 bits per channel, R G B) of every lattice point, the first channel varying slowest.</summary>
    private readonly byte[] _lattice;

    /// <summary>An N-channel or Named Color profile, read instead of sampled.</summary>
    private readonly XpsIccTransform? _transform;

    private XpsColorProfile(int channels, int grid, byte[] lattice, XpsIccTransform? transform = null)
    {
        Channels = channels;
        _grid = grid;
        _lattice = lattice;
        _transform = transform;
    }

    /// <summary>Colour channels the profile takes: 1 (grey), 3 (RGB), 4 (CMYK), 2–8 (N-channel) or its named colours' count.</summary>
    public int Channels { get; }

    /// <summary>
    /// The profile sampled (or read, for N-channel and Named Color profiles); null when it can't be read or is of
    /// another colour space.
    /// </summary>
    public static XpsColorProfile? Create(byte[] icc)
    {
        if (XpsIccTransform.Handles(icc))
            return XpsIccTransform.Create(icc) is { } transform ? new XpsColorProfile(transform.Channels, 0, [], transform) : null;
        try
        {
            var profile = new ColorProfile(icc);
            var channels = profile.ColorSpace switch
            {
                ColorSpace.Gray or ColorSpace.LinearGray => 1,
                ColorSpace.sRGB or ColorSpace.RGB => 3,
                ColorSpace.CMYK => 4,
                _ => 0,
            };
            if (channels == 0)
                return null;
            var grid = channels == 1 ? 256 : Steps;
            var points = 1;
            for (var i = 0; i < channels; i++)
                points *= grid;
            // Grey levels go in as equal RGB channels.
            var stored = channels == 1 ? 3 : channels;
            var input = new byte[points * stored];
            for (var point = 0; point < points; point++)
            {
                var rest = point;
                for (var c = channels - 1; c >= 0; c--)
                {
                    input[point * stored + c] = (byte)(rest % grid * 255 / (grid - 1));
                    rest /= grid;
                }
                if (channels == 1)
                    input[point * 3 + 1] = input[point * 3 + 2] = input[point * 3];
            }
            using var image = new MagickImage();
            image.ReadPixels(input, new PixelReadSettings((uint)points, 1, StorageType.Char, channels == 4 ? PixelMapping.CMYK : PixelMapping.RGB));
            if (channels == 1)
                image.ColorSpace = ColorSpace.Gray;
            image.RenderingIntent = RenderingIntent.Relative;
            image.BlackPointCompensation = true;
            if (!image.TransformColorSpace(profile, ColorProfiles.SRGB))
                return null;
            using var pixels = image.GetPixels();
            var rgb = pixels.ToByteArray(PixelMapping.RGB);
            return rgb is not null && rgb.Length == points * 3 ? new XpsColorProfile(channels, grid, rgb) : null;
        }
        catch (Exception ex) when (ex is MagickException or ArgumentException or NotSupportedException or InvalidOperationException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>The sRGB colour of <paramref name="channels"/> (0–1 each, <see cref="Channels"/> of them).</summary>
    public SKColor Convert(float alpha, ReadOnlySpan<float> channels)
    {
        if (_transform is not null)
            return _transform.Convert(alpha, channels);
        Span<int> low = stackalloc int[4];
        Span<float> fraction = stackalloc float[4];
        for (var c = 0; c < Channels; c++)
        {
            var at = Math.Clamp(float.IsFinite(channels[c]) ? channels[c] : 0, 0, 1) * (_grid - 1);
            low[c] = Math.Min((int)at, _grid - 2);
            fraction[c] = at - low[c];
        }
        // Multilinear: every corner of the lattice cell around the colour, weighted by its nearness.
        Span<float> sum = stackalloc float[3];
        for (var corner = 0; corner < 1 << Channels; corner++)
        {
            var weight = 1f;
            var index = 0;
            for (var c = 0; c < Channels; c++)
            {
                var high = (corner >> c & 1) == 1;
                weight *= high ? fraction[c] : 1 - fraction[c];
                index = index * _grid + low[c] + (high ? 1 : 0);
            }
            if (weight == 0)
                continue;
            for (var k = 0; k < 3; k++)
                sum[k] += weight * _lattice[index * 3 + k];
        }
        return new SKColor(Byte(sum[0]), Byte(sum[1]), Byte(sum[2]), (byte)Math.Clamp(MathF.Round(alpha * 255), 0, 255));

        static byte Byte(float value) => (byte)Math.Clamp(MathF.Round(value), 0, 255);
    }
}
