// The ISO editor retains Joliet for old presets and saves an explicit UDF choice on the same preset.

using Avalonia.Headless.XUnit;
using Filee.App.Services;
using Filee.App.ViewModels;
using Filee.App.Views;
using Filee.Core.Localization;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.App.Tests;

public sealed class Udf1443Tests
{
    [AvaloniaFact]
    public void Every_language_suggests_udf_for_joliet_limits()
    {
        TestServices.EnsureInitialized("en");
        foreach (var language in LocalizationService.Languages)
        {
            var strings = LocalizationService.LoadStrings(language.Code);
            foreach (var key in new[] { "error.iso_name", "error.iso_file_size" })
                Assert.Contains("UDF 2.01", strings[key]);
        }
    }

    [AvaloniaFact]
    public void Filesystem_choice_round_trips_without_changing_the_default()
    {
        TestServices.EnsureInitialized("en");
        var preset = new Preset { Name = "ISO image", TargetFormat = "iso" };
        var editor = new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>());
        Assert.True(editor.ShowIso);
        Assert.False(editor.ShowArchive);
        Assert.Equal(IsoFileSystem.Joliet, editor.IsoFileSystem.Value);
        editor.IsoFileSystem = editor.IsoFileSystems.Single(c => c.Value == IsoFileSystem.Udf201);
        Assert.True(editor.Apply());
        Assert.Equal(IsoFileSystem.Udf201, preset.Archive.IsoFileSystem);
        Assert.Equal(IsoFileSystem.Udf201, preset.Clone().Archive.IsoFileSystem);
        var reopened = new PresetEditorViewModel(preset, AppHost.Get<ILocalizer>());
        Assert.Equal(IsoFileSystem.Udf201, reopened.IsoFileSystem.Value);
        var window = new PresetEditorWindow { DataContext = reopened };
        try
        {
            AppHost.Get<ThemeService>().Apply(new ThemeSettings { Mode = ThemeMode.Dark, ReduceMotion = true }, "en");
            window.Show();
            RenderTests.Pump();
            RenderTests.Save(window, "iso-filesystem-udf.png");
        }
        finally { window.Close(); }
    }
}
