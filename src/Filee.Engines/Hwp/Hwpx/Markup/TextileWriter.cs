// HDocument → Textile (the dialect TextileReader reads back): hN. headings, paragraphs (a line break is a new line),
// bq. quotes, bc. code (bc.. when it has blank lines, closed by an empty comment block), * / # lists nested by
// repeating the markers, tables with |_. header cells and \N / /N spans, *strong* _em_ -deleted- +inserted+ ^sup^
// ~sub~ @code@, ["links":url], !pictures! and [N] footnotes with fnN. at the end. Characters that would read as markup
// where they stand are written as HTML entities. Textile has no title: it is left out.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Filee.Engines.Hwp.Hwpx.Markup;

/// <summary>Writes the document model as Textile.</summary>
internal sealed partial class TextileWriter(Func<string, string?> pictures) : MarkupTextWriter(pictures)
{
    public static string Write(HDocument document, Func<string, string?> pictures) => new TextileWriter(pictures).Write(document);

    protected override void Start(StringBuilder sb, string? title)
    {
    }

    protected override void End(StringBuilder sb)
    {
        for (var i = 0; i < Notes.Count; i++)
            sb.Append(CultureInfo.InvariantCulture, $"fn{i + 1}. ").Append(Notes[i]).Append("\n\n");
    }

    protected override void Heading(StringBuilder sb, int level, string text) =>
        sb.Append(CultureInfo.InvariantCulture, $"h{Math.Min(level, 6)}. ").Append(text).Append("\n\n");

    protected override void Paragraph(StringBuilder sb, string text, int quote)
    {
        if (quote > 0)
            sb.Append("bq. ").Append(text).Append("\n\n");
        else
            sb.Append(BlockStart(text)).Append("\n\n");
    }

    protected override void List(StringBuilder sb, IReadOnlyList<MarkupListItem> items, int quote)
    {
        // The markers of the levels above an item: "#*" is a bullet inside a numbered item.
        var types = new char[9];
        foreach (var item in items)
        {
            if (item.Continuation)
            {
                sb.Append(Lines(item.Text)).Append('\n');
                continue;
            }
            types[item.Level] = item.Ordered ? '#' : '*';
            for (var l = 0; l < item.Level; l++)
                if (types[l] == '\0')
                    types[l] = '*';
            sb.Append(types, 0, item.Level + 1).Append(' ').Append(Lines(item.Text)).Append('\n');
        }
        sb.Append('\n');
    }

    /// <summary>The lines of an item after its first: a line that starts like a list item would be taken for one.</summary>
    private static string Lines(string text) =>
        string.Join('\n', text.Split('\n').Select((line, i) => i == 0 ? line : BlockStart(line)));

    protected override void Code(StringBuilder sb, IReadOnlyList<string> lines, int quote)
    {
        if (lines.Any(l => l.Trim().Length == 0))
        {
            // An extended block runs on until the next block signature: an empty comment ends it.
            sb.Append("bc.. ").AppendJoin('\n', lines.Select(Entities)).Append("\n\n###. \n\n");
            return;
        }
        sb.Append("bc. ").AppendJoin('\n', lines.Select(Entities)).Append("\n\n");
    }

    protected override void Table(StringBuilder sb, MarkupTable table)
    {
        for (var r = 0; r < table.Rows.Count; r++)
        {
            sb.Append('|');
            foreach (var cell in table.Rows[r])
            {
                if (cell is null)
                    continue;
                var prefix = new StringBuilder();
                if (table.HeaderRows[r])
                    prefix.Append('_');
                if (cell.ColSpan > 1)
                    prefix.Append(CultureInfo.InvariantCulture, $"\\{cell.ColSpan}");
                if (cell.RowSpan > 1)
                    prefix.Append(CultureInfo.InvariantCulture, $"/{cell.RowSpan}");
                if (prefix.Length > 0)
                    sb.Append(prefix).Append(". ");
                sb.Append(cell.Text.Replace("\n", "<br />", StringComparison.Ordinal)).Append('|');
            }
            sb.Append('\n');
        }
        sb.Append('\n');
    }

    protected override void Rule(StringBuilder sb) => sb.Append("<hr />\n\n");

    protected override string Run(string text, HCharFormat format)
    {
        if (format.Shade == HtmlReader.CodeShade)
            return Around(text, core => $"@{Entities(core).Replace("@", "&#64;", StringComparison.Ordinal)}@");
        return Around(Escape(text), core =>
        {
            if (format.Superscript is true)
                core = $"[^{core}^]";
            else if (format.Subscript is true)
                core = $"[~{core}~]";
            if (format.Underline is true)
                core = $"+{core}+";
            if (format.Strike is true)
                core = $"-{core}-";
            if (format.Italic is true)
                core = $"_{core}_";
            if (format.Bold is true)
                core = $"*{core}*";
            return core;
        });
    }

    protected override string Link(string target, string content) =>
        $"[\"{content.Replace("\"", "&quot;", StringComparison.Ordinal)}\":{target.Replace("]", "%5D", StringComparison.Ordinal).Replace(" ", "%20", StringComparison.Ordinal)}]";

    protected override string Picture(string link) => $"!{link.Replace("!", "%21", StringComparison.Ordinal).Replace(" ", "%20", StringComparison.Ordinal)}!";

    protected override string LineBreak() => "\n";

    protected override string NoteReference(int number) => $"[{number}]";

    // ───────────────────────── Escaping ─────────────────────────

    /// <summary>&amp;, &lt; and | as entities (they are markup anywhere).</summary>
    private static string Entities(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace("|", "&#124;", StringComparison.Ordinal);

    /// <summary>
    /// Characters that would open or close markup where they stand, as entities: an emphasis marker after a non-letter
    /// before a non-space, or after a non-space before a non-letter; "==" and "??"; a "!" before text; "[" before a
    /// digit or a quote; quotes when the text has a "\":" a link would start from.
    /// </summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        var quotes = text.Contains("\":", StringComparison.Ordinal);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var before = i > 0 ? text[i - 1] : ' ';
            var after = i + 1 < text.Length ? text[i + 1] : ' ';
            var escape = ch switch
            {
                '&' or '<' or '|' => true,
                '*' or '_' or '-' or '+' or '^' or '~' or '@' or '%' =>
                    (!char.IsLetterOrDigit(before) && !char.IsWhiteSpace(after)) || (!char.IsWhiteSpace(before) && !char.IsLetterOrDigit(after)),
                '=' or '?' => after == ch || before == ch,
                '!' => !char.IsWhiteSpace(after),
                '[' => char.IsAsciiDigit(after) || after == '"',
                '"' => quotes,
                _ => false,
            };
            if (escape)
                sb.Append(CultureInfo.InvariantCulture, $"&#{(int)ch};");
            else
                sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>A paragraph whose start reads like a block signature, list or table gets its first character escaped.</summary>
    private static string BlockStart(string text) =>
        StartsBlock().IsMatch(text) ? $"&#{(int)text[0]};{text[1..]}" : text;

    [GeneratedRegex(@"^(?:(?:h[1-6]|p|bq|bc|pre|notextile|###|fn\d+\^?|table)\S*?\.\.? |(?:h[1-6]|p|bq|bc|pre|notextile|###|table)\S*?\.\.?$|[*#]+\S*? |\||- .*:=)")]
    private static partial Regex StartsBlock();
}
