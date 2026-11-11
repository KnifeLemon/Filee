// iPhone ringtones (#1291): an M4R is the part of the sound the preset asks for (at most 40 s), encoded as AAC in the
// M4A container; every other target keeps the whole sound.

using Filee.Core.Presets;
using Filee.Engines.Media;

namespace Filee.Engines.Tests;

public class RingtoneTests
{
    private static readonly MediaInfo Song = new(TimeSpan.FromMinutes(3), null, new AudioStreamInfo(2, 44100, "mp3"), null);

    private static List<string> Plan(string target, MediaOptions options, MediaInfo? info = null) =>
        [.. MediaEncoding.Plan("in.mp3", "out." + target, target, options, info ?? Song, "work").Single().Arguments];

    private static string After(List<string> args, string option) => args[args.IndexOf(option) + 1];

    [Fact]
    public void An_M4R_is_the_chosen_part_encoded_as_AAC_in_the_M4A_container()
    {
        var args = Plan("m4r", new MediaOptions { RingtoneStart = 45, RingtoneLength = 25 });

        Assert.Equal(ClipRange.Ffmpeg(45), After(args, "-ss"));
        Assert.True(args.IndexOf("-ss") < args.IndexOf("-i")); // the input is read from where the ringtone starts
        Assert.Equal(ClipRange.Ffmpeg(25), After(args, "-t"));
        Assert.True(args.IndexOf("-i") < args.IndexOf("-t"));
        Assert.Equal("aac", After(args, "-c:a"));
        Assert.Equal("ipod", After(args, "-f"));
        Assert.Equal("out.m4r", args[^1]);
        Assert.Contains("+faststart", args);
    }

    [Fact]
    public void A_ringtone_is_never_longer_than_forty_seconds_nor_longer_than_the_sound()
    {
        Assert.Equal(ClipRange.Ffmpeg(40), After(Plan("m4r", new MediaOptions { RingtoneLength = 300 }), "-t"));

        var short20 = Song with { Duration = TimeSpan.FromSeconds(20) };
        var args = Plan("m4r", new MediaOptions { RingtoneStart = 5, RingtoneLength = 30 }, short20);
        Assert.Equal(ClipRange.Ffmpeg(15), After(args, "-t"));
    }

    [Fact]
    public void The_result_says_so_when_the_ringtone_is_only_a_part_of_the_sound()
    {
        // A 3 minute song from 0:00 for 30 s, the common case.
        Assert.True(MediaEncoding.RingtoneCutShort("m4r", new MediaOptions(), Song));
        Assert.True(MediaEncoding.RingtoneCutShort("m4r", new MediaOptions { RingtoneStart = 170, RingtoneLength = 5 }, Song));

        // All of a short sound: nothing was cut, even when more was asked for.
        var short25 = Song with { Duration = TimeSpan.FromSeconds(25) };
        Assert.False(MediaEncoding.RingtoneCutShort("m4r", new MediaOptions { RingtoneLength = 60 }, short25));
        Assert.False(MediaEncoding.RingtoneCutShort("m4r", new MediaOptions { RingtoneLength = 25 }, short25));
        Assert.True(MediaEncoding.RingtoneCutShort("m4r", new MediaOptions { RingtoneLength = 10 }, short25));

        // Not for other targets, and with an unknown length only for one asked to be longer than 40 s.
        Assert.False(MediaEncoding.RingtoneCutShort("m4a", new MediaOptions(), Song));
        var unknown = Song with { Duration = null };
        Assert.False(MediaEncoding.RingtoneCutShort("m4r", new MediaOptions(), unknown));
        Assert.True(MediaEncoding.RingtoneCutShort("m4r", new MediaOptions { RingtoneLength = 90 }, unknown));
    }

    [Fact]
    public void An_AAC_sound_is_still_encoded_so_the_cut_is_exact()
    {
        var aac = Song with { Audio = new AudioStreamInfo(2, 44100, "aac") };

        Assert.Equal("aac", After(Plan("m4r", new MediaOptions { CopyAudioWhenPossible = true }, aac), "-c:a"));
    }

    [Fact]
    public void Other_targets_keep_the_whole_sound()
    {
        var args = Plan("m4a", new MediaOptions { RingtoneStart = 45, RingtoneLength = 25 });

        Assert.DoesNotContain("-ss", args);
        Assert.DoesNotContain("-t", args);
        Assert.Null(MediaEncoding.RingtoneRange("m4a", new MediaOptions(), Song));
        Assert.Equal((0.0, 30.0), MediaEncoding.RingtoneRange("m4r", new MediaOptions(), Song));
    }

    [Fact]
    public void Every_audio_and_video_source_can_make_one()
    {
        var edges = new FfmpegConverter().Edges;

        Assert.Contains(edges, e => e.From == "mp3" && e.To == "m4r");
        Assert.Contains(edges, e => e.From == "flac" && e.To == "m4r");
        Assert.Contains(edges, e => e.From == "wav" && e.To == "m4r");
        Assert.Contains(edges, e => e.From == "mp4" && e.To == "m4r");
    }
}
