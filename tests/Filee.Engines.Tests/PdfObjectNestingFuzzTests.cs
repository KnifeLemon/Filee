// Mutation fuzzing of the nesting check PDFsharp's object parser gets every PDF through (PdfObjectNesting): seeds are
// PDFs written in code (PdfObjectNestingTests), one with an object stream, mutated as text with PDF's own tokens and as
// bytes. The check must read or refuse each input, and PDFsharp must then open what it let through without overflowing
// the 1 MB stack (what PDFsharp itself refuses counts as refused). Run by FuzzHarness: a short fixed run normally,
// FILEE_FUZZ_SECONDS for a longer random one, FILEE_FUZZ_SEED to repeat, FILEE_FUZZ_OUT to keep failing inputs.

using Filee.Core.Conversion;
using Filee.Engines.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Tests;

[Trait("Category", "Fuzz")]
public class PdfObjectNestingFuzzTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const int QuickIterations = 60;

    private static readonly string[] Tokens =
    [
        "[", "]", "<<", ">>", "(", ")", "\\", "<5B>", "%", "obj", "endobj", "stream\n", "\nendstream", "trailer", "/N 1", "/First 4",
        "/Type/ObjStm", "/Length 7 0 R", " 0 R", new string('[', 300), new string('[', 3_000), string.Concat(Enumerable.Repeat("[(])", 500)),
    ];

    private static List<byte[]> Seeds() =>
    [
        PdfObjectNestingTests.Pdf("4 0 obj\n<</A [[1 2] (x [) <</B [3]>>]>>\nendobj\n"),
        PdfObjectNestingTests.ObjectStream("<</A [[1] (]]) [2]]>>", deflate: false, typed: false, indirect: true),
    ];

    [Fact]
    public void Check_and_PDFsharp_read_or_refuse_every_mutated_pdf_as_text() =>
        Harness().Data("pdfnesting-text", "pdf", Seeds(), FuzzHarness.Text(Tokens), CheckThenOpen);

    [Fact]
    public void Check_and_PDFsharp_read_or_refuse_every_mutated_pdf_as_bytes() =>
        Harness().Data("pdfnesting-bytes", "pdf", Seeds(), FuzzHarness.Bytes, CheckThenOpen);

    private FuzzHarness Harness() => new(fx, QuickIterations, 20261119);

    private static void CheckThenOpen(byte[] pdf)
    {
        PdfObjectNesting.Check(pdf, TestContext.Current.CancellationToken);
        try
        {
            using var document = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        }
        catch (Exception ex) when (ex is not ConversionRefusedException)
        {
            throw new InvalidDataException("PDFsharp refused it.", ex);
        }
    }
}
