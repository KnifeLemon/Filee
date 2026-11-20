// ODG, OTG and FODG from what Filee reads into the slide model, without LibreOffice: OdpWriter.WriteDrawing writes the
// pages as an OpenDocument drawing (the ODP writer's shapes, text, pictures and tables in office:drawing), a page per
// slide; the template and the flat file are that drawing turned into them (Odf/OdfVariants).
//
// - Drawing formats read into the slide model (DrawingFormats: Visio, whiteboards), except the ODG forms themselves,
//   which become each other through OdfVariantConverter. A format with a check keeps it here.
// - SVG and SVGZ (SvgDrawingReader): shapes, text and data: pictures stay editable; a file with what that reader doesn't
//   draw (filters, masks, CSS, ...) is checked off to LibreOffice Draw, whose SVG import draws more.
// - EMF and WMF: the metafile as it is, one picture filling a page of its size (LibreOffice Draw opens one the same way).
// - Mind maps (MindMaps/): each sheet's map as drawn (topics as shapes with their text, branch lines), a page per sheet
//   on pages of the largest sheet's size, each map centred on its page.
// - Slide decks (SlideFormats: PowerPoint, OpenDocument presentations), with the slide renderer's check; speaker notes
//   are not kept (drawings have none).
// - PDF: each page rebuilt as editable text boxes, shapes, tables and pictures by the PDF slides engine (or as a
//   picture with the "slides as pictures" option), read back from its PPTX. This edge alone is no direct-only one, so
//   what Filee draws into a PDF of its own (XPS, chart templates, documents) reaches ODG through it, as LibreOffice's
//   route through its PDF import did.
//
// Costs: the ODP writer's (8), below LibreOffice's fallback (25) whatever the engine order adds; an SVG checked off goes
// to LibreOffice where it is installed (SVG → PDF → ODG costs more than LibreOffice's own edge), else through PDF here.

using Filee.Core.Conversion;
using Filee.Engines.Hwp.Hwpx.Markup;
using Filee.Engines.Metafiles;
using Filee.Engines.MindMaps;
using Filee.Engines.Odf;
using Filee.Engines.Odf.Draw;
using Filee.Engines.Office;
using Filee.Engines.Pdf;
using Filee.Engines.Slides;
using SkiaSharp;

namespace Filee.Engines.Drawings;

public sealed partial class DrawingConverter
{
    /// <summary>The ODG forms written: the drawing, its template and its flat file.</summary>
    internal static readonly string[] OdgTargets = ["odg", "otg", "fodg"];

    /// <summary>The ODP writer's cost: below LibreOffice's fallback (25) whatever the engine order adds (at most 9).</summary>
    internal const int OdgCost = OdpWriterConverter.Cost;

    /// <summary>Formats written as an ODG through readers of their own here: SVG, the metafiles and mind maps.</summary>
    private static readonly string[] VectorSources = ["svg", "svgz", "emf", "wmf", MindMapReader.FreeMind, MindMapReader.XMind];

    /// <summary>The edges to ODG: of the slide-model formats among <paramref name="formats"/>, and with <paramref name="others"/> of the other sources too.</summary>
    private static IEnumerable<ConversionEdge> OdgEdges(IReadOnlyList<DrawingFormat> formats, bool others)
    {
        foreach (var format in formats.Where(f => f.Read is not null && !OdfDrawReader.Drawings.Contains(f.Id)))
            foreach (var to in OdgTargets)
                yield return new ConversionEdge(format.Id, to, OdgCost, DirectOnly: true, Checked: format.FirstUnsupported is not null);
        if (!others)
            yield break;
        foreach (var from in VectorSources)
            foreach (var to in OdgTargets)
                yield return new ConversionEdge(from, to, OdgCost, DirectOnly: true, Checked: from is "svg" or "svgz");
        foreach (var from in SlideFormats.Decks)
            foreach (var to in OdgTargets)
                yield return new ConversionEdge(from, to, OdgCost, DirectOnly: true, Checked: true);
        yield return new ConversionEdge("pdf", "odg", OdgCost);
    }

    private static bool IsOdg(string format) => OdgTargets.Contains(format, StringComparer.OrdinalIgnoreCase);

    /// <summary>The check of a source that is no drawing format: SVG's, a deck's; null for the others.</summary>
    private static string? FirstUnsupportedForOdg(string sourcePath, string sourceFormat) => sourceFormat switch
    {
        "svg" or "svgz" => SvgDrawingReader.FirstUnsupported(sourcePath),
        _ when SlideFormats.Decks.Contains(sourceFormat, StringComparer.OrdinalIgnoreCase) => SlideFormats.FirstUnsupported(sourcePath, sourceFormat),
        _ => null,
    };

    /// <summary>The source read into pages and written as an ODG, OTG or FODG.</summary>
    private List<string> WriteOdg(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var deck = ReadForOdg(step, progress, cancellationToken);
        if (deck.Slides.Count == 0)
            throw new InvalidDataException("The drawing has no pages.");
        if (deck.LeftOut.Count > 0)
            step.LeftOut?.Invoke(new LeftOutNotice("notice.renderer_left_out", deck.LeftOut));
        progress?.Report(0.7);
        if (step.Output.Allocate(step.To) is not { } output)
            return [];
        if (step.To == "odg")
        {
            OdpWriter.WriteDrawing(deck, output, cancellationToken: cancellationToken);
        }
        else
        {
            // The template or the flat file: the drawing written first, then turned into it.
            var odg = Path.Combine(step.WorkDirectory, $"drawing-{Guid.NewGuid():N}.odg");
            try
            {
                OdpWriter.WriteDrawing(deck, odg, cancellationToken: cancellationToken);
                OdfVariants.Convert(odg, "odg", output, step.To, cancellationToken: cancellationToken);
            }
            finally
            {
                File.Delete(odg);
            }
        }
        progress?.Report(1);
        return [output];
    }

    private SlideDeck ReadForOdg(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (Find(step.From) is { Read: { } read })
            return read(step.InputPath, cancellationToken);
        return step.From switch
        {
            "svg" or "svgz" => SvgDrawingReader.Read(step.InputPath, cancellationToken: cancellationToken),
            "emf" or "wmf" => MetafilePage(step.InputPath),
            MindMapReader.FreeMind or MindMapReader.XMind => MindMap(step.InputPath, step.From, cancellationToken),
            "pdf" => PdfPages(step, progress, cancellationToken),
            _ when SlideFormats.Decks.Contains(step.From, StringComparer.OrdinalIgnoreCase) => SlideFormats.Read(step.InputPath, step.From, cancellationToken),
            _ => throw new NotSupportedException($"The drawing converter doesn't write {step.From} as ODG."),
        };
    }

    /// <summary>A metafile as one picture filling a page of its own size (200 inches a side at most, its shape kept).</summary>
    internal static SlideDeck MetafilePage(string path, MetafileLimits? limits = null)
    {
        limits ??= MetafileLimits.Default;
        var length = new FileInfo(path).Length;
        if (length > limits.MaxFileBytes)
            throw MetafileLimits.TooLarge($"The metafile is {Documents.DocumentLimits.Megabytes(length)}, more than {Documents.DocumentLimits.Megabytes(limits.MaxFileBytes)}.");
        var data = File.ReadAllBytes(path);
        if (Metafile.Detect(data) is null)
            throw new InvalidDataException("The file is neither an EMF nor a WMF metafile.");
        if (Metafile.Size(data) is not var (width, height) || !(width > 0 && height > 0 && float.IsFinite(width) && float.IsFinite(height)))
            throw new InvalidDataException("The metafile has no usable size (its header is damaged).");
        const float MaxSide = 14_400;
        var scale = Math.Min(1, MaxSide / Math.Max(width, height));
        var (w, h) = (Math.Max(1, width * scale), Math.Max(1, height * scale));
        var outline = new SKPath();
        outline.AddRect(new SKRect(0, 0, w, h));
        var deck = new SlideDeck { Width = w, Height = h, Title = Path.GetFileNameWithoutExtension(path) };
        var slide = new Slide();
        slide.Elements.Add(new SlideShape
        {
            Bounds = new SKRect(0, 0, w, h),
            Paths = [new SlidePath(outline)],
            Fill = new PictureFill(new SlideImage(data), SKRect.Empty, SKRect.Empty),
            IsPicture = true,
        });
        deck.Slides.Add(slide);
        return deck;
    }

    /// <summary>A mind map's sheets as drawn, on pages of the largest sheet's size, each map centred on its page.</summary>
    private static SlideDeck MindMap(string path, string format, CancellationToken cancellationToken)
    {
        using var pages = MindMapPages.Open(path, format, cancellationToken);
        if (pages.Decks.Count == 0)
            throw new InvalidDataException("The .mm file is Objective-C++ source, not a mind map: it has no drawing to write.");
        if (pages.Decks.Count == 1)
            return pages.Decks[0];
        var deck = new SlideDeck { Width = pages.Decks.Max(d => d.Width), Height = pages.Decks.Max(d => d.Height), Title = pages.Title };
        foreach (var sheet in pages.Decks)
        {
            var (dx, dy) = ((deck.Width - sheet.Width) / 2, (deck.Height - sheet.Height) / 2);
            foreach (var slide in sheet.Slides)
            {
                foreach (var element in slide.Elements)
                {
                    var box = element.Bounds;
                    box.Offset(dx, dy);
                    element.Bounds = box;
                }
                deck.Slides.Add(slide);
            }
            deck.LeftOut.AddRange(sheet.LeftOut);
        }
        return deck;
    }

    /// <summary>A PDF's pages as the PDF slides engine rebuilds them (its PPTX in the work folder, read back as slides).</summary>
    private static SlideDeck PdfPages(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var pptx = Path.Combine(step.WorkDirectory, $"pages-{Guid.NewGuid():N}.pptx");
        try
        {
            PdfSlides.Wait(cancellationToken);
            try
            {
                new PdfSlidesConverter().ConvertAsync(step with { To = "pptx", Output = new WorkFile(pptx) },
                    progress is null ? null : new Progress<double>(p => progress.Report(0.6 * p)), cancellationToken).GetAwaiter().GetResult();
            }
            finally
            {
                PdfSlides.Release();
            }
            return PptxSlideReader.Read(pptx, cancellationToken: cancellationToken, leftOut: true);
        }
        finally
        {
            File.Delete(pptx);
        }
    }

    /// <summary>One file in the work folder, for a step of this converter's own.</summary>
    private sealed class WorkFile(string path) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => path;
    }
}
