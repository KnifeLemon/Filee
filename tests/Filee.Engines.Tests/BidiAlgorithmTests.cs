// The Unicode Bidirectional Algorithm of the layout engine (Typesetting/BidiAlgorithm.cs). It passes every case of
// Unicode 16's BidiTest.txt and BidiCharacterTest.txt; the cases here are the rules a document's text runs into most:
// numbers, brackets, isolates and embeddings, and the paragraph's own direction. Levels are written one digit per
// character, Hebrew letters standing for right-to-left text.

using Filee.Engines.Typesetting;

namespace Filee.Engines.Tests;

public sealed class BidiAlgorithmTests
{
    [Theory]
    // Right-to-left letters in a left-to-right paragraph, and the spaces between two of them (N1).
    [InlineData("ab אב cd", false, "00011000")]
    [InlineData("ab אב גד cd", false, "00011111000")]
    // European numbers after right-to-left letters keep their order (W7 doesn't make them L; I1 raises them to 2).
    [InlineData("אב 12", true, "11122")]
    // A number with its separators and terminators stays one number (W4, W5).
    [InlineData("אב 1,000.5% ג", true, "1112222222211")]
    // Arabic letters make the numbers after them Arabic numbers (W2).
    [InlineData("عدد 123", null, "1111222")]
    // Paired brackets take the direction of what they hold when it is the paragraph's (N0 b), so "(cd)" holding Latin
    // in a Latin paragraph stays with it, and only the letters inside a right-to-left one are raised.
    [InlineData("ab(cd)גד", false, "00000011")]
    [InlineData("אב(cd)גד", true, "11122111")]
    // Brackets holding only the other direction take it when the text before them has it too (N0 c.1), else the
    // paragraph's (N0 c.2).
    [InlineData("אב cd(ef)", true, "111222222")]
    [InlineData("אב (גד) ef", false, "1111111000")]
    [InlineData("ab (גד) ef", false, "0000110000")]
    // An isolate is laid out on its own, and the text around it doesn't see it (X5a–X6a, N1).
    [InlineData("ab \u2067גד\u2069 ef", false, "0000110000")]
    // An embedding raises its text by two levels at most (X2–X5); the controls take the level before them.
    [InlineData("אב \u202Acd\u202C גד", true, "1111222111")]
    // Trailing white space and tabs fall back to the paragraph's level (L1).
    [InlineData("אב\tcd  ", false, "1100000")]
    public void Resolves_levels_by_uax_9(string text, bool? rightToLeft, string expected)
    {
        var levels = BidiAlgorithm.Levels(text, rightToLeft);
        Assert.Equal(expected, string.Concat(levels));
    }

    [Theory]
    [InlineData("abc אבג", false)]
    [InlineData("אבג abc", true)]
    [InlineData("123 אבג", true)] // numbers aren't strong
    [InlineData("\u2067אבג\u2069 abc", false)] // isolated text doesn't count (P2)
    [InlineData("12 ...", false)] // nothing strong: left to right
    public void A_paragraph_takes_the_direction_of_its_first_strong_character(string text, bool rightToLeft) =>
        Assert.Equal(rightToLeft, BidiAlgorithm.IsRightToLeft(text));

    [Fact]
    public void Surrogate_pairs_take_one_level_for_both_halves()
    {
        // U+10900 PHOENICIAN LETTER ALF is right to left.
        var levels = BidiAlgorithm.Levels("a \U00010900 b", false);
        Assert.Equal("001100", string.Concat(levels));
    }

    [Fact]
    public void Deep_nesting_stops_at_the_deepest_level_without_failing()
    {
        var text = string.Concat(Enumerable.Repeat("\u202B", 200)) + "x" + string.Concat(Enumerable.Repeat("\u202C", 200));
        Assert.All(BidiAlgorithm.Levels(text, false), level => Assert.InRange(level, 0, 126));
    }
}
