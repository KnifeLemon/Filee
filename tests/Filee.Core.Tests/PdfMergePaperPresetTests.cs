using System.Text.Json;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class PdfMergePaperPresetTests
{
    private const string OldPresets = """
        [
          {
            "id": "legacy-merge",
            "name": "Legacy merge",
            "targetFormat": "pdf",
            "pdf": {
              "mergeIntoSingle": true,
              "sourceBookmarks": true,
              "pageSize": "A4",
              "marginMm": 8,
              "pagesPerSheet": 2,
              "sheetPaper": "Letter",
              "sheetOrientation": "Landscape",
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
    public void Merge_paper_defaults_off_in_old_and_fresh_libraries_without_a_schema_change()
    {
        using var dir = new TempDir();
        var fresh = new UserDataStore(dir.Path);
        fresh.Load();
        AssertDefaults(new PdfOptions());
        AssertDefaults(fresh.Presets.Single(preset => preset.Id == "merge-pdf").Pdf);
        Assert.Equal(AppSettings.CurrentSchemaVersion, fresh.Settings.SchemaVersion);
        File.WriteAllText(Path.Combine(dir.Path, "presets.json"), OldPresets);
        var store = new UserDataStore(dir.Path);
        store.Load();
        AssertLegacy(Assert.Single(store.Presets));
        AssertDefaults(store.Presets[0].Pdf);

        store.SaveLibrary();
        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();

        AssertLegacy(Assert.Single(reloaded.Presets));
        AssertDefaults(reloaded.Presets[0].Pdf);
        Assert.Equal(AppSettings.CurrentSchemaVersion, reloaded.Settings.SchemaVersion);
    }

    [Theory]
    [InlineData(true, PdfSheetPaper.A4, PdfSheetOrientation.Auto)]
    [InlineData(true, PdfSheetPaper.Letter, PdfSheetOrientation.Portrait)]
    [InlineData(false, PdfSheetPaper.A3, PdfSheetOrientation.Landscape)]
    [InlineData(true, PdfSheetPaper.SameAsFirstPage, PdfSheetOrientation.Landscape)]
    public void Merge_paper_saves_reloads_and_clones_independently(bool normalize, PdfSheetPaper paper,
        PdfSheetOrientation orientation)
    {
        using var dir = new TempDir();
        var path = dir.File("presets.json", OldPresets);
        var store = new UserDataStore(dir.Path);
        store.Load();
        var preset = Assert.Single(store.Presets);
        preset.Pdf.NormalizeMergePaper = normalize;
        preset.Pdf.MergePaper = paper;
        preset.Pdf.MergeOrientation = orientation;

        store.SaveLibrary();
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var pdf = json.RootElement[0].GetProperty("pdf");
        Assert.Equal(normalize ? JsonValueKind.True : JsonValueKind.False, pdf.GetProperty("normalizeMergePaper").ValueKind);
        Assert.Equal(normalize, pdf.GetProperty("normalizeMergePaper").GetBoolean());
        Assert.Equal(JsonValueKind.String, pdf.GetProperty("mergePaper").ValueKind);
        Assert.Equal(paper.ToString(), pdf.GetProperty("mergePaper").GetString());
        Assert.Equal(JsonValueKind.String, pdf.GetProperty("mergeOrientation").ValueKind);
        Assert.Equal(orientation.ToString(), pdf.GetProperty("mergeOrientation").GetString());
        Assert.False(json.RootElement[0].TryGetProperty("normalizeMergePaper", out _));
        Assert.False(json.RootElement[0].TryGetProperty("mergePaper", out _));
        Assert.False(json.RootElement[0].TryGetProperty("mergeOrientation", out _));
        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();
        var saved = Assert.Single(reloaded.Presets);
        AssertLegacy(saved);
        Assert.Equal(normalize, saved.Pdf.NormalizeMergePaper);
        Assert.Equal(paper, saved.Pdf.MergePaper);
        Assert.Equal(orientation, saved.Pdf.MergeOrientation);

        var clone = saved.Clone();
        Assert.NotSame(saved.Pdf, clone.Pdf);
        Assert.Equal(normalize, clone.Pdf.NormalizeMergePaper);
        Assert.Equal(paper, clone.Pdf.MergePaper);
        Assert.Equal(orientation, clone.Pdf.MergeOrientation);
        clone.Pdf.NormalizeMergePaper = !normalize;
        clone.Pdf.MergePaper = paper == PdfSheetPaper.A4 ? PdfSheetPaper.A3 : PdfSheetPaper.A4;
        clone.Pdf.MergeOrientation = orientation == PdfSheetOrientation.Auto ? PdfSheetOrientation.Portrait : PdfSheetOrientation.Auto;
        Assert.Equal(normalize, saved.Pdf.NormalizeMergePaper);
        Assert.Equal(paper, saved.Pdf.MergePaper);
        Assert.Equal(orientation, saved.Pdf.MergeOrientation);
        AssertLegacy(clone);
        reloaded.SaveLibrary();
        store.Load();
        var original = Assert.Single(store.Presets);
        Assert.Equal(normalize, original.Pdf.NormalizeMergePaper);
        Assert.Equal(paper, original.Pdf.MergePaper);
        Assert.Equal(orientation, original.Pdf.MergeOrientation);
    }

    [Theory]
    [InlineData("\"FuturePaper\"", "\"FutureOrientation\"")]
    [InlineData("999", "-1")]
    public void Unknown_merge_paper_enums_keep_the_enabled_option_and_the_rest_of_the_preset(string paper, string orientation)
    {
        using var dir = new TempDir();
        dir.File("presets.json", OldPresets.Replace("\"sourceBookmarks\": true,",
            $"\"sourceBookmarks\": true, \"normalizeMergePaper\": true, \"mergePaper\": {paper}, \"mergeOrientation\": {orientation},"));
        var store = new UserDataStore(dir.Path);
        store.Load();

        var preset = Assert.Single(store.Presets);
        AssertLegacy(preset);
        Assert.True(preset.Pdf.NormalizeMergePaper);
        Assert.Equal(PdfSheetPaper.A4, preset.Pdf.MergePaper);
        Assert.Equal(PdfSheetOrientation.Auto, preset.Pdf.MergeOrientation);
        store.SaveLibrary();
        store.Load();
        AssertLegacy(Assert.Single(store.Presets));
        Assert.True(store.Presets[0].Pdf.NormalizeMergePaper);
        Assert.Equal(PdfSheetPaper.A4, store.Presets[0].Pdf.MergePaper);
        Assert.Equal(PdfSheetOrientation.Auto, store.Presets[0].Pdf.MergeOrientation);
    }

    [Fact]
    public void Merge_paper_dropped_by_an_old_reader_defaults_off_without_other_data_loss()
    {
        using var dir = new TempDir();
        var path = dir.File("presets.json", OldPresets);
        var store = new UserDataStore(dir.Path);
        store.Load();
        var preset = Assert.Single(store.Presets);
        preset.Pdf.NormalizeMergePaper = true;
        preset.Pdf.MergePaper = PdfSheetPaper.A3;
        preset.Pdf.MergeOrientation = PdfSheetOrientation.Landscape;
        store.SaveLibrary();

        // The previous DTO knows all fields in the old fixture, but none of the new merge paper fields.
        var previous = JsonSerializer.Deserialize<List<PreviousPreset>>(File.ReadAllText(path), JsonSerializerOptions.Web)!;
        File.WriteAllText(path, JsonSerializer.Serialize(previous, JsonSerializerOptions.Web));
        using var oldJson = JsonDocument.Parse(File.ReadAllText(path));
        var pdf = oldJson.RootElement[0].GetProperty("pdf");
        Assert.False(pdf.TryGetProperty("normalizeMergePaper", out _));
        Assert.False(pdf.TryGetProperty("mergePaper", out _));
        Assert.False(pdf.TryGetProperty("mergeOrientation", out _));
        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();
        AssertLegacy(Assert.Single(reloaded.Presets));
        AssertDefaults(reloaded.Presets[0].Pdf);
        reloaded.SaveLibrary();
        store.Load();
        AssertLegacy(Assert.Single(store.Presets));
        AssertDefaults(store.Presets[0].Pdf);
        Assert.Equal(AppSettings.CurrentSchemaVersion, store.Settings.SchemaVersion);
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
        public bool SourceBookmarks { get; set; }
        public string PageSize { get; set; } = "";
        public double MarginMm { get; set; }
        public int PagesPerSheet { get; set; }
        public string SheetPaper { get; set; } = "";
        public string SheetOrientation { get; set; } = "";
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

    private static void AssertDefaults(PdfOptions pdf)
    {
        Assert.False(pdf.NormalizeMergePaper);
        Assert.Equal(PdfSheetPaper.A4, pdf.MergePaper);
        Assert.Equal(PdfSheetOrientation.Auto, pdf.MergeOrientation);
    }

    private static void AssertLegacy(Preset preset)
    {
        Assert.Equal("legacy-merge", preset.Id);
        Assert.Equal("Legacy merge", preset.Name);
        Assert.Equal("pdf", preset.TargetFormat);
        Assert.True(preset.Pdf.MergeIntoSingle);
        Assert.True(preset.Pdf.SourceBookmarks);
        Assert.Equal(PdfPageSize.A4, preset.Pdf.PageSize);
        Assert.Equal(8, preset.Pdf.MarginMm);
        Assert.Equal(2, preset.Pdf.PagesPerSheet);
        Assert.Equal(PdfSheetPaper.Letter, preset.Pdf.SheetPaper);
        Assert.Equal(PdfSheetOrientation.Landscape, preset.Pdf.SheetOrientation);
        Assert.True(preset.Pdf.Ocr);
        Assert.Equal("kor+eng", preset.Pdf.OcrLanguage);
        Assert.Equal(OutputLocation.Subfolder, preset.Output.Location);
        Assert.Equal("exports", preset.Output.SubfolderName);
        Assert.Equal("{name}_joined", preset.Output.FileNamePattern);
        Assert.Equal(ConflictPolicy.Skip, preset.Output.Conflict);
        Assert.True(preset.Output.KeepDates);
    }
}
