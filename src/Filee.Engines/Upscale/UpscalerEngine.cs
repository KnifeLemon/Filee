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
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(5);
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
        var tempInput = Path.Combine(workDirectory, $"upscale-in-{Guid.NewGuid():N}.png");
        var tempOutput = Path.Combine(workDirectory, $"upscale-out-{Guid.NewGuid():N}.png");

        IExifProfile? exif = null;
        IColorProfile? icc = null;

        try
        {
            // Read source picture through ImageReader (camera RAW, layered PSD/XCF, HEIC, LIVP, KRA, etc.)
            using (var images = ImageReader.Read(inputPath, fromFormat, preset))
            {
                var frames = ImageEncoder.SelectFrames(images, fromFormat, "png");
                if (frames.Count == 0)
                    throw new InvalidOperationException("No image frames found to upscale.");

                var frame = frames[0];
                frame.AutoOrient();

                exif = frame.GetExifProfile();
                icc = frame.GetColorProfile();
                if (exif is not null && exif.ThumbnailLength > 0)
                    exif.RemoveThumbnail();

                frame.Write(tempInput, MagickFormat.Png);
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
                Timeout.InfiniteTimeSpan,
                cancellationToken,
                workingDirectory: workDirectory,
                onOutput: ParseProgress,
                stallTimeout: StallTimeout);

            AppLocalRuntime.ThrowIfMissing(executable, result.ExitCode);

            if (result.ExitCode is -1 ||
                result.StandardError.Contains("vulkan", StringComparison.OrdinalIgnoreCase) ||
                result.StandardError.Contains("vkCreateInstance", StringComparison.OrdinalIgnoreCase) ||
                result.StandardError.Contains("no gpu device", StringComparison.OrdinalIgnoreCase) ||
                result.StandardError.Contains("failed to create instance", StringComparison.OrdinalIgnoreCase))
            {
                throw new ConversionRefusedException("error.upscale_needs_vulkan");
            }

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
            if (preset.Image.KeepMetadata)
            {
                if (exif is not null)
                    upscaled.SetProfile(exif);
                if (icc is not null)
                    upscaled.SetProfile(icc);
            }
            else if (preset.Image.KeepColorProfile && icc is not null)
            {
                upscaled.SetProfile(icc);
            }

            ImageEncoder.ApplyOptions(upscaled, preset.Image, toFormat);
            ImageEncoder.WriteImage(upscaled, outputPath, toFormat);
        }
        finally
        {
            TryDelete(tempInput);
            TryDelete(tempOutput);
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file))
                File.Delete(file);
        }
        catch (IOException)
        {
        }
    }
}
