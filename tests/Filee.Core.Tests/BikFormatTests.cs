// Bink Video (.bik, #751): a read-only video source on the video donut, learned by existing libraries.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class BikFormatTests
{
    [Fact]
    public void Bik_is_a_video_filee_reads_but_never_writes()
    {
        var bik = FormatRegistry.Detect("intro.BIK");
        Assert.NotNull(bik);
        Assert.Equal(("bik", FormatCategory.Video, false), (bik.Id, bik.Category, bik.Writable));
        Assert.Equal("video", ProfileSelector.Select(BuiltInData.CreateProfiles(), ["intro.bik"])?.Id);
    }

    [Fact]
    public void Version_55_libraries_learn_bik_once()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        profiles.Single(p => p.Id == "video").Extensions.Remove("bik");

        Assert.True(SettingsMigrations.ApplyToLibrary(55, presets, profiles));

        Assert.Contains("bik", profiles.Single(p => p.Id == "video").Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(55, presets, profiles));
    }
}
