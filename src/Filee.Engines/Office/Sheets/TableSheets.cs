// Tables of the document model (HTable) written onto worksheets: shared by PDF → XLSX (PdfTablesConverter, #267) and
// DOCX / HTML → XLSX (DocumentTablesConverter, #1016).
//
// - Merged cells stay merged or are filled as the preset says (DocumentOptions.MergedCells): Smart keeps the header's,
//   the across-columns ones and the ones holding a number, and fills text down the body's rows. A kept merge's other
//   cells keep the borders, as Excel draws them from every cell. A fill that would repeat more than
//   MaxFilledCharacters of text on one sheet stays merged.
// - Cells that read as numbers ("1,234.50", "(120)", "$9.99"), percentages ("12.5%") or dates with the year first
//   ("2026-10-23", "2026. 10. 23.") become real numbers and dates, in a number format that shows them as the document
//   did, so they can be calculated with. Anything else stays text: numbers with a leading zero (codes, phone numbers),
//   more than 15 digits, a decimal comma, or day-first dates, which can't be told from month-first ones.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Filee.Core.Presets;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;

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
    /// Most characters that filling merged cells may repeat in one table. The cell limit
    /// (<see cref="HwpxWriter.MaxTableCells"/>) doesn't bound text: one long cell merged over a million rows would be
    /// copied into each. Every copy lands in the sheet's one XML part, which readers (ours: <see cref="BoundedXml.MaxPartBytes"/>)
    /// refuse past a size, so a fill stays well under it; past the budget, the merge stays merged.
    /// </summary>
    internal const long MaxFilledCharacters = BoundedXml.MaxPartBytes / 8;

    /// <summary>
    /// The characters filling has repeated on one sheet so far, against <see cref="MaxFilledCharacters"/>: shared by
    /// the parts of a PDF table that goes on over several pages, which all land on that sheet.
    /// </summary>
    internal sealed class FillBudget
    {
        public long Used { get; set; }
    }

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
    /// Writes the table's rows from <paramref name="top"/> on, leaving out its first <paramref name="skip"/>; returns
    /// the next free row.
    /// </summary>
    /// <param name="drawn">
    /// The table was found by the lines drawn around its cells (PDF): every cell gets a thin border and the rows keep
    /// their measured heights. Otherwise (DOCX, HTML) the cells keep their own borders and the rows fit their text, as
    /// a document's row height is only a minimum.
    /// </param>
    /// <param name="merged">
    /// Which merged cells stay merged (<see cref="MergedCells"/>); the rest are filled, up to
    /// <see cref="MaxFilledCharacters"/> repeated in the table.
    /// </param>
    /// <param name="continues">
    /// The table goes on from the one placed above it (a PDF table over a page break): it has no header rows of its own.
    /// </param>
    /// <param name="budget">
    /// What filling has already used on this sheet, when the table goes on from one placed above it; a new budget
    /// otherwise.
    /// </param>
    public static int Place(Worksheet sheet, HTable table, int top, int skip, bool drawn,
        MergedCells merged, bool continues = false, FillBudget? budget = null)
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
        var cells = Grid(table, skip).ToList();
        var header = merged == MergedCells.Smart ? HeaderRows(table, cells, skip, continues) : 0;
        budget ??= new FillBudget();
        foreach (var (cell, row, column, rows, columns) in cells)
        {
            var style = StyleOf(cell, drawn ? null : cell.Borders ?? defaults);
            var at = top + row;
            var text = Text(cell);
            var shown = Shown(text, style);
            var spans = rows > 1 || columns > 1;
            var keep = spans && KeepsMerge(merged, row, rows, header, text, shown.IsNumber);
            var fill = spans && !keep && text.Length > 0;
            if (fill)
            {
                var repeated = (long)text.Length * ((long)rows * columns - 1);
                if (budget.Used + repeated > MaxFilledCharacters)
                    (keep, fill) = (true, false);
                else
                    budget.Used += repeated;
            }
            sheet.Set(at, column, shown);
            for (var r = at; r < at + rows; r++)
                for (var c = column; c < column + columns; c++)
                    if (r != at || c != column)
                        // A filled merge repeats its value, so every row reads whole on its own (filters, pivot tables).
                        sheet.Set(r, c, fill ? shown : new SheetCell("", style));
            if (keep)
                sheet.Merges.Add(new CellRange(at, column, at + rows - 1, column + columns - 1));
        }
        return next;
    }

    /// <summary>
    /// Whether a cell covering <paramref name="rows"/> rows from <paramref name="row"/> stays merged. Smart keeps the
    /// merges that only help reading: in the header rows (empty ones too, such as the corner above a column of labels),
    /// and across columns (a title, a "Total" label, a group heading). A merge holding a number or a date down the rows
    /// of the body (a group total beside its items) stays merged too: repeating it into every row would change what the
    /// column adds up to. A text merge down the body (a category beside its items) leaves blanks that filters and pivot
    /// tables read as missing, so it is filled. An empty merged cell in the body holds nothing to repeat and isn't
    /// merged.
    /// </summary>
    private static bool KeepsMerge(MergedCells merged, int row, int rows, int header, string text, bool number) => merged switch
    {
        MergedCells.Keep => true,
        MergedCells.Fill => false,
        _ => row < header || (text.Length > 0 && (rows == 1 || number)),
    };

    /// <summary>
    /// How many rows at the top of the table are its header, for <see cref="MergedCells.Smart"/>: the rows the document
    /// marks as header (DOCX "repeat as header row", HTML &lt;thead&gt;), or else the first row; a title across the
    /// whole table brings in the row under it; and a header cell spanning rows down brings in every row it covers
    /// (two-level headings such as "Q1" over "Jan Feb Mar" beside an "Item" spanning both). A table going on from the
    /// one above it has none.
    /// </summary>
    internal static int HeaderRows(HTable table, IReadOnlyList<(HCell Cell, int Row, int Column, int Rows, int Columns)> cells,
        int skip, bool continues)
    {
        var count = table.Rows.Count - skip;
        if (continues || count <= 0 || cells.Count == 0)
            return 0;
        var marked = table.Rows.Skip(skip).TakeWhile(r => r.Header).Count();
        var header = Math.Max(1, marked);
        var width = cells.Max(c => c.Column + c.Columns);
        if (marked == 0 && count > 1 && cells.Where(c => c.Row == 0).ToList() is [{ Column: 0 } title] && title.Columns == width && width > 1)
            header = 2;
        // A cell reaching down from the header only makes it a two-level heading when the header is marked, or has a
        // heading across columns ("Q1" over "Jan Feb Mar"). In an unmarked table (every PDF one) a first row whose
        // category spans its items' rows is the body's first row, and growing over it would freeze and keep the body.
        var twoLevel = marked > 0 || cells.Any(c => c.Row < header && c.Columns > 1);
        for (var grew = twoLevel; grew;)
        {
            grew = false;
            foreach (var c in cells)
                if (c.Row < header && c.Row + c.Rows > header)
                {
                    header = c.Row + c.Rows;
                    grew = true;
                }
        }
        return Math.Min(header, count);
    }

    /// <summary>The header rows of a whole table (<see cref="HeaderRows(HTable, IReadOnlyList{ValueTuple{HCell, int, int, int, int}}, int, bool)"/>).</summary>
    public static int HeaderRows(HTable table) => HeaderRows(table, Grid(table).ToList(), 0, false);

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
