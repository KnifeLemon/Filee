// What the writers of text formats (Markdown, Textile, MediaWiki, reStructuredText, Org, DocBook) may make of a
// model: tables placed on their grid with arithmetic spans (each column remembers the row it is free again from, so
// spans that overlap or reach far cost nothing), a cap on a table's grid and on all grids of a document together, and
// a cap on the text written. A model can come from any reader; these keep a hostile one from turning a few bytes of
// spans or one footnote referenced thousands of times into gigabytes. Passing a cap refuses the conversion with
// error.document_too_complex.

using Filee.Engines.Documents;

namespace Filee.Engines.Hwp.Hwpx.Markup;

/// <summary>Places the cells of a table row by row on its grid, skipping the positions spans from above hold.</summary>
internal sealed class SpanCursor
{
    /// <summary>For each column, the first row it is free again in (a cell above spans down to the row before).</summary>
    private readonly int[] _freeFrom;
    private int _row = -1;
    private int _column;

    public SpanCursor(int rows, int columns)
    {
        Rows = rows;
        Columns = columns;
        _freeFrom = new int[columns];
    }

    public int Rows { get; }

    public int Columns { get; }

    /// <summary>Goes to the start of the next row.</summary>
    public void NextRow()
    {
        _row++;
        _column = 0;
    }

    /// <summary>
    /// Places the next cell of the row at the first free column: its row span cut at <paramref name="rowEnd"/> (the
    /// end of the table or of its part), its column span at the table's edge and at the first column a span from above
    /// still holds, so cells never overlap. False when the row has no free column left.
    /// </summary>
    public bool Place(int rowSpan, int colSpan, int rowEnd, out int column, out int rows, out int columns)
    {
        while (_column < Columns && _freeFrom[_column] > _row)
            _column++;
        column = _column;
        rows = columns = 0;
        if (column >= Columns)
            return false;
        var widest = Math.Clamp(colSpan, 1, Columns - column);
        columns = 1;
        while (columns < widest && _freeFrom[column + columns] <= _row)
            columns++;
        rows = Math.Clamp(rowSpan, 1, Math.Max(1, rowEnd - _row));
        for (var c = column; c < column + columns; c++)
            _freeFrom[c] = _row + rows;
        _column = column + columns;
        return true;
    }
}

/// <summary>The caps of one written document (one per writer run).</summary>
internal sealed class MarkupWriteBudget(DocumentLimits limits)
{
    private long _gridCells;

    public DocumentLimits Limits { get; } = limits;

    /// <summary>
    /// Characters a written document may have: 256 million (four times what a part may unpack to, twice what the
    /// markup readers let shared footnotes expand to), far beyond any real document.
    /// </summary>
    public long MaxCharacters => 4 * Limits.MaxPartBytes;

    /// <summary>
    /// Grid positions of all tables of a document together: twice what a table alone may have (MaxParagraphs), as
    /// a real document's cells each hold a paragraph. A writer spends some characters on every position (DocBook a
    /// colspec per column), so this bounds what the tables cost, however many there are.
    /// </summary>
    public long MaxGridCells => 2L * Limits.MaxParagraphs;

    /// <summary>
    /// The columns of a table: what its widest row's spans add up to (or its column count), at least 1 and at most
    /// MaxTableColumns. Counted in longs, so spans written as two billion don't overflow.
    /// </summary>
    public int Columns(HTable table)
    {
        var widest = table.Rows.Count == 0 ? 0 : table.Rows.Max(r => r.Cells.Sum(c => (long)Math.Clamp(c.ColSpan, 1, Limits.MaxTableColumns)));
        return (int)Math.Clamp(Math.Max(widest, table.ColumnCount), 1, Limits.MaxTableColumns);
    }

    /// <summary>Counts a table's grid before it is made; refuses one too large alone or with the tables before it.</summary>
    public void Table(int rows, int columns)
    {
        MarkupBlocks.TableArea(rows, columns, Limits);
        _gridCells += (long)rows * Math.Max(1, columns);
        if (_gridCells > MaxGridCells)
            throw DocumentLimits.Exceeded($"The tables have more than {DocumentLimits.Count(MaxGridCells)} cells together.");
    }

    /// <summary>Refuses a written text that has grown (or would grow) past <see cref="MaxCharacters"/>.</summary>
    public void Characters(long characters)
    {
        if (characters > MaxCharacters)
            throw DocumentLimits.Exceeded($"The converted text would have more than {DocumentLimits.Count(MaxCharacters)} characters.");
    }
}
