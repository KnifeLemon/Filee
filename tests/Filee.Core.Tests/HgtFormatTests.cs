// SRTM elevation tiles (.hgt, #1793): registered as a read-only image format in the Maps (geo) field, opening the
// images donut, which existing libraries learn in schema 102 without overriding a custom donut.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class HgtFormatTests
{
    [Theory]
    [InlineData("N37W122.hgt")]
    [InlineData("s12e045.HGT")]
    public void Hgt_is_a_read_only_image_format_in_the_geo_field(string file)
    {
        var format = FormatRegistry.Detect(file);

        Assert.NotNull(format);
        Assert.Equal(("hgt", FormatCategory.Image, false), (format.Id, format.Category, format.Writable));
        Assert.Equal(FormatGrade.Specialist, FormatFields.GradeOf(format));
        Assert.Equal(["geo"], FormatFields.Of(format).Select(f => f.Id));
        Assert.False(FormatFields.IsShown("hgt", new HashSet<string> { "geo" }));
        Assert.True(FormatFields.IsShown("hgt", new HashSet<string>()));
    }

    [Fact]
    public void Fresh_profiles_open_the_images_donut_for_hgt() =>
        Assert.Equal("images", ProfileSelector.Select(BuiltInData.CreateProfiles(), ["N37W122.hgt"])?.Id);

    [Fact]
    public void Version_101_libraries_learn_hgt_once_and_keep_a_custom_claim()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var images = profiles.Single(p => p.Id == "images");
        images.Extensions.Remove("hgt");

        Assert.True(SettingsMigrations.ApplyToLibrary(101, presets, profiles));
        Assert.Contains("hgt", images.Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(101, presets, profiles));

        images.Extensions.Remove("hgt");
        profiles.Insert(0, new ToolbarProfile { Id = "my-dem", Extensions = ["HGT"] });
        Assert.False(SettingsMigrations.ApplyToLibrary(101, presets, profiles));
        Assert.DoesNotContain("hgt", images.Extensions);
        Assert.Equal("my-dem", ProfileSelector.Select(profiles, ["N37W122.hgt"])?.Id);
    }
}
