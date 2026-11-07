// Offline AI image upscaler converter powered by Real-ESRGAN and NCNN.

using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Magick;

namespace Filee.Engines.Upscale;

public sealed class UpscalerConverter : IConverter
{
    private static readonly string[] TargetFormats =
    [
        "png", "jpg", "webp", "avif", "ico", "bmp", "tiff"
    ];

    private readonly UpscalerEngine _engine = new();

    public string Id => "upscaler";
    public string DisplayName => "Upscayl (AI image upscaler)";
    public int MaxParallelism => 1;

    public IReadOnlyList<ConversionEdge> Edges { get; } =
        (from source in ImageEncoder.Sources
         where FormatRegistry.FindById(source)?.Category == FormatCategory.Image
         from target in TargetFormats
         select new ConversionEdge(source, target, Cost: 900, DirectOnly: true, Upscale: true))
        .ToList();

    public static string? LocateExecutable() =>
        EngineEnvironment.OwnProgram("upscayl-bin", "upscayl-bin.exe")
        ?? EngineEnvironment.BundledProgram("upscayl-bin", "upscayl-bin.exe")
        ?? EngineEnvironment.SystemProgram("upscayl-bin", "upscayl-bin.exe");

    public EngineStatus GetStatus()
    {
        var executable = LocateExecutable();
        if (executable is null)
            return EngineStatus.Unavailable("engine.reason.not_installed");

        return EngineStatus.Available(executable,
            EngineEnvironment.UsesFileesCopy("upscayl-bin") ? EngineVersions.Component("upscayl-bin") : null);
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var options = step.Preset.Upscale;
        var modelId = string.IsNullOrWhiteSpace(options.Model) ? UpscaleModels.Default : options.Model;
        var scale = options.Scale > 0 ? options.Scale : 4;

        return await _engine.UpscaleAsync(
            step.InputPath,
            step.Output,
            step.From,
            step.To,
            modelId,
            scale,
            step.WorkDirectory,
            step.Preset,
            progress,
            cancellationToken);
    }
}
