// Group ring size and spread in settings.json: today's ring (100 %) for new settings and for settings from before
// them (no schema step), saved, and kept within their ranges on a step.

using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class GroupRingSettingsTests
{
    private static UserDataStore NewStore(TempDir dir)
    {
        var store = new UserDataStore(dir.Path);
        store.Load();
        return store;
    }

    [Fact]
    public void New_settings_and_settings_from_before_them_keep_todays_ring()
    {
        using var dir = new TempDir();
        Assert.Equal((1.0, 1.0), (NewStore(dir).Settings.Donut.GroupRingSize, NewStore(dir).Settings.Donut.GroupRingSpread));

        using var old = new TempDir();
        File.WriteAllText(Path.Combine(old.Path, "settings.json"),
            $$"""{ "schemaVersion": {{AppSettings.CurrentSchemaVersion}}, "donut": { "outerRadius": 180 } }""");
        var store = NewStore(old);
        Assert.Equal((1.0, 1.0), (store.Settings.Donut.GroupRingSize, store.Settings.Donut.GroupRingSpread));
    }

    [Fact]
    public void Size_and_spread_are_saved_and_read_back_within_their_ranges()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        store.Settings.Donut.GroupRingSize = 1.25;
        store.Settings.Donut.GroupRingSpread = 1.5;
        store.SaveSettings();
        var reloaded = NewStore(dir);
        Assert.Equal((1.25, 1.5), (reloaded.Settings.Donut.GroupRingSize, reloaded.Settings.Donut.GroupRingSpread));

        // Hand-edited out of range: brought back in on load.
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"),
            $$"""{ "schemaVersion": {{AppSettings.CurrentSchemaVersion}}, "donut": { "groupRingSize": 9, "groupRingSpread": 0.2 } }""");
        var clamped = NewStore(dir);
        Assert.Equal((1.5, 1.0), (clamped.Settings.Donut.GroupRingSize, clamped.Settings.Donut.GroupRingSpread));
    }

    [Theory]
    [InlineData(0.5, 0.75)]
    [InlineData(1.02, 1.0)]
    [InlineData(1.33, 1.35)]
    [InlineData(2, 1.5)]
    [InlineData(double.NaN, 1)]
    public void Ring_size_is_clamped_to_a_step(double size, double expected) =>
        Assert.Equal(expected, DonutSettings.ClampGroupRingSize(size));

    [Theory]
    [InlineData(0.5, 1)]
    [InlineData(1.54, 1.5)]
    [InlineData(5, 3)]
    [InlineData(double.PositiveInfinity, 1)]
    public void Ring_spread_is_clamped_to_a_step(double spread, double expected) =>
        Assert.Equal(expected, DonutSettings.ClampGroupRingSpread(spread));
}
