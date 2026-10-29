// Dahua DVR recordings (.dav, #594): a read-only video source on the video donut, learned by existing libraries.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class DavFormatTests
{
    [Fact]
    public void Dav_is_a_video_filee_reads_but_never_writes()
    {
        var dav = FormatRegistry.Detect("ch10_main_20261005150000.DAV");
        Assert.NotNull(dav);
        Assert.Equal(("dav", FormatCategory.Video, false), (dav.Id, dav.Category, dav.Writable));
        Assert.Equal("video", ProfileSelector.Select(BuiltInData.CreateProfiles(), ["camera.dav"])?.Id);
    }

    [Fact]
    public void Version_49_libraries_learn_dav_once()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var video = profiles.Single(p => p.Id == "video");
        video.Extensions.Remove("dav");

        Assert.True(SettingsMigrations.ApplyToLibrary(49, presets, profiles));

        Assert.Contains("dav", video.Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(49, presets, profiles));
    }
}
