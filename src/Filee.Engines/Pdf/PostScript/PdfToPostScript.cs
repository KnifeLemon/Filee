// PDF → PostScript LanguageLevel 3 and EPS, written by Filee (no Ghostscript). The PDF is read with PDFsharp, each
// page's content translated by PsTranslator, the fonts written by PsFonts, and the file laid out by the Document
// Structuring Conventions:
//
//   %!PS-Adobe-3.0 (EPSF-3.0)  header comments, %%BoundingBox, %%LanguageLevel: 3, %%DocumentData: Clean7Bit
//   prolog                     FileeDict: short procedures for the PDF operators, text, pictures, patterns
//   setup                      the fonts (Type 1, Type 42, Type 3 resources of Filee's own)
//   pages                      each in save / restore, its size set with setpagedevice (PS only), the crop box
//                              clipped and the page turned as /Rotate says; annotation appearances that print
//
// A PostScript file holds every selected page; an EPS holds one page, so a PDF gives one EPS per page (_p{n}), its
// bounding box the page's crop box. Pages with what PostScript can't hold (transparency, text clipping, …; see
// PsTranslator) are drawn as a picture of the page rendered by PDFium, and the file's result carries a notice.

using System.Security.Cryptography;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Microsoft.Extensions.Logging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDFtoImage;
using SkiaSharp;

#pragma warning disable CA1416 // PDFtoImage runs on the desktop platforms Filee targets

namespace Filee.Engines.Pdf.PostScript;

/// <summary>Writes PostScript and EPS files from a PDF.</summary>
internal sealed class PdfToPostScript
{
    /// <summary>Resolution of pages drawn as pictures: at least this, or the preset's render DPI when higher.</summary>
    internal const int MinRasterDpi = 300;
    internal const int MaxRasterDpi = 600;

    /// <summary>A page larger than this many pixels at the chosen resolution is rendered at a lower one.</summary>
    internal const double MaxRasterPixels = 40_000_000;

    private readonly string _inputPath;
    private readonly byte[] _pdf;
    private readonly ILogger? _logger;
    private readonly CancellationToken _cancellationToken;
    private readonly int _rasterDpi;
    private PdfDocument? _document;
    private OptionalContent? _optionalContent;

    /// <summary>
    /// Pages PostScript can't hold are drawn as pictures (true), or left empty (false: the fuzz tests, which look for
    /// crashes in the translation, not in PDFium).
    /// </summary>
    internal bool RasterFallback { get; init; } = true;

    /// <summary>Pages drawn as pictures, with the reason (1-based page numbers).</summary>
    public List<(int Page, string Reason)> RasterPages { get; } = [];

    public PdfToPostScript(string inputPath, int renderDpi, ILogger? logger, CancellationToken cancellationToken)
    {
        _inputPath = inputPath;
        _pdf = File.ReadAllBytes(inputPath);
        _logger = logger;
        _cancellationToken = cancellationToken;
        _rasterDpi = Math.Clamp(renderDpi, MinRasterDpi, MaxRasterDpi);
    }

    /// <summary>Pages of the document (opened with PDFsharp; a PDF it can't read is drawn by PDFium alone).</summary>
    public int PageCount
    {
        get
        {
            Open();
            return _document?.PageCount ?? RenderedPageCount();
        }
    }

    private int RenderedPageCount()
    {
        try
        {
            return Conversion.GetPageCount(_pdf, null);
        }
        catch (PDFtoImage.Exceptions.PdfPasswordProtectedException)
        {
            throw PdfDocumentReader.PasswordProtected();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException("The PDF can't be read.", ex);
        }
    }

    private void Open()
    {
        if (_document is not null || _optionalContent is not null)
            return;
        try
        {
            _document = PdfSharpConverter.Open(_inputPath, PdfDocumentOpenMode.Import);
            _optionalContent = OptionalContent.Read(_document.Internals.Catalog);
        }
        catch (Exception ex) when (ex is PdfReaderException or InvalidOperationException or InvalidDataException or NotSupportedException
            or ArgumentException or FormatException or IndexOutOfRangeException or NullReferenceException or OverflowException)
        {
            if (ex is InvalidOperationException && ex.Message == PdfDocumentReader.PasswordProtected().Message)
                throw;
            // PDFsharp can't read it: every page becomes a picture, if PDFium can draw it.
            _logger?.LogInformation(ex, "PDF → PostScript: PDFsharp can't read {File}; its pages are drawn as pictures", Path.GetFileName(_inputPath));
            _document = null;
            _optionalContent = new OptionalContent();
        }
    }

    /// <summary>Writes the zero-based <paramref name="pages"/> into one PostScript file (or an EPS of one page).</summary>
    public void Write(IReadOnlyList<int> pages, string output, bool eps, string workDirectory)
    {
        Open();
        if (pages.Count > DocumentLimits.Default.MaxPages)
            throw DocumentLimits.Exceeded($"The PDF has more than {DocumentLimits.Count(DocumentLimits.Default.MaxPages)} pages.");
        var tag = Tag(pages);
        var bodyPath = Path.Combine(workDirectory, $"ps-{Guid.NewGuid():N}.body");
        PsContext? context = null;
        PdfDictionary? pageResources = null;
        var fonts = new PsFontSet($"Filee{tag}", (glyph, resources, depth) =>
            PsTranslator.TranslateGlyph(context!, glyph, resources ?? pageResources, depth));
        context = new PsContext(fonts, _optionalContent ?? new OptionalContent(), _cancellationToken);
        var sizes = new List<(double Width, double Height)>();
        try
        {
            using (var body = new PsWriter(new FileStream(bodyPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16)))
            {
                for (var i = 0; i < pages.Count; i++)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    var index = pages[i];
                    var geometry = Geometry(index);
                    sizes.Add((geometry.Width, geometry.Height));
                    body.Line($"%%Page: {index + 1} {i + 1}");
                    if (!eps)
                    {
                        body.Line(FormattableString.Invariant($"%%PageBoundingBox: 0 0 {Math.Ceiling(geometry.Width)} {Math.Ceiling(geometry.Height)}"));
                        body.Line("%%BeginPageSetup");
                        body.NewLine().Token("<<").Name("PageSize").Array([geometry.Width, geometry.Height]).Token(">>").Token("setpagedevice");
                        body.Line("%%EndPageSetup");
                    }
                    body.Line("/FileePageSave save def FileeDict begin");
                    body.Line("0 setgray 1 setlinewidth 0 setlinecap 0 setlinejoin 10 setmiterlimit [] 0 setdash newpath");
                    var start = body.Position;
                    string? reason = null;
                    if (_document is null)
                    {
                        reason = "PDFsharp can't read the PDF";
                    }
                    else
                    {
                        try
                        {
                            var page = _document.Pages[index];
                            pageResources = PdfObjects.Dictionary(page, "/Resources");
                            Translate(page, geometry, context, body);
                        }
                        catch (PsUntranslatableException ex)
                        {
                            reason = ex.Message;
                        }
                        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException
                            or IndexOutOfRangeException or OverflowException or InvalidCastException or NullReferenceException)
                        {
                            reason = $"the page can't be translated ({ex.Message})";
                        }
                    }
                    if (reason is not null)
                    {
                        // Back to the start of the page, and its picture instead.
                        body.BaseStream.Flush();
                        body.BaseStream.SetLength(start);
                        body.BaseStream.Position = start;
                        body.NewLine();
                        RasterPages.Add((index + 1, reason));
                        _logger?.LogInformation("PDF → PostScript: page {Page} of {File} drawn as a picture: {Reason}", index + 1, Path.GetFileName(_inputPath), reason);
                        if (RasterFallback)
                            Raster(index, geometry, body);
                    }
                    body.Line("end FileePageSave restore showpage");
                    body.Line("%%PageTrailer");
                }
            }
            Assemble(output, bodyPath, sizes, eps, fonts);
        }
        finally
        {
            File.Delete(bodyPath);
        }
    }

    /// <summary>Four capital letters from the file and the pages, so fonts of two Filee files placed together don't clash.</summary>
    private string Tag(IReadOnlyList<int> pages)
    {
        var hash = SHA256.HashData([.. SHA256.HashData(_pdf), .. pages.SelectMany(BitConverter.GetBytes)]);
        return new string([.. hash.Take(4).Select(b => (char)('A' + b % 26))]);
    }

    /// <summary>A page's size as printed (crop box, turned, UserUnit) and the transformation from PDF space to it.</summary>
    private readonly record struct PageGeometry(double Width, double Height, Mat Transform, (double X0, double Y0, double X1, double Y1) Box);

    private PageGeometry Geometry(int index)
    {
        (double X0, double Y0, double X1, double Y1) box = (0, 0, 612, 792);
        var rotate = 0;
        var unit = 1.0;
        if (_document is not null)
        {
            var page = _document.Pages[index];
            var media = PdfObjects.Rectangle(PdfObjects.Get(page, "/MediaBox")) ?? (0, 0, 612, 792);
            box = media;
            if (PdfObjects.Rectangle(PdfObjects.Get(page, "/CropBox")) is { } crop)
            {
                var clipped = (Math.Max(crop.X0, media.X0), Math.Max(crop.Y0, media.Y0), Math.Min(crop.X1, media.X1), Math.Min(crop.Y1, media.Y1));
                if (clipped.Item3 > clipped.Item1 && clipped.Item4 > clipped.Item2)
                    box = clipped;
            }
            rotate = ((int)PdfObjects.Number(page, "/Rotate", 0) % 360 + 360) % 360 / 90 * 90;
            unit = Math.Clamp(PdfObjects.Number(page, "/UserUnit", 1), 0.01, 100);
        }
        else
        {
            var size = Conversion.GetPageSizes(_pdf, null)[index];
            box = (0, 0, size.Width, size.Height);
        }
        double w = box.X1 - box.X0, h = box.Y1 - box.Y0;
        if (w < 1 || h < 1 || w * unit > 200_000 || h * unit > 200_000)
            throw new InvalidDataException("A page has an unusual size.");
        var move = new Mat(1, 0, 0, 1, -box.X0, -box.Y0);
        var turn = rotate switch
        {
            90 => new Mat(0, -1, 1, 0, 0, w),
            180 => new Mat(-1, 0, 0, -1, w, h),
            270 => new Mat(0, 1, -1, 0, h, 0),
            _ => Mat.Identity,
        };
        var scale = new Mat(unit, 0, 0, unit, 0, 0);
        var (width, height) = rotate is 90 or 270 ? (h, w) : (w, h);
        return new PageGeometry(width * unit, height * unit, move.Then(turn).Then(scale), box);
    }

    private void Translate(PdfPage page, PageGeometry geometry, PsContext context, PsWriter w)
    {
        context.StartPage();
        var t = geometry.Transform;
        w.NewLine().Array(t.ToArray()).Token("cm");
        var b = geometry.Box;
        w.Numbers([b.X0, b.Y0, b.X1 - b.X0, b.Y1 - b.Y0]).Token("re").Token("W").Token("N");
        w.Line("/FileePageCTM matrix currentmatrix def");
        var resources = PdfObjects.Dictionary(page, "/Resources");
        var content = Contents(page);
        var translator = new PsTranslator(context, w, Mat.Identity, depth: 0);
        translator.Run(content, resources);
        Annotations(page, context, w, resources);
        w.NewLine();
    }

    private static byte[] Contents(PdfPage page)
    {
        using var stream = new MemoryStream();
        var parts = PdfObjects.Get(page, "/Contents") switch
        {
            PdfDictionary { Stream: not null } one => [one],
            PdfArray many => many.Elements.Select(PdfObjects.Resolve).OfType<PdfDictionary>().Where(d => d.Stream is not null).ToList(),
            _ => [],
        };
        if (parts.Count > 10_000)
            throw new InvalidDataException("The page has too many content streams.");
        foreach (var part in parts)
        {
            stream.Write(PdfStreams.Decode(part, DocumentLimits.Default.MaxPartBytes));
            stream.WriteByte((byte)'\n');
            if (stream.Length > DocumentLimits.Default.MaxPartBytes)
                throw new InvalidDataException("The page's content is too long.");
        }
        return stream.ToArray();
    }

    /// <summary>Annotation appearances that print (PDF 32000 12.5.5), placed on their rectangles.</summary>
    private static void Annotations(PdfPage page, PsContext context, PsWriter w, PdfDictionary? resources)
    {
        if (PdfObjects.Array(page, "/Annots") is not { } annotations)
            return;
        foreach (var item in annotations.Elements.Take(10_000))
        {
            if (PdfObjects.Resolve(item) is not PdfDictionary annotation)
                continue;
            var flags = (int)PdfObjects.Number(annotation, "/F", 0);
            if ((flags & 2) != 0 || (flags & 4) == 0)
                continue; // hidden, or not printed
            if (PdfObjects.Dictionary(annotation, "/OC") is { } oc && !context.OptionalContent.Visible(oc))
                continue;
            var appearance = PdfObjects.Get(PdfObjects.Dictionary(annotation, "/AP"), "/N") switch
            {
                PdfDictionary { Stream: not null } stream => stream,
                PdfDictionary states when PdfObjects.Name(annotation, "/AS") is { } state => PdfObjects.Dictionary(states, "/" + state),
                _ => null,
            };
            if (appearance is not { Stream: not null } || PdfObjects.Rectangle(PdfObjects.Get(annotation, "/Rect")) is not { } rect)
                continue;
            if (PdfObjects.Number(PdfObjects.Get(annotation, "/CA")) is < 0.999)
                throw new PsUntranslatableException("An annotation is transparent.");
            var matrix = PdfObjects.Numbers(appearance, "/Matrix") is { Length: 6 } m ? Mat.From(m) : Mat.Identity;
            if (PdfObjects.Rectangle(PdfObjects.Get(appearance, "/BBox")) is not { } box)
                continue;
            // The box as the matrix turns it, mapped onto the annotation's rectangle.
            var corners = new[] { matrix.Apply(box.X0, box.Y0), matrix.Apply(box.X1, box.Y0), matrix.Apply(box.X0, box.Y1), matrix.Apply(box.X1, box.Y1) };
            double x0 = corners.Min(c => c.X), y0 = corners.Min(c => c.Y), x1 = corners.Max(c => c.X), y1 = corners.Max(c => c.Y);
            if (x1 - x0 < 1e-6 || y1 - y0 < 1e-6)
                continue;
            var sx = (rect.X1 - rect.X0) / (x1 - x0);
            var sy = (rect.Y1 - rect.Y0) / (y1 - y0);
            var place = new Mat(sx, 0, 0, sy, rect.X0 - x0 * sx, rect.Y0 - y0 * sy);
            var translator = new PsTranslator(context, w, Mat.Identity, depth: 0);
            translator.DrawForm(appearance, place, clipToBox: true);
            _ = resources;
        }
    }

    /// <summary>The page as PDFium draws it, as an RGB picture covering the page.</summary>
    private void Raster(int index, PageGeometry geometry, PsWriter w)
    {
        var dpi = _rasterDpi;
        var pixels = geometry.Width / 72 * dpi * (geometry.Height / 72 * dpi);
        if (pixels > MaxRasterPixels)
            dpi = Math.Max(1, (int)Math.Floor(dpi * Math.Sqrt(MaxRasterPixels / pixels)));
        SKBitmap bitmap;
        try
        {
            bitmap = Conversion.ToImage(_pdf, index, null,
                new RenderOptions(Dpi: dpi, WithAnnotations: true, WithFormFill: true, BackgroundColor: SKColors.White));
        }
        catch (PDFtoImage.Exceptions.PdfPasswordProtectedException)
        {
            throw PdfDocumentReader.PasswordProtected();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException($"Page {index + 1} can't be drawn.", ex);
        }
        using (bitmap)
        {
            var (width, height) = (bitmap.Width, bitmap.Height);
            var rgb = new byte[width * height * 3];
            using (var rgba = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul)))
            {
                if (!bitmap.CopyTo(rgba, SKColorType.Rgba8888))
                    throw new InvalidDataException($"Page {index + 1} can't be drawn.");
                var data8 = rgba.GetPixelSpan();
                var stride = rgba.RowBytes;
                for (var y = 0; y < height; y++)
                {
                    var row = data8.Slice(y * stride, width * 4);
                    var at = y * width * 3;
                    for (var x = 0; x < width; x++, at += 3)
                    {
                        rgb[at] = row[4 * x];
                        rgb[at + 1] = row[4 * x + 1];
                        rgb[at + 2] = row[4 * x + 2];
                    }
                }
            }
            var data = PsImages.Deflate(rgb);
            var filter = "/FlateDecode filter";
            if (data.Length > 8 * 1024 * 1024)
            {
                using var jpeg = bitmap.Encode(SKEncodedImageFormat.Jpeg, 92);
                if (jpeg.Size < data.Length / 2)
                {
                    data = jpeg.ToArray();
                    filter = "/DCTDecode filter";
                }
            }
            w.NewLine().Numbers([geometry.Width, geometry.Height]).Token("scale").Token("/DeviceRGB setcolorspace");
            w.NewLine().Token("/FileeData currentfile /ASCII85Decode filter def");
            w.NewLine().Token("<<").Name("ImageType").Number(1).Name("Width").Number(width).Name("Height").Number(height)
                .Name("BitsPerComponent").Number(8).Name("Decode").Array([0, 1, 0, 1, 0, 1])
                .Name("ImageMatrix").Array([width, 0, 0, -height, 0, height]).Name("DataSource").Token("FileeData").Token(filter)
                .Token(">>").Token("FileeImage");
            w.Ascii85(data);
        }
    }

    private void Assemble(string output, string bodyPath, List<(double Width, double Height)> sizes, bool eps, PsFontSet fonts)
    {
        var maxWidth = sizes.Count == 0 ? 612 : sizes.Max(s => s.Width);
        var maxHeight = sizes.Count == 0 ? 792 : sizes.Max(s => s.Height);
        using var file = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        var w = new PsWriter(file);
        w.Line(eps ? "%!PS-Adobe-3.0 EPSF-3.0" : "%!PS-Adobe-3.0");
        w.Line("%%Creator: Filee");
        w.Line($"%%Title: {Title(Path.GetFileNameWithoutExtension(_inputPath))}");
        w.Line("%%LanguageLevel: 3");
        w.Line(FormattableString.Invariant($"%%BoundingBox: 0 0 {Math.Ceiling(maxWidth)} {Math.Ceiling(maxHeight)}"));
        w.Line($"%%HiResBoundingBox: 0 0 {PsWriter.Format(maxWidth)} {PsWriter.Format(maxHeight)}");
        w.Line($"%%Pages: {sizes.Count}");
        w.Line("%%DocumentData: Clean7Bit");
        w.Line("%%EndComments");
        w.Line("%%BeginProlog");
        w.Raw(PsProlog.Text);
        w.Line("%%EndProlog");
        w.Line("%%BeginSetup");
        w.Line("FileeDict begin");
        fonts.Write(w);
        w.Line("end");
        w.Line("%%EndSetup");
        w.NewLine();
        file.Flush();
        using (var body = File.OpenRead(bodyPath))
            body.CopyTo(file);
        w.Line("%%Trailer");
        w.Line("%%EOF");
    }

    /// <summary>A title for the DSC comment: printable ASCII only.</summary>
    private static string Title(string name)
    {
        var chars = name.Select(c => c is >= ' ' and < (char)127 ? c : '?').Take(120).ToArray();
        return new string(chars);
    }
}
