// Equations for the layout engine (GitHub Project 4 item "Layout engine: equations"): the equation tree (MathNode,
// read from 한글's equation script by Hwp/Hwpx/EquationParser or from Word math by Docx/OmmlReader) laid out into a
// box with its width, height above the baseline and depth below it, drawn with the document's fonts.
//
// Text: variables (Latin letters) italic, numbers, function names, Greek letters, symbols and rm text upright, bold
// where asked; in 한글's equation fonts when installed (HancomEQN, HYhwpEQ), else Times New Roman or another serif,
// with the font fallback for characters a font lacks. Word's and TeX's equations are set in the math font instead (as
// Word sets them in Cambria Math), their variables and lowercase Greek letters in its math italic alphabet (U+1D434 …),
// bold ones in its bold alphabets; text (\text, quoted) stays in the text font. Relations (=, ≤, ∈, →, …) get 0.13 of the size on each side,
// binary operators (+, −, ×, ∪, …) a tenth, unless they start the row (a sign); - is drawn as the minus sign.
//
// Structure (sizes as fractions of the equation's size): scripts at 0.7 of their base, raised to 0.42 or lowered by
// 0.18 (further for taller bases); fractions on the math axis (0.27 above the baseline) with a rule of 0.05; roots with
// a radical sign over their body; brackets of LEFT … RIGHT as tall as their content; sums, products and unions at 1.4
// with their limits above and below, integrals at 1.6 with them to the right; matrices, cases and piles centred on the
// axis, the lines of a Word equation array lined up at their & marks; accents drawn over (or under) their content;
// stacked content (\overset, Word's m:limUpp) centred over or under its base, an arrow drawn as long as its text.
//
// That is how 한글 sets an equation (MathMode.Fixed). Word's and TeX's equations follow TeX's styles instead
// (MathMode.Display / Text): an inline one (and a display fraction's parts) sets fractions' parts at script size and
// large operators little larger than the text with their limits at the side; scripts of scripts shrink less; matrix
// rows are evenly spaced and closer, and brackets fall a little short of their content, as Word draws them (#356).
// A display formula wider than its column is broken into lines before its binary and relation operators (inside
// brackets too wide for a line as well, each bracket as tall as all it holds), the lines
// after the first indented (Word's defaults: m:brkBin "before", m:wrapIndent 1 inch), the whole centred as a group; a
// Word document's own m:mathPr settings (operators after the break or on both lines, the signs of a repeated minus, the
// indent, continuation lines to the right) are followed. An inline formula comes in pieces split at the same
// top-level operators under the same m:brkBin and m:brkBinSub (Pieces), which the paragraph's lines break between as
// Word breaks inline math across lines; a line ending at a piece leaves out the room before the next operator.
//
// Brackets and radical signs come from a math font when one is installed (Cambria Math on Windows, STIX Two Math on
// macOS, Latin Modern Math, DejaVu Math TeX Gyre ... on Linux: a font with an OpenType MATH table, see MathTable): the
// glyph itself, a taller variant of it, or for taller content an assembly of its parts, as Word draws them; so do a
// display formula's large operators (the font's display ∑ and ∫). Without one, a bracket no taller than a letter is
// the text font's own, and taller ones are drawn: round brackets and braces heavy in the middle and thin at the ends;
// the text is in the text font and large operators are its glyphs scaled up.

using System.Text;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Rendering;
using SkiaSharp;

namespace Filee.Engines.Typesetting;

/// <summary>
/// Laid out math: <see cref="Paint"/> draws it with its origin (left edge, baseline) at (x, y), scaled across by the
/// last argument (to fit a box 한글 laid the equation out narrower).
/// </summary>
internal sealed class EquationBox(float width, float ascent, float descent, Action<PageContent, float, float, float> paint)
{
    public static readonly EquationBox Empty = new(0, 0, 0, (_, _, _, _) => { });

    public float Width { get; } = width;
    public float Ascent { get; } = ascent;
    public float Descent { get; } = descent;
    public float Height => Ascent + Descent;
    public Action<PageContent, float, float, float> Paint { get; } = paint;
}

/// <summary>
/// A piece of an inline formula that a line of text may break after (MathLayout.Pieces): <paramref name="Box"/> as it
/// sits beside the next piece, <paramref name="End"/> as it ends a line (without the room before the next piece, or
/// with the operator Word repeats there), <paramref name="Start"/> as it starts a line when that differs (the second
/// sign of a repeated subtraction), else null; <paramref name="Both"/> as it starts and ends a line alone on it when
/// that differs from <paramref name="End"/> (the second sign at its start, its end as <paramref name="End"/> has it).
/// </summary>
internal sealed record MathPiece(EquationBox Box, EquationBox End, EquationBox? Start = null, EquationBox? Both = null);

/// <summary>How the parts of a formula are sized.</summary>
internal enum MathMode
{
    /// <summary>한글's equations: fractions, operators and matrices at the size of the equation wherever they are.</summary>
    Fixed,

    /// <summary>
    /// A display formula of Word (m:oMathPara) or TeX ($$…$$): large operators with limits above and below; the parts
    /// of a fraction are in text style.
    /// </summary>
    Display,

    /// <summary>
    /// An inline formula of Word (m:oMath among the text, also when tabs centre it) or TeX ($…$), and the parts of a
    /// display fraction: a fraction's parts in script size, large operators little larger than the text with their
    /// limits at the side.
    /// </summary>
    Text,
}

internal sealed class MathLayout(FontResolver fonts, Shaper shaper)
{
    private const float ScriptScale = 0.7f;

    /// <summary>The math axis (fraction bars, the middle of brackets and equation arrays) above the baseline, of the size.</summary>
    internal const float Axis = 0.27f;

    private const float Rule = 0.05f;
    private const float Gap = 0.12f;

    /// <summary>The room between the rows of Word's and TeX's matrices, equation arrays and cases, of the size.</summary>
    private const float RowGap = 0.16f;

    /// <summary>한글's equation fonts first, then common serif fonts.</summary>
    private static readonly string[] Families = ["HancomEQN", "HYhwpEQ", "Times New Roman", "Liberation Serif", "Tinos", "Noto Serif", "DejaVu Serif"];

    /// <summary>Fonts with an OpenType MATH table for brackets and radical signs: Word's first, then common free ones.</summary>
    private static readonly string[] MathFamilies =
    [
        "Cambria Math", "STIX Two Math", "Latin Modern Math", "STIX Math", "XITS Math", "Libertinus Math", "TeX Gyre Termes Math",
        "TeX Gyre Pagella Math", "DejaVu Math TeX Gyre", "Asana Math", "Noto Sans Math",
    ];

    private static readonly HashSet<string> Relations =
    [
        "=", "<", ">", "≤", "≥", "≠", "≈", "≡", "≅", "∼", "≃", "∝", "≪", "≫", "∈", "∉", "∋", "⊂", "⊃", "⊆", "⊇", "⊄", "⊅",
        "→", "←", "↔", "⇒", "⇐", "⇔", "↦", "⊥", "∥", "≍", "≐", "≺", "≻", ":",
        // TeX's further relations and arrows (LatexMath)
        "⟶", "⟵", "⟷", "⟹", "⟸", "⟺", "⟼", "↪", "↩", "⇌", "⇋", "↑", "↓", "↕", "⇑", "⇓", "⇕", "↗", "↘", "↖", "↙", "⇀", "⇁",
        "↼", "↽", "⇉", "⇇", "↠", "⇝", "⊨", "⊢", "⊣", "∣", "∤", "∦", "⊊", "⊋", "⊈", "⊉", "≲", "≳", "⪯", "⪰", "⩽", "⩾", "≦",
        "≧", "≢", "≰", "≱", "≮", "≯", "≁", "≇", "≉", "∌", "⊏", "⊐", "⊑", "⊒", "≔", "≕", "≜", "≊", "≑", "⋈", "≶", "≷", "⋘", "⋙",
    ];

    private static readonly HashSet<string> Binary = ["+", "-", "−", "±", "∓", "×", "÷", "·", "∘", "∪", "∩", "∧", "∨", "⊕", "⊖", "⊗", "⊙", "∗", "⋆", "⊔", "⊓", "\\", "/",
        "∖", "⊎", "⋄", "≀", "⨿", "⊘", "◁", "▷", "⋉", "⋊", "⊞", "⊟", "⊠", "⊡", "∔", "⊼", "⊻", "⋎", "⋏"];

    /// <summary>
    /// Levels laid out, above what the readers build (EquationParser.MaxDepth, OmmlReader.MaxDepth): content deeper
    /// than this is left out rather than recursed into, as a stack overflow would end the app.
    /// </summary>
    private const int MaxDepth = 256;

    private readonly Dictionary<FontStyle, ResolvedFont?> _primary = [];
    private (PageFont Font, MathTable Table)? _mathFont;
    private ResolvedFont? _mathFace;
    private bool _mathFontLooked;
    private int _depth;

    /// <summary>
    /// The style text takes: italic Latin letters unless upright (rm), bold when asked; <c>Mode</c> and <c>Level</c>
    /// (0 for the formula, 1 for scripts, 2 for scripts of scripts) size Word's and TeX's math (see <see cref="MathMode"/>).
    /// </summary>
    private readonly record struct Style(float Size, SKColor Color, bool Upright, bool Bold, MathMode Mode = MathMode.Fixed, int Level = 0)
    {
        public Style Smaller(float scale) => this with { Size = Size * scale };

        /// <summary>Word and TeX: a display formula's parts (a fraction's, a matrix cell) are in text style.</summary>
        public bool Tex => Mode != MathMode.Fixed;

        /// <summary>
        /// The style of a script (and of a fraction's parts in text style): 0.7 of the size; Word and TeX shrink a
        /// script of a script less and stop there (Cambria Math's script sizes are 73% and 60%).
        /// </summary>
        public Style Script() => Mode switch
        {
            MathMode.Fixed => Smaller(ScriptScale),
            _ => this with { Size = Size * (Level switch { 0 => ScriptScale, 1 => 0.82f, _ => 1 }), Mode = MathMode.Text, Level = Math.Min(Level + 1, 2) },
        };
    }

    /// <summary>
    /// Lays out <paramref name="node"/> at <paramref name="size"/> points in <paramref name="color"/>, as a 한글
    /// equation (<see cref="MathMode.Fixed"/>) or as Word or TeX set a display or an inline formula.
    /// </summary>
    public EquationBox Layout(MathNode node, float size, SKColor color, MathMode mode = MathMode.Fixed) => Lay(node, new Style(size, color, false, false, mode));

    /// <summary>
    /// Lays out the lines of an equation array as <see cref="Layout(MathNode, float, SKColor, MathMode)"/> does, with
    /// where each line's baseline is (down from the array's) in <paramref name="baselines"/>.
    /// </summary>
    public EquationBox Lines(MathPile pile, float size, SKColor color, List<float> baselines, MathMode mode = MathMode.Fixed) =>
        Pile(pile.Rows, pile.Align, new Style(size, color, false, false, mode), baselines);

    /// <summary>
    /// The width <paramref name="node"/> takes as an item in the middle of a row, operators with the room on their
    /// sides (for breaking a long equation into lines).
    /// </summary>
    /// <param name="mode">How the equation is set: as Word's display equations (DocxReader breaks those) by default.</param>
    public float ItemWidth(MathNode node, float size, MathMode mode = MathMode.Display)
    {
        var (before, after) = Spacing(node, new MathText("x"), new Style(size, SKColors.Black, false, false, mode));
        return before + Layout(node, size, SKColors.Black, mode).Width + after;
    }

    /// <summary>
    /// <see cref="Layout(MathNode, float, SKColor, MathMode)"/>, with a display formula wider than
    /// <paramref name="width"/> broken into lines before its binary and relation operators as Word breaks one: the
    /// lines after the first indented by an inch (a third of the width at most) and left-aligned with each other in the
    /// box, which the paragraph centres. An inline formula, or one without such an operator between its parts, stays on
    /// one line. <paramref name="wrap"/> holds the document's own rules (Word's m:mathPr); null for Word's defaults.
    /// </summary>
    public EquationBox Layout(MathNode node, float size, SKColor color, MathMode mode, float width, HMathWrap? wrap = null)
    {
        var box = Layout(node, size, color, mode);
        if (mode != MathMode.Display || !(width > 0) || box.Width <= width)
            return box;
        return Wrapped(node, new Style(size, color, false, false, mode), width, wrap ?? DefaultWrap) ?? box;
    }

    /// <summary>Word's defaults: break before binary operators, the lines after the first indented by an inch.</summary>
    private static readonly HMathWrap DefaultWrap = new();

    private EquationBox Lay(MathNode node, Style style)
    {
        if (_depth >= MaxDepth)
            return EquationBox.Empty;
        _depth++;
        try
        {
            return LayNode(node, style);
        }
        finally
        {
            _depth--;
        }
    }

    private EquationBox LayNode(MathNode node, Style style) => node switch
    {
        MathRow row => Row(row.Items, style),
        MathText or MathNumber or MathSymbol or MathCommand or MathQuoted or MathFunction => Atom(node, style),
        MathSpace space => new EquationBox((float)space.Width * 0.5f * style.Size, 0, 0, (_, _, _, _) => { }),
        MathFraction fraction => Fraction(fraction, style),
        MathRoot root => Root(root, style),
        MathScripts scripts => Scripts(Lay(scripts.Base, style), scripts.Sub, scripts.Sup, style, italicBase: scripts.Base is MathText),
        MathStack stack => Stack(stack, style),
        MathBigOperator big => BigOperator(big, style),
        MathLimit limit => Limit(limit, style),
        MathMatrix matrix => Matrix(matrix, style),
        MathCases cases => Cases(cases, style),
        MathPile pile => Pile(pile.Rows, pile.Align, style),
        MathBrackets brackets => Brackets(brackets.Left, Lay(brackets.Body, style), brackets.Right, style),
        MathAccent accent => Accent(accent.Command, Lay(accent.Body, style), style),
        MathStyled styled => Lay(styled.Body, styled.Command switch
        {
            "rm" => style with { Upright = true },
            "it" => style with { Upright = false },
            "bold" => style with { Bold = true },
            _ => style,
        }),
        _ => EquationBox.Empty,
    };

    // ───────────────────────── Text ─────────────────────────

    /// <summary>
    /// A letter, number, symbol, function name or text (kept out of <see cref="LayNode"/>, whose frame each level of a
    /// deep equation takes again).
    /// </summary>
    private EquationBox Atom(MathNode node, Style style) => node switch
    {
        MathText text => Text(text.Text, style, italic: !style.Upright && (text.Text.All(char.IsAsciiLetter) || style.Tex && IsSmallGreek(text.Text))),
        MathNumber number => Text(number.Digits, style, italic: false),
        MathSymbol symbol => Text(symbol.Text == "-" ? "−" : symbol.Text, style, italic: false),
        // Word and TeX lean lowercase Greek letters like Latin ones; capitals stay upright.
        MathCommand command => Text(command.Glyph, style, italic: style.Tex && !style.Upright && IsSmallGreek(command.Glyph)),
        MathQuoted quoted => Text(quoted.Text, style, italic: false, prose: true),
        MathFunction function => Text(function.Name, style, italic: false),
        _ => EquationBox.Empty,
    };

    private ResolvedFont? Primary(FontStyle style)
    {
        if (_primary.TryGetValue(style, out var font))
            return font;
        font = Families.Select(f => fonts.Find(f, style)).FirstOrDefault(f => f is not null) ?? fonts.Resolve("serif", style);
        return _primary[style] = font;
    }

    /// <summary>The installed math font brackets and radical signs are drawn from; null when there is none.</summary>
    internal (PageFont Font, MathTable Table)? MathFont()
    {
        if (_mathFontLooked)
            return _mathFont;
        _mathFontLooked = true;
        foreach (var family in MathFamilies)
        {
            // Another name can find a font without the table (Cambria Math → Caladea): only a MATH table counts.
            if (fonts.Find(family, FontStyle.Regular) is { Face.Cff2: false } found && MathTable.Of(found.Font) is { } table)
            {
                _mathFace = found;
                return _mathFont = (found.Font, table);
            }
        }
        return null;
    }

    /// <summary>
    /// A run of text in the equation's fonts (with fallback for characters they lack): Word's and TeX's in the math
    /// font, its letters from the font's math alphabets.
    /// </summary>
    /// <param name="prose">Text written as text (\text, a quoted word): the text font in any mode.</param>
    private EquationBox Text(string text, Style style, bool italic, float scale = 1, bool prose = false)
    {
        if (text.Length == 0)
            return EquationBox.Empty;
        if (style.Tex && !prose && InMathFont(text, italic, style.Bold) is { } math && _mathFace is { } face)
            return Glyphs(math, style, face, FontStyle.Regular, italic, scale);
        var fontStyle = new FontStyle(style.Bold ? 700 : 400, italic);
        if (Primary(fontStyle) is not { } primary)
            return EquationBox.Empty;
        return Glyphs(text, style, primary, fontStyle, italic, scale);
    }

    /// <summary>
    /// <paramref name="text"/> in the math font's alphabets: italic letters (and lowercase Greek) in math italic, bold
    /// letters and digits in math bold; null without a math font, when the font lacks one of them, or for bold text
    /// with characters outside those alphabets (the text font's bold draws it).
    /// </summary>
    private string? InMathFont(string text, bool italic, bool bold)
    {
        if (MathFont() is null || _mathFace is not { } face)
            return null;
        var sb = new StringBuilder(text.Length * 2);
        foreach (var rune in text.EnumerateRunes())
        {
            var mapped = Alphabet(rune.Value, italic, bold);
            if (mapped != rune.Value && !face.Face.Has(mapped))
                return null;
            if (bold && mapped == rune.Value && !Rune.IsWhiteSpace(rune))
                return null;
            sb.Append(char.ConvertFromUtf32(mapped));
        }
        return sb.ToString();
    }

    /// <summary>
    /// A letter or digit in Unicode's mathematical alphanumerics (U+1D400 …): italic and bold italic Latin letters and
    /// lowercase Greek, bold Latin letters and digits; any other character as it is.
    /// </summary>
    internal static int Alphabet(int c, bool italic, bool bold)
    {
        switch (c)
        {
            case >= 'A' and <= 'Z':
                return !italic && !bold ? c : (italic ? bold ? 0x1D468 : 0x1D434 : 0x1D400) + c - 'A';
            case 'h' when italic && !bold:
                // Planck's constant stands in the italic alphabet's gap.
                return 0x210E;
            case >= 'a' and <= 'z':
                return !italic && !bold ? c : (italic ? bold ? 0x1D482 : 0x1D44E : 0x1D41A) + c - 'a';
            case >= '0' and <= '9':
                return bold && !italic ? 0x1D7CE + c - '0' : c;
            case >= 0x3B1 and <= 0x3C9 when italic:
                return (bold ? 0x1D736 : 0x1D6FC) + c - 0x3B1;
        }
        if (!italic)
            return c;
        // ϵ ϑ ϰ ϕ ϱ ϖ follow the italic small omega and ∂.
        var variant = c switch
        {
            0x3F5 => 1,
            0x3D1 => 2,
            0x3F0 => 3,
            0x3D5 => 4,
            0x3F1 => 5,
            0x3D6 => 6,
            _ => 0,
        };
        return variant == 0 ? c : (bold ? 0x1D74F : 0x1D715) + variant;
    }

    /// <summary>One lowercase Greek letter (or one of its variant forms ϵ ϑ ϰ ϕ ϱ ϖ).</summary>
    private static bool IsSmallGreek(string text) =>
        text.Length == 1 && text[0] is >= 'α' and <= 'ω' or 'ϵ' or 'ϑ' or 'ϰ' or 'ϕ' or 'ϱ' or 'ϖ';

    /// <summary>The glyphs of <paramref name="text"/> in <paramref name="primary"/> and the fonts it falls back to.</summary>
    private EquationBox Glyphs(string text, Style style, ResolvedFont primary, FontStyle fontStyle, bool italic, float scale)
    {
        var size = style.Size * scale;
        var runs = new List<PlacedRun>();
        float x = 0;
        foreach (var run in fonts.Fallback(text, 0, text.Length, primary, fontStyle))
        {
            var shaped = shaper.Shape(run.Font.Font, text, run.Start, run.Length);
            var unit = size / shaped.UnitsPerEm;
            var positions = new SKPoint[shaped.Glyphs.Length];
            for (var g = 0; g < shaped.Glyphs.Length; g++)
            {
                positions[g] = new SKPoint(x + shaped.XOffsets[g] * unit, -shaped.YOffsets[g] * unit);
                x += shaped.XAdvances[g] * unit;
            }
            runs.Add(new PlacedRun(run.Font.Font, size, shaped.Glyphs, positions, text.Substring(run.Start, run.Length), shaped.Clusters)
            {
                Color = style.Color,
                Embolden = run.Font.SyntheticBold,
                Slant = run.Font.SyntheticItalic,
            });
        }
        // Italic letters lean past their advance: a little room after them.
        var width = x + (italic ? size * 0.02f : 0);
        return new EquationBox(width, size * 0.75f, size * 0.25f, (page, left, baseline, scale) =>
        {
            foreach (var run in runs)
            {
                page.DrawGlyphs(new GlyphRun(run.Font, run.Size, run.Glyphs, [.. run.Positions.Select(p => new SKPoint(left + p.X * scale, baseline + p.Y))], run.Text, run.Clusters)
                {
                    Color = run.Color,
                    ScaleX = scale,
                    Embolden = run.Embolden,
                    SkewX = run.Slant ? GlyphRun.SyntheticSlant : 0,
                });
            }
        });
    }

    /// <summary>
    /// A glyph of the math font stretched to a height: its glyphs with their origins
    /// (y up from the bottom of their outlines), the widest advance and the height of the outlines.
    /// </summary>
    private sealed record Stretched(PageFont Font, float Size, string Text, ushort[] Glyphs, SKPoint[] Origins, float Width, float Height)
    {
        /// <summary>Drawn with the bottom of its outlines <paramref name="bottom"/> points above the baseline.</summary>
        public EquationBox Box(float x, float bottom, SKColor color) => new(x + Width, bottom + Height, -bottom, (page, left, baseline, scale) =>
            page.DrawGlyphs(new GlyphRun(Font, Size, Glyphs, [.. Origins.Select(o => new SKPoint(left + (x + o.X) * scale, baseline - bottom - o.Y))], Text, new int[Glyphs.Length])
            {
                Color = color,
                ScaleX = scale,
            }));
    }

    /// <summary><paramref name="character"/> from the math font at least <paramref name="height"/> tall; null without one.</summary>
    /// <param name="variantOnly">Only one of its variants (see <see cref="MathTable.Stretch"/>).</param>
    private Stretched? Stretch(string character, float height, float size, bool variantOnly = false)
    {
        if (character.Length == 0 || !(size > 0) || MathFont() is not var (font, table))
            return null;
        using var measure = new SKFont(font.Typeface, table.UnitsPerEm) { LinearMetrics = true, Hinting = SKFontHinting.None };
        var glyph = measure.GetGlyph(char.ConvertToUtf32(character, 0));
        if (glyph == 0)
            return null;
        float InkHeight(ushort g)
        {
            Span<float> width = stackalloc float[1];
            Span<SKRect> bounds = stackalloc SKRect[1];
            measure.GetGlyphWidths([g], width, bounds);
            return bounds[0].Height;
        }
        var parts = table.Stretch(glyph, height * table.UnitsPerEm / size, InkHeight, variantOnly);
        var glyphs = parts.Select(p => p.Glyph).ToArray();
        var widths = new float[glyphs.Length];
        var outlines = new SKRect[glyphs.Length];
        measure.GetGlyphWidths(glyphs, widths, outlines);
        // Each part's outline starts where the assembly puts it (Skia's bounds run downwards).
        var unit = size / table.UnitsPerEm;
        var origins = new SKPoint[glyphs.Length];
        float top = 0;
        for (var i = 0; i < glyphs.Length; i++)
        {
            var originY = parts[i].Bottom + outlines[i].Bottom;
            origins[i] = new SKPoint(0, originY * unit);
            top = Math.Max(top, originY - outlines[i].Top);
        }
        return new Stretched(font, size, character, glyphs, origins, widths.DefaultIfEmpty(0).Max() * unit, top * unit);
    }

    // ───────────────────────── Rows ─────────────────────────

    /// <param name="previous">The item before the row (a part of a line after an alignment mark), for its spacing.</param>
    private EquationBox Row(IReadOnlyList<MathNode> items, Style style, MathNode? previous = null)
    {
        var parts = new List<(float X, EquationBox Box)>();
        float x = 0;
        foreach (var item in items)
        {
            var box = Lay(item, style);
            var (before, after) = Spacing(item, previous, style);
            x += before;
            parts.Add((x, box));
            x += box.Width + after;
            previous = item;
        }
        return Combine(parts, x);
    }

    /// <summary>
    /// Space around an operator: relations more than binary operators, none for a sign. Word and TeX space them as TeX
    /// does (a thick space, 5/18 em, around relations, a medium one, 4/18 em, around binary operators, a thin one
    /// after a function name or a comma) and leave the operators of scripts unspaced.
    /// </summary>
    private static (float Before, float After) Spacing(MathNode item, MathNode? previous, in Style style)
    {
        var size = style.Size;
        var tex = style.Tex;
        if (tex && style.Level > 0)
            return (0, 0);
        var text = item switch
        {
            MathSymbol symbol => symbol.Text,
            MathCommand command => command.Glyph,
            // An arrow with text over it (\xrightarrow) is still a relation.
            MathStack stack => Glyph(stack.Base),
            _ => null,
        };
        if (text is null)
            return item is MathFunction ? (0, size * (tex ? 1 / 6f : 0.08f)) : (0, 0);
        if (Relations.Contains(text))
            return tex ? (size * 5 / 18f, size * 5 / 18f) : (size * 0.13f, size * 0.13f);
        var sign = previous is null || previous is MathSymbol { Text: "(" or "[" or "{" or "," } || previous is MathSymbol or MathCommand && IsOperator(previous);
        if (Binary.Contains(text) && !sign)
            return tex ? (size * 4 / 18f, size * 4 / 18f) : (size * 0.1f, size * 0.1f);
        return text == "," ? (0, size * (tex ? 1 / 6f : 0.1f)) : (0, 0);
    }

    private static bool IsOperator(MathNode node) => node switch
    {
        MathSymbol symbol => Relations.Contains(symbol.Text) || Binary.Contains(symbol.Text),
        MathCommand command => Relations.Contains(command.Glyph) || Binary.Contains(command.Glyph),
        _ => false,
    };

    /// <summary>A relation (=, ≤, ∈ …) or an arrow, set with a relation's spacing.</summary>
    internal static bool IsRelation(string text) => Relations.Contains(text);

    /// <summary>
    /// Whether a line of a formula may break before <paramref name="item"/> (Word's rule): a relation (an arrow with
    /// text over it too) or a binary operator, not a sign (after an operator, an opening bracket or a comma) and not a
    /// slash.
    /// </summary>
    internal static bool BreaksBefore(MathNode item, MathNode? previous)
    {
        if (previous is null || previous is MathAlign)
            return false;
        var text = item switch
        {
            MathSymbol symbol => symbol.Text,
            MathCommand command => command.Glyph,
            MathStack stack => Glyph(stack.Base),
            _ => null,
        };
        if (text is null or "/" or "\\" or ":")
            return false;
        if (Relations.Contains(text))
            return !IsOperator(previous);
        return Binary.Contains(text) && Spacing(item, previous, new Style(1, SKColors.Black, false, false)).Before > 0;
    }

    /// <summary>
    /// A display formula broken into lines no wider than <paramref name="width"/> where it can be (see
    /// <see cref="BreaksBefore"/>), as <paramref name="wrap"/> says: a binary operator before the break, after it or on
    /// both lines (a subtraction with the signs Word's m:brkBinSub gives), relations always starting the next line; the
    /// lines after the first indented, or set to the right. Brackets wider than a line are broken inside. Null when it
    /// can't be broken.
    /// </summary>
    private EquationBox? Wrapped(MathNode node, Style style, float width, HMathWrap wrap)
    {
        var inner = node;
        while (inner is MathRow { Items: [MathRow only] })
            inner = only;
        if (inner is not MathRow { Items.Count: > 1 } row || row.Items.Any(i => i is MathAlign))
            return null;
        var size = style.Size;
        // Word indents the lines after the first by an inch (m:wrapIndent), here at most a third of the width.
        var indent = wrap.Right ? 0 : Math.Clamp(wrap.Indent / 100f, 0, width / 3);

        // Brackets too wide for any line open up, so that a line may break inside them as Word breaks inside m:d:
        // the opening and the closing bracket each as tall as the whole content, the content's items between them.
        var items = new List<MathNode>();
        var laid = new List<EquationBox>();
        void Expand(IReadOnlyList<MathNode> list, int depth)
        {
            foreach (var item in list)
            {
                var box = Lay(item, style);
                if (depth < 8 && box.Width > width - indent && item is MathBrackets { Body: var body } brackets
                    && Unwrapped(body) is MathRow { Items.Count: > 1 } content && !content.Items.Any(i => i is MathAlign))
                {
                    var contentBox = Lay(content, style);
                    var empty = new EquationBox(0, contentBox.Ascent, contentBox.Descent, (_, _, _, _) => { });
                    items.Add(new MathSymbol(brackets.Left.Length > 0 ? brackets.Left : "("));
                    laid.Add(Brackets(brackets.Left, empty, "", style));
                    Expand(content.Items, depth + 1);
                    items.Add(new MathSymbol(brackets.Right.Length > 0 ? brackets.Right : ")"));
                    laid.Add(Brackets("", empty, brackets.Right, style));
                    continue;
                }
                items.Add(item);
                laid.Add(box);
            }
        }
        Expand(row.Items, 0);

        var boxes = new EquationBox[items.Count];
        var spacing = new (float Before, float After)[items.Count];
        // Where lines may start: 0, each break point, and the end.
        var cuts = new List<int> { 0 };
        for (var i = 0; i < items.Count; i++)
        {
            var previous = i > 0 ? items[i - 1] : null;
            boxes[i] = laid[i];
            spacing[i] = Spacing(items[i], previous, style);
            if (i > 0 && BreaksBefore(items[i], previous))
                cuts.Add(i);
        }
        if (cuts.Count < 2)
            return null;
        cuts.Add(items.Count);

        // A binary operator at a break, when it moves (after) or shows twice (repeat); relations always start the line.
        bool Moves(int i) => wrap.Break != HMathBreak.Before && i < items.Count && Glyph(items[i]) is { } glyph && Binary.Contains(glyph);
        EquationBox Sign(int i, bool start)
        {
            if (wrap.Break != HMathBreak.Repeat || Glyph(items[i]) is not ("-" or "−") || wrap.Subtraction is not { Length: 2 } signs)
                return boxes[i];
            var sign = start ? signs[1] : signs[0];
            return Lay(new MathSymbol(sign == '+' ? "+" : "−"), style);
        }

        // The parts of a line of the pieces first..last (between cuts), with the room before and after each.
        List<(EquationBox Box, float Before, float After)> Parts(int first, int last)
        {
            var parts = new List<(EquationBox, float, float)>();
            var (from, to) = (cuts[first], cuts[last + 1]);
            for (var i = from; i < to; i++)
            {
                if (i == from && first > 0 && Moves(i))
                {
                    if (wrap.Break == HMathBreak.Repeat)
                        parts.Add((Sign(i, start: true), 0, spacing[i].After));
                    continue; // after: the operator ended the line before
                }
                parts.Add((boxes[i], spacing[i].Before, spacing[i].After));
            }
            if (last + 2 < cuts.Count && Moves(to))
                parts.Add((Sign(to, start: false), spacing[to].Before, 0));
            return parts;
        }
        static float Width(List<(EquationBox Box, float Before, float After)> parts)
        {
            float w = 0;
            for (var i = 0; i < parts.Count; i++)
                w += (i == 0 ? 0 : parts[i].Before) + parts[i].Box.Width + parts[i].After;
            return w;
        }

        // As many pieces as fit on each line.
        var pieces = cuts.Count - 1;
        var lines = new List<List<(EquationBox Box, float Before, float After)>>();
        var start = 0;
        while (start < pieces)
        {
            var room = lines.Count == 0 ? width : width - indent;
            var end = start;
            while (end + 1 < pieces && Width(Parts(start, end + 1)) <= room)
                end++;
            lines.Add(Parts(start, end));
            start = end + 1;
        }
        if (lines.Count < 2)
            return null;

        var drawn = new List<EquationBox>();
        foreach (var parts in lines)
        {
            var placedParts = new List<(float X, EquationBox Box)>();
            float x = 0;
            for (var i = 0; i < parts.Count; i++)
            {
                x += i == 0 ? 0 : parts[i].Before;
                placedParts.Add((x, parts[i].Box));
                x += parts[i].Box.Width + parts[i].After;
            }
            drawn.Add(Combine(placedParts, x));
        }
        var total = Math.Max(drawn[0].Width, drawn.Skip(1).Max(l => l.Width + indent));
        var placed = new List<(float X, float Y, EquationBox Box)>();
        float y = 0;
        for (var i = 0; i < drawn.Count; i++)
        {
            if (i > 0)
                y += drawn[i - 1].Descent + size * 0.3f + drawn[i].Ascent;
            var left = i == 0 ? 0 : wrap.Right ? total - drawn[i].Width : indent;
            placed.Add((left, y, drawn[i]));
        }
        return Place(total, drawn[0].Ascent, y + drawn[^1].Descent, [.. placed]);
    }

    /// <summary>
    /// An inline formula in the pieces a line of text may break it into (Word breaks inline math across lines at its
    /// top-level binary and relation operators, see <see cref="BreaksBefore"/>): side by side they are the formula laid
    /// out whole. As <paramref name="wrap"/> says (Word's m:mathPr, null for its defaults), a piece after a break starts
    /// at its operator (before), a binary operator ends the piece before (after), or ends it and starts the next one too
    /// when the line breaks there (repeat, a subtraction with the signs of m:brkBinSub); relations always start a piece.
    /// The room between two pieces ends the first, and a line ending there leaves it out (<see cref="MathPiece.End"/>).
    /// Null when the formula has no such place (or is too deep for <see cref="MaxDepth"/>).
    /// </summary>
    public IReadOnlyList<MathPiece>? Pieces(MathNode node, float size, SKColor color, MathMode mode, HMathWrap? wrap = null)
    {
        var inner = Unwrapped(node);
        if (inner is not MathRow { Items.Count: > 1 } row || row.Items.Any(i => i is MathAlign))
            return null;
        wrap ??= DefaultWrap;
        var items = row.Items;
        bool IsBinary(int i) => Glyph(items[i]) is { } glyph && Binary.Contains(glyph);
        // Where pieces start; for a binary operator ending its line (after), the piece after it starts past it.
        var cuts = new List<int>();
        var repeated = new HashSet<int>();
        for (var i = 1; i < items.Count; i++)
        {
            if (!BreaksBefore(items[i], items[i - 1]))
                continue;
            if (wrap.Break == HMathBreak.After && IsBinary(i))
            {
                if (i + 1 < items.Count)
                    cuts.Add(i + 1);
                continue;
            }
            cuts.Add(i);
            if (wrap.Break == HMathBreak.Repeat && IsBinary(i))
                repeated.Add(i);
        }
        if (cuts.Count == 0)
            return null;
        cuts.Add(items.Count);

        // Laid out as Row lays out the whole formula: where each item's box starts and where the row ends.
        var style = new Style(size, color, false, false, mode);
        var boxes = new EquationBox[items.Count];
        var spacing = new (float Before, float After)[items.Count];
        var at = new float[items.Count + 1];
        float x = 0;
        MathNode? previous = null;
        for (var i = 0; i < items.Count; i++)
        {
            boxes[i] = Lay(items[i], style);
            spacing[i] = Spacing(items[i], previous, style);
            x += spacing[i].Before;
            at[i] = x;
            x += boxes[i].Width + spacing[i].After;
            previous = items[i];
        }
        at[items.Count] = x;

        // A subtraction repeated across a break shows m:brkBinSub's signs: the first ending the line, the second starting the next.
        EquationBox Sign(int i, bool start)
        {
            if (Glyph(items[i]) is not ("-" or "−") || wrap.Subtraction is not { Length: 2 } signs || (start ? signs[1] : signs[0]) != '+')
                return boxes[i];
            return Lay(new MathSymbol("+"), style);
        }

        var pieces = new List<MathPiece>(cuts.Count);
        var from = 0;
        foreach (var to in cuts)
        {
            var origin = from == 0 ? 0 : at[from];
            var parts = new List<(float X, EquationBox Box)>(to - from);
            for (var i = from; i < to; i++)
                parts.Add((at[i] - origin, boxes[i]));
            var box = Combine(parts, at[to] - origin);
            // Ending a line (not the formula): up to its last item's ink, then the repeated operator (with its room
            // before it).
            EquationBox Ending(List<(float X, EquationBox Box)> shown)
            {
                if (!repeated.Contains(to))
                    return Combine(shown, at[to - 1] + boxes[to - 1].Width - origin);
                var sign = Sign(to, start: false);
                return Combine(new List<(float X, EquationBox Box)>(shown) { (at[to] - origin, sign) }, at[to] - origin + sign.Width);
            }
            var end = to < items.Count ? Ending(parts) : box;
            // Starting a line after a repeated subtraction: the operator in its second sign; alone on its line, with
            // its end too.
            EquationBox? start = null, both = null;
            if (from > 0 && repeated.Contains(from) && Sign(from, start: true) is var startSign && !ReferenceEquals(startSign, boxes[from]))
            {
                var swapped = new List<(float X, EquationBox Box)>(parts);
                swapped[0] = (0, startSign);
                start = Combine(swapped, box.Width);
                both = to < items.Count ? Ending(swapped) : start;
            }
            pieces.Add(new MathPiece(box, end, start, both));
            from = to;
        }
        return pieces;
    }

    /// <summary>A node inside rows of one item each.</summary>
    private static MathNode Unwrapped(MathNode node)
    {
        while (node is MathRow { Items: [var only] })
            node = only;
        return node;
    }

    /// <summary>Boxes side by side on one baseline.</summary>
    private static EquationBox Combine(List<(float X, EquationBox Box)> parts, float width) =>
        parts.Count == 0
            ? EquationBox.Empty
            : new EquationBox(width, parts.Max(p => p.Box.Ascent), parts.Max(p => p.Box.Descent), (page, x, y, scale) =>
            {
                foreach (var (dx, box) in parts)
                    box.Paint(page, x + dx * scale, y, scale);
            });

    /// <summary>Boxes placed at offsets from the origin (y down from the baseline).</summary>
    private static EquationBox Place(float width, float ascent, float descent, params (float X, float Y, EquationBox Box)[] parts) =>
        new(width, ascent, descent, (page, x, y, scale) =>
        {
            foreach (var (dx, dy, box) in parts)
                box.Paint(page, x + dx * scale, y + dy, scale);
        });

    /// <summary>A drawing with its origin at x = 0 and the baseline at y, scaled across with the box.</summary>
    private static Action<PageContent, float, float, float> Drawn(Action<SKCanvas, float, float> draw) =>
        (page, x, y, scale) => page.Draw(canvas =>
        {
            canvas.Translate(x, 0);
            canvas.Scale(scale, 1);
            draw(canvas, 0, y);
        });

    private static SKPaint Pen(SKColor color, float width) =>
        new() { Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = width, IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };

    // ───────────────────────── Fractions and roots ─────────────────────────

    private EquationBox Fraction(MathFraction fraction, Style style)
    {
        // Word and TeX: a display fraction's parts are in text style, a text style one's in script size.
        var parts = style.Mode switch
        {
            MathMode.Display => style with { Mode = MathMode.Text },
            MathMode.Text => style.Script(),
            _ => style,
        };
        var (top, bottom) = (Lay(fraction.Numerator, parts), Lay(fraction.Denominator, parts));
        var size = style.Size;
        var (axis, rule, gap) = (size * Axis, fraction.Bar ? Math.Max(0.4f, size * Rule) : 0, size * (style.Mode == MathMode.Text ? Gap / 2 : Gap));
        var width = Math.Max(top.Width, bottom.Width) + size * 0.12f;
        var topY = -(axis + rule / 2 + gap + top.Descent);
        var bottomY = -axis + rule / 2 + gap + bottom.Ascent;
        var placed = new List<(float, float, EquationBox)>
        {
            ((width - top.Width) / 2, topY, top),
            ((width - bottom.Width) / 2, bottomY, bottom),
        };
        if (fraction.Bar)
        {
            placed.Add((0, 0, new EquationBox(width, 0, 0, Drawn((canvas, x, y) =>
            {
                using var pen = Pen(style.Color, rule);
                pen.StrokeCap = SKStrokeCap.Butt;
                canvas.DrawLine(x + size * 0.05f, y - axis, x + width - size * 0.05f, y - axis, pen);
            }))));
        }
        return Place(width, -topY + top.Ascent, bottomY + bottom.Descent, [.. placed]);
    }

    private EquationBox Root(MathRoot root, Style style)
    {
        var body = Lay(root.Body, style);
        var size = style.Size;
        if (MathFont() is var (_, table))
        {
            var unit = size / table.UnitsPerEm;
            var (thickness, clearance) = (Math.Max(0.4f, table.RadicalRuleThickness * unit), Math.Max(0, table.RadicalVerticalGap * unit));
            if (Stretch("√", body.Height + clearance + thickness, size) is { } surd)
                return Radical(root, body, surd, thickness, clearance, table, style);
        }
        var rule = Math.Max(0.4f, size * Rule);
        var gap = size * 0.1f;
        var ascent = Math.Max(body.Ascent, size * 0.7f) + gap + rule;
        var descent = Math.Max(body.Descent, size * 0.2f);
        var sign = size * 0.55f;
        var index = root.Index is null ? null : Lay(root.Index, style.Smaller(0.6f));
        var shift = index is null ? 0 : Math.Max(0, index.Width - sign * 0.45f);
        var width = shift + sign + body.Width + size * 0.1f;
        var radical = new EquationBox(width, ascent, descent, Drawn((canvas, x, y) =>
        {
            using var pen = Pen(style.Color, rule);
            using var path = new SKPath();
            var (left, top, bottom) = (x + shift, y - ascent + rule / 2, y + descent);
            path.MoveTo(left, y - (ascent - descent) * 0.35f);
            path.LineTo(left + sign * 0.2f, y - (ascent - descent) * 0.45f);
            path.LineTo(left + sign * 0.5f, bottom);
            path.LineTo(left + sign * 0.95f, top);
            path.LineTo(x + width, top);
            canvas.DrawPath(path, pen);
        }));
        var parts = new List<(float, float, EquationBox)> { (0, 0, radical), (shift + sign, 0, body) };
        if (index is not null)
            parts.Add((0, -(ascent - descent) * 0.45f - index.Descent - size * 0.05f, index));
        var height = index is null ? ascent : Math.Max(ascent, (ascent - descent) * 0.45f + size * 0.05f + index.Height);
        return Place(width, height, descent, [.. parts]);
    }

    /// <summary>
    /// A root with the math font's radical sign: the rule on top of the sign, the room the sign has beyond the gap
    /// shared above and below the body, and the index kerned in at the height the font asks.
    /// </summary>
    private EquationBox Radical(MathRoot root, EquationBox body, Stretched surd, float thickness, float gap, MathTable table, Style style)
    {
        var size = style.Size;
        var unit = size / table.UnitsPerEm;
        var extra = Math.Max(0, surd.Height - (body.Height + gap + thickness));
        var ruleTop = body.Ascent + gap + extra / 2 + thickness;
        var bottom = ruleTop - surd.Height;
        var index = root.Index is null ? null : Lay(root.Index, style.Smaller(0.6f));
        float surdX = 0, indexRaise = 0;
        if (index is not null)
        {
            surdX = Math.Max(0, table.RadicalKernBeforeDegree * unit + index.Width + table.RadicalKernAfterDegree * unit);
            indexRaise = bottom + surd.Height * table.RadicalDegreeBottomRaisePercent / 100f;
        }
        var bodyX = surdX + surd.Width;
        var width = bodyX + body.Width + size * 0.05f;
        var rule = new EquationBox(width, 0, 0, Drawn((canvas, x, y) =>
        {
            using var fill = new SKPaint { Color = style.Color, IsAntialias = true };
            // A hair into the sign so no seam shows where they meet.
            canvas.DrawRect(SKRect.Create(x + bodyX - thickness / 2, y - ruleTop, width - bodyX + thickness / 2, thickness), fill);
        }));
        var parts = new List<(float, float, EquationBox)> { (0, 0, surd.Box(surdX, bottom, style.Color)), (0, 0, rule), (bodyX, 0, body) };
        var ascent = ruleTop + Math.Max(0, table.RadicalExtraAscender * unit);
        if (index is not null)
        {
            parts.Add((Math.Max(0, table.RadicalKernBeforeDegree * unit), -indexRaise - index.Descent, index));
            ascent = Math.Max(ascent, indexRaise + index.Descent + index.Ascent);
        }
        return Place(width, ascent, Math.Max(body.Descent, -bottom), [.. parts]);
    }

    // ───────────────────────── Scripts and limits ─────────────────────────

    /// <param name="italic">How far the base reaches past its width at the top (a slanted integral): the superscript goes that much further.</param>
    private EquationBox Scripts(EquationBox body, MathNode? subNode, MathNode? supNode, Style style, bool italicBase, float italic = 0)
    {
        var size = style.Size;
        var small = style.Script();
        // A prime (f′, TeX's f' = f^\prime) is already a raised mark: drawn at the base's size on its baseline, after
        // the base (clear of an italic letter's overhang), not shrunk and raised again.
        var prime = supNode is not null && IsPrimes(supNode);
        var sup = supNode is null ? null : Lay(supNode, prime ? style : small);
        var sub = subNode is null ? null : Lay(subNode, small);
        var parts = new List<(float, float, EquationBox)> { (0, 0, body) };
        var supShift = sup is null || prime ? 0 : Math.Max(size * 0.42f, body.Ascent - sup.Ascent * 0.6f);
        var subShift = sub is null ? 0 : Math.Max(size * 0.18f, body.Descent - sub.Descent * 0.2f);
        if (sup is not null && sub is not null && !prime && supShift - sup.Descent - (sub.Ascent - subShift) < size * 0.15f)
            subShift += size * 0.15f - (supShift - sup.Descent - (sub.Ascent - subShift));
        var width = body.Width;
        if (sup is not null)
        {
            var x = body.Width + italic + (italicBase ? size * (prime ? 0.08f : 0.03f) : 0);
            parts.Add((x, -supShift, sup));
            width = Math.Max(width, x + sup.Width);
        }
        if (sub is not null)
        {
            parts.Add((body.Width - (italicBase ? size * 0.03f : 0), subShift, sub));
            width = Math.Max(width, body.Width + sub.Width);
        }
        width += size * 0.02f;
        var ascent = Math.Max(body.Ascent, sup is null ? 0 : supShift + sup.Ascent);
        var descent = Math.Max(body.Descent, sub is null ? 0 : subShift + sub.Descent);
        return Place(width, ascent, descent, [.. parts]);
    }

    /// <summary>One or more primes (′ ″ ‴) and nothing else.</summary>
    private static bool IsPrimes(MathNode node) => node switch
    {
        MathCommand { Glyph: "′" or "″" or "‴" } or MathText { Text: "′" or "″" or "‴" } or MathSymbol { Text: "′" or "″" or "‴" } => true,
        MathRow { Items.Count: > 0 } row => row.Items.All(IsPrimes),
        _ => false,
    };

    /// <summary><paramref name="body"/> with <paramref name="upper"/> centred above and <paramref name="lower"/> below.</summary>
    private static EquationBox Limits(EquationBox body, EquationBox? upper, EquationBox? lower, float gap)
    {
        var width = Math.Max(body.Width, Math.Max(upper?.Width ?? 0, lower?.Width ?? 0));
        var parts = new List<(float, float, EquationBox)> { ((width - body.Width) / 2, 0, body) };
        var (ascent, descent) = (body.Ascent, body.Descent);
        if (upper is not null)
        {
            var y = -(body.Ascent + gap + upper.Descent);
            parts.Add(((width - upper.Width) / 2, y, upper));
            ascent = -y + upper.Ascent;
        }
        if (lower is not null)
        {
            var y = body.Descent + gap + lower.Ascent;
            parts.Add(((width - lower.Width) / 2, y, lower));
            descent = y + lower.Descent;
        }
        return Place(width, ascent, descent, [.. parts]);
    }

    private EquationBox BigOperator(MathBigOperator big, Style style)
    {
        var size = style.Size;
        var integral = big.Glyph is "∫" or "∬" or "∭" or "∮" or "∯" or "∰";
        // Word's inline equations and TeX's $…$ (text style) draw the operator little larger than the text, with its
        // limits at the side as scripts.
        var inline = style.Mode == MathMode.Text;
        EquationBox symbol;
        float italic = 0;
        if (style.Tex && Large(big.Glyph, display: !inline, style) is var (large, correction))
        {
            (symbol, italic) = (large, correction);
        }
        else
        {
            var glyph = Text(big.Glyph, style, italic: false, inline ? (integral ? 1.15f : 1.05f) : integral ? 1.6f : 1.4f);
            // Centred on the math axis.
            var shift = (glyph.Ascent - glyph.Descent) / 2 - size * Axis;
            symbol = Place(glyph.Width, glyph.Ascent - shift, glyph.Descent + shift, (0, shift, glyph));
        }
        EquationBox box;
        if (integral || inline)
        {
            box = Scripts(symbol, big.Lower, big.Upper, style, italicBase: integral && italic == 0, italic);
        }
        else
        {
            // Laid out here only: an integral lays its limits out as scripts, and doing both doubled the work per level.
            var small = style.Script();
            var lower = big.Lower is null ? null : Lay(big.Lower, small);
            var upper = big.Upper is null ? null : Lay(big.Upper, small);
            box = Limits(symbol, upper, lower, size * 0.08f);
        }
        return Place(box.Width + size * 0.05f, box.Ascent, box.Descent, (0, 0, box));
    }

    /// <summary>
    /// A large operator from the math font, centred on the math axis: its own glyph inline, in a display formula its
    /// variant at least as tall as the font asks (Cambria Math's display ∑ and ∫, as Word draws them), with its italic
    /// correction (points); null without a math font or when it lacks the glyph.
    /// </summary>
    private (EquationBox Box, float Italic)? Large(string glyph, bool display, Style style)
    {
        if (MathFont() is not var (_, table) || _mathFace is not { } face || glyph.EnumerateRunes().Count() != 1 || !face.Face.Has(char.ConvertToUtf32(glyph, 0)))
            return null;
        var size = style.Size;
        var height = display ? table.DisplayOperatorMinHeight * size / table.UnitsPerEm : 0;
        if (Stretch(glyph, height, size, variantOnly: true) is not { Glyphs.Length: 1 } large)
            return null;
        return (large.Box(0, size * Axis - large.Height / 2, style.Color), table.ItalicCorrection(large.Glyphs[0]) * size / table.UnitsPerEm);
    }

    /// <summary>
    /// Content centred over and under a base at script size (\overset, \underset, Word's m:limUpp and m:limLow); an
    /// arrow as the base (\xrightarrow) is drawn as long as what it carries.
    /// </summary>
    private EquationBox Stack(MathStack stack, Style style)
    {
        var size = style.Size;
        var small = style.Script();
        var over = stack.Over is null ? null : Lay(stack.Over, small);
        var under = stack.Under is null ? null : Lay(stack.Under, small);
        var carried = Math.Max(over?.Width ?? 0, under?.Width ?? 0);
        var body = Glyph(stack.Base) is { } arrow && Arrows.Contains(arrow) && carried > 0
            ? Arrow(arrow, carried + size * 0.5f, style)
            : Lay(stack.Base, style);
        return Limits(body, over, under, size * 0.06f);
    }

    /// <summary>Arrows that grow under what they carry (\xrightarrow and its kin).</summary>
    private static readonly HashSet<string> Arrows = ["→", "←", "↔", "⇒", "⇐", "⇔", "⟶", "⟵", "⟷", "⟹", "⟸", "⟺", "↦", "⟼"];

    private static string? Glyph(MathNode node) => node switch
    {
        MathSymbol symbol => symbol.Text,
        MathCommand command => command.Glyph,
        _ => null,
    };

    /// <summary>An arrow <paramref name="width"/> long on the math axis: single or double, its heads where it points.</summary>
    private static EquationBox Arrow(string arrow, float width, Style style)
    {
        var size = style.Size;
        var axis = size * Axis;
        var stroke = Math.Max(0.4f, size * 0.05f);
        var head = size * 0.2f;
        var twin = arrow is "⇒" or "⇐" or "⇔" or "⟹" or "⟸" or "⟺";
        var left = arrow is "←" or "⇐" or "⟵" or "⟸" or "↔" or "⇔" or "⟷" or "⟺";
        var right = arrow is not ("←" or "⇐" or "⟵" or "⟸");
        var bar = arrow is "↦" or "⟼";
        return new EquationBox(width, axis + head, 0, Drawn((canvas, x, y) =>
        {
            using var pen = Pen(style.Color, stroke);
            using var path = new SKPath();
            var middle = y - axis;
            var (from, to) = (x + size * 0.05f, x + width - size * 0.05f);
            foreach (var offset in twin ? new[] { -size * 0.08f, size * 0.08f } : [0f])
            {
                path.MoveTo(from + (left && twin ? head * 0.5f : 0), middle + offset);
                path.LineTo(to - (right && twin ? head * 0.5f : 0), middle + offset);
            }
            if (right)
            {
                path.MoveTo(to - head, middle - head * 0.6f);
                path.LineTo(to, middle);
                path.LineTo(to - head, middle + head * 0.6f);
            }
            if (left)
            {
                path.MoveTo(from + head, middle - head * 0.6f);
                path.LineTo(from, middle);
                path.LineTo(from + head, middle + head * 0.6f);
            }
            if (bar)
            {
                path.MoveTo(from, middle - head * 0.5f);
                path.LineTo(from, middle + head * 0.5f);
            }
            canvas.DrawPath(path, pen);
        }));
    }

    private EquationBox Limit(MathLimit limit, Style style)
    {
        var name = Text("lim", style, italic: false);
        var lower = limit.Lower is null ? null : Lay(limit.Lower, style.Script());
        var box = Limits(name, null, lower, style.Size * 0.05f);
        return Place(box.Width + style.Size * 0.12f, box.Ascent, box.Descent, (0, 0, box));
    }

    // ───────────────────────── Grids ─────────────────────────

    /// <summary>Rows of cells in columns: centred (or to one side) and the whole centred on the math axis.</summary>
    private EquationBox Grid(IReadOnlyList<IReadOnlyList<MathNode>> rows, Style style, string align, float columnGap, bool even = false) =>
        Grid([.. rows.Select(r => r.Select(c => Lay(c, style)).ToList())], style, _ => align, _ => columnGap, even: even);

    /// <summary>Laid out cells in columns, each column aligned as <paramref name="align"/> says, with its gap after it.</summary>
    /// <param name="baselines">Collects each row's baseline (down from the grid's), or null.</param>
    /// <param name="even">Every row as tall as the tallest (Word's matrices).</param>
    private static EquationBox Grid(List<List<EquationBox>> cells, Style style, Func<int, string> align, Func<int, float> gapAfter,
        List<float>? baselines = null, bool even = false)
    {
        var size = style.Size;
        var columns = cells.Count == 0 ? 0 : cells.Max(r => r.Count);
        var widths = new float[columns];
        foreach (var row in cells)
            for (var c = 0; c < row.Count; c++)
                widths[c] = Math.Max(widths[c], row[c].Width);
        // Word and TeX keep the rows of a matrix closer than 한글 (#356: 1.16 em apart for rows of plain letters in Word).
        var rowGap = size * (style.Tex ? RowGap : 0.25f);
        var ascents = cells.Select(r => Math.Max(size * 0.75f, r.Count == 0 ? 0 : r.Max(b => b.Ascent))).ToList();
        var descents = cells.Select(r => Math.Max(size * 0.25f, r.Count == 0 ? 0 : r.Max(b => b.Descent))).ToList();
        if (even && cells.Count > 0)
        {
            var (ascent, descent) = (ascents.Max(), descents.Max());
            ascents = [.. ascents.Select(_ => ascent)];
            descents = [.. descents.Select(_ => descent)];
        }
        var height = ascents.Sum() + descents.Sum() + rowGap * Math.Max(0, cells.Count - 1);
        var top = -(height / 2 + size * Axis);
        var parts = new List<(float, float, EquationBox)>();
        var y = top;
        for (var r = 0; r < cells.Count; r++)
        {
            y += ascents[r];
            baselines?.Add(y);
            float x = 0;
            for (var c = 0; c < cells[r].Count; c++)
            {
                var box = cells[r][c];
                var offset = align(c) switch
                {
                    "lpile" => 0,
                    "rpile" => widths[c] - box.Width,
                    _ => (widths[c] - box.Width) / 2,
                };
                parts.Add((x + offset, y, box));
                x += widths[c] + gapAfter(c);
            }
            y += descents[r] + rowGap;
        }
        var width = widths.Sum() + Enumerable.Range(0, Math.Max(0, columns - 1)).Sum(gapAfter);
        return Place(width, -top, height + top, [.. parts]);
    }

    private EquationBox Matrix(MathMatrix matrix, Style style)
    {
        // Word spaces the rows of a matrix evenly (#356: rows with and without an integral 18 pt apart at 12 pt).
        var grid = Grid(matrix.Rows, style, "pile", style.Size * 0.8f, even: style.Tex);
        return matrix.Command switch
        {
            "pmatrix" => Brackets("(", grid, ")", style),
            "bmatrix" => Brackets("[", grid, "]", style),
            "dmatrix" or "vmatrix" => Brackets("|", grid, "|", style),
            _ => grid,
        };
    }

    private EquationBox Cases(MathCases cases, Style style) => Brackets("{", Grid(cases.Rows, style, "lpile", style.Size * 0.8f), "", style);

    /// <summary>
    /// Lines stacked. A Word equation array's lines with alignment marks (&amp;) are lined up at them: the odd marks are
    /// alignment points (what comes before ends there, what follows starts there), the even ones start a new column a
    /// space further on, as in Word.
    /// </summary>
    private EquationBox Pile(IReadOnlyList<MathNode> rows, string align, Style style, List<float>? baselines = null)
    {
        var lines = rows.Select(Segments).ToList();
        if (lines.All(l => l.Count <= 1))
            return Grid([.. rows.Select(r => new List<EquationBox> { Lay(r, style) })], style, _ => align, _ => 0, baselines);
        var cells = lines.Select(segments => segments.Select((segment, i) =>
            Row(segment, style, i > 0 && segments[i - 1] is [.., var last] ? last : null)).ToList()).ToList();
        return Grid(cells, style, c => c % 2 == 0 ? "rpile" : "lpile", c => c % 2 == 1 ? style.Size : 0, baselines);
    }

    /// <summary>The items of a line between its alignment marks.</summary>
    private static List<List<MathNode>> Segments(MathNode line)
    {
        var segments = new List<List<MathNode>> { new() };
        foreach (var item in line is MathRow row ? row.Items : [line])
        {
            if (item is MathAlign)
                segments.Add([]);
            else
                segments[^1].Add(item);
        }
        return segments;
    }

    // ───────────────────────── Brackets and accents ─────────────────────────

    /// <summary>The body between brackets as tall as it is (at least as a letter), centred on the math axis.</summary>
    private EquationBox Brackets(string left, EquationBox body, string right, Style style)
    {
        var size = style.Size;
        var axis = size * Axis;
        var reach = Math.Max(body.Ascent - axis, body.Descent + axis);
        var cover = Math.Max(reach, size * 0.5f) + size * 0.05f;
        // Word (and TeX, by its delimiter factor and shortfall) picks a bracket's size a little short of the boxes of
        // its content, whose letters don't reach their tops and bottoms: a subscript doesn't make a bracket grow, and
        // two rows of letters take Cambria Math's 1.64 em variant, as in Word (#356). Brackets built of parts (taller
        // than the font's largest) and drawn ones cover the content.
        var half = style.Tex ? Math.Max(Math.Max(reach * 0.7f, reach - size * 0.3f), size * 0.45f) : cover;
        var (leftBox, rightBox) = (Delimiter(left, half, cover, style, closing: false), Delimiter(right, half, cover, style, closing: true));
        var pad = size * 0.05f;
        var width = leftBox.Width + pad + body.Width + pad + rightBox.Width;
        return Place(width, Math.Max(body.Ascent, Math.Max(leftBox.Ascent, rightBox.Ascent)), Math.Max(body.Descent, Math.Max(leftBox.Descent, rightBox.Descent)),
            (0, 0, leftBox), (leftBox.Width + pad, 0, body), (width - rightBox.Width, 0, rightBox));
    }

    /// <summary>
    /// One bracket reaching <paramref name="half"/> above and below the axis: from the math font, else the text font's
    /// own for content no taller than a letter, else drawn.
    /// </summary>
    /// <param name="cover">The reach a bracket built of parts or drawn takes, at least <paramref name="half"/>.</param>
    private EquationBox Delimiter(string bracket, float half, float cover, Style style, bool closing)
    {
        if (bracket.Length == 0)
            return EquationBox.Empty;
        var size = style.Size;
        var axis = size * Axis;
        var character = bracket switch
        {
            "<" or "〈" => "⟨",
            ">" or "〉" => "⟩",
            _ => bracket,
        };
        if (Stretch(character, half * 2, size) is { } glyph)
        {
            if (glyph.Glyphs.Length > 1 && cover > half && Stretch(character, cover * 2, size) is { } built)
                glyph = built;
            return glyph.Box(0, axis - glyph.Height / 2, style.Color);
        }
        if (half <= size * 0.6f && Text(bracket, style, italic: false) is { Width: > 0 } text)
            return text;
        half = Math.Max(half, cover);
        var width = BracketWidth(bracket, size, half);
        var (top, bottom) = (-(axis + half), -(axis - half));
        return new EquationBox(width, axis + half, half - axis, Drawn((canvas, x, y) => Bracket(canvas, bracket, x, y + top, y + bottom, width, size, style.Color, closing)));
    }

    private static float BracketWidth(string bracket, float size, float half) => bracket switch
    {
        "" => 0,
        "|" or "‖" => size * (bracket == "|" ? 0.2f : 0.35f),
        "{" or "}" => size * 0.4f + half * 0.05f,
        _ => size * 0.3f + half * 0.05f,
    };

    /// <summary>
    /// A bracket drawn from <paramref name="top"/> to <paramref name="bottom"/> in a box <paramref name="width"/>
    /// wide, with a text bracket's weight: round brackets and braces swell in the middle of each arm.
    /// </summary>
    private static void Bracket(SKCanvas canvas, string bracket, float x, float top, float bottom, float width, float size, SKColor color, bool closing)
    {
        var (stem, hair) = (Math.Max(0.6f, size * 0.075f), Math.Max(0.25f, size * 0.02f));
        using var path = new SKPath();
        var (inner, outer) = closing ? (x + width * 0.25f, x + width * 0.75f) : (x + width * 0.75f, x + width * 0.25f);
        var middle = (top + bottom) / 2;
        var lobes = 0;
        switch (bracket)
        {
            case "(" or ")":
                path.MoveTo(inner, top);
                path.QuadTo(outer - (inner - outer) * 0.3f, middle, inner, bottom);
                lobes = 1;
                break;
            case "[" or "]":
                path.MoveTo(inner, top);
                path.LineTo(outer, top);
                path.LineTo(outer, bottom);
                path.LineTo(inner, bottom);
                break;
            case "⌊" or "⌋":
                path.MoveTo(outer, top);
                path.LineTo(outer, bottom);
                path.LineTo(inner, bottom);
                break;
            case "⌈" or "⌉":
                path.MoveTo(inner, top);
                path.LineTo(outer, top);
                path.LineTo(outer, bottom);
                break;
            case "{" or "}":
                {
                    var center = (inner + outer) / 2;
                    var q = (bottom - top) / 4;
                    path.MoveTo(inner, top);
                    path.QuadTo(center, top, center, top + q * 0.5f);
                    path.LineTo(center, middle - q * 0.5f);
                    path.QuadTo(center, middle, outer, middle);
                    path.QuadTo(center, middle, center, middle + q * 0.5f);
                    path.LineTo(center, bottom - q * 0.5f);
                    path.QuadTo(center, bottom, inner, bottom);
                    lobes = 2;
                    break;
                }
            case "⟨" or "⟩" or "<" or ">" or "〈" or "〉":
                path.MoveTo(inner, top);
                path.LineTo(outer, middle);
                path.LineTo(inner, bottom);
                break;
            case "‖":
                path.MoveTo(x + width * 0.3f, top);
                path.LineTo(x + width * 0.3f, bottom);
                path.MoveTo(x + width * 0.7f, top);
                path.LineTo(x + width * 0.7f, bottom);
                break;
            default:
                path.MoveTo(x + width / 2, top);
                path.LineTo(x + width / 2, bottom);
                break;
        }
        if (lobes > 0)
        {
            using var fill = new SKPaint { Color = color, IsAntialias = true };
            using var shape = Tapered(path, hair, stem, lobes);
            canvas.DrawPath(shape, fill);
            return;
        }
        using var pen = Pen(color, stem * 0.8f);
        pen.StrokeCap = SKStrokeCap.Butt;
        pen.StrokeJoin = SKStrokeJoin.Miter;
        canvas.DrawPath(path, pen);
    }

    /// <summary>
    /// The outline of a stroke along <paramref name="centre"/> (one contour) whose width goes from
    /// <paramref name="thin"/> at the ends to <paramref name="thick"/> in the middle of each of its
    /// <paramref name="lobes"/> equal lengths, and back.
    /// </summary>
    private static SKPath Tapered(SKPath centre, float thin, float thick, int lobes)
    {
        var outline = new SKPath();
        using var measure = new SKPathMeasure(centre, false);
        var length = measure.Length;
        if (!(length > 0))
            return outline;
        var steps = Math.Clamp((int)(length / 0.25f), 32, 400);
        var (left, right) = (new SKPoint[steps + 1], new SKPoint[steps + 1]);
        for (var i = 0; i <= steps; i++)
        {
            measure.GetPositionAndTangent(length * i / steps, out var point, out var tangent);
            var along = (float)i / steps * lobes;
            var swell = i == steps ? 0 : MathF.Sin(MathF.PI * (along - MathF.Floor(along)));
            var half = (thin + (thick - thin) * swell) / 2;
            var normal = new SKPoint(-tangent.Y * half, tangent.X * half);
            (left[i], right[i]) = (point + normal, point - normal);
        }
        outline.MoveTo(left[0]);
        for (var i = 1; i <= steps; i++)
            outline.LineTo(left[i]);
        for (var i = steps; i >= 0; i--)
            outline.LineTo(right[i]);
        outline.Close();
        return outline;
    }

    private EquationBox Accent(string command, EquationBox body, Style style)
    {
        // Word's bars (m:bar) arrive as OVERLINE and UNDERLINE.
        command = command switch
        {
            "OVERLINE" or "overline" => "bar",
            "UNDERLINE" or "underline" => "under",
            _ => command,
        };
        var size = style.Size;
        var stroke = Math.Max(0.4f, size * 0.05f);
        var top = Math.Max(body.Ascent, size * 0.7f) + size * 0.08f;
        var mark = size * 0.18f;
        var under = command == "under";
        var strike = command == "not";
        var (ascent, descent) = (under || strike ? body.Ascent : top + mark, under ? body.Descent + size * 0.12f : body.Descent);
        var accent = new EquationBox(body.Width, 0, 0, Drawn((canvas, x, y) =>
        {
            using var pen = Pen(style.Color, stroke);
            using var path = new SKPath();
            var (left, right, middle) = (x + size * 0.05f, x + body.Width - size * 0.02f, x + body.Width / 2 + size * 0.05f);
            var line = y - top - mark / 2;
            switch (command)
            {
                case "bar":
                    path.MoveTo(left, line);
                    path.LineTo(right, line);
                    break;
                case "under":
                    path.MoveTo(left, y + body.Descent + size * 0.06f);
                    path.LineTo(right, y + body.Descent + size * 0.06f);
                    break;
                case "vec" or "dyad":
                    path.MoveTo(left, line);
                    path.LineTo(right, line);
                    path.MoveTo(right - mark, line - mark / 2);
                    path.LineTo(right, line);
                    path.LineTo(right - mark, line + mark / 2);
                    if (command == "dyad")
                    {
                        path.MoveTo(left + mark, line - mark / 2);
                        path.LineTo(left, line);
                        path.LineTo(left + mark, line + mark / 2);
                    }
                    break;
                case "hat":
                    path.MoveTo(middle - Math.Max(mark, body.Width * 0.4f), line + mark / 2);
                    path.LineTo(middle, line - mark / 2);
                    path.LineTo(middle + Math.Max(mark, body.Width * 0.4f), line + mark / 2);
                    break;
                case "check":
                    path.MoveTo(middle - mark, line - mark / 2);
                    path.LineTo(middle, line + mark / 2);
                    path.LineTo(middle + mark, line - mark / 2);
                    break;
                case "tilde":
                    {
                        var w = Math.Max(mark * 2, body.Width * 0.8f);
                        path.MoveTo(middle - w / 2, line + mark / 3);
                        path.CubicTo(middle - w / 4, line - mark, middle + w / 4, line + mark, middle + w / 2, line - mark / 3);
                        break;
                    }
                case "breve" or "arch":
                    {
                        var w = command == "arch" ? body.Width : mark * 2;
                        path.MoveTo(middle - w / 2, line - mark / 2);
                        path.QuadTo(middle, line + (command == "arch" ? -mark : mark), middle + w / 2, line - mark / 2);
                        break;
                    }
                case "acute" or "grave":
                    path.MoveTo(middle - mark / 2, line + (command == "acute" ? mark / 2 : -mark / 2));
                    path.LineTo(middle + mark / 2, line + (command == "acute" ? -mark / 2 : mark / 2));
                    break;
                case "dot" or "ddot":
                    {
                        using var fill = new SKPaint { Color = style.Color, IsAntialias = true };
                        var r = Math.Max(0.5f, size * 0.05f);
                        if (command == "dot")
                        {
                            canvas.DrawCircle(middle, line, r, fill);
                        }
                        else
                        {
                            canvas.DrawCircle(middle - mark / 2, line, r, fill);
                            canvas.DrawCircle(middle + mark / 2, line, r, fill);
                        }
                        break;
                    }
                case "not":
                    path.MoveTo(x + body.Width * 0.75f, y - body.Ascent);
                    path.LineTo(x + body.Width * 0.25f, y + body.Descent);
                    break;
            }
            canvas.DrawPath(path, pen);
        }));
        return Place(body.Width, ascent, descent, (0, 0, body), (0, 0, accent));
    }
}
