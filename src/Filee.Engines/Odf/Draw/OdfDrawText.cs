// Text of ODF shapes, text boxes and table cells as the slide model's text frames: paragraphs and spans with the ODT
// reader's property bags and model mapping (OdtReader.TextProps / ParaProps / CharFormat / ParaFormat / Merge).
//
// - Formatting: the graphic default style, the shape's graphic and presentation styles (style:text-properties and
//   style:paragraph-properties in them), for outline placeholders the master's outline style of the level
//   ("<master>-outline2" …), the shape's text style (draw:text-style-name), then the paragraph's style and the spans'
//   text styles.
// - Lists (text:list, nested for levels): the list's own list style, else the outer list's, else the one in the shape's
//   graphic properties. Bullets and numbers become the paragraph's first text in the level's own size and colour
//   (fo:font-size as a share, fo:color), then a tab to the hanging indent (or a space without one); numbers count per
//   level and frame (text:start-value, text:continue-numbering). Empty paragraphs show no bullet. The indent is the
//   paragraph's styles' when it isn't zero, else the list level's (space-before + min-label-width, or label alignment).
// - Text frame: insets (fo:padding-*), vertical anchoring (draw:textarea-vertical-align), horizontal centring
//   (draw:textarea-horizontal-align), wrapping (fo:wrap-option, draw:auto-grow-width), vertical writing (tb-rl),
//   columns (style:columns).
// - Fields: the slide number (text:page-number) and count, the page's footer, header and fixed date
//   (presentation:footer / header / date-time); other fields show the text they hold.
// - Tables (table:table in a frame): columns and rows with their widths and heights, repeats capped, spans and covered
//   cells, cell fills and borders from the cell styles, cell text as above.

using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Odt;
using Filee.Engines.Slides;
using SkiaSharp;
using OdfNames = Filee.Engines.Hwp.Hwpx.Odt.Odf;

namespace Filee.Engines.Odf.Draw;

internal sealed partial class OdfDrawReader
{
    /// <summary>Spaces one text:s may stand for (text:c); more is a damaged or hostile file.</summary>
    private const int MaxSpaces = 10_000;

    /// <summary>How often one table row or cell may repeat.</summary>
    private const int MaxRepeat = 1000;

    /// <summary>List levels ODF numbers (text:level 1 … 10).</summary>
    private const int Levels = 10;

    private long _characters;

    /// <summary>A frame's text formatting before the paragraph's own style, at one list level.</summary>
    private sealed record TextBase(OdtReader.TextProps Text, OdtReader.ParaProps Para);

    /// <summary>What the paragraphs of one text frame share while they are read.</summary>
    private sealed class FrameState
    {
        public required List<XElement> Chain { get; init; }
        public required PageContext Context { get; init; }
        public required OdfScope Scope { get; init; }
        public bool Outline { get; init; }

        /// <summary>The shape's paragraph style for its text (draw:text-style-name).</summary>
        public string? TextStyle { get; init; }

        public XElement? ListStyle { get; init; }
        public TextBase?[] Bases { get; } = new TextBase?[Levels];
        public int[] Counters { get; } = new int[Levels];
        public bool[] Started { get; } = new bool[Levels];
        public bool InLink { get; set; }
    }

    private static bool IsBlock(XElement element) =>
        element.Name == Text + "p" || element.Name == Text + "h" || element.Name == Text + "list" || element.Name == Text + "numbered-paragraph";

    /// <summary>The text frame of a shape or text box; null when it holds no paragraphs.</summary>
    /// <param name="area">The geometry's text area in the box; null = the whole box.</param>
    /// <param name="rotation">Degrees the text turns clockwise in its frame.</param>
    private SlideTextFrame? TextFrame(XElement container, List<XElement> chain, PageContext context, OdfScope scope, SKRect? area, float rotation)
    {
        if (!container.Elements().Any(IsBlock))
            return null;
        var props = OdfGraphicStyles.Props(chain, "graphic-properties");
        var owner = container.Name == Draw + "text-box" ? container.Parent : container;
        var frame = NewFrame(props);
        frame.Area = area;
        frame.Rotation = rotation;
        var state = new FrameState
        {
            Chain = chain,
            Context = context,
            Scope = scope,
            Outline = Attr(owner, Presentation + "class") == "outline",
            TextStyle = Attr(owner, Draw + "text-style-name"),
            ListStyle = props.Child(Text + "list-style"),
        };
        Blocks(container.Elements(), state, frame.Paragraphs, 0, null);
        return frame.IsEmpty ? null : frame;
    }

    /// <summary>A frame with the text area settings of graphic properties.</summary>
    private static SlideTextFrame NewFrame(OdfProps props)
    {
        float Inset(string side, float fallback) =>
            Math.Clamp(Points(props[Fo + "padding-" + side] ?? props[Fo + "padding"]) ?? fallback, 0, 10_000);
        var writingMode = props[Style + "writing-mode"];
        var columns = props.Child(Style + "columns");
        return new SlideTextFrame
        {
            // LibreOffice's text distances: 0.25 cm across, 0.125 cm up and down.
            InsetLeft = Inset("left", 7.087f),
            InsetRight = Inset("right", 7.087f),
            InsetTop = Inset("top", 3.543f),
            InsetBottom = Inset("bottom", 3.543f),
            Anchor = props[Draw + "textarea-vertical-align"] switch
            {
                "middle" => TextAnchor.Middle,
                "bottom" => TextAnchor.Bottom,
                _ => TextAnchor.Top,
            },
            AnchorCenter = props[Draw + "textarea-horizontal-align"] == "center",
            Wrap = props[Fo + "wrap-option"] != "no-wrap" && props[Draw + "auto-grow-width"] != "true",
            Direction = writingMode is "tb-rl" or "tb" ? TextDirection.Down : TextDirection.Horizontal,
            Columns = int.TryParse(Attr(columns, Fo + "column-count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? Math.Clamp(count, 1, 16) : 1,
            ColumnGap = Math.Clamp(Points(Attr(columns, Fo + "column-gap")) ?? 0, 0, 10_000),
        };
    }

    // ───────────────────────── Blocks and lists ─────────────────────────

    private void Blocks(IEnumerable<XElement> elements, FrameState state, List<HParagraph> output, int level, XElement? listStyle)
    {
        foreach (var element in elements)
        {
            if (element.Name == Text + "p" || element.Name == Text + "h")
            {
                output.Add(Paragraph(element, state, level, null, null));
            }
            else if (element.Name == Text + "list")
            {
                using var nesting = _budget.Enter();
                List(element, state, output, level, listStyle);
            }
            else if (element.Name == Text + "numbered-paragraph")
            {
                var paragraphLevel = Math.Clamp((int.TryParse(Attr(element, Text + "level"), out var l) ? l : 1) - 1, 0, Levels - 1);
                var style = _styles.ListStyle(Attr(element, Text + "style-name"), state.Scope) ?? listStyle ?? state.ListStyle;
                var first = true;
                foreach (var paragraph in element.Elements().Where(e => e.Name == Text + "p" || e.Name == Text + "h"))
                {
                    var definition = Definition(style, paragraphLevel);
                    output.Add(Paragraph(paragraph, state, paragraphLevel, first ? definition ?? new XElement(Text + "list-level-style-bullet") : null,
                        LevelIndent(definition)));
                    first = false;
                }
            }
        }
    }

    /// <summary>A list's items; <paramref name="level"/> is its nesting (0 = top).</summary>
    private void List(XElement list, FrameState state, List<HParagraph> output, int level, XElement? outerStyle)
    {
        var style = _styles.ListStyle(Attr(list, Text + "style-name"), state.Scope) ?? outerStyle ?? state.ListStyle;
        if (level == 0 && Attr(list, Text + "continue-numbering") != "true")
            Array.Clear(state.Started);
        var modelLevel = Math.Min(level, Levels - 1);
        foreach (var item in list.Elements())
        {
            var numbered = item.Name == Text + "list-item";
            if (!numbered && item.Name != Text + "list-header")
                continue;
            var definition = Definition(style, modelLevel);
            var indent = LevelIndent(definition);
            int? start = int.TryParse(Attr(item, Text + "start-value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? Math.Clamp(s, 0, 1_000_000) : null;
            var first = numbered;
            foreach (var child in item.Elements())
            {
                if (child.Name == Text + "p" || child.Name == Text + "h")
                {
                    output.Add(Paragraph(child, state, modelLevel, first ? definition ?? new XElement(Text + "list-level-style-bullet") : null, indent,
                        first ? start : null));
                    first = false;
                }
                else if (child.Name == Text + "list")
                {
                    using var nesting = _budget.Enter();
                    List(child, state, output, level + 1, style);
                    first = false;
                }
            }
        }
    }

    /// <summary>The definition of a list level (text:list-level-style-* with text:level = level + 1).</summary>
    private static XElement? Definition(XElement? style, int level)
    {
        var wanted = (level + 1).ToString(CultureInfo.InvariantCulture);
        return style?.Elements().FirstOrDefault(e => Attr(e, Text + "level") == wanted);
    }

    /// <summary>The indent a list level gives its paragraphs (HWPUNIT): left margin and first line (negative: hanging).</summary>
    private static (int Left, int FirstLine)? LevelIndent(XElement? definition)
    {
        var props = definition?.Element(Style + "list-level-properties");
        if (props is null)
            return null;
        if (Attr(props, Text + "list-level-position-and-space-mode") == "label-alignment")
        {
            var alignment = props.Element(Style + "list-level-label-alignment");
            return (OdfNames.Length(Attr(alignment, Fo + "margin-left")) ?? 0, OdfNames.Length(Attr(alignment, Fo + "text-indent")) ?? 0);
        }
        var before = OdfNames.Length(Attr(props, Text + "space-before")) ?? 0;
        var width = OdfNames.Length(Attr(props, Text + "min-label-width")) ?? 0;
        return (Math.Clamp(before + width, -10_000_000, 10_000_000), Math.Clamp(-width, -10_000_000, 10_000_000));
    }

    // ───────────────────────── Paragraphs ─────────────────────────

    /// <summary>The frame's formatting at a list level: its styles, and the master's outline style of that level.</summary>
    private TextBase Base(FrameState state, int level)
    {
        if (state.Bases[level] is { } cached)
            return cached;
        var text = new OdtReader.TextProps();
        var para = new OdtReader.ParaProps();
        var styles = new List<XElement>(state.Chain);
        if (state.Outline && level > 0 && state.Context.MasterName is { } master)
        {
            var own = new HashSet<XElement>(state.Chain, ReferenceEqualityComparer.Instance);
            styles.AddRange(_styles.Chain("presentation", $"{master}-outline{level + 1}", OdfScope.Styles, false).Where(s => !own.Contains(s)));
        }
        styles.AddRange(_styles.Chain("paragraph", state.TextStyle, state.Scope, withDefault: false));
        foreach (var style in styles)
        {
            para = para.Apply(style.Element(Style + "paragraph-properties"));
            text = text.Apply(style.Element(Style + "text-properties"), _styles.FontFaces);
        }
        return state.Bases[level] = new TextBase(text, para);
    }

    /// <param name="label">The list level definition whose bullet or number starts the paragraph; null for none.</param>
    /// <param name="start">The number the item starts at (text:start-value).</param>
    private HParagraph Paragraph(XElement element, FrameState state, int level, XElement? label, (int Left, int FirstLine)? listIndent, int? start = null)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        _budget.Paragraph();
        var basis = Base(state, level);
        var para = basis.Para;
        var text = basis.Text;
        foreach (var style in _styles.Chain("paragraph", Attr(element, Text + "style-name"), state.Scope, withDefault: false))
        {
            para = para.Apply(style.Element(Style + "paragraph-properties"));
            text = text.Apply(style.Element(Style + "text-properties"), _styles.FontFaces);
        }
        // An indent of the paragraph's styles wins over the list level's; zeros (as Impress writes them) leave it to the list.
        var ownIndent = (para.MarginLeft ?? 0) != 0 || (para.TextIndent ?? 0) != 0;
        var format = OdtReader.ParaFormat(new OdtReader.ParagraphStyle(para, text, null, null, null, null, ownIndent), listIndent);
        var paragraph = new HParagraph { Format = format, MarkFormat = OdtReader.CharFormat(text) };
        Inlines(element, text, paragraph.Inlines, new TextState(), state);
        OdtReader.Merge(paragraph.Inlines);

        if (label is not null)
        {
            var hasText = paragraph.Inlines.Any(i => i is HText t && t.Text.Trim().Length > 0 || i is HLink or HTab);
            if (Label(label, text, state, level, start, hasText) is var (mark, markFormat))
            {
                // A hanging bullet is followed by a tab to the text's indent; one without a hanging indent by a space.
                paragraph.Inlines.InsertRange(0, [new HText(mark, markFormat), (format.FirstLine ?? 0) < 0 ? new HTab(markFormat) : new HText(" ", markFormat)]);
            }
        }
        return paragraph;
    }

    /// <summary>The bullet or number of a list level and its format; null when the level shows none (or the paragraph is empty).</summary>
    private (string Text, HCharFormat Format)? Label(XElement definition, OdtReader.TextProps text, FrameState state, int level, int? start, bool hasText)
    {
        for (var deeper = level + 1; deeper < Levels; deeper++)
            state.Started[deeper] = false;
        if (!hasText)
            return null;
        var labelText = text.Apply(definition.Element(Style + "text-properties"), _styles.FontFaces);
        if (OdfNames.Percent(Attr(definition, Text + "bullet-relative-size")) is { } relative)
            labelText = labelText with { Size = Math.Clamp((text.Size ?? 12) * relative / 100, 1, 1638) };
        // The bullet's font (OpenSymbol, StarSymbol) is often not installed: the text's font, with fallback for the character.
        labelText = labelText with { Font = text.Font, EastAsianFont = text.EastAsianFont };
        string mark;
        if (definition.Name == Text + "list-level-style-number")
        {
            var first = int.TryParse(Attr(definition, Text + "start-value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? Math.Clamp(s, 0, 1_000_000) : 1;
            state.Counters[level] = start ?? (state.Started[level] ? state.Counters[level] + 1 : first);
            state.Started[level] = true;
            var format = Attr(definition, Style + "num-format");
            if (format is "")
                return null;
            mark = Attr(definition, Style + "num-prefix") + Number(state.Counters[level], format) + Attr(definition, Style + "num-suffix");
        }
        else if (definition.Name == Text + "list-level-style-bullet")
        {
            mark = Attr(definition, Text + "bullet-char") is { Length: > 0 } bullet ? bullet : "•";
        }
        else
        {
            return null; // pictures as bullets
        }
        return mark.Length == 0 ? null : (mark, OdtReader.CharFormat(labelText));
    }

    /// <summary>A list number in an ODF number format ("1", "a", "A", "i", "I").</summary>
    private static string Number(int value, string? format)
    {
        switch (format)
        {
            case "a" or "A":
                {
                    var builder = new StringBuilder();
                    for (var n = Math.Max(1, value); n > 0; n = (n - 1) / 26)
                        builder.Insert(0, (char)((format == "a" ? 'a' : 'A') + (n - 1) % 26));
                    return builder.ToString();
                }
            case "i" or "I" when value is > 0 and < 4000:
                {
                    var builder = new StringBuilder();
                    var n = value;
                    foreach (var (amount, numeral) in new[] { (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") })
                    {
                        for (; n >= amount; n -= amount)
                            builder.Append(numeral);
                    }
                    return format == "i" ? builder.ToString().ToLowerInvariant() : builder.ToString();
                }
            default:
                return value.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>White space as ODF collapses it: runs of spaces, tabs and line feeds become one space, none at the start.</summary>
    private sealed class TextState
    {
        public bool Space { get; set; } = true;
    }

    private void Inlines(XElement container, OdtReader.TextProps text, List<HInline> output, TextState textState, FrameState state)
    {
        if (text.Hidden)
            return;
        foreach (var node in container.Nodes())
        {
            if (node is XText xmlText)
            {
                AddText(output, xmlText.Value, text, textState);
                continue;
            }
            if (node is not XElement element)
                continue;
            var name = element.Name;
            if (name.Namespace == Presentation)
            {
                var field = name.LocalName switch
                {
                    "footer" => state.Context.Footer,
                    "header" => state.Context.Header,
                    "date-time" => state.Context.DateTime,
                    _ => null,
                };
                if (field is { Length: > 0 })
                    AddText(output, field, text, textState);
                continue;
            }
            if (name.Namespace == Office || name.Namespace == Draw)
                continue; // annotations, shapes in text
            if (name.Namespace != Text)
            {
                Inlines(element, text, output, textState, state);
                continue;
            }
            switch (name.LocalName)
            {
                case "s":
                    {
                        var spaces = Math.Clamp(int.TryParse(Attr(element, Text + "c"), out var c) ? c : 1, 1, MaxSpaces);
                        Characters(spaces);
                        output.Add(new HText(new string(' ', spaces), OdtReader.CharFormat(text)));
                        textState.Space = false;
                        break;
                    }
                case "tab":
                    output.Add(new HTab(OdtReader.CharFormat(text)));
                    textState.Space = false;
                    break;
                case "line-break":
                    output.Add(new HLineBreak(OdtReader.CharFormat(text)));
                    textState.Space = true;
                    break;
                case "span":
                    Inlines(element, SpanProps(text, Attr(element, Text + "style-name"), state.Scope), output, textState, state);
                    break;
                case "a":
                    {
                        var inner = SpanProps(text, Attr(element, Text + "style-name"), state.Scope);
                        // Links don't nest: a link inside one is text of the outer link.
                        if (!state.InLink && Attr(element, XLink + "href") is { Length: > 0 } href)
                        {
                            var link = new HLink(href);
                            state.InLink = true;
                            try
                            {
                                Inlines(element, inner, link.Content, textState, state);
                            }
                            finally
                            {
                                state.InLink = false;
                            }
                            output.Add(link);
                        }
                        else
                        {
                            Inlines(element, inner, output, textState, state);
                        }
                        break;
                    }
                case "page-number":
                    AddText(output, state.Context.Number.ToString(CultureInfo.InvariantCulture), text, textState);
                    break;
                case "page-count":
                    AddText(output, state.Context.Count.ToString(CultureInfo.InvariantCulture), text, textState);
                    break;
                case "note" or "bookmark" or "bookmark-start" or "bookmark-end" or "soft-page-break" or "ruby-text" or "change" or "change-start"
                    or "change-end" or "reference-mark-start" or "reference-mark-end":
                    break;
                default:
                    // Fields (dates, titles, ...) and text:meta: the text they show.
                    Inlines(element, text, output, textState, state);
                    break;
            }
        }
    }

    private OdtReader.TextProps SpanProps(OdtReader.TextProps outer, string? styleName, OdfScope scope)
    {
        if (styleName is null)
            return outer;
        var text = outer;
        foreach (var style in _styles.Chain("text", styleName, scope, withDefault: false))
            text = text.Apply(style.Element(Style + "text-properties"), _styles.FontFaces);
        return text;
    }

    private void AddText(List<HInline> output, string value, OdtReader.TextProps text, TextState state)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is ' ' or '\t' or '\r' or '\n')
            {
                if (!state.Space)
                    builder.Append(' ');
                state.Space = true;
            }
            else
            {
                builder.Append(ch);
                state.Space = false;
            }
        }
        if (builder.Length == 0)
            return;
        Characters(builder.Length);
        output.Add(new HText(builder.ToString(), OdtReader.CharFormat(text)));
    }

    /// <summary>Counts characters of text against <see cref="DocumentLimits.MaxPartBytes"/> (text:s and master pages repeat text).</summary>
    private void Characters(int count)
    {
        _characters += count;
        if (_characters > _budget.Limits.MaxPartBytes)
            throw DocumentLimits.Exceeded($"The document's text is longer than {DocumentLimits.Count(_budget.Limits.MaxPartBytes)} characters.");
    }

    // ───────────────────────── Tables ─────────────────────────

    private SlideTable? OdfTable(XElement table, Placement place, List<XElement> chain, PageContext context, OdfScope scope)
    {
        using var nesting = _budget.Enter();
        var result = new SlideTable { Bounds = place.Bounds, Rotation = place.Rotation, FlipHorizontal = place.FlipHorizontal, FlipVertical = place.FlipVertical };
        var maxColumns = Math.Min(_budget.Limits.MaxTableColumns, MaxRepeat);
        static int Repeat(XElement element, string attribute) =>
            int.TryParse(Attr(element, Table + attribute), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, 1, MaxRepeat) : 1;
        // Columns and rows, also those in header, row and column groups (one level).
        static IEnumerable<XElement> Grouped(XElement parent, string name, string[] groups)
        {
            foreach (var element in parent.Elements())
            {
                if (element.Name == Table + name)
                    yield return element;
                else if (element.Name.Namespace == Table && groups.Contains(element.Name.LocalName))
                {
                    foreach (var inner in element.Elements(Table + name))
                        yield return inner;
                }
            }
        }

        var widths = new List<float?>();
        foreach (var column in Grouped(table, "table-column", ["table-columns", "table-header-columns", "table-column-group"]))
        {
            var props = OdfGraphicStyles.Props(_styles.Chain("table-column", Attr(column, Table + "style-name"), scope, false), "table-column-properties");
            var width = Points(props[Style + "column-width"]);
            for (var i = Repeat(column, "number-columns-repeated"); i > 0 && widths.Count < maxColumns; i--)
                widths.Add(width);
        }

        foreach (var row in Grouped(table, "table-row", ["table-rows", "table-header-rows", "table-row-group"]))
        {
            var rowProps = OdfGraphicStyles.Props(_styles.Chain("table-row", Attr(row, Table + "style-name"), scope, false), "table-row-properties");
            var height = Points(rowProps[Style + "row-height"] ?? rowProps[Style + "min-row-height"]) ?? 0;
            for (var copy = Repeat(row, "number-rows-repeated"); copy > 0; copy--)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var slideRow = new SlideTableRow { Height = Math.Clamp(height, 0, MaxLength) };
                foreach (var cell in row.Elements())
                {
                    var covered = cell.Name == Table + "covered-table-cell";
                    if (!covered && cell.Name != Table + "table-cell")
                        continue;
                    for (var i = Repeat(cell, "number-columns-repeated"); i > 0 && slideRow.Cells.Count < maxColumns; i--)
                    {
                        _budget.Paragraph();
                        slideRow.Cells.Add(covered ? new SlideTableCell { Covered = true } : Cell(cell, chain, context, scope));
                    }
                }
                result.Rows.Add(slideRow);
            }
        }
        var count = Math.Max(widths.Count, result.Rows.Count == 0 ? 0 : result.Rows.Max(r => r.Cells.Count));
        if (count == 0 || result.Rows.Count == 0)
            return null;
        while (widths.Count < count)
            widths.Add(null);
        // Columns without a width share what the others leave; all are scaled to the frame's width.
        var known = widths.Where(w => w is > 0).Sum(w => w!.Value);
        var unknown = widths.Count(w => w is not > 0);
        var share = unknown > 0 ? Math.Max(place.Bounds.Width - known, 0) / unknown : 0;
        var columns = widths.Select(w => w is > 0 ? w.Value : share).ToArray();
        var total = columns.Sum();
        var scale = total > 0 && place.Bounds.Width > 0 ? place.Bounds.Width / total : 1;
        result.Columns = [.. columns.Select(w => w * scale)];
        var rowsHeight = result.Rows.Sum(r => r.Height);
        if (rowsHeight <= 0)
        {
            foreach (var row in result.Rows)
                row.Height = place.Bounds.Height / result.Rows.Count;
        }
        return result;
    }

    private SlideTableCell Cell(XElement cell, List<XElement> chain, PageContext context, OdfScope scope)
    {
        var styles = _styles.Chain("table-cell", Attr(cell, Table + "style-name"), scope, false);
        var graphic = OdfGraphicStyles.Props(styles, "graphic-properties");
        var cellProps = OdfGraphicStyles.Props(styles, "table-cell-properties");
        var paragraphProps = OdfGraphicStyles.Props(styles, "paragraph-properties");
        SlideLine? Border(string side)
        {
            var value = cellProps[Fo + "border-" + side] ?? cellProps[Fo + "border"] ?? paragraphProps[Fo + "border-" + side] ?? paragraphProps[Fo + "border"];
            return BorderLine(value);
        }
        int Span(string attribute) => int.TryParse(Attr(cell, Table + attribute), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, 1, MaxRepeat) : 1;
        var result = new SlideTableCell
        {
            ColumnSpan = Span("number-columns-spanned"),
            RowSpan = Span("number-rows-spanned"),
            Fill = _styles.Fill(graphic, solid: false) ?? _styles.Fill(cellProps, solid: false),
            Left = Border("left"),
            Right = Border("right"),
            Top = Border("top"),
            Bottom = Border("bottom"),
        };
        var textChain = new List<XElement>(chain);
        textChain.AddRange(styles);
        var frame = NewFrame(OdfGraphicStyles.Props(textChain, "graphic-properties").With(cellProps.Elements));
        var state = new FrameState { Chain = textChain, Context = context, Scope = scope, ListStyle = graphic.Child(Text + "list-style") };
        Blocks(cell.Elements(), state, frame.Paragraphs, 0, null);
        frame.Anchor = cellProps[Style + "vertical-align"] switch
        {
            "middle" => TextAnchor.Middle,
            "bottom" => TextAnchor.Bottom,
            _ => frame.Anchor,
        };
        result.Text = frame;
        return result;
    }

    /// <summary>A border as fo:border writes it ("0.5pt solid #000000"); null for none.</summary>
    private static SlideLine? BorderLine(string? value)
    {
        if (value is null || value.Contains("none", StringComparison.Ordinal) || value.Contains("hidden", StringComparison.Ordinal))
            return null;
        float width = 0.75f;
        SKColor color = SKColors.Black;
        foreach (var part in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (OdfGraphicStyles.Color(part) is { } c)
                color = c;
            else if (Points(part) is { } w)
                width = Math.Clamp(w, 0, 100);
        }
        return new SlideLine(new SolidFill(color), width);
    }
}
