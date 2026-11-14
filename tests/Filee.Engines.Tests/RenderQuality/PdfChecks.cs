// Read-back checks of a rendered PDF (PdfPig and PDFsharp): each takes the PDF (and what the document asks for) and
// returns a measured value, whether it meets the ideal, and the first offenders. The render quality tests compare the
// values with the thresholds in render-thresholds.json; the checks themselves are tested on PDFs built in code
// (RenderChecksTests).

using System.Globalization;
using System.Text;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Filee.Engines.Tests.RenderQuality;

/// <summary>A measured value; <paramref name="Ok"/> when it meets the ideal (no overlaps, the exact text, ...).</summary>
internal sealed record CheckResult(string Name, double Value, bool Ok, string Detail)
{
    public override string ToString() => $"{Name} = {Value.ToString("0.####", CultureInfo.InvariantCulture)}{(Detail.Length > 0 ? $" ({Detail})" : "")}";
}

/// <summary>The body of a page in points from its edges: the paper less 한글's margins (header and footer areas inside).</summary>
internal readonly record struct PageMargins(double Left, double Top, double Right, double Bottom);

/// <summary>An embedded font as the PDF describes it (a Type 0 font by its descendant).</summary>
internal sealed record PdfFontInfo(string BaseFont, string Subtype, string? File, int Length)
{
    /// <summary>The font program is in the file. Type 3 fonts carry their glyphs as drawing procedures.</summary>
    public bool Embedded => File is not null || Subtype == "/Type3";

    /// <summary>Only the used glyphs are embedded: a subset tag (ABCDEF+), or a Type 3 font (one procedure per glyph drawn).</summary>
    public bool Subset => Subtype == "/Type3" || (BaseFont.Length > 8 && BaseFont[7] == '+' && BaseFont[1..7].All(char.IsAsciiLetterUpper));
}

internal static class PdfChecks
{
    public const string Text = "text";
    public const string Overlaps = "overlaps";
    public const string OutsidePage = "outside-page";
    public const string OutsideMargins = "outside-margins";
    public const string Pages = "pages";
    public const string Baselines = "baselines";
    public const string Fonts = "fonts";

    private const int Examples = 3;

    /// <summary>
    /// Share of the expected text (white space left out) found in the PDF's text in the same order. Text the document
    /// adds (list numbers, headers, page numbers) doesn't count against it.
    /// </summary>
    public static CheckResult TextRoundTrip(byte[] pdf, string expected)
    {
        var want = PdfLines.Normalise(expected);
        var got = PdfLines.Normalise(PdfLines.Text(pdf));
        if (want.Length == 0)
            return new CheckResult(Text, 1, true, "");
        var (pairs, exact) = SequenceMatch.Match(want.ToCharArray(), got.ToCharArray());
        var missing = new StringBuilder();
        var matched = new bool[want.Length];
        foreach (var (a, _) in pairs)
            matched[a] = true;
        for (var i = 0; i < want.Length && missing.Length < 40; i++)
            if (!matched[i])
                missing.Append(want[i]);
        var share = (double)pairs.Count / want.Length;
        var detail = share >= 1 ? "" : $"missing \"{missing}\"{(exact ? "" : ", approximate")}";
        return new CheckResult(Text, share, share >= 1, detail);
    }

    /// <summary>
    /// Share of neighbouring glyphs on a line whose advances overlap by more than <paramref name="tolerance"/> em (a
    /// share, so documents of any length compare). Marks and other glyphs without an advance are left out: they sit on
    /// their base by design. Kerning deeper than the tolerance counts too, unless <paramref name="kerning"/> gives the
    /// pair's own kerning (in em, positive = closer): then only an overlap deeper than the font asks for counts.
    /// </summary>
    public static CheckResult OverlappingGlyphs(byte[] pdf, double tolerance = 0.05, Func<Letter, Letter, double>? kerning = null)
    {
        int count = 0, pairs = 0;
        var examples = new List<string>();
        foreach (var line in PdfLines.Read(pdf))
        {
            var glyphs = line.Letters
                .Where(l => Math.Abs(l.EndBaseLine.X - l.StartBaseLine.X) > 0.01 * PdfLines.Size(l))
                .OrderBy(l => Math.Min(l.StartBaseLine.X, l.EndBaseLine.X))
                .ToList();
            for (var i = 1; i < glyphs.Count; i++)
            {
                pairs++;
                var previous = glyphs[i - 1];
                var next = glyphs[i];
                var overlap = Math.Max(previous.StartBaseLine.X, previous.EndBaseLine.X) - Math.Min(next.StartBaseLine.X, next.EndBaseLine.X);
                var em = Math.Max(PdfLines.Size(previous), PdfLines.Size(next));
                if (overlap <= tolerance * em || kerning is not null && overlap <= (tolerance + kerning(previous, next)) * em)
                    continue;
                count++;
                if (examples.Count < Examples)
                    examples.Add(string.Create(CultureInfo.InvariantCulture, $"p{line.Page + 1} \"{previous.Value}{next.Value}\" {overlap / em:0.##} em"));
            }
        }
        var detail = count == 0 ? "" : $"{count} of {pairs} pairs: {string.Join(", ", examples)}";
        return new CheckResult(Overlaps, pairs == 0 ? 0 : (double)count / pairs, count == 0, detail);
    }

    /// <summary>Glyphs whose outline leaves the page by more than <paramref name="tolerance"/> points.</summary>
    public static CheckResult GlyphsOutsidePage(byte[] pdf, double tolerance = 0.5)
    {
        var count = 0;
        var examples = new List<string>();
        using var document = PdfDocument.Open(pdf);
        foreach (var page in document.GetPages())
        {
            foreach (var letter in page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)))
            {
                var box = letter.BoundingBox;
                if (box.Width <= 0 && box.Height <= 0)
                    continue;
                if (box.Left >= -tolerance && box.Bottom >= -tolerance && box.Right <= page.Width + tolerance && box.Top <= page.Height + tolerance)
                    continue;
                count++;
                if (examples.Count < Examples)
                    examples.Add(string.Create(CultureInfo.InvariantCulture, $"p{page.Number} \"{letter.Value}\" at {box.Left:0.#}, {page.Height - box.Top:0.#}"));
            }
        }
        return new CheckResult(OutsidePage, count, count == 0, string.Join(", ", examples));
    }

    /// <summary>
    /// Glyphs whose advance or baseline lies outside the body by more than <paramref name="tolerance"/> points: the page
    /// less its margins, with the header and footer areas (which 한글 puts inside the top and bottom margins).
    /// </summary>
    public static CheckResult GlyphsOutsideMargins(byte[] pdf, PageMargins margins, double tolerance = 1)
    {
        var count = 0;
        var examples = new List<string>();
        using var document = PdfDocument.Open(pdf);
        foreach (var page in document.GetPages())
        {
            double left = margins.Left - tolerance, right = page.Width - margins.Right + tolerance;
            double top = margins.Top - tolerance, bottom = page.Height - margins.Bottom + tolerance;
            foreach (var letter in page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)))
            {
                var start = Math.Min(letter.StartBaseLine.X, letter.EndBaseLine.X);
                var end = Math.Max(letter.StartBaseLine.X, letter.EndBaseLine.X);
                var baseline = page.Height - letter.StartBaseLine.Y;
                if (start >= left && end <= right && baseline >= top && baseline <= bottom)
                    continue;
                count++;
                if (examples.Count < Examples)
                    examples.Add(string.Create(CultureInfo.InvariantCulture, $"p{page.Number} \"{letter.Value}\" at {start:0.#}, {baseline:0.#}"));
            }
        }
        return new CheckResult(OutsideMargins, count, count == 0, string.Join(", ", examples));
    }

    /// <summary>The number of pages, ideal when it is <paramref name="expected"/>.</summary>
    public static CheckResult PageCount(byte[] pdf, int expected)
    {
        using var document = PdfDocument.Open(pdf);
        var pages = document.NumberOfPages;
        return new CheckResult(Pages, pages, pages == expected, pages == expected ? "" : $"expected {expected}");
    }

    /// <summary>
    /// Share of the expected baselines (points from the top of a page, in reading order) met within
    /// <paramref name="tolerance"/> points by the line at the same position. Missing or extra lines count as misses.
    /// </summary>
    public static CheckResult BaselinesMatch(IReadOnlyList<double> actual, IReadOnlyList<double> expected, double tolerance = 0.5)
    {
        var total = Math.Max(actual.Count, expected.Count);
        if (total == 0)
            return new CheckResult(Baselines, 1, true, "");
        var met = 0;
        var examples = new List<string>();
        for (var i = 0; i < Math.Min(actual.Count, expected.Count); i++)
        {
            if (Math.Abs(actual[i] - expected[i]) <= tolerance)
                met++;
            else if (examples.Count < Examples)
                examples.Add(string.Create(CultureInfo.InvariantCulture, $"line {i + 1} at {actual[i]:0.##}, expected {expected[i]:0.##}"));
        }
        if (actual.Count != expected.Count)
            examples.Add($"{actual.Count} lines, expected {expected.Count}");
        var share = (double)met / total;
        return new CheckResult(Baselines, share, met == total, string.Join(", ", examples));
    }

    /// <summary>
    /// Lines of each page <paramref name="pitch"/> points apart from the page's first line (a document whose lines all
    /// have one fixed height): the baselines check against the grid the document asks for.
    /// </summary>
    public static CheckResult BaselinePitch(byte[] pdf, double pitch, double tolerance = 0.5)
    {
        var lines = PdfLines.Read(pdf);
        var actual = new List<double>();
        var expected = new List<double>();
        foreach (var page in lines.GroupBy(l => l.Page))
        {
            var first = page.First().Baseline;
            var i = 0;
            foreach (var line in page)
            {
                actual.Add(line.Baseline);
                expected.Add(first + i++ * pitch);
            }
        }
        return BaselinesMatch(actual, expected, tolerance);
    }

    /// <summary>Fonts that aren't embedded or aren't subset (Type 3 fonts count as both).</summary>
    public static CheckResult FontsEmbeddedAndSubset(byte[] pdf)
    {
        var fonts = ListFonts(pdf);
        var bad = fonts.Where(f => !f.Embedded || !f.Subset).ToList();
        var detail = string.Join(", ", bad.Take(Examples).Select(f => $"{f.BaseFont.TrimStart('/')} {(f.Embedded ? "not subset" : "not embedded")}"));
        return new CheckResult(Fonts, bad.Count, bad.Count == 0, fonts.Count == 0 ? "no fonts" : detail);
    }

    /// <summary>The fonts of every page, each once.</summary>
    public static List<PdfFontInfo> ListFonts(byte[] pdf)
    {
        using var document = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        var result = new List<PdfFontInfo>();
        var seen = new HashSet<PdfSharp.Pdf.PdfDictionary>(ReferenceEqualityComparer.Instance);
        void Collect(PdfSharp.Pdf.PdfDictionary? resources, int depth)
        {
            if (resources is null || depth > 8)
                return;
            if (resources.Elements.GetDictionary("/Font") is { } fonts)
            {
                foreach (var key in fonts.Elements.Keys)
                {
                    var font = fonts.Elements.GetDictionary(key)!;
                    if (!seen.Add(font))
                        continue;
                    var subtype = font.Elements.GetName("/Subtype");
                    var described = subtype == "/Type0" ? font.Elements.GetArray("/DescendantFonts")!.Elements.GetDictionary(0)! : font;
                    var descriptor = described.Elements.GetDictionary("/FontDescriptor");
                    var file = new[] { "/FontFile", "/FontFile2", "/FontFile3" }.FirstOrDefault(k => descriptor?.Elements.GetDictionary(k) is not null);
                    result.Add(new PdfFontInfo(font.Elements.GetName("/BaseFont"), subtype, file,
                        file is null ? 0 : descriptor!.Elements.GetDictionary(file)!.Stream.Length));
                }
            }
            // Fonts used inside form XObjects (some writers draw each page or each glyph run as one).
            if (resources.Elements.GetDictionary("/XObject") is { } objects)
                foreach (var key in objects.Elements.Keys)
                    if (objects.Elements.GetDictionary(key) is { } form && form.Elements.GetName("/Subtype") == "/Form")
                        Collect(form.Elements.GetDictionary("/Resources"), depth + 1);
        }
        foreach (var page in document.Pages)
            Collect(page.Resources, 0);
        return result;
    }
}
