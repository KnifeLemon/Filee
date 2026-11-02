using Filee.Core.Presets;
using Filee.Core.Profiles;

namespace Filee.Core.Tests;

public partial class BackFaceTests
{
    // Slot table of #806, with Translate from #836 first on the document, text and e-book backs. This is a new-install/reset contract, not migration input.
    internal static readonly Dictionary<string, (string[] Front, string[] Back)> ExpectedFaces = new()
    {
        ["images"] = (["to-png", "to-jpg", "to-webp", "to-gif", "to-pdf", "trace-svg"], ["tool-crop", "rotate", "remove-background", "merge-images", "compress-image", "half-size", "tool-image-edit", "zip-all"]),
        ["pdf"] = (["to-png", "to-jpg", "to-docx", "pdf-slides", "to-txt", "to-hwpx", "to-hwp"], ["pdf-edit", "pdf-split", "merge-pdf", "compress-pdf", "remove-metadata", "to-tiff", "to-html", "zip-all"]),
        ["office"] = (["to-pdf", "to-hwpx", "to-hwp", "to-docx", "to-txt", "to-png", "merge-pdf"], ["translate", "to-md", "pdf-long-image", "zip-all"]),
        ["spreadsheets"] = (["to-pdf", "to-xlsx", "to-csv", "to-hwpx", "to-hwp", "merge-pdf"], ["tool-sheet-studio", "to-docx", "to-vcf", "to-ics", "zip-all"]),
        ["presentations"] = (["to-pdf", "to-png", "to-jpg", "to-pptx", "to-hwpx", "to-hwp", "merge-pdf"], ["to-odp", "pdf-long-image", "to-docx", "zip-all"]),
        ["hwp"] = (["to-pdf", "to-docx", "to-hwpx", "to-hwp", "to-png", "to-txt", "merge-pdf"], ["to-html", "to-odt", "pdf-long-image", "zip-all"]),
        ["text"] = (["to-pdf", "to-hwpx", "to-hwp", "to-docx", "to-html", "to-txt"], ["translate", "to-md", "to-odt", "merge-pdf", "zip-all"]),
        ["ebooks"] = (["to-pdf", "to-epub", "to-docx", "to-txt", "to-azw3", "to-kepub"], ["translate", "to-md", "zip-all"]),
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

    // Work semantics from the slot table above, not target extensions: merges and the sheet studio depend on input.
    private static readonly Dictionary<(string Donut, string Preset), char> Classification = ExpectedFaces
        .SelectMany(p => p.Value.Front.Concat(p.Value.Back).Select(id => (p.Key, Id: id)))
        .ToDictionary(p => (p.Key, p.Id), p => p.Id switch
        {
            "zip-all" => 'B',
            "tool-crop" or "rotate" or "remove-background" or "merge-images" or "compress-image" or "half-size"
                or "tool-image-edit" or "pdf-edit" or "pdf-split" or "compress-pdf" or "remove-metadata"
                or "tool-clip" or "compress-video" or "merge-videos" or "compress-audio" or "merge-audio" or "translate" => 'E',
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
    public void A_new_library_has_only_conversions_on_its_fronts()
    {
        var presets = BuiltInData.CreatePresets().ToDictionary(p => p.Id);
        foreach (var profile in BuiltInData.CreateProfiles())
            foreach (var id in profile.PresetIds)
                foreach (var work in WorkOf(profile.Id, id, presets))
                    Assert.True(work == 'V' || work == 'B' && profile.Id is "archives" or "mixed", $"{profile.Id}/front: {id} is {work}");
    }

    [Fact]
    public void A_new_library_has_the_edits_first_on_its_backs_and_nothing_after_the_zip()
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
                        Assert.Contains(profile.Id, new[] { "images", "pdf", "office", "text", "ebooks", "video", "audio" });
                        Assert.False(conversionSeen, $"{profile.Id}/back: edit {id} follows a conversion");
                    }
                    conversionSeen |= work == 'V';
                    bundleSeen |= work == 'B';
                }
        }
    }

    [Fact]
    public void A_new_library_has_the_new_layout_and_at_most_eight_a_face()
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

    // CreateProfiles() on main before the slot cleanup (328225c9, with Translate from #836 on three backs): the layout
    // migrations and old bundles keep. Extensions are left out on purpose: both layouts take them from ExtensionsOf,
    // and FormatRegistry grows. A donut added later goes into CreateProfiles(), not here.
    private static readonly (string Id, string NameKey, bool IsFallback, string[] Front, string[] Back)[] Schema58Faces =
    [
        ("images", "profile.images", false, ["to-png", "to-jpg", "to-webp", "to-pdf", "trace-svg", "trace-line-art", "to-tiff", "to-bmp", "to-ico", "half-size", "tool-image-edit", "tool-hide"],
            ["tool-crop", "rotate", "flip", "remove-background", "trim-transparent", "grayscale", "to-avif", "merge-images", "merge-pdf", "compress-image", "remove-metadata", "zip-all"]),
        ("pdf", "profile.pdf", false, ["to-png", "to-jpg", "pdf-long-image", "to-docx", "pdf-slides", "to-txt", "to-md", "to-epub", "to-hwpx", "to-hwp", "pdf-split", "merge-pdf"],
            ["pdf-edit", "compress-pdf", "to-webp", "to-tiff", "to-html", "to-odt", "to-rtf", "to-azw3", "to-cbz", "remove-metadata", "zip-all"]),
        ("office", "profile.office", false, ["to-pdf", "to-hwpx", "to-hwp", "to-docx", "to-odt", "to-txt", "to-html", "to-epub", "to-png", "merge-pdf"],
            ["translate", "to-md", "to-rtf", "to-jpg", "pdf-long-image", "to-azw3", "to-kepub", "zip-all"]),
        ("spreadsheets", "profile.spreadsheets", false, ["to-pdf", "to-xlsx", "to-csv", "to-json", "to-ods", "to-hwpx", "to-hwp", "to-html", "merge-pdf"],
            ["tool-sheet-studio", "to-docx", "to-md", "to-txt", "to-yaml", "to-xml", "to-vcf", "to-ics", "to-png", "zip-all"]),
        ("presentations", "profile.presentations", false, ["to-pdf", "to-png", "to-jpg", "to-pptx", "to-hwpx", "to-hwp", "merge-pdf"],
            ["to-odp", "to-webp", "pdf-long-image", "to-docx", "to-txt", "to-md", "to-html", "zip-all"]),
        ("hwp", "profile.hwp", false, ["to-pdf", "to-docx", "to-hwpx", "to-hwp", "to-png", "to-jpg", "to-txt", "to-md", "to-epub", "merge-pdf"],
            ["to-html", "to-odt", "to-rtf", "pdf-long-image", "to-azw3", "zip-all"]),
        ("text", "profile.text", false, ["to-pdf", "to-hwpx", "to-hwp", "to-docx", "to-html", "to-txt", "to-epub"],
            ["translate", "to-md", "to-odt", "to-rtf", "to-kepub", "merge-pdf", "zip-all"]),
        ("ebooks", "profile.ebooks", false, ["to-pdf", "to-epub", "to-docx", "to-txt", "to-azw3", "to-mobi", "to-kepub", "to-hwpx", "to-hwp", "to-html"],
            ["translate", "to-fb2", "to-md", "to-rtf", "to-odt", "merge-pdf", "zip-all"]),
        ("video", "profile.video", false, ["to-mp4", "mp4-720", "mp4-hevc", "to-webm", "to-mov", "to-gif", "to-mp3", "extract-subtitles", "caption-video", "to-mkv", "to-avi"],
            ["tool-clip", "mp4-av1", "to-webp", "extract-frames-png", "extract-frames-jpg", "live-photo-iphone", "motion-photo-android", "extract-audio", "to-wav", "compress-video", "merge-videos", "zip-all"]),
        ("audio", "profile.audio", false, ["to-mp3", "mp3-128", "to-m4a", "to-wav", "to-flac", "to-ogg", "to-opus", "to-aac", "caption-video"],
            ["to-alac", "compress-audio", "merge-audio", "remove-metadata", "zip-all"]),
        ("subtitles", "profile.subtitles", false, ["to-srt", "to-vtt", "to-ass", "to-smi", "to-lrc", "to-ttml"],
            ["zip-all"]),
        ("vector", "profile.vector", false, ["to-png", "to-pdf", "to-svg", "to-jpg", "to-webp", "to-eps"],
            ["to-tiff", "to-ico", "to-icns", "to-avif", "to-dxf", "merge-pdf", "zip-all"]),
        ("archives", "profile.archives", false, ["extract", "zip-all", "to-zip", "to-7z", "to-tgz", "to-tar"],
            []),
        ("cad", "profile.cad", false, ["to-pdf", "to-png", "to-svg", "to-dxf", "to-dwg"],
            ["to-jpg", "to-webp", "to-tiff", "merge-pdf", "zip-all"]),
        ("fonts", "profile.fonts", false, ["to-woff2", "to-woff", "to-ttf", "to-otf", "to-eot"],
            ["zip-all"]),
        ("data", "profile.data", false, ["to-json", "to-yaml", "to-toml", "to-xml", "to-csv", "to-xlsx"],
            ["to-ods", "to-html", "to-md", "zip-all"]),
        ("models", "profile.models", false, ["to-glb", "to-stl", "to-obj", "to-3mf", "to-ply", "to-png"],
            ["to-gltf", "to-jpg", "to-webp", "zip-all"]),
        ("geo", "profile.geo", false, ["to-geojson", "to-shp", "to-kml", "to-csv", "to-gpx", "to-kmz"],
            ["zip-all"]),
        ("mixed", "profile.mixed", true, ["to-pdf", "merge-pdf", "zip-all", "to-png", "to-txt"],
            ["to-jpg", "to-webp", "to-docx", "to-md", "to-html", "compress-pdf"]),
    ];

    [Fact]
    public void The_schema_58_layout_stays_as_it_was_before_the_slot_cleanup()
    {
        var profiles = BuiltInData.ProfilesSchema58();
        Assert.Equal(Schema58Faces.Select(f => f.Id), profiles.Select(p => p.Id));
        foreach (var (profile, expected) in profiles.Zip(Schema58Faces))
        {
            Assert.Equal(expected.NameKey, profile.NameKey);
            Assert.Equal(expected.IsFallback, profile.IsFallback);
            Assert.Equal(expected.Front, profile.PresetIds);
            Assert.Equal(expected.Back, profile.BackPresetIds);
        }
    }
}
