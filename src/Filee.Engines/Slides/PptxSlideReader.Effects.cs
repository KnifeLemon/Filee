// Effects and 3D of PPTX shapes and pictures into the slide model (SlideEffects, SlideThreeD), ECMA-376 Part 1, 20.1.8
// and 20.1.5:
//
// - a:effectLst of the shape's spPr (or the placeholder's it inherits from), else of the theme effect style its
//   p:style effectRef names: outerShdw (blur, distance and direction as an offset, colour), glow, softEdge and
//   reflection (start and end opacity, end position, distance, blur). innerShdw, prstShdw, blur and a:effectDag are
//   not drawn (SlideCapabilities names them).
// - a:scene3d (the camera: its rot, else the turn and projection of its preset, roughly) and a:sp3d (extrusionH,
//   extrusionClr) of the same spPr, else of the theme effect style: a shape turned in space and extruded. Bevels,
//   contours, materials and the light rig are left out.

using System.Xml.Linq;
using SkiaSharp;

namespace Filee.Engines.Slides;

internal sealed partial class PptxSlideReader
{
    /// <summary>Angles in DrawingML are 60 000ths of a degree.</summary>
    private const double AngleUnit = 60000;

    /// <summary>The largest blur, glow, soft edge or shadow distance read, in points.</summary>
    private const float MaxEffectLength = 500;

    /// <summary>The effects of a shape from its spPr chain, else from the theme effect style its effectRef names; null for none.</summary>
    private SlideEffects? Effects(SlideContext context, IEnumerable<XElement?> properties, XElement? style)
    {
        var list = properties.Select(p => p?.Element(A + "effectLst")).FirstOrDefault(e => e is not null) ?? ThemeEffect(context, style)?.Element(A + "effectLst");
        if (list is null)
            return null;
        var reference = style?.Element(A + "effectRef");
        SKColor ColorOf(XElement effect, SKColor fallback) => context.Colors.Color(effect, reference is null ? null : context.Colors.Color(reference)) ?? fallback;
        float Length(XElement effect, string name) => Math.Clamp(Points(Number(effect.Attribute(name))), 0, MaxEffectLength);

        SlideShadow? shadow = null;
        if (list.Element(A + "outerShdw") is { } outer)
        {
            var distance = Length(outer, "dist");
            var direction = Number(outer.Attribute("dir")) / AngleUnit * Math.PI / 180;
            shadow = new SlideShadow(ColorOf(outer, SKColors.Black), Length(outer, "blurRad"), (float)(distance * Math.Cos(direction)), (float)(distance * Math.Sin(direction)));
        }
        var glow = list.Element(A + "glow") is { } g && Length(g, "rad") > 0 ? new SlideGlow(ColorOf(g, SKColors.Black), Length(g, "rad")) : null;
        var soft = list.Element(A + "softEdge") is { } s ? Length(s, "rad") : 0;
        SlideReflection? reflection = null;
        if (list.Element(A + "reflection") is { } r)
        {
            static float Fraction(XElement e, string name, double fallback) => (float)Math.Clamp(Number(e.Attribute(name), fallback) / 100000, 0, 1);
            reflection = new SlideReflection(Fraction(r, "stA", 100000), Fraction(r, "endA", 0), Fraction(r, "endPos", 100000), Length(r, "dist"), Length(r, "blurRad"));
        }
        var effects = new SlideEffects { Shadow = shadow, Glow = glow, SoftEdge = soft, Reflection = reflection };
        return effects.IsNone ? null : effects;
    }

    /// <summary>The theme effect style (a:effectStyle) a p:style effectRef names, or null.</summary>
    private static XElement? ThemeEffect(SlideContext context, XElement? style)
    {
        var index = (int)Number(style?.Element(A + "effectRef")?.Attribute("idx"));
        return index >= 1 ? context.FormatScheme?.Element(A + "effectStyleLst")?.Elements(A + "effectStyle").ElementAtOrDefault(index - 1) : null;
    }

    /// <summary>The turn in space and extrusion of a shape from its spPr chain (else its theme effect style); null for a flat one.</summary>
    private SlideThreeD? ThreeD(SlideContext context, IEnumerable<XElement?> properties, XElement? style)
    {
        var chain = properties.OfType<XElement>().ToList();
        var theme = ThemeEffect(context, style);
        var scene = chain.Select(p => p.Element(A + "scene3d")).FirstOrDefault(e => e is not null) ?? theme?.Element(A + "scene3d");
        var shape = chain.Select(p => p.Element(A + "sp3d")).FirstOrDefault(e => e is not null) ?? theme?.Element(A + "sp3d");
        if (scene is null && shape is null)
            return null;
        var camera = scene?.Element(A + "camera");
        var (x, y, z, perspective, skew, skewAngle) = Camera((string?)camera?.Attribute("prst"));
        if (camera?.Element(A + "rot") is { } rot)
        {
            // lat tilts about the x axis, lon turns about the y axis, rev about the view.
            static float Degrees(XAttribute? a) => (float)(Number(a) / AngleUnit % 360);
            static float Signed(float degrees) => degrees > 180 ? degrees - 360 : degrees;
            (x, y, z) = (Signed(Degrees(rot.Attribute("lat"))), -Signed(Degrees(rot.Attribute("lon"))), Signed(Degrees(rot.Attribute("rev"))));
        }
        var depth = shape is null ? 0 : Math.Clamp(Points(Number(shape.Attribute("extrusionH"))), 0, 10_000);
        var color = shape?.Element(A + "extrusionClr") is { } extrusion ? context.Colors.Color(extrusion) : null;
        var three = new SlideThreeD(x, y, z, depth, color, perspective, skew, skewAngle);
        return three.IsFlat ? null : three;
    }

    /// <summary>
    /// The turn (about x and y, and about the view), projection and oblique skew of a camera preset (ECMA-376 Part 1,
    /// 20.1.10.47), roughly: isometric presets turn 45° and tilt 35°, perspective ones about 20°, oblique ones are a
    /// parallel view with the depth along the named side.
    /// </summary>
    private static (float X, float Y, float Z, bool Perspective, float Skew, float SkewAngle) Camera(string? preset)
    {
        if (string.IsNullOrEmpty(preset) || preset.StartsWith("orthographic", StringComparison.Ordinal) || preset == "legacyObliqueFront")
            return (0, 0, 0, false, 0, 0);
        bool Has(string part) => preset.Contains(part, StringComparison.Ordinal);
        if (preset.StartsWith("oblique", StringComparison.Ordinal) || preset.StartsWith("legacyOblique", StringComparison.Ordinal))
        {
            // The depth shows towards the named side.
            var angle = (Has("Top") ? 90f : Has("Bottom") ? -90f : 0f) switch
            {
                90 => Has("Left") ? 135f : Has("Right") ? 45f : 90f,
                -90 => Has("Left") ? -135f : Has("Right") ? -45f : -90f,
                _ => Has("Left") ? 180f : 0f,
            };
            return (0, 0, 0, false, 0.5f, angle);
        }
        var turn = preset.StartsWith("isometric", StringComparison.Ordinal) ? 45f : 20f;
        var tilt = preset.StartsWith("isometric", StringComparison.Ordinal) ? 35.26f : 15f;
        var x = Has("Top") || Has("Above") ? -tilt : Has("Bottom") || Has("Below") ? tilt : 0;
        var y = Has("Left") ? turn : Has("Right") ? -turn : 0;
        if (Has("Relaxed"))
            x = -30;
        return (x, y, 0, preset.StartsWith("perspective", StringComparison.Ordinal) || preset.StartsWith("legacyPerspective", StringComparison.Ordinal), 0, 0);
    }
}
