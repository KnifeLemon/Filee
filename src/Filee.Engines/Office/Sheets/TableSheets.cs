// Tables of the document model (HTable) written onto worksheets: shared by PDF → XLSX (PdfTablesConverter, #267) and
// DOCX / HTML → XLSX (DocumentTablesConverter, #1016).
//
// - Merged cells stay merged; the rest of a merged area keeps the borders, as Excel draws them from every cell.
// - A cell is bold or italic when all its text is, and centred or right-aligned when all its paragraphs are; left and
//   justified text keeps Excel's own alignment (text to the left, numbers to the right).
// - Cells that read as numbers ("1,234.50", "(120)", "$9.99"), percentages ("12.5%") or dates with the year first
//   ("2026-10-23", "2026. 10. 23.") become real numbers and dates, in a number format that shows them as the document
//   did, so they can be calculated with. Anything else stays text: numbers with a leading zero (codes, phone numbers),
//   more than 15 digits, a decimal comma, or day-first dates, which can't be told from month-first ones.
// - An equation in a cell is its plain text (EquationScript.Plain): "x²", "20 이상", "9/2 cm³".
// - Documents' rows under a merged cell get the height its text needs, measured in its font (FitMergedRows).

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Office.Sheets;

/// <summary>Writes document tables onto worksheets, with numbers and dates as real values.</summary>
internal static partial class TableSheets
{
    /// <summary>Points per character of Excel's column width (Calibri 11: 7 px at 96 dpi).</summary>
    private const double PointsPerCharacter = 5.25;

    /// <summary>
    /// The text width that relative column widths (HTML) are shares of: an A4 page with 2 cm margins, as a web page's
    /// table would print.
    /// </summary>
    private const double TextWidthPoints = 482;

    /// <summary>Widest column sized from its text, in characters: longer text wraps.</summary>
    private const double MaxFittedWidth = 60;

    /// <summary>
    /// Sets the sheet's column widths from the table: its absolute widths (PDF, DOCX), its relative ones (HTML), or
    /// else the longest line of each column.
    /// </summary>
    public static void SetColumnWidths(Worksheet sheet, HTable table)
    {
        if (table.ColumnWidths is { } widths)
        {
            for (var column = 0; column < widths.Length; column++)
                sheet.ColumnWidths[column] = Characters(widths[column] / 100.0);
            return;
        }
        if (table.RelativeWidths is { } shares && shares.All(s => s > 0))
        {
            for (var column = 0; column < shares.Length; column++)
                sheet.ColumnWidths[column] = Characters(shares[column] * TextWidthPoints);
            return;
        }
        foreach (var (column, longest) in LongestLines(table))
        {
            // A little room beside the text, as Excel's own "fit" leaves; short columns keep the standard width.
            var width = Math.Min(MaxFittedWidth, Math.Round(longest * 1.1 + 1, 2));
            if (width > Worksheet.StandardColumnWidth)
                sheet.ColumnWidths[column] = width;
        }
    }

    private static double Characters(double points) => Math.Round(Math.Clamp(points / PointsPerCharacter, 2, 100), 2);

    /// <summary>Per grid column: the display width of its longest line, from cells that span that column alone.</summary>
    private static Dictionary<int, int> LongestLines(HTable table)
    {
        var longest = new Dictionary<int, int>();
        foreach (var (cell, _, column, _, _) in Grid(table))
        {
            if (cell.ColSpan > 1)
                continue;
            var width = Text(cell).Split('\n').Max(DisplayWidth);
            longest[column] = Math.Max(longest.GetValueOrDefault(column), width);
        }
        return longest;
    }

    /// <summary>Characters of text as Excel measures them: CJK and full-width characters take two.</summary>
    private static int DisplayWidth(string line)
    {
        var width = 0;
        foreach (var rune in line.EnumerateRunes())
            width += rune.Value is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3
                or >= 0xF900 and <= 0xFAFF or >= 0xFE30 and <= 0xFE4F or >= 0xFF00 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6
                or >= 0x20000 and <= 0x3FFFD ? 2 : 1;
        return width;
    }

    /// <summary>
    /// Every cell of the table with its grid row (0-based within the table), column and the rows and columns it covers,
    /// row spans skipped. Spans end at the table's last row and at <see cref="HwpxWriter.MaxTableColumns"/>.
    /// </summary>
    /// <exception cref="Filee.Core.Conversion.ConversionRefusedException">The cells cover more than <see cref="HwpxWriter.MaxTableCells"/> positions.</exception>
    private static IEnumerable<(HCell Cell, int Row, int Column, int Rows, int Columns)> Grid(HTable table, int skip = 0)
    {
        // Per column, the first row not covered by a cell spanning rows from above (or from the left in this row).
        var freeFrom = new List<int>();
        var rowCount = table.Rows.Count - skip;
        var row = 0;
        long covered = 0;
        foreach (var source in table.Rows.Skip(skip))
        {
            var column = 0;
            foreach (var cell in source.Cells)
            {
                if (cell.Column >= 0)
                    column = Math.Min(cell.Column, HwpxWriter.MaxTableColumns);
                while (column < freeFrom.Count && freeFrom[column] > row)
                    column++;
                if (column >= HwpxWriter.MaxTableColumns)
                    throw HwpxWriter.TableTooLarge();
                // An HTML rowspan may claim more rows than the table has; the merge ends at its last row.
                var rows = Math.Clamp(cell.RowSpan, 1, Math.Max(1, rowCount - row));
                var columns = Math.Clamp(cell.ColSpan, 1, HwpxWriter.MaxTableColumns - column);
                covered += (long)rows * columns;
                if (covered > HwpxWriter.MaxTableCells)
                    throw HwpxWriter.TableTooLarge();
                while (freeFrom.Count < column + columns)
                    freeFrom.Add(0);
                for (var c = column; c < column + columns; c++)
                    freeFrom[c] = Math.Max(freeFrom[c], row + rows);
                yield return (cell, row, column, rows, columns);
                column += columns;
            }
            row++;
        }
    }

    /// <summary>
    /// The grid positions the table's cells cover (rows × columns of every cell), which <see cref="Place"/> writes one
    /// cell each.
    /// </summary>
    /// <exception cref="Filee.Core.Conversion.ConversionRefusedException">The table is larger than <see cref="Grid"/> takes.</exception>
    public static long Positions(HTable table)
    {
        long positions = 0;
        foreach (var (_, _, _, rows, columns) in Grid(table))
            positions += (long)rows * columns;
        return positions;
    }

    /// <summary>
    /// Writes the table's rows from <paramref name="top"/> on, leaving out its first <paramref name="skip"/>; returns
    /// the next free row.
    /// </summary>
    /// <param name="drawn">
    /// The table was found by the lines drawn around its cells (PDF): every cell gets a thin border and the rows keep
    /// their measured heights. Otherwise (DOCX, HTML) the cells keep their own borders and the rows fit their text, as
    /// a document's row height is only a minimum.
    /// </param>
    public static int Place(Worksheet sheet, HTable table, int top, int skip, bool drawn)
    {
        if (drawn)
        {
            var r = top;
            foreach (var source in table.Rows.Skip(skip))
            {
                if (source.Height is { } height && height > 0)
                    sheet.RowHeights[r] = Math.Round(height / 100.0, 2);
                r++;
            }
        }
        var next = top + Math.Max(0, table.Rows.Count - skip);
        var defaults = table.Borders ?? HBorders.Grid;
        foreach (var (cell, row, column, rows, columns) in Grid(table, skip))
        {
            var style = StyleOf(cell, drawn ? null : cell.Borders ?? defaults);
            var at = top + row;
            var text = Text(cell);
            sheet.Set(at, column, Shown(text, style));
            for (var r = at; r < at + rows; r++)
                for (var c = column; c < column + columns; c++)
                    if (r != at || c != column)
                        sheet.Set(r, c, new SheetCell("", style));
            if (rows > 1 || columns > 1)
                sheet.Merges.Add(new CellRange(at, column, at + rows - 1, column + columns - 1));
        }
        return next;
    }

    /// <summary>
    /// Cells of one workbook measured with their fonts (<see cref="FitMergedRows(Workbook, CancellationToken)"/>);
    /// the rest are estimated by their characters, so a hostile table of a million merged cells is written quickly.
    /// </summary>
    internal const int MaxMeasuredCells = 4000;

    /// <summary>
    /// Excel and Calc fit a row to the wrapped text of its cells, but not to a merged cell's: the rows under a merged
    /// cell whose text needs more height than they have get the rest of it, shared out evenly over them, so the text
    /// isn't cut off. The text is measured as the sheet printer measures a cell (SheetRowHeights): in the cell's font
    /// (the workbook's Calibri 11 unless the style names another), broken into lines at the merged columns' width,
    /// each line as tall as its font. A row keeps the height its own cells need (as Excel would fit it), since a row
    /// given a height no longer fits itself. Past <see cref="MaxMeasuredCells"/> cells, lines are counted by
    /// characters (CJK as two) at 15 pt each.
    /// </summary>
    public static void FitMergedRows(Workbook book, CancellationToken cancellationToken = default)
    {
        Measurer? measurer = null;
        try
        {
            foreach (var sheet in book.Sheets)
            {
                var merges = sheet.Merges.Where(m => sheet.Get(m.Top, m.Left) is { Text.Length: > 0 }).ToList();
                if (merges.Count == 0)
                    continue;
                measurer ??= new Measurer(book);
                FitMergedRows(sheet, merges, measurer, cancellationToken);
            }
        }
        finally
        {
            measurer?.Dispose();
        }
    }

    private static void FitMergedRows(Worksheet sheet, List<CellRange> merges, Measurer measurer, CancellationToken cancellationToken)
    {
        var columns = Math.Max(merges.Max(m => m.Right), sheet.Rows.Values.Max(r => r.Count > 0 ? r.Keys.Max() : 0)) + 1;
        var widths = SheetPagination.Widths(sheet, columns, measurer.DefaultFont);
        var index = new SheetIndex(sheet);
        // A row's height: the one set already, else what its own (unmerged) cells need, measured once.
        var heights = new Dictionary<int, double>();
        double Height(int row)
        {
            if (heights.TryGetValue(row, out var known))
                return known;
            var height = sheet.RowHeights.GetValueOrDefault(row);
            if (height <= 0)
            {
                height = measurer.Standard;
                if (sheet.Rows.TryGetValue(row, out var cells))
                {
                    foreach (var (column, cell) in cells)
                    {
                        if (cell.Text.Length > 0 && index.Merge(row, column) is null)
                            height = Math.Max(height, measurer.Height(cell, column < widths.Length ? widths[column] : 0));
                    }
                }
            }
            return heights[row] = height;
        }

        foreach (var merge in merges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var width = 0.0;
            for (var c = merge.Left; c <= merge.Right; c++)
                width += widths[c];
            var needed = measurer.Height(sheet.Get(merge.Top, merge.Left)!, width);
            var have = 0.0;
            for (var r = merge.Top; r <= merge.Bottom; r++)
                have += Height(r);
            if (needed <= have + 0.5)
                continue;
            var extra = (needed - have) / (merge.Bottom - merge.Top + 1);
            for (var r = merge.Top; r <= merge.Bottom; r++)
            {
                // A quarter point up, as Excel stores heights; at most Excel's tallest row.
                var height = Math.Min(409.5, Math.Ceiling((Height(r) + extra) * 4) / 4);
                heights[r] = height;
                sheet.RowHeights[r] = height;
            }
        }
    }

    /// <summary>The height a cell's text needs: measured in its font, or counted (see <see cref="MaxMeasuredCells"/>).</summary>
    private sealed class Measurer : IDisposable
    {
        private readonly Workbook _book;
        private readonly SheetFonts _fonts = new();
        private int _measured;

        public Measurer(Workbook book)
        {
            _book = book;
            DefaultFont = _fonts.Pixels(book.DefaultFontName, book.DefaultFontSize);
        }

        public PixelMetrics DefaultFont { get; }

        /// <summary>One line of the workbook's font: the standard row height.</summary>
        public double Standard => DefaultFont.LineHeight * 0.75;

        public double Height(SheetCell cell, double width)
        {
            // One line of ASCII text well inside its width (no letter is two digits wide), in the workbook font:
            // nothing to shape.
            var characters = width / Math.Max(1, DefaultFont.DigitWidth * 0.75);
            if (cell.Style.FontName is null && cell.Style.FontSize is null && !cell.Text.Contains('\n')
                && cell.Text.All(char.IsAscii) && cell.Text.Length * 2 + 1 < characters)
                return Standard;
            if (_measured++ < MaxMeasuredCells)
                return SheetRowHeights.CellHeight(_book, cell, width, _fonts);
            var perLine = Math.Max(1, (int)Math.Floor(characters - 1));
            var lines = cell.Text.Split('\n').Sum(line => Math.Max(1, (DisplayWidth(line.Replace("\t", "    ")) + perLine - 1) / perLine));
            return lines * Standard;
        }

        public void Dispose() => _fonts.Dispose();
    }

    /// <param name="borders">The cell's own borders; null draws a thin line on every side.</param>
    private static CellStyle StyleOf(HCell cell, HBorders? borders)
    {
        var texts = Texts(cell).Where(t => t.Text.Trim().Length > 0).ToList();
        return new()
        {
            Bold = texts.Count > 0 && texts.All(t => t.Format.Bold == true),
            Italic = texts.Count > 0 && texts.All(t => t.Format.Italic == true),
            Align = AlignOf(cell),
            Fill = cell.Fill,
            Wrap = true,
            VerticalAlign = cell.VerticalAlign switch
            {
                HVerticalAlign.Center => CellVerticalAlign.Center,
                HVerticalAlign.Bottom => CellVerticalAlign.Bottom,
                _ => CellVerticalAlign.Top,
            },
            Left = borders is { } b ? Side(b.Left) : Thin,
            Right = borders is { } r ? Side(r.Right) : Thin,
            Top = borders is { } t ? Side(t.Top) : Thin,
            Bottom = borders is { } d ? Side(d.Bottom) : Thin,
        };
    }

    /// <summary>Centred or right-aligned when every paragraph with text is; else Excel's own alignment.</summary>
    private static CellAlign AlignOf(HCell cell)
    {
        HAlign? shared = null;
        foreach (var paragraph in cell.Blocks.OfType<HParagraph>())
        {
            if (!HDocumentWalker.InlinesOf(paragraph.Inlines).OfType<HText>().Any(t => t.Text.Trim().Length > 0))
                continue;
            var align = paragraph.Format.Align ?? HAlign.Left;
            if (shared is { } known && known != align)
                return CellAlign.General;
            shared = align;
        }
        return shared switch
        {
            HAlign.Center => CellAlign.Center,
            HAlign.Right => CellAlign.Right,
            _ => CellAlign.General,
        };
    }

    private static readonly CellBorder Thin = new("thin", "#000000");

    /// <summary>A document border as the nearest Excel border style.</summary>
    private static CellBorder Side(HBorder border) => border.Style switch
    {
        HBorderStyle.None => CellBorder.None,
        HBorderStyle.Double => new("double", border.Color),
        HBorderStyle.Dash => new("dashed", border.Color),
        HBorderStyle.Dot => new("dotted", border.Color),
        HBorderStyle.DashDot => new("dashDot", border.Color),
        _ => new(border.WidthMm >= 1 ? "thick" : border.WidthMm >= 0.5 ? "medium" : "thin", border.Color),
    };

    /// <summary>The text runs of a cell's paragraphs, those inside links included.</summary>
    private static IEnumerable<HText> Texts(HCell cell) =>
        cell.Blocks.OfType<HParagraph>().SelectMany(p => HDocumentWalker.InlinesOf(p.Inlines)).OfType<HText>();

    /// <summary>The texts of a row's cells, to compare a repeated header row with the first one.</summary>
    public static string[] RowTexts(HRow row) => [.. row.Cells.Select(Text)];

    /// <summary>
    /// A cell's text: its paragraphs as lines, equations as plain text (a table inside it is left out; it gets a sheet of
    /// its own).
    /// </summary>
    public static string Text(HCell cell)
    {
        var lines = new List<string>();
        foreach (var paragraph in cell.Blocks.OfType<HParagraph>())
        {
            var line = new StringBuilder();
            foreach (var inline in HDocumentWalker.InlinesOf(paragraph.Inlines))
                line.Append(inline switch
                {
                    // An equation as plain text: "20 이상", not its script's "20^(이상)".
                    HEquation equation => EquationScript.Plain(equation.Math),
                    HText text => text.Text,
                    HTab => "\t",
                    HLineBreak => "\n",
                    _ => "",
                });
            lines.Add(line.ToString());
        }
        return string.Join('\n', lines).Trim();
    }

    // ───────────────────────── Numbers and dates ─────────────────────────

    /// <summary>A cell for text as the document shows it: a number or date with a matching format, or text.</summary>
    public static SheetCell Shown(string text, CellStyle style)
    {
        if (Number(text) is { } number)
            return new SheetCell(text, style, IsNumber: true) { Value = number.Value, NumberFormat = number.Format };
        if (Date(text) is { } date)
            return new SheetCell(text, style, IsNumber: true) { Value = date.Value, NumberFormat = date.Format };
        return new SheetCell(text, style);
    }

    /// <summary>"1,234.50", "-7", "(120)", "$9.99", "12.5%" as a value and an Excel number format; null otherwise.</summary>
    public static (double Value, string Format)? Number(string text)
    {
        if (NumberPattern().Match(text.Trim()) is not { Success: true } match)
            return null;
        var whole = match.Groups["whole"].Value;
        var fraction = match.Groups["fraction"].Value;
        var digits = whole.Replace(",", "") + fraction;
        // Codes and phone numbers keep their leading zero; Excel holds 15 significant digits.
        if ((whole.Length > 1 && whole[0] == '0') || digits.TrimStart('0').Length > 15)
            return null;
        var negative = match.Groups["minus"].Success || match.Groups["open"].Success;
        if (match.Groups["open"].Success != match.Groups["close"].Success)
            return null;
        var value = double.Parse(whole.Replace(",", "") + (fraction.Length > 0 ? "." + fraction : ""), CultureInfo.InvariantCulture);
        var format = (whole.Contains(',') ? "#,##0" : "0") + (fraction.Length > 0 ? "." + new string('0', fraction.Length) : "");
        if (match.Groups["percent"].Success)
        {
            value /= 100;
            format += "%";
        }
        if (match.Groups["currency"].Value is { Length: > 0 } currency)
            format = $"\"{currency}\"{format}";
        if (match.Groups["open"].Success)
            format = $"{format};({format})";
        return (negative ? -value : value, format);
    }

    /// <summary>"2026-10-23", "2026/1/5", "2026. 10. 23." as an Excel date serial and format; null otherwise.</summary>
    public static (double Value, string Format)? Date(string text)
    {
        if (DatePattern().Match(text.Trim()) is not { Success: true } match)
            return null;
        var (year, month, day) = (match.Groups["y"].Value, match.Groups["m"].Value, match.Groups["d"].Value);
        if (!DateOnly.TryParseExact($"{year}-{month.PadLeft(2, '0')}-{day.PadLeft(2, '0')}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return null;
        var separator = match.Groups["sep"].Value;
        var format = "yyyy" + separator + (month.Length == 2 ? "mm" : "m") + separator + (day.Length == 2 ? "dd" : "d") + match.Groups["end"].Value;
        return (date.DayNumber - new DateOnly(1899, 12, 30).DayNumber, format);
    }

    [GeneratedRegex(@"^(?<minus>-)?(?<open>\()?(?<currency>[$€£¥₩₹])?(?<whole>\d{1,3}(?:,\d{3})+|\d+)(?:\.(?<fraction>\d+))?(?<percent>%)?(?<close>\))?$")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"^(?<y>\d{4})(?<sep>-|/|\. ?)(?<m>\d{1,2})\k<sep>(?<d>\d{1,2})(?<end>\.?)$")]
    private static partial Regex DatePattern();
}
