// The editor's clips (ClipEdit.Editor): every video it uses is read, the clip kept inside them, and written through
// the editor's graph, on the graphics chip when it works (in software when it doesn't). Videos only joined end to end
// that share their format are copied instead; a clip that came out over its size limit is written once more with a
// lower bitrate; several highlights become one file each.

using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Microsoft.Extensions.Logging;

namespace Filee.Engines.Media;

public sealed partial class FfmpegConverter
{
    private async Task<IReadOnlyList<string>> EditorClipAsync(string ffmpeg, string ffprobe, ConversionStep step, string input, MediaInfo info,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var media = step.Preset.Media;
        var main = new EditorSource(input, info);
        var sources = new Dictionary<string, EditorSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in media.Clip!.Parts.Where(p => !p.Blank && p.Source is not null).Select(p => Path.GetFullPath(p.Source!)).Distinct(StringComparer.OrdinalIgnoreCase))
            sources[path] = new EditorSource(path, await MediaInfo.ProbeAsync(ffprobe, path, cancellationToken));
        EditorSource SourceOf(string? path) => path is null ? main : sources[Path.GetFullPath(path)];

        var edit = media.Clip.Clamped(path => SourceOf(path).Info.Duration?.TotalSeconds);
        if (edit.Parts.Any(p => !p.Blank && SourceOf(p.Source).Info.Video is null))
            throw new InvalidOperationException("A part of the clip comes from a file without a picture.");
        var clips = edit.Separate ? Highlights(edit, path => SourceOf(path).Info.Duration?.TotalSeconds) : [edit];
        var extension = FormatRegistry.Get(step.To).PrimaryExtension;
        var outputs = new List<string>();
        try
        {
            for (var n = 0; n < clips.Count; n++)
            {
                var output = step.Output.Allocate(extension, clips.Count > 1 ? $"_{n + 1}" : null);
                if (output is null)
                    break;
                outputs.Add(output);
                var part = progress is null ? null : new PartProgress(progress, (double)n / clips.Count, 1.0 / clips.Count);
                await WriteEditorClipAsync(ffmpeg, step, clips[n], SourceOf, output, part, cancellationToken);
            }
        }
        catch
        {
            foreach (var output in outputs)
                TryDelete(output);
            throw;
        }
        progress?.Report(1);
        return outputs;
    }

    private async Task WriteEditorClipAsync(string ffmpeg, ConversionStep step, ClipEdit clip, Func<string?, EditorSource> sourceOf, string output,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var media = step.Preset.Media;
        var duration = TimeSpan.FromSeconds(clip.Length);
        if (step.To != "gif" && MediaEncoding.CanJoinByCopy(clip, sourceOf, step.To, media))
        {
            try
            {
                var list = Path.Combine(step.WorkDirectory, $"join-{Guid.NewGuid():N}.txt");
                Directory.CreateDirectory(step.WorkDirectory);
                var copy = new FfmpegPass(MediaEncoding.JoinByCopyArguments(clip, sourceOf, output, step.To, list), 1);
                await RunPassesAsync(ffmpeg, [copy], output, duration, progress, cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("{File}: joining the videos by copying failed ({Reason}); encoding them instead", output, ex.Message);
                TryDelete(output);
            }
        }

        var encoders = EncodersOf(ffmpeg);
        HardwareEncoder? hardware = null;
        if (step.To != "gif" && media.HardwareEncoding && MediaEncoding.VideoTargets.ContainsKey(step.To)
            && MediaCodecs.TakesHardwareEncoding(step.To, media.VideoCodec))
        {
            var codec = MediaCodecs.Effective(step.To, media.VideoCodec) switch
            {
                VideoCodec.Hevc => "hevc",
                VideoCodec.Av1 => "av1",
                _ => "h264",
            };
            hardware = SelectHardware is { } select
                ? await select(ffmpeg, codec, false, encoders, cancellationToken)
                : await HardwareProbe.SelectAsync(ffmpeg, codec, false, encoders, _logger, cancellationToken);
        }

        // Texts need FFmpeg's drawtext (FreeType); a build without it writes the clip without them rather than not at all.
        string? work = Path.Combine(step.WorkDirectory, "texts");
        if (clip.Texts.Any(t => t.Rendered is null) && !HasFilter(ffmpeg, "drawtext"))
        {
            _logger.LogWarning("{File}: this FFmpeg has no drawtext filter; the clip is written without its texts", output);
            work = null;
        }
        async Task WriteAsync(ClipEdit edit)
        {
            IReadOnlyList<FfmpegPass> Plan(HardwareEncoder? encoder) =>
                [new FfmpegPass(MediaEncoding.EditorArguments(edit, sourceOf, output, step.To, media, encoders, encoder, work), 1)];
            await RunWithFallbackAsync(Plan(hardware), hardware, Plan,
                planned => RunPassesAsync(ffmpeg, planned, output, duration, progress, cancellationToken), output, _logger, cancellationToken);
        }

        await WriteAsync(clip);
        // A size limit is aimed at, not promised: a file that came out larger is written again a little smaller.
        var limit = clip.MaxMegabytes * 1024 * 1024;
        if (limit > 0 && step.To != "gif" && new FileInfo(output).Length is var size && size > limit)
        {
            _logger.LogInformation("{File}: {Size} bytes is over the limit of {Limit}; writing it again smaller", output, size, (long)limit);
            var smaller = clip.Clone();
            smaller.MaxMegabytes = clip.MaxMegabytes * 0.92 * limit / size;
            TryDelete(output);
            await WriteAsync(smaller);
        }
    }

    /// <summary>Each part that plays of <paramref name="edit"/> as a clip of its own, with the texts over it and the music under it.</summary>
    internal static List<ClipEdit> Highlights(ClipEdit edit, Func<string?, double?> durationOf)
    {
        var starts = edit.PartStarts();
        var clips = new List<ClipEdit>();
        for (var i = 0; i < edit.Parts.Count; i++)
        {
            if (edit.Parts[i].Blank)
                continue;
            var rest = edit.From(starts[i]);
            var part = rest.Parts[0];
            part.Transition = ClipTransitionKind.Cut;
            rest.Parts = [part];
            rest.Separate = false;
            rest.FadeIn = edit.FadeIn;
            rest.FadeOut = edit.FadeOut;
            clips.Add(rest.Clamped(durationOf));
        }
        return clips;
    }

    /// <summary>One file's share of the progress when a conversion writes several.</summary>
    private sealed class PartProgress(IProgress<double> parent, double start, double length) : IProgress<double>
    {
        public void Report(double value) => parent.Report(start + Math.Clamp(value, 0, 1) * length);
    }
}
