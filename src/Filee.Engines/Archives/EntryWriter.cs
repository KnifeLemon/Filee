// Writes what an in-process archive reader finds into the target folder: entry names become safe paths inside it,
// clashing names are numbered as 7-Zip numbers them, sizes and CRC-32s are checked, hard links become copies of the
// file they name, links are never created, and folder times are set last (writing files into a folder changes its
// time).

namespace Filee.Engines.Archives;

/// <summary>The output side of Filee's own archive readers and the SharpCompress ones.</summary>
internal sealed class EntryWriter(string root, UnpackBudget budget, CancellationToken cancellationToken)
{
    private readonly List<(string Path, DateTime? Modified)> _folders = [];

    /// <summary>Entry path (as <see cref="Key"/> spells it) → the file written for it, for hard links.</summary>
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    private string ArchiveName => budget.ArchiveName;

    /// <summary>Creates a folder entry ("./" and other empty names are the root itself and are left out).</summary>
    public void AddFolder(string name, DateTime? modified)
    {
        if (Key(name).Length == 0)
            return;
        var path = ArchiveSafety.ResolveEntryPath(root, name, ArchiveName);
        if (File.Exists(path))
            return; // a file of that name came first; what goes into the folder fails with a clear error later
        Directory.CreateDirectory(path);
        _folders.Add((path, modified));
    }

    /// <summary>
    /// Writes one file from <paramref name="data"/>, which must hold exactly <paramref name="size"/> bytes (-1: not
    /// known) with the CRC-32 <paramref name="crc"/> (null: none to check). Returns the file written.
    /// </summary>
    public string AddFile(string name, Stream data, long size, uint? crc, DateTime? modified)
    {
        var path = Target(name);
        var checksum = crc is null ? null : new Crc32();
        long written;
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
        {
            Stream output = checksum is null ? file : new CrcStream(new NonClosingStream(file), checksum);
            written = ArchiveFiles.CopyLimited(data, output, size >= 0 ? size : long.MaxValue, cancellationToken, budget: budget);
        }
        if (size >= 0 && written != size)
            throw ArchiveSafety.Damaged(ArchiveName, $"{name} is shorter than the archive says");
        if (crc is { } expected && checksum!.Value != expected)
            throw ArchiveSafety.Damaged(ArchiveName, $"checksum error in {name}");
        SetTime(path, modified);
        _files[Key(name)] = path;
        return path;
    }

    /// <summary>
    /// A hard link: a copy of the file <paramref name="target"/> unpacked before. Nothing is written when that file
    /// was not unpacked (a link itself, or not in the archive).
    /// </summary>
    public void AddHardLink(string name, string target, DateTime? modified)
    {
        if (!_files.TryGetValue(Key(target), out var source))
            return;
        using var data = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        AddFile(name, data, data.Length, null, modified);
    }

    /// <summary>Sets the folder times; call after the last entry.</summary>
    public void Finish()
    {
        foreach (var (path, modified) in _folders)
            if (modified is { } time)
                Directory.SetLastWriteTime(path, time);
    }

    /// <summary>The entry path with "/" separators and without "." and empty parts.</summary>
    public static string Key(string name) =>
        string.Join('/', name.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Where(part => part != "."));

    private string Target(string name)
    {
        if (Key(name).Length == 0)
            throw ArchiveSafety.Damaged(ArchiveName, "an entry has no name");
        var path = ArchiveSafety.ResolveEntryPath(root, name, ArchiveName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return ArchiveFiles.UniquePath(path);
    }

    private static void SetTime(string path, DateTime? modified)
    {
        if (modified is not { } time || time.Year < 1601 || time.Year > 9999)
            return;
        try
        {
            File.SetLastWriteTime(path, time);
        }
        catch (ArgumentOutOfRangeException) { }
    }
}
