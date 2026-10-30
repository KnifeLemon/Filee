// Filee's own document renderer: a document model laid out by the layout engine (PageComposer, ParagraphLayouter)
// and written as a PDF by PdfPageWriter, with the headings as the PDF outline.

using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Rendering;

namespace Filee.Engines.Typesetting;

internal static class FileeRenderer
{
    /// <summary>The document as a PDF.</summary>
    /// <param name="storedLines">Paragraphs follow 한글's stored line layout where it can be used.</param>
    /// <param name="styles">
    /// Resolves the formats a model read from another format leaves to the 한글 style; null for models read from HWPX,
    /// whose formats are complete.
    /// </param>
    public static byte[] ToPdf(HDocument document, CancellationToken cancellationToken = default, bool storedLines = true, StyleResolver? styles = null)
    {
        using var shaper = new Shaper();
        var composed = new PageComposer(new FontResolver(), shaper, document.Kinsoku, styles, storedLines).Compose(document, cancellationToken);
        using var output = new MemoryStream();
        PdfPageWriter.Write(output, composed.Pages, new PdfDocumentInfo { Title = document.Title, Outline = composed.Outline },
            cancellationToken: cancellationToken);
        return output.ToArray();
    }
}
