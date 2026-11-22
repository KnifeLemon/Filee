// Colour palettes (#1615): PAL, GIMP GPL and Adobe ACT into each other, as a list of HEX codes (TXT, JSON), or as a
// swatch drawn as SVG, from which PNG, PDF and the other pictures follow.

using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Palettes;

/// <summary>Reads and writes colour palettes; needs no external program.</summary>
public sealed class PaletteConverter : IConverter
{
    /// <summary>Palette formats Filee reads.</summary>
    internal static readonly string[] Sources = ["pal", "gpl", "act"];

    /// <summary>Palette formats Filee writes (a .pal is only read: which of its layouts to write is anyone's guess).</summary>
    private static readonly string[] PaletteTargets = ["gpl", "act"];

    public string Id => "palette";
    public string DisplayName => "Color palettes (built-in)";
    public int MaxParallelism => 0;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        .. from source in Sources
           from target in PaletteTargets
           where source != target
           select new ConversionEdge(source, target),
        // The HEX lists are a dump, not a document or a data set: never a step towards DOCX, EPUB, YAML, XLSX, ….
        .. Sources.Select(source => new ConversionEdge(source, "txt", DirectOnly: true)),
        .. Sources.Select(source => new ConversionEdge(source, "json", DirectOnly: true)),
        // The swatch picture: PNG, JPG, PDF, … from there.
        .. Sources.Select(source => new ConversionEdge(source, "svg")),
    ];

    public EngineStatus GetStatus() => EngineStatus.Available(version: EngineVersions.BuiltIn);

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            var palette = Palette.Read(step.InputPath, step.From);
            var name = FormatRegistry.NameWithoutExtension(step.InputPath);
            var output = step.Output.Allocate(FormatRegistry.Get(step.To).PrimaryExtension);
            if (output is null)
                return [];
            var utf8 = new UTF8Encoding(false);
            switch (step.To)
            {
                case "act":
                    File.WriteAllBytes(output, palette.ToAct());
                    break;
                case "gpl":
                    File.WriteAllText(output, palette.ToGpl(name), utf8);
                    break;
                case "json":
                    File.WriteAllText(output, palette.ToJson(name), utf8);
                    break;
                case "svg":
                    File.WriteAllText(output, palette.ToSvg(name), utf8);
                    break;
                default:
                    File.WriteAllText(output, palette.ToHexList(), utf8);
                    break;
            }
            progress?.Report(1);
            return [output];
        }, cancellationToken);
}
