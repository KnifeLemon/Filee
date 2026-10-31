// MediaWiki markup (.mediawiki, .wiki) → HDocument, without Pandoc. Blocks: = headings = (levels 1–6), paragraphs (a
// single line break is a space, as the wiki renders it), lists (* # nested and mixed, ; terms and : indents),
// ---- rules, lines starting with a space and <pre> / <syntaxhighlight> / <source> as code, <blockquote>, and tables
// ({| |+ |- ! !! | || |}, colspan / rowspan, tables in cells). Inline: '' italic, ''' bold, ''''' both (the wiki's
// toggles), [[internal links|text]] with link trails, [external links text], bare URLs, [[File:…]] pictures (local
// files only), <ref> footnotes, <nowiki>, the formatting tags (b, i, u, s, del, ins, sup, sub, code, tt, br) and
// HTML entities. Templates are not expanded: {{…}} stays in the text as written, and so do parser functions.
// Comments, categories, magic words (__TOC__) and <references /> are left out. Nested tables and quotes count
// against DocumentLimits' nesting depth.

using System.Text;
using System.Text.RegularExpressions;
using Filee.Engines.Documents;

namespace Filee.Engines.Hwp.Hwpx.Markup;

/// <summary>Reads MediaWiki markup into the document model.</summary>
internal sealed partial class MediaWikiReader
{
    private const int MaxInlineDepth = 8;

    private static readonly HCharFormat Plain = default;
    private static readonly HCharFormat CodeFormat = MarkupBlocks.Code;

    private readonly string _baseFolder;
    private readonly DocumentBudget _budget;
    private readonly Dictionary<string, HNote> _namedReferences = new(StringComparer.Ordinal);

    private MediaWikiReader(string baseFolder, DocumentLimits limits)
    {
        _baseFolder = baseFolder;
        _budget = new DocumentBudget(limits);
    }

    public static HDocument Read(string path, string media) =>
        Read(MarkupBlocks.ReadText(path, DocumentLimits.Default, "MediaWiki"), Path.GetDirectoryName(Path.GetFullPath(path))!, DocumentLimits.Default);

    internal static HDocument Read(string text, string baseFolder, DocumentLimits limits)
    {
        var reader = new MediaWikiReader(baseFolder, limits);
        text = Clean(MarkupBlocks.Lines(text));
        var section = new HSection();
        reader.Blocks(text.Split('\n'), section.Blocks, 0);
        var document = new HDocument();
        document.Sections.Add(section);
        return document;
    }

    /// <summary>Comments, categories, magic words and the references list go before anything is read.</summary>
    private static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        var at = 0;
        while (true)
        {
            var open = text.IndexOf("<!--", at, StringComparison.Ordinal);
            if (open < 0)
            {
                sb.Append(text, at, text.Length - at);
                break;
            }
            sb.Append(text, at, open - at);
            var close = text.IndexOf("-->", open + 4, StringComparison.Ordinal);
            if (close < 0)
                break; // an unclosed comment runs to the end
            // A comment's line breaks stay, so the lines around it don't join.
            sb.Append('\n', text.AsSpan(open, close - open).Count('\n'));
            at = close + 3;
        }
        var cleaned = Category().Replace(sb.ToString(), "");
        cleaned = MagicWord().Replace(cleaned, "");
        return References().Replace(cleaned, "");
    }

    // ───────────────────────── Blocks ─────────────────────────

    [GeneratedRegex(@"^(={1,6})(.+?)\1\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^[*#:;]+")]
    private static partial Regex ListPrefix();

    [GeneratedRegex(@"^<(pre|syntaxhighlight|source)(\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex CodeStart();

    /// <param name="indent">Left indent of the blocks (quotes).</param>
    private void Blocks(string[] lines, List<HBlock> output, int indent)
    {
        for (var i = 0; i < lines.Length;)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (line.Trim().Length == 0)
            {
                i++;
                continue;
            }
            if (IsHeading(line) && Heading().Match(line.TrimEnd()) is { Success: true } heading && heading.Groups[2].Value.Trim().Length > 0)
            {
                var paragraph = Paragraph(heading.Groups[2].Value.Trim(), indent);
                paragraph.HeadingLevel = heading.Groups[1].Length;
                output.Add(paragraph);
                i++;
                continue;
            }
            if (line.StartsWith("----", StringComparison.Ordinal))
            {
                MarkupBlocks.Rule(output, _budget);
                var rest = line.TrimStart('-').Trim();
                if (rest.Length > 0)
                    output.Add(Paragraph(rest, indent));
                i++;
                continue;
            }
            if (trimmed.StartsWith("{|", StringComparison.Ordinal))
            {
                output.Add(Table(lines, ref i, indent));
                continue;
            }
            if (CodeStart().Match(trimmed) is { Success: true } code)
            {
                i = Code(lines, i, code, output, indent);
                continue;
            }
            if (trimmed.StartsWith("<blockquote", StringComparison.OrdinalIgnoreCase))
            {
                i = Quote(lines, i, output, indent);
                continue;
            }
            if (ListPrefix().IsMatch(line))
            {
                var start = i;
                while (i < lines.Length && ListPrefix().IsMatch(lines[i]))
                    i++;
                List(lines[start..i], output, indent);
                continue;
            }
            if (line[0] == ' ')
            {
                // Preformatted: every line starts with a space, which is not part of the text.
                MarkupBlocks.SeparateCode(output, _budget);
                var start = i;
                while (i < lines.Length && lines[i].Length > 0 && lines[i][0] == ' ')
                    i++;
                foreach (var codeLine in lines[start..i])
                {
                    _budget.Paragraph();
                    var paragraph = new HParagraph { Format = indent > 0 ? new HParaFormat(Left: indent) : default, Role = HBlockRole.Code };
                    var sink = new InlineSink(paragraph.Inlines);
                    // Wiki markup still works inside (bold, links); the text is code.
                    Inlines(new Closers(codeLine[1..]), 0, codeLine.Length - 1, CodeFormat, sink, 0);
                    sink.Flush();
                    if (paragraph.Inlines.Count == 0)
                        paragraph.Inlines.Add(new HText("", CodeFormat));
                    output.Add(paragraph);
                }
                continue;
            }

            var text = new List<string>();
            for (; i < lines.Length; i++)
            {
                var next = lines[i];
                if (next.Trim().Length == 0 || (text.Count > 0 && StartsBlock(next)))
                    break;
                text.Add(next);
            }
            output.Add(Paragraph(string.Join(' ', text), indent));
        }
    }

    /// <summary>Starts and ends with "=": only then is the heading pattern tried.</summary>
    private static bool IsHeading(string line) => line.Length > 2 && line[0] == '=' && line.TrimEnd().EndsWith('=');

    private static bool StartsBlock(string line)
    {
        var trimmed = line.TrimStart();
        return line[0] == ' ' || ListPrefix().IsMatch(line) || line.StartsWith("----", StringComparison.Ordinal)
               || trimmed.StartsWith("{|", StringComparison.Ordinal) || (IsHeading(line) && Heading().IsMatch(line.TrimEnd()))
               || CodeStart().IsMatch(trimmed) || trimmed.StartsWith("<blockquote", StringComparison.OrdinalIgnoreCase);
    }

    private HParagraph Paragraph(string text, int indent)
    {
        _budget.Paragraph();
        var paragraph = new HParagraph { Format = indent > 0 ? new HParaFormat(Left: indent) : default };
        var sink = new InlineSink(paragraph.Inlines);
        Inlines(new Closers(text), 0, text.Length, Plain, sink, 0);
        sink.Flush();
        TrimEnds(paragraph.Inlines);
        return paragraph;
    }

    /// <summary>Spaces at the start and end of a paragraph are not text.</summary>
    private static void TrimEnds(List<HInline> inlines)
    {
        if (inlines.Count > 0 && inlines[0] is HText first)
            inlines[0] = new HText(first.Text.TrimStart(), first.Format);
        if (inlines.Count > 0 && inlines[^1] is HText last)
            inlines[^1] = new HText(last.Text.TrimEnd(), last.Format);
        inlines.RemoveAll(i => i is HText { Text.Length: 0 });
    }

    /// <summary>&lt;pre&gt;, &lt;syntaxhighlight&gt; or &lt;source&gt; up to its end tag: the text as it is.</summary>
    private int Code(string[] lines, int i, Match open, List<HBlock> output, int indent)
    {
        var tag = open.Groups[1].Value.ToLowerInvariant();
        var end = $"</{tag}>";
        var first = lines[i].TrimStart()[open.Length..];
        var content = new List<string>();
        for (var line = first; ; line = lines[i])
        {
            var at = line.IndexOf(end, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                content.Add(line[..at]);
                i++;
                break;
            }
            content.Add(line);
            if (++i >= lines.Length)
                break;
        }
        if (content.Count > 0 && content[0].Length == 0)
            content.RemoveAt(0);
        if (content.Count > 0 && content[^1].Trim().Length == 0)
            content.RemoveAt(content.Count - 1);
        // <pre> keeps entities and <nowiki>; syntaxhighlight shows its text as it is.
        var text = content.Select(l => tag == "pre" ? System.Net.WebUtility.HtmlDecode(NowikiTag().Replace(l, "")) : l);
        MarkupBlocks.SeparateCode(output, _budget);
        MarkupBlocks.CodeLines(text, output, _budget, indent);
        return i;
    }

    private int Quote(string[] lines, int i, List<HBlock> output, int indent)
    {
        // The quote runs to its end tag (nested quotes counted); its content is wiki text of its own.
        var content = new List<string>();
        var depth = 0;
        for (; i < lines.Length; i++)
        {
            var line = lines[i];
            depth += BlockquoteOpen().Matches(line).Count;
            var closes = line.Split("</blockquote>", StringSplitOptions.None).Length - 1;
            depth -= closes;
            content.Add(line);
            if (depth <= 0)
            {
                i++;
                break;
            }
        }
        var inner = string.Join('\n', content);
        var start = BlockquoteOpen().Match(inner) is { Success: true } m ? m.Index + m.Length : 0;
        var stop = inner.LastIndexOf("</blockquote>", StringComparison.OrdinalIgnoreCase);
        inner = stop >= start ? inner[start..stop] : inner[start..];
        using (_budget.Enter())
            Blocks(inner.Split('\n'), output, Math.Min(indent + MarkupBlocks.QuoteIndent, 8 * MarkupBlocks.QuoteIndent));
        return i;
    }

    private void List(string[] lines, List<HBlock> output, int indent)
    {
        var numberings = new Dictionary<(int Level, bool Ordered), HNumbering>();
        foreach (var line in lines)
        {
            var prefix = ListPrefix().Match(line).Value;
            var text = line[prefix.Length..].Trim();
            var last = prefix[^1];
            // ";term : definition" on one line.
            string? definition = null;
            if (last == ';' && DefinitionSplit(text) is { } split)
            {
                definition = text[(split + 1)..].Trim();
                text = text[..split].Trim();
            }
            var level = Math.Min(prefix.Length - 1, 8);
            HParagraph paragraph;
            if (last is ':' or ';')
            {
                // ":" indents (a continuation inside a list, a quote elsewhere); ";" is a bold term.
                var inList = prefix.Any(c => c is '*' or '#');
                var depth = Math.Min(prefix.Length - (last == ';' ? 1 : 0), 8);
                paragraph = Paragraph(text, indent + (inList ? 0 : depth * MarkupBlocks.QuoteIndent));
                if (last == ';')
                    Bold(paragraph.Inlines);
                if (inList)
                {
                    var owner = prefix.TrimEnd(':', ';');
                    var ownerLevel = Math.Min(owner.Length - 1, 8);
                    var ordered = owner[^1] == '#';
                    paragraph.List = new HListRef(Numbering(numberings, ownerLevel, ordered), ownerLevel, Numbered: false);
                }
                output.Add(paragraph);
                if (definition is not null)
                    output.Add(Paragraph(definition, indent + MarkupBlocks.QuoteIndent));
                continue;
            }
            foreach (var deeper in numberings.Keys.Where(k => k.Level > level).ToList())
                numberings.Remove(deeper);
            paragraph = Paragraph(text, indent);
            paragraph.List = new HListRef(Numbering(numberings, level, last == '#'), level, Numbered: true);
            output.Add(paragraph);
        }
    }

    private static HNumbering Numbering(Dictionary<(int Level, bool Ordered), HNumbering> numberings, int level, bool ordered)
    {
        if (!numberings.TryGetValue((level, ordered), out var numbering))
            numberings[(level, ordered)] = numbering = MarkupBlocks.Numbering(ordered, level: level);
        return numbering;
    }

    /// <summary>The ':' that splits ";term : definition" (not one inside a link or a URL).</summary>
    private static int? DefinitionSplit(string text)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '[')
                depth++;
            else if (text[i] == ']')
                depth = Math.Max(0, depth - 1);
            else if (text[i] == ':' && depth == 0 && !(i + 2 < text.Length && text[i + 1] == '/' && text[i + 2] == '/'))
                return i;
        }
        return null;
    }

    private static void Bold(List<HInline> inlines)
    {
        for (var i = 0; i < inlines.Count; i++)
            if (inlines[i] is HText text)
                inlines[i] = new HText(text.Text, text.Format with { Bold = true });
    }

    // ───────────────────────── Tables ─────────────────────────

    [GeneratedRegex(@"\b(colspan|rowspan)\s*=\s*[""']?\s*(\d{1,6})", RegexOptions.IgnoreCase)]
    private static partial Regex SpanAttribute();

    /// <summary>A table from "{|" to its "|}" (or the end); <paramref name="i"/> ends after it.</summary>
    private HTable Table(string[] lines, ref int i, int indent)
    {
        _budget.Object();
        using var level = _budget.Enter();
        var table = new HTable { Indent = indent };
        HRow? row = null;
        HCell? cell = null;
        var cellLines = new List<string>();
        var headers = new HashSet<HCell>(ReferenceEqualityComparer.Instance);
        var limit = _budget.Limits.MaxTableColumns;

        void FinishCell()
        {
            if (cell is null)
                return;
            var blocks = new List<HBlock>();
            Blocks([.. cellLines], blocks, 0);
            cell.Blocks.InsertRange(0, blocks);
            if (headers.Contains(cell))
                foreach (var paragraph in cell.Blocks.OfType<HParagraph>())
                    Bold(paragraph.Inlines);
            cellLines.Clear();
            cell = null;
        }

        void AddCell(string segment, bool header)
        {
            FinishCell();
            if (row is null)
            {
                row = new HRow();
                table.Rows.Add(row);
            }
            if (row.Cells.Sum(c => c.ColSpan) >= limit)
                return;
            cell = new HCell();
            var content = segment;
            if (AttributeSplit(segment) is { } bar)
            {
                foreach (Match span in SpanAttribute().Matches(segment[..bar]))
                {
                    var value = int.Parse(span.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                    if (span.Groups[1].Value.Equals("colspan", StringComparison.OrdinalIgnoreCase))
                        cell.ColSpan = Math.Clamp(value, 1, limit);
                    else
                        cell.RowSpan = Math.Clamp(value, 1, 10_000);
                }
                content = segment[(bar + 1)..];
            }
            // A header row is one of header cells only.
            row.Header = header && (row.Cells.Count == 0 || row.Header);
            if (header)
                headers.Add(cell);
            row.Cells.Add(cell);
            cellLines.Add(content.Trim());
        }

        i++;
        while (i < lines.Length)
        {
            var line = lines[i].TrimStart();
            if (line.StartsWith("|}", StringComparison.Ordinal))
            {
                FinishCell();
                i++;
                break;
            }
            if (line.StartsWith("{|", StringComparison.Ordinal))
            {
                // A table in the current cell (or, without one, in a cell of its own).
                if (cell is null)
                    AddCell("", header: false);
                var held = cell!;
                var blocks = new List<HBlock>();
                Blocks([.. cellLines], blocks, 0);
                cellLines.Clear();
                held.Blocks.AddRange(blocks);
                held.Blocks.Add(Table(lines, ref i, 0));
                continue;
            }
            if (line.StartsWith("|+", StringComparison.Ordinal))
            {
                FinishCell();
                table.Caption.Add(Paragraph(line[2..].Trim(), 0));
                i++;
                continue;
            }
            if (line.StartsWith("|-", StringComparison.Ordinal))
            {
                FinishCell();
                row = new HRow();
                table.Rows.Add(row);
                i++;
                continue;
            }
            if (line.StartsWith('!') || line.StartsWith('|'))
            {
                var header = line[0] == '!';
                var body = line[1..];
                foreach (var segment in CellSegments(body, header))
                    AddCell(segment, header);
                i++;
                continue;
            }
            // More of the current cell (or text before the first cell, which the wiki shows above the table).
            if (cell is not null)
                cellLines.Add(lines[i]);
            i++;
        }
        FinishCell();
        table.Rows.RemoveAll(r => r.Cells.Count == 0);
        table.ColumnCount = Math.Max(1, table.Rows.Count == 0 ? 1 : table.Rows.Max(r => r.Cells.Sum(c => c.ColSpan)));
        if (table.Rows.Count == 0)
            table.Rows.Add(new HRow { Cells = { new HCell { Blocks = { new HParagraph() } } } });
        return table;
    }

    /// <summary>The cells of one line: "||" separates them, and "!!" too on a header line (outside links and templates).</summary>
    private static List<string> CellSegments(string line, bool header)
    {
        var segments = new List<string>();
        var start = 0;
        var depth = 0;
        for (var i = 0; i < line.Length - 1; i++)
        {
            var ch = line[i];
            if ((ch == '[' && line[i + 1] == '[') || (ch == '{' && line[i + 1] == '{'))
            {
                depth++;
                i++;
            }
            else if ((ch == ']' && line[i + 1] == ']') || (ch == '}' && line[i + 1] == '}'))
            {
                depth = Math.Max(0, depth - 1);
                i++;
            }
            else if (depth == 0 && ((ch == '|' && line[i + 1] == '|') || (header && ch == '!' && line[i + 1] == '!')))
            {
                segments.Add(line[start..i]);
                start = i + 2;
                i++;
            }
        }
        segments.Add(line[start..]);
        return segments;
    }

    /// <summary>The '|' between a cell's attributes and its content, when the part before it looks like attributes.</summary>
    private static int? AttributeSplit(string segment)
    {
        var depth = 0;
        for (var i = 0; i < segment.Length; i++)
        {
            var ch = segment[i];
            if (ch is '[' or '{')
                depth++;
            else if (ch is ']' or '}')
                depth = Math.Max(0, depth - 1);
            else if (ch == '|' && depth == 0)
                return segment[..i].Contains('=', StringComparison.Ordinal) || segment[..i].Trim().Length == 0 ? i : null;
        }
        return null;
    }

    // ───────────────────────── Inlines ─────────────────────────

    /// <summary>The formatting state of a run of inline text: the wiki's quote toggles and the open HTML tags.</summary>
    private sealed class State(HCharFormat baseFormat)
    {
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public List<(string Tag, Func<HCharFormat, HCharFormat> Apply)> Tags { get; } = [];

        public HCharFormat Format
        {
            get
            {
                var format = baseFormat;
                if (Bold)
                    format = format with { Bold = true };
                if (Italic)
                    format = format with { Italic = true };
                foreach (var (_, apply) in Tags)
                    format = apply(format);
                return format;
            }
        }
    }

    private static readonly Dictionary<string, Func<HCharFormat, HCharFormat>> FormatTags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["b"] = f => f with { Bold = true },
        ["strong"] = f => f with { Bold = true },
        ["i"] = f => f with { Italic = true },
        ["em"] = f => f with { Italic = true },
        ["u"] = f => f with { Underline = true },
        ["ins"] = f => f with { Underline = true },
        ["s"] = f => f with { Strike = true },
        ["strike"] = f => f with { Strike = true },
        ["del"] = f => f with { Strike = true },
        ["sup"] = f => f with { Superscript = true, Subscript = null },
        ["sub"] = f => f with { Subscript = true, Superscript = null },
        ["code"] = f => f with { Shade = HtmlReader.CodeShade },
        ["tt"] = f => f with { Shade = HtmlReader.CodeShade },
        ["kbd"] = f => f with { Shade = HtmlReader.CodeShade },
        ["samp"] = f => f with { Shade = HtmlReader.CodeShade },
        ["mark"] = f => f with { Shade = HtmlReader.MarkShade },
    };

    private void Inlines(Closers text, int start, int end, HCharFormat format, InlineSink sink, int depth)
    {
        var s = text.Text;
        var state = new State(format);
        for (var i = start; i < end;)
        {
            var ch = s[i];
            switch (ch)
            {
                case '\'' when i + 1 < end && s[i + 1] == '\'':
                    {
                        var run = 0;
                        while (i + run < end && s[i + run] == '\'')
                            run++;
                        // 2 italic, 3 bold, 5 both; 4 is an apostrophe and bold; more than 5, the rest are apostrophes.
                        var marks = run >= 5 ? 5 : run == 4 ? 3 : run;
                        if (run > marks)
                            sink.Text(new string('\'', run - marks), state.Format);
                        if (marks is 2 or 5)
                            state.Italic = !state.Italic;
                        if (marks is 3 or 5)
                            state.Bold = !state.Bold;
                        i += run;
                        continue;
                    }
                case '[' when i + 1 < end && s[i + 1] == '[':
                    if (depth < MaxInlineDepth && InternalLink(text, i, end, state.Format, sink, depth) is { } afterLink)
                    {
                        i = afterLink;
                        continue;
                    }
                    break;
                case '[':
                    if (depth < MaxInlineDepth && ExternalLink(text, i, end, state.Format, sink, depth) is { } afterExternal)
                    {
                        i = afterExternal;
                        continue;
                    }
                    break;
                case '{' when i + 1 < end && s[i + 1] == '{':
                    {
                        // Templates are not expanded: the call stays as written (nested calls included).
                        var close = MatchingBraces(s, i, end);
                        sink.Text(s[i..close], state.Format);
                        i = close;
                        continue;
                    }
                case '<':
                    if (Tag(text, ref i, end, state, sink, depth))
                        continue;
                    break;
                case '&':
                    if (Closers.Entity(s, i) is ({ } decoded, var length))
                    {
                        sink.Text(decoded, state.Format);
                        i += length;
                        continue;
                    }
                    break;
                case 'h' or 'f' or 'm' when (i == 0 || !char.IsLetterOrDigit(s[i - 1])) && BareUrl().Match(s, i) is { Success: true } url && url.Index + url.Length <= end:
                    {
                        var target = TrimUrl(url.Value);
                        var link = new HLink(target);
                        link.Content.Add(new HText(target, state.Format));
                        sink.Add(link);
                        i += target.Length;
                        continue;
                    }
            }
            sink.Text(ch, state.Format);
            i++;
        }
    }

    /// <summary>The index after the "}}" that closes the template opened at <paramref name="start"/> (or the end).</summary>
    private static int MatchingBraces(string s, int start, int end)
    {
        var depth = 0;
        for (var i = start; i < end - 1; i++)
        {
            if (s[i] == '{' && s[i + 1] == '{')
            {
                depth++;
                i++;
            }
            else if (s[i] == '}' && s[i + 1] == '}')
            {
                depth--;
                i++;
                if (depth == 0)
                    return i + 1;
            }
        }
        return end;
    }

    /// <summary>[[target|text]] (with its link trail), [[File:…]] pictures; null when it isn't one.</summary>
    private int? InternalLink(Closers text, int i, int end, HCharFormat format, InlineSink sink, int depth)
    {
        var s = text.Text;
        var close = text.Find("]]", i + 2, end);
        if (close < 0)
            return null;
        // [[File:a.png|thumb|[[nested]] caption]]: a nested link inside the caption ends later.
        var inner = s[(i + 2)..close];
        if (inner.Contains("[[", StringComparison.Ordinal))
        {
            var after = close + 2;
            var nested = inner.Split("[[").Length - 1;
            while (nested-- > 0 && text.Find("]]", after, end) is var more and >= 0)
            {
                close = more;
                after = more + 2;
            }
            inner = s[(i + 2)..close];
        }
        var bar = inner.IndexOf('|');
        var target = (bar < 0 ? inner : inner[..bar]).Trim();
        var label = bar < 0 ? null : inner[(bar + 1)..];
        var colon = target.IndexOf(':');
        var space = colon > 0 ? target[..colon].Trim().ToLowerInvariant() : "";
        if (space is "file" or "image" or "media")
        {
            var options = label?.Split('|') ?? [];
            if (MarkdownReader.ResolveImage(target[(colon + 1)..].Trim(), _baseFolder) is { } path)
            {
                _budget.Object();
                sink.Add(new HImage(path));
            }
            return close + 2;
        }
        if (space == "category")
            return close + 2;
        target = target.TrimStart(':');

        // A link trail: the letters right after "]]" belong to the link's text ([[apple]]s).
        var trail = close + 2;
        while (trail < end && char.IsLetter(s[trail]))
            trail++;
        var link = new HLink(target.Replace(' ', '_'));
        var content = new InlineSink(link.Content);
        var shown = (label is { Length: > 0 } ? label : target) + s[(close + 2)..trail];
        Inlines(new Closers(shown), 0, shown.Length, format, content, depth + 1);
        content.Flush();
        sink.Add(link);
        return trail;
    }

    [GeneratedRegex(@"\G\[((?:https?|ftp|ftps|mailto|news|irc|ircs|gopher|telnet|sftp|ssh|git|svn|tel|sms)://?[^\s\]]{1,2000}|//[^\s\]]{1,2000}|mailto:[^\s\]]{1,2000})(?:[ \t]([^\]\n]{0,4000}))?\]", RegexOptions.IgnoreCase)]
    private static partial Regex ExternalLinkPattern();

    private int? ExternalLink(Closers text, int i, int end, HCharFormat format, InlineSink sink, int depth)
    {
        if (ExternalLinkPattern().Match(text.Text, i) is not { Success: true } match || match.Index + match.Length > end)
            return null;
        var link = new HLink(match.Groups[1].Value);
        var label = match.Groups[2].Success && match.Groups[2].Value.Trim().Length > 0 ? match.Groups[2].Value.Trim() : match.Groups[1].Value;
        var content = new InlineSink(link.Content);
        Inlines(new Closers(label), 0, label.Length, format, content, depth + 1);
        content.Flush();
        sink.Add(link);
        return i + match.Length;
    }

    [GeneratedRegex(@"\G<(/?)([A-Za-z][A-Za-z0-9]{0,15})((?:\s[^<>]{0,2000})?)\s*(/?)>")]
    private static partial Regex TagPattern();

    /// <summary>An HTML tag at <paramref name="i"/>: formatting, a line break, &lt;nowiki&gt; or &lt;ref&gt;.</summary>
    private bool Tag(Closers text, ref int i, int end, State state, InlineSink sink, int depth)
    {
        var s = text.Text;
        if (TagPattern().Match(s, i) is not { Success: true } tag || tag.Index + tag.Length > end)
            return false;
        var closing = tag.Groups[1].Value == "/";
        var name = tag.Groups[2].Value.ToLowerInvariant();
        var selfClosing = tag.Groups[4].Value == "/";
        var after = i + tag.Length;
        switch (name)
        {
            case "br":
                sink.Add(new HLineBreak(state.Format));
                break;
            case "nowiki" when !closing && !selfClosing:
                {
                    var close = text.Find("</nowiki>", after, end);
                    var stop = close < 0 ? end : close;
                    sink.Text(System.Net.WebUtility.HtmlDecode(s[after..stop]), state.Format);
                    after = close < 0 ? end : close + "</nowiki>".Length;
                    break;
                }
            case "ref" when !closing && depth < MaxInlineDepth:
                {
                    var refName = RefName().Match(tag.Groups[3].Value) is { Success: true } named ? named.Groups[1].Value : null;
                    if (selfClosing)
                    {
                        if (refName is not null && _namedReferences.TryGetValue(refName, out var known))
                            sink.Add(Copy(known));
                        break;
                    }
                    var close = text.Find("</ref>", after, end);
                    var stop = close < 0 ? end : close;
                    var note = new HNote(endnote: false);
                    using (_budget.Enter())
                    {
                        _budget.Paragraph();
                        var paragraph = new HParagraph();
                        var content = new InlineSink(paragraph.Inlines);
                        Inlines(text, after, stop, default, content, depth + 1);
                        content.Flush();
                        TrimEnds(paragraph.Inlines);
                        note.Blocks.Add(paragraph);
                    }
                    if (refName is not null)
                        _namedReferences.TryAdd(refName, note);
                    sink.Add(note);
                    after = close < 0 ? end : close + "</ref>".Length;
                    break;
                }
            case var formatting when FormatTags.TryGetValue(formatting, out var apply):
                if (closing)
                {
                    var open = state.Tags.FindLastIndex(t => t.Tag == formatting);
                    if (open >= 0)
                        state.Tags.RemoveAt(open);
                }
                else if (!selfClosing && state.Tags.Count < 64)
                {
                    state.Tags.Add((formatting, apply));
                }
                break;
                // Other tags (span, div, font, small, references, gallery, ...) are dropped; their text stays.
        }
        i = after;
        return true;
    }

    /// <summary>A note again ("&lt;ref name=x /&gt;" uses one written before).</summary>
    private HNote Copy(HNote note)
    {
        _budget.Paragraph();
        var copy = new HNote(endnote: false);
        foreach (var paragraph in note.Blocks.OfType<HParagraph>())
        {
            var again = new HParagraph();
            again.Inlines.AddRange(paragraph.Inlines);
            copy.Blocks.Add(again);
        }
        return copy;
    }

    [GeneratedRegex(@"\bname\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s/>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex RefNamePattern();

    private static Regex RefName() => RefNamePattern();

    [GeneratedRegex(@"\G(?:https?|ftp)://[^\s<>\[\]{}|]{1,2000}|\Gmailto:[^\s<>\[\]{}|]{1,2000}", RegexOptions.IgnoreCase)]
    private static partial Regex BareUrl();

    private static string TrimUrl(string url)
    {
        url = url.TrimEnd('.', ',', ';', ':', '!', '?', '\'', '"');
        if (url.EndsWith(')') && !url.Contains('(', StringComparison.Ordinal))
            url = url[..^1];
        return url;
    }

    [GeneratedRegex(@"</?nowiki\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex NowikiTag();

    [GeneratedRegex(@"<blockquote(\s[^>]*)?>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockquoteOpen();

    [GeneratedRegex(@"\[\[\s*Category\s*:[^\]\n]*\]\]", RegexOptions.IgnoreCase)]
    private static partial Regex Category();

    [GeneratedRegex(@"__(?:NOTOC|FORCETOC|TOC|NOEDITSECTION|NEWSECTIONLINK|NONEWSECTIONLINK|NOGALLERY|HIDDENCAT|INDEX|NOINDEX|STATICREDIRECT|DISAMBIG|NOTITLECONVERT|NOTC|NOCONTENTCONVERT|NOCC|EXPECTUNUSEDCATEGORY)__")]
    private static partial Regex MagicWord();

    [GeneratedRegex(@"</?references(?:\s[^<>]{0,200})?/?>", RegexOptions.IgnoreCase)]
    private static partial Regex References();
}
