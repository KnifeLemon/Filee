// The Spreadsheet Print Studio's settings as Filee's own sheet printer reads them: the studio writes each region into
// a temporary copy (SpreadsheetPrintPackage), and the printer reads that copy like any workbook (XlsxReader). Every
// studio option must reach the printer's page setup, and what the studio doesn't offer (gridlines, headings, centring,
// headers and footers) must stay as the sheet has it. Workbooks are built in code.

using Filee.Engines.Documents;
using Filee.Engines.Office.Sheets;
using static Filee.Engines.Tests.XlsxBuilder;

namespace Filee.Engines.Tests;

public sealed class SpreadsheetPrintStudioMappingTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const double PointsPerMm = 72 / 25.4;

    [Fact]
    public void Every_studio_paper_is_one_the_printer_knows_at_the_same_size()
    {
        foreach (var paper in SpreadsheetPaper.All.Where(paper => paper != SpreadsheetPaper.Source))
        {
            var size = Paper.FromCode(paper.OoxmlCode);
            Assert.True(size is not null, paper.Name);
            Assert.Equal(paper.WidthMm * PointsPerMm, size.Value.Width, 0.5);
            Assert.Equal(paper.HeightMm * PointsPerMm, size.Value.Height, 0.5);
        }
    }

    [Fact]
    public void Every_option_of_a_region_reaches_the_printers_page_setup()
    {
        var (workbook, info) = Workbook();
        var region = SpreadsheetPrintRegion.FromSource(info.Sheets[1]) with
        {
            Range = "A1:C40",
            WritePrintArea = true,
            Paper = SpreadsheetPaper.Letter,
            Orientation = SpreadsheetPageOrientation.Landscape,
            ScaleMode = SpreadsheetScaleMode.Percent,
            ScalePercent = 80,
            Order = SpreadsheetPageOrder.OverThenDown,
            MarginLeftMm = 10,
            MarginRightMm = 12,
            MarginTopMm = 14,
            MarginBottomMm = 16,
            IncludeHidden = true,
            KeepManualBreaks = false,
        };

        var sheet = Assert.Single(Print(workbook, info, region).Sheets);

        Assert.Equal("Data", sheet.Name); // the other sheet is hidden in the copy and not printed
        var setup = sheet.PageSetup;
        Assert.Equal(8.5 * 72, setup.PaperWidth, 0.5);
        Assert.Equal(11 * 72, setup.PaperHeight, 0.5);
        Assert.True(setup.Landscape);
        Assert.False(setup.FitToPage);
        Assert.Equal(80, setup.Scale);
        Assert.True(setup.OverThenDown);
        Assert.Equal(10 * PointsPerMm, setup.LeftMargin, 0.1);
        Assert.Equal(12 * PointsPerMm, setup.RightMargin, 0.1);
        Assert.Equal(14 * PointsPerMm, setup.TopMargin, 0.1);
        Assert.Equal(16 * PointsPerMm, setup.BottomMargin, 0.1);
        Assert.Equal([new CellRange(0, 0, 39, 2)], sheet.PrintArea);
        Assert.Empty(sheet.HiddenRows);
        Assert.Empty(sheet.HiddenColumns);
        Assert.Empty(sheet.RowBreaks);
        // Not studio options: kept as the sheet sets them.
        Assert.True(setup.PrintGridLines);
        Assert.True(setup.PrintHeadings);
        Assert.True(setup.CenterHorizontally);
        Assert.True(setup.CenterVertically);
        Assert.Equal("&CQuarterly report&RPage &P of &N", setup.HeaderFooter.OddHeader);
        Assert.Equal(0.4 * 72, setup.HeaderMargin, 0.1);
    }

    [Fact]
    public void Fit_to_pages_and_the_sheets_own_settings_reach_the_printer()
    {
        var (workbook, info) = Workbook();
        var source = SpreadsheetPrintRegion.FromSource(info.Sheets[1]);

        var kept = Assert.Single(Print(workbook, info, source).Sheets);

        // "From workbook" everywhere: A4 portrait at 100 %, the hidden row and column and the manual break stay.
        Assert.Equal(210 * PointsPerMm, kept.PageSetup.PaperWidth, 0.5);
        Assert.False(kept.PageSetup.Landscape);
        Assert.False(kept.PageSetup.FitToPage);
        Assert.False(kept.PageSetup.OverThenDown);
        Assert.Contains(4, kept.HiddenRows);
        Assert.Contains(1, kept.HiddenColumns);
        Assert.Contains(20, kept.RowBreaks);
        Assert.Empty(kept.PrintArea);

        var fitted = Assert.Single(Print(workbook, info, source with
        {
            ScaleMode = SpreadsheetScaleMode.FitToPages,
            FitWidth = 2,
            FitHeight = 0,
            Order = SpreadsheetPageOrder.DownThenOver,
        }).Sheets);

        Assert.True(fitted.PageSetup.FitToPage);
        Assert.Equal(2, fitted.PageSetup.FitWidth);
        Assert.Equal(0, fitted.PageSetup.FitHeight);
        Assert.False(fitted.PageSetup.OverThenDown);
    }

    [Fact]
    public void A_region_fitted_to_one_landscape_page_prints_on_one_landscape_page()
    {
        var (workbook, info) = Workbook();
        var region = SpreadsheetPrintRegion.FromSource(info.Sheets[1]) with
        {
            Paper = SpreadsheetPaper.A4,
            Orientation = SpreadsheetPageOrientation.Landscape,
            ScaleMode = SpreadsheetScaleMode.FitToPages,
            FitWidth = 1,
            FitHeight = 1,
        };

        var book = Print(workbook, info, region);
        using var printed = SheetPrinter.Print(book, new SheetPrintSource("book.xlsx", "C:\\Reports\\"),
            DocumentLimits.Default, DateTime.Now, null, TestContext.Current.CancellationToken);

        var page = Assert.Single(printed.Pages);
        Assert.Equal(297 * PointsPerMm, page.Width, 1);
        Assert.Equal(210 * PointsPerMm, page.Height, 1);
    }

    /// <summary>
    /// Two sheets; "Data" has 60 rows of A:C, row 5 and column B hidden, a manual break after row 20, gridlines,
    /// headings, centring on both axes and a header, on A4 portrait.
    /// </summary>
    private (string Path, SpreadsheetWorkbookInfo Info) Workbook()
    {
        var book = new XlsxBuilder();
        book.Sheet("Other", Row(1, book.Text("A1", "other")));
        var rows = new System.Text.StringBuilder();
        for (var r = 1; r <= 60; r++)
        {
            var cells = Row(r, book.Text($"A{r}", $"a{r}"), book.Text($"B{r}", $"b{r}"), book.Text($"C{r}", $"c{r}"));
            rows.Append(r == 5 ? cells.Replace("<row r=\"5\">", "<row r=\"5\" hidden=\"1\">", StringComparison.Ordinal) : cells);
        }
        book.Sheet("Data", rows.ToString(),
            before: "<cols><col min=\"2\" max=\"2\" width=\"9\" hidden=\"1\" customWidth=\"1\"/></cols>",
            after: "<printOptions gridLines=\"1\" headings=\"1\" horizontalCentered=\"1\" verticalCentered=\"1\"/>" +
                   "<pageMargins left=\"0.7\" right=\"0.7\" top=\"0.75\" bottom=\"0.75\" header=\"0.4\" footer=\"0.3\"/>" +
                   "<pageSetup paperSize=\"9\" orientation=\"portrait\"/>" +
                   "<headerFooter><oddHeader>&amp;CQuarterly report&amp;RPage &amp;P of &amp;N</oddHeader></headerFooter>" +
                   "<rowBreaks count=\"1\" manualBreakCount=\"1\"><brk id=\"20\" max=\"16383\" man=\"1\"/></rowBreaks>");
        var path = book.Save(Path.Combine(fx.NewFolder(), "studio.xlsx"));
        return (path, SpreadsheetPrintInspector.Read(path));
    }

    /// <summary>The region's copy as the sheet printer reads it.</summary>
    private Workbook Print(string path, SpreadsheetWorkbookInfo info, SpreadsheetPrintRegion region)
    {
        var copy = Path.Combine(fx.NewFolder(), "sheet.xlsx");
        SpreadsheetPrintPackage.Create(path, copy, info, region);
        return Filee.Engines.Office.SpreadsheetConverter.Read(copy, "xlsx");
    }
}
