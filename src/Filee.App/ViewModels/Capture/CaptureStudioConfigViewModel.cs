using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services.Capture;
using Filee.Core.Localization;

namespace Filee.App.ViewModels.Capture;

public sealed partial class CaptureStudioConfigViewModel : ObservableObject
{
    private readonly WindowTargetService _targetService;
    private readonly ILocalizer _loc;

    [ObservableProperty]
    private int _selectedSourceIndex = 0; // 0: App Window, 1: Full Screen, 2: Custom Region

    [ObservableProperty]
    private bool _isAppWindowSource = true;

    [ObservableProperty]
    private bool _isFullScreenSource = false;

    [ObservableProperty]
    private bool _isCustomRegionSource = false;

    partial void OnIsAppWindowSourceChanged(bool value)
    {
        if (value)
        {
            SelectedSourceIndex = 0;
            _isFullScreenSource = false;
            _isCustomRegionSource = false;
            OnPropertyChanged(nameof(IsFullScreenSource));
            OnPropertyChanged(nameof(IsCustomRegionSource));
        }
    }

    partial void OnIsFullScreenSourceChanged(bool value)
    {
        if (value)
        {
            SelectedSourceIndex = 1;
            _isAppWindowSource = false;
            _isCustomRegionSource = false;
            OnPropertyChanged(nameof(IsAppWindowSource));
            OnPropertyChanged(nameof(IsCustomRegionSource));
        }
    }

    partial void OnIsCustomRegionSourceChanged(bool value)
    {
        if (value)
        {
            SelectedSourceIndex = 2;
            _isAppWindowSource = false;
            _isFullScreenSource = false;
            OnPropertyChanged(nameof(IsAppWindowSource));
            OnPropertyChanged(nameof(IsFullScreenSource));
        }
    }

    [ObservableProperty]
    private WindowTargetInfo? _detectedWindow;

    [ObservableProperty]
    private bool _isAppTargetMode;

    [ObservableProperty]
    private bool _showAllSources;

    [ObservableProperty]
    private string _detectedAppTitle = "";

    [ObservableProperty]
    private string _detectedAppProcess = "";

    [ObservableProperty]
    private WindowTargetInfo? _selectedWindow;

    partial void OnSelectedWindowChanged(WindowTargetInfo? value)
    {
        if (value != null)
        {
            DetectedAppTitle = value.DisplayName;
            DetectedAppProcess = value.ProcessName;
        }
    }

    [ObservableProperty]
    private ScreenTargetInfo? _selectedScreen;

    [ObservableProperty]
    private string _selectedResolution = "1080p (Full HD)";

    [ObservableProperty]
    private int _selectedFramerate = 30;

    [ObservableProperty]
    private bool _enableMicrophone = true;

    [ObservableProperty]
    private bool _enableSystemAudio = true;

    [ObservableProperty]
    private string _selectedAudioDevice = "Default Microphone";

    [ObservableProperty]
    private bool _highlightCursor = false;

    [ObservableProperty]
    private bool _spotlightMode = false;

    [ObservableProperty]
    private bool _countdownBeforeRecording = true;

    public ObservableCollection<WindowTargetInfo> OpenWindows { get; } = [];
    public ObservableCollection<ScreenTargetInfo> Screens { get; } = [];
    public ObservableCollection<string> Resolutions { get; } = ["Native (Source)", "1080p (Full HD)", "720p (HD)", "2K (QHD)", "4K (UHD)"];
    public ObservableCollection<int> Framerates { get; } = [30, 60];
    public ObservableCollection<string> AudioDevices { get; } = [];

    public event Action<CaptureConfig>? RecordingRequested;
    public event Action? CancelRequested;

    public CaptureStudioConfigViewModel(WindowTargetService targetService, ILocalizer loc, int triggerX = 0, int triggerY = 0)
    {
        _targetService = targetService;
        _loc = loc;

        // Detect window under cursor
        _detectedWindow = _targetService.DetectWindowAtPoint(triggerX, triggerY);

        LoadSources();
    }

    private void LoadSources()
    {
        OpenWindows.Clear();
        var windows = _targetService.GetOpenWindows();
        foreach (var w in windows)
            OpenWindows.Add(w);

        if (DetectedWindow != null)
        {
            var match = OpenWindows.FirstOrDefault(w => w.Handle == DetectedWindow.Handle);
            SelectedWindow = match ?? DetectedWindow;
            SelectedSourceIndex = 0;
            IsAppWindowSource = true;
            IsFullScreenSource = false;
            IsCustomRegionSource = false;
            IsAppTargetMode = true;
            ShowAllSources = false;
            DetectedAppTitle = SelectedWindow.DisplayName;
            DetectedAppProcess = SelectedWindow.ProcessName;
        }
        else
        {
            SelectedWindow = OpenWindows.FirstOrDefault();
            SelectedSourceIndex = 1;
            IsAppWindowSource = false;
            IsFullScreenSource = true;
            IsCustomRegionSource = false;
            IsAppTargetMode = false;
            ShowAllSources = true;
        }

        Screens.Clear();
        var screens = _targetService.GetScreens();
        foreach (var s in screens)
            Screens.Add(s);
        SelectedScreen = Screens.FirstOrDefault(s => s.IsPrimary) ?? Screens.FirstOrDefault();

        AudioDevices.Clear();
        foreach (var d in _targetService.GetAudioInputDevices())
            AudioDevices.Add(d);
        SelectedAudioDevice = AudioDevices.FirstOrDefault() ?? "Default Microphone";
    }

    [RelayCommand]
    private void ToggleShowAllSources()
    {
        ShowAllSources = !ShowAllSources;
    }

    [RelayCommand]
    private void SwitchToAppMode()
    {
        if (DetectedWindow != null)
        {
            SelectedWindow = DetectedWindow;
            SelectedSourceIndex = 0;
            ShowAllSources = false;
        }
    }

    [RelayCommand]
    private void StartRecording()
    {
        var config = BuildConfig();
        RecordingRequested?.Invoke(config);
    }

    [RelayCommand]
    private void Cancel()
    {
        CancelRequested?.Invoke();
    }

    public CaptureConfig BuildConfig()
    {
        var sourceType = SelectedSourceIndex switch
        {
            0 => CaptureSourceType.Window,
            2 => CaptureSourceType.CustomRegion,
            _ => CaptureSourceType.FullScreen
        };

        var resPreset = SelectedResolution switch
        {
            "720p (HD)" => CaptureResolutionPreset.Res720p,
            "1080p (Full HD)" => CaptureResolutionPreset.Res1080p,
            "2K (QHD)" => CaptureResolutionPreset.Res2K,
            "4K (UHD)" => CaptureResolutionPreset.Res4K,
            _ => CaptureResolutionPreset.Native
        };

        return new CaptureConfig
        {
            SourceType = sourceType,
            TargetWindow = SelectedWindow,
            TargetScreen = SelectedScreen,
            Resolution = resPreset,
            Framerate = SelectedFramerate,
            EnableMicrophone = EnableMicrophone,
            EnableSystemAudio = EnableSystemAudio,
            MicrophoneDevice = SelectedAudioDevice,
            HighlightCursor = HighlightCursor,
            SpotlightMode = SpotlightMode,
            CountdownBeforeRecording = CountdownBeforeRecording
        };
    }
}
