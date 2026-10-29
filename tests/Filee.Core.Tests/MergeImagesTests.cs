// "Merge images" (#645): the preset, its place on the images donut, how existing libraries get it (settings schema 53),
// presets files older and newer Filee versions read, and the queue merging pictures into one image.

using System.Text.Json;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class MergeImagesTests
{
    private static Preset BuiltIn(string id) => BuiltInData.CreatePresets().Single(p => p.Id == id);

    /// <summary>The images donut's back face of settings schema 37 to 52.</summary>
    private static readonly string[] BackBefore53 =
        ["tool-crop", "rotate", "flip", "remove-background", "trim-transparent", "grayscale", "to-avif", "to-gif", "merge-pdf",
            "compress-image", "remove-metadata", "zip-all"];

    [Fact]
    public void A_new_library_merges_pictures_in_their_first_format_next_to_merge_pdf()
    {
        var merge = BuiltIn("merge-images");
        Assert.Equal("preset.merge_images", merge.NameKey);
        Assert.True(merge.JoinsImages);
        Assert.False(merge.JoinsIntoOneMedia);
        Assert.Equal(BuiltInData.SameAsSource, merge.TargetFormat);
        Assert.Equal("{name}_merged", merge.Output.FileNamePattern); // as "Merge videos": named after the first file
        Assert.Equal((ImageJoinDirection.Vertical, ImageJoinFit.SameSize, 0, "#FFFFFF"),
            (merge.Image.JoinDirection, merge.Image.JoinFit, merge.Image.JoinGap, merge.Image.JoinBackground));

        var back = BuiltInData.CreateProfiles().Single(p => p.Id == "images").BackPresetIds!;
        Assert.Equal(ToolbarProfile.MaxSlices, back.Count);
        Assert.Equal(["to-avif", "merge-images", "merge-pdf"], back[6..9]);
        // GIF gave its slot: still a preset of the library, and on the video donut's front.
        Assert.DoesNotContain("to-gif", back);
        Assert.Single(BuiltInData.CreatePresets(), p => p.Id == "to-gif");
        Assert.Equal(["merge-images"], BuiltInData.CreatePresets().Where(p => p.JoinsImages).Select(p => p.Id));
    }

    [Theory]
    [InlineData("*", true)]
    [InlineData("png", true)]
    [InlineData("jpg", true)]
    [InlineData("pdf", false)]
    [InlineData("mp4", false)]
    public void Only_a_picture_target_merges_pictures(string target, bool merges)
    {
        Assert.Equal(merges, new Preset { TargetFormat = target, Image = { JoinIntoOne = true } }.JoinsImages);
        Assert.False(new Preset { TargetFormat = target }.JoinsImages);
    }

    [Fact]
    public void A_merge_of_pictures_is_saved_as_one_file()
    {
        Assert.True(AskedLocation.MakesOneResult(BuiltIn("merge-images"), 3));
        Assert.False(AskedLocation.MakesOneResult(BuiltIn("to-png"), 3));
    }

    [Fact]
    public void The_gap_stays_within_its_limits()
    {
        Assert.Equal(0, new ImageOptions { JoinGap = -5 }.JoinSpacing);
        Assert.Equal(12, new ImageOptions { JoinGap = 12 }.JoinSpacing);
        Assert.Equal(ImageOptions.MaxJoinGap, new ImageOptions { JoinGap = 1_000_000 }.JoinSpacing);
    }

    [Fact]
    public void Schema_53_puts_merge_images_in_place_of_gif_on_a_full_images_back_face()
    {
        var presets = BuiltInData.CreatePresets().Where(p => p.Id != "merge-images").ToList();
        var profiles = BuiltInData.CreateProfiles();
        var images = profiles.Single(p => p.Id == "images");
        images.BackPresetIds = [.. BackBefore53];

        Assert.True(SettingsMigrations.ApplyToLibrary(50, presets, profiles));

        Assert.Equal(BuiltInData.CreateProfiles().Single(p => p.Id == "images").BackPresetIds, images.BackPresetIds);
        // In the library after the media tools, as in a new one.
        Assert.Equal(presets.FindIndex(p => p.Id == "remove-metadata") + 1, presets.FindIndex(p => p.Id == "merge-images"));
        Assert.Single(presets, p => p.Id == "to-gif");
        Assert.False(SettingsMigrations.ApplyToLibrary(AppSettings.CurrentSchemaVersion, presets, profiles));
    }

    [Fact]
    public void Schema_53_takes_a_free_slot_before_merge_pdf_and_leaves_gif()
    {
        var presets = BuiltInData.CreatePresets().Where(p => p.Id != "merge-images").ToList();
        var profiles = BuiltInData.CreateProfiles();
        var images = profiles.Single(p => p.Id == "images");
        images.BackPresetIds = ["tool-crop", "to-gif", "merge-pdf", "zip-all"];

        Assert.True(SettingsMigrations.ApplyToLibrary(50, presets, profiles));

        Assert.Equal(["tool-crop", "to-gif", "merge-images", "merge-pdf", "zip-all"], images.BackPresetIds);
    }

    [Fact]
    public void Schema_53_leaves_a_donut_where_the_user_put_merge_images_or_has_no_gif_to_give()
    {
        var presets = BuiltInData.CreatePresets();
        var profiles = BuiltInData.CreateProfiles();
        var images = profiles.Single(p => p.Id == "images");
        images.PresetIds = ["merge-images", "to-png"];
        images.BackPresetIds = [.. BackBefore53];

        Assert.False(SettingsMigrations.ApplyToLibrary(50, presets, profiles));
        Assert.Equal(BackBefore53, images.BackPresetIds);

        // A full face the user laid out without GIF: nothing of theirs goes.
        images.PresetIds = ["to-png"];
        images.BackPresetIds = [.. BackBefore53.Select(id => id == "to-gif" ? "to-bmp" : id)];
        Assert.False(SettingsMigrations.ApplyToLibrary(50, presets, profiles));
        Assert.DoesNotContain("merge-images", images.BackPresetIds);
    }

    [Fact]
    public void Schema_53_puts_the_option_back_when_an_older_filee_dropped_it()
    {
        // A Filee before 53 saved the library again: the preset is left with what it knows, its name kept.
        var presets = BuiltInData.CreatePresets();
        var stripped = presets.Single(p => p.Id == "merge-images");
        stripped.Image.JoinIntoOne = false;
        stripped.Name = "Stack my scans";
        var profiles = BuiltInData.CreateProfiles();

        Assert.True(SettingsMigrations.ApplyToLibrary(50, presets, profiles));

        Assert.True(stripped.JoinsImages);
        Assert.Equal("Stack my scans", stripped.Name);
        Assert.Single(presets, p => p.Id == "merge-images");
    }

    [Fact]
    public void A_presets_file_keeps_the_layout_and_reads_a_newer_filee_s_values_as_the_defaults()
    {
        var merge = BuiltIn("merge-images");
        merge.Image.JoinDirection = ImageJoinDirection.Horizontal;
        merge.Image.JoinFit = ImageJoinFit.Original;
        merge.Image.JoinGap = 24;
        merge.Image.JoinBackground = ImageOptions.TransparentJoin;
        var json = JsonSerializer.Serialize([merge], FileeJsonContext.Default.ListPreset);
        Assert.Contains("\"joinDirection\": \"Horizontal\"", json); // by name, as the other enums
        var read = Assert.Single(JsonSerializer.Deserialize(json, FileeJsonContext.Default.ListPreset)!);
        Assert.Equal((ImageJoinDirection.Horizontal, ImageJoinFit.Original, 24, "transparent"),
            (read.Image.JoinDirection, read.Image.JoinFit, read.Image.JoinGap, read.Image.JoinBackground));

        // A direction or fit this version doesn't know (a newer Filee's) doesn't lose the presets file.
        var newer = json.Replace("\"Horizontal\"", "\"Diagonal\"").Replace("\"Original\"", "7");
        var fallback = Assert.Single(JsonSerializer.Deserialize(newer, FileeJsonContext.Default.ListPreset)!);
        Assert.Equal((ImageJoinDirection.Vertical, ImageJoinFit.SameSize), (fallback.Image.JoinDirection, fallback.Image.JoinFit));
    }

    private static async Task<ConversionJob> Run(JobQueue queue, IReadOnlyList<string> sources, Preset preset)
    {
        var job = new ConversionJob(sources, preset, "Merge images");
        await queue.RunAsync(job);
        return job;
    }

    [Fact]
    public async Task Pictures_merge_in_order_into_one_file_in_the_first_one_s_format()
    {
        using var dir = new TempDir();
        var second = dir.File("b.png");
        var first = dir.File("a.jpg");
        var joiner = new FakeImageJoiner();
        await using var queue = new JobQueue(new ConverterCatalog([joiner]));

        var job = await Run(queue, [first, second], BuiltIn("merge-images"));

        Assert.Equal(JobState.Completed, job.State);
        var output = Assert.Single(job.Outputs);
        Assert.Equal("a_merged.jpg", Path.GetFileName(output));
        Assert.Equal([(first, "jpg"), (second, "png")], joiner.Inputs);
        Assert.Equal("jpg", joiner.Format);
        Assert.All(job.Files, f => Assert.Equal(FileState.Done, f.State));
        Assert.Equal("a.jpg|b.png", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_picture_the_joiner_cannot_read_becomes_a_plain_png_and_a_first_format_it_cannot_write_gives_png()
    {
        using var dir = new TempDir();
        var icon = dir.File("a.icns");
        var photo = dir.File("b.jpg");
        var document = dir.File("c.docx");
        var joiner = new FakeImageJoiner { Unreadable = ["icns"], Unwritable = ["icns"] };
        var engine = new FakeConverter("icns", new ConversionEdge("icns", "png"));
        await using var queue = new JobQueue(new ConverterCatalog([engine, joiner]));
        var preset = BuiltIn("merge-images").Clone();
        preset.Image.Resize = ResizeMode.Percent; // the merged image's: not applied to the picture on its way
        preset.Image.Rotation = ImageRotation.Right90;

        var job = await Run(queue, [icon, photo, document], preset);

        Assert.Equal(JobState.CompletedWithErrors, job.State);
        Assert.Equal("error.images_only", job.Files[2].ErrorKey);
        Assert.Equal("a_merged.png", Path.GetFileName(Assert.Single(job.Outputs)));
        Assert.Equal("png", joiner.Format);
        Assert.Equal(2, joiner.Inputs.Count);
        Assert.Equal("png", joiner.Inputs[0].Format);
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "Filee"), joiner.Inputs[0].Path); // in the job's work folder
        Assert.Equal((photo, "jpg"), joiner.Inputs[1]);
        var step = Assert.Single(engine.Presets);
        Assert.Equal((ResizeMode.None, ImageRotation.None, false), (step.Image.Resize, step.Image.Rotation, step.Image.JoinIntoOne));
        Assert.Equal(ImageRotation.Right90, joiner.Options!.Rotation); // turned once, as the pictures merge
    }

    [Fact]
    public async Task A_target_the_joiner_cannot_write_fails_every_picture()
    {
        using var dir = new TempDir();
        var joiner = new FakeImageJoiner { Unwritable = ["heic"] };
        await using var queue = new JobQueue(new ConverterCatalog([joiner]));
        var preset = BuiltIn("merge-images").Clone();
        preset.TargetFormat = "heic";

        var job = await Run(queue, [dir.File("a.png"), dir.File("b.png")], preset);

        Assert.Equal(JobState.Failed, job.State);
        Assert.All(job.Files, f => Assert.Equal("error.no_route", f.ErrorKey));
        Assert.Empty(joiner.Inputs);
    }

    [Fact]
    public async Task A_merge_refused_as_too_large_says_why_and_leaves_no_file()
    {
        using var dir = new TempDir();
        var joiner = new FakeImageJoiner { Refusal = new ConversionRefusedException("error.merged_image_too_large", "30000 × 9000 px") };
        await using var queue = new JobQueue(new ConverterCatalog([joiner]));

        var job = await Run(queue, [dir.File("a.png"), dir.File("b.png")], BuiltIn("merge-images"));

        Assert.Equal(JobState.Failed, job.State);
        Assert.All(job.Files, f => Assert.Equal(("error.merged_image_too_large", "30000 × 9000 px"), (f.ErrorKey, f.ErrorDetail)));
        Assert.Empty(job.Outputs);
        Assert.False(File.Exists(Path.Combine(dir.Path, "a_merged.png")));
    }
}

/// <summary>Test double for the image engine's merging: records what it was given and writes the pictures' names.</summary>
internal sealed class FakeImageJoiner : IConverter, IImageJoiner
{
    public string Id => "fake-joiner";
    public string DisplayName => Id;
    public IReadOnlyList<ConversionEdge> Edges => [];
    public int MaxParallelism => 0;
    public EngineStatus GetStatus() => EngineStatus.Available();

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionStep step, IProgress<double>? progress, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public HashSet<string> Unreadable { get; init; } = [];
    public HashSet<string> Unwritable { get; init; } = [];
    public Exception? Refusal { get; init; }
    public List<(string Path, string Format)> Inputs { get; } = [];
    public string? Format { get; private set; }
    public ImageOptions? Options { get; private set; }

    public bool CanRead(string format) => !Unreadable.Contains(format);
    public bool CanWrite(string format) => !Unwritable.Contains(format);

    public async Task JoinAsync(IReadOnlyList<(string Path, string Format)> inputs, string outputPath, string format, ImageOptions options,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // Something written first, as a real merge would: a refused one must not leave it behind.
        await File.WriteAllTextAsync(outputPath, "", cancellationToken);
        if (Refusal is not null)
            throw Refusal;
        Inputs.AddRange(inputs);
        Format = format;
        Options = options;
        await File.WriteAllTextAsync(outputPath, string.Join("|", inputs.Select(i => Path.GetFileName(i.Path))), cancellationToken);
    }
}
