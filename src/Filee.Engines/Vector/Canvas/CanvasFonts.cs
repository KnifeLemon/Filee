// Text runs of a canvas measured in the typefaces they are drawn in. CadFonts picks the sans-serif typeface and a CJK one
// from a list of well-known families; letters neither has (a CJK font under another name, as Linux distributions ship
// them, where Skia's MatchCharacter finds nothing) are looked for in the installed families, sans-serif ones first, so
// a Korean or Japanese card isn't drawn as empty boxes.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Filee.Engines.Cad;
using SkiaSharp;

namespace Filee.Engines.Vector.Canvas;

internal static class CanvasFonts
{
    private static readonly ConcurrentDictionary<(int CodePoint, bool Bold), SKTypeface?> Found = new();

    /// <summary>The installed families' regular and bold faces, sans-serif first (opened once).</summary>
    private static readonly Lazy<SKTypeface[]> Installed = new(() =>
        [.. SKFontManager.Default.FontFamilies
            .OrderBy(f => f.Contains("Mono", StringComparison.OrdinalIgnoreCase) || f.Contains("Unifont", StringComparison.OrdinalIgnoreCase)
                || (f.Contains("Serif", StringComparison.OrdinalIgnoreCase) && !f.Contains("Sans", StringComparison.OrdinalIgnoreCase)))
            .Select(f => SKFontManager.Default.MatchFamily(f))
            .OfType<SKTypeface>()]);

    /// <summary>The runs of <paramref name="text"/> in <paramref name="primary"/> or a face that has their letters, measured in em.</summary>
    public static List<GlyphRun> Layout(string text, SKTypeface primary)
    {
        var runs = CadFonts.Layout(text, primary);
        if (runs.All(Covered))
            return runs;
        var result = new List<GlyphRun>();
        foreach (var run in runs)
        {
            if (Covered(run))
            {
                result.Add(run);
                continue;
            }
            // Split the run by the face each letter is found in, and measure each part there.
            var bold = primary.FontStyle.Weight >= (int)SKFontStyleWeight.SemiBold;
            var part = new StringBuilder();
            SKTypeface? current = null;
            for (var i = 0; i < run.Text.Length;)
            {
                var width = char.IsSurrogatePair(run.Text, i) ? 2 : 1;
                var codePoint = width == 2 ? char.ConvertToUtf32(run.Text[i], run.Text[i + 1]) : run.Text[i];
                var face = char.IsWhiteSpace(run.Text[i]) ? current ?? run.Typeface
                    : run.Typeface.ContainsGlyph(codePoint) ? run.Typeface
                    : FaceFor(codePoint, bold) ?? run.Typeface;
                if (current is not null && !ReferenceEquals(face, current))
                {
                    result.AddRange(CadFonts.Layout(part.ToString(), current));
                    part.Clear();
                }
                current = face;
                part.Append(run.Text, i, width);
                i += width;
            }
            if (part.Length > 0)
                result.AddRange(CadFonts.Layout(part.ToString(), current!));
        }
        return result;
    }

    /// <summary>
    /// Letters no font is expected to draw (private use, unassigned, control and format characters, lone surrogates):
    /// the installed families aren't scanned for them, so odd text doesn't cost a font lookup per letter.
    /// </summary>
    private static bool HasNoGlyph(int codePoint) =>
        (uint)codePoint > 0x10FFFF || CharUnicodeInfo.GetUnicodeCategory(codePoint) is UnicodeCategory.PrivateUse
            or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate;

    private static bool Covered(GlyphRun run)
    {
        foreach (var rune in run.Text.EnumerateRunes())
            if (!Rune.IsWhiteSpace(rune) && !run.Typeface.ContainsGlyph(rune.Value))
                return false;
        return true;
    }

    private static SKTypeface? FaceFor(int codePoint, bool bold) => HasNoGlyph(codePoint) ? null : Found.GetOrAdd((codePoint, bold), key =>
    {
        var face = Installed.Value.FirstOrDefault(f => f.ContainsGlyph(key.CodePoint));
        if (face is null || !key.Bold)
            return face;
        return SKFontManager.Default.MatchFamily(face.FamilyName, SKFontStyle.Bold) is { } boldFace && boldFace.ContainsGlyph(key.CodePoint)
            ? boldFace : face;
    });
}
