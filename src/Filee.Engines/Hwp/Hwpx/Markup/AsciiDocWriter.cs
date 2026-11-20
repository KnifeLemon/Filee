// HDocument → AsciiDoc (as Asciidoctor reads it): "= Title" and a :stem: latexmath header when there are equations,
// == section titles (discrete ones in table cells), paragraphs with " +" at line breaks, ____ quote blocks (longer
// delimiters for deeper quotes), * / . lists nested by repeating the marker ("+" for an item's further paragraphs,
// [start=N] for a list that doesn't start at 1), ---- listing blocks, |=== tables with header rows, colspan / rowspan
// specifiers (2.3+|) and AsciiDoc cells (a|) for cells holding lists, code or tables (!=== inside them), ''' rules,
// **bold** __italic__ ``code`` ##mark## ^sup^ ~sub~ and [.underline] / [.line-through] roles, link:++url++[text],
// image: pictures (files in "<name>_files" next to the result), footnote:[…] notes, and equations as latexmath:[…] or
// [latexmath] blocks (their TeX source, or TeX written from the equation tree).
//
// Escaping: the characters that would format, substitute or link (* _ ` # ^ ~ [ ] { } < > \ @ &, and : ; - . ( where
// they would start a URL, a description list, a dash, an ellipsis or a © replacement) are written in ++passthroughs++
// (which still escape < > &), + and | as {plus} and {vbar}, a line starting with anything but a letter or digit gets
// {empty} in front, and a code line that is a preprocessor directive (include::, ifdef:: …) a backslash.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Filee.Engines.Hwp.Hwpx.Markup;

/// <summary>Writes the document model as AsciiDoc.</summary>
internal sealed partial class AsciiDocWriter(Func<string, string?> pictures) : MarkupTextWriter(pictures)
{
    /// <summary>Section levels Asciidoctor has (== to ======).</summary>
    private const int MaxSectionLevel = 5;

    /// <summary>List levels (* to *****).</summary>
    private const int MaxListLevel = 4;

    /// <summary>Table cells around the text being written: one level can hold a table (!===), deeper ones only text.</summary>
    private int _cell;

    public static string Write(HDocument document, Func<string, string?> pictures) => new AsciiDocWriter(pictures).Write(document);

    protected override void Start(StringBuilder sb, string? title)
    {
        var equations = Source is not null && HDocumentWalker.Inlines(Source.Sections.SelectMany(s => s.Blocks)).Any(i => i is HEquation);
        if (title is not null)
            sb.Append("= ").Append(Escape(title.Replace('\n', ' '))).Append('\n');
        if (equations)
            sb.Append(":stem: latexmath\n");
        if (title is not null || equations)
            sb.Append('\n');
    }

    protected override void End(StringBuilder sb)
    {
    }

    protected override void Heading(StringBuilder sb, int level, string text)
    {
        if (text.Length == 0)
            return;
        if (_cell > 0)
            sb.Append("[discrete]\n");
        sb.Append('=', Math.Min(level, MaxSectionLevel) + 1).Append(' ').Append(text).Append("\n\n");
    }

    protected override void Paragraph(StringBuilder sb, string text, int quote) =>
        Quoted(sb, quote, () => sb.Append(Lines(text)).Append("\n\n"));

    private static void Quoted(StringBuilder sb, int quote, Action body)
    {
        for (var q = 0; q < quote; q++)
            sb.Append('_', 4 + q).Append('\n');
        body();
        for (var q = quote - 1; q >= 0; q--)
            sb.Append('_', 4 + q).Append('\n');
        if (quote > 0)
            sb.Append('\n');
    }

    /// <summary>
    /// A paragraph's lines, " +" ending each that breaks, each guarded against reading as a block (an empty one holds
    /// {empty}, as an empty line would end the paragraph).
    /// </summary>
    private static string Lines(string text) =>
        string.Join(" +\n", text.Trim('\n').Split('\n').Select(line => line.Trim().Length == 0 ? "{empty}" : LineStart(line.Trim())));

    protected override void List(StringBuilder sb, IReadOnlyList<MarkupListItem> items, int quote)
    {
        var first = true;
        foreach (var item in items)
        {
            if (item.Continuation)
            {
                if (!first)
                    sb.Append("+\n");
                sb.Append(Lines(item.Text)).Append('\n');
                continue;
            }
            if (first && item.Ordered && item.Number != 1)
                sb.Append(CultureInfo.InvariantCulture, $"[start={item.Number}]\n");
            first = false;
            var level = Math.Min(item.Level, MaxListLevel);
            sb.Append(item.Ordered ? '.' : '*', level + 1).Append(' ').Append(Lines(item.Text)).Append('\n');
        }
        sb.Append('\n');
    }

    protected override void Code(StringBuilder sb, IReadOnlyList<string> lines, int quote)
    {
        var longest = lines.Where(l => l.Length >= 4 && l.All(c => c == '-')).Select(l => l.Length).DefaultIfEmpty(3).Max();
        var fence = new string('-', longest + 1);
        Quoted(sb, quote, () =>
        {
            // Callouts (<1> at a line's end) would be read as such: special characters only.
            if (lines.Any(l => Callout().IsMatch(l)))
                sb.Append("[subs=\"specialchars\"]\n");
            sb.Append(fence).Append('\n');
            foreach (var line in lines)
                sb.Append(Directive().IsMatch(line) ? "\\" : "").Append(line).Append('\n');
            sb.Append(fence).Append("\n\n");
        });
    }

    protected override void Table(StringBuilder sb, MarkupTable table)
    {
        if (_cell > 1)
        {
            // Asciidoctor nests tables one level: deeper ones are their cells' text.
            foreach (var row in table.Rows)
                foreach (var cell in row.OfType<MarkupCell>())
                    if (Content(cell, separator: '|') is { Length: > 0 } text)
                        sb.Append(text).Append("\n\n");
            return;
        }
        var separator = _cell == 0 ? '|' : '!';
        var owners = MarkupTableOwners.Owners(table);
        sb.Append(CultureInfo.InvariantCulture, $"[cols=\"{table.Columns}*\"");
        if (table.HeaderRows.Count > 0 && table.HeaderRows[0])
            sb.Append(",options=\"header\"");
        sb.Append("]\n").Append(separator).Append("===\n");
        for (var r = 0; r < table.Rows.Count; r++)
        {
            for (var c = 0; c < table.Columns; c++)
            {
                var (ownerRow, ownerColumn) = owners[r, c];
                if (ownerRow >= 0 && (ownerRow, ownerColumn) != (r, c))
                    continue; // covered by a merged cell
                if (table.Rows[r][c] is not { } cell)
                {
                    sb.Append(separator).Append('\n');
                    continue;
                }
                if (cell.ColSpan > 1)
                    sb.Append(cell.ColSpan.ToString(CultureInfo.InvariantCulture));
                if (cell.RowSpan > 1)
                    sb.Append('.').Append(cell.RowSpan.ToString(CultureInfo.InvariantCulture));
                if (cell.ColSpan > 1 || cell.RowSpan > 1)
                    sb.Append('+');
                if (!cell.Simple)
                    sb.Append('a');
                sb.Append(separator).Append(' ').Append(Content(cell, separator)).Append('\n');
            }
            sb.Append('\n');
        }
        sb.Append(separator).Append("===\n\n");
    }

    /// <summary>A cell's content: its text with " +" at line breaks, or (an AsciiDoc cell) its blocks.</summary>
    private string Content(MarkupCell cell, char separator)
    {
        string text;
        if (cell.Simple)
        {
            text = Lines(cell.Text);
        }
        else
        {
            var blocks = new StringBuilder();
            _cell++;
            try
            {
                Blocks(cell.Source.Blocks, blocks);
            }
            finally
            {
                _cell--;
            }
            text = blocks.ToString().Trim('\n');
        }
        // A separator in the text would end the cell.
        return separator == '!' ? text.Replace("!", "\\!", StringComparison.Ordinal) : text;
    }

    protected override void Rule(StringBuilder sb) => sb.Append("'''\n\n");

    protected override string Run(string text, HCharFormat format)
    {
        if (format.Shade == HtmlReader.CodeShade)
            return Around(text, core => $"``{Escape(core)}``");
        return Around(Escape(text), core =>
        {
            if (format.Superscript is true)
                core = $"^{core.Replace(" ", "{sp}", StringComparison.Ordinal)}^";
            else if (format.Subscript is true)
                core = $"~{core.Replace(" ", "{sp}", StringComparison.Ordinal)}~";
            if (format.Underline is true)
                core = $"[.underline]##{core}##";
            if (format.Strike is true)
                core = $"[.line-through]##{core}##";
            if (format.Shade == HtmlReader.MarkShade)
                core = $"##{core}##";
            if (format.Italic is true)
                core = $"__{core}__";
            if (format.Bold is true)
                core = $"**{core}**";
            return core;
        });
    }

    protected override string Link(string target, string content) =>
        $"link:++{target.Replace("+", "%2B", StringComparison.Ordinal).Replace(" ", "%20", StringComparison.Ordinal)}++[{content}]";

    protected override string Picture(string link) => $"image:{link}[]";

    protected override string LineBreak() => "\n";

    protected override string NoteReference(int number) => $"footnote:[{Notes[number - 1]}]";

    protected override string? Equation(HEquation equation)
    {
        if (Tex(equation) is not { } tex)
            return null;
        return $"latexmath:[{tex.Replace("]", "\\]", StringComparison.Ordinal)}]";
    }

    protected override bool DisplayEquation(StringBuilder sb, HEquation equation, int quote)
    {
        if (Tex(equation) is not { } tex)
            return false;
        Quoted(sb, quote, () => sb.Append("[latexmath]\n++++\n").Append(tex).Append("\n++++\n\n"));
        return true;
    }

    /// <summary>An equation's TeX: its source, else written from its tree; null when neither can be had.</summary>
    private static string? Tex(HEquation equation) =>
        equation.Tex is { } source && source.Trim().Length > 0 ? source.Trim() : TexMathWriter.Write(equation.Math);

    // ───────────────────────── Escaping ─────────────────────────

    /// <summary>
    /// Text with what Asciidoctor would read as formatting, an attribute, a macro, a link or a replacement put in
    /// passthroughs (consecutive ones together), and + and | as their attributes.
    /// </summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        var pass = new StringBuilder();
        void Flush()
        {
            if (pass.Length > 0)
                sb.Append("++").Append(pass).Append("++");
            pass.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            var special = ch switch
            {
                '*' or '_' or '`' or '#' or '^' or '~' or '[' or ']' or '{' or '}' or '<' or '>' or '\\' or '@' or '&' => true,
                ':' => next is ':' or '/',
                ';' => next == ';',
                '-' => next == '-',
                '.' => next == '.' && i + 2 < text.Length && text[i + 2] == '.',
                '(' => Replacement().IsMatch(text.AsSpan(i)),
                _ => false,
            };
            if (special)
            {
                pass.Append(ch);
                continue;
            }
            Flush();
            switch (ch)
            {
                case '+':
                    sb.Append("{plus}");
                    break;
                case '|':
                    sb.Append("{vbar}");
                    break;
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
        Flush();
        return sb.ToString();
    }

    /// <summary>A line that doesn't start with a letter or digit gets {empty} first, so it can't start a block.</summary>
    private static string LineStart(string line) =>
        line.Length > 0 && !char.IsLetterOrDigit(line[0]) && !line.StartsWith("{empty}", StringComparison.Ordinal) ? "{empty}" + line
        : NumberedLine().IsMatch(line) ? "{empty}" + line
        : line;

    /// <summary>"1. " or "a) " and the like: an ordered list item.</summary>
    [GeneratedRegex(@"^[0-9a-zA-Z]{1,9}[.)](?:\s|$)")]
    private static partial Regex NumberedLine();

    [GeneratedRegex(@"^\((?:C|R|TM)\)")]
    private static partial Regex Replacement();

    [GeneratedRegex(@"<!?-?-?\.?\d+-?-?>\s*$")]
    private static partial Regex Callout();

    [GeneratedRegex(@"^(?:include|ifdef|ifndef|ifeval|endif)::")]
    private static partial Regex Directive();
}
