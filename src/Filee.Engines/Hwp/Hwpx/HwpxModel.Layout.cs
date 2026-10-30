// Rendering detail of the document model: what a layout engine needs and the writers do not write.
//
// Values keep the meaning the source gave them: a Word "multiple" line rule stays a multiple of the font's line
// height instead of becoming a 한글 percentage, 한글's seven language slots stay seven, Word's four run fonts stay
// four. The writers keep using the members of HCharFormat / HParaFormat that hold the 한글 approximations, so
// nothing here changes a written file. Lengths are HWPUNIT, sizes 1/100 pt, like the rest of the model.

namespace Filee.Engines.Hwp.Hwpx;

// ───────────────────────── Characters ─────────────────────────

/// <summary>Rendering detail of a character format (see <see cref="HCharFormat.Layout"/>).</summary>
/// <param name="Slots">한글's per-language properties (fonts, 장평, 자간, 상대 크기, 글자 위치).</param>
/// <param name="WordFonts">Word's run fonts by character class.</param>
/// <param name="Scale">Word character scale (w:w) in percent: the width of every glyph, like 한글's 장평.</param>
/// <param name="LetterSpacing">Word letter spacing (w:spacing) as an absolute distance added after each character.</param>
/// <param name="Position">Word baseline shift (w:position): positive raises the text.</param>
/// <param name="FontSpace">
/// 한글 "글꼴에 어울리는 빈칸" (useFontSpace): a space is as wide as the font's own; off, it is half the font size.
/// Null where the source has no such setting (the font's own width).
/// </param>
/// <param name="Kerning">한글 "커닝" (useKerning): the font's kerning applies; null = it does (as in Word).</param>
internal sealed record HCharLayout(
    HLanguageSlots? Slots = null,
    HWordFonts? WordFonts = null,
    int? Scale = null,
    int? LetterSpacing = null,
    int? Position = null,
    bool? FontSpace = null,
    bool? Kerning = null)
{
    /// <summary>This layout with the members it leaves open taken from <paramref name="baseline"/>.</summary>
    public HCharLayout Over(HCharLayout? baseline) => baseline is null ? this : new HCharLayout(
        Slots is null ? baseline.Slots : Slots.Over(baseline.Slots),
        WordFonts ?? baseline.WordFonts,
        Scale ?? baseline.Scale,
        LetterSpacing ?? baseline.LetterSpacing,
        Position ?? baseline.Position,
        FontSpace ?? baseline.FontSpace,
        Kerning ?? baseline.Kerning);
}

/// <summary>한글's seven language slots (글꼴 언어): each character takes the properties of the slot its script falls into.</summary>
internal enum HLanguage
{
    Hangul,
    Latin,
    Hanja,
    Japanese,
    Other,
    Symbol,
    User,
}

/// <summary>Font and spacing of one language slot; null members are left to the style.</summary>
/// <param name="Ratio">장평: glyph width in percent.</param>
/// <param name="Spacing">자간: extra space in percent of the font size (negative = tighter).</param>
/// <param name="RelativeSize">상대 크기: size in percent of the run's font size.</param>
/// <param name="Offset">글자 위치: baseline shift in percent of the font size (negative = up).</param>
internal readonly record struct HSlotFormat(string? Font = null, int? Ratio = null, int? Spacing = null, int? RelativeSize = null, int? Offset = null)
{
    /// <summary>This slot with the members it leaves open taken from <paramref name="baseline"/>.</summary>
    public HSlotFormat Over(HSlotFormat baseline) => new(
        Font ?? baseline.Font, Ratio ?? baseline.Ratio, Spacing ?? baseline.Spacing, RelativeSize ?? baseline.RelativeSize, Offset ?? baseline.Offset);
}

/// <summary>The seven slots of a 한글 character shape.</summary>
internal sealed record HLanguageSlots(
    HSlotFormat Hangul, HSlotFormat Latin, HSlotFormat Hanja, HSlotFormat Japanese, HSlotFormat Other, HSlotFormat Symbol, HSlotFormat User)
{
    public static readonly HLanguage[] Languages = Enum.GetValues<HLanguage>();

    /// <summary>The same properties in every slot.</summary>
    public static HLanguageSlots All(HSlotFormat slot) => new(slot, slot, slot, slot, slot, slot, slot);

    public HSlotFormat this[HLanguage language] => language switch
    {
        HLanguage.Hangul => Hangul,
        HLanguage.Latin => Latin,
        HLanguage.Hanja => Hanja,
        HLanguage.Japanese => Japanese,
        HLanguage.Other => Other,
        HLanguage.Symbol => Symbol,
        _ => User,
    };

    public HLanguageSlots With(HLanguage language, HSlotFormat slot) => language switch
    {
        HLanguage.Hangul => this with { Hangul = slot },
        HLanguage.Latin => this with { Latin = slot },
        HLanguage.Hanja => this with { Hanja = slot },
        HLanguage.Japanese => this with { Japanese = slot },
        HLanguage.Other => this with { Other = slot },
        HLanguage.Symbol => this with { Symbol = slot },
        _ => this with { User = slot },
    };

    /// <summary>Every slot with the members it leaves open taken from <paramref name="baseline"/>.</summary>
    public HLanguageSlots Over(HLanguageSlots? baseline)
    {
        if (baseline is null)
            return this;
        var result = this;
        foreach (var language in Languages)
            result = result.With(language, this[language].Over(baseline[language]));
        return result;
    }
}

/// <summary>
/// Word's run fonts (w:rFonts): ASCII characters, other Latin (high ANSI), East Asian and complex-script text; the
/// hint says which font ambiguous characters (quotes, dashes) take. Theme references are resolved to names.
/// </summary>
internal sealed record HWordFonts(string? Ascii, string? HighAnsi, string? EastAsia, string? ComplexScript, string? Hint = null);

// ───────────────────────── Paragraphs ─────────────────────────

/// <summary>Rendering detail of a paragraph format (see <see cref="HParaFormat.Layout"/>).</summary>
/// <param name="LineHeight">The line rule with the source's meaning (the writers use <see cref="HParaFormat.LineSpacing"/>).</param>
/// <param name="Border">Border and background; <see cref="HParaBorder.None"/> = none, null = left to the style.</param>
/// <param name="SnapToGrid">Lines follow the section's line grid (한글 "줄 격자 사용", Word w:snapToGrid).</param>
/// <param name="FontLineHeight">한글 "글꼴에 어울리는 줄 높이": line height from the font's own metrics.</param>
internal sealed record HParaLayout(
    HLineHeight? LineHeight = null,
    HParaBorder? Border = null,
    HLineBreaking? Breaking = null,
    bool? SnapToGrid = null,
    bool? FontLineHeight = null)
{
    /// <summary>This layout with the members it leaves open taken from <paramref name="baseline"/>.</summary>
    public HParaLayout Over(HParaLayout? baseline) => baseline is null ? this : new HParaLayout(
        LineHeight ?? baseline.LineHeight,
        Border ?? baseline.Border,
        Breaking is null ? baseline.Breaking : Breaking.Over(baseline.Breaking),
        SnapToGrid ?? baseline.SnapToGrid,
        FontLineHeight ?? baseline.FontLineHeight);
}

internal enum HLineHeightRule
{
    /// <summary>한글 percent of the font size (100 = the size itself; 한글's default is 160).</summary>
    Percent,

    /// <summary>Word "multiple" (w:lineRule="auto"): <c>Value</c> in 1/240 of the font's own line height (240 = single).</summary>
    Multiple,

    /// <summary>Exactly <c>Value</c> high (한글 FIXED, Word exact).</summary>
    Exact,

    /// <summary>At least <c>Value</c> high (한글 AT_LEAST, Word atLeast).</summary>
    AtLeast,

    /// <summary>한글 "여백만 지정" (BETWEEN_LINES): the line's own height plus <c>Value</c>.</summary>
    BetweenLines,
}

/// <summary>A line height rule as the source states it.</summary>
/// <param name="GridPitch">
/// Pitch of the document grid the lines snap to (Word: each line takes a whole number of grid lines), 0 = none.
/// Set by the reader where the source snaps this paragraph.
/// </param>
internal readonly record struct HLineHeight(HLineHeightRule Rule, int Value, int GridPitch = 0)
{
    /// <summary>The rule a writer-side <see cref="HLineSpacing"/> stands for, when the source gave no other.</summary>
    public static HLineHeight From(HLineSpacing spacing) => spacing.Kind switch
    {
        HLineSpacingKind.Fixed => new HLineHeight(HLineHeightRule.Exact, spacing.Value),
        HLineSpacingKind.AtLeast => new HLineHeight(HLineHeightRule.AtLeast, spacing.Value),
        _ => new HLineHeight(HLineHeightRule.Percent, spacing.Value),
    };
}

/// <summary>Border and background of a paragraph.</summary>
/// <param name="Offsets">Distance between the text and each border line.</param>
/// <param name="Connect">Consecutive paragraphs with the same border share one box (한글 "문단 테두리 연결"; Word always).</param>
/// <param name="IgnoreMargin">The box spans the column, not just the paragraph's indents (한글 "문단 여백 무시").</param>
/// <param name="Between">Line between consecutive paragraphs of one box (Word w:between); null = none.</param>
internal sealed record HParaBorder(HBorders Borders, string? Fill, HInsets Offsets, bool Connect = false, bool IgnoreMargin = false, HBorder? Between = null)
{
    public static readonly HParaBorder None = new(HBorders.Empty, null, default);

    public bool IsVisible => Fill is not null || Between is { Style: not HBorderStyle.None } ||
                             new[] { Borders.Left, Borders.Right, Borders.Top, Borders.Bottom }.Any(b => b.Style != HBorderStyle.None);
}

/// <summary>Where lines may break and how they are fitted. Null members are left to the style (or the default rules).</summary>
/// <param name="Latin">Latin words: kept whole (어절), hyphenated or broken anywhere (한글 breakLatinWord, Word w:wordWrap).</param>
/// <param name="NonLatin">
/// Korean words: kept whole (어절) or broken at any syllable (글자) (한글 breakNonLatinWord, whose BREAK_WORD is 어절 and
/// KEEP_WORD 글자).
/// </param>
/// <param name="LineWrap">한글 lineWrap: wrap, squeeze the line, or keep it on one line.</param>
/// <param name="Kinsoku">East Asian line start / end rules apply (Word w:kinsoku; 한글 applies its 금칙 characters).</param>
/// <param name="OverflowPunctuation">Punctuation may hang past the line end (Word w:overflowPunct).</param>
/// <param name="CompressPunctuation">Punctuation at the line start is compressed (Word w:topLinePunct).</param>
/// <param name="AutoSpaceLatin">Space between East Asian and Latin text (한글 autoSpacing eAsianEng, Word w:autoSpaceDE).</param>
/// <param name="AutoSpaceDigits">Space between East Asian text and digits (한글 eAsianNum, Word w:autoSpaceDN).</param>
/// <param name="Condense">한글 "최소 공백": how far spaces may shrink to fit a line, in percent.</param>
internal sealed record HLineBreaking(
    HLatinBreak? Latin = null,
    HNonLatinBreak? NonLatin = null,
    HLineWrap? LineWrap = null,
    bool? Kinsoku = null,
    bool? OverflowPunctuation = null,
    bool? CompressPunctuation = null,
    bool? AutoSpaceLatin = null,
    bool? AutoSpaceDigits = null,
    int? Condense = null)
{
    /// <summary>These settings with the members they leave open taken from <paramref name="baseline"/>.</summary>
    public HLineBreaking Over(HLineBreaking? baseline) => baseline is null ? this : new HLineBreaking(
        Latin ?? baseline.Latin, NonLatin ?? baseline.NonLatin, LineWrap ?? baseline.LineWrap, Kinsoku ?? baseline.Kinsoku,
        OverflowPunctuation ?? baseline.OverflowPunctuation, CompressPunctuation ?? baseline.CompressPunctuation,
        AutoSpaceLatin ?? baseline.AutoSpaceLatin, AutoSpaceDigits ?? baseline.AutoSpaceDigits, Condense ?? baseline.Condense);
}

internal enum HLatinBreak
{
    KeepWord,
    Hyphenation,
    BreakWord,
}

internal enum HNonLatinBreak
{
    KeepWord,
    BreakWord,
}

internal enum HLineWrap
{
    Break,
    Squeeze,
    Keep,
}

/// <summary>Custom East Asian line breaking characters for a language (Word w:noLineBreaksBefore / After).</summary>
/// <param name="Language">Language tag ("ja-JP", "zh-CN" ...); null = every language.</param>
/// <param name="NotAtLineStart">Characters that may not start a line.</param>
/// <param name="NotAtLineEnd">Characters that may not end a line.</param>
internal sealed record HKinsoku(string? Language, string? NotAtLineStart, string? NotAtLineEnd);

/// <summary>
/// Line layout stored in the file (HWPX hp:linesegarray), one segment per line part. A paragraph that a table splits
/// in the model keeps the array on its first part, with text positions counted over the whole stored paragraph.
/// </summary>
/// <param name="Estimated">The values were estimated by Filee, not laid out by 한글, so they must not be trusted.</param>
internal sealed record HLineLayout(IReadOnlyList<HLineSegment> Segments, bool Estimated);

/// <summary>One stored line (all lengths HWPUNIT).</summary>
/// <param name="TextPosition">First character of the line in the paragraph's text.</param>
/// <param name="VerticalPosition">Top of the line from the top of the paragraph list (column, cell, ...).</param>
/// <param name="VerticalSize">Height of the line including its line spacing share.</param>
/// <param name="TextHeight">Height of the text itself.</param>
/// <param name="Baseline">Distance from the top of the line to its baseline.</param>
/// <param name="Spacing">Space after the line (line spacing).</param>
/// <param name="HorizontalPosition">Start of the line part from the column edge.</param>
/// <param name="HorizontalSize">Width of the line part.</param>
/// <param name="Flags">한글's line flags (first line of a page or column, empty segment, ...).</param>
internal readonly record struct HLineSegment(
    int TextPosition, int VerticalPosition, int VerticalSize, int TextHeight, int Baseline, int Spacing,
    int HorizontalPosition, int HorizontalSize, int Flags);

// ───────────────────────── Sections ─────────────────────────

internal enum HGridKind
{
    None,

    /// <summary>Lines snap to the line pitch.</summary>
    Lines,

    /// <summary>Lines snap to the line pitch and characters to the character pitch.</summary>
    LinesAndChars,

    /// <summary>Characters snap to the character pitch (Word snapToChars).</summary>
    SnapToChars,
}

/// <summary>A page grid.</summary>
/// <param name="LinePitch">Distance between grid lines (Word linePitch, 한글 lineGrid); 0 = none.</param>
/// <param name="CharPitch">Character pitch (한글 charGrid); 0 = none or derived from the font (Word).</param>
/// <param name="CharSpace">Word w:charSpace as stored: extra character pitch in 1/4096 pt over the default font size.</param>
internal sealed record HDocumentGrid(HGridKind Kind, int LinePitch, int CharPitch = 0, int CharSpace = 0);

internal enum HMasterPageKind
{
    Both,
    Even,
    Odd,

    /// <summary>The last page of the section.</summary>
    LastPage,

    /// <summary>The page <see cref="HMasterPage.PageNumber"/>.</summary>
    OptionalPage,
}

/// <summary>A 한글 master page (바탕쪽): paragraphs and objects drawn on every page it applies to.</summary>
internal sealed class HMasterPage(HMasterPageKind kind)
{
    public HMasterPageKind Kind { get; } = kind;

    /// <summary>Page of an <see cref="HMasterPageKind.OptionalPage"/> master page.</summary>
    public int PageNumber { get; set; }

    /// <summary>한글 "겹치기": drawn together with the other master pages of the section.</summary>
    public bool Duplicate { get; set; }

    /// <summary>한글 "앞으로 보내기": drawn in front of the body text.</summary>
    public bool Front { get; set; }

    public List<HBlock> Blocks { get; } = [];
}

// ───────────────────────── Tables and objects ─────────────────────────

/// <summary>How a table splits at the end of a page (OWPML hp:tbl pageBreak).</summary>
internal enum HTablePageBreak
{
    /// <summary>나누지 않음: the table moves to the next page whole.</summary>
    None,

    /// <summary>나눔: the table splits, inside rows too.</summary>
    Table,

    /// <summary>셀 단위로 나눔: the table splits between rows only.</summary>
    Cell,
}

/// <summary>Parts of a picture cut away at each edge, as fractions (0..1) of the source picture.</summary>
internal readonly record struct HCrop(double Left, double Top, double Right, double Bottom)
{
    public bool IsEmpty => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;
}

/// <summary>
/// Rotation and mirroring of an object about its centre. <c>Rotation</c> is in degrees as the source stores it
/// (DrawingML measures clockwise; 한글's rotationInfo angle is kept as written).
/// </summary>
internal sealed record HTransform(double Rotation = 0, bool FlipHorizontal = false, bool FlipVertical = false)
{
    public bool IsIdentity => Rotation == 0 && !FlipHorizontal && !FlipVertical;
}

internal readonly record struct HPoint(int X, int Y);

/// <summary>
/// Outline of a polygon, curve or arc in the object's own coordinates, which span <c>Width</c> × <c>Height</c>
/// (한글 orgSz); the object is drawn scaled to its displayed size.
/// </summary>
/// <param name="Points">Polygon corners in order (한글 hc:pt).</param>
/// <param name="Segments">Curve segments as 한글 stores them (hp:seg): each a straight or curved piece from start to end.</param>
/// <param name="Arc">Arc of an ellipse (한글 hp:arc).</param>
internal sealed record HGeometry(int Width, int Height, IReadOnlyList<HPoint>? Points = null, IReadOnlyList<HCurveSegment>? Segments = null, HArc? Arc = null);

/// <summary>A piece of a 한글 curve; <c>Curve</c> is false for a straight piece (seg type LINE).</summary>
internal readonly record struct HCurveSegment(bool Curve, HPoint Start, HPoint End);

/// <summary>Arc of the ellipse around <c>Center</c> with axis end points <c>Axis1</c> and <c>Axis2</c> (한글 hp:arc).</summary>
internal readonly record struct HArc(HArcKind Kind, HPoint Center, HPoint Axis1, HPoint Axis2);

internal enum HArcKind
{
    /// <summary>The open arc.</summary>
    Normal,

    /// <summary>Closed through the centre.</summary>
    Pie,

    /// <summary>Closed by a straight line between the ends.</summary>
    Chord,
}
