// Blizzard textures (.blp, #644), read only by Filee: they open the images donut, which existing libraries learn
// without taking a donut of the user's own, and belong to the Games field.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class BlpFormatTests
{
    [Theory]
    [InlineData("btnstormbolt.blp")]
    [InlineData("INTERFACE_ICON.BLP")]
    public void A_blp_uses_the_image_donut(string file)
    {
        Assert.Same(FormatRegistry.Get("blp"), FormatRegistry.Detect(file));
        Assert.Equal("images", ProfileSelector.Select(BuiltInData.CreateProfiles(), [file])?.Id);
    }

    [Fact]
    public void A_blp_is_a_read_only_game_format()
    {
        var blp = FormatRegistry.Get("blp")!;
        Assert.Equal(FormatCategory.Image, blp.Category);
        Assert.False(blp.Writable);
        Assert.Equal(FormatGrade.Specialist, FormatFields.GradeOf(blp));
        Assert.False(FormatFields.IsShown("blp", new HashSet<string> { "game" }));
        Assert.True(FormatFields.IsShown("blp", new HashSet<string>()));
    }

    [Fact]
    public void Version_104_libraries_learn_blp_without_taking_custom_claims()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var images = profiles.Single(p => p.Id == "images");
        images.Extensions.Remove("blp");
        Assert.True(SettingsMigrations.ApplyToLibrary(104, presets, profiles));
        Assert.Contains("blp", images.Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(104, presets, profiles));

        images.Extensions.Remove("blp");
        profiles.Insert(0, new ToolbarProfile { Id = "my-blp", Extensions = ["BLP"] });
        Assert.False(SettingsMigrations.ApplyToLibrary(104, presets, profiles));
        Assert.DoesNotContain("blp", images.Extensions); // the user's own donut has it
        Assert.Equal("my-blp", ProfileSelector.Select(profiles, ["hero.blp"])?.Id);
    }
}
