// Fonts for the layout engine, from the fonts installed on the computer (IFontCollection; SystemFonts by default).
// Nothing is downloaded or shipped.
//
// - Names: a family is looked up by its name, by its name without trailing style words ("Pretendard Medium" is
//   Pretendard at weight 500), and by its other names (FontNames: 함초롬바탕 ↔ HCR Batang, 한글's built-in 한양신명조 →
//   HY신명조, metric-compatible replacements such as Arial → Liberation Sans).
// - Style: the face nearest the requested weight and slant, by the CSS font matching rules. What the family lacks
//   (bold, italic) is reported as synthetic for the drawing to fake.
// - Missing fonts: a font of the same script (from its name, else the text's language) and style (serif or sans) is
//   used instead, as RhwpFonts does for rhwp (FontSubstitution). Plan applies the document-wide language and coverage
//   rules (Korean default fonts in Chinese or Japanese text, one consistent font for Russian or Arabic text).
// - Fallback: a run is split per grapheme cluster into pieces each drawn by one font: the run's own font when it has
//   every character of the cluster, else a font for the cluster's script, else the platform's fallback. Punctuation
//   and spaces stay with their neighbours' font when it has them.
// - Variable and CFF2 fonts: a variable font counts as its default instance (no instancer yet); a CFF2 font is used
//   only when no other font for its script is installed, because the PDF writer can't subset it (FontFace).

using System.Collections.Concurrent;
using System.Globalization;
using Filee.Engines.Rendering;

namespace Filee.Engines.Typesetting;

/// <summary>A font the layout draws with: an installed face, and what the drawing has to fake.</summary>
/// <param name="SyntheticBold">Bold was asked for and the family has no bold face (embolden the outlines).</param>
/// <param name="SyntheticItalic">Italic was asked for and the family has no italic face (slant the outlines).</param>
internal sealed record ResolvedFont(FontFace Face, bool SyntheticBold = false, bool SyntheticItalic = false)
{
    /// <summary>The font file (read on first use).</summary>
    public PageFont Font => Face.Font;

    public string Family => Face.Family;

    public override string ToString() =>
        $"{Face}{(SyntheticBold ? " +bold" : "")}{(SyntheticItalic ? " +italic" : "")}";
}

/// <summary>A piece of text drawn with one font.</summary>
/// <param name="Start">UTF-16 index of the first character.</param>
/// <param name="Length">Length in UTF-16 code units.</param>
internal readonly record struct FontRun(int Start, int Length, ResolvedFont Font)
{
    public int End => Start + Length;
}

/// <summary>Finds installed fonts for the names documents give, and fonts for the characters those lack.</summary>
internal sealed class FontResolver
{
    /// <summary>Installed fonts for scripts the language candidates may lack, best first.</summary>
    private static readonly Dictionary<UnicodeScript, string[]> ScriptFonts = new()
    {
        [UnicodeScript.Arabic] = ["Segoe UI", "Arial", "Tahoma", "Times New Roman", "Noto Sans Arabic", "Noto Naskh Arabic", "Geeza Pro", "DejaVu Sans"],
        [UnicodeScript.Hebrew] = ["Segoe UI", "Arial", "Times New Roman", "Noto Sans Hebrew", "Arial Hebrew", "DejaVu Sans"],
        [UnicodeScript.Syriac] = ["Segoe UI Historic", "Estrangelo Edessa", "Noto Sans Syriac"],
        [UnicodeScript.Thaana] = ["MV Boli", "Noto Sans Thaana"],
        [UnicodeScript.Devanagari] = ["Nirmala UI", "Mangal", "Noto Sans Devanagari", "Kohinoor Devanagari", "Devanagari Sangam MN", "Lohit Devanagari"],
        [UnicodeScript.Bengali] = ["Nirmala UI", "Vrinda", "Noto Sans Bengali", "Kohinoor Bangla", "Bangla Sangam MN", "Lohit Bengali"],
        [UnicodeScript.Gurmukhi] = ["Nirmala UI", "Raavi", "Noto Sans Gurmukhi", "Gurmukhi Sangam MN", "Lohit Gurmukhi"],
        [UnicodeScript.Gujarati] = ["Nirmala UI", "Shruti", "Noto Sans Gujarati", "Kohinoor Gujarati", "Gujarati Sangam MN", "Lohit Gujarati"],
        [UnicodeScript.Oriya] = ["Nirmala UI", "Kalinga", "Noto Sans Oriya", "Oriya Sangam MN", "Lohit Odia"],
        [UnicodeScript.Tamil] = ["Nirmala UI", "Latha", "Noto Sans Tamil", "Tamil Sangam MN", "Lohit Tamil"],
        [UnicodeScript.Telugu] = ["Nirmala UI", "Gautami", "Noto Sans Telugu", "Kohinoor Telugu", "Telugu Sangam MN", "Lohit Telugu"],
        [UnicodeScript.Kannada] = ["Nirmala UI", "Tunga", "Noto Sans Kannada", "Kannada Sangam MN", "Lohit Kannada"],
        [UnicodeScript.Malayalam] = ["Nirmala UI", "Kartika", "Noto Sans Malayalam", "Malayalam Sangam MN", "Lohit Malayalam"],
        [UnicodeScript.Sinhala] = ["Nirmala UI", "Iskoola Pota", "Noto Sans Sinhala", "Sinhala Sangam MN"],
        [UnicodeScript.Thai] = ["Leelawadee UI", "Tahoma", "Noto Sans Thai", "Thonburi", "Noto Serif Thai"],
        [UnicodeScript.Lao] = ["Leelawadee UI", "Lao UI", "Noto Sans Lao", "Lao Sangam MN"],
        [UnicodeScript.Khmer] = ["Leelawadee UI", "Khmer UI", "Noto Sans Khmer", "Khmer Sangam MN"],
        [UnicodeScript.Myanmar] = ["Myanmar Text", "Noto Sans Myanmar", "Myanmar Sangam MN", "Padauk"],
        [UnicodeScript.Tibetan] = ["Microsoft Himalaya", "Noto Serif Tibetan", "Kailasa"],
        [UnicodeScript.Mongolian] = ["Mongolian Baiti", "Noto Sans Mongolian"],
        [UnicodeScript.Ethiopic] = ["Ebrima", "Nyala", "Noto Sans Ethiopic", "Kefa"],
        [UnicodeScript.Armenian] = ["Segoe UI", "Sylfaen", "Noto Sans Armenian", "DejaVu Sans"],
        [UnicodeScript.Georgian] = ["Segoe UI", "Sylfaen", "Noto Sans Georgian", "DejaVu Sans"],
        [UnicodeScript.Cherokee] = ["Gadugi", "Plantagenet Cherokee", "Noto Sans Cherokee"],
    };

    private readonly IFontCollection _fonts;
    private readonly ConcurrentDictionary<(string Family, FontStyle Style), ResolvedFont?> _found = new();

    /// <param name="fonts">The fonts to choose from; the installed ones when null.</param>
    public FontResolver(IFontCollection? fonts = null) => _fonts = fonts ?? SystemFonts.Instance;

    // ───────────────────────── Names ─────────────────────────

    /// <summary>
    /// The installed face for a family name and style, by the name, the name without style words, and its other
    /// names (<see cref="FontNames"/>); null when none of them is installed.
    /// </summary>
    public ResolvedFont? Find(string family, FontStyle style) => _found.GetOrAdd((family.Trim(), style), static (key, fonts) =>
    {
        foreach (var (name, weight) in FontSubstitution.Names(key.Family))
        {
            // A style word in the name sets the weight unless the run asks for another ("Pretendard Medium" bold).
            var wanted = weight is { } named && key.Style.Weight == FontStyle.Regular.Weight ? key.Style with { Weight = named } : key.Style;
            foreach (var candidate in FontNames.Alternatives(name).Prepend(name))
            {
                var faces = fonts.Family(candidate);
                if (faces.Count > 0)
                    return Match(faces, wanted);
            }
        }
        return null;
    }, _fonts);

    /// <summary>
    /// The font to draw a family with: <see cref="Find"/>, else (or instead of a CFF2 font) an installed font for the
    /// name's script, or the text's <paramref name="language"/>, in the same style (serif or sans). Generic families
    /// (serif, sans-serif, monospace) and no family at all get the language's default fonts. Null only when no
    /// candidate font is installed.
    /// </summary>
    /// <param name="language">"ko", "ja", "zh-Hans", "zh-Hant", or "" / null for other text.</param>
    public ResolvedFont? Resolve(string? family, FontStyle style, string? language = null)
    {
        language ??= "";
        if (string.IsNullOrWhiteSpace(family) || FontNames.IsGeneric(family))
        {
            var generic = family?.Trim().ToLowerInvariant() ?? "";
            var names = generic.EndsWith("monospace", StringComparison.Ordinal)
                ? FontNames.Monospace.Concat(FontSubstitution.Preferred(language, false))
                : FontSubstitution.Preferred(language, generic is "serif" or "ui-serif");
            return First(names, style) ?? First(FontSubstitution.Preferred("", false), style);
        }
        var found = Find(family, style);
        if (found is { Face.Cff2: false })
            return found;
        // A missing monospaced font (code) keeps its columns with an installed one.
        if (FontNames.IsMonospace(family) && First(FontNames.Monospace, style) is { } monospaced)
            return monospaced;
        var script = FontSubstitution.Script(family) is { Length: > 0 } own ? own : language;
        return First(FontSubstitution.Preferred(script, FontSubstitution.IsSerif(family)), style)
            ?? found
            ?? First(FontSubstitution.Preferred("", FontSubstitution.IsSerif(family)), style);
    }

    /// <summary>
    /// Document-wide replacements by (slot, face): Korean default fonts in Chinese or Japanese text, fonts that lack
    /// the text's characters, CFF2 fonts (see <see cref="FontSubstitution.Plan"/>).
    /// </summary>
    /// <param name="slots">The fonts the document names, with the slot each is for ("HANGUL", "LATIN", ...).</param>
    public Dictionary<(string Slot, string Face), string> Plan(IReadOnlyList<(string Slot, string Face)> slots, string text) =>
        FontSubstitution.Plan(slots, text, name => Find(name, FontStyle.Regular) is { } font
            ? new FontCoverage(!font.Face.Cff2, font.Face.Has)
            : null);

    /// <summary>The first of <paramref name="names"/> that is installed and not CFF2.</summary>
    private ResolvedFont? First(IEnumerable<string> names, FontStyle style)
    {
        foreach (var name in names)
        {
            if (Find(name, style) is { Face.Cff2: false } font)
                return font;
        }
        return null;
    }

    /// <summary>
    /// The face of <paramref name="faces"/> nearest <paramref name="style"/>: the slant first, then the weight by the
    /// CSS rules (for 400–500 the weights up to 500, then lighter, then heavier; lighter first below 400, heavier
    /// first above 500).
    /// </summary>
    internal static ResolvedFont Match(IReadOnlyList<FontFace> faces, FontStyle style)
    {
        var slanted = faces.Where(f => f.Italic == style.Italic).ToList();
        var pool = slanted.Count > 0 ? slanted : faces;
        var wanted = style.Weight;
        var best = pool.MinBy(f => WeightDistance(f.Weight, wanted))!;
        return new ResolvedFont(best, SyntheticBold: style.Bold && best.Weight < 600, SyntheticItalic: style.Italic && !best.Italic);
    }

    /// <summary>Order key of a face weight for a wanted weight (lower is better).</summary>
    private static int WeightDistance(int weight, int wanted)
    {
        if (weight == wanted)
            return 0;
        if (wanted is >= 400 and <= 500)
        {
            if (weight > wanted && weight <= 500)
                return weight - wanted;
            if (weight < wanted)
                return 1000 + wanted - weight;
            return 2000 + weight - wanted;
        }
        if (wanted < 400)
            return weight < wanted ? wanted - weight : 1000 + weight - wanted;
        return weight > wanted ? weight - wanted : 1000 + wanted - weight;
    }

    // ───────────────────────── Fallback ─────────────────────────

    /// <summary>
    /// Splits <c>text[start..start+length]</c> into runs that each draw with one font: <paramref name="primary"/>
    /// where it has every character of a grapheme cluster, else the font found for the cluster. Clusters no installed
    /// font has stay with the primary font (they draw as its missing-glyph box).
    /// </summary>
    /// <param name="text">The whole paragraph, so the language can be told from it.</param>
    /// <param name="language">"ko", "ja", "zh-Hans", "zh-Hant" or ""; null: told from <paramref name="text"/>.</param>
    public List<FontRun> Fallback(string text, int start, int length, ResolvedFont primary, FontStyle style, string? language = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start + length, text.Length);
        language ??= FontSubstitution.Language(text);
        var serif = FontSubstitution.IsSerif(primary.Family);
        var used = new List<ResolvedFont> { primary };
        var runs = new List<FontRun>();
        var needed = new List<int>();
        var end = start + length;
        var position = start;
        ResolvedFont? previous = null;
        while (position < end)
        {
            var cluster = Math.Max(1, StringInfo.GetNextTextElementLength(text.AsSpan(position, end - position)));
            Needed(text.AsSpan(position, cluster), needed);
            var font = Choose(needed, primary, previous, used, style, language, serif);
            if (!used.Contains(font))
                used.Add(font);
            if (runs.Count > 0 && runs[^1].Font == font)
                runs[^1] = runs[^1] with { Length = runs[^1].Length + cluster };
            else
                runs.Add(new FontRun(position, cluster, font));
            previous = font;
            position += cluster;
        }
        return runs;
    }

    /// <summary>The font for one cluster.</summary>
    private ResolvedFont Choose(List<int> needed, ResolvedFont primary, ResolvedFont? previous, List<ResolvedFont> used,
        FontStyle style, string language, bool serif)
    {
        if (needed.Count == 0)
            return previous ?? primary;
        // Punctuation, digits and spaces keep the font of the text around them.
        var neutral = needed.All(c => UnicodeData.GetScript(c) is UnicodeScript.Common or UnicodeScript.Inherited);
        if (neutral && previous is not null && Covers(previous, needed))
            return previous;
        if (Covers(primary, needed))
            return primary;
        foreach (var font in used)
        {
            if (Covers(font, needed))
                return font;
        }

        var script = needed.Select(UnicodeData.GetScript).FirstOrDefault(s => s is not (UnicodeScript.Common or UnicodeScript.Inherited));
        var tag = LanguageFor(script, language);
        ResolvedFont? partial = null;
        foreach (var name in Candidates(script, tag, serif))
        {
            if (Find(name, style) is not { Face.Cff2: false } font)
                continue;
            if (Covers(font, needed))
                return font;
            if (partial is null && font.Face.Has(needed[0]))
                partial = font;
        }
        if (_fonts.ForCharacter(needed[0], style, tag.Length > 0 ? tag : null) is { } system)
        {
            var font = Match([system], style);
            if (Covers(font, needed) || partial is null)
                return font;
        }
        return partial ?? primary;
    }

    /// <summary>Installed fonts to try for a script, best first.</summary>
    private static IEnumerable<string> Candidates(UnicodeScript script, string language, bool serif)
    {
        var own = ScriptFonts.TryGetValue(script, out var fonts) ? fonts : [];
        return own.Concat(FontSubstitution.Preferred(language, serif)).Concat(FontSubstitution.Wide)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The language that picks a script's fonts: Hangul is Korean, kana Japanese, Han the text's.</summary>
    private static string LanguageFor(UnicodeScript script, string language) => script switch
    {
        UnicodeScript.Hangul => "ko",
        UnicodeScript.Hiragana or UnicodeScript.Katakana or UnicodeScript.KatakanaOrHiragana => "ja",
        UnicodeScript.Bopomofo => language is "zh-Hans" ? "zh-Hans" : "zh-Hant",
        UnicodeScript.Han => language is "ko" or "ja" or "zh-Hant" ? language : "zh-Hans",
        _ => language,
    };

    private static bool Covers(ResolvedFont font, List<int> needed)
    {
        foreach (var codePoint in needed)
        {
            if (!font.Face.Has(codePoint))
                return false;
        }
        return true;
    }

    /// <summary>
    /// The code points of a cluster a font must have: all but the default-ignorable ones (controls, joiners,
    /// direction marks, variation selectors), which the shaper hides.
    /// </summary>
    private static void Needed(ReadOnlySpan<char> cluster, List<int> needed)
    {
        needed.Clear();
        for (var i = 0; i < cluster.Length; i++)
        {
            var codePoint = UnicodeData.CodePointAt(cluster, i);
            if (codePoint > 0xFFFF)
                i++;
            if (codePoint is >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF)
                continue;
            if (CharUnicodeInfo.GetUnicodeCategory(codePoint) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                continue;
            needed.Add(codePoint);
        }
    }
}
