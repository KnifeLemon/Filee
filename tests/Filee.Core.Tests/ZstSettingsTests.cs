using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class ZstSettingsTests
{
    private static readonly string[] Extensions = ["tar.zst", "tzst", "zst"];

    [Theory]
    [InlineData("logs.zst")]
    [InlineData("backup.tar.zst")]
    [InlineData("backup.tzst")]
    public void A_zstandard_file_has_the_archives_donut_in_a_new_library(string name) =>
        Assert.Equal("archives", ProfileSelector.Select(BuiltInData.CreateProfiles(), [name])?.Id);

    [Fact]
    public void Version_104_archives_learn_zstandard_without_taking_custom_claims()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var archives = profiles.Single(p => p.Id == "archives");
        archives.Extensions.RemoveAll(e => Extensions.Contains(e));
        profiles.Insert(0, new ToolbarProfile { Id = "mine", Name = "Mine", Extensions = ["ZST"] });

        Assert.True(SettingsMigrations.ApplyToLibrary(103, presets, profiles));
        Assert.DoesNotContain("zst", archives.Extensions, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("tar.zst", archives.Extensions);
        Assert.Contains("tzst", archives.Extensions);
        Assert.Equal("mine", ProfileSelector.Select(profiles, ["logs.zst"])?.Id);
        Assert.False(SettingsMigrations.ApplyToLibrary(103, presets, profiles));
    }
}
