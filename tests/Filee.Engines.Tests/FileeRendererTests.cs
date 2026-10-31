// The renderer's outputs from the page model: a PDF, and pictures of the pages drawn straight from the model, which
// must look like PDFium's drawing of the PDF. The documents are the render matrix's (RenderFeatures).

using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Tests.RenderQuality;
using Filee.Engines.Typesetting;
using PDFtoImage;
using SkiaSharp;

#pragma warning disable CA1416 // PDFtoImage runs on Windows, Linux and macOS, the platforms the tests run on

namespace Filee.Engines.Tests;

public sealed class FileeRendererTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("filee-renderer").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private HDocument Read(string feature)
    {
        var path = RenderFeatures.All.Single(f => f.Name == feature).Write(_folder);
        return HwpxReader.ReadForLayout(path, Path.Combine(_folder, "media"));
    }

    [Theory]
    [InlineData("tables")]
    [InlineData("floating")]
    [InlineData("script-ko")]
    [InlineData("equations")]
    public void Pictures_of_the_pages_look_like_the_pdf(string feature)
    {
        var document = Read(feature);
        const int Dpi = 100;

        var pdf = FileeRenderer.ToPdf(document, TestContext.Current.CancellationToken);
        var pictures = FileeRenderer.ToImages(document, 3 * Dpi, cancellationToken: TestContext.Current.CancellationToken).ToList();

        var pages = GreyImage.Pages(pdf, 3 * Dpi);
        Assert.Equal(pages.Count, pictures.Count);
        for (var i = 0; i < pages.Count; i++)
        {
            // Both at 3× and averaged down, so the two rasterizers' anti-aliasing fades.
            using var bitmap = SKBitmap.Decode(pictures[i]);
            var pdfium = Average(pages[i], 3);
            // Same glyphs at the same places; PDFium draws horizontal stems a little darker and snapped to pixels, which
            // keeps SSIM of text-heavy pages near 0.96 (no pixel differs by more than a quarter of the grey range).
            var (ssim, off) = Ssim.Compare(pdfium, GreyImage.From(bitmap, 3));
            Assert.True(ssim >= 0.95 && off < 0.002, $"page {i + 1}: SSIM {ssim:F4}, {off:P2} of the pixels differ");
        }
    }

    [Fact]
    public void A_text_page_is_small_apart_from_its_fonts()
    {
        var pdf = FileeRenderer.ToPdf(Read("script-ko"), TestContext.Current.CancellationToken);

        var fonts = PdfChecks.ListFonts(pdf).Sum(f => f.Length);
        Assert.True(pdf.Length - fonts < 50_000, $"{pdf.Length:N0} bytes, {fonts:N0} of them fonts");
    }

    [Fact]
    public void Pictures_come_as_png_or_jpeg_with_progress_by_page()
    {
        var document = Read("headers-footers");
        var reported = new List<double>();
        var progress = new SynchronousProgress(reported.Add);

        var png = FileeRenderer.ToImages(document, 72, progress: progress, cancellationToken: TestContext.Current.CancellationToken).ToList();
        var jpeg = FileeRenderer.ToImages(document, 72, SKEncodedImageFormat.Jpeg, 80, cancellationToken: TestContext.Current.CancellationToken).ToList();

        Assert.Equal(2, png.Count);
        Assert.Equal([0.5, 1.0], reported);
        using (var page = SKBitmap.Decode(png[0]))
        {
            // A4 at 72 dpi.
            Assert.Equal(595, page.Width);
            Assert.Equal(842, page.Height);
        }
        Assert.All(png, p => Assert.Equal(SKEncodedImageFormat.Png, SKCodec.Create(new SKMemoryStream(p)).EncodedFormat));
        Assert.All(jpeg, p => Assert.Equal(SKEncodedImageFormat.Jpeg, SKCodec.Create(new SKMemoryStream(p)).EncodedFormat));
    }

    [Fact]
    public void Drawing_pictures_stops_when_cancelled()
    {
        var document = Read("headers-footers");
        using var cancel = new CancellationTokenSource();

        var pages = FileeRenderer.ToImages(document, 72, cancellationToken: cancel.Token);
        using var pictures = pages.GetEnumerator();
        Assert.True(pictures.MoveNext());
        cancel.Cancel();

        Assert.Throws<OperationCanceledException>(() => pictures.MoveNext());
    }

    /// <summary><paramref name="image"/> averaged over <paramref name="factor"/>² blocks.</summary>
    private static GreyImage Average(GreyImage image, int factor)
    {
        int width = image.Width / factor, height = image.Height / factor;
        var pixels = new double[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                double sum = 0;
                for (var j = 0; j < factor; j++)
                    for (var i = 0; i < factor; i++)
                        sum += image[x * factor + i, y * factor + j];
                pixels[y * width + x] = sum / (factor * factor);
            }
        }
        return new GreyImage(width, height, pixels);
    }

    /// <summary>Reports on the calling thread (Progress&lt;T&gt; posts to the thread pool).</summary>
    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
