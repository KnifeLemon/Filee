// "Ask every time" for the compression (#1353): which presets and files ask, the preset as it runs once the level is
// chosen, and the arithmetic behind the size the window shows.

using Filee.Core.Presets;

namespace Filee.Core.Tests;

public class AskedCompressionTests
{
    private static Preset BuiltIn(string id) => BuiltInData.CreatePresets().Single(p => p.Id == id);

    [Theory]
    [InlineData("compress-image")]
    [InlineData("compress-video")]
    [InlineData("compress-audio")]
    [InlineData("compress-pdf")]
    public void The_compress_presets_ask_in_a_new_library(string id) => Assert.True(AskedCompression.Asks(BuiltIn(id)));

    [Fact]
    public void Other_presets_and_a_new_preset_do_not_ask()
    {
        Assert.False(new Preset().AskCompression);
        Assert.All(BuiltInData.CreatePresets().Where(p => !p.Id.StartsWith("compress-", StringComparison.Ordinal)),
            p => Assert.False(p.AskCompression, p.Id));
    }

    [Fact]
    public void A_merge_a_group_or_a_tool_never_asks()
    {
        Assert.False(AskedCompression.Asks(new Preset { TargetFormat = "pdf", AskCompression = true, Pdf = { MergeIntoSingle = true } }));
        Assert.False(AskedCompression.Asks(new Preset { TargetFormat = "mp4", AskCompression = true, Media = { JoinIntoOne = true } }));
        Assert.False(AskedCompression.Asks(new Preset { Kind = PresetKind.Group, AskCompression = true }));
        Assert.False(AskedCompression.Asks(new Preset { Kind = PresetKind.Tool, AskCompression = true }));
        Assert.True(AskedCompression.Asks(new Preset { TargetFormat = "jpg", AskCompression = true }));
    }

    [Theory]
    [InlineData("compress-image", "photo.jpg", CompressionKind.Image)]
    [InlineData("compress-image", "photo.webp", CompressionKind.Image)]
    [InlineData("compress-image", "screen.png", null)]
    [InlineData("compress-image", "phone.heic", null)]
    [InlineData("compress-image", "scan.tiff", null)]
    [InlineData("compress-video", "clip.mov", CompressionKind.Video)]
    [InlineData("compress-video", "photo.jpg", null)]
    [InlineData("compress-audio", "song.wav", CompressionKind.Audio)]
    [InlineData("compress-audio", "clip.mp4", CompressionKind.Audio)]
    [InlineData("compress-pdf", "report.pdf", CompressionKind.Pdf)]
    [InlineData("compress-pdf", "letter.docx", null)]
    public void Each_file_gets_the_setting_that_makes_it_smaller(string preset, string file, CompressionKind? kind) =>
        Assert.Equal(kind, AskedCompression.KindOf(BuiltIn(preset), file));

    [Fact]
    public void A_png_converted_to_jpg_gets_a_quality()
    {
        var toJpg = new Preset { TargetFormat = "jpg", AskCompression = true };

        Assert.Equal(CompressionKind.Image, AskedCompression.KindOf(toJpg, "screen.png"));
        Assert.Equal([CompressionKind.Image, CompressionKind.Pdf],
            AskedCompression.KindsOf(new Preset { TargetFormat = BuiltInData.SameAsSource }, ["b.pdf", "a.jpg", "c.jpg", "notes.txt"]));
    }

    /// <summary>
    /// The level chosen for the JPGs goes to them only: a PNG or a HEIC dropped with them converts with the preset as it
    /// is, in a run of its own.
    /// </summary>
    [Fact]
    public void Files_without_a_level_keep_the_preset_as_it_is()
    {
        var preset = BuiltIn("compress-image");
        var choice = CompressionChoice.Of(preset) with { ImageQuality = 40 };

        var runs = AskedCompression.Runs(preset, choice, ["a.jpg", "phone.heic", "b.webp", "screen.png"]);

        Assert.Equal(2, runs.Count);
        Assert.Equal(["a.jpg", "b.webp"], runs[0].Files);
        Assert.Equal((40, false), (runs[0].Preset.Image.Quality, runs[0].Preset.AskCompression));
        Assert.Equal(["phone.heic", "screen.png"], runs[1].Files);
        Assert.Same(preset, runs[1].Preset);
        Assert.Single(AskedCompression.Runs(preset, choice, ["a.jpg"]));
    }

    /// <summary>
    /// "Ask every time" for the place (#521): asked once for the whole drop, that place goes to every run, so a JPG and a
    /// PNG dropped together are saved side by side and neither run asks again.
    /// </summary>
    [Fact]
    public void One_save_location_goes_to_every_run()
    {
        var preset = BuiltIn("compress-image");
        preset.Output.AskEveryTime = true;
        var located = AskedLocation.ForFolder(preset, Path.Combine(Path.GetTempPath(), "picked"));

        var runs = AskedCompression.Runs(preset, CompressionChoice.Of(preset) with { ImageQuality = 40 }, ["a.jpg", "screen.png"], located.Output);

        Assert.Equal(2, runs.Count);
        Assert.All(runs, run =>
        {
            Assert.Equal((OutputLocation.CustomFolder, located.Output.CustomFolder, false),
                (run.Preset.Output.Location, run.Preset.Output.CustomFolder, run.Preset.Output.AskEveryTime));
            Assert.NotSame(located.Output, run.Preset.Output);
        });
        Assert.Equal((40, 75), (runs[0].Preset.Image.Quality, runs[1].Preset.Image.Quality));
        // The preset itself still asks, and its own place is untouched.
        Assert.NotSame(preset, runs[1].Preset);
        Assert.True(preset.Output.AskEveryTime);
        Assert.NotEqual(OutputLocation.CustomFolder, preset.Output.Location);
    }

    [Fact]
    public void The_choice_is_applied_to_a_copy_that_asks_no_more()
    {
        var preset = BuiltIn("compress-video");

        var asked = AskedCompression.Apply(preset, new CompressionChoice(55, MediaQuality.High, 96, PdfCompression.Screen));

        Assert.NotSame(preset, asked);
        Assert.Equal((55, MediaQuality.High, 96, PdfCompression.Screen, false),
            (asked.Image.Quality, asked.Media.Quality, asked.Media.AudioBitrateKbps, asked.Pdf.Compression, asked.AskCompression));
        // The saved preset is left as it was, and the rest of the copy is the preset's.
        Assert.Equal((MediaQuality.Small, true), (preset.Media.Quality, preset.AskCompression));
        Assert.Equal(1080, asked.Media.MaxHeight);
        Assert.Equal(CompressionChoice.Of(preset) with { VideoQuality = MediaQuality.High },
            CompressionChoice.Of(AskedCompression.Apply(preset, CompressionChoice.Of(preset) with { VideoQuality = MediaQuality.High })));
    }

    [Fact]
    public void Asking_survives_a_clone() => Assert.True(BuiltIn("compress-pdf").Clone().AskCompression);

    [Fact]
    public void Media_bytes_are_bitrate_times_length_with_room_for_the_container()
    {
        // 100 s at 128 kbit/s: 1.6 MB of sound, + 2 %.
        Assert.Equal(1_632_000, AskedCompression.MediaBytes(100, 128));
        Assert.Equal(0, AskedCompression.MediaBytes(0, 128));
    }

    [Fact]
    public void The_result_keeps_to_the_limit_and_to_the_original()
    {
        Assert.Equal(30_000_000, AskedCompression.Result(100_000_000, 30_000_000, 0, onlyIfSmaller: true));
        Assert.Equal(10_000_000, AskedCompression.Result(100_000_000, 30_000_000, 10_000_000, onlyIfSmaller: true));
        // Bigger than the original: kept as it is with "only keep if smaller", written as it is without.
        Assert.Equal(100_000_000, AskedCompression.Result(100_000_000, 120_000_000, 0, onlyIfSmaller: true));
        Assert.Equal(120_000_000, AskedCompression.Result(100_000_000, 120_000_000, 0, onlyIfSmaller: false));
    }

    [Fact]
    public void Sizes_and_savings_read_as_filee_shows_them()
    {
        Assert.Equal(70, AskedCompression.PercentSmaller(100_000_000, 30_000_000));
        Assert.Equal(0, AskedCompression.PercentSmaller(100_000_000, 120_000_000));
        Assert.Equal("850 KB", AskedCompression.SizeText(850_000));
        Assert.Equal("30.0 MB", AskedCompression.SizeText(30_000_000));
        Assert.Equal("123 MB", AskedCompression.SizeText(123_400_000));
        Assert.Equal("1.2 GB", AskedCompression.SizeText(1_200_000_000));
    }
}
