// Text of a chart (titles, axis and data labels, legend entries) on the layout engine's services: the font a style
// names (or the theme's body font, else Calibri and what stands in for it), the fonts its characters fall back to,
// shaped with HarfBuzz and drawn as glyph runs, so a PDF keeps the chart's text as text.

using Filee.Engines.Rendering;
using Filee.Engines.Typesetting;
using SkiaSharp;

namespace Filee.Engines.Charts;

/// <summary>Fonts and shaping for chart text; dispose to release a shaper it made itself.</summary>
internal sealed class ChartFonts : IDisposable
{
    private readonly FontResolver _resolver;
    private readonly Shaper _shaper;
    private readonly bool _ownsShaper;
    private readonly Dictionary<(string? Family, bool Bold, bool Italic), ResolvedFont?> _fonts = [];
    private readonly Dictionary<PageFont, FontMetrics> _metrics = new(ReferenceEqualityComparer.Instance);

    /// <param name="resolver">The renderer's font resolver, or null for the installed fonts.</param>
    /// <param name="shaper">The renderer's shaper (not disposed here), or null for one of its own.</param>
    public ChartFonts(FontResolver? resolver = null, Shaper? shaper = null)
    {
        _resolver = resolver ?? new FontResolver();
        _ownsShaper = shaper is null;
        _shaper = shaper ?? new Shaper();
    }

    public void Dispose()
    {
        if (_ownsShaper)
            _shaper.Dispose();
    }

    /// <summary>One line of <paramref name="text"/> in <paramref name="style"/> at <paramref name="size"/> points.</summary>
    public ChartTextLine Line(string text, ChartTextStyle style, float size)
    {
        var line = new ChartTextLine(text, style.Color, size);
        if (Font(style) is not { } primary || size <= 0 || !float.IsFinite(size))
            return line;
        var metrics = Metrics(primary.Font);
        var em = (float)Math.Max(1, metrics.UnitsPerEm);
        line.Ascent = metrics.Ascender / em * size;
        line.Descent = -metrics.Descender / em * size;
        if (line.Ascent <= 0)
            (line.Ascent, line.Descent) = (size * 0.8f, size * 0.2f);
        if (text.Length == 0)
            return line;
        foreach (var run in _resolver.Fallback(text, 0, text.Length, primary, FontStyle.From(style.Bold, style.Italic)))
        {
            var shaped = _shaper.Shape(run.Font.Font, text, run.Start, run.Length);
            line.Runs.Add((run.Font, shaped, run.Start, run.Length));
            line.Width += shaped.Width(size);
        }
        return line;
    }

    private ResolvedFont? Font(ChartTextStyle style)
    {
        var key = (style.Family, style.Bold, style.Italic);
        if (!_fonts.TryGetValue(key, out var font))
        {
            var fontStyle = FontStyle.From(style.Bold, style.Italic);
            _fonts[key] = font = _resolver.Resolve(style.Family ?? "Calibri", fontStyle) ?? _resolver.Resolve(null, fontStyle);
        }
        return font;
    }

    private FontMetrics Metrics(PageFont font)
    {
        if (!_metrics.TryGetValue(font, out var metrics))
            _metrics[font] = metrics = _shaper.Metrics(font);
        return metrics;
    }
}

/// <summary>A line of chart text, shaped: its width, ascent and descent in points, drawn at a pen position.</summary>
internal sealed class ChartTextLine(string text, SKColor color, float size)
{
    public string Text { get; } = text;
    public float Width { get; set; }
    public float Ascent { get; set; }
    public float Descent { get; set; }
    public float Height => Ascent + Descent;

    internal List<(ResolvedFont Font, ShapedRun Run, int Start, int Length)> Runs { get; } = [];

    /// <summary>Draws the line with its pen at (<paramref name="x"/>, <paramref name="baseline"/>) in <paramref name="transform"/>'s frame.</summary>
    public void Draw(PageContent page, float x, float baseline, SKMatrix? transform, SKRect? clip)
    {
        foreach (var (font, run, start, length) in Runs)
        {
            var glyphs = run.ToGlyphRun(font.Font, size, new SKPoint(x, baseline), Text.Substring(start, length)) with
            {
                Color = color,
                Embolden = font.SyntheticBold,
                SkewX = font.SyntheticItalic ? GlyphRun.SyntheticSlant : 0,
                Transform = transform,
                Clip = clip,
            };
            if (glyphs.Glyphs.Length > 0)
                page.DrawGlyphs(glyphs);
            x += run.Width(size);
        }
    }
}
