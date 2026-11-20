// Which cell holds each position of a MarkupTable's grid: the writers whose tables say what a merged cell covers
// (LaTeX's \multicolumn and rules, AsciiDoc's and Typst's cell counts, tbl's span columns) tell a position covered by a
// span from one no cell has. The grid is counted against the budget before it is made (MarkupTextWriter.Grid).

namespace Filee.Engines.Hwp.Hwpx.Markup;

internal static class MarkupTableOwners
{
    /// <summary>The row and column of the cell holding each grid position, or (-1, -1) where none does.</summary>
    public static (int Row, int Column)[,] Owners(MarkupTable table)
    {
        var owners = new (int Row, int Column)[table.Rows.Count, table.Columns];
        for (var r = 0; r < table.Rows.Count; r++)
            for (var c = 0; c < table.Columns; c++)
                owners[r, c] = (-1, -1);
        for (var r = 0; r < table.Rows.Count; r++)
        {
            for (var c = 0; c < table.Columns; c++)
            {
                if (table.Rows[r][c] is not { } cell)
                    continue;
                for (var rr = r; rr < Math.Min(table.Rows.Count, r + cell.RowSpan); rr++)
                    for (var cc = c; cc < Math.Min(table.Columns, c + cell.ColSpan); cc++)
                        owners[rr, cc] = (r, c);
            }
        }
        return owners;
    }
}
