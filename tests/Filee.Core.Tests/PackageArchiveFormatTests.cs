// NuGet packages (.nupkg, .snupkg, #1465), split-APK containers (.apks, .xapk, .apkm) and Android App Bundles (.aab,
// #1464): read-only archives on the archives donut, learned by existing libraries.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class PackageArchiveFormatTests
{
    private static readonly string[] Extensions = ["nupkg", "snupkg", "apks", "xapk", "apkm", "aab"];

    [Theory]
    [InlineData("Newtonsoft.Json.13.0.3.nupkg", "nupkg")]
    [InlineData("MyLib.1.0.0.SNUPKG", "nupkg")]
    [InlineData("app.apks", "apks")]
    [InlineData("com.example.app.XAPK", "apks")]
    [InlineData("com.example.app_1.0-1_minAPI24.apkm", "apks")]
    [InlineData("app-release.aab", "aab")]
    public void A_package_is_an_archive_filee_reads_but_never_writes(string file, string id)
    {
        var format = FormatRegistry.Detect(file);
        Assert.NotNull(format);
        Assert.Equal((id, FormatCategory.Archive, false), (format.Id, format.Category, format.Writable));
        Assert.Equal(FormatGrade.Specialist, FormatFields.GradeOf(format));
        Assert.Equal(["developer"], FormatFields.Of(format).Select(f => f.Id));
        Assert.Equal("archives", ProfileSelector.Select(BuiltInData.CreateProfiles(), [file])?.Id);
    }

    [Fact]
    public void Version_87_libraries_learn_the_packages_once()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var archives = profiles.Single(p => p.Id == "archives");
        archives.Extensions.RemoveAll(e => Extensions.Contains(e));

        Assert.True(SettingsMigrations.ApplyToLibrary(87, presets, profiles));

        Assert.All(Extensions, e => Assert.Contains(e, archives.Extensions));
        Assert.False(SettingsMigrations.ApplyToLibrary(87, presets, profiles));
    }

    [Fact]
    public void A_version_87_library_leaves_a_package_on_the_users_own_donut()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var archives = profiles.Single(p => p.Id == "archives");
        archives.Extensions.RemoveAll(e => Extensions.Contains(e));
        profiles.Insert(0, new ToolbarProfile { Id = "my-android", Name = "Android", Extensions = ["XAPK", "aab"] });

        Assert.True(SettingsMigrations.ApplyToLibrary(87, presets, profiles));

        Assert.DoesNotContain("xapk", archives.Extensions, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("aab", archives.Extensions, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("apks", archives.Extensions);
        Assert.Contains("nupkg", archives.Extensions);
        Assert.Equal("my-android", ProfileSelector.Select(profiles, ["game.xapk"])?.Id);
        Assert.Equal("archives", ProfileSelector.Select(profiles, ["game.apkm"])?.Id);
    }

    [Fact]
    public void A_saved_version_87_library_learns_the_packages_on_the_next_start_only()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Settings.SchemaVersion = 87;
        foreach (var profile in store.Profiles)
            profile.Extensions.RemoveAll(e => Extensions.Contains(e));
        store.SaveLibrary();
        store.SaveSettings();

        var upgraded = new UserDataStore(dir.Path);
        upgraded.Load();
        var archives = upgraded.Profiles.Single(p => p.Id == "archives").Extensions;
        Assert.All(Extensions, e => Assert.Contains(e, archives));
        Assert.Equal(AppSettings.CurrentSchemaVersion, upgraded.Settings.SchemaVersion);

        // The user takes .nupkg off the archives donut again: the start after that leaves it off.
        archives.Remove("nupkg");
        upgraded.SaveLibrary();
        upgraded.SaveSettings();
        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();
        Assert.DoesNotContain("nupkg", reloaded.Profiles.Single(p => p.Id == "archives").Extensions);
    }
}
