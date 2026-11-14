// The fonts the layout engine can draw with, as FontResolver sees them: families of faces with their style, the
// characters they have and the font file behind them. SystemFonts lists the installed fonts; tests pass a list of
// their own.

using Filee.Engines.Rendering;

namespace Filee.Engines.Typesetting;

/// <summary>A collection of fonts: the installed ones (<see cref="SystemFonts"/>) or a fixed list.</summary>
internal interface IFontCollection
{
    /// <summary>
    /// The faces of the family with exactly this name (any name the system knows it by, such as a localized one);
    /// empty when no such family is installed.
    /// </summary>
    IReadOnlyList<FontFace> Family(string name);

    /// <summary>
    /// A face the system would draw <paramref name="codePoint"/> with when none of the named fonts has it (the
    /// platform's own fallback), or null.
    /// </summary>
    /// <param name="language">BCP 47 language of the text ("ja", "zh-Hant"), which picks between CJK fonts.</param>
    FontFace? ForCharacter(int codePoint, FontStyle style, string? language);
}

/// <summary>A requested style: weight 100–900 (400 regular, 700 bold) and italic.</summary>
internal readonly record struct FontStyle(int Weight = 400, bool Italic = false)
{
    /// <summary>400, upright. (A bare <c>new()</c> of this struct is all zeros: the defaults apply only to <c>new(...)</c> with arguments.)</summary>
    public static readonly FontStyle Regular = new(400);

    public bool Bold => Weight >= 600;

    /// <summary>A style from a bold / italic pair, as documents store it.</summary>
    public static FontStyle From(bool bold, bool italic) => new(bold ? 700 : 400, italic);
}

/// <summary>One face of a font: name, style, coverage and the file it comes from.</summary>
internal sealed class FontFace
{
    private readonly Func<int, bool> _has;
    private readonly Lazy<PageFont>? _font;

    /// <param name="has">Whether the face has a glyph for a code point.</param>
    /// <param name="open">Reads the font file (null for faces that are only matched, in tests).</param>
    public FontFace(string family, int weight, bool italic, Func<int, bool> has, Func<PageFont>? open = null)
    {
        Family = family;
        Weight = weight;
        Italic = italic;
        _has = has;
        _font = open is null ? null : new Lazy<PageFont>(open, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The family name the collection lists the face under.</summary>
    public string Family { get; }

    /// <summary>Weight class (OS/2 usWeightClass); a variable font's is that of its default instance.</summary>
    public int Weight { get; }

    public bool Italic { get; }

    /// <summary>
    /// A variable font (fvar). It is shaped, subset and drawn at its default instance: other instances need an
    /// instancer that applies the variation deltas, which Filee doesn't have. A weight the default instance doesn't
    /// have is synthesized like for any family without that weight.
    /// </summary>
    public bool Variable { get; init; }

    /// <summary>
    /// CFF2 (variable PostScript) outlines: the PDF writer can't subset them, so Skia embeds the font as a Type 3 font
    /// (still searchable). The resolver uses such a face only when no other font for its script is installed.
    /// </summary>
    public bool Cff2 { get; init; }

    /// <summary>True when the face has a glyph for <paramref name="codePoint"/>.</summary>
    public bool Has(int codePoint) => _has(codePoint);

    /// <summary>The font file, read on first use.</summary>
    /// <exception cref="InvalidOperationException">The face has no font file (made only for matching).</exception>
    /// <exception cref="InvalidDataException">The font file can't be read.</exception>
    public PageFont Font => _font?.Value ?? throw new InvalidOperationException($"{Family} has no font file.");

    public override string ToString() => $"{Family} {Weight}{(Italic ? " italic" : "")}";
}
