// The fonts Annotate's labels are drawn in, handed to the painter (Filee.Engines): the UI's font chain (SoftPalette) in
// bold, as the box a label is typed in shows it, so the label drawn is the label typed. The embedded Noto fonts are
// read the first time a label is drawn, their bold faces only.

using Avalonia.Media;
using Avalonia.Platform;
using Filee.Engines.Annotations;
using SkiaSharp;

namespace Filee.App.Services;

internal static class AnnotationFonts
{
    private const string EmbeddedPrefix = "fonts:Filee#";

    private static readonly Lazy<IReadOnlyList<SKTypeface>> Embedded = new(LoadEmbedded);
    private static string? _language;

    /// <summary>
    /// Gives the painter the chain of <paramref name="language"/>'s UI font, in bold: at start and when the language
    /// changes, so a conversion run later (a queued one, a retry) draws its labels as the window showed them. The fonts
    /// are read the first time a label is drawn.
    /// </summary>
    public static void Use(string language)
    {
        if (_language == language)
            return;
        _language = language;
        AnnotationPainter.UseFonts(() => Chain(language));
        var family = SoftPalette.FontFamilyFor(language);
        AnnotationPainter.UseFallback(codePoint => FallbackFor(codePoint, family));
    }

    /// <summary>
    /// The face the text box draws a letter in that its first font lacks, and whether it makes it bold: Avalonia's font
    /// manager asked as its text layout asks it (the whole chain, bold, the UI's language), then that face, an embedded
    /// one or the system's, by its name.
    /// </summary>
    private static (SKTypeface Face, bool MadeBold)? FallbackFor(int codePoint, FontFamily family)
    {
        try
        {
            if (!FontManager.Current.TryMatchCharacter(codePoint, FontStyle.Normal, FontWeight.Bold, FontStretch.Normal, family, null, out var typeface)
                || !FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphs))
                return null;
            var weight = (int)glyphs.Weight;
            var face = Embedded.Value.Where(f => f.FamilyName == glyphs.FamilyName).OrderBy(f => Math.Abs(f.FontWeight - weight)).FirstOrDefault()
                       ?? SKFontManager.Default.MatchFamily(glyphs.FamilyName, new SKFontStyle((SKFontStyleWeight)weight,
                           (SKFontStyleWidth)(int)glyphs.Stretch, glyphs.Style == FontStyle.Normal ? SKFontStyleSlant.Upright : SKFontStyleSlant.Italic));
            return face is null ? null : (face, glyphs.FontSimulations.HasFlag(FontSimulations.Bold));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static List<SKTypeface> Chain(string language)
    {
        var chain = new List<SKTypeface>();
        foreach (var family in SoftPalette.FontFamilies(language).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            // "sans-serif" ends the chain: what no family has, the painter finds as the text box's fallback does.
            var face = family.StartsWith(EmbeddedPrefix, StringComparison.Ordinal)
                ? Embedded.Value.FirstOrDefault(f => f.FamilyName == family[EmbeddedPrefix.Length..])
                : family == "sans-serif" ? null
                : SKFontManager.Default.MatchFamily(family, SKFontStyle.Bold) is { } system
                  && string.Equals(system.FamilyName, family, StringComparison.OrdinalIgnoreCase) ? system : null;
            if (face is not null && !chain.Contains(face))
                chain.Add(face);
        }
        return chain;
    }

    /// <summary>The embedded fonts' bold faces; none when they weren't fetched (build/fetch-fonts.ps1).</summary>
    private static IReadOnlyList<SKTypeface> LoadEmbedded()
    {
        var faces = new List<SKTypeface>();
        try
        {
            foreach (var uri in AssetLoader.GetAssets(new Uri("avares://Filee/Assets/Fonts"), null))
            {
                if (!uri.AbsolutePath.Contains("-Bold.", StringComparison.Ordinal))
                    continue;
                using var stream = AssetLoader.Open(uri);
                using var data = SKData.Create(stream);
                if (SKTypeface.FromData(data) is { } face)
                    faces.Add(face);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FileNotFoundException)
        {
            // Without them the system's fonts are used, as the UI does.
        }
        return faces;
    }
}
