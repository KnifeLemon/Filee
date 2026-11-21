// The spreadsheet print studio's conversion boundary. Every preview and export is made from a disposable copy of
// the workbook, so changing a print area, page size or hidden-row option can never touch the user's original file.
//
// The copy is printed by the same engine as every other spreadsheet → PDF: Filee's own sheet printer
// (SheetPrintConverter), or LibreOffice when Settings → Engines → "Use LibreOffice for spreadsheets → PDF" is on and
// LibreOffice is installed. The printer reads every setting the studio writes into the copy (paper, orientation,
// margins, scale or fit to pages, page order, print area, hidden rows and columns, manual breaks, the one sheet left
// visible) and keeps the sheet's own gridlines, headings, centring and headers / footers. A workbook with what the
// printer doesn't draw yet (charts, text in shapes, form controls: SheetCapabilities) goes to LibreOffice when it is
// installed, as in a normal conversion; without it the pages are printed all the same and the region carries the
// printer's notice, which the studio shows.
//
// An Excel binary workbook (.xlsb) can't be rewritten that way: Filee's own sheet printer prints it from what it reads,
// with the region's settings put on the sheet (SpreadsheetPrintBinary), whichever engine is chosen.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office;
using Filee.Engines.Office.Sheets;
using Filee.Engines.Pdf;

namespace Filee.App.Services;

/// <param name="Notices">Localization keys of what the printer said about the region (something left out).</param>
public sealed record SpreadsheetRenderedRegion(string PdfPath, PdfPreviewDocument Preview, string TemporaryDirectory,
    IReadOnlyList<string> Notices);

/// <summary>Which engine prints the studio's pages.</summary>
public enum SpreadsheetPrintEngine
{
    /// <summary>Filee's own sheet printer (SheetPrintConverter): the default.</summary>
    BuiltIn,

    /// <summary>LibreOffice Calc, when chosen for spreadsheets in Settings → Engines and installed.</summary>
    LibreOffice,
}

public sealed class SpreadsheetPrintService(IReadOnlyList<IConverter> converters)
{
    private readonly SheetPrintConverter? _sheetPrinter = converters.OfType<SheetPrintConverter>().FirstOrDefault();
    private readonly LibreOfficeConverter? _libreOffice = converters.OfType<LibreOfficeConverter>().FirstOrDefault();
    private readonly IPdfMerger? _pdfMerger = converters.OfType<IPdfMerger>().FirstOrDefault();

    public SpreadsheetWorkbookInfo Inspect(string path) => SpreadsheetPrintInspector.Read(path);

    /// <summary>The engine the next preview or export uses; null when neither is there.</summary>
    public SpreadsheetPrintEngine? Engine
    {
        get
        {
            var prefer = EngineEnvironment.LibreOfficeForSheets;
            // LibreOffice is only looked for when it could be the one.
            var libreOffice = (prefer || _sheetPrinter is null) && _libreOffice?.GetStatus().IsAvailable == true;
            return Choose(_sheetPrinter is not null, libreOffice, prefer);
        }
    }

    /// <summary>
    /// The engine that prints <paramref name="workbook"/>'s pages: the built-in printer for an Excel binary workbook
    /// (SpreadsheetPrintBinary, whichever engine is chosen), else <see cref="Engine"/>.
    /// </summary>
    public SpreadsheetPrintEngine? EngineFor(SpreadsheetWorkbookInfo workbook) =>
        SpreadsheetPrintBinary.Handles(workbook) ? SpreadsheetPrintEngine.BuiltIn : Engine;

    /// <summary>
    /// The sheet printer, like the spreadsheet → PDF route: LibreOffice only when the user chose it for spreadsheets
    /// (<paramref name="preferLibreOffice"/>) and it is installed, or when there is no sheet printer at all.
    /// </summary>
    internal static SpreadsheetPrintEngine? Choose(bool builtIn, bool libreOffice, bool preferLibreOffice) =>
        libreOffice && (preferLibreOffice || !builtIn) ? SpreadsheetPrintEngine.LibreOffice
        : builtIn ? SpreadsheetPrintEngine.BuiltIn
        : null;

    public EngineStatus Status => Engine switch
    {
        SpreadsheetPrintEngine.BuiltIn => _sheetPrinter!.GetStatus(),
        SpreadsheetPrintEngine.LibreOffice => _libreOffice!.GetStatus(),
        _ => EngineStatus.Unavailable("engine.reason.not_installed"),
    };

    public async Task<SpreadsheetRenderedRegion> RenderAsync(SpreadsheetWorkbookInfo workbook,
        SpreadsheetPrintRegion region, CancellationToken cancellationToken = default)
    {
        var (output, folder, notices) = await MakePdfAsync(workbook, region, cancellationToken);
        try
        {
            var preview = await Task.Run(() => PdfPreviewDocument.Open(output), cancellationToken);
            return new SpreadsheetRenderedRegion(output, preview, folder, notices);
        }
        catch
        {
            TryDelete(folder);
            throw;
        }
    }

    /// <summary>
    /// Renders every region and writes them to <paramref name="outputPath"/> as one merged PDF, or, with
    /// <paramref name="onePerSheet"/>, as one PDF per region named after <paramref name="outputPath"/> and the sheet
    /// (see <see cref="PerSheetPaths"/>), where <paramref name="conflict"/> decides about files already there. Returns
    /// the files written; empty when the policy skipped every sheet. <paramref name="notice"/> hears each notice key
    /// the printer gave (once per key).
    /// </summary>
    public async Task<IReadOnlyList<string>> ExportAsync(SpreadsheetWorkbookInfo workbook,
        IReadOnlyList<SpreadsheetPrintRegion> regions, string outputPath, bool onePerSheet = false,
        ConflictPolicy conflict = ConflictPolicy.Rename, IProgress<double>? progress = null,
        Action<string>? notice = null, CancellationToken cancellationToken = default)
    {
        if (regions.Count == 0)
            throw new ArgumentException("At least one region must be exported.", nameof(regions));
        if (!onePerSheet && _pdfMerger is null)
            throw new InvalidOperationException("The PDF merger is not available.");
        // The merged file is the one the save dialog showed and the user confirmed, so it may replace a file there.
        // The per-sheet names were never shown, so files already there follow the tool's conflict policy.
        var parts = regions;
        IReadOnlyList<string> targets = [outputPath];
        if (onePerSheet)
        {
            var paths = PerSheetPaths(outputPath, regions.Select(region => region.Sheet.Name).ToArray(), conflict);
            parts = regions.Where((_, index) => paths[index] is not null).ToArray();
            targets = paths.OfType<string>().ToArray();
            if (targets.Count == 0)
                return [];
        }
        var overwrite = !onePerSheet || conflict == ConflictPolicy.Overwrite;
        var rendered = new List<(string PdfPath, string TemporaryDirectory, IReadOnlyList<string> Notices)>();
        var temporaryOutputs = targets.Select(target => target + ".filee-tmp").ToArray();
        try
        {
            for (var index = 0; index < parts.Count; index++)
            {
                var done = index;
                var part = progress is null ? null
                    : new SubProgress(value => progress.Report(0.85 * (done + Math.Clamp(value, 0, 1)) / parts.Count));
                rendered.Add(await MakePdfAsync(workbook, parts[index], cancellationToken, part));
                progress?.Report(0.85 * (index + 1) / parts.Count);
            }
            foreach (var temporary in temporaryOutputs)
                TryDelete(temporary);
            if (onePerSheet)
            {
                // Nothing reaches the chosen folder until every sheet has rendered, so a failure or a cancel in the
                // middle leaves no half set of files behind.
                for (var index = 0; index < rendered.Count; index++)
                    File.Copy(rendered[index].PdfPath, temporaryOutputs[index], overwrite: true);
            }
            else
            {
                await _pdfMerger!.MergeAsync(rendered.Select(item => new PdfMergeInput(item.PdfPath, Path.GetFileName(item.PdfPath))).ToArray(),
                    temporaryOutputs[0], sourceBookmarks: false, paper: null, cancellationToken);
            }
            // Without Overwrite, a file that appeared there since the names were chosen is never replaced.
            for (var index = 0; index < targets.Count; index++)
                File.Move(temporaryOutputs[index], targets[index], overwrite);
            foreach (var key in rendered.SelectMany(item => item.Notices).Distinct())
                notice?.Invoke(key);
            progress?.Report(1);
            return targets;
        }
        finally
        {
            foreach (var temporary in temporaryOutputs)
                TryDelete(temporary);
            foreach (var item in rendered)
                TryDelete(item.TemporaryDirectory);
        }
    }

    /// <summary>
    /// The files for a one-PDF-per-sheet export: <c>Report.pdf</c> with sheets "Sales" and "Q1/Q2" gives
    /// <c>Report_Sales.pdf</c> and <c>Report_Q1_Q2.pdf</c> in the same folder. A sheet listed twice (an added region)
    /// or two names that clean up to the same file get <c>_2</c>, <c>_3</c> so no export overwrites another. A file
    /// already on disk is handled like any other Filee output: <c>Report_Sales (2).pdf</c> with
    /// <see cref="ConflictPolicy.Rename"/>, replaced with <see cref="ConflictPolicy.Overwrite"/>, and <c>null</c> (that
    /// sheet is left out) with <see cref="ConflictPolicy.Skip"/>.
    /// </summary>
    public static IReadOnlyList<string?> PerSheetPaths(string outputPath, IReadOnlyList<string> sheetNames,
        ConflictPolicy conflict = ConflictPolicy.Rename, Func<string, bool>? exists = null)
    {
        exists ??= path => File.Exists(path) || Directory.Exists(path);
        var folder = Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? "";
        var baseName = OutputPathResolver.Sanitize(Path.GetFileNameWithoutExtension(outputPath), "export");
        string PathFor(string name) => Path.Combine(folder, name + ".pdf");
        // File names on Windows and macOS ignore case, so "Data" and "DATA" would land on the same file.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new List<string?>(sheetNames.Count);
        foreach (var sheetName in sheetNames)
        {
            var stem = $"{baseName}_{OutputPathResolver.Sanitize(sheetName, "Sheet")}";
            var name = stem;
            for (var number = 2; used.Contains(name); number++)
                name = $"{stem}_{number}";
            // Reserved even when the file is renamed or skipped, so a repeat of this sheet still becomes _2.
            used.Add(name);
            if (exists(PathFor(name)) && conflict != ConflictPolicy.Overwrite)
            {
                if (conflict == ConflictPolicy.Skip)
                {
                    paths.Add(null);
                    continue;
                }
                var renamed = name;
                for (var number = 2; used.Contains(renamed) || exists(PathFor(renamed)); number++)
                    renamed = $"{name} ({number})";
                used.Add(renamed);
                name = renamed;
            }
            paths.Add(PathFor(name));
        }
        return paths;
    }

    private async Task<(string PdfPath, string TemporaryDirectory, IReadOnlyList<string> Notices)> MakePdfAsync(
        SpreadsheetWorkbookInfo workbook, SpreadsheetPrintRegion region, CancellationToken cancellationToken,
        IProgress<double>? progress = null)
    {
        if (SpreadsheetPrintBinary.Handles(workbook))
        {
            var work = CreateWorkFolder();
            try
            {
                var pdf = Path.Combine(work, "sheet.pdf");
                var said = await Task.Run(() => SpreadsheetPrintBinary.Print(workbook, region, pdf, progress, cancellationToken), cancellationToken);
                return (pdf, work, said);
            }
            catch
            {
                TryDelete(work);
                throw;
            }
        }
        IConverter engine = Engine switch
        {
            SpreadsheetPrintEngine.BuiltIn => _sheetPrinter!,
            SpreadsheetPrintEngine.LibreOffice => _libreOffice!,
            _ => throw new InvalidOperationException("No spreadsheet printer is available."),
        };
        var folder = CreateWorkFolder();
        try
        {
            var input = Path.Combine(folder, "sheet.xlsx");
            var output = Path.Combine(folder, "sheet.pdf");
            await Task.Run(() => SpreadsheetPrintPackage.Create(workbook.Path, input, workbook, region), cancellationToken);
            var notices = new List<string>();
            // The original's path names the workbook in the &F / &Z header fields and finds the fonts it lacks.
            var results = await engine.ConvertAsync(new ConversionStep(
                input, "xlsx", "pdf", new Preset { TargetFormat = "pdf" },
                new ExactOutput(output), folder, Notice: key => { lock (notices) notices.Add(key); },
                OriginalSourcePath: workbook.Path), progress, cancellationToken);
            if (results.Count != 1 || !File.Exists(output))
                throw new InvalidOperationException($"{engine.DisplayName} did not create the requested PDF.");
            return (output, folder, notices);
        }
        catch
        {
            TryDelete(folder);
            throw;
        }
    }

    public static void Release(SpreadsheetRenderedRegion? rendered)
    {
        if (rendered is not null)
            TryDelete(rendered.TemporaryDirectory);
    }

    private static string CreateWorkFolder()
    {
        Directory.CreateDirectory(JobQueue.DefaultWorkRoot);
        string folder;
        do folder = Path.Combine(JobQueue.DefaultWorkRoot, Guid.NewGuid().ToString("N")[..10]);
        while (Directory.Exists(folder));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Passes a region's progress on as its share of the export, on the thread that reports it.</summary>
    private sealed class SubProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class ExactOutput(string path) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => path;
    }
}
