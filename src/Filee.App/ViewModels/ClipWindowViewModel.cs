// State of the "Cut a clip" window: one video at a time on a timeline of small pictures with the loudness of its
// sound, the clip between its start and end cut into pieces (ClipCutter), the playhead, the large picture of the frame
// being looked at or of the clip playing with its sound, and the conversion that writes the clip.

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Filee.Engines.Media;
using Microsoft.Extensions.Logging;

namespace Filee.App.ViewModels;

/// <summary>What the arrow keys move and the large picture shows: an end of the clip, or the playhead.</summary>
public enum ClipHandle
{
    Start,
    End,
    Playhead,
}

public sealed partial class ClipWindowViewModel : ObservableObject, IDisposable
{
    /// <summary>Pictures on the timeline.</summary>
    public const int ThumbnailCount = 16;

    /// <summary>Steps of the sound's loudness drawn on the timeline.</summary>
    public const int LoudnessSteps = 480;

    /// <summary>Height of a timeline picture (twice its size on screen, for high-DPI displays).</summary>
    private const int ThumbnailHeight = 96;

    /// <summary>Height of the large picture of the frame looked at.</summary>
    private const int PreviewHeight = 720;

    /// <summary>Height of the pictures while the clip plays (smaller: they come 25 times a second).</summary>
    private const int PlayHeight = 360;

    /// <summary>FFmpeg runs at the same time while the timeline fills: one per picture would starve the window.</summary>
    private const int ParallelThumbnails = 3;

    /// <summary>How long a fade lasts when switched on.</summary>
    public const double FadeSeconds = 1;

    /// <summary>How long a short message (silences removed, nothing to cut) stays.</summary>
    private static readonly TimeSpan MessageTime = TimeSpan.FromSeconds(4);

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
    private CancellationTokenSource? _playCts;
    private ClipCutter? _cutter;
    private VideoSummary? _video;
    private Bitmap? _ownPreview;   // a frame taken for the large picture (not a timeline one), disposed with it
    private readonly WriteableBitmap?[] _playFrames = new WriteableBitmap?[2]; // what plays, written in turn
    private readonly Lock _playFramesLock = new();
    private int _playFrame;
    private int _previewNumber;
    private bool _syncing;
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
    [NotifyPropertyChangedFor(nameof(PreviewTime))]
    [NotifyPropertyChangedFor(nameof(PreviewLabel))]
    private double _start;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndText))]
    [NotifyPropertyChangedFor(nameof(PreviewTime))]
    [NotifyPropertyChangedFor(nameof(PreviewLabel))]
    private double _end;

    /// <summary>How long the clip plays (its pieces at their speeds, without those cut out).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LengthText))]
    private double _length;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStartActive))]
    [NotifyPropertyChangedFor(nameof(IsEndActive))]
    [NotifyPropertyChangedFor(nameof(PreviewTime))]
    [NotifyPropertyChangedFor(nameof(PreviewLabel))]
    private ClipHandle _activeHandle;

    /// <summary>Where the playhead stands in the video: where the blade cuts and playing starts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayheadText))]
    [NotifyPropertyChangedFor(nameof(PreviewTime))]
    [NotifyPropertyChangedFor(nameof(PreviewLabel))]
    private double _playhead;

    /// <summary>The pieces between the start and the end (a copy, new after each change).</summary>
    [ObservableProperty] private IReadOnlyList<ClipPiece> _pieces = [];

    /// <summary>The piece the volume, speed and Delete apply to, or -1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteText))]
    private int _selectedIndex = -1;

    /// <summary>The loudness of the sound over the video (0 to 1), empty until read or without sound.</summary>
    [ObservableProperty] private IReadOnlyList<float> _loudness = [];

    /// <summary>The large picture: the frame looked at, or the clip playing.</summary>
    [ObservableProperty] private Bitmap? _preview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayToolTip))]
    private bool _isPlaying;

    /// <summary>
    /// The magnet: a piece cut out closes up (else its time stays as a pause), and handles and the playhead snap to
    /// cuts and to each other.
    /// </summary>
    [ObservableProperty] private bool _isMagnet = true;

    /// <summary>The timeline shows the clip and a little around it instead of the whole video (Z).</summary>
    [ObservableProperty] private bool _isZoomed;

    /// <summary>The stretch of the video the timeline shows (all of it unless zoomed).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewStartText))]
    private double _viewStart;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewEndText))]
    private double _viewEnd;

    /// <summary>Copy the streams (fast, cut at key frames) rather than encode the clip. Remembered in the tool.</summary>
    [ObservableProperty] private bool _isFast = true;

    /// <summary>True when the clip can be copied: one stretch as recorded, no fades.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FastToolTip))]
    private bool _canFast = true;

    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;
    [ObservableProperty] private bool _hasGaps;
    [ObservableProperty] private bool _isFindingSilence;

    /// <summary>A short note after an action (silences removed, nowhere to cut), shown for a moment.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = "";

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

    /// <summary>The speeds a piece can play at.</summary>
    public IReadOnlyList<Choice<double>> Speeds { get; } =
        ClipPart.Speeds.Select(s => new Choice<double>(s, s.ToString("0.##", CultureInfo.CurrentCulture) + "×")).ToList();

    public bool HasStatusNotice => !string.IsNullOrWhiteSpace(StatusNotice);
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool IsEditing => !IsFinished;
    public bool CanEdit => !IsFinished && !IsLoading;
    public bool IsStartActive => ActiveHandle == ClipHandle.Start;
    public bool IsEndActive => ActiveHandle == ClipHandle.End;
    public string StartText => ClipRange.Display(Start);
    public string EndText => ClipRange.Display(End);
    public string LengthText => ClipRange.Display(Length);
    public string DurationText => ClipRange.Display(Duration);
    public string PlayheadText => ClipRange.Display(Playhead);
    public string ViewStartText => ClipRange.Display(ViewStart);
    public string ViewEndText => ClipRange.Display(ViewEnd);
    public bool Can15 => Duration > QuickLengths[0];
    public bool Can30 => Duration > QuickLengths[1];
    public bool Can60 => Duration > QuickLengths[2];
    public string PlayToolTip => _loc[IsPlaying ? "clip.pause" : "clip.play"];
    public string FastToolTip => _loc[CanFast ? "clip.fast_hint" : "clip.fast_needs_encode"];

    /// <summary>The piece the piece controls apply to: the selected one, else the one at the playhead, else the first.</summary>
    public int CurrentPiece =>
        SelectedIndex >= 0 && SelectedIndex < Pieces.Count ? SelectedIndex
        : _cutter?.IndexAt(Playhead) is >= 0 and var at ? at
        : Pieces.Count > 0 ? 0 : -1;

    /// <summary>Delete, or Put back for a piece cut out.</summary>
    public string DeleteText => _loc[CurrentPiece >= 0 && CurrentPiece < Pieces.Count && !Pieces[CurrentPiece].IsKept ? "clip.restore" : "clip.delete"];

    /// <summary>The current piece's volume for the slider: 0 to 2 (200 %).</summary>
    public double SelectedVolume
    {
        get => CurrentPiece >= 0 ? Pieces[CurrentPiece].Volume : 1;
        set
        {
            if (_syncing || _cutter is null || CurrentPiece < 0)
                return;
            StopPlaying();
            _cutter.SetVolume(CurrentPiece, value);
        }
    }

    public string VolumeText => $"{Math.Round(SelectedVolume * 100).ToString(CultureInfo.CurrentCulture)} %";

    /// <summary>The current piece's speed.</summary>
    public Choice<double>? SelectedSpeed
    {
        get
        {
            var speed = CurrentPiece >= 0 ? Pieces[CurrentPiece].Speed : 1;
            return Speeds.MinBy(s => Math.Abs(s.Value - speed));
        }
        set
        {
            if (_syncing || _cutter is null || CurrentPiece < 0 || value is null)
                return;
            StopPlaying();
            _cutter.SetSpeed(CurrentPiece, value.Value);
        }
    }

    /// <summary>The clip fades in from black and silence.</summary>
    public bool HasFadeIn
    {
        get => _cutter?.FadeIn > 0;
        set
        {
            if (_syncing || _cutter is null)
                return;
            StopPlaying();
            _cutter.FadeIn = value ? FadeSeconds : 0;
        }
    }

    /// <summary>The clip fades out to black and silence.</summary>
    public bool HasFadeOut
    {
        get => _cutter?.FadeOut > 0;
        set
        {
            if (_syncing || _cutter is null)
                return;
            StopPlaying();
            _cutter.FadeOut = value ? FadeSeconds : 0;
        }
    }

    /// <summary>
    /// The time of the frame in the large picture: the start, the clip's last frame for the end (the end itself is just
    /// after it), or the playhead; never past the video's last frame.
    /// </summary>
    public double PreviewTime
    {
        get
        {
            var frame = _video?.FrameLength ?? 1 / 30.0;
            var time = ActiveHandle switch
            {
                ClipHandle.Start => Start,
                ClipHandle.End => Math.Max(Start, End - frame),
                _ => Playhead,
            };
            return Math.Clamp(time, 0, _video?.LastFrameTime ?? Math.Max(0, Duration));
        }
    }

    /// <summary>"Start 0:12.4" over the large picture.</summary>
    public string PreviewLabel => ActiveHandle switch
    {
        ClipHandle.Start => $"{_loc["clip.start"]} {StartText}",
        ClipHandle.End => $"{_loc["clip.end"]} {EndText}",
        _ => $"{_loc["clip.playhead"]} {PlayheadText}",
    };

    /// <summary>"15 s", "30 s", "60 s" on the quick length buttons.</summary>
    public string Length15Text => _loc.Format("clip.seconds", QuickLengths[0]);
    public string Length30Text => _loc.Format("clip.seconds", QuickLengths[1]);
    public string Length60Text => _loc.Format("clip.seconds", QuickLengths[2]);

    public event Action? CloseRequested;

    /// <param name="files">The videos, one after the other.</param>
    /// <param name="tool">The "Cut a clip" preset: name, output rule and the media options of an encoded clip.</param>
    /// <param name="frames">Length, pictures, sound and playing of a video (FFmpeg).</param>
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
        StopPlaying();
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
        UseCutter(null);
        Loudness = [];

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
        ViewEnd = video.Duration;
        UseCutter(new ClipCutter(video.Duration, new ClipRange(0, Math.Min(video.Duration, FirstClipLength))));
        ActiveHandle = ClipHandle.Start;
        Playhead = 0;
        SelectedIndex = -1;
        IsLoading = false;
        SchedulePreview(immediately: true);
        _ = ReadLoudnessAsync(path, video.Duration, ct);
        await FillTimelineAsync(path, video.Duration, ct);
    }

    /// <summary>Takes the timeline's pictures, a few at a time, each shown as soon as it is there.</summary>
    private async Task FillTimelineAsync(string path, double duration, CancellationToken ct)
    {
        var folder = Path.Combine(_workFolder, CurrentIndex.ToString(CultureInfo.InvariantCulture));
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

    private async Task ReadLoudnessAsync(string path, double duration, CancellationToken ct)
    {
        try
        {
            var loudness = await _frames.ReadLoudnessAsync(path, duration, LoudnessSteps, _workFolder, ct);
            if (!ct.IsCancellationRequested && !_closed)
                Loudness = loudness;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogDebug(ex, "No loudness of {File}", path);
        }
    }

    // ───────────────────────── The clip and its pieces ─────────────────────────

    private void UseCutter(ClipCutter? cutter)
    {
        if (_cutter is not null)
            _cutter.Changed -= Sync;
        _cutter = cutter;
        if (cutter is not null)
            cutter.Changed += Sync;
        Sync();
    }

    /// <summary>Everything shown of the clip, after a change.</summary>
    private void Sync()
    {
        _syncing = true;
        try
        {
            var cutter = _cutter;
            Pieces = cutter?.Pieces.ToList() ?? [];
            Start = cutter?.Start ?? 0;
            End = cutter?.End ?? 0;
            Length = cutter?.Length ?? 0;
            CanUndo = cutter?.CanUndo == true;
            CanRedo = cutter?.CanRedo == true;
            HasGaps = cutter?.HasGaps == true;
            CanFast = cutter is null || cutter.ToEdit().IsPlainCut;
            FitView(refit: false);
            if (SelectedIndex >= Pieces.Count)
                SelectedIndex = Pieces.Count - 1;
            OnPropertyChanged(nameof(SelectedVolume));
            OnPropertyChanged(nameof(VolumeText));
            OnPropertyChanged(nameof(SelectedSpeed));
            OnPropertyChanged(nameof(HasFadeIn));
            OnPropertyChanged(nameof(HasFadeOut));
            OnPropertyChanged(nameof(DeleteText));
            OnPropertyChanged(nameof(CurrentPiece));
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnIsZoomedChanged(bool value) => FitView(refit: true);

    /// <summary>The zoom button (Z): the clip close up, or the whole video.</summary>
    [RelayCommand]
    public void ToggleZoom() => IsZoomed = !IsZoomed;

    /// <summary>
    /// The timeline's view: the whole video, or zoomed the clip with a little room around it. Zoomed, it is fitted
    /// again only when the clip no longer fits (<paramref name="refit"/>: always), so a handle dragged to the view's
    /// edge doesn't move the view under the pointer.
    /// </summary>
    private void FitView(bool refit)
    {
        if (!IsZoomed || _cutter is null || Duration <= 0)
        {
            ViewStart = 0;
            ViewEnd = Duration;
            return;
        }
        var start = _cutter.Start;
        var end = _cutter.End;
        if (!refit && start >= ViewStart - 1e-9 && end <= ViewEnd + 1e-9 && ViewEnd > ViewStart)
            return;
        var room = Math.Max((end - start) * 0.15, 1);
        ViewStart = Math.Max(0, start - room);
        ViewEnd = Math.Min(Duration, end + room);
    }

    partial void OnSelectedIndexChanged(int value) => RefreshPieceControls();
    partial void OnPlayheadChanged(double value)
    {
        if (SelectedIndex < 0)
            RefreshPieceControls();
    }

    private void RefreshPieceControls()
    {
        _syncing = true;
        try
        {
            OnPropertyChanged(nameof(CurrentPiece));
            OnPropertyChanged(nameof(SelectedVolume));
            OnPropertyChanged(nameof(VolumeText));
            OnPropertyChanged(nameof(SelectedSpeed));
            OnPropertyChanged(nameof(DeleteText));
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// Moves <paramref name="handle"/> to <paramref name="seconds"/> and makes it the active one. An end moved past the
    /// other takes the clip along at its length; the playhead goes anywhere in the video.
    /// </summary>
    public void MoveHandle(ClipHandle handle, double seconds)
    {
        if (!CanEdit || _cutter is null || !double.IsFinite(seconds))
            return;
        StopPlaying();
        ActiveHandle = handle;
        switch (handle)
        {
            case ClipHandle.Start:
                _cutter.MoveStart(seconds);
                break;
            case ClipHandle.End:
                _cutter.MoveEnd(seconds);
                break;
            default:
                Playhead = Math.Clamp(seconds, 0, Duration);
                SelectedIndex = _cutter.IndexAt(Playhead);
                break;
        }
        SchedulePreview();
    }

    /// <summary>Puts the playhead at <paramref name="seconds"/> and selects the piece there (a click on the timeline).</summary>
    public void MovePlayhead(double seconds) => MoveHandle(ClipHandle.Playhead, seconds);

    /// <summary>Moves the whole clip, cuts and all, so that it starts at <paramref name="start"/>.</summary>
    public void MoveClip(double start)
    {
        if (!CanEdit || _cutter is null || !double.IsFinite(start))
            return;
        StopPlaying();
        _cutter.Move(start);
        SchedulePreview();
    }

    /// <summary>Moves the active handle by <paramref name="seconds"/> (arrow keys: 0.1 s, with Shift 1 s).</summary>
    public void Nudge(double seconds) =>
        MoveHandle(ActiveHandle, ActiveHandle switch { ClipHandle.Start => Start, ClipHandle.End => End, _ => Playhead } + seconds);

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
        if (double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            SetLength(value);
    }

    /// <summary>
    /// The end <paramref name="seconds"/> after the start; when the video ends sooner, its last
    /// <paramref name="seconds"/>. The end becomes the active handle, to see where the clip now ends.
    /// </summary>
    public void SetLength(double seconds)
    {
        if (!CanEdit || _cutter is null || seconds <= 0)
            return;
        StopPlaying();
        _cutter.SetLength(seconds);
        ActiveHandle = ClipHandle.End;
        SchedulePreview();
    }

    /// <summary>The blade (C): cuts the piece at the playhead in two.</summary>
    [RelayCommand]
    public void Split()
    {
        if (!CanEdit || _cutter is null)
            return;
        StopPlaying();
        if (_cutter.Split(Playhead))
            SelectedIndex = _cutter.IndexAt(Playhead);
        else
            ShowMessage(_loc["clip.cannot_cut"]);
    }

    /// <summary>
    /// Delete: cuts the current piece out (closing up with the magnet on, leaving a pause without it); on a piece cut
    /// out, puts it back.
    /// </summary>
    [RelayCommand]
    public void Delete()
    {
        if (!CanEdit || _cutter is null || CurrentPiece is var index && index < 0)
            return;
        StopPlaying();
        if (!Pieces[index].IsKept)
            _cutter.Restore(index);
        else if (!_cutter.Remove(index, closeUp: IsMagnet))
            ShowMessage(_loc["clip.last_piece"]);
        OnPropertyChanged(nameof(DeleteText));
    }

    [RelayCommand]
    public void Undo()
    {
        if (CanEdit && _cutter is not null)
        {
            StopPlaying();
            _cutter.Undo();
        }
    }

    [RelayCommand]
    public void Redo()
    {
        if (CanEdit && _cutter is not null)
        {
            StopPlaying();
            _cutter.Redo();
        }
    }

    /// <summary>Closes the pauses left by pieces cut out with the magnet off.</summary>
    [RelayCommand]
    public void CloseGaps()
    {
        if (CanEdit && _cutter is not null)
        {
            StopPlaying();
            _cutter.CloseGaps();
        }
    }

    /// <summary>Finds the silent stretches of the clip and takes them out (one undo puts them back).</summary>
    [RelayCommand]
    public async Task RemoveSilenceAsync()
    {
        if (!CanEdit || _cutter is null || IsFindingSilence)
            return;
        StopPlaying();
        var cutter = _cutter;
        var ct = _fileCts.Token;
        IsFindingSilence = true;
        try
        {
            var silences = await _frames.FindSilencesAsync(_files[CurrentIndex], cutter.Start, cutter.End, ct);
            if (ct.IsCancellationRequested || _closed || !ReferenceEquals(cutter, _cutter))
                return;
            var removed = cutter.RemoveSilences(silences);
            ShowMessage(removed > 0 ? _loc.Format("clip.silence_removed", removed) : _loc["clip.no_silence"]);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogWarning(ex, "Could not find the silences of {File}", _files[CurrentIndex]);
            ShowMessage(_loc["clip.no_silence"]);
        }
        finally
        {
            IsFindingSilence = false;
        }
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

    // ───────────────────────── Playing ─────────────────────────

    /// <summary>Space: plays the clip as it will be written, with its sound, from the playhead (or its start); again stops.</summary>
    [RelayCommand]
    public void PlayPause()
    {
        if (IsPlaying)
            StopPlaying();
        else
            _ = PlayAsync();
    }

    private async Task PlayAsync()
    {
        if (!CanEdit || _cutter is null || _closed)
            return;
        var edit = _cutter.ToEdit();
        var from = Playhead > edit.Start && Playhead < edit.End ? edit.ClipTimeAt(Playhead) : 0;
        if (from >= edit.Length - 0.05)
            from = 0;
        _previewCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_fileCts.Token);
        _playCts = cts;
        IsPlaying = true;
        ActiveHandle = ClipHandle.Playhead;
        Playhead = edit.SourceTimeAt(from);
        var path = _files[CurrentIndex];
        try
        {
            await _frames.PlayAsync(path, edit.From(from), PlayHeight,
                (pixels, width, height, seconds) => ShowFrame(pixels, width, height, edit.SourceTimeAt(from + seconds), cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogWarning(ex, "Could not play the clip of {File}", path);
            ShowMessage(_loc["clip.cannot_play"]);
        }
        finally
        {
            if (ReferenceEquals(_playCts, cts))
            {
                _playCts = null;
                IsPlaying = false;
                if (!_closed && !cts.IsCancellationRequested)
                    SchedulePreview(immediately: true);
            }
            cts.Dispose();
        }
    }

    /// <summary>Stops the clip playing; the playhead stays where it got to.</summary>
    public void StopPlaying()
    {
        if (_playCts is not { } cts)
            return;
        _playCts = null;
        cts.Cancel();
        IsPlaying = false;
        if (!_closed)
            SchedulePreview(immediately: true);
    }

    /// <summary>A picture of the clip playing (on a worker thread): written into a frame not on screen, then shown.</summary>
    private void ShowFrame(byte[] pixels, int width, int height, double sourceTime, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || _closed)
            return;
        WriteableBitmap bitmap;
        lock (_playFramesLock)
        {
            _playFrame = 1 - _playFrame;
            bitmap = _playFrames[_playFrame] is { } existing && existing.PixelSize == new PixelSize(width, height)
                ? existing
                : _playFrames[_playFrame] = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using var buffer = bitmap.Lock();
            var row = width * 4;
            for (var y = 0; y < height; y++)
                Marshal.Copy(pixels, y * row, buffer.Address + y * buffer.RowBytes, row);
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (ct.IsCancellationRequested || _closed)
                return;
            SetPreview(bitmap, own: false);
            Playhead = sourceTime;
        });
    }

    // ───────────────────────── Saving ─────────────────────────

    /// <summary>
    /// Starts the conversion that writes the clip (its progress shows in the toast, where it can be cancelled), then
    /// opens the next video or closes.
    /// </summary>
    [RelayCommand]
    public async Task SaveAsync()
    {
        if (!CanEdit || _closed || _cutter is null || CurrentIndex >= _files.Count)
            return;
        StopPlaying();
        var path = _files[CurrentIndex];
        var tool = _tool.Clone();
        tool.Media.ExactCut = !IsFast;
        try
        {
            _export(path, ClipTool.ConversionOf(tool, _targetFor(path), _cutter.ToEdit()));
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
    /// Shows the timeline picture nearest the frame looked at at once, and takes that exact frame once the handle rests
    /// for a moment.
    /// </summary>
    private void SchedulePreview(bool immediately = false)
    {
        if (_closed || _video is null || IsPlaying)
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
                if (ct.IsCancellationRequested || _closed || IsPlaying)
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

    // ───────────────────────── Files, messages and the end ─────────────────────────

    private void ShowMessage(string text)
    {
        Message = text;
        _ = ClearMessageLaterAsync(text);
    }

    private async Task ClearMessageLaterAsync(string text)
    {
        try
        {
            await Task.Delay(MessageTime, _fileCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        if (Message == text)
            Message = "";
    }

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
        _playCts?.Cancel();
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
        OnPropertyChanged(nameof(PlayToolTip));
        OnPropertyChanged(nameof(FastToolTip));
        OnPropertyChanged(nameof(DeleteText));
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
        UseCutter(null);
        ClearPictures(keepCount: false);
        lock (_playFramesLock)
        {
            foreach (var frame in _playFrames)
                frame?.Dispose();
            Array.Clear(_playFrames);
        }
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
