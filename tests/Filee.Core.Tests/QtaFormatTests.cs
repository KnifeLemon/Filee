// Apple Voice Memos recordings (.qta, #646): a read-only audio source on the audio donut, learned by existing libraries.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class QtaFormatTests
{
    [Fact]
    public void Qta_is_audio_filee_reads_but_never_writes()
    {
        var qta = FormatRegistry.Detect("Memo.QTA");
        Assert.NotNull(qta);
        Assert.Equal(("qta", FormatCategory.Audio, false), (qta.Id, qta.Category, qta.Writable));
        Assert.Equal("audio", ProfileSelector.Select(BuiltInData.CreateProfiles(), ["Memo.qta"])?.Id);
    }

    [Fact]
    public void Version_54_libraries_learn_qta_once()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var audio = profiles.Single(p => p.Id == "audio");
        audio.Extensions.Remove("qta");

        Assert.True(SettingsMigrations.ApplyToLibrary(54, presets, profiles));

        Assert.Contains("qta", audio.Extensions);
        Assert.False(SettingsMigrations.ApplyToLibrary(54, presets, profiles));
    }

    [Fact]
    public void A_saved_version_54_library_learns_qta_on_the_next_start_only()
    {
        using var dir = new TempDir();
        SaveVersion54Library(dir.Path);

        var upgraded = new UserDataStore(dir.Path);
        upgraded.Load();
        var audio = upgraded.Profiles.Single(p => p.Id == "audio").Extensions;
        Assert.Contains("qta", audio);
        Assert.Equal(AppSettings.CurrentSchemaVersion, upgraded.Settings.SchemaVersion);

        // The user takes it off the audio donut again: the start after that leaves it off.
        audio.Remove("qta");
        upgraded.SaveLibrary();
        upgraded.SaveSettings();
        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();
        Assert.DoesNotContain("qta", reloaded.Profiles.Single(p => p.Id == "audio").Extensions);
    }

    [Fact]
    public void A_version_54_library_leaves_qta_on_the_users_own_donut()
    {
        using var dir = new TempDir();
        SaveVersion54Library(dir.Path, new ToolbarProfile { Id = "my-memos", Name = "Memos", Extensions = ["QTA"] });

        var upgraded = new UserDataStore(dir.Path);
        upgraded.Load();

        Assert.Equal(["QTA"], upgraded.Profiles.Single(p => p.Id == "my-memos").Extensions);
        Assert.DoesNotContain("qta", upgraded.Profiles.Single(p => p.Id == "audio").Extensions, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A library saved by Filee at schema 54: no donut has .qta yet, plus <paramref name="own"/> if given.</summary>
    private static void SaveVersion54Library(string path, ToolbarProfile? own = null)
    {
        var store = new UserDataStore(path);
        store.Load();
        store.Settings.SchemaVersion = 54;
        foreach (var profile in store.Profiles)
            profile.Extensions.Remove("qta");
        if (own is not null)
            store.Profiles.Insert(0, own);
        store.SaveLibrary();
        store.SaveSettings();
    }
}
