using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class DjvuSettingsTests
{
    [Theory]
    [InlineData("book.djvu")]
    [InlineData("book.djv")]
    public void A_djvu_document_has_a_donut_in_a_new_library(string file) =>
        Assert.NotNull(ProfileSelector.Select(BuiltInData.CreateProfiles(), [file]));

    [Fact]
    public void Version_112_donuts_learn_djvu_and_djv_without_taking_custom_claims()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var id = ProfileSelector.Select(profiles, ["book.djvu"])!.Id;
        var donut = profiles.Single(p => p.Id == id).Extensions;
        donut.Remove("djvu");
        donut.Remove("djv");

        Assert.True(SettingsMigrations.ApplyToLibrary(111, presets, profiles));
        Assert.Contains("djvu", donut);
        Assert.Contains("djv", donut);
        Assert.False(SettingsMigrations.ApplyToLibrary(111, presets, profiles));

        donut.Remove("djvu");
        donut.Remove("djv");
        profiles.Insert(0, new ToolbarProfile { Id = "my-books", Name = "Scanned books", Extensions = ["DJVU"] });
        Assert.True(SettingsMigrations.ApplyToLibrary(111, presets, profiles));
        Assert.DoesNotContain("djvu", donut); // the user's own donut has it
        Assert.Contains("djv", donut);
    }

    [Fact]
    public void Version_116_donuts_learn_iw4_and_iw44_without_taking_custom_claims()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var id = ProfileSelector.Select(profiles, ["photo.iw44"])!.Id;
        Assert.Equal(ProfileSelector.Select(profiles, ["book.djvu"])!.Id, id);
        var donut = profiles.Single(p => p.Id == id).Extensions;
        donut.Remove("iw4");
        donut.Remove("iw44");

        Assert.True(SettingsMigrations.ApplyToLibrary(115, presets, profiles));
        Assert.Contains("iw4", donut);
        Assert.Contains("iw44", donut);
        Assert.False(SettingsMigrations.ApplyToLibrary(115, presets, profiles));

        donut.Remove("iw4");
        donut.Remove("iw44");
        profiles.Insert(0, new ToolbarProfile { Id = "my-pictures", Name = "Wavelet pictures", Extensions = ["IW44"] });
        Assert.True(SettingsMigrations.ApplyToLibrary(115, presets, profiles));
        Assert.DoesNotContain("iw44", donut); // the user's own donut has it
        Assert.Contains("iw4", donut);
    }
}
