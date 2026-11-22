// What a PDF page paints, in the order it paints it, for editable slides (PdfDocumentReader.Slides). PdfPig's own page
// keeps letters, paths and pictures in lists of their own, without the clipping path, the transparency, the shading of
// a fill or the matrix a picture is drawn with. This page factory (PdfDocument.AddPageFactory) runs the page's content
// stream once more through PdfPig's stream processor and records, in paint order:
//
// - paths with their outline (slide coordinates: points from the page's top-left corner, y down), fill and stroke
//   (a colour with the constant alpha ca / CA, a shading, or the picture of a tiling pattern's cell), line width,
//   dashes, caps and joins as drawn under the transformation, and the blend mode (BM) they are painted with;
// - pictures with the matrix that maps their unit square onto the page, stencil masks (image masks) with the colour
//   they paint in, and for pictures PdfPig can't decode (JBIG2, JPEG 2000, JPEG behind other filters) how Filee's own decoders
//   read them (PdfOwnPicture);
// - shadings painted with "sh" over the clipping area;
// - where each glyph starts and when it is drawn, so text boxes take their place among the rest, and the glyphs as
//   painted (font, code, matrix, colour, advance: Letters, at most MaxLetters), drawn only as the backdrop of a blend;
//   a Type 3 glyph's procedure is drawn once for each colour it is shown in (in text space, at most MaxType3Glyphs a
//   page, the font's resources), so its glyphs are drawn as the PDF draws them, not from their text; the text a
//   procedure shows is among its paints (at most MaxType3Letters glyphs a page), procedures in it MaxDepth deep;
// - the clipping path everything is painted through (an intersection of the page's W / W* paths and the boxes of
//   form XObjects), and the soft mask: a group that paints one colour is a constant alpha, one that paints one shading
//   an alpha gradient (from its luminosity); any other group, or one with a transfer function (TR) or a backdrop (BC)
//   other than black, is drawn (its paths, shadings and pictures) into a picture of its alpha or luminosity over its
//   box (over the drawing's area when the mask isn't 0 outside the box), at most MaskSide pixels a side and
//   MaxMaskPixels a page.
//
// Function-based and mesh shadings are drawn into pictures when they are met (PdfShadingPictures), with the gradient
// they look like when they are close to one.
//
// Tiling patterns are drawn here, while the pattern's resources can be looked up: the content of one cell into a
// picture that the fill repeats. Text in a pattern cell, pictures in it and patterns more than MaxDepth deep are left
// out of it.
//
// Limits: at most PdfDocumentReader.MaxGraphicsSegments path segments and MaxPaints paints a page (the rest keep their
// place in the order but aren't recorded), MaxGlyphs glyph positions, clipping paths of more than MaxClipCommands
// segments clip by their box.

using System.Runtime.CompilerServices;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Geometry;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Graphics.Core;
using UglyToad.PdfPig.Graphics.Operations;
using UglyToad.PdfPig.Outline.Destinations;
using UglyToad.PdfPig.Parser;
using UglyToad.PdfPig.PdfFonts;
using UglyToad.PdfPig.Tokenization.Scanner;
using UglyToad.PdfPig.Tokens;
using UglyToad.PdfPig.XObjects;

namespace Filee.Engines.Pdf;

/// <summary>A clipping area in slide coordinates, shared by everything painted through it.</summary>
internal sealed class PdfClip(SKPath path, bool rectangular)
{
    public SKPath Path { get; } = path;

    /// <summary>The path is an upright rectangle: its <see cref="Bounds"/>.</summary>
    public bool Rectangular { get; } = rectangular;

    public SKRect Bounds { get; } = path.Bounds;
}

/// <summary>A shading (axial, radial, ...) and the matrix from its space to slide coordinates.</summary>
internal sealed record PdfShadingFill(Shading Shading, SKMatrix Space)
{
    /// <summary>A function-based or mesh shading drawn (PdfShadingPictures); null for axial and radial ones.</summary>
    public PdfShadingPicture? Picture { get; init; }

    /// <summary>The gradient a function-based or mesh shading looks like, when it is close to one.</summary>
    public PdfShadingLook? Look { get; init; }

    /// <summary>A colour that stands for the shading where one is needed.</summary>
    public SKColor Middle => Look?.At(0.5) ?? PdfShadings.ColorAt(Shading, 0.5);
}

/// <summary>The picture of one tiling pattern cell and the matrix from its pixels to slide coordinates; it repeats.</summary>
internal sealed record PdfTile(SKImage Image, SKMatrix Space);

/// <summary>
/// How a fill or stroke paints: <paramref name="Color"/> (its alpha the constant <paramref name="Alpha"/>), or a
/// shading or tile whose <paramref name="Color"/> stands for it where a single colour is needed.
/// </summary>
internal sealed record PdfPaintColor(SKColor Color, double Alpha, PdfShadingFill? Shading = null, PdfTile? Tile = null)
{
    public bool IsSolid => Shading is null && Tile is null;
}

/// <summary>
/// A soft mask: a constant <paramref name="Alpha"/>, an alpha gradient from a shading's luminosity, or the alpha of a
/// drawn group (<paramref name="Picture"/>: its alpha channel, nothing outside it), each times <paramref name="Alpha"/>.
/// </summary>
internal sealed record PdfMask(double Alpha, PdfShadingFill? Shading = null, PdfShadingPicture? Picture = null)
{
    /// <summary>The same alpha everywhere.</summary>
    public bool IsConstant => Shading is null && Picture is null;
}

/// <summary>Something painted, the <paramref name="Order"/>-th thing the page paints (glyphs counted too).</summary>
internal abstract record PdfPaint(int Order, PdfClip? Clip, PdfMask? Mask)
{
    /// <summary>How it is blended with what is under it (the graphics state's BM); SrcOver for Normal.</summary>
    public SKBlendMode Blend { get; init; } = SKBlendMode.SrcOver;

    /// <summary>
    /// The colour under a blended paint where nothing else on the slide is (the page's): drawn under it, so the blend
    /// has the backdrop the page gives it. Null: only what the same picture draws is under it.
    /// </summary>
    public SKColor? Backdrop { get; init; }

    /// <summary>
    /// What the page painted before a blended paint where it paints (pictures, text, graphics on other slide items):
    /// drawn over <see cref="Backdrop"/> under it, so the blend sees them as the PDF's does. Null: the page colour only.
    /// </summary>
    public IReadOnlyList<PdfPaint>? Below { get; init; }
}

/// <summary>A filled and / or stroked path; the line width and dashes are in slide points.</summary>
internal sealed record PdfPaintedPath(int Order, PdfClip? Clip, PdfMask? Mask, SKPath Outline, bool Curved, PdfPaintColor? Fill, PdfPaintColor? Stroke,
    float LineWidth, float[]? Dash, float DashPhase, SKStrokeCap Cap, SKStrokeJoin Join, float Miter) : PdfPaint(Order, Clip, Mask);

/// <summary>
/// A picture: <paramref name="Matrix"/> maps its unit square (y up, the picture's top edge at 1) onto the slide.
/// A stencil mask paints <paramref name="MaskColor"/> where its samples say.
/// </summary>
internal sealed record PdfPaintedImage(int Order, PdfClip? Clip, PdfMask? Mask, SKMatrix Matrix, IPdfImage? Image, SKColor MaskColor, double Alpha)
    : PdfPaint(Order, Clip, Mask)
{
    /// <summary>The picture as Filee's own decoders read it, where PdfPig can't (see <see cref="PdfOwnPicture"/>).</summary>
    public PdfOwnPicture? Own { get; init; }

    /// <summary>A stencil mask: it paints <see cref="MaskColor"/> where its samples say.</summary>
    public bool IsMask => Image?.IsImageMask ?? Own?.IsMask ?? false;
}

/// <summary>
/// A picture PdfPig doesn't decode, read by Filee's own decoders when it is first drawn (a backdrop): JBIG2 through
/// Jbig2Decoder with the stream's JBIG2Globals, JPEG 2000 through Jpeg2000Decoder (PdfJpxPictures), and JPEG data
/// behind other filters (Flate, ASCII85 ...).
/// </summary>
internal sealed class PdfOwnPicture(bool isMask, Func<SKColor, SKBitmap?> decode)
{
    /// <summary>A stencil mask (an image mask): drawn in the colour it is painted with.</summary>
    public bool IsMask { get; } = isMask;

    /// <summary>The pixels (the caller disposes them), a stencil mask in <paramref name="color"/>; null when they can't be read.</summary>
    public SKBitmap? Decode(SKColor color)
    {
        try
        {
            return decode(color);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return null; // damaged, a JBIG2 feature Filee doesn't read, or past its limits: left out
        }
    }
}

/// <summary>A shading painted with "sh" over the clipping area (or its own box).</summary>
internal sealed record PdfPaintedShading(int Order, PdfClip? Clip, PdfMask? Mask, PdfShadingFill Fill, double Alpha) : PdfPaint(Order, Clip, Mask);

/// <summary>
/// A glyph as it is painted: <paramref name="Matrix"/> maps the font's text space (an em of 1, y up) onto the slide.
/// Slides keep text as text boxes; these are drawn only as the backdrop of something blended over them.
/// </summary>
/// <param name="Code">The character code in <paramref name="Font"/>, whose outline is drawn when the font has one.</param>
/// <param name="Unicode">The character, drawn with an installed font when <paramref name="Font"/> has no outline.</param>
/// <param name="Stroked">Painted as an outline (text rendering modes 1 and 5) rather than filled.</param>
internal sealed record PdfPaintedGlyph(int Order, PdfClip? Clip, PdfMask? Mask, IFont Font, int Code, string Unicode, SKMatrix Matrix,
    PdfPaintColor Color, bool Stroked, float LineWidth) : PdfPaint(Order, Clip, Mask)
{
    /// <summary>The em box a glyph is taken to cover in text space, unless <see cref="Bounds"/> says more.</summary>
    internal static readonly SKRect EmBox = new(-0.25f, -0.4f, 1.5f, 1.2f);

    /// <summary>How far the glyph moves the text (text space, an em of 1); 0 when unknown.</summary>
    public float Advance { get; init; }

    /// <summary>What the glyph covers in text space when the font says (a Type 3 glyph's box); null: <see cref="EmBox"/>.</summary>
    public SKRect? Bounds { get; init; }

    /// <summary>A Type 3 glyph's procedure as drawn; null for other fonts.</summary>
    public PdfType3Glyph? Procedure { get; init; }

    /// <summary>What it covers on the slide, about: its box under the matrix.</summary>
    public SKRect Area => Matrix.MapRect(Bounds ?? EmBox);
}

/// <summary>
/// A Type 3 glyph's procedure (CharProc) drawn: its paints in text space (an em of 1, y up). <paramref name="Inked"/>:
/// a "d1" glyph, a shape only, every paint in the colour the text is painted with.
/// </summary>
internal sealed record PdfType3Glyph(IReadOnlyList<PdfPaint> Paints, bool Inked);

/// <summary>
/// Where a glyph starts (PdfPig's coordinates: y up, as its letters' StartBaseLine) and when it is drawn;
/// <paramref name="Turned"/>: its baseline doesn't run left to right.
/// </summary>
internal readonly record struct PdfGlyphOrder(double X, double Y, int Order, bool Turned = false);

/// <summary>
/// A glyph's text (painted or not) and where it starts and ends on its baseline, in slide coordinates, with its size in
/// points (<see cref="PdfSlideDrawingProcessor.RecordsText"/>: PDF → SVG's text layer).
/// </summary>
internal readonly record struct PdfGlyphText(string Unicode, SKPoint Start, SKPoint End, double Size);

/// <summary>What a page's drawing may still take, shared with the pattern cells and soft mask groups drawn for it.</summary>
internal sealed class PdfDrawingBudget
{
    public int Segments { get; set; }
    public int Paints { get; set; }
    public int Tiles { get; set; }
    public int Masks { get; set; }
    public int ClipIntersections { get; set; }

    /// <summary>Pixels of soft mask pictures drawn.</summary>
    public long MaskPixels { get; set; }

    /// <summary>Function-based and mesh shadings drawn, their triangles and function samples (PdfShadingPictures).</summary>
    public int ShadingPictures { get; set; }

    public int Triangles { get; set; }
    public long Samples { get; set; }

    /// <summary>Type 3 glyph procedures drawn (PdfSlideDrawingProcessor.MaxType3Glyphs), and the glyphs their text shows.</summary>
    public int Type3Glyphs { get; set; }

    public int Type3Letters { get; set; }
}

/// <summary>A page's paints in order; owns their outlines, clipping paths and tile pictures.</summary>
internal sealed class PdfSlideDrawing : IDisposable
{
    public List<PdfPaint> Paints { get; } = [];

    public List<PdfGlyphOrder> Glyphs { get; } = [];

    /// <summary>The glyphs as painted (at most <see cref="PdfSlideDrawingProcessor.MaxLetters"/>), for the backdrop of blends.</summary>
    public List<PdfPaintedGlyph> Letters { get; } = [];

    /// <summary>The page's glyphs as text, when the processor records it (at most <see cref="PdfSlideDrawingProcessor.MaxGlyphs"/>).</summary>
    public List<PdfGlyphText> Texts { get; } = [];

    /// <summary>Paths and pictures to dispose with the drawing.</summary>
    internal List<IDisposable> Owned { get; } = [];

    public void Dispose()
    {
        foreach (var owned in Owned)
            owned.Dispose();
        Owned.Clear();
    }
}

/// <summary>Makes a <see cref="PdfSlideDrawing"/> of a page (registered with PdfDocument.AddPageFactory).</summary>
internal sealed class PdfSlideDrawingFactory(IPdfTokenScanner pdfScanner, IResourceStore resourceStore, ILookupFilterProvider filterProvider,
    IPageContentParser pageContentParser, ParsingOptions parsingOptions)
    : BasePageFactory<PdfSlideDrawing>(pdfScanner, resourceStore, filterProvider, pageContentParser, parsingOptions)
{
    protected override PdfSlideDrawing ProcessPage(int pageNumber, DictionaryToken dictionary, NamedDestinations namedDestinations, MediaBox mediaBox,
        CropBox cropBox, UserSpaceUnit userSpaceUnit, PageRotationDegrees rotation, TransformationMatrix initialMatrix,
        IReadOnlyList<IGraphicsStateOperation> operations)
    {
        // PdfPig places the page as it is shown, from the bottom-left corner of its visible part: y flips at its height.
        var height = (float)cropBox.GetVisibleBounds(rotation).Height;
        var toSlide = new SKMatrix(1, 0, 0, 0, -1, height, 0, 0, 1);
        var width = (float)cropBox.GetVisibleBounds(rotation).Width;
        var processor = new PdfSlideDrawingProcessor(pageNumber, ResourceStore, PdfScanner, PageContentParser, FilterProvider, cropBox, userSpaceUnit,
            rotation, initialMatrix, ParsingOptions, toSlide, 0, new PdfDrawingBudget(), new SKRect(0, 0, width, height));
        return processor.Process(pageNumber, operations);
    }
}

/// <summary>PdfPig's stream processor recording paints instead of building its page model.</summary>
internal sealed class PdfSlideDrawingProcessor : BaseStreamProcessor<PdfSlideDrawing>
{
    internal const int MaxPaints = 200_000;
    internal const int MaxGlyphs = 500_000;

    /// <summary>Glyphs kept as painted (<see cref="PdfSlideDrawing.Letters"/>) on one page at most.</summary>
    internal const int MaxLetters = 100_000;
    internal const int MaxClipCommands = 20_000;

    /// <summary>Tiling pattern cells and soft mask groups inside each other at most.</summary>
    internal const int MaxDepth = 2;

    /// <summary>Soft mask groups looked into on one page at most.</summary>
    private const int MaxMasks = 256;

    /// <summary>Tiling pattern cells drawn for one page at most; fills with more patterns are left out.</summary>
    private const int MaxTiles = 64;

    /// <summary>Clipping paths other than rectangles intersected on one page at most; later ones clip by their box.</summary>
    private const int MaxClipIntersections = 10_000;

    /// <summary>Pixels per point of a soft mask's picture, its largest side, and the pixels of all of them on a page.</summary>
    private const float MaskScale = 2;

    private const int MaskSide = 1024;
    private const long MaxMaskPixels = 16_000_000;

    /// <summary>Pixels per point of a tiling pattern's cell picture, and its largest side.</summary>
    private const float TileScale = 3;

    private const int MaxTileSide = 1024;

    private readonly PdfSlideDrawing _drawing = new();
    private readonly SKMatrix _toSlide;
    private readonly int _depth;

    /// <summary>The drawing is a Type 3 glyph's procedure: the text it shows is drawn among its paints.</summary>
    private readonly bool _glyphProcedure;

    /// <summary>The page's glyphs are kept as text too (<see cref="PdfSlideDrawing.Texts"/>), invisible ones included.</summary>
    internal bool RecordsText { get; init; }
    private readonly PdfDrawingBudget _budget;
    private readonly CropBox _cropBox;
    private readonly UserSpaceUnit _userSpaceUnit;
    private readonly PageRotationDegrees _rotation;

    /// <summary>What the drawing covers in slide coordinates (the page, a pattern cell's picture).</summary>
    private readonly SKRect _area;

    private SKPath? _path;
    private int _pathSegments;
    private bool _curved;
    private PdfPoint _current;
    private PdfPoint _start;
    private FillingRule? _clipping;
    private PdfClip? _clip;
    private readonly Stack<PdfClip?> _clips = new();

    /// <summary>The space patterns are defined in: the page's default space, or that of the form being drawn.</summary>
    private TransformationMatrix _patternSpace;

    private readonly Stack<TransformationMatrix> _patternSpaces = new();
    private readonly Dictionary<StreamToken, PdfTile?> _tiles = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(SoftMask Mask, TransformationMatrix Space), PdfMask?> _masks = new(new MaskKeys());

    /// <summary>The transformation each soft mask was set under ("gs"): its group is drawn in that space.</summary>
    private readonly Dictionary<SoftMask, TransformationMatrix> _maskSpaces = new(ReferenceEqualityComparer.Instance);

    private sealed class MaskKeys : IEqualityComparer<(SoftMask Mask, TransformationMatrix Space)>
    {
        public bool Equals((SoftMask Mask, TransformationMatrix Space) x, (SoftMask Mask, TransformationMatrix Space) y) =>
            ReferenceEquals(x.Mask, y.Mask) && x.Space.Equals(y.Space);

        public int GetHashCode((SoftMask Mask, TransformationMatrix Space) key) => HashCode.Combine(RuntimeHelpers.GetHashCode(key.Mask), key.Space);
    }
    private readonly Dictionary<Shading, List<PdfShadingFill>> _fills = new(ReferenceEqualityComparer.Instance);
    private int _order;

    public PdfSlideDrawingProcessor(int pageNumber, IResourceStore resourceStore, IPdfTokenScanner pdfScanner, IPageContentParser pageContentParser,
        ILookupFilterProvider filterProvider, CropBox cropBox, UserSpaceUnit userSpaceUnit, PageRotationDegrees rotation,
        TransformationMatrix initialMatrix, ParsingOptions parsingOptions, SKMatrix toSlide, int depth, PdfDrawingBudget budget, SKRect area,
        bool glyphProcedure = false)
        : base(pageNumber, resourceStore, pdfScanner, pageContentParser, filterProvider, cropBox, userSpaceUnit, rotation, in initialMatrix, parsingOptions)
    {
        _glyphProcedure = glyphProcedure;
        _area = area;
        _toSlide = toSlide;
        _depth = depth;
        _budget = budget;
        _cropBox = cropBox;
        _userSpaceUnit = userSpaceUnit;
        _rotation = rotation;
        _patternSpace = initialMatrix;
    }

    public override PdfSlideDrawing Process(int pageNumberCurrent, IReadOnlyList<IGraphicsStateOperation> operations)
    {
        PageNumber = pageNumberCurrent;
        ProcessOperations(operations);
        _path?.Dispose();
        _path = null;
        return _drawing;
    }

    // ───────────────────────── State ─────────────────────────

    public override void PushState()
    {
        base.PushState();
        _clips.Push(_clip);
    }

    public override void PopState()
    {
        base.PopState();
        if (_clips.Count > 0)
            _clip = _clips.Pop();
    }

    public override void SetNamedGraphicsState(NameToken stateName)
    {
        var before = GetCurrentState().SoftMask;
        base.SetNamedGraphicsState(stateName);
        if (GetCurrentState().SoftMask is { } mask && (!ReferenceEquals(mask, before) || !_maskSpaces.ContainsKey(mask)))
            _maskSpaces[mask] = CurrentTransformationMatrix;
    }

    protected override void ProcessFormXObject(StreamToken formStream, NameToken xObjectName)
    {
        // Patterns used in a form are defined in the form's space: its matrix under the current one.
        var space = Matrix(formStream.StreamDictionary, NameToken.Matrix).Multiply(CurrentTransformationMatrix);
        _patternSpaces.Push(_patternSpace);
        _patternSpace = space;
        try
        {
            base.ProcessFormXObject(formStream, xObjectName);
        }
        finally
        {
            _patternSpace = _patternSpaces.Pop();
        }
    }

    private static TransformationMatrix Matrix(DictionaryToken dictionary, NameToken key)
    {
        if (!dictionary.TryGet(key, out var token) || token is not ArrayToken { Length: 6 } array)
            return TransformationMatrix.Identity;
        Span<double> values = stackalloc double[6];
        for (var i = 0; i < 6; i++)
        {
            if (array.Data[i] is not NumericToken number || !double.IsFinite(number.Double))
                return TransformationMatrix.Identity;
            values[i] = number.Double;
        }
        return TransformationMatrix.FromArray(values);
    }

    /// <summary>A PDF matrix (from user to device space, y up) followed by the step to slide coordinates.</summary>
    private SKMatrix ToSlide(TransformationMatrix m) =>
        new SKMatrix((float)m.A, (float)m.C, (float)m.E, (float)m.B, (float)m.D, (float)m.F, 0, 0, 1).PostConcat(_toSlide);

    private SKPoint Device(double x, double y)
    {
        var point = CurrentTransformationMatrix.Transform(new PdfPoint(x, y));
        _current = point;
        return _toSlide.MapPoint((float)point.X, (float)point.Y);
    }

    // ───────────────────────── Paths ─────────────────────────

    private bool Room() => _budget.Segments + _pathSegments < PdfDocumentReader.MaxGraphicsSegments;

    public override void BeginSubpath()
    {
    }

    public override PdfPoint? CloseSubpath()
    {
        if (_path is null)
            return null;
        _path.Close();
        _current = _start;
        return _start;
    }

    public override void MoveTo(double x, double y)
    {
        var point = Device(x, y);
        _start = _current;
        _path ??= new SKPath();
        if (!Room())
            return;
        _pathSegments++;
        _path.MoveTo(point);
    }

    public override void LineTo(double x, double y)
    {
        var from = _current;
        var point = Device(x, y);
        if (_path is null)
        {
            _path = new SKPath();
            _path.MoveTo(_toSlide.MapPoint((float)from.X, (float)from.Y));
        }
        if (!Room())
            return;
        _pathSegments++;
        _path.LineTo(point);
    }

    public override void BezierCurveTo(double x2, double y2, double x3, double y3)
    {
        // "v": the first control point is the current point.
        var first = _toSlide.MapPoint((float)_current.X, (float)_current.Y);
        Curve(first, Device(x2, y2), Device(x3, y3));
    }

    public override void BezierCurveTo(double x1, double y1, double x2, double y2, double x3, double y3) =>
        Curve(Device(x1, y1), Device(x2, y2), Device(x3, y3));

    private void Curve(SKPoint first, SKPoint second, SKPoint end)
    {
        if (_path is null || !Room())
            return;
        _pathSegments++;
        _curved = true;
        _path.CubicTo(first, second, end);
    }

    public override void Rectangle(double x, double y, double width, double height)
    {
        _path ??= new SKPath();
        var corners = new[] { Device(x, y), Device(x + width, y), Device(x + width, y + height), Device(x, y + height) };
        _current = CurrentTransformationMatrix.Transform(new PdfPoint(x, y));
        _start = _current;
        if (!Room())
            return;
        _pathSegments += 4;
        _path.MoveTo(corners[0]);
        _path.LineTo(corners[1]);
        _path.LineTo(corners[2]);
        _path.LineTo(corners[3]);
        _path.Close();
    }

    public override void ClosePath() => _path?.Close();

    public override void EndPath() => EndOfPath();

    public override void StrokePath(bool close) => Paint(false, true, FillingRule.NonZeroWinding, close);

    public override void FillPath(FillingRule fillingRule, bool close) => Paint(true, false, fillingRule, close);

    public override void FillStrokePath(FillingRule fillingRule, bool close) => Paint(true, true, fillingRule, close);

    private void Paint(bool fill, bool stroke, FillingRule rule, bool close)
    {
        if (close)
            _path?.Close();
        if (_path is { IsEmpty: false } path && _budget.Paints < MaxPaints)
        {
            var state = GetCurrentState();
            var outline = new SKPath(path) { FillType = rule == FillingRule.EvenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding };
            _drawing.Owned.Add(outline);
            _budget.Segments += _pathSegments;
            _budget.Paints++;
            var m = CurrentTransformationMatrix;
            var scale = Math.Sqrt(Math.Abs(m.A * m.D - m.B * m.C));
            scale = double.IsFinite(scale) ? Math.Clamp(scale, 1e-3, 1e3) : 1;
            float[]? dash = null;
            var phase = 0f;
            if (stroke && state.LineDashPattern is { } pattern && pattern.Array.Count > 0 && pattern.Array.Any(d => d > 0))
            {
                dash = [.. pattern.Array.Take(16).Select(d => (float)Math.Max(0.1, d * scale))];
                if (dash.Length % 2 == 1)
                    dash = [.. dash, .. dash]; // "[3] 0 d": 3 on, 3 off
                phase = (float)(pattern.Phase * scale);
            }
            _drawing.Paints.Add(new PdfPaintedPath(_order, _clip, Mask(state), outline, _curved,
                fill ? PaintOf(state.CurrentNonStrokingColor, state.AlphaConstantNonStroking) : null,
                stroke ? PaintOf(state.CurrentStrokingColor, state.AlphaConstantStroking) : null,
                (float)(state.LineWidth * scale), dash, phase,
                state.CapStyle switch { LineCapStyle.Round => SKStrokeCap.Round, LineCapStyle.ProjectingSquare => SKStrokeCap.Square, _ => SKStrokeCap.Butt },
                state.JoinStyle switch { LineJoinStyle.Round => SKStrokeJoin.Round, LineJoinStyle.Bevel => SKStrokeJoin.Bevel, _ => SKStrokeJoin.Miter },
                (float)Math.Clamp(state.MiterLimit, 1, 100))
            {
                Blend = Blend(state),
            });
        }
        _order++;
        EndOfPath();
    }

    /// <summary>Ends the current path, which first becomes part of the clipping path after "W" / "W*".</summary>
    private void EndOfPath()
    {
        if (_clipping is { } rule && _path is { } path)
            Intersect(path, rule == FillingRule.EvenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding, _pathSegments);
        _clipping = null;
        _path?.Dispose();
        _path = null;
        _pathSegments = 0;
        _curved = false;
    }

    public override void ModifyClippingIntersect(FillingRule clippingRule) => _clipping = clippingRule;

    protected override void ClipToRectangle(PdfRectangle rectangle, FillingRule clippingRule)
    {
        using var path = new SKPath();
        path.MoveTo(_toSlide.MapPoint((float)rectangle.TopLeft.X, (float)rectangle.TopLeft.Y));
        path.LineTo(_toSlide.MapPoint((float)rectangle.TopRight.X, (float)rectangle.TopRight.Y));
        path.LineTo(_toSlide.MapPoint((float)rectangle.BottomRight.X, (float)rectangle.BottomRight.Y));
        path.LineTo(_toSlide.MapPoint((float)rectangle.BottomLeft.X, (float)rectangle.BottomLeft.Y));
        path.Close();
        Intersect(path, SKPathFillType.Winding, 4);
    }

    private void Intersect(SKPath path, SKPathFillType fillType, int segments)
    {
        SKPath next;
        bool rectangular;
        if (segments > MaxClipCommands || path.IsEmpty)
        {
            next = new SKPath();
            next.AddRect(path.Bounds);
            rectangular = true;
        }
        else
        {
            next = new SKPath(path) { FillType = fillType };
            rectangular = path.IsRect;
        }
        if (_clip is { } clip)
        {
            if (clip.Rectangular && rectangular)
            {
                var box = clip.Bounds;
                box.Intersect(next.Bounds);
                next.Reset();
                next.AddRect(box);
            }
            else if (_budget.ClipIntersections++ < MaxClipIntersections && clip.Path.Op(next, SKPathOp.Intersect) is { } both)
            {
                next.Dispose();
                next = both;
                rectangular = false;
            }
            else
            {
                // Too many or too long: the box both share.
                var box = clip.Bounds;
                box.Intersect(next.Bounds);
                next.Reset();
                next.AddRect(box);
                rectangular = true;
            }
            if (next.PointCount > MaxClipCommands)
            {
                var box = next.Bounds;
                next.Reset();
                next.AddRect(box);
                rectangular = true;
            }
        }
        _drawing.Owned.Add(next);
        _clip = new PdfClip(next, rectangular);
    }

    // ───────────────────────── Colours, patterns, masks ─────────────────────────

    private PdfPaintColor? PaintOf(IColor? color, double alpha)
    {
        alpha = Math.Clamp(double.IsFinite(alpha) ? alpha : 1, 0, 1);
        var a = (byte)Math.Round(alpha * 255);
        switch (color)
        {
            case ShadingPatternColor shading:
                var fill = Fill(shading.Shading, PatternSpace(shading.Matrix));
                return new PdfPaintColor(fill.Middle.WithAlpha(a), alpha, fill);
            case TilingPatternColor tiling:
                return Tile(tiling) is { } tile ? new PdfPaintColor(SKColors.Gray.WithAlpha(a), alpha, Tile: tile) : null;
            case PatternColor:
                return null;
        }
        return new PdfPaintColor(Rgb(color).WithAlpha(a), alpha);
    }

    internal static SKColor Rgb(IColor? color)
    {
        if (color is null)
            return SKColors.Black;
        try
        {
            var (r, g, b) = color.ToRGBValues();
            return new SKColor(PdfShadings.Byte(r), PdfShadings.Byte(g), PdfShadings.Byte(b));
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException or IndexOutOfRangeException)
        {
            return SKColors.Black;
        }
    }

    /// <summary>
    /// A shading under <paramref name="space"/>; a function-based or mesh shading drawn into a picture of what it covers
    /// of the drawing's area (once for each matrix it is painted with).
    /// </summary>
    private PdfShadingFill Fill(Shading shading, SKMatrix space)
    {
        if (!_fills.TryGetValue(shading, out var fills))
            _fills[shading] = fills = [];
        if (fills.Find(f => f.Space == space) is { } known)
            return known;
        var fill = new PdfShadingFill(shading, space);
        if (fills.Count < 8)
        {
            var (picture, look) = PdfShadingPictures.Draw(shading, space, _area, _budget);
            if (picture is not null)
                _drawing.Owned.Add(picture.Image);
            fill = fill with { Picture = picture, Look = look };
            fills.Add(fill);
        }
        return fill;
    }

    /// <summary>The graphics state's blend mode as Skia's (separable and non-separable modes alike).</summary>
    private static SKBlendMode Blend(CurrentGraphicsState state) => state.BlendMode switch
    {
        BlendMode.Multiply => SKBlendMode.Multiply,
        BlendMode.Screen => SKBlendMode.Screen,
        BlendMode.Overlay => SKBlendMode.Overlay,
        BlendMode.Darken => SKBlendMode.Darken,
        BlendMode.Lighten => SKBlendMode.Lighten,
        BlendMode.ColorDodge => SKBlendMode.ColorDodge,
        BlendMode.ColorBurn => SKBlendMode.ColorBurn,
        BlendMode.HardLight => SKBlendMode.HardLight,
        BlendMode.SoftLight => SKBlendMode.SoftLight,
        BlendMode.Difference => SKBlendMode.Difference,
        BlendMode.Exclusion => SKBlendMode.Exclusion,
        BlendMode.Hue => SKBlendMode.Hue,
        BlendMode.Saturation => SKBlendMode.Saturation,
        BlendMode.Color => SKBlendMode.Color,
        BlendMode.Luminosity => SKBlendMode.Luminosity,
        _ => SKBlendMode.SrcOver,
    };

    /// <summary>A pattern's matrix (to the space patterns are defined in) followed by the step to slide coordinates.</summary>
    private SKMatrix PatternSpace(TransformationMatrix pattern) => ToSlide(pattern.Multiply(_patternSpace));

    /// <summary>The picture of a tiling pattern's cell (null when it can't be drawn) and where it repeats on the slide.</summary>
    private PdfTile? Tile(TilingPatternColor pattern)
    {
        if (_tiles.TryGetValue(pattern.PatternStream, out var cached))
            return cached;
        PdfTile? tile = null;
        var (stepX, stepY) = (Math.Abs(pattern.XStep), Math.Abs(pattern.YStep));
        var box = pattern.BBox;
        if (_depth < MaxDepth && _budget.Tiles < MaxTiles && double.IsFinite(stepX) && double.IsFinite(stepY) && stepX > 1e-3 && stepY > 1e-3)
        {
            var space = PatternSpace(pattern.Matrix);
            // Pixels per pattern unit: the cell as large as it is shown, TileScale pixels a point, at most MaxTileSide.
            var shown = Math.Max(Length(space.MapVector(1, 0)), Length(space.MapVector(0, 1)));
            var scale = (float)Math.Clamp(shown * TileScale, 1e-3, MaxTileSide / Math.Max(stepX, stepY));
            var (width, height) = ((int)Math.Max(1, Math.Ceiling(stepX * scale)), (int)Math.Max(1, Math.Ceiling(stepY * scale)));
            // Pattern space (y up) → cell pixels (y down), the cell's top-left corner at the box's left, a step above its bottom.
            var toPixels = new SKMatrix(scale, 0, (float)(-box.Left * scale), 0, -scale, (float)((box.Bottom + stepY) * scale), 0, 0, 1);
            if (Nested(pattern.PatternStream, pattern.Resources, TransformationMatrix.Identity, toPixels, new SKRect(0, 0, width, height)) is { } drawing)
            {
                using (drawing)
                {
                    using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
                    using (var canvas = new SKCanvas(bitmap))
                    {
                        canvas.Clear(SKColors.Transparent);
                        PdfSlideRaster.Draw(canvas, drawing.Paints, uncolored: pattern.PaintType == PatternPaintType.Uncoloured);
                    }
                    _budget.Tiles++;
                    var image = SKImage.FromBitmap(bitmap);
                    _drawing.Owned.Add(image);
                    if (toPixels.TryInvert(out var fromPixels))
                        tile = new PdfTile(image, fromPixels.PostConcat(space));
                }
            }
        }
        return _tiles[pattern.PatternStream] = tile;
    }

    private static double Length(SKPoint vector) => Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y);

    /// <summary>
    /// The paints of another content stream (a pattern cell, a soft mask group) with its own resources, drawn from
    /// <paramref name="initial"/> and mapped by <paramref name="toSlide"/>; null when it can't be read.
    /// </summary>
    private PdfSlideDrawing? Nested(StreamToken stream, DictionaryToken? resources, TransformationMatrix initial, SKMatrix toSlide, SKRect area) =>
        Operations(stream) is { } operations ? Nested(operations, resources, initial, toSlide, area) : null;

    /// <summary>A content stream's operations; null when it can't be read (a broken stream: what it fills is left out, not the page).</summary>
    private IReadOnlyList<IGraphicsStateOperation>? Operations(StreamToken stream)
    {
        try
        {
            return GetFormOperations(stream);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// The paints of <paramref name="operations"/> as <see cref="Nested(StreamToken, DictionaryToken?, TransformationMatrix, SKMatrix, SKRect)"/>
    /// draws them; <paramref name="setup"/> sets up the graphics state they start from.
    /// </summary>
    private PdfSlideDrawing Nested(IReadOnlyList<IGraphicsStateOperation> operations, DictionaryToken? resources, TransformationMatrix initial, SKMatrix toSlide,
        SKRect area, Action<CurrentGraphicsState>? setup = null, bool glyphProcedure = false)
    {
        var nested = new PdfSlideDrawingProcessor(PageNumber, ResourceStore, PdfScanner, PageContentParser, FilterProvider, _cropBox, _userSpaceUnit,
            _rotation, initial, ParsingOptions, toSlide, _depth + 1, _budget, area, glyphProcedure);
        setup?.Invoke(nested.GetCurrentState());
        if (resources is not null)
            ResourceStore.LoadResourceDictionary(resources);
        try
        {
            return nested.Process(PageNumber, operations);
        }
        finally
        {
            if (resources is not null)
                ResourceStore.UnloadResourceDictionary();
        }
    }

    /// <summary>
    /// The current soft mask: one colour or one shading painted, else the group drawn into a picture of its alpha (or
    /// luminosity); null when there is none or it can't be read or drawn (left out).
    /// </summary>
    private PdfMask? Mask(CurrentGraphicsState state)
    {
        if (state.SoftMask is not { TransparencyGroup: { } group } mask || _depth >= MaxDepth)
            return null;
        // The group is drawn in the space current when the mask was set ("gs"), not when something is painted with it.
        var space = _maskSpaces.TryGetValue(mask, out var set) ? set : CurrentTransformationMatrix;
        if (_masks.TryGetValue((mask, space), out var cached))
            return cached;
        if (_budget.Masks >= MaxMasks)
            return null;
        _budget.Masks++;
        PdfMask? result = null;
        var resources = group.StreamDictionary.TryGet(NameToken.Resources, PdfScanner, out DictionaryToken? found) ? found : null;
        var initial = Matrix(group.StreamDictionary, NameToken.Matrix).Multiply(space);
        if (Nested(group, resources, initial, _toSlide, _area) is { } drawing)
        {
            using (drawing)
            {
                var luminosity = mask.Subtype == SoftMaskType.Luminosity;
                var transfer = Transfer(mask);
                // A transfer function or a backdrop other than black: the mask is drawn, outside the group's box too.
                var plain = transfer is null && (!luminosity || Backdrop(mask.BC) == SKColors.Black);
                result = drawing.Paints switch
                {
                    [PdfPaintedShading { Fill: { Picture: null } fill, Clip: null, Mask: null } shading] when luminosity && plain => new PdfMask(shading.Alpha, fill),
                    [PdfPaintedPath { Fill: { Shading: { Picture: null } fill } paint, Stroke: null, Clip: null, Mask: null, Blend: SKBlendMode.SrcOver }]
                        when luminosity && plain => new PdfMask(paint.Alpha, fill),
                    [PdfPaintedPath { Fill: { IsSolid: true } paint, Stroke: null, Mask: null, Blend: SKBlendMode.SrcOver }] when plain =>
                        new PdfMask(paint.Alpha * (luminosity ? PdfShadings.Luminosity(paint.Color) : 1)),
                    [] when plain => null,
                    // Nothing painted: what the backdrop (or no alpha) gives, through the transfer function.
                    [] => new PdfMask(Transferred(transfer, luminosity ? PdfShadings.Luminosity(Backdrop(mask.BC)) : 0) / 255.0),
                    _ => Drawn(drawing, group, initial, luminosity, mask.BC, transfer),
                };
            }
        }
        return _masks[(mask, space)] = result;
    }

    /// <summary>
    /// A soft mask group's paints drawn over its box (on the drawing's area): a picture of their alpha, or of their
    /// luminosity over the backdrop colour BC (black by default), through the transfer function TR. Where the mask is
    /// not 0 outside the group's box (a light BC, a TR that lifts 0), the picture covers the drawing's area. Null past
    /// the mask budget.
    /// </summary>
    private PdfMask? Drawn(PdfSlideDrawing drawing, StreamToken group, TransformationMatrix initial, bool luminosity, double[]? backdrop, byte[]? transfer)
    {
        var box = _area;
        using var groupArea = new SKPath();
        if (group.StreamDictionary.TryGet(NameToken.Bbox, PdfScanner, out ArrayToken? bbox) && bbox.Length == 4
            && bbox.Data.All(t => t is NumericToken { Double: var v } && double.IsFinite(v)))
        {
            var values = bbox.Data.Select(t => (float)((NumericToken)t).Double).ToArray();
            var corners = new SKRect(Math.Min(values[0], values[2]), Math.Min(values[1], values[3]), Math.Max(values[0], values[2]), Math.Max(values[1], values[3]));
            var groupSpace = ToSlide(initial);
            groupArea.AddPoly([groupSpace.MapPoint(corners.Left, corners.Top), groupSpace.MapPoint(corners.Right, corners.Top),
                groupSpace.MapPoint(corners.Right, corners.Bottom), groupSpace.MapPoint(corners.Left, corners.Bottom)]);
            box = groupSpace.MapRect(corners);
            box.Intersect(_area);
        }
        var outside = Transferred(transfer, luminosity ? PdfShadings.Luminosity(Backdrop(backdrop)) : 0);
        if (outside > 0 && !groupArea.IsEmpty)
            box = _area;
        else
            groupArea.Reset();
        if (box.Width < 0.5f || box.Height < 0.5f)
            return new PdfMask(outside / 255.0); // a group of no area: what is outside it everywhere
        var scale = Math.Min(MaskScale, MaskSide / Math.Max(box.Width, box.Height));
        var (width, height) = ((int)Math.Ceiling(box.Width * scale), (int)Math.Ceiling(box.Height * scale));
        if (_budget.MaskPixels + (long)width * height > MaxMaskPixels)
            return null;
        _budget.MaskPixels += (long)width * height;
        var toPixels = SKMatrix.CreateTranslation(-box.Left, -box.Top).PostConcat(SKMatrix.CreateScale(scale, scale));
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(luminosity ? Backdrop(backdrop) : SKColors.Transparent);
            canvas.SetMatrix(toPixels);
            if (!groupArea.IsEmpty)
                canvas.ClipPath(groupArea, SKClipOperation.Intersect, true);
            PdfSlideRaster.Draw(canvas, drawing.Paints, pictures: true);
        }
        // The mask's alpha in the alpha channel, nothing else.
        using var alpha = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var source = bitmap.GetPixelSpan();
        var target = alpha.GetPixelSpan();
        for (var i = 0; i < width * height; i++)
        {
            var at = i * 4;
            var value = luminosity
                ? (byte)Math.Clamp(Math.Round(0.3 * source[at] + 0.59 * source[at + 1] + 0.11 * source[at + 2]), 0, 255)
                : source[at + 3];
            if (transfer is not null)
                value = transfer[value];
            (target[at], target[at + 1], target[at + 2], target[at + 3]) = (0, 0, 0, value);
        }
        if (!toPixels.TryInvert(out var toSlide))
            return null;
        var image = SKImage.FromBitmap(alpha);
        _drawing.Owned.Add(image);
        return new PdfMask(1, Picture: new PdfShadingPicture(image, toSlide));
    }

    /// <summary>
    /// A soft mask's transfer function TR as a table of 256 mask values; null for none, Identity, or one that can't be
    /// evaluated (taken as the identity).
    /// </summary>
    private static byte[]? Transfer(SoftMask mask)
    {
        if (mask.TransferFunction is not { } function)
            return null;
        var table = new byte[256];
        var identity = true;
        try
        {
            for (var i = 0; i < 256; i++)
            {
                var output = function.Eval([i / 255.0]);
                table[i] = output is [var value, ..] ? PdfShadings.Byte(value) : (byte)i;
                identity &= table[i] == i;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return null;
        }
        return identity ? null : table;
    }

    /// <summary>A mask value 0–1 through the transfer table, as 0–255.</summary>
    private static byte Transferred(byte[]? transfer, double value)
    {
        var level = PdfShadings.Byte(value);
        return transfer is null ? level : transfer[level];
    }

    /// <summary>A luminosity mask's backdrop colour (BC, grey, RGB or CMYK numbers), opaque; black without one.</summary>
    private static SKColor Backdrop(double[]? values) => values switch
    {
        [var g] => new SKColor(PdfShadings.Byte(g), PdfShadings.Byte(g), PdfShadings.Byte(g)),
        [var r, var g, var b] => new SKColor(PdfShadings.Byte(r), PdfShadings.Byte(g), PdfShadings.Byte(b)),
        [var c, var m, var y, var k] => new SKColor(PdfShadings.Byte((1 - c) * (1 - k)), PdfShadings.Byte((1 - m) * (1 - k)), PdfShadings.Byte((1 - y) * (1 - k))),
        _ => SKColors.Black,
    };

    // ───────────────────────── Pictures, shadings, glyphs ─────────────────────────

    protected override void RenderXObjectImage(XObjectContentRecord xObjectContentRecord)
    {
        IPdfImage? image = null;
        try
        {
            image = XObjectFactory.ReadImage(xObjectContentRecord, PdfScanner, FilterProvider, ResourceStore);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // An image PdfPig can't read: PdfDocumentReader's picture of it (cut from the rendered page) keeps its place
            // on the slide; a backdrop draws it with Filee's own decoders where they read it.
        }
        AddImage(image, xObjectContentRecord.AppliedTransformation, _budget.Paints < MaxPaints ? Own(xObjectContentRecord.Stream, image) : null);
    }

    protected override void RenderInlineImage(InlineImage inlineImage) => AddImage(inlineImage, CurrentTransformationMatrix);

    private void AddImage(IPdfImage? image, TransformationMatrix matrix, PdfOwnPicture? own = null)
    {
        if (_budget.Paints < MaxPaints)
        {
            _budget.Paints++;
            var state = GetCurrentState();
            var alpha = Math.Clamp(double.IsFinite(state.AlphaConstantNonStroking) ? state.AlphaConstantNonStroking : 1, 0, 1);
            _drawing.Paints.Add(new PdfPaintedImage(_order, _clip, Mask(state), ToSlide(matrix), image,
                Rgb(state.CurrentNonStrokingColor).WithAlpha((byte)Math.Round(alpha * 255)), alpha)
            {
                Blend = Blend(state),
                Own = own,
            });
        }
        _order++;
    }

    /// <summary>
    /// The picture as Filee's own decoders read it where PdfPig can't (see <see cref="PdfOwnPicture"/>): JBIG2, JPEG
    /// 2000, and JPEG behind other filters; null for any other picture.
    /// </summary>
    private PdfOwnPicture? Own(StreamToken stream, IPdfImage? image)
    {
        var dictionary = stream.StreamDictionary;
        var names = FilterNames(dictionary);
        if (names.Count == 0 || names[^1] is not ("JBIG2Decode" or "DCTDecode" or "DCT" or "JPXDecode"))
            return null;
        if (names[^1] == "JPXDecode")
            return Jpx(stream, image, names.Count);
        var jbig2 = names[^1] == "JBIG2Decode";
        if (!jbig2 && names.Count == 1 && image is not null)
            return null; // a JPEG file as it is: Skia reads it (PdfRasterCache.Read)
        var width = Integer(dictionary, NameToken.Width, NameToken.W);
        var height = Integer(dictionary, NameToken.Height, NameToken.H);
        if (width <= 0 || height <= 0 || (long)width * height > PdfDocumentReader.MaxGraphicsPixels)
            return null;
        var mask = image?.IsImageMask ?? (dictionary.TryGet(NameToken.ImageMask, PdfScanner, out BooleanToken? flag) && flag.Data);
        // Decode [1 0]: the samples' meaning swapped.
        var inverted = dictionary.TryGet(NameToken.Decode, PdfScanner, out ArrayToken? decode) && decode.Data is [NumericToken { Double: > 0.5 }, ..];
        var (scanner, filters) = (PdfScanner, FilterProvider);
        return new PdfOwnPicture(mask, color =>
        {
            var chain = filters.GetFilters(dictionary, scanner);
            if (chain.Count != names.Count)
                return null;
            var data = stream.Data;
            for (var i = 0; i < chain.Count - 1; i++)
            {
                if (!chain[i].IsSupported)
                    return null;
                data = chain[i].Decode(data, dictionary, filters, i);
            }
            if (!jbig2)
                return SKBitmap.Decode(data.Span);
            byte[]? globals = null;
            if (DecodeParameterResolver.GetFilterParameters(dictionary, chain.Count - 1) is { } parameters
                && parameters.TryGet(NameToken.Create("JBIG2Globals"), scanner, out StreamToken? shared))
                globals = shared.Decode(filters, scanner).ToArray();
            var page = Djvu.Jbig2Decoder.Decode(data.ToArray(), globals, new Djvu.Jbig2Limits { MaxBitmapPixels = (long)PdfDocumentReader.MaxGraphicsPixels });
            return Jbig2Bitmap(page, width, height, mask, inverted, color);
        });
    }

    /// <summary>
    /// A JPEG 2000 picture (/JPXDecode last, after any other filters) through Filee's own decoder (PdfJpxPictures),
    /// in the image dictionary's colour space when it has one.
    /// </summary>
    private PdfOwnPicture Jpx(StreamToken stream, IPdfImage? image, int filters)
    {
        var dictionary = stream.StreamDictionary;
        var mask = image?.IsImageMask ?? (dictionary.TryGet(NameToken.ImageMask, PdfScanner, out BooleanToken? flag) && flag.Data);
        var alphaInData = dictionary.TryGet(NameToken.Create("SMaskInData"), PdfScanner, out NumericToken? inData) && inData.Int != 0;
        var space = dictionary.ContainsKey(NameToken.ColorSpace) || dictionary.ContainsKey(NameToken.Create("CS")) ? image?.ColorSpaceDetails : null;
        var (scanner, provider) = (PdfScanner, FilterProvider);
        return new PdfOwnPicture(mask, color =>
        {
            var chain = provider.GetFilters(dictionary, scanner);
            if (chain.Count != filters)
                return null;
            var data = stream.Data;
            for (var i = 0; i < chain.Count - 1; i++)
            {
                if (!chain[i].IsSupported)
                    return null;
                data = chain[i].Decode(data, dictionary, provider, i);
            }
            var picture = PdfJpxPictures.Decode(data.ToArray());
            var rgba = PdfJpxPictures.Rgba(picture, mask ? null : space, alphaInData);
            if (mask)
            {
                // A JPEG 2000 image mask (rare): dark samples paint.
                for (var i = 0; i < rgba.Length; i += 4)
                    (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (color.Red, color.Green, color.Blue, rgba[i] < 128 ? color.Alpha : (byte)0);
            }
            return PdfJpxPictures.Bitmap(rgba, picture.Width, picture.Height);
        });
    }

    /// <summary>The names of a stream's filters, in the order they decode it.</summary>
    private List<string> FilterNames(DictionaryToken dictionary)
    {
        if (!dictionary.TryGet(NameToken.Filter, PdfScanner, out IToken? filter) && !dictionary.TryGet(NameToken.F, PdfScanner, out filter))
            return [];
        return filter switch
        {
            NameToken name => [name.Data],
            ArrayToken array => [.. array.Data.Select(t => t is IndirectReferenceToken r ? PdfScanner.Get(r.Data)?.Data : t).OfType<NameToken>().Select(n => n.Data)],
            _ => [],
        };
    }

    private int Integer(DictionaryToken dictionary, NameToken key, NameToken shortKey) =>
        dictionary.TryGet(key, PdfScanner, out NumericToken? number) || dictionary.TryGet(shortKey, PdfScanner, out number) ? number.Int : 0;

    /// <summary>
    /// A decoded JBIG2 page (a byte a pixel, 1 for black) as the picture's pixels, cut or padded to its size: a stencil
    /// mask in <paramref name="color"/> where it paints (black pixels, or white ones under Decode [1 0]); a grey picture
    /// black and white.
    /// </summary>
    internal static SKBitmap Jbig2Bitmap(Djvu.Jbig2Page page, int width, int height, bool mask, bool inverted, SKColor color)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var pixels = bitmap.GetPixelSpan();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // The filter gives 0 for black; a sample of 0 paints a mask and is black in a grey picture.
                var black = x < page.Width && y < page.Height && page.Pixels[(long)y * page.Width + x] == 1;
                var dark = black != inverted;
                var at = (y * width + x) * 4;
                if (mask)
                    (pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]) = (color.Red, color.Green, color.Blue, dark ? color.Alpha : (byte)0);
                else
                    (pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]) = dark ? ((byte)0, (byte)0, (byte)0, (byte)255) : ((byte)255, (byte)255, (byte)255, (byte)255);
            }
        }
        return bitmap;
    }

    public override void PaintShading(NameToken shadingName)
    {
        Shading? shading = null;
        try
        {
            shading = ResourceStore.GetShading(shadingName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // A shading PdfPig can't read is left out.
        }
        if (shading is not null && _budget.Paints < MaxPaints)
        {
            _budget.Paints++;
            var state = GetCurrentState();
            var alpha = Math.Clamp(double.IsFinite(state.AlphaConstantNonStroking) ? state.AlphaConstantNonStroking : 1, 0, 1);
            _drawing.Paints.Add(new PdfPaintedShading(_order, _clip, Mask(state), Fill(shading, ToSlide(CurrentTransformationMatrix)), alpha) { Blend = Blend(state) });
        }
        _order++;
    }

    public override void RenderGlyph(IFont font, CurrentGraphicsState currentState, double fontSize, double pointSize, int code, string unicode,
        long currentOffset, in TransformationMatrix renderingMatrix, in TransformationMatrix textMatrix, in TransformationMatrix transformationMatrix,
        CharacterBoundingBox characterBoundingBox)
    {
        if (_depth == 0 && _drawing.Glyphs.Count < MaxGlyphs)
        {
            var origin = transformationMatrix.Transform(textMatrix.Transform(renderingMatrix.Transform(new PdfPoint(0, 0))));
            var along = transformationMatrix.Transform(textMatrix.Transform(renderingMatrix.Transform(new PdfPoint(1, 0))));
            var (dx, dy) = (along.X - origin.X, along.Y - origin.Y);
            _drawing.Glyphs.Add(new PdfGlyphOrder(origin.X, origin.Y, _order, dx <= 0 || Math.Abs(dy) > 0.02 * dx));
            if (RecordsText)
            {
                // Invisible text too (an OCR layer): where it starts and ends on its baseline, as PdfPig's letters.
                var width = double.IsFinite(characterBoundingBox.Width) ? characterBoundingBox.Width : 0;
                var end = transformationMatrix.Transform(textMatrix.Transform(renderingMatrix.Transform(new PdfPoint(width, 0))));
                _drawing.Texts.Add(new PdfGlyphText(unicode, _toSlide.MapPoint((float)origin.X, (float)origin.Y), _toSlide.MapPoint((float)end.X, (float)end.Y),
                    pointSize));
            }
            if (_drawing.Letters.Count < MaxLetters
                && Letter(font, currentState, code, unicode, renderingMatrix, textMatrix, transformationMatrix, characterBoundingBox) is { } letter)
                _drawing.Letters.Add(letter);
        }
        else if (_glyphProcedure && _budget.Type3Letters < MaxType3Letters && _budget.Paints < MaxPaints
                 && Letter(font, currentState, code, unicode, renderingMatrix, textMatrix, transformationMatrix, characterBoundingBox) is { } letter)
        {
            // Text a Type 3 glyph's procedure shows: one of its paints, in the glyph's text space.
            _budget.Type3Letters++;
            _budget.Paints++;
            _drawing.Paints.Add(letter);
        }
        _order++;
    }

    /// <summary>A glyph as painted, or null when it paints nothing (invisible text, as OCR layers have).</summary>
    private PdfPaintedGlyph? Letter(IFont font, CurrentGraphicsState state, int code, string unicode, in TransformationMatrix rendering,
        in TransformationMatrix text, in TransformationMatrix transformation, CharacterBoundingBox box)
    {
        var mode = state.FontState.TextRenderingMode;
        if (mode is TextRenderingMode.Neither or TextRenderingMode.NeitherClip)
            return null;
        var stroked = mode is TextRenderingMode.Stroke or TextRenderingMode.StrokeClip;
        var color = stroked ? PaintOf(state.CurrentStrokingColor, state.AlphaConstantStroking) : PaintOf(state.CurrentNonStrokingColor, state.AlphaConstantNonStroking);
        if (color is null)
            return null;
        var matrix = ToSlide(rendering.Multiply(text).Multiply(transformation));
        var m = transformation;
        var scale = Math.Sqrt(Math.Abs(m.A * m.D - m.B * m.C));
        var width = (float)(state.LineWidth * (double.IsFinite(scale) ? Math.Clamp(scale, 1e-3, 1e3) : 1));
        var advance = box.Width is var w && double.IsFinite(w) && Math.Abs(w) < 100 ? (float)w : 0;
        var procedure = font is IType3Font type3 ? Procedure(type3, code, state) : null;
        SKRect? bounds = null;
        if (procedure is not null && box.GlyphBounds is var b && PdfShadings.Finite(b.Left, b.Bottom, b.Right, b.Top) && b.Width > 0 && b.Height > 0)
        {
            // A Type 3 glyph may reach past the em box (its d1 box, in text space).
            var glyphBox = SKRect.Union(PdfPaintedGlyph.EmBox, new SKRect((float)b.Left, (float)b.Bottom, (float)b.Right, (float)b.Top));
            bounds = SKRect.Intersect(glyphBox, new SKRect(-10, -10, 10, 10));
        }
        return new PdfPaintedGlyph(_order, _clip, Mask(state), font, code, unicode, matrix, color, stroked, width)
        {
            Blend = Blend(state),
            Advance = advance,
            Bounds = bounds,
            Procedure = procedure,
        };
    }

    /// <summary>Type 3 glyph procedures drawn on one page at most (each once for each colour it is shown in), procedures in procedures included.</summary>
    internal const int MaxType3Glyphs = 1024;

    /// <summary>Glyphs the text of Type 3 glyph procedures shows on one page at most; past that their text is left out.</summary>
    internal const int MaxType3Letters = 20_000;

    private readonly Dictionary<(IFont Font, int Code, SKColor Fill, SKColor Stroke), PdfType3Glyph?> _type3 = new(new Type3Keys());

    private sealed class Type3Keys : IEqualityComparer<(IFont Font, int Code, SKColor Fill, SKColor Stroke)>
    {
        public bool Equals((IFont Font, int Code, SKColor Fill, SKColor Stroke) x, (IFont Font, int Code, SKColor Fill, SKColor Stroke) y) =>
            ReferenceEquals(x.Font, y.Font) && x.Code == y.Code && x.Fill == y.Fill && x.Stroke == y.Stroke;

        public int GetHashCode((IFont Font, int Code, SKColor Fill, SKColor Stroke) key) =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(key.Font), key.Code, key.Fill, key.Stroke);
    }

    /// <summary>
    /// A Type 3 glyph's procedure (CharProc) drawn in text space (an em of 1, y up: the font matrix maps its glyph
    /// space there), with the font's resources; null when it has none or can't be read, past <see cref="MaxType3Glyphs"/>,
    /// or when it paints nothing. A "d0" glyph starts from the text's colours, a "d1" one is inked with them when drawn.
    /// </summary>
    private PdfType3Glyph? Procedure(IType3Font font, int code, CurrentGraphicsState state)
    {
        var (fill, stroke) = (state.CurrentNonStrokingColor, state.CurrentStrokingColor);
        var key = ((IFont)font, code, Rgb(fill), Rgb(stroke));
        if (_type3.TryGetValue(key, out var known))
            return known;
        PdfType3Glyph? glyph = null;
        StreamToken? charProc = null;
        try
        {
            if (!font.TryGetCharProc(code, out charProc))
                charProc = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // A broken font: drawn from its Unicode with an installed font instead.
        }
        // Procedures inside procedures go MaxDepth deep; deeper glyphs are drawn from their text.
        if (_budget.Type3Glyphs < MaxType3Glyphs && _depth < MaxDepth && charProc is not null && Operations(charProc) is { } operations)
        {
            _budget.Type3Glyphs++;
            var inked = operations.OfType<UglyToad.PdfPig.Graphics.Operations.TextState.Type3SetGlyphWidthAndBoundingBox>().Any();
            var drawing = Nested(operations, font.Type3Resources, font.GetFontMatrix(), SKMatrix.Identity, new SKRect(-2, -2, 3, 3), start =>
            {
                start.CurrentNonStrokingColor = fill;
                start.CurrentStrokingColor = stroke;
            }, glyphProcedure: true);
            _drawing.Owned.Add(drawing);
            if (drawing.Paints.Count > 0)
                glyph = new PdfType3Glyph(drawing.Paints, inked);
        }
        return _type3[key] = glyph;
    }

    public override void BeginMarkedContent(NameToken name, NameToken? propertyDictionaryName, DictionaryToken? properties)
    {
    }

    public override void EndMarkedContent()
    {
    }
}

/// <summary>Colours of axial and radial shadings, for gradient stops and Skia shaders.</summary>
internal static class PdfShadings
{
    /// <summary>Samples of a shading's colour for a Skia gradient.</summary>
    private const int ShaderSamples = 32;

    internal static byte Byte(double value) => (byte)Math.Round(Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1) * 255);

    /// <summary>The share of white in a colour (a luminosity soft mask's alpha).</summary>
    internal static double Luminosity(SKColor color) => (0.3 * color.Red + 0.59 * color.Green + 0.11 * color.Blue) / 255;

    /// <summary>The domain [t0, t1] of an axial or radial shading; [0, 1] for others.</summary>
    private static (double Start, double End) Domain(Shading shading)
    {
        var domain = shading switch
        {
            AxialShading axial => axial.Domain,
            RadialShading radial => radial.Domain,
            _ => null,
        };
        return domain is [var start, var end, ..] && double.IsFinite(start) && double.IsFinite(end) ? (start, end) : (0, 1);
    }

    /// <summary>Whether an axial or radial shading goes on beyond its start and its end.</summary>
    internal static (bool Start, bool End) Extend(Shading shading)
    {
        var extend = shading switch
        {
            AxialShading axial => axial.Extend,
            RadialShading radial => radial.Extend,
            _ => null,
        };
        return extend is [var start, var end, ..] ? (start, end) : (false, false);
    }

    /// <summary>The shading's colour at <paramref name="t"/> (0 the start of its domain, 1 the end); grey when it can't be told.</summary>
    internal static SKColor ColorAt(Shading shading, double t)
    {
        var (start, end) = Domain(shading);
        try
        {
            var components = shading.Eval([start + Math.Clamp(t, 0, 1) * (end - start)]);
            shading.ColorSpace.GetRgb(components, out var r, out var g, out var b);
            return new SKColor(Byte(r), Byte(g), Byte(b));
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidOperationException or NotSupportedException
                                       or InvalidCastException or NullReferenceException)
        {
            return SKColors.Gray;
        }
    }

    /// <summary>
    /// A Skia shader painting <paramref name="fill"/> (axial and radial shadings; null for others), each colour through
    /// <paramref name="color"/> when given (a luminosity mask's alpha).
    /// </summary>
    internal static SKShader? Shader(PdfShadingFill fill, Func<SKColor, SKColor>? color = null)
    {
        // A function-based or mesh shading: its picture (a mask's luminosity is taken from it by ApplyMask).
        if (fill.Picture is { } picture)
            return color is null ? picture.Shader() : null;
        var shading = fill.Shading;
        var (extendStart, extendEnd) = Extend(shading);
        var mode = extendStart && extendEnd ? SKShaderTileMode.Clamp : extendStart || extendEnd ? SKShaderTileMode.Clamp : SKShaderTileMode.Decal;
        var colors = new SKColor[ShaderSamples];
        var positions = new float[ShaderSamples];
        for (var i = 0; i < ShaderSamples; i++)
        {
            positions[i] = i / (float)(ShaderSamples - 1);
            var sample = ColorAt(shading, positions[i]);
            colors[i] = color?.Invoke(sample) ?? sample;
        }
        switch (shading)
        {
            case AxialShading { Coords: [var x0, var y0, var x1, var y1, ..] } when Finite(x0, y0, x1, y1) && (x0 != x1 || y0 != y1):
                return SKShader.CreateLinearGradient(new SKPoint((float)x0, (float)y0), new SKPoint((float)x1, (float)y1), colors, positions, mode, fill.Space);
            case RadialShading { Coords: [var x0, var y0, var r0, var x1, var y1, var r1, ..] } when Finite(x0, y0, r0, x1, y1, r1) && r0 >= 0 && r1 >= 0 && r0 + r1 > 0:
                return SKShader.CreateTwoPointConicalGradient(new SKPoint((float)x0, (float)y0), (float)r0, new SKPoint((float)x1, (float)y1), (float)r1,
                    colors, positions, mode, fill.Space);
            default:
                return null;
        }
    }

    internal static bool Finite(params ReadOnlySpan<double> values)
    {
        foreach (var value in values)
        {
            if (!double.IsFinite(value) || Math.Abs(value) > 1e7)
                return false;
        }
        return true;
    }
}

/// <summary>Draws recorded paths and shadings with Skia: the pictures of a page's graphics and of pattern cells.</summary>
internal static class PdfSlideRaster
{
    /// <summary>
    /// Draws <paramref name="paints"/> through their clipping paths, alpha, soft masks and blend modes; pictures only
    /// when <paramref name="pictures"/> (a soft mask group: on slides they are pictures of their own).
    /// <paramref name="uncolored"/>: a pattern cell that gets its colour from the fill (black here).
    /// <paramref name="cache"/> keeps decoded pictures and glyph outlines for the page; <paramref name="backdrops"/>
    /// false leaves out the backdrops of blended paints (they are being drawn as one). <paramref name="ink"/>: everything
    /// painted in that colour (a "d1" Type 3 glyph's procedure).
    /// </summary>
    public static void Draw(SKCanvas canvas, IEnumerable<PdfPaint> paints, bool uncolored = false, CancellationToken cancellationToken = default,
        bool pictures = false, PdfRasterCache? cache = null, bool backdrops = true, PdfPaintColor? ink = null)
    {
        // What this canvas has drawn so far: a backdrop leaves it out, since it is on the canvas already.
        HashSet<PdfPaint>? drawn = null;
        foreach (var paint in paints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (paint is PdfPaintedImage && !pictures)
                continue;
            canvas.Save();
            if (paint.Clip is { } clip)
                canvas.ClipPath(clip.Path, SKClipOperation.Intersect, true);
            using var blend = paint.Blend == SKBlendMode.SrcOver ? null : new SKPaint { BlendMode = paint.Blend };
            if (blend is not null && backdrops && paint.Backdrop is { } backdrop)
            {
                if (paint.Below is { } below)
                    Behind(canvas, paint, backdrop, below, drawn, cache, cancellationToken);
                else
                    Underlay(canvas, paint, backdrop);
            }
            var masked = paint.Mask is { } mask && (!mask.IsConstant || mask.Alpha < 1);
            // Layers only as large as what the paint covers: a page of small blended or masked paints would otherwise
            // clear and blend a layer of the whole canvas for each.
            SKRect? layer = null;
            if (blend is not null || masked)
            {
                using var coverage = Coverage(canvas, paint);
                if (coverage is not null)
                    layer = SKRect.Inflate(coverage.Bounds, 2 / Math.Max(1e-3f, canvas.TotalMatrix.ScaleX), 2 / Math.Max(1e-3f, canvas.TotalMatrix.ScaleX));
            }
            if (blend is not null)
            {
                if (layer is { } bounds)
                    canvas.SaveLayer(bounds, blend);
                else
                    canvas.SaveLayer(blend);
            }
            if (masked)
            {
                if (layer is { } bounds)
                    canvas.SaveLayer(bounds, null);
                else
                    canvas.SaveLayer();
            }
            switch (paint)
            {
                case PdfPaintedImage image:
                    Picture(canvas, ink is not null && image.IsMask ? image with { MaskColor = ink.Color } : image, cache);
                    break;
                case PdfPaintedGlyph glyph:
                    // Text inside a "d1" Type 3 glyph is inked too.
                    Glyph(canvas, ink is not null ? glyph with { Color = ink } : glyph, uncolored, cache, cancellationToken);
                    break;
                case PdfPaintedPath path:
                    if (path.Fill is { } fill)
                    {
                        using var fillPaint = Paint(ink ?? fill, uncolored);
                        fillPaint.Style = SKPaintStyle.Fill;
                        canvas.DrawPath(path.Outline, fillPaint);
                    }
                    if (path.Stroke is { } stroke)
                    {
                        using var strokePaint = Paint(ink ?? stroke, uncolored);
                        strokePaint.Style = SKPaintStyle.Stroke;
                        // A width of 0 is the thinnest line the device can show: about one pixel.
                        strokePaint.StrokeWidth = Math.Max(path.LineWidth, 1 / Math.Max(1, canvas.TotalMatrix.ScaleX));
                        strokePaint.StrokeCap = path.Cap;
                        strokePaint.StrokeJoin = path.Join;
                        strokePaint.StrokeMiter = path.Miter;
                        using var dash = path.Dash is { } intervals ? SKPathEffect.CreateDash(intervals, path.DashPhase) : null;
                        strokePaint.PathEffect = dash;
                        canvas.DrawPath(path.Outline, strokePaint);
                    }
                    break;
                case PdfPaintedShading shading:
                    using (var shader = PdfShadings.Shader(shading.Fill))
                    {
                        if (shader is null)
                            break;
                        using var shadingPaint = new SKPaint { IsAntialias = true, Shader = shader, Color = SKColors.White.WithAlpha((byte)Math.Round(shading.Alpha * 255)) };
                        if (shading.Fill.Shading.BBox is { } box && paint.Clip is null)
                        {
                            using var area = new SKPath();
                            area.AddPoly([.. new[] { box.BottomLeft, box.BottomRight, box.TopRight, box.TopLeft }.Select(p => new SKPoint((float)p.X, (float)p.Y))]);
                            area.Transform(shading.Fill.Space);
                            canvas.DrawPath(area, shadingPaint);
                        }
                        else
                        {
                            canvas.DrawPaint(shadingPaint);
                        }
                    }
                    break;
            }
            if (masked)
            {
                ApplyMask(canvas, paint.Mask!);
                canvas.Restore();
            }
            if (blend is not null)
                canvas.Restore();
            canvas.Restore();
            if (backdrops)
                (drawn ??= new HashSet<PdfPaint>(ReferenceEqualityComparer.Instance)).Add(paint);
        }
    }

    /// <summary>
    /// The page colour and what the page painted before <paramref name="paint"/> (<paramref name="below"/>, less what
    /// this canvas has <paramref name="drawn"/>), under what is drawn there already (DstOver), where the paint paints.
    /// </summary>
    private static void Behind(SKCanvas canvas, PdfPaint paint, SKColor page, IReadOnlyList<PdfPaint> below, HashSet<PdfPaint>? drawn,
        PdfRasterCache? cache, CancellationToken cancellationToken)
    {
        using var painted = Coverage(canvas, paint);
        // A pixel more all round, clipped sharply: the paint's soft edge then lies over its backdrop too, not over
        // nothing (which left a faint outline over the items under it).
        using var coverage = painted is null ? null : Grown(painted, 1.5f / Math.Max(1e-3f, canvas.TotalMatrix.ScaleX));
        canvas.Save();
        if (coverage is not null)
            canvas.ClipPath(coverage, SKClipOperation.Intersect, false);
        using var under = new SKPaint { BlendMode = SKBlendMode.DstOver };
        if (coverage is not null)
            canvas.SaveLayer(coverage.Bounds, under);
        else
            canvas.SaveLayer(under);
        canvas.DrawColor(page, SKBlendMode.Src);
        Draw(canvas, drawn is null ? below : below.Where(p => !drawn.Contains(p)), cancellationToken: cancellationToken, pictures: true, cache: cache,
            backdrops: false);
        canvas.Restore();
        canvas.Restore();
    }

    /// <summary><paramref name="path"/> grown by <paramref name="by"/> all round.</summary>
    private static SKPath Grown(SKPath path, float by)
    {
        using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 2 * by, StrokeJoin = SKStrokeJoin.Round };
        using var edge = stroke.GetFillPath(path);
        return edge is not null && path.Op(edge, SKPathOp.Union) is { } grown ? grown : new SKPath(path);
    }

    /// <summary>Where <paramref name="paint"/> paints (before its clipping path), or null for all of the canvas.</summary>
    private static SKPath? Coverage(SKCanvas canvas, PdfPaint paint)
    {
        switch (paint)
        {
            case PdfPaintedPath path:
                {
                    var area = new SKPath();
                    if (path.Fill is not null)
                        area.AddPath(path.Outline);
                    if (path.Stroke is not null)
                    {
                        using var stroke = new SKPaint
                        {
                            Style = SKPaintStyle.Stroke,
                            StrokeWidth = Math.Max(path.LineWidth, 1 / Math.Max(1, canvas.TotalMatrix.ScaleX)),
                            StrokeCap = path.Cap,
                            StrokeJoin = path.Join,
                            StrokeMiter = path.Miter,
                        };
                        using var outline = stroke.GetFillPath(path.Outline);
                        if (outline is not null)
                            area.AddPath(outline);
                    }
                    return area;
                }
            case PdfPaintedImage image:
                {
                    var area = new SKPath();
                    area.AddPoly([image.Matrix.MapPoint(0, 0), image.Matrix.MapPoint(1, 0), image.Matrix.MapPoint(1, 1), image.Matrix.MapPoint(0, 1)]);
                    return area;
                }
            case PdfPaintedShading shading when shading.Fill.Shading.BBox is { } box && paint.Clip is null:
                {
                    var area = new SKPath();
                    area.AddPoly([.. new[] { box.BottomLeft, box.BottomRight, box.TopRight, box.TopLeft }.Select(p => new SKPoint((float)p.X, (float)p.Y))]);
                    area.Transform(shading.Fill.Space);
                    return area;
                }
            case PdfPaintedShading { Fill.Picture: { } picture } when paint.Clip is null:
                {
                    var area = new SKPath();
                    area.AddRect(picture.Space.MapRect(SKRect.Create(picture.Image.Width, picture.Image.Height)));
                    return area;
                }
            case PdfPaintedGlyph glyph:
                {
                    var area = new SKPath();
                    area.AddRect(glyph.Area);
                    return area;
                }
            default:
                return null;
        }
    }

    /// <summary>
    /// A glyph's outline filled (or stroked) in its colour; a Type 3 glyph's procedure drawn; one with no outline in the
    /// font is drawn with an installed font.
    /// </summary>
    private static void Glyph(SKCanvas canvas, PdfPaintedGlyph glyph, bool uncolored, PdfRasterCache? cache, CancellationToken cancellationToken)
    {
        if (glyph.Procedure is { } procedure && (cache?.DrawsProcedure(procedure) ?? true))
        {
            canvas.Save();
            canvas.Concat(glyph.Matrix);
            Draw(canvas, procedure.Paints, uncolored, cancellationToken, pictures: true, cache: cache, backdrops: false, ink: procedure.Inked ? glyph.Color : null);
            canvas.Restore();
            return;
        }
        var outline = cache is null ? PdfRasterCache.Shape(glyph) : cache.Outline(glyph);
        if (outline is null)
            return;
        try
        {
            using var paint = Paint(glyph.Color, uncolored);
            canvas.Save();
            canvas.Concat(glyph.Matrix);
            if (glyph.Stroked)
            {
                paint.Style = SKPaintStyle.Stroke;
                // The line width is in slide points; the canvas is in ems here.
                var m = glyph.Matrix;
                var scale = Math.Sqrt(Math.Abs(m.ScaleX * m.ScaleY - m.SkewX * m.SkewY));
                paint.StrokeWidth = (float)(Math.Max(glyph.LineWidth, 0.25) / Math.Max(scale, 1e-3));
            }
            canvas.DrawPath(outline, paint);
            canvas.Restore();
        }
        finally
        {
            if (cache is null)
                outline.Dispose();
        }
    }

    /// <summary>
    /// <paramref name="color"/> where <paramref name="paint"/> paints, under what is drawn there already (DstOver): the
    /// page under a blended paint.
    /// </summary>
    private static void Underlay(SKCanvas canvas, PdfPaint paint, SKColor color)
    {
        using var under = new SKPaint { Color = color, BlendMode = SKBlendMode.DstOver, IsAntialias = true };
        switch (paint)
        {
            case PdfPaintedPath path:
                if (path.Fill is not null)
                    canvas.DrawPath(path.Outline, under);
                if (path.Stroke is not null)
                {
                    under.Style = SKPaintStyle.Stroke;
                    under.StrokeWidth = Math.Max(path.LineWidth, 1 / Math.Max(1, canvas.TotalMatrix.ScaleX));
                    under.StrokeCap = path.Cap;
                    under.StrokeJoin = path.Join;
                    under.StrokeMiter = path.Miter;
                    using var dash = path.Dash is { } intervals ? SKPathEffect.CreateDash(intervals, path.DashPhase) : null;
                    under.PathEffect = dash;
                    canvas.DrawPath(path.Outline, under);
                }
                break;
            case PdfPaintedShading shading when shading.Fill.Shading.BBox is { } box && paint.Clip is null:
                using (var area = new SKPath())
                {
                    area.AddPoly([.. new[] { box.BottomLeft, box.BottomRight, box.TopRight, box.TopLeft }.Select(p => new SKPoint((float)p.X, (float)p.Y))]);
                    area.Transform(shading.Fill.Space);
                    canvas.DrawPath(area, under);
                }
                break;
            case PdfPaintedShading { Fill.Picture: { } picture } when paint.Clip is null:
                canvas.Save();
                canvas.Concat(picture.Space);
                canvas.DrawRect(SKRect.Create(picture.Image.Width, picture.Image.Height), under);
                canvas.Restore();
                break;
            case PdfPaintedShading:
                canvas.DrawPaint(under);
                break;
        }
    }

    /// <summary>
    /// A picture at its place (in a soft mask group, a backdrop, or with a blend mode); a stencil mask in its colour.
    /// One neither PdfPig nor Filee's own decoders read (damaged, or a JBIG2 or JPEG 2000 feature Filee doesn't read) is left out.
    /// </summary>
    private static void Picture(SKCanvas canvas, PdfPaintedImage image, PdfRasterCache? cache)
    {
        if (image.Image is null && image.Own is null)
            return;
        var bitmap = cache is null ? PdfRasterCache.Read(image) : cache.Decode(image);
        if (bitmap is null)
            return;
        try
        {
            // Pixels (y down) → the unit square (y up, top edge at 1) → the slide.
            var toUnit = new SKMatrix(1f / bitmap.Width, 0, 0, 0, -1f / bitmap.Height, 1, 0, 0, 1);
            canvas.Save();
            canvas.Concat(toUnit.PostConcat(image.Matrix));
            // A stencil's colour carries the alpha already.
            var alpha = image.IsMask ? 1 : Math.Clamp(image.Alpha, 0, 1);
            using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(alpha * 255)) };
            canvas.DrawBitmap(bitmap, 0, 0, paint);
            canvas.Restore();
        }
        finally
        {
            if (cache is null)
                bitmap.Dispose();
        }
    }

    /// <summary>A stencil mask (image mask) as pixels of <paramref name="color"/>, transparent where it paints nothing; null when it can't be read.</summary>
    internal static SKBitmap? Stencil(IPdfImage mask, SKColor color)
    {
        if (!mask.TryGetBytesAsMemory(out var memory))
            return null;
        var (width, height) = (mask.WidthInSamples, mask.HeightInSamples);
        var row = (width + 7) / 8;
        if (width <= 0 || height <= 0 || (long)width * height > PdfDocumentReader.MaxGraphicsPixels || memory.Length < (long)row * height)
            return null;
        // Samples of 0 paint, or of 1 with the decode array [1 0].
        var paintsOne = mask.Decode is [> 0.5, ..];
        var bytes = memory.Span;
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var pixels = bitmap.GetPixelSpan();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var bit = (bytes[y * row + x / 8] >> (7 - x % 8)) & 1;
                var offset = (y * width + x) * 4;
                pixels[offset] = color.Red;
                pixels[offset + 1] = color.Green;
                pixels[offset + 2] = color.Blue;
                pixels[offset + 3] = bit == 1 == paintsOne ? color.Alpha : (byte)0;
            }
        }
        return bitmap;
    }

    /// <summary>The luminosity of a shading's picture as alpha (a luminosity mask's shading).</summary>
    private static readonly float[] LuminosityToAlpha =
    [
        0, 0, 0, 0, 0,
        0, 0, 0, 0, 0,
        0, 0, 0, 0, 0,
        0.3f, 0.59f, 0.11f, 0, 0,
    ];

    /// <summary>Keeps of the layer what the mask lets through (the canvas's matrix maps slide coordinates).</summary>
    internal static void ApplyMask(SKCanvas canvas, PdfMask mask)
    {
        var alpha = (byte)Math.Round(Math.Clamp(mask.Alpha, 0, 1) * 255);
        using var paint = new SKPaint { BlendMode = SKBlendMode.DstIn, Color = SKColors.Black.WithAlpha(alpha) };
        if (mask.Picture is { } picture)
        {
            paint.Shader = picture.Shader();
        }
        else if (mask.Shading is { Picture: { } drawn })
        {
            paint.Shader = drawn.Shader();
            paint.ColorFilter = SKColorFilter.CreateColorMatrix(LuminosityToAlpha);
        }
        else if (mask.Shading is { } shading)
        {
            paint.Shader = PdfShadings.Shader(shading, c => SKColors.Black.WithAlpha((byte)Math.Round(PdfShadings.Luminosity(c) * 255)));
        }
        canvas.DrawPaint(paint);
        paint.Shader?.Dispose();
        paint.ColorFilter?.Dispose();
    }

    private static SKPaint Paint(PdfPaintColor color, bool uncolored)
    {
        var paint = new SKPaint { IsAntialias = true, Color = uncolored ? SKColors.Black.WithAlpha(color.Color.Alpha) : color.Color };
        if (color.Shading is { } shading && PdfShadings.Shader(shading) is { } shader)
        {
            paint.Shader = shader;
            paint.Color = SKColors.White.WithAlpha(color.Color.Alpha);
        }
        else if (color.Tile is { } tile)
        {
            paint.Shader = tile.Image.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, new SKSamplingOptions(SKFilterMode.Linear), tile.Space);
            paint.Color = SKColors.White.WithAlpha(color.Color.Alpha);
        }
        return paint;
    }
}
