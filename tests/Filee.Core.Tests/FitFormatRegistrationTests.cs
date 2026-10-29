// Garmin FIT activities (#451): read-only geospatial input, learned by existing libraries.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class FitFormatRegistrationTests
{
    [Theory]
    [InlineData("ride.fit")]
    [InlineData("RIDE.FIT")]
    public void Fit_is_a_read_only_geospatial_format(string file)
    {
        var format = FormatRegistry.Detect(file);

        Assert.NotNull(format);
        Assert.Equal(("fit", FormatCategory.Geo, false), (format.Id, format.Category, format.Writable));
    }

    [Fact]
    public void Fresh_profiles_open_the_geospatial_donut_for_fit() =>
        Assert.Equal("geo", ProfileSelector.Select(BuiltInData.CreateProfiles(), ["a.fit"])?.Id);

    [Fact]
    public void Version_46_libraries_learn_fit_once()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var geo = profiles.Single(p => p.Id == "geo");
        geo.Extensions.Remove("fit");

        Assert.True(SettingsMigrations.ApplyToLibrary(46, presets, profiles));

        Assert.Contains("fit", geo.Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(46, presets, profiles));
    }

    [Fact]
    public void Version_46_migration_keeps_a_custom_fit_claim()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var geo = profiles.Single(p => p.Id == "geo");
        geo.Extensions.Remove("fit");
        profiles.Insert(0, new ToolbarProfile { Id = "activities", Extensions = ["fit"] });

        SettingsMigrations.ApplyToLibrary(46, presets, profiles);

        Assert.DoesNotContain("fit", geo.Extensions);
        Assert.Equal("activities", ProfileSelector.Select(profiles, ["ride.fit"])?.Id);
    }
}
