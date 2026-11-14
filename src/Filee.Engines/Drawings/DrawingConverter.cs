// Filee's own drawing converter (engine id "drawing"): drawings read into the slide model (DrawingFormats: one slide
// per page) and drawn by the slide renderer, to PDF (a page per drawing page), SVG and PNG (a file per page, the PDF
// options' page range and resolution, then the image options). Formats come with their readers; with none it has no
// edges and no route goes through it.
//
// Formats whose reader draws only part of what they hold come with a check (DrawingFormat.FirstUnsupported), which
// FirstUnsupported (ICapabilityCheck) asks: their edges are Checked, so a file the reader can't draw (a binary .vsd)
// keeps the route it would take without this converter.

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using Filee.Engines.Magick;
using Filee.Engines.Slides;
using ImageMagick;

namespace Filee.Engines.Drawings;

/// <summary>Drawings → PDF, SVG and PNG through the slide renderer.</summary>
public sealed class DrawingConverter : IConverter, IImageTransformer, ICapabilityCheck
{
    /// <summary>Pixels of one page picture at most; a larger picture is drawn at a lower resolution.</summary>
    private const long MaxPagePixels = 200_000_000;

    private readonly IReadOnlyList<DrawingFormat> _formats;

    public DrawingConverter() : this(DrawingFormats.BuiltIn)
    {
    }

    /// <summary>A converter of <paramref name="formats"/> instead of the built-in ones (tests).</summary>
    internal DrawingConverter(IReadOnlyList<DrawingFormat> formats)
    {
        _formats = formats;
        Edges =
        [
            .. from format in formats
               from target in DrawingFormats.Targets
               select new ConversionEdge(format.Id, target, Checked: format.FirstUnsupported is not null),
        ];
    }

    public string Id => "drawing";
    public string DisplayName => "Drawings (built-in)";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; }

    /// <summary>Pictures of pages are drawn here, with the image options (turn, mirror, size) applied.</summary>
    public bool Transforms(string from, string to) => to is not ("pdf" or "svg");

    public EngineStatus GetStatus() => EngineStatus.Available(version: EngineVersions.BuiltIn);

    /// <summary>The first thing in the file its format's reader can't draw, or null.</summary>
    public string? FirstUnsupported(string sourcePath, string sourceFormat) =>
        Find(sourceFormat)?.FirstUnsupported?.Invoke(sourcePath);

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            var format = Find(step.From) ?? throw new NotSupportedException($"The drawing converter doesn't read {step.From}.");
            var deck = format.Read(step.InputPath, cancellationToken);
            if (deck.Slides.Count == 0)
                throw new InvalidDataException("The drawing has no pages.");
            progress?.Report(0.2);
            if (step.To == "pdf")
            {
                var pdf = SlideOutput.ToPdf(deck, progress is null ? null : new Progress<double>(p => progress.Report(0.2 + 0.8 * p)), cancellationToken);
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
        var dpi = (float)Math.Clamp(step.Preset.Pdf.RenderDpi, 36, 1200);
        // A large page at a high resolution would take gigabytes: the resolution comes down to fit.
        var inches = deck.Width / 72.0 * (deck.Height / 72.0);
        dpi = (float)Math.Min(dpi, Math.Sqrt(MaxPagePixels / Math.Max(inches, 1e-6)));
        var (_, extension) = ImageEncoder.OutputFormat(step.To, step.Preset.Image);
        var written = new List<string>();
        var done = 0;
        foreach (var bitmap in SlideOutput.ToBitmaps(deck, dpi, pages, cancellationToken))
        {
            using (bitmap)
            using (var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
            using (var image = new MagickImage(data.ToArray()))
            {
                image.Density = new Density(dpi, dpi, DensityUnit.PixelsPerInch);
                ImageEncoder.ApplyOptions(image, step.Preset.Image, step.To);
                if (step.Output.Allocate(extension, pages.Count > 1 ? $"_p{pages[done] + 1}" : null) is { } path)
                {
                    ImageEncoder.WriteImage(image, path, step.To);
                    written.Add(path);
                }
            }
            done++;
            progress?.Report(0.2 + 0.8 * done / pages.Count);
        }
        return written;
    }
}
