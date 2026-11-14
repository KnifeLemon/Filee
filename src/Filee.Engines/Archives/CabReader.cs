// Reads Microsoft Cabinet files (.cab), written from [MS-CAB]; Filee's reader is its own. Little-endian structures:
//
//   CFHEADER  "MSCF", reserved u32, cabinet size u32, reserved u32, offset of the first CFFILE u32, reserved u32,
//             version minor u8 (3), major u8 (1), folder count u16, file count u16, flags u16 (1 previous cabinet,
//             2 next cabinet, 4 reserve fields present), set id u16, cabinet number u16; with flag 4 the reserve sizes
//             (header u16, per folder u8, per data block u8) and the header's reserved bytes; with flags 1 / 2 the
//             names of the previous / next cabinet and disk
//   CFFOLDER  offset of its first CFDATA u32, CFDATA count u16, compression u16 (low 4 bits: 0 none, 1 MSZIP,
//             2 Quantum, 3 LZX; bits 8-12 the LZX window as a power of two, 15 to 21), reserved bytes
//   CFFILE    size u32, offset in the folder's uncompressed data u32, folder index u16 (0xFFFD-0xFFFF: continued from or
//             into another cabinet), DOS date u16, DOS time u16, attributes u16 (0x80: the name is UTF-8), name ending
//             in a zero byte ("\" between folders)
//   CFDATA    checksum u32 (0: none), packed size u16, unpacked size u16 (at most 32 KB), reserved bytes, data
//
// A folder is one compressed stream cut into CFDATA blocks; its files lie end to end in the unpacked data. MSZIP blocks
// start with "CK" and hold deflate data (RFC 1951) whose matches may reach 32 KB back into the blocks before. LZX is
// decoded by the CHM reader's LzxDecoder, every block but the last unpacking to one 32 KB frame. Quantum and cabinets
// that span several files are left to 7-Zip. The checksum (an XOR of the data as 32-bit words, then of the size
// fields and the reserved bytes) is checked where present.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Filee.Engines.Ebooks;

namespace Filee.Engines.Archives;

/// <summary>Unpacks cabinet files (stored, MSZIP and LZX folders).</summary>
internal static class CabReader
{
    private const int BlockSize = 32768;
    private const int MaxNameBytes = 256;

    private const int MethodNone = 0;
    private const int MethodMszip = 1;
    private const int MethodQuantum = 2;
    private const int MethodLzx = 3;

    /// <summary>One CFFOLDER.</summary>
    private sealed record Folder(long Start, int Blocks, int Method, int WindowBits);

    /// <summary>One CFFILE; <see cref="Path"/> is set once its output file has been created.</summary>
    private sealed class Entry(string name, long size, long offset, int folder, DateTime? modified)
    {
        public string Name { get; } = name;
        public long Size { get; } = size;
        public long Offset { get; } = offset;
        public int Folder { get; } = folder;
        public DateTime? Modified { get; } = modified;
        public string Path { get; set; } = "";
        public long End => Offset + Size;
    }

    /// <summary>One CFDATA: where its data lies in the file, its sizes and checksum.</summary>
    private readonly record struct Block(long DataOffset, int Packed, int Unpacked, uint Checksum, long HeaderOffset);

    public static void Extract(string archive, string folder, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(archive);
        using var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var (folders, entries, dataReserve) = ReadCabinet(stream, name);
        ArchiveSafety.CheckListing([.. entries.Select(e => new ArchiveItem(e.Name, false, e.Size))], name, stream.Length, folder);

        // Methods and the block headers are checked before a file is written: what Filee can't decode goes to 7-Zip.
        var used = entries.Where(e => e.Size > 0).GroupBy(e => e.Folder).ToDictionary(g => g.Key, g => g.OrderBy(e => e.Offset).ToList());
        long walked = 0, work = 0;
        foreach (var (index, files) in used)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = folders[index];
            if (info.Method == MethodQuantum)
                throw new NotSupportedException("Quantum-compressed cabinets");
            if (info.Method > MethodLzx)
                throw new NotSupportedException($"the CAB compression type {info.Method}");
            // Folders may share data blocks only as far as there are bytes for their headers.
            walked += info.Blocks;
            if (walked * 8 > stream.Length)
                throw ArchiveSafety.Damaged(name, "CAB data blocks overlap");
            var blocks = Blocks(stream, info, dataReserve, name);
            // LZX frames are 32 KB and every block but the last holds one.
            if (info.Method == MethodLzx && blocks.SkipLast(1).Any(b => b.Unpacked != BlockSize))
                throw new NotSupportedException("LZX blocks of other than 32 KB");
            var size = blocks.Sum(b => (long)b.Unpacked);
            var end = files.Max(e => e.End);
            if (end > size)
                throw ArchiveSafety.Damaged(name, $"{files.First(e => e.End > size).Name} lies past the end of its folder's data");
            work += end;
        }
        // Folders are unpacked up to their last file: overlapping files or folders must not multiply that.
        ArchiveSafety.CheckRatio(work, stream.Length, name);

        // Output names in the cabinet's order, so clashing names are numbered as 7-Zip numbers them.
        var unique = new UniquePaths();
        foreach (var entry in entries)
        {
            var path = ArchiveSafety.ResolveEntryPath(folder, entry.Name, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.Path = unique.Get(path);
            new FileStream(entry.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None).Dispose();
            if (entry.Size == 0)
                SetTime(entry.Path, entry.Modified);
        }

        var budget = new UnpackBudget(name, stream.Length, folder);
        foreach (var (index, files) in used.OrderBy(pair => folders[pair.Key].Start))
        {
            using var sink = new FolderSink(files, budget, cancellationToken);
            Unpack(stream, folders[index], dataReserve, name, sink, files.Max(e => e.End), cancellationToken);
            sink.Finish();
        }
    }

    /// <summary>Reads the header, the folders and the files.</summary>
    private static (List<Folder> Folders, List<Entry> Entries, int DataReserve) ReadCabinet(FileStream stream, string archiveName)
    {
        try
        {
            var header = new byte[36];
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length || !header.AsSpan(0, 4).SequenceEqual("MSCF"u8))
                throw ArchiveSafety.NotAnArchive(archiveName);
            var filesOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
            if (header[25] != 1)
                throw new NotSupportedException($"CAB version {header[25]}.{header[24]}");
            int folderCount = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26));
            int fileCount = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30));
            if ((flags & 3) != 0)
                throw new NotSupportedException("cabinets that span several files");

            int folderReserve = 0, dataReserve = 0;
            if ((flags & 4) != 0)
            {
                Span<byte> reserve = stackalloc byte[4];
                stream.ReadExactly(reserve);
                var headerReserve = BinaryPrimitives.ReadUInt16LittleEndian(reserve);
                folderReserve = reserve[2];
                dataReserve = reserve[3];
                stream.Seek(headerReserve, SeekOrigin.Current);
            }

            var folders = new List<Folder>(folderCount);
            var record = new byte[8 + folderReserve];
            for (var i = 0; i < folderCount; i++)
            {
                stream.ReadExactly(record);
                var type = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(6));
                var method = type & 0x0F;
                var window = (type >> 8) & 0x1F;
                if (method == MethodLzx && window is < 15 or > 21)
                    throw ArchiveSafety.Damaged(archiveName, $"invalid LZX window size 2^{window}");
                folders.Add(new Folder(BinaryPrimitives.ReadUInt32LittleEndian(record), BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4)), method, window));
            }

            if (filesOffset >= stream.Length)
                throw ArchiveSafety.Damaged(archiveName, "the archive ends too early");
            stream.Position = filesOffset;
            var entries = new List<Entry>(fileCount);
            var fields = new byte[16];
            var nameBytes = new byte[MaxNameBytes + 1];
            for (var i = 0; i < fileCount; i++)
            {
                stream.ReadExactly(fields);
                var length = 0;
                while (true)
                {
                    var b = stream.ReadByte();
                    if (b < 0)
                        throw new EndOfStreamException();
                    if (b == 0)
                        break;
                    if (length == MaxNameBytes)
                        throw ArchiveSafety.Damaged(archiveName, "a CAB file name is too long");
                    nameBytes[length++] = (byte)b;
                }
                int folderIndex = BinaryPrimitives.ReadUInt16LittleEndian(fields.AsSpan(8));
                if (folderIndex >= 0xFFFD)
                    throw new NotSupportedException("cabinets that span several files");
                if (folderIndex >= folderCount)
                    throw ArchiveSafety.Damaged(archiveName, "a CAB file names a folder that does not exist");
                var date = BinaryPrimitives.ReadUInt16LittleEndian(fields.AsSpan(10));
                var time = BinaryPrimitives.ReadUInt16LittleEndian(fields.AsSpan(12));
                var attributes = BinaryPrimitives.ReadUInt16LittleEndian(fields.AsSpan(14));
                var bytes = nameBytes.AsSpan(0, length);
                var entryName = (attributes & 0x80) != 0 ? Encoding.UTF8.GetString(bytes) : DecodeName(bytes);
                if (entryName.Trim('\\', '/').Length == 0)
                    throw ArchiveSafety.Damaged(archiveName, "an entry has no name");
                entries.Add(new Entry(entryName, BinaryPrimitives.ReadUInt32LittleEndian(fields), BinaryPrimitives.ReadUInt32LittleEndian(fields.AsSpan(4)),
                    folderIndex, AlzReader.FromDosTime((uint)date << 16 | time)));
            }
            return (folders, entries, dataReserve);
        }
        catch (EndOfStreamException)
        {
            throw ArchiveSafety.Damaged(archiveName, "the archive ends too early");
        }
    }

    /// <summary>The CFDATA blocks of a folder, read from their headers.</summary>
    private static List<Block> Blocks(FileStream stream, Folder folder, int dataReserve, string archiveName)
    {
        var blocks = new List<Block>(folder.Blocks);
        var position = folder.Start;
        Span<byte> header = stackalloc byte[8];
        for (var i = 0; i < folder.Blocks; i++)
        {
            stream.Position = position;
            if (stream.ReadAtLeast(header, 8, throwOnEndOfStream: false) < 8)
                throw ArchiveSafety.Damaged(archiveName, "the archive ends too early");
            int packed = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
            int unpacked = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
            if (unpacked is 0 or > BlockSize || folder.Method == MethodNone && packed != unpacked)
                throw ArchiveSafety.Damaged(archiveName, "invalid CAB data block");
            var data = position + 8 + dataReserve;
            if (data + packed > stream.Length)
                throw ArchiveSafety.Damaged(archiveName, "the archive ends too early");
            blocks.Add(new Block(data, packed, unpacked, BinaryPrimitives.ReadUInt32LittleEndian(header), position));
            position = data + packed;
        }
        return blocks;
    }

    /// <summary>Unpacks a folder's data into <paramref name="sink"/> up to <paramref name="end"/> bytes.</summary>
    private static void Unpack(FileStream stream, Folder folder, int dataReserve, string archiveName, FolderSink sink, long end,
        CancellationToken cancellationToken)
    {
        var blocks = Blocks(stream, folder, dataReserve, archiveName);
        var data = new byte[ushort.MaxValue + 8 + dataReserve];
        if (folder.Method == MethodLzx)
        {
            long packed = 0, unpacked = 0;
            foreach (var block in blocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadBlock(stream, block, dataReserve, data, archiveName); // the checksums
                packed += block.Packed;
                unpacked += block.Unpacked;
            }
            // Whole frames: the Intel E8 translation of a frame looks at all of it.
            var length = Math.Min(unpacked, (end + BlockSize - 1) / BlockSize * BlockSize);
            var input = new LzxInput(new BlockStream(stream, blocks, packed), 0, packed);
            new LzxDecoder(input, folder.WindowBits, 0).Decompress(sink, length, cancellationToken);
            return;
        }

        var inflater = folder.Method == MethodMszip ? new MszipInflater() : null;
        foreach (var block in blocks)
        {
            if (sink.Position >= end)
                break;
            cancellationToken.ThrowIfCancellationRequested();
            var payload = ReadBlock(stream, block, dataReserve, data, archiveName);
            sink.Write(inflater is null ? payload : inflater.Inflate(payload, block.Unpacked));
        }
    }

    /// <summary>Reads one block's data and checks its checksum.</summary>
    private static ReadOnlySpan<byte> ReadBlock(FileStream stream, Block block, int dataReserve, byte[] buffer, string archiveName)
    {
        stream.Position = block.HeaderOffset + 4;
        var all = buffer.AsSpan(0, 4 + dataReserve + block.Packed);
        stream.ReadExactly(all);
        var payload = all[(4 + dataReserve)..];
        if (block.Checksum != 0)
        {
            var sum = Checksum(payload, 0);
            // The checksum covers the reserved bytes too ([MS-CAB]; 7-Zip checks it so). Writers that left them out are
            // accepted as well: the reserved bytes are zeros in practice, so both sums agree.
            if (Checksum(all[..4], sum) != block.Checksum && Checksum(all[..(4 + dataReserve)], sum) != block.Checksum)
                throw ArchiveSafety.Damaged(archiveName, "CAB checksum error");
        }
        return payload;
    }

    /// <summary>The CFDATA checksum: 32-bit little-endian words XORed together, the 1-3 bytes left over as one more.</summary>
    internal static uint Checksum(ReadOnlySpan<byte> data, uint seed)
    {
        var sum = seed;
        var words = data.Length / 4;
        for (var i = 0; i < words; i++)
            sum ^= BinaryPrimitives.ReadUInt32LittleEndian(data[(4 * i)..]);
        uint last = 0;
        foreach (var b in data[(4 * words)..])
            last = (last << 8) | b;
        return sum ^ last;
    }

    /// <summary>A name without the UTF-8 flag: UTF-8 when it is valid UTF-8, else the Windows code page.</summary>
    internal static string DecodeName(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var codePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
            try
            {
                return Encoding.GetEncoding(codePage is > 0 and not 65001 ? codePage : 1252).GetString(bytes);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return Encoding.Latin1.GetString(bytes);
            }
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    private static void SetTime(string path, DateTime? modified)
    {
        if (modified is not { } time)
            return;
        try
        {
            File.SetLastWriteTime(path, time);
        }
        catch (ArgumentOutOfRangeException) { }
    }

    /// <summary>
    /// Takes a folder's unpacked data in order and writes each file's part of it to that file (files may overlap; real
    /// cabinets lay them end to end).
    /// </summary>
    private sealed class FolderSink(List<Entry> files, UnpackBudget budget, CancellationToken cancellationToken) : Stream
    {
        /// <summary>Files written at the same time: only overlapping entries need more than one.</summary>
        private const int MaxOpen = 64;

        private readonly List<(Entry Entry, FileStream Stream)> _open = [];
        private int _next;
        private long _position;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = _position;
            var end = start + buffer.Length;
            while (_next < files.Count && files[_next].Offset < end)
            {
                if (_open.Count == MaxOpen)
                    throw ArchiveSafety.Damaged(budget.ArchiveName, "too many CAB files overlap");
                var entry = files[_next++];
                _open.Add((entry, new FileStream(entry.Path, FileMode.Truncate, FileAccess.Write, FileShare.None, 1 << 16)));
            }
            for (var i = _open.Count - 1; i >= 0; i--)
            {
                var (entry, file) = _open[i];
                var from = Math.Max(start, entry.Offset);
                var to = Math.Min(end, entry.End);
                if (to > from)
                {
                    budget.Add(to - from);
                    file.Write(buffer[(int)(from - start)..(int)(to - start)]);
                }
                if (entry.End <= end)
                {
                    file.Dispose();
                    SetTime(entry.Path, entry.Modified);
                    _open.RemoveAt(i);
                }
            }
            _position = end;
        }

        /// <summary>Throws when the data ended before every file was complete.</summary>
        public void Finish()
        {
            if (_next < files.Count || _open.Count > 0)
                throw ArchiveSafety.Damaged(budget.ArchiveName, "the CAB data ends early");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var (_, file) in _open)
                    file.Dispose();
                _open.Clear();
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>The data of a folder's blocks as one seekable stream, for the LZX decoder.</summary>
    private sealed class BlockStream(FileStream file, List<Block> blocks, long length) : Stream
    {
        private readonly long[] _starts = Starts(blocks);
        private long _position;

        private static long[] Starts(List<Block> blocks)
        {
            var starts = new long[blocks.Count];
            long total = 0;
            for (var i = 0; i < blocks.Count; i++)
            {
                starts[i] = total;
                total += blocks[i].Packed;
            }
            return starts;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= length || buffer.Length == 0)
                return 0;
            var index = Array.BinarySearch(_starts, _position);
            if (index < 0)
                index = ~index - 1;
            while (blocks[index].Packed == 0)
                index++; // empty blocks share their start with the next one
            var within = _position - _starts[index];
            var count = (int)Math.Min(buffer.Length, blocks[index].Packed - within);
            file.Position = blocks[index].DataOffset + within;
            file.ReadExactly(buffer[..count]);
            _position += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => length + offset,
        };

        public override long Position
        {
            get => _position;
            set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// Inflates the MSZIP blocks of one CAB folder: each is "CK" and a deflate stream (RFC 1951) ending with a final block,
/// whose matches may reach up to 32 KB back, into the blocks before.
/// </summary>
internal sealed class MszipInflater
{
    private const int History = 32768;

    private static readonly int[] LengthBase =
        [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];

    private static readonly int[] LengthExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];

    private static readonly int[] DistanceBase =
    [
        1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145,
        8193, 12289, 16385, 24577,
    ];

    private static readonly int[] DistanceExtra = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];

    /// <summary>The order in which the code length code lengths are sent.</summary>
    private static readonly int[] CodeLengthOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

    private static readonly LsbHuffman FixedLiterals = LsbHuffman.Build([.. Enumerable.Range(0, 288).Select(i => (byte)(i < 144 ? 8 : i < 256 ? 9 : i < 280 ? 7 : 8))]);
    private static readonly LsbHuffman FixedDistances = LsbHuffman.Build([.. Enumerable.Repeat((byte)5, 30)]);

    /// <summary>The last 32 KB unpacked at [History − _available, History), then the current block's output.</summary>
    private readonly byte[] _buffer = new byte[2 * History];
    private int _available;

    private byte[] _input = [];
    private int _inputLength;
    private int _inputPosition;
    private ulong _bits;
    private int _bitCount;
    private int _phantom; // bits of zeros added past the end of the input

    /// <summary>Inflates one block's data to the <paramref name="expected"/> bytes it declares.</summary>
    public ReadOnlySpan<byte> Inflate(ReadOnlySpan<byte> data, int expected)
    {
        if (data.Length < 2 || data[0] != 'C' || data[1] != 'K')
            throw new InvalidDataException("An MSZIP block lacks its CK signature.");
        if (_input.Length < data.Length)
            _input = new byte[Math.Max(data.Length, 1 << 16)];
        data[2..].CopyTo(_input);
        _inputLength = data.Length - 2;
        _inputPosition = 0;
        _bits = 0;
        _bitCount = 0;
        _phantom = 0;

        var limit = History + expected;
        var position = History;
        bool last;
        do
        {
            last = Read(1) == 1;
            switch (Read(2))
            {
                case 0:
                    position = Stored(position, limit);
                    break;
                case 1:
                    position = Codes(position, limit, FixedLiterals, FixedDistances);
                    break;
                case 2:
                    {
                        var (literals, distances) = DynamicTables();
                        position = Codes(position, limit, literals, distances);
                        break;
                    }
                default:
                    throw new InvalidDataException("An MSZIP block has an invalid deflate block type.");
            }
        }
        while (!last && position < limit);
        if (position != limit)
            throw new InvalidDataException("An MSZIP block unpacks to less than it declares.");

        // The last 32 KB up to the end of this block become the history; the block's output ends where it starts.
        Array.Copy(_buffer, expected, _buffer, 0, History);
        _available = Math.Min(History, _available + expected);
        return _buffer.AsSpan(History - expected, expected);
    }

    private int Stored(int position, int limit)
    {
        Skip(_bitCount & 7);
        var length = (int)Read(16);
        var inverse = (int)Read(16);
        if ((length ^ 0xFFFF) != inverse)
            throw new InvalidDataException("An MSZIP stored block is damaged.");
        if (position + length > limit)
            throw new InvalidDataException("An MSZIP block unpacks to more than it declares.");
        for (var i = 0; i < length; i++)
            _buffer[position++] = (byte)Read(8);
        return position;
    }

    private int Codes(int position, int limit, LsbHuffman literals, LsbHuffman distances)
    {
        while (true)
        {
            var symbol = Decode(literals);
            if (symbol < 256)
            {
                if (position == limit)
                    throw new InvalidDataException("An MSZIP block unpacks to more than it declares.");
                _buffer[position++] = (byte)symbol;
                continue;
            }
            if (symbol == 256)
                return position;
            symbol -= 257;
            if (symbol >= LengthBase.Length)
                throw new InvalidDataException("An MSZIP block holds an invalid length code.");
            var length = LengthBase[symbol] + (int)Read(LengthExtra[symbol]);
            var code = Decode(distances);
            if (code >= DistanceBase.Length)
                throw new InvalidDataException("An MSZIP block holds an invalid distance code.");
            var distance = DistanceBase[code] + (int)Read(DistanceExtra[code]);
            if (distance > position - History + _available)
                throw new InvalidDataException("An MSZIP match points before the start of the data.");
            if (position + length > limit)
                throw new InvalidDataException("An MSZIP block unpacks to more than it declares.");
            for (var i = 0; i < length; i++, position++)
                _buffer[position] = _buffer[position - distance];
        }
    }

    private (LsbHuffman Literals, LsbHuffman Distances) DynamicTables()
    {
        var literalCount = (int)Read(5) + 257;
        var distanceCount = (int)Read(5) + 1;
        var codeLengthCount = (int)Read(4) + 4;
        if (literalCount > 286 || distanceCount > 30)
            throw new InvalidDataException("An MSZIP block has too many codes.");
        var codeLengths = new byte[19];
        for (var i = 0; i < codeLengthCount; i++)
            codeLengths[CodeLengthOrder[i]] = (byte)Read(3);
        var lengthCode = LsbHuffman.Build(codeLengths);

        var lengths = new byte[literalCount + distanceCount];
        for (var i = 0; i < lengths.Length;)
        {
            var symbol = Decode(lengthCode);
            if (symbol < 16)
            {
                lengths[i++] = (byte)symbol;
                continue;
            }
            byte value = 0;
            int repeat;
            if (symbol == 16)
            {
                if (i == 0)
                    throw new InvalidDataException("An MSZIP code table repeats a length before the first.");
                value = lengths[i - 1];
                repeat = 3 + (int)Read(2);
            }
            else
            {
                repeat = symbol == 17 ? 3 + (int)Read(3) : 11 + (int)Read(7);
            }
            if (i + repeat > lengths.Length)
                throw new InvalidDataException("An MSZIP code table is damaged.");
            lengths.AsSpan(i, repeat).Fill(value);
            i += repeat;
        }
        return (LsbHuffman.Build(lengths.AsSpan(0, literalCount)), LsbHuffman.Build(lengths.AsSpan(literalCount)));
    }

    private int Decode(LsbHuffman code)
    {
        if (code.Bits == 0)
            throw new InvalidDataException("An MSZIP block uses a code of an empty table.");
        Need(code.Bits);
        var entry = code.Entries[(int)(_bits & ((1UL << code.Bits) - 1))];
        if (entry == 0)
            throw new InvalidDataException("An MSZIP block holds a code that is not in its table.");
        Skip(entry & 0x1F);
        return entry >> 5;
    }

    private uint Read(int count)
    {
        if (count == 0)
            return 0;
        Need(count);
        var value = (uint)(_bits & ((1UL << count) - 1));
        Skip(count);
        return value;
    }

    private void Need(int count)
    {
        while (_bitCount < count)
        {
            // Past the end come zeros, so a table lookup near the end works; using them is an error (Skip).
            ulong next = 0;
            if (_inputPosition < _inputLength)
                next = _input[_inputPosition++];
            else
                _phantom += 8;
            _bits |= next << _bitCount;
            _bitCount += 8;
        }
    }

    private void Skip(int count)
    {
        _bits >>= count;
        _bitCount -= count;
        if (_bitCount < _phantom)
            throw new InvalidDataException("An MSZIP block ends early.");
    }

    /// <summary>A canonical Huffman code read least significant bit first (deflate), as a lookup table.</summary>
    private sealed class LsbHuffman
    {
        private LsbHuffman(int bits, int[] entries)
        {
            Bits = bits;
            Entries = entries;
        }

        public int Bits { get; }

        /// <summary>Symbol &lt;&lt; 5 | length by the next <see cref="Bits"/> bits; 0 where no code starts.</summary>
        public int[] Entries { get; }

        public static LsbHuffman Build(ReadOnlySpan<byte> lengths)
        {
            Span<int> counts = stackalloc int[16];
            var bits = 0;
            foreach (var length in lengths)
            {
                counts[length]++;
                bits = Math.Max(bits, length);
            }
            if (bits == 0)
                return new LsbHuffman(0, []);
            counts[0] = 0;
            Span<int> next = stackalloc int[16];
            var code = 0;
            var room = 1;
            for (var length = 1; length <= bits; length++)
            {
                room = (room << 1) - counts[length];
                if (room < 0)
                    throw new InvalidDataException("A deflate code table has more codes than its lengths allow.");
                code = (code + counts[length - 1]) << 1;
                next[length] = code;
            }
            var entries = new int[1 << bits];
            for (var symbol = 0; symbol < lengths.Length; symbol++)
            {
                int length = lengths[symbol];
                if (length == 0)
                    continue;
                var reversed = Reverse(next[length]++, length);
                for (var i = reversed; i < entries.Length; i += 1 << length)
                    entries[i] = (symbol << 5) | length;
            }
            return new LsbHuffman(bits, entries);
        }

        private static int Reverse(int code, int length)
        {
            var result = 0;
            for (var i = 0; i < length; i++, code >>= 1)
                result = (result << 1) | (code & 1);
            return result;
        }
    }
}
