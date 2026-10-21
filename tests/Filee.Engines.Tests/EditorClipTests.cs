// The editor's clips with FFmpeg: the command lines (frame, transitions, texts, the graphics chip, a size limit; no
// FFmpeg needed), and real clips written through the job queue when Filee's FFmpeg is installed: videos of different
// shapes joined with a crossfade into a vertical frame, played backwards, held and slowed down; videos joined by
// copying; highlights as separate files; a size limit kept; a GIF; and the preview.

using System.Globalization;
using System.Text.Json;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Media;

namespace Filee.Engines.Tests;

[Collection(nameof(FfmpegCollection))]
public class EditorClipTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly (string Ffmpeg, string Ffprobe)? Tools = FfmpegConverter.Locate();

    private static void SkipUnlessInstalled() =>
        Assert.SkipUnless(Tools is not null, "FFmpeg is not installed (pwsh build/fetch-engines.ps1 -Only ffmpeg)");

    private static readonly MediaInfo Wide = new(TimeSpan.FromSeconds(60), new VideoStreamInfo(1920, 1080, 1, 0, 30, "h264", "yuv420p"),
        new AudioStreamInfo(2, 48000, "aac"), null);

    private static readonly MediaInfo Tall = new(TimeSpan.FromSeconds(20), new VideoStreamInfo(1080, 1920, 1, 0, 60, "hevc", "yuv420p"), null, null);

    private static EditorSource SourceOf(string? path) => path is null ? new EditorSource("main.mp4", Wide) : new EditorSource(path, Tall);

    private static string After(IReadOnlyList<string> args, string option) => args[args.ToList().IndexOf(option) + 1];

    private static ClipEdit Story() => new()
    {
        Editor = true,
        Aspect = "9:16",
        Resolution = 1080,
        Fit = ClipFit.Blur,
        Parts =
        [
            new() { Start = 10, End = 15, Transition = ClipTransitionKind.Crossfade, TransitionSeconds = 1 },
            new() { Source = "tall.mp4", Start = 0, End = 4, Reverse = true, Transition = ClipTransitionKind.Black },
            new() { Start = 30, End = 30, Hold = 2 },
            new() { Start = 40, End = 50, Speed = 1, SpeedEnd = 0.5 },
        ],
        Texts = [ClipText.Of(ClipTextStyle.Caption, "Goal!", 1)],
    };

    [Fact]
    public void The_editors_graph_reads_each_part_and_joins_them_with_its_transitions()
    {
        var edit = Story().Clamped(source => SourceOf(source).Info.Duration?.TotalSeconds);
        var folder = Path.Combine(Path.GetTempPath(), "filee-editor-args-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            var args = MediaEncoding.EditorArguments(edit, SourceOf, "out.mp4", "mp4", new MediaOptions(), null, null, folder);
            var graph = After(args, "-filter_complex");
            // One input per part, a part played backwards in two-second chunks (last first), a still from one second.
            Assert.Equal(["main.mp4", "tall.mp4", "tall.mp4", "main.mp4", "main.mp4"], args.Where((a, i) => i > 0 && args[i - 1] == "-i"));
            Assert.Contains("reverse", graph);
            Assert.DoesNotContain("areverse", graph); // the tall video has no sound to turn round
            Assert.Contains("tpad=stop_mode=clone", graph);
            // The 9:16 frame at 1080 wide, the wide video over a blurred copy, the tall one filling it.
            Assert.Contains("scale=1080:608", graph);
            Assert.Contains("boxblur", graph);
            Assert.Contains("overlay=0:656", graph);
            // A crossfade and a fade through black, starting where the next part comes in.
            Assert.Contains("xfade=transition=fade:duration=1:offset=4", graph);
            Assert.Contains("xfade=transition=fadeblack:duration=0.5:offset=7.5", graph);
            Assert.Contains("acrossfade=d=1", graph);
            // The slowing part in steps.
            Assert.Contains($"split={ClipPart.RampSteps}", graph);
            // The caption from its own file, in a bold font when the system has one.
            Assert.Contains("drawtext=", graph);
            Assert.Contains("box=1", graph);
            Assert.Equal("Goal!", File.ReadAllText(Path.Combine(folder, "clip-text-0.txt")));
            Assert.Equal("libx264", After(args, "-c:v"));
            Assert.Equal("aac", After(args, "-c:a"));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void A_size_limit_turns_constant_quality_into_a_bitrate_that_fits()
    {
        // 25 MB for a minute: about 3.3 Mbit/s for everything, the sound's 128 kbit/s off.
        Assert.Equal(3157, MediaEncoding.SizeLimitKbps(25, 60, 128));
        var software = MediaEncoding.LimitRate(["-c:v", "libx264", "-preset", "medium", "-crf", "23", "-pix_fmt", "yuv420p"], 3000, null);
        Assert.DoesNotContain("-crf", software);
        Assert.Equal(["-c:v", "libx264", "-preset", "medium", "-pix_fmt", "yuv420p", "-b:v", "3000k", "-maxrate", "3750k", "-bufsize", "6000k"], software);
        var nvenc = MediaEncoding.LimitRate(["-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "24", "-b:v", "0"], 3000, HardwareApi.Nvenc);
        Assert.Equal(["-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-b:v", "3000k", "-maxrate", "3750k", "-bufsize", "6000k"], nvenc);
    }

    [Fact]
    public void The_graphics_chip_encodes_the_editors_clip_when_given()
    {
        var edit = new ClipEdit { Editor = true, Parts = [new() { Start = 0, End = 5 }], MaxMegabytes = 8 }.Clamped(_ => 60);
        var args = MediaEncoding.EditorArguments(edit, SourceOf, "out.mp4", "mp4", new MediaOptions(), null, new HardwareEncoder("h264_nvenc", HardwareApi.Nvenc),
            Path.GetTempPath());
        Assert.Equal("h264_nvenc", After(args, "-c:v"));
        Assert.Contains("-maxrate", args);
        Assert.DoesNotContain("-cq", args);
        Assert.Equal("128k", After(args, "-b:a"));
    }

    [Fact]
    public void Paths_reach_drawtext_whole()
    {
        Assert.Equal(@"C\\:/Users/it\\\'s/a\,b.txt", MediaEncoding.FilterPath(@"C:\Users\it's\a,b.txt"));
    }

    [Fact]
    public void Only_whole_videos_of_one_format_are_joined_by_copying()
    {
        var a = new EditorSource("a.mp4", Wide);
        var join = new ClipEdit { Editor = true, Parts = [new() { Source = "a.mp4", End = 60 }, new() { Source = "b.mp4", End = 60 }] };
        Assert.True(MediaEncoding.CanJoinByCopy(join, _ => a, "mp4", new MediaOptions()));
        Assert.False(MediaEncoding.CanJoinByCopy(join, p => p == "b.mp4" ? new EditorSource("b.mp4", Tall with { Duration = TimeSpan.FromSeconds(60) }) : a, "mp4",
            new MediaOptions())); // another size and codec
        join.Parts[1].End = 30;
        Assert.False(MediaEncoding.CanJoinByCopy(join, _ => a, "mp4", new MediaOptions())); // not the whole video
        join.Parts[1].End = 60;
        join.Texts.Add(ClipText.Of(ClipTextStyle.Title, "Hi", 0));
        Assert.False(MediaEncoding.CanJoinByCopy(join, _ => a, "mp4", new MediaOptions()));
    }

    // ───────────────────────── Real clips ─────────────────────────

    /// <summary>A test video: a moving pattern of <paramref name="size"/>, with a tone unless <paramref name="silent"/>.</summary>
    private static async Task<string> MakeVideoAsync(string folder, string name, string size = "320x240", double seconds = 6, bool silent = false)
    {
        var path = Path.Combine(folder, name);
        List<string> args = ["-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", $"testsrc2=size={size}:rate=25:duration={seconds}"];
        if (!silent)
            args.AddRange(["-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=48000:duration={seconds}", "-ac", "2", "-c:a", "aac"]);
        args.AddRange(["-c:v", "libx264", "-g", "25", "-pix_fmt", "yuv420p", path]);
        var result = await ProcessRunner.RunAsync(Tools!.Value.Ffmpeg, args, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return path;
    }

    private sealed record Probed(double Duration, int Width, int Height, string? VideoCodec, string? AudioCodec, long Size);

    private static async Task<Probed> ProbeAsync(string path)
    {
        var result = await ProcessRunner.RunAsync(Tools!.Value.Ffprobe,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path], TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.True(result.ExitCode == 0, $"ffprobe {path}: {result.StandardError}");
        using var json = JsonDocument.Parse(result.StandardOutput);
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToList();
        string? Codec(string type) => streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == type) is { ValueKind: JsonValueKind.Object } s
            ? s.GetProperty("codec_name").GetString() : null;
        var video = streams.First(s => s.GetProperty("codec_type").GetString() == "video");
        var duration = double.Parse(json.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        return new Probed(duration, video.GetProperty("width").GetInt32(), video.GetProperty("height").GetInt32(), Codec("video"), Codec("audio"),
            new FileInfo(path).Length);
    }

    private async Task<IReadOnlyList<string>> WriteAsync(string source, ClipEdit edit, string target = "mp4", Action<Preset>? change = null)
    {
        var tool = BuiltInData.CreatePresets().Single(p => p.Id == "tool-clip");
        var conversion = ClipTool.ConversionOf(tool, target, edit);
        change?.Invoke(conversion);
        var job = await fx.ConvertAsync([source], conversion);
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return [.. job.Outputs];
    }

    [Fact]
    public async Task Videos_of_different_shapes_become_one_vertical_clip()
    {
        SkipUnlessInstalled();
        var folder = fx.NewFolder();
        var wide = await MakeVideoAsync(folder, "wide.mp4");
        var tall = await MakeVideoAsync(folder, "tall.mp4", "240x320", silent: true);
        var edit = new ClipEdit
        {
            Editor = true,
            Aspect = "9:16",
            Resolution = 360,
            Fit = ClipFit.Blur,
            Parts =
            [
                new() { Start = 1, End = 4, Transition = ClipTransitionKind.Crossfade, TransitionSeconds = 0.5 },
                new() { Source = tall, Start = 0, End = 3, Reverse = true },
                new() { Start = 2, End = 2, Hold = 1, Zoom = 2 },
                new() { Start = 4, End = 6, Speed = 1, SpeedEnd = 0.5, Transition = ClipTransitionKind.Black },
            ],
            Texts = [ClipText.Of(ClipTextStyle.Title, "Привет", 0.5, 2)],
            FadeOut = 0.5,
            LevelLoudness = true,
            ReduceNoise = true,
            LiftSpeech = true,
        };
        var expected = edit.Clamped(_ => 6).Length;

        var output = Assert.Single(await WriteAsync(wide, edit));

        var probed = await ProbeAsync(output);
        Assert.Equal((360, 640), (probed.Width, probed.Height));
        Assert.Equal(("h264", "aac"), (probed.VideoCodec, probed.AudioCodec));
        Assert.InRange(probed.Duration, expected - 0.15, expected + 0.15);
    }

    [Fact]
    public async Task Turned_moved_pictures_layers_and_several_sounds_are_written()
    {
        SkipUnlessInstalled();
        var folder = fx.NewFolder();
        var wide = await MakeVideoAsync(folder, "wide.mp4");
        var logo = Path.Combine(folder, "logo.png");
        var made = await ProcessRunner.RunAsync(Tools!.Value.Ffmpeg,
            ["-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=red@0.5:s=360x640,format=rgba", "-frames:v", "1", logo],
            TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.True(made.ExitCode == 0, made.StandardError);
        var one = Path.Combine(folder, "one.wav");
        var two = Path.Combine(folder, "two.wav");
        foreach (var (file, tone) in new[] { (one, 330), (two, 550) })
        {
            var sound = await ProcessRunner.RunAsync(Tools.Value.Ffmpeg,
                ["-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", $"sine=frequency={tone}:duration=3", "-ac", "2", file],
                TimeSpan.FromMinutes(1), CancellationToken.None);
            Assert.True(sound.ExitCode == 0, sound.StandardError);
        }
        var edit = new ClipEdit
        {
            Editor = true,
            Aspect = "9:16",
            Resolution = 360,
            Fit = ClipFit.Blur,
            Parts =
            [
                new() { Start = 0, End = 2, Rotation = 30, Zoom = 0.8 },
                new() { Start = 2, End = 4, FocusX = 0.2, Zoom = 0.6, Reverse = true },
            ],
            Images = [new() { Path = logo, Rendered = logo, Start = 0.5, Length = 2 }],
            Texts = [new() { Text = "drawn already", Rendered = logo, Start = 1, Length = 2 }],
            Sounds =
            [
                new() { Path = one, Length = 3, Offset = 0 },
                new() { Path = two, Length = 3, Offset = 1, Track = 1, Play = 1.5 },
            ],
        };

        var output = Assert.Single(await WriteAsync(wide, edit));

        var probed = await ProbeAsync(output);
        Assert.Equal((360, 640), (probed.Width, probed.Height));
        Assert.Equal(("h264", "aac"), (probed.VideoCodec, probed.AudioCodec));
        Assert.InRange(probed.Duration, 3.85, 4.15);
    }

    [Fact]
    public void Layers_and_turned_parts_in_the_graph()
    {
        var edit = new ClipEdit
        {
            Editor = true,
            Parts = [new() { Start = 0, End = 5, Rotation = -90 }],
            Texts = [new() { Text = "Hi", Rendered = "hi.png", Start = 1, Length = 2 }],
            Sounds = [new() { Path = "a.mp3", Length = 9 }, new() { Path = "b.mp3", Length = 9, Track = 1 }],
        }.Clamped(_ => 60);
        var args = MediaEncoding.EditorArguments(edit, SourceOf, "out.mp4", "mp4", new MediaOptions(), null, null, Path.GetTempPath());
        var graph = After(args, "-filter_complex");
        Assert.Contains("rotate=-1.571", graph);
        Assert.Contains("overlay=0:0:enable='between(t,1,3)'", graph);
        Assert.DoesNotContain("drawtext", graph); // the editor drew the text
        Assert.Contains("amix=inputs=2:duration=longest", graph);
        Assert.Contains("hi.png", args);
    }

    [Fact]
    public async Task Whole_videos_of_one_format_are_joined_by_copying()
    {
        SkipUnlessInstalled();
        var folder = fx.NewFolder();
        var first = await MakeVideoAsync(folder, "one.mp4", seconds: 4);
        var second = await MakeVideoAsync(folder, "two.mp4", seconds: 3);
        var edit = new ClipEdit { Editor = true, Parts = [new() { End = 4 }, new() { Source = second, End = 3 }] };

        var output = Assert.Single(await WriteAsync(first, edit));

        var probed = await ProbeAsync(output);
        Assert.Equal((320, 240), (probed.Width, probed.Height));
        Assert.InRange(probed.Duration, 6.9, 7.15);
    }

    [Fact]
    public async Task Highlights_become_a_file_each_and_a_size_limit_is_kept()
    {
        SkipUnlessInstalled();
        var folder = fx.NewFolder();
        var source = await MakeVideoAsync(folder, "match.mp4", "640x360", seconds: 8);
        var edit = new ClipEdit
        {
            Editor = true,
            Separate = true,
            MaxMegabytes = 0.25,
            Parts = [new() { Start = 0, End = 3 }, ClipProject.Pause(1), new() { Start = 5, End = 8, Speed = 2 }],
        };

        var outputs = await WriteAsync(source, edit, change: c => c.Media.Quality = MediaQuality.High);

        Assert.Equal(2, outputs.Count);
        Assert.EndsWith("_1.mp4", outputs[0]);
        Assert.EndsWith("_2.mp4", outputs[1]);
        var first = await ProbeAsync(outputs[0]);
        var second = await ProbeAsync(outputs[1]);
        Assert.InRange(first.Duration, 2.9, 3.15);
        Assert.InRange(second.Duration, 1.4, 1.65);
        Assert.True(first.Size <= 0.25 * 1024 * 1024, $"{first.Size} bytes");
    }

    [Fact]
    public async Task An_editor_clip_becomes_a_gif()
    {
        SkipUnlessInstalled();
        var source = await MakeVideoAsync(fx.NewFolder(), "match.mp4", "1280x720", seconds: 3);
        var edit = new ClipEdit { Editor = true, Parts = [new() { Start = 0, End = 2 }] };

        var output = Assert.Single(await WriteAsync(source, edit, "gif"));

        Assert.EndsWith(".gif", output);
        var probed = await ProbeAsync(output);
        Assert.Equal((480, 270), (probed.Width, probed.Height));
    }

    [Fact]
    public async Task The_editor_plays_its_clip_in_its_frame()
    {
        SkipUnlessInstalled();
        var folder = fx.NewFolder();
        var source = await MakeVideoAsync(folder, "match.mp4", seconds: 3);
        var tall = await MakeVideoAsync(folder, "tall.mp4", "240x320", seconds: 2, silent: true);
        var edit = new ClipEdit { Editor = true, Aspect = "1:1", Parts = [new() { Start = 0, End = 1.5 }, new() { Source = tall, Start = 0, End = 1 }] };
        ClipPreview.PlayerVolume = 0;
        try
        {
            var frames = 0;
            var size = (0, 0);
            await ClipPreview.PlayEditorAsync(source, edit, 400, 200, (_, width, height, _) =>
            {
                frames++;
                size = (width, height);
            }, CancellationToken.None);
            Assert.Equal((200, 200), size);
            Assert.InRange(frames, 50, 64);
        }
        finally
        {
            ClipPreview.PlayerVolume = null;
        }
    }
}
