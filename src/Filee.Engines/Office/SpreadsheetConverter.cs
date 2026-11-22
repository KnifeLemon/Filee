// Spreadsheets between XLSX, XLS, ODS, CSV and TSV without Excel or LibreOffice. XLS and its template XLT are written
// by XlsWriter (BIFF8); XLSB, the XLSM / XLTX variants and the OpenDocument template and flat file, OTS / FODS, are
// read, not written (OTS and FODS come from the ODS through OdfVariantConverter). The edges cost less than
// LibreOffice's (25), which is left the formats only it reads.
// XLSX / XLS / ODS → PDF are printed by SheetPrintConverter; CSV / TSV → PDF and every sheet → HWPX and HTML go
// through the HWPX writer (sheets become 한글 tables), see HwpxConverter and SheetDocument.

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office.Sheets;

namespace Filee.Engines.Office;

/// <summary>Converts between spreadsheet formats in-process.</summary>
public sealed class SpreadsheetConverter : IConverter
{
    private static readonly string[] Sources = ["xlsx", "xlsm", "xltx", "xlsb", "xls", "xlt", "ods", "ots", "fods", "csv", "tsv"];
    private static readonly string[] Targets = ["xlsx", "xls", "xlt", "ods", "csv", "tsv"];

    public string Id => "spreadsheet";
    public string DisplayName => "Spreadsheets (built-in)";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
        // XLSM / XLTX → XLSX is left to the ooxml engine, which keeps the whole package instead of rewriting the sheets,
        // and OTS / FODS → ODS to the OpenDocument forms engine, which keeps the whole document.
        [.. Sources.SelectMany(from => Targets.Where(to => to != from && !(to == "xlsx" && from is "xlsm" or "xltx") && !(to == "ods" && from is "ots" or "fods"))
            .Select(to => new ConversionEdge(from, to)))];

    public EngineStatus GetStatus() =>
        EngineStatus.Available("XLSX, XLSB, XLS, ODS, OTS, FODS, CSV, TSV → XLSX, XLS, XLT, ODS, CSV, TSV",
            $"{EngineVersions.BuiltIn} · {EngineVersions.Library("ExcelDataReader", typeof(ExcelDataReader.ExcelReaderFactory))}");

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            progress?.Report(0.1);
            var book = Read(step.InputPath, step.From);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0.6);

            var outputs = Write(book, step);
            progress?.Report(1);
            return outputs;
        }, cancellationToken);

    /// <summary>Writes a workbook as the step's XLSX, XLS, XLT, ODS, CSV or TSV (also the output of PdfTablesConverter and DocumentTablesConverter).</summary>
    internal static List<string> Write(Workbook book, ConversionStep step)
    {
        var outputs = new List<string>();
        switch (step.To)
        {
            case "xlsx" or "ods":
                if (step.Output.Allocate(step.To) is { } path)
                {
                    if (step.To == "xlsx")
                        XlsxWriter.Write(book, path);
                    else
                        OdsWriter.Write(book, path);
                    outputs.Add(path);
                }
                break;
            case "xls" or "xlt":
                // Excel 97–2003 holds 65,536 rows and 256 columns: the note says when the workbook didn't fit.
                if (step.Output.Allocate(step.To) is { } xls)
                {
                    if (XlsWriter.Write(book, xls, template: step.To == "xlt"))
                        step.Notice?.Invoke("notice.xls_limits");
                    outputs.Add(xls);
                }
                break;
            default:
                // CSV and TSV hold one sheet: a workbook with several sheets becomes one file per sheet.
                var sheets = book.Sheets.Where(s => s.UsedRange() is not null).ToList();
                if (sheets.Count == 0 && book.Sheets.Count > 0)
                    sheets = [book.Sheets[0]];
                foreach (var sheet in sheets)
                {
                    var file = step.Output.Allocate(step.To, sheets.Count > 1 ? "_" + SafeName(sheet.Name) : null);
                    if (file is null)
                        continue;
                    CsvFormat.Write(sheet, file, step.To == "tsv" ? CsvFormat.Tab : ',');
                    outputs.Add(file);
                }
                break;
        }
        return outputs;
    }

    /// <summary>Reads a spreadsheet of a format id this engine accepts.</summary>
    internal static Workbook Read(string path, string format) => format switch
    {
        "xls" or "xlt" => XlsReader.Read(path),
        XlsbReader.Format => XlsbReader.Read(path),
        // An OpenDocument template is the package with another media type; a flat file is read as the package it splits into.
        "ods" or "ots" => OdsReader.Read(path),
        "fods" => Odf.OdfVariants.Read(path, "fods", Path.GetTempPath(), OdsReader.Read),
        "csv" => CsvFormat.Read(path),
        "tsv" => CsvFormat.ReadTsv(path),
        _ => XlsxReader.Read(path),
    };

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string([.. name.Select(ch => invalid.Contains(ch) ? '_' : ch)]).Trim();
        return clean.Length == 0 ? "Sheet" : clean;
    }
}
