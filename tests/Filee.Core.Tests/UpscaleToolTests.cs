// "Upscale Quality": the preset in the library (palette only, no donut slot) and how existing libraries get
// it (settings schema 64).

using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;
using Xunit;

namespace Filee.Core.Tests;

public class UpscaleToolTests
{
    private static List<string> Slots(IEnumerable<ToolbarProfile> profiles) =>
        [.. profiles.Select(p => $"{p.Id}: {string.Join(",", p.PresetIds)} | {string.Join(",", p.BackPresetIds ?? [])}")];

    [Fact]
    public void A_new_library_has_the_tool_after_annotate_and_no_slot()
    {
        var presets = BuiltInData.CreatePresets();
        var annotate = presets.FindIndex(p => p.Id == BuiltInData.AnnotateId);
        Assert.True(annotate >= 0);
        Assert.Equal(BuiltInData.UpscaleId, presets[annotate + 1].Id);

        var tool = presets.Single(p => p.Id == BuiltInData.UpscaleId);
        Assert.True(tool.IsTool);
        Assert.True(tool.IsUpscaleTool);
        Assert.Equal(Preset.UpscaleTool, tool.Tool);
        Assert.Equal("tool-upscale", tool.Id);

        // Palette only: no default profile gets a slot (#806)
        Assert.All(BuiltInData.CreateProfiles(), p =>
            Assert.DoesNotContain(BuiltInData.UpscaleId, p.PresetIds.Concat(p.BackPresetIds ?? [])));
    }

    [Fact]
    public void Schema_66_adds_the_tool_to_the_library_and_no_slot()
    {
        var presets = BuiltInData.CreatePresets().Where(p => p.Id != BuiltInData.UpscaleId).ToList();
        var profiles = BuiltInData.CreateProfiles();
        var slots = Slots(profiles);

        Assert.True(SettingsMigrations.ApplyToLibrary(65, presets, profiles));

        var after = presets.FindIndex(p => p.Id == BuiltInData.BackgroundId);
        if (after < 0)
            after = presets.FindIndex(p => p.Id == BuiltInData.AnnotateId);
        Assert.Equal(BuiltInData.UpscaleId, presets[after + 1].Id);
        Assert.Equal(slots, Slots(profiles));
        Assert.False(SettingsMigrations.ApplyToLibrary(AppSettings.CurrentSchemaVersion, presets, profiles));
    }
}
