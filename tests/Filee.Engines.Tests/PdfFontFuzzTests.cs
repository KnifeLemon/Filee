// Mutation fuzzing of the Compress PDF font path: CFF tables (CffSubsetter and the Type 2 interpreter), character maps
// (CmapTable) and page content streams (PdfFontUses, and the nesting check before PDFsharp's content parser). Seeds are
// the test fonts and content written in code. Every input must be read, or refused the way PdfFontSubsetter expects
// (InvalidDataException; NotSupportedException, ArgumentException and IndexOutOfRangeException, which it also catches
// and keeps the font whole for), within the time limit and on a 1 MB stack. Run by FuzzHarness: a short
// fixed run normally, FILEE_FUZZ_SECONDS for a longer random one, FILEE_FUZZ_SEED to repeat, FILEE_FUZZ_OUT to keep
// failing inputs.

using System.Text;
using Filee.Engines.Fonts;
using Filee.Engines.Fonts.Cff;
using Filee.Engines.Pdf;
using PdfSharp.Pdf;

namespace Filee.Engines.Tests;

[Trait("Category", "Fuzz")]
public class PdfFontFuzzTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const int QuickIterations = 60;

    private static SfntFont TestFont(string name) => SfntFont.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", name)));

    [Fact]
    public void Cff_subsetter_subsets_or_refuses_every_mutated_font() =>
        Fuzz("cff", [TestFont("FileeTest-CID.otf").Require("CFF "), TestFont("FileeTest-NameKeyed.otf").Require("CFF "), PdfFontLimitsTests.ExponentialCff()],
            cff => AsTheCompressorCatches(() => CffSubsetter.Subset(cff, Enumerable.Range(0, 256), CancellationToken.None)));

    [Fact]
    public void Cmap_reader_reads_or_refuses_every_mutated_table() =>
        Fuzz("cmap",
            [
                TestFont("FileeTest-TrueType.ttf").Require("cmap"),
                CmapTable.Build(new Dictionary<int, int> { ['A'] = 1, ['B'] = 2, [0x1F600] = 3 }),
                PdfFontLimitsTests.Cmap((3, 10, PdfFontLimitsTests.Format12([(0x20u, 0x7Eu), (0x10000u, 0x10010u)], format: 13)), (3, 1, PdfFontLimitsTests.Format4([(0x20, 0x7E)]))),
            ],
            cmap => AsTheCompressorCatches(() =>
            {
                CmapTable.Read(cmap, out _);
                CmapTable.ReadAll(cmap);
            }));

    [Fact]
    public void Content_stream_walk_reads_every_mutated_page() =>
        Fuzz("content",
            [
                Encoding.Latin1.GetBytes("q /F1 12 Tf BT 72 700 Td (Filee) Tj [(A) -20 (B)] TJ ET Q /P <</MCID 0>> BDC /F1 9 Tf 1 2 (x) \" EMC /X Do"),
                Encoding.Latin1.GetBytes("BI /W 2 /H 1 /BPC 8 /CS /G ID \u0001[ EI /F1 1 Tf <0041 0042> Tj [[[1 2] 3]] pop % comment [\n(a\\)b) ' /X Do"),
            ],
            content =>
            {
                using var document = new PdfDocument();
                var font = new PdfDictionary(document);
                font.Elements.SetName("/Subtype", "/Type0");
                document.Internals.AddObject(font);
                var form = new PdfDictionary(document);
                form.CreateStream(content);
                form.Elements.SetName("/Subtype", "/Form");
                document.Internals.AddObject(form);
                var fonts = new PdfDictionary(document);
                fonts.Elements["/F1"] = font.Reference;
                var xobjects = new PdfDictionary(document);
                xobjects.Elements["/X"] = form.Reference;
                var resources = new PdfDictionary(document);
                resources.Elements["/Font"] = fonts;
                resources.Elements["/XObject"] = xobjects;
                var stream = new PdfDictionary(document);
                stream.CreateStream(content);
                document.Internals.AddObject(stream);
                var page = document.AddPage();
                page.Elements["/Resources"] = resources;
                page.Elements["/Contents"] = stream.Reference;
                PdfFontUses.Find(document, CancellationToken.None);
                PdfContentNesting.Checked(content);
            });

    /// <summary>Exceptions PdfFontSubsetter catches (and leaves the font whole for) count as a refusal.</summary>
    private static void AsTheCompressorCatches(Action read)
    {
        try
        {
            read();
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or IndexOutOfRangeException)
        {
            throw new InvalidDataException("Refused as PdfFontSubsetter would.", ex);
        }
    }

    /// <summary>Seeds may be refused (a CFF built to be too costly): each must only not fail.</summary>
    private void Fuzz(string format, List<byte[]> seeds, Action<byte[]> read) =>
        new FuzzHarness(fx, QuickIterations, 20261114) { SeedsMustRead = false }.Data("pdffont-" + format, "bin", seeds, FuzzHarness.Bytes, read);
}
