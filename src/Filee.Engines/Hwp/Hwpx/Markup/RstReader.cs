// reStructuredText (.rst) → HDocument, without Pandoc, as docutils reads the parts documents use:
//  * sections by their underline (and overline) style, levels in the order the styles first appear; a lone first
//    title in a style of its own is the document's title (as docutils makes it);
//  * paragraphs, "::" literal blocks, block quotes (indentation), bullet and enumerated lists (1. a) (iv) #.),
//    definition lists, field lists, line blocks, doctest blocks, transitions;
//  * grid tables (column and row spans) and simple tables, the list-table and csv-table directives;
//  * directives: code-block / code / sourcecode, image, figure, the admonitions (note, warning, ...), math, topic,
//    sidebar, rubric, container; contents, toctree, raw, include and the other ones that hold no text are left out,
//    and so are comments;
//  * footnotes ([#]_, [#label]_, [1]_ with their ".. [#]" definitions), hyperlink targets (".. _name: url") for
//    `named`_ references, substitutions (|name| with replace:: or image::);
//  * inline: **strong**, *emphasis*, ``literals``, `interpreted text` (italic, as docutils shows a title reference),
//    the roles :sub: :sup: :code: :literal: :emphasis: :strong: :math: (others keep their text), `links <url>`_,
//    standalone URLs and backslash escapes, with reST's rules for where inline markup may start and end.
// Nested quotes, lists and directives count against DocumentLimits' nesting depth.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Filee.Engines.Documents;

namespace Filee.Engines.Hwp.Hwpx.Markup;

/// <summary>Reads reStructuredText into the document model.</summary>
internal sealed partial class RstReader
{
    private const int MaxInlineDepth = 4;

    private static readonly HCharFormat Plain = default;

    private readonly string _baseFolder;
    private readonly DocumentBudget _budget;
    private readonly Dictionary<string, string> _targets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string? Text, string? Image)> _substitutions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<HBlock>> _footnotes = new(StringComparer.Ordinal);
    private readonly List<List<HBlock>> _autoFootnotes = [];
    private readonly List<(string Label, HNote Note)> _references = [];
    private readonly List<(string Style, HParagraph Heading)> _headings = [];

    private RstReader(string baseFolder, DocumentLimits limits)
    {
        _baseFolder = baseFolder;
        _budget = new DocumentBudget(limits);
    }

    public static HDocument Read(string path, string media) =>
        Read(MarkupBlocks.ReadText(path, DocumentLimits.Default, "reStructuredText"), Path.GetDirectoryName(Path.GetFullPath(path))!, DocumentLimits.Default);

    internal static HDocument Read(string text, string baseFolder, DocumentLimits limits)
    {
        var reader = new RstReader(baseFolder, limits);
        var lines = MarkupBlocks.Lines(text).Split('\n').Select(ExpandTabs).ToList();
        reader.Definitions(lines);
        var section = new HSection();
        reader.Blocks(lines, section.Blocks, 0, 0, sections: true);

        // Notes: numbered and labelled ones by their label, auto-numbered ones in order.
        var auto = 0;
        foreach (var (label, note) in reader._references)
        {
            var blocks = label == "#" ? auto < reader._autoFootnotes.Count ? reader._autoFootnotes[auto++] : null
                : reader._footnotes.GetValueOrDefault(label);
            if (blocks is not null)
                note.Blocks.AddRange(blocks);
        }

        var title = reader.Levels(section.Blocks);
        var document = new HDocument { Title = title };
        document.Sections.Add(section);
        return document;
    }

    private static string ExpandTabs(string line)
    {
        if (!line.Contains('\t', StringComparison.Ordinal))
            return line;
        var sb = new StringBuilder();
        foreach (var ch in line)
        {
            if (ch == '\t')
                sb.Append(' ', 8 - sb.Length % 8);
            else
                sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Heading levels by the order their styles first appear. A first heading in a style no other heading has, with
    /// nothing before it, is the title (docutils' doctitle); the levels then start at the next style.
    /// </summary>
    private string? Levels(List<HBlock> blocks)
    {
        if (_headings.Count == 0)
            return null;
        string? title = null;
        var first = _headings[0];
        if (_headings.Count(h => h.Style == first.Style) == 1 && blocks.Count > 0 && ReferenceEquals(blocks[0], first.Heading))
        {
            title = HDocumentWalker.Text(first.Heading.Inlines);
            blocks.RemoveAt(0);
            _headings.RemoveAt(0);
        }
        var order = new List<string>();
        foreach (var (style, heading) in _headings)
        {
            if (!order.Contains(style))
                order.Add(style);
            heading.HeadingLevel = Math.Min(order.IndexOf(style) + 1, 9);
        }
        return title;
    }

    // ───────────────────────── Definitions ─────────────────────────

    [GeneratedRegex(@"^\s*\.\.\s+_([^:`][^:]*|`[^`]+`):\s*(\S*)\s*$")]
    private static partial Regex Target();

    [GeneratedRegex(@"^\s*\.\.\s+\|([^|]+)\|\s+(replace|image|unicode)::\s*(.*)$")]
    private static partial Regex Substitution();

    /// <summary>Hyperlink targets and substitution definitions, wherever they are (references may come first).</summary>
    private void Definitions(List<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (Target().Match(lines[i]) is { Success: true } target)
            {
                var url = target.Groups[2].Value;
                // A target's URL may go on on the indented lines below.
                for (var k = i + 1; k < lines.Count && lines[k].Length > 0 && char.IsWhiteSpace(lines[k][0]) && lines[k].Trim().Length > 0; k++)
                    url += lines[k].Trim();
                _targets.TryAdd(Name(target.Groups[1].Value.Trim('`')), url);
            }
            else if (Substitution().Match(lines[i]) is { Success: true } substitution)
            {
                var name = substitution.Groups[1].Value.Trim();
                var value = substitution.Groups[3].Value.Trim();
                var kind = substitution.Groups[2].Value;
                _substitutions.TryAdd(name, kind == "image" ? (null, value) : (value, null));
            }
        }
    }

    /// <summary>A reference name as docutils compares them: case and runs of white space don't count.</summary>
    private static string Name(string name) => WhiteSpace().Replace(name.Trim(), " ").ToLowerInvariant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    // ───────────────────────── Blocks ─────────────────────────

    [GeneratedRegex(@"^([!-/:-@\[-`{-~])\1+\s*$")]
    private static partial Regex Adornment();

    [GeneratedRegex(@"^([-*+•‣⁃])( +|$)(.*)$")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^(\(?)(\d{1,9}|#|[a-zA-Z]|[ivxlcdm]{1,8}|[IVXLCDM]{1,8})([.)])( +|$)(.*)$")]
    private static partial Regex Enumerator();

    [GeneratedRegex(@"^:((?:[^:\\]|\\.)+):(?: +(.*)|$)")]
    private static partial Regex Field();

    [GeneratedRegex(@"^\.\.\s+([\w:+.-]+)::\s*(.*)$")]
    private static partial Regex Directive();

    [GeneratedRegex(@"^\.\.\s+\[([^\]]+)\]\s*(.*)$")]
    private static partial Regex FootnoteDefinition();

    [GeneratedRegex(@"^=+( +=+)+\s*$")]
    private static partial Regex SimpleTableBorder();

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    private static bool Blank(string line) => line.Trim().Length == 0;

    /// <summary>Lines after <paramref name="start"/> that belong to an indented body (blank lines between included).</summary>
    private static int BodyEnd(List<string> lines, int start, int minimum)
    {
        var end = start;
        for (var i = start; i < lines.Count; i++)
        {
            if (Blank(lines[i]))
                continue;
            if (Indent(lines[i]) < minimum)
                break;
            end = i + 1;
        }
        return end;
    }

    /// <summary>Lines with <paramref name="count"/> leading columns cut (the body of an item, quote or directive).</summary>
    private static List<string> Dedent(IEnumerable<string> lines, int count) =>
        [.. lines.Select(l => l.Length >= count && l[..count].Trim().Length == 0 ? l[count..] : l.TrimStart())];

    /// <param name="indent">Left indent of the paragraphs (quotes).</param>
    /// <param name="listLevel">Nesting of the lists the blocks are in.</param>
    /// <param name="sections">Section titles may stand here (not inside quotes, lists or directives).</param>
    private void Blocks(List<string> lines, List<HBlock> output, int indent, int listLevel, bool sections)
    {
        var literalNext = false;
        for (var i = 0; i < lines.Count;)
        {
            var line = lines[i];
            if (Blank(line))
            {
                i++;
                continue;
            }

            if (Indent(line) > 0)
            {
                var common = Enumerable.Range(i, lines.Count - i).TakeWhile(k => Blank(lines[k]) || Indent(lines[k]) > 0)
                    .Where(k => !Blank(lines[k])).Select(k => Indent(lines[k])).DefaultIfEmpty(0).Min();
                var end = BodyEnd(lines, i, 1);
                var body = Dedent(lines.GetRange(i, end - i), common);
                if (literalNext)
                {
                    // The literal block after "::": the text as it is.
                    while (body.Count > 0 && Blank(body[^1]))
                        body.RemoveAt(body.Count - 1);
                    MarkupBlocks.SeparateCode(output, _budget);
                    MarkupBlocks.CodeLines(body, output, _budget, indent);
                }
                else
                {
                    // A block quote (an attribution "-- name" at its end is text of it).
                    using (_budget.Enter())
                        Blocks(body, output, Math.Min(indent + MarkupBlocks.QuoteIndent, 8 * MarkupBlocks.QuoteIndent), listLevel, sections: false);
                }
                literalNext = false;
                i = end;
                continue;
            }
            literalNext = false;

            // Section titles: overline + title + underline, or title + underline.
            if (sections && Adornment().Match(line) is { Success: true } over && i + 2 < lines.Count && !Blank(lines[i + 1])
                && Adornment().Match(lines[i + 2]) is { Success: true } under && under.Groups[1].Value == over.Groups[1].Value && line.Trim().Length >= 2)
            {
                Heading(lines[i + 1].Trim(), "o" + over.Groups[1].Value, output);
                i += 3;
                continue;
            }
            if (sections && i + 1 < lines.Count && Adornment().Match(lines[i + 1]) is { Success: true } underline && lines[i + 1].Trim().Length >= Math.Min(3, line.Trim().Length)
                && !Adornment().IsMatch(line))
            {
                Heading(line.Trim(), "u" + underline.Groups[1].Value, output);
                i += 2;
                continue;
            }
            if (Adornment().IsMatch(line) && line.Trim().Length >= 4 && (i + 1 >= lines.Count || Blank(lines[i + 1])))
            {
                MarkupBlocks.Rule(output, _budget);
                i++;
                continue;
            }

            if (line.StartsWith("..", StringComparison.Ordinal) && (line.Length == 2 || line[2] == ' '))
            {
                i = Explicit(lines, i, output, indent, listLevel);
                continue;
            }
            if (line.StartsWith("+-", StringComparison.Ordinal) || line.StartsWith("+=", StringComparison.Ordinal))
            {
                var start = i;
                while (i < lines.Count && !Blank(lines[i]) && (lines[i].StartsWith('+') || lines[i].StartsWith('|')))
                    i++;
                output.Add(GridTable(lines.GetRange(start, i - start), indent));
                continue;
            }
            if (SimpleTableBorder().IsMatch(line))
            {
                i = SimpleTable(lines, i, output, indent);
                continue;
            }
            if (Bullet().Match(line) is { Success: true } || EnumeratedItem(lines, i) is not null)
            {
                i = List(lines, i, output, indent, listLevel);
                continue;
            }
            if (Field().Match(line) is { Success: true } field)
            {
                // :Author: name — the field name bold, its body after it.
                var end = BodyEnd(lines, i + 1, 1);
                var text = string.Join(' ', new[] { field.Groups[2].Value }.Concat(lines.GetRange(i + 1, end - i - 1).Select(l => l.Trim())).Where(t => t.Length > 0));
                var paragraph = Paragraph(text, indent);
                var name = Unescape(field.Groups[1].Value);
                paragraph.Inlines.InsertRange(0, [new HText(name, new HCharFormat(Bold: true)), new HText(": ", Plain)]);
                output.Add(paragraph);
                i = end;
                continue;
            }
            if (line.StartsWith("| ", StringComparison.Ordinal) || line == "|")
            {
                // A line block: its lines stay lines.
                var text = new List<string>();
                for (; i < lines.Count && (lines[i].StartsWith("| ", StringComparison.Ordinal) || lines[i] == "|" || (!Blank(lines[i]) && Indent(lines[i]) > 0)); i++)
                {
                    if (lines[i].StartsWith('|'))
                        text.Add(lines[i].Length > 2 ? lines[i][2..] : "");
                    else if (text.Count > 0)
                        text[^1] += " " + lines[i].Trim();
                }
                output.Add(Paragraph(string.Join("\n", text), indent, lineBreaks: true));
                continue;
            }
            if (line.StartsWith(">>> ", StringComparison.Ordinal))
            {
                var code = new List<string>();
                for (; i < lines.Count && !Blank(lines[i]); i++)
                    code.Add(lines[i]);
                MarkupBlocks.SeparateCode(output, _budget);
                MarkupBlocks.CodeLines(code, output, _budget, indent);
                continue;
            }

            // A paragraph; a definition list item when an indented body follows at once.
            var paragraphLines = new List<string>();
            for (; i < lines.Count && !Blank(lines[i]) && Indent(lines[i]) == 0; i++)
            {
                if (paragraphLines.Count > 0 && i + 1 < lines.Count && sections && Adornment().IsMatch(lines[i + 1]) && !Adornment().IsMatch(lines[i]))
                    break; // the next line is a section title
                paragraphLines.Add(lines[i]);
            }
            if (i < lines.Count && !Blank(lines[i]) && Indent(lines[i]) > 0 && paragraphLines.Count == 1)
            {
                // Term, then its definition indented right under it.
                var term = Paragraph(paragraphLines[0].Split(" : ")[0], indent);
                Bold(term.Inlines);
                output.Add(term);
                var end = BodyEnd(lines, i, 1);
                var common = lines.GetRange(i, end - i).Where(l => !Blank(l)).Select(Indent).DefaultIfEmpty(0).Min();
                using (_budget.Enter())
                    Blocks(Dedent(lines.GetRange(i, end - i), common), output, Math.Min(indent + MarkupBlocks.QuoteIndent, 8 * MarkupBlocks.QuoteIndent), listLevel, sections: false);
                i = end;
                continue;
            }
            var joined = string.Join(' ', paragraphLines.Select(l => l.Trim()));
            if (joined.EndsWith("::", StringComparison.Ordinal) && !joined.EndsWith("\\::", StringComparison.Ordinal))
            {
                literalNext = true;
                joined = joined == "::" ? "" : joined.EndsWith(" ::", StringComparison.Ordinal) ? joined[..^3] : joined[..^1];
            }
            if (joined.Length > 0)
                output.Add(Paragraph(joined, indent));
        }
    }

    private void Heading(string text, string style, List<HBlock> output)
    {
        var heading = Paragraph(text, 0);
        heading.HeadingLevel = 1;
        output.Add(heading);
        _headings.Add((style, heading));
    }

    private static void Bold(List<HInline> inlines)
    {
        for (var i = 0; i < inlines.Count; i++)
            if (inlines[i] is HText text)
                inlines[i] = new HText(text.Text, text.Format with { Bold = true });
    }

    /// <summary>An enumerated item at line <paramref name="i"/>: (marker, text, columns); null when it isn't one.</summary>
    private static (string Marker, string Text, int Width)? EnumeratedItem(List<string> lines, int i)
    {
        if (Enumerator().Match(lines[i]) is not { Success: true } match)
            return null;
        var open = match.Groups[1].Value;
        var close = match.Groups[3].Value;
        if (open == "(" && close != ")")
            return null;
        // "A. Smith wrote" is text: a one-letter enumerator needs a blank line or another item after its item.
        var text = match.Groups[5].Value;
        if (match.Groups[4].Value.Length == 0 && text.Length > 0)
            return null;
        if (char.IsLetter(match.Groups[2].Value[0]) && match.Groups[2].Value.Length == 1 && i + 1 < lines.Count && !Blank(lines[i + 1]) && Indent(lines[i + 1]) == 0
            && Enumerator().Match(lines[i + 1]) is not { Success: true })
            return null;
        return (open + match.Groups[2].Value + close, text, match.Length - text.Length);
    }

    private int List(List<string> lines, int i, List<HBlock> output, int indent, int listLevel)
    {
        var level = Math.Min(listLevel, 8);
        var bullet = Bullet().Match(lines[i]).Success;
        var firstMarker = bullet ? Bullet().Match(lines[i]).Groups[1].Value : EnumeratedItem(lines, i)!.Value.Marker;
        HNumbering numbering;
        if (bullet)
        {
            numbering = MarkupBlocks.Numbering(ordered: false, level: level);
        }
        else
        {
            var core = firstMarker.Trim('(', ')', '.');
            var (format, start) = core switch
            {
                "#" => ("DIGIT", 1),
                _ when int.TryParse(core, NumberStyles.None, CultureInfo.InvariantCulture, out var n) => ("DIGIT", n),
                _ when core.Length == 1 && core[0] is not ('i' or 'I') && char.IsLetter(core[0]) => (char.IsUpper(core[0]) ? "LATIN_CAPITAL" : "LATIN_SMALL", char.ToLowerInvariant(core[0]) - 'a' + 1),
                _ when core.All(c => "ivxlcdm".Contains(c, StringComparison.Ordinal)) => ("ROMAN_SMALL", 1),
                _ => ("ROMAN_CAPITAL", 1),
            };
            numbering = MarkupBlocks.Numbering(ordered: true, start, format, level);
        }

        while (i < lines.Count)
        {
            if (Blank(lines[i]))
            {
                var next = i;
                while (next < lines.Count && Blank(lines[next]))
                    next++;
                if (next >= lines.Count || !SameKind(lines, next, bullet, firstMarker))
                    break;
                i = next;
            }
            if (!SameKind(lines, i, bullet, firstMarker))
                break;
            string text;
            int width;
            if (bullet)
            {
                var match = Bullet().Match(lines[i]);
                text = match.Groups[3].Value;
                width = match.Groups[1].Length + Math.Max(1, match.Groups[2].Length);
            }
            else
            {
                (_, text, width) = EnumeratedItem(lines, i)!.Value;
            }
            var end = BodyEnd(lines, i + 1, Math.Max(1, width));
            var body = new List<string> { text };
            body.AddRange(Dedent(lines.GetRange(i + 1, end - i - 1), width));
            var blocks = new List<HBlock>();
            using (_budget.Enter())
                Blocks(body, blocks, indent, listLevel + 1, sections: false);
            var first = true;
            foreach (var block in blocks)
            {
                if (block is HParagraph { List: null, HeadingLevel: 0 } paragraph && !MarkupBlocks.IsCode(paragraph))
                {
                    paragraph.List = new HListRef(numbering, level, Numbered: first);
                }
                first = false;
                output.Add(block);
            }
            if (blocks.Count == 0)
            {
                _budget.Paragraph();
                output.Add(new HParagraph { List = new HListRef(numbering, level, Numbered: true) });
            }
            i = end;
        }
        return i;
    }

    private static bool SameKind(List<string> lines, int i, bool bullet, string firstMarker)
    {
        if (bullet)
            return Bullet().Match(lines[i]) is { Success: true } match && match.Groups[1].Value == firstMarker;
        if (EnumeratedItem(lines, i) is not { } item)
            return false;
        static string Shape(string marker) => (marker.StartsWith('(') ? "(" : "") + (marker.EndsWith(')') ? ")" : ".");
        return Shape(item.Marker) == Shape(firstMarker);
    }

    // ───────────────────────── Explicit markup ─────────────────────────

    private static readonly HashSet<string> Admonitions = new(StringComparer.OrdinalIgnoreCase)
    {
        "note", "warning", "tip", "important", "hint", "caution", "danger", "attention", "error", "seealso", "admonition", "topic", "sidebar",
    };

    private static readonly HashSet<string> WithoutText = new(StringComparer.OrdinalIgnoreCase)
    {
        "contents", "toctree", "raw", "include", "meta", "index", "sectnum", "section-numbering", "header", "footer", "role", "default-role",
        "highlight", "title", "target-notes", "footnotes", "only", "literalinclude", "autosummary", "automodule", "autoclass", "autofunction",
    };

    /// <summary>A directive, footnote, target, substitution or comment at line <paramref name="i"/>; returns the line after it.</summary>
    private int Explicit(List<string> lines, int i, List<HBlock> output, int indent, int listLevel)
    {
        var line = lines[i];
        var end = BodyEnd(lines, i + 1, 1);
        var body = lines.GetRange(i + 1, end - i - 1);
        var common = body.Where(l => !Blank(l)).Select(Indent).DefaultIfEmpty(3).Min();
        var content = Dedent(body, common);

        if (FootnoteDefinition().Match(line) is { Success: true } footnote)
        {
            var label = footnote.Groups[1].Value.Trim();
            var blocks = new List<HBlock>();
            content.Insert(0, footnote.Groups[2].Value);
            using (_budget.Enter())
                Blocks(content, blocks, 0, 0, sections: false);
            if (label is "#" or "*")
                _autoFootnotes.Add(blocks);
            else
                _footnotes.TryAdd(label, blocks);
            return end;
        }
        if (Target().IsMatch(line) || Substitution().IsMatch(line) || Directive().Match(line) is not { Success: true } directive)
            return end; // a target, a substitution (read before) or a comment

        var name = directive.Groups[1].Value.ToLowerInvariant();
        var argument = directive.Groups[2].Value.Trim();
        // The directive's options (":width: 50%") come first, then its content after a blank line.
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var at = 0;
        for (; at < content.Count && Field().Match(content[at]) is { Success: true } option; at++)
            options[option.Groups[1].Value] = option.Groups[2].Value.Trim();
        var text = content.Skip(at).SkipWhile(Blank).ToList();
        while (text.Count > 0 && Blank(text[^1]))
            text.RemoveAt(text.Count - 1);

        switch (name)
        {
            case "code-block" or "code" or "sourcecode" or "parsed-literal":
                MarkupBlocks.SeparateCode(output, _budget);
                MarkupBlocks.CodeLines(text, output, _budget, indent);
                break;
            case "math":
                foreach (var formula in (argument.Length > 0 ? [argument] : text).Where(l => !Blank(l)))
                {
                    _budget.Paragraph();
                    var paragraph = new HParagraph { Format = new HParaFormat(Align: HAlign.Center, Left: indent > 0 ? indent : null) };
                    paragraph.Inlines.Add(new HText(formula.Trim(), new HCharFormat(Italic: true)));
                    output.Add(paragraph);
                }
                break;
            case "image" or "figure":
                {
                    _budget.Paragraph();
                    var paragraph = new HParagraph { Format = indent > 0 ? new HParaFormat(Left: indent) : default };
                    if (Picture(argument, options) is { } picture)
                    {
                        if (options.TryGetValue("target", out var targetUrl) && targetUrl.Length > 0)
                        {
                            var link = new HLink(targetUrl);
                            link.Content.Add(picture);
                            paragraph.Inlines.Add(link);
                        }
                        else
                        {
                            paragraph.Inlines.Add(picture);
                        }
                        output.Add(paragraph);
                    }
                    if (name == "figure" && text.Count > 0)
                    {
                        // The caption is the first paragraph; a legend may follow.
                        using (_budget.Enter())
                            Blocks(text, output, indent, listLevel, sections: false);
                    }
                    break;
                }
            case var admonition when Admonitions.Contains(admonition):
                {
                    var title = admonition is "admonition" or "topic" or "sidebar" ? argument : char.ToUpperInvariant(admonition[0]) + admonition[1..];
                    if (admonition is not ("admonition" or "topic" or "sidebar") && argument.Length > 0)
                        text.Insert(0, argument);
                    if (title.Length > 0)
                    {
                        var heading = Paragraph(title, indent);
                        Bold(heading.Inlines);
                        output.Add(heading);
                    }
                    using (_budget.Enter())
                        Blocks(text, output, Math.Min(indent + MarkupBlocks.QuoteIndent, 8 * MarkupBlocks.QuoteIndent), listLevel, sections: false);
                    break;
                }
            case "rubric":
                {
                    var rubric = Paragraph(argument, indent);
                    Bold(rubric.Inlines);
                    output.Add(rubric);
                    break;
                }
            case "list-table":
                output.Add(ListTable(text, options, argument, indent));
                break;
            case "csv-table":
                output.Add(CsvTable(text, options, argument, indent));
                break;
            case "epigraph" or "highlights" or "pull-quote":
                using (_budget.Enter())
                    Blocks(text, output, Math.Min(indent + MarkupBlocks.QuoteIndent, 8 * MarkupBlocks.QuoteIndent), listLevel, sections: false);
                break;
            case var other when WithoutText.Contains(other):
                break;
            default:
                // container, compound, class, line-block and directives Filee doesn't know: their content is text.
                using (_budget.Enter())
                    Blocks(text, output, indent, listLevel, sections: false);
                break;
        }
        return end;
    }

    private HImage? Picture(string uri, Dictionary<string, string> options)
    {
        if (MarkdownReader.ResolveImage(uri, _baseFolder) is not { } path)
            return null;
        _budget.Object();
        return new HImage(path)
        {
            Width = options.TryGetValue("width", out var width) ? HwpxUnits.ParseLength(width) : null,
            Height = options.TryGetValue("height", out var height) ? HwpxUnits.ParseLength(height) : null,
        };
    }

    // ───────────────────────── Tables ─────────────────────────

    /// <summary>
    /// A grid table: column lines from the "+" of its top border, rows between "+---" lines, a "+===" line under the
    /// header. A cell spans the columns whose "|" is missing in its rows and the rows whose border under it is blank.
    /// </summary>
    private HTable GridTable(List<string> lines, int indent)
    {
        _budget.Object();
        var table = new HTable { Indent = indent };
        var top = lines[0];
        var columns = new List<int>();
        for (var c = 0; c < top.Length; c++)
            if (top[c] == '+')
                columns.Add(c);
        if (columns.Count < 2)
            return Fallback(table, lines);
        if (columns.Count - 1 > _budget.Limits.MaxTableColumns)
            columns = columns.Take(_budget.Limits.MaxTableColumns + 1).ToList();

        // Row borders: lines with a "+" on a column line and a segment of "-" or "=" (a border under a cell that spans
        // rows starts with "|" and is blank across that cell).
        bool Segment(string line, int c, char rule) =>
            Enumerable.Range(columns[c] + 1, columns[c + 1] - columns[c] - 1).All(x => x < line.Length && line[x] == rule);
        bool IsBorder(string line) =>
            columns.Any(x => x < line.Length && line[x] == '+')
            && Enumerable.Range(0, columns.Count - 1).Any(c => Segment(line, c, '-') || Segment(line, c, '='));
        var borders = new List<int>();
        for (var r = 0; r < lines.Count; r++)
            if (IsBorder(lines[r]))
                borders.Add(r);
        var header = lines.FindIndex(l => IsBorder(l) && Enumerable.Range(0, columns.Count - 1).Any(c => Segment(l, c, '=')));
        var rows = borders.Count - 1;
        if (rows < 1)
            return Fallback(table, lines);
        var width = columns.Count - 1;
        var taken = new bool[rows, width];
        var grid = new List<HRow>();
        for (var r = 0; r < rows; r++)
            grid.Add(new HRow { Header = header > borders[r] });

        char At(int line, int column) => line < lines.Count && column < lines[line].Length ? lines[line][column] : ' ';

        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < width; c++)
            {
                if (taken[r, c])
                    continue;
                var firstLine = borders[r] + 1;
                // Columns: on while the "|" right of the cell is missing on its first text line.
                var colSpan = 1;
                while (c + colSpan < width && At(firstLine, columns[c + colSpan]) != '|')
                    colSpan++;
                // Rows: on while the border under the cell is blank across its columns.
                var rowSpan = 1;
                while (r + rowSpan < rows && Enumerable.Range(columns[c] + 1, columns[c + colSpan] - columns[c] - 1).All(x => At(borders[r + rowSpan], x) == ' '))
                    rowSpan++;
                for (var rr = r; rr < r + rowSpan; rr++)
                    for (var cc = c; cc < c + colSpan; cc++)
                        taken[rr, cc] = true;
                var text = new List<string>();
                for (var l = firstLine; l < borders[r + rowSpan]; l++)
                {
                    var line = lines[l];
                    var from = Math.Min(columns[c] + 1, line.Length);
                    var to = Math.Min(columns[c + colSpan], line.Length);
                    text.Add(to > from ? line[from..to] : "");
                }
                var cell = new HCell { Column = c, RowSpan = rowSpan, ColSpan = colSpan };
                var common = text.Where(t => !Blank(t)).Select(Indent).DefaultIfEmpty(0).Min();
                using (_budget.Enter())
                    Blocks(Dedent(text.Select(t => t.TrimEnd()), common), cell.Blocks, 0, 0, sections: false);
                if (grid[r].Header)
                    foreach (var paragraph in cell.Blocks.OfType<HParagraph>())
                        Bold(paragraph.Inlines);
                if (cell.Blocks.Count == 0)
                {
                    _budget.Paragraph();
                    cell.Blocks.Add(new HParagraph());
                }
                grid[r].Cells.Add(cell);
            }
        }
        table.Rows.AddRange(grid.Where(row => row.Cells.Count > 0));
        table.ColumnCount = width;
        return table;
    }

    /// <summary>A table that can't be laid out keeps its lines as code.</summary>
    private HTable Fallback(HTable table, List<string> lines)
    {
        var cell = new HCell();
        MarkupBlocks.CodeLines(lines, cell.Blocks, _budget);
        table.Rows.Add(new HRow { Cells = { cell } });
        table.ColumnCount = 1;
        return table;
    }

    /// <summary>A simple table: "===" borders mark the columns; a second border under the first rows makes them the header.</summary>
    private int SimpleTable(List<string> lines, int i, List<HBlock> output, int indent)
    {
        var border = lines[i];
        var spans = new List<(int Start, int End)>();
        for (var c = 0; c < border.Length;)
        {
            if (border[c] != '=')
            {
                c++;
                continue;
            }
            var start = c;
            while (c < border.Length && border[c] == '=')
                c++;
            spans.Add((start, c));
        }
        spans = spans.Take(_budget.Limits.MaxTableColumns).ToList();
        var rows = new List<List<string>>();
        var borders = 1;
        var header = 0;
        var j = i + 1;
        for (; j < lines.Count; j++)
        {
            var line = lines[j];
            if (SimpleTableBorder().IsMatch(line))
            {
                borders++;
                if (borders == 2 && j + 1 < lines.Count && !Blank(lines[j + 1]))
                {
                    header = rows.Count;
                    continue;
                }
                j++;
                break;
            }
            if (Blank(line))
                continue;
            if (line.Length > spans[0].Start && spans[0].End > spans[0].Start && line[..Math.Min(spans[0].End, line.Length)].Trim().Length == 0 && rows.Count > 0)
            {
                // A line with nothing in the first column goes on with the row above.
                for (var c = 0; c < spans.Count; c++)
                    rows[^1][c] = (rows[^1][c] + " " + Slice(line, spans, c)).Trim();
                continue;
            }
            rows.Add([.. Enumerable.Range(0, spans.Count).Select(c => Slice(line, spans, c))]);
        }
        _budget.Object();
        var table = new HTable { Indent = indent, ColumnCount = Math.Max(1, spans.Count) };
        for (var r = 0; r < rows.Count; r++)
        {
            var row = new HRow { Header = r < header };
            foreach (var text in rows[r])
            {
                var cell = new HCell();
                using (_budget.Enter())
                    cell.Blocks.Add(Paragraph(text, 0));
                if (row.Header)
                    Bold(((HParagraph)cell.Blocks[0]).Inlines);
                row.Cells.Add(cell);
            }
            table.Rows.Add(row);
        }
        if (table.Rows.Count > 0)
            output.Add(table);
        return j;
    }

    /// <summary>The text of column <paramref name="c"/> of a simple table's line (the last column runs to the end).</summary>
    private static string Slice(string line, List<(int Start, int End)> spans, int c)
    {
        var start = Math.Min(spans[c].Start, line.Length);
        var end = c + 1 < spans.Count ? Math.Min(spans[c + 1].Start, line.Length) : line.Length;
        return end > start ? line[start..end].Trim() : "";
    }

    /// <summary>The list-table directive: a bullet list of rows, each a bullet list of cells.</summary>
    private HTable ListTable(List<string> text, Dictionary<string, string> options, string title, int indent)
    {
        _budget.Object();
        var table = new HTable { Indent = indent };
        var headerRows = options.TryGetValue("header-rows", out var h) && int.TryParse(h, out var n) ? n : 0;
        if (title.Length > 0)
            table.Caption.Add(Paragraph(title, 0));
        var rows = new List<List<List<string>>>();
        foreach (var line in text)
        {
            if (line.StartsWith("* ", StringComparison.Ordinal) || line == "*")
            {
                rows.Add([]);
                var rest = line.Length > 2 ? line[2..] : "";
                if (rest.TrimStart().StartsWith("- ", StringComparison.Ordinal) || rest.Trim() == "-")
                    rows[^1].Add([rest.TrimStart().Length > 2 ? rest.TrimStart()[2..] : ""]);
            }
            else if (rows.Count > 0 && (line.TrimStart().StartsWith("- ", StringComparison.Ordinal) || line.Trim() == "-") && Indent(line) <= 2)
            {
                rows[^1].Add([line.TrimStart().Length > 2 ? line.TrimStart()[2..] : ""]);
            }
            else if (rows.Count > 0 && rows[^1].Count > 0)
            {
                rows[^1][^1].Add(line.Length > 4 ? line[Math.Min(4, Indent(line))..] : line.Trim());
            }
        }
        var limit = _budget.Limits.MaxTableColumns;
        for (var r = 0; r < rows.Count; r++)
        {
            var row = new HRow { Header = r < headerRows };
            foreach (var cellLines in rows[r].Take(limit))
            {
                var cell = new HCell();
                using (_budget.Enter())
                    Blocks(cellLines, cell.Blocks, 0, 0, sections: false);
                if (row.Header)
                    foreach (var paragraph in cell.Blocks.OfType<HParagraph>())
                        Bold(paragraph.Inlines);
                if (cell.Blocks.Count == 0)
                {
                    _budget.Paragraph();
                    cell.Blocks.Add(new HParagraph());
                }
                row.Cells.Add(cell);
            }
            table.Rows.Add(row);
        }
        table.ColumnCount = Math.Max(1, table.Rows.Count == 0 ? 1 : table.Rows.Max(r => r.Cells.Count));
        if (table.Rows.Count == 0)
            table.Rows.Add(new HRow { Cells = { new HCell { Blocks = { new HParagraph() } } } });
        return table;
    }

    /// <summary>The csv-table directive: comma-separated rows (quotes as in CSV), :header: and :header-rows:.</summary>
    private HTable CsvTable(List<string> text, Dictionary<string, string> options, string title, int indent)
    {
        var lines = new List<string>();
        if (options.TryGetValue("header", out var headerLine))
            lines.Add(headerLine);
        lines.AddRange(text.Where(l => !Blank(l)));
        var headerRows = headerLine is not null ? 1 : options.TryGetValue("header-rows", out var h) && int.TryParse(h, out var n) ? n : 0;
        var asList = new List<string>();
        foreach (var line in lines)
        {
            asList.Add("* " + string.Join("\n  ", Csv(line).Select(c => "- " + c)));
        }
        options["header-rows"] = headerRows.ToString(CultureInfo.InvariantCulture);
        return ListTable([.. asList.SelectMany(l => l.Split('\n'))], options, title, indent);
    }

    private static List<string> Csv(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    sb.Append(ch);
                }
            }
            else if (ch == '"')
            {
                quoted = true;
            }
            else if (ch == ',')
            {
                cells.Add(sb.ToString().Trim());
                sb.Clear();
            }
            else
            {
                sb.Append(ch);
            }
        }
        cells.Add(sb.ToString().Trim());
        return cells;
    }

    // ───────────────────────── Inlines ─────────────────────────

    private HParagraph Paragraph(string text, int indent, bool lineBreaks = false)
    {
        _budget.Paragraph();
        var paragraph = new HParagraph { Format = indent > 0 ? new HParaFormat(Left: indent) : default };
        var sink = new InlineSink(paragraph.Inlines);
        Inlines(new Closers(text), 0, text.Length, Plain, sink, 0, lineBreaks);
        sink.Flush();
        return paragraph;
    }

    /// <summary>What may stand before inline markup's start (besides white space and the start of the text).</summary>
    private static bool Before(char ch) =>
        char.IsWhiteSpace(ch) || ch is '-' or ':' or '/' or '\'' or '"' or '<' or '(' or '[' or '{' or '\0'
        || CharUnicodeInfo.GetUnicodeCategory(ch) is UnicodeCategory.OpenPunctuation or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OtherPunctuation;

    /// <summary>What may stand after inline markup's end (besides white space and the end of the text).</summary>
    private static bool After(char ch) =>
        char.IsWhiteSpace(ch) || ch is '-' or '.' or ',' or ':' or ';' or '!' or '?' or '\\' or '/' or '\'' or '"' or ')' or ']' or '}' or '>' or '\0'
        || CharUnicodeInfo.GetUnicodeCategory(ch) is UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OtherPunctuation;

    private void Inlines(Closers text, int start, int end, HCharFormat format, InlineSink sink, int depth, bool lineBreaks = false)
    {
        var s = text.Text;
        char At(int index) => index >= start && index < end ? s[index] : '\0';
        bool Closes(int at, int length) => at > 0 && !char.IsWhiteSpace(s[at - 1]) && s[at - 1] != '\\' && (at + length >= s.Length || After(s[at + length]));

        for (var i = start; i < end;)
        {
            var ch = s[i];
            if (ch == '\n')
            {
                if (lineBreaks)
                    sink.Add(new HLineBreak(format));
                else
                    sink.Text(' ', format);
                i++;
                continue;
            }
            if (ch == '\\')
            {
                // An escaped character is text; an escaped space is nothing (it separates markup from a word).
                if (i + 1 < end && !char.IsWhiteSpace(s[i + 1]))
                    sink.Text(s[i + 1], format);
                i += i + 1 < end ? 2 : 1;
                continue;
            }
            var opens = Before(At(i - 1)) && i + 1 < end;
            if (opens && ch == '*' && At(i + 1) == '*' && !char.IsWhiteSpace(At(i + 2)) && depth < MaxInlineDepth)
            {
                var close = text.Find("**", i + 3, end, at => Closes(at, 2));
                if (close > 0)
                {
                    Inlines(text, i + 2, close, format with { Bold = true }, sink, depth + 1);
                    i = close + 2;
                    continue;
                }
            }
            else if (opens && ch == '*' && !char.IsWhiteSpace(At(i + 1)) && At(i + 1) != '*' && depth < MaxInlineDepth)
            {
                var close = text.Find("*", i + 2, end, at => Closes(at, 1) && s[at - 1] != '*' && (at + 1 >= s.Length || s[at + 1] != '*'));
                if (close > 0)
                {
                    Inlines(text, i + 1, close, format with { Italic = true }, sink, depth + 1);
                    i = close + 1;
                    continue;
                }
            }
            if (opens && ch == '`' && At(i + 1) == '`' && !char.IsWhiteSpace(At(i + 2)))
            {
                var close = text.Find("``", i + 3, end, at => at > 0 && !char.IsWhiteSpace(s[at - 1]) && (at + 2 >= s.Length || After(s[at + 2])));
                if (close > 0)
                {
                    sink.Text(s[(i + 2)..close], format with { Shade = HtmlReader.CodeShade });
                    i = close + 2;
                    continue;
                }
            }
            if (opens && ch == ':' && Role().Match(s, i) is { Success: true } role && role.Index + role.Length <= end)
            {
                Interpreted(Unescape(role.Groups[2].Value), role.Groups[1].Value, format, sink, depth);
                i += role.Length;
                continue;
            }
            if (opens && ch == '`' && !char.IsWhiteSpace(At(i + 1)))
            {
                var close = text.Find("`", i + 2, end, at => at > 0 && !char.IsWhiteSpace(s[at - 1]) && s[at - 1] != '\\');
                if (close > 0 && Suffix(s, close + 1, end) is var (kind, suffixLength) && (kind.Length > 0 || close + 1 >= s.Length || After(s[close + 1])))
                {
                    var content = s[(i + 1)..close];
                    if (kind is "_" or "__")
                        Reference(content, format, sink, depth);
                    else
                        Interpreted(Unescape(content), kind.Trim(':'), format, sink, depth);
                    i = close + 1 + suffixLength;
                    continue;
                }
            }
            if (opens && ch == '|' && !char.IsWhiteSpace(At(i + 1)))
            {
                var close = text.Find("|", i + 2, end, at => at > 0 && !char.IsWhiteSpace(s[at - 1]));
                if (close > 0 && _substitutions.TryGetValue(s[(i + 1)..close], out var substitution))
                {
                    var after = close + 1;
                    while (after < end && s[after] == '_')
                        after++;
                    if (substitution.Image is { } image && MarkdownReader.ResolveImage(image, _baseFolder) is { } path)
                    {
                        _budget.Object();
                        sink.Add(new HImage(path));
                    }
                    else if (substitution.Text is { } replacement)
                    {
                        sink.Text(replacement, format);
                    }
                    i = after;
                    continue;
                }
            }
            if (opens && ch == '[' && FootnoteReference().Match(s, i) is { Success: true } footnote && footnote.Index + footnote.Length <= end
                && (footnote.Index + footnote.Length >= s.Length || After(s[footnote.Index + footnote.Length])))
            {
                var label = footnote.Groups[1].Value;
                if (label.StartsWith('#') || label == "*" || char.IsAsciiDigit(label[0]))
                {
                    var note = new HNote(endnote: false);
                    _references.Add((label == "*" ? "#" : label, note));
                    sink.Add(note);
                }
                else
                {
                    sink.Text($"[{label}]", format); // a citation
                }
                i += footnote.Length;
                continue;
            }
            if (opens && ch == '_' && At(i + 1) == '`')
            {
                // An inline target: _`text` is the text.
                var close = text.Find("`", i + 3, end, at => at > 0 && !char.IsWhiteSpace(s[at - 1]) && s[at - 1] != '\\');
                if (close > 0)
                {
                    sink.Text(Unescape(s[(i + 2)..close]), format);
                    i = close + 1;
                    continue;
                }
            }
            if ((ch is 'h' or 'f' or 'm') && Before(At(i - 1)) && BareUrl().Match(s, i) is { Success: true } url && url.Index + url.Length <= end)
            {
                var target = url.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', '>', '\'');
                var link = new HLink(target);
                link.Content.Add(new HText(target, format));
                sink.Add(link);
                i += target.Length;
                continue;
            }
            if (char.IsLetterOrDigit(ch) && Before(At(i - 1)) && SimpleReference().Match(s, i) is { Success: true } simple && simple.Index + simple.Length <= end
                && (simple.Index + simple.Length >= s.Length || After(s[simple.Index + simple.Length])))
            {
                var name = simple.Groups[1].Value;
                if (_targets.TryGetValue(Name(name), out var targetUrl) && targetUrl.Length > 0)
                {
                    var link = new HLink(targetUrl);
                    link.Content.Add(new HText(name, format));
                    sink.Add(link);
                }
                else
                {
                    sink.Text(name, format);
                }
                i += simple.Length;
                continue;
            }
            sink.Text(ch, format);
            i++;
        }
    }

    /// <summary>What follows the closing backquote: "_" / "__" (a reference), ":role:" or nothing.</summary>
    private static (string Kind, int Length) Suffix(string s, int at, int end)
    {
        if (at + 1 < end && s[at] == '_' && s[at + 1] == '_')
            return ("__", 2);
        if (at < end && s[at] == '_')
            return ("_", 1);
        if (at < end && s[at] == ':' && SuffixRole().Match(s, at) is { Success: true } role && role.Index + role.Length <= end)
            return (role.Value, role.Length);
        return ("", 0);
    }

    /// <summary>Interpreted text with a role: sub, sup, code, emphasis, strong, math; the rest is its text.</summary>
    private void Interpreted(string content, string role, HCharFormat format, InlineSink sink, int depth)
    {
        // :ref:`text <label>` and :doc:`text <path>` show the text.
        if (content.Length <= 4000 && Embedded().Match(content) is { Success: true } embedded && role is not ("code" or "literal" or "math"))
            content = embedded.Groups[1].Value.Trim();
        var applied = role.ToLowerInvariant() switch
        {
            "sub" or "subscript" => format with { Subscript = true, Superscript = null },
            "sup" or "superscript" => format with { Superscript = true, Subscript = null },
            "code" or "literal" or "file" or "samp" or "command" or "kbd" or "program" => format with { Shade = HtmlReader.CodeShade },
            "emphasis" or "title-reference" or "title" or "t" or "" or "dfn" => format with { Italic = true },
            "strong" => format with { Bold = true },
            "math" => format with { Italic = true },
            _ => format,
        };
        sink.Text(content, applied);
    }

    /// <summary>`text &lt;url&gt;`_, `text &lt;name_&gt;`_ or `name`_.</summary>
    private void Reference(string content, HCharFormat format, InlineSink sink, int depth)
    {
        string text;
        string? url;
        if (content.Length <= 4000 && Embedded().Match(content) is { Success: true } embedded)
        {
            text = embedded.Groups[1].Value.Trim();
            var target = embedded.Groups[2].Value.Trim();
            url = target.EndsWith('_') && !target.EndsWith("\\_", StringComparison.Ordinal)
                ? _targets.GetValueOrDefault(Name(target.TrimEnd('_')))
                : Unescape(target).Replace(" ", "", StringComparison.Ordinal);
            if (text.Length == 0)
                text = target;
        }
        else
        {
            text = content;
            url = _targets.GetValueOrDefault(Name(Unescape(content)));
        }
        text = Unescape(text);
        if (string.IsNullOrWhiteSpace(url))
        {
            sink.Text(text, format);
            return;
        }
        var link = new HLink(url);
        link.Content.Add(new HText(text, format));
        sink.Add(link);
    }

    /// <summary>Text with reST's backslash escapes taken out (an escaped space is nothing).</summary>
    private static string Unescape(string text)
    {
        if (!text.Contains('\\', StringComparison.Ordinal))
            return text;
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                if (!char.IsWhiteSpace(text[i + 1]))
                    sb.Append(text[i + 1]);
                i++;
                continue;
            }
            sb.Append(text[i]);
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"\G:([A-Za-z][\w.+-]{0,40}):`((?:[^`\\]|\\.){1,4000})`")]
    private static partial Regex Role();

    [GeneratedRegex(@"\G:[A-Za-z][\w.+-]{0,40}:")]
    private static partial Regex SuffixRole();

    [GeneratedRegex(@"^((?:[^<\\]|\\.)*?)\s*<([^<>]+)>$", RegexOptions.Singleline)]
    private static partial Regex Embedded();

    [GeneratedRegex(@"\G\[(#[\w.-]{0,100}|\*|\d{1,6}|[A-Za-z][\w.-]{0,100})\]_")]
    private static partial Regex FootnoteReference();

    [GeneratedRegex(@"\G([A-Za-z0-9](?:[A-Za-z0-9]|[-_.:+](?=[A-Za-z0-9])){0,200})__?(?![A-Za-z0-9_])")]
    private static partial Regex SimpleReference();

    [GeneratedRegex(@"\G(?:https?|ftp)://[^\s<>`]{1,2000}|\Gmailto:[^\s<>`]{1,2000}", RegexOptions.IgnoreCase)]
    private static partial Regex BareUrl();
}
