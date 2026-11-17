// The size the compression window estimates (#1353), held against what the conversion really writes: pictures from a
// tile encoded at the chosen quality, PDFs from the compressor itself, audio and video from bitrate × length. The
// picture and PDF tests run in-process; the audio and video ones only when FFmpeg is installed
// (`pwsh build/fetch-engines.ps1 -Only ffmpeg`), on inputs FFmpeg generates itself.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Media;
using ImageMagick;

namespace Filee.Engines.Tests;

[Collection(nameof(FfmpegCollection))]
public class CompressionEstimatorTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly (string Ffmpeg, string Ffprobe)? Tools = FfmpegConverter.Locate();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Preset BuiltIn(string id) => BuiltInData.CreatePresets().Single(p => p.Id == id);

    private static CompressionChoice With(Preset preset, int? quality = null, MediaQuality? video = null, int? audioKbps = null,
        PdfCompression? pdf = null)
    {
        var choice = CompressionChoice.Of(preset);
        return choice with
        {
            ImageQuality = quality ?? choice.ImageQuality,
            VideoQuality = video ?? choice.VideoQuality,
            AudioBitrateKbps = audioKbps ?? choice.AudioBitrateKbps,
            Pdf = pdf ?? choice.Pdf,
        };
    }

    /// <summary>A photo-like JPEG: a gradient with noise, so the quality changes its size a lot.</summary>
    private string Photo(uint width = 3000, uint height = 2000, uint quality = 95, string name = "photo.jpg")
    {
        using var image = new MagickImage("gradient:#C04020-#2060C0", width, height);
        image.AddNoise(NoiseType.Gaussian, 0.4);
        image.Quality = quality;
        var path = Path.Combine(fx.NewFolder(), name);
        image.Write(path, MagickFormat.Jpeg);
        return path;
    }

    private static long Written(ConversionJob job)
    {
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return new FileInfo(Assert.Single(job.Outputs)).Length;
    }

    [Fact]
    public async Task A_photo_is_estimated_close_to_what_compress_image_writes()
    {
        var source = Photo();
        var preset = BuiltIn("compress-image");
        using var estimator = await CompressionEstimator.ReadAsync([source], preset, cancellationToken: Ct);

        Assert.Equal(1, estimator.FileCount);
        Assert.Equal(new FileInfo(source).Length, estimator.SourceBytes);
        foreach (var quality in new[] { 40, 75 })
        {
            var estimate = Assert.NotNull(await estimator.EstimateAsync(With(preset, quality), Ct));
            var written = Written(await fx.ConvertAsync([source], AskedCompression.Apply(preset, With(preset, quality))));
            // An estimate: within a quarter of the real size for an even photo like this one.
            Assert.InRange(estimate, written * 0.75, written * 1.25);
        }
    }

    [Fact]
    public async Task A_lower_quality_is_estimated_smaller_and_never_above_the_original()
    {
        var source = Photo(1800, 1200);
        var preset = BuiltIn("compress-image");
        using var estimator = await CompressionEstimator.ReadAsync([source], preset, cancellationToken: Ct);

        var low = await estimator.EstimateAsync(With(preset, 30), Ct);
        var middle = await estimator.EstimateAsync(With(preset, 70), Ct);
        var high = await estimator.EstimateAsync(With(preset, 100), Ct);

        Assert.True(low < middle, $"{low} vs {middle}");
        // Quality 100 would be larger than the photo saved at 95: "Compress image" never raises a JPEG's quality, and
        // only keeps a smaller result.
        Assert.InRange(high!.Value, middle!.Value, estimator.SourceBytes);
    }

    [Fact]
    public async Task The_size_limit_of_the_preset_caps_the_estimate()
    {
        var source = Photo();
        var preset = BuiltIn("compress-image");
        preset.Image.MaxSizeMb = 0.2;
        using var estimator = await CompressionEstimator.ReadAsync([source], preset, cancellationToken: Ct);

        Assert.Equal(200_000, await estimator.EstimateAsync(With(preset, 95), Ct));
    }

    [Fact]
    public async Task Pictures_past_the_first_ones_count_at_their_ratio()
    {
        var preset = BuiltIn("compress-image");
        var files = Enumerable.Range(0, CompressionEstimator.SampledFiles(CompressionKind.Image) + 3)
            .Select(i => Photo(800, 600, name: $"photo{i}.jpg")).ToList();
        using var all = await CompressionEstimator.ReadAsync(files, preset, cancellationToken: Ct);
        using var one = await CompressionEstimator.ReadAsync([files[0]], preset, cancellationToken: Ct);

        var each = (double)(await one.EstimateAsync(With(preset, 60), Ct))!.Value / one.SourceBytes;
        var total = (double)(await all.EstimateAsync(With(preset, 60), Ct))!.Value / all.SourceBytes;

        Assert.Equal(files.Count, all.FileCount);
        Assert.Equal(each, total, 0.05);
    }

    [Fact]
    public async Task Files_without_a_level_to_choose_are_left_out()
    {
        var png = Path.Combine(fx.NewFolder(), "flat.png");
        using (var image = new MagickImage(MagickColors.Teal, 200, 200))
            image.Write(png, MagickFormat.Png);
        var text = Path.Combine(fx.NewFolder(), "notes.txt");
        await File.WriteAllTextAsync(text, "nothing to compress", Ct);
        var preset = BuiltIn("compress-image");

        using var estimator = await CompressionEstimator.ReadAsync([png, text], preset, cancellationToken: Ct);

        Assert.Equal(0, estimator.FileCount);
        Assert.Null(await estimator.EstimateAsync(CompressionChoice.Of(preset), Ct));
    }

    [Fact]
    public async Task A_damaged_picture_gives_no_estimate()
    {
        var broken = Path.Combine(fx.NewFolder(), "broken.jpg");
        await File.WriteAllBytesAsync(broken, [0xFF, 0xD8, 0xFF, 0x00, 0x01, 0x02], Ct);
        var preset = BuiltIn("compress-image");

        using var estimator = await CompressionEstimator.ReadAsync([broken], preset, cancellationToken: Ct);

        Assert.Equal(1, estimator.FileCount);
        Assert.Null(await estimator.EstimateAsync(CompressionChoice.Of(preset), Ct));
    }

    /// <summary>
    /// A file that can't be estimated next to one that can counts at the other's ratio: left out, the total would read
    /// too small next to the size now, and "% smaller" too large.
    /// </summary>
    [Fact]
    public async Task A_file_that_cannot_be_estimated_counts_at_the_ratio_of_the_others()
    {
        var good = Photo(1600, 1200);
        var broken = Path.Combine(fx.NewFolder(), "broken.jpg");
        await File.WriteAllBytesAsync(broken, [0xFF, 0xD8, 0xFF, .. new byte[400_000]], Ct);
        var preset = BuiltIn("compress-image");
        using var alone = await CompressionEstimator.ReadAsync([good], preset, cancellationToken: Ct);
        using var both = await CompressionEstimator.ReadAsync([good, broken], preset, cancellationToken: Ct);

        var one = (await alone.EstimateAsync(With(preset, 50), Ct))!.Value;
        var total = (await both.EstimateAsync(With(preset, 50), Ct))!.Value;

        Assert.Equal(alone.SourceBytes + 400_003, both.SourceBytes);
        var ratio = (double)one / alone.SourceBytes;
        Assert.Equal(one + 400_003 * ratio, total, 1.0);
        Assert.Equal(ratio, (double)total / both.SourceBytes, 0.001);
    }

    [Fact]
    public async Task A_pdf_is_estimated_by_its_compression_itself()
    {
        var folder = fx.NewFolder();
        var picture = Photo(2400, 1600);
        var pdf = new PdfBuilder();
        pdf.NewPage();
        pdf.Line("Quarterly report with photos", 14, pdf.Font(), 72, 72);
        pdf.Picture(picture, 72, 100, 216, 144);
        var source = pdf.Save(Path.Combine(folder, "report.pdf"));
        var preset = BuiltIn("compress-pdf");
        preset.Pdf.CompressWithGhostscript = false;
        using var estimator = await CompressionEstimator.ReadAsync([source], preset, cancellationToken: Ct);

        var screen = (await estimator.EstimateAsync(With(preset, pdf: PdfCompression.Screen), Ct))!.Value;
        var print = (await estimator.EstimateAsync(With(preset, pdf: PdfCompression.Print), Ct))!.Value;
        var written = Written(await fx.ConvertAsync([source], AskedCompression.Apply(preset, With(preset, pdf: PdfCompression.Screen))));

        Assert.True(screen < print, $"{screen} vs {print}");
        Assert.InRange(screen, written * 0.95, written * 1.05);
        Assert.Equal(estimator.SourceBytes, await estimator.EstimateAsync(With(preset, pdf: PdfCompression.None), Ct));
    }

    private async Task<string> MakeAsync(string name, params string[] arguments)
    {
        var path = Path.Combine(fx.NewFolder(), name);
        var result = await ProcessRunner.RunAsync(Tools!.Value.Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-y", .. arguments, path],
            TimeSpan.FromMinutes(2), Ct);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return path;
    }

    [Fact]
    public async Task Audio_is_estimated_from_its_bitrate_and_length()
    {
        Assert.SkipUnless(Tools is not null, "FFmpeg is not installed (pwsh build/fetch-engines.ps1 -Only ffmpeg)");
        var source = await MakeAsync("song.wav", "-f", "lavfi", "-i", "anoisesrc=d=20:c=pink:r=44100:a=0.3", "-ac", "2");
        var preset = BuiltIn("compress-audio");
        using var estimator = await CompressionEstimator.ReadAsync([source], preset, cancellationToken: Ct);

        foreach (var kbps in new[] { 96, 192 })
        {
            var estimate = (await estimator.EstimateAsync(With(preset, audioKbps: kbps), Ct))!.Value;
            var written = Written(await fx.ConvertAsync([source], AskedCompression.Apply(preset, With(preset, audioKbps: kbps))));
            Assert.Equal(AskedCompression.MediaBytes(20, kbps), estimate);
            Assert.InRange(estimate, written * 0.85, written * 1.15);
        }
    }

    [Fact]
    public async Task A_video_is_estimated_smaller_at_a_smaller_quality_and_never_above_the_original()
    {
        Assert.SkipUnless(Tools is not null, "FFmpeg is not installed (pwsh build/fetch-engines.ps1 -Only ffmpeg)");
        var source = await MakeAsync("clip.mp4",
            "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=6,noise=alls=30:allf=t",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=6", "-ac", "2",
            "-c:v", "libx264", "-crf", "14", "-pix_fmt", "yuv420p", "-c:a", "aac");
        var preset = BuiltIn("compress-video");
        using var estimator = await CompressionEstimator.ReadAsync([source], preset, cancellationToken: Ct);

        var small = (await estimator.EstimateAsync(With(preset, video: MediaQuality.Small), Ct))!.Value;
        var balanced = (await estimator.EstimateAsync(With(preset, video: MediaQuality.Balanced), Ct))!.Value;
        var high = (await estimator.EstimateAsync(With(preset, video: MediaQuality.High), Ct))!.Value;

        Assert.True(small < estimator.SourceBytes, $"{small} vs {estimator.SourceBytes}");
        // Balanced and High copy an H.264 video that needs no scaling: it keeps its size.
        Assert.Equal(estimator.SourceBytes, balanced);
        Assert.Equal(estimator.SourceBytes, high);
        // 720p30 at Small: 1280 × 720 × 30 × 0.05 bits per pixel ≈ 1382 kbit/s, and 128 kbit/s of sound.
        Assert.Equal((double)AskedCompression.MediaBytes(6, 1382 + 128), small, 0.01 * small);
    }

    [Fact]
    public async Task A_video_with_a_percentage_limit_is_estimated_at_most_at_it()
    {
        Assert.SkipUnless(Tools is not null, "FFmpeg is not installed (pwsh build/fetch-engines.ps1 -Only ffmpeg)");
        var source = await MakeAsync("clip.mp4",
            "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=4,noise=alls=30:allf=t",
            "-c:v", "libx264", "-crf", "12", "-pix_fmt", "yuv420p");
        var preset = BuiltIn("compress-video");
        preset.Media.MaxSizePercent = 1;
        using var estimator = await CompressionEstimator.ReadAsync([source], preset, cancellationToken: Ct);

        Assert.Equal(SizeLimit.Bytes(estimator.SourceBytes, 1), await estimator.EstimateAsync(With(preset, video: MediaQuality.High), Ct));
    }
}
