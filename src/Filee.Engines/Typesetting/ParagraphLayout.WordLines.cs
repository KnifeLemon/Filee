// The height of Word's lines (HDocument.WordLineHeights: models read from DOCX and DOC).
//
// - A font's line is its Win ascent and descent (OS/2 usWinAscent, usWinDescent) and Windows' external leading above
//   them (FontMetrics.ExternalLeading: the hhea line gap less what the Win metrics reach beyond the hhea ascender and
//   descender), as GDI gives Word a font's height: 12 pt Times New Roman or Arial is 13.8 pt (2355 of 2048 units), not
//   13.3; Calibri, Cambria and Segoe UI have none (their Win metrics already reach past the hhea line gap). The
//   typographic metrics and the USE_TYPO_METRICS bit play no part. Not rounded (13.799 pt; whether Word rounds to
//   twips or pixels isn't measured).
// - An East Asian font (one for a Chinese, Japanese or Korean code page, OS/2 ulCodePageRange1) gets 30% more, half
//   above the text and half below: a single line of 12 pt SimSun is 15.6 pt in Word, not 12, its text 1.8 pt below the
//   line's top (#356: 23.4 pt at 1.5 lines). Microsoft YaHei and Malgun Gothic, whose Win metrics are 1.33 em, get
//   1.73 em.
// - A named East Asian font that isn't installed keeps the height Word gives it: the font drawing in its place (Noto Sans
//   CJK, 1.45 em) is scaled to the named font's Win height.
// - A math font (Cambria Math, with a MATH table) has Win metrics that make room for big operators (5.6 em); Word gives
//   its text, and a paragraph mark in it, the typographic height (1.17 em).
// - A character another font draws because the named one lacks it counts with the named font's height, as Word
//   measures the fonts the runs name. Characters read from a symbol font (w:sym, text in Wingdings: TextStyle.Symbol)
//   count with the symbol font: drawn in it where it is installed, else with its Win metrics (SymbolFontHeights), so a
//   20 pt ☑ from Wingdings 2's F052 is 21.1 pt high on every system. Other symbols (☑ typed as Unicode, drawn in a
//   fallback font that is often much taller) count only on a line without other text.
// - "At least" (w:lineRule="atLeast") is at least the line single spacing gives the fonts, not the font size: "at least
//   12 pt" keeps 12 pt Times New Roman's 13.8 pt. Extra room goes below the text, as a multiple's does.
// - Word's document grid (HLineHeight.GridPitch, set for the body's paragraphs that snap to it): a line's text takes
//   whole grid lines, the text in their middle; the multiple is of that height.
//
// Choices (not measured in Word: no reporter file has a grid that adds room and Word's PDF): the grid's extra room goes
// half above and half below, like the East Asian leading. The grid sets the lines per page of the section's text
// (ECMA-376 17.6.5 docGrid, 17.3.1.32 snapToGrid), so only the body's lines snap: headers, footers, notes and text
// boxes, which those lines don't count, keep their fonts' lines (DocxReader.SnapToGrid). Table cells snap only with
// w:adjustLineHeightInTable.

using System.Globalization;
using Filee.Engines.Hwp.Hwpx;

namespace Filee.Engines.Typesetting;

internal sealed partial class ParagraphLayouter
{
    /// <summary>What Word adds to an East Asian font's ascent and descent for its line, as a share of them.</summary>
    private const float EastAsianLeading = 0.3f;

    /// <summary>Share of a grid line a line may be over and still take one line fewer (15.6 pt in a 15.6 pt grid).</summary>
    private const float GridTolerance = 0.01f;

    /// <summary>
    /// The Win ascent and descent (em) of East Asian fonts Word documents name, for when another font draws in their place.
    /// </summary>
    private static readonly Dictionary<string, float> EastAsianFontHeights = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SimSun"] = 1f,
        ["宋体"] = 1f,
        ["NSimSun"] = 1f,
        ["新宋体"] = 1f,
        ["SimHei"] = 1f,
        ["黑体"] = 1f,
        ["FangSong"] = 1f,
        ["仿宋"] = 1f,
        ["KaiTi"] = 1f,
        ["楷体"] = 1f,
        ["Microsoft YaHei"] = 1.32f,
        ["微软雅黑"] = 1.32f,
        ["DengXian"] = 1.042f,
        ["等线"] = 1.042f,
        ["PMingLiU"] = 1f,
        ["MingLiU"] = 1f,
        ["新細明體"] = 1f,
        ["細明體"] = 1f,
        ["Microsoft JhengHei"] = 1.33f,
        ["微軟正黑體"] = 1.33f,
        ["MS Mincho"] = 1f,
        ["ＭＳ 明朝"] = 1f,
        ["MS PMincho"] = 1f,
        ["ＭＳ Ｐ明朝"] = 1f,
        ["MS Gothic"] = 1f,
        ["ＭＳ ゴシック"] = 1f,
        ["MS PGothic"] = 1f,
        ["ＭＳ Ｐゴシック"] = 1f,
        ["Yu Mincho"] = 1.287f,
        ["游明朝"] = 1.287f,
        ["Yu Gothic"] = 1.287f,
        ["游ゴシック"] = 1.287f,
        ["Meiryo"] = 1.5f,
        ["メイリオ"] = 1.5f,
        ["Batang"] = 1f,
        ["바탕"] = 1f,
        ["Gulim"] = 1f,
        ["굴림"] = 1f,
        ["Dotum"] = 1f,
        ["돋움"] = 1f,
        ["Gungsuh"] = 1f,
        ["궁서"] = 1f,
        ["Malgun Gothic"] = 1.33f,
        ["맑은 고딕"] = 1.33f,
    };

    /// <summary>
    /// Win ascent and descent (em) of the symbol fonts, for when another font draws their characters: Word measures the
    /// symbol font.
    /// </summary>
    private static readonly Dictionary<string, (float Ascent, float Descent)> SymbolFontHeights = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Wingdings"] = (1841 / 2048f, 432 / 2048f),
        ["Wingdings 2"] = (1727 / 2048f, 432 / 2048f),
        ["Wingdings 3"] = (1900 / 2048f, 432 / 2048f),
        ["Webdings"] = (1638 / 2048f, 410 / 2048f),
        ["Symbol"] = (2059 / 2048f, 450 / 2048f),
    };

    /// <summary>Lines take the height Word gives their fonts (HDocument.WordLineHeights).</summary>
    public bool WordLineHeights { get; set; }

    /// <summary>Ascent and descent of a style's line in Word (see the notes at the top).</summary>
    internal static (float Ascent, float Descent) WordExtent(TextStyle style, ResolvedFont font, FontMetrics metrics)
    {
        var scale = style.Size / metrics.UnitsPerEm;
        var (ascent, descent) = metrics.Math
            ? ((metrics.Ascender + metrics.LineGap) * scale, -metrics.Descender * scale)
            : metrics.WinAscent > 0 || metrics.WinDescent > 0
            ? (metrics.WinAscent * scale, metrics.WinDescent * scale)
            : (metrics.Ascender * scale, (metrics.LineGap - metrics.Descender) * scale);
        var eastAsian = metrics.EastAsian;
        if (style.Family?.Trim() is { Length: > 0 } family && EastAsianFontHeights.TryGetValue(family, out var em)
            && !string.Equals(family, font.Family, StringComparison.OrdinalIgnoreCase) && ascent + descent > 0)
        {
            var fit = em * style.Size / (ascent + descent);
            (ascent, descent, eastAsian) = (ascent * fit, descent * fit, true);
        }
        if (!eastAsian)
        {
            // Windows' external leading, above the text; East Asian fonts have their own leading instead (SimSun's 0.14 em
            // line gap doesn't add to its 1.3 em line), and a math font's typographic height holds its line gap.
            return metrics.Math || metrics.WinAscent <= 0 && metrics.WinDescent <= 0
                ? (ascent, descent)
                : (ascent + metrics.ExternalLeading * scale, descent);
        }
        var leading = (ascent + descent) * EastAsianLeading / 2;
        return (ascent + leading, descent + leading);
    }

    /// <summary>
    /// A Word item of symbol-font characters (TextStyle.Symbol): the symbol font's line where it draws them, else the
    /// symbol font's known Win metrics; null for other items and formats.
    /// </summary>
    private (float Ascent, float Descent)? SymbolExtent(Item item, bool fromFont)
    {
        if (!WordLineHeights || !fromFont || item.Style.Symbol is not { } symbol)
            return null;
        // Measured as the symbol font itself, not as the text font it stands in for (宋体's East Asian height).
        if (item.Drawn is not null)
            return Extent(item.Style with { Family = symbol }, item.Font, fromFont);
        return SymbolFontHeights.TryGetValue(symbol, out var em) ? (em.Ascent * item.Style.Size, em.Descent * item.Style.Size) : null;
    }

    /// <summary>
    /// The font a text item's height is measured with: in Word the font its run names, also where another font draws
    /// characters that one lacks.
    /// </summary>
    private ResolvedFont Measured(Item item, string language) =>
        WordLineHeights ? Font(item.Style.Family, item.Style.Style, item.Style.Language.Length > 0 ? item.Style.Language : language, item.Style.Named) : item.Font;

    /// <summary>
    /// A Word part of symbols only (☐ ☑ ● ■, Unicode category So): mostly a symbol font's character read as Unicode
    /// (☑ for Wingdings 2's F052) and drawn in another font, often a taller one. Word measured the symbol font, no taller
    /// than the text around it, so such a part counts only on a line without other text.
    /// </summary>
    private bool Symbols(Part part, string text)
    {
        if (!WordLineHeights || part.Length == 0)
            return false;
        for (var i = part.Start; i < part.Start + part.Length; i++)
        {
            if (char.IsLowSurrogate(text[i]) || text[i] == ' ')
                continue;
            if (CharUnicodeInfo.GetUnicodeCategory(text, i) != UnicodeCategory.OtherSymbol)
                return false;
        }
        return true;
    }

    /// <summary>
    /// The room Word's document grid adds to a line's text of <paramref name="single"/> height, above and below it: the
    /// text takes whole grid lines (at least one); 0 without a grid.
    /// </summary>
    private static float GridRoom(HLineHeight rule, float single)
    {
        if (rule is not { Rule: HLineHeightRule.Multiple, GridPitch: > 0 })
            return 0;
        var pitch = rule.GridPitch / 100f;
        var lines = Math.Max(1, MathF.Ceiling(single / pitch - GridTolerance));
        return Math.Max(0, lines * pitch - single) / 2;
    }
}
