// One selection in Explorer can reach Filee as many launches: the classic menu entry ("%1" with MultiSelectModel
// "Player", the only entry on Windows 10) starts one Filee per selected file, in no fixed order and over many seconds
// (with 100 files, gaps of up to 0.7 s on a fast computer). No wait can tell which one is the last. So the
// first launch asks the file manager what is selected: the donut gets all of it at once, whenever the user clicks, and
// the launches still on their way bring nothing new.

using Filee.Core.Platform;

namespace Filee.App.Services;

/// <summary>Turns the launches of one file manager selection into one list of files. Used on the UI thread.</summary>
/// <param name="readSelection">The files selected in the foreground file manager window (empty when unknown).</param>
/// <param name="time">Clock (tests use a fake one).</param>
internal sealed class SelectionLaunches(Func<IReadOnlyList<string>> readSelection, TimeProvider? time = null)
{
    /// <summary>A launch this long after the last one is a new request, so it is worth asking for the selection.</summary>
    private static readonly TimeSpan NewRequestAfter = TimeSpan.FromSeconds(1);

    /// <summary>After this long without a launch, the ones still expected aren't coming (they could not reach Filee).</summary>
    private static readonly TimeSpan ExpectFor = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly HashSet<string> _expected = new(FileSystemPaths.Comparer);
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    /// <summary>
    /// A launch brought <paramref name="files"/>. Returns what it adds: the whole selection when it is the first launch
    /// of one, nothing when an earlier launch already brought its file, otherwise its own files.
    /// </summary>
    public IReadOnlyList<string> Arrived(IReadOnlyList<string> files)
    {
        var now = _time.GetUtcNow();
        var quiet = now - _last;
        _last = now;
        if (quiet > ExpectFor)
            _expected.Clear();
        var added = files.Where(file => !_expected.Remove(file)).ToList();
        // Several files in one launch are a whole selection already (Send To, the Windows 11 entry).
        if (files.Count != 1 || added.Count != 1 || _expected.Count > 0 || quiet < NewRequestAfter)
            return added;
        var selection = readSelection();
        if (selection.Count < 2 || !selection.Contains(added[0], FileSystemPaths.Comparer))
            return added;
        _expected.UnionWith(selection);
        _expected.Remove(added[0]);
        return selection;
    }
}
