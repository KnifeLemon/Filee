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
    /// Camera RAW is decoded by LibRaw; XCF, HEIC, DICOM, Photo CD, OpenEXR, Radiance HDR and FITS are read only. EMF/WMF
    /// are Windows metafiles, which ImageMagick renders through GDI+ on Windows only (<see cref="Metafiles"/>). APNG is
    /// read and written by Filee's own <see cref="Apng"/> (its frames decoded and encoded by ImageMagick); FITS is read
    /// by Filee's own <see cref="Fits"/> and written to TIFF by <see cref="FitsRendering"/>.
    /// </summary>
    public static readonly string[] Readable =
    [
        "png", "jpg", "webp", "tiff", "bmp", "gif", "ico", "avif", "heic",
        "jxl", "jp2", "psd", "psb", "tga", "ppm", "xcf", "raw",
        "apng", "mng", "qoi", "dds", "pcx", "cur", "xpm", "xbm", "wbmp", "pam", "pfm", "sgi", "ras", "miff",
        "dcm", "pcd", "exr", "hdr", "fits",
    ];

    /// <summary>Vector metafiles ImageMagick rasterizes (Windows only: it uses GDI+).</summary>
    public static readonly string[] Metafiles = ["emf", "wmf"];

    /// <summary>
    /// Image formats ImageMagick can write. HEIC is missing: the bundled build has no HEVC encoder (patent
    /// licensing), so HEIC is written with the optional FFmpeg download instead (Heic/HeicWriter).
    /// </summary>
    public static readonly string[] Writable =
    [
        "png", "jpg", "webp", "tiff", "bmp", "gif", "ico", "avif",
        "jxl", "jp2", "psd", "psb", "tga", "ppm",
        "apng", "mng", "qoi", "dds", "pcx", "cur", "xpm", "xbm", "wbmp", "pam", "pfm", "sgi", "ras", "miff",
    ];

    /// <summary>Formats that store several frames/pages in one file.</summary>
    private static readonly HashSet<string> MultiFrame = ["tiff", "gif", "apng", "mng"];

    /// <summary>
    /// Formats whose frames are an animation. Between these, all frames are kept with their timing (an animated GIF
    /// becomes an animated WebP and the other way round); any other target gets the first frame.
    /// </summary>
    private static readonly HashSet<string> Animated = ["gif", "webp", "apng", "mng"];

    /// <summary>
    /// The edge an image engine offers from a picture in <paramref name="source"/> to <paramref name="target"/>. GIF
    /// holds 256 colours, so a still picture costs more there: it reaches video through a full-colour PNG instead
    /// (FFmpeg), while animations and multi-page files still go through GIF and keep their frames. Still picture → APNG
    /// costs as much, so photos never reach video as a one-frame animation. FITS → TIFF is the data for further
    /// processing (linear by default, 16 or 32 bits): it costs a little more, so FITS reaches other formats through a
    /// stretched PNG or JPG instead.
    /// </summary>
    public static ConversionEdge Edge(string source, string target) =>
        new(source, target, target is "gif" or "apng" && !MultiFrame.Contains(source) && !Animated.Contains(source) ? 12
            : source == "fits" && target == "tiff" ? 11 : 10);

    /// <summary>True when frames of <paramref name="sourceFormat"/> are written as one animation in <paramref name="targetFormat"/>.</summary>
    public static bool KeepsAnimation(string sourceFormat, string targetFormat) =>
        Animated.Contains(sourceFormat) && Animated.Contains(targetFormat);

    /// <summary>
    /// Formats written without an alpha channel: transparent pixels are flattened onto the background colour. HEIC
    /// could carry alpha as an auxiliary picture; Filee's writer (Heic/HeicWriter) does not. XBM and WBMP are 1-bit, PFM
    /// has no alpha, and the alpha planes of PCX and Sun Raster are an ImageMagick extension other programs ignore (its
    /// palette writers drop them altogether).
    /// </summary>
    private static readonly HashSet<string> NoAlpha = ["jpg", "ppm", "heic", "xbm", "wbmp", "pfm", "pcx", "ras"];

    /// <summary>False for formats Filee writes without transparency (<see cref="NoAlpha"/>): nothing to trim there.</summary>
    public static bool HoldsAlpha(string format) => !NoAlpha.Contains(format);

    /// <summary>
    /// Formats read with their ImageMagick coder named instead of guessed from the file: some extensions mean another
    /// format to ImageMagick (.rgb and .bw are headerless pixel data there, SGI here) and WBMP has no signature.
    /// </summary>
    public static readonly HashSet<string> ExplicitCoder =
        ["mng", "qoi", "dds", "pcx", "cur", "xpm", "xbm", "wbmp", "pam", "pfm", "sgi", "ras", "miff", "dcm", "pcd", "exr", "hdr"];

    /// <summary>Formats holding one picture in several sizes (icons) or mipmaps: the largest is converted.</summary>
    private static readonly HashSet<string> SizeVariants = ["ico", "cur", "dds"];

    /// <summary>Packages of a picture and more, of which the picture is read (<see cref="ImageReader"/>): an iPhone LIVP.</summary>
    public static readonly string[] Packages = ["livp"];

    /// <summary>
    /// Every format ImageMagick reads as input: the raster formats, the picture of <see cref="Packages"/> and, on
    /// Windows, the metafiles.
    /// </summary>
    public static IEnumerable<string> Sources =>
        OperatingSystem.IsWindows() ? [.. Readable, .. Packages, .. Metafiles] : [.. Readable, .. Packages];

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
        "mng" => MagickFormat.Mng,
        "qoi" => MagickFormat.Qoi,
        "dds" => MagickFormat.Dds,
        "pcx" => MagickFormat.Pcx,
        "cur" => MagickFormat.Cur,
        "xpm" => MagickFormat.Xpm,
        "xbm" => MagickFormat.Xbm,
        "wbmp" => MagickFormat.Wbmp,
        "pam" => MagickFormat.Pam,
        "pfm" => MagickFormat.Pfm,
        "sgi" => MagickFormat.Sgi,
        "ras" => MagickFormat.Sun,
        "miff" => MagickFormat.Miff,
        "dcm" => MagickFormat.Dcm,
        "pcd" => MagickFormat.Pcd,
        "exr" => MagickFormat.Exr,
        "hdr" => MagickFormat.Hdr,
        // Every frame is a PNG; Apng assembles the animation (ImageMagick would need FFmpeg for it).
        "apng" => MagickFormat.Png,
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
    /// ICO, CUR, DDS: the largest image. Animations (GIF, WebP, APNG, MNG): every frame for an animated or TIFF
    /// target, else the first frame.
    /// PSD/PSB: the composite (frame 0; any further frames are layers). Otherwise: all frames/pages.
    /// </summary>
    public static List<IMagickImage<byte>> SelectFrames(MagickImageCollection images, string sourceFormat, string targetFormat)
    {
        if (Animated.Contains(sourceFormat) && images.Count > 1)
            images.Coalesce(); // animation frames can be partial; coalescing makes every frame a full image

        var frames = images.ToList();
        if (frames.Count <= 1)
            return frames;
        if (sourceFormat is "psd" or "psb")
            return [frames[0]];
        if (SizeVariants.Contains(sourceFormat))
            return [frames.OrderByDescending(f => (long)f.Width * f.Height).First()];
        if (Animated.Contains(sourceFormat) && !MultiFrame.Contains(targetFormat) && !KeepsAnimation(sourceFormat, targetFormat))
            return [frames[0]];
        return frames;
    }

    /// <summary>
    /// The longest side a format stores: JPEG 65,535 px (65,500 keeps clear of encoder rounding), WebP 16,383 px,
    /// AVIF 16,384 px. Other formats take more than any image Filee makes.
    /// </summary>
    public static double MaxSide(string targetFormat) => targetFormat switch
    {
        "jpg" or "jpeg" => 65_500,
        "webp" => 16_383,
        "avif" => 16_384,
        _ => double.PositiveInfinity,
    };

    /// <summary>
    /// Scales an image down until both sides fit <see cref="MaxSide"/>. A 1200 × 74,673 px long image used to fail as
    /// JPG with "could not write the file" (issue #50).
    /// </summary>
    private static void FitFormat(IMagickImage<byte> image, string targetFormat)
    {
        var scale = Math.Min(1, MaxSide(targetFormat) / Math.Max(image.Width, image.Height));
        if (scale < 1)
            image.Resize((uint)Math.Max(1, Math.Floor(image.Width * scale)), (uint)Math.Max(1, Math.Floor(image.Height * scale)));
    }

    /// <summary>
    /// How much <see cref="ApplyOptions"/> resizes a width x height image before <see cref="FitFormat"/> (1 when it
    /// does not). Custom sizing with the aspect kept takes one scale for both sides; stretched, the height's factor
    /// is what a tall run of pages has to fit.
    /// </summary>
    public static double ResizeScale(double width, double height, ImageOptions options) =>
        options.CustomSize is { Width: > 0, Height: > 0 } custom
            ? custom.LockAspect
                ? Math.Min(custom.ClampedWidth / width, custom.ClampedHeight / height)
                : custom.ClampedHeight / height
            : options.ClampedMinShortEdge is { } minShortEdge
                ? MinShortEdgeScale(width, height, minShortEdge)
                : options.Resize switch
                {
                    ResizeMode.Percent when options.ResizePercent > 0 => options.ResizePercent / 100.0,
                    ResizeMode.LongEdge when options.LongEdge > 0 => Math.Min(1, options.LongEdge / Math.Max(width, height)),
                    ResizeMode.Width when options.Width > 0 => Math.Min(1, options.Width / width),
                    _ => 1,
                };

    /// <summary>
    /// The most pixels a minimum short edge enlarges a picture to: as many as the largest custom size holds. Without
    /// it a hairline strip (16 x 4000 px, short edge to 4000) would ask for a picture of 4 billion pixels.
    /// </summary>
    internal const double MaxEnlargedPixels = (double)CustomImageSize.MaxSide * CustomImageSize.MaxSide;

    /// <summary>
    /// The factor that brings the shorter edge of a width x height picture up to <paramref name="minShortEdge"/>
    /// (issue #652): 1 when it already reaches it (never below 1, so nothing shrinks), and at most what keeps the
    /// result within <see cref="MaxEnlargedPixels"/>.
    /// </summary>
    public static double MinShortEdgeScale(double width, double height, int minShortEdge)
    {
        var scale = minShortEdge / Math.Min(width, height);
        return scale <= 1 ? 1 : Math.Max(1, Math.Min(scale, Math.Sqrt(MaxEnlargedPixels / (width * height))));
    }

    /// <summary>Turns and mirrors an image as <paramref name="options"/> say (part of <see cref="ApplyOptions"/>).</summary>
    public static void Turn(IMagickImage<byte> image, ImageOptions options)
    {
        switch (options.Rotation)
        {
            case ImageRotation.Right90:
                image.Rotate(90);
                break;
            case ImageRotation.Left90:
                image.Rotate(-90);
                break;
            case ImageRotation.Rotate180:
                image.Rotate(180);
                break;
        }
        // ImageMagick's Flop mirrors left ↔ right, Flip top ↔ bottom.
        if (options.Flip == ImageFlip.Horizontal)
            image.Flop();
        else if (options.Flip == ImageFlip.Vertical)
            image.Flip();
    }

    /// <summary>
    /// Crops <paramref name="image"/> to the box around its pixels that aren't fully transparent (issue #332). Unlike
    /// ImageMagick's Trim, which trims whatever colour the corners have, only transparency counts: a picture whose content
    /// touches a corner keeps its solid border. Nothing changes without transparency, or when every pixel is transparent.
    /// </summary>
    public static void TrimTransparentBorders(IMagickImage<byte> image)
    {
        if (!image.HasAlpha)
            return;
        int width = (int)image.Width, height = (int)image.Height;
        var rgba = image.GetPixels().ToByteArray(PixelMapping.RGBA)!;
        int left = width, top = height, right = -1, bottom = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (rgba[(y * width + x) * 4 + 3] == 0)
                    continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }
        if (right < 0 || (left == 0 && top == 0 && right == width - 1 && bottom == height - 1))
            return;
        image.Crop(new MagickGeometry(left, top, (uint)(right - left + 1), (uint)(bottom - top + 1)));
        image.ResetPage();
    }

    /// <summary>Applies resize / colour / metadata options to one frame.</summary>
    public static void ApplyOptions(IMagickImage<byte> image, ImageOptions options, string targetFormat)
    {
        var turned = options.TurnsOrMirrors || image.Orientation is not (OrientationType.TopLeft or OrientationType.Undefined);
        image.AutoOrient();

        // "Pixelate / blur": the parts were drawn on the picture as it is seen, before it is turned or resized. It also
        // drops the small previews of the uncovered picture a file can carry.
        AreaHider.Apply(image, options);

        // After AutoOrient, so a phone photo turns the way it looks, not the way its pixels are stored.
        Turn(image, options);
        // The small preview kept in the EXIF data still shows the picture as it was: Explorer and photo apps that
        // use it would show it unturned. As HeicWriter does, drop it once the pixels turned.
        if (turned && image.GetExifProfile() is { } exif && exif.ThumbnailLength > 0)
        {
            exif.RemoveThumbnail();
            image.SetProfile(exif);
        }

        // A hand-edited presets.json or an imported bundle can carry sizes the editor would never write, and a
        // width of 0 or below would wrap to about 4 billion px in the (uint) casts below: such a size is skipped,
        // so the picture keeps the preset's own resize behavior (None for a Custom preset) instead. A size above the
        // editor's range is brought down to it, so a hand-edited 200000 x 200000 cannot ask for a 40-gigapixel picture.
        if (options.CustomSize is { Width: > 0, Height: > 0 } custom)
        {
            var customWidth = custom.ClampedWidth;
            var customHeight = custom.ClampedHeight;
            if (custom.LockAspect)
            {
                // The file's own shape is kept: scaled up or down to the largest size that fits inside the box. Both
                // sides come from one scale, so the aspect already matches and Resize keeps it, as it always does.
                var scale = Math.Min(customWidth / (double)image.Width, customHeight / (double)image.Height);
                if (scale != 1)
                    image.Resize((uint)Math.Max(1, Math.Round(image.Width * scale)), (uint)Math.Max(1, Math.Round(image.Height * scale)));
            }
            else if (image.Width != (uint)customWidth || image.Height != (uint)customHeight)
            {
                // No lock: exactly the asked size, stretching or squeezing the picture on purpose (issue #576). Resize
                // fits inside a box and keeps the aspect on its own, so the flag is what makes this a real stretch.
                image.Resize(new MagickGeometry((uint)customWidth, (uint)customHeight) { IgnoreAspectRatio = true });
            }
        }
        else if (options.ClampedMinShortEdge is { } minShortEdge)
        {
            // Only ever up, in proportion; a picture whose shorter edge already reaches the minimum is not touched
            // (issue #652). The default filter, as for Percent above 100 % and the locked custom size, which enlarge
            // the same way. Both sides come from one scale, so ignoring the aspect only stops ImageMagick from
            // rounding the shorter side one pixel under the minimum while it fits the box.
            var scale = MinShortEdgeScale(image.Width, image.Height, minShortEdge);
            if (scale > 1)
                image.Resize(new MagickGeometry((uint)Math.Max(1, Math.Round(image.Width * scale)), (uint)Math.Max(1, Math.Round(image.Height * scale))) { IgnoreAspectRatio = true });
        }
        else switch (options.Resize)
            {
                case ResizeMode.Percent when options.ResizePercent is > 0 and not 100:
                    image.Resize(new Percentage(options.ResizePercent));
                    break;
                case ResizeMode.LongEdge when options.LongEdge > 0:
                    // Greater = only shrink images that are larger than the box, never upscale.
                    image.Resize(new MagickGeometry((uint)options.LongEdge, (uint)options.LongEdge) { Greater = true });
                    break;
                case ResizeMode.Width when options.Width > 0 && image.Width > options.Width:
                    image.Resize((uint)options.Width, (uint)Math.Max(1, Math.Round(image.Height * (double)options.Width / image.Width)));
                    break;
            }
        FitFormat(image, targetFormat);

        // Before grayscale (a blue signature stays blue until then) and before alpha is flattened for JPG and co.
        if (options.RemoveBackground)
            BackgroundRemover.Apply(image, options.BackgroundTolerance, options.KeepEnclosedBackground);

        if (options.Grayscale)
            image.Grayscale();

        if (options.Dpi is > 0)
            image.Density = new Density(options.Dpi.Value, options.Dpi.Value, DensityUnit.PixelsPerInch);

        if (!options.KeepMetadata)
        {
            var colorProfile = options.KeepColorProfile ? image.GetColorProfile() : null;
            image.Strip();
            if (colorProfile is not null)
                image.SetProfile(colorProfile);
        }

        if (NoAlpha.Contains(targetFormat) && image.HasAlpha)
        {
            image.BackgroundColor = ParseColor(options.Background);
            image.Alpha(AlphaOption.Remove);
        }

        var quality = (uint)Math.Clamp(options.Quality, 1, 100);
        // Turning a JPEG has to re-encode it: at the photo's own quality (Magick's estimate when it read the file), not
        // above it, or a phone photo saved at 75 comes back at 95, bigger and no better.
        // "Compress image" likewise never raises a photo's quality, and "Remove metadata" keeps it as it was.
        var sourceQuality = targetFormat == "jpg" && image.Format == MagickFormat.Jpeg && image.Quality is > 0 and <= 100 ? image.Quality : 0;
        if ((options.TurnsOrMirrors || options.MaxCompression) && sourceQuality > 0)
            quality = Math.Min(quality, sourceQuality);
        if (options.KeepSourceQuality && sourceQuality > 0)
            quality = sourceQuality;
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
            case "png" when options.MaxCompression:
                // zlib level 9 with adaptive row filters: the smallest PNG ImageMagick writes, the same pixels.
                image.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
                image.Settings.SetDefine(MagickFormat.Png, "compression-filter", "5");
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
            case "cur":
                // A cursor stores at most 256 × 256 pixels.
                if (image.Width > 256 || image.Height > 256)
                    image.Resize(new MagickGeometry(256, 256));
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
    /// Writes frames using the allocator. Multi-frame targets (and animations, see <see cref="KeepsAnimation"/>) get
    /// one file; other targets get one file per frame with a <c>_p{n}</c> suffix when there is more than one frame.
    /// </summary>
    /// <param name="options">Options of the preset (the Netpbm sub-format depends on them).</param>
    /// <param name="sourcePath">Input file of the step, see <see cref="OutputFormat"/>.</param>
    /// <param name="animation">The frames are an animation: write them into one file even for a WebP target.</param>
    public static List<string> Write(IReadOnlyList<IMagickImage<byte>> frames, string targetFormat, IOutputAllocator output,
        ImageOptions? options = null, string? sourcePath = null, bool animation = false)
    {
        var written = new List<string>();
        var (format, extension) = OutputFormat(targetFormat, options ?? new ImageOptions(), sourcePath);

        if (targetFormat == "apng")
        {
            // One file, also for a single frame (a one-frame animation).
            var animationPath = output.Allocate(extension);
            if (animationPath is null)
                return written;
            Apng.Write(frames, animationPath);
            written.Add(animationPath);
            return written;
        }

        if (frames.Count > 1 && (MultiFrame.Contains(targetFormat) || animation))
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
            // Animations and multi-page files written above aren't fitted: the queue refuses them when they're too large.
            if (options is { MaxSizeMb: > 0 })
                FitToSize(frames[i], path, format, SizeLimit.Bytes(options.MaxSizeMb));
            written.Add(path);
        }
        return written;
    }

    /// <summary>A fitted picture is never made smaller than this long edge (nor than its own): below it, it's a thumbnail.</summary>
    internal const int MinFittedLongEdge = 640;

    /// <summary>
    /// Saves <paramref name="image"/> again until <paramref name="path"/> is at most <paramref name="limit"/> bytes: a
    /// lower quality first (down to 50, where a lossy format still looks fine), then fewer pixels, but never below a
    /// long edge of <see cref="MinFittedLongEdge"/>. Refuses when it still doesn't fit, rather than hand over a file
    /// that breaks the limit or a thumbnail.
    /// </summary>
    internal static void FitToSize(IMagickImage<byte> image, string path, MagickFormat format, long limit)
    {
        var size = new FileInfo(path).Length;
        if (size <= limit)
            return;
        using var copy = image.Clone();
        var lossy = format is MagickFormat.Jpeg or MagickFormat.WebP or MagickFormat.Avif or MagickFormat.Jxl or MagickFormat.Jp2;
        var floor = Math.Min(MinFittedLongEdge, Math.Max(copy.Width, copy.Height));
        while (size > limit)
        {
            if (lossy && copy.Quality > 50)
                copy.Quality = Math.Max(50, copy.Quality - 10);
            else
            {
                var longEdge = Math.Max(copy.Width, copy.Height);
                if (longEdge <= floor)
                    break;
                // The file shrinks about as the pixel count does: aim a little under the limit, but not below the floor.
                var scale = Math.Max(Math.Clamp(Math.Sqrt((double)limit / size) * 0.95, 0.3, 0.9), (double)floor / longEdge);
                copy.Resize(new MagickGeometry((uint)Math.Max(1, Math.Round(copy.Width * scale)), (uint)Math.Max(1, Math.Round(copy.Height * scale))));
            }
            copy.Write(path, format);
            size = new FileInfo(path).Length;
        }
        if (size > limit)
            throw new ConversionRefusedException("error.over_size_limit", SizeLimit.Describe(size, limit));
    }

    /// <summary>Writes one image as <paramref name="targetFormat"/> (APNG through <see cref="Apng"/>).</summary>
    public static void WriteImage(IMagickImage<byte> image, string path, string targetFormat)
    {
        if (targetFormat == "apng")
            Apng.Write([image], path);
        else
            image.Write(path, ToMagickFormat(targetFormat));
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
        // Resized on purpose to less than an icon holds ("Edit pictures": 512 × 512 → 32 × 32): the icon is that size and
        // the smaller ones asked for, not the larger sizes blown up from it.
        if (options.Resize != ResizeMode.None && side < 256)
            sizes = [(int)side, .. sizes.Where(s => s < side)];
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
