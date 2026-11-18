// Exact seeks (#1205): the frame the clip window shows at a time is the frame shown at that time, and a clip, a copy,
// the window's playback and a Live Photo from there start with it, in the files where FFmpeg's own -ss misses: an MP4
// at 30 or 29.97 fps (frame times rounded to the millisecond seeked a frame late), MPEG-TS and MPEG-PS (no index: the
// seek landed between key frames and the decoder skipped ahead to the next one), HEVC with open groups of pictures (the
// leading pictures were skipped), AVI with B-frames (key frames without a time to be shown at) and a TS whose sound
// starts first. Every frame of the test videos carries its number in its pixels, eight black or white blocks, so each
// picture says which frame it is. The real tests run only when FFmpeg is installed.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Media;

namespace Filee.Engines.Tests;

[Collection(nameof(FfmpegCollection))]
public class ExactSeekTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly (string Ffmpeg, string Ffprobe)? Tools = FfmpegConverter.Locate();

    /// <summary>
    /// CI sets it where it installs FFmpeg: a missing FFmpeg or encoder then fails these tests instead of skipping
    /// them.
    /// </summary>
    private static bool Required => Environment.GetEnvironmentVariable("FILEE_TEST_FFMPEG") == "required";

    private static void SkipUnlessInstalled()
    {
        if (Tools is null && Required)
            Assert.Fail("FILEE_TEST_FFMPEG is required, but FFmpeg was not found.");
        Assert.SkipUnless(Tools is not null, "FFmpeg is not installed (pwsh build/fetch-engines.ps1 -Only ffmpeg)");
        // CI names the FFmpeg it means to test, so a fallback to another one can't pass unnoticed.
        if (Environment.GetEnvironmentVariable("FILEE_TEST_FFMPEG_FOLDER") is { Length: > 0 } folder)
            Assert.StartsWith(Path.GetFullPath(folder), Path.GetFullPath(Tools!.Value.Ffmpeg), StringComparison.Ordinal);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ───────────────────────── The key frame (no FFmpeg needed) ─────────────────────────

    /// <summary>
    /// ffprobe's packet list: key frames and others, with the times they are shown and decoded at (null: N/A).
    /// </summary>
    private static string Packets(params (double? Shown, double? Decoded, bool Key)[] packets) =>
        JsonSerializer.Serialize(new
        {
            packets = packets.Select(p => new Dictionary<string, string>
            {
                ["pts_time"] = p.Shown is { } pts ? pts.ToString("0.000000", CultureInfo.InvariantCulture) : "N/A",
                ["dts_time"] = p.Decoded is { } dts ? dts.ToString("0.000000", CultureInfo.InvariantCulture) : "N/A",
                ["flags"] = p.Key ? "K__" : "___",
            }),
        });

    /// <summary>How FFmpeg seeks an MPEG-TS, and an MKV or AVI, without B-frames.</summary>
    private static readonly Seeking Ts1 = new(Unindexed: true, ByShownTime: false, Reordered: false), Index1 = new(false, false, false);

    [Fact]
    public void A_file_without_an_index_is_read_from_the_decode_time_of_the_last_key_frame_shown_before_the_time()
    {
        // An MPEG-TS file whose times start at 1.4 s: key frames shown at 2 s and 3 s of the file, decoded a frame
        // earlier.
        var json = Packets((3.4, 3.3667, true), (3.4333, 3.4, false), (4.4, 4.3667, true), (4.4333, 4.4, false));

        // FFmpeg seeks such a file by decode time onto any packet: from the key frame's own, it lands on the key frame.
        Assert.Equal(1.9667, KeyFrames.Choose(json, 1.4, 2.5, how: Ts1, fromFirstPacket: false).From!.Value, 6);
        Assert.Equal(1.9667, KeyFrames.Choose(json, 1.4, 2.0, how: Ts1, fromFirstPacket: false).From!.Value, 6); // the key frame itself
        Assert.Equal(2.9667, KeyFrames.Choose(json, 1.4, 3.2, how: Ts1, fromFirstPacket: false).From!.Value, 6);
        // Before the first key frame read: none in this look.
        Assert.Null(KeyFrames.Choose(json, 1.4, 1.9, how: Ts1, fromFirstPacket: false).From);
    }

    [Fact]
    public void Any_other_file_is_read_from_the_time_its_key_frame_is_shown()
    {
        // MP4, MKV, AVI: FFmpeg seeks through an index of key frames by the time they are shown; at the decode time it
        // landed on the key frame before, a whole group of pictures early.
        var json = Packets((2.0, 1.9333, true), (4.0, 3.8, true));
        Assert.Equal(2.0, KeyFrames.Choose(json, 0, 3, how: Index1, fromFirstPacket: false).From!.Value, 6);
        Assert.Equal(4.0, KeyFrames.Choose(json, 0, 4.5, how: Index1, fromFirstPacket: false).From!.Value, 6);
    }

    [Fact]
    public void A_key_frame_whose_leading_pictures_are_asked_for_is_passed_over()
    {
        // HEVC with an open group: the key frame shown at 2 s is decoded at 1.8 s, before the pictures shown at 1.87 to
        // 1.97 s that lead it and need the group before. Starting at it would skip them, so the key frame at 0 is
        // taken.
        var json = Packets((0, -0.0667, true), (2.0, 1.8, true), (1.8667, 1.8333, false), (1.9333, 1.8667, false));
        Assert.Equal(0, KeyFrames.Choose(json, 0, 1.8667, how: Ts1, fromFirstPacket: false).From); // before 0 counts as 0
        Assert.Equal(1.8, KeyFrames.Choose(json, 0, 2.0, how: Ts1, fromFirstPacket: false).From!.Value, 6);
    }

    [Fact]
    public void A_key_frame_without_a_time_to_be_shown_at_is_shown_by_its_decode_time_and_the_longest_wait()
    {
        // MPEG-PS: ffprobe gives some key frames a decode time only. Shown no later than the longest wait between
        // decoding and showing in the look (0.12 s here, though has_b_frames says one frame), the one decoded at 0.48 s
        // is shown by 0.6 s: taken for 0.65 s (read from 0.6 s, as FFmpeg seeks 3/23 s before that, still before its
        // decode time), passed over for 0.55 s.
        var ps = new Seeking(Unindexed: true, ByShownTime: false, Reordered: true);
        var json = Packets((0.12, 0, true), (0.2, 0.08, false), (0.16, 0.12, false), (null, 0.48, true), (0.72, 0.6, false));
        Assert.Equal((0.6, 0.48), KeyFrames.Choose(json, 0, 0.65, how: ps, fromFirstPacket: false) is var (from, _, decoded, _)
            ? (Math.Round(from!.Value, 6), decoded!.Value) : default);
        Assert.Equal(0, KeyFrames.Choose(json, 0, 0.55, how: ps, fromFirstPacket: true).From);
        // Without B-frames, a key frame is shown when it is decoded.
        var plain = Packets((0.5, 0.5, true), (null, 1.5, true));
        Assert.Equal(1.5, KeyFrames.Choose(plain, 0, 1.6, how: Index1, fromFirstPacket: false).From!.Value, 6);
    }

    [Fact]
    public void A_key_frame_shown_at_no_time_in_the_look_is_decoded_to_see()
    {
        // AVI with B-frames: no packet is shown at a time of its own. The key frame decoded at 1.4333 s is asked for;
        // decoded, it is shown at 1.4667 s and taken, or, when it couldn't be, passed over for the one before.
        var avi = new Seeking(Unindexed: false, ByShownTime: false, Reordered: true);
        var json = Packets((null, 0.4667, true), (null, 0.5, false), (null, 1.4333, true));
        Assert.Equal(1.4333, KeyFrames.Choose(json, 0, 1.6, avi, fromFirstPacket: false).Unshown!.Value, 6);
        var seen = KeyFrames.Choose(json, 0, 1.6, avi, fromFirstPacket: false, new Dictionary<double, double> { [1.4333] = 1.4667, [0.4667] = 0.5 });
        Assert.Equal((1.4667 + KeyFrames.DtsHeuristic, 1.4667, 1.4333), (seen.From!.Value, seen.Key!.Value, seen.Decoded!.Value));
        var unknown = KeyFrames.Choose(json, 0, 1.6, avi, fromFirstPacket: false, new Dictionary<double, double> { [1.4333] = double.NaN, [0.4667] = 0.5 });
        Assert.Equal(0.5 + KeyFrames.DtsHeuristic, unknown.From!.Value, 6);
        // The file's first key frame: from the start; further on, none in this look.
        var first = Packets((null, 0.4, true), (null, 1.4, true));
        var none = new Dictionary<double, double> { [0.4] = double.NaN, [1.4] = double.NaN };
        Assert.Equal(0, KeyFrames.Choose(first, 0, 1.0, avi, fromFirstPacket: true, none).From);
        Assert.Null(KeyFrames.Choose(first, 0, 1.0, avi, fromFirstPacket: false, none).From);
    }

    [Fact]
    public void From_the_files_first_key_frame_reading_starts_at_its_first_packet()
    {
        // An FLV whose sound starts first: its first key frame is shown 23 ms in. -ss 0.023 made FFmpeg seek before the
        // file's start (3/23 s early, B-frames), which failed and landed between key frames: frame 45 for the first 45.
        var json = Packets((0.067, 0, true), (1.567, 1.5, true));
        var flv = new Seeking(Unindexed: false, ByShownTime: false, Reordered: true);
        Assert.Equal(0, KeyFrames.Choose(json, 0.044, 0.7, flv, fromFirstPacket: true).From);
        // The same key frame in a look that didn't start at the first packet may be any key frame.
        Assert.Equal(0.023 + KeyFrames.DtsHeuristic, KeyFrames.Choose(json, 0.044, 0.7, flv, fromFirstPacket: false).From!.Value, 6);
    }

    [Fact]
    public void With_b_frames_ss_allows_for_the_3_23_s_ffmpeg_seeks_early_where_it_does()
    {
        var json = Packets((0.5, 0.4333, true), (10.0, 9.8, true));
        // MKV, AVI, FLV: FFmpeg seeks 3/23 s before -ss, so -ss is that much past the key frame, or the key frame
        // before it is read, a whole group of pictures more; never past the time itself.
        var mkv = new Seeking(Unindexed: false, ByShownTime: false, Reordered: true);
        Assert.Equal(10 + KeyFrames.DtsHeuristic, KeyFrames.Choose(json, 0, 12, mkv, fromFirstPacket: false).From!.Value, 6);
        Assert.Equal(10.05, KeyFrames.Choose(json, 0, 10.05, mkv, fromFirstPacket: false).From!.Value, 6);
        // MP4 and MOV are sought by the time shown, with no margin.
        Assert.Equal(10, KeyFrames.Choose(json, 0, 12, mkv with { ByShownTime = true }, fromFirstPacket: false).From!.Value, 6);
        // MPEG-TS: from the decode time, or as much later as the margin still lands on the key frame.
        var ts = new Seeking(Unindexed: true, ByShownTime: false, Reordered: true);
        Assert.Equal(9.8 + KeyFrames.DtsHeuristic, KeyFrames.Choose(json, 0, 12, ts, fromFirstPacket: false).From!.Value, 6);
        Assert.Equal(10, KeyFrames.Choose(Packets((10.0, 9.95, true)), 0, 12, ts, fromFirstPacket: false).From!.Value, 6);
    }

    [Fact]
    public void A_key_frame_read_half_a_microsecond_late_is_the_frame_at_the_time()
    {
        // ffprobe writes six decimals: 1.000001 for a key frame shown at the 1.0000006 s asked for.
        var json = """{"packets":[{"pts_time":"1.000001","dts_time":"N/A","flags":"K__"}]}""";
        // Reading never starts past the time itself.
        Assert.Equal(1.0000006, KeyFrames.Choose(json, 0, 1.0000006, how: Index1, fromFirstPacket: false).From!.Value, 7);
        Assert.Null(KeyFrames.Choose("""{"packets":[]}""", 0, 1, how: Index1, fromFirstPacket: true).From);
        Assert.Null(KeyFrames.Choose("{}", 0, 1, how: Index1, fromFirstPacket: true).From);
    }

    [Fact]
    public void A_look_reads_packets_between_times_of_the_file_itself()
    {
        // In the file's own time (it starts at 1.5 s), a tenth of a second past the time; from 0, from its first
        // packet, as the key frame at its start may be decoded before the file's start time.
        var args = KeyFrames.Arguments("in.ts", 1.5, 3, 5);
        Assert.Equal("4.500000%6.600000", args[args.IndexOf("-read_intervals") + 1]);
        Assert.Equal("%6.600000", KeyFrames.Arguments("in.ts", 1.5, 0, 5)[args.IndexOf("-read_intervals") + 1]);
        Assert.Equal("V:0", args[args.IndexOf("-select_streams") + 1]);
        Assert.Equal("in.ts", args[^1]);
    }

    [Fact]
    public void A_seek_point_names_times_as_the_files_own_timestamps_or_as_before()
    {
        // Read from the key frame at 60 s of a file whose times start at 1.4 s, for 61.25 s: what is encoded is moved
        // back to a microsecond before it, so nothing kept is below 0.
        var exact = new SeekPoint(60, 1.4, 62, 61.25, (1, 90000));
        Assert.Equal(["-copyts", "-ss", "60.000000"], exact.Input);
        Assert.Equal("62.649999", exact.Time(61.25)); // a microsecond early: a frame read that close counts
        Assert.Equal(["-copyts", "-noaccurate_seek", "-ss", "60.000000", "-itsoffset", "-62.649999"], exact.EncodeInput);
        Assert.Equal("0", exact.Clip(61.25));
        Assert.Equal("1.75", exact.Clip(63));
        // Trims in the stream's own ticks, moved as FFmpeg rounds the move: the first frame at or after each time.
        Assert.Equal("trim=start_pts=0:end_pts=157500", exact.VideoTrim(61.25, 63));
        // A time base finer than a microsecond (MP4 at 10 MHz): the move is a whole number of its ticks.
        Assert.Equal("trim=start_pts=0:end_pts=17500001", (exact with { Tick = (1, 10_000_000) }).VideoTrim(61.25, 63));
        // FFmpeg rounds the move 1.00135 s to 90121.5 ticks, and up (in doubles, 90121.49999…): the frame at tick
        // 90122, the first at or after 1.001351 s, is tick 0 once moved, and kept.
        Assert.Equal("trim=start_pts=0", new SeekPoint(0, 0, 0, 1.001351, (1, 90000)).VideoTrim(1.001351));
        // A TS whose clock wraps 2.5 s in, so its own times start at -2.5 s: 1 s in is moved forward, by 1.500001 s,
        // which FFmpeg rounds to -135000 ticks (halves away from 0 either way); 2 s in is then tick 90000.
        var wrapped = new SeekPoint(0, -2.5, 0, 1, (1, 90000));
        Assert.Equal("-1.500001", wrapped.Time(1));
        Assert.Equal(["-copyts", "-noaccurate_seek", "-itsoffset", "1.500001"], wrapped.EncodeInput);
        Assert.Equal("trim=start_pts=0:end_pts=90000", wrapped.VideoTrim(1, 2));
        // Without the file's start time, as Filee seeked before: straight to the time, counting from there.
        var plain = SeekPoint.Plain(61.25);
        Assert.Equal(["-ss", "61.250000"], plain.Input);
        Assert.Equal(["-ss", "61.250000"], plain.EncodeInput);
        Assert.Equal("0", plain.Time(61.25));
        Assert.Equal("1.499999", plain.Time(62.75));
        Assert.Equal("trim=start=0:end=1.499999", plain.VideoTrim(61.25, 62.75));
        Assert.Empty(SeekPoint.Plain(0).Input);
    }

    [Fact]
    public async Task The_previews_sound_is_read_from_where_its_pictures_are()
    {
        // Pictures and sound play from two FFmpeg runs: both read with the file's own timestamps, from the same key
        // frame.
        var clip = ClipEdit.Of(new ClipRange(12.5, 20));
        var hd = new VideoStreamInfo(1920, 1080, 1, 0, 30, "h264", "yuv420p");
        var args = await ClipPreview.PreviewSoundArgumentsAsync("ffprobe", "in.ts", clip, hd, new AudioStreamInfo(2, 48000, "aac"), Ct,
            new SeekPoint(10, 1.4, 10, 12.5, (1, 90000)));
        Assert.Equal(["-copyts", "-noaccurate_seek", "-ss", "10.000000", "-itsoffset", "-13.899999"], args.SkipWhile(a => a != "-copyts").Take(6));
        Assert.Contains("atrim=start=0:end=7.5", args[args.IndexOf("-filter_complex") + 1]);
    }

    [Fact]
    public async Task Without_ffprobe_a_frame_is_seeked_as_before()
    {
        var missing = Path.Combine(fx.NewFolder(), EngineEnvironment.ProgramName("ffprobe.exe"));
        Assert.Equal(SeekPoint.Plain(12.5), await KeyFrames.FindAsync(missing, "in.mp4", 12.5, Ct));
        Assert.Equal(SeekPoint.Plain(0), await KeyFrames.FindAsync(missing, "in.mp4", double.NaN, Ct));
    }

    [Fact]
    public async Task A_file_ffprobe_cant_read_is_seeked_as_before()
    {
        SkipUnlessInstalled();
        var junk = Path.Combine(fx.NewFolder(), "junk.mp4");
        await File.WriteAllTextAsync(junk, "not a video", Ct);
        Assert.Equal(SeekPoint.Plain(7), await KeyFrames.FindAsync(Tools!.Value.Ffprobe, junk, 7, Ct));
    }

    [Theory]
    [InlineData("raw.h264", "libx264", false)]
    [InlineData("raw.m2v", "mpeg2video", true)]
    [InlineData("raw.m4v", "mpeg4", true, "m4v")] // its demuxer reports a start time of 0 all the same
    public async Task A_stream_without_timestamps_is_seeked_as_before(string name, string codec, bool picture, string? format = null)
    {
        SkipUnlessInstalled();
        // FFmpeg makes up a raw stream's timestamps from wherever it starts reading, so no time in it can be named:
        // read as its own times, the MPEG-2 stream's picture at 14 s was missing. (FFmpeg's own seek finds no picture
        // in a raw H.264 stream at all.)
        var video = await NumberedAsync(name, "25", 20, ["-c:v", codec, "-g", "25", .. format is null ? Array.Empty<string>() : ["-f", format]]);
        Assert.Equal(SeekPoint.Plain(14), await KeyFrames.FindAsync(Tools!.Value.Ffprobe, video, 14, Ct));
        if (picture)
            Assert.True(await VideoFrames.WriteFrameAsync(video, 14, Path.Combine(fx.NewFolder(), "14.jpg"), Height, Ct));
    }

    // ───────────────────────── Numbered videos ─────────────────────────

    private const int Width = 128, Height = 32;

    /// <summary>
    /// The number a picture carries: across its width, block b of eight is white when bit b is set.
    /// </summary>
    private static int Number(byte[] gray, int offset = 0, int width = Width, int height = Height, int bytesPerPixel = 1)
    {
        var row = offset + height / 2 * width * bytesPerPixel;
        var number = 0;
        for (var bit = 0; bit < 8; bit++)
        {
            var x = (bit * 2 + 1) * width / 16;
            if (gray[row + x * bytesPerPixel] > 128)
                number |= 1 << bit;
        }
        return number;
    }

    /// <summary>
    /// A video of <paramref name="seconds"/> at <paramref name="rate"/> whose frame N shows N, with a beep in frame
    /// <paramref name="beepFrame"/> when given (or a quiet tone with <paramref name="tone"/>). Skipped (failed in CI)
    /// when this FFmpeg lacks the encoder.
    /// </summary>
    private async Task<string> NumberedAsync(string name, string rate, double seconds, string[] codec, int? beepFrame = null, bool tone = false,
        string size = "128x32")
    {
        var path = Path.Combine(fx.NewFolder(), name);
        var numbers = "geq=lum='if(mod(floor(N/pow(2\\,floor(X*8/W)))\\,2)\\,235\\,16)':cb=128:cr=128";
        var length = seconds.ToString(CultureInfo.InvariantCulture);
        List<string> args =
        [
            .. MediaEncoding.Common, "-f", "lavfi", "-i", $"color=black:s={size}:r={rate}:d={length},format=yuv420p,{numbers}",
        ];
        if (beepFrame is { } beep)
            args.AddRange(["-f", "lavfi", "-i",
                $"aevalsrc='if(between(t\\,{beep}/({rate})\\,{beep + 1}/({rate}))\\,0.8*sin(2*PI*1000*t)\\,0)':s=48000:d={length}", "-c:a", "aac"]);
        else if (tone)
            args.AddRange(["-f", "lavfi", "-i", $"sine=frequency=500:d={length}"]);
        args.AddRange([.. codec, path]);
        var result = await ProcessRunner.RunAsync(Tools!.Value.Ffmpeg, args, TimeSpan.FromMinutes(2), Ct);
        if (result.ExitCode != 0 && result.StandardError.Contains("Unknown encoder", StringComparison.OrdinalIgnoreCase))
        {
            if (Required)
                Assert.Fail($"FILEE_TEST_FFMPEG is required, but this FFmpeg can't write {name}.");
            Assert.Skip($"This FFmpeg can't write {name}");
        }
        Assert.True(result.ExitCode == 0, result.StandardError);
        return path;
    }

    /// <summary>
    /// Every frame of the video in the order shown: its time from the file's start, the number it shows, and whether
    /// it is a key frame.
    /// </summary>
    private async Task<List<(double Time, int Number, bool Key)>> FramesAsync(string path)
    {
        var raw = Path.Combine(fx.NewFolder(), "frames.gray");
        var decoded = await ProcessRunner.RunAsync(Tools!.Value.Ffmpeg,
            [.. MediaEncoding.Common, "-i", path, "-map", "0:V:0", "-fps_mode", "passthrough", "-vf", $"scale={Width}:{Height}",
                "-f", "rawvideo", "-pix_fmt", "gray", raw], TimeSpan.FromMinutes(2), Ct);
        Assert.True(decoded.ExitCode == 0, decoded.StandardError);
        var probed = await ProcessRunner.RunAsync(Tools!.Value.Ffprobe,
            ["-v", "error", "-select_streams", "V:0", "-show_entries", "frame=best_effort_timestamp_time,pts_time,pkt_dts_time,key_frame:format=start_time",
                "-print_format", "json", path], TimeSpan.FromMinutes(1), Ct);
        Assert.True(probed.ExitCode == 0, probed.StandardError);
        using var json = JsonDocument.Parse(probed.StandardOutput);
        var start = Seconds(json.RootElement.GetProperty("format"), "start_time") ?? 0;
        var bytes = await File.ReadAllBytesAsync(raw, Ct);
        var frames = new List<(double, int, bool)>();
        var index = 0;
        foreach (var frame in json.RootElement.GetProperty("frames").EnumerateArray())
        {
            var time = Seconds(frame, "best_effort_timestamp_time") ?? Seconds(frame, "pts_time") ?? Seconds(frame, "pkt_dts_time");
            if (time is { } t && (index + 1) * Width * Height <= bytes.Length)
                frames.Add((t - start, Number(bytes, index * Width * Height),
                    frame.TryGetProperty("key_frame", out var key) && key.ValueKind == JsonValueKind.Number && key.GetInt32() == 1));
            index++;
        }
        Assert.NotEmpty(frames);
        return frames;
    }

    private static double? Seconds(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            ? n : null;

    /// <summary>The number the picture in <paramref name="path"/> (a JPEG, a PNG, a photo) shows.</summary>
    private async Task<int> NumberOfPictureAsync(string path)
    {
        var raw = Path.Combine(fx.NewFolder(), "picture.gray");
        var result = await ProcessRunner.RunAsync(Tools!.Value.Ffmpeg,
            [.. MediaEncoding.Common, "-i", path, "-frames:v", "1", "-vf", $"scale={Width}:{Height}", "-f", "rawvideo", "-pix_fmt", "gray", raw],
            TimeSpan.FromMinutes(1), Ct);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return Number(await File.ReadAllBytesAsync(raw, Ct));
    }

    private static readonly string[] Mp4 = ["-c:v", "libx264", "-g", "45", "-x264-params", "scenecut=0", "-pix_fmt", "yuv420p"];
    private static readonly string[] Ts = ["-c:v", "libx264", "-g", "45", "-x264-params", "scenecut=0", "-pix_fmt", "yuv420p", "-f", "mpegts"];

    /// <summary>The test videos of the theory below: name, frame rate, size, and FFmpeg's options for them.</summary>
    private static (string Rate, string Size, string[] Codec) VideoOf(string name) => name switch
    {
        "30fps.mp4" => ("30", "128x32", Mp4),
        "2997fps.mp4" => ("30000/1001", "128x32", Mp4),
        "15fps.mp4" => ("15", "128x32", Mp4),
        "30fps.ts" or "soundfirst.ts" => ("30", "128x32", Ts),
        // A DVD's MPEG-2 with B-frames: some key frames carry a decode time only.
        "dvd.vob" => ("30000/1001", "720x480", ["-target", "ntsc-dvd", "-bf", "2"]),
        "opengop.mp4" => ("30", "128x32", ["-c:v", "libx265", "-x265-params", "log-level=error:keyint=45:open-gop=1", "-tag:v", "hvc1",
            "-pix_fmt", "yuv420p"]),
        "opengop.ts" => ("30", "128x32", ["-c:v", "libx265", "-x265-params", "log-level=error:keyint=45", "-pix_fmt", "yuv420p", "-f", "mpegts"]),
        "bframes.avi" => ("30", "128x32", ["-c:v", "mpeg4", "-bf", "2", "-g", "32", "-q:v", "3"]),
        // Every seventh frame left out, the others at their own times.
        "sound.avi" => ("30", "128x32", ["-c:v", "mpeg4", "-bf", "2", "-g", "32", "-q:v", "3", "-c:a", "pcm_s16le"]),
        "bframes.mkv" => ("30", "128x32", ["-c:v", "libx264", "-g", "32", "-x264-params", "scenecut=0", "-pix_fmt", "yuv420p"]),
        // B-frames decoded 2/15 s before they are shown, longer than FFmpeg's 3/23 s margin.
        "15fps.ts" => ("15", "128x32", ["-c:v", "libx264", "-g", "30", "-x264-params", "scenecut=0", "-pix_fmt", "yuv420p", "-f", "mpegts"]),
        // An FLV whose sound starts before its first key frame is shown.
        "sound.flv" => ("30", "128x32", ["-c:v", "libx264", "-g", "45", "-x264-params", "scenecut=0", "-pix_fmt", "yuv420p", "-c:a", "aac",
            "-ar", "44100"]),
        "60fps.mp4" => ("60", "128x32", ["-c:v", "libx264", "-g", "90", "-x264-params", "scenecut=0", "-pix_fmt", "yuv420p"]),
        "120fps.mp4" => ("120", "128x32", ["-c:v", "libx264", "-g", "180", "-x264-params", "scenecut=0", "-pix_fmt", "yuv420p"]),
        "240fps.mp4" => ("240", "128x32", ["-c:v", "libx264", "-g", "360", "-x264-params", "scenecut=0", "-pix_fmt", "yuv420p"]),
        // Fragments without an index of them: FFmpeg's seek lands on the last key frame read by then.
        "frag.mp4" => ("30", "128x32", [.. Mp4, "-movflags", "frag_keyframe+empty_moov"]),
        // ASF's index names one key frame a second: the one at or before that second.
        "30fps.wmv" => ("30", "128x32", ["-c:v", "wmv2", "-g", "45", "-q:v", "3"]),
        "sound.wmv" => ("30", "128x32", ["-c:v", "wmv2", "-g", "45", "-q:v", "3", "-c:a", "wmav2"]),
        // MPEG-1 in a program stream: some key frames carry a decode time only.
        "mpeg1.mpg" => ("25", "720x480", ["-c:v", "mpeg1video", "-g", "15", "-bf", "2", "-q:v", "4", "-f", "mpeg"]),
        // Every second frame shown 4 ms late, as a camera's frames come a little off its rate.
        "jitter.mp4" => ("30", "128x32", [.. Mp4, "-vf", "settb=1/90000,setpts=PTS+mod(N\\,2)*0.004/TB", "-fps_mode", "passthrough",
            "-enc_time_base", "1/90000", "-video_track_timescale", "90000"]),
        // Matroska counts in milliseconds: frame 53 is shown at 1.767 s, after its time 1.7666… on a 1/30 s grid.
        "30fps.mkv" => ("30", "128x32", ["-c:v", "libx264", "-g", "45", "-x264-params", "scenecut=0", "-pix_fmt", "yuv420p"]),
        // Frames 10 ms off the 1/30 s grid an encoder counts in.
        "offgrid.mp4" => ("30", "128x32", [.. Mp4, "-output_ts_offset", "0.01"]),
        // A time base finer than a microsecond: a frame is shown 333,333 or 333,334 ticks after the one before.
        "10mhz.mp4" => ("30", "128x32", [.. Mp4, "-video_track_timescale", "10000000"]),
        // An AVI counts in whole frames.
        "mjpeg.avi" => ("30", "128x32", ["-c:v", "mjpeg", "-q:v", "3"]),
        // B-frames decoded 0.4 s before they are shown, far longer than FFmpeg's 3/23 s margin.
        "5fps.ts" => ("5", "128x32", ["-c:v", "libx264", "-g", "10", "-bf", "3", "-x264-params", "scenecut=0:b-pyramid=normal", "-pix_fmt", "yuv420p",
            "-f", "mpegts"]),
        // Recorded just before its 26.5 h clock wraps round, at frame 70: FFmpeg gives its times from -2.32 s.
        "wrap.ts" => ("30", "128x32", [.. Ts, "-output_ts_offset", "95440"]),
        _ => throw new ArgumentException(name, nameof(name)),
    };

    public static TheoryData<string> Videos =>
        ["30fps.mp4", "2997fps.mp4", "15fps.mp4", "30fps.ts", "15fps.ts", "soundfirst.ts", "dvd.vob", "opengop.mp4", "opengop.ts", "bframes.avi",
            "bframes.mkv", "sound.flv", "10mhz.mp4", "wrap.ts"];

    private async Task<string> VideoAsync(string name, double seconds = 3)
    {
        var (rate, size, codec) = VideoOf(name);
        // With sound, an MPEG-TS file starts with it, and its frames are shown at times that aren't round.
        return await NumberedAsync(name, rate, seconds, codec, beepFrame: name is "soundfirst.ts" or "sound.wmv" or "sound.avi" ? 50 : null, tone: name == "sound.flv",
            size: size);
    }

    // ───────────────────────── The clip window's picture ─────────────────────────

    [Theory]
    [MemberData(nameof(Videos))]
    public async Task Every_frame_the_window_asks_for_is_the_frame_shown_at_that_time(string name)
    {
        SkipUnlessInstalled();
        var video = await VideoAsync(name);
        var frames = await FramesAsync(video);
        Assert.InRange(frames.Count, 40, 95); // 3 s, as decoded

        var folder = fx.NewFolder();
        List<string> wrong = [];
        async Task CheckAsync(double time, int number)
        {
            var output = Path.Combine(folder, $"{number}.jpg");
            Assert.True(await VideoFrames.WriteFrameAsync(video, time, output, Height, Ct), $"no frame at {time}");
            if (await NumberOfPictureAsync(output) is var shown && shown != number)
                wrong.Add($"{time:0.000000} s: frame {shown} instead of {number}");
        }
        foreach (var (time, number, _) in frames)
            await CheckAsync(time, number);
        // A time between two frames shows the later one, the first frame shown at or after it: also a third of the way,
        // where a time base of whole frames (AVI) rounds back to the earlier one.
        for (var i = 3; i + 1 < frames.Count; i += 7)
            await CheckAsync(frames[i].Time + (frames[i + 1].Time - frames[i].Time) * 0.3, frames[i + 1].Number);
        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
    }

    [Fact]
    public async Task A_frame_long_after_the_last_key_frame_is_found_by_looking_further_back()
    {
        SkipUnlessInstalled();
        // One key frame only, at the start of an MPEG-TS file: the first looks back (2 s, then 8 s) find none.
        var video = await NumberedAsync("long.ts", "25", 10,
            ["-c:v", "libx264", "-g", "1000", "-x264-params", "scenecut=0", "-pix_fmt", "yuv420p", "-f", "mpegts"]);
        var frames = await FramesAsync(video);
        var seek = await KeyFrames.FindAsync(Tools!.Value.Ffprobe, video, frames[^3].Time, Ct);
        Assert.Equal(0, seek.From);
        Assert.NotNull(seek.Origin);

        var folder = fx.NewFolder();
        foreach (var (time, number, _) in frames.Where((_, i) => i % 40 == 7).Append(frames[^3]))
        {
            var output = Path.Combine(folder, $"{number}.jpg");
            Assert.True(await VideoFrames.WriteFrameAsync(video, time, output, Height, Ct));
            Assert.Equal(number, await NumberOfPictureAsync(output));
        }
    }

    // ───────────────────────── Clips, playback and Live Photos ─────────────────────────

    private async Task<string> CutAsync(string source, ClipEdit edit, bool exact, string target = "mp4", bool removeAudio = false)
    {
        var tool = BuiltInData.CreatePresets().Single(p => p.Id == "tool-clip");
        tool.Media.ExactCut = exact;
        tool.Media.RemoveAudio = removeAudio;
        var job = await fx.ConvertAsync([source], ClipTool.ConversionOf(tool, target, edit));
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return Assert.Single(job.Outputs);
    }

    [Theory]
    [InlineData("30fps.mkv", 53, 83)] // to MP4: an encoder rounding the file's own times put frame 53 before time 0
    [InlineData("soundfirst.ts", 40, 70)]
    [InlineData("opengop.ts", 53, 83)] // decoded long before shown: an end counted from the first frame was a frame late
    [InlineData("15fps.mp4", 26, 41)]
    [InlineData("bframes.avi", 40, 70)]
    // Key frames with a decode time only: a clip timed from the first frame's display time had none to go by.
    [InlineData("dvd.vob", 40, 70)]
    // A time base finer than the microseconds the move is given in: frame 41 is shown at 1.3666667 s, which ffprobe
    // names 1.366667 s, so a move to that time would put the frame before 0.
    [InlineData("10mhz.mp4", 41, 70)]
    [InlineData("wrap.ts", 40, 70)] // across the wrap of its clock
    public async Task An_exact_clip_starts_and_ends_with_the_chosen_frames(string name, int first, int end)
    {
        SkipUnlessInstalled();
        var video = await VideoAsync(name);
        var frames = await FramesAsync(video);

        var clip = await CutAsync(video, ClipEdit.Of(new ClipRange(frames[first].Time, frames[end].Time)), exact: true);

        Assert.Equal(frames[first..end].Select(f => f.Number), (await FramesAsync(clip)).Select(f => f.Number));
    }

    [Fact]
    public async Task An_edited_clip_of_an_mpeg_ts_file_keeps_its_picture_and_sound_together()
    {
        SkipUnlessInstalled();
        // The beep sounds in frame 50. A graph that started the picture at a later key frame showed frame 50 too early.
        var video = await VideoAsync("soundfirst.ts", 4);
        var frames = await FramesAsync(video);
        var edit = ClipEdit.Of(new ClipRange(frames[40].Time, frames[70].Time));
        edit.Parts[0].Volume = 0.9; // an edit: the clip goes through the filter graph

        var clip = await CutAsync(video, edit, exact: true);

        Assert.Equal(Enumerable.Range(40, 30), (await FramesAsync(clip)).Select(f => f.Number));
        Assert.InRange(await BeepAsync(clip), 10 / 30.0 - 0.02, 10 / 30.0 + 0.02); // frame 50 is the clip's 11th
    }

    [Fact]
    public async Task Each_part_of_an_edited_clip_starts_and_ends_with_its_frames()
    {
        SkipUnlessInstalled();
        // Parts starting at frames whose times round up at three decimals (2/30 s is 0.0666…, 0.067 cut it off), and
        // from the start of a TS whose sound starts first, without the sound.
        var video = await VideoAsync("30fps.mp4");
        var frames = await FramesAsync(video);
        var edit = new ClipEdit
        {
            Parts =
            [
                new ClipPart { Start = frames[2].Time, End = frames[20].Time },
                new ClipPart { Start = frames[35].Time, End = frames[50].Time },
                new ClipPart { Start = frames[68].Time, End = frames[80].Time },
            ],
        };

        var clip = await CutAsync(video, edit, exact: true);

        List<int> want = [.. frames[2..20].Select(f => f.Number), .. frames[35..50].Select(f => f.Number), .. frames[68..80].Select(f => f.Number)];
        var got = (await FramesAsync(clip)).Select(f => f.Number).ToList();
        Assert.True(want.SequenceEqual(got), $"want {string.Join(",", want)}; got {string.Join(",", got)}");

        var soundFirst = await VideoAsync("soundfirst.ts");
        var all = await FramesAsync(soundFirst);
        var silent = ClipEdit.Of(new ClipRange(0, all[60].Time));
        silent.Parts[0].Volume = 0.9;
        var picture = await CutAsync(soundFirst, silent, exact: true, removeAudio: true);
        // Frame 0 is shown 0.02 s in, nearest the second picture: the first shows it too.
        Assert.Equal([0, .. Enumerable.Range(0, 60)], (await FramesAsync(picture)).Select(f => f.Number));
    }

    /// <summary>When the beep starts in <paramref name="path"/>'s sound, in seconds.</summary>
    /// <summary>
    /// The beep's time after the first frame shown, both in the file's own timestamps as a player has them: a copy's
    /// sound may start before its first frame.
    /// </summary>
    private async Task<double> BeepAfterFirstFrameAsync(string path)
    {
        var result = await ProcessRunner.RunAsync(Tools!.Value.Ffmpeg,
            ["-hide_banner", "-nostdin", "-v", "info", "-copyts", "-i", path, "-map", "0:a:0", "-af", "silencedetect=n=-30dB:d=0.01", "-f", "null", "-"],
            TimeSpan.FromMinutes(1), Ct);
        var line = result.StandardError.Split('\n').First(l => l.Contains("silence_end:", StringComparison.Ordinal));
        var beep = double.Parse(line[(line.IndexOf("silence_end:", StringComparison.Ordinal) + 12)..].Trim().Split(' ')[0], CultureInfo.InvariantCulture);
        var probed = await ProcessRunner.RunAsync(Tools!.Value.Ffprobe,
            ["-v", "error", "-select_streams", "V:0", "-show_entries", "frame=best_effort_timestamp_time", "-of", "csv=p=0", path], TimeSpan.FromMinutes(1), Ct);
        return beep - probed.StandardOutput.Split('\n').Select(l => l.Trim().TrimEnd(','))
            .Where(t => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            .Min(t => double.Parse(t, CultureInfo.InvariantCulture));
    }

    private async Task<double> BeepAsync(string path)
    {
        var result = await ProcessRunner.RunAsync(Tools!.Value.Ffmpeg,
            ["-hide_banner", "-nostdin", "-v", "info", "-i", path, "-map", "0:a:0", "-af", "silencedetect=n=-30dB:d=0.01", "-f", "null", "-"],
            TimeSpan.FromMinutes(1), Ct);
        Assert.True(result.ExitCode == 0, result.StandardError);
        var line = result.StandardError.Split('\n').FirstOrDefault(l => l.Contains("silence_end:", StringComparison.Ordinal));
        Assert.True(line is not null, $"No beep found in {path}: {result.StandardError}");
        var text = line[(line.IndexOf("silence_end:", StringComparison.Ordinal) + 12)..].Trim().Split(' ')[0];
        return double.Parse(text, CultureInfo.InvariantCulture);
    }

    [Theory]
    // A TS: from the key frame found; a seek to the start itself landed between key frames, and the decoder skipped on.
    [InlineData("30fps.ts", "ts", 60, 90)]
    // B-frames decoded 0.4 s before they are shown, far longer than FFmpeg's 3/23 s margin.
    [InlineData("5fps.ts", "ts", 25, 35)]
    // MP4 and MKV with B-frames: from the key frame FFmpeg's own index lands on, not a group earlier.
    [InlineData("30fps.mp4", "mp4", 60, 85)]
    [InlineData("bframes.mkv", "mkv", 80, 88)]
    // At and just after a key frame: FFmpeg seeks 3/23 s early here, which landed a whole group of pictures early.
    [InlineData("bframes.mkv", "mkv", 64, 80)]
    [InlineData("bframes.mkv", "mkv", 66, 80)]
    [InlineData("wrap.ts", "ts", 50, 80)]
    // An MP4 seeks the last key frame decoded by the time: before an open group's key frame, or in a fragmented file,
    // the start itself landed on the next one, a group late.
    [InlineData("opengop.mp4", "mp4", 44, 70)]
    [InlineData("frag.mp4", "mp4", 44, 70)]
    // A WMV's index: the start landed on the key frame at or before its whole second, a group early.
    [InlineData("30fps.wmv", "wmv", 50, 80)]
    // Key frames without a display time: passed over, a group early, where the one found is shown before the start.
    [InlineData("bframes.avi", "avi", 40, 70)]
    [InlineData("mpeg1.mpg", "mpg", 22, 40)]
    // Ends after which FFmpeg's cut by decode time left a gap: an AVI's frames without a time to be shown at, and a
    // program stream whose times FFmpeg makes up after the copy's seek.
    [InlineData("bframes.avi", "avi", 33, 63)]
    [InlineData("mpeg1.mpg", "mpg", 22, 52)]
    public async Task A_copied_clip_starts_at_the_key_frame_shown_last_before_the_start(string name, string target, int start, int end)
    {
        SkipUnlessInstalled();
        var video = await VideoAsync(name, name == "5fps.ts" ? 10 : 4);
        var frames = await FramesAsync(video);
        var key = frames[..(start + 1)].Last(f => f.Key).Number;

        var clip = await CutAsync(video, ClipEdit.Of(new ClipRange(frames[start].Time, frames[end].Time)), exact: false, target: target);

        // Every frame from the key frame on, none of them broken or skipped, to the end or the few frames past it that
        // the frames before need (FFmpeg cut by decode time: 69, 71, 72).
        var numbers = (await FramesAsync(clip)).Select(f => f.Number).ToList();
        Assert.Equal(Enumerable.Range(key, numbers.Count), numbers);
        Assert.InRange(numbers[^1], end - 1, end + 5);
    }

    [Fact]
    public async Task A_copy_of_an_avi_with_b_frames_keeps_its_packets_to_just_before_a_key_or_p_frame()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in ffprobe is a Unix script.");
        // An AVI gives its B-frames a time to be shown at, their decode time, and its key and P-frames none. FFmpeg's
        // end stops only at a frame that has one: it kept the P-frame decoded at 2.2333 s, shown at 2.3333 s, and left
        // out the B-frames shown before it (on the battery's AVI: 63, 66). The copy keeps the packets from its key
        // frame (1.0333 s) to just before the second P-frame from the end (2.1333 s) on, 36 of them, and its sound
        // to there.
        List<string> packets = [];
        for (var k = 31; k <= 75; k++)
        {
            var t = (k / 30.0).ToString("0.000000", CultureInfo.InvariantCulture);
            packets.Add(k % 3 == 1 ? $$"""{"dts_time":"{{t}}","flags":"{{(k == 31 ? "K__" : "___")}}"}""" : $$"""{"pts_time":"{{t}}","dts_time":"{{t}}","flags":"___"}""");
        }
        var ffprobe = StandIn("ffprobe", $$$"""
            #!/bin/sh
            case "$*" in
            *-read_intervals*) echo '{"packets":[{{{string.Join(",", packets)}}}]}';;
            *) echo '{"streams":[{"codec_type":"video","has_b_frames":1,"time_base":"1/30"}],"format":{"format_name":"avi","start_time":"0.000000"}}';;
            esac

            """);
        var seek = new SeekPoint(1.0333 + KeyFrames.DtsHeuristic, 0, 1.2638, 1.1333, (1, 30), Key: 31 / 30.0, KeyShown: 34 / 30.0);

        var end = await KeyFrames.CopyEndAsync(ffprobe, Path.Combine(fx.NewFolder(), "b.avi"), seek, 64 / 30.0, Ct);

        Assert.Equal(67 / 30.0, end.CopyTo!.Value, 6);
        Assert.Equal(36, end.CopyFrames);
        var args = MediaEncoding.ClipCopy("b.avi", "out.avi", "avi", new ClipRange(1.1333, 64 / 30.0), end, new MediaOptions(),
            new MediaInfo(TimeSpan.FromSeconds(4), new VideoStreamInfo(128, 32, 1, 0, 30, "mpeg4", "yuv420p"), new AudioStreamInfo(1, 48000, "pcm_s16le"), null));
        Assert.Equal("36", args[args.IndexOf("-frames:v") + 1]);
        Assert.Equal((67 / 30.0 - 0.0001).ToString("0.000000", CultureInfo.InvariantCulture), args[args.IndexOf("-to") + 1]);
    }

    [Fact]
    public async Task A_copy_whose_key_frame_no_seek_before_the_start_lands_on_ends_at_its_end()
    {
        SkipUnlessInstalled();
        // A WMV's index lands a seek on its second key frame only from the next whole second on, past this short clip's
        // end: the copy read to there. Read from before it and kept from the key frame on, it ends at its own end.
        var video = await VideoAsync("sound.wmv", 4);
        var frames = await FramesAsync(video);
        var key = frames.FindIndex(1, f => f.Key);
        var clip = await CutAsync(video, ClipEdit.Of(new ClipRange(frames[key].Time, frames[key + 25].Time)), exact: false, target: "wmv");

        var numbers = (await FramesAsync(clip)).Select(f => f.Number).ToList();

        Assert.Equal(Enumerable.Range(frames[key].Number, numbers.Count), numbers);
        Assert.InRange(numbers.Count, 24, 28);
        // Its sound where it is against the key frame in the file.
        var beep = await BeepAfterFirstFrameAsync(video) - (frames[key].Time - frames[0].Time);
        Assert.InRange(await BeepAfterFirstFrameAsync(clip), beep - 0.03, beep + 0.03);
    }

    [Fact]
    public async Task A_copy_whose_key_frame_no_seek_lands_on_keeps_it_where_it_is_decoded_before_it_is_shown()
    {
        SkipUnlessInstalled();
        // An MKV whose index names only its first and last key frames: every -ss between lands on frame 0 (with one
        // name alone, FFmpeg reads the file to find the others), so the copy reads from there
        // and keeps from the key frame on. With B-frames that key frame is decoded before it is shown, and kept from
        // the time shown, it was dropped and the copy began at the next one, or had no pictures at all.
        var video = await NumberedAsync("sparse.mkv", "30", 4,
            ["-c:v", "libx264", "-g", "30", "-bf", "2", "-x264-params", "scenecut=0", "-pix_fmt", "yuv420p"]);
        VoidCuesBetweenFirstAndLast(video);
        var frames = await FramesAsync(video);
        var key = frames.FindIndex(1, f => f.Key);

        var numbers = (await FramesAsync(await CutAsync(video, ClipEdit.Of(new ClipRange(frames[key].Time, frames[key + 20].Time)),
            exact: false, target: "mkv"))).Select(f => f.Number).ToList();

        Assert.Equal(frames[key].Number, numbers[0]);
        Assert.Equal(Enumerable.Range(frames[key].Number, numbers.Count), numbers);
        Assert.InRange(numbers.Count, 20, 24);
    }

    /// <summary>
    /// Leaves only the first and last CuePoints of a Matroska file's last Cues element, the others turned into Void
    /// elements of the same size, so a seek between them finds no key frame after the first.
    /// </summary>
    private static void VoidCuesBetweenFirstAndLast(string path)
    {
        var bytes = File.ReadAllBytes(path);
        (long Value, int Length) Vint(int at)
        {
            var length = 1;
            for (var mask = 0x80; (bytes[at] & mask) == 0; mask >>= 1)
                length++;
            long value = bytes[at] & ((0x80 >> (length - 1)) - 1);
            for (var i = 1; i < length; i++)
                value = (value << 8) | bytes[at + i];
            return (value, length);
        }
        var cues = bytes.AsSpan().LastIndexOf((ReadOnlySpan<byte>)[0x1C, 0x53, 0xBB, 0x6B]);
        Assert.True(cues > 0, "No Cues element.");
        var (size, sizeLength) = Vint(cues + 4);
        var points = new List<int>();
        for (var at = cues + 4 + sizeLength; at < cues + 4 + sizeLength + size;)
        {
            var (length, lengthSize) = Vint(at + 1);
            if (bytes[at] == 0xBB)
                points.Add(at);
            at += 1 + lengthSize + (int)length;
        }
        Assert.True(points.Count > 2, "Too few CuePoints.");
        foreach (var at in points[1..^1])
            bytes[at] = 0xEC;
        File.WriteAllBytes(path, bytes);
    }

    [Fact]
    public async Task Times_between_frames_of_an_avi_start_and_end_clips_with_the_frames_shown_then()
    {
        SkipUnlessInstalled();
        // An AVI counts in whole frames: a time a third of the way past frame 40 was rounded back to it, in the parts
        // graph and in an end counted by FFmpeg's own -to.
        var video = await VideoAsync("mjpeg.avi");
        var frames = await FramesAsync(video);
        double Past(int frame) => frames[frame].Time + (frames[frame + 1].Time - frames[frame].Time) * 0.3;
        var range = new ClipRange(Past(40), Past(70));

        Assert.Equal(Enumerable.Range(41, 30), (await FramesAsync(await CutAsync(video, ClipEdit.Of(range), exact: true))).Select(f => f.Number));
        var edit = ClipEdit.Of(range);
        edit.Parts[0].Volume = 0.9;
        // Edited, each frame at the picture nearest its time from the start: frame 41, 0.7 of a frame in, at the
        // second, the first showing it too; 30 pictures, as long as the part.
        Assert.Equal([41, .. Enumerable.Range(41, 29)], (await FramesAsync(await CutAsync(video, edit, exact: true))).Select(f => f.Number));
        Assert.Equal(41, await FirstPlayedAsync(video, ClipEdit.Of(range)));
    }

    [Theory]
    [InlineData("mjpeg.avi", 1.0)]
    [InlineData("mjpeg.avi", 2.0)]
    [InlineData("mjpeg.avi", 0.5)]
    [InlineData("30fps.mp4", 2.0)]
    [InlineData("30fps.mp4", 0.5)]
    [InlineData("soundfirst.ts", 2.0)]
    [InlineData("30fps.mp4", 3.0)]
    [InlineData("30fps.mp4", 4.0)]
    [InlineData("mjpeg.avi", 1.5)]
    [InlineData("jitter.mp4", 1.0)]
    [InlineData("jitter.mp4", 2.0)]
    [InlineData("wrap.ts", 1.0)]
    public async Task An_edited_clip_starts_with_the_frame_shown_at_or_just_before_a_frames_time(string name, double speed)
    {
        SkipUnlessInstalled();
        // An AVI counts in whole frames: the move of the start by a microsecond's fraction, cut short to a whole tick,
        // put the first two frames on the same tick, and only the later one was kept. Frames a little off their rate
        // keep their pictures too: with the first frame put most of the way into its picture, a frame 4 ms late took
        // the next picture, and its neighbour's was shown twice.
        var video = await VideoAsync(name, 4);
        var frames = await FramesAsync(video);
        List<string> wrong = [];
        for (var i = 40; i < 46; i++)
        {
            foreach (var before in new[] { 0.0, 0.4 })
            {
                var start = frames[i].Time - (frames[i].Time - frames[i - 1].Time) * before;
                var edit = ClipEdit.Of(new ClipRange(start, frames[i + 30].Time));
                edit.Parts[0].Volume = 0.9;
                edit.Parts[0].Speed = speed;
                var numbers = (await FramesAsync(await CutAsync(video, edit, exact: true))).Select(f => f.Number).ToList();
                // Played at half speed, each frame twice; faster, every second, third or fourth frame; at 1.5 times,
                // only the first frame is sure (the others are those nearest their times). From 0.4 of a frame before
                // it at half speed, its picture is the second, and the first shows it too, as the window does; an AVI
                // counts in whole frames, so its move to the part's start rounds that 0.4 away.
                var expected = speed == 0.5
                    ? [.. before > 0 && !name.EndsWith(".avi", StringComparison.Ordinal) ? [i] : Array.Empty<int>(), .. Enumerable.Range(0, 60).Select(k => i + k / 2)]
                    : speed == Math.Floor(speed) ? Enumerable.Range(0, (int)Math.Ceiling(30 / speed)).Select(k => i + k * (int)speed).ToList()
                    : [i];
                var same = Math.Min(numbers.Count, expected.Count) - (speed > 2 ? 1 : 0);
                if (numbers.Count == 0 || !numbers.Take(same).SequenceEqual(expected.Take(same))
                    || (speed == Math.Floor(speed) || speed == 0.5) && Math.Abs(numbers.Count - expected.Count) > (speed > 2 ? 1 : 0))
                    wrong.Add($"{start:0.000000} s: {string.Join(",", numbers.Take(6))}… ({numbers.Count}) instead of {string.Join(",", expected.Take(6))}… ({expected.Count})");
            }
        }
        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
    }

    [Fact]
    public async Task An_exact_clip_from_between_a_frames_grid_time_and_its_own_starts_with_it()
    {
        SkipUnlessInstalled();
        // Frame 30 is shown at 1.010026 s, after 1.0 s where the encoder's 1/30 s grid puts it: from 1.005 s, the
        // encoder rounded it to before the clip's start and the MP4 left it out.
        var video = await VideoAsync("offgrid.mp4");
        var frames = await FramesAsync(video);

        var clip = await CutAsync(video, ClipEdit.Of(new ClipRange(frames[30].Time - 0.005, frames[60].Time)), exact: true);

        Assert.Equal(Enumerable.Range(30, 30), (await FramesAsync(clip)).Select(f => f.Number));
    }

    [Fact]
    public async Task A_clip_keeps_the_chapters_where_they_are_in_it()
    {
        SkipUnlessInstalled();
        var video = await VideoAsync("30fps.mkv", 4);
        var chapters = Path.Combine(fx.NewFolder(), "chapters.txt");
        await File.WriteAllTextAsync(chapters, ";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=2000\ntitle=A\n"
            + "[CHAPTER]\nTIMEBASE=1/1000\nSTART=2000\nEND=3000\ntitle=B\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=3000\nEND=4000\ntitle=C\n", Ct);
        var chaptered = Path.Combine(fx.NewFolder(), "chapters.mkv");
        var made = await ProcessRunner.RunAsync(Tools!.Value.Ffmpeg,
            [.. MediaEncoding.Common, "-i", video, "-i", chapters, "-map", "0", "-map_chapters", "1", "-c", "copy", chaptered], TimeSpan.FromMinutes(1), Ct);
        Assert.True(made.ExitCode == 0, made.StandardError);

        var clip = await CutAsync(chaptered, ClipEdit.Of(new ClipRange(2, 3.5)), exact: true);

        // B starts at the clip's start and C 1 s in, as in the video; with the file's own times kept, they stayed at 2
        // and 3 s.
        var probed = await ProcessRunner.RunAsync(Tools!.Value.Ffprobe,
            ["-v", "error", "-show_entries", "chapter=start_time:chapter_tags=title", "-of", "csv=p=0", clip], TimeSpan.FromMinutes(1), Ct);
        var starts = probed.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(',')).Where(f => f.Length == 2).ToDictionary(f => f[1], f => double.Parse(f[0], CultureInfo.InvariantCulture));
        Assert.Equal(0, starts["B"], 2);
        Assert.Equal(1, starts["C"], 2);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(0.5)]
    public async Task Sound_that_starts_after_the_picture_keeps_its_place_in_an_edited_clip(double speed)
    {
        SkipUnlessInstalled();
        // The sound starts 1 s into the video and beeps at 1.5 s; from 0.5 s, the beep is 1 s into the clip, played
        // at that speed. Each part's sound counted from its own first sample beeped at 0.5 s; faster or slower, the
        // silence before it was left as long as it is in the video (atempo itself plays the beep up to 40 ms early).
        var video = await SoundOneSecondInAsync();
        var edit = ClipEdit.Of(new ClipRange(0.5, 2.8));
        edit.Parts[0].Volume = 0.9;
        edit.Parts[0].Speed = speed;

        Assert.InRange(await BeepAsync(await CutAsync(video, edit, exact: true)), 1 / speed - 0.06, 1 / speed + 0.04);
        if (speed == 1)
            Assert.InRange(await BeepAsync(await CutAsync(video, ClipEdit.Of(new ClipRange(0.5, 2.8)), exact: true)), 0.98, 1.04);
    }

    /// <summary>A 3 s MP4, 30 frames a second, whose sound starts 1 s in and beeps at 1.5 s.</summary>
    private async Task<string> SoundOneSecondInAsync()
    {
        var picture = await VideoAsync("30fps.mp4");
        var sound = Path.Combine(fx.NewFolder(), "beep.m4a");
        var beep = await ProcessRunner.RunAsync(Tools!.Value.Ffmpeg,
            [.. MediaEncoding.Common, "-f", "lavfi", "-i", "aevalsrc='if(between(t\\,0.5\\,0.55)\\,0.8*sin(2*PI*1000*t)\\,0)':s=48000:d=2",
                "-c:a", "aac", sound], TimeSpan.FromMinutes(1), Ct);
        Assert.True(beep.ExitCode == 0, beep.StandardError);
        var video = Path.Combine(fx.NewFolder(), "late.mp4");
        var made = await ProcessRunner.RunAsync(Tools!.Value.Ffmpeg,
            [.. MediaEncoding.Common, "-i", picture, "-itsoffset", "1", "-i", sound, "-map", "0:v", "-map", "1:a", "-c", "copy", video],
            TimeSpan.FromMinutes(1), Ct);
        Assert.True(made.ExitCode == 0, made.StandardError);
        return video;
    }

    [Fact]
    public async Task The_last_frame_of_an_mpeg_ts_file_is_there_at_the_windows_last_time()
    {
        SkipUnlessInstalled();
        // The window's last time is a frame before the file's end, which in an MPEG-TS reads a microsecond after it.
        var video = await VideoAsync("30fps.ts");
        var frames = await FramesAsync(video);
        var summary = await VideoFrames.ProbeAsync(video, Ct);
        var output = Path.Combine(fx.NewFolder(), "last.jpg");

        Assert.True(await VideoFrames.WriteFrameAsync(video, summary.LastFrameTime, output, Height, Ct));
        Assert.Equal(frames[^1].Number, await NumberOfPictureAsync(output));
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("edited")]
    [InlineData("live photo")]
    public async Task A_clips_progress_reaches_its_length(string kind)
    {
        SkipUnlessInstalled();
        // With -copyts, FFmpeg's -progress counted from its first report: a clip ended its progress well short of 1,
        // and mixed with each picture's time it went back and forth.
        if (await FfmpegVersionAsync() is { Major: < 6 } old)
            Assert.Skip($"FFmpeg {old} has no picture times, and this clip is over before its second -progress report");
        var video = await VideoAsync("30fps.ts", 4);
        var info = await MediaInfo.ProbeAsync(Tools!.Value.Ffprobe, video, Ct);
        var range = new ClipRange(0.7, 3.7);
        var seek = await KeyFrames.FindAsync(Tools!.Value.Ffprobe, video, range.Start, Ct);
        var output = Path.Combine(fx.NewFolder(), kind == "live photo" ? "clip.mov" : "clip.mp4");
        var edit = ClipEdit.Of(range);
        edit.Parts[0].Volume = 0.9;
        var args = kind switch
        {
            "exact" => MediaEncoding.Plan(video, output, "mp4", new MediaOptions(), info, fx.NewFolder(), clip: range, clipSeek: seek).Single().Arguments,
            "edited" => MediaEncoding.ClipEditArguments(video, output, "mp4", edit.Clamped(info.Duration?.TotalSeconds), new MediaOptions(), info, seek: seek),
            _ => LivePhotoWriter.ClipArguments(video, output, "mov", new LivePhotoClip(range.Start, range.Length, 0), info.Video!, info.Audio,
                new MediaOptions(), "libx264", "", seek),
        };
        var reached = 0.0;
        List<double> back = [];

        var result = await FfmpegRunner.RunAsync(Tools!.Value.Ffmpeg, args, seconds =>
        {
            if (seconds < reached - 0.05)
                back.Add(seconds);
            reached = Math.Max(reached, seconds);
        }, Ct);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.InRange(reached, range.Length - 0.1, range.Length + 0.05);
        Assert.Empty(back);
    }

    [Theory]
    [InlineData("soundfirst.ts")]
    [InlineData("dvd.vob")]
    [InlineData("wrap.ts")]
    public async Task Playback_in_the_window_starts_with_the_frame_it_showed(string name)
    {
        SkipUnlessInstalled();
        var video = await VideoAsync(name, 4);
        var frames = await FramesAsync(video);
        var edit = ClipEdit.Of(new ClipRange(frames[40].Time, frames[70].Time));

        Assert.Equal(40, await FirstPlayedAsync(video, edit));
    }

    [Theory]
    [InlineData("30fps.mp4", 1)]
    [InlineData("soundfirst.ts", 1)]
    [InlineData("2997fps.mp4", 1)]
    [InlineData("60fps.mp4", 1)]
    [InlineData("30fps.mp4", 2)]
    [InlineData("mjpeg.avi", 1)]
    [InlineData("mjpeg.avi", 2)]
    [InlineData("120fps.mp4", 4)]
    public async Task Playback_from_between_two_frames_starts_with_the_later_one_as_the_window_shows(string name, double speed)
    {
        SkipUnlessInstalled();
        // The window shows the first frame at or after a time; playback, 25 pictures a second, starts with it too, also
        // where frames come faster than that.
        var video = await VideoAsync(name, 4);
        var frames = await FramesAsync(video);
        List<string> wrong = [];
        for (var i = 40; i < 46; i++)
        {
            foreach (var part in new[] { 0.0, 0.3, 0.6, 0.75, 0.9 })
            {
                var start = frames[i].Time + (frames[i + 1].Time - frames[i].Time) * part;
                var shown = part == 0 ? frames[i].Number : frames[i + 1].Number;
                var edit = ClipEdit.Of(new ClipRange(start, frames[i + 30].Time));
                edit.Parts[0].Speed = speed;
                if (await FirstPlayedAsync(video, edit) is var first && first != shown)
                    wrong.Add($"{start:0.000000} s: frame {first} instead of {shown}");
            }
        }
        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_preview_plays_the_sound_heard_with_its_pictures(bool soundLater)
    {
        SkipUnlessInstalled();
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in player is a Unix script.");
        // Each TS beeps in frame 50: played from frame 40, the beep is heard 10 frames in, where the pictures show it.
        // The sound starts before the pictures, or 0.3 s after them: read for itself from its own -ss, on Ubuntu's
        // FFmpeg 6.1 the beep of the second came two frames early (FFmpeg 8.0 and later get it right either way).
        var video = soundLater ? await SoundLaterAsync() : await VideoAsync("soundfirst.ts", 4);
        var frames = await FramesAsync(video);
        // The player writes down what it is given: a real one needs a sound device, which a CI runner hasn't got.
        var player = StandIn("player", "#!/bin/sh\ncat > \"$(dirname \"$0\")/played.wav\"\n");
        ClipPreview.PlayerProgram = player;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            limit.CancelAfter(TimeSpan.FromSeconds(30));
            await ClipPreview.PlayAsync(video, ClipEdit.Of(new ClipRange(frames[40].Time, frames[70].Time)), Height, (_, _, _, _) => { },
                limit.Token);
        }
        finally
        {
            ClipPreview.PlayerProgram = null;
        }
        var played = Path.Combine(Path.GetDirectoryName(player)!, "played.wav");
        Assert.InRange(await BeepAsync(played), 10 / 30.0 - 0.02, 10 / 30.0 + 0.02);
    }

    /// <summary>
    /// A 4 s TS, 30 frames a second, whose sound starts 0.3 s after its pictures and beeps in frame 50.
    /// </summary>
    private async Task<string> SoundLaterAsync()
    {
        var pictures = await VideoAsync("30fps.ts", 4);
        var video = Path.Combine(fx.NewFolder(), "soundlater.ts");
        var made = await ProcessRunner.RunAsync(Tools!.Value.Ffmpeg,
            [.. MediaEncoding.Common, "-i", pictures, "-itsoffset", "0.3", "-f", "lavfi", "-i",
                "aevalsrc='if(between(t\\,50/30-0.3\\,51/30-0.3)\\,0.8*sin(2*PI*1000*t)\\,0)':s=48000:d=3.7",
                "-map", "0:v", "-map", "1:a", "-c:v", "copy", "-c:a", "aac", "-f", "mpegts", video], TimeSpan.FromMinutes(1), Ct);
        Assert.True(made.ExitCode == 0, made.StandardError);
        return video;
    }

    /// <summary>
    /// The number of the first picture the window plays of <paramref name="edit"/>, its sound silent.
    /// </summary>
    private async Task<int?> FirstPlayedAsync(string video, ClipEdit edit)
    {
        int? first = null;
        var volume = ClipPreview.PlayerVolume;
        ClipPreview.PlayerVolume = 0;
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            await ClipPreview.PlayAsync(video, edit, Height, (pixels, width, height, _) =>
            {
                first ??= Number(pixels, 0, width, height, bytesPerPixel: 4);
                stop.Cancel();
            }, stop.Token);
        }
        finally
        {
            ClipPreview.PlayerVolume = volume;
        }
        return first;
    }

    [Theory]
    [InlineData("60fps.mp4", 20, 1)]
    [InlineData("60fps.mp4", 23, 2)]
    [InlineData("60fps.mp4", 23, 20)]
    // Frames coming faster than the movie's 30 a second: fps kept the frame after the first, which followed within
    // half a picture (one to three frames late).
    [InlineData("120fps.mp4", 41, 10)]
    [InlineData("240fps.mp4", 41, 10)]
    public async Task A_live_photo_shows_the_movies_frame_at_the_still_time(string name, int start, int cover)
    {
        SkipUnlessInstalled();
        // The movie and its photo pick frames by 30 fps timing from the same time 0: counted from the file's own times,
        // the photo's timing fell between other frames than the movie's in a video not at 30 fps.
        var video = await VideoAsync(name, 4);
        var frames = await FramesAsync(video);
        var preset = new Preset { TargetFormat = "jpg", Media = { LivePhoto = LivePhotoKind.Apple } };
        preset.Media.LivePhotoStart = frames[start].Time;
        preset.Media.LivePhotoLength = 1.5;
        preset.Media.LivePhotoCover = cover / 30.0;

        var job = await fx.ConvertAsync([video], preset);

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var photo = Assert.Single(job.Outputs, p => p.EndsWith(".jpg", StringComparison.Ordinal));
        var movie = Assert.Single(job.Outputs, p => p.EndsWith(".mov", StringComparison.Ordinal));
        var shown = await FramesAsync(movie);
        Assert.Equal(frames[start].Number, shown[0].Number);
        Assert.Equal(shown[cover].Number, await NumberOfPictureAsync(photo));
    }

    [Fact]
    public async Task A_part_played_twice_as_fast_shows_every_second_frame()
    {
        SkipUnlessInstalled();
        // Microsecond times before the speed change made the steps uneven: 21, 22, 25, 27, 28, 31…
        var video = await VideoAsync("30fps.mp4");
        var frames = await FramesAsync(video);
        var edit = ClipEdit.Of(new ClipRange(frames[20].Time, frames[60].Time));
        edit.Parts[0].Speed = 2;

        var numbers = (await FramesAsync(await CutAsync(video, edit, exact: true))).Select(f => f.Number).ToList();

        Assert.InRange(numbers.Count, 19, 21);
        Assert.All(numbers.Zip(numbers.Skip(1)), pair => Assert.Equal(2, pair.Second - pair.First));
    }

    [Fact]
    public async Task A_part_played_slower_ends_at_its_length_and_the_next_part_starts_on_time()
    {
        SkipUnlessInstalled();
        // A quarter of a second from frame 40 at a quarter of the speed is a second, 30 pictures: frames 40 to 47, each
        // four times, the last twice. fps held frame 47 for all of its slowed time, two pictures past the part's end
        // and its sound, and the next part started two pictures late.
        var video = await VideoAsync("30fps.mp4");
        var frames = await FramesAsync(video);
        var edit = new ClipEdit
        {
            Parts =
            [
                new ClipPart { Start = frames[40].Time, End = frames[40].Time + 0.25, Speed = 0.25 },
                new ClipPart { Start = frames[60].Time, End = frames[70].Time },
            ],
        };

        var numbers = (await FramesAsync(await CutAsync(video, edit, exact: true))).Select(f => f.Number).ToList();

        Assert.Equal([.. Enumerable.Range(0, 30).Select(k => 40 + k / 4), .. Enumerable.Range(60, 10)], numbers);
        // From 0.4 of a frame before frame 40: 1.6 pictures before it, which show it, as the window does for that time
        // (fps left them out, and the part was two pictures short of its sound).
        edit.Parts[0].Start = frames[40].Time - (frames[40].Time - frames[39].Time) * 0.4;
        edit.Parts[0].End = frames[40].Time + 0.25;
        var between = (await FramesAsync(await CutAsync(video, edit, exact: true))).Select(f => f.Number).ToList();
        Assert.Equal([40, 40, .. Enumerable.Range(0, 30).Select(k => 40 + k / 4), .. Enumerable.Range(60, 10)], between);
    }

    [Theory]
    [InlineData("soundfirst.ts")]
    [InlineData("dvd.vob")]
    [InlineData("wrap.ts")]
    public async Task A_live_photo_of_a_file_without_an_index_shows_the_frame_its_times_point_at(string name)
    {
        SkipUnlessInstalled();
        var video = await VideoAsync(name, 4);
        var frames = await FramesAsync(video);
        var preset = new Preset { TargetFormat = "jpg", Media = { LivePhoto = LivePhotoKind.Apple } };
        preset.Media.LivePhotoStart = frames[40].Time;
        preset.Media.LivePhotoLength = 1;
        preset.Media.LivePhotoCover = 10 / 30.0;

        var job = await fx.ConvertAsync([video], preset);

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var photo = Assert.Single(job.Outputs, p => p.EndsWith(".jpg", StringComparison.Ordinal));
        var movie = Assert.Single(job.Outputs, p => p.EndsWith(".mov", StringComparison.Ordinal));
        Assert.Equal(50, await NumberOfPictureAsync(photo));
        Assert.Equal(Enumerable.Range(40, 30), (await FramesAsync(movie)).Select(f => f.Number));
        // The TS beeps in frame 50, the movie's 11th: its sound is cut where its picture is.
        if (name == "soundfirst.ts")
            Assert.InRange(await BeepAsync(movie), 10 / 30.0 - 0.02, 10 / 30.0 + 0.02);
    }

    [Fact]
    public async Task A_copy_allows_for_the_early_seek_of_b_frames_in_any_video_stream()
    {
        SkipUnlessInstalled();
        // The first video has no B-frames and the second has: FFmpeg seeks 3/23 s early for both, so a copy from just
        // after the key frame at 2 s landed on the one at 1 s when only the first video counted.
        var plain = await NumberedAsync("plain.mp4", "30", 4, ["-c:v", "libx264", "-g", "30", "-bf", "0", "-x264-params", "scenecut=0",
            "-pix_fmt", "yuv420p"]);
        var reordered = await NumberedAsync("reordered.mp4", "30", 4, ["-c:v", "libx264", "-g", "30", "-bf", "2", "-x264-params", "scenecut=0",
            "-pix_fmt", "yuv420p"]);
        var video = Path.Combine(fx.NewFolder(), "two.mkv");
        var made = await ProcessRunner.RunAsync(Tools!.Value.Ffmpeg,
            [.. MediaEncoding.Common, "-i", plain, "-i", reordered, "-map", "0:v", "-map", "1:v", "-c", "copy", video], TimeSpan.FromMinutes(1), Ct);
        Assert.True(made.ExitCode == 0, made.StandardError);
        var frames = await FramesAsync(video);

        var clip = await CutAsync(video, ClipEdit.Of(new ClipRange(frames[62].Time, frames[80].Time)), exact: false, target: "mkv");

        var numbers = (await FramesAsync(clip)).Select(f => f.Number).ToList();
        Assert.Equal(Enumerable.Range(60, numbers.Count), numbers);

        // A copy shorter than the 3/23 s: its -ss lay past its end, which FFmpeg refused, and the clip was encoded
        // from frame 61 instead.
        var range = new ClipRange(frames[60].Time + 0.01, frames[60].Time + 0.11);
        var seek = await KeyFrames.FindAsync(Tools!.Value.Ffprobe, video, range.Start, Ct);
        Assert.True(seek.CopyFrom > range.End, $"-ss {seek.CopyFrom} is before the end, so this tests nothing");
        var shortClip = await CutAsync(video, ClipEdit.Of(range), exact: false, target: "mkv");
        numbers = [.. (await FramesAsync(shortClip)).Select(f => f.Number)];
        Assert.Equal(60, numbers[0]);
        Assert.InRange(numbers.Count, 4, 7); // to the 3/23 s, and a frame shown later but decoded before it
    }

    [Fact]
    public async Task A_copy_from_before_a_ts_files_first_key_frame_starts_with_it()
    {
        SkipUnlessInstalled();
        // The first key frame is decoded 0.4 s before it is shown, after the sound's start: a copy from between the
        // sound's start and the key frame, seeked by decode time, landed on a packet after the key frame.
        var (rate, _, codec) = VideoOf("5fps.ts");
        var video = await NumberedAsync("bframes-sound.ts", rate, 4, codec, beepFrame: 10);
        var frames = await FramesAsync(video);
        Assert.True(frames[0].Time > 0.01, $"the pictures start at {frames[0].Time}, with the sound");

        var clip = await CutAsync(video, ClipEdit.Of(new ClipRange(frames[0].Time / 2, frames[15].Time)), exact: false, target: "ts");

        var numbers = (await FramesAsync(clip)).Select(f => f.Number).ToList();
        Assert.Equal(Enumerable.Range(0, numbers.Count), numbers);
    }

    [Theory]
    [InlineData("bframes.nut", "30", 30, "libx264")]
    [InlineData("bframes.mxf", "25", 15, "mpeg2video")]
    public async Task Formats_sought_by_the_time_shown_are_read_from_the_key_frame_itself(string name, string rate, int group, string codec)
    {
        SkipUnlessInstalled();
        // Like MOV, these are sought by the time shown, with no 3/23 s taken off: reading starts at the key frame.
        var video = await NumberedAsync(name, rate, 3, ["-c:v", codec, "-g", group.ToString(CultureInfo.InvariantCulture), "-bf", "2",
            .. codec == "libx264" ? new[] { "-x264-params", "scenecut=0" } : []]);
        var frames = await FramesAsync(video);
        var key = frames[group];
        Assert.True(key.Key, $"frame {group} is no key frame");
        var time = frames[group + group * 4 / 5].Time;

        var seek = await KeyFrames.FindAsync(Tools!.Value.Ffprobe, video, time, Ct);

        Assert.Equal(key.Time, seek.From, 0.0005);
    }

    [Fact]
    public async Task With_no_key_frame_in_the_furthest_look_times_are_still_the_files_own()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in ffprobe is a Unix script.");
        // A TS whose ffprobe lists no key frame within 32 s before the time: FFmpeg seeks to the time itself, still
        // with the file's own timestamps, which keep its sound and pictures together (see the file header).
        // The clip window's next picture is looked for in the furthest look at once.
        var folder = fx.NewFolder();
        var calls = Path.Combine(folder, "calls.log");
        var ffprobe = StandIn("ffprobe", $$$"""
            #!/bin/sh
            echo "$*" >> '{{{calls}}}'
            case "$*" in
            *-read_intervals*) echo '{"packets":[{"pts_time":"30.000000","dts_time":"29.900000","flags":"__"}]}';;
            *) echo '{"streams":[{"codec_type":"video","has_b_frames":0,"time_base":"1/90000"}],"format":{"format_name":"mpegts","start_time":"1.400000"}}';;
            esac

            """);
        var video = Path.Combine(folder, "far.ts");

        var seek = await KeyFrames.FindAsync(ffprobe, video, 40, Ct);
        var lookedFirst = (await File.ReadAllLinesAsync(calls, Ct)).Length;
        var next = await KeyFrames.FindAsync(ffprobe, video, 40.1, Ct);
        var lines = await File.ReadAllLinesAsync(calls, Ct);

        Assert.Equal(SeekPoint.Plain(40) with { Origin = 1.4, Tick = (1, 90000) }, seek);
        Assert.Equal(SeekPoint.Plain(40.1) with { Origin = 1.4, Tick = (1, 90000) }, next);
        Assert.Equal(4, lookedFirst); // the format, then 2, 8 and 32 s of packets
        var at = lines[lookedFirst..].Select(line => line.IndexOf("-read_intervals", StringComparison.Ordinal) is >= 0 and var i ? line.Substring(i, 25) : line);
        Assert.Equal(["-read_intervals 9.500000%"], at);
    }

    [Theory]
    [InlineData("dhav", false)]
    [InlineData("dvdvideo", false)]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", false)]
    [InlineData("mxf", false)]
    [InlineData("nut", false)]
    [InlineData("matroska,webm", true)]
    [InlineData("flv", true)]
    public async Task Formats_sought_by_the_time_shown_have_no_margin_for_b_frames(string format, bool margin)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in ffprobe is a Unix script.");
        // Security cameras' DHAV and DVD-Video read as a disc seek by the time a key frame is shown, as MP4 does
        // (AVFMT_SEEK_TO_PTS): from it. Elsewhere FFmpeg seeks 3/23 s early with B-frames, so -ss is that much later.
        var ffprobe = StandIn("ffprobe", $$$"""
            #!/bin/sh
            case "$*" in
            *-read_intervals*) echo '{"packets":[{"pts_time":"2.000000","dts_time":"1.900000","flags":"K__"}]}';;
            *) echo '{"streams":[{"codec_type":"video","has_b_frames":1,"time_base":"1/90000"}],"format":{"format_name":"{{{format}}}","start_time":"0.000000"}}';;
            esac

            """);

        var seek = await KeyFrames.FindAsync(ffprobe, Path.Combine(fx.NewFolder(), "video"), 2.5, Ct);

        Assert.Equal(margin ? 2 + KeyFrames.DtsHeuristic : 2, seek.From, 6);
    }

    [Fact]
    public async Task The_formats_of_more_than_64_files_are_forgotten_and_looked_at_again()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in ffprobe is a Unix script.");
        // A queue of conversions goes through many files once each: what is kept is let go past 64 of them.
        var folder = fx.NewFolder();
        var calls = Path.Combine(folder, "calls.log");
        var ffprobe = StandIn("ffprobe", $$$"""
            #!/bin/sh
            case "$*" in
            *-read_intervals*) echo '{"packets":[{"pts_time":"1.000000","dts_time":"1.000000","flags":"K__"}]}';;
            *) echo format >> '{{{calls}}}'; echo '{"streams":[{"codec_type":"video","has_b_frames":0,"time_base":"1/90000"}],"format":{"format_name":"mpegts","start_time":"0.000000"}}';;
            esac

            """);
        var first = Path.Combine(folder, "first.ts");
        await KeyFrames.FindAsync(ffprobe, first, 1.5, Ct);
        for (var i = 0; i < 70; i++)
            await KeyFrames.FindAsync(ffprobe, Path.Combine(folder, $"{i}.ts"), 1.5, Ct);
        var before = (await File.ReadAllLinesAsync(calls, Ct)).Length;

        await KeyFrames.FindAsync(ffprobe, first, 1.5, Ct);

        Assert.Equal(before + 1, (await File.ReadAllLinesAsync(calls, Ct)).Length);
    }

    [Fact]
    public async Task A_cover_picture_does_not_set_the_videos_ticks()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in ffprobe is a Unix script.");
        // A cover listed before the video (FFmpeg itself writes it after): cut in the cover's ticks of 1/90000 s, a
        // clip from the video's of 1/15360 s would start nearly six times too late.
        var ffprobe = StandIn("ffprobe", """
            #!/bin/sh
            case "$*" in
            *-read_intervals*) echo '{"packets":[{"pts_time":"1.000000","dts_time":"1.000000","flags":"K__"}]}';;
            *) echo '{"streams":[{"codec_type":"video","has_b_frames":0,"time_base":"1/90000","disposition":{"attached_pic":1}},{"codec_type":"video","has_b_frames":0,"time_base":"1/15360","disposition":{"attached_pic":0}}],"format":{"format_name":"mov,mp4,m4a,3gp,3g2,mj2","start_time":"0.000000"}}';;
            esac

            """);

        var seek = await KeyFrames.FindAsync(ffprobe, Path.Combine(fx.NewFolder(), "covered.mp4"), 1.5, Ct);

        Assert.Equal((1L, 15360L), seek.Tick);
    }

    [Fact]
    public async Task A_file_looked_at_again_is_read_from_its_last_reach_with_its_format_kept()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in ffprobe is a Unix script.");
        // A TS whose only key frame is at 30 s (31.4 s in its own time): the first look at 40 s reads 2, 8 and then 32
        // s of packets before it finds it. The clip window looks at the next frame next, from 32 s before it at once,
        // and asks for the file's format only the first time.
        var folder = fx.NewFolder();
        var calls = Path.Combine(folder, "calls.log");
        var ffprobe = StandIn("ffprobe", $$$"""
            #!/bin/sh
            echo "$*" >> '{{{calls}}}'
            case "$*" in
            *-read_intervals*)
                from=$(echo "$*" | sed -n 's/.*-read_intervals \([0-9.]*\)%.*/\1/p')
                if awk "BEGIN { exit !(\"$from\" + 0 <= 31.4) }"; then
                    echo '{"packets":[{"pts_time":"31.400000","dts_time":"31.400000","flags":"K__"}]}'
                else
                    echo '{"packets":[{"pts_time":"38.000000","dts_time":"38.000000","flags":"__"}]}'
                fi;;
            *) echo '{"streams":[{"codec_type":"video","has_b_frames":0,"time_base":"1/90000"}],"format":{"format_name":"mpegts","start_time":"1.400000"}}';;
            esac

            """);
        var video = Path.Combine(folder, "long.ts");
        await File.WriteAllTextAsync(video, "", Ct);

        var first = await KeyFrames.FindAsync(ffprobe, video, 40, Ct);
        var lookedFirst = (await File.ReadAllLinesAsync(calls, Ct)).Length;
        var next = await KeyFrames.FindAsync(ffprobe, video, 40.1, Ct);
        var lines = await File.ReadAllLinesAsync(calls, Ct);

        Assert.Equal(30, first.From, 6);
        Assert.Equal(30, next.From, 6);
        Assert.Equal(4, lookedFirst); // the format, then 2, 8 and 32 s of packets
        var at = lines[lookedFirst..].Select(line => line.IndexOf("-read_intervals", StringComparison.Ordinal) is >= 0 and var i ? line.Substring(i, 25) : line);
        Assert.Equal(["-read_intervals 9.500000%"], at);
    }

    [Fact]
    public async Task A_file_replaced_under_the_same_name_is_looked_at_again()
    {
        SkipUnlessInstalled();
        // The clip window looks at one file again and again; written over meanwhile, its start time is read again.
        var path = Path.Combine(fx.NewFolder(), "video");
        File.Copy(await VideoAsync("30fps.mp4"), path);
        var before = await KeyFrames.FindAsync(Tools!.Value.Ffprobe, path, 1, Ct);
        File.Copy(await VideoAsync("30fps.ts"), path, overwrite: true);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1));

        var after = await KeyFrames.FindAsync(Tools!.Value.Ffprobe, path, 1, Ct);

        Assert.Equal(0, before.Origin);
        Assert.True(after.Origin > 1, $"start time {after.Origin}");
    }

    [Theory]
    [InlineData("concat")]
    [InlineData("subfile")]
    public async Task A_file_read_through_a_protocol_and_replaced_is_looked_at_again(string protocol)
    {
        SkipUnlessInstalled();
        // A Hikvision recording is read as concat:header|file, a .nov export's as subfile,…:file (MediaInput): the
        // file is what may be written over.
        var folder = fx.NewFolder();
        var header = Path.Combine(folder, "header");
        await File.WriteAllBytesAsync(header, [], Ct);
        var path = Path.Combine(folder, "rec.ts");
        File.Copy(await VideoAsync("30fps.ts"), path);
        var input = protocol == "concat" ? $"concat:{header}|{path}" : $"subfile,,start,0,end,0,,:{path}";
        var before = await KeyFrames.FindAsync(Tools!.Value.Ffprobe, input, 1, Ct);
        var later = Path.Combine(folder, "later.ts");
        var moved = await ProcessRunner.RunAsync(Tools!.Value.Ffmpeg,
            [.. MediaEncoding.Common, "-i", path, "-c", "copy", "-output_ts_offset", "100", "-f", "mpegts", later], TimeSpan.FromMinutes(1), Ct);
        Assert.True(moved.ExitCode == 0, moved.StandardError);
        File.Copy(later, path, overwrite: true);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1));

        var after = await KeyFrames.FindAsync(Tools!.Value.Ffprobe, input, 1, Ct);

        Assert.True(before.Origin is > 1 and < 2, $"start time {before.Origin}");
        Assert.True(after.Origin > 100, $"start time {after.Origin}");
    }

    [Theory]
    [InlineData("5.1.1", false)]
    [InlineData("6.0", true)]
    public async Task Ffmpeg_before_6_runs_without_the_picture_times_it_lacks(string version, bool kept)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in FFmpeg is a Unix script.");
        // FFmpeg 5 refused the whole run: "Unrecognized option 'stats_enc_pre:v:0'".
        var ffmpeg = StandIn("ffmpeg", $$"""
            #!/bin/sh
            case "$*" in *-version*) echo 'ffmpeg version {{version}} Copyright (c) 2000-2022 the FFmpeg developers'; exit 0;; esac
            echo "$*" > "$(dirname "$0")/arguments"

            """);
        var folder = Path.GetDirectoryName(ffmpeg)!;

        var result = await FfmpegRunner.RunAsync(ffmpeg, ["-i", "in.mp4", .. MediaEncoding.FrameProgress, "out.mp4"], null, Ct);

        Assert.Equal(0, result.ExitCode);
        var arguments = await File.ReadAllTextAsync(Path.Combine(folder, "arguments"), Ct);
        Assert.Equal(kept, arguments.Contains("-stats_enc_pre", StringComparison.Ordinal));
        Assert.Contains("-progress pipe:1", arguments, StringComparison.Ordinal);
    }

    /// <summary>A program named <paramref name="name"/> in a new folder: a shell script (Unix only).</summary>
    private string StandIn(string name, string script)
    {
        var path = Path.Combine(fx.NewFolder(), name);
        File.WriteAllText(path, script);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>The installed FFmpeg's version (major and minor); null when it doesn't say.</summary>
    private async Task<Version?> FfmpegVersionAsync()
    {
        var version = await ProcessRunner.RunAsync(Tools!.Value.Ffmpeg, ["-hide_banner", "-version"], TimeSpan.FromMinutes(1), Ct);
        return CopyVersions.ParseVersion("ffmpeg", version.StandardOutput);
    }

    [Fact]
    public async Task Sound_between_the_start_and_the_first_frame_after_it_is_kept()
    {
        SkipUnlessInstalled();
        // At 5 fps, frame 10 is shown at 2 s; the beep sounds at 1.95 s, and the clip starts at 1.9 s. Timed from the
        // first frame, the clip's sound began at 2 s and the beep was gone.
        var picture = await NumberedAsync("5fps.mp4", "5", 4, Mp4);
        var sound = Path.Combine(fx.NewFolder(), "beep.m4a");
        var beep = await ProcessRunner.RunAsync(Tools!.Value.Ffmpeg,
            [.. MediaEncoding.Common, "-f", "lavfi", "-i", "aevalsrc='if(between(t\\,1.95\\,2.1)\\,0.8*sin(2*PI*1000*t)\\,0)':s=48000:d=4",
                "-c:a", "aac", sound], TimeSpan.FromMinutes(1), Ct);
        Assert.True(beep.ExitCode == 0, beep.StandardError);
        var video = Path.Combine(fx.NewFolder(), "beep.mp4");
        var made = await ProcessRunner.RunAsync(Tools!.Value.Ffmpeg,
            [.. MediaEncoding.Common, "-i", picture, "-i", sound, "-map", "0:v", "-map", "1:a", "-c", "copy", video], TimeSpan.FromMinutes(1), Ct);
        Assert.True(made.ExitCode == 0, made.StandardError);
        var edit = ClipEdit.Of(new ClipRange(1.9, 3));
        edit.Parts[0].Volume = 0.9;

        foreach (var clip in new[] { await CutAsync(video, ClipEdit.Of(new ClipRange(1.9, 3)), exact: true), await CutAsync(video, edit, exact: true) })
        {
            Assert.InRange(await BeepAsync(clip), 0.03, 0.08);
            Assert.Equal(Enumerable.Range(10, 5), (await FramesAsync(clip)).Select(f => f.Number));
        }
    }

    [Fact]
    public async Task An_edit_whose_parts_lie_far_apart_runs_on_while_ffmpeg_reads_between_them()
    {
        SkipUnlessInstalled();
        // Between the parts no picture reaches the encoder, so the time of each one (the progress of a run with
        // -copyts) is silent for as long as FFmpeg reads there; FFmpeg's own -progress shows it is still working. The
        // video comes over a socket that pauses between the parts, three times the stall limit, as a long read would
        // (after 12 s of it: FFmpeg reads the first seconds before it starts). FFmpeg 6 reports nothing while it waits
        // for its input, though it does while it reads (every half second of a 55 s gap on Ubuntu's 6.1).
        if (await FfmpegVersionAsync() is { Major: < 7 } old)
            Assert.Skip($"FFmpeg {old} reports nothing while its input pauses");
        var video = await VideoAsync("30fps.ts", 20);
        var bytes = await File.ReadAllBytesAsync(video, Ct);
        var info = await MediaInfo.ProbeAsync(Tools!.Value.Ffprobe, video, Ct);
        var edit = new ClipEdit
        {
            Parts = [new ClipPart { Start = 0.2, End = 0.5 }, new ClipPart { Start = 15, End = 15.3 }],
        };
        edit.Parts[0].Volume = 0.9;
        var seek = await KeyFrames.FindAsync(Tools!.Value.Ffprobe, video, 0.2, Ct);
        Assert.Equal(0, seek.From); // nothing to seek: a socket can't
        var stall = TimeSpan.FromSeconds(1);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var serving = ServeAsync(listener, bytes, pauseAt: bytes.Length * 3 / 5 / 188 * 188, pause: stall * 3);
            var output = Path.Combine(fx.NewFolder(), "clip.mp4");
            var args = MediaEncoding.ClipEditArguments($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/video.ts", output, "mp4",
                edit, new MediaOptions(), info, seek: seek);

            var result = await FfmpegRunner.RunAsync(Tools!.Value.Ffmpeg, args, null, Ct, stall);

            Assert.True(result.ExitCode == 0, result.StandardError);
            await serving;
            var frames = await FramesAsync(video);
            var want = edit.Parts.SelectMany(part => frames.Where(f => f.Time >= part.Start - 0.000001 && f.Time < part.End - 0.000001))
                .Select(f => f.Number);
            Assert.Equal(want, (await FramesAsync(output)).Select(f => f.Number));
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Answers one HTTP request with <paramref name="bytes"/>, stopping for <paramref name="pause"/> after
    /// <paramref name="pauseAt"/> of them.
    /// </summary>
    private async Task ServeAsync(TcpListener listener, byte[] bytes, int pauseAt, TimeSpan pause)
    {
        using var client = await listener.AcceptTcpClientAsync(Ct);
        var stream = client.GetStream();
        var request = new byte[8192];
        var read = 0;
        while (!Encoding.ASCII.GetString(request, 0, read).Contains("\r\n\r\n", StringComparison.Ordinal)
            && await stream.ReadAsync(request.AsMemory(read), Ct) is > 0 and var count)
            read += count;
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.0 200 OK\r\nContent-Type: video/mp2t\r\nContent-Length: {bytes.Length}\r\n\r\n"), Ct);
        await stream.WriteAsync(bytes.AsMemory(0, pauseAt), Ct);
        await stream.FlushAsync(Ct);
        await Task.Delay(pause, Ct);
        await stream.WriteAsync(bytes.AsMemory(pauseAt), Ct);
    }
}
