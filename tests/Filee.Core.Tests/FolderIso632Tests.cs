// Folder metadata is explicit: generic format detection and output naming remain pure.

using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class FolderIso632Tests
{
    [Fact]
    public void Migration_appends_iso_when_the_zip_neighbour_was_removed()
    {
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => p.Id is "to-iso" or "zip-all");
        var original = presets.Select(p => p.Id).ToArray();
        var profiles = BuiltInData.CreateProfiles();
        profiles.RemoveAll(p => p.Id == "folders");
        Assert.True(SettingsMigrations.ApplyToLibrary(72, presets, profiles));
        Assert.Equal("to-iso", presets[^1].Id);
        Assert.Equal(original, presets.Take(presets.Count - 1).Select(p => p.Id));
    }

    [Fact]
    public void Upgrade_preserves_a_folder_profile_removed_from_a_previous_test_build()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        profiles.RemoveAll(p => p.Id == "folders");
        Assert.False(SettingsMigrations.ApplyToLibrary(56, presets, profiles));
        Assert.DoesNotContain(profiles, p => p.Id == "folders");
    }

    [Fact]
    public void Dotted_directory_names_do_not_change_generic_format_detection()
    {
        using var temp = new TempDir();
        var folder = Directory.CreateDirectory(Path.Combine(temp.Path, "photos.jpg")).FullName;
        Assert.Equal("jpg", FormatRegistry.Detect(folder)!.Id);
        var tokens = new OutputPathResolver.Tokens(folder, "ISO", 1, DateTime.Now, SourceIsFolder: true);
        Assert.Equal("photos.jpg-", OutputPathResolver.ExpandPattern("{name}-{ext}", tokens));
        Assert.Equal("photos.jpg.iso", AskedLocation.SuggestedName(new Preset { TargetFormat = "iso" }, "ISO", folder, sourceIsFolder: true));
        Assert.Equal(folder + ".iso", OutputPathResolver.Resolve(new OutputRule { Location = OutputLocation.SameFolder }, tokens, "iso", _ => false));
    }

    [Fact]
    public void Migration_adds_the_folder_donut_without_enabling_iso_on_archives_or_overwriting_custom_presets()
    {
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => p.Id == "to-iso");
        var profiles = BuiltInData.CreateProfiles();
        profiles.RemoveAll(p => p.Id == "folders");
        var archives = profiles.Single(p => p.Id == "archives");
        var original = archives.PresetIds.ToArray();
        Assert.True(SettingsMigrations.ApplyToLibrary(72, presets, profiles));
        Assert.Equal(presets.FindIndex(p => p.Id == "zip-all") - 1, presets.FindIndex(p => p.Id == "to-iso"));
        Assert.Equal(original, archives.PresetIds);
        Assert.Equal(["to-iso"],
            profiles.Single(p => p.Id == "folders").PresetIds);
        presets.Single(p => p.Id == "to-iso").Name = "My ISO";
        Assert.False(SettingsMigrations.ApplyToLibrary(72, presets, profiles));
        Assert.Equal("My ISO", presets.Single(p => p.Id == "to-iso").Name);
        Assert.False(new Preset { TargetFormat = "iso", Archive = { CombineIntoOne = true } }.PacksIntoOneArchive);
        Assert.DoesNotContain(presets, p => p.Id.StartsWith("folder-", StringComparison.Ordinal));
    }

}
