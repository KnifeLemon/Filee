// Where FFmpeg has to start reading a video for the frame at a time to come out exact, and how to name that time.
//
// FFmpeg's own -ss misses in files without an index (MPEG-TS, M2TS, MPEG-PS: it lands between key frames, and the
// decoder skips ahead to the next one, up to a group of pictures late) and among the leading pictures of an open group
// (x265's HEVC: they are shown before the key frame they follow, and are skipped when decoding starts there). Reading
// the packets before the time with ffprobe finds the last key frame shown at or before it, without decoding anything,
// and reading starts there: at its decode time in a file without an index, which FFmpeg seeks by decode time, and at
// the time it is shown in any other, which FFmpeg seeks through its index of key frames. Where a video has B-frames,
// in any of its streams, FFmpeg seeks 3/23 s before -ss except in MOV, MP4, MXF and a few others that seek by the time
// shown (dts_heuristic in ffmpeg_demux.c), so -ss is that much later there, or reading would start a group of pictures
// early; and from the file's first key frame, reading starts at its first packet (a seek before an FLV's start failed
// and landed between key frames).
//
// Times are then the file's own (-copyts). Without it, FFmpeg moves time 0 to the first stream a command uses in
// MPEG-TS-like files when -ss lies before it, and FFmpeg 6.0 to 7.1 get that wrong after a seek
// (correct_input_start_times in ffmpeg_opt.c): a picture of a TS whose sound starts first came out a frame early, on
// Ubuntu's 6.1 fifteen. A picture is trimmed at the time as those timestamps give it. What is encoded is moved back by
// -itsoffset to a microsecond before the time asked for, so what is kept starts at 0 before the encoder rounds
// anything: kept in the file's own times, an MP4 clip lost its first frame to the encoder's 1/fps grid, and its
// chapters stayed where they were. The picture is cut by trim in the stream's own ticks, moved as FFmpeg rounds the
// move, and not moved by setpts, which drops frame durations in FFmpeg 7.0 to 8.0 (an MP4 clip lost its last frame).
// FFmpeg's -progress counts from its first report with -copyts, so what is encoded reports each picture's time too.

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
/// <c>-ss</c> for a copy (<c>-c copy</c>), which starts where FFmpeg's seek lands, so it lands on the key frame found:
/// that key frame in a file without an index, otherwise the time itself (FFmpeg's index finds the key frame), or the
/// key frame's time plus the 3/23 s FFmpeg seeks early where it does.
/// </param>
/// <param name="Start">
/// The time asked for, in seconds from the file's start: time 0 of what is encoded from there (<see
/// cref="EncodeInput"/>).
/// </param>
/// <param name="Tick">The video stream's time base (<c>1/90000</c>); null when unknown.</param>
internal readonly record struct SeekPoint(double From, double? Origin, double CopyFrom, double Start, (long Num, long Den)? Tick = null)
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
    /// back by <see cref="Shift"/> so that what is kept starts at time 0, and the encoder and the muxer count from
    /// there as they did before (chapters included); nothing kept goes below 0, where an MP4's encoder rounded the
    /// first frame away. FFmpeg's own cut at <c>-ss</c> is left out: it ignores the move, and the trims (<see
    /// cref="Clip"/>, <see cref="VideoTrim"/>) cut instead.
    /// </summary>
    public string[] EncodeInput => Origin is not null
        ? ["-copyts", "-noaccurate_seek", .. MediaEncoding.SeekTo(From),
            .. Shift > 0 ? ["-itsoffset", "-" + (Shift / 1_000_000.0).ToString("0.000000", CultureInfo.InvariantCulture)]
                : Array.Empty<string>()]
        : MediaEncoding.SeekTo(From);

    /// <summary>
    /// How far <see cref="EncodeInput"/> moves the file's timestamps back, in whole microseconds (as FFmpeg reads
    /// <c>-itsoffset</c>): to a microsecond before <see cref="Start"/>, so a frame shown there stays at or after 0 once
    /// FFmpeg rounds the move to the stream's ticks.
    /// </summary>
    private long Shift => Origin is { } origin ? Math.Max(0, (long)Math.Floor((origin + Start) * 1_000_000) - 1) : 0;

    /// <summary>
    /// <paramref name="seconds"/> from the file's start as what is encoded (<see cref="EncodeInput"/>) reads it, less
    /// the <see cref="Tolerance"/>: to the microsecond, never below 0.
    /// </summary>
    public string Clip(double seconds) => Number(Origin is { } origin
        ? origin + seconds - Shift / 1_000_000.0 - Tolerance
        : seconds - From - Tolerance);

    /// <summary>
    /// The video <c>trim</c> of what is encoded from <paramref name="start"/> to <paramref name="end"/> (null: to the
    /// end), in seconds from the file's start: the first frame shown at or after each. In the time base's own ticks
    /// where it is known, moved as FFmpeg moves them: a time in seconds is rounded to the nearest tick, a whole frame
    /// in an AVI, and in microseconds (<c>settb</c>) the frames of a part played faster stepped unevenly.
    /// </summary>
    public string VideoTrim(double start, double? end = null) => Origin is { } origin && Tick is { Num: > 0, Den: > 0 } tick
        ? $"trim=start_pts={Ticks(origin, start, tick)}" + (end is { } e ? $":end_pts={Ticks(origin, e, tick)}" : "")
        : $"trim=start={Clip(start)}" + (end is { } e2 ? $":end={Clip(e2)}" : "");

    /// <summary>
    /// The first tick at or after <paramref name="seconds"/> (less the <see cref="Tolerance"/>) once
    /// <see cref="EncodeInput"/> has moved it: FFmpeg moves each timestamp by <see cref="Shift"/> in the stream's
    /// ticks, rounded to the nearest, halves away from 0 (<c>av_rescale_q</c>).
    /// </summary>
    private long Ticks(double origin, double seconds, (long Num, long Den) tick)
    {
        var scale = (Int128)tick.Num * 1_000_000;
        var moved = (long)(((Int128)Shift * tick.Den + scale / 2) / scale);
        var at = Math.Ceiling((origin + seconds - Tolerance) * tick.Den / tick.Num);
        return Math.Max(0, (long)at - moved);
    }

    private static string Number(double seconds) => Math.Max(0, seconds).ToString("0.######", CultureInfo.InvariantCulture);
}

/// <summary>How FFmpeg seeks a file (see the file header of <see cref="KeyFrames"/>).</summary>
/// <param name="Unindexed">Without an index, by decode time and onto any packet (MPEG-TS, MPEG-PS).</param>
/// <param name="ByShownTime">
/// By the time a key frame is shown, with no margin for B-frames (MOV, MP4, MXF, NUT).
/// </param>
/// <param name="Reordered">A stream has B-frames, so its frames are decoded before they are shown.</param>
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
    /// How far past the time a look reads, in seconds, so a key frame shown exactly at the time is read too.
    /// </summary>
    private const double Overlap = 0.1;

    /// <summary>
    /// Formats FFmpeg seeks without an index, by decode time and onto any packet (<c>format_name</c> as ffprobe gives
    /// it): MPEG-TS and M2TS, and MPEG-PS (VOB, MPG).
    /// </summary>
    private static readonly HashSet<string> Unindexed = ["mpegts", "mpegtsraw", "mpeg"];

    /// <summary>
    /// Formats FFmpeg seeks by the time a frame is shown, with no margin for B-frames (<c>AVFMT_SEEK_TO_PTS</c>): MOV
    /// and MP4, MXF, NUT, and the security cameras' DHAV and DVD-Video read as a disc.
    /// </summary>
    private static readonly HashSet<string> ByShownTime = ["mov", "mxf", "nut", "dhav", "dvdvideo"];

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
    private static readonly ConcurrentDictionary<string, (double Origin, Seeking How, (long Num, long Den)? Tick)> Formats = new();

    /// <summary>The reach that last found a key frame in each of those files.</summary>
    private static readonly ConcurrentDictionary<string, double> Reaches = new();

    /// <summary>
    /// The first reach in a file without an index, in seconds: its key frames are mostly 0.5 to 10 s apart, and each
    /// look there costs about as much as one in an indexed file.
    /// </summary>
    internal const double UnindexedReach = 2;

    /// <summary>
    /// Where to start reading the first video stream of <paramref name="input"/> (as <c>-map 0:V:0</c> picks it) for
    /// the frame at <paramref name="seconds"/> from the file's start (see the file header). When ffprobe can't read the
    /// file, finds it has no start time (a raw <c>.h264</c> or <c>.m2v</c> stream, whose frames FFmpeg counts from
    /// wherever it starts reading) or finds no key frame within <see cref="LastReach"/>, FFmpeg seeks to the time
    /// itself, as it always did.
    /// </summary>
    public static async Task<SeekPoint> FindAsync(string ffprobe, string input, double seconds, CancellationToken cancellationToken)
    {
        seconds = double.IsFinite(seconds) ? Math.Max(0, seconds) : 0;
        if (await FormatAsync(ffprobe, input, cancellationToken).ConfigureAwait(false) is not var (file, origin, how, tick))
            return SeekPoint.Plain(seconds);
        var unindexed = how.Unindexed;
        // The clip window looks at one file again and again: start where the last look found a key frame.
        var first = Reaches.TryGetValue(file, out var known) ? known : unindexed ? UnindexedReach : FirstReach;
        for (var reach = first; ; reach = Math.Min(reach * 4, LastReach))
        {
            var from = Math.Max(0, seconds - reach);
            var json = await RunAsync(ffprobe, Arguments(input, origin, from, seconds), cancellationToken).ConfigureAwait(false);
            if (json is null)
                return SeekPoint.Plain(seconds);
            (double? From, double? Key) look;
            try
            {
                look = Choose(json, origin, seconds, how, fromFirstPacket: from <= 0);
            }
            catch (JsonException)
            {
                return SeekPoint.Plain(seconds);
            }
            if (look.From is { } found)
            {
                Reaches[file] = reach;
                // A copy keeps what FFmpeg's seek lands on: in an indexed file sought 3/23 s early, the time itself
                // could land a group of pictures early, so it is that much past the key frame then.
                var copy = unindexed ? found
                    : how.Reordered && !how.ByShownTime && look.Key is { } key ? Math.Max(seconds, key + DtsHeuristic) : seconds;
                return new SeekPoint(found, origin, copy, seconds, tick);
            }
            // Before the first key frame (the time is in its leading pictures, or there are none): from the start.
            if (from <= 0)
                return new SeekPoint(0, origin, unindexed ? 0 : seconds, seconds, tick);
            if (reach >= LastReach)
                return SeekPoint.Plain(seconds) with { Origin = origin, Tick = tick };
        }
    }

    /// <summary>
    /// The file's cache key, start time, how FFmpeg seeks it, and its first video stream's time base; null when ffprobe
    /// can't read it or it has no start time.
    /// </summary>
    private static async Task<(string File, double Origin, Seeking How, (long Num, long Den)? Tick)?> FormatAsync(string ffprobe, string input,
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
            return (key, known.Origin, known.How, known.Tick);
        var json = await RunAsync(ffprobe,
            ["-hide_banner", "-v", "error", "-show_entries",
                "format=start_time,format_name:stream=codec_type,has_b_frames,time_base:stream_disposition=attached_pic",
                "-print_format", "json", input],
            cancellationToken).ConfigureAwait(false);
        if (json is null)
            return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            // A stream without timestamps (FFmpeg makes them up from where it starts reading) has no start time either.
            if (!document.RootElement.TryGetProperty("format", out var format) || Time(format, "start_time") is not { } origin)
                return null;
            var names = format.TryGetProperty("format_name", out var name) && name.GetString() is { } text ? text.Split(',') : [];
            List<JsonElement> streams = document.RootElement.TryGetProperty("streams", out var list) && list.ValueKind == JsonValueKind.Array
                ? [.. list.EnumerateArray()] : [];
            // FFmpeg seeks early when any stream is decoded before it is shown, not only the video it reads.
            var reordered = streams.Any(stream => stream.TryGetProperty("has_b_frames", out var delay)
                && delay.ValueKind == JsonValueKind.Number && delay.GetInt32() > 0);
            var video = streams.FirstOrDefault(stream => stream.TryGetProperty("codec_type", out var type) && type.GetString() == "video"
                && !(stream.TryGetProperty("disposition", out var disposition) && disposition.TryGetProperty("attached_pic", out var picture)
                    && picture.ValueKind == JsonValueKind.Number && picture.GetInt32() == 1));
            var tick = video.ValueKind == JsonValueKind.Object && video.TryGetProperty("time_base", out var timeBase)
                && timeBase.GetString()?.Split('/') is [var n, var d]
                && long.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var num)
                && long.TryParse(d, NumberStyles.None, CultureInfo.InvariantCulture, out var den) && num > 0 && den > 0
                ? (num, den) : ((long, long)?)null;
            var found = (origin,
                new Seeking(names.Any(Unindexed.Contains), names.Any(ByShownTime.Contains), reordered), tick);
            // A clip window looks at one file again and again; a queue of conversions goes through many once each.
            if (Formats.Count > 64)
            {
                Formats.Clear();
                Reaches.Clear();
            }
            Formats[key] = found;
            return (key, found.Item1, found.Item2, found.tick);
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
    /// shown, both null when the list holds no key frame shown at or before it. A key frame without a time of its own
    /// to be shown at (AVI with B-frames, MPEG-PS) counts by its decode time, and reading then starts at the key frame
    /// before it, which surely is shown before the time. From the file's first key frame, reading starts at 0.
    /// </summary>
    /// <param name="origin">The file's start time.</param>
    /// <param name="how">How FFmpeg seeks the file.</param>
    /// <param name="fromFirstPacket">The list starts at the file's first packet.</param>
    /// <exception cref="JsonException">The output isn't JSON.</exception>
    internal static (double? From, double? Key) Choose(string json, double origin, double seconds, Seeking how, bool fromFirstPacket)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("packets", out var packets) || packets.ValueKind != JsonValueKind.Array)
            return (null, null);
        List<(double? Shown, double? Decoded)> keys = [];
        foreach (var packet in packets.EnumerateArray())
        {
            if (packet.TryGetProperty("flags", out var flags) && flags.GetString() is { } text && text.Contains('K'))
                keys.Add((Time(packet, "pts_time") - origin, Time(packet, "dts_time") - origin));
        }
        // ffprobe writes six decimals, so a key frame shown exactly at the time may read half a microsecond late.
        var last = keys.FindLastIndex(key => (key.Shown ?? key.Decoded) is { } at && at <= seconds + 5e-7);
        if (last < 0)
            return (null, null);
        if (keys[last].Shown is null)
        {
            if (last == 0)
                return fromFirstPacket ? (0, keys[0].Decoded) : (null, null);
            last--;
        }
        if (last == 0 && fromFirstPacket)
            return (0, keys[0].Shown ?? keys[0].Decoded);
        var (shown, decoded) = keys[last];
        double? start = how.Unindexed
            // Sought by decode time, 3/23 s early with B-frames: from the key frame's decode time, or as much later as
            // still lands on it.
            ? decoded is { } d ? how.Reordered && shown is { } s ? Math.Min(s, d + DtsHeuristic) : d : shown
            // Sought through the index by the time shown: there, with the margin FFmpeg takes off outside MOV and MXF.
            : shown is { } at ? at + (how.Reordered && !how.ByShownTime ? DtsHeuristic : 0) : decoded;
        return start is { } time ? (Math.Clamp(time, 0, seconds), shown ?? decoded) : (null, null);
    }

    private static double? Time(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && double.IsFinite(number)
            ? number
            : null;
}
