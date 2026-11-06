// Animated GIF, WebP, APNG and MNG ↔ animated AVIF (issue #1200). The animations are generated in the tests and every
// result is read back for its frames; with FFmpeg installed, its decoder counts them as well.

using System.Globalization;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Magick;
using Filee.Engines.Media;
using ImageMagick;
using ImageMagick.Drawing;
using PdfSharp.Pdf.IO;

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

    /// <summary>
    /// A moving red square, on white in the first <paramref name="opaque"/> frames (not a transparent pixel in them) and on
    /// transparency in the others. ImageMagick reads such opaque frames of a GIF, WebP or MNG without an alpha channel.
    /// </summary>
    private static string MakeOpaqueThenTransparent(string folder, string name, MagickFormat format, int opaque, int count = 3)
    {
        using var frames = new MagickImageCollection();
        for (var i = 0; i < count; i++)
        {
            var frame = new MagickImage(i < opaque ? MagickColors.White : MagickColors.Transparent, Width, Height)
            {
                AnimationDelay = 10,
                GifDisposeMethod = GifDisposeMethod.Background,
            };
            new Drawables().FillColor(MagickColors.Red).Rectangle(i * 20, 10, i * 20 + 15, 30).Draw(frame);
            frames.Add(frame);
        }
        var path = Path.Combine(folder, name);
        frames.Write(path, format);
        return path;
    }

    /// <summary>Asserts the frames show what <see cref="MakeOpaqueThenTransparent"/> drew, read from their pixels.</summary>
    private static void AssertOpaqueThenTransparent(MagickImageCollection frames, int opaque, int count = 3)
    {
        Assert.Equal(count, frames.Count);
        for (var i = 0; i < count; i++)
        {
            var pixels = frames[i].GetPixels();
            var square = pixels.GetPixel(i * 20 + 7, 20).ToColor()!;
            Assert.True(square.A > 200 && square.R > 200 && square.G < 60, $"frame {i}: the square is {square}");
            var background = pixels.GetPixel(60, 44).ToColor()!;
            if (i < opaque)
                Assert.True(background.A > 200 && background.G > 200, $"frame {i}: the opaque background is {background}");
            else
                Assert.True(background.A < 50, $"frame {i}: the transparent background is {background}");
        }
    }

    /// <summary>The major brand of an HEIF file: "avis" for an image sequence, "avif" for one picture.</summary>
    private static string Brand(string path) => Encoding.ASCII.GetString(File.ReadAllBytes(path), 8, 4);

    /// <summary>True when the file has an alpha track (the handler of an auxiliary video track).</summary>
    private static bool HasAlphaTrack(string path) => Encoding.ASCII.GetString(File.ReadAllBytes(path)).Contains("auxv", StringComparison.Ordinal);

    private static void AssertColours(MagickImageCollection frames, int count)
    {
        Assert.Equal(count, frames.Count);
        for (var i = 0; i < count; i++)
        {
            var pixel = frames[i].GetPixels().GetPixel((int)frames[i].Width / 2, (int)frames[i].Height / 2).ToColor()!;
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
        // A delay of 0 has no length of its own: it plays at the 100 ms players give such a GIF frame.
        Assert.Equal([100, 100, 100], DelaysMs(back));
    }

    [Fact]
    public async Task Resizing_and_turning_apply_to_every_frame()
    {
        var dir = fx.NewFolder();
        var gif = MakeAnimation(dir, "anim.gif", MagickFormat.Gif, 10, 20, 30);
        var preset = To("avif", quality: 90);
        preset.Image.Rotation = ImageRotation.Right90;
        preset.Image.Resize = ResizeMode.Percent;
        preset.Image.ResizePercent = 50;

        var job = await fx.ConvertAsync([gif], preset);

        AssertDone(job);
        var output = job.Outputs.Single();
        Assert.Equal("avis", Brand(output));
        using var frames = new MagickImageCollection(output);
        AssertColours(frames, 3);
        Assert.All(frames, frame => Assert.Equal((24u, 32u), (frame.Width, frame.Height)));
        Assert.Equal([100, 200, 300], DelaysMs(frames));
    }

    [Theory]
    [InlineData("gif", MagickFormat.Gif, 1)]
    [InlineData("gif", MagickFormat.Gif, 2)]
    [InlineData("webp", MagickFormat.WebP, 1)]
    [InlineData("mng", MagickFormat.Mng, 1)]
    public async Task Opaque_frames_before_transparent_ones_stay_visible(string from, MagickFormat format, int opaque)
    {
        var dir = fx.NewFolder();
        var source = MakeOpaqueThenTransparent(dir, "first." + from, format, opaque);

        var job = await fx.ConvertAsync([source], To("avif", quality: 90));

        AssertDone(job);
        var output = job.Outputs.Single();
        Assert.Equal("avis", Brand(output));
        Assert.True(HasAlphaTrack(output));
        using var frames = new MagickImageCollection(output);
        AssertOpaqueThenTransparent(frames, opaque);
    }

    [Theory]
    [InlineData("webp")]
    [InlineData("gif")]
    public async Task Opaque_frames_before_transparent_ones_stay_visible_from_avif_too(string target)
    {
        var dir = fx.NewFolder();
        var avif = await fx.ConvertAsync([MakeOpaqueThenTransparent(dir, "first.gif", MagickFormat.Gif, 1)], To("avif", quality: 90));
        AssertDone(avif);

        var job = await fx.ConvertAsync([avif.Outputs.Single()], To(target, quality: 90));

        AssertDone(job);
        using var frames = new MagickImageCollection(job.Outputs.Single());
        frames.Coalesce(); // the file may store only what changed
        AssertOpaqueThenTransparent(frames, 1);
    }

    [Theory]
    [InlineData("gif", MagickFormat.Gif)]
    [InlineData("webp", MagickFormat.WebP)]
    [InlineData("apng", MagickFormat.Gif)] // through a GIF: Apng.Read gives every frame an alpha channel
    public async Task An_opaque_animation_gets_no_alpha(string from, MagickFormat format)
    {
        var dir = fx.NewFolder();
        var source = MakeAnimation(dir, from == "apng" ? "anim.gif" : "anim." + from, format, 10, 10, 10);
        if (from == "apng")
        {
            var apng = await fx.ConvertAsync([source], To("apng"));
            AssertDone(apng);
            source = apng.Outputs.Single();
        }

        var job = await fx.ConvertAsync([source], To("avif"));

        AssertDone(job);
        var output = job.Outputs.Single();
        Assert.Equal("avis", Brand(output));
        Assert.False(HasAlphaTrack(output));
        using var frames = new MagickImageCollection(output);
        AssertColours(frames, 3);
        Assert.All(frames, frame => Assert.False(frame.HasAlpha));
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
        using var frames = target == "apng" ? Apng.Read(output) : new MagickImageCollection(output);
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

    [Fact]
    public async Task Animated_avif_becomes_one_page_in_a_pdf()
    {
        var dir = fx.NewFolder();
        var avif = await fx.ConvertAsync([MakeAnimation(dir, "anim.gif", MagickFormat.Gif, 10, 10, 10)], To("avif"));
        AssertDone(avif);

        var job = await fx.ConvertAsync([avif.Outputs.Single()], To("pdf"));

        AssertDone(job);
        using var pdf = PdfReader.Open(job.Outputs.Single(), PdfDocumentOpenMode.Import);
        Assert.Equal(1, pdf.PageCount);
    }

    // ───────────────────────── AVIF files of several pictures, not a sequence ─────────────────────────

    private static readonly MagickColor Red = new(255, 0, 0), Green = new(0, 255, 0), Blue = new(0, 0, 255);

    private static string MakeItems(string folder, string brand, params AvifBuilder.Picture[] pictures)
    {
        var path = Path.Combine(folder, "items.avif");
        File.WriteAllBytes(path, AvifBuilder.Items(brand, pictures));
        return path;
    }

    /// <summary>Asserts each output is one picture of its own size and colour, named <c>items_p{n}</c>.</summary>
    private static void AssertSeparate(ConversionJob job, string extension, params AvifBuilder.Picture[] pictures)
    {
        AssertDone(job);
        var outputs = job.Outputs.OrderBy(path => path, StringComparer.Ordinal).ToList();
        Assert.Equal(Enumerable.Range(1, pictures.Length).Select(n => $"items_p{n}.{extension}"), outputs.Select(Path.GetFileName));
        for (var i = 0; i < pictures.Length; i++)
        {
            using var frames = new MagickImageCollection(outputs[i]);
            var frame = Assert.Single(frames);
            Assert.Equal((pictures[i].Width, pictures[i].Height), (frame.Width, frame.Height));
            var pixel = frame.GetPixels().GetPixel((int)frame.Width / 2, (int)frame.Height / 2).ToColor()!;
            var expected = pictures[i].Colour;
            Assert.True(Math.Abs(pixel.R - expected.R) < 24 && Math.Abs(pixel.G - expected.G) < 24 && Math.Abs(pixel.B - expected.B) < 24,
                $"picture {i} is {pixel}, not {expected}");
        }
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpg")]
    [InlineData("webp")]
    [InlineData("avif")]
    public async Task A_burst_stays_separate_pictures(string target)
    {
        AvifBuilder.Picture[] pictures = [new(Red, 64, 48), new(Green, 64, 48)];

        var job = await fx.ConvertAsync([MakeItems(fx.NewFolder(), "avif", pictures)], To(target, quality: 90));

        AssertSeparate(job, target, pictures);
    }

    [Fact]
    public async Task A_burst_keeps_its_pictures_in_formats_that_hold_several()
    {
        var dir = fx.NewFolder();
        var source = MakeItems(dir, "avif", new AvifBuilder.Picture(Red, 64, 48), new AvifBuilder.Picture(Green, 64, 48));

        var gif = await fx.ConvertAsync([source], To("gif"));
        var document = await fx.ConvertAsync([source], To("pdf"));

        AssertDone(gif);
        AssertDone(document);
        using var frames = new MagickImageCollection(gif.Outputs.Single());
        Assert.Equal(2, frames.Count);
        using var pdf = PdfReader.Open(document.Outputs.Single(), PdfDocumentOpenMode.Import);
        Assert.Equal(2, pdf.PageCount);
    }

    [Theory]
    [InlineData("webp")]
    [InlineData("avif")]
    public async Task A_larger_second_item_is_not_cropped(string target)
    {
        AvifBuilder.Picture[] pictures = [new(Red, 64, 48), new(Blue, 128, 96)];

        var job = await fx.ConvertAsync([MakeItems(fx.NewFolder(), "avif", pictures)], To(target, quality: 90));

        AssertSeparate(job, target, pictures);
    }

    [Theory]
    [InlineData("webp")]
    [InlineData("avif")]
    public async Task A_smaller_second_item_like_a_visible_gain_map_is_not_laid_over_the_first(string target)
    {
        AvifBuilder.Picture[] pictures = [new(Red, 64, 48), new(new MagickColor(128, 128, 128), 16, 12)];

        var job = await fx.ConvertAsync([MakeItems(fx.NewFolder(), "avif", pictures)], To(target, quality: 90));

        AssertSeparate(job, target, pictures);
    }

    [Fact]
    public async Task The_avis_brand_alone_does_not_make_a_sequence()
    {
        AvifBuilder.Picture[] pictures = [new(Red, 64, 48), new(Green, 64, 48)];
        var path = MakeItems(fx.NewFolder(), "avis", pictures);
        Assert.False(AvifSequence.IsSequence(path));

        var job = await fx.ConvertAsync([path], To("png"));

        AssertSeparate(job, "png", pictures);
    }

    [Fact]
    public async Task Only_a_file_with_the_avis_brand_and_tracks_is_a_sequence()
    {
        var dir = fx.NewFolder();
        var job = await fx.ConvertAsync([MakeAnimation(dir, "anim.gif", MagickFormat.Gif, 10, 10)], To("avif"));
        AssertDone(job);
        var sequence = job.Outputs.Single();
        Assert.True(AvifSequence.IsSequence(sequence));

        // Browsers show a file with the major brand "avif" as one picture, even with tracks in it.
        var file = await File.ReadAllBytesAsync(sequence, TestContext.Current.CancellationToken);
        Encoding.ASCII.GetBytes("avif").CopyTo(file, 8);
        var still = Path.Combine(dir, "still.avif");
        await File.WriteAllBytesAsync(still, file, TestContext.Current.CancellationToken);
        Assert.False(AvifSequence.IsSequence(still));

        var empty = Path.Combine(dir, "empty.avif");
        await File.WriteAllBytesAsync(empty, [], TestContext.Current.CancellationToken);
        Assert.False(AvifSequence.IsSequence(empty));
        Assert.False(AvifSequence.IsSequence(Path.Combine(dir, "missing.avif")));
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
