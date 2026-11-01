using System.Text.Json;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public partial class UserDataStoreTests
{
    [Fact]
    public void Load_Schema58Defaults_SameAsBaseline() => CompareSlotBaseline("Defaults", import: false);

    [Fact]
    public void Import_Schema58Defaults_SameAsBaseline() => CompareSlotBaseline("Defaults", import: true);

    [Fact]
    public void Load_EditedLayout_SameAsBaseline() => CompareSlotBaseline("Edited", import: false);

    [Fact]
    public void Import_EditedLayout_SameAsBaseline() => CompareSlotBaseline("Edited", import: true);

    [Fact]
    public void Load_PdfFrontBefore18_SameAsBaseline() => CompareSlotBaseline("PdfBefore18", import: false);

    [Fact]
    public void Import_BundleWithOldPdfFront_SameAsBaseline() => CompareSlotBaseline("PdfBefore18", import: true);

    [Fact]
    public void Load_DefaultProfileWithoutBack_SameAsBaseline() => CompareSlotBaseline("DefaultWithoutBack", import: false);

    [Fact]
    public void Import_DefaultProfileWithoutBack_SameAsBaseline() => CompareSlotBaseline("DefaultWithoutBack", import: true);

    [Fact]
    public void Load_UserProfileWithoutBack_SameAsBaseline() => CompareSlotBaseline("UserWithoutBack", import: false);

    [Fact]
    public void Import_UserProfileWithoutBack_SameAsBaseline() => CompareSlotBaseline("UserWithoutBack", import: true);

    [Fact]
    public void Load_Schema29_SameAsBaseline() => CompareSlotBaseline("Schema29", import: false);

    private static string SlotFixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "SlotTable");

    // Captured by executing Load and Import on 51a6ba27, before changing the defaults.
    // Identical outputs share a file; preset values share presets.json with captured order and edited-PNG overrides.
    private static void CompareSlotBaseline(string scenario, bool import)
    {
        using var dir = new TempDir();
        var input = SlotInput(SlotFixtures, scenario);
        var store = new UserDataStore(dir.Path);
        if (import)
        {
            store.Load();
            // The receiving library is part of the comparison input, not a new installation's changing defaults.
            store.Profiles.Clear();
            store.Profiles.AddRange(JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(SlotFixtures, "profiles.json")),
                FileeJsonContext.Default.ListToolbarProfile)!);
            store.Presets.Clear();
            store.Presets.AddRange(JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(SlotFixtures, "presets.json")),
                FileeJsonContext.Default.ListPreset)!);
            var bundlePath = Path.Combine(dir.Path, "bundle.json");
            File.WriteAllText(bundlePath, JsonSerializer.Serialize(input, FileeJsonContext.Default.PresetBundle));
            store.Import(bundlePath);
        }
        else
        {
            File.WriteAllText(Path.Combine(dir.Path, "settings.json"), JsonSerializer.Serialize(
                new AppSettings { SchemaVersion = scenario == "PdfBefore18" ? 17 : scenario == "Schema29" ? 29 : 58 },
                FileeJsonContext.Default.AppSettings));
            File.WriteAllText(Path.Combine(dir.Path, "profiles.json"), JsonSerializer.Serialize(input.Profiles, FileeJsonContext.Default.ListToolbarProfile));
            File.WriteAllText(Path.Combine(dir.Path, "presets.json"), JsonSerializer.Serialize(input.Presets, FileeJsonContext.Default.ListPreset));
            store.Load();
        }
        var profilesFile = scenario switch
        {
            "Edited" => "Load-Edited-profiles.json",
            "UserWithoutBack" => "Load-UserWithoutBack-profiles.json",
            "PdfBefore18" when import => "Import-PdfBefore18-profiles.json",
            _ => "profiles.json",
        };
        var expectedProfiles = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(SlotFixtures, profilesFile)), FileeJsonContext.Default.ListToolbarProfile)!;
        Assert.Equal(JsonSerializer.Serialize(expectedProfiles, FileeJsonContext.Default.ListToolbarProfile),
            JsonSerializer.Serialize(store.Profiles, FileeJsonContext.Default.ListToolbarProfile));
        var expectedPresets = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(SlotFixtures, "presets.json")), FileeJsonContext.Default.ListPreset)!;
        if (scenario == "Edited")
            expectedPresets[0] = JsonSerializer.Deserialize("[" + File.ReadAllText(Path.Combine(SlotFixtures, "edited-png.json")) + "]",
                FileeJsonContext.Default.ListPreset)!.Single();
        if (scenario == "PdfBefore18")
        {
            var ids = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(SlotFixtures,
                $"{(import ? "Import" : "Load")}-PdfBefore18-preset-ids.json")), FileeJsonContext.Default.ListString)!;
            var byId = expectedPresets.ToDictionary(p => p.Id);
            expectedPresets = ids.Select(id => byId[id]).ToList();
        }
        Assert.Equal(JsonSerializer.Serialize(expectedPresets, FileeJsonContext.Default.ListPreset),
            JsonSerializer.Serialize(store.Presets, FileeJsonContext.Default.ListPreset));
    }

    private static PresetBundle SlotInput(string root, string scenario)
    {
        var profiles = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(root, "profiles.json")), FileeJsonContext.Default.ListToolbarProfile)!;
        var presets = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(root, "presets.json")), FileeJsonContext.Default.ListPreset)!;
        if (scenario == "Edited")
        {
            var images = profiles.Single(p => p.Id == "images");
            images.PresetIds.Reverse();
            images.PresetIds.RemoveAt(2);
            profiles.Insert(0, new ToolbarProfile
            {
                Id = "mine",
                Name = "My images",
                Extensions = ["xyz"],
                PresetIds = ["to-png"],
                BackPresetIds = ["to-webp"],
            });
            var png = presets.Single(p => p.Id == "to-png");
            png.Name = "My PNG";
            png.Image.Quality = 42;
            png.Image.Resize = ResizeMode.Percent;
            png.Image.ResizePercent = 75;
        }
        if (scenario == "PdfBefore18")
        {
            profiles.Single(p => p.Id == "pdf").PresetIds = [.. BuiltInData.PdfDonutBefore18];
            foreach (var profile in profiles)
                profile.BackPresetIds = null;
            presets.RemoveAll(p => p.Id == "pdf-slides");
            profiles.RemoveAll(p => p.Id == "geo");
        }
        if (scenario == "DefaultWithoutBack")
            foreach (var profile in profiles.Where(p => p.Id is "images" or "pdf"))
                profile.BackPresetIds = null;
        if (scenario == "UserWithoutBack")
            profiles.Insert(0, new ToolbarProfile { Id = "mine", Name = "Mine", Extensions = ["xyz"], PresetIds = ["to-png"] });
        if (scenario == "Schema29")
            foreach (var profile in profiles)
                profile.BackPresetIds = null;
        return new PresetBundle { Profiles = profiles, Presets = presets };
    }
}
