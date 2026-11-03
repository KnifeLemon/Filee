// "Pages per sheet" (#737): the layout without PDFs, then real files read back with PdfPig, which finds the text of
// each page (still text, not a picture) where its cell is.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Pdf;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Filee.Engines.Tests;

public class PdfSheetsTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    [Theory]
    [InlineData(2, false, 1, 2)]
    [InlineData(2, true, 2, 1)]
    [InlineData(4, false, 2, 2)]
    [InlineData(6, false, 2, 3)]
    [InlineData(6, true, 3, 2)]
    [InlineData(8, false, 2, 4)]
    [InlineData(9, true, 3, 3)]
    public void The_long_side_of_the_sheet_takes_more_pages(int pages, bool landscape, int columns, int rows)
    {
        Assert.Equal((columns, rows), PdfSheets.Grid(pages, landscape));
    }

    [Fact]
    public void Cells_fill_across_or_down_inside_the_margin_and_gap()
    {
        var sheet = new XSize(595.28, 841.89);
        var across = PdfSheets.Cells(sheet, new PdfOptions { SheetMarginMm = 10, SheetGapMm = 5 }, 4);
        var margin = 10 * 72 / 25.4;
        var gap = 5 * 72 / 25.4;
        Assert.Equal(4, across.Count);
        Assert.Equal(margin, across[0].X, 3);
        Assert.Equal(margin, across[0].Y, 3);
        Assert.Equal((sheet.Width - 2 * margin - gap) / 2, across[0].Width, 3);
        Assert.Equal(across[0].Right + gap, across[1].X, 3);
        Assert.Equal(across[0].Y, across[1].Y, 3);
        Assert.Equal(sheet.Width - margin, across[3].Right, 3);
        Assert.Equal(sheet.Height - margin, across[3].Bottom, 3);

        // Down first: the second page under the first.
        var down = PdfSheets.Cells(sheet, new PdfOptions { SheetOrder = PdfSheetOrder.DownFirst }, 4);
        Assert.Equal(down[0].X, down[1].X, 3);
        Assert.True(down[1].Y > down[0].Bottom);

        // A margin too large for the sheet still leaves room for every page.
        Assert.All(PdfSheets.Cells(sheet, new PdfOptions { SheetMarginMm = 500, SheetGapMm = 500 }, 9), c => Assert.True(c.Width > 0 && c.Height > 0));
    }

    [Fact]
    public void A_crop_box_and_a_turn_are_found_where_a_viewer_shows_them()
    {
        // A 400 × 300 page with a crop box 50 in from the left, 20 from the bottom, 300 × 200.
        var media = new PdfRectangle(new XPoint(0, 0), new XPoint(400, 300));
        var crop = new PdfRectangle(new XPoint(50, 20), new XPoint(350, 220));
        Assert.Equal(new ShownPage(400, 300, new XRect(50, 80, 300, 200)), PdfSheets.Shown(media, crop, 0));
        // Turned a quarter clockwise: 300 wide, 400 high; the page's bottom is on the left.
        Assert.Equal(new ShownPage(300, 400, new XRect(20, 50, 200, 300)), PdfSheets.Shown(media, crop, 90));
        Assert.Equal(new ShownPage(400, 300, new XRect(50, 20, 300, 200)), PdfSheets.Shown(media, crop, 180));
        Assert.Equal(new ShownPage(300, 400, new XRect(80, 50, 200, 300)), PdfSheets.Shown(media, crop, 270));
        Assert.Equal(new ShownPage(300, 400, new XRect(80, 50, 200, 300)), PdfSheets.Shown(media, crop, -90));
        // A media box away from the origin; no crop box (the media box itself).
        var moved = new PdfRectangle(new XPoint(100, 100), new XPoint(500, 400));
        Assert.Equal(new ShownPage(400, 300, new XRect(0, 0, 400, 300)), PdfSheets.Shown(moved, moved, 0));
    }

    [Fact]
    public void Auto_turns_the_sheet_the_way_that_fits_the_pages_larger()
    {
        var a4 = new ShownPage(595, 842, new XRect(0, 0, 595, 842));
        var wide = new ShownPage(842, 595, new XRect(0, 0, 842, 595));
        // Two portrait pages side by side on a landscape sheet; two landscape ones one above the other.
        Assert.True(PdfSheets.SheetSize(new PdfOptions(), 2, a4) is { Width: > 800 });
        Assert.True(PdfSheets.SheetSize(new PdfOptions(), 2, wide) is { Height: > 800 });
        Assert.True(PdfSheets.SheetSize(new PdfOptions(), 4, a4) is { Height: > 800 });
        Assert.True(PdfSheets.SheetSize(new PdfOptions { SheetOrientation = PdfSheetOrientation.Landscape }, 4, a4) is { Width: > 800 });
        Assert.Equal(new XSize(612, 792), PdfSheets.SheetSize(new PdfOptions { SheetPaper = PdfSheetPaper.Letter }, 4, a4));
        Assert.Equal(new XSize(400, 500), PdfSheets.SheetSize(new PdfOptions { SheetPaper = PdfSheetPaper.SameAsFirstPage },
            4, new ShownPage(400, 500, new XRect(0, 0, 400, 500))));
    }

    /// <summary>Pages with "P1", "P2", … in the middle; sizes go round <paramref name="sizes"/>.</summary>
    private static string Numbered(string folder, int count, params (double Width, double Height)[] sizes)
    {
        sizes = sizes.Length == 0 ? [(595, 842)] : sizes;
        var pdf = new PdfBuilder();
        var font = pdf.Font();
        for (var i = 0; i < count; i++)
        {
            var (w, h) = sizes[i % sizes.Length];
            pdf.NewPage(w, h);
            var text = $"P{i + 1}";
            pdf.Line(text, 40, font, w / 2 - pdf.Width(text, 40, font) / 2, h / 2);
        }
        return pdf.Save(Path.Combine(folder, "numbered.pdf"));
    }

    /// <summary>The middle of each word on a sheet, in points from its top left.</summary>
    private static Dictionary<string, XPoint> Words(UglyToad.PdfPig.Content.Page page) =>
        page.GetWords().ToDictionary(w => w.Text, w => new XPoint(
            (w.BoundingBox.Left + w.BoundingBox.Right) / 2, page.Height - (w.BoundingBox.Top + w.BoundingBox.Bottom) / 2));

    /// <summary>The middle of the one letter <paramref name="value"/> on a sheet (turned text is no word), from its top left.</summary>
    private static XPoint Letter(UglyToad.PdfPig.Content.Page page, string value)
    {
        var box = Assert.Single(page.Letters, l => l.Value == value).GlyphRectangle;
        return new XPoint((box.Left + box.Right) / 2, page.Height - (box.Top + box.Bottom) / 2);
    }

    private async Task<string> SheetsAsync(string source, PdfOptions options)
    {
        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "pdf", Pdf = options });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return Assert.Single(job.Outputs);
    }

    [Fact]
    public async Task Ten_pages_four_to_a_sheet_make_three_sheets_with_the_text_kept_in_order()
    {
        var output = await SheetsAsync(Numbered(fx.NewFolder(), 10), new PdfOptions { PagesPerSheet = 4 });
        using var pdf = PigDocument.Open(output);
        Assert.Equal(3, pdf.NumberOfPages);
        var first = pdf.GetPage(1);
        Assert.Equal((595, 842), ((int)Math.Round(first.Width), (int)Math.Round(first.Height)));
        var words = Words(first);
        // Across first: P1 top left, P2 top right, P3 bottom left, P4 bottom right, each in the middle of its cell.
        var cells = PdfSheets.Cells(new XSize(595.28, 841.89), new PdfOptions(), 4);
        for (var k = 0; k < 4; k++)
        {
            var at = words[$"P{k + 1}"];
            Assert.InRange(at.X, cells[k].X + cells[k].Width / 2 - 15, cells[k].X + cells[k].Width / 2 + 15);
            Assert.InRange(at.Y, cells[k].Y + cells[k].Height / 2 - 15, cells[k].Y + cells[k].Height / 2 + 15);
        }
        Assert.Equal(["P10", "P9"], Words(pdf.GetPage(3)).Keys.Order());
    }

    [Fact]
    public async Task Down_first_puts_the_second_page_under_the_first()
    {
        var output = await SheetsAsync(Numbered(fx.NewFolder(), 4), new PdfOptions { PagesPerSheet = 4, SheetOrder = PdfSheetOrder.DownFirst });
        using var pdf = PigDocument.Open(output);
        var words = Words(pdf.GetPage(1));
        Assert.Equal(words["P1"].X, words["P2"].X, 1);
        Assert.True(words["P2"].Y > words["P1"].Y + 200);
        Assert.True(words["P3"].X > words["P1"].X + 200);
    }

    [Fact]
    public async Task Two_portrait_pages_go_side_by_side_on_a_landscape_sheet_and_a_mixed_size_one_fits_its_cell()
    {
        // The second page is landscape and smaller: it is turned to fill its portrait cell, centred.
        var output = await SheetsAsync(Numbered(fx.NewFolder(), 2, (595, 842), (400, 200)),
            new PdfOptions { PagesPerSheet = 2, SheetMarginMm = 0, SheetGapMm = 0 });
        using var pdf = PigDocument.Open(output);
        var sheet = pdf.GetPage(1);
        Assert.True(sheet.Width > sheet.Height);
        Assert.InRange(Letter(sheet, "1").X, sheet.Width / 4 - 30, sheet.Width / 4 + 30);
        Assert.InRange(Letter(sheet, "2").X, sheet.Width * 3 / 4 - 30, sheet.Width * 3 / 4 + 30);
        Assert.InRange(Letter(sheet, "2").Y, sheet.Height / 2 - 30, sheet.Height / 2 + 30);
    }

    [Fact]
    public async Task A_cropped_and_a_turned_page_land_in_their_cells_as_a_viewer_shows_them()
    {
        var folder = fx.NewFolder();
        // A square page whose crop box keeps its middle (100 cut off every side), with "IN" in the middle of what shows
        // and "TOP" near its top.
        var pdf = new PdfBuilder();
        var font = pdf.Font();
        pdf.NewPage(400, 400);
        pdf.Line("IN", 20, font, 200 - pdf.Width("IN", 20, font) / 2, 207);
        pdf.Line("TOP", 20, font, 200 - pdf.Width("TOP", 20, font) / 2, 125);
        var source = pdf.Save(Path.Combine(folder, "cropped.pdf"));
        PdfBuilder.Turn(source, 0, inset: 100);

        var cropped = await SheetsAsync(source, new PdfOptions { PagesPerSheet = 2, SheetOrientation = PdfSheetOrientation.Portrait });
        using (var sheet = PigDocument.Open(cropped))
        {
            var page = sheet.GetPage(1);
            var cell = PdfSheets.Cells(new XSize(595.28, 841.89), new PdfOptions(), 2)[0];
            var words = Words(page);
            // What shows is the 200 × 200 middle, as large as the cell allows: "IN" is in the cell's middle, "TOP" above it.
            Assert.InRange(words["IN"].X, cell.X + cell.Width / 2 - 10, cell.X + cell.Width / 2 + 10);
            Assert.InRange(words["IN"].Y, cell.Y + cell.Height / 2 - 10, cell.Y + cell.Height / 2 + 10);
            Assert.True(words["TOP"].Y < words["IN"].Y - 50);
            Assert.Equal(words["IN"].X, words["TOP"].X, 5);
        }

        // Turned a quarter clockwise by /Rotate, as a viewer turns it: its top shows on the right.
        PdfBuilder.Turn(source, 90, drawTurned: false);
        var turned = await SheetsAsync(source, new PdfOptions { PagesPerSheet = 2, SheetOrientation = PdfSheetOrientation.Portrait });
        using (var sheet = PigDocument.Open(turned))
        {
            var page = sheet.GetPage(1);
            Assert.True(Letter(page, "O").X > Letter(page, "I").X + 50);
            Assert.InRange(Letter(page, "O").Y - Letter(page, "I").Y, -20, 20);
        }
    }

    [Fact]
    public async Task The_page_range_and_borders_apply()
    {
        var output = await SheetsAsync(Numbered(fx.NewFolder(), 6),
            new PdfOptions { PagesPerSheet = 4, PageRange = "2-4", SheetBorders = true });
        using var pdf = PigDocument.Open(output);
        Assert.Equal(1, pdf.NumberOfPages);
        Assert.Equal(["P2", "P3", "P4"], Words(pdf.GetPage(1)).Keys.Order());
    }
}
