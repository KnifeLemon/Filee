// PDF/A (#1367): Ghostscript's pdfwrite settings and output intent, which PDFs already count as PDF/A, and real round
// trips (skipped when Ghostscript isn't installed): PDF → PDF/A, a picture → PDF/A, and a PDF/A from Skia kept as it is.
// Every input is made in the test.

using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Vector;
using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Filee.Engines.Tests;

public class PdfATests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string Sentence = "Archived for later";

    private static bool GhostscriptInstalled => GhostscriptConverter.Locate() is not null;

    private static Preset PdfAPreset(string watermark = "") =>
        new() { TargetFormat = "pdf", Document = { PdfA = true }, Pdf = { Watermark = watermark }, Output = { FileNamePattern = "{name}_pdfa" } };

    private static void AssertDone(ConversionJob job)
    {
        var errors = string.Join("; ", job.Files.Where(f => f.State == FileState.Failed).Select(f => $"{f.ErrorKey} {f.ErrorDetail}"));
        Assert.True(job.State == JobState.Completed, $"{job.State}: {errors}");
    }

    private static string TextPdf(string folder)
    {
        var pdf = new PdfBuilder();
        pdf.NewPage();
        pdf.Line(Sentence, 14, pdf.Font(), 72, 72);
        pdf.Bar(72, 120, 200, 40, (200, 40, 40));
        return pdf.Save(Path.Combine(folder, "letter.pdf"));
    }

    /// <summary>A one-rectangle SVG, which Skia writes as PDF/A itself.</summary>
    private static string Badge(string folder)
    {
        var svg = Path.Combine(folder, "badge.svg");
        File.WriteAllText(svg, """<svg xmlns="http://www.w3.org/2000/svg" width="200" height="100"><rect width="200" height="100" fill="#2060C0"/></svg>""");
        return svg;
    }

    /// <summary>The output intent PDF/A asks for: GTS_PDFA1 with an RGB profile.</summary>
    private static void AssertOutputIntent(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var intents = document.Internals.Catalog.Elements.GetArray("/OutputIntents");
        Assert.NotNull(intents);
        var intent = Assert.IsType<PdfDictionary>(intents.Elements.GetObject(0));
        Assert.Equal("/GTS_PDFA1", intent.Elements.GetName("/S"));
        var profile = Assert.IsType<PdfDictionary>(intent.Elements.GetObject("/DestOutputProfile"));
        Assert.Equal(3, profile.Elements.GetInteger("/N"));
    }

    private static string Words(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    [Fact]
    public void Pdfwrite_makes_pdf_a_2b_in_rgb_and_leaves_out_what_it_must()
    {
        var args = GhostscriptConverter.PdfAArguments();

        Assert.Contains("-sDEVICE=pdfwrite", args);
        Assert.Contains("-dPDFA=2", args);
        Assert.Contains("-dPDFACompatibilityPolicy=1", args); // drop what PDF/A forbids, don't give up or write a plain PDF
        Assert.Contains("-sColorConversionStrategy=RGB", args); // to match the sRGB output intent
        Assert.Contains("-dAutoRotatePages=/None", args);
    }

    [Fact]
    public void The_output_intent_carries_the_srgb_profile_inline()
    {
        var profile = ColorProfiles.SRGB.ToByteArray();
        var script = GhostscriptConverter.OutputIntent(profile);

        Assert.StartsWith("%!PS", script);
        Assert.True(profile.Length < 65535, "A PostScript string holds at most 65535 bytes.");
        Assert.Contains(Convert.ToHexString(profile), string.Concat(script.Where(char.IsAsciiHexDigit)));
        Assert.Contains("/S /GTS_PDFA1", script);
        Assert.Contains("[{Catalog} <</OutputIntents [{OutputIntent_PDFA}]>> /PUT pdfmark", script);
        Assert.All(script.Split('\n'), line => Assert.True(line.Length < 256, line));
        Assert.True(script.All(char.IsAscii));
    }

    [Fact]
    public void Only_a_pdf_whose_xmp_says_pdf_a_counts_as_one()
    {
        var dir = fx.NewFolder();
        var plain = TextPdf(dir);
        var broken = Path.Combine(dir, "broken.pdf");
        File.WriteAllText(broken, "%PDF-1.7 not really");

        Assert.False(GhostscriptConverter.DeclaresPdfA(plain));
        Assert.False(GhostscriptConverter.DeclaresPdfA(broken));
    }

    [Fact]
    public async Task A_pdf_a_from_skia_is_kept_as_it_is_without_ghostscript()
    {
        var dir = fx.NewFolder();
        var svg = Badge(dir);

        var job = await fx.ConvertAsync([svg], PdfAPreset());

        AssertDone(job);
        var output = job.Outputs.Single();
        Assert.True(GhostscriptConverter.DeclaresPdfA(output));
        Assert.Null(job.Files.Single().NoticeKey);
        Assert.DoesNotContain("Ghostscript", Encoding.Latin1.GetString(File.ReadAllBytes(output))); // not rewritten
    }

    [Fact]
    public async Task A_pdf_becomes_pdf_a_with_its_text()
    {
        Assert.SkipUnless(GhostscriptInstalled, "Ghostscript is not installed (pwsh build/fetch-engines.ps1 -Only ghostscript).");
        var dir = fx.NewFolder();
        var source = TextPdf(dir);
        Assert.False(GhostscriptConverter.DeclaresPdfA(source));

        var job = await fx.ConvertAsync([source], BuiltInData.CreatePresets().Single(p => p.Id == BuiltInData.PdfAId));

        AssertDone(job);
        var output = job.Outputs.Single();
        Assert.Equal("letter_pdfa.pdf", Path.GetFileName(output));
        Assert.Null(job.Files.Single().NoticeKey);
        Assert.True(GhostscriptConverter.DeclaresPdfA(output));
        AssertOutputIntent(output);
        using var pig = PigDocument.Open(output);
        Assert.Equal(Words(Sentence), Words(pig.GetPage(1).Text));
    }

    [Fact]
    public async Task A_picture_made_into_a_pdf_by_pdfsharp_becomes_pdf_a()
    {
        Assert.SkipUnless(GhostscriptInstalled, "Ghostscript is not installed (pwsh build/fetch-engines.ps1 -Only ghostscript).");
        var dir = fx.NewFolder();
        var picture = EngineFixture.MakeImage(dir, "photo.png", MagickFormat.Png);

        var job = await fx.ConvertAsync([picture], PdfAPreset());

        AssertDone(job);
        var output = job.Outputs.Single();
        Assert.True(GhostscriptConverter.DeclaresPdfA(output));
        AssertOutputIntent(output);
    }

    [Fact]
    public async Task A_pdf_a_given_a_watermark_is_made_pdf_a_again()
    {
        Assert.SkipUnless(GhostscriptInstalled, "Ghostscript is not installed (pwsh build/fetch-engines.ps1 -Only ghostscript).");
        var dir = fx.NewFolder();
        var svg = Badge(dir);

        var job = await fx.ConvertAsync([svg], PdfAPreset(watermark: "DRAFT"));

        AssertDone(job);
        var output = job.Outputs.Single();
        Assert.True(GhostscriptConverter.DeclaresPdfA(output));
        AssertOutputIntent(output); // Skia's PDF/A had none of its own: this one is Ghostscript's
    }
}
