// "Size limit" (#553): megabytes as Filee shows sizes and as mail and chat services count them, and the check that
// no result over a preset's limit is handed over.

using System.Globalization;
using Filee.Core.Conversion;
using Filee.Core.Formats;

namespace Filee.Core.Presets;

public static class SizeLimit
{
    /// <summary>Megabytes of 1,000,000 bytes.</summary>
    public static long Bytes(double megabytes) => (long)(megabytes * 1_000_000);

    /// <summary>A percentage (1–99) of an original file's size in bytes (#680).</summary>
    public static long Bytes(long sourceBytes, int percent) => (long)(sourceBytes * (percent / 100.0));

    /// <summary>
    /// Megabytes of 1,000,000 bytes, to the nearest byte: a minimum truncated like <see cref="Bytes(double)"/> would let a
    /// file one byte short through (1.001 MB is 1,000,999.9999… bytes as a double).
    /// </summary>
    public static long MinimumBytes(double megabytes) => (long)Math.Round(megabytes * 1_000_000, MidpointRounding.AwayFromZero);

    /// <summary>"12.3 MB > 10 MB", in the same megabytes; a picture's limit under 1 MB in KB ("61.2 KB > 50 KB").</summary>
    public static string Describe(long size, long limit, bool image = false) => image && limit < 1_000_000
        ? string.Create(CultureInfo.InvariantCulture, $"{size / 1e3:0.##} KB > {limit / 1e3:0.##} KB")
        : string.Create(CultureInfo.InvariantCulture, $"{size / 1e6:0.##} MB > {limit / 1e6:0.##} MB");

    /// <summary>The limit for a video target in bytes; 0 = none. Can come from <see cref="MediaOptions.MaxSizePercent"/> or <see cref="MediaOptions.MaxSizeMb"/>.</summary>
    public static long ForVideo(MediaOptions media, string? sourcePath = null)
    {
        if (media.MaxSizePercent > 0)
        {
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                throw new ConversionRefusedException("error.over_size_limit");
            return Bytes(new FileInfo(sourcePath).Length, media.MaxSizePercent);
        }
        if (media.MaxSizeMb > 0)
            return Bytes(media.MaxSizeMb);
        return 0;
    }

    /// <summary>The limit for a video target in bytes; 0 = none. For joined sources, percentages apply to their combined size.</summary>
    public static long ForVideo(MediaOptions media, IEnumerable<string>? sourcePaths)
    {
        if (media.MaxSizePercent > 0)
        {
            if (sourcePaths is null)
                throw new ConversionRefusedException("error.over_size_limit");
            long total = 0;
            var any = false;
            foreach (var path in sourcePaths)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    throw new ConversionRefusedException("error.over_size_limit");
                total += new FileInfo(path).Length;
                any = true;
            }
            if (!any)
                throw new ConversionRefusedException("error.over_size_limit");
            return Bytes(total, media.MaxSizePercent);
        }
        if (media.MaxSizeMb > 0)
            return Bytes(media.MaxSizeMb);
        return 0;
    }

    /// <summary>The limit for a video target in bytes; 0 = none.</summary>
    public static long ForVideo(MediaOptions media, long sourceBytes)
    {
        if (media.MaxSizePercent > 0)
        {
            if (sourceBytes <= 0)
                throw new ConversionRefusedException("error.over_size_limit");
            return Bytes(sourceBytes, media.MaxSizePercent);
        }
        if (media.MaxSizeMb > 0)
            return Bytes(media.MaxSizeMb);
        return 0;
    }

    /// <summary>The limit for a result of <paramref name="path"/>'s kind: pictures and videos have their own; 0 = none.</summary>
    public static long For(Preset preset, string path, string? sourcePath = null) => FormatRegistry.Detect(path)?.Category switch
    {
        FormatCategory.Image when preset.Image.MaxSizeMb > 0 => Bytes(preset.Image.MaxSizeMb),
        FormatCategory.Video => ForVideo(preset.Media, sourcePath),
        _ => 0,
    };

    /// <summary>The limit for a result of <paramref name="path"/>'s kind: pictures and videos have their own; 0 = none.</summary>
    public static long For(Preset preset, string path, IEnumerable<string>? sourcePaths) => FormatRegistry.Detect(path)?.Category switch
    {
        FormatCategory.Image when preset.Image.MaxSizeMb > 0 => Bytes(preset.Image.MaxSizeMb),
        FormatCategory.Video => ForVideo(preset.Media, sourcePaths),
        _ => 0,
    };

    /// <summary>
    /// Refuses when a result is over its limit. The engines fit pictures and videos where they can; this catches the
    /// routes they don't (HEIC, CAD and 3D renders, animations and pages, joined videos, "Remove metadata").
    /// </summary>
    public static void Check(Preset preset, IEnumerable<string> outputs, string? sourcePath = null)
    {
        foreach (var output in outputs)
        {
            if (For(preset, output, sourcePath) is > 0 and var limit && File.Exists(output) && new FileInfo(output).Length is var size && size > limit)
                throw new ConversionRefusedException("error.over_size_limit", Describe(size, limit, IsImage(output)));
        }
    }

    /// <summary>
    /// Refuses when a result is over its limit. For joined sources, percentages apply to their combined size.
    /// </summary>
    public static void Check(Preset preset, IEnumerable<string> outputs, IEnumerable<string>? sourcePaths)
    {
        foreach (var output in outputs)
        {
            if (For(preset, output, sourcePaths) is > 0 and var limit && File.Exists(output) && new FileInfo(output).Length is var size && size > limit)
                throw new ConversionRefusedException("error.over_size_limit", Describe(size, limit, IsImage(output)));
        }
    }

    /// <summary>A picture result's minimum in bytes (#662); 0 = none, also for anything that isn't a picture.</summary>
    private static long Minimum(Preset preset, string path) =>
        preset.Image.MinSizeMb > 0 && IsImage(path) ? MinimumBytes(preset.Image.MinSizeMb) : 0;

    /// <summary>
    /// Whether a picture among <paramref name="outputs"/> stayed under the preset's minimum. Only files count: a folder
    /// of video frames is left out.
    /// </summary>
    public static bool IsUnderMinimum(Preset preset, IEnumerable<string> outputs) =>
        outputs.Any(output => Minimum(preset, output) is > 0 and var minimum && File.Exists(output) && new FileInfo(output).Length < minimum);

    private static bool IsImage(string path) => FormatRegistry.Detect(path)?.Category == FormatCategory.Image;
}
