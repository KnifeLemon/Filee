// Fast lookups for printing a sheet: the cells of a block of rows and columns (sorted arrays, binary searched), and
// the merged area a cell belongs to. Merges are kept in bands of rows over which the same merges lie, each band's
// merges sorted by column, so finding a cell's merge is two binary searches however many merges the sheet has. A page
// looks up only its own cells, so a sheet of many pages isn't walked once per page.

using Filee.Engines.Documents;

namespace Filee.Engines.Office.Sheets;

internal sealed class SheetIndex
{
    /// <summary>Merge entries over all bands; a file that needs more is refused.</summary>
    private const int MaxMergeEntries = 10_000_000;

    private readonly int[] _rows;
    private readonly (int[] Columns, SheetCell[] Cells)[] _cells;

    /// <summary>The first row of each band, ascending; a band reaches to the next band's first row.</summary>
    private readonly int[] _bandStarts = [];

    /// <summary>Per band, its merges sorted by column, none overlapping (so their right ends ascend too).</summary>
    private readonly CellRange[][] _bands = [];

    public SheetIndex(Worksheet sheet)
    {
        _rows = [.. sheet.Rows.Keys];
        _cells = [.. sheet.Rows.Values.Select(r => (r.Keys.ToArray(), r.Values.ToArray()))];
        if (sheet.Merges.Count > 0)
            (_bandStarts, _bands) = Bands(sheet);
    }

    /// <summary>
    /// Sweeps the merges from top to bottom: a band starts wherever a merge starts or ends. Merges that overlap one
    /// before them in a band (damage: Excel refuses such files) are left out of that band.
    /// </summary>
    private static (int[], CellRange[][]) Bands(Worksheet sheet)
    {
        var merges = new List<(CellRange Range, int Top, int End)>();
        foreach (var merge in sheet.Merges)
        {
            var top = Math.Max(0, merge.Top);
            var bottom = Math.Min(merge.Bottom, SheetPagination.MaxRow);
            if (top <= bottom && merge.Left <= merge.Right && merge.Right >= 0)
                merges.Add((merge, top, bottom + 1));
        }
        var starts = merges.Select(m => m.Top).Concat(merges.Select(m => m.End)).Distinct().Order().ToArray();
        var byTop = merges.Select((m, i) => i).OrderBy(i => merges[i].Top).ToArray();
        var byEnd = merges.Select((m, i) => i).OrderBy(i => merges[i].End).ToArray();
        var active = new HashSet<int>();
        var bands = new CellRange[starts.Length][];
        int nextTop = 0, nextEnd = 0;
        long entries = 0;
        for (var b = 0; b < starts.Length; b++)
        {
            var row = starts[b];
            for (; nextEnd < byEnd.Length && merges[byEnd[nextEnd]].End <= row; nextEnd++)
                active.Remove(byEnd[nextEnd]);
            for (; nextTop < byTop.Length && merges[byTop[nextTop]].Top <= row; nextTop++)
            {
                if (merges[byTop[nextTop]].End > row)
                    active.Add(byTop[nextTop]);
            }
            entries += active.Count;
            if (entries > MaxMergeEntries)
                throw DocumentLimits.Exceeded($"The sheet {sheet.Name} has too many merged cells.");
            if (active.Count == 0)
            {
                bands[b] = [];
                continue;
            }
            var sorted = active.Select(i => merges[i].Range).OrderBy(m => m.Left).ThenBy(m => m.Right).ToList();
            var kept = new List<CellRange>(sorted.Count);
            foreach (var merge in sorted)
            {
                if (kept.Count == 0 || merge.Left > kept[^1].Right)
                    kept.Add(merge);
            }
            bands[b] = [.. kept];
        }
        return (starts, bands);
    }

    /// <summary>The band holding a row, or -1 when the row is above the first band.</summary>
    private int Band(int row)
    {
        var b = Array.BinarySearch(_bandStarts, row);
        return b >= 0 ? b : ~b - 1;
    }

    /// <summary>The first merge of a band whose right end is at or after <paramref name="column"/>.</summary>
    private static int FirstReaching(CellRange[] band, int column)
    {
        int low = 0, high = band.Length;
        while (low < high)
        {
            var mid = (low + high) >>> 1;
            if (band[mid].Right < column)
                low = mid + 1;
            else
                high = mid;
        }
        return low;
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

    /// <summary>How many rows with cells lie in <paramref name="top"/>..<paramref name="bottom"/>.</summary>
    public int RowsWithCells(int top, int bottom)
    {
        if (bottom < top)
            return 0;
        var first = Array.BinarySearch(_rows, top);
        var last = Array.BinarySearch(_rows, bottom);
        first = first < 0 ? ~first : first;
        last = last < 0 ? ~last : last + 1;
        return Math.Max(0, last - first);
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
        var b = Band(row);
        if (b < 0)
            return null;
        var band = _bands[b];
        var i = FirstReaching(band, column);
        return i < band.Length && band[i].Left <= column ? band[i] : null;
    }

    /// <summary>The merged areas that overlap a block of rows and columns.</summary>
    public HashSet<CellRange> Merges(int top, int bottom, int left, int right)
    {
        var found = new HashSet<CellRange>();
        if (_bands.Length == 0 || bottom < top)
            return found;
        for (var b = Math.Max(0, Band(top)); b < _bands.Length && _bandStarts[b] <= bottom; b++)
        {
            var band = _bands[b];
            for (var i = FirstReaching(band, left); i < band.Length && band[i].Left <= right; i++)
                found.Add(band[i]);
        }
        return found;
    }
}
