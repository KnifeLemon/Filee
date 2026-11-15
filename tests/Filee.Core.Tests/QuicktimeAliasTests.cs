// QuickTime movies named .quicktime (#1283) open as MOV on the video donut; converting to MOV still writes .mov.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class QuicktimeAliasTests
{
    [Theory]
    [InlineData("clip.quicktime")]
    [InlineData("clip.QUICKTIME")]
    public void Quicktime_is_a_mov_and_uses_the_video_donut(string file)
    {
        Assert.Same(FormatRegistry.Get("mov"), FormatRegistry.Detect(file));
        Assert.Equal("video", ProfileSelector.Select(BuiltInData.CreateProfiles(), [file])?.Id);
    }

    [Fact]
    public void Mov_outputs_keep_the_mov_extension() =>
        Assert.Equal("mov", FormatRegistry.Get("mov").PrimaryExtension);

    [Fact]
    public void Version_75_libraries_learn_quicktime_without_taking_custom_claims()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var video = profiles.Single(p => p.Id == "video");
        video.Extensions.Remove("quicktime");
        Assert.True(SettingsMigrations.ApplyToLibrary(75, presets, profiles));
        Assert.Contains("quicktime", video.Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(75, presets, profiles));

        video.Extensions.Remove("quicktime");
        profiles.Insert(0, new ToolbarProfile { Id = "my-clips", Extensions = ["QUICKTIME"] });
        Assert.False(SettingsMigrations.ApplyToLibrary(75, presets, profiles));
        Assert.DoesNotContain("quicktime", video.Extensions);
        Assert.Equal("my-clips", ProfileSelector.Select(profiles, ["clip.quicktime"])?.Id);
    }
}
