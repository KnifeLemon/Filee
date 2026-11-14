using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services.Capture;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Engines.Media;

namespace Filee.App.ViewModels.Capture;

public sealed record LanguageChoice(string Code, string Name);

public sealed partial class CaptureStudioEditorViewModel : ObservableObject
{
    private readonly CaptureStudioService _service;
    private readonly ILocalizer _loc;

    [ObservableProperty]
    private string _videoPath = "";

    [ObservableProperty]
    private TimeSpan _videoDuration = TimeSpan.Zero;

    [ObservableProperty]
    private string _videoDurationText = "00:00";

    [ObservableProperty]
    private double _currentPosition = 0;

    [ObservableProperty]
    private string _currentPositionText = "00:00";

    [ObservableProperty]
    private double _trimStart = 0;

    [ObservableProperty]
    private double _trimEnd = 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayButtonIcon))]
    private bool _isPlaying = false;

    public string PlayButtonIcon => IsPlaying ? "⏸" : "▶";

    [ObservableProperty]
    private bool _isGeneratingCaptions = false;

    [ObservableProperty]
    private bool _isTranslatingCaptions = false;

    [ObservableProperty]
    private bool _isExporting = false;

    [ObservableProperty]
    private double _exportProgress = 0;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private Bitmap? _currentFrameBitmap;

    [ObservableProperty]
    private string? _activeSubtitleText;

    [ObservableProperty]
    private SubtitleCueModel? _selectedSubtitle;

    [ObservableProperty]
    private LanguageChoice? _selectedLanguage;

    [ObservableProperty]
    private string _selectedExportFormat = "MP4";

    public ObservableCollection<SubtitleCueModel> Subtitles { get; } = [];
    public ObservableCollection<LanguageChoice> AvailableLanguages { get; } = [];
    public ObservableCollection<string> ExportFormats { get; } = ["MP4", "GIF", "WebM"];

    public Func<string, Task<string?>>? RequestSaveFileHandler { get; set; }

    private readonly WriteableBitmap?[] _playFrames = new WriteableBitmap?[2];
    private readonly object _playFramesLock = new();
    private int _playFrame;
    private CancellationTokenSource? _playCts;
    private CancellationTokenSource? _scrubCts;
    private bool _isUpdatingFromPlayback;
    private readonly DispatcherTimer _scrubDebounceTimer;

    public CaptureStudioEditorViewModel(CaptureStudioService service, ILocalizer loc, RecordedVideoMetadata metadata)
    {
        _service = service;
        _loc = loc;
        _videoPath = metadata.VideoPath;
        _videoDuration = metadata.Duration;
        _videoDurationText = FormatTime(metadata.Duration);
        _trimStart = 0;
        _trimEnd = metadata.Duration.TotalSeconds;

        _scrubDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(75) };
        _scrubDebounceTimer.Tick += (_, _) =>
        {
            _scrubDebounceTimer.Stop();
            if (!_isPlaying && !_isUpdatingFromPlayback)
            {
                _ = ExtractFrameAtAsync(CurrentPosition);
            }
        };

        LoadLanguages();

        _ = ExtractFrameAtAsync(0.1);
    }

    private void LoadLanguages()
    {
        AvailableLanguages.Add(new LanguageChoice("es", "Spanish (Español)"));
        AvailableLanguages.Add(new LanguageChoice("fr", "French (Français)"));
        AvailableLanguages.Add(new LanguageChoice("de", "German (Deutsch)"));
        AvailableLanguages.Add(new LanguageChoice("ja", "Japanese (日本語)"));
        AvailableLanguages.Add(new LanguageChoice("ko", "Korean (한국어)"));
        AvailableLanguages.Add(new LanguageChoice("zh", "Chinese (中文)"));
        AvailableLanguages.Add(new LanguageChoice("hi", "Hindi (हिन्दी)"));
        AvailableLanguages.Add(new LanguageChoice("pt", "Portuguese (Português)"));
        AvailableLanguages.Add(new LanguageChoice("it", "Italian (Italiano)"));
        AvailableLanguages.Add(new LanguageChoice("ru", "Russian (Русский)"));
        AvailableLanguages.Add(new LanguageChoice("ar", "Arabic (العربية)"));
        AvailableLanguages.Add(new LanguageChoice("en", "English"));

        SelectedLanguage = AvailableLanguages.FirstOrDefault();
    }

    partial void OnCurrentPositionChanged(double value)
    {
        CurrentPositionText = FormatTime(TimeSpan.FromSeconds(value));
        UpdateActiveSubtitle(value);
        if (!_isPlaying && !_isUpdatingFromPlayback)
        {
            _scrubDebounceTimer.Stop();
            _scrubDebounceTimer.Start();
        }
    }

    partial void OnTrimStartChanged(double value)
    {
        if (value > TrimEnd)
        {
            TrimEnd = Math.Min(VideoDuration.TotalSeconds, value + 0.5);
        }
    }

    partial void OnTrimEndChanged(double value)
    {
        if (value < TrimStart)
        {
            TrimStart = Math.Max(0, value - 0.5);
        }
    }

    private void UpdateActiveSubtitle(double seconds)
    {
        var cue = Subtitles.FirstOrDefault(s => seconds >= s.Start.TotalSeconds && seconds <= s.End.TotalSeconds);
        ActiveSubtitleText = cue != null
            ? (!string.IsNullOrWhiteSpace(cue.TranslatedText) ? cue.TranslatedText : cue.Text)
            : null;
    }

    private async Task ExtractFrameAtAsync(double seconds)
    {
        _scrubCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scrubCts = cts;

        var targetSec = Math.Clamp(seconds, 0, VideoDuration.TotalSeconds);
        try
        {
            var temp = Path.Combine(Path.GetTempPath(), $"scrub_{Guid.NewGuid():N}.jpg");
            var ok = await VideoFrames.WriteFrameAsync(VideoPath, targetSec, temp, 480, cts.Token).ConfigureAwait(false);
            if (ok && File.Exists(temp) && !cts.IsCancellationRequested)
            {
                using var s = File.OpenRead(temp);
                var bmp = new Bitmap(s);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!cts.IsCancellationRequested)
                    {
                        var old = CurrentFrameBitmap;
                        CurrentFrameBitmap = bmp;
                        old?.Dispose();
                    }
                    else
                    {
                        bmp.Dispose();
                    }
                });
                try { File.Delete(temp); } catch { }
            }
        }
        catch { }
    }

    [RelayCommand]
    private void TogglePlay()
    {
        if (IsPlaying)
        {
            StopPlayback();
        }
        else
        {
            StartPlayback();
        }
    }

    private void StartPlayback()
    {
        _playCts?.Cancel();
        var cts = new CancellationTokenSource();
        _playCts = cts;
        IsPlaying = true;

        _ = Task.Run(async () =>
        {
            try
            {
                var startSec = CurrentPosition >= TrimEnd ? TrimStart : CurrentPosition;
                var edit = ClipEdit.Of(new ClipRange(startSec, TrimEnd));
                await ClipPreview.PlayAsync(VideoPath, edit, 360, OnPlayFrame, cts.Token).ConfigureAwait(false);
            }
            catch { }
            finally
            {
                if (ReferenceEquals(_playCts, cts))
                {
                    _playCts = null;
                    Dispatcher.UIThread.Post(() => IsPlaying = false);
                }
            }
        }, cts.Token);
    }

    private void StopPlayback()
    {
        _playCts?.Cancel();
        _playCts = null;
        IsPlaying = false;
    }

    private void OnPlayFrame(byte[] bgra, int width, int height, double sourceTime)
    {
        WriteableBitmap bitmap;
        lock (_playFramesLock)
        {
            _playFrame = 1 - _playFrame;
            bitmap = _playFrames[_playFrame] is { } existing && existing.PixelSize == new PixelSize(width, height)
                ? existing
                : _playFrames[_playFrame] = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using var buffer = bitmap.Lock();
            Marshal.Copy(bgra, 0, buffer.Address, bgra.Length);
        }

        Dispatcher.UIThread.Post(() =>
        {
            CurrentFrameBitmap = bitmap;
            _isUpdatingFromPlayback = true;
            try
            {
                CurrentPosition = sourceTime;
            }
            finally
            {
                _isUpdatingFromPlayback = false;
            }
        });
    }

    [RelayCommand]
    private void SplitAtPlayhead()
    {
        if (CurrentPosition > TrimStart && CurrentPosition < TrimEnd)
        {
            TrimEnd = CurrentPosition;
            StatusMessage = $"Segment trimmed to {FormatTime(TimeSpan.FromSeconds(CurrentPosition))}";
        }
    }

    [RelayCommand]
    private async Task GenerateCaptions()
    {
        if (IsGeneratingCaptions || string.IsNullOrEmpty(VideoPath))
            return;

        IsGeneratingCaptions = true;
        StatusMessage = "Analyzing audio and generating captions with Whisper…";

        try
        {
            var cues = await _service.GenerateCaptionsAsync(VideoPath).ConfigureAwait(true);
            Subtitles.Clear();
            foreach (var cue in cues)
                Subtitles.Add(cue);

            StatusMessage = cues.Count > 0
                ? $"Successfully generated {cues.Count} captions!"
                : "No speech detected or Whisper model not installed yet.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Caption generation error: {ex.Message}";
        }
        finally
        {
            IsGeneratingCaptions = false;
        }
    }

    [RelayCommand]
    private async Task TranslateCaptions()
    {
        if (IsTranslatingCaptions || Subtitles.Count == 0 || SelectedLanguage is null)
            return;

        IsTranslatingCaptions = true;
        StatusMessage = $"Translating captions to {SelectedLanguage.Name}…";

        try
        {
            var cuesList = Subtitles.ToList();
            var translated = await _service.TranslateCaptionsAsync(cuesList, SelectedLanguage.Code).ConfigureAwait(true);

            Subtitles.Clear();
            foreach (var t in translated)
                Subtitles.Add(t);

            StatusMessage = $"Translated {translated.Count} subtitles to {SelectedLanguage.Name}!";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Translation error: {ex.Message}";
        }
        finally
        {
            IsTranslatingCaptions = false;
        }
    }

    [RelayCommand]
    private void AddSubtitle()
    {
        var pos = TimeSpan.FromSeconds(CurrentPosition);
        var end = pos + TimeSpan.FromSeconds(3);
        if (end > VideoDuration) end = VideoDuration;

        var cue = new SubtitleCueModel
        {
            Index = Subtitles.Count + 1,
            Start = pos,
            End = end,
            Text = "New caption"
        };
        Subtitles.Add(cue);
        SelectedSubtitle = cue;
    }

    [RelayCommand]
    private void DeleteSubtitle()
    {
        if (SelectedSubtitle != null)
        {
            Subtitles.Remove(SelectedSubtitle);
            SelectedSubtitle = Subtitles.FirstOrDefault();
        }
    }

    [RelayCommand]
    private async Task ExportVideo()
    {
        if (IsExporting || string.IsNullOrEmpty(VideoPath))
            return;

        StopPlayback();

        var ext = SelectedExportFormat.ToLowerInvariant();
        var defaultFileName = $"Capture_{DateTime.Now:yyyyMMdd_HHmmss}.{ext}";

        string? destPath = null;
        if (RequestSaveFileHandler != null)
        {
            destPath = await RequestSaveFileHandler(defaultFileName).ConfigureAwait(true);
        }

        if (string.IsNullOrWhiteSpace(destPath))
            return;

        IsExporting = true;
        ExportProgress = 0;
        StatusMessage = $"Exporting video as {SelectedExportFormat}…";

        try
        {
            var tStart = TimeSpan.FromSeconds(TrimStart);
            var tEnd = TimeSpan.FromSeconds(TrimEnd);
            var progress = new Progress<double>(p => ExportProgress = p);

            var success = await _service.ExportVideoAsync(
                VideoPath,
                destPath,
                tStart,
                tEnd,
                Subtitles.ToList(),
                ext,
                progress).ConfigureAwait(true);

            StatusMessage = success
                ? $"Video exported successfully to {Path.GetFileName(destPath)}!"
                : "Export failed. Please check parameters.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export error: {ex.Message}";
        }
        finally
        {
            IsExporting = false;
        }
    }

    private static string FormatTime(TimeSpan ts)
    {
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
