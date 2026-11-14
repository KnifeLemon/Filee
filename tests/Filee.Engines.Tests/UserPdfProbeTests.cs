// Temporary diagnostic: dumps the raw letter stream of a user PDF plus what ComplexWords
// makes of it, to diagnose interleaved/garbled Arabic OCR layers. Delete after diagnosis.

using System.Text;
using Filee.Engines.Infrastructure;
using Filee.Engines.Pdf;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Graphics.Colors;

namespace Filee.Engines.Tests;

public class UserPdfProbeTests
{
    private const string UserPath = @"D:\Baba\PDF\قرارات المجلس العلمي 161.pdf";
    private const string OutPath = @"D:\Temp\user-pdf-dump.txt";

    [Fact]
    public void DumpLettersAndWords()
    {
        Assert.True(File.Exists(UserPath), $"missing probe file: {UserPath}");
        using var document = PdfDocument.Open(UserPath);
        var sb = new StringBuilder();
        var target = -1;
        foreach (var page in document.GetPages())
        {
            var letters = page.Letters;
            var text = string.Concat(letters.Select(l => l.Value));
            var modes = string.Join(",", letters.Select(l => l.RenderingMode).Distinct());
            var colors = string.Join(",", letters.Select(l => Hex(l.FillColor)).Distinct().Take(8));
            var fonts = string.Join(",", letters.Select(l => l.FontDetails?.Name).Distinct().Take(8));
            sb.AppendLine($"== page {page.Number}: letters={letters.Count} chars={text.Length} modes=[{modes}] colors=[{colors}] fonts=[{fonts}]");
            if (text.Contains("الإغواء") || text.Contains("نركض"))
                target = page.Number;
        }

        sb.AppendLine($"TARGET PAGE: {target}");
        if (target > 0)
        {
            var page = document.GetPage(target);
            var letters = page.Letters;
            sb.AppendLine($"== target {target}: {letters.Count} letters in stream order");
            for (var i = 0; i < letters.Count; i++)
            {
                var l = letters[i];
                sb.AppendLine(
                    $"{i}: '{Esc(l.Value)}' x[{l.StartBaseLine.X:F1}..{l.EndBaseLine.X:F1}] y={l.StartBaseLine.Y:F1} " +
                    $"mode={l.RenderingMode} size={l.PointSize:F1} font={l.FontDetails?.Name} fill={Hex(l.FillColor)} seq={l.TextSequence}");
            }

            var words = PdfDocumentReader.ComplexWords([.. letters]);
            sb.AppendLine("=== ComplexWords output (visual rows) ===");
            foreach (var w in words)
                sb.AppendLine(
                    $"'{w.Text}' box=[{w.Box.Left:F1}..{w.Box.Right:F1}] letters={w.Letters.Count} " +
                    $"modes=[{string.Join(",", w.Letters.Select(l => l.RenderingMode).Distinct())}]");
        }

        File.WriteAllText(OutPath, sb.ToString(), new UTF8Encoding(false));
    }

    private static string? Hex(IColor? color)
    {
        if (color is null)
            return null;
        try
        {
            var (r, g, b) = color.ToRGBValues();
            return $"#{((int)Math.Round(Math.Clamp(r, 0, 1) * 255)):X2}{((int)Math.Round(Math.Clamp(g, 0, 1) * 255)):X2}{((int)Math.Round(Math.Clamp(b, 0, 1) * 255)):X2}";
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private static string Esc(string value) =>
        string.Concat(value.Select(c => char.IsWhiteSpace(c) || c > 126 ? $"\\u{(int)c:X4}" : c.ToString()));
}
