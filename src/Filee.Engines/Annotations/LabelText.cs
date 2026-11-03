// The text of Annotate's labels and numbers, laid out as the window's text box shows it while it is typed: each letter
// in the first font of the UI's font chain that has it (the app passes the chain, its embedded Noto fonts first), else
// one the system finds; put in order by the Unicode bidirectional algorithm (UAX #9) for a paragraph without explicit
// embeddings or isolates (rules W1–W7, N0–N2, I1–I2, L2); each run shaped with HarfBuzz (GlyphOutlines) in its
// direction, so Arabic joins, brackets mirror and Indic and Thai marks sit in place.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Filee.Engines.Vector;
using SkiaSharp;

namespace Filee.Engines.Annotations;

internal static class LabelText
{
    // Per typeface: the chain's embedded "Noto Sans" and a system one of that name have different glyphs.
    private static readonly GlyphOutlines Shaper = new(perTypeface: true);
    private static readonly Lock ShaperLock = new();
    private static volatile Lazy<SKTypeface[]> _chain = new([]);
    private static volatile Func<int, (SKTypeface Face, bool MadeBold)?>? _fallback;

    /// <summary>Faces the app's fallback found, and whether the text box makes each bold.</summary>
    private static readonly ConcurrentDictionary<SKTypeface, bool> FoundFaces = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The fonts labels are drawn in, in order: the UI's font chain in bold, as the text box they are typed in uses it,
    /// loaded the first time a label is laid out. Without it (the command line), the system's bold sans-serif and the
    /// fonts the system finds.
    /// </summary>
    public static void UseFonts(Func<IEnumerable<SKTypeface>> fonts) => _chain = new(() =>
    {
        try
        {
            return [.. fonts()];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Without the chain, labels are drawn in the system's fonts, rather than every label failing after.
            return [];
        }
    });

    /// <summary>
    /// How a font is found for a letter the first font lacks, and whether it is made bold: as the text box does it (the
    /// app asks Avalonia's font manager over the whole chain, as its text layout asks it), so the two pick the same
    /// face. Walking the chain in order picked Noto Sans KR for "★" where the box has Noto Sans JP, and on Windows 11
    /// 25H2 the system, asked without the UI's language, gave the colour Segoe UI Emoji for "👍" where the box has the
    /// plain Segoe UI Symbol. Without it (the command line), the chain is walked and the system asked.
    /// </summary>
    public static void UseFallback(Func<int, (SKTypeface Face, bool MadeBold)?> fallback)
    {
        _fallback = fallback;
        Fallbacks.Clear();
        FoundFaces.Clear();
    }

    /// <summary>
    /// Reads the fonts labels will need, so the first label drawn doesn't wait for them (several MB of CJK fonts, and
    /// on Linux every installed family): called off the UI thread when Annotate opens.
    /// </summary>
    public static void Warm()
    {
        _ = Fonts;
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            _ = Installed.Value;
    }

    private static SKTypeface[] Fonts => _chain.Value;

    /// <summary>The first font of the chain: the one a line's height is measured by.</summary>
    public static SKTypeface Primary => Fonts is [var first, ..] ? first : SystemPrimary.Value;

    public static SKFont FontOf(SKTypeface face, float size) =>
        new(face, size) { Edging = SKFontEdging.Antialias, Subpixel = true, Embolden = MadeBold(face) };

    /// <summary>
    /// Whether Skia makes the face bold, as Avalonia does in the text box: a font of the chain that has no bold face
    /// is (a system font named there); one the system finds for a letter the chain lacks never is (Avalonia's font
    /// fallback asks for no simulation), so an emoji font's colour glyphs keep their shape.
    /// </summary>
    internal static bool MadeBold(SKTypeface face)
    {
        if (FoundFaces.TryGetValue(face, out var madeBold))
            return madeBold;
        if (face.IsBold)
            return false;
        var chain = Fonts;
        return chain.Length > 0 ? Array.IndexOf(chain, face) >= 0 : face == SystemPrimary.Value;
    }

    /// <summary>
    /// Lays <paramref name="text"/> out from <paramref name="x"/> on <paramref name="baseline"/>, adding its glyphs to
    /// <paramref name="builder"/> when there is one, and returns how wide it is. Drawn as text, not outlines, so colour
    /// emoji show.
    /// </summary>
    public static float Lay(string text, float size, SKTextBlobBuilder? builder, float x, float baseline)
    {
        var pen = x;
        lock (ShaperLock)
        {
            foreach (var run in Runs(text))
            {
                using var font = FontOf(run.Face, size);
                // Unshaped only when the font's data can't be read.
                var (glyphs, positions, width) = Shaper.Glyphs(run.Text, font, run.RightToLeft)
                                                 ?? (font.GetGlyphs(run.Text), font.GetGlyphPositions(run.Text), font.MeasureText(run.Text));
                if (builder is not null && glyphs.Length > 0)
                {
                    var buffer = builder.AllocatePositionedRun(font, glyphs.Length);
                    buffer.SetGlyphs(glyphs);
                    buffer.SetPositions([.. positions.Select(p => new SKPoint(p.X + pen, p.Y + baseline))]);
                }
                pen += width;
            }
        }
        return pen - x;
    }

    /// <summary>
    /// Where the baseline of <paramref name="text"/> goes for its line to be centred on <paramref name="middle"/>: the
    /// line reaches as far up and down as the tallest of its fonts, as the text box lays a line out (Arabic's font
    /// reaches further down than Latin's).
    /// </summary>
    public static float BaselineFor(string text, float size, float middle)
    {
        List<SKTypeface> faces;
        lock (ShaperLock)
            faces = text.Length == 0 ? [Primary] : [.. Runs(text).Select(r => r.Face).Distinct()];
        float up = 0, down = 0;
        foreach (var face in faces)
        {
            using var font = FontOf(face, size);
            font.GetFontMetrics(out var metrics);
            up = Math.Max(up, -metrics.Ascent);
            down = Math.Max(down, metrics.Descent);
        }
        return middle + (up - down) / 2;
    }

    /// <summary>
    /// Whether a text reads right to left as a whole: its first strong letter does (Arabic, Hebrew …).
    /// </summary>
    public static bool IsRightToLeft(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            switch (KindOf(rune))
            {
                case Kind.R or Kind.AL:
                    return true;
                case Kind.L:
                    return false;
            }
        }
        return false;
    }

    internal readonly record struct Run(string Text, SKTypeface Face, bool RightToLeft);

    /// <summary>
    /// <paramref name="text"/> cut where its font or direction changes, in the order the parts are drawn, left to
    /// right. Each letter is in the first font of the chain that has it, spaces too, as the text box picks them; marks
    /// stay with the letter before them.
    /// </summary>
    internal static List<Run> Runs(string text)
    {
        var runes = text.EnumerateRunes().ToArray();
        if (runes.Length == 0)
            return [];
        var levels = Levels(runes, IsRightToLeft(text));

        // Each letter's font: marks stay with the letter before them.
        var faces = new SKTypeface[runes.Length];
        for (var i = 0; i < runes.Length; i++)
            faces[i] = i > 0 && StaysWithLetterBefore(runes[i]) ? faces[i - 1] : FaceFor(runes[i].Value, i > 0 ? faces[i - 1] : null);

        var runs = new List<(int Start, int End, int Level)>();
        for (var i = 0; i < runes.Length; i++)
        {
            if (runs.Count > 0 && runs[^1].Level == levels[i] && faces[runs[^1].Start] == faces[i])
                runs[^1] = runs[^1] with { End = i + 1 };
            else
                runs.Add((i, i + 1, levels[i]));
        }
        // From the highest level down to 1, every stretch of runs at that level or above turns around (UAX #9 L2).
        for (var level = runs.Max(r => r.Level); level >= 1; level--)
        {
            for (var i = 0; i < runs.Count; i++)
            {
                if (runs[i].Level < level)
                    continue;
                var end = i;
                while (end + 1 < runs.Count && runs[end + 1].Level >= level)
                    end++;
                runs.Reverse(i, end - i + 1);
                i = end;
            }
        }
        return [.. runs.Select(r => new Run(string.Concat(runes[r.Start..r.End].Select(x => x.ToString())), faces[r.Start], r.Level % 2 == 1))];
    }

    /// <summary>
    /// A letter's bidirectional class (UAX #9, table 4): strong (L, R, AL), weak (EN, AN, ES, ET, CS, NSM, where a
    /// boundary neutral such as a joiner counts as NSM) and neutral (whitespace and other neutrals together).
    /// </summary>
    private enum Kind { L, R, AL, EN, AN, ES, ET, CS, NSM, ON }

    /// <summary>
    /// Each letter's embedding level, 0 to 2, by the rules of UAX #9 for a paragraph without explicit embeddings: W1–W7
    /// for weak types, N1–N2 for neutrals, I1–I2 for levels. A number after Arabic letters is an Arabic number, which
    /// takes no sign or percent (W2, W5), so "خصم 50%" shows "%50" on the left as the text box does.
    /// </summary>
    private static int[] Levels(Rune[] runes, bool rightToLeft)
    {
        var n = runes.Length;
        var sos = rightToLeft ? Kind.R : Kind.L;
        var t = new Kind[n];
        for (var i = 0; i < n; i++)
            t[i] = KindOf(runes[i]);
        var original = (Kind[])t.Clone();
        // W1: a mark takes the type of what is before it.
        for (var i = 0; i < n; i++)
        {
            if (t[i] == Kind.NSM)
                t[i] = i > 0 ? t[i - 1] : sos;
        }
        // W2: a European number after Arabic letters is an Arabic number. W3: Arabic letters are right to left.
        var strong = sos;
        for (var i = 0; i < n; i++)
        {
            if (t[i] is Kind.L or Kind.R or Kind.AL)
                strong = t[i];
            else if (t[i] == Kind.EN && strong == Kind.AL)
                t[i] = Kind.AN;
        }
        for (var i = 0; i < n; i++)
        {
            if (t[i] == Kind.AL)
                t[i] = Kind.R;
        }
        // W4: one separator between two numbers of the same kind joins them ("1,200", "3.5", "1-2" for European ones).
        for (var i = 1; i + 1 < n; i++)
        {
            if (t[i] == Kind.ES && t[i - 1] == Kind.EN && t[i + 1] == Kind.EN)
                t[i] = Kind.EN;
            else if (t[i] == Kind.CS && t[i - 1] == t[i + 1] && t[i - 1] is Kind.EN or Kind.AN)
                t[i] = t[i - 1];
        }
        // W5: terminators ("%", "$", "#", "°") beside a European number belong to it.
        for (var i = 0; i < n; i++)
        {
            if (t[i] != Kind.ET)
                continue;
            var end = i;
            while (end < n && t[end] == Kind.ET)
                end++;
            if ((i > 0 && t[i - 1] == Kind.EN) || (end < n && t[end] == Kind.EN))
            {
                for (var j = i; j < end; j++)
                    t[j] = Kind.EN;
            }
            i = end - 1;
        }
        // W6: separators and terminators left over are neutrals. W7: a European number after left-to-right letters is
        // left to right.
        strong = sos;
        for (var i = 0; i < n; i++)
        {
            if (t[i] is Kind.ES or Kind.ET or Kind.CS)
                t[i] = Kind.ON;
            if (t[i] is Kind.L or Kind.R)
                strong = t[i];
            else if (t[i] == Kind.EN && strong == Kind.L)
                t[i] = Kind.L;
        }
        PairBrackets(runes, t, original, sos);
        // N1, N2: neutrals between two the same way go that way (numbers count as right to left), else the paragraph's.
        static bool IsRight(Kind kind) => kind is Kind.R or Kind.EN or Kind.AN;
        for (var i = 0; i < n; i++)
        {
            if (t[i] != Kind.ON)
                continue;
            var end = i;
            while (end < n && t[end] == Kind.ON)
                end++;
            var before = i > 0 ? IsRight(t[i - 1]) : rightToLeft;
            var after = end < n ? IsRight(t[end]) : rightToLeft;
            var way = before == after ? (before ? Kind.R : Kind.L) : sos;
            for (var j = i; j < end; j++)
                t[j] = way;
            i = end - 1;
        }
        // I1, I2: on the paragraph's level, or one or two above it.
        var levels = new int[n];
        for (var i = 0; i < n; i++)
        {
            levels[i] = rightToLeft
                ? (t[i] == Kind.R ? 1 : 2)
                : t[i] switch { Kind.L => 0, Kind.R => 1, _ => 2 };
        }
        return levels;
    }

    /// <summary>
    /// N0: a pair of brackets goes the paragraph's way when there is a strong letter of that way inside it; else the
    /// other way when there is one of that inside and before it too; else as N1 makes it. So "מחיר (USD) 5" keeps
    /// "(USD)" whole, as the text box does. Marks after a bracket go as the bracket.
    /// </summary>
    private static void PairBrackets(Rune[] runes, Kind[] t, Kind[] original, Kind sos)
    {
        // BD16: the pairs, opening and closing, found with a stack of at most 63 open brackets.
        var pairs = new List<(int Open, int Close)>();
        var open = new List<(int Closing, int At)>();
        for (var i = 0; i < runes.Length; i++)
        {
            if (t[i] != Kind.ON)
                continue;
            var c = Canonical(runes[i].Value);
            if (Brackets.TryGetValue(c, out var closing))
            {
                if (open.Count == 63)
                    break;
                open.Add((closing, i));
            }
            else if (Closings.Contains(c))
            {
                var match = open.FindLastIndex(o => o.Closing == c);
                if (match < 0)
                    continue;
                pairs.Add((open[match].At, i));
                open.RemoveRange(match, open.Count - match);
            }
        }
        static Kind? Strong(Kind kind) => kind switch { Kind.L => Kind.L, Kind.R or Kind.EN or Kind.AN => Kind.R, _ => null };
        foreach (var (start, end) in pairs.OrderBy(p => p.Open))
        {
            var inside = Enumerable.Range(start + 1, end - start - 1).Select(i => Strong(t[i])).Where(k => k is not null).ToList();
            Kind way;
            if (inside.Contains(sos))
            {
                way = sos;
            }
            else if (inside.Count > 0)
            {
                var before = Enumerable.Range(0, start).Reverse().Select(i => Strong(t[i])).FirstOrDefault(k => k is not null) ?? sos;
                way = before == sos ? sos : before;
            }
            else
            {
                continue;
            }
            foreach (var at in (int[])[start, end])
            {
                t[at] = way;
                for (var i = at + 1; i < runes.Length && original[i] == Kind.NSM; i++)
                    t[i] = way;
            }
        }
    }

    /// <summary>Opening brackets and their closing ones (Unicode's Bidi_Paired_Bracket).</summary>
    private static readonly Dictionary<int, int> Brackets = new()
    {
        ['('] = ')',
        ['['] = ']',
        ['{'] = '}',
        [0x0F3A] = 0x0F3B,
        [0x0F3C] = 0x0F3D,
        [0x169B] = 0x169C,
        [0x2045] = 0x2046,
        [0x207D] = 0x207E,
        [0x208D] = 0x208E,
        [0x2308] = 0x2309,
        [0x230A] = 0x230B,
        [0x2768] = 0x2769,
        [0x276A] = 0x276B,
        [0x276C] = 0x276D,
        [0x276E] = 0x276F,
        [0x2770] = 0x2771,
        [0x2772] = 0x2773,
        [0x2774] = 0x2775,
        [0x27C5] = 0x27C6,
        [0x27E6] = 0x27E7,
        [0x27E8] = 0x27E9,
        [0x27EA] = 0x27EB,
        [0x27EC] = 0x27ED,
        [0x27EE] = 0x27EF,
        [0x2983] = 0x2984,
        [0x2985] = 0x2986,
        [0x2987] = 0x2988,
        [0x2989] = 0x298A,
        [0x298B] = 0x298C,
        [0x2E22] = 0x2E23,
        [0x2E24] = 0x2E25,
        [0x2E26] = 0x2E27,
        [0x2E28] = 0x2E29,
        [0x3008] = 0x3009,
        [0x300A] = 0x300B,
        [0x300C] = 0x300D,
        [0x300E] = 0x300F,
        [0x3010] = 0x3011,
        [0x3014] = 0x3015,
        [0x3016] = 0x3017,
        [0x3018] = 0x3019,
        [0x301A] = 0x301B,
        [0xFE59] = 0xFE5A,
        [0xFE5B] = 0xFE5C,
        [0xFE5D] = 0xFE5E,
        [0xFF08] = 0xFF09,
        [0xFF3B] = 0xFF3D,
        [0xFF5B] = 0xFF5D,
        [0xFF5F] = 0xFF60,
        [0xFF62] = 0xFF63,
    };

    private static readonly HashSet<int> Closings = [.. Brackets.Values];

    /// <summary>The old angle brackets are the same as the CJK ones (canonically equivalent).</summary>
    private static int Canonical(int c) => c switch { 0x2329 => 0x3008, 0x232A => 0x3009, _ => c };

    private static Kind KindOf(Rune rune)
    {
        var c = rune.Value;
        // The marks that only say which way: left to right, right to left, Arabic letter.
        switch (c)
        {
            case 0x200E:
                return Kind.L;
            case 0x200F:
                return Kind.R;
            case 0x061C:
                return Kind.AL;
        }
        var category = Rune.GetUnicodeCategory(rune);
        // Before the marks: the Arabic number signs (U+0600 to U+0605) are format characters, but Arabic numbers.
        if (c is >= 0x0660 and <= 0x0669 or 0x066B or 0x066C or >= 0x0600 and <= 0x0605)
            return Kind.AN;
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
            return Kind.NSM;
        if (category == UnicodeCategory.DecimalDigitNumber)
            return Kind.EN;
        // Signs, terminators and separators, with their small and full-width forms.
        if (c is '+' or '-' or 0x2212 or 0xFB29 or 0xFE62 or 0xFE63 or 0xFF0B or 0xFF0D)
            return Kind.ES;
        if (c is '#' or '$' or '%' or 0x00B0 or 0x066A or 0x2030 or 0x2031 or >= 0x2032 and <= 0x2034 or 0xFE5F or 0xFE69 or 0xFE6A
                or 0xFF03 or 0xFF04 or 0xFF05 || category == UnicodeCategory.CurrencySymbol)
            return Kind.ET;
        if (c is ',' or '.' or ':' or '/' or 0x00A0 or 0x060C or 0x202F or 0xFE50 or 0xFE52 or 0xFE55 or 0xFF0C or 0xFF0E or 0xFF0F
                or 0xFF1A)
            return Kind.CS;
        if (Rune.IsLetter(rune) || category == UnicodeCategory.SpacingCombiningMark)
        {
            // Hebrew, N'Ko, Samaritan, Mandaic and the like: right to left; Arabic, Syriac, Thaana: Arabic letters.
            if (c is >= 0x0590 and <= 0x05FF or >= 0x07C0 and <= 0x085F or >= 0xFB1D and <= 0xFB4F or >= 0x10800 and <= 0x10FFF
                or >= 0x1E800 and <= 0x1EDFF)
                return Kind.R;
            if (c is >= 0x0600 and <= 0x08FF or >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF or >= 0x1EE00 and <= 0x1EEFF)
                return Kind.AL;
            return Kind.L;
        }
        return Kind.ON;
    }

    private static bool StaysWithLetterBefore(Rune rune) => Rune.GetUnicodeCategory(rune)
        is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
        or UnicodeCategory.Format or UnicodeCategory.ModifierSymbol;

    /// <summary>
    /// With the app's fallback, as Avalonia's text layout picks it: the first font if it has the letter, else the font
    /// before it if that has it, else the one the fallback finds. Without: the first font of the chain that has the
    /// letter, else the one before it, else one the system finds.
    /// </summary>
    private static SKTypeface FaceFor(int letter, SKTypeface? before)
    {
        if (_fallback is not null)
        {
            if (Primary.ContainsGlyph(letter))
                return Primary;
            return before?.ContainsGlyph(letter) == true ? before : FallbackFor(letter) ?? Primary;
        }
        var chain = Fonts;
        foreach (var face in chain.Length > 0 ? chain : [SystemPrimary.Value])
        {
            if (face.ContainsGlyph(letter))
                return face;
        }
        return before?.ContainsGlyph(letter) == true ? before : FallbackFor(letter) ?? Primary;
    }

    /// <summary>
    /// The system's bold font, unless that is a serif or monospaced one (DejaVu Serif on Linux, where Skia's build here
    /// can't ask for "sans-serif"); then the first common sans-serif family installed.
    /// </summary>
    private static readonly Lazy<SKTypeface> SystemPrimary = new(() =>
    {
        var system = SKTypeface.FromFamilyName(null, SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                     ?? SKTypeface.Default;
        if (!IsSerifOrMono(system.FamilyName))
            return system;
        foreach (var family in (string[])["DejaVu Sans", "Noto Sans", "Liberation Sans", "Ubuntu", "Cantarell", "Arial"])
        {
            if (SKFontManager.Default.MatchFamily(family, SKFontStyle.Bold) is { } face)
                return face;
        }
        return system;
    });

    private static bool IsSerifOrMono(string family) =>
        (family.Contains("Serif", StringComparison.OrdinalIgnoreCase) && !family.Contains("Sans", StringComparison.OrdinalIgnoreCase))
        || family.Contains("Mono", StringComparison.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<int, SKTypeface?> Fallbacks = new();

    /// <summary>
    /// A bold font for a letter the chain lacks, or null when no installed font has it. Skia's Linux build here finds
    /// fonts by family name but never by letter (MatchCharacter returns null even with a Korean font installed), so the
    /// installed families are looked through for one that has it, sans-serif ones first.
    /// </summary>
    private static SKTypeface? FallbackFor(int codePoint) => Fallbacks.GetOrAdd(codePoint, static letter =>
        _fallback is { } app ? Found(app(letter)) :
        SKFontManager.Default.MatchCharacter(null, SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright, null, letter)
        // On Windows and macOS the system has looked through every font already.
        ?? (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? null : Installed.Value.FirstOrDefault(face => face.ContainsGlyph(letter))));

    private static SKTypeface? Found((SKTypeface Face, bool MadeBold)? found)
    {
        if (found is not var (face, madeBold))
            return null;
        FoundFaces[face] = madeBold;
        return face;
    }

    /// <summary>
    /// The installed families' bold faces, sans-serif first: opened once, not for every letter looked for (with many
    /// fonts that took long, under the lock the window draws with).
    /// </summary>
    private static readonly Lazy<SKTypeface[]> Installed = new(() =>
        [.. SKFontManager.Default.FontFamilies.OrderBy(IsSerifOrMono).Select(family => SKFontManager.Default.MatchFamily(family, SKFontStyle.Bold))
            .OfType<SKTypeface>()]);
}
