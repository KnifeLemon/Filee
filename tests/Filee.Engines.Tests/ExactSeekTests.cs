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
    public void A_key_frame_without_a_time_to_be_shown_at_starts_reading_at_the_one_before()
    {
        // AVI with B-frames and MPEG-PS: ffprobe gives some key frames a decode time only. Such a key frame decoded
        // before the time may still be shown after it, so reading starts at the key frame before it, which surely
        // isn't.
        var json = Packets((0.5, 0.4667, true), (null, 1.4333, true), (null, 2.4333, true));
        Assert.Equal(1.4333, KeyFrames.Choose(json, 0, 2.6, how: Index1, fromFirstPacket: true).From!.Value, 6);
        Assert.Equal(0.4667, KeyFrames.Choose(json, 0, 1.5, how: Ts1, fromFirstPacket: false).From!.Value, 6);
        // The file's first key frame: from the start; further on, none in this look.
        var first = Packets((null, 0.4, true), (null, 1.4, true));
        Assert.Equal(0, KeyFrames.Choose(first, 0, 1.0, how: Index1, fromFirstPacket: true).From);
        Assert.Null(KeyFrames.Choose(first, 0, 1.0, how: Index1, fromFirstPacket: false).From);
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
        "bframes.mkv" => ("30", "128x32", ["-c:v", "libx264", "-g", "32", "-x264-params", "scenecut=0", "-pix_fmt", "yuv420p"]),
        // B-frames decoded 2/15 s before they are shown, longer than FFmpeg's 3/23 s margin.
        "15fps.ts" => ("15", "128x32", ["-c:v", "libx264", "-g", "30", "-x264-params", "scenecut=0", "-pix_fmt", "yuv420p", "-f", "mpegts"]),
        // An FLV whose sound starts before its first key frame is shown.
        "sound.flv" => ("30", "128x32", ["-c:v", "libx264", "-g", "45", "-x264-params", "scenecut=0", "-pix_fmt", "yuv420p", "-c:a", "aac",
            "-ar", "44100"]),
        "60fps.mp4" => ("60", "128x32", ["-c:v", "libx264", "-g", "90", "-x264-params", "scenecut=0", "-pix_fmt", "yuv420p"]),
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
        _ => throw new ArgumentException(name, nameof(name)),
    };

    public static TheoryData<string> Videos =>
        ["30fps.mp4", "2997fps.mp4", "15fps.mp4", "30fps.ts", "15fps.ts", "soundfirst.ts", "dvd.vob", "opengop.mp4", "opengop.ts", "bframes.avi",
            "bframes.mkv", "sound.flv", "10mhz.mp4"];

    private async Task<string> VideoAsync(string name, double seconds = 3)
    {
        var (rate, size, codec) = VideoOf(name);
        // With sound, an MPEG-TS file starts with it, and its frames are shown at times that aren't round.
        return await NumberedAsync(name, rate, seconds, codec, beepFrame: name == "soundfirst.ts" ? 50 : null, tone: name == "sound.flv",
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
        Assert.Equal(Enumerable.Range(0, 60), (await FramesAsync(picture)).Select(f => f.Number));
    }

    /// <summary>When the beep starts in <paramref name="path"/>'s sound, in seconds.</summary>
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
    public async Task A_copied_clip_starts_at_the_key_frame_shown_last_before_the_start(string name, string target, int start, int end)
    {
        SkipUnlessInstalled();
        var video = await VideoAsync(name, name == "5fps.ts" ? 10 : 4);
        var frames = await FramesAsync(video);
        var key = frames[..(start + 1)].Last(f => f.Key).Number;

        var clip = await CutAsync(video, ClipEdit.Of(new ClipRange(frames[start].Time, frames[end].Time)), exact: false, target: target);

        // Every frame from the key frame on, none of them broken or skipped. (Where a copy ends depends on the packet
        // order, so only the start is exact.)
        var numbers = (await FramesAsync(clip)).Select(f => f.Number).ToList();
        Assert.Equal(Enumerable.Range(key, numbers.Count), numbers);
        Assert.InRange(numbers[^1], end - 2, end + 3);
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
        Assert.Equal(Enumerable.Range(41, 30), (await FramesAsync(await CutAsync(video, edit, exact: true))).Select(f => f.Number));
        int? first = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await ClipPreview.PlayAsync(video, ClipEdit.Of(range), Height, (pixels, width, height, _) =>
        {
            first ??= Number(pixels, 0, width, height, bytesPerPixel: 4);
            stop.Cancel();
        }, stop.Token);
        Assert.Equal(41, first);
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

    [Fact]
    public async Task Sound_that_starts_after_the_picture_keeps_its_place_in_an_edited_clip()
    {
        SkipUnlessInstalled();
        // The sound starts 1 s into the video and beeps at 1.5 s; from 0.5 s, the beep is 1 s into the clip. Each
        // part's sound counted from its own first sample beeped at 0.5 s.
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
        var edit = ClipEdit.Of(new ClipRange(0.5, 2.8));
        edit.Parts[0].Volume = 0.9;

        Assert.InRange(await BeepAsync(await CutAsync(video, edit, exact: true)), 0.98, 1.04);
        Assert.InRange(await BeepAsync(await CutAsync(video, ClipEdit.Of(new ClipRange(0.5, 2.8)), exact: true)), 0.98, 1.04);
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

    [Fact]
    public async Task An_exact_clips_progress_reaches_its_length()
    {
        SkipUnlessInstalled();
        // With -copyts, FFmpeg's -progress counted from its first report: a clip ended its progress well short of 1,
        // and mixed with each picture's time it went back and forth.
        var video = await VideoAsync("30fps.ts", 4);
        var info = await MediaInfo.ProbeAsync(Tools!.Value.Ffprobe, video, Ct);
        var range = new ClipRange(0.7, 3.7);
        var seek = await KeyFrames.FindAsync(Tools!.Value.Ffprobe, video, range.Start, Ct);
        var output = Path.Combine(fx.NewFolder(), "clip.mp4");
        var args = MediaEncoding.Plan(video, output, "mp4", new MediaOptions(), info, fx.NewFolder(), clip: range, clipSeek: seek).Single().Arguments;
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
    public async Task Playback_in_the_window_starts_with_the_frame_it_showed(string name)
    {
        SkipUnlessInstalled();
        var video = await VideoAsync(name, 4);
        var frames = await FramesAsync(video);
        var edit = ClipEdit.Of(new ClipRange(frames[40].Time, frames[70].Time));

        int? first = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await ClipPreview.PlayAsync(video, edit, Height, (pixels, width, height, _) =>
        {
            first ??= Number(pixels, 0, width, height, bytesPerPixel: 4);
            stop.Cancel();
        }, stop.Token);

        Assert.Equal(40, first);
    }

    [Theory]
    [InlineData("60fps.mp4", 20, 1)]
    [InlineData("60fps.mp4", 23, 2)]
    [InlineData("60fps.mp4", 23, 20)]
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
        Assert.Equal((await FramesAsync(movie))[cover].Number, await NumberOfPictureAsync(photo));
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

    [Theory]
    [InlineData("soundfirst.ts")]
    [InlineData("dvd.vob")]
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
        // (after 12 s of it: FFmpeg reads the first seconds before it starts).
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
