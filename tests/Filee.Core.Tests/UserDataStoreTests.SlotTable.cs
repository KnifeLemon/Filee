using System.Text.Json;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public partial class UserDataStoreTests
{
    [Fact]
    public void A_library_with_the_schema_58_defaults_loads_unchanged() => AssertKeptAsBefore("Defaults", import: false);

    [Fact]
    public void A_bundle_with_the_schema_58_defaults_imports_unchanged() => AssertKeptAsBefore("Defaults", import: true);

    [Fact]
    public void A_library_the_user_edited_loads_unchanged() => AssertKeptAsBefore("Edited", import: false);

    [Fact]
    public void A_bundle_the_user_edited_imports_unchanged() => AssertKeptAsBefore("Edited", import: true);

    [Fact]
    public void Version_17_libraries_with_the_old_fronts_get_the_schema_58_defaults() => AssertKeptAsBefore("PdfBefore18", import: false);

    [Fact]
    public void A_bundle_with_the_pdf_front_before_18_keeps_it_and_gets_the_schema_58_back_faces() => AssertKeptAsBefore("PdfBefore18", import: true);

    [Fact]
    public void A_schema_58_donut_without_a_back_face_gets_its_schema_58_one_on_loading() => AssertKeptAsBefore("DefaultWithoutBack", import: false);

    [Fact]
    public void A_schema_58_donut_without_a_back_face_gets_its_schema_58_one_on_import() => AssertKeptAsBefore("DefaultWithoutBack", import: true);

    [Fact]
    public void A_donut_of_the_user_without_a_back_face_gets_an_empty_one_on_loading() => AssertKeptAsBefore("UserWithoutBack", import: false);

    [Fact]
    public void A_donut_of_the_user_without_a_back_face_gets_an_empty_one_on_import() => AssertKeptAsBefore("UserWithoutBack", import: true);

    [Fact]
    public void Version_29_libraries_with_the_schema_58_fronts_get_the_schema_58_back_faces() => AssertKeptAsBefore("Schema29", import: false);

    // The expected library is built from ProfilesSchema58() and CreatePresets() plus the change of the case, never by
    // Load, Import or the migrations.
    private static void AssertKeptAsBefore(string scenario, bool import)
    {
        using var dir = new TempDir();
        var input = Schema58LibraryFor(scenario);
        var store = new UserDataStore(dir.Path);
        if (import)
        {
            store.Load();
            // The receiving library is part of the input, not a new installation's changing defaults.
            var library = Schema58Library();
            store.Profiles.Clear();
            store.Profiles.AddRange(library.Profiles);
            store.Presets.Clear();
            store.Presets.AddRange(library.Presets);
            var bundlePath = Path.Combine(dir.Path, "bundle.json");
            File.WriteAllText(bundlePath, JsonSerializer.Serialize(input, FileeJsonContext.Default.PresetBundle));
            store.Import(bundlePath);
        }
        else
        {
            File.WriteAllText(Path.Combine(dir.Path, "settings.json"), JsonSerializer.Serialize(
                new AppSettings { SchemaVersion = scenario switch { "PdfBefore18" => 17, "Schema29" => 29, _ => 58 } },
                FileeJsonContext.Default.AppSettings));
            File.WriteAllText(Path.Combine(dir.Path, "profiles.json"), JsonSerializer.Serialize(input.Profiles, FileeJsonContext.Default.ListToolbarProfile));
            File.WriteAllText(Path.Combine(dir.Path, "presets.json"), JsonSerializer.Serialize(input.Presets, FileeJsonContext.Default.ListPreset));
            store.Load();
        }
        var expected = scenario switch
        {
            "PdfBefore18" when import => PdfBefore18Imported(),
            "PdfBefore18" => PdfBefore18Loaded(),
            "Edited" => Schema58LibraryFor("Edited"),
            "UserWithoutBack" => UserWithoutBackKept(),
            _ => Schema58Library(),
        };
        Assert.Equal(JsonSerializer.Serialize(expected.Profiles, FileeJsonContext.Default.ListToolbarProfile),
            JsonSerializer.Serialize(store.Profiles, FileeJsonContext.Default.ListToolbarProfile));
        Assert.Equal(JsonSerializer.Serialize(expected.Presets, FileeJsonContext.Default.ListPreset),
            JsonSerializer.Serialize(store.Presets, FileeJsonContext.Default.ListPreset));
    }

    private static PresetBundle Schema58Library() =>
        new() { Profiles = BuiltInData.ProfilesSchema58(), Presets = BuiltInData.CreatePresets() };

    private static PresetBundle Schema58LibraryFor(string scenario)
    {
        var library = Schema58Library();
        var (profiles, presets) = (library.Profiles, library.Presets);
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
        return library;
    }

    // Schema 18 swaps the untouched PDF front for PdfDonut and puts "PPTX (slides)" right after "Split pages"; the GIS
    // migration adds the map donut back, and the back faces are the schema 58 ones.
    private static PresetBundle PdfBefore18Loaded()
    {
        var library = Schema58Library();
        var slides = library.Presets.Single(p => p.Id == "pdf-slides");
        library.Presets.Remove(slides);
        library.Presets.Insert(library.Presets.FindIndex(p => p.Id == "pdf-split") + 1, slides);
        return library;
    }

    // Import runs no migration: the bundle keeps its PDF front and gets the schema 58 back faces. The donut and preset
    // of the receiving library that the bundle lacks come first.
    private static PresetBundle PdfBefore18Imported()
    {
        var library = Schema58Library();
        var backs = library.Profiles.ToDictionary(p => p.Id, p => p.BackPresetIds);
        var bundle = Schema58LibraryFor("PdfBefore18").Profiles;
        foreach (var profile in bundle)
            profile.BackPresetIds = backs[profile.Id];
        var slides = library.Presets.Single(p => p.Id == "pdf-slides");
        library.Presets.Remove(slides);
        library.Presets.Insert(0, slides);
        return new PresetBundle { Profiles = [library.Profiles.Single(p => p.Id == "geo"), .. bundle], Presets = library.Presets };
    }

    private static PresetBundle UserWithoutBackKept()
    {
        var library = Schema58LibraryFor("UserWithoutBack");
        library.Profiles.Single(p => p.Id == "mine").BackPresetIds = [];
        return library;
    }
}
