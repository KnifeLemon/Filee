// Tracker modules (.xm, .it, .s3m, #1376): read-only audio sources on the audio donut, learned by existing libraries.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class TrackerModuleFormatTests
{
    private static readonly string[] Modules = ["xm", "it", "s3m"];

    [Theory]
    [InlineData("Song.XM", "xm")]
    [InlineData("song.it", "it")]
    [InlineData("song.s3m", "s3m")]
    public void Tracker_modules_are_audio_filee_reads_but_never_writes(string file, string id)
    {
        var format = FormatRegistry.Detect(file);
        Assert.NotNull(format);
        Assert.Equal((id, FormatCategory.Audio, false), (format.Id, format.Category, format.Writable));
        Assert.Equal("audio", ProfileSelector.Select(BuiltInData.CreateProfiles(), [file])?.Id);
    }

    [Fact]
    public void Amiga_mod_stays_the_camcorder_video() =>
        Assert.Equal(FormatCategory.Video, FormatRegistry.Detect("clip.mod")?.Category);

    [Fact]
    public void Version_80_libraries_learn_tracker_modules_once()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var audio = profiles.Single(p => p.Id == "audio");
        audio.Extensions.RemoveAll(Modules.Contains);

        Assert.True(SettingsMigrations.ApplyToLibrary(80, presets, profiles));

        Assert.All(Modules, module => Assert.Contains(module, audio.Extensions));
        Assert.False(SettingsMigrations.ApplyToLibrary(80, presets, profiles));
    }

    [Fact]
    public void A_version_80_library_leaves_a_module_on_the_users_own_donut()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var audio = profiles.Single(p => p.Id == "audio");
        audio.Extensions.RemoveAll(Modules.Contains);
        profiles.Insert(0, new ToolbarProfile { Id = "my-tracks", Name = "Tracks", Extensions = ["XM"] });

        Assert.True(SettingsMigrations.ApplyToLibrary(80, presets, profiles));

        Assert.DoesNotContain("xm", audio.Extensions, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("it", audio.Extensions);
        Assert.Contains("s3m", audio.Extensions);
        Assert.Equal("my-tracks", ProfileSelector.Select(profiles, ["song.xm"])?.Id);
    }

    [Fact]
    public void A_saved_version_80_library_learns_tracker_modules_on_the_next_start_only()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Settings.SchemaVersion = 80;
        foreach (var profile in store.Profiles)
            profile.Extensions.RemoveAll(Modules.Contains);
        store.SaveLibrary();
        store.SaveSettings();

        var upgraded = new UserDataStore(dir.Path);
        upgraded.Load();
        var audio = upgraded.Profiles.Single(p => p.Id == "audio").Extensions;
        Assert.All(Modules, module => Assert.Contains(module, audio));
        Assert.Equal(AppSettings.CurrentSchemaVersion, upgraded.Settings.SchemaVersion);

        // The user takes .xm off the audio donut again: the start after that leaves it off.
        audio.Remove("xm");
        upgraded.SaveLibrary();
        upgraded.SaveSettings();
        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();
        Assert.DoesNotContain("xm", reloaded.Profiles.Single(p => p.Id == "audio").Extensions);
    }
}
