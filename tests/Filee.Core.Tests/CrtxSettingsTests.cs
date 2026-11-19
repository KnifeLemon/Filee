using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class CrtxSettingsTests
{
    [Fact]
    public void A_chart_template_has_the_vector_donut_in_a_new_library() =>
        Assert.Equal("vector", ProfileSelector.Select(BuiltInData.CreateProfiles(), ["Sales.crtx"])?.Id);

    [Fact]
    public void Version_109_donuts_learn_crtx_without_taking_custom_claims()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var donut = profiles.Single(p => p.Id == "vector").Extensions;
        donut.Remove("crtx");

        Assert.True(SettingsMigrations.ApplyToLibrary(108, presets, profiles));
        Assert.Contains("crtx", donut);
        Assert.False(SettingsMigrations.ApplyToLibrary(108, presets, profiles));

        donut.Remove("crtx");
        profiles.Insert(0, new ToolbarProfile { Id = "my-charts", Name = "Chart templates", Extensions = ["CRTX"] });
        Assert.False(SettingsMigrations.ApplyToLibrary(108, presets, profiles));
        Assert.DoesNotContain("crtx", donut); // the user's own donut has it
    }
}
