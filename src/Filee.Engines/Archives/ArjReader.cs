// Reads ARJ archives, written from the ARJ technical documentation; Filee's reader is its own. The archive is a main
// header, one local header per entry each followed by its compressed data, and an empty header at the end. Every
// header is:
//
//   0x60 0xEA, basic header size u16 (0 ends the archive, at most 2600), the basic header, its CRC-32, then extended
//   headers (size u16, data, CRC-32) until a size of 0
//
// Basic header (little endian): its fixed part's size u8 (30 or more), archiver version u8, minimum version u8, host OS
// u8, flags u8 (0x01 password, 0x02 names in the Windows code page (main header), 0x04 continued in the next volume, 0x08 continued from the previous one, 0x10 "/" as the
// path separator), method u8, file type u8 (0 binary, 1 text, 2 the main header's comment type, 3 folder, 4 volume
// label, 5 chapter label), reserved u8, DOS time u32, packed size u32, original size u32, CRC-32 of the data u32,
// file spec position u16, access mode u16, two chapter bytes; then the name and the comment, each ending in a zero
// byte. Names are in the DOS code page.
//
// Methods: 0 stored; 1 to 3 the static Huffman LZSS that LHA's -lh7- uses too (LzhDecoder, 26 KB dictionary within a
// 64 KB window); 4 "fastest": a bit stream (most significant first) of items, each a length code then a literal byte or
// a position. The length code is a run of n 1 bits (at most 7) ended by a 0 (not after the 7th), then n bits: value
// 2^n − 1 + those bits. 0 is a literal (8 bits follow); otherwise the match is value + 2 bytes long and its position
// is read the same way with runs of up to 4 and 9 + n bits (value 2^(9+n) − 512 + those bits), the distance being
// position + 1. Passwords are refused; multi-volume archives and other methods are left to 7-Zip.

using System.Buffers.Binary;

namespace Filee.Engines.Archives;

/// <summary>Unpacks ARJ archives.</summary>
internal static class ArjReader
{
    private const int MaxBasicHeader = 2600;
    private const int MaxExtendedHeaders = 64;

    private sealed record Entry(string Name, bool IsFolder, int Method, long PackedSize, long Size, uint Crc, DateTime? Modified, long DataOffset);

    public static void Extract(string archive, string folder, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(archive);
        using var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var entries = ReadEntries(stream, name, cancellationToken);
        ArchiveSafety.CheckListing([.. entries.Select(e => new ArchiveItem(e.Name, e.IsFolder, e.IsFolder ? 0 : e.Size))],
            name, stream.Length, folder);
        if (entries.FirstOrDefault(e => !e.IsFolder && e.Method > 4) is { } unknown)
            throw new NotSupportedException($"the ARJ method {unknown.Method}");

        var writer = new EntryWriter(folder, new UnpackBudget(name, stream.Length, folder), cancellationToken);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsFolder)
            {
                writer.AddFolder(entry.Name, entry.Modified);
                continue;
            }
            stream.Position = entry.DataOffset;
            var packed = new BoundedStream(stream, entry.PackedSize);
            using Stream data = entry.Method switch
            {
                0 => packed,
                4 => new ArjFastestDecoder(packed, entry.Size),
                _ => new LzhDecoder(packed, 16, 17, 5, entry.Size, 0),
            };
            writer.AddFile(entry.Name, data, entry.Size, entry.Crc, entry.Modified);
        }
        writer.Finish();
    }

    private static List<Entry> ReadEntries(FileStream stream, string archiveName, CancellationToken cancellationToken)
    {
        var entries = new List<Entry>();
        try
        {
            var main = ReadHeader(stream, archiveName, first: true)
                       ?? throw ArchiveSafety.Damaged(archiveName, "the ARJ main header is empty");
            if ((main[4] & 0x04) != 0)
                throw new NotSupportedException("multi-volume ARJ archives");
            var ansi = (main[4] & 0x02) != 0;
            while (ReadHeader(stream, archiveName, first: false) is { } header)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entries.Count >= ArchiveSafety.EntryLimit)
                    throw ArchiveSafety.TooManyEntries(archiveName);
                var flags = header[4];
                var method = header[5];
                var type = header[6];
                long packed = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
                long size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
                var offset = stream.Position;
                if (offset + packed > stream.Length)
                    throw ArchiveSafety.Damaged(archiveName, "the archive ends too early");
                stream.Position = offset + packed;
                if (type is 2 or 4 or 5)
                    continue; // comment, volume and chapter labels hold no file
                if (type > 5)
                    throw ArchiveSafety.Damaged(archiveName, "unknown ARJ entry type");
                if ((flags & 0x01) != 0)
                    throw ArchiveSafety.Encrypted(archiveName);
                if ((flags & 0x0C) != 0)
                    throw new NotSupportedException("multi-volume ARJ archives");
                var entryName = Name(header, ansi, archiveName);
                var time = AlzReader.FromDosTime(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)));
                entries.Add(new Entry(entryName, type == 3, method, packed, size, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20)), time, offset));
            }
            return entries;
        }
        catch (EndOfStreamException)
        {
            throw ArchiveSafety.Damaged(archiveName, "the archive ends too early");
        }
    }

    /// <summary>
    /// Reads one header and its extended headers and checks their CRCs; returns the basic header, or null for the end
    /// of the archive.
    /// </summary>
    private static byte[]? ReadHeader(FileStream stream, string archiveName, bool first)
    {
        Span<byte> start = stackalloc byte[4];
        var read = stream.ReadAtLeast(start, 4, throwOnEndOfStream: false);
        if (read < 4 || start[0] != 0x60 || start[1] != 0xEA)
        {
            if (first)
                throw ArchiveSafety.NotAnArchive(archiveName);
            if (read == 0)
                return null; // no end header
            throw ArchiveSafety.Damaged(archiveName, "invalid ARJ header");
        }
        var size = BinaryPrimitives.ReadUInt16LittleEndian(start[2..]);
        if (size == 0)
            return null;
        if (size > MaxBasicHeader)
            throw ArchiveSafety.Damaged(archiveName, "invalid ARJ header size");
        var header = new byte[size];
        stream.ReadExactly(header);
        Span<byte> crc = stackalloc byte[4];
        stream.ReadExactly(crc);
        if (Crc32.Compute(header) != BinaryPrimitives.ReadUInt32LittleEndian(crc))
            throw ArchiveSafety.Damaged(archiveName, "ARJ header checksum error");
        if (header[0] < 30 || header[0] > size)
            throw ArchiveSafety.Damaged(archiveName, "invalid ARJ header");

        Span<byte> extendedSize = stackalloc byte[2];
        for (var count = 0; ; count++)
        {
            stream.ReadExactly(extendedSize);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(extendedSize);
            if (length == 0)
                break;
            if (count >= MaxExtendedHeaders)
                throw ArchiveSafety.Damaged(archiveName, "invalid ARJ extended header");
            stream.Seek(length + 4L, SeekOrigin.Current);
        }
        return header;
    }

    /// <summary>
    /// The entry name: the zero-terminated bytes after the fixed part, in the DOS code page (the Windows one when the
    /// main header says so: ARJ32), or UTF-8 when they are valid UTF-8.
    /// </summary>
    private static string Name(byte[] header, bool ansi, string archiveName)
    {
        var bytes = header.AsSpan(header[0]);
        var end = bytes.IndexOf((byte)0);
        if (end < 0)
            throw ArchiveSafety.Damaged(archiveName, "invalid ARJ name");
        var name = ansi ? CabReader.DecodeName(bytes[..end]) : LibraryArchives.DecodeName(bytes[..end]);
        if (name.Trim('/', '\\').Length == 0)
            throw ArchiveSafety.Damaged(archiveName, "an entry has no name");
        return name;
    }
}

/// <summary>ARJ method 4 ("fastest") as a stream of the decoded bytes; see the comment at the top of ArjReader.</summary>
internal sealed class ArjFastestDecoder(Stream input, long size) : Stream
{
    private const int WindowSize = 1 << 15; // positions reach 2^13 + 2^12 + … − 512 + 2^13: under 16 KB

    private readonly MsbBitReader _bits = new(input);
    private readonly byte[] _window = new byte[WindowSize];
    private long _remaining = size;
    private int _position;
    private int _matchLength;
    private int _matchDistance;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var count = (int)Math.Min(buffer.Length, _remaining);
        for (var i = 0; i < count; i++)
        {
            if (_matchLength == 0)
            {
                var length = Code(0, 7);
                if (length == 0)
                {
                    buffer[i] = Put((byte)_bits.Read(8));
                    continue;
                }
                _matchLength = length + 2;
                _matchDistance = Code(9, 4) + 1; // before the first byte the window holds zeros
            }
            buffer[i] = Put(_window[(_position - _matchDistance) & (WindowSize - 1)]);
            _matchLength--;
        }
        _remaining -= count;
        return count;
    }

    /// <summary>A run of up to <paramref name="maxRun"/> 1 bits, then <paramref name="minBits"/> + run bits.</summary>
    private int Code(int minBits, int maxRun)
    {
        var run = 0;
        while (run < maxRun && _bits.Read(1) == 1)
            run++;
        var width = minBits + run;
        return (1 << width) - (1 << minBits) + (int)_bits.Read(width);
    }

    private byte Put(byte value)
    {
        _window[_position] = value;
        _position = (_position + 1) & (WindowSize - 1);
        return value;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
