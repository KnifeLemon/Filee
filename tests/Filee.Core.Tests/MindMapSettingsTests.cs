using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class MindMapSettingsTests
{
    [Theory]
    [InlineData("Plan.mm")]
    [InlineData("Plan.xmind")]
    public void A_mind_map_has_the_documents_donut_in_a_new_library(string file) =>
        Assert.Equal("office", ProfileSelector.Select(BuiltInData.CreateProfiles(), [file])?.Id);

    [Fact]
    public void Version_110_donuts_learn_mind_maps_without_taking_custom_claims()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var donut = profiles.Single(p => p.Id == "office").Extensions;
        donut.Remove("mm");
        donut.Remove("xmind");

        Assert.True(SettingsMigrations.ApplyToLibrary(109, presets, profiles));
        Assert.Contains("mm", donut);
        Assert.Contains("xmind", donut);
        Assert.False(SettingsMigrations.ApplyToLibrary(109, presets, profiles));

        donut.Remove("xmind");
        profiles.Insert(0, new ToolbarProfile { Id = "my-maps", Name = "Maps", Extensions = ["XMIND"] });
        Assert.False(SettingsMigrations.ApplyToLibrary(109, presets, profiles));
        Assert.DoesNotContain("xmind", donut); // the user's own donut has it
    }
}
