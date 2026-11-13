// In-memory spreadsheet between the readers (XLSX, XLS, ODS, CSV/TSV) and the writers (XLSX, ODS, CSV/TSV, HWPX
// tables). Cells hold the text as the spreadsheet shows it (numbers and dates already formatted) plus, for numbers,
// the value and its number format, so spreadsheet → spreadsheet conversions keep real numbers.

namespace Filee.Engines.Office.Sheets;

internal sealed class Workbook
{
    public List<Worksheet> Sheets { get; } = [];

    /// <summary>The font of the Normal style: Excel measures column widths in its digits.</summary>
    public string DefaultFontName { get; set; } = "Calibri";

    /// <summary>Size of the Normal style's font in points.</summary>
    public double DefaultFontSize { get; set; } = 11;
}

internal sealed class Worksheet(string name)
{
    /// <summary>Excel's default column width, in characters of the default font.</summary>
    public const double StandardColumnWidth = 8.43;

    public string Name { get; } = name;

    /// <summary>Cells by 0-based row, then 0-based column. Only cells with a value or visible formatting.</summary>
    public SortedDictionary<int, SortedDictionary<int, SheetCell>> Rows { get; } = [];

    /// <summary>Column widths in characters, for columns that set one.</summary>
    public Dictionary<int, double> ColumnWidths { get; } = [];

    public double DefaultColumnWidth { get; set; } = StandardColumnWidth;

    /// <summary>Row heights in points, for rows with a custom height.</summary>
    public Dictionary<int, double> RowHeights { get; } = [];

    public HashSet<int> HiddenRows { get; } = [];
    public HashSet<int> HiddenColumns { get; } = [];
    public List<CellRange> Merges { get; } = [];

    /// <summary>Rows frozen at the top (usually headers): repeated on every printed page.</summary>
    public int FrozenRows { get; set; }

    /// <summary>
    /// 0-based rows to repeat at the top of every printed page (Page Layout → Print Titles), or null. They win over
    /// <see cref="FrozenRows"/> and need not start at the top.
    /// </summary>
    public (int First, int Last)? TitleRows { get; set; }

    /// <summary>0-based rows that start a new printed page (page breaks set in Excel: Page Layout → Breaks).</summary>
    public SortedSet<int> RowBreaks { get; } = [];

    // ───────────── Printing (SheetPrinter); the writers to other spreadsheet formats leave these out ─────────────

    /// <summary>Paper, margins, scaling, print options, headers and footers (Page Layout → Page Setup).</summary>
    public SheetPageSetup PageSetup { get; set; } = new();

    /// <summary>The print area (Page Layout → Print Area), one or more ranges printed one after the other; empty = the used range.</summary>
    public List<CellRange> PrintArea { get; } = [];

    /// <summary>0-based columns to repeat at the left of every printed page (Print Titles), or null.</summary>
    public (int First, int Last)? TitleColumns { get; set; }

    /// <summary>0-based columns that start a new printed page.</summary>
    public SortedSet<int> ColumnBreaks { get; } = [];

    /// <summary>
    /// Heights in points the file stores for rows sized to their content (Excel's and Calc's autofit), which
    /// <see cref="RowHeights"/> leaves out; rows in neither are measured.
    /// </summary>
    public Dictionary<int, double> AutoRowHeights { get; } = [];

    /// <summary>Height of rows the file says nothing about, in points; null = from the default font.</summary>
    public double? DefaultRowHeight { get; set; }

    /// <summary>True when <see cref="DefaultColumnWidth"/> came from the file (XLSX defaultColWidth, XLS STANDARDWIDTH).</summary>
    public bool HasDefaultColumnWidth { get; set; }

    /// <summary>Excel's base column width in digits (sheetFormatPr baseColWidth, XLS DEFCOLWIDTH); null = 8.</summary>
    public int? BaseColumnWidth { get; set; }

    /// <summary>
    /// Column widths are points / <see cref="OdsReader.PointsPerCharacter"/> (OpenDocument) rather than Excel's
    /// characters of the default font with padding.
    /// </summary>
    public bool WidthsInPoints { get; set; }

    /// <summary>Columns run from right to left (Arabic and Hebrew sheets: Page Layout → Sheet Right-to-Left).</summary>
    public bool RightToLeft { get; set; }

    /// <summary>Conditional formats that change how cells look (fills, font colours, borders), in priority order.</summary>
    public List<ConditionalFormat> ConditionalFormats { get; } = [];

    /// <summary>Pictures and shapes placed on the sheet, in drawing order (the last one on top).</summary>
    public List<SheetDrawing> Drawings { get; } = [];

    public void Set(int row, int column, SheetCell cell)
    {
        if (!Rows.TryGetValue(row, out var cells))
            Rows[row] = cells = [];
        cells[column] = cell;
    }

    public SheetCell? Get(int row, int column) =>
        Rows.TryGetValue(row, out var cells) && cells.TryGetValue(column, out var cell) ? cell : null;

    public double ColumnWidth(int column) => ColumnWidths.TryGetValue(column, out var width) ? width : DefaultColumnWidth;

    /// <summary>
    /// The smallest range holding every cell with text, fill or borders (merged areas included); null for an empty
    /// sheet.
    /// </summary>
    public CellRange? UsedRange()
    {
        int top = int.MaxValue, left = int.MaxValue, bottom = -1, right = -1;
        foreach (var (row, cells) in Rows)
        {
            foreach (var (column, cell) in cells)
            {
                if (!cell.HasContent)
                    continue;
                top = Math.Min(top, row);
                bottom = Math.Max(bottom, row);
                left = Math.Min(left, column);
                right = Math.Max(right, column);
            }
        }
        if (bottom < 0)
            return null;
        foreach (var merge in Merges.Where(m => m.Top <= bottom && m.Left <= right && m.Top >= top && m.Left >= left))
        {
            bottom = Math.Max(bottom, merge.Bottom);
            right = Math.Max(right, merge.Right);
        }
        return new CellRange(top, left, bottom, right);
    }
}

/// <summary>An inclusive, 0-based range of cells.</summary>
internal readonly record struct CellRange(int Top, int Left, int Bottom, int Right)
{
    public bool Contains(int row, int column) => row >= Top && row <= Bottom && column >= Left && column <= Right;

    /// <summary>Parses "B2" or "A1:C3".</summary>
    public static CellRange? Parse(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;
        var parts = reference.Replace("$", "", StringComparison.Ordinal).Split(':');
        if (!TryParseCell(parts[0], out var r1, out var c1))
            return null;
        if (parts.Length == 1)
            return new CellRange(r1, c1, r1, c1);
        return TryParseCell(parts[1], out var r2, out var c2)
            ? new CellRange(Math.Min(r1, r2), Math.Min(c1, c2), Math.Max(r1, r2), Math.Max(c1, c2))
            : null;
    }

    /// <summary>"AB12" → row 11, column 27.</summary>
    public static bool TryParseCell(string reference, out int row, out int column)
    {
        row = column = -1;
        var i = 0;
        var col = 0;
        while (i < reference.Length && char.IsAsciiLetter(reference[i]))
            col = col * 26 + (char.ToUpperInvariant(reference[i++]) - 'A' + 1);
        if (i == 0 || i == reference.Length || !int.TryParse(reference.AsSpan(i), out var r) || r < 1)
            return false;
        row = r - 1;
        column = col - 1;
        return true;
    }
}

internal enum CellAlign
{
    General,
    Left,
    Center,
    Right,
}

internal enum CellVerticalAlign
{
    Bottom,
    Center,
    Top,
}

/// <summary>One border side: Excel style name ("thin", "medium", "dashed", ...) and colour.</summary>
internal readonly record struct CellBorder(string Style, string? Color)
{
    public static readonly CellBorder None = new("none", null);
    public bool IsVisible => Style is not ("none" or "");
}

internal sealed record CellStyle
{
    public static readonly CellStyle Default = new();

    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public bool Strike { get; init; }

    /// <summary>Font size in points; null = the workbook default.</summary>
    public double? FontSize { get; init; }

    public string? Color { get; init; }
    public string? Fill { get; init; }
    public CellAlign Align { get; init; }
    public CellVerticalAlign VerticalAlign { get; init; }
    public bool Wrap { get; init; }

    /// <summary>Font family; null = the workbook default.</summary>
    public string? FontName { get; init; }

    /// <summary>Superscript (1) or subscript (-1).</summary>
    public int Script { get; init; }

    /// <summary>Double underline (Excel's "double" and "doubleAccounting").</summary>
    public bool DoubleUnderline { get; init; }

    /// <summary>
    /// Horizontal alignments beyond <see cref="Align"/> (which holds the nearest of left, centre and right, for the
    /// writers): repeated, justified, distributed, centred across the empty cells to the right.
    /// </summary>
    public CellSpecialAlign SpecialAlign { get; init; }

    /// <summary>Vertical justify / distributed (spread the lines over the cell); <see cref="VerticalAlign"/> is Top.</summary>
    public bool VerticalJustify { get; init; }

    /// <summary>Degrees counter-clockwise, -90 to 90; <see cref="Stacked"/> for letters one under another.</summary>
    public int Rotation { get; init; }

    /// <summary>Excel's textRotation 255: the letters stacked one under another.</summary>
    public const int Stacked = 255;

    /// <summary>Indent level (each about three spaces of the default font).</summary>
    public int Indent { get; init; }

    /// <summary>Shrink to fit: the font is made smaller until the text fits the cell's width.</summary>
    public bool Shrink { get; init; }

    /// <summary>Fill pattern other than solid ("darkGray", "lightHorizontal", ...), drawn over <see cref="PatternBackground"/>.</summary>
    public string? FillPattern { get; init; }

    /// <summary>The background colour of a pattern fill (the pattern itself is <see cref="Fill"/>).</summary>
    public string? PatternBackground { get; init; }

    /// <summary>A linear gradient fill (angle in degrees, colour stops from 0 to 1); <see cref="Fill"/> holds its first colour.</summary>
    public CellGradient? Gradient { get; init; }

    public CellBorder DiagonalUp { get; init; } = CellBorder.None;
    public CellBorder DiagonalDown { get; init; } = CellBorder.None;
    public CellBorder Left { get; init; } = CellBorder.None;
    public CellBorder Right { get; init; } = CellBorder.None;
    public CellBorder Top { get; init; } = CellBorder.None;
    public CellBorder Bottom { get; init; } = CellBorder.None;

    public bool HasBorders => Left.IsVisible || Right.IsVisible || Top.IsVisible || Bottom.IsVisible || DiagonalUp.IsVisible || DiagonalDown.IsVisible;
}

internal enum CellSpecialAlign
{
    None,
    Fill,
    Justify,
    Distributed,
    CenterAcross,
}

/// <summary>A linear gradient: the angle in degrees (0 = left to right) and colour stops by position from 0 to 1.</summary>
internal sealed record CellGradient(double Degree, IReadOnlyList<(double Position, string Color)> Stops);

/// <summary>A piece of a rich-text cell in its own font (null members: the cell's).</summary>
internal sealed record CellRun(string Text)
{
    public bool? Bold { get; init; }
    public bool? Italic { get; init; }
    public bool? Underline { get; init; }
    public bool? Strike { get; init; }
    public double? FontSize { get; init; }
    public string? Color { get; init; }
    public string? FontName { get; init; }

    /// <summary>Superscript (1), subscript (-1) or as the cell (null).</summary>
    public int? Script { get; init; }
}

/// <summary>
/// Something drawn over the cells (a picture or a shape), from its anchor: top-left cell and offset in points, and its
/// size in points or the bottom-right cell it reaches.
/// </summary>
internal abstract record SheetDrawing(int Row, int Column, double OffsetX, double OffsetY, double Width, double Height)
{
    /// <summary>The bottom-right cell and offset of a drawing that moves and sizes with its cells (instead of the size).</summary>
    public (int Row, int Column, double OffsetX, double OffsetY)? To { get; init; }

    /// <summary>
    /// The part of the anchor's box the drawing takes, as fractions of it (left, top, right, bottom): a picture or shape
    /// inside a group, whose anchor is the group's. Null for the whole box.
    /// </summary>
    public (double Left, double Top, double Right, double Bottom)? Part { get; init; }

    /// <summary>Clockwise turn in degrees (a:xfrm rot), about the centre of the box.</summary>
    public double Rotation { get; init; }

    /// <summary>Mirrored left to right (a:xfrm flipH), before the turn.</summary>
    public bool FlipH { get; init; }

    /// <summary>Mirrored top to bottom (a:xfrm flipV), before the turn.</summary>
    public bool FlipV { get; init; }

    /// <summary>
    /// The unturned width and height (a:xfrm ext, any unit: only the proportion counts). Excel anchors a turned
    /// drawing by the box it covers, so a picture turned 90° has an anchor as wide as the picture is tall; this tells
    /// the two apart. Null when the file doesn't say.
    /// </summary>
    public (double Width, double Height)? Size { get; init; }
}

/// <summary>A picture on a sheet.</summary>
/// <param name="Data">The picture file (PNG, JPEG, GIF, BMP).</param>
internal sealed record SheetPicture(byte[] Data, int Row, int Column, double OffsetX, double OffsetY, double Width, double Height)
    : SheetDrawing(Row, Column, OffsetX, OffsetY, Width, Height)
{
    /// <summary>
    /// What is cut off each edge of the picture (a:srcRect), as fractions of its width and height (left, top, right,
    /// bottom). Negative values add empty space instead.
    /// </summary>
    public (double Left, double Top, double Right, double Bottom) Crop { get; init; }
}

/// <summary>A preset shape on a sheet (rectangle, oval, line, arrow, ...), filled and outlined in one colour each.</summary>
/// <param name="Preset">The a:prstGeom name, one <see cref="Slides.PresetShapes"/> draws.</param>
internal sealed record SheetShape(string Preset, int Row, int Column, double OffsetX, double OffsetY, double Width, double Height)
    : SheetDrawing(Row, Column, OffsetX, OffsetY, Width, Height)
{
    /// <summary>The shape's adjust values (a:avLst), by name, as their formulas ("val 16667").</summary>
    public IReadOnlyDictionary<string, string>? Adjust { get; init; }

    /// <summary>Fill colour, or null for none.</summary>
    public SkiaSharp.SKColor? Fill { get; init; }

    /// <summary>Outline colour, or null for none.</summary>
    public SkiaSharp.SKColor? Line { get; init; }

    /// <summary>Outline width in points.</summary>
    public double LineWidth { get; init; } = 0.75;

    /// <summary>A dashed outline's dashes and gaps as multiples of its width, or null for a solid one.</summary>
    public float[]? Dash { get; init; }
}

/// <summary>A cell as displayed: formatted text plus style.</summary>
/// <param name="Text">What the cell shows ("1,234.50", "2026-10-01", "TRUE").</param>
/// <param name="IsNumber">Numbers (and dates) align right under the General alignment.</param>
internal sealed record SheetCell(string Text, CellStyle Style, bool IsNumber = false)
{
    /// <summary>
    /// The value behind <see cref="Text"/>: numbers, dates and times as Excel serial days (1900 date system) and
    /// booleans as 1 / 0; null for text and errors. Writers store it instead of the text.
    /// </summary>
    public double? Value { get; init; }

    /// <summary>Excel number format code of <see cref="Value"/> ("#,##0", "yyyy-mm-dd"); null = General.</summary>
    public string? NumberFormat { get; init; }

    /// <summary>A TRUE / FALSE cell (<see cref="Value"/> is 1 or 0).</summary>
    public bool IsBoolean { get; init; }

    /// <summary>An error value (#N/A, #DIV/0!, ...).</summary>
    public bool IsError { get; init; }

    /// <summary>Rich text: the pieces of <see cref="Text"/> in their own fonts, or null.</summary>
    public IReadOnlyList<CellRun>? Runs { get; init; }

    /// <summary>
    /// Text, a fill or borders; or an empty cell centred across with the cell before it (it belongs to that text).
    /// </summary>
    public bool HasContent => Text.Length > 0 || Style.Fill is not null || Style.HasBorders || Style.SpecialAlign == CellSpecialAlign.CenterAcross;
}
