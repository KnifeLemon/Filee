// OpenStreetMap XML (.osm, #713): a read-only geospatial input on the geospatial donut of new installs, taught to the
// donut of existing ones by settings schema 77 without taking a donut the user made for it.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class OsmFormatRegistrationTests
{
    [Theory]
    [InlineData("map.osm")]
    [InlineData("MAP.OSM")]
    public void Osm_is_a_read_only_geospatial_format(string file)
    {
        var format = FormatRegistry.Detect(file);

        Assert.NotNull(format);
        Assert.Equal(("osm", FormatCategory.Geo, false), (format.Id, format.Category, format.Writable));
        Assert.Equal(FormatGrade.Specialist, FormatFields.GradeOf(format));
        Assert.Equal(["geo"], FormatFields.Of(format).Select(f => f.Id));
    }

    [Fact]
    public void The_binary_pbf_is_not_taken_for_osm_xml() =>
        Assert.NotEqual("osm", FormatRegistry.Detect("extract.osm.pbf")?.Id);

    [Fact]
    public void Fresh_profiles_open_the_geospatial_donut_for_osm() =>
        Assert.Equal("geo", ProfileSelector.Select(BuiltInData.CreateProfiles(), ["a.osm"])?.Id);

    [Fact]
    public void Version_76_libraries_learn_osm_once_without_taking_custom_claims()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var geo = profiles.Single(p => p.Id == "geo");
        geo.Extensions.Remove("osm");

        Assert.True(SettingsMigrations.ApplyToLibrary(76, presets, profiles));
        Assert.Contains("osm", geo.Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(76, presets, profiles));

        geo.Extensions.Remove("osm");
        profiles.Insert(0, new ToolbarProfile { Id = "my-maps", Extensions = ["OSM"] });
        Assert.False(SettingsMigrations.ApplyToLibrary(76, presets, profiles));
        Assert.DoesNotContain("osm", geo.Extensions);
        Assert.Equal("my-maps", ProfileSelector.Select(profiles, ["city.osm"])?.Id);
    }
}
