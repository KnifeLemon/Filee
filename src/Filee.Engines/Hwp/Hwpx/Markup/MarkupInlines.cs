// What the inline parsers of the text markup readers (Textile, MediaWiki, reStructuredText, Org) share: text gathered
// into runs of one format, and the search for a closing marker. The places a marker can close are found once per
// paragraph and looked up by binary search, so a paragraph of a million openers takes n log n time, not quadratic.

using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Filee.Engines.Hwp.Hwpx.Markup;

/// <summary>Collects inlines: text is joined into runs of one format.</summary>
internal sealed class InlineSink(List<HInline> output)
{
    private readonly StringBuilder _text = new();
    private HCharFormat _format;

    public List<HInline> Output { get; } = output;

    public void Text(string text, HCharFormat format)
    {
        if (text.Length == 0)
            return;
        if (_text.Length > 0 && format != _format)
            Flush();
        _format = format;
        _text.Append(text);
    }

    public void Text(char ch, HCharFormat format)
    {
        if (_text.Length > 0 && format != _format)
            Flush();
        _format = format;
        _text.Append(ch);
    }

    public void Add(HInline inline)
    {
        Flush();
        Output.Add(inline);
    }

    public void Flush()
    {
        if (_text.Length > 0)
            Output.Add(new HText(_text.ToString(), _format));
        _text.Clear();
    }
}

/// <summary>Finds closing markers in one paragraph's text.</summary>
internal sealed partial class Closers(string text)
{
    private readonly Dictionary<string, List<int>> _closing = new(StringComparer.Ordinal);

    public string Text { get; } = text;

    /// <summary>
    /// The first position at or after <paramref name="from"/> where <paramref name="marker"/> stands, ends by
    /// <paramref name="end"/> and <paramref name="closes"/> accepts it, or -1. <paramref name="closes"/> may look only
    /// at the text around the position (it is asked once per position and marker, for every caller).
    /// </summary>
    public int Find(string marker, int from, int end, Func<int, bool>? closes = null)
    {
        if (!_closing.TryGetValue(marker, out var positions))
        {
            positions = [];
            for (var at = Text.IndexOf(marker, StringComparison.Ordinal); at >= 0; at = Text.IndexOf(marker, at + 1, StringComparison.Ordinal))
                if (closes?.Invoke(at) ?? true)
                    positions.Add(at);
            _closing[marker] = positions;
        }
        var index = positions.BinarySearch(from);
        if (index < 0)
            index = ~index;
        return index < positions.Count && positions[index] + marker.Length <= end ? positions[index] : -1;
    }

    /// <summary>True when the character at <paramref name="index"/> is a letter or digit (outside the text: false).</summary>
    public bool Word(int index) => index >= 0 && index < Text.Length && char.IsLetterOrDigit(Text[index]);

    /// <summary>True when the character at <paramref name="index"/> is white space (outside the text: true).</summary>
    public bool Space(int index) => index < 0 || index >= Text.Length || char.IsWhiteSpace(Text[index]);

    /// <summary>An HTML entity (&amp;amp;, &amp;#42;, &amp;#x2A;) at <paramref name="index"/>: its text and length.</summary>
    public static (string Text, int Length)? Entity(string text, int index)
    {
        var match = EntityPattern().Match(text, index);
        if (!match.Success || match.Index != index)
            return null;
        var decoded = WebUtility.HtmlDecode(match.Value);
        return decoded == match.Value ? null : (decoded, match.Length);
    }

    [GeneratedRegex(@"\G&(?:#[0-9]{1,7}|#[xX][0-9a-fA-F]{1,6}|[A-Za-z][A-Za-z0-9]{1,31});")]
    private static partial Regex EntityPattern();
}
