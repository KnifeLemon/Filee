// "Upscale Quality": the preset in the library (palette only, no donut slot) and how existing libraries get
// it (settings schema 73).

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
    public void A_new_library_has_the_tool_after_metadata_and_no_slot()
    {
        var presets = BuiltInData.CreatePresets();
        var metadata = presets.FindIndex(p => p.Id == BuiltInData.MetadataId);
        Assert.True(metadata >= 0);
        Assert.Equal(BuiltInData.UpscaleId, presets[metadata + 1].Id);

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
    public void Schema_73_adds_the_tool_after_metadata_and_no_slot()
    {
        var presets = BuiltInData.CreatePresets().Where(p => p.Id != BuiltInData.UpscaleId).ToList();
        var profiles = BuiltInData.CreateProfiles();
        var slots = Slots(profiles);

        Assert.True(SettingsMigrations.ApplyToLibrary(72, presets, profiles));

        Assert.Equal(BuiltInData.CreatePresets().Select(p => p.Id), presets.Select(p => p.Id));
        Assert.Equal(slots, Slots(profiles));
        Assert.False(SettingsMigrations.ApplyToLibrary(AppSettings.CurrentSchemaVersion, presets, profiles));
    }

    [Fact]
    public void Schema_73_falls_back_to_after_background_then_the_end()
    {
        var presets = BuiltInData.CreatePresets().Where(p => p.Id is not (BuiltInData.UpscaleId or BuiltInData.MetadataId)).ToList();
        var profiles = BuiltInData.CreateProfiles();

        // From 72: the Metadata step is behind, so a library without it stays without it.
        Assert.True(SettingsMigrations.ApplyToLibrary(72, presets, profiles));
        Assert.Equal(BuiltInData.UpscaleId, presets[presets.FindIndex(p => p.Id == BuiltInData.BackgroundId) + 1].Id);

        presets = BuiltInData.CreatePresets()
            .Where(p => p.Id is not (BuiltInData.UpscaleId or BuiltInData.MetadataId or BuiltInData.BackgroundId)).ToList();
        Assert.True(SettingsMigrations.ApplyToLibrary(72, presets, profiles));
        Assert.Equal(BuiltInData.UpscaleId, presets[^1].Id);

        // Already there: nothing changes, and it isn't added twice.
        var count = presets.Count;
        Assert.False(SettingsMigrations.ApplyToLibrary(72, presets, profiles));
        Assert.Equal(count, presets.Count);
    }
}
