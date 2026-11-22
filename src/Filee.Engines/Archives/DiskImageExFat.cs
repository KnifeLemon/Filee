// Reads exFAT volumes (SDXC cards, USB sticks), written from Microsoft's exFAT file system specification.
//
//   Boot sector: "EXFAT   " at 3, FAT offset and length (sectors) u32 at 80 and 84, cluster heap offset u32 at 88,
//     cluster count u32 at 92, root folder's first cluster u32 at 96, volume flags u16 at 106 (bit 0: the second FAT
//     is the active one), bytes per sector 1 << byte 108 (9 to 12), sectors per cluster 1 << byte 109 (clusters of at
//     most 32 MB), number of FATs at 110.
//   Folders: 32-byte entries; type 0 ends the folder, types without bit 7 are unused. A file is a set: a file entry
//     (0x85: secondary count at 1, set checksum u16 at 2 over the whole set but those two bytes, attributes u16 at 4
//     with 0x10 for folders, modified timestamp u32 at 12, its 10 ms steps at 21 and its UTC offset at 23 (bit 7:
//     valid; bits 0-6: signed quarter hours)), a stream extension (0xC0: flags at 1 with bit 1 "no FAT chain", the
//     clusters then being consecutive; name length at 3; valid data length u64 at 8, after which the data reads as
//     zeros; first cluster u32 at 20; data length u64 at 24) and file name entries (0xC1: 15 UTF-16 characters from
//     2). The root holds the volume label (0x83: character count at 1, UTF-16 from 2), the allocation bitmap and the
//     up-case table, which a reader of files does not need.
//   FAT: 32-bit entries, the next cluster or 0xFFFFFFFF at the end of a chain.
//
// Clusters are checked against the volume, folders against the format's 256 MB, loops (a folder met twice) and broken
// sets (checksum, order) are damage, and a chain is only followed as far as its file's size needs.

using System.Buffers.Binary;
using System.Text;

namespace Filee.Engines.Archives;

/// <summary>One exFAT volume, read in-process.</summary>
internal sealed class DiskImageExFat
{
    private const int MaxDepth = 128;
    private const long MaxFolderBytes = 256L * 1024 * 1024;
    private const long MaxFoldersBytes = 1024L * 1024 * 1024;
    private const int FatPage = 64 * 1024;
    private const int MaxFatPages = 64;

    private readonly Stream _volume;
    private readonly string _name;
    private readonly long _fatOffset;
    private readonly long _fatBytes;
    private readonly long _heapOffset;
    private readonly int _clusterSize;
    private readonly uint _clusters;
    private readonly uint _rootCluster;
    private readonly Dictionary<long, byte[]> _fatPages = [];
    private long _folderBytes;

    /// <summary>The volume label, if it has one.</summary>
    public string? Label { get; private set; }

    /// <summary>An exFAT boot sector.</summary>
    public static bool Detect(ReadOnlySpan<byte> sector) => sector.Length >= 512 && sector.Slice(3, 8).SequenceEqual("EXFAT   "u8);

    public DiskImageExFat(Stream volume, string archiveName)
    {
        _volume = volume;
        _name = archiveName;
        var boot = DiskImagePartitions.ReadAt(volume, 0, 512);
        if (!Detect(boot) || BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(510)) != 0xAA55)
            throw Damaged("invalid exFAT boot sector");
        var sectorShift = boot[108];
        var clusterShift = boot[109];
        if (sectorShift is < 9 or > 12 || sectorShift + clusterShift > 25 || boot[110] is not (1 or 2))
            throw Damaged("invalid exFAT boot sector");
        var sector = 1L << sectorShift;
        _clusterSize = 1 << (sectorShift + clusterShift);
        _clusters = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(92));
        _rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(96));
        long fatOffset = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(80));
        long fatLength = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(84));
        long heapOffset = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(88));
        var secondFat = (BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(106)) & 1) != 0 && boot[110] == 2;
        if (_clusters == 0 || _clusters > 0xFFFFFFF5 || fatOffset < 24 || fatLength * sector < (_clusters + 2L) * 4
            || heapOffset < fatOffset + fatLength * boot[110] || _rootCluster < 2 || _rootCluster > _clusters + 1L)
            throw Damaged("invalid exFAT boot sector");
        _fatOffset = (fatOffset + (secondFat ? fatLength : 0)) * sector;
        _fatBytes = (_clusters + 2L) * 4;
        _heapOffset = heapOffset * sector;
        if (_fatOffset + _fatBytes > volume.Length)
            throw Damaged("the file allocation table lies beyond the end of the image");
    }

    private Exception Damaged(string detail) => ArchiveSafety.Damaged(_name, detail);

    /// <summary>
    /// The files and folders below the root, their paths under <paramref name="prefix"/> ("" for the top), counted
    /// against <paramref name="nodes"/>' limit.
    /// </summary>
    public void Walk(string prefix, List<DiskImageNode> nodes, NameTally names, CancellationToken cancellationToken)
    {
        var visited = new HashSet<uint> { _rootCluster };
        var stack = new Stack<(byte[] Folder, string Path, int Depth)>();
        stack.Push((ReadData(new Data(_rootCluster, false, MaxFolderBytes, MaxFolderBytes), root: true), prefix, 0));
        var root = true;
        while (stack.TryPop(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in Entries(item.Folder, root))
            {
                if (nodes.Count >= ArchiveSafety.EntryLimit)
                    throw ArchiveSafety.TooManyEntries(_name);
                var path = item.Path.Length == 0 ? entry.Name : item.Path + "/" + entry.Name;
                names.Add(path);
                if (entry.IsFolder)
                {
                    if (item.Depth >= MaxDepth)
                        throw Damaged("the folders are nested too deeply");
                    if (entry.Data.First != 0 && !visited.Add(entry.Data.First))
                        throw Damaged("its folders form a loop");
                    nodes.Add(new DiskImageNode(path, true, false, 0, entry.Modified, null));
                    if (entry.Data.Length > MaxFolderBytes)
                        throw Damaged("a folder is larger than exFAT allows");
                    stack.Push((ReadData(entry.Data, root: false), path, item.Depth + 1));
                    continue;
                }
                CheckRun(entry.Data);
                var data = entry.Data;
                nodes.Add(new DiskImageNode(path, false, false, data.Length, entry.Modified, () => new FileData(this, data)));
            }
            root = false;
        }
    }

    /// <summary>Where a file's data lies: its first cluster (0: none), whether the clusters simply follow each other, its size and the part written.</summary>
    private readonly record struct Data(uint First, bool Contiguous, long Length, long Valid);

    private readonly record struct Entry(string Name, bool IsFolder, Data Data, DateTime? Modified);

    /// <summary>The entry sets of one folder (and, in the root, the volume label).</summary>
    private List<Entry> Entries(byte[] folder, bool root)
    {
        var result = new List<Entry>();
        for (var at = 0; at + 32 <= folder.Length;)
        {
            var type = folder[at];
            if (type == 0x00)
                break;
            if ((type & 0x80) == 0)
            {
                at += 32; // unused (deleted)
                continue;
            }
            if (type == 0x83 && root)
            {
                var count = Math.Min((int)folder[at + 1], 11);
                var label = Encoding.Unicode.GetString(folder, at + 2, count * 2).Trim();
                Label = label.Length > 0 && !label.Any(char.IsControl) ? label : null;
                at += 32;
                continue;
            }
            if (type != 0x85)
            {
                at += 32; // the bitmap, the up-case table, GUIDs and the like (or secondaries of a set that was skipped)
                continue;
            }
            int secondaries = folder[at + 1];
            var setLength = (secondaries + 1) * 32;
            if (secondaries < 2 || at + setLength > folder.Length)
                throw Damaged("a folder entry set is incomplete");
            var set = folder.AsSpan(at, setLength);
            if (Checksum(set) != BinaryPrimitives.ReadUInt16LittleEndian(set[2..]))
                throw Damaged("a folder entry set's checksum is wrong");
            var stream = set.Slice(32, 32);
            if (stream[0] != 0xC0)
                throw Damaged("a file entry has no stream extension");
            int nameLength = stream[3];
            var nameEntries = (nameLength + 14) / 15;
            if (nameLength == 0 || nameEntries > secondaries - 1)
                throw Damaged("a file entry's name is out of bounds");
            var chars = new char[nameEntries * 15];
            for (var n = 0; n < nameEntries; n++)
            {
                var e = set.Slice(64 + n * 32, 32);
                if (e[0] != 0xC1)
                    throw Damaged("a file entry's name is out of bounds");
                for (var c = 0; c < 15; c++)
                    chars[n * 15 + c] = (char)BinaryPrimitives.ReadUInt16LittleEndian(e[(2 + c * 2)..]);
            }
            var name = new string(chars, 0, nameLength).Replace('/', '_').Replace('\0', '_');
            var attributes = BinaryPrimitives.ReadUInt16LittleEndian(set[4..]);
            var length = BinaryPrimitives.ReadInt64LittleEndian(stream[24..]);
            var valid = BinaryPrimitives.ReadInt64LittleEndian(stream[8..]);
            var first = BinaryPrimitives.ReadUInt32LittleEndian(stream[20..]);
            if (length < 0 || valid < 0 || valid > length || first == 0 && length > 0)
                throw Damaged("a file entry's size is out of bounds");
            var modified = Timestamp(BinaryPrimitives.ReadUInt32LittleEndian(set[12..]), set[21], set[23]);
            if (name is not ("." or ".."))
                result.Add(new Entry(name, (attributes & 0x10) != 0, new Data(first, (stream[1] & 2) != 0, length, valid), modified));
            at += setLength;
        }
        return result;
    }

    /// <summary>The checksum of an entry set: every byte but the checksum's own two.</summary>
    internal static ushort Checksum(ReadOnlySpan<byte> set)
    {
        ushort sum = 0;
        for (var i = 0; i < set.Length; i++)
        {
            if (i is 2 or 3)
                continue;
            sum = (ushort)(((sum & 1) != 0 ? 0x8000 : 0) + (sum >> 1) + set[i]);
        }
        return sum;
    }

    /// <summary>An exFAT timestamp (its 10 ms steps and UTC offset) as local time, or null when it is not a real one.</summary>
    private static DateTime? Timestamp(uint stamp, byte tenMs, byte utcOffset)
    {
        int year = 1980 + (int)(stamp >> 25), month = (int)(stamp >> 21) & 0x0F, day = (int)(stamp >> 16) & 0x1F;
        int hour = (int)(stamp >> 11) & 0x1F, minute = (int)(stamp >> 5) & 0x3F, second = (int)(stamp & 0x1F) * 2 + Math.Min((int)tenMs, 199) / 100;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
            return null;
        var time = new DateTime(year, month, day, hour, minute, second);
        if ((utcOffset & 0x80) == 0)
            return DateTime.SpecifyKind(time, DateTimeKind.Local);
        var quarters = (sbyte)(utcOffset << 1) >> 1; // bits 0-6, signed
        return DateTime.SpecifyKind(time.AddMinutes(-15 * quarters), DateTimeKind.Utc).ToLocalTime();
    }

    /// <summary>Checks a file's clusters lie on the volume (a chain is checked as it is read).</summary>
    private void CheckRun(Data data)
    {
        if (data.Length == 0)
            return;
        if (data.First < 2 || data.First > _clusters + 1L)
            throw Damaged("a file starts outside the volume");
        var count = (data.Length + _clusterSize - 1) / _clusterSize;
        if (data.Contiguous && data.First + count - 1 > _clusters + 1L)
            throw Damaged("a file's clusters lie outside the volume");
    }

    /// <summary>A folder's data: the root's whole chain (it has no stated size), another folder's stated size.</summary>
    private byte[] ReadData(Data data, bool root)
    {
        if (data.Length == 0)
            return [];
        CheckRun(data);
        var output = new MemoryStream();
        using (var file = new FileData(this, data, chainOnly: root))
            file.CopyTo(output);
        if ((_folderBytes += output.Length) > MaxFoldersBytes)
            throw Damaged("its folders lead in circles");
        return output.ToArray();
    }

    /// <summary>The cluster after <paramref name="cluster"/>, or null at the end of the chain.</summary>
    private uint? Next(uint cluster)
    {
        var offset = cluster * 4L;
        var page = offset / FatPage;
        if (!_fatPages.TryGetValue(page, out var bytes))
        {
            if (_fatPages.Count >= MaxFatPages)
                _fatPages.Clear();
            bytes = DiskImagePartitions.ReadAt(_volume, _fatOffset + page * FatPage, (int)Math.Min(FatPage, _fatBytes - page * FatPage));
            _fatPages[page] = bytes;
        }
        var at = (int)(offset - page * FatPage);
        if (at + 4 > bytes.Length)
            throw Damaged("a cluster chain is broken");
        var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
        if (value == 0xFFFFFFFF)
            return null;
        if (value < 2 || value > _clusters + 1L)
            throw Damaged("a cluster chain is broken");
        return value;
    }

    /// <summary>A file's data along its clusters, zeros past the part written.</summary>
    private sealed class FileData : Stream
    {
        private readonly DiskImageExFat _volume;
        private readonly Data _data;
        private readonly bool _chainOnly;
        private uint _cluster;
        private long _position;
        private long _length;

        /// <param name="chainOnly">The root folder: as many clusters as its chain has (up to the folder limit).</param>
        public FileData(DiskImageExFat volume, Data data, bool chainOnly = false)
        {
            _volume = volume;
            _data = data;
            _chainOnly = chainOnly;
            _cluster = data.First;
            _length = data.Length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0 || _position >= _length)
                return 0;
            var clusterSize = _volume._clusterSize;
            var within = (int)(_position % clusterSize);
            if (_position > 0 && within == 0)
            {
                // On to the next cluster.
                if (_data.Contiguous)
                {
                    _cluster++;
                }
                else if (_volume.Next(_cluster) is { } next)
                {
                    _cluster = next;
                }
                else if (_chainOnly)
                {
                    _length = _position;
                    return 0;
                }
                else
                {
                    throw _volume.Damaged("a file's clusters end before its size");
                }
            }
            var take = (int)Math.Min(Math.Min(buffer.Length, clusterSize - within), _length - _position);
            var written = (int)Math.Clamp(_data.Valid - _position, 0, take);
            if (written > 0)
            {
                var at = _volume._heapOffset + (long)(_cluster - 2) * clusterSize + within;
                if (_cluster > _volume._clusters + 1L || at + written > _volume._volume.Length)
                    throw _volume.Damaged("a cluster lies beyond the end of the image");
                _volume._volume.Position = at;
                _volume._volume.ReadExactly(buffer[..written]);
            }
            buffer[written..take].Clear();
            _position += take;
            return take;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
