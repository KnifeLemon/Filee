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
    /// </summary>
    public static IReadOnlyList<T> Sort<T>(IEnumerable<T> items, Func<T, FileStamp> stamp, FileSortOrder order)
    {
        string Name(T item) => Path.GetFileName(stamp(item).FilePath);
        string Full(T item) => stamp(item).FilePath;

        IOrderedEnumerable<T> ByDate(Func<T, DateTime?> date, bool newestFirst)
        {
            var known = items.OrderBy(item => date(item) is null);
            var dated = newestFirst ? known.ThenByDescending(date) : known.ThenBy(date);
            return dated.ThenBy(Name, NaturalComparer.Instance).ThenBy(Full, NaturalComparer.Instance);
        }

        var sorted = order switch
        {
            FileSortOrder.NameDescending => items.OrderByDescending(Name, NaturalComparer.Instance).ThenByDescending(Full, NaturalComparer.Instance),
            FileSortOrder.ModifiedAscending => ByDate(item => stamp(item).Modified, newestFirst: false),
            FileSortOrder.ModifiedDescending => ByDate(item => stamp(item).Modified, newestFirst: true),
            FileSortOrder.CreatedAscending => ByDate(item => stamp(item).Created, newestFirst: false),
            FileSortOrder.CreatedDescending => ByDate(item => stamp(item).Created, newestFirst: true),
            _ => items.OrderBy(Name, NaturalComparer.Instance).ThenBy(Full, NaturalComparer.Instance),
        };
        return sorted.ToArray();
    }
}
