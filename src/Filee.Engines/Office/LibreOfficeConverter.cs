// Office documents via LibreOffice in headless mode (MPL-2.0). LibreOffice is bundled with the installer
// (engines/libreoffice); a system installation or a user-configured path is used as fallback.
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

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using Microsoft.Win32;

namespace Filee.Engines.Office;

/// <summary>DOCX / DOC / ODT / RTF / XLSX / PPTX / HWP(X) conversions through LibreOffice.</summary>
public sealed class LibreOfficeConverter : IConverter
{
    private const int Workers = 2;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    /// <summary>Usual time of one conversion with a prepared profile (drives the progress estimate).</summary>
    private static readonly TimeSpan TypicalDuration = TimeSpan.FromSeconds(4);

    private static readonly string[] Writer = ["docx", "doc", "odt", "rtf"];
    private static readonly string[] Calc = ["xlsx", "xls", "ods", "csv"];
    private static readonly string[] Impress = ["pptx", "ppt", "odp"];

    private readonly EngineEnvironment _env;

    // A worker profile must never be used by two soffice processes at once: conversions and the warm-up take a
    // profile number from _freeProfiles (guarded by itself) after _capacity lets them in.
    private readonly SemaphoreSlim _capacity = new(Workers, Workers);
    private readonly Stack<int> _freeProfiles = new(Enumerable.Range(0, Workers).Reverse());
    private string? _soffice;
    private bool _hasHwpFilter;

    public LibreOfficeConverter(EngineEnvironment env)
    {
        _env = env;
        Locate();
        Edges = BuildEdges(_hasHwpFilter);
    }

    public string Id => "libreoffice";
    public string DisplayName => "LibreOffice";
    public int MaxParallelism => Workers;
    public IReadOnlyList<ConversionEdge> Edges { get; private set; }

    public EngineStatus GetStatus()
    {
        Locate();
        Edges = BuildEdges(_hasHwpFilter);
        return _soffice is null
            ? EngineStatus.Unavailable("engine.reason.not_installed")
            : EngineStatus.Available(_soffice + (_hasHwpFilter ? " (+H2Orestart)" : ""));
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var soffice = _soffice ?? throw new InvalidOperationException("LibreOffice was not found.");
        var slot = await TakeProfileAsync(cancellationToken);
        try
        {
            var outDir = Path.Combine(step.WorkDirectory, "lo-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(outDir);

            progress?.Report(0.1);
            ProcessResult result;
            using (ProgressEstimate.Start(progress, TypicalDuration))
                result = await RunAsync(soffice, slot, FilterFor(step.From, step.To, step.Preset.Document.PdfA), outDir, step.InputPath, cancellationToken);
            var produced = Directory.EnumerateFiles(outDir).FirstOrDefault(f => new FileInfo(f).Length > 0);
            if (produced is null)
                throw new InvalidOperationException(
                    $"LibreOffice produced no output (exit {result.ExitCode}). {result.StandardError.Trim()}");

            var target = step.Output.Allocate(step.To);
            if (target is null)
                return [];
            File.Move(produced, target, overwrite: true);
            progress?.Report(1);
            return [target];
        }
        finally
        {
            ReturnProfile(slot);
        }
    }

    /// <summary>
    /// Prepares the worker profiles that were never used, by converting a tiny file with each of them, so the user's
    /// first conversion doesn't pay for LibreOffice's first start. Quick no-op when LibreOffice is missing or the
    /// profiles are ready. A conversion that starts meanwhile gets the other profile or waits for this one.
    /// </summary>
    /// <returns>Number of profiles prepared.</returns>
    public async Task<int> WarmUpAsync(CancellationToken cancellationToken = default)
    {
        Locate();
        if (_soffice is not { } soffice || Enumerable.Range(0, Workers).All(IsPrepared))
            return 0;

        var prepared = 0;
        int? held = null;
        try
        {
            for (var i = 0; i < Workers; i++)
            {
                // Taken while the previous one is still held, so every round gets a different profile.
                var slot = await TakeProfileAsync(cancellationToken);
                if (held is { } previous)
                    ReturnProfile(previous);
                held = slot;
                if (IsPrepared(slot))
                    continue;

                var folder = Path.Combine(Path.GetTempPath(), "filee-warmup-" + Guid.NewGuid().ToString("N")[..8]);
                try
                {
                    Directory.CreateDirectory(folder);
                    var input = Path.Combine(folder, "warmup.txt");
                    await File.WriteAllTextAsync(input, "Filee", cancellationToken);
                    await RunAsync(soffice, slot, FilterFor("txt", "pdf", pdfA: false), folder, input, cancellationToken);
                    prepared++;
                }
                finally
                {
                    try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }
        finally
        {
            if (held is { } last)
                ReturnProfile(last);
        }
        return prepared;
    }

    private string ProfileDirectory(int slot) => Path.Combine(_env.DataDirectory, "libreoffice-profiles", $"worker-{slot}");

    /// <summary>LibreOffice has started with this profile before (it creates the "user" folder).</summary>
    private bool IsPrepared(int slot) => Directory.Exists(Path.Combine(ProfileDirectory(slot), "user"));

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

    private async Task<ProcessResult> RunAsync(string soffice, int slot, string filter, string outDir, string input, CancellationToken cancellationToken)
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
            args.Add("-env:UNO_JAVA_JFW_JREHOME=" + new Uri(jre + Path.DirectorySeparatorChar).AbsoluteUri);
        args.AddRange(["--convert-to", filter, "--outdir", outDir, input]);
        return await ProcessRunner.RunAsync(soffice, args, Timeout, cancellationToken);
    }

    /// <summary>Maps a target format to a LibreOffice <c>--convert-to</c> filter specification.</summary>
    internal static string FilterFor(string from, string to, bool pdfA)
    {
        var family = Calc.Contains(from) ? "calc" : Impress.Contains(from) ? "impress" : "writer";
        return to switch
        {
            // PDF/A-2b via the JSON filter options syntax (LibreOffice 7.4+).
            "pdf" when pdfA => $"pdf:{family}_pdf_Export:{{\"SelectPdfVersion\":{{\"type\":\"long\",\"value\":\"2\"}}}}",
            "pdf" => $"pdf:{family}_pdf_Export",
            "docx" => "docx:MS Word 2007 XML",
            "doc" => "doc:MS Word 97",
            "odt" => "odt",
            "rtf" => "rtf:Rich Text Format",
            "txt" => "txt:Text (encoded):UTF8",
            "html" when family == "calc" => "html:HTML (StarCalc)",
            "html" => "html:HTML (StarWriter)",
            "xlsx" => "xlsx:Calc MS Excel 2007 XML",
            "xls" => "xls:MS Excel 97",
            "ods" => "ods",
            "csv" => "csv:Text - txt - csv (StarCalc):44,34,76",
            "pptx" => "pptx:Impress MS PowerPoint 2007 XML",
            "ppt" => "ppt:MS PowerPoint 97",
            "odp" => "odp",
            _ => to,
        };
    }

    private static List<ConversionEdge> BuildEdges(bool hwp)
    {
        var edges = new List<ConversionEdge>();
        void Add(IEnumerable<string> sources, IEnumerable<string> targets, int cost)
        {
            foreach (var s in sources)
                foreach (var t in targets)
                    if (s != t)
                        edges.Add(new ConversionEdge(s, t, cost));
        }

        Add(Writer, [.. Writer, "pdf", "txt", "html"], 12);
        Add(Calc, [.. Calc, "pdf", "html"], 12);
        Add(Impress, [.. Impress, "pdf"], 12);
        Add(["txt", "html"], ["docx", "odt", "pdf"], 15);
        if (hwp)
            Add(["hwp", "hwpx"], ["pdf", "docx", "odt", "rtf"], 18);
        return edges;
    }

    /// <summary>Finds soffice: user path → bundled copy → system installation.</summary>
    private void Locate()
    {
        var bundled = EngineEnvironment.FindBundled("libreoffice");
        var exe = OperatingSystem.IsWindows() ? "soffice.exe" : "soffice";

        _soffice = EngineEnvironment.FirstExisting(
            _env.CustomPath(Id),
            bundled is null ? null : Path.Combine(bundled, "program", exe),
            bundled is null ? null : Path.Combine(bundled, "Contents", "MacOS", "soffice"),
            RegistryInstallPath(),
            OperatingSystem.IsWindows() ? @"C:\Program Files\LibreOffice\program\soffice.exe" : null,
            OperatingSystem.IsWindows() ? @"C:\Program Files (x86)\LibreOffice\program\soffice.exe" : null,
            OperatingSystem.IsMacOS() ? "/Applications/LibreOffice.app/Contents/MacOS/soffice" : null,
            EngineEnvironment.FindOnPath("soffice"));

        _hasHwpFilter = _soffice is not null && HasH2Orestart(_soffice);
    }

    /// <summary>Detects the H2Orestart extension in the installation (shared) extension folders.</summary>
    private static bool HasH2Orestart(string soffice)
    {
        try
        {
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

    private static string? RegistryInstallPath()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\LibreOffice\UNO\InstallPath");
            var dir = key?.GetValue(null) as string;
            return dir is null ? null : Path.Combine(dir, "soffice.exe");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
