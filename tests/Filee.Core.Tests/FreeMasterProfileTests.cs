// Existing text donuts can offer FreeMASTER TXT → CSV without changing custom layouts.

using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class FreeMasterProfileTests
{
    [Fact]
    public void New_text_profile_offers_csv()
    {
        Assert.Contains("to-csv", BuiltInData.TextProfile().BackPresetIds!);
    }

    [Fact]
    public void Migration_offers_csv_only_once_and_respects_existing_choices()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var text = profiles.Single(p => p.Id == "text");
        text.BackPresetIds!.Remove("to-csv");
        Assert.True(SettingsMigrations.ApplyToLibrary(54, presets, profiles));
        Assert.Contains("to-csv", text.BackPresetIds);
        Assert.False(SettingsMigrations.ApplyToLibrary(54, presets, profiles));

        text.BackPresetIds.Remove("to-csv");
        text.PresetIds.Add("to-csv");
        Assert.False(SettingsMigrations.ApplyToLibrary(54, presets, profiles));
        Assert.DoesNotContain("to-csv", text.BackPresetIds);

        text.PresetIds.Remove("to-csv");
        while (text.BackPresetIds.Count < ToolbarProfile.MaxSlices)
            text.BackPresetIds.Add("user-" + text.BackPresetIds.Count);
        Assert.False(SettingsMigrations.ApplyToLibrary(54, presets, profiles));
        Assert.DoesNotContain("to-csv", text.BackPresetIds);
        Assert.False(SettingsMigrations.ApplyToLibrary(55, presets, profiles));
    }
}
