// The "Show output format" switch in settings.json: on for new settings and for settings from before it (it needs no
// schema step), and kept off once switched off.

using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class DonutCaptionSettingsTests
{
    private static UserDataStore NewStore(TempDir dir)
    {
        var store = new UserDataStore(dir.Path);
        store.Load();
        return store;
    }

    [Fact]
    public void New_settings_show_the_output_format()
    {
        using var dir = new TempDir();
        Assert.True(NewStore(dir).Settings.Donut.ShowCaptions);
    }

    [Fact]
    public void Settings_from_before_it_show_it_and_the_switch_is_saved()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"),
            $$"""{ "schemaVersion": {{AppSettings.CurrentSchemaVersion}}, "donut": { "outerRadius": 180, "opacity": 0.5 } }""");
        var store = NewStore(dir);
        Assert.True(store.Settings.Donut.ShowCaptions);
        Assert.Equal(180, store.Settings.Donut.OuterRadius);

        store.Settings.Donut.ShowCaptions = false;
        store.SaveSettings();
        var reloaded = NewStore(dir);
        Assert.False(reloaded.Settings.Donut.ShowCaptions);
        Assert.Equal(0.5, reloaded.Settings.Donut.Opacity, 3);
    }
}
