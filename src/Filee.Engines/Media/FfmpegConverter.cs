// Video and audio conversion with FFmpeg (GPL-3.0 build, an optional download run as a separate program in
// engines/ffmpeg): video ↔ video (H.264, HEVC, AV1, …), video → animated GIF / APNG or a still frame, audio
// extraction, subtitle extraction (the first text track as SRT / ASS / WebVTT), audio ↔ audio and GIF, APNG or a still
// picture → video, and images → HEIC (FFmpeg's HEVC encoder, Filee's own container writer).
// Container and codec choices live in MediaEncoding, the process handling in FfmpegRunner.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Engines.Heic;
using Filee.Engines.Infrastructure;
using Filee.Engines.Magick;

namespace Filee.Engines.Media;

/// <summary>Converts video and audio files with Filee's own copy of FFmpeg.</summary>
public sealed partial class FfmpegConverter : IConverter, IImageTransformer
{
    private static readonly ConcurrentDictionary<string, string> Versions = new();

    private (string Ffmpeg, string Ffprobe)? _tools;

    public string Id => "ffmpeg";
    public string DisplayName => "FFmpeg";

    /// <summary>FFmpeg's encoders use every core themselves; two runs keep the CPU busy between files.</summary>
    public int MaxParallelism => 2;

    /// <summary>Only HEIC, which ImageMagick reads and turns before x265 encodes it (Heic/HeicWriter); never video.</summary>
    public bool Transforms(string from, string to) => to == "heic";

    public IReadOnlyList<ConversionEdge> Edges { get; } = BuildEdges();

    /// <summary>
    /// Every video format (read by FFmpeg's demuxers) → every video target, animated GIF or APNG, a still PNG frame,
    /// every audio target and SRT / ASS / WebVTT (its subtitles); every audio format → every audio target (including
    /// itself: re-encode with another bitrate); GIF, APNG and PNG → MP4, WEBM, MOV; every image ImageMagick reads →
    /// HEIC. Only formats the pinned build can write are targets (see MediaEncoding).
    /// </summary>
    /// <remarks>
    /// The GIF and APNG edges cost more (slow, large), so video → JPG / PDF / TIFF … goes through the still frame
    /// (video → PNG → image engines) and never renders an animation of a whole film just to take one frame of it.
    /// The other way, a photo (HEIC, JPG, …) reaches video as a full-colour PNG: the image engines' still → GIF and
    /// still → APNG edges cost 12 (<see cref="Magick.ImageEncoder.Edge"/>), so 10 + 11 beats 12 + 10 and 12 + 11.
    /// Animated GIF/WebP and PDF or TIFF pages keep the GIF route (10 + 10, APNG costs 10 + 11), which keeps their
    /// frames; an APNG goes straight to video.
    /// </remarks>
    private static List<ConversionEdge> BuildEdges()
    {
        var videos = FormatRegistry.Known.Where(f => f.Category == FormatCategory.Video).Select(f => f.Id).ToList();
        var audios = FormatRegistry.Known.Where(f => f.Category == FormatCategory.Audio).Select(f => f.Id);
        var videoTargets = MediaEncoding.VideoTargets.Keys.Concat(MediaEncoding.AudioTargets.Keys).Append("png").ToList();
        return
        [
            .. from source in videos from target in videoTargets select new ConversionEdge(source, target),
            .. from source in videos from target in new[] { "jpg", "jpeg" } select new ConversionEdge(source, target, 11),
            // Subtitles out of a video. SRT is the hub to the other subtitle formats (the subtitle engine takes it on),
            // so ASS and WebVTT cost a little more and are only taken when they are the target.
            .. from source in videos from target in MediaEncoding.SubtitleTargets.Keys
               select new ConversionEdge(source, target, target == "srt" ? 10 : 11),
            .. videos.Select(source => new ConversionEdge(source, "gif", 15)),
            .. videos.Select(source => new ConversionEdge(source, "apng", 15)),
            .. from source in audios from target in MediaEncoding.AudioTargets.Keys select new ConversionEdge(source, target),
            .. MediaEncoding.PictureToVideoTargets.Select(target => new ConversionEdge("gif", target)),
            .. MediaEncoding.PictureToVideoTargets.Select(target => new ConversionEdge("apng", target, 11)),
            .. MediaEncoding.PictureToVideoTargets.Select(target => new ConversionEdge("png", target, 11)),
            // HEIC: ImageMagick reads, x265 encodes, Filee writes the container (see Heic/HeicWriter).
            .. ImageEncoder.Sources.Select(source => new ConversionEdge(source, "heic")),
        ];
    }

    public EngineStatus GetStatus()
    {
        _tools = Locate();
        if (_tools is not { } tools)
            return EngineStatus.Unavailable("engine.reason.not_installed");
        try
        {
            return EngineStatus.Available(tools.Ffmpeg, $"FFmpeg {VersionOf(tools.Ffmpeg)}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            // Present but broken, e.g. a DLL next to ffmpeg.exe is missing.
            return EngineStatus.Unavailable("engine.reason.error", ex.Message);
        }
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var (ffmpeg, ffprobe) = _tools ?? Locate() ?? throw new InvalidOperationException("FFmpeg was not found.");
        if (step.To == "heic")
            return await HeicWriter.ConvertAsync(ffmpeg, step, progress, cancellationToken);
        var input = Path.GetFullPath(step.InputPath);
        var info = await MediaInfo.ProbeAsync(ffprobe, input, cancellationToken);
        progress?.Report(0.02);

        if (IsVideoFrameSplitting(step, info, out var imageExt))
            return await ExtractFramesAsync(ffmpeg, step, input, info, imageExt, progress, cancellationToken);

        var output = step.Output.Allocate(FormatRegistry.Get(step.To).PrimaryExtension);
        if (output is null)
            return [];

        // A PNG is a still picture (from the image engines), shown for a few seconds.
        var still = step.From == "png";

        // Album art for targets FFmpeg can't put a picture stream into (Ogg, Matroska): prepared on the side.
        var cover = !still && info.CoverStreamIndex is { } coverStream && MediaEncoding.AudioTargets.ContainsKey(step.To)
            ? await CoverArt.PrepareAsync(ffmpeg, input, coverStream, step.To, step.WorkDirectory, cancellationToken)
            : null;

        // Throws for a missing stream before anything is written (an existing file of that name stays untouched).
        var passes = still
            ? MediaEncoding.PlanStill(input, output, step.To, step.Preset.Media, info)
            : MediaEncoding.Plan(input, output, step.To, step.Preset.Media, info, step.WorkDirectory, cover, EncodersOf(ffmpeg), step.Preset.Image.Quality);
        var duration = still ? MediaEncoding.StillDuration : info.Duration;
        try
        {
            var done = 0.0;
            foreach (var pass in passes)
            {
                await RunPassAsync(ffmpeg, pass, duration, progress, done, cancellationToken);
                done += pass.Weight;
            }
            if (!File.Exists(output) || new FileInfo(output).Length == 0)
                throw new InvalidOperationException("FFmpeg did not write any output.");
        }
        catch
        {
            // Never leave a half-written file where the user expects a finished one.
            TryDelete(output);
            throw;
        }

        progress?.Report(1);
        return [output];
    }

    /// <summary>Runs one pass and maps FFmpeg's position (seconds) onto this pass' share of the progress.</summary>
    private static async Task RunPassAsync(string ffmpeg, FfmpegPass pass, TimeSpan? duration, IProgress<double>? progress, double done, CancellationToken cancellationToken)
    {
        var total = duration?.TotalSeconds ?? 0;
        Action<double>? onPosition = progress is null || total <= 0
            ? null
            : seconds => progress.Report(0.02 + 0.97 * (done + pass.Weight * Math.Clamp(seconds / total, 0, 1)));

        ProcessResult result;
        // Without a duration (raw streams) there is nothing to measure against: show an estimate instead.
        using (total <= 0 ? ProgressEstimate.Start(progress, TimeSpan.FromSeconds(20), 0.02 + 0.97 * done, 0.02 + 0.97 * (done + pass.Weight)) : null)
            result = await FfmpegRunner.RunAsync(ffmpeg, pass.Arguments, onPosition, cancellationToken);

        if (result.ExitCode != 0)
        {
            var reason = FfmpegRunner.Summarize(result.StandardError);
            throw new InvalidOperationException($"FFmpeg failed (exit code {result.ExitCode}){(reason.Length > 0 ? ": " + reason : ".")}");
        }
    }

    /// <summary>Both tools must come from the same installation.</summary>
    internal static (string Ffmpeg, string Ffprobe)? Locate()
    {
        var folder = EngineEnvironment.OwnCopyFolder("ffmpeg")
            ?? (EngineEnvironment.FindBundled("ffmpeg") is { } bundled ? EngineEnvironment.FolderWithPrograms("ffmpeg", bundled) : null)
            ?? EngineEnvironment.SystemCopyFolder("ffmpeg");
        return folder is null ? null : (Path.Combine(folder, EngineEnvironment.ProgramName("ffmpeg.exe")),
            Path.Combine(folder, EngineEnvironment.ProgramName("ffprobe.exe")));
    }

    /// <summary>"9.0.2" from <c>ffmpeg -version</c>; cached per executable so re-checking the engines stays cheap.</summary>
    private static string VersionOf(string ffmpeg) =>
        Versions.GetOrAdd($"{ffmpeg}|{File.GetLastWriteTimeUtc(ffmpeg).Ticks}", _ =>
        {
            using var process = Process.Start(new ProcessStartInfo(ffmpeg, "-hide_banner -version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }) ?? throw new InvalidOperationException("FFmpeg could not be started.");
            var firstLine = process.StandardOutput.ReadLine() ?? "";
            process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException("FFmpeg did not start in time.");
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"FFmpeg does not start (exit code {process.ExitCode}).");
            // "ffmpeg version 9.0.2-full_build-www.gyan.dev Copyright …" → "9.0.2"
            var match = VersionPattern().Match(firstLine);
            return match.Success ? match.Groups[1].Value : firstLine.Trim();
        });

    [GeneratedRegex(@"version\s+n?(\d+(?:\.\d+)+|\S+)")]
    private static partial Regex VersionPattern();

    private static readonly ConcurrentDictionary<string, IReadOnlySet<string>?> Encoders = new();

    /// <summary>
    /// The encoders of <paramref name="ffmpeg"/> (<c>ffmpeg -encoders</c>), cached per executable: a copy the user
    /// picked may lack x265 or SVT-AV1. Null (assume Filee's build) when the list can't be read.
    /// </summary>
    private static IReadOnlySet<string>? EncodersOf(string ffmpeg) =>
        Encoders.GetOrAdd($"{ffmpeg}|{File.GetLastWriteTimeUtc(ffmpeg).Ticks}", _ =>
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(ffmpeg, "-hide_banner -encoders")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                if (process is null)
                    return null;
                process.ErrorDataReceived += (_, _) => { };
                process.BeginErrorReadLine();
                var list = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(10_000) || process.ExitCode != 0)
                    return null;
                // " V....D libx265              libx265 H.265 / HEVC (codec hevc)"
                return list.Split('\n')
                    .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .Where(parts => parts.Length >= 2 && parts[0].Length == 6 && parts[0][0] is 'V' or 'A' or 'S')
                    .Select(parts => parts[1])
                    .ToHashSet(StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                return null;
            }
        });

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv", ".mpg", ".mpeg",
        ".ts", ".m2ts", ".3gp", ".3g2", ".ogv", ".vob", ".dv", ".mxf"
    };

    private static bool IsVideoFrameSplitting(ConversionStep step, MediaInfo info, out string imageExt)
    {
        imageExt = "";
        if (!step.Preset.Media.SaveEveryFrame)
            return false;

        var isSourceVideo = (FormatRegistry.FindById(step.From)?.Category == FormatCategory.Video)
            || (FormatRegistry.Detect(step.InputPath)?.Category == FormatCategory.Video)
            || VideoExtensions.Contains(Path.GetExtension(step.InputPath))
            || (info.Video is not null && step.From is not "png" and not "gif");

        if (!isSourceVideo)
            return false;

        var targetExt = step.To.TrimStart('.').ToLowerInvariant();
        if (targetExt is "jpg" or "jpeg")
        {
            imageExt = "jpg";
            return true;
        }
        if (targetExt is "png")
        {
            imageExt = "png";
            return true;
        }

        var presetTarget = step.Preset.TargetFormat.TrimStart('.').ToLowerInvariant();
        if (presetTarget is "jpg" or "jpeg")
        {
            imageExt = "jpg";
            return true;
        }
        if (presetTarget is "png")
        {
            imageExt = "png";
            return true;
        }

        return false;
    }

    private static async Task<IReadOnlyList<string>> ExtractFramesAsync(
        string ffmpeg,
        ConversionStep step,
        string input,
        MediaInfo info,
        string imageExt,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var allocated = step.Output.Allocate(imageExt);
        if (allocated is null)
            return [];

        var outputDirectory = Path.GetDirectoryName(allocated)!;
        var videoFilename = Path.GetFileNameWithoutExtension(allocated);
        if (string.IsNullOrEmpty(videoFilename))
            videoFilename = FormatRegistry.NameWithoutExtension(step.InputPath);

        var framesSubfolder = Path.Combine(outputDirectory, $"{videoFilename}_frames");
        for (var n = 2; Directory.Exists(framesSubfolder); n++)
            framesSubfolder = Path.Combine(outputDirectory, $"{videoFilename}_frames ({n})");

        Directory.CreateDirectory(framesSubfolder);

        var outputPattern = Path.Combine(framesSubfolder, $"frame_%06d.{imageExt}");
        var args = MediaEncoding.FrameSequence(
            input,
            outputPattern,
            imageExt,
            info.Video,
            step.Preset.Image.Quality,
            step.Preset.Media.FrameInterval,
            step.Preset.Media.FramesPerSecond);

        var pass = new FfmpegPass(args, 1);

        try
        {
            await RunPassAsync(ffmpeg, pass, info.Duration, progress, done: 0, cancellationToken);

            if (!Directory.Exists(framesSubfolder) || !Directory.EnumerateFiles(framesSubfolder).Any())
                throw new InvalidOperationException("FFmpeg did not write any frames.");
        }
        catch
        {
            TryDeleteDirectory(framesSubfolder);
            throw;
        }

        progress?.Report(1);
        return [framesSubfolder];
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
