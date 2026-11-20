// Filee's own DjVu reader (engine id "djvu", #346): DjVu documents (.djvu, .djv; one page or bundled) to PDF — every
// page at its size, the layers kept (DjvuPdfWriter), the hidden text searchable — and pictures of the pages (PNG, JPEG,
// TIFF; the PDF options' page range, the pages' own resolution, then the image options; TIFF holds all pages in one
// file). Layers Filee doesn't decode are named in the file's result (ConversionStep.LeftOut).

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using Filee.Engines.Magick;
using Filee.Engines.Ocr;
using ImageMagick;

namespace Filee.Engines.Djvu;

/// <summary>DjVu → PDF and pictures.</summary>
public sealed class DjvuConverter : IConverter, IImageTransformer
{
    /// <summary>The picture formats pages are written to.</summary>
    internal static readonly string[] PictureTargets = ["png", "jpg", "tiff"];

    public string Id => "djvu";
    public string DisplayName => "DjVu (built-in)";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        new ConversionEdge("djvu", "pdf"),
        .. PictureTargets.Select(target => ImageEncoder.Edge("djvu", target)),
    ];

    /// <summary>Pictures of pages are written here, with the image options (turn, mirror, size) applied.</summary>
    public bool Transforms(string from, string to) => to != "pdf";

    public EngineStatus GetStatus() => EngineStatus.Available(version: EngineVersions.BuiltIn);

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            using var document = DjvuDocument.Open(step.InputPath, cancellationToken: cancellationToken);
            var written = step.To == "pdf"
                ? WritePdf(step, document, progress, cancellationToken)
                : WritePictures(step, document, progress, cancellationToken);
            if (document.LeftOut > 0)
                step.LeftOut?.Invoke(new LeftOutNotice("notice.renderer_left_out", [new LeftOutPart("notice.renderer_left_out.pictures", document.LeftOut)]));
            return written;
        }, cancellationToken);

    private static List<string> WritePdf(ConversionStep step, DjvuDocument document, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var writer = new OcrPdfWriter();
        for (var i = 0; i < document.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DjvuPdfWriter.AddPage(writer, document.ReadPage(i, cancellationToken), cancellationToken);
            progress?.Report(0.95 * (i + 1) / document.Count);
        }
        if (step.Output.Allocate("pdf") is not { } target)
            return [];
        writer.Save(target);
        progress?.Report(1);
        return [target];
    }

    private static List<string> WritePictures(ConversionStep step, DjvuDocument document, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var pages = PageRange.Parse(step.Preset.Pdf.PageRange, document.Count);
        var (_, extension) = ImageEncoder.OutputFormat(step.To, step.Preset.Image);
        var written = new List<string>();
        var collected = new List<IMagickImage<byte>>();
        try
        {
            var done = 0;
            foreach (var index in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var image = Picture(document.ReadPage(index, cancellationToken), cancellationToken);
                ImageEncoder.ApplyOptions(image, step.Preset.Image, step.To);
                if (step.To == "tiff")
                {
                    collected.Add(image);
                }
                else
                {
                    using (image)
                    {
                        if (step.Output.Allocate(extension, pages.Count > 1 ? $"_p{index + 1}" : null) is { } path)
                        {
                            ImageEncoder.WriteImage(image, path, step.To);
                            written.Add(path);
                        }
                    }
                }
                done++;
                progress?.Report(0.95 * done / pages.Count);
            }
            if (collected.Count > 0)
                written.AddRange(ImageEncoder.Write(collected, step.To, step.Output, step.Preset.Image));
        }
        finally
        {
            foreach (var image in collected)
                image.Dispose();
        }
        progress?.Report(1);
        return written;
    }

    /// <summary>A page drawn at its own resolution, turned as its INFO says; grey when it has no colour.</summary>
    internal static MagickImage Picture(DjvuPage page, CancellationToken cancellationToken)
    {
        var rgb = DjvuRenderer.Compose(page, cancellationToken);
        var grey = true;
        for (var i = 0; i < rgb.Length && grey; i += 3)
            grey = rgb[i] == rgb[i + 1] && rgb[i] == rgb[i + 2];
        MagickImage image;
        if (grey)
        {
            // One channel, read as a PGM: a third of the memory of a colour picture.
            var header = System.Text.Encoding.ASCII.GetBytes($"P5\n{page.Width} {page.Height}\n255\n");
            var pgm = new byte[header.Length + (long)page.Width * page.Height];
            header.CopyTo(pgm, 0);
            for (long i = 0, o = header.Length; o < pgm.Length; i += 3, o++)
                pgm[o] = rgb[i];
            image = new MagickImage(pgm, MagickFormat.Pgm);
        }
        else
        {
            image = new MagickImage();
            image.ReadPixels(rgb, new PixelReadSettings((uint)page.Width, (uint)page.Height, StorageType.Char, PixelMapping.RGB));
        }
        image.Density = new Density(page.Dpi, page.Dpi, DensityUnit.PixelsPerInch);
        if (page.Rotation != 0)
            image.Rotate(-90 * page.Rotation);
        return image;
    }
}
