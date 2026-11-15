// The system's installed fonts under fonts:System, for Theme → Use the system font (#1065). Asked whether a family has
// a letter, Avalonia's own system collection doesn't look in that family: it hands the letter to the platform's
// fallback, which on Windows answers Yu Gothic UI for Han whatever the family or the UI language. A chain that starts
// with a system font then drew every language's Han in Japanese shapes. Here a letter is only matched in the family
// asked for, as FileeFontCollection does for the embedded fonts, so SoftPalette's chain decides.
//
// The font manager also passes over a match whose family is named like its default font (Segoe UI on Windows) unless
// that family ends the chain: it takes it for a collection's "nothing found" answer (Avalonia 12.1.4,
// FontManager.TryMatchCharacter). Here the default font is a real answer, so it comes back under an alias that only this
// collection reads. Without it, Arabic on Windows 11 skipped Segoe UI for the embedded Noto Sans Arabic.

using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace Filee.App;

internal sealed class SystemUiFontCollection : IFontCollection
{
    /// <summary>The key SoftPalette's chain names these fonts by: <c>fonts:System#Segoe UI</c>.</summary>
    internal const string KeyName = "fonts:System";

    public Uri Key { get; } = new(KeyName, UriKind.Absolute);

    /// <summary>Marks the default font's name in a match, so the font manager doesn't pass over it.</summary>
    private const string Alias = "|asked";

    private static IFontCollection System => FontManager.Current.SystemFonts;

    /// <summary>Lists none: every family is the system collection's, listed there.</summary>
    public int Count => 0;

    public FontFamily this[int index] => throw new ArgumentOutOfRangeException(nameof(index));

    public bool TryMatchCharacter(int codepoint, FontStyle fontStyle, FontWeight fontWeight, FontStretch fontStretch,
        string? familyName, CultureInfo? culture, out Typeface typeface)
    {
        typeface = default;
        if (familyName is null
            || !TryGetGlyphTypeface(familyName, fontStyle, fontWeight, fontStretch, out var face)
            || !face.CharacterToGlyphMap.TryGetGlyph(codepoint, out _))
            return false;
        var name = Unaliased(familyName);
        if (name == FontManager.Current.DefaultFontFamily.Name)
            name += Alias;
        typeface = new Typeface(new FontFamily($"{KeyName}#{name}"), fontStyle, fontWeight, fontStretch);
        return true;
    }

    public bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? glyphTypeface) =>
        System.TryGetGlyphTypeface(Unaliased(familyName), style, weight, stretch, out glyphTypeface);

    public bool TryGetFamilyTypefaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<Typeface>? familyTypefaces) =>
        System.TryGetFamilyTypefaces(Unaliased(familyName), out familyTypefaces);

    public bool TryCreateSyntheticGlyphTypeface(GlyphTypeface glyphTypeface, FontStyle style, FontWeight weight, FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? syntheticGlyphTypeface) =>
        System.TryCreateSyntheticGlyphTypeface(glyphTypeface, style, weight, stretch, out syntheticGlyphTypeface);

    public bool TryGetNearestMatch(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? glyphTypeface) =>
        System.TryGetNearestMatch(Unaliased(familyName), style, weight, stretch, out glyphTypeface);

    private static string Unaliased(string familyName) =>
        familyName.EndsWith(Alias, StringComparison.Ordinal) ? familyName[..^Alias.Length] : familyName;

    public IEnumerator<FontFamily> GetEnumerator() => Enumerable.Empty<FontFamily>().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        // The system collection belongs to the font manager.
    }
}
