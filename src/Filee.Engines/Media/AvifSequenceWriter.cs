// Animated GIF / WebP / APNG / MNG / JPEG XL / AVIF → animated AVIF with FFmpeg, when it is installed and has libaom's
// AV1 encoder. ImageMagick's own sequence writer (libheif) takes 50 s or more for 60 busy frames, can't be stopped once
// it writes and stores no loop count; FFmpeg's libaom-av1 and its avif muxer write the same image sequence in a few
// seconds, stop with the conversion and keep the loop count. ImageMagick still reads the frames and applies the preset
// (MagickImageConverter.WriteAsync); Apng packs them into one APNG in the work directory with their exact delays, which
// FFmpeg's APNG demuxer hands on frame for frame. Without FFmpeg, ImageEncoder.Write writes the sequence as before.

using System.Globalization;
using Filee.Engines.Magick;
using ImageMagick;

namespace Filee.Engines.Media;

/// <summary>Writes an AVIF image sequence with FFmpeg's libaom-av1 and avif muxer.</summary>
internal static class AvifSequenceWriter
{
    /// <summary>The AV1 encoder of both tracks.</summary>
    internal const string Encoder = "libaom-av1";

    /// <summary>
    /// Frames ImageMagick reads with a delay of 0 play at the 100 ms players give such a GIF frame: FFmpeg's APNG demuxer
    /// shows a frame without a delay for 1 / this many seconds.
    /// </summary>
    private const int ZeroDelayFps = 10;

    /// <summary>
    /// True when an FFmpeg with these encoders writes the sequence: libaom-av1 takes any size and the alpha track, which
    /// the avif muxer takes only as one grey plane. SVT-AV1 can't be used, although it is about as fast: it encodes no
    /// grey plane, and FFmpeg's wrapper hands on no packet durations, so the muxer guesses the last frame's (a GIF's long
    /// pause at the end would be cut short). False when the list can't be read: ImageMagick writes the sequence then.
    /// </summary>
    internal static bool CanWrite(IReadOnlySet<string>? available) => available?.Contains(Encoder) == true;

    /// <summary>The FFmpeg to write the sequence with (<see cref="CanWrite"/>), or null to leave it to ImageMagick.</summary>
    public static async Task<string?> FindAsync(Func<(string Ffmpeg, string Ffprobe)?> locate)
    {
        if (locate() is not { } tools || !File.Exists(tools.Ffmpeg))
            return null;
        return CanWrite(await FfmpegConverter.EncodersOfAsync(tools.Ffmpeg).ConfigureAwait(false)) ? tools.Ffmpeg : null;
    }

    /// <summary>
    /// Writes <paramref name="frames"/> into <paramref name="output"/>. The work goes into <paramref name="workDirectory"/>;
    /// <paramref name="output"/> is only written once FFmpeg has finished, so a cancelled or failed run leaves nothing there.
    /// </summary>
    /// <param name="progress">Reports from <paramref name="start"/> to just under 1 while FFmpeg encodes.</param>
    /// <exception cref="InvalidOperationException">FFmpeg failed (an old copy without the avif muxer, …).</exception>
    public static async Task WriteAsync(string ffmpeg, IReadOnlyList<IMagickImage<byte>> frames, string output,
        int quality, string workDirectory, IProgress<double>? progress, double start, CancellationToken cancellationToken)
    {
        // One kind of alpha for every frame, as on ImageMagick's route: an opaque animation gets no alpha track.
        ImageEncoder.MatchAlpha(frames);
        var (width, height) = (frames.Max(f => f.Width), frames.Max(f => f.Height));
        // Apng pads smaller frames with transparency.
        var alpha = frames.Any(f => f.HasAlpha || f.Width != width || f.Height != height);
        var name = Path.Combine(workDirectory, $"avif-{Guid.NewGuid():N}");
        var apng = name + ".apng";
        var avif = name + ".avif";
        try
        {
            Apng.Write(frames, apng, cancellationToken);
            var total = frames.Sum(Seconds);
            Action<double>? onPosition = progress is null || total <= 0
                ? null
                : seconds => progress.Report(start + (0.99 - start) * Math.Clamp(seconds / total, 0, 1));
            var loops = frames[0].AnimationIterations;
            var result = await FfmpegRunner.RunAsync(ffmpeg, Arguments(apng, avif, alpha, quality, loops), onPosition, cancellationToken);
            if (result.ExitCode != 0)
            {
                var reason = FfmpegRunner.Summarize(result.StandardError);
                throw new InvalidOperationException($"FFmpeg failed (exit code {result.ExitCode}){(reason.Length > 0 ? ": " + reason : ".")}");
            }
            if (!File.Exists(avif) || new FileInfo(avif).Length == 0)
                throw new InvalidOperationException("FFmpeg did not write any output.");
            File.Move(avif, output, overwrite: true);
        }
        finally
        {
            TryDelete(apng);
            TryDelete(avif);
        }
    }

    /// <summary>
    /// The FFmpeg run: the APNG's frames at their own times (no frames added or dropped), converted to full-range BT.709
    /// YUV 4:2:0 and tagged as sRGB; with transparency the alpha channel becomes the second, grey track. The avif muxer
    /// writes the first frame as the still picture too and <paramref name="loops"/> as the edit list's repeat (0 loops
    /// for ever, as in an APNG).
    /// </summary>
    internal static List<string> Arguments(string apng, string output, bool alpha, int quality, uint loops)
    {
        const string colour = "scale=out_color_matrix=bt709:out_range=pc,format=yuv420p," +
            "setparams=range=pc:color_primaries=bt709:color_trc=iec61966-2-1:colorspace=bt709";
        List<string> args =
        [
            .. MediaEncoding.Common, "-default_fps", Text(ZeroDelayFps), "-f", "apng", "-i", apng,
            "-filter_complex", !alpha
                ? $"[0:v]{colour}[c]"
                // A grey plane keeps the colour space of the RGBA frames ("gbr"), which libaom refuses for one plane.
                : $"[0:v]format=rgba,split[rgb][al];[rgb]{colour}[c];[al]alphaextract,format=gray,setparams=range=pc:colorspace=bt709[a]",
            "-map", "[c]",
        ];
        if (alpha)
            args.AddRange(["-map", "[a]"]);
        // As for video (MediaEncoding): -b:v 0 makes the CRF the quality, cpu-used 6 with row threads keeps libaom quick.
        // Without frames of look-ahead it is twice as fast again (2.6 s for 60 busy 480 × 270 frames), the file a few
        // percent larger.
        args.AddRange(["-fps_mode", "passthrough", "-c:v", Encoder, "-crf", Text(Crf(quality)), "-b:v", "0", "-cpu-used", "6",
            "-row-mt", "1", "-lag-in-frames", "0"]);
        args.AddRange(["-loop", Text(loops), "-f", "avif", "-progress", "pipe:1", output]);
        return args;
    }

    /// <summary>
    /// The preset's quality (1–100) as AV1's quantizer (63–0), the way libheif turns it into one for ImageMagick's
    /// route, so a preset gives about the same picture both ways.
    /// </summary>
    internal static int Crf(int quality) => ((100 - Math.Clamp(quality, 1, 100)) * 63 + 50) / 100;

    /// <summary>How long FFmpeg shows <paramref name="frame"/>, a delay of 0 included.</summary>
    private static double Seconds(IMagickImage<byte> frame) =>
        frame.AnimationDelay == 0 ? 1.0 / ZeroDelayFps
            : frame.AnimationDelay / (double)(frame.AnimationTicksPerSecond > 0 ? frame.AnimationTicksPerSecond : 100);

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
