// Document body of an ODT: sections (a master page each, set on their first paragraph or table), column changes as
// text:section, paragraphs with their automatic styles, lists, text with ODF's white space rules, notes, fields and
// tables.
//
// Lists: consecutive paragraphs of one list definition become one text:list, nested by level; a paragraph without a
// number continues the item before it (or is the list's header); a list that goes on after other content continues
// the first one (text:continue-list), so its numbers go on too.

using System.Text;

namespace Filee.Engines.Hwp.Hwpx.Odt;

internal sealed partial class OdtWriter
{
    /// <summary>What is being written: notes are only allowed in the body (and its tables).</summary>
    private enum Context
    {
        Body,
        Note,
        TextBox,
        Header,
    }

    /// <summary>Width (HWPUNIT) available to the content being written: column, table cell or text box.</summary>
    private int _width = DefaultPage.TextWidth;

    /// <summary>The master page (and page number restart) the next block of the body starts.</summary>
    private (string Name, int? PageNumber)? _pendingMaster;

    /// <summary>True while blocks of the body itself are written (not in a table, note, frame or header).</summary>
    private bool _topLevel;

    private int _tables;
    private int _frames;
    private int _sections;
    private int _footnotes;
    private int _endnotes;

    // ───────────────────────── Sections ─────────────────────────

    private string Body(HDocument document)
    {
        var sections = document.Sections.Count > 0 ? document.Sections : [new HSection()];
        var sb = new StringBuilder();
        for (var s = 0; s < sections.Count; s++)
        {
            var section = sections[s];
            var page = section.Page ?? DefaultPage;
            _pendingMaster = (MasterPage(section, s), section.StartPageNumber);
            foreach (var (columns, blocks) in SplitAtColumnChanges(section))
            {
                _width = ColumnWidth(page.TextWidth, columns);
                var wrap = columns != section.Columns && columns.Count > 1;
                if (wrap)
                {
                    var style = _styles.Add("section", "Sect", "", $"<style:section-properties text:dont-balance-text-columns=\"false\">{Columns(columns)}</style:section-properties>");
                    sb.Append($"<text:section text:style-name=\"{style}\" text:name=\"Section{Number(++_sections)}\">");
                }
                _topLevel = true;
                sb.Append(Blocks(blocks, "Standard", Context.Body));
                _topLevel = false;
                if (wrap)
                    sb.Append("</text:section>");
            }
            // A section with nothing in it still starts its page.
            if (_pendingMaster is not null)
            {
                _topLevel = true;
                sb.Append(Paragraph(new HParagraph(), "Standard", Context.Body));
                _topLevel = false;
            }
        }
        return sb.ToString();
    }

    /// <summary>Splits a section where its columns change: the pieces with other columns than the page's become text:section.</summary>
    private static List<(HColumns Columns, List<HBlock> Blocks)> SplitAtColumnChanges(HSection section)
    {
        var parts = new List<(HColumns, List<HBlock>)>();
        var columns = section.Columns;
        var current = new List<HBlock>();
        foreach (var block in section.Blocks)
        {
            if (block is HParagraph { ColumnsChange: { } change })
            {
                if (current.Count > 0)
                {
                    parts.Add((columns, current));
                    current = [];
                }
                columns = change;
            }
            current.Add(block);
        }
        parts.Add((columns, current));
        return parts;
    }

    private static int ColumnWidth(int textWidth, HColumns columns) =>
        columns.Count <= 1 ? textWidth : Math.Max(2000, (textWidth - columns.Gap * (columns.Count - 1)) / columns.Count);

    /// <summary>Runs <paramref name="write"/> with <paramref name="width"/> available and outside the body's top level.</summary>
    private T Nested<T>(int width, Func<T> write)
    {
        var (outerWidth, outerTop) = (_width, _topLevel);
        _width = width;
        _topLevel = false;
        try
        {
            return write();
        }
        finally
        {
            (_width, _topLevel) = (outerWidth, outerTop);
        }
    }

    // ───────────────────────── Blocks ─────────────────────────

    /// <param name="paragraphStyle">The named style plain paragraphs are based on (Standard, Footnote, Header ...).</param>
    private string Blocks(IReadOnlyList<HBlock> blocks, string paragraphStyle, Context context)
    {
        var sb = new StringBuilder();
        var lists = new ListWriter(this, sb);
        foreach (var block in blocks)
        {
            switch (block)
            {
                case HParagraph { List: { } } item:
                    lists.Add(item, Paragraph(item, paragraphStyle, context));
                    break;
                case HParagraph paragraph:
                    lists.Close();
                    sb.Append(Paragraph(paragraph, paragraphStyle, context));
                    break;
                case HTable table:
                    lists.Close();
                    sb.Append(Table(table, context));
                    break;
            }
        }
        lists.Close();
        return sb.ToString();
    }

    /// <summary>The master page for the block about to be written, when it is the first of a section.</summary>
    private (string? Master, int? PageNumber) TakeMaster()
    {
        if (!_topLevel || _pendingMaster is not { } master)
            return (null, null);
        _pendingMaster = null;
        return master;
    }

    // ───────────────────────── Lists ─────────────────────────

    /// <summary>Writes paragraphs of lists into nested text:list elements.</summary>
    private sealed class ListWriter(OdtWriter writer, StringBuilder sb)
    {
        private enum Open
        {
            None,
            Item,
            Header,
        }

        private readonly List<Open> _levels = [];
        private HNumbering? _numbering;

        public void Add(HParagraph paragraph, string xml)
        {
            var list = paragraph.List!;
            var level = Math.Clamp(list.Level, 0, 9);
            if (!ReferenceEquals(_numbering, list.Numbering))
                Close();
            if (_levels.Count == 0)
            {
                _numbering = list.Numbering;
                var style = writer.ListStyle(list.Numbering, out var first);
                var id = style.ToLowerInvariant();
                sb.Append(first
                    ? $"<text:list xml:id=\"{id}\" text:style-name=\"{style}\">"
                    : $"<text:list text:continue-list=\"{id}\" text:style-name=\"{style}\">");
                _levels.Add(Open.None);
            }
            while (_levels.Count - 1 > level)
                CloseLevel();
            while (_levels.Count - 1 < level)
            {
                // A deeper level goes into an item of the level above (one without a paragraph has no number).
                if (_levels[^1] == Open.None)
                {
                    sb.Append("<text:list-item>");
                    _levels[^1] = Open.Item;
                }
                sb.Append("<text:list>");
                _levels.Add(Open.None);
            }

            if (list.Numbered)
            {
                CloseItem(level);
                sb.Append("<text:list-item>");
                _levels[level] = Open.Item;
            }
            else if (_levels[level] == Open.None)
            {
                // A paragraph without a number before the list's first item: the list's header.
                sb.Append("<text:list-header>");
                _levels[level] = Open.Header;
            }
            sb.Append(xml);
        }

        public void Close()
        {
            while (_levels.Count > 0)
                CloseLevel();
            _numbering = null;
        }

        private void CloseLevel()
        {
            CloseItem(_levels.Count - 1);
            sb.Append("</text:list>");
            _levels.RemoveAt(_levels.Count - 1);
        }

        private void CloseItem(int level)
        {
            sb.Append(_levels[level] switch
            {
                Open.Item => "</text:list-item>",
                Open.Header => "</text:list-header>",
                _ => "",
            });
            _levels[level] = Open.None;
        }
    }

    // ───────────────────────── Paragraphs ─────────────────────────

    private string Paragraph(HParagraph paragraph, string paragraphStyle, Context context)
    {
        var heading = paragraph.HeadingLevel is >= 1 and <= 9 ? paragraph.HeadingLevel : 0;
        var parent = heading > 0 ? $"Heading_20_{heading}" : paragraphStyle;
        var (master, pageNumber) = TakeMaster();
        var breakBefore = paragraph.PageBreakBefore ? "page" : paragraph.ColumnBreakBefore ? "column" : null;
        var properties = ParagraphProperties(paragraph.Format, breakBefore, pageNumber) + TextProperties(paragraph.MarkFormat);
        var attributes = $" style:parent-style-name=\"{parent}\"" + (master is null ? "" : $" style:master-page-name=\"{master}\"");
        var style = _styles.Add("paragraph", "P", attributes, properties);

        var sb = new StringBuilder();
        sb.Append(heading > 0 ? $"<text:h text:style-name=\"{style}\" text:outline-level=\"{Number(heading)}\">" : $"<text:p text:style-name=\"{style}\">");
        Inlines(paragraph.Inlines, sb, new TextState(paragraph.MarkFormat), context);
        return sb.Append(heading > 0 ? "</text:h>" : "</text:p>").ToString();
    }

    /// <summary>style:paragraph-properties of the members that are set (null ones are left to the named style).</summary>
    private static string ParagraphProperties(HParaFormat f, string? breakBefore, int? pageNumber)
    {
        var a = new StringBuilder();
        var rtl = f.RightToLeft == true;
        if (f.Align is { } align)
        {
            a.Append(align switch
            {
                HAlign.Center => " fo:text-align=\"center\"",
                HAlign.Right => " fo:text-align=\"end\"",
                HAlign.Justify => " fo:text-align=\"justify\" fo:text-align-last=\"start\"",
                HAlign.Distribute => " fo:text-align=\"justify\" fo:text-align-last=\"justify\"",
                _ => " fo:text-align=\"start\"",
            });
        }
        // Right to left, the model's Left is the start side: the right one.
        if ((rtl ? f.Right : f.Left) is { } left)
            a.Append($" fo:margin-left=\"{Odf.Points(left)}\"");
        if ((rtl ? f.Left : f.Right) is { } right)
            a.Append($" fo:margin-right=\"{Odf.Points(right)}\"");
        if (f.FirstLine is { } firstLine)
            a.Append($" fo:text-indent=\"{Odf.Points(firstLine)}\"");
        if (f.Before is { } before)
            a.Append($" fo:margin-top=\"{Odf.Points(Math.Max(0, before))}\"");
        if (f.After is { } after)
            a.Append($" fo:margin-bottom=\"{Odf.Points(Math.Max(0, after))}\"");
        if (f.LineSpacing is { } line)
        {
            a.Append(line.Kind switch
            {
                HLineSpacingKind.Fixed => $" fo:line-height=\"{Odf.Points(Math.Max(1, line.Value))}\"",
                HLineSpacingKind.AtLeast => $" style:line-height-at-least=\"{Odf.Points(Math.Max(0, line.Value))}\"",
                _ => $" fo:line-height=\"{Number(Math.Clamp((int)Math.Round(line.Value / OdtReader.LineToHwpPercent), 1, 1000))}%\"",
            });
        }
        if (f.KeepWithNext is { } keepNext)
            a.Append($" fo:keep-with-next=\"{(keepNext ? "always" : "auto")}\"");
        if (f.KeepLines is { } keepLines)
            a.Append($" fo:keep-together=\"{(keepLines ? "always" : "auto")}\"");
        if (f.WidowOrphan is { } widows)
            a.Append(widows ? " fo:widows=\"2\" fo:orphans=\"2\"" : " fo:widows=\"0\" fo:orphans=\"0\"");
        if (f.RightToLeft is { } direction)
            a.Append($" style:writing-mode=\"{(direction ? "rl-tb" : "lr-tb")}\"");
        if (breakBefore is not null)
            a.Append($" fo:break-before=\"{breakBefore}\"");
        if (pageNumber is { } number)
            a.Append($" style:page-number=\"{Number(Math.Max(0, number))}\"");

        var tabs = f.Tabs is null ? "" : "<style:tab-stops>" + string.Concat(f.Tabs.OrderBy(t => t.Position).Select(TabStop)) + "</style:tab-stops>";
        return a.Length == 0 && tabs.Length == 0 ? "" : $"<style:paragraph-properties{a}>{tabs}</style:paragraph-properties>";
    }

    private static string TabStop(HTabStop tab)
    {
        var type = tab.Kind switch
        {
            HTabKind.Right => " style:type=\"right\"",
            HTabKind.Center => " style:type=\"center\"",
            HTabKind.Decimal => " style:type=\"char\" style:char=\".\"",
            _ => "",
        };
        var leader = tab.Leader switch
        {
            "DOT" => " style:leader-style=\"dotted\" style:leader-text=\".\"",
            "DASH" => " style:leader-style=\"dash\" style:leader-text=\"-\"",
            "SOLID" => " style:leader-style=\"solid\" style:leader-text=\"_\"",
            _ => "",
        };
        // ODF measures tab stops from the paragraph's indent (LibreOffice's default), as 한글 does.
        return $"<style:tab-stop style:position=\"{Odf.Points(Math.Max(0, tab.Position))}\"{type}{leader}/>";
    }

    /// <summary>style:text-properties of the members that are set.</summary>
    private static string TextProperties(HCharFormat f)
    {
        var a = new StringBuilder();
        if (f.Font is { } font)
            a.Append($" fo:font-family=\"{Family(font)}\" style:font-family-complex=\"{Family(font)}\"");
        if (f.EastAsianFont is { } eastAsian)
            a.Append($" style:font-family-asian=\"{Family(eastAsian)}\"");
        if (f.Size is { } size)
        {
            var points = Odf.FontSize(Math.Clamp(size, 100, 163_800));
            a.Append($" fo:font-size=\"{points}\" style:font-size-asian=\"{points}\" style:font-size-complex=\"{points}\"");
        }
        if (f.Bold is { } bold)
        {
            var weight = bold ? "bold" : "normal";
            a.Append($" fo:font-weight=\"{weight}\" style:font-weight-asian=\"{weight}\" style:font-weight-complex=\"{weight}\"");
        }
        if (f.Italic is { } italic)
        {
            var style = italic ? "italic" : "normal";
            a.Append($" fo:font-style=\"{style}\" style:font-style-asian=\"{style}\" style:font-style-complex=\"{style}\"");
        }
        if (f.Underline is { } underline)
            a.Append(underline ? " style:text-underline-style=\"solid\" style:text-underline-width=\"auto\" style:text-underline-color=\"font-color\"" : " style:text-underline-style=\"none\"");
        if (f.Strike is { } strike)
            a.Append($" style:text-line-through-style=\"{(strike ? "solid" : "none")}\"");
        if (f.Superscript == true)
            a.Append(" style:text-position=\"super 58%\"");
        else if (f.Subscript == true)
            a.Append(" style:text-position=\"sub 58%\"");
        else if (f.Superscript == false || f.Subscript == false)
            a.Append(" style:text-position=\"0% 100%\"");
        if (Hex(f.Color) is { } color)
            a.Append($" fo:color=\"{color}\"");
        if (f.Shade is not null)
            a.Append($" fo:background-color=\"{Hex(f.Shade) ?? "transparent"}\"");
        if (f.Spacing is { } spacing)
        {
            // 한글's letter spacing is a percentage of the font size, ODF's a length.
            var letters = (int)Math.Round(spacing * (f.Size ?? 1000) / 100.0);
            a.Append($" fo:letter-spacing=\"{(letters == 0 ? "normal" : Odf.Points(letters))}\"");
        }
        return a.Length == 0 ? "" : $"<style:text-properties{a}/>";
    }

    /// <summary>A font family as fo:font-family takes it: quoted when it is more than letters and digits.</summary>
    private static string Family(string font)
    {
        var name = font.Replace("'", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal);
        return Escape(name.All(char.IsAsciiLetterOrDigit) ? name : $"'{name}'");
    }

    // ───────────────────────── Text ─────────────────────────

    /// <summary>
    /// White space within a paragraph: ODF collapses runs of spaces into one and drops spaces at the start, so a space
    /// after another (or first in the paragraph) is written as text:s. <see cref="Mark"/> is what text without a span
    /// is formatted as.
    /// </summary>
    private sealed class TextState(HCharFormat mark)
    {
        public bool Space { get; set; } = true;
        public HCharFormat Mark { get; } = mark;
    }

    private void Inlines(IEnumerable<HInline> inlines, StringBuilder sb, TextState state, Context context)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case HText text:
                    Span(sb, text.Format, state, () => Text(sb, text.Text, state));
                    break;
                case HLineBreak lineBreak:
                    Span(sb, lineBreak.Format, state, () => sb.Append("<text:line-break/>"));
                    state.Space = false;
                    break;
                case HTab tab:
                    Span(sb, tab.Format, state, () => sb.Append("<text:tab/>"));
                    state.Space = false;
                    break;
                case HLink link:
                    sb.Append($"<text:a xlink:type=\"simple\" xlink:href=\"{Escape(link.Target)}\">");
                    Inlines(link.Content, sb, state, context);
                    sb.Append("</text:a>");
                    break;
                case HBookmark bookmark:
                    sb.Append($"<text:bookmark text:name=\"{Escape(BookmarkName(bookmark.Name))}\"/>");
                    break;
                case HField field:
                    Span(sb, field.Format, state, () => sb.Append(field.Kind == HFieldKind.PageNumber
                        ? "<text:page-number text:select-page=\"current\">1</text:page-number>"
                        : "<text:page-count>1</text:page-count>"));
                    state.Space = false;
                    break;
                case HNote note:
                    Note(sb, note, state, context);
                    state.Space = false;
                    break;
                case HImage image:
                    sb.Append(Picture(image));
                    state.Space = false;
                    break;
                case HTextBox box:
                    sb.Append(TextBox(box));
                    state.Space = false;
                    break;
                case HShape shape:
                    sb.Append(Shape(shape));
                    state.Space = false;
                    break;
            }
        }
    }

    /// <summary>Writes <paramref name="content"/> in a text:span when <paramref name="format"/> is not the paragraph's own.</summary>
    private void Span(StringBuilder sb, HCharFormat format, TextState state, Action content)
    {
        var properties = format.Equals(state.Mark) ? "" : TextProperties(format);
        if (properties.Length == 0)
        {
            content();
            return;
        }
        sb.Append($"<text:span text:style-name=\"{_styles.Add("text", "T", "", properties)}\">");
        content();
        sb.Append("</text:span>");
    }

    /// <summary>Text with tabs and line feeds as their elements and spaces ODF would collapse as text:s.</summary>
    private static void Text(StringBuilder sb, string text, TextState state)
    {
        var segment = new StringBuilder();
        var spaces = 0;
        void FlushSegment()
        {
            if (segment.Length > 0)
                sb.Append(Escape(segment.ToString()));
            segment.Clear();
        }
        void FlushSpaces()
        {
            if (spaces == 0)
                return;
            FlushSegment();
            sb.Append(spaces == 1 ? "<text:s/>" : $"<text:s text:c=\"{Number(spaces)}\"/>");
            spaces = 0;
        }
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == ' ')
            {
                // The first space after other text stays a space; the ones ODF would collapse are text:s.
                if (state.Space)
                {
                    spaces++;
                }
                else
                {
                    segment.Append(' ');
                    state.Space = true;
                }
                continue;
            }
            FlushSpaces();
            state.Space = false;
            if (ch is '\t' or '\n' or '\r')
            {
                FlushSegment();
                if (ch == '\t')
                    sb.Append("<text:tab/>");
                else if (ch == '\n' || i + 1 >= text.Length || text[i + 1] != '\n')
                    sb.Append("<text:line-break/>");
                continue;
            }
            segment.Append(ch);
        }
        FlushSpaces();
        FlushSegment();
    }

    /// <summary>A bookmark name not used before (ODF bookmark names are unique).</summary>
    private string BookmarkName(string name)
    {
        var unique = name;
        for (var i = 2; !_bookmarks.Add(unique); i++)
            unique = $"{name}_{i}";
        return unique;
    }

    /// <summary>
    /// A footnote or endnote. ODF has notes only in the body text and its tables (not in notes, headers, footers or
    /// frames): there the note's text stays in place, in brackets.
    /// </summary>
    private void Note(StringBuilder sb, HNote note, TextState state, Context context)
    {
        if (context != Context.Body)
        {
            var text = string.Concat(note.Blocks.OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HText>().Select(t => t.Text));
            Span(sb, note.Format ?? state.Mark, state, () => Text(sb, $" ({text})", state));
            return;
        }
        var (kind, style, number) = note.Endnote ? ("endnote", "Endnote", ++_endnotes) : ("footnote", "Footnote", ++_footnotes);
        var prefix = note.Endnote ? "edn" : "ftn";
        var body = Nested(_width, () => Blocks(note.Blocks.Count > 0 ? note.Blocks : [new HParagraph()], style, Context.Note));
        Span(sb, note.Format ?? state.Mark, state, () =>
            sb.Append($"<text:note text:id=\"{prefix}{Number(number)}\" text:note-class=\"{kind}\"><text:note-citation>{Number(number)}</text:note-citation>")
              .Append($"<text:note-body>{body}</text:note-body></text:note>"));
    }

    // ───────────────────────── Tables ─────────────────────────

    /// <summary>Cell padding where neither the cell nor the table gives one: Word's (and the DOCX writer's) 0.19 cm left and right.</summary>
    private static readonly HInsets DefaultCellMargin = new(540, 540, 0, 0);

    private string Table(HTable table, Context context)
    {
        if (table.Rows.Count == 0)
            return Blocks(table.Caption, "Standard", context);
        var (master, pageNumber) = TakeMaster();
        var (placed, columnCount) = HwpxWriter.PlaceCells(table);
        var available = Math.Max(4000, _width - (table.Align == HAlign.Left ? table.Indent : 0));
        var widths = table.ColumnWidths is { } explicitWidths && explicitWidths.Length == columnCount && explicitWidths.All(w => w > 0)
            ? explicitWidths
            : HwpxWriter.ColumnWidths(table, columnCount, available).Select(w => Math.Max(1, w)).ToArray();

        var tableProps = new StringBuilder($"<style:table-properties style:width=\"{Odf.Points(widths.Sum())}\"")
            .Append(table.Align switch
            {
                HAlign.Center => " table:align=\"center\"",
                HAlign.Right => " table:align=\"right\"",
                _ => $" table:align=\"left\" fo:margin-left=\"{Odf.Points(table.Indent)}\"",
            });
        if (table.PageBreakBefore)
            tableProps.Append(" fo:break-before=\"page\"");
        if (pageNumber is { } number)
            tableProps.Append($" style:page-number=\"{Number(Math.Max(0, number))}\"");
        tableProps.Append("/>");
        var tableStyle = _styles.Add("table", "Table", master is null ? "" : $" style:master-page-name=\"{master}\"", tableProps.ToString());

        var sb = new StringBuilder($"<table:table table:name=\"Table{Number(++_tables)}\" table:style-name=\"{tableStyle}\">");
        foreach (var width in widths)
        {
            var columnStyle = _styles.Add("table-column", "Col", "",
                $"<style:table-column-properties style:column-width=\"{Odf.Points(width)}\" style:rel-column-width=\"{Number(width)}*\"/>");
            sb.Append($"<table:table-column table:style-name=\"{columnStyle}\"/>");
        }

        // Positions inside a spanning cell (other than its first) are covered cells.
        var covered = new HashSet<(int Row, int Column)>();
        for (var r = 0; r < placed.Count; r++)
        {
            foreach (var (cell, column) in placed[r])
            {
                for (var dr = 0; dr < Math.Max(1, cell.RowSpan); dr++)
                {
                    for (var dc = 0; dc < Math.Max(1, cell.ColSpan); dc++)
                    {
                        if (dr > 0 || dc > 0)
                            covered.Add((r + dr, column + dc));
                    }
                }
            }
        }

        var headerRows = table.Rows.TakeWhile(r => r.Header).Count();
        var outerWidth = _width;
        for (var r = 0; r < table.Rows.Count; r++)
        {
            if (r == 0 && headerRows > 0)
                sb.Append("<table:table-header-rows>");
            var row = table.Rows[r];
            var rowProps = new StringBuilder();
            if (row.Height is > 0 and var height)
                rowProps.Append($" style:min-row-height=\"{Odf.Points(height)}\"");
            if (row.CantSplit)
                rowProps.Append(" fo:keep-together=\"always\"");
            sb.Append(rowProps.Length == 0
                ? "<table:table-row>"
                : $"<table:table-row table:style-name=\"{_styles.Add("table-row", "Row", "", $"<style:table-row-properties{rowProps}/>")}\">");

            var starts = r < placed.Count ? placed[r].GroupBy(p => p.Column).ToDictionary(g => g.Key, g => g.First().Cell) : [];
            for (var column = 0; column < columnCount; column++)
            {
                if (starts.TryGetValue(column, out var cell))
                {
                    var span = Math.Clamp(cell.ColSpan, 1, columnCount - column);
                    var rowSpan = Math.Clamp(cell.RowSpan, 1, table.Rows.Count - r);
                    var margin = cell.CellMargin ?? table.CellMargin ?? DefaultCellMargin;
                    var width = widths.Skip(column).Take(span).Sum();
                    var content = Nested(Math.Max(1000, width - margin.Left - margin.Right), () => Blocks(cell.Blocks, "Standard", context));
                    sb.Append($"<table:table-cell table:style-name=\"{CellStyle(cell, table, margin)}\" office:value-type=\"string\"")
                      .Append(span > 1 ? $" table:number-columns-spanned=\"{Number(span)}\"" : "")
                      .Append(rowSpan > 1 ? $" table:number-rows-spanned=\"{Number(rowSpan)}\"" : "")
                      .Append(content.Length == 0 ? "/>" : $">{content}</table:table-cell>");
                }
                else
                {
                    // Inside a spanning cell, or a gap in a ragged row (an empty cell keeps the grid rectangular).
                    sb.Append(covered.Contains((r, column)) ? "<table:covered-table-cell/>" : "<table:table-cell/>");
                }
            }
            sb.Append("</table:table-row>");
            if (r == headerRows - 1)
                sb.Append("</table:table-header-rows>");
        }
        _width = outerWidth;
        sb.Append("</table:table>");
        if (table.Caption.Count > 0)
            sb.Append(Blocks(table.Caption, "Standard", context));
        return sb.ToString();
    }

    private string CellStyle(HCell cell, HTable table, HInsets margin)
    {
        var borders = cell.Borders ?? table.Borders ?? HBorders.Grid;
        var props = new StringBuilder("<style:table-cell-properties");
        if (Hex(cell.Fill) is { } fill)
            props.Append($" fo:background-color=\"{fill}\"");
        foreach (var (side, border) in new[] { ("left", borders.Left), ("right", borders.Right), ("top", borders.Top), ("bottom", borders.Bottom) })
        {
            if (border.Style != HBorderStyle.None)
                props.Append($" fo:border-{side}=\"{Border(border)}\"");
        }
        props.Append($" fo:padding-left=\"{Odf.Points(margin.Left)}\" fo:padding-right=\"{Odf.Points(margin.Right)}\"")
             .Append($" fo:padding-top=\"{Odf.Points(margin.Top)}\" fo:padding-bottom=\"{Odf.Points(margin.Bottom)}\"")
             .Append(cell.VerticalAlign switch
             {
                 HVerticalAlign.Center => " style:vertical-align=\"middle\"",
                 HVerticalAlign.Bottom => " style:vertical-align=\"bottom\"",
                 _ => " style:vertical-align=\"top\"",
             })
             .Append("/>");
        return _styles.Add("table-cell", "Cell", "", props.ToString());
    }

    /// <summary>"0.5pt solid #000000"; a width that rounds to nothing is a hairline.</summary>
    private static string Border(HBorder border)
    {
        var style = border.Style switch
        {
            HBorderStyle.Dash => "dashed",
            HBorderStyle.Dot => "dotted",
            HBorderStyle.DashDot => "dashed",
            HBorderStyle.Double => "double",
            _ => "solid",
        };
        var width = Math.Max(1, (int)Math.Round(border.WidthMm * HwpxUnits.PerMm));
        return $"{Odf.Points(width)} {style} {Hex(border.Color) ?? "#000000"}";
    }
}
