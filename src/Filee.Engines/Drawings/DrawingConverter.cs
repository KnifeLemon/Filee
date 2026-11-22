// Filee's own drawing converter (engine id "drawing"): drawings read into the slide model (DrawingFormats: one slide
// per page) and drawn by the slide renderer, to PDF (a page per drawing page), SVG and PNG (a file per page, the PDF
// options' page range and resolution, then the image options). Formats come with their readers; with none it has no
// edges and no route goes through it.
//
// Formats whose reader draws only part of what they hold come with a check (DrawingFormat.FirstUnsupported), which
// FirstUnsupported (ICapabilityCheck) asks: their edges are Checked, so a file the reader can't draw (a binary .vsd)
// keeps the route it would take without this converter.
//
// Fixed-layout documents (XPS, DrawingFormat.ReadPages) draw their own pages into the page model; the same outputs
// are written from those pages. What a reader left out is named in the file's result (ConversionStep.LeftOut).
//
// Slides no other engine reads (DrawingFormat.Slides: interactive whiteboards) also become ODP (OdpWriter) and PPTX
// (their PDF, rebuilt as editable slides by PdfSlidesConverter), routes of their own (DirectOnly) at the ODP writer's
// cost.
//
// ODG, OTG and FODG output (from these drawings, SVG, metafiles, decks and PDF): DrawingConverter.Odg.cs.
// EMF and WMF output (from these drawings, SVG and mind maps): DrawingConverter.Metafile.cs.

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using Filee.Engines.Magick;
using Filee.Engines.Odf.Draw;
using Filee.Engines.Office;
using Filee.Engines.Pdf;
using Filee.Engines.Rendering;
using Filee.Engines.Slides;
using ImageMagick;
using SkiaSharp;

namespace Filee.Engines.Drawings;

/// <summary>Drawings → PDF, SVG and PNG through the slide renderer.</summary>
public sealed partial class DrawingConverter : IConverter, IImageTransformer, ICapabilityCheck
{
    /// <summary>Pixels of one page picture at most; a larger picture is drawn at a lower resolution.</summary>
    private const long MaxPagePixels = 200_000_000;

    private readonly IReadOnlyList<DrawingFormat> _formats;

    /// <summary>One deck at a time through the PDF slides engine, which renders with PDFium (as its own engine gate does).</summary>
    private static readonly SemaphoreSlim PdfSlides = new(1, 1);

    public DrawingConverter() : this(DrawingFormats.BuiltIn, odg: true, metafiles: true)
    {
    }

    /// <summary>A converter of <paramref name="formats"/> instead of the built-in ones (tests).</summary>
    /// <param name="odg">Also ODG output, of these formats and of the other sources (SVG, metafiles, decks, PDF).</param>
    /// <param name="metafiles">Also EMF and WMF output, of these formats and of SVG and mind maps.</param>
    internal DrawingConverter(IReadOnlyList<DrawingFormat> formats, bool odg = false, bool metafiles = false)
    {
        _formats = formats;
        Edges =
        [
            .. from format in formats
               from target in DrawingFormats.Targets
               select new ConversionEdge(format.Id, target, format.Cost, Checked: format.FirstUnsupported is not null),
            .. from format in formats
               where format.Slides
               from target in DrawingFormats.SlideTargets
               select new ConversionEdge(format.Id, target, OdpWriterConverter.Cost, DirectOnly: true, Checked: format.FirstUnsupported is not null),
            .. odg ? OdgEdges(formats, others: true) : [],
            .. metafiles ? MetafileEdges(formats) : [],
        ];
    }

    public string Id => "drawing";
    public string DisplayName => "Drawings (built-in)";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; }

    /// <summary>Pictures of pages are drawn here, with the image options (turn, mirror, size) applied.</summary>
    public bool Transforms(string from, string to) => to is not ("pdf" or "svg" or "odp" or "pptx" or "odg" or "otg" or "fodg" or "emf" or "wmf");

    public EngineStatus GetStatus() => EngineStatus.Available(version: EngineVersions.BuiltIn);

    /// <summary>The first thing in the file its format's reader can't draw, or null.</summary>
    public string? FirstUnsupported(string sourcePath, string sourceFormat) =>
        Find(sourceFormat) is { } format ? format.FirstUnsupported?.Invoke(sourcePath) : FirstUnsupportedForOdg(sourcePath, sourceFormat);

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            if (IsOdg(step.To))
                return WriteOdg(step, progress, cancellationToken);
            if (IsMetafile(step.To))
                return WriteMetafiles(step, progress, cancellationToken);
            var format = Find(step.From) ?? throw new NotSupportedException($"The drawing converter doesn't read {step.From}.");
            if (format.ReadPages is { } open)
            {
                using var document = open(step.InputPath, cancellationToken);
                var written = WriteFixed(step, document, progress, cancellationToken);
                if (document.LeftOut.Count > 0)
                    step.LeftOut?.Invoke(new LeftOutNotice("notice.renderer_left_out", document.LeftOut));
                return written;
            }
            var deck = format.Read!(step.InputPath, cancellationToken);
            if (deck.Slides.Count == 0)
                throw new InvalidDataException("The drawing has no pages.");
            if (deck.LeftOut.Count > 0)
                step.LeftOut?.Invoke(new LeftOutNotice("notice.renderer_left_out", deck.LeftOut));
            progress?.Report(0.2);
            if (step.To is "odp" or "pptx")
                return WriteSlides(step, deck, progress, cancellationToken);
            if (step.To == "pdf")
            {
                var pdf = SlideOutput.ToPdf(deck, progress is null ? null : new Progress<double>(p => progress.Report(0.2 + 0.8 * p)), cancellationToken,
                    step.Preset.Document.PdfA);
                if (step.Output.Allocate("pdf") is not { } target)
                    return [];
                File.WriteAllBytes(target, pdf);
                progress?.Report(1);
                return [target];
            }
            var pages = PageRange.Parse(step.Preset.Pdf.PageRange, deck.Slides.Count);
            return step.To == "svg" ? WriteSvg(step, deck, pages, progress, cancellationToken) : WritePictures(step, deck, pages, progress, cancellationToken);
        }, cancellationToken);

    private DrawingFormat? Find(string format) => _formats.FirstOrDefault(f => f.Id == format);

    /// <summary>A deck as ODP, or as PPTX through its PDF.</summary>
    private static List<string> WriteSlides(ConversionStep step, SlideDeck deck, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (step.To == "odp")
        {
            if (step.Output.Allocate("odp") is not { } odp)
                return [];
            OdpWriter.Write(deck, odp, cancellationToken: cancellationToken);
            progress?.Report(1);
            return [odp];
        }
        var pdf = Path.Combine(step.WorkDirectory, $"slides-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(pdf, SlideOutput.ToPdf(deck, progress is null ? null : new Progress<double>(p => progress.Report(0.2 + 0.3 * p)), cancellationToken));
            PdfSlides.Wait(cancellationToken);
            try
            {
                return [.. new PdfSlidesConverter().ConvertAsync(step with { InputPath = pdf, From = "pdf" },
                    progress is null ? null : new Progress<double>(p => progress.Report(0.5 + 0.5 * p)), cancellationToken).GetAwaiter().GetResult()];
            }
            finally
            {
                PdfSlides.Release();
            }
        }
        finally
        {
            File.Delete(pdf);
        }
    }

    /// <summary>A fixed-layout document's pages as a PDF, SVG files or pictures.</summary>
    private static List<string> WriteFixed(ConversionStep step, IFixedPages document, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(0.1);
        if (step.To == "pdf")
        {
            var all = new List<PageContent>(document.Count);
            for (var i = 0; i < document.Count; i++)
            {
                all.Add(document.Page(i, cancellationToken));
                progress?.Report(0.1 + 0.4 * (i + 1) / document.Count);
            }
            if (step.Output.Allocate("pdf") is not { } target)
                return [];
            using (var output = File.Create(target))
            {
                PdfPageWriter.Write(output, all, new PdfDocumentInfo { Title = document.Title, PdfA = step.Preset.Document.PdfA },
                    progress is null ? null : new Progress<double>(p => progress.Report(0.5 + 0.5 * p)), cancellationToken);
            }
            return [target];
        }
        var pages = PageRange.Parse(step.Preset.Pdf.PageRange, document.Count);
        var extension = step.To == "svg" ? "svg" : ImageEncoder.OutputFormat(step.To, step.Preset.Image).Extension;
        var written = new List<string>();
        var done = 0;
        foreach (var index in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = document.Page(index, cancellationToken);
            var suffix = pages.Count > 1 ? $"_p{index + 1}" : null;
            if (step.To == "svg")
            {
                var svg = SlideOutput.Svg(page, cancellationToken);
                if (step.Output.Allocate(extension, suffix) is { } path)
                {
                    File.WriteAllBytes(path, svg);
                    written.Add(path);
                }
            }
            else
            {
                var dpi = Resolution(step, page.Width, page.Height);
                using var bitmap = page.Render(dpi);
                if (WritePicture(step, bitmap, dpi, extension, suffix) is { } path)
                    written.Add(path);
            }
            done++;
            progress?.Report(0.1 + 0.9 * done / pages.Count);
        }
        return written;
    }

    /// <summary>
    /// The preset's resolution, lowered so a page of <paramref name="width"/> × <paramref name="height"/> points fits
    /// <see cref="MaxPagePixels"/>: a large page at a high resolution would take gigabytes.
    /// </summary>
    private static float Resolution(ConversionStep step, float width, float height)
    {
        var dpi = (float)Math.Clamp(step.Preset.Pdf.RenderDpi, 36, 1200);
        var inches = width / 72.0 * (height / 72.0);
        return (float)Math.Min(dpi, Math.Sqrt(MaxPagePixels / Math.Max(inches, 1e-6)));
    }

    /// <summary>Writes a page's picture with the image options applied; null when the output was skipped.</summary>
    private static string? WritePicture(ConversionStep step, SKBitmap bitmap, float dpi, string extension, string? suffix)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var image = new MagickImage(data.ToArray());
        image.Density = new Density(dpi, dpi, DensityUnit.PixelsPerInch);
        ImageEncoder.ApplyOptions(image, step.Preset.Image, step.To);
        if (step.Output.Allocate(extension, suffix) is not { } path)
            return null;
        ImageEncoder.WriteImage(image, path, step.To);
        return path;
    }

    private static List<string> WriteSvg(ConversionStep step, SlideDeck deck, IReadOnlyList<int> pages, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var written = new List<string>();
        var done = 0;
        foreach (var svg in SlideOutput.ToSvg(deck, pages, cancellationToken))
        {
            if (step.Output.Allocate("svg", pages.Count > 1 ? $"_p{pages[done] + 1}" : null) is { } path)
            {
                File.WriteAllBytes(path, svg);
                written.Add(path);
            }
            done++;
            progress?.Report(0.2 + 0.8 * done / pages.Count);
        }
        return written;
    }

    private static List<string> WritePictures(ConversionStep step, SlideDeck deck, IReadOnlyList<int> pages, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var dpi = Resolution(step, deck.Width, deck.Height);
        var (_, extension) = ImageEncoder.OutputFormat(step.To, step.Preset.Image);
        var written = new List<string>();
        var done = 0;
        foreach (var bitmap in SlideOutput.ToBitmaps(deck, dpi, pages, cancellationToken))
        {
            using (bitmap)
            {
                if (WritePicture(step, bitmap, dpi, extension, pages.Count > 1 ? $"_p{pages[done] + 1}" : null) is { } path)
                    written.Add(path);
            }
            done++;
            progress?.Report(0.2 + 0.8 * done / pages.Count);
        }
        return written;
    }
}
