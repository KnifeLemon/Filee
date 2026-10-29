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

    /// <summary>"12.3 MB > 10 MB", in the same megabytes.</summary>
    public static string Describe(long size, long limit) =>
        string.Create(CultureInfo.InvariantCulture, $"{size / 1e6:0.##} MB > {limit / 1e6:0.##} MB");

    /// <summary>The limit for a result of <paramref name="path"/>'s kind: pictures and videos have their own; 0 = none.</summary>
    public static long For(Preset preset, string path) => FormatRegistry.Detect(path)?.Category switch
    {
        FormatCategory.Image when preset.Image.MaxSizeMb > 0 => Bytes(preset.Image.MaxSizeMb),
        FormatCategory.Video when preset.Media.MaxSizeMb > 0 => Bytes(preset.Media.MaxSizeMb),
        _ => 0,
    };

    /// <summary>
    /// Refuses when a result is over its limit. The engines fit pictures and videos where they can; this catches the
    /// routes they don't (HEIC, CAD and 3D renders, animations and pages, joined videos, "Remove metadata").
    /// </summary>
    public static void Check(Preset preset, IEnumerable<string> outputs)
    {
        foreach (var output in outputs)
        {
            if (For(preset, output) is > 0 and var limit && File.Exists(output) && new FileInfo(output).Length is var size && size > limit)
                throw new ConversionRefusedException("error.over_size_limit", Describe(size, limit));
        }
    }
}
