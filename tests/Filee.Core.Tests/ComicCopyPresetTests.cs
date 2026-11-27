// A ZIP or RAR of pictures copied as a CBZ or CBR (#1482): CBR becomes a target (Filee still never writes RAR), and
// the "CBR" preset joins the palette after "CBZ" in new libraries and, through settings schema 106, in older ones.

using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class ComicCopyPresetTests
{
    [Fact]
    public void Cbr_is_a_target_and_the_other_comic_archives_stay_read_only()
    {
        Assert.True(FormatRegistry.Get("cbr").Writable);
        Assert.Equal(FormatGrade.General, FormatFields.GradeOf(FormatRegistry.Get("cbr")));
        Assert.All(new[] { "cb7", "cbt", "cbc", "rar" }, id => Assert.False(FormatRegistry.Get(id).Writable, id));
    }

    [Fact]
    public void The_cbr_preset_follows_the_cbz_preset_and_is_on_no_donut()
    {
        var ids = BuiltInData.CreatePresets().Select(p => p.Id).ToList();

        Assert.Equal(ids.IndexOf("to-cbz") + 1, ids.IndexOf("to-cbr"));
        var preset = BuiltInData.CreatePresets().Single(p => p.Id == "to-cbr");
        Assert.Equal(("CBR", "cbr"), (preset.Name, preset.TargetFormat));
        Assert.DoesNotContain(BuiltInData.CreateProfiles(),
            p => p.PresetIds.Contains("to-cbr") || p.BackPresetIds?.Contains("to-cbr") == true);
    }

    [Fact]
    public void Version_105_libraries_get_the_cbr_preset_once_after_cbz_and_no_donut_changes()
    {
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => p.Id == "to-cbr");
        var profiles = BuiltInData.CreateProfiles();
        var before = profiles.Select(p => (p.Id, string.Join(",", p.PresetIds), string.Join(",", p.BackPresetIds ?? []))).ToList();

        Assert.True(SettingsMigrations.ApplyToLibrary(105, presets, profiles));
        Assert.Equal(BuiltInData.CreatePresets().Select(p => p.Id), presets.Select(p => p.Id));
        Assert.Equal(before, profiles.Select(p => (p.Id, string.Join(",", p.PresetIds), string.Join(",", p.BackPresetIds ?? []))));
        Assert.False(SettingsMigrations.ApplyToLibrary(105, presets, profiles));

        // A library that removed it after the step doesn't get it back.
        presets.RemoveAll(p => p.Id == "to-cbr");
        Assert.False(SettingsMigrations.ApplyToLibrary(106, presets, profiles));
    }
}
