// State of the "Cut a clip" window: one video at a time on a timeline of small pictures, the start and end of the
// clip, the large picture of the frame at the handle being moved, and the conversion that writes the clip.

using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines.Media;
using Microsoft.Extensions.Logging;

namespace Filee.App.ViewModels;

/// <summary>The two ends of the clip on the timeline.</summary>
public enum ClipHandle
{
    Start,
    End,
}

public sealed partial class ClipWindowViewModel : ObservableObject, IDisposable
{
    /// <summary>Pictures on the timeline.</summary>
    public const int ThumbnailCount = 16;

    /// <summary>Height of a timeline picture (twice its size on screen, for high-DPI displays).</summary>
    private const int ThumbnailHeight = 96;

    /// <summary>Height of the large picture of the frame at the handle.</summary>
    private const int PreviewHeight = 720;

    /// <summary>FFmpeg runs at the same time while the timeline fills: one per picture would starve the window.</summary>
    private const int ParallelThumbnails = 3;

    /// <summary>How long a handle rests before its exact frame is taken (the nearest timeline picture shows meanwhile).</summary>
    private static readonly TimeSpan PreviewDelay = TimeSpan.FromMilliseconds(90);

    /// <summary>The clip a video starts with: its first this many seconds, or all of a shorter one.</summary>
    public const double FirstClipLength = 30;

    /// <summary>The quick lengths, in seconds from the start handle.</summary>
    public static IReadOnlyList<double> QuickLengths { get; } = [15, 30, 60];

    private readonly IReadOnlyList<string> _files;
    private readonly Preset _tool;
    private readonly ILocalizer _loc;
    private readonly UserDataStore _store;
    private readonly IVideoFrameSource _frames;
    private readonly Func<string, string> _targetFor;
    private readonly Action<string, Preset> _export;
    private readonly ILogger? _log;
    private readonly string _workFolder;

    private CancellationTokenSource _fileCts = new();
    private CancellationTokenSource? _previewCts;
    private VideoSummary? _video;
    private Bitmap? _ownPreview;   // a frame taken for the large picture (not one of the timeline's), disposed with it
    private int _previewNumber;
    private bool _closed;

    [ObservableProperty] private int _currentIndex;
    [ObservableProperty] private string _currentFileName = "";
    [ObservableProperty] private string _counterText = "";
    [ObservableProperty] private bool _hasMultipleFiles;
    [ObservableProperty] private string _saveText = "";

    /// <summary>Reading the video (its length): the timeline and the buttons wait.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    [NotifyPropertyChangedFor(nameof(Can15))]
    [NotifyPropertyChangedFor(nameof(Can30))]
    [NotifyPropertyChangedFor(nameof(Can60))]
    private double _duration;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartText))]
    [NotifyPropertyChangedFor(nameof(LengthText))]
    [NotifyPropertyChangedFor(nameof(PreviewTime))]
    [NotifyPropertyChangedFor(nameof(PreviewLabel))]
    private double _start;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndText))]
    [NotifyPropertyChangedFor(nameof(LengthText))]
    [NotifyPropertyChangedFor(nameof(PreviewTime))]
    [NotifyPropertyChangedFor(nameof(PreviewLabel))]
    private double _end;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStartActive))]
    [NotifyPropertyChangedFor(nameof(IsEndActive))]
    [NotifyPropertyChangedFor(nameof(PreviewTime))]
    [NotifyPropertyChangedFor(nameof(PreviewLabel))]
    private ClipHandle _activeHandle;

    /// <summary>The large picture: the frame at the active handle (its nearest timeline picture until that is taken).</summary>
    [ObservableProperty] private Bitmap? _preview;

    /// <summary>Copy the streams (fast, cut at key frames) rather than encode the clip exactly. Remembered in the tool.</summary>
    [ObservableProperty] private bool _isFast = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusNotice))]
    private string _statusNotice = "";

    /// <summary>Every file is done and some could not be opened: the window stays on <see cref="Problems"/> until closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isFinished;

    [ObservableProperty] private string _summary = "";

    /// <summary>The timeline's pictures, in order; null until taken (or when FFmpeg found none).</summary>
    public ObservableCollection<Bitmap?> Thumbnails { get; } = [];

    /// <summary>"file: reason" for each file that could not be opened.</summary>
    public ObservableCollection<string> Problems { get; } = [];

    public bool HasStatusNotice => !string.IsNullOrWhiteSpace(StatusNotice);
    public bool IsEditing => !IsFinished;
    public bool CanEdit => !IsFinished && !IsLoading;
    public bool IsStartActive => ActiveHandle == ClipHandle.Start;
    public bool IsEndActive => ActiveHandle == ClipHandle.End;
    public string StartText => ClipRange.Display(Start);
    public string EndText => ClipRange.Display(End);
    public string LengthText => ClipRange.Display(End - Start);
    public string DurationText => ClipRange.Display(Duration);
    public bool Can15 => Duration > QuickLengths[0];
    public bool Can30 => Duration > QuickLengths[1];
    public bool Can60 => Duration > QuickLengths[2];

    /// <summary>
    /// The time of the frame in the large picture: the start, or the clip's last frame for the end (the end itself is
    /// just after it), never past the video's last frame.
    /// </summary>
    public double PreviewTime
    {
        get
        {
            var frame = _video?.FrameLength ?? 1 / 30.0;
            var time = ActiveHandle == ClipHandle.Start ? Start : Math.Max(Start, End - frame);
            return Math.Clamp(time, 0, _video?.LastFrameTime ?? Math.Max(0, Duration));
        }
    }

    /// <summary>"Start 0:12.4" over the large picture.</summary>
    public string PreviewLabel => $"{_loc[IsStartActive ? "clip.start" : "clip.end"]} {(IsStartActive ? StartText : EndText)}";

    /// <summary>"15 s", "30 s", "60 s" on the quick length buttons.</summary>
    public string Length15Text => _loc.Format("clip.seconds", QuickLengths[0]);
    public string Length30Text => _loc.Format("clip.seconds", QuickLengths[1]);
    public string Length60Text => _loc.Format("clip.seconds", QuickLengths[2]);

    public event Action? CloseRequested;

    /// <param name="files">The videos, one after the other.</param>
    /// <param name="tool">The "Cut a clip" preset: name, output rule and the media options of an exact cut.</param>
    /// <param name="frames">Length and pictures of a video (FFmpeg).</param>
    /// <param name="targetFor">The format a clip of the file at a path is written in.</param>
    /// <param name="export">Starts the conversion that writes a clip (the file, the conversion's preset).</param>
    public ClipWindowViewModel(IReadOnlyList<string> files, Preset tool, ILocalizer loc, UserDataStore store, IVideoFrameSource frames,
        Func<string, string> targetFor, Action<string, Preset> export, ILogger? log = null)
    {
        _files = files;
        _tool = tool;
        _loc = loc;
        _store = store;
        _frames = frames;
        _targetFor = targetFor;
        _export = export;
        _log = log;
        _isFast = !tool.Media.ExactCut;
        _hasMultipleFiles = files.Count > 1;
        _workFolder = Path.Combine(Path.GetTempPath(), "Filee", "clip-" + Guid.NewGuid().ToString("N")[..10]);
        loc.LanguageChanged += OnLanguageChanged;
        _ = LoadFileAsync(0);
    }

    /// <summary>Opens file <paramref name="index"/>: its length, the first clip, and the timeline filling in.</summary>
    public async Task LoadFileAsync(int index)
    {
        if (_closed)
            return;
        if (index < 0 || index >= _files.Count)
        {
            Finish();
            return;
        }

        await _fileCts.CancelAsync();
        _fileCts.Dispose();
        _fileCts = new CancellationTokenSource();
        var ct = _fileCts.Token;
        _previewCts?.Cancel();

        IsLoading = true;
        CurrentIndex = index;
        var path = _files[index];
        CurrentFileName = Path.GetFileName(path);
        CounterText = _loc.Format("crop.page_counter", index + 1, _files.Count);
        SaveText = _loc[index < _files.Count - 1 ? "clip.save_next" : "clip.save"];
        ClearPictures();

        VideoSummary video;
        try
        {
            Directory.CreateDirectory(_workFolder);
            video = await _frames.ProbeAsync(path, ct);
            if (video.Duration <= 0)
                throw new InvalidDataException("The video doesn't say how long it is.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogWarning(ex, "Could not open {File} to cut a clip", path);
            if (_closed || ct.IsCancellationRequested)
                return;
            AddProblem(path, ex is InvalidDataException ? "error.unsupported_source" : "error.conversion_failed");
            await LoadFileAsync(index + 1);
            return;
        }
        if (ct.IsCancellationRequested)
            return;

        _video = video;
        Duration = video.Duration;
        ActiveHandle = ClipHandle.Start;
        Start = 0;
        End = ClipRange.Clamp(0, Math.Min(video.Duration, FirstClipLength), video.Duration).End;
        IsLoading = false;
        SchedulePreview(immediately: true);
        await FillTimelineAsync(path, video.Duration, ct);
    }

    /// <summary>Takes the timeline's pictures, a few at a time, each shown as soon as it is there.</summary>
    private async Task FillTimelineAsync(string path, double duration, CancellationToken ct)
    {
        var folder = Path.Combine(_workFolder, CurrentIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        using var gate = new SemaphoreSlim(ParallelThumbnails);
        var tasks = Enumerable.Range(0, ThumbnailCount).Select(async i =>
        {
            await gate.WaitAsync(ct);
            string? file = null;
            try
            {
                var output = Path.Combine(folder, $"thumb_{i:00}.jpg");
                if (await _frames.WriteFrameAsync(path, VideoFrames.ThumbnailTime(i, ThumbnailCount, duration), output, ThumbnailHeight, ct))
                    file = output;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                _log?.LogDebug(ex, "No timeline picture {Index} of {File}", i, path);
            }
            finally
            {
                gate.Release();
            }
            if (file is null || ct.IsCancellationRequested || _closed)
                return;
            var bitmap = Load(file);
            if (bitmap is null)
                return;
            if (ct.IsCancellationRequested || _closed || i >= Thumbnails.Count)
            {
                bitmap.Dispose();
                return;
            }
            Thumbnails[i] = bitmap;
            // The large picture shows the nearest timeline picture until its own frame is there.
            if (Preview is null)
                Preview = bitmap;
        }).ToList();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // Another file opened, or the window closed.
        }
    }

    // ───────────────────────── Moving the clip ─────────────────────────

    /// <summary>
    /// Moves <paramref name="handle"/> to <paramref name="seconds"/> (and makes it the active one), inside the video.
    /// Moved past the other end, it takes the other end along at the clip's length (as far as the video goes), so the
    /// start can be put on a moment anywhere and the clip follows; the clip is never shorter than
    /// <see cref="ClipRange.MinLength"/>.
    /// </summary>
    public void MoveHandle(ClipHandle handle, double seconds)
    {
        if (!CanEdit || !double.IsFinite(seconds))
            return;
        ActiveHandle = handle;
        var length = Math.Max(ClipRange.MinLength, End - Start);
        if (handle == ClipHandle.Start)
        {
            var start = Math.Clamp(seconds, 0, Math.Max(0, Duration - ClipRange.MinLength));
            if (start > End - ClipRange.MinLength)
                End = Math.Min(Duration, start + length);
            Start = start;
        }
        else
        {
            var end = Math.Clamp(seconds, Math.Min(Duration, ClipRange.MinLength), Duration);
            if (end < Start + ClipRange.MinLength)
                Start = Math.Max(0, end - length);
            End = end;
        }
        SchedulePreview();
    }

    /// <summary>Moves the whole clip so that it starts at <paramref name="start"/>; its length stays.</summary>
    public void MoveClip(double start)
    {
        if (!CanEdit || !double.IsFinite(start))
            return;
        var length = End - Start;
        var newStart = Math.Clamp(start, 0, Math.Max(0, Duration - length));
        Start = newStart;
        End = newStart + length;
        SchedulePreview();
    }

    /// <summary>Moves the active handle by <paramref name="seconds"/> (arrow keys: 0.1 s, with Shift 1 s).</summary>
    public void Nudge(double seconds) =>
        MoveHandle(ActiveHandle, (ActiveHandle == ClipHandle.Start ? Start : End) + seconds);

    /// <summary>Makes <paramref name="handle"/> the one the arrow keys move and the large picture shows.</summary>
    public void Activate(ClipHandle handle)
    {
        if (!CanEdit || ActiveHandle == handle)
            return;
        ActiveHandle = handle;
        SchedulePreview();
    }

    [RelayCommand]
    private void ActivateStart() => Activate(ClipHandle.Start);

    [RelayCommand]
    private void ActivateEnd() => Activate(ClipHandle.End);

    /// <summary>A quick length button: its seconds (<see cref="QuickLengths"/>) as text.</summary>
    [RelayCommand]
    private void QuickLength(string seconds)
    {
        if (double.TryParse(seconds, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
            SetLength(value);
    }

    /// <summary>
    /// A clip of <paramref name="seconds"/> from the start handle; when the video ends sooner, its last
    /// <paramref name="seconds"/>. The end becomes the active handle, to see where the clip now ends.
    /// </summary>
    public void SetLength(double seconds)
    {
        if (!CanEdit || seconds <= 0)
            return;
        var length = Math.Min(seconds, Duration);
        var start = Math.Min(Start, Math.Max(0, Duration - length));
        Start = start;
        End = start + length;
        ActiveHandle = ClipHandle.End;
        SchedulePreview();
    }

    partial void OnIsFastChanged(bool value)
    {
        // Remembered for next time, in the tool as saved (and in the one given to this window, maybe the same).
        if (_store.FindPreset(_tool.Id) is { IsClipTool: true } saved && saved.Media.ExactCut == value)
        {
            saved.Media.ExactCut = !value;
            _tool.Media.ExactCut = !value;
            try
            {
                _store.SaveLibrary();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.LogWarning(ex, "Could not remember the clip's cut");
            }
        }
        _tool.Media.ExactCut = !value;
    }

    // ───────────────────────── Saving ─────────────────────────

    /// <summary>
    /// Starts the conversion that writes the clip (its progress shows in the toast, where it can be cancelled), then
    /// opens the next video or closes.
    /// </summary>
    [RelayCommand]
    public async Task SaveAsync()
    {
        if (!CanEdit || _closed || CurrentIndex >= _files.Count)
            return;
        var path = _files[CurrentIndex];
        var clip = ClipRange.Clamp(Start, End, Duration);
        var tool = _tool.Clone();
        tool.Media.ExactCut = !IsFast;
        try
        {
            _export(path, ClipTool.ConversionOf(tool, _targetFor(path), clip));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogError(ex, "Could not start cutting a clip of {File}", path);
            AddProblem(path, "error.conversion_failed");
        }
        await LoadFileAsync(CurrentIndex + 1);
    }

    /// <summary>Leaves this video as it is and opens the next one.</summary>
    [RelayCommand]
    public async Task SkipAsync()
    {
        if (IsFinished || _closed)
            return;
        await LoadFileAsync(CurrentIndex + 1);
    }

    /// <summary>Close (Esc). Clips already started go on in the toast.</summary>
    [RelayCommand]
    public void Cancel()
    {
        Shutdown();
        CloseRequested?.Invoke();
    }

    // ───────────────────────── The large picture ─────────────────────────

    /// <summary>
    /// Shows the timeline picture nearest the active handle at once, and takes the handle's exact frame once the handle
    /// rests for a moment.
    /// </summary>
    private void SchedulePreview(bool immediately = false)
    {
        if (_closed || _video is null)
            return;
        var nearest = NearestThumbnail(PreviewTime);
        if (nearest is not null && !ReferenceEquals(nearest, Preview) && !immediately)
            SetPreview(nearest, own: false);

        _previewCts?.Cancel();
        _previewCts?.Dispose();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_fileCts.Token);
        _previewCts = cts;
        _ = TakePreviewAsync(_files[CurrentIndex], PreviewTime, immediately ? TimeSpan.Zero : PreviewDelay, cts.Token);
    }

    private async Task TakePreviewAsync(string path, double seconds, TimeSpan delay, CancellationToken ct)
    {
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);
            var output = Path.Combine(_workFolder, $"frame_{++_previewNumber}.jpg");
            if (!await _frames.WriteFrameAsync(path, seconds, output, PreviewHeight, ct) || ct.IsCancellationRequested || _closed)
                return;
            if (Load(output) is { } bitmap)
            {
                if (ct.IsCancellationRequested || _closed)
                    bitmap.Dispose();
                else
                    SetPreview(bitmap, own: true);
            }
            TryDelete(output);
        }
        catch (OperationCanceledException)
        {
            // The handle moved on.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogDebug(ex, "No frame at {Seconds} s of {File}", seconds, path);
        }
    }

    private Bitmap? NearestThumbnail(double seconds)
    {
        if (Thumbnails.Count == 0 || Duration <= 0)
            return null;
        var index = Math.Clamp((int)(seconds / Duration * Thumbnails.Count), 0, Thumbnails.Count - 1);
        // The nearest one that is there already, looking outwards.
        for (var d = 0; d < Thumbnails.Count; d++)
        {
            if (index - d >= 0 && Thumbnails[index - d] is { } before)
                return before;
            if (index + d < Thumbnails.Count && Thumbnails[index + d] is { } after)
                return after;
        }
        return null;
    }

    private void SetPreview(Bitmap bitmap, bool own)
    {
        var old = _ownPreview;
        Preview = bitmap;
        _ownPreview = own ? bitmap : null;
        if (old is not null && !ReferenceEquals(old, bitmap))
            old.Dispose();
    }

    private static Bitmap? Load(string file)
    {
        try
        {
            return new Bitmap(file);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    // ───────────────────────── Files and the end ─────────────────────────

    private void AddProblem(string path, string key)
    {
        var line = $"{Path.GetFileName(path)}: {_loc[key]}";
        Problems.Add(line);
        StatusNotice = line;
    }

    /// <summary>Closes after the last file, or stays open on the summary when a file could not be opened.</summary>
    private void Finish()
    {
        if (Problems.Count == 0)
        {
            Cancel();
            return;
        }
        IsLoading = false;
        Summary = _loc.Format("toast.done_errors", Problems.Count, _files.Count);
        StatusNotice = "";
        HasMultipleFiles = false;
        ClearPictures(keepCount: false);
        IsFinished = true;
    }

    private void ClearPictures(bool keepCount = true)
    {
        _video = null;
        var own = _ownPreview;
        _ownPreview = null;
        Preview = null;
        own?.Dispose();
        var old = Thumbnails.ToList();
        Thumbnails.Clear();
        if (keepCount)
        {
            for (var i = 0; i < ThumbnailCount; i++)
                Thumbnails.Add(null);
        }
        foreach (var bitmap in old)
            bitmap?.Dispose();
    }

    private void Shutdown()
    {
        if (_closed)
            return;
        _closed = true;
        _fileCts.Cancel();
        _previewCts?.Cancel();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (!IsFinished && CurrentIndex < _files.Count)
        {
            CounterText = _loc.Format("crop.page_counter", CurrentIndex + 1, _files.Count);
            SaveText = _loc[CurrentIndex < _files.Count - 1 ? "clip.save_next" : "clip.save"];
        }
        OnPropertyChanged(nameof(PreviewLabel));
        OnPropertyChanged(nameof(Length15Text));
        OnPropertyChanged(nameof(Length30Text));
        OnPropertyChanged(nameof(Length60Text));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The work folder goes when the window closes.
        }
    }

    public void Dispose()
    {
        Shutdown();
        _loc.LanguageChanged -= OnLanguageChanged;
        ClearPictures(keepCount: false);
        _previewCts?.Dispose();
        _fileCts.Dispose();
        // FFmpeg may still hold a picture for a moment after it was cancelled: the folder goes a little later.
        var folder = _workFolder;
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 5 && Directory.Exists(folder); attempt++)
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
            }
        });
    }
}
