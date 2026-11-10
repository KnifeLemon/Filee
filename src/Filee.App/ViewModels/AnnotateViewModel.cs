// State of the "Annotate" window: one picture at a time with the marks drawn on it (AnnotationSet), the tool, colour
// and width in use (a colour or width picked while a mark is selected with Select restyles it; a mark just drawn isn't
// selected, so a colour picked next goes to the next mark), and the conversion that writes the picture with its marks,
// which runs in the toast like any other. The colour and width are kept in the tool.

using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Filee.App.ViewModels;

public sealed partial class AnnotateViewModel : ObservableObject, IDisposable
{
    private readonly IReadOnlyList<string> _files;
    private readonly Preset _preset;
    private readonly ILocalizer _loc;
    private readonly UserDataStore _store;
    private readonly Func<string, string> _targetFor;
    private readonly Action<string, Preset> _export;
    private readonly ILogger? _log;
    private bool _closed;

    /// <summary>How each picture went: saved, skipped or failed (none yet while it is open).</summary>
    private readonly CropItemState?[] _outcomes;

    public AnnotateViewModel(IReadOnlyList<string> files, Preset tool, ILocalizer loc, UserDataStore store,
        Func<string, string> targetFor, Action<string, Preset> export, ILogger? log = null)
    {
        _files = files;
        _preset = tool;
        _loc = loc;
        _store = store;
        _targetFor = targetFor;
        _export = export;
        _log = log;
        _color = AnnotateTool.Colors.Contains(tool.Image.AnnotationColor) ? tool.Image.AnnotationColor! : AnnotateTool.Colors[0];
        _width = tool.Image.AnnotationWidth;
        _hasMultipleFiles = files.Count > 1;
        _outcomes = new CropItemState?[files.Count];
        Swatches = [.. AnnotateTool.Colors.Select((c, i) => new AnnotateSwatch(c, AnnotateTool.ColorNameKeys[i], this) { Name = loc[AnnotateTool.ColorNameKeys[i]] })];
        Marks.Changed += OnMarksChanged;
        loc.LanguageChanged += OnLanguageChanged;
        // The fonts labels are drawn in, read now off the UI thread rather than on the first text.
        _ = Task.Run(Filee.Engines.Annotations.AnnotationPainter.WarmFonts);
        UpdateSwatches();
        _ = LoadFileAsync(0);
    }

    /// <summary>The marks on the picture shown.</summary>
    public AnnotationSet Marks { get; } = new();

    public IReadOnlyList<AnnotateSwatch> Swatches { get; }

    [ObservableProperty] private int _currentIndex;
    [ObservableProperty] private string _currentFileName = "";
    [ObservableProperty] private string _counterText = "";
    [ObservableProperty] private bool _hasMultipleFiles;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private Bitmap? _previewImage;

    /// <summary>The picture's own size, upright: the window paints the marks at it, as the file gets them.</summary>
    [ObservableProperty] private Avalonia.PixelSize _pictureSize;
    [ObservableProperty] private string _statusNotice = "";

    /// <summary>
    /// All pictures are done and some could not be opened or saved: the window stays open on <see cref="Summary"/> and
    /// <see cref="Problems"/> until it is closed (as "Pixelate / blur" does).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    private bool _isFinished;

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private int _doneCount;
    [ObservableProperty] private int _skippedCount;
    [ObservableProperty] private int _failedCount;

    /// <summary>"file: reason" for each picture that could not be opened or saved.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> Problems { get; } = [];

    public bool IsEditing => !IsFinished;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isBusy;

    /// <summary>The tool in use (what a press on the picture does).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelect), nameof(IsPen), nameof(IsArrow), nameof(IsBox), nameof(IsHighlight), nameof(IsText), nameof(IsNumber))]
    private AnnotateMode _mode = AnnotateMode.Arrow;

    [ObservableProperty] private string _color;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsThin), nameof(IsMedium), nameof(IsThick))]
    private AnnotationWidth _width;

    public bool IsSelect => Mode == AnnotateMode.Select;
    public bool IsPen => Mode == AnnotateMode.Pen;
    public bool IsArrow => Mode == AnnotateMode.Arrow;
    public bool IsBox => Mode == AnnotateMode.Box;
    public bool IsHighlight => Mode == AnnotateMode.Highlight;
    public bool IsText => Mode == AnnotateMode.Text;
    public bool IsNumber => Mode == AnnotateMode.Number;
    public bool IsThin => Width == AnnotationWidth.Thin;
    public bool IsMedium => Width == AnnotationWidth.Medium;
    public bool IsThick => Width == AnnotationWidth.Thick;

    public bool CanUndo => Marks.CanUndo;
    public bool CanRedo => Marks.CanRedo;
    public bool HasSelection => Marks.Selected >= 0;

    public string SaveText => _loc[CurrentIndex < _files.Count - 1 ? "annotate.save_next" : "annotate.save"];

    public event Action? CloseRequested;

    partial void OnModeChanged(AnnotateMode value)
    {
        // A drawing tool leaves the selection: what it draws gets selected instead.
        if (value != AnnotateMode.Select)
            Marks.Select(-1);
    }

    private void OnMarksChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(HasSelection));
        SaveCommand.NotifyCanExecuteChanged();
        // A selected mark's colour and width show in the bar.
        if (Marks.SelectedMark is { } mark && (mark.Color != Color || mark.Width != Width))
        {
            Color = mark.Color;
            OnPropertyChanged(nameof(Color));
            Width = mark.Width;
            OnPropertyChanged(nameof(Width));
            OnPropertyChanged(nameof(IsThin));
            OnPropertyChanged(nameof(IsMedium));
            OnPropertyChanged(nameof(IsThick));
            UpdateSwatches();
        }
    }

    [RelayCommand]
    private void SetMode(AnnotateMode mode) => Mode = mode;

    /// <summary>The colour for new marks, and the selected mark's.</summary>
    [RelayCommand]
    internal void SetColor(string color)
    {
        Color = color;
        if (Marks.Selected >= 0 && Marks.SelectedMark!.Color != color)
            Marks.Change(Marks.Selected, mark => mark.Color = color);
        UpdateSwatches();
    }

    /// <summary>The width for new marks, and the selected mark's.</summary>
    [RelayCommand]
    private void SetWidth(AnnotationWidth width)
    {
        Width = width;
        if (Marks.Selected >= 0 && Marks.SelectedMark!.Width != width)
            Marks.Change(Marks.Selected, mark => mark.Width = width);
    }

    private void UpdateSwatches()
    {
        foreach (var swatch in Swatches)
            swatch.IsSelected = swatch.Color == Color;
    }

    /// <summary>A new text typed at <paramref name="at"/>; nothing for blank text.</summary>
    public void AddText(AnnotationPoint at, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        Marks.Add(new Annotation { Kind = AnnotationKind.Text, Color = Color, Width = Width, Points = [at], Text = text.Trim() });
        // Typed with the Text tool, not picked: the next colour goes to the next mark.
        if (Mode != AnnotateMode.Select)
            Marks.Select(-1);
    }

    /// <summary>Text mark <paramref name="index"/> typed again; blank text removes it.</summary>
    public void EditText(int index, string text)
    {
        if (index < 0 || index >= Marks.Count)
            return;
        Marks.Select(index);
        if (string.IsNullOrWhiteSpace(text))
            Marks.RemoveSelected();
        else if (Marks.Marks[index].Text != text.Trim())
            Marks.Change(index, mark => mark.Text = text.Trim());
    }

    [RelayCommand]
    private void Undo() => Marks.Undo();

    [RelayCommand]
    private void Redo() => Marks.Redo();

    [RelayCommand]
    private void RemoveSelected() => Marks.RemoveSelected();

    // ───────── Pictures and saving ─────────

    /// <summary>
    /// Opens picture <paramref name="index"/> with no marks; past the last one, closes, or shows the summary when a
    /// picture could not be opened or saved.
    /// </summary>
    public async Task LoadFileAsync(int index)
    {
        if (_closed)
            return;
        if (index >= _files.Count)
        {
            Finish();
            return;
        }
        IsBusy = true;
        CurrentIndex = index;
        var path = _files[index];
        CurrentFileName = Path.GetFileName(path);
        CounterText = _loc.Format("crop.page_counter", index + 1, _files.Count);
        OnPropertyChanged(nameof(SaveText));
        Marks.Reset();
        try
        {
            var (preview, width, height) = await HideService.LoadPreviewAsync(path, _preset);
            Bitmap bitmap;
            using (preview)
                bitmap = await Task.Run(() => HideService.ToBitmap(preview));
            if (_closed)
            {
                bitmap.Dispose();
                return;
            }
            var old = PreviewImage;
            PictureSize = new Avalonia.PixelSize(width, height);
            PreviewImage = bitmap;
            old?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogInformation(ex, "Could not open {File} to annotate", CurrentFileName);
            AddProblem(index, ex is NotSupportedException ? "error.unsupported_source" : "error.conversion_failed");
            IsBusy = false;
            await LoadFileAsync(index + 1);
            return;
        }
        IsBusy = false;
    }

    /// <summary>
    /// Only with a mark on the picture, as "Pixelate / blur" only with a part: Skip moves on without one.
    /// </summary>
    private bool CanSave() => !IsBusy && PreviewImage is not null && Marks.Marks.Any(AnnotateTool.IsDrawable);

    /// <summary>
    /// Starts the conversion that writes the picture with its marks (shown in the toast), keeps the colour and width,
    /// and opens the next picture or closes. When it can't be started, the picture stays with its marks, to save again
    /// or skip.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var path = _files[CurrentIndex];
        Remember();
        try
        {
            _export(path, AnnotateTool.ConversionOf(_preset, _targetFor(path), Marks.Marks));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogError(ex, "Could not start annotating {File}", CurrentFileName);
            AddProblem(CurrentIndex, "error.conversion_failed");
            return;
        }
        _outcomes[CurrentIndex] = CropItemState.Done;
        ClearProblems(CurrentIndex);
        await LoadFileAsync(CurrentIndex + 1);
    }

    [RelayCommand]
    private async Task SkipAsync()
    {
        if (IsBusy || IsFinished)
            return;
        _outcomes[CurrentIndex] ??= CropItemState.Skipped;
        await LoadFileAsync(CurrentIndex + 1);
    }

    private void AddProblem(int index, string key)
    {
        _outcomes[index] = CropItemState.Failed;
        var line = $"{Path.GetFileName(_files[index])}: {_loc[key]}";
        Problems.Add(line);
        _problemFiles.Add(index);
        StatusNotice = line;
    }

    /// <summary>A picture saved after all: what went wrong with it before is no failure any more.</summary>
    private void ClearProblems(int index)
    {
        for (var i = _problemFiles.Count - 1; i >= 0; i--)
        {
            if (_problemFiles[i] != index)
                continue;
            _problemFiles.RemoveAt(i);
            Problems.RemoveAt(i);
        }
        StatusNotice = Problems.LastOrDefault() ?? "";
    }

    /// <summary>The picture each of <see cref="Problems"/> is about.</summary>
    private readonly List<int> _problemFiles = [];

    /// <summary>
    /// Closes after the last picture, or stays open on the summary when a picture could not be opened or saved.
    /// </summary>
    private void Finish()
    {
        if (Problems.Count == 0)
        {
            Close();
            return;
        }
        Remember();
        FailedCount = _outcomes.Count(o => o == CropItemState.Failed);
        DoneCount = _outcomes.Count(o => o == CropItemState.Done);
        SkippedCount = _outcomes.Count(o => o == CropItemState.Skipped);
        Summary = _loc.Format("toast.done_errors", FailedCount, _files.Count);
        StatusNotice = "";
        HasMultipleFiles = false;
        IsBusy = false;
        Marks.Reset();
        var old = PreviewImage;
        PreviewImage = null;
        old?.Dispose();
        IsFinished = true;
    }

    [RelayCommand]
    private void Close()
    {
        Remember();
        CloseRequested?.Invoke();
    }

    private void Remember()
    {
        AnnotateTool.Remember(_preset, Color, Width);
        if (_store.FindPreset(_preset.Id) is not { IsAnnotateTool: true } saved
            || (saved.Image.AnnotationColor == Color && saved.Image.AnnotationWidth == Width))
            return;
        AnnotateTool.Remember(saved, Color, Width);
        try
        {
            _store.SaveLibrary();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning(ex, "Could not remember the annotation style");
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (CurrentIndex < _files.Count)
            CounterText = _loc.Format("crop.page_counter", CurrentIndex + 1, _files.Count);
        OnPropertyChanged(nameof(SaveText));
        foreach (var swatch in Swatches)
            swatch.Name = _loc[swatch.NameKey];
    }

    public void Dispose()
    {
        // Closed with the title bar or Alt+F4 too: the colour and width are kept for next time.
        if (!_closed && PreviewImage is not null)
            Remember();
        _closed = true;
        _loc.LanguageChanged -= OnLanguageChanged;
        Marks.Changed -= OnMarksChanged;
        PreviewImage?.Dispose();
        PreviewImage = null;
    }
}

/// <summary>A colour in the bar, named for screen readers and its tooltip.</summary>
public sealed partial class AnnotateSwatch(string color, string nameKey, AnnotateViewModel owner) : ObservableObject
{
    public string Color { get; } = color;
    public string NameKey { get; } = nameKey;

    [ObservableProperty] private string _name = "";
    public Avalonia.Media.IBrush Brush { get; } = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(color));

    [ObservableProperty] private bool _isSelected;

    [RelayCommand]
    private void Choose() => owner.SetColor(Color);
}
