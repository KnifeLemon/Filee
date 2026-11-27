// Colour-mapped raster preview presets (#155): RasterColours option serialization/cloning, Maps (geo) preset-level
// field gating via FormatFields.IsShown(Preset, ...), built-in placement, and schema 107 migration.

using System.Text.Json;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class RasterColoursPresetTests
{
    private static List<string> Slots(IEnumerable<ToolbarProfile> profiles) =>
        [.. profiles.Select(p => $"{p.Id}: {string.Join(",", p.PresetIds)} | {string.Join(",", p.BackPresetIds ?? [])}")];

    [Fact]
    public void Built_in_colour_presets_sit_after_geo_presets_and_take_no_donut_slot()
    {
        var presets = BuiltInData.CreatePresets();
        var ids = presets.Select(p => p.Id).ToList();
        var kmz = ids.IndexOf("to-kmz");

        Assert.True(kmz >= 0);
        Assert.Equal(["geo-colours-terrain", "geo-colours-viridis"], ids.Skip(kmz + 1).Take(2));

        var terrain = presets.Single(p => p.Id == "geo-colours-terrain");
        var viridis = presets.Single(p => p.Id == "geo-colours-viridis");
        Assert.Equal(("png", RasterColours.Terrain, "preset.geo_colours_terrain", "{name}_colours", "geo"),
            (terrain.TargetFormat, terrain.Image.RasterColours, terrain.NameKey, terrain.Output.FileNamePattern, terrain.FieldId));
        Assert.Equal(("png", RasterColours.Viridis, "preset.geo_colours_viridis", "{name}_colours", "geo"),
            (viridis.TargetFormat, viridis.Image.RasterColours, viridis.NameKey, viridis.Output.FileNamePattern, viridis.FieldId));

        Assert.All(BuiltInData.CreateProfiles(), p =>
            Assert.DoesNotContain("geo-colours-terrain", p.PresetIds.Concat(p.BackPresetIds ?? [])));
        Assert.All(BuiltInData.CreateProfiles(), p =>
            Assert.DoesNotContain("geo-colours-viridis", p.PresetIds.Concat(p.BackPresetIds ?? [])));
    }

    [Fact]
    public void Migration_from_106_adds_both_presets_once_without_changing_any_donut()
    {
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => p.Id is "geo-colours-terrain" or "geo-colours-viridis");
        var profiles = BuiltInData.CreateProfiles();
        var beforeSlots = Slots(profiles);

        Assert.True(SettingsMigrations.ApplyToLibrary(106, presets, profiles));
        Assert.Equal(BuiltInData.CreatePresets().Select(p => p.Id), presets.Select(p => p.Id));
        Assert.Equal(beforeSlots, Slots(profiles));

        // Idempotent on a second run.
        Assert.False(SettingsMigrations.ApplyToLibrary(106, presets, profiles));
    }

    [Fact]
    public void Preset_field_gating_hides_colour_presets_when_geo_is_off_and_leaves_plain_png_shown()
    {
        var terrain = BuiltInData.CreatePresets().Single(p => p.Id == "geo-colours-terrain");
        var viridis = BuiltInData.CreatePresets().Single(p => p.Id == "geo-colours-viridis");
        var copy = terrain.Clone();
        copy.Id = "my-dem-colours";
        var plainPng = BuiltInData.CreatePresets().Single(p => p.Id == "to-png");

        var geoOff = new HashSet<string>(["geo"], StringComparer.OrdinalIgnoreCase);
        var noneOff = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.False(FormatFields.IsShown(terrain, geoOff));
        Assert.False(FormatFields.IsShown(viridis, geoOff));
        Assert.False(FormatFields.IsShown(copy, geoOff));
        Assert.True(FormatFields.IsShown(plainPng, geoOff));

        Assert.True(FormatFields.IsShown(terrain, noneOff));
        Assert.True(FormatFields.IsShown(viridis, noneOff));
        Assert.True(FormatFields.IsShown(copy, noneOff));
        Assert.Null(plainPng.FieldId);
    }

    [Fact]
    public void Raster_colours_round_trips_in_json_reads_unknown_as_off_and_clones()
    {
        const string json = """
        [
          { "id": "old", "targetFormat": "png", "image": {} },
          { "id": "newer", "targetFormat": "png", "image": { "rasterColours": "Rainbow" } },
          { "id": "terrain", "targetFormat": "png", "image": { "rasterColours": "Terrain" } },
          { "id": "viridis", "targetFormat": "png", "image": { "rasterColours": "Viridis" } }
        ]
        """;

        var list = JsonSerializer.Deserialize(json, FileeJsonContext.Default.ListPreset)!;
        var byId = list.ToDictionary(p => p.Id, p => p.Image.RasterColours);

        Assert.Equal(RasterColours.Off, byId["old"]);
        Assert.Equal(RasterColours.Off, byId["newer"]);
        Assert.Equal(RasterColours.Terrain, byId["terrain"]);
        Assert.Equal(RasterColours.Viridis, byId["viridis"]);

        var cloned = list.Single(p => p.Id == "viridis").Clone();
        Assert.Equal(RasterColours.Viridis, cloned.Image.RasterColours);
        Assert.Equal("geo", cloned.FieldId);
    }
}
