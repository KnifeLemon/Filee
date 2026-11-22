using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class OtgSettingsTests
{
    [Fact]
    public void A_drawing_template_is_a_writable_vector_format_with_the_vector_donut()
    {
        Assert.Equal(("otg", FormatCategory.Vector, true), (FormatRegistry.Get("otg").Id, FormatRegistry.Get("otg").Category, FormatRegistry.Get("otg").Writable));
        Assert.Equal("vector", ProfileSelector.Select(BuiltInData.CreateProfiles(), ["Poster.otg"])?.Id);
        // The flat files are written too now.
        Assert.All(new[] { "fodt", "fods", "fodp", "fodg" }, id => Assert.True(FormatRegistry.Get(id).Writable, id));
    }

    [Fact]
    public void Version_113_donuts_learn_otg_without_taking_custom_claims()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var donut = profiles.Single(p => p.Id == "vector").Extensions;
        donut.Remove("otg");

        Assert.True(SettingsMigrations.ApplyToLibrary(112, presets, profiles));
        Assert.Contains("otg", donut);
        Assert.False(SettingsMigrations.ApplyToLibrary(112, presets, profiles));

        donut.Remove("otg");
        profiles.Insert(0, new ToolbarProfile { Id = "my-templates", Name = "Templates", Extensions = ["OTG"] });
        Assert.False(SettingsMigrations.ApplyToLibrary(112, presets, profiles));
        Assert.DoesNotContain("otg", donut); // the user's own donut has it
    }
}
