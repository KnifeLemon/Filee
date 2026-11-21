// Mutation fuzzing of the built-in PDF/A writer (Pdf/PdfAConformance.cs with PdfAFonts, PdfAAppearances and the
// device-independent colour): PDFs with fonts that aren't embedded (simple and composite, with a ToUnicode CMap),
// annotations without appearances and device CMYK, mutated as text with PDF tokens and as bytes. Each must become a
// PDF/A or be refused (ConversionRefusedException; PDFsharp failing to open it counts as refused, as Archive treats
// it), within the time limit. Run by FuzzHarness like the other fuzz targets.

using Filee.Core.Conversion;
using Filee.Engines.Pdf;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Tests;

[Trait("Category", "Fuzz")]
public class PdfAFuzzTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const int QuickIterations = 30;

    private static readonly string[] Tokens =
    [
        "[", "]", "<<", ">>", "(", ")", "/Widths [", "/W [0 65535 500]", "/W [1 [", "/FirstChar 0", "/LastChar 255", "/Encoding /Identity-H",
        "/Encoding << /Differences [32 /space /A /uni0041 /g12] >>", "/Subtype /Type0", "/Subtype /TrueType", "/Subtype /Widget", "/FT /Btn",
        "/FT /Tx", "/FT /Ch", "/V (x)", "/Opt [(a) [(b) (c)]]", "/DA (/F 0 Tf 1 0 0 rg)", "/QuadPoints [0 0 1 1]", "/InkList [[1 2 3]]",
        "/Vertices [1]", "/L [1 2]", "/C [0 0 0 1]", "/IC []", "/BS << /W 1e9 /S /D /D [0] >>", "beginbfrange", "endbfrange", "<0000> <FFFF> <0041>",
        "beginbfchar", "endbfchar", "<FFFFFFFF>", "k", "K", "/DeviceCMYK", "/DeviceRGB", "BI /CS /CMYK ID", "EI", "Tf", "Tj", "obj", "endobj",
    ];

    private List<byte[]> Seeds()
    {
        var dir = fx.NewFolder();
        return
        [
            File.ReadAllBytes(PdfAEmbeddingTests.Annotated(Path.Combine(dir, "annotated.pdf"))),
            File.ReadAllBytes(PdfAEmbeddingTests.Page(Path.Combine(dir, "font.pdf"), "BT /F1 24 Tf 72 700 Td (Hamburgefonstiv) Tj ET 0 0 0 1 k 0 0 9 9 re f",
                "/Font << /F1 5 0 R >>", extra: [PdfAEmbeddingTests.SansFont()])),
            File.ReadAllBytes(PdfAEmbeddingTests.Composite(Path.Combine(dir, "cid.pdf"))),
        ];
    }

    [Fact]
    public void Pdf_a_writer_makes_or_refuses_every_mutated_pdf_as_text() =>
        Harness().Data("pdfa-text", "pdf", Seeds(), FuzzHarness.Text(Tokens), Archive);

    [Fact]
    public void Pdf_a_writer_makes_or_refuses_every_mutated_pdf_as_bytes() =>
        Harness().Data("pdfa-bytes", "pdf", Seeds(), FuzzHarness.Bytes, Archive);

    private FuzzHarness Harness() => new(fx, QuickIterations, 20261121) { SeedsMustRead = true };

    private static void Archive(byte[] pdf)
    {
        PdfDocument document;
        try
        {
            document = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Modify);
        }
        catch (Exception ex) when (ex is not ConversionRefusedException)
        {
            throw new InvalidDataException("PDFsharp refused it.", ex);
        }
        using (document)
        {
            var info = PdfAMetadata.InfoOf(document);
            PdfAConformance.Prepare(document, TestContext.Current.CancellationToken, PdfATestFonts.Sans);
            using var output = new MemoryStream();
            PdfAMetadata.Save(document, output, info);
        }
    }
}
