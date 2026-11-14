// Temporary diagnostic: runs Filee's own OCR pipeline (Tesseract ara) on three pages of a
// user PDF, then dumps the searchable PDF's raw stream text next to what ComplexWords makes
// of it — separating Tesseract's recognition from the reader's word building. Delete after.

using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Pdf;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Graphics.Colors;

namespace Filee.Engines.Tests;

public class UserOcrProbeTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string UserPath = @"D:\Baba\PDF\قرارات المجلس العلمي 161.pdf";
    private const string OutPath = @"D:\Temp\user-ocr-dump.txt";

    [Fact]
    public async Task DumpOcrLayerOfThreePages()
    {
        Assert.True(File.Exists(UserPath), $"missing probe file: {UserPath}");
        var small = Path.Combine(fx.NewFolder(), "user-slice.pdf");
        using (var document = new PdfSharp.Pdf.PdfDocument())
        {
            using var source = PdfReader.Open(UserPath, PdfDocumentOpenMode.Import);
            for (var i = 0; i < Math.Min(3, source.PageCount); i++)
                document.AddPage(source.Pages[i]);
            document.Save(small);
        }

        var job = await fx.ConvertAsync([small], new Preset { TargetFormat = "pdf", Pdf = { Ocr = true, OcrLanguage = "ara" } });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var searchable = job.Outputs.Single();

        var docxJob = await fx.ConvertAsync([small], new Preset { TargetFormat = "docx", Pdf = { Ocr = true, OcrLanguage = "ara" } });
        Assert.True(docxJob.State == JobState.Completed, string.Join("; ", docxJob.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var docx = docxJob.Outputs.Single();

        using var zip = System.IO.Compression.ZipFile.OpenRead(docx);
        using var documentXml = new System.IO.StreamReader(zip.GetEntry("word/document.xml")!.Open());
        var xml = await documentXml.ReadToEndAsync(TestContext.Current.CancellationToken);

        using var pdf = UglyToad.PdfPig.PdfDocument.Open(searchable);
        var sb = new StringBuilder();
        sb.AppendLine("=== DOCX text (what the user sees) ===");
        foreach (var paragraph in System.Text.RegularExpressions.Regex.Matches(xml, @"<w:p\b.*?</w:p>", System.Text.RegularExpressions.RegexOptions.Singleline))
            sb.AppendLine(System.Text.RegularExpressions.Regex.Replace(paragraph.ToString(), @"<[^>]+>", ""));
        sb.AppendLine();
        foreach (var page in pdf.GetPages())
        {
            var letters = page.Letters;
            sb.AppendLine($"== page {page.Number}: letters={letters.Count}");
            sb.AppendLine("RAW stream: " + string.Concat(letters.Select(l => l.Value)));
            var words = PdfDocumentReader.ComplexWords([.. letters]);
            sb.AppendLine("ComplexWords: " + string.Join(" ", words.Select(w => w.Text)));
            var dateWord = words.FirstOrDefault(w => w.Text.Contains("التاربيخ"));
            if (dateWord is not null)
            {
                sb.AppendLine($"DATE WORD '{dateWord.Text}' drawnForward={dateWord.DrawnForward} letters={dateWord.Letters.Count}");
                foreach (var l in dateWord.Letters)
                    sb.AppendLine($"   '{l.Value}' start={l.StartBaseLine.X:F2} end={l.EndBaseLine.X:F2} y={l.StartBaseLine.Y:F2} box=[{l.BoundingBox.Left:F2}..{l.BoundingBox.Right:F2}] size={l.PointSize:F2}");
            }
            foreach (var w in words.Where(w => w.Text.Length > 8).Take(6))
                sb.AppendLine($"   word '{w.Text}' box=[{w.Box.Left:F1}..{w.Box.Right:F1}] y={w.Box.Bottom:F1} letters={w.Letters.Count}");
            sb.AppendLine();
        }

        File.WriteAllText(OutPath, sb.ToString(), new UTF8Encoding(false));
    }
}
