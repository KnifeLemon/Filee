// The "Progress ring" choice in settings.json (Settings → Theme): the arc for new settings and for settings from
// before it (it needs no schema step), and kept once the dial of ticks is chosen.

using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class ProgressRingSettingsTests
{
    private static UserDataStore NewStore(TempDir dir)
    {
        var store = new UserDataStore(dir.Path);
        store.Load();
        return store;
    }

    [Fact]
    public void New_settings_keep_the_arc()
    {
        using var dir = new TempDir();
        Assert.Equal(ProgressRingStyle.Arc, NewStore(dir).Settings.Theme.ProgressRing);
    }

    [Fact]
    public void Settings_from_before_it_keep_the_arc_and_the_choice_is_saved()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"),
            $$"""{ "schemaVersion": {{AppSettings.CurrentSchemaVersion}}, "theme": { "cornerRadius": 8, "accent": "#1D9E75" } }""");
        var store = NewStore(dir);
        Assert.Equal(ProgressRingStyle.Arc, store.Settings.Theme.ProgressRing);
        Assert.Equal(8, store.Settings.Theme.CornerRadius);

        store.Settings.Theme.ProgressRing = ProgressRingStyle.Ticks;
        store.SaveSettings();
        var reloaded = NewStore(dir);
        Assert.Equal(ProgressRingStyle.Ticks, reloaded.Settings.Theme.ProgressRing);
        Assert.Equal("#1D9E75", reloaded.Settings.Theme.Accent);
        Assert.Equal(ProgressRingStyle.Ticks, reloaded.Settings.Theme.Clone().ProgressRing);
    }
}
