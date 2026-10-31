// A read-only compound file (CFB, "structured storage") reader of Filee's own, for HWP 5.0 documents: a file of
// fixed-size sectors whose allocation table (FAT) chains them into streams, with a directory of named storages and
// streams. Streams below the mini stream cutoff live in 64-byte mini sectors inside the root entry's stream.
//
// HWP files come from anywhere, so nothing in the file is trusted: every sector number is checked against the sectors
// the file has, every chain is walked at most once per sector (a cycle ends the read), a chain must be as long as its
// stream says, the directory is walked without recursion and visits each entry once, and stream sizes stay within
// DocumentLimits (MaxPartBytes per stream, MaxPackageBytes in all). A damaged file fails with InvalidDataException,
// one past a limit with error.document_too_complex.

using System.Buffers.Binary;
using System.Text;
using Filee.Engines.Documents;

namespace Filee.Engines.Hwp.Binary;

/// <summary>A compound file opened for reading its streams.</summary>
internal sealed class CompoundFile : IDisposable
{
    private static ReadOnlySpan<byte> Signature => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private const uint MaxRegularSector = 0xFFFFFFFA;
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint NoStream = 0xFFFFFFFF;
    private const int HeaderDifatEntries = 109;
    private const int DirectoryEntrySize = 128;

    private readonly Stream _file;
    private readonly DocumentLimits _limits;
    private readonly int _sectorSize;
    private readonly long _sectorCount;
    private readonly uint[] _fat;
    private readonly uint[] _miniFat;
    private readonly uint _miniCutoff;
    private readonly List<Entry> _entries;
    private byte[]? _miniStream;
    private long _total;

    /// <summary>A directory entry: a storage (folder) or a stream, with its full path below the root ("BodyText/Section0").</summary>
    private sealed record Entry(string Name, int Type, uint Left, uint Right, uint Child, uint Start, long Size)
    {
        public string Path { get; set; } = "";
    }

    private CompoundFile(Stream file, DocumentLimits limits)
    {
        _file = file;
        _limits = limits;
        var header = new byte[512];
        if (Fill(0, header) < header.Length || !header.AsSpan(0, 8).SequenceEqual(Signature))
            throw new InvalidDataException("The file is not a compound file.");

        var major = U16(header, 26);
        var shift = U16(header, 30);
        if (major is not (3 or 4) || shift != (major == 3 ? 9 : 12) || U16(header, 32) != 6 || U16(header, 28) != 0xFFFE)
            throw Damaged("its header is not valid");
        _sectorSize = 1 << shift;
        // The header takes the first sector's place; a last sector cut short still counts (it is read as far as it goes).
        _sectorCount = Math.Max(0, (file.Length - _sectorSize + _sectorSize - 1) / _sectorSize);
        _miniCutoff = U32(header, 56);

        _fat = ReadFat(header);
        _entries = ReadDirectory(U32(header, 48));
        _miniFat = U32(header, 64) is var miniFatCount && miniFatCount > 0
            ? ToUInts(ReadChain(U32(header, 60), (long)miniFatCount * _sectorSize, "the mini allocation table"))
            : [];
        NamePaths();
    }

    /// <summary>Opens <paramref name="path"/>; the file stays open until the reader is disposed.</summary>
    public static CompoundFile Open(string path, DocumentLimits? limits = null)
    {
        var file = File.OpenRead(path);
        try
        {
            return new CompoundFile(file, limits ?? DocumentLimits.Default);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>A compound file in a stream (left open; for tests).</summary>
    public static CompoundFile Open(Stream stream, DocumentLimits? limits = null) => new(stream, limits ?? DocumentLimits.Default);

    /// <summary>True when <paramref name="path"/> starts with the compound file signature.</summary>
    public static bool HasSignature(string path)
    {
        Span<byte> start = stackalloc byte[8];
        using var file = File.OpenRead(path);
        return file.ReadAtLeast(start, 8, throwOnEndOfStream: false) == 8 && start.SequenceEqual(Signature);
    }

    public void Dispose() => _file.Dispose();

    /// <summary>Paths of every stream, like "BodyText/Section0".</summary>
    public IEnumerable<string> Streams => _entries.Where(e => e.Type == 2 && e.Path.Length > 0).Select(e => e.Path);

    /// <summary>True when there is a stream or storage at <paramref name="path"/> (names compare without case, as in CFB).</summary>
    public bool Exists(string path) => Find(path) is not null;

    /// <summary>The bytes of the stream at <paramref name="path"/>; null when there is none.</summary>
    public byte[]? Read(string path)
    {
        if (Find(path) is not { Type: 2 } entry)
            return null;
        if (entry.Size > _limits.MaxPartBytes)
            throw DocumentLimits.Exceeded($"A part of the document is larger than {DocumentLimits.Megabytes(_limits.MaxPartBytes)}.");
        Count(entry.Size);
        return entry.Size < _miniCutoff ? ReadMini(entry) : ReadChain(entry.Start, entry.Size, entry.Path);
    }

    /// <summary>Counts bytes read against the document's total (also for what streams inflate to).</summary>
    public void Count(long bytes)
    {
        _total += bytes;
        if (_total > _limits.MaxPackageBytes)
            throw DocumentLimits.Exceeded($"The document unpacks to more than {DocumentLimits.Megabytes(_limits.MaxPackageBytes)}.");
    }

    public DocumentLimits Limits => _limits;

    private Entry? Find(string path) => _entries.FirstOrDefault(e => e.Path.Length > 0 && string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));

    // ───────────────────────── Allocation tables ─────────────────────────

    /// <summary>The FAT: its sectors are listed in the header (109) and in a chain of DIFAT sectors after that.</summary>
    private uint[] ReadFat(byte[] header)
    {
        var fatSectors = U32(header, 44);
        if (fatSectors > _sectorCount)
            throw Damaged("its allocation table is larger than the file");
        var sectors = new List<uint>((int)fatSectors);
        for (var i = 0; i < HeaderDifatEntries && sectors.Count < fatSectors; i++)
            sectors.Add(U32(header, 76 + i * 4));

        var next = U32(header, 68);
        var perSector = _sectorSize / 4 - 1;
        var seen = new HashSet<uint>();
        var buffer = new byte[_sectorSize];
        while (sectors.Count < fatSectors)
        {
            if (next >= MaxRegularSector)
                throw Damaged("its allocation table is cut short");
            if (!seen.Add(next) || next >= _sectorCount)
                throw Damaged("the list of its allocation table sectors loops or points outside the file");
            ReadSector(next, buffer);
            for (var i = 0; i < perSector && sectors.Count < fatSectors; i++)
                sectors.Add(BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i * 4)));
            next = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(perSector * 4));
        }

        var fat = new uint[(long)fatSectors * (_sectorSize / 4)];
        for (var i = 0; i < sectors.Count; i++)
        {
            if (sectors[i] >= _sectorCount)
                throw Damaged("its allocation table points outside the file");
            ReadSector(sectors[i], buffer);
            for (var j = 0; j < _sectorSize / 4; j++)
                fat[i * (_sectorSize / 4) + j] = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(j * 4));
        }
        return fat;
    }

    /// <summary>
    /// The bytes of a chain of regular sectors: <paramref name="size"/> bytes, or the whole chain when size is negative
    /// (the directory, whose length only its chain gives). Every sector is visited once at most.
    /// </summary>
    private byte[] ReadChain(uint start, long size, string what)
    {
        var chain = Chain(_fat, start, _sectorCount, size < 0 ? -1 : (size + _sectorSize - 1) / _sectorSize, what);
        var length = size < 0 ? (long)chain.Count * _sectorSize : size;
        if (length > _limits.MaxPartBytes)
            throw DocumentLimits.Exceeded($"A part of the document is larger than {DocumentLimits.Megabytes(_limits.MaxPartBytes)}.");
        var bytes = new byte[length];
        var buffer = new byte[_sectorSize];
        for (var i = 0; i < chain.Count; i++)
        {
            ReadSector(chain[i], buffer);
            var offset = (long)i * _sectorSize;
            buffer.AsSpan(0, (int)Math.Min(_sectorSize, length - offset)).CopyTo(bytes.AsSpan((int)offset));
        }
        return bytes;
    }

    private byte[] ReadMini(Entry entry)
    {
        _miniStream ??= _entries.Count > 0 && _entries[0].Size > 0
            ? ReadChain(_entries[0].Start, _entries[0].Size, "the mini stream")
            : [];
        const int MiniSize = 64;
        var chain = Chain(_miniFat, entry.Start, _miniStream.Length / MiniSize, (entry.Size + MiniSize - 1) / MiniSize, entry.Path);
        var bytes = new byte[entry.Size];
        for (var i = 0; i < chain.Count; i++)
        {
            var offset = (long)i * MiniSize;
            _miniStream.AsSpan((int)(chain[i] * MiniSize), (int)Math.Min(MiniSize, entry.Size - offset)).CopyTo(bytes.AsSpan((int)offset));
        }
        return bytes;
    }

    /// <summary>
    /// The sectors of a chain in <paramref name="table"/>, checked: each within <paramref name="sectors"/>, none twice,
    /// and exactly <paramref name="expected"/> of them when that is known (-1 = until the end of the chain).
    /// </summary>
    private static List<uint> Chain(uint[] table, uint start, long sectors, long expected, string what)
    {
        var chain = new List<uint>();
        if (expected == 0)
            return chain;
        var seen = new HashSet<uint>();
        for (var sector = start; sector != EndOfChain; sector = table[sector])
        {
            if (sector >= sectors || sector >= table.Length)
                throw Damaged($"{what} points outside the file");
            if (!seen.Add(sector))
                throw Damaged($"{what} loops");
            chain.Add(sector);
            if (expected > 0 && chain.Count == expected)
                return chain;
        }
        if (expected > 0)
            throw Damaged($"{what} is cut short");
        return chain;
    }

    // ───────────────────────── Directory ─────────────────────────

    private List<Entry> ReadDirectory(uint start)
    {
        var bytes = ReadChain(start, -1, "the directory");
        var count = bytes.Length / DirectoryEntrySize;
        if (count > _limits.MaxPackageEntries)
            throw DocumentLimits.Exceeded($"The package has more than {DocumentLimits.Count(_limits.MaxPackageEntries)} parts.");
        var entries = new List<Entry>(count);
        for (var i = 0; i < count; i++)
        {
            var e = bytes.AsSpan(i * DirectoryEntrySize, DirectoryEntrySize);
            var nameLength = Math.Clamp((int)BinaryPrimitives.ReadUInt16LittleEndian(e[64..]), 0, 64);
            var name = Encoding.Unicode.GetString(e[..Math.Max(0, nameLength - 2)]);
            var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(e[120..]);
            // Version 3 files keep only the low 32 bits of the size; the high ones may hold garbage.
            if (_sectorSize == 512)
                size &= 0xFFFFFFFF;
            if (size < 0)
                throw Damaged("a stream has an impossible size");
            entries.Add(new Entry(name, e[66],
                BinaryPrimitives.ReadUInt32LittleEndian(e[68..]), BinaryPrimitives.ReadUInt32LittleEndian(e[72..]),
                BinaryPrimitives.ReadUInt32LittleEndian(e[76..]), BinaryPrimitives.ReadUInt32LittleEndian(e[116..]), size));
        }
        if (entries.Count == 0 || entries[0].Type != 5)
            throw Damaged("it has no root entry");
        return entries;
    }

    /// <summary>
    /// Names every entry by its path, walking the tree of storages (child) and their red-black trees of members (left,
    /// right) with a stack; an entry reached twice means the tree loops.
    /// </summary>
    private void NamePaths()
    {
        var visited = new bool[_entries.Count];
        visited[0] = true;
        var stack = new Stack<(uint Index, string Folder)>();
        Push(stack, _entries[0].Child, "");
        while (stack.Count > 0)
        {
            var (index, folder) = stack.Pop();
            if (visited[index])
                throw Damaged("its directory loops");
            visited[index] = true;
            var entry = _entries[(int)index];
            entry.Path = folder + entry.Name;
            Push(stack, entry.Left, folder);
            Push(stack, entry.Right, folder);
            if (entry.Type == 1)
                Push(stack, entry.Child, entry.Path + "/");
        }

        void Push(Stack<(uint, string)> pending, uint index, string folder)
        {
            if (index == NoStream)
                return;
            if (index >= _entries.Count)
                throw Damaged("its directory points outside the directory");
            pending.Push((index, folder));
        }
    }

    // ───────────────────────── Helpers ─────────────────────────

    private void ReadSector(uint sector, byte[] buffer)
    {
        var read = Fill((sector + 1L) * _sectorSize, buffer);
        buffer.AsSpan(read).Clear(); // the file's last sector may be cut short
    }

    private int Fill(long offset, byte[] buffer)
    {
        _file.Position = offset;
        return _file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
    }

    private static uint[] ToUInts(byte[] bytes)
    {
        var values = new uint[bytes.Length / 4];
        for (var i = 0; i < values.Length; i++)
            values[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4));
        return values;
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));

    private static InvalidDataException Damaged(string reason) => new($"The compound file is damaged: {reason}.");
}
