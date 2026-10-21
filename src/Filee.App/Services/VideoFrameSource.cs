// Where the clip window gets a video's length and pictures of its frames: Filee's FFmpeg, or pictures made up by tests.

using Filee.Engines.Media;

namespace Filee.App.Services;

/// <summary>Reads a video's length and writes pictures of single frames.</summary>
public interface IVideoFrameSource
{
    /// <summary>The video's length and size; throws when it can't be read.</summary>
    Task<VideoSummary> ProbeAsync(string path, CancellationToken cancellationToken);

    /// <summary>Writes the frame at <paramref name="seconds"/> as a JPEG; false when there is none.</summary>
    Task<bool> WriteFrameAsync(string path, double seconds, string output, int maxHeight, CancellationToken cancellationToken);
}

/// <summary>Frames taken with Filee's FFmpeg (<see cref="VideoFrames"/>).</summary>
public sealed class FfmpegFrameSource : IVideoFrameSource
{
    public Task<VideoSummary> ProbeAsync(string path, CancellationToken cancellationToken) =>
        VideoFrames.ProbeAsync(path, cancellationToken);

    public Task<bool> WriteFrameAsync(string path, double seconds, string output, int maxHeight, CancellationToken cancellationToken) =>
        VideoFrames.WriteFrameAsync(path, seconds, output, maxHeight, cancellationToken);
}
