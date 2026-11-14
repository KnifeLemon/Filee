// XLSX (and XLSM / XLTX), XLS (and XLT) and ODS → PDF printed the way Excel prints them, without Excel or LibreOffice:
// the built-in readers (Office/Sheets) keep each sheet's page setup, and SheetPrinter lays the pages out with Excel's
// page logic and writes them with the PDF writer of the Filee renderer. CSV and TSV have no page setup and keep their
// route through the HWPX writer (each sheet fitted to the page width).
//
// What the printer doesn't draw yet (charts, text in shapes, SmartArt, form controls, the drawings of XLS and ODS
// files: SheetCapabilities) is looked for first. A workbook with any of it is handed to LibreOffice when it is
// installed, inside this step: a planned route without this engine (ConversionEdge.Checked) would take the HWPX writer
// and rhwp instead, which cost less than LibreOffice's fallback edge and keep less than this printer. Without
// LibreOffice the workbook is printed all the same, with a notice that something was left out.

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office.Sheets;

namespace Filee.Engines.Office;

/// <summary>Prints spreadsheets to PDF in-process.</summary>
public sealed class SheetPrintConverter : IConverter
{
    /// <summary>
    /// With the most an engine left out of the priority list adds (9): 21, below the HWPX writer and rhwp (about 31)
    /// and LibreOffice's fallback (25 + 9), and above LibreOffice when Settings → Engines chooses it for spreadsheets
    /// (10 + 9), so that choice still holds.
    /// </summary>
    internal const int Cost = 12;

    /// <summary>Shown with a workbook printed without what <see cref="SheetCapabilities"/> named.</summary>
    internal const string LeftOutNotice = "notice.sheet_objects_left_out";

    private static readonly string[] Sources = ["xlsx", "xlsm", "xltx", "xls", "xlt", "ods"];

    public string Id => "sheet-print";
    public string DisplayName => "Spreadsheet printing (built-in)";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; } = [.. Sources.Select(from => new ConversionEdge(from, "pdf", Cost))];

    /// <summary>
    /// Hands a workbook with something this engine doesn't draw to the engine that draws it (LibreOffice:
    /// <see cref="WithFallback"/>); returns null while that engine isn't installed. Null prints everything here.
    /// </summary>
    internal Func<ConversionStep, IProgress<double>?, CancellationToken, Task<IReadOnlyList<string>>?>? Fallback { get; set; }

    public EngineStatus GetStatus() => EngineStatus.Available("XLSX, XLS, ODS → PDF", EngineVersions.BuiltIn);

    /// <summary>Gives the sheet printer among <paramref name="converters"/> LibreOffice, when it is there, for what it doesn't draw.</summary>
    internal static IReadOnlyList<IConverter> WithFallback(IReadOnlyList<IConverter> converters)
    {
        if (converters.OfType<LibreOfficeConverter>().FirstOrDefault() is { } libreOffice)
        {
            foreach (var printer in converters.OfType<SheetPrintConverter>())
                printer.Fallback = (step, progress, cancellationToken) =>
                    libreOffice.Found ? libreOffice.ConvertAsync(step, progress, cancellationToken) : null;
        }
        return converters;
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var leftOut = await Task.Run(() => SheetCapabilities.FirstUnsupported(step.InputPath, step.From), cancellationToken);
        if (leftOut is not null && Fallback?.Invoke(step, progress, cancellationToken) is { } handedOver)
            return await handedOver;
        var outputs = await Task.Run<IReadOnlyList<string>>(() =>
        {
            if (DocumentFonts.Missing(step.OriginalSourcePath ?? step.InputPath) is [_, ..] missing)
                step.MissingFonts?.Invoke(missing);
            progress?.Report(0.05);
            var book = SpreadsheetConverter.Read(step.InputPath, step.From);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.2);
            var pages = progress is null ? null : new Progress<double>(p => progress.Report(0.2 + 0.8 * p));
            var pdf = SheetPrinter.ToPdf(book, SheetPrintSource.Of(step.OriginalSourcePath ?? step.InputPath), cancellationToken, pages);
            var target = step.Output.Allocate("pdf");
            if (target is null)
                return [];
            File.WriteAllBytes(target, pdf);
            progress?.Report(1);
            return [target];
        }, cancellationToken);
        if (leftOut is not null && outputs.Count > 0)
            step.Notice?.Invoke(LeftOutNotice);
        return outputs;
    }
}
