// Exact seeks (#1205): the frame the clip window shows at a time is the frame shown at that time, and a clip, a copy,
// the window's playback and a Live Photo from there start with it, in the files where FFmpeg's own -ss misses: an MP4
// at 30 or 29.97 fps (frame times rounded to the millisecond seeked a frame late), MPEG-TS and MPEG-PS (no index: the
// seek landed between key frames and the decoder skipped ahead to the next one), HEVC with open groups of pictures (the
// leading pictures were skipped), AVI with B-frames (key frames without a time to be shown at) and a TS whose sound
// starts first. Every frame of the test videos carries its number in its pixels, eight black or white blocks, so each
// picture says which frame it is. The real tests run only when FFmpeg is installed.

using System.Globalization;
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
        Assert.Equal(1.9667, KeyFrames.Choose(json, 1.4, 2.5, how: Ts1, fromFirstPacket: false)!.Value, 6);
        Assert.Equal(1.9667, KeyFrames.Choose(json, 1.4, 2.0, how: Ts1, fromFirstPacket: false)!.Value, 6); // the key frame itself
        Assert.Equal(2.9667, KeyFrames.Choose(json, 1.4, 3.2, how: Ts1, fromFirstPacket: false)!.Value, 6);
        // Before the first key frame read: none in this look.
        Assert.Null(KeyFrames.Choose(json, 1.4, 1.9, how: Ts1, fromFirstPacket: false));
    }

    [Fact]
    public void Any_other_file_is_read_from_the_time_its_key_frame_is_shown()
    {
        // MP4, MKV, AVI: FFmpeg seeks through an index of key frames by the time they are shown; at the decode time it
        // landed on the key frame before, a whole group of pictures early.
        var json = Packets((2.0, 1.9333, true), (4.0, 3.8, true));
        Assert.Equal(2.0, KeyFrames.Choose(json, 0, 3, how: Index1, fromFirstPacket: false)!.Value, 6);
        Assert.Equal(4.0, KeyFrames.Choose(json, 0, 4.5, how: Index1, fromFirstPacket: false)!.Value, 6);
    }

    [Fact]
    public void A_key_frame_whose_leading_pictures_are_asked_for_is_passed_over()
    {
        // HEVC with an open group: the key frame shown at 2 s is decoded at 1.8 s, before the pictures shown at 1.87 to
        // 1.97 s that lead it and need the group before. Starting at it would skip them, so the key frame at 0 is
        // taken.
        var json = Packets((0, -0.0667, true), (2.0, 1.8, true), (1.8667, 1.8333, false), (1.9333, 1.8667, false));
        Assert.Equal(0, KeyFrames.Choose(json, 0, 1.8667, how: Ts1, fromFirstPacket: false)); // before 0 counts as 0
        Assert.Equal(1.8, KeyFrames.Choose(json, 0, 2.0, how: Ts1, fromFirstPacket: false)!.Value, 6);
    }

    [Fact]
    public void A_key_frame_without_a_time_to_be_shown_at_starts_reading_at_the_one_before()
    {
        // AVI with B-frames and MPEG-PS: ffprobe gives some key frames a decode time only. Such a key frame decoded
        // before the time may still be shown after it, so reading starts at the key frame before it, which surely
        // isn't.
        var json = Packets((0.5, 0.4667, true), (null, 1.4333, true), (null, 2.4333, true));
        Assert.Equal(1.4333, KeyFrames.Choose(json, 0, 2.6, how: Index1, fromFirstPacket: true)!.Value, 6);
        Assert.Equal(0.4667, KeyFrames.Choose(json, 0, 1.5, how: Ts1, fromFirstPacket: false)!.Value, 6);
        // The file's first key frame: from the start; further on, none in this look.
        var first = Packets((null, 0.4, true), (null, 1.4, true));
        Assert.Equal(0, KeyFrames.Choose(first, 0, 1.0, how: Index1, fromFirstPacket: true));
        Assert.Null(KeyFrames.Choose(first, 0, 1.0, how: Index1, fromFirstPacket: false));
    }

    [Fact]
    public void From_the_files_first_key_frame_reading_starts_at_its_first_packet()
    {
        // An FLV whose sound starts first: its first key frame is shown 23 ms in. -ss 0.023 made FFmpeg seek before the
        // file's start (3/23 s early, B-frames), which failed and landed between key frames: frame 45 for the first 45.
        var json = Packets((0.067, 0, true), (1.567, 1.5, true));
        var flv = new Seeking(Unindexed: false, ByShownTime: false, Reordered: true);
        Assert.Equal(0, KeyFrames.Choose(json, 0.044, 0.7, flv, fromFirstPacket: true));
        // The same key frame in a look that didn't start at the first packet may be any key frame.
        Assert.Equal(0.023 + KeyFrames.DtsHeuristic, KeyFrames.Choose(json, 0.044, 0.7, flv, fromFirstPacket: false)!.Value, 6);
    }

    [Fact]
    public void With_b_frames_ss_allows_for_the_3_23_s_ffmpeg_seeks_early_where_it_does()
    {
        var json = Packets((0.5, 0.4333, true), (10.0, 9.8, true));
        // MKV, AVI, FLV: FFmpeg seeks 3/23 s before -ss, so -ss is that much past the key frame, or the key frame
        // before it is read, a whole group of pictures more; never past the time itself.
        var mkv = new Seeking(Unindexed: false, ByShownTime: false, Reordered: true);
        Assert.Equal(10 + KeyFrames.DtsHeuristic, KeyFrames.Choose(json, 0, 12, mkv, fromFirstPacket: false)!.Value, 6);
        Assert.Equal(10.05, KeyFrames.Choose(json, 0, 10.05, mkv, fromFirstPacket: false)!.Value, 6);
        // MP4 and MOV are sought by the time shown, with no margin.
        Assert.Equal(10, KeyFrames.Choose(json, 0, 12, mkv with { ByShownTime = true }, fromFirstPacket: false)!.Value, 6);
        // MPEG-TS: from the decode time, or as much later as the margin still lands on the key frame.
        var ts = new Seeking(Unindexed: true, ByShownTime: false, Reordered: true);
        Assert.Equal(9.8 + KeyFrames.DtsHeuristic, KeyFrames.Choose(json, 0, 12, ts, fromFirstPacket: false)!.Value, 6);
        Assert.Equal(10, KeyFrames.Choose(Packets((10.0, 9.95, true)), 0, 12, ts, fromFirstPacket: false)!.Value, 6);
    }

    [Fact]
    public void A_key_frame_read_half_a_microsecond_late_is_the_frame_at_the_time()
    {
        // ffprobe writes six decimals: 1.000001 for a key frame shown at the 1.0000006 s asked for.
        var json = """{"packets":[{"pts_time":"1.000001","dts_time":"N/A","flags":"K__"}]}""";
        // Reading never starts past the time itself.
        Assert.Equal(1.0000006, KeyFrames.Choose(json, 0, 1.0000006, how: Index1, fromFirstPacket: false)!.Value, 7);
        Assert.Null(KeyFrames.Choose("""{"packets":[]}""", 0, 1, how: Index1, fromFirstPacket: true));
        Assert.Null(KeyFrames.Choose("{}", 0, 1, how: Index1, fromFirstPacket: true));
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
        var exact = new SeekPoint(60, 1.4, 62);
        Assert.Equal(["-copyts", "-ss", "60.000000"], exact.Input);
        Assert.Equal("62.65", exact.Time(61.25));
        Assert.Equal("PTS-62.65/TB", exact.Zero(61.25));
        Assert.Equal(["-output_ts_offset", "-62.65"], exact.Offset(61.25));
        Assert.Empty(SeekPoint.Plain(61.25).Offset(61.25));
        // Without the file's start time, as Filee seeked before: straight to the time, counting from there.
        var plain = SeekPoint.Plain(61.25);
        Assert.Equal(["-ss", "61.250000"], plain.Input);
        Assert.Equal("0", plain.Time(61.25));
        Assert.Equal("1.5", plain.Time(62.75));
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
            new SeekPoint(10, 1.4, 10));
        Assert.Equal(["-copyts", "-ss", "10.000000"], args.SkipWhile(a => a != "-copyts").Take(3));
        Assert.Contains("atrim=start=13.9:end=21.4", args[args.IndexOf("-filter_complex") + 1]);
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

    private static readonly string[] Mp4 = ["-c:v", "libx264", "-g", "45", "-pix_fmt", "yuv420p"];
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
        "25fps.mp4" => ("25", "128x32", Mp4),
        _ => throw new ArgumentException(name, nameof(name)),
    };

    public static TheoryData<string> Videos =>
        ["30fps.mp4", "2997fps.mp4", "15fps.mp4", "30fps.ts", "15fps.ts", "soundfirst.ts", "dvd.vob", "opengop.mp4", "opengop.ts", "bframes.avi",
            "bframes.mkv", "sound.flv"];

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
    [InlineData("soundfirst.ts", 40, 70)]
    [InlineData("opengop.ts", 53, 83)] // decoded long before shown: an end counted from the first frame was a frame late
    [InlineData("15fps.mp4", 26, 41)]
    [InlineData("bframes.avi", 40, 70)]
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
    // MP4 and MKV with B-frames: from the key frame FFmpeg's own index lands on, not a group earlier.
    [InlineData("30fps.mp4", "mp4", 60, 85)]
    [InlineData("bframes.mkv", "mkv", 80, 88)]
    public async Task A_copied_clip_starts_at_the_key_frame_shown_last_before_the_start(string name, string target, int start, int end)
    {
        SkipUnlessInstalled();
        var video = await VideoAsync(name, 4);
        var frames = await FramesAsync(video);
        var key = frames[..(start + 1)].Last(f => f.Key).Number;
        Assert.True(key < start, $"frame {start} of {name} is a key frame itself; the test needs one between key frames");

        var clip = await CutAsync(video, ClipEdit.Of(new ClipRange(frames[start].Time, frames[end].Time)), exact: false, target: target);

        // Every frame from the key frame on, none of them broken or skipped. (Where a copy ends depends on the packet
        // order, so only the start is exact.)
        var numbers = (await FramesAsync(clip)).Select(f => f.Number).ToList();
        Assert.Equal(Enumerable.Range(key, numbers.Count), numbers);
        Assert.InRange(numbers[^1], end - 2, end + 3);
    }

    [Fact]
    public async Task Playback_in_the_window_starts_with_the_frame_it_showed()
    {
        SkipUnlessInstalled();
        var video = await VideoAsync("soundfirst.ts", 4);
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
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(20)]
    public async Task A_live_photo_of_a_25_fps_video_shows_the_movies_frame_at_the_still_time(int cover)
    {
        SkipUnlessInstalled();
        // The movie and its photo pick frames by 30 fps timing from the same time 0: counted from the file's own times,
        // the photo's timing fell between other frames than the movie's.
        var video = await VideoAsync("25fps.mp4", 4);
        var frames = await FramesAsync(video);
        var preset = new Preset { TargetFormat = "jpg", Media = { LivePhoto = LivePhotoKind.Apple } };
        preset.Media.LivePhotoStart = frames[20].Time;
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

    [Fact]
    public async Task A_live_photo_of_an_mpeg_ts_file_shows_the_frame_its_times_point_at()
    {
        SkipUnlessInstalled();
        var video = await VideoAsync("soundfirst.ts", 4);
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
}
