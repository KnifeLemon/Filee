// HDocument → plain text: paragraphs separated by an empty line, list markers and numbers written out, table rows
// as tab-separated cells, text boxes on lines of their own and footnotes collected at the end. Used for e-books and
// HWP / HWPX → TXT. WriteLines is a word processor's "plain text" instead (RTF, Word and ODT → TXT): a line per
// paragraph, an empty paragraph an empty line, and the text's own spaces and tabs as they are.

using System.Globalization;
using System.Text;

namespace Filee.Engines.Hwp.Hwpx;

internal static class PlainTextWriter
{
    /// <summary>The text of a document, its paragraphs separated by an empty line.</summary>
    public static string Write(HDocument document) => Write(document, lines: false);

    /// <summary>
    /// The text of a document as a word processor saves it as plain text: a line per paragraph and per line break, an
    /// empty paragraph an empty line, leading spaces, runs of spaces and tabs kept, no indentation added.
    /// </summary>
    public static string WriteLines(HDocument document) => Write(document, lines: true);

    private static string Write(HDocument document, bool lines)
    {
        Nesting.Limit(document);
        var sb = new StringBuilder();
        var notes = new List<string>();
        var numbers = new ListNumbers();
        foreach (var section in document.Sections)
        {
            if (lines)
                Lines(section.Blocks, sb, notes, numbers);
            else
                Blocks(section.Blocks, sb, notes, numbers);
        }
        if (notes.Count > 0)
        {
            sb.AppendLine().AppendLine("----");
            for (var i = 0; i < notes.Count; i++)
                sb.AppendLine($"[{i + 1}] {notes[i]}");
        }
        // Line by line, only the empty paragraphs at the end go: the last line keeps its own spaces.
        return (lines ? sb.ToString().TrimEnd('\r', '\n') : sb.ToString().TrimEnd()) + Environment.NewLine;
    }

    /// <summary>The blocks for <see cref="WriteLines"/>: every paragraph and table row ends with a line end.</summary>
    private static void Lines(List<HBlock> blocks, StringBuilder sb, List<string> notes, ListNumbers numbers)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HParagraph paragraph:
                    {
                        var text = Inlines(paragraph.Inlines, notes, trim: false);
                        if (paragraph.Inlines.Count > 0 && paragraph.Inlines[^1] is HTextBox)
                            text = text.TrimEnd('\n'); // the box's own line end, not an empty line
                        if (paragraph.Inlines.Any(i => i is HShape { Kind: HShapeKind.Line }) && text.Trim().Length == 0)
                            text = "* * *";
                        if (paragraph.List is { } list)
                        {
                            var marker = list.Numbered ? numbers.Next(list) + " " : "";
                            var indent = new string(' ', list.Level * 4);
                            sb.Append(indent).Append(marker)
                              .Append(text.Replace("\n", Environment.NewLine + indent + new string(' ', marker.Length)));
                        }
                        else
                        {
                            sb.Append(text.Replace("\n", Environment.NewLine));
                        }
                        sb.AppendLine();
                        break;
                    }
                case HTable { Rows.Count: > 0 } table:
                    foreach (var row in table.Rows)
                        sb.AppendJoin('\t', row.Cells.Select(c => Cell(c, notes))).AppendLine();
                    break;
            }
        }
    }

    private static void Blocks(List<HBlock> blocks, StringBuilder sb, List<string> notes, ListNumbers numbers)
    {
        HBlock? previous = null;
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HParagraph paragraph:
                    {
                        var text = Inlines(paragraph.Inlines, notes);
                        if (paragraph.Inlines.Any(i => i is HShape { Kind: HShapeKind.Line }) && text.Length == 0)
                            text = "* * *";
                        if (text.Trim().Length == 0 && paragraph.List is null)
                            break; // spacing paragraphs: the empty line between paragraphs already separates them
                        // List items and code lines stay together; everything else is separated by an empty line.
                        var together = previous is HParagraph before
                                       && ((before.List is not null && paragraph.List is not null) || (IsCode(before) && IsCode(paragraph)));
                        if (sb.Length > 0)
                            sb.AppendLine();
                        if (sb.Length > 0 && !together)
                            sb.AppendLine();
                        var indent = Math.Max(0, paragraph.Format.Left ?? 0) / 1000;
                        if (paragraph.List is { } list)
                        {
                            var marker = list.Numbered ? numbers.Next(list) + " " : "";
                            sb.Append(new string(' ', list.Level * 4 + indent)).Append(marker)
                              .Append(text.Replace("\n", Environment.NewLine + new string(' ', list.Level * 4 + indent + marker.Length)));
                        }
                        else
                        {
                            sb.Append(new string(' ', indent)).Append(text.Replace("\n", Environment.NewLine + new string(' ', indent)));
                        }
                        previous = paragraph;
                        break;
                    }
                case HTable { Rows.Count: > 0 } table:
                    if (sb.Length > 0)
                        sb.AppendLine().AppendLine();
                    sb.AppendJoin(Environment.NewLine, table.Rows.Select(row => string.Join('\t', row.Cells.Select(c => Cell(c, notes)))));
                    previous = table;
                    break;
            }
        }
    }

    private static bool IsCode(HParagraph paragraph) =>
        paragraph.Inlines.Count > 0 && paragraph.Inlines.All(i => i is HText { Format.Shade: HtmlReader.CodeShade });

    /// <summary>A cell's text on one line: its paragraphs, and the cells of a table inside it, separated by a space.</summary>
    private static string Cell(HCell cell, List<string> notes) => string.Join(" ", CellTexts(cell.Blocks, notes));

    private static IEnumerable<string> CellTexts(List<HBlock> blocks, List<string> notes)
    {
        foreach (var block in blocks)
        {
            IEnumerable<string> texts = block switch
            {
                HParagraph p => [Inlines(p.Inlines, notes).Replace('\n', ' ').Replace('\t', ' ')],
                // A table in the cell (Word, ODT and RTF nest them); Nesting.Limit bounds how deep this goes.
                HTable table => table.Rows.SelectMany(r => r.Cells).SelectMany(c => CellTexts(c.Blocks, notes)),
                _ => [],
            };
            foreach (var text in texts)
            {
                if (text.Length > 0)
                    yield return text;
            }
        }
    }

    /// <summary>
    /// The text of inlines, lines separated by '\n' (the callers write the line ends), without the white space at the
    /// end unless <paramref name="trim"/> is false.
    /// </summary>
    private static string Inlines(List<HInline> inlines, List<string> notes, bool trim = true)
    {
        var sb = new StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case HText text:
                    sb.Append(text.Text);
                    break;
                case HLineBreak:
                    sb.Append('\n');
                    break;
                case HTab:
                    sb.Append('\t');
                    break;
                case HLink link:
                    sb.Append(Inlines(link.Content, notes));
                    break;
                case HNote note:
                    {
                        var content = new StringBuilder();
                        Blocks(note.Blocks, content, notes, new ListNumbers());
                        notes.Add(content.ToString().Trim().Replace(Environment.NewLine + Environment.NewLine, " "));
                        sb.Append('[').Append(notes.Count).Append(']');
                        break;
                    }
                case HTextBox box:
                    {
                        // A text box is text of its own: on its own lines, apart from the paragraph it is anchored in.
                        var lines = box.Blocks.OfType<HParagraph>().Select(p => Inlines(p.Inlines, notes)).Where(t => t.Length > 0).ToList();
                        if (lines.Count == 0)
                            break;
                        if (sb.Length > 0 && sb[^1] != '\n')
                            sb.Append('\n');
                        sb.AppendJoin('\n', lines).Append('\n');
                        break;
                    }
            }
        }
        return trim ? sb.ToString().TrimEnd() : sb.ToString();
    }
}

/// <summary>Counts list items and formats their numbers like 한글 does ("1.", "가.", "iv)", "●").</summary>
internal sealed class ListNumbers
{
    private readonly Dictionary<HNumbering, int[]> _counters = new(ReferenceEqualityComparer.Instance);

    /// <summary>The marker of the next numbered item of a list.</summary>
    public string Next(HListRef item)
    {
        if (item.Numbering.Levels.Count == 0)
            return "•";
        var level = Math.Min(item.Level, item.Numbering.Levels.Count - 1);
        var definition = item.Numbering.Levels[level];
        if (definition.Bullet)
            return definition.Text.Length > 0 ? definition.Text : "•";

        if (!_counters.TryGetValue(item.Numbering, out var counters))
            _counters[item.Numbering] = counters = new int[Math.Max(10, item.Numbering.Levels.Count)];
        counters[level] = counters[level] == 0 ? definition.Start : counters[level] + 1;
        for (var deeper = level + 1; deeper < counters.Length; deeper++)
            counters[deeper] = 0;

        var text = definition.Text;
        for (var l = Math.Min(9, item.Numbering.Levels.Count); l >= 1; l--)
        {
            var value = counters[l - 1] == 0 ? item.Numbering.Levels[l - 1].Start : counters[l - 1];
            text = text.Replace($"^{l}", Format(value, item.Numbering.Levels[l - 1].Format), StringComparison.Ordinal);
        }
        return text;
    }

    /// <summary>The marker <see cref="Next"/> would give, without counting the item (to measure it ahead).</summary>
    public string Peek(HListRef item)
    {
        var saved = _counters.TryGetValue(item.Numbering, out var counters) ? (int[])counters.Clone() : null;
        var text = Next(item);
        if (saved is null)
            _counters.Remove(item.Numbering);
        else
            _counters[item.Numbering] = saved;
        return text;
    }

    /// <summary>A number in an OWPML number format.</summary>
    public static string Format(int value, string format) => format switch
    {
        "LATIN_SMALL" => Letters(value, 'a'),
        "LATIN_CAPITAL" => Letters(value, 'A'),
        "ROMAN_SMALL" => Roman(value).ToLowerInvariant(),
        "ROMAN_CAPITAL" => Roman(value),
        "HANGUL_SYLLABLE" => value is >= 1 and <= 14 ? "가나다라마바사아자차카타파하"[value - 1].ToString() : value.ToString(CultureInfo.InvariantCulture),
        "CIRCLED_DIGIT" => value is >= 1 and <= 20 ? ((char)('①' + value - 1)).ToString() : value.ToString(CultureInfo.InvariantCulture),
        _ => value.ToString(CultureInfo.InvariantCulture),
    };

    private static string Letters(int value, char first)
    {
        if (value < 1)
            return value.ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        for (; value > 0; value = (value - 1) / 26)
            sb.Insert(0, (char)(first + (value - 1) % 26));
        return sb.ToString();
    }

    private static string Roman(int value)
    {
        if (value is < 1 or > 3999)
            return value.ToString(CultureInfo.InvariantCulture);
        (int Value, string Text)[] numerals = [(1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")];
        var sb = new StringBuilder();
        foreach (var (number, text) in numerals)
            for (; value >= number; value -= number)
                sb.Append(text);
        return sb.ToString();
    }
}
