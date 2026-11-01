// Filee's slide model: what a picture of each slide of a deck needs, resolved by the reader so the renderer draws
// without knowing the file format. PPTX fills it today (PptxSlideReader); the PPT, ODP / ODG and Visio readers are meant
// to fill the same model:
//
// - Geometry is resolved to outlines (SlidePath) in the element's own box, so every format's way of describing shapes
//   ends in the same place: DrawingML preset and custom geometry (ShapeGeometry), ODF draw:enhanced-geometry (the same
//   kind of formula and path language), Visio Geometry sections (MoveTo / LineTo / ArcTo / NURBS rows in shape units),
//   PPT's escher shape types (the same presets by number, its vertices as custom geometry).
// - Text is the document model's paragraphs (HParagraph with HText / HTab / HLink / HLineBreak), which the ODT and DOCX
//   readers already make, laid out by the layout engine. Bullets and numbers are resolved into the paragraph's text, so
//   a reader decides them once (PowerPoint's auto numbers, ODF list styles, Visio bullets).
// - Placeholders, masters, layouts and themes are inherited by the reader: an element holds its final fill, line and
//   text. Masters in ODP (style:master-page) and Visio (master shapes) resolve the same way.
// - Fills, lines and colours are final values (SKColor with alpha), pictures their encoded bytes.
//
// Lengths are points, y downwards, each element's box in its parent's coordinates (the slide, or a group's child space).

using Filee.Engines.Hwp.Hwpx;
using SkiaSharp;

namespace Filee.Engines.Slides;

/// <summary>A deck: slides of one size.</summary>
internal sealed class SlideDeck
{
    public required float Width { get; init; }
    public required float Height { get; init; }
    public string? Title { get; init; }
    public List<Slide> Slides { get; } = [];
}

/// <summary>One slide as it is shown: background, then the elements in drawing order (master and layout ones first).</summary>
internal sealed class Slide
{
    /// <summary>The fill behind everything; null = white.</summary>
    public SlideFill? Background { get; set; }

    public List<SlideElement> Elements { get; } = [];
}

/// <summary>Something drawn on a slide, in a box of its parent, turned and mirrored about the box's centre.</summary>
internal abstract class SlideElement
{
    /// <summary>The box in the parent's coordinates (points), before rotation.</summary>
    public SKRect Bounds { get; set; }

    /// <summary>Degrees clockwise about the centre of <see cref="Bounds"/>.</summary>
    public float Rotation { get; set; }

    public bool FlipHorizontal { get; set; }
    public bool FlipVertical { get; set; }
}

/// <summary>A shape: outlines with a fill and a line, and text. A picture is a shape filled with a picture.</summary>
internal sealed class SlideShape : SlideElement
{
    /// <summary>Outlines in the box's own coordinates (0, 0)–(width, height).</summary>
    public IReadOnlyList<SlidePath> Paths { get; set; } = [];

    public SlideFill? Fill { get; set; }
    public SlideLine? Line { get; set; }
    public SlideTextFrame? Text { get; set; }

    /// <summary>A picture fill keeps its place in the box when the shape turns (DrawingML rotWithShape) — always, for pictures.</summary>
    public bool IsPicture { get; set; }
}

/// <summary>How a path's inside is filled.</summary>
internal enum PathFill
{
    None,
    Normal,

    /// <summary>The fill lightened (3D-looking parts of presets such as can and cube).</summary>
    Lighten,
    LightenLess,
    Darken,
    DarkenLess,
}

/// <summary>One outline of a shape; open paths are only stroked.</summary>
internal sealed record SlidePath(SKPath Path, PathFill Fill = PathFill.Normal, bool Stroke = true);

/// <summary>Elements drawn in a child space mapped onto <see cref="SlideElement.Bounds"/>.</summary>
internal sealed class SlideGroup : SlideElement
{
    /// <summary>The child space that maps onto the group's box.</summary>
    public SKRect ChildBounds { get; set; }

    public List<SlideElement> Children { get; } = [];
}

/// <summary>A table: columns, rows of cells; cells covered by a merged cell are marked.</summary>
internal sealed class SlideTable : SlideElement
{
    public float[] Columns { get; set; } = [];
    public List<SlideTableRow> Rows { get; } = [];
}

internal sealed class SlideTableRow
{
    /// <summary>The height the row asks for; content taller than that makes it taller.</summary>
    public float Height { get; set; }

    public List<SlideTableCell> Cells { get; } = [];
}

internal sealed class SlideTableCell
{
    public int ColumnSpan { get; set; } = 1;
    public int RowSpan { get; set; } = 1;

    /// <summary>Covered by a cell spanning over it: not drawn.</summary>
    public bool Covered { get; set; }

    public SlideFill? Fill { get; set; }
    public SlideLine? Left { get; set; }
    public SlideLine? Right { get; set; }
    public SlideLine? Top { get; set; }
    public SlideLine? Bottom { get; set; }

    /// <summary>Lines from the top-left to the bottom-right corner and from the bottom-left to the top-right.</summary>
    public SlideLine? DiagonalDown { get; set; }
    public SlideLine? DiagonalUp { get; set; }

    public SlideTextFrame Text { get; set; } = new();
}

/// <summary>Where the text of a frame sits vertically.</summary>
internal enum TextAnchor
{
    Top,
    Middle,
    Bottom,
}

/// <summary>Which way a frame's lines run.</summary>
internal enum TextDirection
{
    Horizontal,

    /// <summary>Turned 90° clockwise: lines run down, the next one to the left (DrawingML vert).</summary>
    Down,

    /// <summary>Turned 270°: lines run up, the next one to the right (DrawingML vert270).</summary>
    Up,
}

/// <summary>The text of a shape or a table cell.</summary>
internal sealed class SlideTextFrame
{
    public List<HParagraph> Paragraphs { get; } = [];

    /// <summary>The geometry's text rectangle in the box's coordinates; null = the whole box.</summary>
    public SKRect? Area { get; set; }

    public float InsetLeft { get; set; } = 7.2f;
    public float InsetTop { get; set; } = 3.6f;
    public float InsetRight { get; set; } = 7.2f;
    public float InsetBottom { get; set; } = 3.6f;

    public TextAnchor Anchor { get; set; }

    /// <summary>The text block is centred across the frame (DrawingML anchorCtr).</summary>
    public bool AnchorCenter { get; set; }

    /// <summary>Lines wrap at the frame's width; off, each paragraph is one line however long.</summary>
    public bool Wrap { get; set; } = true;

    public TextDirection Direction { get; set; }

    public int Columns { get; set; } = 1;
    public float ColumnGap { get; set; }

    /// <summary>Extra degrees the text turns about the frame's centre (DrawingML bodyPr rot).</summary>
    public float Rotation { get; set; }

    public bool IsEmpty => Paragraphs.All(p => p.Inlines.Count == 0);
}

/// <summary>A fill: solid, gradient, picture or pattern.</summary>
internal abstract record SlideFill;

internal sealed record SolidFill(SKColor Color) : SlideFill;

/// <summary>A colour at a place along a gradient (0–1).</summary>
internal readonly record struct GradientStop(float Position, SKColor Color);

internal enum GradientShape
{
    /// <summary>Along a straight line at <see cref="GradientFill.Angle"/>.</summary>
    Linear,

    /// <summary>Outwards from the focus rectangle in circles.</summary>
    Circle,

    /// <summary>Outwards from the focus rectangle in rectangles (DrawingML path rect and shape).</summary>
    Rectangle,
}

/// <param name="Angle">Degrees clockwise from left-to-right (linear gradients).</param>
/// <param name="Scaled">The angle is that of the unit square, stretched with the box (DrawingML lin scaled).</param>
/// <param name="Focus">Where the colours start, as fractions of the box (left, top, right, bottom insets).</param>
/// <param name="RotateWithShape">The gradient turns with the shape.</param>
internal sealed record GradientFill(IReadOnlyList<GradientStop> Stops, GradientShape Shape, float Angle, bool Scaled, SKRect Focus,
    bool RotateWithShape = true) : SlideFill;

/// <summary>A picture's encoded bytes (PNG, JPEG, GIF, BMP, TIFF, EMF, WMF).</summary>
internal sealed class SlideImage(byte[] data)
{
    public byte[] Data { get; } = data;
}

/// <summary>Colour changes of a picture.</summary>
/// <param name="Alpha">Opacity, 1 = as it is.</param>
/// <param name="BiLevel">Black and white at this luminance threshold (0–1); null = off.</param>
/// <param name="Duotone">The picture's luminance mapped between two colours; null = off.</param>
/// <param name="Brightness">−1…1 added to each channel.</param>
/// <param name="Contrast">−1…1: 0 as it is.</param>
internal sealed record PictureEffects(float Alpha = 1, bool Grayscale = false, float? BiLevel = null, (SKColor Dark, SKColor Light)? Duotone = null,
    float Brightness = 0, float Contrast = 0)
{
    public static readonly PictureEffects None = new();

    public bool IsNone => this == None;
}

/// <summary>A picture filling a box.</summary>
/// <param name="Crop">The part of the picture used, as insets in fractions of its size (negative insets add room).</param>
/// <param name="Stretch">Where the used part goes, as insets in fractions of the box (DrawingML fillRect).</param>
/// <param name="Tile">Repeat the picture at its own size instead of stretching it; null = stretched.</param>
internal sealed record PictureFill(SlideImage Image, SKRect Crop, SKRect Stretch, PictureTile? Tile = null, PictureEffects? Effects = null) : SlideFill;

/// <summary>A tiled picture: offset and scale of the tiles, mirroring, and the corner they align to.</summary>
internal sealed record PictureTile(float OffsetX, float OffsetY, float ScaleX, float ScaleY, bool FlipX, bool FlipY, string Align);

/// <summary>A two-colour pattern (DrawingML prstPatt names: pct50, horz, diagCross, ...).</summary>
internal sealed record PatternFill(string Preset, SKColor Foreground, SKColor Background) : SlideFill;

/// <summary>An arrowhead or other mark at a line's end.</summary>
internal enum LineEndKind
{
    None,
    Triangle,
    Stealth,
    Diamond,
    Oval,
    Arrow,
}

/// <param name="Width">Width as a multiple of the line width (2, 3 or 5 in DrawingML).</param>
/// <param name="Length">Length as a multiple of the line width.</param>
internal readonly record struct LineEnd(LineEndKind Kind, float Width = 3, float Length = 3)
{
    public static readonly LineEnd None = new(LineEndKind.None);
}

internal enum LineCompound
{
    Single,
    Double,
    ThickThin,
    ThinThick,
    Triple,
}

/// <summary>A line (outline) of a shape or a table border.</summary>
/// <param name="Dash">Dash and gap lengths as multiples of the width; null = solid.</param>
internal sealed record SlideLine(SlideFill Fill, float Width, float[]? Dash = null, SKStrokeCap Cap = SKStrokeCap.Butt,
    SKStrokeJoin Join = SKStrokeJoin.Round, LineEnd Head = default, LineEnd Tail = default, LineCompound Compound = LineCompound.Single)
{
    /// <summary>The width a hairline (0) is drawn at.</summary>
    public const float Hairline = 0.5f;
}
