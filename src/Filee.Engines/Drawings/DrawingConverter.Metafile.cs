// EMF and WMF from what Filee reads into the slide model, without LibreOffice: DrawingMetafile writes each page with
// Filee's own metafile writers (Metafiles/EmfWriter, WmfWriter), shapes as vector drawing and text as text.
//
// - Sources: SVG and SVGZ (SvgDrawingReader; a file with what that reader doesn't draw, such as filters, masks or CSS,
//   is checked off to LibreOffice Draw, whose SVG import draws more), the drawing formats read into the slide model
//   (DrawingFormats: Visio, ODG / OTG / FODG, whiteboards; each with its check) and mind maps.
// - A metafile holds one picture: a drawing of several pages becomes a file per page (_p1, _p2, ...), the PDF
//   options' page range choosing them, as SVG and PNG output do.
// - Costs: the ODP writer's (8), below LibreOffice's fallback (25) whatever the engine order adds; direct only.

using Filee.Core.Conversion;
using Filee.Engines.Hwp.Hwpx.Markup;
using Filee.Engines.Pdf;
using Filee.Engines.Slides;
using Filee.Engines.Typesetting;

namespace Filee.Engines.Drawings;

public sealed partial class DrawingConverter
{
    /// <summary>The metafile formats written.</summary>
    internal static readonly string[] MetafileTargets = ["emf", "wmf"];

    /// <summary>Sources written as metafiles besides the drawing formats with a slide-model reader.</summary>
    private static readonly string[] MetafileSources = ["svg", "svgz", MindMapReader.FreeMind, MindMapReader.XMind];

    /// <summary>The edges to EMF and WMF of the slide-model formats among <paramref name="formats"/> and of the other sources.</summary>
    private static IEnumerable<ConversionEdge> MetafileEdges(IReadOnlyList<DrawingFormat> formats)
    {
        foreach (var format in formats.Where(f => f.Read is not null))
            foreach (var to in MetafileTargets)
                yield return new ConversionEdge(format.Id, to, OdgCost, DirectOnly: true, Checked: format.FirstUnsupported is not null);
        foreach (var from in MetafileSources)
            foreach (var to in MetafileTargets)
                yield return new ConversionEdge(from, to, OdgCost, DirectOnly: true, Checked: from is "svg" or "svgz");
    }

    private static bool IsMetafile(string format) => MetafileTargets.Contains(format, StringComparer.OrdinalIgnoreCase);

    /// <summary>The source's pages in the page range, each written as an EMF or a WMF.</summary>
    private List<string> WriteMetafiles(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var deck = ReadForOdg(step, progress, cancellationToken);
        if (deck.Slides.Count == 0)
            throw new InvalidDataException("The drawing has no pages.");
        if (deck.LeftOut.Count > 0)
            step.LeftOut?.Invoke(new LeftOutNotice("notice.renderer_left_out", deck.LeftOut));
        progress?.Report(0.2);
        var wmf = string.Equals(step.To, "wmf", StringComparison.OrdinalIgnoreCase);
        var pages = PageRange.Parse(step.Preset.Pdf.PageRange, deck.Slides.Count);
        using var shaper = new Shaper();
        using var renderer = new SlideRenderer(new FontResolver(), shaper, cancellationToken);
        var written = new List<string>();
        var done = 0;
        foreach (var index in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = DrawingMetafile.Write(deck, deck.Slides[index], wmf, renderer, cancellationToken: cancellationToken);
            if (step.Output.Allocate(wmf ? "wmf" : "emf", pages.Count > 1 ? $"_p{index + 1}" : null) is { } path)
            {
                File.WriteAllBytes(path, bytes);
                written.Add(path);
            }
            done++;
            progress?.Report(0.2 + 0.8 * done / pages.Count);
        }
        return written;
    }
}
