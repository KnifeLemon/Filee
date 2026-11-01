// Fast lookups for printing a sheet: the cells of a block of rows and columns (sorted arrays, binary searched), and
// the merged area a cell belongs to (merges bucketed by blocks of 256 rows). A page looks up only its own cells, so
// a sheet of many pages isn't walked once per page.

using Filee.Engines.Documents;

namespace Filee.Engines.Office.Sheets;

internal sealed class SheetIndex
{
    private const int BlockShift = 8;

    /// <summary>Merge entries over all row blocks; a file that needs more is refused.</summary>
    private const int MaxMergeEntries = 10_000_000;

    private readonly int[] _rows;
    private readonly (int[] Columns, SheetCell[] Cells)[] _cells;
    private readonly Dictionary<int, List<CellRange>> _merges = [];

    public SheetIndex(Worksheet sheet)
    {
        _rows = [.. sheet.Rows.Keys];
        _cells = [.. sheet.Rows.Values.Select(r => (r.Keys.ToArray(), r.Values.ToArray()))];
        long entries = 0;
        foreach (var merge in sheet.Merges)
        {
            var first = merge.Top >> BlockShift;
            var last = Math.Min(merge.Bottom, SheetPagination.MaxRow) >> BlockShift;
            entries += last - first + 1;
            if (entries > MaxMergeEntries)
                throw DocumentLimits.Exceeded($"The sheet {sheet.Name} has too many merged cells.");
            for (var block = first; block <= last; block++)
            {
                if (!_merges.TryGetValue(block, out var list))
                    _merges[block] = list = [];
                list.Add(merge);
            }
        }
    }

    /// <summary>The cell at a position, or null.</summary>
    public SheetCell? Get(int row, int column)
    {
        var r = Array.BinarySearch(_rows, row);
        if (r < 0)
            return null;
        var c = Array.BinarySearch(_cells[r].Columns, column);
        return c < 0 ? null : _cells[r].Cells[c];
    }

    /// <summary>The cells in rows <paramref name="top"/>..<paramref name="bottom"/> and columns <paramref name="left"/>..<paramref name="right"/>.</summary>
    public IEnumerable<(int Row, int Column, SheetCell Cell)> Cells(int top, int bottom, int left, int right)
    {
        var r = Array.BinarySearch(_rows, top);
        if (r < 0)
            r = ~r;
        for (; r < _rows.Length && _rows[r] <= bottom; r++)
        {
            var (columns, cells) = _cells[r];
            var c = Array.BinarySearch(columns, left);
            if (c < 0)
                c = ~c;
            for (; c < columns.Length && columns[c] <= right; c++)
                yield return (_rows[r], columns[c], cells[c]);
        }
    }

    /// <summary>The nearest cell with text to the left or right of a column in a row (for text overflow), or null.</summary>
    public int? NextFilled(int row, int column, int step, int limit)
    {
        var r = Array.BinarySearch(_rows, row);
        if (r < 0)
            return null;
        var (columns, cells) = _cells[r];
        var c = Array.BinarySearch(columns, column);
        c = c < 0 ? ~c + (step > 0 ? 0 : -1) : c + step;
        for (; c >= 0 && c < columns.Length; c += step)
        {
            if (step > 0 ? columns[c] > limit : columns[c] < limit)
                return null;
            if (cells[c].Text.Length > 0)
                return columns[c];
        }
        return null;
    }

    /// <summary>The merged area holding the cell, or null.</summary>
    public CellRange? Merge(int row, int column)
    {
        if (!_merges.TryGetValue(row >> BlockShift, out var list))
            return null;
        foreach (var merge in list)
        {
            if (merge.Contains(row, column))
                return merge;
        }
        return null;
    }

    /// <summary>The merged areas that overlap a block of rows and columns.</summary>
    public HashSet<CellRange> Merges(int top, int bottom, int left, int right)
    {
        var found = new HashSet<CellRange>();
        for (var block = top >> BlockShift; block <= bottom >> BlockShift; block++)
        {
            if (!_merges.TryGetValue(block, out var list))
                continue;
            foreach (var merge in list)
            {
                if (merge.Top <= bottom && merge.Bottom >= top && merge.Left <= right && merge.Right >= left)
                    found.Add(merge);
            }
        }
        return found;
    }
}
