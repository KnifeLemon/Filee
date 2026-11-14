// A compound file (CFB, "structured storage") writer of Filee's own, the counterpart of CompoundFile: version 3 with
// 512-byte sectors, as 한글 saves HWP files. Streams below the 4096-byte cutoff go into 64-byte mini sectors inside the
// root entry's stream (the mini stream), larger ones into regular sectors. The allocation table (FAT) lists its own
// sectors in the header's 109 slots and, beyond them, in DIFAT sectors; each storage's members form a red-black tree
// ordered as CFB compares names (shorter first, then by upper-case UTF-16 code unit).

using System.Buffers.Binary;
using System.Text;

namespace Filee.Engines.Hwp.Binary;

/// <summary>Collects streams by path ("BodyText/Section0") and writes them as one compound file.</summary>
internal sealed class CompoundFileWriter
{
    internal const int SectorSize = 512;
    internal const int MiniSectorSize = 64;
    internal const int MiniStreamCutoff = 4096;
    private const int HeaderDifatEntries = 109;
    private const int DirectoryEntrySize = 128;
    private const int EntriesPerSector = SectorSize / 4;

    private const uint DifatSector = 0xFFFFFFFC;
    private const uint FatSector = 0xFFFFFFFD;
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint Free = 0xFFFFFFFF;

    /// <summary>A storage (folder) or stream of the directory.</summary>
    private sealed class Node(string name, bool storage)
    {
        public string Name { get; } = name;
        public bool Storage { get; } = storage;
        public byte[] Data { get; set; } = [];
        public List<Node> Members { get; } = [];
        public int Index { get; set; }
        public uint Start { get; set; } = EndOfChain;
        public uint Left { get; set; } = Free;
        public uint Right { get; set; } = Free;
        public uint Child { get; set; } = Free;
        public bool Red { get; set; }
    }

    private readonly Node _root = new("Root Entry", storage: true);

    /// <summary>Adds (or replaces) the stream at <paramref name="path"/>; storages on the way are made as needed.</summary>
    public void Add(string path, byte[] data)
    {
        var parts = path.Split('/');
        var folder = _root;
        for (var i = 0; i < parts.Length; i++)
        {
            var name = parts[i];
            if (name.Length is 0 or > 31)
                throw new ArgumentException($"A compound file name must have 1 to 31 characters: {path}", nameof(path));
            var last = i == parts.Length - 1;
            var node = folder.Members.FirstOrDefault(m => Compare(m.Name, name) == 0);
            if (node is null)
            {
                node = new Node(name, storage: !last);
                folder.Members.Add(node);
            }
            else if (node.Storage == last)
            {
                throw new ArgumentException($"{path} names a storage and a stream at once.", nameof(path));
            }
            if (last)
                node.Data = data;
            folder = node;
        }
    }

    /// <summary>Writes the compound file to <paramref name="output"/>.</summary>
    public void Write(Stream output)
    {
        // Directory order: the root first, then every storage's members, depth first.
        var nodes = new List<Node>();
        Number(_root, nodes);
        foreach (var node in nodes.Where(n => n.Storage))
            node.Child = Tree(node.Members.OrderBy(m => m.Name, Comparer<string>.Create(Compare)).ToList(), 0, node.Members.Count - 1, 0, Depth(node.Members.Count));

        // The mini stream: small streams packed in 64-byte sectors, chained in the mini FAT.
        var miniFat = new List<uint>();
        using var miniStream = new MemoryStream();
        foreach (var node in nodes.Where(n => !n.Storage && n.Data.Length > 0 && n.Data.Length < MiniStreamCutoff))
        {
            var count = (node.Data.Length + MiniSectorSize - 1) / MiniSectorSize;
            node.Start = (uint)miniFat.Count;
            for (var i = 0; i < count; i++)
                miniFat.Add(i + 1 < count ? (uint)(miniFat.Count + 1) : EndOfChain);
            miniStream.Write(node.Data);
            miniStream.Write(new byte[count * MiniSectorSize - node.Data.Length]);
        }
        _root.Data = miniStream.ToArray();

        // Regular sectors, in file order: directory, mini FAT, mini stream, the large streams; FAT and DIFAT at the end.
        var directorySectors = Sectors((long)nodes.Count * DirectoryEntrySize);
        var miniFatSectors = Sectors(miniFat.Count * 4L);
        var large = nodes.Where(n => !n.Storage && n.Data.Length >= MiniStreamCutoff).ToList();
        var dataSectors = directorySectors + miniFatSectors + Sectors(_root.Data.Length) + large.Sum(n => Sectors(n.Data.Length));
        var (fatSectors, difatSectors) = TableSizes(dataSectors);

        var fat = new uint[fatSectors * EntriesPerSector];
        Array.Fill(fat, Free);
        var next = 0;
        uint Allocate(long length)
        {
            var count = Sectors(length);
            if (count == 0)
                return EndOfChain;
            var start = next;
            for (var i = 0; i < count; i++)
                fat[start + i] = i + 1 < count ? (uint)(start + i + 1) : EndOfChain;
            next += count;
            return (uint)start;
        }
        var directoryStart = Allocate((long)nodes.Count * DirectoryEntrySize);
        var miniFatStart = Allocate(miniFat.Count * 4L);
        _root.Start = Allocate(_root.Data.Length);
        foreach (var node in large)
            node.Start = Allocate(node.Data.Length);
        var fatStart = next;
        for (var i = 0; i < fatSectors; i++)
            fat[next++] = FatSector;
        var difatStart = next;
        for (var i = 0; i < difatSectors; i++)
            fat[next++] = DifatSector;

        // Header.
        var header = new byte[SectorSize];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(header, 0);
        Put16(header, 24, 0x3E);
        Put16(header, 26, 3);
        Put16(header, 28, 0xFFFE);
        Put16(header, 30, 9);
        Put16(header, 32, 6);
        Put32(header, 44, (uint)fatSectors);
        Put32(header, 48, directoryStart);
        Put32(header, 56, MiniStreamCutoff);
        Put32(header, 60, miniFatSectors > 0 ? miniFatStart : EndOfChain);
        Put32(header, 64, (uint)miniFatSectors);
        Put32(header, 68, difatSectors > 0 ? (uint)difatStart : EndOfChain);
        Put32(header, 72, (uint)difatSectors);
        for (var i = 0; i < HeaderDifatEntries; i++)
            Put32(header, 76 + i * 4, i < fatSectors ? (uint)(fatStart + i) : Free);
        output.Write(header);

        // Sectors in the order they were allocated.
        WritePadded(output, Directory(nodes));
        if (miniFat.Count > 0)
        {
            var bytes = new byte[miniFatSectors * SectorSize];
            for (var i = 0; i < bytes.Length / 4; i++)
                Put32(bytes, i * 4, i < miniFat.Count ? miniFat[i] : Free);
            output.Write(bytes);
        }
        WritePadded(output, _root.Data);
        foreach (var node in large)
            WritePadded(output, node.Data);
        var table = new byte[fat.Length * 4];
        for (var i = 0; i < fat.Length; i++)
            Put32(table, i * 4, fat[i]);
        output.Write(table);
        // DIFAT sectors: the FAT sectors after the header's 109, 127 a sector, the last slot pointing to the next one.
        for (var d = 0; d < difatSectors; d++)
        {
            var sector = new byte[SectorSize];
            for (var i = 0; i < EntriesPerSector - 1; i++)
            {
                var fatIndex = HeaderDifatEntries + d * (EntriesPerSector - 1) + i;
                Put32(sector, i * 4, fatIndex < fatSectors ? (uint)(fatStart + fatIndex) : Free);
            }
            Put32(sector, SectorSize - 4, d + 1 < difatSectors ? (uint)(difatStart + d + 1) : EndOfChain);
            output.Write(sector);
        }
    }

    /// <summary>
    /// FAT and DIFAT sectors for <paramref name="dataSectors"/> sectors of content: the FAT lists every sector, its own
    /// and the DIFAT's included, so the sizes are found together.
    /// </summary>
    internal static (int Fat, int Difat) TableSizes(long dataSectors)
    {
        var (fat, difat) = (0, 0);
        while (true)
        {
            var neededFat = (int)((dataSectors + fat + difat + EntriesPerSector - 1) / EntriesPerSector);
            var neededDifat = neededFat <= HeaderDifatEntries ? 0 : (neededFat - HeaderDifatEntries + EntriesPerSector - 2) / (EntriesPerSector - 1);
            if (neededFat == fat && neededDifat == difat)
                return (fat, difat);
            (fat, difat) = (Math.Max(fat, neededFat), Math.Max(difat, neededDifat));
        }
    }

    private static void Number(Node node, List<Node> nodes)
    {
        node.Index = nodes.Count;
        nodes.Add(node);
        foreach (var member in node.Members)
            Number(member, nodes);
    }

    /// <summary>The depth of the deepest level of a balanced tree of <paramref name="count"/> nodes (0-based).</summary>
    private static int Depth(int count) => count <= 0 ? 0 : System.Numerics.BitOperations.Log2((uint)count);

    /// <summary>
    /// A balanced tree of the sorted members: the middle one is the root of each range. Nodes on the deepest level,
    /// the only one that may be incomplete, are red and every other node black, so every path holds as many black
    /// nodes and no red node has a red child: a valid red-black tree.
    /// </summary>
    private static uint Tree(List<Node> sorted, int low, int high, int depth, int deepest)
    {
        if (low > high)
            return Free;
        var middle = (low + high) / 2;
        var node = sorted[middle];
        node.Red = depth == deepest && deepest > 0 && !Perfect(sorted.Count);
        node.Left = Tree(sorted, low, middle - 1, depth + 1, deepest);
        node.Right = Tree(sorted, middle + 1, high, depth + 1, deepest);
        return (uint)node.Index;
    }

    private static bool Perfect(int count) => ((count + 1) & count) == 0;

    private static byte[] Directory(List<Node> nodes)
    {
        var bytes = new byte[Sectors((long)nodes.Count * DirectoryEntrySize) * SectorSize];
        for (var i = 0; i < bytes.Length / DirectoryEntrySize; i++)
        {
            var entry = bytes.AsSpan(i * DirectoryEntrySize, DirectoryEntrySize);
            if (i >= nodes.Count)
            {
                // Unused entries: no name, no siblings, no child.
                BinaryPrimitives.WriteUInt32LittleEndian(entry[68..], Free);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[72..], Free);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[76..], Free);
                continue;
            }
            var node = nodes[i];
            Encoding.Unicode.GetBytes(node.Name).CopyTo(entry);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[64..], (ushort)((node.Name.Length + 1) * 2));
            entry[66] = (byte)(i == 0 ? 5 : node.Storage ? 1 : 2);
            entry[67] = (byte)(node.Red ? 0 : 1);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[68..], node.Left);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[72..], node.Right);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[76..], node.Storage ? node.Child : Free);
            // Storages have no sectors; an empty stream (or mini stream) starts at the end of a chain.
            var stream = !node.Storage || i == 0;
            BinaryPrimitives.WriteUInt32LittleEndian(entry[116..], !stream ? 0 : node.Data.Length > 0 ? node.Start : EndOfChain);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[120..], stream ? (ulong)node.Data.Length : 0);
        }
        return bytes;
    }

    /// <summary>CFB's name order: shorter names first, then by upper-case UTF-16 code units.</summary>
    internal static int Compare(string? a, string? b)
    {
        a ??= "";
        b ??= "";
        if (a.Length != b.Length)
            return a.Length.CompareTo(b.Length);
        for (var i = 0; i < a.Length; i++)
        {
            var (x, y) = (char.ToUpperInvariant(a[i]), char.ToUpperInvariant(b[i]));
            if (x != y)
                return x.CompareTo(y);
        }
        return 0;
    }

    private static int Sectors(long length) => (int)((length + SectorSize - 1) / SectorSize);

    private static void WritePadded(Stream output, byte[] data)
    {
        output.Write(data);
        var rest = data.Length % SectorSize;
        if (rest != 0)
            output.Write(new byte[SectorSize - rest]);
    }

    private static void Put16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);

    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
