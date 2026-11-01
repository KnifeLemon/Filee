// The layout engine's font resolver (Typesetting/FontResolver.cs) against a fake list of installed fonts, like
// RhwpFontsTests: other names (함초롬바탕 → HCR Batang), style matching, variable and CFF2 fonts, replacements for
// missing fonts and per-cluster fallback. One test looks at the real installed fonts and skips without Malgun Gothic.

using Filee.Engines.Typesetting;

namespace Filee.Engines.Tests;

public class FontResolverTests
{
    private const string Latin = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 .,;:!?'\"()-“”‘’…";
    private const string Hangul = "한글문서대한민국섞인장입니다";
    private const string Han = "改造我们的学习这是一个测试文档说话国家和会时间漢字辻";
    private const string Kana = "日本語のテキストあいうえおアイウエオ";
    private const string Cyrillic = "АБВГДЕЖЗИКЛМНОПРСТУФХЦЧШЩЭЮЯабвгдежзиклмнопрстуфхцчшщэюя";
    private const string Arabic = "ابتثجحخدذرزسشصضطظعغفقكلمنهوي،";

    /// <summary>A fake computer: families by their English names only, so Korean names must be mapped.</summary>
    private static FakeFonts Computer() => new(
        Face("Malgun Gothic", Latin + Hangul + "漢字國"),
        Face("Malgun Gothic", Latin + Hangul + "漢字國", weight: 700),
        Face("HCR Batang", Latin + Hangul + "漢字國"),
        Face("Batang", Latin + Hangul + "漢字國"),
        Face("HYSinMyeongJo-Medium", Latin + Hangul),
        Face("Microsoft YaHei", Latin + Cyrillic + Han),
        Face("SimSun", Latin + Han),
        Face("Yu Gothic", Latin + Han + Kana),
        Face("Segoe UI", Latin + Cyrillic + Arabic + "\u0301"),
        Face("Times New Roman", Latin + Cyrillic + Arabic),
        Face("Liberation Sans", Latin),
        Face("Consolas", Latin),
        Face("思源宋体 VF", Latin + Han, cff2: true, variable: true),
        Face("Noto Sans KR", Latin + Hangul, variable: true),
        Face("Pretendard", Latin + Hangul, weight: 400),
        Face("Pretendard", Latin + Hangul, weight: 500),
        Face("Pretendard", Latin + Hangul, weight: 700),
        Face("Pretendard", Latin + Hangul, weight: 300, italic: true));

    private static FontFace Face(string family, string chars, int weight = 400, bool italic = false, bool cff2 = false, bool variable = false) =>
        new(family, weight, italic, c => c <= 0xFFFF && chars.Contains((char)c)) { Cff2 = cff2, Variable = variable };

    private static string? Family(ResolvedFont? font) => font?.Family;

    [Theory]
    [InlineData("함초롬바탕", "HCR Batang")]
    [InlineData("맑은 고딕", "Malgun Gothic")]
    [InlineData("바탕", "Batang")]
    [InlineData("한양신명조", "HYSinMyeongJo-Medium")] // 한글's built-in font → the TrueType font of the same design
    [InlineData("HY신명조", "HYSinMyeongJo-Medium")]
    [InlineData("Arial", "Liberation Sans")] // metric-compatible
    [InlineData("malgun gothic", "Malgun Gothic")]
    [InlineData("Not Installed", null)]
    public void Fonts_are_found_by_their_other_names(string name, string? family)
    {
        Assert.Equal(family, Family(new FontResolver(Computer()).Find(name, FontStyle.Regular)));
    }

    [Theory]
    [InlineData("Menlo")]
    [InlineData("Source Code Pro")]
    [InlineData("JetBrains Mono")]
    [InlineData("GulimChe")]
    [InlineData("monospace")]
    public void A_missing_monospaced_font_or_the_generic_family_takes_an_installed_monospaced_one(string name)
    {
        Assert.Equal("Consolas", Family(new FontResolver(Computer()).Resolve(name, FontStyle.Regular)));
        Assert.NotEqual("Consolas", Family(new FontResolver(Computer()).Resolve("Not Installed", FontStyle.Regular)));
    }

    [Fact]
    public void The_nearest_style_is_chosen_and_what_is_missing_is_synthesized()
    {
        var resolver = new FontResolver(Computer());

        var bold = resolver.Find("맑은 고딕", FontStyle.From(bold: true, italic: false))!;
        Assert.Equal(700, bold.Face.Weight);
        Assert.False(bold.SyntheticBold);

        var batang = resolver.Find("바탕", FontStyle.From(bold: true, italic: true))!;
        Assert.Equal(400, batang.Face.Weight);
        Assert.True(batang.SyntheticBold);
        Assert.True(batang.SyntheticItalic);

        // CSS weight matching: 400 prefers up to 500 first; 600 prefers heavier; 200 takes the lightest above it.
        FontFace[] faces = [Face("X", "", 300), Face("X", "", 500), Face("X", "", 700)];
        Assert.Equal(500, FontResolver.Match(faces, new FontStyle(400)).Face.Weight);
        Assert.Equal(700, FontResolver.Match(faces, new FontStyle(600)).Face.Weight);
        Assert.Equal(300, FontResolver.Match(faces, new FontStyle(350)).Face.Weight);
        Assert.Equal(300, FontResolver.Match(faces, new FontStyle(200)).Face.Weight);
        Assert.Equal(500, FontResolver.Match([Face("X", "", 300), Face("X", "", 500)], new FontStyle(800)).Face.Weight);
    }

    [Fact]
    public void Style_words_in_a_name_set_the_weight()
    {
        var resolver = new FontResolver(Computer());

        Assert.Equal(500, resolver.Find("Pretendard Medium", FontStyle.Regular)!.Face.Weight);
        Assert.Equal(700, resolver.Find("Pretendard Medium", FontStyle.From(bold: true, italic: false))!.Face.Weight);
        // Italic is matched before weight: the only italic face, though lighter.
        var italic = resolver.Find("Pretendard", FontStyle.From(bold: false, italic: true))!;
        Assert.Equal(300, italic.Face.Weight);
        Assert.False(italic.SyntheticItalic);
    }

    [Fact]
    public void A_variable_font_is_its_default_instance()
    {
        var bold = new FontResolver(Computer()).Find("Noto Sans KR", FontStyle.From(bold: true, italic: false))!;

        Assert.True(bold.Face.Variable);
        Assert.True(bold.SyntheticBold); // no instancer: the default instance, emboldened
    }

    [Fact]
    public void A_cff2_font_is_used_only_without_another_font_for_its_script()
    {
        Assert.Equal("SimSun", Family(new FontResolver(Computer()).Resolve("思源宋体 VF Medium", FontStyle.Regular)));

        var only = new FontResolver(new FakeFonts(Face("思源宋体 VF", Latin + Han, cff2: true)));
        Assert.Equal("思源宋体 VF", Family(only.Resolve("思源宋体 VF Medium", FontStyle.Regular)));
    }

    [Theory]
    [InlineData("함초롬돋움", null, "Malgun Gothic")] // missing Korean sans → the first installed Korean sans
    [InlineData("휴먼명조", null, "Batang")] // missing Korean serif (명조) → Korean serif
    [InlineData("Unknown Sans", "ja", "Yu Gothic")]
    [InlineData("Unknown Serif", "zh-Hans", "SimSun")]
    [InlineData("Unknown", "", "Segoe UI")]
    [InlineData("serif", "zh-Hans", "SimSun")]
    [InlineData("sans-serif", "ko", "Malgun Gothic")]
    [InlineData("monospace", "", "Consolas")]
    [InlineData(null, "ja", "Yu Gothic")]
    public void Missing_and_generic_fonts_get_one_for_the_script_and_style(string? name, string? language, string family)
    {
        Assert.Equal(family, Family(new FontResolver(Computer()).Resolve(name, FontStyle.Regular, language)));
    }

    [Fact]
    public void The_document_plan_follows_the_rhwp_rules()
    {
        // Issue #64: Korean default fonts in a Chinese document.
        var plan = new FontResolver(Computer()).Plan([("HANJA", "맑은 고딕"), ("HANJA", "함초롬바탕")], "改造我们的学习 这是一个测试文档。");

        Assert.Equal("Microsoft YaHei", plan[("HANJA", "맑은 고딕")]);
        Assert.Equal("SimSun", plan[("HANJA", "함초롬바탕")]);
    }

    [Fact]
    public void Clusters_the_font_lacks_go_to_a_font_for_their_script()
    {
        var resolver = new FontResolver(Computer());
        var primary = resolver.Find("맑은 고딕", FontStyle.Regular)!;
        const string text = "한글 مرحبا، abc";

        var runs = resolver.Fallback(text, 0, text.Length, primary, FontStyle.Regular);

        // The Arabic word goes to Segoe UI with the punctuation and space after it; the Latin returns to the primary.
        Assert.Equal([("한글 ", "Malgun Gothic"), ("مرحبا، ", "Segoe UI"), ("abc", "Malgun Gothic")], Describe(text, runs));
    }

    [Fact]
    public void A_mark_stays_with_its_base_in_a_font_that_has_both()
    {
        var resolver = new FontResolver(Computer());
        var primary = resolver.Find("Malgun Gothic", FontStyle.Regular)!;
        const string text = "cafe\u0301";

        var runs = resolver.Fallback(text, 0, text.Length, primary, FontStyle.Regular);

        Assert.Equal([("caf", "Malgun Gothic"), ("e\u0301", "Segoe UI")], Describe(text, runs));
    }

    [Fact]
    public void Han_characters_take_the_font_of_the_text_s_language()
    {
        var resolver = new FontResolver(Computer());
        var primary = resolver.Find("Malgun Gothic", FontStyle.Regular)!;

        const string japanese = "日本語のテキスト辻";
        Assert.All(resolver.Fallback(japanese, 0, japanese.Length, primary, FontStyle.Regular), r => Assert.Equal("Yu Gothic", r.Font.Family));
        const string chinese = "这是一个测试文档辻";
        Assert.All(resolver.Fallback(chinese, 0, chinese.Length, primary, FontStyle.Regular), r => Assert.Equal("Microsoft YaHei", r.Font.Family));
        // A part of the paragraph: the language still comes from the whole text.
        Assert.Equal("Yu Gothic", Assert.Single(resolver.Fallback(japanese, 8, 1, primary, FontStyle.Regular)).Font.Family);
    }

    [Fact]
    public void The_platform_fallback_comes_last_and_the_primary_font_when_nothing_has_the_character()
    {
        var fonts = Computer();
        fonts.Fallbacks.Add(Face("Noto Sans Symbols 2", "♔"));
        var resolver = new FontResolver(fonts);
        var primary = resolver.Find("Malgun Gothic", FontStyle.Regular)!;
        const string text = "a♔b\U0001F9FF";

        var runs = resolver.Fallback(text, 0, text.Length, primary, FontStyle.Regular);

        Assert.Equal([("a", "Malgun Gothic"), ("♔", "Noto Sans Symbols 2"), ("b\U0001F9FF", "Malgun Gothic")], Describe(text, runs));
    }

    [Fact]
    public void Installed_fonts_are_found_by_korean_and_english_names()
    {
        var resolver = new FontResolver();
        var malgun = resolver.Find("Malgun Gothic", FontStyle.Regular);
        Assert.SkipUnless(malgun is not null, "Malgun Gothic is not installed");

        var korean = resolver.Find("맑은 고딕", FontStyle.From(bold: true, italic: false))!;
        Assert.True(korean.Face.Weight >= 600);
        Assert.False(korean.SyntheticBold);
        Assert.True(malgun.Face.Has('한'));
        Assert.Equal(0, malgun.Font.FaceIndex);
        Assert.True(malgun.Font.Data.Length > 1_000_000);
        Assert.Null(resolver.Find("Filee No Such Family", FontStyle.Regular));
    }

    private static List<(string Text, string Family)> Describe(string text, List<FontRun> runs) =>
        runs.Select(r => (text.Substring(r.Start, r.Length), r.Font.Family)).ToList();

    private sealed class FakeFonts(params FontFace[] faces) : IFontCollection
    {
        /// <summary>What the platform's own fallback answers with.</summary>
        public List<FontFace> Fallbacks { get; } = [];

        public IReadOnlyList<FontFace> Family(string name) =>
            faces.Where(f => string.Equals(f.Family, name, StringComparison.OrdinalIgnoreCase)).ToList();

        public FontFace? ForCharacter(int codePoint, FontStyle style, string? language) => Fallbacks.FirstOrDefault(f => f.Has(codePoint));
    }
}
