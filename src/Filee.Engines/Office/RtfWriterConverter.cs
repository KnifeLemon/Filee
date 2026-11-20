// Anything → RTF without LibreOffice or Pandoc: a built-in reader (DocumentReaders) turns the source into the document
// model and RtfWriter writes Rich Text Format. Its edges cost less than Pandoc's and LibreOffice's, so the built-in
// writer is used wherever it can read the source.

using Filee.Core.Conversion;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Office;

/// <summary>Writes RTF files with the built-in Rich Text Format writer.</summary>
public sealed class RtfWriterConverter : IConverter, ICapabilityCheck
{
    /// <summary>Below Pandoc's and LibreOffice's direct edges (10 and more), so the built-in writer wins.</summary>
    private const int Cost = 8;

    public string Id => "rtf-writer";
    public string DisplayName => "RTF writer (built-in)";
    public int MaxParallelism => 0;

    /// <summary>RTF → RTF is no conversion.</summary>
    public IReadOnlyList<ConversionEdge> Edges { get; } =
        DocumentReaders.BuiltInFormats.Where(f => f != "rtf")
            .Select(f => new ConversionEdge(f, "rtf", f == "pdf" ? Cost + DocumentReaders.PdfPenalty : Cost, Checked: DocumentReaders.Checked(f))).ToList();

    /// <summary>A Word 6.0 / 95 or Works file with a .doc or .wps name, which the DOC reader doesn't read.</summary>
    public string? FirstUnsupported(string sourcePath, string sourceFormat) => DocumentReaders.Unsupported(sourcePath, sourceFormat);

    public EngineStatus GetStatus() =>
        EngineStatus.Available(string.Join(", ", Edges.Select(e => e.From.ToUpperInvariant())) + " → RTF", EngineVersions.BuiltIn);

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(0.1);
        var document = await DocumentReaders.ReadAsync(step.InputPath, step.From, step.WorkDirectory, cancellationToken,
            ocrLayer: step.Preset.Pdf.Ocr);
        progress?.Report(0.6);

        var output = step.Output.Allocate("rtf");
        if (output is null)
            return [];
        await Task.Run(() => RtfWriter.Write(document, output, cancellationToken), cancellationToken);
        progress?.Report(1);
        return [output];
    }
}
