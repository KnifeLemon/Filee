// The fonts installed on the computer, through Skia's font manager: DirectWrite on Windows, CoreText on macOS and
// fontconfig on Linux. Nothing is downloaded or shipped.
//
// - Windows matches localized family names ("맑은 고딕") and returns nothing for unknown ones. fontconfig (and
//   CoreText) answer an unknown family with some installed font, so elsewhere only faces whose family name is the
//   requested one count.
// - macOS: a family is asked for only when CoreText lists it as installed. A request for one that isn't can start
//   CoreText's activation of a font macOS offers for download (Nanum Gothic, which the Korean fallback list names),
//   and while that waits every other font request of the process waits with it: on a computer nobody answers, the
//   layout of a short document and unrelated chart reads stood still for minutes. Asks is the check for every font
//   lookup of Filee by a name a document or setting gives (MatchFamily here); Apple's hidden system faces (".SF NS",
//   ".AppleSystemUIFont") are never listed and never downloaded, so they are asked for.
// - Per-character fallback goes through CharacterFallbacks, which keeps answers and stops asking about blocks that have
//   nothing, so a document full of characters no font has doesn't make one native call per character.
// - Style, variable and CFF2 flags come from the font's own tables (OS/2, fvar, CFF2), not from what the font
//   manager reports: DirectWrite lists the named instances of a variable font as faces of their own, but their data
//   is the whole font, which shapes and draws at its default instance.
// - Typefaces are never disposed: SkiaSharp hands out one wrapper per native typeface, so it may be the very object
//   the UI, Svg.Skia or the CAD fonts still draw with (see RhwpFonts.Find).

using System.Buffers.Binary;
using System.Collections.Concurrent;
using Filee.Engines.Rendering;
using SkiaSharp;

namespace Filee.Engines.Typesetting;

/// <summary>The installed fonts.</summary>
internal sealed class SystemFonts : IFontCollection
{
    private static readonly uint Os2 = Tag("OS/2"), Fvar = Tag("fvar"), Cff2 = Tag("CFF2");

    private readonly ConcurrentDictionary<string, IReadOnlyList<FontFace>> _families = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string Family, int Weight, bool Italic), FontFace> _fallbacks = new();

    private readonly CharacterFallbacks _characters;

    private SystemFonts() => _characters = new CharacterFallbacks(Ask);

    public static SystemFonts Instance { get; } = new();

    /// <summary>Forgets the fonts looked up so far (<see cref="Infrastructure.EngineCaches.Release"/>).</summary>
    internal static void ReleaseCache()
    {
        Instance._families.Clear();
        Instance._fallbacks.Clear();
        Instance._characters.Clear();
        _installed = null;
    }

    /// <summary>macOS: the installed families by name (see the top), made on first use.</summary>
    private static volatile HashSet<string>? _installed;

    /// <summary>
    /// Whether <see cref="Family"/> asks the font manager for <paramref name="name"/>: always on Windows and Linux; on
    /// macOS only for a family CoreText lists as installed, by its listed name or the name its fonts give.
    /// </summary>
    internal static bool Asks(string name) => !OperatingSystem.IsMacOS() || Listed(Installed(), name);

    /// <summary>Whether <paramref name="name"/> is one of <paramref name="installed"/> or one of Apple's hidden system faces.</summary>
    internal static bool Listed(IReadOnlySet<string> installed, string name) =>
        installed.Contains(name) || (name.Length > 1 && name[0] == '.' && name.AsSpan().IndexOfAnyExceptInRange(' ', '~') < 0);

    /// <summary>
    /// The installed family's face nearest <paramref name="style"/> through Skia's font manager, or null; on macOS only
    /// for a family <see cref="Asks"/> allows. Not to be disposed (see the top).
    /// </summary>
    internal static SKTypeface? MatchFamily(string name, SKFontStyle? style = null) =>
        Asks(name) ? SKFontManager.Default.MatchFamily(name, style ?? SKFontStyle.Normal) : null;

    private static HashSet<string> Installed()
    {
        if (_installed is { } known)
            return known;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in SKFontManager.Default.FontFamilies)
        {
            names.Add(family);
            // The list may give a family its localized name; Family compares the fonts' own one.
            if (!System.Text.Ascii.IsValid(family))
            {
                using var styles = SKFontManager.Default.GetFontStyles(family);
                if (styles is { Count: > 0 } && styles.CreateTypeface(0) is { } typeface)
                    names.Add(typeface.FamilyName);
            }
        }
        return _installed = names;
    }

    public IReadOnlyList<FontFace> Family(string name) => _families.GetOrAdd(name, static name =>
    {
        if (!Asks(name))
            return [];
        using var styles = SKFontManager.Default.GetFontStyles(name);
        if (styles is null)
            return [];
        var faces = new List<FontFace>();
        for (var i = 0; i < styles.Count; i++)
        {
            var typeface = styles.CreateTypeface(i);
            if (typeface is null)
                continue;
            if (!OperatingSystem.IsWindows() && !string.Equals(typeface.FamilyName, name, StringComparison.OrdinalIgnoreCase))
                continue;
            var face = Face(name, typeface);
            // The named instances of a variable font are one face: the same data, the same default instance.
            if (!faces.Any(f => f.Weight == face.Weight && f.Italic == face.Italic))
                faces.Add(face);
        }
        return faces;
    });

    public FontFace? ForCharacter(int codePoint, FontStyle style, string? language) => _characters.Find(codePoint, style, language);

    /// <summary>Native questions <see cref="Ask"/> has put on this thread (tests count them).</summary>
    [ThreadStatic]
    internal static int CharacterQuestions;

    private FontFace? Ask(int codePoint, FontStyle style, string? language)
    {
        CharacterQuestions++;
        var typeface = SKFontManager.Default.MatchCharacter(null, style.Weight, (int)SKFontStyleWidth.Normal,
            style.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright, language is null ? [] : [language], codePoint);
        if (typeface is null || typeface.GetGlyph(codePoint) == 0)
            return null;
        var probe = Face(typeface.FamilyName, typeface);
        return _fallbacks.GetOrAdd((typeface.FamilyName, probe.Weight, probe.Italic), _ => probe);
    }

    /// <summary>A face over a typeface Skia found, with its style read from the font's tables.</summary>
    private static FontFace Face(string family, SKTypeface typeface)
    {
        var tags = typeface.GetTableTags() ?? [];
        var weight = typeface.FontWeight;
        var italic = typeface.FontSlant != SKFontStyleSlant.Upright;
        if (typeface.TryGetTableData(Os2, out var os2) && os2.Length >= 64)
        {
            // Some old fonts store the weight class as 1–9.
            weight = BinaryPrimitives.ReadUInt16BigEndian(os2.AsSpan(4)) switch
            {
                0 => weight,
                < 10 and var small => small * 100,
                var value => Math.Min((int)value, 1000),
            };
            italic = (os2[63] & 1) != 0; // fsSelection bit 0: ITALIC
        }
        return new FontFace(family, weight, italic, codePoint => typeface.GetGlyph(codePoint) != 0, () => PageFont.FromTypeface(typeface))
        {
            Variable = tags.Contains(Fvar),
            Cff2 = tags.Contains(Cff2),
        };
    }

    private static uint Tag(string tag) => (uint)tag[0] << 24 | (uint)tag[1] << 16 | (uint)tag[2] << 8 | tag[3];
}
