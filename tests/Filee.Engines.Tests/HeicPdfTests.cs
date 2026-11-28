// HEIC photos → PDF go in as JPEG, like JPG photos (#1873): stored losslessly, a 1.7 MB phone photo made a 13 MB PDF.
// ImageMagick here has no HEVC encoder, so it can't write a HEIC to read back: the steps say "heic" and the frame is
// read from a PNG (ImageMagick reads by content, not by the format the step names), as the HEIC decoder would hand it.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Pdf;
using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Tests;

public class HeicPdfTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>A photo-like picture (soft colour fields with sensor noise), optionally half see-through, as a PNG.</summary>
    private string Photo(bool transparent = false)
    {
        using var image = new MagickImage("plasma:", 480, 360);
        image.AddNoise(NoiseType.Gaussian, 0.5);
        if (transparent)
        {
            image.Alpha(AlphaAction.Set);
            image.Evaluate(Channels.Alpha, EvaluateOperator.Multiply, 0.5);
        }
        var path = Path.Combine(fx.NewFolder(), "photo.png");
        image.Write(path, MagickFormat.Png);
        return path;
    }

    /// <summary>The picture as a PDF, through PDFsharp's image → PDF step from <paramref name="from"/>.</summary>
    private async Task<string> ToPdfAsync(string picture, string from)
    {
        var dir = fx.NewFolder();
        var output = Path.Combine(dir, "photo.pdf");
        var step = new ConversionStep(picture, from, "pdf", new Preset { TargetFormat = "pdf" }, new SinglePath(output), dir);
        var written = await new PdfSharpConverter().ConvertAsync(step, null, TestContext.Current.CancellationToken);
        return written.Single();
    }

    /// <summary>The pictures on every page of the PDF.</summary>
    private static List<PdfDictionary> Pictures(PdfDocument document) =>
        [.. document.Pages.Cast<PdfPage>().SelectMany(page => page.Resources.Elements.GetDictionary("/XObject")!.Elements.Values)
            .Select(item => (PdfDictionary)((PdfReference)item!).Value)];

    private static string FilterOf(string pdf)
    {
        using var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        return Pictures(document).Single().Elements.GetName("/Filter");
    }

    [Theory]
    [InlineData("heic", "/DCTDecode")]
    [InlineData("jpg", "/DCTDecode")]
    [InlineData("png", "/FlateDecode")]
    [InlineData("avif", "/FlateDecode")]
    [InlineData("webp", "/FlateDecode")]
    public async Task Lossy_photos_go_into_the_pdf_as_jpeg_others_losslessly(string from, string filter)
    {
        Assert.Equal(filter, FilterOf(await ToPdfAsync(Photo(), from)));
    }

    [Fact]
    public async Task A_heic_photo_makes_a_far_smaller_pdf_than_a_lossless_one()
    {
        var photo = Photo();
        var heic = new FileInfo(await ToPdfAsync(photo, "heic")).Length;
        var lossless = new FileInfo(await ToPdfAsync(photo, "png")).Length;

        Assert.True(heic * 3 < lossless, $"HEIC PDF {heic} bytes, lossless PDF {lossless} bytes");
    }

    [Fact]
    public async Task A_heic_photo_with_transparency_stays_lossless()
    {
        using var document = PdfReader.Open(await ToPdfAsync(Photo(transparent: true), "heic"), PdfDocumentOpenMode.Import);
        var picture = Pictures(document).Single();

        Assert.Equal("/FlateDecode", picture.Elements.GetName("/Filter"));
        Assert.NotNull(picture.Elements.GetReference("/SMask"));
    }

    [Fact]
    public async Task A_cmyk_heic_photo_goes_in_as_cmyk_jpeg()
    {
        var path = Path.Combine(fx.NewFolder(), "print.tiff");
        using (var image = new MagickImage("plasma:", 160, 120))
        {
            image.SetProfile(ColorProfiles.SRGB);
            image.TransformColorSpace(ColorProfiles.SRGB, ColorProfiles.USWebCoatedSWOP);
            image.Write(path);
        }

        Assert.Equal("/DCTDecode", FilterOf(await ToPdfAsync(path, "heic")));
    }

    [Fact]
    public async Task Merged_heic_photos_stay_jpeg_in_the_merged_pdf()
    {
        // Merge PDF turns each file into a PDF through the same step, then imports the pages as they are.
        var first = await ToPdfAsync(Photo(), "heic");
        var second = await ToPdfAsync(Photo(), "heic");
        var merged = Path.Combine(fx.NewFolder(), "merged.pdf");
        PdfSharpConverter.Merge([new(first, "first.heic"), new(second, "second.heic")], merged, sourceBookmarks: false,
            paper: null, TestContext.Current.CancellationToken);

        using var document = PdfReader.Open(merged, PdfDocumentOpenMode.Import);
        Assert.Equal(2, document.PageCount);
        Assert.All(Pictures(document), picture => Assert.Equal("/DCTDecode", picture.Elements.GetName("/Filter")));
    }

    [Theory]
    [InlineData("jpg", true)]
    [InlineData("heic", true)]
    [InlineData("png", false)]
    [InlineData("avif", false)]
    [InlineData("webp", false)]
    [InlineData("tiff", false)]
    public void Only_jpeg_and_heic_are_lossy_photos(string format, bool lossy) =>
        Assert.Equal(lossy, PdfSharpConverter.IsLossyPhoto(format));

    private sealed class SinglePath(string path) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => path;
    }
}
