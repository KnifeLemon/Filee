// Excel binary workbooks (.xlsb, #1535) read by Filee's own XLSB reader. Every workbook is written here in code twice
// (XlsbBuilder): as BIFF12 records and as the same workbook in XLSX, and the XLSB reader must read what the XLSX
// reader reads, so every output the sheet model feeds (XLSX, ODS, CSV, PDF, HWPX, HTML) comes out the same. Hostile
// packages (records that lie about their size, too many records, ranges that expand without end) are refused quickly
// with the usual reasons. Set FILEE_RENDER_OUT to a folder to keep the printed pages of both twins.

using System.Diagnostics;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Documents;
using Filee.Engines.Office.Sheets;
using UglyToad.PdfPig;
using static Filee.Engines.Tests.XlsbBuilder;

namespace Filee.Engines.Tests;

public sealed class XlsbTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly DateTime Now = new(2026, 11, 19, 9, 0, 0);

    /// <summary>A workbook with every kind of cell, format, row and column setting and page setup the reader takes.</summary>
    internal static XlsbBuilder Twin()
    {
        var book = new XlsbBuilder();
        var sales = book.Sheet("지점별 매출");
        sales.Text(0, 0, "지점별 매출", Header).Merge(0, 0, 0, 4)
            .Text(1, 0, "지점", Header).Text(1, 1, "매출", Header).Text(1, 2, "날짜", Header).Text(1, 3, "비율", Header).Text(1, 4, "확인", Header)
            .Text(2, 0, "서울 강남").Number(2, 1, 1350000, Won).Rk(2, 2, RkInteger(46296), Date).Number(2, 3, 0.128, Percent).Bool(2, 4, true)
            .Inline(3, 0, "부산 서면").Rk(3, 1, RkInteger(870000), Won).Rk(3, 2, RkInteger(46297), Date).Rk(3, 3, RkInteger(9, times100: true), Percent).Bool(3, 4, false)
            .Text(4, 0, "합계", Accent).Formula(4, 1, 2220000.0, Won).Formula(4, 2, "계산됨").Formula(4, 3, (byte)0x07).Formula(4, 4, true)
            .Rich(5, 0, "Bold and red text", (0, 1), (9, 2)).Rk(5, 1, RkDouble(2.5)).Rk(5, 2, RkInteger(-1234, times100: true), Decimals).Error(5, 3, 0x2A)
            .Text(6, 0, "Pattern", Pattern).Text(6, 1, "Gradient", Gradient).Text(6, 2, "Diagonal", Diagonal).Text(6, 3, "Across", Across).Blank(6, 4, Pattern)
            .Height(0, 30).Height(6, 40).Height(7, 15, hidden: true).Text(8, 0, "after the hidden row").Height(8, 21.75, custom: false)
            .Width(0, 0, 18.7109375).Width(1, 3, 16.5).Width(5, 5, 9, hidden: true)
            .RowBreak(5).ColumnBreak(3);
        sales.FrozenRows = 2;
        sales.PageSetup = (9, true, 80, false, 1, 0, true, 3);
        sales.Margins = [0.5, 0.5, 1, 1, 0.4, 0.4];
        sales.GridLines = true;
        sales.CenterHorizontally = true;
        sales.HeaderFooter = ("&C&\"Calibri,Bold\"지점별 매출", "&R&P / &N", "&L첫 쪽", true);
        book.Sheet("Hidden", state: 1).Text(0, 0, "secret");
        var notes = book.Sheet("Notes");
        notes.Text(0, 0, "오른쪽에서 왼쪽").Number(1, 0, 3.25).Text(19, 9, "End");
        notes.RightToLeft = true;
        notes.PageSetup = (1, false, 100, true, 1, 2, false, null);
        book.Names.Add((0, "Print_Area", [(0, 0, 8, 4)]));
        book.Names.Add((0, "Print_Titles", [(1, 0, 1, 16383)]));
        book.Names.Add((2, "Print_Area", [(0, 0, 9, 4), (15, 5, 19, 9)]));
        return book;
    }

    private (string Xlsb, string Xlsx) Save(XlsbBuilder book, string name = "book")
    {
        var folder = fx.NewFolder();
        return (book.SaveXlsb(Path.Combine(folder, name + ".xlsb")), book.SaveXlsx(Path.Combine(folder, name + ".xlsx")));
    }

    // ───────────────────────── The same workbook as its XLSX twin ─────────────────────────

    [Fact]
    public void An_xlsb_reads_as_its_xlsx_twin()
    {
        var (xlsb, xlsx) = Save(Twin());

        AssertSame(XlsxReader.Read(xlsx), XlsbReader.Read(xlsb));
    }

    [Fact]
    public void Cells_read_with_their_values_and_formats()
    {
        var book = XlsbReader.Read(Save(Twin()).Xlsb);

        Assert.Equal(["지점별 매출", "Notes"], book.Sheets.Select(s => s.Name)); // the hidden sheet is left out
        var sheet = book.Sheets[0];
        Assert.Equal("1,350,000원", sheet.Get(2, 1)!.Text);
        Assert.Equal(1350000, sheet.Get(2, 1)!.Value);
        Assert.Equal("#,##0\"원\"", sheet.Get(2, 1)!.NumberFormat);
        Assert.Equal(46296, sheet.Get(2, 2)!.Value); // an RK integer
        Assert.Equal("12.8%", sheet.Get(2, 3)!.Text);
        Assert.Equal(0.09, sheet.Get(3, 3)!.Value!.Value, 10); // an RK integer stored × 100
        Assert.Equal("부산 서면", sheet.Get(3, 0)!.Text); // a string in the cell
        Assert.Equal(2.5, sheet.Get(5, 1)!.Value); // an RK double
        Assert.Equal("-12.34", sheet.Get(5, 2)!.Text);
        Assert.True(sheet.Get(2, 4)!.IsBoolean);
        Assert.Equal("TRUE", sheet.Get(2, 4)!.Text);

        // Formulas: the value Excel last calculated.
        Assert.Equal("2,220,000원", sheet.Get(4, 1)!.Text);
        Assert.Equal("계산됨", sheet.Get(4, 2)!.Text);
        Assert.True(sheet.Get(4, 3)!.IsError);
        Assert.Equal("#DIV/0!", sheet.Get(4, 3)!.Text);
        Assert.Equal("TRUE", sheet.Get(4, 4)!.Text);
        Assert.Equal("#N/A", sheet.Get(5, 3)!.Text);

        // Styles: fonts, fills, borders and alignment.
        var header = sheet.Get(1, 0)!.Style;
        Assert.True(header.Bold);
        Assert.Equal("#FFFFFF", header.Color);
        Assert.Equal("#1F4E79", header.Fill);
        Assert.Equal(CellAlign.Center, header.Align);
        Assert.Equal(CellVerticalAlign.Center, header.VerticalAlign);
        Assert.Equal("thin", header.Bottom.Style);
        var accent = sheet.Get(4, 0)!.Style;
        Assert.True(accent is { Italic: true, Underline: true, Strike: true, Wrap: true, Align: CellAlign.Right, VerticalAlign: CellVerticalAlign.Top });
        Assert.Equal(14, accent.FontSize);
        Assert.Equal("#C00000", accent.Color);
        Assert.Equal(("darkGray", "#FF0000", "#FFFFFF"), (sheet.Get(6, 0)!.Style.FillPattern, sheet.Get(6, 0)!.Style.Fill, sheet.Get(6, 0)!.Style.PatternBackground));
        Assert.Equal(2, sheet.Get(6, 0)!.Style.Indent);
        Assert.Equal([(0.0, "#FFFFFF"), (1.0, "#0000FF")], sheet.Get(6, 1)!.Style.Gradient!.Stops);
        Assert.Equal(45, sheet.Get(6, 1)!.Style.Rotation);
        Assert.True(sheet.Get(6, 1)!.Style.Shrink);
        Assert.True(sheet.Get(6, 2)!.Style.DiagonalUp.IsVisible && sheet.Get(6, 2)!.Style.DiagonalDown.IsVisible);
        Assert.Equal(CellStyle.Stacked, sheet.Get(6, 2)!.Style.Rotation);
        var across = sheet.Get(6, 3)!.Style;
        Assert.Equal(CellSpecialAlign.CenterAcross, across.SpecialAlign);
        Assert.True(across.DoubleUnderline && across.VerticalJustify);
        Assert.Equal("맑은 고딕", across.FontName);
        Assert.Equal(("double", "dashed", "#FF0000"), (across.Top.Style, across.Bottom.Style, across.Bottom.Color));
        Assert.NotNull(sheet.Get(6, 4)); // a blank cell with a fill

        // Rich text: the runs in their fonts.
        var rich = sheet.Get(5, 0)!;
        Assert.Equal(["Bold and ", "red text"], rich.Runs!.Select(r => r.Text));
        Assert.True(rich.Runs![0].Bold);
        Assert.Equal("#C00000", rich.Runs[1].Color);

        Assert.Equal("Calibri", book.DefaultFontName);
        Assert.Equal(11, book.DefaultFontSize);
    }

    [Fact]
    public void Rows_columns_and_the_page_setup_are_read()
    {
        var book = XlsbReader.Read(Save(Twin()).Xlsb);
        var sheet = book.Sheets[0];

        Assert.Equal(30, sheet.RowHeights[0]);
        Assert.Equal(21.75, sheet.AutoRowHeights[8]);
        Assert.Contains(7, sheet.HiddenRows);
        Assert.Contains(5, sheet.HiddenColumns);
        Assert.Equal(16.5, sheet.ColumnWidth(2));
        Assert.Equal(new CellRange(0, 0, 0, 4), Assert.Single(sheet.Merges));
        Assert.Equal(2, sheet.FrozenRows);
        Assert.Equal([5], sheet.RowBreaks);
        Assert.Equal([3], sheet.ColumnBreaks);
        Assert.Equal(15, sheet.DefaultRowHeight);
        Assert.Equal(8, sheet.BaseColumnWidth);
        Assert.Equal((1, 1), sheet.TitleRows);
        Assert.Equal(new CellRange(0, 0, 8, 4), Assert.Single(sheet.PrintArea));

        var setup = sheet.PageSetup;
        Assert.True(setup is { Landscape: true, Scale: 80, OverThenDown: true, FirstPageNumber: 3, PrintGridLines: true, CenterHorizontally: true });
        Assert.Equal(Paper.A4, (setup.PaperWidth, setup.PaperHeight));
        Assert.Equal(36, setup.LeftMargin);
        Assert.Equal(72, setup.TopMargin);
        Assert.Equal("&R&P / &N", setup.HeaderFooter.OddFooter);
        Assert.True(setup.HeaderFooter.DifferentFirst);
        Assert.Equal("&L첫 쪽", setup.HeaderFooter.FirstHeader);

        var notes = book.Sheets[1];
        Assert.True(notes.RightToLeft);
        Assert.True(notes.PageSetup is { FitToPage: true, FitWidth: 1, FitHeight: 2 });
        Assert.Equal([new CellRange(0, 0, 9, 4), new CellRange(15, 5, 19, 9)], notes.PrintArea);
    }

    [Fact]
    public void Dates_of_a_1904_workbook_mean_the_same_day()
    {
        var book = new XlsbBuilder { Date1904 = true };
        book.Sheet("Dates").Rk(0, 0, RkInteger(44834), Date);
        var (xlsb, xlsx) = Save(book);

        var cell = XlsbReader.Read(xlsb).Sheets[0].Get(0, 0)!;
        Assert.Equal(44834 + 1462, cell.Value);
        Assert.Equal(XlsxReader.Read(xlsx).Sheets[0].Get(0, 0)!.Text, cell.Text);
    }

    [Fact]
    public void A_picture_on_the_sheet_is_read_from_its_drawing()
    {
        var book = new XlsbBuilder();
        var sheet = book.Sheet("Picture").Text(19, 5, "End");
        sheet.DrawingId = "rIdD";
        sheet.Relationships.Add(("rIdD", "drawing", "../drawings/drawing1.xml"));
        book.Parts["xl/drawings/drawing1.xml"] =
            "<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><xdr:absoluteAnchor><xdr:pos x=\"254000\" y=\"254000\"/><xdr:ext cx=\"1016000\" cy=\"508000\"/>" +
            "<xdr:pic><xdr:nvPicPr><xdr:cNvPr id=\"2\" name=\"Picture 1\"/><xdr:cNvPicPr/></xdr:nvPicPr><xdr:blipFill><a:blip r:embed=\"rIdP\"/><a:stretch><a:fillRect/></a:stretch></xdr:blipFill>" +
            "<xdr:spPr><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></xdr:spPr></xdr:pic><xdr:clientData/></xdr:absoluteAnchor></xdr:wsDr>";
        book.Parts["xl/drawings/_rels/drawing1.xml.rels"] = OpcWriter.Relationships([("rIdP", "image", "../media/image1.png")]);
        book.Media["xl/media/image1.png"] = OdpBuilder.Png(4, 4, SkiaSharp.SKColors.Teal);
        var (xlsb, xlsx) = Save(book);

        var picture = Assert.IsType<SheetPicture>(Assert.Single(XlsbReader.Read(xlsb).Sheets[0].Drawings));
        Assert.Equal((80.0, 40.0), (picture.Width, picture.Height));
        Assert.Null(SheetCapabilities.FirstUnsupported(xlsb, "xlsb"));
        Assert.Single(XlsxReader.Read(xlsx).Sheets[0].Drawings);
    }

    [Fact]
    public void A_chart_is_named_before_printing()
    {
        var book = new XlsbBuilder();
        var sheet = book.Sheet("Chart").Text(0, 0, "x");
        sheet.DrawingId = "rIdD";
        sheet.Relationships.Add(("rIdD", "drawing", "../drawings/drawing1.xml"));
        book.Parts["xl/drawings/drawing1.xml"] =
            "<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><xdr:absoluteAnchor><xdr:pos x=\"0\" y=\"0\"/><xdr:ext cx=\"1016000\" cy=\"508000\"/>" +
            "<xdr:graphicFrame macro=\"\"><xdr:nvGraphicFramePr><xdr:cNvPr id=\"2\" name=\"Chart 1\"/><xdr:cNvGraphicFramePr/></xdr:nvGraphicFramePr>" +
            "<xdr:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/></xdr:xfrm><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\">" +
            "<c:chart xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" r:id=\"rIdC\"/></a:graphicData></a:graphic></xdr:graphicFrame><xdr:clientData/></xdr:absoluteAnchor></xdr:wsDr>";
        var (xlsb, _) = Save(book);

        Assert.Equal("charts", SheetCapabilities.FirstUnsupported(xlsb, "xlsb"));
    }

    // ───────────────────────── Routes and outputs ─────────────────────────

    [Fact]
    public void Xlsb_is_a_spreadsheet_with_its_routes()
    {
        Assert.Equal("xlsb", FormatRegistry.Detect("Budget.xlsb")?.Id);
        Assert.False(FormatRegistry.Get("xlsb").Writable);
        var planner = fx.Catalog.CreatePlanner();
        Assert.Equal("sheet-print", Assert.Single(planner.Plan("xlsb", "pdf")!.Steps).Converter.Id);
        foreach (var to in new[] { "xlsx", "ods", "csv", "tsv" })
            Assert.Equal("spreadsheet", Assert.Single(planner.Plan("xlsb", to)!.Steps).Converter.Id);
        Assert.Equal(["hwpx-writer"], planner.Plan("xlsb", "hwpx")!.Steps.Select(s => s.Converter.Id));
        Assert.NotNull(planner.Plan("xlsb", "html"));
    }

    [Fact]
    public async Task An_xlsb_converts_to_xlsx_csv_and_pdf_in_a_job()
    {
        var (xlsb, xlsx) = Save(Twin(), "sales");

        var toXlsx = await fx.ConvertAsync([xlsb], new Preset { TargetFormat = "xlsx" });
        Assert.Equal(JobState.Completed, toXlsx.State);
        var written = XlsxReader.Read(toXlsx.Outputs.Single());
        Assert.Equal("1,350,000원", written.Sheets[0].Get(2, 1)!.Text);
        Assert.Equal(1350000, written.Sheets[0].Get(2, 1)!.Value);

        var toCsv = await fx.ConvertAsync([xlsb], new Preset { TargetFormat = "csv" });
        Assert.Equal(JobState.Completed, toCsv.State);
        Assert.Contains("서울 강남", File.ReadAllText(toCsv.Outputs.First()));

        var toPdf = await fx.ConvertAsync([xlsb], new Preset { TargetFormat = "pdf" });
        Assert.Equal(JobState.Completed, toPdf.State);
        using var pdf = PdfDocument.Open(toPdf.Outputs.Single());
        var text = string.Join(" ", pdf.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text));
        Assert.Contains("1,350,000원", text);
        Assert.Contains("계산됨", text);
    }

    [Fact]
    public void The_printed_pages_are_those_of_the_xlsx_twin()
    {
        var (xlsb, xlsx) = Save(Twin(), "sales");

        var fromXlsb = Pages(XlsbReader.Read(xlsb), "sales-xlsb");
        var fromXlsx = Pages(XlsxReader.Read(xlsx), "sales-xlsx");
        Assert.Equal(fromXlsx.Count, fromXlsb.Count);
        Assert.Equal(fromXlsx, fromXlsb);
    }

    /// <summary>The words of each printed page (and, with FILEE_RENDER_OUT, the PDF and a picture of each page).</summary>
    private static List<string> Pages(Workbook book, string name)
    {
        var source = new SheetPrintSource(name + ".xlsb", "C:\\Reports\\");
        var bytes = SheetPrinter.ToPdf(book, source, now: Now);
        if (Environment.GetEnvironmentVariable("FILEE_RENDER_OUT") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, name + ".pdf"), bytes);
            using var printed = SheetPrinter.Print(book, source, DocumentLimits.Default, Now, null, default);
            for (var i = 0; i < printed.Pages.Count; i++)
            {
                using var bitmap = printed.Pages[i].Render(100);
                using var png = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(Path.Combine(folder, $"{name}-{i + 1}.png"), png.ToArray());
            }
        }
        using var pdf = PdfDocument.Open(bytes);
        return [.. pdf.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text)))];
    }

    // ───────────────────────── Files that are something else ─────────────────────────

    [Fact]
    public void An_xlsx_or_xls_with_an_xlsb_name_is_read_as_what_it_is()
    {
        var folder = fx.NewFolder();
        var xlsx = Twin().SaveXlsx(Path.Combine(folder, "web.xlsb"));
        Assert.Equal("1,350,000원", XlsbReader.Read(xlsx).Sheets[0].Get(2, 1)!.Text);

        var xls = new XlsBuilder();
        xls.Sheet("Old").Text(0, 0, "BIFF8");
        var path = xls.Save(Path.Combine(folder, "old.xlsb"));
        Assert.Equal("BIFF8", XlsbReader.Read(path).Sheets[0].Get(0, 0)!.Text);
    }

    [Fact]
    public void An_encrypted_workbook_asks_for_the_password_to_be_removed()
    {
        var path = Path.Combine(fx.NewFolder(), "locked.xlsb");
        CompoundFile.Write(path, "EncryptedPackage", new byte[600]);

        var error = Assert.Throws<InvalidDataException>(() => XlsbReader.Read(path));
        Assert.Contains("password", error.Message, StringComparison.Ordinal);
    }

    // ───────────────────────── Hostile input ─────────────────────────

    [Fact]
    public void Hostile_records_are_refused_quickly()
    {
        var watch = Stopwatch.StartNew();

        // A record that claims 200 MB which aren't there: damage, found without reading or allocating them.
        Assert.Throws<InvalidDataException>(() => XlsbReader.Read(Hostile(sheet => sheet.ExtraRecords.Add(RecordWriter.Header(5, 200 * 1024 * 1024)))));

        // A cell record of 2 MB: more than a record the reader takes apart may hold.
        var refused = Assert.Throws<ConversionRefusedException>(() => XlsbReader.Read(Hostile(sheet =>
            sheet.ExtraRecords.Add(RecordWriter.Encode(6, [.. U32(0), .. U32(0), .. U32(1_000_000), .. new byte[2_000_000]])))));
        Assert.Equal(DocumentLimits.ErrorKey, refused.ErrorKey);

        // A string whose length is more than its record holds.
        Assert.Throws<InvalidDataException>(() => XlsbReader.Read(Hostile(sheet =>
            sheet.ExtraRecords.Add(RecordWriter.Encode(6, [.. U32(0), .. U32(0), .. U32(0x7FFFFFFF), 0x41, 0])))));

        // Column ranges over the whole sheet, again and again.
        var columns = Hostile(sheet =>
        {
            for (var i = 0; i < 300; i++)
                sheet.ExtraRecords.Add(RecordWriter.Encode(60, [.. U32(0), .. U32(16383), .. U32(2560), .. U32(0), .. U16(0)]));
        });
        Assert.Equal(DocumentLimits.ErrorKey, Assert.Throws<ConversionRefusedException>(() => XlsbReader.Read(columns)).ErrorKey);

        // More records than a workbook may hold (a lowered limit stands for 5,000,000).
        var many = Hostile(sheet =>
        {
            for (var i = 0; i < 2000; i++)
                sheet.ExtraRecords.Add(RecordWriter.Encode(1, [.. U32((uint)(i % 100)), .. U32(0)]));
        });
        Assert.Equal(DocumentLimits.ErrorKey,
            Assert.Throws<ConversionRefusedException>(() => XlsbReader.Read(many, DocumentLimits.Default with { MaxXmlElements = 1000 })).ErrorKey);

        // Cells and ranges beyond the sheet, styles and strings that don't exist, names with foreign tokens: read, left out.
        var odd = Hostile(sheet =>
        {
            sheet.ExtraRecords.Add(RecordWriter.Encode(5, [.. U32(2), .. U32(0), .. F64(double.NaN)]));
            sheet.ExtraRecords.Add(RecordWriter.Encode(7, [.. U32(16384), .. U32(0), .. U32(0)]));
            sheet.ExtraRecords.Add(RecordWriter.Encode(7, [.. U32(1), .. U32(0xFFFFFF), .. U32(999999)]));
            sheet.ExtraRecords.Add(RecordWriter.Encode(0, [.. U32(0xFFFFFFFF), .. U32(0), .. U16(0), 0, 0, 0, .. U32(0)]));
        }, book =>
        {
            book.ExtraWorkbookRecords.Add(RecordWriter.Encode(39, [.. U32(0x20), 0, .. U32(0), .. WideString("_xlnm.Print_Area"), .. U32(3), 0x17, 0xFF, 0xFF, .. U32(0)]));
            book.ExtraWorkbookRecords.Add(RecordWriter.Encode(39, [.. U32(0x20), 0, .. U32(0x7FFFFFFF), .. WideString("Print_Titles"), .. U32(99)]));
        });
        var read = XlsbReader.Read(odd);
        Assert.Equal("#NUM!", read.Sheets[0].Get(0, 2)!.Text);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"Took {watch.Elapsed}");
    }

    /// <summary>A one-sheet XLSB whose sheet data ends with what <paramref name="sheet"/> adds.</summary>
    private string Hostile(Action<XlsbSheet> sheet, Action<XlsbBuilder>? book = null)
    {
        var builder = new XlsbBuilder();
        sheet(builder.Sheet("Data").Text(0, 0, "x"));
        book?.Invoke(builder);
        return builder.SaveXlsb(Path.Combine(fx.NewFolder(), "hostile.xlsb"));
    }

    // ───────────────────────── Comparison ─────────────────────────

    internal static void AssertSame(Workbook expected, Workbook actual)
    {
        Assert.Equal(expected.DefaultFontName, actual.DefaultFontName);
        Assert.Equal(expected.DefaultFontSize, actual.DefaultFontSize);
        Assert.Equal(expected.Sheets.Select(s => s.Name), actual.Sheets.Select(s => s.Name));
        for (var i = 0; i < expected.Sheets.Count; i++)
        {
            var (x, y) = (expected.Sheets[i], actual.Sheets[i]);
            Assert.Equal(x.Rows.SelectMany(r => r.Value.Keys.Select(c => (r.Key, c))), y.Rows.SelectMany(r => r.Value.Keys.Select(c => (r.Key, c))));
            foreach (var (row, cells) in x.Rows)
            {
                foreach (var (column, cell) in cells)
                {
                    var other = y.Get(row, column)!;
                    var at = $"{x.Name}!{XlsbBuilder.Column(column)}{row + 1}";
                    Assert.True((cell.Text, cell.Value, cell.NumberFormat, cell.IsNumber, cell.IsBoolean, cell.IsError)
                        == (other.Text, other.Value, other.NumberFormat, other.IsNumber, other.IsBoolean, other.IsError), $"{at}: {cell} / {other}");
                    Assert.True(cell.Style with { Gradient = null } == other.Style with { Gradient = null }, $"{at}: {cell.Style} / {other.Style}");
                    Assert.Equal(cell.Style.Gradient?.Degree, other.Style.Gradient?.Degree);
                    Assert.Equal(cell.Style.Gradient?.Stops ?? [], other.Style.Gradient?.Stops ?? []);
                    Assert.Equal(cell.Runs ?? [], other.Runs ?? []);
                }
            }
            Assert.Equal(x.ColumnWidths.OrderBy(p => p.Key), y.ColumnWidths.OrderBy(p => p.Key));
            Assert.Equal(x.RowHeights.OrderBy(p => p.Key), y.RowHeights.OrderBy(p => p.Key));
            Assert.Equal(x.AutoRowHeights.OrderBy(p => p.Key), y.AutoRowHeights.OrderBy(p => p.Key));
            Assert.Equal(x.HiddenRows.Order(), y.HiddenRows.Order());
            Assert.Equal(x.HiddenColumns.Order(), y.HiddenColumns.Order());
            Assert.Equal(x.Merges, y.Merges);
            Assert.Equal(x.RowBreaks, y.RowBreaks);
            Assert.Equal(x.ColumnBreaks, y.ColumnBreaks);
            Assert.Equal(x.PrintArea, y.PrintArea);
            Assert.Equal((x.FrozenRows, x.TitleRows, x.TitleColumns, x.RightToLeft), (y.FrozenRows, y.TitleRows, y.TitleColumns, y.RightToLeft));
            Assert.Equal((x.DefaultColumnWidth, x.HasDefaultColumnWidth, x.BaseColumnWidth, x.DefaultRowHeight),
                (y.DefaultColumnWidth, y.HasDefaultColumnWidth, y.BaseColumnWidth, y.DefaultRowHeight));
            var empty = new Dictionary<string, (byte[], double, double)>();
            Assert.Equal(x.PageSetup with { HeaderFooter = x.PageSetup.HeaderFooter with { Pictures = empty } },
                y.PageSetup with { HeaderFooter = y.PageSetup.HeaderFooter with { Pictures = empty } });
            Assert.Equal(x.Drawings.Count, y.Drawings.Count);
        }
    }
}
