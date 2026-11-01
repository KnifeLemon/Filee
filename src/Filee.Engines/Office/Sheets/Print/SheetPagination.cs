// Excel's page logic for one sheet: what is printed (the print area, else the used range from its first used cell),
// the size of every column and row in points (Excel's pixel units: widths in digits of the default font plus 5 px of
// padding, heights from the file or from the fonts and lines of the row's cells), the scale (a percent, or the
// largest whole percent that fits N × M pages), and the pages: each print area is cut into rows and columns of pages
// at manual breaks and where the next row or column no longer fits, with the print titles repeated on the pages after
// them, ordered down then over (or over then down).

using Filee.Engines.Documents;

namespace Filee.Engines.Office.Sheets;

/// <summary>One printed page: the cells of one print area it shows, and whether the print titles repeat on it.</summary>
internal sealed record SheetPage(CellRange Area, int Top, int Bottom, int Left, int Right, bool RepeatRows, bool RepeatColumns);

/// <summary>The sizes of a sheet's columns and rows in points at 100 % (hidden ones 0).</summary>
internal sealed class SheetGrid
{
    private readonly double[] _left;
    private readonly double[] _top;

    public SheetGrid(double[] widths, double[] heights)
    {
        Widths = widths;
        Heights = heights;
        _left = new double[widths.Length + 1];
        for (var i = 0; i < widths.Length; i++)
            _left[i + 1] = _left[i] + widths[i];
        _top = new double[heights.Length + 1];
        for (var i = 0; i < heights.Length; i++)
            _top[i + 1] = _top[i] + heights[i];
    }

    public double[] Widths { get; }
    public double[] Heights { get; }

    public double Width(int column) => column >= 0 && column < Widths.Length ? Widths[column] : 0;
    public double Height(int row) => row >= 0 && row < Heights.Length ? Heights[row] : 0;

    /// <summary>Width of the columns from <paramref name="from"/> up to (not including) <paramref name="to"/>; negative when to &lt; from.</summary>
    public double ColumnSpan(int from, int to) => Offset(_left, to) - Offset(_left, from);

    public double RowSpan(int from, int to) => Offset(_top, to) - Offset(_top, from);

    private static double Offset(double[] prefix, int index) => prefix[Math.Clamp(index, 0, prefix.Length - 1)];
}

/// <summary>How one sheet prints.</summary>
internal sealed record SheetPrintPlan(Worksheet Sheet, SheetGrid Grid, double Scale, IReadOnlyList<SheetPage> Pages,
    double HeadingWidth, double HeadingHeight, PixelMetrics DefaultFont);

internal static class SheetPagination
{
    /// <summary>Excel's sheet: 1,048,576 rows and 16,384 columns.</summary>
    public const int MaxRow = 1_048_575;

    public const int MaxColumn = 16_383;

    /// <summary>Lengths compare with this slack (points), so 48 rows of exactly 15 pt fill 720 pt.</summary>
    private const double Slack = 0.01;

    public static SheetPrintPlan Plan(Workbook book, Worksheet sheet, SheetIndex index, SheetFonts fonts, DocumentLimits limits,
        int pagesBefore, CancellationToken cancellationToken = default)
    {
        var defaultFont = fonts.Pixels(book.DefaultFontName, book.DefaultFontSize);
        var areas = Areas(sheet);
        var right = areas.Select(a => a.Right).Concat(sheet.TitleColumns is { } tc ? [tc.Last] : []).DefaultIfEmpty(-1).Max();
        var bottom = areas.Select(a => a.Bottom).Concat(sheet.TitleRows is { } tr ? [tr.Last] : []).DefaultIfEmpty(-1).Max();
        var widths = Widths(sheet, right + 1, defaultFont);
        var heights = SheetRowHeights.Measure(book, sheet, bottom + 1, fonts, defaultFont, widths, index, cancellationToken);
        var grid = new SheetGrid(widths, heights);
        var setup = sheet.PageSetup;

        double headingWidth = 0, headingHeight = 0;
        if (setup.PrintHeadings)
        {
            var digits = Math.Max(1, (bottom + 1).ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
            headingWidth = (digits * defaultFont.DigitWidth + 8) * 0.75;
            headingHeight = defaultFont.LineHeight * 0.75;
        }

        var (pageWidth, pageHeight) = setup.PageSize;
        var width = Math.Max(1, pageWidth - setup.LeftMargin - setup.RightMargin);
        var height = Math.Max(1, pageHeight - setup.TopMargin - setup.BottomMargin);
        var titleColumns = Valid(sheet.TitleColumns, grid.Widths.Length);
        var titleRows = Valid(sheet.TitleRows, grid.Heights.Length);
        var titleWidth = titleColumns is { } c ? grid.ColumnSpan(c.First, c.Last + 1) : 0;
        var titleHeight = titleRows is { } r ? grid.RowSpan(r.First, r.Last + 1) : 0;

        // Manual breaks don't count when the sheet is fitted to pages.
        var rowBreaks = setup.FitToPage ? null : sheet.RowBreaks;
        var columnBreaks = setup.FitToPage ? null : sheet.ColumnBreaks;
        List<(int, int)> Columns(CellRange area, double scale) =>
            Cut(grid.Widths, area.Left, area.Right, width / scale - headingWidth, columnBreaks, titleColumns, titleWidth);
        List<(int, int)> Rows(CellRange area, double scale) =>
            Cut(grid.Heights, area.Top, area.Bottom, height / scale - headingHeight, rowBreaks, titleRows, titleHeight);

        var percent = Math.Clamp(setup.Scale, 10, 400);
        if (setup.FitToPage)
        {
            var across = setup.FitWidth <= 0 ? 100 : Fit(s => areas.Select(a => Columns(a, s / 100.0).Count).DefaultIfEmpty(0).Max() <= setup.FitWidth);
            var down = setup.FitHeight <= 0 ? 100 : Fit(s => areas.Select(a => Rows(a, s / 100.0).Count).DefaultIfEmpty(0).Max() <= setup.FitHeight);
            percent = Math.Min(across, down);
        }
        var scaleFactor = percent / 100.0;

        var pages = new List<SheetPage>();
        foreach (var area in areas)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var columns = Columns(area, scaleFactor);
            var rows = Rows(area, scaleFactor);
            if ((long)columns.Count * rows.Count + pages.Count + pagesBefore > limits.MaxPages)
                throw DocumentLimits.Exceeded($"The sheet {sheet.Name} prints on more than {DocumentLimits.Count(limits.MaxPages)} pages.");
            IEnumerable<((int, int) Rows, (int, int) Columns)> order = setup.OverThenDown
                ? rows.SelectMany(rr => columns.Select(cc => (rr, cc)))
                : columns.SelectMany(cc => rows.Select(rr => (rr, cc)));
            foreach (var ((top, last), (left, end)) in order)
            {
                pages.Add(new SheetPage(area, top, last, left, end,
                    titleRows is { } tRows && top > tRows.Last,
                    titleColumns is { } tColumns && left > tColumns.Last));
            }
        }
        return new SheetPrintPlan(sheet, grid, scaleFactor, pages, headingWidth, headingHeight, defaultFont);
    }

    /// <summary>The largest whole percent from 10 to 100 for which <paramref name="fits"/> holds (10 when none does).</summary>
    private static int Fit(Func<int, bool> fits)
    {
        if (fits(100))
            return 100;
        int low = 10, high = 99;
        if (!fits(low))
            return low;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (fits(mid))
                low = mid;
            else
                high = mid - 1;
        }
        return low;
    }

    private static (int First, int Last)? Valid((int First, int Last)? titles, int count) =>
        titles is { } t && t.First >= 0 && t.Last >= t.First && t.First < count ? (t.First, Math.Min(t.Last, count - 1)) : null;

    /// <summary>
    /// Cuts the indexes <paramref name="first"/>..<paramref name="last"/> into pages of at most
    /// <paramref name="available"/> points: a page ends before a manual break and before the index that no longer fits
    /// (an index larger than a page gets a page of its own). Pages after the titles have the titles' length less room.
    /// Hidden indexes (size 0) never start a page; a range of hidden indexes only prints nothing.
    /// </summary>
    internal static List<(int Start, int End)> Cut(double[] sizes, int first, int last, double available, SortedSet<int>? breaks,
        (int First, int Last)? titles, double titleSize)
    {
        var pages = new List<(int, int)>();
        var start = -1;
        var used = 0.0;
        var room = 0.0;
        var end = -1;
        // The manual breaks in order; the next one not yet passed.
        var manual = breaks is null ? [] : breaks.GetViewBetween(first + 1, Math.Max(first + 1, last)).ToArray();
        var next = 0;
        for (var i = first; i <= last; i++)
        {
            var size = i < sizes.Length ? sizes[i] : 0;
            if (size <= 0)
                continue;
            // A break above a hidden row still counts: the next visible one starts the page.
            var broken = false;
            while (next < manual.Length && manual[next] <= i)
            {
                broken |= manual[next] > end;
                next++;
            }
            if (start >= 0 && (broken || used + size > room + Slack))
            {
                pages.Add((start, end));
                start = -1;
            }
            if (start < 0)
            {
                start = i;
                used = 0;
                room = available - (titles is { } t && i > t.Last ? titleSize : 0);
            }
            used += size;
            end = i;
        }
        if (start >= 0)
            pages.Add((start, end));
        return pages;
    }

    /// <summary>
    /// The ranges that print: the print area (whole columns or rows cut to the used range), else the used range — the
    /// cells with text, a fill or borders, merged areas and pictures, from the first of them.
    /// </summary>
    internal static List<CellRange> Areas(Worksheet sheet)
    {
        var used = Used(sheet);
        if (sheet.PrintArea.Count == 0)
            return used is { } u ? [u] : [];
        var areas = new List<CellRange>();
        foreach (var area in sheet.PrintArea)
        {
            var top = area.Top;
            var bottom = Math.Min(area.Bottom, MaxRow);
            var left = area.Left;
            var right = Math.Min(area.Right, MaxColumn);
            if (top == 0 && bottom == MaxRow)
            {
                if (used is not { } rows)
                    continue;
                bottom = rows.Bottom;
            }
            if (left == 0 && right == MaxColumn)
            {
                if (used is not { } columns)
                    continue;
                right = columns.Right;
            }
            if (top <= bottom && left <= right)
                areas.Add(new CellRange(top, left, bottom, right));
        }
        return areas;
    }

    private static CellRange? Used(Worksheet sheet)
    {
        var used = sheet.UsedRange();
        foreach (var picture in sheet.Pictures)
        {
            var (row, column) = (Math.Clamp(picture.Row, 0, MaxRow), Math.Clamp(picture.Column, 0, MaxColumn));
            var (lastRow, lastColumn) = picture.To is { } to ? (Math.Clamp(to.Row, row, MaxRow), Math.Clamp(to.Column, column, MaxColumn)) : (row, column);
            used = used is { } u
                ? new CellRange(Math.Min(u.Top, row), Math.Min(u.Left, column), Math.Max(u.Bottom, lastRow), Math.Max(u.Right, lastColumn))
                : new CellRange(row, column, lastRow, lastColumn);
        }
        if (used is { } range)
        {
            // A merged area that starts inside counts whole.
            foreach (var merge in sheet.Merges)
            {
                if (range.Contains(merge.Top, merge.Left))
                    range = range with { Bottom = Math.Max(range.Bottom, Math.Min(merge.Bottom, MaxRow)), Right = Math.Max(range.Right, Math.Min(merge.Right, MaxColumn)) };
            }
            return range;
        }
        return null;
    }

    /// <summary>
    /// Column widths in points: Excel's characters of the default font, Truncate(((256 × w + Truncate(128 / mdw)) / 256)
    /// × mdw) pixels; the default width from the file or 8 digits + 5 px rounded up to 8 px; ODS widths are points.
    /// </summary>
    internal static double[] Widths(Worksheet sheet, int count, PixelMetrics font)
    {
        var mdw = font.DigitWidth;
        double Pixels(double characters) => Math.Truncate((256 * characters + Math.Truncate(128.0 / mdw)) / 256 * mdw);
        double standard;
        if (sheet.WidthsInPoints)
            standard = sheet.DefaultColumnWidth * OdsReader.PointsPerCharacter;
        else if (sheet.HasDefaultColumnWidth)
            standard = Pixels(sheet.DefaultColumnWidth) * 0.75;
        else
        {
            var basePixels = (sheet.BaseColumnWidth ?? 8) * mdw + 5;
            standard = Math.Ceiling(basePixels / 8.0) * 8 * 0.75;
        }
        var widths = new double[Math.Max(0, count)];
        for (var c = 0; c < widths.Length; c++)
        {
            if (sheet.HiddenColumns.Contains(c))
                continue;
            widths[c] = sheet.ColumnWidths.TryGetValue(c, out var w)
                ? sheet.WidthsInPoints ? w * OdsReader.PointsPerCharacter : Pixels(Math.Max(0, Math.Min(w, 255))) * 0.75
                : standard;
        }
        return widths;
    }
}
