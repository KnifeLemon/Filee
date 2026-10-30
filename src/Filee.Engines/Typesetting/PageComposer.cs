// Flows a document onto pages (GitHub Project 4, item "Layout engine: page composer"): each section's pages with its
// paper size, margins and gutter, its body filled with laid out paragraphs, and its headers and footers.
//
// - Pages: a section starts a page; the body runs from the top margin and header area down to the footer area. A
//   paragraph starts a page when it asks for one (page break before, or a column break while there is one column), and
//   a line that doesn't fit goes to the next page.
// - Keep rules: "keep lines together" moves a paragraph that would split to the next page, "keep with next" a paragraph
//   whose next one wouldn't start on the same page, and widow / orphan control keeps at least two lines of a split
//   paragraph on either page. None of them moves anything off an empty page.
// - 한글's stored layout: lines on their stored layout (ParagraphLayouter) take 한글's page breaks and their place on
//   the page from it, while the page so far follows it too; anything laid out by Filee in between (a paragraph broken
//   again, a table) ends that until 한글 starts its next page.
// - Headers and footers: the section's header and footer for both pages, odd or even pages (by page number), none on
//   the section's first page where it hides them; laid out in the header and footer areas.
// - Page numbers count on from section to section unless a section restarts them; a page number field shows the
//   number of its page and a total pages field the count, which a first pass finds when the body shows it.
// - Styles: models read from other formats leave formats to the 한글 style (null members); with a StyleResolver every
//   paragraph and run is resolved before layout, as the HWPX writer would write it.
//
// - Tables (TableLayout): rows bound by spanning cells move to a page together, header rows repeat, and a table splits
//   by its page break mode.
//
// Columns, notes, floating objects and master pages are later milestones.

using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Rendering;

namespace Filee.Engines.Typesetting;

/// <summary>Pages and the PDF outline of a composed document.</summary>
internal sealed record ComposedDocument(List<PageContent> Pages, List<OutlineEntry> Outline);

/// <summary>Flows documents onto pages; one per layout job (not thread-safe).</summary>
internal sealed class PageComposer
{
    private const float Epsilon = 0.01f;

    /// <summary>Lines widow and orphan control keeps together at a page break.</summary>
    private const int MinimumLines = 2;

    /// <summary>A4 with 한글's default margins, for sections without a page of their own.</summary>
    internal static readonly HPage DefaultPage = new(59528, 84186, 8504, 8504, 5669, 4252, 4252, 4252, 0);

    private readonly ParagraphLayouter _layouter;
    private readonly StyleResolver? _styles;

    /// <param name="styles">Resolves formats a model leaves to the 한글 style; null for models read from HWPX.</param>
    /// <param name="storedLines">Paragraphs follow 한글's stored line layout where it can be used.</param>
    public PageComposer(FontResolver fonts, Shaper shaper, IReadOnlyList<HKinsoku>? kinsoku = null, StyleResolver? styles = null, bool storedLines = true)
    {
        _layouter = new ParagraphLayouter(fonts, shaper, kinsoku, storedLines);
        _styles = styles;
    }

    /// <summary>The document's pages.</summary>
    public ComposedDocument Compose(HDocument document, CancellationToken cancellationToken = default)
    {
        // The body's total pages fields need the count first: a first pass finds it.
        int? total = null;
        if (document.Sections.Any(s => Fields(s.Blocks).Any(f => f.Kind == HFieldKind.TotalPages)))
            total = new Run(this, document, null, cancellationToken).Compose().Pages.Count;
        return new Run(this, document, total, cancellationToken).Compose();
    }

    /// <summary>The page geometry of a section, in points.</summary>
    private sealed record Geometry(float Width, float Height, float Left, float TextWidth, float HeaderTop, float BodyTop, float BodyBottom, float FooterTop)
    {
        public static Geometry Of(HPage page) => new(
            page.Width / 100f,
            page.Height / 100f,
            (page.Left + page.Gutter) / 100f,
            page.TextWidth / 100f,
            page.Top / 100f,
            (page.Top + page.Header) / 100f,
            (page.Height - page.Bottom - page.Footer) / 100f,
            (page.Height - page.Bottom - page.Footer) / 100f);
    }

    /// <summary>A page and what its headers and footers need.</summary>
    private sealed record Page(PageContent Content, HSection Section, Geometry Geometry, int Number, bool FirstOfSection);

    /// <summary>One composition of a document.</summary>
    private sealed class Run(PageComposer composer, HDocument document, int? total, CancellationToken cancellationToken)
    {
        private readonly LayoutGuard _guard = new(DocumentLimits.Default, cancellationToken);
        private readonly ListNumbers _numbers = new();
        private readonly List<Page> _pages = [];
        private readonly List<OutlineEntry> _outline = [];
        private Geometry _geometry = Geometry.Of(DefaultPage);
        private HSection? _section;
        private int _number;
        private float _y;
        private bool _empty = true;

        /// <summary>The page so far follows 한글's stored layout, so stored lines may take their stored place.</summary>
        private bool _inStep;

        /// <summary>The next paragraph's space before went before the table that starts it.</summary>
        private bool _skipSpaceBefore;

        public ComposedDocument Compose()
        {
            foreach (var section in document.Sections)
            {
                _section = section;
                _geometry = Geometry.Of(section.Page ?? DefaultPage);
                if (section.StartPageNumber is { } start)
                    _number = start - 1;
                NewPage(firstOfSection: true);
                Body(section.Blocks);
            }
            if (_pages.Count == 0)
            {
                _section = new HSection();
                NewPage(firstOfSection: true);
            }
            _guard.PageFinished(_empty);
            foreach (var page in _pages)
                HeadersAndFooters(page);
            return new ComposedDocument([.. _pages.Select(p => p.Content)], _outline);
        }

        private void NewPage(bool firstOfSection = false)
        {
            if (_pages.Count > 0)
                _guard.PageFinished(_empty);
            _number++;
            _pages.Add(new Page(new PageContent(_geometry.Width, _geometry.Height), _section!, _geometry, _number, firstOfSection));
            _y = _geometry.BodyTop;
            _empty = true;
            _inStep = true;
        }

        private Page Current => _pages[^1];

        // ───────────────────────── Body ─────────────────────────

        private TableLayout? _tables;

        /// <summary>Tables and paragraph lists inside cells, headers and footers, laid out with this run's numbering and fields.</summary>
        private TableLayout Tables => _tables ??= new TableLayout((p, width) => Layout(composer.Resolve(p), width), composer._styles is not null);

        private void Body(List<HBlock> blocks)
        {
            object? next = null;
            HParagraph? holder = null;
            for (var i = 0; i < blocks.Count; i++)
            {
                _guard.Paragraph();
                if (blocks[i] is HTable table)
                {
                    var tableBox = next as TableBox ?? Tables.Layout(table, _geometry.TextWidth);
                    next = null;
                    PlaceTable(table, tableBox, holder, holder is null ? TableLayout.Follower(blocks, i) : null);
                    holder = null;
                    continue;
                }
                if (blocks[i] is not HParagraph source)
                    continue;
                // 한글 keeps a table inside a paragraph, which the model splits around it. With no text before the
                // table (only objects floating at it), 한글's line is the table's: the part takes no line of its own,
                // and the table takes its spacing and stored place.
                if (TableLayout.HoldsTable(blocks, i))
                {
                    holder = composer.Resolve(source);
                    continue;
                }
                var paragraph = composer.Resolve(source);
                // A column break moves to the next page only where there is one column (columns are a later milestone).
                if ((paragraph.PageBreakBefore || paragraph.ColumnBreakBefore && _section!.Columns.Count <= 1) && !_empty)
                    NewPage();
                var box = next as ParagraphBox ?? Layout(paragraph, _geometry.TextWidth);
                next = null;

                // Keep with next: this paragraph and the start of the next block on one page.
                if (paragraph.Format.KeepWithNext == true && !_empty && i + 1 < blocks.Count && !(box.StoredLines && _inStep))
                {
                    float start;
                    switch (blocks[i + 1])
                    {
                        case HParagraph following:
                            var nextBox = Layout(composer.Resolve(following), _geometry.TextWidth);
                            next = nextBox;
                            start = nextBox.SpaceBefore + (nextBox.Lines.Count > 0 ? nextBox.Lines[0].Height : 0);
                            break;
                        case HTable following:
                            var nextTable = Tables.Layout(following, _geometry.TextWidth);
                            next = nextTable;
                            start = nextTable.Groups() is [var (first, count), ..] ? nextTable.Rows.Skip(first).Take(count).Sum(r => r.Height) : 0;
                            break;
                        default:
                            start = 0;
                            break;
                    }
                    var together = box.Height + start;
                    if (_y + together > _geometry.BodyBottom + Epsilon && together <= BodyHeight)
                        NewPage();
                }
                // Keep lines together: a paragraph that would split starts on the next page.
                if (paragraph.Format.KeepLines == true && !_empty && !(box.StoredLines && _inStep) &&
                    _y + box.Height > _geometry.BodyBottom + Epsilon && box.Height <= BodyHeight)
                    NewPage();

                Place(paragraph, box);
                if (!box.StoredLines)
                    _inStep = false;
            }
        }

        /// <summary>
        /// Puts a table's rows on pages from the current position on, repeating its header rows; <paramref name="holder"/>
        /// is the 한글 paragraph whose line the table is, or null.
        /// </summary>
        /// <param name="follower">The rest of the 한글 paragraph the table starts, whose text follows right under it.</param>
        private void PlaceTable(HTable table, TableBox box, HParagraph? holder, HParagraph? follower)
        {
            if (table.PageBreakBefore && !_empty)
                NewPage();
            var line = holder?.Lines?.Segments.FirstOrDefault();
            if (holder is not null)
            {
                // Where 한글 put the line while the page follows 한글, else after the paragraph's space before.
                if (_inStep && line is { } stored)
                    _y = Math.Max(_y, _geometry.BodyTop + stored.VerticalPosition / 100f);
                else if (!_empty)
                    _y += (holder.Format.Before ?? 0) / 100f;
            }
            else if (follower is not null)
            {
                // The table starts its 한글 paragraph, placed from the paragraph's top by its offset; 한글 puts the
                // paragraph's text right under it, without the space before.
                _skipSpaceBefore = true;
            }
            var margin = table.OuterMargin ?? default;
            _y += (table.VerticalOffset + margin.Top) / 100f;
            // Filee's rows are not 한글's: the stored places hold again on 한글's next page.
            _inStep = false;
            var x = _geometry.Left + box.Offset + margin.Left / 100f;
            var header = box.Rows.Take(box.HeaderRows).ToList();
            // A table that must not split goes to the next page whole when it fits on one.
            if (box.PageBreak == HTablePageBreak.None && !_empty && _y + box.Height > _geometry.BodyBottom + Epsilon && box.Height <= BodyHeight)
                NewPage();

            // The page holds nothing yet but, perhaps, this table's repeated header rows.
            var fresh = _empty;
            void Draw(IReadOnlyList<RowBox> rows)
            {
                TableLayout.Draw(Current.Content, rows, x, _y);
                _y += rows.Sum(r => r.Height);
                _empty = false;
            }
            void NextPage(bool repeat)
            {
                NewPage();
                _inStep = false;
                if (repeat && header.Count > 0)
                    Draw(header);
                fresh = true;
            }

            foreach (var (first, count) in box.Groups())
            {
                IReadOnlyList<RowBox> rows = [.. box.Rows.Skip(first).Take(count)];
                var repeat = first >= header.Count;
                while (true)
                {
                    var room = _geometry.BodyBottom - _y;
                    if (rows.Sum(r => r.Height) <= room + Epsilon)
                    {
                        Draw(rows);
                        fresh = false;
                        break;
                    }
                    // Rows may split inside their cells unless the table splits between rows only (한글 셀 단위로
                    // 나눔), must not split, or the row says so.
                    var splits = rows.Count == 1 && !rows[0].CantSplit && box.PageBreak is not (HTablePageBreak.Cell or HTablePageBreak.None);
                    if ((splits || fresh) && rows.Count == 1 && TableLayout.Split(rows[0], room) is var (part, rest))
                    {
                        Draw([part]);
                        NextPage(repeat);
                        rows = [rest];
                        continue;
                    }
                    if (!fresh)
                    {
                        NextPage(repeat);
                        continue;
                    }
                    // Taller than an empty page and it can't split: placed as it is.
                    Draw(rows);
                    fresh = false;
                    break;
                }
            }
            foreach (var piece in box.Caption)
            {
                if (!_empty && _y + piece.Height > _geometry.BodyBottom + Epsilon)
                    NewPage();
                piece.Draw(Current.Content, x, _y);
                _y += piece.Height;
                _empty = false;
            }
            _y += margin.Bottom / 100f;
            // The rest of the table's line: its line spacing and the paragraph's space after.
            if (holder is not null)
                _y += (line?.Spacing ?? 0) / 100f + (holder.Format.After ?? 0) / 100f;
        }

        private float BodyHeight => _geometry.BodyBottom - _geometry.BodyTop;

        private ParagraphBox Layout(HParagraph paragraph, float width)
        {
            var marker = paragraph.List is { Numbered: true } list ? _numbers.Next(list) : null;
            return composer._layouter.Layout(paragraph, width, marker, FieldText);
        }

        /// <summary>Puts a paragraph's lines on pages from the current position on.</summary>
        private void Place(HParagraph paragraph, ParagraphBox box)
        {
            var lines = box.Lines;
            var widowOrphan = paragraph.Format.WidowOrphan == true;
            if (!_empty && !_skipSpaceBefore)
                _y += box.SpaceBefore;
            _skipSpaceBefore = false;
            for (var i = 0; i < lines.Count;)
            {
                var stored = box.StoredLines && _inStep;
                if (stored && lines[i].StoredPageStart && !_empty)
                {
                    NewPage();
                }
                // How many of the remaining lines fit on this page.
                var fit = 0;
                var y = _y;
                for (var j = i; j < lines.Count; j++)
                {
                    if (j > i && stored && lines[j].StoredPageStart)
                        break;
                    var top = stored && lines[j].StoredTop is { } storedTop ? Math.Max(y, _geometry.BodyTop + storedTop) : y;
                    if (top + lines[j].Height > _geometry.BodyBottom + Epsilon)
                        break;
                    y = top + lines[j].Height;
                    fit++;
                }
                if (widowOrphan && !stored && fit < lines.Count - i)
                {
                    // At least two lines of the paragraph on this page (or none) and two on the next.
                    if (i == 0 && fit < MinimumLines && !_empty)
                        fit = 0;
                    else if (lines.Count - i - fit < MinimumLines && fit > MinimumLines - 1 + (i == 0 ? 1 : 0))
                        fit -= MinimumLines - (lines.Count - i - fit);
                }
                if (fit == 0)
                {
                    if (!_empty)
                    {
                        NewPage();
                        _inStep = false;
                        continue;
                    }
                    fit = 1; // a line higher than the page: placed anyway
                }
                for (var j = i; j < i + fit; j++)
                    PlaceLine(paragraph, lines[j], j == 0, stored);
                i += fit;
                if (i < lines.Count)
                {
                    NewPage();
                    // A break Filee made, not 한글: the stored places no longer hold until 한글's next page.
                    if (!lines[i].StoredPageStart)
                        _inStep = false;
                }
            }
            _y += box.SpaceAfter;
        }

        private void PlaceLine(HParagraph paragraph, LineBox line, bool first, bool stored)
        {
            if (stored && line.StoredTop is { } top)
                _y = Math.Max(_y, _geometry.BodyTop + top);
            if (first && paragraph.HeadingLevel > 0)
                _outline.Add(new OutlineEntry(Title(paragraph), _pages.Count - 1, _y, paragraph.HeadingLevel - 1));
            line.Draw(Current.Content, _geometry.Left, _y);
            _y += line.Height;
            _empty = false;
        }

        // ───────────────────────── Headers and footers ─────────────────────────

        private void HeadersAndFooters(Page page)
        {
            _geometry = page.Geometry;
            var section = page.Section;
            // Fields in headers and footers show the number of the page they are on.
            _number = page.Number;
            if (!(page.FirstOfSection && section.HideFirstHeader) && Pick(section.Headers, page.Number) is { } header)
                Area(page, header.Blocks, page.Geometry.HeaderTop);
            if (!(page.FirstOfSection && section.HideFirstFooter) && Pick(section.Footers, page.Number) is { } footer)
                Area(page, footer.Blocks, page.Geometry.FooterTop);
        }

        /// <summary>The header or footer for a page: the one for odd or even pages, else the one for both.</summary>
        private static HHeaderFooter? Pick(List<HHeaderFooter> list, int number)
        {
            var side = number % 2 == 0 ? HPageType.Even : HPageType.Odd;
            return list.LastOrDefault(h => h.Pages == side) ?? list.LastOrDefault(h => h.Pages == HPageType.Both);
        }

        /// <summary>Lays out a header's or footer's blocks from <paramref name="top"/> down.</summary>
        private void Area(Page page, List<HBlock> blocks, float top)
        {
            foreach (var piece in Tables.Stack(blocks, page.Geometry.TextWidth))
                piece.Draw(page.Content, page.Geometry.Left, top + piece.Top);
        }

        // ───────────────────────── Fields ─────────────────────────

        private string FieldText(HField field) => field.Kind switch
        {
            HFieldKind.TotalPages => (total ?? _pages.Count).ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => _number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    /// <summary>The fields of a list of blocks, tables and links included.</summary>
    private static IEnumerable<HField> Fields(IEnumerable<HBlock> blocks)
    {
        static IEnumerable<HField> InInlines(IEnumerable<HInline> inlines) =>
            inlines.SelectMany(i => i switch
            {
                HField field => [field],
                HLink link => InInlines(link.Content),
                _ => Enumerable.Empty<HField>(),
            });

        foreach (var block in blocks)
        {
            var found = block switch
            {
                HParagraph paragraph => InInlines(paragraph.Inlines),
                HTable table => Fields(table.Rows.SelectMany(r => r.Cells).SelectMany(c => c.Blocks)),
                _ => [],
            };
            foreach (var field in found)
                yield return field;
        }
    }

    // ───────────────────────── Styles ─────────────────────────

    /// <summary>The paragraph with every format member set (as it is when there is no resolver).</summary>
    private HParagraph Resolve(HParagraph paragraph)
    {
        if (_styles is null)
            return paragraph;
        var resolved = new HParagraph
        {
            Format = _styles.Paragraph(paragraph),
            MarkFormat = _styles.Mark(paragraph),
            HeadingLevel = paragraph.HeadingLevel,
            List = paragraph.List,
            PageBreakBefore = paragraph.PageBreakBefore,
            ColumnBreakBefore = paragraph.ColumnBreakBefore,
            ColumnsChange = paragraph.ColumnsChange,
            Lines = paragraph.Lines,
        };
        resolved.Inlines.AddRange(Inlines(paragraph.Inlines, paragraph, link: false));
        return resolved;
    }

    private IEnumerable<HInline> Inlines(IEnumerable<HInline> inlines, HParagraph paragraph, bool link)
    {
        HCharFormat Format(HCharFormat format) => _styles!.Character(format, paragraph, HStyleContext.Body, link);
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case HEquation equation:
                    yield return new HEquation(equation.Math, Format(equation.Format));
                    break;
                case HText text:
                    yield return new HText(text.Text, Format(text.Format));
                    break;
                case HTab tab:
                    yield return new HTab(Format(tab.Format));
                    break;
                case HLineBreak lineBreak:
                    yield return new HLineBreak(Format(lineBreak.Format));
                    break;
                case HField field:
                    yield return new HField(field.Kind, Format(field.Format));
                    break;
                case HLink hyperlink:
                    var copy = new HLink(hyperlink.Target);
                    copy.Content.AddRange(Inlines(hyperlink.Content, paragraph, link: true));
                    yield return copy;
                    break;
                default:
                    yield return inline;
                    break;
            }
        }
    }

    private static string Title(HParagraph paragraph) =>
        string.Concat(paragraph.Inlines.Select(i => i switch
        {
            HText text => text.Text,
            HLink link => string.Concat(link.Content.OfType<HText>().Select(t => t.Text)),
            HTab => " ",
            _ => "",
        })).Trim();
}
