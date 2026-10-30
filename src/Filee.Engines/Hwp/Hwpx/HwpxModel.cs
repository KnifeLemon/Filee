// In-memory document model between the readers (DOCX, Pandoc AST, plain text) and HwpxWriter.
//
// Lengths are HWPUNIT (1/7200 inch) and font sizes are 1/100 pt, like OWPML itself. A null format field means
// "inherit from the 한글 style" (the Pandoc reader only knows bold/italic/..., the DOCX reader knows everything);
// StyleResolver turns those into concrete values.
//
// Members the writers write describe the document in 한글's terms. What only a renderer needs, kept with the meaning
// the source gave it (Word line rules, 한글's language slots, stored line layout, crops, master pages ...), is in
// HwpxModel.Layout.cs; the writers ignore it, so adding it changes no written file.

namespace Filee.Engines.Hwp.Hwpx;

/// <summary>A document ready to be written as HWPX.</summary>
internal sealed class HDocument
{
    public string? Title { get; set; }
    public List<HSection> Sections { get; } = [];

    /// <summary>Custom East Asian line breaking characters (Word settings), by language; empty = the usual rules.</summary>
    public List<HKinsoku> Kinsoku { get; } = [];
}

/// <summary>A run of pages sharing page setup, headers and footers (한글 "구역").</summary>
internal sealed class HSection
{
    /// <summary>Paper and margins; null keeps the template's A4 page.</summary>
    public HPage? Page { get; set; }

    public HColumns Columns { get; set; } = HColumns.Single;
    public List<HHeaderFooter> Headers { get; } = [];
    public List<HHeaderFooter> Footers { get; } = [];

    /// <summary>Hide header / footer on the first page (DOCX "different first page" with an empty first header).</summary>
    public bool HideFirstHeader { get; set; }
    public bool HideFirstFooter { get; set; }

    /// <summary>Restart page numbering at this number; null continues from the previous section.</summary>
    public int? StartPageNumber { get; set; }

    /// <summary>Line and character grid of the pages (Word's document grid, 한글's 줄/글자 격자); null = none.</summary>
    public HDocumentGrid? Grid { get; set; }

    /// <summary>Master pages (한글 바탕쪽) drawn behind the body of the section's pages.</summary>
    public List<HMasterPage> MasterPages { get; } = [];

    public List<HBlock> Blocks { get; } = [];
}

/// <summary>Paper size and margins.</summary>
/// <param name="Width">Paper width as printed (landscape pages are wider than tall).</param>
/// <param name="Height">Paper height as printed.</param>
/// <param name="Top">Paper edge to the header (한글 "위쪽"); the body starts at <c>Top + Header</c>.</param>
/// <param name="Header">Height of the header area (한글 "머리말").</param>
/// <param name="Bottom">Paper edge to the footer (한글 "아래쪽").</param>
/// <param name="Footer">Height of the footer area (한글 "꼬리말").</param>
internal sealed record HPage(int Width, int Height, int Left, int Right, int Top, int Bottom, int Header, int Footer, int Gutter)
{
    public bool Landscape => Width > Height;
    public int TextWidth => Math.Max(2000, Width - Left - Right - Gutter);
}

/// <summary>Newspaper-style columns.</summary>
internal sealed record HColumns(int Count, int Gap, bool Separator)
{
    public static readonly HColumns Single = new(1, 0, false);
}

/// <summary>Which pages a header or footer applies to.</summary>
internal enum HPageType
{
    Both,
    Even,
    Odd,
}

internal sealed class HHeaderFooter(HPageType pages)
{
    public HPageType Pages { get; } = pages;
    public List<HBlock> Blocks { get; } = [];
}

// ───────────────────────── Blocks ─────────────────────────

internal abstract class HBlock;

internal sealed class HParagraph : HBlock
{
    public HParaFormat Format { get; set; }

    /// <summary>Format of the paragraph mark: decides the height of an empty paragraph.</summary>
    public HCharFormat MarkFormat { get; set; }

    /// <summary>1..9 for headings (mapped to the "Heading n" styles, which are outline levels in 한글), else 0.</summary>
    public int HeadingLevel { get; set; }

    /// <summary>List numbering of this paragraph, or null.</summary>
    public HListRef? List { get; set; }

    public bool PageBreakBefore { get; set; }
    public bool ColumnBreakBefore { get; set; }

    /// <summary>Columns change here without a new page (DOCX continuous section break); null = unchanged.</summary>
    public HColumns? ColumnsChange { get; set; }

    /// <summary>Line layout stored with the paragraph (HWPX hp:linesegarray), or null.</summary>
    public HLineLayout? Lines { get; set; }

    public List<HInline> Inlines { get; } = [];
}

/// <summary>A list a paragraph belongs to.</summary>
/// <param name="Numbering">List definition (shared by all items of one list).</param>
/// <param name="Level">0-based nesting level.</param>
/// <param name="Numbered">False for continuation paragraphs of an item (no bullet or number).</param>
internal sealed record HListRef(HNumbering Numbering, int Level, bool Numbered);

/// <summary>A list definition: one entry per nesting level.</summary>
internal sealed class HNumbering
{
    public List<HNumberingLevel> Levels { get; } = [];
}

/// <param name="Format">OWPML numFormat: DIGIT, LATIN_SMALL, HANGUL_SYLLABLE, ... (ignored for bullets).</param>
/// <param name="Text">Number text with <c>^n</c> for the level-n counter ("^1.", "(^2)"), or the bullet character.</param>
internal sealed record HNumberingLevel(string Format, string Text, int Start, bool Bullet);

internal sealed class HTable : HBlock
{
    public List<HRow> Rows { get; } = [];

    /// <summary>Absolute column widths; null = use <see cref="RelativeWidths"/> or share the text width equally.</summary>
    public int[]? ColumnWidths { get; set; }

    /// <summary>Column widths as fractions of the text width (Pandoc); zero entries share the rest.</summary>
    public double[]? RelativeWidths { get; set; }

    public int ColumnCount { get; set; }
    public HAlign Align { get; set; } = HAlign.Left;
    public int Indent { get; set; }

    /// <summary>Cell padding; null = 한글 default.</summary>
    public HInsets? CellMargin { get; set; }

    /// <summary>Space kept free around the table (한글 hp:outMargin); null = none. Only the layout engine uses it.</summary>
    public HInsets? OuterMargin { get; set; }

    /// <summary>
    /// Distance from its paragraph's top to a table 한글 places at the paragraph rather than as a character (hp:pos
    /// vertRelTo PARA, vertOffset). Only the layout engine uses it.
    /// </summary>
    public int VerticalOffset { get; set; }

    /// <summary>
    /// The table is the first thing of its 한글 paragraph, whose text is the model paragraph after the table (it keeps
    /// the paragraph's stored lines). Only the layout engine uses it.
    /// </summary>
    public bool OpensParagraph { get; set; }

    /// <summary>Borders used where a cell does not define its own; null = thin solid grid.</summary>
    public HBorders? Borders { get; set; }

    /// <summary>The table starts a new page (a page break set in Excel).</summary>
    public bool PageBreakBefore { get; set; }

    /// <summary>
    /// The table takes no more height than its rows (fixed layouts: OFD, whose next paragraph has its own distance).
    /// In HWPX it gets no 5 mm space under it and the paragraph holding it no empty line of its own, so a full page
    /// does not run over.
    /// </summary>
    public bool Tight { get; set; }

    /// <summary>How the table splits at the end of a page (한글 "쪽 경계에서"); null = the source does not say.</summary>
    public HTablePageBreak? PageBreak { get; set; }

    public List<HBlock> Caption { get; } = [];
}

internal sealed class HRow
{
    public List<HCell> Cells { get; } = [];

    /// <summary>Minimum row height; null = fit content.</summary>
    public int? Height { get; set; }

    public bool Header { get; set; }

    /// <summary>The row is not split across pages (Word w:cantSplit).</summary>
    public bool CantSplit { get; set; }
}

internal sealed class HCell
{
    /// <summary>Grid column of the cell; -1 = right after the previous cell (row spans are skipped).</summary>
    public int Column { get; set; } = -1;
    public int RowSpan { get; set; } = 1;
    public int ColSpan { get; set; } = 1;
    public HVerticalAlign VerticalAlign { get; set; }
    public string? Fill { get; set; }
    public HBorders? Borders { get; set; }
    /// <summary>Cell-specific padding; null inherits the table's padding.</summary>
    public HInsets? CellMargin { get; set; }
    public List<HBlock> Blocks { get; } = [];
}

internal enum HVerticalAlign
{
    Top,
    Center,
    Bottom,
}

internal readonly record struct HInsets(int Left, int Right, int Top, int Bottom);

/// <summary>One border line. Width is in mm (snapped to the widths 한글 offers when written).</summary>
internal readonly record struct HBorder(HBorderStyle Style, double WidthMm, string Color)
{
    public static readonly HBorder None = new(HBorderStyle.None, 0.1, "#000000");
    public static readonly HBorder Thin = new(HBorderStyle.Solid, 0.12, "#000000");
}

internal enum HBorderStyle
{
    None,
    Solid,
    Dash,
    Dot,
    DashDot,
    Double,
}

internal readonly record struct HBorders(HBorder Left, HBorder Right, HBorder Top, HBorder Bottom)
{
    public static readonly HBorders Grid = new(HBorder.Thin, HBorder.Thin, HBorder.Thin, HBorder.Thin);
    public static readonly HBorders Empty = new(HBorder.None, HBorder.None, HBorder.None, HBorder.None);
}

// ───────────────────────── Inlines ─────────────────────────

internal abstract class HInline;

internal class HText(string text, HCharFormat format) : HInline
{
    public string Text { get; } = text;
    public HCharFormat Format { get; } = format;
}

/// <summary>
/// An equation (from Word math): a 한글 equation object in HWPX. It is text too, its linear form ("(a)/(b)", "x^2"),
/// which the writers without equations (HTML, EPUB, Markdown, plain text) write instead.
/// </summary>
internal sealed class HEquation(MathNode math, HCharFormat format) : HText(EquationScript.Linear(math), format)
{
    public MathNode Math { get; } = math;
}

internal sealed class HLineBreak(HCharFormat format) : HInline
{
    public HCharFormat Format { get; } = format;
}

internal sealed class HTab(HCharFormat format) : HInline
{
    public HCharFormat Format { get; } = format;
}

/// <summary>Hyperlink to a URL, or to a bookmark when the target starts with '#'.</summary>
internal sealed class HLink(string target) : HInline
{
    public string Target { get; } = target;
    public List<HInline> Content { get; } = [];
}

internal sealed class HBookmark(string name) : HInline
{
    public string Name { get; } = name;
}

internal sealed class HNote(bool endnote) : HInline
{
    public bool Endnote { get; } = endnote;
    public List<HBlock> Blocks { get; } = [];
}

internal enum HFieldKind
{
    PageNumber,
    TotalPages,
}

/// <summary>Automatic number (page number in headers and footers).</summary>
internal sealed class HField(HFieldKind kind, HCharFormat format) : HInline
{
    public HFieldKind Kind { get; } = kind;
    public HCharFormat Format { get; } = format;
}

/// <summary>Picture from a local file.</summary>
internal sealed class HImage(string path) : HInline
{
    public string Path { get; } = path;

    /// <summary>Display size; null = natural size (capped to the text width).</summary>
    public int? Width { get; set; }
    public int? Height { get; set; }

    /// <summary>Position for floating pictures; null = inline, moves with the text like a character.</summary>
    public HAnchor? Anchor { get; set; }

    /// <summary>Parts of the picture cut away; null = the whole picture.</summary>
    public HCrop? Crop { get; set; }

    /// <summary>Rotation and mirroring; null = none.</summary>
    public HTransform? Transform { get; set; }

    /// <summary>Stacking order among floating objects (higher is in front); null = document order.</summary>
    public int? ZOrder { get; set; }
}

/// <summary>A box with its own paragraphs (DOCX text box, 한글 글상자).</summary>
internal sealed class HTextBox : HInline
{
    public int Width { get; set; }
    public int Height { get; set; }
    public HAnchor? Anchor { get; set; }

    /// <summary>Rotation and mirroring; null = none.</summary>
    public HTransform? Transform { get; set; }

    /// <summary>Stacking order among floating objects (higher is in front); null = document order.</summary>
    public int? ZOrder { get; set; }

    /// <summary>Outline of a polygon, curve or arc holding the text (<see cref="Shape"/> says which); else null.</summary>
    public HGeometry? Geometry { get; set; }
    public string? Fill { get; set; }
    public HGradient? Gradient { get; set; }
    public HBorder Line { get; set; } = HBorder.Thin;

    /// <summary>Outline of the box: <see cref="HShapeKind.Rectangle"/> (default) or <see cref="HShapeKind.Ellipse"/>.</summary>
    public HShapeKind Shape { get; set; }

    /// <summary>Corner rounding of a rectangular box in percent (0 = square).</summary>
    public int CornerRatio { get; set; }

    public HInsets Padding { get; set; } = new(283, 283, 283, 283);
    public HVerticalAlign VerticalAlign { get; set; }
    public List<HBlock> Blocks { get; } = [];
}

internal enum HShapeKind
{
    Rectangle,
    Ellipse,
    Line,

    /// <summary>한글 다각형: <see cref="HGeometry.Points"/>. Only in models read for layout (the writers have none).</summary>
    Polygon,

    /// <summary>한글 곡선: <see cref="HGeometry.Segments"/>. Only in models read for layout.</summary>
    Curve,

    /// <summary>한글 호: <see cref="HGeometry.Arc"/>. Only in models read for layout.</summary>
    Arc,
}

/// <summary>A drawing without text: rectangle, ellipse or straight line (and, read for layout, polygon, curve or arc).</summary>
internal sealed class HShape(HShapeKind kind) : HInline
{
    public HShapeKind Kind { get; } = kind;
    public int Width { get; set; }
    public int Height { get; set; }
    public HAnchor? Anchor { get; set; }

    /// <summary>Rotation and mirroring as the source stores them; null = none.</summary>
    public HTransform? Transform { get; set; }

    /// <summary>Stacking order among floating objects (higher is in front); null = document order.</summary>
    public int? ZOrder { get; set; }

    /// <summary>Outline of a polygon, curve or arc; null for the other kinds.</summary>
    public HGeometry? Geometry { get; set; }
    public string? Fill { get; set; }
    public HGradient? Gradient { get; set; }
    public HBorder Line { get; set; } = HBorder.Thin;

    /// <summary>Corner rounding of rectangles in percent (0 = square).</summary>
    public int CornerRatio { get; set; }

    /// <summary>Lines run from top-left to bottom-right unless flipped.</summary>
    public bool FlipHorizontal { get; set; }
    public bool FlipVertical { get; set; }
}

/// <summary>Linear gradient fill: colours from start to end, angle in degrees.</summary>
internal sealed record HGradient(IReadOnlyList<string> Colors, int Angle);

/// <summary>Position of a floating object.</summary>
/// <param name="HorizontalRelativeTo">PAPER, PAGE (= margin box), COLUMN or PARA — OWPML horzRelTo.</param>
/// <param name="HorizontalAlign">LEFT, CENTER, RIGHT, INSIDE, OUTSIDE — used with a zero offset.</param>
/// <param name="VerticalRelativeTo">PAPER, PAGE or PARA — OWPML vertRelTo.</param>
/// <param name="VerticalAlign">TOP, CENTER, BOTTOM, INSIDE, OUTSIDE.</param>
/// <param name="Wrap">SQUARE, TOP_AND_BOTTOM, BEHIND_TEXT or IN_FRONT_OF_TEXT — OWPML textWrap.</param>
internal sealed record HAnchor(
    string HorizontalRelativeTo, string HorizontalAlign, int HorizontalOffset,
    string VerticalRelativeTo, string VerticalAlign, int VerticalOffset,
    string Wrap, bool AllowOverlap);

// ───────────────────────── Formats ─────────────────────────

internal enum HAlign
{
    Left,
    Center,
    Right,
    Justify,
    Distribute,
}

internal enum HLineSpacingKind
{
    /// <summary>Percent of the font size (한글 default is 160%).</summary>
    Percent,
    Fixed,
    AtLeast,
}

internal readonly record struct HLineSpacing(HLineSpacingKind Kind, int Value);

/// <summary>Paragraph formatting; null members keep the 한글 style's value.</summary>
/// <param name="FirstLine">First line indent relative to <paramref name="Left"/>; negative = hanging indent.</param>
/// <param name="RightToLeft">
/// A right-to-left paragraph (Arabic, Hebrew): its lines start at the right. <paramref name="Align"/>,
/// <paramref name="Left"/> and <paramref name="Right"/> then name the start and the end side, as Word's w:bidi
/// paragraphs do (Left aligns to the right edge). Written to DOCX only; HWPX has no right-to-left paragraphs.
/// </param>
/// <param name="LineSpacing">Line spacing in 한글's terms, as the writers write it (Word spacing converted).</param>
/// <param name="Layout">
/// Rendering detail the writers do not write (the source's own line rule, borders, line breaking ...). It takes no
/// part in equality: formats that differ only here are written alike. <see cref="SameLayout"/> compares it too.
/// </param>
internal readonly record struct HParaFormat(
    HAlign? Align = null,
    int? Left = null,
    int? Right = null,
    int? FirstLine = null,
    int? Before = null,
    int? After = null,
    HLineSpacing? LineSpacing = null,
    bool? KeepWithNext = null,
    bool? KeepLines = null,
    bool? WidowOrphan = null,
    IReadOnlyList<HTabStop>? Tabs = null,
    bool? RightToLeft = null,
    HParaLayout? Layout = null)
{
    /// <summary>No member the writers write is set (<see cref="Layout"/> does not count).</summary>
    public bool IsEmpty => this == default;

    /// <summary>Equal, including <see cref="Layout"/>.</summary>
    public bool SameLayout(HParaFormat other) => Equals(other) && Equals(Layout, other.Layout);

    // Tab stop lists compare by content so equal formats share one paraPr.
    public bool Equals(HParaFormat other) =>
        Align == other.Align && Left == other.Left && Right == other.Right && FirstLine == other.FirstLine &&
        Before == other.Before && After == other.After && LineSpacing == other.LineSpacing &&
        KeepWithNext == other.KeepWithNext && KeepLines == other.KeepLines && WidowOrphan == other.WidowOrphan &&
        RightToLeft == other.RightToLeft && (Tabs ?? []).SequenceEqual(other.Tabs ?? []);

    public override int GetHashCode() =>
        HashCode.Combine(Align, Left, Right, FirstLine, Before, After, LineSpacing, HashCode.Combine(KeepWithNext, KeepLines, WidowOrphan, RightToLeft, Tabs?.Count ?? 0));
}

internal enum HTabKind
{
    Left,
    Right,
    Center,
    Decimal,
}

/// <param name="Leader">OWPML line type of the leader: NONE, DOT, DASH, SOLID ...</param>
internal readonly record struct HTabStop(int Position, HTabKind Kind, string Leader);

/// <summary>Character formatting; null members keep the 한글 style's value.</summary>
/// <param name="Size">Font size in 1/100 pt.</param>
/// <param name="Font">Font for Latin text.</param>
/// <param name="EastAsianFont">Font for Hangul / Hanja / Japanese text and CJK punctuation (the symbol slot).</param>
/// <param name="Color">Text colour "#RRGGBB".</param>
/// <param name="Shade">Background (highlight) colour "#RRGGBB".</param>
/// <param name="Spacing">Letter spacing in percent of the font size, as 한글 has it (Word's absolute spacing converted).</param>
/// <param name="Layout">
/// Rendering detail the writers do not write (한글's per-language slots, Word's four fonts and exact spacing). It
/// takes no part in equality, hashing or <see cref="ToString"/>: formats that differ only here are written alike.
/// <see cref="SameLayout"/> compares it too.
/// </param>
internal readonly record struct HCharFormat(
    bool? Bold = null,
    bool? Italic = null,
    bool? Underline = null,
    bool? Strike = null,
    bool? Superscript = null,
    bool? Subscript = null,
    int? Size = null,
    string? Font = null,
    string? EastAsianFont = null,
    string? Color = null,
    string? Shade = null,
    int? Spacing = null,
    HCharLayout? Layout = null)
{
    /// <summary>No member the writers write is set (<see cref="Layout"/> does not count).</summary>
    public bool IsEmpty => this == default;

    /// <summary>Equal, including <see cref="Layout"/>.</summary>
    public bool SameLayout(HCharFormat other) => Equals(other) && Equals(Layout, other.Layout);

    public bool Equals(HCharFormat other) =>
        Bold == other.Bold && Italic == other.Italic && Underline == other.Underline && Strike == other.Strike &&
        Superscript == other.Superscript && Subscript == other.Subscript && Size == other.Size &&
        Font == other.Font && EastAsianFont == other.EastAsianFont && Color == other.Color && Shade == other.Shade &&
        Spacing == other.Spacing;

    public override int GetHashCode() =>
        HashCode.Combine(HashCode.Combine(Bold, Italic, Underline, Strike, Superscript, Subscript), Size, Font, EastAsianFont, Color, Shade, Spacing);

    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Bold = {Bold}, Italic = {Italic}, Underline = {Underline}, Strike = {Strike}, Superscript = {Superscript}, ")
               .Append($"Subscript = {Subscript}, Size = {Size}, Font = {Font}, EastAsianFont = {EastAsianFont}, Color = {Color}, ")
               .Append($"Shade = {Shade}, Spacing = {Spacing}");
        return true;
    }
}
