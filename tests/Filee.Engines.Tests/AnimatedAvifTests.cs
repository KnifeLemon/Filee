// Animated GIF, WebP, APNG and MNG ↔ animated AVIF (issue #1200). The animations are generated in the tests and every
// result is read back for its frames; with FFmpeg installed, its decoder counts them as well.

using System.Globalization;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Media;
using ImageMagick;
using ImageMagick.Drawing;

namespace Filee.Engines.Tests;

[Collection(nameof(FfmpegCollection))]
public class AnimatedAvifTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const uint Width = 64, Height = 48;

    private static void AssertDone(ConversionJob job)
    {
        var errors = string.Join("; ", job.Files.Where(f => f.State == FileState.Failed).Select(f => $"{f.ErrorKey} {f.ErrorDetail}"));
        Assert.True(job.State == JobState.Completed, $"{job.State}: {errors}");
    }

    private static Preset To(string target, int quality = 60, double speed = 1) =>
        new() { TargetFormat = target, Image = { Quality = quality, AnimationSpeed = speed } };

    private static MagickColor Colour(int i) => new((byte)(i * 60 % 256), (byte)(255 - i * 60 % 256), 90);

    /// <summary>A frame of its own colour for every delay (hundredths of a second).</summary>
    private static string MakeAnimation(string folder, string name, MagickFormat format, params uint[] delays)
    {
        using var frames = new MagickImageCollection();
        for (var i = 0; i < delays.Length; i++)
            frames.Add(new MagickImage(Colour(i), Width, Height) { AnimationDelay = delays[i] });
        var path = Path.Combine(folder, name);
        frames.Write(path, format);
        return path;
    }

    /// <summary>A square that moves right on a transparent background, a step of 20 px per frame.</summary>
    private static string MakeMovingSquare(string folder, int count = 3)
    {
        using var frames = new MagickImageCollection();
        for (var i = 0; i < count; i++)
        {
            var frame = new MagickImage(MagickColors.Transparent, Width, Height)
            {
                AnimationDelay = 10,
                GifDisposeMethod = GifDisposeMethod.Background,
            };
            new Drawables().FillColor(MagickColors.Red).Rectangle(i * 20, 10, i * 20 + 15, 30).Draw(frame);
            frames.Add(frame);
        }
        var path = Path.Combine(folder, "square.gif");
        frames.Write(path, MagickFormat.Gif);
        return path;
    }

    /// <summary>The major brand of an HEIF file: "avis" for an image sequence, "avif" for one picture.</summary>
    private static string Brand(string path) => Encoding.ASCII.GetString(File.ReadAllBytes(path), 8, 4);

    private static void AssertColours(MagickImageCollection frames, int count)
    {
        Assert.Equal(count, frames.Count);
        for (var i = 0; i < count; i++)
        {
            var pixel = frames[i].GetPixels().GetPixel(32, 24).ToColor()!;
            var expected = Colour(i);
            Assert.True(Math.Abs(pixel.R - expected.R) < 16 && Math.Abs(pixel.G - expected.G) < 16 && Math.Abs(pixel.B - expected.B) < 16,
                $"frame {i} is {pixel}, not {expected}");
        }
    }

    /// <summary>Each frame's delay in milliseconds.</summary>
    private static List<int> DelaysMs(MagickImageCollection frames) =>
        [.. frames.Select(f => (int)Math.Round(f.AnimationDelay * 1000.0 / f.AnimationTicksPerSecond))];

    /// <summary>Asserts every frame shows the square at its own place and nothing where it was before.</summary>
    private static void AssertSquareMoves(MagickImageCollection frames, int count)
    {
        Assert.Equal(count, frames.Count);
        for (var i = 0; i < count; i++)
        {
            var pixels = frames[i].GetPixels();
            var square = pixels.GetPixel(i * 20 + 7, 20).ToColor()!;
            Assert.True(square.A > 200 && square.R > 200, $"frame {i}: the square is {square}");
            if (i > 0)
                Assert.True(pixels.GetPixel((i - 1) * 20 + 2, 20).ToColor()!.A < 50, $"frame {i} still shows the square of the frame before");
        }
    }

    [Theory]
    [InlineData("gif", MagickFormat.Gif)]
    [InlineData("webp", MagickFormat.WebP)]
    public async Task Animations_stay_animated_with_their_timing(string from, MagickFormat format)
    {
        var dir = fx.NewFolder();
        var source = MakeAnimation(dir, "anim." + from, format, 10, 20, 30, 40);

        var job = await fx.ConvertAsync([source], To("avif"));

        AssertDone(job);
        var output = job.Outputs.Single();
        Assert.Equal("avis", Brand(output));
        using var frames = new MagickImageCollection(output);
        AssertColours(frames, 4);
        Assert.Equal([100, 200, 300, 400], DelaysMs(frames));
    }

    [Fact]
    public async Task Apng_and_mng_stay_animated_too()
    {
        var dir = fx.NewFolder();
        var gif = MakeAnimation(dir, "anim.gif", MagickFormat.Gif, 10, 10, 10);
        foreach (var format in new[] { "apng", "mng" })
        {
            var source = await fx.ConvertAsync([gif], To(format));
            AssertDone(source);

            var job = await fx.ConvertAsync([source.Outputs.Single()], To("avif"));

            AssertDone(job);
            Assert.Equal("avis", Brand(job.Outputs.Single()));
            using var frames = new MagickImageCollection(job.Outputs.Single());
            AssertColours(frames, 3);
        }
    }

    [Fact]
    public async Task Odd_sizes_and_delays_of_zero_are_written()
    {
        var dir = fx.NewFolder();
        using var frames = new MagickImageCollection();
        for (var i = 0; i < 3; i++)
            frames.Add(new MagickImage(Colour(i), 3, 1));
        var gif = Path.Combine(dir, "tiny.gif");
        frames.Write(gif, MagickFormat.Gif);

        var job = await fx.ConvertAsync([gif], To("avif"));

        AssertDone(job);
        using var back = new MagickImageCollection(job.Outputs.Single());
        Assert.Equal(3, back.Count);
        Assert.Equal((3u, 1u), (back[0].Width, back[0].Height));
    }

    [Fact]
    public async Task Transparency_stays_and_frames_do_not_pile_up()
    {
        var dir = fx.NewFolder();

        var job = await fx.ConvertAsync([MakeMovingSquare(dir)], To("avif", quality: 90));

        AssertDone(job);
        using var frames = new MagickImageCollection(job.Outputs.Single());
        AssertSquareMoves(frames, 3);
    }

    [Theory]
    [InlineData("webp")]
    [InlineData("gif")]
    public async Task Transparent_animated_avif_does_not_pile_up_either(string target)
    {
        var dir = fx.NewFolder();
        var avif = await fx.ConvertAsync([MakeMovingSquare(dir)], To("avif", quality: 90));
        AssertDone(avif);

        var job = await fx.ConvertAsync([avif.Outputs.Single()], To(target, quality: 90));

        AssertDone(job);
        using var frames = new MagickImageCollection(job.Outputs.Single());
        frames.Coalesce(); // the file may store only what changed
        AssertSquareMoves(frames, 3);
    }

    [Fact]
    public async Task Quality_applies_to_every_frame()
    {
        var dir = fx.NewFolder();
        using (var noise = new MagickImageCollection())
        {
            for (var i = 0; i < 4; i++)
            {
                var frame = new MagickImage("plasma:", 160, 120) { AnimationDelay = 10 };
                noise.Add(frame);
            }
            noise.Write(Path.Combine(dir, "noise.gif"), MagickFormat.Gif);
        }
        var gif = Path.Combine(dir, "noise.gif");

        var low = await fx.ConvertAsync([gif], new Preset { TargetFormat = "avif", Image = { Quality = 20 }, Output = { FileNamePattern = "{name}_low" } });
        var high = await fx.ConvertAsync([gif], new Preset { TargetFormat = "avif", Image = { Quality = 90 }, Output = { FileNamePattern = "{name}_high" } });

        AssertDone(low);
        AssertDone(high);
        Assert.Equal("avis", Brand(high.Outputs.Single()));
        Assert.True(new FileInfo(high.Outputs.Single()).Length > 2 * new FileInfo(low.Outputs.Single()).Length);
    }

    [Fact]
    public async Task Playback_speed_changes_the_delays()
    {
        var dir = fx.NewFolder();
        var gif = MakeAnimation(dir, "anim.gif", MagickFormat.Gif, 10, 20, 30, 40);

        var job = await fx.ConvertAsync([gif], To("avif", speed: 2));

        AssertDone(job);
        using var frames = new MagickImageCollection(job.Outputs.Single());
        Assert.Equal([50, 100, 150, 200], DelaysMs(frames));
    }

    // ───────────────────────── Still pictures ─────────────────────────

    [Theory]
    [InlineData("png", MagickFormat.Png)]
    [InlineData("gif", MagickFormat.Gif)]
    public async Task A_still_picture_is_written_as_before(string from, MagickFormat format)
    {
        var dir = fx.NewFolder();
        var source = MakeAnimation(dir, "still." + from, format, 10);

        var job = await fx.ConvertAsync([source], To("avif"));

        AssertDone(job);
        var output = job.Outputs.Single();
        Assert.Equal("avif", Brand(output));
        // The same bytes as writing the one picture at the preset's quality: no sequence, no other encoder settings.
        using var image = new MagickImage(source);
        image.Strip();
        image.Quality = 60;
        Assert.Equal(image.ToByteArray(MagickFormat.Avif), await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Pages_stay_one_picture_each()
    {
        var dir = fx.NewFolder();

        var job = await fx.ConvertAsync([EngineFixture.MakeMultiPageTiff(dir, 2)], To("avif"));

        AssertDone(job);
        Assert.Equal(2, job.Outputs.Count());
        Assert.All(job.Outputs, output => Assert.Equal("avif", Brand(output)));
    }

    // ───────────────────────── Animated AVIF as the source ─────────────────────────

    [Theory]
    [InlineData("gif")]
    [InlineData("webp")]
    [InlineData("apng")]
    [InlineData("avif")]
    public async Task Animated_avif_keeps_its_frames_and_timing(string target)
    {
        var dir = fx.NewFolder();
        var avif = await fx.ConvertAsync([MakeAnimation(dir, "anim.gif", MagickFormat.Gif, 10, 20, 30, 40)], To("avif", quality: 90));
        AssertDone(avif);

        var job = await fx.ConvertAsync([avif.Outputs.Single()],
            new Preset { TargetFormat = target, Image = { Quality = 90 }, Output = { FileNamePattern = "{name}_again" } });

        AssertDone(job);
        var output = job.Outputs.Single();
        using var frames = target == "apng" ? Filee.Engines.Magick.Apng.Read(output) : new MagickImageCollection(output);
        AssertColours(frames, 4);
        Assert.Equal([100, 200, 300, 400], DelaysMs(frames));
    }

    [Fact]
    public async Task Animated_avif_becomes_one_picture_for_other_targets()
    {
        var dir = fx.NewFolder();
        var avif = await fx.ConvertAsync([MakeAnimation(dir, "anim.gif", MagickFormat.Gif, 10, 10, 10)], To("avif"));
        AssertDone(avif);

        var job = await fx.ConvertAsync([avif.Outputs.Single()], To("png"));

        AssertDone(job);
        using var frames = new MagickImageCollection(job.Outputs.Single());
        AssertColours(frames, 1);
    }

    [Fact]
    public void Avif_reaches_gif_directly_and_counts_as_a_still_picture_on_the_way_to_video()
    {
        var planner = fx.Catalog.CreatePlanner(["ffmpeg"]);
        Assert.Equal([("magick", "avif", "gif")], planner.Plan("avif", "gif")!.Steps.Select(s => (s.Converter.Id, s.From, s.To)));
        Assert.Equal([("magick", "gif", "avif")], planner.Plan("gif", "avif")!.Steps.Select(s => (s.Converter.Id, s.From, s.To)));
        Assert.Equal([("magick", "avif", "png"), ("ffmpeg", "png", "mp4")], planner.Plan("avif", "mp4")!.Steps.Select(s => (s.Converter.Id, s.From, s.To)));
    }

    // ───────────────────────── With FFmpeg, a decoder and encoder of its own ─────────────────────────

    private static string FfmpegOrSkip()
    {
        Assert.SkipUnless(FfmpegConverter.Locate() is not null, "FFmpeg is not installed (pwsh build/fetch-engines.ps1 -Only ffmpeg)");
        return FfmpegConverter.Locate()!.Value.Ffmpeg;
    }

    [Fact]
    public async Task Ffmpeg_counts_the_same_frames()
    {
        var ffmpeg = FfmpegOrSkip();
        var dir = fx.NewFolder();
        var job = await fx.ConvertAsync([MakeAnimation(dir, "anim.gif", MagickFormat.Gif, 10, 20, 30, 40)], To("avif"));
        AssertDone(job);

        // The sequence track is the first stream; "-f null" decodes it. Stream 1 is the still picture for other viewers.
        var result = await FfmpegRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-nostdin", "-i", job.Outputs.Single(), "-map", "0:v:0", "-f", "null", "-"], null, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        var frames = result.StandardError.Split('\r', '\n').Last(line => line.StartsWith("frame=", StringComparison.Ordinal));
        Assert.Equal(4, int.Parse(frames["frame=".Length..].Trim().Split(' ')[0], CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task An_animated_avif_from_ffmpeg_keeps_its_frames()
    {
        var ffmpeg = FfmpegOrSkip();
        var dir = fx.NewFolder();
        var gif = MakeAnimation(dir, "anim.gif", MagickFormat.Gif, 10, 10, 10, 10);
        var avif = Path.Combine(dir, "ffmpeg.avif");
        var encode = await FfmpegRunner.RunAsync(ffmpeg,
            ["-hide_banner", "-nostdin", "-v", "error", "-y", "-i", gif, "-c:v", "libaom-av1", "-crf", "20", "-cpu-used", "8", "-pix_fmt", "yuv420p", "-f", "avif", avif],
            null, TestContext.Current.CancellationToken);
        Assert.SkipUnless(encode.ExitCode == 0, $"This FFmpeg has no AVIF route: {encode.StandardError}");

        var job = await fx.ConvertAsync([avif], To("webp", quality: 90));

        AssertDone(job);
        using var frames = new MagickImageCollection(job.Outputs.Single());
        AssertColours(frames, 4);
        Assert.Equal([100, 100, 100, 100], DelaysMs(frames));
    }
}
