// Line break opportunities by the Unicode line breaking algorithm (UAX #14, Unicode 16.0, rules LB1–LB31), with the
// tailorings documents ask for:
// - 한글 "어절": Hangul syllables and jamo stay together like letters of a word, so a Korean line breaks only where a
//   Latin one would (spaces, after hyphens ...). "글자" is UAX #14's own: a break between any two syllables.
// - Breaking Latin words anywhere (한글 breakLatinWord BREAK_WORD, Word without w:wordWrap): letters and digits of
//   Latin, Greek and Cyrillic text break like ideographs. Hyphenation needs dictionaries Filee doesn't have, so it
//   keeps words whole.
// - Characters a document forbids at the start or end of a line (Word w:noLineBreaksBefore / After) on top of the
//   East Asian rules UAX #14 already has (closing punctuation, small kana and iteration marks don't start a line).
//
// SA characters (Thai, Lao, Khmer, Myanmar): LB1 makes their marks join the letter before them and the letters AL, and
// the line may also break between the words WordDictionary finds in a run of them (these scripts don't put spaces
// between words), as Word breaks them. 한글 has no dictionary: in its 어절 setting a run without spaces is one word, in
// 글자 the line breaks between any two of its characters. A script without a dictionary breaks only at its spaces.

using System.Globalization;

namespace Filee.Engines.Typesetting;

/// <summary>What may happen between two characters.</summary>
internal enum LineBreak : byte
{
    /// <summary>The line must not break here.</summary>
    None,

    /// <summary>The line may break here.</summary>
    Allowed,

    /// <summary>The line must break here (after a paragraph or line separator, a line feed ...).</summary>
    Mandatory,
}

/// <summary>Tailorings of the line breaking rules.</summary>
/// <param name="KeepNonLatinWords">
/// Non-Latin text breaks only between words (한글 어절), not between characters (글자): Hangul, and as 한글 treats them
/// in that setting Chinese and Japanese characters, East Asian punctuation and quotation marks, and Thai, Lao, Khmer and
/// Myanmar text, so a run without spaces is one word (broken between characters only when it is longer than a line).
/// </param>
/// <param name="BreakLatinWords">Latin, Greek and Cyrillic words may break between any two letters.</param>
/// <param name="BreakNonLatinCharacters">
/// 한글's 글자 setting, given by the document: Thai, Lao, Khmer and Myanmar text breaks between any two characters, as
/// 한글 breaks it, not between dictionary words.
/// </param>
/// <param name="NotAtLineStart">Characters that may not start a line, besides UAX #14's.</param>
/// <param name="NotAtLineEnd">Characters that may not end a line, besides UAX #14's.</param>
internal sealed record LineBreakRules(
    bool KeepNonLatinWords = false,
    bool BreakLatinWords = false,
    bool BreakNonLatinCharacters = false,
    string? NotAtLineStart = null,
    string? NotAtLineEnd = null)
{
    public static readonly LineBreakRules Default = new();
}

/// <summary>Finds where lines may and must break (UAX #14).</summary>
internal static class LineBreaker
{
    private const int DottedCircle = 0x25CC;

    /// <summary>
    /// What may happen before each UTF-16 index of <paramref name="text"/>: <c>result[i]</c> is the break between
    /// <c>text[i - 1]</c> and <c>text[i]</c>, <c>result[text.Length]</c> the end of the text (always mandatory) and
    /// <c>result[0]</c> the start (never). Indices inside a surrogate pair or a grapheme's marks are <see cref="LineBreak.None"/>.
    /// </summary>
    public static LineBreak[] Breaks(string text, LineBreakRules? rules = null)
    {
        rules ??= LineBreakRules.Default;
        var result = new LineBreak[text.Length + 1];
        if (text.Length == 0)
            return result;
        result[text.Length] = LineBreak.Mandatory;

        var units = Units(text, rules);
        for (var j = 1; j < units.Count; j++)
            result[units[j].Start] = Between(units, j);
        Words(text, units, rules, result);

        if (rules.NotAtLineStart is { Length: > 0 } || rules.NotAtLineEnd is { Length: > 0 })
        {
            foreach (var unit in units)
            {
                if (unit.Start > 0 && result[unit.Start] == LineBreak.Allowed && Contains(rules.NotAtLineStart, text, unit.Start))
                    result[unit.Start] = LineBreak.None;
            }
            for (var j = 0; j + 1 < units.Count; j++)
            {
                var next = units[j + 1].Start;
                if (result[next] == LineBreak.Allowed && Contains(rules.NotAtLineEnd, text, next - units[j].Length, units[j].Length))
                    result[next] = LineBreak.None;
            }
        }
        return result;
    }

    /// <summary>Breaks between the dictionary's words in runs of SA letters of one script, or as 한글 breaks them.</summary>
    private static void Words(string text, List<Unit> units, LineBreakRules rules, LineBreak[] result)
    {
        if (rules.KeepNonLatinWords)
            return;
        for (var j = 0; j < units.Count;)
        {
            var script = SaScript(text, units[j]);
            var end = j + 1;
            while (script is not null && end < units.Count && SaScript(text, units[end]) == script)
                end++;
            if (end - j > 1 && rules.BreakNonLatinCharacters)
            {
                for (var k = j + 1; k < end; k++)
                {
                    if (result[units[k].Start] == LineBreak.None)
                        result[units[k].Start] = LineBreak.Allowed;
                }
            }
            else if (end - j > 1 && WordDictionary.For(script!.Value) is { } dictionary)
            {
                var starts = new HashSet<int>(units.Skip(j).Take(end - j).Select(u => u.Start));
                var last = units[end - 1];
                foreach (var boundary in dictionary.Boundaries(text, units[j].Start, last.Start + last.Length, starts.Contains))
                {
                    if (result[boundary] == LineBreak.None)
                        result[boundary] = LineBreak.Allowed;
                }
            }
            j = end;
        }
    }

    /// <summary>The script of a unit whose character is an SA letter, or null.</summary>
    private static UnicodeScript? SaScript(string text, Unit unit)
    {
        var codePoint = UnicodeData.CodePointAt(text, unit.Start);
        return UnicodeData.GetLineBreak(codePoint) == LineBreakClass.SA ? UnicodeData.GetScript(codePoint) : null;
    }

    /// <summary>Whether the code point at <paramref name="index"/> (or the unit ending there) is in <paramref name="set"/>.</summary>
    private static bool Contains(string? set, string text, int index, int length = 0)
    {
        if (string.IsNullOrEmpty(set))
            return false;
        var codePoint = UnicodeData.CodePointAt(text, length > 0 ? LastCodePointStart(text, index, length) : index);
        return set.EnumerateRunes().Any(r => r.Value == codePoint);
    }

    private static int LastCodePointStart(string text, int start, int length) =>
        length >= 2 && char.IsSurrogatePair(text[start + length - 2], text[start + length - 1]) ? start + length - 2 : start + length - 1;

    // ───────────────────────── Units ─────────────────────────

    /// <summary>
    /// A character with the combining marks LB9 attaches to it: the rules see its class only. <c>Length</c> covers the
    /// marks, so no break falls between them.
    /// </summary>
    private record struct Unit(int Start, int Length, LineBreakClass Class, bool EastAsian, bool InitialQuote, bool FinalQuote,
        bool PictographicUnassigned, bool Dotted, bool Hyphen2010, bool EndsWithZwj);

    private static List<Unit> Units(string text, LineBreakRules rules)
    {
        var units = new List<Unit>(text.Length);
        for (var i = 0; i < text.Length;)
        {
            var codePoint = UnicodeData.CodePointAt(text, i);
            var size = codePoint > 0xFFFF ? 2 : 1;
            var cls = Resolve(codePoint, rules);
            // LB9: X (CM | ZWJ)* is X, for any X but BK, CR, LF, NL, SP and ZW. LB10: other marks are AL.
            if (cls is LineBreakClass.CM or LineBreakClass.ZWJ && units.Count > 0 &&
                units[^1].Class is not (LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL or LineBreakClass.SP or LineBreakClass.ZW))
            {
                var last = units[^1];
                units[^1] = last with { Length = last.Length + size, EndsWithZwj = cls == LineBreakClass.ZWJ };
            }
            else
            {
                var isZwj = cls == LineBreakClass.ZWJ;
                if (cls is LineBreakClass.CM or LineBreakClass.ZWJ)
                    cls = LineBreakClass.AL;
                var category = CharUnicodeInfo.GetUnicodeCategory(codePoint);
                units.Add(new Unit(i, size, cls,
                    UnicodeData.GetEastAsianWidth(codePoint) is EastAsianWidth.Fullwidth or EastAsianWidth.Wide or EastAsianWidth.Halfwidth,
                    category == UnicodeCategory.InitialQuotePunctuation,
                    category == UnicodeCategory.FinalQuotePunctuation,
                    category == UnicodeCategory.OtherNotAssigned && IsPictographicBlock(codePoint),
                    codePoint == DottedCircle,
                    codePoint == 0x2010,
                    isZwj));
            }
            i += size;
        }
        return units;
    }

    /// <summary>LB1 and the tailorings: the class the rules work with.</summary>
    private static LineBreakClass Resolve(int codePoint, LineBreakRules rules)
    {
        var cls = UnicodeData.GetLineBreak(codePoint);
        cls = cls switch
        {
            LineBreakClass.AI or LineBreakClass.SG or LineBreakClass.XX => LineBreakClass.AL,
            LineBreakClass.SA => CharUnicodeInfo.GetUnicodeCategory(codePoint) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                ? LineBreakClass.CM
                : LineBreakClass.AL,
            LineBreakClass.CJ => LineBreakClass.NS,
            _ => cls,
        };
        if (rules.KeepNonLatinWords && (cls is LineBreakClass.H2 or LineBreakClass.H3 or LineBreakClass.JL or LineBreakClass.JV or LineBreakClass.JT
                                        or LineBreakClass.QU || EastAsian(codePoint, cls)))
            return LineBreakClass.AL;
        if (rules.BreakLatinWords && cls is LineBreakClass.AL or LineBreakClass.NU &&
            UnicodeData.GetScript(codePoint) is UnicodeScript.Latin or UnicodeScript.Greek or UnicodeScript.Cyrillic or UnicodeScript.Common &&
            (cls == LineBreakClass.NU || char.IsLetter(char.ConvertFromUtf32(codePoint), 0)))
            return LineBreakClass.ID;
        return cls;
    }

    /// <summary>Chinese and Japanese characters and East Asian punctuation (a word's letters in 한글's 어절 setting).</summary>
    private static bool EastAsian(int codePoint, LineBreakClass cls) =>
        cls is LineBreakClass.ID or LineBreakClass.CL or LineBreakClass.OP or LineBreakClass.NS or LineBreakClass.EX or LineBreakClass.IS
        && (codePoint is >= 0x3000 and <= 0x303F or >= 0xFF00 and <= 0xFFEF
            || UnicodeData.GetScript(codePoint) is UnicodeScript.Han or UnicodeScript.Hiragana or UnicodeScript.Katakana
                or UnicodeScript.KatakanaOrHiragana or UnicodeScript.Bopomofo);

    /// <summary>Blocks whose unassigned code points are Extended_Pictographic (reserved for emoji).</summary>
    private static bool IsPictographicBlock(int codePoint) => codePoint is >= 0x1F000 and <= 0x1FAFF or >= 0x1FC00 and <= 0x1FFFD;

    // ───────────────────────── Rules ─────────────────────────

    /// <summary>The break between <c>units[j - 1]</c> and <c>units[j]</c>, by rules LB4–LB31.</summary>
    private static LineBreak Between(List<Unit> units, int j)
    {
        var a = units[j - 1];
        var b = units[j];
        var A = a.Class;
        var B = b.Class;
        var next = j + 1 < units.Count ? units[j + 1].Class : (LineBreakClass?)null;

        // LB4, LB5: hard line breaks.
        if (A == LineBreakClass.BK)
            return LineBreak.Mandatory;
        if (A == LineBreakClass.CR && B == LineBreakClass.LF)
            return LineBreak.None;
        if (A is LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL)
            return LineBreak.Mandatory;
        // LB6, LB7.
        if (B is LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL or LineBreakClass.SP or LineBreakClass.ZW)
            return LineBreak.None;

        // The last unit before the spaces that end at a (LB8, LB14–LB17 look through them).
        var k = j - 1;
        while (k > 0 && units[k].Class == LineBreakClass.SP)
            k--;
        var beforeSpaces = units[k];

        // LB8: ZW SP* ÷. LB8a: ZWJ ×.
        if (beforeSpaces.Class == LineBreakClass.ZW)
            return LineBreak.Allowed;
        if (a.EndsWithZwj)
            return LineBreak.None;
        // LB11: × WJ, WJ ×. LB12: GL ×. LB12a: [^SP BA HY] × GL.
        if (A == LineBreakClass.WJ || B == LineBreakClass.WJ || A == LineBreakClass.GL)
            return LineBreak.None;
        if (B == LineBreakClass.GL && A is not (LineBreakClass.SP or LineBreakClass.BA or LineBreakClass.HY))
            return LineBreak.None;
        // LB13: × CL, × CP, × EX, × SY.
        if (B is LineBreakClass.CL or LineBreakClass.CP or LineBreakClass.EX or LineBreakClass.SY)
            return LineBreak.None;
        // LB14: OP SP* ×.
        if (beforeSpaces.Class == LineBreakClass.OP)
            return LineBreak.None;
        // LB15a: (sot | BK | CR | LF | NL | OP | QU | GL | SP | ZW) [Pi & QU] SP* ×.
        if (beforeSpaces is { Class: LineBreakClass.QU, InitialQuote: true } &&
            (k == 0 || units[k - 1].Class is LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL or LineBreakClass.OP
                or LineBreakClass.QU or LineBreakClass.GL or LineBreakClass.SP or LineBreakClass.ZW))
            return LineBreak.None;
        // LB15b: × [Pf & QU] (SP | GL | WJ | CL | QU | CP | EX | IS | SY | BK | CR | LF | NL | ZW | eot).
        if (b is { Class: LineBreakClass.QU, FinalQuote: true } &&
            next is null or LineBreakClass.SP or LineBreakClass.GL or LineBreakClass.WJ or LineBreakClass.CL or LineBreakClass.QU or LineBreakClass.CP
                or LineBreakClass.EX or LineBreakClass.IS or LineBreakClass.SY or LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF
                or LineBreakClass.NL or LineBreakClass.ZW)
            return LineBreak.None;
        // LB15c: SP ÷ IS NU. LB15d: × IS.
        if (A == LineBreakClass.SP && B == LineBreakClass.IS && next == LineBreakClass.NU)
            return LineBreak.Allowed;
        if (B == LineBreakClass.IS)
            return LineBreak.None;
        // LB16: (CL | CP) SP* × NS. LB17: B2 SP* × B2.
        if (beforeSpaces.Class is LineBreakClass.CL or LineBreakClass.CP && B == LineBreakClass.NS)
            return LineBreak.None;
        if (beforeSpaces.Class == LineBreakClass.B2 && B == LineBreakClass.B2)
            return LineBreak.None;
        // LB18: SP ÷.
        if (A == LineBreakClass.SP)
            return LineBreak.Allowed;
        // LB19: × [QU - Pi], [QU - Pf] ×.
        if (B == LineBreakClass.QU && !b.InitialQuote || A == LineBreakClass.QU && !a.FinalQuote)
            return LineBreak.None;
        // LB19a: quotation marks bind unless East Asian text is on both sides.
        if (B == LineBreakClass.QU && (!a.EastAsian || j + 1 >= units.Count || !units[j + 1].EastAsian))
            return LineBreak.None;
        if (A == LineBreakClass.QU && (!b.EastAsian || j < 2 || !units[j - 2].EastAsian))
            return LineBreak.None;
        // LB20: ÷ CB, CB ÷.
        if (A == LineBreakClass.CB || B == LineBreakClass.CB)
            return LineBreak.Allowed;
        // LB20a: (sot | BK | CR | LF | NL | SP | ZW | CB | GL) (HY | ‐) × AL.
        if ((A == LineBreakClass.HY || a.Hyphen2010) && B == LineBreakClass.AL &&
            (j < 2 || units[j - 2].Class is LineBreakClass.BK or LineBreakClass.CR or LineBreakClass.LF or LineBreakClass.NL or LineBreakClass.SP
                or LineBreakClass.ZW or LineBreakClass.CB or LineBreakClass.GL))
            return LineBreak.None;
        // LB21: × BA, × HY, × NS, BB ×.
        if (B is LineBreakClass.BA or LineBreakClass.HY or LineBreakClass.NS || A == LineBreakClass.BB)
            return LineBreak.None;
        // LB21a: HL (HY | [BA - EastAsian]) × [^HL].
        if (j >= 2 && units[j - 2].Class == LineBreakClass.HL && (A == LineBreakClass.HY || A == LineBreakClass.BA && !a.EastAsian) && B != LineBreakClass.HL)
            return LineBreak.None;
        // LB21b: SY × HL. LB22: × IN.
        if (A == LineBreakClass.SY && B == LineBreakClass.HL || B == LineBreakClass.IN)
            return LineBreak.None;
        // LB23: (AL | HL) × NU, NU × (AL | HL).
        if (IsLetter(A) && B == LineBreakClass.NU || A == LineBreakClass.NU && IsLetter(B))
            return LineBreak.None;
        // LB23a: PR × (ID | EB | EM), (ID | EB | EM) × PO.
        if (A == LineBreakClass.PR && B is LineBreakClass.ID or LineBreakClass.EB or LineBreakClass.EM ||
            A is LineBreakClass.ID or LineBreakClass.EB or LineBreakClass.EM && B == LineBreakClass.PO)
            return LineBreak.None;
        // LB24: (PR | PO) × (AL | HL), (AL | HL) × (PR | PO).
        if (A is LineBreakClass.PR or LineBreakClass.PO && IsLetter(B) || IsLetter(A) && B is LineBreakClass.PR or LineBreakClass.PO)
            return LineBreak.None;
        if (Numeric(units, j, next))
            return LineBreak.None;
        // LB26: Korean syllable blocks.
        if (A == LineBreakClass.JL && B is LineBreakClass.JL or LineBreakClass.JV or LineBreakClass.H2 or LineBreakClass.H3 ||
            A is LineBreakClass.JV or LineBreakClass.H2 && B is LineBreakClass.JV or LineBreakClass.JT ||
            A is LineBreakClass.JT or LineBreakClass.H3 && B == LineBreakClass.JT)
            return LineBreak.None;
        // LB27: Korean syllable blocks with prefixes and postfixes.
        if (IsKorean(A) && B == LineBreakClass.PO || A == LineBreakClass.PR && IsKorean(B))
            return LineBreak.None;
        // LB28: (AL | HL) × (AL | HL).
        if (IsLetter(A) && IsLetter(B))
            return LineBreak.None;
        // LB28a: Brahmic orthographic syllables (Aksara).
        if (A == LineBreakClass.AP && IsAksara(b) ||
            IsAksara(a) && B is LineBreakClass.VF or LineBreakClass.VI ||
            j >= 2 && IsAksara(units[j - 2]) && A == LineBreakClass.VI && (B == LineBreakClass.AK || b.Dotted) ||
            IsAksara(a) && IsAksara(b) && next == LineBreakClass.VF)
            return LineBreak.None;
        // LB29: IS × (AL | HL).
        if (A == LineBreakClass.IS && IsLetter(B))
            return LineBreak.None;
        // LB30: (AL | HL | NU) × [OP - EastAsian], [CP - EastAsian] × (AL | HL | NU).
        if ((IsLetter(A) || A == LineBreakClass.NU) && B == LineBreakClass.OP && !b.EastAsian ||
            A == LineBreakClass.CP && !a.EastAsian && (IsLetter(B) || B == LineBreakClass.NU))
            return LineBreak.None;
        // LB30a: regional indicators pair up.
        if (A == LineBreakClass.RI && B == LineBreakClass.RI)
        {
            var count = 0;
            for (var i = j - 1; i >= 0 && units[i].Class == LineBreakClass.RI; i--)
                count++;
            if (count % 2 == 1)
                return LineBreak.None;
        }
        // LB30b: EB × EM, [Extended_Pictographic & Cn] × EM.
        if (B == LineBreakClass.EM && (A == LineBreakClass.EB || a.PictographicUnassigned))
            return LineBreak.None;
        // LB31.
        return LineBreak.Allowed;
    }

    /// <summary>LB25: no break inside numbers such as "$(12.5)", "-3", "1,000%".</summary>
    private static bool Numeric(List<Unit> units, int j, LineBreakClass? next)
    {
        var A = units[j - 1].Class;
        var B = units[j].Class;
        if (B is LineBreakClass.PO or LineBreakClass.PR or LineBreakClass.NU)
        {
            // NU (SY | IS)* (CL | CP)? × (PO | PR) and NU (SY | IS)* × NU.
            var i = j - 1;
            if (B != LineBreakClass.NU && units[i].Class is LineBreakClass.CL or LineBreakClass.CP)
                i--;
            while (i >= 0 && units[i].Class is LineBreakClass.SY or LineBreakClass.IS)
                i--;
            if (i >= 0 && units[i].Class == LineBreakClass.NU)
                return true;
        }
        if (A is LineBreakClass.PO or LineBreakClass.PR)
        {
            // (PO | PR) × NU, (PO | PR) × OP NU, (PO | PR) × OP IS NU.
            if (B == LineBreakClass.NU)
                return true;
            if (B == LineBreakClass.OP && (next == LineBreakClass.NU ||
                                           next == LineBreakClass.IS && j + 2 < units.Count && units[j + 2].Class == LineBreakClass.NU))
                return true;
        }
        // HY × NU, IS × NU.
        return A is LineBreakClass.HY or LineBreakClass.IS && B == LineBreakClass.NU;
    }

    private static bool IsLetter(LineBreakClass cls) => cls is LineBreakClass.AL or LineBreakClass.HL;

    private static bool IsKorean(LineBreakClass cls) =>
        cls is LineBreakClass.JL or LineBreakClass.JV or LineBreakClass.JT or LineBreakClass.H2 or LineBreakClass.H3;

    private static bool IsAksara(Unit unit) => unit.Class is LineBreakClass.AK or LineBreakClass.AS || unit.Dotted;
}
