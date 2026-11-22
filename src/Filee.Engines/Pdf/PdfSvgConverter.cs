// PDF → SVG with Filee's own PDF interpreter (PdfSvgWriter): vector paths, gradients, clipping, transparency, pictures
// and text as glyph outlines with a selectable text layer. Every page of the preset's page range becomes an SVG of its
// own, named as PDF → PNG names its pictures (name_p3.svg; one page keeps the name as it is). LibreOffice's PDF → SVG
// (Draw, the first page only) stays as the fallback; this edge costs less.
//
// NotForImageSources, as LibreOffice's edge: a photo that became a PDF would only come back as a picture inside an SVG,
// so images are traced instead (VectorConverter).

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Pdf;

/// <summary>PDF → SVG, one SVG per page.</summary>
public sealed class PdfSvgConverter : IConverter
{
    public string Id => "pdf-svg";
    public string DisplayName => "PDF to SVG (built-in)";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; } = [new("pdf", "svg", NotForImageSources: true)];

    public EngineStatus GetStatus() => EngineStatus.Available("PDF → SVG", EngineVersions.BuiltIn);

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() => PdfSvgWriter.Write(step.InputPath, count =>
        {
            var pages = PageRange.Parse(step.Preset.Pdf.PageRange, count);
            if (pages.Count == 0)
                throw new InvalidOperationException("The PDF has no pages in the selected range.");
            return pages;
        }, (page, count) => step.Output.Allocate("svg", count > 1 ? $"_p{page + 1}" : null), progress, cancellationToken), cancellationToken);
}
