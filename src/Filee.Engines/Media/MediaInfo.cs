// What the FFmpeg engine needs to know about a media file before encoding it (duration, first video and audio
// stream, cover picture), read from ffprobe's JSON output.

using System.Globalization;
using System.Text.Json;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Media;

/// <summary>The first real video stream of a file (cover pictures are not counted).</summary>
/// <param name="Width">Coded width in pixels.</param>
/// <param name="Height">Coded height in pixels.</param>
/// <param name="SampleAspect">Pixel aspect ratio (1 for square pixels, 8/9 for NTSC DV 4:3, ...).</param>
/// <param name="Rotation">Display rotation in degrees from the display matrix (phones record portrait video this way).</param>
/// <param name="FrameRate">Average frames per second, 0 when unknown.</param>
internal sealed record VideoStreamInfo(int Width, int Height, double SampleAspect, int Rotation, double FrameRate)
{
    /// <summary>True when the picture is shown turned by 90° (FFmpeg rotates it upright while decoding).</summary>
    public bool IsRotated => Math.Abs(Rotation % 180) == 90;

    /// <summary>Width ÷ height as the picture is displayed: pixel aspect and rotation applied.</summary>
    public double DisplayAspect
    {
        get
        {
            if (Width <= 0 || Height <= 0)
                return 16 / 9.0;
            var aspect = Width * SampleAspect / Height;
            return IsRotated ? 1 / aspect : aspect;
        }
    }
}

/// <summary>The first audio stream of a file.</summary>
internal sealed record AudioStreamInfo(int Channels, int SampleRate);

/// <summary>Summary of a media file.</summary>
/// <param name="Duration">Playing time, null when the container does not say (raw streams).</param>
/// <param name="Video">First video stream that is not a cover picture, if any.</param>
/// <param name="Audio">First audio stream, if any.</param>
/// <param name="CoverStreamIndex">Index of an embedded cover picture (JPEG or PNG) such as album art, if any.</param>
internal sealed record MediaInfo(TimeSpan? Duration, VideoStreamInfo? Video, AudioStreamInfo? Audio, int? CoverStreamIndex)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(1);

    /// <summary>Runs ffprobe on <paramref name="input"/>; throws with FFmpeg's message when the file cannot be read.</summary>
    public static async Task<MediaInfo> ProbeAsync(string ffprobe, string input, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(ffprobe,
            ["-hide_banner", "-v", "error", "-print_format", "json", "-show_format", "-show_streams", input],
            ProbeTimeout, cancellationToken);
        if (result.ExitCode != 0)
        {
            var reason = FfmpegRunner.Summarize(result.StandardError);
            throw new InvalidOperationException($"FFmpeg cannot read this file{(reason.Length > 0 ? ": " + reason : ".")}");
        }
        return Parse(result.StandardOutput);
    }

    /// <summary>Parses the output of <c>ffprobe -print_format json -show_format -show_streams</c>.</summary>
    public static MediaInfo Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        VideoStreamInfo? video = null;
        AudioStreamInfo? audio = null;
        int? cover = null;
        double longestStream = 0;
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                longestStream = Math.Max(longestStream, Number(stream, "duration"));
                switch (Text(stream, "codec_type"))
                {
                    case "video" when IsAttachedPicture(stream):
                        // Album art and thumbnails: kept for audio targets that can hold a cover, never encoded as video.
                        if (cover is null && Text(stream, "codec_name") is "mjpeg" or "png")
                            cover = (int)Number(stream, "index");
                        break;
                    case "video" when video is null:
                        video = new VideoStreamInfo(
                            (int)Number(stream, "width"),
                            (int)Number(stream, "height"),
                            Ratio(Text(stream, "sample_aspect_ratio"), ':') is var sar and > 0 ? sar : 1,
                            RotationOf(stream),
                            FrameRateOf(stream));
                        break;
                    case "audio" when audio is null:
                        audio = new AudioStreamInfo((int)Number(stream, "channels"), (int)Number(stream, "sample_rate"));
                        break;
                }
            }
        }

        var seconds = root.TryGetProperty("format", out var format) ? Number(format, "duration") : 0;
        if (seconds <= 0)
            seconds = longestStream;
        return new MediaInfo(seconds > 0 ? TimeSpan.FromSeconds(seconds) : null, video, audio, cover);
    }

    private static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var disposition)
        && disposition.TryGetProperty("attached_pic", out var attached)
        && attached.ValueKind == JsonValueKind.Number && attached.GetInt32() == 1;

    /// <summary>Average frame rate; the nominal rate when the average is missing or nonsense (e.g. "0/0").</summary>
    private static double FrameRateOf(JsonElement stream)
    {
        var average = Ratio(Text(stream, "avg_frame_rate"), '/');
        if (average is > 0 and < 1000)
            return average;
        var nominal = Ratio(Text(stream, "r_frame_rate"), '/');
        return nominal is > 0 and < 1000 ? nominal : 0;
    }

    /// <summary>Rotation from the display matrix side data, or the older "rotate" tag.</summary>
    private static int RotationOf(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sideData.EnumerateArray())
            {
                if (item.TryGetProperty("rotation", out var rotation) && rotation.ValueKind == JsonValueKind.Number)
                    return (int)Math.Round(rotation.GetDouble());
            }
        }
        return stream.TryGetProperty("tags", out var tags) && int.TryParse(Text(tags, "rotate"), out var rotate) ? rotate : 0;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>ffprobe writes most numbers as strings ("2.000000"), some as JSON numbers.</summary>
    private static double Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number)
            return value.GetDouble();
        return value.ValueKind == JsonValueKind.String
               && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
               && double.IsFinite(number)
            ? number
            : 0;
    }

    /// <summary>"30000/1001" → 29.97, "16:9" → 1.78; 0 for missing or invalid values.</summary>
    private static double Ratio(string? text, char separator)
    {
        var parts = text?.Split(separator);
        return parts is [var n, var d]
               && double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
               && double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
               && den > 0
            ? num / den
            : 0;
    }
}
