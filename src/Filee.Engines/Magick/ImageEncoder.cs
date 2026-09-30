// Shared ImageMagick logic: applies preset ImageOptions and writes frames in the requested format.
// Used by MagickImageConverter and by PdfiumConverter (PDF pages → images).

using Filee.Core.Conversion;
using Filee.Core.Presets;
using ImageMagick;

namespace Filee.Engines.Magick;

internal static class ImageEncoder
{
    /// <summary>
    /// Image formats ImageMagick can read (see <see cref="ImageReader"/> for RAW, layered and metafile sources).
    /// Camera RAW is decoded by LibRaw, XCF and HEIC are read only. EMF/WMF are Windows metafiles, which ImageMagick
    /// renders through GDI+ on Windows only (<see cref="Metafiles"/>).
    /// </summary>
    public static readonly string[] Readable =
    [
        "png", "jpg", "webp", "tiff", "bmp", "gif", "ico", "avif", "heic",
        "jxl", "jp2", "psd", "psb", "tga", "ppm", "xcf", "raw",
    ];

    /// <summary>Vector metafiles ImageMagick rasterizes (Windows only: it uses GDI+).</summary>
    public static readonly string[] Metafiles = ["emf", "wmf"];

    /// <summary>
    /// Image formats ImageMagick can write. HEIC is missing on purpose: the bundled build has no HEVC encoder
    /// (patent licensing), so we offer AVIF instead.
    /// </summary>
    public static readonly string[] Writable =
    [
        "png", "jpg", "webp", "tiff", "bmp", "gif", "ico", "avif",
        "jxl", "jp2", "psd", "psb", "tga", "ppm",
    ];

    /// <summary>Formats that store several frames/pages in one file.</summary>
    private static readonly HashSet<string> MultiFrame = ["tiff", "gif"];

    /// <summary>Formats without an alpha channel: transparent pixels are flattened onto the background colour.</summary>
    private static readonly HashSet<string> NoAlpha = ["jpg", "ppm"];

    /// <summary>Every format ImageMagick reads as input: the raster formats plus, on Windows, the metafiles.</summary>
    public static IEnumerable<string> Sources =>
        OperatingSystem.IsWindows() ? [.. Readable, .. Metafiles] : Readable;

    public static MagickFormat ToMagickFormat(string formatId) => formatId switch
    {
        "png" => MagickFormat.Png,
        "jpg" => MagickFormat.Jpeg,
        "webp" => MagickFormat.WebP,
        "tiff" => MagickFormat.Tiff,
        "bmp" => MagickFormat.Bmp,
        "gif" => MagickFormat.Gif,
        "ico" => MagickFormat.Icon,
        "avif" => MagickFormat.Avif,
        "heic" => MagickFormat.Heic,
        "jxl" => MagickFormat.Jxl,
        "jp2" => MagickFormat.Jp2,
        "psd" => MagickFormat.Psd,
        "psb" => MagickFormat.Psb,
        "tga" => MagickFormat.Tga,
        "ppm" => MagickFormat.Ppm,
        _ => throw new NotSupportedException($"ImageMagick cannot handle '{formatId}'."),
    };

    /// <summary>
    /// ImageMagick format and file extension for writing <paramref name="targetFormat"/>. The "ppm" format covers the
    /// Netpbm family: a grayscale result, or a .pgm/.pbm source kept as such, is written as PGM/PBM with its own extension.
    /// </summary>
    /// <param name="sourcePath">Input file of the step, used to keep the Netpbm sub-format of .pgm/.pbm sources.</param>
    public static (MagickFormat Format, string Extension) OutputFormat(string targetFormat, ImageOptions options, string? sourcePath = null)
    {
        if (targetFormat != "ppm")
            return (ToMagickFormat(targetFormat), targetFormat);
        var sourceExtension = sourcePath is null ? "" : Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
        return sourceExtension switch
        {
            "pbm" => (MagickFormat.Pbm, "pbm"),
            "pgm" => (MagickFormat.Pgm, "pgm"),
            _ when options.Grayscale => (MagickFormat.Pgm, "pgm"),
            _ => (MagickFormat.Ppm, "ppm"),
        };
    }

    /// <summary>
    /// Chooses which frames of a (possibly multi-frame) source are converted.
    /// ICO: the largest icon. GIF → single-frame target: the first frame. PSD/PSB: the composite (frame 0; any
    /// further frames are layers). Otherwise: all frames/pages.
    /// </summary>
    public static List<IMagickImage<byte>> SelectFrames(MagickImageCollection images, string sourceFormat, string targetFormat)
    {
        if (sourceFormat == "gif")
            images.Coalesce(); // GIF frames can be partial; coalescing makes every frame a full image

        var frames = images.ToList();
        if (frames.Count <= 1)
            return frames;
        if (sourceFormat is "psd" or "psb")
            return [frames[0]];
        if (sourceFormat == "ico")
            return [frames.OrderByDescending(f => (long)f.Width * f.Height).First()];
        if (sourceFormat == "gif" && !MultiFrame.Contains(targetFormat))
            return [frames[0]];
        return frames;
    }

    /// <summary>Applies resize / colour / metadata options to one frame.</summary>
    public static void ApplyOptions(IMagickImage<byte> image, ImageOptions options, string targetFormat)
    {
        image.AutoOrient();

        switch (options.Resize)
        {
            case ResizeMode.Percent when options.ResizePercent is > 0 and not 100:
                image.Resize(new Percentage(options.ResizePercent));
                break;
            case ResizeMode.LongEdge when options.LongEdge > 0:
                // Greater = only shrink images that are larger than the box, never upscale.
                image.Resize(new MagickGeometry((uint)options.LongEdge, (uint)options.LongEdge) { Greater = true });
                break;
        }

        if (options.Grayscale)
            image.Grayscale();

        if (options.Dpi is > 0)
            image.Density = new Density(options.Dpi.Value, options.Dpi.Value, DensityUnit.PixelsPerInch);

        if (!options.KeepMetadata)
            image.Strip();

        if (NoAlpha.Contains(targetFormat) && image.HasAlpha)
        {
            image.BackgroundColor = ParseColor(options.Background);
            image.Alpha(AlphaOption.Remove);
        }

        var quality = (uint)Math.Clamp(options.Quality, 1, 100);
        switch (targetFormat)
        {
            case "jpg":
            case "avif":
                image.Quality = quality;
                break;
            case "jxl":
            case "jp2":
                // 100 means lossless for both encoders (JPEG XL distance 0, JPEG 2000 reversible 5/3 wavelet).
                image.Quality = quality;
                break;
            case "webp":
                image.Quality = quality;
                if (options.WebpLossless)
                    image.Settings.SetDefine(MagickFormat.WebP, "lossless", true);
                break;
            case "tiff":
                image.Settings.Compression = options.TiffCompression switch
                {
                    TiffCompression.Lzw => CompressionMethod.LZW,
                    TiffCompression.Zip => CompressionMethod.Zip,
                    _ => CompressionMethod.NoCompression,
                };
                break;
            case "ico":
                PrepareIcon(image, options);
                break;
            case "psd":
            case "psb":
                // ImageMagick writes a broken layer section for palette images with transparency (as read from
                // PNG or GIF): store them as direct colour.
                image.ClassType = ClassType.Direct;
                break;
        }
    }

    /// <summary>
    /// Writes frames using the allocator. Multi-frame targets get one file; other targets get one file per frame
    /// with a <c>_p{n}</c> suffix when there is more than one frame.
    /// </summary>
    /// <param name="options">Options of the preset (the Netpbm sub-format depends on them).</param>
    /// <param name="sourcePath">Input file of the step, see <see cref="OutputFormat"/>.</param>
    public static List<string> Write(IReadOnlyList<IMagickImage<byte>> frames, string targetFormat, IOutputAllocator output,
        ImageOptions? options = null, string? sourcePath = null)
    {
        var written = new List<string>();
        var (format, extension) = OutputFormat(targetFormat, options ?? new ImageOptions(), sourcePath);

        if (frames.Count > 1 && MultiFrame.Contains(targetFormat))
        {
            var path = output.Allocate(extension);
            if (path is null)
                return written;
            using var collection = new MagickImageCollection();
            foreach (var frame in frames)
                collection.Add(frame.Clone());
            collection.Write(path, format);
            written.Add(path);
            return written;
        }

        for (var i = 0; i < frames.Count; i++)
        {
            var path = output.Allocate(extension, frames.Count > 1 ? $"_p{i + 1}" : null);
            if (path is null)
                continue;
            frames[i].Write(path, format);
            written.Add(path);
        }
        return written;
    }

    private static void PrepareIcon(IMagickImage<byte> image, ImageOptions options)
    {
        // Icons must be square: pad the shorter side with transparency, then let ImageMagick
        // generate every requested size inside one .ico file.
        var side = Math.Max(image.Width, image.Height);
        if (image.Width != image.Height)
        {
            image.BackgroundColor = MagickColors.Transparent;
            image.Extent(side, side, Gravity.Center);
        }
        if (side > 256)
            image.Resize(256, 256);

        var sizes = options.IcoSizes.Where(s => s is > 0 and <= 256).Distinct().OrderDescending().ToList();
        if (sizes.Count == 0)
            sizes = [256, 48, 32, 16];
        image.Settings.SetDefine(MagickFormat.Icon, "auto-resize", string.Join(',', sizes));
    }

    private static MagickColor ParseColor(string hex)
    {
        try
        {
            return new MagickColor(string.IsNullOrWhiteSpace(hex) ? "#FFFFFF" : hex);
        }
        catch (ArgumentException)
        {
            return new MagickColor("#FFFFFF");
        }
    }
}
