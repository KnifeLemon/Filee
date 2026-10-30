// "Merge images" (issue #645): pictures one under another or side by side in one image, with a gap and a background
// colour between them. The layout is worked out from the pictures' sizes first, so only one picture at a time (and the
// merged image) is in memory.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using ImageMagick;

namespace Filee.Engines.Magick;

internal static class ImageJoiner
{
    /// <summary>
    /// The largest merged image Filee makes, the size of the longest "Long image" (PdfiumConverter.MaxStackedPixels):
    /// about 800 MB of memory while it is put together.
    /// </summary>
    internal const long MaxPixels = Pdf.PdfiumConverter.MaxStackedPixels;

    /// <summary>
    /// Pictures read to measure them stay in memory up to this many pixels in all, for the merge: two or three photos are
    /// read once, a long series a second time.
    /// </summary>
    private const long KeptPixels = 50_000_000;

    /// <summary>Where one picture goes in the merged image, at which size.</summary>
    internal readonly record struct Placement(int X, int Y, uint Width, uint Height);

    /// <summary>The merged image's size and where each picture goes, in order.</summary>
    internal sealed record Layout(uint Width, uint Height, IReadOnlyList<Placement> Pictures);

    /// <summary>
    /// Lays out pictures of <paramref name="sizes"/> (as they look, turned) as <paramref name="options"/> say. Refuses
    /// with <c>error.merged_image_too_large</c> above <see cref="MaxPixels"/>.
    /// </summary>
    internal static Layout Plan(IReadOnlyList<(uint Width, uint Height)> sizes, ImageOptions options)
    {
        var sideBySide = options.JoinDirection == ImageJoinDirection.Horizontal;
        var gap = options.JoinSpacing;
        // "Across" is the side the pictures share (the width of a column, the height of a row), "along" the other.
        var fitted = sizes.Select(s => (Across: (double)(sideBySide ? s.Height : s.Width), Along: (double)(sideBySide ? s.Width : s.Height)))
            .ToList();
        var across = fitted.Max(s => s.Across);
        var placed = fitted.Select(s => options.JoinFit == ImageJoinFit.SameSize
                ? (Across: across, Along: Math.Max(1, Math.Round(s.Along * across / s.Across)))
                : s)
            .ToList();
        var along = placed.Sum(s => s.Along) + (double)gap * (placed.Count - 1);
        if (across * along > MaxPixels)
        {
            var (w, h) = sideBySide ? (along, across) : (across, along);
            throw new ConversionRefusedException("error.merged_image_too_large", $"{w:0} × {h:0} px");
        }

        var pictures = new List<Placement>();
        double at = 0;
        foreach (var (pictureAcross, pictureAlong) in placed)
        {
            // Centred across: only a smaller picture of "keep sizes" has room around it.
            var offset = (int)Math.Floor((across - pictureAcross) / 2);
            pictures.Add(sideBySide
                ? new Placement((int)at, offset, (uint)pictureAlong, (uint)pictureAcross)
                : new Placement(offset, (int)at, (uint)pictureAcross, (uint)pictureAlong));
            at += pictureAlong + gap;
        }
        return sideBySide ? new Layout((uint)along, (uint)across, pictures) : new Layout((uint)across, (uint)along, pictures);
    }

    /// <summary>Writes the pictures of <paramref name="inputs"/> into one <paramref name="format"/> image at <paramref name="outputPath"/>.</summary>
    internal static void Join(IReadOnlyList<(string Path, string Format)> inputs, string outputPath, string format, ImageOptions options,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // The pictures are read with the preset's picture settings (a FITS stretch, a metafile's dpi).
        var reading = new Preset { Image = options };
        var kept = new IMagickImage<byte>?[inputs.Count];
        try
        {
            var sizes = new (uint Width, uint Height)[inputs.Count];
            long keptPixels = 0;
            Density? density = null;
            for (var i = 0; i < inputs.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var picture = Read(inputs[i], reading, options);
                sizes[i] = (picture.Width, picture.Height);
                density ??= picture.Density;
                var pixels = (long)picture.Width * picture.Height;
                if (keptPixels + pixels <= KeptPixels)
                {
                    kept[i] = picture;
                    keptPixels += pixels;
                }
                else
                    picture.Dispose();
                progress?.Report(0.4 * (i + 1) / inputs.Count);
            }

            var layout = Plan(sizes, options);
            using var merged = new MagickImage(Background(options.JoinBackground), layout.Width, layout.Height);
            for (var i = 0; i < inputs.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var picture = kept[i] ?? Read(inputs[i], reading, options);
                kept[i] = null;
                var place = layout.Pictures[i];
                if (picture.Width != place.Width || picture.Height != place.Height)
                    picture.Resize(new MagickGeometry(place.Width, place.Height) { IgnoreAspectRatio = true });
                merged.Composite(picture, place.X, place.Y, CompositeOperator.Over);
                progress?.Report(0.4 + 0.4 * (i + 1) / inputs.Count);
            }
            if (density is { } first)
                merged.Density = first;

            // The pictures are turned already: the rest of the options (resize, grayscale, quality, a background for a
            // format without transparency, what the format stores) apply to the merged image.
            var rest = options.Clone();
            rest.Rotation = ImageRotation.None;
            rest.Flip = ImageFlip.None;
            ImageEncoder.ApplyOptions(merged, rest, format);
            cancellationToken.ThrowIfCancellationRequested();
            ImageEncoder.WriteImage(merged, outputPath, format);
            if (options.MaxSizeMb > 0 || options.MinSizeMb > 0)
                ImageEncoder.FitToSize(merged, outputPath, ImageEncoder.OutputFormat(format, rest).Format, SizeLimit.Bytes(options.MaxSizeMb),
                    SizeLimit.MinimumBytes(options.MinSizeMb));
            progress?.Report(1);
        }
        finally
        {
            foreach (var picture in kept)
                picture?.Dispose();
        }
    }

    /// <summary>
    /// One picture as it looks: the first page or frame (an icon's largest size), turned by its EXIF orientation and the
    /// preset, in sRGB so photos with different colour profiles match.
    /// </summary>
    private static IMagickImage<byte> Read((string Path, string Format) input, Preset reading, ImageOptions options)
    {
        using var images = ImageReader.Read(input.Path, input.Format, reading);
        if (images.Count == 0)
            throw new InvalidOperationException("The picture has no image in it.");
        var picture = ImageEncoder.SelectFrames(images, input.Format, "png")[0].Clone();
        try
        {
            picture.AutoOrient();
            ImageEncoder.Turn(picture, options);
            if (picture.GetColorProfile() is { } profile
                && profile.Description?.Contains("sRGB", StringComparison.OrdinalIgnoreCase) != true)
                picture.TransformColorSpace(ColorProfiles.SRGB);
            else if (picture.ColorSpace == ColorSpace.CMYK)
                picture.ColorSpace = ColorSpace.sRGB;
            picture.ResetPage();
            return picture;
        }
        catch
        {
            picture.Dispose();
            throw;
        }
    }

    /// <summary>The background colour: <c>#RRGGBB</c>, <see cref="ImageOptions.TransparentJoin"/>, white for anything else.</summary>
    internal static MagickColor Background(string? colour)
    {
        if (string.Equals(colour, ImageOptions.TransparentJoin, StringComparison.OrdinalIgnoreCase))
            return MagickColors.Transparent;
        try
        {
            return new MagickColor(string.IsNullOrWhiteSpace(colour) ? "#FFFFFF" : colour.Trim());
        }
        catch (ArgumentException)
        {
            return MagickColors.White;
        }
    }
}
