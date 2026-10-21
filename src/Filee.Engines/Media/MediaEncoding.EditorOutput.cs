// Writing and playing the editor's clip: its frame size and frame rate, the encoder (the graphics chip's when it works,
// else the software one), a bitrate that keeps the file under a size limit, GIFs with their own palette, videos joined
// without encoding when they allow it, and the preview's pictures and sound.

using System.Globalization;
using System.Text;
using Filee.Core.Presets;

namespace Filee.Engines.Media;

internal static partial class MediaEncoding
{
    /// <summary>A GIF's longer side and frame rate when the clip doesn't set them: small enough to share.</summary>
    private const int GifSide = 480, GifFps = 15;

    /// <summary>Of a file's size limit, what the picture and sound may use: the rest is the container and a margin.</summary>
    private const double SizeShare = 0.94;

    /// <summary>
    /// The frame of <paramref name="edit"/>: its size (the clip's shape and size, else the first video's) and frame rate
    /// (the clip's, else the first video's).
    /// </summary>
    internal static (int Width, int Height, string Fps) EditorCanvas(ClipEdit edit, Func<string?, EditorSource> sourceOf)
    {
        var first = sourceOf(edit.Parts.First(p => !p.Blank).Source);
        var (sourceWidth, sourceHeight) = first.Size;
        var (width, height) = ClipCanvas.Size(edit.Aspect, edit.Resolution, sourceWidth, sourceHeight);
        var fps = (edit.Fps > 0 ? NearestRate(edit.Fps) : NearestRate(first.Info.Video?.FrameRate ?? 0)) ?? "30";
        return (width, height, fps);
    }

    /// <summary>The frame rate FFmpeg was given (a whole number or a fraction) as a number.</summary>
    internal static double RateValue(string fps)
    {
        var parts = fps.Split('/');
        var num = double.Parse(parts[0], CultureInfo.InvariantCulture);
        return parts.Length == 2 ? num / double.Parse(parts[1], CultureInfo.InvariantCulture) : num;
    }

    /// <summary>
    /// The video bitrate (kbit/s) that keeps <paramref name="seconds"/> of video with <paramref name="audioKbps"/> of
    /// sound under <paramref name="megabytes"/> (1024 × 1024 bytes); at least 60.
    /// </summary>
    internal static int SizeLimitKbps(double megabytes, double seconds, int audioKbps)
    {
        var bits = megabytes * 1024 * 1024 * 8 * SizeShare;
        var total = bits / Math.Max(0.5, seconds) / 1000;
        return (int)Math.Max(60, Math.Floor(total - audioKbps));
    }

    /// <summary>
    /// The arguments that write <paramref name="edit"/> (an editor clip, clamped) as <paramref name="target"/>:
    /// <paramref name="hardware"/> encodes it when given (else the preset's codec in software), with the preset's quality
    /// or, under the clip's size limit, the bitrate that fits it. The texts' files go into <paramref name="workFolder"/>
    /// (null: the texts are left out, for an FFmpeg without drawtext).
    /// </summary>
    /// <exception cref="NotSupportedException">FFmpeg cannot write <paramref name="target"/> as a video.</exception>
    internal static List<string> EditorArguments(ClipEdit edit, Func<string?, EditorSource> sourceOf, string output, string target,
        MediaOptions options, IReadOnlySet<string>? encoders, HardwareEncoder? hardware, string? workFolder)
    {
        var gif = target == "gif";
        if (!gif && !VideoTargets.ContainsKey(target))
            throw new NotSupportedException($"FFmpeg cannot write '{target}'.");
        var (width, height, fps) = EditorCanvas(edit, sourceOf);
        if (gif)
        {
            if (edit.Resolution <= 0 && Math.Max(width, height) > GifSide)
            {
                var scale = (double)GifSide / Math.Max(width, height);
                (width, height) = (Even(width * scale), Even(height * scale));
            }
            if (edit.Fps <= 0 && RateValue(fps) > GifFps)
                fps = GifFps.ToString(CultureInfo.InvariantCulture);
        }
        var sound = !gif && !options.RemoveAudio;
        var graph = EditorGraphOf(edit, sourceOf, width, height, fps, "yuv420p", audio: sound, music: sound, textFolder: workFolder, sound: sound, layers: true);
        var filter = graph.Filter;
        List<string> args = [.. Common];

        if (gif)
        {
            filter += ";[v]split=2[g1][g2];[g1]palettegen=stats_mode=diff[gp];[g2][gp]paletteuse=dither=bayer:bayer_scale=4[gif]";
            args.AddRange([.. graph.Inputs, "-filter_complex", filter, "-map", "[gif]", "-an", "-loop", "0"]);
            args.AddRange(["-f", "gif", "-progress", "pipe:1", output]);
            return args;
        }

        var spec = VideoTargets[target];
        var picture = new VideoStreamInfo(width, height, 1, 0, RateValue(fps), PixelFormat: "yuv420p");
        var audioKbps = spec.AudioCodec == "libopus" ? 96 : 128;
        List<string> codec;
        var map = "[v]";
        if (hardware is not null)
        {
            args.AddRange(HardwareVideo.InputArguments(hardware));
            var suffix = HardwareVideo.FilterSuffix(hardware, tenBit: false);
            if (suffix.Length > 0)
            {
                filter += $";[v]null{suffix}[vh]";
                map = "[vh]";
            }
            codec = HardwareVideo.CodecArguments(hardware, target, options.Quality, picture, 0);
        }
        else
        {
            var encoder = VideoEncoder(target, spec.VideoCodec!, options.VideoCodec, encoders);
            codec = [.. VideoCodecArguments(target, encoder, options.Quality, picture, IsPal(picture.FrameRate))];
        }
        if (edit.MaxMegabytes > 0)
            codec = LimitRate(codec, SizeLimitKbps(edit.MaxMegabytes, graph.Length, graph.HasAudio ? audioKbps : 0), hardware?.Api);

        args.AddRange([.. graph.Inputs, "-filter_complex", filter, "-map", map]);
        args.AddRange(codec);
        if (graph.HasAudio)
        {
            var audioOptions = options.Clone();
            if (edit.MaxMegabytes > 0)
                audioOptions.AudioBitrateKbps = audioKbps;
            args.AddRange(["-map", "[a]"]);
            args.AddRange(AudioCodecArguments(target, spec.AudioCodec, audioOptions, new AudioStreamInfo(2, 48000), inVideo: true));
        }
        else
            args.Add("-an");
        args.AddRange(MetadataArguments());
        args.AddRange(ContainerArguments(target));
        args.AddRange(["-max_muxing_queue_size", "4096", "-f", spec.Muxer, "-progress", "pipe:1", output]);
        return args;
    }

    /// <summary>
    /// <paramref name="codec"/>'s options with its constant quality taken out and an average bitrate of
    /// <paramref name="kbps"/> put in (peaks a quarter higher), for a file that has to stay under a size.
    /// </summary>
    internal static List<string> LimitRate(List<string> codec, int kbps, HardwareApi? api)
    {
        string[] quality = ["-crf", "-cq", "-qp", "-global_quality", "-q:v", "-qp_i", "-qp_p", "-qp_b", "-rc", "-rc_mode", "-b:v"];
        var args = new List<string>();
        for (var i = 0; i < codec.Count; i++)
        {
            if (quality.Contains(codec[i]) && i + 1 < codec.Count)
            {
                i++;
                continue;
            }
            args.Add(codec[i]);
        }
        var rate = kbps.ToString(CultureInfo.InvariantCulture) + "k";
        var peak = ((int)(kbps * 1.25)).ToString(CultureInfo.InvariantCulture) + "k";
        var buffer = (kbps * 2).ToString(CultureInfo.InvariantCulture) + "k";
        args.AddRange(api switch
        {
            HardwareApi.Nvenc => ["-rc", "vbr"],
            HardwareApi.Amf => ["-rc", "vbr_peak"],
            HardwareApi.Vaapi => ["-rc_mode", "VBR"],
            _ => Array.Empty<string>(),
        });
        args.AddRange(["-b:v", rate, "-maxrate", peak, "-bufsize", buffer]);
        return args;
    }

    /// <summary>
    /// The arguments that play <paramref name="edit"/> in the editor: its pictures, the frame scaled to at most
    /// <paramref name="maxWidth"/> × <paramref name="maxHeight"/>, as raw BGRA frames at <paramref name="fps"/> on
    /// stdout; the texts are left out (the editor draws them).
    /// </summary>
    internal static List<string> EditorPreviewVideo(ClipEdit edit, Func<string?, EditorSource> sourceOf, int maxWidth, int maxHeight, int fps,
        out int width, out int height)
    {
        (width, height) = PreviewSize(edit, sourceOf, maxWidth, maxHeight);
        var graph = EditorGraphOf(edit, sourceOf, width, height, fps.ToString(CultureInfo.InvariantCulture), "bgra", sound: false);
        return [.. Common, .. graph.Inputs, "-filter_complex", graph.Filter, "-map", "[v]", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1"];
    }

    /// <summary>The arguments that play the sound of <paramref name="edit"/>: a WAV stream on stdout (for ffplay).</summary>
    internal static List<string> EditorPreviewAudio(ClipEdit edit, Func<string?, EditorSource> sourceOf)
    {
        var graph = EditorGraphOf(edit, sourceOf, 16, 16, "10", video: false);
        return [.. Common, .. graph.Inputs, "-filter_complex", graph.Filter, "-map", "[a]", "-c:a", "pcm_s16le", "-f", "wav", "pipe:1"];
    }

    /// <summary>The editor's frame scaled to fit <paramref name="maxWidth"/> × <paramref name="maxHeight"/> (even sides).</summary>
    internal static (int Width, int Height) PreviewSize(ClipEdit edit, Func<string?, EditorSource> sourceOf, int maxWidth, int maxHeight)
    {
        var (width, height, _) = EditorCanvas(edit, sourceOf);
        var scale = Math.Min(1, Math.Min((double)Math.Max(16, maxWidth) / width, (double)Math.Max(16, maxHeight) / height));
        return (Even(width * scale), Even(height * scale));
    }

    /// <summary>
    /// True when <paramref name="edit"/> only puts whole videos one after the other, as recorded, and they share their
    /// format (codecs, picture size, frame rate, sound) and the target takes it: they are joined without encoding.
    /// </summary>
    internal static bool CanJoinByCopy(ClipEdit edit, Func<string?, EditorSource> sourceOf, string target, MediaOptions options)
    {
        if (edit.Parts.Count < 2 || edit.Texts.Count > 0 || edit.Images.Count > 0 || edit.AllSounds.Any() || edit.FadeIn > 0 || edit.FadeOut > 0
            || edit.Aspect is not null || edit.Resolution > 0 || edit.Fps > 0 || edit.MaxMegabytes > 0 || edit.Separate
            || edit.LevelLoudness || edit.ReduceNoise || edit.LiftSpeech || options.RemoveAudio)
            return false;
        VideoStreamInfo? firstVideo = null;
        AudioStreamInfo? firstAudio = null;
        for (var i = 0; i < edit.Parts.Count; i++)
        {
            var part = edit.Parts[i];
            if (!part.IsPlain || part.Transition != ClipTransitionKind.Cut || part.FocusX != 0.5 || part.FocusY != 0.5)
                return false;
            var source = sourceOf(part.Source);
            var duration = source.Info.Duration?.TotalSeconds ?? 0;
            if (source.Info.Video is not { } video || part.Start > 0.05 || duration <= 0 || part.End < duration - 0.05)
                return false;
            if (!CanCopyVideo(target, video, options) || source.Info.Audio is { } sound && !CanCopyAudio(target, sound, options))
                return false;
            if (i == 0)
            {
                (firstVideo, firstAudio) = (video, source.Info.Audio);
                continue;
            }
            if ((video.Codec, video.Width, video.Height, video.PixelFormat, video.Rotation, NearestRate(video.FrameRate))
                != (firstVideo!.Codec, firstVideo.Width, firstVideo.Height, firstVideo.PixelFormat, firstVideo.Rotation, NearestRate(firstVideo.FrameRate)))
                return false;
            if ((source.Info.Audio?.Codec, source.Info.Audio?.Channels, source.Info.Audio?.SampleRate)
                != (firstAudio?.Codec, firstAudio?.Channels, firstAudio?.SampleRate))
                return false;
        }
        return true;
    }

    /// <summary>
    /// The arguments that join the videos of <paramref name="edit"/> (see <see cref="CanJoinByCopy"/>) by copying
    /// them through FFmpeg's concat demuxer; <paramref name="list"/> is the file list it reads, written here.
    /// </summary>
    internal static List<string> JoinByCopyArguments(ClipEdit edit, Func<string?, EditorSource> sourceOf, string output, string target, string list)
    {
        var lines = new StringBuilder("ffconcat version 1.0\n");
        foreach (var part in edit.Parts)
            lines.Append("file '").Append(sourceOf(part.Source).Path.Replace("'", @"'\''", StringComparison.Ordinal)).Append("'\n");
        File.WriteAllText(list, lines.ToString(), new UTF8Encoding(false));
        var spec = VideoTargets[target];
        List<string> args = [.. Common, "-f", "concat", "-safe", "0", "-i", list, "-map", "0:V:0", "-map", "0:a:0?", "-c", "copy"];
        args.AddRange(MetadataArguments());
        args.AddRange(ContainerArguments(target));
        args.AddRange(["-f", spec.Muxer, "-progress", "pipe:1", output]);
        return args;
    }
}
