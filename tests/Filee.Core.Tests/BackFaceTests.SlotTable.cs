using System.Text.Json;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public partial class BackFaceTests
{
    // Approved slot table (#806). This is a new-install/reset contract, not migration input.
    internal static readonly Dictionary<string, (string[] Front, string[] Back)> ExpectedFaces = new()
    {
        ["images"] = (["to-png", "to-jpg", "to-webp", "to-gif", "to-pdf", "trace-svg"], ["tool-crop", "rotate", "remove-background", "merge-images", "compress-image", "half-size", "tool-image-edit", "zip-all"]),
        ["pdf"] = (["to-png", "to-jpg", "to-docx", "pdf-slides", "to-txt", "to-hwpx", "to-hwp"], ["pdf-edit", "pdf-split", "merge-pdf", "compress-pdf", "remove-metadata", "to-tiff", "to-html", "zip-all"]),
        ["office"] = (["to-pdf", "to-hwpx", "to-hwp", "to-docx", "to-txt", "to-png", "merge-pdf"], ["to-md", "pdf-long-image", "zip-all"]),
        ["spreadsheets"] = (["to-pdf", "to-xlsx", "to-csv", "to-hwpx", "to-hwp", "merge-pdf"], ["tool-sheet-studio", "to-docx", "to-vcf", "to-ics", "zip-all"]),
        ["presentations"] = (["to-pdf", "to-png", "to-jpg", "to-pptx", "to-hwpx", "to-hwp", "merge-pdf"], ["to-odp", "pdf-long-image", "to-docx", "zip-all"]),
        ["hwp"] = (["to-pdf", "to-docx", "to-hwpx", "to-hwp", "to-png", "to-txt", "merge-pdf"], ["to-html", "to-odt", "pdf-long-image", "zip-all"]),
        ["text"] = (["to-pdf", "to-hwpx", "to-hwp", "to-docx", "to-html", "to-txt"], ["to-md", "to-odt", "merge-pdf", "zip-all"]),
        ["ebooks"] = (["to-pdf", "to-epub", "to-docx", "to-txt", "to-azw3", "to-kepub"], ["to-md", "zip-all"]),
        ["video"] = (["to-mp4", "mp4-720", "to-mov", "to-gif", "to-mp3", "caption-video"], ["tool-clip", "compress-video", "merge-videos", "remove-metadata", "mp4-av1", "extract-frames-png", "extract-audio", "zip-all"]),
        ["audio"] = (["to-mp3", "mp3-128", "to-m4a", "to-wav", "caption-video"], ["compress-audio", "merge-audio", "remove-metadata", "to-alac", "zip-all"]),
        ["subtitles"] = (["to-srt", "to-vtt"], ["zip-all"]),
        ["vector"] = (["to-png", "to-pdf", "to-svg", "to-jpg", "to-webp"], ["to-tiff", "to-ico", "to-icns", "to-dxf", "zip-all"]),
        ["archives"] = (["extract", "zip-all", "to-zip", "to-7z"], []),
        ["cad"] = (["to-pdf", "to-png", "to-svg", "to-dxf", "to-dwg"], ["to-jpg", "merge-pdf", "zip-all"]),
        ["fonts"] = (["to-woff2", "to-woff", "to-ttf", "to-otf"], ["zip-all"]),
        ["data"] = (["to-json", "to-yaml", "to-xml", "to-csv", "to-xlsx"], ["to-ods", "to-html", "to-md", "zip-all"]),
        ["models"] = (["to-glb", "to-stl", "to-obj", "to-3mf", "to-png"], ["zip-all"]),
        ["geo"] = (["to-geojson", "to-shp", "to-kml", "to-csv", "to-gpx"], ["zip-all"]),
        ["mixed"] = (["to-pdf", "merge-pdf", "zip-all", "to-png", "to-txt"], ["to-jpg", "to-docx"]),
    };

    // Work semantics from the approved table, not target extensions: merges and the sheet studio depend on input.
    private static readonly Dictionary<(string Donut, string Preset), char> Classification = ExpectedFaces
        .SelectMany(p => p.Value.Front.Concat(p.Value.Back).Select(id => (p.Key, Id: id)))
        .ToDictionary(p => (p.Key, p.Id), p => p.Id switch
        {
            "zip-all" => 'B',
            "tool-crop" or "rotate" or "remove-background" or "merge-images" or "compress-image" or "half-size"
                or "tool-image-edit" or "pdf-edit" or "pdf-split" or "compress-pdf" or "remove-metadata"
                or "tool-clip" or "compress-video" or "merge-videos" or "compress-audio" or "merge-audio" => 'E',
            "merge-pdf" when p.Key == "pdf" => 'E',
            _ => 'V',
        });

    private static IEnumerable<char> WorkOf(string donut, string id, Dictionary<string, Preset> presets)
    {
        if (presets[id].IsGroup)
        {
            // Both built-in groups in the table contain only edits, including every leaf.
            foreach (var child in presets[id].SubPresetIds)
            {
                Assert.Contains(child, new[] { "rotate-left", "rotate-right", "rotate-180", "tool-pdf-crop",
                    "pdf-rotate-left", "pdf-rotate-right", "tool-pdf-pages", "pdf-split", "tool-pdf-add" });
                Assert.False(presets[child].IsGroup);
                yield return 'E';
            }
        }
        else
            yield return Classification[(donut, id)];
    }

    [Fact]
    public void DefaultFronts_HoldOnlyConversions()
    {
        var presets = BuiltInData.CreatePresets().ToDictionary(p => p.Id);
        foreach (var profile in BuiltInData.CreateProfiles())
            foreach (var id in profile.PresetIds)
                foreach (var work in WorkOf(profile.Id, id, presets))
                    Assert.True(work == 'V' || work == 'B' && profile.Id is "archives" or "mixed", $"{profile.Id}/front: {id} is {work}");
    }

    [Fact]
    public void DefaultBacks_HoldConversionsOnlyAfterTools()
    {
        var presets = BuiltInData.CreatePresets().ToDictionary(p => p.Id);
        foreach (var profile in BuiltInData.CreateProfiles())
        {
            var conversionSeen = false;
            var bundleSeen = false;
            foreach (var id in profile.BackPresetIds!)
                foreach (var work in WorkOf(profile.Id, id, presets))
                {
                    Assert.False(bundleSeen, $"{profile.Id}/back: {id} follows zip-all");
                    if (work == 'E')
                    {
                        Assert.Contains(profile.Id, new[] { "images", "pdf", "video", "audio" });
                        Assert.False(conversionSeen, $"{profile.Id}/back: edit {id} follows a conversion");
                    }
                    conversionSeen |= work == 'V';
                    bundleSeen |= work == 'B';
                }
        }
    }

    [Fact]
    public void DefaultFaces_HoldAtMostEight()
    {
        var presets = BuiltInData.CreatePresets().ToDictionary(p => p.Id);
        var profiles = BuiltInData.CreateProfiles();
        Assert.Equal(ExpectedFaces.Keys, profiles.Select(p => p.Id));
        foreach (var profile in profiles)
        {
            var expected = ExpectedFaces[profile.Id];
            Assert.Equal(expected.Front, profile.PresetIds);
            Assert.Equal(expected.Back, profile.BackPresetIds);
            foreach (var face in new[] { profile.PresetIds, profile.BackPresetIds! })
            {
                Assert.InRange(face.Count, 0, 8);
                Assert.All(face, id => Assert.True(presets.ContainsKey(id), $"{profile.Id}: unknown {id}"));
                foreach (var group in face.Select(id => presets[id]).Where(p => p.IsGroup))
                {
                    Assert.InRange(group.SubPresetIds.Count, 1, Preset.MaxSubPresets);
                    Assert.All(group.SubPresetIds, id => Assert.True(presets.ContainsKey(id), $"{group.Id}: unknown {id}"));
                }
            }
        }
    }

    [Fact]
    public void FrozenSchema58Profiles_MatchBaseline()
    {
        var expected = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "SlotTable", "profiles.json")), FileeJsonContext.Default.ListToolbarProfile)!;
        Assert.Equal(JsonSerializer.Serialize(expected, FileeJsonContext.Default.ListToolbarProfile),
            JsonSerializer.Serialize(BuiltInData.ProfilesSchema58(), FileeJsonContext.Default.ListToolbarProfile));
    }
}
