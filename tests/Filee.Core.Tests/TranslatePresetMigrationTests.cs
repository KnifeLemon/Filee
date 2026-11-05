// Translate (#1160): settings schema 69 brings the preset to libraries saved before it existed, after the built-in preset
// before it as in a new library, takes no donut slot, leaves a library that has one alone, is saved at once, and doesn't
// come back once deleted.

using System.Text.Json;
using System.Text.Json.Nodes;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class TranslatePresetMigrationTests
{
    private const string Id = "translate";

    private static UserDataStore Saved(TempDir dir)
    {
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.SaveSettings();
        store.SaveLibrary();
        return store;
    }

    /// <summary>
    /// A library as an older Filee saved it: no Translate preset or slot, and Captions off the video and audio donuts so
    /// the schema 57 step has a slot to add. With <paramref name="keepTranslate"/> the preset stays in the list, so the
    /// new step doesn't run and only the older steps apply.
    /// </summary>
    private static void OldLibrary(TempDir dir, int schema, bool keepTranslate = false)
    {
        Saved(dir);
        var presetsPath = Path.Combine(dir.Path, "presets.json");
        var presets = JsonNode.Parse(File.ReadAllText(presetsPath))!.AsArray();
        if (!keepTranslate)
            presets.Remove(presets.Single(n => n!["id"]!.GetValue<string>() == Id));
        File.WriteAllText(presetsPath, presets.ToJsonString());

        var profilesPath = Path.Combine(dir.Path, "profiles.json");
        var profiles = JsonNode.Parse(File.ReadAllText(profilesPath))!.AsArray();
        foreach (var profile in profiles.Select(n => n!.AsObject()))
        {
            var drop = profile["id"]!.GetValue<string>() is "video" or "audio" ? new[] { Id, "caption-video" } : [Id];
            foreach (var face in new[] { "presetIds", "backPresetIds" })
            {
                if (profile[face] is not JsonArray ids)
                    continue;
                foreach (var slot in ids.Where(n => drop.Contains(n!.GetValue<string>())).ToList())
                    ids.Remove(slot);
            }
        }
        File.WriteAllText(profilesPath, profiles.ToJsonString());

        var settingsPath = Path.Combine(dir.Path, "settings.json");
        var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        settings["schemaVersion"] = schema;
        File.WriteAllText(settingsPath, settings.ToJsonString());
    }

    private static UserDataStore Loaded(TempDir dir)
    {
        var store = new UserDataStore(dir.Path);
        store.Load();
        return store;
    }

    private static string Json(IEnumerable<Preset> presets) =>
        JsonSerializer.Serialize(presets.ToList(), FileeJsonContext.Default.ListPreset);

    private static string Json(List<ToolbarProfile> profiles) =>
        JsonSerializer.Serialize(profiles, FileeJsonContext.Default.ListToolbarProfile);

    private static bool HasSlot(IEnumerable<ToolbarProfile> profiles, string id) =>
        profiles.Any(p => p.PresetIds.Contains(id) || p.BackPresetIds?.Contains(id) == true);

    /// <summary>Where a new library has Translate: right after the last built-in preset before it that the list has.</summary>
    private static int PlaceIn(List<Preset> withoutTranslate)
    {
        var defaults = BuiltInData.CreatePresets();
        var before = defaults.Take(defaults.FindIndex(p => p.Id == Id)).Select(p => p.Id).ToList();
        var last = withoutTranslate.Select((p, i) => (p.Id, i)).Where(x => before.Contains(x.Id))
            .OrderBy(x => before.IndexOf(x.Id)).Select(x => x.i).LastOrDefault(-1);
        return last + 1;
    }

    [Theory]
    [InlineData(54)] // 2.2.0
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(66)]
    [InlineData(67)]
    [InlineData(68)]
    public void Old_library_gets_Translate_in_place_and_keeps_its_slots(int schema)
    {
        using var dir = new TempDir();
        using var expectedDir = new TempDir();
        OldLibrary(dir, schema);
        OldLibrary(expectedDir, schema, keepTranslate: true);

        var store = Loaded(dir);
        var expected = Loaded(expectedDir);

        var translate = store.FindPreset(Id);
        Assert.NotNull(translate);
        Assert.Equal("preset.translate", translate.NameKey);
        Assert.Equal(BuiltInData.SameAsSource, translate.TargetFormat);
        Assert.True(translate.Translate.Enabled);
        Assert.Equal("en", translate.Translate.TargetLanguage);
        Assert.Equal("{name}_translated", translate.Output.FileNamePattern);

        var others = store.Presets.Where(p => p.Id != Id).ToList();
        Assert.Equal(PlaceIn(others), store.Presets.IndexOf(translate));
        Assert.Equal(Json(expected.Presets.Where(p => p.Id != Id)), Json(others));

        Assert.Equal(Json(expected.Profiles), Json(store.Profiles));
        Assert.False(HasSlot(store.Profiles, Id));
        if (schema < 57)
            Assert.Contains("caption-video", store.Profiles.Single(p => p.Id == "video").PresetIds);
        Assert.Equal(AppSettings.CurrentSchemaVersion, store.Settings.SchemaVersion);
    }

    [Fact]
    public void Translate_goes_after_the_built_in_preset_before_it()
    {
        var defaults = BuiltInData.CreatePresets();
        List<Preset> Only(params string[] ids) => ids.Select(id => defaults.Single(p => p.Id == id)).ToList();
        // Later steps add their own presets; what is checked here is where Translate lands.
        static IEnumerable<string> Ids(List<Preset> presets) => presets.Select(p => p.Id).Where(id => id != BuiltInData.MetadataId);
        List<ToolbarProfile> Profiles()
        {
            var profiles = BuiltInData.CreateProfiles();
            foreach (var profile in profiles)
            {
                profile.PresetIds.Remove(Id);
                profile.BackPresetIds?.Remove(Id);
            }
            return profiles;
        }

        // The default order decides, not the saved one: trim-transparent comes after to-png in a new library.
        var presets = Only("trim-transparent", "to-png");
        var profiles = Profiles();
        var before = Json(profiles);
        Assert.True(SettingsMigrations.ApplyToLibrary(68, presets, profiles));
        Assert.Equal(["trim-transparent", Id, "to-png"], Ids(presets));
        Assert.Equal(before, Json(profiles));

        // No built-in preset before it: at the start.
        presets = Only("tool-crop");
        profiles = Profiles();
        before = Json(profiles);
        Assert.True(SettingsMigrations.ApplyToLibrary(68, presets, profiles));
        Assert.Equal([Id, "tool-crop"], Ids(presets));
        Assert.Equal(before, Json(profiles));
    }

    [Fact]
    public void An_existing_Translate_preset_is_left_alone()
    {
        var presets = BuiltInData.CreatePresets();
        var translate = presets.Single(p => p.Id == Id);
        translate.Name = "To Korean";
        translate.Translate.TargetLanguage = "ko";
        var profiles = BuiltInData.CreateProfiles();

        SettingsMigrations.ApplyToLibrary(54, presets, profiles);

        Assert.Same(translate, presets.Single(p => p.Id == Id));
        Assert.Equal("To Korean", translate.Name);
        Assert.Equal("ko", translate.Translate.TargetLanguage);
    }

    [Theory]
    [InlineData(54)]
    [InlineData(68)] // No older step changes anything, so only the new step's answer gets the library saved.
    public void Upgraded_library_is_saved_and_reloads_unchanged(int schema)
    {
        using var dir = new TempDir();
        OldLibrary(dir, schema);
        var store = Loaded(dir);
        var presets = Json(store.Presets);
        var profiles = Json(store.Profiles);

        var again = Loaded(dir);
        Assert.Equal(presets, Json(again.Presets));
        Assert.Equal(profiles, Json(again.Profiles));
        Assert.NotNull(again.FindPreset(Id));

        var presetsFile = File.ReadAllText(Path.Combine(dir.Path, "presets.json"));
        var profilesFile = File.ReadAllText(Path.Combine(dir.Path, "profiles.json"));
        again.SaveLibrary();
        Assert.Equal(presetsFile, File.ReadAllText(Path.Combine(dir.Path, "presets.json")));
        Assert.Equal(profilesFile, File.ReadAllText(Path.Combine(dir.Path, "profiles.json")));
        Assert.False(SettingsMigrations.ApplyToLibrary(AppSettings.CurrentSchemaVersion, again.Presets, again.Profiles));
    }

    [Fact]
    public void Translate_deleted_after_schema_69_stays_deleted()
    {
        using var dir = new TempDir();
        var store = Saved(dir);
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(dir.Path, "settings.json")))!;
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings["schemaVersion"]!.GetValue<int>());

        store.Presets.RemoveAll(p => p.Id == Id);
        store.SaveLibrary();

        Assert.Null(Loaded(dir).FindPreset(Id));
    }
}
