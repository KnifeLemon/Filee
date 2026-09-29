// General document conversions with Pandoc (GPL-2.0-or-later, bundled as a separate program in engines/pandoc).
// Mainly adds Markdown in and out, which no other engine offers; for office ↔ HTML LibreOffice/Word stay preferred.

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Office;

/// <summary>Markdown, HTML, DOCX, ODT and RTF conversions through Pandoc.</summary>
public sealed class PandocConverter(EngineEnvironment env) : IConverter
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private static readonly Dictionary<string, string> Readers = new()
    {
        ["md"] = "markdown",
        ["html"] = "html",
        ["docx"] = "docx",
        ["odt"] = "odt",
        ["rtf"] = "rtf",
    };

    private static readonly Dictionary<string, string> Writers = new()
    {
        ["md"] = "gfm",
        ["html"] = "html",
        ["docx"] = "docx",
        ["odt"] = "odt",
        ["rtf"] = "rtf",
        ["txt"] = "plain",
    };

    private string? _pandoc;

    public string Id => "pandoc";
    public string DisplayName => "Pandoc";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        // Markdown in / out: only Pandoc does this.
        .. new[] { "docx", "odt", "html", "rtf" }.Select(from => new ConversionEdge(from, "md")),
        .. new[] { "docx", "odt", "html", "rtf", "txt" }.Select(to => new ConversionEdge("md", to)),
        // Overlaps with LibreOffice: slightly more expensive so LibreOffice / Word win by default.
        .. new[] { "docx", "odt" }.Select(to => new ConversionEdge("html", to, 14)),
        .. new[] { "docx", "odt" }.Select(from => new ConversionEdge(from, "html", 14)),
    ];

    public EngineStatus GetStatus()
    {
        _pandoc = Locate(env);
        return _pandoc is null ? EngineStatus.Unavailable("engine.reason.not_installed") : EngineStatus.Available(_pandoc);
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var pandoc = _pandoc ?? Locate(env) ?? throw new InvalidOperationException("Pandoc was not found.");
        var output = step.Output.Allocate(step.To);
        if (output is null)
            return [];

        progress?.Report(0.1);
        List<string> args = [step.InputPath, "-f", Readers[step.From], "-t", Writers[step.To], "-o", output];
        if (step.To == "html")
            args.Add("--standalone");
        if (step.To is "md" or "html")
            args.Add("--extract-media=" + Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "_files"));

        var result = await ProcessRunner.RunAsync(pandoc, args, Timeout, cancellationToken,
            workingDirectory: Path.GetDirectoryName(Path.GetFullPath(step.InputPath)));
        if (!File.Exists(output))
            throw new InvalidOperationException($"Pandoc failed (exit {result.ExitCode}). {result.StandardError.Trim()}");
        progress?.Report(1);
        return [output];
    }

    /// <summary>pandoc executable: custom path, bundled engines/pandoc, then PATH.</summary>
    internal static string? Locate(EngineEnvironment env)
    {
        var bundled = EngineEnvironment.FindBundled("pandoc");
        var exe = OperatingSystem.IsWindows() ? "pandoc.exe" : "pandoc";
        return EngineEnvironment.FirstExisting(
            env.CustomPath("pandoc"),
            bundled is null ? null : Path.Combine(bundled, exe),
            EngineEnvironment.FindOnPath("pandoc"));
    }
}
