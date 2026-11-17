// FITS astronomy pictures (issue #460): .fits and .fts are FITS, ".fit" is left for Garmin's FIT; the images donut
// learns the extensions in schema 43 without taking them from a donut of the user's.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class FitsFormatTests
{
    [Theory]
    [InlineData("m31.fits")]
    [InlineData("Light_001.FTS")]
    public void Fits_extensions_are_read_only_images(string file)
    {
        var format = FormatRegistry.Detect(file);

        Assert.Equal("fits", format?.Id);
        Assert.Equal(FormatCategory.Image, format!.Category);
        Assert.False(format.Writable);
    }

    [Fact]
    public void Fit_is_not_taken_for_fits() =>
        Assert.NotEqual("fits", FormatRegistry.Detect("ride.fit")?.Id);

    [Fact]
    public void Fresh_profiles_open_the_images_donut_for_fits() =>
        Assert.Equal("images", ProfileSelector.Select(BuiltInData.CreateProfiles(), ["a.fits", "b.fts"])?.Id);

    [Fact]
    public void Version_42_libraries_learn_fits_once_and_keep_a_custom_claim()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var images = profiles.Single(p => p.Id == "images");
        images.Extensions.RemoveAll(e => e is "fits" or "fts");
        profiles.Add(new ToolbarProfile { Id = "astro", Extensions = ["fts"] });

        Assert.True(SettingsMigrations.ApplyToLibrary(42, presets, profiles));

        Assert.Contains("fits", images.Extensions);
        Assert.DoesNotContain("fts", images.Extensions);
        Assert.Equal("astro", ProfileSelector.Select(profiles, ["light.fts"])?.Id);
        Assert.False(SettingsMigrations.ApplyToLibrary(42, presets, profiles));
    }

    [Fact]
    public void Presets_saved_before_the_options_debayer_and_keep_tiffs_linear()
    {
        // presets.json as UserDataStore writes it: camelCase keys, enums by name. PascalCase keys would be skipped and
        // every value below would only be a default.
        var presets = System.Text.Json.JsonSerializer.Deserialize("""
            [
              { "id": "old", "targetFormat": "png", "image": { "quality": 80 } },
              { "id": "newer", "targetFormat": "tiff", "image": { "quality": 70, "fitsTiff": "Sharpened" } },
              { "id": "mono", "targetFormat": "png", "image": { "fitsDebayer": false } },
              { "id": "stretched", "targetFormat": "tiff", "image": { "fitsTiff": "Stretched" } }
            ]
            """, FileeJsonContext.Default.ListPreset)!;
        var image = presets.ToDictionary(p => p.Id, p => p.Image);

        Assert.Equal(80, image["old"].Quality); // the image block was read, not left at its defaults
        Assert.True(image["old"].FitsDebayer);
        Assert.Equal(FitsTiffMode.Linear, image["old"].FitsTiff);
        Assert.Equal(70, image["newer"].Quality);
        Assert.Equal(FitsTiffMode.Linear, image["newer"].FitsTiff); // a mode from a newer Filee
        Assert.False(image["mono"].FitsDebayer);
        Assert.Equal(FitsTiffMode.Stretched, image["stretched"].FitsTiff);

        var written = System.Text.Json.JsonSerializer.Serialize(new Preset(), FileeJsonContext.Default.Preset);
        Assert.Contains("\"fitsTiff\": \"Linear\"", written);
        Assert.Contains("\"fitsDebayer\": true", written);

        var stretched = new ImageOptions { FitsTiff = FitsTiffMode.Stretched, FitsDebayer = false }.Clone();
        Assert.Equal((FitsTiffMode.Stretched, false), (stretched.FitsTiff, stretched.FitsDebayer));
    }
}
