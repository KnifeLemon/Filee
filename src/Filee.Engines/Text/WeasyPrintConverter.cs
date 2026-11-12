// HTML → PDF with WeasyPrint (BSD-3-Clause, a separate program the user downloads in Settings → Engines, Windows only
// for now, #1403). WeasyPrint lays a page out with Pango and HarfBuzz like a browser prints it: headings, tables, bold
// and Arabic or Hindi shaping come out as on screen, where the built-in route (HWPX writer → rhwp) loses them.
//
// Its one edge, HTML → PDF, serves routes that start from an HTML page, Markdown (Markdig writes the page), an e-mail
// (EML, MSG) or a saved web page (MHTML), and no other (ConversionEdge.Sources): a Word document or an e-book keeps its
// route instead of going through a page. It costs so little that WeasyPrint wins wherever it is installed, even after
// the e-mail step and in last place of the engine order (1 + 9 against 31 for the built-in route); without it nothing
// changes. It is found only as Filee's download or a copy the user picked, never on the PATH.
//
// Locked down: WeasyPrint gets a copy of the page whose pictures inside the document's folder are data: URIs and that
// links to nothing else (LinkedContent: style sheets, @import, CSS url(), frames, objects, <base> taken out), and it
// runs with --allowed-protocols data, so whatever that copy still names (a picture by a relative or absolute path, a
// file: or https: address, an attachment) is refused by WeasyPrint itself and never read or fetched.

using System.Globalization;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office;

namespace Filee.Engines.Text;

/// <summary>Conversions through WeasyPrint's command line program.</summary>
public sealed partial class WeasyPrintConverter : IConverter
{
    /// <summary>Formats whose routes may end with WeasyPrint's HTML → PDF: all of them reach PDF through a page.</summary>
    internal static readonly string[] Sources = ["html", "md", "eml", "msg", "mhtml"];

    /// <summary>
    /// Cost of HTML → PDF. The built-in route costs 31 in the default engine order (HWPX writer 10 + 8, rhwp 10 + 3) and
    /// LibreOffice 28 + 9. WeasyPrint at the end of the order costs 1 + 9, after Markdown → HTML (4 + 5) 19, after
    /// MHTML or an e-mail → HTML (10 + 9) 29: always cheaper. Its source list keeps it out of every other route.
    /// </summary>
    internal const int PdfCost = 1;

    /// <summary>
    /// With --verbose WeasyPrint prints each step and each page it lays out ("Step 5 - Creating layout - Page 12"). Only
    /// this long without a line means it is stuck.
    /// </summary>
    internal static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The longest a page may take even while WeasyPrint keeps printing.</summary>
    internal static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(30);

    private string? _executable;

    public string Id => "weasyprint";
    public string DisplayName => "WeasyPrint";

    /// <summary>One WeasyPrint is a Python process with the whole page in memory; two at a time keep the PC responsive.</summary>
    public int MaxParallelism => 2;

    public IReadOnlyList<ConversionEdge> Edges { get; } = [new("html", "pdf", PdfCost, Sources: Sources)];

    public EngineStatus GetStatus()
    {
        _executable = Locate();
        return _executable is null
            ? EngineStatus.Unavailable("engine.reason.not_installed")
            : EngineStatus.Available(_executable, EngineEnvironment.UsesFileesCopy("weasyprint") ? EngineVersions.Component("weasyprint") : null);
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var executable = _executable ?? Locate() ?? throw new InvalidOperationException("WeasyPrint is not installed.");
        var work = Directory.CreateDirectory(Path.Combine(step.WorkDirectory, "weasyprint-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var page = PreparePage(step.InputPath, work, step.OriginalSourcePath);
        var temp = Path.Combine(work, "output.pdf");

        var environment = new Dictionary<string, string>
        {
            ["PYTHONIOENCODING"] = "utf-8",
            ["PYTHONUNBUFFERED"] = "1", // each step and page as it is printed: the stall watch counts on them
        };
        progress?.Report(0.02);
        var result = await ProcessRunner.RunAsync(executable, Arguments(page, temp), MaxDuration, cancellationToken,
            workingDirectory: work, environment: environment, onOutput: line => ReportProgress(line, progress),
            stallTimeout: StallTimeout);
        if (result.ExitCode != 0 || !File.Exists(temp))
            throw new InvalidOperationException($"WeasyPrint failed (exit {result.ExitCode}). {LastLines(result.StandardError.Trim().Length > 0 ? result.StandardError : result.StandardOutput)}");

        var output = step.Output.Allocate("pdf");
        if (output is null)
            return [];
        File.Move(temp, output, overwrite: true);
        progress?.Report(1);
        return [output];
    }

    /// <summary>
    /// WeasyPrint's command line: only data: URIs may be loaded, HTML attributes such as width and bgcolor count (e-mails
    /// use them), and progress lines for the stall watch.
    /// </summary>
    internal static string[] Arguments(string page, string pdf) =>
        ["--allowed-protocols", "data", "--presentational-hints", "--verbose", page, pdf];

    /// <summary>
    /// The page WeasyPrint gets, in <paramref name="work"/>: <paramref name="input"/> with the pictures the document may
    /// use (inside its folder, or the folder of <paramref name="originalSource"/> for a page made from it) as data: URIs
    /// and every other address of a picture, frame, object or style sheet taken out (<see cref="LinkedContent"/>).
    /// </summary>
    internal static string PreparePage(string input, string work, string? originalSource)
    {
        var page = Path.Combine(work, "page.html");
        var html = File.ReadAllBytes(input);
        File.WriteAllBytes(page, LinkedContent.StripHtml(html, LinkedContent.PagePictures(input, originalSource, svg: true)) ?? html);
        return page;
    }

    /// <summary>WeasyPrint prints "INFO: Step 5 - Creating layout - Page 3" while it works: seven steps.</summary>
    private static void ReportProgress(string line, IProgress<double>? progress)
    {
        if (progress is not null && StepLine().Match(line) is { Success: true } match
            && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            progress.Report(Math.Clamp(number, 1, 7) / 7.0 * 0.95);
    }

    /// <summary>The end of WeasyPrint's output, where its error message (or Python traceback) is.</summary>
    private static string LastLines(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", lines.TakeLast(3));
    }

    [GeneratedRegex(@"Step (\d) - ")]
    private static partial Regex StepLine();

    /// <summary>
    /// The user's own WeasyPrint when they chose one in Settings → Engines, else Filee's (engines/weasyprint, downloaded
    /// on demand). Never one on the PATH or elsewhere on the PC (#1403). A copy the user picked that is too old to know
    /// --allowed-protocols fails with a usage error instead of running unlocked.
    /// </summary>
    internal static string? Locate() =>
        EngineEnvironment.OwnProgram("weasyprint", "weasyprint.exe")
        ?? EngineEnvironment.BundledProgram("weasyprint", "weasyprint.exe");
}
