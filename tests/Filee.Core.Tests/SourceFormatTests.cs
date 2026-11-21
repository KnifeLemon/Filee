// Source engine files (#1526): Garry's Mod addons, Valve paks and maps (for their packed files) open the archives donut,
// which existing libraries learn without taking a donut of the user's own, and Source models and SMD the 3D models
// donut; each is a target only where the Games field is on.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class SourceFormatTests
{
    [Theory]
    [InlineData("wire.gma", "gma", "archives")]
    [InlineData("WIRE.GMA", "gma", "archives")]
    [InlineData("pak01_dir.vpk", "vpk", "archives")]
    [InlineData("gm_construct.bsp", "bsp", "archives")]
    [InlineData("gate_e2.mdl", "mdl", "models")]
    [InlineData("gate_e2_reference.smd", "smd", "models")]
    public void Source_files_use_their_donut(string file, string format, string donut)
    {
        Assert.Same(FormatRegistry.Get(format), FormatRegistry.Detect(file));
        Assert.Equal(donut, ProfileSelector.Select(BuiltInData.CreateProfiles(), [file])?.Id);
    }

    [Theory]
    [InlineData("gma", FormatCategory.Archive, true)]
    [InlineData("vpk", FormatCategory.Archive, false)]
    [InlineData("bsp", FormatCategory.Archive, false)]
    [InlineData("mdl", FormatCategory.Model3D, false)]
    [InlineData("smd", FormatCategory.Model3D, true)]
    public void Source_files_are_game_formats(string id, FormatCategory category, bool writable)
    {
        var format = FormatRegistry.Get(id)!;
        Assert.Equal((category, writable), (format.Category, format.Writable));
        Assert.Equal(FormatGrade.Specialist, FormatFields.GradeOf(format));
        Assert.False(FormatFields.IsShown(id, new HashSet<string> { "game" }));
        Assert.True(FormatFields.IsShown(id, new HashSet<string>()));
    }

    [Fact]
    public void Smd_joins_the_3d_models_in_the_palette_after_gltf()
    {
        var presets = BuiltInData.CreatePresets();
        var ids = presets.Select(p => p.Id).ToList();
        Assert.Equal(ids.IndexOf("to-gltf") + 1, ids.IndexOf("to-smd"));
        Assert.Equal("smd", presets.Single(p => p.Id == "to-smd").TargetFormat);
    }

    [Fact]
    public void Version_95_libraries_learn_the_source_files_without_taking_custom_claims()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var archives = profiles.Single(p => p.Id == "archives");
        archives.Extensions.RemoveAll(e => e is "gma" or "vpk" or "bsp");

        Assert.True(SettingsMigrations.ApplyToLibrary(95, presets, profiles));
        Assert.Contains("gma", archives.Extensions);
        Assert.Contains("vpk", archives.Extensions);
        Assert.Contains("bsp", archives.Extensions);
        Assert.Equal(BuiltInData.CreatePresets().Select(p => p.Id), presets.Select(p => p.Id)); // no preset added
        Assert.False(SettingsMigrations.ApplyToLibrary(95, presets, profiles));

        archives.Extensions.Remove("gma");
        profiles.Insert(0, new ToolbarProfile { Id = "my-addons", Extensions = ["GMA"] });
        Assert.False(SettingsMigrations.ApplyToLibrary(95, presets, profiles));
        Assert.DoesNotContain("gma", archives.Extensions); // the user's own donut has it
        Assert.Equal("my-addons", ProfileSelector.Select(profiles, ["wire.gma"])?.Id);
    }

    [Fact]
    public void Version_96_libraries_get_the_gma_preset_after_the_game_packs()
    {
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => p.Id == "to-gma");
        var profiles = BuiltInData.CreateProfiles();

        Assert.True(SettingsMigrations.ApplyToLibrary(96, presets, profiles));
        var ids = presets.Select(p => p.Id).ToList();
        Assert.Equal(ids.IndexOf("to-mrpack") + 1, ids.IndexOf("to-gma"));
        Assert.False(SettingsMigrations.ApplyToLibrary(96, presets, profiles));
        // Not on a donut.
        Assert.DoesNotContain(BuiltInData.CreateProfiles(), p => p.PresetIds.Contains("to-gma") || p.BackPresetIds?.Contains("to-gma") == true);
    }

    [Fact]
    public void Version_99_libraries_learn_source_models_and_get_the_smd_preset()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var models = profiles.Single(p => p.Id == "models");
        models.Extensions.RemoveAll(e => e is "mdl" or "smd");
        presets.RemoveAll(p => p.Id == "to-smd");

        Assert.True(SettingsMigrations.ApplyToLibrary(99, presets, profiles));
        Assert.Contains("mdl", models.Extensions);
        Assert.Contains("smd", models.Extensions);
        Assert.Equal(BuiltInData.CreatePresets().Select(p => p.Id), presets.Select(p => p.Id)); // "SMD" back after glTF
        Assert.False(SettingsMigrations.ApplyToLibrary(99, presets, profiles));

        models.Extensions.Remove("mdl");
        profiles.Insert(0, new ToolbarProfile { Id = "my-models", Extensions = ["MDL"] });
        Assert.False(SettingsMigrations.ApplyToLibrary(99, presets, profiles));
        Assert.DoesNotContain("mdl", models.Extensions);
    }

    [Fact]
    public void Version_100_libraries_get_the_decompile_preset_after_smd()
    {
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => p.Id == BuiltInData.DecompileModelId);
        var profiles = BuiltInData.CreateProfiles();

        Assert.True(SettingsMigrations.ApplyToLibrary(100, presets, profiles));
        Assert.Equal(BuiltInData.CreatePresets().Select(p => p.Id), presets.Select(p => p.Id)); // right after "SMD"
        Assert.Equal(FormatRegistry.Folder, presets.Single(p => p.Id == BuiltInData.DecompileModelId).TargetFormat);
        Assert.False(SettingsMigrations.ApplyToLibrary(100, presets, profiles));
        // Not on a donut.
        Assert.DoesNotContain(BuiltInData.CreateProfiles(),
            p => p.PresetIds.Contains(BuiltInData.DecompileModelId) || p.BackPresetIds?.Contains(BuiltInData.DecompileModelId) == true);
    }
}
