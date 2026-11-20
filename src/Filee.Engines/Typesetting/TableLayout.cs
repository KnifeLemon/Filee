// Lays out tables (GitHub Project 4, item "Layout engine: tables") for the page composer, and stacks the blocks of
// any paragraph list (a cell, a header or footer) into pieces the composer and cells draw.
//
// - Grid: cells take their column (or the next free one, skipping cells that span down from above), their column and
//   row spans. Column widths are the table's absolute widths, else its relative ones (columns without one share what
//   is left), else equal shares of the width. Columns and spans of a damaged or hostile model are cut at
//   DocumentLimits.MaxTableColumns and row spans at the last row; free columns are found in a segment tree, not a grid
//   of every spanned cell. Tables of other formats (a spreadsheet's columns) are scaled down to the text width when
//   wider; 한글's keep the widths 한글 gave them, as 한글 draws them.
// - Word's tables (HTable.Sizing) are sized as Word sizes them. A column takes its grid width (w:gridCol, the widths
//   Word laid the table out at when it saved), else the widest preferred width (w:tcW: a length, or a share of the
//   table's width) a cell of the column gives, else, where nothing gives one, its content's width. An autofit table
//   (not w:tblLayout fixed) then widens each column to its longest word (what a cell's text needs at least, with the
//   cell's margins), and to the table's preferred width (w:tblW: a length or a share of the width around it) when it
//   is narrower; a cell spanning columns spreads what it needs over them. A table wider than its room (from its indent
//   to the far edge: the text column, or the cell a nested table is in, which it never overflows) gives up first what
//   its columns have beyond their widest line, then down to their longest words, then shrinks evenly; a fixed table
//   outside cells keeps its widths and runs into the margin (both margins when centred), as Word lets it. Content widths
//   come from ParagraphLayouter.ContentWidths, once per table (nested tables measure as the widths they need), for
//   its first MaxMeasuredCells cells. Grid columns a row leaves empty before or after its cells (w:gridBefore,
//   w:gridAfter) take the width the row gives them (w:wBefore, w:wAfter) as a cell's preferred width. The indent is to
//   the table's edge, or to the first cell's text in Word 2010's compatibility mode (HTable.EdgeBeforeIndent); a Word
//   table may start in the margin.
// - Cells: their blocks are laid out at the columns' width less the padding (the cell's, else the table's, else
//   한글's default 1.8 mm by 0.5 mm), nested tables too; a row is as high as its tallest single-row cell or its own
//   height, whichever is more, and a cell spanning rows makes its last row higher when the rows are short of it. A
//   한글 cell holds its content down to its last line's text; a Word cell (WordCells) holds the whole last line and
//   the space after its last paragraph too, as Word sizes rows.
//   Content sits at the top, middle or bottom of the cell. Paragraph borders and backgrounds in a stack (a cell, a
//   header) are drawn as in the body (BoxBorder), each line with its part of the box. Cells spaced apart (Word's
//   w:tblCellSpacing) sit that far in from their grid columns and their row on every side (twice that between two
//   cells), measured with it, and the table's outer border (HTable.Frame) is drawn around them.
// - Right to left (Word w:bidiVisual, RTF \rtlrow): the first column is the rightmost, and the table's alignment and
//   indent and each cell's left and right borders and padding name the start and the end side, so they change places.
// - Drawing: each cell's fill (a colour, a picture; EMF / WMF as vector drawing), then its content, then its borders (the cell's, else the table's, else a thin grid);
//   solid, dashed, dotted, dash-dot and double lines at their width.
// - Pages: rows bound by a cell spanning them form a group that moves to a page together. A table that must not split
//   (한글 나누지 않음) goes to the next page whole when it fits on one; others split between groups (한글 셀 단위로
//   나눔), and a one-row group taller than what is left splits inside its cells where the rows may split (한글 나눔,
//   and sources that don't say, as Word allows) unless the row must not split. Header rows repeat on every page.

using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Metafiles;
using Filee.Engines.Rendering;
using SkiaSharp;

namespace Filee.Engines.Typesetting;

/// <summary>Something drawn at <c>Top</c> in a stack, <c>Height</c> high: a line, or a row of a nested table.</summary>
/// <param name="Draw">Draws the piece with its stack's top-left corner at the given point.</param>
/// <param name="Below">Space at the bottom of the piece that is no ink (a line's spacing below its text).</param>
internal sealed record Piece(float Top, float Height, Action<PageContent, float, float> Draw, float Below = 0)
{
    /// <summary>A floating object behind the text of the stack (a header's watermark goes behind the page's body too).</summary>
    public bool Behind { get; init; }
}

/// <summary>A cell laid out: its place in the table, size, padding and content.</summary>
internal sealed class CellBox
{
    public required int Row { get; init; }
    public required int RowSpan { get; init; }
    public required float X { get; init; }
    public required float Width { get; init; }
    public required HInsets Padding { get; init; }
    public required HVerticalAlign VerticalAlign { get; init; }
    public required HBorders Borders { get; init; }
    public SKColor? Fill { get; init; }

    /// <summary>A picture stretched over the cell (한글 picture fill), over its colour.</summary>
    public string? FillPicture { get; init; }
    public required IReadOnlyList<Piece> Content { get; init; }

    /// <summary>Height of the content.</summary>
    public float ContentHeight => Content.Count == 0 ? 0 : Content.Max(p => p.Top + p.Height);

    /// <summary>Height of the content without the spacing below its last line: what 한글 centres in a cell.</summary>
    public float InkHeight => Content.Count == 0 ? 0 : Content.Max(p => p.Top + p.Height - p.Below);

    /// <summary>
    /// How far the content goes down with the space after its last paragraph, which a Word cell holds (Stack's extent);
    /// null for a 한글 cell, which holds its content down to the last line's text.
    /// </summary>
    public float? Extent { get; init; }

    /// <summary>The height of the content the cell holds and centres: Word's down to <see cref="Extent"/>, 한글's <see cref="InkHeight"/>.</summary>
    public float Held => Extent is { } extent ? Math.Max(ContentHeight, extent) : InkHeight;

    /// <summary>The space between the cell and its row's top and bottom (Word's cell spacing; across it is in X and Width).</summary>
    public float Spacing { get; init; }

    /// <summary>The height the cell needs: the content it holds, its padding and the space above and below it.</summary>
    public float Needed => Held + (Padding.Top + Padding.Bottom) / 100f + 2 * Spacing;
}

/// <summary>A table's outer border around cells spaced apart, <paramref name="Width"/> wide (Word's tblBorders with tblCellSpacing).</summary>
internal sealed record TableFrame(HBorders Borders, float Width);

/// <summary>A row: its height and the cells that start in it.</summary>
internal sealed class RowBox
{
    public required float Height { get; set; }
    public required IReadOnlyList<CellBox> Cells { get; init; }
    public bool Header { get; init; }
    public bool CantSplit { get; init; }

    /// <summary>The table's border around spaced cells, drawn at the row's sides; its top over the first row, its bottom under the last.</summary>
    public TableFrame? Frame { get; init; }
    public bool FrameTop { get; init; }
    public bool FrameBottom { get; init; }
}

/// <summary>A laid out table.</summary>
/// <param name="Offset">Left edge from the column's left edge.</param>
internal sealed record TableBox(float Offset, float Width, IReadOnlyList<RowBox> Rows, HTablePageBreak? PageBreak, IReadOnlyList<Piece> Caption)
{
    /// <summary>The width of the column the table was laid out in.</summary>
    public float Column { get; init; }

    public float Height => Rows.Sum(r => r.Height);

    /// <summary>Rows bound together by cells spanning them, as (first, count).</summary>
    public IReadOnlyList<(int First, int Count)> Groups()
    {
        var groups = new List<(int, int)>();
        for (var first = 0; first < Rows.Count;)
        {
            var last = first;
            for (var r = first; r <= last; r++)
            {
                foreach (var cell in Rows[r].Cells)
                    last = Math.Max(last, Math.Min(Rows.Count - 1, cell.Row + cell.RowSpan - 1));
            }
            groups.Add((first, last - first + 1));
            first = last + 1;
        }
        return groups;
    }

    /// <summary>The leading header rows (repeated on every page the table continues on).</summary>
    public int HeaderRows => Rows.TakeWhile(r => r.Header).Count();
}

/// <summary>Lays out tables and paragraph lists for a layout job.</summary>
/// <param name="frame">The page areas floating objects relative to the paper or the page are placed in; null places them in the list.</param>
/// <param name="measure">
/// The narrowest and the natural width of a paragraph (ParagraphLayouter.ContentWidths), which Word's autofit tables size
/// their columns by; null sizes them by their preferred widths alone.
/// </param>
internal sealed class TableLayout(Func<HParagraph, float, ParagraphBox> paragraph, bool fitToWidth, Func<AnchorFrame>? frame = null,
    Func<HParagraph, (float Min, float Max)>? measure = null)
{
    /// <summary>한글's default cell padding: 1.8 mm left and right, 0.5 mm top and bottom (HWPUNIT).</summary>
    private static readonly HInsets DefaultPadding = new(510, 510, 141, 141);

    /// <summary>Tables nested deeper than this inside cells are left out (a damaged or hostile file).</summary>
    private const int MaxDepth = 16;

    private int _depth;

    /// <summary>How deep in cells the tables being laid out are (0: in the body, a header, a text box).</summary>
    private int _inCell;

    /// <summary>
    /// Cells are sized as Word sizes them (a model of another format, resolved through the style resolver): the space
    /// after a cell's last paragraph counts in its row. Else as 한글 sizes them, down to the last line's text.
    /// </summary>
    public bool WordCells { get; init; }

    // ───────────────────────── Stacks ─────────────────────────

    /// <summary>
    /// The blocks of a paragraph list stacked from the top in a column <paramref name="width"/> wide. Lines on 한글's
    /// stored layout take their stored place while the list so far follows it, as on a page. Floating objects come
    /// first (behind the text) or last (in front of it).
    /// </summary>
    public List<Piece> Stack(IReadOnlyList<HBlock> blocks, float width) => Stack(blocks, width, out _);

    /// <inheritdoc cref="Stack(IReadOnlyList{HBlock}, float)"/>
    /// <param name="extent">How far the list goes down: its lines and tables with their spacing, without floating objects.</param>
    public List<Piece> Stack(IReadOnlyList<HBlock> blocks, float width, out float extent)
    {
        var pieces = new List<Piece>();
        var (behind, front) = (new List<(int, Piece)>(), new List<(int, Piece)>());
        void Objects(HParagraph p, float top)
        {
            foreach (var (order, piece, under) in FloatingObjects.Pieces(p, top, width, frame?.Invoke(), Stack))
                (under ? behind : front).Add((order, piece));
        }
        float y = 0;
        var inStep = true;
        var skipBefore = false;
        HParagraph? holder = null;
        // The paragraph laid out ahead (to see whether its border continues the box), and the box the last paragraph drew.
        (int Index, ParagraphBox Box)? ahead = null;
        (BoxBorder Border, int Index, float Bottom)? box = null;
        for (var i = 0; i < blocks.Count; i++)
        {
            switch (blocks[i])
            {
                case HParagraph p when HoldsTable(blocks, i):
                    holder = p;
                    break;
                case HParagraph p:
                    var laid = ahead is (var at, var early) && at == i ? early : paragraph(p, width);
                    ahead = null;
                    y += skipBefore ? 0 : laid.SpaceBefore;
                    skipBefore = false;
                    var (opens, closes) = (true, true);
                    if (laid.Border is { } border)
                    {
                        opens = !(box is (var before, var index, _) && index == i - 1 && before.Joins(border));
                        if (i + 1 < blocks.Count && blocks[i + 1] is HParagraph following && !HoldsTable(blocks, i + 1))
                        {
                            ahead = (i + 1, paragraph(following, width));
                            closes = !border.Joins(ahead.Value.Box.Border);
                        }
                        laid = border.Pad(laid, opens, closes);
                    }
                    if (laid.Lines.Count == 0)
                        Objects(p, y);
                    for (var l = 0; l < laid.Lines.Count; l++)
                    {
                        var line = laid.Lines[l];
                        if (laid.StoredLines && inStep && line.StoredTop is { } top)
                            y = Math.Max(y, top);
                        if (l == 0)
                            Objects(p, y);
                        if (laid.Border is { } own)
                        {
                            var gap = l == 0 && !opens && box is (_, _, var bottom) ? Math.Max(0, y - bottom) : 0;
                            var slice = own.Slice(laid, l == 0, l == laid.Lines.Count - 1, opens, closes, gap);
                            var height = line.Height;
                            pieces.Add(new Piece(y, height, (page, left, lineTop) =>
                            {
                                page.Draw(canvas => slice.Draw(canvas, left, lineTop, height));
                                line.Draw(page, left, lineTop);
                            }, line.Below));
                        }
                        else
                        {
                            pieces.Add(new Piece(y, line.Height, line.Draw, line.Below));
                        }
                        y += line.Height;
                    }
                    box = laid.Border is { } drawn ? (drawn, i, y) : null;
                    y += laid.SpaceAfter;
                    inStep &= laid.StoredLines;
                    break;
                case HTable t when _depth < MaxDepth:
                    box = null;
                    _depth++;
                    try
                    {
                        // The 한글 line the table is: the holder's, or the table's own when it is all its paragraph holds.
                        var follower = Follower(blocks, i);
                        var line = holder?.Lines?.Segments.FirstOrDefault() ?? (follower is null ? t.Line : null);
                        var lineTop = y;
                        if (holder is not null || line is not null)
                        {
                            y = inStep && line is { } stored ? Math.Max(y, stored.VerticalPosition / 100f) : y + (holder?.Format.Before ?? 0) / 100f;
                            lineTop = y;
                            if (holder is not null)
                                Objects(holder, y);
                        }
                        else if (follower is not null)
                        {
                            // The table starts its 한글 paragraph, placed from the paragraph's top by its offset; 한글
                            // puts the paragraph's text right under it, without the space before.
                            skipBefore = true;
                        }
                        var margin = t.OuterMargin ?? default;
                        y += (t.VerticalOffset + margin.Top) / 100f;
                        var table = Layout(t, width - (margin.Left + margin.Right) / 100f);
                        var left = table.Offset + margin.Left / 100f;
                        foreach (var (first, count) in table.Groups())
                        {
                            var rows = table.Rows.Skip(first).Take(count).ToList();
                            var height = rows.Sum(r => r.Height);
                            pieces.Add(new Piece(y, height, (page, x, top) => Draw(page, rows, x + left, top)));
                            y += height;
                        }
                        foreach (var piece in table.Caption)
                            pieces.Add(piece with { Top = y + piece.Top });
                        y += table.Caption.Count == 0 ? 0 : table.Caption.Max(p => p.Top + p.Height);
                        y += margin.Bottom / 100f;
                        // Filee's rows are not 한글's unless the table is as high as 한글's.
                        var kept = AsStored(t, table.Height, line);
                        if (line is { } rest)
                            y = Math.Max(y, lineTop + rest.VerticalSize / 100f) + rest.Spacing / 100f + (holder?.Format.After ?? 0) / 100f;
                        else if (holder is not null)
                            y += (holder.Format.After ?? 0) / 100f;
                        holder = null;
                        inStep &= kept;
                    }
                    finally
                    {
                        _depth--;
                    }
                    break;
            }
        }
        pieces.InsertRange(0, behind.OrderBy(o => o.Item1).Select(o => o.Item2 with { Behind = true }));
        pieces.AddRange(front.OrderBy(o => o.Item1).Select(o => o.Item2));
        extent = y;
        return pieces;
    }

    /// <summary>
    /// 한글 keeps a table inside a paragraph, which the model splits around it: with no text before the table (only
    /// objects floating at it), 한글's line is the table's, and the part takes no line of its own.
    /// </summary>
    public static bool HoldsTable(IReadOnlyList<HBlock> blocks, int index) =>
        blocks[index] is HParagraph { Lines.Starts: null } paragraph && index + 1 < blocks.Count && blocks[index + 1] is HTable &&
        ParagraphText.From(paragraph).Text.Length == 0;

    /// <summary>
    /// A table laid out <paramref name="height"/> high is as high as 한글 laid it out: as its line for it (outer margins
    /// included) or, placed at its paragraph, as its stored size.
    /// </summary>
    public static bool AsStored(HTable table, float height, HLineSegment? line)
    {
        var margin = table.OuterMargin ?? default;
        return line is { } own
            ? Math.Abs(height + (margin.Top + margin.Bottom) / 100f - own.VerticalSize / 100f) <= 1
            : table.StoredHeight is { } stored && Math.Abs(height - stored / 100f) <= 1;
    }

    /// <summary>The paragraph after the table at <paramref name="index"/> when it is the rest of the 한글 paragraph the table starts.</summary>
    public static HParagraph? Follower(IReadOnlyList<HBlock> blocks, int index) =>
        blocks[index] is HTable { OpensParagraph: true, Line: null } && index + 1 < blocks.Count && blocks[index + 1] is HParagraph next ? next : null;

    // ───────────────────────── Tables ─────────────────────────

    /// <summary>Lays out <paramref name="table"/> in a column <paramref name="width"/> points wide.</summary>
    public TableBox Layout(HTable table, float width)
    {
        var placed = Place(table);
        var columns = Math.Max(1, Math.Max(Math.Min(table.ColumnCount, MaxColumns), placed.Count == 0 ? 0 : placed.Max(c => c.Column + ColumnSpan(c.Column, c.Cell))));
        var widths = table.Sizing is { } sizing ? WordColumnWidths(table, sizing, placed, columns, width) : ColumnWidths(table, columns, width);
        var left = new float[columns + 1];
        for (var c = 0; c < columns; c++)
            left[c + 1] = left[c] + widths[c];
        var tableWidth = left[columns];

        var rowCount = table.Rows.Count;
        var cells = new List<CellBox>[rowCount];
        for (var r = 0; r < rowCount; r++)
            cells[r] = [];
        var mirror = table.RightToLeft;
        // Cells spaced apart (Word's cell spacing) sit this far in from their grid columns and their row on every side.
        var spacing = Math.Max(0, table.CellSpacing / 100f);
        foreach (var (row, column, cell) in placed)
        {
            var span = Math.Clamp(cell.ColSpan, 0, columns - column);
            var padding = cell.CellMargin ?? table.CellMargin ?? DefaultPadding;
            var borders = cell.Borders ?? table.Borders ?? HBorders.Grid;
            if (mirror)
            {
                padding = padding with { Left = padding.Right, Right = padding.Left };
                borders = borders with { Left = borders.Right, Right = borders.Left };
            }
            var cellWidth = spacing > 0 ? Math.Max(0, left[column + span] - left[column] - 2 * spacing) : left[column + span] - left[column];
            var contentWidth = Math.Max(1, cellWidth - (padding.Left + padding.Right) / 100f);
            List<Piece> content;
            float extent;
            _inCell++;
            try
            {
                content = Stack(cell.Blocks, contentWidth, out extent);
            }
            finally
            {
                _inCell--;
            }
            cells[row].Add(new CellBox
            {
                Row = row,
                RowSpan = Math.Max(1, Math.Min(cell.RowSpan, rowCount - row)),
                X = (mirror ? tableWidth - left[column + span] : left[column]) + spacing,
                Width = cellWidth,
                Spacing = spacing,
                Padding = padding,
                VerticalAlign = cell.VerticalAlign,
                Borders = borders,
                Fill = ParagraphText.Color(cell.Fill),
                FillPicture = cell.FillPicture,
                Content = content,
                Extent = WordCells ? extent : null,
            });
        }

        // Row heights: the row's own, its single-row cells; then the cells spanning rows stretch their last row.
        var rows = new List<RowBox>(rowCount);
        var outerFrame = spacing > 0 && table.Frame is { } outer
            ? new TableFrame(mirror ? outer with { Left = outer.Right, Right = outer.Left } : outer, tableWidth)
            : null;
        for (var r = 0; r < rowCount; r++)
        {
            var source = table.Rows[r];
            var height = Math.Max((source.Height ?? 0) / 100f, cells[r].Where(c => c.RowSpan == 1).Select(c => c.Needed).DefaultIfEmpty(0).Max());
            rows.Add(new RowBox
            {
                Height = height,
                Cells = cells[r],
                Header = source.Header,
                CantSplit = source.CantSplit,
                Frame = outerFrame,
                FrameTop = r == 0,
                FrameBottom = r == rowCount - 1,
            });
        }
        // The rows' heights are summed through a Fenwick tree, so a table of many cells spanning many rows costs a
        // logarithm per cell rather than its rows.
        var heights = new RowSums(rows);
        foreach (var cell in cells.SelectMany(c => c).Where(c => c.RowSpan > 1).OrderBy(c => c.Row + c.RowSpan))
        {
            var have = heights.Sum(cell.Row, cell.Row + cell.RowSpan);
            if (cell.Needed > have)
            {
                var last = rows[cell.Row + cell.RowSpan - 1];
                var before = last.Height;
                last.Height += cell.Needed - have;
                heights.Add(cell.Row + cell.RowSpan - 1, (double)last.Height - before);
            }
        }

        var offset = (table.Align, mirror) switch
        {
            (HAlign.Center, _) => (width - tableWidth) / 2,
            (HAlign.Right, false) => width - tableWidth,
            (HAlign.Right, true) => 0,
            (_, true) => width - tableWidth - EdgeIndent(table),
            _ => EdgeIndent(table),
        };
        var caption = table.Caption.Count > 0 ? Stack(table.Caption, tableWidth) : [];
        // A Word table may start in the margin (a negative indent, Word 2010's indent to the cell's text) and run into
        // both margins when it is centred and wider than the column, as may an RTF table with a negative \trleft (Word
        // writes \trleft-108 for its 108 twips of padding); other formats' tables start at the column's edge.
        return new TableBox(table.Sizing is null && table.Indent >= 0 ? Math.Max(0, offset) : offset, tableWidth, rows, table.PageBreak, caption) { Column = width };
    }

    /// <summary>Where a table's edge is from the column's start side, in points: its indent, less what lies before it (HTable.EdgeBeforeIndent).</summary>
    private static float EdgeIndent(HTable table) => (table.Indent - table.EdgeBeforeIndent) / 100f;

    /// <summary>
    /// Columns a table has at most (<see cref="DocumentLimits.MaxTableColumns"/>): cells of a damaged or hostile model
    /// that start or reach further are cut there.
    /// </summary>
    private static int MaxColumns => DocumentLimits.Default.MaxTableColumns;

    /// <summary>The columns a cell at <paramref name="column"/> spans, cut at <see cref="MaxColumns"/>.</summary>
    private static int ColumnSpan(int column, HCell cell) => Math.Min(cell.ColSpan, MaxColumns - column);

    /// <summary>Each cell's row and grid column.</summary>
    private static List<(int Row, int Column, HCell Cell)> Place(HTable table)
    {
        var placed = new List<(int, int, HCell)>();
        var rowCount = table.Rows.Count;
        // The row up to which each column is taken (by a cell of this row or one spanning down from above). Only as
        // many columns as the cells can reach are tracked.
        var reach = 1L;
        foreach (var row in table.Rows)
        {
            foreach (var cell in row.Cells)
                reach += Math.Max(0, cell.Column) + Math.Max(1, (long)cell.ColSpan);
        }
        var taken = new TakenColumns((int)Math.Min(reach, MaxColumns));
        for (var r = 0; r < rowCount; r++)
        {
            var next = 0;
            foreach (var cell in table.Rows[r].Cells)
            {
                var column = Math.Min(cell.Column, MaxColumns - 1);
                if (column < 0)
                    column = Math.Min(taken.FirstFree(next, r), MaxColumns - 1);
                placed.Add((r, column, cell));
                var span = Math.Clamp(cell.ColSpan, 1, MaxColumns - column);
                var end = r + Math.Clamp(cell.RowSpan, 1, rowCount - r);
                taken.Take(column, column + span, end);
                next = column + span;
            }
        }
        return placed;
    }

    /// <summary>
    /// For each column the row up to which it is taken (exclusive): a segment tree that raises a range of columns and
    /// finds the first free column from a start, each in a logarithm of the columns.
    /// </summary>
    private sealed class TakenColumns
    {
        private readonly int _size;
        private readonly int[] _low;
        private readonly int[] _raise;

        public TakenColumns(int columns)
        {
            _size = 1;
            while (_size < columns)
                _size *= 2;
            _low = new int[2 * _size];
            _raise = new int[2 * _size];
        }

        /// <summary>Columns <paramref name="from"/> to <paramref name="to"/> (exclusive) are taken up to <paramref name="row"/>.</summary>
        public void Take(int from, int to, int row) => Take(1, 0, _size, Math.Min(from, _size), Math.Min(to, _size), row);

        private void Take(int node, int start, int end, int from, int to, int row)
        {
            if (to <= start || end <= from)
                return;
            if (from <= start && end <= to)
            {
                _low[node] = Math.Max(_low[node], row);
                _raise[node] = Math.Max(_raise[node], row);
                return;
            }
            var middle = (start + end) / 2;
            Take(2 * node, start, middle, from, to, row);
            Take(2 * node + 1, middle, end, from, to, row);
            _low[node] = Math.Max(_raise[node], Math.Min(_low[2 * node], _low[2 * node + 1]));
        }

        /// <summary>
        /// The first column from <paramref name="from"/> that row <paramref name="row"/> doesn't take; past the
        /// tracked columns when all are taken.
        /// </summary>
        public int FirstFree(int from, int row)
        {
            if (from >= _size)
                return from;
            var found = FirstFree(1, 0, _size, from, row, 0);
            return found < 0 ? _size : found;
        }

        private int FirstFree(int node, int start, int end, int from, int row, int raised)
        {
            raised = Math.Max(raised, _raise[node]);
            if (end <= from || Math.Max(_low[node], raised) > row)
                return -1;
            if (end - start == 1)
                return start;
            var middle = (start + end) / 2;
            var left = FirstFree(2 * node, start, middle, from, row, raised);
            return left >= 0 ? left : FirstFree(2 * node + 1, middle, end, from, row, raised);
        }
    }

    /// <summary>Sums of consecutive rows' heights while single rows grow (a Fenwick tree, in double as LINQ sums floats).</summary>
    private sealed class RowSums
    {
        private readonly double[] _tree;

        public RowSums(IReadOnlyList<RowBox> rows)
        {
            _tree = new double[rows.Count + 1];
            for (var i = 0; i < rows.Count; i++)
                Add(i, rows[i].Height);
        }

        public void Add(int row, double amount)
        {
            for (var i = row + 1; i < _tree.Length; i += i & -i)
                _tree[i] += amount;
        }

        /// <summary>The heights of rows <paramref name="from"/> to <paramref name="to"/> (exclusive).</summary>
        public float Sum(int from, int to) => (float)(Prefix(to) - Prefix(from));

        private double Prefix(int count)
        {
            double sum = 0;
            for (var i = count; i > 0; i -= i & -i)
                sum += _tree[i];
            return sum;
        }
    }

    /// <summary>The columns' widths in points.</summary>
    private float[] ColumnWidths(HTable table, int columns, float width)
    {
        var widths = new float[columns];
        if (table.ColumnWidths is { Length: > 0 } absolute)
        {
            for (var c = 0; c < columns; c++)
                widths[c] = c < absolute.Length ? Math.Max(0, absolute[c]) / 100f : 0;
            // Columns without a width share the width the others leave, or a hundredth of it each.
            var missing = widths.Count(w => w <= 0);
            if (missing > 0)
            {
                var rest = Math.Max(width / 100 * missing, width - widths.Sum());
                for (var c = 0; c < columns; c++)
                {
                    if (widths[c] <= 0)
                        widths[c] = rest / missing;
                }
            }
        }
        else if (table.RelativeWidths is { Length: > 0 } relative)
        {
            var given = relative.Take(columns).Where(f => f > 0).Sum();
            var shared = columns - relative.Take(columns).Count(f => f > 0);
            for (var c = 0; c < columns; c++)
            {
                var fraction = c < relative.Length && relative[c] > 0 ? relative[c] : shared > 0 ? Math.Max(0, 1 - given) / shared : 0;
                widths[c] = (float)fraction * width;
            }
        }
        else
        {
            Array.Fill(widths, width / columns);
        }
        var total = widths.Sum();
        if (total <= 0)
            Array.Fill(widths, width / columns);
        else if (fitToWidth && total > width + 0.5f)
        {
            for (var c = 0; c < columns; c++)
                widths[c] *= width / total;
        }
        return widths;
    }

    // ───────────────────────── Word's column widths ─────────────────────────

    /// <summary>Cells measured for a table's content widths at most: the cells after them don't widen its columns.</summary>
    private const int MaxMeasuredCells = 2048;

    /// <summary>What each column's cells need: their longest word and their widest line, with the cells' margins (points).</summary>
    private sealed record ContentWidths(float[] Min, float[] Max);

    /// <summary>Tables measured so far (a nested table is measured for its cell, then laid out with the same widths).</summary>
    private readonly Dictionary<HTable, ContentWidths> _measured = new(ReferenceEqualityComparer.Instance);

    /// <summary>A Word table's columns in a column <paramref name="width"/> points wide (see the file's notes).</summary>
    private float[] WordColumnWidths(HTable table, HTableSizing sizing, List<(int Row, int Column, HCell Cell)> placed, int columns, float width)
    {
        // The room the table has: from its indent to the column's far edge (a negative indent reaches into the margin).
        var room = Math.Max(1, table.Align == HAlign.Left ? width - EdgeIndent(table) : width);
        float? tableWidth = sizing.Width.Type switch
        {
            HWidthType.Absolute => (float)(sizing.Width.Value / 100),
            HWidthType.Percent => (float)(sizing.Width.Value * width),
            _ => null,
        };
        var widths = Preferred(table, sizing, placed, columns, tableWidth ?? room);
        var open = widths.Contains(0f);
        var content = !sizing.Fixed || open || (_inCell > 0 && widths.Sum() > room + 0.5f) ? Measure(table, placed, columns) : null;
        if (content is { } measured)
        {
            for (var c = 0; c < columns; c++)
            {
                if (widths[c] <= 0)
                    widths[c] = measured.Max[c];
                else if (!sizing.Fixed)
                    widths[c] = Math.Max(widths[c], measured.Min[c]);
            }
        }
        // Columns nothing gives a width share what the table's width leaves, or a hundredth of it each.
        var missing = widths.Count(w => w <= 0);
        if (missing > 0)
        {
            var basis = tableWidth ?? room;
            var rest = Math.Max(basis / 100 * missing, basis - widths.Sum());
            for (var c = 0; c < columns; c++)
            {
                if (widths[c] <= 0)
                    widths[c] = rest / missing;
            }
        }
        var total = widths.Sum();
        if (!sizing.Fixed && tableWidth is { } wanted && total < Math.Min(wanted, room) - 0.5f && total > 0)
        {
            var scale = Math.Min(wanted, room) / total;
            for (var c = 0; c < columns; c++)
                widths[c] *= scale;
            total = widths.Sum();
        }
        // A fixed table in the body keeps its widths and runs into the margin, as Word lets it; one in a cell, and an
        // autofit table, gives up what its content doesn't need first.
        if (total > room + 0.5f && !(sizing.Fixed && _inCell == 0))
            Shrink(widths, content ?? Measure(table, placed, columns), room);
        return widths;
    }

    /// <summary>
    /// Each column's preferred width in points: the grid's, else the widest a cell of the column gives (a cell spanning
    /// only such columns spreads its own over them), else 0. The grid columns a row leaves empty before its first cell
    /// and after its last take the width the row gives them (Word's w:wBefore, w:wAfter) the same way.
    /// </summary>
    /// <param name="basis">What a cell's share of the table's width is a share of.</param>
    private static float[] Preferred(HTable table, HTableSizing sizing, List<(int Row, int Column, HCell Cell)> placed, int columns, float basis)
    {
        var widths = new float[columns];
        for (var c = 0; c < Math.Min(columns, sizing.Grid.Length); c++)
            widths[c] = Math.Max(0, sizing.Grid[c]) / 100f;
        var gridded = widths.Select(w => w > 0).ToArray();
        float? Given(HPreferredWidth? width) => width switch
        {
            { Type: HWidthType.Absolute, Value: var length } => (float)(length / 100),
            { Type: HWidthType.Percent, Value: var share } => (float)(share * basis),
            _ => null,
        };
        var given = new List<(int Column, int Span, float Width)>();
        // Each row's first column and the column after its last cell.
        var extents = new Dictionary<int, (int First, int End)>();
        foreach (var (row, column, cell) in placed)
        {
            if (column >= columns)
                continue;
            var span = Math.Clamp(cell.ColSpan, 1, columns - column);
            extents[row] = extents.TryGetValue(row, out var known) ? (Math.Min(known.First, column), Math.Max(known.End, column + span)) : (column, column + span);
            if (Given(cell.Width) is > 0 and var width)
                given.Add((column, span, width));
        }
        foreach (var (row, (first, end)) in extents)
        {
            if (row >= table.Rows.Count)
                continue;
            var source = table.Rows[row];
            if (first > 0 && Given(source.WidthBefore) is > 0 and var before)
                given.Add((0, first, before));
            if (source.GridAfter > 0 && end < columns && Given(source.WidthAfter) is > 0 and var after)
                given.Add((end, Math.Min(source.GridAfter, columns - end), after));
        }
        foreach (var (column, _, width) in given.Where(g => g.Span == 1))
        {
            if (!gridded[column])
                widths[column] = Math.Max(widths[column], width);
        }
        foreach (var (column, span, width) in given.Where(g => g.Span > 1).OrderBy(g => g.Span))
        {
            if (!gridded.AsSpan(column, span).Contains(true))
                Spread(widths, column, span, width);
        }
        return widths;
    }

    /// <summary>What each column's cells need (see <see cref="ContentWidths"/>); null when paragraphs aren't measured.</summary>
    private ContentWidths? Measure(HTable table, List<(int Row, int Column, HCell Cell)> placed, int columns)
    {
        if (measure is null)
            return null;
        if (_measured.TryGetValue(table, out var same) && same.Min.Length == columns)
            return same;
        var min = new float[columns];
        var max = new float[columns];
        var spanning = new List<(int Column, int Span, float Min, float Max)>();
        foreach (var (_, column, cell) in placed.Take(MaxMeasuredCells))
        {
            if (column >= columns)
                continue;
            var padding = cell.CellMargin ?? table.CellMargin ?? DefaultPadding;
            var sides = Math.Max(0, (padding.Left + padding.Right) / 100f) + 2 * Math.Max(0, table.CellSpacing / 100f);
            var (least, natural) = Measure(cell.Blocks);
            var span = Math.Clamp(cell.ColSpan, 1, columns - column);
            if (span > 1)
            {
                spanning.Add((column, span, least + sides, natural + sides));
                continue;
            }
            min[column] = Math.Max(min[column], least + sides);
            max[column] = Math.Max(max[column], natural + sides);
        }
        foreach (var (column, span, least, natural) in spanning.OrderBy(s => s.Span))
        {
            Spread(min, column, span, least);
            Spread(max, column, span, natural);
        }
        var widths = new ContentWidths(min, max);
        _measured[table] = widths;
        return widths;
    }

    /// <summary>The narrowest and the natural width of a list of blocks (a cell's): its widest paragraph or nested table.</summary>
    private (float Min, float Max) Measure(IReadOnlyList<HBlock> blocks)
    {
        float min = 0, max = 0;
        foreach (var block in blocks)
        {
            var (least, natural) = block switch
            {
                HParagraph p => measure!(p),
                HTable t when _depth < MaxDepth => Measure(t),
                _ => (0f, 0f),
            };
            min = Math.Max(min, least);
            max = Math.Max(max, natural);
        }
        return (min, max);
    }

    /// <summary>
    /// A nested table's narrowest and natural width: its columns at their longest words, and at their preferred widths
    /// (their content where they have none), with its indent and outer margins.
    /// </summary>
    private (float Min, float Max) Measure(HTable table)
    {
        _depth++;
        try
        {
            var placed = Place(table);
            var columns = Math.Max(1, Math.Max(Math.Min(table.ColumnCount, MaxColumns), placed.Count == 0 ? 0 : placed.Max(c => c.Column + ColumnSpan(c.Column, c.Cell))));
            var margin = table.OuterMargin ?? default;
            var around = Math.Max(0, (margin.Left + margin.Right) / 100f) + Math.Max(0, EdgeIndent(table));
            if (table.Sizing is not { } sizing)
            {
                var given = table.ColumnWidths is { } absolute ? absolute.Take(columns).Sum(w => Math.Max(0, w) / 100f) : 0;
                return (around + given, around + given);
            }
            var content = Measure(table, placed, columns);
            var length = sizing.Width.Type == HWidthType.Absolute ? (float)(sizing.Width.Value / 100) : 0;
            var preferred = Preferred(table, sizing, placed, columns, length);
            float min = 0, max = 0;
            for (var c = 0; c < columns; c++)
            {
                var least = content?.Min[c] ?? 0;
                min += least;
                max += preferred[c] > 0 ? Math.Max(preferred[c], sizing.Fixed ? 0 : least) : content?.Max[c] ?? 0;
            }
            if (!sizing.Fixed)
                max = Math.Max(max, length);
            return (around + min, around + Math.Max(min, max));
        }
        finally
        {
            _depth--;
        }
    }

    /// <summary>Widens <paramref name="span"/> columns from <paramref name="from"/> to <paramref name="need"/> together, each by its share.</summary>
    private static void Spread(float[] widths, int from, int span, float need)
    {
        var have = 0f;
        for (var c = from; c < from + span; c++)
            have += widths[c];
        if (need <= have)
            return;
        var extra = need - have;
        for (var c = from; c < from + span; c++)
            widths[c] += have > 0 ? extra * widths[c] / have : extra / span;
    }

    /// <summary>
    /// Narrows a table to <paramref name="room"/>: first what its columns have beyond their widest line, then down to
    /// their longest words, each column by its share of what it can give; then all of them evenly.
    /// </summary>
    private static void Shrink(float[] widths, ContentWidths? content, float room)
    {
        var total = widths.Sum();
        if (content is { } needs)
        {
            foreach (var natural in (bool[])[true, false])
            {
                float Floor(int c) => natural ? Math.Max(needs.Max[c], needs.Min[c]) : needs.Min[c];
                var spare = 0f;
                for (var c = 0; c < widths.Length; c++)
                    spare += Math.Max(0, widths[c] - Floor(c));
                if (spare <= 0)
                    continue;
                var cut = Math.Min(spare, total - room);
                for (var c = 0; c < widths.Length; c++)
                    widths[c] -= cut * Math.Max(0, widths[c] - Floor(c)) / spare;
                total = widths.Sum();
                if (total <= room + 0.01f)
                    return;
            }
        }
        if (total > 0)
        {
            for (var c = 0; c < widths.Length; c++)
                widths[c] *= room / total;
        }
    }

    // ───────────────────────── Splitting ─────────────────────────

    /// <summary>
    /// A one-row group split where <paramref name="available"/> points are left: the part that fits and the rest, or
    /// null when nothing of any cell fits.
    /// </summary>
    public static (RowBox First, RowBox Remaining)? Split(RowBox row, float available)
    {
        var first = new List<CellBox>();
        var rest = new List<CellBox>();
        var any = false;
        foreach (var cell in row.Cells)
        {
            var room = available - (cell.Padding.Top + cell.Padding.Bottom) / 100f - 2 * cell.Spacing;
            var fits = cell.Content.Where(p => p.Top + p.Height <= room + 0.01f).ToList();
            var after = cell.Content.Skip(fits.Count).ToList();
            any |= fits.Count > 0;
            var shift = after.Count > 0 ? after[0].Top : 0;
            first.Add(Copy(cell, fits));
            rest.Add(Copy(cell, [.. after.Select(p => p with { Top = p.Top - shift })], after.Count > 0 ? cell.Extent - shift : null));
        }
        if (!any)
            return null;
        var firstRow = new RowBox
        {
            Height = available,
            Cells = first,
            Header = row.Header,
            CantSplit = row.CantSplit,
            Frame = row.Frame,
            FrameTop = row.FrameTop,
        };
        var restRow = new RowBox
        {
            Height = rest.Select(c => c.Needed).DefaultIfEmpty(0).Max(),
            Cells = rest,
            Header = row.Header,
            CantSplit = row.CantSplit,
            Frame = row.Frame,
            FrameBottom = row.FrameBottom,
        };
        return (firstRow, restRow);
    }

    private static CellBox Copy(CellBox cell, IReadOnlyList<Piece> content, float? extent = null) => new()
    {
        Row = cell.Row,
        RowSpan = 1,
        X = cell.X,
        Width = cell.Width,
        Padding = cell.Padding,
        VerticalAlign = HVerticalAlign.Top,
        Borders = cell.Borders,
        Fill = cell.Fill,
        FillPicture = cell.FillPicture,
        Content = content,
        Extent = extent,
        Spacing = cell.Spacing,
    };

    // ───────────────────────── Drawing ─────────────────────────

    /// <summary>Draws consecutive rows with the table's top-left corner at (<paramref name="x"/>, <paramref name="top"/>).</summary>
    public static void Draw(PageContent page, IReadOnlyList<RowBox> rows, float x, float top)
    {
        var tops = new float[rows.Count + 1];
        tops[0] = top;
        for (var r = 0; r < rows.Count; r++)
            tops[r + 1] = tops[r] + rows[r].Height;
        var cells = new List<(CellBox Cell, SKRect Area)>();
        for (var r = 0; r < rows.Count; r++)
        {
            foreach (var cell in rows[r].Cells)
            {
                var bottom = tops[Math.Min(rows.Count, r + cell.RowSpan)];
                cells.Add((cell, new SKRect(x + cell.X, tops[r] + cell.Spacing, x + cell.X + cell.Width, Math.Max(tops[r] + cell.Spacing, bottom - cell.Spacing))));
            }
        }
        var fills = cells.Where(c => c.Cell.Fill is not null || c.Cell.FillPicture is not null).ToList();
        if (fills.Count > 0)
        {
            page.Draw(canvas =>
            {
                using var paint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = false };
                foreach (var (cell, area) in fills)
                {
                    if (cell.Fill is { } color)
                    {
                        paint.Color = color;
                        canvas.DrawRect(area, paint);
                    }
                    if (cell.FillPicture is not { } path || !File.Exists(path))
                        continue;
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
            });
        }
        foreach (var (cell, area) in cells)
        {
            var inner = area.Height - (cell.Padding.Top + cell.Padding.Bottom) / 100f;
            var extra = Math.Max(0, inner - cell.Held);
            var shift = cell.VerticalAlign switch
            {
                HVerticalAlign.Center => extra / 2,
                HVerticalAlign.Bottom => extra,
                _ => 0f,
            };
            var left = area.Left + cell.Padding.Left / 100f;
            var contentTop = area.Top + cell.Padding.Top / 100f + shift;
            foreach (var piece in cell.Content)
                piece.Draw(page, left, contentTop + piece.Top);
        }
        page.Draw(canvas =>
        {
            foreach (var (cell, area) in cells)
                Borders(canvas, cell.Borders, area);
            // The table's own border around cells spaced apart: at each row's sides, over the first and under the last.
            for (var r = 0; r < rows.Count; r++)
            {
                if (rows[r].Frame is not { } around)
                    continue;
                var (left, right) = (x, x + around.Width);
                Line(canvas, around.Borders.Left, left, tops[r], left, tops[r + 1]);
                Line(canvas, around.Borders.Right, right, tops[r], right, tops[r + 1]);
                if (rows[r].FrameTop)
                    Line(canvas, around.Borders.Top, left, tops[r], right, tops[r]);
                if (rows[r].FrameBottom)
                    Line(canvas, around.Borders.Bottom, left, tops[r + 1], right, tops[r + 1]);
            }
        });
    }

    /// <summary>Draws the four borders of a cell (or of a page).</summary>
    internal static void Borders(SKCanvas canvas, HBorders borders, SKRect area)
    {
        Line(canvas, borders.Top, area.Left, area.Top, area.Right, area.Top);
        Line(canvas, borders.Bottom, area.Left, area.Bottom, area.Right, area.Bottom);
        Line(canvas, borders.Left, area.Left, area.Top, area.Left, area.Bottom);
        Line(canvas, borders.Right, area.Right, area.Top, area.Right, area.Bottom);
    }

    /// <summary>Draws one border line; <paramref name="phase"/> starts its dashes as far into their pattern (to join up parts of one line).</summary>
    internal static void Line(SKCanvas canvas, HBorder border, float x0, float y0, float x1, float y1, float phase = 0)
    {
        if (border.Style == HBorderStyle.None)
            return;
        var width = Math.Max(0.25f, (float)(border.WidthMm * 72 / 25.4));
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            Color = ParagraphText.Color(border.Color) ?? SKColors.Black,
            IsAntialias = true,
        };
        if (border.Style == HBorderStyle.Double)
        {
            // Two lines of a third of the width each, a third apart.
            paint.StrokeWidth = Math.Max(0.25f, width / 3);
            var (dx, dy) = y0 == y1 ? (0f, width / 3) : (width / 3, 0f);
            canvas.DrawLine(x0 - dx, y0 - dy, x1 - dx, y1 - dy, paint);
            canvas.DrawLine(x0 + dx, y0 + dy, x1 + dx, y1 + dy, paint);
            return;
        }
        paint.StrokeWidth = width;
        float[]? dashes = border.Style switch
        {
            HBorderStyle.Dash => [width * 4, width * 2],
            HBorderStyle.Dot => [width, width * 2],
            HBorderStyle.DashDot => [width * 4, width * 2, width, width * 2],
            _ => null,
        };
        using var effect = dashes is null ? null : SKPathEffect.CreateDash(dashes, phase % dashes.Sum());
        paint.PathEffect = effect;
        canvas.DrawLine(x0, y0, x1, y1, paint);
    }
}
