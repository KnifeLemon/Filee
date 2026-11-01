// Textile (.textile) → HDocument, without Pandoc. Blocks: h1.–h6., p., bq. (a quote), bc. / pre. (code), notextile.,
// ###. (a comment, left out), fnN. (footnotes), the extended forms ("bc.." runs on over blank lines until the next
// block signature), lists (* and #, nested by repeating them, mixed), definition lists (- term := definition) and
// tables (|_. header|, \2. column span, /2. row span, attributes in (), {} and [] skipped). A single line break inside
// a paragraph is a line break, as Textile renders it. Inline: *strong*, **bold**, _em_, __italic__, -deleted-,
// +inserted+, ^superscript^, ~subscript~, @code@, %span%, ??citation??, "links":url and ["links":url], !pictures!
// (local files only), [N] footnote references, ==notextile==, HTML entities and the <br> tag (other tags are dropped).
// Glyph replacements (quotes, dashes, (c)) are not made: the text stays as written.

using System.Text.RegularExpressions;
using Filee.Engines.Documents;

namespace Filee.Engines.Hwp.Hwpx.Markup;

/// <summary>Reads Textile into the document model.</summary>
internal sealed partial class TextileReader
{
    /// <summary>Levels of emphasis inside emphasis; deeper markers are text.</summary>
    private const int MaxInlineDepth = 12;

    private static readonly HCharFormat Plain = default;

    private readonly string _baseFolder;
    private readonly DocumentBudget _budget;
    private readonly Dictionary<string, List<HNote>> _references = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<HBlock>> _footnotes = new(StringComparer.Ordinal);

    /// <summary>Numbers of the footnotes the text defines: only "[N]" with a definition is a reference.</summary>
    private readonly HashSet<string> _defined = new(StringComparer.Ordinal);

    private TextileReader(string baseFolder, DocumentLimits limits)
    {
        _baseFolder = baseFolder;
        _budget = new DocumentBudget(limits);
    }

    public static HDocument Read(string path, string media) =>
        Read(MarkupBlocks.ReadText(path, DocumentLimits.Default, "Textile"), Path.GetDirectoryName(Path.GetFullPath(path))!, DocumentLimits.Default);

    internal static HDocument Read(string text, string baseFolder, DocumentLimits limits)
    {
        var reader = new TextileReader(baseFolder, limits);
        var lines = MarkupBlocks.Lines(text).Split('\n');
        foreach (var line in lines)
            if (line.StartsWith("fn", StringComparison.Ordinal) && Signature().Match(line) is { Success: true } definition)
                reader._defined.Add(new string([.. definition.Groups[1].Value.Skip(2).TakeWhile(char.IsAsciiDigit)]));
        var section = new HSection();
        reader.Blocks(lines, section.Blocks);
        // Footnote references get the text of their definitions; a reference without one stays text.
        foreach (var (number, notes) in reader._references)
        {
            foreach (var note in notes)
            {
                if (reader._footnotes.TryGetValue(number, out var blocks))
                    note.Blocks.AddRange(blocks);
            }
        }
        var document = new HDocument();
        document.Sections.Add(section);
        return document;
    }

    // ───────────────────────── Blocks ─────────────────────────

    // Attributes are groups with their own delimiters or single characters (never a run inside a repeated group,
    // which makes a regular expression take exponential time on a line that almost matches).
    [GeneratedRegex(@"^(h[1-6]|p|bq|bc|pre|notextile|###|fn\d{1,6}\^?)((?:\([^()\n]*\)|\{[^{}\n]*\}|\[[^\[\]\n]*\]|[<>=])*)(\.\.?)(?: (.*)|$)")]
    private static partial Regex Signature();

    [GeneratedRegex(@"^([*#]{1,64})((?:\([^()\n]*\)|\{[^{}\n]*\}|\[[^\[\]\n]*\])*) (.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^table((?:\([^()\n]*\)|\{[^{}\n]*\}|\[[^\[\]\n]*\]|[<>=])*)\.\s*$")]
    private static partial Regex TableSignature();

    [GeneratedRegex(@"^((?:\([^()\n]*\)|\{[^{}\n]*\}|\[[^\[\]\n]*\]|[<>=^~_-])+)\.\s*(?=\|)")]
    private static partial Regex RowAttributes();

    private void Blocks(string[] lines, List<HBlock> output)
    {
        for (var i = 0; i < lines.Length;)
        {
            var line = lines[i];
            if (line.Trim().Length == 0)
            {
                i++;
                continue;
            }

            if (Signature().Match(line) is { Success: true } signature)
            {
                var tag = signature.Groups[1].Value;
                var extended = signature.Groups[3].Value == "..";
                var content = new List<string> { signature.Groups[4].Value };
                i++;
                for (; i < lines.Length; i++)
                {
                    if (extended ? Signature().IsMatch(lines[i]) : lines[i].Trim().Length == 0)
                        break;
                    content.Add(lines[i]);
                }
                if (extended)
                    while (content.Count > 0 && content[^1].Trim().Length == 0)
                        content.RemoveAt(content.Count - 1);
                Block(tag, content, extended, output);
                continue;
            }
            if (TableSignature().IsMatch(line) || line.TrimStart().StartsWith('|') || RowAttributes().IsMatch(line))
            {
                if (TableSignature().IsMatch(line))
                    i++;
                var start = i;
                while (i < lines.Length && (lines[i].TrimStart().StartsWith('|') || RowAttributes().IsMatch(lines[i])))
                    i++;
                if (i > start)
                    output.Add(Table(lines[start..i]));
                continue;
            }
            if (ListItem().IsMatch(line))
            {
                var start = i;
                while (i < lines.Length && lines[i].Trim().Length > 0)
                    i++;
                List(lines[start..i], output);
                continue;
            }
            if (line.StartsWith("- ", StringComparison.Ordinal) && line.Contains(":=", StringComparison.Ordinal))
            {
                for (; i < lines.Length && lines[i].StartsWith("- ", StringComparison.Ordinal); i++)
                    Definition(lines[i][2..], output);
                continue;
            }

            // A paragraph: lines up to the next blank line.
            var paragraph = new List<string>();
            for (; i < lines.Length && lines[i].Trim().Length > 0; i++)
                paragraph.Add(lines[i]);
            output.Add(Paragraph(string.Join('\n', paragraph), 0));
        }
    }

    private void Block(string tag, List<string> content, bool extended, List<HBlock> output)
    {
        switch (tag)
        {
            case "bc" or "pre":
                MarkupBlocks.SeparateCode(output, _budget);
                MarkupBlocks.CodeLines(content.Select(Decode), output, _budget);
                break;
            case "###":
                break;
            case "notextile":
                foreach (var part in Paragraphs(content, extended))
                {
                    _budget.Paragraph();
                    var paragraph = new HParagraph();
                    paragraph.Inlines.Add(new HText(Decode(AnyTag().Replace(part, "")).Replace('\n', ' '), Plain));
                    output.Add(paragraph);
                }
                break;
            case "bq":
                foreach (var part in Paragraphs(content, extended))
                    output.Add(Paragraph(part, MarkupBlocks.QuoteIndent));
                break;
            case "p":
                foreach (var part in Paragraphs(content, extended))
                    output.Add(Paragraph(part, 0));
                break;
            case var h when h[0] == 'h':
                {
                    var heading = Paragraph(string.Join(' ', content), 0);
                    heading.HeadingLevel = h[1] - '0';
                    output.Add(heading);
                    break;
                }
            default: // fnN: a footnote's text
                {
                    var number = new string([.. tag.Skip(2).TakeWhile(char.IsAsciiDigit)]);
                    var blocks = new List<HBlock>();
                    using (_budget.Enter())
                        foreach (var part in Paragraphs(content, extended))
                            blocks.Add(Paragraph(part, 0));
                    _footnotes.TryAdd(number, blocks);
                    break;
                }
        }
    }

    /// <summary>The paragraphs of a block: an extended one has several, separated by blank lines.</summary>
    private static IEnumerable<string> Paragraphs(List<string> content, bool extended)
    {
        if (!extended)
        {
            yield return string.Join('\n', content);
            yield break;
        }
        var part = new List<string>();
        foreach (var line in content.Append(""))
        {
            if (line.Trim().Length > 0)
            {
                part.Add(line);
                continue;
            }
            if (part.Count > 0)
                yield return string.Join('\n', part);
            part.Clear();
        }
    }

    private HParagraph Paragraph(string text, int indent)
    {
        _budget.Paragraph();
        var paragraph = new HParagraph { Format = indent > 0 ? new HParaFormat(Left: indent) : default };
        var sink = new InlineSink(paragraph.Inlines);
        Inlines(new Closers(text), 0, text.Length, Plain, sink, 0);
        sink.Flush();
        return paragraph;
    }

    private void List(string[] lines, List<HBlock> output)
    {
        // One numbering per list type and level, so the items of one list count on together.
        var numberings = new Dictionary<(int Level, bool Ordered), HNumbering>();
        HParagraph? last = null;
        foreach (var line in lines)
        {
            if (ListItem().Match(line) is not { Success: true } item)
            {
                if (last is null)
                    continue;
                // A line without a marker goes on with the item before.
                var sink = new InlineSink(last.Inlines);
                sink.Add(new HLineBreak(Plain));
                Inlines(new Closers(line), 0, line.Length, Plain, sink, 0);
                sink.Flush();
                continue;
            }
            var markers = item.Groups[1].Value;
            var level = Math.Min(markers.Length - 1, 8);
            var ordered = markers[^1] == '#';
            foreach (var deeper in numberings.Keys.Where(k => k.Level > level).ToList())
                numberings.Remove(deeper);
            if (!numberings.TryGetValue((level, ordered), out var numbering))
                numberings[(level, ordered)] = numbering = MarkupBlocks.Numbering(ordered, level: level);
            var paragraph = Paragraph(item.Groups[3].Value, 0);
            paragraph.List = new HListRef(numbering, level, Numbered: true);
            output.Add(paragraph);
            last = paragraph;
        }
    }

    private void Definition(string line, List<HBlock> output)
    {
        var split = line.IndexOf(":=", StringComparison.Ordinal);
        var term = Paragraph(line[..split].Trim(), 0);
        for (var i = 0; i < term.Inlines.Count; i++)
            if (term.Inlines[i] is HText text)
                term.Inlines[i] = new HText(text.Text, text.Format with { Bold = true });
        output.Add(term);
        output.Add(Paragraph(line[(split + 2)..].Trim(), MarkupBlocks.QuoteIndent));
    }

    [GeneratedRegex(@"^((?:_|\\\d{1,5}|/\d{1,5}|\{[^{}]*\}|\([^()]*\)|\[[^\[\]]*\]|[<>=^~-])+)\. ?")]
    private static partial Regex CellPrefix();

    private HTable Table(string[] lines)
    {
        _budget.Object();
        var table = new HTable();
        var columns = 0;
        var limit = _budget.Limits.MaxTableColumns;
        foreach (var raw in lines)
        {
            var line = RowAttributes().Replace(raw, "").Trim();
            if (line.StartsWith('|'))
                line = line[1..];
            if (line.EndsWith('|'))
                line = line[..^1];
            var row = new HRow();
            var width = 0;
            var allHeaders = true;
            foreach (var part in line.Split('|'))
            {
                if (width >= limit)
                    break;
                var cell = new HCell();
                var content = part;
                var header = false;
                if (CellPrefix().Match(content) is { Success: true } prefix)
                {
                    foreach (Match spec in CellSpec().Matches(prefix.Groups[1].Value))
                    {
                        var value = spec.Value;
                        if (value == "_")
                            header = true;
                        else if (value[0] == '\\' && int.TryParse(value[1..], out var span))
                            cell.ColSpan = Math.Clamp(span, 1, limit - width);
                        else if (value[0] == '/' && int.TryParse(value[1..], out var rows))
                            cell.RowSpan = Math.Clamp(rows, 1, 10_000);
                    }
                    content = content[prefix.Length..];
                }
                using (_budget.Enter())
                {
                    var paragraph = Paragraph(content.Trim(), 0);
                    if (header)
                        for (var i = 0; i < paragraph.Inlines.Count; i++)
                            if (paragraph.Inlines[i] is HText text)
                                paragraph.Inlines[i] = new HText(text.Text, text.Format with { Bold = true });
                    cell.Blocks.Add(paragraph);
                }
                allHeaders &= header;
                row.Cells.Add(cell);
                width += cell.ColSpan;
            }
            // A header row is one whose cells are all headers.
            row.Header = allHeaders && row.Cells.Count > 0;
            if (row.Cells.Count > 0)
                table.Rows.Add(row);
            columns = Math.Max(columns, width);
        }
        table.ColumnCount = Math.Max(1, columns);
        return table;
    }

    [GeneratedRegex(@"_|\\\d{1,5}|/\d{1,5}")]
    private static partial Regex CellSpec();

    // ───────────────────────── Inlines ─────────────────────────

    /// <summary>Emphasis markers and the format each gives, longest first.</summary>
    private static readonly (string Marker, Func<HCharFormat, HCharFormat> Apply)[] Markers =
    [
        ("**", f => f with { Bold = true }),
        ("__", f => f with { Italic = true }),
        ("??", f => f with { Italic = true }),
        ("*", f => f with { Bold = true }),
        ("_", f => f with { Italic = true }),
        ("-", f => f with { Strike = true }),
        ("+", f => f with { Underline = true }),
        ("^", f => f with { Superscript = true, Subscript = null }),
        ("~", f => f with { Subscript = true, Superscript = null }),
        ("%", f => f),
    ];

    private void Inlines(Closers text, int start, int end, HCharFormat format, InlineSink sink, int depth)
    {
        var s = text.Text;
        for (var i = start; i < end;)
        {
            var ch = s[i];
            switch (ch)
            {
                case '\n':
                    sink.Add(new HLineBreak(format));
                    i++;
                    continue;
                case '=' when i + 1 < end && s[i + 1] == '=':
                    {
                        var close = text.Find("==", i + 2, end);
                        if (close >= 0)
                        {
                            sink.Text(s[(i + 2)..close], format);
                            i = close + 2;
                            continue;
                        }
                        break;
                    }
                case '@':
                    {
                        var close = i + 1 < end && !char.IsWhiteSpace(s[i + 1]) ? text.Find("@", i + 2, end) : -1;
                        if (close > i + 1)
                        {
                            sink.Text(Decode(s[(i + 1)..close]), format with { Shade = HtmlReader.CodeShade });
                            i = close + 1;
                            continue;
                        }
                        break;
                    }
                case '<':
                    if (TagPattern().Match(s, i) is { Success: true } tag && tag.Index == i && tag.Index + tag.Length <= end)
                    {
                        if (tag.Value.StartsWith("<br", StringComparison.OrdinalIgnoreCase))
                            sink.Add(new HLineBreak(format));
                        i += tag.Length;
                        continue;
                    }
                    break;
                case '&':
                    if (Closers.Entity(s, i) is ({ } decoded, var length))
                    {
                        sink.Text(decoded, format);
                        i += length;
                        continue;
                    }
                    break;
                case '[':
                    {
                        if (FootnoteReference().Match(s, i) is { Success: true } reference && reference.Index == i
                            && _defined.Contains(reference.Groups[1].Value))
                        {
                            var note = new HNote(endnote: false);
                            if (!_references.TryGetValue(reference.Groups[1].Value, out var notes))
                                _references[reference.Groups[1].Value] = notes = [];
                            notes.Add(note);
                            sink.Add(note);
                            i += reference.Length;
                            continue;
                        }
                        if (i + 1 < end && s[i + 1] == '"' && BracketLink().Match(s, i) is { Success: true } bracketed && bracketed.Index == i
                            && bracketed.Index + bracketed.Length <= end)
                        {
                            Link(bracketed.Groups[1].Value, bracketed.Groups[2].Value, format, sink, depth);
                            i += bracketed.Length;
                            continue;
                        }
                        break;
                    }
                case '"':
                    if (LinkPattern().Match(s, i) is { Success: true } link && link.Index == i && link.Index + link.Length <= end)
                    {
                        var url = TrimUrl(link.Groups[2].Value);
                        Link(link.Groups[1].Value, url, format, sink, depth);
                        i += link.Length - (link.Groups[2].Value.Length - url.Length);
                        continue;
                    }
                    break;
                case '!' when i + 1 < end && !char.IsWhiteSpace(s[i + 1]):
                    if (ImagePattern().Match(s, i) is { Success: true } image && image.Index == i && image.Index + image.Length <= end)
                    {
                        if (MarkdownReader.ResolveImage(image.Groups[1].Value, _baseFolder) is { } path)
                        {
                            _budget.Object();
                            var picture = new HImage(path);
                            if (image.Groups[3].Success)
                            {
                                var target = new HLink(TrimUrl(image.Groups[3].Value));
                                target.Content.Add(picture);
                                sink.Add(target);
                            }
                            else
                            {
                                sink.Add(picture);
                            }
                        }
                        i += image.Length;
                        continue;
                    }
                    break;
            }

            if (depth < MaxInlineDepth && Emphasis(text, i, end, format, sink, depth) is { } after)
            {
                i = after;
                continue;
            }
            sink.Text(ch, format);
            i++;
        }
    }

    /// <summary>Emphasis opened at <paramref name="i"/>: its content is read and the index after it returned.</summary>
    private int? Emphasis(Closers text, int i, int end, HCharFormat format, InlineSink sink, int depth)
    {
        var s = text.Text;
        foreach (var (marker, apply) in Markers)
        {
            if (string.CompareOrdinal(s, i, marker, 0, marker.Length) != 0)
                continue;
            var open = i + marker.Length;
            // Opens after a non-letter (or in [brackets]) before a non-space; closes after a non-space before a non-letter.
            var bracket = i > 0 && s[i - 1] == '[';
            if ((text.Word(i - 1) && !bracket) || text.Space(open) || open >= end || s.IndexOf(marker[0], open, 1) == open)
                return null;
            var close = text.Find(marker, open + 1, end, at => !text.Space(at - 1)
                                                              && (!text.Word(at + marker.Length) || (at + marker.Length < s.Length && s[at + marker.Length] == ']')));
            if (close < 0)
                return null;
            var after = close + marker.Length;
            if (bracket && after < s.Length && s[after] == ']')
            {
                // [*intraword*]: the brackets go too. The "[" was written as text already.
                RemoveLast(sink, '[');
                after++;
            }
            Inlines(text, open, close, apply(format), sink, depth + 1);
            return after;
        }
        return null;
    }

    /// <summary>Takes back the bracket that opened [*intraword*] emphasis.</summary>
    private static void RemoveLast(InlineSink sink, char ch)
    {
        sink.Flush();
        for (var k = sink.Output.Count - 1; k >= 0; k--)
        {
            if (sink.Output[k] is not HText text)
                continue;
            if (text.Text.EndsWith(ch))
                sink.Output[k] = new HText(text.Text[..^1], text.Format);
            if (sink.Output[k] is HText { Text.Length: 0 })
                sink.Output.RemoveAt(k);
            return;
        }
    }

    private void Link(string content, string url, HCharFormat format, InlineSink sink, int depth)
    {
        // "text(title)":url — the title in brackets is a tooltip, not text.
        if (TitlePattern().Match(content) is { Success: true } titled)
            content = titled.Groups[1].Value;
        var link = new HLink(Decode(url));
        var inner = new InlineSink(link.Content);
        Inlines(new Closers(content), 0, content.Length, format, inner, depth + 1);
        inner.Flush();
        sink.Add(link);
    }

    /// <summary>Punctuation after a URL ends the sentence, not the URL.</summary>
    private static string TrimUrl(string url)
    {
        url = url.TrimEnd('.', ',', ';', ':', '!', '?', '\'', '"');
        if (url.EndsWith(')') && url.Count(c => c == '(') < url.Count(c => c == ')'))
            url = url[..^1];
        return url;
    }

    /// <summary>Text with its HTML entities decoded.</summary>
    private static string Decode(string text) => text.Contains('&', StringComparison.Ordinal) ? System.Net.WebUtility.HtmlDecode(text) : text;

    [GeneratedRegex(@"\G\[(\d{1,6})\]")]
    private static partial Regex FootnoteReference();

    [GeneratedRegex(@"\G\[""([^""\n]{1,2000})"":([^\]\s]{1,2000})\]")]
    private static partial Regex BracketLink();

    [GeneratedRegex(@"\G""([^""\n]{1,2000})"":(\S{1,2000})")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"\G!(?:\([^()\n]*\)|\{[^{}\n]*\}|[<>])*([^\s!()<>{}][^\s!()]{0,2000})(?:\(([^()\n]*)\))?!(?::(\S{1,2000}))?")]
    private static partial Regex ImagePattern();

    [GeneratedRegex(@"^(.*?)\s*\(([^()]*)\)$")]
    private static partial Regex TitlePattern();

    [GeneratedRegex(@"\G</?[A-Za-z][A-Za-z0-9]{0,15}(?:\s[^<>]{0,2000})?/?>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"</?[A-Za-z][A-Za-z0-9]{0,15}(?:\s[^<>]{0,2000})?/?>")]
    private static partial Regex AnyTag();
}
