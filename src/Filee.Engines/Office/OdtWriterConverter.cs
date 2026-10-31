// Anything → ODT without LibreOffice or Pandoc: a built-in reader (DocumentReaders) turns the source into the document
// model and OdtWriter writes OpenDocument text. Its edges cost less than Pandoc's and LibreOffice's, so the built-in
// writer is used wherever it can read the source.

using Filee.Core.Conversion;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Odt;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Office;

/// <summary>Writes ODT files with the built-in OpenDocument writer.</summary>
public sealed class OdtWriterConverter : IConverter
{
    /// <summary>Below Pandoc's and LibreOffice's direct edges (10 and more), so the built-in writer wins.</summary>
    private const int Cost = 8;

    public string Id => "odt-writer";
    public string DisplayName => "ODT writer (built-in)";
    public int MaxParallelism => 0;

    /// <summary>ODT → ODT is no conversion.</summary>
    public IReadOnlyList<ConversionEdge> Edges { get; } =
        DocumentReaders.BuiltInFormats.Where(f => f != "odt")
            .Select(f => new ConversionEdge(f, "odt", f == "pdf" ? Cost + DocumentReaders.PdfPenalty : Cost)).ToList();

    public EngineStatus GetStatus() =>
        EngineStatus.Available(string.Join(", ", Edges.Select(e => e.From.ToUpperInvariant())) + " → ODT", EngineVersions.BuiltIn);

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(0.1);
        var document = await DocumentReaders.ReadAsync(step.InputPath, step.From, step.WorkDirectory, cancellationToken,
            ocrLayer: step.Preset.Pdf.Ocr);
        progress?.Report(0.6);

        var output = step.Output.Allocate("odt");
        if (output is null)
            return [];
        await Task.Run(() => OdtWriter.Write(document, output), cancellationToken);
        progress?.Report(1);
        return [output];
    }
}
