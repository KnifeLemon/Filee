// PivotTables on a sheet and the look their PivotTable style gives their cells, for the printer. Excel saves a
// PivotTable's values and captions in the sheet's cells; its style (pivotTableStyleInfo: a built-in PivotStyleLight1–28,
// PivotStyleMedium1–28, PivotStyleDark1–28, or one of the workbook's own tableStyles with PivotTable parts) is put on
// those cells when they are shown, as a table's is.
//
// The parts are applied in ECMA-376's PivotTable order (Part 1, 18.8.41): whole table, page field labels and values,
// column and row stripes, first column, header row, first header cell, subtotal columns 1–3, blank rows, subtotal rows
// 1–3, column subheadings 1–3, row subheadings 1–3, the grand total column (lastColumn) and the grand total row
// (totalRow); a later part's property wins. Where each part lies follows ST_TableStyleType's PivotTable pictures
// (18.18.77): from the location's ref, firstHeaderRow, firstDataRow and firstDataCol, the row and column items
// (rowItems / colItems i: t data, default / sum / ... subtotals, grand, blank; the item's field level from r and its
// x values) and the page fields above the table (rowPageCount rows, colPageCount columns of label and value):
//  * whole table: the location and the page fields; header row: the rows above the data; first column: the row label
//    columns, top to bottom; first header cell: those columns in the header rows but the last;
//  * row stripes: the rows from the first data row, across the table; column stripes: the data columns in those rows;
//  * a subtotal row, blank row or row subheading (an item of an outer row field, in compact and outline form):
//    across the table; subtotals and subheadings of the outermost field are the first, those of deeper fields the
//    second and third by turns ([MS-XLSB] 2.5.150 TSEType); a subtotal or grand total column: from its caption's row (firstHeaderRow + its level) down;
//    a column subheading: the caption row of an outer column field, and its subtotal columns' captions;
//  * the grand total row: across the table.
// The style options say which parts show ([MS-XLSB] 2.4.849 BrtTableStyleClient, XLSX pivotTableStyleInfo):
// showRowHeaders the first column and row subheadings, showColHeaders the header row and column subheadings,
// showRowStripes / showColStripes the stripes, showLastColumn the grand total column.
// The cells' own formatting stays over the style (SheetTables.Look), and the workbook's TableBudget bounds the cells
// styled, as for tables.
//
// The built-in styles are presetTableStyles.xml's PivotStyle* (ECMA-376 Part 1, OfficeOpenXML-SpreadsheetMLStyles.zip),
// in SheetTables' short form; that file's PivotTable dxfId values count from 0.

namespace Filee.Engines.Office.Sheets;

/// <summary>What one row or column of a PivotTable's items is (rowItems / colItems i t).</summary>
internal enum PivotItemKind
{
    /// <summary>An item of a field (t data).</summary>
    Data,

    /// <summary>A subtotal (t default, sum, count, average, ...).</summary>
    Subtotal,

    /// <summary>The grand total (t grand).</summary>
    Grand,

    /// <summary>An empty line after an item (t blank: "Insert blank line after each item").</summary>
    Blank,
}

/// <summary>A row or column of a PivotTable's items: its kind and its field's level (0 = the outermost field).</summary>
internal readonly record struct PivotItem(PivotItemKind Kind, int Level);

/// <summary>
/// A PivotTable on a sheet: its location (the table below its page fields), where its header rows, data rows and data
/// columns start (offsets from the location's top-left cell), its row and column items, the number of row and column
/// fields, its page fields' caption cells, and its style with the parts the style options turn on.
/// </summary>
internal sealed record SheetPivot(CellRange Range, string? StyleName)
{
    public int FirstHeaderRow { get; init; }
    public int FirstDataRow { get; init; }
    public int FirstDataColumn { get; init; }

    /// <summary>The rows from the first data row down.</summary>
    public IReadOnlyList<PivotItem> Rows { get; init; } = [];

    /// <summary>The columns from the first data column on.</summary>
    public IReadOnlyList<PivotItem> Columns { get; init; } = [];

    public int RowFields { get; init; }
    public int ColumnFields { get; init; }

    /// <summary>The page fields' caption cells (their values are in the next column).</summary>
    public IReadOnlyList<(int Row, int Column)> PageFields { get; init; } = [];

    public bool RowHeaders { get; init; }
    public bool ColumnHeaders { get; init; }
    public bool RowStripes { get; init; }
    public bool ColumnStripes { get; init; }
    public bool LastColumn { get; init; }
}

internal static class SheetPivots
{
    /// <summary>PivotTables of one workbook styled at most.</summary>
    public const int MaxPivots = 10_000;

    /// <summary>Page fields of one PivotTable placed at most.</summary>
    public const int MaxPageFields = 1_000;

    /// <summary>The order in which a PivotTable style's parts are applied (the later wins).</summary>
    private static readonly string[] Order =
    [
        "wholeTable", "pageFieldLabels", "pageFieldValues", "firstColumnStripe", "secondColumnStripe", "firstRowStripe", "secondRowStripe",
        "firstColumn", "headerRow", "firstHeaderCell", "firstSubtotalColumn", "secondSubtotalColumn", "thirdSubtotalColumn", "blankRow",
        "firstSubtotalRow", "secondSubtotalRow", "thirdSubtotalRow", "firstColumnSubheading", "secondColumnSubheading",
        "thirdColumnSubheading", "firstRowSubheading", "secondRowSubheading", "thirdRowSubheading", "lastColumn", "totalRow",
    ];

    /// <summary>The ECMA-376 part names of the three levels of subtotals and subheadings.</summary>
    private static readonly string[] SubtotalRows = ["firstSubtotalRow", "secondSubtotalRow", "thirdSubtotalRow"];

    private static readonly string[] SubtotalColumns = ["firstSubtotalColumn", "secondSubtotalColumn", "thirdSubtotalColumn"];
    private static readonly string[] RowSubheadings = ["firstRowSubheading", "secondRowSubheading", "thirdRowSubheading"];
    private static readonly string[] ColumnSubheadings = ["firstColumnSubheading", "secondColumnSubheading", "thirdColumnSubheading"];

    /// <summary>Whether a tableStyleElement type is one a PivotTable uses.</summary>
    public static bool IsPivotPart(string? type) => type is not null && Order.Contains(type);

    /// <summary>XLSB's TSEType of the PivotTable parts (MS-XLSB 2.5.150), by value: the table ones, then 13–27.</summary>
    public static string? PartOf(uint tseType) => tseType switch
    {
        13 => "firstSubtotalColumn",
        14 => "secondSubtotalColumn",
        15 => "thirdSubtotalColumn",
        16 => "firstSubtotalRow",
        17 => "secondSubtotalRow",
        18 => "thirdSubtotalRow",
        19 => "blankRow",
        20 => "firstColumnSubheading",
        21 => "secondColumnSubheading",
        22 => "thirdColumnSubheading",
        23 => "firstRowSubheading",
        24 => "secondRowSubheading",
        25 => "thirdRowSubheading",
        26 => "pageFieldLabels",
        27 => "pageFieldValues",
        _ => SheetTables.PartOf(tseType),
    };

    /// <summary>The built-in PivotTable style of a name ("PivotStyleLight16"), its theme colours through <paramref name="color"/>.</summary>
    public static TableStyle? BuiltIn(string? name, Func<int, double, string?> color) =>
        name is not null && BuiltInStyles.TryGetValue(name, out var definition) ? SheetTables.Parse(definition, color) : null;

    /// <summary>
    /// What a rowItems / colItems item's t attribute makes it; its level is its r (the leading fields it repeats from
    /// the item before) plus its x values, less one.
    /// </summary>
    public static PivotItemKind Kind(string? type) => type switch
    {
        null or "data" => PivotItemKind.Data,
        "grand" => PivotItemKind.Grand,
        "blank" => PivotItemKind.Blank,
        _ => PivotItemKind.Subtotal,
    };

    /// <summary>
    /// The page fields' caption cells: <paramref name="count"/> fields in <paramref name="rowsPerColumn"/> rows ending
    /// two rows above the table, each column of them a label, a value and a gap; down then over, or over then down.
    /// </summary>
    public static List<(int Row, int Column)> PageCells(CellRange range, int count, int rowsPerColumn, int columns, bool overThenDown)
    {
        var cells = new List<(int, int)>();
        rowsPerColumn = Math.Max(1, rowsPerColumn);
        columns = Math.Max(1, columns);
        var top = range.Top - rowsPerColumn - 1;
        for (var i = 0; i < Math.Min(count, MaxPageFields); i++)
        {
            var (row, group) = overThenDown ? (i / columns, i % columns) : (i % rowsPerColumn, i / rowsPerColumn);
            var (r, c) = (top + row, range.Left + 3 * group);
            if (r >= 0 && r < range.Top && c + 1 <= SheetPagination.MaxColumn)
                cells.Add((r, c));
        }
        return cells;
    }

    /// <summary>
    /// Puts the look of each PivotTable's style on its cells, under the cells' own formatting: <paramref name="style"/>
    /// finds a style by name (the workbook's own first, then the built-in ones).
    /// </summary>
    public static void Apply(Worksheet sheet, IEnumerable<SheetPivot> pivots, Func<string?, TableStyle?> style, CellStyle normal, TableBudget budget)
    {
        foreach (var pivot in pivots)
        {
            if (budget.Visits <= 0)
                return;
            if (style(pivot.StyleName) is { } found)
                Apply(sheet, pivot, found, normal, budget);
        }
    }

    /// <summary>
    /// Puts the look of <paramref name="style"/> on the cells of <paramref name="pivot"/>: every cell of its rows while
    /// the budget's cells last (blank ones get the fill and borders), then only the cells the sheet has.
    /// </summary>
    public static void Apply(Worksheet sheet, SheetPivot pivot, TableStyle style, CellStyle normal, TableBudget budget)
    {
        var range = pivot.Range;
        if (range.Top < 0 || range.Left < 0 || range.Bottom < range.Top || range.Right < range.Left
            || range.Bottom > SheetPagination.MaxRow || range.Right > SheetPagination.MaxColumn)
            return;
        var height = range.Bottom - range.Top + 1;
        var dataTop = range.Top + Math.Clamp(pivot.FirstDataRow, 0, height);
        var headTop = range.Top + Math.Clamp(pivot.FirstHeaderRow, 0, dataTop - range.Top);
        var dataLeft = range.Left + Math.Clamp(pivot.FirstDataColumn, 0, range.Right - range.Left + 1);
        var parts = Order.Where(style.Parts.ContainsKey).Where(type => type switch
        {
            "firstColumnStripe" or "secondColumnStripe" => pivot.ColumnStripes,
            "firstRowStripe" or "secondRowStripe" => pivot.RowStripes,
            "firstColumn" or "firstRowSubheading" or "secondRowSubheading" or "thirdRowSubheading" => pivot.RowHeaders,
            "headerRow" or "firstColumnSubheading" or "secondColumnSubheading" or "thirdColumnSubheading" => pivot.ColumnHeaders,
            "lastColumn" => pivot.LastColumn,
            _ => true,
        }).Select(type => (Type: type, Part: style.Parts[type])).ToList();
        if (parts.Count == 0)
            return;
        var rowBand = SheetTables.Band(style, "firstRowStripe", "secondRowStripe");
        var columnBand = SheetTables.Band(style, "firstColumnStripe", "secondColumnStripe");
        PivotItem? RowItem(int row) => row >= dataTop && row - dataTop < pivot.Rows.Count ? pivot.Rows[row - dataTop] : null;
        PivotItem? ColumnItem(int column) => column >= dataLeft && column - dataLeft < pivot.Columns.Count ? pivot.Columns[column - dataLeft] : null;
        // The outermost field's subtotals and subheadings are the first; then the second and third alternate ([MS-XLSB]
        // TSEType: odd field indexes the second, even ones past 0 the third).
        static int Level(int level) => level <= 0 ? 0 : level % 2 == 1 ? 1 : 2;
        var across = (Left: range.Left, Right: range.Right);

        void Style(int row, int column, SheetCell? existing)
        {
            var look = new SheetTables.Look();
            var rowItem = RowItem(row);
            var columnItem = ColumnItem(column);
            foreach (var (type, part) in parts)
            {
                (int Top, int Bottom, int Left, int Right)? region = type switch
                {
                    "wholeTable" => (range.Top, range.Bottom, range.Left, range.Right),
                    "firstRowStripe" or "secondRowStripe" when row >= dataTop
                        && SheetTables.Stripe(row - dataTop, rowBand, type == "firstRowStripe") is { } rows
                        => (dataTop + rows.Start, Math.Min(range.Bottom, dataTop + rows.End), across.Left, across.Right),
                    "firstColumnStripe" or "secondColumnStripe" when row >= dataTop && column >= dataLeft
                        && SheetTables.Stripe(column - dataLeft, columnBand, type == "firstColumnStripe") is { } columns
                        => (dataTop, range.Bottom, dataLeft + columns.Start, Math.Min(range.Right, dataLeft + columns.End)),
                    "firstColumn" when column < dataLeft => (range.Top, range.Bottom, range.Left, dataLeft - 1),
                    "headerRow" when row < dataTop => (range.Top, dataTop - 1, across.Left, across.Right),
                    "firstHeaderCell" when row < dataTop - 1 && column < dataLeft => (range.Top, dataTop - 2, range.Left, dataLeft - 1),
                    "firstSubtotalColumn" or "secondSubtotalColumn" or "thirdSubtotalColumn"
                        when columnItem is { Kind: PivotItemKind.Subtotal } subtotal && SubtotalColumns[Level(subtotal.Level)] == type
                             && row >= headTop + subtotal.Level
                        => (headTop + subtotal.Level, range.Bottom, column, column),
                    "blankRow" when rowItem is { Kind: PivotItemKind.Blank } => (row, row, across.Left, across.Right),
                    "firstSubtotalRow" or "secondSubtotalRow" or "thirdSubtotalRow"
                        when rowItem is { Kind: PivotItemKind.Subtotal } subtotal && SubtotalRows[Level(subtotal.Level)] == type
                        => (row, row, across.Left, across.Right),
                    "firstColumnSubheading" or "secondColumnSubheading" or "thirdColumnSubheading" when ColumnSubheading(row, column) is { } heading
                        && ColumnSubheadings[Level(heading.Level)] == type
                        => heading.Region,
                    "firstRowSubheading" or "secondRowSubheading" or "thirdRowSubheading"
                        when rowItem is { Kind: PivotItemKind.Data } item && item.Level < pivot.RowFields - 1 && RowSubheadings[Level(item.Level)] == type
                        => (row, row, across.Left, across.Right),
                    "lastColumn" when columnItem is { Kind: PivotItemKind.Grand } && row >= headTop => (headTop, range.Bottom, column, column),
                    "totalRow" when rowItem is { Kind: PivotItemKind.Grand } => (row, row, across.Left, across.Right),
                    _ => null,
                };
                if (region is { } r)
                    look.Add(part, row, column, r);
            }
            Put(sheet, row, column, existing, look, normal);
        }

        // A column subheading: the caption row of an outer column field across the data columns, and a subtotal
        // column's caption from its field's row down to the last header row.
        (int Level, (int Top, int Bottom, int Left, int Right) Region)? ColumnSubheading(int row, int column)
        {
            if (row < headTop || row >= dataTop || column < dataLeft)
                return null;
            if (ColumnItem(column) is { Kind: PivotItemKind.Subtotal } subtotal && row >= headTop + subtotal.Level)
                return (subtotal.Level, (headTop + subtotal.Level, dataTop - 1, column, column));
            var level = row - headTop;
            return level < pivot.ColumnFields - 1 ? (level, (row, row, dataLeft, range.Right)) : null;
        }

        // The page fields: their labels and values, inside the whole table's look.
        var pageParts = parts.Where(p => p.Type is "wholeTable" or "pageFieldLabels" or "pageFieldValues").ToList();
        foreach (var (row, column) in pivot.PageFields)
        {
            if (budget.Cells < 2)
                break;
            budget.Cells -= 2;
            for (var value = 0; value < 2; value++)
            {
                var look = new SheetTables.Look();
                foreach (var (type, part) in pageParts)
                {
                    if (type == "wholeTable" || (type == "pageFieldValues") == (value == 1))
                        look.Add(part, row, column + value, (row, row, column + value, column + value));
                }
                Put(sheet, row, column + value, sheet.Get(row, column + value), look, normal);
            }
        }

        var width = range.Right - range.Left + 1;
        var next = range.Top;
        for (; next <= range.Bottom && budget.Cells >= width; next++)
        {
            budget.Cells -= width;
            for (var column = range.Left; column <= range.Right; column++)
                Style(next, column, sheet.Get(next, column));
        }
        if (next > range.Bottom)
            return;
        // Past the budget: the cells the sheet has (the rows are walked once per PivotTable), while the visits last.
        budget.Visits -= sheet.Rows.Count;
        if (budget.Visits < 0)
            return;
        foreach (var (row, cells) in sheet.Rows)
        {
            if (row < next)
                continue;
            if (row > range.Bottom)
                break;
            var columns = cells.Keys.Where(c => c >= range.Left && c <= range.Right).ToList();
            budget.Visits -= columns.Count;
            if (budget.Visits < 0)
                return;
            foreach (var column in columns)
                Style(row, column, cells[column]);
        }
    }

    /// <summary>Puts a cell's look under its own formatting (a blank cell only when the look fills or borders it).</summary>
    private static void Put(Worksheet sheet, int row, int column, SheetCell? existing, SheetTables.Look look, CellStyle normal)
    {
        if (!look.Any)
            return;
        var own = existing?.Style ?? normal;
        var styled = look.Under(own, normal);
        if (ReferenceEquals(styled, own))
            return;
        if (existing is not null)
            sheet.Set(row, column, existing with { Style = styled });
        else if (styled.HasBorders || styled.Fill is not null)
            sheet.Set(row, column, new SheetCell("", styled));
    }

    /// <summary>The built-in PivotTable styles of ECMA-376 Part 1 (presetTableStyles.xml), in SheetTables' short form.</summary>
    private static readonly Dictionary<string, string> BuiltInStyles = new(StringComparer.Ordinal)
    {
        ["PivotStyleLight1"] = "wholeTable:c1 Hthin@0~-0.15;headerRow:b c1 Tthin@1~0.5 Bthin@1~0.5;totalRow:b c1 f0 Tthin@1~0.5 Bthin@1~0.5;firstRowStripe:f0~-0.15 Tthin@0~-0.35 Bthin@0~-0.35;firstColumnStripe:f0~-0.15 Lthin@0~-0.35 Rthin@0~-0.35 Tthin@0~-0.35 Bthin@0~-0.35 Vthin@0~-0.35 Hthin@0~-0.35;firstSubtotalRow:b c1;secondSubtotalRow:b c1~0.5;firstRowSubheading:b c1;secondRowSubheading:b c1~0.5;pageFieldLabels:Tthin@1~0.5 Bthin@1~0.5;pageFieldValues:Tthin@1~0.5 Bthin@1~0.5",
        ["PivotStyleLight2"] = "wholeTable:c1 Hthin@4~0.8;headerRow:b c1 Tthin@4 Bthin@4;totalRow:b c1 f0 Tthin@4 Bthin@4;firstRowStripe:f4~0.8 Tthin@4~0.6 Bthin@4~0.6;firstColumnStripe:f4~0.8 Lthin@4~0.6 Rthin@4~0.6 Tthin@4~0.6 Bthin@4~0.6 Vthin@4~0.6 Hthin@4~0.6;firstSubtotalRow:b c1;secondSubtotalRow:c4;firstRowSubheading:b c1;secondRowSubheading:c4;pageFieldLabels:Tthin@4 Bthin@4;pageFieldValues:Tthin@4 Bthin@4",
        ["PivotStyleLight3"] = "wholeTable:c1 Hthin@5~0.8;headerRow:b c1 Tthin@5 Bthin@5;totalRow:b c1 f0 Tthin@5 Bthin@5;firstRowStripe:f5~0.8 Tthin@5~0.6 Bthin@5~0.6;firstColumnStripe:f5~0.8 Lthin@5~0.6 Rthin@5~0.6 Tthin@5~0.6 Bthin@5~0.6 Vthin@5~0.6 Hthin@5~0.6;firstSubtotalRow:b c1;secondSubtotalRow:b c5;firstRowSubheading:b c1;secondRowSubheading:b c5;pageFieldLabels:Tthin@5 Bthin@5;pageFieldValues:Tthin@5 Bthin@5",
        ["PivotStyleLight4"] = "wholeTable:c1 Hthin@6~0.8;headerRow:b c1 Tthin@6 Bthin@6;totalRow:b c1 f0 Tthin@6 Bthin@6;firstRowStripe:f6~0.8 Tthin@6~0.6 Bthin@6~0.6;firstColumnStripe:f6~0.8 Lthin@6~0.6 Rthin@6~0.6 Tthin@6~0.6 Bthin@6~0.6 Vthin@6~0.6 Hthin@6~0.6;firstSubtotalRow:b c1;secondSubtotalRow:b c6;firstRowSubheading:b c1;secondRowSubheading:b c6;pageFieldLabels:Tthin@6 Bthin@6;pageFieldValues:Tthin@6 Bthin@6",
        ["PivotStyleLight5"] = "wholeTable:c1 Hthin@7~0.8;headerRow:b c1 Tthin@7 Bthin@7;totalRow:b c1 f0 Tthin@7 Bthin@7;firstRowStripe:f7~0.8 Tthin@7~0.6 Bthin@7~0.6;firstColumnStripe:f7~0.8 Lthin@7~0.6 Rthin@7~0.6 Tthin@7~0.6 Bthin@7~0.6 Vthin@7~0.6 Hthin@7~0.6;firstSubtotalRow:b c1;secondSubtotalRow:b c7;firstRowSubheading:b c1;secondRowSubheading:b c7;pageFieldLabels:Tthin@7 Bthin@7;pageFieldValues:Tthin@7 Bthin@7",
        ["PivotStyleLight6"] = "wholeTable:c1 Hthin@8~0.8;headerRow:b c1 Tthin@8 Bthin@8;totalRow:b c1 f0 Tthin@8 Bthin@8;firstRowStripe:f8~0.8 Tthin@8~0.6 Bthin@8~0.6;firstColumnStripe:f8~0.8 Lthin@8~0.6 Rthin@8~0.6 Tthin@8~0.6 Bthin@8~0.6 Vthin@8~0.6 Hthin@8~0.6;firstSubtotalRow:b c1;secondSubtotalRow:b c8;firstRowSubheading:b c1;secondRowSubheading:b c8;pageFieldLabels:Tthin@8 Bthin@8;pageFieldValues:Tthin@8 Bthin@8",
        ["PivotStyleLight7"] = "wholeTable:c1 Hthin@9~0.8;headerRow:b c1 Tthin@9 Bthin@9;totalRow:b c1 f0 Tthin@9 Bthin@9;firstRowStripe:f9~0.8 Tthin@9~0.6 Bthin@9~0.6;firstColumnStripe:f9~0.8 Lthin@9~0.6 Rthin@9~0.6 Tthin@9~0.6 Bthin@9~0.6 Vthin@9~0.6 Hthin@9~0.6;firstSubtotalRow:b c1;secondSubtotalRow:b c9;firstRowSubheading:b c1;secondRowSubheading:b c9;pageFieldLabels:Tthin@9 Bthin@9;pageFieldValues:Tthin@9 Bthin@9",
        ["PivotStyleLight8"] = "wholeTable:c1 Hthin@0~-0.15;headerRow:b c1 Lmedium@1~0.5 Rmedium@1~0.5 Tmedium@1~0.5 Bmedium@1~0.5 Hthin@0;totalRow:b c1 Lmedium@1~0.5 Rmedium@1~0.5 Tmedium@1~0.5 Bmedium@1~0.5;firstColumn:Rthin@1~0.5;firstRowStripe:Lthin@0~-0.35 Rthin@0~-0.35 Tthin@0~-0.35 Bthin@0~-0.35;firstColumnStripe:Lthin@0~-0.45 Rthin@0~-0.45 Tthin@0~-0.45 Bthin@0~-0.45;firstSubtotalRow:b c1 f0~-0.15 Tthin@0~-0.35 Bthin@0~-0.35;secondSubtotalRow:b c1;secondColumnSubheading:Tthin@0~-0.35;firstRowSubheading:b c1 f0~-0.15 Bthin@0;secondRowSubheading:b c1",
        ["PivotStyleLight9"] = "wholeTable:c4~-0.25 Hthin@4~0.8;headerRow:b c1 Lmedium@4 Rmedium@4 Tmedium@4 Bmedium@4 Hthin@0;totalRow:b c1 Lmedium@4 Rmedium@4 Tmedium@4 Bmedium@4;firstColumn:Rthin@4;firstRowStripe:Lthin@4~0.6 Rthin@4~0.6 Tthin@4~0.6 Bthin@4~0.6;firstColumnStripe:Lthin@4 Rthin@4 Tthin@4 Bthin@4;firstSubtotalRow:b c1 f4~0.8 Tthin@4~0.6 Bthin@4~0.6;secondSubtotalRow:b c1;secondColumnSubheading:Tthin@4~0.8;thirdColumnSubheading:Tthin@4~0.8;firstRowSubheading:b c1 f4~0.8 Bthin@0;secondRowSubheading:b c1",
        ["PivotStyleLight10"] = "wholeTable:c5~-0.25 Hthin@5~0.8;headerRow:b c1 Lmedium@5 Rmedium@5 Tmedium@5 Bmedium@5 Hthin@0;totalRow:b c1 Lmedium@5 Rmedium@5 Tmedium@5 Bmedium@5;firstColumn:Rthin@5;firstRowStripe:Lthin@5~0.6 Rthin@5~0.6 Tthin@5~0.6 Bthin@5~0.6;firstColumnStripe:Lthin@5 Rthin@5 Tthin@5 Bthin@5;firstSubtotalRow:b c1 f5~0.8 Tthin@5~0.6 Bthin@5~0.6;secondSubtotalRow:b c1;secondColumnSubheading:Tthin@5~0.8;thirdColumnSubheading:Tthin@5~0.8;firstRowSubheading:b c1 f5~0.8 Bthin@0;secondRowSubheading:b c1",
        ["PivotStyleLight11"] = "wholeTable:c6~-0.25 Hthin@6~0.8;headerRow:b c1 Lmedium@6 Rmedium@6 Tmedium@6 Bmedium@6 Hthin@0;totalRow:b c1 Lmedium@6 Rmedium@6 Tmedium@6 Bmedium@6;firstColumn:Rthin@6;firstRowStripe:Lthin@6~0.6 Rthin@6~0.6 Tthin@6~0.6 Bthin@6~0.6;firstColumnStripe:Lthin@6 Rthin@6 Tthin@6 Bthin@6;firstSubtotalRow:b c1 f6~0.8 Tthin@6~0.6 Bthin@6~0.6;secondSubtotalRow:b c1;secondColumnSubheading:Tthin@6~0.8;thirdColumnSubheading:Tthin@6~0.8;firstRowSubheading:b c1 f6~0.8 Bthin@0;secondRowSubheading:b c1",
        ["PivotStyleLight12"] = "wholeTable:c7~-0.25 Hthin@7~0.8;headerRow:b c1 Lmedium@7 Rmedium@7 Tmedium@7 Bmedium@7 Hthin@0;totalRow:b c1 Lmedium@7 Rmedium@7 Tmedium@7 Bmedium@7;firstColumn:Rthin@7;firstRowStripe:Lthin@7~0.6 Rthin@7~0.6 Tthin@7~0.6 Bthin@7~0.6;firstColumnStripe:Lthin@7 Rthin@7 Tthin@7 Bthin@7;firstSubtotalRow:b c1 f7~0.8 Tthin@7~0.6 Bthin@7~0.6;secondSubtotalRow:b c1;secondColumnSubheading:Tthin@7~0.8;thirdColumnSubheading:Tthin@7~0.8;firstRowSubheading:b c1 f7~0.8 Bthin@0;secondRowSubheading:b c1",
        ["PivotStyleLight13"] = "wholeTable:c8~-0.25 Hthin@8~0.8;headerRow:b c1 Lmedium@8 Rmedium@8 Tmedium@8 Bmedium@8 Hthin@0;totalRow:b c1 Lmedium@8 Rmedium@8 Tmedium@8 Bmedium@8;firstColumn:Rthin@8;firstRowStripe:Lthin@8~0.6 Rthin@8~0.6 Tthin@8~0.6 Bthin@8~0.6;firstColumnStripe:Lthin@8 Rthin@8 Tthin@8 Bthin@8;firstSubtotalRow:b c1 f8~0.8 Tthin@8~0.6 Bthin@8~0.6;secondSubtotalRow:b c1;secondColumnSubheading:Tthin@8~0.8;thirdColumnSubheading:Tthin@8~0.8;firstRowSubheading:b c1 f8~0.8 Bthin@0;secondRowSubheading:b c1",
        ["PivotStyleLight14"] = "wholeTable:c9~-0.25 Hthin@9~0.8;headerRow:b c1 Lmedium@9 Rmedium@9 Tmedium@9 Bmedium@9 Hthin@0;totalRow:b c1 Lmedium@9 Rmedium@9 Tmedium@9 Bmedium@9;firstColumn:Rthin@9;firstRowStripe:Lthin@9~0.6 Rthin@9~0.6 Tthin@9~0.6 Bthin@9~0.6;firstColumnStripe:Lthin@9 Rthin@9 Tthin@9 Bthin@9;firstSubtotalRow:b c1 f9~0.8 Tthin@9~0.6 Bthin@9~0.6;secondSubtotalRow:b c1;secondColumnSubheading:Tthin@9~0.8;thirdColumnSubheading:Tthin@9~0.8;firstRowSubheading:b c1 f9~0.8 Bthin@0;secondRowSubheading:b c1",
        ["PivotStyleLight15"] = "headerRow:b c1 f0~-0.15 Bthin@0~-0.35;totalRow:b c1 f0~-0.15 Tthin@0~-0.35;firstRowStripe:f0~-0.15;firstColumnStripe:f0~-0.15 Lthin@0~-0.25 Rthin@0~-0.25;firstSubtotalColumn:f0~-0.15;firstSubtotalRow:b c1 Tthin@1~0.5 Bthin@1~0.5;secondSubtotalRow:b c1;firstRowSubheading:b c1 Bthin@0~-0.35;secondRowSubheading:b c1;pageFieldLabels:f0~-0.15 Bthin@0~-0.35;pageFieldValues:f0~-0.15 Bthin@0~-0.35",
        ["PivotStyleLight16"] = "headerRow:b c1 f4~0.8 Bthin@4~0.4;totalRow:b c1 f4~0.8 Tthin@4~0.4;firstRowStripe:f0~-0.15;firstColumnStripe:f0~-0.15 Lthin@0~-0.25 Rthin@0~-0.25;firstSubtotalColumn:f0~-0.15;firstSubtotalRow:b c1 Tthin@4 Bthin@4;secondSubtotalRow:b c1;firstRowSubheading:b c1 Bthin@4~0.4;secondRowSubheading:b c1;pageFieldLabels:f4~0.8 Bthin@4~0.4;pageFieldValues:f4~0.8 Bthin@4~0.4",
        ["PivotStyleLight17"] = "headerRow:b c1 f5~0.8 Bthin@5~0.4;totalRow:b c1 f5~0.8 Tthin@5~0.4;firstRowStripe:f0~-0.15;firstColumnStripe:f0~-0.15 Lthin@0~-0.25 Rthin@0~-0.25;firstSubtotalColumn:f0~-0.15;firstSubtotalRow:b c1 Tthin@5 Bthin@5;secondSubtotalRow:b c1;firstRowSubheading:b c1 Bthin@5~0.4;secondRowSubheading:b c1;pageFieldLabels:f5~0.8 Bthin@5~0.4;pageFieldValues:f5~0.8 Bthin@5~0.4",
        ["PivotStyleLight18"] = "headerRow:b c1 f6~0.8 Bthin@6~0.4;totalRow:b c1 f6~0.8 Tthin@6~0.4;firstRowStripe:f0~-0.15;firstColumnStripe:f0~-0.15 Lthin@0~-0.25 Rthin@0~-0.25;firstSubtotalColumn:f0~-0.15;firstSubtotalRow:b c1 Tthin@6 Bthin@6;secondSubtotalRow:b c1;firstRowSubheading:b c1 Bthin@6~0.4;secondRowSubheading:b c1;pageFieldLabels:f6~0.8 Bthin@6~0.4;pageFieldValues:f6~0.8 Bthin@6~0.4",
        ["PivotStyleLight19"] = "headerRow:b c1 f7~0.8 Bthin@7~0.4;totalRow:b c1 f7~0.8 Tthin@7~0.4;firstRowStripe:f0~-0.15;firstColumnStripe:f0~-0.15 Lthin@0~-0.25 Rthin@0~-0.25;firstSubtotalColumn:f0~-0.15;firstSubtotalRow:b c1 Tthin@7 Bthin@7;secondSubtotalRow:b c1;firstRowSubheading:b c1 Bthin@7~0.4;secondRowSubheading:b c1;pageFieldLabels:f7~0.8 Bthin@7~0.4;pageFieldValues:f7~0.8 Bthin@7~0.4",
        ["PivotStyleLight20"] = "headerRow:b c1 f8~0.8 Bthin@8~0.4;totalRow:b c1 f8~0.8 Tthin@8~0.4;firstRowStripe:f0~-0.15;firstColumnStripe:f0~-0.15 Lthin@0~-0.25 Rthin@0~-0.25;firstSubtotalColumn:f0~-0.15;firstSubtotalRow:b c1 Tthin@8 Bthin@8;secondSubtotalRow:b c1;firstRowSubheading:b c1 Bthin@8~0.4;secondRowSubheading:b c1;pageFieldLabels:f8~0.8 Bthin@8~0.4;pageFieldValues:f8~0.8 Bthin@8~0.4",
        ["PivotStyleLight21"] = "headerRow:b c1 f9~0.8 Bthin@9~0.4;totalRow:b c1 f9~0.8 Tthin@9~0.4;firstRowStripe:f0~-0.15;firstColumnStripe:f0~-0.15 Lthin@0~-0.25 Rthin@0~-0.25;firstSubtotalColumn:f0~-0.15;firstSubtotalRow:b c1 Tthin@9 Bthin@9;secondSubtotalRow:b c1;firstRowSubheading:b c1 Bthin@9~0.4;secondRowSubheading:b c1;pageFieldLabels:f9~0.8 Bthin@9~0.4;pageFieldValues:f9~0.8 Bthin@9~0.4",
        ["PivotStyleLight22"] = "wholeTable:c1 Lthin@1~0.5 Rthin@1~0.5 Tthin@1~0.5 Bthin@1~0.5 Vthin@1~0.5;headerRow:b c1 Bthin@1~0.5;totalRow:b c1 Tthin@1~0.5;firstColumn:b c1;firstRowStripe:f0~-0.15 Tthin@1~0.5 Bthin@1~0.5;firstColumnStripe:f0~-0.15;firstSubtotalColumn:b c1 Lthin@0~-0.35 Rthin@0~-0.35;firstSubtotalRow:b c1;secondSubtotalRow:b c1;pageFieldLabels:b c1",
        ["PivotStyleLight23"] = "wholeTable:c4~-0.25 Lthin@4 Rthin@4 Tthin@4 Bthin@4 Vthin@4;headerRow:c4~-0.25 Bthin@4;totalRow:c4~-0.25 Tthin@4;firstColumn:c4~-0.25;firstRowStripe:f4~0.8 Tthin@1~0.5 Bthin@1~0.5;firstColumnStripe:f4~0.8;firstSubtotalColumn:c4~-0.25;firstSubtotalRow:c4~-0.25;secondSubtotalRow:c4~-0.25;pageFieldLabels:c4~-0.25",
        ["PivotStyleLight24"] = "wholeTable:c5~-0.25 Lthin@5 Rthin@5 Tthin@5 Bthin@5 Vthin@5;headerRow:c5~-0.25 Bthin@5;totalRow:c5~-0.25 Tthin@5;firstColumn:c5~-0.25;firstRowStripe:f5~0.8 Tthin@1~0.5 Bthin@1~0.5;firstColumnStripe:f5~0.8;firstSubtotalColumn:c5~-0.25;firstSubtotalRow:c5~-0.25;secondSubtotalRow:c5~-0.25;pageFieldLabels:c5~-0.25",
        ["PivotStyleLight25"] = "wholeTable:c6~-0.25 Lthin@6 Rthin@6 Tthin@6 Bthin@6 Vthin@6;headerRow:c6~-0.25 Bthin@6;totalRow:c6~-0.25 Tthin@6;firstColumn:c6~-0.25;firstRowStripe:f6~0.8 Tthin@1~0.5 Bthin@1~0.5;firstColumnStripe:f6~0.8;firstSubtotalColumn:c6~-0.25;firstSubtotalRow:c6~-0.25;secondSubtotalRow:c6~-0.25;pageFieldLabels:c6~-0.25",
        ["PivotStyleLight26"] = "wholeTable:c7~-0.25 Lthin@7 Rthin@7 Tthin@7 Bthin@7 Vthin@7;headerRow:c7~-0.25 Bthin@7;totalRow:c7~-0.25 Tthin@7;firstColumn:c7~-0.25;firstRowStripe:f7~0.8 Tthin@1~0.5 Bthin@1~0.5;firstColumnStripe:f7~0.8;firstSubtotalColumn:c7~-0.25;firstSubtotalRow:c7~-0.25;secondSubtotalRow:c7~-0.25;pageFieldLabels:c7~-0.25",
        ["PivotStyleLight27"] = "wholeTable:c8~-0.25 Lthin@8 Rthin@8 Tthin@8 Bthin@8 Vthin@8;headerRow:c8~-0.25 Bthin@8;totalRow:c8~-0.25 Tthin@8;firstColumn:c8~-0.25;firstRowStripe:f8~0.8 Tthin@1~0.5 Bthin@1~0.5;firstColumnStripe:f8~0.8;firstSubtotalColumn:c8~-0.25;firstSubtotalRow:c8~-0.25;secondSubtotalRow:c8~-0.25;pageFieldLabels:c8~-0.25",
        ["PivotStyleLight28"] = "wholeTable:c9~-0.25 Lthin@9 Rthin@9 Tthin@9 Bthin@9 Vthin@9;headerRow:c9~-0.25 Bthin@9;totalRow:c9~-0.25 Tthin@9;firstColumn:c9~-0.25;firstRowStripe:f9~0.8 Tthin@1~0.5 Bthin@1~0.5;firstColumnStripe:f9~0.8;firstSubtotalColumn:c9~-0.25;firstSubtotalRow:c9~-0.25;secondSubtotalRow:c9~-0.25;pageFieldLabels:c9~-0.25",
        ["PivotStyleMedium1"] = "wholeTable:c1 Hthin@0~-0.15;headerRow:c0 f1~0.5 Hthin@1~0.5;totalRow:b c1 Tdouble@1~0.5;firstRowStripe:Tthin@1~0.5 Bthin@1~0.5 Hthin@1~0.5;firstColumnStripe:Lthin@1~0.5 Rthin@1~0.5;firstHeaderCell:b c0;firstSubtotalRow:b c0 f0~-0.35;secondSubtotalRow:b c1 f0~-0.15;firstColumnSubheading:Bthin@0~-0.15;firstRowSubheading:c0 f0~-0.35 Bthin@0~-0.15 Hthin@0~-0.15;secondRowSubheading:f0~-0.15 Bthin@0~-0.15;pageFieldLabels:Tthin@0~-0.15 Bthin@0~-0.15;pageFieldValues:Tthin@0~-0.15 Bthin@0~-0.15",
        ["PivotStyleMedium2"] = "wholeTable:c1 Hthin@4~0.8;headerRow:c0 f4~-0.25 Hthin@4~-0.25;totalRow:b c1 Tdouble@4~-0.25;firstRowStripe:Tthin@4~-0.25 Bthin@4~-0.25 Hthin@4~-0.25;firstColumnStripe:Lthin@4~-0.25 Rthin@4~-0.25;firstHeaderCell:b c0;firstSubtotalRow:b c0 f4~0.4;secondSubtotalRow:b c1 f0~-0.15;firstColumnSubheading:Bthin@4~0.6;firstRowSubheading:c0 f4~0.4 Bthin@4~0.8 Hthin@4~0.4;secondRowSubheading:f4~0.8 Bthin@4;pageFieldLabels:Tthin@4~0.8 Bthin@4~0.8;pageFieldValues:Tthin@4~0.8 Bthin@4~0.8",
        ["PivotStyleMedium3"] = "wholeTable:c1 Hthin@5~0.8;headerRow:c0 f5~-0.25 Hthin@5~-0.25;totalRow:b c1 Tdouble@5~-0.25;firstRowStripe:Tthin@5~-0.25 Bthin@5~-0.25 Hthin@5~-0.25;firstColumnStripe:Lthin@5~-0.25 Rthin@5~-0.25;firstHeaderCell:b c0;firstSubtotalRow:b c0 f5~0.4;secondSubtotalRow:b c1 f0~-0.15;firstColumnSubheading:Bthin@5~0.6;firstRowSubheading:c0 f5~0.4 Bthin@5~0.8 Hthin@5~0.4;secondRowSubheading:f5~0.8 Bthin@5;pageFieldLabels:Tthin@5~0.8 Bthin@5~0.8;pageFieldValues:Tthin@5~0.8 Bthin@5~0.8",
        ["PivotStyleMedium4"] = "wholeTable:c1 Hthin@6~0.8;headerRow:c0 f6~-0.25 Hthin@6~-0.25;totalRow:b c1 Tdouble@6~-0.25;firstRowStripe:Tthin@6~-0.25 Bthin@6~-0.25 Hthin@6~-0.25;firstColumnStripe:Lthin@6~-0.25 Rthin@6~-0.25;firstHeaderCell:b c0;firstSubtotalRow:b c0 f6~0.4;secondSubtotalRow:b c1 f0~-0.15;firstColumnSubheading:Bthin@6~0.6;firstRowSubheading:c0 f6~0.4 Bthin@6~0.8 Hthin@6~0.4;secondRowSubheading:f6~0.8 Bthin@6;pageFieldLabels:Tthin@6~0.8 Bthin@6~0.8;pageFieldValues:Tthin@6~0.8 Bthin@6~0.8",
        ["PivotStyleMedium5"] = "wholeTable:c1 Hthin@7~0.8;headerRow:c0 f7~-0.25 Hthin@7~-0.25;totalRow:b c1 Tdouble@7~-0.25;firstRowStripe:Tthin@7~-0.25 Bthin@7~-0.25 Hthin@7~-0.25;firstColumnStripe:Lthin@7~-0.25 Rthin@7~-0.25;firstHeaderCell:b c0;firstSubtotalRow:b c0 f7~0.4;secondSubtotalRow:b c1 f0~-0.15;firstColumnSubheading:Bthin@7~0.6;firstRowSubheading:c0 f7~0.4 Bthin@7~0.8 Hthin@7~0.4;secondRowSubheading:f7~0.8 Bthin@7;pageFieldLabels:Tthin@7~0.8 Bthin@7~0.8;pageFieldValues:Tthin@7~0.8 Bthin@7~0.8",
        ["PivotStyleMedium6"] = "wholeTable:c1 Hthin@8~0.8;headerRow:c0 f8~-0.25 Hthin@8~-0.25;totalRow:b c1 Tdouble@8~-0.25;firstRowStripe:Tthin@8~-0.25 Bthin@8~-0.25 Hthin@8~-0.25;firstColumnStripe:Lthin@8~-0.25 Rthin@8~-0.25;firstHeaderCell:b c0;firstSubtotalRow:b c0 f8~0.4;secondSubtotalRow:b c1 f0~-0.15;firstColumnSubheading:Bthin@8~0.6;firstRowSubheading:c0 f8~0.4 Bthin@8~0.8 Hthin@8~0.4;secondRowSubheading:f8~0.8 Bthin@8;pageFieldLabels:Tthin@8~0.8 Bthin@8~0.8;pageFieldValues:Tthin@8~0.8 Bthin@8~0.8",
        ["PivotStyleMedium7"] = "wholeTable:c1 Hthin@9~0.8;headerRow:c0 f9~-0.25 Hthin@9~-0.25;totalRow:b c1 Tdouble@9~-0.25;firstRowStripe:Tthin@9~-0.25 Bthin@9~-0.25 Hthin@9~-0.25;firstColumnStripe:Lthin@9~-0.25 Rthin@9~-0.25;firstHeaderCell:b c0;firstSubtotalRow:b c0 f9~0.4;secondSubtotalRow:b c1 f0~-0.15;firstColumnSubheading:Bthin@9~0.6;firstRowSubheading:c0 f9~0.4 Bthin@9~0.8 Hthin@9~0.4;secondRowSubheading:f9~0.8 Bthin@9;pageFieldLabels:Tthin@9~0.8 Bthin@9~0.8;pageFieldValues:Tthin@9~0.8 Bthin@9~0.8",
        ["PivotStyleMedium8"] = "wholeTable:c1;headerRow:b c0 f1~0.5 Tmedium@1~0.5;totalRow:b c1 Tthin@1 Bmedium@1~0.5;firstRowStripe:Tthin@0~-0.25 Bthin@0~-0.25 Hthin@0~-0.25;firstColumnStripe:Lmedium@0~-0.15 Rmedium@0~-0.15;firstSubtotalColumn:b c1 Lmedium@0~-0.25 Rmedium@0~-0.25 Tmedium@0~-0.25 Bmedium@0~-0.25;firstSubtotalRow:b c1 f0~-0.25;secondSubtotalRow:b c1;firstRowSubheading:b c1 f0~-0.15;secondRowSubheading:b c1;pageFieldLabels:f0~-0.15;pageFieldValues:f0~-0.15",
        ["PivotStyleMedium9"] = "wholeTable:c1;headerRow:b c0 f4 Tmedium@4~-0.25;totalRow:b c1 Tthin@4~-0.25 Bmedium@4~-0.25;firstRowStripe:Tthin@4~0.4 Bthin@4~0.4 Hthin@4~0.4;firstColumnStripe:Lthin@4~0.4 Rthin@4~0.4;firstSubtotalColumn:b c1 Lmedium@4~0.6 Rmedium@4~0.6 Tmedium@4~0.6 Bmedium@4~0.6;firstSubtotalRow:b c1 f4~0.6;secondSubtotalRow:b c1;firstRowSubheading:b c1 f4~0.8;secondRowSubheading:b c1;pageFieldLabels:f4~0.8;pageFieldValues:f4~0.8",
        ["PivotStyleMedium10"] = "wholeTable:c1;headerRow:b c0 f5 Tmedium@5~-0.25;totalRow:b c1 Tthin@5~-0.25 Bmedium@5~-0.25;firstRowStripe:Tthin@5~0.4 Bthin@5~0.4 Hthin@5~0.4;firstColumnStripe:Lthin@5~0.4 Rthin@5~0.4;firstSubtotalColumn:b c1 Lmedium@5~0.6 Rmedium@5~0.6 Tmedium@5~0.6 Bmedium@5~0.6;firstSubtotalRow:b c1 f5~0.6;secondSubtotalRow:b c1;firstRowSubheading:b c1 f5~0.8;secondRowSubheading:b c1;pageFieldLabels:f5~0.8;pageFieldValues:f5~0.8",
        ["PivotStyleMedium11"] = "wholeTable:c1;headerRow:b c0 f6 Tmedium@6~-0.25;totalRow:b c1 Tthin@6~-0.25 Bmedium@6~-0.25;firstRowStripe:Tthin@6~0.4 Bthin@6~0.4 Hthin@6~0.4;firstColumnStripe:Lthin@6~0.4 Rthin@6~0.4;firstSubtotalColumn:b c1 Lmedium@6~0.6 Rmedium@6~0.6 Tmedium@6~0.6 Bmedium@6~0.6;firstSubtotalRow:b c1 f6~0.6;secondSubtotalRow:b c1;firstRowSubheading:b c1 f6~0.8;secondRowSubheading:b c1;pageFieldLabels:f6~0.8;pageFieldValues:f6~0.8",
        ["PivotStyleMedium12"] = "wholeTable:c1;headerRow:b c0 f7 Tmedium@7~-0.25;totalRow:b c1 Tthin@7~-0.25 Bmedium@7~-0.25;firstRowStripe:Tthin@7~0.4 Bthin@7~0.4 Hthin@7~0.4;firstColumnStripe:Lthin@7~0.4 Rthin@7~0.4;firstSubtotalColumn:b c1 Lmedium@7~0.6 Rmedium@7~0.6 Tmedium@7~0.6 Bmedium@7~0.6;firstSubtotalRow:b c1 f7~0.6;secondSubtotalRow:b c1;firstRowSubheading:b c1 f7~0.8;secondRowSubheading:b c1;pageFieldLabels:f7~0.8;pageFieldValues:f7~0.8",
        ["PivotStyleMedium13"] = "wholeTable:c1;headerRow:b c0 f8 Tmedium@8~-0.25;totalRow:b c1 Tthin@8~-0.25 Bmedium@8~-0.25;firstRowStripe:Tthin@8~0.4 Bthin@8~0.4 Hthin@8~0.4;firstColumnStripe:Lthin@8~0.4 Rthin@8~0.4;firstSubtotalColumn:b c1 Lmedium@8~0.6 Rmedium@8~0.6 Tmedium@8~0.6 Bmedium@8~0.6;firstSubtotalRow:b c1 f8~0.6;secondSubtotalRow:b c1;firstRowSubheading:b c1 f8~0.8;secondRowSubheading:b c1;pageFieldLabels:f8~0.8;pageFieldValues:f8~0.8",
        ["PivotStyleMedium14"] = "wholeTable:c1;headerRow:b c0 f9 Tmedium@9~-0.25;totalRow:b c1 Tthin@9~-0.25 Bmedium@9~-0.25;firstRowStripe:Tthin@9~0.4 Bthin@9~0.4 Hthin@9~0.4;firstColumnStripe:Lthin@9~0.4 Rthin@9~0.4;firstSubtotalColumn:b c1 Lmedium@9~0.6 Rmedium@9~0.6 Tmedium@9~0.6 Bmedium@9~0.6;firstSubtotalRow:b c1 f9~0.6;secondSubtotalRow:b c1;firstRowSubheading:b c1 f9~0.8;secondRowSubheading:b c1;pageFieldLabels:f9~0.8;pageFieldValues:f9~0.8",
        ["PivotStyleMedium15"] = "wholeTable:c1 f0~-0.05 Lthin@0~-0.15 Rthin@0~-0.15 Vthin@0~-0.15;headerRow:c0 f1;totalRow:c0 f1;firstRowStripe:f0~-0.15 Tthin@0~-0.25 Bthin@0~-0.25;firstColumnStripe:f0~-0.15 Lthin@0~-0.25 Rthin@0~-0.25;firstSubtotalColumn:c1 f0~-0.25 Lthin@0~-0.35 Rthin@0~-0.35 Tthin@0~-0.35;firstSubtotalRow:b c1 Bthin@0~-0.25;secondSubtotalRow:b c1~0.5;firstRowSubheading:b c1;secondRowSubheading:b c1~0.5;thirdRowSubheading:b c1;pageFieldLabels:Tthin@1 Bthin@1;pageFieldValues:Tthin@1 Bthin@1",
        ["PivotStyleMedium16"] = "wholeTable:c1 f4~0.8 Lthin@4~0.6 Rthin@4~0.6 Vthin@4~0.6;headerRow:c0 f1;totalRow:c0 f1;firstRowStripe:f4~0.8 Tthin@4~0.6 Bthin@4~0.6;firstColumnStripe:f4~0.8 Lthin@4~0.6 Rthin@4~0.6;firstSubtotalColumn:c1 f4~0.6 Lthin@4~0.4 Rthin@4~0.4 Tthin@4~0.4;firstSubtotalRow:b c1 Bthin@4~0.6;secondSubtotalRow:b c1~0.5;firstRowSubheading:b c1;secondRowSubheading:b c1~0.5;thirdRowSubheading:b c1;pageFieldLabels:Tthin@1 Bthin@1;pageFieldValues:Tthin@1 Bthin@1",
        ["PivotStyleMedium17"] = "wholeTable:c1 f5~0.8 Lthin@5~0.6 Rthin@5~0.6 Vthin@5~0.6;headerRow:c0 f1;totalRow:c0 f1;firstRowStripe:f5~0.8 Tthin@5~0.6 Bthin@5~0.6;firstColumnStripe:f5~0.8 Lthin@5~0.6 Rthin@5~0.6;firstSubtotalColumn:c1 f5~0.6 Lthin@5~0.4 Rthin@5~0.4 Tthin@5~0.4;firstSubtotalRow:b c1 Bthin@5~0.6;secondSubtotalRow:b c1~0.5;firstRowSubheading:b c1;secondRowSubheading:b c1~0.5;thirdRowSubheading:b c1;pageFieldLabels:Tthin@1 Bthin@1;pageFieldValues:Tthin@1 Bthin@1",
        ["PivotStyleMedium18"] = "wholeTable:c1 f6~0.8 Lthin@6~0.6 Rthin@6~0.6 Vthin@6~0.6;headerRow:c0 f1;totalRow:c0 f1;firstRowStripe:f6~0.8 Tthin@6~0.6 Bthin@6~0.6;firstColumnStripe:f6~0.8 Lthin@6~0.6 Rthin@6~0.6;firstSubtotalColumn:c1 f6~0.6 Lthin@6~0.4 Rthin@6~0.4 Tthin@6~0.4;firstSubtotalRow:b c1 Bthin@6~0.6;secondSubtotalRow:b c1~0.5;firstRowSubheading:b c1;secondRowSubheading:b c1~0.5;thirdRowSubheading:b c1;pageFieldLabels:Tthin@1 Bthin@1;pageFieldValues:Tthin@1 Bthin@1",
        ["PivotStyleMedium19"] = "wholeTable:c1 f7~0.8 Lthin@7~0.6 Rthin@7~0.6 Vthin@7~0.6;headerRow:c0 f1;totalRow:c0 f1;firstRowStripe:f7~0.8 Tthin@7~0.6 Bthin@7~0.6;firstColumnStripe:f7~0.8 Lthin@7~0.6 Rthin@7~0.6;firstSubtotalColumn:c1 f7~0.6 Lthin@7~0.4 Rthin@7~0.4 Tthin@7~0.4;firstSubtotalRow:b c1 Bthin@7~0.6;secondSubtotalRow:b c1~0.5;firstRowSubheading:b c1;secondRowSubheading:b c1~0.5;thirdRowSubheading:b c1;pageFieldLabels:Tthin@1 Bthin@1;pageFieldValues:Tthin@1 Bthin@1",
        ["PivotStyleMedium20"] = "wholeTable:c1 f8~0.8 Lthin@8~0.6 Rthin@8~0.6 Vthin@8~0.6;headerRow:c0 f1;totalRow:c0 f1;firstRowStripe:f8~0.8 Tthin@8~0.6 Bthin@8~0.6;firstColumnStripe:f8~0.8 Lthin@8~0.6 Rthin@8~0.6;firstSubtotalColumn:c1 f8~0.6 Lthin@8~0.4 Rthin@8~0.4 Tthin@8~0.4;firstSubtotalRow:b c1 Bthin@8~0.6;secondSubtotalRow:b c1~0.5;firstRowSubheading:b c1;secondRowSubheading:b c1~0.5;thirdRowSubheading:b c1;pageFieldLabels:Tthin@1 Bthin@1;pageFieldValues:Tthin@1 Bthin@1",
        ["PivotStyleMedium21"] = "wholeTable:c1 f9~0.8 Lthin@9~0.6 Rthin@9~0.6 Vthin@9~0.6;headerRow:c0 f1;totalRow:c0 f1;firstRowStripe:f9~0.8 Tthin@9~0.6 Bthin@9~0.6;firstColumnStripe:f9~0.8 Lthin@9~0.6 Rthin@9~0.6;firstSubtotalColumn:c1 f9~0.6 Lthin@9~0.4 Rthin@9~0.4 Tthin@9~0.4;firstSubtotalRow:b c1 Bthin@9~0.6;secondSubtotalRow:b c1~0.5;firstRowSubheading:b c1;secondRowSubheading:b c1~0.5;thirdRowSubheading:b c1;pageFieldLabels:Tthin@1 Bthin@1;pageFieldValues:Tthin@1 Bthin@1",
        ["PivotStyleMedium22"] = "wholeTable:c1~0.5 f0~-0.15 Vthin@0;headerRow:b c1~0.5 Bthin@0;totalRow:b c1~0.5;firstColumn:b c1~0.5 f0~-0.25;secondRowStripe:f0~-0.25;secondColumnStripe:f0~-0.25;firstSubtotalRow:b c1;secondSubtotalRow:b c1;firstRowSubheading:b c1;pageFieldLabels:b c1~0.5 f0~-0.25",
        ["PivotStyleMedium23"] = "wholeTable:c4~-0.25 f4~0.8 Vthin@0;headerRow:b c4~-0.25 Bthin@0;totalRow:b c4~-0.25;firstColumn:b c4~-0.25 f4~0.6;secondRowStripe:f4~0.6;secondColumnStripe:f4~0.6;firstSubtotalRow:b c1;secondSubtotalRow:b c1;firstRowSubheading:b c1;pageFieldLabels:b c4~-0.25 f4~0.6",
        ["PivotStyleMedium24"] = "wholeTable:c5~-0.25 f5~0.8 Vthin@0;headerRow:b c5~-0.25 Bthin@0;totalRow:b c5~-0.25;firstColumn:b c5~-0.25 f5~0.6;secondRowStripe:f5~0.6;secondColumnStripe:f5~0.6;firstSubtotalRow:b c1;secondSubtotalRow:b c1;firstRowSubheading:b c1;pageFieldLabels:b c5~-0.25 f5~0.6",
        ["PivotStyleMedium25"] = "wholeTable:c6~-0.25 f6~0.8 Vthin@0;headerRow:b c6~-0.25 Bthin@0;totalRow:b c6~-0.25;firstColumn:b c6~-0.25 f6~0.6;secondRowStripe:f6~0.6;secondColumnStripe:f6~0.6;firstSubtotalRow:b c1;secondSubtotalRow:b c1;firstRowSubheading:b c1;pageFieldLabels:b c6~-0.25 f6~0.6",
        ["PivotStyleMedium26"] = "wholeTable:c7~-0.25 f7~0.8 Vthin@0;headerRow:b c7~-0.25 Bthin@0;totalRow:b c7~-0.25;firstColumn:b c7~-0.25 f7~0.6;secondRowStripe:f7~0.6;secondColumnStripe:f7~0.6;firstSubtotalRow:b c1;secondSubtotalRow:b c1;firstRowSubheading:b c1;pageFieldLabels:b c7~-0.25 f7~0.6",
        ["PivotStyleMedium27"] = "wholeTable:c8~-0.25 f8~0.8 Vthin@0;headerRow:b c8~-0.25 Bthin@0;totalRow:b c8~-0.25;firstColumn:b c8~-0.25 f8~0.6;secondRowStripe:f8~0.6;secondColumnStripe:f8~0.6;firstSubtotalRow:b c1;secondSubtotalRow:b c1;firstRowSubheading:b c1;pageFieldLabels:b c8~-0.25 f8~0.6",
        ["PivotStyleMedium28"] = "wholeTable:c9~-0.25 f9~0.8 Vthin@0;headerRow:b c9~-0.25 Bthin@0;totalRow:b c9~-0.25;firstColumn:b c9~-0.25 f9~0.6;secondRowStripe:f9~0.6;secondColumnStripe:f9~0.6;firstSubtotalRow:b c1;secondSubtotalRow:b c1;firstRowSubheading:b c1;pageFieldLabels:b c9~-0.25 f9~0.6",
        ["PivotStyleDark1"] = "wholeTable:c1 f0~-0.25 Hthin@0~-0.15;headerRow:b c0 f1~0.5 Bthin@0 Hthin@1~0.5;totalRow:b c0 f1~0.5;secondRowStripe:f0~-0.35;secondColumnStripe:Lthin@0~-0.15 Rthin@0~-0.15;firstSubtotalRow:b c1 Bthin@0~-0.35;firstRowSubheading:b c1 f0~-0.15 Tthin@0~-0.35 Bthin@0~-0.35;secondRowSubheading:b c1;pageFieldLabels:b c0 f1~0.5 Hthin@1~0.5;pageFieldValues:c0 f1~0.5 Hthin@1~0.5",
        ["PivotStyleDark2"] = "wholeTable:c1 f4~0.6 Hthin@4~0.8;headerRow:b c0 f4~-0.5 Bthin@4 Hthin@4~-0.5;totalRow:b c0 f4~-0.5;secondRowStripe:f4~0.4;secondColumnStripe:Lthin@4~0.8 Rthin@4~0.8;firstSubtotalRow:b c1 Bthin@4~0.8;firstRowSubheading:b c1 f4~0.8 Tthin@4~0.4 Bthin@4~0.4;secondRowSubheading:b c1;pageFieldLabels:b c0 f4~-0.5 Hthin@4~-0.5;pageFieldValues:c0 f4~-0.5 Hthin@4~-0.5",
        ["PivotStyleDark3"] = "wholeTable:c1 f5~0.6 Hthin@5~0.8;headerRow:b c0 f5~-0.5 Bthin@5 Hthin@5~-0.5;totalRow:b c0 f5~-0.5;secondRowStripe:f5~0.4;secondColumnStripe:Lthin@5~0.8 Rthin@5~0.8;firstSubtotalRow:b c1 Bthin@5~0.8;firstRowSubheading:b c1 f5~0.8 Tthin@5~0.4 Bthin@5~0.4;secondRowSubheading:b c1;pageFieldLabels:b c0 f5~-0.5 Hthin@5~-0.5;pageFieldValues:c0 f5~-0.5 Hthin@5~-0.5",
        ["PivotStyleDark4"] = "wholeTable:c1 f6~0.6 Hthin@6~0.8;headerRow:b c0 f6~-0.5 Bthin@6 Hthin@6~-0.5;totalRow:b c0 f6~-0.5;secondRowStripe:f6~0.4;secondColumnStripe:Lthin@6~0.8 Rthin@6~0.8;firstSubtotalRow:b c1 Bthin@6~0.8;firstRowSubheading:b c1 f6~0.8 Tthin@6~0.4 Bthin@6~0.4;secondRowSubheading:b c1;pageFieldLabels:b c0 f6~-0.5 Hthin@6~-0.5;pageFieldValues:c0 f6~-0.5 Hthin@6~-0.5",
        ["PivotStyleDark5"] = "wholeTable:c1 f7~0.6 Hthin@7~0.8;headerRow:b c0 f7~-0.5 Bthin@7 Hthin@7~-0.5;totalRow:b c0 f7~-0.5;secondRowStripe:f7~0.4;secondColumnStripe:Lthin@7~0.8 Rthin@7~0.8;firstSubtotalRow:b c1 Bthin@7~0.8;firstRowSubheading:b c1 f7~0.8 Tthin@7~0.4 Bthin@7~0.4;secondRowSubheading:b c1;pageFieldLabels:b c0 f7~-0.5 Hthin@7~-0.5;pageFieldValues:c0 f7~-0.5 Hthin@7~-0.5",
        ["PivotStyleDark6"] = "wholeTable:c1 f8~0.6 Hthin@8~0.8;headerRow:b c0 f8~-0.5 Bthin@8 Hthin@8~-0.5;totalRow:b c0 f8~-0.5;secondRowStripe:f8~0.4;secondColumnStripe:Lthin@8~0.8 Rthin@8~0.8;firstSubtotalRow:b c1 Bthin@8~0.8;firstRowSubheading:b c1 f8~0.8 Tthin@8~0.4 Bthin@8~0.4;secondRowSubheading:b c1;pageFieldLabels:b c0 f8~-0.5 Hthin@8~-0.5;pageFieldValues:c0 f8~-0.5 Hthin@8~-0.5",
        ["PivotStyleDark7"] = "wholeTable:c1 f9~0.6 Hthin@9~0.8;headerRow:b c0 f9~-0.5 Bthin@9 Hthin@9~-0.5;totalRow:b c0 f9~-0.5;secondRowStripe:f9~0.4;secondColumnStripe:Lthin@9~0.8 Rthin@9~0.8;firstSubtotalRow:b c1 Bthin@9~0.8;firstRowSubheading:b c1 f9~0.8 Tthin@9~0.4 Bthin@9~0.4;secondRowSubheading:b c1;pageFieldLabels:b c0 f9~-0.5 Hthin@9~-0.5;pageFieldValues:c0 f9~-0.5 Hthin@9~-0.5",
        ["PivotStyleDark8"] = "wholeTable:c1 f0~-0.15 Lmedium@1~0.5 Rmedium@1~0.5 Tmedium@1~0.5 Bmedium@1~0.5;headerRow:b c0 f1~0.25;totalRow:b c0 f1~0.25;secondRowStripe:Tthin@0~-0.25 Bthin@0~-0.25;firstColumnStripe:Lthin@0~-0.45 Rthin@0~-0.45;secondColumnStripe:Lthin@0~-0.45 Rthin@0~-0.45;firstSubtotalRow:b c1 f0~-0.25;secondColumnSubheading:Tmedium@0~-0.25;thirdColumnSubheading:Tmedium@0~-0.25;firstRowSubheading:b c1 f0~-0.25 Bmedium@0~-0.15;secondRowSubheading:b c1;pageFieldLabels:Lmedium@1~0.5 Tmedium@1~0.5 Bmedium@1~0.5;pageFieldValues:Rmedium@1~0.5 Tmedium@1~0.5 Bmedium@1~0.5",
        ["PivotStyleDark9"] = "wholeTable:c1 f4~0.8 Lmedium@1~0.5 Rmedium@1~0.5 Tmedium@1~0.5 Bmedium@1~0.5;headerRow:b c0 f1~0.25;totalRow:b c0 f1~0.25;secondRowStripe:Tthin@4~0.6 Bthin@4~0.6;firstColumnStripe:Lthin@4~0.4 Rthin@4~0.4;secondColumnStripe:Lthin@4~0.4 Rthin@4~0.4;firstSubtotalRow:b c1 f4~0.6;secondColumnSubheading:Tmedium@4~0.8;thirdColumnSubheading:Tmedium@4~0.8;firstRowSubheading:b c1 f4~0.6 Bmedium@4~0.8;secondRowSubheading:b c1;pageFieldLabels:Lmedium@1~0.5 Tmedium@1~0.5 Bmedium@1~0.5;pageFieldValues:Rmedium@1~0.5 Tmedium@1~0.5 Bmedium@1~0.5",
        ["PivotStyleDark10"] = "wholeTable:c1 f5~0.8 Lmedium@1~0.5 Rmedium@1~0.5 Tmedium@1~0.5 Bmedium@1~0.5;headerRow:b c0 f1~0.25;totalRow:b c0 f1~0.25;secondRowStripe:Tthin@5~0.6 Bthin@5~0.6;firstColumnStripe:Lthin@5~0.4 Rthin@5~0.4;secondColumnStripe:Lthin@5~0.4 Rthin@5~0.4;firstSubtotalRow:b c1 f5~0.6;secondColumnSubheading:Tmedium@5~0.8;thirdColumnSubheading:Tmedium@5~0.8;firstRowSubheading:b c1 f5~0.6 Bmedium@5~0.8;secondRowSubheading:b c1;pageFieldLabels:Lmedium@1~0.5 Tmedium@1~0.5 Bmedium@1~0.5;pageFieldValues:Rmedium@1~0.5 Tmedium@1~0.5 Bmedium@1~0.5",
        ["PivotStyleDark11"] = "wholeTable:c1 f6~0.8 Lmedium@1~0.5 Rmedium@1~0.5 Tmedium@1~0.5 Bmedium@1~0.5;headerRow:b c0 f1~0.25;totalRow:b c0 f1~0.25;secondRowStripe:Tthin@6~0.6 Bthin@6~0.6;firstColumnStripe:Lthin@6~0.4 Rthin@6~0.4;secondColumnStripe:Lthin@6~0.4 Rthin@6~0.4;firstSubtotalRow:b c1 f6~0.6;secondColumnSubheading:Tmedium@6~0.8;thirdColumnSubheading:Tmedium@6~0.8;firstRowSubheading:b c1 f6~0.6 Bmedium@6~0.8;secondRowSubheading:b c1;pageFieldLabels:Lmedium@1~0.5 Tmedium@1~0.5 Bmedium@1~0.5;pageFieldValues:Rmedium@1~0.5 Tmedium@1~0.5 Bmedium@1~0.5",
        ["PivotStyleDark12"] = "wholeTable:c1 f7~0.8 Lmedium@1~0.5 Rmedium@1~0.5 Tmedium@1~0.5 Bmedium@1~0.5;headerRow:b c0 f1~0.25;totalRow:b c0 f1~0.25;secondRowStripe:Tthin@7~0.6 Bthin@7~0.6;firstColumnStripe:Lthin@7~0.4 Rthin@7~0.4;secondColumnStripe:Lthin@7~0.4 Rthin@7~0.4;firstSubtotalRow:b c1 f7~0.6;secondColumnSubheading:Tmedium@7~0.8;thirdColumnSubheading:Tmedium@7~0.8;firstRowSubheading:b c1 f7~0.6 Bmedium@7~0.8;secondRowSubheading:b c1;pageFieldLabels:Lmedium@1~0.5 Tmedium@1~0.5 Bmedium@1~0.5;pageFieldValues:Rmedium@1~0.5 Tmedium@1~0.5 Bmedium@1~0.5",
        ["PivotStyleDark13"] = "wholeTable:c1 f8~0.8 Lmedium@1~0.5 Rmedium@1~0.5 Tmedium@1~0.5 Bmedium@1~0.5;headerRow:b c0 f1~0.25;totalRow:b c0 f1~0.25;secondRowStripe:Tthin@8~0.6 Bthin@8~0.6;firstColumnStripe:Lthin@8~0.4 Rthin@8~0.4;secondColumnStripe:Lthin@8~0.4 Rthin@8~0.4;firstSubtotalRow:b c1 f8~0.6;secondColumnSubheading:Tmedium@8~0.8;thirdColumnSubheading:Tmedium@8~0.8;firstRowSubheading:b c1 f8~0.6 Bmedium@8~0.8;secondRowSubheading:b c1;pageFieldLabels:Lmedium@1~0.5 Tmedium@1~0.5 Bmedium@1~0.5;pageFieldValues:Rmedium@1~0.5 Tmedium@1~0.5 Bmedium@1~0.5",
        ["PivotStyleDark14"] = "wholeTable:c1 f9~0.8 Lmedium@1~0.5 Rmedium@1~0.5 Tmedium@1~0.5 Bmedium@1~0.5;headerRow:b c0 f1~0.25;totalRow:b c0 f1~0.25;secondRowStripe:Tthin@9~0.6 Bthin@9~0.6;firstColumnStripe:Lthin@9~0.4 Rthin@9~0.4;secondColumnStripe:Lthin@9~0.4 Rthin@9~0.4;firstSubtotalRow:b c1 f9~0.6;secondColumnSubheading:Tmedium@9~0.8;thirdColumnSubheading:Tmedium@9~0.8;firstRowSubheading:b c1 f9~0.6 Bmedium@9~0.8;secondRowSubheading:b c1;pageFieldLabels:Lmedium@1~0.5 Tmedium@1~0.5 Bmedium@1~0.5;pageFieldValues:Rmedium@1~0.5 Tmedium@1~0.5 Bmedium@1~0.5",
        ["PivotStyleDark15"] = "wholeTable:c0~-0.15 f0~-0.45;headerRow:b c0 f1;totalRow:b c0 f1;firstRowStripe:Tthin@0~-0.35 Bthin@0~-0.35;firstColumnStripe:Lthin@0~-0.25 Rthin@0~-0.25;firstSubtotalColumn:f1~0.5 Lthin@0~-0.35 Rthin@0~-0.35 Tthin@0~-0.35 Bthin@0~-0.35;firstSubtotalRow:b c0;firstColumnSubheading:Bthin@0~-0.35;firstRowSubheading:b c0 f1~0.5 Bthin@4~0.8;secondRowSubheading:b c0;pageFieldLabels:b c0",
        ["PivotStyleDark16"] = "wholeTable:c4~0.8 f4;headerRow:b c0 f1;totalRow:b c0 f1;firstRowStripe:Tthin@4~0.4 Bthin@4~0.4;firstColumnStripe:Lthin@4~0.4 Rthin@4~0.4;firstSubtotalColumn:f4~-0.25 Lthin@4~0.4 Rthin@4~0.4 Tthin@4~0.4 Bthin@4~0.4;firstSubtotalRow:b c0;firstColumnSubheading:Bthin@4~0.4;firstRowSubheading:b c0 f4~-0.25;secondRowSubheading:b c0;pageFieldLabels:b c0",
        ["PivotStyleDark17"] = "wholeTable:c5~0.8 f5;headerRow:b c0 f1;totalRow:b c0 f1;firstRowStripe:Tthin@5~0.4 Bthin@5~0.4;firstColumnStripe:Lthin@5~0.4 Rthin@5~0.4;firstSubtotalColumn:f5~-0.25 Lthin@5~0.4 Rthin@5~0.4 Tthin@5~0.4 Bthin@5~0.4;firstSubtotalRow:b c0;firstColumnSubheading:Bthin@5~0.4;firstRowSubheading:b c0 f5~-0.25;secondRowSubheading:b c0;pageFieldLabels:b c0",
        ["PivotStyleDark18"] = "wholeTable:c6~0.8 f6;headerRow:b c0 f1;totalRow:b c0 f1;firstRowStripe:Tthin@6~0.4 Bthin@6~0.4;firstColumnStripe:Lthin@6~0.4 Rthin@6~0.4;firstSubtotalColumn:f6~-0.25 Lthin@6~0.4 Rthin@6~0.4 Tthin@6~0.4 Bthin@6~0.4;firstSubtotalRow:b c0;firstColumnSubheading:Bthin@6~0.4;firstRowSubheading:b c0 f6~-0.25;secondRowSubheading:b c0;pageFieldLabels:b c0",
        ["PivotStyleDark19"] = "wholeTable:c7~0.8 f7;headerRow:b c0 f1;totalRow:b c0 f1;firstRowStripe:Tthin@7~0.4 Bthin@7~0.4;firstColumnStripe:Lthin@7~0.4 Rthin@7~0.4;firstSubtotalColumn:f7~-0.25 Lthin@7~0.4 Rthin@7~0.4 Tthin@7~0.4 Bthin@7~0.4;firstSubtotalRow:b c0;firstColumnSubheading:Bthin@7~0.4;firstRowSubheading:b c0 f7~-0.25;secondRowSubheading:b c0;pageFieldLabels:b c0",
        ["PivotStyleDark20"] = "wholeTable:c8~0.8 f8;headerRow:b c0 f1;totalRow:b c0 f1;firstRowStripe:Tthin@8~0.4 Bthin@8~0.4;firstColumnStripe:Lthin@8~0.4 Rthin@8~0.4;firstSubtotalColumn:f8~-0.25 Lthin@8~0.4 Rthin@8~0.4 Tthin@8~0.4 Bthin@8~0.4;firstSubtotalRow:b c0;firstColumnSubheading:Bthin@8~0.4;firstRowSubheading:b c0 f8~-0.25;secondRowSubheading:b c0;pageFieldLabels:b c0",
        ["PivotStyleDark21"] = "wholeTable:c9~0.8 f9;headerRow:b c0 f1;totalRow:b c0 f1;firstRowStripe:Tthin@9~0.4 Bthin@9~0.4;firstColumnStripe:Lthin@9~0.4 Rthin@9~0.4;firstSubtotalColumn:f9~-0.25 Lthin@9~0.4 Rthin@9~0.4 Tthin@9~0.4 Bthin@9~0.4;firstSubtotalRow:b c0;firstColumnSubheading:Bthin@9~0.4;firstRowSubheading:b c0 f9~-0.25;secondRowSubheading:b c0;pageFieldLabels:b c0",
        ["PivotStyleDark22"] = "wholeTable:c0~-0.15 f0~-0.5 Vthin@0;headerRow:f1~0.5 Bmedium@0;totalRow:b c0 Tmedium@0;firstColumn:f1~0.5;secondRowStripe:f0~-0.45;secondColumnStripe:f0~-0.45;firstHeaderCell:b c0;firstSubtotalRow:b c0~-0.15;secondSubtotalRow:b c1;firstRowSubheading:b c0;secondRowSubheading:b c0;pageFieldLabels:f1~0.5",
        ["PivotStyleDark23"] = "wholeTable:c4~0.8 f4 Vthin@0;headerRow:f4~-0.25 Bmedium@0;totalRow:b c0 Tmedium@0;firstColumn:f4~-0.25;secondRowStripe:f4~0.4;secondColumnStripe:f4~0.4;firstHeaderCell:b c0;firstSubtotalRow:c4~0.8;secondSubtotalRow:b c0;firstRowSubheading:b c0;secondRowSubheading:b c0;pageFieldLabels:f4~-0.25",
        ["PivotStyleDark24"] = "wholeTable:c5~0.8 f5 Vthin@0;headerRow:f5~-0.25 Bmedium@0;totalRow:b c0 Tmedium@0;firstColumn:f5~-0.25;secondRowStripe:f5~0.4;secondColumnStripe:f5~0.4;firstHeaderCell:b c0;firstSubtotalRow:c5~0.8;secondSubtotalRow:b c0;firstRowSubheading:b c0;secondRowSubheading:b c0;pageFieldLabels:f5~-0.25",
        ["PivotStyleDark25"] = "wholeTable:c6~0.8 f6 Vthin@0;headerRow:f6~-0.25 Bmedium@0;totalRow:b c0 Tmedium@0;firstColumn:f6~-0.25;secondRowStripe:f6~0.4;secondColumnStripe:f6~0.4;firstHeaderCell:b c0;firstSubtotalRow:c6~0.8;secondSubtotalRow:b c0;firstRowSubheading:b c0;secondRowSubheading:b c0;pageFieldLabels:f6~-0.25",
        ["PivotStyleDark26"] = "wholeTable:c7~0.8 f7 Vthin@0;headerRow:f7~-0.25 Bmedium@0;totalRow:b c0 Tmedium@0;firstColumn:f7~-0.25;secondRowStripe:f7~0.4;secondColumnStripe:f7~0.4;firstHeaderCell:b c0;firstSubtotalRow:c7~0.8;secondSubtotalRow:b c0;firstRowSubheading:b c0;secondRowSubheading:b c0;pageFieldLabels:f7~-0.25",
        ["PivotStyleDark27"] = "wholeTable:c8~0.8 f8 Vthin@0;headerRow:f8~-0.25 Bmedium@0;totalRow:b c0 Tmedium@0;firstColumn:f8~-0.25;secondRowStripe:f8~0.4;secondColumnStripe:f8~0.4;firstHeaderCell:b c0;firstSubtotalRow:c8~0.8;secondSubtotalRow:b c0;firstRowSubheading:b c0;secondRowSubheading:b c0;pageFieldLabels:f8~-0.25",
        ["PivotStyleDark28"] = "wholeTable:c9~0.8 f9 Vthin@0;headerRow:f9~-0.25 Bmedium@0;totalRow:b c0 Tmedium@0;firstColumn:f9~-0.25;secondRowStripe:f9~0.4;secondColumnStripe:f9~0.4;firstHeaderCell:b c0;firstSubtotalRow:c9~0.8;secondSubtotalRow:b c0;firstRowSubheading:b c0;secondRowSubheading:b c0;pageFieldLabels:f9~-0.25",
    };
}
