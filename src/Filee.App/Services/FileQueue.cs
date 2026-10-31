// The files waiting on the Home page to be converted together. The first file fixes the type: only files of the same
// format join the queue, and the order shown is the order the files are converted (or merged) in.

using Filee.Core.Formats;
using Filee.Core.Platform;

namespace Filee.App.Services;

/// <summary>Why a file didn't join the queue.</summary>
internal enum QueueRefusal
{
    /// <summary>Filee doesn't know the format, so it can't convert the file.</summary>
    Unsupported,

    /// <summary>The queue holds another format; the first file's format decides.</summary>
    OtherFormat,
}

/// <summary>An ordered list of files of one format (<see cref="Format"/>), kept without duplicates.</summary>
internal sealed class FileQueue
{
    private readonly List<string> _paths = [];

    /// <summary>The format every file in the queue has; null while the queue is empty.</summary>
    public FileFormat? Format { get; private set; }

    /// <summary>The files in the order they are converted.</summary>
    public IReadOnlyList<string> Paths => _paths;

    /// <summary>
    /// Adds <paramref name="paths"/> in order. Files of another format, or of a format Filee doesn't know, are left out
    /// and reported in the result; the queue's format is set by the first file added to an empty queue.
    /// </summary>
    public IReadOnlyList<(string Path, QueueRefusal Reason)> Add(IEnumerable<string> paths)
    {
        var refused = new List<(string, QueueRefusal)>();
        foreach (var path in paths)
        {
            if (_paths.Contains(path, FileSystemPaths.Comparer))
                continue;
            var format = FormatRegistry.Detect(path);
            if (format is null)
            {
                refused.Add((path, QueueRefusal.Unsupported));
                continue;
            }
            if (Format is { } queued && format.Id != queued.Id)
            {
                refused.Add((path, QueueRefusal.OtherFormat));
                continue;
            }
            Format = format;
            _paths.Add(path);
        }
        return refused;
    }

    /// <summary>Takes <paramref name="path"/> out. An empty queue forgets its format.</summary>
    public bool Remove(string path)
    {
        var index = _paths.FindIndex(p => FileSystemPaths.Comparer.Equals(p, path));
        if (index < 0)
            return false;
        _paths.RemoveAt(index);
        if (_paths.Count == 0)
            Format = null;
        return true;
    }

    /// <summary>Moves the file at <paramref name="from"/> to position <paramref name="to"/>.</summary>
    public void Move(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= _paths.Count || to >= _paths.Count)
            return;
        var path = _paths[from];
        _paths.RemoveAt(from);
        _paths.Insert(to, path);
    }

    public void Clear()
    {
        _paths.Clear();
        Format = null;
    }
}
