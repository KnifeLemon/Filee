using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services.Capture;

namespace Filee.App.ViewModels.Capture;

public sealed partial class CaptureStudioBarViewModel : ObservableObject
{
    private readonly CaptureStudioService _service;

    [ObservableProperty]
    private bool _isRecording = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseButtonIcon))]
    [NotifyPropertyChangedFor(nameof(PauseButtonText))]
    private bool _isPaused = false;

    public string PauseButtonIcon => IsPaused ? "▶" : "⏸";
    public string PauseButtonText => IsPaused ? "Resume" : "Pause";

    [ObservableProperty]
    private bool _isCollapsed = false;

    [ObservableProperty]
    private string _elapsedTimeText = "00:00";

    [ObservableProperty]
    private AnnotationTool _selectedTool = AnnotationTool.Pointer;

    [ObservableProperty]
    private string _selectedColorHex = "#FF3B30";

    public ObservableCollection<string> PaletteColors { get; } =
    [
        "#FF3B30", // Red
        "#FF9500", // Orange
        "#FFCC00", // Yellow
        "#34C759", // Green
        "#007AFF", // Blue
        "#AF52DE", // Purple
        "#FFFFFF", // White
        "#1C1C1E"  // Black / Dark
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MicButtonIcon))]
    private bool _isMicMuted = false;

    partial void OnIsMicMutedChanged(bool value)
    {
        MicToggled?.Invoke(value);
    }

    public string MicButtonIcon => IsMicMuted ? "🔇" : "🎙";

    [ObservableProperty]
    private bool _isSystemAudioMuted = false;

    [ObservableProperty]
    private bool _isCursorHighlightActive = true;

    partial void OnIsCursorHighlightActiveChanged(bool value)
    {
        CursorHighlightToggled?.Invoke(value);
    }

    [ObservableProperty]
    private bool _isSpotlightActive = false;

    partial void OnIsSpotlightActiveChanged(bool value)
    {
        SpotlightToggled?.Invoke(value);
    }

    [ObservableProperty]
    private double _spotlightRadius = 120.0;

    [ObservableProperty]
    private bool _isMobileMirrorOpen = false;

    public event Action? PauseToggled;
    public event Action? StopRequested;
    public event Action<AnnotationTool>? ToolSelected;
    public event Action<string>? ColorSelected;
    public event Action? ClearAnnotationsRequested;
    public event Action<bool>? CursorHighlightToggled;
    public event Action<bool>? SpotlightToggled;
    public event Action<bool>? MicToggled;
    public event Action? MobileMirrorToggled;

    public CaptureStudioBarViewModel(CaptureStudioService service, CaptureConfig config)
    {
        _service = service;
        _isCursorHighlightActive = config.HighlightCursor;
        _isSpotlightActive = config.SpotlightMode;
        _spotlightRadius = config.SpotlightRadius;
        _isMicMuted = !config.EnableMicrophone;
        _isSystemAudioMuted = !config.EnableSystemAudio;

        _service.RecordingEngine.ElapsedTimeTicked += OnTimeTicked;
    }

    private void OnTimeTicked(TimeSpan ts)
    {
        ElapsedTimeText = ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    [RelayCommand]
    private async Task TogglePause()
    {
        if (IsPaused)
        {
            await _service.ResumeRecordingAsync().ConfigureAwait(false);
            IsPaused = false;
        }
        else
        {
            await _service.PauseRecordingAsync().ConfigureAwait(false);
            IsPaused = true;
        }
        PauseToggled?.Invoke();
    }

    [RelayCommand]
    private void StopRecording()
    {
        IsRecording = false;
        _service.RecordingEngine.ElapsedTimeTicked -= OnTimeTicked;
        StopRequested?.Invoke();
    }

    [RelayCommand]
    private void ToggleCollapse()
    {
        IsCollapsed = !IsCollapsed;
    }

    [RelayCommand]
    private void SelectColor(string hex)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            SelectedColorHex = hex;
            ColorSelected?.Invoke(hex);
        }
    }

    [RelayCommand]
    private void SelectTool(string toolName)
    {
        var tool = toolName.ToLowerInvariant() switch
        {
            "pen" => AnnotationTool.Pen,
            "arrow" => AnnotationTool.Arrow,
            "rectangle" or "rect" => AnnotationTool.Rectangle,
            "circle" => AnnotationTool.Circle,
            "blur" => AnnotationTool.Blur,
            "text" => AnnotationTool.TextCallout,
            _ => AnnotationTool.Pointer
        };

        SelectedTool = tool;
        ToolSelected?.Invoke(tool);
    }

    [RelayCommand]
    private void ToggleCursorHighlight()
    {
        IsCursorHighlightActive = !IsCursorHighlightActive;
    }

    [RelayCommand]
    private void ToggleSpotlight()
    {
        IsSpotlightActive = !IsSpotlightActive;
    }

    [RelayCommand]
    private void ToggleMic()
    {
        IsMicMuted = !IsMicMuted;
    }

    [RelayCommand]
    private void ToggleSystemAudio()
    {
        IsSystemAudioMuted = !IsSystemAudioMuted;
    }

    [RelayCommand]
    private void ToggleMobileMirror()
    {
        IsMobileMirrorOpen = !IsMobileMirrorOpen;
        MobileMirrorToggled?.Invoke();
    }

    [RelayCommand]
    private void ClearAnnotations()
    {
        ClearAnnotationsRequested?.Invoke();
    }
}
