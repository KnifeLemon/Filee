// Lays out tables (GitHub Project 4, item "Layout engine: tables") for the page composer, and stacks the blocks of
// any paragraph list (a cell, a header or footer) into pieces the composer and cells draw.
//
// - Grid: cells take their column (or the next free one, skipping cells that span down from above), their column and
//   row spans. Column widths are the table's absolute widths, else its relative ones (columns without one share what
//   is left), else equal shares of the width. Tables of other formats (a spreadsheet's columns) are scaled down to the
//   text width when wider; 한글's keep the widths 한글 gave them, as 한글 draws them.
// - Cells: their blocks are laid out at the columns' width less the padding (the cell's, else the table's, else
//   한글's default 1.8 mm by 0.5 mm), nested tables too; a row is as high as its tallest single-row cell or its own
//   height, whichever is more, and a cell spanning rows makes its last row higher when the rows are short of it.
//   Content sits at the top, middle or bottom of the cell.
// - Drawing: each cell's fill (a colour, a picture; EMF / WMF as vector drawing), then its content, then its borders (the cell's, else the table's, else a thin grid);
//   solid, dashed, dotted, dash-dot and double lines at their width.
// - Pages: rows bound by a cell spanning them form a group that moves to a page together. A table that must not split
//   (한글 나누지 않음) goes to the next page whole when it fits on one; others split between groups (한글 셀 단위로
//   나눔), and a one-row group taller than what is left splits inside its cells where the rows may split (한글 나눔,
//   and sources that don't say, as Word allows) unless the row must not split. Header rows repeat on every page.

using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Metafiles;
using Filee.Engines.Rendering;
using SkiaSharp;

namespace Filee.Engines.Typesetting;

/// <summary>Something drawn at <c>Top</c> in a stack, <c>Height</c> high: a line, or a row of a nested table.</summary>
/// <param name="Draw">Draws the piece with its stack's top-left corner at the given point.</param>
/// <param name="Below">Space at the bottom of the piece that is no ink (a line's spacing below its text).</param>
internal sealed record Piece(float Top, float Height, Action<PageContent, float, float> Draw, float Below = 0);

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

    /// <summary>The height the cell needs: its content without the spacing below the last line, and its padding (as 한글 sizes cells).</summary>
    public float Needed => InkHeight + (Padding.Top + Padding.Bottom) / 100f;
}

/// <summary>A row: its height and the cells that start in it.</summary>
internal sealed class RowBox
{
    public required float Height { get; set; }
    public required IReadOnlyList<CellBox> Cells { get; init; }
    public bool Header { get; init; }
    public bool CantSplit { get; init; }
}

/// <summary>A laid out table.</summary>
/// <param name="Offset">Left edge from the column's left edge.</param>
internal sealed record TableBox(float Offset, float Width, IReadOnlyList<RowBox> Rows, HTablePageBreak? PageBreak, IReadOnlyList<Piece> Caption)
{
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
internal sealed class TableLayout(Func<HParagraph, float, ParagraphBox> paragraph, bool fitToWidth, Func<AnchorFrame>? frame = null)
{
    /// <summary>한글's default cell padding: 1.8 mm left and right, 0.5 mm top and bottom (HWPUNIT).</summary>
    private static readonly HInsets DefaultPadding = new(510, 510, 141, 141);

    /// <summary>Tables nested deeper than this inside cells are left out (a damaged or hostile file).</summary>
    private const int MaxDepth = 16;

    private int _depth;

    // ───────────────────────── Stacks ─────────────────────────

    /// <summary>
    /// The blocks of a paragraph list stacked from the top in a column <paramref name="width"/> wide. Lines on 한글's
    /// stored layout take their stored place while the list so far follows it, as on a page. Floating objects come
    /// first (behind the text) or last (in front of it).
    /// </summary>
    public List<Piece> Stack(IReadOnlyList<HBlock> blocks, float width)
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
        for (var i = 0; i < blocks.Count; i++)
        {
            switch (blocks[i])
            {
                case HParagraph p when HoldsTable(blocks, i):
                    holder = p;
                    break;
                case HParagraph p:
                    var box = paragraph(p, width);
                    y += skipBefore ? 0 : box.SpaceBefore;
                    skipBefore = false;
                    if (box.Lines.Count == 0)
                        Objects(p, y);
                    foreach (var line in box.Lines)
                    {
                        if (box.StoredLines && inStep && line.StoredTop is { } top)
                            y = Math.Max(y, top);
                        if (line == box.Lines[0])
                            Objects(p, y);
                        pieces.Add(new Piece(y, line.Height, line.Draw, line.Below));
                        y += line.Height;
                    }
                    y += box.SpaceAfter;
                    inStep &= box.StoredLines;
                    break;
                case HTable t when _depth < MaxDepth:
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
        pieces.InsertRange(0, behind.OrderBy(o => o.Item1).Select(o => o.Item2));
        pieces.AddRange(front.OrderBy(o => o.Item1).Select(o => o.Item2));
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
        var columns = Math.Max(1, Math.Max(table.ColumnCount, placed.Count == 0 ? 0 : placed.Max(c => c.Column + c.Cell.ColSpan)));
        var widths = ColumnWidths(table, columns, width);
        var left = new float[columns + 1];
        for (var c = 0; c < columns; c++)
            left[c + 1] = left[c] + widths[c];
        var tableWidth = left[columns];

        var rowCount = table.Rows.Count;
        var cells = new List<CellBox>[rowCount];
        for (var r = 0; r < rowCount; r++)
            cells[r] = [];
        foreach (var (row, column, cell) in placed)
        {
            var span = Math.Min(cell.ColSpan, columns - column);
            var padding = cell.CellMargin ?? table.CellMargin ?? DefaultPadding;
            var cellWidth = left[column + span] - left[column];
            var contentWidth = Math.Max(1, cellWidth - (padding.Left + padding.Right) / 100f);
            cells[row].Add(new CellBox
            {
                Row = row,
                RowSpan = Math.Max(1, Math.Min(cell.RowSpan, rowCount - row)),
                X = left[column],
                Width = cellWidth,
                Padding = padding,
                VerticalAlign = cell.VerticalAlign,
                Borders = cell.Borders ?? table.Borders ?? HBorders.Grid,
                Fill = ParagraphText.Color(cell.Fill),
                FillPicture = cell.FillPicture,
                Content = Stack(cell.Blocks, contentWidth),
            });
        }

        // Row heights: the row's own, its single-row cells; then the cells spanning rows stretch their last row.
        var rows = new List<RowBox>(rowCount);
        for (var r = 0; r < rowCount; r++)
        {
            var source = table.Rows[r];
            var height = Math.Max((source.Height ?? 0) / 100f, cells[r].Where(c => c.RowSpan == 1).Select(c => c.Needed).DefaultIfEmpty(0).Max());
            rows.Add(new RowBox { Height = height, Cells = cells[r], Header = source.Header, CantSplit = source.CantSplit });
        }
        foreach (var cell in cells.SelectMany(c => c).Where(c => c.RowSpan > 1).OrderBy(c => c.Row + c.RowSpan))
        {
            var have = rows.Skip(cell.Row).Take(cell.RowSpan).Sum(r => r.Height);
            if (cell.Needed > have)
                rows[cell.Row + cell.RowSpan - 1].Height += cell.Needed - have;
        }

        var offset = table.Align switch
        {
            HAlign.Center => (width - tableWidth) / 2,
            HAlign.Right => width - tableWidth,
            _ => table.Indent / 100f,
        };
        var caption = table.Caption.Count > 0 ? Stack(table.Caption, tableWidth) : [];
        return new TableBox(Math.Max(0, offset), tableWidth, rows, table.PageBreak, caption);
    }

    /// <summary>Each cell's row and grid column.</summary>
    private static List<(int Row, int Column, HCell Cell)> Place(HTable table)
    {
        var placed = new List<(int, int, HCell)>();
        // Columns taken by cells spanning down from rows above, by row.
        var taken = new Dictionary<int, HashSet<int>>();
        HashSet<int> Taken(int row) => taken.TryGetValue(row, out var set) ? set : taken[row] = [];
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var next = 0;
            foreach (var cell in table.Rows[r].Cells)
            {
                var column = cell.Column;
                if (column < 0)
                {
                    column = next;
                    while (Taken(r).Contains(column))
                        column++;
                }
                placed.Add((r, column, cell));
                for (var dr = 0; dr < Math.Max(1, cell.RowSpan) && r + dr < table.Rows.Count; dr++)
                {
                    for (var dc = 0; dc < Math.Max(1, cell.ColSpan); dc++)
                        Taken(r + dr).Add(column + dc);
                }
                next = column + Math.Max(1, cell.ColSpan);
            }
        }
        return placed;
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
            var room = available - (cell.Padding.Top + cell.Padding.Bottom) / 100f;
            var fits = cell.Content.Where(p => p.Top + p.Height <= room + 0.01f).ToList();
            var after = cell.Content.Skip(fits.Count).ToList();
            any |= fits.Count > 0;
            var shift = after.Count > 0 ? after[0].Top : 0;
            first.Add(Copy(cell, fits));
            rest.Add(Copy(cell, [.. after.Select(p => p with { Top = p.Top - shift })]));
        }
        if (!any)
            return null;
        var firstRow = new RowBox { Height = available, Cells = first, Header = row.Header, CantSplit = row.CantSplit };
        var restRow = new RowBox { Height = rest.Select(c => c.Needed).DefaultIfEmpty(0).Max(), Cells = rest, Header = row.Header, CantSplit = row.CantSplit };
        return (firstRow, restRow);
    }

    private static CellBox Copy(CellBox cell, IReadOnlyList<Piece> content) => new()
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
                cells.Add((cell, new SKRect(x + cell.X, tops[r], x + cell.X + cell.Width, bottom)));
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
                    else if (SKImage.FromEncodedData(path) is { } image)
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
            var extra = Math.Max(0, inner - cell.InkHeight);
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
        });
    }

    /// <summary>Draws the four borders of a cell.</summary>
    private static void Borders(SKCanvas canvas, HBorders borders, SKRect area)
    {
        Line(canvas, borders.Top, area.Left, area.Top, area.Right, area.Top);
        Line(canvas, borders.Bottom, area.Left, area.Bottom, area.Right, area.Bottom);
        Line(canvas, borders.Left, area.Left, area.Top, area.Left, area.Bottom);
        Line(canvas, borders.Right, area.Right, area.Top, area.Right, area.Bottom);
    }

    private static void Line(SKCanvas canvas, HBorder border, float x0, float y0, float x1, float y1)
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
        using var effect = dashes is null ? null : SKPathEffect.CreateDash(dashes, 0);
        paint.PathEffect = effect;
        canvas.DrawLine(x0, y0, x1, y1, paint);
    }
}
