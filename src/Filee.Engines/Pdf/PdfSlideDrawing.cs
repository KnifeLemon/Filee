// What a PDF page paints, in the order it paints it, for editable slides (PdfDocumentReader.Slides). PdfPig's own page
// keeps letters, paths and pictures in lists of their own, without the clipping path, the transparency, the shading of
// a fill or the matrix a picture is drawn with. This page factory (PdfDocument.AddPageFactory) runs the page's content
// stream once more through PdfPig's stream processor and records, in paint order:
//
// - paths with their outline (slide coordinates: points from the page's top-left corner, y down), fill and stroke
//   (a colour with the constant alpha ca / CA, an axial or radial shading, or the picture of a tiling pattern's cell),
//   line width, dashes, caps and joins as drawn under the transformation;
// - pictures with the matrix that maps their unit square onto the page, stencil masks (image masks) with the colour
//   they paint in;
// - shadings painted with "sh" over the clipping area;
// - where each glyph starts and when it is drawn, so text boxes take their place among the rest;
// - the clipping path everything is painted through (an intersection of the page's W / W* paths and the boxes of
//   form XObjects), and a soft mask when it is simple: a group that paints one colour (a constant alpha) or one axial
//   or radial shading (an alpha gradient, from its luminosity).
//
// Tiling patterns are drawn here, while the pattern's resources can be looked up: the content of one cell into a
// picture that the fill repeats. Text in a pattern cell, pictures in it and patterns more than MaxDepth deep are left
// out of it.
//
// Limits: at most PdfDocumentReader.MaxGraphicsSegments path segments and MaxPaints paints a page (the rest keep their
// place in the order but aren't recorded), MaxGlyphs glyph positions, clipping paths of more than MaxClipCommands
// segments clip by their box.

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
internal sealed record PdfShadingFill(Shading Shading, SKMatrix Space);

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

/// <summary>A soft mask simple enough to keep: a constant <paramref name="Alpha"/>, or an alpha gradient from a shading's luminosity.</summary>
internal sealed record PdfMask(double Alpha, PdfShadingFill? Shading = null);

/// <summary>Something painted, the <paramref name="Order"/>-th thing the page paints (glyphs counted too).</summary>
internal abstract record PdfPaint(int Order, PdfClip? Clip, PdfMask? Mask);

/// <summary>A filled and / or stroked path; the line width and dashes are in slide points.</summary>
internal sealed record PdfPaintedPath(int Order, PdfClip? Clip, PdfMask? Mask, SKPath Outline, bool Curved, PdfPaintColor? Fill, PdfPaintColor? Stroke,
    float LineWidth, float[]? Dash, float DashPhase, SKStrokeCap Cap, SKStrokeJoin Join, float Miter) : PdfPaint(Order, Clip, Mask);

/// <summary>
/// A picture: <paramref name="Matrix"/> maps its unit square (y up, the picture's top edge at 1) onto the slide.
/// A stencil mask paints <paramref name="MaskColor"/> where its samples say.
/// </summary>
internal sealed record PdfPaintedImage(int Order, PdfClip? Clip, PdfMask? Mask, SKMatrix Matrix, IPdfImage? Image, SKColor MaskColor, double Alpha)
    : PdfPaint(Order, Clip, Mask);

/// <summary>A shading painted with "sh" over the clipping area (or its own box).</summary>
internal sealed record PdfPaintedShading(int Order, PdfClip? Clip, PdfMask? Mask, PdfShadingFill Fill, double Alpha) : PdfPaint(Order, Clip, Mask);

/// <summary>
/// Where a glyph starts (PdfPig's coordinates: y up, as its letters' StartBaseLine) and when it is drawn;
/// <paramref name="Turned"/>: its baseline doesn't run left to right.
/// </summary>
internal readonly record struct PdfGlyphOrder(double X, double Y, int Order, bool Turned = false);

/// <summary>What a page's drawing may still take, shared with the pattern cells and soft mask groups drawn for it.</summary>
internal sealed class PdfDrawingBudget
{
    public int Segments { get; set; }
    public int Paints { get; set; }
    public int Tiles { get; set; }
    public int Masks { get; set; }
    public int ClipIntersections { get; set; }
}

/// <summary>A page's paints in order; owns their outlines, clipping paths and tile pictures.</summary>
internal sealed class PdfSlideDrawing : IDisposable
{
    public List<PdfPaint> Paints { get; } = [];

    public List<PdfGlyphOrder> Glyphs { get; } = [];

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
        var processor = new PdfSlideDrawingProcessor(pageNumber, ResourceStore, PdfScanner, PageContentParser, FilterProvider, cropBox, userSpaceUnit,
            rotation, initialMatrix, ParsingOptions, toSlide, 0, new PdfDrawingBudget());
        return processor.Process(pageNumber, operations);
    }
}

/// <summary>PdfPig's stream processor recording paints instead of building its page model.</summary>
internal sealed class PdfSlideDrawingProcessor : BaseStreamProcessor<PdfSlideDrawing>
{
    internal const int MaxPaints = 200_000;
    internal const int MaxGlyphs = 500_000;
    internal const int MaxClipCommands = 20_000;

    /// <summary>Tiling pattern cells and soft mask groups inside each other at most.</summary>
    internal const int MaxDepth = 2;

    /// <summary>Soft mask groups looked into on one page at most.</summary>
    private const int MaxMasks = 256;

    /// <summary>Tiling pattern cells drawn for one page at most; fills with more patterns are left out.</summary>
    private const int MaxTiles = 64;

    /// <summary>Clipping paths other than rectangles intersected on one page at most; later ones clip by their box.</summary>
    private const int MaxClipIntersections = 10_000;

    /// <summary>Pixels per point of a tiling pattern's cell picture, and its largest side.</summary>
    private const float TileScale = 3;

    private const int MaxTileSide = 1024;

    private readonly PdfSlideDrawing _drawing = new();
    private readonly SKMatrix _toSlide;
    private readonly int _depth;
    private readonly PdfDrawingBudget _budget;
    private readonly CropBox _cropBox;
    private readonly UserSpaceUnit _userSpaceUnit;
    private readonly PageRotationDegrees _rotation;

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
    private readonly Dictionary<SoftMask, PdfMask?> _masks = new(ReferenceEqualityComparer.Instance);
    private int _order;

    public PdfSlideDrawingProcessor(int pageNumber, IResourceStore resourceStore, IPdfTokenScanner pdfScanner, IPageContentParser pageContentParser,
        ILookupFilterProvider filterProvider, CropBox cropBox, UserSpaceUnit userSpaceUnit, PageRotationDegrees rotation,
        TransformationMatrix initialMatrix, ParsingOptions parsingOptions, SKMatrix toSlide, int depth, PdfDrawingBudget budget)
        : base(pageNumber, resourceStore, pdfScanner, pageContentParser, filterProvider, cropBox, userSpaceUnit, rotation, in initialMatrix, parsingOptions)
    {
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
                (float)Math.Clamp(state.MiterLimit, 1, 100)));
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
                var fill = new PdfShadingFill(shading.Shading, PatternSpace(shading.Matrix));
                return new PdfPaintColor(PdfShadings.ColorAt(shading.Shading, 0.5).WithAlpha(a), alpha, fill);
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
            if (Nested(pattern.PatternStream, pattern.Resources, TransformationMatrix.Identity, toPixels) is { } drawing)
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
    private PdfSlideDrawing? Nested(StreamToken stream, DictionaryToken? resources, TransformationMatrix initial, SKMatrix toSlide)
    {
        IReadOnlyList<IGraphicsStateOperation> operations;
        try
        {
            operations = GetFormOperations(stream);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return null; // a broken stream: the fill is left out, not the page
        }
        var nested = new PdfSlideDrawingProcessor(PageNumber, ResourceStore, PdfScanner, PageContentParser, FilterProvider, _cropBox, _userSpaceUnit,
            _rotation, initial, ParsingOptions, toSlide, _depth + 1, _budget);
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

    /// <summary>The current soft mask when it is simple (one colour or one shading painted), else null (left out).</summary>
    private PdfMask? Mask(CurrentGraphicsState state)
    {
        if (state.SoftMask is not { TransparencyGroup: { } group } mask || _depth >= MaxDepth)
            return null;
        if (_masks.TryGetValue(mask, out var cached))
            return cached;
        if (_budget.Masks >= MaxMasks)
            return null;
        _budget.Masks++;
        PdfMask? result = null;
        var resources = group.StreamDictionary.TryGet(NameToken.Resources, PdfScanner, out DictionaryToken? found) ? found : null;
        // The group is drawn in the space current when the mask was set: taken as the current one.
        var initial = Matrix(group.StreamDictionary, NameToken.Matrix).Multiply(CurrentTransformationMatrix);
        if (Nested(group, resources, initial, _toSlide) is { } drawing)
        {
            using (drawing)
            {
                var luminosity = mask.Subtype == SoftMaskType.Luminosity;
                result = drawing.Paints switch
                {
                    [PdfPaintedShading { Fill: var fill } shading] when luminosity => new PdfMask(shading.Alpha, fill),
                    [PdfPaintedPath { Fill: { Shading: { } fill } paint, Stroke: null }] when luminosity => new PdfMask(paint.Alpha, fill),
                    [PdfPaintedPath { Fill: { IsSolid: true } paint, Stroke: null }] =>
                        new PdfMask(paint.Alpha * (luminosity ? PdfShadings.Luminosity(paint.Color) : 1)),
                    _ => null,
                };
            }
        }
        return _masks[mask] = result;
    }

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
            // An image PdfPig can't read: PdfDocumentReader's picture of it (cut from the rendered page) keeps its place.
        }
        AddImage(image, xObjectContentRecord.AppliedTransformation);
    }

    protected override void RenderInlineImage(InlineImage inlineImage) => AddImage(inlineImage, CurrentTransformationMatrix);

    private void AddImage(IPdfImage? image, TransformationMatrix matrix)
    {
        if (_budget.Paints < MaxPaints)
        {
            _budget.Paints++;
            var state = GetCurrentState();
            var alpha = Math.Clamp(double.IsFinite(state.AlphaConstantNonStroking) ? state.AlphaConstantNonStroking : 1, 0, 1);
            _drawing.Paints.Add(new PdfPaintedImage(_order, _clip, Mask(state), ToSlide(matrix), image,
                Rgb(state.CurrentNonStrokingColor).WithAlpha((byte)Math.Round(alpha * 255)), alpha));
        }
        _order++;
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
            _drawing.Paints.Add(new PdfPaintedShading(_order, _clip, Mask(state), new PdfShadingFill(shading, ToSlide(CurrentTransformationMatrix)), alpha));
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
        }
        _order++;
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
    /// Draws <paramref name="paints"/> (pictures are left out: they are pictures of their own) through their clipping
    /// paths, alpha and soft masks. <paramref name="uncolored"/>: a pattern cell that gets its colour from the fill (black here).
    /// </summary>
    public static void Draw(SKCanvas canvas, IEnumerable<PdfPaint> paints, bool uncolored = false, CancellationToken cancellationToken = default)
    {
        foreach (var paint in paints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (paint is PdfPaintedImage)
                continue;
            canvas.Save();
            if (paint.Clip is { } clip)
                canvas.ClipPath(clip.Path, SKClipOperation.Intersect, true);
            var masked = paint.Mask is { } mask && (mask.Shading is not null || mask.Alpha < 1);
            if (masked)
                canvas.SaveLayer();
            switch (paint)
            {
                case PdfPaintedPath path:
                    if (path.Fill is { } fill)
                    {
                        using var fillPaint = Paint(fill, uncolored);
                        fillPaint.Style = SKPaintStyle.Fill;
                        canvas.DrawPath(path.Outline, fillPaint);
                    }
                    if (path.Stroke is { } stroke)
                    {
                        using var strokePaint = Paint(stroke, uncolored);
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
            canvas.Restore();
        }
    }

    /// <summary>Keeps of the layer what the mask lets through.</summary>
    private static void ApplyMask(SKCanvas canvas, PdfMask mask)
    {
        var alpha = (byte)Math.Round(Math.Clamp(mask.Alpha, 0, 1) * 255);
        using var paint = new SKPaint { BlendMode = SKBlendMode.DstIn, Color = SKColors.Black.WithAlpha(alpha) };
        if (mask.Shading is { } shading)
            paint.Shader = PdfShadings.Shader(shading, c => SKColors.Black.WithAlpha((byte)Math.Round(PdfShadings.Luminosity(c) * 255)));
        canvas.DrawPaint(paint);
        paint.Shader?.Dispose();
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
