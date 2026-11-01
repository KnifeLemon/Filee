// The walk the writers of the text markup languages (Textile, MediaWiki, reStructuredText, Org) share: code lines
// gathered into one block, list paragraphs into one list (levels, numbers counted like 한글's), quotes by indent, tables
// as a grid with their spans, runs of the same format joined, footnotes numbered. Each language says how a heading, a
// paragraph, a list item, a code block, a table and a run of text are written.

using System.Text;

namespace Filee.Engines.Hwp.Hwpx.Markup;

/// <summary>One item of a list run (<see cref="MarkupTextWriter.List"/>).</summary>
/// <param name="Level">0-based nesting level.</param>
/// <param name="Number">The item's number in an ordered list (from the list's counters), else 0.</param>
/// <param name="Continuation">A further paragraph of the item before (no marker of its own).</param>
internal sealed record MarkupListItem(int Level, bool Ordered, int Number, string Text, bool Continuation);

/// <summary>A table as a grid: covered positions are null.</summary>
internal sealed record MarkupTable(IReadOnlyList<MarkupCell?[]> Rows, int Columns, IReadOnlyList<bool> HeaderRows);

/// <summary>One cell of a <see cref="MarkupTable"/> with its spans, its paragraphs' text and the cell itself.</summary>
/// <param name="Simple">The cell holds paragraphs only (no tables, lists or code), which <paramref name="Text"/> says.</param>
internal sealed record MarkupCell(string Text, int RowSpan, int ColSpan, HCell Source, bool Simple);

/// <summary>Base of the text markup writers.</summary>
internal abstract class MarkupTextWriter
{
    private readonly ListNumbers _numbers = new();

    /// <param name="pictures">Maps a picture file to the link written into the result, or null to leave it out.</param>
    protected MarkupTextWriter(Func<string, string?> pictures) => Pictures = pictures;

    protected Func<string, string?> Pictures { get; }

    /// <summary>The footnotes so far (their text), numbered from 1.</summary>
    protected List<string> Notes { get; } = [];

    /// <summary>The text of the document.</summary>
    public string Write(HDocument document)
    {
        var sb = new StringBuilder();
        Start(sb, string.IsNullOrWhiteSpace(document.Title) ? null : document.Title.Trim());
        foreach (var section in document.Sections)
            Blocks(section.Blocks, sb);
        End(sb);
        return sb.ToString().TrimEnd() + "\n";
    }

    // ───────────────────────── What each language writes ─────────────────────────

    /// <summary>Before the body: the title.</summary>
    protected abstract void Start(StringBuilder sb, string? title);

    /// <summary>After the body: the footnotes.</summary>
    protected abstract void End(StringBuilder sb);

    protected abstract void Heading(StringBuilder sb, int level, string text);

    /// <param name="quote">Block quote level (0 = none).</param>
    protected abstract void Paragraph(StringBuilder sb, string text, int quote);

    protected abstract void List(StringBuilder sb, IReadOnlyList<MarkupListItem> items, int quote);

    protected abstract void Code(StringBuilder sb, IReadOnlyList<string> lines, int quote);

    protected abstract void Table(StringBuilder sb, MarkupTable table);

    protected abstract void Rule(StringBuilder sb);

    /// <summary>A run of text in one format (spaces at its ends belong outside the markers).</summary>
    protected abstract string Run(string text, HCharFormat format);

    /// <summary>A link with its written content.</summary>
    protected abstract string Link(string target, string content);

    /// <summary>A picture by the link <see cref="Pictures"/> gave it.</summary>
    protected abstract string Picture(string link);

    protected abstract string LineBreak();

    /// <summary>The reference to footnote <paramref name="number"/> (its text is <c>Notes[number - 1]</c>).</summary>
    protected abstract string NoteReference(int number);

    // ───────────────────────── The walk ─────────────────────────

    /// <summary>Quote level of a paragraph: the readers indent quotes by 2000 HWPUNIT per level.</summary>
    protected static int Quote(HParagraph paragraph) => paragraph.List is null ? Math.Clamp((paragraph.Format.Left ?? 0) / MarkupBlocks.QuoteIndent, 0, 8) : 0;

    protected void Blocks(List<HBlock> blocks, StringBuilder sb)
    {
        for (var i = 0; i < blocks.Count;)
        {
            var block = blocks[i];
            if (MarkupBlocks.IsCode(block))
            {
                var lines = new List<string>();
                var quote = Quote((HParagraph)block);
                for (; i < blocks.Count && MarkupBlocks.IsCode(blocks[i]); i++)
                    lines.Add(string.Concat(((HParagraph)blocks[i]).Inlines.OfType<HText>().Select(t => t.Text)));
                Code(sb, lines, quote);
                continue;
            }
            if (block is HParagraph { List: not null })
            {
                var items = new List<MarkupListItem>();
                for (; i < blocks.Count && blocks[i] is HParagraph { List: { } item } paragraph; i++)
                {
                    var level = Math.Clamp(item.Level, 0, 8);
                    var definition = item.Numbering.Levels.Count == 0 ? null : item.Numbering.Levels[Math.Min(level, item.Numbering.Levels.Count - 1)];
                    var ordered = definition is { Bullet: false };
                    var number = 0;
                    if (item.Numbered)
                    {
                        var marker = _numbers.Next(item);
                        number = ordered && definition!.Format == "DIGIT" && int.TryParse(new string([.. marker.Where(char.IsAsciiDigit)]), out var n) ? n : 1;
                    }
                    items.Add(new MarkupListItem(level, ordered, number, Inlines(paragraph.Inlines), !item.Numbered));
                }
                List(sb, items, 0);
                continue;
            }
            i++;
            switch (block)
            {
                case HParagraph { HeadingLevel: > 0 } heading:
                    Heading(sb, Math.Clamp(heading.HeadingLevel, 1, 9), Inlines(heading.Inlines).Replace("\n", " ", StringComparison.Ordinal).Trim());
                    break;
                case HParagraph paragraph when paragraph.Inlines.Any(x => x is HShape { Kind: HShapeKind.Line }) && HDocumentWalker.Text(paragraph.Inlines).Length == 0:
                    Rule(sb);
                    break;
                case HParagraph paragraph:
                    {
                        var text = Inlines(paragraph.Inlines);
                        if (text.Trim().Length > 0)
                            Paragraph(sb, text.Trim('\n'), Quote(paragraph));
                        break;
                    }
                case HTable table when table.Rows.Count > 0:
                    Table(sb, Grid(table));
                    break;
            }
        }
    }

    /// <summary>The table as a grid; a cell's paragraphs are joined by line breaks.</summary>
    private MarkupTable Grid(HTable table)
    {
        var columns = Math.Max(1, Math.Max(table.ColumnCount, table.Rows.Max(r => r.Cells.Sum(c => Math.Max(1, c.ColSpan)))));
        columns = Math.Min(columns, Documents.DocumentLimits.Default.MaxTableColumns);
        var rows = new List<MarkupCell?[]>();
        var covered = new HashSet<(int Row, int Column)>();
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var cells = new MarkupCell?[columns];
            var column = 0;
            foreach (var cell in table.Rows[r].Cells)
            {
                while (column < columns && covered.Contains((r, column)))
                    column++;
                if (column >= columns)
                    break;
                var rowSpan = Math.Clamp(cell.RowSpan, 1, table.Rows.Count - r);
                var colSpan = Math.Clamp(cell.ColSpan, 1, columns - column);
                // Header cells are bold by nature: no bold markers inside them.
                var header = table.Rows[r].Header;
                var text = string.Join(LineBreak(), cell.Blocks.OfType<HParagraph>()
                    .Select(p => Inlines(header ? [.. p.Inlines.Select(i => i is HText t ? new HText(t.Text, t.Format with { Bold = null }) : i)] : p.Inlines)
                        .Replace("\n", LineBreak(), StringComparison.Ordinal).Trim())
                    .Where(t => t.Length > 0));
                var simple = cell.Blocks.All(b => b is HParagraph { List: null } p && !MarkupBlocks.IsCode(p));
                cells[column] = new MarkupCell(text, rowSpan, colSpan, cell, simple);
                for (var rr = r; rr < r + rowSpan; rr++)
                    for (var cc = column; cc < column + colSpan; cc++)
                        if (rr != r || cc != column)
                            covered.Add((rr, cc));
                column += colSpan;
            }
            rows.Add(cells);
        }
        return new MarkupTable(rows, columns, [.. table.Rows.Select(r => r.Header)]);
    }

    /// <summary>The inlines as text: runs of one format joined, then written by the language.</summary>
    protected string Inlines(List<HInline> inlines)
    {
        var sb = new StringBuilder();
        var run = new StringBuilder();
        HCharFormat? format = null;

        void Flush()
        {
            if (run.Length > 0)
                sb.Append(Run(run.ToString(), format ?? default));
            run.Clear();
            format = null;
        }

        foreach (var inline in inlines)
        {
            if (inline is HText t)
            {
                var plain = WithoutLayout(t.Format);
                if (format is { } current && current != plain)
                    Flush();
                format = plain;
                run.Append(t.Text);
                continue;
            }
            Flush();
            switch (inline)
            {
                case HLineBreak:
                    sb.Append(LineBreak());
                    break;
                case HTab:
                    sb.Append(' ');
                    break;
                case HLink link:
                    {
                        var content = Inlines(link.Content);
                        sb.Append(link.Target.StartsWith('#') || content.Trim().Length == 0 ? content : Link(link.Target, content));
                        break;
                    }
                case HImage image:
                    if (Pictures(image.Path) is { } target)
                        sb.Append(Picture(target));
                    break;
                case HNote note:
                    Notes.Add(string.Join(" ", note.Blocks.OfType<HParagraph>().Select(p => Inlines(p.Inlines).Replace("\n", " ", StringComparison.Ordinal).Trim())
                        .Where(t => t.Length > 0)));
                    sb.Append(NoteReference(Notes.Count));
                    break;
                case HTextBox box:
                    sb.AppendJoin(' ', box.Blocks.OfType<HParagraph>().Select(p => Inlines(p.Inlines)));
                    break;
            }
        }
        Flush();
        return sb.ToString();
    }

    /// <summary>Only what a markup language can say: emphasis, code and marks; fonts, sizes and colours go.</summary>
    private static HCharFormat WithoutLayout(HCharFormat format) => new(
        Bold: format.Bold is true ? true : null,
        Italic: format.Italic is true ? true : null,
        Underline: format.Underline is true ? true : null,
        Strike: format.Strike is true ? true : null,
        Superscript: format.Superscript is true ? true : null,
        Subscript: format.Subscript is true && format.Superscript is not true ? true : null,
        Shade: format.Shade is HtmlReader.CodeShade or HtmlReader.MarkShade ? format.Shade : null);

    /// <summary>
    /// <paramref name="core"/> applied to the text without its leading and trailing spaces, which stay outside the
    /// markers (emphasis markers must touch the text in every one of these languages).
    /// </summary>
    protected static string Around(string text, Func<string, string> core)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return text;
        var lead = text[..(text.Length - text.TrimStart().Length)];
        var trail = text[text.TrimEnd().Length..];
        return lead + core(trimmed) + trail;
    }
}
