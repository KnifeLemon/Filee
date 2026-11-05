// Folder metadata is explicit: generic format detection and output naming remain pure.

using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class FolderIso632Tests
{
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
    public void Iso_standard_defaults_clones_and_serializes_without_changing_existing_presets()
    {
        var preset = System.Text.Json.JsonSerializer.Deserialize<Preset>("{\"TargetFormat\":\"iso\"}")!;
        Assert.Equal(IsoStandard.Iso9660Joliet, preset.Iso.Standard);
        var copy = preset.Clone();
        copy.Iso.Standard = IsoStandard.Iso9660;
        Assert.Equal(IsoStandard.Iso9660Joliet, preset.Iso.Standard);
        var json = System.Text.Json.JsonSerializer.Serialize(copy);
        Assert.Equal(IsoStandard.Iso9660, System.Text.Json.JsonSerializer.Deserialize<Preset>(json)!.Iso.Standard);
        Assert.Equal(IsoStandard.Iso9660Joliet,
            System.Text.Json.JsonSerializer.Deserialize<Preset>("{\"Iso\":{\"Standard\":\"Unknown\"}}")!.Iso.Standard);
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
        Assert.True(SettingsMigrations.ApplyToLibrary(69, presets, profiles));
        Assert.Equal(original, archives.PresetIds);
        Assert.Equal(["to-iso", "folder-zip", "folder-7z", "folder-tar", "folder-tgz", "folder-tbz2", "folder-txz"],
            profiles.Single(p => p.Id == "folders").PresetIds);
        presets.Single(p => p.Id == "to-iso").Name = "My ISO";
        Assert.False(SettingsMigrations.ApplyToLibrary(69, presets, profiles));
        Assert.Equal("My ISO", presets.Single(p => p.Id == "to-iso").Name);
        Assert.False(new Preset { TargetFormat = "iso", Archive = { CombineIntoOne = true } }.PacksIntoOneArchive);
    }

    [Fact]
    public void Previous_test_build_gets_the_folders_label_and_archive_actions_but_custom_layouts_stay()
    {
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => p.Id.StartsWith("folder-", StringComparison.Ordinal));
        var profiles = BuiltInData.CreateProfiles();
        var folder = profiles.Single(p => p.Id == "folders");
        folder.NameKey = "preset.iso_image";
        folder.PresetIds = ["to-iso"];
        Assert.True(SettingsMigrations.ApplyToLibrary(70, presets, profiles));
        Assert.Equal("profile.folders", folder.NameKey);
        Assert.Equal(7, folder.PresetIds.Count);
        Assert.All(BuiltInData.FolderArchivePresets(), p => Assert.True(p.PacksIntoOneArchive));
        Assert.False(SettingsMigrations.ApplyToLibrary(70, presets, profiles));

        folder.Name = "My folders";
        folder.NameKey = "preset.iso_image";
        folder.PresetIds = ["to-iso", "to-png"];
        Assert.False(SettingsMigrations.ApplyToLibrary(70, presets, profiles));
        Assert.Equal("My folders", folder.Name);
        Assert.Equal(["to-iso", "to-png"], folder.PresetIds);
    }
}
