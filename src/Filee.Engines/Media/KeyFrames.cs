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
//
// A copy keeps what FFmpeg's seek lands on, which each demuxer decides its own way: an MP4 lands on the last key frame
// decoded by -ss (the next one, in an open group or a fragmented file), a WMV's index names one key frame a second. So
// a copy's -ss is checked where FFmpeg's own copy lands, and its end, which FFmpeg cuts by decode time, is put where no
// frame shown before the last one kept is missing. A key frame ffprobe gives no time to be shown at is shown no later
// than the longest wait between decoding and showing seen near it, or, where nothing near it is shown at a time of its
// own (AVI), as FFmpeg decodes it.

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
/// The time asked for, in seconds from the file's start: time 0 of what is encoded from there
/// (<see cref="EncodeInput"/>).
/// </param>
/// <param name="Tick">The video stream's time base (<c>1/90000</c>); null when unknown.</param>
/// <param name="Key">When the key frame reading starts at is decoded, in seconds from the file's start; null when none
/// was found.</param>
/// <param name="KeyShown">When that key frame is shown, in seconds from the file's start; null when unknown.</param>
/// <param name="CopyAfter">
/// Where a copy starts when no <c>-ss</c> lands it on the key frame (<see cref="KeyFrames.CopyStartAsync"/>): read from
/// the file's start and kept from the key frame on (an output <c>-ss</c> at its decode time), in seconds from the
/// file's start; null otherwise.
/// </param>
/// <param name="CopyTo">
/// Where a copy ends (<see cref="KeyFrames.CopyEndAsync"/>): the first time at or after the end where no frame decoded
/// before it is shown after it; null for the end itself.
/// </param>
/// <param name="CopyFrames">
/// How many video packets a copy keeps, where its end can't be given as a time (an AVI's key and P-frames have no time
/// to be shown at, and FFmpeg's end stops only at a frame that has one); null otherwise.
/// </param>
internal readonly record struct SeekPoint(double From, double? Origin, double CopyFrom, double Start, (long Num, long Den)? Tick = null,
    double? Key = null, double? KeyShown = null, double? CopyAfter = null, double? CopyTo = null, int? CopyFrames = null)
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
    /// the <see cref="Tolerance"/>: to the microsecond, below 0 only in a file whose own times start below 0 (an
    /// MPEG-TS recorded just before its clock wraps round).
    /// </summary>
    public string Time(double seconds) => Origin is { } origin
        ? Number(origin + seconds - Tolerance, signed: true)
        : Number(seconds - From - Tolerance);

    /// <summary>
    /// The input options of what is encoded (a clip, its playback, a Live Photo): with the file's own timestamps, moved
    /// back by <see cref="Shift"/> so that what is kept starts at time 0, and the encoder and the muxer count from
    /// there as they did before (chapters included); nothing kept goes below 0, where an MP4's encoder rounded the
    /// first frame away. FFmpeg's own cut at <c>-ss</c> is left out: it ignores the move, and the trims
    /// (<see cref="Clip"/>, <see cref="VideoTrim"/>) cut instead.
    /// </summary>
    public string[] EncodeInput => Origin is not null
        ? ["-copyts", "-noaccurate_seek", .. MediaEncoding.SeekTo(From),
            .. Shift != 0 ? ["-itsoffset", (-Shift / 1_000_000.0).ToString("0.000000", CultureInfo.InvariantCulture)]
                : Array.Empty<string>()]
        : MediaEncoding.SeekTo(From);

    /// <summary>
    /// How far <see cref="EncodeInput"/> moves the file's timestamps back, in whole microseconds (as FFmpeg reads
    /// <c>-itsoffset</c>): to a microsecond before <see cref="Start"/>, so a frame shown there stays at or after 0 once
    /// FFmpeg rounds the move to the stream's ticks; forward, where the file's own time there is below 0.
    /// </summary>
    private long Shift => Origin is { } origin && (long)Math.Floor((origin + Start) * 1_000_000) - 1 is var shift
        ? origin + Start < 0 ? shift : Math.Max(0, shift)
        : 0;

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
        var ticks = (Int128)Shift * tick.Den;
        var moved = (long)((ticks + (ticks < 0 ? -scale / 2 : scale / 2)) / scale);
        var at = Math.Ceiling((origin + seconds - Tolerance) * tick.Den / tick.Num);
        return Math.Max(0, (long)at - moved);
    }

    private static string Number(double seconds, bool signed = false) =>
        (signed ? seconds : Math.Max(0, seconds)).ToString("0.######", CultureInfo.InvariantCulture);
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
    /// Elementary streams without a container (<c>format_name</c> as ffprobe gives it), whose timestamps FFmpeg makes
    /// up from where it starts reading: some report a start time all the same (MPEG-4 Part 2's <c>m4v</c> one of 0).
    /// </summary>
    private static readonly HashSet<string> RawStreams =
        ["m4v", "h261", "h263", "h264", "hevc", "vvc", "evc", "mpegvideo", "cavsvideo", "avs2", "avs3", "dirac", "vc1", "obu", "mjpeg",
            "mjpeg_2000"];

    /// <summary>
    /// How much earlier than <c>-ss</c> FFmpeg seeks a video with B-frames in other formats, in seconds.
    /// </summary>
    internal const double DtsHeuristic = 3.0 / 23;

    /// <summary>
    /// How long one ffprobe run may take; a look reads packets only, from about <see cref="LastReach"/> before the time
    /// at most (an indexed file from the key frame before that).
    /// </summary>
    private static readonly TimeSpan LookTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The start time and format of inputs looked at lately, by input and its files' size and time.</summary>
    private static readonly ConcurrentDictionary<string, (double Origin, Seeking How, (long Num, long Den)? Tick)> Formats = new();

    /// <summary>The reach of the last look in each of those inputs that found a key frame, or found none.</summary>
    private static readonly ConcurrentDictionary<string, double> Reaches = new();

    /// <summary>When those inputs' key frames without a display time are shown, as decoded (NaN: unknown).</summary>
    private static readonly ConcurrentDictionary<string, double> Shown = new();

    /// <summary>
    /// The first reach in a file without an index, in seconds: its key frames are mostly 0.5 to 10 s apart, and each
    /// look there costs about as much as one in an indexed file.
    /// </summary>
    internal const double UnindexedReach = 2;

    /// <summary>
    /// Where to start reading the first video stream of <paramref name="input"/> (as <c>-map 0:V:0</c> picks it) for
    /// the frame at <paramref name="seconds"/> from the file's start (see the file header). A stream without a
    /// container (a raw <c>.h264</c>, <c>.m2v</c> or <c>.m4v</c>, whose frames FFmpeg counts from wherever it starts
    /// reading) is read from its start. When ffprobe can't read the file or finds no key frame within
    /// <see cref="LastReach"/>, FFmpeg seeks to the time itself, as it always did.
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
            (double? From, double? Key, double? Decoded, double? Unshown) look;
            try
            {
                var measured = new Dictionary<double, double>();
                while ((look = Choose(json, origin, seconds, how, fromFirstPacket: from <= 0, measured)).Unshown is { } ask
                    && !measured.ContainsKey(ask))
                {
                    var decodedAt = string.Create(CultureInfo.InvariantCulture, $"{file}|{ask:R}");
                    if (!Shown.TryGetValue(decodedAt, out var at))
                        Shown[decodedAt] = at = await ShownAsync(ffprobe, input, origin, ask, cancellationToken).ConfigureAwait(false) ?? double.NaN;
                    measured[ask] = at;
                }
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
                return new SeekPoint(found, origin, copy, seconds, tick, look.Decoded, look.Key);
            }
            // Before the first key frame (the time is in its leading pictures, or there are none): from the start.
            if (from <= 0)
                return new SeekPoint(0, origin, unindexed ? 0 : seconds, seconds, tick);
            if (reach >= LastReach)
            {
                // Its next picture is most likely as far from a key frame: one look, not three, before giving up.
                Reaches[file] = LastReach;
                return SeekPoint.Plain(seconds) with { Origin = origin, Tick = tick };
            }
        }
    }

    /// <summary>
    /// <paramref name="seek"/> with the copy's <c>-ss</c> (<see cref="SeekPoint.CopyFrom"/>) where FFmpeg's seek lands
    /// a copy on the key frame found, as FFmpeg itself shows: each demuxer seeks its own way, so the start time can
    /// land a copy elsewhere. An MP4 lands on the last key frame decoded by then, the next one in an open group of
    /// pictures or a fragmented file; a WMV's index names one key frame a second, the one at or before that second, or
    /// none. Its landing only moves on as <c>-ss</c> does, so the times around the key frame are searched; where none
    /// lands on it, the copy reads from the file's start and keeps from the key frame on
    /// (<see cref="SeekPoint.CopyAfter"/>). A file without an index (TS, PS) lands on the key frame's own decode time
    /// already.
    /// </summary>
    public static async Task<SeekPoint> CopyStartAsync(string ffmpeg, string ffprobe, string input, SeekPoint seek,
        CancellationToken cancellationToken)
    {
        if (seek.Origin is not { } origin || seek.Key is not { } key
            || await FormatAsync(ffprobe, input, cancellationToken).ConfigureAwait(false) is not var (_, _, how, _) || how.Unindexed)
            return seek;
        double? below = null, above = null;
        async Task<bool> LandsAsync(double ss)
        {
            if (await LandingAsync(ffmpeg, input, origin, ss, cancellationToken).ConfigureAwait(false) is not var (shown, decoded))
                return false;
            // The key frame is known by the time it is shown where both name one (a Matroska file has no decode times
            // of its own, and FFmpeg's differ from ffprobe's), else by its decode time (an AVI shows none).
            var distance = seek.KeyShown is { } keyShown && shown is { } at ? at - keyShown : decoded - key;
            if (Math.Abs(distance) < 5e-4)
                return true;
            if (distance < 0)
                below = Math.Max(below ?? ss, ss);
            else
                above = Math.Min(above ?? ss, ss);
            return false;
        }
        foreach (var ss in new[] { seek.CopyFrom, seek.From })
        {
            if (await LandsAsync(ss).ConfigureAwait(false))
                return seek with { CopyFrom = ss };
        }
        // Where both land before it, later, but not past the start (a copy reads to its end from there); where both
        // land after it, earlier: up to the longest look.
        var latest = Math.Max(seek.CopyFrom, seek.Start);
        if (above is null && below is { } before && before < latest)
            above = latest + 1e-4;
        for (var step = 0.25; below is null && above is { } high && high > 0 && step <= LastReach; step *= 2)
        {
            if (await LandsAsync(Math.Max(0, high - step)).ConfigureAwait(false))
                return seek with { CopyFrom = Math.Max(0, high - step) };
        }
        for (var i = 0; i < 12 && below is { } lo && above is { } hi && hi - lo > 1e-4; i++)
        {
            if (await LandsAsync((lo + hi) / 2).ConfigureAwait(false))
                return seek with { CopyFrom = (lo + hi) / 2 };
        }
        // No -ss up to the start lands on it: from the last that lands before it, kept from the key frame on (a copy
        // waits for a key frame there). By its decode time: a copy's output -ss drops a packet decoded before it, so
        // with B-frames one at the time it is shown dropped the key frame itself and the copy waited for the next.
        return seek with { CopyFrom = below is { } early && early <= latest ? early : 0, CopyAfter = key };
    }

    /// <summary>
    /// When the first video packet of a copy from <paramref name="ss"/> is shown (null when it has no time of its own)
    /// and decoded, in seconds from the file's start, as FFmpeg's own copy gives it; null when FFmpeg gave none.
    /// </summary>
    private static async Task<(double? Shown, double Decoded)?> LandingAsync(string ffmpeg, string input, double origin, double ss,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(ffmpeg,
                ["-hide_banner", "-nostdin", "-v", "error", "-copyts", .. MediaEncoding.SeekTo(ss), "-i", input, "-map", "0:V:0", "-c", "copy",
                    "-frames:v", "1", "-f", "framecrc", "-"], LookTimeout, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                return null;
            (long Num, long Den)? tb = null;
            foreach (var line in result.StandardOutput.Split('\n'))
            {
                // #tb 0: 1/90000, then 0,      dts,      pts, duration, size, crc
                if (line.StartsWith("#tb 0:", StringComparison.Ordinal) && line[6..].Trim().Split('/') is [var n, var d]
                    && long.TryParse(n, CultureInfo.InvariantCulture, out var num) && long.TryParse(d, CultureInfo.InvariantCulture, out var den) && den > 0)
                    tb = (num, den);
                else if (tb is { } t && !line.StartsWith('#') && line.Split(',') is [_, var dts, var pts, ..]
                    && long.TryParse(dts.Trim(), CultureInfo.InvariantCulture, out var decoded))
                {
                    double? shown = long.TryParse(pts.Trim(), CultureInfo.InvariantCulture, out var p) && p != long.MinValue
                        ? (double)p * t.Num / t.Den - origin : null;
                    return (shown, (double)decoded * t.Num / t.Den - origin);
                }
            }
        }
        catch (Exception ex) when (ex is TimeoutException or Win32Exception or IOException or InvalidOperationException or ConversionRefusedException)
        {
        }
        return null;
    }

    /// <summary>
    /// <paramref name="seek"/> with where a copy to <paramref name="end"/> (from the file's start) ends: FFmpeg's end
    /// cuts by decode time, so with B-frames a frame shown before the end could be cut while one shown after it,
    /// decoded earlier, stayed (69, 71, 72). A copy can't drop that one, the frames before need it: it ends where every
    /// frame shown up to the last one it keeps is kept, a frame or two past the end, as it starts at a key frame. Null
    /// <see cref="SeekPoint.CopyTo"/> when ffprobe can't read the packets, or nothing is reordered.
    /// </summary>
    public static async Task<SeekPoint> CopyEndAsync(string ffprobe, string input, SeekPoint seek, double end, CancellationToken cancellationToken)
    {
        if (seek.Origin is not { } origin || await FormatAsync(ffprobe, input, cancellationToken).ConfigureAwait(false) is not var (_, _, how, _)
            || !how.Reordered)
            return seek;
        // From the key frame the copy starts at where its packets are counted (an AVI), else a little before the end.
        var from = Math.Max(0, end - 2);
        if (seek.Key is { } key && !how.Unindexed && how.Reordered)
            from = Math.Min(from, key);
        var json = await RunAsync(ffprobe,
            ["-hide_banner", "-v", "error", "-select_streams", "V:0", "-read_intervals", Absolute(origin + from) + "%" + Absolute(origin + end + 4),
                "-show_entries", "packet=pts_time,dts_time", "-print_format", "json", input],
            cancellationToken).ConfigureAwait(false);
        if (json is null)
            return seek;
        try
        {
            List<(double? Shown, double Decoded)> list = [];
            using (var document = JsonDocument.Parse(json))
            {
                if (!document.RootElement.TryGetProperty("packets", out var packets) || packets.ValueKind != JsonValueKind.Array)
                    return seek;
                foreach (var packet in packets.EnumerateArray())
                {
                    if (Time(packet, "dts_time") is { } dts)
                        list.Add((Time(packet, "pts_time") - origin, dts - origin));
                }
            }
            // An AVI shows its B-frames when they are decoded and gives its key and P-frames no time to be shown at,
            // so FFmpeg's end, which stops at a frame shown from it on, kept such a frame decoded after the end and
            // left out the B-frames shown before it. Just before one of those, every B-frame shown before the one
            // kept last is kept: before the second from the end on, so those shown before the end, decoded after the
            // first, are too. Given as a count of the packets from the key frame, and its time for the sound.
            if (!how.Unindexed && list.Any(p => p.Shown is null))
            {
                var anchors = list.Where(p => p.Shown is null && p.Decoded >= end - 5e-7).Select(p => p.Decoded).Take(2).ToList();
                return anchors.Count == 2 && seek.Key is { } first
                    ? seek with { CopyTo = anchors[1], CopyFrames = list.Count(p => p.Decoded >= first - 5e-7 && p.Decoded < anchors[1] - 5e-7) }
                    : seek;
            }
            var wait = list.Where(p => p.Shown is not null).Select(p => p.Shown!.Value - p.Decoded).DefaultIfEmpty(0).Max();
            if (list.Any(p => p.Shown is null) && wait <= 0)
                return seek;
            var times = list.Select(p => (Shown: p.Shown ?? p.Decoded + wait, p.Decoded)).ToList();
            // FFmpeg keeps the packets decoded before the cut: from the end on, the first cut (the end, or a packet's
            // decode time, which it then leaves out) after which no frame shown before the last one kept is missing.
            foreach (var cut in times.Select(p => p.Decoded).Where(t => t > end + 5e-7).Prepend(end).Distinct().Order())
            {
                var kept = times.Where(p => p.Decoded < cut - 5e-7).ToList();
                if (kept.Count == 0)
                    return seek;
                var last = kept.Max(p => p.Shown);
                if (!times.Any(p => p.Decoded >= cut - 5e-7 && p.Shown < last - 5e-7))
                    return Math.Abs(cut - end) < 5e-7 ? seek : seek with { CopyTo = cut };
            }
        }
        catch (JsonException)
        {
        }
        return seek;
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
            foreach (var path in FilesIn(input))
            {
                if (File.Exists(path) && new FileInfo(path) is var file)
                    key += string.Create(CultureInfo.InvariantCulture, $"|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
            }
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
            // A stream without timestamps (FFmpeg makes them up from where it starts reading) has no start time either,
            // or reports 0 all the same.
            if (!document.RootElement.TryGetProperty("format", out var format) || Time(format, "start_time") is not { } origin)
                return null;
            var names = format.TryGetProperty("format_name", out var name) && name.GetString() is { } text ? text.Split(',') : [];
            if (names.Any(RawStreams.Contains))
                return null;
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
            (double Origin, Seeking How, (long Num, long Den)? Tick) found = (origin,
                new Seeking(names.Any(Unindexed.Contains), names.Any(ByShownTime.Contains), reordered), tick);
            // A clip window looks at one file again and again; a queue of conversions goes through many once each.
            if (Formats.Count > 64)
            {
                Formats.Clear();
                Reaches.Clear();
                Shown.Clear();
            }
            Formats[key] = found;
            return (key, found.Origin, found.How, found.Tick);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The files <paramref name="input"/> reads: itself, or those a protocol URL of <see cref="MediaInput"/> names (a
    /// .nov export's recording through <c>subfile</c>, a Hikvision recording after its header through <c>concat</c>).
    /// </summary>
    private static string[] FilesIn(string input) =>
        input.StartsWith("subfile,", StringComparison.Ordinal) && input.IndexOf(",,:", StringComparison.Ordinal) is >= 0 and var at
            ? [input[(at + 3)..]]
            : input.StartsWith("concat:", StringComparison.Ordinal) ? input["concat:".Length..].Split('|') : [input];

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
    /// starts reading for the frame at <paramref name="seconds"/> (see the file header), the time its key frame is
    /// shown and the time it is decoded, all null when the list holds no key frame shown at or before it. From the
    /// file's first key frame, reading starts at 0.
    /// </summary>
    /// <remarks>
    /// A key frame without a time to be shown at (AVI with B-frames, MPEG-PS) is shown when it is decoded in a video
    /// without B-frames; with them, no later after its decode time than the longest wait between decoding and showing
    /// in the list (MPEG-2's open groups: three frames, though ffprobe's <c>has_b_frames</c> says one), and reading
    /// starts at the key frame before when that passes the time. Where nothing in the list is shown at a time of its
    /// own (AVI), the key frame is decoded to see (<paramref name="measured"/>, by decode time): until it has been,
    /// <c>Unshown</c> asks for it; NaN, when it couldn't be, passes it over for the one before.
    /// </remarks>
    /// <param name="origin">The file's start time.</param>
    /// <param name="how">How FFmpeg seeks the file.</param>
    /// <param name="fromFirstPacket">The list starts at the file's first packet.</param>
    /// <exception cref="JsonException">The output isn't JSON.</exception>
    internal static (double? From, double? Key, double? Decoded, double? Unshown) Choose(string json, double origin, double seconds, Seeking how,
        bool fromFirstPacket, IReadOnlyDictionary<double, double>? measured = null)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("packets", out var packets) || packets.ValueKind != JsonValueKind.Array)
            return (null, null, null, null);
        List<(double? Shown, double? Decoded)> keys = [];
        double? wait = null;
        foreach (var packet in packets.EnumerateArray())
        {
            var (pts, dts) = (Time(packet, "pts_time") - origin, Time(packet, "dts_time") - origin);
            if (pts is { } shownAt && dts is { } decodedThen)
                wait = Math.Max(wait ?? 0, shownAt - decodedThen);
            if (packet.TryGetProperty("flags", out var flags) && flags.GetString() is { } text && text.Contains('K'))
                keys.Add((pts, dts));
        }
        double? unshown = null;
        for (var i = 0; i < keys.Count; i++)
        {
            if (keys[i] is not (null, { } decoded))
                continue;
            if (!how.Reordered)
                keys[i] = (decoded, decoded);
            else if (wait is > 0)
                keys[i] = (decoded + wait, decoded);
            else if (measured is not null && measured.TryGetValue(decoded, out var seen))
                keys[i] = (double.IsNaN(seen) ? null : seen, decoded);
            else if (decoded <= seconds + 5e-7)
                unshown = decoded;
        }
        // ffprobe writes six decimals, so a key frame shown exactly at the time may read half a microsecond late.
        var last = keys.FindLastIndex(key => (key.Shown ?? key.Decoded) is { } at && at <= seconds + 5e-7);
        if (last < 0)
            return (null, null, null, null);
        if (keys[last].Shown is null)
        {
            if (unshown is { } ask && keys[last].Decoded == ask)
                return (null, null, null, ask);
            if (last == 0)
                return fromFirstPacket ? (0, keys[0].Decoded, keys[0].Decoded, null) : (null, null, null, null);
            last--;
        }
        if (last == 0 && fromFirstPacket)
            return (0, keys[0].Shown ?? keys[0].Decoded, keys[0].Decoded, null);
        var (shown, decodedAt) = keys[last];
        double? start = how.Unindexed
            // Sought by decode time, 3/23 s early with B-frames: from the key frame's decode time, or as much later as
            // still lands on it.
            ? decodedAt is { } d ? how.Reordered && shown is { } s ? Math.Min(s, d + DtsHeuristic) : d : shown
            // Sought through the index by the time shown: there, with the margin FFmpeg takes off outside MOV and MXF.
            : shown is { } at ? at + (how.Reordered && !how.ByShownTime ? DtsHeuristic : 0) : decodedAt;
        return start is { } time ? (Math.Clamp(time, 0, seconds), shown ?? decodedAt, decodedAt, null) : (null, null, null, null);
    }

    /// <summary>
    /// The time the key frame decoded at <paramref name="decoded"/> (from the file's start) is shown at, as FFmpeg
    /// gives it when it decodes the frame; null when reading didn't start at that key frame or it gave none.
    /// </summary>
    private static async Task<double?> ShownAsync(string ffprobe, string input, double origin, double decoded, CancellationToken cancellationToken)
    {
        var json = await RunAsync(ffprobe,
            ["-hide_banner", "-v", "error", "-select_streams", "V:0", "-read_intervals", Absolute(origin + decoded) + "%+#16",
                "-show_entries", "packet=dts_time,flags:frame=key_frame,best_effort_timestamp_time", "-print_format", "json", input],
            cancellationToken).ConfigureAwait(false);
        if (json is null)
            return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("packets_and_frames", out var items) || items.ValueKind != JsonValueKind.Array)
                return null;
            var startedThere = false;
            foreach (var item in items.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "packet" && !startedThere)
                {
                    // Reading must start at that very key frame, or the frame decoded first is another one's.
                    if (!(item.TryGetProperty("flags", out var flags) && flags.GetString()?.Contains('K') == true
                        && Time(item, "dts_time") is { } dts && Math.Abs(dts - origin - decoded) < 5e-7))
                        return null;
                    startedThere = true;
                }
                else if (type == "frame" && item.TryGetProperty("key_frame", out var key) && key.ValueKind == JsonValueKind.Number
                    && key.GetInt32() == 1)
                    return Time(item, "best_effort_timestamp_time") - origin;
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static double? Time(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && double.IsFinite(number)
            ? number
            : null;
}
