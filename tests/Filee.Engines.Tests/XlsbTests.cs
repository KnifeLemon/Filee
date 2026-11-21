// Excel binary workbooks (.xlsb, BIFF12) without Excel or LibreOffice: reading values, dates, inline/shared strings,
// booleans, errors, merged cells, hidden sheets and empty sheets, converting to XLSX / CSV / HWPX / PDF, complex
// script detection, error handling for encrypted or invalid files, and format / MIME / content detection.

using System.IO.Compression;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Web;
using Filee.Engines.Hwp;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office;
using Filee.Engines.Office.Sheets;
using Filee.Engines.Signed;

namespace Filee.Engines.Tests;

public class XlsbTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <summary>1 October 2026 as an Excel serial date.</summary>
    private const double October1 = 46296;

    private static readonly string[] OptionalEngines = ["pandoc", "libreoffice"];

    private RoutePlanner BuiltInPlanner(params string[] assumeInstalled) =>
        new ConverterCatalog(fx.Converters.Where(c => !OptionalEngines.Contains(c.Id))) { Priority = fx.Catalog.Priority }
            .CreatePlanner(assumeInstalled);

    private async Task<IReadOnlyList<string>> ConvertAsync(string input, string target)
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return [.. job.Outputs];
    }

    /// <summary>
    /// Workbook with visible sheets ("Sales", "Notes", "Empty") and a hidden sheet ("Secret"), covering shared strings,
    /// inline strings, numbers, dates, booleans, errors, blank cells and a merged range.
    /// </summary>
    private static string SampleXlsb(string folder)
    {
        var xlsb = new XlsbBuilder();
        xlsb.Sheet("Sales")
            .Text(0, 0, "Quarterly Report", XlsbBuilder.Centered).Merge(0, 0, 0, 3)
            .Text(1, 0, "Region").Text(1, 1, "Amount").Text(1, 2, "Date").Text(1, 3, "Status")
            .Text(2, 0, "Seoul").Number(2, 1, 1350000, XlsbBuilder.Won).Number(2, 2, October1, XlsbBuilder.Date).InlineText(2, 3, "Inline OK")
            .Text(3, 0, "Busan").Number(3, 1, 3.5).Bool(3, 2, true).Error(3, 3, 0x07)
            .Blank(4, 0);
        xlsb.Sheet("Secret", hidden: true).Text(0, 0, "Must stay hidden");
        xlsb.Sheet("Notes").Text(0, 0, "Line 1\nLine 2").Number(1, 0, 42);
        xlsb.Sheet("Empty");
        return xlsb.Save(Path.Combine(folder, "report.xlsb"));
    }

    [Fact]
    public void Xlsb_reader_reads_sheets_types_dates_merges_and_skips_hidden_sheets()
    {
        var book = XlsbReader.Read(SampleXlsb(fx.NewFolder()));

        Assert.Equal(["Sales", "Notes", "Empty"], book.Sheets.Select(s => s.Name));
        var sales = book.Sheets[0];
        Assert.Equal("Quarterly Report", sales.Get(0, 0)!.Text);
        Assert.Equal(CellAlign.Center, sales.Get(0, 0)!.Style.Align);
        Assert.Equal(new CellRange(0, 0, 0, 3), Assert.Single(sales.Merges));

        Assert.Equal("Seoul", sales.Get(2, 0)!.Text);
        var won = sales.Get(2, 1)!;
        Assert.Equal(("1,350,000원", true, 1350000.0, "#,##0\"원\""), (won.Text, won.IsNumber, won.Value, won.NumberFormat));
        var date = sales.Get(2, 2)!;
        Assert.Equal(("2026-10-01", October1, "yyyy-mm-dd"), (date.Text, date.Value, date.NumberFormat));
        Assert.Equal("Inline OK", sales.Get(2, 3)!.Text);

        Assert.Equal(("3.5", true, 3.5, null), (sales.Get(3, 1)!.Text, sales.Get(3, 1)!.IsNumber, sales.Get(3, 1)!.Value, sales.Get(3, 1)!.NumberFormat));
        Assert.Equal(("TRUE", true, 1.0), (sales.Get(3, 2)!.Text, sales.Get(3, 2)!.IsBoolean, sales.Get(3, 2)!.Value));
        Assert.Equal(("#DIV/0!", false), (sales.Get(3, 3)!.Text, sales.Get(3, 3)!.IsNumber));
        Assert.Null(sales.Get(4, 0));

        var notes = book.Sheets[1];
        Assert.Equal("Line 1\nLine 2", notes.Get(0, 0)!.Text);
        Assert.Equal("42", notes.Get(1, 0)!.Text);

        Assert.Null(book.Sheets[2].UsedRange());
    }

    [Fact]
    public async Task Xlsb_to_xlsx_preserves_sheets_values_dates_and_merges()
    {
        var xlsx = Assert.Single(await ConvertAsync(SampleXlsb(fx.NewFolder()), "xlsx"));

        using (var zip = ZipFile.OpenRead(xlsx))
        {
            using var wbStream = zip.GetEntry("xl/workbook.xml")!.Open();
            var wb = XElement.Load(wbStream);
            Assert.Equal(["Sales", "Notes", "Empty"], wb.Descendants(S + "sheet").Select(s => (string)s.Attribute("name")!));
        }

        var result = XlsxReader.Read(xlsx);
        Assert.Equal(["Sales", "Notes", "Empty"], result.Sheets.Select(s => s.Name));
        var sales = result.Sheets[0];
        Assert.Equal("Quarterly Report", sales.Get(0, 0)!.Text);
        Assert.Equal("1,350,000원", sales.Get(2, 1)!.Text);
        Assert.Equal(("2026-10-01", October1), (sales.Get(2, 2)!.Text, sales.Get(2, 2)!.Value));
        Assert.Equal("Inline OK", sales.Get(2, 3)!.Text);
        Assert.Equal("3.5", sales.Get(3, 1)!.Text);
        Assert.Equal(("TRUE", true), (sales.Get(3, 2)!.Text, sales.Get(3, 2)!.IsBoolean));
        Assert.Equal("#DIV/0!", sales.Get(3, 3)!.Text);
        Assert.Equal(new CellRange(0, 0, 0, 3), Assert.Single(sales.Merges));
    }

    [Fact]
    public async Task Xlsb_to_csv_writes_one_file_per_non_empty_sheet()
    {
        var outputs = await ConvertAsync(SampleXlsb(fx.NewFolder()), "csv");

        Assert.Equal(["report_Sales.csv", "report_Notes.csv"], outputs.Select(Path.GetFileName));
        var salesCsv = (await File.ReadAllTextAsync(outputs[0], TestContext.Current.CancellationToken)).TrimStart('\uFEFF');
        Assert.Contains("Seoul,\"1,350,000원\",2026-10-01,Inline OK", salesCsv);
        Assert.Contains("Busan,3.5,TRUE,#DIV/0!", salesCsv);
        var notesCsv = (await File.ReadAllTextAsync(outputs[1], TestContext.Current.CancellationToken)).TrimStart('\uFEFF');
        Assert.Contains("\"Line 1\nLine 2\"", notesCsv);
        Assert.Contains("42", notesCsv);
    }

    [Fact]
    public async Task Xlsb_to_hwpx_and_docx_produce_non_empty_documents()
    {
        var xlsb = SampleXlsb(fx.NewFolder());

        var hwpx = Assert.Single(await ConvertAsync(xlsb, "hwpx"));
        Assert.True(new FileInfo(hwpx).Length > 0);
        using (var zip = ZipFile.OpenRead(hwpx))
        {
            using var reader = new StreamReader(zip.GetEntry("Contents/section0.xml")!.Open());
            var section = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
            Assert.Contains("Quarterly Report", section);
            Assert.Contains("2026-10-01", section);
        }

        var docx = Assert.Single(await ConvertAsync(xlsb, "docx"));
        Assert.True(new FileInfo(docx).Length > 0);
        Assert.True(RhwpConverter.UsesDirectBackend(xlsb));
    }

    [Fact]
    public async Task Xlsb_to_pdf_when_rhwp_is_available()
    {
        Assert.SkipUnless(fx.Converters.Any(c => c.Id == "rhwp" && fx.Catalog.StatusOf(c).IsAvailable), "rhwp not found");
        var pdf = Assert.Single(await ConvertAsync(SampleXlsb(fx.NewFolder()), "pdf"));

        Assert.True(new FileInfo(pdf).Length > 0);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            Assert.True(PDFtoImage.Conversion.GetPageCount(await File.ReadAllBytesAsync(pdf, TestContext.Current.CancellationToken)) > 0);
    }

    [Fact]
    public void Edges_include_xlsb_sources_and_no_engine_writes_xlsb()
    {
        var spreadsheet = fx.Converters.OfType<SpreadsheetConverter>().Single();
        foreach (var target in new[] { "xlsx", "ods", "csv", "tsv" })
            Assert.Contains(spreadsheet.Edges, e => e.From == "xlsb" && e.To == target);

        // No converter produces XLSB (only envelope openers such as P7M unwrap an existing XLSB payload).
        Assert.DoesNotContain(fx.Converters.Where(c => c is not IEnvelopeOpener).SelectMany(c => c.Edges), e => e.To == "xlsb");

        Assert.Equal(["spreadsheet"], BuiltInPlanner("rhwp").Plan("xlsb", "xlsx")!.Steps.Select(s => s.Converter.Id));
        Assert.Equal(["hwpx-writer"], BuiltInPlanner("rhwp").Plan("xlsb", "hwpx")!.Steps.Select(s => s.Converter.Id));
        Assert.Equal(["docx-writer"], BuiltInPlanner("rhwp").Plan("xlsb", "docx")!.Steps.Select(s => s.Converter.Id));
        Assert.Equal(["hwpx-writer", "rhwp"], BuiltInPlanner("rhwp").Plan("xlsb", "pdf")!.Steps.Select(s => s.Converter.Id));
        Assert.DoesNotContain(fx.Catalog.CreatePlanner(["libreoffice", "rhwp"]).Plan("xlsb", "pdf")!.Steps, s => s.Converter.Id == "libreoffice");
    }

    [Fact]
    public void Xlsx_renamed_to_xlsb_is_read_as_xlsx()
    {
        var dir = fx.NewFolder();
        var renamed = Path.Combine(dir, "misnamed.xlsb");
        File.Copy(OfficeTests.SalesWorkbook(dir), renamed);

        var sales = XlsbReader.Read(renamed).Sheets[0];

        Assert.Equal("1,350,000원", sales.Get(2, 1)!.Text);
        Assert.Equal("#1F4E79", sales.Get(1, 0)!.Style.Fill);
    }

    [Fact]
    public void Invalid_and_encrypted_xlsb_fail_with_clear_messages()
    {
        var dir = fx.NewFolder();

        var garbage = Path.Combine(dir, "garbage.xlsb");
        File.WriteAllBytes(garbage, [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]);
        Assert.Equal("The file is not an Excel binary workbook (XLSB).", Assert.Throws<InvalidDataException>(() => XlsbReader.Read(garbage)).Message);

        var plainZip = Path.Combine(dir, "not-a-workbook.xlsb");
        using (var zip = ZipFile.Open(plainZip, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("readme.txt").Open());
            writer.Write("hello");
        }
        Assert.Equal("The file is not an Excel binary workbook (XLSB).", Assert.Throws<InvalidDataException>(() => XlsbReader.Read(plainZip)).Message);

        var encrypted = Path.Combine(dir, "encrypted.xlsb");
        File.WriteAllBytes(encrypted, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0, 0, 0, 0, 0]);
        Assert.Equal(
            "The workbook is protected with a password (encrypted). Remove the password in Excel and convert it again.",
            Assert.Throws<InvalidDataException>(() => XlsbReader.Read(encrypted)).Message);
    }

    [Fact]
    public void Complex_and_unmeasured_scripts_are_found_in_xlsb()
    {
        var dir = fx.NewFolder();
        string Make(string name, string text)
        {
            var book = new XlsbBuilder();
            book.Sheet("Sheet1").Text(0, 0, text);
            return book.Save(Path.Combine(dir, name));
        }

        Assert.Equal("Arabic", ComplexScripts.FindInFile(Make("arabic.xlsb", "كشف حساب"), "xlsb"));
        Assert.Equal("Cyrillic", ComplexScripts.FindUnmeasuredInFile(Make("cyrillic.xlsb", "Отчёт за месяц"), "xlsb"));
        Assert.Null(ComplexScripts.FindInFile(Make("plain.xlsb", "Quarterly report, 한국어"), "xlsb"));
    }

    [Fact]
    public void Registry_mime_and_content_sniffing_recognize_xlsb()
    {
        var format = FormatRegistry.FindById("xlsb");
        Assert.NotNull(format);
        Assert.Equal(FormatCategory.Spreadsheet, format.Category);
        Assert.False(format.Writable);
        Assert.Equal(format, FormatRegistry.Detect("quarterly.xlsb"));

        Assert.Equal("xlsb", WebFormats.ExtensionForMime("application/vnd.ms-excel.sheet.binary.macroEnabled.12"));

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("[Content_Types].xml").Open()))
                w.Write("<Types/>");
            using (var w = zip.CreateEntry("xl/workbook.bin").Open())
                w.Write([0x83, 0x00, 0x84, 0x00]);
        }
        Assert.Equal("xlsb", P7mConverter.Sniff(ms.ToArray()));
    }
}
