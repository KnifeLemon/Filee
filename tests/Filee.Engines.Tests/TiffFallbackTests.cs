// TIFF strip decoding failures can use the Windows Imaging Component without changing normal TIFF conversions.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Magick;
using Filee.Engines.Wic;
using ImageMagick;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Tests;

public sealed class TiffFallbackTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    [Theory]
    [InlineData("Read error on strip 0; got 7111410 bytes, expected 129408906. `TIFFFillStrip' @ error/tiff.c/TIFFErrors/565", true)]
    [InlineData("TIFFReadEncodedStrip: Invalid strip byte count", true)]
    [InlineData("cache resources exhausted", false)]
    [InlineData("unable to open image", false)]
    public void Only_strip_read_errors_are_retried(string message, bool expected) =>
        Assert.Equal(expected, ImageReader.IsTiffStripReadError(message));

    [Fact]
    public void Windows_tiff_reader_keeps_all_pages_pixels_and_resolution()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows TIFF codec is required.");
            return;
        }

        var path = Path.Combine(fx.NewFolder(), "STASINÓW test.tif");
        using (var pages = new MagickImageCollection())
        {
            pages.Add(new MagickImage(MagickColors.Red, 128, 96) { Density = new Density(300, 300, DensityUnit.PixelsPerInch) });
            pages.Add(new MagickImage(MagickColors.Blue, 64, 48) { Density = new Density(150, 150, DensityUnit.PixelsPerInch) });
            pages.Write(path, MagickFormat.Tiff);
        }

        using var decoded = WicImageReader.ReadTiff(path);
        Assert.Equal(2, decoded.Count);
        Assert.Equal((128u, 96u), (decoded[0].Width, decoded[0].Height));
        Assert.Equal((64u, 48u), (decoded[1].Width, decoded[1].Height));
        Assert.InRange(decoded[0].Density.X, 299, 301);
        Assert.InRange(decoded[1].Density.X, 149, 151);
        using var first = decoded[0].GetPixels();
        using var second = decoded[1].GetPixels();
        Assert.True(first.GetPixel(10, 10).ToColor()!.R > 240);
        Assert.True(second.GetPixel(10, 10).ToColor()!.B > 240);
    }

    [Fact]
    public async Task Normal_multi_page_tiff_still_converts_to_pdf_and_jpg()
    {
        var path = EngineFixture.MakeMultiPageTiff(fx.NewFolder(), 2);
        var pdf = await fx.ConvertAsync([path], new Preset { TargetFormat = "pdf" });
        Assert.Equal(JobState.Completed, pdf.State);
        using (var document = PdfReader.Open(Assert.Single(pdf.Outputs), PdfDocumentOpenMode.Import))
            Assert.Equal(2, document.PageCount);

        var jpg = await fx.ConvertAsync([path], new Preset { TargetFormat = "jpg" });
        Assert.Equal(JobState.Completed, jpg.State);
        Assert.Equal(2, jpg.Outputs.Count());
        foreach (var output in jpg.Outputs)
        {
            using var image = new MagickImage(output);
            Assert.Equal((120u, 80u), (image.Width, image.Height));
        }
    }
}
