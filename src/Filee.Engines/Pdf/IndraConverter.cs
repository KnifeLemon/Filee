// PDF → structured Markdown with Indra OCR.  Indra is an optional local Python/ONNX command-line engine: its
// automatic mode keeps a usable PDF text layer and OCRs only the parts that need it, while scans become headings,
// lists and simple HTML tables instead of one unstructured text stream.

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Pdf;

/// <summary>Writes structured Markdown from PDFs through a locally installed Indra OCR command.</summary>
public sealed class IndraConverter : IConverter
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);
    private string? _executable;

    public string Id => "indra";
    public string DisplayName => "Indra OCR";

    // Indra itself starts one worker per usable CPU core, so concurrent Filee jobs would otherwise oversubscribe it.
    public int MaxParallelism => 1;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        // Direct only: a document rendered to PDF should not silently become an OCR Markdown file on its way to some
        // other format. Its cost is lower than PdfTextConverter's so Indra is chosen when the optional engine exists.
        new("pdf", "md", 9, DirectOnly: true),
    ];

    public EngineStatus GetStatus()
    {
        _executable = Locate();
        return _executable is null ? EngineStatus.Unavailable("engine.reason.not_installed") : EngineStatus.Available(_executable);
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var executable = _executable ?? Locate() ?? throw new InvalidOperationException("Indra OCR is not installed.");
        var output = step.Output.Allocate("md");
        if (output is null)
            return [];

        var work = Path.Combine(step.WorkDirectory, $"indra-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        progress?.Report(0.02);
        ProcessResult result;
        using (ProgressEstimate.Start(progress, TimeSpan.FromSeconds(2)))
            result = await ProcessRunner.RunAsync(executable, Arguments(step.InputPath, work), Timeout, cancellationToken,
                workingDirectory: Path.GetDirectoryName(Path.GetFullPath(step.InputPath)));

        var markdown = Directory.EnumerateFiles(work, "*.md", SearchOption.AllDirectories).SingleOrDefault();
        if (result.ExitCode != 0 || markdown is null)
        {
            var detail = (result.StandardError.Trim().Length > 0 ? result.StandardError : result.StandardOutput).Trim();
            throw new InvalidOperationException($"Indra OCR failed (exit {result.ExitCode}). {detail}");
        }

        File.Move(markdown, output, overwrite: true);
        progress?.Report(1);
        return [output];
    }

    /// <summary>Arguments for one PDF: Indra writes one Markdown file into the empty work folder.</summary>
    internal static string[] Arguments(string input, string outputDirectory) =>
        [input, "--out", outputDirectory, "--format", "md", "--mode", "auto"];

    /// <summary>Uses the command the user selected in Settings → Engines, then one available on PATH.</summary>
    internal static string? Locate() =>
        EngineEnvironment.OwnProgram("indra", "indra-ocr.exe") ?? EngineEnvironment.SystemProgram("indra", "indra-ocr.exe");
}
