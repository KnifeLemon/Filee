// Mutation fuzzing of the interactive whiteboard reader (Whiteboard/): the seeds are a lesson with every kind of
// element the SVG reader draws (IwbBuilder.Lesson) and a page of uses, gradients, a foreignObject and a data: picture.
// DocumentFuzzer changes the package, and every input must be read or refused with InvalidDataException /
// ConversionRefusedException, in time and on a 1 MB stack; every deck read is drawn and written as a PDF and an ODP.
// Run by FuzzHarness: a short fixed run normally, FILEE_FUZZ_SECONDS for a longer random one, FILEE_FUZZ_SEED to
// repeat, FILEE_FUZZ_OUT to keep failing inputs.

using Filee.Engines.Odf.Draw;
using Filee.Engines.Slides;
using Filee.Engines.Whiteboard;
using SkiaSharp;

namespace Filee.Engines.Tests;

[Trait("Category", "Fuzz")]
public class IwbFuzzTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>Inputs per seed in a normal test run.</summary>
    private const int QuickIterations = 50;

    private static byte[] Shapes()
    {
        var dot = Convert.ToBase64String(IwbBuilder.Png(2, 2, SKColors.Red, SKColors.Blue));
        return IwbBuilder.Single(
            "<svg:defs><svg:radialGradient id=\"r\" r=\"40%\"><svg:stop offset=\"10%\" style=\"stop-color:#f00;stop-opacity:.5\"/><svg:stop offset=\"1\" stop-color=\"navy\"/></svg:radialGradient>" +
            "<svg:linearGradient id=\"l\" xlink:href=\"#r\" x2=\"0\" y2=\"1\"/><svg:symbol id=\"s\" viewBox=\"0 0 10 10\"><svg:ellipse cx=\"5\" cy=\"5\" rx=\"5\" ry=\"3\"/></svg:symbol></svg:defs>" +
            "<svg:g opacity=\"0.5\" transform=\"skewX(20) scale(1.5 0.5)\"><svg:path d=\"M10 10 h50 v50 Q 30 80 10 60 z m5 5 a 10 10 0 1 0 20 0\" fill=\"url(#l)\" fill-rule=\"evenodd\" stroke=\"url(#r)\"/>" +
            "<svg:use xlink:href=\"#s\" x=\"100\" width=\"40\" height=\"40\"/></svg:g>" +
            "<svg:svg x=\"200\" y=\"10\" width=\"100\" height=\"50\" viewBox=\"0 0 10 5\" preserveAspectRatio=\"xMinYMax slice\"><svg:rect width=\"10\" height=\"5\" rx=\"2\"/></svg:svg>" +
            "<svg:switch><svg:text x=\"10\" y=\"200\" xml:space=\"preserve\" style=\"font: 12px serif; text-decoration: underline line-through\">a  b<svg:tspan x=\"10\" dy=\"20\" font-style=\"italic\">c</svg:tspan></svg:text></svg:switch>" +
            "<svg:foreignObject x=\"10\" y=\"220\" width=\"100\" height=\"50\"><div xmlns=\"http://www.w3.org/1999/xhtml\"><span style=\"font-size:2em\">d</span><br/>e</div></svg:foreignObject>" +
            $"<svg:image x=\"300\" y=\"200\" width=\"50\" height=\"20\" preserveAspectRatio=\"xMaxYMin slice\" xlink:href=\"data:image/png;base64,{dot}\"/>" +
            "<svg:line x1=\"0\" y1=\"290\" x2=\"400\" y2=\"290\" stroke=\"currentColor\" color=\"teal\" stroke-dasharray=\"5,3,1\" stroke-linejoin=\"bevel\"/>");
    }

    [Fact]
    public void IwbReader_reads_or_refuses_every_mutated_package() =>
        new FuzzHarness(fx, QuickIterations, 20261120).Files("iwb", "iwb", [IwbBuilder.Lesson(), Shapes()], FuzzHarness.Packages, (path, media) =>
        {
            var deck = IwbReader.Read(path, cancellationToken: CancellationToken.None);
            if (deck.Slides.Count == 0)
                return;
            SlideOutput.ToPdf(deck);
            Directory.CreateDirectory(media);
            OdpWriter.Write(deck, Path.Combine(media, "out.odp"));
        });
}
