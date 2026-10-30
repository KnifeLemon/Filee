// "Edit pictures" (#584) runs as an ordinary conversion once its window has the user's choices: the tool preset becomes a
// conversion with that format, size, turn and colour, so the job queue, the toast, cancelling, the output rules and the
// history work as for any other file. The choices are kept in the tool, so the window opens with them the next time.

using Filee.Core.Conversion;
using Filee.Core.Formats;

namespace Filee.Core.Presets;

/// <summary>Turns the "Edit pictures" tool into the conversions that write the edited pictures.</summary>
public static class ImageEditTool
{
    /// <summary>Where a picture goes when its own format can't be written ("Same format" for a camera RAW, a DICOM …).</summary>
    public const string FallbackTarget = "png";

    /// <summary>The formats the window offers, after "Same format as the source": the everyday pictures.</summary>
    public static readonly IReadOnlyList<string> Targets = ["png", "jpg", "webp", "avif", "heic", "gif", "bmp", "tiff", "ico"];

    /// <summary>Formats the quality slider is shown for.</summary>
    public static readonly IReadOnlySet<string> LossyTargets = new HashSet<string> { "jpg", "webp", "avif", "heic" };

    /// <summary>
    /// The resizes the window offers, named rather than every <see cref="ResizeMode"/>: a mode added later needs fields
    /// of its own in the window before it can be offered there. <see cref="ResizeMode.Custom"/> is the free width ×
    /// height, in pixels, cm, mm or inches (#725).
    /// </summary>
    public static readonly IReadOnlyList<ResizeMode> ResizeModes = [ResizeMode.None, ResizeMode.Percent, ResizeMode.LongEdge, ResizeMode.Width, ResizeMode.Custom];

    /// <summary>True when a <paramref name="sourceFormat"/> file is a picture the tool can open.</summary>
    public static bool CanEdit(string sourceFormat) =>
        FormatRegistry.FindById(sourceFormat)?.Category == FormatCategory.Image;

    /// <summary>
    /// The <see cref="Targets"/> at least one of the <paramref name="sourceFormats"/> can be written in with the engines
    /// there are: HEIC only once FFmpeg is downloaded, as the donut's HEIC preset.
    /// </summary>
    public static IReadOnlyList<string> TargetsFrom(IEnumerable<string> sourceFormats, RoutePlanner planner)
    {
        var sources = sourceFormats.Distinct().ToList();
        return [.. Targets.Where(target => sources.Any(source => planner.Plan(source, target) is not null))];
    }

    /// <summary>
    /// The format a <paramref name="sourceFormat"/> picture is written in for the <paramref name="chosen"/> one: as
    /// chosen, but for "Same format as the source" one that can't be written straight from it becomes
    /// <see cref="FallbackTarget"/>.
    /// </summary>
    public static string TargetFor(string sourceFormat, string chosen, RoutePlanner planner) =>
        chosen != BuiltInData.SameAsSource
            ? chosen
            : FormatRegistry.FindById(sourceFormat) is { Writable: true } && planner.Plan(sourceFormat, sourceFormat) is not null
                ? BuiltInData.SameAsSource
                : FallbackTarget;

    /// <summary>
    /// The conversion that writes pictures as <paramref name="targetFormat"/> with <paramref name="image"/>'s options
    /// and the tool's name and output rule. The tool itself is left as it is.
    /// </summary>
    public static Preset ConversionOf(Preset tool, string targetFormat, ImageOptions image)
    {
        var preset = tool.Clone();
        preset.Kind = PresetKind.Convert;
        preset.Tool = null;
        preset.SubPresetIds = [];
        preset.TargetFormat = targetFormat;
        preset.Image = image.Clone();
        return preset;
    }

    /// <summary>
    /// Keeps the window's choices in <paramref name="tool"/> for the next time: its format and the picture options the
    /// window shows. Other options (metadata, ICO sizes …) stay as the tool has them.
    /// </summary>
    public static void Remember(Preset tool, string targetFormat, ImageOptions image)
    {
        tool.TargetFormat = targetFormat;
        var kept = tool.Image;
        kept.Resize = image.Resize;
        kept.ResizePercent = image.ResizePercent;
        kept.LongEdge = image.LongEdge;
        kept.Width = image.Width;
        kept.CustomSize = image.Clone().CustomSize;
        kept.MinShortEdge = image.MinShortEdge;
        kept.Rotation = image.Rotation;
        kept.Flip = image.Flip;
        kept.Grayscale = image.Grayscale;
        kept.Quality = image.Quality;
    }
}
