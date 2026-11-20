// SVG drawings as the slide model (Slides/SlideModel.cs), so the slide renderer draws them (PDF, SVG, PNG) and the ODP
// writer writes them as shapes that stay editable. Written for the SVG of interactive whiteboard files (IwbReader:
// SVG Tiny 1.2 with SVG 1.2's pageSet and textArea), which Svg.Skia doesn't read; every element is resolved into page
// coordinates here, so the model needs no transforms of its own.
//
// - Structure: svg (nested, with viewBox and preserveAspectRatio), g and a (a g of several drawn things becomes a
//   group), switch (its first child), use (#id references, loops cut), defs / symbol only through use; title, desc,
//   metadata and unknown elements are skipped; groups nest 16 deep at most in the model (deeper ones are
//   flattened). display:none, visibility:hidden and the hidden ids the caller names aren't drawn.
// - Shapes: rect (rounded), circle, ellipse, line, polyline, polygon and path (SVG path data), with their transform
//   applied to the outline, so a turned, skewed or scaled shape keeps its exact form; fill and stroke (colours,
//   currentColor, opacity, fill-opacity, stroke-opacity, fill-rule, width, dashes, caps and joins), linear and radial
//   gradients (their stops through xlink:href; a radial gradient as an ellipse over the box, focus at its centre).
// - Text: text and tspan, one text box per positioned run (x / y), at its baseline, anchored by text-anchor, unwrapped;
//   textArea (SVG Tiny 1.2, also written lowercase) wraps at its width, tbreak starts a new line, text-align and
//   display-align place it; foreignObject with XHTML text keeps its paragraphs. Font family, size, weight, style,
//   underline and line-through and the fill colour per run; the box turns with the text's transform.
// - Pictures: image with PNG, JPEG, GIF, BMP, WebP, EMF, WMF (what the renderer decodes, within
//   DocumentLimits.MaxPicturePixels), from the package or a data: URI, fitted by preserveAspectRatio (meet, slice,
//   none); an SVG picture is read as more of the drawing (in its own folder, nested at most 8 deep, loops cut).
// - Left out and counted (LeftOut): video, audio, animation and foreignObject without text, pictures that can't be
//   decoded or aren't in the package. Not drawn: filters, masks, clip paths, markers, patterns, CSS style sheets.
//
// Hostile input: XML through SafeXml within DocumentLimits (no DTD; a DOCTYPE without an internal subset is dropped
// first, as SVG editors write one), nesting (MaxDepth), uses followed, elements visited (use expansions included),
// path points and the budget's objects and paragraphs; numbers are clamped and must be finite. Refusals are
// error.document_too_complex, or error.document_too_large for a picture's bytes.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Slides;
using Filee.Engines.Vector;
using SkiaSharp;

namespace Filee.Engines.Whiteboard;

/// <summary>Reads SVG elements into slide elements.</summary>
internal sealed partial class SvgSlideReader
{
    /// <summary>SVG user units are CSS pixels; the slide model is in points.</summary>
    public const float PointsPerPixel = 0.75f;

    /// <summary>Levels of elements (use references and SVG pictures included) followed.</summary>
    internal const int MaxDepth = 256;

    /// <summary>Elements visited for one file, each use expansion again.</summary>
    internal const int MaxVisits = 2_000_000;

    /// <summary>use elements followed for one file (each is a copy of what it names, uses in it again).</summary>
    internal const int MaxUses = 50_000;

    /// <summary>Points of all outlines of one file.</summary>
    internal const long MaxPathPoints = 10_000_000;

    /// <summary>SVG pictures inside SVG pictures.</summary>
    private const int MaxPictureNesting = 8;

    /// <summary>Groups in groups the model gets (deeper ones are drawn without their group; the ODP writer allows 32).</summary>
    private const int MaxGroupNesting = 16;

    private const float MaxLength = 1e6f;

    /// <summary>Where a baseline sits below the top of a line, and an average glyph's width, in em (Arial's ascent).</summary>
    internal const float Ascent = 0.905f;

    private const float CharWidth = 0.55f;

    /// <summary>Single-spaced lines (the font's own line height), no space between paragraphs.</summary>
    private static readonly HParaFormat Single = new(Before: 0, After: 0, LineSpacing: new HLineSpacing(HLineSpacingKind.Percent, 120),
        Layout: new HParaLayout(LineHeight: new HLineHeight(HLineHeightRule.Multiple, 240)));

    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";
    private static readonly XNamespace XmlNs = XNamespace.Xml;

    private readonly Func<string, string, SvgFile?> _files;
    private readonly DocumentLimits _limits;
    private readonly DocumentBudget _budget;
    private readonly CancellationToken _token;
    private readonly HashSet<string> _hidden;
    private readonly Dictionary<string, int> _leftOut = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SvgDocument?> _pictures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _openPictures = new(StringComparer.Ordinal);
    private readonly HashSet<XElement> _openUses = new(ReferenceEqualityComparer.Instance);
    private int _visits;
    private int _uses;
    private long _points;
    private int _depth;
    private int _groups;

    /// <param name="files">A file of the package by the folder of the SVG naming it and its href; null when it isn't there.</param>
    /// <param name="elementsBefore">XML elements the package's other parts held (they count against the limit).</param>
    /// <param name="hidden">Ids of elements not drawn.</param>
    public SvgSlideReader(Func<string, string, SvgFile?> files, DocumentLimits limits, int elementsBefore, IEnumerable<string>? hidden,
        CancellationToken cancellationToken)
    {
        _files = files;
        _limits = limits;
        _budget = new DocumentBudget(limits);
        XmlElements = elementsBefore;
        _hidden = new HashSet<string>(hidden ?? [], StringComparer.Ordinal);
        _token = cancellationToken;
    }

    /// <summary>XML elements read so far, the caller's included.</summary>
    public int XmlElements { get; private set; }

    /// <summary>What wasn't drawn, as the renderer's left-out parts.</summary>
    public IReadOnlyList<LeftOutPart> LeftOut =>
        [.. _leftOut.Where(p => p.Value > 0).Select(p => new LeftOutPart("notice.renderer_left_out." + p.Key, p.Value))];

    private void Drop(string kind) => _leftOut[kind] = _leftOut.GetValueOrDefault(kind) + 1;

    // ───────────────────────── Documents ─────────────────────────

    /// <summary>
    /// SVG or other XML from a file: a DOCTYPE without an internal subset is dropped (SafeXml reads no DTD), a stray
    /// "&amp;" escaped when that is what stops it. Throws XmlException for anything else that isn't well formed.
    /// </summary>
    public XElement LoadXml(byte[] bytes)
    {
        bytes = WithoutDoctype(bytes) ?? throw new XmlException("The XML declares entities in a DTD, which Filee doesn't read.");
        try
        {
            return Parse(bytes);
        }
        catch (XmlException) when (SvgXmlRepair.FixAmpersands(bytes) is { } repaired)
        {
            return Parse(repaired);
        }
    }

    private XElement Parse(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        try
        {
            var document = SafeXml.Load(stream, _limits.Xml(XmlElements, preserveWhitespace: true), DocumentLimits.Exceeded, out var elements, DocumentLimits.TooLarge);
            XmlElements += elements;
            return document.Root ?? throw new XmlException("The XML has no root element.");
        }
        catch (Exception ex) when (ex is DecoderFallbackException or ArgumentException)
        {
            throw new XmlException(ex.Message, ex);
        }
    }

    /// <summary>The bytes without their DOCTYPE; null when it has an internal subset (entities Filee won't expand).</summary>
    internal static byte[]? WithoutDoctype(byte[] bytes)
    {
        var head = bytes.AsSpan(0, Math.Min(bytes.Length, 4096));
        var at = head.IndexOf("<!DOCTYPE"u8);
        if (at < 0)
            return bytes;
        var end = bytes.AsSpan(at).IndexOf((byte)'>');
        if (end < 0)
            return null;
        if (bytes.AsSpan(at, end).IndexOf((byte)'[') >= 0)
            return null;
        return [.. bytes.AsSpan(0, at), .. bytes.AsSpan(at + end + 1)];
    }

    /// <summary>An SVG document: its root, the package folder its hrefs start from, its elements by id.</summary>
    public sealed class SvgDocument(XElement root, string folder)
    {
        private Dictionary<string, XElement>? _ids;

        public XElement Root { get; } = root;
        public string Folder { get; } = folder;

        public XElement? Find(string id)
        {
            if (_ids is null)
            {
                _ids = new Dictionary<string, XElement>(StringComparer.Ordinal);
                foreach (var element in Root.DescendantsAndSelf())
                {
                    if (Attr(element, "id") is { Length: > 0 } key)
                        _ids.TryAdd(key, element);
                }
            }
            return _ids.GetValueOrDefault(id);
        }
    }

    /// <summary>A file of the package: its bytes and its name (the folder its own hrefs start from).</summary>
    internal sealed record SvgFile(byte[] Data, string Name);

    // ───────────────────────── Viewports ─────────────────────────

    /// <summary>
    /// The size of an outermost svg element in pixels (width and height, else its viewBox, else
    /// <paramref name="fallback"/>), and the matrix from its user units to points on a page of that size.
    /// </summary>
    public static (SKSize Pixels, SKMatrix ToPoints) Viewport(XElement svg, SKSize fallback)
    {
        var viewBox = ViewBox(svg);
        var width = Length(Attr(svg, "width"), fallback.Width, 16) is { } w and > 0 ? w : viewBox?.Width ?? fallback.Width;
        var height = Length(Attr(svg, "height"), fallback.Height, 16) is { } h and > 0 ? h : viewBox?.Height ?? fallback.Height;
        var size = new SKSize(Math.Clamp(width, 1, MaxLength), Math.Clamp(height, 1, MaxLength));
        var matrix = SKMatrix.CreateScale(PointsPerPixel, PointsPerPixel);
        if (viewBox is { } box)
            matrix = matrix.PreConcat(Fit(box, new SKRect(0, 0, size.Width, size.Height), Attr(svg, "preserveAspectRatio")).Matrix);
        return (size, matrix);
    }

    /// <summary>A viewBox ("x y width height"); null when missing or empty.</summary>
    internal static SKRect? ViewBox(XElement element)
    {
        var numbers = Numbers(Attr(element, "viewBox"));
        if (numbers.Count != 4 || numbers[2] <= 0 || numbers[3] <= 0)
            return null;
        return SKRect.Create(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    /// <summary>
    /// <paramref name="source"/> fitted into <paramref name="target"/> by a preserveAspectRatio value (xMidYMid meet
    /// when missing): the matrix, and with slice the part of the source shown (as insets in fractions of it).
    /// </summary>
    internal static (SKMatrix Matrix, SKRect Crop) Fit(SKRect source, SKRect target, string? preserve)
    {
        var parts = (preserve ?? "").Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Where(p => p != "defer").ToArray();
        var align = parts.Length > 0 ? parts[0] : "xMidYMid";
        var slice = parts.Length > 1 && parts[1] == "slice";
        var sx = target.Width / source.Width;
        var sy = target.Height / source.Height;
        if (align == "none")
        {
            var stretch = SKMatrix.CreateTranslation(target.Left, target.Top)
                .PreConcat(SKMatrix.CreateScale(sx, sy)).PreConcat(SKMatrix.CreateTranslation(-source.Left, -source.Top));
            return (stretch, SKRect.Empty);
        }
        var scale = slice ? Math.Max(sx, sy) : Math.Min(sx, sy);
        float Share(string axis) => align.Contains(axis + "Min", StringComparison.Ordinal) ? 0 : align.Contains(axis + "Max", StringComparison.Ordinal) ? 1 : 0.5f;
        var (ax, ay) = (Share("x"), Share("Y"));
        var dx = target.Left + (target.Width - source.Width * scale) * ax;
        var dy = target.Top + (target.Height - source.Height * scale) * ay;
        var matrix = SKMatrix.CreateTranslation(dx, dy).PreConcat(SKMatrix.CreateScale(scale, scale))
            .PreConcat(SKMatrix.CreateTranslation(-source.Left, -source.Top));
        var crop = SKRect.Empty;
        if (slice)
        {
            // The part of the source outside the target, split by the alignment.
            var overX = Math.Max(0, 1 - target.Width / (source.Width * scale));
            var overY = Math.Max(0, 1 - target.Height / (source.Height * scale));
            crop = new SKRect(overX * ax, overY * ay, overX * (1 - ax), overY * (1 - ay));
        }
        return (matrix, crop);
    }

    /// <summary>The fill behind a page or drawing (SVG Tiny 1.2 viewport-fill); null for none.</summary>
    public static SlideFill? ViewportFill(XElement element)
    {
        var css = CssDeclarations.Parse(Attr(element, "style"));
        var value = css.Get("viewport-fill") ?? Attr(element, "viewport-fill");
        if (value is null || Color(value) is not { } color)
            return null;
        var opacity = Fraction(css.Get("viewport-fill-opacity") ?? Attr(element, "viewport-fill-opacity")) ?? 1;
        return color.Alpha == 0 || opacity <= 0 ? null : new SolidFill(color.WithAlpha((byte)Math.Round(color.Alpha * opacity)));
    }

    // ───────────────────────── Walking ─────────────────────────

    /// <summary>Draws <paramref name="elements"/> of <paramref name="document"/> into <paramref name="output"/>.</summary>
    /// <param name="matrix">User units of the elements' parent → points on the slide.</param>
    /// <param name="style">The style of the elements' parent.</param>
    public void Draw(IEnumerable<XElement> elements, SvgDocument document, SKMatrix matrix, SvgStyle style, List<SlideElement> output) =>
        Children(elements, document, matrix, style, style.Opacity, output);

    private void Children(IEnumerable<XElement> elements, SvgDocument document, SKMatrix matrix, SvgStyle style, float opacity, List<SlideElement> output)
    {
        foreach (var element in elements)
            Element(element, document, matrix, style, opacity, output);
    }

    private void Element(XElement element, SvgDocument document, SKMatrix parent, SvgStyle inherited, float opacity, List<SlideElement> output)
    {
        _token.ThrowIfCancellationRequested();
        if (++_visits > MaxVisits)
            throw DocumentLimits.Exceeded($"The drawing has more than {DocumentLimits.Count(MaxVisits)} elements to draw.");
        if (_depth >= MaxDepth)
            throw DocumentLimits.Exceeded($"The drawing nests elements more than {MaxDepth} levels deep.");
        if (Attr(element, "id") is { } id && _hidden.Contains(id))
            return;
        var name = element.Name.LocalName.ToLowerInvariant();
        if (name is "defs" or "symbol" or "title" or "desc" or "metadata" or "style" or "script" or "lineargradient" or "radialgradient"
            or "pattern" or "clippath" or "mask" or "filter" or "marker" or "font" or "font-face" or "solidcolor")
            return;
        var style = inherited.Apply(element);
        if (style.Hidden)
            return;
        var matrix = parent.PreConcat(Transform(Attr(element, "transform")));
        if (!IsFinite(matrix))
            return;
        opacity *= style.Opacity;
        _depth++;
        try
        {
            switch (name)
            {
                case "g" or "a" or "page" or "layer":
                    Group(element.Elements(), document, matrix, style, opacity, output, name == "g");
                    break;
                case "switch":
                    if (element.Elements().FirstOrDefault(e => Attr(e, "requiredExtensions") is null) is { } first)
                        Element(first, document, matrix, style, opacity, output);
                    break;
                case "svg":
                    NestedSvg(element, document, matrix, style, opacity, output);
                    break;
                case "use":
                    Use(element, document, matrix, style, opacity, output);
                    break;
                case "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon" or "path":
                    if (style.Visible)
                        Add(output, Shape(element, name, document, matrix, style, opacity));
                    break;
                case "text":
                    if (style.Visible || element.Descendants().Any())
                        Text(element, matrix, style, opacity, output);
                    break;
                case "textarea":
                    TextArea(element, matrix, style, opacity, output);
                    break;
                case "foreignobject":
                    ForeignObject(element, matrix, style, opacity, output);
                    break;
                case "image":
                    if (style.Visible)
                        Image(element, document, matrix, style, opacity, output);
                    break;
                case "video":
                    Drop("videos");
                    break;
                case "audio" or "animation" or "iframe" or "embed" or "object":
                    Drop("objects");
                    break;
            }
        }
        finally
        {
            _depth--;
        }
    }

    private static void Add(List<SlideElement> output, SlideElement? element)
    {
        if (element is not null)
            output.Add(element);
    }

    /// <summary>Children of a g: a group when there are several drawn things in it (and <paramref name="group"/>).</summary>
    private void Group(IEnumerable<XElement> children, SvgDocument document, SKMatrix matrix, SvgStyle style, float opacity, List<SlideElement> output, bool group)
    {
        var items = new List<SlideElement>();
        _groups += group ? 1 : 0;
        try
        {
            Children(children, document, matrix, style, opacity, items);
        }
        finally
        {
            _groups -= group ? 1 : 0;
        }
        if (!group || items.Count < 2 || _groups >= MaxGroupNesting)
        {
            output.AddRange(items);
            return;
        }
        var bounds = items.Select(Extent).Aggregate((a, b) => SKRect.Union(a, b));
        var result = new SlideGroup { Bounds = bounds, ChildBounds = bounds };
        result.Children.AddRange(items);
        output.Add(result);
    }

    /// <summary>The box an element takes on its parent, turned.</summary>
    private static SKRect Extent(SlideElement element)
    {
        if (element.Rotation == 0)
            return element.Bounds;
        var box = element.Bounds;
        var turn = SKMatrix.CreateRotationDegrees(element.Rotation, box.MidX, box.MidY);
        return turn.MapRect(box);
    }

    private void NestedSvg(XElement svg, SvgDocument document, SKMatrix matrix, SvgStyle style, float opacity, List<SlideElement> output)
    {
        var x = Length(Attr(svg, "x"), 0, style.FontSize) ?? 0;
        var y = Length(Attr(svg, "y"), 0, style.FontSize) ?? 0;
        var viewBox = ViewBox(svg);
        var width = Length(Attr(svg, "width"), 0, style.FontSize) is { } w and > 0 ? w : viewBox?.Width ?? 0;
        var height = Length(Attr(svg, "height"), 0, style.FontSize) is { } h and > 0 ? h : viewBox?.Height ?? 0;
        matrix = matrix.PreConcat(SKMatrix.CreateTranslation(x, y));
        if (viewBox is { } box && width > 0 && height > 0)
            matrix = matrix.PreConcat(Fit(box, new SKRect(0, 0, width, height), Attr(svg, "preserveAspectRatio")).Matrix);
        Group(svg.Elements(), document, matrix, style, opacity, output, group: true);
    }

    private void Use(XElement use, SvgDocument document, SKMatrix matrix, SvgStyle style, float opacity, List<SlideElement> output)
    {
        if (Href(use) is not ['#', .. var id] || document.Find(id) is not { } target || !_openUses.Add(target))
            return;
        if (++_uses > MaxUses)
        {
            _openUses.Remove(target);
            throw DocumentLimits.Exceeded($"The drawing repeats elements (use) more than {DocumentLimits.Count(MaxUses)} times.");
        }
        try
        {
            var x = Length(Attr(use, "x"), 0, style.FontSize) ?? 0;
            var y = Length(Attr(use, "y"), 0, style.FontSize) ?? 0;
            matrix = matrix.PreConcat(SKMatrix.CreateTranslation(x, y));
            // A symbol is drawn as a nested svg; anything else as itself.
            if (target.Name.LocalName == "symbol")
                NestedSvg(target, document, matrix, style, opacity, output);
            else
                Element(target, document, matrix, style, opacity, output);
        }
        finally
        {
            _openUses.Remove(target);
        }
    }

    // ───────────────────────── Shapes ─────────────────────────

    private SlideShape? Shape(XElement element, string name, SvgDocument document, SKMatrix matrix, SvgStyle style, float opacity)
    {
        float L(string attribute, float fallback = 0) => Length(Attr(element, attribute), 0, style.FontSize) ?? fallback;
        var path = new SKPath();
        var filled = name != "line";
        switch (name)
        {
            case "rect":
                var (w, h) = (L("width"), L("height"));
                if (w <= 0 || h <= 0)
                    return null;
                var rect = SKRect.Create(L("x"), L("y"), w, h);
                var rx = Length(Attr(element, "rx"), 0, style.FontSize);
                var ry = Length(Attr(element, "ry"), 0, style.FontSize);
                var (cornerX, cornerY) = (Math.Clamp(rx ?? ry ?? 0, 0, w / 2), Math.Clamp(ry ?? rx ?? 0, 0, h / 2));
                if (cornerX > 0 && cornerY > 0)
                    path.AddRoundRect(rect, cornerX, cornerY);
                else
                    path.AddRect(rect);
                break;
            case "circle":
                var r = L("r");
                if (r <= 0)
                    return null;
                path.AddCircle(L("cx"), L("cy"), r);
                break;
            case "ellipse":
                var (erx, ery) = (L("rx"), L("ry"));
                if (erx <= 0 || ery <= 0)
                    return null;
                path.AddOval(SKRect.Create(L("cx") - erx, L("cy") - ery, erx * 2, ery * 2));
                break;
            case "line":
                path.MoveTo(L("x1"), L("y1"));
                path.LineTo(L("x2"), L("y2"));
                break;
            case "polyline" or "polygon":
                var points = Numbers(Attr(element, "points"));
                if (points.Count < 4)
                    return null;
                path.MoveTo(points[0], points[1]);
                for (var i = 2; i + 1 < points.Count; i += 2)
                    path.LineTo(points[i], points[i + 1]);
                if (name == "polygon")
                    path.Close();
                break;
            case "path":
                if (Attr(element, "d") is not { Length: > 0 } data)
                    return null;
                path.Dispose();
                path = SKPath.ParseSvgPathData(data);
                if (path is null)
                    return null;
                break;
        }
        _points += path.PointCount;
        if (_points > MaxPathPoints)
            throw DocumentLimits.Exceeded($"The drawing's outlines have more than {DocumentLimits.Count(MaxPathPoints)} points.");
        if (style.EvenOdd)
            path.FillType = SKPathFillType.EvenOdd;
        var local = path.TightBounds;
        path.Transform(matrix);
        var bounds = path.TightBounds;
        if (path.IsEmpty || !IsFinite(bounds))
            return null;
        path.Transform(SKMatrix.CreateTranslation(-bounds.Left, -bounds.Top));
        var fill = filled ? Paint(style.Fill, style.FillOpacity * opacity, style, document, matrix, local) : null;
        var line = Line(style, opacity, document, matrix, local);
        if (fill is null && line is null)
            return null;
        _budget.Object();
        return new SlideShape
        {
            Bounds = bounds,
            Paths = [new SlidePath(path, fill is null ? PathFill.None : PathFill.Normal)],
            Fill = fill,
            Line = line,
        };
    }

    private SlideLine? Line(SvgStyle style, float opacity, SvgDocument document, SKMatrix matrix, SKRect local)
    {
        if (style.StrokeWidth <= 0 || Paint(style.Stroke, style.StrokeOpacity * opacity, style, document, matrix, local) is not { } fill)
            return null;
        var scale = Scale(matrix);
        var width = style.StrokeWidth * scale;
        if (!float.IsFinite(width) || width <= 0)
            return null;
        float[]? dash = null;
        if (style.Dash is { Length: > 0 } pattern && pattern.Any(d => d > 0))
            dash = [.. pattern.Select(d => Math.Clamp(d / style.StrokeWidth, 0.01f, 10_000f))];
        return new SlideLine(fill, Math.Min(width, 10_000), dash, style.Cap, style.Join);
    }

    /// <summary>How much the matrix scales lengths (the square root of its area scale).</summary>
    private static float Scale(SKMatrix matrix) => MathF.Sqrt(MathF.Abs(matrix.ScaleX * matrix.ScaleY - matrix.SkewX * matrix.SkewY));

    // ───────────────────────── Paint ─────────────────────────

    /// <summary>A fill or stroke paint as a slide fill; null for none.</summary>
    /// <param name="local">The shape's box in its own user units (before <paramref name="matrix"/>).</param>
    private SlideFill? Paint(SvgPaint paint, float opacity, SvgStyle style, SvgDocument document, SKMatrix matrix, SKRect local)
    {
        opacity = Math.Clamp(opacity, 0, 1);
        if (opacity <= 0)
            return null;
        switch (paint.Kind)
        {
            case SvgPaintKind.Color:
                return Solid(paint.Color, opacity);
            case SvgPaintKind.CurrentColor:
                return Solid(style.Color, opacity);
            case SvgPaintKind.Url:
                if (document.Find(paint.Url!) is { } gradient && Gradient(gradient, document, opacity, matrix, local) is { } fill)
                    return fill;
                return paint.Color.Alpha > 0 ? Solid(paint.Color, opacity) : null;
            default:
                return null;
        }
    }

    private static SolidFill? Solid(SKColor color, float opacity)
    {
        var alpha = (byte)Math.Round(color.Alpha * opacity);
        return alpha == 0 ? null : new SolidFill(color.WithAlpha(alpha));
    }

    /// <summary>A linear or radial gradient; its stops and attributes through xlink:href (8 references at most).</summary>
    private static SlideFill? Gradient(XElement element, SvgDocument document, float opacity, SKMatrix matrix, SKRect local)
    {
        var kind = element.Name.LocalName;
        if (kind is not ("linearGradient" or "radialGradient"))
            return null;
        var chain = new List<XElement> { element };
        for (var at = element; chain.Count < 8 && Href(at) is ['#', .. var id] && document.Find(id) is { } next && !chain.Contains(next); at = next)
            chain.Add(next);
        string? Get(string attribute) => chain.Select(e => Attr(e, attribute)).FirstOrDefault(v => v is not null);
        var stopsOf = chain.FirstOrDefault(e => e.Elements().Any(s => s.Name.LocalName == "stop")) ?? element;
        var stops = new List<GradientStop>();
        var last = 0f;
        foreach (var stop in stopsOf.Elements().Where(s => s.Name.LocalName == "stop").Take(256))
        {
            var css = CssDeclarations.Parse(Attr(stop, "style"));
            var position = Math.Max(last, Math.Clamp(Fraction(Attr(stop, "offset")) ?? 0, 0, 1));
            last = position;
            var color = Color(css.Get("stop-color") ?? Attr(stop, "stop-color") ?? "black") ?? SKColors.Black;
            var alpha = Math.Clamp(Fraction(css.Get("stop-opacity") ?? Attr(stop, "stop-opacity")) ?? 1, 0, 1) * opacity;
            stops.Add(new GradientStop(position, color.WithAlpha((byte)Math.Round(color.Alpha * alpha))));
        }
        if (stops.Count == 0)
            return null;
        if (stops.Count == 1)
            return stops[0].Color.Alpha == 0 ? null : new SolidFill(stops[0].Color);
        var userSpace = Get("gradientUnits") == "userSpaceOnUse";
        if (kind == "linearGradient")
        {
            float Coordinate(string attribute, float fallback) => Fraction(Get(attribute)) ?? fallback;
            var (dx, dy) = (Coordinate("x2", 1) - Coordinate("x1", 0), Coordinate("y2", 0) - Coordinate("y1", 0));
            // The outline is in page coordinates already: the gradient's direction is turned with it. Without a turn
            // or skew a box gradient is the unit square's, stretched with the box (mirrored where the box is).
            var scaled = !userSpace && matrix.SkewX == 0 && matrix.SkewY == 0;
            if (scaled)
            {
                (dx, dy) = (matrix.ScaleX < 0 ? -dx : dx, matrix.ScaleY < 0 ? -dy : dy);
            }
            else
            {
                var vector = userSpace ? matrix.MapVector(dx, dy) : matrix.MapVector(dx * local.Width, dy * local.Height);
                (dx, dy) = (vector.X, vector.Y);
            }
            var angle = dx == 0 && dy == 0 ? 0 : MathF.Atan2(dy, dx) * 180 / MathF.PI;
            return new GradientFill(stops, GradientShape.Linear, float.IsFinite(angle) ? angle : 0, scaled, SKRect.Empty);
        }
        // Radial: an ellipse over the box from its centre, as the renderer draws a rectangle gradient, whose ellipse
        // passes through the box's corners; SVG's radius (50 % of the box) reaches the middle of its sides.
        var radius = userSpace ? 0.5f : Math.Clamp(Fraction(Get("r")) ?? 0.5f, 0.01f, 10);
        var cx = userSpace ? 0.5f : Math.Clamp(Fraction(Get("cx")) ?? 0.5f, 0, 1);
        var cy = userSpace ? 0.5f : Math.Clamp(Fraction(Get("cy")) ?? 0.5f, 0, 1);
        var reach = radius / 0.5f / MathF.Sqrt(2);
        var reaching = stops.Select(s => s with { Position = Math.Clamp(s.Position * reach, 0, 1) }).ToList();
        if (reaching[^1].Position < 1)
            reaching.Add(new GradientStop(1, reaching[^1].Color));
        return new GradientFill(reaching, GradientShape.Rectangle, 0, false, new SKRect(cx, cy, 1 - cx, 1 - cy));
    }

    // ───────────────────────── Text ─────────────────────────

    /// <summary>A run of text with its style.</summary>
    private readonly record struct Run(string Text, SvgStyle Style);

    /// <summary>Text set from one point: a text element's start, or a tspan with its own x or y.</summary>
    private sealed class Chunk(float x, float y, string anchor)
    {
        public float X { get; } = x;
        public float Y { get; } = y;
        public string Anchor { get; } = anchor;

        /// <summary>Lines (textArea: one per tbreak) of runs.</summary>
        public List<List<Run>> Lines { get; } = [[]];
    }

    private void Text(XElement text, SKMatrix matrix, SvgStyle style, float opacity, List<SlideElement> output)
    {
        var x = Numbers(Attr(text, "x")) is [var first, ..] ? first : 0;
        var y = Numbers(Attr(text, "y")) is [var top, ..] ? top : 0;
        var chunks = new List<Chunk> { new(x, y, style.TextAnchor) };
        Gather(text, style, chunks, positioned: true);
        foreach (var chunk in chunks)
        {
            var runs = Trim(chunk.Lines[0]);
            if (runs.Count == 0)
                continue;
            var size = runs[0].Style.FontSize;
            var width = runs.Sum(r => r.Text.Length * r.Style.FontSize * CharWidth);
            var left = chunk.Anchor switch
            {
                "middle" => chunk.X - width / 2,
                "end" => chunk.X - width,
                _ => chunk.X,
            };
            var align = chunk.Anchor switch
            {
                "middle" => HAlign.Center,
                "end" => HAlign.Right,
                _ => HAlign.Left,
            };
            var local = SKRect.Create(left, chunk.Y - size * Ascent, Math.Max(width, 1), size * 1.25f);
            Add(output, TextBox(matrix, local, [runs], align, wrap: false, TextAnchor.Top, opacity));
        }
    }

    private void TextArea(XElement area, SKMatrix matrix, SvgStyle style, float opacity, List<SlideElement> output)
    {
        var x = Length(Attr(area, "x"), 0, style.FontSize) ?? 0;
        var y = Length(Attr(area, "y"), 0, style.FontSize) ?? 0;
        var width = Length(Attr(area, "width"), 0, style.FontSize);
        var height = Length(Attr(area, "height"), 0, style.FontSize);
        var chunks = new List<Chunk> { new(x, y, "start") };
        Gather(area, style, chunks, positioned: false);
        var lines = chunks[0].Lines.Select(Trim).ToList();
        if (lines.All(l => l.Count == 0))
            return;
        var align = style.TextAlign switch
        {
            "center" => HAlign.Center,
            "end" or "right" => HAlign.Right,
            "justify" => HAlign.Justify,
            _ => HAlign.Left,
        };
        var wrap = width is > 0;
        var estimated = lines.Sum(l => (l.Count > 0 ? l.Max(r => r.Style.FontSize) : style.FontSize) * 1.2f);
        var boxHeight = height is > 0 ? height.Value : estimated;
        var anchor = height is > 0
            ? style.DisplayAlign switch
            {
                "center" => TextAnchor.Middle,
                "after" => TextAnchor.Bottom,
                _ => TextAnchor.Top,
            }
            : TextAnchor.Top;
        var boxWidth = wrap ? width!.Value : Math.Max(1, lines.Max(l => l.Sum(r => r.Text.Length * r.Style.FontSize * CharWidth)));
        Add(output, TextBox(matrix, SKRect.Create(x, y, boxWidth, boxHeight), lines, align, wrap, anchor, opacity));
    }

    /// <summary>
    /// The runs of <paramref name="element"/> into <paramref name="chunks"/>: with <paramref name="positioned"/> a
    /// tspan with its own x or y starts a chunk; tbreak (and a line break in preserved white space) starts a line.
    /// </summary>
    private void Gather(XElement element, SvgStyle style, List<Chunk> chunks, bool positioned)
    {
        if (++_visits > MaxVisits)
            throw DocumentLimits.Exceeded($"The drawing has more than {DocumentLimits.Count(MaxVisits)} elements to draw.");
        if (_depth >= MaxDepth)
            throw DocumentLimits.Exceeded($"The drawing nests elements more than {MaxDepth} levels deep.");
        _depth++;
        try
        {
            foreach (var node in element.Nodes())
            {
                switch (node)
                {
                    case XText text when style.Visible:
                        Append(chunks[^1], text.Value, style);
                        break;
                    case XElement child:
                        var name = child.Name.LocalName.ToLowerInvariant();
                        if (name == "tbreak")
                        {
                            chunks[^1].Lines.Add([]);
                            continue;
                        }
                        if (name is not ("tspan" or "a" or "textpath" or "altglyph" or "tref"))
                            continue;
                        if (Attr(child, "id") is { } id && _hidden.Contains(id))
                            continue;
                        var childStyle = style.Apply(child);
                        if (childStyle.Hidden)
                            continue;
                        if (positioned && name == "tspan" && (Attr(child, "x") is not null || Attr(child, "y") is not null))
                        {
                            var current = chunks[^1];
                            var x = Numbers(Attr(child, "x")) is [var nx, ..] ? nx : current.X;
                            var y = Numbers(Attr(child, "y")) is [var ny, ..] ? ny : current.Y;
                            if (Numbers(Attr(child, "dy")) is [var dy, ..])
                                y += dy;
                            chunks.Add(new Chunk(x, y, childStyle.TextAnchor));
                        }
                        Gather(child, childStyle, chunks, positioned);
                        break;
                }
            }
        }
        finally
        {
            _depth--;
        }
    }

    /// <summary>Text added to a chunk's last line, white space collapsed as SVG does unless xml:space is "preserve".</summary>
    private static void Append(Chunk chunk, string text, SvgStyle style)
    {
        if (style.Preserve)
        {
            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                    chunk.Lines.Add([]);
                if (lines[i].Length > 0)
                    chunk.Lines[^1].Add(new Run(lines[i].Replace('\t', ' '), style));
            }
            return;
        }
        var collapsed = Spaces().Replace(text.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal), " ");
        var line = chunk.Lines[^1];
        if (collapsed.StartsWith(' ') && (line.Count == 0 || line[^1].Text.EndsWith(' ')))
            collapsed = collapsed[1..];
        if (collapsed.Length > 0)
            line.Add(new Run(collapsed, style));
    }

    /// <summary>A line without white space at its ends and without empty runs.</summary>
    private static List<Run> Trim(List<Run> line)
    {
        var result = line.Where(r => r.Text.Length > 0).ToList();
        if (result.Count > 0 && !result[0].Style.Preserve)
            result[0] = result[0] with { Text = result[0].Text.TrimStart() };
        if (result.Count > 0 && !result[^1].Style.Preserve)
            result[^1] = result[^1] with { Text = result[^1].Text.TrimEnd() };
        return [.. result.Where(r => r.Text.Length > 0)];
    }

    /// <summary>A text box over <paramref name="local"/> (user units), turned and scaled with <paramref name="matrix"/>.</summary>
    private SlideShape? TextBox(SKMatrix matrix, SKRect local, List<List<Run>> lines, HAlign align, bool wrap, TextAnchor anchor, float opacity)
    {
        if (Place(matrix, local) is not var (bounds, rotation, _))
            return null;
        var scale = Scale(matrix);
        var frame = new SlideTextFrame
        {
            InsetLeft = 0,
            InsetTop = 0,
            InsetRight = 0,
            InsetBottom = 0,
            Wrap = wrap,
            Anchor = anchor,
        };
        foreach (var line in lines)
        {
            _budget.Paragraph();
            var mark = line.Count > 0 ? Format(line[0].Style, scale, opacity) : default;
            var paragraph = new HParagraph { Format = Single with { Align = align }, MarkFormat = mark };
            foreach (var run in line)
            {
                if (Format(run.Style, scale, opacity) is var format && format.Color is not null)
                    paragraph.Inlines.Add(new HText(run.Text, format));
            }
            frame.Paragraphs.Add(paragraph);
        }
        // Lines with nothing drawn at the end make no text; empty lines between keep their place.
        while (frame.Paragraphs.Count > 0 && frame.Paragraphs[^1].Inlines.Count == 0)
            frame.Paragraphs.RemoveAt(frame.Paragraphs.Count - 1);
        if (frame.IsEmpty)
            return null;
        _budget.Object();
        var box = new SKPath();
        box.AddRect(new SKRect(0, 0, bounds.Width, bounds.Height));
        return new SlideShape { Bounds = bounds, Rotation = rotation, Paths = [new SlidePath(box, PathFill.None, Stroke: false)], Text = frame };
    }

    /// <summary>The character format of a run in points; no colour when its fill draws nothing.</summary>
    private static HCharFormat Format(SvgStyle style, float scale, float opacity)
    {
        var color = style.Fill.Kind switch
        {
            SvgPaintKind.Color => style.Fill.Color,
            SvgPaintKind.CurrentColor => style.Color,
            SvgPaintKind.Url => style.Fill.Color.Alpha > 0 ? style.Fill.Color : SKColors.Black,
            _ => SKColors.Empty,
        };
        var visible = style.Visible && color.Alpha > 0 && style.FillOpacity * opacity > 0;
        var size = (int)Math.Clamp(Math.Round(style.FontSize * scale * 100), 100, 400_000);
        return new HCharFormat(
            Bold: style.Bold ? true : null,
            Italic: style.Italic ? true : null,
            Underline: style.Underline ? true : null,
            Strike: style.Strike ? true : null,
            Size: size,
            Font: style.FontFamily,
            Color: visible ? $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}" : null);
    }

    /// <summary>foreignObject: the text of XHTML in it (paragraphs and line breaks, with inline CSS); left out without text.</summary>
    private void ForeignObject(XElement element, SKMatrix matrix, SvgStyle style, float opacity, List<SlideElement> output)
    {
        var x = Length(Attr(element, "x"), 0, style.FontSize) ?? 0;
        var y = Length(Attr(element, "y"), 0, style.FontSize) ?? 0;
        var width = Length(Attr(element, "width"), 0, style.FontSize) ?? 0;
        var height = Length(Attr(element, "height"), 0, style.FontSize) ?? 0;
        var chunk = new Chunk(x, y, "start");
        var align = HAlign.Left;
        Html(element, style, chunk, ref align, block: false);
        var lines = chunk.Lines.Select(Trim).ToList();
        while (lines.Count > 0 && lines[0].Count == 0)
            lines.RemoveAt(0);
        if (lines.Count == 0 || width <= 0)
        {
            Drop("objects");
            return;
        }
        Add(output, TextBox(matrix, SKRect.Create(x, y, width, Math.Max(height, 1)), lines, align, wrap: true, TextAnchor.Top, opacity));
    }

    private void Html(XElement element, SvgStyle style, Chunk chunk, ref HAlign align, bool block)
    {
        if (++_visits > MaxVisits)
            throw DocumentLimits.Exceeded($"The drawing has more than {DocumentLimits.Count(MaxVisits)} elements to draw.");
        if (_depth >= MaxDepth)
            throw DocumentLimits.Exceeded($"The drawing nests elements more than {MaxDepth} levels deep.");
        _depth++;
        try
        {
            foreach (var node in element.Nodes())
            {
                if (node is XText text)
                {
                    Append(chunk, text.Value, style);
                    continue;
                }
                if (node is not XElement child)
                    continue;
                var name = child.Name.LocalName.ToLowerInvariant();
                if (name is "script" or "style" or "head" or "title")
                    continue;
                if (name == "br")
                {
                    chunk.Lines.Add([]);
                    continue;
                }
                var childStyle = style.ApplyHtml(child);
                if (childStyle.Hidden)
                    continue;
                var isBlock = name is "p" or "div" or "li" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "tr" or "blockquote" or "pre";
                if (isBlock && chunk.Lines[^1].Count > 0)
                    chunk.Lines.Add([]);
                if (isBlock && childStyle.TextAlign is { } textAlign && chunk.Lines.Count <= 1)
                {
                    align = textAlign switch
                    {
                        "center" => HAlign.Center,
                        "right" or "end" => HAlign.Right,
                        "justify" => HAlign.Justify,
                        _ => HAlign.Left,
                    };
                }
                Html(child, childStyle, chunk, ref align, isBlock);
                if (isBlock && chunk.Lines[^1].Count > 0)
                    chunk.Lines.Add([]);
            }
        }
        finally
        {
            _depth--;
        }
    }

    // ───────────────────────── Pictures ─────────────────────────

    private void Image(XElement image, SvgDocument document, SKMatrix matrix, SvgStyle style, float opacity, List<SlideElement> output)
    {
        var href = Href(image);
        if (href is null || _files(document.Folder, href) is not { } file)
        {
            Drop("pictures");
            return;
        }
        var x = Length(Attr(image, "x"), 0, style.FontSize) ?? 0;
        var y = Length(Attr(image, "y"), 0, style.FontSize) ?? 0;
        var width = Length(Attr(image, "width"), 0, style.FontSize);
        var height = Length(Attr(image, "height"), 0, style.FontSize);
        var preserve = Attr(image, "preserveAspectRatio");

        if (IsSvg(file.Data))
        {
            SvgPicture(file, x, y, width, height, preserve, matrix, style, opacity, output);
            return;
        }
        if (!SlideImages.CanDraw(file.Data, _limits))
        {
            Drop("pictures");
            return;
        }
        SKSizeI? pixels = null;
        using (var codec = SKCodec.Create(new MemoryStream(file.Data, writable: false)))
        {
            if (codec is not null)
                pixels = new SKSizeI(codec.Info.Width, codec.Info.Height);
        }
        var target = SKRect.Create(x, y, width is > 0 ? width.Value : pixels?.Width ?? 0, height is > 0 ? height.Value : pixels?.Height ?? 0);
        if (target.Width <= 0 || target.Height <= 0)
            return;
        var crop = SKRect.Empty;
        if (pixels is { Width: > 0, Height: > 0 } size)
        {
            var (fit, sliced) = Fit(SKRect.Create(size.Width, size.Height), target, preserve);
            if (sliced.IsEmpty && (preserve ?? "").Trim() != "none")
                target = fit.MapRect(SKRect.Create(size.Width, size.Height));
            crop = sliced;
        }
        if (Place(matrix, target) is not var (bounds, rotation, mirrored))
            return;
        _budget.Object();
        var path = new SKPath();
        path.AddRect(new SKRect(0, 0, bounds.Width, bounds.Height));
        opacity = Math.Clamp(opacity, 0, 1);
        output.Add(new SlideShape
        {
            Bounds = bounds,
            Rotation = rotation,
            FlipVertical = mirrored,
            Paths = [new SlidePath(path)],
            IsPicture = true,
            Fill = new PictureFill(new SlideImage(file.Data), crop, SKRect.Empty, Effects: opacity < 1 ? new PictureEffects(Alpha: opacity) : null),
        });
    }

    /// <summary>An SVG picture: its drawing placed in the image's box, read as more of this drawing.</summary>
    private void SvgPicture(SvgFile file, float x, float y, float? width, float? height, string? preserve, SKMatrix matrix, SvgStyle style,
        float opacity, List<SlideElement> output)
    {
        if (_openPictures.Count >= MaxPictureNesting || _openPictures.Contains(file.Name))
            return;
        if (!_pictures.TryGetValue(file.Name, out var picture))
        {
            try
            {
                var bytes = SvgRenderer.IsGzip(file.Data) ? Gunzip(file.Data) : file.Data;
                var root = LoadXml(bytes);
                picture = root.Name.LocalName == "svg" ? new SvgDocument(root, Folder(file.Name)) : null;
            }
            catch (XmlException)
            {
                picture = null;
            }
            _pictures[file.Name] = picture;
        }
        if (picture is null)
        {
            Drop("pictures");
            return;
        }
        var root2 = picture.Root;
        var viewBox = ViewBox(root2);
        var ownWidth = Length(Attr(root2, "width"), 0, 16) is { } w and > 0 ? w : viewBox?.Width ?? 0;
        var ownHeight = Length(Attr(root2, "height"), 0, 16) is { } h and > 0 ? h : viewBox?.Height ?? 0;
        if (ownWidth <= 0 || ownHeight <= 0)
            return;
        var target = SKRect.Create(x, y, width is > 0 ? width.Value : ownWidth, height is > 0 ? height.Value : ownHeight);
        // The picture's own viewport (its size, its viewBox mapped onto it), then that fitted into the image's box.
        var own = SKMatrix.Identity;
        if (viewBox is { } box)
            own = Fit(box, SKRect.Create(ownWidth, ownHeight), Attr(root2, "preserveAspectRatio")).Matrix;
        var placed = matrix.PreConcat(Fit(SKRect.Create(ownWidth, ownHeight), target, preserve).Matrix).PreConcat(own);
        _openPictures.Add(file.Name);
        try
        {
            Group(root2.Elements(), picture, placed, SvgStyle.Initial.Apply(root2), opacity * style.Opacity, output, group: true);
        }
        finally
        {
            _openPictures.Remove(file.Name);
        }
    }

    private byte[] Gunzip(byte[] data)
    {
        using var gzip = new GZipStream(new MemoryStream(data, writable: false), CompressionMode.Decompress);
        using var limited = new LimitedStream(gzip, _limits.MaxPartBytes,
            () => DocumentLimits.TooLarge($"An SVG picture unpacks to more than {DocumentLimits.Megabytes(_limits.MaxPartBytes)}."));
        using var copy = new MemoryStream();
        try
        {
            limited.CopyTo(copy);
        }
        catch (InvalidDataException ex)
        {
            throw new XmlException("The SVG picture can't be unpacked.", ex);
        }
        return copy.ToArray();
    }

    /// <summary>SVG text (after a byte order mark and white space) or gzip.</summary>
    private static bool IsSvg(byte[] data)
    {
        if (SvgRenderer.IsGzip(data))
            return true;
        var span = data.AsSpan();
        if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            span = span[3..];
        span = span.TrimStart(" \t\r\n"u8);
        return span.StartsWith("<"u8);
    }

    /// <summary>The folder of a package part name ("" at the top).</summary>
    internal static string Folder(string name) => name.LastIndexOf('/') is var slash and >= 0 ? name[..slash] : "";

    // ───────────────────────── Geometry ─────────────────────────

    /// <summary>
    /// A box in user units mapped by <paramref name="matrix"/>, as the slide model places it: its box in points
    /// (before turning), its turn in degrees clockwise and whether it is mirrored; null when it can't be placed.
    /// </summary>
    private static (SKRect Bounds, float Rotation, bool Mirrored)? Place(SKMatrix matrix, SKRect local)
    {
        var ex = matrix.MapVector(1, 0);
        var ey = matrix.MapVector(0, 1);
        var center = matrix.MapPoint(local.MidX, local.MidY);
        var (sx, sy) = (ex.Length, ey.Length);
        if (!float.IsFinite(sx) || !float.IsFinite(sy) || !float.IsFinite(center.X) || !float.IsFinite(center.Y) || sx <= 0 || sy <= 0)
            return null;
        var w = Math.Clamp(local.Width * sx, 0, MaxLength);
        var h = Math.Clamp(local.Height * sy, 0, MaxLength);
        var rotation = MathF.Atan2(ex.Y, ex.X) * 180 / MathF.PI;
        if (rotation < 0)
            rotation += 360;
        if (Math.Abs(rotation) < 1e-3 || Math.Abs(rotation - 360) < 1e-3)
            rotation = 0;
        var mirrored = ex.X * ey.Y - ex.Y * ey.X < 0;
        return (SKRect.Create(center.X - w / 2, center.Y - h / 2, w, h), rotation, mirrored);
    }

    private static bool IsFinite(SKMatrix m) =>
        float.IsFinite(m.ScaleX) && float.IsFinite(m.ScaleY) && float.IsFinite(m.SkewX) && float.IsFinite(m.SkewY)
        && float.IsFinite(m.TransX) && float.IsFinite(m.TransY);

    private static bool IsFinite(SKRect r) =>
        float.IsFinite(r.Left) && float.IsFinite(r.Top) && float.IsFinite(r.Right) && float.IsFinite(r.Bottom)
        && Math.Abs(r.Left) < MaxLength && Math.Abs(r.Top) < MaxLength && r.Width < MaxLength && r.Height < MaxLength;

    /// <summary>An SVG transform list (matrix, translate, scale, rotate with its centre, skewX, skewY); identity when missing.</summary>
    internal static SKMatrix Transform(string? value)
    {
        var result = SKMatrix.Identity;
        if (string.IsNullOrWhiteSpace(value))
            return result;
        var count = 0;
        foreach (Match operation in TransformOperation().Matches(value))
        {
            if (++count > 64)
                break;
            var n = Numbers(operation.Groups["args"].Value);
            float N(int i, float fallback = 0) => i < n.Count ? n[i] : fallback;
            SKMatrix? step = operation.Groups["name"].Value switch
            {
                "matrix" when n.Count == 6 => new SKMatrix(n[0], n[2], n[4], n[1], n[3], n[5], 0, 0, 1),
                "translate" => SKMatrix.CreateTranslation(N(0), N(1)),
                "scale" => SKMatrix.CreateScale(N(0, 1), N(1, N(0, 1))),
                "rotate" => SKMatrix.CreateRotationDegrees(N(0), N(1), N(2)),
                "skewX" => SKMatrix.CreateSkew(MathF.Tan(N(0) * MathF.PI / 180), 0),
                "skewY" => SKMatrix.CreateSkew(0, MathF.Tan(N(0) * MathF.PI / 180)),
                _ => null,
            };
            if (step is { } s)
                result = result.PreConcat(s);
        }
        return result;
    }

    [GeneratedRegex(@"(?<name>[A-Za-z]+)\s*\((?<args>[^)]*)\)")]
    private static partial Regex TransformOperation();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")]
    private static partial Regex Number();

    /// <summary>The numbers in a list ("1, 2 3e1-4"), clamped and finite; at most 4 million.</summary>
    internal static List<float> Numbers(string? value)
    {
        var result = new List<float>();
        if (string.IsNullOrEmpty(value))
            return result;
        foreach (Match match in Number().Matches(value))
        {
            if (result.Count >= 4_000_000)
                break;
            if (float.TryParse(match.ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && float.IsFinite(number))
                result.Add(Math.Clamp(number, -MaxLength, MaxLength));
        }
        return result;
    }

    /// <summary>A length in pixels (px, pt, pc, mm, cm, in, em, ex; % of <paramref name="reference"/>); null when missing or unreadable.</summary>
    internal static float? Length(string? value, float reference, float fontSize)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = value.Trim();
        var unit = text.Length;
        while (unit > 0 && (char.IsAsciiLetter(text[unit - 1]) || text[unit - 1] == '%'))
            unit--;
        if (!float.TryParse(text.AsSpan(0, unit), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !float.IsFinite(number))
            return null;
        var factor = text[unit..].ToLowerInvariant() switch
        {
            "" or "px" => 1f,
            "pt" => 96f / 72,
            "pc" => 16f,
            "mm" => 96f / 25.4f,
            "cm" => 96f / 2.54f,
            "in" => 96f,
            "em" => fontSize,
            "ex" => fontSize / 2,
            "%" => reference / 100,
            _ => float.NaN,
        };
        var result = number * factor;
        return float.IsFinite(result) ? Math.Clamp(result, -MaxLength, MaxLength) : null;
    }

    /// <summary>A number or a percentage as a fraction (0.5, "50%").</summary>
    internal static float? Fraction(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = value.Trim();
        var percent = text.EndsWith('%');
        if (!float.TryParse(percent ? text[..^1] : text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !float.IsFinite(number))
            return null;
        return Math.Clamp(percent ? number / 100 : number, -MaxLength, MaxLength);
    }

    /// <summary>An attribute without a namespace, also when written in lower case (viewbox, preserveaspectratio).</summary>
    internal static string? Attr(XElement element, string name)
    {
        if (element.Attribute(name) is { } exact)
            return exact.Value;
        foreach (var attribute in element.Attributes())
        {
            if (attribute.Name.NamespaceName.Length == 0 && attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
                return attribute.Value;
        }
        return null;
    }

    /// <summary>xlink:href, or SVG 2's plain href.</summary>
    internal static string? Href(XElement element) =>
        ((string?)element.Attribute(XLink + "href") ?? Attr(element, "href"))?.Trim();

    // ───────────────────────── Colours ─────────────────────────

    /// <summary>A CSS colour: #rgb, #rgba, #rrggbb, #rrggbbaa, rgb() / rgba(), a colour name, transparent; null otherwise.</summary>
    internal static SKColor? Color(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = value.Trim();
        if (text.Equals("transparent", StringComparison.OrdinalIgnoreCase))
            return SKColors.Transparent;
        if (text.StartsWith('#'))
        {
            var hex = text[1..];
            if (hex.Length is not (3 or 4 or 6 or 8) || !hex.All(char.IsAsciiHexDigit))
                return null;
            var step = hex.Length <= 4 ? 1 : 2;
            var channels = new byte[hex.Length / step];
            for (var i = 0; i < channels.Length; i++)
            {
                var digits = hex.Substring(i * step, step);
                channels[i] = Convert.ToByte(step == 1 ? digits + digits : digits, 16);
            }
            return new SKColor(channels[0], channels[1], channels[2], channels.Length == 4 ? channels[3] : (byte)255);
        }
        if (text.StartsWith("rgb", StringComparison.OrdinalIgnoreCase) && text.IndexOf('(') is var open and > 0 && text.IndexOf(')') is var close && close > open)
        {
            var parts = text[(open + 1)..close].Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                return null;
            var channels = new byte[3];
            for (var i = 0; i < 3; i++)
            {
                if (Fraction(parts[i]) is not { } number)
                    return null;
                channels[i] = (byte)Math.Clamp(Math.Round(parts[i].EndsWith('%') ? number * 255 : number), 0, 255);
            }
            var alpha = parts.Length >= 4 ? Math.Clamp(Fraction(parts[3]) ?? 1, 0, 1) : 1;
            return new SKColor(channels[0], channels[1], channels[2], (byte)Math.Round(alpha * 255));
        }
        if (text.All(char.IsAsciiLetter))
        {
            var named = System.Drawing.Color.FromName(text);
            if (named.IsKnownColor && !named.IsSystemColor)
                return new SKColor(named.R, named.G, named.B, named.A);
        }
        return null;
    }
}
