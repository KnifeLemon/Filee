// "Crop video" (#830): the frame of a video cut to an aspect ratio, centred, or to the picture inside its black bars
// (MediaOptions.CropFrame). FFmpeg does the cutting (MediaEncoding.CropFilter); the video is always encoded again.

namespace Filee.Core.Presets;

/// <summary>The frames "Crop video" can cut a video to (<see cref="MediaOptions.CropFrame"/>).</summary>
public static class VideoCrop
{
    /// <summary>The picture inside the black bars, found by FFmpeg's <c>cropdetect</c>.</summary>
    public const string Auto = "auto";

    /// <summary>The ratios offered, landscape and portrait, as "width:height".</summary>
    public static IReadOnlyList<string> Ratios { get; } = ["16:9", "9:16", "4:3", "3:4", "1:1", "21:9", "9:21"];

    /// <summary>
    /// Reads a "width:height" ratio such as "16:9"; false for <see cref="Auto"/>, an empty value and anything that isn't
    /// two whole numbers from 1 to 100.
    /// </summary>
    public static bool TryParseRatio(string? frame, out int width, out int height)
    {
        width = height = 0;
        var parts = frame?.Split(':');
        return parts is [var w, var h]
            && int.TryParse(w, out width) && int.TryParse(h, out height)
            && width is >= 1 and <= 100 && height is >= 1 and <= 100;
    }

    /// <summary>
    /// The format a crop of <paramref name="source"/> (a format id) is saved in for "same as source": MP4 for VOB and DV,
    /// whose fixed frame sizes would stretch the cropped picture back to the whole frame.
    /// </summary>
    public static string TargetFor(string source) => source is "vob" or "dv" ? "mp4" : source;

    /// <summary>True when <paramref name="frame"/> crops anything: <see cref="Auto"/> or a ratio.</summary>
    public static bool Crops(string? frame) => frame == Auto || TryParseRatio(frame, out _, out _);
}
