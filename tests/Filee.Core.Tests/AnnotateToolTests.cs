// "Annotate": settings schema 59 brings the tool to existing libraries (in the palette only), and the marks a
// conversion carries keep their shape through saving.

using System.Text.Json.Nodes;
using Filee.Core.Conversion;
using Filee.Core.History;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class AnnotateToolTests
{
    private static UserDataStore Saved(TempDir dir)
    {
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.SaveSettings();
        store.SaveLibrary();
        return store;
    }

    [Fact]
    public void A_failed_conversion_with_marks_offers_no_retry_an_older_Filee_would_run_without_them()
    {
        var tool = BuiltInData.AnnotatePresets().Single();
        var marks = new[] { new Annotation { Kind = AnnotationKind.Box, Points = [new() { X = 0.1, Y = 0.1 }, new() { X = 0.5, Y = 0.5 }] } };
        var job = new ConversionJob(["shot.png"], AnnotateTool.ConversionOf(tool, BuiltInData.SameAsSource, marks), "Annotate");
        job.Files[0].State = FileState.Failed;
        Assert.Null(HistoryEntry.From(job).Preset);
    }

    [Fact]
    public void Schema_59_adds_the_Annotate_tool_and_marks_keep_their_shape_through_saving()
    {
        using var dir = new TempDir();
        Saved(dir);
        var path = Path.Combine(dir.Path, "presets.json");
        var presets = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        presets.Remove(presets.Single(n => n!["id"]!.GetValue<string>() == BuiltInData.AnnotateId));
        File.WriteAllText(path, presets.ToJsonString());
        var settingsPath = Path.Combine(dir.Path, "settings.json");
        var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        settings["schemaVersion"] = 58;
        File.WriteAllText(settingsPath, settings.ToJsonString());

        var store = new UserDataStore(dir.Path);
        store.Load();
        var tool = store.FindPreset(BuiltInData.AnnotateId)!;
        Assert.True(tool.IsAnnotateTool);
        // Right after "Pixelate / blur", where a new library has it, not at the end of the list.
        var ids = store.Presets.Select(p => p.Id).ToList();
        Assert.Equal(ids.IndexOf(BuiltInData.HidePresets().Single().Id) + 1, ids.IndexOf(BuiltInData.AnnotateId));
        Assert.Equal("{name} Annotated", tool.Output.FileNamePattern);

        var marks = new[]
        {
            new Annotation { Kind = AnnotationKind.Arrow, Color = "#3E63DD", Width = AnnotationWidth.Thick, Points = [new() { X = 0.1, Y = 0.2 }, new() { X = 0.8, Y = 0.9 }] },
            new Annotation { Kind = AnnotationKind.Text, Text = "Hi 안녕", Points = [new() { X = 0.5, Y = 0.5 }] },
            new Annotation { Kind = AnnotationKind.Number, Number = 3, Points = [new() { X = 0.3, Y = 0.3 }] },
        };
        var conversion = AnnotateTool.ConversionOf(tool, "png", marks);
        var json = System.Text.Json.JsonSerializer.Serialize(new List<Preset> { conversion }, FileeJsonContext.Default.ListPreset);
        var read = System.Text.Json.JsonSerializer.Deserialize(json, FileeJsonContext.Default.ListPreset)!.Single();

        Assert.True(read.Image.ReworksPixels);
        Assert.Equal([AnnotationKind.Arrow, AnnotationKind.Text, AnnotationKind.Number], read.Image.Annotations!.Select(m => m.Kind));
        Assert.Equal((0.8, AnnotationWidth.Thick), (read.Image.Annotations![0].Points[1].X, read.Image.Annotations[0].Width));
        Assert.Equal(("Hi 안녕", 3), (read.Image.Annotations![1].Text!, read.Image.Annotations[2].Number));
        // A kind a newer Filee draws is read without failing the whole file, and isn't drawn as something it isn't.
        var later = json.Replace("\"kind\": \"Arrow\"", "\"kind\": \"Ellipse\"", StringComparison.Ordinal);
        var unknown = System.Text.Json.JsonSerializer.Deserialize(later, FileeJsonContext.Default.ListPreset)!.Single().Image.Annotations![0];
        Assert.Equal(AnnotationKind.Unknown, unknown.Kind);
        Assert.False(AnnotateTool.IsDrawable(unknown));
        // Points left out or empty in a file edited by hand are no points: the mark isn't drawn, nothing fails.
        var noPoints = json.Replace("\"points\": [", "\"points\": null, \"x\": [", StringComparison.Ordinal);
        var read2 = System.Text.Json.JsonSerializer.Deserialize(noPoints, FileeJsonContext.Default.ListPreset)!.Single().Image.Annotations!;
        Assert.All(read2, m => Assert.Empty(m.Points));
        Assert.DoesNotContain(read2, AnnotateTool.IsDrawable);
    }
}
