// Tables of the document model (HTable) written onto worksheets: shared by PDF → XLSX (PdfTablesConverter, #267) and
// DOCX / HTML → XLSX (DocumentTablesConverter, #1016).
//
// - Merged cells stay merged; the rest of a merged area keeps the borders, as Excel draws them from every cell.
// - Cells that read as numbers ("1,234.50", "(120)", "$9.99"), percentages ("12.5%") or dates with the year first
//   ("2026-10-23", "2026. 10. 23.") become real numbers and dates, in a number format that shows them as the document
//   did, so they can be calculated with. Anything else stays text: numbers with a leading zero (codes, phone numbers),
//   more than 15 digits, a decimal comma, or day-first dates, which can't be told from month-first ones.

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
        foreach (var (cell, _, column) in Grid(table))
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

    /// <summary>Every cell of the table with its grid row (0-based within the table) and column, row spans skipped.</summary>
    private static IEnumerable<(HCell Cell, int Row, int Column)> Grid(HTable table, int skip = 0)
    {
        var taken = new HashSet<(int, int)>();
        var rowCount = table.Rows.Count - skip;
        var row = 0;
        foreach (var source in table.Rows.Skip(skip))
        {
            var column = 0;
            foreach (var cell in source.Cells)
            {
                if (cell.Column >= 0)
                    column = cell.Column;
                while (taken.Contains((row, column)))
                    column++;
                // An HTML rowspan may claim more rows than the table has; the merge ends at its last row.
                var rows = Math.Clamp(cell.RowSpan, 1, Math.Max(1, rowCount - row));
                var columns = Math.Max(1, cell.ColSpan);
                for (var r = row; r < row + rows; r++)
                    for (var c = column; c < column + columns; c++)
                        taken.Add((r, c));
                yield return (cell, row, column);
                column += columns;
            }
            row++;
        }
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
        foreach (var (cell, row, column) in Grid(table, skip))
        {
            var style = StyleOf(cell, drawn ? null : cell.Borders ?? defaults);
            var at = top + row;
            sheet.Set(at, column, Shown(Text(cell), style));
            var rows = Math.Clamp(cell.RowSpan, 1, Math.Max(1, next - at));
            var columns = Math.Max(1, cell.ColSpan);
            for (var r = at; r < at + rows; r++)
                for (var c = column; c < column + columns; c++)
                    if (r != at || c != column)
                        sheet.Set(r, c, new SheetCell("", style));
            if (rows > 1 || columns > 1)
                sheet.Merges.Add(new CellRange(at, column, at + rows - 1, column + columns - 1));
        }
        return next;
    }

    /// <param name="borders">The cell's own borders; null draws a thin line on every side.</param>
    private static CellStyle StyleOf(HCell cell, HBorders? borders) => new()
    {
        Bold = Texts(cell).ToList() is { Count: > 0 } texts && texts.All(t => t.Format.Bold == true || t.Text.Trim().Length == 0),
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

    /// <summary>A cell's text: its paragraphs as lines (a table inside it is left out; it gets a sheet of its own).</summary>
    public static string Text(HCell cell)
    {
        var lines = new List<string>();
        foreach (var paragraph in cell.Blocks.OfType<HParagraph>())
        {
            var line = new StringBuilder();
            foreach (var inline in HDocumentWalker.InlinesOf(paragraph.Inlines))
                line.Append(inline switch { HText text => text.Text, HTab => "\t", HLineBreak => "\n", _ => "" });
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
