// The preset editor's video card (#1259): one video, or parts every so many seconds or minutes, or equal parts, saved
// into the preset and read back; shown only where a video becomes a video.

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.App.Tests;

[Collection("MainWindow")]
public class VideoSplitEditorTests
{
    [AvaloniaTheory]
    [InlineData("en", false)]
    [InlineData("ko", true)]
    public void The_editor_splits_a_video_every_so_often_or_into_equal_parts(string language, bool dark)
    {
        TestServices.EnsureInitialized(language);
        var theme = AppHost.Get<ThemeService>();
        var loc = AppHost.Get<ILocalizer>();
        var mp4 = BuiltInData.CreatePresets().Single(p => p.Id == "to-mp4");
        var vm = new PresetEditorViewModel(mp4, loc);
        Assert.True(vm.ShowVideoSplit);
        Assert.False(vm.VideoOutput.Value);
        Assert.False(vm.ShowVideoParts);
        vm.VideoOutput = vm.VideoOutputChoices.Single(c => c.Value);
        Assert.True(vm.ShowVideoParts);
        // Ten minutes per part until something else is chosen.
        Assert.Equal((VideoSplitKind.Minutes, 10), (vm.VideoSplitKind.Value, vm.VideoSplitSize));
        vm.VideoSplitSize = 15;
        if (dark)
            theme.Apply(new ThemeSettings { Mode = ThemeMode.Dark }, language);
        try
        {
            var window = new PresetEditorWindow { DataContext = vm, Height = 1400 };
            window.Show();
            RenderTests.Pump();
            var controls = window.GetVisualDescendants().OfType<Control>().ToList();
            Assert.True(controls.Single(c => c.Name == "VideoOutput").IsEffectivelyVisible);
            Assert.True(controls.Single(c => c.Name == "VideoSplitSize").IsEffectivelyVisible);
            Assert.True(controls.Single(c => c.Name == "VideoSplitKind").IsEffectivelyVisible);
            RenderTests.Save(window, $"video-split-editor-{language}-{(dark ? "dark" : "light")}.png");
            window.Close();
        }
        finally
        {
            if (dark)
                theme.Apply(new ThemeSettings(), language);
        }
        Assert.True(vm.Apply());
        Assert.Equal((900, 0), (mp4.Media.SplitEverySeconds, mp4.Media.SplitParts));

        // Whole minutes read back as minutes, other lengths as seconds, and equal parts as parts.
        vm = new PresetEditorViewModel(mp4, loc);
        Assert.Equal((true, VideoSplitKind.Minutes, 15), (vm.VideoOutput.Value, vm.VideoSplitKind.Value, vm.VideoSplitSize));
        vm.VideoSplitKind = vm.VideoSplitKinds.Single(k => k.Value == VideoSplitKind.Seconds);
        vm.VideoSplitSize = 90;
        Assert.True(vm.Apply());
        Assert.Equal((90, 0), (mp4.Media.SplitEverySeconds, mp4.Media.SplitParts));
        vm = new PresetEditorViewModel(mp4, loc);
        Assert.Equal((VideoSplitKind.Seconds, 90), (vm.VideoSplitKind.Value, vm.VideoSplitSize));
        vm.VideoSplitKind = vm.VideoSplitKinds.Single(k => k.Value == VideoSplitKind.EqualParts);
        vm.VideoSplitSize = 3;
        Assert.True(vm.Apply());
        Assert.Equal((0, 3), (mp4.Media.SplitEverySeconds, mp4.Media.SplitParts));

        // One video: no split.
        vm = new PresetEditorViewModel(mp4, loc);
        Assert.Equal((VideoSplitKind.EqualParts, 3), (vm.VideoSplitKind.Value, vm.VideoSplitSize));
        vm.VideoOutput = vm.VideoOutputChoices.Single(c => !c.Value);
        Assert.False(vm.ShowVideoParts);
        Assert.True(vm.Apply());
        Assert.False(mp4.Media.Splits);
    }

    [AvaloniaFact]
    public void The_split_is_offered_only_where_a_video_becomes_a_video()
    {
        TestServices.EnsureInitialized("en");
        var loc = AppHost.Get<ILocalizer>();
        var presets = BuiltInData.CreatePresets();
        Assert.False(new PresetEditorViewModel(presets.Single(p => p.Id == "to-mp3"), loc).ShowVideoSplit);
        Assert.False(new PresetEditorViewModel(presets.Single(p => p.Id == "video-from-images"), loc).ShowVideoSplit);
        Assert.False(new PresetEditorViewModel(presets.Single(p => p.Id == "tool-clip"), loc).ShowVideoSplit);

        // A split left behind by another target saves nothing.
        var mp4 = presets.Single(p => p.Id == "to-mp4");
        mp4.Media.SplitParts = 2;
        var vm = new PresetEditorViewModel(mp4, loc);
        vm.Target = vm.Targets.First(t => t.Value == "mp3");
        Assert.False(vm.ShowVideoSplit);
        Assert.True(vm.Apply());
        Assert.False(mp4.Media.Splits);
    }
}
