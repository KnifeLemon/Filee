using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class XpsSettingsTests
{
    [Theory]
    [InlineData("printout.xps")]
    [InlineData("printout.oxps")]
    public void An_xps_document_has_a_donut_in_a_new_library(string file) =>
        Assert.NotNull(ProfileSelector.Select(BuiltInData.CreateProfiles(), [file]));

    [Fact]
    public void Version_107_donuts_learn_xps_and_oxps_without_taking_custom_claims()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var id = ProfileSelector.Select(profiles, ["printout.xps"])!.Id;
        var donut = profiles.Single(p => p.Id == id).Extensions;
        donut.Remove("xps");
        donut.Remove("oxps");

        Assert.True(SettingsMigrations.ApplyToLibrary(106, presets, profiles));
        Assert.Contains("xps", donut);
        Assert.Contains("oxps", donut);
        Assert.False(SettingsMigrations.ApplyToLibrary(106, presets, profiles));

        donut.Remove("xps");
        donut.Remove("oxps");
        profiles.Insert(0, new ToolbarProfile { Id = "my-prints", Name = "Prints", Extensions = ["XPS"] });
        Assert.True(SettingsMigrations.ApplyToLibrary(106, presets, profiles));
        Assert.DoesNotContain("xps", donut); // the user's own donut has it
        Assert.Contains("oxps", donut);
    }
}
