// Wisenet WAVE / Nx Witness exports (.nov): a read-only video source on the video donut, learned by existing libraries.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class NovFormatTests
{
    [Fact]
    public void Nov_is_a_video_filee_reads_but_never_writes()
    {
        var nov = FormatRegistry.Detect("Parking lot 2026-10-05.NOV");
        Assert.NotNull(nov);
        Assert.Equal(("nov", FormatCategory.Video, false), (nov.Id, nov.Category, nov.Writable));
        Assert.Equal("video", ProfileSelector.Select(BuiltInData.CreateProfiles(), ["export.nov"])?.Id);
    }

    [Fact]
    public void Version_86_libraries_learn_nov_once()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var video = profiles.Single(p => p.Id == "video");
        video.Extensions.Remove("nov");

        Assert.True(SettingsMigrations.ApplyToLibrary(86, presets, profiles));

        Assert.Contains("nov", video.Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(86, presets, profiles));
    }
}
