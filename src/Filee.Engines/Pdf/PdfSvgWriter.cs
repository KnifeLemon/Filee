// PDF pages as SVG drawings (PDF → SVG, PdfSvgConverter), written from what Filee's own PDF interpreter records of a
// page (PdfSlideDrawing, as for PDF → PPTX: the paint order, the clipping paths, transparency and soft masks):
//
// - Paths keep their outline (curves stay curves), their fill rule, and the stroke as it is drawn under the
//   transformation: width, dashes, caps, joins and miter limit; a line width of 0 is one device pixel
//   (non-scaling-stroke). Colours are sRGB as PdfPig turns them (CMYK, ICC, spot colours); the constant alpha (ca, CA)
//   is the fill and stroke opacity.
// - Clipping paths are clipPath elements (the recorder has intersected them already); what is painted through the
//   same one shares a group.
// - Axial and radial shadings (sh, and shading patterns of fills and strokes) are linear and radial gradients: the
//   colours sampled at 65 points, fewer stops where they run straight, and a transparent stop at an end that doesn't
//   extend, so nothing is painted past it. Radial shadings SVG 1.1 can't express (two circles of their own with
//   different centres, a focus outside the end circle) are drawn into a picture of what they paint, as Filee's renderer
//   draws them; function-based and mesh shadings are the pictures PdfShadingPictures drew of them, tiling patterns
//   picture patterns of one cell.
// - Soft masks: a constant alpha is the group's opacity, a luminosity shading a mask of a grey gradient, a drawn group
//   a mask of its alpha (a picture). Blend modes are CSS mix-blend-mode.
// - Pictures are data: URIs, each once (drawn again, it is a use of it): a plain JPEG (DCTDecode alone, grey or RGB,
//   no mask, no Decode array) as the JPEG itself, the others as PNG with their alpha (soft masks, stencil masks in
//   their colour, JBIG2 and JPEG 2000 through Filee's own decoders).
// - Text is drawn as glyph outlines: the PDF's own font programs (each glyph once, used where it is shown), an
//   installed font of the same kind for the standard 14 fonts PDFs don't embed, Type 3 glyphs from their procedures.
//   That looks the same in every SVG viewer. SVG text with the font embedded as @font-face would not: the fonts in
//   PDFs are subsets whose codes aren't Unicode, browsers load no Type 1 or bare CFF programs, and many viewers and
//   editors (Inkscape, rsvg, Skia's SVG readers) ignore @font-face and set the text in another font, wider or
//   narrower than the PDF's. The text itself lies on top as transparent SVG text, a line at a time, each character
//   where the PDF puts it (an OCR text layer too), so it can be selected, copied and searched.
//
// Not drawn, as for PDF → PPTX: the appearance of annotations and form fields, text clipping modes, text in pattern
// cells. Limits: the recorder's (PdfSlideDrawingProcessor), PdfRasterCache's glyph outlines and Type 3 paints, at most
// MaxPicturePixels of pictures decoded a page and DocumentLimits.MaxPdfStreamBytes of SVG a page, DocumentLimits.MaxPages
// pages; a PDF past these is refused with error.document_too_large.

using System.Globalization;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using Filee.Engines.Documents;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Geometry;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Graphics.Core;
using UglyToad.PdfPig.Graphics.Operations;
using UglyToad.PdfPig.Outline.Destinations;
using UglyToad.PdfPig.Parser;
using UglyToad.PdfPig.Tokenization.Scanner;
using UglyToad.PdfPig.Tokens;

namespace Filee.Engines.Pdf;

/// <summary>A page's paints and its size in points (as shown: the visible crop box, turned).</summary>
internal sealed class PdfSvgPage(PdfSlideDrawing drawing, float width, float height) : IDisposable
{
    public PdfSlideDrawing Drawing { get; } = drawing;

    public float Width { get; } = width;

    public float Height { get; } = height;

    public void Dispose() => Drawing.Dispose();
}

/// <summary>Makes a <see cref="PdfSvgPage"/> of a page (registered with PdfDocument.AddPageFactory).</summary>
internal sealed class PdfSvgPageFactory(IPdfTokenScanner pdfScanner, IResourceStore resourceStore, ILookupFilterProvider filterProvider,
    IPageContentParser pageContentParser, ParsingOptions parsingOptions)
    : BasePageFactory<PdfSvgPage>(pdfScanner, resourceStore, filterProvider, pageContentParser, parsingOptions)
{
    protected override PdfSvgPage ProcessPage(int pageNumber, DictionaryToken dictionary, NamedDestinations namedDestinations, MediaBox mediaBox,
        CropBox cropBox, UserSpaceUnit userSpaceUnit, PageRotationDegrees rotation, TransformationMatrix initialMatrix,
        IReadOnlyList<IGraphicsStateOperation> operations)
    {
        // As PdfSlideDrawingFactory: the page as it is shown, y flipped at the height of its visible part.
        var visible = cropBox.GetVisibleBounds(rotation);
        var (width, height) = ((float)visible.Width, (float)visible.Height);
        var toSlide = new SKMatrix(1, 0, 0, 0, -1, height, 0, 0, 1);
        var processor = new PdfSlideDrawingProcessor(pageNumber, ResourceStore, PdfScanner, PageContentParser, FilterProvider, cropBox, userSpaceUnit,
            rotation, initialMatrix, ParsingOptions, toSlide, 0, new PdfDrawingBudget(), new SKRect(0, 0, width, height));
        return new PdfSvgPage(processor.Process(pageNumber, operations), width, height);
    }
}

/// <summary>Writes PDF pages as SVG files (see the top).</summary>
internal static class PdfSvgWriter
{
    /// <summary>Pixels of pictures one page decodes at most (16 pictures of PdfDocumentReader.MaxGraphicsPixels).</summary>
    internal const long MaxPicturePixels = 16 * (long)PdfDocumentReader.MaxGraphicsPixels;

    /// <summary>
    /// Writes the pages <paramref name="pagesOf"/> picks (zero-based, from the page count) of the PDF at
    /// <paramref name="path"/>, each to the file <paramref name="outputOf"/> names for it (page index, pages written);
    /// a page it names no file for (null) is skipped. Returns the files written.
    /// </summary>
    /// <exception cref="InvalidOperationException">The PDF is password-protected.</exception>
    public static List<string> Write(string path, Func<int, IReadOnlyList<int>> pagesOf, Func<int, int, string?> outputOf,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default, DocumentLimits? limits = null)
    {
        limits ??= DocumentLimits.Default;
        PdfDocument pdf;
        try
        {
            // Without PdfPig's clipping of paths (as ReadSlides): the recorder clips, and keeps curves curves.
            pdf = PdfDocument.Open(path, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        }
        catch (PdfDocumentEncryptedException)
        {
            throw PdfDocumentReader.PasswordProtected();
        }

        var written = new List<string>();
        using (pdf)
        {
            pdf.AddPageFactory<PdfSvgPage, PdfSvgPageFactory>();
            var pages = pagesOf(pdf.NumberOfPages);
            if (pages.Count > limits.MaxPages)
                throw DocumentLimits.TooLarge($"The PDF has more than {DocumentLimits.Count(limits.MaxPages)} pages to write as SVG.");
            for (var i = 0; i < pages.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (outputOf(pages[i], pages.Count) is not { } output)
                    continue;
                Page text;
                PdfSvgPage page;
                try
                {
                    text = pdf.GetPage(pages[i] + 1);
                    page = pdf.GetPage<PdfSvgPage>(pages[i] + 1);
                }
                catch (PdfDocumentEncryptedException)
                {
                    throw PdfDocumentReader.PasswordProtected();
                }
                using (page)
                {
                    try
                    {
                        using var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16);
                        new PdfSvgPageWriter(writer, page, limits.MaxPdfStreamBytes, cancellationToken).Write(text.Letters);
                    }
                    catch
                    {
                        TryDelete(output);
                        throw;
                    }
                }
                written.Add(output);
                progress?.Report((i + 1.0) / pages.Count);
            }
        }
        return written;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The work folder goes with the job anyway.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>One page's SVG (see the top of the file).</summary>
internal sealed class PdfSvgPageWriter
{
    /// <summary>Samples of a shading's colours for a gradient, before the stops in straight runs are left out.</summary>
    private const int GradientSamples = 65;

    /// <summary>How far (of 255) a colour may lie from the straight run between two stops and still be left out.</summary>
    private const int StopTolerance = 2;

    /// <summary>Pixels per point of a paint drawn into a picture, and the pixels of one such picture at most.</summary>
    private const float RasterScale = 3;

    private const long MaxRasterPixels = 4_000_000;

    /// <summary>Pixels per point of a luminosity mask drawn into a picture, and its largest side.</summary>
    private const float MaskScale = 2;

    private const int MaxMaskSide = 2048;

    /// <summary>Letters of the page's text written as SVG text at most.</summary>
    internal const int MaxTextLetters = PdfSlideDrawingProcessor.MaxGlyphs;

    private static readonly SKMatrix FlipToUnit = new(1, 0, 0, 0, -1, 1, 0, 0, 1);

    private readonly TextWriter _writer;
    private readonly PdfSvgPage _page;
    private readonly long _maxChars;
    private readonly CancellationToken _cancellationToken;
    private readonly PdfRasterCache _cache = new();
    private readonly SKRect _area;

    /// <summary>Where masks reach: the page and as much again all round (paints may lie past its edges).</summary>
    private readonly SKRect _region;

    private long _chars;
    private long _pixels;
    private int _ids;

    private readonly Dictionary<PdfClip, string> _clips = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PdfMask, string?> _masks = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(PdfShadingFill Fill, SKMatrix Space, bool Grey), string?> _gradients = [];
    private readonly Dictionary<(object Source, SKMatrix Space), string> _patterns = [];
    private readonly Dictionary<object, string> _pictures = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(SKImage Image, bool Luminosity), string> _recoloured = [];
    private readonly Dictionary<object, (string Id, int Width, int Height)?> _images = [];
    private readonly Dictionary<SKPath, string> _glyphs = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(PdfType3Glyph Glyph, SKColor Ink, bool Inked), string?> _type3 = [];
    private readonly Dictionary<ReadOnlyMemory<byte>, UInt128> _hashes = new(new MemoryKeys());

    public PdfSvgPageWriter(TextWriter writer, PdfSvgPage page, long maxBytes, CancellationToken cancellationToken)
    {
        _writer = writer;
        _page = page;
        _maxChars = maxBytes;
        _cancellationToken = cancellationToken;
        _area = new SKRect(0, 0, page.Width, page.Height);
        var reach = Math.Max(page.Width, page.Height);
        _region = SKRect.Inflate(_area, reach, reach);
    }

    /// <summary>Writes the page, <paramref name="letters"/> (PdfPig's) as its text.</summary>
    public void Write(IReadOnlyList<Letter> letters)
    {
        try
        {
            Raw("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            Raw($"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" version=\"1.1\" width=\"{N(_page.Width)}pt\" " +
                $"height=\"{N(_page.Height)}pt\" viewBox=\"0 0 {N(_page.Width)} {N(_page.Height)}\">\n");
            var drawing = _page.Drawing;
            Paints(Merged(drawing.Paints, drawing.Letters), null, top: true);
            Text(letters);
            Raw("</svg>\n");
        }
        finally
        {
            _cache.Dispose();
        }
    }

    /// <summary>The page's paints and its glyphs (recorded apart) in the order the page paints them.</summary>
    private static IEnumerable<PdfPaint> Merged(List<PdfPaint> paints, List<PdfPaintedGlyph> glyphs)
    {
        var (p, g) = (0, 0);
        while (p < paints.Count || g < glyphs.Count)
        {
            if (g >= glyphs.Count || (p < paints.Count && paints[p].Order <= glyphs[g].Order))
                yield return paints[p++];
            else
                yield return glyphs[g++];
        }
    }

    // ───────────────────────── Paints ─────────────────────────

    /// <summary>
    /// <paramref name="paints"/> in their order, each through its clipping path (consecutive paints with the same one
    /// in one group); <paramref name="ink"/> paints all of them (a "d1" Type 3 glyph). <paramref name="top"/>: the
    /// page's own paints (pictures of paints are cut to the page), not those of a glyph procedure (in text space).
    /// </summary>
    private void Paints(IEnumerable<PdfPaint> paints, PdfPaintColor? ink, bool top)
    {
        PdfClip? clip = null;
        foreach (var paint in paints)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(paint.Clip, clip))
            {
                if (clip is not null)
                    Raw("</g>\n");
                clip = paint.Clip;
                if (clip is not null)
                    Raw($"<g clip-path=\"url(#{Clip(clip)})\">\n");
            }
            var element = paint switch
            {
                PdfPaintedPath path => PathElement(path, ink, top),
                PdfPaintedImage image => ImageElement(ink is not null && image.IsMask ? image with { MaskColor = ink.Color } : image),
                PdfPaintedShading shading => ShadingElement(shading, top),
                PdfPaintedGlyph glyph => GlyphElement(ink is not null ? glyph with { Color = ink } : glyph, top),
                _ => null,
            };
            if (element is null)
                continue;
            var wrapper = Wrapper(paint);
            if (wrapper.Length > 0)
                Raw($"<g{wrapper}>{element}</g>\n");
            else
                Raw(element + "\n");
        }
        if (clip is not null)
            Raw("</g>\n");
    }

    /// <summary>The attributes of the group a paint is drawn in for its soft mask and blend mode; empty for none.</summary>
    private string Wrapper(PdfPaint paint)
    {
        var attributes = new StringBuilder();
        if (paint.Mask is { } mask)
        {
            if (!mask.IsConstant && Mask(mask) is { } id)
                attributes.Append($" mask=\"url(#{id})\"");
            else if (mask.IsConstant && mask.Alpha < 1)
                attributes.Append($" opacity=\"{N(Math.Clamp(mask.Alpha, 0, 1))}\"");
        }
        if (BlendName(paint.Blend) is { } blend)
            attributes.Append($" style=\"mix-blend-mode:{blend}\"");
        return attributes.ToString();
    }

    private static string? BlendName(SKBlendMode mode) => mode switch
    {
        SKBlendMode.Multiply => "multiply",
        SKBlendMode.Screen => "screen",
        SKBlendMode.Overlay => "overlay",
        SKBlendMode.Darken => "darken",
        SKBlendMode.Lighten => "lighten",
        SKBlendMode.ColorDodge => "color-dodge",
        SKBlendMode.ColorBurn => "color-burn",
        SKBlendMode.HardLight => "hard-light",
        SKBlendMode.SoftLight => "soft-light",
        SKBlendMode.Difference => "difference",
        SKBlendMode.Exclusion => "exclusion",
        SKBlendMode.Hue => "hue",
        SKBlendMode.Saturation => "saturation",
        SKBlendMode.Color => "color",
        SKBlendMode.Luminosity => "luminosity",
        _ => null,
    };

    private string? PathElement(PdfPaintedPath path, PdfPaintColor? ink, bool top)
    {
        var fill = path.Fill is { } f ? ink ?? f : null;
        var stroke = path.Stroke is { } s ? ink ?? s : null;
        var fillServer = fill is null ? null : Server(fill, SKMatrix.Identity);
        var strokeServer = stroke is null ? null : Server(stroke, SKMatrix.Identity);
        if ((fill is not null && fillServer is null) || (stroke is not null && strokeServer is null))
        {
            var reach = stroke is null ? 0 : path.LineWidth * Math.Max(path.Miter, 1) / 2 + 1;
            return Raster(path, SKRect.Inflate(path.Outline.Bounds, reach, reach), top);
        }
        var element = new StringBuilder("<path d=\"");
        AppendPath(element, path.Outline, 1, "0.###");
        element.Append('"');
        if (fillServer is { } fs)
        {
            if (fs.Value != "#000000")
                element.Append($" fill=\"{fs.Value}\"");
            if (fs.Opacity < 1)
                element.Append($" fill-opacity=\"{N(fs.Opacity)}\"");
            if (path.Outline.FillType == SKPathFillType.EvenOdd)
                element.Append(" fill-rule=\"evenodd\"");
        }
        else
        {
            element.Append(" fill=\"none\"");
        }
        if (strokeServer is { } ss)
        {
            element.Append($" stroke=\"{ss.Value}\"");
            if (ss.Opacity < 1)
                element.Append($" stroke-opacity=\"{N(ss.Opacity)}\"");
            // A width of 0 is the thinnest line the device can show: a pixel wherever the drawing is shown.
            element.Append(path.LineWidth > 0 ? $" stroke-width=\"{N(path.LineWidth)}\"" : " stroke-width=\"1\" vector-effect=\"non-scaling-stroke\"");
            if (path.Cap != SKStrokeCap.Butt)
                element.Append(path.Cap == SKStrokeCap.Round ? " stroke-linecap=\"round\"" : " stroke-linecap=\"square\"");
            if (path.Join != SKStrokeJoin.Miter)
                element.Append(path.Join == SKStrokeJoin.Round ? " stroke-linejoin=\"round\"" : " stroke-linejoin=\"bevel\"");
            else if (Math.Abs(path.Miter - 4) > 1e-3)
                element.Append($" stroke-miterlimit=\"{N(path.Miter)}\"");
            if (path.Dash is { Length: > 0 } dash)
            {
                element.Append(" stroke-dasharray=\"").AppendJoin(' ', dash.Select(d => N(d))).Append('"');
                if (path.DashPhase != 0)
                    element.Append($" stroke-dashoffset=\"{N(path.DashPhase)}\"");
            }
        }
        element.Append("/>");
        return element.ToString();
    }

    private string? ImageElement(PdfPaintedImage image)
    {
        if (Picture(image) is not { } picture)
            return null;
        // Pixels (y down) → the unit square (y up, top edge at 1) → the page.
        var matrix = SKMatrix.CreateScale(1f / picture.Width, 1f / picture.Height).PostConcat(FlipToUnit).PostConcat(image.Matrix);
        var alpha = image.IsMask ? 1 : Math.Clamp(image.Alpha, 0, 1);
        return $"<use xlink:href=\"#{picture.Id}\" transform=\"{Matrix(matrix)}\"{(alpha < 1 ? $" opacity=\"{N(alpha)}\"" : "")}/>";
    }

    private string? ShadingElement(PdfPaintedShading shading, bool top)
    {
        var fill = shading.Fill;
        var alpha = Math.Clamp(shading.Alpha, 0, 1);
        var opacity = alpha < 1 ? $" opacity=\"{N(alpha)}\"" : "";
        if (fill.Picture is { } picture)
            return $"<use xlink:href=\"#{PictureId(picture.Image)}\" transform=\"{Matrix(picture.Space)}\"{opacity}/>";

        // What "sh" paints: the shading's own box when it has one, else all of the clipping area.
        using var area = new SKPath();
        if (fill.Shading.BBox is { } box)
        {
            area.AddPoly([.. new[] { box.BottomLeft, box.BottomRight, box.TopRight, box.TopLeft }.Select(p => new SKPoint((float)p.X, (float)p.Y))]);
            area.Transform(fill.Space);
        }
        else
        {
            area.AddRect(shading.Clip?.Bounds ?? (top ? _area : _region));
        }
        if (Gradient(fill, SKMatrix.Identity, grey: false) is not { } gradient)
            return Raster(shading, area.Bounds, top);
        var element = new StringBuilder("<path d=\"");
        AppendPath(element, area, 1, "0.###");
        element.Append($"\" fill=\"url(#{gradient})\"{opacity}/>");
        return element.ToString();
    }

    private string? GlyphElement(PdfPaintedGlyph glyph, bool top)
    {
        if (glyph.Procedure is { } procedure && Type3(procedure, procedure.Inked ? glyph.Color : null) is { } drawn)
            return $"<use xlink:href=\"#{drawn}\" transform=\"{Matrix(glyph.Matrix)}\"/>";
        if (_cache.Outline(glyph) is not { } outline)
            return null;
        var id = GlyphId(outline);
        // The outline is written at 1000 units an em.
        var matrix = SKMatrix.CreateScale(0.001f, 0.001f).PostConcat(glyph.Matrix);
        if (Server(glyph.Color, matrix) is not { } server)
            return Raster(glyph, glyph.Area, top);
        var element = new StringBuilder($"<use xlink:href=\"#{id}\" transform=\"{Matrix(matrix)}\"");
        if (glyph.Stroked)
        {
            // The line width is in page points; the glyph's space is 1000 units an em.
            var m = glyph.Matrix;
            var scale = Math.Sqrt(Math.Abs(m.ScaleX * m.ScaleY - m.SkewX * m.SkewY));
            var width = Math.Max(glyph.LineWidth, 0.25) / Math.Max(scale, 1e-3) * 1000;
            element.Append($" fill=\"none\" stroke=\"{server.Value}\" stroke-width=\"{N(width)}\"");
            if (server.Opacity < 1)
                element.Append($" stroke-opacity=\"{N(server.Opacity)}\"");
        }
        else
        {
            if (server.Value != "#000000")
                element.Append($" fill=\"{server.Value}\"");
            if (server.Opacity < 1)
                element.Append($" fill-opacity=\"{N(server.Opacity)}\"");
        }
        element.Append("/>");
        return element.ToString();
    }

    /// <summary>
    /// A paint drawn into a picture by Filee's renderer (PdfSlideRaster) over <paramref name="bounds"/>, for what SVG
    /// can't express (some radial shadings); its clipping path, mask and blend stay SVG's. Null when it covers nothing.
    /// </summary>
    private string? Raster(PdfPaint paint, SKRect bounds, bool top)
    {
        if (paint.Clip is { } clip)
            bounds = SKRect.Intersect(bounds, clip.Bounds);
        bounds = SKRect.Intersect(bounds, top ? _area : _region);
        if (bounds.Width <= 0 || bounds.Height <= 0 || !float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height))
            return null;
        // Pixels enough for a page at three times 72 dpi, and for a glyph procedure's em box at least 256 a side.
        var scale = Math.Max(RasterScale, 256 / Math.Max(bounds.Width, bounds.Height));
        scale = Math.Min(scale, (float)Math.Sqrt(MaxRasterPixels / ((double)bounds.Width * bounds.Height)));
        var width = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(scale);
            canvas.Translate(-bounds.Left, -bounds.Top);
            PdfSlideRaster.Draw(canvas, [paint with { Clip = null, Mask = null, Blend = SKBlendMode.SrcOver }], cancellationToken: _cancellationToken,
                pictures: true, cache: _cache);
        }
        var id = NewId("r");
        Count(width, height);
        Raw($"<defs><image id=\"{id}\" width=\"{width}\" height=\"{height}\" xlink:href=\"data:image/png;base64,");
        Base64(Png(bitmap));
        Raw("\"/></defs>\n");
        var matrix = SKMatrix.CreateScale(1 / scale, 1 / scale).PostConcat(SKMatrix.CreateTranslation(bounds.Left, bounds.Top));
        return $"<use xlink:href=\"#{id}\" transform=\"{Matrix(matrix)}\"/>";
    }

    // ───────────────────────── Definitions ─────────────────────────

    private string NewId(string prefix) => prefix + (++_ids).ToString(CultureInfo.InvariantCulture);

    private string Clip(PdfClip clip)
    {
        if (_clips.TryGetValue(clip, out var known))
            return known;
        var id = NewId("c");
        var path = new StringBuilder();
        AppendPath(path, clip.Path, 1, "0.###");
        var rule = clip.Path.FillType == SKPathFillType.EvenOdd ? " clip-rule=\"evenodd\"" : "";
        Raw($"<defs><clipPath id=\"{id}\" clipPathUnits=\"userSpaceOnUse\"><path d=\"{path}\"{rule}/></clipPath></defs>\n");
        return _clips[clip] = id;
    }

    /// <summary>A glyph outline (text space, an em of 1, y up) as a path of 1000 units an em.</summary>
    private string GlyphId(SKPath outline)
    {
        if (_glyphs.TryGetValue(outline, out var known))
            return known;
        var id = NewId("g");
        var path = new StringBuilder();
        AppendPath(path, outline, 1000, "0.#");
        var rule = outline.FillType == SKPathFillType.EvenOdd ? " fill-rule=\"evenodd\"" : "";
        Raw($"<defs><path id=\"{id}\" d=\"{path}\"{rule}/></defs>\n");
        return _glyphs[outline] = id;
    }

    /// <summary>
    /// A Type 3 glyph's procedure drawn once (in text space) for each colour it is inked in; null past PdfRasterCache's
    /// budget of procedure paints (the glyph is then drawn from its text, as Filee's renderer does).
    /// </summary>
    private string? Type3(PdfType3Glyph procedure, PdfPaintColor? ink)
    {
        var key = (procedure, ink?.Color ?? SKColors.Empty, ink is not null);
        if (_type3.TryGetValue(key, out var known))
            return known;
        if (!_cache.DrawsProcedure(procedure))
            return _type3[key] = null;
        var id = NewId("t");
        // Its definitions go inside it: the group is in text space.
        Raw($"<defs><g id=\"{id}\">\n");
        Paints(procedure.Paints, ink, top: false);
        Raw("</g></defs>\n");
        return _type3[key] = id;
    }

    /// <summary>
    /// The fill or stroke value of a colour (a hex colour or a gradient or pattern) for an element whose space is
    /// mapped onto the page by <paramref name="toPage"/>, and its opacity; null when SVG can't express it.
    /// </summary>
    private (string Value, double Opacity)? Server(PdfPaintColor color, SKMatrix toPage)
    {
        var opacity = color.Color.Alpha / 255.0;
        if (!toPage.TryInvert(out var fromPage))
            return null;
        if (color.Tile is { } tile)
            return ($"url(#{Pattern(tile.Image, tile.Space.PostConcat(fromPage))})", opacity);
        if (color.Shading is { } fill)
        {
            if (fill.Picture is { } picture)
                return ($"url(#{Pattern(picture.Image, picture.Space.PostConcat(fromPage))})", opacity);
            return Gradient(fill, toPage, grey: false) is { } gradient ? ($"url(#{gradient})", opacity) : null;
        }
        return (Hex(color.Color), opacity);
    }

    /// <summary>A picture repeated from its pixels' space mapped by <paramref name="space"/> (a tiling pattern's cell).</summary>
    private string Pattern(SKImage image, SKMatrix space)
    {
        if (_patterns.TryGetValue((image, space), out var known))
            return known;
        var picture = PictureId(image);
        var id = NewId("p");
        Raw($"<defs><pattern id=\"{id}\" patternUnits=\"userSpaceOnUse\" width=\"{image.Width}\" height=\"{image.Height}\" " +
            $"patternTransform=\"{Matrix(space)}\"><use xlink:href=\"#{picture}\"/></pattern></defs>\n");
        return _patterns[(image, space)] = id;
    }

    /// <summary>A picture Filee drew (a pattern cell, a shading) as a PNG image at its pixel size.</summary>
    private string PictureId(SKImage image)
    {
        if (_pictures.TryGetValue(image, out var known))
            return known;
        var id = NewId("i");
        Count(image.Width, image.Height);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        Raw($"<defs><image id=\"{id}\" width=\"{image.Width}\" height=\"{image.Height}\" xlink:href=\"data:image/png;base64,");
        Base64(data.AsSpan());
        Raw("\"/></defs>\n");
        return _pictures[image] = id;
    }

    /// <summary>
    /// A gradient painting an axial or radial shading for an element whose space is mapped onto the page by
    /// <paramref name="toPage"/>, <paramref name="grey"/>: its colours as the grey of their luminosity (a mask). Null
    /// for other shadings and for radial ones SVG 1.1 can't express.
    /// </summary>
    private string? Gradient(PdfShadingFill fill, SKMatrix toPage, bool grey)
    {
        if (!toPage.TryInvert(out var fromPage))
            return null;
        var key = (fill, fromPage, grey);
        if (_gradients.TryGetValue(key, out var known))
            return known;
        var transform = Matrix(fill.Space.PostConcat(fromPage));
        var shading = fill.Shading;
        var (extendStart, extendEnd) = PdfShadings.Extend(shading);
        var stops = Stops(shading, grey);
        string? element = null;
        var id = NewId("l");
        switch (shading)
        {
            case AxialShading { Coords: [var x0, var y0, var x1, var y1, ..] } when PdfShadings.Finite(x0, y0, x1, y1) && (x0 != x1 || y0 != y1):
                {
                    var list = new List<(double Offset, SKColor Color, bool Clear)>();
                    if (!extendStart)
                        list.Add((0, stops[0].Color, true));
                    list.AddRange(stops.Select(s => (s.T, s.Color, false)));
                    if (!extendEnd)
                        list.Add((1, stops[^1].Color, true));
                    element = $"<linearGradient id=\"{id}\" gradientUnits=\"userSpaceOnUse\" x1=\"{N(x0)}\" y1=\"{N(y0)}\" x2=\"{N(x1)}\" y2=\"{N(y1)}\" " +
                              $"gradientTransform=\"{transform}\">{StopElements(list)}</linearGradient>";
                    break;
                }
            case RadialShading { Coords: [var x0, var y0, var r0, var x1, var y1, var r1, ..] }
                when PdfShadings.Finite(x0, y0, r0, x1, y1, r1) && r0 >= 0 && r1 >= 0 && r0 + r1 > 0:
                element = Radial(id, transform, stops, (x0, y0, r0), (x1, y1, r1), extendStart, extendEnd);
                break;
        }
        if (element is null)
            return _gradients[key] = null;
        Raw($"<defs>{element}</defs>\n");
        return _gradients[key] = id;
    }

    /// <summary>
    /// A radial gradient for circles from <paramref name="start"/> to <paramref name="end"/>, where SVG 1.1 has one:
    /// circles of one centre, or one of the circles a point inside the other (the focus). Null for others.
    /// </summary>
    private static string? Radial(string id, string transform, List<(double T, SKColor Color)> stops, (double X, double Y, double R) start,
        (double X, double Y, double R) end, bool extendStart, bool extendEnd)
    {
        var largest = Math.Max(start.R, end.R);
        var apart = Math.Sqrt((end.X - start.X) * (end.X - start.X) + (end.Y - start.Y) * (end.Y - start.Y));
        var list = new List<(double Offset, SKColor Color, bool Clear)>();
        string circle;
        if (apart <= 1e-4 * largest)
        {
            // One centre: the offset is the radius over the largest; inside the smaller circle nothing unless it extends.
            var growing = end.R >= start.R;
            var (inner, extendInner, extendOuter) = growing ? (start.R, extendStart, extendEnd) : (end.R, extendEnd, extendStart);
            var placed = stops.Select(s => ((start.R + s.T * (end.R - start.R)) / largest, s.Color, false)).OrderBy(s => s.Item1).ToList();
            if (!extendInner && inner > 0)
            {
                list.Add((0, placed[0].Color, true));
                list.Add((placed[0].Item1, placed[0].Color, true));
            }
            list.AddRange(placed);
            if (!extendOuter)
                list.Add((1, placed[^1].Color, true));
            circle = $"cx=\"{N(start.X)}\" cy=\"{N(start.Y)}\" r=\"{N(largest)}\"";
        }
        else if (start.R <= 1e-6 * largest && apart < end.R * 0.999)
        {
            // The start is a point inside the end circle: SVG's focus. Circles between them are those of the PDF.
            list.AddRange(stops.Select(s => (s.T, s.Color, false)));
            if (!extendEnd)
                list.Add((1, stops[^1].Color, true));
            circle = $"cx=\"{N(end.X)}\" cy=\"{N(end.Y)}\" r=\"{N(end.R)}\" fx=\"{N(start.X)}\" fy=\"{N(start.Y)}\"";
        }
        else if (end.R <= 1e-6 * largest && apart < start.R * 0.999)
        {
            // The same, the other way round: the end is the focus.
            list.AddRange(stops.Select(s => (1 - s.T, s.Color, false)).Reverse());
            if (!extendStart)
                list.Add((1, stops[0].Color, true));
            circle = $"cx=\"{N(start.X)}\" cy=\"{N(start.Y)}\" r=\"{N(start.R)}\" fx=\"{N(end.X)}\" fy=\"{N(end.Y)}\"";
        }
        else
        {
            return null;
        }
        return $"<radialGradient id=\"{id}\" gradientUnits=\"userSpaceOnUse\" {circle} gradientTransform=\"{transform}\">{StopElements(list)}</radialGradient>";
    }

    private static string StopElements(List<(double Offset, SKColor Color, bool Clear)> stops)
    {
        var text = new StringBuilder();
        foreach (var (offset, color, clear) in stops)
            text.Append($"<stop offset=\"{N(Math.Clamp(offset, 0, 1), "0.####")}\" stop-color=\"{Hex(color)}\"{(clear ? " stop-opacity=\"0\"" : "")}/>");
        return text.ToString();
    }

    /// <summary>
    /// The shading's colours along its domain (0 to 1), as the grey of their luminosity when <paramref name="grey"/>:
    /// <see cref="GradientSamples"/> samples less those that lie on the straight run between their neighbours kept.
    /// </summary>
    private static List<(double T, SKColor Color)> Stops(Shading shading, bool grey)
    {
        var samples = new (double T, SKColor Color)[GradientSamples];
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / (double)(samples.Length - 1);
            var color = PdfShadings.ColorAt(shading, t);
            if (grey)
            {
                var level = PdfShadings.Byte(PdfShadings.Luminosity(color));
                color = new SKColor(level, level, level);
            }
            samples[i] = (t, color);
        }
        var kept = new List<(double T, SKColor Color)> { samples[0] };
        var from = 0;
        while (from < samples.Length - 1)
        {
            var to = from + 1;
            while (to + 1 < samples.Length && Straight(samples, from, to + 1))
                to++;
            kept.Add(samples[to]);
            from = to;
        }
        return kept;
    }

    /// <summary>Whether every sample between <paramref name="from"/> and <paramref name="to"/> lies on the run between them.</summary>
    private static bool Straight((double T, SKColor Color)[] samples, int from, int to)
    {
        var (a, b) = (samples[from].Color, samples[to].Color);
        for (var i = from + 1; i < to; i++)
        {
            var f = (samples[i].T - samples[from].T) / (samples[to].T - samples[from].T);
            var c = samples[i].Color;
            if (Math.Abs(a.Red + (b.Red - a.Red) * f - c.Red) > StopTolerance || Math.Abs(a.Green + (b.Green - a.Green) * f - c.Green) > StopTolerance
                || Math.Abs(a.Blue + (b.Blue - a.Blue) * f - c.Blue) > StopTolerance)
                return false;
        }
        return true;
    }

    /// <summary>
    /// A soft mask other than a constant alpha as a mask element (in page space); null when it can't be drawn.
    /// SVG masks by luminosity: a luminosity shading is its grey gradient, a drawn group's alpha a white picture of it.
    /// </summary>
    private string? Mask(PdfMask mask)
    {
        if (_masks.TryGetValue(mask, out var known))
            return known;
        string? content = null;
        var region = $"x=\"{N(_region.Left)}\" y=\"{N(_region.Top)}\" width=\"{N(_region.Width)}\" height=\"{N(_region.Height)}\"";
        var alpha = Math.Clamp(mask.Alpha, 0, 1);
        var opacity = alpha < 1 ? $" opacity=\"{N(alpha)}\"" : "";
        if (mask.Picture is { } picture)
        {
            content = $"<use xlink:href=\"#{Recoloured(picture.Image, luminosity: false)}\" transform=\"{Matrix(picture.Space)}\"{opacity}/>";
        }
        else if (mask.Shading is { Picture: { } drawn })
        {
            content = $"<use xlink:href=\"#{Recoloured(drawn.Image, luminosity: true)}\" transform=\"{Matrix(drawn.Space)}\"{opacity}/>";
        }
        else if (mask.Shading is { } shading)
        {
            content = Gradient(shading, SKMatrix.Identity, grey: true) is { } gradient
                ? $"<rect {region} fill=\"url(#{gradient})\"{opacity}/>"
                : DrawnMask(shading, opacity);
        }
        if (content is null)
            return _masks[mask] = null;
        var id = NewId("m");
        Raw($"<defs><mask id=\"{id}\" maskUnits=\"userSpaceOnUse\" {region}>{content}</mask></defs>\n");
        return _masks[mask] = id;
    }

    /// <summary>A luminosity shading SVG has no gradient for, drawn grey into a picture over the page.</summary>
    private string? DrawnMask(PdfShadingFill shading, string opacity)
    {
        using var shader = PdfShadings.Shader(shading, c =>
        {
            var level = PdfShadings.Byte(PdfShadings.Luminosity(c));
            return new SKColor(level, level, level);
        });
        if (shader is null)
            return null;
        var scale = Math.Min(MaskScale, MaxMaskSide / Math.Max(_area.Width, _area.Height));
        var (width, height) = (Math.Max(1, (int)Math.Ceiling(_area.Width * scale)), Math.Max(1, (int)Math.Ceiling(_area.Height * scale)));
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Black);
            canvas.Scale(scale);
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawRect(_area, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        var id = PictureId(image);
        return $"<use xlink:href=\"#{id}\" transform=\"{Matrix(SKMatrix.CreateScale(1 / scale, 1 / scale))}\"{opacity}/>";
    }

    /// <summary>
    /// A mask's picture as an image SVG's luminosity mask reads the same: white with the picture's alpha
    /// (<paramref name="luminosity"/> false), or opaque grey of the picture's luminosity.
    /// </summary>
    private string Recoloured(SKImage image, bool luminosity)
    {
        var key = (image, luminosity);
        if (_recoloured.TryGetValue(key, out var known))
            return known;
        using var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        image.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
        var pixels = bitmap.GetPixelSpan();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (luminosity)
            {
                var level = (byte)Math.Clamp(Math.Round(0.3 * pixels[i] + 0.59 * pixels[i + 1] + 0.11 * pixels[i + 2]), 0, 255);
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (level, level, level, 255);
            }
            else
            {
                var a = pixels[i + 3];
                (pixels[i], pixels[i + 1], pixels[i + 2]) = (a, a, a); // white, premultiplied
            }
        }
        var id = NewId("i");
        Count(image.Width, image.Height);
        Raw($"<defs><image id=\"{id}\" width=\"{image.Width}\" height=\"{image.Height}\" xlink:href=\"data:image/png;base64,");
        Base64(Png(bitmap));
        Raw("\"/></defs>\n");
        return _recoloured[key] = id;
    }

    // ───────────────────────── Pictures ─────────────────────────

    /// <summary>A picture of the PDF as an image definition (once for each picture and stencil colour); null when it can't be read.</summary>
    private (string Id, int Width, int Height)? Picture(PdfPaintedImage image)
    {
        var key = PictureKey(image);
        if (key is null)
            return null;
        if (_images.TryGetValue(key, out var known))
            return known;
        (string Id, int Width, int Height)? result = null;
        if (Jpeg(image) is { } jpeg && image.Image is { } plain)
        {
            Count(plain.WidthInSamples, plain.HeightInSamples);
            var id = NewId("i");
            Raw($"<defs><image id=\"{id}\" width=\"{plain.WidthInSamples}\" height=\"{plain.HeightInSamples}\" preserveAspectRatio=\"none\" " +
                "xlink:href=\"data:image/jpeg;base64,");
            Base64(jpeg.Span);
            Raw("\"/></defs>\n");
            result = (id, plain.WidthInSamples, plain.HeightInSamples);
        }
        else
        {
            if (image.Image is { } source)
                Count(source.WidthInSamples, source.HeightInSamples);
            using var bitmap = PdfRasterCache.Read(image);
            if (bitmap is not null)
            {
                if (image.Image is null)
                    Count(bitmap.Width, bitmap.Height);
                var id = NewId("i");
                Raw($"<defs><image id=\"{id}\" width=\"{bitmap.Width}\" height=\"{bitmap.Height}\" preserveAspectRatio=\"none\" " +
                    "xlink:href=\"data:image/png;base64,");
                Base64(Png(bitmap));
                Raw("\"/></defs>\n");
                result = (id, bitmap.Width, bitmap.Height);
            }
        }
        return _images[key] = result;
    }

    /// <summary>
    /// What tells a picture from the others: its data, its soft mask's, its dictionary and the stencil colour (the same
    /// XObject drawn again is the same picture, though PdfPig reads it again for each "Do").
    /// </summary>
    private object? PictureKey(PdfPaintedImage image)
    {
        if (image.Image is not { } source)
            return image.Own;
        var mask = source.MaskImage is { } soft ? Hash(soft.RawMemory) : default;
        return (Hash(source.RawMemory), mask, source.WidthInSamples, source.HeightInSamples, source.ImageDictionary?.ToString(),
            image.IsMask ? image.MaskColor : SKColors.Empty, image.Own is null);
    }

    private UInt128 Hash(ReadOnlyMemory<byte> data)
    {
        if (_hashes.TryGetValue(data, out var known))
            return known;
        return _hashes[data] = XxHash128.HashToUInt128(data.Span);
    }

    /// <summary>The same bytes in memory (PdfPig hands out the stream's data as it is): their hash is kept.</summary>
    private sealed class MemoryKeys : IEqualityComparer<ReadOnlyMemory<byte>>
    {
        public bool Equals(ReadOnlyMemory<byte> x, ReadOnlyMemory<byte> y) =>
            MemoryMarshal.TryGetArray(x, out var a) && MemoryMarshal.TryGetArray(y, out var b)
                ? ReferenceEquals(a.Array, b.Array) && a.Offset == b.Offset && a.Count == b.Count
                : x.Equals(y);

        public int GetHashCode(ReadOnlyMemory<byte> key) =>
            MemoryMarshal.TryGetArray(key, out var a) ? HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(a.Array), a.Offset, a.Count) : key.GetHashCode();
    }

    /// <summary>
    /// The JPEG file of a picture stored as one (DCTDecode its only filter) that shows as it is: grey or RGB, without
    /// a soft mask, a colour key mask or a Decode array; null for any other picture.
    /// </summary>
    internal static ReadOnlyMemory<byte>? Jpeg(PdfPaintedImage image)
    {
        if (image.Own is not null || image.Image is not { IsImageMask: false, MaskImage: null, BitsPerComponent: 8 } source)
            return null;
        var dictionary = source.ImageDictionary;
        if (dictionary is null || dictionary.ContainsKey(NameToken.Decode) || dictionary.ContainsKey(NameToken.D) || dictionary.ContainsKey(NameToken.Mask)
            || dictionary.ContainsKey(NameToken.Smask))
            return null;
        var filter = dictionary.TryGet(NameToken.Filter, out var token) || dictionary.TryGet(NameToken.F, out token) ? token : null;
        var dct = filter switch
        {
            NameToken name => name.Data is "DCTDecode" or "DCT",
            ArrayToken { Length: 1 } array => array.Data[0] is NameToken { Data: "DCTDecode" or "DCT" },
            _ => false,
        };
        if (!dct || source.ColorSpaceDetails is not { NumberOfColorComponents: 1 or 3 } space
            || space.Type is not (ColorSpace.DeviceGray or ColorSpace.DeviceRGB or ColorSpace.CalGray or ColorSpace.CalRGB or ColorSpace.ICCBased))
            return null;
        var data = source.RawMemory;
        return data.Length > 3 && data.Span[0] == 0xFF && data.Span[1] == 0xD8 && data.Span[2] == 0xFF ? data : null;
    }

    /// <summary>Counts pixels of pictures decoded or drawn for the page against <see cref="PdfSvgWriter.MaxPicturePixels"/>.</summary>
    private void Count(long width, long height)
    {
        _pixels += Math.Max(0, width) * Math.Max(0, height);
        if (_pixels > PdfSvgWriter.MaxPicturePixels)
            throw DocumentLimits.TooLarge($"The pictures of a page hold more than {DocumentLimits.Count(PdfSvgWriter.MaxPicturePixels)} pixels.");
    }

    private static byte[] Png(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // ───────────────────────── Text ─────────────────────────

    /// <summary>A line of the page's text: where it starts, which way it runs, its size, and its characters' places along it.</summary>
    private sealed class Line(SKPoint origin, SKPoint direction, double size)
    {
        public SKPoint Origin { get; } = origin;
        public SKPoint Direction { get; } = direction;
        public double Size { get; } = size;
        public StringBuilder Text { get; } = new();
        public List<double> Places { get; } = [];

        /// <summary>Where the last character ends along the line.</summary>
        public double End { get; private set; }

        /// <summary>
        /// Where a letter starting at <paramref name="start"/> lies along the line, when it goes on it: the same size
        /// and direction, on its baseline, not far before its end nor far after it.
        /// </summary>
        public double? Along(SKPoint start, SKPoint direction, double size)
        {
            if (Math.Abs(size - Size) > 0.05 * Size || direction.X * Direction.X + direction.Y * Direction.Y < 0.999)
                return null;
            var (dx, dy) = (start.X - Origin.X, start.Y - Origin.Y);
            var along = dx * Direction.X + dy * Direction.Y;
            var across = dy * Direction.X - dx * Direction.Y;
            return Math.Abs(across) <= 0.25 * Size && along >= End - 0.5 * Size && along - End <= 8 * Size ? along : null;
        }

        public void Add(string value, double along, double length)
        {
            // Words the PDF places apart without a space between them get one, so they can be searched for.
            if (Text.Length > 0 && along - End > 0.2 * Size && !char.IsWhiteSpace(Text[^1]) && !char.IsWhiteSpace(value[0]))
            {
                Text.Append(' ');
                Places.Add(End);
            }
            for (var i = 0; i < value.Length; i++)
            {
                Text.Append(value[i]);
                Places.Add(along + length * i / value.Length);
            }
            End = along + length;
        }
    }

    /// <summary>The page's text as transparent SVG text on top, a line at a time, each character where the PDF has it.</summary>
    private void Text(IReadOnlyList<Letter> letters)
    {
        var lines = new List<Line>();
        Line? line = null;
        var count = 0;
        foreach (var letter in letters)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (++count > MaxTextLetters)
                break;
            var value = Clean(letter.Value);
            var size = letter.PointSize;
            if (value.Length == 0 || !double.IsFinite(size) || size < 0.1 || size > 10_000)
                continue;
            // PdfPig's letters are y up from the bottom of the page as shown.
            var start = new SKPoint((float)letter.StartBaseLine.X, (float)(_page.Height - letter.StartBaseLine.Y));
            var end = new SKPoint((float)letter.EndBaseLine.X, (float)(_page.Height - letter.EndBaseLine.Y));
            if (!float.IsFinite(start.X) || !float.IsFinite(start.Y) || !float.IsFinite(end.X) || !float.IsFinite(end.Y))
                continue;
            var length = Math.Sqrt((end.X - start.X) * (end.X - start.X) + (end.Y - start.Y) * (end.Y - start.Y));
            var direction = length > 1e-6 ? new SKPoint((float)((end.X - start.X) / length), (float)((end.Y - start.Y) / length)) : line?.Direction ?? new SKPoint(1, 0);
            if (line?.Along(start, direction, size) is { } along)
            {
                line.Add(value, along, length);
            }
            else
            {
                line = new Line(start, direction, size);
                lines.Add(line);
                line.Add(value, 0, length);
            }
        }
        if (lines.Count == 0)
            return;
        // Transparent twice over: Skia's SVG readers draw text whose fill-opacity alone is 0.
        Raw("<g opacity=\"0\" fill-opacity=\"0\">\n");
        foreach (var text in lines)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var (x, y) = (text.Direction.X, text.Direction.Y);
            var matrix = new SKMatrix(x, -y, text.Origin.X, y, x, text.Origin.Y, 0, 0, 1);
            Raw($"<text xml:space=\"preserve\" fill-opacity=\"0\" transform=\"{Matrix(matrix)}\" font-size=\"{N(text.Size)}\" x=\"{string.Join(' ', text.Places.Select(p => N(p)))}\" y=\"0\">");
            Raw(Escape(text.Text.ToString()));
            Raw("</text>\n");
        }
        Raw("</g>\n");
    }

    /// <summary>A letter's characters that XML can hold, control characters and broken surrogate pairs left out.</summary>
    private static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var clean = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                clean.Append(c).Append(value[++i]);
                continue;
            }
            if (char.IsSurrogate(c) || c < ' ' || c is '￾' or '￿' || (c >= '\u007F' && c <= '\u009F'))
                continue;
            clean.Append(c);
        }
        return clean.ToString();
    }

    private static string Escape(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    // ───────────────────────── Output ─────────────────────────

    private void Raw(string text)
    {
        _chars += text.Length;
        if (_chars > _maxChars)
            throw TooLarge();
        _writer.Write(text);
    }

    private Filee.Core.Conversion.ConversionRefusedException TooLarge() =>
        DocumentLimits.TooLarge($"A page's SVG would be larger than {DocumentLimits.Megabytes(_maxChars)}.");

    private void Base64(ReadOnlySpan<byte> data)
    {
        Span<char> chars = stackalloc char[4096];
        while (data.Length > 0)
        {
            var take = Math.Min(data.Length, 3072);
            Convert.TryToBase64Chars(data[..take], chars, out var written);
            _chars += written;
            if (_chars > _maxChars)
                throw TooLarge();
            _writer.Write(chars[..written]);
            data = data[take..];
        }
    }

    private static void AppendPath(StringBuilder text, SKPath path, float scale, string format)
    {
        using var iterator = path.CreateRawIterator();
        var points = new SKPoint[4];
        SKPathVerb verb;
        void Point(SKPoint p) => text.Append(N(p.X * scale, format)).Append(' ').Append(N(p.Y * scale, format));
        while ((verb = iterator.Next(points)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    text.Append('M');
                    Point(points[0]);
                    break;
                case SKPathVerb.Line:
                    text.Append('L');
                    Point(points[1]);
                    break;
                case SKPathVerb.Quad:
                    text.Append('Q');
                    Point(points[1]);
                    text.Append(' ');
                    Point(points[2]);
                    break;
                case SKPathVerb.Conic:
                    {
                        var quads = SKPath.ConvertConicToQuads(points[0], points[1], points[2], iterator.ConicWeight(), 2);
                        for (var i = 1; i + 1 < quads.Length; i += 2)
                        {
                            text.Append('Q');
                            Point(quads[i]);
                            text.Append(' ');
                            Point(quads[i + 1]);
                        }
                        break;
                    }
                case SKPathVerb.Cubic:
                    text.Append('C');
                    Point(points[1]);
                    text.Append(' ');
                    Point(points[2]);
                    text.Append(' ');
                    Point(points[3]);
                    break;
                case SKPathVerb.Close:
                    text.Append('Z');
                    break;
            }
        }
    }

    private static string Matrix(SKMatrix m) =>
        $"matrix({N(m.ScaleX, "0.######")} {N(m.SkewY, "0.######")} {N(m.SkewX, "0.######")} {N(m.ScaleY, "0.######")} {N(m.TransX)} {N(m.TransY)})";

    private static string N(double value, string format = "0.###")
    {
        if (!double.IsFinite(value))
            return "0";
        var text = value.ToString(format, CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    private static string Hex(SKColor color) => $"#{color.Red:x2}{color.Green:x2}{color.Blue:x2}";
}
