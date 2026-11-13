// Raster image ↔ raster image conversion with ImageMagick (Magick.NET, Apache-2.0). Animations → AVIF are encoded by
// FFmpeg when it is installed (Media/AvifSequenceWriter), much faster than ImageMagick's own AVIF sequence writer.

using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Engines.Infrastructure;
using Filee.Engines.Media;
using ImageMagick;

namespace Filee.Engines.Magick;

/// <summary>
/// Converts between PNG, JPG, WEBP, TIFF, BMP, GIF, ICO, AVIF, JPEG XL, JPEG 2000, PSD/PSB, TGA, PPM, APNG, MNG, QOI,
/// DDS, PCX, CUR, XPM, XBM, WBMP, PAM, PFM, SGI, Sun Raster and MIFF, reads and writes Paint.NET's PDN and GIMP's XCF
/// (Filee's own readers and writers, keeping layers between PDN, XCF and PSD), and reads HEIC, camera RAW, DICOM,
/// Photo CD, OpenEXR, Radiance HDR, FITS, animated cursors (ANI) and (on Windows) EMF/WMF.
/// </summary>
public sealed class MagickImageConverter : IConverter, ITiffMerger, IImageTransformer, IImageJoiner
{
    public string Id => "magick";
    public string DisplayName => "ImageMagick";
    public int MaxParallelism => 0;

    public bool Transforms(string from, string to) => true;

    /// <summary>
    /// Finds the FFmpeg that writes animated AVIF (<see cref="AvifSequenceWriter"/>); null leaves it to ImageMagick.
    /// Tests return null to check that route.
    /// </summary>
    internal Func<(string Ffmpeg, string Ffprobe)?> LocateFfmpeg { get; init; } = FfmpegConverter.Locate;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        .. from source in ImageEncoder.Sources
           from target in ImageEncoder.Writable
           select ImageEncoder.Edge(source, target),
        // Paint.NET's and GIMP's own files, written by Filee (PaintNetWriter, XcfWriter); layers are kept between them
        // and PSD.
        .. ImageEncoder.Sources.Where(source => source != "pdn").Select(source => ImageEncoder.Edge(source, "pdn")),
        .. ImageEncoder.Sources.Where(source => source != "xcf").Select(source => ImageEncoder.Edge(source, "xcf")),
    ];

    public EngineStatus GetStatus() =>
        EngineStatus.Available(MagickNET.ImageMagickVersion,
            $"{EngineVersions.Library("Magick.NET", typeof(MagickImage))} · ImageMagick {ImageMagickNumber()}");

    /// <summary>"7.1.2-5" out of "ImageMagick 7.1.2-5 Q8 x64 …".</summary>
    private static string ImageMagickNumber() =>
        MagickNET.ImageMagickVersion.Split(' ').FirstOrDefault(part => part.Length > 0 && char.IsAsciiDigit(part[0])) ?? "?";

    /// <summary>
    /// Writes the pages into one multi-page TIFF. Each page is flattened onto the preset's background (no alpha) at 8 bits
    /// and marked as a page of a multi-page document (SubfileType 2), so older viewers (Microsoft Office Document
    /// Imaging, fax software) show it page by page; compression is the preset's.
    /// </summary>
    public Task MergeAsync(IReadOnlyList<string> inputPaths, string outputPath, Filee.Core.Presets.ImageOptions options,
        CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var compression = options.TiffCompression switch
            {
                Filee.Core.Presets.TiffCompression.Lzw => CompressionMethod.LZW,
                Filee.Core.Presets.TiffCompression.Zip => CompressionMethod.Zip,
                _ => CompressionMethod.NoCompression,
            };
            using var pages = new MagickImageCollection();
            foreach (var path in inputPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var source = new MagickImageCollection(path);
                foreach (var frame in source)
                {
                    var page = frame.Clone();
                    page.BackgroundColor = new MagickColor(string.IsNullOrWhiteSpace(options.Background) ? "#FFFFFF" : options.Background);
                    page.Alpha(AlphaAction.Remove);
                    page.Depth = 8;
                    page.Settings.Compression = compression;
                    page.SetAttribute("tiff:subfiletype", "2");
                    pages.Add(page);
                }
            }
            pages.Write(outputPath, MagickFormat.Tiff);
        }, cancellationToken);

    public bool CanRead(string format) => ImageEncoder.Sources.Contains(format);

    public bool CanWrite(string format) => format is "pdn" or "xcf" || ImageEncoder.Writable.Contains(format);

    /// <summary>"Merge images": the pictures one under another or side by side in one image (<see cref="ImageJoiner"/>).</summary>
    public Task JoinAsync(IReadOnlyList<(string Path, string Format)> inputs, string outputPath, string format,
        Filee.Core.Presets.ImageOptions options, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => ImageJoiner.Join(inputs, outputPath, format, options, progress, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(async () =>
        {
            // An animated cursor as CUR is its first frame as stored, every size and the hotspot, unless the preset
            // changes it; then the hotspot follows the picture into the converted cursor (below).
            if (step.From == "ani" && step.To == "cur" && Ani.KeptAsItIs(step.Preset.Image) && Ani.SaveFirstCursor(step, progress) is { } cursor)
                return cursor;
            // FITS → TIFF keeps the file's depth (16 or 32 bits), which the Q8 build can't: Filee writes it.
            if (step.From == "fits" && step.To == "tiff")
                return FitsRendering.WriteTiff(step, progress, cancellationToken);
            // A program's icon as ICO is the icon itself, with the sizes drawn for it, unless the preset changes it.
            if (step.From == FormatRegistry.Program && step.To == "ico" && ProgramIcon.KeptAsItIs(step.Preset.Image))
                return ProgramIcon.Save(step, progress);
            // A Paint.NET file as PSD keeps its layers, unless the preset changes the picture.
            if (step.From == "pdn" && step.To is "psd" or "psb" && PaintNet.KeepsLayers(step.Preset.Image))
                return PaintNet.SaveLayered(step, progress, cancellationToken);
            // A PSD as a Paint.NET file keeps its layers too.
            if (step.From is "psd" or "psb" && step.To == "pdn" && PaintNet.KeepsLayers(step.Preset.Image))
                return PaintNetWriter.SaveLayered(step, progress, cancellationToken);
            // A GIMP file keeps its layers as PSD or Paint.NET, and those keep theirs as a GIMP file.
            if (step.From == "xcf" && step.To is "psd" or "psb" && PaintNet.KeepsLayers(step.Preset.Image))
                return Xcf.SaveLayeredPsd(step, progress, cancellationToken);
            if (step.From == "xcf" && step.To == "pdn" && PaintNet.KeepsLayers(step.Preset.Image))
                return Xcf.SaveLayeredPdn(step, progress, cancellationToken);
            if (step.From == "pdn" && step.To == "xcf" && PaintNet.KeepsLayers(step.Preset.Image))
                return XcfWriter.SaveFromPdn(step, progress, cancellationToken);
            if (step.From is "psd" or "psb" && step.To == "xcf" && PaintNet.KeepsLayers(step.Preset.Image))
                return XcfWriter.SaveFromPsd(step, progress, cancellationToken);
            // Animated PNGs are often saved as .png: read them as APNG, so they stay animated as GIF or WebP. An AVIF is an
            // animation only when it is an image sequence.
            var from = step.From == "png" && Apng.IsAnimated(step.InputPath) ? "apng" : AvifSequence.SourceFormat(step.From, step.InputPath);
            using var images = ImageReader.Read(step.InputPath, from, step.Preset, cancellationToken);
            var written = await WriteAsync(images, from, step, progress, cancellationToken, LocateFfmpeg);
            // From a cursor, the hotspot follows the picture into the new one (every CUR is already a cursor, its
            // hotspot at the top left).
            if (step.To == "cur" && from is "cur" or "ani" && CursorFile.HotspotFollows(step.Preset.Image)
                && (from == "ani" ? Ani.Hotspot(step.InputPath) : CursorFile.Hotspot(step.InputPath)) is { } hotspot)
            {
                foreach (var path in written)
                    CursorFile.Write(path, hotspot, step.Preset.Image);
            }
            return written;
        }, cancellationToken);

    /// <summary>
    /// The rest of a conversion once the picture is read (also for pictures another engine decoded, see
    /// Wic/WicImageConverter): the frames kept for the target, the preset's options, the files.
    /// </summary>
    /// <param name="from">Format the frames were read from (animations and icons keep different frames).</param>
    /// <param name="locateFfmpeg">The FFmpeg for animated AVIF (<see cref="AvifSequenceWriter"/>); null for ImageMagick's writer.</param>
    internal static async Task<IReadOnlyList<string>> WriteAsync(MagickImageCollection images, string from, ConversionStep step,
        IProgress<double>? progress, CancellationToken cancellationToken, Func<(string Ffmpeg, string Ffprobe)?>? locateFfmpeg = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(0.3);

        // A JPG has no transparent border to trim: re-encoding it would only cost a JPEG generation.
        if (step.Preset.Image.TrimTransparent && !ImageEncoder.HoldsAlpha(step.To))
            throw new ConversionRefusedException("error.no_transparency", step.To.ToUpperInvariant());

        var animation = ImageEncoder.KeepsAnimation(from, step.To);
        var frames = ImageEncoder.SelectFrames(images, from, step.To);
        // Before the options: frames dropped for speed aren't resized for nothing.
        if (animation && step.Preset.Image.ChangesSpeed)
            frames = AnimationTiming.ChangeSpeed(frames, step.Preset.Image.PlaybackSpeed, step.To);
        foreach (var frame in frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImageEncoder.ApplyOptions(frame, step.Preset.Image, step.To);
            // Animations aren't trimmed: each frame trimmed alone would shift. Trimming all frames to one box could be added.
            if (step.Preset.Image.TrimTransparent && !(animation && frames.Count > 1))
                ImageEncoder.TrimTransparentBorders(frame);
        }
        progress?.Report(0.7);

        if (animation && step.To == "avif" && frames.Count > 1 && locateFfmpeg is not null
            && await AvifSequenceWriter.FindAsync(locateFfmpeg) is { } ffmpeg)
        {
            var path = step.Output.Allocate(ImageEncoder.OutputFormat(step.To, step.Preset.Image, step.InputPath).Extension);
            if (path is null)
                return [];
            try
            {
                await AvifSequenceWriter.WriteAsync(ffmpeg, frames, path, step.Preset.Image.Quality, step.WorkDirectory,
                    progress, 0.7, cancellationToken);
                progress?.Report(1);
                return [path];
            }
            catch (InvalidOperationException)
            {
                // A copy of FFmpeg that can't write it (an older one whose avif muxer has no loop option, say): ImageMagick
                // still can, only slower, into the same file.
                var fallback = ImageEncoder.Write(frames, step.To, new AllocatedPath(path), step.Preset.Image, step.InputPath, animation);
                progress?.Report(1);
                return fallback;
            }
        }

        var written = ImageEncoder.Write(frames, step.To, step.Output, step.Preset.Image, step.InputPath, animation);
        progress?.Report(1);
        return written;
    }

    /// <summary>Hands out the one path already allocated for the output.</summary>
    private sealed class AllocatedPath(string path) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => path;
    }
}
