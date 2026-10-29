// "Merge images" (#645) with ImageMagick: generated pictures merged one under another and side by side, with a gap,
// pictures of different sizes, a background colour or transparency, and the limit on the merged image's size.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Magick;
using ImageMagick;

namespace Filee.Engines.Tests;

public class MergeImagesEngineTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static Preset Merge(Action<ImageOptions>? set = null)
    {
        var preset = Filee.Core.Presets.BuiltInData.CreatePresets().Single(p => p.Id == "merge-images").Clone();
        set?.Invoke(preset.Image);
        return preset;
    }

    private static string Picture(string folder, string name, MagickColor colour, uint width, uint height, MagickFormat format = MagickFormat.Png)
    {
        using var image = new MagickImage(colour, width, height);
        var path = Path.Combine(folder, name);
        image.Write(path, format);
        return path;
    }

    private static string Output(ConversionJob job)
    {
        var errors = string.Join("; ", job.Files.Where(f => f.State == FileState.Failed).Select(f => $"{f.ErrorKey} {f.ErrorDetail}"));
        Assert.True(job.State == JobState.Completed, $"{job.State}: {errors}");
        return Assert.Single(job.Outputs);
    }

    private static IMagickColor<byte> Colour(IMagickImage<byte> image, int x, int y)
    {
        using var pixels = image.GetPixels();
        return pixels.GetPixel(x, y).ToColor()!;
    }

    /// <summary>The colour at (x, y) as #RRGGBBAA.</summary>
    private static string At(IMagickImage<byte> image, int x, int y)
    {
        var colour = Colour(image, x, y);
        return $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}{(image.HasAlpha ? colour.A : 255):X2}";
    }

    [Fact]
    public async Task Pictures_of_different_widths_stack_at_the_same_width_with_a_white_gap()
    {
        var dir = fx.NewFolder();
        var wide = Picture(dir, "1 front.png", MagickColors.Red, 200, 100);
        var narrow = Picture(dir, "2 back.png", MagickColors.Blue, 100, 100);

        var output = Output(await fx.ConvertAsync([wide, narrow], Merge(i => i.JoinGap = 10)));

        Assert.Equal("1 front_merged.png", Path.GetFileName(output));
        using var merged = new MagickImage(output);
        // The narrow one is scaled up to the width of the wide one: 100 + 10 + 200.
        Assert.Equal((200u, 310u), (merged.Width, merged.Height));
        Assert.Equal("#FF0000FF", At(merged, 100, 50));
        Assert.Equal("#FFFFFFFF", At(merged, 100, 105));
        Assert.Equal("#0000FFFF", At(merged, 100, 210));
        Assert.Equal("#0000FFFF", At(merged, 199, 309));
    }

    [Fact]
    public async Task Side_by_side_pictures_keep_their_sizes_centred_on_a_transparent_background()
    {
        var dir = fx.NewFolder();
        var low = Picture(dir, "a.png", MagickColors.Red, 100, 50);
        var tall = Picture(dir, "b.png", MagickColors.Lime, 60, 100);

        var output = Output(await fx.ConvertAsync([low, tall], Merge(i =>
        {
            i.JoinDirection = ImageJoinDirection.Horizontal;
            i.JoinFit = ImageJoinFit.Original;
            i.JoinGap = 8;
            i.JoinBackground = ImageOptions.TransparentJoin;
        })));

        using var merged = new MagickImage(output);
        Assert.Equal((168u, 100u), (merged.Width, merged.Height));
        Assert.EndsWith("00", At(merged, 50, 10)); // above the low picture: see-through
        Assert.Equal("#FF0000FF", At(merged, 50, 50)); // centred: rows 25 to 74
        Assert.EndsWith("00", At(merged, 50, 80));
        Assert.EndsWith("00", At(merged, 104, 50)); // the gap
        Assert.Equal("#00FF00FF", At(merged, 140, 5));
    }

    [Fact]
    public async Task Side_by_side_pictures_of_the_same_height_into_a_jpg_get_a_coloured_gap()
    {
        var dir = fx.NewFolder();
        var photo = Picture(dir, "card front.jpg", MagickColors.White, 300, 200, MagickFormat.Jpeg);
        var gray = Path.Combine(dir, "card back.png");
        using (var image = new MagickImage(new MagickColor("#808080"), 150, 50))
        {
            image.ColorType = ColorType.Grayscale; // a greyscale picture joins a colour one
            image.Write(gray, MagickFormat.Png);
        }

        var output = Output(await fx.ConvertAsync([photo, gray], Merge(i =>
        {
            i.JoinDirection = ImageJoinDirection.Horizontal;
            i.JoinGap = 20;
            i.JoinBackground = "#000080";
        })));

        Assert.Equal("card front_merged.jpg", Path.GetFileName(output)); // the first picture's format
        using var merged = new MagickImage(output);
        Assert.Equal(MagickFormat.Jpeg, merged.Format);
        // The greyscale one scaled to the height of the photo: 150 × 50 → 600 × 200.
        Assert.Equal((300u + 20 + 600, 200u), (merged.Width, merged.Height));
        var gap = Colour(merged, 310, 100);
        Assert.InRange(gap.B, 100, 150); // navy, give or take JPEG
        Assert.InRange(gap.R, 0, 30);
        var back = Colour(merged, 600, 100);
        Assert.InRange(back.R, 115, 140);
        Assert.InRange(back.B, 115, 140);
    }

    [Fact]
    public async Task Each_picture_turns_before_the_merge_and_a_transparent_gap_turns_white_in_a_jpg()
    {
        var dir = fx.NewFolder();
        var first = Picture(dir, "a.jpg", MagickColors.Red, 200, 100, MagickFormat.Jpeg);
        var second = Picture(dir, "b.jpg", MagickColors.Blue, 200, 100, MagickFormat.Jpeg);

        var output = Output(await fx.ConvertAsync([first, second], Merge(i =>
        {
            i.Rotation = ImageRotation.Right90;
            i.JoinDirection = ImageJoinDirection.Horizontal;
            i.JoinGap = 10;
            i.JoinBackground = ImageOptions.TransparentJoin;
        })));

        using var merged = new MagickImage(output);
        // Two 100 × 200 pictures, not one 200 × 210 picture turned on its side.
        Assert.Equal((210u, 200u), (merged.Width, merged.Height));
        var gap = Colour(merged, 105, 100);
        Assert.True(gap.R > 240 && gap.G > 240 && gap.B > 240, $"{gap}");
    }

    [Fact]
    public async Task A_drawing_becomes_a_picture_first_and_merges_with_a_photo()
    {
        var dir = fx.NewFolder();
        var svg = Path.Combine(dir, "logo.svg");
        await File.WriteAllTextAsync(svg,
            """<svg xmlns="http://www.w3.org/2000/svg" width="100" height="50"><rect width="100" height="50" fill="#00FF00"/></svg>""",
            TestContext.Current.CancellationToken);
        var photo = Picture(dir, "photo.png", MagickColors.Red, 100, 100);

        var output = Output(await fx.ConvertAsync([svg, photo], Merge()));

        // Named after the drawing, written as PNG: a merged picture can't be an SVG.
        Assert.Equal("logo_merged.png", Path.GetFileName(output));
        using var merged = new MagickImage(output);
        // The drawing is rendered at the vector engine's size, the photo scaled to its width.
        var width = (int)merged.Width;
        Assert.Equal(width / 2 + width, (int)merged.Height);
        Assert.Equal("#00FF00FF", At(merged, width / 2, width / 4));
        Assert.Equal("#FF0000FF", At(merged, width / 2, width));
    }

    [Fact]
    public async Task The_preset_s_resize_applies_to_the_merged_image()
    {
        var dir = fx.NewFolder();
        var a = Picture(dir, "a.png", MagickColors.Red, 200, 100);
        var b = Picture(dir, "b.png", MagickColors.Blue, 200, 100);

        var output = Output(await fx.ConvertAsync([a, b], Merge(i =>
        {
            i.Resize = ResizeMode.Percent;
            i.ResizePercent = 50;
        })));

        using var merged = new MagickImage(output);
        Assert.Equal((100u, 100u), (merged.Width, merged.Height));
    }

    [Fact]
    public void A_merged_image_beyond_the_limit_is_refused_with_its_size()
    {
        // 12 photos of 24 MP side by side: 288 MP.
        var sizes = Enumerable.Repeat((6000u, 4000u), 12).ToList();
        var refused = Assert.Throws<ConversionRefusedException>(() =>
            ImageJoiner.Plan(sizes, new ImageOptions { JoinDirection = ImageJoinDirection.Horizontal }));
        Assert.Equal("error.merged_image_too_large", refused.ErrorKey);
        Assert.Equal("72000 × 4000 px", refused.Detail);

        // Eight of them fit.
        var layout = ImageJoiner.Plan(sizes.Take(8).ToList(), new ImageOptions { JoinDirection = ImageJoinDirection.Horizontal });
        Assert.Equal((48000u, 4000u), (layout.Width, layout.Height));
        Assert.True((long)layout.Width * layout.Height <= ImageJoiner.MaxPixels);
    }

    [Fact]
    public void The_layout_scales_to_the_widest_or_centres_the_original_sizes()
    {
        (uint, uint)[] sizes = [(400, 300), (200, 200), (100, 50)];

        var same = ImageJoiner.Plan(sizes, new ImageOptions { JoinGap = 5 });
        Assert.Equal((400u, 300u + 5 + 400 + 5 + 200), (same.Width, same.Height));
        Assert.Equal([new(0, 0, 400, 300), new(0, 305, 400, 400), new(0, 710, 400, 200)], same.Pictures);

        var kept = ImageJoiner.Plan(sizes, new ImageOptions { JoinFit = ImageJoinFit.Original });
        Assert.Equal((400u, 550u), (kept.Width, kept.Height));
        Assert.Equal([new(0, 0, 400, 300), new(100, 300, 200, 200), new(150, 500, 100, 50)], kept.Pictures);

        var row = ImageJoiner.Plan(sizes, new ImageOptions { JoinDirection = ImageJoinDirection.Horizontal, JoinFit = ImageJoinFit.Original, JoinGap = 2 });
        Assert.Equal((704u, 300u), (row.Width, row.Height));
        Assert.Equal([new(0, 0, 400, 300), new(402, 50, 200, 200), new(604, 125, 100, 50)], row.Pictures);
    }

    [Theory]
    [InlineData("#00FF00", "#00FF00FF")]
    [InlineData("transparent", "#00000000")]
    [InlineData("TRANSPARENT", "#00000000")]
    [InlineData("not a colour", "#FFFFFFFF")]
    [InlineData("", "#FFFFFFFF")]
    public void The_background_reads_a_colour_or_transparency_and_falls_back_to_white(string setting, string expected)
    {
        var colour = ImageJoiner.Background(setting);
        Assert.Equal(expected, $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}{colour.A:X2}");
    }
}
