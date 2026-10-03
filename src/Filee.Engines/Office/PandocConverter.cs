// General document conversions with Pandoc (GPL-2.0-or-later, bundled as a separate program in engines/pandoc).
// Adds Markdown ↔ DOCX / ODT / RTF, HTML ↔ DOCX / ODT, and reStructuredText and LaTeX ↔ Markdown / HTML / DOCX / ODT
// (and → RTF, EPUB, TXT); Markdown → HTML / TXT / HWPX / DOCX is built in (MarkdownConverter, the HWPX and DOCX writers).

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Office;

/// <summary>Markdown, HTML, DOCX, ODT, RTF, reStructuredText and LaTeX conversions through Pandoc.</summary>
public sealed class PandocConverter : IConverter
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private static readonly Dictionary<string, string> Readers = new()
    {
        ["md"] = "markdown",
        ["html"] = "html",
        ["docx"] = "docx",
        ["odt"] = "odt",
        ["rtf"] = "rtf",
        ["rst"] = "rst",
        ["tex"] = "latex",
    };

    private static readonly Dictionary<string, string> Writers = new()
    {
        ["md"] = "gfm",
        ["html"] = "html",
        ["docx"] = "docx",
        ["odt"] = "odt",
        ["rtf"] = "rtf",
        ["txt"] = "plain",
        ["rst"] = "rst",
        ["tex"] = "latex",
        ["epub"] = "epub",
    };

    /// <summary>reStructuredText and LaTeX: what Pandoc reads and writes well (no PDF, which would need a TeX engine).</summary>
    private static readonly string[] Markup = ["rst", "tex"];

    private string? _pandoc;

    public string Id => "pandoc";
    public string DisplayName => "Pandoc";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        // Markdown in / out: only Pandoc does this.
        .. new[] { "docx", "odt", "html", "rtf" }.Select(from => new ConversionEdge(from, "md")),
        .. new[] { "docx", "odt", "html", "rtf", "txt" }.Select(to => new ConversionEdge("md", to)),
        // Overlaps with LibreOffice (the fallback engine, which costs more).
        .. new[] { "docx", "odt" }.Select(to => new ConversionEdge("html", to, 14)),
        .. new[] { "docx", "odt" }.Select(from => new ConversionEdge(from, "html", 14)),
        // reStructuredText and LaTeX in / out: only Pandoc does this.
        .. from markup in Markup
           from to in new[] { "md", "html", "docx", "odt", "rtf", "epub", "txt", "rst", "tex" }
           where to != markup
           select new ConversionEdge(markup, to),
        .. from markup in Markup
           from source in new[] { "md", "html", "docx", "odt" }
           select new ConversionEdge(source, markup),
    ];

    public EngineStatus GetStatus()
    {
        _pandoc = Locate();
        return _pandoc is null ? EngineStatus.Unavailable("engine.reason.not_installed") : EngineStatus.Available(_pandoc, EngineEnvironment.OwnCopyFolder("pandoc") is null ? EngineVersions.Component("pandoc") : null);
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var pandoc = _pandoc ?? Locate() ?? throw new InvalidOperationException("Pandoc was not found.");
        var output = step.Output.Allocate(step.To);
        if (output is null)
            return [];

        progress?.Report(0.1);
        List<string> args = [step.InputPath, "-f", Readers[step.From], "-t", Writers[step.To], "-o", output];
        if (step.To is "html" or "tex")
            args.Add("--standalone");
        if (step.To is "md" or "html" or "rst" or "tex")
            args.Add("--extract-media=" + Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "_files"));

        ProcessResult result;
        using (ProgressEstimate.Start(progress, TimeSpan.FromSeconds(1.5)))
            result = await ProcessRunner.RunAsync(pandoc, args, Timeout, cancellationToken,
                workingDirectory: Path.GetDirectoryName(Path.GetFullPath(step.InputPath)));
        if (!File.Exists(output))
            throw new InvalidOperationException($"Pandoc failed (exit {result.ExitCode}). {result.StandardError.Trim()}");
        progress?.Report(1);
        return [output];
    }

    /// <summary>Filee's own pandoc (engines/pandoc, downloaded on demand), never one found elsewhere on the system.</summary>
    internal static string? Locate() =>
        EngineEnvironment.OwnProgram("pandoc", "pandoc.exe") ?? LocateBundled();

    private static string? LocateBundled() =>
        EngineEnvironment.FindBundled("pandoc") is { } bundled
            ? EngineEnvironment.FirstExisting(Path.Combine(bundled, OperatingSystem.IsWindows() ? "pandoc.exe" : "pandoc"))
            : null;
}
