// Arabic in spreadsheets → PDF (issue #345). rhwp draws text one letter after the other, left to right, so a sheet
// with Arabic must never go through it. Spreadsheets are printed by Filee's own print engine, which shapes text with
// HarfBuzz, so .xlsx and the older binary .xls convert without LibreOffice. Persian that shows only through a number
// format, or that the file keeps in character references (issue #881), and Arabic on a slide master (issue #979) are
// found as well.

using System.IO.Compression;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Tests.RenderQuality;

namespace Filee.Engines.Tests;

public class ArabicSheetTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string Statement = "كشف حساب";

    // Column A is twice the default width: the printed page ends with it, and text past its edge isn't drawn. At the
    // default 8.43 characters "كشف حساب" fits only just, and fonts that draw it a little wider lose a letter at that edge.
    private const double Width = 17;

    private string Xlsx(string name, string text)
    {
        var book = new XlsxBuilder();
        book.Sheet("Sheet1", XlsxBuilder.Row(1, book.Text("A1", text)),
            before: $"<cols><col min=\"1\" max=\"1\" width=\"{Width}\" customWidth=\"1\"/></cols>");
        return book.Save(Path.Combine(fx.NewFolder(), name));
    }

    private string Xls(string name, string text)
    {
        var book = new XlsBuilder();
        book.Sheet("Sheet1").Width(0, Width).Text(0, 0, text);
        return book.Save(Path.Combine(fx.NewFolder(), name));
    }

    [Fact]
    public void Arabic_is_found_in_both_kinds_of_excel_file()
    {
        Assert.Equal("Arabic", ComplexScripts.FindInFile(Xlsx("statement.xlsx", Statement), "xlsx"));
        Assert.Equal("Arabic", ComplexScripts.FindInFile(Xls("statement.xls", Statement), "xls"));
        Assert.Null(ComplexScripts.FindInFile(Xls("plain.xls", "Account statement, 한국어"), "xls"));
        // The same scan finds words rhwp has no widths for (#378), in old Excel files too.
        Assert.Equal("Cyrillic", ComplexScripts.FindUnmeasuredInFile(Xls("report.xls", "Отчёт за месяц"), "xls"));
    }

    /// <summary>Changes one part of a package the test builders wrote.</summary>
    private static void Rewrite(string path, string part, Func<string, string> change)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.GetEntry(part)!;
        string xml;
        using (var reader = new StreamReader(entry.Open()))
            xml = reader.ReadToEnd();
        entry.Delete();
        using var writer = new StreamWriter(zip.CreateEntry(part).Open());
        writer.Write(change(xml));
    }

    /// <summary>A workbook with one number, in the XlsxBuilder.Won style or not, and that style's text replaced.</summary>
    private string NumberWithFormat(string name, string formatText, bool used = true)
    {
        var book = new XlsxBuilder();
        book.Sheet("Sheet1", XlsxBuilder.Row(1, XlsxBuilder.Number("A1", 1200, used ? XlsxBuilder.Won : XlsxBuilder.Plain)));
        var path = book.Save(Path.Combine(fx.NewFolder(), name));
        Rewrite(path, "xl/styles.xml", xml => xml.Replace("원", formatText, StringComparison.Ordinal));
        return path;
    }

    [Fact]
    public void Persian_in_a_number_format_is_found_where_a_cell_shows_it()
    {
        Assert.Equal("Arabic", ComplexScripts.FindInFile(NumberWithFormat("rial.xlsx", "ریال"), "xlsx"));
        // Excel keeps the currency formats of its language in files that don't use them.
        Assert.Null(ComplexScripts.FindInFile(NumberWithFormat("unused.xlsx", "ریال", used: false), "xlsx"));
        Assert.Equal("Cyrillic", ComplexScripts.FindUnmeasuredInFile(NumberWithFormat("ruble.xlsx", "руб"), "xlsx"));
        Assert.Null(ComplexScripts.FindUnmeasuredInFile(NumberWithFormat("unused-ruble.xlsx", "руб", used: false), "xlsx"));
    }

    [Theory]
    [InlineData("&#1711;&#1586;&#1575;&#1585;&#1588;")]
    [InlineData("&#x6AF;&#x632;&#x627;&#x631;&#x634;")]
    public void Text_in_character_references_is_found(string references)
    {
        var path = Xlsx("references.xlsx", "PLACEHOLDER");
        Rewrite(path, "xl/sharedStrings.xml", xml => xml.Replace("PLACEHOLDER", references, StringComparison.Ordinal));

        Assert.Equal("Arabic", ComplexScripts.FindInFile(path, "xlsx"));
    }

    [Theory]
    [InlineData("pptx")]
    [InlineData("potm")]
    public void Arabic_on_a_slide_master_is_found(string format)
    {
        var deck = new PptxBuilder();
        deck.Slide(deck.Title("English title"));
        var path = deck.Save(Path.Combine(fx.NewFolder(), "deck." + format));
        Assert.Null(ComplexScripts.FindInFile(path, format));

        // A text box on the master is drawn on every slide; a placeholder's prompt on it is not.
        const string prompt = "<p:sp><p:nvSpPr><p:cNvPr id=\"91\" name=\"Prompt\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:p><a:r><a:t>انقر لتحرير العنوان</a:t></a:r></a:p></p:txBody></p:sp>";
        const string box = "<p:sp><p:nvSpPr><p:cNvPr id=\"90\" name=\"Box\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:p><a:r><a:t>البطولة المفتوحة</a:t></a:r></a:p></p:txBody></p:sp>";
        Rewrite(path, "ppt/slideMasters/slideMaster1.xml", xml => xml.Replace("</p:spTree>", prompt + "</p:spTree>", StringComparison.Ordinal));
        Assert.Null(ComplexScripts.FindInFile(path, format));
        Rewrite(path, "ppt/slideMasters/slideMaster1.xml", xml => xml.Replace("</p:spTree>", box + "</p:spTree>", StringComparison.Ordinal));
        Assert.Equal("Arabic", ComplexScripts.FindInFile(path, format));
    }

    [Fact]
    public async Task A_persian_number_format_is_never_drawn_unshaped()
    {
        // The sheet print engine shapes it; rhwp, which can't, is never the route.
        var planned = fx.Catalog.CreatePlanner(["rhwp"]).Plan("xlsx", "pdf", shaping: true)!;
        Assert.Equal("sheet-print", Assert.Single(planned.Steps).Converter.Id);

        var job = await fx.ConvertAsync([NumberWithFormat("rial.xlsx", "ریال")], new Preset { TargetFormat = "pdf" });

        Assert.Equal(FileState.Done, Assert.Single(job.Files).State);
        Assert.Contains("ریال", PdfLines.Text(File.ReadAllBytes(job.Outputs.Single())));
    }

    [Theory]
    [InlineData("xlsx")]
    [InlineData("xls")]
    public async Task An_arabic_sheet_is_never_drawn_unshaped(string format)
    {
        var path = format == "xlsx" ? Xlsx("statement.xlsx", Statement) : Xls("statement.xls", Statement);
        var planned = fx.Catalog.CreatePlanner(["rhwp"]).Plan(format, "pdf", shaping: true)!;
        Assert.Equal("sheet-print", Assert.Single(planned.Steps).Converter.Id);

        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "pdf" });

        var file = Assert.Single(job.Files);
        Assert.Equal(FileState.Done, file.State);
        // Shaped (joined letters carry their text as ActualText) and in logical order: it reads back as written.
        Assert.Contains(Statement, PdfLines.Text(File.ReadAllBytes(job.Outputs.Single())));
    }
}
