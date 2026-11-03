// Transcribes speech in video and audio using OpenAI Whisper (whisper.cpp) into SRT subtitles.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Engines.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.Engines.Media;

/// <summary>
/// Speech transcription engine powered by OpenAI Whisper.
/// Transcribes speech in video and audio files into SRT subtitles.
/// </summary>
public sealed partial class WhisperConverter : IConverter
{
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(5);

    private readonly ILogger _logger;

    public WhisperConverter(ILogger<WhisperConverter>? logger = null)
    {
        _logger = logger ?? NullLogger<WhisperConverter>.Instance;
    }

    public string Id => "whisper";
    public string DisplayName => "Whisper";
    public int MaxParallelism => 1;

    public IReadOnlyList<ConversionEdge> Edges { get; } = BuildEdges();

    private static IReadOnlyList<ConversionEdge> BuildEdges()
    {
        var videos = FormatRegistry.Known.Where(f => f.Category == FormatCategory.Video).Select(f => f.Id).ToList();
        var audios = FormatRegistry.Known.Where(f => f.Category == FormatCategory.Audio).Select(f => f.Id).ToList();
        return
        [
            // Video -> SRT transcription directly
            .. videos.Select(v => new ConversionEdge(v, "srt", Cost: 10, Caption: true)),
            // Audio -> SRT transcription directly
            .. audios.Select(a => new ConversionEdge(a, "srt", Cost: 10, Caption: true)),
        ];
    }

    public EngineStatus GetStatus()
    {
        if (!OperatingSystem.IsWindows())
            return EngineStatus.Unavailable("engine.reason.unsupported_platform");

        var (cli, model) = LocateTools();
        if (cli is null || model is null)
            return EngineStatus.Unavailable("engine.reason.not_installed");

        var modelName = Path.GetFileNameWithoutExtension(model);
        return EngineStatus.Available(cli, $"Whisper ({modelName})");
    }

    public static (string? Cli, string? Model) LocateTools()
    {
        var cli = LocateCli();
        var model = LocateModel(cli);
        return (cli, model);
    }

    internal static string? LocateCli()
    {
        // 1. User's own copy from Settings -> Engines
        if (EngineEnvironment.OwnProgram("whisper", "whisper-cli.exe") is { } own && File.Exists(own))
            return own;

        // 2. Filee bundled or downloaded engine folder
        if (EngineEnvironment.BundledProgram("whisper", "whisper-cli.exe") is { } bundled && File.Exists(bundled))
            return bundled;

        // 3. System / PATH
        if (EngineEnvironment.SystemProgram("whisper", "whisper-cli.exe") is { } system && File.Exists(system))
            return system;

        return null;
    }

    internal static string? LocateModel(string? cliPath = null)
    {
        var searchDirs = new List<string>();

        // 1. Downloaded / bundled whisper-model folder
        if (EngineEnvironment.FindBundled("whisper-model") is { } wmDir && Directory.Exists(wmDir))
            searchDirs.Add(wmDir);

        // 2. Downloaded / bundled whisper folder
        if (EngineEnvironment.FindBundled("whisper") is { } wDir && Directory.Exists(wDir))
            searchDirs.Add(wDir);

        // 3. User's own copy folder
        if (EngineEnvironment.OwnCopyFolder("whisper") is { } own && Directory.Exists(own))
            searchDirs.Add(own);

        // 4. Folder containing the cli executable if located
        if (!string.IsNullOrEmpty(cliPath) && Path.GetDirectoryName(cliPath) is { } cliDir && Directory.Exists(cliDir))
            searchDirs.Add(cliDir);

        string[] preferredModels = ["ggml-large-v3-turbo.bin", "ggml-base.bin", "ggml-small.bin", "ggml-tiny.bin"];

        foreach (var dir in searchDirs.Distinct())
        {
            foreach (var pref in preferredModels)
            {
                var candidate = Path.Combine(dir, pref);
                if (File.Exists(candidate))
                    return candidate;
            }

            var anyBin = Directory.GetFiles(dir, "*.bin").FirstOrDefault();
            if (anyBin is not null)
                return anyBin;
        }

        return null;
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (step.To != "srt")
            throw new InvalidOperationException($"Whisper converter only produces SRT subtitles (requested '{step.To}').");

        var (whisperCli, modelPath) = LocateTools();
        if (whisperCli is null || modelPath is null)
            throw new InvalidOperationException("Whisper engine or model was not found. Please download Whisper in Settings \u2192 Engines.");

        var ffmpegTools = FfmpegConverter.Locate();
        if (ffmpegTools is null || !File.Exists(ffmpegTools.Value.Ffmpeg))
            throw new InvalidOperationException("FFmpeg was not found. Please install FFmpeg from Settings \u2192 Engines.");

        var ffmpeg = ffmpegTools.Value.Ffmpeg;

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(0.02);

        // 1. Extract 16kHz mono WAV for Whisper inference (works for both video and audio files)
        var audioWav = Path.Combine(step.WorkDirectory, $"whisper_audio_{Guid.NewGuid():N}.wav");
        var extractArgs = new[]
        {
            "-hide_banner", "-nostdin", "-nostats", "-v", "error", "-y",
            "-i", step.InputPath,
            "-vn", "-ar", "16000", "-ac", "1",
            "-progress", "pipe:1",
            audioWav
        };

        var extractResult = await FfmpegRunner.RunAsync(ffmpeg, extractArgs, null, cancellationToken, StallTimeout);
        if (extractResult.ExitCode != 0 || !File.Exists(audioWav))
            throw new InvalidOperationException($"FFmpeg failed to extract audio: {extractResult.StandardError}");

        progress?.Report(0.10);
        cancellationToken.ThrowIfCancellationRequested();

        // 2. Run Whisper CLI transcription (auto-detect language or preset's SpokenLanguage, srt output)
        var outPrefix = Path.Combine(step.WorkDirectory, $"whisper_out_{Guid.NewGuid():N}");
        var lang = string.IsNullOrWhiteSpace(step.Preset?.Media?.SpokenLanguage)
            ? "auto"
            : step.Preset.Media.SpokenLanguage.Trim();

        var threads = Math.Clamp(Environment.ProcessorCount, 1, 8).ToString(CultureInfo.InvariantCulture);
        var whisperArgs = new[]
        {
            "-m", modelPath,
            "-f", audioWav,
            "-osrt",
            "-of", outPrefix,
            "-l", lang,
            "-t", threads,
            "-sow",
            "-pp"
        };

        var whisperResult = await ProcessRunner.RunAsync(
            whisperCli,
            whisperArgs,
            Timeout.InfiniteTimeSpan,
            cancellationToken,
            onOutput: line => ReportWhisperProgress(line, progress),
            stallTimeout: StallTimeout);

        var srtPath = outPrefix + ".srt";

        if (!File.Exists(srtPath) || whisperResult.ExitCode != 0)
            throw new InvalidOperationException($"Whisper failed to transcribe audio (exit code {whisperResult.ExitCode}): {whisperResult.StandardError}");

        var srtContent = await File.ReadAllTextAsync(srtPath, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        // 3. Produce SRT output
        var srtOutput = step.Output.Allocate("srt");
        if (srtOutput is null)
            return [];

        await File.WriteAllTextAsync(srtOutput, srtContent, Encoding.UTF8, cancellationToken);
        progress?.Report(1.0);
        return [srtOutput];
    }

    private static void ReportWhisperProgress(string line, IProgress<double>? progress)
    {
        if (progress is not null && ProgressPercent().Match(line) is { Success: true } match
            && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var percent))
        {
            progress.Report(0.10 + (Math.Clamp(percent, 0, 100) / 100.0 * 0.88));
        }
    }

    [GeneratedRegex(@"progress\s*=\s*(\d{1,3})%")]
    private static partial Regex ProgressPercent();
}
