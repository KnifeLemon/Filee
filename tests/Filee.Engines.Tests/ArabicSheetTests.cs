// Arabic in spreadsheets → PDF (issue #345). rhwp draws text one letter after the other, left to right, so a sheet
// with Arabic must never go through it. Spreadsheets are printed by Filee's own print engine, which shapes text with
// HarfBuzz, so .xlsx and the older binary .xls convert without LibreOffice.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Tests.RenderQuality;

namespace Filee.Engines.Tests;

public class ArabicSheetTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string Statement = "كشف حساب";

    private string Xlsx(string name, string text)
    {
        var book = new XlsxBuilder();
        book.Sheet("Sheet1", XlsxBuilder.Row(1, book.Text("A1", text)));
        return book.Save(Path.Combine(fx.NewFolder(), name));
    }

    private string Xls(string name, string text)
    {
        var book = new XlsBuilder();
        book.Sheet("Sheet1").Text(0, 0, text);
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
