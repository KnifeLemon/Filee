// "Video Captioning" with Whisper: Whisper's caption edges start a route only when the preset asks for them (Caption = true).

using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Tests;

public class CaptionRouteTests
{
    private static readonly ConversionEdge[] CaptionEdges =
    [
        new("mp4", "mp4", Caption: true),
        new("mkv", "mp4", Caption: true),
        new("mov", "mp4", Caption: true),
        new("webm", "mp4", Caption: true),
    ];

    private static string Steps(Route? route) => string.Join(", ", route!.Steps.Select(s => $"{s.Converter.Id}:{s.From}→{s.To}"));

    private static RoutePlanner Planner() => new([
        new FakeConverter("whisper", CaptionEdges),
        new FakeConverter("ffmpeg", new ConversionEdge("mp4", "mp4"), new ConversionEdge("mov", "mp4"), new ConversionEdge("mkv", "mp4")),
    ]);

    [Fact]
    public void Caption_edges_are_never_part_of_an_ordinary_route()
    {
        var planner = Planner();

        Assert.Equal("ffmpeg:mp4→mp4", Steps(planner.Plan("mp4", "mp4")));
        Assert.Equal("ffmpeg:mov→mp4", Steps(planner.Plan("mov", "mp4")));
    }

    [Fact]
    public void Asking_for_caption_routes_to_whisper()
    {
        var planner = Planner();

        Assert.Equal("whisper:mp4→mp4", Steps(planner.Plan("mp4", "mp4", caption: true)));
        Assert.Equal("whisper:mov→mp4", Steps(planner.Plan("mov", "mp4", caption: true)));
        Assert.Equal("whisper:webm→mp4", Steps(planner.Plan("webm", "mp4", caption: true)));
        Assert.True(planner.CanConvert("mp4", "mp4", caption: true));
    }

    [Fact]
    public void Without_whisper_there_is_no_caption_route()
    {
        var planner = new RoutePlanner([new FakeConverter("ffmpeg", new ConversionEdge("mp4", "mp4"))]);

        Assert.Null(planner.Plan("mp4", "mp4", caption: true));
        Assert.False(planner.CanConvert("mp4", "mp4", caption: true));
    }

    [Fact]
    public void BuiltIn_caption_preset_has_caption_flag_and_output_pattern()
    {
        var preset = BuiltInData.CreatePresets().First(p => p.Id == "caption-video");

        Assert.Equal("mp4", preset.TargetFormat);
        Assert.True(preset.Media.Caption);
        Assert.True(preset.CaptionsVideo("mp4"));
        Assert.False(preset.CaptionsVideo("png"));
        Assert.Equal("{name}_captioned", preset.Output.FileNamePattern);
        Assert.Equal("preset.caption_video", preset.NameKey);
    }

    [Fact]
    public void Schema_55_library_gets_caption_preset_on_video_donut()
    {
        var presets = BuiltInData.CreatePresets().Where(p => p.Id != "caption-video").ToList();
        var profiles = BuiltInData.CreateProfiles();
        var video = profiles.Single(p => p.Id == "video");
        video.PresetIds.Remove("caption-video");

        Assert.True(SettingsMigrations.ApplyToLibrary(55, presets, profiles));

        Assert.Contains(presets, p => p.Id == "caption-video");
        Assert.Contains("caption-video", video.PresetIds);
    }
}
