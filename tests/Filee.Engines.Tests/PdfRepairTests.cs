using System.Text;
using Filee.Core.Presets;
using Filee.Engines.Pdf;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Tests;

/// <summary>Issue #1175: a PDF with an object (the bookmarks in the original file) closed by one ">>" too many.</summary>
public class PdfRepairTests
{
    private const string Catalog = "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n";
    private const string DamagedCatalog = "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\n>>\nendobj\n";

    /// <summary>A one-page PDF with a classic xref table that lists the objects in <paramref name="extra"/> after the four it has.</summary>
    private static byte[] Pdf(string catalog, params string[] extra)
    {
        const string content = "0 0 100 100 re f";
        string[] bodies =
        [
            catalog,
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
            .. extra,
        ];

        var text = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        foreach (var body in bodies)
        {
            offsets.Add(text.Length);
            text.Append(body);
        }

        var xref = text.Length;
        text.Append("xref\n0 ").Append(bodies.Length + 1).Append('\n');
        text.Append("0000000000 65535 f\r\n");
        foreach (var offset in offsets)
            text.Append($"{offset:D10} 00000 n\r\n");
        text.Append($"trailer\n<< /Size {bodies.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(text.ToString());
    }

    private static byte[] DamagedPdf() => Pdf(DamagedCatalog);

    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    /// <summary>Writes a real password-protected PDF (PDFsharp's own encryption) to <paramref name="path"/>.</summary>
    private static void WritePasswordProtectedPdf(string path)
    {
        using var document = new PdfDocument();
        document.AddPage();
        document.SecuritySettings.UserPassword = "user-secret";
        document.SecuritySettings.OwnerPassword = "owner-secret";
        document.Save(path);
    }

    private static string TempPdf() => Path.Combine(Path.GetTempPath(), $"filee-test-{Guid.NewGuid():N}.pdf");

    [Fact]
    public void Repair_overwrites_the_surplus_close_and_moves_nothing()
    {
        var damaged = DamagedPdf();
        var repaired = PdfRepair.Repair(damaged, TestContext.Current.CancellationToken);

        Assert.NotNull(repaired);
        Assert.Equal(damaged.Length, repaired.Length); // same length, so the file's own xref table stays valid
        using var document = PdfReader.Open(new MemoryStream(repaired), PdfDocumentOpenMode.Import);
        Assert.Equal(1, document.PageCount);
    }

    [Fact]
    public void Repair_leaves_a_file_without_damage_as_it_is()
    {
        Assert.Null(PdfRepair.Repair(Pdf(Catalog), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Repair_does_not_count_close_marks_inside_a_string()
    {
        const string bookmark = "5 0 obj\n<< /Title (Part A >> Part B) /Parent 2 0 R >>\nendobj\n";

        Assert.Null(PdfRepair.Repair(Pdf(Catalog, bookmark), TestContext.Current.CancellationToken));

        var repaired = PdfRepair.Repair(Pdf(DamagedCatalog, bookmark), TestContext.Current.CancellationToken);
        Assert.NotNull(repaired);
        Assert.Contains("(Part A >> Part B)", Latin1(repaired), StringComparison.Ordinal);
    }

    [Fact]
    public void Repair_only_looks_at_objects_the_xref_table_lists()
    {
        // an attached file inside a stream holds text that looks like a damaged object, but the xref table does not list it
        const string inner = "7 0 obj\n<< /A 1 >>\n>>\nendobj\n";
        var attached = $"5 0 obj\n<< /Length {inner.Length} >>\nstream\n{inner}\nendstream\nendobj\n";

        var repaired = PdfRepair.Repair(Pdf(DamagedCatalog, attached), TestContext.Current.CancellationToken);

        Assert.NotNull(repaired);
        Assert.Contains(inner, Latin1(repaired), StringComparison.Ordinal);
    }

    [Fact]
    public void Repair_gives_up_on_a_truncated_file()
    {
        var damaged = DamagedPdf();
        Assert.Null(PdfRepair.Repair(damaged[..(damaged.Length / 2)], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Merge_takes_the_damaged_pdf()
    {
        var damaged = TempPdf();
        var output = TempPdf();
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
    public void Pages_per_sheet_takes_the_damaged_pdf()
    {
        var damaged = TempPdf();
        var output = TempPdf();
        try
        {
            File.WriteAllBytes(damaged, DamagedPdf());
            PdfSheets.Write(damaged, output, new PdfOptions { PagesPerSheet = 2 }, null, TestContext.Current.CancellationToken);

            using var sheets = PdfReader.Open(output, PdfDocumentOpenMode.Import);
            Assert.Equal(1, sheets.PageCount);
        }
        finally
        {
            File.Delete(damaged);
            File.Delete(output);
        }
    }

    [Fact]
    public void A_healthy_file_stays_as_it_is_even_with_an_attached_pdf_that_has_a_lone_CR()
    {
        // an uncompressed attached PDF whose "stream" keyword is followed by a bare CR
        var attached = Encoding.Latin1.GetBytes("%PDF-1.4\r1 0 obj\r<< /Length 3 >>\rstream\rabc\rendstream\rendobj\r");
        var path = TempPdf();
        try
        {
            using (var document = new PdfDocument())
            {
                for (var i = 0; i < 3; i++)
                    document.AddPage();
                var file = new PdfDictionary(document);
                file.CreateStream(attached);
                document.Internals.AddObject(file);
                document.Internals.Catalog.Elements.Add("/Attached", file.Reference!);
                document.Save(path);
            }

            Assert.Null(PdfRepair.Repair(File.ReadAllBytes(path), TestContext.Current.CancellationToken));
            using var opened = PdfSharpConverter.Open(path, PdfDocumentOpenMode.Import, TestContext.Current.CancellationToken);
            Assert.Equal(3, opened.PageCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_password_protected_pdf_is_reported_and_not_repaired()
    {
        var path = TempPdf();
        try
        {
            WritePasswordProtectedPdf(path);

            Assert.Null(PdfRepair.Repair(File.ReadAllBytes(path), TestContext.Current.CancellationToken));
            Assert.Throws<InvalidOperationException>(() => PdfSharpConverter.Open(path, PdfDocumentOpenMode.Import, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Pages_per_sheet_reports_a_password_protected_pdf_as_the_other_pdf_tools_do()
    {
        var path = TempPdf();
        var output = TempPdf();
        try
        {
            WritePasswordProtectedPdf(path);

            var error = Assert.Throws<InvalidOperationException>(() =>
                PdfSheets.Write(path, output, new PdfOptions { PagesPerSheet = 2 }, null, TestContext.Current.CancellationToken));

            Assert.Equal(PdfDocumentReader.PasswordProtected().Message, error.Message);
            Assert.False(File.Exists(output));
        }
        finally
        {
            File.Delete(path);
            File.Delete(output);
        }
    }

    [Fact]
    public void Repair_mends_a_damaged_bookmark_whose_title_has_the_word_stream_in_it()
    {
        // "stream" inside a string is not a stream keyword: the object has no stream, so its surplus ">>" is mended
        const string bookmark = "5 0 obj\n<< /Title (Upstream plan) /Parent 2 0 R >>\n>>\nendobj\n";
        var damaged = Pdf(Catalog, bookmark);

        var repaired = PdfRepair.Repair(damaged, TestContext.Current.CancellationToken);

        Assert.NotNull(repaired);
        Assert.Equal(damaged.Length, repaired.Length);
        var text = Latin1(repaired);
        Assert.Contains("(Upstream plan)", text, StringComparison.Ordinal);
        Assert.DoesNotContain(">>\n>>\nendobj", text, StringComparison.Ordinal);
        using var document = PdfReader.Open(new MemoryStream(repaired), PdfDocumentOpenMode.Import);
        Assert.Equal(1, document.PageCount);
    }

    [Fact]
    public void Repair_leaves_an_object_with_a_stream_alone()
    {
        // a real "stream" keyword: the object is not judged, even though its dictionary has a ">>" too many
        const string data = "abc";
        var withStream = $"5 0 obj\n<< /Length {data.Length} >>\n>>\nstream\n{data}\nendstream\nendobj\n";

        Assert.Null(PdfRepair.Repair(Pdf(Catalog, withStream), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("stream \nabc>>\nendstream")] // a space before the line end
    [InlineData("stream abc>>\nendstream")] // no line end at all
    public void Repair_leaves_a_stream_alone_when_its_keyword_is_written_loosely(string stream)
    {
        // what the stream holds must never be counted, or the ">>" in its data is overwritten
        var withStream = $"5 0 obj\n<< /Length 5 >>\n{stream}\nendobj\n";

        Assert.Null(PdfRepair.Repair(Pdf(Catalog, withStream), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Repair_can_be_cancelled_on_a_file_with_many_objects()
    {
        // 20,000 listed objects that never end: the repair gives up on them, and a cancelled token stops it
        var objects = Enumerable.Range(5, 20_000).Select(n => $"{n} 0 obj\n<< /A 1 >>\n").ToArray();
        var pdf = Pdf(Catalog, objects);

        Assert.Null(PdfRepair.Repair(pdf, TestContext.Current.CancellationToken));
        Assert.Throws<OperationCanceledException>(() => PdfRepair.Repair(pdf, new CancellationToken(canceled: true)));
    }

    [Fact]
    public void Open_hands_back_the_mended_bytes_only_for_a_file_PDFsharp_refused()
    {
        var damaged = TempPdf();
        var healthy = TempPdf();
        try
        {
            File.WriteAllBytes(damaged, DamagedPdf());
            File.WriteAllBytes(healthy, Pdf(Catalog));

            using (var document = PdfSharpConverter.Open(damaged, PdfDocumentOpenMode.Import, out var mended, TestContext.Current.CancellationToken))
            {
                Assert.NotNull(mended);
                Assert.Equal(1, document.PageCount);
            }
            using (var document = PdfSharpConverter.Open(healthy, PdfDocumentOpenMode.Import, out var mended, TestContext.Current.CancellationToken))
            {
                Assert.Null(mended);
                Assert.Equal(1, document.PageCount);
            }
        }
        finally
        {
            File.Delete(damaged);
            File.Delete(healthy);
        }
    }
}
