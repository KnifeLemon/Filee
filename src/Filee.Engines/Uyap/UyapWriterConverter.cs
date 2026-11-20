// Anything → UYAP (.udf, #1546, #1633): a built-in reader (DocumentReaders) turns the source into the document model
// and UyapWriter writes the UYAP Document Editor's file. No other engine writes UDF; the edges cost what the RTF and
// ODT writers' do, so a source reaches UDF in one step.

using Filee.Core.Conversion;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Uyap;

/// <summary>Writes UYAP documents with the built-in UDF writer.</summary>
public sealed class UyapWriterConverter : IConverter, ICapabilityCheck
{
    private const int Cost = 8;

    public string Id => "udf-writer";
    public string DisplayName => "UYAP writer (built-in)";
    public int MaxParallelism => 0;

    /// <summary>UDF → UDF is no conversion.</summary>
    public IReadOnlyList<ConversionEdge> Edges { get; } =
        DocumentReaders.BuiltInFormats.Where(f => f != UyapReader.Format)
            .Select(f => new ConversionEdge(f, UyapReader.Format, f == "pdf" ? Cost + DocumentReaders.PdfPenalty : Cost, Checked: DocumentReaders.Checked(f))).ToList();

    /// <summary>A Word 6.0 / 95 or Works file with a .doc or .wps name, which the DOC reader doesn't read.</summary>
    public string? FirstUnsupported(string sourcePath, string sourceFormat) => DocumentReaders.Unsupported(sourcePath, sourceFormat);

    public EngineStatus GetStatus() =>
        EngineStatus.Available(string.Join(", ", Edges.Select(e => e.From.ToUpperInvariant())) + " → UDF", EngineVersions.BuiltIn);

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(0.1);
        var document = await DocumentReaders.ReadAsync(step.InputPath, step.From, step.WorkDirectory, cancellationToken,
            ocrLayer: step.Preset.Pdf.Ocr);
        progress?.Report(0.6);

        var output = step.Output.Allocate(UyapReader.Format);
        if (output is null)
            return [];
        await Task.Run(() => UyapWriter.Write(document, output, cancellationToken), cancellationToken);
        progress?.Report(1);
        return [output];
    }
}
