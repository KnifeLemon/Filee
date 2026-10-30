// "AI Captioning" with Whisper: Whisper's caption edges start a route only when the preset asks for them (Caption = true).
// Supports both video and audio files directly to SRT subtitles.

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class CaptionRouteTests
{
    private static readonly ConversionEdge[] CaptionEdges =
    [
        new("mp4", "srt", Caption: true),
        new("mkv", "srt", Caption: true),
        new("mov", "srt", Caption: true),
        new("webm", "srt", Caption: true),
        new("mp3", "srt", Caption: true),
        new("wav", "srt", Caption: true),
        new("m4a", "srt", Caption: true),
    ];

    private static string Steps(Route? route) => string.Join(", ", route!.Steps.Select(s => $"{s.Converter.Id}:{s.From}→{s.To}"));

    private static RoutePlanner Planner() => new([
        new FakeConverter("whisper", CaptionEdges),
        new FakeConverter("ffmpeg", new ConversionEdge("mp4", "mp4"), new ConversionEdge("mov", "mp4"), new ConversionEdge("mp3", "wav")),
    ]);

    [Fact]
    public void Caption_edges_are_never_part_of_an_ordinary_route()
    {
        var planner = Planner();

        Assert.Equal("ffmpeg:mp4→mp4", Steps(planner.Plan("mp4", "mp4")));
        Assert.Equal("ffmpeg:mp3→wav", Steps(planner.Plan("mp3", "wav")));
    }

    [Fact]
    public void Asking_for_caption_routes_to_whisper_for_both_video_and_audio()
    {
        var planner = Planner();

        // Video -> SRT
        Assert.Equal("whisper:mp4→srt", Steps(planner.Plan("mp4", "srt", caption: true)));
        Assert.Equal("whisper:mov→srt", Steps(planner.Plan("mov", "srt", caption: true)));
        Assert.Equal("whisper:webm→srt", Steps(planner.Plan("webm", "srt", caption: true)));
        Assert.True(planner.CanConvert("mp4", "srt", caption: true));

        // Audio -> SRT
        Assert.Equal("whisper:mp3→srt", Steps(planner.Plan("mp3", "srt", caption: true)));
        Assert.Equal("whisper:wav→srt", Steps(planner.Plan("wav", "srt", caption: true)));
        Assert.Equal("whisper:m4a→srt", Steps(planner.Plan("m4a", "srt", caption: true)));
        Assert.True(planner.CanConvert("mp3", "srt", caption: true));
    }

    [Fact]
    public void Without_whisper_there_is_no_caption_route()
    {
        var planner = new RoutePlanner([new FakeConverter("ffmpeg", new ConversionEdge("mp4", "mp4"))]);

        Assert.Null(planner.Plan("mp4", "srt", caption: true));
        Assert.False(planner.CanConvert("mp4", "srt", caption: true));
        Assert.Null(planner.Plan("mp3", "srt", caption: true));
        Assert.False(planner.CanConvert("mp3", "srt", caption: true));
    }

    [Fact]
    public void BuiltIn_caption_preset_has_caption_flag_and_targets_srt()
    {
        var preset = BuiltInData.CreatePresets().First(p => p.Id == "caption-video");

        Assert.Equal("srt", preset.TargetFormat);
        Assert.True(preset.Media.Caption);
        Assert.True(preset.CaptionsVideo("mp4"));
        Assert.True(preset.CaptionsVideo("mp3"));
        Assert.False(preset.CaptionsVideo("png"));
        Assert.Equal("{name}", preset.Output.FileNamePattern);
        Assert.Equal("preset.caption_video", preset.NameKey);
    }

    [Fact]
    public void Schema_56_library_gets_caption_preset_on_both_video_and_audio_donuts()
    {
        var presets = BuiltInData.CreatePresets().Where(p => p.Id != "caption-video").ToList();
        var profiles = BuiltInData.CreateProfiles();
        var video = profiles.Single(p => p.Id == "video");
        video.PresetIds.Remove("caption-video");
        var audio = profiles.Single(p => p.Id == "audio");
        audio.PresetIds.Remove("caption-video");

        Assert.True(SettingsMigrations.ApplyToLibrary(56, presets, profiles));
        Assert.False(SettingsMigrations.ApplyToLibrary(56, presets, profiles)); // once only

        Assert.Contains(presets, p => p.Id == "caption-video");
        Assert.Contains("caption-video", video.PresetIds);
        Assert.Contains("caption-video", audio.PresetIds);
    }

    [Fact]
    public void Existing_caption_preset_without_flag_learns_caption_flag_in_migration()
    {
        var presets = BuiltInData.CreatePresets().ToList();
        var caption = presets.First(p => p.Id == "caption-video");
        caption.Media.Caption = false;
        var profiles = BuiltInData.CreateProfiles();

        Assert.True(SettingsMigrations.ApplyToLibrary(56, presets, profiles));
        Assert.True(caption.Media.Caption);
    }
}
