// What stands between a hostile disk image and the DiscUtils readers (UDF, HFS+, DMG), whose behaviour on bad input
// Filee does not control:
//
//   DiskImageStream: the only way a volume is read. One window of the image (a partition), read-only. Every read checks
//     cancellation and the stack (a B-tree whose nodes point back at themselves recurses once per read: it ends in
//     InsufficientExecutionStackException instead of a crashed process) and counts against a budget the caller sets
//     for each call into the library, so structures that lead in circles stop after a bounded amount of reading.
//   UdifCheck: the DMG trailer ("koly", 512 bytes at the end, big endian: data fork offset u64 at 24, XML offset and
//     length u64 at 216 and 224, sector count i64 at 492) and its XML property list are checked before DiscUtils.Dmg
//     reads them. In the list, resource-fork / blkx holds one "mish" block table per partition (first sector i64 at
//     8, sector count at 16, data start u64 at 24, decompression buffer in sectors u32 at 32, run count i32 at 200,
//     then 40-byte runs from 204: type u32, first sector i64 at 8, sector count at 16, compressed offset at 24 and
//     length at 32). Every run must lie inside the file and fit the buffer DiscUtils allocates for it, nesting and
//     sizes are capped, and run types DiscUtils lacks (LZFSE, LZMA) are sent to 7-Zip before any of them is read.
//     Written from Apple's UDIF layout as hdiutil writes it.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Xml;

namespace Filee.Engines.Archives;

/// <summary>Counts what one call into a disk image library may read, and stops it past that.</summary>
internal sealed class DiskImageGuard(string archiveName, CancellationToken cancellationToken)
{
    /// <summary>What a call that only reads the file system's structures (a folder listing, a file's size) may read.</summary>
    public const long StructureBudget = 256L * 1024 * 1024;

    private long _left = StructureBudget;

    public string ArchiveName => archiveName;

    /// <summary>Starts a new call that may read <paramref name="bytes"/>.</summary>
    public void Allow(long bytes) => _left = bytes;

    public void Count(int bytes)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if ((_left -= bytes) < 0)
            throw ArchiveSafety.Damaged(archiveName, "its file system structures lead in circles");
    }
}

/// <summary>A read-only window of a disk image whose reads go through a <see cref="DiskImageGuard"/>.</summary>
internal sealed class DiskImageStream(Stream image, long offset, long length, DiskImageGuard guard) : Stream
{
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= length || buffer.Length == 0)
            return 0;
        var wanted = (int)Math.Min(buffer.Length, length - _position);
        guard.Count(wanted);
        int read;
        try
        {
            image.Position = offset + _position;
            read = image.ReadAtLeast(buffer[..wanted], wanted, throwOnEndOfStream: false);
        }
        catch (Exception ex) when (DiskImageReader.Translate(ex, guard.ArchiveName) is { } translated)
        {
            throw translated; // the image below may be DiscUtils.Dmg's blocks
        }
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        _ => length + offset,
    };

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Checks a DMG's UDIF trailer and block tables before DiscUtils.Dmg reads them.</summary>
internal static class UdifCheck
{
    private const int TrailerSize = 512;
    private const long MaxXmlBytes = 16L * 1024 * 1024;
    private const int MaxXmlDepth = 32;
    private const int MaxBlockTables = 4096;
    private const long MaxRuns = 4_000_000;

    /// <summary>The largest decompression buffer a block table may ask for: 64 MB (hdiutil uses 260 KB).</summary>
    private const long MaxBufferSectors = 131072;

    private const uint Raw = 1, Zeros = 2, Ignored = 0, Adc = 0x80000004, Zlib = 0x80000005, Bzip2 = 0x80000006,
        Lzfse = 0x80000007, Lzma = 0x80000008, Comment = 0x7FFFFFFE, Terminator = 0xFFFFFFFF;

    /// <summary>True for an encrypted DMG (FileVault's "encrcdsa" header or the older "cdsaencr" trailer).</summary>
    public static bool IsEncrypted(Stream file)
    {
        var head = DiskImagePartitions.ReadAt(file, 0, 8);
        var tail = DiskImagePartitions.ReadAt(file, Math.Max(0, file.Length - 8), 8);
        return head.AsSpan().SequenceEqual("encrcdsa"u8) || tail.AsSpan().SequenceEqual("cdsaencr"u8);
    }

    /// <summary>
    /// False when the file has no UDIF trailer (a plain disk image named .dmg); throws when the trailer or its block
    /// tables are damaged or use what DiscUtils does not read.
    /// </summary>
    public static bool Check(Stream file, string archiveName)
    {
        var length = file.Length;
        if (length < TrailerSize)
            return false;
        var koly = DiskImagePartitions.ReadAt(file, length - TrailerSize, TrailerSize);
        if (!koly.AsSpan(0, 4).SequenceEqual("koly"u8))
            return false;
        var dataForkOffset = BinaryPrimitives.ReadUInt64BigEndian(koly.AsSpan(24));
        var xmlOffset = BinaryPrimitives.ReadUInt64BigEndian(koly.AsSpan(216));
        var xmlLength = BinaryPrimitives.ReadUInt64BigEndian(koly.AsSpan(224));
        var sectors = BinaryPrimitives.ReadInt64BigEndian(koly.AsSpan(492));
        if (xmlLength == 0)
            throw new NotSupportedException("a DMG without an XML block list (resource fork only)");
        if (dataForkOffset != 0)
            throw new NotSupportedException("a DMG whose data does not start at the beginning of the file");
        if (xmlLength > MaxXmlBytes)
            throw ArchiveSafety.Oversized(archiveName, "its block list is larger than " + ArchiveSafety.FormatSize(MaxXmlBytes));
        if (xmlOffset > (ulong)(length - TrailerSize) || xmlLength > (ulong)(length - TrailerSize) - xmlOffset)
            throw ArchiveSafety.Damaged(archiveName, "the block list lies beyond the end of the file");
        if (sectors <= 0 || sectors > long.MaxValue / 512)
            throw ArchiveSafety.Damaged(archiveName, "invalid sector count");

        var xml = DiskImagePartitions.ReadAt(file, (long)xmlOffset, (int)xmlLength);
        var tables = 0;
        long runs = 0;
        foreach (var table in BlockTables(xml, archiveName))
        {
            if (++tables > MaxBlockTables)
                throw ArchiveSafety.Oversized(archiveName, "it has more than " + MaxBlockTables + " block tables");
            runs += CheckTable(table, sectors, length, archiveName);
            if (runs > MaxRuns)
                throw ArchiveSafety.Oversized(archiveName, "its block tables hold more than " + MaxRuns.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " runs");
        }
        if (tables == 0)
            throw ArchiveSafety.Damaged(archiveName, "the block list holds no block tables");
        return true;
    }

    /// <summary>One "mish" table: returns its run count.</summary>
    private static long CheckTable(byte[] table, long diskSectors, long fileLength, string archiveName)
    {
        if (table.Length < 204 || !table.AsSpan(0, 4).SequenceEqual("mish"u8))
            throw ArchiveSafety.Damaged(archiveName, "invalid block table");
        var first = BinaryPrimitives.ReadInt64BigEndian(table.AsSpan(8));
        var count = BinaryPrimitives.ReadInt64BigEndian(table.AsSpan(16));
        var dataStart = BinaryPrimitives.ReadUInt64BigEndian(table.AsSpan(24));
        long buffer = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(32));
        var runCount = BinaryPrimitives.ReadInt32BigEndian(table.AsSpan(200));
        if (first < 0 || count < 0 || first > diskSectors || count > diskSectors - first)
            throw ArchiveSafety.Damaged(archiveName, "a block table lies outside the disk");
        if (dataStart != 0)
            throw new NotSupportedException("a DMG whose blocks are stored at an offset");
        if (buffer > MaxBufferSectors)
            throw ArchiveSafety.Oversized(archiveName, "a block table asks for a buffer larger than " + ArchiveSafety.FormatSize(MaxBufferSectors * 512));
        if (runCount < 0 || runCount > (table.Length - 204) / 40)
            throw ArchiveSafety.Damaged(archiveName, "a block table is cut short");
        for (var i = 0; i < runCount; i++)
        {
            var run = table.AsSpan(204 + i * 40, 40);
            var type = BinaryPrimitives.ReadUInt32BigEndian(run);
            var start = BinaryPrimitives.ReadInt64BigEndian(run[8..]);
            var sectors = BinaryPrimitives.ReadInt64BigEndian(run[16..]);
            var offset = BinaryPrimitives.ReadInt64BigEndian(run[24..]);
            var packed = BinaryPrimitives.ReadInt64BigEndian(run[32..]);
            if (start < 0 || sectors < 0 || start > count || sectors > count - start)
                throw ArchiveSafety.Damaged(archiveName, "a run lies outside its block table");
            if (sectors == 0 || type is Comment or Terminator)
                continue;
            switch (type)
            {
                case Zeros:
                    break;
                case Raw:
                    if (offset < 0 || offset > fileLength || sectors > (fileLength - offset) / 512)
                        throw ArchiveSafety.Damaged(archiveName, "a run lies beyond the end of the file");
                    break;
                case Adc or Zlib or Bzip2:
                    if (sectors > buffer)
                        throw ArchiveSafety.Damaged(archiveName, "a run is larger than its block table's buffer");
                    if (offset < 0 || packed < 0 || offset > fileLength || packed > fileLength - offset)
                        throw ArchiveSafety.Damaged(archiveName, "a run lies beyond the end of the file");
                    break;
                case Lzfse:
                    throw new NotSupportedException("LZFSE-compressed DMG blocks");
                case Lzma:
                    throw new NotSupportedException("LZMA-compressed DMG blocks");
                case Ignored:
                    throw new NotSupportedException("DMG runs of type 0");
                default:
                    throw new NotSupportedException($"DMG runs of type 0x{type:X8}");
            }
        }
        return runCount;
    }

    /// <summary>
    /// The decoded "Data" of every entry of resource-fork / blkx, read as a stream with a nesting limit (DiscUtils'
    /// own property list reader recurses once per level). DTDs are ignored, nothing outside is fetched.
    /// </summary>
    private static IEnumerable<byte[]> BlockTables(byte[] xml, string archiveName)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            MaxCharactersInDocument = MaxXmlBytes,
        };
        var tables = new List<byte[]>();
        var keys = new string?[MaxXmlDepth + 2];
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(xml), settings);
            reader.Read();
            while (!reader.EOF)
            {
                if (reader.Depth > MaxXmlDepth)
                    throw ArchiveSafety.Damaged(archiveName, "the block list is nested too deeply");
                if (reader.NodeType == XmlNodeType.Element)
                {
                    var depth = reader.Depth;
                    if (reader.LocalName == "key")
                    {
                        keys[depth] = reader.ReadElementContentAsString();
                        continue;
                    }
                    if (reader.LocalName is "dict" or "array")
                        keys[depth + 1] = null; // a new container starts without a key
                    // plist (0) / dict (1) / resource-fork: dict (2) / blkx: array (3) / dict (4) / Data: data (5)
                    if (reader.LocalName == "data" && depth == 5 && keys[5] == "Data" && keys[3] == "blkx" && keys[2] == "resource-fork")
                    {
                        tables.Add(Convert.FromBase64String(reader.ReadElementContentAsString()));
                        continue;
                    }
                }
                reader.Read();
            }
        }
        catch (Exception ex) when (ex is XmlException or FormatException)
        {
            throw ArchiveSafety.Damaged(archiveName, "invalid block list: " + ex.Message.TrimEnd('.'));
        }
        return tables;
    }
}
