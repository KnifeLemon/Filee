// The render quality harness for slides: the slide feature deck (SlideFeatures) drawn by Filee's slide renderer, read
// back (each slide's text present, nothing outside the slide, every font embedded and subset, one page per slide) and,
// where LibreOffice is installed, compared slide by slide with LibreOffice's PDF of the same deck (SSIM of PDFium's
// pictures at 100 dpi). Measurements are held to render-thresholds.json ("filee-slides"), as for documents.
//
// LibreOffice is looked for in FILEE_LIBREOFFICE (soffice itself), the repository's engines/ folder
// (pwsh build/fetch-engines.ps1 -Only libreoffice) and the usual install folders; without it the compare is skipped.
// With FILEE_RENDER_OUT set, the deck, both PDFs and a side-by-side picture of every slide are kept there.

using System.Diagnostics;
using Filee.Engines.Slides;
using PDFtoImage;
using SkiaSharp;
using PigDocument = UglyToad.PdfPig.PdfDocument;

#pragma warning disable CA1416 // PDFtoImage runs on Windows, Linux and macOS, the platforms the tests run on

namespace Filee.Engines.Tests.RenderQuality;

[Trait("Category", "Render")]
public sealed class SlideQualityTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    internal const string Renderer = "filee-slides";

    [Fact]
    public void Slide_features_read_back()
    {
        var folder = fx.NewFolder();
        var deck = SlideFeatures.Write(Path.Combine(folder, "slides.pptx"));
        var pdf = SlideOutput.ToPdf(PptxSlideReader.Read(deck, cancellationToken: TestContext.Current.CancellationToken), cancellationToken: TestContext.Current.CancellationToken);

        var measurements = ReadBack(pdf);
        Publish("read-back", measurements);
        var regressions = measurements.Where(m => RenderThresholds.Load().Judge(m) == Verdict.Regression).ToList();
        Assert.True(regressions.Count == 0, "Slide render regressions:\n" + string.Join("\n", regressions.Select(m => $"{m.Feature}: {m.Result}")));
    }

    [Fact]
    public async Task Slide_features_compared_with_libreoffice()
    {
        var soffice = LocateLibreOffice();
        Assert.SkipWhen(soffice is null, "LibreOffice not found (FILEE_LIBREOFFICE, or pwsh build/fetch-engines.ps1 -Only libreoffice)");
        var folder = fx.NewFolder();
        var deck = SlideFeatures.Write(Path.Combine(folder, "slides.pptx"));
        var stopwatch = Stopwatch.StartNew();
        var pdf = SlideOutput.ToPdf(PptxSlideReader.Read(deck, cancellationToken: TestContext.Current.CancellationToken), cancellationToken: TestContext.Current.CancellationToken);
        var time = stopwatch.Elapsed;
        var reference = await LibreOfficePdfAsync(soffice!, deck, folder, TestContext.Current.CancellationToken);

        var ours = GreyImage.Pages(pdf);
        var theirs = GreyImage.Pages(reference);
        var measurements = new List<Measurement>();
        for (var i = 0; i < SlideFeatures.Names.Length; i++)
        {
            var ssim = i < ours.Count && i < theirs.Count && ours[i].Width == theirs[i].Width && ours[i].Height == theirs[i].Height
                ? Ssim.Compare(theirs[i], ours[i]).Ssim
                : 0;
            measurements.Add(new Measurement(Renderer, SlideFeatures.Names[i],
                new CheckResult(RenderThresholds.VersusLibreOffice + RenderThresholds.MinSsim, ssim, ssim >= 0.999, "")));
        }
        Publish($"against LibreOffice ({time.TotalSeconds:0.0} s for Filee)", measurements);
        if (Environment.GetEnvironmentVariable("FILEE_RENDER_OUT") is { Length: > 0 } output)
            Keep(output, deck, pdf, reference);

        var regressions = measurements.Where(m => RenderThresholds.Load().Judge(m) == Verdict.Regression).ToList();
        Assert.True(regressions.Count == 0, "Slide render regressions:\n" + string.Join("\n", regressions.Select(m => $"{m.Feature}: {m.Result}")));
    }

    /// <summary>Per slide: its text on its page; for the deck: pages, glyphs outside the slide, overlaps, fonts.</summary>
    internal static List<Measurement> ReadBack(byte[] pdf)
    {
        var measurements = new List<Measurement>();
        using (var document = PigDocument.Open(pdf))
        {
            for (var i = 0; i < SlideFeatures.Names.Length && i < document.NumberOfPages; i++)
            {
                var name = SlideFeatures.Names[i];
                var text = PdfLines.Normalise(PdfLines.Text(document.GetPage(i + 1)));
                var expected = SlideFeatures.Text[name];
                var missing = expected.Where(e => !text.Contains(PdfLines.Normalise(e), StringComparison.Ordinal)).ToList();
                var share = (double)(expected.Length - missing.Count) / expected.Length;
                measurements.Add(new Measurement(Renderer, name, new CheckResult(PdfChecks.Text, share, missing.Count == 0, string.Join(", ", missing))));
            }
        }
        measurements.Add(new Measurement(Renderer, "deck", PdfChecks.PageCount(pdf, SlideFeatures.Names.Length)));
        measurements.Add(new Measurement(Renderer, "deck", PdfChecks.GlyphsOutsidePage(pdf)));
        measurements.Add(new Measurement(Renderer, "deck", PdfChecks.OverlappingGlyphs(pdf)));
        measurements.Add(new Measurement(Renderer, "deck", PdfChecks.FontsEmbeddedAndSubset(pdf)));
        return measurements;
    }

    // ───────────────────────── LibreOffice ─────────────────────────

    internal static string? LocateLibreOffice()
    {
        var program = OperatingSystem.IsWindows() ? "soffice.exe" : "soffice";
        if (Environment.GetEnvironmentVariable("FILEE_LIBREOFFICE") is { Length: > 0 } own && File.Exists(own))
            return own;
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var bundled = Path.Combine(folder.FullName, "engines", "libreoffice", "program", program);
            if (File.Exists(bundled))
                return bundled;
        }
        string[] installed =
        [
            @"C:\Program Files\LibreOffice\program\soffice.exe", "/usr/bin/soffice", "/usr/lib/libreoffice/program/soffice",
            "/Applications/LibreOffice.app/Contents/MacOS/soffice",
        ];
        return installed.FirstOrDefault(File.Exists);
    }

    private static async Task<byte[]> LibreOfficePdfAsync(string soffice, string deck, string folder, CancellationToken cancellationToken)
    {
        var profile = new Uri(Path.Combine(folder, "lo-profile")).AbsoluteUri;
        var output = Path.Combine(folder, "libreoffice");
        Directory.CreateDirectory(output);
        var start = new ProcessStartInfo(soffice)
        {
            ArgumentList = { $"-env:UserInstallation={profile}", "--headless", "--norestore", "--convert-to", "pdf", "--outdir", output, deck },
            UseShellExecute = false,
        };
        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        await process.WaitForExitAsync(timeout.Token);
        var pdf = Path.Combine(output, Path.ChangeExtension(Path.GetFileName(deck), ".pdf"));
        Assert.True(File.Exists(pdf), $"LibreOffice wrote no PDF (exit {process.ExitCode})");
        return await File.ReadAllBytesAsync(pdf, cancellationToken);
    }

    // ───────────────────────── Output ─────────────────────────

    /// <summary>Keeps the deck, both PDFs and Filee | LibreOffice pictures of each slide (96 dpi, in colour).</summary>
    private static void Keep(string folder, string deck, byte[] pdf, byte[] reference)
    {
        Directory.CreateDirectory(folder);
        File.Copy(deck, Path.Combine(folder, "slides.pptx"), overwrite: true);
        File.WriteAllBytes(Path.Combine(folder, "slides-filee.pdf"), pdf);
        File.WriteAllBytes(Path.Combine(folder, "slides-libreoffice.pdf"), reference);
        var options = new RenderOptions(Dpi: 96, WithAnnotations: false, BackgroundColor: SKColors.White);
        var ours = Conversion.ToImages(pdf, null, options).ToList();
        var theirs = Conversion.ToImages(reference, null, options).ToList();
        for (var i = 0; i < Math.Min(ours.Count, theirs.Count) && i < SlideFeatures.Names.Length; i++)
        {
            using var pair = SideBySide(ours[i], theirs[i]);
            using var data = pair.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(folder, $"slides-{SlideFeatures.Names[i]}.png"), data.ToArray());
        }
        foreach (var bitmap in ours.Concat(theirs))
            bitmap.Dispose();
    }

    /// <summary>Two slide pictures side by side, each under its label.</summary>
    internal static SKBitmap SideBySide(SKBitmap left, SKBitmap right, string leftLabel = "Filee", string rightLabel = "LibreOffice")
    {
        const int Gap = 24, Label = 40;
        var result = new SKBitmap(left.Width + right.Width + Gap * 3, Math.Max(left.Height, right.Height) + Label + Gap);
        using var canvas = new SKCanvas(result);
        canvas.Clear(new SKColor(0xEC, 0xEF, 0xF1));
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default, 20);
        using var ink = new SKPaint { Color = new SKColor(0x26, 0x32, 0x38), IsAntialias = true };
        canvas.DrawText(leftLabel, Gap, Label - 12, font, ink);
        canvas.DrawText(rightLabel, Gap * 2 + left.Width, Label - 12, font, ink);
        canvas.DrawBitmap(left, Gap, Label);
        canvas.DrawBitmap(right, Gap * 2 + left.Width, Label);
        return result;
    }

    private static void Publish(string title, IReadOnlyList<Measurement> measurements)
    {
        var thresholds = RenderThresholds.Load();
        var heading = $"Slide render quality: {title}";
        TestContext.Current.TestOutputHelper?.WriteLine(heading);
        TestContext.Current.TestOutputHelper?.WriteLine(RenderHarness.Report(measurements, thresholds, markdown: false));
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
            File.AppendAllText(summary, $"### {heading}\n\n{RenderHarness.Report(measurements, thresholds, markdown: true)}\n");
    }
}
