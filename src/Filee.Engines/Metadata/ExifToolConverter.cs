// The engine behind the Metadata tool's conversions: writes a file's metadata changes (Preset.Metadata) into a copy in
// its own format with ExifTool, the picture, sound or document itself as it was. Its edges are of their own kind
// (ConversionEdge.Metadata), never part of an ordinary route, and the only ones a conversion with changes takes.

using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Metadata;

public sealed class ExifToolConverter : IConverter
{
    private string? _executable;

    public string Id => "exiftool";
    public string DisplayName => "ExifTool";

    /// <summary>Each write copies the whole file: two at a time keep the disk busy enough.</summary>
    public int MaxParallelism => 2;

    public IReadOnlyList<ConversionEdge> Edges { get; } = [.. ExifTool.WritableFormats.Select(f => new ConversionEdge(f, f, Metadata: true))];

    public EngineStatus GetStatus()
    {
        _executable = ExifTool.Locate();
        return _executable is null ? EngineStatus.Unavailable("engine.reason.not_installed")
            : ExifTool.NeedsPerl(_executable) ? EngineStatus.Unavailable("engine.reason.needs_perl")
            : EngineStatus.Available(_executable, EngineEnvironment.UsesFileesCopy("exiftool") ? EngineVersions.Component("exiftool") : null);
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var exifTool = _executable ?? ExifTool.Locate() ?? throw new InvalidOperationException("ExifTool is not installed.");
        if (step.Preset.Metadata is not { IsEmpty: false } edits)
            throw new InvalidOperationException("No metadata changes to write.");
        // The file's own extension (".jpeg" stays ".jpeg"): the copy is the same file but for its metadata.
        var extension = Path.GetExtension(step.InputPath).TrimStart('.');
        // Null: a file is there already and the output rule skips it.
        if (step.Output.Allocate(extension.Length > 0 ? extension : step.To) is not { } output)
            return [];
        try
        {
            await ExifTool.WriteAsync(exifTool, step.InputPath, output, edits, cancellationToken, progress);
        }
        catch (MetadataRefusedException ex)
        {
            throw new ConversionRefusedException("error.metadata_refused", ex.Message);
        }
        progress?.Report(1);
        return [output];
    }
}
