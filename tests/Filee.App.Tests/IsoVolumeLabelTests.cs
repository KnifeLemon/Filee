// ISO label editing validates the active filesystem and survives reopening the shared editor.
using Avalonia.Headless.XUnit;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public sealed class IsoVolumeLabelTests
{
    [AvaloniaFact]
    public void Custom_labels_validate_save_reopen_and_can_be_cleared()
    {
        TestServices.EnsureInitialized("en");
        var preset = new Preset { Name = "ISO image", TargetFormat = "iso" };
        var editor = new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>());
        editor.IsoVolumeLabel = "ABCDEFGHIJKLMNOPQ";
        Assert.False(editor.IsValid);
        Assert.NotNull(editor.IsoLabelError);
        Assert.False(editor.Apply());
        editor.IsoFileSystem = editor.IsoFileSystems.Single(c => c.Value == IsoFileSystem.Udf201);
        editor.IsoVolumeLabel = "My disc";
        Assert.Null(editor.IsoLabelError);
        Assert.True(editor.Apply());
        Assert.Equal("My disc", preset.Archive.IsoVolumeLabel);
        var reopened = new PresetEditorViewModel(preset.Clone(), AppHost.Get<ILocalizer>());
        Assert.Equal("My disc", reopened.IsoVolumeLabel);
        reopened.IsoVolumeLabel = "";
        Assert.True(reopened.Apply());
        var theme = AppHost.Get<UserDataStore>().Settings.Theme;
        var motion = Motion.Enabled;
        var window = new PresetEditorWindow { DataContext = editor };
        try
        {
            AppHost.Get<ThemeService>().Apply(new ThemeSettings { Mode = ThemeMode.Dark, ReduceMotion = true }, "en");
            window.Show();
            RenderTests.Pump();
            RenderTests.Save(window, "iso-volume-label.png");
        }
        finally
        {
            window.Close();
            AppHost.Get<ThemeService>().Apply(theme, "en");
            Motion.Set(motion);
        }
    }

    [AvaloniaFact]
    public void Every_catalog_contains_the_label_and_validation_text()
    {
        foreach (var language in LocalizationService.Languages)
        {
            var strings = LocalizationService.LoadStrings(language.Code);
            foreach (var key in new[] { "presets.iso_label", "presets.iso_label_hint", "error.iso_label" })
                Assert.False(string.IsNullOrWhiteSpace(strings[key]), language.Code + ": " + key);
        }
    }
}
