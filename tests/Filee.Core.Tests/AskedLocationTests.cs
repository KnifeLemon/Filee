// "Ask every time" (#521): what the save dialog suggests, and where the files go once it is answered: only the file
// the dialog asked about may replace one, and a preset that asks still loads in a Filee from before it.

using System.Text.Json;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class AskedLocationTests
{
    private static readonly OutputPathResolver.Tokens At = new(Path.Combine(Path.GetTempPath(), "photos", "IMG_0412.heic"), "JPG", 1, DateTime.Now);

    private static Preset Asking(string target = "jpg") => new()
    {
        TargetFormat = target,
        Output = { AskEveryTime = true, FileNamePattern = "{name}_small", Renames = [new RenameStep { Kind = RenameKind.Replace, Find = "IMG_", Replace = "Photo_" }] },
    };

    [Fact]
    public void The_dialog_suggests_the_name_the_preset_would_give()
    {
        Assert.Equal("Photo_0412_small.jpg", AskedLocation.SuggestedName(Asking(), "JPG", At.SourcePath));
        Assert.Equal("Photo_0412_small.heic", AskedLocation.SuggestedName(Asking(BuiltInData.SameAsSource), "Same", At.SourcePath));
        // Saved as a ZIP (the preset's or Settings › General's): the result is a .zip.
        var zipped = Asking();
        zipped.Output.ZipResult = true;
        Assert.Equal("Photo_0412_small.zip", AskedLocation.SuggestedName(zipped, "JPG", At.SourcePath));
    }

    [Fact]
    public void One_file_is_asked_for_by_name_also_when_several_sources_make_it()
    {
        Assert.True(AskedLocation.MakesOneResult(Asking(), 1));
        Assert.False(AskedLocation.MakesOneResult(Asking(), 3));
        Assert.True(AskedLocation.MakesOneResult(new Preset { TargetFormat = "pdf", Pdf = { MergeIntoSingle = true } }, 3));
        Assert.True(AskedLocation.MakesOneResult(new Preset { TargetFormat = "tiff", Image = { MultiPageTiff = true } }, 3));
        Assert.True(AskedLocation.MakesOneResult(BuiltInData.CreatePresets().First(p => p.PacksIntoOneArchive), 3));
        Assert.True(AskedLocation.MakesOneResult(BuiltInData.CreatePresets().First(p => p.JoinsIntoOneMedia), 3));
    }

    [Fact]
    public void Where_nothing_asks_the_presets_location_applies()
    {
        var preset = Asking();
        preset.Output.Location = OutputLocation.SameFolder;
        var path = OutputPathResolver.Resolve(preset.Output, At, "jpg", _ => false);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(At.SourcePath)!, "Photo_0412_small.jpg"), path);
    }

    [Fact]
    public void Only_the_file_the_dialog_asked_about_is_replaced()
    {
        var chosen = Path.Combine(Path.GetTempPath(), "out", "holiday.jpg");
        var preset = Asking();

        var asked = AskedLocation.ForFile(preset, chosen);

        // The name typed in the dialog, without the pattern or renames on top, replacing what's there.
        Assert.Equal(chosen, OutputPathResolver.Resolve(asked.Output, At, "jpg", _ => true));
        // Pages of it, or a result with another extension than the one typed, keep the preset's own policy.
        Assert.Equal(Path.Combine(Path.GetTempPath(), "out", "holiday_p1 (2).jpg"),
            OutputPathResolver.Resolve(asked.Output, At, "jpg", p => !p.Contains("(2)"), "_p1"));
        Assert.Equal(Path.Combine(Path.GetTempPath(), "out", "holiday (2).zip"),
            OutputPathResolver.Resolve(asked.Output, At, "zip", p => !p.Contains("(2)")));
        // The preset itself keeps asking.
        Assert.True(preset.Output.AskEveryTime);
        Assert.Single(preset.Output.Renames);
        Assert.False(asked.Output.AskEveryTime);
    }

    [Fact]
    public async Task Pages_never_replace_files_the_dialog_didnt_ask_about()
    {
        using var dir = new TempDir();
        var pdf = dir.File("book.pdf");
        var outFolder = Path.Combine(dir.Path, "out");
        Directory.CreateDirectory(outFolder);
        var existing = Path.Combine(outFolder, "holiday_p1.png");
        File.WriteAllText(existing, "keep me");
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("pdfium", new ConversionEdge("pdf", "png")) { OutputsPerInput = 2 }]));
        var asked = AskedLocation.ForFile(new Preset { TargetFormat = "png", Output = { AskEveryTime = true } }, Path.Combine(outFolder, "holiday.png"));

        var job = new ConversionJob([pdf], asked, "PNG");
        await queue.RunAsync(job);

        Assert.Equal("keep me", File.ReadAllText(existing));
        Assert.Contains(Path.Combine(outFolder, "holiday_p1 (2).png"), job.Outputs);
    }

    [Fact]
    public void Several_results_go_into_the_chosen_folder_under_their_own_names()
    {
        var folder = Path.Combine(Path.GetTempPath(), "chosen");

        var asked = AskedLocation.ForFolder(Asking(), folder);

        Assert.Equal(Path.Combine(folder, "Photo_0412_small.jpg"), OutputPathResolver.Resolve(asked.Output, At, "jpg", _ => false));
        // An existing file there isn't replaced: only one picked by name in the dialog is.
        Assert.Equal(Path.Combine(folder, "Photo_0412_small (2).jpg"), OutputPathResolver.Resolve(asked.Output, At, "jpg", p => !p.Contains("(2)")));
    }

    [Fact]
    public void The_chosen_place_is_where_the_result_goes_not_back_over_the_original()
    {
        var preset = Asking(BuiltInData.SameAsSource);
        preset.Output.ReplaceOriginal = true;

        Assert.False(AskedLocation.ForFolder(preset, Path.GetTempPath()).Output.ReplaceOriginal);
        Assert.False(AskedLocation.ForFile(preset, Path.Combine(Path.GetTempPath(), "x.heic")).Output.ReplaceOriginal);
        Assert.True(preset.Output.ReplaceOriginal);
    }

    [Fact]
    public void A_preset_that_asks_keeps_its_location_for_a_filee_that_cant()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        var preset = new Preset { Id = "asks", TargetFormat = "png", Output = { AskEveryTime = true, Location = OutputLocation.SameFolder } };
        store.Presets.Add(preset);
        store.SaveLibrary();

        var json = File.ReadAllText(Path.Combine(dir.Path, "presets.json"));
        // Only names every version knows: 2.1.0 reads "SameFolder" and skips "AskEveryTime".
        Assert.DoesNotContain("\"Ask\"", json);
        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();
        var back = reloaded.FindPreset("asks")!;
        Assert.True(back.Output.AskEveryTime);
        Assert.Equal(OutputLocation.SameFolder, back.Output.Location);
    }

    [Fact]
    public void A_location_from_a_newer_filee_reads_as_the_default()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.Presets.Add(new Preset { Id = "future", TargetFormat = "png" });
        store.SaveLibrary();
        var path = Path.Combine(dir.Path, "presets.json");
        var json = File.ReadAllText(path);
        File.WriteAllText(path, json.Replace("\"location\": \"Default\"", "\"location\": \"Somewhere\""));
        Assert.Contains("Somewhere", File.ReadAllText(path));

        var reloaded = new UserDataStore(dir.Path);
        reloaded.Load();

        // The user's presets survive (a broken file would be replaced by the factory ones).
        Assert.Equal(OutputLocation.Default, reloaded.FindPreset("future")!.Output.Location);
    }
}
