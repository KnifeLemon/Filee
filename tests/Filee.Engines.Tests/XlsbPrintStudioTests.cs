// The print studio for Excel binary workbooks (SpreadsheetPrintBinary): the sheets it lists and the pages it prints for
// a region, checked against the same workbook as XLSX, which the studio prints from a rewritten copy
// (SpreadsheetPrintPackage). XlsbBuilder writes both.

using Filee.Engines.Documents;
using Filee.Engines.Office.Sheets;
using UglyToad.PdfPig;

namespace Filee.Engines.Tests;

public sealed class XlsbPrintStudioTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static XlsbBuilder Book()
    {
        var book = new XlsbBuilder();
        var data = book.Sheet("Data");
        for (var r = 0; r < 60; r++)
            data.Text(r, 0, $"Row {r + 1}").Number(r, 1, r * 1.5).Number(r, 2, r * 100);
        data.PageSetup = (9, false, 100, false, 1, 1, false, null);
        data.Margins = [0.5, 0.5, 0.75, 0.75, 0.3, 0.3];
        data.Height(4, 15, hidden: true);
        data.RowBreak(30);
        book.Sheet("Secret", state: 1).Text(0, 0, "hidden");
        book.Names.Add((0, "Print_Area", [(0, 0, 19, 2)]));
        book.Names.Add((0, "Print_Titles", [(0, 0, 0, 16383)]));
        return book;
    }

    [Fact]
    public void An_xlsb_lists_its_sheets_as_its_xlsx_twin()
    {
        var folder = fx.NewFolder();
        var book = Book();
        var binary = SpreadsheetPrintInspector.Read(book.SaveXlsb(Path.Combine(folder, "book.xlsb")));
        var twin = SpreadsheetPrintInspector.Read(book.SaveXlsx(Path.Combine(folder, "book.xlsx")));
        Assert.True(SpreadsheetPrintBinary.Handles(binary));
        Assert.False(SpreadsheetPrintBinary.Handles(twin));
        Assert.Equal(twin.Sheets.Select(s => (s.Index, s.Name, s.IsHidden)), binary.Sheets.Select(s => (s.Index, s.Name, s.IsHidden)));
        var (a, b) = (binary.Sheets[0], twin.Sheets[0]);
        Assert.Equal((b.UsedRange, b.PrintArea, b.RepeatRows, b.UsedRows, b.UsedColumns, b.HiddenRows),
            (a.UsedRange, a.PrintArea, a.RepeatRows, a.UsedRows, a.UsedColumns, a.HiddenRows));
        Assert.Equal(("A1:C60", "A1:C20", "1:1"), (a.UsedRange, a.PrintArea, a.RepeatRows));
        Assert.Equal(SpreadsheetPaper.A4, a.PageSetup.Paper);
        Assert.Equal(b.PageSetup.Orientation, a.PageSetup.Orientation);
        Assert.Equal(b.PageSetup.MarginLeftMm, a.PageSetup.MarginLeftMm, 3);
        Assert.Equal(b.PageSetup.MarginTopMm, a.PageSetup.MarginTopMm, 3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_region_of_an_xlsb_prints_as_the_studio_prints_its_xlsx_twin(bool changed)
    {
        var folder = fx.NewFolder();
        var book = Book();
        var xlsb = book.SaveXlsb(Path.Combine(folder, "book.xlsb"));
        var xlsx = book.SaveXlsx(Path.Combine(folder, "book.xlsx"));
        var original = File.ReadAllBytes(xlsb);
        var binary = SpreadsheetPrintInspector.Read(xlsb);
        var twin = SpreadsheetPrintInspector.Read(xlsx);
        SpreadsheetPrintRegion Region(SpreadsheetSheetInfo sheet)
        {
            var region = SpreadsheetPrintRegion.FromSource(sheet);
            return changed
                ? region with
                {
                    Range = "A1:C60",
                    Paper = SpreadsheetPaper.A5,
                    Orientation = SpreadsheetPageOrientation.Landscape,
                    ScaleMode = SpreadsheetScaleMode.Percent,
                    ScalePercent = 80,
                    MarginLeftMm = 10,
                    IncludeHidden = true,
                    KeepManualBreaks = false,
                    Order = SpreadsheetPageOrder.OverThenDown,
                }
                : region;
        }

        var fromBinary = Path.Combine(folder, "binary.pdf");
        Assert.Empty(SpreadsheetPrintBinary.Print(binary, Region(binary.Sheets[0]), fromBinary, cancellationToken: TestContext.Current.CancellationToken));
        var copy = Path.Combine(folder, "copy.xlsx");
        SpreadsheetPrintPackage.Create(xlsx, copy, twin, Region(twin.Sheets[0]));
        var fromTwin = Path.Combine(folder, "twin.pdf");
        File.WriteAllBytes(fromTwin, SheetPrinter.ToPdf(XlsxReader.Read(copy), SheetPrintSource.Of(xlsx), TestContext.Current.CancellationToken));

        using var a = PdfDocument.Open(fromBinary);
        using var b = PdfDocument.Open(fromTwin);
        Assert.Equal(b.NumberOfPages, a.NumberOfPages);
        Assert.True(changed ? a.NumberOfPages > 1 : a.NumberOfPages == 1);
        for (var page = 1; page <= a.NumberOfPages; page++)
        {
            Assert.Equal((b.GetPage(page).Width, b.GetPage(page).Height), (a.GetPage(page).Width, a.GetPage(page).Height));
            Assert.Equal(b.GetPage(page).Text, a.GetPage(page).Text);
        }
        // A5 across, or the sheet's own A4 upright.
        Assert.Equal(changed ? 420 : 842, (int)Math.Round(a.GetPage(1).Height));
        // The source workbook is never written.
        Assert.Equal(original, File.ReadAllBytes(xlsb));
    }

    [Fact]
    public void A_sheet_gone_from_the_workbook_is_refused()
    {
        var folder = fx.NewFolder();
        var xlsb = Book().SaveXlsb(Path.Combine(folder, "book.xlsb"));
        var info = SpreadsheetPrintInspector.Read(xlsb);
        var region = SpreadsheetPrintRegion.FromSource(info.Sheets[0] with { Index = 7 });
        Assert.Throws<InvalidDataException>(() => SpreadsheetPrintBinary.Print(info, region, Path.Combine(folder, "out.pdf"), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => SpreadsheetPrintBinary.Print(info, SpreadsheetPrintRegion.FromSource(info.Sheets[0]) with { Range = "nonsense" },
            Path.Combine(folder, "out.pdf"), cancellationToken: TestContext.Current.CancellationToken));
    }
}
