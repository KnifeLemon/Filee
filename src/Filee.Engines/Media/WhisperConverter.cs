// Transcribes speech in video and audio using OpenAI Whisper (whisper.cpp) into SRT subtitles.
// Supports any language with auto-detection and Hindi/Devanagari script restoration.

using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Engines.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.Engines.Media;

/// <summary>
/// AI-powered speech transcription and captioning engine powered by Whisper.
/// Directly outputs .srt subtitles for both video and audio files.
/// </summary>
public sealed class WhisperConverter : IConverter
{
    private readonly ILogger _logger;

    public WhisperConverter(ILogger<WhisperConverter>? logger = null)
    {
        _logger = logger ?? NullLogger<WhisperConverter>.Instance;
    }

    public string Id => "whisper";
    public string DisplayName => "Whisper (AI Captions)";
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
            // Audio -> SRT fallback (without explicit Caption flag)
            .. audios.Select(a => new ConversionEdge(a, "srt", Cost: 12, NotForImageSources: true)),
            // Video -> MP4 with captions burned in (legacy fallback if target is mp4)
            .. videos.Select(v => new ConversionEdge(v, "mp4", Cost: 14, Caption: true)),
        ];
    }

    public EngineStatus GetStatus()
    {
        var (cli, model) = LocateTools();
        if (cli is null || model is null)
            return EngineStatus.Unavailable("engine.reason.not_installed");

        var modelName = Path.GetFileNameWithoutExtension(model);
        return EngineStatus.Available(cli, $"Whisper ({modelName})");
    }

    public static (string? Cli, string? Model) LocateTools()
    {
        var cli = LocateCli();
        var model = LocateModel();
        return (cli, model);
    }

    private static string? LocateCli()
    {
        // 1. Filee bundled / downloaded engine folder
        if (EngineEnvironment.FindBundled("whisper") is { } bundled)
        {
            var exeName = OperatingSystem.IsWindows() ? "whisper-cli.exe" : "whisper-cli";
            var direct = Path.Combine(bundled, exeName);
            if (File.Exists(direct))
                return direct;
            var subRelease = Path.Combine(bundled, "Release", exeName);
            if (File.Exists(subRelease))
                return subRelease;
        }

        // 2. User's own copy from Settings -> Engines
        if (EngineEnvironment.OwnCopyFolder("whisper") is { } own)
        {
            var exeName = OperatingSystem.IsWindows() ? "whisper-cli.exe" : "whisper-cli";
            var path = Path.Combine(own, exeName);
            if (File.Exists(path))
                return path;
        }

        // 3. Companion installation from ChiCaption if present
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            Path.Combine("D:", "ChiCaption", "ChiCaption", "binaries", "whisper-cli.exe"),
            Path.Combine(localAppData, "ChiCaption", "_up_", "binaries", "whisper-cli.exe"),
            Path.Combine(localAppData, "ChiCaption", "binaries", "whisper-cli.exe"),
        ];
        foreach (var c in candidates)
        {
            if (File.Exists(c))
                return c;
        }

        // 4. PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir, OperatingSystem.IsWindows() ? "whisper-cli.exe" : "whisper-cli");
            if (File.Exists(p))
                return p;
        }

        return null;
    }

    private static string? LocateModel()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var searchDirs = new List<string>();

        if (EngineEnvironment.FindBundled("whisper") is { } wDir)
            searchDirs.Add(wDir);
        if (EngineEnvironment.FindBundled("whisper-model") is { } wmDir)
            searchDirs.Add(wmDir);

        searchDirs.Add(Path.Combine(localAppData, "ChiCaption", "models"));
        searchDirs.Add(Path.Combine("D:", "ChiCaption", "ChiCaption", "models"));
        searchDirs.Add(Path.Combine(localAppData, "Filee", "models"));
        searchDirs.Add(Path.Combine(AppContext.BaseDirectory, "models"));

        // Priority order for models
        string[] preferredModels = ["ggml-large-v3-turbo.bin", "ggml-base.bin", "ggml-small.bin", "ggml-tiny.bin"];

        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir))
                continue;

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

    private static string LocateFfmpeg()
    {
        if (FfmpegConverter.Locate() is { } tools && File.Exists(tools.Ffmpeg))
            return tools.Ffmpeg;

        if (EngineEnvironment.FindBundled("ffmpeg") is { } bundled)
        {
            var exe = Path.Combine(bundled, OperatingSystem.IsWindows() ? "bin\\ffmpeg.exe" : "bin/ffmpeg");
            if (File.Exists(exe))
                return exe;
            exe = Path.Combine(bundled, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            if (File.Exists(exe))
                return exe;
        }

        return "ffmpeg";
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var (whisperCli, modelPath) = LocateTools();
        if (whisperCli is null || modelPath is null)
            throw new InvalidOperationException("Whisper engine or model was not found. Please download Whisper in Settings > Engines.");

        var ffmpeg = LocateFfmpeg();
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(0.05);

        // 1. Extract 16kHz mono WAV for Whisper inference (works for both video and audio files)
        var audioWav = Path.Combine(step.WorkDirectory, $"whisper_audio_{Guid.NewGuid():N}.wav");
        var extractArgs = new[]
        {
            "-y", "-nostdin", "-hide_banner", "-v", "error",
            "-i", step.InputPath,
            "-vn", "-ar", "16000", "-ac", "1",
            audioWav
        };

        var extractResult = await ProcessRunner.RunAsync(ffmpeg, extractArgs, TimeSpan.FromMinutes(10), cancellationToken);
        if (extractResult.ExitCode != 0 || !File.Exists(audioWav))
            throw new InvalidOperationException($"FFmpeg failed to extract audio: {extractResult.StandardError}");

        progress?.Report(0.20);
        cancellationToken.ThrowIfCancellationRequested();

        // 2. Run Whisper CLI transcription (auto-detect language, word-split, srt output)
        var outPrefix = Path.Combine(step.WorkDirectory, $"whisper_out_{Guid.NewGuid():N}");
        var whisperArgs = new[]
        {
            "-m", modelPath,
            "-f", audioWav,
            "-osrt",
            "-of", outPrefix,
            "-l", "auto",
            "-t", "4",
            "-sow"
        };

        var whisperResult = await ProcessRunner.RunAsync(whisperCli, whisperArgs, TimeSpan.FromMinutes(30), cancellationToken);
        var srtPath = outPrefix + ".srt";

        string srtContent = string.Empty;
        if (File.Exists(srtPath))
        {
            srtContent = await File.ReadAllTextAsync(srtPath, cancellationToken);
        }

        // Clean Hindi / Urdu transcription: if Urdu Perso-Arabic characters were generated,
        // convert them to natural Hindi Devanagari script.
        if (UrduToDevanagari.ContainsUrdu(srtContent))
        {
            srtContent = UrduToDevanagari.ConvertSrt(srtContent);
            await File.WriteAllTextAsync(srtPath, srtContent, Encoding.UTF8, cancellationToken);
        }

        progress?.Report(0.70);
        cancellationToken.ThrowIfCancellationRequested();

        // 3. Produce output: if target is SRT (default for caption), output the SRT file directly!
        if (step.To == "srt")
        {
            var srtOutput = step.Output.Allocate("srt");
            if (srtOutput is null)
                return [];
            await File.WriteAllTextAsync(srtOutput, srtContent, Encoding.UTF8, cancellationToken);
            progress?.Report(1.0);
            return [srtOutput];
        }

        // Fallback for custom presets targeting video output with burned subtitles:
        var outputExt = FormatRegistry.Get(step.To).PrimaryExtension;
        var videoOutput = step.Output.Allocate(outputExt);
        if (videoOutput is null)
            return [];

        // Escape path for FFmpeg subtitles filter on Windows (escaped colons and backslashes)
        var escapedSrt = srtPath.Replace('\\', '/').Replace(":", "\\:");

        var vf = $"subtitles='{escapedSrt}':force_style='Fontname=Impact,Fontsize=28,PrimaryColour=&H0000FFFF,OutlineColour=&H00000000,BorderStyle=1,Outline=2.5,Shadow=1.5,Bold=1,Alignment=2,MarginV=35'";

        var burnArgs = new[]
        {
            "-y", "-nostdin", "-hide_banner", "-v", "error",
            "-i", step.InputPath,
            "-vf", vf,
            "-c:a", "copy",
            videoOutput
        };

        var burnResult = await ProcessRunner.RunAsync(ffmpeg, burnArgs, TimeSpan.FromMinutes(45), cancellationToken);
        if (burnResult.ExitCode != 0 || !File.Exists(videoOutput))
            throw new InvalidOperationException($"FFmpeg failed to burn captions into video: {burnResult.StandardError}");

        progress?.Report(1.0);
        return [videoOutput];
    }
}
