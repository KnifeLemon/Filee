// Valve textures (.vtf), read and written by Filee: they open the images donut, which existing libraries learn without
// taking a donut of the user's own, and are a target only where the Games field is on.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class VtfFormatTests
{
    [Theory]
    [InlineData("brick01.vtf")]
    [InlineData("SPRAY.VTF")]
    public void A_vtf_uses_the_image_donut(string file)
    {
        Assert.Same(FormatRegistry.Get("vtf"), FormatRegistry.Detect(file));
        Assert.Equal("images", ProfileSelector.Select(BuiltInData.CreateProfiles(), [file])?.Id);
    }

    [Fact]
    public void A_vtf_is_a_game_target()
    {
        var vtf = FormatRegistry.Get("vtf")!;
        Assert.Equal(FormatCategory.Image, vtf.Category);
        Assert.True(vtf.Writable);
        Assert.Equal(FormatGrade.Specialist, FormatFields.GradeOf(vtf));
        Assert.False(FormatFields.IsShown("vtf", new HashSet<string> { "game" }));
        Assert.True(FormatFields.IsShown("vtf", new HashSet<string>()));
    }

    [Fact]
    public void Version_89_libraries_learn_vtf_without_taking_custom_claims()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var images = profiles.Single(p => p.Id == "images");
        images.Extensions.Remove("vtf");
        Assert.True(SettingsMigrations.ApplyToLibrary(89, presets, profiles));
        Assert.Contains("vtf", images.Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(89, presets, profiles));

        images.Extensions.Remove("vtf");
        profiles.Insert(0, new ToolbarProfile { Id = "my-textures", Extensions = ["VTF"] });
        Assert.False(SettingsMigrations.ApplyToLibrary(89, presets, profiles));
        Assert.DoesNotContain("vtf", images.Extensions); // the user's own donut has it
        Assert.Equal("my-textures", ProfileSelector.Select(profiles, ["spray.vtf"])?.Id);
    }
}
