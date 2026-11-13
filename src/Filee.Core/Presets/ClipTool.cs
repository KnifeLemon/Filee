// "Cut a clip" runs as an ordinary conversion once the clip window has its part of the video (or sound): the tool preset
// becomes a conversion of that part into the file's own format (or MP4 for a video, MP3 for a sound), so the job queue,
// the toast, cancelling, the output rules and the history work as for any other file.

using Filee.Core.Conversion;
using Filee.Core.Formats;

namespace Filee.Core.Presets;

/// <summary>Turns the "Cut a clip" tool into the conversion that writes one clip.</summary>
public static class ClipTool
{
    /// <summary>Where a clip goes when the video's own format can't be written: plays everywhere.</summary>
    public const string FallbackTarget = "mp4";

    /// <summary>Where a clip of a sound goes when the sound's own format can't be written: plays everywhere.</summary>
    public const string SoundFallbackTarget = "mp3";

    /// <summary>The id of the converter that writes clips (the engine the clip window's preview reads with too).</summary>
    private const string ClipEngine = "ffmpeg";

    /// <summary>True for a sound (an MP3, a recording): no picture, only a waveform to cut.</summary>
    public static bool IsSound(string sourceFormat) => FormatRegistry.FindById(sourceFormat)?.Category == FormatCategory.Audio;

    /// <summary>
    /// The format a clip of a <paramref name="sourceFormat"/> file is written in: its own when one engine writes it
    /// straight from the file (MP4 → MP4, MKV → MKV, MP3 → MP3), else <see cref="FallbackTarget"/> for a video
    /// (RealVideo, WTV, …) or <see cref="SoundFallbackTarget"/> for a sound.
    /// </summary>
    public static string TargetFor(string sourceFormat, RoutePlanner planner) =>
        WritesInOneStep(sourceFormat, sourceFormat, planner) ? sourceFormat : IsSound(sourceFormat) ? SoundFallbackTarget : FallbackTarget;

    /// <summary>
    /// True when a clip can be cut of a <paramref name="sourceFormat"/> file: it is a video, or a sound, that can be
    /// written.
    /// </summary>
    public static bool CanCut(string sourceFormat, RoutePlanner planner) =>
        (FormatRegistry.FindById(sourceFormat)?.Category is FormatCategory.Video or FormatCategory.Audio)
        && WritesInOneStep(sourceFormat, TargetFor(sourceFormat, planner), planner);

    /// <summary>
    /// One step makes <paramref name="to"/> of <paramref name="from"/>. A sound's step must be FFmpeg's: other engines
    /// convert sounds too (MIDI, tracker modules) and know nothing of clips.
    /// </summary>
    private static bool WritesInOneStep(string from, string to, RoutePlanner planner) =>
        planner.Plan(from, to) is { Steps.Count: 1 } route && (!IsSound(from) || route.Steps[0].Converter.Id == ClipEngine);

    /// <summary>The conversion that writes one stretch of a video, as recorded (see the other overload).</summary>
    public static Preset ConversionOf(Preset tool, string targetFormat, ClipRange clip) =>
        ConversionOf(tool, targetFormat, ClipEdit.Of(clip));

    /// <summary>
    /// The conversion that writes <paramref name="clip"/> of a video as <paramref name="targetFormat"/> with the
    /// tool's name, output rule and media options (which an encoded clip is written with). The tool itself is left as
    /// it is.
    /// </summary>
    public static Preset ConversionOf(Preset tool, string targetFormat, ClipEdit clip)
    {
        var preset = tool.Clone();
        preset.Kind = PresetKind.Convert;
        preset.Tool = null;
        preset.SubPresetIds = [];
        preset.TargetFormat = targetFormat;
        // Only the clip: none of the other things a video preset can make of a video.
        preset.Media.SaveEveryFrame = false;
        preset.Media.ExtractMotionPhoto = false;
        preset.Media.LivePhoto = LivePhotoKind.None;
        preset.Media.Clip = clip.Clone();
        return preset;
    }
}
