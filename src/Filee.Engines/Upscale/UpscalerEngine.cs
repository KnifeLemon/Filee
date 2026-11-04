// Execution engine for offline image upscaling using Real-ESRGAN / NCNN.

using System.Globalization;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Magick;
using ImageMagick;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.Engines.Upscale;

public sealed partial class UpscalerEngine
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);
    private readonly ILogger _logger;

    public UpscalerEngine(ILogger<UpscalerEngine>? logger = null)
    {
        _logger = logger ?? NullLogger<UpscalerEngine>.Instance;
    }

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*%", RegexOptions.Compiled)]
    private static partial Regex PercentageRegex();

    public async Task UpscaleAsync(
        string inputPath,
        string outputPath,
        string fromFormat,
        string toFormat,
        string modelId,
        int scale,
        string workDirectory,
        Preset preset,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var executable = UpscalerConverter.LocateExecutable()
            ?? throw new ConversionRefusedException("error.upscale_needs_engine");

        var modelFolder = await UpscaleModels.EnsureAsync(modelId, progress: null, cancellationToken);
        var model = UpscaleModels.Find(modelId)
            ?? throw new InvalidOperationException($"Unknown upscale model '{modelId}'.");

        Directory.CreateDirectory(workDirectory);
        var tempInput = Path.Combine(workDirectory, "upscale-input.png");
        var tempOutput = Path.Combine(workDirectory, "upscale-output.png");

        // Read source picture through ImageReader (camera RAW, layered PSD/XCF, HEIC, LIVP, KRA, etc.)
        using (var images = ImageReader.Read(inputPath, fromFormat, preset))
        {
            var frames = ImageEncoder.SelectFrames(images, fromFormat, "png");
            if (frames.Count == 0)
                throw new InvalidOperationException("No image frames found to upscale.");
            frames[0].Write(tempInput, MagickFormat.Png);
        }

        var scaleValue = Math.Clamp(scale, 2, 4);
        var arguments = new List<string>
        {
            "-i", tempInput,
            "-o", tempOutput,
            "-m", modelFolder,
            "-n", model.Id,
            "-s", scaleValue.ToString(CultureInfo.InvariantCulture),
            "-f", "png",
            "-v"
        };

        void ParseProgress(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            var match = PercentageRegex().Match(line.Trim());
            if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            {
                progress?.Report(Math.Clamp(pct / 100.0, 0.0, 1.0));
            }
        }

        var result = await ProcessRunner.RunAsync(
            executable,
            arguments,
            Timeout,
            cancellationToken,
            workingDirectory: workDirectory,
            onOutput: ParseProgress);

        if (result.ExitCode != 0 || !File.Exists(tempOutput))
        {
            throw new InvalidOperationException(
                $"Upscaling failed (exit code {result.ExitCode}):\n{result.StandardError}");
        }

        progress?.Report(1.0);

        var targetDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(targetDir))
            Directory.CreateDirectory(targetDir);

        using var upscaled = new MagickImage(tempOutput);
        ImageEncoder.ApplyOptions(upscaled, preset.Image, toFormat);
        ImageEncoder.WriteImage(upscaled, outputPath, toFormat);
    }
}
