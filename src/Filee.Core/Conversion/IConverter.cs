// The contract every conversion engine implements. Adding a new engine = one class implementing IConverter.
// See docs/ADDING-A-CONVERTER.md.

using Filee.Core.Presets;

namespace Filee.Core.Conversion;

/// <summary>
/// One direct conversion a converter can perform, e.g. <c>docx → pdf</c>.
/// </summary>
/// <param name="From">Source format id.</param>
/// <param name="To">Target format id. May equal <paramref name="From"/> (e.g. re-encode a JPG at lower quality).</param>
/// <param name="Cost">
/// Relative cost used by <see cref="RoutePlanner"/>. Use 10 for a normal direct conversion,
/// higher for lossy or slow paths. Multi-step routes add up their costs.
/// </param>
public readonly record struct ConversionEdge(string From, string To, int Cost = 10);

/// <summary>Whether an engine can currently be used.</summary>
/// <param name="IsAvailable">True if conversions can run.</param>
/// <param name="ReasonKey">Localization key explaining why the engine is unavailable (e.g. <c>engine.reason.not_installed</c>).</param>
/// <param name="Detail">Extra, non-localized detail such as the program's path (shown as a tooltip).</param>
/// <param name="Version">Version label shown in Settings → Engines, e.g. <c>"Markdig 1.4.0"</c> or <c>"0.8.6"</c>.</param>
public sealed record EngineStatus(bool IsAvailable, string? ReasonKey = null, string? Detail = null, string? Version = null)
{
    public static EngineStatus Available(string? detail = null, string? version = null) => new(true, null, detail, version);
    public static EngineStatus Unavailable(string reasonKey, string? detail = null) => new(false, reasonKey, detail);
}

/// <summary>Hands out output paths to a converter.</summary>
public interface IOutputAllocator
{
    /// <summary>
    /// Returns the path the converter should write the next output file to.
    /// </summary>
    /// <param name="extension">
    /// Extension of the file being written, without dot. Empty asks for a folder path (archive extraction); the
    /// converter creates the folder.
    /// </param>
    /// <param name="suffix">Optional name suffix such as <c>"_p2"</c> when one input produces several files.</param>
    /// <returns>A path, or <c>null</c> if this output must be skipped (existing file + "skip" policy).</returns>
    string? Allocate(string extension, string? suffix = null);
}

/// <summary>Everything a converter needs to perform one step.</summary>
/// <param name="InputPath">File to read.</param>
/// <param name="From">Format id of <paramref name="InputPath"/>.</param>
/// <param name="To">Format id to produce.</param>
/// <param name="Preset">Options chosen by the user.</param>
/// <param name="Output">Where to write results.</param>
/// <param name="WorkDirectory">Private scratch directory for this job; deleted afterwards.</param>
public sealed record ConversionStep(
    string InputPath,
    string From,
    string To,
    Preset Preset,
    IOutputAllocator Output,
    string WorkDirectory);

/// <summary>
/// A conversion engine (ImageMagick, LibreOffice, rhwp, ...).
/// </summary>
/// <remarks>
/// Implementations must be thread-safe up to <see cref="MaxParallelism"/> concurrent calls,
/// must honour the cancellation token, and must never modify the input file.
/// </remarks>
public interface IConverter
{
    /// <summary>Stable id used in settings (engine priority, custom paths). Lower-case, no spaces.</summary>
    string Id { get; }

    /// <summary>Human readable engine name (not localized, e.g. "LibreOffice").</summary>
    string DisplayName { get; }

    /// <summary>Direct conversions this engine supports.</summary>
    IReadOnlyList<ConversionEdge> Edges { get; }

    /// <summary>How many conversions may run at the same time. 0 means "one per CPU core".</summary>
    int MaxParallelism { get; }

    /// <summary>Checks if the engine is installed / usable. Should be cheap; results may be cached.</summary>
    EngineStatus GetStatus();

    /// <summary>Performs one conversion step and returns the files that were written.</summary>
    Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>Merges several PDF files into one. Implemented by the PDF engine.</summary>
public interface IPdfMerger
{
    Task MergeAsync(IReadOnlyList<string> inputPaths, string outputPath, CancellationToken cancellationToken);
}
