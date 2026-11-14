// The Markdown of a canvas card laid out as Obsidian shows it, simply: headings, paragraphs (a single line break is kept,
// as Obsidian keeps it), bold, italic, strikethrough, ==highlights==, inline code, links and [[internal links]] in the
// link colour, bullet, numbered and task lists, quotes, code blocks, tables and rules. Pictures and embeds inside the
// text are shown by name; nothing is fetched. Text is measured with the typefaces CadFonts finds (a CJK font for the
// letters the sans-serif one lacks) and wrapped at spaces, or anywhere in CJK text and in words wider than the card.
// Layout stops at the bottom of the card, which clips the rest as Obsidian does before scrolling.

using System.Text;
using System.Text.RegularExpressions;
using Filee.Engines.Cad;
using Filee.Engines.Hwp.Hwpx;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using SkiaSharp;

namespace Filee.Engines.Vector.Canvas;

/// <summary>Lays out Markdown into a box of a <see cref="CanvasSvg"/>.</summary>
internal sealed partial class CanvasText
{
    /// <summary>Obsidian's heading sizes, in em of the body text.</summary>
    private static readonly double[] HeadingSizes = [1.802, 1.602, 1.424, 1.266, 1.125, 1.0];

    /// <summary>Monospace families for code, the first installed one wins (Windows, macOS, Linux).</summary>
    private static readonly string[] MonospaceFamilies =
        ["Consolas", "Menlo", "JetBrains Mono", "DejaVu Sans Mono", "Liberation Mono", "Courier New"];

    private static readonly Lazy<SKTypeface> Monospace = new(() =>
        MonospaceFamilies.Select(f => SKFontManager.Default.MatchFamily(f)).FirstOrDefault(t => t is { GlyphCount: > 0 })
        ?? CadFonts.Resolve(null, false, false));

    /// <summary>Deepest nesting of lists and quotes laid out; deeper blocks are left out.</summary>
    private const int MaxDepth = 16;

    private const double LineHeight = 1.5;

    private readonly CanvasSvg _svg;
    private readonly double _size;
    private readonly double _bottom;
    private readonly string _color;
    private double _y;

    /// <param name="svg">Where the text is drawn.</param>
    /// <param name="top">Top of the box.</param>
    /// <param name="bottom">Bottom of the box: nothing is laid out below it.</param>
    /// <param name="size">Body text size in canvas pixels.</param>
    /// <param name="color">Body text colour.</param>
    public CanvasText(CanvasSvg svg, double top, double bottom, double size = 16, string color = CanvasColors.Text)
    {
        _svg = svg;
        _y = top;
        _bottom = bottom;
        _size = size;
        _color = color;
    }

    /// <summary>The y below the last line laid out.</summary>
    public double Y => _y;

    private bool Full => _y >= _bottom;

    /// <summary>Lays out <paramref name="markdown"/> from the current y, between <paramref name="left"/> and <paramref name="left"/> + <paramref name="width"/>.</summary>
    public void Markdown(string markdown, double left, double width)
    {
        var document = Markdig.Markdown.Parse(WikiLinksAsLinks(markdown), MarkdownReader.Pipeline);
        Blocks(document, left, width, 0);
    }

    /// <summary>Lays out plain text (no Markdown), wrapped, in one style.</summary>
    public void Plain(string text, double left, double width, bool bold = false, double? size = null, string? color = null)
    {
        var style = new Style(Bold: bold, Size: size ?? _size, Color: color ?? _color);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (var line in lines)
        {
            if (Full)
                return;
            var builder = new LineBuilder(this, left, width, style.Size);
            builder.Add(line, style);
            builder.Flush(force: true);
        }
    }

    // ───────────────────────── Blocks ─────────────────────────

    private void Blocks(ContainerBlock container, double left, double width, int depth)
    {
        var first = true;
        foreach (var block in container)
        {
            if (Full)
                return;
            if (block is YamlFrontMatterBlock or LinkReferenceDefinitionGroup)
                continue;
            if (!first)
                _y += _size * 0.5;
            first = false;
            Block(block, left, width, depth);
        }
    }

    private void Block(Block block, double left, double width, int depth)
    {
        switch (block)
        {
            case HeadingBlock heading:
                {
                    var size = _size * HeadingSizes[Math.Clamp(heading.Level, 1, 6) - 1];
                    Inlines(heading.Inline, left, width, new Style(Bold: true, Size: size, Color: _color));
                    break;
                }
            case ParagraphBlock paragraph:
                Inlines(paragraph.Inline, left, width, new Style(Size: _size, Color: _color));
                break;
            case ListBlock list when depth < MaxDepth:
                List(list, left, width, depth);
                break;
            case QuoteBlock quote when depth < MaxDepth:
                {
                    var top = _y;
                    Blocks(quote, left + _size, width - _size, depth + 1);
                    _svg.Rect(left + 1, top, 2, Math.Min(_y, _bottom) - top, 0, CanvasColors.Accent);
                    break;
                }
            case CodeBlock code:
                Code(code, left, width);
                break;
            case ThematicBreakBlock:
                _y += _size * 0.5;
                _svg.Line(left, _y, left + width, _y, CanvasColors.Border, 1);
                _y += _size * 0.5;
                break;
            case Table table:
                TableBlock(table, left, width);
                break;
            case HtmlBlock html:
                Plain(html.Lines.ToString(), left, width, color: CanvasColors.Muted);
                break;
            case ContainerBlock other when depth < MaxDepth:
                Blocks(other, left, width, depth + 1);
                break;
            case LeafBlock leaf when leaf.Inline is not null:
                Inlines(leaf.Inline, left, width, new Style(Size: _size, Color: _color));
                break;
            case LeafBlock leaf:
                Plain(leaf.Lines.ToString(), left, width);
                break;
        }
    }

    private void List(ListBlock list, double left, double width, int depth)
    {
        var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
        var indent = _size * 1.5;
        var first = true;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            if (Full)
                return;
            if (!first)
                _y += list.IsLoose ? _size * 0.5 : 0;
            first = false;
            var top = _y;
            var task = item.FirstOrDefault() is ParagraphBlock { Inline.FirstChild: TaskList checkbox } ? checkbox : null;
            var lineHeight = _size * LineHeight;
            var middle = top + lineHeight / 2;
            if (task is not null)
            {
                var box = _size * 0.8;
                _svg.Rect(left + indent - box - _size * 0.35, middle - box / 2, box, box, 3,
                    task.Checked ? CanvasColors.Accent : CanvasColors.Background, task.Checked ? null : CanvasColors.Faint, 1.2);
                if (task.Checked)
                {
                    var x = left + indent - box - _size * 0.35;
                    var y = middle - box / 2;
                    _svg.Path($"M{CanvasSvg.N(x + box * 0.22)} {CanvasSvg.N(y + box * 0.52)} L{CanvasSvg.N(x + box * 0.42)} {CanvasSvg.N(y + box * 0.72)} L{CanvasSvg.N(x + box * 0.78)} {CanvasSvg.N(y + box * 0.3)}",
                        null, CanvasColors.Background, 1.6);
                }
            }
            else if (list.IsOrdered)
            {
                var label = $"{number}.";
                var face = CadFonts.Resolve(null, false, false);
                var runs = CanvasFonts.Layout(label, face);
                var labelWidth = runs.Sum(r => r.Advance) * _size;
                _svg.Text(left + indent - labelWidth - _size * 0.35, Baseline(top, _size, lineHeight), label, face, _size, false, false, CanvasColors.Muted);
            }
            else
            {
                var radius = _size * 0.16;
                _svg.Raw($"""<circle cx="{CanvasSvg.N(left + indent - _size * 0.65)}" cy="{CanvasSvg.N(middle)}" r="{CanvasSvg.N(radius)}" fill="{CanvasColors.Muted}"/>""");
            }
            number++;
            Blocks(item, left + indent, width - indent, depth + 1);
            if (_y < top + lineHeight)
                _y = top + lineHeight;
        }
    }

    private void Code(CodeBlock code, double left, double width)
    {
        var size = _size * 0.875;
        var padding = _size * 0.5;
        var top = _y;
        _y += padding;
        var style = new Style(Code: true, Size: size, Color: _color);
        var textTop = _y;
        // The text is laid out first, its shading drawn under it afterwards: the box's height isn't known before.
        var lines = code.Lines.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        var body = new CanvasSvg();
        var inner = new CanvasText(body, textTop, _bottom, _size, _color);
        foreach (var line in lines)
        {
            if (inner.Full)
                break;
            var builder = new LineBuilder(inner, left + padding, width - 2 * padding, size);
            builder.Add(line, style);
            builder.Flush(force: true);
        }
        _y = inner._y + padding;
        _svg.Rect(left, top, width, Math.Min(_y, _bottom + padding) - top, 4, CanvasColors.CodeBackground);
        _svg.Raw(body.ToString());
    }

    private void TableBlock(Table table, double left, double width)
    {
        var rows = table.OfType<TableRow>().ToList();
        var columns = Math.Max(1, rows.Max(r => r.Count));
        var columnWidth = width / columns;
        var padding = _size * 0.4;
        foreach (var row in rows)
        {
            if (Full)
                return;
            var top = _y;
            var bottom = top;
            var index = 0;
            foreach (var cell in row.OfType<TableCell>())
            {
                _y = top + padding;
                var x = left + index * columnWidth;
                foreach (var block in cell)
                {
                    if (block is ParagraphBlock paragraph)
                        Inlines(paragraph.Inline, x + padding, columnWidth - 2 * padding,
                            new Style(Bold: row.IsHeader, Size: _size, Color: _color));
                }
                bottom = Math.Max(bottom, _y + padding);
                index++;
            }
            _y = bottom;
            _svg.Line(left, _y, left + width, _y, CanvasColors.Border, 1);
        }
    }

    // ───────────────────────── Inlines ─────────────────────────

    /// <summary>The look of a run of text.</summary>
    private readonly record struct Style(
        bool Bold = false, bool Italic = false, bool Code = false, bool Link = false, bool Strike = false, bool Mark = false,
        double Size = 16, string Color = CanvasColors.Text);

    private void Inlines(ContainerInline? inlines, double left, double width, Style style)
    {
        var builder = new LineBuilder(this, left, width, style.Size);
        if (inlines is not null)
            Inline(inlines, builder, style, 0);
        builder.Flush(force: true);
    }

    private void Inline(ContainerInline container, LineBuilder builder, Style style, int depth)
    {
        for (var inline = container.FirstChild; inline is not null && !Full; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    Literal(literal.Content.ToString(), builder, style);
                    break;
                case CodeInline code:
                    builder.Add(code.Content, style with { Code = true, Size = style.Size * 0.875 });
                    break;
                case LineBreakInline:
                    builder.Flush(force: true);
                    break;
                case HtmlEntityInline entity:
                    builder.Add(entity.Transcoded.ToString(), style);
                    break;
                case AutolinkInline auto:
                    builder.Add(auto.Url, style with { Link = true });
                    break;
                case TaskList:
                    break; // drawn as the list item's box
                case LinkInline { IsImage: true } image:
                    builder.Add(PictureName(image), style with { Italic = true, Color = CanvasColors.Muted });
                    break;
                case LinkInline { Url: { } url } embed when url.StartsWith(EmbedScheme, StringComparison.Ordinal) && depth < MaxDepth:
                    Inline(embed, builder, style with { Italic = true, Color = CanvasColors.Muted }, depth + 1);
                    break;
                case LinkInline link when depth < MaxDepth:
                    Inline(link, builder, style with { Link = true }, depth + 1);
                    break;
                case EmphasisInline emphasis when depth < MaxDepth:
                    Inline(emphasis, builder, Emphasis(emphasis, style), depth + 1);
                    break;
                case HtmlInline:
                    break; // tags are left out, the text between them stays
                case ContainerInline other when depth < MaxDepth:
                    Inline(other, builder, style, depth + 1);
                    break;
            }
        }
    }

    private static Style Emphasis(EmphasisInline emphasis, Style style) => (emphasis.DelimiterChar, emphasis.DelimiterCount) switch
    {
        ('*' or '_', 1) => style with { Italic = true },
        ('*' or '_', 2) => style with { Bold = true },
        ('*' or '_', _) => style with { Bold = true, Italic = true },
        ('~', 2) => style with { Strike = true },
        ('=', 2) => style with { Mark = true },
        _ => style,
    };

    private static string PictureName(LinkInline image)
    {
        var alt = new StringBuilder();
        for (var child = image.FirstChild; child is not null; child = child.NextSibling)
            if (child is LiteralInline literal)
                alt.Append(literal.Content.ToString());
        return alt.Length > 0 ? alt.ToString() : Path.GetFileName(image.Url ?? "");
    }

    private const string EmbedScheme = "filee-embed:";

    /// <summary>
    /// The Markdown with Obsidian's internal links ([[Note]], [[Note|shown text]], ![[picture.png]]) written as ordinary
    /// links before it is parsed: Markdig splits the text at brackets, so they can't be found in its literals. Code
    /// blocks are left alone.
    /// </summary>
    private static string WikiLinksAsLinks(string markdown)
    {
        if (!markdown.Contains("[[", StringComparison.Ordinal))
            return markdown;
        var lines = markdown.Split('\n');
        var fence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
                fence = !fence;
            else if (!fence)
                lines[i] = WikiLink().Replace(lines[i], match =>
                {
                    var target = match.Groups["target"].Value;
                    var shown = match.Groups["alias"].Success ? match.Groups["alias"].Value
                        : target.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? target[..^3] : target;
                    shown = MarkdownSpecial().Replace(shown.Replace("#", " > ", StringComparison.Ordinal), @"\$0");
                    return $"[{shown}](<{(match.Groups["embed"].Success ? EmbedScheme : "filee-link:")}>)";
                });
        }
        return string.Join('\n', lines);
    }

    [GeneratedRegex(@"[\\`*_\[\]<>!]")]
    private static partial Regex MarkdownSpecial();

    /// <summary>Text with Obsidian's internal links ([[Note]], [[Note|shown text]], ![[picture.png]]) in the link colour.</summary>
    private static void Literal(string text, LineBuilder builder, Style style)
    {
        var start = 0;
        foreach (Match match in WikiLink().Matches(text))
        {
            builder.Add(text[start..match.Index], style);
            var target = match.Groups["target"].Value;
            var shown = match.Groups["alias"].Success ? match.Groups["alias"].Value
                : target.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? target[..^3] : target;
            builder.Add(shown.Replace("#", " > ", StringComparison.Ordinal), match.Groups["embed"].Success
                ? style with { Italic = true, Color = CanvasColors.Muted }
                : style with { Link = true });
            start = match.Index + match.Length;
        }
        builder.Add(text[start..], style);
    }

    [GeneratedRegex(@"(?<embed>!)?\[\[(?<target>[^\[\]|]+)(?:\|(?<alias>[^\[\]]+))?\]\]")]
    private static partial Regex WikiLink();

    // ───────────────────────── Lines ─────────────────────────

    private static double Baseline(double top, double size, double lineHeight) => top + (lineHeight - size) / 2 + size * 0.8;

    private static SKTypeface FaceOf(Style style) =>
        style.Code ? Monospace.Value : CadFonts.Resolve(null, style.Bold, style.Italic);

    /// <summary>A run placed on the line being built.</summary>
    private sealed record Piece(string Text, SKTypeface Face, Style Style, double X, double Width);

    /// <summary>Fills one line after another with words, breaking where the next one doesn't fit.</summary>
    private sealed class LineBuilder(CanvasText owner, double left, double width, double size)
    {
        private readonly List<Piece> _pieces = [];
        private double _x;
        private double _pendingSpace;
        private Style? _spaceStyle;
        private readonly double _bodySize = size;
        private double _maxSize = size;

        public void Add(string text, Style style)
        {
            if (owner.Full)
                return;
            foreach (var token in Tokens(text))
            {
                if (owner.Full)
                    return;
                if (char.IsWhiteSpace(token[0]))
                {
                    if (_pieces.Count > 0)
                    {
                        _pendingSpace = Measure(" ", style).Width;
                        _spaceStyle = style;
                    }
                    continue;
                }
                Word(token, style);
            }
        }

        private void Word(string word, Style style)
        {
            var (runs, wordWidth) = Measure(word, style);
            if (_pieces.Count > 0 && _x + _pendingSpace + wordWidth > width)
                Flush(force: false);
            if (wordWidth > width && word.Length > 1)
            {
                // Wider than the card (a long URL): break it letter by letter, down to the card's bottom only, so a
                // pasted blob of a few MB isn't laid out to its end as clipped, invisible text.
                var part = new StringBuilder();
                foreach (var letter in Letters(word))
                {
                    if (owner.Full)
                        return;
                    var (_, partWidth) = Measure(part + letter, style);
                    if (part.Length > 0 && _x + partWidth > width)
                    {
                        Place(part.ToString(), style);
                        Flush(force: false);
                        part.Clear();
                    }
                    part.Append(letter);
                }
                if (part.Length > 0)
                    Place(part.ToString(), style);
                return;
            }
            if (_pieces.Count > 0 && _pendingSpace > 0)
            {
                var spaceStyle = _spaceStyle ?? style;
                _pieces.Add(new Piece(" ", FaceOf(spaceStyle), spaceStyle, _x, _pendingSpace));
                _x += _pendingSpace;
            }
            _pendingSpace = 0;
            foreach (var run in runs)
            {
                _pieces.Add(new Piece(run.Text, run.Typeface, style, _x, run.Advance * style.Size));
                _x += run.Advance * style.Size;
            }
            _maxSize = Math.Max(_maxSize, style.Size);
        }

        private void Place(string text, Style style)
        {
            foreach (var run in Measure(text, style).Runs)
            {
                _pieces.Add(new Piece(run.Text, run.Typeface, style, _x, run.Advance * style.Size));
                _x += run.Advance * style.Size;
            }
            _pendingSpace = 0;
            _maxSize = Math.Max(_maxSize, style.Size);
        }

        /// <summary>Draws the line and starts the next; <paramref name="force"/> also ends an empty line (a blank line in code, a hard break).</summary>
        public void Flush(bool force)
        {
            if (_pieces.Count == 0 && !force)
                return;
            if (owner.Full)
            {
                // Below the card's bottom: nothing would show.
                _pieces.Clear();
                _x = 0;
                _pendingSpace = 0;
                return;
            }
            var lineHeight = _maxSize * LineHeight;
            var top = owner._y;
            var baseline = Baseline(top, _maxSize, lineHeight);
            foreach (var group in Merge(_pieces))
            {
                var style = group.Style;
                if (style.Mark)
                    owner._svg.Rect(left + group.X, top + (lineHeight - style.Size * 1.3) / 2, group.Width, style.Size * 1.3, 2, CanvasColors.Highlight);
                else if (style.Code)
                    owner._svg.Rect(left + group.X - 2, top + (lineHeight - style.Size * 1.35) / 2, group.Width + 4, style.Size * 1.35, 3, CanvasColors.CodeBackground);
            }
            foreach (var group in Merge(_pieces))
            {
                var style = group.Style;
                var color = style.Link ? CanvasColors.Accent : style.Color;
                // Underlines and strike-through are drawn as lines: viewers differ on text-decoration in SVG.
                owner._svg.Text(left + group.X, baseline, group.Text, group.Face, style.Size, style.Bold && !style.Code, style.Italic, color);
                if (style.Strike)
                    owner._svg.Line(left + group.X, baseline - style.Size * 0.3, left + group.X + group.Width, baseline - style.Size * 0.3, color, Math.Max(1, style.Size / 16));
                else if (style.Link)
                    owner._svg.Line(left + group.X, baseline + style.Size * 0.12, left + group.X + group.Width, baseline + style.Size * 0.12, color, Math.Max(0.75, style.Size / 20));
            }
            owner._y += lineHeight;
            _pieces.Clear();
            _x = 0;
            _pendingSpace = 0;
            _maxSize = _bodySize;
        }

        /// <summary>Neighbouring pieces in the same typeface and style, as one text element.</summary>
        private static IEnumerable<Piece> Merge(List<Piece> pieces)
        {
            Piece? current = null;
            var text = new StringBuilder();
            foreach (var piece in pieces)
            {
                if (current is not null && ReferenceEquals(current.Face, piece.Face) && current.Style == piece.Style)
                {
                    text.Append(piece.Text);
                    current = current with { Width = piece.X + piece.Width - current.X };
                    continue;
                }
                if (current is not null)
                    yield return current with { Text = text.ToString() };
                current = piece;
                text.Clear().Append(piece.Text);
            }
            if (current is not null)
                yield return current with { Text = text.ToString() };
        }

        private static (List<GlyphRun> Runs, double Width) Measure(string text, Style style)
        {
            var runs = CanvasFonts.Layout(text, FaceOf(style));
            return (runs, runs.Sum(r => r.Advance) * style.Size);
        }

        /// <summary>Words, runs of spaces, and single CJK letters (which a line may break between).</summary>
        private static IEnumerable<string> Tokens(string text)
        {
            var start = 0;
            var i = 0;
            while (i < text.Length)
            {
                var width = char.IsSurrogatePair(text, i) ? 2 : 1;
                var codePoint = width == 2 ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];
                if (char.IsWhiteSpace(text[i]))
                {
                    if (i > start)
                        yield return text[start..i];
                    var end = i;
                    while (end < text.Length && char.IsWhiteSpace(text[end]))
                        end++;
                    yield return text[i..end];
                    start = i = end;
                    continue;
                }
                if (BreaksAnywhere(codePoint))
                {
                    if (i > start)
                        yield return text[start..i];
                    yield return text.Substring(i, width);
                    start = i + width;
                }
                i += width;
            }
            if (start < text.Length)
                yield return text[start..];
        }

        private static IEnumerable<string> Letters(string word)
        {
            for (var i = 0; i < word.Length;)
            {
                var width = char.IsSurrogatePair(word, i) ? 2 : 1;
                yield return word.Substring(i, width);
                i += width;
            }
        }

        /// <summary>CJK letters, kana and Hangul, between which a line may break.</summary>
        private static bool BreaksAnywhere(int c) =>
            c is >= 0x2E80 and <= 0x9FFF or >= 0xAC00 and <= 0xD7AF or >= 0xF900 and <= 0xFAFF or >= 0xFF00 and <= 0xFFEF
                or >= 0x20000 and <= 0x3FFFF;
    }
}
