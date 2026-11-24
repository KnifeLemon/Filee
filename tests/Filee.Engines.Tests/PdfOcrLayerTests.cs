// The invisible text layer of searchable PDFs, read back by the built-in PDF reader (#1538). The test PDFs are written
// the way Tesseract writes its PDF output: a picture of the page, and the recognized words in render mode 3 (invisible)
// in an Identity-H "GlyphLessFont" whose ToUnicode CMap maps every code to the same Unicode value. Tesseract writes a
// line of a page that was scanned or photographed at an angle along its slanted baseline (a turned text matrix).

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Core.Presets;

namespace Filee.Engines.Tests;

public class PdfOcrLayerTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string FirstLine = "Filee reads the text layer of scanned pages";
    private const string SecondLine = "even when the page was photographed at an angle";

    /// <summary>
    /// A one-page A4 PDF as Tesseract writes it: a scan (a one-pixel picture over the page) and <paramref name="lines"/>
    /// as invisible text, each line turned by <paramref name="degrees"/> (negative: falling to the right) about its start.
    /// </summary>
    private string OcrPdf(double degrees, params string[] lines)
    {
        const double size = 12;
        const double advance = 6; // the font's default width (DW 500) at 12 pt
        var cos = Math.Cos(degrees * Math.PI / 180);
        var sin = Math.Sin(degrees * Math.PI / 180);
        var content = new StringBuilder("q 595 0 0 842 0 0 cm /Im1 Do Q\nBT\n3 Tr\n");
        for (var i = 0; i < lines.Length; i++)
        {
            content.Append(Invariant($"{cos:F6} {sin:F6} {-sin:F6} {cos:F6} 60 {760 - i * 40} Tm /F1 {size} Tf\n"));
            var previous = 0.0;
            foreach (var word in lines[i].Split(' '))
            {
                // Tesseract places each word with Td from the one before, in the line's (turned) text space.
                if (previous > 0)
                    content.Append(Invariant($"{previous} 0 Td\n"));
                content.Append('<').Append(string.Concat(word.Select(c => ((int)c).ToString("X4", CultureInfo.InvariantCulture)))).Append("> Tj\n");
                previous = (word.Length + 1) * advance;
            }
        }
        content.Append("ET\n");

        const string toUnicode = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n" +
                                 "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
                                 "/CMapName /Adobe-Identity-UCS def /CMapType 2 def\n" +
                                 "1 begincodespacerange <0000> <FFFF> endcodespacerange\n" +
                                 "1 beginbfrange <0000> <FFFF> <0000> endbfrange\n" +
                                 "endcmap CMapName currentdict /CMap defineresource pop end end";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> /XObject << /Im1 9 0 R >> >> >>",
            RawPdf.Stream("", content.ToString()),
            "<< /Type /Font /Subtype /Type0 /BaseFont /GlyphLessFont /Encoding /Identity-H /DescendantFonts [6 0 R] /ToUnicode 8 0 R >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /GlyphLessFont /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /CIDToGIDMap /Identity /DW 500 /FontDescriptor 7 0 R >>",
            "<< /Type /FontDescriptor /FontName /GlyphLessFont /Flags 5 /FontBBox [0 0 500 1000] /ItalicAngle 0 /Ascent 1000 /Descent 0 /CapHeight 1000 /StemV 80 >>",
            RawPdf.Stream("", toUnicode),
            RawPdf.Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", "È"),
        ];
        return RawPdf.Write(Path.Combine(fx.NewFolder(), "searchable.pdf"), objects);
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private async Task<string> TextAsync(string pdf)
    {
        var job = await fx.ConvertAsync([pdf], new Preset { TargetFormat = "txt" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return Regex.Replace(await File.ReadAllTextAsync(job.Outputs.Single(), TestContext.Current.CancellationToken), @"\s+", " ").Trim();
    }

    [Fact]
    public async Task A_straight_ocr_layer_becomes_the_text()
    {
        Assert.Equal($"{FirstLine} {SecondLine}", await TextAsync(OcrPdf(0, FirstLine, SecondLine)));
    }

    [Theory]
    [InlineData(-4)]
    [InlineData(3)]
    [InlineData(-12)]
    public async Task The_ocr_layer_of_a_page_scanned_at_an_angle_becomes_the_text_line_by_line(double degrees)
    {
        // Each line drifts by several times its font size from one end to the other: read as it lies on the page,
        // every word would be a line of its own, or no text at all.
        Assert.Equal($"{FirstLine} {SecondLine}", await TextAsync(OcrPdf(degrees, FirstLine, SecondLine)));
    }
}
