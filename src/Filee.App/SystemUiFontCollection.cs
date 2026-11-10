// The system's installed fonts under fonts:System, for Theme → Use the system font (#1065). Asked whether a family has
// a letter, Avalonia's own system collection doesn't look in that family: it hands the letter to the platform's
// fallback, which on Windows answers Yu Gothic UI for Han whatever the family or the UI language. A chain that starts
// with a system font then drew every language's Han in Japanese shapes. Here a letter is only matched in the family
// asked for, as FileeFontCollection does for the embedded fonts, so SoftPalette's chain decides.

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
        typeface = new Typeface(new FontFamily($"{KeyName}#{familyName}"), fontStyle, fontWeight, fontStretch);
        return true;
    }

    public bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? glyphTypeface) =>
        System.TryGetGlyphTypeface(familyName, style, weight, stretch, out glyphTypeface);

    public bool TryGetFamilyTypefaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<Typeface>? familyTypefaces) =>
        System.TryGetFamilyTypefaces(familyName, out familyTypefaces);

    public bool TryCreateSyntheticGlyphTypeface(GlyphTypeface glyphTypeface, FontStyle style, FontWeight weight, FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? syntheticGlyphTypeface) =>
        System.TryCreateSyntheticGlyphTypeface(glyphTypeface, style, weight, stretch, out syntheticGlyphTypeface);

    public bool TryGetNearestMatch(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? glyphTypeface) =>
        System.TryGetNearestMatch(familyName, style, weight, stretch, out glyphTypeface);

    public IEnumerator<FontFamily> GetEnumerator() => Enumerable.Empty<FontFamily>().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        // The system collection belongs to the font manager.
    }
}
