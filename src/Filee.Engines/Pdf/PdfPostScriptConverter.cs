// PDF → PostScript and EPS without Ghostscript: Filee's own writer (Pdf/PostScript). One PostScript file holds the
// selected pages; an EPS holds one page, so a multi-page PDF gives one EPS per page (_p{n}, as PDF → PNG names
// them). PostScript, EPS and AI → PDF stay with Ghostscript: reading PostScript needs a PostScript interpreter.
//
// The edges cost 8, below Ghostscript's 10, so the writer wins with the default engine order (an engine missing from
// the order gets the largest penalty, like Ghostscript at the end of it); other routes keep their costs.

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using Filee.Engines.Pdf.PostScript;
using Microsoft.Extensions.Logging;

namespace Filee.Engines.Pdf;

/// <summary>PDF → PS / EPS with Filee's own PostScript writer.</summary>
public sealed class PdfPostScriptConverter(ILogger<PdfPostScriptConverter>? logger = null) : IConverter
{
    public string Id => "pdf-ps";
    public string DisplayName => "PostScript writer (built-in)";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        new("pdf", "ps", Cost: 8),
        new("pdf", "eps", Cost: 8),
    ];

    public EngineStatus GetStatus() => EngineStatus.Available("PDF → PS, EPS", EngineVersions.BuiltIn);

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            var writer = new PdfToPostScript(step.InputPath, step.Preset.Pdf.RenderDpi, logger, cancellationToken);
            var pages = PageRange.Parse(step.Preset.Pdf.PageRange, writer.PageCount);
            if (pages.Count == 0)
                throw new InvalidDataException("The PDF has no pages.");
            progress?.Report(0.05);
            var written = new List<string>();
            if (step.To == "ps")
            {
                var output = step.Output.Allocate("ps");
                if (output is null)
                    return [];
                var scratch = Path.Combine(step.WorkDirectory, $"ps-{Guid.NewGuid():N}.ps");
                writer.Write(pages, scratch, eps: false, step.WorkDirectory);
                File.Move(scratch, output, overwrite: true);
                written.Add(output);
            }
            else
            {
                for (var i = 0; i < pages.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var output = step.Output.Allocate("eps", pages.Count > 1 ? $"_p{pages[i] + 1}" : null);
                    if (output is null)
                        continue;
                    var scratch = Path.Combine(step.WorkDirectory, $"ps-{Guid.NewGuid():N}.eps");
                    writer.Write([pages[i]], scratch, eps: true, step.WorkDirectory);
                    File.Move(scratch, output, overwrite: true);
                    written.Add(output);
                    progress?.Report(0.05 + 0.95 * (i + 1) / pages.Count);
                }
            }
            if (writer.RasterPages.Count > 0)
                step.Notice?.Invoke("notice.ps_pages_as_pictures");
            progress?.Report(1);
            return written;
        }, cancellationToken);
}
