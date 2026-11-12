// "Ask every time" for the compression (#1353): which drops ask, what the window offers for each kind of file, the
// preset as it runs once the user has chosen, and the arithmetic behind the size it shows. The estimate itself (reading
// the files) is Filee.Engines' CompressionEstimator.

using System.Globalization;
using Filee.Core.Formats;

namespace Filee.Core.Presets;

/// <summary>What the window sets for a dropped file: the setting that decides how small it gets.</summary>
public enum CompressionKind
{
    /// <summary>A picture saved in a lossy format: <see cref="ImageOptions.Quality"/>.</summary>
    Image,
    /// <summary>A video encoded again: <see cref="MediaOptions.Quality"/>.</summary>
    Video,
    /// <summary>Sound encoded again, from an audio or a video file: <see cref="MediaOptions.AudioBitrateKbps"/>.</summary>
    Audio,
    /// <summary>A PDF that stays a PDF: <see cref="PdfOptions.Compression"/>.</summary>
    Pdf,
}

/// <summary>The settings the window chose; only the ones of the kinds dropped matter.</summary>
/// <param name="AudioBitrateKbps">0 for the bitrate the quality picks, as in the preset editor.</param>
public sealed record CompressionChoice(int ImageQuality, MediaQuality VideoQuality, int AudioBitrateKbps, PdfCompression Pdf)
{
    /// <summary>The preset's own settings: what the window starts with.</summary>
    public static CompressionChoice Of(Preset preset) =>
        new(preset.Image.Quality, preset.Media.Quality, preset.Media.AudioBitrateKbps, preset.Pdf.Compression);
}

public static class AskedCompression
{
    /// <summary>
    /// Picture formats with a quality to choose and a size the window can estimate. Not HEIC, though the preset editor
    /// has a quality for it: Filee writes it through FFmpeg, and ImageMagick can't encode the tile the estimate rests
    /// on. A HEIC file converts with the preset's own quality, as a PNG does.
    /// </summary>
    public static readonly IReadOnlySet<string> LossyImageTargets = new HashSet<string>(["jpg", "webp", "avif"]);

    /// <summary>
    /// The preset asks for the compression of the files dropped on it (<see cref="Preset.AskCompression"/>). Not a
    /// merge: the window shows what each file comes to, which a joined result isn't; a merge keeps the preset's values.
    /// </summary>
    public static bool Asks(Preset preset) =>
        preset.AskCompression && !preset.IsGroup && !preset.IsTool
        && !preset.JoinsIntoOneMedia && !preset.JoinsImages && !(preset.TargetFormat == "pdf" && preset.Pdf.MergeIntoSingle);

    /// <summary>
    /// What the window sets for <paramref name="path"/> on <paramref name="preset"/>, or null when the preset converts
    /// it without a level to choose (a PNG stays lossless, a document isn't compressed): such a file converts as the
    /// preset says. Also a browser file, by its name.
    /// </summary>
    public static CompressionKind? KindOf(Preset preset, string path)
    {
        if (FormatRegistry.Detect(path) is not { } source)
            return null;
        var target = preset.TargetFormat == BuiltInData.SameAsSource ? source.Id : preset.TargetFormat;
        var category = FormatRegistry.FindById(target)?.Category;
        return (source.Category, category) switch
        {
            (FormatCategory.Pdf, FormatCategory.Pdf) when !preset.RecognizesTextOf("pdf") && !preset.Pdf.MakesSheets => CompressionKind.Pdf,
            (FormatCategory.Image, FormatCategory.Image) when LossyImageTargets.Contains(target) => CompressionKind.Image,
            (FormatCategory.Video, FormatCategory.Video) => CompressionKind.Video,
            (FormatCategory.Audio or FormatCategory.Video, FormatCategory.Audio) => CompressionKind.Audio,
            _ => null,
        };
    }

    /// <summary>The kinds among <paramref name="paths"/>, in the order of <see cref="CompressionKind"/>.</summary>
    public static IReadOnlyList<CompressionKind> KindsOf(Preset preset, IEnumerable<string> paths) =>
        [.. paths.Select(path => KindOf(preset, path)).OfType<CompressionKind>().Distinct().Order()];

    /// <summary>
    /// The preset as it runs: the <paramref name="choice"/>, and no asking again (a retry from history converts with
    /// the same settings). A copy; the saved preset is left alone.
    /// </summary>
    public static Preset Apply(Preset preset, CompressionChoice choice)
    {
        var copy = preset.Clone();
        copy.Image.Quality = Math.Clamp(choice.ImageQuality, 1, 100);
        copy.Media.Quality = choice.VideoQuality;
        copy.Media.AudioBitrateKbps = Math.Max(0, choice.AudioBitrateKbps);
        copy.Pdf.Compression = choice.Pdf;
        copy.AskCompression = false;
        return copy;
    }

    /// <summary>
    /// The conversions a drop makes once the window has answered: the files with a level to choose at the
    /// <paramref name="choice"/>, the others (a PNG or a HEIC on "Compress image") with the preset as it is, so a level
    /// chosen for the JPGs never reaches them. One run when every file is of one sort.
    /// </summary>
    /// <param name="output">
    /// Where every run saves, when the save dialog was asked once for the whole drop ("Ask every time", #521): the
    /// runs then go to the same place and none asks again. Null keeps the preset's own output.
    /// </param>
    public static IReadOnlyList<(IReadOnlyList<string> Files, Preset Preset)> Runs(Preset preset, CompressionChoice choice,
        IReadOnlyList<string> files, OutputRule? output = null)
    {
        var asked = files.Where(f => KindOf(preset, f) is not null).ToList();
        var others = files.Where(f => KindOf(preset, f) is null).ToList();
        var runs = new List<(IReadOnlyList<string>, Preset)>();
        if (asked.Count > 0)
            runs.Add((asked, Apply(preset, choice)));
        if (others.Count > 0)
            runs.Add((others, output is null ? preset : preset.Clone()));
        if (output is not null)
        {
            foreach (var (_, run) in runs)
                run.Output = output.Clone();
        }
        return runs;
    }

    /// <summary>
    /// Bytes of a media file of <paramref name="seconds"/> at <paramref name="kbps"/> (video and sound together), with
    /// 2 % for the container (MP4's index and headers).
    /// </summary>
    public static long MediaBytes(double seconds, double kbps) =>
        seconds > 0 && kbps > 0 ? (long)Math.Round(seconds * kbps * 1000 / 8 * 1.02) : 0;

    /// <summary>
    /// What a file of <paramref name="sourceBytes"/> estimated at <paramref name="encodedBytes"/> ends up as: at most the
    /// preset's size limit (<paramref name="limitBytes"/>, 0 for none; the encoder keeps to it) and, with "Only keep if
    /// smaller" (<paramref name="onlyIfSmaller"/>), at most the original, which is kept then.
    /// </summary>
    public static long Result(long sourceBytes, long encodedBytes, long limitBytes, bool onlyIfSmaller)
    {
        var result = limitBytes > 0 ? Math.Min(encodedBytes, limitBytes) : encodedBytes;
        return onlyIfSmaller && sourceBytes > 0 ? Math.Min(result, sourceBytes) : result;
    }

    /// <summary>How much smaller <paramref name="estimated"/> is than <paramref name="source"/>, in whole percent (0 when it isn't).</summary>
    public static int PercentSmaller(long source, long estimated) =>
        source > 0 && estimated < source ? (int)Math.Round((source - estimated) * 100.0 / source) : 0;

    /// <summary>"850 KB", "12.3 MB", "123 MB", "1.2 GB", in megabytes of 1,000,000 bytes as Filee shows sizes.</summary>
    public static string SizeText(long bytes) => bytes switch
    {
        >= 1_000_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e9:0.0} GB"),
        >= 100_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e6:0} MB"),
        >= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e6:0.0} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, Math.Round(bytes / 1e3)):0} KB"),
    };
}
