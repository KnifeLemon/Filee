// Anything → DOCX without Word, LibreOffice or Pandoc: a built-in reader (DocumentReaders) turns the source into the
// document model and DocxWriter writes WordprocessingML. Its edges cost less than Pandoc's and LibreOffice's, so the
// built-in writer is used wherever it can read the source.

using Filee.Core.Conversion;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Ocr;
using Filee.Engines.Pdf;

namespace Filee.Engines.Office;

/// <summary>Writes DOCX files with the built-in WordprocessingML writer.</summary>
public sealed class DocxWriterConverter : IConverter
{
    /// <summary>Below Pandoc's and LibreOffice's direct edges (10 and more), so the built-in writer wins.</summary>
    private const int Cost = 8;

    /// <summary>Word files themselves: DOCX → DOCX is no conversion, the variants are the OOXML engine's job.</summary>
    private static readonly HashSet<string> WordFormats = new(StringComparer.OrdinalIgnoreCase) { "docx", "docm", "dotx", "dotm" };

    public string Id => "docx-writer";
    public string DisplayName => "DOCX writer (built-in)";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
        DocumentReaders.BuiltInFormats.Where(f => !WordFormats.Contains(f))
            .Select(f => new ConversionEdge(f, "docx", f == "pdf" ? Cost + DocumentReaders.PdfPenalty : Cost)).ToList();

    public EngineStatus GetStatus() =>
        EngineStatus.Available(string.Join(", ", Edges.Select(e => e.From.ToUpperInvariant())) + " → DOCX", EngineVersions.BuiltIn);

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(0.05);
        var input = step.InputPath;
        var ocrLayer = step.Preset.Pdf.Ocr;
        if (step.From == "pdf" && !ocrLayer && !PdfDocumentReader.HasText(input, cancellationToken))
        {
            // The layer between analysis and conversion: a PDF without text is a scan — Tesseract reads it into
            // text first, so the Word file carries the text, never the page picture. The layer itself is an
            // intermediate: it goes to the work directory and is deleted with it.
            var ocr = new TesseractConverter();
            var layered = await ocr.ConvertAsync(step with { To = "pdf", Output = new ScratchAllocator(step.WorkDirectory) },
                progress is null ? null : new Progress<double>(p => progress.Report(0.05 + p * 0.5)), cancellationToken);
            if (layered.Count == 0)
                throw new InvalidOperationException("The OCR layer produced no file.");
            input = layered[0];
            ocrLayer = true;
        }

        progress?.Report(0.6);
        var document = await DocumentReaders.ReadAsync(input, step.From, step.WorkDirectory, cancellationToken,
            ocrLayer: ocrLayer);

        var output = step.Output.Allocate("docx");
        if (output is null)
            return [];
        await Task.Run(() => DocxWriter.Write(document, output), cancellationToken);
        progress?.Report(1);
        return [output];
    }

    /// <summary>Hands out paths in the job's work directory: the intermediate OCR layer is deleted with it.</summary>
    private sealed class ScratchAllocator(string workDirectory) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null)
        {
            var dir = Path.Combine(workDirectory, Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var ext = extension.TrimStart('.');
            return ext.Length == 0 ? dir : Path.Combine(dir, $"layer{suffix}.{ext}");
        }
    }
}
