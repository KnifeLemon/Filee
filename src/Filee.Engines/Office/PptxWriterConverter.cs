// Anything → PPTX slides without PowerPoint, LibreOffice or Pandoc: a reader from DocumentReaders turns the source into
// the document model and PptxWriter makes a slide per heading (Pandoc's rules), with title, section and content
// slides, lists, tables, pictures and code.
//
// Two edges per source format:
//  * the conversion to PPTX itself costs 8 (as the DOCX and ODT writers), below Pandoc's slides (20), and is
//    DirectOnly, so a document → PDF never takes the shortcut through slides and LibreOffice's cheap PPTX → PDF;
//  * as a step towards other presentation formats (Markdown → PPTX → ODP / PPT through LibreOffice) it costs as much
//    as a fallback (25), so it is only taken where nothing else reaches the target.
// PDF keeps its picture slides (PdfSlidesConverter); the PowerPoint formats themselves are the OOXML engine's job.

using Filee.Core.Conversion;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Pptx;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Office;

/// <summary>Writes PPTX slides with the built-in PresentationML writer.</summary>
public sealed class PptxWriterConverter : IConverter
{
    /// <summary>Below Pandoc's slides (20) even when Pandoc stands first in the engine order and this writer last (8 + 9).</summary>
    internal const int Cost = 8;

    /// <summary>As a step to ODP, PPT, ...: as expensive as LibreOffice's fallback edges.</summary>
    internal const int StepCost = 25;

    /// <summary>Formats that don't become document slides: presentations already, and PDF (its picture slides).</summary>
    private static readonly HashSet<string> NotSources = new(StringComparer.OrdinalIgnoreCase) { "pptx", "pptm", "potx", "potm", "ppsx", "pdf" };

    private bool? _pandoc;

    public string Id => "pptx-writer";
    public string DisplayName => "PPTX writer (built-in)";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges
    {
        get
        {
            var sources = Sources(_pandoc ??= PandocConverter.Locate() is not null).ToList();
            return
            [
                .. sources.Select(from => new ConversionEdge(from, "pptx", Cost, DirectOnly: true)),
                .. sources.Select(from => new ConversionEdge(from, "pptx", StepCost)),
            ];
        }
    }

    private static IEnumerable<string> Sources(bool pandoc) => DocumentReaders.Formats(pandoc).Where(f => !NotSources.Contains(f));

    public EngineStatus GetStatus()
    {
        _pandoc = PandocConverter.Locate() is not null;
        return EngineStatus.Available(string.Join(", ", Sources(_pandoc.Value).Select(f => f.ToUpperInvariant())) + " → PPTX", EngineVersions.BuiltIn);
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(0.1);
        var document = await DocumentReaders.ReadAsync(step.InputPath, step.From, step.WorkDirectory, cancellationToken);
        progress?.Report(0.6);

        var output = step.Output.Allocate("pptx");
        if (output is null)
            return [];
        await Task.Run(() => PptxWriter.Write(document, output), cancellationToken);
        progress?.Report(1);
        return [output];
    }
}
