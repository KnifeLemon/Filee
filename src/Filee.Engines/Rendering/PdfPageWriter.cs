// Writes pages of the layout engine's page model as a PDF through Skia's PDF backend (SKDocument.CreatePdf), with
// fonts subset by Filee (FontSubsetter): SkiaSharp's backend has no subsetter of its own and embeds every font file
// whole (13 MB for one line of Malgun Gothic), and draws CFF and variable fonts as Type 3 fonts.
//
// Two passes: the glyphs every page uses are collected per font first, each font is subset once for the whole
// document (glyph IDs unchanged, so the shaped runs draw as they are), and then the pages are drawn with the subset
// typefaces. Skia writes each subset as a Type 0 / CIDFontType2 font with a ToUnicode map from the subset's cmap and
// /ActualText for glyphs that don't map one to one (see PageContent.DrawRun). Links and named destinations are Skia
// annotations; the outline (bookmarks) is added afterwards with PDFsharp, which Skia has no API for.
//
// docs/ENGINES.md ("Filee renderer: PDF output") compares this with a page writer of Filee's own on PDFsharp.

using Filee.Engines.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;

namespace Filee.Engines.Rendering;

/// <summary>Document information written into the PDF.</summary>
internal sealed record PdfDocumentInfo
{
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? Subject { get; init; }

    /// <summary>Outline (bookmark) entries in document order.</summary>
    public IReadOnlyList<OutlineEntry> Outline { get; init; } = [];
}

/// <summary>Page model → PDF.</summary>
internal static class PdfPageWriter
{
    /// <summary>Writes <paramref name="pages"/> as a PDF to <paramref name="output"/>.</summary>
    /// <param name="progress">Pages written, as a fraction of all pages.</param>
    public static void Write(Stream output, IReadOnlyList<PageContent> pages, PdfDocumentInfo? info = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        info ??= new PdfDocumentInfo();
        using var fonts = SubsetFonts(pages, cancellationToken);
        if (info.Outline.Count == 0 && pages.All(p => !NeedsExactSize(p)))
        {
            WritePages(output, pages, info, fonts, progress, cancellationToken);
            return;
        }
        using var buffer = new MemoryStream();
        WritePages(buffer, pages, info, fonts, progress, cancellationToken);
        buffer.Position = 0;
        Finish(buffer, output, pages, info.Outline);
    }

    /// <summary>
    /// True when the page size isn't whole points: Skia rounds the media box to whole points (A4 becomes 595 × 842) and
    /// draws from its top edge, so the box is set again afterwards.
    /// </summary>
    private static bool NeedsExactSize(PageContent page) =>
        page.Width != MathF.Round(page.Width) || page.Height != MathF.Round(page.Height);

    private static void WritePages(Stream output, IReadOnlyList<PageContent> pages, PdfDocumentInfo info, SubsetTypefaces fonts,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var metadata = new SKDocumentPdfMetadata
        {
            Title = info.Title,
            Author = info.Author,
            Subject = info.Subject,
            Creator = "Filee",
            Producer = "Filee",
            Creation = DateTime.Now,
            Modified = DateTime.Now,
            RasterDpi = SKDocument.DefaultRasterDpi,
            EncodingQuality = 100,
            PdfA = false,
        };
        using var stream = new SKManagedWStream(output);
        using var document = SKDocument.CreatePdf(stream, metadata) ?? throw new InvalidOperationException("Skia can't write PDF.");
        for (var i = 0; i < pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = pages[i];
            var canvas = document.BeginPage(page.Width, page.Height);
            page.Draw(canvas, fonts.For);
            document.EndPage();
            progress?.Report((i + 1.0) / pages.Count);
        }
        document.Close();
    }

    /// <summary>One subset typeface per font the pages use.</summary>
    private static SubsetTypefaces SubsetFonts(IReadOnlyList<PageContent> pages, CancellationToken cancellationToken)
    {
        var glyphs = new Dictionary<PageFont, (HashSet<int> Glyphs, HashSet<int> CodePoints)>(ReferenceEqualityComparer.Instance);
        foreach (var page in pages)
        {
            foreach (var item in page.Items)
            {
                if (item is not GlyphItem { Run: var run })
                    continue;
                if (!glyphs.TryGetValue(run.Font, out var used))
                    glyphs[run.Font] = used = ([], []);
                foreach (var glyph in run.Glyphs)
                    used.Glyphs.Add(glyph);
                foreach (var rune in run.Text.EnumerateRunes())
                    used.CodePoints.Add(rune.Value);
            }
        }
        var result = new SubsetTypefaces();
        foreach (var (font, used) in glyphs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(font, SubsetTypeface(font, used.Glyphs, used.CodePoints, cancellationToken));
        }
        return result;
    }

    /// <summary>
    /// A typeface over the subset of <paramref name="font"/>; the whole font when it can't be subset (Skia then
    /// embeds it whole or as a Type 3 font, still with its text).
    /// </summary>
    private static SKTypeface SubsetTypeface(PageFont font, IEnumerable<int> glyphs, IReadOnlySet<int> codePoints, CancellationToken cancellationToken = default)
    {
        if (!FontSubsetter.CanSubset(font.Sfnt, out _))
            return font.Typeface;
        var subset = FontSubsetter.Subset(font.Sfnt, glyphs, cancellationToken, codePoints).Write();
        return SKTypeface.FromData(SKData.CreateCopy(subset)) ?? font.Typeface;
    }

    /// <summary>
    /// What Skia can't write, added to the PDF it wrote: exact page sizes (a media box that ends at the top edge Skia
    /// drew from, so nothing moves) and the outline.
    /// </summary>
    private static void Finish(Stream pdf, Stream output, IReadOnlyList<PageContent> pages, IReadOnlyList<OutlineEntry> entries)
    {
        using var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Modify);
        for (var i = 0; i < pages.Count; i++)
        {
            if (!NeedsExactSize(pages[i]))
                continue;
            var top = MathF.Round(pages[i].Height);
            var box = new PdfArray(document);
            foreach (var value in new[] { 0, top - pages[i].Height, pages[i].Width, top })
                box.Elements.Add(new PdfReal(Math.Round(value, 3)));
            document.Pages[i].Elements["/MediaBox"] = box;
        }
        AddOutline(document, pages, entries);
        document.Save(output, closeStream: false);
    }

    /// <summary>Adds outline entries pointing into the pages (y measured from the top edge Skia drew from).</summary>
    private static void AddOutline(PdfDocument document, IReadOnlyList<PageContent> pages, IReadOnlyList<OutlineEntry> entries)
    {
        if (entries.Count == 0)
            return;
        var parents = new List<PdfOutlineCollection> { document.Outlines };
        foreach (var entry in entries)
        {
            if (entry.PageIndex < 0 || entry.PageIndex >= document.PageCount)
                throw new ArgumentException($"The outline entry '{entry.Title}' points to page {entry.PageIndex + 1}, which doesn't exist.");
            var level = Math.Clamp(entry.Level, 0, parents.Count - 1);
            var outline = parents[level].Add(entry.Title, document.Pages[entry.PageIndex], opened: level == 0);
            outline.PageDestinationType = PdfPageDestinationType.Xyz;
            outline.Top = MathF.Round(pages[entry.PageIndex].Height) - entry.Top;
            outline.Left = 0;
            parents.RemoveRange(level + 1, parents.Count - level - 1);
            parents.Add(outline.Outlines);
        }
        document.PageMode = PdfPageMode.UseOutlines;
    }

    /// <summary>Subset typefaces by font; disposes the ones it made.</summary>
    private sealed class SubsetTypefaces : IDisposable
    {
        private readonly Dictionary<PageFont, SKTypeface> _typefaces = new(ReferenceEqualityComparer.Instance);

        public void Add(PageFont font, SKTypeface typeface) => _typefaces[font] = typeface;

        public SKTypeface For(PageFont font) => _typefaces.GetValueOrDefault(font) ?? font.Typeface;

        public void Dispose()
        {
            foreach (var (font, typeface) in _typefaces)
            {
                if (!ReferenceEquals(typeface, font.Typeface))
                    typeface.Dispose();
            }
        }
    }
}
