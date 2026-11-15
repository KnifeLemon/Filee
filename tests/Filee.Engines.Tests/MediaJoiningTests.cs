// Command lines of the everyday media tools (no FFmpeg needed): videos and audio files joined into one, pictures as a
// slideshow, a video's sound copied into M4A, and a copy of a file without its tags.

using Filee.Core.Presets;
using Filee.Engines.Media;

namespace Filee.Engines.Tests;

public class MediaJoiningTests
{
    private static readonly VideoStreamInfo PhoneVideo = new(1920, 1080, 1, 0, 30, "h264", "yuv420p", HeaderHash: "MD5:1");

    private static MediaInfo Clip(VideoStreamInfo? video = null, AudioStreamInfo? audio = null, double seconds = 10) =>
        new(TimeSpan.FromSeconds(seconds), video, audio, null);

    private static string After(IReadOnlyList<string> args, string option) => args[args.ToList().IndexOf(option) + 1];

    [Fact]
    public void Separate_m4s_streams_are_identified_in_either_order()
    {
        var video = ("picture.m4s", Clip(PhoneVideo));
        var audio = ("sound.M4S", Clip(audio: new AudioStreamInfo(2, 48000, "aac", 1)));
        foreach (var clips in new[] { new[] { video, audio }, new[] { audio, video } })
        {
            var pair = MediaJoining.M4sPair(clips)!.Value;
            Assert.Equal(video.Item1, pair.Video.Path);
            var args = MediaJoining.MuxM4sPair(pair.Video, pair.Audio, "out.mp4");
            Assert.Contains("0:V:0", args);
            Assert.Contains("1:a:1", args);
            Assert.Equal("copy", After(args, "-c"));
        }
        Assert.Null(MediaJoining.M4sPair([video, ("sound.mp4", audio.Item2)]));
        Assert.Null(MediaJoining.M4sPair([video, video]));
        Assert.Null(MediaJoining.M4sPair([video]));
    }

    [Fact]
    public void Clips_from_one_phone_are_copied()
    {
        var clip = Clip(PhoneVideo, new AudioStreamInfo(2, 48000, "aac"));
        Assert.True(MediaJoining.CanCopyVideos([clip, clip], new MediaOptions()));

        var args = MediaJoining.CopyVideos("clips.txt", "out.mp4", clip);
        Assert.Equal("concat", After(args, "-f"));
        Assert.Equal("copy", After(args, "-c"));
        Assert.Contains("0:a:0", args);
    }

    [Fact]
    public void Clips_of_another_size_codec_or_sound_are_encoded()
    {
        var clip = Clip(PhoneVideo, new AudioStreamInfo(2, 48000, "aac"));
        var options = new MediaOptions();
        Assert.False(MediaJoining.CanCopyVideos([clip, clip with { Video = PhoneVideo with { Width = 1280, Height = 720 } }], options));
        Assert.False(MediaJoining.CanCopyVideos([clip, clip with { Video = PhoneVideo with { Codec = "hevc" } }], options));
        Assert.False(MediaJoining.CanCopyVideos([clip, clip with { Audio = new AudioStreamInfo(2, 44100, "aac") }], options));
        Assert.False(MediaJoining.CanCopyVideos([clip, clip with { Audio = null }], options));
        Assert.False(MediaJoining.CanCopyVideos([clip, clip], new MediaOptions { Quality = MediaQuality.Small }));
        // Same size and codec from another encoder (other parameter sets), another frame rate, or no setup data to compare.
        Assert.False(MediaJoining.CanCopyVideos([clip, clip with { Video = PhoneVideo with { HeaderHash = "MD5:2" } }], options));
        Assert.False(MediaJoining.CanCopyVideos([clip, clip with { Video = PhoneVideo with { FrameRate = 60 } }], options));
        var unknown = clip with { Video = PhoneVideo with { HeaderHash = null } };
        Assert.False(MediaJoining.CanCopyVideos([unknown, unknown], options));
    }

    [Fact]
    public void Clips_whose_chosen_sound_is_another_track_are_encoded()
    {
        var first = Clip(PhoneVideo, new AudioStreamInfo(2, 48000, "aac", 1));
        var second = Clip(PhoneVideo, new AudioStreamInfo(2, 48000, "aac", 0));
        Assert.False(MediaJoining.CanCopyVideos([first, second], new MediaOptions()));
        Assert.True(MediaJoining.CanCopyVideos([first, first], new MediaOptions()));
    }

    [Fact]
    public void Joined_files_take_the_sound_track_the_probe_chose()
    {
        var clip = Clip(PhoneVideo, new AudioStreamInfo(2, 48000, "aac", 1));
        Assert.Contains("0:a:1", MediaJoining.CopyVideos("clips.txt", "out.mp4", clip));
        var video = MediaJoining.EncodeVideos([("a.mov", clip), ("b.mp4", Clip(PhoneVideo, new AudioStreamInfo(2, 48000, "aac")))],
            "out.mp4", new MediaOptions(), "libx264", "work");
        Assert.Contains("[0:a:1]", After(video, "-filter_complex"));
        Assert.Contains("[1:a:0]", After(video, "-filter_complex"));
        var audio = MediaJoining.EncodeAudio([("a.mp3", Clip(audio: new AudioStreamInfo(2, 48000, "mp3"))),
            ("b.qta", Clip(audio: new AudioStreamInfo(2, 48000, "aac", 2)))], "out.m4a", new MediaOptions(), "work");
        Assert.Contains("[0:a:0]", After(audio, "-filter_complex"));
        Assert.Contains("[1:a:2]", After(audio, "-filter_complex"));
    }

    [Fact]
    public void Encoded_clips_fit_the_first_ones_frame_and_a_silent_one_gets_silence()
    {
        var portrait = PhoneVideo with { Rotation = -90 };
        var withSound = Clip(portrait, new AudioStreamInfo(2, 44100, "aac"));
        var silent = Clip(PhoneVideo, seconds: 4.5);

        var args = MediaJoining.EncodeVideos([("a.mov", withSound), ("b.mp4", silent)], "out.mp4", new MediaOptions(), "libx264", "work");

        var graph = After(args, "-filter_complex");
        Assert.Contains("[0:V:0]scale=1080:1920:force_original_aspect_ratio=decrease", graph); // the first clip stands upright
        Assert.Contains("pad=1080:1920", graph);
        Assert.Contains("aevalsrc=0:c=stereo:s=48000:d=4.5", graph);
        Assert.Contains("concat=n=2:v=1:a=1[v][a]", graph);
        Assert.Equal("libx264", After(args, "-c:v"));
        Assert.Equal("aac", After(args, "-c:a"));
        Assert.Equal("out.mp4", args[^1]);
    }

    [Fact]
    public void Clips_without_any_sound_join_without_an_audio_track()
    {
        var args = MediaJoining.EncodeVideos([("a.mp4", Clip(PhoneVideo)), ("b.mp4", Clip(PhoneVideo))], "out.mp4", new MediaOptions(),
            "libx264", "work");

        Assert.Contains("concat=n=2:v=1:a=0[v]", After(args, "-filter_complex"));
        Assert.Contains("-an", args);
    }

    // ───────────────────────── Music under videos (#1402) ─────────────────────────

    private static (string, MediaInfo) Song(string name, double seconds, int track = 0) =>
        (name, Clip(audio: new AudioStreamInfo(2, 44100, "mp3", track), seconds: seconds));

    [Fact]
    public void Songs_play_one_after_another_under_the_videos_and_stop_with_them()
    {
        var (music, prepare) = MediaJoining.PlanMusic([Song("a.mp3", 4), Song("b.flac", 5, track: 1)], 12, loop: false, "work/music.flac");
        Assert.Null(prepare);
        Assert.Equal([("a.mp3", 0), ("b.flac", 1)], music.Files);
        Assert.False(music.Repeat);
        Assert.False(music.Cut); // 9 s of songs under 12 s of video: they end on their own

        var clip = Clip(PhoneVideo, new AudioStreamInfo(2, 48000, "aac"), seconds: 6);
        var args = MediaJoining.EncodeVideos([("x.mp4", clip), ("y.mp4", clip)], "out.mp4", new MediaOptions(), "libx264", "work", music);

        var inputs = args.Select((a, i) => (a, i)).Where(p => p.a == "-i").Select(p => args[p.i + 1]).ToList();
        Assert.Equal(["x.mp4", "y.mp4", "a.mp3", "b.flac"], inputs);
        var graph = After(args, "-filter_complex");
        Assert.Contains("concat=n=2:v=1:a=1[v][va]", graph);
        Assert.Contains("[2:a:0]aresample=48000", graph);
        Assert.Contains("[3:a:1]aresample=48000", graph);
        Assert.Contains("[m0][m1]concat=n=2:v=0:a=1,atrim=duration=12,asetpts=PTS-STARTPTS,volume=0.6[m]", graph);
        Assert.DoesNotContain("afade", graph);
        Assert.Contains("[va][m]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[a]", graph);
        Assert.Equal("[a]", args[args.LastIndexOf("-map") + 1]);
        Assert.DoesNotContain("-stream_loop", args);
    }

    [Fact]
    public void Songs_longer_than_the_videos_are_cut_with_a_fade_and_play_as_recorded_over_silent_videos()
    {
        var (music, _) = MediaJoining.PlanMusic([Song("long.mp3", 300)], 8, loop: false, "work/music.flac");
        Assert.True(music.Cut);

        var args = MediaJoining.EncodeVideos([("x.mp4", Clip(PhoneVideo, seconds: 8))], "out.mp4", new MediaOptions(), "libx264", "work", music);

        var graph = After(args, "-filter_complex");
        Assert.Contains("concat=n=1:v=1:a=0[v];", graph);
        Assert.Contains("[1:a:0]aresample=48000", graph);
        // No sound to go under: no volume, and the music is the sound.
        Assert.EndsWith("[m0]atrim=duration=8,asetpts=PTS-STARTPTS,afade=t=out:st=7:d=1[a]", graph);
        Assert.DoesNotContain("-an", args);
        Assert.Equal("aac", After(args, "-c:a"));
    }

    [Fact]
    public void Looped_songs_become_one_file_that_repeats_until_the_videos_end()
    {
        var (music, prepare) = MediaJoining.PlanMusic([Song("a.mp3", 2), Song("b.mp3", 3)], 20, loop: true, "work/music.flac");

        Assert.NotNull(prepare);
        Assert.Equal("work/music.flac", prepare[^1]);
        Assert.Equal("flac", After(prepare, "-c:a"));
        Assert.Contains("[m0][m1]concat=n=2:v=0:a=1,atrim=duration=20,asetpts=PTS-STARTPTS[a]", After(prepare, "-filter_complex"));
        Assert.Equal([("work/music.flac", 0)], music.Files);
        Assert.True(music.Repeat && music.Cut);

        var clip = Clip(PhoneVideo, new AudioStreamInfo(2, 48000, "aac"), seconds: 10);
        var args = MediaJoining.CopyVideos("clips.txt", "out.mp4", clip, music, new MediaOptions(), "work");
        var loop = args.IndexOf("-stream_loop");
        Assert.Equal(["-stream_loop", "-1", "-i", "work/music.flac"], args.Skip(loop).Take(4));
        var graph = After(args, "-filter_complex");
        Assert.Contains("[0:a:0]aresample=48000", graph);
        Assert.Contains("[1:a:0]aresample=48000", graph);
        Assert.Contains("[m0]atrim=duration=20,asetpts=PTS-STARTPTS,volume=0.6,afade=t=out:st=19:d=1[m]", graph);
        // The pictures are still copied; only the sound is encoded.
        Assert.Equal("copy", After(args, "-c:v"));
        Assert.Equal("aac", After(args, "-c:a"));
        Assert.Equal(["0:V:0", "[a]"], args.Select((a, i) => (a, i)).Where(p => p.a == "-map").Select(p => args[p.i + 1]));
    }

    [Fact]
    public void Loop_music_long_enough_already_plays_once_and_a_song_without_sound_is_refused()
    {
        var (music, prepare) = MediaJoining.PlanMusic([Song("a.mp3", 30)], 20, loop: true, "work/music.flac");
        Assert.Null(prepare);
        Assert.False(music.Repeat);
        Assert.True(music.Cut);
        // A length FFmpeg can't tell: prepared to repeat, so it lasts in any case.
        Assert.NotNull(MediaJoining.PlanMusic([("a.mp3", new MediaInfo(null, null, new AudioStreamInfo(2, 44100, "mp3"), null))], 20,
            loop: true, "work/music.flac").Prepare);
        Assert.Throws<InvalidOperationException>(() => MediaJoining.PlanMusic([("a.mp3", Clip(seconds: 3))], 20, loop: false, "m.flac"));
    }

    [Fact]
    public void A_frame_never_gets_taller_than_the_preset_allows()
    {
        var uhd = PhoneVideo with { Width = 3840, Height = 2160 };
        Assert.Equal((1920, 1080), MediaJoining.JoinFrame(uhd, 1080));
        Assert.Equal((3840, 2160), MediaJoining.JoinFrame(uhd, 0));
    }

    [Fact]
    public void Audio_is_decoded_and_joined_without_gaps()
    {
        var song = Clip(audio: new AudioStreamInfo(2, 44100, "mp3"));
        var memo = Clip(audio: new AudioStreamInfo(1, 16000, "aac"));

        var args = MediaJoining.EncodeAudio([("a.mp3", song), ("b.m4a", memo)], "out.m4a", new MediaOptions(), "work");

        var graph = After(args, "-filter_complex");
        Assert.Contains("[0:a:0]aresample=48000", graph);
        Assert.Contains("channel_layouts=stereo", graph);
        Assert.Contains("concat=n=2:v=0:a=1[a]", graph);
        Assert.Equal("aac", After(args, "-c:a"));
        Assert.Equal("192k", After(args, "-b:a"));
        Assert.Equal("ipod", After(args, "-f"));
    }

    [Fact]
    public void Mono_recordings_stay_mono_and_apple_lossless_is_kept()
    {
        var memo = Clip(audio: new AudioStreamInfo(1, 16000, "aac"));
        var mono = MediaJoining.EncodeAudio([("a.m4a", memo), ("b.m4a", memo)], "out.m4a", new MediaOptions(), "work");
        Assert.Contains("channel_layouts=mono", After(mono, "-filter_complex"));
        Assert.Equal("96k", After(mono, "-b:a"));

        var alac = MediaJoining.EncodeAudio([("a.m4a", memo)], "out.m4a", new MediaOptions { AudioCodec = AudioCodec.Alac }, "work");
        Assert.Equal("alac", After(alac, "-c:a"));
    }

    [Fact]
    public void Slides_fade_into_each_other_at_the_right_times()
    {
        var options = new MediaOptions { Slideshow = true, SlideSeconds = 3 };

        var args = MediaJoining.Slideshow(["1.jpg", "2.jpg", "3.jpg"], "out.mp4", options, "libx264", "work");

        var graph = After(args, "-filter_complex");
        Assert.Contains("tpad=stop_mode=clone:stop_duration=3,trim=duration=3", graph);
        Assert.Contains("[s0][s1]xfade=transition=fade:duration=1:offset=2[x1]", graph);
        Assert.Contains("[x1][s2]xfade=transition=fade:duration=1:offset=4[out]", graph);
        Assert.Equal(TimeSpan.FromSeconds(7), MediaJoining.SlideshowDuration(3, options));
        Assert.Contains("-an", args);
    }

    [Fact]
    public void Without_crossfades_the_slides_are_cut()
    {
        var options = new MediaOptions { Slideshow = true, SlideSeconds = 2, Crossfade = false };

        var graph = After(MediaJoining.Slideshow(["1.jpg", "2.jpg"], "out.mp4", options, "libx264", "work"), "-filter_complex");

        Assert.Contains("[s0][s1]concat=n=2:v=1:a=0[out]", graph);
        Assert.DoesNotContain("xfade", graph);
        Assert.Equal(TimeSpan.FromSeconds(4), MediaJoining.SlideshowDuration(2, options));
    }

    [Fact]
    public void A_picture_is_fitted_into_the_1080p_frame()
    {
        var args = MediaJoining.PrepareSlide("photo.png", "slide.jpg", 1920, 1080);

        Assert.Contains("scale=1920:1080:force_original_aspect_ratio=decrease:force_divisible_by=2:flags=lanczos,pad=1920:1080",
            After(args, "-vf"));
        Assert.Equal("mjpeg", After(args, "-c:v"));
    }

    [Fact]
    public void A_long_graph_goes_into_a_file()
    {
        var work = Path.Combine(Path.GetTempPath(), "filee-tests", Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var slides = Enumerable.Range(1, 100).Select(i => $"{i}.jpg").ToList();
            var args = MediaJoining.Slideshow(slides, "out.mp4", new MediaOptions { Slideshow = true }, "libx264", work);

            Assert.DoesNotContain("-filter_complex", args);
            var file = After(args, "-/filter_complex");
            Assert.Contains("[x98][s99]xfade", File.ReadAllText(file));
        }
        finally
        {
            if (Directory.Exists(work))
                Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void Concat_lists_quote_every_path()
    {
        var list = MediaJoining.ConcatList(["it's.mp4"]);
        Assert.StartsWith("file '", list);
        Assert.EndsWith(@"it'\''s.mp4'" + "\n", list);
    }

    [Fact]
    public void Extract_audio_copies_aac_into_m4a_and_encodes_what_it_cannot_copy()
    {
        var copy = new MediaOptions { CopyAudioWhenPossible = true };
        var aac = new AudioStreamInfo(2, 48000, "aac");
        Assert.True(MediaEncoding.CanCopyIntoAudio("m4a", aac, copy));
        Assert.False(MediaEncoding.CanCopyIntoAudio("m4a", new AudioStreamInfo(2, 48000, "opus"), copy));
        Assert.False(MediaEncoding.CanCopyIntoAudio("m4a", aac, new MediaOptions())); // only when the preset asks
        Assert.False(MediaEncoding.CanCopyIntoAudio("m4a", aac, new MediaOptions { CopyAudioWhenPossible = true, AudioBitrateKbps = 96 }));
        Assert.False(MediaEncoding.CanCopyIntoAudio("m4a", aac, new MediaOptions { CopyAudioWhenPossible = true, AudioCodec = AudioCodec.Alac }));

        var phone = new MediaInfo(TimeSpan.FromSeconds(10), PhoneVideo, aac, null);
        var args = MediaEncoding.Plan("in.mov", "out.m4a", "m4a", copy, phone, "work").Single().Arguments;
        Assert.Equal("copy", After(args, "-c:a"));
    }

    [Fact]
    public void Remove_metadata_copies_the_streams_without_tags_chapters_or_data_tracks()
    {
        var phone = new MediaInfo(TimeSpan.FromSeconds(10), PhoneVideo with { Codec = "hevc" }, new AudioStreamInfo(2, 48000, "aac"), null);

        var args = MediaEncoding.WithoutMetadata("in.mov", "out.mov", "mov", phone);

        Assert.Equal("copy", After(args, "-c"));
        Assert.Equal("-1", After(args, "-map_metadata"));
        Assert.Equal("-1", After(args, "-map_metadata:s"));
        Assert.Equal("-1", After(args, "-map_chapters"));
        Assert.Contains("0:V?", args);
        Assert.DoesNotContain("0", args.Where((a, i) => i > 0 && args[i - 1] == "-map")); // never every stream (data tracks)
        Assert.Equal("hvc1", After(args, "-tag:v"));
        Assert.Equal("mov", After(args, "-f"));
        Assert.DoesNotContain("0:t", args); // FFmpeg refuses an attachment specifier that matches nothing

        var episode = new MediaInfo(TimeSpan.FromSeconds(10), PhoneVideo, null, null, HasAttachments: true);
        var mkv = MediaEncoding.WithoutMetadata("in.mkv", "out.mkv", "mkv", episode);
        Assert.Contains("0:t", mkv); // subtitle fonts stay, with their file name and type
        Assert.Equal("0:s:t", After(mkv, "-map_metadata:s:t"));

        var song = new MediaInfo(TimeSpan.FromSeconds(10), null, new AudioStreamInfo(2, 44100, "mp3"), 1);
        var audio = MediaEncoding.WithoutMetadata("in.mp3", "out.mp3", "mp3", song);
        Assert.Contains("0:v?", audio); // the album art stays
        Assert.Equal("mp3", After(audio, "-f"));
    }
}
