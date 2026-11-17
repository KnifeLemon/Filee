// "Crop video" (#830): the frame cut to a ratio, centred, or to the picture inside its black bars. A few short stretches
// spread over the video go through cropdetect first and the largest box wins, so a dark scene doesn't cut into the
// picture. FFmpeg turns the frame upright before any filter, and pixels are made square first, so ratios are the ones
// the viewer sees.

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

    /// <summary>The filters that crop a frame to <paramref name="box"/>; they end with a comma, ready for the scale filter that follows.</summary>
    internal static string CropFilter(VideoStreamInfo video, CropBox box) =>
        SquarePixels(video) + $"crop={box.Width}:{box.Height}:{box.X}:{box.Y},";

    /// <summary>The largest box of a <c>width:height</c> ratio that fits the picture, centred, in even pixels.</summary>
    internal static CropBox RatioBox(VideoStreamInfo video, int width, int height)
    {
        var (w, h) = ShownSize(video);
        var cw = (int)Math.Min(w, (long)h * width / height) & ~1;
        var ch = (int)Math.Min(h, (long)w * height / width) & ~1;
        return new CropBox(cw, ch, (w - cw) / 2 & ~1, (h - ch) / 2 & ~1);
    }

    /// <summary>
    /// cropdetect over <paramref name="seconds"/> from <paramref name="at"/> seconds: it writes a <c>crop=w:h:x:y</c>
    /// line for each frame, at the info level.
    /// </summary>
    internal static List<string> CropDetect(string input, double at, double seconds, VideoStreamInfo video) =>
    [
        "-hide_banner", "-nostdin", "-nostats", "-v", "info",
        "-ss", ClipRange.Ffmpeg(at), "-i", input, "-t", ClipRange.Ffmpeg(seconds), "-map", "0:V:0",
        // limit as a part of the range, so 10-bit and HDR video read as 8-bit does (24 of 255); round=2: even sizes
        // (4:2:0); reset=0: the box only grows over the stretch, so one dark frame doesn't shrink it.
        "-vf", SquarePixels(video) + "cropdetect=limit=0.094:round=2:reset=0", "-an", "-f", "null", "-",
    ];

    [GeneratedRegex(@"crop=(-?\d+):(-?\d+):(-?\d+):(-?\d+)")]
    private static partial Regex CropLine();

    /// <summary>True when cropdetect wrote any box at all, even an empty one (a black stretch writes negative sizes).</summary>
    internal static bool HasCropLine(IEnumerable<string> lines) => lines.Any(line => CropLine().IsMatch(line));

    /// <summary>
    /// The box a stretch ended with (with reset=0 the last is the largest), sizes and offsets rounded down to even
    /// numbers; null when none has a size.
    /// </summary>
    internal static CropBox? FinalCrop(IEnumerable<string> lines) =>
        lines.Select(line => CropLine().Match(line)).Where(m => m.Success)
            .Select(m => new CropBox(Even(m.Groups[1]), Even(m.Groups[2]), Even(m.Groups[3]), Even(m.Groups[4])))
            .Where(b => b is { Width: > 0, Height: > 0, X: >= 0, Y: >= 0 })
            .Select(b => (CropBox?)b).LastOrDefault();

    private static int Even(Group g) => int.Parse(g.Value, CultureInfo.InvariantCulture) & ~1;

    /// <summary>The largest of the stretches' boxes: a dark stretch finds a box inside the picture, never one larger.</summary>
    internal static CropBox? LargestCrop(IEnumerable<CropBox?> boxes) =>
        boxes.Where(b => b is not null).MaxBy(b => (long)b!.Value.Width * b.Value.Height);

    /// <summary>
    /// Width and height of the picture cropdetect sees: upright, with square pixels (<see cref="SquarePixels"/>).
    /// </summary>
    internal static (int Width, int Height) ShownSize(VideoStreamInfo video)
    {
        var (w, h) = video.IsRotated ? (video.Height, video.Width) : (video.Width, video.Height);
        var sar = video.IsRotated ? 1 / video.SampleAspect : video.SampleAspect;
        return Math.Abs(video.SampleAspect - 1) > 0.01 ? ((int)(w * sar) & ~1, h) : (w, h);
    }

    /// <summary>True when <paramref name="box"/> is the whole picture, give or take the rounding: nothing to crop.</summary>
    internal static bool IsWholeFrame(CropBox box, VideoStreamInfo video)
    {
        var (w, h) = ShownSize(video);
        return box.X <= 1 && box.Y <= 1 && box.Width >= w - 2 && box.Height >= h - 2;
    }

    /// <summary>
    /// True when a found box keeps less than a quarter of the width or height: a video dark all through, not bars.
    /// </summary>
    internal static bool IsTooSmall(CropBox box, VideoStreamInfo video)
    {
        var (w, h) = ShownSize(video);
        return box.Width * 4 < w || box.Height * 4 < h;
    }

    /// <summary>
    /// The options for a crop: with the usual codec, an HEVC or AV1 source stays HEVC or AV1 where the target holds it and
    /// FFmpeg has the encoder, so its 10 bits and HDR tags are kept (<see cref="HighBitDepthArguments"/>).
    /// </summary>
    internal static MediaOptions CropOptions(string target, MediaOptions options, VideoStreamInfo video, IReadOnlySet<string>? encoders)
    {
        var codec = video.Codec switch
        {
            "hevc" => VideoCodec.Hevc,
            "av1" => VideoCodec.Av1,
            _ => VideoCodec.Default,
        };
        if (options.VideoCodec != VideoCodec.Default || codec == VideoCodec.Default || !MediaCodecs.VideoCodecsFor(target).Contains(codec))
            return options;
        try
        {
            VideoEncoder(target, "", codec, encoders);
        }
        catch (InvalidOperationException)
        {
            return options;
        }
        var kept = new Preset { Media = options }.Clone().Media;
        kept.VideoCodec = codec;
        return kept;
    }
}
