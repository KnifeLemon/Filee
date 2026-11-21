// The platform's fallback font for a character, asked as seldom as it can be. Each question is a native call into the
// font manager (DirectWrite, CoreText, fontconfig), and a document can hold thousands of characters none of its fonts
// has: a run of private-use or rare Han characters asked one by one each time a paragraph is laid out.
//
// - Answers are kept per character, style and language, "none" too (up to MaxAnswers; then only the rules below hold).
// - A face the platform gave for that style and language is tried first: the next character it has needs no question.
// - Code points nothing can draw (unassigned, surrogates, controls) are never asked about.
// - A block of 128 code points in which MissesPerBlock characters found nothing, and none found a face, is taken to have
//   nothing more (private-use planes, scripts no installed font covers).

using System.Collections.Concurrent;
using System.Globalization;

namespace Filee.Engines.Typesetting;

/// <summary>A cache over the platform's per-character fallback (<see cref="IFontCollection.ForCharacter"/>).</summary>
internal sealed class CharacterFallbacks(Func<int, FontStyle, string?, FontFace?> ask)
{
    /// <summary>Characters of one 128-code-point block that found nothing before the block is given up.</summary>
    internal const int MissesPerBlock = 4;

    /// <summary>Answers kept per character; past that only the faces found and the given-up blocks save questions.</summary>
    internal const int MaxAnswers = 1 << 16;

    private readonly ConcurrentDictionary<(int CodePoint, FontStyle Style, string Language), FontFace?> _answers = new();
    private readonly ConcurrentDictionary<(FontStyle Style, string Language), ConcurrentQueue<FontFace>> _faces = new();
    private readonly ConcurrentDictionary<(int Block, FontStyle Style, string Language), int> _misses = new();

    /// <summary>The face the platform draws <paramref name="codePoint"/> with, or null.</summary>
    public FontFace? Find(int codePoint, FontStyle style, string? language)
    {
        if (!MayHaveGlyph(codePoint))
            return null;
        var tag = language ?? "";
        if (_answers.TryGetValue((codePoint, style, tag), out var known))
            return known;
        var faces = _faces.GetOrAdd((style, tag), static _ => new ConcurrentQueue<FontFace>());
        foreach (var face in faces)
        {
            if (face.Has(codePoint))
                return Remember(codePoint, style, tag, face);
        }
        var block = (codePoint >> 7, style, tag);
        if (_misses.TryGetValue(block, out var misses) && misses >= MissesPerBlock)
            return null;

        var found = ask(codePoint, style, language);
        if (found is null)
            _misses.AddOrUpdate(block, 1, static (_, n) => n < 0 ? n : n + 1);
        else
        {
            _misses[block] = -1; // found something: the block stays open
            if (!faces.Contains(found))
                faces.Enqueue(found);
        }
        return Remember(codePoint, style, tag, found);
    }

    /// <summary>Forgets every answer (the installed fonts may have changed).</summary>
    public void Clear()
    {
        _answers.Clear();
        _faces.Clear();
        _misses.Clear();
    }

    private FontFace? Remember(int codePoint, FontStyle style, string language, FontFace? face)
    {
        if (_answers.Count < MaxAnswers)
            _answers.TryAdd((codePoint, style, language), face);
        return face;
    }

    /// <summary>False for code points no font is expected to draw: past U+10FFFF, unassigned, surrogates, controls.</summary>
    internal static bool MayHaveGlyph(int codePoint) =>
        (uint)codePoint <= 0x10FFFF && CharUnicodeInfo.GetUnicodeCategory(codePoint) is not
            (UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate or UnicodeCategory.Control);
}
