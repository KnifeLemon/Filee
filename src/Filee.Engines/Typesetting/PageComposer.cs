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
// - Headers and footers: the section's header and footer for both pages, odd or even pages (by page number), the first
//   page's own (Word's "different first page") or none on the section's first page where it hides them; laid out in the
//   header and footer areas. A header or footer of a continuous section break (Word) applies from the page after the
//   break, or from the break's page when the break starts it. Word models: the footer grows up from the bottom of its
//   area, and a header or footer taller than its area moves the body (see Dress).
// - Master pages (바탕쪽): drawn behind the page's body (or in front of it) from the top of the body; see MasterPages.
// - Page borders and backgrounds (쪽 테두리/배경) under everything else; a Word page border in front of the text (its
//   default, w:pgBorders zOrder) over everything instead. See PageBorder.
// - Paragraph borders and backgrounds (BoxBorder): consecutive paragraphs with the same border share one box, which
//   takes room above its first line and below its last, is drawn under the text a line at a time and stays open where a
//   page or column break splits it.
// - Page numbers count on from section to section unless a section restarts them (or a paragraph in it does, from its
//   page on); a page number field shows the number of its page and a total pages field the count, which a first pass
//   finds when the body shows it. 한글's automatic page number is drawn apart from the footer; see PageNumber.
// - Styles: models read from other formats leave formats to the 한글 style (null members); with a StyleResolver every
//   paragraph and run is resolved before layout, as the HWPX writer would write it. A Word equation's number, which
//   the reader keeps in a text box placed for rhwp, goes with its equation instead (ParagraphLayout.Equations.cs).
//
// - Tables (TableLayout): rows bound by spanning cells move to a page together, header rows repeat, and a table splits
//   by its page break mode. A Word floating table (HTable.Float) is placed as a floating object instead, against the
//   paragraph after it or the page, and the text after it goes around it; one longer than a column splits across
//   pages, and one that must not overlap (w:tblOverlap never) goes below the floating tables in its way (see Float).
// - Paragraph spacing: in a Word document the larger of a paragraph's space after and the next one's space before
//   goes between them (HDocument.CollapseParagraphSpacing); the model keeps both.
// - Sections written vertically (HSection.TextFlow: Word tbRl / btLr, 한글 세로쓰기, ODF tb-rl pages): the body is laid
//   out across on the page turned a quarter (its lines as long as the body is high, the next line further on), then
//   turned into place with VerticalText: top to bottom with the first line at the right (bottom to top with it at the
//   left for btLr), CJK characters standing upright. Columns become bands from the top down, footnotes go to the
//   far side. Headers, footers, page numbers, master pages and page borders stay across; floating pictures and shapes
//   keep standing upright at the place their turned box takes (floating tables turn with the text).
// - Columns: a section's newspaper columns, with their gap and separator line; what doesn't fit in a column goes on in
//   the next, and after the last on the next page. Columns change without a new page where the model says (Word's
//   continuous section break, 한글's 다단 설정 나누기); the columns before are balanced then, so they end at about the
//   same height (columns of their own widths with their lines laid out anew for each column's width). At the end of a section they are not (neither 한글 nor Word does). 한글's stored lines that start a
//   column (stored flag) start one.
// - Notes: a note reference shows its number, raised, in the section's note format (한글 각주/미주 모양); footnotes go
//   to the foot of the page their reference is on, under the section's separator line and spacing, and a line whose
//   footnotes don't fit there moves on with them. Endnotes follow the last section's body on a new page, or right under
//   it when the section says so. Footnotes and endnotes take 한글's numbers, else they are numbered on through the
//   document. 한글 sets each note as high as its text, without the spacing below its last line, the last ending at the
//   body's bottom; that is measured against 한글's own PDFs. Footnotes go across the page's text width, also in
//   columns (한글 puts them under each column).
// - Floating objects (FloatingObjects): placed from where their paragraph starts (those against the paper or the page
//   from the line that holds them) and drawn when the page is done. Lines laid out anew go around the ones text wraps
//   around: each line gets its place and the stretches beside them at its height (Room), a row of text taking a part in
//   each stretch on both sides of an object; a row's lines stay in one column, also when columns are balanced.
// - Limits (LayoutGuard): the time budget and cancellation are checked before every block, every paragraph in a cell,
//   header, footer or text box, every page a table goes on to and every few hundred lines; the pages count as they are
//   finished. A line finds its notes, objects and the objects in its way without looking through the whole paragraph.

using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Metafiles;
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

    /// <summary>The document's neighbouring paragraphs keep the larger of their spaces between them (HDocument.CollapseParagraphSpacing).</summary>
    private bool _collapseSpacing;

    /// <param name="styles">Resolves formats a model leaves to the 한글 style; null for models read from HWPX.</param>
    /// <param name="storedLines">Paragraphs follow 한글's stored line layout where it can be used.</param>
    public PageComposer(FontResolver fonts, Shaper shaper, IReadOnlyList<HKinsoku>? kinsoku = null, StyleResolver? styles = null, bool storedLines = true)
    {
        // Paragraph borders and equation numbers of models of other formats are Word's (borders outside the text), 한글's otherwise.
        _layouter = new ParagraphLayouter(fonts, shaper, kinsoku, storedLines)
        {
            WordBorders = styles is not null,
            WordEquations = styles is not null,
            TemplateFaces = styles?.Faces,
        };
        _styles = styles;
    }

    /// <summary>The document's pages.</summary>
    public ComposedDocument Compose(HDocument document, CancellationToken cancellationToken = default)
    {
        _layouter.FontAlternates = document.FontAlternates;
        _layouter.DefaultTabStop = document.DefaultTabStop;
        _layouter.DefaultTabFromIndent = document.DefaultTabStopFromIndent;
        _layouter.WordLineHeights = document.WordLineHeights;
        _collapseSpacing = document.CollapseParagraphSpacing;
        // The body's total pages fields need the count first: a first pass finds it.
        int? total = null;
        if (document.Sections.Any(s => Fields(s.Blocks).Any(f => f.Kind == HFieldKind.TotalPages)))
            total = new Run(this, document, null, cancellationToken).Compose().Pages.Count;
        return new Run(this, document, total, cancellationToken).Compose();
    }

    /// <summary>The page geometry of a section, in points.</summary>
    private sealed record Geometry(float Width, float Height, float Left, float TextWidth, float HeaderTop, float BodyTop, float BodyBottom, float FooterTop, float FooterBottom)
    {
        /// <summary>The body's top and bottom as the margins set them, before a tall header or footer moves the body.</summary>
        public float MarginTop { get; init; }
        public float MarginBottom { get; init; }

        public static Geometry Of(HPage page)
        {
            var geometry = new Geometry(
                page.Width / 100f,
                page.Height / 100f,
                (page.Left + page.Gutter) / 100f,
                page.TextWidth / 100f,
                page.Top / 100f,
                (page.Top + page.Header) / 100f,
                (page.Height - page.Bottom - page.Footer) / 100f,
                (page.Height - page.Bottom - page.Footer) / 100f,
                (page.Height - page.Bottom) / 100f);
            return geometry with { MarginTop = geometry.BodyTop, MarginBottom = geometry.BodyBottom };
        }

        /// <summary>
        /// The body of these areas on the page turned a quarter, for text written vertically: lines run along the body's
        /// height from its top (from its bottom when <paramref name="upward"/>), and go on from its right edge to its
        /// left (from the left to the right). VerticalText.Place(upward, the paper) turns it back.
        /// </summary>
        public Geometry Turned(bool upward)
        {
            var (first, last) = upward ? (Left, Left + TextWidth) : (Width - Left - TextWidth, Width - Left);
            return new Geometry(Height, Width, upward ? Height - BodyBottom : BodyTop, BodyBottom - BodyTop, first, first, last, last, last)
            {
                MarginTop = first,
                MarginBottom = last,
            };
        }
    }

    /// <summary>A page and what its headers and footers need.</summary>
    private sealed record Page(PageContent Content, HSection Section, Geometry Geometry, int Number, bool FirstOfSection)
    {
        /// <summary>The page's areas, its body moved by a tall header or footer (see Dress).</summary>
        public Geometry Geometry { get; set; } = Geometry;

        /// <summary>The header and footer drawn on the page; null for none.</summary>
        public List<HBlock>? Header { get; set; }
        public List<HBlock>? Footer { get; set; }

        /// <summary>The floating objects on the page, drawn when the page is done.</summary>
        public List<PlacedObject> Objects { get; } = [];

        /// <summary>What a paragraph on the page hides of it (한글 쪽 감추기).</summary>
        public HPageHiding? Hiding { get; set; }

        /// <summary>
        /// The body of a page written vertically, laid out across on the page turned a quarter (Geometry.Turned) and
        /// turned into place when the page is done; null for a page written across, whose body is drawn on
        /// <see cref="Content"/>.
        /// </summary>
        public PageContent? Turned { get; init; }

        /// <summary>What the body is drawn on.</summary>
        public PageContent Body => Turned ?? Content;
    }

    /// <summary>One composition of a document.</summary>
    private sealed class Run(PageComposer composer, HDocument document, int? total, CancellationToken cancellationToken)
    {
        private readonly LayoutGuard _guard = new(DocumentLimits.Default, cancellationToken);
        private readonly ListNumbers _numbers = new();
        private readonly List<Page> _pages = [];
        private readonly List<OutlineEntry> _outline = [];
        private Geometry _geometry = Geometry.Of(DefaultPage);

        /// <summary>The section's areas before a header or footer moves the body.</summary>
        private Geometry _paper = Geometry.Of(DefaultPage);
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
        /// <param name="Under">
        /// What goes under everything in the region (a paragraph's border and background), told whether the thing is the
        /// first in its column.
        /// </param>
        /// <param name="Source">
        /// The paragraph line it is, when that line may be laid out anew for a column of another width (no border, not
        /// 한글's stored line, not beside an object); see BalanceSized.
        /// </param>
        private sealed record Placed(int Column, float X, float Y, float Height, Action<PageContent, float, float> Draw,
            Action<PageContent, float, float, bool>? Under = null, (HParagraph Paragraph, ParagraphBox Box, LineBox Line)? Source = null);

        /// <summary>The columns of the region being filled: from <see cref="_regionTop"/> down, side by side.</summary>
        private HColumns _columns = HColumns.Single;
        private int _column;
        private bool _columnEmpty = true;
        private float _regionTop;
        private readonly List<Placed> _placed = [];

        private float Gap => _columns.Count > 1 ? _columns.Gap / 100f : 0;

        /// <summary>The width of the current column.</summary>
        private float ColumnWidth => ColumnBox(_column).Width;

        /// <summary>The left edge of a column: the first is the leftmost, or the rightmost in a right-to-left section.</summary>
        private float ColumnLeft(int column) => ColumnBox(column).Left;

        /// <summary>A column's left edge and width: equal columns, or columns of their own sizes scaled to the text width.</summary>
        private (float Left, float Width) ColumnBox(int column)
        {
            var count = Math.Max(1, _columns.Count);
            if (_columns.Sizes is { } sizes && sizes.Count == count && count > 1)
                return SizedColumns(sizes)[Math.Clamp(column, 0, count - 1)];
            var width = (_geometry.TextWidth - Gap * (count - 1)) / count;
            return (_geometry.Left + (_columns.RightToLeft ? count - 1 - column : column) * (width + Gap), width);
        }

        /// <summary>The columns of their own sizes worked out for the columns and text width they were last asked for.</summary>
        private (HColumns Columns, float Left, float Width, (float Left, float Width)[] Boxes)? _sized;

        /// <summary>
        /// Columns of their own sizes across the text width, in reading order: their widths and the spaces after them
        /// scaled together to fill it (Word's add up to it), the first at the left, or at the right in a right-to-left
        /// section.
        /// </summary>
        private (float Left, float Width)[] SizedColumns(IReadOnlyList<HColumnSize> sizes)
        {
            var (left, textWidth) = (_geometry.Left, _geometry.TextWidth);
            if (_sized is { } known && ReferenceEquals(known.Columns, _columns) && known.Left == left && known.Width == textWidth)
                return known.Boxes;
            var total = 0.0;
            for (var i = 0; i < sizes.Count; i++)
                total += sizes[i].Width + (i + 1 < sizes.Count ? sizes[i].Gap : 0);
            var scale = total > 0 ? textWidth / total : 0;
            var boxes = new (float Left, float Width)[sizes.Count];
            var x = 0.0;
            for (var i = 0; i < sizes.Count; i++)
            {
                var width = sizes[i].Width * scale;
                boxes[i] = ((float)(_columns.RightToLeft ? left + textWidth - x - width : left + x), (float)width);
                x += width + (i + 1 < sizes.Count ? sizes[i].Gap * scale : 0);
            }
            _sized = (_columns, left, textWidth, boxes);
            return boxes;
        }

        /// <summary>Where the body ends on this page: above the footnotes.</summary>
        private float Bottom => _geometry.BodyBottom - FootnotesHeight();

        private float ColumnHeight => _geometry.BodyBottom - _regionTop;

        /// <summary>Puts something into the current column at the current position.</summary>
        private void Put(float x, float height, Action<PageContent, float, float> draw, Action<PageContent, float, float, bool>? under = null,
            (HParagraph, ParagraphBox, LineBox)? source = null)
        {
            _placed.Add(new Placed(_column, x, _y, height, draw, under, source));
            _empty = false;
            _columnEmpty = false;
        }

        private void StartRegion(HColumns columns)
        {
            _columns = columns.Count > 1 ? columns : HColumns.Single;
            _columnBroken = false;
            _column = 0;
            _columnEmpty = true;
            _regionTop = _y;
        }

        /// <summary>A column break in the region put its content into the columns it is in: they stay as filled.</summary>
        private bool _columnBroken;

        /// <summary>
        /// Draws what the region holds, its columns balanced first when <paramref name="balance"/> (columns changing on
        /// the page) and no column break placed its content, and moves below it.
        /// </summary>
        private void EndRegion(bool balance)
        {
            if (_placed.Count == 0)
            {
                _column = 0;
                return;
            }
            var placed = balance && _columns.Count > 1 && !_columnBroken ? Balance(_placed) : _placed;
            var page = Current.Body;
            // Paragraph borders and backgrounds first, under the text and tables; things come in their columns' order.
            var started = new HashSet<int>();
            foreach (var item in placed)
            {
                var columnTop = started.Add(item.Column);
                item.Under?.Invoke(page, ColumnLeft(item.Column) + item.X, item.Y, columnTop);
            }
            foreach (var item in placed)
                item.Draw(page, ColumnLeft(item.Column) + item.X, item.Y);
            var bottom = placed.Max(p => p.Y + p.Height);
            if (_columns.Separator && _columns.Count > 1)
            {
                // A line in the middle of the space between each two columns.
                var lines = new float[_columns.Count - 1];
                for (var c = 0; c + 1 < _columns.Count; c++)
                {
                    var (one, next) = (ColumnBox(c), ColumnBox(c + 1));
                    lines[c] = _columns.RightToLeft ? (next.Left + next.Width + one.Left) / 2 : (one.Left + one.Width + next.Left) / 2;
                }
                var top = _regionTop;
                page.Draw(canvas =>
                {
                    using var paint = new SkiaSharp.SKPaint { Style = SkiaSharp.SKPaintStyle.Stroke, StrokeWidth = 0.5f, Color = SkiaSharp.SKColors.Black, IsAntialias = true };
                    foreach (var x in lines)
                        canvas.DrawLine(x, top, x, bottom, paint);
                });
            }
            _placed.Clear();
            _y = bottom;
            _column = 0;
            _columnEmpty = true;
        }

        /// <summary>
        /// The region's content spread over its columns as evenly as it goes: the lowest column height at which
        /// everything still fits, keeping the order and the space between things. Columns of their own widths are
        /// balanced with their paragraphs' lines laid out anew for each column's width (BalanceSized).
        /// </summary>
        private List<Placed> Balance(List<Placed> placed)
        {
            if (_columns.Sizes is not null)
                return BalanceSized(placed);
            // The space above each thing in its column (none at a column's top). A thing that starts above the bottom of
            // the one before it is on that one's row (lines on both sides of an object) and stays beside it.
            var units = new List<(Placed Item, float Gap, bool Along)>();
            var rows = new Dictionary<int, (float Top, float Bottom)>();
            foreach (var item in placed)
            {
                var known = rows.TryGetValue(item.Column, out var row);
                var along = known && item.Y < row.Bottom - Epsilon;
                units.Add((item, along ? item.Y - row.Top : known ? Math.Max(0, item.Y - row.Bottom) : 0, along));
                rows[item.Column] = along ? (row.Top, Math.Max(row.Bottom, item.Y + item.Height)) : (item.Y, item.Y + item.Height);
            }
            var total = units.Where(u => !u.Along).Sum(u => u.Gap + u.Item.Height);
            var capacity = Bottom - _regionTop;
            List<Placed>? Try(float target)
            {
                var result = new List<Placed>(units.Count);
                var (column, y, rowTop, first) = (0, _regionTop, _regionTop, true);
                foreach (var (item, gap, along) in units)
                {
                    if (along && !first)
                    {
                        result.Add(item with { Column = column, Y = rowTop + gap });
                        y = Math.Max(y, rowTop + gap + item.Height);
                        continue;
                    }
                    var top = first ? y : y + gap;
                    if (!first && top + item.Height > _regionTop + target + Epsilon)
                    {
                        // Past the target height: on in the next column, the last one too high means the target is.
                        if (column + 1 == _columns.Count)
                            return null;
                        (column, top) = (column + 1, _regionTop);
                    }
                    result.Add(item with { Column = column, Y = top });
                    (y, rowTop) = (top + item.Height, top);
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

        /// <summary>
        /// Columns of their own widths balanced as Word balances a section's columns before a continuous section break:
        /// the lowest column height at which everything fits, a paragraph's lines laid out anew from the line that
        /// starts a column for that column's width (its rest too: a line in a wider or narrower column holds more or
        /// less). Tables, lines with a border and 한글's stored lines keep their width: they only go into columns at
        /// least as wide as the one they were laid out for. Content beside floating objects stays as filled.
        /// </summary>
        private List<Placed> BalanceSized(List<Placed> placed)
        {
            if (placed.Count == 0 || Current.Objects.Any(o => o.Wrap is "SQUARE" or "TOP_AND_BOTTOM"))
                return placed;
            // The space above each thing in its column (none at a column's top), as in Balance; a thing beside the one
            // before it (lines on both sides of an object) leaves the columns as filled.
            var units = new List<(Placed Item, float Gap)>();
            var bottoms = new Dictionary<int, float>();
            foreach (var item in placed)
            {
                var known = bottoms.TryGetValue(item.Column, out var bottom);
                if (known && item.Y < bottom - Epsilon)
                    return placed;
                units.Add((item, known ? Math.Max(0, item.Y - bottom) : 0));
                bottoms[item.Column] = item.Y + item.Height;
            }
            var count = _columns.Count;
            var widths = new float[count];
            for (var c = 0; c < count; c++)
                widths[c] = ColumnBox(c).Width;
            // A paragraph's lines laid out from a text position for a width, each once.
            var laid = new Dictionary<(HParagraph, int, float), IReadOnlyList<LineBox>>();
            IReadOnlyList<LineBox> Lines(HParagraph paragraph, ParagraphBox box, int from, float width)
            {
                if (!laid.TryGetValue((paragraph, from, width), out var lines))
                {
                    _guard.Paragraph();
                    // Lines from the paragraph's start get its number again (without counting it); later ones none.
                    lines = composer._layouter.Layout(paragraph, width, from == 0 ? box.Marker : null, FieldText, NoteText, from: from).Lines;
                    laid[(paragraph, from, width)] = lines;
                }
                return lines;
            }
            List<Placed>? Try(float target)
            {
                var result = new List<Placed>(units.Count);
                var (column, y, first) = (0, _regionTop, true);
                var limit = _regionTop + target + Epsilon;
                for (var u = 0; u < units.Count;)
                {
                    var (item, gap) = units[u];
                    if (item.Source is not { } source)
                    {
                        // Kept as laid out: into the next column when past the target, never into a narrower one.
                        var top = first ? y : y + gap;
                        if (!first && top + item.Height > limit)
                        {
                            if (column + 1 == count)
                                return null;
                            (column, top, first) = (column + 1, _regionTop, true);
                        }
                        if (widths[column] + Epsilon < widths[item.Column])
                            return null;
                        result.Add(item with { Column = column, Y = top });
                        (y, first) = (top + item.Height, false);
                        u++;
                        continue;
                    }
                    // The paragraph's lines in the region: from this line to its end (a balanced region ends where a
                    // paragraph starts), the units that are its lines passed over.
                    var (paragraph, box, line) = source;
                    var end = u + 1;
                    while (end < units.Count && units[end].Item.Source is { } next && ReferenceEquals(next.Paragraph, paragraph))
                        end++;
                    var lines = Lines(paragraph, box, line.Start, widths[column]);
                    var pending = gap;
                    for (var k = 0; k < lines.Count;)
                    {
                        var piece = lines[k];
                        var top = first ? y : y + pending;
                        if (!first && top + piece.Height > limit)
                        {
                            if (column + 1 == count)
                                return null;
                            (column, y, first, pending) = (column + 1, _regionTop, true, 0);
                            lines = Lines(paragraph, box, piece.Start, widths[column]);
                            k = 0;
                            continue;
                        }
                        result.Add(new Placed(column, 0, top, piece.Height, piece.Draw, null, (paragraph, box, piece)));
                        (y, first, pending) = (top + piece.Height, false, 0);
                        k++;
                    }
                    u = end;
                }
                return result;
            }
            // The first columns take the more (as in Balance): the lowest target at which everything fits.
            var total = units.Sum(u => u.Gap + u.Item.Height);
            float low = total / count / 2, high = Bottom - _regionTop;
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
                FloatParts();
            }
            else
            {
                NewPage();
            }
        }

        // ───────────────────────── Pages ─────────────────────────

        public ComposedDocument Compose()
        {
            // Text boxes laid out like characters stack their content through the table layout: set it up before the
            // first one, also in a document without tables.
            _ = Tables;
            for (var s = 0; s < document.Sections.Count; s++)
            {
                var section = document.Sections[s];
                if (_pages.Count > 0)
                    EndRegion(balance: false);
                _section = section;
                _lastStored = null;
                _geometry = _paper = Geometry.Of(section.Page ?? DefaultPage);
                _startsAt.Clear();
                foreach (var item in section.Headers.Concat(section.Footers))
                {
                    if (item.StartsAt is { } paragraph)
                        _startsAt.Add(paragraph);
                }
                if (section.StartPageNumber is { } start)
                    _number = start - 1;
                HangulPage(firstOfSection: true, section.Columns);
                Body(section.Blocks);
                // A floating table split across pages goes on onto pages of its own after the section's text.
                while (_floatRests.Count > 0)
                    NewPage();
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
            for (var i = 0; i < _pages.Count; i++)
            {
                var page = _pages[i];
                if (page.Turned is { } body)
                    page.Content.Add(VerticalText.Stand(body, VerticalText.Upright(page.Section.TextFlow)), Turn(page));
                PageBorder(page);
                MasterPages(page, lastOfSection: i == _pages.Count - 1 || _pages[i + 1].Section != page.Section);
                // Headers and footers first: what of them lies behind the text (a watermark) goes under the body's
                // objects behind the text too, as Word draws them.
                HeadersAndFooters(page);
                DrawObjects(page);
                // A page border in front of the text (Word's default) goes over everything on the page.
                PageBorder(page, front: true);
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
            var page = new Page(new PageContent(_paper.Width, _paper.Height), _section!, _paper, _number, firstOfSection)
            {
                Turned = Vertical(_section!) ? new PageContent(_paper.Height, _paper.Width) : null,
            };
            _pages.Add(page);
            Dress(page, _pages.Count - 1);
            _y = _geometry.BodyTop;
            _empty = true;
            _inStep = true;
            StartRegion(keep);
            FloatParts();
        }

        private Page Current => _pages[^1];

        /// <summary>Whether the section's body is written vertically (and laid out turned).</summary>
        private static bool Vertical(HSection section) => section.TextFlow is HTextFlow.Vertical or HTextFlow.Stacked or HTextFlow.Up;

        /// <summary>The matrix that turns a vertical page's body (Page.Turned) into place on the paper.</summary>
        private static SKMatrix Turn(Page page) =>
            VerticalText.Place(VerticalText.Upward(page.Section.TextFlow), new SKRect(0, 0, page.Content.Width, page.Content.Height));

        // ───────────────────────── Body ─────────────────────────

        private TableLayout? _tables;

        /// <summary>Tables and paragraph lists inside cells, headers and footers, laid out with this run's numbering and fields.</summary>
        private TableLayout Tables => _tables ??= CreateTables();

        private TableLayout CreateTables()
        {
            // Paragraphs in cells, headers, footers and text boxes check the time and cancellation as the body's do: a
            // single table can hold most of a document.
            var tables = new TableLayout((p, width) =>
            {
                _guard.Paragraph();
                return Layout(composer.Resolve(p), width);
            }, composer._styles is not null, () => Frame(_y), p =>
            {
                // Word's autofit tables measure their cells' paragraphs first; a list item is measured with the number it
                // will get, without counting it.
                _guard.Paragraph();
                var resolved = composer.Resolve(p);
                var marker = resolved.List is { Numbered: true } list ? _numbers.Peek(list) : null;
                return composer._layouter.ContentWidths(resolved, marker, FieldText, NoteText);
            })
            {
                WordCells = composer._styles is not null,
                CollapseSpacing = composer._collapseSpacing,
            };
            // Text boxes laid out like characters stack their content as cells do.
            composer._layouter.Stack = tables.Stack;
            composer._layouter.Guard = _guard;
            return tables;
        }

        private void Body(List<HBlock> blocks)
        {
            object? next = null;
            HParagraph? holder = null;
            _box = null;
            for (var i = 0; i < blocks.Count; i++)
            {
                _guard.Paragraph();
                if (blocks[i] is HTable table)
                {
                    // In columns of their own widths the table may go on in a column of another width and be laid out
                    // again: its list numbers then count from where they were before it.
                    var numbers = next is null && _columns.Sizes is not null ? _numbers.Save() : null;
                    var tableBox = next as TableBox ?? Tables.Layout(table, ColumnWidth);
                    next = null;
                    if (holder is null && table.Float is { } anchor && Float(table, anchor, tableBox, blocks, i, ref next))
                        continue;
                    PlaceTable(table, tableBox, holder, holder is null ? TableLayout.Follower(blocks, i) : null, numbers);
                    holder = null;
                    continue;
                }
                if (blocks[i] is not HParagraph source)
                    continue;
                if (_startsAt.Contains(source))
                    StartsHere(source);
                // 한글 keeps a table inside a paragraph, which the model splits around it. With no text before the
                // table (only objects floating at it), 한글's line is the table's: the part takes no line of its own,
                // and the table takes its spacing and stored place.
                if (TableLayout.HoldsTable(blocks, i))
                {
                    holder = composer.Resolve(source);
                    continue;
                }
                var paragraph = composer.Resolve(source, i + 1 < blocks.Count ? blocks[i + 1] : null);
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
                    _columnBroken = true;
                    // 한글's own column break: its stored places, measured from the column's top, hold again.
                    NextColumn();
                    _hangulStart = (_pages.Count, _column);
                    _inStep = true;
                }
                var box = next as ParagraphBox ?? Layout(paragraph, ColumnWidth);
                next = null;
                if (!box.StoredLines && Wrapped(paragraph, box) is { } wrapped)
                    box = wrapped;

                // A border: one box with the paragraphs before and after it that have the same, room for its lines.
                var (opens, closes) = (true, true);
                if (box.Border is { } border)
                {
                    opens = !(_box is (var before, var index) && index == i - 1 && before.Joins(border));
                    if (i + 1 < blocks.Count && blocks[i + 1] is HParagraph after && !TableLayout.HoldsTable(blocks, i + 1) && after.ColumnsChange is null)
                    {
                        var afterBox = Layout(composer.Resolve(after, i + 2 < blocks.Count ? blocks[i + 2] : null), ColumnWidth);
                        next = afterBox;
                        closes = !border.Joins(afterBox.Border);
                    }
                    box = border.Pad(box, opens, closes);
                }

                // Keep with next: this paragraph and the start of the next block in one column.
                if (paragraph.Format.KeepWithNext == true && !_columnEmpty && i + 1 < blocks.Count && !(box.StoredLines && _inStep))
                {
                    float start;
                    switch (blocks[i + 1])
                    {
                        case HParagraph following:
                            var nextBox = next as ParagraphBox ?? Layout(composer.Resolve(following, i + 2 < blocks.Count ? blocks[i + 2] : null), ColumnWidth);
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

                Place(paragraph, box, opens, closes);
                _box = box.Border is { } drawn ? (drawn, i) : null;
                if (!box.StoredLines)
                    _inStep = false;
            }
        }

        /// <summary>The border of the last paragraph placed and its index in the blocks, while it is the last block placed.</summary>
        private (BoxBorder Border, int Index)? _box;

        /// <summary>The page, column and bottom of the last line placed that drew a border.</summary>
        private (int Page, int Column, float Bottom)? _boxBottom;

        /// <summary>
        /// Puts a table's rows into columns and pages from the current position on, repeating its header rows;
        /// <paramref name="holder"/> is the 한글 paragraph whose line the table is, or null.
        /// </summary>
        /// <param name="follower">The rest of the 한글 paragraph the table starts, whose text follows right under it.</param>
        /// <param name="numbers">
        /// The list counters before the table was laid out: in columns of their own widths, a table going on in a column
        /// of another width is laid out again for it from there (its rows from the next group on). Null keeps its widths.
        /// </param>
        private void PlaceTable(HTable table, TableBox box, HParagraph? holder, HParagraph? follower, IReadOnlyDictionary<HNumbering, int[]>? numbers = null)
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
            // A table of another format has the outer margin the HWPX writer gives it: 5 mm below unless it is tight. A
            // Word table (sized by Word's rules) has none: Word puts the next paragraph right under it.
            var margin = table.OuterMargin ?? (composer._styles is not null && !table.Tight && table.Sizing is null ? new HInsets(0, 0, 0, 1417) : default);
            _y += (table.VerticalOffset + margin.Top) / 100f;
            // Filee's rows are not 한글's unless the table is as high as 한글's (its line for it, else its stored size);
            // else the stored places hold again on 한글's next page.
            _inStep = _inStep && TableLayout.AsStored(table, box.Height, line);
            // A table is centred or set right with its outer margins (한글 바깥 여백), as one set as a character is, like
            // a character that wide; one placed at its paragraph (not as a character) too, as tables in cells already are.
            var shift = (!table.RightToLeft
                ? table.Align switch
                {
                    HAlign.Center => (margin.Left - margin.Right) / 2f,
                    HAlign.Right => -margin.Right,
                    _ => margin.Left,
                }
                : margin.Left) / 100f;
            var x = box.Offset + shift;
            var header = box.Rows.Take(box.HeaderRows).ToList();
            // A column of another width than the one the table was laid out for (columns of their own widths): the
            // table is laid out again for it, its list numbers counted from where they were before it.
            bool Refit()
            {
                if (numbers is null || _columns.Sizes is null || Math.Abs(box.Column - ColumnWidth) < Epsilon)
                    return false;
                _numbers.Restore(numbers);
                box = Tables.Layout(table, ColumnWidth);
                x = box.Offset + shift;
                header = [.. box.Rows.Take(box.HeaderRows)];
                return true;
            }
            // A table that must not split goes to the next column whole when it fits in one.
            if (box.PageBreak == HTablePageBreak.None && !_columnEmpty && _y + box.Height > Bottom + Epsilon && box.Height <= ColumnHeight)
            {
                NextColumn();
                Refit();
            }

            // The column holds nothing yet but, perhaps, this table's repeated header rows.
            var fresh = _columnEmpty;
            void Draw(IReadOnlyList<RowBox> rows)
            {
                var height = rows.Sum(r => r.Height);
                Put(x, height, (page, left, top) => TableLayout.Draw(page, rows, left, top));
                _y += height;
            }
            // On in the next column or page: true when the table was laid out again for it.
            bool NextPage(bool repeat)
            {
                NextColumn();
                _inStep = false;
                var refitted = Refit();
                if (repeat && header.Count > 0)
                    Draw(header);
                fresh = true;
                return refitted;
            }

            // The groups come from the table itself (cells spanning rows), so laid out again it has the same ones.
            foreach (var (first, count) in box.Groups())
            {
                IReadOnlyList<RowBox> rows = [.. box.Rows.Skip(first).Take(count)];
                var repeat = first >= header.Count;
                while (true)
                {
                    _guard.Paragraph();
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
                        // The rest of a split row keeps the widths its first part had.
                        Draw([part]);
                        NextPage(repeat);
                        rows = [rest];
                        continue;
                    }
                    if (!fresh)
                    {
                        if (NextPage(repeat))
                            rows = [.. box.Rows.Skip(first).Take(count)];
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
            // Notes referenced inside the table's cells go to the foot of the page the table ends on. All notes numbered
            // before stay placed, so only those numbered since the last table are looked at.
            for (; _numberedChecked < _numbered.Count; _numberedChecked++)
            {
                if (!_notesPlaced.Contains(_numbered[_numberedChecked]))
                    AddNote(_numbered[_numberedChecked]);
            }
        }

        private ParagraphBox Layout(HParagraph paragraph, float width, string? marker = null, Func<float, float, LineRoom>? wrap = null,
            string? noteNumber = null, int from = 0)
        {
            marker ??= paragraph.List is { Numbered: true } list ? _numbers.Next(list) : null;
            return composer._layouter.Layout(paragraph, width, marker, FieldText, NoteText, wrap, noteNumber, from);
        }

        /// <summary>
        /// The paragraph's lines from line <paramref name="from"/> on laid out anew for the current column, when it is
        /// one of columns of their own widths and not as wide as the column they were laid out for; null when they stay.
        /// A paragraph with a border goes on with the lines it has (its box is cut where they were laid out).
        /// </summary>
        private ParagraphBox? Refit(HParagraph paragraph, ParagraphBox box, int from, bool opens, bool closes)
        {
            if (_columns.Sizes is null || box.StoredLines || box.Width <= 0 || Math.Abs(box.Width - ColumnWidth) < Epsilon
                || from >= box.Lines.Count || from > 0 && (box.Border is not null || box.Lines[from].Start <= box.Lines[from - 1].Start))
                return null;
            var fresh = Layout(paragraph, ColumnWidth, box.Marker, from: box.Lines[from].Start);
            if (from == 0 && box.Border is { } border)
                fresh = border.Pad(fresh, opens, closes);
            return fresh.Lines.Count == 0 ? null : fresh;
        }

        /// <summary>Paragraph boxes laid out with their lines beside objects (Wrapped, Rewrap).</summary>
        private readonly HashSet<ParagraphBox> _wrapped = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// The paragraph's lines from line <paramref name="from"/> on laid out anew in the column it just went on to,
        /// when they were laid out beside objects in the column before or objects text goes around are in the way here
        /// (a split floating table going on at the column's top): beside those, else across the column. Null when they
        /// stay: 한글's stored lines, a paragraph with a border, one starting here with objects of its own to place.
        /// </summary>
        private ParagraphBox? Rewrap(HParagraph paragraph, ParagraphBox box, int from)
        {
            if (box.StoredLines || box.Border is not null || from >= box.Lines.Count
                || from == 0 && FloatingObjects.Of(paragraph).Any(o => o.Anchor.Wrap is "SQUARE" or "TOP_AND_BOTTOM"))
                return null;
            var top = _y;
            var objects = InTheWay();
            var beside = objects.Any(o => o.Wrap == "SQUARE" && o.Bounds.Bottom > top + Epsilon);
            if (!beside && !_wrapped.Contains(box))
                return null;
            var left = ColumnEdges.Left;
            LineRoom Beside(float y, float height)
            {
                var (at, spans) = Room(objects, top + y, height);
                return new LineRoom(at - top, spans?.ConvertAll(s => (s.Start - left, s.End - left)));
            }
            var fresh = Layout(paragraph, ColumnWidth, box.Marker, beside ? Beside : null, from: box.Lines[from].Start);
            if (fresh.Lines.Count == 0)
                return null;
            if (beside)
                _wrapped.Add(fresh);
            return fresh;
        }

        /// <summary>Puts a paragraph's lines into columns and pages from the current position on.</summary>
        /// <param name="opens">The paragraph starts its border's box (see BoxBorder).</param>
        /// <param name="closes">The paragraph ends its border's box.</param>
        private void Place(HParagraph paragraph, ParagraphBox box, bool opens = true, bool closes = true)
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
            // Lines laid out anew from a line on, for a column of another width, go on from the paragraph's middle.
            var resumed = false;
            // The paragraph went on to the next column: its rest may need laying out anew around the objects there.
            var moved = false;
            for (var i = 0; i < lines.Count;)
            {
                _guard.Paragraph();
                if (Refit(paragraph, box, i, opens, closes) is { } refit)
                {
                    resumed |= i > 0;
                    (box, lines, i) = (refit, refit.Lines, 0);
                }
                else if (moved && Rewrap(paragraph, box, i) is { } rewrapped)
                {
                    resumed |= i > 0;
                    (box, lines, i) = (rewrapped, rewrapped.Lines, 0);
                }
                moved = false;
                var stored = box.StoredLines && _inStep;
                if (stored && lines[i].StoredPageStart && !_empty)
                    HangulPage();
                else if (stored && lines[i].StoredColumnStart && !_columnEmpty && _columns.Count > 1)
                    NextColumn();
                // How many of the remaining lines fit in this column, with the footnotes they bring (their heights summed
                // on as they come, after the page's own).
                var fit = 0;
                var y = _y;
                var rowTop = _y;
                double? noteSum = null;
                var noteCount = 0;
                for (var j = i; j < lines.Count; j++)
                {
                    if (((j - i) & 1023) == 1023)
                        _guard.Paragraph();
                    if (j > i && stored && (lines[j].StoredPageStart || lines[j].StoredColumnStart && _columns.Count > 1))
                        break;
                    // 한글's line on both sides of an object is two stored lines at one place: the second goes beside the first.
                    // A line laid out anew beside an object goes on its row, the row as high as its highest line.
                    var along = j > i && lines[j].Beside;
                    var top = along ? rowTop
                        : box.StoredLines && j > i && SameRow(lines, j) ? y - lines[j - 1].Height
                        : stored && lines[j].StoredTop is { } storedTop ? Math.Max(y, _regionTop + storedTop) : stored ? y : Clear(y, lines[j].Height);
                    var fresh = NewNotes(lines[j]).ToList();
                    noteSum ??= FootnotesSum();
                    foreach (var note in fresh)
                    {
                        noteSum += NoteLayout(note).Height;
                        noteCount++;
                    }
                    if (top + lines[j].Height > _geometry.BodyBottom - FootnotesHeight(noteSum.Value, noteCount) + Epsilon)
                        break;
                    y = along ? Math.Max(y, top + lines[j].Height) : top + lines[j].Height;
                    rowTop = top;
                    fit++;
                }
                if (widowOrphan && !stored && fit < lines.Count - i)
                {
                    // At least two lines of the paragraph in this column (or none) and two in the next.
                    if (i == 0 && !resumed && fit < MinimumLines && !_columnEmpty)
                        fit = 0;
                    else if (lines.Count - i - fit < MinimumLines && fit > MinimumLines - 1 + (i == 0 && !resumed ? 1 : 0))
                        fit -= MinimumLines - (lines.Count - i - fit);
                }
                // A row of lines beside an object stays in one column.
                while (fit > 0 && i + fit < lines.Count && lines[i + fit].Beside)
                    fit--;
                if (fit == 0)
                {
                    if (!_columnEmpty)
                    {
                        // Objects placed ahead with the paragraph go on with it.
                        if (i == 0 && !resumed && _anchored.Remove(paragraph, out var ahead))
                        {
                            Current.Objects.RemoveAll(ahead.Contains);
                            foreach (var item in ahead)
                                _floated.Remove(item.Object);
                        }
                        NextColumn();
                        _inStep = false;
                        moved = true;
                        continue;
                    }
                    fit = 1; // a line higher than the column: placed anyway
                }
                for (var j = i; j < i + fit; j++)
                {
                    if (box.StoredLines && j > i && SameRow(lines, j))
                        _y -= lines[j - 1].Height;
                    PlaceLine(paragraph, box, lines[j], j == 0 && !resumed, stored, j == lines.Count - 1, opens, closes);
                }
                i += fit;
                if (i < lines.Count)
                {
                    var stays = lines[i].StoredPageStart || lines[i].StoredColumnStart;
                    if (!(stored && stays))
                    {
                        NextColumn();
                        moved = true;
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

        /// <summary>The page, column, top and bottom of the last row of lines placed (see LineBox.Beside).</summary>
        private (int Page, int Column, float Top, float Bottom) _row;

        private void PlaceLine(HParagraph paragraph, ParagraphBox box, LineBox line, bool first, bool stored, bool last = false, bool opens = true, bool closes = true)
        {
            if (line.StoredTop is { } storedTop)
                _lastStored = storedTop;
            var along = line.Beside && _row.Page == _pages.Count && _row.Column == _column && !_columnEmpty;
            if (along)
                _y = _row.Top;
            else if (stored && line.StoredTop is { } top)
                _y = Math.Max(_y, _regionTop + top);
            else if (!stored)
                _y = Clear(_y, line.Height);
            if (first && paragraph.HeadingLevel > 0)
                _outline.Add(new OutlineEntry(Title(paragraph), _pages.Count - 1, Current.Turned is null ? _y : Current.Geometry.BodyTop, paragraph.HeadingLevel - 1));
            // An object placed against the paper or the page goes on the page of the line that holds it, as 한글 places
            // it; the paragraph's first line places the others.
            if (first && !_anchored.Remove(paragraph))
                Anchor(paragraph, _y, (item, anchor) => !OnPage(anchor) || !box.FloatingAt.TryGetValue(item, out var at) || at < line.End || box.Lines.Count == 1);
            else if (!first)
                Anchor(paragraph, _y, objects: LineObjects(paragraph, box, line));
            if (first && paragraph.PageHiding is { } hiding)
                Current.Hiding = hiding;
            // A new page number in the middle of a section: this page has it, the next ones count on from it.
            if (first && paragraph.NewPageNumber is { } number)
            {
                _number = number;
                _pages[^1] = Current with { Number = number };
            }
            Action<PageContent, float, float, bool>? under = null;
            if (box.Border is { } border)
            {
                // The background goes on through the spacing above a paragraph that continues the box in this column.
                var gap = first && !opens && _boxBottom is (var page, var column, var bottom) && page == _pages.Count && column == _column ? Math.Max(0, _y - bottom) : 0;
                var slice = border.Slice(box, first, last, opens, closes, gap);
                var height = line.Height;
                under = (content, x, top, columnTop) => content.Draw(canvas => slice.Draw(canvas, x, top, height, columnTop));
            }
            Put(0, line.Height, line.Draw, under, box.Border is null && !box.StoredLines && !line.Beside ? (paragraph, box, line) : null);
            _row = along ? _row with { Bottom = Math.Max(_row.Bottom, _y + line.Height) } : (_pages.Count, _column, _y, _y + line.Height);
            _y = _row.Bottom;
            if (box.Border is not null)
                _boxBottom = (_pages.Count, _column, _y);
            foreach (var note in line.Notes)
                AddNote(note);
        }

        // ───────────────────────── Floating objects ─────────────────────────

        private int _objects;

        /// <summary>Paragraphs whose objects were placed before their lines were laid out (to wrap the lines around them).</summary>
        private readonly Dictionary<HParagraph, List<PlacedObject>> _anchored = new(ReferenceEqualityComparer.Instance);

        /// <summary>The column's left and right edge.</summary>
        private (float Left, float Right) ColumnEdges => (ColumnLeft(_column), ColumnLeft(_column) + ColumnWidth);

        /// <summary>
        /// The objects on the page text goes around (beside them or above and below) that reach into the column, by the
        /// top of the room they keep (see PlacedObject.Bounds).
        /// </summary>
        private List<PlacedObject> InTheWay()
        {
            var (left, right) = ColumnEdges;
            return [.. Current.Objects.Where(o => o.Wrap is "SQUARE" or "TOP_AND_BOTTOM" && o.Bounds.Right > left + Epsilon && o.Bounds.Left < right - Epsilon)
                .OrderBy(o => o.Bounds.Top)];
        }

        /// <summary>
        /// Where a line laid out anew from <paramref name="top"/> goes: below the objects text goes above and below, and
        /// below those beside it where they leave it no stretch wide enough (see Room).
        /// </summary>
        private float Clear(float top, float height) => Current.Objects.Count == 0 ? top : Room(InTheWay(), top, height).Top;

        /// <summary>The narrowest stretch beside an object a line's text goes in, in points; narrower ones stay empty.</summary>
        private const float NarrowestSpan = 18;

        /// <summary>
        /// Where a line laid out anew from <paramref name="top"/> goes and the stretches of the page across (left to right)
        /// its text goes in there, between the column's edges and beside the objects text goes around; null stretches
        /// for the whole column. The line goes below the objects text goes above and below, and further down past
        /// objects beside it until a stretch is at least <see cref="NarrowestSpan"/> wide.
        /// </summary>
        /// <param name="objects">The objects in the way (InTheWay).</param>
        private (float Top, List<(float Start, float End)>? Spans) Room(List<PlacedObject> objects, float top, float height)
        {
            if (objects.Count == 0)
                return (top, null);
            var (left, right) = ColumnEdges;
            // Each round moves the line below one more object at least.
            for (var round = 1; ; round++)
            {
                if (round % 64 == 0)
                    _guard.Paragraph();
                top = AboveAndBelow(objects, top, height);
                var spans = FloatingObjects.Beside(objects, left, right, top, height, out var below);
                if (spans is null)
                    return (top, null);
                spans.RemoveAll(s => s.End - s.Start < NarrowestSpan);
                if (spans.Count > 0 || below <= top + Epsilon)
                    return (top, spans.Count > 0 ? spans : null);
                top = below;
            }
        }

        /// <summary>Where a line from <paramref name="top"/> goes below the objects text goes above and below.</summary>
        private static float AboveAndBelow(List<PlacedObject> objects, float top, float height)
        {
            // From the top down, so one pass moves the line below all of them; the next only confirms it.
            var moved = true;
            while (moved)
            {
                moved = false;
                foreach (var item in objects)
                {
                    if (item.Bounds.Top >= top + height - Epsilon)
                        break;
                    if (item.Wrap == "TOP_AND_BOTTOM" && item.Bounds.Bottom > top + Epsilon)
                    {
                        top = item.Bounds.Bottom;
                        moved = true;
                    }
                }
            }
            return top;
        }

        /// <summary>
        /// A paragraph laid out anew again with its lines beside the objects text goes around (see Room), its own
        /// objects placed first; null when none is beside it.
        /// </summary>
        private ParagraphBox? Wrapped(HParagraph paragraph, ParagraphBox box)
        {
            var top = _y + (_columnEmpty || _skipSpaceBefore ? 0 : box.SpaceBefore);
            if (FloatingObjects.Of(paragraph).Any(o => o.Anchor.Wrap is "SQUARE" or "TOP_AND_BOTTOM"))
                _anchored[paragraph] = Anchor(paragraph, top);
            var objects = InTheWay();
            if (!objects.Any(o => o.Wrap == "SQUARE" && o.Bounds.Bottom > top + Epsilon))
                return null;
            var left = ColumnEdges.Left;
            // Lines go below the objects text goes above and below first (as they will be placed), and then beside.
            LineRoom Beside(float y, float height)
            {
                var (at, spans) = Room(objects, top + y, height);
                return new LineRoom(at - top, spans?.ConvertAll(s => (s.Start - left, s.End - left)));
            }
            var wrapped = Layout(paragraph, ColumnWidth, box.Marker, Beside);
            _wrapped.Add(wrapped);
            return wrapped;
        }

        /// <summary>The page's areas for anchors, with the current column; a paragraph at <paramref name="top"/> as wide as the column.</summary>
        private AnchorFrame Frame(float top, HParagraph? paragraph = null)
        {
            var g = _geometry;
            var (left, width) = (ColumnLeft(_column), ColumnWidth);
            return new AnchorFrame(
                new SKRect(0, 0, g.Width, g.Height),
                new SKRect(g.Left, g.MarginTop, g.Left + g.TextWidth, g.MarginBottom),
                new SKRect(left, g.MarginTop, left + width, g.MarginBottom),
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
        /// <param name="objects">The paragraph's objects to look at; null for all of them.</param>
        private List<PlacedObject> Anchor(HParagraph paragraph, float top, Func<HInline, HAnchor, bool>? include = null,
            IEnumerable<(HInline Object, HAnchor Anchor)>? objects = null)
        {
            var frame = Frame(top, paragraph);
            var placed = new List<PlacedObject>();
            foreach (var (item, anchor) in objects ?? FloatingObjects.Of(paragraph))
            {
                if (_floated.Contains(item) || include?.Invoke(item, anchor) == false)
                    continue;
                if (FloatingObjects.Size(item) is not var (w, h))
                    continue;
                _floated.Add(item);
                placed.Add(FloatingObjects.Placed(item, anchor, FloatingObjects.Place(anchor, w, h, frame), _objects++));
            }
            Current.Objects.AddRange(placed);
            return placed;
        }

        /// <summary>The objects placed against the paper or the page held in a paragraph box's text, by their position.</summary>
        private (HParagraph? Paragraph, ParagraphBox? Box, List<(int At, HInline Object, HAnchor Anchor)> Objects) _lineObjects = (null, null, []);

        /// <summary>
        /// The objects a line after the first places: those against the paper or the page held in its text (the last
        /// line also those after it). Found by position, so a paragraph of many lines isn't looked through for each.
        /// </summary>
        private List<(HInline, HAnchor)> LineObjects(HParagraph paragraph, ParagraphBox box, LineBox line)
        {
            if (!ReferenceEquals(_lineObjects.Paragraph, paragraph) || !ReferenceEquals(_lineObjects.Box, box))
            {
                var held = new List<(int At, HInline Object, HAnchor Anchor)>();
                foreach (var (item, anchor) in FloatingObjects.Of(paragraph))
                {
                    if (OnPage(anchor) && box.FloatingAt.TryGetValue(item, out var at))
                        held.Add((at, item, anchor));
                }
                // Objects come in text order, so this keeps their order.
                _lineObjects = (paragraph, box, [.. held.OrderBy(o => o.At)]);
            }
            var objects = _lineObjects.Objects;
            var (low, high) = (0, objects.Count);
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (objects[middle].At < line.Start)
                    low = middle + 1;
                else
                    high = middle;
            }
            var last = line == box.Lines[^1];
            var result = new List<(HInline, HAnchor)>();
            for (var k = low; k < objects.Count && (objects[k].At < line.End || last); k++)
                result.Add((objects[k].Object, objects[k].Anchor));
            return result;
        }

        /// <summary>The rows of a Word floating table (all, or its part on one page), placed as a floating object.</summary>
        private sealed class FloatingTable(IReadOnlyList<RowBox> rows) : HObject
        {
            public IReadOnlyList<RowBox> Rows { get; } = rows;
        }

        /// <summary>A Word floating table split across pages: the rows still to place, from the next column's top (FloatParts).</summary>
        private sealed class FloatRest(HTable table, HAnchor anchor, TableBox box)
        {
            public HTable Table { get; } = table;
            public HAnchor Anchor { get; } = anchor;
            public TableBox Box { get; } = box;

            /// <summary>The row groups (rows bound by cells spanning them) and the first row of each.</summary>
            public List<(int First, IReadOnlyList<RowBox> Rows)> Groups { get; } =
                [.. box.Groups().Select(g => (g.First, (IReadOnlyList<RowBox>)[.. box.Rows.Skip(g.First).Take(g.Count)]))];

            /// <summary>The next group to place.</summary>
            public int Next { get; set; }

            /// <summary>The rest of a row split at the bottom of the last part, placed before the next group.</summary>
            public RowBox? Carry { get; set; }

            /// <summary>Across: from the column's left edge (anchored to the column) or on the page.</summary>
            public float X { get; init; }
            public bool FromColumn { get; init; }

            public bool Done => Carry is null && Next >= Groups.Count;
        }

        /// <summary>The floating tables going on at the top of the next column.</summary>
        private readonly List<FloatRest> _floatRests = [];

        /// <summary>
        /// Places a Word floating table where its anchor puts it: down from the top of the paragraph after it (laid out
        /// ahead into <paramref name="next"/>, so it keeps its list number), never above the text before it, or against
        /// the page. Text after it goes around it (as around a picture wrapped square). With w:tblOverlap never it goes
        /// down below the floating tables it would overlap. One that moves with the text and would run past the
        /// column's bottom goes to the next column with its paragraph when it fits in one; a longer one is split as
        /// Word splits it: the rows that fit stay, the rest goes on at the top of the next columns (FloatParts), its
        /// header rows repeated, and the text there goes around it too. False for a table against the page taller than a
        /// column, which stays in the flow (split by its rows).
        /// </summary>
        private bool Float(HTable table, HAnchor anchor, TableBox box, List<HBlock> blocks, int index, ref object? next)
        {
            var (width, height) = (box.Width, box.Height);
            var withText = anchor.VerticalRelativeTo == "PARA";
            if (width <= 0 || !withText && height > ColumnHeight + Epsilon)
                return false;
            HParagraph? paragraph = null;
            ParagraphBox? following = null;
            if (index + 1 < blocks.Count && blocks[index + 1] is HParagraph after && !TableLayout.HoldsTable(blocks, index + 1))
            {
                paragraph = composer.Resolve(after, index + 2 < blocks.Count ? blocks[index + 2] : null);
                following = Layout(paragraph, ColumnWidth);
                next = following;
            }
            SKRect area;
            while (true)
            {
                var top = _y + (_columnEmpty || following is null ? 0 : following.SpaceBefore);
                area = FloatingObjects.Place(anchor, width, height, Frame(top, paragraph));
                if (withText)
                {
                    var y = Math.Max(_y, top + anchor.VerticalOffset / 100f);
                    area = new SKRect(area.Left, y, area.Right, y + height);
                }
                if (!anchor.AllowOverlap)
                    area = Unlapped(area);
                if (!withText || _columnEmpty || area.Bottom <= Bottom + Epsilon || height > ColumnHeight + Epsilon)
                    break;
                NextColumn();
                _inStep = false;
            }
            var rest = new FloatRest(table, anchor, box)
            {
                X = anchor.HorizontalRelativeTo is "COLUMN" or "PARA" ? area.Left - ColumnLeft(_column) : area.Left,
                FromColumn = anchor.HorizontalRelativeTo is "COLUMN" or "PARA",
            };
            if (!withText || area.Bottom <= Bottom + Epsilon)
            {
                Current.Objects.Add(FloatingObjects.Placed(new FloatingTable(box.Rows) { OuterMargin = table.FloatDistance }, anchor, area, _objects++));
                rest.Next = rest.Groups.Count;
            }
            else
            {
                FloatPart(rest, area.Left, area.Top, "SQUARE");
            }
            if (!rest.Done)
                _floatRests.Add(rest);
            for (; _numberedChecked < _numbered.Count; _numberedChecked++)
            {
                if (!_notesPlaced.Contains(_numbered[_numberedChecked]))
                    AddNote(_numbered[_numberedChecked]);
            }
            return true;
        }

        /// <summary>
        /// A floating table's area moved down below the floating tables on the page it would overlap (w:tblOverlap
        /// never), with their distance from the text.
        /// </summary>
        private SKRect Unlapped(SKRect area)
        {
            for (var round = 0; round < Current.Objects.Count; round++)
            {
                var inTheWay = Current.Objects.FirstOrDefault(o => o.Object is FloatingTable && o.Bounds.Left < area.Right - Epsilon && o.Bounds.Right > area.Left + Epsilon
                    && o.Bounds.Top < area.Bottom - Epsilon && o.Bounds.Bottom > area.Top + Epsilon);
                if (inTheWay is null)
                    break;
                area.Offset(0, inTheWay.Bounds.Bottom - area.Top);
            }
            return area;
        }

        /// <summary>
        /// Puts the rows of a split floating table that fit from <paramref name="top"/> down to the column's bottom into
        /// one floating part at <paramref name="left"/>: whole groups, a row that may split cut at the bottom, at least
        /// one group (one taller than a column is placed as it is). A part after the first repeats the header rows.
        /// </summary>
        private void FloatPart(FloatRest rest, float left, float top, string wrap)
        {
            var box = rest.Box;
            var header = rest.Next > 0 || rest.Carry is not null ? box.Rows.Take(box.HeaderRows).ToList() : [];
            if (rest.Next < rest.Groups.Count && rest.Groups[rest.Next].First < box.HeaderRows)
                header.Clear();
            var rows = new List<RowBox>(header);
            var height = header.Sum(r => r.Height);
            while (!rest.Done)
            {
                _guard.Paragraph();
                IReadOnlyList<RowBox> group = rest.Carry is { } carried ? [carried] : rest.Groups[rest.Next].Rows;
                var needed = group.Sum(r => r.Height);
                var room = Bottom - top - height;
                var empty = rows.Count == header.Count;
                if (needed <= room + Epsilon || empty && room <= 0)
                {
                    rows.AddRange(group);
                    height += needed;
                    Advance();
                    if (needed > room + Epsilon)
                        break;
                    continue;
                }
                // A row splits inside its cells where it may (as in PlaceTable), or anyway in an empty part.
                var splits = group.Count == 1 && (!group[0].CantSplit && box.PageBreak is not (HTablePageBreak.Cell or HTablePageBreak.None) || empty);
                if (splits && TableLayout.Split(group[0], room) is var (part, remaining))
                {
                    rows.Add(part);
                    height += part.Height;
                    Advance();
                    rest.Carry = remaining;
                    break;
                }
                if (empty)
                {
                    // Taller than the column and it can't split: placed as it is.
                    rows.AddRange(group);
                    height += needed;
                    Advance();
                }
                break;
            }
            var anchor = rest.Anchor with { Wrap = wrap };
            var area = new SKRect(left, top, left + box.Width, top + height);
            Current.Objects.Add(FloatingObjects.Placed(new FloatingTable(rows) { OuterMargin = rest.Table.FloatDistance }, anchor, area, _objects++));

            void Advance()
            {
                if (rest.Carry is not null)
                    rest.Carry = null;
                else
                    rest.Next++;
            }
        }

        /// <summary>
        /// The floating tables split across pages go on at the top of a new column, one under the other, the text there
        /// going around them (a paragraph going on from the column before is laid out anew for it: Rewrap).
        /// </summary>
        private void FloatParts()
        {
            if (_floatRests.Count == 0)
                return;
            var rests = _floatRests.ToList();
            _floatRests.Clear();
            var top = _y;
            foreach (var rest in rests)
            {
                FloatPart(rest, rest.FromColumn ? ColumnLeft(_column) + rest.X : rest.X, top, "SQUARE");
                top = Current.Objects[^1].Area.Bottom;
                if (!rest.Done)
                    _floatRests.Add(rest);
            }
            // The column holds the parts: text that finds no room beside them goes on to the next column, where the
            // tables go on in turn.
            _empty = _columnEmpty = false;
        }

        /// <summary>Draws a page's floating objects, behind or in front of its text, lower z-order first.</summary>
        private void DrawObjects(Page page)
        {
            _geometry = page.Geometry;
            _number = page.Number;
            foreach (var item in page.Objects.OrderBy(o => o.Order).ThenBy(o => o.Sequence))
            {
                var layer = new PageContent(page.Content.Width, page.Content.Height);
                if (item.Object is FloatingTable table)
                {
                    if (page.Turned is { } body)
                    {
                        // A floating table of a vertical page turns with its text.
                        var turned = new PageContent(body.Width, body.Height);
                        TableLayout.Draw(turned, table.Rows, item.Area.Left, item.Area.Top);
                        layer.Add(VerticalText.Stand(turned, VerticalText.Upright(page.Section.TextFlow)), Turn(page));
                    }
                    else
                    {
                        TableLayout.Draw(layer, table.Rows, item.Area.Left, item.Area.Top);
                    }
                }
                else
                {
                    FloatingObjects.Draw(layer, item.Object, page.Turned is null ? item.Area : Upright(item.Area, Turn(page)),
                        (blocks, width) => Tables.Stack(blocks, width));
                }
                page.Content.Add(layer, item.Behind);
            }
        }

        /// <summary>
        /// The place on the paper of an object a vertical page's body placed at <paramref name="area"/>: its own size,
        /// standing upright, centred where the turned area's middle is.
        /// </summary>
        private static SKRect Upright(SKRect area, SKMatrix turn)
        {
            var middle = turn.MapPoint(area.MidX, area.MidY);
            return SKRect.Create(middle.X - area.Width / 2, middle.Y - area.Height / 2, area.Width, area.Height);
        }

        // ───────────────────────── Notes ─────────────────────────

        private readonly Dictionary<HNote, string> _noteNumbers = new(ReferenceEqualityComparer.Instance);
        private readonly List<HNote> _numbered = [];

        /// <summary>The notes of <see cref="_numbered"/> before this index are all placed.</summary>
        private int _numberedChecked;
        private readonly HashSet<HNote> _notesPlaced = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<HNote, (List<Piece> Lines, float Height)> _noteLayouts = new(ReferenceEqualityComparer.Instance);
        private readonly List<HNote> _footnotes = [];

        /// <summary>The heights of the page's first <see cref="_footnotesSummed"/> footnotes, summed in double as LINQ sums floats.</summary>
        private double _footnotesSum;
        private int _footnotesSummed;
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
            float y = 0, height = 0, after = 0;
            var first = true;
            foreach (var paragraph in note.Blocks.OfType<HParagraph>())
            {
                // A Word note whose number is inside its text keeps it there ("(1) text" between the brackets); one that
                // starts with it, and 한글's notes, get it in front.
                var own = first && paragraph.Inlines.SkipWhile(i => i is HNoteNumber)
                    .Any(i => i is HNoteNumber || i is HLink link && link.Content.Any(c => c is HNoteNumber));
                var box = Layout(composer.Resolve(paragraph), _geometry.TextWidth, first && !own ? marker : null, noteNumber: own ? marker : null);
                // Word's paragraph spacing: the larger of the space after the paragraph before and this one's before.
                y += composer._collapseSpacing && !first ? Math.Max(0, box.SpaceBefore - after) : box.SpaceBefore;
                first = false;
                foreach (var line in box.Lines)
                {
                    pieces.Add(new Piece(y, line.Height, line.Draw, line.Below));
                    height = y + line.TextHeight;
                    y += line.Height;
                }
                y += after = box.SpaceAfter;
            }
            return _noteLayouts[note] = (pieces, height);
        }

        /// <summary>The heights of this page's footnotes summed (each laid out on first use, in order).</summary>
        private double FootnotesSum()
        {
            for (; _footnotesSummed < _footnotes.Count; _footnotesSummed++)
                _footnotesSum += NoteLayout(_footnotes[_footnotesSummed]).Height;
            return _footnotesSum;
        }

        /// <summary>The height this page's footnotes take: space and line above included.</summary>
        private float FootnotesHeight() => FootnotesHeight(FootnotesSum(), 0);

        /// <summary>
        /// The height the footnotes take with <paramref name="more"/> more: <paramref name="sum"/> is their heights,
        /// the more ones' added on to <see cref="FootnotesSum"/>.
        /// </summary>
        private float FootnotesHeight(double sum, int more)
        {
            var count = _footnotes.Count + more;
            if (count == 0)
                return 0;
            var shape = _section!.Footnotes;
            return (float)sum + (count - 1) * shape.Between / 100f + (shape.Above + shape.Below) / 100f;
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
            var page = Current.Body;
            var shape = _section!.Footnotes;
            var top = _geometry.BodyBottom - FootnotesHeight() + shape.Above / 100f;
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
            (_footnotesSum, _footnotesSummed) = (0, 0);
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

        // ───────────────────────── Page borders ─────────────────────────

        /// <summary>
        /// Draws a page's border and background (쪽 테두리/배경) under everything else: the odd or even pages' one where it
        /// draws something, else the one for both pages. 한글 places the border around the body grown by its offsets
        /// (around the header and footer areas too when they are inside), or its offsets in from the paper's edges.
        /// </summary>
        /// <summary>
        /// Draws the page's border and background under everything (<paramref name="front"/> false: the background, and the
        /// border unless it goes in front), or the border that goes in front of the text over everything (true).
        /// </summary>
        private void PageBorder(Page page, bool front = false)
        {
            var section = page.Section;
            static bool Shows(HFirstPageRule rule, bool first) => rule switch
            {
                HFirstPageRule.HideFirst => !first,
                HFirstPageRule.OnlyFirst => first,
                _ => true,
            };
            var side = page.Number % 2 == 0 ? HPageType.Even : HPageType.Odd;
            if ((section.PageBorders.LastOrDefault(b => b.Pages == side) ?? section.PageBorders.LastOrDefault(b => b.Pages == HPageType.Both)) is not { } border)
                return;
            var showBorder = Shows(section.PageBorderShows, page.FirstOfSection) && page.Hiding?.Border != true && border.InFront == front;
            var showFill = Shows(section.PageFillShows, page.FirstOfSection) && page.Hiding?.Fill != true && !front
                && (border.Fill is not null || border.Picture is not null);
            if (!showBorder && !showFill)
                return;

            var g = page.Geometry;
            var paper = new SKRect(0, 0, g.Width, g.Height);
            var (left, right) = (g.Left, g.Left + g.TextWidth);
            var o = border.Offsets;
            var frame = border.AroundBody
                ? new SKRect(left - o.Left / 100f, (border.HeaderInside ? g.HeaderTop : g.BodyTop) - o.Top / 100f,
                    right + o.Right / 100f, (border.FooterInside ? g.FooterBottom : g.BodyBottom) + o.Bottom / 100f)
                : new SKRect(o.Left / 100f, o.Top / 100f, g.Width - o.Right / 100f, g.Height - o.Bottom / 100f);
            var area = border.FillArea switch
            {
                HPageFillArea.Border => frame,
                HPageFillArea.Page => new SKRect(left, g.HeaderTop, right, g.FooterBottom),
                _ => paper,
            };
            var layer = new PageContent(g.Width, g.Height);
            layer.Draw(canvas =>
            {
                if (showFill && border.Fill is not null && ParagraphText.Color(border.Fill) is { } color)
                {
                    using var paint = new SKPaint { Style = SKPaintStyle.Fill, Color = color };
                    canvas.DrawRect(area, paint);
                }
                if (showFill && border.Picture is { } path && File.Exists(path))
                {
                    if (Metafile.IsMetafile(path))
                    {
                        using var drawing = Metafile.TryLoad(path);
                        drawing?.Draw(canvas, area);
                    }
                    else if (FloatingObjects.Image(path) is { } image)
                    {
                        using (image)
                            canvas.DrawImage(image, area, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                    }
                }
                if (showBorder)
                    TableLayout.Borders(canvas, border.Borders, frame);
            });
            page.Content.Add(layer, under: !front);
        }

        // ───────────────────────── Master pages ─────────────────────────

        /// <summary>
        /// Draws a page's master pages (바탕쪽) behind everything else on it, or in front of the text where they say so:
        /// the one for odd or even pages, else the one for both; on the section's last page its last-page master, on a
        /// chosen page its optional master, each instead of the regular one unless it overlaps it (겹치기). Their text
        /// is laid out from the top of the body, as wide as it.
        /// </summary>
        private void MasterPages(Page page, bool lastOfSection)
        {
            var section = page.Section;
            if (section.MasterPages.Count == 0 || (page.FirstOfSection && section.HideFirstMasterPage) || page.Hiding?.MasterPage == true)
                return;
            _geometry = page.Geometry;
            _number = page.Number;
            HMasterPage? Last(HMasterPageKind kind, Func<HMasterPage, bool>? also = null) =>
                section.MasterPages.LastOrDefault(m => m.Kind == kind && (also is null || also(m)));
            var regular = Last(page.Number % 2 == 0 ? HMasterPageKind.Even : HMasterPageKind.Odd) ?? Last(HMasterPageKind.Both);
            var special = (lastOfSection ? Last(HMasterPageKind.LastPage) : null)
                          ?? Last(HMasterPageKind.OptionalPage, m => m.PageNumber == page.Number);
            HMasterPage?[] drawn = special is null ? [regular] : special.Duplicate ? [regular, special] : [special];
            foreach (var master in drawn.OfType<HMasterPage>())
            {
                var layer = new PageContent(page.Content.Width, page.Content.Height);
                foreach (var piece in Tables.Stack(master.Blocks, page.Geometry.TextWidth))
                    piece.Draw(layer, page.Geometry.Left, page.Geometry.BodyTop + piece.Top);
                page.Content.Add(layer, under: !master.Front);
            }
        }

        // ───────────────────────── Headers and footers ─────────────────────────

        /// <summary>Header and footer heights by blocks and width: a page's own differ only by their fields.</summary>
        private readonly Dictionary<(List<HBlock>, float), float> _heights = [];

        /// <summary>The paragraphs where a header or footer of the section starts (Word's continuous section break).</summary>
        private readonly HashSet<HParagraph> _startsAt = new(ReferenceEqualityComparer.Instance);

        /// <summary>The page (index) from which the header or footer starting at a paragraph applies.</summary>
        private readonly Dictionary<HParagraph, int> _startPages = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// Picks a page's header and footer and sets its areas. On Word models a header taller than the room above the
        /// body moves the body down to below it, and a footer taller than the room below moves it up, as Word does; a
        /// header or footer never leaves less than a quarter of the body (a damaged file).
        /// </summary>
        private void Dress(Page page, int index)
        {
            var section = page.Section;
            var first = page.FirstOfSection;
            page.Header = first && section.FirstPageHeader is { } firstHeader ? firstHeader
                : first && section.HideFirstHeader ? null : Pick(section.Headers, page.Number, index)?.Blocks;
            page.Footer = first && section.FirstPageFooter is { } firstFooter ? firstFooter
                : first && section.HideFirstFooter ? null : Pick(section.Footers, page.Number, index)?.Blocks;

            var g = _paper;
            var least = (g.BodyBottom - g.BodyTop) / 4;
            if (section.HeaderMovesBody && page.Header is { } header)
                g = g with { BodyTop = Math.Min(Math.Max(g.BodyTop, g.HeaderTop + Height(header, g.TextWidth)), g.BodyBottom - least) };
            if (section.FooterMovesBody && page.Footer is { } footer)
                g = g with { BodyBottom = Math.Max(Math.Min(g.BodyBottom, g.FooterBottom - Height(footer, g.TextWidth)), g.BodyTop + least) };
            // Headers and footers stay across; a vertical page's body is laid out turned.
            page.Geometry = g;
            _geometry = page.Turned is null ? g : g.Turned(VerticalText.Upward(section.TextFlow));
        }

        private float Height(List<HBlock> blocks, float width)
        {
            if (!_heights.TryGetValue((blocks, width), out var height))
            {
                Tables.Stack(blocks, width, out height);
                _heights[(blocks, width)] = height;
            }
            return height;
        }

        /// <summary>
        /// A header or footer of a continuous section break starts at <paramref name="paragraph"/>: Word shows it from the
        /// next page, or from this one when the paragraph starts it (and the body then moves for it).
        /// </summary>
        private void StartsHere(HParagraph paragraph)
        {
            if (!_empty)
            {
                _startPages[paragraph] = _pages.Count;
                return;
            }
            _startPages[paragraph] = _pages.Count - 1;
            var before = _geometry.BodyTop;
            Dress(Current, _pages.Count - 1);
            var shift = _geometry.BodyTop - before;
            _y += shift;
            _regionTop += shift;
        }

        private void HeadersAndFooters(Page page)
        {
            _geometry = page.Geometry;
            var section = page.Section;
            // Fields in headers and footers show the number of the page they are on.
            _number = page.Number;
            if (page.Header is { } header && page.Hiding?.Header != true)
                Area(page, header, page.Geometry.HeaderTop);
            if (page.Footer is { } footer && page.Hiding?.Footer != true)
                Area(page, footer, section.FooterGrowsUp ? null : page.Geometry.FooterTop);
            if (section.PageNumber is { } number && page.Hiding?.PageNumber != true)
                PageNumber(page, number);
        }

        /// <summary>
        /// Draws 한글's automatic page number apart from the header and footer: across the text width by its position
        /// (inside and outside by the binding side), at the bottom of the footer area (its descent, about 0.3 of its
        /// size, above the area's bottom, as 한글's PDFs have it) or at the top of the header area.
        /// </summary>
        private void PageNumber(Page page, HPageNumber number)
        {
            var position = number.Position;
            var odd = page.Number % 2 == 1;
            var align = position switch
            {
                _ when position.EndsWith("LEFT", StringComparison.Ordinal) => HAlign.Left,
                _ when position.EndsWith("RIGHT", StringComparison.Ordinal) => HAlign.Right,
                _ when position.StartsWith("INSIDE", StringComparison.Ordinal) => odd ? HAlign.Left : HAlign.Right,
                _ when position.StartsWith("OUTSIDE", StringComparison.Ordinal) => odd ? HAlign.Right : HAlign.Left,
                _ => HAlign.Center,
            };
            var paragraph = new HParagraph { Format = new HParaFormat(Align: align, Left: 0, Right: 0, FirstLine: 0) };
            if (number.Side is { } side)
                paragraph.Inlines.Add(new HText(side + " ", number.Format));
            paragraph.Inlines.Add(new HField(HFieldKind.PageNumber, number.Format) { NumberFormat = number.NumberFormat });
            if (number.Side is { } after)
                paragraph.Inlines.Add(new HText(" " + after, number.Format));
            var g = page.Geometry;
            if (Layout(paragraph, g.TextWidth).Lines is not [var line, ..])
                return;
            var size = number.Format.Size is int hundredths and > 0 ? hundredths / 100f : 10f;
            var top = position.Contains("TOP", StringComparison.Ordinal)
                ? g.HeaderTop
                : g.FooterBottom - 0.3f * size - line.Baseline;
            // Under the page's text, as 한글 writes it before the body (a PDF's text then reads in 한글's order).
            var layer = new PageContent(page.Content.Width, page.Content.Height);
            line.Draw(layer, g.Left, top);
            page.Content.Add(layer, under: true);
        }

        /// <summary>
        /// The header or footer for a page: the one for odd or even pages, else the one for both; of those that apply on
        /// the page (<paramref name="index"/>), the last, so a continuous section break's replaces the one before.
        /// </summary>
        private HHeaderFooter? Pick(List<HHeaderFooter> list, int number, int index)
        {
            bool Applies(HHeaderFooter item) =>
                item.StartsAt is not { } paragraph || _startPages.TryGetValue(paragraph, out var from) && index >= from;
            var side = number % 2 == 0 ? HPageType.Even : HPageType.Odd;
            return list.LastOrDefault(h => h.Pages == side && Applies(h)) ?? list.LastOrDefault(h => h.Pages == HPageType.Both && Applies(h));
        }

        /// <summary>
        /// Lays out a header's or footer's blocks from <paramref name="top"/> down, or (null) up from the bottom of the
        /// footer area. Its objects behind the text go under the page's body (Word's watermarks).
        /// </summary>
        private void Area(Page page, List<HBlock> blocks, float? top)
        {
            var pieces = Tables.Stack(blocks, page.Geometry.TextWidth, out var height);
            var from = top ?? page.Geometry.FooterBottom - height;
            var behind = new PageContent(page.Content.Width, page.Content.Height);
            foreach (var piece in pieces)
                piece.Draw(piece.Behind ? behind : page.Content, page.Geometry.Left, from + piece.Top);
            if (behind.Items.Count > 0)
                page.Content.Add(behind, under: true);
        }

        // ───────────────────────── Fields ─────────────────────────

        private string FieldText(HField field) => field.Kind switch
        {
            HFieldKind.TotalPages => (total ?? _pages.Count).ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => PageNumberText(_number, field.NumberFormat),
        };
    }

    /// <summary>
    /// A page number in an OWPML number format. 한글 counts letters round (page 27 is A again, 33 is G), and 가나다 the
    /// same way; the other formats are those of list numbers.
    /// </summary>
    internal static string PageNumberText(int number, string? format) => format switch
    {
        "LATIN_CAPITAL" or "LATIN_SMALL" when number >= 1 => ((char)((format == "LATIN_CAPITAL" ? 'A' : 'a') + (number - 1) % 26)).ToString(),
        "HANGUL_SYLLABLE" when number >= 1 => "가나다라마바사아자차카타파하"[(number - 1) % 14].ToString(),
        null or "DIGIT" => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => ListNumbers.Format(number, format),
    };

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

    /// <summary>
    /// A line of a code block in a document of another format (a Markdown or notebook code cell, an HTML pre): the
    /// readers mark it as code, or shade all of its text as code.
    /// </summary>
    internal static bool IsCode(HBlock? block) =>
        block is HParagraph paragraph && (paragraph.Role == HBlockRole.Code
                                          || paragraph.Inlines.Count > 0 && paragraph.Inlines.All(i => i is HText { Format.Shade: HtmlReader.CodeShade }));

    /// <summary>Line spacing of code lines: set solid, as editors and browsers show code.</summary>
    private static readonly HLineSpacing CodeSpacing = new(HLineSpacingKind.Percent, 130);

    /// <summary>
    /// The paragraph with every format member set (as it is when there is no resolver). A code line of another format
    /// gets a monospaced font where it names none, and solid lines without paragraph spacing, the space after the
    /// paragraph style's only at the end of its block (before <paramref name="next"/>, which isn't code).
    /// </summary>
    private HParagraph Resolve(HParagraph paragraph, HBlock? next = null)
    {
        if (_styles is null)
            return paragraph;
        var code = IsCode(paragraph);
        var format = _styles.Paragraph(paragraph);
        if (code)
            format = format with
            {
                Before = 0,
                After = IsCode(next) ? 0 : format.After,
                LineSpacing = CodeSpacing,
                Layout = (format.Layout ?? new HParaLayout()) with { LineHeight = HLineHeight.From(CodeSpacing) },
            };
        // Word's paragraph spacing: the larger of this one's space after and the next one's space before goes between them.
        if (_collapseSpacing && next is HParagraph following)
            format = format with { After = HDocument.CollapsedAfter(format, following, _styles.Paragraph(following)) };
        // The style resolver leaves out a block quote's bar and background and Word's borders as the writers write them;
        // the paragraph keeps them to draw (BoxBorder).
        format = format with { LeftBar = paragraph.Format.LeftBar, Fill = paragraph.Format.Fill, Borders = paragraph.Format.Borders };
        var resolved = new HParagraph
        {
            Format = format,
            MarkFormat = _styles.Mark(paragraph) is var mark && code ? _styles.Character(paragraph.MarkFormat with { Font = paragraph.MarkFormat.Font ?? FontNames.MonospaceFamily }, paragraph) : mark,
            HeadingLevel = paragraph.HeadingLevel,
            // A number's own format (Word's) is resolved as the text's.
            List = paragraph.List is { MarkerFormat: { } marker } list ? list with { MarkerFormat = _styles.Character(marker, paragraph) } : paragraph.List,
            PageBreakBefore = paragraph.PageBreakBefore,
            ColumnBreakBefore = paragraph.ColumnBreakBefore,
            ColumnsChange = paragraph.ColumnsChange,
            PageHiding = paragraph.PageHiding,
            Lines = paragraph.Lines,
        };
        resolved.Inlines.AddRange(Inlines(paragraph.Inlines, paragraph, link: false, code));
        return resolved;
    }

    private IEnumerable<HInline> Inlines(IEnumerable<HInline> inlines, HParagraph paragraph, bool link, bool code = false)
    {
        HCharFormat Format(HCharFormat format) => Named(format, _styles!.Character(code && format.Font is null ? format with { Font = FontNames.MonospaceFamily } : format,
            paragraph, HStyleContext.Body, link));
        // A Word equation's number box (DocxReader.NumberBox) is placed for rhwp: the number goes with the equation after it.
        HTextBox? numberBox = null;
        foreach (var inline in inlines)
        {
            if (numberBox is not null && inline is not HEquation)
            {
                yield return numberBox;
                numberBox = null;
            }
            switch (inline)
            {
                case HTextBox { EquationNumber: true, Blocks: [HParagraph { Inlines: [HText] }] } box:
                    numberBox = box;
                    break;
                case HEquation equation:
                    var resolved = new HEquation(equation.Math, Format(equation.Format))
                    {
                        Display = equation.Display,
                        Width = equation.Width,
                        Height = equation.Height,
                        BaseLine = equation.BaseLine,
                        Size = equation.Size,
                        Color = equation.Color,
                    };
                    if (numberBox is { Blocks: [HParagraph { Inlines: [HText numberText] }] })
                        _layouter.Number(resolved, new HText(numberText.Text, Format(numberText.Format)));
                    numberBox = null;
                    yield return resolved;
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
                case HNoteNumber number:
                    yield return new HNoteNumber(Format(number.Format));
                    break;
                case HLink hyperlink:
                    var copy = new HLink(hyperlink.Target);
                    copy.Content.AddRange(Inlines(hyperlink.Content, paragraph, link: true, code));
                    yield return copy;
                    break;
                default:
                    yield return inline;
                    break;
            }
        }
        if (numberBox is not null)
            yield return numberBox;
    }

    /// <summary>
    /// <paramref name="resolved"/> with the template fonts <paramref name="own"/> (the run's format before the template
    /// filled it in) names itself kept as its own (HCharLayout.NamedFonts), so they keep the font's ₩ for a backslash.
    /// </summary>
    private HCharFormat Named(HCharFormat own, HCharFormat resolved)
    {
        if (_styles?.Faces is not { Count: > 0 } template)
            return resolved;
        var layout = own.Layout;
        string?[] names = [own.Font, own.EastAsianFont, layout?.WordFonts?.Ascii, layout?.WordFonts?.HighAnsi, layout?.WordFonts?.EastAsia,
            layout?.WordFonts?.ComplexScript, .. layout?.Slots is { } slots ? HLanguageSlots.Languages.Select(l => slots[l].Font) : []];
        var named = names.OfType<string>().Select(n => n.Trim()).Where(template.Contains).Distinct(StringComparer.Ordinal).ToList();
        return named.Count == 0 ? resolved
            : resolved with { Layout = (resolved.Layout ?? new HCharLayout()) with { NamedFonts = string.Join('\n', named) } };
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
