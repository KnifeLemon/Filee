using System.Diagnostics;
using System.Globalization;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Media;
using Filee.Engines.Subtitles;
using Filee.Engines.Translation;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services.Capture;

public sealed class CaptureStudioService
{
    private readonly ILogger<CaptureStudioService>? _log;
    private readonly WindowTargetService _targetService;
    private readonly CaptureRecordingEngine _engine;
    private readonly OnePhoneMirrorService _onePhoneMirror;

    public WindowTargetService TargetService => _targetService;
    public CaptureRecordingEngine RecordingEngine => _engine;
    public OnePhoneMirrorService OnePhoneMirror => _onePhoneMirror;

    public CaptureConfig CurrentConfig { get; set; } = new();
    public RecordedVideoMetadata? LastRecordedVideo { get; set; }

    public CaptureStudioService(
        WindowTargetService targetService,
        CaptureRecordingEngine engine,
        OnePhoneMirrorService onePhoneMirror,
        ILogger<CaptureStudioService>? log = null)
    {
        _targetService = targetService;
        _engine = engine;
        _onePhoneMirror = onePhoneMirror;
        _log = log;
    }

    public async Task<bool> StartRecordingAsync(CaptureConfig config)
    {
        CurrentConfig = config;
        return await _engine.StartRecordingAsync(config).ConfigureAwait(false);
    }

    public Task PauseRecordingAsync() => _engine.PauseAsync();

    public Task ResumeRecordingAsync() => _engine.ResumeAsync();

    public async Task<RecordedVideoMetadata?> StopRecordingAsync()
    {
        var meta = await _engine.StopAsync().ConfigureAwait(false);
        LastRecordedVideo = meta;
        return meta;
    }

    public async Task<IReadOnlyList<SubtitleCueModel>> GenerateCaptionsAsync(
        string videoPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var list = new List<SubtitleCueModel>();
        var (cli, model) = WhisperConverter.LocateTools();
        if (cli is null || model is null)
        {
            _log?.LogWarning("Whisper engine or model is not installed");
            return list;
        }

        var workDir = Path.Combine(Path.GetTempPath(), "FileeCapture_Whisper_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        try
        {
            var whisper = new WhisperConverter();
            var preset = new Preset { Id = "temp_whisper", TargetFormat = "srt" };
            var outSrt = Path.Combine(workDir, "captions.srt");
            var output = new ExactOutput(outSrt);
            var step = new ConversionStep(videoPath, "mp4", "srt", preset, output, workDir);

            var generated = await whisper.ConvertAsync(step, progress, cancellationToken).ConfigureAwait(false);
            if (generated.Count > 0 && File.Exists(generated[0]))
            {
                var srtBytes = await File.ReadAllBytesAsync(generated[0], cancellationToken).ConfigureAwait(false);
                var doc = SubtitleReader.Read("srt", srtBytes);

                int index = 1;
                foreach (var cue in doc.Cues)
                {
                    list.Add(new SubtitleCueModel
                    {
                        Index = index++,
                        Start = cue.Start,
                        End = cue.End,
                        Text = cue.Text
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Whisper caption extraction failed");
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }

        return list;
    }

    public async Task<IReadOnlyList<SubtitleCueModel>> TranslateCaptionsAsync(
        IReadOnlyList<SubtitleCueModel> cues,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var results = new List<SubtitleCueModel>();
        if (cues.Count == 0)
            return results;

        try
        {
            var options = new TranslateOptions { TargetLanguage = targetLanguage };
            var translator = new TranslationService(options);

            var texts = cues.Select(c => c.Text).ToList();
            var translated = await translator.TranslateBatchAsync(texts, targetLanguage, "auto", cancellationToken).ConfigureAwait(false);

            for (int i = 0; i < cues.Count; i++)
            {
                var item = new SubtitleCueModel
                {
                    Index = cues[i].Index,
                    Start = cues[i].Start,
                    End = cues[i].End,
                    Text = cues[i].Text,
                    TranslatedText = i < translated.Count ? translated[i] : cues[i].Text
                };
                results.Add(item);
            }
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Failed to translate subtitle cues");
            foreach (var cue in cues)
            {
                results.Add(new SubtitleCueModel
                {
                    Index = cue.Index,
                    Start = cue.Start,
                    End = cue.End,
                    Text = cue.Text,
                    TranslatedText = cue.Text
                });
            }
        }

        return results;
    }

    public async Task<bool> ExportVideoAsync(
        string sourceVideo,
        string destinationVideo,
        TimeSpan trimStart,
        TimeSpan trimEnd,
        IReadOnlyList<SubtitleCueModel>? subtitles,
        string format,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var ffmpeg = CaptureRecordingEngine.FindFfmpeg();
        if (string.IsNullOrWhiteSpace(ffmpeg))
            throw new InvalidOperationException("FFmpeg not found.");

        double exportDurationSec = trimEnd > trimStart ? (trimEnd - trimStart).TotalSeconds : 0;

        string? srtPath = null;
        if (subtitles != null && subtitles.Count > 0)
        {
            srtPath = Path.Combine(Path.GetTempPath(), $"subs_{Guid.NewGuid():N}.srt");
            var sb = new StringBuilder();
            int idx = 1;
            foreach (var sub in subtitles)
            {
                var text = !string.IsNullOrWhiteSpace(sub.TranslatedText) ? sub.TranslatedText : sub.Text;
                sb.AppendLine(idx.ToString());
                sb.AppendLine($"{FormatSrtTime(sub.Start)} --> {FormatSrtTime(sub.End)}");
                sb.AppendLine(text);
                sb.AppendLine();
                idx++;
            }
            await File.WriteAllTextAsync(srtPath, sb.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var psi = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };

            psi.ArgumentList.Add("-y");

            if (trimStart > TimeSpan.Zero)
            {
                psi.ArgumentList.Add("-ss");
                psi.ArgumentList.Add(trimStart.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            }

            if (trimEnd > trimStart)
            {
                psi.ArgumentList.Add("-to");
                psi.ArgumentList.Add(trimEnd.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            }

            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(sourceVideo);

            if (format.Equals("gif", StringComparison.OrdinalIgnoreCase))
            {
                psi.ArgumentList.Add("-vf");
                psi.ArgumentList.Add("fps=15,scale=720:-1:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse");
                psi.ArgumentList.Add("-threads");
                psi.ArgumentList.Add("0");
            }
            else if (format.Equals("webm", StringComparison.OrdinalIgnoreCase))
            {
                if (srtPath != null)
                {
                    var escaped = srtPath.Replace("\\", "/").Replace(":", "\\:");
                    psi.ArgumentList.Add("-vf");
                    psi.ArgumentList.Add($"subtitles='{escaped}'");
                }
                psi.ArgumentList.Add("-c:v");
                psi.ArgumentList.Add("libvpx-vp9");
                psi.ArgumentList.Add("-crf");
                psi.ArgumentList.Add("32");
                psi.ArgumentList.Add("-b:v");
                psi.ArgumentList.Add("0");
                psi.ArgumentList.Add("-threads");
                psi.ArgumentList.Add("0");
            }
            else
            {
                // Fast MP4 export: ultra/veryfast preset, all CPU threads
                if (srtPath != null)
                {
                    var escaped = srtPath.Replace("\\", "/").Replace(":", "\\:");
                    psi.ArgumentList.Add("-vf");
                    psi.ArgumentList.Add($"subtitles='{escaped}'");
                }

                psi.ArgumentList.Add("-c:v");
                psi.ArgumentList.Add("libx264");
                psi.ArgumentList.Add("-preset");
                psi.ArgumentList.Add("veryfast");
                psi.ArgumentList.Add("-crf");
                psi.ArgumentList.Add("22");
                psi.ArgumentList.Add("-threads");
                psi.ArgumentList.Add("0");
                psi.ArgumentList.Add("-c:a");
                psi.ArgumentList.Add("aac");
                psi.ArgumentList.Add("-b:a");
                psi.ArgumentList.Add("192k");
                psi.ArgumentList.Add("-movflags");
                psi.ArgumentList.Add("+faststart");
            }

            psi.ArgumentList.Add(destinationVideo);

            using var proc = Process.Start(psi);
            if (proc is null)
                return false;

            progress?.Report(0.0);

            var stderrReadTask = Task.Run(async () =>
            {
                try
                {
                    using var reader = proc.StandardError;
                    string? line;
                    while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(line, @"time=(\d+):(\d+):(\d+\.\d+)");
                        if (match.Success && exportDurationSec > 0)
                        {
                            var h = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                            var m = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                            var s = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                            var curSec = h * 3600 + m * 60 + s;
                            var pct = Math.Clamp((curSec / exportDurationSec) * 100.0, 0, 99.0);
                            progress?.Report(pct);
                        }
                    }
                }
                catch { }
            }, cancellationToken);

            await Task.WhenAll(proc.WaitForExitAsync(cancellationToken), stderrReadTask).ConfigureAwait(false);

            if (proc.ExitCode == 0)
            {
                progress?.Report(100.0);
            }
            return proc.ExitCode == 0;
        }
        finally
        {
            if (srtPath != null && File.Exists(srtPath))
            {
                try { File.Delete(srtPath); } catch { }
            }
        }
    }

    private static string FormatSrtTime(TimeSpan ts)
    {
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";
    }

    private sealed class ExactOutput(string path) : IOutputAllocator
    {
        public string? Allocate(string extension, string? suffix = null) => path;
    }
}
