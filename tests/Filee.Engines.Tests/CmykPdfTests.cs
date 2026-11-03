// CMYK print files (issue #1027): a PDF or Illustrator file drawn in CMYK comes out as a CMYK JPG or TIFF with its ink
// values when Ghostscript is installed (those tests are skipped without it); RGB documents and other targets keep
// PDFium's RGB rendering. The documents are written with PDFsharp in CMYK colour mode, so their fills use the k operator.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Pdf;
using Filee.Engines.Vector;
using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Filee.Engines.Tests;

public class CmykPdfTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static bool GhostscriptInstalled => GhostscriptConverter.Locate() is not null;

    private const string NoGhostscript = "Ghostscript is not installed (pwsh build/fetch-engines.ps1 -Only ghostscript).";

    /// <summary>The fill of the left half of every test page (C, M, Y, K from 0 to 1).</summary>
    private static readonly double[] Left = [0.1, 0.2, 0.3, 0.4];

    /// <summary>The fill of the right half.</summary>
    private static readonly double[] Right = [0.8, 0, 0.6, 0.1];

    private static void AssertDone(ConversionJob job)
    {
        var errors = string.Join("; ", job.Files.Where(f => f.State == FileState.Failed).Select(f => $"{f.ErrorKey} {f.ErrorDetail}"));
        Assert.True(job.State == JobState.Completed, $"{job.State}: {errors}");
        Assert.All(job.Outputs, o => Assert.True(new FileInfo(o).Length > 0, o));
    }

    /// <summary>
    /// A PDF of 200 x 100 pt pages, each half filled with <see cref="Left"/> and <see cref="Right"/> (the second page
    /// of three is 300 pt wide, so stacked pages need padding). In RGB mode PDFsharp writes the same colours as RGB.
    /// </summary>
    private static string MakePdf(string folder, string name, bool cmyk = true, int pages = 1, byte[]? outputProfile = null)
    {
        using var document = new PdfDocument();
        document.Options.ColorMode = cmyk ? PdfColorMode.Cmyk : PdfColorMode.Rgb;
        for (var i = 0; i < pages; i++)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(i == 1 ? 300 : 200);
            page.Height = XUnit.FromPoint(100);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawRectangle(new XSolidBrush(XColor.FromCmyk(Left[0], Left[1], Left[2], Left[3])), 0, 0, 100, 100);
            gfx.DrawRectangle(new XSolidBrush(XColor.FromCmyk(Right[0], Right[1], Right[2], Right[3])), 100, 0, 100, 100);
        }
        if (outputProfile is not null)
            AddOutputIntent(document, outputProfile);
        var path = Path.Combine(folder, name);
        document.Save(path);
        return path;
    }

    /// <summary>A PDF/X-style output intent naming the printing condition by its ICC profile.</summary>
    private static void AddOutputIntent(PdfDocument document, byte[] profile)
    {
        var stream = new PdfDictionary(document);
        stream.CreateStream(profile);
        stream.Elements.SetInteger("/N", profile.AsSpan(16, 4).SequenceEqual("CMYK"u8) ? 4 : 3);
        document.Internals.AddObject(stream);
        var intent = new PdfDictionary(document);
        intent.Elements.SetName("/Type", "/OutputIntent");
        intent.Elements.SetName("/S", "/GTS_PDFX");
        intent.Elements.SetString("/OutputConditionIdentifier", "Test condition");
        intent.Elements.SetReference("/DestOutputProfile", stream);
        document.Internals.Catalog.Elements["/OutputIntents"] = new PdfArray(document, intent);
    }

    /// <summary>The channels of one pixel (C, M, Y, K for a CMYK picture).</summary>
    private static byte[] Channels(IMagickImage<byte> image, int x, int y) =>
        image.GetPixels().GetPixel(x, y).ToArray()[..(int)image.ChannelCount];

    /// <summary>The CMYK pixel holds <paramref name="expected"/> within a JPEG's rounding.</summary>
    private static void AssertInk(IMagickImage<byte> image, int x, int y, double[] expected)
    {
        var actual = Channels(image, x, y);
        Assert.True(actual.Length >= 4, $"{actual.Length} channels");
        for (var channel = 0; channel < 4; channel++)
            Assert.True(Math.Abs(actual[channel] - expected[channel] * 255) <= 3,
                $"channel {channel}: {string.Join(',', actual)} instead of {string.Join(',', expected.Select(v => Math.Round(v * 255)))}");
    }

    private static void AssertCmykPage(IMagickImage<byte> image)
    {
        Assert.Equal(ColorSpace.CMYK, image.ColorSpace);
        AssertInk(image, (int)image.Width / 4, (int)image.Height / 2, Left);
        AssertInk(image, (int)image.Width * 3 / 4, (int)image.Height / 2, Right);
    }

    // ───────────────────────── Ghostscript renders CMYK ─────────────────────────

    [Fact]
    public async Task Cmyk_pdf_becomes_a_cmyk_jpg_with_its_ink_values()
    {
        Assert.SkipUnless(GhostscriptInstalled, NoGhostscript);
        var dir = fx.NewFolder();
        var pdf = MakePdf(dir, "print.pdf");

        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "jpg", Image = { Quality = 95 } });

        AssertDone(job);
        using var image = new MagickImage(job.Outputs.Single());
        Assert.Equal(MagickFormat.Jpeg, image.Format);
        // 200 x 100 pt at the default 150 dpi, as PDFium renders it.
        Assert.Equal((417u, 208u), (image.Width, image.Height));
        AssertCmykPage(image);
        // No output intent: untagged, not Ghostscript's own default profile.
        Assert.Null(image.GetColorProfile());
    }

    [Fact]
    public async Task Cmyk_illustrator_file_becomes_a_cmyk_jpg()
    {
        Assert.SkipUnless(GhostscriptInstalled, NoGhostscript);
        var dir = fx.NewFolder();
        var ai = Path.ChangeExtension(MakePdf(dir, "artwork.pdf"), ".ai");
        File.Move(Path.ChangeExtension(ai, ".pdf"), ai);

        var job = await fx.ConvertAsync([ai], new Preset { TargetFormat = "jpg", Pdf = { RenderDpi = 72 } });

        AssertDone(job);
        using var image = new MagickImage(job.Outputs.Single());
        Assert.Equal((200u, 100u), (image.Width, image.Height));
        AssertCmykPage(image);
    }

    [Fact]
    public async Task Cmyk_pdf_becomes_a_multi_page_cmyk_tiff()
    {
        Assert.SkipUnless(GhostscriptInstalled, NoGhostscript);
        var dir = fx.NewFolder();
        var pdf = MakePdf(dir, "book.pdf", pages: 3);

        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "tiff", Pdf = { RenderDpi = 72, PageRange = "2-3" } });

        AssertDone(job);
        using var pages = new MagickImageCollection(job.Outputs.Single());
        Assert.Equal(2, pages.Count);
        Assert.Equal([300u, 200u], pages.Select(p => p.Width));
        foreach (var page in pages)
        {
            Assert.Equal(ColorSpace.CMYK, page.ColorSpace);
            AssertInk(page, 50, 50, Left);
            AssertInk(page, 150, 50, Right);
        }
        // The second page's empty right part is paper: no ink at all.
        Assert.Equal([0, 0, 0, 0], Channels(pages[0], 250, 50));
    }

    [Fact]
    public async Task Cmyk_pages_stack_into_one_cmyk_jpg_on_blank_paper()
    {
        Assert.SkipUnless(GhostscriptInstalled, NoGhostscript);
        var dir = fx.NewFolder();
        var pdf = MakePdf(dir, "strip.pdf", pages: 2);

        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "jpg", Pdf = { RenderDpi = 72, CombinePages = true } });

        AssertDone(job);
        using var image = new MagickImage(job.Outputs.Single());
        Assert.Equal((300u, 200u), (image.Width, image.Height));
        Assert.Equal(ColorSpace.CMYK, image.ColorSpace);
        AssertInk(image, 50 + 50, 50, Left); // the 200 pt page is centred on the 300 pt strip
        AssertInk(image, 50, 150, Left);
        // The padding beside the narrower page is blank paper too, not black ink.
        Assert.All(Channels(image, 10, 50), c => Assert.True(c <= 3, string.Join(',', Channels(image, 10, 50))));
    }

    [Fact]
    public async Task Output_intent_profile_goes_into_the_jpg_and_the_ink_values_stay()
    {
        Assert.SkipUnless(GhostscriptInstalled, NoGhostscript);
        var dir = fx.NewFolder();
        var swop = ColorProfiles.USWebCoatedSWOP.ToByteArray();
        var pdf = MakePdf(dir, "pdfx.pdf", outputProfile: swop);

        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "jpg", Pdf = { RenderDpi = 72 } });

        AssertDone(job);
        using var image = new MagickImage(job.Outputs.Single());
        AssertCmykPage(image);
        var profile = image.GetColorProfile();
        Assert.NotNull(profile);
        Assert.Equal(swop, profile.ToByteArray());
    }

    // ───────────────────────── PDFium as before ─────────────────────────

    [Fact]
    public async Task Rgb_pdf_still_becomes_an_rgb_jpg()
    {
        var dir = fx.NewFolder();
        var pdf = MakePdf(dir, "screen.pdf", cmyk: false);

        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "jpg", Pdf = { RenderDpi = 72 } });

        AssertDone(job);
        using var image = new MagickImage(job.Outputs.Single());
        Assert.Equal(ColorSpace.sRGB, image.ColorSpace);
        Assert.Equal((200u, 100u), (image.Width, image.Height));
    }

    [Fact]
    public async Task Cmyk_pdf_still_becomes_an_rgb_png()
    {
        var dir = fx.NewFolder();
        var pdf = MakePdf(dir, "print.pdf");

        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "png", Pdf = { RenderDpi = 72 } });

        AssertDone(job);
        using var image = new MagickImage(job.Outputs.Single());
        Assert.Equal(ColorSpace.sRGB, image.ColorSpace);
        Assert.Equal((200u, 100u), (image.Width, image.Height));
    }

    [Fact]
    public async Task Grayscale_preset_keeps_pdfium_for_cmyk_pages()
    {
        var dir = fx.NewFolder();
        var pdf = MakePdf(dir, "print.pdf");

        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "jpg", Pdf = { RenderDpi = 72 }, Image = { Grayscale = true } });

        AssertDone(job);
        using var image = new MagickImage(job.Outputs.Single());
        Assert.NotEqual(ColorSpace.CMYK, image.ColorSpace);
    }

    // ───────────────────────── Detection ─────────────────────────

    [Fact]
    public void Cmyk_fills_are_found_and_rgb_ones_are_not()
    {
        var dir = fx.NewFolder();
        var cmyk = MakePdf(dir, "cmyk.pdf");
        var rgb = MakePdf(dir, "rgb.pdf", cmyk: false);

        Assert.Equal(new PdfCmykContent.Findings(true, null), PdfCmykContent.Inspect(cmyk, [0]));
        Assert.False(PdfCmykContent.Inspect(rgb, [0]).UsesCmyk);
    }

    [Theory]
    [InlineData("DeviceCMYK", true)]
    [InlineData("ICCBased", true)]
    [InlineData("DeviceRGB", false)]
    public void A_picture_alone_decides_by_its_colour_space(string space, bool cmyk)
    {
        var dir = fx.NewFolder();
        var pdf = Path.Combine(dir, $"{space}.pdf");
        using (var document = new PdfDocument())
        {
            var page = document.AddPage();
            var picture = new PdfDictionary(document);
            picture.Elements.SetName("/Type", "/XObject");
            picture.Elements.SetName("/Subtype", "/Image");
            picture.Elements.SetInteger("/Width", 2);
            picture.Elements.SetInteger("/Height", 2);
            picture.Elements.SetInteger("/BitsPerComponent", 8);
            if (space == "ICCBased")
            {
                var profile = new PdfDictionary(document);
                profile.CreateStream(ColorProfiles.USWebCoatedSWOP.ToByteArray());
                profile.Elements.SetInteger("/N", 4);
                document.Internals.AddObject(profile);
                picture.Elements["/ColorSpace"] = new PdfArray(document, new PdfName("/ICCBased"), profile.Reference!);
            }
            else
            {
                picture.Elements.SetName("/ColorSpace", "/" + space);
            }
            picture.CreateStream([.. Enumerable.Repeat((byte)128, 2 * 2 * (space == "DeviceRGB" ? 3 : 4))]);
            document.Internals.AddObject(picture);
            var pictures = new PdfDictionary(document);
            pictures.Elements.SetReference("/Im0", picture);
            page.Resources.Elements["/XObject"] = pictures;
            page.Contents.AppendContent().CreateStream("q 64 0 0 64 10 10 cm /Im0 Do Q"u8.ToArray());
            document.Save(pdf);
        }

        Assert.Equal(cmyk, PdfCmykContent.Inspect(pdf, [0]).UsesCmyk);
    }

    [Fact]
    public void Only_a_cmyk_output_intent_profile_is_taken()
    {
        var dir = fx.NewFolder();
        var swop = ColorProfiles.USWebCoatedSWOP.ToByteArray();
        var cmyk = MakePdf(dir, "swop.pdf", outputProfile: swop);
        var srgb = MakePdf(dir, "srgb.pdf", outputProfile: ColorProfiles.SRGB.ToByteArray());

        Assert.Equal(swop, PdfCmykContent.Inspect(cmyk, [0]).OutputProfile);
        var rgbIntent = PdfCmykContent.Inspect(srgb, [0]);
        Assert.True(rgbIntent.UsesCmyk);
        Assert.Null(rgbIntent.OutputProfile);
    }

    [Fact]
    public void Pages_outside_the_selection_are_not_looked_at()
    {
        var dir = fx.NewFolder();
        var pdf = MakePdf(dir, "cmyk.pdf");

        Assert.False(PdfCmykContent.Inspect(pdf, [5]).UsesCmyk);
        Assert.False(PdfCmykContent.Inspect(pdf, []).UsesCmyk);
    }

    [Fact]
    public void Malformed_pdf_counts_as_not_cmyk()
    {
        var dir = fx.NewFolder();
        var broken = Path.Combine(dir, "broken.pdf");
        File.WriteAllText(broken, "%PDF-1.7\n1 0 obj << /Type /Catalog /Pages 9 0 R >> endobj\n0 0 0 1 k garbage\n%%EOF");
        var cut = Path.Combine(dir, "cut.pdf");
        var whole = File.ReadAllBytes(MakePdf(dir, "whole.pdf"));
        File.WriteAllBytes(cut, whole[..(whole.Length / 2)]);

        Assert.Equal(default, PdfCmykContent.Inspect(broken, [0]));
        Assert.False(PdfCmykContent.Inspect(Path.Combine(dir, "missing.pdf"), [0]).UsesCmyk);
        // Half a file: whatever PdfPig still reads, it answers without throwing.
        _ = PdfCmykContent.Inspect(cut, [0]);
    }

    // ───────────────────────── Routes ─────────────────────────

    [Theory]
    [InlineData("ai", "jpg", "vector,pdfium")]
    [InlineData("ai", "tiff", "vector,pdfium")]
    [InlineData("pdf", "jpg", "pdfium")]
    public void Cmyk_capable_targets_are_reached_through_pdfium(string from, string to, string engines)
    {
        var route = fx.Catalog.CreatePlanner(["ghostscript"]).Plan(from, to);

        Assert.NotNull(route);
        Assert.Equal(engines, string.Join(',', route.Steps.Select(s => s.Converter.Id)));
    }
}
