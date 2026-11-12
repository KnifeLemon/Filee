// Windows animated cursors (.ani, #1365) are read as pictures: they open the images donut, which existing libraries learn
// without taking a donut of the user's own. Read only, and a cursor format like CUR: specialist, in the Developer field.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class AniFormatTests
{
    [Theory]
    [InlineData("busy.ani")]
    [InlineData("WAIT.ANI")]
    public void An_animated_cursor_uses_the_image_donut(string file)
    {
        Assert.Same(FormatRegistry.Get("ani"), FormatRegistry.Detect(file));
        Assert.Equal("images", ProfileSelector.Select(BuiltInData.CreateProfiles(), [file])?.Id);
    }

    [Fact]
    public void It_is_read_only_and_classed_as_cur_is()
    {
        var ani = FormatRegistry.Get("ani");
        Assert.False(ani.Writable);
        Assert.Equal(FormatFields.GradeOf(FormatRegistry.Get("cur")), FormatFields.GradeOf(ani));
        Assert.Equal(["developer"], FormatFields.Of(ani).Select(f => f.Id));
    }

    [Fact]
    public void Version_85_libraries_learn_ani_without_taking_custom_claims()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var images = profiles.Single(p => p.Id == "images");
        images.Extensions.Remove("ani");
        Assert.True(SettingsMigrations.ApplyToLibrary(84, presets, profiles));
        Assert.Contains("ani", images.Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(84, presets, profiles));

        images.Extensions.Remove("ani");
        profiles.Insert(0, new ToolbarProfile { Id = "my-cursors", Extensions = ["ANI"] });
        Assert.False(SettingsMigrations.ApplyToLibrary(84, presets, profiles));
        Assert.DoesNotContain("ani", images.Extensions); // the user's own donut has it
        Assert.Equal("my-cursors", ProfileSelector.Select(profiles, ["busy.ani"])?.Id);
    }
}
