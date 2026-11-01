// HDocument → Org mode (what OrgReader reads back): #+TITLE, * headlines, paragraphs (\\ at a line break),
// #+BEGIN_QUOTE blocks (nested for deeper quotes), - / 1. lists indented by level, #+BEGIN_SRC code (lines starting
// with "*" or "#+" get Org's "," in front), | tables | with a |---+---| rule under the header rows (merged cells keep
// their text in the first one), ----- rules, *bold* /italic/ _underline_ +strike+ ~code~ ^{sup} _{sub},
// [[links][text]], [[file:pictures]] and [fn:N] footnotes defined at the end. A character that would read as markup
// where it stands gets a zero-width space before or after it, as Org's manual suggests.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Filee.Engines.Hwp.Hwpx.Markup;

/// <summary>Writes the document model as Org mode.</summary>
internal sealed partial class OrgWriter(Func<string, string?> pictures) : MarkupTextWriter(pictures)
{
    private const char ZeroWidthSpace = '​';

    public static string Write(HDocument document, Func<string, string?> pictures) => new OrgWriter(pictures).Write(document);

    protected override void Start(StringBuilder sb, string? title)
    {
        if (title is not null)
            sb.Append("#+TITLE: ").Append(title.Replace('\n', ' ')).Append("\n\n");
    }

    protected override void End(StringBuilder sb)
    {
        for (var i = 0; i < Notes.Count; i++)
            sb.Append(CultureInfo.InvariantCulture, $"[fn:{i + 1}] ").Append(Notes[i]).Append("\n\n");
    }

    protected override void Heading(StringBuilder sb, int level, string text) =>
        sb.Append('*', level).Append(' ').Append(text).Append("\n\n");

    protected override void Paragraph(StringBuilder sb, string text, int quote)
    {
        for (var q = 0; q < quote; q++)
            sb.Append("#+BEGIN_QUOTE\n");
        sb.Append(Lines(text)).Append('\n');
        for (var q = 0; q < quote; q++)
            sb.Append("#+END_QUOTE\n");
        sb.Append('\n');
    }

    /// <summary>A paragraph's lines: \\ ends a line that breaks, and a line that starts like a block is guarded.</summary>
    private static string Lines(string text, string indent = "") =>
        string.Join("\\\\\n" + indent, text.Split('\n').Select(LineStart));

    protected override void List(StringBuilder sb, IReadOnlyList<MarkupListItem> items, int quote)
    {
        foreach (var item in items)
        {
            var indent = new string(' ', item.Level * 3);
            if (item.Continuation)
            {
                sb.Append(indent).Append("   ").Append(Lines(item.Text, indent + "   ")).Append('\n');
                continue;
            }
            var marker = item.Ordered ? item.Number.ToString(CultureInfo.InvariantCulture) + ". " : "- ";
            sb.Append(indent).Append(marker).Append(Lines(item.Text, indent + new string(' ', marker.Length))).Append('\n');
        }
        sb.Append('\n');
    }

    protected override void Code(StringBuilder sb, IReadOnlyList<string> lines, int quote)
    {
        sb.Append("#+BEGIN_SRC\n");
        foreach (var line in lines)
            sb.Append(line.StartsWith('*') || line.StartsWith("#+", StringComparison.Ordinal) || line.StartsWith(',') ? "," : "").Append(line).Append('\n');
        sb.Append("#+END_SRC\n\n");
    }

    protected override void Table(StringBuilder sb, MarkupTable table)
    {
        var header = 0;
        while (header < table.Rows.Count && table.HeaderRows[header])
            header++;
        for (var r = 0; r < table.Rows.Count; r++)
        {
            sb.Append('|');
            foreach (var cell in table.Rows[r])
                sb.Append(' ').Append((cell?.Text ?? "").Replace("\n", " ", StringComparison.Ordinal).Replace("|", "\\vert{}", StringComparison.Ordinal)).Append(" |");
            sb.Append('\n');
            if (r + 1 == header && header < table.Rows.Count)
                sb.Append('|').Append(string.Join('+', Enumerable.Repeat("---", table.Columns))).Append("|\n");
        }
        sb.Append('\n');
    }

    protected override void Rule(StringBuilder sb) => sb.Append("-----\n\n");

    protected override string Run(string text, HCharFormat format)
    {
        if (format.Shade == HtmlReader.CodeShade)
        {
            // ~code~ can hold anything but a "~" that closes it; =verbatim= then.
            var marker = text.Contains('~', StringComparison.Ordinal) ? '=' : '~';
            return Around(text, core => $"{marker}{core}{marker}");
        }
        return Around(Escape(text), core =>
        {
            if (format.Superscript is true)
                return $"^{{{core}}}";
            if (format.Subscript is true)
                return $"_{{{core}}}";
            if (format.Strike is true)
                core = $"+{core}+";
            if (format.Underline is true)
                core = $"_{core}_";
            if (format.Italic is true)
                core = $"/{core}/";
            if (format.Bold is true)
                core = $"*{core}*";
            return core;
        });
    }

    protected override string Link(string target, string content) =>
        $"[[{target.Replace("[", "%5B", StringComparison.Ordinal).Replace("]", "%5D", StringComparison.Ordinal)}][{content.Replace("]]", "]" + ZeroWidthSpace + "]", StringComparison.Ordinal)}]]";

    protected override string Picture(string link) =>
        $"[[file:{link.Replace("[", "%5B", StringComparison.Ordinal).Replace("]", "%5D", StringComparison.Ordinal)}]]";

    protected override string LineBreak() => "\n";

    protected override string NoteReference(int number) => $"[fn:{number}]";

    // ───────────────────────── Escaping ─────────────────────────

    /// <summary>
    /// A zero-width space before a marker that could open emphasis (after a space, before text) and after one that
    /// could close it, before "[[" and "[fn:", and around ^{ / _{.
    /// </summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 4);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var before = i > 0 ? text[i - 1] : ' ';
            var after = i + 1 < text.Length ? text[i + 1] : ' ';
            if (ch is '*' or '/' or '_' or '+' or '=' or '~')
            {
                var opens = (char.IsWhiteSpace(before) || before is '-' or '(' or '{' or '\'' or '"') && !char.IsWhiteSpace(after);
                var closes = !char.IsWhiteSpace(before) && (char.IsWhiteSpace(after) || char.IsPunctuation(after));
                if (opens)
                    sb.Append(ZeroWidthSpace);
                sb.Append(ch);
                if (closes)
                    sb.Append(ZeroWidthSpace);
                if ((ch is '_' or '^') && after == '{')
                    sb.Append(ZeroWidthSpace);
                continue;
            }
            if (ch == '^' && after == '{')
            {
                sb.Append(ch).Append(ZeroWidthSpace);
                continue;
            }
            if (ch == '[' && (after == '[' || (i + 3 < text.Length && string.CompareOrdinal(text, i, "[fn:", 0, 4) == 0)))
            {
                sb.Append(ch).Append(ZeroWidthSpace);
                continue;
            }
            if (ch == '\\' && after == '\\')
            {
                sb.Append(ch).Append(ZeroWidthSpace);
                continue;
            }
            sb.Append(ch);
        }
        var escaped = sb.ToString();
        // A URL would become a link.
        return escaped.Contains("://", StringComparison.Ordinal) ? escaped.Replace("://", ":" + ZeroWidthSpace + "//", StringComparison.Ordinal) : escaped;
    }

    /// <summary>A line that starts like a headline, list item, table, keyword, rule or fixed-width line gets a zero-width space first.</summary>
    private static string LineStart(string line) => BlockLike().IsMatch(line) ? ZeroWidthSpace + line : line;

    [GeneratedRegex(@"^(?:\*+\s|\s*[-+]\s|\s*\d+[.)]\s|\s*\||\s*#|\s*:(?:\s|$)|-{5,}|\[fn:)")]
    private static partial Regex BlockLike();
}
