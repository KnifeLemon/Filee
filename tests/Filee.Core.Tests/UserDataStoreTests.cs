using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class UserDataStoreTests
{
    [Fact]
    public void Round_trips_settings_presets_and_profiles()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Settings.Language = "ko";
        store.Settings.Theme.Accent = "#1D9E75";
        store.Settings.Triggers[0].Modifiers = ModifierKeys.Alt | ModifierKeys.Shift;
        store.Presets.Add(new Preset { Id = "mine", Name = "Mine", TargetFormat = "webp", Image = { Quality = 42 } });
        store.Profiles[0].PresetIds.Insert(0, "mine");
        store.SaveSettings();
        store.SaveLibrary();

        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();

        Assert.Equal("ko", reloaded.Settings.Language);
        Assert.Equal("#1D9E75", reloaded.Settings.Theme.Accent);
        Assert.Equal(ModifierKeys.Alt | ModifierKeys.Shift, reloaded.Settings.Triggers[0].Modifiers);
        Assert.Equal(42, reloaded.FindPreset("mine")!.Image.Quality);
        Assert.Equal("mine", reloaded.Profiles[0].PresetIds[0]);
    }

    [Fact]
    public void Version_1_settings_are_upgraded_to_the_current_engine_list()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"),
            """{ "schemaVersion": 1, "enginePriority": ["magick", "word", "hancom", "libreoffice"] }""");

        var store = new UserDataStore(dir.Path);
        store.Load();

        Assert.Equal(["magick", "markdown", "spreadsheet", "ooxml", "hwpx-writer", "docx-writer", "vector", "icns", "font", "cad", "archive", "ebook", "ffmpeg", "pandoc", "ghostscript", "calibre", "libreoffice"], store.Settings.EnginePriority);
        Assert.Equal(AppSettings.CurrentSchemaVersion, store.Settings.SchemaVersion);
    }

    [Fact]
    public void Version_3_libraries_get_the_text_profile_once()
    {
        using var dir = new TempDir();
        var old = new UserDataStore(dir.Path);
        old.Load();
        old.Profiles.RemoveAll(p => p.Id == "text"); // as written by 1.0.0
        old.Settings.SchemaVersion = 3;
        old.Settings.EnginePriority.Remove("markdown");
        old.SaveSettings();
        old.SaveLibrary();

        var store = new UserDataStore(dir.Path);
        store.Load();

        var ids = store.Profiles.Select(p => p.Id).ToList();
        Assert.Equal(ids.IndexOf("mixed") - 1, ids.IndexOf("text")); // right before the fallback
        Assert.Contains("md", store.Profiles.Single(p => p.Id == "text").Extensions);
        Assert.Equal(store.Settings.EnginePriority.IndexOf("hwpx-writer") - 1, store.Settings.EnginePriority.IndexOf("markdown"));

        // Saved right away: the next start neither migrates again nor brings back a profile the user deleted.
        store.Profiles.RemoveAll(p => p.Id == "text");
        store.SaveLibrary();
        var next = new UserDataStore(dir.Path);
        next.Load();
        Assert.DoesNotContain(next.Profiles, p => p.Id == "text");
        Assert.Equal(AppSettings.CurrentSchemaVersion, next.Settings.SchemaVersion);
    }

    [Fact]
    public void Version_4_libraries_get_spreadsheet_and_presentation_profiles()
    {
        using var dir = new TempDir();
        var old = new UserDataStore(dir.Path);
        old.Load();
        // As written by 1.0.1: one documents donut for Word, Excel and PowerPoint files, Word-only PDF targets.
        old.Profiles.RemoveAll(p => p.Id is "spreadsheets" or "presentations");
        old.Profiles.Single(p => p.Id == "office").Extensions.AddRange(["xlsx", "xls", "ods", "csv", "pptx", "ppt", "odp"]);
        old.Profiles.Single(p => p.Id == "pdf").PresetIds.AddRange(["to-docx", "to-hwpx"]);
        old.Presets.RemoveAll(p => p.Id is "to-xlsx" or "to-csv");
        old.Settings.SchemaVersion = 4;
        old.Settings.EnginePriority = ["magick", "word", "markdown", "hwpx-writer", "pandoc", "libreoffice"];
        old.SaveSettings();
        old.SaveLibrary();

        var store = new UserDataStore(dir.Path);
        store.Load();

        Assert.Equal(["magick", "markdown", "spreadsheet", "ooxml", "hwpx-writer", "docx-writer", "vector", "icns", "font", "cad", "archive", "ebook", "ffmpeg", "pandoc", "ghostscript", "calibre", "libreoffice"], store.Settings.EnginePriority);
        var ids = store.Profiles.Select(p => p.Id).ToList();
        Assert.Equal(ids.IndexOf("office") + 1, ids.IndexOf("spreadsheets"));
        Assert.Equal(ids.IndexOf("office") + 2, ids.IndexOf("presentations"));
        Assert.DoesNotContain("xlsx", store.Profiles.Single(p => p.Id == "office").Extensions);
        Assert.Contains("to-csv", store.Profiles.Single(p => p.Id == "spreadsheets").PresetIds);
        Assert.NotNull(store.FindPreset("to-xlsx"));
        Assert.DoesNotContain("to-hwpx", store.Profiles.Single(p => p.Id == "pdf").PresetIds); // Word-only target removed by v5
    }

    [Fact]
    public void Version_5_libraries_get_the_format_catalog()
    {
        // As written by 1.0.x with schema 5: no catalog presets, donuts or extensions.
        var presets = BuiltInData.CreatePresets().Where(p => BuiltInData.CatalogPresets().All(c => c.Id != p.Id)).ToList();
        var catalogIds = BuiltInData.CatalogProfiles().Select(p => p.Id).ToHashSet();
        var profiles = BuiltInData.CreateProfiles().Where(p => !catalogIds.Contains(p.Id)).ToList();
        profiles.Single(p => p.Id == "images").Extensions = ["png", "jpg"];
        profiles.Single(p => p.Id == "pdf").PresetIds.Remove("to-docx");
        profiles.Single(p => p.Id == "mixed").PresetIds.Remove("zip-all");
        // A donut of the user already handles MP3.
        profiles.Insert(0, new ToolbarProfile { Id = "mine", Name = "Mine", Extensions = ["mp3"] });

        Assert.True(SettingsMigrations.ApplyToLibrary(5, presets, profiles));

        var ids = profiles.Select(p => p.Id).ToList();
        Assert.Equal(ids.IndexOf("text") + 1, ids.IndexOf("ebooks"));
        Assert.Equal(ids.IndexOf("mixed") - 1, ids.IndexOf("fonts"));
        Assert.Contains("cr2", profiles.Single(p => p.Id == "images").Extensions);
        Assert.DoesNotContain("mp3", profiles.Single(p => p.Id == "audio").Extensions);
        Assert.Contains("flac", profiles.Single(p => p.Id == "audio").Extensions);
        Assert.Contains(presets, p => p.Id == "to-mp4");
        Assert.Equal(["to-png", "to-jpg", "to-docx"], profiles.Single(p => p.Id == "pdf").PresetIds.Take(3));
        Assert.Contains("zip-all", profiles.Single(p => p.Id == "mixed").PresetIds);
        // Every extension belongs to one donut only.
        var all = profiles.SelectMany(p => p.Extensions).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Spreadsheet_profile_is_not_added_when_a_user_profile_takes_spreadsheets()
    {
        var profiles = BuiltInData.CreateProfiles().Where(p => p.Id is not ("spreadsheets" or "presentations")).ToList();
        profiles.Insert(0, new ToolbarProfile { Id = "mine", Name = "Mine", Extensions = ["xlsx"] });

        SettingsMigrations.ApplyToLibrary(4, BuiltInData.CreatePresets(), profiles);

        Assert.DoesNotContain(profiles, p => p.Id == "spreadsheets");
        Assert.Contains(profiles, p => p.Id == "presentations");
        Assert.Equal(["xlsx"], profiles[0].Extensions);
    }

    [Fact]
    public void Text_profile_is_not_added_when_a_profile_already_takes_markdown()
    {
        var profiles = BuiltInData.CreateProfiles().Where(p => p.Id != "text").ToList();
        profiles[0].Extensions.Add("md");

        SettingsMigrations.ApplyToLibrary(3, BuiltInData.CreatePresets(), profiles);
        Assert.DoesNotContain(profiles, p => p.Id == "text");
    }

    [Fact]
    public void Enums_are_stored_as_strings()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.SaveSettings();

        var json = File.ReadAllText(Path.Combine(dir.Path, "settings.json"));
        Assert.Contains("\"Drag\"", json);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults_and_is_kept()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "presets.json"), "{ not json");

        var store = new UserDataStore(dir.Path);
        store.Load();

        Assert.NotEmpty(store.Presets);
        Assert.True(File.Exists(Path.Combine(dir.Path, "presets.json.broken")));
    }

    [Fact]
    public void Dangling_preset_references_are_removed()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Profiles[0].PresetIds.Add("does-not-exist");
        store.SaveLibrary();

        Assert.DoesNotContain("does-not-exist", store.Profiles[0].PresetIds);
    }

    [Fact]
    public void Export_then_import_restores_presets()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Presets.Add(new Preset { Id = "shared", Name = "Shared", TargetFormat = "png" });
        var bundle = Path.Combine(dir.Path, "bundle.json");
        store.Export(bundle);

        using var other = new TempDir();
        var target = new UserDataStore(other.Path);
        target.Load();
        target.Import(bundle);

        Assert.NotNull(target.FindPreset("shared"));
        Assert.Single(target.Profiles, p => p.IsFallback);
    }
}
