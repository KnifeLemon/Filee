// Execution engine for offline image upscaling using Real-ESRGAN / NCNN.

using System.Globalization;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Magick;
using ImageMagick;

namespace Filee.Engines.Upscale;

public sealed partial class UpscalerEngine
{
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(5);
    internal const int MaxFrames = 300;

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*%", RegexOptions.Compiled)]
    private static partial Regex PercentageRegex();

    public async Task<IReadOnlyList<string>> UpscaleAsync(
        string inputPath,
        string outputPath,
        string fromFormat,
        string toFormat,
        string modelId,
        int scale,
        string workDirectory,
        Preset preset,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        await UpscaleAsync(
            inputPath,
            new SingleFileOutput(outputPath),
            fromFormat,
            toFormat,
            modelId,
            scale,
            workDirectory,
            preset,
            progress,
            cancellationToken);

    public async Task<IReadOnlyList<string>> UpscaleAsync(
        string inputPath,
        IOutputAllocator output,
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

        var from = ReadFormat(fromFormat, inputPath);
        using var images = ImageReader.Read(inputPath, from, preset, cancellationToken);
        var animation = ImageEncoder.KeepsAnimation(from, toFormat);
        var frames = ImageEncoder.SelectFrames(images, from, toFormat);
        if (frames.Count == 0)
            throw new InvalidOperationException("No image frames found to upscale.");

        if (frames.Count > MaxFrames)
        {
            throw new ConversionRefusedException(
                "error.upscale_too_many_frames",
                string.Create(CultureInfo.InvariantCulture, $"{frames.Count:N0} > {MaxFrames:N0}"));
        }

        var upscaledFrames = new List<IMagickImage<byte>>();
        try
        {
            for (var frameIndex = 0; frameIndex < frames.Count; frameIndex++)
            {
                var frame = frames[frameIndex];
                var tempInput = Path.Combine(workDirectory, $"upscale-in-{frameIndex}-{Guid.NewGuid():N}.png");
                var tempOutput = Path.Combine(workDirectory, $"upscale-out-{frameIndex}-{Guid.NewGuid():N}.png");

                try
                {
                    var (exif, icc) = PrepareFrame(frame, tempInput);

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
                            var totalPct = (frameIndex + Math.Clamp(pct / 100.0, 0.0, 1.0)) / frames.Count;
                            progress?.Report(Math.Clamp(totalPct, 0.0, 1.0));
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

                    if (IsVulkanRefusal(result.ExitCode, result.StandardError))
                        throw new ConversionRefusedException("error.upscale_needs_vulkan");

                    if (result.ExitCode != 0 || !File.Exists(tempOutput))
                    {
                        throw new InvalidOperationException(
                            $"Upscaling failed (exit code {result.ExitCode}):\n{result.StandardError}");
                    }

                    var upscaled = new MagickImage(tempOutput);
                    upscaledFrames.Add(upscaled);
                    if (animation)
                    {
                        upscaled.AnimationDelay = frame.AnimationDelay;
                        upscaled.GifDisposeMethod = frame.GifDisposeMethod;
                        upscaled.AnimationIterations = frame.AnimationIterations;
                    }
                    FinishImage(upscaled, preset, toFormat, exif, icc);
                }
                finally
                {
                    TryDelete(tempInput);
                    TryDelete(tempOutput);
                }
            }

            progress?.Report(1.0);

            return ImageEncoder.Write(upscaledFrames, toFormat, output, preset.Image, inputPath, animation);
        }
        finally
        {
            foreach (var f in upscaledFrames)
                f.Dispose();
        }
    }

    /// <summary>
    /// The format the frames are read as, as MagickImageConverter reads them: an animated PNG saved as .png is "apng",
    /// an AVIF image sequence "avis" (<see cref="AvifSequence"/>), so both keep their frames and timing.
    /// </summary>
    internal static string ReadFormat(string fromFormat, string inputPath) =>
        fromFormat == "png" && Apng.IsAnimated(inputPath) ? "apng" : AvifSequence.SourceFormat(fromFormat, inputPath);

    internal static (IExifProfile? Exif, IColorProfile? Icc) PrepareFrame(IMagickImage<byte> frame, string tempInputPath)
    {
        frame.AutoOrient();
        var exif = frame.GetExifProfile();
        var icc = frame.GetColorProfile();
        if (exif is not null && exif.ThumbnailLength > 0)
            exif.RemoveThumbnail();

        frame.Write(tempInputPath, MagickFormat.Png);
        return (exif, icc);
    }

    internal static void FinishImage(
        IMagickImage<byte> upscaled,
        Preset preset,
        string toFormat,
        IExifProfile? exif,
        IColorProfile? icc)
    {
        if (preset.Image.KeepMetadata)
        {
            if (exif is not null)
                upscaled.SetProfile(exif);
            if (icc is not null && ColorSpaceMatches(icc, upscaled.ColorSpace))
                upscaled.SetProfile(icc);
        }
        else if (preset.Image.KeepColorProfile && icc is not null && ColorSpaceMatches(icc, upscaled.ColorSpace))
        {
            upscaled.SetProfile(icc);
        }

        ImageEncoder.ApplyOptions(upscaled, preset.Image, toFormat);
    }

    internal static bool ColorSpaceMatches(IColorProfile profile, ColorSpace colorSpace) =>
        (colorSpace is ColorSpace.sRGB or ColorSpace.RGB && profile.ColorSpace is ColorSpace.sRGB or ColorSpace.RGB)
        || profile.ColorSpace == colorSpace;

    internal static bool IsVulkanRefusal(int exitCode, string stderr)
    {
        if (exitCode == 0)
            return false;
        if (exitCode is -1 or 255)
            return true;
        return stderr.Contains("invalid gpu device", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("no gpu device", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("vulkan", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("vkCreateInstance", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("failed to create instance", StringComparison.OrdinalIgnoreCase);
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

    private sealed class SingleFileOutput(string path) : IOutputAllocator
    {
        private bool _allocated;

        public string? Allocate(string extension, string? suffix = null)
        {
            if (_allocated && suffix is not null)
            {
                var dir = Path.GetDirectoryName(path);
                var name = Path.GetFileNameWithoutExtension(path);
                return Path.Combine(dir ?? "", $"{name}{suffix}.{extension}");
            }
            _allocated = true;
            return path;
        }
    }
}
