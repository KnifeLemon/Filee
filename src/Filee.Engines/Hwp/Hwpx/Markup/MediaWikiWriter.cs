// HDocument → MediaWiki markup (what MediaWikiReader reads back): = headings =, paragraphs (line breaks as <br />),
// <blockquote>, * / # lists nested by repeating the markers (": " for an item's further paragraphs), <pre> code,
// {| class="wikitable" tables with ! header cells and colspan / rowspan, '''bold''' ''italic'' and the <s> <u> <sup>
// <sub> <code> tags, [external links] and [[relative links|text]], [[File:…]] pictures and <ref> footnotes with
// <references /> at the end. Text that would read as markup is written inside <nowiki>. The wiki has no title.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Filee.Engines.Hwp.Hwpx.Markup;

/// <summary>Writes the document model as MediaWiki markup.</summary>
internal sealed partial class MediaWikiWriter(Func<string, string?> pictures) : MarkupTextWriter(pictures)
{
    public static string Write(HDocument document, Func<string, string?> pictures) => new MediaWikiWriter(pictures).Write(document);

    protected override void Start(StringBuilder sb, string? title)
    {
    }

    protected override void End(StringBuilder sb)
    {
        if (Notes.Count > 0)
            sb.Append("<references />\n");
    }

    protected override void Heading(StringBuilder sb, int level, string text)
    {
        var marks = new string('=', Math.Min(level, 6));
        sb.Append(marks).Append(' ').Append(text).Append(' ').Append(marks).Append("\n\n");
    }

    protected override void Paragraph(StringBuilder sb, string text, int quote)
    {
        text = LineStart(text).Replace("\n", "<br />\n", StringComparison.Ordinal);
        if (quote > 0)
            sb.Append("<blockquote>").Append(text).Append("</blockquote>\n\n");
        else
            sb.Append(text).Append("\n\n");
    }

    protected override void List(StringBuilder sb, IReadOnlyList<MarkupListItem> items, int quote)
    {
        var types = new char[9];
        foreach (var item in items)
        {
            if (!item.Continuation)
            {
                types[item.Level] = item.Ordered ? '#' : '*';
                for (var l = 0; l < item.Level; l++)
                    if (types[l] == '\0')
                        types[l] = '*';
            }
            var prefix = new string(types, 0, item.Level + 1);
            if (item.Continuation)
                prefix += ":";
            sb.Append(prefix).Append(' ').Append(item.Text.Replace("\n", "<br />", StringComparison.Ordinal)).Append('\n');
        }
        sb.Append('\n');
    }

    protected override void Code(StringBuilder sb, IReadOnlyList<string> lines, int quote) =>
        sb.Append("<pre>").AppendJoin('\n', lines.Select(Entities)).Append("</pre>\n\n");

    protected override void Table(StringBuilder sb, MarkupTable table)
    {
        sb.Append("{| class=\"wikitable\"\n");
        for (var r = 0; r < table.Rows.Count; r++)
        {
            if (r > 0)
                sb.Append("|-\n");
            foreach (var cell in table.Rows[r])
            {
                if (cell is null)
                    continue;
                sb.Append(table.HeaderRows[r] ? '!' : '|');
                if (cell.ColSpan > 1)
                    sb.Append(CultureInfo.InvariantCulture, $" colspan=\"{cell.ColSpan}\"");
                if (cell.RowSpan > 1)
                    sb.Append(CultureInfo.InvariantCulture, $" rowspan=\"{cell.RowSpan}\"");
                if (cell.ColSpan > 1 || cell.RowSpan > 1)
                    sb.Append(" |");
                if (!cell.Simple)
                {
                    // Tables, lists and code in a cell: its blocks on the lines after it.
                    var blocks = new StringBuilder();
                    Blocks(cell.Source.Blocks, blocks);
                    sb.Append('\n').Append(blocks.ToString().Trim('\n')).Append('\n');
                    continue;
                }
                var text = cell.Text.Replace("\n", "<br />", StringComparison.Ordinal);
                // A cell's text starting with markup of a line start would be read as that.
                sb.Append(' ').Append(LineStart(text)).Append('\n');
            }
        }
        sb.Append("|}\n\n");
    }

    protected override void Rule(StringBuilder sb) => sb.Append("----\n\n");

    protected override string Run(string text, HCharFormat format)
    {
        if (format.Shade == HtmlReader.CodeShade)
            return Around(text, core => $"<code>{Escape(core)}</code>");
        return Around(Escape(text), core =>
        {
            if (format.Superscript is true)
                core = $"<sup>{core}</sup>";
            else if (format.Subscript is true)
                core = $"<sub>{core}</sub>";
            if (format.Underline is true)
                core = $"<u>{core}</u>";
            if (format.Strike is true)
                core = $"<s>{core}</s>";
            if (format.Italic is true)
                core = $"''{core}''";
            if (format.Bold is true)
                core = $"'''{core}'''";
            return core;
        });
    }

    protected override string Link(string target, string content)
    {
        if (ExternalScheme().IsMatch(target))
            return $"[{target.Replace(" ", "%20", StringComparison.Ordinal).Replace("]", "%5D", StringComparison.Ordinal)} {content}]";
        return $"[[{target.Replace("|", "%7C", StringComparison.Ordinal).Replace("]", "%5D", StringComparison.Ordinal)}|{content}]]";
    }

    protected override string Picture(string link) => $"[[File:{link.Replace("|", "%7C", StringComparison.Ordinal).Replace("]", "%5D", StringComparison.Ordinal)}]]";

    protected override string LineBreak() => "\n";

    protected override string NoteReference(int number) => $"<ref>{Notes[number - 1]}</ref>";

    // ───────────────────────── Escaping ─────────────────────────

    private static string Entities(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal);

    /// <summary>
    /// Text with what the wiki would read as markup written as entities (which the wiki shows as the characters):
    /// apostrophes next to apostrophes, brackets, braces, bars, &amp; and &lt;, tildes in threes, double underscores
    /// (magic words) and the colon of a URL (which would become a link).
    /// </summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var before = i > 0 ? text[i - 1] : '\0';
            var after = i + 1 < text.Length ? text[i + 1] : '\0';
            var escape = ch switch
            {
                '&' or '<' or '[' or ']' or '{' or '}' or '|' => true,
                '\'' => before == '\'' || after == '\'',
                '~' => before == '~' || after == '~',
                '_' => before == '_' || after == '_',
                ':' => after == '/' && i + 2 < text.Length && text[i + 2] == '/',
                _ => false,
            };
            if (escape)
                sb.Append(CultureInfo.InvariantCulture, $"&#{(int)ch};");
            else
                sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>A line whose start would read as a list, heading, rule or preformatted text gets that character as an entity.</summary>
    private static string LineStart(string text) =>
        string.Join('\n', text.Split('\n').Select(line => line.Length > 0 && (line[0] is '*' or '#' or ':' or ';' or ' ' or '=' or '!' || line.StartsWith("----", StringComparison.Ordinal))
            ? $"&#{(int)line[0]};{line[1..]}"
            : line));

    [GeneratedRegex(@"^(?:https?|ftp|ftps|mailto|news|irc|ircs|gopher|telnet|sftp|ssh|git|svn|tel|sms):", RegexOptions.IgnoreCase)]
    private static partial Regex ExternalScheme();
}
