// Reads ISO 9660 disc images with Joliet and Rock Ridge names. From sector 16 on (2048 bytes each) come the volume
// descriptors: type 1 the primary one, type 2 a supplementary one (Joliet when its escape sequence is "%/@", "%/C" or
// "%/E": names in UCS-2 big endian), 255 the end. Each holds the root directory record at offset 156 and the block
// size at 128. A directory is a run of records that never cross a block:
//
//   length u8, extended length u8, extent u32 LE+BE, data length u32 LE+BE, date 7 bytes, flags u8 (1 hidden,
//   2 directory, 4 associated, 0x80 more extents follow), unit size u8, gap u8, volume u16 LE+BE, name length u8,
//   name ("NAME.EXT;1"; 0 is "." and 1 is ".."), a pad byte for even lengths, then the System Use area
//
// Rock Ridge (SUSP entries in the System Use area, announced by "SP" in the root's "." record): NM the real name,
// PX the Unix mode, SL a symbolic link, CL / RE a directory moved out of a deep tree and back, CE more entries in
// another block. Names come from Rock Ridge when present, else from Joliet, else the plain ISO 9660 names.
// Directories that point back at a folder already read (a loop), extents beyond the image, deep trees and huge
// listings are refused. UDF images (Windows install discs) are left to 7-Zip; the El Torito boot image is not
// unpacked. Written from ECMA-119, the Joliet specification and IEEE P1282 / P1281 (Rock Ridge, SUSP).

using System.Buffers.Binary;
using System.Text;

namespace Filee.Engines.Archives;

/// <summary>Unpacks ISO 9660 images.</summary>
internal static class IsoReader
{
    private const int SectorSize = 2048;
    private const int FirstDescriptor = 16;
    private const int MaxDescriptors = 64;
    private const int MaxDepth = 64;
    private const long MaxDirectorySize = 16 * 1024 * 1024;
    private const long MaxDirectoryBytes = 256 * 1024 * 1024;
    private const int MaxContinuations = 32;

    private sealed record Extent(long Block, long Size);

    private sealed class Node
    {
        public required string Path { get; init; }
        public bool IsFolder { get; init; }
        public bool IsLink { get; init; }
        public List<Extent> Extents { get; } = [];
        public DateTime? Modified { get; init; }
        public long Size => Extents.Sum(e => e.Size);
    }

    /// <summary>True when the image carries a UDF file system (NSR02 / NSR03 in its volume recognition area).</summary>
    public static bool HasUdf(string image)
    {
        using var stream = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.Read, SectorSize);
        var sector = new byte[SectorSize];
        for (var i = 0; i < MaxDescriptors; i++)
        {
            stream.Position = (long)(FirstDescriptor + i) * SectorSize;
            if (stream.ReadAtLeast(sector, 6, throwOnEndOfStream: false) < 6)
                return false;
            var id = sector.AsSpan(1, 5);
            if (id.SequenceEqual("NSR02"u8) || id.SequenceEqual("NSR03"u8))
                return true;
            if (!id.SequenceEqual("CD001"u8) && !id.SequenceEqual("BEA01"u8) && !id.SequenceEqual("TEA01"u8)
                && !id.SequenceEqual("BOOT2"u8) && !id.SequenceEqual("CDW02"u8))
                return false;
        }
        return false;
    }

    public static void Extract(string image, string folder, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(image);
        using var stream = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var walker = new Walker(stream, name, cancellationToken);
        var nodes = walker.Walk();
        ArchiveSafety.CheckListing([.. nodes.Select(n => new ArchiveItem(n.Path, n.IsFolder, n.IsFolder || n.IsLink ? 0 : n.Size, IsLink: n.IsLink))],
            name, stream.Length, folder);

        var writer = new EntryWriter(folder, new UnpackBudget(name, stream.Length, folder), cancellationToken);
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.IsFolder)
                writer.AddFolder(node.Path, node.Modified);
            else if (!node.IsLink)
                writer.AddFile(node.Path, new ExtentStream(stream, node.Extents, walker.BlockSize), node.Size, null, node.Modified);
        }
        writer.Finish();
    }

    /// <summary>Reads the descriptors and walks the directory tree.</summary>
    private sealed class Walker(FileStream stream, string name, CancellationToken cancellationToken)
    {
        private readonly HashSet<long> _visited = [];
        private readonly List<Node> _nodes = [];
        private int _blockSize = SectorSize;
        private long _directoryBytes;
        private bool _joliet;
        private bool _rockRidge;
        private int _suspSkip;

        /// <summary>The logical block size (2048 bytes in practice).</summary>
        public int BlockSize => _blockSize;

        public List<Node> Walk()
        {
            byte[]? primary = null, joliet = null;
            var sector = new byte[SectorSize];
            for (var i = 0; i < MaxDescriptors; i++)
            {
                stream.Position = (long)(FirstDescriptor + i) * SectorSize;
                if (stream.ReadAtLeast(sector, SectorSize, throwOnEndOfStream: false) < SectorSize)
                    break;
                if (!sector.AsSpan(1, 5).SequenceEqual("CD001"u8))
                    break;
                if (sector[0] == 255)
                    break;
                if (sector[0] == 1 && primary is null)
                    primary = (byte[])sector.Clone();
                else if (sector[0] == 2 && joliet is null && IsJoliet(sector))
                    joliet = (byte[])sector.Clone();
            }
            if (primary is null)
                throw new NotSupportedException($"{name} has no ISO 9660 file system (a UDF-only or other disc image).");

            _blockSize = BinaryPrimitives.ReadUInt16LittleEndian(primary.AsSpan(128));
            if (_blockSize is not (512 or 1024 or 2048))
                throw ArchiveSafety.Damaged(name, $"unsupported ISO block size {_blockSize}");

            var root = Record.Parse(primary.AsSpan(156, 34), name);
            DetectRockRidge(root);
            if (!_rockRidge && joliet is not null)
            {
                _joliet = true;
                root = Record.Parse(joliet.AsSpan(156, 34), name);
            }
            ReadDirectory(root.Block, root.Size, "", 0);
            return _nodes;
        }

        private static bool IsJoliet(byte[] descriptor)
        {
            var escape = descriptor.AsSpan(88, 32);
            return escape.IndexOf("%/@"u8) >= 0 || escape.IndexOf("%/C"u8) >= 0 || escape.IndexOf("%/E"u8) >= 0;
        }

        /// <summary>Rock Ridge is in use when the root's "." record starts its System Use area with "SP" BE EF.</summary>
        private void DetectRockRidge(Record root)
        {
            var bytes = ReadExtent(root.Block, Math.Min(root.Size, SectorSize));
            if (bytes.Length < 34 || bytes[0] < 34)
                return;
            var record = Record.Parse(bytes.AsSpan(0, bytes[0]), name);
            var su = record.SystemUse;
            if (su.Length >= 7 && su[0] == 'S' && su[1] == 'P' && su[4] == 0xBE && su[5] == 0xEF)
            {
                _rockRidge = true;
                _suspSkip = su[6];
            }
        }

        private void ReadDirectory(long block, long size, string parent, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > MaxDepth)
                throw ArchiveSafety.Damaged(name, "the folders are nested too deeply");
            if (!_visited.Add(block))
                throw ArchiveSafety.Damaged(name, "its folders form a loop");
            if (size > MaxDirectorySize || (_directoryBytes += size) > MaxDirectoryBytes)
                throw ArchiveSafety.Damaged(name, "a folder listing is too large");

            var data = ReadExtent(block, size);
            List<Extent>? pending = null;
            var position = 0;
            while (position < data.Length)
            {
                var length = data[position];
                if (length == 0)
                {
                    position = (position / _blockSize + 1) * _blockSize; // the rest of the block is padding
                    continue;
                }
                if (length < 34 || position + length > data.Length)
                    throw ArchiveSafety.Damaged(name, "invalid directory record");
                var record = Record.Parse(data.AsSpan(position, length), name);
                position += length;
                if (record.IsSelfOrParent)
                    continue;
                if (record.UnitSize != 0 || record.Gap != 0)
                    throw new NotSupportedException($"{name} has interleaved files, which Filee's built-in ISO reader does not unpack.");

                var rr = _rockRidge ? RockRidge.Read(this, record.SystemUse[Math.Min(_suspSkip, record.SystemUse.Length)..]) : default;
                if (rr.Relocated || (record.Flags & 0x04) != 0)
                    continue; // shown where its CL entry points; associated (resource fork) files are left out

                var entryName = rr.Name ?? (_joliet ? JolietName(record.Name) : PlainName(record.Name));
                if (entryName.Length == 0)
                    throw ArchiveSafety.Damaged(name, "an entry has no name");
                var path = parent.Length == 0 ? entryName : parent + "/" + entryName;

                if (_nodes.Count >= ArchiveSafety.EntryLimit)
                    throw ArchiveSafety.TooManyEntries(name);
                if (rr.ChildBlock is { } moved)
                {
                    var child = ReadExtent(moved, Math.Min(_blockSize, SectorSize));
                    var self = child.Length >= 34 && child[0] >= 34 ? Record.Parse(child.AsSpan(0, child[0]), name) : throw ArchiveSafety.Damaged(name, "a moved folder is missing");
                    _nodes.Add(new Node { Path = path, IsFolder = true, Modified = record.Modified });
                    ReadDirectory(self.Block, self.Size, path, depth + 1);
                    continue;
                }
                if ((record.Flags & 0x02) != 0)
                {
                    _nodes.Add(new Node { Path = path, IsFolder = true, Modified = record.Modified });
                    ReadDirectory(record.Block, record.Size, path, depth + 1);
                    continue;
                }

                CheckExtent(record.Block, record.Size);
                (pending ??= []).Add(new Extent(record.Block, record.Size));
                if ((record.Flags & 0x80) != 0)
                    continue; // more extents of the same file follow
                var node = new Node { Path = path, IsLink = rr.IsLink, Modified = record.Modified };
                node.Extents.AddRange(pending);
                pending = null;
                _nodes.Add(node);
            }
            if (pending is not null)
                throw ArchiveSafety.Damaged(name, "a file's last extent is missing");
        }

        private void CheckExtent(long block, long size)
        {
            if (block * _blockSize + size > stream.Length)
                throw ArchiveSafety.Damaged(name, "a file lies beyond the end of the image");
        }

        public byte[] ReadExtent(long block, long size)
        {
            CheckExtent(block, size);
            var bytes = new byte[size];
            stream.Position = block * _blockSize;
            stream.ReadExactly(bytes);
            return bytes;
        }

        public string Name => name;
    }

    /// <summary>The fields of one directory record Filee uses.</summary>
    private readonly ref struct Record
    {
        public long Block { get; init; }
        public long Size { get; init; }
        public byte Flags { get; init; }
        public byte UnitSize { get; init; }
        public byte Gap { get; init; }
        public ReadOnlySpan<byte> Name { get; init; }
        public ReadOnlySpan<byte> SystemUse { get; init; }
        public DateTime? Modified { get; init; }

        public bool IsSelfOrParent => Name.Length == 1 && Name[0] is 0 or 1;

        public static Record Parse(ReadOnlySpan<byte> record, string archiveName)
        {
            int nameLength = record[32];
            if (33 + nameLength > record.Length)
                throw ArchiveSafety.Damaged(archiveName, "invalid directory record");
            var systemUse = 33 + nameLength + (nameLength % 2 == 0 ? 1 : 0);
            return new Record
            {
                Block = BinaryPrimitives.ReadUInt32LittleEndian(record[2..]),
                Size = BinaryPrimitives.ReadUInt32LittleEndian(record[10..]),
                Modified = Date(record.Slice(18, 7)),
                Flags = record[25],
                UnitSize = record[26],
                Gap = record[27],
                Name = record.Slice(33, nameLength),
                SystemUse = systemUse < record.Length ? record[systemUse..] : [],
            };
        }

        /// <summary>Years since 1900, month, day, hour, minute, second, offset from UTC in 15-minute steps.</summary>
        private static DateTime? Date(ReadOnlySpan<byte> date)
        {
            int year = 1900 + date[0], month = date[1], day = date[2], hour = date[3], minute = date[4], second = date[5];
            if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
                return null;
            var offset = TimeSpan.FromMinutes((sbyte)date[6] * 15);
            if (offset.Duration() > TimeSpan.FromHours(14))
                offset = TimeSpan.Zero;
            return new DateTimeOffset(year, month, day, hour, minute, second, offset).LocalDateTime;
        }
    }

    /// <summary>"README.TXT;1" → "README.TXT", "FOLDER." → "FOLDER".</summary>
    private static string PlainName(ReadOnlySpan<byte> raw)
    {
        var text = Encoding.Latin1.GetString(raw);
        var version = text.LastIndexOf(';');
        if (version > 0)
            text = text[..version];
        return text.TrimEnd('.');
    }

    private static string JolietName(ReadOnlySpan<byte> raw)
    {
        var text = Encoding.BigEndianUnicode.GetString(raw[..(raw.Length & ~1)]);
        var version = text.LastIndexOf(';');
        if (version > 0 && text[(version + 1)..].All(char.IsAsciiDigit))
            text = text[..version];
        return text;
    }

    /// <summary>What the Rock Ridge entries of one record say.</summary>
    private readonly record struct RockRidge(string? Name, bool IsLink, bool Relocated, long? ChildBlock)
    {
        public static RockRidge Read(Walker walker, ReadOnlySpan<byte> area)
        {
            StringBuilder? name = null;
            bool link = false, relocated = false;
            long? child = null;
            var continuations = 0;
            var current = area.ToArray();
            while (current is not null)
            {
                byte[]? next = null;
                var i = 0;
                while (i + 4 <= current.Length)
                {
                    int length = current[i + 2];
                    if (length < 4 || i + length > current.Length)
                        break;
                    var entry = current.AsSpan(i, length);
                    switch ((char)entry[0], (char)entry[1])
                    {
                        case ('N', 'M') when length >= 5 && (entry[4] & 0x06) == 0:
                            (name ??= new StringBuilder()).Append(Encoding.UTF8.GetString(entry[5..]));
                            break;
                        case ('P', 'X') when length >= 12:
                            link |= (BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]) & 0xF000) == 0xA000;
                            break;
                        case ('S', 'L'):
                            link = true;
                            break;
                        case ('R', 'E'):
                            relocated = true;
                            break;
                        case ('C', 'L') when length >= 12:
                            child = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
                            break;
                        case ('C', 'E') when length >= 28:
                            {
                                if (++continuations > MaxContinuations)
                                    throw ArchiveSafety.Damaged(walker.Name, "Rock Ridge continuation entries loop");
                                var block = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
                                var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
                                var size = BinaryPrimitives.ReadUInt32LittleEndian(entry[20..]);
                                if (size > SectorSize || offset >= SectorSize)
                                    throw ArchiveSafety.Damaged(walker.Name, "invalid Rock Ridge continuation entry");
                                var bytes = walker.ReadExtent(block, SectorSize);
                                next = bytes.AsSpan((int)offset, (int)Math.Min(size, SectorSize - offset)).ToArray();
                                break;
                            }
                        case ('S', 'T'):
                            i = current.Length;
                            continue;
                    }
                    i += length;
                }
                current = next;
            }
            var text = name?.ToString();
            return new RockRidge(string.IsNullOrEmpty(text) ? null : text, link, relocated, child);
        }
    }

    /// <summary>Reads a file's extents one after the other.</summary>
    private sealed class ExtentStream(FileStream image, List<Extent> extents, int blockSize) : Stream
    {
        private int _index;
        private long _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => extents.Sum(e => e.Size);

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            while (_index < extents.Count && _offset >= extents[_index].Size)
            {
                _index++;
                _offset = 0;
            }
            if (_index == extents.Count || buffer.Length == 0)
                return 0;
            var extent = extents[_index];
            image.Position = extent.Block * blockSize + _offset;
            var read = image.Read(buffer[..(int)Math.Min(buffer.Length, extent.Size - _offset)]);
            if (read == 0)
                throw new EndOfStreamException("The image ends in the middle of a file.");
            _offset += read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
