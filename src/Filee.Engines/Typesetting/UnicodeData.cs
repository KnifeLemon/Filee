// Unicode character properties the layout engine needs and .NET doesn't expose: Line_Break for line breaking
// (UAX #14), Bidi_Class and Bidi_Paired_Bracket for the bidirectional algorithm (UAX #9), Script for itemisation and shaping (UAX #24) and
// East_Asian_Width for CJK widths (UAX #11). The tables are generated from the Unicode Character Database by
// build/generate-unicode-tables.ps1 into UnicodeData.Generated.cs, at the Unicode version .NET uses, so these values
// and .NET's own (categories, grapheme clusters) describe the same characters.

using System.Text;

namespace Filee.Engines.Typesetting;

/// <summary>Unicode property lookups for any code point (unassigned ones get the UCD's defaults).</summary>
internal static partial class UnicodeData
{
    /// <summary>The Line_Break class of <paramref name="codePoint"/>, before UAX #14's tailoring (rule LB1).</summary>
    public static LineBreakClass GetLineBreak(int codePoint) => (LineBreakClass)Lookup(LineBreakStarts, LineBreakValues, codePoint);

    /// <summary>The Bidi_Class of <paramref name="codePoint"/>.</summary>
    public static BidiClass GetBidiClass(int codePoint) => (BidiClass)Lookup(BidiStarts, BidiValues, codePoint);

    /// <summary>The East_Asian_Width of <paramref name="codePoint"/>.</summary>
    public static EastAsianWidth GetEastAsianWidth(int codePoint) => (EastAsianWidth)Lookup(EastAsianWidthStarts, EastAsianWidthValues, codePoint);

    /// <summary>The Script of <paramref name="codePoint"/> (Common and Inherited included, not resolved).</summary>
    public static UnicodeScript GetScript(int codePoint) => (UnicodeScript)Lookup(ScriptStarts, ScriptValues, codePoint);

    /// <inheritdoc cref="GetLineBreak(int)"/>
    public static LineBreakClass GetLineBreak(Rune rune) => GetLineBreak(rune.Value);

    /// <inheritdoc cref="GetBidiClass(int)"/>
    public static BidiClass GetBidiClass(Rune rune) => GetBidiClass(rune.Value);

    /// <inheritdoc cref="GetEastAsianWidth(int)"/>
    public static EastAsianWidth GetEastAsianWidth(Rune rune) => GetEastAsianWidth(rune.Value);

    /// <inheritdoc cref="GetScript(int)"/>
    public static UnicodeScript GetScript(Rune rune) => GetScript(rune.Value);

    /// <summary>The ISO 15924 code of a script ("Latn", "Hang", "Zyyy" for Common), as HarfBuzz names it.</summary>
    public static string IsoCode(this UnicodeScript script) => ScriptCodes[(int)script];

    /// <summary>True for East Asian Wide and Fullwidth characters: one em wide in CJK typography.</summary>
    public static bool IsWide(int codePoint) => GetEastAsianWidth(codePoint) is EastAsianWidth.Wide or EastAsianWidth.Fullwidth;

    /// <summary>True for a strong right-to-left character (Bidi_Class R or AL).</summary>
    public static bool IsRightToLeft(int codePoint) => GetBidiClass(codePoint) is BidiClass.R or BidiClass.AL;

    /// <summary>
    /// The Bidi_Paired_Bracket of <paramref name="codePoint"/> and whether it opens; false for a code point that isn't a
    /// paired bracket.
    /// </summary>
    public static bool PairedBracket(int codePoint, out int pair, out bool opens)
    {
        var index = BracketCodes.BinarySearch(codePoint);
        (pair, opens) = index >= 0 ? (BracketPairs[index], BracketOpens[index]) : (0, false);
        return index >= 0;
    }

    /// <summary>
    /// The code point at <paramref name="index"/>: a surrogate pair as one, a lone surrogate as itself (it has its
    /// own properties: Line_Break SG).
    /// </summary>
    public static int CodePointAt(ReadOnlySpan<char> text, int index) =>
        Rune.DecodeFromUtf16(text[index..], out var rune, out _) == System.Buffers.OperationStatus.Done ? rune.Value : text[index];

    /// <summary>The value of the range that holds <paramref name="codePoint"/>; outside the code space 0.</summary>
    private static byte Lookup(ReadOnlySpan<int> starts, ReadOnlySpan<byte> values, int codePoint)
    {
        if ((uint)codePoint > 0x10FFFF)
            return 0;
        // The last range that starts at or before the code point. BinarySearch gives the complement of the first larger
        // start when there is no exact match.
        var index = starts.BinarySearch(codePoint);
        return values[index >= 0 ? index : ~index - 1];
    }
}
