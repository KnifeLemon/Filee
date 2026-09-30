// Raster image ↔ raster image conversion with ImageMagick (Magick.NET, Apache-2.0).

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using ImageMagick;

namespace Filee.Engines.Magick;

/// <summary>Converts between PNG, JPG, WEBP, TIFF, BMP, GIF, ICO, AVIF and HEIC (read only).</summary>
public sealed class MagickImageConverter : IConverter
{
    public string Id => "magick";
    public string DisplayName => "ImageMagick";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
        (from source in ImageEncoder.Readable
         from target in ImageEncoder.Writable
         select new ConversionEdge(source, target)).ToList();

    public EngineStatus GetStatus() =>
        EngineStatus.Available(MagickNET.ImageMagickVersion,
            $"{EngineVersions.Library("Magick.NET", typeof(MagickImage))} · ImageMagick {ImageMagickNumber()}");

    /// <summary>"7.1.2-5" out of "ImageMagick 7.1.2-5 Q8 x64 …".</summary>
    private static string ImageMagickNumber() =>
        MagickNET.ImageMagickVersion.Split(' ').FirstOrDefault(part => part.Length > 0 && char.IsAsciiDigit(part[0])) ?? "?";

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            using var images = new MagickImageCollection(step.InputPath);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.3);

            var frames = ImageEncoder.SelectFrames(images, step.From, step.To);
            foreach (var frame in frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ImageEncoder.ApplyOptions(frame, step.Preset.Image, step.To);
            }
            progress?.Report(0.7);

            var written = ImageEncoder.Write(frames, step.To, step.Output);
            progress?.Report(1);
            return written;
        }, cancellationToken);
}
