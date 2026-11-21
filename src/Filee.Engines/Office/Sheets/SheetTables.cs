// Tables (Excel's list objects: Insert → Table) and the look their table style gives their cells, for the printer:
// the header row, the total row, banded rows and columns, the first and last column, and the borders of each part,
// from a built-in table style (TableStyleLight1–21, TableStyleMedium1–28, TableStyleDark1–11) or a custom one of the
// workbook's styles (tableStyles / tableStyleElement in XLSX, BrtBeginTableStyle / BrtTableStyleElement in XLSB).
//
// The parts are applied in ECMA-376's order (Part 1, 18.8.41): whole table, column stripes, row stripes, last column,
// first column, header row, total row, first and last header cell, first and last total cell; a later part's
// property wins. A part's left, right, top and bottom borders are those of its outline, its vertical and horizontal
// ones those between its cells. Stripes band the data rows (or columns) only. The table's look lies under the cells'
// own: a fill, a border side, a font colour or a bold / italic / underline / strike that a cell sets itself (differs
// from the Normal style) stays, as Excel shows a table whose cells were formatted by hand.
//
// A table can also set formats of its own, over its style and under the cells' own (ECMA-376 Part 1 18.5.1.2 table
// headerRowDxfId / dataDxfId / totalsRowDxfId on each cell of the header row, data rows and total row, and
// headerRowBorderDxfId / tableBorderDxfId / totalsRowBorderDxfId as their outlines; 18.5.1.3 tableColumn's per
// column; [MS-XLSB] BrtBeginList nDxfHeader … nDxfAggBorder and BrtBeginListCol nDxfHdr / nDxfInsertRow / nDxfAgg).
//
// The built-in styles are those of ECMA-376 Part 1's presetTableStyles.xml (OfficeOpenXML-SpreadsheetMLStyles.zip),
// kept here in a short form: per part "type[/band size]:" and then b (bold), i (italic), c<theme>[~tint] (font colour),
// f<theme>[~tint] (fill) and L|R|T|B|V|H<line style>@<theme>[~tint] (left, right, top, bottom, vertical, horizontal
// border). That file's dxfId values count from 1 for its table styles (from 0 for its pivot styles: SheetPivots).
//
// The cells a table colours are put on the sheet: blank ones too (when the style fills or borders them) while the
// workbook's TableBudget lasts, then only the cells the sheet has, so a table over whole columns costs what its cells
// cost.

using System.Globalization;

namespace Filee.Engines.Office.Sheets;

/// <summary>A table on a sheet: its range, header and total rows, style and the parts its style options turn on.</summary>
internal sealed record SheetTable(CellRange Range, string? StyleName)
{
    public int HeaderRows { get; init; } = 1;
    public int TotalRows { get; init; }
    public bool FirstColumn { get; init; }
    public bool LastColumn { get; init; }
    public bool RowStripes { get; init; } = true;
    public bool ColumnStripes { get; init; }

    /// <summary>The table's own formats, applied in order after its style's parts.</summary>
    public IReadOnlyList<TableFormat> Formats { get; init; } = [];
}

/// <summary>The rows of a table a format of its own covers.</summary>
internal enum TableArea
{
    Header,
    Data,
    Totals,
}

/// <summary>
/// A format a table sets itself: on each cell of its area (the cell's own sides), or as the area's outline with its
/// inner lines between the cells (the border formats); for one column of the table (0-based within it) or all.
/// </summary>
internal sealed record TableFormat(TableArea Area, TableStylePart Part, bool Outline = false, int? Column = null);

/// <summary>
/// What the tables of one workbook may still cost: cells looked at one by one (blank ones get the style), and past
/// that the rows walked and the cells the sheet has that are styled.
/// </summary>
internal sealed class TableBudget
{
    public long Cells { get; set; } = 250_000;
    public long Visits { get; set; } = 1_000_000;
}

/// <summary>One part of a table style: what it changes, the borders between its cells, its band size (stripes).</summary>
internal sealed record TableStylePart(DifferentialStyle Style, CellBorder? Vertical = null, CellBorder? Horizontal = null, int Size = 1);

/// <summary>A table style: its parts by ECMA-376 type name (wholeTable, headerRow, firstRowStripe, ...).</summary>
internal sealed record TableStyle(IReadOnlyDictionary<string, TableStylePart> Parts);

internal static class SheetTables
{
    /// <summary>Tables of one workbook applied at most.</summary>
    public const int MaxTables = 10_000;

    /// <summary>The order in which the parts are applied (the later wins).</summary>
    private static readonly string[] Order =
    [
        "wholeTable", "firstColumnStripe", "secondColumnStripe", "firstRowStripe", "secondRowStripe", "lastColumn", "firstColumn",
        "headerRow", "totalRow", "firstHeaderCell", "lastHeaderCell", "firstTotalCell", "lastTotalCell",
    ];

    /// <summary>XLSB's TSEType of the table parts (MS-XLSB 2.5.150), by value.</summary>
    public static string? PartOf(uint tseType) => tseType switch
    {
        0 => "wholeTable",
        1 => "headerRow",
        2 => "totalRow",
        3 => "firstColumn",
        4 => "lastColumn",
        5 => "firstRowStripe",
        6 => "secondRowStripe",
        7 => "firstColumnStripe",
        8 => "secondColumnStripe",
        9 => "firstHeaderCell",
        10 => "lastHeaderCell",
        11 => "firstTotalCell",
        12 => "lastTotalCell",
        _ => null,
    };

    /// <summary>Whether a tableStyleElement type is one a table uses (PivotTable parts aren't).</summary>
    public static bool IsTablePart(string? type) => type is not null && Order.Contains(type);

    /// <summary>The built-in style of a name ("TableStyleMedium2"), its theme colours through <paramref name="color"/>; null for another name.</summary>
    /// <param name="color">A theme colour index (SpreadsheetML's: 0 lt1, 1 dk1, 2 lt2, 3 dk2, 4–9 accents) and tint as "#RRGGBB".</param>
    public static TableStyle? BuiltIn(string? name, Func<int, double, string?> color) =>
        name is not null && BuiltInStyles.TryGetValue(name, out var definition) ? Parse(definition, color) : null;

    /// <summary>A built-in style kept in the short form above, its theme colours through <paramref name="color"/>.</summary>
    internal static TableStyle Parse(string definition, Func<int, double, string?> color)
    {
        var parts = new Dictionary<string, TableStylePart>(StringComparer.Ordinal);
        foreach (var part in definition.Split(';'))
        {
            var colon = part.IndexOf(':');
            var head = part[..colon].Split('/');
            var style = new DifferentialStyle();
            CellBorder? vertical = null, horizontal = null;
            foreach (var property in part[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                switch (property[0])
                {
                    case 'b':
                        style = style with { Bold = true };
                        break;
                    case 'i':
                        style = style with { Italic = true };
                        break;
                    case 'c':
                        style = style with { Color = Color(property[1..], color) };
                        break;
                    case 'f':
                        style = style with { Fill = Color(property[1..], color) };
                        break;
                    default:
                        var at = property.IndexOf('@');
                        var border = new CellBorder(property[1..at], Color(property[(at + 1)..], color));
                        switch (property[0])
                        {
                            case 'L':
                                style = style with { Left = border };
                                break;
                            case 'R':
                                style = style with { Right = border };
                                break;
                            case 'T':
                                style = style with { Top = border };
                                break;
                            case 'B':
                                style = style with { Bottom = border };
                                break;
                            case 'V':
                                vertical = border;
                                break;
                            default:
                                horizontal = border;
                                break;
                        }
                        break;
                }
            }
            parts[head[0]] = new TableStylePart(style, vertical, horizontal, head.Length > 1 ? int.Parse(head[1], CultureInfo.InvariantCulture) : 1);
        }
        return new TableStyle(parts);
    }

    private static string? Color(string value, Func<int, double, string?> color)
    {
        var tilde = value.IndexOf('~');
        var theme = int.Parse(tilde < 0 ? value : value[..tilde], CultureInfo.InvariantCulture);
        return color(theme, tilde < 0 ? 0 : double.Parse(value[(tilde + 1)..], CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Puts the look of each table's style on its cells, under the cells' own formatting: <paramref name="style"/>
    /// finds a style by name (the workbook's own first, then the built-in ones), <paramref name="normal"/> is the
    /// Normal style's look (what a cell that sets nothing has).
    /// </summary>
    /// <param name="budget">What the workbook's tables may still cost; counted down.</param>
    public static void Apply(Worksheet sheet, IEnumerable<SheetTable> tables, Func<string?, TableStyle?> style, CellStyle normal, TableBudget budget)
    {
        foreach (var table in tables)
        {
            if (budget.Visits <= 0)
                return;
            if (style(table.StyleName) is { } found)
                Apply(sheet, table, found, normal, budget);
            else if (table.Formats.Count > 0)
                Apply(sheet, table, NoStyle, normal, budget);
        }
    }

    private static readonly TableStyle NoStyle = new(new Dictionary<string, TableStylePart>());

    /// <summary>Cell styles by name one workbook keeps at most (Excel allows 64,000).</summary>
    public const int MaxCellStyles = 65_536;

    /// <summary>
    /// What a named cell style (a table's headerRowCellStyle / dataCellStyle / totalsRowCellStyle, XLSB stStyle*)
    /// gives the cells of a table's area: its solid fill, font colour and the bold, italic, underline and strike it
    /// turns on, and its visible borders, as far as they differ from the Normal style; null when it gives nothing.
    /// </summary>
    /// <param name="look">The cell style's formatting (only what the style includes: XLSX apply*, XLSB xfGrbitAtr).</param>
    public static TableStylePart? CellStylePart(CellStyle look, CellStyle normal)
    {
        static CellBorder? Visible(CellBorder side) => side.IsVisible ? side : null;
        var style = new DifferentialStyle
        {
            Fill = look.FillPattern is null && look.Gradient is null ? look.Fill : null,
            Color = look.Color is { } color && color != normal.Color ? color : null,
            Bold = look.Bold && !normal.Bold ? true : null,
            Italic = look.Italic && !normal.Italic ? true : null,
            Underline = look.Underline && !normal.Underline ? true : null,
            Strike = look.Strike && !normal.Strike ? true : null,
            Left = Visible(look.Left),
            Right = Visible(look.Right),
            Top = Visible(look.Top),
            Bottom = Visible(look.Bottom),
        };
        return style == new DifferentialStyle() ? null : new TableStylePart(style);
    }

    /// <summary>
    /// Puts the look of <paramref name="style"/> on the cells of <paramref name="table"/>: every cell of its rows while
    /// the budget's cells last (blank ones get the fill and borders), then only the cells the sheet has.
    /// </summary>
    public static void Apply(Worksheet sheet, SheetTable table, TableStyle style, CellStyle normal, TableBudget budget)
    {
        var range = table.Range;
        if (range.Top < 0 || range.Left < 0 || range.Bottom < range.Top || range.Right < range.Left
            || range.Bottom > SheetPagination.MaxRow || range.Right > SheetPagination.MaxColumn)
            return;
        var headers = Math.Clamp(table.HeaderRows, 0, range.Bottom - range.Top + 1);
        var totals = Math.Clamp(table.TotalRows, 0, range.Bottom - range.Top + 1 - headers);
        var dataTop = range.Top + headers;
        var dataBottom = range.Bottom - totals;
        var parts = Order.Where(style.Parts.ContainsKey).Where(type => type switch
        {
            "firstColumnStripe" or "secondColumnStripe" => table.ColumnStripes,
            "firstRowStripe" or "secondRowStripe" => table.RowStripes,
            "firstColumn" => table.FirstColumn,
            "lastColumn" => table.LastColumn,
            "headerRow" or "firstHeaderCell" or "lastHeaderCell" => headers > 0,
            "totalRow" or "firstTotalCell" or "lastTotalCell" => totals > 0,
            _ => true,
        }).Select(type => (Type: type, Part: style.Parts[type])).ToList();
        if (parts.Count == 0 && table.Formats.Count == 0)
            return;
        // The table's own formats: those of all its columns, then each column's (looked up by the cell's column).
        var wide = table.Formats.Where(f => f.Column is null).ToList();
        var byColumn = table.Formats.Where(f => f.Column is not null).ToLookup(f => range.Left + f.Column!.Value);
        var rowBand = Band(style, "firstRowStripe", "secondRowStripe");
        var columnBand = Band(style, "firstColumnStripe", "secondColumnStripe");

        void Style(int row, int column, SheetCell? existing)
        {
            var look = new Look();
            foreach (var (type, part) in parts)
            {
                // The part's region: its first and last row and column.
                (int Top, int Bottom, int Left, int Right)? region = type switch
                {
                    "wholeTable" => (range.Top, range.Bottom, range.Left, range.Right),
                    "headerRow" when row < dataTop => (range.Top, dataTop - 1, range.Left, range.Right),
                    "totalRow" when row > dataBottom => (dataBottom + 1, range.Bottom, range.Left, range.Right),
                    "firstColumn" when column == range.Left => (range.Top, range.Bottom, column, column),
                    "lastColumn" when column == range.Right => (range.Top, range.Bottom, column, column),
                    "firstHeaderCell" when row < dataTop && column == range.Left => (range.Top, dataTop - 1, column, column),
                    "lastHeaderCell" when row < dataTop && column == range.Right => (range.Top, dataTop - 1, column, column),
                    "firstTotalCell" when row > dataBottom && column == range.Left => (dataBottom + 1, range.Bottom, column, column),
                    "lastTotalCell" when row > dataBottom && column == range.Right => (dataBottom + 1, range.Bottom, column, column),
                    "firstRowStripe" or "secondRowStripe" when row >= dataTop && row <= dataBottom
                        && Stripe(row - dataTop, rowBand, type == "firstRowStripe") is { } rows
                        => (dataTop + rows.Start, Math.Min(dataBottom, dataTop + rows.End), range.Left, range.Right),
                    "firstColumnStripe" or "secondColumnStripe" when row >= dataTop && row <= dataBottom
                        && Stripe(column - range.Left, columnBand, type == "firstColumnStripe") is { } columns
                        => (dataTop, dataBottom, range.Left + columns.Start, Math.Min(range.Right, range.Left + columns.End)),
                    _ => null,
                };
                if (region is { } r)
                    look.Add(part, row, column, r);
            }
            foreach (var format in table.Formats.Count == 0 ? Enumerable.Empty<TableFormat>() : wide.Concat(byColumn[column]))
            {
                var (top, bottom) = format.Area switch
                {
                    TableArea.Header => (range.Top, dataTop - 1),
                    TableArea.Data => (dataTop, dataBottom),
                    _ => (dataBottom + 1, range.Bottom),
                };
                var (left, right) = format.Column is { } at ? (range.Left + at, range.Left + at) : (range.Left, range.Right);
                if (row >= top && row <= bottom && column >= left && column <= right)
                    look.Add(format.Part, row, column, format.Outline ? (top, bottom, left, right) : (row, row, column, column));
            }
            if (!look.Any)
                return;
            var own = existing?.Style ?? normal;
            var styled = look.Under(own, normal);
            if (ReferenceEquals(styled, own))
                return;
            if (existing is not null)
                sheet.Set(row, column, existing with { Style = styled });
            else if (styled.HasBorders || styled.Fill is not null) // a blank cell matters only for what shows without text
                sheet.Set(row, column, new SheetCell("", styled));
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
        // Past the budget: the cells the sheet has (the rows are walked once per table), while the visits last.
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

    internal static (int First, int Second) Band(TableStyle style, string first, string second) =>
        (Math.Clamp(style.Parts.GetValueOrDefault(first)?.Size ?? 1, 1, 9), Math.Clamp(style.Parts.GetValueOrDefault(second)?.Size ?? 1, 1, 9));

    /// <summary>The stripe an offset falls in, as the offsets of its first and last row (or column); null in the other stripe.</summary>
    internal static (int Start, int End)? Stripe(int offset, (int First, int Second) band, bool first)
    {
        var cycle = band.First + band.Second;
        var at = offset % cycle;
        var start = offset - at;
        if (at < band.First)
            return first ? (start, start + band.First - 1) : null;
        return first ? null : (start + band.First, start + cycle - 1);
    }

    /// <summary>What the parts that cover one cell give it, the later part winning property by property.</summary>
    internal sealed class Look
    {
        private string? _fill, _color;
        private bool? _bold, _italic, _underline, _strike;
        private CellBorder? _left, _right, _top, _bottom;

        public bool Any { get; private set; }

        public void Add(TableStylePart part, int row, int column, (int Top, int Bottom, int Left, int Right) region)
        {
            var s = part.Style;
            Any = true;
            _fill = s.Fill ?? _fill;
            _color = s.Color ?? _color;
            _bold = s.Bold ?? _bold;
            _italic = s.Italic ?? _italic;
            _underline = s.Underline ?? _underline;
            _strike = s.Strike ?? _strike;
            // The outline's sides on the region's edges, the inner lines between its cells.
            _left = (column == region.Left ? s.Left : part.Vertical) ?? _left;
            _right = (column == region.Right ? s.Right : part.Vertical) ?? _right;
            _top = (row == region.Top ? s.Top : part.Horizontal) ?? _top;
            _bottom = (row == region.Bottom ? s.Bottom : part.Horizontal) ?? _bottom;
        }

        /// <summary>The cell's style with the table's look under it; <paramref name="own"/> itself when nothing changes.</summary>
        public CellStyle Under(CellStyle own, CellStyle normal)
        {
            var styled = own;
            if (_fill is not null && own.Fill is null && own.Gradient is null)
                styled = styled with { Fill = _fill, FillPattern = null, PatternBackground = null };
            if (_color is not null && (own.Color is null || own.Color == normal.Color))
                styled = styled with { Color = _color };
            if (_bold is { } bold && own.Bold == normal.Bold)
                styled = styled with { Bold = bold };
            if (_italic is { } italic && own.Italic == normal.Italic)
                styled = styled with { Italic = italic };
            if (_underline is { } underline && own.Underline == normal.Underline)
                styled = styled with { Underline = underline };
            if (_strike is { } strike && own.Strike == normal.Strike)
                styled = styled with { Strike = strike };
            if (_left is { IsVisible: true } left && !own.Left.IsVisible)
                styled = styled with { Left = left };
            if (_right is { IsVisible: true } right && !own.Right.IsVisible)
                styled = styled with { Right = right };
            if (_top is { IsVisible: true } top && !own.Top.IsVisible)
                styled = styled with { Top = top };
            if (_bottom is { IsVisible: true } bottom && !own.Bottom.IsVisible)
                styled = styled with { Bottom = bottom };
            return styled == own ? own : styled;
        }
    }

    /// <summary>The built-in table styles of ECMA-376 Part 1 (presetTableStyles.xml), in the short form above.</summary>
    private static readonly Dictionary<string, string> BuiltInStyles = new(StringComparer.Ordinal)
    {
        ["TableStyleLight1"] = "wholeTable:c1 Tthin@1 Bthin@1;headerRow:b c1 Bthin@1;totalRow:b c1 Tthin@1;firstColumn:b c1;lastColumn:b c1;firstRowStripe:f0~-0.15;firstColumnStripe:f0~-0.15",
        ["TableStyleLight2"] = "wholeTable:c4~-0.25 Tthin@4 Bthin@4;headerRow:b c4~-0.25 Bthin@4;totalRow:b c4~-0.25 Tthin@4;firstColumn:b c4~-0.25;lastColumn:b c4~-0.25;firstRowStripe:f4~0.8;firstColumnStripe:f4~0.8",
        ["TableStyleLight3"] = "wholeTable:c5~-0.25 Tthin@5 Bthin@5;headerRow:b c5~-0.25 Bthin@5;totalRow:b c5~-0.25 Tthin@5;firstColumn:b c5~-0.25;lastColumn:b c5~-0.25;firstRowStripe:f5~0.8;firstColumnStripe:f5~0.8",
        ["TableStyleLight4"] = "wholeTable:c6~-0.25 Tthin@6 Bthin@6;headerRow:b c6~-0.25 Bthin@6;totalRow:b c6~-0.25 Tthin@6;firstColumn:b c6~-0.25;lastColumn:b c6~-0.25;firstRowStripe:f6~0.8;firstColumnStripe:f6~0.8",
        ["TableStyleLight5"] = "wholeTable:c7~-0.25 Tthin@7 Bthin@7;headerRow:b c7~-0.25 Bthin@7;totalRow:b c7~-0.25 Tthin@7;firstColumn:b c7~-0.25;lastColumn:b c7~-0.25;firstRowStripe:f7~0.8;firstColumnStripe:f7~0.8",
        ["TableStyleLight6"] = "wholeTable:c8~-0.25 Tthin@8 Bthin@8;headerRow:b c8~-0.25 Bthin@8;totalRow:b c8~-0.25 Tthin@8;firstColumn:b c8~-0.25;lastColumn:b c8~-0.25;firstRowStripe:f8~0.8;firstColumnStripe:f8~0.8",
        ["TableStyleLight7"] = "wholeTable:c9~-0.25 Tthin@9 Bthin@9;headerRow:b c9~-0.25 Bthin@9;totalRow:b c9~-0.25 Tthin@9;firstColumn:b c9~-0.25;lastColumn:b c9~-0.25;firstRowStripe:f9~0.8;firstColumnStripe:f9~0.8",
        ["TableStyleLight8"] = "wholeTable:c1 Lthin@1 Rthin@1 Tthin@1 Bthin@1;headerRow:b c0 f1;totalRow:b c1 Tdouble@1;firstColumn:b c1;lastColumn:b c1;firstRowStripe:Tthin@1;secondRowStripe:Tthin@1;firstColumnStripe:Lthin@1;secondColumnStripe:Lthin@1",
        ["TableStyleLight9"] = "wholeTable:c1 Lthin@4 Rthin@4 Tthin@4 Bthin@4;headerRow:b c0 f4;totalRow:b c1 Tdouble@4;firstColumn:b c1;lastColumn:b c1;firstRowStripe:Tthin@4;secondRowStripe:Tthin@4;firstColumnStripe:Lthin@4;secondColumnStripe:Lthin@4",
        ["TableStyleLight10"] = "wholeTable:c1 Lthin@5 Rthin@5 Tthin@5 Bthin@5;headerRow:b c0 f5;totalRow:b c1 Tdouble@5;firstColumn:b c1;lastColumn:b c1;firstRowStripe:Tthin@5;secondRowStripe:Tthin@5;firstColumnStripe:Lthin@5;secondColumnStripe:Lthin@5",
        ["TableStyleLight11"] = "wholeTable:c1 Lthin@6 Rthin@6 Tthin@6 Bthin@6;headerRow:b c0 f6;totalRow:b c1 Tdouble@6;firstColumn:b c1;lastColumn:b c1;firstRowStripe:Tthin@6;secondRowStripe:Tthin@6;firstColumnStripe:Lthin@6;secondColumnStripe:Lthin@6",
        ["TableStyleLight12"] = "wholeTable:c1 Lthin@7 Rthin@7 Tthin@7 Bthin@7;headerRow:b c0 f7;totalRow:b c1 Tdouble@7;firstColumn:b c1;lastColumn:b c1;firstRowStripe:Tthin@7;secondRowStripe:Tthin@7;firstColumnStripe:Lthin@7;secondColumnStripe:Lthin@7",
        ["TableStyleLight13"] = "wholeTable:c1 Lthin@8 Rthin@8 Tthin@8 Bthin@8;headerRow:b c0 f8;totalRow:b c1 Tdouble@8;firstColumn:b c1;lastColumn:b c1;firstRowStripe:Tthin@8;secondRowStripe:Tthin@8;firstColumnStripe:Lthin@8;secondColumnStripe:Lthin@8",
        ["TableStyleLight14"] = "wholeTable:c1 Lthin@9 Rthin@9 Tthin@9 Bthin@9;headerRow:b c0 f9;totalRow:b c1 Tdouble@9;firstColumn:b c1;lastColumn:b c1;firstRowStripe:Tthin@9;secondRowStripe:Tthin@9;firstColumnStripe:Lthin@9;secondColumnStripe:Lthin@9",
        ["TableStyleLight15"] = "wholeTable:c1 Lthin@1 Rthin@1 Tthin@1 Bthin@1 Vthin@1 Hthin@1;headerRow:b c1 Bmedium@1;totalRow:b c1 Tdouble@1;firstColumn:b c1;lastColumn:b c1;firstRowStripe:f0~-0.15;firstColumnStripe:f0~-0.15",
        ["TableStyleLight16"] = "wholeTable:c1 Lthin@4 Rthin@4 Tthin@4 Bthin@4 Vthin@4 Hthin@4;headerRow:b c1 Bmedium@4;totalRow:b c1 Tdouble@4;firstColumn:b c1;lastColumn:b c1;firstRowStripe:f4~0.8;firstColumnStripe:f4~0.8",
        ["TableStyleLight17"] = "wholeTable:c1 Lthin@5 Rthin@5 Tthin@5 Bthin@5 Vthin@5 Hthin@5;headerRow:b c1 Bmedium@5;totalRow:b c1 Tdouble@5;firstColumn:b c1;lastColumn:b c1;firstRowStripe:f5~0.8;firstColumnStripe:f5~0.8",
        ["TableStyleLight18"] = "wholeTable:c1 Lthin@6 Rthin@6 Tthin@6 Bthin@6 Vthin@6 Hthin@6;headerRow:b c1 Bmedium@6;totalRow:b c1 Tdouble@6;firstColumn:b c1;lastColumn:b c1;firstRowStripe:f6~0.8;firstColumnStripe:f6~0.8",
        ["TableStyleLight19"] = "wholeTable:c1 Lthin@7 Rthin@7 Tthin@7 Bthin@7 Vthin@7 Hthin@7;headerRow:b c1 Bmedium@7;totalRow:b c1 Tdouble@7;firstColumn:b c1;lastColumn:b c1;firstRowStripe:f7~0.8;firstColumnStripe:f7~0.8",
        ["TableStyleLight20"] = "wholeTable:c1 Lthin@8 Rthin@8 Tthin@8 Bthin@8 Vthin@8 Hthin@8;headerRow:b c1 Bmedium@8;totalRow:b c1 Tdouble@8;firstColumn:b c1;lastColumn:b c1;firstRowStripe:f8~0.8;firstColumnStripe:f8~0.8",
        ["TableStyleLight21"] = "wholeTable:c1 Lthin@9 Rthin@9 Tthin@9 Bthin@9 Vthin@9 Hthin@9;headerRow:b c1 Bmedium@9;totalRow:b c1 Tdouble@9;firstColumn:b c1;lastColumn:b c1;firstRowStripe:f9~0.8;firstColumnStripe:f9~0.8",
        ["TableStyleMedium1"] = "wholeTable:c1 Lthin@1 Rthin@1 Tthin@1 Bthin@1 Hthin@1;headerRow:b c0 f1;totalRow:b c1 Tdouble@1;firstColumn:b c1;lastColumn:b c1;firstRowStripe:f0~-0.15;firstColumnStripe:f0~-0.15",
        ["TableStyleMedium2"] = "wholeTable:c1 Lthin@4~0.4 Rthin@4~0.4 Tthin@4~0.4 Bthin@4~0.4 Hthin@4~0.4;headerRow:b c0 f4;totalRow:b c1 Tdouble@4;firstColumn:b c1;lastColumn:b c1;firstRowStripe:f4~0.8;firstColumnStripe:f4~0.8",
        ["TableStyleMedium3"] = "wholeTable:c1 Lthin@5~0.4 Rthin@5~0.4 Tthin@5~0.4 Bthin@5~0.4 Hthin@5~0.4;headerRow:b c0 f5;totalRow:b c1 Tdouble@5;firstColumn:b c1;lastColumn:b c1;firstRowStripe:f5~0.8;firstColumnStripe:f5~0.8",
        ["TableStyleMedium4"] = "wholeTable:c1 Lthin@6~0.4 Rthin@6~0.4 Tthin@6~0.4 Bthin@6~0.4 Hthin@6~0.4;headerRow:b c0 f6;totalRow:b c1 Tdouble@6;firstColumn:b c1;lastColumn:b c1;firstRowStripe:f6~0.8;firstColumnStripe:f6~0.8",
        ["TableStyleMedium5"] = "wholeTable:c1 Lthin@7~0.4 Rthin@7~0.4 Tthin@7~0.4 Bthin@7~0.4 Hthin@7~0.4;headerRow:b c0 f7;totalRow:b c1 Tdouble@7;firstColumn:b c1;lastColumn:b c1;firstRowStripe:f7~0.8;firstColumnStripe:f7~0.8",
        ["TableStyleMedium6"] = "wholeTable:c1 Lthin@8~0.4 Rthin@8~0.4 Tthin@8~0.4 Bthin@8~0.4 Hthin@8~0.4;headerRow:b c0 f8;totalRow:b c1 Tdouble@8;firstColumn:b c1;lastColumn:b c1;firstRowStripe:f8~0.8;firstColumnStripe:f8~0.8",
        ["TableStyleMedium7"] = "wholeTable:c1 Lthin@9~0.4 Rthin@9~0.4 Tthin@9~0.4 Bthin@9~0.4 Hthin@9~0.4;headerRow:b c0 f9;totalRow:b c1 Tdouble@9;firstColumn:b c1;lastColumn:b c1;firstRowStripe:f9~0.8;firstColumnStripe:f9~0.8",
        ["TableStyleMedium8"] = "wholeTable:c1 f0~-0.15 Vthin@0 Hthin@0;headerRow:b c0 f1 Bthick@0;totalRow:b c0 f1 Tthick@0;firstColumn:b c0 f1;lastColumn:b c0 f1;firstRowStripe:f0~-0.35;firstColumnStripe:f0~-0.35",
        ["TableStyleMedium9"] = "wholeTable:c1 f4~0.8 Vthin@0 Hthin@0;headerRow:b c0 f4 Bthick@0;totalRow:b c0 f4 Tthick@0;firstColumn:b c0 f4;lastColumn:b c0 f4;firstRowStripe:f4~0.6;firstColumnStripe:f4~0.6",
        ["TableStyleMedium10"] = "wholeTable:c1 f5~0.8 Vthin@0 Hthin@0;headerRow:b c0 f5 Bthick@0;totalRow:b c0 f5 Tthick@0;firstColumn:b c0 f5;lastColumn:b c0 f5;firstRowStripe:f5~0.6;firstColumnStripe:f5~0.6",
        ["TableStyleMedium11"] = "wholeTable:c1 f6~0.8 Vthin@0 Hthin@0;headerRow:b c0 f6 Bthick@0;totalRow:b c0 f6 Tthick@0;firstColumn:b c0 f6;lastColumn:b c0 f6;firstRowStripe:f6~0.6;firstColumnStripe:f6~0.6",
        ["TableStyleMedium12"] = "wholeTable:c1 f7~0.8 Vthin@0 Hthin@0;headerRow:b c0 f7 Bthick@0;totalRow:b c0 f7 Tthick@0;firstColumn:b c0 f7;lastColumn:b c0 f7;firstRowStripe:f7~0.6;firstColumnStripe:f7~0.6",
        ["TableStyleMedium13"] = "wholeTable:c1 f8~0.8 Vthin@0 Hthin@0;headerRow:b c0 f8 Bthick@0;totalRow:b c0 f8 Tthick@0;firstColumn:b c0 f8;lastColumn:b c0 f8;firstRowStripe:f8~0.6;firstColumnStripe:f8~0.6",
        ["TableStyleMedium14"] = "wholeTable:c1 f9~0.8 Vthin@0 Hthin@0;headerRow:b c0 f9 Bthick@0;totalRow:b c0 f9 Tthick@0;firstColumn:b c0 f9;lastColumn:b c0 f9;firstRowStripe:f9~0.6;firstColumnStripe:f9~0.6",
        ["TableStyleMedium15"] = "wholeTable:c1 Lthin@1 Rthin@1 Tmedium@1 Bmedium@1 Vthin@1 Hthin@1;headerRow:b c0 f1 Bmedium@1;totalRow:Tdouble@1;firstColumn:b c0 f1;lastColumn:b c0 f1;firstRowStripe:f0~-0.15;firstColumnStripe:f0~-0.15",
        ["TableStyleMedium16"] = "wholeTable:c1 Tmedium@1 Bmedium@1;headerRow:b c0 f4 Bmedium@1;totalRow:Tdouble@1;firstColumn:b c0 f4;lastColumn:b c0 f4;firstRowStripe:f0~-0.15;firstColumnStripe:f0~-0.15",
        ["TableStyleMedium17"] = "wholeTable:c1 Tmedium@1 Bmedium@1;headerRow:b c0 f5 Bmedium@1;totalRow:Tdouble@1;firstColumn:b c0 f5;lastColumn:b c0 f5;firstRowStripe:f0~-0.15;firstColumnStripe:f0~-0.15",
        ["TableStyleMedium18"] = "wholeTable:c1 Tmedium@1 Bmedium@1;headerRow:b c0 f6 Bmedium@1;totalRow:Tdouble@1;firstColumn:b c0 f6;lastColumn:b c0 f6;firstRowStripe:f0~-0.15;firstColumnStripe:f0~-0.15",
        ["TableStyleMedium19"] = "wholeTable:c1 Tmedium@1 Bmedium@1;headerRow:b c0 f7 Bmedium@1;totalRow:Tdouble@1;firstColumn:b c0 f7;lastColumn:b c0 f7;firstRowStripe:f0~-0.15;firstColumnStripe:f0~-0.15",
        ["TableStyleMedium20"] = "wholeTable:c1 Tmedium@1 Bmedium@1;headerRow:b c0 f8 Bmedium@1;totalRow:Tdouble@1;firstColumn:b c0 f8;lastColumn:b c0 f8;firstRowStripe:f0~-0.15;firstColumnStripe:f0~-0.15",
        ["TableStyleMedium21"] = "wholeTable:c1 Tmedium@1 Bmedium@1;headerRow:b c0 f9 Bmedium@1;totalRow:Tdouble@1;firstColumn:b c0 f9;lastColumn:b c0 f9;firstRowStripe:f0~-0.15;firstColumnStripe:f0~-0.15",
        ["TableStyleMedium22"] = "wholeTable:c1 f0~-0.15 Lthin@1 Rthin@1 Tthin@1 Bthin@1 Vthin@1 Hthin@1;headerRow:b c1;totalRow:b c1 Tmedium@1;firstColumn:b c1;lastColumn:b c1;firstRowStripe:f0~-0.35;firstColumnStripe:f0~-0.35",
        ["TableStyleMedium23"] = "wholeTable:c1 f4~0.8 Lthin@4~0.4 Rthin@4~0.4 Tthin@4~0.4 Bthin@4~0.4 Vthin@4~0.4 Hthin@4~0.4;headerRow:b c1;totalRow:b c1 Tmedium@4;firstColumn:b c1;lastColumn:b c1;firstRowStripe:f4~0.6;firstColumnStripe:f4~0.6",
        ["TableStyleMedium24"] = "wholeTable:c1 f5~0.8 Lthin@5~0.4 Rthin@5~0.4 Tthin@5~0.4 Bthin@5~0.4 Vthin@5~0.4 Hthin@5~0.4;headerRow:b c1;totalRow:b c1 Tmedium@5;firstColumn:b c1;lastColumn:b c1;firstRowStripe:f5~0.6;firstColumnStripe:f5~0.6",
        ["TableStyleMedium25"] = "wholeTable:c1 f6~0.8 Lthin@6~0.4 Rthin@6~0.4 Tthin@6~0.4 Bthin@6~0.4 Vthin@6~0.4 Hthin@6~0.4;headerRow:b c1;totalRow:b c1 Tmedium@6;firstColumn:b c1;lastColumn:b c1;firstRowStripe:f6~0.6;firstColumnStripe:f6~0.6",
        ["TableStyleMedium26"] = "wholeTable:c1 f7~0.8 Lthin@7~0.4 Rthin@7~0.4 Tthin@7~0.4 Bthin@7~0.4 Vthin@7~0.4 Hthin@7~0.4;headerRow:b c1;totalRow:b c1 Tmedium@7;firstColumn:b c1;lastColumn:b c1;firstRowStripe:f7~0.6;firstColumnStripe:f7~0.6",
        ["TableStyleMedium27"] = "wholeTable:c1 f8~0.8 Lthin@8~0.4 Rthin@8~0.4 Tthin@8~0.4 Bthin@8~0.4 Vthin@8~0.4 Hthin@8~0.4;headerRow:b c1;totalRow:b c1 Tmedium@8;firstColumn:b c1;lastColumn:b c1;firstRowStripe:f8~0.6;firstColumnStripe:f8~0.6",
        ["TableStyleMedium28"] = "wholeTable:c1 f9~0.8 Lthin@9~0.4 Rthin@9~0.4 Tthin@9~0.4 Bthin@9~0.4 Vthin@9~0.4 Hthin@9~0.4;headerRow:b c1;totalRow:b c1 Tmedium@9;firstColumn:b c1;lastColumn:b c1;firstRowStripe:f9~0.6;firstColumnStripe:f9~0.6",
        ["TableStyleDark1"] = "wholeTable:c0 f1~0.45;headerRow:b c0 f1 Bmedium@0;totalRow:b c0 f1~0.15 Tmedium@0;firstColumn:b c0 f1~0.25 Rmedium@0;lastColumn:b c0 f1~0.25 Lmedium@0;firstRowStripe:f1~0.25;firstColumnStripe:f1~0.25",
        ["TableStyleDark2"] = "wholeTable:c0 f4;headerRow:b c0 f1 Bmedium@0;totalRow:b c0 f4~-0.5 Tmedium@0;firstColumn:b c0 f4~-0.25 Rmedium@0;lastColumn:b c0 f4~-0.25 Lmedium@0;firstRowStripe:f4~-0.25;firstColumnStripe:f4~-0.25",
        ["TableStyleDark3"] = "wholeTable:c0 f5;headerRow:b c0 f1 Bmedium@0;totalRow:b c0 f5~-0.5 Tmedium@0;firstColumn:b c0 f5~-0.25 Rmedium@0;lastColumn:b c0 f5~-0.25 Lmedium@0;firstRowStripe:f5~-0.25;firstColumnStripe:f5~-0.25",
        ["TableStyleDark4"] = "wholeTable:c0 f6;headerRow:b c0 f1 Bmedium@0;totalRow:b c0 f6~-0.5 Tmedium@0;firstColumn:b c0 f6~-0.25 Rmedium@0;lastColumn:b c0 f6~-0.25 Lmedium@0;firstRowStripe:f6~-0.25;firstColumnStripe:f6~-0.25",
        ["TableStyleDark5"] = "wholeTable:c0 f7;headerRow:b c0 f1 Bmedium@0;totalRow:b c0 f7~-0.5 Tmedium@0;firstColumn:b c0 f7~-0.25 Rmedium@0;lastColumn:b c0 f7~-0.25 Lmedium@0;firstRowStripe:f7~-0.25;firstColumnStripe:f7~-0.25",
        ["TableStyleDark6"] = "wholeTable:c0 f8;headerRow:b c0 f1 Bmedium@0;totalRow:b c0 f8~-0.5 Tmedium@0;firstColumn:b c0 f8~-0.25 Rmedium@0;lastColumn:b c0 f8~-0.25 Lmedium@0;firstRowStripe:f8~-0.25;firstColumnStripe:f8~-0.25",
        ["TableStyleDark7"] = "wholeTable:c0 f9;headerRow:b c0 f1 Bmedium@0;totalRow:b c0 f9~-0.5 Tmedium@0;firstColumn:b c0 f9~-0.25 Rmedium@0;lastColumn:b c0 f9~-0.25 Lmedium@0;firstRowStripe:f9~-0.25;firstColumnStripe:f9~-0.25",
        ["TableStyleDark8"] = "wholeTable:f0~-0.15;headerRow:c0 f1;totalRow:b c1 Tdouble@1;firstColumn:b c1;lastColumn:b c1;firstRowStripe:f0~-0.35;firstColumnStripe:f0~-0.35",
        ["TableStyleDark9"] = "wholeTable:f4~0.8;headerRow:c0 f5;totalRow:b c1 Tdouble@1;firstColumn:b c1;lastColumn:b c1;firstRowStripe:f4~0.6;firstColumnStripe:f4~0.6",
        ["TableStyleDark10"] = "wholeTable:f6~0.8;headerRow:c0 f7;totalRow:b c1 Tdouble@1;firstColumn:b c1;lastColumn:b c1;firstRowStripe:f6~0.6;firstColumnStripe:f6~0.6",
        ["TableStyleDark11"] = "wholeTable:f8~0.8;headerRow:c0 f9;totalRow:b c1 Tdouble@1;firstColumn:b c1;lastColumn:b c1;firstRowStripe:f8~0.6;firstColumnStripe:f8~0.6",
    };
}
