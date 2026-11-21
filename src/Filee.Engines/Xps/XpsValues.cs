// Attribute values of XPS markup: numbers (invariant culture, finite only), points ("x,y"), point lists, booleans,
// matrices ("m11,m12,m21,m22,dx,dy") and colours: "#RRGGBB", "#AARRGGBB", scRGB ("sc#A,R,G,B" or "sc#R,G,B", linear
// light) and ContextColor (an ICC profile and its channels). XpsDocument applies a ContextColor's profile
// (XpsColorProfile); here it follows ECMA-388 §15.2's rules for a colour without a usable profile: one channel, or two
// or three of a named or N-channel colour, is the first channel as sRGB grey, three channels are scRGB, four CMYK, and
// more than four the first four as CMYK.

using System.Globalization;
using SkiaSharp;

namespace Filee.Engines.Xps;

internal static class XpsValues
{
    private static readonly char[] Separators = [',', ' ', '\t', '\r', '\n'];

    public static float? Number(string? text) =>
        text is not null && float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && float.IsFinite(value)
            ? value
            : null;

    public static bool Bool(string? text, bool fallback) => text?.Trim().ToLowerInvariant() switch
    {
        "true" => true,
        "false" => false,
        _ => fallback,
    };

    /// <summary>The numbers of a comma or space separated list; null when one isn't a number.</summary>
    public static float[]? Numbers(string? text)
    {
        if (text is null)
            return null;
        var parts = text.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        var numbers = new float[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (Number(parts[i]) is not { } value)
                return null;
            numbers[i] = value;
        }
        return numbers;
    }

    public static SKPoint? Point(string? text) =>
        Numbers(text) is [var x, var y] ? new SKPoint(x, y) : null;

    /// <summary>"x,y x,y …" as points; a damaged list ends where it breaks.</summary>
    public static List<SKPoint> Points(string? text)
    {
        var points = new List<SKPoint>();
        if (text is null)
            return points;
        var parts = text.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            if (Number(parts[i]) is not { } x || Number(parts[i + 1]) is not { } y)
                break;
            points.Add(new SKPoint(x, y));
        }
        return points;
    }

    /// <summary>"x,y,width,height" as a rectangle; null when damaged or of negative size.</summary>
    public static SKRect? Rect(string? text) =>
        Numbers(text) is [var x, var y, var width, var height] && width >= 0 && height >= 0
            ? SKRect.Create(x, y, width, height)
            : null;

    /// <summary>"m11,m12,m21,m22,dx,dy" as a Skia matrix (x' = m11·x + m21·y + dx).</summary>
    public static SKMatrix? Matrix(string? text) =>
        Numbers(text) is [var m11, var m12, var m21, var m22, var dx, var dy]
            ? new SKMatrix(m11, m21, dx, m12, m22, dy, 0, 0, 1)
            : null;

    /// <summary>A colour; null when it can't be read.</summary>
    public static SKColor? Color(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim();
        if (text.StartsWith('#'))
        {
            var hex = text.AsSpan(1);
            if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                return null;
            return hex.Length == 6 ? new SKColor(value | 0xFF000000) : new SKColor(value);
        }
        if (text.StartsWith("sc#", StringComparison.OrdinalIgnoreCase))
        {
            return Numbers(text[3..]) switch
            {
                [var a, var r, var g, var b] => FromLinear(a, r, g, b),
                [var r, var g, var b] => FromLinear(1, r, g, b),
                _ => null,
            };
        }
        if (ContextColor(text) is { } context)
            return ContextFallback(context.Values);
        return null;
    }

    /// <summary>"ContextColor /profile.icc a,c1,c2…": the profile's reference and the numbers (alpha first); null otherwise.</summary>
    public static (string Profile, float[] Values)? ContextColor(string? text)
    {
        text = text?.Trim();
        if (text is null || !text.StartsWith("ContextColor ", StringComparison.OrdinalIgnoreCase))
            return null;
        var rest = text["ContextColor ".Length..].TrimStart();
        var space = rest.IndexOfAny([' ', '\t']);
        if (space <= 0 || Numbers(rest[(space + 1)..]) is not { Length: >= 2 } values)
            return null;
        return (rest[..space], values);
    }

    /// <summary>A ContextColor without a usable profile (alpha, then the channels), by ECMA-388's rules.</summary>
    public static SKColor ContextFallback(ReadOnlySpan<float> values)
    {
        var alpha = values[0];
        var channels = values[1..];
        return channels.Length switch
        {
            3 => FromLinear(alpha, channels[0], channels[1], channels[2]),
            >= 4 => Gamma(alpha, (1 - channels[0]) * (1 - channels[3]), (1 - channels[1]) * (1 - channels[3]), (1 - channels[2]) * (1 - channels[3])),
            _ => Gamma(alpha, channels[0], channels[0], channels[0]),
        };
    }

    /// <summary>Channels already in sRGB (0–1).</summary>
    private static SKColor Gamma(float a, float r, float g, float b) =>
        new(Byte(r), Byte(g), Byte(b), Byte(a));

    /// <summary>scRGB channels (linear light) as sRGB.</summary>
    private static SKColor FromLinear(float a, float r, float g, float b) =>
        new(Byte(Encode(r)), Byte(Encode(g)), Byte(Encode(b)), Byte(a));

    private static float Encode(float linear) =>
        linear <= 0.0031308f ? 12.92f * linear : 1.055f * MathF.Pow(linear, 1 / 2.4f) - 0.055f;

    private static byte Byte(float value) => (byte)Math.Clamp(MathF.Round(value * 255), 0, 255);
}
