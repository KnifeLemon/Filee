// Exact seeks (#1205): the frame the clip window shows at a time is the frame shown at that time, and a clip, a copy,
// the window's playback and a Live Photo from there start with it, in the files where FFmpeg's own -ss misses: an MP4
// at 30 or 29.97 fps (frame times rounded to the millisecond seeked a frame late), MPEG-TS and MPEG-PS (no index: the
// seek landed between key frames and the decoder skipped ahead to the next one) and HEVC with open groups of pictures
// (the leading pictures were skipped). Every frame of the test videos carries its number in its pixels, eight black or
// white blocks, so each picture says which frame it is. The real tests run only when FFmpeg is installed.

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

    private static string Packets(double start, params (double Pts, double? Dts, bool Key)[] packets) =>
        Packets(start, null, 0, packets);

    private static string Packets(double start, double? videoStart, int bFrames, params (double Pts, double? Dts, bool Key)[] packets) =>
        JsonSerializer.Serialize(new
        {
            streams = new[]
            {
                new
                {
                    start_time = videoStart is { } first ? first.ToString("0.000000", CultureInfo.InvariantCulture) : "N/A",
                    has_b_frames = bFrames,
                },
            },
            packets = packets.Select(p => new Dictionary<string, string>
            {
                ["pts_time"] = p.Pts.ToString("0.000000", CultureInfo.InvariantCulture),
                ["dts_time"] = p.Dts is { } dts ? dts.ToString("0.000000", CultureInfo.InvariantCulture) : "N/A",
                ["flags"] = p.Key ? "K__" : "___",
            }),
            format = new { start_time = start.ToString("0.000000", CultureInfo.InvariantCulture) },
        });

    [Fact]
    public void Decoding_starts_at_the_last_key_frame_shown_at_or_before_the_time()
    {
        // An MPEG-TS file starting at 1.4 s, key frames shown at 2 s and 3 s of the file (decoded a frame earlier).
        var json = Packets(1.4, (3.4, 3.3667, true), (3.4333, 3.4, false), (4.4, 4.3667, true), (4.4333, 4.4, false));

        // Times count from the file's start, and the start is where decoding begins: the key frame's decode time.
        Assert.Equal(1.9667, KeyFrames.Look(json, 2.5).Start!.Value, 6);
        Assert.Equal(1.9667, KeyFrames.Look(json, 2.0).Start!.Value, 6); // the key frame itself
        Assert.Equal(2.9667, KeyFrames.Look(json, 3.2).Start!.Value, 6);
        // Before the first key frame read: none in this look.
        Assert.Null(KeyFrames.Look(json, 1.9).Start);
    }

    [Fact]
    public void A_key_frame_whose_leading_pictures_are_asked_for_is_passed_over()
    {
        // HEVC with an open group: the key frame at 2 s is decoded at 1.8 s, before the pictures shown at 1.87–1.97 s
        // that lead it and need the group before. Starting at it would skip them, so the key frame at 0 is taken.
        var json = Packets(0, (0, -0.0667, true), (2.0, 1.8, true), (1.8667, 1.8333, false), (1.9333, 1.8667, false));
        Assert.Equal(0, KeyFrames.Look(json, 1.8667).Start); // a decode time before the file's start counts as 0
        Assert.Equal(1.8, KeyFrames.Look(json, 2.0).Start!.Value, 6);
    }

    [Fact]
    public void A_key_frame_without_a_decode_time_starts_at_the_time_it_is_shown()
    {
        Assert.Equal(1.5, KeyFrames.Look(Packets(0.5, (2.0, null, true)), 1.6).Start!.Value, 6);
        // ffprobe's six decimals: a key frame read half a microsecond after the time is the frame at the time.
        Assert.Equal(1.0, KeyFrames.Look(Packets(0, (1.0000004, null, true)), 1.0).Start!.Value, 6);
        Assert.Equal((null, 0), KeyFrames.Look("""{"packets":[],"format":{}}""", 1));
        Assert.Equal((null, 0), KeyFrames.Look("{}", 1));
    }

    [Fact]
    public void Decoding_never_starts_before_the_videos_first_frame()
    {
        // An MPEG-TS file whose sound starts 21 ms before its picture: the first key frame is decoded before the file's
        // start. Reading from 0, FFmpeg would count time from the picture's start and every frame would come out one
        // early; from the first frame on, it counts from where -ss says.
        var json = Packets(1.445333, 1.466667, 0, (1.466667, 1.4, true), (2.966667, 2.9, true));
        Assert.Equal(0.021334, KeyFrames.Look(json, 0.5).Start!.Value, 6);
        Assert.Equal(1.454667, KeyFrames.Look(json, 1.6).Start!.Value, 6); // later key frames as usual
        // Before the first frame no key frame is shown yet: DecodeStartAsync starts at the first frame.
        Assert.Equal((null, 0.021334), (KeyFrames.Look(json, 0).Start, Math.Round(KeyFrames.Look(json, 0).FirstFrame, 6)));
    }

    [Fact]
    public void With_b_frames_ss_is_when_the_key_frame_is_shown_unless_it_is_decoded_long_before()
    {
        // MP4 and MOV seek by the time a frame is shown: -ss at the key frame's own time lands on it (at its decode
        // time it landed on the key frame before, a whole group early). MPEG-TS seeks by decode time, 3/23 s before -ss
        // with B-frames, which reaches a key frame decoded up to 3/23 s before it is shown.
        var json = Packets(0, 0, 2, (2.0, 1.9333, true), (4.0, 3.8, true));
        Assert.Equal(2.0, KeyFrames.Look(json, 3).Start!.Value, 6);
        Assert.Equal(3.8 + KeyFrames.DtsHeuristic, KeyFrames.Look(json, 4.5).Start!.Value, 6); // decoded 0.2 s before
        // Without B-frames a key frame is decoded when it is shown, and nothing is sought earlier.
        Assert.Equal(1.9333, KeyFrames.Look(Packets(0, 0, 0, (2.0, 1.9333, true)), 3).Start!.Value, 6);
    }

    [Fact]
    public void A_look_reads_packets_from_the_files_start_on_and_a_moment_past_the_time()
    {
        var args = KeyFrames.Arguments("in.ts", 3, 5);
        // "+": from the file's start time, as -ss counts; the end a tenth of a second past the time.
        Assert.Equal("+3.000000%+2.100000", args[args.IndexOf("-read_intervals") + 1]);
        Assert.Equal("V:0", args[args.IndexOf("-select_streams") + 1]);
        Assert.Contains("stream=start_time", args[args.IndexOf("-show_entries") + 1]);
        Assert.Equal("in.ts", args[^1]);
    }

    [Fact]
    public async Task Without_ffprobe_a_frame_is_seeked_as_before()
    {
        var missing = Path.Combine(fx.NewFolder(), EngineEnvironment.ProgramName("ffprobe.exe"));
        Assert.Equal(12.5, await KeyFrames.DecodeStartAsync(missing, "in.mp4", 12.5, Ct));
        Assert.Equal(0, await KeyFrames.DecodeStartAsync(missing, "in.mp4", 0, Ct));
        Assert.Equal(0, await KeyFrames.DecodeStartAsync(missing, "in.mp4", double.NaN, Ct));
    }

    [Fact]
    public async Task A_file_ffprobe_cant_read_is_seeked_as_before()
    {
        SkipUnlessInstalled();
        var junk = Path.Combine(fx.NewFolder(), "junk.mp4");
        await File.WriteAllTextAsync(junk, "not a video", Ct);
        Assert.Equal(7, await KeyFrames.DecodeStartAsync(Tools!.Value.Ffprobe, junk, 7, Ct));
    }

    // ───────────────────────── Numbered videos ─────────────────────────

    private const int Width = 128, Height = 32;

    /// <summary>The number a picture carries: block b (16 pixels wide) is white when bit b is set.</summary>
    private static int Number(byte[] gray, int offset = 0, int width = Width, int height = Height, int bytesPerPixel = 1)
    {
        var row = offset + height / 2 * width * bytesPerPixel;
        var number = 0;
        for (var bit = 0; bit < 8; bit++)
        {
            var x = (bit * 16 + 8) * width / Width;
            if (gray[row + x * bytesPerPixel] > 128)
                number |= 1 << bit;
        }
        return number;
    }

    /// <summary>
    /// A video of <paramref name="seconds"/> at <paramref name="rate"/> whose frame N shows N, with a beep in frame
    /// <paramref name="beepFrame"/> when given; null when this FFmpeg lacks the encoder.
    /// </summary>
    private async Task<string?> NumberedAsync(string name, string rate, double seconds, string[] codec, int? beepFrame = null)
    {
        var path = Path.Combine(fx.NewFolder(), name);
        var numbers = "geq=lum='if(mod(floor(N/pow(2\\,floor(X/16)))\\,2)\\,235\\,16)':cb=128:cr=128";
        var length = seconds.ToString(CultureInfo.InvariantCulture);
        List<string> args =
        [
            .. MediaEncoding.Common, "-f", "lavfi", "-i", $"color=black:s={Width}x{Height}:r={rate}:d={length},format=yuv420p,{numbers}",
        ];
        if (beepFrame is { } beep)
            args.AddRange(["-f", "lavfi", "-i",
                $"aevalsrc='if(between(t\\,{beep}/({rate})\\,{beep + 1}/({rate}))\\,0.8*sin(2*PI*1000*t)\\,0)':s=48000:d={length}", "-c:a", "aac"]);
        args.AddRange([.. codec, path]);
        var result = await ProcessRunner.RunAsync(Tools!.Value.Ffmpeg, args, TimeSpan.FromMinutes(2), Ct);
        if (result.ExitCode != 0 && result.StandardError.Contains("Unknown encoder", StringComparison.OrdinalIgnoreCase))
            return null;
        Assert.True(result.ExitCode == 0, result.StandardError);
        return path;
    }

    /// <summary>
    /// Every frame of the video in the order shown: its time from the file's start and the number it shows.
    /// </summary>
    private async Task<List<(double Time, int Number)>> FramesAsync(string path)
    {
        var raw = Path.Combine(fx.NewFolder(), "frames.gray");
        var decoded = await ProcessRunner.RunAsync(Tools!.Value.Ffmpeg,
            [.. MediaEncoding.Common, "-i", path, "-map", "0:V:0", "-fps_mode", "passthrough", "-vf", $"scale={Width}:{Height}",
                "-f", "rawvideo", "-pix_fmt", "gray", raw], TimeSpan.FromMinutes(2), Ct);
        Assert.True(decoded.ExitCode == 0, decoded.StandardError);
        var probed = await ProcessRunner.RunAsync(Tools!.Value.Ffprobe,
            ["-v", "error", "-select_streams", "V:0", "-show_entries", "frame=best_effort_timestamp_time,pts_time,pkt_dts_time:format=start_time",
                "-print_format", "json", path], TimeSpan.FromMinutes(1), Ct);
        Assert.True(probed.ExitCode == 0, probed.StandardError);
        using var json = JsonDocument.Parse(probed.StandardOutput);
        var start = Seconds(json.RootElement.GetProperty("format"), "start_time") ?? 0;
        var bytes = await File.ReadAllBytesAsync(raw, Ct);
        var frames = new List<(double, int)>();
        var index = 0;
        foreach (var frame in json.RootElement.GetProperty("frames").EnumerateArray())
        {
            var time = Seconds(frame, "best_effort_timestamp_time") ?? Seconds(frame, "pts_time") ?? Seconds(frame, "pkt_dts_time");
            if (time is { } t && (index + 1) * Width * Height <= bytes.Length)
                frames.Add((t - start, Number(bytes, index * Width * Height)));
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
    private static readonly string[] Ps = ["-c:v", "mpeg2video", "-g", "30", "-b:v", "1M", "-f", "vob"];
    private static readonly string[] OpenGopHevc =
        ["-c:v", "libx265", "-x265-params", "log-level=error:keyint=45:open-gop=1", "-tag:v", "hvc1", "-pix_fmt", "yuv420p"];

    public static TheoryData<string, string> Videos => new()
    {
        { "30fps.mp4", "30" },
        { "2997fps.mp4", "30000/1001" },
        { "30fps.ts", "30" },
        { "soundfirst.ts", "30" },
        { "30fps.mpg", "30" },
        { "opengop.mp4", "30" },
    };

    private static string[] CodecOf(string name) => name switch
    {
        "30fps.ts" or "soundfirst.ts" => Ts,
        "30fps.mpg" => Ps,
        "opengop.mp4" => OpenGopHevc,
        _ => Mp4,
    };

    // ───────────────────────── The clip window's picture ─────────────────────────

    [Theory]
    [MemberData(nameof(Videos))]
    public async Task Every_frame_the_window_asks_for_is_the_frame_shown_at_that_time(string name, string rate)
    {
        SkipUnlessInstalled();
        // With sound, an MPEG-TS file starts with it, and the frames are shown at times that aren't round: frame 40 at
        // 1.3546666… s, which six decimals round up past it.
        var video = await NumberedAsync(name, rate, 3, CodecOf(name), beepFrame: name == "soundfirst.ts" ? 50 : null);
        if (video is null && Required)
            Assert.Fail($"FILEE_TEST_FFMPEG is required, but this FFmpeg can't write {name}.");
        Assert.SkipWhen(video is null, $"This FFmpeg can't write {name}");
        var frames = await FramesAsync(video!);
        Assert.InRange(frames.Count, 80, 95); // 3 s at 30 or 29.97 fps, as decoded

        var folder = fx.NewFolder();
        List<string> wrong = [];
        foreach (var (time, number) in frames)
        {
            var output = Path.Combine(folder, $"{number}.jpg");
            Assert.True(await VideoFrames.WriteFrameAsync(video!, time, output, Height, Ct), $"no frame at {time}");
            if (await NumberOfPictureAsync(output) is var shown && shown != number)
                wrong.Add($"{time:0.000000} s: frame {shown} instead of {number}");
        }
        Assert.True(wrong.Count == 0, string.Join("; ", wrong));
    }

    [Fact]
    public async Task A_frame_long_after_the_last_key_frame_is_found_by_looking_further_back()
    {
        SkipUnlessInstalled();
        // One key frame only, at the start of an MPEG-TS file: the first looks back (2 s, then 8 s) find none.
        var video = await NumberedAsync("long.ts", "25", 10,
            ["-c:v", "libx264", "-g", "1000", "-x264-params", "scenecut=0", "-pix_fmt", "yuv420p", "-f", "mpegts"]);
        var frames = await FramesAsync(video!);
        Assert.Equal(0, await KeyFrames.DecodeStartAsync(Tools!.Value.Ffprobe, video!, frames[^3].Time, Ct), 3);

        var folder = fx.NewFolder();
        foreach (var (time, number) in frames.Where((_, i) => i % 40 == 7).Append(frames[^3]))
        {
            var output = Path.Combine(folder, $"{number}.jpg");
            Assert.True(await VideoFrames.WriteFrameAsync(video!, time, output, Height, Ct));
            Assert.Equal(number, await NumberOfPictureAsync(output));
        }
    }

    // ───────────────────────── Clips, playback and Live Photos ─────────────────────────

    private async Task<string> CutAsync(string source, ClipEdit edit, bool exact, string target = "mp4")
    {
        var tool = BuiltInData.CreatePresets().Single(p => p.Id == "tool-clip");
        tool.Media.ExactCut = exact;
        var job = await fx.ConvertAsync([source], ClipTool.ConversionOf(tool, target, edit));
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return Assert.Single(job.Outputs);
    }

    [Fact]
    public async Task An_exact_clip_of_an_mpeg_ts_file_starts_and_ends_with_the_chosen_frames()
    {
        SkipUnlessInstalled();
        var video = await NumberedAsync("match.ts", "30", 4, Ts, beepFrame: 50);
        var frames = await FramesAsync(video!);

        var clip = await CutAsync(video!, ClipEdit.Of(new ClipRange(frames[40].Time, frames[70].Time)), exact: true);

        Assert.Equal(Enumerable.Range(40, 30), (await FramesAsync(clip)).Select(f => f.Number));
    }

    [Fact]
    public async Task An_edited_clip_of_an_mpeg_ts_file_keeps_its_picture_and_sound_together()
    {
        SkipUnlessInstalled();
        // The beep sounds in frame 50. A graph that started the picture at a later key frame showed frame 50 too early.
        var video = await NumberedAsync("match.ts", "30", 4, Ts, beepFrame: 50);
        var frames = await FramesAsync(video!);
        var edit = ClipEdit.Of(new ClipRange(frames[40].Time, frames[70].Time));
        edit.Parts[0].Volume = 0.9; // an edit: the clip goes through the filter graph

        var clip = await CutAsync(video!, edit, exact: true);

        var numbers = (await FramesAsync(clip)).Select(f => f.Number).ToList();
        Assert.Equal(Enumerable.Range(40, 30), numbers);
        var beep = await BeepAsync(clip);
        Assert.InRange(beep, 10 / 30.0 - 0.02, 10 / 30.0 + 0.02); // frame 50 is the clip's 11th
    }

    /// <summary>When the beep starts in <paramref name="path"/>'s sound, in seconds.</summary>
    private async Task<double> BeepAsync(string path)
    {
        var result = await ProcessRunner.RunAsync(Tools!.Value.Ffmpeg,
            ["-hide_banner", "-nostdin", "-v", "info", "-i", path, "-map", "0:a:0", "-af", "silencedetect=n=-30dB:d=0.01", "-f", "null", "-"],
            TimeSpan.FromMinutes(1), Ct);
        Assert.True(result.ExitCode == 0, result.StandardError);
        var line = result.StandardError.Split('\n').First(l => l.Contains("silence_end:", StringComparison.Ordinal));
        var text = line[(line.IndexOf("silence_end:", StringComparison.Ordinal) + 12)..].Trim().Split(' ')[0];
        return double.Parse(text, CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task A_copied_clip_of_an_mpeg_ts_file_starts_at_the_key_frame_before_the_start()
    {
        SkipUnlessInstalled();
        // Key frames every 45 frames: frame 45 is the last one before frame 60.
        var video = await NumberedAsync("match.ts", "30", 4, Ts);
        var frames = await FramesAsync(video!);

        var clip = await CutAsync(video!, ClipEdit.Of(new ClipRange(frames[60].Time, frames[90].Time)), exact: false, target: "ts");

        // Every frame from the key frame on, none of them broken or skipped: a copy that started between key frames
        // began with frames that can't be decoded, and the decoder skipped to frame 90. (Where a copy ends depends on
        // the packet order, so only the start is exact.)
        var numbers = (await FramesAsync(clip)).Select(f => f.Number).ToList();
        Assert.Equal(Enumerable.Range(45, numbers.Count), numbers);
        Assert.InRange(numbers[^1], 88, 91);
    }

    [Fact]
    public async Task Playback_in_the_window_starts_with_the_frame_it_showed()
    {
        SkipUnlessInstalled();
        var video = await NumberedAsync("match.ts", "30", 4, Ts);
        var frames = await FramesAsync(video!);
        var edit = ClipEdit.Of(new ClipRange(frames[40].Time, frames[70].Time));

        int? first = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await ClipPreview.PlayAsync(video!, edit, Height, (pixels, width, height, _) =>
        {
            first ??= Number(pixels, 0, width, height, bytesPerPixel: 4);
            stop.Cancel();
        }, stop.Token);

        Assert.Equal(40, first);
    }

    [Fact]
    public async Task A_live_photo_of_an_mpeg_ts_file_shows_the_frame_its_times_point_at()
    {
        SkipUnlessInstalled();
        var video = await NumberedAsync("match.ts", "30", 4, Ts);
        var frames = await FramesAsync(video!);
        var preset = new Preset { TargetFormat = "jpg", Media = { LivePhoto = LivePhotoKind.Apple } };
        preset.Media.LivePhotoStart = frames[40].Time;
        preset.Media.LivePhotoLength = 1;
        preset.Media.LivePhotoCover = 10 / 30.0;

        var job = await fx.ConvertAsync([video!], preset);

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var photo = Assert.Single(job.Outputs, p => p.EndsWith(".jpg", StringComparison.Ordinal));
        var movie = Assert.Single(job.Outputs, p => p.EndsWith(".mov", StringComparison.Ordinal));
        Assert.Equal(50, await NumberOfPictureAsync(photo));
        Assert.Equal(Enumerable.Range(40, 30), (await FramesAsync(movie)).Select(f => f.Number));
    }
}
