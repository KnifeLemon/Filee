using Filee.Core.Conversion;
using Filee.Core.Presets;
using ImageMagick;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Tests;

public class ImageAndPdfTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static void AssertDone(ConversionJob job)
    {
        var errors = string.Join("; ", job.Files.Where(f => f.State == FileState.Failed).Select(f => $"{f.ErrorKey} {f.ErrorDetail}"));
        Assert.True(job.State == JobState.Completed, $"{job.State}: {errors}");
        Assert.All(job.Outputs, o => Assert.True(new FileInfo(o).Length > 0, o));
    }

    [Theory]
    [InlineData("photo.png_large", MagickFormat.Png)]
    [InlineData("photo.jpg_large", MagickFormat.Jpeg)]
    public async Task Large_image_aliases_convert_to_png(string name, MagickFormat format)
    {
        var dir = fx.NewFolder();
        var source = EngineFixture.MakeImage(dir, name, format, 64, 48);

        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "png" });

        AssertDone(job);
        var output = Assert.Single(job.Outputs);
        Assert.Equal(".png", Path.GetExtension(output));
        using var result = new MagickImage(output);
        Assert.Equal(MagickFormat.Png, result.Format);
        Assert.Equal((64u, 48u), (result.Width, result.Height));
    }

    [Theory]
    [InlineData("jpg", MagickFormat.Jpeg)]
    [InlineData("webp", MagickFormat.WebP)]
    [InlineData("tiff", MagickFormat.Tiff)]
    [InlineData("bmp", MagickFormat.Bmp)]
    [InlineData("gif", MagickFormat.Gif)]
    [InlineData("avif", MagickFormat.Avif)]
    public async Task Png_converts_to(string target, MagickFormat expected)
    {
        var dir = fx.NewFolder();
        var source = EngineFixture.MakeImage(dir, "a.png", MagickFormat.Png);

        var job = await fx.ConvertAsync([source], new Preset { Name = target, TargetFormat = target });

        AssertDone(job);
        using var result = new MagickImage(job.Outputs.Single());
        Assert.Equal(expected, result.Format);
        Assert.Equal(320u, result.Width);
    }

    /// <summary>A 3-frame animated GIF (red, green, blue; 20, 30 and 40 hundredths of a second, looping).</summary>
    private static string MakeAnimatedGif(string folder)
    {
        using var frames = new MagickImageCollection();
        foreach (var (color, delay) in new[] { (MagickColors.Red, 20u), (MagickColors.Lime, 30u), (MagickColors.Blue, 40u) })
            frames.Add(new MagickImage(color, 64, 48) { AnimationDelay = delay });
        var path = Path.Combine(folder, "anim.gif");
        frames.Write(path, MagickFormat.Gif);
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Animated_gif_becomes_an_animated_webp_and_back(bool lossless)
    {
        var dir = fx.NewFolder();
        var gif = MakeAnimatedGif(dir);

        var toWebp = await fx.ConvertAsync([gif], new Preset { TargetFormat = "webp", Image = { WebpLossless = lossless } });

        AssertDone(toWebp);
        var webp = toWebp.Outputs.Single();
        using (var frames = new MagickImageCollection(webp))
        {
            Assert.Equal(3, frames.Count);
            Assert.All(frames, f => Assert.Equal(MagickFormat.WebP, f.Format));
            Assert.Equal([20u, 30u, 40u], frames.Select(f => f.AnimationDelay));
            Assert.True(frames[1].GetPixels().GetPixel(32, 24).ToColor()!.G > 200, "second frame should be green");
        }

        var back = await fx.ConvertAsync([webp], new Preset { TargetFormat = "gif", Output = { FileNamePattern = "{name}_back" } });

        AssertDone(back);
        using var again = new MagickImageCollection(back.Outputs.Single());
        Assert.Equal(3, again.Count);
        Assert.Equal([20u, 30u, 40u], again.Select(f => f.AnimationDelay));
    }

    [Fact]
    public async Task Animated_gif_to_a_still_format_takes_the_first_frame()
    {
        var dir = fx.NewFolder();
        var gif = MakeAnimatedGif(dir);

        var job = await fx.ConvertAsync([gif], new Preset { TargetFormat = "png" });

        AssertDone(job);
        using var png = new MagickImage(job.Outputs.Single());
        Assert.True(png.GetPixels().GetPixel(32, 24).ToColor()!.R > 200, "first frame is red");
    }

    [Fact]
    public async Task Multi_page_tiff_to_webp_writes_one_image_per_page()
    {
        var dir = fx.NewFolder();
        using (var pages = new MagickImageCollection())
        {
            pages.Add(new MagickImage(MagickColors.Red, 64, 48));
            pages.Add(new MagickImage(MagickColors.Blue, 64, 48));
            pages.Write(Path.Combine(dir, "pages.tiff"), MagickFormat.Tiff);
        }

        var job = await fx.ConvertAsync([Path.Combine(dir, "pages.tiff")], new Preset { TargetFormat = "webp" });

        AssertDone(job);
        Assert.Equal(2, job.Outputs.Count());
    }

    [Theory]
    [InlineData(true, true, true)]    // all metadata kept
    [InlineData(false, true, false)]  // metadata removed, colour profile kept
    [InlineData(false, false, false)] // everything removed
    public async Task Removing_metadata_can_keep_the_colour_profile(bool keepMetadata, bool keepColorProfile, bool exifExpected)
    {
        var dir = fx.NewFolder();
        var source = Path.Combine(dir, "photo.jpg");
        using (var image = new MagickImage(MagickColors.SkyBlue, 64, 48))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Model, "TestPhone");
            image.SetProfile(exif);
            // A wide-gamut profile, like Display P3 from a phone (an sRGB profile would just become PNG's sRGB chunk).
            image.SetProfile(ColorProfiles.AdobeRGB1998);
            image.Write(source, MagickFormat.Jpeg);
        }

        var job = await fx.ConvertAsync([source], new Preset
        {
            TargetFormat = "webp",
            Image = { KeepMetadata = keepMetadata, KeepColorProfile = keepColorProfile },
        });

        AssertDone(job);
        using var result = new MagickImage(job.Outputs.Single());
        Assert.Equal(exifExpected, result.GetExifProfile() is not null);
        Assert.Equal(keepMetadata || keepColorProfile, result.GetColorProfile() is not null);
    }

    [Fact]
    public async Task Jpg_output_flattens_transparency_and_applies_quality()
    {
        var dir = fx.NewFolder();
        var source = EngineFixture.MakeImage(dir, "a.png", MagickFormat.Png);

        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "jpg", Image = { Quality = 40, Background = "#FF0000" } });

        AssertDone(job);
        using var result = new MagickImage(job.Outputs.Single());
        Assert.False(result.HasAlpha);
        Assert.InRange(result.Quality, 35u, 45u);
        var corner = result.GetPixels().GetPixel(5, 5).ToColor()!;
        Assert.True(corner.R > 200 && corner.G < 60, "transparent corner should become the background colour");
    }

    [Fact]
    public async Task Resize_by_long_edge_and_percent()
    {
        var dir = fx.NewFolder();
        var source = EngineFixture.MakeImage(dir, "a.png", MagickFormat.Png, 1000, 500);

        var longEdge = await fx.ConvertAsync([source], new Preset { TargetFormat = "png", Image = { Resize = ResizeMode.LongEdge, LongEdge = 400 }, Output = { FileNamePattern = "{name}_le" } });
        var percent = await fx.ConvertAsync([source], new Preset { TargetFormat = BuiltInData.SameAsSource, Image = { Resize = ResizeMode.Percent, ResizePercent = 50 }, Output = { FileNamePattern = "{name}_50" } });

        AssertDone(longEdge);
        AssertDone(percent);
        using (var a = new MagickImage(longEdge.Outputs.Single())) Assert.Equal((400u, 200u), (a.Width, a.Height));
        using (var b = new MagickImage(percent.Outputs.Single())) Assert.Equal((500u, 250u), (b.Width, b.Height));
    }

    [Fact]
    public async Task Ico_contains_several_sizes()
    {
        var dir = fx.NewFolder();
        var source = EngineFixture.MakeImage(dir, "logo.png", MagickFormat.Png, 300, 200);

        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "ico", Image = { IcoSizes = [64, 32, 16] } });

        AssertDone(job);
        using var icons = new MagickImageCollection(job.Outputs.Single());
        Assert.Equal([64u, 32u, 16u], icons.Select(i => i.Width));
    }

    [Fact]
    public async Task Ico_resized_below_its_sizes_holds_that_size_and_the_smaller_ones()
    {
        // "Edit pictures" (#584): 512 × 512 → long edge 32 is a 32 px icon, not 256 and 48 blown up from 32 px.
        var dir = fx.NewFolder();
        var source = EngineFixture.MakeImage(dir, "logo.png", MagickFormat.Png, 512, 512);

        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "ico", Image = { Resize = ResizeMode.LongEdge, LongEdge = 32 } });

        AssertDone(job);
        using var icons = new MagickImageCollection(job.Outputs.Single());
        Assert.Equal([32u, 16u], icons.Select(i => i.Width));
    }

    [Fact]
    public async Task Multi_page_tiff_to_png_writes_one_file_per_page()
    {
        var dir = fx.NewFolder();
        var source = EngineFixture.MakeMultiPageTiff(dir, 3);

        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "png" });

        AssertDone(job);
        Assert.Equal(["pages_p1.png", "pages_p2.png", "pages_p3.png"], job.Outputs.Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Images_to_pdf_and_back()
    {
        var dir = fx.NewFolder();
        var tiff = EngineFixture.MakeMultiPageTiff(dir, 2);

        var toPdf = await fx.ConvertAsync([tiff], new Preset { TargetFormat = "pdf" });
        AssertDone(toPdf);
        using (var pdf = PdfReader.Open(toPdf.Outputs.Single(), PdfDocumentOpenMode.Import))
            Assert.Equal(2, pdf.PageCount);

        var toPng = await fx.ConvertAsync([toPdf.Outputs.Single()], new Preset { TargetFormat = "png", Pdf = { RenderDpi = 72 }, Output = { FileNamePattern = "{name}_render" } });
        AssertDone(toPng);
        Assert.Equal(2, toPng.Outputs.Count());
    }

    /// <summary>
    /// A black square on white, written with the given PNG bit depth and colour type, or as a black-and-white TIFF
    /// (a fax or a scan) when <paramref name="name"/> ends in .tif.
    /// </summary>
    internal static string MakeScan(string folder, string name, string bitDepth = "1", string colorType = "0")
    {
        using var image = new MagickImage(MagickColors.White, 64, 48);
        image.Draw(new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Black).Rectangle(10, 10, 40, 30));
        var path = Path.Combine(folder, name);
        if (name.EndsWith(".tif", StringComparison.Ordinal))
        {
            image.Threshold(new Percentage(50));
            image.ColorType = ColorType.Bilevel;
            image.Depth = 1;
            image.Settings.Compression = CompressionMethod.Group4;
            image.Write(path, MagickFormat.Tiff);
            return path;
        }
        image.Settings.SetDefine(MagickFormat.Png, "bit-depth", bitDepth);
        image.Settings.SetDefine(MagickFormat.Png, "color-type", colorType);
        image.Write(path, MagickFormat.Png);
        return path;
    }

    [Theory]
    [InlineData("scan.png", "1", "0")]
    [InlineData("grey.png", "4", "0")]
    [InlineData("deep.png", "16", "2")]
    [InlineData("fax.tif", "", "")]
    public async Task Black_and_white_and_16_bit_pictures_go_into_a_pdf(string name, string bitDepth, string colorType)
    {
        // #605: PDFsharp refuses 1-, 2- and 4-bit grey and 16-bit PNGs ("Unsupported image format").
        var source = MakeScan(fx.NewFolder(), name, bitDepth, colorType);

        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "pdf" });

        AssertDone(job);
        using var pdf = PdfReader.Open(job.Outputs.Single(), PdfDocumentOpenMode.Import);
        Assert.Equal(1, pdf.PageCount);
    }

    [Fact]
    public void Pngs_pdfsharp_refuses_are_written_again_as_8_bit()
    {
        var folder = fx.NewFolder();
        foreach (var (bitDepth, colorType, refused) in new[] { ("1", "0", true), ("4", "0", true), ("16", "2", true), ("8", "0", false), ("8", "2", false) })
        {
            var png = MakeScan(folder, $"{bitDepth}-{colorType}.png", bitDepth, colorType);
            Assert.Equal(refused, Filee.Engines.Pdf.PdfPictures.NeedsReencoding(png));

            var readable = Filee.Engines.Pdf.PdfPictures.Readable(png, Path.Combine(folder, "readable"));
            Assert.Equal(!refused, readable == png);
            Assert.False(Filee.Engines.Pdf.PdfPictures.NeedsReencoding(readable));
            using var image = new MagickImage(readable);
            Assert.Equal((64u, 48u), (image.Width, image.Height));
            using var x = PdfSharp.Drawing.XImage.FromFile(readable);
        }

        var jpeg = EngineFixture.MakeImage(folder, "photo.jpg", MagickFormat.Jpeg);
        Assert.Equal(jpeg, Filee.Engines.Pdf.PdfPictures.Readable(jpeg, folder));
    }

    [Fact]
    public async Task Pdf_to_multi_page_tiff()
    {
        var dir = fx.NewFolder();
        var pdf = (await fx.ConvertAsync([EngineFixture.MakeMultiPageTiff(dir, 3)], new Preset { TargetFormat = "pdf" })).Outputs.Single();

        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "tiff", Pdf = { RenderDpi = 72 }, Output = { FileNamePattern = "{name}_back" } });

        AssertDone(job);
        using var pages = new MagickImageCollection(job.Outputs.Single());
        Assert.Equal(3, pages.Count);
    }

    [Fact]
    public async Task Pdf_pages_stack_into_one_tall_image()
    {
        // Issue #50: an 8-page PDF gave 8 pictures; "Long image" gives one with the pages one under another.
        var dir = fx.NewFolder();
        var pdf = (await fx.ConvertAsync([EngineFixture.MakeMultiPageTiff(dir, 3)], new Preset { TargetFormat = "pdf" })).Outputs.Single();
        using var firstPage = new MagickImage((await fx.ConvertAsync([pdf], new Preset { TargetFormat = "png", Pdf = { RenderDpi = 72, PageRange = "1" } })).Outputs.Single());

        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "png", Pdf = { RenderDpi = 72, CombinePages = true }, Output = { FileNamePattern = "{name}_long" } });

        AssertDone(job);
        Assert.Equal("pages_long.png", Path.GetFileName(job.Outputs.Single()));
        using var tall = new MagickImage(job.Outputs.Single());
        Assert.Equal(firstPage.Width, tall.Width);
        Assert.Equal(firstPage.Height * 3, tall.Height);
    }

    [Theory]
    [InlineData("jpg", 65_500)]
    [InlineData("webp", 16_383)]
    public void A_stack_taller_than_the_format_stores_is_scaled_down(string target, int limit)
    {
        // 40 A4 pages at 300 dpi: about 140,000 px tall.
        var pages = Enumerable.Range(0, 40).Select(_ => (IMagickImage<byte>)new MagickImage(MagickColors.SkyBlue, 2480, 3508)).ToList();
        try
        {
            using var tall = Filee.Engines.Pdf.PdfiumConverter.Stack(pages);
            Filee.Engines.Magick.ImageEncoder.ApplyOptions(tall, new ImageOptions(), target);
            Assert.True(tall.Height <= limit, $"{tall.Height} px");
            Assert.True((long)tall.Width * tall.Height <= Filee.Engines.Pdf.PdfiumConverter.MaxStackedPixels);
            Assert.InRange(tall.Width / (double)tall.Height, 2480 / (3508.0 * 40) * 0.95, 2480 / (3508.0 * 40) * 1.05); // pages keep their shape
        }
        finally
        {
            pages.ForEach(p => p.Dispose());
        }
    }

    [Fact]
    public async Task An_image_taller_than_a_jpg_holds_is_scaled_down_instead_of_failing()
    {
        // Issue #50: a 1200 × 74,673 px long PNG → JPG failed with "could not write the file".
        var dir = fx.NewFolder();
        var png = Path.Combine(dir, "long.png");
        using (var source = new MagickImage(MagickColors.SkyBlue, 12, 70_000))
            source.Write(png, MagickFormat.Png);

        var job = await fx.ConvertAsync([png], new Preset { TargetFormat = "jpg" });

        AssertDone(job);
        using var jpg = new MagickImage(job.Outputs.Single());
        Assert.InRange(jpg.Height, 60_000u, 65_500u);
    }

    [Fact]
    public void Width_resize_shrinks_wide_images_and_leaves_narrow_ones()
    {
        var options = new ImageOptions { Resize = ResizeMode.Width, Width = 1200 };
        using var wide = new MagickImage(MagickColors.White, 3000, 1000);
        using var narrow = new MagickImage(MagickColors.White, 800, 2000);

        Filee.Engines.Magick.ImageEncoder.ApplyOptions(wide, options, "jpg");
        Filee.Engines.Magick.ImageEncoder.ApplyOptions(narrow, options, "jpg");

        Assert.Equal((1200u, 400u), (wide.Width, wide.Height));
        Assert.Equal((800u, 2000u), (narrow.Width, narrow.Height));
    }

    [Fact]
    public void Custom_resize_stretches_to_exactly_the_asked_size()
    {
        // The case from the issue: a 1080 x 1920 photo asked for a 500 x 2000 banner, distorted on purpose.
        var options = new ImageOptions { CustomSize = new() { Width = 500, Height = 2000 } };
        using var portrait = new MagickImage(MagickColors.White, 1080, 1920);

        Filee.Engines.Magick.ImageEncoder.ApplyOptions(portrait, options, "png");

        Assert.Equal((500u, 2000u), (portrait.Width, portrait.Height));
    }

    [Fact]
    public void Custom_resize_with_the_lock_fits_inside_without_distortion()
    {
        var options = new ImageOptions { CustomSize = new() { Width = 500, Height = 2000, LockAspect = true } };

        // The width is the limit: 1080 x 1920 shrinks to the largest 9:16 fit inside the box, 500 x 889.
        using var portrait = new MagickImage(MagickColors.White, 1080, 1920);
        Filee.Engines.Magick.ImageEncoder.ApplyOptions(portrait, options, "png");
        Assert.Equal((500u, 889u), (portrait.Width, portrait.Height));

        // The lock grows as well: 250 x 444 doubles to 500 x 888, the same shape.
        using var small = new MagickImage(MagickColors.White, 250, 444);
        Filee.Engines.Magick.ImageEncoder.ApplyOptions(small, options, "png");
        Assert.Equal((500u, 888u), (small.Width, small.Height));
    }

    [Fact]
    public void Custom_resize_leaves_a_picture_that_already_has_the_asked_size()
    {
        var options = new ImageOptions { CustomSize = new() { Width = 500, Height = 2000 } };
        using var exact = new MagickImage(MagickColors.White, 500, 2000);

        Filee.Engines.Magick.ImageEncoder.ApplyOptions(exact, options, "png");

        Assert.Equal((500u, 2000u), (exact.Width, exact.Height));
    }

    [Fact]
    public void A_custom_size_read_from_a_file_with_a_side_of_zero_or_below_is_skipped()
    {
        // The editor keeps both sides within 16-16384, but a hand-edited presets.json or an imported bundle can
        // carry anything, and a width of 0 or below used to reach (uint)custom.Width and wrap to about 4 billion px.
        var options = new ImageOptions { CustomSize = new() { Width = 0, Height = 2000 } };
        using var zeroed = new MagickImage(MagickColors.White, 250, 444);
        Filee.Engines.Magick.ImageEncoder.ApplyOptions(zeroed, options, "png");
        Assert.Equal((250u, 444u), (zeroed.Width, zeroed.Height));

        options.CustomSize = new() { Width = -5, Height = 2000 };
        using var negative = new MagickImage(MagickColors.White, 250, 444);
        Filee.Engines.Magick.ImageEncoder.ApplyOptions(negative, options, "png");
        Assert.Equal((250u, 444u), (negative.Width, negative.Height));
    }

    [Fact]
    public void A_custom_size_read_from_a_file_is_brought_into_the_editors_range()
    {
        // A hand-edited 200000 used to go straight into image.Resize; it now stops at the editor's 16384, and a side
        // under 16 comes up to it, the same range the editor writes.
        var options = new ImageOptions { CustomSize = new() { Width = 200000, Height = 10 } };
        using var image = new MagickImage(MagickColors.White, 250, 444);
        Filee.Engines.Magick.ImageEncoder.ApplyOptions(image, options, "png");
        Assert.Equal((16384u, 16u), (image.Width, image.Height));

        var locked = new ImageOptions { CustomSize = new() { Width = 200000, Height = 200000, LockAspect = true } };
        Assert.Equal(20.48, Filee.Engines.Magick.ImageEncoder.ResizeScale(400, 800, locked), 3);

        var stretched = new ImageOptions { CustomSize = new() { Width = 500, Height = 200000 } };
        Assert.Equal(20.48, Filee.Engines.Magick.ImageEncoder.ResizeScale(400, 800, stretched), 3);
    }

    [Fact]
    public void Custom_resize_plans_pdf_page_runs_at_the_scale_the_resize_will_use()
    {
        // How PdfiumConverter.Split plans runs: the factor the stacked strip is scaled by (issue #576).
        var locked = new ImageOptions { CustomSize = new() { Width = 500, Height = 2000, LockAspect = true } };
        Assert.Equal(1.25, Filee.Engines.Magick.ImageEncoder.ResizeScale(400, 800, locked), 3);

        var stretched = new ImageOptions { CustomSize = new() { Width = 500, Height = 2000 } };
        Assert.Equal(2.5, Filee.Engines.Magick.ImageEncoder.ResizeScale(400, 800, stretched), 3);

        Assert.Equal(1, Filee.Engines.Magick.ImageEncoder.ResizeScale(400, 800, new ImageOptions()), 3);

        // A size of 0 or below from a hand-edited file is no size: the preset's own resize runs instead of it.
        var zeroed = new ImageOptions { CustomSize = new() { Width = 0, Height = 2000 } };
        Assert.Equal(1, Filee.Engines.Magick.ImageEncoder.ResizeScale(400, 800, zeroed), 3);

        var negative = new ImageOptions { CustomSize = new() { Width = 500, Height = -1 } };
        Assert.Equal(1, Filee.Engines.Magick.ImageEncoder.ResizeScale(400, 800, negative), 3);
    }

    [Fact]
    public void Min_short_edge_enlarges_a_small_picture_in_proportion()
    {
        // The case from the issue (#652): a site wants at least 1080 px on the short side.
        var options = new ImageOptions { MinShortEdge = 1080 };

        using var landscape = new MagickImage(MagickColors.White, 400, 300);
        Filee.Engines.Magick.ImageEncoder.ApplyOptions(landscape, options, "png");
        Assert.Equal((1440u, 1080u), (landscape.Width, landscape.Height));

        using var portrait = new MagickImage(MagickColors.White, 300, 500);
        Filee.Engines.Magick.ImageEncoder.ApplyOptions(portrait, options, "png");
        Assert.Equal((1080u, 1800u), (portrait.Width, portrait.Height));

        // An uneven factor still lands the short edge exactly on the minimum, never a pixel under it.
        using var uneven = new MagickImage(MagickColors.White, 333, 500);
        Filee.Engines.Magick.ImageEncoder.ApplyOptions(uneven, new ImageOptions { MinShortEdge = 1000 }, "png");
        Assert.Equal((1000u, 1502u), (uneven.Width, uneven.Height));
    }

    [Fact]
    public void Min_short_edge_never_shrinks_a_picture_that_already_reaches_it()
    {
        var options = new ImageOptions { MinShortEdge = 1080 };

        using var large = new MagickImage(MagickColors.White, 4000, 3000);
        Filee.Engines.Magick.ImageEncoder.ApplyOptions(large, options, "png");
        Assert.Equal((4000u, 3000u), (large.Width, large.Height));

        using var exact = new MagickImage(MagickColors.White, 1080, 1920);
        Filee.Engines.Magick.ImageEncoder.ApplyOptions(exact, options, "png");
        Assert.Equal((1080u, 1920u), (exact.Width, exact.Height));

        Assert.Equal(1, Filee.Engines.Magick.ImageEncoder.ResizeScale(4000, 3000, options), 3);
    }

    [Fact]
    public void A_min_short_edge_read_from_a_file_is_skipped_at_zero_or_below_and_kept_in_the_editors_range()
    {
        // The editor writes 16-16384; a hand-edited presets.json or an imported bundle can carry anything.
        foreach (var edge in new[] { 0, -5 })
        {
            using var image = new MagickImage(MagickColors.White, 250, 444);
            Filee.Engines.Magick.ImageEncoder.ApplyOptions(image, new ImageOptions { MinShortEdge = edge }, "png");
            Assert.Equal((250u, 444u), (image.Width, image.Height));
            Assert.Null(new ImageOptions { MinShortEdge = edge }.ClampedMinShortEdge);
        }

        // Under 16 comes up to it...
        using var tiny = new MagickImage(MagickColors.White, 8, 10);
        Filee.Engines.Magick.ImageEncoder.ApplyOptions(tiny, new ImageOptions { MinShortEdge = 5 }, "png");
        Assert.Equal((16u, 20u), (tiny.Width, tiny.Height));

        // ...and a hand-edited 200000 stops at 16384 (planned here: the picture itself would be 268 megapixels).
        Assert.Equal(16384, new ImageOptions { MinShortEdge = 200000 }.ClampedMinShortEdge);
        Assert.Equal(40.96, Filee.Engines.Magick.ImageEncoder.ResizeScale(400, 400, new ImageOptions { MinShortEdge = 200000 }), 3);
    }

    [Fact]
    public void Min_short_edge_does_not_enlarge_a_hairline_strip_past_the_largest_custom_size()
    {
        // 16 x 4000 to a short edge of 4000 would be 4000 x 1,000,000 px: it stops at as many pixels as the largest
        // custom size (16384 x 16384) holds.
        var scale = Filee.Engines.Magick.ImageEncoder.MinShortEdgeScale(16, 4000, 4000);
        Assert.Equal(16384.0 * 16384, 16 * scale * 4000 * scale, 0);
        Assert.True(scale > 1);
    }

    [Fact]
    public void Min_short_edge_still_fits_the_format_after_enlarging()
    {
        // WebP holds 16,383 px a side: a tall 100 x 2000 strip enlarged to a 1080 short edge (21,600 px tall) is
        // brought back inside it, as after any other resize.
        using var strip = new MagickImage(MagickColors.White, 100, 2000);
        Filee.Engines.Magick.ImageEncoder.ApplyOptions(strip, new ImageOptions { MinShortEdge = 1080 }, "webp");
        Assert.InRange(strip.Height, 16300u, 16383u);
        Assert.Equal(20.0, strip.Height / (double)strip.Width, 1);
    }

    [Fact]
    public async Task Min_short_edge_enlarges_in_a_conversion_and_an_icon_keeps_its_256_px_cap()
    {
        var dir = fx.NewFolder();
        var source = EngineFixture.MakeImage(dir, "small.png", MagickFormat.Png, 300, 200);

        var jpg = await fx.ConvertAsync([source], new Preset { TargetFormat = "jpg", Image = { MinShortEdge = 1080 } });
        AssertDone(jpg);
        using (var picture = new MagickImage(jpg.Outputs.Single()))
            Assert.Equal((1620u, 1080u), (picture.Width, picture.Height));

        var ico = await fx.ConvertAsync([source], new Preset { TargetFormat = "ico", Image = { MinShortEdge = 1080 } });
        AssertDone(ico);
        using var icons = new MagickImageCollection(ico.Outputs.Single());
        Assert.Equal(256u, icons.Max(i => i.Width));
    }

    [Fact]
    public void Min_short_edge_plans_pdf_page_runs_at_the_scale_the_resize_will_use()
    {
        // How PdfiumConverter.Split plans runs: the factor the stacked strip is scaled by.
        Assert.Equal(2.5, Filee.Engines.Magick.ImageEncoder.ResizeScale(400, 800, new ImageOptions { MinShortEdge = 1000 }), 3);
        Assert.Equal(1, Filee.Engines.Magick.ImageEncoder.ResizeScale(400, 800, new ImageOptions { MinShortEdge = 300 }), 3);
        Assert.Equal(1, Filee.Engines.Magick.ImageEncoder.ResizeScale(400, 800, new ImageOptions { MinShortEdge = 0 }), 3);

        // Custom sizing comes first when a hand-edited file carries both.
        var both = new ImageOptions { MinShortEdge = 1000, CustomSize = new() { Width = 200, Height = 400 } };
        Assert.Equal(0.5, Filee.Engines.Magick.ImageEncoder.ResizeScale(400, 800, both), 3);
    }

    [Fact]
    public void A_long_image_splits_between_pages_into_parts_the_format_holds()
    {
        // Five 6,000 px pages: a WebP holds 16,383 px, so two pages per part; at half width (Width 50 px) all fit.
        var pages = Enumerable.Range(0, 5).Select(_ => (IMagickImage<byte>)new MagickImage(MagickColors.White, 100, 6000)).ToList();
        try
        {
            var parts = Filee.Engines.Pdf.PdfiumConverter.Split(pages, "webp", new ImageOptions());
            Assert.Equal([2, 2, 1], parts.Select(p => p.Count));

            var half = Filee.Engines.Pdf.PdfiumConverter.Split(pages, "webp", new ImageOptions { Resize = ResizeMode.Width, Width = 50 });
            Assert.Equal([5], half.Select(p => p.Count));
            Assert.Single(Filee.Engines.Pdf.PdfiumConverter.Split(pages, "png", new ImageOptions()));
        }
        finally
        {
            pages.ForEach(p => p.Dispose());
        }
    }

    [Fact]
    public void Trimming_keeps_the_content_and_a_small_margin()
    {
        using var page = new MagickImage(MagickColors.White, 200, 1000);
        page.Draw(new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Black).Rectangle(20, 400, 180, 499));
        using var blank = new MagickImage(MagickColors.White, 200, 1000);

        Filee.Engines.Pdf.PdfiumConverter.TrimVertically(page, 72); // margin 12 px at 72 dpi
        Filee.Engines.Pdf.PdfiumConverter.TrimVertically(blank, 72);

        Assert.Equal((200u, 124u), (page.Width, page.Height));
        Assert.Equal(24u, blank.Height);
    }

    [Fact]
    public async Task Trimmed_long_image_is_shorter_and_a_split_one_names_its_parts()
    {
        var dir = fx.NewFolder();
        var pdf = (await fx.ConvertAsync([EngineFixture.MakeMultiPageTiff(dir, 3)], new Preset { TargetFormat = "pdf" })).Outputs.Single();
        var full = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "png", Pdf = { RenderDpi = 72, CombinePages = true }, Output = { FileNamePattern = "{name}_full" } });
        var trimmed = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "png", Pdf = { RenderDpi = 72, CombinePages = true, TrimPageMargins = true }, Output = { FileNamePattern = "{name}_trim" } });

        AssertDone(full);
        AssertDone(trimmed);
        using var a = new MagickImage(full.Outputs.Single());
        using var b = new MagickImage(trimmed.Outputs.Single());
        Assert.Equal(a.Width, b.Width);
        Assert.True(b.Height <= a.Height, $"{b.Height} > {a.Height}");

        // A split that fits in one part writes one file without a number.
        var split = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "jpg", Pdf = { RenderDpi = 72, CombinePages = true, SplitTallImage = true }, Output = { FileNamePattern = "{name}_long" } });
        AssertDone(split);
        Assert.Equal("pages_long.jpg", Path.GetFileName(split.Outputs.Single()));
    }

    [Fact]
    public async Task Merge_images_into_one_pdf()
    {
        var dir = fx.NewFolder();
        var a = EngineFixture.MakeImage(dir, "1.jpg", MagickFormat.Jpeg);
        var b = EngineFixture.MakeImage(dir, "2.png", MagickFormat.Png);
        var c = EngineFixture.MakeMultiPageTiff(dir, 2);

        var job = await fx.ConvertAsync([a, b, c], new Preset { TargetFormat = "pdf", Pdf = { MergeIntoSingle = true, PageSize = PdfPageSize.A4, MarginMm = 10 }, Output = { FileNamePattern = "{name}_merged" } });

        AssertDone(job);
        using var pdf = PdfReader.Open(job.Outputs.Single(), PdfDocumentOpenMode.Import);
        Assert.Equal(4, pdf.PageCount);
        Assert.Equal("1_merged.pdf", Path.GetFileName(job.Outputs.Single()));
    }

    [Fact]
    public async Task Split_pdf_by_page_range()
    {
        var dir = fx.NewFolder();
        var pdf = (await fx.ConvertAsync([EngineFixture.MakeMultiPageTiff(dir, 4)], new Preset { TargetFormat = "pdf" })).Outputs.Single();

        var job = await fx.ConvertAsync([pdf], new Preset
        {
            TargetFormat = "pdf",
            Pdf = { SplitPages = true, PageRange = "2-3" },
            Output = { Location = OutputLocation.Subfolder, SubfolderName = "{name}" },
        });

        AssertDone(job);
        Assert.Equal(["pages_p2.pdf", "pages_p3.pdf"], job.Outputs.Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }
}
