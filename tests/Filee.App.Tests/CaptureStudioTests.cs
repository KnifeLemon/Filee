using Avalonia;
using Filee.App.Services.Capture;
using Filee.App.ViewModels.Capture;
using Filee.Core.Localization;
using Xunit;

namespace Filee.App.Tests;

public sealed class CaptureStudioTests
{
    private sealed class DummyLocalizer : ILocalizer
    {
        public string Language => "en";
        public string this[string key] => key;
        public string Format(string key, params object[] args) => string.Format(key, args);
        public event EventHandler? LanguageChanged { add { } remove { } }
    }

    [Fact]
    public void WindowTargetService_returns_screens_and_audio_devices()
    {
        var service = new WindowTargetService();
        var screens = service.GetScreens();
        Assert.NotEmpty(screens);

        var devices = service.GetAudioInputDevices();
        Assert.NotEmpty(devices);
    }

    [Fact]
    public void CaptureStudioConfigViewModel_builds_expected_config()
    {
        var targetService = new WindowTargetService();
        var loc = new DummyLocalizer();
        var vm = new CaptureStudioConfigViewModel(targetService, loc);

        vm.SelectedSourceIndex = 1; // Full Screen
        vm.SelectedResolution = "1080p (Full HD)";
        vm.SelectedFramerate = 60;
        vm.EnableMicrophone = true;
        vm.EnableSystemAudio = false;
        vm.HighlightCursor = true;
        vm.SpotlightMode = true;

        var config = vm.BuildConfig();
        Assert.Equal(CaptureSourceType.FullScreen, config.SourceType);
        Assert.Equal(CaptureResolutionPreset.Res1080p, config.Resolution);
        Assert.Equal(60, config.Framerate);
        Assert.True(config.EnableMicrophone);
        Assert.False(config.EnableSystemAudio);
        Assert.True(config.HighlightCursor);
        Assert.True(config.SpotlightMode);
    }

    [Fact]
    public void CaptureStudioBarViewModel_toggles_and_emits_tool_events()
    {
        var targetService = new WindowTargetService();
        var mirrorService = new MobileMirrorService();
        var engine = new CaptureRecordingEngine();
        var studio = new CaptureStudioService(targetService, engine, mirrorService);

        var config = new CaptureConfig { HighlightCursor = true, SpotlightMode = false };
        var vm = new CaptureStudioBarViewModel(studio, config);

        AnnotationTool? chosen = null;
        vm.ToolSelected += t => chosen = t;

        vm.SelectToolCommand.Execute("pen");
        Assert.Equal(AnnotationTool.Pen, vm.SelectedTool);
        Assert.Equal(AnnotationTool.Pen, chosen);

        vm.SelectToolCommand.Execute("blur");
        Assert.Equal(AnnotationTool.Blur, vm.SelectedTool);

        bool spotlightNotified = false;
        vm.SpotlightToggled += val => spotlightNotified = val;
        vm.ToggleSpotlightCommand.Execute(null);
        Assert.True(vm.IsSpotlightActive);
        Assert.True(spotlightNotified);

        string? colorChosen = null;
        vm.ColorSelected += c => colorChosen = c;
        vm.SelectColorCommand.Execute("#007AFF");
        Assert.Equal("#007AFF", vm.SelectedColorHex);
        Assert.Equal("#007AFF", colorChosen);

        vm.ToggleCollapseCommand.Execute(null);
        Assert.True(vm.IsCollapsed);
    }

    [Fact]
    public void MobileMirrorService_handles_streaming_state_and_frames()
    {
        var service = new MobileMirrorService();
        Assert.False(service.IsStreaming);
        Assert.Equal(0, service.FrameCount);

        bool stateNotified = false;
        service.StateChanged += s => stateNotified = s;

        service.OnConnected();
        Assert.True(service.IsStreaming);
        Assert.True(stateNotified);

        byte[]? received = null;
        service.FrameReceived += bytes => received = bytes;

        var sampleFrame = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        service.OnFrame(sampleFrame);

        Assert.Equal(1, service.FrameCount);
        Assert.Equal(sampleFrame, service.LastJpegFrame);
        Assert.NotNull(received);

        service.OnDisconnected();
        Assert.False(service.IsStreaming);
    }

    [Fact]
    public void CaptureStudioEditorViewModel_subtitles_and_trim_operations()
    {
        var targetService = new WindowTargetService();
        var mirrorService = new MobileMirrorService();
        var engine = new CaptureRecordingEngine();
        var studio = new CaptureStudioService(targetService, engine, mirrorService);
        var loc = new DummyLocalizer();

        var meta = new RecordedVideoMetadata(
            "dummy_path.mp4",
            TimeSpan.FromSeconds(60),
            1920,
            1080,
            30,
            true);

        var vm = new CaptureStudioEditorViewModel(studio, loc, meta);
        Assert.Equal(60, vm.VideoDuration.TotalSeconds);
        Assert.Equal(0, vm.TrimStart);
        Assert.Equal(60, vm.TrimEnd);

        // Add subtitle
        vm.CurrentPosition = 10;
        vm.AddSubtitleCommand.Execute(null);
        Assert.Single(vm.Subtitles);
        Assert.Equal(TimeSpan.FromSeconds(10), vm.Subtitles[0].Start);

        // Split at playhead
        vm.CurrentPosition = 35;
        vm.SplitAtPlayheadCommand.Execute(null);
        Assert.Equal(35, vm.TrimEnd);

        // Delete subtitle
        vm.SelectedSubtitle = vm.Subtitles[0];
        vm.DeleteSubtitleCommand.Execute(null);
        Assert.Empty(vm.Subtitles);
    }

    [Fact]
    public void CaptureStudioBarViewModel_mic_toggle_notifies()
    {
        var targetService = new WindowTargetService();
        var mirrorService = new MobileMirrorService();
        var engine = new CaptureRecordingEngine();
        var studio = new CaptureStudioService(targetService, engine, mirrorService);

        var config = new CaptureConfig { EnableMicrophone = true };
        var vm = new CaptureStudioBarViewModel(studio, config);

        bool? micMutedNotified = null;
        vm.MicToggled += m => micMutedNotified = m;

        Assert.False(vm.IsMicMuted);
        vm.ToggleMicCommand.Execute(null);
        Assert.True(vm.IsMicMuted);
        Assert.True(micMutedNotified);

        vm.ToggleMicCommand.Execute(null);
        Assert.False(vm.IsMicMuted);
        Assert.False(micMutedNotified);
    }

    [Fact]
    public void CaptureStudioConfigViewModel_updates_detected_app_when_selected_window_changes()
    {
        var targetService = new WindowTargetService();
        var loc = new DummyLocalizer();
        var vm = new CaptureStudioConfigViewModel(targetService, loc);

        var testWindow = new WindowTargetInfo(
            (nint)12345,
            "Visual Studio Code",
            "Code.exe",
            0,
            0,
            1920,
            1080);

        vm.SelectedWindow = testWindow;
        Assert.Equal("Code.exe - Visual Studio Code", vm.DetectedAppTitle);
        Assert.Equal("Code.exe", vm.DetectedAppProcess);
    }

    [Fact]
    public void CaptureStudioEditorViewModel_active_subtitle_updates_on_position_change()
    {
        var targetService = new WindowTargetService();
        var mirrorService = new MobileMirrorService();
        var engine = new CaptureRecordingEngine();
        var studio = new CaptureStudioService(targetService, engine, mirrorService);
        var loc = new DummyLocalizer();

        var meta = new RecordedVideoMetadata(
            "test.mp4",
            TimeSpan.FromSeconds(60),
            1920,
            1080,
            30,
            true);

        var vm = new CaptureStudioEditorViewModel(studio, loc, meta);
        vm.Subtitles.Add(new SubtitleCueModel
        {
            Index = 1,
            Start = TimeSpan.FromSeconds(5),
            End = TimeSpan.FromSeconds(15),
            Text = "Hello world caption"
        });

        vm.CurrentPosition = 2;
        Assert.Null(vm.ActiveSubtitleText);

        vm.CurrentPosition = 10;
        Assert.Equal("Hello world caption", vm.ActiveSubtitleText);

        vm.CurrentPosition = 20;
        Assert.Null(vm.ActiveSubtitleText);
    }

    [Fact]
    public void CaptureStudioBarViewModel_icons_and_pause_toggle_state()
    {
        var targetService = new WindowTargetService();
        var mirrorService = new MobileMirrorService();
        var engine = new CaptureRecordingEngine();
        var studio = new CaptureStudioService(targetService, engine, mirrorService);

        var config = new CaptureConfig { EnableMicrophone = true };
        var vm = new CaptureStudioBarViewModel(studio, config);

        // Microphone icon toggle
        Assert.Equal("🎙", vm.MicButtonIcon);
        vm.ToggleMicCommand.Execute(null);
        Assert.Equal("🔇", vm.MicButtonIcon);

        // Pause / Resume icon and text toggle
        Assert.Equal("⏸", vm.PauseButtonIcon);
        Assert.Equal("Pause", vm.PauseButtonText);
        vm.TogglePauseCommand.Execute(null);
        Assert.Equal("▶", vm.PauseButtonIcon);
        Assert.Equal("Resume", vm.PauseButtonText);
    }

    [Fact]
    public void CaptureStudioConfigViewModel_source_radio_buttons_update_state()
    {
        var targetService = new WindowTargetService();
        var loc = new DummyLocalizer();
        var vm = new CaptureStudioConfigViewModel(targetService, loc);

        vm.IsAppWindowSource = true;
        Assert.Equal(0, vm.SelectedSourceIndex);
        Assert.True(vm.IsAppWindowSource);
        Assert.False(vm.IsFullScreenSource);
        Assert.False(vm.IsCustomRegionSource);

        vm.IsFullScreenSource = true;
        Assert.Equal(1, vm.SelectedSourceIndex);
        Assert.True(vm.IsFullScreenSource);
        Assert.False(vm.IsAppWindowSource);

        vm.IsCustomRegionSource = true;
        Assert.Equal(2, vm.SelectedSourceIndex);
        Assert.True(vm.IsCustomRegionSource);
        Assert.False(vm.IsFullScreenSource);
    }

    [Fact]
    public void CaptureStudioEditorViewModel_play_button_icon()
    {
        var targetService = new WindowTargetService();
        var mirrorService = new MobileMirrorService();
        var engine = new CaptureRecordingEngine();
        var studio = new CaptureStudioService(targetService, engine, mirrorService);
        var loc = new DummyLocalizer();

        var meta = new RecordedVideoMetadata(
            "dummy_path.mp4",
            TimeSpan.FromSeconds(60),
            1920,
            1080,
            30,
            true);

        var vm = new CaptureStudioEditorViewModel(studio, loc, meta);
        Assert.Equal("▶", vm.PlayButtonIcon);
        vm.TogglePlayCommand.Execute(null);
        Assert.Equal("⏸", vm.PlayButtonIcon);
    }
}
