// The donut's look is saved quietly: written to settings.json at once, without the SettingsChanged that re-applies the
// theme, triggers and integration (a slider sends a change for every pixel it moves). So is where the Donut toolbar
// page's donut on screen was dragged, which settings from before it read as never dragged (no schema step).

using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class DonutLookSaveTests
{
    [Fact]
    public void The_look_is_written_without_raising_settings_changed()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        var raised = 0;
        store.SettingsChanged += (_, _) => raised++;

        store.Settings.Donut.OuterRadius = 188;
        store.Settings.Donut.Opacity = 0.6;
        store.Settings.Donut.GroupRingSpread = 2;
        store.SaveDonutLook();

        Assert.Equal(0, raised);
        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();
        Assert.Equal((188.0, 0.6, 2.0),
            (reloaded.Settings.Donut.OuterRadius, reloaded.Settings.Donut.Opacity, reloaded.Settings.Donut.GroupRingSpread));
    }

    [Fact]
    public void The_donut_on_screens_place_is_kept_quietly_and_older_settings_have_none()
    {
        using var old = new TempDir();
        File.WriteAllText(Path.Combine(old.Path, "settings.json"),
            $$"""{ "schemaVersion": {{AppSettings.CurrentSchemaVersion}}, "donut": { "outerRadius": 180 } }""");
        var store = new UserDataStore(old.Path);
        store.Load();
        Assert.Null(store.Settings.DonutPreviewPlace);

        var raised = 0;
        store.SettingsChanged += (_, _) => raised++;
        store.Settings.DonutPreviewPlace = new WindowBounds { X = 1320, Y = -40 };
        store.SaveDonutLook();
        Assert.Equal(0, raised);
        var reloaded = new UserDataStore(old.Path);
        reloaded.Load();
        Assert.Equal((1320, -40), (reloaded.Settings.DonutPreviewPlace!.X, reloaded.Settings.DonutPreviewPlace.Y));
        Assert.Equal(AppSettings.CurrentSchemaVersion, reloaded.Settings.SchemaVersion);
    }
}
