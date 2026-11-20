// The height of Word's lines (HDocument.WordLineHeights: models read from DOCX and DOC).
//
// - A font's line is its Win ascent and descent (OS/2 usWinAscent, usWinDescent), as before.
// - An East Asian font (one for a Chinese, Japanese or Korean code page, OS/2 ulCodePageRange1) gets 30% more, half
//   above the text and half below: a single line of 12 pt SimSun is 15.6 pt in Word, not 12, its text 1.8 pt below the
//   line's top (#356: 23.4 pt at 1.5 lines). Microsoft YaHei and Malgun Gothic, whose Win metrics are 1.33 em, get
//   1.73 em.
// - A named East Asian font that isn't installed keeps the height Word gives it: the font drawing in its place (Noto Sans
//   CJK, 1.45 em) is scaled to the named font's Win height.
// - A math font (Cambria Math, with a MATH table) has Win metrics that make room for big operators (5.6 em); Word gives
//   its text, and a paragraph mark in it, the typographic height (1.17 em).
// - A character another font draws because the named one lacks it counts with the named font's height, as Word
//   measures the fonts the runs name. Symbols (☑ read from Wingdings 2's F052, drawn in another font) count only on a
//   line without other text.
// - Word's document grid (HLineHeight.GridPitch, set for the body's paragraphs that snap to it): a line's text takes
//   whole grid lines, the text in their middle; the multiple is of that height.
//
// Choices (not measured in Word): the grid's extra room goes half above and half below like the East Asian leading;
// lines in headers, footers, notes and text boxes don't snap (DocxReader.SnapToGrid).

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

    /// <summary>Lines take the height Word gives their fonts (HDocument.WordLineHeights).</summary>
    public bool WordLineHeights { get; set; }

    /// <summary>Ascent and descent of a style's line in Word (see the notes at the top).</summary>
    private static (float Ascent, float Descent) WordExtent(TextStyle style, ResolvedFont font, FontMetrics metrics)
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
            return (ascent, descent);
        var leading = (ascent + descent) * EastAsianLeading / 2;
        return (ascent + leading, descent + leading);
    }

    /// <summary>
    /// The font a text item's height is measured with: in Word the font its run names, also where another font draws
    /// characters that one lacks.
    /// </summary>
    private ResolvedFont Measured(Item item, string language) =>
        WordLineHeights ? Font(item.Style.Family, item.Style.Style, item.Style.Language.Length > 0 ? item.Style.Language : language) : item.Font;

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
