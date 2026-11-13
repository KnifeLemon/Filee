// Document-wide font replacements, shared by the layout engine's FontResolver and by RhwpFonts (which writes them into
// the HWPX copy rhwp draws). The rules came from RhwpFonts:
//
// - Language: documents Filee writes (Markdown, text, HTML → HWPX) name Korean fonts in every slot. In a Chinese or
//   Japanese document those draw Korean-style Hanja and, for characters they lack, a different fallback font per
//   character (issue #64). Without Hangul in the text, Korean fonts are swapped for an installed Chinese (Simplified
//   or Traditional) or Japanese font of the same style.
// - Coverage: a slot's font draws a character when it has it, else a fallback does (Arabic came out in a calligraphic
//   font). In text without Hangul, Han or kana, a slot's font is swapped for an installed one of the same style that
//   has every character of the text the old one had, and more of the rest: Russian, Greek, Thai or Arabic text gets
//   one consistent font.
// - Unusable fonts: a font the consumer can't use (rhwp: CFF2 variable fonts, issue #61, whose pages came out blank)
//   is swapped for one of its script and style when one is installed.

using System.Globalization;
using System.Text;

namespace Filee.Engines.Typesetting;

/// <summary>What the substitution rules need to know about an installed font.</summary>
/// <param name="Usable">False when the consumer can't use the font (rhwp: CFF2 outlines) and wants another.</param>
/// <param name="Has">Whether the font has a glyph for a code point.</param>
internal record FontCoverage(bool Usable, Func<int, bool> Has);

/// <summary>Language and coverage rules for replacing fonts, and what a font's name says about it.</summary>
internal static class FontSubstitution
{
    /// <summary>Style words a family name may end with ("思源宋体 VF Medium"), with the weight they stand for.</summary>
    internal static readonly IReadOnlyDictionary<string, int> StyleWords = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["Thin"] = 100,
        ["ExtraLight"] = 200,
        ["Light"] = 300,
        ["Normal"] = 400,
        ["Regular"] = 400,
        ["Medium"] = 500,
        ["SemiBold"] = 600,
        ["Bold"] = 700,
        ["ExtraBold"] = 800,
        ["Heavy"] = 900,
        ["Black"] = 900,
    };

    /// <summary>Installed fonts in order of preference, by script, serif first then sans.</summary>
    private static readonly Dictionary<string, (string[] Serif, string[] Sans)> Candidates = new()
    {
        ["zh-Hans"] = (["SimSun", "NSimSun", "Noto Serif SC", "Songti SC", "Noto Serif CJK SC"],
            ["Microsoft YaHei", "DengXian", "Noto Sans SC", "PingFang SC", "Noto Sans CJK SC", "SimHei"]),
        ["zh-Hant"] = (["PMingLiU", "MingLiU", "Noto Serif TC", "Songti TC", "Noto Serif CJK TC"],
            ["Microsoft JhengHei", "Noto Sans TC", "PingFang TC", "Noto Sans CJK TC"]),
        ["ja"] = (["Yu Mincho", "MS Mincho", "Noto Serif JP", "Hiragino Mincho ProN", "Noto Serif CJK JP"],
            ["Yu Gothic", "Meiryo", "MS Gothic", "Noto Sans JP", "Hiragino Sans", "Noto Sans CJK JP"]),
        ["ko"] = (["Batang", "HCR Batang", "Noto Serif KR", "AppleMyungjo", "Noto Serif CJK KR"],
            ["Malgun Gothic", "Noto Sans KR", "Apple SD Gothic Neo", "NanumGothic", "Noto Sans CJK KR"]),
        // Wide coverage first (Tahoma and Times New Roman also have Greek, Cyrillic, Arabic, Hebrew; Tahoma Thai).
        [""] = (["Times New Roman", "Noto Serif", "DejaVu Serif", "Georgia", "Liberation Serif"],
            ["Segoe UI", "Tahoma", "Arial", "Noto Sans", "DejaVu Sans", "Helvetica", "Liberation Sans"]),
    };

    /// <summary>Fonts for scripts a renderer's own fallback draws, which may have better ones installed.</summary>
    private static readonly string[] WideFonts = ["Leelawadee UI", "Nirmala UI", "Microsoft Sans Serif", "Arial Unicode MS"];

    /// <summary>
    /// Simplified and Traditional forms of common characters: which form a Chinese text uses. Forms Japanese writes
    /// the same way (国, 学, 会, 点, …) are left out.
    /// </summary>
    private const string SimplifiedOnly = "这们说为时个对发经过还进动东车长门间问见现实从开关书无让应该话样";
    private const string TraditionalOnly = "這們說為時個對發經過還進動東車長門間問見現實從開關書無讓應該話樣";

    /// <summary>Replacements by (slot, face): the language, coverage and unusable-font rules above.</summary>
    /// <param name="slots">The fonts a document names, with the slot (language) each is used for.</param>
    /// <param name="text">The document's text.</param>
    /// <param name="lookup">An installed font by name, or null when it is not installed.</param>
    /// <param name="measuredLatin">
    /// Latin fonts the consumer measures itself (rhwp's width tables): a Korean font in the LATIN slot of a Chinese or
    /// Japanese text becomes the first installed one of them in its style before the language font (issue #1207).
    /// </param>
    public static Dictionary<(string Slot, string Face), string> Plan(
        IReadOnlyList<(string Slot, string Face)> slots, string text, Func<string, FontCoverage?> lookup,
        IReadOnlySet<string>? measuredLatin = null)
    {
        var characters = Characters(text);
        var language = Language(text);
        var result = new Dictionary<(string, string), string>();
        foreach (var (slot, face) in slots.Distinct())
        {
            if (face.Length == 0)
                continue;
            var serif = IsSerif(face);
            var chosen = face;
            var current = lookup(face);

            // Korean fonts in a Chinese or Japanese text; a font that can't be used.
            var korean = language is not ("ko" or "") && Script(face) == "ko";
            if (korean || current is { Usable: false })
            {
                var script = current is { Usable: false } && Script(face) is { Length: > 0 } own ? own : language;
                var latin = korean && slot == "LATIN" && measuredLatin is not null
                    ? (serif ? Candidates[""].Serif : Candidates[""].Sans).Where(measuredLatin.Contains)
                    : [];
                if (latin.Concat(Preferred(script, serif)).FirstOrDefault(name => lookup(name) is { Usable: true }) is { } replacement)
                    chosen = replacement;
            }

            // Text without Hangul, Han or kana (Russian, Greek, Thai, Arabic, …): a font of the same style with every
            // character the chosen one has, and more of the others. In Korean, Chinese and Japanese text the language
            // font stays, and the fallback draws the odd foreign word as before.
            var mine = language != "" || slot is "HANJA" or "JAPANESE"
                ? []
                : characters.Where(c => !IsHan(c) && !IsKana(c) && (slot == "HANGUL" || !IsHangul(c))).ToList();
            if (mine.Count > 0 && lookup(chosen) is { Usable: true } font)
            {
                var covered = mine.Where(c => font.Has(c)).ToList();
                if (covered.Count < mine.Count)
                {
                    var best = chosen;
                    var bestCount = covered.Count;
                    var sameStyle = (serif ? Styled(language).Serif.Concat(Styled("").Serif) : Styled(language).Sans.Concat(Styled("").Sans).Concat(WideFonts))
                        .Distinct(StringComparer.OrdinalIgnoreCase);
                    foreach (var name in sameStyle)
                    {
                        if (lookup(name) is not { Usable: true } other || !covered.All(other.Has))
                            continue;
                        var count = mine.Count(other.Has);
                        if (count == mine.Count)
                        {
                            (best, bestCount) = (name, count); // the first one in order that has everything
                            break;
                        }
                        if (count > bestCount)
                            (best, bestCount) = (name, count);
                    }
                    chosen = best;
                }
            }

            if (!string.Equals(chosen, face, StringComparison.Ordinal))
                result[(slot, face)] = chosen;
        }
        return result;
    }

    /// <summary>
    /// Installed-font candidates for a language ("ko", "ja", "zh-Hans", "zh-Hant", "" for other text) in order of
    /// preference: the requested style (serif or sans) first, then the other, then wide-coverage fonts.
    /// </summary>
    public static IEnumerable<string> Preferred(string script, bool serif)
    {
        var (serifs, sans) = Styled(script);
        return serif ? serifs.Concat(sans) : sans.Concat(serifs);
    }

    /// <summary>Fonts with wide script coverage (Thai, Indic, ...) to try after the language's own.</summary>
    public static IReadOnlyList<string> Wide => WideFonts;

    private static (string[] Serif, string[] Sans) Styled(string script) =>
        Candidates.TryGetValue(script, out var c) ? c : Candidates[""];

    private static bool IsHangul(int c) => c is >= 0xAC00 and <= 0xD7A3 or >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F;

    private static bool IsKana(int c) => c is >= 0x3040 and <= 0x30FF;

    private static bool IsHan(int c) => c is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF or >= 0x20000 and <= 0x2FFFF;

    /// <summary>
    /// The language the Han characters of a text are in: Korean with Hangul, Japanese with kana, else Simplified or
    /// Traditional Chinese by common characters; "" without Han characters.
    /// </summary>
    public static string Language(string text)
    {
        int hangul = 0, kana = 0, han = 0, simplified = 0, traditional = 0;
        foreach (var c in text)
        {
            if (c is >= '가' and <= '힣' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏')
                hangul++;
            else if (c is >= '぀' and <= 'ヿ')
                kana++;
            else if (c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿')
            {
                han++;
                if (SimplifiedOnly.Contains(c))
                    simplified++;
                else if (TraditionalOnly.Contains(c))
                    traditional++;
            }
        }
        if (hangul > 0)
            return "ko";
        // Japanese prose is mostly kana; a Chinese text quoting a Japanese title is not.
        if (kana > 0 && (simplified + traditional == 0 || kana * 3 >= han))
            return "ja";
        if (han > 0)
            return traditional > simplified ? "zh-Hant" : "zh-Hans";
        return "";
    }

    /// <summary>Distinct characters of the text that need a glyph (no spaces or controls), at most 4000.</summary>
    private static List<int> Characters(string text)
    {
        var set = new HashSet<int>();
        foreach (var rune in text.EnumerateRunes())
        {
            if (set.Count >= 4000)
                break;
            if (NeedsGlyph(rune.Value))
                set.Add(rune.Value);
        }
        return set.ToList();
    }

    /// <summary>
    /// False for characters no font needs to have: spaces, controls, format characters (joiners, direction marks),
    /// combining marks drawn with their base, private use and unassigned code points.
    /// </summary>
    public static bool NeedsGlyph(int codePoint) =>
        Rune.IsValid(codePoint) && Rune.GetUnicodeCategory(new Rune(codePoint)) is not (UnicodeCategory.SpaceSeparator
            or UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator or UnicodeCategory.NonSpacingMark or UnicodeCategory.PrivateUse
            or UnicodeCategory.OtherNotAssigned);

    /// <summary>
    /// The names to look a font up by: the name itself, then without trailing style words ("思源宋体 VF Medium" names a
    /// style of "思源宋体 VF"), each with the weight the dropped word stands for.
    /// </summary>
    public static IEnumerable<(string Name, int? Weight)> Names(string face)
    {
        yield return (face, null);
        var words = face.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        int? weight = null;
        while (words.Count > 1 && StyleWords.TryGetValue(words[^1], out var stands))
        {
            weight ??= stands;
            words.RemoveAt(words.Count - 1);
            yield return (string.Join(' ', words), weight);
        }
    }

    /// <summary>The script a font name points to: "思源宋体" and "SC" are Simplified Chinese, "명조" Korean, ….</summary>
    public static string Script(string face)
    {
        bool Has(params string[] parts) => parts.Any(p => face.Contains(p, StringComparison.OrdinalIgnoreCase));
        bool Word(params string[] words) => face.Split(' ', '-', '_').Any(w => words.Contains(w, StringComparer.OrdinalIgnoreCase));
        if (Word("SC", "CN", "GB") || Has("简", "宋体", "黑体", "仿宋", "楷体"))
            return "zh-Hans";
        if (Word("TC", "HK", "TW") || Has("繁", "明體", "黑體", "宋體"))
            return "zh-Hant";
        if (Word("JP", "J") || Has("明朝", "ゴシック", "游"))
            return "ja";
        if (Word("KR", "K") || face.Any(c => c is >= '가' and <= '힣')
            || Candidates["ko"].Serif.Concat(Candidates["ko"].Sans).Contains(face, StringComparer.OrdinalIgnoreCase))
            return "ko";
        if (face.Any(c => c is >= '一' and <= '鿿'))
            return "zh-Hans";
        return "";
    }

    /// <summary>Serif (Song/Ming/Mincho/Batang) rather than sans (Hei/Gothic/Dotum).</summary>
    public static bool IsSerif(string face) =>
        new[] { "Serif", "Song", "Ming", "Mincho", "Myeongjo", "Myungjo", "Batang", "Times", "宋", "明", "바탕", "명조", "仿宋", "楷" }
            .Any(p => face.Contains(p, StringComparison.OrdinalIgnoreCase));
}
