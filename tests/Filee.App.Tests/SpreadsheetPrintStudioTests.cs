using System.IO.Compression;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office;
using Filee.Engines.Office.Sheets;
using Filee.Engines.Pdf;

namespace Filee.App.Tests;

public class SpreadsheetPrintStudioTests
{
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("pt-PT")]
    [InlineData("pt-BR")]
    public async Task Translated_print_settings_fit_at_the_minimum_window_width(string language)
    {
        TestServices.EnsureInitialized(language);
        var folder = Directory.CreateTempSubdirectory("filee-studio-layout-").FullName;
        SpreadsheetPrintStudioWindow? window = null;
        SpreadsheetPrintStudioViewModel? vm = null;
        try
        {
            var source = Path.Combine(folder, "sample.xlsx");
            WriteWorkbook(source);
            vm = new SpreadsheetPrintStudioViewModel(source, AppHost.Get<SpreadsheetPrintService>(),
                AppHost.Get<ILocalizer>(), AppHost.Get<UserDataStore>());
            await vm.Initialization;
            Assert.Single(vm.Sheets);
            vm.SelectedSheet!.Order = vm.PageOrders.Last().Value;
            window = new SpreadsheetPrintStudioWindow { DataContext = vm, Width = 1040, Height = 860 };
            window.Show();
            RenderTests.Pump();
            if (Environment.GetEnvironmentVariable("FILEE_LOCALIZATION_SCREENSHOTS") == "1")
                RenderTests.Save(window, $"spreadsheet-studio-{language}-1040.png");
            var labels = window.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(text.Text));
            Assert.NotEmpty(labels);
            var clipped = labels.Where(text => text.TextLayout.Width > text.Bounds.Width + 1)
                .Select(text => $"'{text.Text}' ({text.TextLayout.Width} > {text.Bounds.Width})").ToArray();
            Assert.True(clipped.Length == 0, $"{language}: clipped text: {string.Join("; ", clipped)}");
        }
        finally
        {
            // The window owns its view model and disposes it when closed.
            if (window is not null) window.Close();
            else vm?.Dispose();
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public void Multiple_workbooks_keep_independent_studio_windows()
    {
        TestServices.EnsureInitialized();
        var folder = Directory.CreateTempSubdirectory("filee-studios-test-").FullName;
        var windows = AppHost.Get<WindowService>();
        var before = windows.SpreadsheetStudios.ToArray();
        try
        {
            var first = Path.Combine(folder, "first.xlsx");
            var second = Path.Combine(folder, "second.xlsx");
            WriteWorkbook(first);
            WriteWorkbook(second);

            windows.ShowSpreadsheetPrintStudio(first);
            windows.ShowSpreadsheetPrintStudio(second);

            var opened = windows.SpreadsheetStudios.Except(before).ToArray();
            Assert.Equal(2, opened.Length);
            Assert.All(opened, window => Assert.True(window.IsVisible));
            Assert.Equal(["first.xlsx", "second.xlsx"], opened.Select(window =>
                ((SpreadsheetPrintStudioViewModel)window.DataContext!).FileName).Order());
            opened[0].Close();
            Assert.True(opened[1].IsVisible);
            Assert.DoesNotContain(opened[0], windows.SpreadsheetStudios);
        }
        finally
        {
            foreach (var window in windows.SpreadsheetStudios.Except(before).ToArray())
                window.Close();
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task Closing_during_export_cancels_the_printer_and_removes_temporary_files()
    {
        TestServices.EnsureInitialized();
        var service = AppHost.Get<SpreadsheetPrintService>();
        Assert.SkipUnless(service.Status.IsAvailable, "No spreadsheet printer");
        var folder = Directory.CreateTempSubdirectory("filee-sheet-studio-test-").FullName;
        try
        {
            var source = Path.Combine(folder, "large.xlsx");
            WriteWorkbook(source);
            var output = Path.Combine(folder, "cancelled.pdf");
            var before = WorkFolders();
            var vm = new SpreadsheetPrintStudioViewModel(source, service, AppHost.Get<ILocalizer>(),
                AppHost.Get<UserDataStore>());
            await vm.Initialization;

            var export = vm.ExportAsync(output);
            await WaitUntilAsync(() => WorkFolders().Except(before).Any(), TimeSpan.FromSeconds(10));
            vm.Dispose();
            await export;
            await WaitUntilAsync(() => !WorkFolders().Except(before).Any(), TimeSpan.FromSeconds(10));

            Assert.False(File.Exists(output + ".filee-tmp"));
            Assert.Empty(WorkFolders().Except(before));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void One_pdf_per_sheet_names_follow_the_chosen_file_and_never_collide()
    {
        var folder = Path.Combine(Path.GetTempPath(), "exports");
        var paths = SpreadsheetPrintService.PerSheetPaths(Path.Combine(folder, "Report.pdf"),
            ["Sales", "Q1/Q2", "Sales", "q1:q2", "  ", "DATA", "data"], exists: _ => false);

        Assert.Equal(
        [
            "Report_Sales.pdf", "Report_Q1_Q2.pdf", "Report_Sales_2.pdf", "Report_q1_q2_2.pdf", "Report_Sheet.pdf",
            "Report_DATA.pdf", "Report_data_2.pdf",
        ], paths.Select(Path.GetFileName));
        Assert.All(paths, path => Assert.Equal(folder, Path.GetDirectoryName(path)));
    }

    [Theory]
    [InlineData(ConflictPolicy.Rename, "Report_Sales (3).pdf|Report_Sales_2 (2).pdf|Report_Data.pdf")]
    [InlineData(ConflictPolicy.Overwrite, "Report_Sales.pdf|Report_Sales_2.pdf|Report_Data.pdf")]
    [InlineData(ConflictPolicy.Skip, "-|-|Report_Data.pdf")]
    public void One_pdf_per_sheet_follows_the_conflict_policy_for_files_already_there(ConflictPolicy conflict,
        string expected)
    {
        var folder = Path.Combine(Path.GetTempPath(), "exports");
        // An earlier export left Report_Sales.pdf, Report_Sales (2).pdf and Report_Sales_2.pdf behind.
        var existing = new HashSet<string>(["Report_Sales.pdf", "Report_Sales (2).pdf", "Report_Sales_2.pdf"],
            StringComparer.OrdinalIgnoreCase);

        var paths = SpreadsheetPrintService.PerSheetPaths(Path.Combine(folder, "Report.pdf"),
            ["Sales", "Sales", "Data"], conflict, path => existing.Contains(Path.GetFileName(path)));

        Assert.Equal(expected.Split('|'), paths.Select(path => path is null ? "-" : Path.GetFileName(path)));
    }

    [AvaloniaFact]
    public async Task One_pdf_per_sheet_writes_a_file_for_each_region_and_records_them_all()
    {
        TestServices.EnsureInitialized();
        var service = AppHost.Get<SpreadsheetPrintService>();
        Assert.SkipUnless(service.Status.IsAvailable, "No spreadsheet printer");
        var folder = Directory.CreateTempSubdirectory("filee-sheet-studio-split-").FullName;
        var store = AppHost.Get<UserDataStore>();
        try
        {
            var source = Path.Combine(folder, "book.xlsx");
            WriteWorkbook(source);
            using var vm = new SpreadsheetPrintStudioViewModel(source, service, AppHost.Get<ILocalizer>(), store);
            await vm.Initialization;
            vm.AddRegionCommand.Execute(null);
            vm.OnePdfPerSheet = true;
            // An earlier export must survive: the tool's default policy numbers the new file instead.
            var earlier = Path.Combine(folder, "book_Data.pdf");
            File.WriteAllText(earlier, "earlier export");

            await vm.ExportAsync(Path.Combine(folder, "book.pdf"));

            string[] expected = [Path.Combine(folder, "book_Data (2).pdf"), Path.Combine(folder, "book_Data_2.pdf")];
            Assert.All(expected, path => Assert.True(File.Exists(path), path));
            Assert.Equal("earlier export", File.ReadAllText(earlier));
            Assert.False(File.Exists(Path.Combine(folder, "book.pdf")));
            Assert.Empty(Directory.GetFiles(folder, "*.filee-tmp"));
            Assert.Contains(store.History, entry => entry.Outputs.SequenceEqual(expected));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData(true, false, false, SpreadsheetPrintEngine.BuiltIn)]
    [InlineData(true, true, false, SpreadsheetPrintEngine.BuiltIn)]
    [InlineData(true, true, true, SpreadsheetPrintEngine.LibreOffice)]
    [InlineData(true, false, true, SpreadsheetPrintEngine.BuiltIn)]
    [InlineData(false, true, false, SpreadsheetPrintEngine.LibreOffice)]
    [InlineData(false, false, true, null)]
    public void The_studio_prints_with_the_built_in_printer_unless_LibreOffice_is_chosen_and_installed(bool builtIn,
        bool libreOffice, bool preferLibreOffice, SpreadsheetPrintEngine? expected)
    {
        Assert.Equal(expected, SpreadsheetPrintService.Choose(builtIn, libreOffice, preferLibreOffice));
    }

    [AvaloniaFact]
    public async Task Without_LibreOffice_the_preview_and_export_use_the_built_in_printer()
    {
        var folder = Directory.CreateTempSubdirectory("filee-sheet-studio-builtin-").FullName;
        try
        {
            // LibreOffice is looked for in an empty engine folder: not installed.
            var libreOffice = new LibreOfficeConverter(new EngineEnvironment(Path.Combine(folder, "data")));
            var service = new SpreadsheetPrintService([libreOffice, new SheetPrintConverter(), new PdfSharpConverter()]);
            Assert.Equal(SpreadsheetPrintEngine.BuiltIn, service.Engine);
            Assert.True(service.Status.IsAvailable);
            var source = Path.Combine(folder, "book.xlsx");
            WriteWorkbook(source, rows: 200);
            var workbook = service.Inspect(source);
            var sheet = Assert.Single(workbook.Sheets);
            var fitted = SpreadsheetPrintRegion.FromSource(sheet) with
            {
                Paper = SpreadsheetPaper.A4,
                Orientation = SpreadsheetPageOrientation.Landscape,
                ScaleMode = SpreadsheetScaleMode.FitToPages,
                FitWidth = 1,
                FitHeight = 1,
            };
            var portion = SpreadsheetPrintRegion.FromSource(sheet) with { Range = "A1:A100", WritePrintArea = true };

            var rendered = await service.RenderAsync(workbook, fitted, TestContext.Current.CancellationToken);
            try
            {
                Assert.Equal(1, rendered.Preview.PageCount);
                var page = rendered.Preview.RenderPage(1, 200, TestContext.Current.CancellationToken);
                Assert.True(page.Width > page.Height); // landscape
                Assert.Empty(rendered.Notices);
            }
            finally
            {
                SpreadsheetPrintService.Release(rendered);
            }
            Assert.False(Directory.Exists(rendered.TemporaryDirectory));

            var whole = await service.RenderAsync(workbook, portion, TestContext.Current.CancellationToken);
            var portionPages = whole.Preview.PageCount;
            SpreadsheetPrintService.Release(whole);
            Assert.True(portionPages > 1);

            var output = Path.Combine(folder, "book.pdf");
            var outputs = await service.ExportAsync(workbook, [fitted, portion], output,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal([output], outputs);
            Assert.Equal(1 + portionPages, PdfPreviewDocument.Open(output).PageCount);
            Assert.False(File.Exists(output + ".filee-tmp"));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task Without_any_printer_the_studio_says_it_cannot_render()
    {
        var folder = Directory.CreateTempSubdirectory("filee-sheet-studio-none-").FullName;
        try
        {
            var service = new SpreadsheetPrintService([new PdfSharpConverter()]);
            Assert.Null(service.Engine);
            Assert.False(service.Status.IsAvailable);
            var source = Path.Combine(folder, "book.xlsx");
            WriteWorkbook(source, rows: 10);
            var workbook = service.Inspect(source);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RenderAsync(workbook,
                SpreadsheetPrintRegion.FromSource(workbook.Sheets[0]), TestContext.Current.CancellationToken));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task What_the_built_in_printer_leaves_out_is_shown_in_the_studio()
    {
        TestServices.EnsureInitialized();
        var folder = Directory.CreateTempSubdirectory("filee-sheet-studio-notice-").FullName;
        try
        {
            var service = new SpreadsheetPrintService([new SheetPrintConverter(), new PdfSharpConverter()]);
            var source = Path.Combine(folder, "controls.xlsx");
            WriteWorkbook(source, rows: 10, formControl: true);
            var loc = AppHost.Get<ILocalizer>();
            using var vm = new SpreadsheetPrintStudioViewModel(source, service, loc, AppHost.Get<UserDataStore>());
            await vm.Initialization;

            await WaitUntilAsync(() => vm.Pages.Count > 0, TimeSpan.FromSeconds(20));

            Assert.True(vm.HasNotice);
            Assert.Equal(loc["notice.sheet_objects_left_out"], vm.Notice);
            Assert.Equal(loc.Format("sheet_studio.page_count", vm.Pages.Count), vm.Status);

            await vm.ExportAsync(Path.Combine(folder, "controls.pdf"));

            Assert.True(File.Exists(Path.Combine(folder, "controls.pdf")));
            Assert.Equal(loc["notice.sheet_objects_left_out"], vm.Notice);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task A_cancelled_export_leaves_no_file_and_no_work_folder()
    {
        var folder = Directory.CreateTempSubdirectory("filee-sheet-studio-cancel-").FullName;
        try
        {
            var service = new SpreadsheetPrintService([new SheetPrintConverter(), new PdfSharpConverter()]);
            var source = Path.Combine(folder, "book.xlsx");
            WriteWorkbook(source, rows: 10);
            var workbook = service.Inspect(source);
            var before = WorkFolders();
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExportAsync(workbook,
                [SpreadsheetPrintRegion.FromSource(workbook.Sheets[0])], Path.Combine(folder, "book.pdf"),
                cancellationToken: cancelled.Token));

            Assert.False(File.Exists(Path.Combine(folder, "book.pdf")));
            Assert.False(File.Exists(Path.Combine(folder, "book.pdf.filee-tmp")));
            // Work folders of other tests may come and go meanwhile; none of this export's copies stays.
            Assert.DoesNotContain(WorkFolders().Except(before), work => File.Exists(Path.Combine(work, "sheet.xlsx")));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public async Task The_page_strip_renders_only_the_thumbnails_it_shows()
    {
        TestServices.EnsureInitialized();
        var folder = Directory.CreateTempSubdirectory("filee-sheet-studio-strip-").FullName;
        SpreadsheetPrintStudioWindow? window = null;
        try
        {
            var service = new SpreadsheetPrintService([new SheetPrintConverter(), new PdfSharpConverter()]);
            var source = Path.Combine(folder, "long.xlsx");
            WriteWorkbook(source);
            var vm = new SpreadsheetPrintStudioViewModel(source, service, AppHost.Get<ILocalizer>(),
                AppHost.Get<UserDataStore>());
            window = new SpreadsheetPrintStudioWindow { DataContext = vm, Width = 1380, Height = 860 };
            window.Show();
            await vm.Initialization;

            // The strip shows about seven pages; they (and none far along) get their thumbnail.
            var shown = 0;
            await WaitUntilAsync(() =>
            {
                RenderTests.Pump();
                shown = window.GetVisualDescendants().OfType<ListBox>().Last().GetRealizedContainers().Count();
                return vm.Pages.Count > 0 && vm.Pages.Take(shown).All(page => page.Thumbnail is not null);
            }, TimeSpan.FromSeconds(30));

            Assert.True(vm.Pages.Count > 40, $"{vm.Pages.Count} pages");
            Assert.InRange(shown, 1, 20);
            Assert.Null(vm.Pages[^1].Thumbnail);
            Assert.True(vm.Pages.Count(page => page.Thumbnail is not null) < vm.Pages.Count / 2);
            // The caption under the preview names the engine that drew it.
            Assert.Equal(AppHost.Get<ILocalizer>()["sheet_studio.drawn_by_builtin"], vm.PreviewEngine);
            Assert.True(vm.HasPreviewEngine);
            if (Environment.GetEnvironmentVariable("FILEE_LOCALIZATION_SCREENSHOTS") == "1")
                RenderTests.Save(window, "spreadsheet-studio-built-in.png");

            // Scrolled to the end, the strip lets go of the first pages' thumbnails and renders the last ones (the
            // selected first page keeps its item, and so its thumbnail).
            var strip = window.GetVisualDescendants().OfType<ListBox>().Last();
            var first = vm.Pages[1];
            strip.ScrollIntoView(vm.Pages.Count - 1);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!(vm.Pages[^1].Thumbnail is not null && first.Thumbnail is null) && DateTime.UtcNow < deadline)
            {
                RenderTests.Pump();
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
            var realized = strip.GetRealizedContainers().Select(strip.IndexFromContainer).ToList();
            Assert.True(vm.Pages[^1].Thumbnail is not null && first.Thumbnail is null,
                $"last {vm.Pages[^1].Thumbnail is not null}, first {first.Thumbnail is not null}, shown [{string.Join(",", realized)}], with thumbnails [{string.Join(",", vm.Pages.Where(p => p.Thumbnail is not null).Select(p => p.Number))}]");
            Assert.True(vm.Pages.Count(page => page.Thumbnail is not null) <= 2 * shown + 4);
        }
        finally
        {
            window?.Close();
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    private static HashSet<string> WorkFolders() => Directory.Exists(JobQueue.DefaultWorkRoot)
        ? Directory.GetDirectories(JobQueue.DefaultWorkRoot).ToHashSet(StringComparer.Ordinal)
        : [];

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        Assert.True(condition());
    }

    /// <param name="formControl">The sheet has a form control (a part the built-in printer doesn't draw yet).</param>
    private static void WriteWorkbook(string path, int rows = 4000, bool formControl = false)
    {
        var data = new StringBuilder();
        for (var row = 1; row <= rows; row++)
            data.Append($"<row r=\"{row}\"><c r=\"A{row}\"><v>{row}</v></c></row>");
        var parts = new Dictionary<string, string>
        {
            ["_rels/.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>",
            ["xl/workbook.xml"] = "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><bookViews><workbookView activeTab=\"0\"/></bookViews><sheets><sheet name=\"Data\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>",
            ["xl/worksheets/sheet1.xml"] = $"<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetViews><sheetView workbookViewId=\"0\" tabSelected=\"1\"/></sheetViews><sheetData>{data}</sheetData></worksheet>",
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>",
        };
        if (formControl)
        {
            parts["xl/worksheets/_rels/sheet1.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/ctrlProp\" Target=\"../ctrlProps/ctrlProp1.xml\"/></Relationships>";
            parts["xl/ctrlProps/ctrlProp1.xml"] = "<formControlPr xmlns=\"http://schemas.microsoft.com/office/spreadsheetml/2009/9/main\" objectType=\"CheckBox\"/>";
        }
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, xml) in parts)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + xml);
        }
    }
}
