// An equation drawn as a picture, for the formats that keep one beside the equation or instead of it: the replacement
// picture of an OpenDocument formula object, the fallback of PowerPoint's Office Math, and the equations of Kindle
// books (KF8 readers don't draw MathML; Amazon's guidelines ask for PNG line art without transparency, sized in em).
//
// The equation is laid out by MathLayout as the renderer lays it out: a Word or TeX equation as a display or inline
// formula (math font, math italic letters), a 한글 equation (one with the box 한글 laid it out in) at one size.
// Pictures aren't kept: each is drawn when its part is written.

using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Rendering;
using SkiaSharp;

namespace Filee.Engines.Typesetting;

/// <summary>A drawn equation: a PNG and its size in points, the baseline <see cref="Ascent"/> below its top.</summary>
internal sealed record EquationImage(byte[] Png, float Width, float Ascent, float Descent)
{
    public float Height => Ascent + Descent;
}

/// <summary>
/// Draws equations as PNG pictures. Nothing is kept: a writer measures an equation first (<see cref="Frame"/>) and
/// draws it straight into its package when it writes the part (<see cref="Write"/>), so a document with many equations
/// doesn't hold all their pictures at once.
/// </summary>
internal sealed class EquationPicture : IDisposable
{
    /// <summary>A picture is at most this many pixels across or down; a larger one is drawn at a lower resolution.</summary>
    private const int MaxPixels = 4000;

    private readonly Shaper _shaper = new();
    private readonly MathLayout _math;

    public EquationPicture(FontResolver? fonts = null) => _math = new MathLayout(fonts ?? new FontResolver(), _shaper);

    public void Dispose() => _shaper.Dispose();

    /// <summary>The mode MathLayout sets <paramref name="equation"/> in: 한글's at one size, others as Word and TeX set them.</summary>
    internal static MathMode Mode(HEquation equation, bool display) =>
        equation.Width is not null || equation.Height is not null ? MathMode.Fixed : display ? MathMode.Display : MathMode.Text;

    /// <summary>The equation's box at its size (its own, else its text's), in points; no picture is drawn.</summary>
    public EquationBox Measure(HEquation equation, bool display) =>
        _math.Layout(equation.Math, Size(equation), SKColors.Black, Mode(equation, display));

    /// <summary>
    /// The size of <paramref name="equation"/>'s picture in points, the baseline <c>Ascent</c> below its top (the
    /// equation with a little room around its ink); null for an empty equation. Nothing is drawn.
    /// </summary>
    public (float Width, float Ascent, float Descent)? Frame(HEquation equation, bool display) =>
        Laid(equation, display) is var (box, pad) ? (box.Width + 2 * pad, box.Ascent + pad, box.Descent + pad) : null;

    /// <summary>
    /// <paramref name="equation"/> as a PNG at <paramref name="dpi"/>: on white when <paramref name="opaque"/>, else on
    /// a transparent ground; null for an empty equation.
    /// </summary>
    public EquationImage? Draw(HEquation equation, bool display, bool opaque = false, float dpi = 300)
    {
        if (Frame(equation, display) is not var (width, ascent, descent))
            return null;
        using var png = new MemoryStream();
        return Write(equation, display, png, opaque, dpi) ? new EquationImage(png.ToArray(), width, ascent, descent) : null;
    }

    /// <summary>
    /// Draws <paramref name="equation"/> as <see cref="Draw"/> does, the PNG written to <paramref name="target"/>; false
    /// (nothing written) for an empty equation.
    /// </summary>
    public bool Write(HEquation equation, bool display, Stream target, bool opaque = false, float dpi = 300)
    {
        if (Laid(equation, display) is not var (box, pad))
            return false;
        // A little room around the ink: italic letters and brackets reach past the box.
        var width = box.Width + 2 * pad;
        var height = box.Height + 2 * pad;
        var page = new PageContent(width, height);
        box.Paint(page, pad, pad + box.Ascent, 1);
        var scale = Math.Min(dpi / 72f, MaxPixels / Math.Max(width, height));
        using var bitmap = new SKBitmap(Math.Max(1, (int)Math.Ceiling(width * scale)), Math.Max(1, (int)Math.Ceiling(height * scale)),
            SKColorType.Rgba8888, opaque ? SKAlphaType.Opaque : SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(opaque ? SKColors.White : SKColors.Transparent);
            canvas.Scale(scale);
            page.Draw(canvas);
        }
        return bitmap.Encode(target, SKEncodedImageFormat.Png, 100);
    }

    /// <summary>The equation laid out in its colour, and the room around it; null for an empty one.</summary>
    private (EquationBox Box, float Pad)? Laid(HEquation equation, bool display)
    {
        var color = ParagraphText.Color(equation.Color ?? equation.Format.Color) ?? SKColors.Black;
        var box = _math.Layout(equation.Math, Size(equation), color, Mode(equation, display));
        if (!(box.Width > 0 && box.Height > 0 && float.IsFinite(box.Width) && float.IsFinite(box.Height)))
            return null;
        return (box, Size(equation) * 0.08f);
    }

    private static float Size(HEquation equation) => Math.Clamp((equation.Size ?? equation.Format.Size ?? 1000) / 100f, 1, 1000);
}
