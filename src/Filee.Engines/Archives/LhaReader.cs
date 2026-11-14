// Reads LHA / LZH archives. Written from the LHA header notes (header levels 0 to 3) and the description of the
// "-lh5-" family of methods; Filee's reader is its own. Each entry is a header followed by its compressed data, and
// the archive ends with a zero byte (or simply ends). Little-endian fields:
//
//   level 0  size u8 (header bytes after the first two), checksum u8 (byte sum of those), method "-lh5-", packed u32,
//            original u32, DOS time u32, attribute u8, level u8, name length u8, name, CRC-16 u16 (optional), then an
//            optional extended area
//   level 1  as level 0 up to the CRC (the packed size counts the extended headers too, attribute byte 0x20), then OS
//            id u8 and the size u16 of the first extended header
//   level 2  whole header size u16, method, packed u32, original u32, Unix time u32 (UTC), 0x20, level, CRC-16 u16,
//            OS id u8, size u16 of the first extended header
//   level 3  word size u16 (4), method … OS id as in level 2, whole header size u32, size u32 of the first extended header
//
// An extended header is a type byte, its data and the size of the next one (u16, u32 in level 3; 0 ends the chain).
// Types read here: 0x01 file name, 0x02 folder name (0xFF between folders), 0x40 MS-DOS attributes, 0x41 Windows
// times (creation, modification, access as FILETIME), 0x42 64-bit sizes, 0x50 Unix mode, 0x54 Unix time. Folders use
// the method "-lhd-"; Unix symbolic links are "-lhd-" entries named "link|target" (or with the link bit in the mode)
// and are left out. Names in levels 0 and 1 separate folders with "\" or 0xFF.
//
// Methods: -lh0- and -lz4- stored; -lh4- to -lh7- static Huffman LZSS (LzhDecoder below, also used for ARJ methods 1
// to 3) with 4, 8, 32 and 64 KB dictionaries. Others (-lh1-, -lzs-, -lz5-, -pm?- …) are left to 7-Zip. The data's
// CRC-16 (polynomial 0xA001, as in ARC) is checked.

using System.Buffers.Binary;
using System.Text;

namespace Filee.Engines.Archives;

/// <summary>Unpacks LHA / LZH archives.</summary>
internal static class LhaReader
{
    /// <summary>Extended header bytes of one entry: real ones hold a name, a few attributes and times.</summary>
    private const int MaxExtendedBytes = 1 << 20;

    private const int MaxExtendedHeaders = 1024;

    /// <summary>One entry of the archive.</summary>
    private sealed record Entry(string Name, bool IsFolder, bool IsLink, string Method, long PackedSize, long Size, ushort? Crc,
        DateTime? Modified, long DataOffset);

    public static void Extract(string archive, string folder, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(archive);
        using var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var entries = ReadEntries(stream, name, cancellationToken);
        ArchiveSafety.CheckListing([.. entries.Select(e => new ArchiveItem(e.Name, e.IsFolder, e.IsFolder || e.IsLink ? 0 : e.Size, IsLink: e.IsLink))],
            name, stream.Length, folder);
        // Methods Filee can't decode go to 7-Zip before anything is written.
        if (entries.FirstOrDefault(e => !e.IsFolder && !e.IsLink && DictionaryBits(e.Method) is null) is { } unknown)
            throw new NotSupportedException($"the LHA method {unknown.Method}");

        var writer = new EntryWriter(folder, new UnpackBudget(name, stream.Length, folder), cancellationToken);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsLink)
                continue;
            if (entry.IsFolder)
            {
                writer.AddFolder(entry.Name, entry.Modified);
                continue;
            }
            stream.Position = entry.DataOffset;
            var packed = new BoundedStream(stream, entry.PackedSize);
            var bits = DictionaryBits(entry.Method)!.Value;
            using var data = new Crc16Stream(bits == 0 ? packed : new LzhDecoder(packed, bits, bits + 1, bits <= 13 ? 4 : 5, entry.Size, 0x20));
            writer.AddFile(entry.Name, data, entry.Size, null, entry.Modified);
            if (entry.Crc is { } crc && data.Value != crc)
                throw ArchiveSafety.Damaged(name, $"checksum error in {entry.Name}");
        }
        writer.Finish();
    }

    /// <summary>Dictionary size of a method as a power of two; 0 for stored data, null for methods Filee doesn't decode.</summary>
    private static int? DictionaryBits(string method) => method switch
    {
        "-lh0-" or "-lz4-" => 0,
        "-lh4-" => 12,
        "-lh5-" => 13,
        "-lh6-" => 15,
        "-lh7-" => 16,
        _ => null,
    };

    private static List<Entry> ReadEntries(FileStream stream, string archiveName, CancellationToken cancellationToken)
    {
        var entries = new List<Entry>();
        var start = new byte[24];
        try
        {
            while (stream.Position < stream.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var position = stream.Position;
                var read = stream.ReadAtLeast(start, start.Length, throwOnEndOfStream: false);
                if (read == 0 || start[0] == 0)
                    break; // the end marker, or an archive without one
                if (read < 22 || start[2] != '-' || start[6] != '-')
                {
                    if (entries.Count == 0)
                        throw ArchiveSafety.NotAnArchive(archiveName);
                    throw ArchiveSafety.Damaged(archiveName, "invalid LHA header");
                }
                if (entries.Count >= ArchiveSafety.EntryLimit)
                    throw ArchiveSafety.TooManyEntries(archiveName);
                stream.Position = position;
                var entry = start[20] switch
                {
                    0 or 1 => ReadLevel01(stream, archiveName, start[20]),
                    2 or 3 => ReadLevel23(stream, archiveName, start[20]),
                    _ => throw new NotSupportedException($"LHA header level {start[20]}"),
                };
                if (entry.DataOffset + entry.PackedSize > stream.Length)
                    throw ArchiveSafety.Damaged(archiveName, "the archive ends too early");
                stream.Position = entry.DataOffset + entry.PackedSize;
                entries.Add(entry);
            }
            return entries;
        }
        catch (EndOfStreamException)
        {
            throw ArchiveSafety.Damaged(archiveName, "the archive ends too early");
        }
    }

    /// <summary>Levels 0 and 1: a header of up to 257 bytes with a byte-sum check.</summary>
    private static Entry ReadLevel01(FileStream stream, string archiveName, int level)
    {
        var size = stream.ReadByte();
        var checksum = stream.ReadByte();
        var header = new byte[size];
        stream.ReadExactly(header);
        var sum = 0;
        foreach (var b in header)
            sum += b;
        if ((byte)sum != checksum)
            throw ArchiveSafety.Damaged(archiveName, "LHA header checksum error");
        // Offsets below are into the header after its first two bytes.
        if (size < 20 || 20 + header[19] > size)
            throw ArchiveSafety.Damaged(archiveName, "invalid LHA header");
        var method = Encoding.ASCII.GetString(header, 0, 5);
        long packed = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(5));
        long original = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(9));
        var modified = AlzReader.FromDosTime(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(13)));
        var attributes = header[17];
        var nameBytes = header.AsSpan(20, header[19]);
        var rest = header.AsSpan(20 + header[19]);
        ushort? crc = rest.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(rest) : null;

        var info = new Extended { Name = SplitName(nameBytes) };
        if (level == 1)
        {
            if (rest.Length < 5)
                throw ArchiveSafety.Damaged(archiveName, "invalid LHA header");
            var next = BinaryPrimitives.ReadUInt16LittleEndian(rest[^2..]);
            // The packed size counts the extended headers, which follow the base header.
            var extended = ReadExtended(stream, archiveName, next, wide: false, info);
            packed -= extended;
            if (packed < 0)
                throw ArchiveSafety.Damaged(archiveName, "invalid LHA sizes");
        }
        return Finish(archiveName, method, packed, original, crc, modified, attributes, info, stream.Position);
    }

    /// <summary>Levels 2 and 3: fixed fields, then the extended headers carry the name.</summary>
    private static Entry ReadLevel23(FileStream stream, string archiveName, int level)
    {
        var start = stream.Position;
        var fixedSize = level == 2 ? 26 : 32;
        var header = new byte[fixedSize];
        stream.ReadExactly(header);
        var method = Encoding.ASCII.GetString(header, 2, 5);
        long packed = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(7));
        long original = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(11));
        var time = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(15));
        var crc = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(21));
        long total;
        long next;
        if (level == 2)
        {
            total = BinaryPrimitives.ReadUInt16LittleEndian(header);
            next = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(24));
        }
        else
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(header) != 4)
                throw ArchiveSafety.Damaged(archiveName, "invalid LHA header");
            total = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24));
            next = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28));
        }
        var info = new Extended();
        var extended = ReadExtended(stream, archiveName, next, wide: level == 3, info);
        if (total < fixedSize + extended)
            throw ArchiveSafety.Damaged(archiveName, "invalid LHA header size");
        var modified = time == 0 ? (DateTime?)null : DateTimeOffset.FromUnixTimeSeconds(time).LocalDateTime;
        return Finish(archiveName, method, packed, original, crc, modified, 0, info, start + total);
    }

    private static Entry Finish(string archiveName, string method, long packed, long original, ushort? crc, DateTime? modified,
        int attributes, Extended info, long dataOffset)
    {
        if (info.PackedSize is { } packed64)
            (packed, original) = (packed64, info.Size!.Value);
        if (packed < 0 || original < 0)
            throw ArchiveSafety.Damaged(archiveName, "invalid LHA sizes");
        var name = info.Folder is { Length: > 0 } folder ? folder + "/" + info.Name : info.Name ?? "";
        var unixType = info.UnixMode & 0xF000;
        var isFolder = method == "-lhd-" || unixType == 0x4000 || (attributes & 0x10) != 0 && original == 0;
        var isLink = unixType == 0xA000 || method == "-lhd-" && name.Contains('|', StringComparison.Ordinal);
        if (name.Trim('/').Length == 0)
            throw ArchiveSafety.Damaged(archiveName, "an entry has no name");
        return new Entry(name, isFolder && !isLink, isLink, method, packed, original, crc, info.Modified ?? modified, dataOffset);
    }

    /// <summary>What the extended headers say.</summary>
    private sealed class Extended
    {
        public string? Name { get; set; }
        public string? Folder { get; set; }
        public DateTime? Modified { get; set; }
        public int UnixMode { get; set; }
        public long? PackedSize { get; set; }
        public long? Size { get; set; }
    }

    /// <summary>Reads the chain of extended headers starting with one of <paramref name="size"/> bytes; returns their total size.</summary>
    private static long ReadExtended(FileStream stream, string archiveName, long size, bool wide, Extended info)
    {
        long total = 0;
        var sizeField = wide ? 4 : 2;
        for (var count = 0; size != 0; count++)
        {
            total += size;
            if (size < 1 + sizeField || total > MaxExtendedBytes || count >= MaxExtendedHeaders)
                throw ArchiveSafety.Damaged(archiveName, "invalid LHA extended header");
            var data = new byte[size];
            stream.ReadExactly(data);
            var body = data.AsSpan(1, data.Length - 1 - sizeField);
            switch (data[0])
            {
                case 0x01:
                    info.Name = SplitName(body);
                    break;
                case 0x02:
                    info.Folder = SplitName(body).Trim('/');
                    break;
                case 0x41 when body.Length >= 24:
                    {
                        var fileTime = BinaryPrimitives.ReadInt64LittleEndian(body[8..]);
                        if (fileTime > 0 && fileTime < DateTime.MaxValue.ToFileTimeUtc())
                            info.Modified = DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
                        break;
                    }
                case 0x42 when body.Length >= 16:
                    info.PackedSize = BinaryPrimitives.ReadInt64LittleEndian(body);
                    info.Size = BinaryPrimitives.ReadInt64LittleEndian(body[8..]);
                    break;
                case 0x50 when body.Length >= 2:
                    info.UnixMode = BinaryPrimitives.ReadUInt16LittleEndian(body);
                    break;
                case 0x54 when body.Length >= 4 && info.Modified is null:
                    info.Modified = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadUInt32LittleEndian(body)).LocalDateTime;
                    break;
            }
            size = wide ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(data.Length - 4)) : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(data.Length - 2));
        }
        return total;
    }

    /// <summary>
    /// A name with 0xFF between folders as a "/" path. "\" separators stay in the decoded text (the path checks split
    /// on both): split as bytes they would cut Shift-JIS and CP949 characters whose second byte is 0x5C.
    /// </summary>
    private static string SplitName(ReadOnlySpan<byte> bytes)
    {
        var parts = new List<string>();
        var start = 0;
        for (var i = 0; i <= bytes.Length; i++)
        {
            if (i < bytes.Length && bytes[i] != 0xFF)
                continue;
            if (i > start)
                parts.Add(LibraryArchives.DecodeName(bytes[start..i]));
            start = i + 1;
        }
        return string.Join('/', parts);
    }
}

/// <summary>Read-through stream that computes the CRC-16 (ARC, polynomial 0xA001) of what it passes on.</summary>
internal sealed class Crc16Stream(Stream inner) : Stream
{
    private static readonly ushort[] Table = CreateTable();
    private ushort _crc;

    /// <summary>The checksum of everything read so far.</summary>
    public ushort Value => _crc;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        var crc = _crc;
        foreach (var b in buffer[..read])
            crc = (ushort)(Table[(crc ^ b) & 0xFF] ^ (crc >> 8));
        _crc = crc;
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }

    private static ushort[] CreateTable()
    {
        var table = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            var c = (uint)i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xA001 ^ (c >> 1) : c >> 1;
            table[i] = (ushort)c;
        }
        return table;
    }
}

/// <summary>
/// The static Huffman LZSS of LHA's -lh4- to -lh7- and ARJ's methods 1 to 3, as a stream of the decoded bytes. Data comes
/// in blocks: a 16-bit count of codes, then three code length tables. A table starts with its symbol count (0: one
/// symbol, given next, used without bits). The pretree (19 symbols) and position tree lengths are 3 bits each, 7 and
/// more as 7 plus a run of 1 bits ended by a 0; after the third pretree length come 2 bits of zero lengths. The 510
/// literal/length code lengths are read through the pretree: 0 one zero, 1 a 4-bit count + 3 zeros, 2 a 9-bit count
/// + 20 zeros, n the length n − 2. Codes 0-255 are bytes, 256 on a match of code − 253 bytes; its position code p
/// gives the distance p + 1 for p &lt; 2, else 2^(p−1) + (p − 1 bits) + 1. Bits are read most significant first.
/// </summary>
internal sealed class LzhDecoder : Stream
{
    private const int LiteralSymbols = 510;
    private const int PretreeSymbols = 19;
    private const int MinMatch = 3;

    private readonly MsbBitReader _bits;
    private readonly byte[] _window;
    private readonly int _mask;
    private readonly int _positionSymbols;
    private readonly int _positionBits;
    private long _remaining;
    private int _windowPosition;
    private int _blockRemaining;
    private int _matchLength;
    private int _matchDistance;

    private HuffmanCode _pretree = HuffmanCode.Empty;
    private HuffmanCode _literals = HuffmanCode.Empty;
    private HuffmanCode _positions = HuffmanCode.Empty;

    /// <param name="dictionaryBits">Window size as a power of two (at least as far as a position code reaches).</param>
    /// <param name="positionSymbols">Symbols of the position tree (dictionary bits + 1).</param>
    /// <param name="positionBits">Width of the position tree's symbol count.</param>
    /// <param name="size">Bytes to produce.</param>
    /// <param name="fill">The window's content before the first byte (LHA starts with spaces).</param>
    public LzhDecoder(Stream input, int dictionaryBits, int positionSymbols, int positionBits, long size, byte fill)
    {
        _bits = new MsbBitReader(input);
        _window = new byte[1 << Math.Max(dictionaryBits, positionSymbols - 1)];
        _window.AsSpan().Fill(fill);
        _mask = _window.Length - 1;
        _positionSymbols = positionSymbols;
        _positionBits = positionBits;
        _remaining = size;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var count = (int)Math.Min(buffer.Length, _remaining);
        for (var i = 0; i < count; i++)
        {
            if (_matchLength == 0)
            {
                if (_blockRemaining == 0)
                    ReadBlockHeader();
                _blockRemaining--;
                var code = _literals.Decode(_bits);
                if (code < 256)
                {
                    buffer[i] = Put((byte)code);
                    continue;
                }
                _matchLength = code - 256 + MinMatch;
                var position = _positions.Decode(_bits);
                _matchDistance = (position < 2 ? position : (1 << (position - 1)) + (int)_bits.Read(position - 1)) + 1;
            }
            buffer[i] = Put(_window[(_windowPosition - _matchDistance) & _mask]);
            _matchLength--;
        }
        _remaining -= count;
        return count;
    }

    private byte Put(byte value)
    {
        _window[_windowPosition] = value;
        _windowPosition = (_windowPosition + 1) & _mask;
        return value;
    }

    private void ReadBlockHeader()
    {
        var codes = (int)_bits.Read(16);
        _blockRemaining = codes == 0 ? 65536 : codes;
        _pretree = ReadShortLengths(PretreeSymbols, 5, special: 3);
        ReadLiteralLengths();
        _positions = ReadShortLengths(_positionSymbols, _positionBits, special: -1);
    }

    /// <summary>The pretree or position tree: lengths of 3 bits, 7 and more in unary.</summary>
    private HuffmanCode ReadShortLengths(int symbols, int countBits, int special)
    {
        var count = (int)_bits.Read(countBits);
        if (count == 0)
            return HuffmanCode.Single(Symbol((int)_bits.Read(countBits), symbols));
        if (count > symbols)
            throw new InvalidDataException("An LZH code table is damaged.");
        var lengths = new byte[symbols];
        for (var i = 0; i < count;)
        {
            var length = (int)_bits.Read(3);
            if (length == 7)
            {
                while (_bits.Read(1) == 1)
                {
                    if (++length > 16)
                        throw new InvalidDataException("An LZH code is longer than 16 bits.");
                }
            }
            lengths[i++] = (byte)length;
            if (i == special)
            {
                // Lengths 3 to 3 + n are zero.
                for (var zeros = (int)_bits.Read(2); zeros > 0 && i < count; zeros--)
                    lengths[i++] = 0;
            }
        }
        return HuffmanCode.Build(lengths);
    }

    private void ReadLiteralLengths()
    {
        var count = (int)_bits.Read(9);
        if (count == 0)
        {
            _literals = HuffmanCode.Single(Symbol((int)_bits.Read(9), LiteralSymbols));
            return;
        }
        if (count > LiteralSymbols)
            throw new InvalidDataException("An LZH code table is damaged.");
        var lengths = new byte[LiteralSymbols];
        for (var i = 0; i < count;)
        {
            var code = _pretree.Decode(_bits);
            if (code > 2)
            {
                lengths[i++] = (byte)(code - 2);
                continue;
            }
            var zeros = code switch
            {
                0 => 1,
                1 => (int)_bits.Read(4) + 3,
                _ => (int)_bits.Read(9) + 20,
            };
            if (i + zeros > LiteralSymbols)
                throw new InvalidDataException("An LZH code table is damaged.");
            i += zeros; // already zero
        }
        _literals = HuffmanCode.Build(lengths);
    }

    private static int Symbol(int value, int symbols) =>
        value < symbols ? value : throw new InvalidDataException("An LZH code table is damaged.");

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

/// <summary>
/// Bits of a stream, most significant first (LHA, ARJ). Past the end it reads zeros for a few bytes, as decoders read
/// ahead; beyond that the data counts as cut short.
/// </summary>
internal sealed class MsbBitReader(Stream input)
{
    private const int MaxOverrun = 8;

    private readonly byte[] _buffer = new byte[1 << 14];
    private int _bufferPosition;
    private int _bufferLength;
    private ulong _bits;
    private int _count;
    private int _overrun;

    /// <summary>The next <paramref name="count"/> bits (up to 25) without using them.</summary>
    public uint Peek(int count)
    {
        while (_count < count)
        {
            _bits |= (ulong)NextByte() << (56 - _count);
            _count += 8;
        }
        return (uint)(_bits >> (64 - count));
    }

    public void Skip(int count)
    {
        _bits <<= count;
        _count -= count;
    }

    public uint Read(int count)
    {
        if (count == 0)
            return 0;
        var value = Peek(count);
        Skip(count);
        return value;
    }

    private byte NextByte()
    {
        if (_bufferPosition == _bufferLength)
        {
            _bufferLength = input.Read(_buffer);
            _bufferPosition = 0;
            if (_bufferLength == 0)
            {
                if (++_overrun > MaxOverrun)
                    throw new InvalidDataException("The compressed data ends early.");
                return 0;
            }
        }
        return _buffer[_bufferPosition++];
    }
}

/// <summary>A canonical Huffman code read most significant bit first, as a lookup table.</summary>
internal sealed class HuffmanCode
{
    public static readonly HuffmanCode Empty = new(0, [], -1);

    private readonly int _bits;
    private readonly int[] _entries; // symbol << 5 | length; 0 where no code starts
    private readonly int _single;

    private HuffmanCode(int bits, int[] entries, int single)
    {
        _bits = bits;
        _entries = entries;
        _single = single;
    }

    /// <summary>A code of one symbol that takes no bits.</summary>
    public static HuffmanCode Single(int symbol) => new(0, [], symbol);

    /// <summary>Builds the code from its lengths (0: unused, at most 16). Incomplete codes are allowed, oversubscribed ones not.</summary>
    public static HuffmanCode Build(ReadOnlySpan<byte> lengths)
    {
        var bits = 0;
        foreach (var length in lengths)
            bits = Math.Max(bits, length);
        if (bits == 0)
            return Empty;
        var entries = new int[1 << bits];
        var position = 0;
        for (var length = 1; length <= bits; length++)
        {
            var span = 1 << (bits - length);
            for (var symbol = 0; symbol < lengths.Length; symbol++)
            {
                if (lengths[symbol] != length)
                    continue;
                if (position + span > entries.Length)
                    throw new InvalidDataException("A Huffman table has more codes than its lengths allow.");
                entries.AsSpan(position, span).Fill((symbol << 5) | length);
                position += span;
            }
        }
        return new HuffmanCode(bits, entries, -1);
    }

    public int Decode(MsbBitReader reader)
    {
        if (_single >= 0)
            return _single;
        if (_bits == 0)
            throw new InvalidDataException("The compressed data uses a code of an empty table.");
        var entry = _entries[reader.Peek(_bits)];
        if (entry == 0)
            throw new InvalidDataException("The compressed data holds a code that is not in its table.");
        reader.Skip(entry & 0x1F);
        return entry >> 5;
    }
}
