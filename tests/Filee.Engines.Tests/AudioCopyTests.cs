// "Same format as the audio" (#1490): a video's sound copied out as it is. The codec → format choice and the FFmpeg
// arguments need no FFmpeg; the real copies run only when Filee's FFmpeg is installed, on films FFmpeg makes itself.

using System.Text.Json;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;
using Filee.Engines.Media;

namespace Filee.Engines.Tests;

[Collection(nameof(FfmpegCollection))]
public class AudioCopyTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    [Theory]
    [InlineData("aac", "m4a")]
    [InlineData("alac", "m4a")]
    [InlineData("mp3", "mp3")]
    [InlineData("flac", "flac")]
    [InlineData("opus", "opus")]
    [InlineData("vorbis", "ogg")]
    [InlineData("pcm_s16le", "wav")]
    [InlineData("pcm_s24le", "wav")]
    [InlineData("pcm_s16be", "mka")] // WAV holds little-endian PCM only
    [InlineData("ac3", "mka")]
    [InlineData("eac3", "mka")]
    [InlineData("dts", "mka")]
    [InlineData("truehd", "mka")]
    [InlineData(null, "mka")]
    public void Each_codec_is_copied_into_a_format_that_holds_it(string? codec, string target)
    {
        Assert.Equal(target, MediaEncoding.CopyTargetFor(codec));
        Assert.True(MediaEncoding.AudioTargets.ContainsKey(target));
    }

    [Fact]
    public void The_track_is_copied_without_pictures_subtitles_or_data()
    {
        var args = MediaEncoding.CopyAudio("in.mkv", "out.mka", "mka", new AudioStreamInfo(6, 48000, "dts", 2));
        Assert.Equal("0:a:2", args[args.IndexOf("-map") + 1]);
        Assert.Single(args, a => a == "-map");
        Assert.Equal("copy", args[args.IndexOf("-c:a") + 1]);
        Assert.Contains("-vn", args);
        Assert.Contains("-sn", args);
        Assert.Contains("-dn", args);
        Assert.Equal("matroska", args[args.IndexOf("-f") + 1]);
        Assert.Equal("out.mka", args[^1]);
        // Over 4 GB a WAV becomes RF64 instead of breaking.
        var wav = MediaEncoding.CopyAudio("in.mov", "out.wav", "wav", new AudioStreamInfo(2, 48000, "pcm_s24le"));
        Assert.Equal("auto", wav[wav.IndexOf("-rf64") + 1]);
    }

    [Fact]
    public void The_default_track_is_copied_else_the_first()
    {
        const string marked = """
            { "streams": [
              { "index": 0, "codec_type": "video", "codec_name": "h264" },
              { "index": 1, "codec_type": "audio", "codec_name": "aac", "channels": 2, "disposition": { "default": 0 } },
              { "index": 2, "codec_type": "audio", "codec_name": "dts", "channels": 6, "disposition": { "default": 1 } }
            ] }
            """;
        var copy = MediaInfo.Parse(marked).CopyAudio;
        Assert.Equal(("dts", 1), (copy?.Codec, copy?.Ordinal));
        // An undecodable track is still copied: FFmpeg doesn't decode it.
        Assert.Equal(1, MediaInfo.Parse(marked, new HashSet<string> { "h264", "aac" }).CopyAudio?.Ordinal);

        var unmarked = MediaInfo.Parse(marked.Replace("\"default\": 1", "\"default\": 0", StringComparison.Ordinal)).CopyAudio;
        Assert.Equal(0, unmarked?.Ordinal);
        Assert.Null(MediaInfo.Parse("""{ "streams": [{ "codec_type": "video", "codec_name": "h264" }] }""").CopyAudio);
    }

    [Theory]
    [InlineData("libopus", "opus", "opus")]
    [InlineData("ac3", "ac3", "mka")]
    [InlineData("pcm_s24le", "pcm_s24le", "wav")]
    public async Task A_films_sound_comes_out_as_it_is(string encoder, string codec, string extension)
    {
        SkipUnlessInstalled();
        var film = Path.Combine(fx.NewFolder(), "film.mkv");
        await RunAsync(Tools!.Value.Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=10:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2",
            "-map", "0:v", "-map", "1:a", "-c:v", "mpeg4", "-c:a", encoder, "-f", "matroska", film]);

        var output = await ConvertAsync(film);
        Assert.Equal("." + extension, Path.GetExtension(output));
        var streams = await StreamsAsync(output);
        Assert.Equal([("audio", codec)], streams);
        Assert.Equal(await PacketHashAsync(film), await PacketHashAsync(output));
    }

    [Fact]
    public async Task The_default_track_of_several_is_taken()
    {
        SkipUnlessInstalled();
        var film = Path.Combine(fx.NewFolder(), "film.mkv");
        await RunAsync(Tools!.Value.Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=10:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=2",
            "-map", "0:v", "-map", "1:a", "-map", "2:a", "-c:v", "mpeg4", "-c:a:0", "aac", "-c:a:1", "flac",
            "-disposition:a:0", "0", "-disposition:a:1", "default", "-f", "matroska", film]);

        var output = await ConvertAsync(film);
        Assert.Equal(".flac", Path.GetExtension(output));
        Assert.Equal([("audio", "flac")], await StreamsAsync(output));
    }

    [Fact]
    public async Task A_film_without_sound_says_so()
    {
        SkipUnlessInstalled();
        var film = Path.Combine(fx.NewFolder(), "silent.mkv");
        await RunAsync(Tools!.Value.Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=10:duration=1", "-c:v", "mpeg4", "-f", "matroska", film]);

        var job = await fx.ConvertAsync([film], new Preset { TargetFormat = BuiltInData.SameAsAudio });
        var file = Assert.Single(job.Files);
        Assert.Equal(FileState.Failed, file.State);
        Assert.Equal("error.no_audio", file.ErrorKey);
    }

    private static readonly (string Ffmpeg, string Ffprobe)? Tools = FfmpegConverter.Locate();

    private static void SkipUnlessInstalled() =>
        Assert.SkipUnless(Tools is not null, "FFmpeg is not installed (pwsh build/fetch-engines.ps1 -Only ffmpeg)");

    private async Task<string> ConvertAsync(string input)
    {
        var job = await fx.ConvertAsync([input], new Preset { TargetFormat = BuiltInData.SameAsAudio });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        return Assert.Single(job.Outputs);
    }

    private static async Task RunAsync(string exe, IEnumerable<string> args)
    {
        var result = await ProcessRunner.RunAsync(exe, args, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.StandardError);
    }

    private static async Task<List<(string Type, string Codec)>> StreamsAsync(string path)
    {
        var result = await ProcessRunner.RunAsync(Tools!.Value.Ffprobe,
            ["-v", "error", "-show_entries", "stream=codec_type,codec_name", "-of", "json", path], TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.StandardError);
        using var json = JsonDocument.Parse(result.StandardOutput);
        return [.. json.RootElement.GetProperty("streams").EnumerateArray()
            .Select(s => (s.GetProperty("codec_type").GetString()!, s.GetProperty("codec_name").GetString()!))];
    }

    /// <summary>A hash of the first audio track's packets: equal only when the sound was copied, not encoded again.</summary>
    private static async Task<string> PacketHashAsync(string path)
    {
        var result = await ProcessRunner.RunAsync(Tools!.Value.Ffmpeg,
            ["-hide_banner", "-nostdin", "-v", "error", "-i", path, "-map", "0:a:0", "-c", "copy", "-f", "hash", "-hash", "md5", "-"],
            TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result.StandardOutput.Trim();
    }
}
