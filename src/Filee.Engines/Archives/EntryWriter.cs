// Writes what an in-process archive reader finds into the target folder: entry names become safe paths inside it,
// clashing names are numbered as 7-Zip numbers them, sizes and CRC-32s are checked, hard links become copies of the
// file they name, links are never created, and folder times are set last (writing files into a folder changes its
// time).
//
// Small files are written on worker threads: creating a file costs the system (and a virus scanner looking at every
// new file) far more than the few KB in it, and archives of thousands of small files spend most of their time there.
// The reader's thread still reads each one whole, counts it against the budget and checks its size and CRC, so every
// error comes where it always did; it only hands the bytes over. Names are reserved as they are handed out, so the
// numbering of clashing names and "a file of that name came first" are decided as if every file were already there.
// Finish waits for the writes and reports the first one that failed; Dispose waits for them on the way out of an
// error.

using System.Buffers;
using System.Runtime.ExceptionServices;

namespace Filee.Engines.Archives;

/// <summary>The output side of Filee's own archive readers and the SharpCompress ones.</summary>
internal sealed class EntryWriter(string root, UnpackBudget budget, CancellationToken cancellationToken) : IDisposable
{
    /// <summary>Files up to this size are written on worker threads.</summary>
    private const int SmallFile = 1 << 20;

    /// <summary>Tests turn the worker threads off.</summary>
    internal static readonly AsyncLocal<bool?> TestOneThread = new();

    private readonly List<(string Path, DateTime? Modified)> _folders = [];
    private readonly UniquePaths _unique = new();
    private string? _lastFolder; // the folder the last file went into, known to exist

    /// <summary>Entry path (as <see cref="Key"/> spells it) → the file written for it, for hard links.</summary>
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    /// <summary>Every file path handed out, written or still being written.</summary>
    private readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<Task> _pending = [];
    private readonly SemaphoreSlim _slots = new(Math.Clamp(Environment.ProcessorCount * 2, 2, 32));
    private readonly bool _threads = !(TestOneThread.Value ?? Environment.ProcessorCount == 1);
    private readonly string _root = Path.GetFullPath(root);
    private ExceptionDispatchInfo? _failed;

    private string ArchiveName => budget.ArchiveName;

    /// <summary>Creates a folder entry ("./" and other empty names are the root itself and are left out).</summary>
    public void AddFolder(string name, DateTime? modified)
    {
        if (Key(name).Length == 0)
            return;
        var path = ArchiveSafety.ResolveEntryPath(root, name, ArchiveName);
        if (_reserved.Contains(path) || File.Exists(path))
            return; // a file of that name came first; what goes into the folder fails with a clear error later
        Directory.CreateDirectory(path);
        _folders.Add((path, modified));
    }

    /// <summary>
    /// Writes one file from <paramref name="data"/>, which must hold exactly <paramref name="size"/> bytes (-1: not
    /// known) with the CRC-32 <paramref name="crc"/> (null: none to check). Returns the file written (small files may
    /// still be on their way until <see cref="Finish"/>).
    /// </summary>
    public string AddFile(string name, Stream data, long size, uint? crc, DateTime? modified)
    {
        _failed?.Throw();
        var path = Target(name);
        if (_threads && size is > 0 and <= SmallFile)
            WriteLater(name, path, data, (int)size, crc, modified);
        else
            WriteNow(name, path, data, size, crc, modified);
        _files[Key(name)] = path;
        return path;
    }

    private void WriteNow(string name, string path, Stream data, long size, uint? crc, DateTime? modified)
    {
        var checksum = crc is null ? null : new Crc32();
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        Stream output = checksum is null ? file : new CrcStream(new NonClosingStream(file), checksum);
        var written = ArchiveFiles.CopyLimited(data, output, size >= 0 ? size : long.MaxValue, cancellationToken, budget: budget);
        Check(name, size, written, crc, checksum);
        // The time through the open file (after the last write reached it): no second open per file.
        file.Flush();
        SetTime(file, modified);
    }

    /// <summary>Reads the file here (budget, size and CRC checked as it is read) and writes it on a worker thread.</summary>
    private void WriteLater(string name, string path, Stream data, int size, uint? crc, DateTime? modified)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(size + 1);
        try
        {
            var checksum = crc is null ? null : new Crc32();
            using var memory = new MemoryStream(buffer, 0, buffer.Length, writable: true);
            Stream output = checksum is null ? memory : new CrcStream(new NonClosingStream(memory), checksum);
            var written = ArchiveFiles.CopyLimited(data, output, size, cancellationToken, budget: budget);
            Check(name, size, written, crc, checksum);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
        _slots.Wait(cancellationToken);
        _pending.Add(Task.Run(() =>
        {
            try
            {
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1);
                file.Write(buffer, 0, size);
                SetTime(file, modified);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                _slots.Release();
            }
        }, CancellationToken.None));
        if (_pending.Count >= 256)
            Collect();
    }

    private void Check(string name, long size, long written, uint? crc, Crc32? checksum)
    {
        if (size >= 0 && written != size)
            throw ArchiveSafety.Damaged(ArchiveName, $"{name} is shorter than the archive says");
        if (crc is { } expected && checksum!.Value != expected)
            throw ArchiveSafety.Damaged(ArchiveName, $"checksum error in {name}");
    }

    /// <summary>Forgets the writes that are done; the first one that failed is kept, to be thrown.</summary>
    private void Collect()
    {
        _pending.RemoveAll(task =>
        {
            if (!task.IsCompleted)
                return false;
            if (task.Exception is { } error && _failed is null)
                _failed = ExceptionDispatchInfo.Capture(error.InnerException ?? error);
            return true;
        });
    }

    /// <summary>Waits for every write on the worker threads; throws the first that failed.</summary>
    private void Drain()
    {
        try
        {
            Task.WaitAll(_pending);
        }
        catch (AggregateException)
        {
            // Collected below.
        }
        Collect();
        _failed?.Throw();
    }

    /// <summary>
    /// A hard link: a copy of the file <paramref name="target"/> unpacked before. Nothing is written when that file
    /// was not unpacked (a link itself, or not in the archive).
    /// </summary>
    public void AddHardLink(string name, string target, DateTime? modified)
    {
        if (!_files.TryGetValue(Key(target), out var source))
            return;
        Drain(); // the file it copies may still be on its way
        using var data = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        AddFile(name, data, data.Length, null, modified);
    }

    /// <summary>A copy, under <paramref name="name"/>, of the file <paramref name="written"/> that AddFile returned.</summary>
    public void AddCopy(string name, string written, DateTime? modified)
    {
        Drain(); // it may still be on its way
        using var data = new FileStream(written, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        AddFile(name, data, data.Length, null, modified);
    }

    /// <summary>Waits for the files still being written and sets the folder times; call after the last entry.</summary>
    public void Finish()
    {
        Drain();
        foreach (var (path, modified) in _folders)
            if (modified is { } time)
                Directory.SetLastWriteTime(path, time);
    }

    /// <summary>Waits for the files still being written (their errors are dropped: something failed already, or Finish reported them).</summary>
    public void Dispose()
    {
        try
        {
            Drain();
        }
        catch (Exception)
        {
            // On the way out of another error, or reported by Finish already.
        }
        _slots.Dispose();
    }

    /// <summary>The entry path with "/" separators and without "." and empty parts.</summary>
    public static string Key(string name) =>
        string.Join('/', name.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Where(part => part != "."));

    private string Target(string name)
    {
        if (Key(name).Length == 0)
            throw ArchiveSafety.Damaged(ArchiveName, "an entry has no name");
        var path = ArchiveSafety.ResolveEntryPath(root, name, ArchiveName);
        var folder = Path.GetDirectoryName(path)!;
        if (folder != _lastFolder)
        {
            // A file still being written where a folder must go: let it land, so making the folder fails as it would.
            for (var up = folder; up.Length > _root.Length && _pending.Count > 0; up = Path.GetDirectoryName(up) ?? "")
            {
                if (_reserved.Contains(up))
                {
                    Drain();
                    break;
                }
            }
            Directory.CreateDirectory(folder);
            _lastFolder = folder;
        }
        var unique = _unique.Get(path, _reserved);
        _reserved.Add(unique);
        return unique;
    }

    private static void SetTime(FileStream file, DateTime? modified)
    {
        if (modified is not { } time || time.Year < 1601 || time.Year > 9999)
            return;
        try
        {
            File.SetLastWriteTime(file.SafeFileHandle, time);
        }
        catch (ArgumentOutOfRangeException) { }
    }
}
