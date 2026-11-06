// Offline AI image upscaler converter powered by Real-ESRGAN and NCNN.

using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Magick;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.Engines.Upscale;

public sealed class UpscalerConverter : IConverter
{
    private static readonly string[] TargetFormats =
    [
        "png", "jpg", "webp", "avif", "ico", "bmp", "tiff"
    ];

    private readonly UpscalerEngine _engine;
    private string? _executable;

    public UpscalerConverter(ILogger<UpscalerConverter>? logger = null)
    {
        _engine = new UpscalerEngine(logger as ILogger<UpscalerEngine>);
    }

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
        _executable = LocateExecutable();
        if (_executable is null)
            return EngineStatus.Unavailable("engine.reason.not_installed");

        return EngineStatus.Available(_executable,
            EngineEnvironment.UsesFileesCopy("upscayl-bin") ? EngineVersions.Component("upscayl-bin") : null);
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var targetFormat = step.To;
        var outputPath = step.Output.Allocate(targetFormat);
        if (outputPath is null)
            return [];

        var options = step.Preset.Upscale;
        var modelId = string.IsNullOrWhiteSpace(options.Model) ? UpscaleModels.Default : options.Model;
        var scale = options.Scale > 0 ? options.Scale : 4;

        await _engine.UpscaleAsync(
            step.InputPath,
            outputPath,
            step.From,
            step.To,
            modelId,
            scale,
            step.WorkDirectory,
            step.Preset,
            progress,
            cancellationToken);

        return [outputPath];
    }
}
