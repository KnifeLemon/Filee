// Sorts files by name or date, for the order they merge in: the merge order window and "filee convert --sort".

namespace Filee.Core.Platform;

/// <summary>An order files can be sorted in. Ascending is A to Z or oldest first.</summary>
public enum FileSortOrder
{
    NameAscending,
    NameDescending,
    ModifiedAscending,
    ModifiedDescending,
    CreatedAscending,
    CreatedDescending,
}

/// <summary>A file and its dates in UTC; a date is null when it can't be known (the file isn't there or can't be read).</summary>
public sealed record FileStamp(string FilePath, DateTime? Modified = null, DateTime? Created = null)
{
    /// <summary>Reads the dates of <paramref name="path"/>. Never throws for a missing or unreadable file.</summary>
    public static FileStamp Read(string path)
    {
        try
        {
            // A missing file reports the year 1601 rather than failing, which would sort it as the oldest.
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(path, info.LastWriteTimeUtc, info.CreationTimeUtc) : new FileStamp(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new FileStamp(path);
        }
    }
}

/// <summary>Sorts files; the same input always gives the same order, also when names or dates are equal.</summary>
public static class FileSorter
{
    /// <summary>
    /// Sorts <paramref name="items"/>. Names sort the way people count ("page 2" before "page 10", see
    /// <see cref="NaturalComparer"/>), with the full path deciding between equal names. Files with equal dates sort by
    /// name, and files without a date come last in both directions: they have no place on the timeline.
    /// With <paramref name="foldersTogether"/> the name is the whole path, in Explorer's order: each folder's files stay
    /// together, sub folders before files. For files from one folder tree, that is the order of their relative paths.
    /// </summary>
    public static IReadOnlyList<T> Sort<T>(IEnumerable<T> items, Func<T, FileStamp> stamp, FileSortOrder order, bool foldersTogether = false)
    {
        string Full(T item) => stamp(item).FilePath;
        string Name(T item) => foldersTogether ? Full(item) : Path.GetFileName(Full(item));
        IComparer<string> byName = foldersTogether ? FolderOrderComparer.Instance : NaturalComparer.Instance;

        IOrderedEnumerable<T> ByDate(Func<T, DateTime?> date, bool newestFirst)
        {
            var known = items.OrderBy(item => date(item) is null);
            var dated = newestFirst ? known.ThenByDescending(date) : known.ThenBy(date);
            return dated.ThenBy(Name, byName).ThenBy(Full, NaturalComparer.Instance);
        }

        var sorted = order switch
        {
            FileSortOrder.NameDescending => items.OrderByDescending(Name, byName).ThenByDescending(Full, NaturalComparer.Instance),
            FileSortOrder.ModifiedAscending => ByDate(item => stamp(item).Modified, newestFirst: false),
            FileSortOrder.ModifiedDescending => ByDate(item => stamp(item).Modified, newestFirst: true),
            FileSortOrder.CreatedAscending => ByDate(item => stamp(item).Created, newestFirst: false),
            FileSortOrder.CreatedDescending => ByDate(item => stamp(item).Created, newestFirst: true),
            _ => items.OrderBy(Name, byName).ThenBy(Full, NaturalComparer.Instance),
        };
        return sorted.ToArray();
    }

    /// <summary>Compares paths one folder level at a time, naturally, with a sub folder before a file at the same level.</summary>
    private sealed class FolderOrderComparer : IComparer<string>
    {
        public static readonly FolderOrderComparer Instance = new();
        private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

        public int Compare(string? x, string? y)
        {
            if (x is null || y is null)
                return string.CompareOrdinal(x, y);
            var a = x.Split(Separators);
            var b = y.Split(Separators);
            for (var level = 0; level < Math.Min(a.Length, b.Length); level++)
            {
                bool aIsFolder = level < a.Length - 1, bIsFolder = level < b.Length - 1;
                if (aIsFolder != bIsFolder)
                    return aIsFolder ? -1 : 1;
                var byName = NaturalComparer.Instance.Compare(a[level], b[level]);
                if (byName != 0)
                    return byName;
            }
            return a.Length.CompareTo(b.Length);
        }
    }
}
