// HDocument → reStructuredText (what docutils and RstReader read): the title over- and underlined with "=", section
// titles underlined = - ~ ^ " ' ` : . by level (as wide as the title, East Asian characters counting two columns),
// paragraphs (line blocks "| " when they have line breaks), block quotes indented, - / 1. lists with their nested
// lists and further paragraphs indented under the item, "::" literal blocks, grid tables with column and row spans,
// transitions, **strong** *emphasis* ``literals`` :sub: :sup:, `links <url>`__, pictures as |imageN| substitutions
// and [#]_ footnotes defined at the end. reST has no underline or strike-through: that text stays plain, and in text
// that is both bold and italic, bold wins. Markup characters in text are escaped with a backslash; inline markup next
// to a letter is set off with "\ " (an escaped space, which is nothing).

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Filee.Engines.Hwp.Hwpx.Markup;

/// <summary>Writes the document model as reStructuredText.</summary>
internal sealed partial class RstWriter(Func<string, string?> pictures) : MarkupTextWriter(pictures)
{
    /// <summary>Stands between inline markup and its neighbour until <see cref="Finish"/> decides whether "\ " is needed.</summary>
    private const char Edge = '\u0001';

    private static readonly char[] Underlines = ['=', '-', '~', '^', '"', '\'', '`', ':', '.'];

    private readonly List<string> _images = [];

    public static string Write(HDocument document, Func<string, string?> pictures) => new RstWriter(pictures).Write(document);

    protected override void Start(StringBuilder sb, string? title)
    {
        if (title is null)
            return;
        var text = Finish(Escape(title.Replace('\n', ' ')));
        var rule = new string('=', Width(text));
        sb.Append(rule).Append('\n').Append(text).Append('\n').Append(rule).Append("\n\n");
    }

    protected override void End(StringBuilder sb)
    {
        for (var i = 0; i < Notes.Count; i++)
            sb.Append(".. [#] ").Append(Finish(Notes[i])).Append("\n\n");
        for (var i = 0; i < _images.Count; i++)
            sb.Append(CultureInfo.InvariantCulture, $".. |image{i + 1}| image:: ").Append(_images[i]).Append('\n');
    }

    protected override void Heading(StringBuilder sb, int level, string text)
    {
        text = Finish(text);
        if (text.Length == 0)
            return;
        sb.Append(text).Append('\n').Append(Underlines[Math.Clamp(level, 1, 9) - 1], Math.Max(Width(text), 2)).Append("\n\n");
    }

    protected override void Paragraph(StringBuilder sb, string text, int quote)
    {
        var indent = new string(' ', quote * 4);
        sb.Append(Body(Finish(text), indent)).Append("\n\n");
    }

    /// <summary>
    /// A paragraph's text at an indent: a line block when it has line breaks, the start and end escaped where they
    /// would read as other markup.
    /// </summary>
    private static string Body(string text, string indent)
    {
        var lines = text.Split('\n');
        if (lines.Length > 1)
            return string.Join('\n', lines.Select(l => indent + "| " + l));
        return indent + Guard(text);
    }

    /// <summary>A text that would read as a list, directive, table, field or transition, or end in "::", is escaped.</summary>
    private static string Guard(string text)
    {
        if (BlockLike().IsMatch(text) || (text.Length > 0 && text.All(c => char.IsPunctuation(c) || char.IsSymbol(c))))
            text = "\\" + text;
        if (text.EndsWith("::", StringComparison.Ordinal))
            text = text[..^1] + "\\:";
        return text;
    }

    protected override void List(StringBuilder sb, IReadOnlyList<MarkupListItem> items, int quote)
    {
        var markers = new int[9];
        for (var k = 0; k < items.Count; k++)
        {
            var item = items[k];
            var indent = new string(' ', Indent(items, k, markers));
            if (item.Continuation)
            {
                sb.Append(Body(Finish(item.Text), indent)).Append("\n\n");
                continue;
            }
            var marker = item.Ordered ? item.Number.ToString(CultureInfo.InvariantCulture) + ". " : "- ";
            markers[item.Level] = marker.Length;
            var text = Finish(item.Text);
            var lines = text.Split('\n');
            // A line block can't start on the marker's line: its lines go under it.
            var body = lines.Length > 1
                ? "\n\n" + string.Join('\n', lines.Select(l => indent + new string(' ', marker.Length) + "| " + l))
                : Guard(text);
            sb.Append(indent).Append(marker).Append(body.Length == 0 ? "\\ " : body).Append("\n\n");
        }
    }

    /// <summary>The column an item's marker starts at: the text column of the items above it.</summary>
    private static int Indent(IReadOnlyList<MarkupListItem> items, int k, int[] markers)
    {
        var item = items[k];
        var column = 0;
        for (var l = 0; l < item.Level; l++)
            column += markers[l] == 0 ? 2 : markers[l];
        if (item.Continuation)
            column += markers[item.Level] == 0 ? 2 : markers[item.Level];
        return column;
    }

    protected override void Code(StringBuilder sb, IReadOnlyList<string> lines, int quote)
    {
        var indent = new string(' ', quote * 4);
        sb.Append(indent).Append("::\n\n");
        foreach (var line in lines)
            sb.Append(line.Length == 0 ? "" : indent + "    " + line).Append('\n');
        sb.Append('\n');
    }

    protected override void Table(StringBuilder sb, MarkupTable table)
    {
        var columns = table.Columns;
        var rows = table.Rows.Count;
        var texts = new string?[rows, columns];
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < columns; c++)
                texts[r, c] = table.Rows[r][c] is { } cell ? Finish(cell.Text.Replace("\n", " ", StringComparison.Ordinal)) : null;

        // Column widths: what single cells need; a spanning cell widens its last column if it needs more.
        var widths = new int[columns];
        for (var c = 0; c < columns; c++)
            widths[c] = 3;
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < columns; c++)
                if (table.Rows[r][c] is { ColSpan: 1 } && texts[r, c] is { } text)
                    widths[c] = Math.Max(widths[c], Width(text) + 2);
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                if (table.Rows[r][c] is not { ColSpan: > 1 } cell || texts[r, c] is not { } text)
                    continue;
                var span = Enumerable.Range(c, cell.ColSpan).Sum(x => widths[x]) + cell.ColSpan - 1;
                widths[c + cell.ColSpan - 1] += Math.Max(0, Width(text) + 2 - span);
            }
        }

        // Which cell covers each position.
        var owner = new (int Row, int Column)[rows, columns];
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < columns; c++)
                if (table.Rows[r][c] is { } cell)
                    for (var rr = r; rr < Math.Min(rows, r + cell.RowSpan); rr++)
                        for (var cc = c; cc < Math.Min(columns, c + cell.ColSpan); cc++)
                            owner[rr, cc] = (r, c);

        var header = 0;
        while (header < rows && table.HeaderRows[header])
            header++;
        if (header == rows)
            header = 0;

        // The border above row r (r == rows: the bottom one): "-" where a cell ends, blank where it goes on into row r,
        // "|" where the line between two cells that go on passes, "+" where lines meet.
        string Border(int r)
        {
            bool GoesOn(int c) => r > 0 && r < rows && owner[r, c] == owner[r - 1, c];
            var line = new StringBuilder();
            line.Append(GoesOn(0) ? '|' : '+');
            for (var c = 0; c < columns; c++)
            {
                line.Append(GoesOn(c) ? ' ' : r == header && header > 0 ? '=' : '-', widths[c]);
                if (c + 1 == columns)
                    line.Append(GoesOn(c) ? '|' : '+');
                else if (GoesOn(c) && GoesOn(c + 1))
                    line.Append(owner[r, c] == owner[r, c + 1] ? ' ' : '|');
                else
                    line.Append('+');
            }
            return line.ToString();
        }

        for (var r = 0; r < rows; r++)
        {
            sb.Append(Border(r)).Append('\n');
            var line = new StringBuilder("|");
            for (var c = 0; c < columns;)
            {
                var (or, oc) = owner[r, c];
                var cell = table.Rows[or][oc]!;
                var span = Math.Min(cell.ColSpan, columns - c);
                var width = Enumerable.Range(c, span).Sum(x => widths[x]) + span - 1;
                // A cell's text stands in its first row; the rows it spans below are blank.
                var text = or == r ? texts[or, oc] ?? "" : "";
                line.Append(' ').Append(text).Append(' ', Math.Max(0, width - 1 - Width(text))).Append('|');
                c += span;
            }
            sb.Append(line).Append('\n');
        }
        sb.Append(Border(rows)).Append("\n\n");
    }

    protected override void Rule(StringBuilder sb) => sb.Append("----\n\n");

    protected override string Run(string text, HCharFormat format)
    {
        if (format.Shade == HtmlReader.CodeShade)
            return Around(text, core => core.Contains("``", StringComparison.Ordinal) ? Escape(core) : $"{Edge}``{core}``{Edge}");
        return Around(Escape(text), core =>
        {
            if (format.Superscript is true)
                return $"{Edge}:sup:`{core.Replace("`", "\\`", StringComparison.Ordinal)}`{Edge}";
            if (format.Subscript is true)
                return $"{Edge}:sub:`{core.Replace("`", "\\`", StringComparison.Ordinal)}`{Edge}";
            if (format.Bold is true)
                return $"{Edge}**{core}**{Edge}";
            if (format.Italic is true)
                return $"{Edge}*{core}*{Edge}";
            return core;
        });
    }

    protected override string Link(string target, string content)
    {
        // Links hold text only: the markup of their content is taken off.
        var text = Strip().Replace(content, "").Replace("<", "\\<", StringComparison.Ordinal);
        return $"{Edge}`{text} <{target.Replace(" ", "%20", StringComparison.Ordinal).Replace(">", "%3E", StringComparison.Ordinal).Replace("`", "%60", StringComparison.Ordinal)}>`__{Edge}";
    }

    protected override string Picture(string link)
    {
        _images.Add(link);
        return $"{Edge}|image{_images.Count}|{Edge}";
    }

    protected override string LineBreak() => "\n";

    protected override string NoteReference(int number) => $"{Edge}[#]_{Edge}";

    // ───────────────────────── Escaping ─────────────────────────

    /// <summary>The characters that open inline markup, backslash-escaped.</summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 4);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var after = i + 1 < text.Length ? text[i + 1] : ' ';
            var before = i > 0 ? text[i - 1] : ' ';
            var escape = ch switch
            {
                '\\' or '*' or '`' or '|' => true,
                // name_ is a reference, _`x` a target, [1]_ a footnote.
                '_' => !char.IsLetterOrDigit(after) || before == ']' || !char.IsLetterOrDigit(before),
                '[' => true,
                ':' => after == '`' || (char.IsLetter(after) && RoleAhead().IsMatch(text[i..])),
                _ => false,
            };
            if (escape)
                sb.Append('\\');
            sb.Append(ch);
        }
        // A URL would become a link.
        return sb.Replace("://", "\\://").ToString();
    }

    /// <summary>
    /// Takes the markers that set markup off: "\ " where the markup touches a letter or digit (which would keep it
    /// from being markup), nothing where a space or punctuation is next to it.
    /// </summary>
    private static string Finish(string text)
    {
        if (!text.Contains(Edge, StringComparison.Ordinal))
            return text;
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != Edge)
            {
                sb.Append(text[i]);
                continue;
            }
            // What stands on either side: markup may start only after a space or some punctuation, and end only before.
            var left = sb.Length > 0 ? sb[^1] : ' ';
            var j = i + 1;
            while (j < text.Length && text[j] == Edge)
                j++;
            var right = j < text.Length ? text[j] : ' ';
            if ((IsMarkupStart(right) && !OkBefore(left)) || (IsMarkupEnd(left) && !OkAfter(right)))
                sb.Append("\\ ");
            i = j - 1;
        }
        return sb.ToString();
    }

    private static bool IsMarkupEnd(char ch) => ch is '*' or '`' or '_' or '|';

    private static bool IsMarkupStart(char ch) => ch is '*' or '`' or ':' or '|' or '[';

    private static bool OkAfter(char ch) => char.IsWhiteSpace(ch) || ch is '-' or '.' or ',' or ':' or ';' or '!' or '?' or '/' or '\'' or '"' or ')' or ']' or '}' or '>';

    private static bool OkBefore(char ch) => char.IsWhiteSpace(ch) || ch is '-' or ':' or '/' or '\'' or '"' or '<' or '(' or '[' or '{';

    /// <summary>Display columns: East Asian wide and full-width characters count two, as docutils measures titles.</summary>
    private static int Width(string text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var v = rune.Value;
            width += v is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF
                or >= 0xFE30 and <= 0xFE4F or >= 0xFF00 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 or >= 0x20000 and <= 0x3FFFD or >= 0x1F300 and <= 0x1FAFF
                ? 2
                : 1;
        }
        return width;
    }

    [GeneratedRegex(@"^(?:[-*+•‣⁃](?:\s|$)|\(?(?:\d+|#|[A-Za-z]|[ivxlcdmIVXLCDM]+)[.)](?:\s|$)|\.\.(?:\s|$)|\||:[^:\s][^:]*:(?:\s|$)|>>>|\+[-=])")]
    private static partial Regex BlockLike();

    [GeneratedRegex(@"^:[A-Za-z][\w.+-]*:`")]
    private static partial Regex RoleAhead();

    [GeneratedRegex("[\u0001]|\\\\ |``|\\*\\*|(?<!\\\\)\\*")]
    private static partial Regex Strip();
}
