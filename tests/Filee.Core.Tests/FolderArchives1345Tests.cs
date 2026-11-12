using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public sealed class FolderArchives1345Tests
{
    [Theory]
    [InlineData("zip")]
    [InlineData("7z")]
    [InlineData("tar")]
    [InlineData("tgz")]
    [InlineData("tbz2")]
    [InlineData("txz")]
    public void Folder_presets_accept_only_container_archives(string target)
    {
        Assert.True(BuiltInData.AcceptsFolders(new Preset { TargetFormat = target }));
        foreach (var single in new[] { "gz", "bz2", "xz", "pdf" })
            Assert.False(BuiltInData.AcceptsFolders(new Preset { TargetFormat = single }));
    }

    [Fact]
    public void Default_and_existing_iso_only_folders_offer_zip_and_one_zip()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var folders = profiles.Single(p => p.Id == "folders");
        Assert.Equal(["to-iso", "to-zip", "zip-all"], folders.PresetIds);
        folders.PresetIds = ["to-iso"];
        Assert.True(SettingsMigrations.ApplyToLibrary(83, presets, profiles));
        Assert.Equal(["to-iso", "to-zip", "zip-all"], folders.PresetIds);
        Assert.False(SettingsMigrations.ApplyToLibrary(83, presets, profiles));
        Assert.Single(presets, p => p.Id == "to-zip");
    }

    [Fact]
    public void Migration_preserves_custom_or_deleted_folder_profiles()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var folders = profiles.Single(p => p.Id == "folders");
        folders.PresetIds = ["zip-all", "to-iso"];
        Assert.False(SettingsMigrations.ApplyToLibrary(83, presets, profiles));
        Assert.Equal(["zip-all", "to-iso"], folders.PresetIds);
        profiles.Remove(folders);
        Assert.False(SettingsMigrations.ApplyToLibrary(83, presets, profiles));
        Assert.DoesNotContain(profiles, p => p.Id == "folders");
    }
}
