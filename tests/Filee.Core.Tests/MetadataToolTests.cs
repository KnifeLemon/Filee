// "Metadata": settings schema 69 brings the tool to existing libraries (in the palette only, after Add background), the
// conversion it starts carries the edits and writes them through the one engine that writes only metadata (never a
// re-encode), a route without it is refused, edits are read leniently, History offers no Retry an older Filee would run
// without the edits, and "Replace the original" is the tool's own output rule.

using System.Text.Json;
using System.Text.Json.Nodes;
using Filee.Core.Conversion;
using Filee.Core.History;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class MetadataToolTests
{
    private static Preset Tool() => BuiltInData.CreatePresets().Single(p => p.Id == BuiltInData.MetadataId);

    private static MetadataEdits Edits() => new()
    {
        Set = { ["XMP-dc:Title"] = ["Harbour"], ["XMP-dc:Creator"] = ["Smith, John", "Kim"] },
        Remove = ["GPS:GPSLatitude"],
        RemoveLocation = true,
    };

    private static UserDataStore Saved(TempDir dir)
    {
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.SaveSettings();
        store.SaveLibrary();
        return store;
    }

    [Fact]
    public void Schema_69_adds_the_Metadata_tool_after_Add_background_and_to_no_donut()
    {
        using var dir = new TempDir();
        Saved(dir);
        var path = Path.Combine(dir.Path, "presets.json");
        var presets = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        presets.Remove(presets.Single(n => n!["id"]!.GetValue<string>() == BuiltInData.MetadataId));
        File.WriteAllText(path, presets.ToJsonString());
        var settingsPath = Path.Combine(dir.Path, "settings.json");
        var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        settings["schemaVersion"] = 68;
        File.WriteAllText(settingsPath, settings.ToJsonString());

        var store = new UserDataStore(dir.Path);
        store.Load();

        var tool = store.FindPreset(BuiltInData.MetadataId)!;
        Assert.True(tool.IsMetadataTool);
        Assert.Equal("{name}_metadata", tool.Output.FileNamePattern);
        // Right after "Add background", where a new library has it, not at the end of the list.
        var ids = store.Presets.Select(p => p.Id).ToList();
        Assert.Equal(ids.IndexOf(BuiltInData.BackgroundId) + 1, ids.IndexOf(BuiltInData.MetadataId));
        Assert.Equal(BuiltInData.CreatePresets().Select(p => p.Id), ids.Where(BuiltInData.CreatePresets().Select(p => p.Id).Contains));
        Assert.DoesNotContain(store.Profiles, p => p.PresetIds.Contains(BuiltInData.MetadataId) || p.BackPresetIds?.Contains(BuiltInData.MetadataId) == true);
    }

    [Fact]
    public void Schema_69_puts_the_tool_at_the_end_without_Add_background_and_never_twice()
    {
        var presets = BuiltInData.CreatePresets().Where(p => p.Id is not (BuiltInData.MetadataId or BuiltInData.BackgroundId)).ToList();
        var profiles = BuiltInData.CreateProfiles();

        Assert.True(SettingsMigrations.ApplyToLibrary(68, presets, profiles));
        Assert.Equal(BuiltInData.MetadataId, presets[^1].Id);

        // Already there (a library a newer Filee saved): nothing changes.
        var count = presets.Count;
        Assert.False(SettingsMigrations.ApplyToLibrary(68, presets, profiles));
        Assert.Equal(count, presets.Count);
        Assert.False(SettingsMigrations.ApplyToLibrary(AppSettings.CurrentSchemaVersion, presets, profiles));
    }

    [Fact]
    public void The_conversion_carries_the_edits_and_the_tools_output_rule_and_leaves_the_tool_as_it_was()
    {
        var tool = Tool();
        Assert.Null(tool.Metadata);
        Assert.False(tool.EditsMetadata);

        var conversion = MetadataTool.ConversionOf(tool, Edits(), replaceOriginal: true);

        Assert.Equal((PresetKind.Convert, BuiltInData.SameAsSource, (string?)null), (conversion.Kind, conversion.TargetFormat, conversion.Tool));
        Assert.Empty(conversion.SubPresetIds);
        Assert.True(conversion.EditsMetadata);
        Assert.Equal(["Harbour"], conversion.Metadata!.Set["XMP-dc:Title"]);
        Assert.True(conversion.Output.ReplaceOriginal);
        Assert.Equal("{name}_metadata", conversion.Output.FileNamePattern);
        // A copy with only its metadata changed is about as large as the file: "Only if smaller" would keep the
        // original.
        Assert.False(conversion.Output.OnlyIfSmaller);
        // Nothing else is asked of the file: no step of the route may re-encode it.
        Assert.False(conversion.Image.ReworksPixels);
        Assert.False(conversion.Pdf.RemoveMetadata || conversion.Media.RemoveMetadata);

        // The edits are the conversion's own: changing them afterwards changes nothing already started.
        var edits = Edits();
        var second = MetadataTool.ConversionOf(tool, edits, replaceOriginal: false);
        edits.Set["XMP-dc:Title"] = ["Changed"];
        edits.Set["XMP-dc:Creator"].Add("Lee");
        Assert.Equal(["Harbour"], second.Metadata!.Set["XMP-dc:Title"]);
        Assert.Equal(["Smith, John", "Kim"], second.Metadata.Set["XMP-dc:Creator"]);
        Assert.False(second.Output.ReplaceOriginal);
        Assert.True(tool.IsMetadataTool);
        Assert.Null(tool.Metadata);
    }

    [Fact]
    public void Replace_the_original_is_kept_in_the_tools_output_rule()
    {
        var tool = Tool();
        Assert.False(MetadataTool.ReplacesOriginal(tool));

        MetadataTool.Remember(tool, replaceOriginal: true);
        Assert.True(tool.Output.ReplaceOriginal);
        Assert.True(MetadataTool.ReplacesOriginal(tool));
        var json = JsonSerializer.Serialize(new List<Preset> { tool }, FileeJsonContext.Default.ListPreset);
        Assert.True(MetadataTool.ReplacesOriginal(JsonSerializer.Deserialize(json, FileeJsonContext.Default.ListPreset)!.Single()));
    }

    [Fact]
    public void Edits_survive_a_copy_of_the_preset_and_a_round_trip_and_an_empty_one_edits_nothing()
    {
        var preset = MetadataTool.ConversionOf(Tool(), Edits(), replaceOriginal: false);
        var clone = preset.Clone();
        clone.Metadata!.Set["XMP-dc:Title"] = ["Other"];
        clone.Metadata.Set["XMP-dc:Creator"].Add("Lee");
        clone.Metadata.Remove.Add("IFD0:Make");
        Assert.Equal(["Harbour"], preset.Metadata!.Set["XMP-dc:Title"]);
        Assert.Equal(["Smith, John", "Kim"], preset.Metadata.Set["XMP-dc:Creator"]);
        Assert.DoesNotContain("IFD0:Make", preset.Metadata.Remove);

        var json = JsonSerializer.Serialize(new List<Preset> { preset }, FileeJsonContext.Default.ListPreset);
        var read = JsonSerializer.Deserialize(json, FileeJsonContext.Default.ListPreset)!.Single().Metadata!;
        // A list keeps its entries whole: "Smith, John" is one creator, not two.
        Assert.Equal(["Harbour"], read.Set["XMP-dc:Title"]);
        Assert.Equal(["Smith, John", "Kim"], read.Set["XMP-dc:Creator"]);
        Assert.Equal((true, false), (read.RemoveLocation, read.RemoveAll));
        Assert.Equal(["GPS:GPSLatitude"], read.Remove);

        Assert.True(new MetadataEdits().IsEmpty);
        Assert.False(new Preset { Metadata = new MetadataEdits() }.EditsMetadata);
        Assert.False(new MetadataEdits { RemoveAll = true }.IsEmpty);
    }

    [Theory]
    [InlineData("XMP-dc:Title", true)]
    [InlineData("IPTC:Caption-Abstract", true)]
    [InlineData("XMP-iptcCore:CreatorWorkEmail", true)]
    [InlineData("Title", false)]                          // no group: ExifTool would write it wherever it likes
    [InlineData("XMP-dc:Title=x", false)]
    [InlineData("-all", false)]
    [InlineData("XMP-dc:Title\n-o", false)]
    [InlineData("XMP-dc:Title ", false)]
    [InlineData("XMP-dc:GPS*", false)]
    [InlineData("a:b:c", false)]
    [InlineData("", false)]
    public void Only_one_named_field_is_a_field(string key, bool accepted) =>
        Assert.Equal(accepted, MetadataEdits.IsFieldKey(key));

    [Fact]
    public void Edits_edited_by_hand_are_read_with_what_isnt_a_field_or_has_no_value_left_out()
    {
        var preset = MetadataTool.ConversionOf(Tool(), Edits(), replaceOriginal: false);
        var json = JsonSerializer.Serialize(new List<Preset> { preset }, FileeJsonContext.Default.ListPreset);
        var title = json[json.IndexOf("\"XMP-dc:Title\"", StringComparison.Ordinal)..];
        title = title[..(title.IndexOf(']') + 1)];
        var creator = json[json.IndexOf("\"XMP-dc:Creator\"", StringComparison.Ordinal)..];
        creator = creator[..(creator.IndexOf(']') + 1)];

        var edited = json.Replace(title, "\"XMP-dc:Title\": null, \"-all\": [\"x\"], \"Title\": [\"y\"]", StringComparison.Ordinal)
            .Replace(creator, "\"XMP-dc:Creator\": [\"Kim\", null]", StringComparison.Ordinal)
            .Replace("\"GPS:GPSLatitude\"", "\"GPS:GPSLatitude\", null, \"-o\", \"XMP-dc:Title\\n-all\"", StringComparison.Ordinal);
        var read = JsonSerializer.Deserialize(edited, FileeJsonContext.Default.ListPreset)!.Single().Metadata!;

        Assert.Equal(["XMP-dc:Creator"], read.Set.Keys);
        Assert.Equal(["Kim"], read.Set["XMP-dc:Creator"]);
        Assert.Equal(["GPS:GPSLatitude"], read.Remove);
    }

    [Fact]
    public void The_edits_are_written_only_by_an_engine_that_writes_only_metadata_and_refused_without_one()
    {
        var plain = new FakeEngine("plain", [new ConversionEdge("jpg", "jpg"), new ConversionEdge("mp4", "mp4")]);
        var exifTool = new FakeEngine("exiftool", [new ConversionEdge("jpg", "jpg", Metadata: true)]);
        var planner = new RoutePlanner([plain, exifTool]);

        // An ordinary conversion never takes the metadata edge, and one with edits takes nothing else.
        Assert.Same(plain, planner.Plan("jpg", "jpg")!.Steps.Single().Converter);
        var route = planner.Plan("jpg", "jpg", metadata: true)!;
        Assert.Equal((exifTool, true), (route.Steps.Single().Converter, route.Steps.Single().Metadata));
        Assert.Null(planner.Plan("mp4", "mp4", metadata: true));
        Assert.Null(planner.Plan("jpg", "png", metadata: true));
        Assert.True(planner.CanConvert("jpg", "jpg", metadata: true));
        Assert.False(planner.CanConvert("mp4", "mp4", metadata: true));
        // Never a step of a longer route either.
        Assert.Null(new RoutePlanner([exifTool, new FakeEngine("png", [new ConversionEdge("jpg", "png")])]).Plan("jpg", "png", metadata: true));
        Assert.Same(plain, new RoutePlanner([exifTool, plain]).Plan("jpg", "jpg")!.Steps.Single().Converter);
    }

    [Fact]
    public async Task A_file_whose_metadata_no_engine_writes_fails_with_the_route_asked_for_and_is_never_written_otherwise()
    {
        using var dir = new TempDir();
        var source = Path.Combine(dir.Path, "song.mp4");
        File.WriteAllBytes(source, [1, 2, 3]);
        var plain = new FakeEngine("plain", [new ConversionEdge("mp4", "mp4")]);
        await using var queue = new JobQueue(new ConverterCatalog([plain]));
        var job = new ConversionJob([source], MetadataTool.ConversionOf(Tool(), Edits(), replaceOriginal: false), "Metadata");

        await queue.RunAsync(job);

        var file = job.Files.Single();
        Assert.Equal(FileState.Failed, file.State);
        Assert.Equal("error.no_route", file.ErrorKey);
        Assert.Equal(new RouteRequest("mp4", "mp4", Metadata: true), file.FailedRoute);
        Assert.Equal(0, plain.Runs);
        Assert.Empty(job.Outputs);
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void A_failed_conversion_with_edits_offers_no_retry_an_older_Filee_would_run_without_them()
    {
        var job = new ConversionJob(["photo.jpg"], MetadataTool.ConversionOf(Tool(), Edits(), replaceOriginal: false), "Metadata");
        job.Files[0].State = FileState.Failed;
        Assert.Null(HistoryEntry.From(job).Preset);
    }

    private sealed class FakeEngine(string id, IReadOnlyList<ConversionEdge> edges) : IConverter
    {
        public int Runs { get; private set; }
        public string Id => id;
        public string DisplayName => id;
        public IReadOnlyList<ConversionEdge> Edges => edges;
        public int MaxParallelism => 1;
        public EngineStatus GetStatus() => EngineStatus.Available();

        public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Runs++;
            var output = step.Output.Allocate(step.To)!;
            File.Copy(step.InputPath, output);
            return Task.FromResult<IReadOnlyList<string>>([output]);
        }
    }
}
