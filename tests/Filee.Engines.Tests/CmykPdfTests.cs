// CMYK pictures → PDF keep CMYK and their profile (#1027): print work stays print work. Rendered back with PDFium, the
// page shows the picture's colours; read back with PDFsharp, the image is CMYK with the source's samples.

using System.IO.Compression;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Tests;

public class CmykPdfTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static void AssertDone(ConversionJob job) =>
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey}: {f.ErrorDetail}")));

    /// <summary>
    /// A CMYK picture: left half orange, right half dark blue, through the U.S. Web Coated (SWOP) profile (or none),
    /// optionally with a transparent bottom half.
    /// </summary>
    private string Cmyk(string format, bool profile = true, bool transparent = false)
    {
        using var image = new MagickImage(MagickColors.Orange, 120, 80);
        image.Draw(new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Navy).Rectangle(60, 0, 119, 79));
        image.SetProfile(ColorProfiles.SRGB);
        image.TransformColorSpace(ColorProfiles.SRGB, ColorProfiles.USWebCoatedSWOP);
        if (!profile)
            image.RemoveProfile("icc");
        if (transparent)
        {
            image.Alpha(AlphaAction.Set);
            using var pixels = image.GetPixels();
            var channels = (int)image.ChannelCount;
            var bottom = pixels.GetArea(0, 40, 120, 40)!;
            for (var i = channels - 1; i < bottom.Length; i += channels)
                bottom[i] = 0;
            pixels.SetArea(0, 40, 120, 40, bottom);
        }
        var path = Path.Combine(fx.NewFolder(), "print." + format);
        image.Write(path);
        using (var check = new MagickImage(path))
            Assert.Equal(ColorSpace.CMYK, check.ColorSpace);
        return path;
    }

    private async Task<string> ToPdfAsync(string picture)
    {
        var job = await fx.ConvertAsync([picture], new Preset { TargetFormat = "pdf" });
        AssertDone(job);
        return job.Outputs.Single();
    }

    /// <summary>The page drawn back by PDFium.</summary>
    private async Task<MagickImage> RenderAsync(string pdf)
    {
        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "png", Output = { FileNamePattern = "{name}_page" } });
        AssertDone(job);
        return new MagickImage(job.Outputs.Single());
    }

    /// <summary>The one picture on the PDF's first page.</summary>
    private static PdfDictionary Picture(PdfDocument document)
    {
        var xobjects = document.Pages[0].Resources.Elements.GetDictionary("/XObject")!;
        return (PdfDictionary)((PdfReference)xobjects.Elements.Values.Single()!).Value;
    }

    private static string ColorSpaceOf(PdfDictionary picture) => picture.Elements["/ColorSpace"] switch
    {
        PdfName name => name.Value,
        PdfArray { Elements: [PdfName { Value: "/ICCBased" }, PdfReference icc] } =>
            $"/ICCBased N={((PdfDictionary)icc.Value).Elements.GetInteger("/N")}",
        var other => other?.ToString() ?? "none",
    };

    private static IMagickColor<byte> At(MagickImage page, double x, double y) =>
        page.GetPixels().GetPixel((int)(page.Width * x), (int)(page.Height * y))!.ToColor()!;

    [Theory]
    [InlineData("jpg", "/DCTDecode")]
    [InlineData("tiff", "/FlateDecode")]
    [InlineData("psd", "/FlateDecode")]
    public async Task A_cmyk_picture_stays_cmyk_with_its_profile_in_the_pdf(string format, string filter)
    {
        var pdf = await ToPdfAsync(Cmyk(format));

        using (var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Import))
        {
            var picture = Picture(document);
            Assert.Equal("/ICCBased N=4", ColorSpaceOf(picture));
            Assert.Equal(filter, picture.Elements.GetName("/Filter"));
        }
        using var page = await RenderAsync(pdf);
        // Orange (255, 165, 0) and navy (0, 0, 128) through a print profile: duller, never another hue.
        var orange = At(page, 0.25, 0.5);
        var navy = At(page, 0.75, 0.5);
        Assert.True(orange.R > 200 && orange.G is > 100 and < 200 && orange.B < 90, $"left is {orange}");
        Assert.True(navy.B > navy.R + 40 && navy.B > navy.G + 30 && navy.R < 90, $"right is {navy}");
    }

    [Fact]
    public async Task A_cmyk_picture_without_a_profile_is_device_cmyk()
    {
        var pdf = await ToPdfAsync(Cmyk("tiff", profile: false));

        using (var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Import))
            Assert.Equal("/DeviceCMYK", ColorSpaceOf(Picture(document)));
        using var page = await RenderAsync(pdf);
        var orange = At(page, 0.25, 0.5);
        Assert.True(orange.R > 200 && orange.G is > 100 and < 200 && orange.B < 90, $"left is {orange}");
    }

    [Fact]
    public async Task Lossless_cmyk_samples_are_the_pictures_own()
    {
        var tiff = Cmyk("tiff");
        byte[] expected;
        using (var source = new MagickImage(tiff))
        using (var pixels = source.GetPixels())
            expected = pixels.ToByteArray("CMYK")!;

        using var document = PdfReader.Open(await ToPdfAsync(tiff), PdfDocumentOpenMode.Import);
        var picture = Picture(document);
        using var inflated = new ZLibStream(new MemoryStream(picture.Stream.Value), CompressionMode.Decompress);
        using var samples = new MemoryStream();
        inflated.CopyTo(samples);

        Assert.Equal(expected, samples.ToArray());
    }

    [Fact]
    public async Task A_transparent_part_of_a_cmyk_picture_shows_the_page()
    {
        var pdf = await ToPdfAsync(Cmyk("tiff", transparent: true));

        using (var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Import))
            Assert.NotNull(Picture(document).Elements.GetReference("/SMask"));
        using var page = await RenderAsync(pdf);
        var orange = At(page, 0.25, 0.25);
        var empty = At(page, 0.25, 0.75);
        Assert.True(orange.R > 200 && orange.G is > 100 and < 200 && orange.B < 90, $"top is {orange}");
        Assert.True(empty.R > 240 && empty.G > 240 && empty.B > 240, $"bottom is {empty}");
    }

    [Fact]
    public async Task Rgb_pictures_still_go_in_as_before()
    {
        var dir = fx.NewFolder();
        var png = Path.Combine(dir, "screen.png");
        using (var image = new MagickImage(MagickColors.Orange, 120, 80))
            image.Write(png);

        using var document = PdfReader.Open(await ToPdfAsync(png), PdfDocumentOpenMode.Import);
        Assert.Equal("/DeviceRGB", ColorSpaceOf(Picture(document)));
    }

    [Fact]
    public void The_adobe_marker_is_found_only_before_the_scan()
    {
        using var image = new MagickImage(MagickColors.Orange, 8, 8);
        image.ColorSpace = ColorSpace.CMYK;
        Assert.True(Filee.Engines.Pdf.CmykPictures.HasAdobeMarker(image.ToByteArray(MagickFormat.Jpeg)));
        using var rgb = new MagickImage(MagickColors.Orange, 8, 8);
        Assert.False(Filee.Engines.Pdf.CmykPictures.HasAdobeMarker(rgb.ToByteArray(MagickFormat.Jpeg)));
        Assert.False(Filee.Engines.Pdf.CmykPictures.HasAdobeMarker([0xFF, 0xD8, 0xFF]));
    }
}
