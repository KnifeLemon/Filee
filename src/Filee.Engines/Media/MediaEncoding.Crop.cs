// "Crop video" (#830): the frame cut to a ratio, centred, or to the picture inside its black bars. A few short stretches
// spread over the video go through cropdetect first and the most common box wins, so a dark scene doesn't cut into
// the picture. FFmpeg turns the frame upright before any filter, and pixels are made square first, so ratios are the
// ones the viewer sees.

using System.Globalization;
using System.Text.RegularExpressions;
using Filee.Core.Presets;

namespace Filee.Engines.Media;

/// <summary>A box in a video's frame, in pixels of the upright picture with square pixels.</summary>
internal readonly record struct CropBox(int Width, int Height, int X, int Y);

internal static partial class MediaEncoding
{
    /// <summary>Where cropdetect looks, as parts of the video's length.</summary>
    internal static readonly double[] CropDetectPoints = [0.1, 0.3, 0.5, 0.7, 0.9];

    /// <summary>Seconds cropdetect reads at each point; a video shorter than all of them is read from the start.</summary>
    internal const double CropDetectSeconds = 1;

    /// <summary>
    /// Where cropdetect starts and how long it reads each time: at <see cref="CropDetectPoints"/> of a long video, the
    /// first seconds of a short one (or one of unknown length).
    /// </summary>
    internal static IEnumerable<(double At, double Seconds)> CropDetectStretches(double? length) =>
        length is { } total && total > CropDetectSeconds * CropDetectPoints.Length * 2
            ? CropDetectPoints.Select(p => (p * total, CropDetectSeconds))
            : [(0, CropDetectSeconds * CropDetectPoints.Length)];

    /// <summary>
    /// The filters that crop a frame to <paramref name="box"/>, or to a <c>width:height</c> ratio as large as fits,
    /// centred; they end with a comma, ready for the scale filter that follows.
    /// </summary>
    internal static string CropFilter(VideoStreamInfo video, string frame, CropBox? box)
    {
        var crop = box is { } b
            ? $"crop={b.Width}:{b.Height}:{b.X}:{b.Y}"
            : VideoCrop.TryParseRatio(frame, out var w, out var h)
                ? $"crop=w='trunc(min(iw,ih*{w}/{h})/2)*2':h='trunc(min(ih,iw*{h}/{w})/2)*2'"
                : throw new ArgumentException($"Not a crop frame: '{frame}'.", nameof(frame));
        return SquarePixels(video) + crop + ",";
    }

    /// <summary>
    /// cropdetect over <paramref name="seconds"/> from <paramref name="at"/> seconds: it writes a <c>crop=w:h:x:y</c>
    /// line for each frame, at the info level.
    /// </summary>
    internal static List<string> CropDetect(string input, double at, double seconds, VideoStreamInfo video) =>
    [
        "-hide_banner", "-nostdin", "-nostats", "-v", "info",
        "-ss", ClipRange.Ffmpeg(at), "-i", input, "-t", ClipRange.Ffmpeg(seconds), "-map", "0:V:0",
        // round=2: even sizes (4:2:0); reset=0: the box grows over the stretch, so one dark frame doesn't shrink it.
        "-vf", SquarePixels(video) + "cropdetect=limit=24:round=2:reset=0", "-an", "-f", "null", "-",
    ];

    [GeneratedRegex(@"crop=(-?\d+):(-?\d+):(-?\d+):(-?\d+)")]
    private static partial Regex CropLine();

    /// <summary>
    /// The most common box in cropdetect's lines (the latest of equally common ones), sizes and offsets rounded down to
    /// even numbers; null when none has a size (a black stretch writes negative ones).
    /// </summary>
    internal static CropBox? MostCommonCrop(IEnumerable<string> lines)
    {
        var boxes = lines.Select(line => CropLine().Match(line)).Where(m => m.Success)
            .Select(m => new CropBox(Even(m.Groups[1]), Even(m.Groups[2]), Even(m.Groups[3]), Even(m.Groups[4])))
            .Where(b => b is { Width: > 0, Height: > 0, X: >= 0, Y: >= 0 })
            .ToList();
        return boxes.Count == 0 ? null : boxes.GroupBy(b => b).MaxBy(g => (g.Count(), boxes.LastIndexOf(g.Key)))!.Key;

        static int Even(Group g) => int.Parse(g.Value, CultureInfo.InvariantCulture) & ~1;
    }

    /// <summary>
    /// Width and height of the picture cropdetect sees: upright, with square pixels (<see cref="SquarePixels"/>).
    /// </summary>
    internal static (int Width, int Height) ShownSize(VideoStreamInfo video)
    {
        var (w, h) = video.IsRotated ? (video.Height, video.Width) : (video.Width, video.Height);
        var sar = video.IsRotated ? 1 / video.SampleAspect : video.SampleAspect;
        return Math.Abs(video.SampleAspect - 1) > 0.01 ? ((int)(w * sar) & ~1, h) : (w, h);
    }

    /// <summary>True when <paramref name="box"/> is the whole picture, give or take the rounding: no black bars.</summary>
    internal static bool IsWholeFrame(CropBox box, VideoStreamInfo video)
    {
        var (w, h) = ShownSize(video);
        return box.X <= 1 && box.Y <= 1 && box.Width >= w - 2 && box.Height >= h - 2;
    }
}
