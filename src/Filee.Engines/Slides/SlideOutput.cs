// A deck as a PDF (one page per slide, fonts subset and embedded by PdfPageWriter) or as pictures of its slides, drawn
// from the same page record (no PDF → PDFium step).

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
}
