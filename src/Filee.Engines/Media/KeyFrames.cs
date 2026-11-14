// Where FFmpeg has to start decoding for the frame at a time to come out exact: the last key frame shown at or before
// that time. FFmpeg's own -ss lands there in an indexed file with closed groups of pictures (most MP4, MOV and MKV),
// but not in a file without an index (MPEG-TS, M2TS, MPEG-PS: it lands between key frames, and the decoder skips ahead
// to the next one, up to a group of pictures late) or among the leading pictures of an open group (x265's HEVC: they
// are shown before the key frame they follow, and are skipped when decoding starts there). Reading the packets around
// the time with ffprobe finds the key frame without decoding anything.
//
// -ss is then the time the key frame is shown, which MP4, MOV and MKV seek by. A file that seeks by decode time
// (MPEG-TS, MPEG-PS) is sought 3/23 s before -ss when its video has B-frames (FFmpeg's dts_heuristic in
// ffmpeg_demux.c), enough for a key frame decoded before it is shown; where it is decoded earlier than that, -ss is
// that decode time plus the 3/23 s. And -ss is never before the video's first frame: in formats whose timestamps may
// jump, FFmpeg counts time from the first stream a command uses when -ss lies before it (correct_input_start_times in
// ffmpeg_opt.c), not from the file's start. In a TS file whose sound starts 21 ms before the picture, every frame read
// from 0 came out a frame early.

using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Media;

/// <summary>Finds where decoding has to start for an exact frame (see the file header).</summary>
internal static class KeyFrames
{
    /// <summary>
    /// How far before the time the first look reaches, in seconds; each next look reaches four times as far.
    /// </summary>
    internal const double FirstReach = 2;

    /// <summary>
    /// How far past the time a look reads: a packet shown later ends the look, and a little more makes sure a key frame
    /// shown exactly at the time is among the packets read.
    /// </summary>
    private const double Overlap = 0.1;

    /// <summary>
    /// How much earlier FFmpeg seeks a file that seeks by decode time when its video has B-frames, in seconds.
    /// </summary>
    internal const double DtsHeuristic = 3.0 / 23;

    /// <summary>
    /// How long one look may take; a look reads packets only, so it is quick even in a long 4K video.
    /// </summary>
    private static readonly TimeSpan LookTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Where to start decoding the first video stream of <paramref name="input"/> (as <c>-map 0:V:0</c> picks it) for
    /// the frame at <paramref name="seconds"/> from the file's start, in seconds from the file's start as <c>-ss</c>
    /// counts them: the last key frame shown at or before it, or the video's first frame when that is later (see the
    /// file header). 0 means from the start of the file. When ffprobe fails, the time itself, so FFmpeg seeks as it
    /// always did.
    /// </summary>
    public static async Task<double> DecodeStartAsync(string ffprobe, string input, double seconds, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(seconds))
            seconds = 0;
        seconds = Math.Max(0, seconds);
        for (var reach = FirstReach; ; reach *= 4)
        {
            var from = Math.Max(0, seconds - reach);
            ProcessResult result;
            try
            {
                result = await ProcessRunner.RunAsync(ffprobe, Arguments(input, from, seconds), LookTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or Win32Exception or IOException)
            {
                return seconds;
            }
            if (result.ExitCode != 0)
                return seconds;
            (double? Start, double FirstFrame) look;
            try
            {
                look = Look(result.StandardOutput, seconds);
            }
            catch (JsonException)
            {
                return seconds;
            }
            if (look.Start is { } found)
                return found;
            // Before the first key frame, or packets without times: from the first frame.
            if (from <= 0)
                return look.FirstFrame;
        }
    }

    /// <summary>
    /// The ffprobe run that lists the packets of the first video stream from <paramref name="from"/> to a moment past
    /// <paramref name="seconds"/>, and the file's start time. A <c>+</c> interval counts from the file's start, as
    /// <c>-ss</c> does.
    /// </summary>
    internal static List<string> Arguments(string input, double from, double seconds) =>
    [
        "-hide_banner", "-v", "error", "-select_streams", "V:0",
        "-read_intervals", $"+{ClipRange.Ffmpeg(from)}%+{ClipRange.Ffmpeg(seconds - from + Overlap)}",
        "-show_entries", "packet=pts_time,dts_time,flags:stream=start_time,has_b_frames:format=start_time", "-print_format", "json", input,
    ];

    /// <summary>
    /// From ffprobe's output (<see cref="Arguments"/>), in seconds from the file's start: the <c>-ss</c> that starts
    /// decoding at the last key frame shown at or before <paramref name="seconds"/> (see the file header), null when
    /// the packets read hold no such key frame; and the video's first frame.
    /// </summary>
    /// <exception cref="JsonException">The output isn't JSON.</exception>
    internal static (double? Start, double FirstFrame) Look(string json, double seconds)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var origin = root.TryGetProperty("format", out var format) && Time(format, "start_time") is { } startTime ? startTime : 0;
        var stream = root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array
            ? streams.EnumerateArray().FirstOrDefault()
            : default;
        var first = stream.ValueKind == JsonValueKind.Object && Time(stream, "start_time") is { } videoStart ? Math.Max(0, videoStart - origin) : 0;
        var reordered = stream.ValueKind == JsonValueKind.Object && stream.TryGetProperty("has_b_frames", out var delay)
            && delay.ValueKind == JsonValueKind.Number && delay.GetInt32() > 0;
        if (!root.TryGetProperty("packets", out var packets) || packets.ValueKind != JsonValueKind.Array)
            return (null, first);
        double? shown = null, start = null;
        foreach (var packet in packets.EnumerateArray())
        {
            if (!(packet.TryGetProperty("flags", out var flags) && flags.GetString() is { } text && text.Contains('K')))
                continue;
            if (Time(packet, "pts_time") is not { } pts)
                continue;
            // ffprobe writes six decimals, so a key frame shown exactly at the time may read half a microsecond late.
            pts -= origin;
            if (pts > seconds + 5e-7 || pts <= shown)
                continue;
            shown = pts;
            var dts = (Time(packet, "dts_time") ?? pts + origin) - origin;
            start = Math.Min(pts, dts + (reordered ? DtsHeuristic : 0));
        }
        return (start is { } time ? Math.Max(first, time) : null, first);
    }

    private static double? Time(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && double.IsFinite(number)
            ? number
            : null;
}
