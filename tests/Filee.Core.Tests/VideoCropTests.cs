// "Crop video" (#830): the ratios, the group on the video donut of a new install, and how existing libraries get it
// (settings schema 59: the library only, no slot).

using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class VideoCropTests
{
    private static readonly string[] CropIds =
        ["crop-video", "crop-video-auto", "crop-video-16-9", "crop-video-9-16", "crop-video-4-3", "crop-video-1-1", "crop-video-21-9"];

    [Theory]
    [InlineData("16:9", true, 16, 9)]
    [InlineData("9:16", true, 9, 16)]
    [InlineData("1:1", true, 1, 1)]
    [InlineData("auto", false, 0, 0)]
    [InlineData("", false, 0, 0)]
    [InlineData(null, false, 0, 0)]
    [InlineData("16:0", false, 0, 0)]
    [InlineData("-4:3", false, 0, 0)]
    [InlineData("16:9:1", false, 0, 0)]
    [InlineData("1000:1", false, 0, 0)]
    public void Ratios_are_two_whole_numbers(string? frame, bool ok, int width, int height)
    {
        Assert.Equal(ok, VideoCrop.TryParseRatio(frame, out var w, out var h));
        if (ok)
            Assert.Equal((width, height), (w, h));
        Assert.Equal(ok || frame == VideoCrop.Auto, VideoCrop.Crops(frame));
    }

    [Fact]
    public void Every_offered_ratio_reads()
    {
        Assert.All(VideoCrop.Ratios, r => Assert.True(VideoCrop.TryParseRatio(r, out _, out _), r));
    }

    [Fact]
    public void A_crop_of_a_vob_or_dv_video_is_saved_as_mp4()
    {
        // Their fixed frame sizes would stretch the crop back to the whole frame.
        Assert.Equal(["mp4", "mp4", "mkv", "mp4"], new[] { "vob", "dv", "mkv", "mp4" }.Select(VideoCrop.TargetFor));
        var crop = BuiltInData.CreatePresets().Single(p => p.Id == "crop-video-auto");
        Assert.Equal("mp4", AskedLocation.Extension(crop, @"C:\v\movie.vob"));
        Assert.Equal("mp4", AskedLocation.Extension(crop, @"C:\v\tape.dif"));
        Assert.Equal("mkv", AskedLocation.Extension(crop, @"C:\v\movie.mkv"));
        // Other tools keep the source's own format.
        Assert.Equal("vob", AskedLocation.Extension(BuiltInData.CreatePresets().Single(p => p.Id == "tool-clip"), @"C:\v\movie.vob"));
    }

    [Fact]
    public void A_new_library_has_the_group_after_cut_a_clip_on_the_video_back_face()
    {
        var presets = BuiltInData.CreatePresets();
        var group = presets.Single(p => p.Id == "crop-video");
        Assert.True(group.IsGroup);
        Assert.Equal(CropIds[1..], group.SubPresetIds);
        Assert.True(group.SubPresetIds.Count <= Preset.MaxSubPresets);
        foreach (var id in CropIds[1..])
        {
            var crop = presets.Single(p => p.Id == id);
            Assert.True(crop.CropsVideo, id);
            Assert.Equal(BuiltInData.SameAsSource, crop.TargetFormat);
            Assert.Equal("{name}_cropped", crop.Output.FileNamePattern);
        }
        Assert.Equal(VideoCrop.Auto, presets.Single(p => p.Id == "crop-video-auto").Media.CropFrame);
        Assert.Equal("21:9", presets.Single(p => p.Id == "crop-video-21-9").Media.CropFrame);

        var back = BuiltInData.CreateProfiles().Single(p => p.Id == "video").BackPresetIds!;
        Assert.Equal(8, back.Count);
        Assert.Equal(["tool-clip", "crop-video"], back[..2]);
        // "Extract audio" gave its slot: still in the library.
        Assert.DoesNotContain("extract-audio", back);
        Assert.Single(presets, p => p.Id == "extract-audio");
    }

    [Fact]
    public void Schema_59_adds_the_presets_to_the_library_and_no_slot()
    {
        var presets = BuiltInData.CreatePresets().Where(p => !CropIds.Contains(p.Id)).ToList();
        var profiles = BuiltInData.CreateProfiles();
        var video = profiles.Single(p => p.Id == "video");
        video.BackPresetIds = [.. video.BackPresetIds!.Where(id => id != "crop-video")];
        var front = video.PresetIds.ToList();
        var back = video.BackPresetIds.ToList();

        Assert.True(SettingsMigrations.ApplyToLibrary(58, presets, profiles));

        // After "Cut a clip", as in a new library.
        var clip = presets.FindIndex(p => p.Id == "tool-clip");
        Assert.Equal(CropIds[1..].Append("crop-video"), presets.Skip(clip + 1).Take(CropIds.Length).Select(p => p.Id));
        Assert.Equal(front, video.PresetIds);
        Assert.Equal(back, video.BackPresetIds);
        Assert.All(profiles, p => Assert.DoesNotContain("crop-video", p.PresetIds.Concat(p.BackPresetIds ?? [])));
        Assert.False(SettingsMigrations.ApplyToLibrary(AppSettings.CurrentSchemaVersion, presets, profiles));
    }

    [Fact]
    public void Schema_59_puts_the_option_back_when_an_older_filee_dropped_it()
    {
        var presets = BuiltInData.CreatePresets();
        var stripped = presets.Single(p => p.Id == "crop-video-4-3");
        stripped.Media.CropFrame = "";
        stripped.Name = "Old TV";

        Assert.True(SettingsMigrations.ApplyToLibrary(58, presets, BuiltInData.CreateProfiles()));

        Assert.Equal("4:3", stripped.Media.CropFrame);
        Assert.Equal("Old TV", stripped.Name);
        Assert.Single(presets, p => p.Id == "crop-video-4-3");
    }
}
