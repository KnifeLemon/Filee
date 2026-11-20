// HDocument → Typst markup (Typst 0.11 or later, for table.cell and table.header): the title as the document's
// metadata and a centred heading line, = headings, paragraphs with \ at line breaks, #quote(block: true) for quotes,
// - and "N." lists nested by indentation (an item's further paragraphs indented under it), ``` raw blocks, #table with
// table.header rows and table.cell(colspan, rowspan) for merged cells, #line rules, #strong #emph #underline #strike
// #super #sub #highlight and `raw` text, #link, #image pictures (files in "<name>_files" next to the result),
// #footnote notes, and equations in Typst's own math ($…$ inline, $ … $ on their own), written from the equation tree.
//
// Escaping: every markup character (\ # $ * _ ` < > @ [ ] ~ ' ") gets a backslash, as do // and /* (comments), -- and
// -? (dashes, soft hyphens), ... and :// (an ellipsis, a URL), a "." or "(" that starts a run (it would continue the
// #function before it), and a line that would start a heading, list or term (= - + / "1.").

using System.Globalization;
using System.Text;

namespace Filee.Engines.Hwp.Hwpx.Markup;

/// <summary>Writes the document model as Typst markup.</summary>
internal sealed class TypstWriter(Func<string, string?> pictures) : MarkupTextWriter(pictures)
{
    public static string Write(HDocument document, Func<string, string?> pictures) => new TypstWriter(pictures).Write(document);

    protected override void Start(StringBuilder sb, string? title)
    {
        var authors = Source?.Authors.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => Quoted(a.Trim())).ToList() ?? [];
        if (title is null && authors.Count == 0)
            return;
        sb.Append("#set document(");
        if (title is not null)
            sb.Append("title: ").Append(Quoted(title.Replace('\n', ' ')));
        if (authors.Count > 0)
            sb.Append(title is null ? "" : ", ").Append("author: (").AppendJoin(", ", authors).Append(authors.Count == 1 ? ",)" : ")");
        sb.Append(")\n\n");
        if (title is not null)
            sb.Append("#align(center, text(size: 1.7em, weight: \"bold\")[").Append(Escape(title.Replace('\n', ' '))).Append("])\n\n");
    }

    protected override void End(StringBuilder sb)
    {
    }

    protected override void Heading(StringBuilder sb, int level, string text)
    {
        if (text.Length > 0)
            sb.Append('=', level).Append(' ').Append(text).Append("\n\n");
    }

    protected override void Paragraph(StringBuilder sb, string text, int quote) =>
        Quoted(sb, quote, () => sb.Append(Lines(text, "")).Append("\n\n"));

    private static void Quoted(StringBuilder sb, int quote, Action body)
    {
        for (var q = 0; q < quote; q++)
            sb.Append("#quote(block: true)[\n");
        body();
        for (var q = 0; q < quote; q++)
            sb.Append("]\n");
        if (quote > 0)
            sb.Append('\n');
    }

    /// <summary>A paragraph's lines, \ ending each that breaks, each guarded against starting a block.</summary>
    private static string Lines(string text, string indent) =>
        string.Join(" \\\n" + indent, text.Trim('\n').Split('\n').Select(line => LineStart(line.Trim())));

    protected override void List(StringBuilder sb, IReadOnlyList<MarkupListItem> items, int quote)
    {
        foreach (var item in items)
        {
            var indent = new string(' ', item.Level * 2);
            if (item.Continuation)
            {
                // A further paragraph of the item: after a blank line, indented under its text.
                var under = indent + "  ";
                sb.Append('\n').Append(under).Append(Lines(item.Text, under)).Append('\n');
                continue;
            }
            var marker = item.Ordered ? item.Number.ToString(CultureInfo.InvariantCulture) + ". " : "- ";
            sb.Append(indent).Append(marker).Append(Lines(item.Text, indent + new string(' ', marker.Length))).Append('\n');
        }
        sb.Append('\n');
    }

    protected override void Code(StringBuilder sb, IReadOnlyList<string> lines, int quote)
    {
        var longest = lines.Select(l => LongestRun(l, '`')).DefaultIfEmpty(0).Max();
        var fence = new string('`', Math.Max(3, longest + 1));
        Quoted(sb, quote, () =>
        {
            sb.Append(fence).Append('\n');
            foreach (var line in lines)
                sb.Append(line).Append('\n');
            sb.Append(fence).Append("\n\n");
        });
    }

    private static int LongestRun(string text, char ch)
    {
        int longest = 0, run = 0;
        foreach (var c in text)
        {
            run = c == ch ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        return longest;
    }

    protected override void Table(StringBuilder sb, MarkupTable table)
    {
        var owners = MarkupTableOwners.Owners(table);
        sb.Append(CultureInfo.InvariantCulture, $"#table(\n  columns: {table.Columns},\n");
        var header = 0;
        while (header < table.Rows.Count && table.HeaderRows[header])
            header++;
        if (header > 0)
            sb.Append("  table.header(\n");
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var indent = r < header ? "    " : "  ";
            for (var c = 0; c < table.Columns; c++)
            {
                var (ownerRow, ownerColumn) = owners[r, c];
                if (ownerRow >= 0 && (ownerRow, ownerColumn) != (r, c))
                    continue; // covered by a merged cell
                sb.Append(indent);
                if (table.Rows[r][c] is not { } cell)
                {
                    sb.Append("[],\n");
                    continue;
                }
                if (cell.ColSpan > 1 || cell.RowSpan > 1)
                {
                    sb.Append("table.cell(");
                    if (cell.ColSpan > 1)
                        sb.Append(CultureInfo.InvariantCulture, $"colspan: {cell.ColSpan}");
                    if (cell.RowSpan > 1)
                        sb.Append(cell.ColSpan > 1 ? ", " : "").Append(CultureInfo.InvariantCulture, $"rowspan: {cell.RowSpan}");
                    sb.Append(')');
                }
                sb.Append('[').Append(Content(cell)).Append("],\n");
            }
            if (r + 1 == header)
                sb.Append("  ),\n");
        }
        sb.Append(")\n\n");
    }

    private string Content(MarkupCell cell)
    {
        if (cell.Simple)
            return Lines(cell.Text, "");
        var blocks = new StringBuilder();
        Blocks(cell.Source.Blocks, blocks);
        return "\n" + blocks.ToString().Trim('\n') + "\n";
    }

    protected override void Rule(StringBuilder sb) => sb.Append("#line(length: 100%)\n\n");

    protected override string Run(string text, HCharFormat format)
    {
        if (format.Shade == HtmlReader.CodeShade)
            return Around(text, core => core.Contains('`', StringComparison.Ordinal) ? $"#raw({Quoted(core)})" : $"`{core}`");
        return Around(Escape(text), core =>
        {
            if (format.Superscript is true)
                core = $"#super[{core}]";
            else if (format.Subscript is true)
                core = $"#sub[{core}]";
            if (format.Underline is true)
                core = $"#underline[{core}]";
            if (format.Strike is true)
                core = $"#strike[{core}]";
            if (format.Shade == HtmlReader.MarkShade)
                core = $"#highlight[{core}]";
            if (format.Italic is true)
                core = $"#emph[{core}]";
            if (format.Bold is true)
                core = $"#strong[{core}]";
            return core;
        });
    }

    protected override string Link(string target, string content) => $"#link({Quoted(target)})[{content}]";

    // The link is a URL path; Typst wants the file's name.
    protected override string Picture(string link) => $"#image({Quoted(Uri.UnescapeDataString(link))})";

    protected override string LineBreak() => "\n";

    protected override string NoteReference(int number) => $"#footnote[{Notes[number - 1]}]";

    protected override string? Equation(HEquation equation)
    {
        if (TypstMathWriter.Write(equation.Math) is not { Length: > 0 } math)
            return null;
        return equation.Display ? $"$ {math} $" : $"${math}$";
    }

    protected override bool DisplayEquation(StringBuilder sb, HEquation equation, int quote)
    {
        if (TypstMathWriter.Write(equation.Math) is not { Length: > 0 } math)
            return false;
        Quoted(sb, quote, () => sb.Append("$ ").Append(math).Append(" $\n\n"));
        return true;
    }

    // ───────────────────────── Escaping ─────────────────────────

    /// <summary>A Typst string literal.</summary>
    private static string Quoted(string text)
    {
        var sb = new StringBuilder(text.Length + 2).Append('"');
        foreach (var ch in text)
        {
            sb.Append(ch switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => ch.ToString(),
            });
        }
        return sb.Append('"').ToString();
    }

    /// <summary>Text with every character Typst's markup gives a meaning escaped.</summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            var escape = ch switch
            {
                '\\' or '#' or '$' or '*' or '_' or '`' or '<' or '>' or '@' or '[' or ']' or '~' or '\'' or '"' => true,
                '/' => next is '/' or '*',
                '-' => next is '-' or '?',
                '.' => i == 0 || (next == '.' && i + 2 < text.Length && text[i + 2] == '.'),
                '(' => i == 0,
                ':' => next == '/' && i + 2 < text.Length && text[i + 2] == '/',
                _ => false,
            };
            if (escape)
            {
                sb.Append('\\').Append(ch);
                continue;
            }
            switch (ch)
            {
                case '\t':
                    sb.Append(' ');
                    break;
                case '\n':
                    sb.Append('\n');
                    break;
                case < ' ' or '﻿':
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>A line that would start a heading, list item or term gets its first character escaped.</summary>
    private static string LineStart(string line)
    {
        if (line.Length == 0)
            return line;
        if (line[0] is '=' or '-' or '+' or '/')
            return "\\" + line;
        var digits = line.TakeWhile(char.IsAsciiDigit).Count();
        return digits > 0 && digits < line.Length && line[digits] == '.' && (digits + 1 == line.Length || char.IsWhiteSpace(line[digits + 1]))
            ? line[..digits] + "\\" + line[digits..]
            : line;
    }
}
