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
        Swatches = [.. AnnotateTool.Colors.Select(c => new AnnotateSwatch(c, this))];
        Marks.Changed += OnMarksChanged;
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
    [ObservableProperty] private Bitmap? _previewImage;
    [ObservableProperty] private string _statusNotice = "";

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

    /// <summary>Opens picture <paramref name="index"/> with no marks; past the last one, closes.</summary>
    public async Task LoadFileAsync(int index)
    {
        if (_closed)
            return;
        if (index >= _files.Count)
        {
            CloseRequested?.Invoke();
            return;
        }
        IsBusy = true;
        CurrentIndex = index;
        var path = _files[index];
        CurrentFileName = Path.GetFileName(path);
        CounterText = _loc.Format("crop.page_counter", index + 1, _files.Count);
        OnPropertyChanged(nameof(SaveText));
        StatusNotice = "";
        Marks.Reset();
        try
        {
            var (preview, _, _) = await HideService.LoadPreviewAsync(path, _preset);
            Bitmap bitmap;
            using (preview)
                bitmap = await Task.Run(() => HideService.ToBitmap(preview));
            if (_closed)
            {
                bitmap.Dispose();
                return;
            }
            var old = PreviewImage;
            PreviewImage = bitmap;
            old?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogInformation(ex, "Could not open {File} to annotate", CurrentFileName);
            StatusNotice = $"{CurrentFileName}: {_loc[ex is NotSupportedException ? "error.unsupported_source" : "error.conversion_failed"]}";
            IsBusy = false;
            await LoadFileAsync(index + 1);
            return;
        }
        IsBusy = false;
    }

    private bool CanSave() => !IsBusy && PreviewImage is not null;

    /// <summary>
    /// Starts the conversion that writes the picture with its marks (shown in the toast), keeps the colour and width,
    /// opens the next picture or closes. A picture without marks is saved as it is drawn: a copy.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var path = _files[CurrentIndex];
        Remember();
        if (Marks.Marks.Any(AnnotateTool.IsDrawable))
        {
            try
            {
                _export(path, AnnotateTool.ConversionOf(_preset, _targetFor(path), Marks.Marks));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log?.LogError(ex, "Could not start annotating {File}", CurrentFileName);
                StatusNotice = $"{CurrentFileName}: {_loc["error.conversion_failed"]}";
            }
        }
        await LoadFileAsync(CurrentIndex + 1);
    }

    [RelayCommand]
    private async Task SkipAsync()
    {
        if (!IsBusy)
            await LoadFileAsync(CurrentIndex + 1);
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

    public void Dispose()
    {
        _closed = true;
        Marks.Changed -= OnMarksChanged;
        PreviewImage?.Dispose();
        PreviewImage = null;
    }
}

/// <summary>A colour in the bar.</summary>
public sealed partial class AnnotateSwatch(string color, AnnotateViewModel owner) : ObservableObject
{
    public string Color { get; } = color;
    public Avalonia.Media.IBrush Brush { get; } = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(color));

    [ObservableProperty] private bool _isSelected;

    [RelayCommand]
    private void Choose() => owner.SetColor(Color);
}
