// Row heights of a printed sheet in points: hidden rows 0, the height the file stores (custom, or the autofit Excel
// and Calc saved), else what Excel's autofit gives — the tallest of the row's cells: one line of the cell's font
// (rounded ascent + descent + 2 px), the lines of wrapped text in its column's width, rotated text's extent — and at
// least the sheet's default height. Merged cells don't count, as in Excel.

namespace Filee.Engines.Office.Sheets;

internal static class SheetRowHeights
{
    /// <param name="widths">Column widths in points (wrapped text is measured in them).</param>
    public static double[] Measure(Workbook book, Worksheet sheet, int count, SheetFonts fonts, PixelMetrics defaultFont,
        double[] widths, SheetIndex index, CancellationToken cancellationToken)
    {
        var heights = new double[Math.Max(0, count)];
        var standard = sheet.DefaultRowHeight is { } stored and > 0 and <= 409 ? stored : defaultFont.LineHeight * 0.75;
        Array.Fill(heights, standard);
        foreach (var (row, cells) in sheet.Rows)
        {
            if (row >= count)
                break;
            if (sheet.RowHeights.ContainsKey(row) || sheet.AutoRowHeights.ContainsKey(row))
                continue;
            cancellationToken.ThrowIfCancellationRequested();
            var height = standard;
            foreach (var (column, cell) in cells)
            {
                if (cell.Text.Length == 0 || index.Merge(row, column) is not null)
                    continue;
                height = Math.Max(height, CellHeight(book, cell, column < widths.Length ? widths[column] : 0, fonts));
            }
            heights[row] = Math.Min(409.5, height);
        }
        foreach (var (row, height) in sheet.AutoRowHeights)
        {
            if (row >= 0 && row < count)
                heights[row] = Math.Clamp(height, 0, 409.5);
        }
        foreach (var (row, height) in sheet.RowHeights)
        {
            if (row >= 0 && row < count)
                heights[row] = Math.Clamp(height, 0, 409.5);
        }
        foreach (var row in sheet.HiddenRows)
        {
            if (row >= 0 && row < count)
                heights[row] = 0;
        }
        return heights;
    }

    private static double CellHeight(Workbook book, SheetCell cell, double columnWidth, SheetFonts fonts)
    {
        var style = cell.Style;
        if (style.Rotation == 0 && !CellText.Wraps(style))
        {
            // One line: the tallest of its fonts, no text to shape.
            var single = CellText.Style(book, style, SheetColors.Black);
            var pixels = fonts.Pixels(single.Family, single.Size, single.Bold, single.Italic).LineHeight;
            foreach (var run in cell.Runs ?? [])
                pixels = Math.Max(pixels, fonts.Pixels(run.FontName ?? single.Family, run.FontSize is > 0 and <= 409 ? run.FontSize.Value : single.Size,
                    run.Bold ?? single.Bold, run.Italic ?? single.Italic).LineHeight);
            return pixels * 0.75;
        }
        var text = CellText.Build(fonts, book, cell, style, SheetColors.Black, wrap: CellText.Wraps(style));
        if (text.IsEmpty)
            return 0;
        var lineHeight = text.LineMetrics(0, text.Text.Length).LineHeight;
        if (style.Rotation == CellStyle.Stacked)
            return lineHeight * text.Text.Count(ch => !char.IsLowSurrogate(ch) && ch != '\n');
        if (style.Rotation != 0)
        {
            var angle = Math.Abs(style.Rotation) * Math.PI / 180;
            return text.SingleLineWidth() * Math.Sin(angle) + lineHeight * Math.Cos(angle) + 3;
        }
        if (!CellText.Wraps(style))
            return lineHeight;
        var width = (float)Math.Max(1, columnWidth - CellText.Padding * 2);
        var lines = text.Lines(width, wrap: true);
        var total = 0.0;
        foreach (var (start, end) in lines)
            total += text.LineMetrics(start, end).LineHeight;
        return total;
    }
}
