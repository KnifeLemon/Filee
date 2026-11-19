// Reads .xz files (The .xz File Format 1.2.1): one or more streams, each a 12-byte header (magic, flags naming the
// check, CRC-32), blocks (header with the filter chain and optional sizes, LZMA2 data, padding to four bytes, check),
// an index that lists every block's unpadded and unpacked size, and a 12-byte footer. Streams may be followed by
// zero padding in multiples of four bytes. Only the LZMA2 filter alone is read: BCJ and delta chains are refused as
// unsupported. Every header CRC, the check (CRC-32, CRC-64 or SHA-256) and the index are verified.
//
// Limits: the dictionary each block names is refused past the caller's limit before anything is allocated, and the
// window grows with the output (LzmaDecoder). What the stream unpacks to is not limited here: the caller copies it
// through ArchiveFiles.CopyLimited with an UnpackBudget, like every other single-stream decoder.
//
// Threads: a block whose header names both sizes (as xz, 7-Zip and Filee write them with several threads) is read
// whole, then decoded and checked on a worker thread while the next block headers are read (Lzma2Parallel bounds how
// many and how large). Blocks are handed out in order; a block that fails on its thread is decoded again on the
// reader's thread as it is read, so damaged data gives the same bytes and the same error, at the same place, as the
// one-thread reader. An error in a header read ahead is thrown after the blocks before it. A worker hands out its
// block as it decodes it (the check is compared at the block's end, as the one-thread reader does).

using System.Buffers.Binary;
using System.Runtime.ExceptionServices;
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

    // Blocks decoding on worker threads, in order; the one being handed out; an error met while reading ahead.
    private readonly Queue<AheadBlock> _ahead = new();
    private AheadBlock? _current;
    private int _currentReady;
    private Replay? _replay;
    private int _currentPosition;
    private int _aheadLimit = Environment.ProcessorCount;
    private ExceptionDispatchInfo? _deferred;
    private CancellationTokenSource? _cancel;

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
            if (_replay is { } replay)
            {
                var n = replay.Reader.Read(buffer);
                if (n > 0)
                {
                    replay.Check.Append(buffer[..n]);
                    replay.Unpacked += n;
                    _position += n;
                    if (replay.Unpacked > replay.Block.DeclaredUnpacked)
                        throw Damaged("a block unpacks to more than its header says");
                    return n;
                }
                CheckBlockEnd(replay.Input, replay.Input.Consumed, replay.Block.DeclaredPacked, replay.Block.DeclaredUnpacked, replay.Unpacked,
                    _checkType, replay.Check, _name);
                _replay = null;
                continue;
            }
            if (_current is { } current)
            {
                if (_currentPosition < _currentReady)
                {
                    var count = Math.Min(buffer.Length, _currentReady - _currentPosition);
                    current.Work.Output.AsSpan(_currentPosition, count).CopyTo(buffer);
                    _currentPosition += count;
                    _position += count;
                    return count;
                }
                // The worker hands its block out as it decodes it.
                _currentReady = current.Work.WaitBeyond(_currentPosition, out var done);
                if (_currentPosition < _currentReady || !done)
                    continue;
                _current = null;
                if (current.Work.Error is { } error)
                {
                    // Decoded again here, as the one-thread reader reads it, past what the worker handed out.
                    if (error.SourceException is OperationCanceledException)
                        error.Throw();
                    var input = new ByteInput(new MemoryStream(current.Data, 0, current.Length, writable: false));
                    var again = new Replay(current, input, new Lzma2Reader(input, current.Dictionary, current.DeclaredUnpacked, _cancellationToken),
                        Check.Create(_checkType));
                    Lzma2Parallel.Skip(again.Reader, _currentPosition, again.Check.Append);
                    again.Unpacked = _currentPosition;
                    _replay = again;
                }
                continue;
            }
            ReadAhead();
            if (_ahead.TryDequeue(out var next))
            {
                _current = next;
                _currentPosition = 0;
                _currentReady = 0;
                continue;
            }
            if (_deferred is { } deferred)
            {
                _deferred = null;
                deferred.Throw();
            }
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

    /// <summary>
    /// Reads the next block headers (and the blocks that name both sizes) while blocks decode on worker threads; an
    /// error is kept until the blocks before it are handed out.
    /// </summary>
    private void ReadAhead()
    {
        while (_ahead.Count > 0 && _ahead.Count < _aheadLimit && _step == Step.BlockOrIndex && _deferred is null)
        {
            try
            {
                ReadBlockHeaderOrIndex();
            }
            catch (Exception ex)
            {
                _deferred = ExceptionDispatchInfo.Capture(ex);
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
        if (TryDecodeAhead(dictionary!.Value))
            return;
        _check = Check.Create(_checkType);
        _block = new Lzma2Reader(_input, dictionary.Value, _declaredUnpacked, _cancellationToken);
        _step = Step.BlockData;
    }

    /// <summary>
    /// Reads a block whose header names both sizes whole and decodes it on a worker thread, when threads are allowed
    /// and it fits in memory beside the others; false to decode it here as it is read.
    /// </summary>
    private bool TryDecodeAhead(uint dictionary)
    {
        if (!Lzma2Parallel.Enabled || _declaredPacked < 0 || _declaredUnpacked < 0
            || _declaredPacked > Lzma2Parallel.MaxSegment || _declaredUnpacked > Lzma2Parallel.MaxSegment)
            return false;
        // A block that hardly shrank is mostly stored chunks: copying them here is quicker than a thread.
        if (!Lzma2Parallel.Forced && _declaredPacked > _declaredUnpacked / 10 * 9)
            return false;
        var checkSize = XzFormat.CheckSize(_checkType);
        var length = (int)_declaredPacked + (int)((4 - _declaredPacked % 4) % 4) + checkSize;
        var workers = Lzma2Parallel.Workers(_declaredUnpacked, length, dictionary);
        if (workers < 2)
            return false;
        _aheadLimit = workers;
        var data = GC.AllocateUninitializedArray<byte>(length);
        var read = _input.ReadAtMost(data);
        // Recorded as the header says: a block that differs fails on its own thread, before the index is compared.
        _blocks.Add((_blockHeaderSize + _declaredPacked + checkSize, _declaredUnpacked));
        _cancel ??= CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken);
        var (token, packed, unpacked, checkType, name) = (_cancel.Token, _declaredPacked, (int)_declaredUnpacked, _checkType, _name);
        var work = new Lzma2Parallel.Decoded(GC.AllocateUninitializedArray<byte>(unpacked));
        _ = Task.Run(() => DecodeBlock(work, data, read, dictionary, packed, unpacked, checkType, name, token), CancellationToken.None);
        _ahead.Enqueue(new AheadBlock(work, data, read, dictionary, packed, unpacked));
        _step = Step.BlockOrIndex;
        return true;
    }

    /// <summary>A block read whole and decoding on a worker thread, with what it takes to decode it again here.</summary>
    private sealed record AheadBlock(Lzma2Parallel.Decoded Work, byte[] Data, int Length, uint Dictionary, long DeclaredPacked,
        int DeclaredUnpacked);

    /// <summary>A block that failed on its thread, decoded again on this one as it is read.</summary>
    private sealed class Replay(AheadBlock block, ByteInput input, Lzma2Reader reader, Check check)
    {
        public AheadBlock Block { get; } = block;
        public ByteInput Input { get; } = input;
        public Lzma2Reader Reader { get; } = reader;
        public Check Check { get; } = check;
        public long Unpacked { get; set; }
    }

    /// <summary>A block read whole (data, padding and check), decoded and checked as <see cref="FinishBlock"/> does.</summary>
    private static void DecodeBlock(Lzma2Parallel.Decoded result, byte[] data, int length, uint dictionary, long declaredPacked,
        int declaredUnpacked, int checkType, string name, CancellationToken cancellationToken)
    {
        try
        {
            var input = new ByteInput(new MemoryStream(data, 0, length, writable: false));
            var reader = new Lzma2Reader(input, dictionary, declaredUnpacked, cancellationToken);
            var check = Check.Create(checkType);
            var output = result.Output;
            while (true)
            {
                var read = result.Count < declaredUnpacked
                    ? reader.Read(output.AsSpan(result.Count, Math.Min(Lzma2Parallel.Decoded.Step, declaredUnpacked - result.Count)))
                    : 0;
                if (read == 0)
                {
                    // All of it, or the data ended; one more byte would be past the header's size.
                    Span<byte> probe = stackalloc byte[1];
                    if (result.Count == declaredUnpacked && reader.Read(probe) > 0)
                        throw ArchiveSafety.Damaged(name, "a block unpacks to more than its header says");
                    break;
                }
                check.Append(output.AsSpan(result.Count, read));
                result.Count += read;
                result.Publish();
            }
            CheckBlockEnd(input, input.Consumed, declaredPacked, declaredUnpacked, result.Count, checkType, check, name);
        }
        catch (Exception ex)
        {
            result.Error = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            result.Finish();
        }
    }

    private void FinishBlock()
    {
        var packed = _input.Consumed - _blockStart - _blockHeaderSize;
        var checkSize = CheckBlockEnd(_input, packed, _declaredPacked, _declaredUnpacked, _blockUnpacked, _checkType, _check!, _name);
        _blocks.Add((_blockHeaderSize + packed + checkSize, _blockUnpacked));
        _block = null;
        _check = null;
        _step = Step.BlockOrIndex;
    }

    /// <summary>
    /// Compares a block's sizes with its header, reads its padding and check from <paramref name="input"/> and
    /// compares the check; returns the check's size.
    /// </summary>
    private static int CheckBlockEnd(ByteInput input, long packed, long declaredPacked, long declaredUnpacked, long unpacked, int checkType,
        Check check, string name)
    {
        if (declaredPacked >= 0 && packed != declaredPacked)
            throw ArchiveSafety.Damaged(name, "a block's packed size does not match its header");
        if (declaredUnpacked >= 0 && unpacked != declaredUnpacked)
            throw ArchiveSafety.Damaged(name, "a block's unpacked size does not match its header");
        for (var padding = (4 - (int)(packed % 4)) % 4; padding > 0; padding--)
        {
            var b = input.ReadByte();
            if (b < 0)
                throw ArchiveSafety.Damaged(name, "the data ends too early");
            if (b != 0)
                throw ArchiveSafety.Damaged(name, "a block has data in its padding");
        }

        var stored = new byte[XzFormat.CheckSize(checkType)];
        input.ReadExactly(stored);
        if (!check.Matches(stored))
            throw ArchiveSafety.Damaged(name, "the check of the unpacked data does not match");
        return stored.Length;
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
        if (disposing)
        {
            _cancel?.Cancel();
            _cancel?.Dispose();
            if (!_leaveOpen)
                _stream.Dispose();
        }
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
