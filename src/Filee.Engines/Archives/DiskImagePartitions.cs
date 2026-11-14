// The partitions of a disk image (a raw .img or the disk inside a DMG). Three layouts, tried in this order:
//
//   GPT (UEFI specification, chapter 5): a protective MBR (type 0xEE), then at LBA 1 the header "EFI PART" with the
//     LBA of the entry array (u64 at 72), the number of entries (u32 at 80) and the size of one (u32 at 84). An entry
//     holds the type GUID (all zeros: unused), the first and last LBA (u64 at 32 and 40, the last one included) and a
//     UTF-16LE name (72 bytes at 56). 4096-byte sectors are tried when 512-byte ones hold no header.
//   APM (Apple Partition Map, Inside Macintosh: Devices): block 0 "ER" with the block size (u16 BE at 2), then from
//     block 1 one "PM" entry per block: entry count (u32 BE at 4), first block (at 8), block count (at 12), name (32
//     bytes at 16), type (32 bytes at 48). The map itself, drivers and free space are left out.
//   MBR: four 16-byte entries at 446 (type at 4, first LBA u32 at 8, sector count at 12) and 55 AA at 510. Types 0x05,
//     0x0F and 0x85 are extended partitions: a chain of EBRs, each with one logical partition (relative to itself) and
//     the next EBR (relative to the extended partition).
//
// No table at all means one volume over the whole disk (a floppy image, or a DMG made with -layout NONE). Every table
// is checked against the disk's size before it is used: a partition may not start beyond the end (one that runs past
// it is cut there, as images are often cut after the last used sector), entry arrays are capped, and an EBR chain that
// comes back to an EBR already read is damaged.

using System.Buffers.Binary;
using System.Text;

namespace Filee.Engines.Archives;

/// <summary>One partition: its number in the table (from 1), where it lies in bytes and its name if the table has one.</summary>
internal sealed record DiskVolume(int Number, long Offset, long Length, string? Name);

/// <summary>Reads the partition table of a disk image.</summary>
internal static class DiskImagePartitions
{
    private const int MaxGptEntries = 1024;
    private const int MaxApmEntries = 256;
    private const int MaxLogicalPartitions = 128;

    /// <summary>
    /// The partitions of <paramref name="disk"/>, or a single volume over the whole disk when it has no partition
    /// table.
    /// </summary>
    public static List<DiskVolume> Find(Stream disk, string archiveName)
    {
        var length = disk.Length;
        var sector0 = ReadAt(disk, 0, 512);
        if (Gpt(disk, archiveName) is { } gpt)
            return gpt;
        if (Apm(disk, sector0, archiveName) is { } apm)
            return apm;
        if (Mbr(disk, sector0, archiveName) is { } mbr)
            return mbr;
        return [new DiskVolume(1, 0, length, null)];
    }

    private static List<DiskVolume>? Gpt(Stream disk, string archiveName)
    {
        // The protective MBR is not required (some tools write a hybrid one): the header decides.
        foreach (var sectorSize in (ReadOnlySpan<int>)[512, 4096])
        {
            var header = ReadAt(disk, sectorSize, 92);
            if (header.Length < 92 || !header.AsSpan(0, 8).SequenceEqual("EFI PART"u8))
                continue;
            var entriesLba = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(72));
            var count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80));
            var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84));
            if (entrySize < 128 || entrySize > 4096 || entrySize % 8 != 0 || count > MaxGptEntries
                || entriesLba < 2 || entriesLba > (ulong)(disk.Length / sectorSize))
                throw ArchiveSafety.Damaged(archiveName, "invalid GPT header");
            var table = ReadAt(disk, (long)entriesLba * sectorSize, (int)(count * entrySize));
            var volumes = new List<DiskVolume>();
            for (var i = 0; i < count && (i + 1) * entrySize <= table.Length; i++)
            {
                var entry = table.AsSpan((int)(i * entrySize), (int)entrySize);
                if (entry[..16].IndexOfAnyExcept((byte)0) < 0)
                    continue; // unused
                var first = BinaryPrimitives.ReadUInt64LittleEndian(entry[32..]);
                var last = BinaryPrimitives.ReadUInt64LittleEndian(entry[40..]);
                if (last < first)
                    throw ArchiveSafety.Damaged(archiveName, "a GPT partition ends before it starts");
                var name = Encoding.Unicode.GetString(entry.Slice(56, 72)).TrimEnd('\0').Trim();
                Add(volumes, disk, i + 1, first, last - first + 1, sectorSize, name, archiveName);
            }
            return volumes;
        }
        return null;
    }

    private static List<DiskVolume>? Apm(Stream disk, byte[] sector0, string archiveName)
    {
        if (sector0.Length < 512 || sector0[0] != 'E' || sector0[1] != 'R')
            return null;
        var blockSize = BinaryPrimitives.ReadUInt16BigEndian(sector0.AsSpan(2));
        if (blockSize is not (512 or 1024 or 2048 or 4096))
            blockSize = 512;
        // The map entries lie in 512-byte blocks on most disks; a map written for 2048-byte blocks (CDs) uses those.
        foreach (var entryBlock in blockSize == 512 ? (ReadOnlySpan<int>)[512] : [512, blockSize])
        {
            var first = ReadAt(disk, entryBlock, 512);
            if (first.Length < 512 || first[0] != 'P' || first[1] != 'M')
                continue;
            var count = BinaryPrimitives.ReadUInt32BigEndian(first.AsSpan(4));
            if (count is 0 or > MaxApmEntries)
                throw ArchiveSafety.Damaged(archiveName, "invalid Apple partition map");
            var volumes = new List<DiskVolume>();
            for (var i = 1; i <= count; i++)
            {
                var entry = ReadAt(disk, (long)i * entryBlock, 512);
                if (entry.Length < 512 || entry[0] != 'P' || entry[1] != 'M')
                    throw ArchiveSafety.Damaged(archiveName, "the Apple partition map is cut short");
                var start = BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(8));
                var blocks = BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(12));
                var name = Latin(entry.AsSpan(16, 32));
                var type = Latin(entry.AsSpan(48, 32));
                if (type is "Apple_partition_map" or "Apple_Free" or "Apple_Void" or "Apple_Patches" or "Apple_Extra"
                    || type.StartsWith("Apple_Driver", StringComparison.Ordinal) || type.StartsWith("Apple_FWDriver", StringComparison.Ordinal)
                    || blocks == 0)
                    continue;
                Add(volumes, disk, i, start, blocks, entryBlock, name, archiveName);
            }
            return volumes;
        }
        return null;
    }

    private static List<DiskVolume>? Mbr(Stream disk, byte[] sector0, string archiveName)
    {
        if (!IsMbr(sector0))
            return null;
        var volumes = new List<DiskVolume>();
        for (var i = 0; i < 4; i++)
        {
            var entry = sector0.AsSpan(446 + i * 16, 16);
            var type = entry[4];
            var start = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            var count = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (type == 0 || count == 0)
                continue;
            if (type is 0x05 or 0x0F or 0x85)
                Logical(disk, start, volumes, archiveName);
            else
                Add(volumes, disk, i + 1, start, count, 512, null, archiveName);
        }
        return volumes;
    }

    /// <summary>
    /// A partition table rather than a volume's boot sector: 55 AA, status bytes 0 or 0x80 and at least one entry. A
    /// FAT or NTFS boot sector at 0 (a "superfloppy") has a BPB where the entries would be and is read as a volume.
    /// </summary>
    private static bool IsMbr(byte[] sector0)
    {
        if (sector0.Length < 512 || sector0[510] != 0x55 || sector0[511] != 0xAA)
            return false;
        if (LooksLikeBootSector(sector0))
            return false;
        var used = 0;
        for (var i = 0; i < 4; i++)
        {
            var entry = sector0.AsSpan(446 + i * 16, 16);
            if (entry[0] is not (0 or 0x80))
                return false;
            if (entry[4] != 0)
                used++;
        }
        return used > 0;
    }

    /// <summary>A FAT, exFAT or NTFS boot sector: a jump instruction and a file system name or a sane BPB.</summary>
    internal static bool LooksLikeBootSector(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < 512 || sector[0] is not (0xEB or 0xE9))
            return false;
        if (sector.Slice(3, 8).SequenceEqual("NTFS    "u8) || sector.Slice(3, 8).SequenceEqual("EXFAT   "u8))
            return true;
        var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(sector[11..]);
        var sectorsPerCluster = sector[13];
        return bytesPerSector is 512 or 1024 or 2048 or 4096 && sectorsPerCluster != 0
               && (sectorsPerCluster & (sectorsPerCluster - 1)) == 0 && sector[16] is 1 or 2;
    }

    /// <summary>The logical partitions of an extended partition that starts at <paramref name="extended"/>.</summary>
    private static void Logical(Stream disk, uint extended, List<DiskVolume> volumes, string archiveName)
    {
        var visited = new HashSet<long>();
        long ebr = extended;
        for (var n = 0; ; n++)
        {
            if (n >= MaxLogicalPartitions || !visited.Add(ebr))
                throw ArchiveSafety.Damaged(archiveName, "the extended partitions form a loop");
            var sector = ReadAt(disk, ebr * 512, 512);
            if (sector.Length < 512 || sector[510] != 0x55 || sector[511] != 0xAA)
                throw ArchiveSafety.Damaged(archiveName, "an extended partition record is missing");
            var own = sector.AsSpan(446, 16);
            var next = sector.AsSpan(462, 16);
            var start = BinaryPrimitives.ReadUInt32LittleEndian(own[8..]);
            var count = BinaryPrimitives.ReadUInt32LittleEndian(own[12..]);
            if (own[4] != 0 && count != 0)
                Add(volumes, disk, 5 + n, (ulong)ebr + start, count, 512, null, archiveName);
            if (next[4] == 0)
                return;
            ebr = extended + (long)BinaryPrimitives.ReadUInt32LittleEndian(next[8..]);
        }
    }

    private static void Add(List<DiskVolume> volumes, Stream disk, int number, ulong first, ulong count, int sectorSize,
        string? name, string archiveName)
    {
        var diskSectors = (ulong)disk.Length / (ulong)sectorSize;
        if (first >= diskSectors)
            throw ArchiveSafety.Damaged(archiveName, $"partition {number} lies beyond the end of the image");
        count = Math.Min(count, diskSectors - first);
        volumes.Add(new DiskVolume(number, (long)first * sectorSize, (long)count * sectorSize, string.IsNullOrWhiteSpace(name) ? null : name));
    }

    private static string Latin(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? bytes : bytes[..end]).Trim();
    }

    /// <summary>Up to <paramref name="count"/> bytes at <paramref name="offset"/> (fewer at the end of the disk).</summary>
    internal static byte[] ReadAt(Stream disk, long offset, int count)
    {
        if (offset < 0 || offset >= disk.Length)
            return [];
        var bytes = new byte[(int)Math.Min(count, disk.Length - offset)];
        disk.Position = offset;
        disk.ReadExactly(bytes);
        return bytes;
    }
}
