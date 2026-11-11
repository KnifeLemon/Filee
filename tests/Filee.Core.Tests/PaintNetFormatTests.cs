// Paint.NET pictures (.pdn) are read as their merged picture, or as layers for PSD: they open the images donut, which
// existing libraries learn without taking a donut of the user's own.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class PaintNetFormatTests
{
    [Theory]
    [InlineData("drawing.pdn")]
    [InlineData("LOGO.PDN")]
    public void A_pdn_uses_the_image_donut(string file)
    {
        Assert.Same(FormatRegistry.Get("pdn"), FormatRegistry.Detect(file));
        Assert.Equal("images", ProfileSelector.Select(BuiltInData.CreateProfiles(), [file])?.Id);
    }

    [Fact]
    public void A_pdn_is_read_only()
    {
        var pdn = FormatRegistry.Get("pdn")!;
        Assert.Equal(FormatCategory.Image, pdn.Category);
        Assert.False(pdn.Writable);
    }

    [Fact]
    public void Version_78_libraries_learn_pdn_without_taking_custom_claims()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var images = profiles.Single(p => p.Id == "images");
        images.Extensions.Remove("pdn");
        Assert.True(SettingsMigrations.ApplyToLibrary(78, presets, profiles));
        Assert.Contains("pdn", images.Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(78, presets, profiles));

        images.Extensions.Remove("pdn");
        profiles.Insert(0, new ToolbarProfile { Id = "my-paint", Extensions = ["PDN"] });
        Assert.False(SettingsMigrations.ApplyToLibrary(78, presets, profiles));
        Assert.DoesNotContain("pdn", images.Extensions); // the user's own donut has it
        Assert.Equal("my-paint", ProfileSelector.Select(profiles, ["drawing.pdn"])?.Id);
    }
}
