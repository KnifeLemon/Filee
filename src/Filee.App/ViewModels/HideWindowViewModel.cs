// State of the "Pixelate / blur" window: one picture at a time, as in the crop overlay, with frames drawn over the parts
// to hide, each showing them already pixelated, blurred or blacked out (each frame its own style); the last style picked
// and how strongly (kept in the tool for the next time); and the conversion that writes the picture with those parts covered, which runs in the toast like
// any other. The frames carry on to the next picture of a batch (screenshots of one app put names in the same place).

using System.Collections.ObjectModel;
using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using ImageMagick;
using Microsoft.Extensions.Logging;

namespace Filee.App.ViewModels;

public sealed partial class HideWindowViewModel : ObservableObject, IDisposable
{
    private readonly IReadOnlyList<string> _files;
    private readonly Preset _tool;
    private readonly ILocalizer _loc;
    private readonly UserDataStore _store;
    private readonly Func<string, string> _targetFor;
    private readonly Action<string, Preset> _export;
    private readonly ILogger? _log;

    private MagickImage? _preview;
    private int _coverVersion;
    private int _coverShown;
    private bool _closed;

    /// <summary>The frames of the last picture saved, with their styles, for the next one.</summary>
    private List<(Rect, HideStyle)> _carried = [];

    /// <summary>The styles the covers were last asked for, so a frame moved or added in one of them asks for nothing.</summary>
    private string _coverStyles = "";

    [ObservableProperty] private int _currentIndex;
    [ObservableProperty] private string _currentFileName = "";
    [ObservableProperty] private string _counterText = "";
    [ObservableProperty] private bool _hasMultipleFiles;
    [ObservableProperty] private Bitmap? _previewImage;
    /// <summary>The preview pixelated all over: what a pixelate frame shows inside it.</summary>
    [ObservableProperty] private Bitmap? _coverImage;

    /// <summary>The preview blurred all over: what a blur frame shows inside it.</summary>
    [ObservableProperty] private Bitmap? _blurCoverImage;
    [ObservableProperty] private string _saveText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PixelSize))]
    private int _originalWidth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PixelSize))]
    private int _originalHeight;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave), nameof(ShowEmptyHint))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isBusy;

    /// <summary>The style the buttons show: the selected frame's, else the one a new frame gets.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPixelate), nameof(IsBlur), nameof(IsBox), nameof(CanChangeStrength))]
    private HideStyle _style;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StrengthText))]
    private int _strength;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusNotice))]
    private string _statusNotice = "";

    /// <summary>
    /// All files are done and some could not be opened: the window stays open on <see cref="Summary"/> and
    /// <see cref="Problems"/> until the user closes it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(CanSave), nameof(ShowEmptyHint))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isFinished;

    [ObservableProperty] private string _summary = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDone))]
    private int _doneCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSkipped))]
    private int _skippedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailed))]
    private int _failedCount;

    /// <summary>The parts of the current picture to hide.</summary>
    public HideAreaSet Areas { get; } = new();

    /// <summary>"file: reason" for each file that could not be opened.</summary>
    public ObservableCollection<string> Problems { get; } = [];

    /// <summary>Every file of the run with how it went, for the strip of thumbnails.</summary>
    public ObservableCollection<CropQueueItem> Queue { get; } = [];

    public PixelSize PixelSize => new(OriginalWidth, OriginalHeight);
    public bool IsEditing => !IsFinished;
    public bool HasStatusNotice => !string.IsNullOrWhiteSpace(StatusNotice);
    public bool HasDone => DoneCount > 0;
    public bool HasSkipped => SkippedCount > 0;
    public bool HasFailed => FailedCount > 0;
    public bool HasAreas => Areas.Count > 0;
    public bool HasSelection => Areas.Selected >= 0;
    public bool CanUndo => Areas.CanUndo;
    public bool CanRedo => Areas.CanRedo;

    /// <summary>Saving needs a part to hide: without one the copy would only be the picture again.</summary>
    public bool CanSave => IsEditing && !IsBusy && HasAreas;

    /// <summary>"Drag over what you want to hide", on a picture without frames.</summary>
    public bool ShowEmptyHint => IsEditing && !IsBusy && !HasAreas && PreviewImage is not null;

    public bool IsPixelate
    {
        get => Style == HideStyle.Pixelate;
        set
        {
            if (value)
                Style = HideStyle.Pixelate;
        }
    }

    public bool IsBlur
    {
        get => Style == HideStyle.Blur;
        set
        {
            if (value)
                Style = HideStyle.Blur;
        }
    }

    public bool IsBox
    {
        get => Style == HideStyle.Box;
        set
        {
            if (value)
                Style = HideStyle.Box;
        }
    }

    /// <summary>A black box has no strength: the slider is greyed while it is the style shown.</summary>
    public bool CanChangeStrength => Style != HideStyle.Box;

    /// <summary>"Strength: 5", for the slider's tooltip and screen readers.</summary>
    public string StrengthText => _loc.Format("hide.strength_value", Strength);

    public int MinStrength => HideTool.MinStrength;
    public int MaxStrength => HideTool.MaxStrength;

    public event Action? CloseRequested;

    /// <param name="files">The pictures, one after the other (other files are left out by the caller).</param>
    /// <param name="tool">The "Pixelate / blur" tool: the last choice, its name and output rule.</param>
    /// <param name="targetFor">The format a picture at a path is written in (<see cref="HideTool.TargetFor"/>).</param>
    /// <param name="export">Starts the conversion that writes a picture (the file, the conversion's preset).</param>
    public HideWindowViewModel(IReadOnlyList<string> files, Preset tool, ILocalizer loc, UserDataStore store,
        Func<string, string> targetFor, Action<string, Preset> export, ILogger? log = null)
    {
        _files = files;
        _tool = tool;
        _loc = loc;
        _store = store;
        _targetFor = targetFor;
        _export = export;
        _log = log;
        _style = tool.Image.HideStyle;
        Areas.DefaultStyle = _style;
        _strength = HideTool.ClampStrength(tool.Image.HideStrength);
        _hasMultipleFiles = files.Count > 1;
        for (var i = 0; i < files.Count; i++)
            Queue.Add(new CropQueueItem(loc, Path.GetFileName(files[i]), i, "hide.state_done"));
        Areas.Changed += OnAreasChanged;
        loc.LanguageChanged += OnLanguageChanged;
        _ = LoadFileAsync(0);
    }

    partial void OnStyleChanged(HideStyle value) => RefreshCoverIfStylesChanged();

    partial void OnStrengthChanged(int value)
    {
        var clamped = HideTool.ClampStrength(value);
        if (clamped != value)
        {
            Strength = clamped;
            return;
        }
        RefreshCover();
    }

    partial void OnPreviewImageChanged(Bitmap? value) => OnPropertyChanged(nameof(ShowEmptyHint));

    private void OnAreasChanged()
    {
        OnPropertyChanged(nameof(HasAreas));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ShowEmptyHint));
        SaveCommand.NotifyCanExecuteChanged();
        // Selecting a frame shows its style; with none selected, the style a new frame gets.
        Style = Areas.Selected >= 0 ? Areas.Styles[Areas.Selected] : Areas.DefaultStyle;
        RefreshCoverIfStylesChanged();
    }

    /// <summary>The styles that need a cover: those of the frames and the one shown (a frame is drawn in it next).</summary>
    private List<HideStyle> CoverStyles() =>
        [.. Areas.Styles.Append(Style).Where(s => s != HideStyle.Box).Distinct().Order()];

    private void RefreshCoverIfStylesChanged()
    {
        if (string.Join(",", CoverStyles()) != _coverStyles)
            RefreshCover();
    }

    /// <summary>Opens picture <paramref name="index"/>; past the last one, the end.</summary>
    public async Task LoadFileAsync(int index)
    {
        if (_closed)
            return;
        if (index < 0 || index >= _files.Count)
        {
            Finish();
            return;
        }

        IsBusy = true;
        CurrentIndex = index;
        var path = _files[index];
        CurrentFileName = Path.GetFileName(path);
        CounterText = _loc.Format("crop.page_counter", index + 1, _files.Count);
        SaveText = _loc[index < _files.Count - 1 ? "hide.save_next" : "hide.save"];
        Queue[index].State = CropItemState.Current;

        try
        {
            var (preview, width, height) = await HideService.LoadPreviewAsync(path, _tool);
            var bitmap = await Task.Run(() => HideService.ToBitmap(preview));
            if (_closed)
            {
                preview.Dispose();
                bitmap.Dispose();
                return;
            }
            ReplacePreview(preview);
            var old = PreviewImage;
            OriginalWidth = width;
            OriginalHeight = height;
            PreviewImage = bitmap;
            old?.Dispose();
            Queue[index].SetThumbnail(bitmap);
            Areas.Reset(_carried);
            RefreshCover();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogInformation(ex, "Could not open {File} for hiding parts", path);
            if (_closed)
                return;
            // The animated / multi-page check throws NotSupportedException: its English message stays in the log.
            var unsupported = ex is NotSupportedException;
            AddProblem(path, unsupported ? "error.unsupported_source" : "error.conversion_failed");
            Queue[index].State = CropItemState.Failed;
            IsBusy = false;
            await LoadFileAsync(index + 1);
            return;
        }
        finally
        {
            if (!_closed && CurrentIndex == index)
                IsBusy = false;
        }
    }

    /// <summary>
    /// Covers the preview again for each style in use (pixelate, blur; a black box needs none), off the window's thread:
    /// the frames show the picture as it is until then (softly blurred). Only the newest result is kept, so dragging the
    /// strength never queues up work.
    /// </summary>
    private void RefreshCover()
    {
        if (_preview is not { } preview || _closed)
            return;
        var version = ++_coverVersion;
        var styles = CoverStyles();
        _coverStyles = string.Join(",", styles);
        var sizes = styles.Select(style => (style, HideService.PreviewEffectSize(style, Strength, OriginalWidth, OriginalHeight, (int)preview.Width))).ToList();
        _ = Task.Run(() =>
        {
            var covers = new Dictionary<HideStyle, Bitmap>();
            try
            {
                lock (preview)
                {
                    // A newer choice, or another picture (this one disposed): nothing to do.
                    if (version != Volatile.Read(ref _coverVersion) || !ReferenceEquals(preview, Volatile.Read(ref _preview)))
                        return;
                    foreach (var (style, size) in sizes)
                        covers[style] = HideService.Cover(preview, style, size);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log?.LogDebug(ex, "Could not cover the preview");
                foreach (var made in covers.Values)
                    made.Dispose();
                return;
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (version != _coverVersion || _closed || !ReferenceEquals(preview, _preview))
                {
                    foreach (var made in covers.Values)
                        made.Dispose();
                    return;
                }
                var oldPixelate = CoverImage;
                var oldBlur = BlurCoverImage;
                CoverImage = covers.GetValueOrDefault(HideStyle.Pixelate);
                BlurCoverImage = covers.GetValueOrDefault(HideStyle.Blur);
                _coverShown = version;
                oldPixelate?.Dispose();
                oldBlur?.Dispose();
            });
        });
    }

    /// <summary>Waits for the covered previews of the current choice (tests capture them).</summary>
    internal bool IsCoverCurrent => _coverShown == _coverVersion
        && CoverStyles().All(style => (style == HideStyle.Blur ? BlurCoverImage : CoverImage) is not null);

    private void ReplacePreview(MagickImage? preview)
    {
        var old = _preview;
        _preview = preview;
        if (old is not null)
        {
            lock (old)
                old.Dispose();
        }
        var cover = CoverImage;
        var blur = BlurCoverImage;
        CoverImage = null;
        BlurCoverImage = null;
        _coverStyles = "";
        cover?.Dispose();
        blur?.Dispose();
    }

    // ───────────────────────── Commands ─────────────────────────

    /// <summary>
    /// Starts the conversion that writes the picture with its parts covered (its progress shows in the toast), keeps the
    /// choice in the tool, then opens the next picture or closes.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task SaveAsync()
    {
        if (!CanSave || _closed || CurrentIndex >= _files.Count)
            return;
        var path = _files[CurrentIndex];
        var areas = Areas.Areas.Select((a, i) => new HiddenArea { X = a.X, Y = a.Y, Width = a.Width, Height = a.Height, Style = Areas.Styles[i] }).ToList();
        Remember();
        try
        {
            _export(path, HideTool.ConversionOf(_tool, _targetFor(path), areas));
            Queue[CurrentIndex].State = CropItemState.Done;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogError(ex, "Could not start hiding parts of {File}", path);
            AddProblem(path, "error.conversion_failed");
            Queue[CurrentIndex].State = CropItemState.Failed;
        }
        _carried = Carried();
        await LoadFileAsync(CurrentIndex + 1);
    }

    private List<(Rect, HideStyle)> Carried() => [.. Areas.Areas.Zip(Areas.Styles)];

    /// <summary>Leaves this picture as it is and opens the next one.</summary>
    [RelayCommand]
    public async Task SkipAsync()
    {
        if (IsBusy || IsFinished || _closed)
            return;
        Queue[CurrentIndex].State = CropItemState.Skipped;
        // The frames stay for the next picture, as drawn.
        _carried = Carried();
        await LoadFileAsync(CurrentIndex + 1);
    }

    /// <summary>Close (Esc). Pictures already started go on in the toast.</summary>
    [RelayCommand]
    public void Cancel()
    {
        Shutdown();
        CloseRequested?.Invoke();
    }

    /// <summary>
    /// Covers the selected frame as <paramref name="style"/> (one undo step); either way, frames drawn next get it too.
    /// </summary>
    [RelayCommand]
    public void SetStyle(HideStyle style)
    {
        if (!IsEditing)
            return;
        Areas.DefaultStyle = style;
        if (Areas.Selected >= 0)
            Areas.SetStyle(Areas.Selected, style);
        Style = style;
    }

    /// <summary>Removes every frame (R); Ctrl+Z brings them back.</summary>
    [RelayCommand]
    public void Clear()
    {
        if (IsEditing && !IsBusy)
            Areas.Clear();
    }

    [RelayCommand]
    public void RemoveSelected()
    {
        if (IsEditing && !IsBusy)
            Areas.RemoveSelected();
    }

    [RelayCommand]
    public void Undo()
    {
        if (IsEditing && !IsBusy)
            Areas.Undo();
    }

    [RelayCommand]
    public void Redo()
    {
        if (IsEditing && !IsBusy)
            Areas.Redo();
    }

    /// <summary>A step stronger or weaker (+ / −).</summary>
    public void StepStrength(int step)
    {
        if (IsEditing)
            Strength = HideTool.ClampStrength(Strength + step);
    }

    // ───────────────────────── Files, messages and the end ─────────────────────────

    /// <summary>Keeps the style picked last and the strength in the tool, as saved and as given to this window.</summary>
    private void Remember()
    {
        var style = Areas.DefaultStyle;
        HideTool.Remember(_tool, style, Strength);
        if (_store.FindPreset(_tool.Id) is not { IsHideTool: true } saved
            || (saved.Image.HideStyle == style && saved.Image.HideStrength == Strength))
            return;
        HideTool.Remember(saved, style, Strength);
        try
        {
            _store.SaveLibrary();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning(ex, "Could not remember how parts are hidden");
        }
    }

    private void AddProblem(string path, string key)
    {
        var line = $"{Path.GetFileName(path)}: {_loc[key]}";
        Problems.Add(line);
        StatusNotice = line;
    }

    /// <summary>Closes after the last picture, or stays open on the summary when a picture could not be opened.</summary>
    private void Finish()
    {
        if (Problems.Count == 0)
        {
            Cancel();
            return;
        }
        Summary = _loc.Format("toast.done_errors", Problems.Count, Queue.Count);
        DoneCount = Queue.Count(q => q.State == CropItemState.Done);
        SkippedCount = Queue.Count(q => q.State == CropItemState.Skipped);
        FailedCount = Queue.Count(q => q.State == CropItemState.Failed);
        StatusNotice = "";
        HasMultipleFiles = false;
        IsBusy = false;
        Areas.Reset([]);
        ReplacePreview(null);
        var old = PreviewImage;
        PreviewImage = null;
        old?.Dispose();
        IsFinished = true;
    }

    private void Shutdown()
    {
        if (_closed)
            return;
        // A choice made without saving is kept too: the window opens with it next time.
        if (IsEditing && PreviewImage is not null)
            Remember();
        _closed = true;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (!IsFinished && CurrentIndex < _files.Count)
        {
            CounterText = _loc.Format("crop.page_counter", CurrentIndex + 1, _files.Count);
            SaveText = _loc[CurrentIndex < _files.Count - 1 ? "hide.save_next" : "hide.save"];
        }
        OnPropertyChanged(nameof(StrengthText));
        foreach (var item in Queue)
            item.RefreshTexts();
    }

    public void Dispose()
    {
        Shutdown();
        _loc.LanguageChanged -= OnLanguageChanged;
        Areas.Changed -= OnAreasChanged;
        ReplacePreview(null);
        PreviewImage?.Dispose();
        PreviewImage = null;
        foreach (var item in Queue)
            item.Dispose();
    }
}
