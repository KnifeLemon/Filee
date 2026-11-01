// LZX decompression (the "MSCompressed" section of CHM files), written from Microsoft's description of the LZX format
// in the Cabinet SDK and the LZX DELTA specification. The bit stream is read as 16-bit little-endian words, most
// significant bit first. Blocks are verbatim (main and length Huffman trees), aligned offset (verbatim plus a tree for
// the low three bits of long offsets) or uncompressed. Tree lengths are sent as deltas from the previous block's
// through a 20-symbol pretree with run codes. Output comes in 32 KB frames: after each frame the input is realigned to
// 16 bits, frames may carry Intel E8 call translation, and every reset interval the state (repeat offsets, tree
// lengths, header) starts over, which is what lets CHM readers start in the middle of the stream.
//
// Damaged input fails with InvalidDataException: unknown block types, trees with more codes than lengths allow,
// codes that are not in a tree, matches that reach before the start of the data or past their block, and input that
// ends early. The caller says how many bytes to produce, so the output can't grow beyond its limit.

namespace Filee.Engines.Ebooks;

internal sealed class LzxDecoder
{
    public const int FrameSize = 32768;

    private const int MinMatch = 2;
    private const int NumChars = 256;
    private const int PretreeSymbols = 20;
    private const int LengthSymbols = 249;
    private const int AlignedSymbols = 8;
    private const int MaxCodeLength = 16;

    /// <summary>Bytes the decoder may read past the end of its input (as zeros) before the input counts as cut short.</summary>
    private const int MaxOverrun = 64;

    private static readonly int[] ExtraBits = new int[52];
    private static readonly uint[] PositionBase = new uint[51];

    static LzxDecoder()
    {
        for (int i = 0, j = 0; i < 51; i += 2)
        {
            ExtraBits[i] = j;
            ExtraBits[i + 1] = j;
            if (i != 0 && j < 17)
                j++;
        }
        for (uint i = 0, j = 0; i < 51; i++)
        {
            PositionBase[i] = j;
            j += 1u << ExtraBits[i];
        }
    }

    private readonly LzxInput _input;
    private readonly byte[] _window;
    private readonly int _windowMask;
    private readonly int _mainSymbols;
    private readonly int _resetFrames;

    private readonly byte[] _pretreeLengths = new byte[PretreeSymbols];
    private readonly byte[] _mainLengths;
    private readonly byte[] _lengthLengths = new byte[LengthSymbols];
    private readonly byte[] _alignedLengths = new byte[AlignedSymbols];
    private HuffmanTable _main = HuffmanTable.Empty;
    private HuffmanTable _length = HuffmanTable.Empty;
    private HuffmanTable _aligned = HuffmanTable.Empty;

    private uint _r0 = 1, _r1 = 1, _r2 = 1;
    private int _blockType;
    private int _blockLength;
    private int _blockRemaining;
    private bool _headerRead;
    private bool _intelStarted;
    private int _intelFileSize;

    private uint _bitBuffer;
    private int _bitCount;

    private int _windowPosition;
    private long _decoded;

    /// <param name="windowBits">Window size as a power of two, 15 to 21.</param>
    /// <param name="resetInterval">Frames between state resets; 0 for none.</param>
    public LzxDecoder(LzxInput input, int windowBits, int resetInterval)
    {
        if (windowBits is < 15 or > 21)
            throw new InvalidDataException($"The LZX window size 2^{windowBits} is not supported.");
        _input = input;
        _window = new byte[1 << windowBits];
        _windowMask = _window.Length - 1;
        var positionSlots = windowBits switch
        {
            20 => 42,
            21 => 50,
            _ => windowBits * 2,
        };
        _mainSymbols = NumChars + positionSlots * 8;
        _mainLengths = new byte[_mainSymbols];
        _resetFrames = Math.Max(0, resetInterval);
    }

    /// <summary>Decompresses <paramref name="length"/> bytes into <paramref name="output"/>.</summary>
    public void Decompress(Stream output, long length, CancellationToken cancellationToken)
    {
        var frame = new byte[FrameSize];
        long frameIndex = 0;
        var framePosition = 0; // bytes of the current frame already in the window (a match may run into it)
        while (_decoded - framePosition < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_resetFrames > 0 && frameIndex % _resetFrames == 0)
                ResetState();
            if (!_headerRead)
            {
                _intelFileSize = ReadBits(1) == 1 ? (int)((ReadBits(16) << 16) | ReadBits(16)) : 0;
                _headerRead = true;
            }

            var frameStart = (int)((_decoded - framePosition) & _windowMask);
            var frameSize = (int)Math.Min(FrameSize, length - (_decoded - framePosition));
            while (framePosition < frameSize)
            {
                if (_blockRemaining == 0)
                    ReadBlockHeader();
                framePosition += _blockType == 3 ? CopyUncompressed(frameSize - framePosition) : DecodeSymbol();
            }

            // The frame: realigned input, E8 translation on a copy (the window keeps the untranslated bytes).
            for (var i = 0; i < frameSize; i++)
                frame[i] = _window[(frameStart + i) & _windowMask];
            if (_intelStarted && _intelFileSize != 0 && frameIndex < 32768 && frameSize > 10)
                TranslateE8(frame.AsSpan(0, frameSize), frameIndex * FrameSize);
            output.Write(frame, 0, frameSize);
            framePosition -= frameSize;
            frameIndex++;
            if (_bitCount > 0)
                EnsureBits(16);
            RemoveBits(_bitCount & 15);
        }
    }

    private void ResetState()
    {
        if (_blockRemaining != 0)
            throw new InvalidDataException("An LZX block runs past a reset point.");
        _r0 = _r1 = _r2 = 1;
        _headerRead = false;
        _blockType = 0;
        _blockLength = 0;
        Array.Clear(_mainLengths);
        Array.Clear(_lengthLengths);
    }

    // ───────────────────────── Blocks ─────────────────────────

    private void ReadBlockHeader()
    {
        // An uncompressed block of odd length is followed by a padding byte.
        if (_blockType == 3 && (_blockLength & 1) == 1)
            _input.ReadByte();
        _blockType = (int)ReadBits(3);
        _blockLength = (int)((ReadBits(16) << 8) | ReadBits(8));
        _blockRemaining = _blockLength;
        if (_blockLength == 0)
            throw new InvalidDataException("An LZX block is empty.");
        switch (_blockType)
        {
            case 2:
                for (var i = 0; i < AlignedSymbols; i++)
                    _alignedLengths[i] = (byte)ReadBits(3);
                _aligned = HuffmanTable.Build(_alignedLengths);
                goto case 1;
            case 1:
                ReadLengths(_mainLengths, 0, NumChars);
                ReadLengths(_mainLengths, NumChars, _mainSymbols);
                _main = HuffmanTable.Build(_mainLengths);
                if (_mainLengths[0xE8] != 0)
                    _intelStarted = true;
                ReadLengths(_lengthLengths, 0, LengthSymbols);
                _length = HuffmanTable.Build(_lengthLengths);
                break;
            case 3:
                _intelStarted = true; // nothing tells otherwise
                // 1 to 16 bits of padding up to the next 16-bit word, then the repeat offsets and the raw bytes.
                EnsureBits(16);
                if (_bitCount > 16)
                    _input.Position -= 2;
                _bitBuffer = 0;
                _bitCount = 0;
                _r0 = _input.ReadUInt32();
                _r1 = _input.ReadUInt32();
                _r2 = _input.ReadUInt32();
                break;
            default:
                throw new InvalidDataException($"The LZX data has an unknown block type ({_blockType}).");
        }
    }

    /// <summary>Tree lengths <paramref name="first"/>..<paramref name="last"/> as deltas through a new pretree.</summary>
    private void ReadLengths(byte[] lengths, int first, int last)
    {
        for (var i = 0; i < PretreeSymbols; i++)
            _pretreeLengths[i] = (byte)ReadBits(4);
        var pretree = HuffmanTable.Build(_pretreeLengths);
        for (var x = first; x < last;)
        {
            var code = Decode(pretree);
            switch (code)
            {
                case 17:
                    x = Fill(lengths, x, last, (int)ReadBits(4) + 4, 0);
                    break;
                case 18:
                    x = Fill(lengths, x, last, (int)ReadBits(5) + 20, 0);
                    break;
                case 19:
                    {
                        var run = (int)ReadBits(1) + 4;
                        var delta = Decode(pretree);
                        if (delta > 16)
                            throw new InvalidDataException("The LZX tree lengths are damaged.");
                        x = Fill(lengths, x, last, run, (byte)((lengths[x] - delta + 17) % 17));
                        break;
                    }
                default:
                    lengths[x] = (byte)((lengths[x] - code + 17) % 17);
                    x++;
                    break;
            }
        }
    }

    private static int Fill(byte[] lengths, int x, int last, int count, byte value)
    {
        var end = Math.Min(last, x + count);
        lengths.AsSpan(x, end - x).Fill(value);
        return x + count;
    }

    /// <summary>One literal or match of a verbatim or aligned block; returns the bytes it produced.</summary>
    private int DecodeSymbol()
    {
        var symbol = Decode(_main);
        if (symbol < NumChars)
        {
            Put((byte)symbol);
            _blockRemaining--;
            return 1;
        }
        symbol -= NumChars;
        var length = (symbol & 7) + MinMatch;
        if ((symbol & 7) == 7)
            length += Decode(_length);
        var slot = symbol >> 3;
        uint offset;
        switch (slot)
        {
            case 0:
                offset = _r0;
                break;
            case 1:
                offset = _r1;
                _r1 = _r0;
                _r0 = offset;
                break;
            case 2:
                offset = _r2;
                _r2 = _r0;
                _r0 = offset;
                break;
            default:
                {
                    var extra = ExtraBits[slot];
                    offset = PositionBase[slot] - 2;
                    if (_blockType == 2 && extra >= 3)
                    {
                        if (extra > 3)
                            offset += ReadBits(extra - 3) << 3;
                        offset += (uint)Decode(_aligned);
                    }
                    else if (extra > 0)
                    {
                        offset += ReadBits(extra);
                    }
                    _r2 = _r1;
                    _r1 = _r0;
                    _r0 = offset;
                    break;
                }
        }
        if (length > _blockRemaining)
            throw new InvalidDataException("An LZX match runs past the end of its block.");
        if (offset == 0 || offset > _decoded || offset > _window.Length)
            throw new InvalidDataException("An LZX match points before the start of the data.");
        var from = (int)((_windowPosition - offset) & (uint)_windowMask);
        for (var i = 0; i < length; i++)
        {
            Put(_window[from]);
            from = (from + 1) & _windowMask;
        }
        _blockRemaining -= length;
        return length;
    }

    private int CopyUncompressed(int room)
    {
        var count = Math.Min(room, _blockRemaining);
        for (var i = 0; i < count; i++)
        {
            var b = _input.ReadByte();
            if (b < 0)
                throw new InvalidDataException("The LZX data ends inside an uncompressed block.");
            Put((byte)b);
        }
        _blockRemaining -= count;
        return count;
    }

    private void Put(byte value)
    {
        _window[_windowPosition] = value;
        _windowPosition = (_windowPosition + 1) & _windowMask;
        _decoded++;
    }

    /// <summary>Intel E8 call translation: absolute call targets back to relative ones.</summary>
    private void TranslateE8(Span<byte> data, long frameOffset)
    {
        var position = (int)frameOffset;
        var end = data.Length - 10;
        for (var i = 0; i < end;)
        {
            if (data[i++] != 0xE8)
            {
                position++;
                continue;
            }
            var absolute = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data[i..]);
            if (absolute >= -position && absolute < _intelFileSize)
            {
                var relative = absolute >= 0 ? absolute - position : absolute + _intelFileSize;
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data[i..], relative);
            }
            i += 4;
            position += 5;
        }
    }

    // ───────────────────────── Bits ─────────────────────────

    private void EnsureBits(int count)
    {
        while (_bitCount < count)
        {
            var lo = _input.ReadByte();
            var hi = _input.ReadByte();
            if (hi < 0 && _input.Overrun > MaxOverrun)
                throw new InvalidDataException("The LZX data ends early.");
            var word = (uint)(Math.Max(0, lo) | (Math.Max(0, hi) << 8));
            _bitBuffer |= word << (16 - _bitCount);
            _bitCount += 16;
        }
    }

    private uint ReadBits(int count)
    {
        if (count == 0)
            return 0;
        EnsureBits(count);
        var value = _bitBuffer >> (32 - count);
        RemoveBits(count);
        return value;
    }

    private void RemoveBits(int count)
    {
        if (count == 0)
            return;
        _bitBuffer = count >= 32 ? 0 : _bitBuffer << count;
        _bitCount -= count;
    }

    private int Decode(HuffmanTable table)
    {
        if (table.Bits == 0)
            throw new InvalidDataException("The LZX data uses a code of an empty tree.");
        EnsureBits(MaxCodeLength);
        var entry = table.Entries[_bitBuffer >> (32 - table.Bits)];
        if (entry == 0)
            throw new InvalidDataException("The LZX data holds a code that is not in its tree.");
        RemoveBits(entry & 0x1F);
        return entry >> 5;
    }

    /// <summary>A canonical Huffman code as a lookup table indexed by the next <see cref="Bits"/> bits.</summary>
    private sealed class HuffmanTable
    {
        public static readonly HuffmanTable Empty = new(0, []);

        private HuffmanTable(int bits, int[] entries)
        {
            Bits = bits;
            Entries = entries;
        }

        /// <summary>Longest code; 0 for a tree without codes.</summary>
        public int Bits { get; }

        /// <summary>Symbol &lt;&lt; 5 | code length; 0 for bit patterns no code starts with.</summary>
        public int[] Entries { get; }

        public static HuffmanTable Build(byte[] lengths)
        {
            var bits = 0;
            foreach (var length in lengths)
            {
                if (length > MaxCodeLength)
                    throw new InvalidDataException("The LZX data has a code longer than 16 bits.");
                bits = Math.Max(bits, length);
            }
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
                        throw new InvalidDataException("The LZX data has a tree with more codes than its lengths allow.");
                    entries.AsSpan(position, span).Fill((symbol << 5) | length);
                    position += span;
                }
            }
            return new HuffmanTable(bits, entries);
        }
    }
}

/// <summary>Bytes the LZX decoder reads, from a stream it may step back in.</summary>
internal sealed class LzxInput(Stream stream, long start, long length)
{
    private readonly byte[] _buffer = new byte[64 * 1024];
    private long _bufferStart = -1;
    private int _bufferLength;

    /// <summary>Position relative to the start of the compressed data.</summary>
    public long Position { get; set; }

    /// <summary>Bytes asked for past the end.</summary>
    public long Overrun { get; private set; }

    public int ReadByte()
    {
        if (Position >= length)
        {
            Position++;
            Overrun++;
            return -1;
        }
        if (Position < _bufferStart || Position >= _bufferStart + _bufferLength)
        {
            _bufferStart = Position;
            stream.Position = start + Position;
            _bufferLength = stream.ReadAtLeast(_buffer.AsSpan(0, (int)Math.Min(_buffer.Length, length - Position)), 1, throwOnEndOfStream: false);
            if (_bufferLength == 0)
            {
                Position++;
                Overrun++;
                return -1;
            }
        }
        return _buffer[Position++ - _bufferStart];
    }

    public uint ReadUInt32()
    {
        uint value = 0;
        for (var i = 0; i < 4; i++)
        {
            var b = ReadByte();
            if (b < 0)
                throw new InvalidDataException("The LZX data ends early.");
            value |= (uint)b << (8 * i);
        }
        return value;
    }
}
