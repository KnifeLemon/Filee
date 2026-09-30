// Renders PDF pages to raster images with PDFium (via PDFtoImage, MIT / PDFium BSD-3).

using Filee.Core.Conversion;
using Filee.Engines.Magick;
using ImageMagick;
using PDFtoImage;
using SkiaSharp;

// PDFtoImage marks its API for the platforms PDFium ships for (Windows, macOS, Linux, mobile).
// Filee only targets desktop platforms that are all covered.
#pragma warning disable CA1416

namespace Filee.Engines.Pdf;

/// <summary>PDF → every raster format ImageMagick writes (PNG, JPG, WEBP, TIFF, …, PSD, TGA, PPM).</summary>
public sealed class PdfiumConverter : IConverter
{
    private static readonly HashSet<string> MultiPageTargets = ["tiff", "gif"];

    public string Id => "pdfium";
    public string DisplayName => "PDFium";

    /// <summary>PDFium is not thread-safe.</summary>
    public int MaxParallelism => 1;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
        ImageEncoder.Writable.Select(target => new ConversionEdge("pdf", target)).ToList();

    public EngineStatus GetStatus() => EngineStatus.Available();

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            var pdf = File.ReadAllBytes(step.InputPath);
            var pageCount = Conversion.GetPageCount(pdf, null);
            var pages = PageRange.Parse(step.Preset.Pdf.PageRange, pageCount);
            var dpi = Math.Clamp(step.Preset.Pdf.RenderDpi, 36, 1200);
            var options = new RenderOptions(Dpi: dpi, WithAnnotations: true, WithFormFill: true, BackgroundColor: SKColors.White);
            var written = new List<string>();
            var collected = new List<IMagickImage<byte>>();
            var (format, extension) = ImageEncoder.OutputFormat(step.To, step.Preset.Image);

            try
            {
                var index = 0;
                foreach (var bitmap in Conversion.ToImages(pdf, pages, null, options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var pageNumber = pages[index] + 1;
                    index++;

                    var frame = ToMagick(bitmap, dpi);
                    ImageEncoder.ApplyOptions(frame, step.Preset.Image, step.To);

                    if (MultiPageTargets.Contains(step.To))
                    {
                        collected.Add(frame); // one multi-page file, written after the loop
                    }
                    else
                    {
                        using (frame)
                        {
                            var path = step.Output.Allocate(extension, pages.Count > 1 ? $"_p{pageNumber}" : null);
                            if (path is not null)
                            {
                                frame.Write(path, format);
                                written.Add(path);
                            }
                        }
                    }
                    progress?.Report((double)index / pages.Count);
                }

                if (collected.Count > 0)
                    written.AddRange(ImageEncoder.Write(collected, step.To, step.Output, step.Preset.Image));
            }
            finally
            {
                foreach (var frame in collected)
                    frame.Dispose();
            }
            return written;
        }, cancellationToken);

    private static MagickImage ToMagick(SKBitmap bitmap, int dpi)
    {
        using (bitmap)
        using (var data = bitmap.Encode(SKEncodedImageFormat.Png, 100))
        {
            var image = new MagickImage(data.ToArray());
            image.Density = new Density(dpi, dpi, DensityUnit.PixelsPerInch);
            return image;
        }
    }
}
