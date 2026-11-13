// Fonts for rhwp, chosen from the fonts installed on the computer (RhwpInput writes them into a copy of the HWPX).
// Nothing is downloaded or shipped.
//
// The language and coverage rules are shared with the layout engine's font resolver (Typesetting/FontSubstitution.cs):
// Korean default fonts in Chinese or Japanese text (issue #64), one consistent font for text without Hangul, Han or
// kana. What is rhwp's own stays here:
//
// - Latin in Chinese and Japanese text: rhwp lays out characters with width tables of its own and guesses 0.5 em for
//   a font it has none for, as for Microsoft YaHei, DengXian, Yu Gothic or PingFang SC. English in such a font came
//   out evenly spaced and stretched to fit (issue #1207). The LATIN slot gets an installed Latin font of the same
//   style that rhwp measures (MeasuredLatin); without one it gets the language font as before.
// - CFF2: variable fonts with CFF2 outlines (Source Han Serif VF, "思源宋体 VF") fail rhwp's font subsetter and the
//   page came out blank (issue #61); they are not usable and are always swapped.
// - The lookup through Skia's font manager, cached per name for the HWPX copies rhwp gets.

using System.Collections.Concurrent;
using Filee.Engines.Typesetting;
using SkiaSharp;

namespace Filee.Engines.Hwp;

/// <summary>An installed font: whether rhwp can embed it, which characters it has and how wide they are.</summary>
/// <param name="Advance">Advance width of a character in em, null when the font lacks it (or is not measured).</param>
internal sealed record InstalledFont(bool Usable, Func<int, bool> Has, Func<int, double?>? Advance = null) : FontCoverage(Usable, Has);

internal static class RhwpFonts
{
    /// <summary>Latin fonts of the substitution candidates rhwp 0.8.7 has width tables for, by exact name.</summary>
    private static readonly HashSet<string> MeasuredLatin =
        ["Times New Roman", "DejaVu Serif", "Georgia", "Segoe UI", "Tahoma", "Arial", "DejaVu Sans"];

    private static readonly ConcurrentDictionary<string, InstalledFont?> Fonts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Forgets the fonts looked up so far (<see cref="Infrastructure.EngineCaches.Release"/>).</summary>
    internal static void ReleaseCache() => Fonts.Clear();

    /// <summary>Rechecks one family after the user may have installed it during this session.</summary>
    internal static void Forget(string face) => Fonts.TryRemove(face, out _);

    /// <summary>Replacements by (slot, face): the language, coverage and CFF2 rules (see <see cref="FontSubstitution.Plan"/>).</summary>
    internal static Dictionary<(string Slot, string Face), string> Plan(
        IReadOnlyList<(string Slot, string Face)> slots, string text, Func<string, InstalledFont?> lookup) =>
        FontSubstitution.Plan(slots, text, lookup, MeasuredLatin);

    /// <inheritdoc cref="FontSubstitution.Language"/>
    internal static string Language(string text) => FontSubstitution.Language(text);

    /// <summary>
    /// An installed font by name, or null. "思源宋体 VF Medium" names a style of "思源宋体 VF", so trailing style words
    /// are tried off too.
    /// </summary>
    internal static InstalledFont? Find(string face) => Fonts.GetOrAdd(face, static name =>
    {
        foreach (var (candidate, _) in FontSubstitution.Names(name))
        {
            var typeface = SKFontManager.Default.MatchFamily(candidate);
            // Windows matches localized names ("思源宋体 VF") and returns nothing for unknown ones; fontconfig always
            // returns some font, so elsewhere only a font with the requested name counts.
            if (typeface is null)
                continue;
            if (OperatingSystem.IsWindows() || string.Equals(typeface.FamilyName, candidate, StringComparison.OrdinalIgnoreCase))
                return new InstalledFont(IsUsable(typeface), codePoint => typeface.GetGlyph(codePoint) != 0, codePoint => Advance(typeface, codePoint));
            // Not disposed: SkiaSharp hands out one wrapper per native typeface, so this may be the very object the
            // CAD font cache, Svg.Skia or the UI's font manager still draws with. Disposing it would leave them a
            // null handle (a native crash on their next call); the finalizer releases it once nobody uses it.
        }
        return null;
    });

    private static double? Advance(SKTypeface typeface, int codePoint)
    {
        var glyph = typeface.GetGlyph(codePoint);
        if (glyph == 0)
            return null;
        using var font = new SKFont(typeface, 1000);
        return font.GetGlyphWidths([glyph])[0] / 1000.0;
    }

    /// <summary>False when the font has CFF2 outlines (variable OpenType fonts made from PostScript outlines).</summary>
    internal static bool IsUsable(SKTypeface typeface) => !typeface.GetTableTags().Contains(Tag("CFF2"));

    /// <inheritdoc cref="FontSubstitution.Script"/>
    internal static string Script(string face) => FontSubstitution.Script(face);

    /// <inheritdoc cref="FontSubstitution.IsSerif"/>
    internal static bool IsSerif(string face) => FontSubstitution.IsSerif(face);

    private static uint Tag(string tag) => (uint)tag[0] << 24 | (uint)tag[1] << 16 | (uint)tag[2] << 8 | tag[3];
}
