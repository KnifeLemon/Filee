// Font conversion between TTF, OTF, WOFF, WOFF2 and EOT, entirely in managed code (zlib and Brotli come with .NET).
// CFF (PostScript) outlines are converted to TrueType when the target needs them (TTF, EOT). See docs/ENGINES.md.

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Fonts;

/// <summary>Converts web and desktop font formats into each other.</summary>
public sealed class FontConverter : IConverter
{
    public string Id => "font";
    public string DisplayName => "Fonts (built-in)";
    public int MaxParallelism => 0;

    /// <summary>Every format to every other one, directly, plus read-only Windows bitmap FON to each of them.</summary>
    public IReadOnlyList<ConversionEdge> Edges { get; } =
    [
        .. from source in FontFile.Formats from target in FontFile.Formats where source != target select new ConversionEdge(source, target),
        .. FontFile.Formats.Select(target => new ConversionEdge("fon", target)),
    ];

    public EngineStatus GetStatus() => EngineStatus.Available(
        "TTF, OTF, WOFF, WOFF2, EOT; FON (Windows bitmap fonts) read",
        EngineVersions.BuiltIn);

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            if (step.From == "fon")
                return ConvertFon(step, progress, cancellationToken);
            var font = FontFile.Load(File.ReadAllBytes(step.InputPath), cancellationToken);
            progress?.Report(0.2);
            var outlines = progress is null ? null : new SubProgress(progress, 0.2, 0.6);
            var data = FontFile.Save(font, step.To, cancellationToken, outlines);
            progress?.Report(0.9);

            var output = step.Output.Allocate(step.To);
            if (output is null)
                return [];
            File.WriteAllBytes(output, data);
            progress?.Report(1);
            return [output];
        }, cancellationToken);

    /// <summary>
    /// One output per RT_FONT resource. Every resource is parsed and every output is built in memory before the first
    /// path is allocated, so a failure or cancellation before writing leaves no new file and no existing file changed.
    /// </summary>
    private static IReadOnlyList<string> ConvertFon(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var strikes = FonReader.Read(File.ReadAllBytes(step.InputPath), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(0.1);

        // Same-height, same-style ordinals and the resource count are fixed now, so skipped outputs never rename the others.
        var ordinals = new int[strikes.Count];
        var counts = new Dictionary<(int Height, int Weight, bool Italic), int>();
        for (var i = 0; i < strikes.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var strike = strikes[i];
            var key = (strike.PixelHeight, strike.Weight, strike.Italic);
            ordinals[i] = counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        var results = new byte[strikes.Count][];
        for (var i = 0; i < strikes.Count; i++)
        {
            var font = FonOutlineBuilder.Build(strikes[i], cancellationToken, strikes.Count, ordinals[i]);
            results[i] = FontFile.Save(font, step.To, cancellationToken);
            progress?.Report(i == 0 ? 0.85 : 0.85 + 0.05 * i / strikes.Count);
            cancellationToken.ThrowIfCancellationRequested();
        }
        cancellationToken.ThrowIfCancellationRequested();

        var outputs = new List<string>();
        for (var i = 0; i < strikes.Count; i++)
        {
            var strike = strikes[i];
            var style = (strike.Weight, strike.Italic) switch
            {
                (400, false) => "",
                (700, false) => "-Bold",
                (400, true) => "-Italic",
                (700, true) => "-BoldItalic",
                (_, false) => $"-Weight{strike.Weight}",
                (_, true) => $"-Weight{strike.Weight}Italic",
            };
            var suffix = strikes.Count == 1 ? null
                : $"-{strike.PixelHeight}px{style}{(ordinals[i] > 1 ? $"-{ordinals[i]}" : "")}";
            if (step.Output.Allocate(step.To, suffix) is not { } output)
                continue;
            File.WriteAllBytes(output, results[i]);
            outputs.Add(output);
        }
        progress?.Report(1);
        return outputs;
    }

    /// <summary>Maps 0–1 progress of one phase onto a slice of the step's progress, synchronously.</summary>
    private sealed class SubProgress(IProgress<double> parent, double start, double length) : IProgress<double>
    {
        public void Report(double value) => parent.Report(start + length * value);
    }
}
