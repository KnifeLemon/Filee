// Org mode (.org) → HDocument, without Pandoc. Blocks: * headlines (TODO keywords and priorities kept as text, tags
// and planning lines left out), paragraphs, lists (- + * and 1. 1) items nested by indentation, [ ] / [X] check
// boxes, "term :: description"), #+BEGIN_SRC / EXAMPLE / QUOTE / VERSE / CENTER blocks, ": " fixed-width lines,
// tables (|---+---| under the header rows), ----- rules, [fn:N] footnotes (and inline [fn::text]), #+TITLE and
// #+CAPTION. Drawers (:PROPERTIES: … :END:), comments and other #+ keywords are left out. Inline: *bold*, /italic/,
// _underline_, +strike+, =verbatim= and ~code~ with Org's rules for where emphasis may start and end, [[links][text]],
// [[file:pictures]] (local files), bare URLs, x^{sup} / x_{sub} and \\ line ends. Zero-width spaces, which Org users
// (and OrgWriter) put in to keep a character from reading as markup, are taken out.

using System.Text.RegularExpressions;
using Filee.Engines.Documents;

namespace Filee.Engines.Hwp.Hwpx.Markup;

/// <summary>Reads Org mode into the document model.</summary>
internal sealed partial class OrgReader
{
    private const int MaxInlineDepth = 12;
    private const char ZeroWidthSpace = '​';

    private static readonly HCharFormat Plain = default;

    private readonly string _baseFolder;
    private readonly DocumentBudget _budget;
    private readonly Dictionary<string, List<HBlock>> _footnotes = new(StringComparer.Ordinal);
    private readonly List<(string Label, HNote Note)> _references = [];
    private string? _title;

    private OrgReader(string baseFolder, DocumentLimits limits)
    {
        _baseFolder = baseFolder;
        _budget = new DocumentBudget(limits);
    }

    public static HDocument Read(string path, string media) =>
        Read(MarkupBlocks.ReadText(path, DocumentLimits.Default, "Org"), Path.GetDirectoryName(Path.GetFullPath(path))!, DocumentLimits.Default);

    internal static HDocument Read(string text, string baseFolder, DocumentLimits limits)
    {
        var reader = new OrgReader(baseFolder, limits);
        var lines = MarkupBlocks.Lines(text).Split('\n');
        var section = new HSection();
        reader.Blocks(lines, 0, lines.Length, section.Blocks, 0);
        foreach (var (label, note) in reader._references)
            if (reader._footnotes.TryGetValue(label, out var blocks))
                note.Blocks.AddRange(blocks);
        var document = new HDocument { Title = reader._title };
        document.Sections.Add(section);
        return document;
    }

    // ───────────────────────── Blocks ─────────────────────────

    [GeneratedRegex(@"^(\*+)\s+(.*?)\s*$")]
    private static partial Regex Headline();

    /// <summary>A headline without its tags (":work:home:" at its end).</summary>
    private static string WithoutTags(string title)
    {
        var trimmed = title.TrimEnd();
        if (!trimmed.EndsWith(':'))
            return title;
        var space = trimmed.LastIndexOfAny([' ', '\t']);
        var tags = trimmed[(space + 1)..];
        return space > 0 && tags.Length > 2 && tags[0] == ':' && tags.All(c => char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '%' or ':')
            ? trimmed[..space].TrimEnd()
            : title;
    }

    [GeneratedRegex(@"^\[#[A-Z0-9]\]\s*")]
    private static partial Regex Priority();

    [GeneratedRegex(@"^(\s*)([-+*]|\d{1,9}[.)]|[A-Za-z][.)])(?:\s+(.*))?$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\s*#\+(BEGIN|begin)_(\w+)(.*)$")]
    private static partial Regex BlockStart();

    [GeneratedRegex(@"^\s*#\+(\w+):\s*(.*)$")]
    private static partial Regex Keyword();

    [GeneratedRegex(@"^\[fn:([^\]\s]{1,100})\]\s*(.*)$")]
    private static partial Regex FootnoteDefinition();

    [GeneratedRegex(@"^\s*(SCHEDULED|DEADLINE|CLOSED):")]
    private static partial Regex Planning();

    /// <summary>Blocks of lines [start, end); <paramref name="indent"/> is the left indent they get (quotes).</summary>
    private void Blocks(string[] lines, int start, int end, List<HBlock> output, int indent)
    {
        string? caption = null;
        for (var i = start; i < end;)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                i++;
                continue;
            }
            if (Headline().Match(line) is { Success: true } headline && indent == 0)
            {
                var title = WithoutTags(headline.Groups[2].Value);
                title = Priority().Replace(title, "");
                var paragraph = Paragraph(title, 0);
                paragraph.HeadingLevel = Math.Min(headline.Groups[1].Length, 9);
                output.Add(paragraph);
                i++;
                // Planning lines and drawers right under a headline are not text.
                while (i < end && Planning().IsMatch(lines[i]))
                    i++;
                continue;
            }
            if (trimmed.StartsWith("# ", StringComparison.Ordinal) || trimmed == "#" || Planning().IsMatch(line))
            {
                i++;
                continue;
            }
            if (Drawer(trimmed) && FindLine(lines, i + 1, end, l => l.Trim().Equals(":END:", StringComparison.OrdinalIgnoreCase)) is var drawerEnd and >= 0)
            {
                i = drawerEnd + 1;
                continue;
            }
            if (BlockStart().Match(line) is { Success: true } block)
            {
                i = Block(lines, i, end, block, output, indent);
                continue;
            }
            if (Keyword().Match(line) is { Success: true } keyword)
            {
                var name = keyword.Groups[1].Value.ToUpperInvariant();
                if (name == "TITLE")
                    _title = _title is null ? keyword.Groups[2].Value.Trim() : _title + " " + keyword.Groups[2].Value.Trim();
                else if (name == "CAPTION")
                    caption = keyword.Groups[2].Value.Trim();
                i++;
                continue;
            }
            if (FootnoteDefinition().Match(line) is { Success: true } footnote)
            {
                var text = new List<string> { footnote.Groups[2].Value };
                i++;
                for (; i < end && lines[i].Trim().Length > 0 && !FootnoteDefinition().IsMatch(lines[i]) && !Headline().IsMatch(lines[i]); i++)
                    text.Add(lines[i].Trim());
                var blocks = new List<HBlock>();
                using (_budget.Enter())
                    blocks.Add(Paragraph(string.Join(' ', text), 0));
                _footnotes.TryAdd(footnote.Groups[1].Value, blocks);
                continue;
            }
            if (trimmed.StartsWith('|'))
            {
                var tableStart = i;
                while (i < end && lines[i].TrimStart().StartsWith('|'))
                    i++;
                var table = Table(lines[tableStart..i], indent);
                if (caption is not null)
                {
                    table.Caption.Add(Paragraph(caption, 0));
                    caption = null;
                }
                output.Add(table);
                continue;
            }
            if (trimmed.Length >= 5 && trimmed.All(c => c == '-'))
            {
                MarkupBlocks.Rule(output, _budget);
                i++;
                continue;
            }
            if (trimmed == ":" || trimmed.StartsWith(": ", StringComparison.Ordinal))
            {
                var code = new List<string>();
                for (; i < end && (lines[i].Trim() == ":" || lines[i].TrimStart().StartsWith(": ", StringComparison.Ordinal)); i++)
                    code.Add(lines[i].TrimStart().Length > 1 ? lines[i].TrimStart()[2..] : "");
                MarkupBlocks.SeparateCode(output, _budget);
                MarkupBlocks.CodeLines(code, output, _budget, indent);
                continue;
            }
            if (IsListItem(line))
            {
                i = List(lines, i, end, output, indent);
                continue;
            }

            var paragraphLines = new List<string>();
            for (; i < end; i++)
            {
                var next = lines[i];
                if (next.Trim().Length == 0 || (paragraphLines.Count > 0 && StartsBlock(next)))
                    break;
                paragraphLines.Add(next.Trim());
            }
            var hParagraph = Paragraph(string.Join('\n', paragraphLines), indent);
            output.Add(hParagraph);
            if (caption is not null && hParagraph.Inlines.Any(x => x is HImage))
                caption = null;
        }
    }

    private static bool Drawer(string trimmed) =>
        trimmed.Length > 2 && trimmed[0] == ':' && trimmed[^1] == ':' && trimmed[1..^1].All(c => char.IsLetterOrDigit(c) || c is '-' or '_');

    private static int FindLine(string[] lines, int start, int end, Func<string, bool> match)
    {
        for (var i = start; i < end; i++)
            if (match(lines[i]))
                return i;
        return -1;
    }

    private static bool StartsBlock(string line) =>
        Headline().IsMatch(line) || BlockStart().IsMatch(line) || Keyword().IsMatch(line) || line.TrimStart().StartsWith('|')
        || IsListItem(line) || FootnoteDefinition().IsMatch(line) || line.TrimStart().StartsWith(": ", StringComparison.Ordinal)
        || line.TrimStart().StartsWith("# ", StringComparison.Ordinal) || line.Trim() == "#";

    /// <summary>A list item: "* " only when indented (at the start of a line it is a headline).</summary>
    private static bool IsListItem(string line) =>
        ListItem().Match(line) is { Success: true } item && !(item.Groups[2].Value == "*" && item.Groups[1].Length == 0)
        && (item.Groups[3].Success || item.Groups[2].Value is "-" or "+");

    /// <summary>A #+BEGIN_… block up to its #+END_…; returns the index after it.</summary>
    private int Block(string[] lines, int i, int end, Match start, List<HBlock> output, int indent)
    {
        var kind = start.Groups[2].Value.ToUpperInvariant();
        var close = FindLine(lines, i + 1, end, l => l.Trim().Equals("#+END_" + kind, StringComparison.OrdinalIgnoreCase));
        var stop = close < 0 ? end : close;
        var after = close < 0 ? end : close + 1;
        // Lines inside a block that start with "*" or "#+" are written with a "," in front.
        var content = lines[(i + 1)..stop].Select(l => l.StartsWith(",*", StringComparison.Ordinal) || l.StartsWith(",#+", StringComparison.Ordinal) ? l[1..] : l).ToArray();
        switch (kind)
        {
            case "SRC" or "EXAMPLE" or "EXPORT":
                {
                    var common = content.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
                    MarkupBlocks.SeparateCode(output, _budget);
                    MarkupBlocks.CodeLines(content.Select(l => l.Length >= common ? l[common..] : l.TrimStart()), output, _budget, indent);
                    break;
                }
            case "COMMENT":
                break;
            case "QUOTE" or "CENTER":
                using (_budget.Enter())
                    Blocks(content, 0, content.Length, output, Math.Min(indent + (kind == "QUOTE" ? MarkupBlocks.QuoteIndent : 0), 8 * MarkupBlocks.QuoteIndent));
                break;
            case "VERSE":
                output.Add(Paragraph(string.Join("\\\\\n", content.Select(l => l.Trim())), indent));
                break;
            default:
                using (_budget.Enter())
                    Blocks(content, 0, content.Length, output, indent);
                break;
        }
        return after;
    }

    private HParagraph Paragraph(string text, int indent)
    {
        _budget.Paragraph();
        var paragraph = new HParagraph { Format = indent > 0 ? new HParaFormat(Left: indent) : default };
        var sink = new InlineSink(paragraph.Inlines);
        Inlines(new Closers(text), 0, text.Length, Plain, sink, 0);
        sink.Flush();
        Clean(paragraph.Inlines);
        return paragraph;
    }

    /// <summary>Zero-width spaces out, a paragraph's lines joined by spaces (Org's line ends are not breaks).</summary>
    private static void Clean(List<HInline> inlines)
    {
        for (var i = 0; i < inlines.Count; i++)
        {
            if (inlines[i] is HText text && text.Text.Contains(ZeroWidthSpace, StringComparison.Ordinal))
                inlines[i] = new HText(text.Text.Replace(ZeroWidthSpace.ToString(), "", StringComparison.Ordinal), text.Format);
            else if (inlines[i] is HLink link)
                Clean(link.Content);
        }
        inlines.RemoveAll(i => i is HText { Text.Length: 0 });
    }

    /// <summary>A list from line <paramref name="i"/>: items by indentation, their further lines and nested lists.</summary>
    private int List(string[] lines, int i, int end, List<HBlock> output, int indent, int level = 0)
    {
        var first = ListItem().Match(lines[i]);
        var column = first.Groups[1].Length;
        var ordered = char.IsAsciiDigit(first.Groups[2].Value[0]) || char.IsLetter(first.Groups[2].Value[0]);
        var numbering = MarkupBlocks.Numbering(ordered, Start(first.Groups[2].Value), Format(first.Groups[2].Value), Math.Min(level, 8));
        var blanks = 0;
        while (i < end)
        {
            var line = lines[i];
            if (line.Trim().Length == 0)
            {
                // Two blank lines end a list.
                if (++blanks >= 2)
                    break;
                i++;
                continue;
            }
            var lineIndent = line.Length - line.TrimStart().Length;
            if (!IsListItem(line) || lineIndent != column)
            {
                if (lineIndent <= column)
                    break;
                // A nested list, or more of the item before.
                if (IsListItem(line))
                {
                    using (_budget.Enter())
                        i = List(lines, i, end, output, indent, level + 1);
                    blanks = 0;
                    continue;
                }
                var more = new List<string>();
                for (; i < end && lines[i].Trim().Length > 0 && (lines[i].Length - lines[i].TrimStart().Length) > column && !IsListItem(lines[i]); i++)
                    more.Add(lines[i].Trim());
                if (more.Count > 0 && output.Count > 0 && output[^1] is HParagraph { List: not null } previous && blanks == 0)
                {
                    var sink = new InlineSink(previous.Inlines);
                    sink.Text(" ", Plain);
                    var text = string.Join(' ', more);
                    Inlines(new Closers(text), 0, text.Length, Plain, sink, 0);
                    sink.Flush();
                    Clean(previous.Inlines);
                }
                else if (more.Count > 0)
                {
                    var paragraph = Paragraph(string.Join(' ', more), indent);
                    paragraph.List = new HListRef(numbering, Math.Min(level, 8), Numbered: false);
                    output.Add(paragraph);
                }
                blanks = 0;
                continue;
            }
            var item = ListItem().Match(line);
            // An item of the other kind (numbers after bullets) starts a list of its own.
            if ((char.IsAsciiDigit(item.Groups[2].Value[0]) || char.IsLetter(item.Groups[2].Value[0])) != ordered)
                break;
            blanks = 0;
            var content = item.Groups[3].Success ? item.Groups[3].Value : "";
            // Check boxes and "term :: description".
            content = CheckBox().Replace(content, m => m.Groups[1].Value is "X" or "x" ? "☑ " : m.Groups[1].Value == "-" ? "☒ " : "☐ ");
            var split = !ordered ? content.IndexOf(" :: ", StringComparison.Ordinal) : -1;
            HParagraph hItem;
            if (split > 0)
            {
                hItem = Paragraph(content[..split], indent);
                for (var k = 0; k < hItem.Inlines.Count; k++)
                    if (hItem.Inlines[k] is HText t)
                        hItem.Inlines[k] = new HText(t.Text, t.Format with { Bold = true });
                var sink = new InlineSink(hItem.Inlines);
                sink.Text(": ", Plain);
                var description = content[(split + 4)..];
                Inlines(new Closers(description), 0, description.Length, Plain, sink, 0);
                sink.Flush();
                Clean(hItem.Inlines);
            }
            else
            {
                hItem = Paragraph(content, indent);
            }
            hItem.List = new HListRef(numbering, Math.Min(level, 8), Numbered: true);
            output.Add(hItem);
            i++;
        }
        return i;
    }

    private static int Start(string marker) =>
        char.IsAsciiDigit(marker[0]) && int.TryParse(marker[..^1], out var number) ? number : char.IsLetter(marker[0]) ? char.ToLowerInvariant(marker[0]) - 'a' + 1 : 1;

    private static string Format(string marker) =>
        char.IsLetter(marker[0]) ? char.IsUpper(marker[0]) ? "LATIN_CAPITAL" : "LATIN_SMALL" : "DIGIT";

    [GeneratedRegex(@"^\[([ Xx-])\]\s+")]
    private static partial Regex CheckBox();

    private HTable Table(string[] lines, int indent)
    {
        _budget.Object();
        var table = new HTable { Indent = indent };
        var limit = _budget.Limits.MaxTableColumns;
        var headerRows = 0;
        var columns = 0;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("|-", StringComparison.Ordinal))
            {
                // Rows above the first rule are the header.
                if (headerRows == 0)
                    headerRows = table.Rows.Count;
                continue;
            }
            line = line[1..];
            if (line.EndsWith('|'))
                line = line[..^1];
            var row = new HRow();
            foreach (var part in line.Split('|').Take(limit))
            {
                var cell = new HCell();
                using (_budget.Enter())
                    cell.Blocks.Add(Paragraph(part.Trim(), 0));
                row.Cells.Add(cell);
            }
            columns = Math.Max(columns, row.Cells.Count);
            table.Rows.Add(row);
        }
        // A rule only under the last row (or none) makes no header.
        if (headerRows > 0 && headerRows < table.Rows.Count)
        {
            foreach (var row in table.Rows.Take(headerRows))
            {
                row.Header = true;
                foreach (var paragraph in row.Cells.SelectMany(c => c.Blocks).OfType<HParagraph>())
                    for (var k = 0; k < paragraph.Inlines.Count; k++)
                        if (paragraph.Inlines[k] is HText t)
                            paragraph.Inlines[k] = new HText(t.Text, t.Format with { Bold = true });
            }
        }
        table.ColumnCount = Math.Max(1, columns);
        if (table.Rows.Count == 0)
            table.Rows.Add(new HRow { Cells = { new HCell { Blocks = { new HParagraph() } } } });
        return table;
    }

    // ───────────────────────── Inlines ─────────────────────────

    private static readonly Dictionary<char, Func<HCharFormat, HCharFormat>> Emphasis = new()
    {
        ['*'] = f => f with { Bold = true },
        ['/'] = f => f with { Italic = true },
        ['_'] = f => f with { Underline = true },
        ['+'] = f => f with { Strike = true },
    };

    /// <summary>
    /// Characters Org lets before an opening marker (besides white space and the line start). A zero-width space is
    /// not one: that is how a marker is kept from opening.
    /// </summary>
    private static bool Pre(char ch) => char.IsWhiteSpace(ch) || ch is '-' or '(' or '{' or '\'' or '"';

    /// <summary>Characters Org lets after a closing marker (besides white space and the line end).</summary>
    private static bool Post(char ch) => char.IsWhiteSpace(ch) || ch is '-' or '.' or ',' or ';' or ':' or '!' or '?' or '\'' or ')' or '}' or '"' or '\\' or '[';

    private void Inlines(Closers text, int start, int end, HCharFormat format, InlineSink sink, int depth)
    {
        var s = text.Text;
        for (var i = start; i < end;)
        {
            var ch = s[i];
            var before = i > start ? s[i - 1] : ' ';
            if (ch == '\n')
            {
                sink.Text(' ', format);
                i++;
                continue;
            }
            if (ch == '\\' && i + 1 < end && s[i + 1] == '\\' && (i + 2 >= end || s[i + 2] == '\n'))
            {
                sink.Add(new HLineBreak(format));
                i += i + 2 < end ? 3 : 2;
                continue;
            }
            if (ch == '[' && i + 1 < end && s[i + 1] == '[' && depth < MaxInlineDepth && Link(text, i, end, format, sink, depth) is { } afterLink)
            {
                i = afterLink;
                continue;
            }
            if (ch == '[' && i + 3 < end && string.CompareOrdinal(s, i, "[fn:", 0, 4) == 0 && FootnoteReference(text, i, end, sink, depth) is { } afterNote)
            {
                i = afterNote;
                continue;
            }
            if ((ch is '^' or '_') && i + 1 < end && s[i + 1] == '{' && i > start && !char.IsWhiteSpace(before))
            {
                var close = text.Find("}", i + 2, end);
                if (close > i + 2)
                {
                    var script = ch == '^' ? format with { Superscript = true, Subscript = null } : format with { Subscript = true, Superscript = null };
                    if (depth < MaxInlineDepth)
                        Inlines(text, i + 2, close, script, sink, depth + 1);
                    else
                        sink.Text(s[(i + 2)..close], script);
                    i = close + 1;
                    continue;
                }
            }
            if ((ch is '=' or '~') && (i == start || Pre(before)) && i + 1 < end && !char.IsWhiteSpace(s[i + 1]))
            {
                // Verbatim and code: their content is not markup.
                var marker = ch.ToString();
                var close = text.Find(marker, i + 2, end, at => at > 0 && !char.IsWhiteSpace(s[at - 1]) && (at + 1 >= s.Length || Post(s[at + 1])));
                if (close > i + 1)
                {
                    sink.Text(s[(i + 1)..close].Replace(ZeroWidthSpace.ToString(), "", StringComparison.Ordinal), format with { Shade = HtmlReader.CodeShade });
                    i = close + 1;
                    continue;
                }
            }
            if (Emphasis.TryGetValue(ch, out var apply) && depth < MaxInlineDepth && (i == start || Pre(before)) && i + 1 < end && !char.IsWhiteSpace(s[i + 1]))
            {
                var marker = ch.ToString();
                var close = text.Find(marker, i + 2, end, at => at > 0 && !char.IsWhiteSpace(s[at - 1]) && (at + 1 >= s.Length || Post(s[at + 1])));
                if (close > i + 1)
                {
                    Inlines(text, i + 1, close, apply(format), sink, depth + 1);
                    i = close + 1;
                    continue;
                }
            }
            if ((ch is 'h' or 'f' or 'm') && (i == start || !char.IsLetterOrDigit(before)) && BareUrl().Match(s, i) is { Success: true } url && url.Index + url.Length <= end)
            {
                var target = url.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')');
                var link = new HLink(target);
                link.Content.Add(new HText(target, format));
                sink.Add(link);
                i += target.Length;
                continue;
            }
            sink.Text(ch, format);
            i++;
        }
    }

    [GeneratedRegex(@"\G\[\[((?:[^\]\[\\]|\\.){1,4000})\](?:\[([^\n]{0,4000}?)\])?\]")]
    private static partial Regex LinkPattern();

    private static readonly string[] PictureExtensions = [".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".bmp", ".tif", ".tiff"];

    /// <summary>[[target][description]] or [[target]]; a picture when it is one without a description.</summary>
    private int? Link(Closers text, int i, int end, HCharFormat format, InlineSink sink, int depth)
    {
        if (LinkPattern().Match(text.Text, i) is not { Success: true } match || match.Index + match.Length > end)
            return null;
        var target = match.Groups[1].Value.Replace("\\]", "]", StringComparison.Ordinal).Replace("\\[", "[", StringComparison.Ordinal).Trim();
        var path = target.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? target[5..] : target.StartsWith("./", StringComparison.Ordinal) || target.StartsWith('/') ? target : null;
        if (!match.Groups[2].Success && path is not null && PictureExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            if (MarkdownReader.ResolveImage(path, _baseFolder) is { } file)
            {
                _budget.Object();
                sink.Add(new HImage(file));
            }
            return i + match.Length;
        }
        var link = new HLink(path is not null && target.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? path : target);
        var description = match.Groups[2].Success ? match.Groups[2].Value : target;
        var content = new InlineSink(link.Content);
        Inlines(new Closers(description), 0, description.Length, format, content, depth + 1);
        content.Flush();
        Clean(link.Content);
        sink.Add(link);
        return i + match.Length;
    }

    [GeneratedRegex(@"\G\[fn:([^\]:\s]{0,100})(?::([^\]\n]{1,4000}))?\]")]
    private static partial Regex FootnotePattern();

    private int? FootnoteReference(Closers text, int i, int end, InlineSink sink, int depth)
    {
        if (FootnotePattern().Match(text.Text, i) is not { Success: true } match || match.Index + match.Length > end)
            return null;
        var note = new HNote(endnote: false);
        if (match.Groups[2].Success)
        {
            // [fn::inline text] or [fn:name:inline text]
            using (_budget.Enter())
            {
                _budget.Paragraph();
                var paragraph = new HParagraph();
                var content = new InlineSink(paragraph.Inlines);
                var body = match.Groups[2].Value;
                Inlines(new Closers(body), 0, body.Length, Plain, content, depth + 1);
                content.Flush();
                Clean(paragraph.Inlines);
                note.Blocks.Add(paragraph);
            }
            if (match.Groups[1].Value.Length > 0)
                _footnotes.TryAdd(match.Groups[1].Value, [.. note.Blocks]);
        }
        else
        {
            _references.Add((match.Groups[1].Value, note));
        }
        sink.Add(note);
        return i + match.Length;
    }

    [GeneratedRegex(@"\G(?:https?|ftp)://[^\s<>\[\]]{1,2000}|\Gmailto:[^\s<>\[\]]{1,2000}", RegexOptions.IgnoreCase)]
    private static partial Regex BareUrl();
}
