// The forms of one OpenDocument type to each other without LibreOffice (Odf/OdfVariants): ODT ↔ OTT ↔ FODT,
// ODS ↔ OTS ↔ FODS, ODP ↔ OTP ↔ FODP, ODG ↔ OTG ↔ FODG. Nothing is read into a model or written again: a template
// is the package with another media type, a flat file the package's parts in one XML document (Odf/OdfFlat), so the
// document comes out as it went in.
//
// The edges cost 2, below every writer, and are steps of longer routes too: DOCX → ODT (the ODT writer) → OTT, XLSX →
// ODS (the spreadsheet engine) → FODS, each far below LibreOffice's fallback (25). The writers that only make the
// package form directly (the ODP writer's edges are direct only) write the other forms themselves through
// OdfVariants.Convert.

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using Filee.Engines.Odf;

namespace Filee.Engines.Office;

/// <summary>Converts between the package, template and flat forms of an OpenDocument type.</summary>
public sealed class OdfVariantConverter : IConverter
{
    /// <summary>Below every writer: the forms of a type convert without loss.</summary>
    internal const int Cost = 2;

    public string Id => "odf-variants";
    public string DisplayName => "OpenDocument templates and flat files (built-in)";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        .. from source in OdfVariants.All
           from target in OdfVariants.All
           where source.Family == target.Family && source.Form != target.Form
           select new ConversionEdge(source.Format, target.Format, Cost),
    ];

    public EngineStatus GetStatus() =>
        EngineStatus.Available("ODT ↔ OTT ↔ FODT, ODS ↔ OTS ↔ FODS, ODP ↔ OTP ↔ FODP, ODG ↔ OTG ↔ FODG", EngineVersions.BuiltIn);

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(0.1);
        var output = step.Output.Allocate(step.To);
        if (output is null)
            return [];
        await Task.Run(() => OdfVariants.Convert(step.InputPath, step.From, output, step.To, cancellationToken: cancellationToken), cancellationToken);
        progress?.Report(1);
        return [output];
    }
}
