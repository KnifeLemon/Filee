// Writes disk images in code for the disk image tests: FAT12/16/32 volumes with long names (Microsoft's FAT
// specification), UDF discs (ECMA-167 and OSTA UDF 1.02 / 2.01, optionally behind an ISO 9660 bridge), MBR, GPT and
// Apple partition maps around volumes, and DMG (UDIF) files around a disk with raw, zero, zlib, bzip2 and ADC runs.
// Each follows the format's own description, independently of the readers in Filee.Engines/Archives; the bundled
// 7-Zip, where present, reads the same images (DiskImageTests).

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using static Filee.Engines.Tests.ArchiveBuilders;

namespace Filee.Engines.Tests;

/// <summary>How <see cref="DiskImageBuilders.Dmg"/> stores a run.</summary>
public enum DmgRun
{
    Raw,
    Zlib,
    Bzip2,
    Adc,

    /// <summary>Declared LZFSE (the data is stored, never decoded).</summary>
    Lzfse,
}

internal static class DiskImageBuilders
{
    // ───────────────────────── Folder trees ─────────────────────────

    /// <summary>The tree of <paramref name="entries"/>: folder path → its children (name, entry or null for a folder).</summary>
    private static SortedDictionary<string, List<(string Name, TestEntry? File)>> Tree(IReadOnlyList<TestEntry> entries)
    {
        var folders = new SortedDictionary<string, List<(string Name, TestEntry? File)>>(StringComparer.Ordinal) { [""] = [] };
        void Ensure(string path)
        {
            if (folders.ContainsKey(path))
                return;
            var parent = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
            Ensure(parent);
            folders[path] = [];
            folders[parent].Add((path[(path.LastIndexOf('/') + 1)..], null));
        }
        foreach (var entry in entries)
        {
            var path = entry.Name.TrimEnd('/');
            if (entry.IsFolder)
            {
                Ensure(path);
                continue;
            }
            var parent = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
            Ensure(parent);
            folders[parent].Add((path[(path.LastIndexOf('/') + 1)..], entry));
        }
        return folders;
    }

    // ───────────────────────── FAT ─────────────────────────

    /// <summary>
    /// A FAT volume (12, 16 or 32 bits) of <paramref name="entries"/>: names that are not plain 8.3 capitals get long
    /// names (and an 8.3 alias "NAME~1"), all-lowercase 8.3 names the lowercase flags instead, as Windows writes them.
    /// <paramref name="loop"/> makes the last data cluster of the first folder chain point back at its first one.
    /// </summary>
    public static byte[] Fat(IReadOnlyList<TestEntry> entries, int bits, string label = "FILEE TEST", bool loop = false, int floppySectors = 2880)
    {
        // Geometry: a floppy (FAT12, 1.44 MB unless smaller), 16 MB with 2 KB clusters (FAT16), 34 MB with 512-byte
        // clusters (FAT32).
        var (totalSectors, perCluster, reserved, rootEntries) = bits switch
        {
            12 => (floppySectors, 1, 1, 224),
            16 => (32768, 4, 4, 512),
            _ => (69632, 1, 32, 0),
        };
        const int bps = 512;
        var rootSectors = (rootEntries * 32 + bps - 1) / bps;
        var clusters = (totalSectors - reserved - rootSectors) / perCluster; // upper bound, fixed below
        var fatSize = (int)Math.Ceiling((clusters + 2) * bits / 8.0 / bps);
        clusters = (totalSectors - reserved - 2 * fatSize - rootSectors) / perCluster;
        var clusterSize = bps * perCluster;
        var image = new byte[totalSectors * bps];
        var fat = new uint[clusters + 2];
        fat[0] = bits switch { 12 => 0xFF8, 16 => 0xFFF8, _ => 0x0FFFFFF8 };
        fat[1] = bits switch { 12 => 0xFFF, 16 => 0xFFFF, _ => 0x0FFFFFFF };
        var eoc = bits switch { 12 => 0xFFFu, 16 => 0xFFFFu, _ => 0x0FFFFFFFu };
        var dataStart = (reserved + 2 * fatSize + rootSectors) * bps;
        uint next = 2;

        uint Allocate(byte[] data, bool chainLoop = false)
        {
            if (data.Length == 0)
                return 0;
            var count = (data.Length + clusterSize - 1) / clusterSize;
            var first = next;
            for (var i = 0; i < count; i++)
            {
                fat[next] = i == count - 1 ? (chainLoop ? first : eoc) : next + 1;
                data.AsSpan(i * clusterSize, Math.Min(clusterSize, data.Length - i * clusterSize)).CopyTo(image.AsSpan(dataStart + (int)(next - 2) * clusterSize));
                next++;
            }
            return first;
        }

        var tree = Tree(entries);
        var aliases = new Dictionary<string, int>(StringComparer.Ordinal);
        var looped = false;
        // Folders deepest first, so a folder's entry can name its children's clusters; "." and ".." are patched after.
        var firstCluster = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var path in tree.Keys.OrderByDescending(p => p.Length == 0 ? -1 : p.Count(c => c == '/')).ThenBy(p => p, StringComparer.Ordinal))
        {
            var records = new MemoryStream();
            if (path.Length > 0)
            {
                records.Write(ShortEntry(".          "u8, 0x10, 0, 0, 0, KnownTime));
                records.Write(ShortEntry("..         "u8, 0x10, 0, 0, 0, KnownTime));
            }
            else if (label.Length > 0)
                records.Write(ShortEntry(Encoding.ASCII.GetBytes(label.PadRight(11)[..11]), 0x08, 0, 0, 0, KnownTime));
            foreach (var (name, file) in tree[path])
            {
                var childPath = path.Length == 0 ? name : path + "/" + name;
                var data = file?.Data ?? [];
                var cluster = file is null ? firstCluster[childPath] : Allocate(data);
                var (shortName, flags, needsLong) = ShortName(name, path, aliases);
                if (needsLong)
                    foreach (var part in LongEntries(name, shortName))
                        records.Write(part);
                records.Write(ShortEntry(shortName, (byte)(file is null ? 0x10 : 0x20), flags, cluster, file is null ? 0 : data.Length, file?.Time ?? KnownTime));
            }
            var bytes = records.ToArray();
            if (path.Length == 0 && bits != 32)
            {
                if (bytes.Length > rootEntries * 32)
                    throw new InvalidOperationException("too many root entries");
                bytes.CopyTo(image, (reserved + 2 * fatSize) * bps);
                continue;
            }
            // Pad the folder to whole clusters so the end marker (a zero first byte) follows the last entry.
            Array.Resize(ref bytes, Math.Max(clusterSize, (bytes.Length + 32 + clusterSize - 1) / clusterSize * clusterSize));
            var chainLoop = loop && !looped && path.Length > 0;
            looped |= chainLoop;
            firstCluster[path] = Allocate(bytes, chainLoop);
        }
        // "." and "..": the folder itself and its parent (0 for the root).
        foreach (var (path, cluster) in firstCluster)
        {
            if (path.Length == 0)
                continue;
            var parent = path.Contains('/') ? firstCluster[path[..path.LastIndexOf('/')]] : 0u;
            var at = dataStart + (int)(cluster - 2) * clusterSize;
            SetCluster(image.AsSpan(at), cluster, bits);
            SetCluster(image.AsSpan(at + 32), bits == 32 && parent == firstCluster.GetValueOrDefault("") ? 0 : parent, bits);
        }

        // Boot sector.
        var boot = image.AsSpan(0, 512);
        boot[0] = 0xEB;
        boot[1] = bits == 32 ? (byte)0x58 : (byte)0x3C; // the boot code starts after the BPB (90 or 62 bytes)
        boot[2] = 0x90;
        "FILEE1.0"u8.CopyTo(boot[3..]);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[11..], bps);
        boot[13] = (byte)perCluster;
        BinaryPrimitives.WriteUInt16LittleEndian(boot[14..], (ushort)reserved);
        boot[16] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(boot[17..], (ushort)rootEntries);
        if (totalSectors < 65536)
            BinaryPrimitives.WriteUInt16LittleEndian(boot[19..], (ushort)totalSectors);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(boot[32..], (uint)totalSectors);
        boot[21] = bits == 12 ? (byte)0xF0 : (byte)0xF8;
        BinaryPrimitives.WriteUInt16LittleEndian(boot[24..], 18);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[26..], 2);
        var labelBytes = Encoding.ASCII.GetBytes((label.Length > 0 ? label : "NO NAME").PadRight(11)[..11]);
        if (bits == 32)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(boot[36..], (uint)fatSize);
            BinaryPrimitives.WriteUInt32LittleEndian(boot[44..], firstCluster[""]);
            BinaryPrimitives.WriteUInt16LittleEndian(boot[48..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(boot[50..], 6);
            boot[64] = 0x80;
            boot[66] = 0x29;
            BinaryPrimitives.WriteUInt32LittleEndian(boot[67..], 0x20261114);
            labelBytes.CopyTo(boot[71..]);
            "FAT32   "u8.CopyTo(boot[82..]);
            // FSInfo
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(512), 0x41615252);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(512 + 484), 0x61417272);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(512 + 488), 0xFFFFFFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(512 + 492), 0xFFFFFFFF);
            image[512 + 510] = 0x55;
            image[512 + 511] = 0xAA;
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(boot[22..], (ushort)fatSize);
            boot[36] = 0x80;
            boot[38] = 0x29;
            BinaryPrimitives.WriteUInt32LittleEndian(boot[39..], 0x20261114);
            labelBytes.CopyTo(boot[43..]);
            (bits == 12 ? "FAT12   "u8 : "FAT16   "u8).CopyTo(boot[54..]);
        }
        boot[510] = 0x55;
        boot[511] = 0xAA;

        // Both FATs.
        var table = new byte[fatSize * bps];
        for (var c = 0; c < fat.Length; c++)
        {
            switch (bits)
            {
                case 12:
                    var at = c + c / 2;
                    var value = BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(at));
                    value = (c & 1) == 0 ? (ushort)((value & 0xF000u) | fat[c]) : (ushort)((value & 0x000Fu) | (fat[c] << 4));
                    BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(at), value);
                    break;
                case 16:
                    BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(c * 2), (ushort)fat[c]);
                    break;
                default:
                    BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(c * 4), fat[c]);
                    break;
            }
        }
        table.CopyTo(image, reserved * bps);
        table.CopyTo(image, (reserved + fatSize) * bps);
        return image;
    }

    private static void SetCluster(Span<byte> entry, uint cluster, int bits)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(entry[26..], (ushort)cluster);
        if (bits == 32)
            BinaryPrimitives.WriteUInt16LittleEndian(entry[20..], (ushort)(cluster >> 16));
    }

    private static byte[] ShortEntry(ReadOnlySpan<byte> name, byte attributes, byte flags, uint cluster, int size, DateTime time)
    {
        var e = new byte[32];
        name.CopyTo(e);
        e[11] = attributes;
        e[12] = flags;
        var dosTime = (ushort)((time.Hour << 11) | (time.Minute << 5) | (time.Second / 2));
        var dosDate = (ushort)(((time.Year - 1980) << 9) | (time.Month << 5) | time.Day);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(14), dosTime);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(16), dosDate);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(18), dosDate);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(20), (ushort)(cluster >> 16));
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(22), dosTime);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(24), dosDate);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(26), (ushort)cluster);
        BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(28), (uint)size);
        return e;
    }

    /// <summary>The 11-byte 8.3 name, the lowercase flags and whether a long name is needed.</summary>
    private static (byte[] Name, byte Flags, bool Long) ShortName(string name, string folder, Dictionary<string, int> aliases)
    {
        static bool Plain(string part, int max) => part.Length is > 0 && part.Length <= max
            && part.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-');
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var extension = dot > 0 ? name[(dot + 1)..] : "";
        if (Plain(stem, 8) && (extension.Length == 0 || Plain(extension, 3)))
            return (Encoding.ASCII.GetBytes(stem.PadRight(8) + extension.PadRight(3)), 0, false);
        if (Plain(stem.ToUpperInvariant(), 8) && (extension.Length == 0 || Plain(extension.ToUpperInvariant(), 3))
            && stem == stem.ToLowerInvariant() && extension == extension.ToLowerInvariant())
            return (Encoding.ASCII.GetBytes(stem.ToUpperInvariant().PadRight(8) + extension.ToUpperInvariant().PadRight(3)), 0x18, false);
        var basis = new string([.. stem.ToUpperInvariant().Where(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9')]);
        if (basis.Length == 0)
            basis = "FILE";
        var key = folder + "/" + basis[..Math.Min(6, basis.Length)];
        var n = aliases[key] = aliases.GetValueOrDefault(key) + 1;
        var aliasStem = basis[..Math.Min(6, basis.Length)] + "~" + n;
        var aliasExtension = new string([.. extension.ToUpperInvariant().Where(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9')]);
        return (Encoding.ASCII.GetBytes(aliasStem.PadRight(8) + aliasExtension[..Math.Min(3, aliasExtension.Length)].PadRight(3)), 0, true);
    }

    /// <summary>The long-name parts of <paramref name="name"/>, last part first as they lie on disk.</summary>
    private static List<byte[]> LongEntries(string name, byte[] shortName)
    {
        byte sum = 0;
        foreach (var b in shortName)
            sum = (byte)(((sum & 1) << 7) + (sum >> 1) + b);
        var chars = name.ToCharArray();
        var count = (chars.Length + 12) / 13;
        var parts = new List<byte[]>();
        int[] offsets = [1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30];
        for (var order = count; order >= 1; order--)
        {
            var e = new byte[32];
            e[0] = (byte)(order | (order == count ? 0x40 : 0));
            e[11] = 0x0F;
            e[13] = sum;
            for (var i = 0; i < 13; i++)
            {
                var index = (order - 1) * 13 + i;
                var c = index < chars.Length ? chars[index] : index == chars.Length ? '\0' : '￿';
                BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(offsets[i]), c);
            }
            parts.Add(e);
        }
        return parts;
    }

    /// <summary>A FAT volume written by DiscUtils.Fat (8.3 names only): an independent writer for the reader.</summary>
    public static byte[] DiscUtilsFat(IReadOnlyList<TestEntry> entries)
    {
        using var stream = new MemoryStream();
        using (var fs = DiscUtils.Fat.FatFileSystem.FormatFloppy(stream, DiscUtils.FloppyDiskType.HighDensity, "DISCUTILS"))
        {
            foreach (var entry in entries)
            {
                var path = entry.Name.TrimEnd('/').Replace('/', '\\');
                if (entry.IsFolder)
                {
                    fs.CreateDirectory(path);
                    continue;
                }
                var folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder))
                    fs.CreateDirectory(folder);
                using (var file = fs.OpenFile(path, FileMode.Create, FileAccess.Write))
                    file.Write(entry.Data);
                fs.SetLastWriteTime(path, entry.Time);
            }
        }
        return stream.ToArray();
    }

    // ───────────────────────── Partition tables ─────────────────────────

    /// <summary>An MBR disk: 1 MB of room for the table, then each volume at a 1 MB boundary.</summary>
    public static byte[] Mbr(params (byte Type, byte[] Volume)[] partitions) => Mbr(partitions, 2048);

    /// <summary>An MBR disk whose volumes start at multiples of <paramref name="align"/> sectors (1: packed, for fuzzing).</summary>
    public static byte[] Mbr((byte Type, byte[] Volume)[] partitions, int align)
    {
        var (disk, starts) = Layout([.. partitions.Select(p => p.Volume)], align, align: align);
        for (var i = 0; i < partitions.Length; i++)
        {
            var entry = disk.AsSpan(446 + i * 16, 16);
            entry[4] = partitions[i].Type;
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)starts[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)((partitions[i].Volume.Length + 511) / 512));
        }
        disk[510] = 0x55;
        disk[511] = 0xAA;
        return disk;
    }

    /// <summary>
    /// An MBR disk whose first entry is an extended partition holding the volumes as a chain of EBRs;
    /// <paramref name="loop"/> points the last EBR back at the first.
    /// </summary>
    public static byte[] Extended(byte[][] volumes, bool loop = false)
    {
        // Each logical partition: its EBR one MB before it.
        var (disk, starts) = Layout([.. volumes.SelectMany(v => new[] { new byte[512], v })], 2048);
        var extended = starts[0];
        var entry = disk.AsSpan(446, 16);
        entry[4] = 0x0F;
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)extended);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)(disk.Length / 512 - extended));
        disk[510] = 0x55;
        disk[511] = 0xAA;
        for (var i = 0; i < volumes.Length; i++)
        {
            var ebr = starts[2 * i];
            var at = (int)ebr * 512;
            var own = disk.AsSpan(at + 446, 16);
            own[4] = 0x0C;
            BinaryPrimitives.WriteUInt32LittleEndian(own[8..], (uint)(starts[2 * i + 1] - ebr));
            BinaryPrimitives.WriteUInt32LittleEndian(own[12..], (uint)((volumes[i].Length + 511) / 512));
            var nextEbr = i + 1 < volumes.Length ? starts[2 * i + 2] : loop ? extended : -1;
            if (nextEbr >= 0)
            {
                var link = disk.AsSpan(at + 462, 16);
                link[4] = 0x05;
                BinaryPrimitives.WriteUInt32LittleEndian(link[8..], (uint)(nextEbr - extended));
                BinaryPrimitives.WriteUInt32LittleEndian(link[12..], 1);
            }
            disk[at + 510] = 0x55;
            disk[at + 511] = 0xAA;
        }
        return disk;
    }

    /// <summary>A GPT disk (protective MBR, primary header and 128 entries; no backup copy) with named volumes.</summary>
    public static byte[] Gpt(params (string Name, byte[] Volume)[] partitions) => Gpt(partitions, 2048);

    /// <summary>A GPT disk whose volumes start at multiples of <paramref name="align"/> sectors (after the entries).</summary>
    public static byte[] Gpt((string Name, byte[] Volume)[] partitions, int align)
    {
        var (disk, starts) = Layout([.. partitions.Select(p => p.Volume)], Math.Max(34, align), tail: 34, align: align);
        var sectors = disk.Length / 512;
        var mbr = disk.AsSpan(446, 16);
        mbr[4] = 0xEE;
        BinaryPrimitives.WriteUInt32LittleEndian(mbr[8..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(mbr[12..], (uint)Math.Min(sectors - 1, uint.MaxValue));
        disk[510] = 0x55;
        disk[511] = 0xAA;
        var basicData = Guid.Parse("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7").ToByteArray();
        for (var i = 0; i < partitions.Length; i++)
        {
            var e = disk.AsSpan(1024 + i * 128, 128);
            basicData.CopyTo(e);
            Guid.NewGuid().ToByteArray().CopyTo(e[16..]);
            BinaryPrimitives.WriteUInt64LittleEndian(e[32..], (ulong)starts[i]);
            BinaryPrimitives.WriteUInt64LittleEndian(e[40..], (ulong)(starts[i] + (partitions[i].Volume.Length + 511) / 512 - 1));
            Encoding.Unicode.GetBytes(partitions[i].Name).CopyTo(e[56..]);
        }
        var header = disk.AsSpan(512, 92);
        "EFI PART"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], 0x00010000);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 92);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(header[32..], (ulong)(sectors - 1));
        BinaryPrimitives.WriteUInt64LittleEndian(header[40..], 34);
        BinaryPrimitives.WriteUInt64LittleEndian(header[48..], (ulong)(sectors - 34));
        Guid.NewGuid().ToByteArray().CopyTo(header[56..]);
        BinaryPrimitives.WriteUInt64LittleEndian(header[72..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header[80..], 128);
        BinaryPrimitives.WriteUInt32LittleEndian(header[84..], 128);
        BinaryPrimitives.WriteUInt32LittleEndian(header[88..], Crc(disk.AsSpan(1024, 128 * 128)));
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], Crc(header));
        return disk;
    }

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        var crc = new Filee.Engines.Archives.Crc32();
        crc.Append(data);
        return crc.Value;
    }

    /// <summary>An Apple partition map disk (512-byte blocks): the map itself, then the volumes as Apple_HFS.</summary>
    public static byte[] Apm(params (string Name, byte[] Volume)[] partitions)
    {
        var (disk, starts) = Layout([.. partitions.Select(p => p.Volume)], 64);
        disk[0] = (byte)'E';
        disk[1] = (byte)'R';
        BinaryPrimitives.WriteUInt16BigEndian(disk.AsSpan(2), 512);
        BinaryPrimitives.WriteUInt32BigEndian(disk.AsSpan(4), (uint)(disk.Length / 512));
        var count = partitions.Length + 1;
        void Entry(int index, uint start, uint blocks, string name, string type)
        {
            var e = disk.AsSpan(index * 512, 512);
            e[0] = (byte)'P';
            e[1] = (byte)'M';
            BinaryPrimitives.WriteUInt32BigEndian(e[4..], (uint)count);
            BinaryPrimitives.WriteUInt32BigEndian(e[8..], start);
            BinaryPrimitives.WriteUInt32BigEndian(e[12..], blocks);
            Encoding.ASCII.GetBytes(name).CopyTo(e[16..]);
            Encoding.ASCII.GetBytes(type).CopyTo(e[48..]);
        }
        Entry(1, 1, 63, "Apple", "Apple_partition_map");
        for (var i = 0; i < partitions.Length; i++)
            Entry(i + 2, (uint)starts[i], (uint)((partitions[i].Volume.Length + 511) / 512), partitions[i].Name, "Apple_HFS");
        return disk;
    }

    /// <summary>
    /// The volumes one after the other from sector <paramref name="first"/>, each at a multiple of
    /// <paramref name="align"/> sectors (1 MB by default).
    /// </summary>
    private static (byte[] Disk, long[] Starts) Layout(byte[][] volumes, long first, long tail = 0, int align = 2048)
    {
        var starts = new long[volumes.Length];
        var sector = first;
        for (var i = 0; i < volumes.Length; i++)
        {
            starts[i] = sector;
            sector += (volumes[i].Length + 511) / 512;
            sector = (sector + align - 1) / align * align;
        }
        var disk = new byte[(sector + tail) * 512];
        for (var i = 0; i < volumes.Length; i++)
            volumes[i].CopyTo(disk, starts[i] * 512);
        return (disk, starts);
    }

    // ───────────────────────── UDF ─────────────────────────

    private const int Sector = 2048;

    /// <summary>
    /// A UDF disc of <paramref name="entries"/> (revision 0x0102 or 0x0201): the volume recognition sequence at sector
    /// 16, the anchor at 256 and at the last sector, the volume descriptors at 32, and one type-1 partition from 257
    /// with the file set, folders (file identifiers) and files (short allocation descriptors; files of up to 64 bytes
    /// embedded in their file entry). <paramref name="bridge"/> puts an ISO 9660 volume with a single README first, as
    /// on discs whose files only the UDF part holds. <paramref name="loop"/> lets a folder "loop" point at the root.
    /// </summary>
    public static byte[] Udf(IReadOnlyList<TestEntry> entries, int revision = 0x0102, bool bridge = false, bool loop = false)
    {
        var version = revision >= 0x0200 ? 3 : 2;
        var tree = Tree(entries);
        const int partitionStart = 257;
        // Partition blocks: 0 file set descriptor, 1 its terminator, then the file entries and data.
        var blocks = new List<byte[]>([new byte[Sector], new byte[Sector]]);
        var unique = 16L;
        int Add(byte[] data)
        {
            var at = blocks.Count;
            for (var i = 0; i < Math.Max(1, (data.Length + Sector - 1) / Sector); i++)
                blocks.Add(data.AsSpan(i * Sector, Math.Min(Sector, data.Length - i * Sector)).ToArray().Concat(new byte[Sector]).Take(Sector).ToArray());
            return at;
        }
        // File entries are written once their blocks are known: reserve one block per folder and file first.
        var entryBlock = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var path in tree.Keys)
            entryBlock[path] = Add(new byte[Sector]);
        foreach (var (path, children) in tree)
            foreach (var (name, file) in children)
                if (file is not null)
                    entryBlock[(path.Length == 0 ? "" : path + "/") + name] = Add(new byte[Sector]);

        var time = KnownTime;
        byte[] FileEntry(int block, bool folder, long length, int? dataBlock, byte[]? embedded, int links, DateTime modified)
        {
            var e = new byte[Sector];
            // ICB tag: strategy 4, one entry, file type 4 (folder) or 5 (file), allocation in the flags.
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(16 + 4), 4);
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(16 + 8), 1);
            e[16 + 11] = (byte)(folder ? 4 : 5);
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(16 + 18), (ushort)(embedded is not null ? 3 : 0));
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(36), 0xFFFFFFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(40), 0xFFFFFFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(44), folder ? 0x14A5u : 0x0421u);
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(48), (ushort)links);
            BinaryPrimitives.WriteUInt64LittleEndian(e.AsSpan(56), (ulong)length);
            BinaryPrimitives.WriteUInt64LittleEndian(e.AsSpan(64), (ulong)(embedded is not null ? 0 : (length + Sector - 1) / Sector));
            Timestamp(modified, e.AsSpan(72));
            Timestamp(modified, e.AsSpan(84));
            Timestamp(modified, e.AsSpan(96));
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(108), 1);
            RegId("*Filee", e.AsSpan(128));
            BinaryPrimitives.WriteUInt64LittleEndian(e.AsSpan(160), (ulong)(folder && block == entryBlock[""] ? 0 : unique++));
            int ad;
            if (embedded is not null)
            {
                embedded.CopyTo(e, 176);
                ad = embedded.Length;
            }
            else if (length > 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(176), (uint)length);
                BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(180), (uint)dataBlock!.Value);
                ad = 8;
            }
            else
                ad = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(172), (uint)ad);
            Tag(e, 261, version, block, 176 + ad - 16);
            return e;
        }

        foreach (var (path, children) in tree.Reverse())
        {
            var self = entryBlock[path];
            var parent = path.Length == 0 ? self : entryBlock[path.Contains('/') ? path[..path.LastIndexOf('/')] : ""];
            var fids = new List<(byte Characteristics, string Name, int Icb)> { (0x0A, "", parent) };
            foreach (var (name, file) in children)
                fids.Add(((byte)(file is null ? 0x02 : 0x00), name, entryBlock[(path.Length == 0 ? "" : path + "/") + name]));
            if (loop && path.Length == 0)
                fids.Add((0x02, "loop", self));
            // The folder's data comes after all reserved entries; its file identifiers carry their own block numbers.
            var dataBlock = blocks.Count;
            var data = new MemoryStream();
            foreach (var (characteristics, name, icb) in fids)
            {
                var id = name.Length == 0 ? [] : DCharacters(name);
                var length = (38 + id.Length + 3) / 4 * 4;
                var fid = new byte[length];
                BinaryPrimitives.WriteUInt16LittleEndian(fid.AsSpan(16), 1);
                fid[18] = characteristics;
                fid[19] = (byte)id.Length;
                BinaryPrimitives.WriteUInt32LittleEndian(fid.AsSpan(20), Sector);
                BinaryPrimitives.WriteUInt32LittleEndian(fid.AsSpan(24), (uint)icb);
                id.CopyTo(fid, 38);
                Tag(fid, 257, version, dataBlock + (int)(data.Length / Sector), length - 16);
                data.Write(fid);
            }
            var bytes = data.ToArray();
            Add(bytes.Length == 0 ? new byte[Sector] : bytes);
            blocks[self] = FileEntry(self, true, bytes.Length, dataBlock, null, 1 + children.Count(c => c.File is null) + (path.Length == 0 && loop ? 1 : 0), time);
            foreach (var (name, file) in children)
            {
                if (file is null)
                    continue;
                var block = entryBlock[(path.Length == 0 ? "" : path + "/") + name];
                var content = file.Data!;
                if (content.Length is > 0 and <= 64)
                    blocks[block] = FileEntry(block, false, content.Length, null, content, 1, file.Time);
                else
                    blocks[block] = FileEntry(block, false, content.Length, content.Length > 0 ? Add(content) : null, null, 1, file.Time);
            }
        }

        // File set descriptor and its terminator.
        var fsd = blocks[0];
        Timestamp(time, fsd.AsSpan(16));
        BinaryPrimitives.WriteUInt16LittleEndian(fsd.AsSpan(28), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(fsd.AsSpan(30), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(fsd.AsSpan(32), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(fsd.AsSpan(36), 1);
        CharSpec(fsd.AsSpan(48));
        DString("FILEE_UDF", fsd.AsSpan(112, 128));
        CharSpec(fsd.AsSpan(240));
        DString("FILEE_UDF", fsd.AsSpan(304, 32));
        BinaryPrimitives.WriteUInt32LittleEndian(fsd.AsSpan(400), Sector);
        BinaryPrimitives.WriteUInt32LittleEndian(fsd.AsSpan(404), (uint)entryBlock[""]);
        Domain(revision, fsd.AsSpan(416));
        Tag(fsd, 256, version, 0, 496);
        Tag(blocks[1], 8, version, 1, 496);

        var partitionLength = blocks.Count;
        var total = partitionStart + partitionLength + 1;
        var image = new byte[(long)total * Sector];
        for (var i = 0; i < blocks.Count; i++)
            blocks[i].CopyTo(image, (long)(partitionStart + i) * Sector);

        // Volume recognition sequence (after the ISO 9660 descriptors of a bridge disc).
        var vrs = 16;
        if (bridge)
            vrs = IsoBridge(image);
        foreach (var id in new[] { "BEA01", version == 3 ? "NSR03" : "NSR02", "TEA01" })
        {
            var d = image.AsSpan(vrs++ * Sector, Sector);
            Encoding.ASCII.GetBytes(id).CopyTo(d[1..]);
            d[6] = 1;
        }

        // Main volume descriptor sequence at 32, the logical volume integrity descriptor at 64.
        var sector = 32;
        byte[] Descriptor(int tag, Action<Span<byte>> fill)
        {
            var d = new byte[512];
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(16), (uint)(sector - 31));
            fill(d);
            Tag(d, tag, version, sector, 496);
            d.CopyTo(image, (long)sector++ * Sector);
            return d;
        }
        Descriptor(1, d =>
        {
            DString("FILEE_UDF", d.Slice(24, 32));
            BinaryPrimitives.WriteUInt16LittleEndian(d[56..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(d[58..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(d[60..], 2);
            BinaryPrimitives.WriteUInt16LittleEndian(d[62..], 2);
            BinaryPrimitives.WriteUInt32LittleEndian(d[64..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(d[68..], 1);
            DString("20261114FILEE", d.Slice(72, 128));
            CharSpec(d[200..]);
            CharSpec(d[264..]);
            Timestamp(time, d[376..]);
            RegId("*Filee", d[388..]);
        });
        Descriptor(4, d =>
        {
            RegId("*UDF LV Info", d[20..]);
            BinaryPrimitives.WriteUInt16LittleEndian(d[44..], (ushort)revision);
            CharSpec(d[52..]);
            DString("FILEE_UDF", d.Slice(116, 128));
        });
        Descriptor(5, d =>
        {
            BinaryPrimitives.WriteUInt16LittleEndian(d[20..], 1);
            RegId(version == 3 ? "+NSR03" : "+NSR02", d[24..]);
            BinaryPrimitives.WriteUInt32LittleEndian(d[184..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(d[188..], partitionStart);
            BinaryPrimitives.WriteUInt32LittleEndian(d[192..], (uint)partitionLength);
            RegId("*Filee", d[196..]);
        });
        Descriptor(6, d =>
        {
            CharSpec(d[20..]);
            DString("FILEE_UDF", d.Slice(84, 128));
            BinaryPrimitives.WriteUInt32LittleEndian(d[212..], Sector);
            Domain(revision, d[216..]);
            BinaryPrimitives.WriteUInt32LittleEndian(d[248..], Sector); // file set descriptor: block 0, partition 0
            BinaryPrimitives.WriteUInt32LittleEndian(d[264..], 6);
            BinaryPrimitives.WriteUInt32LittleEndian(d[268..], 1);
            RegId("*Filee", d[272..]);
            BinaryPrimitives.WriteUInt32LittleEndian(d[432..], 2 * Sector);
            BinaryPrimitives.WriteUInt32LittleEndian(d[436..], 64);
            d[440] = 1;
            d[441] = 6;
            BinaryPrimitives.WriteUInt16LittleEndian(d[442..], 1);
        });
        Descriptor(7, _ => { });
        var terminator = new byte[512];
        Tag(terminator, 8, version, sector, 496);
        terminator.CopyTo(image, (long)sector * Sector);

        var lvid = new byte[512];
        Timestamp(time, lvid.AsSpan(16));
        BinaryPrimitives.WriteUInt32LittleEndian(lvid.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(lvid.AsSpan(40), (ulong)unique);
        BinaryPrimitives.WriteUInt32LittleEndian(lvid.AsSpan(72), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(lvid.AsSpan(76), 46);
        BinaryPrimitives.WriteUInt32LittleEndian(lvid.AsSpan(84), (uint)partitionLength);
        RegId("*Filee", lvid.AsSpan(88));
        BinaryPrimitives.WriteUInt32LittleEndian(lvid.AsSpan(120), (uint)entries.Count(e => !e.IsFolder));
        BinaryPrimitives.WriteUInt32LittleEndian(lvid.AsSpan(124), (uint)tree.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(lvid.AsSpan(128), (ushort)revision);
        BinaryPrimitives.WriteUInt16LittleEndian(lvid.AsSpan(130), (ushort)revision);
        BinaryPrimitives.WriteUInt16LittleEndian(lvid.AsSpan(132), (ushort)revision);
        Tag(lvid, 9, version, 64, 134 - 16);
        lvid.CopyTo(image, 64L * Sector);
        var lvidEnd = new byte[512];
        Tag(lvidEnd, 8, version, 65, 496);
        lvidEnd.CopyTo(image, 65L * Sector);

        // Anchors at 256 and at the last sector.
        foreach (var at in new[] { 256, total - 1 })
        {
            var anchor = new byte[512];
            BinaryPrimitives.WriteUInt32LittleEndian(anchor.AsSpan(16), 16 * Sector);
            BinaryPrimitives.WriteUInt32LittleEndian(anchor.AsSpan(20), 32);
            BinaryPrimitives.WriteUInt32LittleEndian(anchor.AsSpan(24), 16 * Sector);
            BinaryPrimitives.WriteUInt32LittleEndian(anchor.AsSpan(28), 32);
            Tag(anchor, 2, version, at, 496);
            anchor.CopyTo(image, (long)at * Sector);
        }
        return image;
    }

    /// <summary>An ISO 9660 primary descriptor at 16 and its terminator at 17, whose root holds README.TXT; returns 18.</summary>
    private static int IsoBridge(byte[] image)
    {
        // The README and the root folder go into sectors 66 and 67 (free in the UDF layout above).
        var text = "This disc holds its files in UDF.\r\n"u8.ToArray();
        text.CopyTo(image, 67 * Sector);
        var root = image.AsSpan(66 * Sector, Sector);
        var at = 0;
        foreach (var (name, extent, size, flags) in new[] { ("\0", 66, Sector, 2), ("\u0001", 66, Sector, 2), ("README.TXT;1", 67, text.Length, 0) })
            at += IsoRecord(root[at..], name, extent, size, (byte)flags);
        var pvd = image.AsSpan(16 * Sector, Sector);
        pvd[0] = 1;
        "CD001"u8.CopyTo(pvd[1..]);
        pvd[6] = 1;
        Encoding.ASCII.GetBytes("FILEE_BRIDGE".PadRight(32)).CopyTo(pvd[40..]);
        BothEndian32(pvd[80..], image.Length / Sector);
        BothEndian16(pvd[120..], 1);
        BothEndian16(pvd[124..], 1);
        BothEndian16(pvd[128..], Sector);
        IsoRecord(pvd[156..], "\0", 66, Sector, 2);
        pvd[881] = 1;
        var end = image.AsSpan(17 * Sector, Sector);
        end[0] = 255;
        "CD001"u8.CopyTo(end[1..]);
        end[6] = 1;
        return 18;
    }

    private static int IsoRecord(Span<byte> at, string name, int extent, int size, byte flags)
    {
        var id = Encoding.ASCII.GetBytes(name);
        var length = 33 + id.Length + (id.Length % 2 == 0 ? 1 : 0);
        at[0] = (byte)length;
        BothEndian32(at[2..], extent);
        BothEndian32(at[10..], size);
        at[18] = 121;
        at[19] = 5;
        at[20] = 6;
        at[25] = flags;
        BothEndian16(at[28..], 1);
        at[32] = (byte)id.Length;
        id.CopyTo(at[33..]);
        return length;
    }

    private static void BothEndian32(Span<byte> at, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(at, value);
        BinaryPrimitives.WriteInt32BigEndian(at[4..], value);
    }

    private static void BothEndian16(Span<byte> at, int value)
    {
        BinaryPrimitives.WriteInt16LittleEndian(at, (short)value);
        BinaryPrimitives.WriteInt16BigEndian(at[2..], (short)value);
    }

    /// <summary>A descriptor tag: identifier, version, checksum, serial 1, CRC-ITU-T over the body, its location.</summary>
    private static void Tag(Span<byte> descriptor, int id, int version, int location, int crcLength)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor, (ushort)id);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[2..], (ushort)version);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[6..], 1);
        var crc = 0;
        crcLength = Math.Min(crcLength, descriptor.Length - 16);
        foreach (var b in descriptor.Slice(16, crcLength))
        {
            crc ^= b << 8;
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[8..], (ushort)crc);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[10..], (ushort)crcLength);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[12..], (uint)location);
        byte sum = 0;
        for (var i = 0; i < 16; i++)
            if (i != 4)
                sum += descriptor[i];
        descriptor[4] = sum;
    }

    /// <summary>OSTA CS0: compression 8 (Latin-1) when every character fits, else 16 (UTF-16 big endian).</summary>
    private static byte[] DCharacters(string text)
    {
        if (text.All(c => c < 256))
            return [8, .. Encoding.Latin1.GetBytes(text)];
        return [16, .. Encoding.BigEndianUnicode.GetBytes(text)];
    }

    private static void DString(string text, Span<byte> field)
    {
        var bytes = DCharacters(text);
        bytes.CopyTo(field);
        field[^1] = (byte)bytes.Length;
    }

    private static void CharSpec(Span<byte> field)
    {
        field[0] = 0;
        "OSTA Compressed Unicode"u8.CopyTo(field[1..]);
    }

    private static void RegId(string id, Span<byte> field) => Encoding.ASCII.GetBytes(id).CopyTo(field[1..]);

    private static void Domain(int revision, Span<byte> field)
    {
        RegId("*OSTA UDF Compliant", field);
        BinaryPrimitives.WriteUInt16LittleEndian(field[24..], (ushort)revision);
    }

    private static void Timestamp(DateTime time, Span<byte> field)
    {
        var offset = (int)TimeZoneInfo.Local.GetUtcOffset(time).TotalMinutes;
        BinaryPrimitives.WriteUInt16LittleEndian(field, (ushort)(0x1000 | (offset & 0x0FFF)));
        BinaryPrimitives.WriteInt16LittleEndian(field[2..], (short)time.Year);
        field[4] = (byte)time.Month;
        field[5] = (byte)time.Day;
        field[6] = (byte)time.Hour;
        field[7] = (byte)time.Minute;
        field[8] = (byte)time.Second;
    }

    // ───────────────────────── DMG ─────────────────────────

    /// <summary>
    /// A DMG (UDIF) of <paramref name="disk"/>: one block table, chunks of <paramref name="chunkSectors"/> sectors
    /// stored as <paramref name="runs"/> says (in turn), all-zero chunks as zero runs, then the XML property list and
    /// the "koly" trailer.
    /// </summary>
    public static byte[] Dmg(byte[] disk, DmgRun[] runs, int chunkSectors = 520)
    {
        if (disk.Length % 512 != 0)
            Array.Resize(ref disk, (disk.Length + 511) / 512 * 512);
        var sectors = disk.Length / 512;
        var data = new MemoryStream();
        var table = new MemoryStream();
        var runList = new List<(uint Type, long Start, long Count, long Offset, long Length)>();
        var n = 0;
        for (long s = 0; s < sectors; s += chunkSectors)
        {
            var count = Math.Min(chunkSectors, sectors - s);
            var chunk = disk.AsSpan((int)(s * 512), (int)(count * 512));
            var offset = data.Position;
            if (chunk.IndexOfAnyExcept((byte)0) < 0)
            {
                runList.Add((2, s, count, offset, 0));
                continue;
            }
            var kind = runs[n++ % runs.Length];
            switch (kind)
            {
                case DmgRun.Raw:
                case DmgRun.Lzfse:
                    data.Write(chunk);
                    break;
                case DmgRun.Zlib:
                    using (var zlib = new ZLibStream(data, CompressionLevel.Optimal, leaveOpen: true))
                        zlib.Write(chunk);
                    break;
                case DmgRun.Bzip2:
                    data.Write(Bzip2(chunk.ToArray()));
                    break;
                case DmgRun.Adc:
                    for (var i = 0; i < chunk.Length; i += 128)
                    {
                        var length = Math.Min(128, chunk.Length - i);
                        data.WriteByte((byte)(0x80 | (length - 1)));
                        data.Write(chunk.Slice(i, length));
                    }
                    break;
            }
            uint type = kind switch { DmgRun.Raw => 1, DmgRun.Zlib => 0x80000005, DmgRun.Bzip2 => 0x80000006, DmgRun.Adc => 0x80000004, _ => 0x80000007 };
            runList.Add((type, s, count, offset, data.Position - offset));
        }
        runList.Add((0xFFFFFFFF, sectors, 0, data.Position, 0));

        var mish = new byte[204 + runList.Count * 40];
        "mish"u8.CopyTo(mish);
        BinaryPrimitives.WriteUInt32BigEndian(mish.AsSpan(4), 1);
        BinaryPrimitives.WriteInt64BigEndian(mish.AsSpan(16), sectors);
        BinaryPrimitives.WriteUInt32BigEndian(mish.AsSpan(32), (uint)chunkSectors);
        BinaryPrimitives.WriteUInt32BigEndian(mish.AsSpan(36), 0);
        BinaryPrimitives.WriteInt32BigEndian(mish.AsSpan(200), runList.Count);
        for (var i = 0; i < runList.Count; i++)
        {
            var r = mish.AsSpan(204 + i * 40, 40);
            BinaryPrimitives.WriteUInt32BigEndian(r, runList[i].Type);
            BinaryPrimitives.WriteInt64BigEndian(r[8..], runList[i].Start);
            BinaryPrimitives.WriteInt64BigEndian(r[16..], runList[i].Count);
            BinaryPrimitives.WriteInt64BigEndian(r[24..], runList[i].Offset);
            BinaryPrimitives.WriteInt64BigEndian(r[32..], runList[i].Length);
        }
        var dataLength = data.Length;
        var xml = Encoding.UTF8.GetBytes($"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
            	<key>resource-fork</key>
            	<dict>
            		<key>blkx</key>
            		<array>
            			<dict>
            				<key>Attributes</key>
            				<string>0x0050</string>
            				<key>CFName</key>
            				<string>whole disk (Filee : 0)</string>
            				<key>Data</key>
            				<data>
            				{Convert.ToBase64String(mish)}
            				</data>
            				<key>ID</key>
            				<string>0</string>
            				<key>Name</key>
            				<string>whole disk (Filee : 0)</string>
            			</dict>
            		</array>
            	</dict>
            </dict>
            </plist>

            """);
        data.Write(xml);
        var koly = new byte[512];
        "koly"u8.CopyTo(koly);
        BinaryPrimitives.WriteUInt32BigEndian(koly.AsSpan(4), 4);
        BinaryPrimitives.WriteUInt32BigEndian(koly.AsSpan(8), 512);
        BinaryPrimitives.WriteUInt32BigEndian(koly.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt64BigEndian(koly.AsSpan(32), (ulong)dataLength);
        BinaryPrimitives.WriteUInt32BigEndian(koly.AsSpan(56), 1);
        BinaryPrimitives.WriteUInt32BigEndian(koly.AsSpan(60), 1);
        BinaryPrimitives.WriteUInt64BigEndian(koly.AsSpan(216), (ulong)dataLength);
        BinaryPrimitives.WriteUInt64BigEndian(koly.AsSpan(224), (ulong)xml.Length);
        BinaryPrimitives.WriteUInt32BigEndian(koly.AsSpan(488), 1);
        BinaryPrimitives.WriteInt64BigEndian(koly.AsSpan(492), sectors);
        data.Write(koly);
        return data.ToArray();
    }
}
