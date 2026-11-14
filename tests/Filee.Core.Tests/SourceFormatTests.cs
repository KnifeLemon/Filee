// Source engine files (#1526): Garry's Mod addons and Valve paks open the archives donut, which existing libraries learn
// without taking a donut of the user's own; they unpack only where the Games field is on.

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
    public void Source_files_use_their_donut(string file, string format, string donut)
    {
        Assert.Same(FormatRegistry.Get(format), FormatRegistry.Detect(file));
        Assert.Equal(donut, ProfileSelector.Select(BuiltInData.CreateProfiles(), [file])?.Id);
    }

    [Theory]
    [InlineData("gma", FormatCategory.Archive, false)]
    [InlineData("vpk", FormatCategory.Archive, false)]
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
        archives.Extensions.RemoveAll(e => e is "gma" or "vpk");

        Assert.True(SettingsMigrations.ApplyToLibrary(94, presets, profiles));
        Assert.Contains("gma", archives.Extensions);
        Assert.Contains("vpk", archives.Extensions);
        Assert.Equal(BuiltInData.CreatePresets().Select(p => p.Id), presets.Select(p => p.Id)); // no preset added
        Assert.False(SettingsMigrations.ApplyToLibrary(94, presets, profiles));

        archives.Extensions.Remove("gma");
        profiles.Insert(0, new ToolbarProfile { Id = "my-addons", Extensions = ["GMA"] });
        Assert.False(SettingsMigrations.ApplyToLibrary(94, presets, profiles));
        Assert.DoesNotContain("gma", archives.Extensions); // the user's own donut has it
        Assert.Equal("my-addons", ProfileSelector.Select(profiles, ["wire.gma"])?.Id);
    }
}
