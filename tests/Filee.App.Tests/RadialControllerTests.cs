// The drag-wait donut is an invisible, topmost window that catches clicks so a drag can reach it: it must close when
// the gesture ends without files, even after a conversion left the ring behind (issue: desktop icons stopped reacting
// to clicks until Filee quit).

using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Filee.App.Controls;
using Filee.App.Services;
using Filee.App.Services.Triggers;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Filee.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.App.Tests;

public class RadialControllerTests
{
    [AvaloniaFact]
    public void Hiding_the_donut_restores_native_configuration_before_it_is_reused()
    {
        TestServices.EnsureInitialized("en");
        var platform = new WindowPlatform();
        var radial = Create(platform);
        try
        {
            for (var i = 1; i <= 3; i++)
            {
                radial.ShowForDrag(400, 400);
                Assert.Equal(i - 1, platform.Restores);
                // Both an external Hide and the controller's close path must restore the native window.
                if (i == 1)
                    radial.Window!.Hide();
                else
                    radial.Close(animate: false);
                Assert.False(radial.Window!.IsVisible);
                Assert.Equal(i, platform.Restores);
            }
        }
        finally
        {
            radial.Window?.Close();
        }
    }

    [AvaloniaFact]
    public void Closing_a_visible_donut_restores_it_before_the_native_window_is_released()
    {
        TestServices.EnsureInitialized("en");
        var platform = new WindowPlatform();
        var radial = Create(platform);
        radial.ShowForDrag(400, 400);
        var window = radial.Window!;
        var checkedBeforeRelease = false;
        window.Closing += (_, _) =>
        {
            Assert.Equal(1, platform.Restores);
            Assert.NotNull(window.PlatformImpl);
            checkedBeforeRelease = true;
        };
        try
        {
            window.Close();
            Assert.True(checkedBeforeRelease);
            Assert.Null(window.PlatformImpl);
        }
        finally
        {
            if (window.PlatformImpl is not null)
                window.Close();
        }
    }

    [AvaloniaFact]
    public void Files_entering_the_donut_during_drag_takes_and_releases_key_window()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Requires macOS.");
            return;
        }

        TestServices.EnsureInitialized("en");
        var platform = new WindowPlatform();
        var radial = Create(platform);
        try
        {
            radial.ShowForDrag(400, 400);
            var window = radial.Window!;
            var handle = window.TryGetPlatformHandle()?.Handle ?? 0;
            Assert.Empty(platform.KeyWindowsTaken);
            Assert.Empty(platform.KeyWindowsReleased);

            var filesEntered = (Action<IReadOnlyList<string>>?)typeof(RadialWindow)
                .GetField("FilesEntered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?
                .GetValue(window);
            Assert.NotNull(filesEntered);

            filesEntered([@"/tmp/filee-test-drag.png"]);

            Assert.Equal([handle], platform.KeyWindowsTaken);
            Assert.Equal(1, platform.RememberFrontAppCalls);
            Assert.Empty(platform.KeyWindowsReleased);

            radial.Close(animate: false);

            Assert.Equal([handle], platform.KeyWindowsReleased);
            Assert.Equal(1, platform.ReturnFocusCalls);
        }
        finally
        {
            radial.Close(animate: false);
        }
    }

    [AvaloniaFact]
    public void Dropping_files_on_the_donut_releases_key_window_while_progress_ring_shows()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Requires macOS.");
            return;
        }

        TestServices.EnsureInitialized("en");
        var platform = new WindowPlatform();
        var radial = Create(platform);
        var tempFile = Path.Combine(Path.GetTempPath(), "filee-test-drop.png");
        File.WriteAllBytes(tempFile, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        try
        {
            radial.ShowForDrag(400, 400);
            var window = radial.Window!;
            var handle = window.TryGetPlatformHandle()?.Handle ?? 0;

            var filesEntered = (Action<IReadOnlyList<string>>?)typeof(RadialWindow)
                .GetField("FilesEntered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?
                .GetValue(window);
            Assert.NotNull(filesEntered);

            filesEntered([tempFile]);
            Assert.Equal([handle], platform.KeyWindowsTaken);
            Assert.Empty(platform.KeyWindowsReleased);

            var dropped = (Action<DonutHit, DraggedItems>?)typeof(RadialWindow)
                .GetField("Dropped", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?
                .GetValue(window);
            Assert.NotNull(dropped);

            var model = AppHost.Get<RadialViewModel>();
            var index = -1;
            for (var i = 0; i < model.Items.Count; i++)
            {
                var preset = model.PresetAt(i, -1);
                if (preset is not null && !preset.IsTool && !preset.Output.AskEveryTime)
                {
                    index = i;
                    break;
                }
            }

            Assert.True(index >= 0);
            dropped(new DonutHit(DonutHitKind.Segment, index, -1), new DraggedItems([tempFile], null));

            // Key window status is released immediately on drop so typing returns to previous app
            Assert.Equal([handle], platform.KeyWindowsReleased);
            Assert.Equal(1, platform.ReturnFocusCalls);
        }
        finally
        {
            radial.Close(animate: false);
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [AvaloniaFact]
    public void Drop_opening_owned_window_keeps_keyboard_for_it()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Requires macOS.");
            return;
        }

        TestServices.EnsureInitialized("en");
        var store = AppHost.Get<UserDataStore>();
        var profile = store.Profiles.First(p => p.Id == "images");
        var beforePresets = profile.PresetIds;
        profile.PresetIds = ["tool-crop", ..beforePresets];
        var platform = new WindowPlatform();
        var radial = Create(platform);
        var tempFile = Path.Combine(Path.GetTempPath(), "filee-test-tool.png");
        File.WriteAllBytes(tempFile, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        try
        {
            radial.ShowForDrag(400, 400);
            var window = radial.Window!;
            var handle = window.TryGetPlatformHandle()?.Handle ?? 0;

            var filesEntered = (Action<IReadOnlyList<string>>?)typeof(RadialWindow)
                .GetField("FilesEntered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?
                .GetValue(window);
            Assert.NotNull(filesEntered);

            filesEntered([tempFile]);
            Assert.Equal([handle], platform.KeyWindowsTaken);
            Assert.Empty(platform.KeyWindowsReleased);

            var dropped = (Action<DonutHit, DraggedItems>?)typeof(RadialWindow)
                .GetField("Dropped", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?
                .GetValue(window);
            Assert.NotNull(dropped);

            var model = AppHost.Get<RadialViewModel>();
            var toolIndex = -1;
            for (var i = 0; i < model.Items.Count; i++)
            {
                var preset = model.PresetAt(i, -1);
                if (preset is not null && preset.IsTool && !preset.IsCopyToFolder)
                {
                    toolIndex = i;
                    break;
                }
            }

            Assert.True(toolIndex >= 0);
            dropped(new DonutHit(DonutHitKind.Segment, toolIndex, -1), new DraggedItems([tempFile], null));

            // Keyboard stays with Filee for the tool window
            Assert.Empty(platform.KeyWindowsReleased);
            Assert.Equal(0, platform.ReturnFocusCalls);
        }
        finally
        {
            profile.PresetIds = beforePresets;
            radial.Close(animate: false);
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    private static RadialController Create(IPlatformServices platform) =>
        new(AppHost.Get<RadialViewModel>(), AppHost.Get<ConversionService>(), AppHost.Get<WindowService>(),
            AppHost.Get<UserDataStore>(), platform, AppHost.Get<TriggerService>(), NullLogger<RadialController>.Instance);

    private sealed class WindowPlatform : IPlatformServices
    {
        public int Restores { get; private set; }
        public List<nint> KeyWindowsTaken { get; } = [];
        public List<nint> KeyWindowsReleased { get; } = [];
        public int RememberFrontAppCalls { get; private set; }
        public int ReturnFocusCalls { get; private set; }

        public void RestoreWindow(nint windowHandle) => Restores++;
        public void RaiseTopmost(nint windowHandle) { }
        public void TakeKeyWindow(nint windowHandle) => KeyWindowsTaken.Add(windowHandle);
        public void ReleaseKeyWindow(nint windowHandle) => KeyWindowsReleased.Add(windowHandle);
        public void RememberFrontApp() => RememberFrontAppCalls++;
        public void ReturnFocus() => ReturnFocusCalls++;
        public bool IsFileManagerAt(int x, int y) => false;
        public string? ProcessNameAt(int x, int y) => null;
        public IReadOnlyList<string> GetFileManagerSelection() => [];
        public int SystemDragThreshold => 4;
        public bool PrefersReducedMotion => false;
        public void SetStartWithSystem(bool enabled, string executablePath) { }
        public void SetContextMenu(bool enabled, string executablePath, string label) { }
        public ModernContextMenuState GetModernContextMenuState(string executablePath) => ModernContextMenuState.Unsupported;
        public Task<ModernContextMenuResult> SetModernContextMenuAsync(bool enabled, string executablePath) =>
            Task.FromResult(new ModernContextMenuResult(false));
        public void RevealInFileManager(string path) { }
        public void OpenFile(string path) { }
    }

    /// <summary>A conversion ran through the donut and its ring finished: progress mode stays on, nothing is pending.</summary>
    private static void FinishARing(DonutMenu donut)
    {
        donut.BeginProgress();
        Motion.Set(false);
        try
        {
            donut.HandOffProgress(() => { });
        }
        finally
        {
            Motion.Set(true);
        }
        Assert.True(donut.IsInProgressMode);
        Assert.False(donut.IsFinishingProgress);
    }

    [AvaloniaFact]
    public void A_drag_without_files_after_a_conversion_closes_the_invisible_window()
    {
        TestServices.EnsureInitialized("en");
        var radial = AppHost.Get<RadialController>();
        radial.ShowForDrag(400, 400);
        FinishARing(radial.Window!.DonutControl);

        // A later Ctrl+drag that brings no files: a rubber-band selection on the desktop, a folder.
        radial.ShowForDrag(600, 500);
        Assert.False(radial.Window.DonutControl.IsInProgressMode);
        radial.OnDragGestureEnded();

        Assert.False(radial.IsVisible);
        Assert.False(radial.Window.IsVisible);
    }

    [AvaloniaFact]
    public void Esc_closes_a_donut_whose_ring_already_finished()
    {
        TestServices.EnsureInitialized("en");
        var radial = AppHost.Get<RadialController>();
        radial.ShowForDrag(400, 400);
        FinishARing(radial.Window!.DonutControl);

        radial.Close(animate: false);

        Assert.False(radial.IsVisible);
        Assert.False(radial.Window.IsVisible);
    }

    [AvaloniaFact]
    public void Click_mode_focuses_the_donut_for_tab_flip()
    {
        TestServices.EnsureInitialized("en");
        var radial = AppHost.Get<RadialController>();
        try
        {
            radial.ShowForFiles(400, 400, [@"C:\photos\a.jpg"]);
            var window = radial.Window!;

            Assert.True(window.DonutControl.IsFocused);
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Assert.True(AppHost.Get<Filee.App.ViewModels.RadialViewModel>().IsBackFace);
        }
        finally
        {
            radial.Close(animate: false);
        }
    }

    [AvaloniaFact]
    public void A_later_launch_joins_the_donut_open_for_earlier_ones()
    {
        // Issue #767: launches more than the debounce apart each replaced the donut's files, so one picture was converted.
        TestServices.EnsureInitialized("en");
        var radial = AppHost.Get<RadialController>();
        var model = AppHost.Get<RadialViewModel>();
        try
        {
            radial.ShowForFiles(400, 400, [@"C:\photos\a.jpg"], forAnotherProcess: true);

            Assert.True(radial.TryAddFiles([@"C:\photos\b.jpg"]));
            Assert.True(radial.TryAddFiles([@"C:\photos\a.jpg", @"C:\photos\c.jpg"]));

            Assert.Equal([@"C:\photos\a.jpg", @"C:\photos\b.jpg", @"C:\photos\c.jpg"], model.Files);
            Assert.True(radial.IsVisible);
        }
        finally
        {
            radial.Close(animate: false);
        }
    }

    [AvaloniaFact]
    public void A_later_launch_does_not_join_a_donut_that_is_closed_or_open_for_something_else()
    {
        TestServices.EnsureInitialized("en");
        var radial = AppHost.Get<RadialController>();
        var model = AppHost.Get<RadialViewModel>();
        try
        {
            radial.ShowForFiles(400, 400, [@"C:\photos\a.jpg"]);
            Assert.False(radial.TryAddFiles([@"C:\photos\b.jpg"]));
            Assert.Equal([@"C:\photos\a.jpg"], model.Files);

            radial.ShowForFiles(400, 400, [@"C:\photos\a.jpg"], forAnotherProcess: true);
            radial.Close(animate: false);
            Assert.False(radial.TryAddFiles([@"C:\photos\b.jpg"]));
            Assert.Equal([@"C:\photos\a.jpg"], model.Files);

            radial.ShowForDrag(400, 400);
            Assert.False(radial.TryAddFiles([@"C:\photos\b.jpg"]));
        }
        finally
        {
            radial.Close(animate: false);
        }
    }

    [Fact]
    public void Folders_only_go_into_one_archive()
    {
        var folder = Directory.CreateTempSubdirectory("filee-donut-folder-").FullName;
        try
        {
            var file = Path.Combine(folder, "1.png");
            var zip = new Preset { TargetFormat = "zip", Archive = { CombineIntoOne = true } };

            // Issue #186: a dragged folder goes into "Compress into one archive"; other presets take only the files.
            Assert.Equal([folder, file], RadialController.SourcesFor(zip, [folder, file]));
            Assert.Equal([file], RadialController.SourcesFor(new Preset { TargetFormat = "jpg" }, [folder, file]));
            Assert.Empty(RadialController.SourcesFor(new Preset { TargetFormat = "zip" }, [folder]));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
