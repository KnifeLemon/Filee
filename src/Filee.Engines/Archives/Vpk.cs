// Valve pak files (.vpk), the archives the Source engine keeps its games' files in (Half-Life 2, Counter-Strike: Source,
// Team Fortress 2, the content Garry's Mod mounts). Read by Filee; layout (little endian) as in Valve's vpklib and
// srctools:
//
//   signature u32 0x55AA1234, version u32 (1 or 2), tree size u32
//   version 2: file data size, archive MD5 size, other MD5 size, signature size (u32 each; the sections are not read)
//   the tree: extensions, in each its folders, in each its file names (NUL-terminated; an empty string ends each level,
//             " " stands for no extension or no folder); per file a CRC-32 u32, preload size u16, archive index u16,
//             offset u32, length u32 and 0xFFFF, then the preload bytes
//   archive index 0x7FFF: the file's data follows the tree in the directory file itself; any other index n: the data
//   is in "<name>_nnn.vpk" next to "<name>_dir.vpk"
//
// A file's content is its preload bytes followed by its data. The PS Vita's .vpk is a ZIP and goes to 7-Zip instead.

using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Filee.Engines.Archives;

/// <summary>Unpacks Valve pak files.</summary>
internal static partial class Vpk
{
    private const uint Signature = 0x55AA1234;

    /// <summary>The archive index of data stored in the directory file after the tree.</summary>
    private const ushort InDirectory = 0x7FFF;

    /// <summary>The longest name Filee reads from the tree (longer is a damaged file).</summary>
    private const int MaxNameBytes = 4096;

    /// <summary>The largest tree Filee reads (Valve's largest, Team Fortress 2's, is about 20 MB).</summary>
    private const uint MaxTreeBytes = 256 * 1024 * 1024;

    /// <summary>Data parts are numbered "_000" to "_999".</summary>
    private const int MaxParts = 1000;

    /// <summary>One file of a pak.</summary>
    internal sealed record Entry(string Name, uint Crc, byte[] Preload, int Archive, long Offset, long Length)
    {
        public long Size => Preload.Length + Length;
    }

    /// <summary>A data part of a split pak ("pak01_003.vpk"), which holds no tree.</summary>
    [GeneratedRegex(@"^(?<prefix>.+)_(?<part>\d{3})\.vpk$", RegexOptions.IgnoreCase)]
    private static partial Regex DataPart();

    /// <summary>True for a ZIP (a PS Vita package), which 7-Zip reads.</summary>
    public static bool IsZip(string path)
    {
        Span<byte> start = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        return stream.ReadAtLeast(start, 4, throwOnEndOfStream: false) == 4 && start.SequenceEqual("PK\x03\x04"u8);
    }

    /// <summary>Unpacks every file of the pak <paramref name="archive"/> (its directory file) into <paramref name="targetDirectory"/>.</summary>
    public static void Extract(string archive, string targetDirectory, CancellationToken cancellationToken)
    {
        var archiveName = Path.GetFileName(archive);
        using var directory = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var (entries, dataStart) = ReadTree(directory, archiveName);
        // Every name is checked before anything is written: one that leaves the folder stops the whole pak.
        foreach (var entry in entries)
            ArchiveSafety.ResolveEntryPath(targetDirectory, entry.Name, archiveName);
        ArchiveSafety.EnsureRoom(entries.Sum(e => e.Size), targetDirectory, archiveName);

        var parts = new Dictionary<int, FileStream>();
        try
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (stream, start) = entry.Archive == InDirectory
                    ? (directory, dataStart + entry.Offset)
                    : (Part(archive, entry.Archive, parts, archiveName), entry.Offset);
                if (entry.Length > 0 && start + entry.Length > stream.Length)
                    throw ArchiveSafety.Damaged(archiveName, $"{entry.Name} is cut off");

                var path = ArchiveSafety.ResolveEntryPath(targetDirectory, entry.Name, archiveName);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                path = ArchiveFiles.UniquePath(path);
                var crc = new Crc32();
                using (var output = new CrcStream(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16), crc))
                {
                    output.Write(entry.Preload);
                    if (entry.Length > 0)
                    {
                        stream.Position = start;
                        ArchiveFiles.CopyLimited(new BoundedStream(stream, entry.Length), output, entry.Length, cancellationToken);
                    }
                }
                if (new FileInfo(path).Length != entry.Size)
                    throw ArchiveSafety.Damaged(archiveName, $"{entry.Name} is cut off");
                if (entry.Crc != 0 && crc.Value != entry.Crc)
                    throw ArchiveSafety.Damaged(archiveName, $"checksum error in {entry.Name}");
            }
        }
        finally
        {
            foreach (var part in parts.Values)
                part.Dispose();
        }
    }

    /// <summary>The data part <paramref name="index"/> of a split pak, opened once.</summary>
    private static FileStream Part(string directoryFile, int index, Dictionary<int, FileStream> parts, string archiveName)
    {
        if (parts.TryGetValue(index, out var open))
            return open;
        var folder = Path.GetDirectoryName(Path.GetFullPath(directoryFile))!;
        var name = Path.GetFileNameWithoutExtension(directoryFile);
        if (name.EndsWith("_dir", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        var partName = $"{name}_{index:D3}.vpk";
        var path = Path.Combine(folder, partName);
        if (!File.Exists(path))
        {
            // Valve's tools write lower case; a copy may have changed it.
            path = Directory.EnumerateFiles(folder).FirstOrDefault(f => string.Equals(Path.GetFileName(f), partName, StringComparison.OrdinalIgnoreCase))
                ?? throw new FileNotFoundException($"{archiveName} keeps files in {partName}, which is not next to it.", partName);
        }
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        parts[index] = stream;
        return stream;
    }

    /// <summary>Reads the header and the tree; returns the files and where the data in the directory file starts.</summary>
    internal static (List<Entry> Entries, long DataStart) ReadTree(Stream stream, string archiveName)
    {
        Span<byte> header = stackalloc byte[12];
        if (stream.ReadAtLeast(header, 12, throwOnEndOfStream: false) < 12
            || BinaryPrimitives.ReadUInt32LittleEndian(header) != Signature)
        {
            if (DataPart().Match(archiveName) is { Success: true } part)
                throw new InvalidDataException(
                    $"{archiveName} is part {part.Groups["part"].Value} of a Valve pak; unpack {part.Groups["prefix"].Value}_dir.vpk, which lists its files.");
            throw new InvalidDataException($"{archiveName} is not a Valve pak (VPK).");
        }
        var version = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        var treeSize = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        if (version is not (1 or 2))
            throw new InvalidDataException($"{archiveName} is a VPK of version {version}; Filee reads versions 1 and 2.");
        var headerSize = version == 2 ? 28 : 12;
        var dataStart = (long)headerSize + treeSize;
        if (treeSize > MaxTreeBytes)
            throw new InvalidDataException($"{archiveName} lists more files than Filee unpacks from one pak.");
        if (dataStart > stream.Length)
            throw ArchiveSafety.Damaged(archiveName, "the pak is cut off");
        stream.Position = headerSize;

        try
        {
            var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            var entries = new List<Entry>();
            Span<byte> fields = stackalloc byte[18];
            for (string extension; (extension = ReadString(stream, archiveName)).Length > 0;)
            {
                for (string folder; (folder = ReadString(stream, archiveName)).Length > 0;)
                {
                    for (string file; (file = ReadString(stream, archiveName)).Length > 0;)
                    {
                        if (entries.Count >= ArchiveSafety.MaxEntries)
                            throw new InvalidDataException($"{archiveName} has more than {ArchiveSafety.MaxEntries:N0} files; Filee does not unpack it.");
                        stream.ReadExactly(fields);
                        var crc = BinaryPrimitives.ReadUInt32LittleEndian(fields);
                        var preloadSize = BinaryPrimitives.ReadUInt16LittleEndian(fields[4..]);
                        var archive = BinaryPrimitives.ReadUInt16LittleEndian(fields[6..]);
                        var offset = BinaryPrimitives.ReadUInt32LittleEndian(fields[8..]);
                        var length = BinaryPrimitives.ReadUInt32LittleEndian(fields[12..]);
                        if (BinaryPrimitives.ReadUInt16LittleEndian(fields[16..]) != 0xFFFF
                            || (archive != InDirectory && archive >= MaxParts))
                            throw ArchiveSafety.Damaged(archiveName, "the file list is broken");
                        // The preload bytes are part of the tree: checked against it before they are read.
                        if (stream.Position + preloadSize > dataStart)
                            throw ArchiveSafety.Damaged(archiveName, "the file list is broken");
                        var preload = reader.ReadBytes(preloadSize);
                        if (preload.Length != preloadSize)
                            throw new EndOfStreamException();
                        entries.Add(new Entry(Join(folder, file, extension), crc, preload, archive, offset, length));
                    }
                }
            }
            if (stream.Position > dataStart)
                throw ArchiveSafety.Damaged(archiveName, "the file list is broken");
            return (entries, dataStart);
        }
        catch (EndOfStreamException)
        {
            throw ArchiveSafety.Damaged(archiveName, "the pak is cut off");
        }
    }

    /// <summary>"folder/name.ext"; " " (Valve's tools) or "" stand for no folder or no extension.</summary>
    private static string Join(string folder, string name, string extension)
    {
        folder = folder.Trim() is "" ? "" : folder.Trim().TrimEnd('/') + "/";
        extension = extension.Trim() is "" ? "" : "." + extension.Trim();
        return folder + name + extension;
    }

    /// <summary>A NUL-terminated string (Valve writes ASCII; UTF-8 reads it too).</summary>
    private static string ReadString(Stream stream, string archiveName)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var b = stream.ReadByte();
            if (b < 0)
                throw new EndOfStreamException();
            if (b == 0)
                return Encoding.UTF8.GetString([.. bytes]);
            if (bytes.Count >= MaxNameBytes)
                throw ArchiveSafety.Damaged(archiveName, "a name in the pak is too long");
            bytes.Add((byte)b);
        }
    }
}
