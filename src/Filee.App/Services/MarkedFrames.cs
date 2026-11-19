// The frames "Mark this frame" takes in Cut a clip (#1205) on their way to Annotate: each is a PNG named after its
// video and its time, in a work folder of its own (swept after a crash like a job's). Annotate's output rules read the
// video's folder as the frame's own (so its marked copy goes next to the video unless they name another folder), and
// it never trashes the frame as an "original". A frame goes once its marked copy is saved, cancelled or skipped, when
// Annotate leaves it unsaved, and when Filee quits; a failed save keeps it for "Try again".

using System.Collections.Concurrent;
using System.Globalization;
using Filee.Core.Conversion;
using Filee.Core.Presets;

namespace Filee.App.Services;

public sealed class MarkedFrames : IDisposable
{
    private readonly string _root;
    private readonly JobQueue? _queue;

    /// <summary>Each frame and the video it was taken from.</summary>
    private readonly ConcurrentDictionary<string, string> _videos = new(Filee.Core.Platform.FileSystemPaths.Comparer);

    /// <param name="workRoot">Where the frames' folders go (<see cref="JobQueue.DefaultWorkRoot"/>).</param>
    /// <param name="queue">Says when a frame's marked copy is saved.</param>
    public MarkedFrames(string workRoot, JobQueue? queue = null)
    {
        _root = workRoot;
        _queue = queue;
        if (queue is not null)
            queue.JobFinished += OnJobFinished;
    }

    /// <summary>
    /// The frame's file name: the video's name and the frame's time to the millisecond ("Site walk 1m43.400s.png"), as
    /// a frame number alone can mislead in variable frame rate video, and a colon can't be in a file name on Windows.
    /// </summary>
    public static string NameFor(string video, double seconds)
    {
        var ms = double.IsFinite(seconds) ? (long)Math.Round(Math.Max(0, seconds) * 1000, MidpointRounding.AwayFromZero) : 0;
        var (hours, minutes) = (ms / 3_600_000, ms / 60_000 % 60);
        var rest = string.Create(CultureInfo.InvariantCulture, $"{ms / 1000 % 60:00}.{ms % 1000:000}s");
        var time = hours > 0 ? string.Create(CultureInfo.InvariantCulture, $"{hours}h{minutes:00}m{rest}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}m{rest}");
        return $"{Shortened(Path.GetFileNameWithoutExtension(video))} {time}.png";
    }

    /// <summary>
    /// The longest a video's name may be in a frame's name, in UTF-8 bytes: with the time, Annotate's " Annotated" and
    /// a " (99)" for a name already taken, the marked copy's name stays within the 255 bytes a file name may have.
    /// </summary>
    private const int MaxNameBytes = 200;

    /// <summary><paramref name="name"/>, cut to whole characters within <see cref="MaxNameBytes"/>.</summary>
    private static string Shortened(string name)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(name) <= MaxNameBytes)
            return name;
        var kept = new System.Text.StringBuilder();
        var bytes = 0;
        var characters = StringInfo.GetTextElementEnumerator(name);
        while (characters.MoveNext())
        {
            var character = characters.GetTextElement();
            bytes += System.Text.Encoding.UTF8.GetByteCount(character);
            if (bytes > MaxNameBytes)
                break;
            kept.Append(character);
        }
        return kept.ToString().TrimEnd();
    }

    /// <summary>
    /// A path for the frame at <paramref name="seconds"/> of <paramref name="video"/>, in a new folder of its own.
    /// </summary>
    public string NewPath(string video, double seconds)
    {
        var folder = Path.Combine(_root, Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, NameFor(video, seconds));
        _videos[path] = video;
        return path;
    }

    /// <summary>The video <paramref name="frame"/> was taken from; null for any other picture.</summary>
    public string? VideoOf(string frame) => _videos.GetValueOrDefault(frame);

    /// <summary>
    /// Annotate's conversion of <paramref name="source"/>: for a frame, "next to the source" is next to its video, and
    /// there is no "original" to replace or trash (that would only be the frame); any other picture's as it is. A frame
    /// marked again finds its first marked copy there: "skip" would lose the new marks with the frame, so it gets a
    /// name of its own instead.
    /// </summary>
    public Preset Apply(string source, Preset conversion)
    {
        if (VideoOf(source) is not { } video)
            return conversion;
        var copy = conversion.Clone();
        copy.Output.SourceFolder = Path.GetDirectoryName(Path.GetFullPath(video));
        copy.Output.ReplaceOriginal = false;
        copy.Output.TrashOriginalAfterConvert = false;
        if (copy.Output.Conflict == ConflictPolicy.Skip)
            copy.Output.Conflict = ConflictPolicy.Rename;
        return copy;
    }

    /// <summary>
    /// <paramref name="frame"/> is one of these frames and still wanted: false once it is discarded, and for every
    /// frame once Filee quits.
    /// </summary>
    public bool IsWanted(string frame) => !_quit && _videos.ContainsKey(frame);

    private volatile bool _quit;

    /// <summary>Deletes <paramref name="frame"/> and its folder; anything that isn't a frame is left alone.</summary>
    public void Discard(string frame)
    {
        if (!_videos.ContainsKey(frame))
            return;
        // A folder still open somewhere (on Windows, while a program reads the file) stays known, so the next try
        // takes it, at the latest when Filee quits; failing that, the sweep at a later start
        // (JobQueue.DeleteStaleWorkFolders).
        var folder = Path.GetDirectoryName(frame)!;
        Filee.Engines.Archives.ArchiveFiles.TryDeleteFolder(folder);
        if (!Directory.Exists(folder))
            _videos.TryRemove(frame, out _);
    }

    /// <summary>
    /// A frame whose marked copy was saved, or whose saving was cancelled or skipped, is no longer needed; a failed one
    /// stays for "Try again" (until Filee quits).
    /// </summary>
    internal void OnJobFinished(ConversionJob job)
    {
        foreach (var file in job.Files)
            if (file.State is FileState.Done or FileState.Cancelled or FileState.Skipped)
                Discard(file.SourcePath);
    }

    private void OnJobFinished(object? sender, ConversionJob job) => OnJobFinished(job);

    /// <summary>Filee quits: every frame goes, and one still being taken is no longer wanted.</summary>
    public void Dispose()
    {
        _quit = true;
        if (_queue is not null)
            _queue.JobFinished -= OnJobFinished;
        foreach (var frame in _videos.Keys)
            Discard(frame);
    }
}
