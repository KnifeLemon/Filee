// Where FFmpeg has to start reading a video for the frame at a time to come out exact, and how to name that time.
//
// FFmpeg's own -ss misses in files without an index (MPEG-TS, M2TS, MPEG-PS: it lands between key frames, and the
// decoder skips ahead to the next one, up to a group of pictures late) and among the leading pictures of an open group
// (x265's HEVC: they are shown before the key frame they follow, and are skipped when decoding starts there). Reading
// the packets before the time with ffprobe finds the last key frame shown at or before it, without decoding anything,
// and reading starts there: at its decode time in a file without an index, which FFmpeg seeks by decode time, and at
// the time it is shown in any other, which FFmpeg seeks through its index of key frames. Where a video has B-frames,
// FFmpeg seeks 3/23 s before -ss except in MOV, MP4 and MXF (dts_heuristic in ffmpeg_demux.c), so -ss is that much
// later there, or reading would start a group of pictures early; and from the file's first key frame, reading starts at
// its first packet (a seek before an FLV's start failed and landed between key frames).
//
// Times are then the file's own (-copyts). Without it, FFmpeg moves time 0 to the first stream a command uses in
// MPEG-TS-like files when -ss lies before it, and FFmpeg 6.0 to 7.1 get that wrong after a seek
// (correct_input_start_times in ffmpeg_opt.c): a picture of a TS whose sound starts first came out a frame early, on
// Ubuntu's 6.1 fifteen. A picture is trimmed at the time as those timestamps give it. What is encoded is moved by
// -itsoffset so that the first frame shown at or after the start is time 0 before the encoder rounds anything: kept in
// the file's own times, an MP4 clip lost its first frame to the encoder's 1/fps grid, and its chapters stayed where
// they were. It is cut by trim in the stream's own ticks, and not moved by setpts, which drops frame durations in
// FFmpeg 7.0 to 8.0 (an MP4 clip lost its last frame). FFmpeg's -progress counts from its first report with -copyts, so
// what is encoded reports each picture's time instead.

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Media;

/// <summary>
/// Where a command that reads a video at a time starts reading, and how it names its times.
/// </summary>
/// <param name="From">
/// <c>-ss</c> before <c>-i</c>, in seconds from the file's start; 0 reads from the file's first packet.
/// </param>
/// <param name="Origin">
/// The file's start time, which FFmpeg's timestamps begin at with <c>-copyts</c>; null when it couldn't be read, and
/// the command then seeks to <see cref="From"/> and counts from there, as Filee did before.
/// </param>
/// <param name="CopyFrom">
/// <c>-ss</c> for a copy (<c>-c copy</c>), which starts where FFmpeg's seek lands: on the key frame found.
/// </param>
/// <param name="First">
/// The first frame shown at or after the time asked for, in seconds from the file's start: time 0 of what is encoded
/// from there (the time asked for when the look couldn't tell).
/// </param>
/// <param name="Tick">The video stream's time base, in seconds; null when unknown.</param>
internal readonly record struct SeekPoint(double From, double? Origin, double CopyFrom, double First, double? Tick = null)
{
    /// <summary>
    /// Seeking straight to <paramref name="seconds"/> as FFmpeg does by itself, with times counted from there.
    /// </summary>
    public static SeekPoint Plain(double seconds) =>
        new(Math.Max(0, seconds), null, Math.Max(0, seconds), Math.Max(0, seconds));

    /// <summary>
    /// How far before a time a frame still counts as shown at it, in seconds: times are written to the microsecond, and
    /// a frame shown at a time may read a microsecond before it (the last frame of an MPEG-TS, for one).
    /// </summary>
    internal const double Tolerance = 0.000001;

    /// <summary>
    /// The input options of a picture: <c>-copyts</c> when the times are the file's own (<see cref="Time"/>), and
    /// <c>-ss</c>.
    /// </summary>
    public string[] Input => [.. Origin is null ? Array.Empty<string>() : ["-copyts"], .. MediaEncoding.SeekTo(From)];

    /// <summary>
    /// <paramref name="seconds"/> from the file's start as a picture's timestamps (<see cref="Input"/>) read it, less
    /// the <see cref="Tolerance"/>: to the microsecond, never below 0.
    /// </summary>
    public string Time(double seconds) =>
        Number(Origin is { } origin ? origin + seconds - Tolerance : seconds - From - Tolerance);

    /// <summary>
    /// The input options of what is encoded (a clip, its playback, a Live Photo): with the file's own timestamps, moved
    /// so that <see cref="First"/> is time 0. That frame is shown at a whole tick of the file's time base, so the move
    /// rounds to nothing, and the encoder and the muxer count from it as they did before (chapters included). FFmpeg's
    /// own cut at <c>-ss</c> is left out: it ignores the move, and the trims (<see cref="Clip"/>) cut instead.
    /// </summary>
    public string[] EncodeInput => Origin is { } origin
        ? ["-copyts", "-noaccurate_seek", .. MediaEncoding.SeekTo(From), .. origin + First is var zero && zero > 0
            ? ["-itsoffset", "-" + Number(zero)] : Array.Empty<string>()]
        : MediaEncoding.SeekTo(From);

    /// <summary>
    /// <paramref name="seconds"/> from the file's start as what is encoded (<see cref="EncodeInput"/>) reads it, less
    /// the <see cref="Tolerance"/>: to the microsecond, never below 0.
    /// </summary>
    public string Clip(double seconds) => Number(seconds - (Origin is null ? From : First) - Tolerance);

    /// <summary>
    /// The video <c>trim</c> of what is encoded from <paramref name="start"/> to <paramref name="end"/> (null: to the
    /// end), in seconds from the file's start: the first frame shown at or after each. In the time base's own ticks
    /// where it is known, as <see cref="First"/> is a whole tick: a time in seconds is rounded to the nearest tick, a
    /// whole frame in an AVI, and in microseconds (<c>settb</c>) the frames of a part played faster stepped unevenly.
    /// </summary>
    public string VideoTrim(double start, double? end = null) => Origin is not null && Tick is > 0 and var tick
        ? $"trim=start_pts={Ticks(start, tick)}" + (end is { } e ? $":end_pts={Ticks(e, tick)}" : "")
        : $"trim=start={Clip(start)}" + (end is { } e2 ? $":end={Clip(e2)}" : "");

    private long Ticks(double seconds, double tick) =>
        (long)Math.Max(0, Math.Ceiling((seconds - First - Tolerance) / tick));

    private static string Number(double seconds) => Math.Max(0, seconds).ToString("0.######", CultureInfo.InvariantCulture);
}

/// <summary>How FFmpeg seeks a file (see the file header of <see cref="KeyFrames"/>).</summary>
/// <param name="Unindexed">Without an index, by decode time and onto any packet (MPEG-TS, MPEG-PS).</param>
/// <param name="ByShownTime">By the time a key frame is shown, with no margin for B-frames (MOV, MP4, MXF).</param>
/// <param name="Reordered">The video has B-frames, so its frames are decoded before they are shown.</param>
internal readonly record struct Seeking(bool Unindexed, bool ByShownTime, bool Reordered);

/// <summary>Finds where reading has to start for an exact frame (see the file header).</summary>
internal static class KeyFrames
{
    /// <summary>
    /// How far before the time the first look reaches, in seconds; each next look reaches four times as far.
    /// </summary>
    internal const double FirstReach = 0.5;

    /// <summary>
    /// The farthest a look reaches, in seconds: a key frame further back than that is a file FFmpeg's own seek has to
    /// do for, rather than reading minutes of packets for each picture.
    /// </summary>
    internal const double LastReach = 32;

    /// <summary>
    /// How far past the time a look reads, in seconds: the first frame shown at or after the time is read too, though
    /// with B-frames it may come after frames shown later.
    /// </summary>
    private const double Overlap = 0.5;

    /// <summary>
    /// Formats FFmpeg seeks without an index, by decode time and onto any packet (<c>format_name</c> as ffprobe gives
    /// it): MPEG-TS and M2TS, and MPEG-PS (VOB, MPG).
    /// </summary>
    private static readonly HashSet<string> Unindexed = ["mpegts", "mpegtsraw", "mpeg"];

    /// <summary>
    /// Formats FFmpeg seeks by the time a frame is shown, with no margin for B-frames: MOV and MP4, MXF.
    /// </summary>
    private static readonly HashSet<string> ByShownTime = ["mov", "mxf"];

    /// <summary>
    /// How much earlier than <c>-ss</c> FFmpeg seeks a video with B-frames in other formats, in seconds.
    /// </summary>
    internal const double DtsHeuristic = 3.0 / 23;

    /// <summary>
    /// How long one ffprobe run may take; a look reads packets only, from about <see cref="LastReach"/> before the time
    /// at most (an indexed file from the key frame before that).
    /// </summary>
    private static readonly TimeSpan LookTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The start time and format of files looked at lately, by path, size and change time.</summary>
    private static readonly ConcurrentDictionary<string, (double Origin, Seeking How, double? Tick)> Formats = new();

    /// <summary>
    /// Where to start reading the first video stream of <paramref name="input"/> (as <c>-map 0:V:0</c> picks it) for
    /// the frame at <paramref name="seconds"/> from the file's start (see the file header). When ffprobe can't read the
    /// file or finds no key frame within <see cref="LastReach"/>, FFmpeg seeks to the time itself, as it always did.
    /// </summary>
    public static async Task<SeekPoint> FindAsync(string ffprobe, string input, double seconds, CancellationToken cancellationToken)
    {
        seconds = double.IsFinite(seconds) ? Math.Max(0, seconds) : 0;
        if (await FormatAsync(ffprobe, input, cancellationToken).ConfigureAwait(false) is not var (origin, how, tick))
            return SeekPoint.Plain(seconds);
        var unindexed = how.Unindexed;
        for (var reach = FirstReach; ; reach = Math.Min(reach * 4, LastReach))
        {
            var from = Math.Max(0, seconds - reach);
            var json = await RunAsync(ffprobe, Arguments(input, origin, from, seconds), cancellationToken).ConfigureAwait(false);
            if (json is null)
                return SeekPoint.Plain(seconds);
            (double? From, double? Key, double? First) look;
            try
            {
                look = Choose(json, origin, seconds, how, fromFirstPacket: from <= 0);
            }
            catch (JsonException)
            {
                return SeekPoint.Plain(seconds);
            }
            var first = look.First ?? seconds;
            if (look.From is { } found)
            {
                // A copy keeps what FFmpeg's seek lands on: in an indexed file sought 3/23 s early, the time itself
                // could land a group of pictures early, so it is that much past the key frame then.
                var copy = unindexed ? found
                    : how.Reordered && !how.ByShownTime && look.Key is { } key ? Math.Max(seconds, key + DtsHeuristic) : seconds;
                return new SeekPoint(found, origin, copy, first, tick);
            }
            // Before the first key frame (the time is in its leading pictures, or there are none): from the start.
            if (from <= 0)
                return new SeekPoint(0, origin, unindexed ? 0 : seconds, first, tick);
            if (reach >= LastReach)
                return SeekPoint.Plain(seconds) with { Origin = origin, First = first, Tick = tick };
        }
    }

    /// <summary>
    /// The file's start time, and whether FFmpeg seeks it without an index; null when ffprobe can't read it.
    /// </summary>
    private static async Task<(double Origin, Seeking How, double? Tick)?> FormatAsync(string ffprobe, string input,
        CancellationToken cancellationToken)
    {
        var key = input;
        try
        {
            if (File.Exists(input) && new FileInfo(input) is var file)
                key = string.Create(CultureInfo.InvariantCulture, $"{input}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
        if (Formats.TryGetValue(key, out var known))
            return known;
        var json = await RunAsync(ffprobe,
            ["-hide_banner", "-v", "error", "-select_streams", "V:0", "-show_entries", "format=start_time,format_name:stream=has_b_frames,time_base",
                "-print_format", "json", input],
            cancellationToken).ConfigureAwait(false);
        if (json is null)
            return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("format", out var format))
                return null;
            var names = format.TryGetProperty("format_name", out var name) && name.GetString() is { } text ? text.Split(',') : [];
            var reordered = document.RootElement.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array
                && streams.EnumerateArray().Any(stream => stream.TryGetProperty("has_b_frames", out var delay)
                    && delay.ValueKind == JsonValueKind.Number && delay.GetInt32() > 0);
            var tick = streams.ValueKind == JsonValueKind.Array && streams.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } video
                && video.TryGetProperty("time_base", out var timeBase) && timeBase.GetString()?.Split('/') is [var n, var d]
                && double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
                && double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out var den) && num > 0 && den > 0
                ? num / den : (double?)null;
            var found = (Time(format, "start_time") ?? 0,
                new Seeking(names.Any(Unindexed.Contains), names.Any(ByShownTime.Contains), reordered), tick);
            // A clip window looks at one file again and again; a queue of conversions goes through many once each.
            if (Formats.Count > 64)
                Formats.Clear();
            Formats[key] = found;
            return found;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>ffprobe's output, or null when it couldn't run or failed.</summary>
    private static async Task<string?> RunAsync(string ffprobe, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(ffprobe, arguments, LookTimeout, cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0 ? result.StandardOutput : null;
        }
        catch (Exception ex) when (ex is TimeoutException or Win32Exception or IOException or InvalidOperationException
            or ConversionRefusedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The ffprobe run that lists the key frame flags and times of the first video stream's packets from
    /// <paramref name="from"/> (the file's first packet at 0) to a moment past <paramref name="seconds"/>, both counted
    /// from <paramref name="origin"/>, the file's start time.
    /// </summary>
    internal static List<string> Arguments(string input, double origin, double from, double seconds) =>
    [
        "-hide_banner", "-v", "error", "-select_streams", "V:0",
        "-read_intervals", (from > 0 ? Absolute(origin + from) : "") + "%" + Absolute(origin + seconds + Overlap),
        "-show_entries", "packet=pts_time,dts_time,flags", "-print_format", "json", input,
    ];

    private static string Absolute(double seconds) => seconds.ToString("0.000000", CultureInfo.InvariantCulture);

    /// <summary>
    /// From ffprobe's packet list (<see cref="Arguments"/>), in seconds from the file's start: the <c>-ss</c> that
    /// starts reading for the frame at <paramref name="seconds"/> (see the file header) and the time its key frame is
    /// shown, both null when the list holds no key frame shown at or before it; and the first frame shown at or after
    /// it, null when the list holds none. A key frame without a time of its own to be shown at (AVI with B-frames,
    /// MPEG-PS) counts by its decode time, and reading then starts at the key frame before it, which surely is shown
    /// before the time. From the file's first key frame, reading starts at 0.
    /// </summary>
    /// <param name="origin">The file's start time.</param>
    /// <param name="how">How FFmpeg seeks the file.</param>
    /// <param name="fromFirstPacket">The list starts at the file's first packet.</param>
    /// <exception cref="JsonException">The output isn't JSON.</exception>
    internal static (double? From, double? Key, double? First) Choose(string json, double origin, double seconds, Seeking how,
        bool fromFirstPacket)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("packets", out var packets) || packets.ValueKind != JsonValueKind.Array)
            return (null, null, null);
        List<(double? Shown, double? Decoded)> keys = [];
        double? first = null;
        foreach (var packet in packets.EnumerateArray())
        {
            var shownAt = Time(packet, "pts_time") - origin;
            if (shownAt >= seconds - SeekPoint.Tolerance && !(shownAt >= first))
                first = shownAt;
            if (packet.TryGetProperty("flags", out var flags) && flags.GetString() is { } text && text.Contains('K'))
                keys.Add((shownAt, Time(packet, "dts_time") - origin));
        }
        // ffprobe writes six decimals, so a key frame shown exactly at the time may read half a microsecond late.
        var last = keys.FindLastIndex(key => (key.Shown ?? key.Decoded) is { } at && at <= seconds + 5e-7);
        if (last < 0)
            return (null, null, first);
        if (keys[last].Shown is null)
        {
            if (last == 0)
                return fromFirstPacket ? (0, keys[0].Decoded, first) : (null, null, first);
            last--;
        }
        if (last == 0 && fromFirstPacket)
            return (0, keys[0].Shown ?? keys[0].Decoded, first);
        var (shown, decoded) = keys[last];
        double? start = how.Unindexed
            // Sought by decode time, 3/23 s early with B-frames: from the key frame's decode time, or as much later as
            // still lands on it.
            ? decoded is { } d ? how.Reordered && shown is { } s ? Math.Min(s, d + DtsHeuristic) : d : shown
            // Sought through the index by the time shown: there, with the margin FFmpeg takes off outside MOV and MXF.
            : shown is { } at ? at + (how.Reordered && !how.ByShownTime ? DtsHeuristic : 0) : decoded;
        return start is { } time ? (Math.Clamp(time, 0, seconds), shown ?? decoded, first) : (null, null, first);
    }

    private static double? Time(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && double.IsFinite(number)
            ? number
            : null;
}
