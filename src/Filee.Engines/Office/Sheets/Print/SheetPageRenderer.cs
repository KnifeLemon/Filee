// Draws one printed page of a sheet: the print titles and the page's cells at their place inside the margins (centred
// when the sheet says so), fills (solid, patterns as their mixed colour, gradients), the gridlines and the row and
// column headings when they print, borders (shared with the neighbour, merged areas only at their edges, diagonals),
// text (aligned, indented, wrapped, justified, shrunk, rotated or stacked; text overflowing into empty cells; numbers
// too wide for their column as ####), pictures, and the header and footer with their fields.

using System.Globalization;
using Filee.Engines.Rendering;
using SkiaSharp;

namespace Filee.Engines.Office.Sheets;

/// <summary>What the header and footer fields of a page show.</summary>
/// <param name="Pictures">Decoded header / footer pictures by section key.</param>
internal sealed record PageFields(int Number, int Total, string SheetName, string FileName, string Folder, DateTime Now,
    IReadOnlyDictionary<string, (SKImage Image, double Width, double Height)> Pictures);

internal sealed class SheetPageRenderer
{
    private readonly Workbook _book;
    private readonly SheetPrintPlan _plan;
    private readonly SheetIndex _index;
    private readonly SheetFonts _fonts;
    private readonly IReadOnlyDictionary<(int Row, int Column), CellStyle> _conditional;
    private readonly Dictionary<int, List<(int Column, CellStyle Style)>> _blanks = [];
    private readonly IReadOnlyList<(SheetPicture Picture, SKImage Image)> _pictures;
    private readonly float _scale;

    public SheetPageRenderer(Workbook book, SheetPrintPlan plan, SheetIndex index, SheetFonts fonts,
        IReadOnlyDictionary<(int, int), CellStyle> conditional, IReadOnlyList<(SheetPicture, SKImage)> pictures)
    {
        _book = book;
        _plan = plan;
        _index = index;
        _fonts = fonts;
        _conditional = conditional;
        // Blank cells a rule colours (containsBlanks) by row: the only conditional looks the cell index doesn't list.
        foreach (var ((row, column), style) in conditional)
        {
            if (index.Get(row, column) is not null)
                continue;
            if (!_blanks.TryGetValue(row, out var list))
                _blanks[row] = list = [];
            list.Add((column, style));
        }
        _pictures = pictures;
        _scale = (float)plan.Scale;
    }

    private Worksheet Sheet => _plan.Sheet;
    private SheetGrid Grid => _plan.Grid;

    /// <summary>A column or row placed on the page: its index and where it is (page points).</summary>
    private readonly record struct Placed(int Index, float Start, float Size)
    {
        public float End => Start + Size;
    }

    public PageContent Draw(SheetPage page, PageFields fields)
    {
        var setup = Sheet.PageSetup;
        var (width, height) = setup.PageSize;
        var content = new PageContent((float)width, (float)height);
        var s = _scale;

        var columnIndexes = Visible(page.RepeatColumns && Sheet.TitleColumns is { } tc ? Range(tc.First, tc.Last) : [], page.Left, page.Right, Grid.Widths);
        var rowIndexes = Visible(page.RepeatRows && Sheet.TitleRows is { } tr ? Range(tr.First, tr.Last) : [], page.Top, page.Bottom, Grid.Heights);
        var gridWidth = (float)(columnIndexes.Sum(c => Grid.Width(c)) * s);
        var gridHeight = (float)(rowIndexes.Sum(r => Grid.Height(r)) * s);
        var headingWidth = (float)(_plan.HeadingWidth * s);
        var headingHeight = (float)(_plan.HeadingHeight * s);

        var left = (float)setup.LeftMargin;
        var top = (float)setup.TopMargin;
        var areaWidth = (float)(width - setup.LeftMargin - setup.RightMargin);
        var areaHeight = (float)(height - setup.TopMargin - setup.BottomMargin);
        if (setup.CenterHorizontally)
            left += Math.Max(0, (areaWidth - gridWidth - headingWidth) / 2);
        if (setup.CenterVertically)
            top += Math.Max(0, (areaHeight - gridHeight - headingHeight) / 2);
        var gridLeft = left + headingWidth;
        var gridTop = top + headingHeight;

        // Columns from the left, or from the right on a right-to-left sheet.
        var columns = new List<Placed>();
        var x = Sheet.RightToLeft ? gridLeft + gridWidth : gridLeft;
        foreach (var c in columnIndexes)
        {
            var w = (float)(Grid.Width(c) * s);
            columns.Add(Sheet.RightToLeft ? new Placed(c, x - w, w) : new Placed(c, x, w));
            x += Sheet.RightToLeft ? -w : w;
        }
        var rows = new List<Placed>();
        var y = gridTop;
        foreach (var r in rowIndexes)
        {
            var h = (float)(Grid.Height(r) * s);
            rows.Add(new Placed(r, y, h));
            y += h;
        }
        var gridArea = new SKRect(gridLeft, gridTop, gridLeft + gridWidth, gridTop + gridHeight);
        var columnAt = columns.ToDictionary(p => p.Index);
        var rowAt = rows.ToDictionary(p => p.Index);

        // The header first and the footer last, so the page's text reads from top to bottom.
        DrawHeaderFooter(content, fields, (float)width, (float)height, header: true);
        if (columns.Count > 0 && rows.Count > 0)
        {
            var blocks = Blocks(columns, rows);
            DrawFills(content, blocks, columnAt, rowAt);
            if (setup.PrintGridLines)
                DrawGridLines(content, columns, rows, blocks);
            DrawBorders(content, blocks, columnAt, rowAt);
            DrawTexts(content, blocks, columnAt, rowAt, gridArea);
            DrawPictures(content, columns, rows, gridArea);
        }
        if (setup.PrintHeadings)
            DrawHeadings(content, columns, rows, gridLeft, gridTop, headingWidth, headingHeight);
        DrawHeaderFooter(content, fields, (float)width, (float)height, header: false);
        return content;
    }

    private static IEnumerable<int> Range(int first, int last) => Enumerable.Range(first, Math.Max(0, last - first + 1));

    /// <summary>The titles and the page's own indexes that aren't hidden.</summary>
    private static List<int> Visible(IEnumerable<int> titles, int first, int last, double[] sizes) =>
        [.. titles.Concat(Range(first, last)).Where(i => i >= 0 && i < sizes.Length && sizes[i] > 0)];

    /// <summary>
    /// The page in blocks of rows × columns that follow each other in the sheet (titles and the page's own cells are
    /// separate blocks; hidden ones between don't split a block), so cells and merges are looked up per block.
    /// </summary>
    private List<(int Top, int Bottom, int Left, int Right)> Blocks(List<Placed> columns, List<Placed> rows)
    {
        static List<(int, int)> Runs(List<Placed> placed, Func<int, int, double> span)
        {
            var runs = new List<(int, int)>();
            foreach (var p in placed)
            {
                if (runs.Count > 0 && p.Index > runs[^1].Item2 && span(runs[^1].Item2 + 1, p.Index) <= 0)
                    runs[^1] = (runs[^1].Item1, p.Index);
                else
                    runs.Add((p.Index, p.Index));
            }
            return runs;
        }
        var blocks = new List<(int, int, int, int)>();
        foreach (var (top, bottom) in Runs(rows, Grid.RowSpan))
            foreach (var (left, right) in Runs(columns, Grid.ColumnSpan))
                blocks.Add((top, bottom, left, right));
        return blocks;
    }

    /// <summary>The blank cells a conditional format changes in a block.</summary>
    private IEnumerable<(int Row, int Column, CellStyle Style)> Blanks(int top, int bottom, int left, int right)
    {
        if (_blanks.Count == 0)
            yield break;
        for (var row = top; row <= bottom; row++)
        {
            if (!_blanks.TryGetValue(row, out var list))
                continue;
            foreach (var (column, style) in list)
            {
                if (column >= left && column <= right)
                    yield return (row, column, style);
            }
        }
    }

    private CellStyle StyleAt(int row, int column, SheetCell? cell) =>
        _conditional.TryGetValue((row, column), out var conditional) ? conditional : cell?.Style ?? CellStyle.Default;

    private SKRect CellRect(Placed column, Placed row) => new(column.Start, row.Start, column.End, row.End);

    /// <summary>
    /// Where a merged area lies on the page, whole (also the part off the page, for centring its text) and the part
    /// that shows; null when none of it shows.
    /// </summary>
    private (SKRect Whole, SKRect Shown)? MergeRect(CellRange merge, Dictionary<int, Placed> columnAt, Dictionary<int, Placed> rowAt)
    {
        var shownColumns = columnAt.Values.Where(p => p.Index >= merge.Left && p.Index <= merge.Right).ToList();
        var shownRows = rowAt.Values.Where(p => p.Index >= merge.Top && p.Index <= merge.Bottom).ToList();
        if (shownColumns.Count == 0 || shownRows.Count == 0)
            return null;
        var shown = new SKRect(shownColumns.Min(p => p.Start), shownRows.Min(p => p.Start), shownColumns.Max(p => p.End), shownRows.Max(p => p.End));
        var firstColumn = shownColumns.MinBy(p => p.Index);
        var firstRow = shownRows.MinBy(p => p.Index);
        var before = (float)(Grid.ColumnSpan(merge.Left, firstColumn.Index) * _scale);
        var wide = (float)(Grid.ColumnSpan(merge.Left, merge.Right + 1) * _scale);
        var above = (float)(Grid.RowSpan(merge.Top, firstRow.Index) * _scale);
        var tall = (float)(Grid.RowSpan(merge.Top, merge.Bottom + 1) * _scale);
        var wholeLeft = Sheet.RightToLeft ? firstColumn.End + before - wide : firstColumn.Start - before;
        var whole = new SKRect(wholeLeft, firstRow.Start - above, wholeLeft + wide, firstRow.Start - above + tall);
        return (whole, shown);
    }

    // ───────────────────────── Fills ─────────────────────────

    private void DrawFills(PageContent page, List<(int Top, int Bottom, int Left, int Right)> blocks, Dictionary<int, Placed> columnAt, Dictionary<int, Placed> rowAt)
    {
        var fills = new List<(SKRect Rect, CellStyle Style)>();
        var mergesDone = new HashSet<CellRange>();
        foreach (var (top, bottom, left, right) in blocks)
        {
            foreach (var (row, column, cell) in _index.Cells(top, bottom, left, right))
            {
                if (_index.Merge(row, column) is not null || !columnAt.TryGetValue(column, out var c) || !rowAt.TryGetValue(row, out var r))
                    continue;
                var style = StyleAt(row, column, cell);
                if (style.Fill is not null || style.Gradient is not null)
                    fills.Add((CellRect(c, r), style));
            }
            foreach (var merge in _index.Merges(top, bottom, left, right))
            {
                if (!mergesDone.Add(merge) || MergeRect(merge, columnAt, rowAt) is not { } rect)
                    continue;
                var anchor = _index.Get(merge.Top, merge.Left);
                var style = StyleAt(merge.Top, merge.Left, anchor);
                if (style.Fill is not null || style.Gradient is not null)
                    fills.Add((rect.Shown, style));
            }
            // Conditional fills on blank cells (containsBlanks).
            foreach (var (row, column, style) in Blanks(top, bottom, left, right))
            {
                if (style.Fill is null || _index.Merge(row, column) is not null || !columnAt.TryGetValue(column, out var c) || !rowAt.TryGetValue(row, out var r))
                    continue;
                fills.Add((CellRect(c, r), style));
            }
        }
        if (fills.Count == 0)
            return;
        page.Draw(canvas =>
        {
            using var paint = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
            foreach (var (rect, style) in fills)
            {
                if (style.Gradient is { Stops.Count: >= 2 } gradient)
                {
                    var angle = gradient.Degree * Math.PI / 180;
                    var (dx, dy) = ((float)Math.Cos(angle), (float)Math.Sin(angle));
                    var (cx, cy) = (rect.MidX, rect.MidY);
                    var half = (Math.Abs(dx) * rect.Width + Math.Abs(dy) * rect.Height) / 2;
                    using var shader = SKShader.CreateLinearGradient(new SKPoint(cx - dx * half, cy - dy * half), new SKPoint(cx + dx * half, cy + dy * half),
                        [.. gradient.Stops.Select(stop => SheetColors.Parse(stop.Color, SKColors.White))],
                        [.. gradient.Stops.Select(stop => (float)Math.Clamp(stop.Position, 0, 1))], SKShaderTileMode.Clamp);
                    paint.Shader = shader;
                    canvas.DrawRect(rect, paint);
                    paint.Shader = null;
                    continue;
                }
                paint.Color = FillColor(style);
                canvas.DrawRect(rect, paint);
            }
        });
    }

    /// <summary>A solid fill's colour; a pattern as the mix of its colour and background it looks like from afar.</summary>
    internal static SKColor FillColor(CellStyle style)
    {
        var color = SheetColors.Parse(style.Fill, SKColors.White);
        if (style.FillPattern is not { } pattern)
            return color;
        var density = pattern switch
        {
            "darkGray" => 0.75,
            "mediumGray" => 0.5,
            "lightGray" => 0.25,
            "gray125" => 0.125,
            "gray0625" => 0.0625,
            "darkHorizontal" or "darkVertical" or "darkDown" or "darkUp" or "darkGrid" or "darkTrellis" => 0.5,
            _ => 0.25,
        };
        var background = SheetColors.Parse(style.PatternBackground, SKColors.White);
        byte Mix(byte a, byte b) => (byte)Math.Round(b + (a - b) * density);
        return new SKColor(Mix(color.Red, background.Red), Mix(color.Green, background.Green), Mix(color.Blue, background.Blue));
    }

    // ───────────────────────── Gridlines and borders ─────────────────────────

    private void DrawGridLines(PageContent page, List<Placed> columns, List<Placed> rows, List<(int Top, int Bottom, int Left, int Right)> blocks)
    {
        // Merged areas have no gridlines inside: the lines are drawn in pieces around them.
        var merges = blocks.SelectMany(b => _index.Merges(b.Top, b.Bottom, b.Left, b.Right)).Distinct().ToList();
        var vertical = new List<(float X, float Y0, float Y1)>();
        var horizontal = new List<(float Y, float X0, float X1)>();
        var xs = columns.SelectMany(c => new[] { c.Start, c.End }).Distinct().Order().ToList();
        var ys = rows.SelectMany(r => new[] { r.Start, r.End }).Distinct().Order().ToList();
        foreach (var gx in xs)
            foreach (var row in rows)
                vertical.Add((gx, row.Start, row.End));
        foreach (var gy in ys)
            foreach (var column in columns)
                horizontal.Add((gy, column.Start, column.End));
        if (merges.Count > 0)
        {
            var columnAt = columns.ToDictionary(p => p.Index);
            var rowAt = rows.ToDictionary(p => p.Index);
            var inside = merges.Select(m => MergeRect(m, columnAt, rowAt)).Where(r => r is not null).Select(r => r!.Value.Shown).ToList();
            vertical.RemoveAll(v => inside.Any(m => v.X > m.Left + 0.01f && v.X < m.Right - 0.01f && v.Y0 >= m.Top - 0.01f && v.Y1 <= m.Bottom + 0.01f));
            horizontal.RemoveAll(h => inside.Any(m => h.Y > m.Top + 0.01f && h.Y < m.Bottom - 0.01f && h.X0 >= m.Left - 0.01f && h.X1 <= m.Right + 0.01f));
        }
        page.Draw(canvas =>
        {
            using var paint = new SKPaint { Color = SKColors.Black, StrokeWidth = 0.4f, Style = SKPaintStyle.Stroke, IsAntialias = true };
            foreach (var (gx, y0, y1) in vertical)
                canvas.DrawLine(gx, y0, gx, y1, paint);
            foreach (var (gy, x0, x1) in horizontal)
                canvas.DrawLine(x0, gy, x1, gy, paint);
        });
    }

    private void DrawBorders(PageContent page, List<(int Top, int Bottom, int Left, int Right)> blocks, Dictionary<int, Placed> columnAt, Dictionary<int, Placed> rowAt)
    {
        var lines = new List<(CellBorder Border, SKPoint From, SKPoint To)>();
        var rtl = Sheet.RightToLeft;
        foreach (var (top, bottom, left, right) in blocks)
        {
            // Every cell of the block that has borders, and the edges of the block where a neighbour off the page has one.
            var positions = new HashSet<(int, int)>();
            foreach (var (row, column, _) in _index.Cells(top, bottom, left, right))
                positions.Add((row, column));
            foreach (var (row, column, _) in _index.Cells(top, bottom, left - 1, left - 1))
                positions.Add((row, left));
            foreach (var (row, column, _) in _index.Cells(top, bottom, right + 1, right + 1))
                positions.Add((row, right));
            foreach (var (row, column, _) in _index.Cells(top - 1, top - 1, left, right))
                positions.Add((top, column));
            foreach (var (row, column, _) in _index.Cells(bottom + 1, bottom + 1, left, right))
                positions.Add((bottom, column));
            foreach (var (row, column, _) in Blanks(top, bottom, left, right))
                positions.Add((row, column));
            foreach (var (row, column) in positions)
            {
                if (!columnAt.TryGetValue(column, out var c) || !rowAt.TryGetValue(row, out var r))
                    continue;
                var style = StyleAt(row, column, _index.Get(row, column));
                var merge = _index.Merge(row, column);
                var rect = CellRect(c, r);
                var (leftX, rightX) = rtl ? (rect.Right, rect.Left) : (rect.Left, rect.Right);
                // A side inside a merged area isn't drawn; the neighbour's border on a shared side counts.
                if (merge is not { } m || column == m.Left)
                    Add(lines, Pick(style.Left, StyleAt(row, column - 1, _index.Get(row, column - 1)).Right), new SKPoint(leftX, rect.Top), new SKPoint(leftX, rect.Bottom));
                if (merge is not { } m2 || column == m2.Right)
                    Add(lines, Pick(style.Right, StyleAt(row, column + 1, _index.Get(row, column + 1)).Left), new SKPoint(rightX, rect.Top), new SKPoint(rightX, rect.Bottom));
                if (merge is not { } m3 || row == m3.Top)
                    Add(lines, Pick(style.Top, StyleAt(row - 1, column, _index.Get(row - 1, column)).Bottom), new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Top));
                if (merge is not { } m4 || row == m4.Bottom)
                    Add(lines, Pick(style.Bottom, StyleAt(row + 1, column, _index.Get(row + 1, column)).Top), new SKPoint(rect.Left, rect.Bottom), new SKPoint(rect.Right, rect.Bottom));
                if (merge is null)
                {
                    Add(lines, style.DiagonalDown, new SKPoint(leftX, rect.Top), new SKPoint(rightX, rect.Bottom));
                    Add(lines, style.DiagonalUp, new SKPoint(leftX, rect.Bottom), new SKPoint(rightX, rect.Top));
                }
            }
            foreach (var merge in _index.Merges(top, bottom, left, right))
            {
                var anchor = StyleAt(merge.Top, merge.Left, _index.Get(merge.Top, merge.Left));
                if ((anchor.DiagonalDown.IsVisible || anchor.DiagonalUp.IsVisible) && MergeRect(merge, columnAt, rowAt) is { } area && merge.Top >= top && merge.Left >= left)
                {
                    var (leftX, rightX) = rtl ? (area.Whole.Right, area.Whole.Left) : (area.Whole.Left, area.Whole.Right);
                    Add(lines, anchor.DiagonalDown, new SKPoint(leftX, area.Whole.Top), new SKPoint(rightX, area.Whole.Bottom));
                    Add(lines, anchor.DiagonalUp, new SKPoint(leftX, area.Whole.Bottom), new SKPoint(rightX, area.Whole.Top));
                }
            }
        }
        if (lines.Count == 0)
            return;
        var scale = Math.Max(0.4f, _scale);
        page.Draw(canvas =>
        {
            foreach (var (border, from, to) in lines.Distinct())
                DrawBorder(canvas, border, from, to, scale);
        });
    }

    private static CellBorder Pick(CellBorder own, CellBorder neighbour) => own.IsVisible ? own : neighbour.IsVisible ? neighbour : CellBorder.None;

    private static void Add(List<(CellBorder, SKPoint, SKPoint)> lines, CellBorder border, SKPoint from, SKPoint to)
    {
        if (border.IsVisible)
            lines.Add((border, from, to));
    }

    /// <summary>Excel's border styles: thin 1 px, medium 2 px, thick 3 px, hair, dots, dashes and double lines.</summary>
    internal static void DrawBorder(SKCanvas canvas, CellBorder border, SKPoint from, SKPoint to, float scale)
    {
        var (width, dashes) = border.Style switch
        {
            "hair" => (0.25f, (float[]?)null),
            "medium" => (1.5f, null),
            "thick" => (2.25f, null),
            "dotted" => (0.75f, [0.75f, 0.75f]),
            "dashed" => (0.75f, [2.25f, 0.75f]),
            "mediumDashed" => (1.5f, [6.75f, 2.25f]),
            "dashDot" => (0.75f, [6.75f, 2.25f, 2.25f, 2.25f]),
            "mediumDashDot" or "slantDashDot" => (1.5f, [6.75f, 2.25f, 2.25f, 2.25f]),
            "dashDotDot" => (0.75f, [6.75f, 2.25f, 2.25f, 2.25f, 2.25f, 2.25f]),
            "mediumDashDotDot" => (1.5f, [6.75f, 2.25f, 2.25f, 2.25f, 2.25f, 2.25f]),
            _ => (0.75f, null),
        };
        using var paint = new SKPaint
        {
            Color = SheetColors.Parse(border.Color, SKColors.Black),
            StrokeWidth = width * scale,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
        };
        using var effect = dashes is null ? null : SKPathEffect.CreateDash([.. dashes.Select(d => d * scale)], 0);
        paint.PathEffect = effect;
        if (border.Style == "double")
        {
            // Two lines 1 px wide with 1 px between them.
            paint.StrokeWidth = 0.75f * scale;
            var dx = to.X - from.X;
            var dy = to.Y - from.Y;
            var length = MathF.Max(0.001f, MathF.Sqrt(dx * dx + dy * dy));
            var (nx, ny) = (-dy / length * 0.75f * scale, dx / length * 0.75f * scale);
            canvas.DrawLine(from.X - nx, from.Y - ny, to.X - nx, to.Y - ny, paint);
            canvas.DrawLine(from.X + nx, from.Y + ny, to.X + nx, to.Y + ny, paint);
            return;
        }
        canvas.DrawLine(from, to, paint);
    }

    // ───────────────────────── Text ─────────────────────────

    private void DrawTexts(PageContent page, List<(int Top, int Bottom, int Left, int Right)> blocks,
        Dictionary<int, Placed> columnAt, Dictionary<int, Placed> rowAt, SKRect gridArea)
    {
        var mergesDone = new HashSet<CellRange>();
        foreach (var (top, bottom, left, right) in blocks)
        {
            foreach (var (row, column, cell) in _index.Cells(top, bottom, left, right))
            {
                if (cell.Text.Length == 0 || _index.Merge(row, column) is not null || !columnAt.TryGetValue(column, out var c) || !rowAt.TryGetValue(row, out var r))
                    continue;
                var rect = CellRect(c, r);
                var style = StyleAt(row, column, cell);
                var (overflowLeft, overflowRight) = Overflow(row, column, left, right, columnAt, style, cell);
                DrawCell(page, cell, style, rect, rect, overflowLeft, overflowRight, gridArea);
            }
            foreach (var merge in _index.Merges(top, bottom, left, right))
            {
                if (!mergesDone.Add(merge) || _index.Get(merge.Top, merge.Left) is not { Text.Length: > 0 } anchor || MergeRect(merge, columnAt, rowAt) is not { } area)
                    continue;
                DrawCell(page, anchor, StyleAt(merge.Top, merge.Left, anchor), area.Whole, area.Shown, area.Whole.Left, area.Whole.Right, gridArea);
            }
        }
    }

    /// <summary>
    /// How far a cell's text may run over its neighbours (Excel's overflow): to the next cell with text, the end of the
    /// block on the page, or a merged area; not for wrapped, rotated, filled, shrunk or numeric text.
    /// </summary>
    private (float Left, float Right) Overflow(int row, int column, int blockLeft, int blockRight,
        Dictionary<int, Placed> columnAt, CellStyle style, SheetCell cell)
    {
        var own = columnAt[column];
        if (cell.IsNumber || CellText.Wraps(style) || style.Rotation != 0 || style.Shrink || style.SpecialAlign == CellSpecialAlign.Fill)
            return (own.Start, own.End);
        var align = Horizontal(style, cell);
        var rtl = Sheet.RightToLeft;
        // In sheet columns: does the text run towards higher columns (right in a left-to-right sheet), lower ones, or both?
        bool towardsHigher = align is CellAlign.Left or CellAlign.Center, towardsLower = align is CellAlign.Right or CellAlign.Center;
        if (rtl)
            (towardsHigher, towardsLower) = (align is CellAlign.Right or CellAlign.Center, align is CellAlign.Left or CellAlign.Center);
        if (style.SpecialAlign == CellSpecialAlign.CenterAcross)
            (towardsHigher, towardsLower) = (true, false);
        var highest = column;
        if (towardsHigher)
        {
            var stop = _index.NextFilled(row, column, 1, blockRight) ?? blockRight + 1;
            for (var c = column + 1; c < stop && c <= blockRight; c++)
            {
                if (_index.Merge(row, c) is not null)
                    break;
                // Centred across: over the empty cells after it that are centred across too.
                if (style.SpecialAlign == CellSpecialAlign.CenterAcross && _index.Get(row, c)?.Style.SpecialAlign != CellSpecialAlign.CenterAcross)
                    break;
                highest = c;
            }
        }
        var lowest = column;
        if (towardsLower)
        {
            var stop = _index.NextFilled(row, column, -1, blockLeft) ?? blockLeft - 1;
            for (var c = column - 1; c > stop && c >= blockLeft; c--)
            {
                if (_index.Merge(row, c) is not null)
                    break;
                lowest = c;
            }
        }
        var a = columnAt.TryGetValue(lowest, out var low) ? low : own;
        var b = columnAt.TryGetValue(highest, out var high) ? high : own;
        return (Math.Min(a.Start, b.Start), Math.Max(a.End, b.End));
    }

    /// <summary>The cell a text centred across is centred in: the cell and the empty ones after it that are centred across.</summary>
    private SKRect AcrossRect(SKRect rect, float overflowLeft, float overflowRight, CellStyle style) =>
        style.SpecialAlign == CellSpecialAlign.CenterAcross ? new SKRect(Math.Min(rect.Left, overflowLeft), rect.Top, Math.Max(rect.Right, overflowRight), rect.Bottom) : rect;

    private static CellAlign Horizontal(CellStyle style, SheetCell cell) => style.SpecialAlign switch
    {
        CellSpecialAlign.CenterAcross => CellAlign.Center,
        _ => style.Align != CellAlign.General ? style.Align
            : cell.IsBoolean || cell.IsError ? CellAlign.Center
            : cell.IsNumber ? CellAlign.Right
            : CellAlign.Left,
    };

    /// <param name="rect">The cell (or merged area, whole).</param>
    /// <param name="shown">What of it shows on the page: text is clipped to it, or to the overflow.</param>
    private void DrawCell(PageContent page, SheetCell cell, CellStyle style, SKRect rect, SKRect shown, float overflowLeft, float overflowRight,
        SKRect gridArea)
    {
        var s = _scale;
        var color = SheetColors.Parse(style.Color, SKColors.Black);
        if (cell.Value is { } value && SheetColors.NumberFormatColor(cell.NumberFormat, value) is { } formatColor && style.Color is null or "#000000")
            color = SheetColors.Parse(formatColor, color);
        rect = AcrossRect(rect, overflowLeft, overflowRight, style);
        var wrap = CellText.Wraps(style);
        var align = Horizontal(style, cell);
        if (Sheet.RightToLeft && style.Align == CellAlign.General && !cell.IsNumber && !cell.IsBoolean && !cell.IsError)
            align = CellAlign.Right;
        var indent = (float)(style.Indent * 3 * _plan.DefaultFont.SpaceWidth * 0.75) * s;
        var padding = CellText.Padding * s;
        var available = Math.Max(1, rect.Width - 2 * padding - indent);
        var text = CellText.Build(_fonts, _book, cell, style, color, wrap);
        if (text.IsEmpty)
            return;
        var textScale = s;

        if (style.Rotation is not (0 or CellStyle.Stacked))
        {
            DrawRotated(page, text, style, rect, Intersect(shown, gridArea), align, s);
            return;
        }

        // Numbers that don't fit: General shows fewer decimals, every other format ####.
        if (cell.IsNumber && !wrap && !style.Shrink && text.SingleLineWidth() * s > available + 0.01f)
        {
            var replacement = ShorterNumber(cell, available / s, style, color);
            text = CellText.Build(_fonts, _book, cell, style, color, false, replacement);
        }
        if (style.Shrink && !wrap)
        {
            var width = text.SingleLineWidth() * s;
            if (width > available)
                textScale = s * available / width;
        }
        if (style.SpecialAlign == CellSpecialAlign.Fill && !wrap)
        {
            var one = text.SingleLineWidth();
            var copies = one > 0 ? (int)Math.Floor(available / s / one) : 0;
            if (copies > 1)
                text = CellText.Build(_fonts, _book, cell, style, color, false, string.Concat(Enumerable.Repeat(text.Text, Math.Min(copies, 1000))));
        }

        var lines = style.Rotation == CellStyle.Stacked
            ? StackedLines(text.Text)
            : text.Lines(wrap ? available / textScale : float.MaxValue, wrap);
        var heights = lines.Select(l => text.LineMetrics(l.Start, l.End)).ToList();
        var blockHeight = heights.Sum(h => h.LineHeight) * textScale;
        var vertical = style.VerticalJustify ? CellVerticalAlign.Top : style.VerticalAlign;
        var y = vertical switch
        {
            CellVerticalAlign.Top => rect.Top,
            CellVerticalAlign.Center => (rect.Top + rect.Bottom - blockHeight) / 2,
            _ => rect.Bottom - blockHeight,
        };
        // Lines spread over the cell's height (vertical justify / distributed).
        var gap = style.VerticalJustify && lines.Count > 1 ? Math.Max(0, (rect.Height - blockHeight) / (lines.Count - 1)) : 0;

        var clip = Intersect(new SKRect(Math.Min(overflowLeft, rect.Left), shown.Top, Math.Max(overflowRight, rect.Right), shown.Bottom), gridArea);
        if (wrap || style.Rotation == CellStyle.Stacked)
            clip = Intersect(shown, gridArea);
        for (var i = 0; i < lines.Count; i++)
        {
            var (start, end) = lines[i];
            var (ascent, _, lineHeight) = heights[i];
            var baseline = y + (0.75f + ascent) * textScale;
            var lineWidth = text.Width(start, end) * textScale;
            var spaceExtra = 0f;
            var lineAlign = style.Rotation == CellStyle.Stacked ? CellAlign.Center : align;
            var justify = style.SpecialAlign == CellSpecialAlign.Justify && i < lines.Count - 1 || style.SpecialAlign == CellSpecialAlign.Distributed;
            var spaces = text.Text.AsSpan(start, end - start).Count(' ');
            if (justify && spaces > 0)
            {
                spaceExtra = Math.Max(0, (available - lineWidth) / spaces);
                lineAlign = CellAlign.Left;
            }
            else if (style.SpecialAlign == CellSpecialAlign.Distributed)
            {
                lineAlign = CellAlign.Center;
            }
            var x = lineAlign switch
            {
                CellAlign.Right => rect.Right - padding - (style.Align == CellAlign.Right || style.SpecialAlign == CellSpecialAlign.Distributed ? indent : 0) - lineWidth,
                CellAlign.Center => (rect.Left + rect.Right - lineWidth) / 2,
                _ => rect.Left + padding + indent,
            };
            // Only the characters that show: text clipped by a neighbour isn't in the PDF either.
            var (from, to, offset) = text.Visible(start, end, x, textScale, spaceExtra, clip.Left, clip.Right);
            if (from < to)
                text.Draw(page, from, to, x + offset, baseline, textScale, null, spaceExtra, clip);
            y += lineHeight * textScale + gap;
        }
    }

    private static SKRect Intersect(SKRect a, SKRect b)
    {
        var r = a;
        return r.IntersectsWith(b) ? SKRect.Intersect(a, b) : new SKRect(a.Left, a.Top, a.Left, a.Top);
    }

    /// <summary>Each character on a line of its own (Excel's stacked text).</summary>
    private static List<(int Start, int End)> StackedLines(string text)
    {
        var lines = new List<(int, int)>();
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var start = elements.ElementIndex;
            var element = elements.GetTextElement();
            if (element is "\n" or "\r" or "\r\n")
                continue;
            lines.Add((start, start + element.Length));
        }
        return lines;
    }

    /// <summary>What a number shows when it is wider than its cell: General with fewer decimals (or in E notation), else ####.</summary>
    private string ShorterNumber(SheetCell cell, float available, CellStyle style, SKColor color)
    {
        if (cell.NumberFormat is null && cell.Value is { } value && !cell.IsBoolean)
        {
            for (var decimals = 9; decimals >= 0; decimals--)
            {
                var candidate = Math.Round(value, decimals).ToString(decimals == 0 ? "0" : "0." + new string('#', decimals), CultureInfo.InvariantCulture);
                if (Measure(cell, style, color, candidate) <= available)
                    return candidate;
            }
            for (var digits = 5; digits >= 0; digits--)
            {
                var candidate = value.ToString(digits == 0 ? "0E+00" : "0." + new string('#', digits) + "E+00", CultureInfo.InvariantCulture);
                if (Measure(cell, style, color, candidate) <= available)
                    return candidate;
            }
        }
        var hash = Measure(cell, style, color, "#");
        var count = hash > 0 ? (int)Math.Floor(available / hash) : 0;
        return new string('#', Math.Clamp(count, 1, 255));
    }

    private float Measure(SheetCell cell, CellStyle style, SKColor color, string candidate) =>
        CellText.Build(_fonts, _book, cell, style, color, false, candidate).SingleLineWidth();

    /// <summary>Text turned by its angle around the middle of its box, the box placed in the cell by the alignment.</summary>
    private static void DrawRotated(PageContent page, StyledText text, CellStyle style, SKRect rect, SKRect clip, CellAlign align, float s)
    {
        var lines = text.Lines(float.MaxValue, wrap: false);
        var width = lines.Select(l => text.Width(l.Start, l.End)).DefaultIfEmpty(0).Max() * s;
        var heights = lines.Select(l => text.LineMetrics(l.Start, l.End)).ToList();
        var height = heights.Sum(h => h.LineHeight) * s;
        var angle = style.Rotation * MathF.PI / 180;
        var boxWidth = MathF.Abs(MathF.Cos(angle)) * width + MathF.Abs(MathF.Sin(angle)) * height;
        var boxHeight = MathF.Abs(MathF.Sin(angle)) * width + MathF.Abs(MathF.Cos(angle)) * height;
        var padding = CellText.Padding * s;
        var cx = align switch
        {
            CellAlign.Right => rect.Right - padding - boxWidth / 2,
            CellAlign.Center => rect.MidX,
            _ => rect.Left + padding + boxWidth / 2,
        };
        var cy = style.VerticalAlign switch
        {
            CellVerticalAlign.Top => rect.Top + boxHeight / 2,
            CellVerticalAlign.Center => rect.MidY,
            _ => rect.Bottom - boxHeight / 2,
        };
        // Skia turns clockwise for positive degrees on a y-down page; Excel's angles turn counter-clockwise.
        var matrix = SKMatrix.CreateTranslation(cx, cy).PreConcat(SKMatrix.CreateRotationDegrees(-style.Rotation)).PreConcat(SKMatrix.CreateTranslation(-width / 2, -height / 2));
        var y = 0f;
        for (var i = 0; i < lines.Count; i++)
        {
            var (start, end) = lines[i];
            var lineWidth = text.Width(start, end) * s;
            var x = align switch
            {
                CellAlign.Right => width - lineWidth,
                CellAlign.Center => (width - lineWidth) / 2,
                _ => 0,
            };
            text.Draw(page, start, end, x, y + (0.75f + heights[i].Ascent) * s, s, matrix, 0, clip);
            y += heights[i].LineHeight * s;
        }
    }

    // ───────────────────────── Pictures ─────────────────────────

    private void DrawPictures(PageContent page, List<Placed> columns, List<Placed> rows, SKRect area)
    {
        if (_pictures.Count == 0)
            return;
        var s = _scale;
        var first = columns.MinBy(c => c.Index);
        var top = rows.MinBy(r => r.Index);
        var placed = new List<(SKImage, SKRect)>();
        foreach (var (picture, image) in _pictures)
        {
            // Position from the page's first column and row (pictures over print titles are left out).
            var dx = (float)((Grid.ColumnSpan(first.Index, picture.Column) + picture.OffsetX) * s);
            var dy = (float)((Grid.RowSpan(top.Index, picture.Row) + picture.OffsetY) * s);
            var w = (float)((picture.To is { } to ? Grid.ColumnSpan(picture.Column, to.Column) + to.OffsetX - picture.OffsetX : picture.Width) * s);
            var h = (float)((picture.To is { } end ? Grid.RowSpan(picture.Row, end.Row) + end.OffsetY - picture.OffsetY : picture.Height) * s);
            if (w <= 0 || h <= 0)
                continue;
            var x = Sheet.RightToLeft ? first.End - dx - w : first.Start + dx;
            var rect = new SKRect(x, top.Start + dy, x + w, top.Start + dy + h);
            if (rect.IntersectsWith(area))
                placed.Add((image, rect));
        }
        if (placed.Count == 0)
            return;
        page.Draw(canvas =>
        {
            canvas.ClipRect(area);
            using var paint = new SKPaint { IsAntialias = true };
            foreach (var (image, rect) in placed)
                canvas.DrawImage(image, rect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        });
    }

    // ───────────────────────── Headings ─────────────────────────

    private void DrawHeadings(PageContent page, List<Placed> columns, List<Placed> rows, float gridLeft, float gridTop, float width, float height)
    {
        var s = _scale;
        var style = new CellStyle { Align = CellAlign.Center, VerticalAlign = CellVerticalAlign.Center };
        var boxes = new List<SKRect>();
        foreach (var column in columns)
        {
            var rect = new SKRect(column.Start, gridTop - height, column.End, gridTop);
            boxes.Add(rect);
            Label(page, ColumnName(column.Index), rect, style, s);
        }
        var x0 = Sheet.RightToLeft ? gridLeft + columns.Sum(c => c.Size) : gridLeft - width;
        foreach (var row in rows)
        {
            var rect = new SKRect(x0, row.Start, x0 + width, row.End);
            boxes.Add(rect);
            Label(page, (row.Index + 1).ToString(CultureInfo.InvariantCulture), rect, style, s);
        }
        boxes.Add(new SKRect(x0, gridTop - height, x0 + width, gridTop));
        page.Draw(canvas =>
        {
            using var paint = new SKPaint { Color = SKColors.Black, StrokeWidth = 0.4f, Style = SKPaintStyle.Stroke, IsAntialias = true };
            foreach (var box in boxes)
                canvas.DrawRect(box, paint);
        });
    }

    private void Label(PageContent page, string label, SKRect rect, CellStyle style, float s)
    {
        var text = CellText.Build(_fonts, _book, new SheetCell(label, style), style, SKColors.Black, false);
        if (text.IsEmpty)
            return;
        var (ascent, _, lineHeight) = text.LineMetrics(0, label.Length);
        var width = text.Width(0, label.Length) * s;
        var y = (rect.Top + rect.Bottom - lineHeight * s) / 2;
        text.Draw(page, 0, label.Length, (rect.Left + rect.Right - width) / 2, y + (0.75f + ascent) * s, s, null, 0, rect);
    }

    /// <summary>0 → "A", 26 → "AA".</summary>
    internal static string ColumnName(int column)
    {
        var name = "";
        for (var c = column + 1; c > 0; c = (c - 1) / 26)
            name = (char)('A' + (c - 1) % 26) + name;
        return name;
    }

    // ───────────────────────── Header and footer ─────────────────────────

    private void DrawHeaderFooter(PageContent page, PageFields fields, float width, float height, bool header)
    {
        var hf = Sheet.PageSetup.HeaderFooter;
        if (hf.IsEmpty)
            return;
        var setup = Sheet.PageSetup;
        var first = hf.DifferentFirst && fields.Number == FirstPageNumber;
        var even = !first && hf.DifferentOddEven && fields.Number % 2 == 0;
        var (headerCode, footerCode, suffix) = first ? (hf.FirstHeader, hf.FirstFooter, "FIRST") : even ? (hf.EvenHeader, hf.EvenFooter, "EVEN") : (hf.OddHeader, hf.OddFooter, "");
        var scale = hf.ScaleWithDocument ? _scale : 1f;
        var left = hf.AlignWithMargins ? (float)setup.LeftMargin : 0.3f * 72;
        var right = width - (hf.AlignWithMargins ? (float)setup.RightMargin : 0.3f * 72);
        var sections = HeaderFooterText.Parse(header ? headerCode : footerCode);
        if (sections.IsEmpty)
            return;
        var key = header ? "H" : "F";
        DrawSection(page, sections.Left, fields, "L" + key + suffix, CellAlign.Left, left, right, header, width, height, scale);
        DrawSection(page, sections.Center, fields, "C" + key + suffix, CellAlign.Center, left, right, header, width, height, scale);
        DrawSection(page, sections.Right, fields, "R" + key + suffix, CellAlign.Right, left, right, header, width, height, scale);
    }

    /// <summary>The number of the sheet's first page (a page with it gets the first-page header).</summary>
    public int FirstPageNumber { get; set; } = 1;

    private void DrawSection(PageContent page, IReadOnlyList<HeaderPiece> pieces, PageFields fields, string pictureKey, CellAlign align,
        float left, float right, bool header, float pageWidth, float pageHeight, float scale)
    {
        if (pieces.Count == 0)
            return;
        // Lines of styled text (a picture field is a line of its own).
        var lines = new List<List<(string Text, TextStyle Style)>> { new() };
        var pictures = new Dictionary<int, (SKImage Image, double Width, double Height)>();
        foreach (var piece in pieces)
        {
            var style = new TextStyle(piece.Font ?? _book.DefaultFontName, piece.Size ?? _book.DefaultFontSize, piece.Bold, piece.Italic,
                piece.Underline, piece.DoubleUnderline, piece.Strike, piece.Script)
            {
                Color = SheetColors.Parse(piece.Color, SKColors.Black),
            };
            if (piece.Field == HeaderField.Picture)
            {
                if (fields.Pictures.TryGetValue(pictureKey, out var picture) || pictureKey.Length > 2 && fields.Pictures.TryGetValue(pictureKey[..2], out picture))
                {
                    pictures[lines.Count - 1] = picture;
                    lines.Add([]);
                }
                continue;
            }
            var value = piece.Field switch
            {
                HeaderField.Page => (fields.Number + piece.Offset).ToString(CultureInfo.CurrentCulture),
                HeaderField.Pages => fields.Total.ToString(CultureInfo.CurrentCulture),
                HeaderField.Date => fields.Now.ToString("d", CultureInfo.CurrentCulture),
                HeaderField.Time => fields.Now.ToString("t", CultureInfo.CurrentCulture),
                HeaderField.FileName => fields.FileName,
                HeaderField.Path => fields.Folder,
                HeaderField.SheetName => fields.SheetName,
                _ => piece.Text,
            };
            var parts = value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                    lines.Add([]);
                if (parts[i].Length > 0)
                    lines[^1].Add((parts[i], style));
            }
        }

        // Measure every line; the header grows down from its margin, the footer up from its margin.
        var laid = new List<(StyledText? Text, float Height, float Ascent, (SKImage Image, double Width, double Height)? Picture)>();
        for (var i = 0; i < lines.Count; i++)
        {
            StyledText? text = null;
            float lineHeight = 0, ascent = 0;
            if (lines[i].Count > 0)
            {
                var content = string.Concat(lines[i].Select(p => p.Text));
                var spans = new List<(int, int, TextStyle)>();
                var at = 0;
                foreach (var (part, style) in lines[i])
                {
                    spans.Add((at, part.Length, style));
                    at += part.Length;
                }
                text = new StyledText(_fonts, content, spans);
                var metrics = text.LineMetrics(0, content.Length);
                (lineHeight, ascent) = (metrics.LineHeight * scale, metrics.Ascent * scale);
            }
            else if (!pictures.ContainsKey(i) && i < lines.Count - 1)
            {
                var metrics = _fonts.Pixels(_book.DefaultFontName, _book.DefaultFontSize);
                lineHeight = metrics.LineHeight * 0.75f * scale;
            }
            laid.Add((text, lineHeight, ascent, null));
            if (pictures.TryGetValue(i, out var picture))
                laid.Add((null, (float)picture.Height, 0, picture));
        }
        var total = laid.Sum(l => l.Height);
        var y = header ? (float)Sheet.PageSetup.HeaderMargin : pageHeight - (float)Sheet.PageSetup.FooterMargin - total;
        var area = new SKRect(0, 0, pageWidth, pageHeight);
        foreach (var (text, lineHeight, ascent, picture) in laid)
        {
            if (picture is { } p)
            {
                var w = (float)p.Width;
                var x = align switch { CellAlign.Right => right - w, CellAlign.Center => (pageWidth - w) / 2, _ => left };
                var rect = new SKRect(x, y, x + w, y + (float)p.Height);
                var image = p.Image;
                page.Draw(canvas => canvas.DrawImage(image, rect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)));
            }
            else if (text is not null)
            {
                var lineWidth = text.Width(0, text.Text.Length) * scale;
                var x = align switch { CellAlign.Right => right - lineWidth, CellAlign.Center => (pageWidth - lineWidth) / 2, _ => left };
                text.Draw(page, 0, text.Text.Length, x, y + 0.75f * scale + ascent, scale, null, 0, area);
            }
            y += lineHeight;
        }
    }
}
