// "Add background": settings schema 64 brings the tool to existing libraries (in the palette only, after Annotate), its
// look is kept in the tool apart from the conversion's and read leniently, the conversion it starts carries the look
// and is applied once, a route that can't is refused, History offers no Retry an older Filee would run without it, and
// a picture of one's own never leaves in an export.

using System.Text.Json;
using System.Text.Json.Nodes;
using Filee.Core.Conversion;
using Filee.Core.History;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class BackgroundToolTests
{
    private static Preset Tool() => BuiltInData.CreatePresets().Single(p => p.Id == BuiltInData.BackgroundId);

    private static UserDataStore Saved(TempDir dir)
    {
        var store = new UserDataStore(dir.Path);
        store.Load();
        store.SaveSettings();
        store.SaveLibrary();
        return store;
    }

    [Fact]
    public void Schema_64_adds_the_Add_background_tool_after_Annotate_and_to_no_donut()
    {
        using var dir = new TempDir();
        Saved(dir);
        var path = Path.Combine(dir.Path, "presets.json");
        var presets = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        presets.Remove(presets.Single(n => n!["id"]!.GetValue<string>() == BuiltInData.BackgroundId));
        File.WriteAllText(path, presets.ToJsonString());
        var settingsPath = Path.Combine(dir.Path, "settings.json");
        var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        settings["schemaVersion"] = 63;
        File.WriteAllText(settingsPath, settings.ToJsonString());

        var store = new UserDataStore(dir.Path);
        store.Load();

        var tool = store.FindPreset(BuiltInData.BackgroundId)!;
        Assert.True(tool.IsBackgroundTool);
        Assert.Equal("{name} With Background", tool.Output.FileNamePattern);
        // Right after "Annotate", where a new library has it, not at the end of the list.
        var ids = store.Presets.Select(p => p.Id).ToList();
        Assert.Equal(ids.IndexOf(BuiltInData.AnnotateId) + 1, ids.IndexOf(BuiltInData.BackgroundId));
        Assert.Equal(BuiltInData.CreatePresets().Select(p => p.Id), ids.Where(BuiltInData.CreatePresets().Select(p => p.Id).Contains));
        Assert.DoesNotContain(store.Profiles, p => p.PresetIds.Contains(BuiltInData.BackgroundId) || p.BackPresetIds?.Contains(BuiltInData.BackgroundId) == true);
    }

    [Fact]
    public void Schema_64_puts_the_tool_at_the_end_without_Annotate_and_never_twice()
    {
        var presets = BuiltInData.CreatePresets().Where(p => p.Id is not (BuiltInData.BackgroundId or BuiltInData.AnnotateId)).ToList();
        var profiles = BuiltInData.CreateProfiles();

        Assert.True(SettingsMigrations.ApplyToLibrary(63, presets, profiles));
        Assert.Equal(BuiltInData.BackgroundId, presets[^1].Id);

        // Already there (a library a newer Filee saved): nothing changes.
        var count = presets.Count;
        Assert.False(SettingsMigrations.ApplyToLibrary(63, presets, profiles));
        Assert.Equal(count, presets.Count);
        Assert.False(SettingsMigrations.ApplyToLibrary(AppSettings.CurrentSchemaVersion, presets, profiles));
    }

    [Fact]
    public void The_look_is_kept_in_the_tool_and_the_conversion_carries_it_within_its_ranges()
    {
        var tool = Tool();
        BackgroundTool.Remember(tool, new BackdropOptions { Kind = BackdropKind.Color, Color = "#123456", Padding = 99, Corners = -3, Canvas = BackdropCanvas.Portrait });
        // The tool itself sets nothing on a background: only the conversion it starts does.
        Assert.Null(tool.Image.Backdrop);
        Assert.False(tool.Image.ReworksPixels);
        var json = JsonSerializer.Serialize(new List<Preset> { tool }, FileeJsonContext.Default.ListPreset);
        Assert.Contains("\"kind\": \"Color\"", json);
        Assert.Contains("\"canvas\": \"Portrait\"", json);

        var read = JsonSerializer.Deserialize(json, FileeJsonContext.Default.ListPreset)!.Single();
        var look = BackgroundTool.LookOf(read);
        Assert.Equal((BackdropKind.Color, "#123456", BackdropOptions.MaxPadding, 0, BackdropCanvas.Portrait),
            (look.Kind, look.Color, look.Padding, look.Corners, look.Canvas));

        var conversion = BackgroundTool.ConversionOf(read, look);
        Assert.Equal((PresetKind.Convert, "png", (string?)null), (conversion.Kind, conversion.TargetFormat, conversion.Tool));
        Assert.True(conversion.Image.ReworksPixels);
        Assert.Equal("#123456", conversion.Image.Backdrop!.Color);
        Assert.Null(conversion.Image.BackdropLook);
        Assert.False(conversion.Output.OnlyIfSmaller);
        // The tool is left as it was.
        Assert.True(read.IsBackgroundTool);
    }

    [Theory]
    [InlineData("\"kind\": \"Color\"", "\"kind\": \"Pattern\"")]          // a kind a newer Filee knows
    [InlineData("\"kind\": \"Color\"", "\"kind\": 7")]
    [InlineData("\"canvas\": \"Portrait\"", "\"canvas\": \"Story\"")]
    [InlineData("\"color\": \"#123456\"", "\"color\": null")]
    [InlineData("\"from\": \"#8EC5FC\"", "\"from\": \"blue-ish\"")]
    [InlineData("\"to\": \"#E0C3FC\"", "\"to\": 12")]
    public void A_look_edited_by_hand_or_by_a_newer_Filee_is_read_and_made_whole(string written, string edited)
    {
        var tool = Tool();
        BackgroundTool.Remember(tool, new BackdropOptions { Kind = BackdropKind.Color, Color = "#123456", Canvas = BackdropCanvas.Portrait });
        var json = JsonSerializer.Serialize(new List<Preset> { tool }, FileeJsonContext.Default.ListPreset);
        Assert.Contains(written, json);

        List<Preset>? read = null;
        var error = Record.Exception(() => read = JsonSerializer.Deserialize(json.Replace(written, edited, StringComparison.Ordinal), FileeJsonContext.Default.ListPreset));

        if (edited.EndsWith("12", StringComparison.Ordinal))
        {
            // A number where text belongs fails the whole file, as for every other preset field: nothing to make whole.
            Assert.NotNull(error);
            return;
        }
        Assert.Null(error);
        var look = BackgroundTool.LookOf(read!.Single());
        var defaults = new BackdropOptions();
        Assert.True(Enum.IsDefined(look.Kind) && Enum.IsDefined(look.Canvas));
        Assert.Matches("^#[0-9A-F]{6}$", look.From);
        Assert.Matches("^#[0-9A-F]{6}$", look.To);
        Assert.Matches("^#[0-9A-F]{6}$", look.Color);
        if (edited.Contains("color", StringComparison.Ordinal))
            Assert.Equal(defaults.Color, look.Color);
        if (edited.Contains("from", StringComparison.Ordinal))
            Assert.Equal(defaults.From, look.From);
    }

    [Fact]
    public void A_failed_conversion_with_a_background_offers_no_retry_an_older_Filee_would_run_without_it()
    {
        var job = new ConversionJob(["shot.png"], BackgroundTool.ConversionOf(Tool(), new BackdropOptions()), "Add background");
        job.Files[0].State = FileState.Failed;
        Assert.Null(HistoryEntry.From(job).Preset);
    }

    [Fact]
    public async Task The_background_is_set_once_by_the_first_engine_that_applies_image_options()
    {
        // EPS → TIFF (Ghostscript) → PNG (ImageMagick): set in the first step; a second would set it on a background
        // again.
        using var dir = new TempDir();
        var art = dir.File("art.eps");
        var reader = new BackdropRecorder("ghostscript", transforms: true, new ConversionEdge("eps", "tiff"));
        var writer = new BackdropRecorder("magick", transforms: true, new ConversionEdge("tiff", "png"));
        await using var queue = new JobQueue(new ConverterCatalog([reader, writer]));

        var job = new ConversionJob([art], BackgroundTool.ConversionOf(Tool(), new BackdropOptions()), "Test");
        await queue.RunAsync(job);

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        Assert.Equal([true], reader.Backdrops);
        Assert.Equal([false], writer.Backdrops);
    }

    [Fact]
    public async Task A_route_without_an_engine_that_sets_it_fails_instead_of_writing_the_picture_as_it_is()
    {
        using var dir = new TempDir();
        var picture = dir.File("scan.png");
        var copier = new BackdropRecorder("copier", transforms: false, new ConversionEdge("png", "png"));
        await using var queue = new JobQueue(new ConverterCatalog([copier]));

        var job = new ConversionJob([picture], BackgroundTool.ConversionOf(Tool(), new BackdropOptions()), "Test");
        await queue.RunAsync(job);

        var file = Assert.Single(job.Files);
        Assert.Equal(FileState.Failed, file.State);
        Assert.Equal("error.images_only", file.ErrorKey);
        Assert.Empty(copier.Backdrops);
        Assert.Empty(job.Outputs);
    }

    [Fact]
    public void The_background_pictures_path_never_leaves_in_an_export_and_never_comes_in_with_an_import()
    {
        using var dir = new TempDir();
        var store = new UserDataStore(dir.Path);
        store.Load();
        var tool = store.FindPreset(BuiltInData.BackgroundId)!;
        BackgroundTool.Remember(tool, new BackdropOptions { Kind = BackdropKind.Picture, Picture = "/Users/me/Private/holiday.jpg" });
        // A conversion kept in the library by hand carries one too.
        var conversion = BackgroundTool.ConversionOf(tool, BackgroundTool.LookOf(tool));
        conversion.Id = "my-background";
        conversion.Image.Backdrop!.Picture = "/Users/me/Private/beach.jpg";
        store.Presets.Add(conversion);
        var bundle = Path.Combine(dir.Path, "bundle.json");

        store.Export(bundle);

        var text = File.ReadAllText(bundle);
        Assert.DoesNotContain("holiday.jpg", text);
        Assert.DoesNotContain("beach.jpg", text);
        // Ours are kept.
        Assert.Equal("/Users/me/Private/holiday.jpg", tool.Image.BackdropLook!.Picture);
        Assert.Equal("/Users/me/Private/beach.jpg", conversion.Image.Backdrop.Picture);

        // Someone else's bundle pointing at a network share: the paths are dropped on the way in.
        var share = "\"picture\": \"\\\\\\\\server\\\\share\\\\x.png\", ";
        File.WriteAllText(bundle, text.Replace("\"kind\": \"Picture\"", share + "\"kind\": \"Picture\"", StringComparison.Ordinal));
        Assert.Equal(2, File.ReadAllText(bundle).Split("server").Length - 1);
        store.Import(bundle);
        Assert.Null(store.FindPreset(BuiltInData.BackgroundId)!.Image.BackdropLook!.Picture);
        Assert.Null(store.FindPreset("my-background")!.Image.Backdrop!.Picture);
    }

    /// <summary>A converter that notes whether each step it ran was asked to set the picture on a background.</summary>
    private sealed class BackdropRecorder(string id, bool transforms, params ConversionEdge[] edges) : IConverter, IImageTransformer
    {
        private readonly FakeConverter _writer = new(id, edges);

        public string Id => id;
        public string DisplayName => id;
        public IReadOnlyList<ConversionEdge> Edges => edges;
        public int MaxParallelism => 0;
        public List<bool> Backdrops { get; } = [];

        public bool Transforms(string from, string to) => transforms;

        public EngineStatus GetStatus() => EngineStatus.Available();

        public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            lock (Backdrops)
                Backdrops.Add(step.Preset.Image.AddsBackdrop);
            return _writer.ConvertAsync(step, progress, cancellationToken);
        }
    }

    [Fact]
    public void The_stamp_of_the_picture_behind_shown_goes_with_the_conversion_only()
    {
        var written = new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc).AddTicks(1234567);
        var look = new BackdropOptions { Kind = BackdropKind.Picture, Picture = "/Users/me/behind.png", PictureWritten = written, PictureLength = 4321 };

        var conversion = BackgroundTool.ConversionOf(Tool(), look);
        var json = JsonSerializer.Serialize(new List<Preset> { conversion }, FileeJsonContext.Default.ListPreset);
        var read = JsonSerializer.Deserialize(json, FileeJsonContext.Default.ListPreset)!.Single();
        Assert.Equal((written, 4321L), (read.Image.Backdrop!.PictureWritten, read.Image.Backdrop.PictureLength));

        // Not kept as the look for next time (it is about the file as it was then), nor in an export.
        var tool = Tool();
        BackgroundTool.Remember(tool, look);
        Assert.Null(tool.Image.BackdropLook!.PictureWritten);
        Assert.Null(tool.Image.BackdropLook.PictureLength);
        var shared = conversion.ForSharing();
        Assert.Null(shared.Image.Backdrop!.PictureWritten);
        Assert.Null(shared.Image.Backdrop.PictureLength);
        // Without one (a look from before), nothing is checked.
        Assert.DoesNotContain("pictureWritten", JsonSerializer.Serialize(new List<Preset> { tool }, FileeJsonContext.Default.ListPreset), StringComparison.OrdinalIgnoreCase);
    }
}
