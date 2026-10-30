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

    private static RadialController Create(IPlatformServices platform) =>
        new(AppHost.Get<RadialViewModel>(), AppHost.Get<ConversionService>(), AppHost.Get<WindowService>(),
            AppHost.Get<UserDataStore>(), platform, AppHost.Get<TriggerService>(), NullLogger<RadialController>.Instance);

    private sealed class WindowPlatform : IPlatformServices
    {
        public int Restores { get; private set; }
        public void RestoreWindow(nint windowHandle) => Restores++;
        public void RaiseTopmost(nint windowHandle) { }
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
