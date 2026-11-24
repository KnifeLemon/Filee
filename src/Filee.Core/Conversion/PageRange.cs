// Parses user page selections such as "1-3, 5, 8-" into zero-based page indices.

namespace Filee.Core.Conversion;

/// <summary>Page range parsing.</summary>
public static class PageRange
{
    /// <summary>
    /// Parses a 1-based page range expression. Empty or whitespace means every page.
    /// Supported forms: <c>"3"</c>, <c>"2-5"</c>, <c>"7-"</c> (to the end), <c>"-4"</c> (from the start), comma separated.
    /// Invalid parts are ignored; out-of-range pages are clamped away.
    /// </summary>
    /// <returns>Sorted, distinct zero-based page indices.</returns>
    public static IReadOnlyList<int> Parse(string? expression, int pageCount)
    {
        if (pageCount <= 0)
            return [];
        if (string.IsNullOrWhiteSpace(expression))
            return Enumerable.Range(0, pageCount).ToList();

        var pages = new SortedSet<int>();
        foreach (var raw in expression.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = raw.IndexOf('-');
            int start, end;
            if (dash < 0)
            {
                if (!int.TryParse(raw, out start))
                    continue;
                end = start;
            }
            else
            {
                var left = raw[..dash].Trim();
                var right = raw[(dash + 1)..].Trim();
                start = left.Length == 0 ? 1 : int.TryParse(left, out var l) ? l : -1;
                end = right.Length == 0 ? pageCount : int.TryParse(right, out var r) ? r : -1;
                if (start < 0 || end < 0)
                    continue;
            }

            if (start > end)
                (start, end) = (end, start);
            for (var p = Math.Max(1, start); p <= Math.Min(pageCount, end); p++)
                pages.Add(p - 1);
        }

        return pages.Count == 0 ? Enumerable.Range(0, pageCount).ToList() : pages.ToList();
    }

    /// <summary>
    /// Checks a range typed by hand (the "Choose pages" window, <c>filee convert --pages</c>), which
    /// <see cref="Parse"/> would quietly make the best of: every part must be a page number or a span of them, from 1,
    /// and none past the last page when <paramref name="pageCount"/> is known. What a keyboard in another language
    /// types is read too: other digits (Devanagari, Arabic-Indic, Thai, full-width), en, em and full-width dashes,
    /// full-width, ideographic and Arabic commas. Empty means every page.
    /// </summary>
    public static PageRangeCheck Check(string? expression, int? pageCount)
    {
        var text = Normalize(expression ?? "").Trim();
        if (text.Length == 0)
            return new(PageRangeProblem.None, "", pageCount);

        var last = 0;
        foreach (var raw in text.Split(','))
        {
            var part = raw.Trim();
            if (part.Length == 0)
                continue;
            var dash = part.IndexOf('-');
            var left = dash < 0 ? part : part[..dash].Trim();
            var right = dash < 0 ? part : part[(dash + 1)..].Trim();
            if (dash >= 0 && left.Length == 0 && right.Length == 0)
                return new(PageRangeProblem.Invalid, text, null);
            if (!Page(left, out var start) || !Page(right, out var end))
                return new(PageRangeProblem.Invalid, text, null);
            last = Math.Max(last, Math.Max(start, end));
        }
        if (last == 0 && !text.Contains('-'))
            return new(PageRangeProblem.Invalid, text, null);
        if (pageCount is { } count && last > count)
            return new(PageRangeProblem.PastTheEnd, text, null);
        return new(PageRangeProblem.None, text, pageCount is { } total ? Parse(text, total).Count : null);

        // A side left empty ("7-", "-4") is open; anything else must be a whole number from 1.
        static bool Page(string side, out int page)
        {
            page = 0;
            if (side.Length == 0)
                return true;
            return side.All(char.IsAsciiDigit) && int.TryParse(side, System.Globalization.CultureInfo.InvariantCulture, out page) && page >= 1;
        }
    }

    /// <summary>The range in the digits, hyphens and commas <see cref="Parse"/> reads.</summary>
    private static string Normalize(string expression) =>
        string.Create(expression.Length, expression, (chars, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                chars[i] = c switch
                {
                    '\u2013' or '\u2014' or '\u2212' or '\uFF0D' => '-', // en, em, minus and full-width dashes
                    '\uFF0C' or '\u3001' or '\u060C' => ',', // full-width, ideographic and Arabic commas
                    _ when !char.IsAsciiDigit(c) && char.IsDigit(c) => (char)('0' + (int)char.GetNumericValue(c)),
                    _ => c,
                };
            }
        });
}

/// <summary>What is wrong with a range typed by hand (<see cref="PageRange.Check"/>).</summary>
public enum PageRangeProblem
{
    None,
    /// <summary>A part that is not a page number or a span of them ("abc", "3-x", "0").</summary>
    Invalid,
    /// <summary>A page after the last one of the PDF.</summary>
    PastTheEnd,
}

/// <summary>
/// The result of <see cref="PageRange.Check"/>: <see cref="Text"/> is the range as it is used (dashes made hyphens,
/// trimmed); <see cref="Pages"/> how many pages it takes, when the page count is known and the range is fine.
/// </summary>
public readonly record struct PageRangeCheck(PageRangeProblem Problem, string Text, int? Pages)
{
    public bool IsValid => Problem == PageRangeProblem.None;
}
