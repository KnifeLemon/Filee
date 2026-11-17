using Avalonia.Headless.XUnit;
using Filee.App.Services;
using Filee.Core.Localization;
using Filee.Core.Platform;
using Filee.Core.Presets;

namespace Filee.App.Tests;

public class AskSaveLocationTests
{
    private sealed class TrackingPlatformServices : IPlatformServices
    {
        public int MoveToActiveSpaceCalls { get; private set; }
        public nint LastHandle { get; private set; }

        public bool SupportsGlobalPointerGestures => false;
        public bool SupportsSelectionShortcut => false;
        public bool IsFileManagerAt(int x, int y) => false;
        public string? ProcessNameAt(int x, int y) => null;
        public IReadOnlyList<string> GetFileManagerSelection() => [];
        public int SystemDragThreshold => 4;
        public bool PrefersReducedMotion => false;
        public void SetStartWithSystem(bool enabled, string executablePath) { }
        public void SetContextMenu(bool enabled, string executablePath, string label) { }
        public ModernContextMenuState GetModernContextMenuState(string executablePath) => ModernContextMenuState.Unsupported;
        public Task<ModernContextMenuResult> SetModernContextMenuAsync(bool enabled, string executablePath) => Task.FromResult(new ModernContextMenuResult(false));
        public void RevealInFileManager(string path) { }
        public void OpenFile(string path) { }
        public void RaiseTopmost(nint windowHandle) { }
        public void MoveToActiveSpace(nint windowHandle)
        {
            MoveToActiveSpaceCalls++;
            LastHandle = windowHandle;
        }
    }

    private sealed class DelegatingServiceProvider(IServiceProvider inner, IPlatformServices platform) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IPlatformServices) ? platform : inner.GetService(serviceType);
    }

    [AvaloniaFact]
    public async Task AskSaveLocationAsync_returns_null_and_cancels_cleanly_when_dialog_is_cancelled()
    {
        TestServices.EnsureInitialized();
        var trackingPlatform = new TrackingPlatformServices();
        var provider = new DelegatingServiceProvider(AppHost.Services, trackingPlatform);
        var windowService = new WindowService(AppHost.Get<ILocalizer>(), provider, AppHost.Get<MemoryTrimmer>());

        var tempFile = Path.Combine(Path.GetTempPath(), "test_" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(tempFile, "sample");
        try
        {
            var preset = new Preset { Id = "test-preset", TargetFormat = "jpg", Output = { AskEveryTime = true } };
            var result = await windowService.AskSaveLocationAsync([tempFile], preset, oneResult: true);

            // In headless environment without user interaction, picker returns null (cancelled)
            Assert.Null(result);
            Assert.Equal(1, trackingPlatform.MoveToActiveSpaceCalls);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [AvaloniaFact]
    public async Task AskSaveLocationAsync_handles_folder_picker_mode_cleanly()
    {
        TestServices.EnsureInitialized();
        var trackingPlatform = new TrackingPlatformServices();
        var provider = new DelegatingServiceProvider(AppHost.Services, trackingPlatform);
        var windowService = new WindowService(AppHost.Get<ILocalizer>(), provider, AppHost.Get<MemoryTrimmer>());

        var tempFile = Path.Combine(Path.GetTempPath(), "test_" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(tempFile, "sample");
        try
        {
            var preset = new Preset { Id = "test-preset", TargetFormat = "jpg", Output = { AskEveryTime = true } };
            var result = await windowService.AskSaveLocationAsync([tempFile], preset, oneResult: false);

            Assert.Null(result);
            Assert.Equal(1, trackingPlatform.MoveToActiveSpaceCalls);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [AvaloniaFact]
    public async Task AskSaveLocationAsync_handles_empty_sources_or_root_path_gracefully()
    {
        TestServices.EnsureInitialized();
        var trackingPlatform = new TrackingPlatformServices();
        var provider = new DelegatingServiceProvider(AppHost.Services, trackingPlatform);
        var windowService = new WindowService(AppHost.Get<ILocalizer>(), provider, AppHost.Get<MemoryTrimmer>());

        var preset = new Preset { Id = "test-preset", Kind = PresetKind.CopyToFolder, TargetFormat = "jpg", Output = { AskEveryTime = true } };

        // Empty sources returns null immediately without opening a host window
        var resultEmpty = await windowService.AskSaveLocationAsync([], preset, oneResult: true);
        Assert.Null(resultEmpty);
        Assert.Equal(0, trackingPlatform.MoveToActiveSpaceCalls);

        // Root path "/" falls back to DownloadsFolder without crashing and configures space behavior
        var resultRoot = await windowService.AskSaveLocationAsync(["/"], preset, oneResult: true);
        Assert.Null(resultRoot);
        Assert.Equal(1, trackingPlatform.MoveToActiveSpaceCalls);
    }
}
