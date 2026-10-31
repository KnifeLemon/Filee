// Filee's own document renderer: a document model laid out by the layout engine (PageComposer, ParagraphLayouter)
// and written as a PDF by PdfPageWriter, with the headings as the PDF outline, or drawn straight into pictures of its
// pages (no PDF → PDFium step in between).

using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Rendering;
using SkiaSharp;

namespace Filee.Engines.Typesetting;

internal static class FileeRenderer
{
    /// <summary>The document as a PDF.</summary>
    /// <param name="storedLines">Paragraphs follow 한글's stored line layout where it can be used.</param>
    /// <param name="styles">
    /// Resolves the formats a model read from another format leaves to the 한글 style; null for models read from HWPX,
    /// whose formats are complete.
    /// </param>
    /// <param name="progress">Pages written, as a fraction of all pages.</param>
    public static byte[] ToPdf(HDocument document, CancellationToken cancellationToken = default, bool storedLines = true,
        StyleResolver? styles = null, IProgress<double>? progress = null)
    {
        var composed = Compose(document, cancellationToken, storedLines, styles);
        using var output = new MemoryStream();
        PdfPageWriter.Write(output, composed.Pages, new PdfDocumentInfo { Title = document.Title, Outline = composed.Outline },
            progress, cancellationToken);
        return output.ToArray();
    }

    /// <summary>
    /// The document's pages as pictures at <paramref name="dpi"/>, encoded as <paramref name="format"/> on white, one
    /// per page in order. The pages are laid out first; each picture is drawn when it is asked for.
    /// </summary>
    /// <param name="quality">Encoder quality (JPEG, WebP).</param>
    /// <param name="progress">Pages drawn, as a fraction of all pages.</param>
    public static IEnumerable<byte[]> ToImages(HDocument document, int dpi, SKEncodedImageFormat format = SKEncodedImageFormat.Png,
        int quality = 90, IProgress<double>? progress = null, CancellationToken cancellationToken = default,
        bool storedLines = true, StyleResolver? styles = null)
    {
        var pages = Compose(document, cancellationToken, storedLines, styles).Pages;
        for (var i = 0; i < pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var bitmap = pages[i].Render(dpi);
            using var data = bitmap.Encode(format, quality) ?? throw new InvalidOperationException($"Skia can't encode {format}.");
            progress?.Report((i + 1.0) / pages.Count);
            yield return data.ToArray();
        }
    }

    private static ComposedDocument Compose(HDocument document, CancellationToken cancellationToken, bool storedLines, StyleResolver? styles)
    {
        using var shaper = new Shaper();
        return new PageComposer(new FontResolver(), shaper, document.Kinsoku, styles, storedLines).Compose(document, cancellationToken);
    }
}
