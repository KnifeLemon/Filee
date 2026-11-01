// XLSX (and XLSM / XLTX), XLS (and XLT) and ODS → PDF printed the way Excel prints them, without Excel or LibreOffice:
// the built-in readers (Office/Sheets) keep each sheet's page setup, and SheetPrinter lays the pages out with Excel's
// page logic and writes them with the PDF writer of the Filee renderer. CSV and TSV have no page setup and keep their
// route through the HWPX writer (each sheet fitted to the page width).

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

    private static readonly string[] Sources = ["xlsx", "xlsm", "xltx", "xls", "xlt", "ods"];

    public string Id => "sheet-print";
    public string DisplayName => "Spreadsheet printing (built-in)";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; } = [.. Sources.Select(from => new ConversionEdge(from, "pdf", Cost))];

    public EngineStatus GetStatus() => EngineStatus.Available("XLSX, XLS, ODS → PDF", EngineVersions.BuiltIn);

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
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
}
