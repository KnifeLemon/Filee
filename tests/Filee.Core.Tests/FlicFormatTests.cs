// FLIC animations (.flc, .fli, #940): a read-only video source on the video donut, learned by existing libraries.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class FlicFormatTests
{
    [Theory]
    [InlineData("intro.flc")]
    [InlineData("INTRO.FLI")]
    public void Flic_is_a_video_filee_reads_but_never_writes(string name)
    {
        var flic = FormatRegistry.Detect(name);
        Assert.NotNull(flic);
        Assert.Equal(("flc", FormatCategory.Video, false), (flic.Id, flic.Category, flic.Writable));
        Assert.Equal("intro", FormatRegistry.NameWithoutExtension(name).ToLowerInvariant());
        Assert.Equal("video", ProfileSelector.Select(BuiltInData.CreateProfiles(), [name])?.Id);
    }

    [Fact]
    public void Version_66_libraries_learn_flc_and_fli_once()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var video = profiles.Single(p => p.Id == "video").Extensions;
        video.RemoveAll(e => e is "flc" or "fli");
        profiles.Insert(0, new ToolbarProfile { Id = "my-retro", Name = "Retro", Extensions = ["fli"] });

        Assert.True(SettingsMigrations.ApplyToLibrary(66, presets, profiles));

        Assert.Contains("flc", video);
        Assert.DoesNotContain("fli", video); // the user's own donut has it
        Assert.False(SettingsMigrations.ApplyToLibrary(66, presets, profiles));
    }
}
