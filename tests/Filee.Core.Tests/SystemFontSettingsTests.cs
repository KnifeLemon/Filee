// The "Use the system font" switch in settings.json (#1065): off for new settings and for settings from before it (it
// needs no schema step), and kept on once switched on.

using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class SystemFontSettingsTests
{
    private static UserDataStore NewStore(TempDir dir)
    {
        var store = new UserDataStore(dir.Path);
        store.Load();
        return store;
    }

    [Fact]
    public void New_settings_use_the_embedded_fonts()
    {
        using var dir = new TempDir();
        Assert.False(NewStore(dir).Settings.Theme.UseSystemFont);
    }

    [Fact]
    public void Settings_from_before_it_keep_the_embedded_fonts_and_the_switch_is_saved()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"),
            $$"""{ "schemaVersion": {{AppSettings.CurrentSchemaVersion}}, "theme": { "cornerRadius": 8, "accent": "#1D9E75" } }""");
        var store = NewStore(dir);
        Assert.False(store.Settings.Theme.UseSystemFont);
        Assert.Equal(8, store.Settings.Theme.CornerRadius);

        store.Settings.Theme.UseSystemFont = true;
        store.SaveSettings();
        var reloaded = NewStore(dir);
        Assert.True(reloaded.Settings.Theme.UseSystemFont);
        Assert.Equal("#1D9E75", reloaded.Settings.Theme.Accent);
        Assert.True(reloaded.Settings.Theme.Clone().UseSystemFont);
    }
}
