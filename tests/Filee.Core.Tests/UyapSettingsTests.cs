using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class UyapSettingsTests
{
    [Fact]
    public void A_uyap_document_has_a_donut_in_a_new_library() =>
        Assert.NotNull(ProfileSelector.Select(BuiltInData.CreateProfiles(), ["dilekce.udf"]));

    [Fact]
    public void Version_103_donuts_learn_udf_without_taking_custom_claims()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var id = ProfileSelector.Select(profiles, ["dilekce.udf"])!.Id;
        var donut = profiles.Single(p => p.Id == id).Extensions;
        donut.Remove("udf");

        Assert.True(SettingsMigrations.ApplyToLibrary(102, presets, profiles));
        Assert.Contains("udf", donut);
        Assert.False(SettingsMigrations.ApplyToLibrary(102, presets, profiles));

        donut.Remove("udf");
        profiles.Insert(0, new ToolbarProfile { Id = "my-court", Name = "Court", Extensions = ["UDF"] });
        Assert.False(SettingsMigrations.ApplyToLibrary(102, presets, profiles));
        Assert.DoesNotContain("udf", donut); // the user's own donut has it
    }
}
