// Unpacks disk images in-process: raw disk images (.img), Apple disk images (.dmg, UDIF) and UDF discs (.iso).
//
//   Disk: a DMG's blocks are read through DiscUtils.Dmg (raw, zero, zlib, bzip2 and ADC runs; hdiutil's zero-fill
//     runs relabelled as zero runs) once UdifCheck has passed its trailer and block tables; a .img is the disk itself. A .img that is really a CD or DVD image (ISO
//     9660 or UDF at sector 16) goes to the disc readers.
//   Partitions: GPT, Apple partition map or MBR (DiskImagePartitions), or one volume over the whole disk.
//   File systems: FAT12/16/32 by Filee's own reader (DiskImageFat: long names), UDF through DiscUtils.Udf (UDF 1.02 to
//     2.60, plain and metadata partitions), HFS+ through DiscUtils.HfsPlus. A volume of another known kind (NTFS,
//     exFAT, APFS, HFSX, classic HFS, ext2/3/4, ISO 9660 inside a partition) sends the whole image to 7-Zip, so nothing
//     is left out quietly; encrypted volumes (BitLocker, LUKS, FileVault DMGs) are refused. Unknown partitions (swap,
//     Microsoft reserved, empty ones) are skipped. With more than one volume to unpack each gets its own folder,
//     "<number> <label>".
//
// The libraries only ever read through a DiskImageStream, with a budget per call, and all their exceptions become
// Filee's messages (damage, or NotSupportedException for what 7-Zip may still read). The listing goes through
// ArchiveSafety.CheckListing and the writing through EntryWriter like every other in-process reader; symbolic links,
// devices and NTFS-style reparse points are left out.

using DiscUtils;
using DiscUtils.HfsPlus;
using DiscUtils.Streams;
using DiscUtils.Udf;

namespace Filee.Engines.Archives;

/// <summary>One file or folder found on a volume; <see cref="Open"/> reads a file's data.</summary>
internal sealed record DiskImageNode(string Path, bool IsFolder, bool IsLink, long Size, DateTime? Modified, Func<Stream>? Open);

/// <summary>Unpacks raw disk images, DMGs and UDF discs.</summary>
internal static class DiskImageReader
{
    private const int MaxDepth = 128;

    /// <summary>What lies on a volume.</summary>
    private enum Kind
    {
        Unknown,
        Fat,
        Udf,
        HfsPlus,
    }

    /// <summary>Unpacks a .img or .dmg file.</summary>
    public static void Extract(string image, string format, string folder, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(image);
        using var file = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var guard = new DiskImageGuard(name, cancellationToken);
        if (format == "dmg")
        {
            if (UdifCheck.IsEncrypted(file))
                throw ArchiveSafety.Encrypted(name);
            if (UdifCheck.Check(file, name) is { } udif)
            {
                using var disk = Library(name, () => new DiscUtils.Dmg.Disk(udif, Ownership.None));
                var content = Library(name, () => disk.Content);
                // A guard of its own that never runs out: it only turns DiscUtils.Dmg's errors into Filee's, the
                // volumes' guard above it counts.
                var blocks = new DiskImageGuard(name, cancellationToken);
                blocks.Allow(long.MaxValue);
                ExtractDisk(new DiskImageStream(content, 0, content.Length, blocks), file.Length, name, folder, guard, cancellationToken);
                return;
            }
        }
        if (Disc(file, name) is { } disc)
        {
            if (disc == Kind.Udf)
                ExtractVolumes([(new DiskVolume(1, 0, file.Length, null), Kind.Udf)], file, file.Length, name, folder, guard, cancellationToken);
            else
                IsoReader.Extract(image, folder, cancellationToken);
            return;
        }
        ExtractDisk(file, file.Length, name, folder, guard, cancellationToken);
    }

    /// <summary>Unpacks a UDF disc (an .iso whose volume recognition area names NSR02 or NSR03).</summary>
    public static void ExtractUdf(string image, string folder, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(image);
        using var file = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        ExtractVolumes([(new DiskVolume(1, 0, file.Length, null), Kind.Udf)], file, file.Length, name, folder,
            new DiskImageGuard(name, cancellationToken), cancellationToken);
    }

    /// <summary>UDF or ISO 9660 at sector 16 of a 2048-byte-sector disc, or null.</summary>
    private static Kind? Disc(Stream file, string name)
    {
        var descriptor = DiskImagePartitions.ReadAt(file, 16 * 2048, 6);
        if (descriptor.Length < 6)
            return null;
        var id = descriptor.AsSpan(1, 5);
        if (id.SequenceEqual("BEA01"u8) || id.SequenceEqual("NSR02"u8) || id.SequenceEqual("NSR03"u8))
            return Kind.Udf;
        if (!id.SequenceEqual("CD001"u8))
            return null;
        // A bridge disc: ISO 9660 first, then the UDF descriptors; UDF has the full names and large files.
        return Library(name, () => UdfReader.Detect(file)) ? Kind.Udf : Kind.Unknown;
    }

    private static void ExtractDisk(Stream disk, long archiveLength, string name, string folder, DiskImageGuard guard,
        CancellationToken cancellationToken)
    {
        var volumes = DiskImagePartitions.Find(disk, name);
        var readable = new List<(DiskVolume Volume, Kind Kind)>();
        string? unsupported = null;
        foreach (var volume in volumes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            guard.Allow(DiskImageGuard.StructureBudget);
            var (kind, other) = Identify(new DiskImageStream(disk, volume.Offset, volume.Length, guard), name);
            if (kind != Kind.Unknown)
                readable.Add((volume, kind));
            unsupported ??= other;
        }
        if (unsupported is not null)
            throw new NotSupportedException($"the {unsupported} file system");
        if (readable.Count == 0)
            throw new NotSupportedException("no file system Filee's built-in reader knows");
        ExtractVolumes(readable, disk, archiveLength, name, folder, guard, cancellationToken);
    }

    /// <summary>
    /// What a volume holds: a kind Filee reads, or the name of one it leaves to 7-Zip. Encrypted volumes are refused.
    /// </summary>
    private static (Kind Kind, string? Other) Identify(Stream volume, string name)
    {
        var head = DiskImagePartitions.ReadAt(volume, 0, 4096);
        if (head.Length < 512)
            return (Kind.Unknown, null);
        var oem = head.AsSpan(3, 8);
        if (oem.SequenceEqual("-FVE-FS-"u8) || head.AsSpan(0, 6).SequenceEqual((ReadOnlySpan<byte>)[0x4C, 0x55, 0x4B, 0x53, 0xBA, 0xBE]))
            throw ArchiveSafety.Encrypted(name);
        if (oem.SequenceEqual("NTFS    "u8))
            return (Kind.Unknown, "NTFS");
        if (oem.SequenceEqual("EXFAT   "u8))
            return (Kind.Unknown, "exFAT");
        if (DiskImageFat.Detect(head))
            return (Kind.Fat, null);
        if (head.Length >= 1026)
        {
            var signature = head.AsSpan(1024, 2);
            if (signature.SequenceEqual("H+"u8))
                return (Kind.HfsPlus, null);
            if (signature.SequenceEqual("HX"u8))
                return (Kind.Unknown, "HFSX");
            if (signature.SequenceEqual("BD"u8))
                return (Kind.Unknown, "HFS");
        }
        if (head.Length >= 36 && head.AsSpan(32, 4).SequenceEqual("NXSB"u8))
            return (Kind.Unknown, "APFS");
        if (head.Length >= 1082 && head[1080] == 0x53 && head[1081] == 0xEF)
            return (Kind.Unknown, "ext2/3/4");
        if (Disc(volume, name) is { } disc)
            return disc == Kind.Udf ? (Kind.Udf, null) : (Kind.Unknown, "ISO 9660");
        return (Kind.Unknown, null);
    }

    private static void ExtractVolumes(List<(DiskVolume Volume, Kind Kind)> volumes, Stream disk, long archiveLength, string name,
        string folder, DiskImageGuard guard, CancellationToken cancellationToken)
    {
        var nodes = new List<DiskImageNode>();
        var names = new NameTally(name);
        var opened = new List<IDisposable>();
        try
        {
            foreach (var (volume, kind) in volumes)
            {
                var stream = new DiskImageStream(disk, volume.Offset, volume.Length, guard);
                var start = nodes.Count;
                string? label;
                if (kind == Kind.Fat)
                {
                    guard.Allow(long.MaxValue); // Filee's own reader bounds itself
                    var fat = new DiskImageFat(stream, name);
                    fat.Walk("", nodes, names, cancellationToken);
                    label = fat.Label ?? volume.Name ?? fat.Type;
                }
                else
                {
                    guard.Allow(DiskImageGuard.StructureBudget);
                    DiscFileSystem fs = kind == Kind.Udf
                        ? Library(name, () => new UdfReader(stream))
                        : Library(name, () => new HfsPlusFileSystem(stream));
                    opened.Add(fs);
                    Walk(fs, kind == Kind.HfsPlus, nodes, names, guard, cancellationToken);
                    label = Library(name, () => fs.VolumeLabel) is { Length: > 0 } l ? l : volume.Name ?? (kind == Kind.Udf ? "UDF" : "HFS+");
                }
                if (volumes.Count > 1)
                {
                    var top = $"{volume.Number} {label.Replace('/', '_').Replace('\\', '_').Trim()}".TrimEnd();
                    nodes.Insert(start, new DiskImageNode(top, true, false, 0, null, null));
                    for (var i = start + 1; i < nodes.Count; i++)
                        nodes[i] = nodes[i] with { Path = top + "/" + nodes[i].Path };
                }
            }

            ArchiveSafety.CheckListing([.. nodes.Select(n => new ArchiveItem(n.Path, n.IsFolder, n.IsFolder || n.IsLink ? 0 : n.Size, IsLink: n.IsLink))],
                name, archiveLength, folder);
            var writer = new EntryWriter(folder, new UnpackBudget(name, archiveLength, folder), cancellationToken);
            foreach (var node in nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (node.IsFolder)
                    writer.AddFolder(node.Path, node.Modified);
                else if (!node.IsLink && node.Open is { } open)
                {
                    guard.Allow(node.Size + DiskImageGuard.StructureBudget);
                    using var data = new LibraryStream(Library(name, open), name);
                    writer.AddFile(node.Path, data, node.Size, null, node.Modified);
                }
            }
            writer.Finish();
        }
        finally
        {
            foreach (var item in opened)
                item.Dispose();
        }
    }

    /// <summary>
    /// Lists a DiscUtils file system. Its paths use "\" ("\folder\file"); each call may read up to
    /// <see cref="DiskImageGuard.StructureBudget"/>.
    /// </summary>
    /// <param name="decomposed">HFS+: names are stored decomposed (NFD) and are composed for the files written.</param>
    private static void Walk(DiscFileSystem fs, bool decomposed, List<DiskImageNode> nodes, NameTally names, DiskImageGuard guard,
        CancellationToken cancellationToken)
    {
        var name = guard.ArchiveName;
        var stack = new Stack<(string FsPath, string Path, int Depth)>();
        stack.Push(("", "", 0));
        while (stack.TryPop(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            guard.Allow(DiskImageGuard.StructureBudget);
            var entries = Library(name, () => fs.GetFileSystemEntries(item.FsPath));
            foreach (var entry in entries)
            {
                if (nodes.Count >= ArchiveSafety.EntryLimit)
                    throw ArchiveSafety.TooManyEntries(name);
                var leaf = entry[(entry.LastIndexOf('\\') + 1)..];
                if (leaf.Length == 0 || leaf is "." or "..")
                    continue;
                if (decomposed)
                    leaf = leaf.Normalize(System.Text.NormalizationForm.FormC);
                var path = (item.Path.Length == 0 ? "" : item.Path + "/") + leaf.Replace('/', '_');
                names.Add(path);
                guard.Allow(DiskImageGuard.StructureBudget);
                var attributes = Library(name, () => fs.GetAttributes(entry));
                var modified = Library(name, () => Time(fs.GetLastWriteTimeUtc(entry)));
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    nodes.Add(new DiskImageNode(path, false, true, 0, modified, null));
                    continue;
                }
                // The record type decides, not the attributes: HFS+ folders made without Unix permissions have none.
                if (Library(name, () => fs.DirectoryExists(entry)))
                {
                    if (item.Depth >= MaxDepth)
                        throw ArchiveSafety.Damaged(name, "the folders are nested too deeply");
                    nodes.Add(new DiskImageNode(path, true, false, 0, modified, null));
                    stack.Push((entry, path, item.Depth + 1));
                    continue;
                }
                var size = Library(name, () => fs.GetFileLength(entry));
                var fsPath = entry;
                nodes.Add(new DiskImageNode(path, false, false, size, modified, () => fs.OpenFile(fsPath, FileMode.Open, FileAccess.Read)));
            }
        }
    }

    /// <summary>A library's time (UTC, or unspecified as UTC) as local time; null outside what a file can carry.</summary>
    private static DateTime? Time(DateTime time)
    {
        if (time.Year is < 1980 or > 9999)
            return null;
        return (time.Kind == DateTimeKind.Local ? time : DateTime.SpecifyKind(time, DateTimeKind.Utc).ToLocalTime());
    }

    /// <summary>
    /// Runs a call into DiscUtils: its exceptions become Filee's (damage; NotSupportedException for features it lacks,
    /// which 7-Zip may read). Filee's own refusals and cancellation pass unchanged.
    /// </summary>
    private static T Library<T>(string name, Func<T> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex) when (Translate(ex, name) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>The exception to throw instead of <paramref name="ex"/>, or null to let it pass.</summary>
    internal static Exception? Translate(Exception ex, string name) => ex switch
    {
        OperationCanceledException => null,
        _ when ArchiveSafety.IsRefusal(ex) => null,
        InvalidDataException when name.Length > 0 && ex.Message.Contains(name, StringComparison.Ordinal) => null,
        InsufficientExecutionStackException => ArchiveSafety.Damaged(name, "its file system structures nest too deeply"),
        OutOfMemoryException => ArchiveSafety.Damaged(name, "a file system structure is larger than the memory allows"),
        NotImplementedException or NotSupportedException => new NotSupportedException(ex.Message.TrimEnd('.'), ex),
        _ => ArchiveSafety.Damaged(name, ex.Message.TrimEnd('.')),
    };

    /// <summary>A library's file stream whose read errors become Filee's messages (write errors stay what they are).</summary>
    private sealed class LibraryStream(Stream inner, string name) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Library(name, () => inner.Read(buffer, offset, count));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
