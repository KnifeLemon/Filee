// HDocument → Markdown (the dialect MarkdownReader reads back: GitHub tables, ~sub~, ^sup^, footnotes): headings,
// emphasis, code, lists, block quotes, pipe tables (with column alignment), links, pictures and footnotes. Used for
// e-books, PDF and HWP / HWPX → MD, and TXTZ.

using System.Text;

namespace Filee.Engines.Hwp.Hwpx;

internal sealed class MarkdownWriter
{
    private readonly Func<string, string?> _imageTarget;
    private readonly List<string> _notes = [];
    private readonly ListNumbers _numbers = new();
    private readonly Markup.MarkupWriteBudget _budget;

    private MarkdownWriter(Func<string, string?> imageTarget, Markup.MarkupWriteBudget? budget)
    {
        _imageTarget = imageTarget;
        _budget = budget ?? new(Documents.DocumentLimits.Default);
    }

    /// <summary>The Markdown text of a document.</summary>
    /// <param name="imageTarget">Maps a picture file to the path written into the Markdown, or null to leave it out.</param>
    /// <param name="budget">
    /// The caps of the document this text is part of (a notebook's or outline's pieces count together), or null for a
    /// document of its own.
    /// </param>
    public static string Write(HDocument document, Func<string, string?> imageTarget, Markup.MarkupWriteBudget? budget = null)
    {
        Nesting.Limit(document);
        var writer = new MarkdownWriter(imageTarget, budget);
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(document.Title))
            sb.Append("---\ntitle: \"").Append(document.Title.Replace("\"", "\\\"", StringComparison.Ordinal)).Append("\"\n---\n\n");
        foreach (var section in document.Sections)
            writer.Blocks(section.Blocks, sb, "");
        for (var i = 0; i < writer._notes.Count; i++)
            sb.Append($"\n[^{i + 1}]: ").Append(writer._notes[i]).Append('\n');
        return sb.ToString().TrimEnd() + "\n";
    }

    private void Blocks(List<HBlock> blocks, StringBuilder sb, string prefix)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            switch (blocks[i])
            {
                case HParagraph paragraph when IsCode(paragraph):
                    {
                        var lines = new List<string>();
                        for (; i < blocks.Count && blocks[i] is HParagraph code && IsCode(code); i++)
                            lines.Add(string.Concat(code.Inlines.OfType<HText>().Select(t => t.Text)));
                        i--;
                        var fence = lines.Any(l => l.Contains("```", StringComparison.Ordinal)) ? "~~~~" : "```";
                        sb.Append(prefix).Append(fence).Append('\n');
                        foreach (var line in lines)
                            sb.Append(prefix).Append(line).Append('\n');
                        sb.Append(prefix).Append(fence).Append("\n\n");
                        break;
                    }
                case HParagraph paragraph:
                    Paragraph(paragraph, sb, prefix, i + 1 < blocks.Count ? blocks[i + 1] : null);
                    break;
                case HTable table:
                    Table(table, sb, prefix);
                    break;
            }
        }
    }

    private static bool IsCode(HParagraph paragraph) =>
        paragraph is { List: null, HeadingLevel: 0 } && paragraph.Inlines.Count > 0
        && paragraph.Inlines.All(i => i is HText { Format.Shade: HtmlReader.CodeShade });

    private void Paragraph(HParagraph paragraph, StringBuilder sb, string prefix, HBlock? next)
    {
        var text = Inlines(paragraph.Inlines);
        if (paragraph.Inlines.Any(i => i is HShape { Kind: HShapeKind.Line }) && text.Trim().Length == 0)
        {
            sb.Append(prefix).Append("---\n\n");
            return;
        }
        if (text.Trim().Length == 0)
            return;

        // Block quotes: the readers indent quotes by 2000 HWPUNIT per level.
        var quote = paragraph.List is null ? string.Concat(Enumerable.Repeat("> ", Math.Max(0, paragraph.Format.Left ?? 0) / 2000)) : "";
        var linePrefix = prefix + quote;
        if (paragraph.HeadingLevel > 0)
        {
            sb.Append(linePrefix).Append('#', Math.Clamp(paragraph.HeadingLevel, 1, 6)).Append(' ').Append(text.Replace("\\\n", " ", StringComparison.Ordinal)).Append("\n\n");
            return;
        }
        if (paragraph.List is { } list)
        {
            var indent = new string(' ', list.Level * 4);
            var marker = !list.Numbered ? "" : IsBullet(list) ? "- " : OrderedMarker(list);
            var continuation = linePrefix + indent + new string(' ', Math.Max(marker.Length, list.Numbered ? 0 : 4));
            sb.Append(list.Numbered ? linePrefix + indent + marker : continuation)
              .Append(EscapeLineStarts(text).Replace("\n", "\n" + continuation, StringComparison.Ordinal)).Append('\n');
            // Items of one list stay together; a blank line ends the list.
            if (next is not HParagraph { List: not null })
                sb.Append('\n');
            return;
        }
        sb.Append(linePrefix).Append(EscapeLineStarts(text).Replace("\n", "\n" + linePrefix, StringComparison.Ordinal)).Append("\n\n");
    }

    private static bool IsBullet(HListRef list) =>
        list.Numbering.Levels.Count == 0 || list.Numbering.Levels[Math.Min(list.Level, list.Numbering.Levels.Count - 1)].Bullet;

    /// <summary>"3. " — Markdown only knows numbers, so letters and roman numerals become their position.</summary>
    private string OrderedMarker(HListRef list)
    {
        var level = list.Numbering.Levels[Math.Min(list.Level, list.Numbering.Levels.Count - 1)];
        var number = _numbers.Next(list);
        var digits = new string(number.Where(char.IsAsciiDigit).ToArray());
        return (level.Format == "DIGIT" && digits.Length > 0 ? digits : "1") + ". ";
    }

    private void Table(HTable table, StringBuilder sb, string prefix)
    {
        if (table.Rows.Count == 0)
            return;
        // The grid is counted before it is made, and spans are placed arithmetically (a column remembers the row it is
        // free again from), so spans that are huge or overlap cost no more than the grid.
        var columns = _budget.Columns(table);
        _budget.Table(table.Rows.Count, columns);
        var grid = new List<string[]>(table.Rows.Count);
        var alignments = new ColumnAlignments(columns);
        var cursor = new Markup.SpanCursor(table.Rows.Count, columns);
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var cells = new string[columns];
            cursor.NextRow();
            foreach (var cell in table.Rows[r].Cells)
            {
                if (!cursor.Place(cell.RowSpan, cell.ColSpan, table.Rows.Count, out var column, out _, out _))
                    break;
                if (cell.ColSpan <= 1)
                    alignments.Add(column, cell);
                // Header cells are bold by nature: no ** inside them. A cell is one line: its line breaks are <br>,
                // which MarkdownReader and the usual Markdown renderers read as breaks.
                var header = table.Rows[r].Header;
                cells[column] = string.Join(" ", cell.Blocks.OfType<HParagraph>()
                        .Select(p => header ? p.Inlines.Select(i => i is HText t ? new HText(t.Text, t.Format with { Bold = null }) : i).ToList() : p.Inlines)
                        .Select(inlines => Inlines(inlines).Replace(HardBreak, "<br>", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)))
                    .Replace("|", "\\|", StringComparison.Ordinal);
            }
            grid.Add(cells);
        }

        sb.Append(prefix).Append("| ").AppendJoin(" | ", grid[0].Select(c => c ?? "")).Append(" |\n");
        sb.Append(prefix).Append('|');
        for (var c = 0; c < columns; c++)
            sb.Append(alignments.Delimiter(c)).Append('|');
        sb.Append('\n');
        foreach (var row in grid.Skip(1))
            sb.Append(prefix).Append("| ").AppendJoin(" | ", row.Select(c => c ?? "")).Append(" |\n");
        sb.Append('\n');
    }

    /// <summary>
    /// The alignment each column's cells share, written as the column's delimiter (":---:" centred, "---:" right). Left
    /// is how Markdown shows a column anyway, so a left, justified or unaligned column stays "---" (documents read from
    /// HWPX or Word align most cells left or justified; ":---" on every column would only be noise), and so does a
    /// column whose cells differ. Cells spanning columns don't count.
    /// </summary>
    private sealed class ColumnAlignments(int columns)
    {
        private readonly HAlign?[] _align = new HAlign?[columns];
        private readonly bool[] _seen = new bool[columns];
        private readonly bool[] _mixed = new bool[columns];

        public void Add(int column, HCell cell)
        {
            foreach (var paragraph in cell.Blocks.OfType<HParagraph>())
            {
                // An empty paragraph shows no alignment.
                if (paragraph.Inlines.Count == 0)
                    continue;
                // Right to left, the model's Left is the start side: the right edge (HParaFormat.RightToLeft).
                HAlign? align = (paragraph.Format.Align, paragraph.Format.RightToLeft is true) switch
                {
                    (HAlign.Center, _) => HAlign.Center,
                    (HAlign.Right, false) or (HAlign.Left, true) => HAlign.Right,
                    _ => null,
                };
                if (!_seen[column])
                    (_seen[column], _align[column]) = (true, align);
                else if (_align[column] != align)
                    _mixed[column] = true;
            }
        }

        public string Delimiter(int column) => _mixed[column] ? "---" : _align[column] switch
        {
            HAlign.Center => ":---:",
            HAlign.Right => "---:",
            _ => "---",
        };
    }

    private string Inlines(List<HInline> inlines)
    {
        var sb = new StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case HText text:
                    sb.Append(Run(text.Text, text.Format));
                    break;
                case HLineBreak:
                    sb.Append("\\\n");
                    break;
                case HTab:
                    sb.Append("    ");
                    break;
                case HLink link:
                    {
                        var content = Inlines(link.Content);
                        // Bookmarks have no Markdown form: links inside the document keep their text only.
                        sb.Append(link.Target.StartsWith('#') || content.Trim().Length == 0 ? content : $"[{content}](<{link.Target}>)");
                        break;
                    }
                case HImage image:
                    if (_imageTarget(image.Path) is { } target)
                        sb.Append($"![](<{target}>)");
                    break;
                case HNote note:
                    {
                        var content = new StringBuilder();
                        Blocks(note.Blocks, content, "");
                        _notes.Add(content.ToString().Trim().Replace("\n\n", "\n    ", StringComparison.Ordinal));
                        sb.Append($"[^{_notes.Count}]");
                        break;
                    }
                case HTextBox box:
                    {
                        // A text box is text of its own: its lines are kept apart from the paragraph it is anchored in.
                        var lines = box.Blocks.OfType<HParagraph>().Select(p => Inlines(p.Inlines)).Where(t => t.Trim().Length > 0).ToList();
                        if (lines.Count == 0)
                            break;
                        if (sb.Length > 0 && !EndsWithBreak(sb))
                            sb.Append(HardBreak);
                        sb.AppendJoin(HardBreak, lines).Append(HardBreak);
                        break;
                    }
            }
        }
        // A line break that ends the text breaks nothing.
        while (EndsWithBreak(sb))
            sb.Length -= HardBreak.Length;
        return sb.ToString();
    }

    /// <summary>A Markdown hard line break, as <see cref="HLineBreak"/> is written.</summary>
    private const string HardBreak = "\\\n";

    private static bool EndsWithBreak(StringBuilder sb) => sb.Length >= 2 && sb[^1] == '\n' && sb[^2] == '\\';

    private static string Run(string text, HCharFormat format)
    {
        if (text.Trim().Length == 0)
            return text;
        if (format.Shade == HtmlReader.CodeShade)
            return text.Contains('`') ? $"`` {text} ``" : $"`{text}`";

        // Emphasis markers must touch the text: spaces stay outside.
        var core = Escape(text.Trim());
        var lead = text[..(text.Length - text.TrimStart().Length)];
        var trail = text[text.TrimEnd().Length..];
        if (format.Superscript is true)
            core = $"^{core.Replace(" ", "\\ ", StringComparison.Ordinal)}^";
        else if (format.Subscript is true)
            core = $"~{core.Replace(" ", "\\ ", StringComparison.Ordinal)}~";
        if (format.Strike is true)
            core = $"~~{core}~~";
        if (format.Italic is true)
            core = $"*{core}*";
        if (format.Bold is true)
            core = $"**{core}**";
        if (format.Shade == HtmlReader.MarkShade)
            core = $"=={core}==";
        return lead + core + trail;
    }

    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '~' or '^' or '|' or '=')
                sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>
    /// <see cref="EscapeLineStart"/> for every line of a paragraph (#1416): a line after a hard break can start a
    /// heading, a list or a definition, or underline the lines above into a heading, as well as the first one.
    /// </summary>
    private static string EscapeLineStarts(string text) =>
        text.Contains(HardBreak, StringComparison.Ordinal)
            ? string.Join(HardBreak, text.Split(HardBreak).Select(EscapeLineStart))
            : EscapeLineStart(text);

    /// <summary>
    /// Text that would read as a heading, list item, definition or rule (a heading's underline after other lines) at
    /// the start of a line is escaped. Quotes, code fences and HTML are escaped with the text (<see cref="Escape"/>).
    /// </summary>
    private static string EscapeLineStart(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('#') || trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("+ ", StringComparison.Ordinal)
            || trimmed.StartsWith(": ", StringComparison.Ordinal) || trimmed is "+" or ":"
            || (trimmed.Length > 0 && trimmed.All(c => c is '-' or ' ' or '\t')))
            return "\\" + trimmed;
        var digits = trimmed.TakeWhile(char.IsAsciiDigit).Count();
        if (digits > 0 && digits < trimmed.Length && trimmed[digits] is '.' or ')')
            return trimmed[..digits] + "\\" + trimmed[digits..];
        return text;
    }
}
