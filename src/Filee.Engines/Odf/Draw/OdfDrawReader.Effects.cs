// Effects and extrusion of ODF shapes into the slide model (SlideEffects, SlideThreeD):
//
// - Shadows: draw:shadow="visible" with draw:shadow-offset-x / -y, draw:shadow-color and draw:shadow-opacity
//   (OpenDocument 1.3 Part 3, 20.158–20.162), and LibreOffice's loext:shadow-blur. Without an offset or colour, the
//   values LibreOffice gives a new shadow (0.2 cm, grey).
// - Glow and soft edges: LibreOffice's loext:glow-radius / -color / -transparency and loext:softedge-radius.
// - Extrusion (draw:enhanced-geometry draw:extrusion="true", 19.148–19.169): its depth (draw:extrusion-depth, default
//   36pt), the turn about x and y (draw:extrusion-rotation-angle; about z it is the shape's own draw:transform),
//   dr3d:projection (parallel by default) with draw:extrusion-skew in parallel (default 50 45), and its colour
//   (draw:extrusion-color with draw:secondary-fill-color). Light, diffusion, shininess and the viewpoint are left
//   to the renderer's own light.

using System.Globalization;
using System.Xml.Linq;
using Filee.Engines.Slides;
using SkiaSharp;
using OdfNames = Filee.Engines.Hwp.Hwpx.Odt.Odf;

namespace Filee.Engines.Odf.Draw;

internal sealed partial class OdfDrawReader
{
    /// <summary>LibreOffice's extensions (loext:glow-radius, loext:softedge-radius, loext:shadow-blur).</summary>
    internal static readonly XNamespace Loext = "urn:org:documentfoundation:names:experimental:office:xmlns:loext:1.0";

    /// <summary>The largest blur, glow, soft edge or shadow offset read, in points.</summary>
    private const float MaxEffectLength = 500;

    /// <summary>LibreOffice's offset of a new shadow: 0.2 cm.</summary>
    private const float DefaultShadowOffset = 72 * 0.2f / 2.54f;

    /// <summary>The effects of graphic properties; null for none.</summary>
    private static SlideEffects? Effects(OdfProps props)
    {
        float Length(XName name, float fallback = 0) => Math.Clamp(Points(props[name]) ?? fallback, -MaxEffectLength, MaxEffectLength);
        static byte Alpha(double? percent, double fallback) => (byte)Math.Round(Math.Clamp(percent ?? fallback, 0, 100) / 100 * 255);

        SlideShadow? shadow = null;
        if (props[Draw + "shadow"] == "visible")
        {
            var color = OdfGraphicStyles.Color(props[Draw + "shadow-color"]) ?? new SKColor(0x80, 0x80, 0x80);
            shadow = new SlideShadow(color.WithAlpha(Alpha(OdfNames.Percent(props[Draw + "shadow-opacity"]), 100)), Math.Max(0, Length(Loext + "shadow-blur")),
                Length(Draw + "shadow-offset-x", DefaultShadowOffset), Length(Draw + "shadow-offset-y", DefaultShadowOffset));
        }
        SlideGlow? glow = null;
        if (Length(Loext + "glow-radius") is > 0 and var radius)
        {
            var color = OdfGraphicStyles.Color(props[Loext + "glow-color"]) ?? SKColors.Black;
            glow = new SlideGlow(color.WithAlpha(Alpha(100 - (OdfNames.Percent(props[Loext + "glow-transparency"]) ?? 0), 100)), radius);
        }
        var effects = new SlideEffects { Shadow = shadow, Glow = glow, SoftEdge = Math.Max(0, Length(Loext + "softedge-radius")) };
        return effects.IsNone ? null : effects;
    }

    /// <summary>The extrusion of an enhanced geometry (draw:extrusion="true"), with the shape's graphic properties for its colour.</summary>
    private static SlideThreeD Extrusion(XElement geometry, OdfProps props)
    {
        string? Get(XName name) => (string?)geometry.Attribute(name);
        var depth = Get(Draw + "extrusion-depth") is { } d ? Points(d.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()) ?? 36 : 36;
        var (ax, ay) = Pair(Get(Draw + "extrusion-rotation-angle"), (0, 0));
        var perspective = Get(Dr3d + "projection") == "perspective";
        var (amount, angle) = perspective ? (0, 0) : Pair(Get(Draw + "extrusion-skew"), (50, 45), firstIsAngle: false);
        var color = Get(Draw + "extrusion-color") == "true" ? OdfGraphicStyles.Color(props[Draw + "secondary-fill-color"]) : null;
        // ODF turns with y up (a positive turn about x brings the top forward); the model's RotationX tilts it away.
        return new SlideThreeD(-ax, ay, 0, Math.Clamp(depth, 0, 10_000), color, perspective, (float)Math.Clamp(amount / 100, 0, 10), angle);
    }

    /// <summary>Two values of an attribute (angles in degrees with or without their unit; with <paramref name="firstIsAngle"/> off, a number then an angle).</summary>
    private static (float First, float Second) Pair(string? value, (float, float) fallback, bool firstIsAngle = true)
    {
        var parts = (value ?? "").Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return fallback;
        float? Number(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? (float)Math.Clamp(v, -1e6, 1e6) : null;
        var first = firstIsAngle ? Degrees(parts[0]) : Number(parts[0]);
        var second = Degrees(parts[1]);
        return first is { } a && second is { } b ? (a, b) : fallback;
    }

    /// <summary>An ODF angle (18.3.1): degrees without a unit, else deg, rad or grad; with <paramref name="wrap"/>, less than a full turn.</summary>
    private static float? Degrees(string text, bool wrap = true)
    {
        text = text.Trim();
        var (number, factor) = text.EndsWith("deg", StringComparison.Ordinal) ? (text[..^3], 1.0)
            : text.EndsWith("grad", StringComparison.Ordinal) ? (text[..^4], 0.9)
            : text.EndsWith("rad", StringComparison.Ordinal) ? (text[..^3], 180 / Math.PI)
            : (text, 1.0);
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var angle) && double.IsFinite(angle)
            ? (float)(wrap ? angle * factor % 360 : Math.Clamp(angle * factor, -1e6, 1e6))
            : null;
    }
}
