using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class IwbSettingsTests
{
    [Theory]
    [InlineData("lesson.iwb")]
    [InlineData("lesson.notebook")]
    public void A_whiteboard_file_has_the_presentations_donut_in_a_new_library(string file) =>
        Assert.Equal("presentations", ProfileSelector.Select(BuiltInData.CreateProfiles(), [file])?.Id);

    [Fact]
    public void Version_115_donuts_learn_iwb_and_notebook_without_taking_custom_claims()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var donut = profiles.Single(p => p.Id == "presentations").Extensions;
        donut.Remove("iwb");
        donut.Remove("notebook");

        Assert.True(SettingsMigrations.ApplyToLibrary(114, presets, profiles));
        Assert.Contains("iwb", donut);
        Assert.Contains("notebook", donut);
        Assert.False(SettingsMigrations.ApplyToLibrary(114, presets, profiles));

        donut.Remove("iwb");
        donut.Remove("notebook");
        profiles.Insert(0, new ToolbarProfile { Id = "my-boards", Name = "Whiteboards", Extensions = ["IWB"] });
        Assert.True(SettingsMigrations.ApplyToLibrary(114, presets, profiles));
        Assert.DoesNotContain("iwb", donut); // the user's own donut has it
        Assert.Contains("notebook", donut);
    }
}
