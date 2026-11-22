// The built-in XLS / XLT writer (XlsWriter, BIFF8 in a compound file): workbooks built in code are written and read
// back by Filee's own XLS reader (values, number formats, fonts, fills, borders, alignment, merges, widths, heights,
// frozen rows, page setup), the BIFF8 limits are kept and reported, hostile models still give a valid file, and the
// compound file and record structure are checked sector by sector and record by record.

using System.Buffers.Binary;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Office;
using Filee.Engines.Office.Sheets;

namespace Filee.Engines.Tests;

public class XlsWriterTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>1 October 2026 as an Excel serial date.</summary>
    private const double October1 = 46296;

    private static readonly string[] OptionalEngines = ["pandoc", "libreoffice"];

    /// <summary>
    /// "보고서": a bold title merged over four columns with a fill, numbers in custom and built-in formats, a date, a
    /// boolean, an error, borders, wrapped, rotated and indented text, a custom width, a hidden column, a tall row, a
    /// hidden row, two frozen rows and a landscape page setup with print titles; "Notes": wide characters, a pattern
    /// fill and a right-to-left sheet.
    /// </summary>
    internal static Workbook Rich()
    {
        var book = new Workbook { DefaultFontName = "Arial", DefaultFontSize = 10 };
        var report = new Worksheet("보고서") { FrozenRows = 2, BaseColumnWidth = 9 };
        var title = CellStyle.Default with
        {
            Bold = true,
            FontSize = 14,
            Color = "#FFFFFF",
            Fill = "#1F4E79",
            Align = CellAlign.Center,
            VerticalAlign = CellVerticalAlign.Center,
            FontName = "Times New Roman",
        };
        report.Set(0, 0, new SheetCell("분기 보고서", title));
        report.Merges.Add(new CellRange(0, 0, 0, 3));
        var boxed = CellStyle.Default with
        {
            Left = new CellBorder("thin", "#000000"),
            Right = new CellBorder("medium", "#FF0000"),
            Top = new CellBorder("dashed", null),
            Bottom = new CellBorder("double", "#0000FF"),
        };
        report.Set(1, 0, new SheetCell("지점", boxed with { Italic = true, Underline = true }));
        report.Set(1, 1, new SheetCell("매출", boxed with { Strike = true, Script = 1 }));
        report.Set(2, 0, new SheetCell("서울", CellStyle.Default with { Indent = 2, Align = CellAlign.Left }));
        report.Set(2, 1, new SheetCell("1,350,000원", boxed, IsNumber: true) { Value = 1350000, NumberFormat = "#,##0\"원\"" });
        report.Set(2, 2, new SheetCell("10/1/2026", CellStyle.Default, IsNumber: true) { Value = October1, NumberFormat = "m/d/yyyy" });
        report.Set(2, 3, new SheetCell("12.8%", CellStyle.Default, IsNumber: true) { Value = 0.128, NumberFormat = "0.0%" });
        report.Set(3, 0, new SheetCell("TRUE", CellStyle.Default) { Value = 1, IsBoolean = true });
        report.Set(3, 1, new SheetCell("#DIV/0!", CellStyle.Default) { IsError = true });
        report.Set(3, 2, new SheetCell("3.5", CellStyle.Default, IsNumber: true) { Value = 3.5 });
        report.Set(3, 3, new SheetCell("0.25", CellStyle.Default, IsNumber: true) { Value = 0.25, NumberFormat = "0.00%" });
        report.Set(4, 0, new SheetCell("줄을 바꾸는 긴 글입니다", CellStyle.Default with { Wrap = true, VerticalAlign = CellVerticalAlign.Top }));
        report.Set(4, 1, new SheetCell("돌림", CellStyle.Default with { Rotation = 45 }));
        report.Set(4, 2, new SheetCell("아래로", CellStyle.Default with { Rotation = -30, Shrink = true }));
        report.Set(4, 3, new SheetCell("AB", CellStyle.Default with { Rotation = CellStyle.Stacked, SpecialAlign = CellSpecialAlign.CenterAcross, Align = CellAlign.Center }));
        report.Set(5, 0, new SheetCell("", CellStyle.Default with { Fill = "#FFFF00", DiagonalDown = new CellBorder("thin", "#808080") }));
        report.Set(6, 4, new SheetCell("숨긴 열", CellStyle.Default));
        report.Set(7, 0, new SheetCell("숨긴 행", CellStyle.Default));
        report.ColumnWidths[0] = 14;
        report.ColumnWidths[1] = 12.5;
        report.HiddenColumns.Add(4);
        report.RowHeights[4] = 45;
        report.HiddenRows.Add(7);
        report.TitleRows = (1, 1);
        report.PrintArea.Add(new CellRange(0, 0, 7, 3));
        report.RowBreaks.Add(5);
        report.PageSetup = new SheetPageSetup
        {
            PaperWidth = Paper.Letter.Width,
            PaperHeight = Paper.Letter.Height,
            Landscape = true,
            FitToPage = true,
            FitWidth = 1,
            FitHeight = 0,
            LeftMargin = 36,
            RightMargin = 36,
            TopMargin = 54,
            BottomMargin = 54,
            HeaderMargin = 18,
            FooterMargin = 18,
            CenterHorizontally = true,
            PrintGridLines = true,
            FirstPageNumber = 3,
            HeaderFooter = new SheetHeaderFooter { OddHeader = "&C&A", OddFooter = "&CPage &P of &N" },
        };
        book.Sheets.Add(report);

        var notes = new Worksheet("Notes") { RightToLeft = true };
        notes.Set(0, 0, new SheetCell("مرحبا 你好 😀", CellStyle.Default with { Color = "#123456" }));
        notes.Set(1, 0, new SheetCell("Ünïcödé, Latin-1 only", CellStyle.Default with { FillPattern = "lightGrid", Fill = "#FF6600", PatternBackground = "#EEEEEE" }));
        notes.Set(2, 0, new SheetCell("첫 줄\n둘째 줄", CellStyle.Default));
        book.Sheets.Add(notes);
        return book;
    }

    private string WriteXls(Workbook book, string name = "book.xls", bool template = false)
    {
        var path = Path.Combine(fx.NewFolder(), name);
        Assert.False(XlsWriter.Write(book, path, template, TestContext.Current.CancellationToken));
        return path;
    }

    // ───────────────────────── Round trip ─────────────────────────

    [Fact]
    public void Values_and_number_formats_come_back_from_the_xls()
    {
        using var culture = new CultureScope("en-US");
        var book = XlsReader.Read(WriteXls(Rich()));

        Assert.Equal(["보고서", "Notes"], book.Sheets.Select(s => s.Name));
        Assert.Equal(("Arial", 10.0), (book.DefaultFontName, book.DefaultFontSize));
        var report = book.Sheets[0];
        Assert.Equal("분기 보고서", report.Get(0, 0)!.Text);
        var won = report.Get(2, 1)!;
        Assert.Equal(("1,350,000원", true, 1350000.0, "#,##0\"원\""), (won.Text, won.IsNumber, won.Value, won.NumberFormat));
        var date = report.Get(2, 2)!;
        Assert.Equal(("10/1/2026", October1, "m/d/yyyy"), (date.Text, date.Value, date.NumberFormat));
        Assert.Equal(("12.8%", "0.0%"), (report.Get(2, 3)!.Text, report.Get(2, 3)!.NumberFormat));
        Assert.Equal(("25.00%", "0.00%"), (report.Get(3, 3)!.Text, report.Get(3, 3)!.NumberFormat)); // built-in 10
        Assert.Equal(("TRUE", true, 1.0), (report.Get(3, 0)!.Text, report.Get(3, 0)!.IsBoolean, report.Get(3, 0)!.Value));
        Assert.Equal(("#DIV/0!", true), (report.Get(3, 1)!.Text, report.Get(3, 1)!.IsError));
        Assert.Equal(("3.5", null), (report.Get(3, 2)!.Text, report.Get(3, 2)!.NumberFormat));

        var notes = book.Sheets[1];
        Assert.Equal("مرحبا 你好 😀", notes.Get(0, 0)!.Text);
        Assert.Equal("Ünïcödé, Latin-1 only", notes.Get(1, 0)!.Text);
        Assert.Equal("첫 줄\n둘째 줄", notes.Get(2, 0)!.Text);
    }

    [Fact]
    public void Fonts_fills_borders_and_alignment_come_back_from_the_xls()
    {
        var report = XlsReader.Read(WriteXls(Rich())).Sheets[0];

        var title = report.Get(0, 0)!.Style;
        Assert.Equal((true, 14.0, "Times New Roman", "#FFFFFF", "#1F4E79"), (title.Bold, title.FontSize, title.FontName, title.Color, title.Fill));
        Assert.Equal((CellAlign.Center, CellVerticalAlign.Center), (title.Align, title.VerticalAlign));

        var header = report.Get(1, 0)!.Style;
        Assert.Equal((true, true, "Arial", 10.0), (header.Italic, header.Underline, header.FontName, header.FontSize));
        Assert.Equal(new CellBorder("thin", "#000000"), header.Left);
        Assert.Equal(new CellBorder("medium", "#FF0000"), header.Right);
        Assert.Equal(new CellBorder("dashed", null), header.Top); // automatic colour
        Assert.Equal(new CellBorder("double", "#0000FF"), header.Bottom);
        Assert.Equal((true, 1), (report.Get(1, 1)!.Style.Strike, report.Get(1, 1)!.Style.Script));

        Assert.Equal((CellAlign.Left, 2), (report.Get(2, 0)!.Style.Align, report.Get(2, 0)!.Style.Indent));
        Assert.Equal((true, CellVerticalAlign.Top), (report.Get(4, 0)!.Style.Wrap, report.Get(4, 0)!.Style.VerticalAlign));
        Assert.Equal(45, report.Get(4, 1)!.Style.Rotation);
        Assert.Equal((-30, true), (report.Get(4, 2)!.Style.Rotation, report.Get(4, 2)!.Style.Shrink));
        Assert.Equal((CellStyle.Stacked, CellSpecialAlign.CenterAcross), (report.Get(4, 3)!.Style.Rotation, report.Get(4, 3)!.Style.SpecialAlign));
        // A blank cell with a fill and a diagonal border is kept.
        var blank = report.Get(5, 0)!;
        Assert.Equal(("", "#FFFF00", new CellBorder("thin", "#808080")), (blank.Text, blank.Style.Fill, blank.Style.DiagonalDown));

        var notes = XlsReader.Read(WriteXls(Rich())).Sheets[1];
        Assert.Equal("#123456", notes.Get(0, 0)!.Style.Color);
        var pattern = notes.Get(1, 0)!.Style;
        Assert.Equal(("lightGrid", "#FF6600", "#EEEEEE"), (pattern.FillPattern, pattern.Fill, pattern.PatternBackground));
        Assert.True(notes.RightToLeft);
    }

    [Fact]
    public void Merges_widths_heights_frozen_rows_and_page_setup_come_back_from_the_xls()
    {
        var report = XlsReader.Read(WriteXls(Rich())).Sheets[0];

        Assert.Equal(new CellRange(0, 0, 0, 3), Assert.Single(report.Merges));
        Assert.Equal((14.0, 12.5), (report.ColumnWidth(0), report.ColumnWidth(1)));
        Assert.Equal([4], report.HiddenColumns);
        Assert.Equal(45, report.RowHeights[4]);
        Assert.Equal([7], report.HiddenRows);
        Assert.Equal(9, report.BaseColumnWidth);
        Assert.Equal(2, report.FrozenRows);
        // Rows without a height of their own get the height Excel's autofit gives, at least the default row's.
        Assert.All(new[] { 1, 2, 3 }, r => Assert.True(report.AutoRowHeights[r] >= report.DefaultRowHeight, $"row {r}"));
        Assert.Equal(report.DefaultRowHeight, report.AutoRowHeights[0]); // the merged title doesn't count, as in Excel

        var setup = report.PageSetup;
        Assert.Equal((Paper.Letter.Width, Paper.Letter.Height, true), (setup.PaperWidth, setup.PaperHeight, setup.Landscape));
        Assert.Equal((true, 1, 0), (setup.FitToPage, setup.FitWidth, setup.FitHeight));
        Assert.Equal((36.0, 36.0, 54.0, 54.0, 18.0, 18.0), (setup.LeftMargin, setup.RightMargin, setup.TopMargin, setup.BottomMargin, setup.HeaderMargin, setup.FooterMargin));
        Assert.Equal((true, false, true, 3), (setup.CenterHorizontally, setup.CenterVertically, setup.PrintGridLines, setup.FirstPageNumber));
        Assert.Equal(("&C&A", "&CPage &P of &N"), (setup.HeaderFooter.OddHeader, setup.HeaderFooter.OddFooter));
        Assert.Equal((1, 1), report.TitleRows);
        Assert.Equal(new CellRange(0, 0, 7, 3), Assert.Single(report.PrintArea));
        Assert.Equal([5], report.RowBreaks);
    }

    [Fact]
    public async Task Xlsx_and_csv_convert_to_xls_and_xlt_with_the_built_in_writer()
    {
        using var culture = new CultureScope("en-US"); // the date's format 14 is the PC's short date
        var dir = fx.NewFolder();
        var xlsx = OfficeTests.SalesWorkbook(dir);
        var source = XlsxReader.Read(xlsx);

        var xls = Assert.Single(await ConvertAsync(xlsx, "xls"));
        var xlt = Assert.Single(await ConvertAsync(xlsx, "xlt"));
        Assert.Equal(".xlt", Path.GetExtension(xlt));
        Assert.Equal("xlt", Core.Formats.FormatRegistry.Detect(xlt)!.Id);
        foreach (var written in new[] { xls, xlt })
        {
            var book = XlsReader.Read(written);
            Assert.Equal(source.Sheets.Select(s => s.Name), book.Sheets.Select(s => s.Name));
            var sales = book.Sheets[0];
            Assert.Equal("1,350,000원", sales.Get(2, 1)!.Text);
            Assert.Equal(("10/1/2026", October1), (sales.Get(2, 2)!.Text, sales.Get(2, 2)!.Value));
            Assert.Equal("12.8%", sales.Get(2, 3)!.Text);
            Assert.Equal("2,220,000원", sales.Get(4, 1)!.Text); // the formula's cached value
            Assert.Equal(source.Sheets[0].Get(1, 0)!.Style.Fill, sales.Get(1, 0)!.Style.Fill);
            Assert.Equal(new CellRange(0, 0, 0, 3), Assert.Single(sales.Merges));
            Assert.Equal(2, sales.FrozenRows);
            Assert.Equal([14.0, 12.0], new[] { sales.ColumnWidth(0), sales.ColumnWidth(3) });
        }

        // CSV in, and the XLS back out as CSV.
        var csv = Path.Combine(dir, "표.csv");
        await File.WriteAllTextAsync(csv, "이름,금액\r\n김민지,1200\r\n", TestContext.Current.CancellationToken);
        var fromCsv = Assert.Single(await ConvertAsync(csv, "xls"));
        Assert.Equal(1200.0, XlsReader.Read(fromCsv).Sheets[0].Get(1, 1)!.Value);
        var back = Assert.Single(await ConvertAsync(fromCsv, "csv"));
        Assert.Equal("이름,금액\r\n김민지,1200\r\n", (await File.ReadAllTextAsync(back, TestContext.Current.CancellationToken)).TrimStart('﻿'));
    }

    [Fact]
    public void A_template_carries_the_template_record_and_nothing_else_differs()
    {
        var workbook = XlsWriter.Build(Rich(), template: false, out _, TestContext.Current.CancellationToken);
        var template = XlsWriter.Build(Rich(), template: true, out _, TestContext.Current.CancellationToken);

        var plain = Records(workbook, 0).Select(r => r.Id).ToList();
        var marked = Records(template, 0).Select(r => r.Id).ToList();
        Assert.Equal(0x0060, marked[1]); // TEMPLATE right after the BOF
        marked.RemoveAt(1);
        Assert.Equal(plain, marked);
        Assert.DoesNotContain(0x0060, plain);
    }

    // ───────────────────────── Routes ─────────────────────────

    [Theory]
    [InlineData("xlsx", "xls")]
    [InlineData("xlsm", "xls")]
    [InlineData("xlsb", "xls")]
    [InlineData("ods", "xls")]
    [InlineData("csv", "xls")]
    [InlineData("tsv", "xls")]
    [InlineData("xlsx", "xlt")]
    [InlineData("xls", "xlt")]
    [InlineData("xlt", "xls")]
    [InlineData("ods", "xlt")]
    public void Xls_and_xlt_are_written_without_libreoffice(string from, string to)
    {
        var builtIn = new ConverterCatalog(fx.Converters.Where(c => !OptionalEngines.Contains(c.Id))) { Priority = fx.Catalog.Priority }.CreatePlanner([]);
        Assert.Equal(["spreadsheet"], builtIn.Plan(from, to)!.Steps.Select(s => s.Converter.Id));
        // The built-in writer wins when LibreOffice is installed too.
        Assert.Equal(["spreadsheet"], fx.Catalog.CreatePlanner(["libreoffice"]).Plan(from, to)!.Steps.Select(s => s.Converter.Id));
    }

    // ───────────────────────── Limits ─────────────────────────

    [Fact]
    public async Task What_doesnt_fit_biff8_is_left_out_and_the_conversion_says_so()
    {
        var book = new Workbook();
        var sheet = new Worksheet("Big");
        for (var r = 0; r < XlsWriter.MaxRows + 10; r++)
            sheet.Set(r, 0, new SheetCell("", CellStyle.Default, IsNumber: true) { Value = r });
        sheet.Set(0, XlsWriter.MaxColumns + 5, new SheetCell("too far right", CellStyle.Default));
        sheet.Set(1, 1, new SheetCell(new string('가', XlsWriter.MaxText + 100), CellStyle.Default));
        sheet.Merges.Add(new CellRange(65_530, 250, 65_540, 260));
        book.Sheets.Add(sheet);
        var path = Path.Combine(fx.NewFolder(), "big.xls");

        Assert.True(XlsWriter.Write(book, path, cancellationToken: TestContext.Current.CancellationToken));

        var back = XlsReader.Read(path).Sheets[0];
        Assert.Equal(XlsWriter.MaxRows - 1, back.Rows.Keys.Max());
        Assert.Equal(XlsWriter.MaxRows - 1.0, back.Get(XlsWriter.MaxRows - 1, 0)!.Value);
        Assert.All(back.Rows.Values, cells => Assert.True(cells.Keys.Max() < XlsWriter.MaxColumns));
        Assert.Equal(XlsWriter.MaxText, back.Get(1, 1)!.Text.Length);
        Assert.Equal(new CellRange(65_530, 250, 65_535, 255), Assert.Single(back.Merges));

        // Through the converter: the note on the file says what happened.
        var csv = Path.Combine(fx.NewFolder(), "wide.csv");
        await File.WriteAllTextAsync(csv, string.Join(',', Enumerable.Range(0, 300)) + "\r\n", TestContext.Current.CancellationToken);
        var job = await fx.ConvertAsync([csv], new Preset { TargetFormat = "xls" });
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("notice.xls_limits", Assert.Single(job.Files).NoticeKey);
        Assert.Equal(255.0, XlsReader.Read(Assert.Single(job.Outputs)).Sheets[0].Get(0, 255)!.Value);
    }

    [Fact]
    public void Many_and_long_strings_go_on_in_continue_records()
    {
        var book = new Workbook();
        var sheet = new Worksheet("Strings");
        // Long ones in one byte and in two bytes a character (which may split only between characters), many short ones.
        sheet.Set(0, 0, new SheetCell(string.Concat(Enumerable.Repeat("ASCII text ", 2_000)), CellStyle.Default));
        sheet.Set(0, 1, new SheetCell(string.Concat(Enumerable.Repeat("한글 문장 ", 3_000)), CellStyle.Default));
        for (var r = 1; r < 3_000; r++)
            sheet.Set(r, r % 3, new SheetCell($"값 {r} — {new string('x', r % 40)}", CellStyle.Default));
        book.Sheets.Add(sheet);

        var stream = XlsWriter.Build(book, false, out var clipped, TestContext.Current.CancellationToken);
        Assert.False(clipped);
        var globals = Records(stream, 0);
        Assert.True(globals.SkipWhile(r => r.Id != 0x00FC).Skip(1).TakeWhile(r => r.Id == 0x003C).Count() >= 3);

        var back = XlsReader.Read(Save(stream)).Sheets[0];
        foreach (var (row, cells) in sheet.Rows)
            foreach (var (column, cell) in cells)
                Assert.Equal(cell.Text, back.Get(row, column)!.Text);
    }

    [Fact]
    public void More_colours_than_the_palette_holds_get_the_nearest_and_too_many_formats_are_reported()
    {
        var book = new Workbook();
        var sheet = new Worksheet("Colours");
        for (var i = 0; i < 80; i++)
            sheet.Set(i, 0, new SheetCell($"{i}", CellStyle.Default with { Fill = $"#{i * 3:X2}{255 - i:X2}{(i * 7) % 256:X2}" }));
        book.Sheets.Add(sheet);
        var back = XlsReader.Read(Save(XlsWriter.Build(book, false, out var clipped, TestContext.Current.CancellationToken))).Sheets[0];
        Assert.False(clipped);
        // The first 56 are exact (the default palette holds none of them); every one is a colour of the palette.
        Assert.All(Enumerable.Range(0, 56), i => Assert.Equal(sheet.Get(i, 0)!.Style.Fill, back.Get(i, 0)!.Style.Fill));
        Assert.All(Enumerable.Range(56, 24), i => Assert.NotNull(back.Get(i, 0)!.Style.Fill));

        var many = new Workbook();
        var formats = new Worksheet("Formats");
        for (var i = 0; i < XlsWriter.MaxCellFormats + 20; i++)
            formats.Set(i, 0, new SheetCell("x", CellStyle.Default with { Indent = i % 16, FontSize = 8 + i / 16 % 300 * 0.5 }));
        many.Sheets.Add(formats);
        XlsWriter.Build(many, false, out var tooMany, TestContext.Current.CancellationToken);
        Assert.True(tooMany);
    }

    // ───────────────────────── Hostile models ─────────────────────────

    [Fact]
    public void Odd_models_still_give_a_workbook_excel_and_filee_read()
    {
        var book = new Workbook { DefaultFontName = "Bad\0Font name that goes on far past thirty-one characters" };
        var a = new Worksheet("Sheet[1]:*?/\\");
        a.Set(-1, 0, new SheetCell("negative row", CellStyle.Default));
        a.Set(0, -3, new SheetCell("negative column", CellStyle.Default));
        a.Set(0, 0, new SheetCell("NaN", CellStyle.Default, IsNumber: true) { Value = double.NaN });
        a.Set(0, 1, new SheetCell("∞", CellStyle.Default, IsNumber: true) { Value = double.PositiveInfinity });
        a.Set(0, 2, new SheetCell("#WHAT?", CellStyle.Default) { IsError = true });
        a.Set(0, 3, new SheetCell("colours", CellStyle.Default with { Color = "red", Fill = "#12", Left = new CellBorder("weird", "nope"), Rotation = 400 }));
        a.Set(0, 4, new SheetCell("not a number", CellStyle.Default, IsNumber: true));
        a.Set(1, 0, new SheetCell("format", CellStyle.Default, IsNumber: true) { Value = 1, NumberFormat = new string('0', 300) });
        a.RowHeights[0] = double.NaN;
        a.RowHeights[1] = 0;
        a.ColumnWidths[0] = double.PositiveInfinity;
        a.ColumnWidths[1] = -5;
        a.Merges.Add(new CellRange(5, 5, 2, 2)); // corners swapped
        a.Merges.Add(new CellRange(-1, 0, 3, 3));
        a.PrintArea.Add(new CellRange(70_000, 0, 70_010, 3));
        a.RowBreaks.Add(-4);
        a.FrozenRows = 1_000_000;
        a.PageSetup = new SheetPageSetup { Scale = 9_999, LeftMargin = double.NaN, PaperWidth = 1, PaperHeight = 1, FirstPageNumber = int.MinValue };
        book.Sheets.Add(a);
        book.Sheets.Add(new Worksheet("sheet[1]:*?/\\")); // the same name once cleaned, ignoring case
        book.Sheets.Add(new Worksheet(new string('긴', 40)));

        var stream = XlsWriter.Build(book, false, out var clipped, TestContext.Current.CancellationToken);
        Assert.True(clipped); // the format too long to keep
        var back = XlsReader.Read(Save(stream));

        Assert.Equal(3, back.Sheets.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(back.Sheets, s => Assert.True(s.Name.Length <= 31));
        var sheet = back.Sheets[0];
        Assert.Equal("NaN", sheet.Get(0, 0)!.Text); // no finite number: kept as its text
        Assert.Equal("#WHAT?", sheet.Get(0, 2)!.Text);
        Assert.Equal("not a number", sheet.Get(0, 4)!.Text);
        Assert.Equal("1", sheet.Get(1, 0)!.Text);
        Assert.Null(sheet.Get(0, 3)!.Style.Fill);
        Assert.Empty(sheet.Merges);
        Assert.Empty(sheet.PrintArea);
        Assert.Equal(0, sheet.FrozenRows);
        Assert.Equal(31, back.DefaultFontName.Length);
        Assert.DoesNotContain('\0', back.DefaultFontName);
        Assert.All(Records(stream, 0), r => Assert.True(r.Data.Length <= 8224));
    }

    [Fact]
    public void An_empty_workbook_gets_one_empty_sheet()
    {
        var stream = XlsWriter.Build(new Workbook(), false, out var clipped, TestContext.Current.CancellationToken);
        Assert.False(clipped);
        Assert.True(stream.Length >= 4096); // padded to the mini stream cutoff, kept out of the mini stream
        var back = XlsReader.Read(Save(stream));
        Assert.Equal("Sheet1", Assert.Single(back.Sheets).Name);
        Assert.Empty(back.Sheets[0].Rows);
    }

    // ───────────────────────── Structure ─────────────────────────

    [Fact]
    public void The_compound_file_chains_every_sector_once_and_lists_the_workbook()
    {
        var path = WriteXls(Rich());
        var file = File.ReadAllBytes(path);
        var stream = XlsWriter.Build(Rich(), false, out _, TestContext.Current.CancellationToken);

        Assert.Equal(0xE11AB1A1E011CFD0, BinaryPrimitives.ReadUInt64LittleEndian(file));
        Assert.Equal(0, file.Length % 512);
        var sectors = file.Length / 512 - 1;
        Assert.Equal((ushort)0xFFFE, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(28))); // little-endian
        Assert.Equal((ushort)9, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(30))); // 512-byte sectors
        var fatSectors = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(44));
        var fatList = Enumerable.Range(0, fatSectors).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(76 + i * 4))).ToList();
        Assert.Equal(0xFFFFFFFE, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(68))); // no DIFAT for a small file
        var fat = fatList.SelectMany(s => Enumerable.Range(0, 128).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan((int)(s + 1) * 512 + i * 4)))).ToList();
        Assert.True(fat.Count >= sectors);
        Assert.All(fatList, s => Assert.Equal(0xFFFFFFFD, fat[(int)s])); // FAT sectors are marked as such

        var used = new HashSet<uint>();
        List<uint> Chain(uint start)
        {
            var chain = new List<uint>();
            for (var s = start; s != 0xFFFFFFFE; s = fat[(int)s])
            {
                Assert.True(s < sectors, $"sector {s} past the end");
                Assert.True(used.Add(s), $"sector {s} in two chains");
                chain.Add(s);
            }
            return chain;
        }
        var directory = Chain(BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(48)));
        var entries = directory.SelectMany(s => file.AsSpan((int)(s + 1) * 512, 512).ToArray()).ToArray();
        var (rootName, rootType) = (Encoding.Unicode.GetString(entries, 0, BinaryPrimitives.ReadUInt16LittleEndian(entries.AsSpan(64)) - 2), entries[66]);
        Assert.Equal(("Root Entry", (byte)5), (rootName, rootType));
        var child = BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(76));
        var workbook = entries.AsSpan((int)child * 128, 128);
        Assert.Equal("Workbook", Encoding.Unicode.GetString(workbook[..(BinaryPrimitives.ReadUInt16LittleEndian(workbook[64..]) - 2)]));
        Assert.Equal((byte)2, workbook[66]);
        var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(workbook[120..]);
        Assert.Equal(stream.Length, size);
        Assert.True(size >= 4096); // in regular sectors, not the mini stream
        var chain = Chain(BinaryPrimitives.ReadUInt32LittleEndian(workbook[116..]));
        Assert.Equal((size + 511) / 512, chain.Count);
        Assert.Equal(stream, chain.SelectMany(s => file.AsSpan((int)(s + 1) * 512, 512).ToArray()).Take((int)size).ToArray());
        // Every sector is in a chain, a FAT sector or free.
        Assert.All(Enumerable.Range(0, sectors).Where(s => !used.Contains((uint)s)), s => Assert.True(fat[s] is 0xFFFFFFFD or 0xFFFFFFFF, $"sector {s}"));

        using var cfb = Documents.CompoundFile.Open(path);
        Assert.Equal(stream, cfb.Read("Workbook"));
    }

    [Fact]
    public void The_records_point_where_they_say()
    {
        var book = Rich();
        for (var r = 10; r < 200; r++)
            book.Sheets[1].Set(r, 1, new SheetCell($"row {r}", CellStyle.Default));
        var stream = XlsWriter.Build(book, false, out _, TestContext.Current.CancellationToken);
        var globals = Records(stream, 0);

        Assert.Equal((0x0809, 0x0005), (globals[0].Id, (int)BinaryPrimitives.ReadUInt16LittleEndian(globals[0].Data.AsSpan(2))));
        Assert.Equal(0x000A, globals[^1].Id);
        Assert.True(globals.Count(r => r.Id == 0x00E0) >= 16); // the 16 XF records every workbook starts with
        Assert.DoesNotContain(globals, r => r.Data.Length > 8224);

        // BOUNDSHEET → each worksheet's BOF.
        var offsets = globals.Where(r => r.Id == 0x0085).Select(r => BinaryPrimitives.ReadInt32LittleEndian(r.Data)).ToList();
        Assert.Equal(2, offsets.Count);
        foreach (var offset in offsets)
        {
            Assert.Equal(0x0809, BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(offset)));
            Assert.Equal(0x0010, BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(offset + 6)));
            var sheet = Records(stream, offset);
            // INDEX → DEFCOLWIDTH and each block's DBCELL; DBCELL → the block's first ROW.
            var index = sheet.Single(r => r.Id == 0x020B);
            Assert.Equal(0x0055, BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(BinaryPrimitives.ReadInt32LittleEndian(index.Data.AsSpan(12)))));
            var blocks = (index.Data.Length - 16) / 4;
            Assert.Equal(sheet.Count(r => r.Id == 0x00D7), blocks);
            for (var i = 0; i < blocks; i++)
            {
                var dbCell = BinaryPrimitives.ReadInt32LittleEndian(index.Data.AsSpan(16 + i * 4));
                Assert.Equal(0x00D7, BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(dbCell)));
                var firstRow = dbCell - BinaryPrimitives.ReadInt32LittleEndian(stream.AsSpan(dbCell + 4));
                Assert.Equal(0x0208, BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(firstRow)));
                // The first offset leads from the end of the first ROW to the first cell of the first row.
                var firstCell = firstRow + 20 + BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(dbCell + 8));
                Assert.Contains(BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(firstCell)), new[] { 0x0203, 0x00FD, 0x0205, 0x0201, 0x0208, 0x00D7 });
            }
        }

        // EXTSST → where every eighth (or more) string of the SST starts, from its record.
        var position = 0;
        var at = new List<int>();
        foreach (var (id, data) in globals)
        {
            at.Add(position);
            position += 4 + data.Length;
        }
        var ext = globals.Single(r => r.Id == 0x00FF).Data;
        var sstAt = at[globals.FindIndex(r => r.Id == 0x00FC)];
        var first = BinaryPrimitives.ReadInt32LittleEndian(ext.AsSpan(2));
        Assert.Equal(sstAt + 12, first); // past the record header and the two counts
        Assert.Equal(12, BinaryPrimitives.ReadUInt16LittleEndian(ext.AsSpan(6)));
    }

    // ───────────────────────── Helpers ─────────────────────────

    private async Task<IReadOnlyList<string>> ConvertAsync(string input, string target)
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return [.. job.Outputs];
    }

    /// <summary>A Workbook stream in a compound file of its own, for the reader.</summary>
    private string Save(byte[] stream)
    {
        var writer = new Hwp.Binary.CompoundFileWriter();
        writer.Add("Workbook", stream);
        var path = Path.Combine(fx.NewFolder(), "book.xls");
        using (var output = File.Create(path))
            writer.Write(output);
        return path;
    }

    /// <summary>The records of the substream whose BOF is at <paramref name="offset"/>, through its EOF.</summary>
    private static List<(int Id, byte[] Data)> Records(byte[] stream, int offset)
    {
        var records = new List<(int Id, byte[] Data)>();
        for (var at = offset; at + 4 <= stream.Length;)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(at));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(at + 2));
            records.Add((id, stream.AsSpan(at + 4, length).ToArray()));
            at += 4 + length;
            if (id == 0x000A)
                break;
        }
        return records;
    }
}
