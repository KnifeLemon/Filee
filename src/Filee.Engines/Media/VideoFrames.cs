// What the clip window ("Cut a clip") needs of a video before anything is cut: how long it is and pictures of single
// frames for its timeline and its large preview, taken with Filee's FFmpeg into the window's own work folder.

namespace Filee.Engines.Media;

/// <summary>A video, or a sound (no picture: <see cref="HasPicture"/> is false), as the clip window shows it.</summary>
/// <param name="Duration">Playing time in seconds, 0 when the file doesn't say.</param>
/// <param name="Width">Width of the picture as shown (pixel aspect and rotation applied); 0 for a sound.</param>
/// <param name="Height">Height of the picture as shown; 0 for a sound.</param>
/// <param name="FrameRate">Frames per second, 0 when unknown.</param>
public sealed record VideoSummary(double Duration, int Width, int Height, double FrameRate)
{
    /// <summary>The file has pictures to show; a sound (an MP3, a recording) has none, only its waveform.</summary>
    public bool HasPicture => Width > 0 && Height > 0;

    /// <summary>One frame's length in seconds (a 30 fps one when the rate is unknown).</summary>
    public double FrameLength => FrameRate is > 1 and < 1000 ? 1 / FrameRate : 1 / 30.0;

    /// <summary>
    /// The last time a frame can be taken at: a frame before the end, as FFmpeg finds nothing to show at the end
    /// itself.
    /// </summary>
    public double LastFrameTime => Math.Max(0, Duration - FrameLength);
}

/// <summary>A frame written for "Mark this frame".</summary>
/// <param name="Written">False when FFmpeg found no frame there (past the end) or failed.</param>
/// <param name="ColorsNotConverted">
/// An HDR frame written with its colours as decoded (FFmpeg has no zscale): it looks washed out, and Annotate says so.
/// </param>
public sealed record FrameStill(bool Written, bool ColorsNotConverted);

/// <summary>Reads a video's length and takes pictures of single frames with Filee's FFmpeg.</summary>
public static class VideoFrames
{
    /// <summary>How long one frame may take: FFmpeg decodes from the key frame before it.</summary>
    private static readonly TimeSpan FrameStall = TimeSpan.FromSeconds(30);

    /// <summary>True when FFmpeg is installed (Settings › Engines).</summary>
    public static bool IsAvailable => FfmpegConverter.Locate() is { } tools && File.Exists(tools.Ffmpeg) && File.Exists(tools.Ffprobe);

    /// <summary>
    /// The length and size of the video at <paramref name="path"/>; a file with sound and no picture gives its length and
    /// no size (<see cref="VideoSummary.HasPicture"/> false).
    /// </summary>
    /// <exception cref="InvalidOperationException">FFmpeg is missing, the file can't be read or has no video or sound.</exception>
    public static async Task<VideoSummary> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var (_, ffprobe) = FfmpegConverter.Locate() ?? throw new InvalidOperationException("FFmpeg was not found.");
        var info = await MediaInfo.ProbeAsync(ffprobe, MediaInput.For(path), cancellationToken);
        if (info.Video is null)
            return info.Audio is not null
                ? new VideoSummary(info.Duration?.TotalSeconds ?? 0, 0, 0, 0)
                : throw new InvalidOperationException("The file has no video or sound stream.");
        var video = info.Video;
        var height = video.IsRotated ? video.Width : video.Height;
        return new VideoSummary(info.Duration?.TotalSeconds ?? 0, (int)Math.Round(height * video.DisplayAspect), height, video.FrameRate);
    }

    /// <summary>
    /// Writes the frame at <paramref name="seconds"/> as a JPEG at most <paramref name="maxHeight"/> pixels high to
    /// <paramref name="output"/>: the first frame shown at or after that time, the one a clip from there starts with.
    /// False when FFmpeg found no frame there (past the end) or failed.
    /// </summary>
    public static async Task<bool> WriteFrameAsync(string path, double seconds, string output, int maxHeight, CancellationToken cancellationToken)
    {
        var (ffmpeg, ffprobe) = FfmpegConverter.Locate() ?? throw new InvalidOperationException("FFmpeg was not found.");
        var input = MediaInput.For(path);
        seconds = Math.Max(0, seconds);
        var seek = await KeyFrames.FindAsync(ffprobe, input, seconds, cancellationToken);
        var arguments = MediaEncoding.FrameAt(input, seconds, seek, output, maxHeight, null);
        return Written(await FfmpegRunner.RunAsync(ffmpeg, arguments, null, cancellationToken, FrameStall), output);
    }

    /// <summary>FFmpeg ended well and wrote the picture.</summary>
    private static bool Written(Infrastructure.ProcessResult result, string output) =>
        result.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0;

    /// <summary>
    /// Writes the frame at <paramref name="seconds"/> as a PNG at the size the video is shown at to
    /// <paramref name="output"/> ("Mark this frame"): the frame <see cref="WriteFrameAsync"/> shows at that time. An
    /// HDR frame gets ordinary colours where FFmpeg can convert them (as a Live Photo's).
    /// </summary>
    public static async Task<FrameStill> WriteStillAsync(string path, double seconds, string output, CancellationToken cancellationToken)
    {
        var (ffmpeg, ffprobe) = FfmpegConverter.Locate() ?? throw new InvalidOperationException("FFmpeg was not found.");
        var input = MediaInput.For(path);
        seconds = Math.Max(0, seconds);
        // The stream (HDR, turned, pixel shape) and where to read from, found side by side.
        var probing = MediaInfo.ProbeAsync(ffprobe, input, cancellationToken);
        var seeking = KeyFrames.FindAsync(ffprobe, input, seconds, cancellationToken);
        await Task.WhenAll(probing, seeking); // both looked at, also when one fails
        var video = probing.Result.Video ?? throw new InvalidOperationException("The file has no video stream.");
        var seek = seeking.Result;
        // Asking FFmpeg for its filters waits for it (the first time): off the caller's thread.
        var toneMap = video.IsHdr && await Task.Run(() => FfmpegConverter.HasFilter(ffmpeg, "zscale"), cancellationToken);
        var written = Written(await FfmpegRunner.RunAsync(ffmpeg, MediaEncoding.StillAt(input, seconds, seek, output, video, toneMap), null,
            cancellationToken, FrameStall), output);
        return new FrameStill(written, written && video.IsHdr && !toneMap);
    }

    /// <summary>The time of the timeline's picture <paramref name="index"/> of <paramref name="count"/>: the middle of its part.</summary>
    public static double ThumbnailTime(int index, int count, double duration) =>
        count <= 0 || duration <= 0 ? 0 : duration * (index + 0.5) / count;
}
