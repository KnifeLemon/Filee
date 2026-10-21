// The everyday media tools of settings schema 33: their presets, how existing libraries get them, and the queue
// joining videos, audio files and pictures into one file.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Core.Profiles;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class MediaToolTests
{
    private static readonly string[] ToolIds =
        ["compress-image", "compress-video", "video-from-images", "extract-audio", "merge-videos", "compress-audio", "merge-audio", "remove-metadata"];

    private static Preset BuiltIn(string id) => BuiltInData.CreatePresets().Single(p => p.Id == id);

    private static async Task<ConversionJob> Run(JobQueue queue, IReadOnlyList<string> sources, Preset preset)
    {
        var job = new ConversionJob(sources, preset, "Test");
        await queue.RunAsync(job);
        return job;
    }

    [Fact]
    public void A_new_library_has_every_tool_with_a_name_to_show()
    {
        var presets = BuiltInData.CreatePresets();
        foreach (var id in ToolIds)
            Assert.StartsWith("preset.", Assert.Single(presets, p => p.Id == id).NameKey);
    }

    [Fact]
    public void Only_the_merge_and_slideshow_tools_join_files()
    {
        Assert.Equal(["video-from-images", "merge-videos", "merge-audio"],
            BuiltInData.CreatePresets().Where(p => p.JoinsIntoOneMedia).Select(p => p.Id));
        // A join option on a target that can't hold it converts file by file, as before.
        Assert.False(new Preset { TargetFormat = "webm", Media = { JoinIntoOne = true } }.JoinsIntoOneMedia);
    }

    [Fact]
    public void Remove_metadata_takes_everything_and_keeps_the_format()
    {
        var clean = BuiltIn("remove-metadata");
        Assert.Equal(BuiltInData.SameAsSource, clean.TargetFormat);
        Assert.False(clean.Image.KeepMetadata);
        Assert.False(clean.Image.KeepColorProfile);
        Assert.True(clean.Image.KeepSourceQuality);
        Assert.True(clean.Pdf.RemoveMetadata);
        Assert.True(clean.Media.RemoveMetadata);
        Assert.False(clean.Output.KeepDates);
    }

    [Fact]
    public void Slides_and_fades_stay_within_their_limits()
    {
        var media = new MediaOptions { SlideSeconds = 2, CrossfadeSeconds = 5 };
        Assert.Equal(1, media.FadeDuration); // at most half of the time a picture is shown
        media.Crossfade = false;
        Assert.Equal(0, media.FadeDuration);
        Assert.Equal(MediaOptions.MaxSlideSeconds, new MediaOptions { SlideSeconds = 1e9 }.SlideDuration);
        Assert.Equal(3, new MediaOptions { SlideSeconds = double.NaN }.SlideDuration);
    }

    [Fact]
    public void Options_survive_a_copy()
    {
        var copy = BuiltIn("video-from-images").Clone();
        Assert.True(copy.Media.Slideshow);
        Assert.True(copy.Media.Crossfade);
        Assert.True(BuiltIn("compress-image").Clone().Image.MaxCompression);
    }

    [Fact]
    public void Version_32_libraries_get_the_tools_and_back_faces_with_room_offer_them()
    {
        var presets = BuiltInData.CreatePresets().Where(p => !ToolIds.Contains(p.Id)).ToList();
        var profiles = BuiltInData.CreateProfiles();
        foreach (var profile in profiles)
            profile.BackPresetIds!.RemoveAll(ToolIds.Contains);
        // A back face the user filled up stays as it is.
        var pdf = profiles.Single(p => p.Id == "pdf");
        while (pdf.BackPresetIds!.Count < ToolbarProfile.MaxSlices)
            pdf.BackPresetIds.Insert(0, presets.First(p => !pdf.BackPresetIds.Contains(p.Id) && !pdf.PresetIds.Contains(p.Id)).Id);
        var full = pdf.BackPresetIds.ToList();

        Assert.True(SettingsMigrations.ApplyToLibrary(32, presets, profiles));

        Assert.All(ToolIds, id => Assert.Single(presets, p => p.Id == id));
        var audio = profiles.Single(p => p.Id == "audio").BackPresetIds!;
        Assert.Equal(["to-alac", "compress-audio", "merge-audio", "remove-metadata", "zip-all"], audio);
        var video = profiles.Single(p => p.Id == "video").BackPresetIds!;
        Assert.Equal(["compress-video", "merge-videos", "zip-all"], video.TakeLast(3));
        Assert.Equal(full, pdf.BackPresetIds);
        Assert.False(SettingsMigrations.ApplyToLibrary(32, presets, profiles)); // once only
        Assert.False(SettingsMigrations.ApplyToLibrary(AppSettings.CurrentSchemaVersion, presets, profiles));
    }

    [Fact]
    public async Task Merge_videos_joins_the_clips_in_order_into_one_file_named_after_the_first()
    {
        using var dir = new TempDir();
        var second = dir.File("b.mp4");
        var first = dir.File("a.mov");
        var joiner = new FakeJoiner();
        await using var queue = new JobQueue(new ConverterCatalog([]), mediaJoiner: joiner);

        var job = await Run(queue, [first, second], BuiltIn("merge-videos"));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal([first, second], joiner.Inputs);
        Assert.Equal(Path.Combine(dir.Path, "a_merged.mp4"), job.Outputs.Single());
    }

    [Fact]
    public async Task A_file_that_is_no_video_fails_and_the_other_clips_still_join()
    {
        using var dir = new TempDir();
        var clip = dir.File("a.mp4");
        var song = dir.File("b.mp3");
        var joiner = new FakeJoiner();
        await using var queue = new JobQueue(new ConverterCatalog([]), mediaJoiner: joiner);

        var job = await Run(queue, [clip, song], BuiltIn("merge-videos"));

        Assert.Equal(JobState.CompletedWithErrors, job.State);
        Assert.Equal([clip], joiner.Inputs);
        Assert.Equal("error.no_route", job.Files[1].ErrorKey);
    }

    [Fact]
    public async Task Merge_audio_takes_audio_files_and_the_sound_of_videos()
    {
        using var dir = new TempDir();
        var song = dir.File("a.mp3");
        var clip = dir.File("b.mp4");
        var joiner = new FakeJoiner();
        await using var queue = new JobQueue(new ConverterCatalog([]), mediaJoiner: joiner);

        var job = await Run(queue, [song, clip], BuiltIn("merge-audio"));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal([song, clip], joiner.Inputs);
        Assert.Equal(Path.Combine(dir.Path, "a_merged.m4a"), job.Outputs.Single());
    }

    [Fact]
    public async Task Video_from_images_makes_pngs_first_and_takes_a_png_as_it_is()
    {
        using var dir = new TempDir();
        var photo = dir.File("a.jpg");
        var drawing = dir.File("b.png");
        var joiner = new FakeJoiner();
        await using var queue = new JobQueue(new ConverterCatalog([new FakeConverter("magick", new ConversionEdge("jpg", "png"))]),
            mediaJoiner: joiner);

        var job = await Run(queue, [photo, drawing], BuiltIn("video-from-images"));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(2, joiner.Inputs.Count);
        Assert.EndsWith(".png", joiner.Inputs[0]);
        Assert.NotEqual(photo, joiner.Inputs[0]); // converted in the work directory
        Assert.Equal(drawing, joiner.Inputs[1]);
        Assert.Equal(Path.Combine(dir.Path, "a_video.mp4"), job.Outputs.Single());
        Assert.False(File.Exists(Path.Combine(dir.Path, "a.png"))); // no picture left beside the source
    }

    [Fact]
    public async Task A_failed_join_fails_every_file_and_leaves_no_output()
    {
        using var dir = new TempDir();
        var a = dir.File("a.mp4");
        var b = dir.File("b.mp4");
        await using var queue = new JobQueue(new ConverterCatalog([]), mediaJoiner: new FakeJoiner { Throw = true });

        var job = await Run(queue, [a, b], BuiltIn("merge-videos"));

        Assert.Equal(JobState.Failed, job.State);
        Assert.All(job.Files, f => Assert.Equal("error.conversion_failed", f.ErrorKey));
        Assert.Empty(job.Outputs);
    }
}
