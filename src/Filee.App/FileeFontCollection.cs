// The embedded Noto fonts (build/fetch-fonts.ps1), each family read when text first asks for it (issue #957). Avalonia's
// EmbeddedFontCollection opens every file at the start, and SkiaSharp copies a font it opens from a stream into native
// memory for good. So all 18 files (37 MB) stayed in memory on top of the pages of Filee.dll that the copying read, also
// in a Filee started to the tray that has drawn no text yet. Since a letter is only matched in the family asked for, the
// families after the one that has it are never asked for: Noto Sans SC stays unread until a chain reaches it.

using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;

namespace Filee.App;

/// <summary>
/// Embedded Noto fonts: <c>fonts:Filee#Noto Sans</c>, <c>#Noto Sans JP</c>, <c>#Noto Sans KR</c>,
/// <c>#Noto Sans SC</c>, <c>#Noto Sans Devanagari</c>, <c>#Noto Sans Arabic</c>, <c>#Noto Sans Thai</c>.
/// <para>
/// A letter is only matched in the family asked for, so the order of SoftPalette's font chain decides (#1095). Avalonia's
/// own collection, asked for Noto Sans about a Han character, answers with the first family of its own that has it,
/// Noto Sans JP, before the font manager reaches the chain's next family: every language drew Han in Japanese shapes,
/// up to the first character that font lacks. Text has no culture to go by either, as TextBlock passes none. It takes
/// this wrapper rather than a subclass: the font manager skips the virtual method on Avalonia's collections.
/// </para>
/// </summary>
internal sealed class FileeFontCollection : IFontCollection
{
    internal static readonly Uri Source = new("avares://Filee/Assets/Fonts", UriKind.Absolute);

    private readonly ReadFonts _fonts;
    private readonly Lock _lock = new();
    private readonly HashSet<string> _asked = new(StringComparer.OrdinalIgnoreCase);

    internal FileeFontCollection(string key = "fonts:Filee") => _fonts = new ReadFonts(new Uri(key, UriKind.Absolute));

    public Uri Key => _fonts.Key;

    /// <summary>The families read so far.</summary>
    public int Count => _fonts.Count;

    public FontFamily this[int index] => _fonts[index];

    public bool TryMatchCharacter(int codepoint, FontStyle fontStyle, FontWeight fontWeight, FontStretch fontStretch,
        string? familyName, CultureInfo? culture, out Typeface typeface)
    {
        typeface = default;
        // No culture from here on: the family is settled, and one would send it looking elsewhere again.
        return familyName is not null
            && TryGetGlyphTypeface(familyName, fontStyle, fontWeight, fontStretch, out var face)
            && face.CharacterToGlyphMap.TryGetGlyph(codepoint, out _)
            && _fonts.TryMatchCharacter(codepoint, fontStyle, fontWeight, fontStretch, familyName, null, out typeface);
    }

    public bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? glyphTypeface)
    {
        ReadFamily(familyName);
        return _fonts.TryGetGlyphTypeface(familyName, style, weight, stretch, out glyphTypeface);
    }

    public bool TryGetFamilyTypefaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<Typeface>? familyTypefaces)
    {
        ReadFamily(familyName);
        return _fonts.TryGetFamilyTypefaces(familyName, out familyTypefaces);
    }

    public bool TryCreateSyntheticGlyphTypeface(GlyphTypeface glyphTypeface, FontStyle style, FontWeight weight, FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? syntheticGlyphTypeface) =>
        _fonts.TryCreateSyntheticGlyphTypeface(glyphTypeface, style, weight, stretch, out syntheticGlyphTypeface);

    public bool TryGetNearestMatch(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? glyphTypeface)
    {
        ReadFamily(familyName);
        return _fonts.TryGetNearestMatch(familyName, style, weight, stretch, out glyphTypeface);
    }

    public IEnumerator<FontFamily> GetEnumerator() => _fonts.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose() => ((IDisposable)_fonts).Dispose();

    /// <summary>"NotoSansKR-Bold.otf" belongs to "Noto Sans KR", and not to "Noto Sans".</summary>
    internal static bool IsFileOf(string familyName, string fileName) =>
        fileName.StartsWith(familyName.Replace(" ", "", StringComparison.Ordinal) + "-", StringComparison.OrdinalIgnoreCase);

    private void ReadFamily(string familyName)
    {
        lock (_lock)
        {
            if (!_asked.Add(familyName))
                return;
            // None when the fonts weren't fetched: the family isn't found, and the chain goes on to the system's fonts.
            foreach (var asset in AssetLoader.GetAssets(Source, null))
                if (IsFileOf(familyName, Path.GetFileName(asset.AbsolutePath)))
                    _fonts.TryAddFontSource(asset);
        }
    }

    private sealed class ReadFonts(Uri key) : FontCollectionBase
    {
        public override Uri Key => key;
    }
}
