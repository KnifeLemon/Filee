// Merged cells in spreadsheets made from tables (#1846): the preset's default and how a presets file with a value this
// version doesn't know reads.

using System.Text.Json;
using System.Text.Json.Nodes;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class MergedCellsPresetTests
{
    [Fact]
    public void Smart_is_the_default_set_by_the_option_itself()
    {
        Assert.Equal(MergedCells.Smart, new Preset().Document.MergedCells);
        Assert.Equal(MergedCells.Smart, new DocumentOptions().MergedCells);
    }

    [Theory]
    [InlineData("\"Diagonal\"")]
    [InlineData("7")]
    public void An_unknown_value_reads_as_smart_and_the_other_presets_survive(string unknown)
    {
        var tables = new Preset { Name = "Tables", TargetFormat = "xlsx", Document = { MergedCells = MergedCells.Fill } };
        var other = new Preset { Name = "Other", TargetFormat = "pdf", Document = { MergedCells = MergedCells.Keep } };
        var json = JsonSerializer.Serialize([tables, other], FileeJsonContext.Default.ListPreset);
        Assert.Contains("\"mergedCells\": \"Fill\"", json); // by name, as the other enums

        var newer = json.Replace("\"mergedCells\": \"Fill\"", $"\"mergedCells\": {unknown}");
        var read = JsonSerializer.Deserialize(newer, FileeJsonContext.Default.ListPreset)!;

        Assert.Equal(2, read.Count);
        Assert.Equal(("Tables", "xlsx", MergedCells.Smart), (read[0].Name, read[0].TargetFormat, read[0].Document.MergedCells));
        Assert.Equal(("Other", MergedCells.Keep), (read[1].Name, read[1].Document.MergedCells));
    }

    [Fact]
    public void A_preset_saved_before_the_option_reads_as_smart()
    {
        var json = JsonSerializer.Serialize([new Preset { Name = "Old", TargetFormat = "xlsx", Document = { MergedCells = MergedCells.Keep } }],
            FileeJsonContext.Default.ListPreset);
        var node = JsonNode.Parse(json)!;
        Assert.True(node[0]!["document"]!.AsObject().Remove("mergedCells"));
        var old = node.ToJsonString();
        Assert.DoesNotContain("mergedCells", old);

        Assert.Equal(MergedCells.Smart, Assert.Single(JsonSerializer.Deserialize(old, FileeJsonContext.Default.ListPreset)!).Document.MergedCells);
    }
}
