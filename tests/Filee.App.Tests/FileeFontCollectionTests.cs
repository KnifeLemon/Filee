// The embedded fonts are read family by family when text asks (issue #957), and which families were read before never
// changes the font a letter ends up in: SoftPalette's chain decides (#1095).

using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Platform;
using Filee.App.Services;

namespace Filee.App.Tests;

public class FileeFontCollectionTests
{
    private const string Key = "fonts:FileeOnDemand";

    private static readonly string[] Families =
        ["Noto Sans", "Noto Sans Arabic", "Noto Sans Devanagari", "Noto Sans Thai", "Noto Sans JP", "Noto Sans KR", "Noto Sans SC"];

    private static void RequireFonts()
    {
        TestServices.EnsureInitialized("en");
        Assert.SkipUnless(AssetLoader.GetAssets(FileeFontCollection.Source, null).Any(), "Requires build/fetch-fonts.ps1.");
    }

    /// <summary>A new collection in the font manager (it replaces and disposes the last, or the fonts would add up).</summary>
    private static FileeFontCollection NewCollection()
    {
        var fonts = new FileeFontCollection(Key);
        FontManager.Current.AddFontCollection(fonts);
        return fonts;
    }

    private static string? Match(string language, int letter, FontWeight weight = FontWeight.Normal)
    {
        var chain = ThemeService.FontFamilies(language).Replace("fonts:Filee", Key, StringComparison.Ordinal);
        return FontManager.Current.TryMatchCharacter(letter, FontStyle.Normal, weight, FontStretch.Normal, FontFamily.Parse(chain), null, out var typeface)
            ? typeface.GlyphTypeface.FamilyName
            : null;
    }

    [Theory]
    [InlineData("Noto Sans", "NotoSans-Regular.ttf", true)]
    [InlineData("Noto Sans", "NotoSans-Bold.ttf", true)]
    [InlineData("Noto Sans", "NotoSansKR-Regular.otf", false)]
    [InlineData("Noto Sans KR", "NotoSansKR-Bold.otf", true)]
    [InlineData("Noto Sans KR", "NotoSansJP-Bold.otf", false)]
    [InlineData("Noto Sans Devanagari", "NotoSansDevanagari-Medium.ttf", true)]
    [InlineData("Segoe UI", "NotoSans-Regular.ttf", false)]
    public void A_font_file_belongs_to_its_family(string family, string file, bool expected) =>
        Assert.Equal(expected, FileeFontCollection.IsFileOf(family, file));

    [AvaloniaFact]
    public void A_family_is_read_when_asked_for_and_not_before()
    {
        RequireFonts();
        using var fonts = new FileeFontCollection(Key);
        Assert.Empty(fonts);

        Assert.True(fonts.TryGetGlyphTypeface("Noto Sans", FontStyle.Normal, FontWeight.Bold, FontStretch.Normal, out var bold));
        Assert.Equal(FontWeight.Bold, bold.Weight);
        Assert.DoesNotContain(fonts, f => f.Name == "Noto Sans KR");

        Assert.True(fonts.TryGetGlyphTypeface("Noto Sans KR", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out var korean));
        Assert.NotEqual((ushort)0, korean.CharacterToGlyphMap.GetGlyph('한'));
        Assert.DoesNotContain(fonts, f => f.Name == "Noto Sans JP");

        Assert.False(fonts.TryGetGlyphTypeface("Not a font", FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out _));
    }

    [AvaloniaFact]
    public void A_letter_is_only_matched_in_the_family_asked_for()
    {
        RequireFonts();
        using var fonts = new FileeFontCollection(Key);
        Assert.False(fonts.TryMatchCharacter('한', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, "Noto Sans", null, out _));
        Assert.DoesNotContain(fonts, f => f.Name == "Noto Sans KR");
        Assert.True(fonts.TryMatchCharacter('한', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, "Noto Sans KR", null, out _));
    }

    [AvaloniaTheory]
    [InlineData("en", "Noto Sans KR", "Noto Sans JP", "Noto Sans SC")] // other languages keep Korean first
    [InlineData("ko", "Noto Sans KR", "Noto Sans JP", "Noto Sans SC")]
    [InlineData("ja", "Noto Sans JP", "Noto Sans KR", "Noto Sans SC")]
    [InlineData("zh-CN", "Noto Sans SC", "Noto Sans JP", "Noto Sans KR")]
    public void A_letter_Noto_Sans_lacks_reads_its_family_and_leaves_later_ones_unread(string language, string font, string unread1, string unread2)
    {
        RequireFonts();
        var fonts = NewCollection();

        Assert.Equal("Noto Sans", Match(language, 'A'));
        Assert.DoesNotContain(fonts, f => f.Name == font);

        Assert.Equal(font, Match(language, '将'));
        Assert.Contains(fonts, f => f.Name == font);
        Assert.DoesNotContain(fonts, f => f.Name == unread1);
        Assert.DoesNotContain(fonts, f => f.Name == unread2);

        // The arrow between a format and what it becomes: Noto Sans has none, so the main window comes this far too.
        Assert.False(fonts.TryMatchCharacter('→', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, "Noto Sans", null, out _));
        Assert.Equal(font, Match(language, '→'));
        Assert.DoesNotContain(fonts, f => f.Name == unread1);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ko")]
    [InlineData("ja")]
    [InlineData("zh-CN")]
    [InlineData("ar")]
    public void A_letter_takes_the_same_font_whatever_was_read_before(string language)
    {
        RequireFonts();
        // The first letter a new collection sees is the hard case, so each letter gets a new one.
        foreach (var letter in "A한あ将กनم→")
        {
            foreach (var weight in new[] { FontWeight.Normal, FontWeight.Bold })
            {
                NewCollection();
                var firstLetter = Match(language, letter, weight);

                var fonts = NewCollection();
                foreach (var family in Families)
                    fonts.TryGetGlyphTypeface(family, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out _);
                var allRead = Match(language, letter, weight);

                Assert.True(firstLetter == allRead, $"{letter} {weight} in {language}: {firstLetter} when first, {allRead} with all read");
            }
        }
    }
}
