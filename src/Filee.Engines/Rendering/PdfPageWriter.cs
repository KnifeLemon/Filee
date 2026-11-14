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
// A glyph no character of the cmap leads to (a font feature put it in place of the character's own glyph: Noto Sans
// CJK's digits after a Latin letter, small caps, a ligature) is written to the ToUnicode map as U+0000, its text only
// as /ActualText, which many readers ignore (they copy "H" and a NUL for "H1"). Where such a glyph always draws the
// same text as a cluster of its own, that text goes into the map afterwards, with PDFsharp.
//
// docs/ENGINES.md ("Filee renderer: PDF output") compares this with a page writer of Filee's own on PDFsharp.

using System.Text;
using System.Text.RegularExpressions;
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
internal static partial class PdfPageWriter
{
    /// <summary>Writes <paramref name="pages"/> as a PDF to <paramref name="output"/>.</summary>
    /// <param name="progress">Pages written, as a fraction of all pages.</param>
    public static void Write(Stream output, IReadOnlyList<PageContent> pages, PdfDocumentInfo? info = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        info ??= new PdfDocumentInfo();
        using var fonts = SubsetFonts(pages, cancellationToken);
        if (info.Outline.Count == 0 && fonts.Unmapped.Count == 0 && pages.All(p => !NeedsExactSize(p)))
        {
            WritePages(output, pages, info, fonts, progress, cancellationToken);
            return;
        }
        using var buffer = new MemoryStream();
        WritePages(buffer, pages, info, fonts, progress, cancellationToken);
        buffer.Position = 0;
        Finish(buffer, output, pages, info.Outline, fonts.Unmapped);
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
        var glyphs = new Dictionary<PageFont, (HashSet<int> Glyphs, HashSet<int> CodePoints, Dictionary<ushort, string?> Texts)>(ReferenceEqualityComparer.Instance);
        foreach (var page in pages)
        {
            foreach (var item in page.Items)
            {
                if (item is not GlyphItem { Run: var run })
                    continue;
                if (!glyphs.TryGetValue(run.Font, out var used))
                    glyphs[run.Font] = used = ([], [], []);
                foreach (var glyph in run.Glyphs)
                    used.Glyphs.Add(glyph);
                foreach (var rune in run.Text.EnumerateRunes())
                    used.CodePoints.Add(rune.Value);
                GlyphTexts(run, used.Texts);
            }
        }
        var result = new SubsetTypefaces();
        foreach (var (font, used) in glyphs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var typeface = SubsetTypeface(font, used.Glyphs, used.CodePoints, cancellationToken);
            result.Add(font, typeface);
            // The glyphs the subset's cmap leads to: it keeps the entries of the drawn characters (FontSubsetter).
            var mapped = used.CodePoints.Select(c => font.Typeface.GetGlyph(c)).ToHashSet();
            result.AddUnmapped(typeface.PostScriptName, used.Texts.Where(t => t.Value is not null && !mapped.Contains(t.Key)));
        }
        return result;
    }

    /// <summary>
    /// The text of each glyph of <paramref name="run"/> that is a cluster on its own; a glyph found with two different
    /// texts gets null.
    /// </summary>
    internal static void GlyphTexts(GlyphRun run, Dictionary<ushort, string?> texts)
    {
        var (glyphs, _, clusters) = PageContent.LogicalOrder(run);
        for (var i = 0; i < glyphs.Length; i++)
        {
            var start = clusters[i];
            if ((i > 0 && clusters[i - 1] == start) || (i + 1 < glyphs.Length && clusters[i + 1] == start))
                continue; // one of several glyphs of a cluster
            var end = i + 1 < glyphs.Length ? clusters[i + 1] : run.Text.Length;
            // A ligature's few letters at most: more means a run whose text goes on past its glyphs.
            if (start < 0 || end <= start || end > run.Text.Length || end - start > MaxGlyphText)
                continue;
            var text = run.Text.AsSpan(start, end - start);
            if (texts.TryGetValue(glyphs[i], out var known))
            {
                if (known is not null && !text.SequenceEqual(known))
                    texts[glyphs[i]] = null;
            }
            else if (!text.ContainsAnyInRange('\0', '\x1F') && !text.ContainsAnyInRange('\x7F', '\x9F'))
            {
                texts[glyphs[i]] = text.ToString();
            }
        }
    }

    /// <summary>The most UTF-16 units of text one glyph gets in the ToUnicode map ("ffi", an emoji sequence).</summary>
    private const int MaxGlyphText = 8;

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
    private static void Finish(Stream pdf, Stream output, IReadOnlyList<PageContent> pages, IReadOnlyList<OutlineEntry> entries,
        IReadOnlyDictionary<string, Dictionary<ushort, string?>> unmapped)
    {
        using var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Modify);
        AddGlyphText(document, unmapped);
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

    /// <summary>
    /// Gives the glyphs Skia mapped to U+0000 in a font's ToUnicode map their text. Fonts are matched by PostScript name
    /// (Skia's BaseFont is a subset tag and the typeface's PostScript name); codes are glyph IDs (Identity-H with an
    /// identity CIDToGIDMap).
    /// </summary>
    private static void AddGlyphText(PdfDocument document, IReadOnlyDictionary<string, Dictionary<ushort, string?>> unmapped)
    {
        if (unmapped.Count == 0)
            return;
        var done = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var item in document.Internals.GetAllObjects())
        {
            if (item is not PdfDictionary font || font.Elements.GetName("/Type") != "/Font" || font.Elements.GetName("/Subtype") != "/Type0"
                || !unmapped.TryGetValue(PostScriptName(font.Elements.GetName("/BaseFont")), out var texts)
                || font.Elements.GetDictionary("/ToUnicode") is not { Stream: { } stream } map || !done.Add(map))
                continue;
            var cmap = Encoding.Latin1.GetString(stream.UnfilteredValue);
            var patched = BfChar().Replace(cmap, section => ZeroEntry().Replace(section.Value, entry =>
                texts.TryGetValue(Convert.ToUInt16(entry.Groups[1].Value, 16), out var text) && text is not null
                    ? $"<{entry.Groups[1].Value}>{entry.Groups[2].Value}<{Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text))}>"
                    : entry.Value));
            if (patched == cmap)
                continue;
            stream.Value = Encoding.Latin1.GetBytes(patched);
            map.Elements.Remove("/Filter");
            map.Elements.Remove("/DecodeParms");
        }
    }

    /// <summary>A PostScript name as a key: without a subset tag ("AAAAAA+") and without spaces.</summary>
    internal static string PostScriptName(string name)
    {
        name = name.TrimStart('/');
        if (name.Length > 7 && name[6] == '+' && name[..6].All(char.IsAsciiLetterUpper))
            name = name[7..];
        return name.Replace(" ", "", StringComparison.Ordinal);
    }

    /// <summary>A bfchar section of a CMap.</summary>
    [GeneratedRegex(@"beginbfchar.*?endbfchar", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex BfChar();

    /// <summary>A bfchar entry of a two-byte code mapped to U+0000.</summary>
    [GeneratedRegex(@"<([0-9A-Fa-f]{4})>(\s*)<0000>", RegexOptions.CultureInvariant)]
    private static partial Regex ZeroEntry();

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
        private readonly Dictionary<string, Dictionary<ushort, string?>> _unmapped = new(StringComparer.Ordinal);

        /// <summary>Text of glyphs no character of the cmap leads to, by the PostScript name of the typeface drawing them.</summary>
        public IReadOnlyDictionary<string, Dictionary<ushort, string?>> Unmapped => _unmapped;

        public void Add(PageFont font, SKTypeface typeface) => _typefaces[font] = typeface;

        public void AddUnmapped(string? postScriptName, IEnumerable<KeyValuePair<ushort, string?>> texts)
        {
            if (string.IsNullOrEmpty(postScriptName))
                return;
            var key = PostScriptName(postScriptName);
            foreach (var (glyph, text) in texts)
            {
                if (!_unmapped.TryGetValue(key, out var known))
                    _unmapped[key] = known = [];
                // Two fonts of one name that disagree about a glyph: neither text.
                known[glyph] = known.TryGetValue(glyph, out var other) && other != text ? null : text;
            }
        }

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
