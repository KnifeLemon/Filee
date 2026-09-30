// XLSX (and XLSM / XLTX) → CSV and CSV → XLSX without Excel or LibreOffice. XLSX / CSV → PDF, HWPX and HTML go through the HWPX
// writer (sheets become 한글 tables), see HwpxConverter and SheetDocument.

using Filee.Core.Conversion;
using Filee.Engines.Office.Sheets;

namespace Filee.Engines.Office;

/// <summary>Converts between spreadsheet formats in-process.</summary>
public sealed class SpreadsheetConverter : IConverter
{
    public string Id => "spreadsheet";
    public string DisplayName => "Spreadsheets (built-in)";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; } = [new("xlsx", "csv"), new("xlsm", "csv"), new("xltx", "csv"), new("csv", "xlsx")];

    public EngineStatus GetStatus() => EngineStatus.Available("XLSX ↔ CSV, XLSM / XLTX → CSV");

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            progress?.Report(0.1);
            var book = step.From == "csv" ? CsvFormat.Read(step.InputPath) : XlsxReader.Read(step.InputPath);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.6);

            var outputs = new List<string>();
            if (step.To == "xlsx")
            {
                if (step.Output.Allocate("xlsx") is { } path)
                {
                    XlsxWriter.Write(book.Sheets[0], path);
                    outputs.Add(path);
                }
            }
            else
            {
                // CSV holds one sheet: a workbook with several sheets becomes one file per sheet.
                var sheets = book.Sheets.Where(s => s.UsedRange() is not null).ToList();
                if (sheets.Count == 0 && book.Sheets.Count > 0)
                    sheets = [book.Sheets[0]];
                foreach (var sheet in sheets)
                {
                    var path = step.Output.Allocate("csv", sheets.Count > 1 ? "_" + SafeName(sheet.Name) : null);
                    if (path is null)
                        continue;
                    CsvFormat.Write(sheet, path);
                    outputs.Add(path);
                }
            }
            progress?.Report(1);
            return outputs;
        }, cancellationToken);

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string([.. name.Select(ch => invalid.Contains(ch) ? '_' : ch)]).Trim();
        return clean.Length == 0 ? "Sheet" : clean;
    }
}
