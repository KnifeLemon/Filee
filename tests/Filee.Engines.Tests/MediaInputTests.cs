// What FFmpeg is given for each file (MediaInput), and the actions besides a plain conversion that go through it: Merge
// videos, Live Photo, the Cut a clip editor's probe. The FFmpeg-backed tests run only with FFmpeg installed.

using System.Globalization;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Media;

namespace Filee.Engines.Tests;

[Collection(nameof(FfmpegCollection))]
public class MediaInputTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly byte[] SdCardStream =
        [.. HikvisionRecording.PackHeader(90_000), .. HikvisionRecording.StreamMap((0x1B, 0xE0), (0x91, 0xC0))];

    private static byte[] Export(params int[] recordingSizes) => NovFile.Write(
    [
        ("uuid.bin", "{5b3c0f8e-0000-4000-8000-000000000001}"u8.ToArray()),
        .. recordingSizes.Select((size, i) => ($"e3e9a385_{1791212400000 + i * 60_000}.mkv", new byte[size])),
    ]);

    [Fact]
    public void Each_file_is_given_to_ffmpeg_the_way_it_can_read_it()
    {
        var folder = fx.NewFolder();
        var work = Path.Combine(folder, "work");
        var export = Path.Combine(folder, "Parking lot.nov");
        File.WriteAllBytes(export, Export(500));
        var sdCard = Path.Combine(folder, "hiv00000.mp4");
        File.WriteAllBytes(sdCard, SdCardStream);
        // The same bytes under a sound file's name aren't looked into.
        var sound = Path.Combine(folder, "voice.mp3");
        File.WriteAllBytes(sound, SdCardStream);
        var plain = Path.Combine(folder, "clip.mp4");
        File.WriteAllBytes(plain, [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', .. new byte[64]]);

        Assert.StartsWith("subfile,,start,", MediaInput.For(export), StringComparison.Ordinal);
        Assert.Equal($"concat:{Path.Combine(work, "hikvision-imkh.bin")}|{sdCard}", MediaInput.For(sdCard, "mp4", work));
        Assert.StartsWith("concat:", MediaInput.For(sdCard), StringComparison.Ordinal); // header in the temp folder
        Assert.Equal(sound, MediaInput.For(sound, workDirectory: work));
        Assert.Equal(plain, MediaInput.For(plain, workDirectory: work));
        Assert.True(MediaInput.IsUrl(MediaInput.For(export)));
        Assert.False(MediaInput.IsUrl(plain));
    }

    [Fact]
    public void A_nov_export_with_other_recordings_says_they_are_left_out()
    {
        var folder = fx.NewFolder();
        var several = Path.Combine(folder, "two cameras.nov");
        File.WriteAllBytes(several, Export(300, 900));
        var single = Path.Combine(folder, "one camera.nov");
        File.WriteAllBytes(single, Export(900));
        var notices = new List<string>();

        MediaInput.For(several, notice: notices.Add);
        MediaInput.For(single, notice: notices.Add);

        Assert.Equal(["notice.nov_recordings_left_out"], notices);
    }

    [Fact]
    public void The_merge_list_keeps_protocol_inputs_as_they_are()
    {
        var input = "subfile,,start,10,end,20,,:" + Path.GetFullPath("it's.nov");
        Assert.Equal("file '" + input.Replace("'", @"'\''", StringComparison.Ordinal) + "'\n", MediaJoining.ConcatList([input]));
    }

    [Fact]
    public async Task The_cut_a_clip_editor_probes_what_is_inside_a_nov_export_and_an_sd_card_recording()
    {
        SkipUnlessInstalled();
        var export = await MakeExportAsync("Parking lot.nov", 2);
        var sdCard = await MakeSdCardRecordingAsync();

        var inside = await VideoFrames.ProbeAsync(export, TestContext.Current.CancellationToken);
        var hikvision = await VideoFrames.ProbeAsync(sdCard, TestContext.Current.CancellationToken);

        Assert.InRange(inside.Duration, 1.7, 2.4);
        Assert.InRange(hikvision.Duration, 1.7, 2.4);
        var frame = Path.Combine(fx.NewFolder(), "frame.jpg");
        Assert.True(await VideoFrames.WriteFrameAsync(export, 1, frame, 120, TestContext.Current.CancellationToken));
        Assert.True(new FileInfo(frame).Length > 0);
    }

    [Fact]
    public async Task Merge_videos_reads_nov_exports_and_sd_card_recordings()
    {
        SkipUnlessInstalled();
        var first = await MakeExportAsync("first.nov", 2);
        var second = await MakeExportAsync("second.nov", 1);
        var sdCard = await MakeSdCardRecordingAsync();
        var merge = BuiltInData.CreatePresets().Single(p => p.Id == "merge-videos");

        // Two exports alike are copied one after the other (the concat demuxer), with the SD card they are encoded.
        var copied = await fx.ConvertAsync([first, second], merge);
        var encoded = await fx.ConvertAsync([first, sdCard], merge);

        Assert.InRange(await DurationAsync(Completed(copied)), 2.7, 3.4);
        Assert.InRange(await DurationAsync(Completed(encoded)), 3.7, 4.4);
    }

    [Fact]
    public async Task A_live_photo_is_made_from_a_nov_export()
    {
        SkipUnlessInstalled();
        var export = await MakeExportAsync("Parking lot.nov", 2);
        var preset = new Preset { TargetFormat = "jpg", Media = { LivePhoto = LivePhotoKind.Apple } };

        var job = await fx.ConvertAsync([export], preset);

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        Assert.NotEmpty(job.Outputs);
    }

    [Fact]
    public async Task Converting_a_nov_export_of_two_recordings_tells_the_user()
    {
        SkipUnlessInstalled();
        var export = await MakeExportAsync("two pieces.nov", 1, 2);

        var job = await fx.ConvertAsync([export], new Preset { TargetFormat = "mp4" });

        Completed(job);
        Assert.Equal("notice.nov_recordings_left_out", job.Files.Single().NoticeKey);
        Assert.InRange(await DurationAsync(job.Outputs.Single()), 1.7, 2.4);
    }

    private static (string Ffmpeg, string Ffprobe) Tools()
    {
        var tools = FfmpegConverter.Locate();
        Assert.SkipUnless(tools is not null, "FFmpeg is not installed.");
        return tools.Value;
    }

    private static void SkipUnlessInstalled() => Tools();

    private static string Completed(ConversionJob job)
    {
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return job.Outputs.Single();
    }

    private static async Task<double> DurationAsync(string path)
    {
        var probe = await ProcessRunner.RunAsync(Tools().Ffprobe, ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path],
            TimeSpan.FromMinutes(1), CancellationToken.None);
        return double.Parse(probe.StandardOutput.Trim(), CultureInfo.InvariantCulture);
    }

    private async Task<string> RunFfmpegAsync(string output, params string[] args)
    {
        var result = await FfmpegRunner.RunAsync(Tools().Ffmpeg, [.. MediaEncoding.Common, .. args, output], null,
            TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return output;
    }

    /// <summary>A WAVE export holding one Matroska recording per entry of <paramref name="seconds"/>, 160x120 at 25 fps.</summary>
    private async Task<string> MakeExportAsync(string name, params int[] seconds)
    {
        var folder = fx.NewFolder();
        var streams = new List<(string, byte[])> { ("uuid.bin", "{5b3c0f8e-0000-4000-8000-000000000001}"u8.ToArray()) };
        for (var i = 0; i < seconds.Length; i++)
        {
            var length = seconds[i].ToString(CultureInfo.InvariantCulture);
            var clip = await RunFfmpegAsync(Path.Combine(folder, $"part{i}.mkv"),
                "-f", "lavfi", "-i", $"testsrc2=size=160x120:rate=25:duration={length}",
                "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=44100:duration={length}",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac");
            streams.Add(($"e3e9a385_{1791212400000 + i * 60_000}.mkv", await File.ReadAllBytesAsync(clip, TestContext.Current.CancellationToken)));
        }
        var export = Path.Combine(folder, name);
        await File.WriteAllBytesAsync(export, NovFile.Write(streams), TestContext.Current.CancellationToken);
        return export;
    }

    /// <summary>A 2 s Hikvision SD-card recording (no IMKH header) with mu-law sound.</summary>
    private async Task<string> MakeSdCardRecordingAsync()
    {
        var folder = fx.NewFolder();
        var h264 = await RunFfmpegAsync(Path.Combine(folder, "camera.h264"),
            "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=25:duration=2", "-c:v", "libx264", "-g", "25", "-pix_fmt", "yuv420p",
            "-bsf:v", "h264_metadata=aud=insert");
        var g711 = await RunFfmpegAsync(Path.Combine(folder, "camera.ulaw"),
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=8000:duration=2", "-ac", "1", "-f", "mulaw");
        var recording = Path.Combine(folder, "hiv00000.mp4");
        await File.WriteAllBytesAsync(recording, HikvisionRecording.Write(
            await File.ReadAllBytesAsync(h264, TestContext.Current.CancellationToken),
            await File.ReadAllBytesAsync(g711, TestContext.Current.CancellationToken), muLaw: true, imkhHeader: false),
            TestContext.Current.CancellationToken);
        return recording;
    }
}
