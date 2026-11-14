// Writes archives, all in-process: ZIP (and the game packs that are ZIPs, see GamePacks), 7Z, TAR, TAR.GZ, TAR.BZ2,
// TAR.XZ and single-file GZ / BZ2 / XZ (System.IO.Compression / System.Formats.Tar, and Filee's own SevenZipWriter,
// Bzip2Writer and XzWriter), so file names are exactly what Filee chooses: UTF-8 in ZIP (with the language encoding
// flag), UTF-16 in 7Z, PAX headers in TAR.

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Filee.Core.Presets;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Archives;

/// <summary>A file or folder to store in an archive under <paramref name="EntryName"/>; folders go in with their content.</summary>
internal sealed record PackItem(string SourcePath, string EntryName);

/// <summary>Creates archives from files and folders.</summary>
internal sealed class ArchivePacker
{
    /// <summary>Formats written without 7-Zip (all that Filee writes).</summary>
    public static readonly IReadOnlySet<string> BuiltInFormats =
        new HashSet<string>(["zip", "7z", "tar", "tgz", "tbz2", "txz", "gz", "bz2", "xz", .. GamePacks.Formats], StringComparer.OrdinalIgnoreCase);

    /// <summary>Formats that hold exactly one file, without name or folders.</summary>
    public static readonly IReadOnlySet<string> SingleFileFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "gz", "bz2", "xz" };

    /// <summary>
    /// Writes <paramref name="items"/> into a new archive of <paramref name="format"/> at <paramref name="outputPath"/>.
    /// The archive is built under a temporary name next to it and renamed when complete, so a failed or cancelled run
    /// never leaves a broken archive (or replaces an existing one).
    /// </summary>
    public async Task PackAsync(IReadOnlyList<PackItem> items, string outputPath, string format, ArchiveLevel level,
        string workDirectory, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(outputPath)}.filee-{Guid.NewGuid().ToString("N")[..8]}.tmp");
        try
        {
            switch (format)
            {
                case "zip" or "mcpack" or "mcaddon" or "mcworld" or "mctemplate" or "mrpack":
                    await Task.Run(() => WriteZip(items, temp, level, progress, cancellationToken), cancellationToken);
                    break;
                case "tar":
                case "tgz":
                case "tbz2":
                case "txz":
                    await Task.Run(() => WriteTar(items, temp, format, level, progress, cancellationToken), cancellationToken);
                    break;
                case "7z":
                    await Task.Run(() => WriteSevenZip(items, temp, level, progress, cancellationToken), cancellationToken);
                    break;
                case "gz":
                case "bz2":
                case "xz":
                    if (items.Count != 1 || !File.Exists(items[0].SourcePath))
                        throw new InvalidOperationException(
                            $"A .{format} file holds a single file. Choose TAR.{format.ToUpperInvariant()} to compress several files or folders.");
                    await Task.Run(() => WriteSingle(items[0].SourcePath, temp, format, level, progress, cancellationToken), cancellationToken);
                    break;
                default:
                    throw new NotSupportedException($"Filee cannot write {format} archives.");
            }
            File.Move(temp, outputPath, overwrite: true);
        }
        finally
        {
            ArchiveFiles.TryDeleteFile(temp);
        }
    }

    private static void WriteZip(IReadOnlyList<PackItem> items, string path, ArchiveLevel level, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var files = Expand(items).ToList();
        var counter = new ByteCounter(files.Sum(f => f.Length), progress);
        var compression = level switch
        {
            ArchiveLevel.Store => CompressionLevel.NoCompression,
            ArchiveLevel.Fast => CompressionLevel.Fastest,
            ArchiveLevel.Maximum => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal,
        };

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        // UTF-8 names get the "language encoding" flag, so every unzip tool shows Korean or Chinese names correctly.
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, new UTF8Encoding(false));
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.IsFolder)
            {
                zip.CreateEntry(file.EntryName + "/").LastWriteTime = ZipTime(file.Modified);
                continue;
            }
            var entry = zip.CreateEntry(file.EntryName, compression);
            entry.LastWriteTime = ZipTime(file.Modified);
            using var source = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            using var target = entry.Open();
            ArchiveFiles.CopyLimited(source, target, long.MaxValue, cancellationToken, counter.Add);
        }
    }

    /// <summary>
    /// A 7z archive (SevenZipWriter): folders first, then the files grouped by extension, so similar data shares a
    /// solid block and the dictionary.
    /// </summary>
    private static void WriteSevenZip(IReadOnlyList<PackItem> items, string path, ArchiveLevel level, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var files = Expand(items).ToList();
        if (files.Count > ArchiveSafety.EntryLimit)
            throw new InvalidOperationException(
                $"A 7z archive Filee writes holds at most {ArchiveSafety.EntryLimit:N0} files and folders; there are {files.Count:N0}.");
        var bytes = files.Sum(f => f.Length);
        var counter = new ByteCounter(bytes, progress);
        var ordered = files.Where(f => f.IsFolder)
            .Concat(files.Where(f => !f.IsFolder)
                .OrderBy(f => Path.GetExtension(f.EntryName), StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.EntryName, StringComparer.Ordinal));

        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        var writer = new SevenZipWriter(file, level, bytes, cancellationToken);
        foreach (var item in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 7z keeps times to 100 ns, so they are read again here: a folder listing can lag behind on NTFS.
            if (item.IsFolder)
            {
                writer.AddFolder(item.EntryName, FreshTime(() => Directory.GetLastWriteTimeUtc(item.Path), item.Modified),
                    SevenZipAttributes(item.Path, folder: true));
                continue;
            }
            using var source = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            writer.AddFile(item.EntryName, source, source.Length, FreshTime(() => File.GetLastWriteTimeUtc(source.SafeFileHandle), item.Modified),
                SevenZipAttributes(item.Path, folder: false), counter.Add);
        }
        writer.Finish();
    }

    private static DateTime FreshTime(Func<DateTime> read, DateTimeOffset listed)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return listed.UtcDateTime;
        }
    }

    /// <summary>
    /// 7z attributes: the Windows ones that matter (read-only, hidden, system, archive, directory) and the Unix mode in
    /// the high 16 bits: the file's own off Windows, rw-r--r-- / rwxr-xr-x on Windows (as the TAR writer).
    /// </summary>
    private static uint SevenZipAttributes(string path, bool folder)
    {
        const FileAttributes kept = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive;
        var attributes = folder ? FileAttributes.Directory : 0;
        var mode = folder ? 0x1EDu : 0x1A4u; // 0755, 0644
        try
        {
            attributes |= File.GetAttributes(path) & kept;
            if (!OperatingSystem.IsWindows())
                mode = (uint)File.GetUnixFileMode(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Gone or locked since it was listed: the defaults.
        }
        if (attributes.HasFlag(FileAttributes.ReadOnly))
            mode &= ~0x92u; // no write bits
        mode |= folder ? 0x4000u : 0x8000u; // S_IFDIR / S_IFREG
        return (uint)attributes | SevenZipWriter.UnixExtension | (mode << 16);
    }

    /// <summary>One file as a .gz, .bz2 or .xz, its time kept on the archive.</summary>
    private static void WriteSingle(string source, string path, string format, ArchiveLevel level, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
        {
            var length = Math.Max(1, input.Length);
            long done = 0;
            void Report(long read) => progress?.Report((done += read) / (double)length);
            if (format == "xz")
            {
                using var xz = new XzWriter(file, level, input.Length, cancellationToken, leaveOpen: true);
                ArchiveFiles.CopyLimited(input, xz, long.MaxValue, cancellationToken, Report);
                xz.Finish();
            }
            else if (format == "bz2")
            {
                using var bzip2 = new Bzip2Writer(file, level, cancellationToken, leaveOpen: true);
                ArchiveFiles.CopyLimited(input, bzip2, long.MaxValue, cancellationToken, Report);
                bzip2.Finish();
            }
            else
            {
                using var gzip = new GZipStream(file, GzipLevel(level), leaveOpen: true);
                ArchiveFiles.CopyLimited(input, gzip, long.MaxValue, cancellationToken, Report);
            }
        }
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(source));
    }

    private static CompressionLevel GzipLevel(ArchiveLevel level) => level switch
    {
        ArchiveLevel.Store => CompressionLevel.NoCompression,
        ArchiveLevel.Fast => CompressionLevel.Fastest,
        ArchiveLevel.Maximum => CompressionLevel.SmallestSize,
        _ => CompressionLevel.Optimal,
    };

    /// <summary>
    /// Writes a PAX TAR (UTF-8 names, times): plain for "tar", gzip-compressed for "tgz", bzip2-compressed for "tbz2",
    /// xz-compressed for "txz" (at <paramref name="level"/>; a plain TAR ignores it).
    /// </summary>
    private static void WriteTar(IReadOnlyList<PackItem> items, string path, string format, ArchiveLevel level,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var files = Expand(items).ToList();
        var bytes = files.Sum(f => f.Length);
        var counter = new ByteCounter(bytes, progress);

        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        XzWriter? xz = null;
        Bzip2Writer? bzip2 = null;
        using (var stream = format switch
        {
            "tgz" => new GZipStream(file, GzipLevel(level), leaveOpen: true),
            "tbz2" => bzip2 = new Bzip2Writer(file, level, cancellationToken, leaveOpen: true),
            // A PAX TAR adds about 1.5 KB of headers per entry; the estimate only sizes the dictionary.
            "txz" => xz = new XzWriter(file, level, bytes + files.Count * 1536L + 1024, cancellationToken, leaveOpen: true),
            _ => (Stream)new NonClosingStream(file),
        })
        {
            WriteTarEntries(files, stream, counter, cancellationToken);
            xz?.Finish();
            bzip2?.Finish();
        }
    }

    private static void WriteTarEntries(List<PackFile> files, Stream stream, ByteCounter counter, CancellationToken cancellationToken)
    {
        using var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true);
        foreach (var item in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.IsFolder)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, item.EntryName + "/")
                {
                    ModificationTime = item.Modified,
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                           | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
                });
                continue;
            }
            using var source = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            var entry = new PaxTarEntry(TarEntryType.RegularFile, item.EntryName)
            {
                ModificationTime = item.Modified,
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                DataStream = new CountingStream(source, counter.Add, cancellationToken),
            };
            writer.WriteEntry(entry);
        }
    }

    /// <summary>A file or folder in the order it is written, with its name inside the archive.</summary>
    private sealed record PackFile(string Path, string EntryName, bool IsFolder, long Length, DateTimeOffset Modified);

    /// <summary>
    /// Every file and folder of the items, folders before their content. Links are skipped, never followed, so
    /// nothing outside the given folders ends up in the archive.
    /// </summary>
    private static IEnumerable<PackFile> Expand(IReadOnlyList<PackItem> items)
    {
        foreach (var item in items)
        {
            var name = item.EntryName.Replace('\\', '/').Trim('/');
            if (File.Exists(item.SourcePath))
            {
                var info = new FileInfo(item.SourcePath);
                yield return new PackFile(info.FullName, name, false, info.Length, info.LastWriteTimeUtc);
            }
            else if (Directory.Exists(item.SourcePath))
            {
                foreach (var file in ExpandFolder(new DirectoryInfo(item.SourcePath), name))
                    yield return file;
            }
            else
            {
                throw new FileNotFoundException($"{item.SourcePath} was not found.", item.SourcePath);
            }
        }
    }

    private static IEnumerable<PackFile> ExpandFolder(DirectoryInfo folder, string name)
    {
        yield return new PackFile(folder.FullName, name, true, 0, folder.LastWriteTimeUtc);
        foreach (var info in folder.EnumerateFileSystemInfos().OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;
            var child = name + "/" + info.Name;
            if (info is DirectoryInfo dir)
            {
                foreach (var file in ExpandFolder(dir, child))
                    yield return file;
            }
            else if (info is FileInfo file)
            {
                yield return new PackFile(file.FullName, child, false, file.Length, file.LastWriteTimeUtc);
            }
        }
    }

    /// <summary>ZIP stores DOS times (local, 1980–2107).</summary>
    private static DateTimeOffset ZipTime(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        return local.Year < 1980 ? new DateTimeOffset(1980, 1, 1, 0, 0, 0, local.Offset)
            : local.Year > 2107 ? new DateTimeOffset(2107, 12, 31, 0, 0, 0, local.Offset)
            : local;
    }

    /// <summary>Turns bytes written into a 0..1 progress value.</summary>
    private sealed class ByteCounter(long total, IProgress<double>? progress)
    {
        private long _done;
        private double _reported;

        public void Add(long bytes)
        {
            _done += bytes;
            var value = total <= 0 ? 1 : Math.Min(1, _done / (double)total);
            if (value - _reported >= 0.01)
            {
                _reported = value;
                progress?.Report(value);
            }
        }
    }

    /// <summary>Read-only pass-through that reports bytes read and honours cancellation (TarWriter copies the data itself).</summary>
    private sealed class CountingStream(Stream inner, Action<long> onRead, CancellationToken cancellationToken) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = inner.Read(buffer);
            onRead(read);
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
