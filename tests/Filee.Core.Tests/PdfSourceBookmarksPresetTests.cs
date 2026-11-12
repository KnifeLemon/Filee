using System.Text.Json;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class PdfSourceBookmarksPresetTests
{
    private const string OldPresets = """
        [
          {
            "id": "legacy-merge",
            "name": "Legacy merge",
            "targetFormat": "pdf",
            "pdf": {
              "mergeIntoSingle": true,
              "pageSize": "A4",
              "marginMm": 8,
              "renderDpi": 240,
              "pageRange": "1-2",
              "removeMetadata": true,
              "ocr": true,
              "ocrLanguage": "kor+eng"
            },
            "output": {
              "location": "Subfolder",
              "subfolderName": "exports",
              "fileNamePattern": "{name}_joined",
              "conflict": "Skip",
              "keepDates": true
            }
          }
        ]
        """;

    [Fact]
    public void Source_bookmarks_default_off_in_old_presets_and_survive_round_trip()
    {
        using var dir = new TempDir();
        var fresh = new UserDataStore(dir.Path);
        fresh.Load();
        Assert.False(fresh.Presets.Single(preset => preset.Id == "merge-pdf").Pdf.SourceBookmarks);
        Assert.False(new PdfOptions().SourceBookmarks);
        File.WriteAllText(Path.Combine(dir.Path, "presets.json"), OldPresets);
        var store = new UserDataStore(dir.Path);
        store.Load();
        AssertLegacy(Assert.Single(store.Presets));
        var schema = store.Settings.SchemaVersion;

        store.SaveLibrary();
        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();

        AssertLegacy(Assert.Single(reloaded.Presets));
        Assert.Equal(schema, reloaded.Settings.SchemaVersion);
    }

    [Fact]
    public void Source_bookmarks_save_reload_and_clone_independently()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "presets.json"), OldPresets);
        var store = new UserDataStore(dir.Path);
        store.Load();
        var preset = Assert.Single(store.Presets);
        preset.Pdf.SourceBookmarks = true;

        store.SaveLibrary();
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.Path, "presets.json")));
        Assert.True(json.RootElement[0].GetProperty("pdf").GetProperty("sourceBookmarks").GetBoolean());
        Assert.False(json.RootElement[0].TryGetProperty("sourceBookmarks", out _));
        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();
        var saved = Assert.Single(reloaded.Presets);
        Assert.True(saved.Pdf.SourceBookmarks);
        var clone = saved.Clone();
        Assert.True(clone.Pdf.SourceBookmarks);
        Assert.NotSame(saved.Pdf, clone.Pdf);
        clone.Pdf.SourceBookmarks = false;
        Assert.True(saved.Pdf.SourceBookmarks);
        Assert.True(preset.Pdf.SourceBookmarks);
        AssertLegacy(clone);
        store.SaveLibrary();
        reloaded.Load();
        Assert.True(Assert.Single(reloaded.Presets).Pdf.SourceBookmarks);
    }

    [Fact]
    public void Source_bookmarks_dropped_by_an_old_reader_default_off_without_other_data_loss()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "presets.json");
        File.WriteAllText(path, OldPresets);
        var store = new UserDataStore(dir.Path);
        store.Load();
        Assert.Single(store.Presets).Pdf.SourceBookmarks = true;
        store.SaveLibrary();

        // The previous reader knows every field in the fixed old fixture, but not SourceBookmarks.
        var previous = JsonSerializer.Deserialize<List<PreviousPreset>>(File.ReadAllText(path), JsonSerializerOptions.Web)!;
        File.WriteAllText(path, JsonSerializer.Serialize(previous, JsonSerializerOptions.Web));
        using var oldJson = JsonDocument.Parse(File.ReadAllText(path));
        Assert.False(oldJson.RootElement[0].GetProperty("pdf").TryGetProperty("sourceBookmarks", out _));
        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();

        AssertLegacy(Assert.Single(reloaded.Presets));
        reloaded.SaveLibrary();
        store.Load();
        AssertLegacy(Assert.Single(store.Presets));
    }

    private sealed class PreviousPreset
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string TargetFormat { get; set; } = "";
        public PreviousPdf Pdf { get; set; } = new();
        public PreviousOutput Output { get; set; } = new();
    }

    private sealed class PreviousPdf
    {
        public bool MergeIntoSingle { get; set; }
        public string PageSize { get; set; } = "";
        public double MarginMm { get; set; }
        public int RenderDpi { get; set; }
        public string PageRange { get; set; } = "";
        public bool RemoveMetadata { get; set; }
        public bool Ocr { get; set; }
        public string OcrLanguage { get; set; } = "";
    }

    private sealed class PreviousOutput
    {
        public string Location { get; set; } = "";
        public string SubfolderName { get; set; } = "";
        public string FileNamePattern { get; set; } = "";
        public string Conflict { get; set; } = "";
        public bool KeepDates { get; set; }
    }

    private static void AssertLegacy(Preset preset)
    {
        Assert.Equal("legacy-merge", preset.Id);
        Assert.Equal("Legacy merge", preset.Name);
        Assert.Equal("pdf", preset.TargetFormat);
        Assert.False(preset.Pdf.SourceBookmarks);
        Assert.True(preset.Pdf.MergeIntoSingle);
        Assert.Equal(PdfPageSize.A4, preset.Pdf.PageSize);
        Assert.Equal(8, preset.Pdf.MarginMm);
        Assert.Equal(240, preset.Pdf.RenderDpi);
        Assert.Equal("1-2", preset.Pdf.PageRange);
        Assert.True(preset.Pdf.RemoveMetadata);
        Assert.True(preset.Pdf.Ocr);
        Assert.Equal("kor+eng", preset.Pdf.OcrLanguage);
        Assert.Equal(OutputLocation.Subfolder, preset.Output.Location);
        Assert.Equal("exports", preset.Output.SubfolderName);
        Assert.Equal("{name}_joined", preset.Output.FileNamePattern);
        Assert.Equal(ConflictPolicy.Skip, preset.Output.Conflict);
        Assert.True(preset.Output.KeepDates);
    }
}
