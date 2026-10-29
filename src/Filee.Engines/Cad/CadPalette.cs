// The colours a drawing is printed in (CadOptions.Colors, #411), applied while drawing so PDF, SVG and pictures
// all get them: the scene keeps the drawing's own colours.

using Filee.Core.Presets;
using SkiaSharp;

namespace Filee.Engines.Cad;

/// <summary>Maps the drawing's colours to the colours printed, like a plot style table in AutoCAD.</summary>
/// <remarks>
/// <para>
/// Ink is everything drawn with a pen: lines, curves, text, dimensions, hatch pattern lines, points and the small
/// fills that are part of a line (arrowheads, SOLID / TRACE, wide polyline segments). Black and white prints all of it
/// black, as monochrome.ctb does. Grayscale prints it as the gray of its brightness, scaled so the lightest colour is
/// still a mid gray: yellow (the brightest pen colour) would otherwise be almost white, as unreadable as it is now.
/// </para>
/// <para>
/// Areas are filled hatches (solid, gradient, and the tint of a pattern too dense to draw). monochrome.ctb fills them
/// black, which turns a light background colour into a black block hiding the lines and text drawn over it. Both
/// choices print an area as the gray of its brightness instead: a yellow or light blue fill stays light, a dark fill
/// stays dark, a black one stays black. The page itself stays white.
/// </para>
/// </remarks>
internal readonly record struct CadPalette(CadColors Colors)
{
    /// <summary>The lightest gray ink is printed in with <see cref="CadColors.Grayscale"/>.</summary>
    private const double LightestInk = 160;

    public SKColor Ink(SKColor color) => Colors switch
    {
        CadColors.BlackAndWhite => new SKColor(0, 0, 0, color.Alpha),
        CadColors.Grayscale => Gray(Brightness(color) * LightestInk / 255, color.Alpha),
        _ => color,
    };

    public SKColor Area(SKColor color) => Colors is CadColors.BlackAndWhite or CadColors.Grayscale
        ? Gray(Brightness(color), color.Alpha)
        : color;

    /// <summary>Luma with the Rec. 601 weights, the usual grayscale conversion of sRGB values (0–255).</summary>
    private static double Brightness(SKColor color) => 0.299 * color.Red + 0.587 * color.Green + 0.114 * color.Blue;

    private static SKColor Gray(double level, byte alpha)
    {
        var value = (byte)Math.Clamp(Math.Round(level), 0, 255);
        return new SKColor(value, value, value, alpha);
    }
}
