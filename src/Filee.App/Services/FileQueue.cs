// The files waiting on the Home page to be converted together, in the order the user put them in. Any file can join: the
// donut offers the conversions that fit the files, as it does for files dropped anywhere else.

using Filee.Core.Platform;

namespace Filee.App.Services;

/// <summary>An ordered list of files, kept without duplicates.</summary>
internal sealed class FileQueue
{
    private readonly List<string> _paths = [];

    /// <summary>The files in the order they are converted.</summary>
    public IReadOnlyList<string> Paths => _paths;

    /// <summary>Adds <paramref name="paths"/> at the end, in order; a file already in the queue is not added twice.</summary>
    public void Add(IEnumerable<string> paths)
    {
        foreach (var path in paths)
            if (!_paths.Contains(path, FileSystemPaths.Comparer))
                _paths.Add(path);
    }

    /// <summary>Takes <paramref name="path"/> out.</summary>
    public bool Remove(string path)
    {
        var index = _paths.FindIndex(p => FileSystemPaths.Comparer.Equals(p, path));
        if (index < 0)
            return false;
        _paths.RemoveAt(index);
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

    public void Clear() => _paths.Clear();
}
