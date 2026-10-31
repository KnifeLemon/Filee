// Flows a document onto pages (GitHub Project 4, item "Layout engine: page composer"): each section's pages with its
// paper size, margins and gutter, its body filled with laid out paragraphs, and its headers and footers.
//
// - Pages: a section starts a page; the body runs from the top margin and header area down to the footer area. A
//   paragraph starts a page when it asks for one, a column when it asks for a column break, and a line that doesn't fit
//   goes to the next column or page.
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
// - Columns: a section's newspaper columns, with their gap and separator line; what doesn't fit in a column goes on in
//   the next, and after the last on the next page. Columns change without a new page where the model says (Word's
//   continuous section break, 한글's 다단 설정 나누기); the columns before are balanced then, so they end at about the
//   same height. At the end of a section they are not (neither 한글 nor Word does). 한글's stored lines that start a
//   column (stored flag) start one.
// - Notes: a note reference shows its number, raised, in the section's note format (한글 각주/미주 모양); footnotes go
//   to the foot of the page their reference is on, under the section's separator line and spacing, and a line whose
//   footnotes don't fit there moves on with them. Endnotes follow the last section's body on a new page, or right under
//   it when the section says so. Footnotes and endnotes take 한글's numbers, else they are numbered on through the
//   document. 한글 sets each note as high as its text, without the spacing below its last line, the last ending at the
//   body's bottom; that is measured against 한글's own PDFs. Footnotes go across the page's text width, also in
//   columns (한글 puts them under each column).
//
// Floating objects and master pages are later milestones.

using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Rendering;
using SkiaSharp;

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
    private sealed record Page(PageContent Content, HSection Section, Geometry Geometry, int Number, bool FirstOfSection)
    {
        /// <summary>The floating objects on the page, drawn when the page is done.</summary>
        public List<PlacedObject> Objects { get; } = [];

        /// <summary>What a paragraph on the page hides of it (한글 쪽 감추기).</summary>
        public HPageHiding? Hiding { get; set; }
    }

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

        // ───────────────────────── Regions and columns ─────────────────────────

        /// <summary>Something put into a column, drawn when its region is finished (columns may be balanced first).</summary>
        private sealed record Placed(int Column, float X, float Y, float Height, Action<PageContent, float, float> Draw);

        /// <summary>The columns of the region being filled: from <see cref="_regionTop"/> down, side by side.</summary>
        private HColumns _columns = HColumns.Single;
        private int _column;
        private bool _columnEmpty = true;
        private float _regionTop;
        private readonly List<Placed> _placed = [];

        private float Gap => _columns.Count > 1 ? _columns.Gap / 100f : 0;

        private float ColumnWidth => (_geometry.TextWidth - Gap * (_columns.Count - 1)) / Math.Max(1, _columns.Count);

        private float ColumnLeft(int column) => _geometry.Left + column * (ColumnWidth + Gap);

        /// <summary>Where the body ends on this page: above the footnotes.</summary>
        private float Bottom => _geometry.BodyBottom - FootnotesHeight([]);

        private float ColumnHeight => _geometry.BodyBottom - _regionTop;

        /// <summary>Puts something into the current column at the current position.</summary>
        private void Put(float x, float height, Action<PageContent, float, float> draw)
        {
            _placed.Add(new Placed(_column, x, _y, height, draw));
            _empty = false;
            _columnEmpty = false;
        }

        private void StartRegion(HColumns columns)
        {
            _columns = columns.Count > 1 ? columns : HColumns.Single;
            _column = 0;
            _columnEmpty = true;
            _regionTop = _y;
        }

        /// <summary>
        /// Draws what the region holds, its columns balanced first when <paramref name="balance"/> (columns changing on
        /// the page), and moves below it.
        /// </summary>
        private void EndRegion(bool balance)
        {
            if (_placed.Count == 0)
            {
                _column = 0;
                return;
            }
            var placed = balance && _columns.Count > 1 ? Balance(_placed) : _placed;
            var page = Current.Content;
            foreach (var item in placed)
                item.Draw(page, ColumnLeft(item.Column) + item.X, item.Y);
            var bottom = placed.Max(p => p.Y + p.Height);
            if (_columns.Separator && _columns.Count > 1)
            {
                var (top, gap, width, count, left) = (_regionTop, Gap, ColumnWidth, _columns.Count, _geometry.Left);
                page.Draw(canvas =>
                {
                    using var paint = new SkiaSharp.SKPaint { Style = SkiaSharp.SKPaintStyle.Stroke, StrokeWidth = 0.5f, Color = SkiaSharp.SKColors.Black, IsAntialias = true };
                    for (var c = 1; c < count; c++)
                    {
                        var x = left + c * (width + gap) - gap / 2;
                        canvas.DrawLine(x, top, x, bottom, paint);
                    }
                });
            }
            _placed.Clear();
            _y = bottom;
            _column = 0;
            _columnEmpty = true;
        }

        /// <summary>
        /// The region's content spread over its columns as evenly as it goes: the lowest column height at which
        /// everything still fits, keeping the order and the space between things.
        /// </summary>
        private List<Placed> Balance(List<Placed> placed)
        {
            // The space above each thing in its column (none at a column's top).
            var units = new List<(Placed Item, float Gap)>();
            var bottoms = new Dictionary<int, float>();
            foreach (var item in placed)
            {
                var gap = bottoms.TryGetValue(item.Column, out var bottom) ? Math.Max(0, item.Y - bottom) : 0;
                units.Add((item, gap));
                bottoms[item.Column] = item.Y + item.Height;
            }
            var total = units.Sum(u => u.Gap + u.Item.Height);
            var capacity = Bottom - _regionTop;
            List<Placed>? Try(float target)
            {
                var result = new List<Placed>(units.Count);
                var (column, y, first) = (0, _regionTop, true);
                foreach (var (item, gap) in units)
                {
                    var top = first ? y : y + gap;
                    if (!first && top + item.Height > _regionTop + target + Epsilon)
                    {
                        // Past the target height: on in the next column, the last one too high means the target is.
                        if (column + 1 == _columns.Count)
                            return null;
                        (column, top) = (column + 1, _regionTop);
                    }
                    result.Add(item with { Column = column, Y = top });
                    y = top + item.Height;
                    first = false;
                }
                return result;
            }
            // The first columns take the more (as 한글 balances): the lowest target at which everything fits.
            float low = total / _columns.Count, high = capacity;
            if (Try(high) is null)
                return placed;
            for (var step = 0; step < 24 && high - low > 0.5f; step++)
            {
                var middle = (low + high) / 2;
                if (Try(middle) is null)
                    low = middle;
                else
                    high = middle;
            }
            return Try(high) ?? placed;
        }

        /// <summary>Moves to the next column, or to the next page after the last one.</summary>
        private void NextColumn()
        {
            if (_column + 1 < _columns.Count)
            {
                _column++;
                _y = _regionTop;
                _columnEmpty = true;
            }
            else
            {
                NewPage();
            }
        }

        // ───────────────────────── Pages ─────────────────────────

        public ComposedDocument Compose()
        {
            for (var s = 0; s < document.Sections.Count; s++)
            {
                var section = document.Sections[s];
                if (_pages.Count > 0)
                    EndRegion(balance: false);
                _section = section;
                _lastStored = null;
                _geometry = Geometry.Of(section.Page ?? DefaultPage);
                if (section.StartPageNumber is { } start)
                    _number = start - 1;
                HangulPage(firstOfSection: true, section.Columns);
                Body(section.Blocks);
                if (s == document.Sections.Count - 1)
                    Endnotes();
            }
            if (_pages.Count == 0)
            {
                _section = new HSection();
                NewPage(firstOfSection: true);
            }
            EndRegion(balance: false);
            FinishPage();
            _guard.PageFinished(_empty);
            foreach (var page in _pages)
            {
                DrawObjects(page);
                HeadersAndFooters(page);
            }
            return new ComposedDocument([.. _pages.Select(p => p.Content)], _outline);
        }

        /// <summary>A page 한글 starts too (a section's first, a page break, a stored page start).</summary>
        private void HangulPage(bool firstOfSection = false, HColumns? columns = null)
        {
            NewPage(firstOfSection, columns);
            _hangulStart = (_pages.Count, 0);
        }

        private void NewPage(bool firstOfSection = false, HColumns? columns = null)
        {
            var keep = columns ?? _columns;
            if (_pages.Count > 0)
            {
                EndRegion(balance: false);
                FinishPage();
                _guard.PageFinished(_empty);
            }
            _number++;
            _pages.Add(new Page(new PageContent(_geometry.Width, _geometry.Height), _section!, _geometry, _number, firstOfSection));
            _y = _geometry.BodyTop;
            _empty = true;
            _inStep = true;
            StartRegion(keep);
        }

        private Page Current => _pages[^1];

        // ───────────────────────── Body ─────────────────────────

        private TableLayout? _tables;

        /// <summary>Tables and paragraph lists inside cells, headers and footers, laid out with this run's numbering and fields.</summary>
        private TableLayout Tables => _tables ??= CreateTables();

        private TableLayout CreateTables()
        {
            var tables = new TableLayout((p, width) => Layout(composer.Resolve(p), width), composer._styles is not null, () => Frame(_y));
            // Text boxes laid out like characters stack their content as cells do.
            composer._layouter.Stack = tables.Stack;
            return tables;
        }

        private void Body(List<HBlock> blocks)
        {
            object? next = null;
            HParagraph? holder = null;
            for (var i = 0; i < blocks.Count; i++)
            {
                _guard.Paragraph();
                if (blocks[i] is HTable table)
                {
                    var tableBox = next as TableBox ?? Tables.Layout(table, ColumnWidth);
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
                // Columns change here without a new page (Word's continuous section break): the columns so far are
                // balanced and the new ones start under them.
                if (paragraph.ColumnsChange is { } change)
                {
                    EndRegion(balance: true);
                    StartRegion(change);
                }
                if (paragraph.PageBreakBefore && !_empty)
                    HangulPage();
                else if (paragraph.ColumnBreakBefore && !_columnEmpty)
                {
                    // 한글's own column break: its stored places, measured from the column's top, hold again.
                    NextColumn();
                    _hangulStart = (_pages.Count, _column);
                    _inStep = true;
                }
                var box = next as ParagraphBox ?? Layout(paragraph, ColumnWidth);
                next = null;
                if (!box.StoredLines && Wrapped(paragraph, box) is { } wrapped)
                    box = wrapped;

                // Keep with next: this paragraph and the start of the next block in one column.
                if (paragraph.Format.KeepWithNext == true && !_columnEmpty && i + 1 < blocks.Count && !(box.StoredLines && _inStep))
                {
                    float start;
                    switch (blocks[i + 1])
                    {
                        case HParagraph following:
                            var nextBox = Layout(composer.Resolve(following), ColumnWidth);
                            next = nextBox;
                            start = nextBox.SpaceBefore + (nextBox.Lines.Count > 0 ? nextBox.Lines[0].Height : 0);
                            break;
                        case HTable following:
                            var nextTable = Tables.Layout(following, ColumnWidth);
                            next = nextTable;
                            start = nextTable.Groups() is [var (first, count), ..] ? nextTable.Rows.Skip(first).Take(count).Sum(r => r.Height) : 0;
                            break;
                        default:
                            start = 0;
                            break;
                    }
                    var together = box.Height + start;
                    if (_y + together > Bottom + Epsilon && together <= ColumnHeight)
                        NextColumn();
                }
                // Keep lines together: a paragraph that would split starts in the next column.
                if (paragraph.Format.KeepLines == true && !_columnEmpty && !(box.StoredLines && _inStep) &&
                    _y + box.Height > Bottom + Epsilon && box.Height <= ColumnHeight)
                    NextColumn();

                Place(paragraph, box);
                if (!box.StoredLines)
                    _inStep = false;
            }
        }

        /// <summary>
        /// Puts a table's rows into columns and pages from the current position on, repeating its header rows;
        /// <paramref name="holder"/> is the 한글 paragraph whose line the table is, or null.
        /// </summary>
        /// <param name="follower">The rest of the 한글 paragraph the table starts, whose text follows right under it.</param>
        private void PlaceTable(HTable table, TableBox box, HParagraph? holder, HParagraph? follower)
        {
            if (table.PageBreakBefore && !_empty)
                HangulPage();
            if (table.PageHiding is { } hiding)
                Current.Hiding = hiding;
            // The 한글 line the table is: the holder's, or the table's own when it is all its paragraph holds.
            var line = holder?.Lines?.Segments.FirstOrDefault() ?? (follower is null ? table.Line : null);
            var (lineTop, linePage) = (0f, _pages.Count);
            if (holder is not null || line is not null)
            {
                // Where 한글 put the line while the page follows 한글, else after the paragraph's space before.
                if (_inStep && line is { } stored)
                    _y = Math.Max(_y, _regionTop + stored.VerticalPosition / 100f);
                else if (!_columnEmpty)
                    _y += (holder?.Format.Before ?? 0) / 100f;
                lineTop = _y;
                // The objects floating at the table belong to the line the table is.
                if (holder is not null)
                    Anchor(holder, _y);
            }
            else if (follower is not null)
            {
                // The table starts its 한글 paragraph, placed from the paragraph's top by its offset; 한글 puts the
                // paragraph's text right under it, without the space before.
                _skipSpaceBefore = true;
            }
            var margin = table.OuterMargin ?? default;
            _y += (table.VerticalOffset + margin.Top) / 100f;
            // Filee's rows are not 한글's unless the table is as high as 한글's (its line for it, else its stored size);
            // else the stored places hold again on 한글's next page.
            _inStep = _inStep && TableLayout.AsStored(table, box.Height, line);
            var x = box.Offset + margin.Left / 100f;
            var header = box.Rows.Take(box.HeaderRows).ToList();
            // A table that must not split goes to the next column whole when it fits in one.
            if (box.PageBreak == HTablePageBreak.None && !_columnEmpty && _y + box.Height > Bottom + Epsilon && box.Height <= ColumnHeight)
                NextColumn();

            // The column holds nothing yet but, perhaps, this table's repeated header rows.
            var fresh = _columnEmpty;
            void Draw(IReadOnlyList<RowBox> rows)
            {
                var height = rows.Sum(r => r.Height);
                Put(x, height, (page, left, top) => TableLayout.Draw(page, rows, left, top));
                _y += height;
            }
            void NextPage(bool repeat)
            {
                NextColumn();
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
                    var room = Bottom - _y;
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
                    // Taller than an empty column and it can't split: placed as it is.
                    Draw(rows);
                    fresh = false;
                    break;
                }
            }
            foreach (var piece in box.Caption)
            {
                if (!_columnEmpty && _y + piece.Height > Bottom + Epsilon)
                    NextColumn();
                Put(x, piece.Height, piece.Draw);
                _y += piece.Height;
            }
            _y += margin.Bottom / 100f;
            // The rest of the table's line: down to its stored height while the table stayed on its page, its line
            // spacing and the paragraph's space after.
            if (line is { } own)
            {
                if (_pages.Count == linePage)
                    _y = Math.Max(_y, lineTop + own.VerticalSize / 100f);
                _y += own.Spacing / 100f + (holder?.Format.After ?? 0) / 100f;
            }
            else if (holder is not null)
            {
                _y += (holder.Format.After ?? 0) / 100f;
            }
            // Notes referenced inside the table's cells go to the foot of the page the table ends on.
            foreach (var note in _numbered.Where(n => !_notesPlaced.Contains(n)).ToList())
                AddNote(note);
        }

        private ParagraphBox Layout(HParagraph paragraph, float width, string? marker = null, Func<float, float, (float, float)>? wrap = null)
        {
            marker ??= paragraph.List is { Numbered: true } list ? _numbers.Next(list) : null;
            return composer._layouter.Layout(paragraph, width, marker, FieldText, NoteText, wrap);
        }

        /// <summary>Puts a paragraph's lines into columns and pages from the current position on.</summary>
        private void Place(HParagraph paragraph, ParagraphBox box)
        {
            var lines = box.Lines;
            if (lines.Count == 0)
                Anchor(paragraph, _y);
            var widowOrphan = paragraph.Format.WidowOrphan == true;
            if (!_columnEmpty && !_skipSpaceBefore)
                _y += box.SpaceBefore;
            _skipSpaceBefore = false;
            // In columns, 한글's stored places start over (a smaller one than the last) where 한글 started the next
            // column. Filee follows unless it moved on by itself since 한글's last start, and the stored places hold
            // again. (A page 한글 started is left to Filee's own page breaks: tables crossing pages make many.)
            if (_columns.Count > 1 && box.StoredLines && lines.Count > 0 && lines[0] is { StoredTop: { } first, StoredPageStart: false } && _lastStored is { } last
                && first < last - 1)
            {
                if (_hangulStart == (_pages.Count, _column) && !_columnEmpty && _column + 1 < _columns.Count)
                    NextColumn();
                _hangulStart = (_pages.Count, _column);
                _inStep = true;
            }
            for (var i = 0; i < lines.Count;)
            {
                var stored = box.StoredLines && _inStep;
                if (stored && lines[i].StoredPageStart && !_empty)
                    HangulPage();
                else if (stored && lines[i].StoredColumnStart && !_columnEmpty && _columns.Count > 1)
                    NextColumn();
                // How many of the remaining lines fit in this column, with the footnotes they bring.
                var fit = 0;
                var y = _y;
                var notes = new List<HNote>();
                for (var j = i; j < lines.Count; j++)
                {
                    if (j > i && stored && (lines[j].StoredPageStart || lines[j].StoredColumnStart && _columns.Count > 1))
                        break;
                    // 한글's line on both sides of an object is two stored lines at one place: the second goes beside the first.
                    var top = box.StoredLines && j > i && SameRow(lines, j) ? y - lines[j - 1].Height
                        : stored && lines[j].StoredTop is { } storedTop ? Math.Max(y, _regionTop + storedTop) : stored ? y : Clear(y, lines[j].Height);
                    notes.AddRange(NewNotes(lines[j]));
                    if (top + lines[j].Height > _geometry.BodyBottom - FootnotesHeight(notes) + Epsilon)
                        break;
                    y = top + lines[j].Height;
                    fit++;
                }
                if (widowOrphan && !stored && fit < lines.Count - i)
                {
                    // At least two lines of the paragraph in this column (or none) and two in the next.
                    if (i == 0 && fit < MinimumLines && !_columnEmpty)
                        fit = 0;
                    else if (lines.Count - i - fit < MinimumLines && fit > MinimumLines - 1 + (i == 0 ? 1 : 0))
                        fit -= MinimumLines - (lines.Count - i - fit);
                }
                if (fit == 0)
                {
                    if (!_columnEmpty)
                    {
                        // Objects placed ahead with the paragraph go on with it.
                        if (i == 0 && _anchored.Remove(paragraph, out var ahead))
                        {
                            Current.Objects.RemoveAll(ahead.Contains);
                            foreach (var item in ahead)
                                _floated.Remove(item.Object);
                        }
                        NextColumn();
                        _inStep = false;
                        continue;
                    }
                    fit = 1; // a line higher than the column: placed anyway
                }
                for (var j = i; j < i + fit; j++)
                {
                    if (box.StoredLines && j > i && SameRow(lines, j))
                        _y -= lines[j - 1].Height;
                    PlaceLine(paragraph, box, lines[j], j == 0, stored);
                }
                i += fit;
                if (i < lines.Count)
                {
                    var stays = lines[i].StoredPageStart || lines[i].StoredColumnStart;
                    if (!(stored && stays))
                    {
                        NextColumn();
                        // A break Filee made, not 한글: the stored places no longer hold until 한글's next page.
                        if (!stays)
                            _inStep = false;
                    }
                }
            }
            _y += box.SpaceAfter;
        }

        /// <summary>A stored line at the same place as the one before it: the other side of an object 한글 put text on both sides of.</summary>
        private static bool SameRow(IReadOnlyList<LineBox> lines, int j) =>
            j > 0 && lines[j].StoredTop is { } top && lines[j - 1].StoredTop == top && !lines[j].StoredPageStart && !lines[j].StoredColumnStart;

        /// <summary>The stored top of the last line placed from 한글's stored lines.</summary>
        private float? _lastStored;

        /// <summary>The page and column where 한글's last column or page started.</summary>
        private (int Page, int Column) _hangulStart;

        private void PlaceLine(HParagraph paragraph, ParagraphBox box, LineBox line, bool first, bool stored)
        {
            if (line.StoredTop is { } storedTop)
                _lastStored = storedTop;
            if (stored && line.StoredTop is { } top)
                _y = Math.Max(_y, _regionTop + top);
            else if (!stored)
                _y = Clear(_y, line.Height);
            if (first && paragraph.HeadingLevel > 0)
                _outline.Add(new OutlineEntry(Title(paragraph), _pages.Count - 1, _y, paragraph.HeadingLevel - 1));
            // An object placed against the paper or the page goes on the page of the line that holds it, as 한글 places
            // it; the paragraph's first line places the others.
            if (first && !_anchored.Remove(paragraph))
                Anchor(paragraph, _y, (item, anchor) => !OnPage(anchor) || !box.FloatingAt.TryGetValue(item, out var at) || at < line.End || box.Lines.Count == 1);
            else if (!first)
                Anchor(paragraph, _y, (item, anchor) => OnPage(anchor) && box.FloatingAt.TryGetValue(item, out var at) && at >= line.Start && (at < line.End || line == box.Lines[^1]));
            if (first && paragraph.PageHiding is { } hiding)
                Current.Hiding = hiding;
            Put(0, line.Height, line.Draw);
            _y += line.Height;
            foreach (var note in line.Notes)
                AddNote(note);
        }

        // ───────────────────────── Floating objects ─────────────────────────

        private int _objects;

        /// <summary>Paragraphs whose objects were placed before their lines were laid out (to wrap the lines around them).</summary>
        private readonly Dictionary<HParagraph, List<PlacedObject>> _anchored = new(ReferenceEqualityComparer.Instance);

        /// <summary>The column's left and right edge.</summary>
        private (float Left, float Right) ColumnEdges => (ColumnLeft(_column), ColumnLeft(_column) + ColumnWidth);

        /// <summary>The objects on the page text goes around (beside or above and below) that reach into the column.</summary>
        private IEnumerable<PlacedObject> InTheWay(string wrap)
        {
            var (left, right) = ColumnEdges;
            return Current.Objects.Where(o => o.Wrap == wrap && o.Area.Right > left + Epsilon && o.Area.Left < right - Epsilon);
        }

        /// <summary>Where a line laid out anew from <paramref name="top"/> goes: below the objects text goes above and below.</summary>
        private float Clear(float top, float height)
        {
            var moved = true;
            while (moved)
            {
                moved = false;
                foreach (var item in InTheWay("TOP_AND_BOTTOM"))
                {
                    if (item.Area.Top < top + height - Epsilon && item.Area.Bottom > top + Epsilon)
                    {
                        top = item.Area.Bottom;
                        moved = true;
                    }
                }
            }
            return top;
        }

        /// <summary>
        /// A paragraph laid out anew again with its lines beside the objects text goes around (on each object's wider
        /// side), its own objects placed first; null when none is beside it.
        /// </summary>
        private ParagraphBox? Wrapped(HParagraph paragraph, ParagraphBox box)
        {
            var top = _y + (_columnEmpty || _skipSpaceBefore ? 0 : box.SpaceBefore);
            if (FloatingObjects.Of(paragraph).Any(o => o.Anchor.Wrap is "SQUARE" or "TOP_AND_BOTTOM"))
                _anchored[paragraph] = Anchor(paragraph, top);
            var (left, right) = ColumnEdges;
            var beside = InTheWay("SQUARE").Where(o => o.Area.Bottom > top + Epsilon).ToList();
            if (beside.Count == 0)
                return null;
            // Lines go below the objects text goes above and below first (as they will be placed), and then beside.
            var shift = 0f;
            (float, float) Insets(float y, float height)
            {
                var at = Clear(top + y + shift, height);
                shift = at - top - y;
                var (inLeft, inRight) = (0f, 0f);
                foreach (var item in beside.Where(o => o.Area.Top < at + height - Epsilon && o.Area.Bottom > at + Epsilon))
                {
                    if (item.Area.Left - left >= right - item.Area.Right)
                        inRight = Math.Max(inRight, right - item.Area.Left);
                    else
                        inLeft = Math.Max(inLeft, item.Area.Right - left);
                }
                return (inLeft, inRight);
            }
            return Layout(paragraph, ColumnWidth, box.Marker, Insets);
        }

        /// <summary>The page's areas for anchors, with the current column; a paragraph at <paramref name="top"/> as wide as the column.</summary>
        private AnchorFrame Frame(float top, HParagraph? paragraph = null)
        {
            var g = _geometry;
            var (left, width) = (ColumnLeft(_column), ColumnWidth);
            return new AnchorFrame(
                new SKRect(0, 0, g.Width, g.Height),
                new SKRect(g.Left, g.BodyTop, g.Left + g.TextWidth, g.BodyBottom),
                new SKRect(left, g.BodyTop, left + width, g.BodyBottom),
                new SKRect(left + (paragraph?.Format.Left ?? 0) / 100f, top, left + width - (paragraph?.Format.Right ?? 0) / 100f, g.BodyBottom),
                _number % 2 == 1);
        }

        private static bool OnPage(HAnchor anchor) => anchor.VerticalRelativeTo is "PAPER" or "PAGE";

        /// <summary>Floating objects placed so far (each goes on the page once).</summary>
        private readonly HashSet<HInline> _floated = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// Places the floating objects of a paragraph whose first line starts at <paramref name="top"/> (those
        /// <paramref name="include"/> takes, else all), each once.
        /// </summary>
        private List<PlacedObject> Anchor(HParagraph paragraph, float top, Func<HInline, HAnchor, bool>? include = null)
        {
            var frame = Frame(top, paragraph);
            var placed = new List<PlacedObject>();
            foreach (var (item, anchor) in FloatingObjects.Of(paragraph))
            {
                if (_floated.Contains(item) || include?.Invoke(item, anchor) == false)
                    continue;
                if (FloatingObjects.Size(item) is not var (w, h))
                    continue;
                _floated.Add(item);
                placed.Add(new PlacedObject(item, FloatingObjects.Place(anchor, w, h, frame), anchor.Wrap, FloatingObjects.ZOrder(item), _objects++));
            }
            Current.Objects.AddRange(placed);
            return placed;
        }

        /// <summary>Draws a page's floating objects, behind or in front of its text, lower z-order first.</summary>
        private void DrawObjects(Page page)
        {
            _geometry = page.Geometry;
            _number = page.Number;
            foreach (var item in page.Objects.OrderBy(o => o.Order).ThenBy(o => o.Sequence))
            {
                var layer = new PageContent(page.Content.Width, page.Content.Height);
                FloatingObjects.Draw(layer, item.Object, item.Area, (blocks, width) => Tables.Stack(blocks, width));
                page.Content.Add(layer, item.Behind);
            }
        }

        // ───────────────────────── Notes ─────────────────────────

        private readonly Dictionary<HNote, string> _noteNumbers = new(ReferenceEqualityComparer.Instance);
        private readonly List<HNote> _numbered = [];
        private readonly HashSet<HNote> _notesPlaced = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<HNote, (List<Piece> Lines, float Height)> _noteLayouts = new(ReferenceEqualityComparer.Instance);
        private readonly List<HNote> _footnotes = [];
        private readonly List<HNote> _endnotes = [];
        private int _footnoteCount;
        private int _endnoteCount;

        private HNoteShape Shape(HNote note) => note.Endnote ? _section!.Endnotes : _section!.Footnotes;

        /// <summary>
        /// The number a note reference shows: 한글's own number, else footnotes and endnotes counted on separately
        /// through the document; in the section's note format.
        /// </summary>
        private string NoteText(HNote note)
        {
            if (!_noteNumbers.TryGetValue(note, out var text))
            {
                var number = note.Number ?? (note.Endnote ? _endnoteCount + 1 : _footnoteCount + 1);
                if (note.Endnote)
                    _endnoteCount = number;
                else
                    _footnoteCount = number;
                var format = Shape(note).Format;
                _noteNumbers[note] = text = format.Prefix + ListNumbers.Format(number, format.Type) + format.Suffix;
                _numbered.Add(note);
            }
            return text;
        }

        /// <summary>
        /// A note's paragraphs laid out across the text width, its number in front of the first, and its height: from
        /// the first paragraph's space before down to its last line's text, without the spacing below it (as 한글
        /// stacks notes).
        /// </summary>
        private (List<Piece> Lines, float Height) NoteLayout(HNote note)
        {
            if (_noteLayouts.TryGetValue(note, out var layout))
                return layout;
            var marker = NoteText(note);
            var pieces = new List<Piece>();
            float y = 0, height = 0;
            var first = true;
            foreach (var paragraph in note.Blocks.OfType<HParagraph>())
            {
                var box = Layout(composer.Resolve(paragraph), _geometry.TextWidth, first ? marker : null);
                y += box.SpaceBefore;
                first = false;
                foreach (var line in box.Lines)
                {
                    pieces.Add(new Piece(y, line.Height, line.Draw, line.Below));
                    height = y + line.TextHeight;
                    y += line.Height;
                }
                y += box.SpaceAfter;
            }
            return _noteLayouts[note] = (pieces, height);
        }

        /// <summary>The height the footnotes of this page take with <paramref name="more"/> added: space and line above included.</summary>
        private float FootnotesHeight(IReadOnlyCollection<HNote> more)
        {
            var count = _footnotes.Count + more.Count;
            if (count == 0)
                return 0;
            var shape = _section!.Footnotes;
            return _footnotes.Concat(more).Sum(n => NoteLayout(n).Height) + (count - 1) * shape.Between / 100f + (shape.Above + shape.Below) / 100f;
        }

        /// <summary>The footnotes a line brings that this page doesn't hold yet.</summary>
        private IEnumerable<HNote> NewNotes(LineBox line) => line.Notes.Where(n => !n.Endnote && !_notesPlaced.Contains(n));

        private void AddNote(HNote note)
        {
            if (!_notesPlaced.Add(note))
                return;
            if (note.Endnote)
                _endnotes.Add(note);
            else
                _footnotes.Add(note);
        }

        /// <summary>Draws the page's footnotes at the foot of its body, under their separator line.</summary>
        private void FinishPage()
        {
            if (_footnotes.Count == 0 || _pages.Count == 0)
                return;
            var page = Current.Content;
            var shape = _section!.Footnotes;
            var top = _geometry.BodyBottom - FootnotesHeight([]) + shape.Above / 100f;
            SeparatorLine(page, shape, _geometry.Left, top);
            top += shape.Below / 100f;
            foreach (var note in _footnotes)
            {
                var (lines, height) = NoteLayout(note);
                foreach (var piece in lines)
                    piece.Draw(page, _geometry.Left, top + piece.Top);
                top += height + shape.Between / 100f;
            }
            _footnotes.Clear();
        }

        private void SeparatorLine(PageContent page, HNoteShape shape, float left, float y)
        {
            var length = shape.LineLength(_geometry.TextWidth);
            if (length <= 0)
                return;
            page.Draw(canvas =>
            {
                using var paint = new SkiaSharp.SKPaint { Style = SkiaSharp.SKPaintStyle.Stroke, StrokeWidth = 0.34f, Color = SkiaSharp.SKColors.Black, IsAntialias = true };
                canvas.DrawLine(left, y, left + length, y, paint);
            });
        }

        /// <summary>
        /// The endnotes, after the last section's body: on a page of their own, or right under the text and a separator
        /// line when the section says so.
        /// </summary>
        private void Endnotes()
        {
            if (_endnotes.Count == 0)
                return;
            var shape = _section!.Endnotes;
            if (!shape.BeneathText)
            {
                if (!_empty)
                    NewPage();
            }
            else if (!_columnEmpty)
            {
                _y += shape.Above / 100f;
                Put(0, 0, (page, left, top) => SeparatorLine(page, shape, left, top));
                _y += shape.Below / 100f;
            }
            foreach (var note in _endnotes.ToList())
            {
                var (lines, height) = NoteLayout(note);
                var start = _y;
                foreach (var piece in lines)
                {
                    if (!_columnEmpty && _y + piece.Height > Bottom + Epsilon)
                    {
                        NextColumn();
                        start = _y - piece.Top;
                    }
                    _y = start + piece.Top;
                    Put(0, piece.Height, piece.Draw);
                    _y += piece.Height;
                }
                _y = start + height + shape.Between / 100f;
            }
            _endnotes.Clear();
        }

        // ───────────────────────── Headers and footers ─────────────────────────

        private void HeadersAndFooters(Page page)
        {
            _geometry = page.Geometry;
            var section = page.Section;
            // Fields in headers and footers show the number of the page they are on.
            _number = page.Number;
            if (!(page.FirstOfSection && section.HideFirstHeader) && page.Hiding?.Header != true && Pick(section.Headers, page.Number) is { } header)
                Area(page, header.Blocks, page.Geometry.HeaderTop);
            if (!(page.FirstOfSection && section.HideFirstFooter) && page.Hiding?.Footer != true && Pick(section.Footers, page.Number) is { } footer)
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
            PageHiding = paragraph.PageHiding,
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
                    yield return new HEquation(equation.Math, Format(equation.Format))
                    {
                        Width = equation.Width,
                        Height = equation.Height,
                        BaseLine = equation.BaseLine,
                        Size = equation.Size,
                        Color = equation.Color,
                    };
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
