// Writes disk images in code for the disk image tests: FAT12/16/32 volumes with long names (Microsoft's FAT
// specification), exFAT volumes (Microsoft's exFAT specification), ext2/3/4 volumes (the kernel's ext4 layout:
// extent trees, block maps, inline data, hashed folders), UDF discs (ECMA-167 and OSTA UDF 1.02 / 2.01, optionally behind an ISO 9660 bridge), MBR, GPT and
// Apple partition maps around volumes, and DMG (UDIF) files around a disk with raw, zero, zlib, bzip2, ADC, LZFSE and
// LZMA (.xz) runs.
// Each follows the format's own description, independently of the readers in Filee.Engines/Archives; the bundled
// 7-Zip, where present, reads the same images (DiskImageTests).

using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using Filee.Core.Presets;
using Filee.Engines.Archives;
using static Filee.Engines.Tests.ArchiveBuilders;

namespace Filee.Engines.Tests;

/// <summary>How <see cref="DiskImageBuilders.Dmg"/> stores a run.</summary>
public enum DmgRun
{
    Raw,
    Zlib,
    Bzip2,
    Adc,

    /// <summary>
    /// LZFSE, its chunks in FSE (bvx2) and LZVN blocks in turn, as Apple's encoder writes them (LzfseBuilders; the
    /// other block kinds are LzfseTests', 7-Zip doesn't read them).
    /// </summary>
    Lzfse,

    /// <summary>LZMA as Apple's tools write it: an .xz stream (Filee's XzWriter).</summary>
    Lzma,
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
                // DiscUtils paths use "\" on every system; Path.GetDirectoryName splits them on Windows only.
                var name = entry.Name.TrimEnd('/');
                var path = name.Replace('/', '\\');
                if (entry.IsFolder)
                {
                    fs.CreateDirectory(path);
                    continue;
                }
                if (name.LastIndexOf('/') is var slash and > 0)
                    fs.CreateDirectory(name[..slash].Replace('/', '\\'));
                using (var file = fs.OpenFile(path, FileMode.Create, FileAccess.Write))
                    file.Write(entry.Data);
                fs.SetLastWriteTime(path, entry.Time);
            }
        }
        return stream.ToArray();
    }

    // ───────────────────────── exFAT ─────────────────────────

    /// <summary>How <see cref="ExFat"/> writes a volume.</summary>
    /// <param name="ClusterSize">Bytes per cluster (512-byte sectors).</param>
    /// <param name="Contiguous">Files and folders marked "no FAT chain" (their clusters in a row), as Windows writes them; false: FAT chains.</param>
    /// <param name="Fragment">With FAT chains, every other cluster left free, so the chains jump.</param>
    /// <param name="Unwritten">Bytes at the end of each file that are zeros lie past its valid data length (not written).</param>
    /// <param name="SecondFat">Two FATs, the second the active one (the first is left empty).</param>
    /// <param name="Small">No smaller than the data needs (the specification asks for 1 MB at least): for fuzzing.</param>
    public sealed record ExFatOptions(int ClusterSize = 4096, bool Contiguous = true, bool Fragment = false, bool Unwritten = false,
        bool SecondFat = false, string Label = "FILEE EXFAT", bool Small = false);

    /// <summary>
    /// An exFAT volume of <paramref name="entries"/> written from Microsoft's exFAT specification: the boot region
    /// and its backup (with the boot checksum), the FAT, the allocation bitmap, an up-case table for ASCII, the root
    /// folder with the label, then the folders' and files' clusters. Times carry their UTC offset.
    /// <paramref name="patch"/> may change the image afterwards, given where each path's entry set lies.
    /// </summary>
    public static byte[] ExFat(IReadOnlyList<TestEntry> entries, ExFatOptions? options = null, Action<byte[], Dictionary<string, int>>? patch = null)
    {
        var o = options ?? new ExFatOptions();
        const int sector = 512;
        var cs = o.ClusterSize;
        var spc = cs / sector;
        var tree = Tree(entries);
        var files = entries.Where(e => !e.IsFolder).ToList();
        long SetBytes(string name) => 32 * (2 + (name.Length + 14) / 15);
        var needed = 2 + 8 + files.Sum(f => (f.Data!.Length + cs - 1) / cs * (o.Fragment ? 2 : 1))
                     + tree.Sum(t => (t.Value.Sum(c => SetBytes(c.Name)) + 3 * 32 + cs - 1) / cs * (o.Fragment ? 2 : 1) + 1);
        const long fatOffset = 24;
        var fats = o.SecondFat ? 2 : 1;
        long clusterCount = Math.Max(needed, o.Small ? 16 : 64), fatLength, heap;
        while (true)
        {
            fatLength = ((clusterCount + 2) * 4 + sector - 1) / sector;
            heap = (fatOffset + fats * fatLength + spc - 1) / spc * spc;
            var volume = Math.Max(heap + clusterCount * spc, o.Small ? 0 : 2048);
            var fit = (volume - heap) / spc;
            if (fit == clusterCount)
                break;
            clusterCount = fit;
        }
        var totalSectors = heap + clusterCount * spc;
        var image = new byte[totalSectors * sector];
        var fat = new uint[clusterCount + 2];
        fat[0] = 0xFFFFFFF8;
        fat[1] = 0xFFFFFFFF;
        var used = new bool[clusterCount + 2];
        uint next = 2;
        Span<byte> ClusterSpan(uint cluster) => image.AsSpan((int)((heap + (cluster - 2L) * spc) * sector), cs);

        // Clusters for data of this length: in a row, or chained (with gaps when fragmenting); returns the first.
        uint Allocate(ReadOnlySpan<byte> data, bool chained, bool fragment = true)
        {
            if (data.Length == 0)
                return 0;
            var count = (data.Length + cs - 1) / cs;
            var first = next;
            uint previous = 0;
            for (var i = 0; i < count; i++)
            {
                var cluster = next++;
                if (chained && fragment && o.Fragment && i < count - 1)
                    next++;
                used[cluster] = true;
                data.Slice(i * cs, Math.Min(cs, data.Length - i * cs)).CopyTo(ClusterSpan(cluster));
                if (chained)
                {
                    if (previous != 0)
                        fat[previous] = cluster;
                    fat[cluster] = 0xFFFFFFFF;
                }
                previous = cluster;
            }
            return first;
        }

        // The allocation bitmap and the up-case table come first (written at the end, once all is allocated).
        var bitmapCluster = next++;
        used[bitmapCluster] = true;
        fat[bitmapCluster] = 0xFFFFFFFF;
        var upcase = new byte[128 * 2];
        for (var c = 0; c < 128; c++)
            BinaryPrimitives.WriteUInt16LittleEndian(upcase.AsSpan(c * 2), char.ToUpperInvariant((char)c));
        var upcaseCluster = Allocate(upcase, chained: true);

        var sets = new Dictionary<string, int>(StringComparer.Ordinal);
        var setOffsets = new List<(string Path, int Offset)>();
        // Folders deepest first, so a folder's set can name its children's clusters.
        var data = new Dictionary<string, (uint First, long Length)>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            var content = f.Data!;
            data[f.Name] = (Allocate(content, chained: !o.Contiguous), content.Length);
        }
        var rootCluster = 0u;
        // The image offset of byte <offset> of data from <first>, its clusters <step> apart.
        int Locate(uint first, int offset, int step) => (int)((heap + (first + (long)offset / cs * step - 2) * spc) * sector) + offset % cs;
        foreach (var path in tree.Keys.OrderByDescending(p => p.Length == 0 ? -1 : p.Count(c => c == '/')).ThenBy(p => p, StringComparer.Ordinal))
        {
            var folder = new MemoryStream();
            if (path.Length == 0)
            {
                var label = new byte[32];
                label[0] = 0x83;
                label[1] = (byte)o.Label.Length;
                Encoding.Unicode.GetBytes(o.Label).CopyTo(label, 2);
                folder.Write(label);
                var bitmap = new byte[32];
                bitmap[0] = 0x81;
                BinaryPrimitives.WriteUInt32LittleEndian(bitmap.AsSpan(20), bitmapCluster);
                BinaryPrimitives.WriteUInt64LittleEndian(bitmap.AsSpan(24), (ulong)((clusterCount + 7) / 8));
                folder.Write(bitmap);
                var table = new byte[32];
                table[0] = 0x82;
                BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(4), BootChecksum(upcase, skip: false));
                BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(20), upcaseCluster);
                BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(24), (ulong)upcase.Length);
                folder.Write(table);
            }
            foreach (var (name, file) in tree[path])
            {
                var childPath = path.Length == 0 ? name : path + "/" + name;
                var (first, length) = data[file is null ? childPath + "/" : childPath];
                var valid = length;
                if (file is not null && o.Unwritten)
                {
                    while (valid > 0 && file.Data![valid - 1] == 0)
                        valid--;
                }
                setOffsets.Add((file is null ? childPath + "/" : childPath, (int)folder.Length));
                folder.Write(EntrySet(name, file is null, file?.Time ?? KnownTime, first, length, valid,
                    contiguous: file is null ? !o.Fragment && o.Contiguous : o.Contiguous));
            }
            var bytes = folder.ToArray();
            if (path.Length == 0)
            {
                bytes = [.. bytes, .. new byte[(cs - bytes.Length % cs) % cs]];
                rootCluster = Allocate(bytes, chained: true, fragment: false);
                foreach (var (p, offset) in setOffsets.Where(s => !s.Path.TrimEnd('/').Contains('/')))
                    sets[p] = Locate(rootCluster, offset, 1);
            }
            else
            {
                // A folder always has a cluster; its size is whole clusters.
                bytes = [.. bytes, .. new byte[bytes.Length == 0 ? cs : (cs - bytes.Length % cs) % cs]];
                var first = Allocate(bytes, chained: o.Fragment || !o.Contiguous);
                data[path + "/"] = (first, bytes.Length);
                foreach (var (p, offset) in setOffsets.Where(s => s.Path.TrimEnd('/').Contains('/') && s.Path[..s.Path.TrimEnd('/').LastIndexOf('/')] == path))
                    sets[p] = Locate(first, offset, o.Fragment ? 2 : 1);
            }
        }

        // The bitmap, the FAT, the boot region and its backup.
        var bitmapBytes = ClusterSpan(bitmapCluster);
        for (var c = 2; c < used.Length; c++)
        {
            if (used[c])
                bitmapBytes[(c - 2) >> 3] |= (byte)(1 << ((c - 2) & 7));
        }
        var fatStart = (int)((fatOffset + (o.SecondFat ? fatLength : 0)) * sector);
        for (var c = 0; c < fat.Length; c++)
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(fatStart + c * 4), fat[c]);
        var boot = image.AsSpan(0, sector);
        ((ReadOnlySpan<byte>)[0xEB, 0x76, 0x90]).CopyTo(boot);
        "EXFAT   "u8.CopyTo(boot[3..]);
        BinaryPrimitives.WriteUInt64LittleEndian(boot[72..], (ulong)totalSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[80..], (uint)fatOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[84..], (uint)fatLength);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[88..], (uint)heap);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[92..], (uint)clusterCount);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[96..], rootCluster);
        BinaryPrimitives.WriteUInt32LittleEndian(boot[100..], 0x1234ABCD);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[104..], 0x0100);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[106..], (ushort)(o.SecondFat ? 1 : 0));
        boot[108] = 9;
        boot[109] = (byte)BitOperations.Log2((uint)spc);
        boot[110] = (byte)fats;
        boot[111] = 0x80;
        boot[112] = 0xFF;
        boot[120..510].Fill(0xF4);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[510..], 0xAA55);
        for (var s = 1; s <= 8; s++)
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(s * sector + sector - 4), 0xAA550000);
        var checksum = BootChecksum(image.AsSpan(0, 11 * sector), skip: true);
        for (var i = 0; i < sector; i += 4)
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(11 * sector + i), checksum);
        image.AsSpan(0, 12 * sector).CopyTo(image.AsSpan(12 * sector));
        patch?.Invoke(image, sets);
        return image;

        static byte[] EntrySet(string name, bool folder, DateTime time, uint first, long length, long valid, bool contiguous)
        {
            var nameEntries = (name.Length + 14) / 15;
            var set = new byte[32 * (2 + nameEntries)];
            set[0] = 0x85;
            set[1] = (byte)(1 + nameEntries);
            BinaryPrimitives.WriteUInt16LittleEndian(set.AsSpan(4), (ushort)(folder ? 0x10 : 0x20));
            var stamp = (uint)((time.Year - 1980) << 25 | time.Month << 21 | time.Day << 16 | time.Hour << 11 | time.Minute << 5 | time.Second / 2);
            var offset = TimeZoneInfo.Local.GetUtcOffset(time);
            var quarters = (int)(offset.TotalMinutes / 15);
            foreach (var at in (ReadOnlySpan<int>)[8, 12, 16])
                BinaryPrimitives.WriteUInt32LittleEndian(set.AsSpan(at), stamp);
            set[20] = set[21] = (byte)(time.Second % 2 * 100);
            set[22] = set[23] = set[24] = (byte)(0x80 | (quarters & 0x7F));
            var stream = set.AsSpan(32, 32);
            stream[0] = 0xC0;
            stream[1] = (byte)(1 | (contiguous ? 2 : 0));
            stream[3] = (byte)name.Length;
            ushort hash = 0;
            foreach (var c in name.ToUpperInvariant())
            {
                hash = (ushort)(((hash & 1) != 0 ? 0x8000 : 0) + (hash >> 1) + (c & 0xFF));
                hash = (ushort)(((hash & 1) != 0 ? 0x8000 : 0) + (hash >> 1) + (c >> 8));
            }
            BinaryPrimitives.WriteUInt16LittleEndian(stream[4..], hash);
            BinaryPrimitives.WriteUInt64LittleEndian(stream[8..], (ulong)valid);
            BinaryPrimitives.WriteUInt32LittleEndian(stream[20..], first);
            BinaryPrimitives.WriteUInt64LittleEndian(stream[24..], (ulong)length);
            for (var n = 0; n < nameEntries; n++)
            {
                var e = set.AsSpan(64 + n * 32, 32);
                e[0] = 0xC1;
                var part = name.Substring(n * 15, Math.Min(15, name.Length - n * 15));
                Encoding.Unicode.GetBytes(part).CopyTo(e[2..]);
            }
            ushort sum = 0;
            for (var i = 0; i < set.Length; i++)
            {
                if (i is not (2 or 3))
                    sum = (ushort)(((sum & 1) != 0 ? 0x8000 : 0) + (sum >> 1) + set[i]);
            }
            BinaryPrimitives.WriteUInt16LittleEndian(set.AsSpan(2), sum);
            return set;
        }

        static uint BootChecksum(ReadOnlySpan<byte> bytes, bool skip)
        {
            uint sum = 0;
            for (var i = 0; i < bytes.Length; i++)
            {
                if (skip && i is 106 or 107 or 112)
                    continue;
                sum = ((sum & 1) != 0 ? 0x80000000u : 0) + (sum >> 1) + bytes[i];
            }
            return sum;
        }
    }

    // ───────────────────────── ext2/3/4 ─────────────────────────

    /// <summary>How <see cref="Ext"/> writes a volume.</summary>
    /// <param name="BlockSize">1024 (the first data block is then 1), 2048 or 4096.</param>
    /// <param name="Extents">ext4 extent trees; false: ext2 block maps (direct, one and two levels of indirect blocks).</param>
    /// <param name="Fragment">Every other block of a file left free, so its extents are one block each (trees of depth 1 or 2).</param>
    /// <param name="Sparse">All-zero blocks of a file are not stored: holes in its map.</param>
    /// <param name="Unwritten">All-zero blocks are stored but marked unwritten in their extent (ext4 preallocation).</param>
    /// <param name="Inline">Files of up to 128 bytes and small folders kept in their inodes (inline data).</param>
    /// <param name="Htree">Folders as hashed folders: the first block an index root ("." and a ".." over the rest of it).</param>
    /// <param name="BlocksPerGroup">Blocks per group (0: 8 × block size as mke2fs, at most 8 MB, so test images stay small).</param>
    /// <param name="SymbolicLink">A symbolic link "link" at the top (its target kept in the inode), which readers leave out.</param>
    public sealed record ExtOptions(
        int BlockSize = 1024,
        bool Extents = true,
        bool SixtyFourBit = false,
        bool Fragment = false,
        bool Sparse = false,
        bool Unwritten = false,
        bool Inline = false,
        bool Htree = false,
        int BlocksPerGroup = 0,
        int InodesPerGroup = 32,
        bool SymbolicLink = false,
        string Label = "FILEE EXT");

    /// <summary>
    /// An ext2/3/4 volume of <paramref name="entries"/> written from the kernel's description of the layout: a
    /// superblock, group descriptors, inode tables of every group one after the other (as flex_bg places them), then
    /// the folders' and files' blocks. No journal and no bitmaps worth the name (readers of files don't need them).
    /// <paramref name="patch"/> may change the image afterwards, given the block size and each path's inode offset.
    /// </summary>
    public static byte[] Ext(IReadOnlyList<TestEntry> entries, ExtOptions? options = null, Action<byte[], ExtLayout>? patch = null)
    {
        var o = options ?? new ExtOptions();
        var bs = o.BlockSize;
        var firstData = bs == 1024 ? 1 : 0;
        var perGroup = o.BlocksPerGroup > 0 ? o.BlocksPerGroup : Math.Min(8 * bs, (8 << 20) / bs); // groups of 8 MB at most
        const int inodeSize = 256;
        var tree = Tree(entries);
        var paths = tree.Keys.ToList(); // folders, "" first
        var files = entries.Where(e => !e.IsFolder).ToList();
        var inodeCount = 11 + paths.Count + files.Count + 1;
        // Room: data and folder blocks, map blocks, twice that when fragmented; then the groups that hold it all.
        long dataBlocks = files.Sum(f => (f.Data!.Length + bs - 1) / bs * (o.Fragment ? 2 : 1) + 4);
        dataBlocks += paths.Sum(p => (tree[p].Sum(c => 8 + Encoding.UTF8.GetByteCount(c.Name) + 4) + 64) / bs + 4) * (o.Htree ? 2 : 1);
        long groups = 1;
        long inodeBlocks;
        while (true)
        {
            var ipg = Math.Max(o.InodesPerGroup, (int)((inodeCount + groups - 1) / groups + 7) / 8 * 8);
            inodeBlocks = groups * ((long)ipg * inodeSize + bs - 1) / bs;
            var descriptorBlocks = (groups * (o.SixtyFourBit ? 64 : 32) + bs - 1) / bs;
            var needed = firstData + 1 + descriptorBlocks + 2 * groups + inodeBlocks + dataBlocks + 16;
            if (needed <= groups * perGroup)
                break;
            groups++;
        }
        var inodesPerGroup = Math.Max(o.InodesPerGroup, (int)((inodeCount + groups - 1) / groups + 7) / 8 * 8);
        // As mke2fs counts them: with 1 KB blocks the first group starts at block 1 and the last is a block short.
        var totalBlocks = groups * perGroup;
        var image = new byte[totalBlocks * bs];
        var descSize = o.SixtyFourBit ? 64 : 32;
        var gdtBlocks = (groups * descSize + bs - 1) / bs;
        long next = firstData + 1 + gdtBlocks;
        var bitmapStart = next; // a block bitmap and an inode bitmap per group
        next += 2 * groups;
        var tableStart = next;
        var tableBlocksPerGroup = ((long)inodesPerGroup * inodeSize + bs - 1) / bs;
        next += groups * tableBlocksPerGroup;

        var layout = new ExtLayout(bs, new Dictionary<string, long>(StringComparer.Ordinal));
        long InodeOffset(uint number)
        {
            var group = (number - 1) / inodesPerGroup;
            var index = (number - 1) % inodesPerGroup;
            return (tableStart + group * tableBlocksPerGroup) * bs + index * (long)inodeSize;
        }
        var usedBlocks = new bool[totalBlocks];
        long Allocate()
        {
            var block = next++;
            usedBlocks[block] = true;
            if (o.Fragment)
                next++;
            if (next > totalBlocks)
                throw new InvalidOperationException("the ext builder ran out of blocks");
            return block;
        }

        // Inode numbers: the root is 2, the first free one 11.
        var numbers = new Dictionary<string, uint>(StringComparer.Ordinal) { [""] = 2 };
        uint nextInode = 11;
        foreach (var p in paths.Where(p => p.Length > 0))
            numbers[p] = nextInode++;
        foreach (var f in files)
            numbers[f.Name] = nextInode++;
        var linkInode = o.SymbolicLink ? nextInode++ : 0;

        var hasInline = false;
        long sectors = 0;
        // Writes the data map of an inode for its content (extents, block map or inline) and returns the 60 bytes.
        byte[] MapFor(byte[] content, long inodeAt, bool folder, ref uint flags)
        {
            var map = new byte[60];
            if (o.Inline && (folder ? content.Length <= 56 + 4 : content.Length <= 128))
            {
                hasInline = true;
                flags |= 0x10000000;
                content.AsSpan(0, Math.Min(60, content.Length)).CopyTo(map);
                if (content.Length > 60)
                {
                    // The rest in the "system.data" attribute after the 32-byte extra part.
                    var at = (int)inodeAt + 128 + 32;
                    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at), 0xEA020000);
                    var entry = at + 4;
                    image[entry] = 4; // name length
                    image[entry + 1] = 7; // "system."
                    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entry + 2), 24); // value offset from the first entry
                    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(entry + 8), (uint)(content.Length - 60));
                    "data"u8.CopyTo(image.AsSpan(entry + 16));
                    content.AsSpan(60).CopyTo(image.AsSpan(entry + 24));
                }
                return map;
            }
            var count = (content.Length + bs - 1) / bs;
            var blocks = new long[count];
            var unwritten = new bool[count];
            for (var i = 0; i < count; i++)
            {
                var chunk = content.AsSpan(i * bs, Math.Min(bs, content.Length - i * bs));
                var zero = !folder && !chunk.ContainsAnyExcept((byte)0);
                if (zero && o.Sparse)
                {
                    blocks[i] = 0;
                    continue;
                }
                blocks[i] = Allocate();
                sectors += bs / 512;
                if (zero && o.Unwritten)
                    unwritten[i] = true;
                else
                    chunk.CopyTo(image.AsSpan((int)(blocks[i] * bs)));
            }
            if (o.Extents)
            {
                flags |= 0x80000;
                var extents = new List<(uint First, ushort Length, long Start)>();
                for (var i = 0; i < count; i++)
                {
                    if (blocks[i] == 0)
                        continue;
                    var length = (ushort)(unwritten[i] ? 32769 : 1);
                    if (extents.Count > 0 && extents[^1] is var last && last.First + (last.Length & 0x7FFF) == i
                        && last.Start + (last.Length & 0x7FFF) == blocks[i] && (last.Length > 32768) == unwritten[i] && (last.Length & 0x7FFF) < 32767)
                        extents[^1] = (last.First, (ushort)(last.Length + 1), last.Start);
                    else
                        extents.Add(((uint)i, length, blocks[i]));
                }
                // Leaves of up to (bs - 12) / 12 extents, then index levels until the root (4 entries) holds them.
                var level = extents.Select(e => (e.First, Bytes: Extent(e))).ToList();
                var depth = 0;
                while (level.Count > 4)
                {
                    var perNode = (bs - 12) / 12;
                    var upper = new List<(uint, byte[])>();
                    for (var i = 0; i < level.Count; i += perNode)
                    {
                        var part = level.Skip(i).Take(perNode).ToList();
                        var block = Allocate();
                        sectors += bs / 512;
                        Header(image.AsSpan((int)(block * bs)), part.Count, perNode, depth);
                        for (var k = 0; k < part.Count; k++)
                            part[k].Bytes.CopyTo(image.AsSpan((int)(block * bs) + 12 + k * 12));
                        var index = new byte[12];
                        BinaryPrimitives.WriteUInt32LittleEndian(index, part[0].First);
                        BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(4), (uint)block);
                        BinaryPrimitives.WriteUInt16LittleEndian(index.AsSpan(8), (ushort)(block >> 32));
                        upper.Add((part[0].First, index));
                    }
                    level = upper;
                    depth++;
                }
                Header(map, level.Count, 4, depth);
                for (var k = 0; k < level.Count; k++)
                    level[k].Bytes.CopyTo(map.AsSpan(12 + k * 12));
                return map;
            }
            // Block map: 12 direct, one indirect block, one double indirect block.
            var perBlock = bs / 4;
            for (var i = 0; i < Math.Min(12, count); i++)
                BinaryPrimitives.WriteUInt32LittleEndian(map.AsSpan(i * 4), (uint)blocks[i]);
            long Indirect(ReadOnlySpan<long> part)
            {
                if (!part.ContainsAnyExcept(0L))
                    return 0;
                var block = Allocate();
                sectors += bs / 512;
                for (var k = 0; k < part.Length; k++)
                    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan((int)(block * bs) + k * 4), (uint)part[k]);
                return block;
            }
            if (count > 12)
                BinaryPrimitives.WriteUInt32LittleEndian(map.AsSpan(48), (uint)Indirect(blocks.AsSpan(12, Math.Min(perBlock, count - 12))));
            if (count > 12 + perBlock)
            {
                var rest = blocks.AsSpan(12 + perBlock);
                if (rest.Length > perBlock * perBlock)
                    throw new InvalidOperationException("the ext builder writes no triple indirect blocks");
                var singles = new long[(rest.Length + perBlock - 1) / perBlock];
                for (var k = 0; k < singles.Length; k++)
                    singles[k] = Indirect(rest.Slice(k * perBlock, Math.Min(perBlock, rest.Length - k * perBlock)));
                BinaryPrimitives.WriteUInt32LittleEndian(map.AsSpan(52), (uint)Indirect(singles));
            }
            return map;
        }

        void WriteInode(uint number, ushort mode, long size, DateTime time, ushort links, byte[] content, bool folder, string path)
        {
            var at = InodeOffset(number);
            layout.Inodes[path] = at;
            uint flags = folder && o.Htree && content.Length > bs ? 0x1000u : 0;
            sectors = 0;
            var map = MapFor(content, at, folder, ref flags);
            var inode = image.AsSpan((int)at, inodeSize);
            BinaryPrimitives.WriteUInt16LittleEndian(inode, mode);
            BinaryPrimitives.WriteUInt32LittleEndian(inode[4..], (uint)size);
            BinaryPrimitives.WriteUInt32LittleEndian(inode[0x6C..], (uint)(size >> 32));
            var seconds = (uint)new DateTimeOffset(time).ToUnixTimeSeconds();
            foreach (var field in (ReadOnlySpan<int>)[0x08, 0x0C, 0x10])
                BinaryPrimitives.WriteUInt32LittleEndian(inode[field..], seconds);
            BinaryPrimitives.WriteUInt16LittleEndian(inode[0x1A..], links);
            BinaryPrimitives.WriteUInt32LittleEndian(inode[0x1C..], (uint)sectors);
            BinaryPrimitives.WriteUInt32LittleEndian(inode[0x20..], flags);
            map.CopyTo(inode[0x28..]);
            BinaryPrimitives.WriteUInt16LittleEndian(inode[0x80..], 32); // extra part: up to the creation time
        }

        // Folders: "." and "..", then the children; each block's last entry reaches its end.
        foreach (var path in paths)
        {
            var children = tree[path];
            var list = new List<(uint Number, byte Type, string Name)>
            {
                (numbers[path], 2, "."),
                (path.Length == 0 ? 2 : numbers[path.Contains('/') ? path[..path.LastIndexOf('/')] : ""], 2, ".."),
            };
            foreach (var (name, file) in children)
                list.Add((numbers[path.Length == 0 ? name : path + "/" + name], file is null ? (byte)2 : (byte)1, name));
            if (path.Length == 0 && o.SymbolicLink)
                list.Add((linkInode, 7, "link"));
            byte[] content;
            var inlineFolder = o.Inline && list.Skip(2).Sum(e => (8 + Encoding.UTF8.GetByteCount(e.Name) + 3) & ~3) <= 56;
            if (inlineFolder)
            {
                // The parent's inode number, then the entries (no "." or ".."), the last one over the rest of the 60 bytes.
                content = new byte[60];
                BinaryPrimitives.WriteUInt32LittleEndian(content, list[1].Number);
                var at = 4;
                var rest = list.Skip(2).ToList();
                if (rest.Count == 0)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(content.AsSpan(8), 56);
                }
                for (var i = 0; i < rest.Count; i++)
                {
                    var nameBytes = Encoding.UTF8.GetBytes(rest[i].Name);
                    var length = i == rest.Count - 1 ? 60 - at : (8 + nameBytes.Length + 3) & ~3;
                    DirEntry(content.AsSpan(at), rest[i].Number, length, rest[i].Type, nameBytes);
                    at += length;
                }
            }
            else
            {
                var blocks = new List<byte[]>();
                var block = new byte[bs];
                var used = 0;
                var lastAt = -1;
                var start = 0;
                if (o.Htree)
                {
                    // An index root: "." and a ".." whose record holds the index (count and limit, one entry).
                    DirEntry(block, list[0].Number, 12, 2, "."u8);
                    DirEntry(block.AsSpan(12), list[1].Number, bs - 12, 2, ".."u8);
                    block[24 + 5] = 8; // info length
                    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(32), (ushort)((bs - 32) / 8)); // limit
                    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(34), 1); // count
                    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(36), 1); // the leaf block
                    blocks.Add(block);
                    block = new byte[bs];
                    start = 2;
                }
                foreach (var e in list.Skip(start))
                {
                    var nameBytes = Encoding.UTF8.GetBytes(e.Name);
                    var length = (8 + nameBytes.Length + 3) & ~3;
                    if (used + length > bs)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(lastAt + 4), (ushort)(bs - lastAt));
                        blocks.Add(block);
                        block = new byte[bs];
                        used = 0;
                    }
                    DirEntry(block.AsSpan(used), e.Number, length, e.Type, nameBytes);
                    lastAt = used;
                    used += length;
                }
                if (lastAt >= 0)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(lastAt + 4), (ushort)(bs - lastAt));
                    blocks.Add(block);
                }
                else if (o.Htree)
                {
                    DirEntry(block, 0, bs, 0, []); // an empty leaf for the index's one entry
                    blocks.Add(block);
                }
                content = [.. blocks.SelectMany(b => b)];
            }
            var subfolders = children.Count(c => c.File is null);
            WriteInode(numbers[path], 0x41ED, content.Length, KnownTime, (ushort)(2 + subfolders), content, folder: true, path.Length == 0 ? "/" : path + "/");
        }
        foreach (var f in files)
            WriteInode(numbers[f.Name], 0x81A4, f.Data!.Length, f.Time, 1, f.Data, folder: false, f.Name);
        if (o.SymbolicLink)
        {
            // A fast symbolic link: the target in the map's 60 bytes, no extents flag.
            var at = InodeOffset(linkInode);
            var inode = image.AsSpan((int)at, inodeSize);
            BinaryPrimitives.WriteUInt16LittleEndian(inode, 0xA1FF);
            BinaryPrimitives.WriteUInt32LittleEndian(inode[4..], 10);
            BinaryPrimitives.WriteUInt16LittleEndian(inode[0x1A..], 1);
            "/etc/hosts"u8.CopyTo(inode[0x28..]);
            BinaryPrimitives.WriteUInt16LittleEndian(inode[0x80..], 32);
            layout.Inodes["link"] = at;
        }

        // Superblock (at byte 1024) and group descriptors.
        var sb = image.AsSpan(1024, 1024);
        BinaryPrimitives.WriteUInt32LittleEndian(sb, (uint)(groups * inodesPerGroup));
        BinaryPrimitives.WriteUInt32LittleEndian(sb[4..], (uint)totalBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x14..], (uint)firstData);
        var log = BitOperations.Log2((uint)bs) - 10;
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x18..], (uint)log);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x1C..], (uint)log);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x20..], (uint)perGroup);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x24..], (uint)perGroup);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x28..], (uint)inodesPerGroup);
        var now = (uint)new DateTimeOffset(KnownTime).ToUnixTimeSeconds();
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x2C..], now);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x30..], now);
        BinaryPrimitives.WriteUInt16LittleEndian(sb[0x36..], 0xFFFF); // max mount count
        BinaryPrimitives.WriteUInt16LittleEndian(sb[0x38..], 0xEF53);
        BinaryPrimitives.WriteUInt16LittleEndian(sb[0x3A..], 1); // clean
        BinaryPrimitives.WriteUInt16LittleEndian(sb[0x3C..], 1); // on errors: continue
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x4C..], 1); // dynamic revision
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x54..], 11); // first free inode
        BinaryPrimitives.WriteUInt16LittleEndian(sb[0x58..], inodeSize);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x5C..], o.Htree ? 0x20u : 0); // dir_index
        var incompat = 0x2u | (o.Extents ? 0x40u : 0) | (o.SixtyFourBit ? 0x80u : 0) | (hasInline ? 0x8000u : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x60..], incompat);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x64..], 0x1 | 0x2); // sparse_super, large_file
        for (var i = 0; i < 16; i++)
            sb[0x68 + i] = (byte)(0x31 + i); // UUID
        Encoding.UTF8.GetBytes(o.Label).CopyTo(sb[0x78..]);
        if (o.SixtyFourBit)
            BinaryPrimitives.WriteUInt16LittleEndian(sb[0xFE..], 64);
        BinaryPrimitives.WriteUInt16LittleEndian(sb[0x15C..], 32); // min / wanted extra inode size
        BinaryPrimitives.WriteUInt16LittleEndian(sb[0x15E..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x150..], (uint)(totalBlocks >> 32));
        var gdt = image.AsSpan((firstData + 1) * bs, (int)gdtBlocks * bs);
        // Bitmaps (the padding past the last block or inode set, as mke2fs does) and the free counts they add up to.
        for (var b = 0L; b < bitmapStart + 2 * groups + groups * tableBlocksPerGroup; b++)
            usedBlocks[b] = true;
        long freeBlocks = 0, freeInodes = 0;
        for (var g = 0; g < groups; g++)
        {
            var blockBitmap = image.AsSpan((int)((bitmapStart + 2 * g) * bs), bs);
            var inodeBitmap = image.AsSpan((int)((bitmapStart + 2 * g + 1) * bs), bs);
            int groupFreeBlocks = 0, groupFreeInodes = 0, groupFolders = 0;
            for (var i = 0; i < bs * 8; i++)
            {
                var block = firstData + (long)g * perGroup + i;
                if (i >= perGroup || block >= totalBlocks || usedBlocks[block])
                    blockBitmap[i >> 3] |= (byte)(1 << (i & 7));
                else
                    groupFreeBlocks++;
                var inode = (long)g * inodesPerGroup + i + 1;
                if (i >= inodesPerGroup || inode < nextInode)
                    inodeBitmap[i >> 3] |= (byte)(1 << (i & 7));
                else
                    groupFreeInodes++;
            }
            foreach (var (path, number) in numbers)
            {
                if ((number - 1) / inodesPerGroup == g && tree.ContainsKey(path))
                    groupFolders++;
            }
            freeBlocks += groupFreeBlocks;
            freeInodes += groupFreeInodes;
            var d = gdt.Slice(g * descSize, descSize);
            BinaryPrimitives.WriteUInt32LittleEndian(d, (uint)(bitmapStart + 2 * g));
            BinaryPrimitives.WriteUInt32LittleEndian(d[4..], (uint)(bitmapStart + 2 * g + 1));
            BinaryPrimitives.WriteUInt32LittleEndian(d[8..], (uint)(tableStart + g * tableBlocksPerGroup));
            BinaryPrimitives.WriteUInt16LittleEndian(d[0x0C..], (ushort)groupFreeBlocks);
            BinaryPrimitives.WriteUInt16LittleEndian(d[0x0E..], (ushort)groupFreeInodes);
            BinaryPrimitives.WriteUInt16LittleEndian(d[0x10..], (ushort)groupFolders);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x0C..], (uint)freeBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x10..], (uint)freeInodes);
        patch?.Invoke(image, layout);
        return image;

        static byte[] Extent((uint First, ushort Length, long Start) e)
        {
            var bytes = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, e.First);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), e.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)(e.Start >> 32));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)e.Start);
            return bytes;
        }

        static void Header(Span<byte> node, int count, int max, int depth)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(node, 0xF30A);
            BinaryPrimitives.WriteUInt16LittleEndian(node[2..], (ushort)count);
            BinaryPrimitives.WriteUInt16LittleEndian(node[4..], (ushort)max);
            BinaryPrimitives.WriteUInt16LittleEndian(node[6..], (ushort)depth);
        }

        static void DirEntry(Span<byte> at, uint number, int length, byte type, ReadOnlySpan<byte> name)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(at, number);
            BinaryPrimitives.WriteUInt16LittleEndian(at[4..], (ushort)length);
            at[6] = (byte)name.Length;
            at[7] = type;
            name.CopyTo(at[8..]);
        }
    }

    /// <summary>Where <see cref="Ext"/> put things: the block size and each path's inode ("/" the root, "dir/" a folder).</summary>
    public sealed record ExtLayout(int BlockSize, Dictionary<string, long> Inodes);

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
    /// Apple Data Compression, greedy: 1LLLLLLL + L + 1 literals; 0LLLLDDD dddddddd for L + 3 bytes (3–18) from D + 1
    /// back (up to 1,024); 01LLLLLL and D as 16 bits big endian for L + 4 bytes (4–67) from up to 65,536 back.
    /// </summary>
    public static byte[] Adc(ReadOnlySpan<byte> chunk)
    {
        var output = new MemoryStream();
        var last = new Dictionary<int, int>();
        var literals = new List<byte>();
        void Flush()
        {
            for (var i = 0; i < literals.Count; i += 128)
            {
                var n = Math.Min(128, literals.Count - i);
                output.WriteByte((byte)(0x80 | (n - 1)));
                output.Write(literals.GetRange(i, n).ToArray());
            }
            literals.Clear();
        }
        for (var at = 0; at < chunk.Length;)
        {
            var (length, distance) = (0, 0);
            if (chunk.Length - at >= 3)
            {
                var key = chunk[at] | chunk[at + 1] << 8 | chunk[at + 2] << 16;
                if (last.TryGetValue(key, out var earlier) && at - earlier <= 65536)
                {
                    distance = at - earlier;
                    while (at + length < chunk.Length && length < 67 && chunk[earlier + length] == chunk[at + length])
                        length++;
                }
                last[key] = at;
            }
            if (length >= 3 && distance <= 1024 && length <= 18)
            {
                Flush();
                output.WriteByte((byte)((length - 3) << 2 | (distance - 1) >> 8));
                output.WriteByte((byte)(distance - 1));
            }
            else if (length >= 4)
            {
                Flush();
                output.WriteByte((byte)(0x40 | (length - 4)));
                output.WriteByte((byte)((distance - 1) >> 8));
                output.WriteByte((byte)(distance - 1));
            }
            else
            {
                literals.Add(chunk[at]);
                length = 1;
            }
            at += length;
        }
        Flush();
        return output.ToArray();
    }

    /// <summary>
    /// A DMG (UDIF) of <paramref name="disk"/>: one block table, chunks of <paramref name="chunkSectors"/> sectors
    /// stored as <paramref name="runs"/> says (in turn), all-zero chunks as zero runs (zero fill, type 0, and
    /// ignore, type 2, in turn, as hdiutil writes both), then the XML property list (base64 in lines of 52 like
    /// hdiutil's) and the "koly" trailer.
    /// </summary>
    public static byte[] Dmg(byte[] disk, DmgRun[] runs, int chunkSectors = 520)
    {
        if (disk.Length % 512 != 0)
            Array.Resize(ref disk, (disk.Length + 511) / 512 * 512);
        var sectors = disk.Length / 512;
        var data = new MemoryStream();
        var table = new MemoryStream();
        var runList = new List<(uint Type, long Start, long Count, long Offset, long Length)>();
        var (n, zeros) = (0, 0);
        for (long s = 0; s < sectors; s += chunkSectors)
        {
            var count = Math.Min(chunkSectors, sectors - s);
            var chunk = disk.AsSpan((int)(s * 512), (int)(count * 512));
            var offset = data.Position;
            if (chunk.IndexOfAnyExcept((byte)0) < 0)
            {
                runList.Add((zeros++ % 2 == 0 ? 0u : 2u, s, count, offset, 0));
                continue;
            }
            var kind = runs[n++ % runs.Length];
            switch (kind)
            {
                case DmgRun.Raw:
                    data.Write(chunk);
                    break;
                case DmgRun.Lzfse:
                    data.Write(LzfseBuilders.Encode(chunk, [LzfseBlock.FseV2, LzfseBlock.Lzvn], 4096));
                    break;
                case DmgRun.Lzma:
                    using (var xz = new XzWriter(data, ArchiveLevel.Fast, chunk.Length, CancellationToken.None, leaveOpen: true))
                    {
                        xz.Write(chunk);
                        xz.Finish();
                    }
                    break;
                case DmgRun.Zlib:
                    using (var zlib = new ZLibStream(data, CompressionLevel.Optimal, leaveOpen: true))
                        zlib.Write(chunk);
                    break;
                case DmgRun.Bzip2:
                    data.Write(Bzip2(chunk.ToArray()));
                    break;
                case DmgRun.Adc:
                    data.Write(Adc(chunk));
                    break;
            }
            uint type = kind switch
            {
                DmgRun.Raw => 1,
                DmgRun.Zlib => 0x80000005,
                DmgRun.Bzip2 => 0x80000006,
                DmgRun.Adc => 0x80000004,
                DmgRun.Lzfse => 0x80000007,
                _ => 0x80000008,
            };
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
            				{string.Join("\n\t\t\t\t", Convert.ToBase64String(mish).Chunk(52).Select(c => new string(c)))}
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
