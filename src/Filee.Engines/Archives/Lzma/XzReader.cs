// Reads .xz files (The .xz File Format 1.2.1): one or more streams, each a 12-byte header (magic, flags naming the
// check, CRC-32), blocks (header with the filter chain and optional sizes, LZMA2 data, padding to four bytes, check),
// an index that lists every block's unpadded and unpacked size, and a 12-byte footer. Streams may be followed by
// zero padding in multiples of four bytes. Only the LZMA2 filter alone is read: BCJ and delta chains are refused as
// unsupported. Every header CRC, the check (CRC-32, CRC-64 or SHA-256) and the index are verified.
//
// Limits: the dictionary each block names is refused past the caller's limit before anything is allocated, and the
// window grows with the output (LzmaDecoder). What the stream unpacks to is not limited here: the caller copies it
// through ArchiveFiles.CopyLimited with an UnpackBudget, like every other single-stream decoder.

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Filee.Engines.Archives.Lzma;

/// <summary>A read-only stream of the data in an .xz file.</summary>
internal sealed class XzReader : Stream
{
    /// <summary>The largest dictionary read by default (1.5 GiB, the most xz writes).</summary>
    public const uint DefaultMaxDictionary = LzmaDecoder.MaxDictionary;

    private enum Step { StreamHeader, BlockOrIndex, BlockData, StreamPadding, Done }

    private readonly Stream _stream;
    private readonly ByteInput _input;
    private readonly string _name;
    private readonly uint _maxDictionary;
    private readonly CancellationToken _cancellationToken;
    private readonly bool _leaveOpen;
    private readonly List<(long Unpadded, long Unpacked)> _blocks = [];

    private Step _step = Step.StreamHeader;
    private int _checkType;
    private byte[] _streamFlags = [];
    private Lzma2Reader? _block;
    private Check? _check;
    private long _blockStart;
    private int _blockHeaderSize;
    private long _declaredPacked;
    private long _declaredUnpacked;
    private long _blockUnpacked;
    private long _position;

    /// <param name="name">The file name used in error messages.</param>
    /// <param name="maxDictionary">Dictionaries larger than this are refused (<see cref="ArchiveSafety.Oversized"/>).</param>
    public XzReader(Stream input, string name, uint maxDictionary = DefaultMaxDictionary,
        CancellationToken cancellationToken = default, bool leaveOpen = true)
    {
        _stream = input;
        _input = new ByteInput(input);
        _name = name;
        _maxDictionary = Math.Min(maxDictionary, LzmaDecoder.MaxDictionary);
        _cancellationToken = cancellationToken;
        _leaveOpen = leaveOpen;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return 0;
        while (true)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            switch (_step)
            {
                case Step.StreamHeader:
                    ReadStreamHeader();
                    break;
                case Step.BlockOrIndex:
                    ReadBlockHeaderOrIndex();
                    break;
                case Step.BlockData:
                    {
                        var n = _block!.Read(buffer);
                        if (n > 0)
                        {
                            _check!.Append(buffer[..n]);
                            _blockUnpacked += n;
                            _position += n;
                            if (_declaredUnpacked >= 0 && _blockUnpacked > _declaredUnpacked)
                                throw Damaged("a block unpacks to more than its header says");
                            return n;
                        }
                        FinishBlock();
                        break;
                    }
                case Step.StreamPadding:
                    ReadStreamPadding();
                    break;
                default:
                    return 0;
            }
        }
    }

    private void ReadStreamHeader()
    {
        Span<byte> header = stackalloc byte[12];
        if (_input.ReadAtMost(header) < header.Length || !header[..6].SequenceEqual(XzFormat.HeaderMagic))
            throw ArchiveSafety.NotAnArchive(_name);
        ReadStreamFlags(header[6..]);
    }

    /// <summary>The flags and CRC-32 that follow the magic bytes of a stream header.</summary>
    private void ReadStreamFlags(ReadOnlySpan<byte> flagsAndCrc)
    {
        if (flagsAndCrc[0] != 0 || (flagsAndCrc[1] & 0xF0) != 0)
            throw Damaged("the stream flags are not known");
        if (BinaryPrimitives.ReadUInt32LittleEndian(flagsAndCrc[2..]) != Crc32.Compute(flagsAndCrc[..2]))
            throw Damaged("the stream header checksum does not match");
        _streamFlags = flagsAndCrc[..2].ToArray();
        _checkType = flagsAndCrc[1];
        _blocks.Clear();
        _step = Step.BlockOrIndex;
    }

    private void ReadBlockHeaderOrIndex()
    {
        _blockStart = _input.Consumed;
        var first = _input.ReadByte();
        if (first < 0)
            throw Damaged("the data ends too early");
        if (first == 0)
        {
            ReadIndexAndFooter();
            return;
        }

        var size = (first + 1) * 4;
        var header = new byte[size];
        header[0] = (byte)first;
        _input.ReadExactly(header.AsSpan(1));
        if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(size - 4)) != Crc32.Compute(header.AsSpan(0, size - 4)))
            throw Damaged("a block header checksum does not match");

        var flags = header[1];
        if ((flags & 0x3C) != 0)
            throw Damaged("a block header has unknown flags");
        var at = 2;
        var end = size - 4;
        _declaredPacked = (flags & 0x40) != 0 ? ReadVarint(header, ref at, end) : -1;
        _declaredUnpacked = (flags & 0x80) != 0 ? ReadVarint(header, ref at, end) : -1;
        if (_declaredPacked == 0)
            throw Damaged("a block header names an empty block");

        var filters = (flags & 0x03) + 1;
        uint? dictionary = null;
        for (var i = 0; i < filters; i++)
        {
            var id = ReadVarint(header, ref at, end);
            var propertiesSize = ReadVarint(header, ref at, end);
            if (propertiesSize > end - at)
                throw Damaged("a block header is cut short");
            if (id != XzFormat.Lzma2FilterId || i != filters - 1 || propertiesSize != 1)
                throw new InvalidDataException($"{_name} uses an xz filter Filee does not read (only LZMA2 alone).");
            dictionary = XzFormat.DictionaryFromProperty(header[at]) ?? throw Damaged("the LZMA2 dictionary size is out of range");
            at += (int)propertiesSize;
        }
        for (; at < end; at++)
            if (header[at] != 0)
                throw Damaged("a block header has data in its padding");

        if (dictionary > _maxDictionary)
            throw ArchiveSafety.Oversized(_name,
                $"it needs a {ArchiveSafety.FormatSize(dictionary.Value)} dictionary, more than the {ArchiveSafety.FormatSize(_maxDictionary)} Filee allows");

        _blockHeaderSize = size;
        _blockUnpacked = 0;
        _check = Check.Create(_checkType);
        _block = new Lzma2Reader(_input, dictionary!.Value, _declaredUnpacked, _cancellationToken);
        _step = Step.BlockData;
    }

    private void FinishBlock()
    {
        var packed = _input.Consumed - _blockStart - _blockHeaderSize;
        if (_declaredPacked >= 0 && packed != _declaredPacked)
            throw Damaged("a block's packed size does not match its header");
        if (_declaredUnpacked >= 0 && _blockUnpacked != _declaredUnpacked)
            throw Damaged("a block's unpacked size does not match its header");
        for (var padding = (4 - (int)(packed % 4)) % 4; padding > 0; padding--)
            if (Next() != 0)
                throw Damaged("a block has data in its padding");

        var stored = new byte[XzFormat.CheckSize(_checkType)];
        _input.ReadExactly(stored);
        if (!_check!.Matches(stored))
            throw Damaged("the check of the unpacked data does not match");

        _blocks.Add((_blockHeaderSize + packed + stored.Length, _blockUnpacked));
        _block = null;
        _check = null;
        _step = Step.BlockOrIndex;
    }

    private void ReadIndexAndFooter()
    {
        // The indicator byte (0) is already read; the index CRC covers it.
        var crc = new Crc32();
        crc.Append([0]);
        var count = ReadVarint(crc);
        if (count != _blocks.Count)
            throw Damaged("the index lists a different number of blocks");
        foreach (var (unpadded, unpacked) in _blocks)
        {
            if (ReadVarint(crc) != unpadded || ReadVarint(crc) != unpacked)
                throw Damaged("the index does not match the blocks");
        }
        var indexSize = _input.Consumed - _blockStart;
        for (; indexSize % 4 != 0; indexSize++)
        {
            var b = Next();
            if (b != 0)
                throw Damaged("the index has data in its padding");
            crc.Append([0]);
        }
        Span<byte> stored = stackalloc byte[4];
        _input.ReadExactly(stored);
        if (BinaryPrimitives.ReadUInt32LittleEndian(stored) != crc.Value)
            throw Damaged("the index checksum does not match");
        indexSize += 4;

        Span<byte> footer = stackalloc byte[12];
        _input.ReadExactly(footer);
        if (!footer[10..].SequenceEqual(XzFormat.FooterMagic))
            throw Damaged("the stream footer is missing");
        if (BinaryPrimitives.ReadUInt32LittleEndian(footer) != Crc32.Compute(footer[4..10]))
            throw Damaged("the stream footer checksum does not match");
        if ((BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]) + 1L) * 4 != indexSize)
            throw Damaged("the stream footer names a different index size");
        if (!footer.Slice(8, 2).SequenceEqual(_streamFlags))
            throw Damaged("the stream footer flags differ from the header");
        _step = Step.StreamPadding;
    }

    private void ReadStreamPadding()
    {
        // Zero padding in multiples of four, then another stream or the end of the file.
        Span<byte> word = stackalloc byte[4];
        while (true)
        {
            var read = _input.ReadAtMost(word);
            if (read == 0)
            {
                _step = Step.Done;
                return;
            }
            if (read < 4)
                throw Damaged("the data after the stream is not padding");
            if (word.SequenceEqual(XzFormat.HeaderMagic.AsSpan(0, 4)))
            {
                ReadStreamHeaderAfter();
                return;
            }
            if (BinaryPrimitives.ReadUInt32LittleEndian(word) != 0)
                throw Damaged("the data after the stream is not padding");
        }
    }

    private void ReadStreamHeaderAfter()
    {
        Span<byte> rest = stackalloc byte[8];
        _input.ReadExactly(rest);
        if (!rest[..2].SequenceEqual(XzFormat.HeaderMagic.AsSpan(4)))
            throw Damaged("the data after the stream is not padding");
        ReadStreamFlags(rest[2..]);
    }

    private long ReadVarint(Crc32 crc)
    {
        long value = 0;
        for (var i = 0; i < 9; i++)
        {
            var b = Next();
            crc.Append([(byte)b]);
            value |= (long)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0)
            {
                if (b == 0 && i > 0)
                    throw Damaged("a number in the index is not minimally coded");
                return value;
            }
        }
        throw Damaged("a number in the index is too long");
    }

    private long ReadVarint(byte[] header, ref int at, int end)
    {
        long value = 0;
        for (var i = 0; i < 9; i++)
        {
            if (at >= end)
                throw Damaged("a block header is cut short");
            var b = header[at++];
            value |= (long)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0)
            {
                if (b == 0 && i > 0)
                    throw Damaged("a number in a block header is not minimally coded");
                return value;
            }
        }
        throw Damaged("a number in a block header is too long");
    }

    private int Next()
    {
        var b = _input.ReadByte();
        return b >= 0 ? b : throw Damaged("the data ends too early");
    }

    private Exception Damaged(string detail) => ArchiveSafety.Damaged(_name, detail);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
            _stream.Dispose();
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>The block check named in the stream flags.</summary>
    private abstract class Check
    {
        public abstract void Append(ReadOnlySpan<byte> data);
        public abstract bool Matches(ReadOnlySpan<byte> stored);

        public static Check Create(int type) => type switch
        {
            XzFormat.CheckCrc32 => new Crc32Check(),
            XzFormat.CheckCrc64 => new Crc64Check(),
            XzFormat.CheckSha256 => new Sha256Check(),
            _ => new UncheckedCheck(), // none, or a check type this version of the format reserves: skipped like xz does
        };
    }

    private sealed class UncheckedCheck : Check
    {
        public override void Append(ReadOnlySpan<byte> data) { }
        public override bool Matches(ReadOnlySpan<byte> stored) => true;
    }

    private sealed class Crc32Check : Check
    {
        private readonly Crc32 _crc = new();
        public override void Append(ReadOnlySpan<byte> data) => _crc.Append(data);
        public override bool Matches(ReadOnlySpan<byte> stored) => BinaryPrimitives.ReadUInt32LittleEndian(stored) == _crc.Value;
    }

    private sealed class Crc64Check : Check
    {
        private readonly Crc64 _crc = new();
        public override void Append(ReadOnlySpan<byte> data) => _crc.Append(data);
        public override bool Matches(ReadOnlySpan<byte> stored) => BinaryPrimitives.ReadUInt64LittleEndian(stored) == _crc.Value;
    }

    private sealed class Sha256Check : Check
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public override void Append(ReadOnlySpan<byte> data) => _hash.AppendData(data);
        public override bool Matches(ReadOnlySpan<byte> stored) => _hash.GetHashAndReset().AsSpan().SequenceEqual(stored);
    }
}
