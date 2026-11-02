// Office documents via LibreOffice in headless mode (MPL-2.0): the optional fallback for formats the built-in engines
// don't handle (DOC, XLS, PPT, OpenDocument, ...) and the only reader of rarer ones: Works / WPS, WordPerfect, Lotus
// Word Pro, AbiWord, Apple Pages / Numbers / Keynote, StarOffice, WPS Office, ClarisWorks / AppleWorks, MacWrite, T602,
// Pocket Word, flat OpenDocument, Publisher, CorelDRAW, Visio, CGM. It also writes the OpenDocument templates, FODT,
// XLT and EMF / WMF, and turns SVG into EMF / WMF / ODG and a PDF page into SVG.
// Uses a selected installation, Filee's downloaded copy, or the system installation.
//
// Gotchas handled here:
//  * Each concurrent soffice process needs its own user profile (-env:UserInstallation), otherwise
//    conversions silently fail with exit code 1.
//  * soffice writes <outdir>/<input name>.<ext>; we convert into a private folder and move the result.
//  * Always use a timeout: a document with a modal dialog would otherwise hang forever.
//  * HWP/HWPX import needs the H2Orestart extension (GPL-3.0), pre-installed into the bundled copy.
//  * The first conversion with a new profile takes 2-3× longer (LibreOffice builds its caches), so WarmUpAsync
//    converts a tiny file once per worker profile in the background. --terminate_after_init alone doesn't help.
//  * soffice reports no progress: ProgressEstimate keeps the progress ring moving while it runs.
//  * Its ZIP reader refuses some containers other readers open (Tencent Docs exports, issue #141), saying only "source
//    file could not be loaded": an Office Open XML / OpenDocument file is tried once more as a repacked copy.

using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Engines.Infrastructure;
using Filee.Engines.Pdf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Filee.Engines.Office;

/// <summary>Word processor, spreadsheet, presentation and drawing conversions (and HWP / HWPX) through LibreOffice.</summary>
public sealed class LibreOfficeConverter : IConverter
{
    private const int Workers = 2;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    /// <summary>Usual time of one conversion with a prepared profile (drives the progress estimate).</summary>
    private static readonly TimeSpan TypicalDuration = TimeSpan.FromSeconds(4);

    // Read and written: FODT (flat XML) and the OpenDocument templates (OTT, OTS, OTP), XLT (Excel 97 template).
    private static readonly string[] Writer = ["docx", "doc", "odt", "rtf", "fodt", "ott"];
    private static readonly string[] Calc = ["xlsx", "xls", "ods", "csv", "xlt", "ots"];
    private static readonly string[] Impress = ["pptx", "ppt", "odp", "otp"];

    // Formats LibreOffice only reads, by the application that opens them (checked against the filter registry of
    // LibreOffice 26.2: the DocumentService of each type's preferred import filter). ClarisWorks / AppleWorks files can
    // also be spreadsheets or drawings; they are taken as word processor files, the common case.
    private static readonly string[] WriterImport = ["dot", "wps", "wpd", "lwp", "abw", "pages", "sdw", "cwk", "mcw", "602", "psw"];
    private static readonly string[] CalcImport = ["et", "numbers", "sdc", "fods"];

    /// <summary>"sda" also covers .sdd: StarDraw files (.sda) open in Draw, StarImpress files (.sdd) in Impress.</summary>
    private static readonly string[] ImpressImport = ["key", "dps", "sda", "fodp"];

    /// <summary>CGM opens in Impress but is a drawing: exported like one (PDF, ODG, SVG, PNG, EMF, WMF).</summary>
    private static readonly string[] ImpressGraphics = ["cgm"];
    private static readonly string[] Draw = ["pub", "cdr", "vsd", "odg", "fodg", "emf", "wmf"];

    /// <summary>What a drawing is exported as. SVG, PNG, EMF and WMF hold the first page.</summary>
    private static readonly string[] DrawTargets = ["pdf", "odg", "svg", "png", "emf", "wmf"];

    /// <summary>
    /// Opened in Draw although another engine reads them better: SVG (only → EMF / WMF / ODG; the vector engine draws it
    /// to PDF and images) and PDF (only → SVG / ODG; one page for SVG, see <see cref="FirstPage"/>).
    /// </summary>
    private static readonly string[] DrawOnlyFor = ["svg", "pdf"];

    /// <summary>Outputs for which LibreOffice lays out document text rather than preserving it for a later editor.</summary>
    private static readonly HashSet<string> RenderedTargets = ["pdf", "png", "svg", "emf", "wmf"];

    private readonly EngineEnvironment _env;
    private readonly ILogger _logger;

    // A worker profile must never be used by two soffice processes at once: conversions and the warm-up take a
    // profile number from _freeProfiles (guarded by itself) after _capacity lets them in.
    private readonly SemaphoreSlim _capacity = new(Workers, Workers);
    private readonly Stack<int> _freeProfiles = new(Enumerable.Range(0, Workers).Reverse());
    private readonly bool[] _prepared = new bool[Workers];

    private string? _soffice;
    private bool _hasHwpFilter;

    public LibreOfficeConverter(EngineEnvironment env, ILogger? logger = null)
    {
        _env = env;
        _logger = logger ?? NullLogger.Instance;
        Locate();
    }

    public string Id => "libreoffice";
    public string DisplayName => "LibreOffice";

    public EngineStatus Status()
    {
        return _soffice is null
            ? EngineStatus.Unavailable("engine.reason.not_installed")
            : _hasHwpFilter
                ? EngineStatus.Available(_soffice, _hasHwpFilter ? "H2Orestart" : null)
                : EngineStatus.Available(_soffice, EngineVersions.Component("libreoffice")
                    is { } v ? v : null);
    }

    public async Task ConvertAsync(ConversionStep step, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var soffice = _soffice ?? throw new InvalidOperationException("LibreOffice was not found.");
        var input = step.InputPath;
        var filter = FilterFor(step.From, step.To, step.PdfA, Path.GetExtension(input).TrimStart('.'));
        var slot = await TakeProfileAsync(cancellationToken);
        try
        {
            var (produced, result) = await ConvertOnceAsync(soffice, slot, filter, step.WorkDirectory, input, progress, cancellationToken);
            if (produced is null && result.ExitCode != 0 && IsOfficeOpenXmlOrOpenDocument(input))
            {
                var repacked = await RepackAsync(input, step.WorkDirectory, cancellationToken);
                (produced, _) = await ConvertOnceAsync(soffice, slot, filter, step.WorkDirectory, repacked, progress, cancellationToken);
            }
            if (produced is null)
                throw new ConversionException(
                    $"LibreOffice produced no output (exit {result.ExitCode}). {result.StandardError.Trim()}");
            // Calc sometimes turns stale Excel print tiles into completely empty PDF pages anywhere in the output.
            if (step.To == "pdf" && FamilyOf(step.From) == "calc")
                PdfBlankPages.Remove(produced);
            File.Move(produced, step.OutputPath, overwrite: true);
        }
        finally
        {
            ReturnProfile(slot);
        }
    }

    /// <summary>Runs LibreOffice into a fresh folder; returns the file it wrote (null if none) and how it ended.</summary>
    private async Task<(string? Produced, ProcessResult Result)> ConvertOnceAsync(string soffice, int slot, string filter, string workDirectory,
        string input, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var outDir = Path.Combine(workDirectory, "lo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outDir);
        ProcessResult result;
        using (ProgressEstimate.Start(progress, TypicalDuration))
            result = await RunAsync(soffice, slot, filter, outDir, input, cancellationToken);
        return (ProducedIn(outDir), result);
    }

    /// <summary>The first non-empty file LibreOffice wrote into <paramref name="outDir"/>, or null.</summary>
    private static string? ProducedIn(string outDir) =>
        Directory.EnumerateFiles(outDir).FirstOrDefault(f => new FileInfo(f).Length > 0);

    /// <summary>
    /// A probe for <see cref="ProcessRunner"/>: true when the PDF output file exists,
    /// has not changed size since the last call and ends with its %%EOF marker.
    /// Non-PDF formats are deliberately not probed: a stable file size alone does not prove completion.
    /// </summary>
    private static Func<bool> OutputComplete(string outDir)
    {
        long last = -1;
        return () =>
        {
            if (!Directory.Exists(outDir) || ProducedIn(outDir) is not { } file)
                return false;

            if (!file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                return false;

            var length = new FileInfo(file).Length;
            var steady = length == last;
            last = length;

            if (!steady || length == 0)
                return false;

            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var tail = new byte[(int)Math.Min(length, 1024)];
            stream.Seek(-tail.Length, SeekOrigin.End);
            stream.ReadExactly(tail);

            return System.Text.Encoding.ASCII.GetString(tail).Contains("%%EOF", StringComparison.Ordinal);
        };
    }

    /// <summary>
    /// Prepares each worker profile once so the first real conversion is not 2–3× slower. Runs in the background;
    /// failures are ignored (the conversion will still work, just slower the first time).
    /// </summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        if (_soffice is not { } soffice || Enumerable.Range(0, Workers).All(IsPrepared))
            return;
        var prepared = 0;
        for (var i = 0; i < Workers; i++)
        {
            if (IsPrepared(i))
                continue;
            var slot = await TakeProfileAsync(cancellationToken);
            try
            {
                if (IsPrepared(slot))
                    continue;
                var folder = Path.Combine(Path.GetTempPath(), "filee-lo-warmup-" + Guid.NewGuid().ToString("N")[..8]);
                try
                {
                    Directory.CreateDirectory(folder);
                    var input = Path.Combine(folder, "warmup.txt");
                    await File.WriteAllTextAsync(input, "Filee", cancellationToken);
                    await RunAsync(soffice, slot, FilterFor("txt", "pdf", pdfA: false), folder, input, cancellationToken, probe: false);
                    prepared++;
                }
                finally
                {
                    try { Directory.Delete(folder, true); } catch { }
                }
                MarkPrepared(slot);
            }
            finally
            {
                ReturnProfile(slot);
            }
        }
        if (prepared > 0)
            _logger.LogInformation("Warmed up {Count} LibreOffice profile(s)", prepared);
    }

    private bool IsPrepared(int slot) => _prepared[slot];
    private void MarkPrepared(int slot) => _prepared[slot] = true;

    private async Task<int> TakeProfileAsync(CancellationToken cancellationToken)
    {
        await _capacity.WaitAsync(cancellationToken);
        lock (_freeProfiles)
            return _freeProfiles.Pop();
    }

    private void ReturnProfile(int slot)
    {
        lock (_freeProfiles)
            _freeProfiles.Push(slot);
        _capacity.Release();
    }

    private async Task<ProcessResult> RunAsync(string soffice, int slot, string filter, string outDir, string input, CancellationToken cancellationToken,
        bool probe = true)
    {
        var profileDir = ProfileDirectory(slot);
        Directory.CreateDirectory(profileDir);
        var args = new List<string>
        {
            "--headless", "--norestore", "--nologo", "--nodefault", "--nolockcheck", "--nofirststartwizard",
            "-env:UserInstallation=" + new Uri(profileDir).AbsoluteUri,
        };
        // The HWP import filter (H2Orestart) is written in Java: point LibreOffice at the bundled JRE.
        if (EngineEnvironment.FindBundled("jre") is { } jre)
        {
            if (Directory.Exists(Path.Combine(jre, "Contents", "Home")))
                jre = Path.Combine(jre, "Contents", "Home");
            args.Add("-env:UNO_JAVA_JFW_JREHOME=" + new Uri(jre + Path.DirectorySeparatorChar).AbsoluteUri);
        }
        args.AddRange(["--convert-to", filter, "--outdir", outDir, input]);

        // Only PDF has a completion marker we can verify here. Other formats may pause while being written,
        // so a stable file size is not sufficient evidence that the conversion has finished.
        var isPdf = filter.StartsWith("pdf:", StringComparison.OrdinalIgnoreCase);

        // The probe ends a run whose soffice.bin keeps our pipes open after the document was converted (the warm-up
        // converts nothing we need, so it runs without one).
        return await ProcessRunner.RunAsync(soffice, args, Timeout, cancellationToken,
            outputReady: probe && isPdf ? OutputComplete(outDir) : null);
    }

    /// <summary>Maps a target format to a LibreOffice <c>--convert-to</c> filter specification.</summary>
    /// <param name="extension">Extension of the input file, for format ids that cover files of two applications.</param>
    internal static string FilterFor(string from, string to, bool pdfA, string? extension = null)
    {
        var family = FamilyOf(from, extension);
        return to switch
        {
            // PDF/A-2b via the JSON filter options syntax (LibreOffice 7.4+).
            "pdf" when pdfA => $"pdf:{family}_pdf_Export:{{\"SelectPdfVersion\":{{\"type\":\"long\",\"value\":\"2\"}}}}",
            "pdf" => $"pdf:{family}_pdf_Export",
            "docx" => "docx:MS Word 2007 XML",
            "doc" => "doc:MS Word 97",
            "odt" => "odt:writer8",
            "rtf" => "rtf:Rich Text Format",
            "fodt" => "fodt:OpenDocument Text Flat XML",
            "ott" => "ott:writer8_template",
            "xlsx" => "xlsx:Calc MS Excel 2007 XML",
            "xls" => "xls:MS Excel 97",
            "ods" => "ods:calc8",
            "csv" => "csv:Text - txt - csv (StarCalc)",
            "xlt" => "xlt:MS Excel 97 Vorlage/Template",
            "ots" => "ots:calc8_template",
            "pptx" => "pptx:Impress MS PowerPoint 2007 XML",
            "ppt" => "ppt:MS PowerPoint 97",
            "odp" => "odp:impress8",
            "otp" => "otp:impress8_template",
            "odg" => "odg:draw8",
            "svg" => "svg:draw_svg_Export",
            "png" => "png:draw_png_Export",
            "emf" => "emf:draw_emf_Export",
            "wmf" => "wmf:draw_wmf_Export",
            _ => throw new ArgumentException($"LibreOffice cannot write {to}.")
        };
    }

    private static string FamilyOf(string from, string? extension = null)
    {
        if (Writer.Contains(from) || WriterImport.Contains(from) || from is "txt" or "html" or "htm")
            return "writer";
        if (Calc.Contains(from) || CalcImport.Contains(from))
            return "calc";
        if (Impress.Contains(from) || ImpressImport.Contains(from) || ImpressGraphics.Contains(from))
            return "impress";
        if (Draw.Contains(from) || DrawOnlyFor.Contains(from))
            return "draw";
        // HWP / HWPX
        if (from is "hwp" or "hwpx")
            return "writer";
        return "writer";
    }

    private static bool IsOfficeOpenXmlOrOpenDocument(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext is "docx" or "xlsx" or "pptx" or "odt" or "ods" or "odp" or "odg";
    }

    private static async Task<string> RepackAsync(string input, string workDirectory, CancellationToken cancellationToken)
    {
        var repacked = Path.Combine(workDirectory, "repacked-" + Path.GetFileName(input));
        await using (var src = File.OpenRead(input))
        await using (var dst = File.Create(repacked))
            await src.CopyToAsync(dst, cancellationToken);
        return repacked;
    }

    private string ProfileDirectory(int slot) =>
        Path.Combine(_env.DataDirectory, "libreoffice-profiles", "worker-" + slot);

    /// <summary>
    /// Locates soffice: the one chosen in Settings → Engines, else Filee's copy (engines/libreoffice),
    /// else a LibreOffice installed on the PC. Each conversion uses its own profile, so an open LibreOffice is untouched.
    /// </summary>
    private void Locate()
    {
        _soffice = EngineEnvironment.OwnProgram("libreoffice", "soffice.exe")
            ?? WithRuntime(EngineEnvironment.BundledProgram("libreoffice", "soffice.exe"))
            ?? EngineEnvironment.SystemProgram("libreoffice", "soffice.exe");
        _hasHwpFilter = _soffice is not null && HasH2Orestart(_soffice);
    }

    /// <summary>
    /// Filee's copy as earlier versions installed it has LibreOffice's C++ runtime only in System64, where soffice.exe
    /// does not look (see <see cref="AppLocalRuntime.CopyLibreOfficeRuntime"/>): copied next to it once, so LibreOffice
    /// also starts on PCs without the Visual C++ Redistributable.
    /// </summary>
    private string? WithRuntime(string? soffice)
    {
        if (soffice is null || !OperatingSystem.IsWindows())
            return soffice;
        try
        {
            var copied = AppLocalRuntime.CopyLibreOfficeRuntime(Directory.GetParent(Path.GetDirectoryName(soffice)!)!.FullName);
            if (copied > 0)
                _logger.LogInformation("Copied {Count} C++ runtime files next to {Program}", copied, soffice);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not copy the C++ runtime next to {Program}", soffice);
        }
        return soffice;
    }

    /// <summary>Detects the H2Orestart extension in the installation (shared) extension folders.</summary>
    private static bool HasH2Orestart(string soffice)
    {
        try
        {
            soffice = File.ResolveLinkTarget(soffice, returnFinalTarget: true)?.FullName ?? soffice;
            var root = Directory.GetParent(Path.GetDirectoryName(soffice)!)!.FullName;
            foreach (var sub in new[] { "share/uno_packages", "share/extensions", "Resources/uno_packages", "Resources/extensions" })
            {
                var dir = Path.Combine(root, sub);
                if (Directory.Exists(dir) &&
                    Directory.EnumerateFileSystemEntries(dir, "*H2Orestart*", SearchOption.AllDirectories).Any())
                    return true;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }
}
