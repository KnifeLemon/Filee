using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class MsixFormatTests
{
    private static readonly string[] Extensions = ["msix", "appx", "msixbundle", "appxbundle"];

    [Theory]
    [InlineData("app.msix")]
    [InlineData("app.APPX")]
    [InlineData("bundle.msixbundle")]
    [InlineData("bundle.appxbundle")]
    public void Windows_packages_are_read_only_archives(string name)
    {
        var format = FormatRegistry.Detect(name);
        Assert.NotNull(format);
        Assert.Equal(("msix", FormatCategory.Archive, false), (format.Id, format.Category, format.Writable));
        Assert.Equal(FormatGrade.Specialist, FormatFields.GradeOf(format));
        Assert.Equal(["developer"], FormatFields.Of(format).Select(f => f.Id));
        Assert.Equal("archives", ProfileSelector.Select(BuiltInData.CreateProfiles(), [name])?.Id);
    }

    [Theory]
    [InlineData("app.emsix")]
    [InlineData("app.eappx")]
    public void Encrypted_packages_are_not_recognized(string name) => Assert.Null(FormatRegistry.Detect(name));

    [Fact]
    public void Existing_libraries_learn_packages_without_taking_custom_claims()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var archives = profiles.Single(p => p.Id == "archives");
        archives.Extensions.RemoveAll(e => Extensions.Contains(e));
        profiles.Insert(0, new ToolbarProfile { Id = "mine", Name = "Mine", Extensions = ["APPX"] });

        Assert.True(SettingsMigrations.ApplyToLibrary(94, presets, profiles));
        Assert.DoesNotContain("appx", archives.Extensions, StringComparer.OrdinalIgnoreCase);
        Assert.All(Extensions.Where(e => e != "appx"), e => Assert.Contains(e, archives.Extensions));
        Assert.Equal("mine", ProfileSelector.Select(profiles, ["app.appx"])?.Id);
        Assert.False(SettingsMigrations.ApplyToLibrary(94, presets, profiles));
    }

    [Fact]
    public void Saved_libraries_learn_packages_once()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Settings.SchemaVersion = 94;
        foreach (var profile in store.Profiles)
            profile.Extensions.RemoveAll(e => Extensions.Contains(e));
        store.SaveLibrary();
        store.SaveSettings();

        var upgraded = new UserDataStore(dir.Path);
        upgraded.Load();
        var archives = upgraded.Profiles.Single(p => p.Id == "archives").Extensions;
        Assert.All(Extensions, e => Assert.Contains(e, archives));
        Assert.Equal(AppSettings.CurrentSchemaVersion, upgraded.Settings.SchemaVersion);
        archives.Remove("msix");
        upgraded.SaveLibrary();
        upgraded.SaveSettings();
        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();
        Assert.DoesNotContain("msix", reloaded.Profiles.Single(p => p.Id == "archives").Extensions);
    }
}
