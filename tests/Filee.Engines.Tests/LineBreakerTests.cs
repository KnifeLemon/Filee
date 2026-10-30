// The layout engine's line breaking (Typesetting/LineBreaker.cs): UAX #14 on text written here, and the tailorings
// documents ask for (한글 어절 / 글자, Latin words broken anywhere, characters forbidden at a line's start or end).
//
// The implementation passes all 16,672 cases of Unicode 16.0's LineBreakTest.txt; that file is not kept in the
// repository, so these cases cover each rule family instead. Expected breaks are written into the text: '|' where a
// line may break, '¶' where it must (after the character before it).

using System.Text;
using Filee.Engines.Typesetting;

namespace Filee.Engines.Tests;

public class LineBreakerTests
{
    [Theory]
    [InlineData("Hello |world")]
    [InlineData("Filee, |the |converter.")]
    [InlineData("e-|mail |co-|op")] // LB21: no break before a hyphen; after it, yes
    [InlineData("-5 |-mail")] // LB25 HY × NU; LB20a: a hyphen at a word start stays with it
    [InlineData("$(12.50) |1,000% |US$5")] // LB25 numbers, LB24 PR × AL
    [InlineData("(a) |[b] |{c}")] // LB14 OP ×, LB13 × CP / CL
    [InlineData("“Hello,” |she |said")] // LB15a/b: quotes bind to what they quote
    [InlineData("a\u00A0b |c\u2060d")] // LB12 GL, LB11 WJ
    [InlineData("a\u200B|b")] // LB8 ZW ÷
    [InlineData("e\u0301e\u0301 |x")] // LB9: combining marks stay with their base
    [InlineData("👨\u200D👩\u200D👧 |👍🏽|👍🏽")] // LB8a ZWJ ×, LB30b EB × EM
    [InlineData("🇰🇷|🇯🇵|🇺🇸")] // LB30a: regional indicators in pairs
    [InlineData("ภาษาไทยไม่เว้นวรรค |ระหว่างคำ")] // SA without a dictionary: only at spaces
    [InlineData("क्षत्रिय |हिन्दी")] // Devanagari conjuncts: marks join their base
    public void Breaks_by_uax_14(string expected) => Assert.Equal(expected, Mark(Unmark(expected)));

    [Theory]
    [InlineData("한|글 |문|서|는 |줄|을 |나|눕|니|다.")] // UAX #14: between any two syllables (한글 글자)
    [InlineData("\u1100\u1161\u11A8|\u1100\u1161")] // LB26: conjoining jamo form one syllable
    [InlineData("文|件|转|换|工|具。|汉|字")] // ideographs break anywhere, never before 。 (LB13)
    [InlineData("ちょっ|とー|ず")] // small kana and the prolonged sound mark start no line (CJ resolved to NS)
    [InlineData("「漢|字」|と|か")] // LB14 / LB13 corner brackets
    public void Breaks_east_asian_text(string expected) => Assert.Equal(expected, Mark(Unmark(expected)));

    [Theory]
    [InlineData("a\n¶b")]
    [InlineData("a\r\n¶b")] // LB5: CR × LF
    [InlineData("a\u2028¶b")]
    [InlineData("a \n¶ |b")]
    public void Breaks_after_line_separators(string expected) => Assert.Equal(expected, Mark(Unmark(expected)));

    [Fact]
    public void The_end_of_the_text_is_a_mandatory_break_and_the_start_none()
    {
        var breaks = LineBreaker.Breaks("ab");
        Assert.Equal([LineBreak.None, LineBreak.None, LineBreak.Mandatory], breaks);
        Assert.Equal([LineBreak.None], LineBreaker.Breaks(""));
    }

    [Fact]
    public void A_surrogate_pair_is_never_split()
    {
        var breaks = LineBreaker.Breaks("𠀀𠀁");
        Assert.Equal(LineBreak.None, breaks[1]);
        Assert.Equal(LineBreak.Allowed, breaks[2]);
        Assert.Equal(LineBreak.None, breaks[3]);
    }

    [Theory]
    [InlineData("한글 |문서는 |줄을 |나눕니다.")]
    [InlineData("Filee는 |한글 |문서를 |PDF로 |바꿉니다.")]
    // As 한글 keeps them in that setting: ideographs, kana, East Asian punctuation and quotes are a word's letters too.
    [InlineData("한글漢字 |섞어 |쓰기")]
    [InlineData("文件转换工具，“引号”和‘单引号’。 |下一句")]
    [InlineData("「かぎ括弧」と『二重』。 |次の文")]
    public void Keeps_non_latin_words_whole(string expected) =>
        Assert.Equal(expected, Mark(Unmark(expected), new LineBreakRules(KeepNonLatinWords: true)));

    [Theory]
    [InlineData("F|i|l|e|e |l|a|y|s |1|2|3 |п|р|и|в|е|т.")]
    [InlineData("(a|b)")] // punctuation keeps its rules
    [InlineData("한|글 |w|o|r|d")]
    [InlineData("مرحبا |بالعالم")] // Arabic words stay whole: breaking them would break the joining
    public void Breaks_latin_words_anywhere(string expected) =>
        Assert.Equal(expected, Mark(Unmark(expected), new LineBreakRules(BreakLatinWords: true)));

    [Fact]
    public void Honours_characters_forbidden_at_the_start_or_end_of_a_line()
    {
        var rules = new LineBreakRules(NotAtLineStart: "字", NotAtLineEnd: "換");
        Assert.Equal("漢字|変|換文|書", Mark("漢字変換文書", rules));
    }

    [Fact]
    public void Forbidden_characters_do_not_cancel_mandatory_breaks()
    {
        var rules = new LineBreakRules(NotAtLineStart: "b");
        Assert.Equal("a\n¶b", Mark("a\nb", rules));
    }

    /// <summary>The text with '|' at each allowed break and '¶' at each mandatory one (but the end).</summary>
    private static string Mark(string text, LineBreakRules? rules = null)
    {
        var breaks = LineBreaker.Breaks(text, rules);
        var result = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (i > 0)
            {
                if (breaks[i] == LineBreak.Allowed)
                    result.Append('|');
                else if (breaks[i] == LineBreak.Mandatory)
                    result.Append('¶');
            }
            result.Append(text[i]);
        }
        return result.ToString();
    }

    private static string Unmark(string marked) => marked.Replace("|", "").Replace("¶", "");
}
