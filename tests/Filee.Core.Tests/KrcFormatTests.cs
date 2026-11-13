// KuGou lyrics (.krc, #1296) are read as subtitles: they open the subtitles donut, which existing libraries learn
// without taking a donut of the user's own. Read only, and a lyrics format like LRC: general, in the subtitle field.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class KrcFormatTests
{
    [Theory]
    [InlineData("song.krc")]
    [InlineData("SONG.KRC")]
    public void Kugou_lyrics_use_the_subtitles_donut(string file)
    {
        Assert.Same(FormatRegistry.Get("krc"), FormatRegistry.Detect(file));
        Assert.Equal("subtitles", ProfileSelector.Select(BuiltInData.CreateProfiles(), [file])?.Id);
    }

    [Fact]
    public void It_is_read_only_and_classed_as_lrc_is()
    {
        var krc = FormatRegistry.Get("krc");
        Assert.False(krc.Writable);
        Assert.Equal(FormatFields.GradeOf(FormatRegistry.Get("lrc")), FormatFields.GradeOf(krc));
        Assert.Equal(FormatFields.Of(FormatRegistry.Get("lrc")).Select(f => f.Id), FormatFields.Of(krc).Select(f => f.Id));
    }

    [Fact]
    public void Version_86_libraries_learn_krc_without_taking_custom_claims()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var subtitles = profiles.Single(p => p.Id == "subtitles");
        subtitles.Extensions.Remove("krc");
        Assert.True(SettingsMigrations.ApplyToLibrary(86, presets, profiles));
        Assert.Contains("krc", subtitles.Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(86, presets, profiles));

        subtitles.Extensions.Remove("krc");
        profiles.Insert(0, new ToolbarProfile { Id = "my-lyrics", Extensions = ["KRC"] });
        Assert.False(SettingsMigrations.ApplyToLibrary(86, presets, profiles));
        Assert.DoesNotContain("krc", subtitles.Extensions); // the user's own donut has it
        Assert.Equal("my-lyrics", ProfileSelector.Select(profiles, ["song.krc"])?.Id);
    }
}
