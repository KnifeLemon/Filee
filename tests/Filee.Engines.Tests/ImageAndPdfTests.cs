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
