// Property lists (.plist, issue #1256): recognised as a read-only data format, on the data donut of new installs, and
// taught to the data donut of existing ones by settings schema 75 without taking a donut the user made for it.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class PlistFormatTests
{
    [Theory]
    [InlineData("Info.plist")]
    [InlineData("com.apple.finder.PLIST")]
    public void Files_are_recognised_by_their_extension(string file) =>
        Assert.Equal("plist", FormatRegistry.Detect(file)?.Id);

    [Fact]
    public void A_property_list_is_a_data_format_that_is_only_read()
    {
        var format = FormatRegistry.Get("plist");

        Assert.Equal(FormatCategory.Data, format.Category);
        Assert.False(format.Writable);
        Assert.Equal(FormatGrade.Specialist, FormatFields.GradeOf(format));
        Assert.Equal(["developer"], FormatFields.Of(format).Select(f => f.Id));
    }

    [Fact]
    public void New_installs_open_property_lists_on_the_data_donut()
    {
        var data = BuiltInData.CreateProfiles().Single(p => p.Id == "data");

        Assert.Contains("plist", data.Extensions);
        Assert.Equal("data", ProfileSelector.Select(BuiltInData.CreateProfiles(), ["/tmp/Info.plist"]).Id);
    }

    [Fact]
    public void Version_74_libraries_learn_the_extension_without_taking_custom_claims()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Settings.SchemaVersion = 74;
        foreach (var profile in store.Profiles)
            profile.Extensions.RemoveAll(e => e == "plist");
        store.SaveLibrary();
        store.SaveSettings();

        var upgraded = new UserDataStore(dir.Path);
        upgraded.Load();

        Assert.Contains("plist", upgraded.Profiles.Single(p => p.Id == "data").Extensions);
        Assert.Equal(AppSettings.CurrentSchemaVersion, upgraded.Settings.SchemaVersion);

        // A donut of the user's own that already claims .plist keeps it.
        store.Settings.SchemaVersion = 74;
        foreach (var profile in store.Profiles)
            profile.Extensions.RemoveAll(e => e == "plist");
        store.Profiles.Insert(0, new ToolbarProfile { Id = "my-apple", Name = "Apple", Extensions = ["plist"] });
        store.SaveLibrary();
        store.SaveSettings();

        var kept = new UserDataStore(dir.Path);
        kept.Load();

        Assert.DoesNotContain("plist", kept.Profiles.Single(p => p.Id == "data").Extensions);
        Assert.Contains("plist", kept.Profiles.Single(p => p.Id == "my-apple").Extensions);
    }
}
