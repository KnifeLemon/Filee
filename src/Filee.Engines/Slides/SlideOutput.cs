// A deck as a PDF (one page per slide, fonts subset and embedded by PdfPageWriter), as pictures of its slides or as an
// SVG per slide, drawn from the same page record (no PDF → PDFium step).
//
// SVG: Skia's SVG canvas, in pixels at 96 dpi (the slide's size in points × 4/3). Text is written as the outlines of
// its glyphs, as shaped and placed for the PDF: an SVG viewer has neither the fonts nor HarfBuzz's shaping, and Skia
// would write text elements by character, which loses ligatures, conjuncts and right-to-left order. Pictures are
// embedded as data URIs; links are left out.

using Filee.Engines.Rendering;
using Filee.Engines.Typesetting;
using SkiaSharp;

namespace Filee.Engines.Slides;

internal static class SlideOutput
{
    /// <param name="progress">Slides written, as a fraction of all slides.</param>
    public static byte[] ToPdf(SlideDeck deck, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var shaper = new Shaper();
        using var renderer = new SlideRenderer(new FontResolver(), shaper, cancellationToken);
        var pages = renderer.Render(deck);
        using var output = new MemoryStream();
        PdfPageWriter.Write(output, pages, new PdfDocumentInfo { Title = deck.Title }, progress, cancellationToken);
        return output.ToArray();
    }

    /// <summary>The slides <paramref name="slides"/> (indices, in order) as pictures at <paramref name="dpi"/>, on white.</summary>
    public static IEnumerable<SKBitmap> ToBitmaps(SlideDeck deck, float dpi, IReadOnlyList<int>? slides = null, CancellationToken cancellationToken = default)
    {
        using var shaper = new Shaper();
        using var renderer = new SlideRenderer(new FontResolver(), shaper, cancellationToken);
        foreach (var index in slides ?? Enumerable.Range(0, deck.Slides.Count).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = renderer.Render(deck, deck.Slides[index]);
            yield return page.Render(dpi);
        }
    }

    /// <summary>Pixels of an SVG per point.</summary>
    private const float SvgScale = 96f / 72f;

    /// <summary>The slides <paramref name="slides"/> (indices, in order) as SVG files, one per slide.</summary>
    public static IEnumerable<byte[]> ToSvg(SlideDeck deck, IReadOnlyList<int>? slides = null, CancellationToken cancellationToken = default)
    {
        using var shaper = new Shaper();
        using var renderer = new SlideRenderer(new FontResolver(), shaper, cancellationToken);
        foreach (var index in slides ?? Enumerable.Range(0, deck.Slides.Count).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = renderer.Render(deck, deck.Slides[index]);
            yield return Svg(page, cancellationToken);
        }
    }

    /// <summary>One page as an SVG, its text as outlines.</summary>
    internal static byte[] Svg(PageContent page, CancellationToken cancellationToken = default)
    {
        using var output = new MemoryStream();
        // The SVG canvas writes its XML when disposed.
        using (var canvas = SKSvgCanvas.Create(new SKRect(0, 0, page.Width * SvgScale, page.Height * SvgScale), output))
        {
            canvas.Scale(SvgScale);
            foreach (var item in page.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (item)
                {
                    case GlyphItem glyphs:
                        DrawOutlines(canvas, glyphs.Run);
                        break;
                    case DrawingItem drawing:
                        canvas.Save();
                        drawing.Drawing(canvas);
                        canvas.Restore();
                        break;
                }
            }
        }
        return output.ToArray();
    }

    /// <summary>A glyph run as one filled path of its glyphs' outlines, in the font as PageContent draws it.</summary>
    private static void DrawOutlines(SKCanvas canvas, GlyphRun run)
    {
        if (run.Glyphs.Length == 0)
            return;
        using var font = new SKFont(run.Font.Typeface, run.Size)
        {
            ScaleX = run.ScaleX,
            SkewX = run.SkewX,
            Embolden = run.Embolden,
            Subpixel = true,
            Hinting = SKFontHinting.None,
            LinearMetrics = true,
        };
        using var outlines = new SKPath();
        for (var i = 0; i < run.Glyphs.Length; i++)
        {
            using var glyph = font.GetGlyphPath(run.Glyphs[i]);
            if (glyph is not null && !glyph.IsEmpty)
                outlines.AddPath(glyph, run.Positions[i].X, run.Positions[i].Y);
        }
        if (outlines.IsEmpty)
            return;
        using var paint = new SKPaint { Color = run.Color, IsAntialias = true, Style = SKPaintStyle.Fill };
        canvas.Save();
        if (run.Clip is { } clip)
            canvas.ClipRect(clip);
        if (run.Transform is { } transform)
            canvas.Concat(in transform);
        canvas.DrawPath(outlines, paint);
        canvas.Restore();
    }
}
