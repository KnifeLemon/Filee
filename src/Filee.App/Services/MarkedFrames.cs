// The frames "Mark this frame" takes in Cut a clip (#1205) on their way to Annotate: each is a PNG named after its
// video and its time, in a work folder of its own (swept after a crash like a job's). Annotate's output rules read the
// video's folder as the frame's own (so its marked copy goes next to the video unless they name another folder), and
// it never trashes the frame as an "original". A frame goes once its marked copy is saved, cancelled or skipped, when
// Annotate leaves it unsaved, and when Filee quits; a failed save keeps it for "Try again".
//
// Step 3: each copy carries the frame's facts (FrameTags), so Cut a clip's tray finds a video's copies again, by those,
// in the folder Annotate's output rules give and in every folder a copy of it was saved to (remembered in a file of
// its own, marked-frames.json, so saving a frame never touches the settings or their listeners). Marking a frame again
// replaces its earlier copy, which goes to the Trash once the new one is saved. Folders are read off the UI thread and
// off the job queue's.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Filee.Core.Conversion;
using Filee.Core.Platform;
using Filee.Core.Presets;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services;

/// <summary>
/// A marked frame's copy found for a video: where it is, what it says it is, and when it was written (a copy saved
/// again over the same name is another copy).
/// </summary>
public sealed record MarkedCopy(string Path, FrameFacts Facts, DateTime Written = default);

public sealed class MarkedFrames : IDisposable
{
    private readonly string _root;
    private readonly JobQueue? _queue;
    private readonly string? _remembered;
    private readonly Func<string, TrashRefusal?>? _trash;
    private readonly ILogger? _log;

    /// <summary>
    /// A frame taken and what its copy will say about it: its video, the video's length and size, its time, and its
    /// number being counted (stopped once the frame is gone).
    /// </summary>
    private sealed record Frame(string Video, double VideoSeconds, long VideoBytes, double Seconds, Task<long?> Number, CancellationTokenSource Counting);

    /// <summary>The frames taken, by path.</summary>
    private readonly ConcurrentDictionary<string, Frame> _frames = new(FileSystemPaths.Comparer);

    /// <summary>
    /// What each PNG looked through says it is (a marked frame's facts, or null), by path, kept while its size and
    /// time stay the same: a folder looked through again is only listed (on macOS, each file's flags read too).
    /// </summary>
    private readonly ConcurrentDictionary<string, (long Length, DateTime Written, FrameFacts? Facts)> _read = new(FileSystemPaths.Comparer);

    /// <summary>
    /// The folders copies were saved to, as video and folder pairs, the most recent last; read on first use.
    /// </summary>
    private List<(string Video, string Folder)>? _folders;

    private readonly Lock _remembering = new();

    /// <summary>How many video and folder pairs are remembered; the oldest go first.</summary>
    internal const int FolderLimit = 500;

    /// <param name="workRoot">Where the frames' folders go (<see cref="JobQueue.DefaultWorkRoot"/>).</param>
    /// <param name="queue">Says when a frame's marked copy is saved.</param>
    /// <param name="dataDirectory">
    /// Where the remembered folders are kept (Filee's own data folder); none when null.
    /// </param>
    /// <param name="trash">Moves a file to the Trash (<see cref="IPlatformServices.MoveToTrash"/>).</param>
    public MarkedFrames(string workRoot, JobQueue? queue = null, string? dataDirectory = null, Func<string, TrashRefusal?>? trash = null,
        ILogger? log = null)
    {
        _root = workRoot;
        _queue = queue;
        _remembered = dataDirectory is null ? null : Path.Combine(dataDirectory, "marked-frames.json");
        _trash = trash;
        _log = log;
        if (queue is not null)
            queue.JobFinished += OnJobFinished;
    }

    /// <summary>
    /// A copy of the video at this path was saved or removed: its tray reads again. Raised off the UI thread.
    /// </summary>
    public event Action<string>? CopiesChanged;

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
        return $"{OutputPathResolver.Shortened(Path.GetFileNameWithoutExtension(video), MaxNameBytes)} {time}.png";
    }

    /// <summary>
    /// The longest a video's name may be in a frame's name, in UTF-8 bytes: with the time, Annotate's " Annotated" and
    /// a " (99)" for a name already taken, the marked copy's name stays within the 255 bytes a file name may have.
    /// </summary>
    private const int MaxNameBytes = 200;

    /// <summary>
    /// A path for the frame at <paramref name="seconds"/> of <paramref name="video"/>, in a new folder of its own;
    /// <paramref name="videoSeconds"/> long, its number counted by <paramref name="count"/> (none when null).
    /// </summary>
    public string NewPath(string video, double seconds, double videoSeconds = 0, Func<CancellationToken, Task<long?>>? count = null)
    {
        var folder = Path.Combine(_root, Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, NameFor(video, seconds));
        var counting = new CancellationTokenSource();
        Task<long?> number;
        try
        {
            number = count?.Invoke(counting.Token) ?? Task.FromResult<long?>(null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            number = Task.FromException<long?>(ex);
        }
        _frames[path] = new Frame(video, videoSeconds, SizeOf(video), seconds, number, counting);
        return path;
    }

    private static long SizeOf(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 0;
        }
    }

    /// <summary>
    /// The frame's video, its time and the video's length, and its number being counted; null for any other picture.
    /// </summary>
    public (string Video, double Seconds, double VideoSeconds, Task<long?> Number)? FrameOf(string frame) =>
        _frames.TryGetValue(frame, out var taken) ? (taken.Video, taken.Seconds, taken.VideoSeconds, taken.Number) : null;

    /// <summary>The video <paramref name="frame"/> was taken from; null for any other picture.</summary>
    public string? VideoOf(string frame) => _frames.GetValueOrDefault(frame)?.Video;

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
    /// <see cref="Apply"/>, and for a frame its facts with <paramref name="note"/> (<see cref="ImageOptions.Frame"/>),
    /// once its number is counted: a copy is never written before it, and is written without one when counting fails
    /// or was cancelled.
    /// </summary>
    public async Task<Preset> ApplyAsync(string source, Preset conversion, string? note, CancellationToken cancellationToken)
    {
        var copy = Apply(source, conversion);
        if (!_frames.TryGetValue(source, out var facts))
            return copy;
        long? number;
        try
        {
            number = await facts.Number.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
            number = null;
        }
        copy.Image.Frame = new FrameFacts(Path.GetFileName(facts.Video), facts.VideoSeconds, facts.VideoBytes, facts.Seconds, number, note ?? "");
        return copy;
    }

    /// <summary>
    /// The marked copies of <paramref name="video"/> (<paramref name="videoSeconds"/> long) in time order, one per
    /// frame time (the newest file of a time), read off the calling thread: found by what they say, in the folder
    /// <paramref name="annotateTool"/>'s output rules give for the video and in the folders copies of it were saved
    /// to. A folder or file that can't be read is passed over; a remembered folder that is gone is forgotten.
    /// </summary>
    public Task<IReadOnlyList<MarkedCopy>> CopiesOfAsync(string video, double videoSeconds, Preset annotateTool, CancellationToken cancellationToken) =>
        Task.Run(() => CopiesOf(video, videoSeconds, annotateTool, cancellationToken), cancellationToken);

    internal IReadOnlyList<MarkedCopy> CopiesOf(string video, double videoSeconds, Preset annotateTool, CancellationToken cancellationToken = default)
    {
        var (name, bytes) = (Path.GetFileName(video), SizeOf(video));
        var found = new List<(MarkedCopy Copy, DateTime Written)>();
        foreach (var folder in FoldersOf(video, annotateTool))
            foreach (var (file, facts, written) in CopiesIn(folder, cancellationToken))
                if (FrameTags.SameVideo(facts, name, videoSeconds, bytes))
                    found.Add((new MarkedCopy(file, facts, written), written));
        return [.. found.GroupBy(f => FrameTags.Milliseconds(f.Copy.Facts.FrameSeconds))
            .Select(time => time.OrderByDescending(f => f.Written).ThenBy(f => f.Copy.Path, StringComparer.Ordinal).First().Copy)
            .OrderBy(c => c.Facts.FrameSeconds)];
    }

    /// <summary>
    /// The PNGs in <paramref name="folder"/> that are marked frames' copies. Files that can't be one are never opened:
    /// too small for a PNG (a pipe reads as empty), or online-only in a cloud folder (opening would download it):
    /// marked so by Windows' attributes, or by macOS's dataless flag (MacFileFlags).
    /// </summary>
    /// <param name="listing">For the list (true), or for replacing a frame's copy (false).</param>
    private IEnumerable<(string File, FrameFacts Facts, DateTime Written)> CopiesIn(string folder, CancellationToken cancellationToken,
        Func<string, bool>? named = null, bool listing = true)
    {
        List<FileInfo> files;
        try
        {
            files = [.. new DirectoryInfo(folder).EnumerateFiles("*.png", new EnumerationOptions
            {
                MatchCasing = MatchCasing.CaseInsensitive,
                IgnoreInaccessible = true,
                // Hidden only as Windows marks it: on macOS and Linux a name starting with a dot reads as hidden, and a
                // video named so (".site walk.mp4") has copies named so.
                AttributesToSkip = FileAttributes.System | (OperatingSystem.IsWindows() ? FileAttributes.Hidden : 0),
            })];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            // Forgotten only when it is gone from a folder that is there: not when its drive or share is away for now
            // (on Windows its drive's root is gone too; on macOS and Linux a drive's own folder is gone from where
            // drives are mounted).
            if (!Directory.Exists(folder) && Path.GetDirectoryName(folder) is { } parent && Directory.Exists(parent) && !IsMountArea(parent))
                Forget(folder);
            yield break;
        }
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (named is not null && !named(file.Name))
                continue;
            long length;
            DateTime written;
            try
            {
                (length, written) = (file.Length, file.LastWriteTimeUtc);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (length < 8)
                continue;
            // Online-only in a cloud folder (Windows' offline and recall attributes, macOS's dataless flag), checked at
            // every look: a copy read before can have been moved to the cloud since. The list leaves it out (its
            // picture would be downloaded); replacing a frame's copy still sees it if what it says is known, without
            // opening it.
            var known = _read.TryGetValue(file.FullName, out var read) && read.Length == length && read.Written == written;
            if (OnlineOnly(file) && (listing || !known))
                continue;
            FrameFacts? facts;
            if (known)
                facts = read.Facts;
            else
            {
                facts = FrameTags.ReadFile(file.FullName);
                if (_read.Count > ReadLimit)
                    _read.Clear();
                _read[file.FullName] = (length, written, facts);
            }
            if (facts is not null)
                yield return (file.FullName, facts, written);
        }
    }

    /// <summary>
    /// Where macOS and Linux put a drive's own folder when it is plugged in: one there that is gone may be away.
    /// </summary>
    internal static bool IsMountArea(string folder)
    {
        if (OperatingSystem.IsWindows())
            return false;
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var parent = Path.GetDirectoryName(path);
        return path is "/Volumes" or "/media" or "/mnt" or "/run/media" || parent is "/media" or "/run/media";
    }

    /// <summary>The file is online-only in a cloud folder: opening it would download it.</summary>
    private static bool OnlineOnly(FileInfo file)
    {
        try
        {
            if ((file.Attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0)
                return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // can't be told: left alone
        }
        return OperatingSystem.IsMacOS() && MacFileFlags.IsDataless(file.FullName);
    }

    /// <summary>How many files' facts are kept; past this, the list starts again.</summary>
    private const int ReadLimit = 20_000;

    // Windows' marks of a cloud file that isn't on the disk (FILE_ATTRIBUTE_RECALL_ON_OPEN, _RECALL_ON_DATA_ACCESS).
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>
    /// The folders a video's copies can be in: where <paramref name="annotateTool"/>'s rules put one now, and those
    /// remembered; each once.
    /// </summary>
    private List<string> FoldersOf(string video, Preset annotateTool)
    {
        var folders = new List<string>();
        // A frame is saved alone: a subfolder "only for several files" isn't used for it.
        var rule = annotateTool.Output.ForOneFile().Clone();
        rule.SourceFolder = Path.GetDirectoryName(Path.GetFullPath(video));
        if (OutputPathResolver.Resolve(rule, new OutputPathResolver.Tokens(video, annotateTool.Name, 0, DateTime.Now), "png", _ => false) is { } path
            && Path.GetDirectoryName(path) is { } now)
            folders.Add(now);
        folders.AddRange(Remembered(video));
        return [.. folders.Distinct(FileSystemPaths.Comparer)];
    }


    /// <summary>
    /// "Remove" in the tray: <paramref name="copy"/> of <paramref name="video"/> to the Trash, so it can be got back.
    /// Null when it went, or was gone already (or is no longer that frame's copy); else why it couldn't (no Trash on a
    /// network drive, say). Either way the tray reads again.
    /// </summary>
    public TrashRefusal? Remove(MarkedCopy copy, string video)
    {
        TrashRefusal? refusal = null;
        if (File.Exists(copy.Path) && FrameTags.ReadFile(copy.Path) == copy.Facts)
            refusal = _trash is null ? new TrashRefusal("trash.no_recycle_bin") : _trash(copy.Path);
        CopiesChanged?.Invoke(video);
        return refusal;
    }

    // ───────── The remembered folders (marked-frames.json) ─────────

    /// <summary>The folders copies of <paramref name="video"/> were saved to.</summary>
    internal List<string> Remembered(string video)
    {
        var key = Path.GetFullPath(video);
        lock (_remembering)
            return [.. Folders().Where(f => FileSystemPaths.Comparer.Equals(f.Video, key)).Select(f => f.Folder)];
    }

    /// <summary>
    /// Remembers that a copy of <paramref name="video"/> was saved in <paramref name="folder"/>: last in the list,
    /// once, the oldest dropped past <see cref="FolderLimit"/>.
    /// </summary>
    internal void Remember(string video, string folder)
    {
        var (key, place) = (Path.GetFullPath(video), Path.GetFullPath(folder));
        lock (_remembering)
        {
            var list = Folders();
            if (list.Count > 0 && FileSystemPaths.Comparer.Equals(list[^1].Video, key) && FileSystemPaths.Comparer.Equals(list[^1].Folder, place))
                return; // already the newest: nothing to write
            list.RemoveAll(f => FileSystemPaths.Comparer.Equals(f.Video, key) && FileSystemPaths.Comparer.Equals(f.Folder, place));
            list.Add((key, place));
            if (list.Count > FolderLimit)
                list.RemoveRange(0, list.Count - FolderLimit);
            Write(list);
        }
    }

    /// <summary>Forgets <paramref name="folder"/>, which is gone, for every video.</summary>
    private void Forget(string folder)
    {
        lock (_remembering)
        {
            var list = Folders();
            if (list.RemoveAll(f => FileSystemPaths.Comparer.Equals(f.Folder, folder)) > 0)
                Write(list);
        }
    }

    /// <summary>
    /// The list, read from the file on first use; a missing or damaged file reads as none. Under the lock.
    /// </summary>
    private List<(string Video, string Folder)> Folders()
    {
        if (_folders is not null)
            return _folders;
        _folders = [];
        if (_remembered is null || !File.Exists(_remembered))
            return _folders;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(_remembered)) is JsonArray array)
                foreach (var item in array.OfType<JsonObject>())
                    if (item["video"]?.GetValueKind() == System.Text.Json.JsonValueKind.String && item["folder"]?.GetValueKind() == System.Text.Json.JsonValueKind.String)
                        _folders.Add((item["video"]!.GetValue<string>(), item["folder"]!.GetValue<string>()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            _log?.LogWarning(ex, "Could not read the remembered folders of marked frames");
        }
        return _folders;
    }

    /// <summary>
    /// Writes the list (through a temporary file, so a crash never leaves half of it). Under the lock.
    /// </summary>
    private void Write(List<(string Video, string Folder)> list)
    {
        if (_remembered is null)
            return;
        try
        {
            var array = new JsonArray([.. list.Select(f => (JsonNode)new JsonObject { ["video"] = f.Video, ["folder"] = f.Folder })]);
            var temporary = _remembered + ".tmp";
            File.WriteAllText(temporary, array.ToJsonString());
            File.Move(temporary, _remembered, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning(ex, "Could not keep the remembered folders of marked frames");
        }
    }

    /// <summary>
    /// <paramref name="frame"/> is one of these frames and still wanted: false once it is discarded, and for every
    /// frame once Filee quits.
    /// </summary>
    public bool IsWanted(string frame) => !_quit && _frames.ContainsKey(frame);

    private volatile bool _quit;

    /// <summary>Deletes <paramref name="frame"/> and its folder; anything that isn't a frame is left alone.</summary>
    public void Discard(string frame)
    {
        if (!_frames.TryGetValue(frame, out var taken))
            return;
        taken.Counting.Cancel(); // its number isn't wanted any more
        // A folder still open somewhere (on Windows, while a program reads the file) stays known, so the next try
        // takes it, at the latest when Filee quits; failing that, the sweep at a later start
        // (JobQueue.DeleteStaleWorkFolders).
        var folder = Path.GetDirectoryName(frame)!;
        Filee.Engines.Archives.ArchiveFiles.TryDeleteFolder(folder);
        if (!Directory.Exists(folder))
            _frames.TryRemove(frame, out _);
    }

    /// <summary>
    /// A frame whose marked copy was saved, or whose saving was cancelled or skipped, is no longer needed; a failed one
    /// stays for "Try again" (until Filee quits).
    /// </summary>
    internal void OnJobFinished(ConversionJob job)
    {
        foreach (var file in job.Files)
        {
            if (file.State == FileState.Done && VideoOf(file.SourcePath) is { } video && job.Preset.Image.Frame is { } frame)
            {
                // Off the job queue's thread: the next conversion doesn't wait for folders being read.
                var outputs = file.Outputs.ToList();
                LastSaved = Task.Run(() => Saved(video, frame, outputs));
            }
            if (file.State is FileState.Done or FileState.Cancelled or FileState.Skipped)
                Discard(file.SourcePath);
        }
    }

    /// <summary>The handling of the last copy saved (for tests to wait on).</summary>
    internal Task LastSaved { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// A frame's copy was saved as <paramref name="outputs"/>: its folder is remembered, and the earlier copies of the
    /// same frame (same video, same time) that Annotate wrote, named as it names that frame's copy, go to the Trash:
    /// marking a frame again replaces it. A file of one's own that still carries the frame's text (a copy compressed
    /// or with its metadata edited, which skip the step that drops it) is left alone. The copy just saved is told apart
    /// ignoring case and Unicode form, so a disk that keeps another spelling of its name never loses it.
    /// </summary>
    private void Saved(string video, FrameFacts frame, IReadOnlyList<string> outputs)
    {
        // One save's clean-up at a time: two saves of one frame back to back each look through the same folders.
        lock (_replacing)
        {
            try
            {
                foreach (var output in outputs)
                    if (Path.GetDirectoryName(output) is { } folder)
                        Remember(video, folder);
                var saved = outputs.Select(SameFile).ToHashSet(StringComparer.Ordinal);
                // Only a copy written before this one is earlier: a later save's copy, there already when this save's
                // clean-up runs late, is left for that save to replace this one. Two written at the same time to the
                // disk's precision (FAT keeps 2 s) can't be told apart, and both stay: nothing newer is ever lost.
                var newest = outputs.Select(o => File.Exists(o) ? File.GetLastWriteTimeUtc(o) : DateTime.MinValue).DefaultIfEmpty(DateTime.MinValue).Max();
                // Annotate's names for this frame's copy: the one just saved, with or without a number for a taken name
                // ("Site walk 2m04.700s Annotated", "… Annotated (2)"); a file named otherwise is one's own.
                var names = outputs.Select(o => OutputPathResolver.WithoutCopyNumber(Path.GetFileNameWithoutExtension(o))).ToHashSet(StringComparer.OrdinalIgnoreCase);
                bool Named(string file) => names.Contains(OutputPathResolver.WithoutCopyNumber(Path.GetFileNameWithoutExtension(file)));
                if (_trash is not null)
                    foreach (var folder in Remembered(video))
                        foreach (var (file, earlier, written) in CopiesIn(folder, CancellationToken.None, Named, listing: false))
                            if (!saved.Contains(SameFile(file)) && written < newest
                                && FrameTags.SameVideo(earlier, frame.VideoName, frame.VideoSeconds, frame.VideoBytes)
                                && FrameTags.Milliseconds(earlier.FrameSeconds) == FrameTags.Milliseconds(frame.FrameSeconds)
                                && _trash(file) is { } refusal)
                                _log?.LogWarning("Could not move the earlier copy {File} to the Trash: {Reason}", file, refusal.ReasonKey);
                CopiesChanged?.Invoke(video);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log?.LogError(ex, "Could not finish saving a marked frame of {Video}", video);
            }
        }
    }

    /// <summary>A path as it compares to another spelling of the same file: full, composed, in one case.</summary>
    private static string SameFile(string path) => FrameTags.Composed(Path.GetFullPath(path)).ToUpperInvariant();

    private readonly Lock _replacing = new();

    private void OnJobFinished(object? sender, ConversionJob job) => OnJobFinished(job);

    /// <summary>Filee quits: every frame goes, and one still being taken is no longer wanted.</summary>
    public void Dispose()
    {
        _quit = true;
        if (_queue is not null)
            _queue.JobFinished -= OnJobFinished;
        foreach (var frame in _frames.Keys)
            Discard(frame);
    }
}
