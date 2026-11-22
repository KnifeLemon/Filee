// Reads ext2, ext3 and ext4 volumes (the root partition of a Raspberry Pi card or a Linux disk image), written from
// the ext4 on-disk layout the Linux kernel documents ("ext4 Data Structures and Algorithms"). Only what reading the
// files needs; the journal is not replayed (an image taken from a running system reads as it was last written).
//
//   Superblock (1024 bytes at byte 1024, little endian): inode count u32 at 0, block count u32 at 4 (high half at
//     0x150 with the 64-bit feature), first data block at 0x14, block size 1024 << u32 at 0x18, blocks and inodes per
//     group at 0x20 and 0x28, magic 0xEF53 at 0x38, revision at 0x4C (0: 128-byte inodes), inode size u16 at 0x58,
//     features at 0x5C (compatible), 0x60 (incompatible) and 0x64 (read-only compatible), label at 0x78, group
//     descriptor size at 0xFE (64-bit feature), first meta group at 0x104, backup groups at 0x24C.
//   Group descriptors (32 bytes, or the stated size with the 64-bit feature) from the block after the superblock; with
//     meta_bg, the descriptors of each meta group from the first meta group on lie in its first group (after that
//     group's superblock copy, if it has one). The inode table of a group: u32 at 8 (high half at 0x28).
//   Inode: mode u16 at 0 (0x4000 folder, 0x8000 file, others are links, devices and the like), size u32 at 4 (high
//     half at 0x6C), modified u32 at 0x10 (two more bits of its epoch at 0x88 when the inode's extra part reaches it),
//     flags u32 at 0x20 (0x800 encrypted, 0x80000 extents, 0x10000000 inline data, 0x4 compressed), 60 bytes at 0x28
//     that map the data: an extent tree (header magic 0xF30A, entry count, capacity, depth; leaves of 12 bytes:
//     first logical block, length (over 32768: unwritten, reads as zeros), start; index entries: first logical block,
//     child block), 15 block numbers (12 direct, then one, two and three levels of indirect blocks; 0 is a hole), or
//     the data itself (inline data, the rest in the "system.data" extended attribute after the inode's extra part).
//   Folders: entries of inode u32, record length u16, name length (u8, the next byte the file type, or u16), name.
//     Hashed folders (htree) keep their index inside entries of inode 0, so reading every block in order lists all
//     the names; inline folders start with the parent's inode number.
//
// Every block number is checked against the volume, extent trees against their depth (each level one less, at most
// five), folders against a size, a total and loops (a folder met twice), and a file's blocks are mapped as it is read
// (a few cached blocks per tree level), so a hostile map costs time in proportion to what is written, which the
// unpack budget bounds, never memory.

using System.Buffers.Binary;
using System.Text;

namespace Filee.Engines.Archives;

/// <summary>One ext2/3/4 volume, read in-process.</summary>
internal sealed class DiskImageExt
{
    private const int MaxDepth = 128;

    /// <summary>The most one folder may hold, and all folders together (real ones stay far below).</summary>
    private const long MaxFolderBytes = 64L * 1024 * 1024;

    private const long MaxFoldersBytes = 512L * 1024 * 1024;

    private const int RootInode = 2;
    private const int MaxExtentDepth = 5;

    private const uint IncompatCompression = 0x1, IncompatFiletype = 0x2, IncompatRecover = 0x4, IncompatJournalDevice = 0x8,
        IncompatMetaBg = 0x10, IncompatExtents = 0x40, Incompat64Bit = 0x80, IncompatMmp = 0x100, IncompatFlexBg = 0x200,
        IncompatEaInode = 0x400, IncompatDirData = 0x1000, IncompatCsumSeed = 0x2000, IncompatLargeDir = 0x4000,
        IncompatInlineData = 0x8000, IncompatEncrypt = 0x10000, IncompatCasefold = 0x20000;

    /// <summary>Incompatible features that change nothing for a reader of files, or that Filee reads.</summary>
    private const uint KnownIncompat = IncompatFiletype | IncompatRecover | IncompatMetaBg | IncompatExtents | Incompat64Bit
        | IncompatMmp | IncompatFlexBg | IncompatEaInode | IncompatCsumSeed | IncompatLargeDir | IncompatInlineData
        | IncompatEncrypt | IncompatCasefold;

    private const uint RoCompatSparseSuper = 0x1, CompatSparseSuper2 = 0x200;

    private const uint FlagCompressed = 0x4, FlagEncrypted = 0x800, FlagExtents = 0x80000, FlagInlineData = 0x10000000;

    private readonly Stream _volume;
    private readonly string _name;
    private readonly int _blockSize;
    private readonly long _blocks;
    private readonly long _firstDataBlock;
    private readonly long _blocksPerGroup;
    private readonly uint _inodesPerGroup;
    private readonly uint _inodes;
    private readonly int _inodeSize;
    private readonly int _descriptorSize;
    private readonly uint _incompat;
    private readonly bool _sparseSuper;
    private readonly bool _sparseSuper2;
    private readonly uint _firstMetaGroup;
    private readonly uint[] _backupGroups = new uint[2];
    private readonly Dictionary<long, byte[]> _cache = [];
    private long _folderBytes;

    /// <summary>The volume's label, if it has one.</summary>
    public string? Label { get; }

    /// <summary>"ext2", "ext3" or "ext4", as far as the features tell.</summary>
    public string Type { get; }

    /// <summary>An ext2/3/4 superblock magic at byte 1024 of <paramref name="head"/>.</summary>
    public static bool Detect(ReadOnlySpan<byte> head) => head.Length >= 2048 && head[1080] == 0x53 && head[1081] == 0xEF;

    public DiskImageExt(Stream volume, string archiveName)
    {
        _volume = volume;
        _name = archiveName;
        var sb = DiskImagePartitions.ReadAt(volume, 1024, 1024);
        if (sb.Length < 1024 || BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(0x38)) != 0xEF53)
            throw Damaged("invalid superblock");
        var s = sb.AsSpan();
        var logBlock = BinaryPrimitives.ReadUInt32LittleEndian(s[0x18..]);
        if (logBlock > 6)
            throw Damaged("invalid block size");
        _blockSize = 1024 << (int)logBlock;
        _incompat = BinaryPrimitives.ReadUInt32LittleEndian(s[0x60..]);
        var compat = BinaryPrimitives.ReadUInt32LittleEndian(s[0x5C..]);
        var roCompat = BinaryPrimitives.ReadUInt32LittleEndian(s[0x64..]);
        if ((_incompat & IncompatJournalDevice) != 0)
            throw new NotSupportedException("an ext3/4 journal device (it holds no files)");
        if ((_incompat & (IncompatCompression | IncompatDirData)) != 0 || (_incompat & ~KnownIncompat) != 0)
            throw new NotSupportedException($"ext2/3/4 volumes with the features 0x{_incompat & ~KnownIncompat | _incompat & (IncompatCompression | IncompatDirData):X}");
        var is64 = (_incompat & Incompat64Bit) != 0;
        _inodes = BinaryPrimitives.ReadUInt32LittleEndian(s);
        _blocks = BinaryPrimitives.ReadUInt32LittleEndian(s[4..]) | (is64 ? (long)BinaryPrimitives.ReadUInt32LittleEndian(s[0x150..]) << 32 : 0);
        _firstDataBlock = BinaryPrimitives.ReadUInt32LittleEndian(s[0x14..]);
        _blocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(s[0x20..]);
        _inodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(s[0x28..]);
        var revision = BinaryPrimitives.ReadUInt32LittleEndian(s[0x4C..]);
        _inodeSize = revision == 0 ? 128 : BinaryPrimitives.ReadUInt16LittleEndian(s[0x58..]);
        _descriptorSize = is64 ? BinaryPrimitives.ReadUInt16LittleEndian(s[0xFE..]) : 32;
        if (_blocks == 0 || _blocks > long.MaxValue / _blockSize || _firstDataBlock >= _blocks
            || _blocksPerGroup is 0 || _blocksPerGroup > 8L * _blockSize || _inodesPerGroup == 0 || _inodesPerGroup > 8L * _blockSize
            || _inodes == 0 || _inodeSize < 128 || _inodeSize > _blockSize || (_inodeSize & (_inodeSize - 1)) != 0
            || _descriptorSize < 32 || _descriptorSize > _blockSize || (_descriptorSize & (_descriptorSize - 1)) != 0
            || is64 && _descriptorSize < 64)
            throw Damaged("invalid superblock");
        var groups = (_blocks - _firstDataBlock + _blocksPerGroup - 1) / _blocksPerGroup;
        if ((long)_inodes > groups * _inodesPerGroup)
            throw Damaged("the superblock counts more inodes than its groups hold");
        _sparseSuper = (roCompat & RoCompatSparseSuper) != 0;
        _sparseSuper2 = (compat & CompatSparseSuper2) != 0;
        _firstMetaGroup = (_incompat & IncompatMetaBg) != 0 ? BinaryPrimitives.ReadUInt32LittleEndian(s[0x104..]) : uint.MaxValue;
        _backupGroups[0] = BinaryPrimitives.ReadUInt32LittleEndian(s[0x24C..]);
        _backupGroups[1] = BinaryPrimitives.ReadUInt32LittleEndian(s[0x250..]);

        var label = s.Slice(0x78, 16);
        var end = label.IndexOf((byte)0);
        var text = Encoding.UTF8.GetString(end < 0 ? label : label[..end]).Trim();
        Label = text.Length > 0 && !text.Any(char.IsControl) ? text : null;
        Type = (_incompat & (IncompatExtents | Incompat64Bit | IncompatFlexBg)) != 0 ? "ext4" : (compat & 0x4) != 0 ? "ext3" : "ext2";
    }

    private Exception Damaged(string detail) => ArchiveSafety.Damaged(_name, detail);

    /// <summary>
    /// The files and folders below the root, their paths under <paramref name="prefix"/> ("" for the top), counted
    /// against <paramref name="nodes"/>' limit.
    /// </summary>
    public void Walk(string prefix, List<DiskImageNode> nodes, NameTally names, CancellationToken cancellationToken)
    {
        var visited = new HashSet<uint> { RootInode };
        var stack = new Stack<(Inode Folder, string Path, int Depth)>();
        var root = ReadInode(RootInode);
        if (!root.IsFolder)
            throw Damaged("the root is not a folder");
        stack.Push((root, prefix, 0));
        while (stack.TryPop(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var (number, name) in Entries(item.Folder))
            {
                if (nodes.Count >= ArchiveSafety.EntryLimit)
                    throw ArchiveSafety.TooManyEntries(_name);
                var path = item.Path.Length == 0 ? name : item.Path + "/" + name;
                names.Add(path);
                var inode = ReadInode(number);
                if (inode.IsFolder)
                {
                    if (!visited.Add(number))
                        throw Damaged("its folders form a loop");
                    if (item.Depth >= MaxDepth)
                        throw Damaged("the folders are nested too deeply");
                    nodes.Add(new DiskImageNode(path, true, false, 0, inode.Modified, null));
                    stack.Push((inode, path, item.Depth + 1));
                    continue;
                }
                if (!inode.IsFile)
                {
                    nodes.Add(new DiskImageNode(path, false, true, 0, inode.Modified, null)); // links, devices: left out
                    continue;
                }
                if ((inode.Flags & FlagCompressed) != 0)
                    throw new NotSupportedException("compressed ext2 files");
                nodes.Add(new DiskImageNode(path, false, false, inode.Size, inode.Modified, () => new FileData(this, inode)));
            }
        }
    }

    // ───────── Inodes ─────────

    /// <summary>What Filee needs of an inode.</summary>
    private sealed record Inode(uint Number, ushort Mode, long Size, uint Flags, byte[] Map, byte[] Raw, DateTime? Modified)
    {
        public bool IsFolder => (Mode & 0xF000) == 0x4000;
        public bool IsFile => (Mode & 0xF000) == 0x8000;
    }

    private Inode ReadInode(uint number)
    {
        if (number == 0 || number > _inodes)
            throw Damaged($"an inode number ({number}) lies outside the volume");
        var group = (number - 1) / _inodesPerGroup;
        var index = (number - 1) % _inodesPerGroup;
        var table = InodeTable(group);
        var offset = (long)index * _inodeSize;
        var block = Block(table + offset / _blockSize);
        var raw = block.AsSpan((int)(offset % _blockSize), _inodeSize).ToArray();
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x20));
        if ((flags & FlagEncrypted) != 0)
            throw ArchiveSafety.Encrypted(_name);
        var mode = BinaryPrimitives.ReadUInt16LittleEndian(raw);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4)) | (long)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x6C)) << 32;
        if (size < 0)
            throw Damaged("a file is larger than any volume");
        long seconds = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(0x10));
        var extra = _inodeSize > 128 ? BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0x80)) : 0;
        if (extra >= 0x0C && 0x80 + extra <= _inodeSize)
            seconds += (long)(BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x88)) & 3) << 32;
        return new Inode(number, mode, size, flags, raw.AsSpan(0x28, 60).ToArray(), raw, Time(seconds));
    }

    private static DateTime? Time(long seconds)
    {
        if (seconds <= 0)
            return null;
        var time = DateTimeOffset.FromUnixTimeSeconds(Math.Min(seconds, 253_402_300_799)).LocalDateTime;
        return time.Year is < 1980 or > 9999 ? null : time;
    }

    /// <summary>The first block of a group's inode table, from its descriptor.</summary>
    private long InodeTable(uint group)
    {
        var perBlock = _blockSize / _descriptorSize;
        var descriptorBlock = group / perBlock;
        long block;
        if (descriptorBlock < _firstMetaGroup)
        {
            block = _firstDataBlock + 1 + descriptorBlock;
        }
        else
        {
            // meta_bg: the descriptors of a meta group sit in its first group, after its superblock copy if any.
            var first = (long)descriptorBlock * perBlock;
            block = _firstDataBlock + first * _blocksPerGroup + (HasSuperblock(first) ? 1 : 0);
        }
        var bytes = Block(block);
        var at = (int)(group % perBlock) * _descriptorSize;
        var table = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 8))
                    | (_descriptorSize >= 64 ? (long)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 0x28)) << 32 : 0);
        var tableBlocks = ((long)_inodesPerGroup * _inodeSize + _blockSize - 1) / _blockSize;
        if (table <= _firstDataBlock || table > _blocks - tableBlocks)
            throw Damaged("an inode table lies outside the volume");
        return table;
    }

    /// <summary>Whether a group holds a copy of the superblock (and so of the descriptors).</summary>
    private bool HasSuperblock(long group)
    {
        if (group == 0)
            return true;
        if (_sparseSuper2)
            return group == _backupGroups[0] || group == _backupGroups[1];
        if (!_sparseSuper || group == 1)
            return true;
        foreach (var root in (ReadOnlySpan<int>)[3, 5, 7])
        {
            var power = (long)root;
            while (power < group)
                power *= root;
            if (power == group)
                return true;
        }
        return false;
    }

    /// <summary>One block of the volume, from a small cache (the inode tables and maps are read over and over).</summary>
    private byte[] Block(long number)
    {
        if (number < 0 || number >= _blocks)
            throw Damaged("a block number lies outside the volume");
        if (_cache.TryGetValue(number, out var cached))
            return cached;
        var bytes = DiskImagePartitions.ReadAt(_volume, number * _blockSize, _blockSize);
        if (bytes.Length < _blockSize)
            throw Damaged("a block lies beyond the end of the image");
        if (_cache.Count >= 256)
            _cache.Clear();
        _cache[number] = bytes;
        return bytes;
    }

    // ───────── Folders ─────────

    /// <summary>The entries of a folder (inode number and name), "." and ".." and unused ones left out.</summary>
    private List<(uint Number, string Name)> Entries(Inode folder)
    {
        if (folder.Size > MaxFolderBytes)
            throw ArchiveSafety.Oversized(_name, $"a folder is larger than {ArchiveSafety.FormatSize(MaxFolderBytes)}");
        _folderBytes += folder.Size;
        if (_folderBytes > MaxFoldersBytes)
            throw Damaged("its folders lead in circles");
        var result = new List<(uint, string)>();
        byte[] bytes;
        int start;
        if ((folder.Flags & FlagInlineData) != 0)
        {
            bytes = InlineData(folder, int.MaxValue);
            start = 4; // the parent's inode number
            Parse(bytes.AsSpan(0, Math.Min(bytes.Length, 60)), start, result);
            if (bytes.Length > 60)
                Parse(bytes.AsSpan(60), 0, result);
            return result;
        }
        using var data = new FileData(this, folder);
        bytes = new byte[folder.Size];
        data.ReadExactly(bytes);
        for (var at = 0L; at < bytes.Length; at += _blockSize)
            Parse(bytes.AsSpan((int)at, (int)Math.Min(_blockSize, bytes.Length - at)), 0, result);
        return result;
    }

    /// <summary>The entries of one folder block (or inline area) from <paramref name="start"/>.</summary>
    private void Parse(ReadOnlySpan<byte> block, int start, List<(uint, string)> result)
    {
        var filetype = (_incompat & IncompatFiletype) != 0;
        for (var at = start; at + 8 <= block.Length;)
        {
            var number = BinaryPrimitives.ReadUInt32LittleEndian(block[at..]);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(block[(at + 4)..]);
            if (length is 0 or 65535 && _blockSize == 65536)
                length = 65536;
            int nameLength = filetype ? block[at + 6] : BinaryPrimitives.ReadUInt16LittleEndian(block[(at + 6)..]);
            if (length < 8 || length % 4 != 0 || at + length > block.Length || nameLength > length - 8)
                throw Damaged("a folder entry is out of bounds");
            if (number != 0 && nameLength > 0)
            {
                var name = Encoding.UTF8.GetString(block.Slice(at + 8, nameLength));
                if (name is not ("." or ".."))
                    result.Add((number, name.Replace('/', '_').Replace('\0', '_')));
            }
            at += length;
        }
    }

    // ───────── Data ─────────

    /// <summary>
    /// The data of an inline file or folder: the 60 bytes of the map and then the value of its "system.data" extended
    /// attribute (in the inode, after its extra part), as far as its size goes.
    /// </summary>
    private byte[] InlineData(Inode inode, long limit)
    {
        var size = Math.Min(inode.Size, limit);
        if (size > 60 + _inodeSize)
            throw Damaged("inline data is larger than an inode holds");
        var result = new byte[size];
        inode.Map.AsSpan(0, (int)Math.Min(60, size)).CopyTo(result);
        if (size <= 60)
            return result;
        var raw = inode.Raw.AsSpan();
        var extra = _inodeSize > 128 ? BinaryPrimitives.ReadUInt16LittleEndian(raw[0x80..]) : 0;
        var at = 128 + extra;
        if (at + 4 > raw.Length || BinaryPrimitives.ReadUInt32LittleEndian(raw[at..]) != 0xEA020000)
            throw Damaged("inline data is missing its attribute");
        var entries = raw[(at + 4)..];
        for (var e = 0; e + 16 <= entries.Length && BinaryPrimitives.ReadUInt32LittleEndian(entries[e..]) != 0;)
        {
            int nameLength = entries[e];
            var index = entries[e + 1];
            int valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(entries[(e + 2)..]);
            var valueSize = BinaryPrimitives.ReadUInt32LittleEndian(entries[(e + 8)..]);
            if (e + 16 + nameLength > entries.Length)
                break;
            if (index == 7 && entries.Slice(e + 16, nameLength).SequenceEqual("data"u8))
            {
                if (valueOffset + valueSize > entries.Length || valueSize < size - 60)
                    throw Damaged("inline data is out of bounds");
                entries.Slice(valueOffset, (int)(size - 60)).CopyTo(result.AsSpan(60));
                return result;
            }
            e += (16 + nameLength + 3) & ~3;
        }
        throw Damaged("inline data is missing its attribute");
    }

    /// <summary>
    /// Where the data at logical block <paramref name="logical"/> of <paramref name="inode"/> lies: a run of
    /// <c>Count</c> blocks from <c>Physical</c> (<see cref="Hole"/>: a hole or unwritten blocks, read as zeros), at
    /// least one.
    /// </summary>
    private (long Physical, long Count) Map(Inode inode, long logical, long fileBlocks, MapCache cache)
    {
        if ((inode.Flags & FlagExtents) != 0)
            return MapExtent(inode.Map, logical, fileBlocks, cache);
        return MapIndirect(inode.Map, logical, fileBlocks, cache);
    }

    private const long Hole = -1;

    private (long, long) MapExtent(byte[] root, long logical, long fileBlocks, MapCache cache)
    {
        var node = root.AsSpan();
        var depth = -1;
        var bound = fileBlocks; // where the subtree of the node walked into ends
        for (var level = 0; ; level++)
        {
            if (node.Length < 12 || BinaryPrimitives.ReadUInt16LittleEndian(node) != 0xF30A)
                throw Damaged("an extent tree node is invalid");
            int count = BinaryPrimitives.ReadUInt16LittleEndian(node[2..]);
            int max = BinaryPrimitives.ReadUInt16LittleEndian(node[4..]);
            int nodeDepth = BinaryPrimitives.ReadUInt16LittleEndian(node[6..]);
            if (count > max || 12 + max * 12 > node.Length || nodeDepth > MaxExtentDepth || depth >= 0 && nodeDepth != depth - 1)
                throw Damaged("an extent tree node is invalid");
            depth = nodeDepth;
            if (depth == 0)
            {
                // The extent that holds the block, or the hole up to the next one.
                var next = bound;
                for (var i = 0; i < count; i++)
                {
                    var e = node.Slice(12 + i * 12, 12);
                    long first = BinaryPrimitives.ReadUInt32LittleEndian(e);
                    int length = BinaryPrimitives.ReadUInt16LittleEndian(e[4..]);
                    var unwritten = length > 32768;
                    if (unwritten)
                        length -= 32768;
                    var start = (long)BinaryPrimitives.ReadUInt16LittleEndian(e[6..]) << 32 | BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
                    if (length == 0)
                        continue;
                    if (logical >= first && logical < first + length)
                    {
                        if (!unwritten && (start < _firstDataBlock || start > _blocks - length))
                            throw Damaged("an extent lies outside the volume");
                        var run = Math.Min(first + length, bound) - logical;
                        return (unwritten ? Hole : start + (logical - first), Math.Max(1, run));
                    }
                    if (first > logical)
                        next = Math.Min(next, first);
                }
                return (Hole, Math.Max(1, next - logical));
            }
            // The index entry whose range holds the block: the one with the greatest first block not after it.
            var child = -1L;
            var childFirst = -1L;
            for (var i = 0; i < count; i++)
            {
                var e = node.Slice(12 + i * 12, 12);
                long first = BinaryPrimitives.ReadUInt32LittleEndian(e);
                if (first <= logical && first > childFirst)
                {
                    childFirst = first;
                    child = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]) | (long)BinaryPrimitives.ReadUInt16LittleEndian(e[8..]) << 32;
                }
                else if (first > logical)
                {
                    bound = Math.Min(bound, first);
                }
            }
            if (child < 0)
                return (Hole, Math.Max(1, bound - logical)); // before the first index entry: a hole up to it
            node = cache.Get(level, child, this);
        }
    }

    private (long, long) MapIndirect(byte[] map, long logical, long fileBlocks, MapCache cache)
    {
        var perBlock = _blockSize / 4;
        if (logical < 12)
            return Pointers(map.AsSpan((int)logical * 4, (12 - (int)logical) * 4), fileBlocks - logical);
        var rest = logical - 12;
        long span = perBlock;
        for (var levels = 1; levels <= 3; levels++, span *= perBlock)
        {
            if (rest >= span)
            {
                rest -= span;
                continue;
            }
            var block = (long)BinaryPrimitives.ReadUInt32LittleEndian(map.AsSpan((11 + levels) * 4));
            // Down the levels: each holds perBlock pointers, each covering a perBlock-th of the level above.
            var cover = span;
            for (var level = 0; level < levels; level++)
            {
                if (block == 0)
                    return (Hole, Math.Max(1, Math.Min(cover - rest % cover, fileBlocks - logical)));
                cover /= perBlock;
                var pointers = cache.Get(level, block, this);
                var index = (int)(rest / cover % perBlock);
                if (level == levels - 1)
                    return Pointers(pointers.AsSpan(index * 4, (perBlock - index) * 4), fileBlocks - logical);
                block = BinaryPrimitives.ReadUInt32LittleEndian(pointers.AsSpan(index * 4));
            }
        }
        throw Damaged("a file is larger than its block map reaches");
    }

    /// <summary>The run that starts at the first of <paramref name="pointers"/>: consecutive blocks, or a hole (0).</summary>
    private (long, long) Pointers(ReadOnlySpan<byte> pointers, long left)
    {
        long first = BinaryPrimitives.ReadUInt32LittleEndian(pointers);
        var count = 1L;
        for (var i = 1; i < pointers.Length / 4 && count < left; i++)
        {
            long next = BinaryPrimitives.ReadUInt32LittleEndian(pointers[(i * 4)..]);
            if (first == 0 ? next != 0 : next != first + count)
                break;
            count++;
        }
        if (first == 0)
            return (Hole, count);
        if (first < _firstDataBlock || first > _blocks - count)
            throw Damaged("a block number lies outside the volume");
        return (first, count);
    }

    /// <summary>The last block read at each level of a file's map.</summary>
    private sealed class MapCache
    {
        private readonly long[] _numbers = [-1, -1, -1, -1, -1, -1];
        private readonly byte[]?[] _blocks = new byte[6][];

        public byte[] Get(int level, long block, DiskImageExt volume)
        {
            if (level >= _numbers.Length)
                throw volume.Damaged("an extent tree is too deep");
            if (_numbers[level] != block)
            {
                if (block < volume._firstDataBlock || block >= volume._blocks)
                    throw volume.Damaged("a block map points outside the volume");
                _blocks[level] = DiskImagePartitions.ReadAt(volume._volume, block * volume._blockSize, volume._blockSize);
                if (_blocks[level]!.Length < volume._blockSize)
                    throw volume.Damaged("a block lies beyond the end of the image");
                _numbers[level] = block;
            }
            return _blocks[level]!;
        }
    }

    /// <summary>A file's data, mapped run by run as it is read.</summary>
    private sealed class FileData(DiskImageExt volume, Inode inode) : Stream
    {
        private readonly MapCache _cache = new();
        private readonly long _fileBlocks = (inode.Size + volume._blockSize - 1) / volume._blockSize;
        private byte[]? _inline;
        private long _position;
        private long _runStart = -1; // logical block of the current run
        private long _runPhysical;
        private long _runCount;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inode.Size;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var left = inode.Size - _position;
            if (buffer.Length == 0 || left <= 0)
                return 0;
            if (buffer.Length > left)
                buffer = buffer[..(int)left];
            if ((inode.Flags & FlagInlineData) != 0)
            {
                _inline ??= volume.InlineData(inode, inode.Size);
                var n = Math.Min(buffer.Length, (int)(_inline.Length - _position));
                if (n <= 0)
                    throw volume.Damaged("a file's data ends before its size");
                _inline.AsSpan((int)_position, n).CopyTo(buffer);
                _position += n;
                return n;
            }
            var blockSize = volume._blockSize;
            var logical = _position / blockSize;
            if (_runStart < 0 || logical < _runStart || logical >= _runStart + _runCount)
            {
                (_runPhysical, _runCount) = volume.Map(inode, logical, _fileBlocks, _cache);
                _runStart = logical;
            }
            var within = _position - _runStart * blockSize;
            var available = _runCount * blockSize - within;
            var take = (int)Math.Min(Math.Min(buffer.Length, available), 1 << 20);
            if (_runPhysical == Hole)
            {
                buffer[..take].Clear();
            }
            else
            {
                var at = _runPhysical * blockSize + within;
                if (at + take > volume._volume.Length)
                    throw volume.Damaged("a block lies beyond the end of the image");
                volume._volume.Position = at;
                volume._volume.ReadExactly(buffer[..take]);
            }
            _position += take;
            return take;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
