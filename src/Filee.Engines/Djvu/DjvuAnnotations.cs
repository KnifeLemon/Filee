// A DjVu document's bookmarks and its pages' hyperlinks (DjVu v3 specification, "Document Outline Chunk: NAVM" and
// "Annotation Chunk: ANTa, ANTz").
//
// NAVM (BZZ-compressed): the number of bookmarks, then each bookmark depth first — its number of children, its title
// and its URL, each a 24-bit length and UTF-8 text. ANTa (ANTz BZZ-compressed) is text of parenthesised expressions;
// a (maparea url comment area …) gives a link: url a string or (url "href" "target"), area (rect x y w h),
// (oval …), (text …) or (poly x0 y0 x1 y1 …), in page pixels from the bottom-left corner. Lines and the other
// expressions (zoom, background colour, metadata, highlighting) aren't links and are skipped.
//
// A URL starting with '#' points into the document (DjvuDocument resolves it); other URLs are kept only when they are
// http, https, ftp or mailto addresses, so a link can't run a script or open a local file (javascript:, file:, …).

using System.Text;
using Filee.Engines.Documents;

namespace Filee.Engines.Djvu;

/// <summary>A bookmark of the document outline.</summary>
internal sealed record DjvuBookmark(string Title, string Url, List<DjvuBookmark> Children);

/// <summary>
/// A link area on a page, in page pixels from the bottom-left corner, and where it points: its URL, and the page it
/// leads to (from 0) when it points into the document.
/// </summary>
internal sealed record DjvuLink(int Left, int Bottom, int Width, int Height, string Url, int Page = -1);

internal static class DjvuAnnotations
{
    /// <summary>Nesting of an annotation expression past which it is damage.</summary>
    private const int MaxDepth = 64;

    /// <summary>The bookmarks of a (decompressed) NAVM chunk; a cut-short chunk keeps the bookmarks it holds.</summary>
    public static List<DjvuBookmark> ReadOutline(byte[] data)
    {
        var roots = new List<DjvuBookmark>();
        var reader = new DjvuBytes(data);
        try
        {
            var count = reader.U16();
            // The parents still waiting for children, with how many each still has.
            var parents = new List<(List<DjvuBookmark> Children, int Left)>();
            for (var i = 0; i < count; i++)
            {
                var children = reader.U8();
                var title = Text(reader.Bytes(reader.U24()));
                var url = Text(reader.Bytes(reader.U24()));
                var bookmark = new DjvuBookmark(title, url, []);
                if (parents.Count == 0)
                {
                    roots.Add(bookmark);
                }
                else
                {
                    var (siblings, left) = parents[^1];
                    siblings.Add(bookmark);
                    if (left == 1)
                        parents.RemoveAt(parents.Count - 1);
                    else
                        parents[^1] = (siblings, left - 1);
                }
                if (children > 0)
                    parents.Add((bookmark.Children, children));
            }
        }
        catch (InvalidDataException)
        {
            // Cut short: the bookmarks read so far.
        }
        return roots;
    }

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes).Trim('\0').Trim();

    /// <summary>Whether <paramref name="url"/> may be a link out of the document: an http, https, ftp or mailto address.</summary>
    public static bool IsWebAddress(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFtp || uri.Scheme == Uri.UriSchemeMailto) &&
        !url.Any(char.IsControl);

    /// <summary>
    /// The link areas of an annotation text, with their URLs as written (DjvuDocument keeps the ones it can follow).
    /// Damaged text (unbalanced, nested too deep) gives no links; more than the limit's expressions is refused.
    /// </summary>
    public static List<DjvuLink> ReadLinks(string text, DjvuLimits limits)
    {
        var links = new List<DjvuLink>();
        try
        {
            foreach (var expression in Parse(text, limits))
            {
                if (Link(expression) is { } link)
                    links.Add(link);
            }
        }
        catch (InvalidDataException)
        {
            // Damaged annotations leave the page without links; the page itself is still drawn.
        }
        return links;
    }

    /// <summary>A (maparea …) expression as a link, or null when it isn't one Filee draws as a link.</summary>
    private static DjvuLink? Link(List<object> expression)
    {
        if (expression is not ["maparea", var target, _, List<object> area, ..])
            return null;
        var url = target switch
        {
            string s => s,
            List<object> { Count: >= 2 } list when list[0] is "url" && list[1] is string href => href,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(url))
            return null;
        var numbers = new List<int>();
        for (var i = 1; i < area.Count; i++)
        {
            if (area[i] is not string value || !int.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var number))
                return null;
            numbers.Add(number);
        }
        switch (area[0])
        {
            case "rect" or "oval" or "text" when numbers.Count == 4 && numbers[2] > 0 && numbers[3] > 0:
                return new DjvuLink(numbers[0], numbers[1], numbers[2], numbers[3], url.Trim());
            case "poly" when numbers.Count >= 6 && numbers.Count % 2 == 0:
                {
                    int left = int.MaxValue, bottom = int.MaxValue, right = int.MinValue, top = int.MinValue;
                    for (var i = 0; i < numbers.Count; i += 2)
                    {
                        left = Math.Min(left, numbers[i]);
                        right = Math.Max(right, numbers[i]);
                        bottom = Math.Min(bottom, numbers[i + 1]);
                        top = Math.Max(top, numbers[i + 1]);
                    }
                    return right > left && top > bottom ? new DjvuLink(left, bottom, right - left, top - bottom, url.Trim()) : null;
                }
            default:
                return null;
        }
    }

    /// <summary>The top-level expressions of an annotation text: lists of atoms (strings) and lists.</summary>
    internal static List<List<object>> Parse(string text, DjvuLimits limits)
    {
        var expressions = new List<List<object>>();
        var open = new List<List<object>>();
        var nodes = 0L;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (++nodes > limits.MaxAnnotationNodes)
                throw DocumentLimits.Exceeded($"A DjVu page's annotations have more than {DocumentLimits.Count(limits.MaxAnnotationNodes)} parts.");
            switch (c)
            {
                case '(':
                    {
                        if (open.Count >= MaxDepth)
                            throw new InvalidDataException("DjVu annotations are nested too deep.");
                        var list = new List<object>();
                        if (open.Count > 0)
                            open[^1].Add(list);
                        open.Add(list);
                        i++;
                        break;
                    }
                case ')':
                    if (open.Count == 0)
                        throw new InvalidDataException("DjVu annotations close more than they open.");
                    if (open.Count == 1)
                        expressions.Add(open[0]);
                    open.RemoveAt(open.Count - 1);
                    i++;
                    break;
                case '"':
                    {
                        var value = QuotedString(text, ref i);
                        if (open.Count > 0)
                            open[^1].Add(value);
                        break;
                    }
                default:
                    {
                        var start = i;
                        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('(' or ')' or '"'))
                            i++;
                        if (open.Count > 0)
                            open[^1].Add(text[start..i]);
                        break;
                    }
            }
        }
        // An expression left open at the end is dropped; the ones before it stand.
        return expressions;
    }

    /// <summary>A string in double quotes from <paramref name="i"/> (at the quote), with C escapes.</summary>
    private static string QuotedString(string text, ref int i)
    {
        var value = new StringBuilder();
        i++;
        while (i < text.Length && text[i] != '"')
        {
            var c = text[i++];
            if (c != '\\' || i >= text.Length)
            {
                value.Append(c);
                continue;
            }
            c = text[i++];
            if (c is >= '0' and <= '7')
            {
                var code = c - '0';
                for (var n = 0; n < 2 && i < text.Length && text[i] is >= '0' and <= '7'; n++)
                    code = code * 8 + (text[i++] - '0');
                value.Append((char)code);
                continue;
            }
            value.Append(c switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                'b' => '\b',
                'f' => '\f',
                'v' => '\v',
                'a' => '\a',
                _ => c,
            });
        }
        if (i >= text.Length)
            throw new InvalidDataException("A DjVu annotation string isn't closed.");
        i++;
        return value.ToString();
    }
}
