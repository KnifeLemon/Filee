// Settings → Security and limits for pictures: the largest picture Filee opens (megapixels, checked from the file's
// header before anything is decoded, so an "image bomb" of a few kilobytes that unpacks to gigapixels is refused), the
// longest side, and how much memory and disk ImageMagick may use. ImageMagick's own limits are process-wide.

using Filee.Core.Conversion;
using ImageMagick;

namespace Filee.Engines.Magick;

/// <summary>Resource limits for ImageMagick and the picture size check.</summary>
public static class MagickLimits
{
    // ImageMagick's defaults, to go back to when a limit is set to "automatic" again.
    private static readonly ulong DefaultWidth = ResourceLimits.Width;
    private static readonly ulong DefaultHeight = ResourceLimits.Height;
    private static readonly ulong DefaultMemory = ResourceLimits.Memory;
    private static readonly ulong DefaultDisk = ResourceLimits.Disk;

    /// <summary>Formats whose size is chosen when they are drawn (the preset's DPI), not stored in the file.</summary>
    private static readonly HashSet<string> Drawn = new(StringComparer.OrdinalIgnoreCase)
    {
        "svg", "svgz", "pdf", "eps", "ps", "ai", "emf", "wmf", "emz", "wmz",
    };

    /// <summary>Largest picture opened, in megapixels; 0 for no limit.</summary>
    public static int MaxMegapixels { get; private set; }

    /// <summary>Applies the limits (each 0 for ImageMagick's default, or no limit of Filee's own).</summary>
    public static void Apply(int maxMegapixels, int maxSide, int memoryMb, int diskMb)
    {
        MaxMegapixels = Math.Max(0, maxMegapixels);
        ResourceLimits.Width = maxSide > 0 ? (ulong)maxSide : DefaultWidth;
        ResourceLimits.Height = maxSide > 0 ? (ulong)maxSide : DefaultHeight;
        ResourceLimits.Memory = memoryMb > 0 ? (ulong)memoryMb << 20 : DefaultMemory;
        ResourceLimits.Disk = diskMb > 0 ? (ulong)diskMb << 20 : DefaultDisk;
    }

    /// <summary>
    /// Refuses <paramref name="path"/> when one of its pictures is larger than <see cref="MaxMegapixels"/>, reading only
    /// the header. A file whose header can't be read is left to the reader, which reports it in its own words.
    /// </summary>
    /// <exception cref="ConversionRefusedException"><c>error.image_too_large</c>.</exception>
    public static void Check(string path, string formatId, IMagickReadSettings<byte>? settings = null) =>
        Check(path, formatId, settings, MaxMegapixels);

    /// <summary><see cref="Check(string, string, IMagickReadSettings{byte})"/> with a given limit (tests: the real one is process-wide).</summary>
    internal static void Check(string path, string formatId, IMagickReadSettings<byte>? settings, int maxMegapixels)
    {
        if (maxMegapixels <= 0 || Drawn.Contains(formatId))
            return;
        IEnumerable<IMagickImageInfo> pictures;
        try
        {
            pictures = (settings is null ? MagickImageInfo.ReadCollection(path) : MagickImageInfo.ReadCollection(path, settings)).ToList();
        }
        catch (MagickException)
        {
            return;
        }
        foreach (var picture in pictures)
            CheckSize(picture.Width, picture.Height, maxMegapixels);
    }

    /// <summary>Refuses a picture of <paramref name="width"/> × <paramref name="height"/> over <see cref="MaxMegapixels"/>.</summary>
    /// <exception cref="ConversionRefusedException"><c>error.image_too_large</c>.</exception>
    public static void CheckSize(ulong width, ulong height) => CheckSize(width, height, MaxMegapixels);

    internal static void CheckSize(ulong width, ulong height, int maxMegapixels)
    {
        if (maxMegapixels <= 0)
            return;
        var megapixels = (double)width * height / 1_000_000;
        if (megapixels > maxMegapixels)
            throw new ConversionRefusedException("error.image_too_large",
                $"{width} × {height} = {megapixels:0} MP > {maxMegapixels} MP");
    }
}
