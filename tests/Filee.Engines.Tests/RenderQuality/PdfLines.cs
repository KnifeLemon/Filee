// What the render checks read back from a PDF with PdfPig: the text in content order (with /ActualText spans in place
// of their glyphs, as Filee's page writer marks ligatures, Indic clusters and Arabic forms), and the text lines of
// each page: letters on one baseline, measured from the top of the page like the layout engine measures.

using System.Globalization;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Filee.Engines.Tests.RenderQuality;

/// <summary>One line of text on a page: letters sharing a baseline, in content order.</summary>
/// <param name="Page">0-based page index.</param>
/// <param name="Baseline">Baseline in points from the top of the page.</param>
/// <param name="Size">Largest letter size on the line, in points.</param>
/// <param name="Letters">The letters, in content-stream order.</param>
internal sealed record TextLine(int Page, double Baseline, double Size, IReadOnlyList<Letter> Letters)
{
    /// <summary>The line's text without white space, normalised (NFC): what two renderers of one document share.</summary>
    public string Key { get; } = PdfLines.Normalise(string.Concat(Letters.Select(l => l.Value)));
}

internal static class PdfLines
{
    /// <summary>A letter belongs to a line when its baseline is within this share of the line's size.</summary>
    private const double SameLine = 0.35;

    /// <summary>The text lines of every page, top to bottom; lines without visible text are left out.</summary>
    public static List<TextLine> Read(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        var lines = new List<TextLine>();
        foreach (var page in document.GetPages())
            lines.AddRange(Read(page, page.Number - 1));
        return lines;
    }

    /// <summary>The text lines of one page, top to bottom.</summary>
    public static List<TextLine> Read(Page page, int index)
    {
        var height = page.Height;
        var letters = page.Letters
            .Select((letter, order) => (Letter: letter, Order: order, Baseline: height - letter.StartBaseLine.Y))
            .Where(l => l.Letter.TextOrientation == TextOrientation.Horizontal && !string.IsNullOrWhiteSpace(l.Letter.Value))
            .OrderBy(l => l.Baseline)
            .ToList();
        var lines = new List<TextLine>();
        var group = new List<(Letter Letter, int Order, double Baseline)>();
        void Flush()
        {
            if (group.Count == 0)
                return;
            var baselines = group.Select(l => l.Baseline).Order().ToList();
            var line = new TextLine(index, baselines[baselines.Count / 2], group.Max(l => Size(l.Letter)),
                group.OrderBy(l => l.Order).Select(l => l.Letter).ToList());
            if (line.Key.Length > 0)
                lines.Add(line);
            group.Clear();
        }
        foreach (var letter in letters)
        {
            if (group.Count > 0)
            {
                var size = Math.Max(Size(letter.Letter), group.Max(l => Size(l.Letter)));
                if (letter.Baseline - group[0].Baseline > SameLine * size)
                    Flush();
            }
            group.Add(letter);
        }
        Flush();
        return lines;
    }

    /// <summary>The text of every page in content order, each /ActualText span replacing its glyphs.</summary>
    public static string Text(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return string.Concat(document.GetPages().Select(Text));
    }

    /// <summary>The text of one page in content order, each /ActualText span replacing its glyphs.</summary>
    public static string Text(Page page)
    {
        var result = new StringBuilder();
        var spans = new Dictionary<Letter, MarkedContentElement>(ReferenceEqualityComparer.Instance);
        void Walk(IEnumerable<MarkedContentElement> elements)
        {
            foreach (var element in elements)
            {
                if (element.ActualText is null)
                {
                    Walk(element.Children);
                    continue;
                }
                foreach (var letter in All(element))
                    spans.TryAdd(letter, element);
            }
        }
        Walk(page.GetMarkedContents());
        var done = new HashSet<MarkedContentElement>(ReferenceEqualityComparer.Instance);
        foreach (var letter in page.Letters)
        {
            if (!spans.TryGetValue(letter, out var span))
                result.Append(letter.Value);
            else if (done.Add(span))
                result.Append(span.ActualText);
        }
        return result.ToString();

        static IEnumerable<Letter> All(MarkedContentElement element) => element.Letters.Concat(element.Children.SelectMany(All));
    }

    /// <summary>The letter's size on the page in points (font size times the text matrix scale).</summary>
    public static double Size(Letter letter) => letter.PointSize > 0 ? letter.PointSize : letter.FontSize;

    /// <summary>Text without white space, in Unicode normalisation form C.</summary>
    public static string Normalise(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormC))
            if (!char.IsWhiteSpace(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format)
                builder.Append(c);
        return builder.ToString();
    }
}
