// PDF/A-2 keeps an annotation only when its print flag is on (ISO 19005-2 6.3.2), and many PDF writers never set /F on
// links and markup, so Ghostscript's PDF/A mode leaves them out (#1496). Before a PDF becomes PDF/A, a copy with that
// flag turned on goes to Ghostscript instead. Annotations that are hidden in any way, popups and form fields stay as
// they are; nothing else in the PDF is changed.

using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Pdf;

/// <summary>A copy of a PDF whose visible annotations print, for PDF/A.</summary>
internal static class PdfPrintableAnnotations
{
    private const int Invisible = 1;
    private const int Hidden = 2;
    private const int Print = 4;
    private const int NoView = 32;
    private const int ToggleNoView = 256;

    // A normal page tree is a few levels deep; the node limit is far above any real page count.
    private const int MaxTreeDepth = 64;
    private const int MaxTreeNodes = 100_000;

    /// <summary>
    /// Writes <c>printable.pdf</c> in <paramref name="folder"/>: <paramref name="input"/> with the print flag turned on
    /// for every annotation that isn't a popup or form field, isn't hidden in any way and doesn't print yet.
    /// </summary>
    /// <returns>The copy, or null when no annotation needs the flag or the copy can't be made (the PDF can't be read
    /// or changed, or saving fails): Ghostscript gets the PDF as it is then.</returns>
    internal static string? Prepare(string input, string folder, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = Path.Combine(folder, "printable.pdf");
        try
        {
            if (!PageTreeIsSafe(input))
                return null;
            using var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
            var changed = 0;
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance); // one annotation on several pages
            foreach (var page in document.Pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (PdfCompressor.Resolve(page.Elements["/Annots"]) is not PdfArray annotations)
                    continue;
                foreach (var item in annotations.Elements)
                {
                    if (PdfCompressor.Resolve(item) is not PdfDictionary annotation || !seen.Add(annotation)
                        || annotation.Elements.GetName("/Subtype") is "/Popup" or "/Widget"
                        || Flags(annotation) is not { } flags
                        || (flags & (Invisible | Hidden | NoView | ToggleNoView)) != 0 || (flags & Print) != 0)
                        continue;
                    annotation.Elements["/F"] = new PdfInteger(flags | Print);
                    changed++;
                }
            }
            if (changed == 0)
                return null;

            // PDFsharp names itself the creator of a PDF that names none, and Ghostscript would carry that into the PDF/A.
            if (!document.Info.Elements.ContainsKey("/Creator"))
                document.Info.Elements["/Creator"] = new PdfString("");
            cancellationToken.ThrowIfCancellationRequested();
            document.Save(target);
            return target;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                if (File.Exists(target))
                    File.Delete(target);
            }
            catch (Exception deleting) when (deleting is IOException or UnauthorizedAccessException)
            {
                // Only a leftover in the work folder.
            }
            return null;
        }
    }

    /// <summary>/F as a number, 0 when it is missing, null when it is something else.</summary>
    private static int? Flags(PdfDictionary annotation) => PdfCompressor.Resolve(annotation.Elements["/F"]) switch
    {
        null => 0,
        PdfInteger flags => flags.Value,
        PdfIntegerObject flags => flags.Value,
        _ => null,
    };

    /// <summary>
    /// The page tree has no loops and stays within the limits. PDFsharp flattens the tree when it opens a PDF for
    /// changes, recursing without such checks, so a page tree that contains itself would overflow the stack.
    /// Opening for import doesn't flatten it.
    /// </summary>
    private static bool PageTreeIsSafe(string input)
    {
        using var document = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        if (PdfCompressor.Resolve(document.Internals.Catalog.Elements["/Pages"]) is not PdfDictionary root)
            return false;
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(PdfDictionary Node, int Depth)>();
        pending.Push((root, 1));
        while (pending.TryPop(out var entry))
        {
            if (entry.Depth > MaxTreeDepth || !visited.Add(entry.Node) || visited.Count > MaxTreeNodes)
                return false;
            if (PdfCompressor.Resolve(entry.Node.Elements["/Kids"]) is not PdfArray kids)
                continue;
            foreach (var kid in kids.Elements)
            {
                if (PdfCompressor.Resolve(kid) is PdfDictionary child)
                    pending.Push((child, entry.Depth + 1));
            }
        }
        return true;
    }
}
