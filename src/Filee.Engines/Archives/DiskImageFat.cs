// Reads FAT12, FAT16 and FAT32 volumes (floppy images, memory cards, the EFI partition of a disk image), written from
// Microsoft's "FAT: General Overview of On-Disk Format". DiscUtils.Fat would read them too, but only by their 8.3
// names in capitals ("MYDOCU~1"), so Filee reads the names itself:
//
//   Boot sector (BPB): bytes per sector u16 at 11, sectors per cluster at 13, reserved sectors u16 at 14, FAT count at
//     16, root entries u16 at 17, total sectors u16 at 19 (or u32 at 32), FAT size u16 at 22 (FAT32: u32 at 36, flags
//     u16 at 40 with the active FAT in bits 0-3 when bit 7 is set, root cluster u32 at 44). The FAT type follows from
//     the number of data clusters alone: under 4085 FAT12, under 65525 FAT16, else FAT32.
//   Directory entries (32 bytes): 8.3 name, attributes at 11 (0x10 folder, 0x08 volume label, 0x0F a long-name
//     part), lowercase flags at 12 (0x08 name, 0x10 extension), write time and date at 22 and 24, first cluster
//     (high half at 20 on FAT32, low at 26), size u32 at 28. Long names come in parts of 13 UTF-16 characters before
//     their 8.3 entry, numbered down to 1 (0x40 marks the last part) and tied to it by a checksum of the 8.3 name.
//
// Every cluster number is checked against the volume, every chain is cut at the number of clusters a file needs (a
// chain that loops then just repeats data, within the bomb limits), folder chains may not loop or hold more than
// 65,536 entries (the format's own limit), and a folder seen twice (a loop through "subfolders") is damaged. The FAT
// is read in 64 KB pages as the chains need it (at most 4 MB at once), whatever size the boot sector claims.

using System.Buffers.Binary;
using System.Text;

namespace Filee.Engines.Archives;

/// <summary>One FAT12/16/32 volume, read in-process.</summary>
internal sealed class DiskImageFat
{
    /// <summary>The FAT is read in pages of this size, at most <see cref="MaxFatPages"/> kept at once.</summary>
    private const int FatPage = 64 * 1024;

    private const int MaxFatPages = 64;

    /// <summary>A folder holds at most 65,536 entries of 32 bytes.</summary>
    private const int MaxFolderBytes = 65536 * 32;

    private const int MaxDepth = 128;

    private readonly Stream _volume;
    private readonly string _name;
    private readonly Dictionary<long, byte[]> _fatPages = [];
    private readonly long _fatOffset;
    private readonly long _fatLength;
    private readonly int _bits;
    private readonly int _clusterSize;
    private readonly long _dataStart;
    private readonly uint _maxCluster;
    private readonly long _rootOffset;
    private readonly int _rootBytes;
    private readonly uint _rootCluster;

    /// <summary>"FAT12", "FAT16" or "FAT32".</summary>
    public string Type => "FAT" + _bits;

    /// <summary>The volume label of the root folder or the boot sector, if any.</summary>
    public string? Label { get; private set; }

    /// <summary>A FAT boot sector: a jump instruction and a BPB whose fields make sense, and not NTFS or exFAT.</summary>
    public static bool Detect(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < 512 || sector[0] is not (0xEB or 0xE9) || sector.Slice(3, 8).SequenceEqual("NTFS    "u8)
            || sector.Slice(3, 8).SequenceEqual("EXFAT   "u8))
            return false;
        var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(sector[11..]);
        var sectorsPerCluster = sector[13];
        var reserved = BinaryPrimitives.ReadUInt16LittleEndian(sector[14..]);
        var total = BinaryPrimitives.ReadUInt16LittleEndian(sector[19..]) is var t16 and not 0 ? t16 : BinaryPrimitives.ReadUInt32LittleEndian(sector[32..]);
        var fatSize = BinaryPrimitives.ReadUInt16LittleEndian(sector[22..]) is var f16 and not 0 ? f16 : BinaryPrimitives.ReadUInt32LittleEndian(sector[36..]);
        return bytesPerSector is 512 or 1024 or 2048 or 4096 && sectorsPerCluster != 0 && (sectorsPerCluster & (sectorsPerCluster - 1)) == 0
               && reserved != 0 && sector[16] is >= 1 and <= 4 && total != 0 && fatSize != 0;
    }

    public DiskImageFat(Stream volume, string archiveName)
    {
        _volume = volume;
        _name = archiveName;
        var boot = DiskImagePartitions.ReadAt(volume, 0, 512);
        if (!Detect(boot))
            throw ArchiveSafety.Damaged(archiveName, "invalid FAT boot sector");
        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11));
        int sectorsPerCluster = boot[13];
        long reserved = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(14));
        int fats = boot[16];
        int rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(17));
        long total = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(19)) is var t16 and not 0 ? t16 : BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(32));
        long fatSize = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(22)) is var f16 and not 0 ? f16 : BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(36));

        var rootSectors = (rootEntries * 32L + bytesPerSector - 1) / bytesPerSector;
        var dataSectors = total - (reserved + fats * fatSize + rootSectors);
        if (dataSectors <= 0)
            throw ArchiveSafety.Damaged(archiveName, "the FAT boot sector leaves no room for data");
        var clusters = dataSectors / sectorsPerCluster;
        _bits = clusters < 4085 ? 12 : clusters < 65525 ? 16 : 32;
        _clusterSize = bytesPerSector * sectorsPerCluster;
        _maxCluster = (uint)Math.Min(clusters + 1, 0x0FFFFFF6);

        var fatBytes = fatSize * bytesPerSector;
        // Enough FAT for every cluster; the rest of a longer FAT is never looked at.
        var needed = (_maxCluster + 1L) * _bits / 8 + 2;
        if (fatBytes < needed)
            throw ArchiveSafety.Damaged(archiveName, "the file allocation table is smaller than the volume needs");
        var active = 0;
        if (_bits == 32)
        {
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(40));
            if ((flags & 0x80) != 0 && (flags & 0x0F) < fats)
                active = flags & 0x0F;
            _rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(44)) & 0x0FFFFFFF;
        }
        var fatOffset = (reserved + active * fatSize) * bytesPerSector;
        if (fatOffset + needed > volume.Length)
            throw ArchiveSafety.Damaged(archiveName, "the file allocation table lies beyond the end of the image");
        _fatOffset = fatOffset;
        _fatLength = needed;
        _rootOffset = (reserved + fats * fatSize) * bytesPerSector;
        _rootBytes = rootEntries * 32;
        _dataStart = _rootOffset + rootSectors * bytesPerSector;
        if (_bits != 32 && _rootOffset + _rootBytes > volume.Length)
            throw ArchiveSafety.Damaged(archiveName, "the root folder lies beyond the end of the image");

        var label = Encoding.ASCII.GetString(boot, _bits == 32 ? 71 : 43, 11).Trim();
        if (label.Length > 0 && label != "NO NAME" && label.All(c => c >= ' ' && c < 0x7F))
            Label = label;
    }

    /// <summary>
    /// The files and folders below the root, their paths under <paramref name="prefix"/> ("" for the top), counted
    /// against <paramref name="nodes"/>' limit.
    /// </summary>
    public void Walk(string prefix, List<DiskImageNode> nodes, NameTally names, CancellationToken cancellationToken)
    {
        var visited = new HashSet<uint>();
        var stack = new Stack<(byte[] Folder, string Path, int Depth)>();
        stack.Push((_bits == 32 ? ReadFolder(_rootCluster, visited) : DiskImagePartitions.ReadAt(_volume, _rootOffset, _rootBytes), prefix, 0));
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
                        throw ArchiveSafety.Damaged(_name, "the folders are nested too deeply");
                    nodes.Add(new DiskImageNode(path, true, false, 0, entry.Modified, null));
                    stack.Push((ReadFolder(entry.Cluster, visited), path, item.Depth + 1));
                    continue;
                }
                if (entry.Size > 0 && (entry.Cluster < 2 || entry.Cluster > _maxCluster))
                    throw ArchiveSafety.Damaged(_name, $"{path} has no clusters");
                var (cluster, size) = (entry.Cluster, entry.Size);
                nodes.Add(new DiskImageNode(path, false, false, size, entry.Modified, () => new ChainStream(this, cluster, size)));
            }
            root = false;
        }
    }

    private readonly record struct Entry(string Name, bool IsFolder, uint Cluster, long Size, DateTime? Modified);

    /// <summary>The entries of one folder, long names joined to their 8.3 entries; "." and ".." left out.</summary>
    private IEnumerable<Entry> Entries(byte[] folder, bool root)
    {
        var parts = new char[20 * 13];
        var expected = 0; // the long-name part that should come next (0: none pending)
        var total = 0;
        byte checksum = 0;
        for (var at = 0; at + 32 <= folder.Length; at += 32)
        {
            var e = folder.AsSpan(at, 32);
            if (e[0] == 0x00)
                yield break;
            if (e[0] == 0xE5)
            {
                expected = 0;
                continue;
            }
            var attributes = e[11];
            if ((attributes & 0x3F) == 0x0F)
            {
                var order = e[0] & 0x1F;
                if ((e[0] & 0x40) != 0 && order is >= 1 and <= 20)
                {
                    total = order;
                    expected = order;
                    checksum = e[13];
                }
                if (expected == 0 || order != expected || e[13] != checksum)
                {
                    expected = 0;
                    continue;
                }
                var chars = parts.AsSpan((order - 1) * 13, 13);
                for (var i = 0; i < 13; i++)
                    chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(e[LongNameOffsets[i]..]);
                expected--;
                if (expected == 0)
                    expected = -1; // complete: the 8.3 entry follows
                continue;
            }
            var longName = expected == -1 && Checksum(e[..11]) == checksum ? LongName(parts.AsSpan(0, total * 13)) : null;
            expected = 0;
            if ((attributes & 0x08) != 0)
            {
                if (root && (attributes & 0x10) == 0
                    && (Encoding.Latin1.GetString(e[..11]).Trim() is { Length: > 0 } label && label.All(c => c >= ' ' && c < 0x7F)))
                    Label = label; // newer than the boot sector's copy
                continue;
            }
            var name = longName ?? ShortName(e, lowercase: true);
            if (name is "." or ".." || name.Length == 0)
                continue;
            var cluster = BinaryPrimitives.ReadUInt16LittleEndian(e[26..]) | (_bits == 32 ? (uint)BinaryPrimitives.ReadUInt16LittleEndian(e[20..]) << 16 : 0);
            var isFolder = (attributes & 0x10) != 0;
            yield return new Entry(name, isFolder, cluster, isFolder ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(e[28..]),
                DosTime(BinaryPrimitives.ReadUInt16LittleEndian(e[24..]), BinaryPrimitives.ReadUInt16LittleEndian(e[22..])));
        }
    }

    private static readonly int[] LongNameOffsets = [1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30];

    /// <summary>The characters up to the first NUL (the rest is padded with 0xFFFF).</summary>
    private static string? LongName(ReadOnlySpan<char> chars)
    {
        var end = chars.IndexOf('\0');
        var name = new string(end < 0 ? chars : chars[..end]);
        return name.Length == 0 || name.Contains('￿') ? null : name;
    }

    /// <summary>The checksum a long name stores of its 8.3 name.</summary>
    private static byte Checksum(ReadOnlySpan<byte> shortName)
    {
        byte sum = 0;
        foreach (var b in shortName)
            sum = (byte)(((sum & 1) << 7) + (sum >> 1) + b);
        return sum;
    }

    /// <summary>"README  TXT" → "README.TXT", in lowercase where the entry's flags (Windows NT and later) say so.</summary>
    private static string ShortName(ReadOnlySpan<byte> entry, bool lowercase)
    {
        Span<byte> raw = stackalloc byte[11];
        entry[..11].CopyTo(raw);
        if (raw[0] == 0x05)
            raw[0] = 0xE5; // a name that really starts with 0xE5 (Shift-JIS lead byte)
        var stem = LibraryArchives.DecodeName(raw[..8]).TrimEnd(' ');
        var extension = LibraryArchives.DecodeName(raw[8..]).TrimEnd(' ');
        if (lowercase && (entry[12] & 0x08) != 0)
            stem = stem.ToLowerInvariant();
        if (lowercase && (entry[12] & 0x10) != 0)
            extension = extension.ToLowerInvariant();
        return extension.Length == 0 ? stem : stem + "." + extension;
    }

    /// <summary>A DOS date and time (local, 2-second steps), or null when it is not a real one.</summary>
    private static DateTime? DosTime(ushort date, ushort time)
    {
        int year = 1980 + (date >> 9), month = (date >> 5) & 0x0F, day = date & 0x1F;
        int hour = time >> 11, minute = (time >> 5) & 0x3F, second = (time & 0x1F) * 2;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
            return null;
        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
    }

    /// <summary>A folder's whole chain: no loops, no more than the format allows, and never read twice.</summary>
    private byte[] ReadFolder(uint first, HashSet<uint> visited)
    {
        if (first < 2 || first > _maxCluster)
            throw ArchiveSafety.Damaged(_name, "a folder starts outside the volume");
        if (!visited.Add(first))
            throw ArchiveSafety.Damaged(_name, "its folders form a loop");
        var output = new MemoryStream();
        var cluster = first;
        var buffer = new byte[_clusterSize];
        for (var n = 0; ; n++)
        {
            if ((long)(n + 1) * _clusterSize > MaxFolderBytes + _clusterSize)
                throw ArchiveSafety.Damaged(_name, "a folder is larger than FAT allows (its clusters may loop)");
            ReadCluster(cluster, buffer, _clusterSize);
            output.Write(buffer);
            if (Next(cluster) is not { } next)
                break;
            cluster = next;
        }
        return output.ToArray();
    }

    /// <summary>
    /// The cluster after <paramref name="cluster"/>, or null at the end of the chain; a free, bad or out-of-range
    /// entry inside a chain is damage.
    /// </summary>
    private uint? Next(uint cluster)
    {
        uint value = _bits switch
        {
            12 => (uint)(FatWord(cluster + cluster / 2L) >> ((cluster & 1) != 0 ? 4 : 0)) & 0x0FFF,
            16 => FatWord(cluster * 2L),
            _ => (FatWord(cluster * 4L) | (uint)FatWord(cluster * 4L + 2) << 16) & 0x0FFFFFFF,
        };
        var endOfChain = _bits switch { 12 => 0xFF8u, 16 => 0xFFF8u, _ => 0x0FFFFFF8u };
        if (value >= endOfChain)
            return null;
        if (value < 2 || value > _maxCluster)
            throw ArchiveSafety.Damaged(_name, "a cluster chain is broken");
        return value;
    }

    /// <summary>The 16-bit little-endian value at <paramref name="offset"/> in the FAT, read a page at a time.</summary>
    private ushort FatWord(long offset) => (ushort)(FatByte(offset) | FatByte(offset + 1) << 8);

    private byte FatByte(long offset)
    {
        var page = offset / FatPage;
        if (!_fatPages.TryGetValue(page, out var bytes))
        {
            if (_fatPages.Count >= MaxFatPages)
                _fatPages.Clear();
            bytes = DiskImagePartitions.ReadAt(_volume, _fatOffset + page * FatPage, (int)Math.Min(FatPage, _fatLength - page * FatPage));
            _fatPages[page] = bytes;
        }
        var at = (int)(offset - page * FatPage);
        return at < bytes.Length ? bytes[at] : throw ArchiveSafety.Damaged(_name, "a cluster chain is broken");
    }

    private void ReadCluster(uint cluster, byte[] buffer, int count)
    {
        var offset = _dataStart + (long)(cluster - 2) * _clusterSize;
        if (offset + count > _volume.Length)
            throw ArchiveSafety.Damaged(_name, "a cluster lies beyond the end of the image");
        _volume.Position = offset;
        _volume.ReadExactly(buffer, 0, count);
    }

    /// <summary>Reads a file along its chain, as many clusters as its size needs.</summary>
    private sealed class ChainStream(DiskImageFat volume, uint first, long size) : Stream
    {
        private readonly byte[] _cluster = new byte[volume._clusterSize];
        private uint _current = first;
        private int _filled;
        private int _used;
        private readonly long _size = size;
        private long _left = size;
        private bool _started;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _size;

        public override long Position
        {
            get => _size - _left;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0 || _left == 0 && _used == _filled)
                return 0;
            if (_used == _filled)
            {
                if (_started)
                    _current = volume.Next(_current) ?? throw ArchiveSafety.Damaged(volume._name, "a file's clusters end before its size");
                _started = true;
                _filled = (int)Math.Min(_left, volume._clusterSize);
                volume.ReadCluster(_current, _cluster, _filled);
                _left -= _filled;
                _used = 0;
            }
            var n = Math.Min(buffer.Length, _filled - _used);
            _cluster.AsSpan(_used, n).CopyTo(buffer);
            _used += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
