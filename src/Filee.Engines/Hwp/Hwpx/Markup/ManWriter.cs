// HDocument → a man page (the man macros, as groff and mandoc read them): .TH with the title, .SH / .SS headings
// (deeper ones as bold paragraphs), .PP paragraphs with .br at line breaks, .RS / .RE indents for quotes and nested
// lists, .IP items with a bullet or their number, .EX / .EE code, tbl tables (.TS … .TE, boxed, header rows bold, s and
// ^ for merged cells, every cell a T{ … T} text block), \fB \fI \f(CR fonts, links as their text and <URL>, and
// footnotes numbered [1] in the text and listed under NOTES at the end. A man page has no pictures: they are left out.
// Equations are their linear text ("(a)/(b)").
//
// Escaping: the file is ASCII. Backslashes are \e, minus signs \-, quotes, apostrophes, backticks, carets and tildes
// their named characters (\(dq \(aq \(ga \(ha \(ti), other characters \[uXXXX]; a line that starts with "." or "'"
// (a request), a space (a break) or "T}" (the end of a table cell) gets \& first.

using System.Globalization;
using System.Text;

namespace Filee.Engines.Hwp.Hwpx.Markup;

/// <summary>Writes the document model as a man page.</summary>
internal sealed class ManWriter : MarkupTextWriter
{
    /// <summary>Pictures are left out: a man page can't show them.</summary>
    public ManWriter()
        : base(_ => null)
    {
    }

    protected override void Start(StringBuilder sb, string? title)
    {
        var name = title is null ? "" : Escape(title.Replace('\n', ' '));
        var date = Source?.Date is { } d ? Escape(d.Trim()) : "";
        sb.Append(".TH \"").Append(name).Append("\" \"1\" \"").Append(date).Append("\" \"\" \"\"\n");
    }

    protected override void End(StringBuilder sb)
    {
        if (Notes.Count == 0)
            return;
        sb.Append(".SH NOTES\n");
        for (var i = 0; i < Notes.Count; i++)
            sb.Append(CultureInfo.InvariantCulture, $".IP \"[{i + 1}]\" 5\n").Append(Lines(Notes[i])).Append('\n');
    }

    protected override void Heading(StringBuilder sb, int level, string text)
    {
        if (text.Length == 0)
            return;
        if (level <= 2)
            sb.Append(level == 1 ? ".SH \"" : ".SS \"").Append(text).Append("\"\n");
        else
            sb.Append(".PP\n").Append(Lines("\\fB" + text + "\\fP")).Append('\n');
    }

    protected override void Paragraph(StringBuilder sb, string text, int quote)
    {
        for (var q = 0; q < quote; q++)
            sb.Append(".RS 4\n");
        sb.Append(".PP\n").Append(Lines(text)).Append('\n');
        for (var q = 0; q < quote; q++)
            sb.Append(".RE\n");
    }

    /// <summary>Text lines, .br between the lines of a break, each guarded against reading as a request.</summary>
    private static string Lines(string text) =>
        string.Join("\n.br\n", text.Trim('\n').Split('\n').Select(LineStart));

    protected override void List(StringBuilder sb, IReadOnlyList<MarkupListItem> items, int quote)
    {
        var depth = 0;
        foreach (var item in items)
        {
            for (; depth < item.Level; depth++)
                sb.Append(".RS 4\n");
            for (; depth > item.Level; depth--)
                sb.Append(".RE\n");
            var tag = item.Continuation ? "" : item.Ordered ? item.Number.ToString(CultureInfo.InvariantCulture) + "." : "\\(bu";
            sb.Append(".IP \"").Append(tag).Append("\" 4\n").Append(Lines(item.Text)).Append('\n');
        }
        for (; depth > 0; depth--)
            sb.Append(".RE\n");
    }

    protected override void Code(StringBuilder sb, IReadOnlyList<string> lines, int quote)
    {
        for (var q = 0; q < quote; q++)
            sb.Append(".RS 4\n");
        sb.Append(".PP\n.EX\n");
        foreach (var line in lines)
            sb.Append(LineStart(Escape(line))).Append('\n');
        sb.Append(".EE\n");
        for (var q = 0; q < quote; q++)
            sb.Append(".RE\n");
    }

    protected override void Table(StringBuilder sb, MarkupTable table)
    {
        var owners = MarkupTableOwners.Owners(table);
        sb.Append(".PP\n.TS\nallbox tab(@);\n");
        // A format line per row: l for a cell (lb in a header row), s where a cell spans from the left, ^ from above.
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var keys = new List<string>(table.Columns);
            for (var c = 0; c < table.Columns; c++)
            {
                var (ownerRow, ownerColumn) = owners[r, c];
                keys.Add(ownerRow < r && ownerRow >= 0 && ownerColumn == c ? "^"
                    : ownerRow >= 0 && ownerColumn < c ? "s"
                    : table.HeaderRows[r] ? "lb" : "l");
            }
            sb.AppendJoin(' ', keys).Append(r + 1 == table.Rows.Count ? ".\n" : "\n");
        }
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var entries = new List<string>(table.Columns);
            for (var c = 0; c < table.Columns; c++)
            {
                var (ownerRow, ownerColumn) = owners[r, c];
                entries.Add((ownerRow, ownerColumn) == (r, c) && table.Rows[r][c] is { } cell
                    ? "T{\n" + Lines(CellText(cell).Replace("@", "\\(at", StringComparison.Ordinal)) + "\nT}"
                    : "");
            }
            // Entries after the last cell of the row are left blank.
            while (entries.Count > 0 && entries[^1].Length == 0)
                entries.RemoveAt(entries.Count - 1);
            sb.AppendJoin('@', entries).Append('\n');
        }
        sb.Append(".TE\n");
    }

    /// <summary>A cell's text: its paragraphs, and those of the tables in it, as lines.</summary>
    private string CellText(MarkupCell cell)
    {
        if (cell.Simple)
            return cell.Text;
        var lines = new List<string>();
        var pending = new Stack<HBlock>(Enumerable.Reverse(cell.Source.Blocks));
        while (pending.TryPop(out var block))
        {
            switch (block)
            {
                case HParagraph paragraph when Inlines(paragraph.Inlines).Trim() is { Length: > 0 } text:
                    lines.Add(text.Replace("\n", " ", StringComparison.Ordinal));
                    break;
                case HTable inner:
                    foreach (var nested in Enumerable.Reverse(inner.Rows.SelectMany(r => r.Cells).SelectMany(c => c.Blocks).ToList()))
                        pending.Push(nested);
                    break;
            }
        }
        return string.Join('\n', lines);
    }

    protected override void Rule(StringBuilder sb) => sb.Append(".PP\n.ce 1\n* * *\n");

    protected override string Run(string text, HCharFormat format)
    {
        if (format.Shade == HtmlReader.CodeShade)
            return Around(Escape(text), core => $"\\f(CR{core}\\fP");
        return Around(Escape(text), core => (format.Bold is true, format.Italic is true || format.Underline is true) switch
        {
            (true, true) => $"\\f(BI{core}\\fP",
            (true, false) => $"\\fB{core}\\fP",
            (false, true) => $"\\fI{core}\\fP",
            _ => core,
        });
    }

    protected override string Link(string target, string content)
    {
        var url = Escape(target);
        return content.Trim() == url ? content : $"{content} <{url}>";
    }

    protected override string Picture(string link) => "";

    protected override string LineBreak() => "\n";

    protected override string NoteReference(int number) => $"[{number}]";

    protected override string? Equation(HEquation equation) =>
        EquationScript.Deeper(equation.Math, TexMathWriter.MaxDepth) ? null : Escape(EquationScript.Linear(equation.Math));

    // ───────────────────────── Escaping ─────────────────────────

    /// <summary>Text as roff prints it: ASCII, with the characters roff reads specially escaped.</summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        foreach (var rune in text.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '\\':
                    sb.Append("\\e");
                    break;
                case '-':
                    sb.Append("\\-");
                    break;
                case '"':
                    sb.Append("\\(dq");
                    break;
                case '\'':
                    sb.Append("\\(aq");
                    break;
                case '`':
                    sb.Append("\\(ga");
                    break;
                case '^':
                    sb.Append("\\(ha");
                    break;
                case '~':
                    sb.Append("\\(ti");
                    break;
                case '\n':
                    sb.Append('\n');
                    break;
                case '\t':
                    sb.Append(' ');
                    break;
                case 0xA0:
                    sb.Append("\\ ");
                    break;
                case < ' ' or 0x7F or 0xAD or 0xFEFF:
                    break;
                case < 0x80:
                    sb.Append((char)rune.Value);
                    break;
                default:
                    sb.Append(CultureInfo.InvariantCulture, $"\\[u{rune.Value:X4}]");
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>A text line that would read as a request, a break or the end of a table cell gets \&amp; first.</summary>
    private static string LineStart(string line) =>
        line.Length > 0 && (line[0] is '.' or '\'' or ' ' || line.StartsWith("T}", StringComparison.Ordinal)) ? "\\&" + line : line;
}
