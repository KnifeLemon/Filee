// Which container and codecs Filee writes for every video and audio format, and the FFmpeg command lines for them.
// Pure (no processes), so the choices are unit tested without FFmpeg. Every encoder and muxer used here was checked
// against the pinned FFmpeg build (engines.json: `ffmpeg -encoders` / `ffmpeg -muxers`).

using System.Globalization;
using Filee.Core.Presets;

namespace Filee.Engines.Media;

/// <summary>How one target format is written.</summary>
/// <param name="Muxer">FFmpeg container name (<c>-f</c>), always given explicitly: some extensions are ambiguous.</param>
/// <param name="VideoCodec">FFmpeg video encoder, null for audio-only formats.</param>
/// <param name="AudioCodec">FFmpeg audio encoder.</param>
internal sealed record MediaTarget(string Muxer, string? VideoCodec, string AudioCodec);

/// <summary>One run of ffmpeg.</summary>
/// <param name="Arguments">Command line after the executable.</param>
/// <param name="Weight">Share of the whole conversion's progress (the passes of one conversion add up to 1).</param>
internal sealed record FfmpegPass(IReadOnlyList<string> Arguments, double Weight);

/// <summary>Encoding choices and command lines.</summary>
internal static class MediaEncoding
{
    /// <summary>
    /// Video formats Filee writes. H.264 + AAC wherever the container allows it: it plays on every browser, phone,
    /// TV and editor. The other containers get the codecs their players expect.
    /// </summary>
    public static IReadOnlyDictionary<string, MediaTarget> VideoTargets { get; } = new Dictionary<string, MediaTarget>
    {
        ["mp4"] = new("mp4", "libx264", "aac"),
        // Apple's M4V is MP4 (FFmpeg's "m4v" muxer would write a raw MPEG-4 part 2 stream instead).
        ["m4v"] = new("mp4", "libx264", "aac"),
        ["mov"] = new("mov", "libx264", "aac"),
        ["mkv"] = new("matroska", "libx264", "aac"),
        ["flv"] = new("flv", "libx264", "aac"),
        ["ts"] = new("mpegts", "libx264", "aac"),
        ["m2ts"] = new("mpegts", "libx264", "aac"), // + 192-byte Blu-ray packets, see ContainerArguments
        ["3gp"] = new("3gp", "libx264", "aac"),
        ["3g2"] = new("3g2", "libx264", "aac"),
        // The WebM specification allows only VP8/VP9/AV1 with Vorbis/Opus.
        ["webm"] = new("webm", "libvpx-vp9", "libopus"),
        // MPEG-4 part 2 tagged XVID + CBR MP3: what DVD players, old TVs and Windows' built-in codecs read in AVI.
        ["avi"] = new("avi", "mpeg4", "libmp3lame"),
        ["wmv"] = new("asf", "wmv2", "wmav2"),
        // MPEG-2 program stream ("vob" is FFmpeg's plain MPEG-2 PS muxer; "mpeg" would write an MPEG-1 system stream).
        ["mpg"] = new("vob", "mpeg2video", "mp2"),
        // DVD-Video compliant: 720×480 NTSC / 720×576 PAL, AC-3, DVD mux rate and navigation packets.
        ["vob"] = new("dvd", "mpeg2video", "ac3"),
        ["ogv"] = new("ogg", "libtheora", "libvorbis"),
        // DV has fixed frame sizes and rates (see DvdOrDvFilter) and 48 kHz PCM.
        ["dv"] = new("dv", "dvvideo", "pcm_s16le"),
        // MXF OP1a with long-GOP MPEG-2 (XDCAM style) and 48 kHz PCM: what broadcast and editing tools import.
        ["mxf"] = new("mxf", "mpeg2video", "pcm_s16le"),
    };

    /// <summary>Audio formats Filee writes (also the targets of audio extraction from video).</summary>
    public static IReadOnlyDictionary<string, MediaTarget> AudioTargets { get; } = new Dictionary<string, MediaTarget>
    {
        ["mp3"] = new("mp3", null, "libmp3lame"),
        // The iPod muxer writes the M4A/M4B brands iTunes and phones expect.
        ["m4a"] = new("ipod", null, "aac"),
        ["m4b"] = new("ipod", null, "aac"),
        ["aac"] = new("adts", null, "aac"),
        ["wav"] = new("wav", null, "pcm_s16le"),
        ["flac"] = new("flac", null, "flac"),
        ["ogg"] = new("ogg", null, "libvorbis"),
        ["opus"] = new("opus", null, "libopus"),
        ["wma"] = new("asf", null, "wmav2"),
        ["aiff"] = new("aiff", null, "pcm_s16be"),
        ["ac3"] = new("ac3", null, "ac3"),
        // AMR-NB (OpenCORE): 8 kHz mono speech, what phones record voice memos in.
        ["amr"] = new("amr", null, "libopencore_amrnb"),
        ["au"] = new("au", null, "pcm_s16be"),
        // CAF with big-endian linear PCM, the variant Apple's own tools write by default.
        ["caf"] = new("caf", null, "pcm_s16be"),
        ["weba"] = new("webm", null, "libopus"),
        ["mka"] = new("matroska", null, "libopus"),
        ["mp2"] = new("mp2", null, "mp2"),
        // 16-bit PCM in an extended block: exact sample rate (8-bit blocks can only approximate it).
        ["voc"] = new("voc", null, "pcm_s16le"),
    };

    /// <summary>Formats an animated GIF can be turned into.</summary>
    public static IReadOnlyList<string> GifToVideoTargets { get; } = ["mp4", "webm", "mov"];

    /// <summary>Audio targets that can carry a cover picture (album art) copied from the source.</summary>
    private static readonly HashSet<string> CoverTargets = ["mp3", "m4a", "m4b", "flac"];

    /// <summary>
    /// Frame rates MPEG-2 (and MXF, which only knows broadcast rates) can signal exactly. Other rates are
    /// converted to the nearest one by dropping or repeating frames.
    /// </summary>
    private static readonly (int Num, int Den)[] BroadcastRates =
        [(24000, 1001), (24, 1), (25, 1), (30000, 1001), (30, 1), (50, 1), (60000, 1001), (60, 1)];

    /// <summary>Container metadata that describes the source file, not the content: not copied.</summary>
    private static readonly string[] SourceOnlyTags = ["major_brand", "minor_version", "compatible_brands"];

    /// <summary>Global arguments of every run: no banner, no keyboard input, only errors on stderr, overwrite.</summary>
    internal static readonly string[] Common = ["-hide_banner", "-nostdin", "-nostats", "-v", "error", "-y"];

    /// <summary>
    /// The ffmpeg runs that convert <paramref name="input"/> into <paramref name="output"/> in format
    /// <paramref name="target"/>.
    /// </summary>
    /// <param name="workDirectory">Scratch folder (the GIF palette goes there).</param>
    /// <param name="cover">The source's cover for targets FFmpeg can't put a picture stream into (<see cref="CoverArt"/>).</param>
    /// <exception cref="InvalidOperationException">The file lacks the stream the target needs (e.g. no audio track).</exception>
    public static IReadOnlyList<FfmpegPass> Plan(string input, string output, string target, MediaOptions options, MediaInfo info, string workDirectory,
        CoverExtras? cover = null)
    {
        if (target == "gif")
            return AnimatedGif(input, output, options, info, workDirectory);
        if (target == "png")
            return [new FfmpegPass(PosterFrame(input, output, info), 1)];
        if (VideoTargets.TryGetValue(target, out var video))
            return [new FfmpegPass(Video(input, output, target, video, options, info), 1)];
        if (AudioTargets.TryGetValue(target, out var audio))
            return [new FfmpegPass(Audio(input, output, target, audio, options, info, cover), 1)];
        throw new NotSupportedException($"FFmpeg cannot write '{target}'.");
    }

    // ───────────────────────── Video ─────────────────────────

    private static List<string> Video(string input, string output, string target, MediaTarget spec, MediaOptions options, MediaInfo info)
    {
        var video = info.Video ?? throw new InvalidOperationException("The file has no video stream.");
        // An input without sound is fine: the output simply has no audio track.
        var audio = options.RemoveAudio ? null : info.Audio;

        List<string> args = [.. Common, "-i", input, "-map", "0:V:0"];
        if (audio is not null)
            args.AddRange(["-map", "0:a:0"]);

        var pal = IsPal(video.FrameRate);
        args.AddRange(["-vf", target is "vob" or "dv" ? DvdOrDvFilter(video, pal) : ScaleFilter(options.MaxHeight)]);
        args.AddRange(VideoCodecArguments(target, spec.VideoCodec!, options.Quality, video, pal));

        if (audio is null)
            args.Add("-an");
        else
            args.AddRange(AudioCodecArguments(target, spec.AudioCodec, options, audio, inVideo: true));

        args.AddRange(MetadataArguments());
        args.AddRange(ContainerArguments(target));
        // Sparse or late-starting streams (subtitles-like audio) otherwise fail with "too many packets buffered".
        args.AddRange(["-max_muxing_queue_size", "4096", "-f", spec.Muxer, "-progress", "pipe:1", output]);
        return args;
    }

    /// <summary>
    /// Scales down to <paramref name="maxHeight"/> (never up) keeping the aspect ratio, and makes both sides even,
    /// which 4:2:0 encoders require (an odd-sized GIF or phone video would otherwise fail).
    /// </summary>
    internal static string ScaleFilter(int maxHeight) => maxHeight > 0
        ? $"scale=w=-2:h='trunc(min(ih,{Math.Max(16, maxHeight)})/2)*2'"
        : "scale=w='trunc(iw/2)*2':h='trunc(ih/2)*2'";

    /// <summary>
    /// DVD and DV frames are always 720×480 (NTSC) or 720×576 (PAL), shown at 4:3 or 16:9 through the pixel aspect.
    /// The picture is fitted into whichever of the two is closer to its own shape, with black bars for the rest.
    /// </summary>
    internal static string DvdOrDvFilter(VideoStreamInfo video, bool pal)
    {
        var height = pal ? 576 : 480;
        var aspect = video.DisplayAspect;
        var wide = aspect >= 1.55; // between 4:3 (1.33) and 16:9 (1.78)
        var frameAspect = wide ? 16 / 9.0 : 4 / 3.0;
        var (w, h) = aspect > frameAspect
            ? (720, Even(height * frameAspect / aspect))
            : (Even(720 * aspect / frameAspect), height);
        return $"scale={w}:{h},pad=720:{height}:(ow-iw)/2:(oh-ih)/2:black,setdar={(wide ? "16/9" : "4/3")}";
    }

    /// <summary>PAL (25 fps) for 25/50 fps sources, NTSC (29.97 fps) for everything else.</summary>
    internal static bool IsPal(double fps) => Math.Abs(fps - 25) < 0.5 || Math.Abs(fps - 50) < 1 || Math.Abs(fps - 12.5) < 0.3;

    private static IEnumerable<string> VideoCodecArguments(string target, string codec, MediaQuality quality, VideoStreamInfo video, bool pal)
    {
        // Constant quantizer for the MPEG-style codecs: 2 is the best useful value, 4 good, 7 visibly compressed.
        var quantizer = Text(Choose(quality, 2, 4, 7));
        switch (codec)
        {
            case "libx264":
                // CRF 18 is visually lossless, 23 is x264's default, 28 is clearly smaller. Preset medium is x264's
                // default speed/size balance; yuv420p (8-bit 4:2:0) is the only format every H.264 decoder plays.
                return ["-c:v", "libx264", "-preset", "medium", "-crf", Text(Choose(quality, 18, 23, 28)), "-pix_fmt", "yuv420p"];
            case "libvpx-vp9":
                // Constant quality mode (-b:v 0 + CRF). Row multithreading and speed 4 keep VP9 usable on a desktop:
                // the slower speeds gain little size for several times the encoding time.
                return ["-c:v", "libvpx-vp9", "-b:v", "0", "-crf", Text(Choose(quality, 24, 32, 38)), "-row-mt", "1",
                        "-deadline", "good", "-cpu-used", "4", "-pix_fmt", "yuv420p"];
            case "mpeg4":
                // GOP 250 because the encoder's default of 12 bloats files. The frame rate is fixed because MPEG-4
                // part 2 cannot store the fine time bases of phone videos.
                return ["-c:v", "mpeg4", "-tag:v", "XVID", "-q:v", quantizer, "-g", "250", "-bf", "2",
                        "-pix_fmt", "yuv420p", .. FrameRate(NearestRate(video.FrameRate))];
            case "wmv2":
                return ["-c:v", "wmv2", "-q:v", quantizer, "-g", "250", "-pix_fmt", "yuv420p",
                        .. FrameRate(NearestRate(video.FrameRate))];
            case "libtheora":
                // Theora quality 0-10.
                return ["-c:v", "libtheora", "-q:v", Text(Choose(quality, 8, 6, 4)), "-pix_fmt", "yuv420p",
                        .. FrameRate(NearestRate(video.FrameRate))];
            case "mpeg2video" when target == "vob":
                // DVD-Video limits: peak 9.8 Mbit/s for video + audio, 1.8 Mbit VBV buffer, GOP of 15 (PAL) / 18 (NTSC).
                return ["-c:v", "mpeg2video", "-b:v", Text(Choose(quality, 8000, 6000, 4000)) + "k", "-maxrate", "9000k",
                        "-minrate", "0", "-bufsize", "1835k", "-g", pal ? "15" : "18", "-bf", "2", "-pix_fmt", "yuv420p",
                        "-r", pal ? "25" : "30000/1001"];
            case "mpeg2video":
                // mpg and mxf: constant quantizer, broadcast GOP of 15 and a frame rate MPEG-2 can signal.
                return ["-c:v", "mpeg2video", "-q:v", quantizer, "-g", "15", "-bf", "2", "-pix_fmt", "yuv420p",
                        "-r", BroadcastRate(video.FrameRate)];
            case "dvvideo":
                // DV is fixed at 25 Mbit/s: NTSC samples colour 4:1:1, PAL 4:2:0.
                return ["-c:v", "dvvideo", "-pix_fmt", pal ? "yuv420p" : "yuv411p", "-r", pal ? "25" : "30000/1001"];
            default:
                throw new NotSupportedException(codec);
        }
    }

    /// <summary><c>-r</c> for a rate, nothing when the source rate is unknown (FFmpeg then keeps its own guess).</summary>
    private static string[] FrameRate(string? rate) => rate is null ? [] : ["-r", rate];

    /// <summary>
    /// The source rate as a simple fraction: exact NTSC/film/PAL rates are kept, anything else (variable frame rate
    /// phone videos report averages like 29.87) is rounded to whole frames per second.
    /// </summary>
    internal static string? NearestRate(double fps)
    {
        if (fps <= 0)
            return null;
        foreach (var (num, den) in BroadcastRates)
        {
            if (Math.Abs(fps - (double)num / den) < 0.01)
                return den == 1 ? num.ToString(CultureInfo.InvariantCulture) : $"{num}/{den}";
        }
        return Math.Clamp((int)Math.Round(fps), 1, 120).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The broadcast rate closest to <paramref name="fps"/> (PAL's 25 when unknown).</summary>
    internal static string BroadcastRate(double fps)
    {
        if (fps <= 0)
            return "25";
        var (num, den) = BroadcastRates.MinBy(r => Math.Abs(fps - (double)r.Num / r.Den));
        return den == 1 ? num.ToString(CultureInfo.InvariantCulture) : $"{num}/{den}";
    }

    private static IEnumerable<string> ContainerArguments(string target) => target switch
    {
        // Moves the index to the front so playback (and web streaming) can start before the file is loaded.
        "mp4" or "m4v" or "mov" or "3gp" or "3g2" or "m4a" or "m4b" => ["-movflags", "+faststart"],
        // Blu-ray / AVCHD transport stream: 192-byte packets with timestamps.
        "m2ts" => ["-mpegts_m2ts_mode", "1"],
        // DVD program stream: 10.08 Mbit/s mux rate and 2 KB sectors.
        "vob" => ["-muxrate", "10080000", "-packetsize", "2048"],
        // ID3v2.3 is what Windows Explorer and most car stereos read (FFmpeg writes 2.4 by default).
        "mp3" => ["-id3v2_version", "3"],
        // WAV is limited to 4 GB; long recordings switch to RF64 automatically.
        "wav" => ["-rf64", "auto"],
        _ => [],
    };

    private static IEnumerable<string> MetadataArguments(int from = 0)
    {
        // Keep title, artist, dates, … (chapters are kept by default); an empty value removes a tag.
        yield return "-map_metadata";
        yield return from.ToString(CultureInfo.InvariantCulture);
        foreach (var tag in SourceOnlyTags)
        {
            yield return "-metadata";
            yield return tag + "=";
        }
    }

    // ───────────────────────── Audio ─────────────────────────

    private static List<string> Audio(string input, string output, string target, MediaTarget spec, MediaOptions options, MediaInfo info,
        CoverExtras? extras)
    {
        var audio = info.Audio ?? throw new InvalidOperationException("The file has no audio track.");
        List<string> args = [.. Common, "-i", input];
        // Ogg: the tags, and the picture as a comment, come from a metadata file (input 1) instead of the source.
        if (extras?.MetadataFile is { } metadata)
            args.AddRange(["-i", metadata]);
        args.AddRange(["-map", "0:a:0"]);
        if (info.CoverStreamIndex is { } cover && CoverTargets.Contains(target))
            args.AddRange(["-map", $"0:{cover}", "-c:v", "copy", "-disposition:v:0", "attached_pic"]);
        if (extras is { AttachmentFile: { } picture, AttachmentMime: { } mime })
            args.AddRange(["-attach", picture, "-metadata:s:t", "mimetype=" + mime, "-metadata:s:t", "filename=" + Path.GetFileName(picture)]);
        var codec = target == "wav" ? WavCodec(options.WavFormat) : spec.AudioCodec;
        args.AddRange(AudioCodecArguments(target, codec, options, audio, inVideo: false));
        args.AddRange(ChannelAndRateArguments(target, codec, options));
        args.AddRange(MetadataArguments(extras?.MetadataFile is null ? 0 : 1));
        args.AddRange(ContainerArguments(target));
        args.AddRange(["-f", spec.Muxer, "-progress", "pipe:1", output]);
        return args;
    }

    /// <summary>The PCM codec of a WAV sample format (little-endian, 8-bit is unsigned as WAV defines it).</summary>
    internal static string WavCodec(WavSampleFormat format) => format switch
    {
        WavSampleFormat.Pcm8 => "pcm_u8",
        WavSampleFormat.Pcm24 => "pcm_s24le",
        WavSampleFormat.Pcm32 => "pcm_s32le",
        WavSampleFormat.Float32 => "pcm_f32le",
        _ => "pcm_s16le",
    };

    /// <summary>
    /// Mono or stereo for any audio target (after the codec's own limits, so the user's choice wins), and the sample
    /// rate for WAV, whose PCM takes any rate. Codecs with a fixed layout (AMR is mono, 8 kHz) are left alone.
    /// </summary>
    internal static IEnumerable<string> ChannelAndRateArguments(string target, string codec, MediaOptions options)
    {
        if (options.AudioChannels is 1 or 2 && codec != "libopencore_amrnb")
        {
            yield return "-ac";
            yield return options.AudioChannels.ToString(CultureInfo.InvariantCulture);
        }
        if (target == "wav" && options.AudioSampleRate is >= 8000 and <= 192000)
        {
            yield return "-ar";
            yield return options.AudioSampleRate.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Encoder options for one audio codec. <see cref="MediaOptions.AudioBitrateKbps"/> wins when set (clamped to
    /// what the codec accepts); otherwise the quality picks a bitrate or VBR level. Lossless and PCM codecs ignore
    /// both. Channel counts the codec cannot store are mixed down.
    /// </summary>
    private static List<string> AudioCodecArguments(string target, string codec, MediaOptions options, AudioStreamInfo audio, bool inVideo)
    {
        var quality = options.Quality;
        var requested = options.AudioBitrateKbps;
        var channels = audio.Channels > 0 ? audio.Channels : 2;
        List<string> args = ["-c:a", codec];

        switch (codec)
        {
            case "aac":
                // Per stereo pair: 256 kbit/s is transparent, 192 excellent, 128 fine for speech and small files.
                args.AddRange(Bitrate(requested > 0 ? requested : Scaled(Choose(quality, 256, 192, 128), channels), 16, 256 * channels));
                break;
            case "libopus":
                // Opus is transparent at about 128 kbit/s for stereo; 64 is still good for music.
                args.AddRange(Bitrate(requested > 0 ? requested : Scaled(Choose(quality, 160, 128, 64), channels), 6, 256 * channels));
                break;
            case "libmp3lame" when requested > 0 || inVideo:
                // Constant bitrate (variable-bitrate MP3 confuses AVI players' audio sync) from the MP3 bitrate table.
                args.AddRange(Bitrate(Snap(requested > 0 ? requested : Choose(quality, 256, 192, 128), Mp3Bitrates), 32, 320));
                break;
            case "libmp3lame":
                // LAME's VBR presets: V0 ≈ 245, V2 ≈ 190, V5 ≈ 130 kbit/s.
                args.AddRange(["-q:a", Text(Choose(quality, 0, 2, 5))]);
                break;
            case "libvorbis" when requested > 0:
                args.AddRange(Bitrate(requested, 32, 500));
                break;
            case "libvorbis":
                // Vorbis quality 6 ≈ 192, 4 ≈ 128, 2 ≈ 96 kbit/s.
                args.AddRange(["-q:a", Text(Choose(quality, 6, 4, 2))]);
                break;
            case "wmav2":
                // WMA 2 stores at most two channels.
                args.AddRange(Bitrate(requested > 0 ? requested : Choose(quality, 192, 128, 96), 32, 320));
                if (channels > 2)
                    args.AddRange(["-ac", "2"]);
                break;
            case "mp2":
                // Layer II allows only the bitrates of its table, and those only at 32 kHz or more.
                args.AddRange(Bitrate(Snap(requested > 0 ? requested : Choose(quality, 320, 224, 160), Mp2Bitrates), 32, 384));
                if (audio.SampleRate is > 0 and < 32000)
                    args.AddRange(["-ar", "32000"]);
                break;
            case "ac3" when target == "vob":
                // DVD audio is 48 kHz; 448 kbit/s is the DVD maximum for 5.1, 192 the usual stereo rate.
                args.AddRange(Bitrate(Snap(requested > 0 ? requested : channels > 2 ? 448 : 192, Ac3Bitrates), 64, 448));
                args.AddRange(["-ar", "48000"]);
                break;
            case "ac3":
                // Without a bitrate the encoder picks the standard rate for the channel layout (192k stereo, 448k 5.1).
                if (requested > 0)
                    args.AddRange(Bitrate(Snap(requested, Ac3Bitrates), 32, 640));
                break;
            case "libopencore_amrnb":
                // AMR-NB is defined for 8 kHz mono only; 12.2 kbit/s is its best mode.
                args.AddRange(["-ar", "8000", "-ac", "1", "-b:a", "12.2k"]);
                break;
            case "pcm_s16le" when target is "dv":
                args.AddRange(["-ar", "48000", "-ac", "2"]);
                break;
            case "pcm_s16le" when target is "mxf":
                // MXF (SMPTE 377) carries 48 kHz audio only.
                args.AddRange(["-ar", "48000"]);
                break;
            case "pcm_s16le" when target is "voc" && channels > 2:
                args.AddRange(["-ac", "2"]);
                break;
        }
        return args;
    }

    /// <summary>Bitrate for a stereo pair scaled to the channel count (mono gets half, 5.1 three times as much).</summary>
    private static int Scaled(int stereoKbps, int channels) => stereoKbps * Math.Clamp(channels, 1, 8) / 2;

    private static string[] Bitrate(int kbps, int min, int max) => ["-b:a", Text(Math.Clamp(kbps, min, Math.Max(min, max))) + "k"];

    /// <summary>The largest allowed bitrate not above <paramref name="kbps"/> (the smallest one if all are above).</summary>
    internal static int Snap(int kbps, int[] allowed) => allowed.Where(a => a <= kbps).DefaultIfEmpty(allowed[0]).Max();

    private static readonly int[] Mp3Bitrates = [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] Mp2Bitrates = [32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384];
    private static readonly int[] Ac3Bitrates = [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 576, 640];

    // ───────────────────────── Animated GIF ─────────────────────────

    /// <summary>
    /// Animated GIF in two passes: the first finds the 256 colours that suit the whole clip best (palettegen), the
    /// second maps every frame to them (paletteuse). A single pass with a fixed palette looks banded; generating
    /// the palette in the same run would keep every frame in memory until the end. Frame rate and width are limited
    /// by the quality setting because GIF stores every frame uncompressed-ish and grows fast.
    /// </summary>
    private static List<FfmpegPass> AnimatedGif(string input, string output, MediaOptions options, MediaInfo info, string workDirectory)
    {
        var video = info.Video ?? throw new InvalidOperationException("The file has no video stream.");
        var quality = options.Quality;
        var chain = GifFrames(video, options.MaxHeight, fps: Choose(quality, 15, 12, 10), maxWidth: Choose(quality, 640, 480, 320));
        var palette = Path.Combine(workDirectory, $"palette-{Guid.NewGuid():N}.png");
        var colors = Choose(quality, 256, 256, 128);
        // Error diffusion looks best; ordered (Bayer) dithering compresses much better because it does not flicker.
        var dither = Choose(quality, "sierra2_4a", "bayer:bayer_scale=4", "bayer:bayer_scale=5");

        // Pass 1 reports no output time until the palette is written at the very end, so a second, discarded copy
        // of the frames goes to a null output whose per-frame stats (frame_time=<seconds>) serve as progress.
        List<string> first =
        [
            .. Common, "-i", input,
            "-filter_complex", $"[0:V:0]{chain},split[a][b];[a]palettegen=max_colors={colors}[palette]",
            "-map", "[palette]", "-frames:v", "1", "-update", "1", "-f", "image2", palette,
            "-map", "[b]", "-stats_enc_pre", "pipe:1", "-stats_enc_pre_fmt", FfmpegRunner.FrameTimeKey + "={t}", "-f", "null", "-",
        ];
        List<string> second =
        [
            .. Common, "-i", input, "-i", palette,
            "-filter_complex", $"[0:V:0]{chain}[frames];[frames][1:v]paletteuse=dither={dither}:diff_mode=rectangle[gif]",
            "-map", "[gif]", "-loop", "0", "-f", "gif", "-progress", "pipe:1", output,
        ];
        return [new FfmpegPass(first, 0.4), new FfmpegPass(second, 0.6)];
    }

    /// <summary>Frame rate, square pixels and size of the GIF frames (never upscaled).</summary>
    private static string GifFrames(VideoStreamInfo video, int maxHeight, int fps, int maxWidth)
    {
        var height = maxHeight > 0 ? $"'min(ih,{Math.Max(16, maxHeight)})'" : "ih";
        return $"fps={fps},{SquarePixels(video)}scale=w='min(iw,{maxWidth})':h={height}:force_original_aspect_ratio=decrease:flags=lanczos";
    }

    /// <summary>Images have no pixel aspect ratio: anamorphic video (DV, DVD) is stretched to square pixels first.</summary>
    private static string SquarePixels(VideoStreamInfo video) =>
        Math.Abs(video.SampleAspect - 1) > 0.01 ? "scale=w='trunc(iw*sar/2)*2':h=ih,setsar=1," : "";

    // ───────────────────────── Still frame ─────────────────────────

    /// <summary>
    /// One full-size PNG frame from a tenth into the video (the first frames are often black), the start for video →
    /// image conversions (the image engines take it from there). Without this edge the route planner would reach
    /// images through an animated GIF of the whole video.
    /// </summary>
    private static List<string> PosterFrame(string input, string output, MediaInfo info)
    {
        var video = info.Video ?? throw new InvalidOperationException("The file has no video stream.");
        var seconds = (info.Duration?.TotalSeconds ?? 0) / 10;
        return
        [
            .. Common, "-ss", seconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", input, "-map", "0:V:0",
            "-vf", SquarePixels(video) + "null", "-frames:v", "1", "-update", "1", "-c:v", "png", "-f", "image2",
            "-progress", "pipe:1", output,
        ];
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static T Choose<T>(MediaQuality quality, T high, T balanced, T small) => quality switch
    {
        MediaQuality.High => high,
        MediaQuality.Small => small,
        _ => balanced,
    };

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);
}
