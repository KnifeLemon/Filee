// An equation drawn as a picture, for the formats that keep one beside the equation or instead of it: the replacement
// picture of an OpenDocument formula object, the fallback of PowerPoint's Office Math, and the equations of Kindle
// books (KF8 readers don't draw MathML; Amazon's guidelines ask for PNG line art without transparency, sized in em).
//
// The equation is laid out by MathLayout as the renderer lays it out: a Word or TeX equation as a display or inline
// formula (math font, math italic letters), a 한글 equation (one with the box 한글 laid it out in) at one size.

using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Rendering;
using SkiaSharp;

namespace Filee.Engines.Typesetting;

/// <summary>A drawn equation: a PNG and its size in points, the baseline <see cref="Ascent"/> below its top.</summary>
internal sealed record EquationImage(byte[] Png, float Width, float Ascent, float Descent)
{
    public float Height => Ascent + Descent;
}

/// <summary>Draws equations as PNG pictures; one per equation and look, kept while it lives.</summary>
internal sealed class EquationPicture : IDisposable
{
    /// <summary>A picture is at most this many pixels across or down; a larger one is drawn at a lower resolution.</summary>
    private const int MaxPixels = 4000;

    private readonly Shaper _shaper = new();
    private readonly MathLayout _math;
    private readonly Dictionary<(HEquation, bool, bool), EquationImage?> _drawn = new();

    public EquationPicture(FontResolver? fonts = null) => _math = new MathLayout(fonts ?? new FontResolver(), _shaper);

    public void Dispose() => _shaper.Dispose();

    /// <summary>The mode MathLayout sets <paramref name="equation"/> in: 한글's at one size, others as Word and TeX set them.</summary>
    internal static MathMode Mode(HEquation equation, bool display) =>
        equation.Width is not null || equation.Height is not null ? MathMode.Fixed : display ? MathMode.Display : MathMode.Text;

    /// <summary>The equation's box at its size (its own, else its text's), in points; no picture is drawn.</summary>
    public EquationBox Measure(HEquation equation, bool display) =>
        _math.Layout(equation.Math, Size(equation), SKColors.Black, Mode(equation, display));

    /// <summary>
    /// <paramref name="equation"/> as a PNG at <paramref name="dpi"/>: on white when <paramref name="opaque"/>, else on
    /// a transparent ground; null for an empty equation.
    /// </summary>
    public EquationImage? Draw(HEquation equation, bool display, bool opaque = false, float dpi = 300)
    {
        if (_drawn.TryGetValue((equation, display, opaque), out var known))
            return known;
        if (_drawn.Count > 10_000)
            _drawn.Clear();
        var color = ParagraphText.Color(equation.Color ?? equation.Format.Color) ?? SKColors.Black;
        var box = _math.Layout(equation.Math, Size(equation), color, Mode(equation, display));
        EquationImage? image = null;
        if (box.Width > 0 && box.Height > 0 && float.IsFinite(box.Width) && float.IsFinite(box.Height))
        {
            // A little room around the ink: italic letters and brackets reach past the box.
            var pad = Size(equation) * 0.08f;
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
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            image = new EquationImage(data.ToArray(), width, box.Ascent + pad, box.Descent + pad);
        }
        return _drawn[(equation, display, opaque)] = image;
    }

    private static float Size(HEquation equation) => Math.Clamp((equation.Size ?? equation.Format.Size ?? 1000) / 100f, 1, 1000);
}
