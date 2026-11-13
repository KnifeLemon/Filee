// GIMP's layer modes, drawn as GIMP 2.10 and 3 draw them, for merging the layers of an .xcf (Xcf). A mode blends a layer
// with what is under it in its blend space (linear RGB, perceptual RGB or CIE Lab), then composites the result in its
// composite space with one of four ways of mixing alpha (union, clip to backdrop, clip to layer, intersection). Colours are
// straight (not premultiplied) floats, four per pixel. The legacy modes of GIMP 2.8 and earlier blend in perceptual RGB
// and keep the alpha of what is under the layer. The formulas follow GIMP's layer-mode operations; they are written
// again here, not taken from GIMP's code.

namespace Filee.Engines.Magick;

internal static class XcfBlend
{
    /// <summary>Where a mode blends or composites (GimpLayerColorSpace; GIMP 3's "non-linear" is perceptual here).</summary>
    internal enum Space { Linear = 1, Perceptual = 2, Lab = 3 }

    /// <summary>How the layer's alpha and the backdrop's mix (GimpLayerCompositeMode).</summary>
    internal enum Composite { Union = 1, ClipToBackdrop = 2, ClipToLayer = 3, Intersection = 4 }

    /// <summary>A layer mode with the spaces and composite mode it is drawn in.</summary>
    internal readonly record struct Mode(int Id, Space BlendSpace, Space CompositeSpace, Composite Composite);

    // GimpLayerMode, as stored in PROP_MODE.
    internal const int NormalLegacy = 0, Dissolve = 1, BehindLegacy = 2, MultiplyLegacy = 3, ScreenLegacy = 4,
        OverlayLegacy = 5, DifferenceLegacy = 6, AdditionLegacy = 7, SubtractLegacy = 8, DarkenLegacy = 9,
        LightenLegacy = 10, HueLegacy = 11, SaturationLegacy = 12, ColorLegacy = 13, ValueLegacy = 14,
        DivideLegacy = 15, DodgeLegacy = 16, BurnLegacy = 17, HardLightLegacy = 18, SoftLightLegacy = 19,
        GrainExtractLegacy = 20, GrainMergeLegacy = 21, ColorEraseLegacy = 22, Overlay = 23, LchHue = 24,
        LchChroma = 25, LchColor = 26, LchLightness = 27, Normal = 28, Behind = 29, Multiply = 30, Screen = 31,
        Difference = 32, Addition = 33, Subtract = 34, Darken = 35, Lighten = 36, HsvHue = 37, HsvSaturation = 38,
        HslColor = 39, HsvValue = 40, Divide = 41, Dodge = 42, Burn = 43, HardLight = 44, SoftLight = 45,
        GrainExtract = 46, GrainMerge = 47, VividLight = 48, PinLight = 49, LinearLight = 50, HardMix = 51,
        Exclusion = 52, LinearBurn = 53, LumaDarken = 54, LumaLighten = 55, Luminance = 56, ColorErase = 57,
        Erase = 58, Merge = 59, Split = 60, PassThrough = 61;

    /// <summary>The highest mode id GIMP stores; a larger one is drawn as Normal.</summary>
    internal const int LastMode = PassThrough;

    /// <summary>Modes of GIMP 2.8 and earlier, with their own alpha (Normal, Dissolve and Behind aside).</summary>
    private static bool IsLegacyBlend(int mode) => mode is >= MultiplyLegacy and <= GrainMergeLegacy;

    /// <summary>Linear-light luminance of GIMP's sRGB primaries (D50, as babl adapts them).</summary>
    private const float LumaR = 0.22248840f, LumaG = 0.71690369f, LumaB = 0.06060791f;

    private const float Epsilon = 1e-6f;

    /// <summary>
    /// The mode a layer is drawn in: <paramref name="mode"/> with the blend space, composite space and composite mode
    /// the file gives (0 when it gives none: the mode's own). GIMP stores "Auto" as the negative of what it meant, and
    /// GIMP 3's "RGB (perceptual)" as 4. Legacy modes always blend and composite in perceptual RGB.
    /// </summary>
    public static Mode Resolve(int mode, int blendSpace, int compositeSpace, int composite)
    {
        if (mode is < 0 or > LastMode)
            mode = Normal;
        var (blend, space, how) = Defaults(mode);
        if (mode > ColorEraseLegacy)
        {
            blend = SpaceOf(blendSpace) ?? blend;
            space = SpaceOf(compositeSpace) ?? space;
            how = Math.Abs(composite) is >= 1 and <= 4 ? (Composite)Math.Abs(composite) : how;
        }
        return new Mode(mode, blend, space, how);
    }

    private static Space? SpaceOf(int stored) => Math.Abs(stored) switch
    {
        1 => Space.Linear,
        2 or 4 => Space.Perceptual,
        3 => Space.Lab,
        _ => null,
    };

    /// <summary>GIMP's own spaces and composite mode for each layer mode (gimp-layer-modes.c).</summary>
    private static (Space Blend, Space Composite, Composite How) Defaults(int mode) => mode switch
    {
        NormalLegacy or Dissolve or BehindLegacy => (Space.Perceptual, Space.Perceptual, Composite.Union),
        <= ColorEraseLegacy => (Space.Perceptual, Space.Perceptual, Composite.ClipToBackdrop),
        Overlay => (Space.Perceptual, Space.Linear, Composite.ClipToBackdrop),
        >= LchHue and <= LchLightness => (Space.Lab, Space.Linear, Composite.ClipToBackdrop),
        Normal or Behind or Merge or PassThrough => (Space.Linear, Space.Linear, Composite.Union),
        Multiply or Addition or Subtract or Darken or Lighten or Divide or Luminance or ColorErase or Erase or Split
            => (Space.Linear, Space.Linear, Composite.ClipToBackdrop),
        _ => (Space.Perceptual, Space.Linear, Composite.ClipToBackdrop),
    };

    /// <summary>Rows for the blend to work in, as wide as the canvas.</summary>
    internal sealed class Scratch(int width)
    {
        public float[] In { get; } = new float[width * 4];
        public float[] Layer { get; } = new float[width * 4];
        public float[] Comp { get; } = new float[width * 4];
    }

    /// <summary>
    /// Draws one row of a layer onto the row under it: <paramref name="row"/>, in <paramref name="rowSpace"/>, becomes
    /// the result in the mode's composite space. <paramref name="layer"/> (in <paramref name="layerSpace"/>, changed in
    /// place) covers pixels <paramref name="from"/> to <paramref name="to"/>; each pixel's opacity is
    /// <paramref name="opacity"/> times its <paramref name="mask"/> value (none: 1).
    /// </summary>
    public static void Draw(Mode mode, float[] row, ref Space rowSpace, float[] layer, Space layerSpace, float opacity,
        ReadOnlySpan<float> mask, int from, int to, Scratch scratch)
    {
        var space = mode.CompositeSpace;
        Convert(row, rowSpace, space);
        rowSpace = space;
        if (to <= from || opacity <= 0)
            return;
        var start = from * 4;
        var length = (to - from) * 4;
        Convert(layer.AsSpan(start, length), layerSpace, space);

        var id = mode.Id;
        if (IsLegacyBlend(id))
        {
            DrawLegacy(id, row, layer, opacity, mask, from, to);
            return;
        }
        switch (id)
        {
            case BehindLegacy or Behind:
                DrawBehind(mode.Composite, row, layer, opacity, mask, from, to);
                return;
            case Erase:
                DrawErase(mode.Composite, row, layer, opacity, mask, from, to);
                return;
            case Merge:
                DrawMerge(mode.Composite, row, layer, opacity, mask, from, to);
                return;
            case Split:
                DrawSplit(mode.Composite, row, layer, opacity, mask, from, to);
                return;
        }

        // The blend: the layer itself for Normal (and Dissolve, drawn as Normal), else the mode's function of both,
        // in the blend space.
        float[] comp;
        if (id is NormalLegacy or Dissolve or Normal)
        {
            comp = layer;
        }
        else
        {
            comp = scratch.Comp;
            var blend = mode.BlendSpace;
            if (blend == space)
            {
                Blend(id, row, layer, comp, start, length);
            }
            else
            {
                // The backdrop and the layer stay in the composite space for compositing: they blend as copies.
                row.AsSpan(start, length).CopyTo(scratch.In.AsSpan(start));
                layer.AsSpan(start, length).CopyTo(scratch.Layer.AsSpan(start));
                Convert(scratch.In.AsSpan(start, length), space, blend);
                Convert(scratch.Layer.AsSpan(start, length), space, blend);
                Blend(id, scratch.In, scratch.Layer, comp, start, length);
                Convert(comp.AsSpan(start, length), blend, space);
            }
        }

        if (id is ColorErase or ColorEraseLegacy)
            CompositeSubtractive(mode.Composite, row, layer, comp, opacity, mask, from, to);
        else
            CompositeAll(mode.Composite, row, layer, comp, opacity, mask, from, to);
    }

    /// <summary>
    /// Mixes a pass-through group's result back over what was under it at the group's opacity (GIMP's "replace").
    /// Both rows are in the same space.
    /// </summary>
    public static void Replace(float[] row, float[] group, float opacity, ReadOnlySpan<float> mask, int from, int to)
    {
        for (var x = from; x < to; x++)
        {
            var i = x * 4;
            var o = opacity * MaskAt(mask, x - from);
            var newAlpha = (group[i + 3] - row[i + 3]) * o + row[i + 3];
            var ratio = o;
            if (newAlpha != 0)
                ratio *= group[i + 3] / newAlpha;
            for (var c = 0; c < 3; c++)
                row[i + c] = (group[i + c] - row[i + c]) * ratio + row[i + c];
            row[i + 3] = newAlpha;
        }
    }

    private static float MaskAt(ReadOnlySpan<float> mask, int index) => mask.IsEmpty ? 1 : mask[index];

    /// <summary>The non-subtractive composite modes (union, clip to backdrop, clip to layer, intersection).</summary>
    private static void CompositeAll(Composite how, float[] row, float[] layer, float[] comp, float opacity,
        ReadOnlySpan<float> mask, int from, int to)
    {
        for (var x = from; x < to; x++)
        {
            var i = x * 4;
            var o = opacity * MaskAt(mask, x - from);
            var inAlpha = row[i + 3];
            switch (how)
            {
                case Composite.Union:
                    {
                        var layerAlpha = layer[i + 3] * o;
                        var newAlpha = layerAlpha + (1 - layerAlpha) * inAlpha;
                        if (layerAlpha == 0 || newAlpha == 0)
                        {
                            // The colour under it stays.
                        }
                        else if (inAlpha == 0)
                        {
                            row[i] = layer[i];
                            row[i + 1] = layer[i + 1];
                            row[i + 2] = layer[i + 2];
                        }
                        else
                        {
                            var ratio = layerAlpha / newAlpha;
                            for (var c = 0; c < 3; c++)
                                row[i + c] = ratio * (inAlpha * (comp[i + c] - layer[i + c]) + layer[i + c] - row[i + c]) + row[i + c];
                        }
                        row[i + 3] = newAlpha;
                        break;
                    }
                case Composite.ClipToBackdrop:
                    {
                        var layerAlpha = comp[i + 3] * o;
                        if (inAlpha != 0 && layerAlpha != 0)
                        {
                            for (var c = 0; c < 3; c++)
                                row[i + c] = comp[i + c] * layerAlpha + row[i + c] * (1 - layerAlpha);
                        }
                        break;
                    }
                case Composite.ClipToLayer:
                    {
                        var layerAlpha = layer[i + 3] * o;
                        if (layerAlpha == 0)
                        {
                        }
                        else if (inAlpha == 0)
                        {
                            row[i] = layer[i];
                            row[i + 1] = layer[i + 1];
                            row[i + 2] = layer[i + 2];
                        }
                        else
                        {
                            for (var c = 0; c < 3; c++)
                                row[i + c] = comp[i + c] * inAlpha + layer[i + c] * (1 - inAlpha);
                        }
                        row[i + 3] = layerAlpha;
                        break;
                    }
                default:
                    {
                        var newAlpha = inAlpha * comp[i + 3] * o;
                        if (newAlpha != 0)
                        {
                            row[i] = comp[i];
                            row[i + 1] = comp[i + 1];
                            row[i + 2] = comp[i + 2];
                        }
                        row[i + 3] = newAlpha;
                        break;
                    }
            }
        }
    }

    /// <summary>The subtractive composite modes, for Color erase: comp's alpha is what is left of the overlap.</summary>
    private static void CompositeSubtractive(Composite how, float[] row, float[] layer, float[] comp, float opacity,
        ReadOnlySpan<float> mask, int from, int to)
    {
        for (var x = from; x < to; x++)
        {
            var i = x * 4;
            var o = opacity * MaskAt(mask, x - from);
            var inAlpha = row[i + 3];
            var layerAlpha = layer[i + 3] * o;
            var compAlpha = comp[i + 3];
            switch (how)
            {
                case Composite.Union:
                    {
                        var newAlpha = inAlpha + layerAlpha - (2 - compAlpha) * inAlpha * layerAlpha;
                        if (layerAlpha == 0 || newAlpha == 0)
                        {
                        }
                        else if (inAlpha == 0)
                        {
                            row[i] = layer[i];
                            row[i + 1] = layer[i + 1];
                            row[i + 2] = layer[i + 2];
                        }
                        else
                        {
                            var ratio = inAlpha / newAlpha;
                            var layerCoefficient = 1 / inAlpha - 1;
                            for (var c = 0; c < 3; c++)
                                row[i + c] = ratio * (layerAlpha * (compAlpha * comp[i + c] + layerCoefficient * layer[i + c] - row[i + c]) + row[i + c]);
                        }
                        row[i + 3] = newAlpha;
                        break;
                    }
                case Composite.ClipToBackdrop:
                    {
                        compAlpha *= layerAlpha;
                        var newAlpha = 1 - layerAlpha + compAlpha;
                        if (inAlpha != 0 && compAlpha != 0)
                        {
                            var ratio = compAlpha / newAlpha;
                            for (var c = 0; c < 3; c++)
                                row[i + c] = comp[i + c] * ratio + row[i + c] * (1 - ratio);
                        }
                        row[i + 3] = newAlpha * inAlpha;
                        break;
                    }
                case Composite.ClipToLayer:
                    {
                        compAlpha *= inAlpha;
                        var newAlpha = 1 - inAlpha + compAlpha;
                        if (layerAlpha == 0)
                        {
                        }
                        else if (inAlpha == 0)
                        {
                            row[i] = layer[i];
                            row[i + 1] = layer[i + 1];
                            row[i + 2] = layer[i + 2];
                        }
                        else
                        {
                            var ratio = compAlpha / newAlpha;
                            for (var c = 0; c < 3; c++)
                                row[i + c] = comp[i + c] * ratio + layer[i + c] * (1 - ratio);
                        }
                        row[i + 3] = newAlpha * layerAlpha;
                        break;
                    }
                default:
                    {
                        var newAlpha = inAlpha * layer[i + 3] * compAlpha * o;
                        if (newAlpha != 0)
                        {
                            row[i] = comp[i];
                            row[i + 1] = comp[i + 1];
                            row[i + 2] = comp[i + 2];
                        }
                        row[i + 3] = newAlpha;
                        break;
                    }
            }
        }
    }

    /// <summary>
    /// The blend functions of GIMP 2.10's modes: <paramref name="comp"/> gets the blended colour and the layer's alpha
    /// where both the backdrop and the layer have some (Color erase: the alpha left of the overlap).
    /// </summary>
    private static void Blend(int mode, float[] input, float[] layer, float[] comp, int start, int length)
    {
        for (var i = start; i < start + length; i += 4)
        {
            comp[i + 3] = layer[i + 3];
            if (input[i + 3] == 0 || layer[i + 3] == 0)
            {
                if (mode is ColorErase or ColorEraseLegacy)
                    comp[i + 3] = 0;
                continue;
            }
            switch (mode)
            {
                case HsvHue or HsvSaturation or HslColor or HsvValue or LchHue or LchChroma or LchColor or LchLightness
                    or LumaDarken or LumaLighten or Luminance:
                    BlendPixel(mode, input, layer, comp, i);
                    continue;
                case ColorErase or ColorEraseLegacy:
                    ColorErasePixel(input, layer, comp, i);
                    continue;
            }
            for (var c = 0; c < 3; c++)
            {
                float a = input[i + c], b = layer[i + c];
                comp[i + c] = mode switch
                {
                    Addition => a + b,
                    Burn => 1 - SafeDivide(1 - a, b),
                    Darken => Math.Min(a, b),
                    Difference => Math.Abs(a - b),
                    Divide => SafeDivide(a, b),
                    Dodge => SafeDivide(a, 1 - b),
                    Exclusion => 0.5f - 2 * (a - 0.5f) * (b - 0.5f),
                    GrainExtract => a - b + 0.5f,
                    GrainMerge => a + b - 0.5f,
                    HardMix => a + b < 1 ? 0 : 1,
                    HardLight => b > 0.5f ? Math.Min(1 - (1 - a) * (1 - (b - 0.5f) * 2), 1) : Math.Min(a * b * 2, 1),
                    Lighten => Math.Max(a, b),
                    LinearBurn => a + b - 1,
                    LinearLight => b <= 0.5f ? a + 2 * b - 1 : a + 2 * (b - 0.5f),
                    Multiply => a * b,
                    Overlay => a < 0.5f ? 2 * a * b : 1 - 2 * (1 - b) * (1 - a),
                    PinLight => b > 0.5f ? Math.Max(a, 2 * (b - 0.5f)) : Math.Min(a, 2 * b),
                    Screen => 1 - (1 - a) * (1 - b),
                    SoftLight => (1 - a) * a * b + a * (1 - (1 - a) * (1 - b)),
                    Subtract => a - b,
                    VividLight => b <= 0.5f ? Math.Max(1 - SafeDivide(1 - a, 2 * b), 0) : Math.Min(SafeDivide(a, 2 * (1 - b)), 1),
                    _ => b,
                };
            }
        }
    }

    /// <summary>a / b as GIMP divides in its blends: 0 when a is (almost) zero, else at most a million either way.</summary>
    private static float SafeDivide(float a, float b) =>
        Math.Abs(a) > Epsilon ? Math.Clamp(a / b, -1e6f, 1e6f) : 0;

    /// <summary>The modes that work on the whole colour (HSV, HSL, LCh, luminance) for one pixel.</summary>
    private static void BlendPixel(int mode, float[] input, float[] layer, float[] comp, int i)
    {
        float r1 = input[i], g1 = input[i + 1], b1 = input[i + 2];
        float r2 = layer[i], g2 = layer[i + 1], b2 = layer[i + 2];
        switch (mode)
        {
            case HsvHue:
                {
                    var srcMin = Math.Min(Math.Min(r2, g2), b2);
                    var srcMax = Math.Max(Math.Max(r2, g2), b2);
                    var srcDelta = srcMax - srcMin;
                    if (srcDelta > Epsilon)
                    {
                        var destMin = Math.Min(Math.Min(r1, g1), b1);
                        var destMax = Math.Max(Math.Max(r1, g1), b1);
                        var destDelta = destMax - destMin;
                        var destS = destMax != 0 ? destDelta / destMax : 0;
                        var ratio = destS * destMax / srcDelta;
                        var offset = destMax - srcMax * ratio;
                        Set(comp, i, r2 * ratio + offset, g2 * ratio + offset, b2 * ratio + offset);
                    }
                    else
                    {
                        Set(comp, i, r1, g1, b1);
                    }
                    break;
                }
            case HsvSaturation:
                {
                    var destMin = Math.Min(Math.Min(r1, g1), b1);
                    var destMax = Math.Max(Math.Max(r1, g1), b1);
                    var destDelta = destMax - destMin;
                    if (destDelta > Epsilon)
                    {
                        var srcMin = Math.Min(Math.Min(r2, g2), b2);
                        var srcMax = Math.Max(Math.Max(r2, g2), b2);
                        var srcS = srcMax != 0 ? (srcMax - srcMin) / srcMax : 0;
                        var ratio = srcS * destMax / destDelta;
                        var offset = (1 - ratio) * destMax;
                        Set(comp, i, r1 * ratio + offset, g1 * ratio + offset, b1 * ratio + offset);
                    }
                    else
                    {
                        Set(comp, i, destMax, destMax, destMax);
                    }
                    break;
                }
            case HslColor:
                {
                    var destL = (Math.Min(Math.Min(r1, g1), b1) + Math.Max(Math.Max(r1, g1), b1)) / 2;
                    var srcL = (Math.Min(Math.Min(r2, g2), b2) + Math.Max(Math.Max(r2, g2), b2)) / 2;
                    if (Math.Abs(srcL) > Epsilon && Math.Abs(1 - srcL) > Epsilon)
                    {
                        var destHigh = destL > 0.5f;
                        var srcHigh = srcL > 0.5f;
                        destL = Math.Min(destL, 1 - destL);
                        srcL = Math.Min(srcL, 1 - srcL);
                        var ratio = destL / srcL;
                        var offset = 0f;
                        if (destHigh)
                            offset += 1 - 2 * destL;
                        if (srcHigh)
                            offset += 2 * destL - ratio;
                        Set(comp, i, r2 * ratio + offset, g2 * ratio + offset, b2 * ratio + offset);
                    }
                    else
                    {
                        Set(comp, i, destL, destL, destL);
                    }
                    break;
                }
            case HsvValue:
                {
                    var destV = Math.Max(Math.Max(r1, g1), b1);
                    var srcV = Math.Max(Math.Max(r2, g2), b2);
                    if (Math.Abs(destV) > Epsilon)
                    {
                        var ratio = srcV / destV;
                        Set(comp, i, r1 * ratio, g1 * ratio, b1 * ratio);
                    }
                    else
                    {
                        Set(comp, i, srcV, srcV, srcV);
                    }
                    break;
                }
            // In the LCh modes the channels are L, a and b. Hue: the layer's hue at the backdrop's chroma.
            case LchHue:
                {
                    var c2 = MathF.Sqrt(g2 * g2 + b2 * b2);
                    if (c2 > Epsilon)
                    {
                        var c1 = MathF.Sqrt(g1 * g1 + b1 * b1);
                        Set(comp, i, r1, c1 * g2 / c2, c1 * b2 / c2);
                    }
                    else
                    {
                        Set(comp, i, r1, g1, b1);
                    }
                    break;
                }
            // Chroma: the layer's chroma at the backdrop's hue.
            case LchChroma:
                {
                    var c1 = MathF.Sqrt(g1 * g1 + b1 * b1);
                    if (c1 > Epsilon)
                    {
                        var c2 = MathF.Sqrt(g2 * g2 + b2 * b2);
                        Set(comp, i, r1, c2 * g1 / c1, c2 * b1 / c1);
                    }
                    else
                    {
                        Set(comp, i, r1, g1, b1);
                    }
                    break;
                }
            case LchColor:
                Set(comp, i, r1, g2, b2);
                break;
            case LchLightness:
                Set(comp, i, r2, g1, b1);
                break;
            case LumaDarken or LumaLighten:
                {
                    var dest = LumaR * r1 + LumaG * g1 + LumaB * b1;
                    var src = LumaR * r2 + LumaG * g2 + LumaB * b2;
                    var keep = mode == LumaDarken ? dest <= src : dest >= src;
                    if (keep)
                        Set(comp, i, r1, g1, b1);
                    else
                        Set(comp, i, r2, g2, b2);
                    break;
                }
            default: // Luminance
                {
                    var ratio = SafeDivide(LumaR * r2 + LumaG * g2 + LumaB * b2, LumaR * r1 + LumaG * g1 + LumaB * b1);
                    Set(comp, i, r1 * ratio, g1 * ratio, b1 * ratio);
                    break;
                }
        }
    }

    private static void Set(float[] pixels, int i, float r, float g, float b)
    {
        pixels[i] = r;
        pixels[i + 1] = g;
        pixels[i + 2] = b;
    }

    /// <summary>Color erase: the layer's colour taken out of the backdrop, as alpha.</summary>
    private static void ColorErasePixel(float[] input, float[] layer, float[] comp, int i)
    {
        var alpha = 0f;
        for (var c = 0; c < 3; c++)
        {
            var col = Math.Clamp(input[i + c], 0, 1);
            var background = Math.Clamp(layer[i + c], 0, 1);
            if (Math.Abs(col - background) > Epsilon)
                alpha = Math.Max(alpha, col > background ? (col - background) / (1 - background) : (background - col) / background);
        }
        if (alpha > Epsilon)
        {
            for (var c = 0; c < 3; c++)
                comp[i + c] = (input[i + c] - layer[i + c]) / alpha + layer[i + c];
        }
        else
        {
            Set(comp, i, 0, 0, 0);
        }
        comp[i + 3] = alpha;
    }

    /// <summary>
    /// The legacy modes: the overlap is the smaller of the two alphas times the opacity, the backdrop keeps its alpha.
    /// Overlay (legacy) was Soft light all along.
    /// </summary>
    private static void DrawLegacy(int mode, float[] row, float[] layer, float opacity, ReadOnlySpan<float> mask, int from, int to)
    {
        Span<float> mixed = stackalloc float[3];
        for (var x = from; x < to; x++)
        {
            var i = x * 4;
            var inAlpha = row[i + 3];
            var compAlpha = Math.Min(inAlpha, layer[i + 3]) * opacity * MaskAt(mask, x - from);
            var newAlpha = inAlpha + (1 - inAlpha) * compAlpha;
            if (compAlpha == 0 || newAlpha == 0)
                continue;
            var ratio = compAlpha / newAlpha;
            if (mode is HueLegacy or SaturationLegacy or ColorLegacy or ValueLegacy)
            {
                LegacyColour(mode, row, layer, i, mixed);
            }
            else
            {
                for (var c = 0; c < 3; c++)
                {
                    float a = row[i + c], b = layer[i + c];
                    mixed[c] = mode switch
                    {
                        MultiplyLegacy => a * b,
                        ScreenLegacy => 1 - (1 - a) * (1 - b),
                        DifferenceLegacy => Math.Abs(a - b),
                        AdditionLegacy => Math.Clamp(a + b, 0, 1),
                        SubtractLegacy => Math.Clamp(a - b, 0, 1),
                        DarkenLegacy => Math.Min(a, b),
                        LightenLegacy => Math.Max(a, b),
                        DivideLegacy => ClampNaN(a / b, 1),
                        DodgeLegacy => ClampNaN(a / (1 - b), 1),
                        BurnLegacy => ClampNaN(1 - (1 - a) / b, 1),
                        HardLightLegacy => b > 128f / 255
                            ? Math.Min(1 - (1 - a) * (1 - (b - 128f / 255) * 2), 1)
                            : Math.Min(a * b * 2, 1),
                        GrainExtractLegacy => Math.Clamp(a - b + 128f / 255, 0, 1),
                        GrainMergeLegacy => Math.Clamp(a + b - 128f / 255, 0, 1),
                        _ => (1 - a) * a * b + a * (1 - (1 - a) * (1 - b)), // Soft light, and Overlay (legacy)
                    };
                }
            }
            for (var c = 0; c < 3; c++)
                row[i + c] = mixed[c] * ratio + row[i + c] * (1 - ratio);
        }
    }

    /// <summary>Clamps to 0…1; not-a-number (0 / 0) becomes <paramref name="nan"/>, as GIMP's legacy modes give.</summary>
    private static float ClampNaN(float value, float nan) => float.IsNaN(value) ? nan : value < 0 ? 0 : value < 1 ? value : 1;

    /// <summary>The legacy HSV and HSL modes: one component of the layer's colour taken into the backdrop's.</summary>
    private static void LegacyColour(int mode, float[] row, float[] layer, int i, Span<float> mixed)
    {
        if (mode == ColorLegacy)
        {
            var (h, s, _) = ToHsl(layer[i], layer[i + 1], layer[i + 2]);
            var (_, _, l) = ToHsl(row[i], row[i + 1], row[i + 2]);
            FromHsl(h, s, l, mixed);
            return;
        }
        var (lh, ls, lv) = ToHsv(layer[i], layer[i + 1], layer[i + 2]);
        var (oh, os, ov) = ToHsv(row[i], row[i + 1], row[i + 2]);
        switch (mode)
        {
            case HueLegacy:
                if (ls != 0)
                    oh = lh;
                break;
            case SaturationLegacy:
                os = ls;
                break;
            default:
                ov = lv;
                break;
        }
        FromHsv(oh, os, ov, mixed);
    }

    /// <summary>HSV with hue 0…1 (GimpHSV).</summary>
    private static (double H, double S, double V) ToHsv(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        if (delta <= 0.0001)
            return (0, 0, max);
        var s = delta / max;
        double h;
        if (r == max)
            h = (g - b) / delta;
        else if (g == max)
            h = 2 + (b - r) / delta;
        else
            h = 4 + (r - g) / delta;
        h /= 6;
        if (h < 0)
            h += 1;
        else if (h > 1)
            h -= 1;
        return (h, s, max);
    }

    private static void FromHsv(double h, double s, double v, Span<float> rgb)
    {
        if (s == 0)
        {
            rgb[0] = rgb[1] = rgb[2] = (float)v;
            return;
        }
        var hue = h == 1 ? 0 : h * 6;
        var sector = (int)Math.Floor(hue);
        var f = hue - sector;
        var w = v * (1 - s);
        var q = v * (1 - s * f);
        var t = v * (1 - s * (1 - f));
        var (r, g, b) = sector switch
        {
            0 => (v, t, w),
            1 => (q, v, w),
            2 => (w, v, t),
            3 => (w, q, v),
            4 => (t, w, v),
            _ => (v, w, q),
        };
        rgb[0] = (float)r;
        rgb[1] = (float)g;
        rgb[2] = (float)b;
    }

    /// <summary>HSL with hue 0…1 (GimpHSL).</summary>
    private static (double H, double S, double L) ToHsl(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max == min)
            return (-1, 0, l);
        var delta = max - min;
        var s = l <= 0.5 ? delta / (max + min) : delta / (2 - max - min);
        double h;
        if (max == r)
            h = (g - b) / delta;
        else if (max == g)
            h = 2 + (b - r) / delta;
        else
            h = 4 + (r - g) / delta;
        h /= 6;
        if (h < 0)
            h += 1;
        return (h, s, l);
    }

    private static void FromHsl(double h, double s, double l, Span<float> rgb)
    {
        if (s == 0)
        {
            rgb[0] = rgb[1] = rgb[2] = (float)l;
            return;
        }
        var m2 = l <= 0.5 ? l * (1 + s) : l + s - l * s;
        var m1 = 2 * l - m2;
        rgb[0] = (float)HslValue(m1, m2, h * 6 + 2);
        rgb[1] = (float)HslValue(m1, m2, h * 6);
        rgb[2] = (float)HslValue(m1, m2, h * 6 - 2);
    }

    private static double HslValue(double n1, double n2, double hue)
    {
        if (hue > 6)
            hue -= 6;
        else if (hue < 0)
            hue += 6;
        return hue < 1 ? n1 + (n2 - n1) * hue
            : hue < 3 ? n2
            : hue < 4 ? n1 + (n2 - n1) * (4 - hue)
            : n1;
    }

    /// <summary>Behind: the layer goes under the backdrop.</summary>
    private static void DrawBehind(Composite how, float[] row, float[] layer, float opacity, ReadOnlySpan<float> mask, int from, int to)
    {
        for (var x = from; x < to; x++)
        {
            var i = x * 4;
            var inAlpha = row[i + 3];
            if (how == Composite.ClipToBackdrop)
            {
                if (inAlpha == 0)
                    Set(row, i, layer[i], layer[i + 1], layer[i + 2]);
                continue;
            }
            var layerAlpha = layer[i + 3] * opacity * MaskAt(mask, x - from);
            var newAlpha = layerAlpha + (1 - layerAlpha) * inAlpha;
            if (newAlpha != 0)
            {
                var ratio = inAlpha / newAlpha;
                for (var c = 0; c < 3; c++)
                    row[i + c] = row[i + c] * ratio + layer[i + c] * (1 - ratio);
            }
            else
            {
                Set(row, i, layer[i], layer[i + 1], layer[i + 2]);
            }
            row[i + 3] = newAlpha;
        }
    }

    /// <summary>Erase: the layer's alpha taken out of the backdrop.</summary>
    private static void DrawErase(Composite how, float[] row, float[] layer, float opacity, ReadOnlySpan<float> mask, int from, int to)
    {
        for (var x = from; x < to; x++)
        {
            var i = x * 4;
            var inAlpha = row[i + 3];
            var layerAlpha = layer[i + 3] * opacity * MaskAt(mask, x - from);
            if (how != Composite.Union)
            {
                row[i + 3] = (1 - layerAlpha) * inAlpha;
                continue;
            }
            var newAlpha = inAlpha + layerAlpha - 2 * inAlpha * layerAlpha;
            if (newAlpha != 0)
            {
                var ratio = (1 - inAlpha) * layerAlpha / newAlpha;
                for (var c = 0; c < 3; c++)
                    row[i + c] = ratio * layer[i + c] + (1 - ratio) * row[i + c];
            }
            row[i + 3] = newAlpha;
        }
    }

    /// <summary>Merge: the layer replaces as much of the backdrop as its alpha takes.</summary>
    private static void DrawMerge(Composite how, float[] row, float[] layer, float opacity, ReadOnlySpan<float> mask, int from, int to)
    {
        for (var x = from; x < to; x++)
        {
            var i = x * 4;
            var inAlpha = row[i + 3];
            var layerAlpha = layer[i + 3] * opacity * MaskAt(mask, x - from);
            if (how == Composite.ClipToBackdrop)
            {
                layerAlpha -= 1 - inAlpha;
                if (layerAlpha > 0)
                {
                    var ratio = layerAlpha / inAlpha;
                    for (var c = 0; c < 3; c++)
                        row[i + c] += (layer[i + c] - row[i + c]) * ratio;
                }
                continue;
            }
            inAlpha = Math.Min(inAlpha, 1 - layerAlpha);
            var newAlpha = inAlpha + layerAlpha;
            if (newAlpha != 0)
            {
                var ratio = layerAlpha / newAlpha;
                for (var c = 0; c < 3; c++)
                    row[i + c] += (layer[i + c] - row[i + c]) * ratio;
            }
            row[i + 3] = newAlpha;
        }
    }

    /// <summary>Split: the layer's alpha taken from the backdrop's, or the other way round.</summary>
    private static void DrawSplit(Composite how, float[] row, float[] layer, float opacity, ReadOnlySpan<float> mask, int from, int to)
    {
        for (var x = from; x < to; x++)
        {
            var i = x * 4;
            var inAlpha = row[i + 3];
            var layerAlpha = layer[i + 3] * opacity * MaskAt(mask, x - from);
            if (how != Composite.Union)
            {
                row[i + 3] = Math.Max(inAlpha - layerAlpha, 0);
            }
            else if (layerAlpha <= inAlpha)
            {
                row[i + 3] = inAlpha - layerAlpha;
            }
            else
            {
                Set(row, i, layer[i], layer[i + 1], layer[i + 2]);
                row[i + 3] = layerAlpha - inAlpha;
            }
        }
    }

    // ───────────────────────── Colour spaces ─────────────────────────

    /// <summary>
    /// Perceptual (sRGB-encoded) value of a linear one, as babl converts it: negative values (blends can make them)
    /// stay on the curve's straight part.
    /// </summary>
    public static float ToPerceptual(float linear) =>
        linear > 0.0031308f ? 1.055f * MathF.Pow(linear, 1 / 2.4f) - 0.055f : 12.92f * linear;

    /// <summary>Linear value of a perceptual one.</summary>
    public static float ToLinear(float perceptual) =>
        perceptual > 0.04045f ? MathF.Pow((perceptual + 0.055f) / 1.055f, 2.4f) : perceptual / 12.92f;

    /// <summary>Converts pixels (four floats each, alpha untouched) between spaces.</summary>
    public static void Convert(Span<float> pixels, Space from, Space to)
    {
        if (from == to)
            return;
        if (from == Space.Lab)
        {
            for (var i = 0; i < pixels.Length; i += 4)
                LabToLinear(pixels.Slice(i, 3));
            from = Space.Linear;
            if (to == Space.Linear)
                return;
        }
        if (from == Space.Perceptual)
        {
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = ToLinear(pixels[i]);
                pixels[i + 1] = ToLinear(pixels[i + 1]);
                pixels[i + 2] = ToLinear(pixels[i + 2]);
            }
            if (to == Space.Linear)
                return;
        }
        if (to == Space.Perceptual)
        {
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = ToPerceptual(pixels[i]);
                pixels[i + 1] = ToPerceptual(pixels[i + 1]);
                pixels[i + 2] = ToPerceptual(pixels[i + 2]);
            }
            return;
        }
        for (var i = 0; i < pixels.Length; i += 4)
            LinearToLab(pixels.Slice(i, 3));
    }

    // sRGB primaries adapted to D50 (Bradford), and D50 white: what babl's "CIE Lab" uses.
    private const float WhiteX = 0.96420288f, WhiteZ = 0.82490540f;
    private const float LabEpsilon = 216f / 24389, LabKappa = 24389f / 27;

    private static void LinearToLab(Span<float> c)
    {
        float r = c[0], g = c[1], b = c[2];
        var x = (0.43603516f * r + 0.38511658f * g + 0.14305115f * b) / WhiteX;
        var y = 0.22248840f * r + 0.71690369f * g + 0.06060791f * b;
        var z = (0.01391602f * r + 0.09706116f * g + 0.71392822f * b) / WhiteZ;
        float F(float t) => t > LabEpsilon ? MathF.Cbrt(t) : (LabKappa * t + 16) / 116;
        float fx = F(x), fy = F(y), fz = F(z);
        c[0] = 116 * fy - 16;
        c[1] = 500 * (fx - fy);
        c[2] = 200 * (fy - fz);
    }

    private static void LabToLinear(Span<float> c)
    {
        var fy = (c[0] + 16) / 116;
        var fx = fy + c[1] / 500;
        var fz = fy - c[2] / 200;
        float Inverse(float f) => f * f * f > LabEpsilon ? f * f * f : (116 * f - 16) / LabKappa;
        var x = Inverse(fx) * WhiteX;
        var y = c[0] > LabKappa * LabEpsilon ? fy * fy * fy : c[0] / LabKappa;
        var z = Inverse(fz) * WhiteZ;
        c[0] = 3.13405134f * x - 1.61702771f * y - 0.49065221f * z;
        c[1] = -0.97876273f * x + 1.91614223f * y + 0.03344963f * z;
        c[2] = 0.07194258f * x - 0.22897118f * y + 1.40521831f * z;
    }
}
