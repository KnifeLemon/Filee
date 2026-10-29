// "Pixelate / blur": where a part lies in pixels, how large the squares or the blur are and which square a pixel is in,
// the conversion the tool becomes, how it is saved (and read by an older Filee), the job queue covering the parts once
// and never handing the picture over uncovered, and the tool arriving in libraries from before it (settings schema 52).

using System.Text.Json;
using Filee.Core.Conversion;
using Filee.Core.History;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class HideToolTests
{
    private static Preset Tool() => BuiltInData.CreatePresets().Single(p => p.Id == "tool-hide");

    private static HiddenArea Area(double x, double y, double w, double h) => new() { X = x, Y = y, Width = w, Height = h };

    // ───────────────────────── The arithmetic ─────────────────────────

    [Theory]
    [InlineData(1, 1920, 1080, 21.6)]   // a desktop screenshot: 2 % of 1080, a little over a line of text
    [InlineData(5, 1920, 1080, 43.2)]   // the default: 4 %
    [InlineData(10, 1920, 1080, 70.2)]  // 6.5 %
    [InlineData(5, 1080, 2400, 43.2)]   // a phone screenshot: its shorter side counts, as tall as a line of its text
    [InlineData(5, 6000, 4000, 160)]    // a photo
    [InlineData(1, 120, 80, 8)]         // never under 8 px
    [InlineData(0, 1920, 1080, 21.6)]   // a hand-edited strength: within 1 … 10
    [InlineData(99, 1920, 1080, 70.2)]
    public void Squares_and_blur_grow_with_the_strength_and_the_pictures_shorter_side(int strength, int width, int height, double expected) =>
        Assert.Equal(expected, HideTool.EffectSize(strength, width, height), 6);

    [Fact]
    public void Even_the_weakest_strength_covers_a_desktop_screenshots_line_of_text()
    {
        // Text on a 1080p screen is about 13 px tall: a square at least that tall leaves no letter to compare.
        Assert.True(HideTool.EffectSize(HideTool.MinStrength, 1920, 1080) >= 13);
        Assert.True(HideTool.EffectSize(HideTool.MinStrength, 3840, 2160) >= 26); // the same screen at 200 %
    }

    [Fact]
    public void A_part_in_pixels_takes_every_pixel_it_touches()
    {
        // 0.1 × 1000 = 100 exactly; 0.1234 × 1000 = 123.4 → from 123; to 0.3234 × 1000 = 323.4 → up to 324.
        Assert.Equal((100, 50, 200, 100), HideTool.PixelBounds(Area(0.1, 0.1, 0.2, 0.2), 1000, 500));
        Assert.Equal((123, 61, 201, 101), HideTool.PixelBounds(Area(0.1234, 0.1234, 0.2, 0.2), 1000, 500));
        // At least one pixel, even for a sliver.
        Assert.Equal((500, 250, 1, 1), HideTool.PixelBounds(Area(0.5, 0.5, 0.0001, 0.0001), 1000, 500));
    }

    [Fact]
    public void A_part_is_kept_inside_the_picture_and_nothing_is_made_of_one_outside_it()
    {
        Assert.Equal((900, 0, 100, 100), HideTool.PixelBounds(Area(0.9, -0.5, 0.5, 0.7), 1000, 500));
        // Drawn from right to left (a negative width): the same part.
        Assert.Equal((100, 50, 200, 100), HideTool.PixelBounds(Area(0.3, 0.3, -0.2, -0.2), 1000, 500));
        Assert.Null(HideTool.PixelBounds(Area(1.2, 0.2, 0.3, 0.3), 1000, 500));
        Assert.Null(HideTool.PixelBounds(Area(0.2, 0.2, 0, 0.3), 1000, 500));
        Assert.Null(HideTool.PixelBounds(Area(double.NaN, 0.2, 0.1, 0.1), 1000, 500));
        Assert.Null(HideTool.PixelBounds(Area(0.2, 0.2, 0.1, 0.1), 0, 500));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(2.5)]
    [InlineData(7.3)]
    [InlineData(43.2)]
    public void Every_pixel_falls_in_exactly_one_square_starting_where_the_square_starts(double cell)
    {
        Assert.Equal(0, HideTool.CellStart(0, cell));
        for (var pixel = 0; pixel < 500; pixel++)
        {
            var square = HideTool.CellOf(pixel, cell);
            Assert.InRange(pixel, HideTool.CellStart(square, cell), HideTool.CellStart(square + 1, cell) - 1);
        }
        // Whole-pixel squares start on their multiples.
        Assert.Equal((0, 10, 20), (HideTool.CellStart(0, 10), HideTool.CellStart(1, 10), HideTool.CellStart(2, 10)));
        Assert.Equal((0, 2, 5), (HideTool.CellStart(0, 2.5), HideTool.CellStart(1, 2.5), HideTool.CellStart(2, 2.5)));
    }

    // ───────────────────────── The tool and its conversion ─────────────────────────

    [Fact]
    public void The_built_in_tool_pixelates_at_the_default_strength_and_names_its_pictures_name_hidden()
    {
        var tool = Tool();
        Assert.True(tool.IsTool);
        Assert.True(tool.IsHideTool);
        Assert.False(tool.IsCropTool || tool.IsClipTool);
        Assert.Equal(("preset.hide", BuiltInData.SameAsSource, "{name}_hidden"), (tool.NameKey, tool.TargetFormat, tool.Output.FileNamePattern));
        Assert.Equal((HideStyle.Pixelate, HideTool.DefaultStrength), (tool.Image.HideStyle, tool.Image.HideStrength));
        Assert.Null(tool.Image.HiddenAreas);
        Assert.False(tool.Image.HidesAreas);
        Assert.False(new Preset { Kind = PresetKind.Tool, Tool = "crop" }.IsHideTool);
        Assert.False(new Preset { Tool = Preset.HideTool }.IsHideTool); // a conversion, not a tool
    }

    [Fact]
    public void The_tool_becomes_a_conversion_with_its_parts_and_never_keeps_the_original_for_being_smaller()
    {
        var tool = Tool();
        tool.Name = "Redact";
        tool.Image.HideStyle = HideStyle.Blur;
        tool.Image.HideStrength = 42; // a hand-edited file
        tool.Output.OnlyIfSmaller = true;

        var conversion = HideTool.ConversionOf(tool, "png", [Area(0.1, 0.1, 0.2, 0.2), Area(0.5, 0.5, -0.1, 0.2), Area(2, 2, 1, 1)]);

        Assert.Equal(PresetKind.Convert, conversion.Kind);
        Assert.Null(conversion.Tool);
        Assert.Equal("png", conversion.TargetFormat);
        Assert.Equal(("tool-hide", "Redact"), (conversion.Id, conversion.Name));
        Assert.Equal(HideStyle.Blur, conversion.Image.HideStyle);
        Assert.Equal(HideTool.MaxStrength, conversion.Image.HideStrength);
        // The part drawn backwards is turned around; the one off the picture is left out.
        Assert.Equal(2, conversion.Image.HiddenAreas!.Count);
        var turned = conversion.Image.HiddenAreas[1];
        Assert.Equal((0.4, 0.5, 0.1, 0.2), (Math.Round(turned.X, 9), Math.Round(turned.Y, 9), Math.Round(turned.Width, 9), Math.Round(turned.Height, 9)));
        Assert.True(conversion.Image.HidesAreas);
        Assert.True(conversion.Image.ReworksPixels);
        Assert.False(conversion.Output.OnlyIfSmaller);
        // The tool itself is untouched, and a copy of the conversion has its own parts.
        Assert.True(tool.IsHideTool);
        Assert.Null(tool.Image.HiddenAreas);
        Assert.True(tool.Output.OnlyIfSmaller);
        var copy = conversion.Clone();
        copy.Image.HiddenAreas![0].X = 0.9;
        Assert.Equal(0.1, conversion.Image.HiddenAreas[0].X);
    }

    [Fact]
    public void The_choice_is_remembered_in_the_tool()
    {
        var tool = Tool();
        HideTool.Remember(tool, HideStyle.Blur, 8);
        Assert.Equal((HideStyle.Blur, 8), (tool.Image.HideStyle, tool.Image.HideStrength));
        HideTool.Remember(tool, HideStyle.Pixelate, -3);
        Assert.Equal((HideStyle.Pixelate, HideTool.MinStrength), (tool.Image.HideStyle, tool.Image.HideStrength));
    }

    [Fact]
    public void A_picture_keeps_its_format_where_it_can_be_written_else_becomes_png()
    {
        var magick = new FakeConverter("magick", new ConversionEdge("jpg", "jpg"), new ConversionEdge("raw", "png"), new ConversionEdge("png", "png"));
        var planner = new ConverterCatalog([magick]).CreatePlanner();

        Assert.Equal(BuiltInData.SameAsSource, HideTool.TargetFor("jpg", planner));
        Assert.Equal(HideTool.FallbackTarget, HideTool.TargetFor("raw", planner)); // a camera RAW is read only
        Assert.Equal(HideTool.FallbackTarget, HideTool.TargetFor("webp", planner)); // no engine here
        Assert.True(HideTool.CanHide("jpg"));
        Assert.False(HideTool.CanHide("pdf"));
        Assert.False(HideTool.CanHide("mp4"));
    }

    // ───────────────────────── Saved, and read by an older Filee ─────────────────────────

    [Fact]
    public void The_choice_is_saved_by_name_and_the_parts_never_reach_a_saved_tool()
    {
        var tool = Tool();
        HideTool.Remember(tool, HideStyle.Blur, 7);
        var json = JsonSerializer.Serialize(new List<Preset> { tool }, FileeJsonContext.Default.ListPreset);
        Assert.Contains("\"tool\": \"hide\"", json);
        Assert.Contains("\"hideStyle\": \"Blur\"", json);
        Assert.Contains("\"hideStrength\": 7", json);
        Assert.DoesNotContain("hiddenAreas", json);

        var read = JsonSerializer.Deserialize(json, FileeJsonContext.Default.ListPreset)!.Single();
        Assert.True(read.IsHideTool);
        Assert.Equal((HideStyle.Blur, 7), (read.Image.HideStyle, read.Image.HideStrength));

        // A conversion keeps its parts.
        var conversion = HideTool.ConversionOf(read, "jpg", [Area(0.1, 0.2, 0.3, 0.4)]);
        var again = JsonSerializer.Deserialize(JsonSerializer.Serialize(new List<Preset> { conversion }, FileeJsonContext.Default.ListPreset),
            FileeJsonContext.Default.ListPreset)!.Single();
        var kept = again.Image.HiddenAreas![0];
        Assert.Equal((0.1, 0.2, 0.3, 0.4), (Math.Round(kept.X, 9), Math.Round(kept.Y, 9), Math.Round(kept.Width, 9), Math.Round(kept.Height, 9)));
    }

    [Fact]
    public void A_style_from_a_newer_Filee_reads_as_pixelate_instead_of_failing_the_whole_file()
    {
        var json = JsonSerializer.Serialize(new List<Preset> { Tool() }, FileeJsonContext.Default.ListPreset)
            .Replace("\"hideStyle\": \"Pixelate\"", "\"hideStyle\": \"Smudge\"", StringComparison.Ordinal);
        Assert.Contains("Smudge", json);
        var read = JsonSerializer.Deserialize(json, FileeJsonContext.Default.ListPreset)!.Single();
        Assert.Equal(HideStyle.Pixelate, read.Image.HideStyle);
    }

    [Fact]
    public void Every_new_option_is_a_field_of_its_own_so_an_older_Filee_reads_the_presets_as_before()
    {
        // 2.1.0 crashes on a value its enums don't know: the tool only adds new fields (which it skips) and a tool name
        // (which it greys out), and leaves every value it knows as it was.
        var tool = Tool();
        HideTool.Remember(tool, HideStyle.Blur, 9);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new List<Preset> { tool }, FileeJsonContext.Default.ListPreset));
        var saved = document.RootElement[0];
        Assert.Equal("Tool", saved.GetProperty("kind").GetString());
        var image = saved.GetProperty("image");
        Assert.Equal("None", image.GetProperty("resize").GetString());
        Assert.Equal("None", image.GetProperty("rotation").GetString());
        Assert.Equal("None", image.GetProperty("flip").GetString());
        Assert.Equal("Lzw", image.GetProperty("tiffCompression").GetString());
    }

    [Fact]
    public void A_failed_conversion_with_hidden_parts_offers_no_retry_an_older_Filee_would_run_without_them()
    {
        var conversion = HideTool.ConversionOf(Tool(), BuiltInData.SameAsSource, [Area(0.1, 0.1, 0.2, 0.2)]);
        var job = new ConversionJob(["shot.png"], conversion, "Pixelate / blur");
        job.Files[0].State = FileState.Failed;
        Assert.Null(HistoryEntry.From(job).Preset);

        var plain = new ConversionJob(["shot.png"], new Preset { TargetFormat = "jpg" }, "JPG");
        plain.Files[0].State = FileState.Failed;
        Assert.NotNull(HistoryEntry.From(plain).Preset);
    }

    // ───────────────────────── The job queue ─────────────────────────

    [Fact]
    public async Task The_parts_are_covered_once_by_the_first_engine_that_applies_image_options()
    {
        // EPS → PNG (Ghostscript) → JPG (ImageMagick): covered in the first step, the second gets the picture covered.
        using var dir = new TempDir();
        var art = dir.File("art.eps");
        var reader = new AreaRecorder("ghostscript", transforms: true, new ConversionEdge("eps", "png"));
        var writer = new AreaRecorder("magick", transforms: true, new ConversionEdge("png", "jpg"));
        await using var queue = new JobQueue(new ConverterCatalog([reader, writer]));

        var job = new ConversionJob([art], HideTool.ConversionOf(Tool(), "jpg", [Area(0.1, 0.1, 0.2, 0.2)]), "Test");
        await queue.RunAsync(job);

        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        Assert.Equal([1], reader.Areas);
        Assert.Equal([0], writer.Areas);
    }

    [Fact]
    public async Task A_route_without_an_engine_that_covers_them_fails_instead_of_writing_the_picture_as_it_is()
    {
        using var dir = new TempDir();
        var picture = dir.File("scan.png");
        var copier = new AreaRecorder("copier", transforms: false, new ConversionEdge("png", "png"));
        await using var queue = new JobQueue(new ConverterCatalog([copier]));

        var job = new ConversionJob([picture], HideTool.ConversionOf(Tool(), BuiltInData.SameAsSource, [Area(0.1, 0.1, 0.2, 0.2)]), "Test");
        await queue.RunAsync(job);

        var file = Assert.Single(job.Files);
        Assert.Equal(FileState.Failed, file.State);
        Assert.Equal("error.cannot_hide", file.ErrorKey);
        Assert.Empty(copier.Areas);
        Assert.Empty(job.Outputs);
    }

    [Fact]
    public async Task Only_if_smaller_never_puts_the_original_back_over_a_covered_picture()
    {
        using var dir = new TempDir();
        var picture = dir.File("shot.png"); // 1 byte: any result is larger
        var magick = new AreaRecorder("magick", transforms: true, new ConversionEdge("png", "png"));
        await using var queue = new JobQueue(new ConverterCatalog([magick]));
        var conversion = HideTool.ConversionOf(Tool(), BuiltInData.SameAsSource, [Area(0.1, 0.1, 0.2, 0.2)]);
        conversion.Output.OnlyIfSmaller = true; // set again after the tool turned it off

        var job = new ConversionJob([picture], conversion, "Test");
        await queue.RunAsync(job);

        var output = Assert.Single(job.Outputs);
        Assert.NotEqual("x", File.ReadAllText(output));
    }

    // ───────────────────────── Settings schema 52 ─────────────────────────

    [Fact]
    public void A_new_library_has_the_tool_after_edit_pictures_in_the_library_and_on_the_images_donut()
    {
        var presets = BuiltInData.CreatePresets();
        Assert.Equal("tool-image-edit", presets[presets.FindIndex(p => p.Id == "tool-hide") - 1].Id);
        var images = BuiltInData.CreateProfiles().Single(p => p.Id == "images");
        Assert.Equal(["half-size", "tool-image-edit", "tool-hide"], images.PresetIds.Skip(images.PresetIds.IndexOf("half-size")));
        Assert.DoesNotContain("tool-hide", images.BackPresetIds!);
        Assert.InRange(images.PresetIds.Count, 1, ToolbarProfile.MaxSlices);
    }

    private static (List<Preset> Presets, List<ToolbarProfile> Profiles, ToolbarProfile Images) Version51()
    {
        var presets = BuiltInData.CreatePresets();
        presets.RemoveAll(p => p.Id == "tool-hide");
        var profiles = BuiltInData.CreateProfiles();
        var images = profiles.Single(p => p.Id == "images");
        images.PresetIds.Remove("tool-hide");
        return (presets, profiles, images);
    }

    [Fact]
    public void Version_51_libraries_get_the_tool_after_edit_pictures_once()
    {
        var (presets, profiles, images) = Version51();

        Assert.True(SettingsMigrations.ApplyToLibrary(51, presets, profiles));

        Assert.Equal(BuiltInData.CreatePresets().Select(p => p.Id), presets.Select(p => p.Id));
        Assert.True(presets.Single(p => p.Id == "tool-hide").IsHideTool);
        Assert.Equal(BuiltInData.CreateProfiles().Single(p => p.Id == "images").PresetIds, images.PresetIds);
        Assert.False(SettingsMigrations.ApplyToLibrary(52, presets, profiles)); // once only
    }

    [Fact]
    public void After_edit_pictures_when_that_follows_half_size_else_after_half_size()
    {
        var (presets, profiles, images) = Version51();
        SettingsMigrations.ApplyToLibrary(51, presets, profiles);
        Assert.Equal(["half-size", "tool-image-edit", "tool-hide"], images.PresetIds.Skip(images.PresetIds.IndexOf("half-size")).Take(3));

        // "Edit pictures" moved to the back face by the user: right after "Half size".
        (presets, profiles, images) = Version51();
        images.PresetIds.Remove("tool-image-edit");
        images.BackPresetIds!.Remove("grayscale");
        images.BackPresetIds.Insert(0, "tool-image-edit");
        SettingsMigrations.ApplyToLibrary(51, presets, profiles);
        Assert.Equal(["half-size", "tool-hide"], images.PresetIds.Skip(images.PresetIds.IndexOf("half-size")).Take(2));
    }

    [Fact]
    public void A_full_front_puts_it_on_the_back_and_a_full_donut_or_the_users_own_place_is_left_alone()
    {
        // Front full: at the end of the back face (before "Compress into one archive") when that has room.
        var (presets, profiles, images) = Version51();
        while (images.PresetIds.Count < ToolbarProfile.MaxSlices)
            images.PresetIds.Add("to-gif");
        images.BackPresetIds!.Remove("grayscale");
        SettingsMigrations.ApplyToLibrary(51, presets, profiles);
        Assert.DoesNotContain("tool-hide", images.PresetIds);
        Assert.Equal("tool-hide", images.BackPresetIds[images.BackPresetIds.IndexOf("zip-all") - 1]);

        // Both full: in the library only.
        (presets, profiles, images) = Version51();
        while (images.PresetIds.Count < ToolbarProfile.MaxSlices)
            images.PresetIds.Add("to-gif");
        var front = images.PresetIds.ToList();
        var back = images.BackPresetIds!.ToList();
        Assert.True(SettingsMigrations.ApplyToLibrary(51, presets, profiles));
        Assert.Equal(front, images.PresetIds);
        Assert.Equal(back, images.BackPresetIds);
        Assert.Contains(presets, p => p.Id == "tool-hide");

        // Already on the donut where the user put it (a build of this branch): stays there.
        (presets, profiles, images) = Version51();
        images.BackPresetIds!.Remove("grayscale");
        images.BackPresetIds.Insert(0, "tool-hide");
        presets.Insert(0, Tool());
        SettingsMigrations.ApplyToLibrary(51, presets, profiles);
        Assert.DoesNotContain("tool-hide", images.PresetIds);
        Assert.Equal("tool-hide", images.BackPresetIds[0]);
    }

    [Fact]
    public void Back_from_a_Filee_before_2_1_the_tool_hides_parts_again_and_keeps_the_users_changes()
    {
        // A Filee before 45 (2.0) drops the tool's name: the tool reads as a crop there and is saved so.
        var presets = BuiltInData.CreatePresets();
        var hide = presets.Single(p => p.Id == "tool-hide");
        hide.Tool = null;
        hide.Name = "Redact";
        hide.Output.FileNamePattern = "{name}_safe";
        Assert.True(hide.IsCropTool);

        Assert.True(SettingsMigrations.ApplyToLibrary(51, presets, BuiltInData.CreateProfiles()));

        Assert.True(hide.IsHideTool);
        Assert.Equal(("Redact", "{name}_safe"), (hide.Name, hide.Output.FileNamePattern));
        Assert.Single(presets, p => p.Id == "tool-hide");

        // One that read it as a group.
        hide.Kind = PresetKind.Group;
        hide.TargetFormat = Preset.GroupTarget;
        Assert.True(SettingsMigrations.ApplyToLibrary(51, presets, BuiltInData.CreateProfiles()));
        Assert.True(hide.IsHideTool);
        Assert.Equal(BuiltInData.SameAsSource, hide.TargetFormat);
    }

    /// <summary>An engine that records how many hidden parts each step was given; it may say it applies image options.</summary>
    private sealed class AreaRecorder(string id, bool transforms, params ConversionEdge[] edges) : IConverter, IImageTransformer
    {
        private readonly FakeConverter _writer = new(id, edges);

        public string Id => id;
        public string DisplayName => id;
        public IReadOnlyList<ConversionEdge> Edges => edges;
        public int MaxParallelism => 0;
        public List<int> Areas { get; } = [];

        public bool Transforms(string from, string to) => transforms;

        public EngineStatus GetStatus() => EngineStatus.Available();

        public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            lock (Areas)
                Areas.Add(step.Preset.Image.HiddenAreas?.Count ?? 0);
            return _writer.ConvertAsync(step, progress, cancellationToken);
        }
    }
}
