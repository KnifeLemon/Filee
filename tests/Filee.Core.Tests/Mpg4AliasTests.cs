// MP4 videos named .mpg4 (#1397) open as MP4 on the video donut; converting to MP4 still writes .mp4.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class Mpg4AliasTests
{
    [Theory]
    [InlineData("reel.mpg4")]
    [InlineData("reel.MPG4")]
    public void Mpg4_is_an_mp4_and_uses_the_video_donut(string file)
    {
        Assert.Same(FormatRegistry.Get("mp4"), FormatRegistry.Detect(file));
        Assert.Equal("video", ProfileSelector.Select(BuiltInData.CreateProfiles(), [file])?.Id);
    }

    [Fact]
    public void Mp4_outputs_keep_the_mp4_extension() =>
        Assert.Equal("mp4", FormatRegistry.Get("mp4").PrimaryExtension);

    [Fact]
    public void Version_82_libraries_learn_mpg4_without_taking_custom_claims()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var video = profiles.Single(p => p.Id == "video");
        video.Extensions.Remove("mpg4");
        Assert.True(SettingsMigrations.ApplyToLibrary(82, presets, profiles));
        Assert.Contains("mpg4", video.Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(82, presets, profiles));

        video.Extensions.Remove("mpg4");
        profiles.Insert(0, new ToolbarProfile { Id = "my-reels", Extensions = ["MPG4"] });
        Assert.False(SettingsMigrations.ApplyToLibrary(82, presets, profiles));
        Assert.DoesNotContain("mpg4", video.Extensions);
        Assert.Equal("my-reels", ProfileSelector.Select(profiles, ["reel.mpg4"])?.Id);
    }
}
