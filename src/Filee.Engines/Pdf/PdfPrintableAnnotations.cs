// PDF/A-2 keeps an annotation only when its print flag is on (ISO 19005-2 6.3.2), and many PDF writers never set /F on
// links and markup, so Ghostscript's PDF/A mode leaves them out (#1496). Before a PDF becomes PDF/A, a copy with that
// flag turned on goes to Ghostscript instead. Form fields get it too: PDF/A keeps no fields, but Ghostscript draws a
// printing field's appearance into the page. Annotations that are hidden in any way and popups stay as they are;
// nothing else in the PDF is changed.
// PDF/A-2 also wants an appearance stream (/AP /N) on every kept annotation but links, popups and zero-size ones
// (ISO 19005-2 6.3.3), and Ghostscript copies annotations without one as they are (#1566). So the types of printing
// annotations without an appearance are collected too: Ghostscript draws those types into the page instead of keeping them.

using System.Collections.Frozen;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Pdf;

/// <summary>What <see cref="PdfPrintableAnnotations.Prepare"/> made of a PDF.</summary>
/// <param name="Copy">The copy whose visible annotations print, or null when Ghostscript should read the PDF itself.</param>
/// <param name="HasFormFields">The PDF has form fields (a non-empty <c>/AcroForm /Fields</c>); null when it couldn't be read.</param>
/// <param name="WithoutAppearance">The subtypes (e.g. <c>/Highlight</c>) of annotations that print in what Ghostscript
/// reads but have no appearance stream, which PDF/A doesn't allow; empty when there are none or the PDF couldn't be read.</param>
internal sealed record PdfAPreparation(string? Copy, bool? HasFormFields, IReadOnlySet<string>? WithoutAppearance = null)
{
    /// <summary>Never null: the one empty set when there are none, so preparations without any compare equal.</summary>
    public IReadOnlySet<string> WithoutAppearance { get; init; } = WithoutAppearance is { Count: > 0 } types ? types : FrozenSet<string>.Empty;
}

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
    /// for every annotation that isn't a popup, isn't hidden in any way and doesn't print yet. Also says whether the PDF
    /// has form fields, which PDF/A doesn't keep, and which types of printing annotations have no appearance stream.
    /// </summary>
    /// <returns>No copy when no annotation needs the flag or the copy can't be made (the PDF can't be read or changed,
    /// or saving fails): Ghostscript gets the PDF as it is then, and only annotations that printed in it count for
    /// <see cref="PdfAPreparation.WithoutAppearance"/>. Whether there are form fields is known once the PDF could be
    /// opened, even when the copy fails later.</returns>
    internal static PdfAPreparation Prepare(string input, string folder, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = Path.Combine(folder, "printable.pdf");
        bool? hasFormFields = null;
        // Types without an appearance among annotations that printed already, and among all that print in the copy.
        var printedWithoutAppearance = new HashSet<string>(StringComparer.Ordinal);
        var withoutAppearance = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (!PageTreeIsSafe(input))
                return new(null, null);
            using var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
            hasFormFields = HasFormFields(document);
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
                        || annotation.Elements.GetName("/Subtype") is "/Popup"
                        || Flags(annotation) is not { } flags
                        || (flags & (Invisible | Hidden | NoView | ToggleNoView)) != 0)
                        continue;
                    var printed = (flags & Print) != 0;
                    if (NeedsAppearance(annotation) is { } subtype)
                    {
                        withoutAppearance.Add(subtype);
                        if (printed)
                            printedWithoutAppearance.Add(subtype);
                    }
                    if (printed)
                        continue;
                    annotation.Elements["/F"] = new PdfInteger(flags | Print);
                    changed++;
                }
            }
            if (changed == 0)
                return new(null, hasFormFields, withoutAppearance);

            // PDFsharp names itself the creator of a PDF that names none, and Ghostscript would carry that into the PDF/A.
            if (!document.Info.Elements.ContainsKey("/Creator"))
                document.Info.Elements["/Creator"] = new PdfString("");
            cancellationToken.ThrowIfCancellationRequested();
            document.Save(target);
            return new(target, hasFormFields, withoutAppearance);
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
            // Ghostscript reads the PDF itself: the annotations that didn't print there are left out of the PDF/A anyway.
            return new(null, hasFormFields, printedWithoutAppearance);
        }
    }

    /// <summary>
    /// The annotation's subtype when PDF/A wants an appearance stream for it and it has none (ISO 19005-2 6.3.3: every
    /// annotation but links, popups and zero-size ones), otherwise null. Form fields are left out: Ghostscript draws a
    /// field's appearance into the page and keeps no fields in a PDF/A.
    /// </summary>
    private static string? NeedsAppearance(PdfDictionary annotation)
    {
        var subtype = annotation.Elements.GetName("/Subtype");
        if (subtype is "" or "/Link" or "/Popup" or "/Widget" || HasNoSize(annotation))
            return null;
        return PdfCompressor.Resolve(annotation.Elements["/AP"]) is PdfDictionary appearance
               && PdfCompressor.Resolve(appearance.Elements["/N"]) is PdfDictionary
            ? null
            : subtype;
    }

    /// <summary>The annotation's /Rect has no width or no height; false when it isn't four numbers.</summary>
    private static bool HasNoSize(PdfDictionary annotation)
    {
        if (PdfCompressor.Resolve(annotation.Elements["/Rect"]) is not PdfArray { Elements.Count: 4 } rect)
            return false;
        var values = rect.Elements.Select(item => PdfCompressor.Resolve(item) switch
        {
            PdfInteger number => (double?)number.Value,
            PdfIntegerObject number => number.Value,
            PdfReal number => number.Value,
            PdfRealObject number => number.Value,
            _ => null,
        }).ToList();
        if (values.Any(value => value is null))
            return false;
        return values[0] == values[2] || values[1] == values[3];
    }

    /// <summary>The catalog's <c>/AcroForm</c> has a <c>/Fields</c> array with at least one field.</summary>
    private static bool HasFormFields(PdfDocument document) =>
        PdfCompressor.Resolve(document.Internals.Catalog.Elements["/AcroForm"]) is PdfDictionary form
        && PdfCompressor.Resolve(form.Elements["/Fields"]) is PdfArray { Elements.Count: > 0 };

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
