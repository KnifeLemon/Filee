// Source engine files (#1526): Garry's Mod addons, Valve paks and maps (for their packed files) open the archives donut,
// which existing libraries learn without taking a donut of the user's own; they unpack only where the Games field is
// on.

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
    public void Source_files_use_their_donut(string file, string format, string donut)
    {
        Assert.Same(FormatRegistry.Get(format), FormatRegistry.Detect(file));
        Assert.Equal(donut, ProfileSelector.Select(BuiltInData.CreateProfiles(), [file])?.Id);
    }

    [Theory]
    [InlineData("gma", FormatCategory.Archive, true)]
    [InlineData("vpk", FormatCategory.Archive, false)]
    [InlineData("bsp", FormatCategory.Archive, false)]
    public void Source_files_are_game_formats(string id, FormatCategory category, bool writable)
    {
        var format = FormatRegistry.Get(id)!;
        Assert.Equal((category, writable), (format.Category, format.Writable));
        Assert.Equal(FormatGrade.Specialist, FormatFields.GradeOf(format));
        Assert.False(FormatFields.IsShown(id, new HashSet<string> { "game" }));
        Assert.True(FormatFields.IsShown(id, new HashSet<string>()));
    }

    [Fact]
    public void Version_94_libraries_learn_the_source_files_without_taking_custom_claims()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var archives = profiles.Single(p => p.Id == "archives");
        archives.Extensions.RemoveAll(e => e is "gma" or "vpk" or "bsp");

        Assert.True(SettingsMigrations.ApplyToLibrary(94, presets, profiles));
        Assert.Contains("gma", archives.Extensions);
        Assert.Contains("vpk", archives.Extensions);
        Assert.Contains("bsp", archives.Extensions);
        Assert.Equal(BuiltInData.CreatePresets().Select(p => p.Id), presets.Select(p => p.Id)); // no preset added
        Assert.False(SettingsMigrations.ApplyToLibrary(94, presets, profiles));

        archives.Extensions.Remove("gma");
        profiles.Insert(0, new ToolbarProfile { Id = "my-addons", Extensions = ["GMA"] });
        Assert.False(SettingsMigrations.ApplyToLibrary(94, presets, profiles));
        Assert.DoesNotContain("gma", archives.Extensions); // the user's own donut has it
        Assert.Equal("my-addons", ProfileSelector.Select(profiles, ["wire.gma"])?.Id);
    }

    [Fact]
    public void Version_95_libraries_get_the_gma_preset_after_the_game_packs()
    {
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => p.Id == "to-gma");
        var profiles = BuiltInData.CreateProfiles();

        Assert.True(SettingsMigrations.ApplyToLibrary(95, presets, profiles));
        var ids = presets.Select(p => p.Id).ToList();
        Assert.Equal(ids.IndexOf("to-mrpack") + 1, ids.IndexOf("to-gma"));
        Assert.False(SettingsMigrations.ApplyToLibrary(95, presets, profiles));
        // Not on a donut.
        Assert.DoesNotContain(BuiltInData.CreateProfiles(), p => p.PresetIds.Contains("to-gma") || p.BackPresetIds?.Contains("to-gma") == true);
    }
}
