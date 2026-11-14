// Writes .7z archives (7zFormat.txt from the LZMA SDK) with Filee's own LZMA2 encoder (Archives/Lzma), without 7-Zip.
//
// Layout: the 32-byte signature header (signature, version 0.4, the CRC of the start header, and the start header:
// offset, size and CRC of the header at the end), then the packed streams, then the header. Files are written as they
// come into solid blocks: one folder per block with a single LZMA2 coder (the Copy coder at the store level, one file
// per folder there, as 7-Zip does), a new block when the next file would take the current one past the level's block
// size. The header lists the pack sizes, the folders with their coder and unpacked size, the files of each folder
// with their CRC-32 (substreams), and the files info: UTF-16 names with "/" between folders, which entries have no
// data (empty files and folders) and which of those are files, modification times (FILETIME, UTC) and attributes
// (Windows attributes, plus the Unix mode in the high 16 bits with the 0x8000 flag, as 7-Zip writes on Linux).
// The header itself is LZMA-compressed into one more packed stream and described by a short "encoded header"
// (kept plain when that would not be smaller). No encryption. The output must be seekable: the signature header is
// filled in last.

using System.Buffers.Binary;
using Filee.Core.Presets;
using Filee.Engines.Archives.Lzma;

namespace Filee.Engines.Archives;

/// <summary>Writes a 7z archive entry by entry.</summary>
internal sealed class SevenZipWriter
{
    private const int SignatureHeaderSize = 32;

    // Property ids of 7zFormat.txt.
    private const byte IdEnd = 0x00;
    private const byte IdHeader = 0x01;
    private const byte IdMainStreamsInfo = 0x04;
    private const byte IdFilesInfo = 0x05;
    private const byte IdPackInfo = 0x06;
    private const byte IdUnpackInfo = 0x07;
    private const byte IdSubStreamsInfo = 0x08;
    private const byte IdSize = 0x09;
    private const byte IdCrc = 0x0A;
    private const byte IdFolder = 0x0B;
    private const byte IdCodersUnpackSize = 0x0C;
    private const byte IdNumUnpackStream = 0x0D;
    private const byte IdEmptyStream = 0x0E;
    private const byte IdEmptyFile = 0x0F;
    private const byte IdName = 0x11;
    private const byte IdMTime = 0x14;
    private const byte IdWinAttributes = 0x15;
    private const byte IdEncodedHeader = 0x17;

    /// <summary>FILE_ATTRIBUTE_UNIX_EXTENSION: the high 16 bits of the attributes hold a Unix mode.</summary>
    public const uint UnixExtension = 0x8000;

    private const int LookAhead = LzmaModel.MatchMaxLength + 4;

    private readonly Stream _output;
    private readonly long _start;
    private readonly ArchiveLevel _level;
    private readonly long _blockLimit;
    private readonly CancellationToken _cancellationToken;
    private readonly List<Entry> _entries = [];
    private readonly List<Block> _blocks = [];
    private readonly byte[] _buffer = new byte[1 << 16];
    private Block? _block;
    private long _expectedLeft;
    private bool _finished;

    /// <param name="expectedTotal">
    /// The bytes all files will add up to, when known (-1 otherwise): a block that will stay small gets a dictionary no
    /// larger than it, which saves memory for the writer and for whoever unpacks the archive.
    /// </param>
    /// <param name="blockLimit">The solid block size; <see cref="SolidBlockSize"/> of the level unless a test sets one.</param>
    public SevenZipWriter(Stream output, ArchiveLevel level, long expectedTotal = -1, CancellationToken cancellationToken = default,
        long? blockLimit = null)
    {
        if (!output.CanSeek)
            throw new ArgumentException("A 7z archive is written to a seekable stream.", nameof(output));
        _output = output;
        _start = output.Position;
        _level = level;
        _blockLimit = blockLimit ?? SolidBlockSize(level);
        _expectedLeft = expectedTotal;
        _cancellationToken = cancellationToken;
        _output.Write(new byte[SignatureHeaderSize]);
    }

    /// <summary>
    /// The most data one solid block takes at <paramref name="level"/>: 16 × the LZMA2 dictionary (64 MiB fast,
    /// 256 MiB normal, 1 GiB maximum). Beyond the dictionary a new block costs only a little ratio, and a reader that
    /// wants one file decodes at most this much before it. The store level puts every file into its own folder.
    /// </summary>
    public static long SolidBlockSize(ArchiveLevel level) =>
        level == ArchiveLevel.Store ? 0 : 16L * Lzma2Settings.For(level).Dictionary;

    /// <summary>Entries added so far.</summary>
    public int Count => _entries.Count;

    /// <summary>Solid blocks (folders) written so far, the open one included.</summary>
    internal int BlockCount => _blocks.Count + (_block is null ? 0 : 1);

    /// <summary>Adds a folder.</summary>
    /// <param name="name">The path inside the archive, with "/" between folders.</param>
    public void AddFolder(string name, DateTime? modifiedUtc, uint attributes)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _cancellationToken.ThrowIfCancellationRequested();
        _entries.Add(new Entry(CheckName(name), IsFolder: true, Size: 0, Crc: 0, modifiedUtc, attributes));
    }

    /// <summary>Adds a file with everything <paramref name="data"/> holds from its position on.</summary>
    /// <param name="expectedLength">Its length if known (-1 otherwise), to start a new block before it when it wouldn't fit.</param>
    /// <param name="onRead">Called with the bytes read, for progress.</param>
    public void AddFile(string name, Stream data, long expectedLength, DateTime? modifiedUtc, uint attributes, Action<long>? onRead = null)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _cancellationToken.ThrowIfCancellationRequested();
        name = CheckName(name);
        var crc = new Crc32();
        long size = 0;
        var read = data.Read(_buffer);
        if (read > 0)
        {
            if (_block is { Unpacked: > 0 } full && full.Unpacked + Math.Max(expectedLength, read) > _blockLimit)
                CloseBlock();
            _block ??= OpenBlock(Math.Max(expectedLength, read));
            do
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var chunk = _buffer.AsSpan(0, read);
                crc.Append(chunk);
                _block.Write(chunk);
                size += read;
                onRead?.Invoke(read);
                read = data.Read(_buffer);
            } while (read > 0);
            _block.Streams++;
        }
        if (_expectedLeft >= 0)
            _expectedLeft = Math.Max(0, _expectedLeft - Math.Max(expectedLength, size));
        _entries.Add(new Entry(name, IsFolder: false, size, crc.Value, modifiedUtc, attributes));
    }

    private Block OpenBlock(long first)
    {
        // The data this block will most likely get: what is still expected, at least this file, at most a block.
        var expected = _expectedLeft < 0 ? -1 : Math.Min(Math.Max(_expectedLeft, first), Math.Max(_blockLimit, first));
        if (_level == ArchiveLevel.Store)
            return new Block(_output, null);
        var settings = Lzma2Settings.For(_level, expected);
        return new Block(_output, new Lzma2Writer(_output, settings, expected, _cancellationToken)) { Dictionary = settings.Dictionary };
    }

    private void CloseBlock()
    {
        if (_block is null)
            return;
        _block.Finish();
        _blocks.Add(_block);
        _block = null;
    }

    /// <summary>Writes the header and fills in the signature header; the archive is complete after this.</summary>
    public void Finish()
    {
        if (_finished)
            return;
        _cancellationToken.ThrowIfCancellationRequested();
        CloseBlock();
        _finished = true;

        byte[] next = [];
        if (_entries.Count > 0)
        {
            var header = WriteHeader();
            var packPosition = _output.Position - _start - SignatureHeaderSize;
            next = header;
            if (CompressLzma(header, out var dictionary) is { } packed)
            {
                var encoded = WriteEncodedHeader(packPosition, packed.Length, dictionary, header.Length, Crc32.Compute(header));
                if (packed.Length + encoded.Length < header.Length)
                {
                    _output.Write(packed);
                    next = encoded;
                }
            }
        }
        var nextOffset = next.Length == 0 ? 0 : _output.Position - _start - SignatureHeaderSize;
        _output.Write(next);
        var end = _output.Position;

        // An archive without entries is the bare signature header with an empty start header, as 7-Zip writes it.
        Span<byte> signature = stackalloc byte[SignatureHeaderSize];
        signature.Clear();
        "7z"u8.CopyTo(signature);
        signature[2] = 0xBC;
        signature[3] = 0xAF;
        signature[4] = 0x27;
        signature[5] = 0x1C;
        signature[6] = 0; // version 0.4
        signature[7] = 4;
        BinaryPrimitives.WriteUInt64LittleEndian(signature[12..], (ulong)nextOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(signature[20..], (ulong)next.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(signature[28..], next.Length == 0 ? 0 : Crc32.Compute(next));
        BinaryPrimitives.WriteUInt32LittleEndian(signature[8..], Crc32.Compute(signature[12..]));
        _output.Position = _start;
        _output.Write(signature);
        _output.Position = end;
        _output.Flush();
    }

    private byte[] WriteHeader()
    {
        var w = new HeaderWriter();
        w.Byte(IdHeader);
        if (_blocks.Count > 0)
        {
            w.Byte(IdMainStreamsInfo);
            w.Byte(IdPackInfo);
            w.Number(0);
            w.Number((ulong)_blocks.Count);
            w.Byte(IdSize);
            foreach (var block in _blocks)
                w.Number((ulong)block.Packed);
            w.Byte(IdEnd);

            w.Byte(IdUnpackInfo);
            w.Byte(IdFolder);
            w.Number((ulong)_blocks.Count);
            w.Byte(0); // not external
            foreach (var block in _blocks)
                WriteFolder(w, block.Lzma2 ? XzFormat.PropertyFor((uint)block.Dictionary) : null);
            w.Byte(IdCodersUnpackSize);
            foreach (var block in _blocks)
                w.Number((ulong)block.Unpacked);
            w.Byte(IdEnd);

            // Substreams: the files of every folder, the sizes of all but each folder's last, and every file's CRC.
            var withData = _entries.Where(e => e.Size > 0).ToList();
            w.Byte(IdSubStreamsInfo);
            if (_blocks.Any(b => b.Streams != 1))
            {
                w.Byte(IdNumUnpackStream);
                foreach (var block in _blocks)
                    w.Number((ulong)block.Streams);
            }
            if (_blocks.Any(b => b.Streams > 1))
            {
                w.Byte(IdSize);
                var at = 0;
                foreach (var block in _blocks)
                {
                    for (var i = 0; i < block.Streams - 1; i++)
                        w.Number((ulong)withData[at + i].Size);
                    at += block.Streams;
                }
            }
            w.Byte(IdCrc);
            w.Byte(1); // all defined
            foreach (var entry in withData)
                w.UInt32(entry.Crc);
            w.Byte(IdEnd);
            w.Byte(IdEnd);
        }

        w.Byte(IdFilesInfo);
        w.Number((ulong)_entries.Count);
        var empty = _entries.Where(e => e.Size == 0).ToList();
        if (empty.Count > 0)
        {
            w.Property(IdEmptyStream, Bits(_entries.Select(e => e.Size == 0).ToList()));
            if (empty.Any(e => !e.IsFolder))
                w.Property(IdEmptyFile, Bits(empty.Select(e => !e.IsFolder).ToList()));
        }

        var names = new HeaderWriter();
        names.Byte(0); // not external
        foreach (var entry in _entries)
        {
            foreach (var c in entry.Name)
                names.UInt16(c);
            names.UInt16(0);
        }
        w.Property(IdName, names.ToArray());

        var times = _entries.Select(e => e.Modified is { Year: >= 1601 } t ? t.ToFileTimeUtc() : (long?)null).ToList();
        if (times.Any(t => t is not null))
        {
            var property = new HeaderWriter();
            WriteDefined(property, times.Select(t => t is not null).ToList());
            property.Byte(0); // not external
            foreach (var time in times)
                if (time is { } value)
                    property.UInt64((ulong)value);
            w.Property(IdMTime, property.ToArray());
        }

        var attributes = new HeaderWriter();
        attributes.Byte(1); // all defined
        attributes.Byte(0); // not external
        foreach (var entry in _entries)
            attributes.UInt32(entry.Attributes);
        w.Property(IdWinAttributes, attributes.ToArray());
        w.Byte(IdEnd);

        w.Byte(IdEnd);
        return w.ToArray();
    }

    /// <summary>A folder of one coder: LZMA2 with its dictionary property, or Copy when <paramref name="lzma2Property"/> is null.</summary>
    private static void WriteFolder(HeaderWriter w, byte? lzma2Property)
    {
        w.Number(1); // coders
        if (lzma2Property is { } property)
        {
            w.Byte(0x21); // id size 1, has properties
            w.Byte((byte)XzFormat.Lzma2FilterId);
            w.Number(1);
            w.Byte(property);
        }
        else
        {
            w.Byte(0x01); // id size 1, no properties
            w.Byte(0x00); // Copy
        }
    }

    /// <summary>The header's own streams info: one packed stream, one LZMA folder, its size and CRC.</summary>
    private static byte[] WriteEncodedHeader(long packPosition, long packedSize, int dictionary, long unpackedSize, uint crc)
    {
        var w = new HeaderWriter();
        w.Byte(IdEncodedHeader);
        w.Byte(IdPackInfo);
        w.Number((ulong)packPosition);
        w.Number(1);
        w.Byte(IdSize);
        w.Number((ulong)packedSize);
        w.Byte(IdEnd);

        w.Byte(IdUnpackInfo);
        w.Byte(IdFolder);
        w.Number(1);
        w.Byte(0); // not external
        w.Number(1); // coders
        w.Byte(0x23); // id size 3, has properties
        w.Byte(0x03); // LZMA: 03 01 01
        w.Byte(0x01);
        w.Byte(0x01);
        w.Number(5);
        w.Byte(LzmaProperties.Default.ToByte());
        w.UInt32((uint)dictionary);
        w.Byte(IdCodersUnpackSize);
        w.Number((ulong)unpackedSize);
        w.Byte(IdCrc);
        w.Byte(1); // all defined
        w.UInt32(crc);
        w.Byte(IdEnd);
        w.Byte(IdEnd);
        return w.ToArray();
    }

    /// <summary>
    /// <paramref name="data"/> as one raw LZMA stream (no end marker: the folder gives its size), or null when it does
    /// not come out smaller.
    /// </summary>
    internal static byte[]? CompressLzma(byte[] data, out int dictionary)
    {
        var settings = Lzma2Settings.For(ArchiveLevel.Normal, data.Length);
        dictionary = settings.Dictionary;
        var ahead = Math.Min(Math.Max(1 << 21, settings.Dictionary / 4), data.Length + LookAhead);
        var mf = new MatchFinder(settings.Dictionary, ahead, settings.Encoder.Depth, settings.Encoder.Nice, settings.Encoder.Tree);
        var encoder = new LzmaEncoder(mf, LzmaProperties.Default, settings.Encoder);
        var rc = new RangeEncoder(data.Length + 4096);
        ReadOnlySpan<byte> rest = data;
        while (true)
        {
            rest = rest[mf.Append(rest)..];
            var final = rest.IsEmpty;
            var limit = final ? mf.Filled : mf.Filled - LookAhead;
            encoder.Encode(rc, limit, data.Length);
            if (mf.Position < limit)
                return null;
            if (final)
                break;
            encoder.Shifted(mf.Slide());
        }
        rc.Flush();
        return rc.Output.Length < data.Length ? rc.Output.ToArray() : null;
    }

    /// <summary>The bit vector of 7zFormat.txt: the first item in the high bit of the first byte.</summary>
    private static byte[] Bits(IReadOnlyList<bool> values)
    {
        var bytes = new byte[(values.Count + 7) / 8];
        for (var i = 0; i < values.Count; i++)
            if (values[i])
                bytes[i / 8] |= (byte)(0x80 >> (i % 8));
        return bytes;
    }

    /// <summary>"All defined" (1), or 0 and the bit vector.</summary>
    private static void WriteDefined(HeaderWriter w, IReadOnlyList<bool> defined)
    {
        if (defined.All(d => d))
        {
            w.Byte(1);
            return;
        }
        w.Byte(0);
        w.Bytes(Bits(defined));
    }

    /// <summary>The name without empty parts (doubled, leading or trailing "/"); the packer has turned "\" into "/" already.</summary>
    private static string CheckName(string name)
    {
        var normal = string.Join('/', name.Split('/', StringSplitOptions.RemoveEmptyEntries));
        if (normal.Length == 0 || normal.Contains('\0'))
            throw new ArgumentException($"\"{name}\" is not a name for an archive entry.", nameof(name));
        return normal;
    }

    private sealed record Entry(string Name, bool IsFolder, long Size, uint Crc, DateTime? Modified, uint Attributes);

    /// <summary>One folder: its coder's output goes straight to the archive.</summary>
    private sealed class Block(Stream output, Lzma2Writer? lzma2)
    {
        public int Dictionary { get; init; }
        public bool Lzma2 => lzma2 is not null;
        public long Unpacked { get; private set; }
        public long Packed { get; private set; }
        public int Streams { get; set; }

        public void Write(ReadOnlySpan<byte> data)
        {
            if (lzma2 is null)
                output.Write(data);
            else
                lzma2.Write(data);
            Unpacked += data.Length;
        }

        public void Finish()
        {
            if (lzma2 is null)
            {
                Packed = Unpacked;
                return;
            }
            lzma2.Finish();
            Packed = lzma2.Written;
        }
    }

    /// <summary>Little-endian numbers and 7z's variable-length NUMBER into a growing buffer.</summary>
    private sealed class HeaderWriter
    {
        private readonly MemoryStream _stream = new();

        public void Byte(byte value) => _stream.WriteByte(value);

        public void Bytes(ReadOnlySpan<byte> value) => _stream.Write(value);

        public void UInt16(ushort value)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
            _stream.Write(bytes);
        }

        public void UInt32(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            _stream.Write(bytes);
        }

        public void UInt64(ulong value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
            _stream.Write(bytes);
        }

        /// <summary>
        /// A NUMBER: the count of leading 1 bits in the first byte says how many more bytes follow (little-endian); the
        /// first byte's remaining bits are the value's highest bits.
        /// </summary>
        public void Number(ulong value)
        {
            byte first = 0;
            byte mask = 0x80;
            var extra = 0;
            for (; extra < 8; extra++)
            {
                if (value < 1UL << (7 * (extra + 1)))
                {
                    first |= (byte)(value >> (8 * extra));
                    break;
                }
                first |= mask;
                mask >>= 1;
            }
            _stream.WriteByte(first);
            for (var i = 0; i < extra; i++)
                _stream.WriteByte((byte)(value >> (8 * i)));
        }

        /// <summary>A files-info property: its id, its size and its data.</summary>
        public void Property(byte id, ReadOnlySpan<byte> data)
        {
            Byte(id);
            Number((ulong)data.Length);
            Bytes(data);
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
