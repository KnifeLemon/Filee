// bzip2 decompression in managed code, written for ALZip archives: ALZ stores bzip2 blocks without the "BZh" stream
// header, marks each block with "DLZ\x01" (instead of the 48-bit block magic, the block CRC and the "randomised" bit)
// and ends with "DLZ\x02". Standard bzip2 streams (.bz2, .tar.bz2, 7z and EGG) are read too, and NSIS installers'
// framing: no stream header, the byte 0x31 before each block (no block CRC, no "randomised" bit) and 0x17 at the end.
// The block format itself (Huffman coded MTF/RLE2 symbols, Burrows-Wheeler transform, RLE1) is plain bzip2.
//
// Each block is decoded in two steps: the Huffman and move-to-front symbols into the transformed block (which must
// be read in order: where a block ends is only known once it is decoded), then the inverse transform, the run-length
// coding and the block CRC. Standard streams opened with OpenRead do the second step for several blocks at once on
// worker threads and hand the blocks out in order; the push API (Decompress) does both in turn. Huffman codes of up
// to 10 bits are looked up in a table, longer ones are found as the reference decoder does, so any table, even a
// malformed one, decodes to the same symbols. "Randomised" blocks (bzip2 0.9.0, and encoders that still randomise
// very repetitive blocks) are not decoded here: the stream reader then starts over with the caller's fallback decoder
// and skips what it handed out already.

using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace Filee.Engines.Archives;

/// <summary>Decodes standard, ALZ-framed or NSIS-framed bzip2 data.</summary>
internal sealed class Bzip2Decoder
{
    private const ulong BlockMagic = 0x314159265359;
    private const ulong StreamEndMagic = 0x177245385090;
    private const uint AlzBlockMarker = 0x444C5A01; // "DLZ\x01"
    private const uint AlzEndMarker = 0x444C5A02;   // "DLZ\x02"
    private const uint NsisBlockMarker = 0x31;
    private const uint NsisEndMarker = 0x17;
    private const int MaxCodeLength = 20;
    private const int GroupSize = 50;
    private const int MaxSelectors = 18002;
    private const int FastBits = 10;

    /// <summary>bzip2's block CRC table (see <see cref="CreateBlockCrcTable"/>), shared with Bzip2Writer.</summary>
    internal static readonly uint[] BlockCrcTable = CreateBlockCrcTable();

    private readonly Stream _input;
    private readonly byte[] _inBuffer = new byte[64 * 1024];
    private int _inPos;
    private int _inLength;
    private ulong _bits;
    private int _bitCount;  // bits held in _bits (the oldest highest)
    private int _padBits;   // of those, zero bits past the end of the input

    private Bzip2Decoder(Stream input)
    {
        _input = input;
    }

    /// <summary>Decompresses <paramref name="input"/> to <paramref name="output"/>.</summary>
    /// <param name="alzFraming">True for ALZ's variant of the stream framing (see the file comment).</param>
    /// <param name="maxOutput">Stops with an error when the data would unpack to more bytes than this.</param>
    /// <returns>Number of bytes written.</returns>
    public static long Decompress(Stream input, Stream output, bool alzFraming, long maxOutput, CancellationToken cancellationToken)
    {
        var decoder = new Bzip2Decoder(input);
        var sink = new PushSink(output, maxOutput);
        if (alzFraming)
            decoder.DecodeAlz(sink, cancellationToken);
        else
            decoder.DecodeStandard(block => sink.Block(block), cancellationToken);
        return sink.Written;
    }

    /// <summary>Decompresses bzip2 data in NSIS's framing (see the file comment) to <paramref name="output"/>.</summary>
    /// <param name="maxOutput">Stops with an error when the data would unpack to more bytes than this.</param>
    /// <returns>Number of bytes written.</returns>
    public static long DecompressNsis(Stream input, Stream output, long maxOutput, CancellationToken cancellationToken)
    {
        var decoder = new Bzip2Decoder(input);
        var sink = new PushSink(output, maxOutput);
        // NSIS always writes 900k blocks.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var marker = decoder.ReadBits(8);
            if (marker == NsisEndMarker)
                return sink.Written;
            if (marker != NsisBlockMarker)
                throw new InvalidDataException("bzip2 block marker missing");
            sink.Block(decoder.ReadBlock(9, expectedCrc: null));
        }
    }

    /// <summary>
    /// A stream of the data in the standard bzip2 stream(s) of <paramref name="input"/>, with blocks decoded on worker
    /// threads. Disposing it leaves <paramref name="input"/> open.
    /// </summary>
    /// <param name="fallback">
    /// A decoder of the same data from its start, for streams with randomised blocks; without one they are refused.
    /// </param>
    public static Stream OpenRead(Stream input, CancellationToken cancellationToken, Func<Stream>? fallback = null) =>
        new ReadStream(input, cancellationToken, fallback);

    private const string RandomisedMessage = "randomised bzip2 blocks (bzip2 0.9.0) are not supported";

    private static bool IsRandomised(Exception ex) => ex is InvalidDataException && ex.Message == RandomisedMessage;

    private void DecodeAlz(PushSink sink, CancellationToken cancellationToken)
    {
        // ALZip always writes 900k blocks and leaves out the stream header.
        while (TryReadBits(32, out var marker))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (marker == AlzEndMarker)
                return;
            if (marker != AlzBlockMarker)
                throw new InvalidDataException("bzip2 block marker missing");
            sink.Block(ReadBlock(9, expectedCrc: null));
        }
    }

    /// <summary>Reads standard streams, handing every block (not yet transformed back) to <paramref name="onBlock"/>.</summary>
    private void DecodeStandard(Action<Block> onBlock, CancellationToken cancellationToken)
    {
        // Several streams may follow each other (pbzip2, "cat a.bz2 b.bz2").
        var first = true;
        while (true)
        {
            if (!TryReadBits(8, out var b))
            {
                if (first)
                    throw new InvalidDataException("empty bzip2 data");
                return;
            }
            // Some writers leave out the "BZ" signature and start with "h9".
            if (b == 'B')
            {
                if (ReadBits(8) != 'Z')
                    throw new InvalidDataException("not bzip2 data");
                b = ReadBits(8);
            }
            if (b != 'h')
                throw new InvalidDataException("not bzip2 data");
            var level = (int)ReadBits(8) - '0';
            if (level is < 1 or > 9)
                throw new InvalidDataException("invalid bzip2 block size");
            first = false;

            uint combined = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var magic = ((ulong)ReadBits(24) << 24) | ReadBits(24);
                var crc = ReadBits(32);
                if (magic == StreamEndMagic)
                {
                    if (crc != combined)
                        throw new InvalidDataException("bzip2 stream checksum mismatch");
                    break;
                }
                if (magic != BlockMagic)
                    throw new InvalidDataException("bzip2 block magic missing");
                if (ReadBits(1) != 0)
                    throw new InvalidDataException(RandomisedMessage);
                onBlock(ReadBlock(level, crc));
                combined = ((combined << 1) | (combined >> 31)) ^ crc;
            }
            // The next stream starts on a byte boundary.
            _bitCount -= _bitCount % 8;
        }
    }

    /// <summary>A block after its Huffman and move-to-front step: the transformed bytes (in the low 8 bits of Tt).</summary>
    private sealed class Block(int[] tt, int origPtr, uint? expectedCrc)
    {
        public int[] Tt { get; } = tt;
        public int Length { get; set; }
        public int OrigPtr { get; } = origPtr;
        public uint? ExpectedCrc { get; } = expectedCrc;
        public int[] Counts { get; } = new int[256];
    }

    /// <summary>Decodes the symbols of one block, starting at origPtr (after magic, CRC and randomised bit).</summary>
    private Block ReadBlock(int level, uint? expectedCrc)
    {
        var blockMax = level * 100_000;
        var tt = ArrayPool<int>.Shared.Rent(blockMax);
        try
        {
            return ReadBlock(tt, blockMax, expectedCrc);
        }
        catch
        {
            ArrayPool<int>.Shared.Return(tt);
            throw;
        }
    }

    private Block ReadBlock(int[] tt, int blockMax, uint? expectedCrc)
    {
        var origPtr = (int)ReadBits(24);

        // Symbols used in this block, as a 16x16 bitmap.
        var seqToUnseq = new byte[256];
        var inUseCount = 0;
        var inUse16 = ReadBits(16);
        for (var i = 0; i < 16; i++)
        {
            if ((inUse16 & (0x8000u >> i)) == 0)
                continue;
            var bits = ReadBits(16);
            for (var j = 0; j < 16; j++)
                if ((bits & (0x8000u >> j)) != 0)
                    seqToUnseq[inUseCount++] = (byte)(i * 16 + j);
        }
        if (inUseCount == 0)
            throw new InvalidDataException("bzip2 block uses no symbols");
        var alphaSize = inUseCount + 2;

        var groupCount = (int)ReadBits(3);
        var selectorCount = (int)ReadBits(15);
        if (groupCount is < 2 or > 6 || selectorCount < 1)
            throw new InvalidDataException("invalid bzip2 block header");

        // Selectors (which Huffman table each group of 50 symbols uses), MTF and unary coded.
        var selectors = new byte[Math.Min(selectorCount, MaxSelectors)];
        Span<byte> mtfGroups = stackalloc byte[6];
        for (var i = 0; i < groupCount; i++)
            mtfGroups[i] = (byte)i;
        for (var i = 0; i < selectorCount; i++)
        {
            var j = 0;
            while (ReadBits(1) == 1)
                if (++j >= groupCount)
                    throw new InvalidDataException("invalid bzip2 selector");
            var value = mtfGroups[j];
            for (; j > 0; j--)
                mtfGroups[j] = mtfGroups[j - 1];
            mtfGroups[0] = value;
            if (i < selectors.Length)
                selectors[i] = value; // bzip2 1.0.8 reads but ignores selectors beyond the limit
        }

        // Code lengths, delta coded, and the decode tables built from them.
        var tables = new HuffmanTable[groupCount];
        var lengths = new int[alphaSize];
        for (var t = 0; t < groupCount; t++)
        {
            var current = (int)ReadBits(5);
            for (var i = 0; i < alphaSize; i++)
            {
                while (true)
                {
                    if (current is < 1 or > MaxCodeLength)
                        throw new InvalidDataException("invalid bzip2 code length");
                    if (ReadBits(1) == 0)
                        break;
                    current += ReadBits(1) == 0 ? 1 : -1;
                }
                lengths[i] = current;
            }
            tables[t] = new HuffmanTable(lengths, alphaSize);
        }

        // MTF / RLE2 symbols → block bytes.
        Span<byte> mtf = stackalloc byte[256];
        for (var i = 0; i < 256; i++)
            mtf[i] = (byte)i;
        var block = new Block(tt, origPtr, expectedCrc);
        var counts = block.Counts;
        var endOfBlock = inUseCount + 1;
        var length = 0;
        var groupIndex = -1;
        var groupLeft = 0;
        HuffmanTable table = tables[0];

        var symbol = NextSymbol();
        while (symbol != endOfBlock)
        {
            if (symbol <= 1)
            {
                // RUNA / RUNB: a bijective base-2 run length of the symbol at the MTF front.
                var run = 0;
                var weight = 1;
                do
                {
                    if (weight > blockMax)
                        throw new InvalidDataException("bzip2 run too long");
                    run += (symbol + 1) * weight;
                    weight <<= 1;
                    symbol = NextSymbol();
                } while (symbol <= 1);

                var value = seqToUnseq[mtf[0]];
                if (length + run > blockMax)
                    throw new InvalidDataException("bzip2 block too long");
                counts[value] += run;
                tt.AsSpan(length, run).Fill(value);
                length += run;
                continue;
            }

            if (length >= blockMax)
                throw new InvalidDataException("bzip2 block too long");
            var index = symbol - 1;
            if (index >= inUseCount)
                throw new InvalidDataException("invalid bzip2 symbol");
            var front = mtf[index];
            if (index < 16)
            {
                for (var k = index; k > 0; k--)
                    mtf[k] = mtf[k - 1];
            }
            else
            {
                mtf[..index].CopyTo(mtf[1..]);
            }
            mtf[0] = front;
            var b = seqToUnseq[front];
            counts[b]++;
            tt[length++] = b;
            symbol = NextSymbol();
        }
        if (origPtr < 0 || origPtr >= Math.Max(length, 1))
            throw new InvalidDataException("invalid bzip2 origin pointer");
        block.Length = length;
        return block;

        int NextSymbol()
        {
            if (groupLeft == 0)
            {
                if (++groupIndex >= selectors.Length)
                    throw new InvalidDataException("bzip2 selectors exhausted");
                table = tables[selectors[groupIndex]];
                groupLeft = GroupSize;
            }
            groupLeft--;
            var peek = PeekBits(MaxCodeLength);
            var entry = table.Fast[peek >> (MaxCodeLength - FastBits)];
            if (entry >= 0)
            {
                SkipBits(entry & 31);
                return entry >> 5;
            }
            return table.Slow(this, peek);
        }
    }

    /// <summary>
    /// Canonical Huffman decode tables as in the reference bzip2 (limit / base / perm per code length), plus a table
    /// of the codes of up to <see cref="FastBits"/> bits found the same way.
    /// </summary>
    private sealed class HuffmanTable
    {
        private readonly int[] _limit = new int[MaxCodeLength + 2];
        private readonly int[] _base = new int[MaxCodeLength + 2];
        private readonly int[] _perm = new int[258];
        private readonly int _minLength;

        /// <summary>Symbol &lt;&lt; 5 | length for a code of up to FastBits bits, by its next FastBits bits; -1 for longer or invalid codes.</summary>
        public readonly short[] Fast = new short[1 << FastBits];

        public HuffmanTable(int[] lengths, int alphaSize)
        {
            var minLength = MaxCodeLength;
            var maxLength = 0;
            for (var i = 0; i < alphaSize; i++)
            {
                minLength = Math.Min(minLength, lengths[i]);
                maxLength = Math.Max(maxLength, lengths[i]);
            }

            var p = 0;
            for (var len = minLength; len <= maxLength; len++)
                for (var symbol = 0; symbol < alphaSize; symbol++)
                    if (lengths[symbol] == len)
                        _perm[p++] = symbol;

            Span<int> countAtLength = stackalloc int[MaxCodeLength + 2];
            countAtLength.Clear();
            for (var i = 0; i < alphaSize; i++)
                countAtLength[lengths[i] + 1]++;
            for (var i = 1; i < MaxCodeLength + 2; i++)
                countAtLength[i] += countAtLength[i - 1];

            for (var i = 0; i < MaxCodeLength + 2; i++)
            {
                _limit[i] = -1; // lengths outside min..max never match, so decoding continues to longer codes
                _base[i] = countAtLength[i];
            }

            var code = 0;
            for (var len = minLength; len <= maxLength; len++)
            {
                code += countAtLength[len + 1] - countAtLength[len];
                _limit[len] = code - 1;
                code <<= 1;
            }
            for (var len = minLength + 1; len <= maxLength; len++)
                _base[len] = ((_limit[len - 1] + 1) << 1) - countAtLength[len];
            _minLength = minLength;

            // The fast table: for every FastBits-bit prefix, the code the reference search finds within those bits.
            for (var prefix = 0; prefix < Fast.Length; prefix++)
            {
                short entry = -1;
                for (var len = minLength; len <= FastBits; len++)
                {
                    var c = prefix >> (FastBits - len);
                    if (c <= _limit[len])
                    {
                        var slot = c - _base[len];
                        if (slot is >= 0 and < 258)
                            entry = (short)(_perm[slot] << 5 | len);
                        break;
                    }
                }
                Fast[prefix] = entry;
            }
        }

        /// <summary>The reference search, for codes the fast table doesn't hold: <paramref name="peek"/> holds the next 20 bits.</summary>
        public int Slow(Bzip2Decoder decoder, uint peek)
        {
            var bitsUsed = _minLength;
            var code = (int)(peek >> (MaxCodeLength - bitsUsed));
            while (bitsUsed <= MaxCodeLength && code > _limit[bitsUsed])
            {
                bitsUsed++;
                if (bitsUsed <= MaxCodeLength)
                    code = (int)(peek >> (MaxCodeLength - bitsUsed));
            }
            if (bitsUsed > MaxCodeLength)
            {
                // The reference decoder reads all 20 bits (and more) before it gives up.
                decoder.SkipBits(MaxCodeLength);
                throw new InvalidDataException("invalid bzip2 Huffman code");
            }
            decoder.SkipBits(bitsUsed);
            var slot = code - _base[bitsUsed];
            if (slot is < 0 or >= 258)
                throw new InvalidDataException("invalid bzip2 Huffman code");
            return _perm[slot];
        }
    }

    /// <summary>
    /// The second step of a block: the inverse Burrows-Wheeler transform, the run-length coding (4 equal bytes + a
    /// count byte) and the block CRC. Writes into <paramref name="output"/> (grown as needed, rented) and returns the
    /// byte count; past <paramref name="limit"/> bytes it stops with an error.
    /// </summary>
    private static int Inverse(Block block, ref byte[] output, long limit)
    {
        var tt = block.Tt;
        var length = block.Length;
        var counts = block.Counts;

        // Link every position to its successor.
        Span<int> starts = stackalloc int[256];
        var sum = 0;
        for (var i = 0; i < 256; i++)
        {
            starts[i] = sum;
            sum += counts[i];
        }
        for (var i = 0; i < length; i++)
        {
            var b = tt[i] & 0xFF;
            tt[starts[b]++] |= i << 8;
        }

        // Walk the chain and undo the initial run-length coding.
        var table = BlockCrcTable;
        var crc = 0xFFFFFFFFu;
        var used = 0;
        var position = length == 0 ? 0 : tt[block.OrigPtr] >> 8;
        var previous = -1;
        var repeat = 0;
        var buffer = output;
        for (var i = 0; i < length; i++)
        {
            var entry = tt[position];
            var b = entry & 0xFF;
            position = entry >> 8;
            if (repeat == 4)
            {
                if (used + b > buffer.Length)
                    buffer = Grow(buffer, used + b, used, limit);
                for (var k = 0; k < b; k++)
                {
                    crc = (crc << 8) ^ table[(crc >> 24) ^ (uint)previous];
                    buffer[used++] = (byte)previous;
                }
                repeat = 0;
                previous = -1;
                continue;
            }
            repeat = b == previous ? repeat + 1 : 1;
            previous = b;
            if (used == buffer.Length)
                buffer = Grow(buffer, used + 1, used, limit);
            crc = (crc << 8) ^ table[(crc >> 24) ^ (uint)b];
            buffer[used++] = (byte)b;
        }
        output = buffer;
        if (used > limit)
            throw new InvalidDataException("the data unpacks to more than the archive declares");
        if (block.ExpectedCrc is { } expected && ~crc != expected)
            throw new BlockCrcException(used);
        return used;
    }

    private static byte[] Grow(byte[] buffer, int needed, int used, long limit)
    {
        if (needed > limit)
            throw new InvalidDataException("the data unpacks to more than the archive declares");
        var larger = ArrayPool<byte>.Shared.Rent(Math.Max(needed, buffer.Length * 2));
        buffer.AsSpan(0, used).CopyTo(larger);
        if (buffer.Length > 0)
            ArrayPool<byte>.Shared.Return(buffer);
        return larger;
    }

    /// <summary>A block whose bytes came out (all of them, <see cref="Count"/>) but whose CRC doesn't match.</summary>
    private sealed class BlockCrcException(int count) : Exception("bzip2 block checksum mismatch")
    {
        public int Count { get; } = count;
    }

    /// <summary>Writes blocks to a stream, as they are decoded.</summary>
    private sealed class PushSink(Stream output, long maxOutput)
    {
        private byte[] _buffer = [];

        public long Written { get; private set; }

        public void Block(Block block)
        {
            try
            {
                if (_buffer.Length < block.Length + (block.Length >> 3))
                {
                    if (_buffer.Length > 0)
                        ArrayPool<byte>.Shared.Return(_buffer);
                    _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1 << 16, block.Length + (block.Length >> 3)));
                }
                int count;
                try
                {
                    count = Inverse(block, ref _buffer, maxOutput - Written);
                }
                catch (BlockCrcException ex)
                {
                    // The bytes go out before the mismatch is reported, as they always did.
                    Write(ex.Count);
                    throw new InvalidDataException(ex.Message);
                }
                Write(count);
            }
            finally
            {
                ArrayPool<int>.Shared.Return(block.Tt);
            }
        }

        private void Write(int count)
        {
            Written += count;
            output.Write(_buffer, 0, count);
        }
    }

    /// <summary>Standard bzip2 as a stream: blocks are read in turn and transformed back on worker threads.</summary>
    private sealed class ReadStream : ReadOnlyDecoderStream
    {
        private readonly BlockingCollection<Task<Decoded>> _blocks;
        private readonly CancellationTokenSource _stop;
        private readonly CancellationToken _cancellationToken;
        private readonly Task _producer;
        private readonly Func<Stream>? _fallback;
        private Decoded? _current;
        private int _position;
        private long _handedOut;
        private Stream? _other; // the fallback decoder, once a randomised block came

        public ReadStream(Stream input, CancellationToken cancellationToken, Func<Stream>? fallback)
        {
            _cancellationToken = cancellationToken;
            _fallback = fallback;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _blocks = new BlockingCollection<Task<Decoded>>(Math.Max(2, Environment.ProcessorCount));
            var token = _stop.Token;
            _producer = Task.Factory.StartNew(() => Produce(input, token), CancellationToken.None, TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        private void Produce(Stream input, CancellationToken token)
        {
            try
            {
                new Bzip2Decoder(input).DecodeStandard(block => _blocks.Add(Task.Run(() => Decode(block), CancellationToken.None), token), token);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                // After the blocks before it; waits for room like they do (never dropped).
                try
                {
                    _blocks.Add(Task.FromException<Decoded>(ex), token);
                }
                catch (OperationCanceledException)
                {
                    // The reader is gone.
                }
            }
            catch (OperationCanceledException)
            {
                // Stopped: the reader is gone or the work was cancelled (the reader sees that itself).
            }
            finally
            {
                _blocks.CompleteAdding();
            }
        }

        private static Decoded Decode(Block block)
        {
            var output = ArrayPool<byte>.Shared.Rent(Math.Max(1 << 16, block.Length + (block.Length >> 3)));
            try
            {
                var count = Inverse(block, ref output, long.MaxValue);
                return new Decoded(output, count, null);
            }
            catch (BlockCrcException ex)
            {
                return new Decoded(output, ex.Count, ExceptionDispatchInfo.Capture(new InvalidDataException(ex.Message)));
            }
            catch (Exception ex)
            {
                return new Decoded(output, 0, ExceptionDispatchInfo.Capture(ex));
            }
            finally
            {
                ArrayPool<int>.Shared.Return(block.Tt);
            }
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty)
                return 0;
            while (true)
            {
                if (_other is not null)
                    return _other.Read(buffer);
                if (_current is { } current)
                {
                    if (_position < current.Count)
                    {
                        var count = Math.Min(buffer.Length, current.Count - _position);
                        current.Output.AsSpan(_position, count).CopyTo(buffer);
                        _position += count;
                        _handedOut += count;
                        return count;
                    }
                    current.Error?.Throw();
                    ArrayPool<byte>.Shared.Return(current.Output);
                    _current = null;
                }
                _cancellationToken.ThrowIfCancellationRequested();
                if (!_blocks.TryTake(out var next, Timeout.Infinite, _cancellationToken))
                    return 0;
                try
                {
                    _current = next.GetAwaiter().GetResult();
                }
                catch (InvalidDataException ex) when (IsRandomised(ex) && _fallback is not null)
                {
                    StartOver();
                    continue;
                }
                _position = 0;
            }
        }

        /// <summary>A randomised block: the fallback decoder from the start, past what was handed out already.</summary>
        private void StartOver()
        {
            _producer.Wait(_cancellationToken); // it stopped at the block; the input is free
            var other = _fallback!();
            var skip = new byte[1 << 16];
            for (var left = _handedOut; left > 0;)
            {
                var read = other.Read(skip, 0, (int)Math.Min(skip.Length, left));
                if (read <= 0)
                    throw new InvalidDataException("bzip2 data ends unexpectedly");
                left -= read;
            }
            _other = other;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stop.Cancel();
                try
                {
                    _producer.Wait();
                }
                catch (AggregateException)
                {
                    // The producer catches everything it throws; nothing to report on the way out.
                }
                _stop.Dispose();
                _blocks.Dispose();
                _other?.Dispose();
            }
            base.Dispose(disposing);
        }

        private sealed record Decoded(byte[] Output, int Count, ExceptionDispatchInfo? Error);
    }

    private uint ReadBits(int count)
    {
        if (!TryReadBits(count, out var value))
            throw new InvalidDataException("bzip2 data ends unexpectedly");
        return value;
    }

    /// <summary>Reads up to 32 bits, most significant first; false when the input is exhausted.</summary>
    private bool TryReadBits(int count, out uint value)
    {
        if (_bitCount - _padBits < count)
            Refill();
        if (_bitCount - _padBits < count)
        {
            value = 0;
            return false;
        }
        _bitCount -= count;
        value = (uint)((_bits >> _bitCount) & ((1UL << count) - 1));
        return true;
    }

    /// <summary>The next <paramref name="count"/> bits (up to 32) without using them; zeros past the end of the input.</summary>
    private uint PeekBits(int count)
    {
        if (_bitCount < count)
            Refill();
        return (uint)((_bits >> (_bitCount - count)) & ((1UL << count) - 1));
    }

    /// <summary>Uses bits seen with <see cref="PeekBits"/>; past the end of the input the data is cut short.</summary>
    private void SkipBits(int count)
    {
        _bitCount -= count;
        if (_bitCount < _padBits)
            throw new InvalidDataException("bzip2 data ends unexpectedly");
    }

    /// <summary>Tops the bit buffer up to more than 56 bits, with zero bits once the input has ended.</summary>
    private void Refill()
    {
        while (_bitCount <= 56)
        {
            if (_inPos == _inLength && _padBits == 0)
            {
                _inLength = _input.Read(_inBuffer, 0, _inBuffer.Length);
                _inPos = 0;
            }
            if (_inPos < _inLength)
            {
                _bits = (_bits << 8) | _inBuffer[_inPos++];
            }
            else
            {
                _bits <<= 8;
                _padBits += 8;
            }
            _bitCount += 8;
        }
    }

    /// <summary>bzip2's block CRC: CRC-32 with the polynomial 0x04C11DB7, most significant bit first.</summary>
    private static uint[] CreateBlockCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i << 24;
            for (var k = 0; k < 8; k++)
                c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
            table[i] = c;
        }
        return table;
    }
}
