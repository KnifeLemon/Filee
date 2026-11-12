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
    private bool _isPaused = false;

    [ObservableProperty]
    private string _elapsedTimeText = "00:00";

    [ObservableProperty]
    private AnnotationTool _selectedTool = AnnotationTool.Pointer;

    [ObservableProperty]
    private bool _isMicMuted = false;

    [ObservableProperty]
    private bool _isSystemAudioMuted = false;

    [ObservableProperty]
    private bool _isCursorHighlightActive = true;

    [ObservableProperty]
    private bool _isSpotlightActive = false;

    [ObservableProperty]
    private double _spotlightRadius = 120.0;

    [ObservableProperty]
    private bool _isMobileMirrorOpen = false;

    public event Action? PauseToggled;
    public event Action? StopRequested;
    public event Action<AnnotationTool>? ToolSelected;
    public event Action? ClearAnnotationsRequested;
    public event Action<bool>? CursorHighlightToggled;
    public event Action<bool>? SpotlightToggled;
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
        CursorHighlightToggled?.Invoke(IsCursorHighlightActive);
    }

    [RelayCommand]
    private void ToggleSpotlight()
    {
        IsSpotlightActive = !IsSpotlightActive;
        SpotlightToggled?.Invoke(IsSpotlightActive);
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
