// PDF → PostScript / EPS with Filee's own writer (Pdf/PostScript). The PDFs are built in code (PostScriptSamples):
// paths, every kind of font, pictures, shadings, patterns, turned pages, annotations, optional content and
// transparency. Where Ghostscript is installed, it renders Filee's PostScript and the pages must look like PDFium's
// rendering of the PDF (SSIM ≥ 0.98); the other tests check the files themselves and run everywhere.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Pdf;
using Filee.Engines.Pdf.PostScript;
using Filee.Engines.Tests.RenderQuality;
using Filee.Engines.Vector;
using SkiaSharp;

namespace Filee.Engines.Tests;

public class PdfPostScriptTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const int Dpi = 100;

    /// <summary>Pages are drawn at 3 × <see cref="Dpi"/> and averaged down, so the renderers' anti-aliasing matters less.</summary>
    private const int Supersampling = 3;
    private const double MinSsim = 0.98;

    private static string? Ghostscript => GhostscriptConverter.Locate();

    private const string NoGhostscript = "Ghostscript is not installed (pwsh build/fetch-engines.ps1 -Only ghostscript).";

    public static TheoryData<string> Samples() => [.. PostScriptSamples.Names];

    // ───────────────────────── rendering ─────────────────────────

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Ghostscript_renders_the_postscript_like_the_pdf(string sample)
    {
        Assert.SkipUnless(Ghostscript is not null, NoGhostscript);
        var folder = fx.NewFolder();
        var pdf = PostScriptSamples.Make(sample, folder);
        var ps = Path.Combine(folder, sample + ".ps");
        var writer = new PdfToPostScript(pdf, 150, null, TestContext.Current.CancellationToken);
        writer.Write([.. Enumerable.Range(0, writer.PageCount)], ps, eps: false, folder);
        foreach (var (page, reason) in writer.RasterPages)
            TestContext.Current.TestOutputHelper?.WriteLine($"{sample} page {page} drawn as a picture: {reason}");

        if (Environment.GetEnvironmentVariable("FILEE_PS_COMPARE_OUT") is { Length: > 0 } keep)
        {
            Directory.CreateDirectory(keep);
            File.Copy(pdf, Path.Combine(keep, sample + ".pdf"), overwrite: true);
            File.Copy(ps, Path.Combine(keep, sample + ".ps"), overwrite: true);
#pragma warning disable CA1416
            var colour = PDFtoImage.Conversion.ToImages(File.ReadAllBytes(pdf), null,
                new PDFtoImage.RenderOptions(Dpi: Dpi, WithAnnotations: true, WithFormFill: true, BackgroundColor: SKColors.White));
#pragma warning restore CA1416
            var number = 0;
            foreach (var bitmap in colour)
            {
                using (bitmap)
                using (var png = bitmap.Encode(SKEncodedImageFormat.Png, 100))
                    File.WriteAllBytes(Path.Combine(keep, $"{sample}-{++number}-pdfium-colour.png"), png.ToArray());
            }
        }
        // PDFium draws the PDF; Ghostscript draws Filee's PostScript, and also the PDF itself, which shows what of a
        // difference is the two renderers' (substitute fonts, anti-aliasing, CMYK) and what the translation's.
        var pdfium = PdfiumPages(pdf);
        var rendered = await RenderAsync(ps, eps: false, folder);
        var direct = await RenderAsync(pdf, eps: false, folder);
        Assert.Equal(pdfium.Count, rendered.Count);
        var failures = new List<string>();
        for (var i = 0; i < pdfium.Count; i++)
        {
            var (ssim, off) = Ssim.Compare(pdfium[i], rendered[i]);
            var (same, _) = Ssim.Compare(direct[i], rendered[i]);
            var picture = writer.RasterPages.Any(r => r.Page == i + 1);
            TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"| {sample} | {i + 1} | {ssim:0.0000} | {same:0.0000} | {off:P2} | {(picture ? "picture" : "translated")} |"));
            if (ssim < MinSsim && !RendererDifferences.Contains(sample))
                failures.Add($"page {i + 1}: SSIM against PDFium {ssim:0.0000}");
            if (same < MinSsim)
                failures.Add($"page {i + 1}: SSIM against Ghostscript's rendering of the PDF {same:0.0000}");
            if (Environment.GetEnvironmentVariable("FILEE_PS_COMPARE_OUT") is { Length: > 0 } outFolder)
            {
                Directory.CreateDirectory(outFolder);
                File.WriteAllBytes(Path.Combine(outFolder, $"{sample}-{i + 1}-pdfium.png"), pdfium[i].ToPng());
                File.WriteAllBytes(Path.Combine(outFolder, $"{sample}-{i + 1}-ps.png"), rendered[i].ToPng());
                File.WriteAllBytes(Path.Combine(outFolder, $"{sample}-{i + 1}-gspdf.png"), direct[i].ToPng());
            }
        }
        Assert.True(failures.Count == 0, $"{sample}: {string.Join("; ", failures)}");
    }

    /// <summary>
    /// Samples PDFium and Ghostscript draw differently whatever the PostScript says, where Ghostscript's own rendering of
    /// the PDF is the fair reference: fonts that aren't embedded (each has its own substitutes), tiling pattern cells
    /// (placed and anti-aliased differently) and a bitmap Type 3 glyph (PDFium smooths a magnified image mask).
    /// </summary>
    private static readonly string[] RendererDifferences = ["text-standard", "patterns", "text-type3"];

    /// <summary>Pages drawn by PDFium (annotations included) at <see cref="Dpi"/>, averaged down from 3 × the resolution.</summary>
    internal static List<GreyImage> PdfiumPages(string pdf)
    {
        var pages = new List<GreyImage>();
        var options = new PDFtoImage.RenderOptions(Dpi: Dpi * Supersampling, WithAnnotations: true, WithFormFill: true, BackgroundColor: SKColors.White);
#pragma warning disable CA1416
        foreach (var bitmap in PDFtoImage.Conversion.ToImages(File.ReadAllBytes(pdf), null, options))
#pragma warning restore CA1416
        {
            using (bitmap)
                pages.Add(GreyImage.From(bitmap, Supersampling));
        }
        return pages;
    }

    [Fact]
    public async Task Eps_pages_are_rendered_like_the_pdf_pages()
    {
        Assert.SkipUnless(Ghostscript is not null, NoGhostscript);
        var folder = fx.NewFolder();
        var pdf = PostScriptSamples.Rotated(folder);
        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "eps" });
        AssertDone(job);
        var reference = PdfiumPages(pdf);
        var outputs = job.Outputs.Order(StringComparer.Ordinal).ToList();
        Assert.Equal(2, outputs.Count);
        for (var i = 0; i < outputs.Count; i++)
        {
            var rendered = Assert.Single(await RenderAsync(outputs[i], eps: true, folder));
            var (ssim, _) = Ssim.Compare(reference[i], rendered);
            Assert.True(ssim >= MinSsim, $"EPS page {i + 1}: SSIM {ssim:0.0000}");
        }
    }

    // ───────────────────────── files ─────────────────────────

    [Fact]
    public async Task Pdf_converts_to_postscript_and_eps_without_ghostscript()
    {
        var folder = fx.NewFolder();
        var pdf = PostScriptSamples.Rotated(folder);
        var catalog = new ConverterCatalog(fx.Converters.Where(c => c is not GhostscriptConverter)) { Priority = fx.Catalog.Priority };
        Assert.Equal("pdf-ps", Assert.Single(catalog.CreatePlanner([]).Plan("pdf", "ps")!.Steps).Converter.Id);

        var ps = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "ps" });
        var eps = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "eps", Pdf = { PageRange = "2" } });

        AssertDone(ps);
        AssertDone(eps);
        var text = File.ReadAllText(ps.Outputs.Single());
        Assert.StartsWith("%!PS-Adobe-3.0\n", text, StringComparison.Ordinal);
        Assert.Contains("%%LanguageLevel: 3", text, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(text, "^%%Page: ", RegexOptions.Multiline).Count);
        // The first page is turned (/Rotate 90) and cropped: 720 × 550 points.
        Assert.Contains("%%PageBoundingBox: 0 0 720 550", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);

        var single = File.ReadAllText(eps.Outputs.Single());
        Assert.Equal("rotated.eps", Path.GetFileName(eps.Outputs.Single()));
        Assert.StartsWith("%!PS-Adobe-3.0 EPSF-3.0\n", single, StringComparison.Ordinal);
        Assert.Contains("%%BoundingBox: 0 0 400 300\n", single, StringComparison.Ordinal);
        Assert.DoesNotContain("setpagedevice", single, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Files_are_clean_7_bit_dsc(string sample)
    {
        var folder = fx.NewFolder();
        var pdf = PostScriptSamples.Make(sample, folder);
        var ps = Path.Combine(folder, "out.ps");
        var writer = new PdfToPostScript(pdf, 150, null, TestContext.Current.CancellationToken);
        writer.Write([.. Enumerable.Range(0, writer.PageCount)], ps, eps: false, folder);
        var bytes = File.ReadAllBytes(ps);
        Assert.All(bytes, b => Assert.True(b is 10 or (>= 32 and < 127), $"byte {b}"));
        var lines = Encoding.ASCII.GetString(bytes).Split('\n');
        Assert.All(lines, l => Assert.True(l.Length < 255, l[..Math.Min(60, l.Length)]));
        // Only Filee's own comments start with %% (no data line can).
        Assert.All(lines.Where(l => l.StartsWith("%%", StringComparison.Ordinal)), l =>
            Assert.Matches(@"^%%(\w+:?|BeginResource: .*|EndResource|IncludeResource: font [\w-]+|Page: \d+ \d+|PageBoundingBox: [\d ]+|BoundingBox: [\d ]+|HiResBoundingBox: [\d. ]+|Title: .*|Creator: Filee|LanguageLevel: 3|Pages: \d+|DocumentData: Clean7Bit)$", l));
        Assert.Equal(sample == "transparency" ? [1] : [], writer.RasterPages.Select(r => r.Page));
    }

    [Fact]
    public async Task Transparent_pages_become_pictures_with_a_notice()
    {
        var folder = fx.NewFolder();
        var pdf = PostScriptSamples.Transparency(folder);
        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "ps" });
        AssertDone(job);
        Assert.Equal("notice.ps_pages_as_pictures", job.Files.Single().NoticeKey);
        var text = File.ReadAllText(job.Outputs.Single());
        var pages = text.Split("%%Page: ");
        Assert.Contains("/DeviceRGB setcolorspace", pages[1], StringComparison.Ordinal); // the picture
        Assert.DoesNotContain("FileeImage", pages[2], StringComparison.Ordinal);       // translated
        Assert.Contains("100 100 400 400 re", pages[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Font_programs_are_never_copied()
    {
        // A Type 1 font's cleartext and private parts are PostScript: a hostile one must not reach the output.
        var folder = fx.NewFolder();
        using var b = new PostScriptPdfBuilder();
        var program = PostScriptPdfBuilder.Type1Program("FileeEvil");
        var text = Encoding.Latin1.GetString(program).Replace("/PaintType 0 def", "/PaintType 0 def (x) deletefile", StringComparison.Ordinal);
        var descriptor = b.Descriptor("FileeEvil", 32, "FontFile", b.Type1File(Encoding.Latin1.GetBytes(text)));
        var font = b.SimpleFont("Type1", "FileeEvil", null, descriptor, _ => 600);
        b.Page(300, 200, "BT /F1 40 Tf 20 100 Td (ABC) Tj ET", b.Resources(("Font", "F1", font)));
        var pdf = b.Save(folder, "evil.pdf");
        var ps = Path.Combine(folder, "evil.ps");
        var writer = new PdfToPostScript(pdf, 150, null, TestContext.Current.CancellationToken);
        writer.Write([0], ps, eps: false, folder);
        var output = File.ReadAllText(ps);
        Assert.Empty(writer.RasterPages);
        Assert.DoesNotContain("deletefile", output, StringComparison.Ordinal);
        Assert.DoesNotContain("FileeEvil", output, StringComparison.Ordinal);
        Assert.Contains("FontType 1", output, StringComparison.Ordinal);
    }

    // ───────────────────────── hostile input ─────────────────────────

    public static TheoryData<string, string> HostileContent() => new()
    {
        { "deep arrays", new string('[', 5000) + " TJ" },
        { "deep q", string.Concat(Enumerable.Repeat("q ", 5000)) + "0 0 10 10 re f" },
        { "garbage", "1 2 3 ) ] >> BT ET Tf Tj TJ cm cm 1 cm (unclosed" },
        { "huge picture", "BI /W 100000 /H 100000 /CS /G /BPC 8 ID \0\0 EI" },
        { "unknown operators", "0 0 m 10 10 l xyzzy deletefile (x) run S" },
        { "no font", "BT (text without a font) Tj ET" },
        { "missing resources", "/Nope gs /Nope Do /Nope sh /Nope cs 1 scn 0 0 10 10 re f BT /Nope 12 Tf (x) Tj ET" },
    };

    [Theory]
    [MemberData(nameof(HostileContent))]
    public void Hostile_content_is_translated_or_drawn_as_a_picture(string name, string content)
    {
        var folder = fx.NewFolder();
        using var b = new PostScriptPdfBuilder();
        b.Page(200, 200, content);
        var pdf = b.Save(folder, "hostile.pdf");
        var ps = Path.Combine(folder, "hostile.ps");
        var writer = new PdfToPostScript(pdf, 150, null, TestContext.Current.CancellationToken);
        writer.Write([0], ps, eps: false, folder);
        var output = File.ReadAllText(ps);
        Assert.DoesNotContain("deletefile", output, StringComparison.Ordinal);
        Assert.DoesNotContain("xyzzy", output, StringComparison.Ordinal);
        TestContext.Current.TestOutputHelper?.WriteLine($"{name}: {(writer.RasterPages.Count > 0 ? writer.RasterPages[0].Reason : "translated")}");
    }

    [Fact]
    public void Hostile_functions_and_fonts_are_refused()
    {
        var folder = fx.NewFolder();
        using var b = new PostScriptPdfBuilder();
        // A calculator function with an operator that isn't one, a sampled function with billions of samples, a damaged TrueType font.
        var loop = b.Stream("{ 1 { (x) deletefile } if }", ("FunctionType", PostScriptPdfBuilder.Number(4)), ("Domain", b.Numbers(0, 1)), ("Range", b.Numbers(0, 1)));
        var huge = b.Stream([0], false, ("FunctionType", PostScriptPdfBuilder.Number(0)), ("Domain", b.Numbers(0, 1, 0, 1)), ("Range", b.Numbers(0, 1)),
            ("Size", b.Numbers(100000, 100000)), ("BitsPerSample", PostScriptPdfBuilder.Number(8)));
        var space = b.Array(PostScriptPdfBuilder.Name("Separation"), PostScriptPdfBuilder.Name("X"), PostScriptPdfBuilder.Name("DeviceRGB"), loop.Reference!);
        var space2 = b.Array(PostScriptPdfBuilder.Name("Separation"), PostScriptPdfBuilder.Name("Y"), PostScriptPdfBuilder.Name("DeviceGray"), huge.Reference!);
        var broken = b.Stream([0, 1, 0, 0, 0, 9, 0, 0, 0xFF, 0xFF], false);
        var descriptor = b.Descriptor("Broken", 32, "FontFile2", broken);
        var font = b.SimpleFont("TrueType", "Broken", PostScriptPdfBuilder.Name("WinAnsiEncoding"), descriptor, _ => 500);
        var resources = b.Resources(("Font", "F1", font));
        resources.Elements["/ColorSpace"] = b.Dictionary(("A", space), ("B", space2));
        b.Page(200, 200, "/A cs 0.5 scn 0 0 50 50 re f", resources);
        b.Page(200, 200, "/B cs 0.5 scn 0 0 50 50 re f", resources);
        b.Page(200, 200, "BT /F1 12 Tf 10 10 Td (abc) Tj ET", resources);
        var pdf = b.Save(folder, "hostile.pdf");
        var writer = new PdfToPostScript(pdf, 150, null, TestContext.Current.CancellationToken);
        writer.Write([0, 1, 2], Path.Combine(folder, "hostile.ps"), eps: false, folder);
        Assert.Equal([1, 2, 3], writer.RasterPages.Select(r => r.Page));
    }

    [Fact]
    public async Task Garbage_is_refused()
    {
        var folder = fx.NewFolder();
        var pdf = Path.Combine(folder, "garbage.pdf");
        File.WriteAllBytes(pdf, [.. "%PDF-1.7\n"u8, .. Enumerable.Repeat((byte)'x', 500)]);
        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "ps" });
        Assert.Equal(FileState.Failed, job.Files.Single().State);
    }

    // ───────────────────────── routes ─────────────────────────

    [Fact]
    public void The_writer_beats_ghostscript_and_leaves_other_routes_alone()
    {
        var all = fx.Converters.Select(c => c.Id).ToArray();
        var with = fx.Catalog.CreatePlanner(all);
        foreach (var to in (string[])["ps", "eps"])
            Assert.Equal("pdf-ps", Assert.Single(with.Plan("pdf", to)!.Steps).Converter.Id);
        Assert.Equal(["vector", "pdf-ps"], with.Plan("svg", "eps")!.Steps.Select(s => s.Converter.Id));
        // PostScript input stays with Ghostscript.
        Assert.Equal("ghostscript", Assert.Single(with.Plan("eps", "pdf")!.Steps).Converter.Id);
        Assert.Equal("ghostscript", Assert.Single(with.Plan("ps", "pdf")!.Steps).Converter.Id);

        var without = new ConverterCatalog(fx.Converters.Where(c => c is not PdfPostScriptConverter)) { Priority = fx.Catalog.Priority }.CreatePlanner(all);
        var formats = Core.Formats.FormatRegistry.Known.Select(f => f.Id).ToList();
        var checkedRoutes = 0;
        foreach (var from in formats)
        {
            foreach (var to in formats.Where(t => t != from && t is not ("ps" or "eps")))
            {
                Assert.Equal(without.Plan(from, to)?.Cost, with.Plan(from, to)?.Cost);
                checkedRoutes++;
            }
        }
        Assert.True(checkedRoutes > 1000);
    }

    private static void AssertDone(ConversionJob job) =>
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));

    /// <summary>Renders a PS, EPS or PDF file with Ghostscript at <see cref="Dpi"/> (supersampled), colours converted the simple way.</summary>
    internal static async Task<List<GreyImage>> RenderAsync(string file, bool eps, string folder)
    {
        var output = Path.Combine(folder, "gs-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(output);
        List<string> args =
        [
            "-dSAFER", "-dBATCH", "-dNOPAUSE", "-dQUIET", "-sDEVICE=png16m", $"-r{Dpi * Supersampling}", "-dTextAlphaBits=4", "-dGraphicsAlphaBits=4", "-dUseCropBox", "-dGridFitTT=0",
            "-dUseFastColor", "-sOutputFile=" + Path.Combine(output, "page-%d.png"),
        ];
        if (eps)
            args.Add("-dEPSCrop");
        args.AddRange(["-f", file]);
        var environment = new Dictionary<string, string> { ["GS_OPTIONS"] = "", ["GS_LIB"] = "", ["GS_FONTPATH"] = "" };
        var result = await ProcessRunner.RunAsync(Ghostscript!, args, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken, environment: environment);
        Assert.True(result.ExitCode == 0, $"Ghostscript failed: {result.StandardError} {result.StandardOutput}");
        Assert.DoesNotContain("Error", result.StandardOutput + result.StandardError, StringComparison.Ordinal);
        var pages = new List<GreyImage>();
        foreach (var png in Directory.GetFiles(output, "page-*.png").OrderBy(f => int.Parse(Path.GetFileNameWithoutExtension(f)[5..], CultureInfo.InvariantCulture)))
        {
            using var bitmap = SKBitmap.Decode(png);
            pages.Add(GreyImage.From(bitmap, Supersampling));
        }
        return pages;
    }
}
