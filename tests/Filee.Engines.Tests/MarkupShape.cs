// A canonical text form of the parts of the document model a markup language can hold (headings, quotes, lists,
// code, tables with spans and header rows, emphasis, code and marks, links, pictures by content, notes and line
// breaks), for round-trip checks of the markup readers and writers: format → model → format → model gives the same
// shape. Empty paragraphs (the writers leave them out) and bookmarks are not part of it.

using System.Security.Cryptography;
using System.Text;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Markup;

namespace Filee.Engines.Tests;

internal static class MarkupShape
{
    public static string Of(HDocument document)
    {
        var sb = new StringBuilder();
        foreach (var block in document.Sections.SelectMany(s => s.Blocks))
            Block(block, sb, 0);
        return sb.ToString();
    }

    private static void Block(HBlock block, StringBuilder sb, int depth)
    {
        switch (block)
        {
            case HParagraph paragraph when MarkupBlocks.IsEmpty(paragraph) && !paragraph.Inlines.Any(i => i is HShape):
                break;
            case HParagraph paragraph:
                {
                    sb.Append(' ', depth * 2).Append('P');
                    if (paragraph.HeadingLevel > 0)
                        sb.Append(" h").Append(paragraph.HeadingLevel);
                    if (paragraph.List is { } list)
                    {
                        var definition = list.Numbering.Levels[Math.Min(list.Level, list.Numbering.Levels.Count - 1)];
                        sb.Append(" list").Append(list.Level).Append(!list.Numbered ? "+" : definition.Bullet ? "*" : "#");
                    }
                    else if ((paragraph.Format.Left ?? 0) >= MarkupBlocks.QuoteIndent)
                    {
                        sb.Append(" quote").Append((paragraph.Format.Left ?? 0) / MarkupBlocks.QuoteIndent);
                    }
                    if (MarkupBlocks.IsCode(paragraph))
                        sb.Append(" code");
                    sb.Append(": ").Append(Inlines(paragraph.Inlines).Trim()).Append('\n');
                    break;
                }
            case HTable table:
                sb.Append(' ', depth * 2).Append("TABLE\n");
                foreach (var row in table.Rows)
                {
                    sb.Append(' ', depth * 2 + 2).Append(row.Header ? "HEADER" : "ROW").Append('\n');
                    foreach (var cell in row.Cells)
                    {
                        sb.Append(' ', depth * 2 + 4).Append("CELL ").Append(cell.RowSpan).Append('x').Append(cell.ColSpan).Append('\n');
                        foreach (var inner in cell.Blocks)
                            Block(inner, sb, depth + 3);
                    }
                }
                break;
        }
    }

    private static string Inlines(List<HInline> inlines)
    {
        var sb = new StringBuilder();
        string? open = null;
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case HText text:
                    {
                        var marks = Marks(text.Format);
                        if (marks != open)
                        {
                            if (open is { Length: > 0 })
                                sb.Append('}');
                            if (marks.Length > 0)
                                sb.Append(marks).Append('{');
                            open = marks;
                        }
                        sb.Append(text.Text);
                        continue;
                    }
                case HBookmark:
                    continue;
            }
            if (open is { Length: > 0 })
                sb.Append('}');
            open = null;
            switch (inline)
            {
                case HLineBreak:
                    sb.Append('⏎');
                    break;
                case HLink link:
                    sb.Append("link(").Append(link.Target).Append("){").Append(Inlines(link.Content)).Append('}');
                    break;
                case HImage image:
                    sb.Append("img(").Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(image.Path)))[..12]).Append(')');
                    break;
                case HNote note:
                    sb.Append("note{").Append(string.Join(" ", note.Blocks.OfType<HParagraph>().Select(p => Inlines(p.Inlines).Trim()))).Append('}');
                    break;
                case HShape { Kind: HShapeKind.Line }:
                    sb.Append("----");
                    break;
            }
        }
        if (open is { Length: > 0 })
            sb.Append('}');
        return sb.ToString();
    }

    private static string Marks(HCharFormat format)
    {
        var sb = new StringBuilder();
        if (format.Bold is true)
            sb.Append('B');
        if (format.Italic is true)
            sb.Append('I');
        if (format.Underline is true)
            sb.Append('U');
        if (format.Strike is true)
            sb.Append('S');
        if (format.Superscript is true)
            sb.Append('^');
        else if (format.Subscript is true)
            sb.Append('_');
        if (format.Shade == HtmlReader.CodeShade)
            sb.Append('C');
        return sb.ToString();
    }
}
