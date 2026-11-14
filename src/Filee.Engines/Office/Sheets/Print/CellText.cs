// The text of one cell as it prints: the cell's font (or the runs of rich text), line feeds kept only when the text
// wraps (Excel shows a cell without wrapping on one line), and the colour a number format gives ([Red], [Color10]).

using System.Globalization;
using System.Text;
using SkiaSharp;

namespace Filee.Engines.Office.Sheets;

internal static class CellText
{
    /// <summary>Space between a cell's edge and its text: 2 px at 96 dpi.</summary>
    public const float Padding = 1.5f;

    /// <summary>The cell's text is broken into lines in its width.</summary>
    public static bool Wraps(CellStyle style) =>
        style.Wrap || style.SpecialAlign is CellSpecialAlign.Justify or CellSpecialAlign.Distributed || style.VerticalJustify;

    public static TextStyle Style(Workbook book, CellStyle style, SKColor color) => new(
        style.FontName ?? book.DefaultFontName, style.FontSize is > 0 and <= 409 ? style.FontSize.Value : book.DefaultFontSize,
        style.Bold, style.Italic, style.Underline && !style.DoubleUnderline, style.DoubleUnderline, style.Strike, style.Script)
    {
        Color = color,
    };

    /// <summary>The cell's text (or <paramref name="text"/> instead, as for "####") in the cell's styles.</summary>
    public static StyledText Build(SheetFonts fonts, Workbook book, SheetCell cell, CellStyle style, SKColor color, bool wrap, string? text = null)
    {
        var baseStyle = Style(book, style, color);
        if (text is null && cell.Runs is { Count: > 0 } runs)
        {
            var sb = new StringBuilder();
            var spans = new List<(int, int, TextStyle)>();
            foreach (var run in runs)
            {
                var piece = wrap ? run.Text : run.Text.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
                var start = sb.Length;
                sb.Append(piece);
                spans.Add((start, piece.Length, baseStyle with
                {
                    Bold = run.Bold ?? baseStyle.Bold,
                    Italic = run.Italic ?? baseStyle.Italic,
                    Underline = run.Underline ?? baseStyle.Underline,
                    Strike = run.Strike ?? baseStyle.Strike,
                    Size = run.FontSize is > 0 and <= 409 ? run.FontSize.Value : baseStyle.Size,
                    Family = run.FontName ?? baseStyle.Family,
                    Script = run.Script ?? baseStyle.Script,
                    Color = run.Color is { } runColor ? SheetColors.Parse(runColor, color) : color,
                }));
            }
            return new StyledText(fonts, sb.ToString(), spans);
        }
        var shown = text ?? cell.Text;
        if (!wrap)
            shown = shown.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
        return new StyledText(fonts, shown, [(0, shown.Length, baseStyle)]);
    }
}

internal static class SheetColors
{
    public static readonly SKColor Black = SKColors.Black;

    /// <summary>"#RRGGBB" (or "#AARRGGBB") → a colour; <paramref name="fallback"/> for anything else.</summary>
    public static SKColor Parse(string? hex, SKColor fallback)
    {
        if (hex is null || hex.Length is not (7 or 9) || hex[0] != '#'
            || !uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            return fallback;
        return hex.Length == 7 ? new SKColor(value | 0xFF000000) : new SKColor(value);
    }

    /// <summary>Excel's eight named colours of number formats; [ColorN] picks from the 56-colour palette.</summary>
    private static readonly Dictionary<string, string> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Black"] = "#000000",
        ["White"] = "#FFFFFF",
        ["Red"] = "#FF0000",
        ["Green"] = "#00FF00",
        ["Blue"] = "#0000FF",
        ["Yellow"] = "#FFFF00",
        ["Magenta"] = "#FF00FF",
        ["Cyan"] = "#00FFFF",
    };

    /// <summary>The colour the section of <paramref name="format"/> that shows <paramref name="value"/> asks for, or null.</summary>
    public static string? NumberFormatColor(string? format, double value)
    {
        if (string.IsNullOrEmpty(format) || !format.Contains('['))
            return null;
        var sections = Sections(format);
        // Sections with conditions ([>100]) choose differently; their colours are left alone.
        if (sections.Any(s => s.Contains("[<", StringComparison.Ordinal) || s.Contains("[>", StringComparison.Ordinal) || s.Contains("[=", StringComparison.Ordinal)))
            return null;
        var section = sections.Count switch
        {
            1 => sections[0],
            2 => value < 0 ? sections[1] : sections[0],
            _ => value < 0 ? sections[1] : value == 0 ? sections[2] : sections[0],
        };
        var i = 0;
        while ((i = section.IndexOf('[', i)) >= 0)
        {
            var end = section.IndexOf(']', i);
            if (end < 0)
                break;
            var name = section[(i + 1)..end];
            if (Named.TryGetValue(name, out var hex))
                return hex;
            if (name.StartsWith("Color", StringComparison.OrdinalIgnoreCase) && int.TryParse(name.AsSpan(5), out var index) && index is >= 1 and <= 56)
                return XlsPalette.Default[index - 1];
            i = end + 1;
        }
        return null;
    }

    /// <summary>The ';'-separated sections of a format code (not inside quotes or brackets).</summary>
    private static List<string> Sections(string format)
    {
        var sections = new List<string>();
        var start = 0;
        var quoted = false;
        var bracket = false;
        for (var i = 0; i < format.Length; i++)
        {
            switch (format[i])
            {
                case '"':
                    quoted = !quoted;
                    break;
                case '\\':
                    i++;
                    break;
                case '[' when !quoted:
                    bracket = true;
                    break;
                case ']' when !quoted:
                    bracket = false;
                    break;
                case ';' when !quoted && !bracket:
                    sections.Add(format[start..i]);
                    start = i + 1;
                    break;
            }
        }
        sections.Add(format[start..]);
        return sections;
    }
}

/// <summary>Excel's default 56-colour palette (indexes 8–63 of XLS colour numbers, [Color1]–[Color56] of number formats).</summary>
internal static class XlsPalette
{
    public static readonly string[] Default =
    [
        "#000000", "#FFFFFF", "#FF0000", "#00FF00", "#0000FF", "#FFFF00", "#FF00FF", "#00FFFF",
        "#800000", "#008000", "#000080", "#808000", "#800080", "#008080", "#C0C0C0", "#808080",
        "#9999FF", "#993366", "#FFFFCC", "#CCFFFF", "#660066", "#FF8080", "#0066CC", "#CCCCFF",
        "#000080", "#FF00FF", "#FFFF00", "#00FFFF", "#800080", "#800000", "#008080", "#0000FF",
        "#00CCFF", "#CCFFFF", "#CCFFCC", "#FFFF99", "#99CCFF", "#FF99CC", "#CC99FF", "#FFCC99",
        "#3366FF", "#33CCCC", "#99CC00", "#FFCC00", "#FF9900", "#FF6600", "#666699", "#969696",
        "#003366", "#339966", "#003300", "#333300", "#993300", "#993366", "#333399", "#333333",
    ];
}
