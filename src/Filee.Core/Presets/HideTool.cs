// "Pixelate / blur" runs as an ordinary conversion once its window has the parts of a picture to hide: the tool preset
// becomes a conversion into the picture's own format with those parts (ImageOptions.HiddenAreas), so the job queue, the
// toast, cancelling and the output rules work as for any other file. How they are covered (pixelate or blur, and how
// strongly) is kept in the tool, so the window opens with the last choice. The arithmetic shared by the window's
// preview and the image engine is here too: where a part lies in pixels, how large the squares or the blur are, and
// which square a pixel falls in.

using Filee.Core.Conversion;
using Filee.Core.Formats;

namespace Filee.Core.Presets;

/// <summary>Turns the "Pixelate / blur" tool into the conversion that writes a picture with parts of it covered.</summary>
public static class HideTool
{
    /// <summary>Where a picture goes when its own format can't be written (a camera RAW, a DICOM …).</summary>
    public const string FallbackTarget = "png";

    /// <summary>The range of <see cref="ImageOptions.HideStrength"/>.</summary>
    public const int MinStrength = 1, MaxStrength = 10, DefaultStrength = 5;

    /// <summary>The smallest square (or blur) in pixels, whatever the picture's size.</summary>
    public const double MinEffectPixels = 8;

    /// <summary>
    /// Parts smaller than this (in parts of the picture's side) are a click, not a drag: none is added for them.
    /// </summary>
    public const double MinAreaSide = 0.004;

    /// <summary>True when a <paramref name="sourceFormat"/> file is a picture the tool can open.</summary>
    public static bool CanHide(string sourceFormat) =>
        FormatRegistry.FindById(sourceFormat)?.Category == FormatCategory.Image;

    /// <summary>
    /// The format a <paramref name="sourceFormat"/> picture is written in: its own when it can be written from itself,
    /// else <see cref="FallbackTarget"/>.
    /// </summary>
    public static string TargetFor(string sourceFormat, RoutePlanner planner) =>
        FormatRegistry.FindById(sourceFormat) is { Writable: true } && planner.Plan(sourceFormat, sourceFormat) is not null
            ? BuiltInData.SameAsSource
            : FallbackTarget;

    /// <summary>
    /// The conversion that writes a picture as <paramref name="targetFormat"/> with <paramref name="areas"/> covered as
    /// the tool says, with the tool's name and output rule. The tool itself is left as it is.
    /// </summary>
    public static Preset ConversionOf(Preset tool, string targetFormat, IEnumerable<HiddenArea> areas)
    {
        var preset = tool.Clone();
        preset.Kind = PresetKind.Convert;
        preset.Tool = null;
        preset.SubPresetIds = [];
        preset.TargetFormat = targetFormat;
        preset.Image.HiddenAreas = [.. areas.Select(Clamp).OfType<HiddenArea>()];
        preset.Image.HideStrength = ClampStrength(preset.Image.HideStrength);
        // "Only if smaller" would keep the original, every part of it in view, when the covered copy came out larger.
        preset.Output.OnlyIfSmaller = false;
        return preset;
    }

    /// <summary>Keeps the window's choice in <paramref name="tool"/> for the next time.</summary>
    public static void Remember(Preset tool, HideStyle style, int strength)
    {
        tool.Image.HideStyle = style;
        tool.Image.HideStrength = ClampStrength(strength);
    }

    /// <summary><paramref name="strength"/> within <see cref="MinStrength"/> … <see cref="MaxStrength"/>.</summary>
    public static int ClampStrength(int strength) => Math.Clamp(strength, MinStrength, MaxStrength);

    /// <summary>
    /// The side of a square (pixelate) or the blur's sigma, in pixels of a <paramref name="width"/> × <paramref name="height"/>
    /// picture: 2 % of its shorter side at the lowest strength to 6.5 % at the highest, never under
    /// <see cref="MinEffectPixels"/>.
    /// </summary>
    /// <remarks>
    /// Tied to the picture rather than to the part being hidden: a screenshot's line of text is about 1.2 % of a desktop
    /// screen's shorter side and 4 % of a phone's, so at the default strength (4 %) a square is as tall as a line of
    /// text or taller. Squares much smaller than the letters leave them guessable: text in a known font can be drawn,
    /// pixelated the same way and compared square by square until it matches.
    /// </remarks>
    public static double EffectSize(int strength, int width, int height) =>
        Math.Max(MinEffectPixels, Math.Min(Math.Max(1, width), Math.Max(1, height)) * (0.015 + 0.005 * ClampStrength(strength)));

    /// <summary>
    /// <paramref name="area"/> within the picture, or null when nothing of it is left (or it is not a number).
    /// </summary>
    public static HiddenArea? Clamp(HiddenArea area)
    {
        if (!double.IsFinite(area.X) || !double.IsFinite(area.Y) || !double.IsFinite(area.Width) || !double.IsFinite(area.Height))
            return null;
        var left = Math.Clamp(Math.Min(area.X, area.X + area.Width), 0, 1);
        var right = Math.Clamp(Math.Max(area.X, area.X + area.Width), 0, 1);
        var top = Math.Clamp(Math.Min(area.Y, area.Y + area.Height), 0, 1);
        var bottom = Math.Clamp(Math.Max(area.Y, area.Y + area.Height), 0, 1);
        return right > left && bottom > top ? new HiddenArea { X = left, Y = top, Width = right - left, Height = bottom - top } : null;
    }

    /// <summary>
    /// <paramref name="area"/> in whole pixels of a <paramref name="width"/> × <paramref name="height"/> picture, rounded
    /// outwards (a pixel the part touches is covered), at least one pixel; null when nothing of it is on the picture.
    /// </summary>
    public static (int X, int Y, int Width, int Height)? PixelBounds(HiddenArea area, int width, int height)
    {
        if (width <= 0 || height <= 0 || Clamp(area) is not { } inside)
            return null;
        var left = Math.Clamp((int)Math.Floor(inside.X * width + 1e-9), 0, width - 1);
        var top = Math.Clamp((int)Math.Floor(inside.Y * height + 1e-9), 0, height - 1);
        var right = Math.Clamp((int)Math.Ceiling((inside.X + inside.Width) * width - 1e-9), left + 1, width);
        var bottom = Math.Clamp((int)Math.Ceiling((inside.Y + inside.Height) * height - 1e-9), top + 1, height);
        return (left, top, right - left, bottom - top);
    }

    /// <summary>
    /// The square a pixel column (or row) falls in, on a grid of <paramref name="cell"/>-pixel squares from the picture's
    /// top-left corner: by the pixel's centre, so a cell of 2.5 pixels takes pixels 0–1, 2–4, 5–6 …
    /// </summary>
    public static int CellOf(int pixel, double cell) => (int)Math.Floor((pixel + 0.5) / cell);

    /// <summary>The first pixel column (or row) of square <paramref name="index"/> (see <see cref="CellOf"/>).</summary>
    public static int CellStart(int index, double cell) => (int)Math.Ceiling(index * cell - 0.5);
}
