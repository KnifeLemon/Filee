// Adobe extension packages (.zxp, #1382): a read-only archive on the archives donut, learned by existing libraries.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class ZxpFormatTests
{
    [Fact]
    public void Zxp_is_an_archive_filee_reads_but_never_writes()
    {
        var zxp = FormatRegistry.Detect("Panel.ZXP");
        Assert.NotNull(zxp);
        Assert.Equal(("zxp", FormatCategory.Archive, false), (zxp.Id, zxp.Category, zxp.Writable));
        Assert.Equal("archives", ProfileSelector.Select(BuiltInData.CreateProfiles(), ["Panel.zxp"])?.Id);
    }

    [Fact]
    public void Version_81_libraries_learn_zxp_once()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var archives = profiles.Single(p => p.Id == "archives");
        archives.Extensions.Remove("zxp");

        Assert.True(SettingsMigrations.ApplyToLibrary(81, presets, profiles));

        Assert.Contains("zxp", archives.Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(81, presets, profiles));
    }

    [Fact]
    public void A_version_81_library_leaves_zxp_on_the_users_own_donut()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var archives = profiles.Single(p => p.Id == "archives");
        archives.Extensions.Remove("zxp");
        profiles.Insert(0, new ToolbarProfile { Id = "my-extensions", Name = "Extensions", Extensions = ["ZXP"] });

        Assert.False(SettingsMigrations.ApplyToLibrary(81, presets, profiles));

        Assert.DoesNotContain("zxp", archives.Extensions, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("my-extensions", ProfileSelector.Select(profiles, ["panel.zxp"])?.Id);
    }

    [Fact]
    public void A_saved_version_81_library_learns_zxp_on_the_next_start_only()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Settings.SchemaVersion = 81;
        foreach (var profile in store.Profiles)
            profile.Extensions.Remove("zxp");
        store.SaveLibrary();
        store.SaveSettings();

        var upgraded = new UserDataStore(dir.Path);
        upgraded.Load();
        var archives = upgraded.Profiles.Single(p => p.Id == "archives").Extensions;
        Assert.Contains("zxp", archives);
        Assert.Equal(AppSettings.CurrentSchemaVersion, upgraded.Settings.SchemaVersion);

        // The user takes it off the archives donut again: the start after that leaves it off.
        archives.Remove("zxp");
        upgraded.SaveLibrary();
        upgraded.SaveSettings();
        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();
        Assert.DoesNotContain("zxp", reloaded.Profiles.Single(p => p.Id == "archives").Extensions);
    }
}
