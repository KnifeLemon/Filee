using System.Text;
using Filee.Engines.Pdf;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Tests;

/// <summary>Issue #1175: a PDF with a lone CR after "stream" and a bookmark closed by one ">>" too many.</summary>
public class PdfRepairTests
{
    /// <summary>One A4 page whose content stream follows the keyword with a bare CR, and a catalog with an extra ">>".</summary>
    private static byte[] DamagedPdf()
    {
        var text = new StringBuilder("%PDF-1.7\r");
        var offsets = new List<int>();
        void Add(string body)
        {
            offsets.Add(text.Length);
            text.Append(body);
        }

        Add("1 0 obj\r<</Type /Catalog /Pages 2 0 R >>\r>>\rendobj\r");
        Add("2 0 obj\r<</Type /Pages /Kids [3 0 R] /Count 1 >>\rendobj\r");
        Add("3 0 obj\r<</Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R >>\rendobj\r");
        const string content = "0 0 100 100 re f";
        Add($"4 0 obj\r<< /Length {content.Length} >>\rstream\r{content}\rendstream\rendobj\r");

        var xref = text.Length;
        text.Append("xref\r0 5 \r0000000000 65535 f\r\n");
        foreach (var offset in offsets)
            text.Append($"{offset:D10} 00000 n\r\n");
        text.Append($"trailer\r<</Size 5 /Root 1 0 R >>\rstartxref\r{xref}\r%%EOF\r");
        return Encoding.Latin1.GetBytes(text.ToString());
    }

    [Fact]
    public void Finds_a_lone_carriage_return_after_stream()
    {
        Assert.True(PdfRepair.HasLoneCrStream(DamagedPdf()));
        Assert.False(PdfRepair.HasLoneCrStream("x << /Length 1 >>\nstream\nA\nendstream\r\nendobj"u8));
        Assert.False(PdfRepair.HasLoneCrStream("x << /Length 1 >>\r\nstream\r\nA\rendstream\rendobj"u8));
    }

    [Fact]
    public void Repair_leaves_a_pdf_that_PDFsharp_reads()
    {
        var repaired = PdfRepair.Repair(DamagedPdf());
        Assert.NotNull(repaired);
        Assert.False(PdfRepair.HasLoneCrStream(repaired));

        using var document = PdfReader.Open(new MemoryStream(repaired), PdfDocumentOpenMode.Import);
        Assert.Equal(1, document.PageCount);
    }

    [Fact]
    public async Task Merge_takes_the_damaged_pdf()
    {
        var damaged = Path.Combine(Path.GetTempPath(), $"filee-damaged-{Guid.NewGuid():N}.pdf");
        var output = Path.Combine(Path.GetTempPath(), $"filee-merged-{Guid.NewGuid():N}.pdf");
        try
        {
            await File.WriteAllBytesAsync(damaged, DamagedPdf(), TestContext.Current.CancellationToken);
            await new PdfSharpConverter().MergeAsync([damaged, damaged], output, TestContext.Current.CancellationToken);

            using var merged = PdfReader.Open(output, PdfDocumentOpenMode.Import);
            Assert.Equal(2, merged.PageCount);
        }
        finally
        {
            File.Delete(damaged);
            File.Delete(output);
        }
    }

    [Fact]
    public void Repair_gives_up_on_an_encrypted_file()
    {
        var text = Encoding.Latin1.GetString(DamagedPdf()).Replace("/Root 1 0 R", "/Root 1 0 R /Encrypt 9 0 R");
        Assert.Null(PdfRepair.Repair(Encoding.Latin1.GetBytes(text)));
    }
}
