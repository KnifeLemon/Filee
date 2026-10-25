// Settings → Security and limits: defaults, the ranges values are taken into, and settings files from before the page
// (or edited by hand) reading as the defaults.

using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class SecuritySettingsTests
{
    [Fact]
    public void Defaults_ask_and_keep_generous_limits()
    {
        var settings = new AppSettings();

        Assert.Equal(ActiveContentPolicy.Ask, settings.Security.ActiveContent);
        Assert.Equal(500, settings.Limits.EffectiveMaxImageMegapixels);
        Assert.Equal(0, settings.Limits.EffectiveMaxImageMemoryMb);
        Assert.Equal(Environment.ProcessorCount, settings.Limits.EffectiveParallelFiles);
        Assert.Equal(TimeSpan.FromMinutes(15), settings.Limits.EffectiveStepTimeout);
    }

    [Fact]
    public void Values_outside_a_range_are_taken_as_its_nearest_end()
    {
        var limits = new LimitSettings
        {
            MaxImageMegapixels = -5,
            MaxImageSide = int.MaxValue,
            MaxParallelFiles = 1000,
            StepTimeoutMinutes = 100_000,
        };

        Assert.Equal(0, limits.EffectiveMaxImageMegapixels);
        Assert.Equal(LimitSettings.MaxSide, limits.EffectiveMaxImageSide);
        Assert.Equal(LimitSettings.MaxFiles, limits.EffectiveParallelFiles);
        Assert.Equal(TimeSpan.FromMinutes(LimitSettings.MaxTimeoutMinutes), limits.EffectiveStepTimeout);
    }

    [Fact]
    public void Zero_means_automatic()
    {
        var limits = new LimitSettings { MaxParallelFiles = 0, StepTimeoutMinutes = 0 };

        Assert.Equal(Environment.ProcessorCount, limits.EffectiveParallelFiles);
        Assert.Null(limits.EffectiveStepTimeout);
    }

    [Fact]
    public void A_settings_file_from_before_the_page_reads_as_the_defaults()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"), """{ "schemaVersion": 46, "language": "en" }""");

        var store = new UserDataStore(dir.Path);
        store.Load();

        Assert.Equal(ActiveContentPolicy.Ask, store.Settings.Security.ActiveContent);
        Assert.Equal(15, store.Settings.Limits.StepTimeoutMinutes);
    }

    [Fact]
    public void Null_sections_edited_by_hand_read_as_the_defaults()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"),
            """{ "schemaVersion": 46, "security": null, "limits": null }""");

        var store = new UserDataStore(dir.Path);
        store.Load();

        Assert.NotNull(store.Settings.Security);
        Assert.NotNull(store.Settings.Limits);
    }

    [Fact]
    public void Settings_round_trip_and_an_unknown_policy_reads_as_ask()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Settings.Security.ActiveContent = ActiveContentPolicy.Refuse;
        store.Settings.Limits.MaxImageMegapixels = 123;
        store.SaveSettings();

        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();
        Assert.Equal(ActiveContentPolicy.Refuse, reloaded.Settings.Security.ActiveContent);
        Assert.Equal(123, reloaded.Settings.Limits.MaxImageMegapixels);

        // A value from a newer Filee.
        var path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"refuse\"", "\"sandbox\"", StringComparison.OrdinalIgnoreCase));
        var newer = new UserDataStore(dir.Path);
        newer.Load();
        Assert.Equal(ActiveContentPolicy.Ask, newer.Settings.Security.ActiveContent);
    }
}
