// Writes .bz2 files with Filee's own encoder, from the bzip2 format: the "BZh9" stream header, blocks of at most
// 900,000 bytes, and the end marker with the CRC of all block CRCs. The input goes through bzip2's first run-length
// step here (runs of 4 to 255 equal bytes become 4 bytes and a count) while the block CRC is taken over the bytes as
// they came; full blocks are encoded (Bzip2BlockEncoder) on up to four threads at once, and written in order. Memory
// stays bounded by the number of blocks in flight (about 20 MB each while sorting). It is a write-only stream, so a TAR
// can be compressed while it is written (.tar.bz2) without a temporary file.

using Filee.Core.Presets;

namespace Filee.Engines.Archives;

/// <summary>A write-only stream that compresses into a .bz2 file.</summary>
internal sealed class Bzip2Writer : Stream
{
    /// <summary>The block size (in units of 100,000 bytes) of every stream Filee writes.</summary>
    private const int Level = 9;

    /// <summary>
    /// A block ends once it holds this much; the run being added (up to five bytes) still fits. bzip2 keeps the same
    /// margin below 900,000.
    /// </summary>
    private const int BlockLimit = Level * 100_000 - 19;

    private readonly Stream _output;
    private readonly bool _leaveOpen;
    private readonly int _passes;
    private readonly CancellationToken _cancellationToken;
    private readonly Slot[] _slots;
    private readonly Bzip2Bits _bits = new();
    private int _current;
    private uint _crc = 0xFFFFFFFF;
    private uint _combined;
    private int _runByte = -1;
    private int _runLength;
    private long _position;
    private bool _finished;

    /// <summary>A block being filled or encoded, with its encoder.</summary>
    private sealed class Slot
    {
        public byte[] Block { get; } = new byte[Level * 100_000];
        public int Length { get; set; }
        public uint Crc { get; set; }
        public Bzip2BlockEncoder Encoder { get; } = new();
        public Task? Work { get; set; }
    }

    /// <summary>Blocks encoded at once by default: a few cores, leaving the rest to the machine.</summary>
    internal static int DefaultThreads => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <param name="threads">Blocks encoded at once (1 encodes on the writing thread).</param>
    public Bzip2Writer(Stream output, ArchiveLevel level, CancellationToken cancellationToken = default, bool leaveOpen = false,
        int threads = 0)
    {
        _output = output;
        _leaveOpen = leaveOpen;
        _cancellationToken = cancellationToken;
        // How often the Huffman tables are refined: the transform costs the same at every level.
        _passes = level switch
        {
            ArchiveLevel.Store or ArchiveLevel.Fast => 1,
            ArchiveLevel.Maximum => 6,
            _ => 4,
        };
        _slots = new Slot[threads > 0 ? threads : DefaultThreads];
        for (var i = 0; i < _slots.Length; i++)
            _slots[i] = new Slot();
        _bits.Write(24, 0x425A68); // "BZh"
        _bits.Write(8, '0' + Level);
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        if (buffer.IsEmpty)
            return;
        _cancellationToken.ThrowIfCancellationRequested();
        var table = Bzip2Decoder.BlockCrcTable;
        var crc = _crc;
        var runByte = _runByte;
        var runLength = _runLength;
        foreach (var b in buffer)
        {
            if (b == runByte && runLength < 255)
            {
                crc = (crc << 8) ^ table[(crc >> 24) ^ b];
                runLength++;
                continue;
            }
            if (runLength > 0)
            {
                var slot = _slots[_current];
                AddRun(slot, (byte)runByte, runLength);
                if (slot.Length >= BlockLimit)
                {
                    _crc = crc;
                    EndBlock();
                    crc = 0xFFFFFFFF;
                }
            }
            crc = (crc << 8) ^ table[(crc >> 24) ^ b];
            runByte = b;
            runLength = 1;
        }
        _crc = crc;
        _runByte = runByte;
        _runLength = runLength;
        _position += buffer.Length;
    }

    private static void AddRun(Slot slot, byte value, int length)
    {
        var block = slot.Block;
        var at = slot.Length;
        if (length < 4)
        {
            for (var i = 0; i < length; i++)
                block[at++] = value;
        }
        else
        {
            block[at++] = value;
            block[at++] = value;
            block[at++] = value;
            block[at++] = value;
            block[at++] = (byte)(length - 4);
        }
        slot.Length = at;
    }

    /// <summary>Hands the current block to its encoder and moves to the next slot, writing out what that slot held.</summary>
    private void EndBlock()
    {
        var slot = _slots[_current];
        slot.Crc = ~_crc;
        _crc = 0xFFFFFFFF;
        if (_slots.Length == 1)
        {
            slot.Encoder.Encode(slot.Block.AsSpan(0, slot.Length), slot.Crc, _passes, _cancellationToken);
            Emit(slot);
            slot.Length = 0;
            return;
        }
        var passes = _passes;
        var token = _cancellationToken;
        slot.Work = Task.Run(() => slot.Encoder.Encode(slot.Block.AsSpan(0, slot.Length), slot.Crc, passes, token), token);
        _current = (_current + 1) % _slots.Length;
        Complete(_slots[_current]);
    }

    /// <summary>Waits for the slot's block (if any), writes it, and empties the slot.</summary>
    private void Complete(Slot slot)
    {
        if (slot.Work is { } work)
        {
            slot.Work = null;
            work.GetAwaiter().GetResult();
            Emit(slot);
        }
        slot.Length = 0;
    }

    private void Emit(Slot slot)
    {
        _bits.Append(slot.Encoder.Output);
        _combined = ((_combined << 1) | (_combined >> 31)) ^ slot.Crc;
        _bits.DrainTo(_output);
    }

    /// <summary>Encodes what is left and writes the end marker and the stream CRC.</summary>
    public void Finish()
    {
        if (_finished)
            return;
        _cancellationToken.ThrowIfCancellationRequested();
        var slot = _slots[_current];
        if (_runLength > 0)
        {
            AddRun(slot, (byte)_runByte, _runLength);
            _runLength = 0;
        }
        if (slot.Length > 0)
            EndBlock();
        for (var i = 0; i < _slots.Length; i++)
            Complete(_slots[(_current + i) % _slots.Length]);
        _finished = true;

        _bits.Write(24, 0x177245);
        _bits.Write(24, 0x385090);
        _bits.Write(32, _combined);
        _bits.Finish();
        _bits.DrainTo(_output);
        _output.Flush();
    }

    protected override void Dispose(bool disposing)
    {
        // As XzWriter: Dispose never finishes a half-written file; it lets running blocks stop and closes.
        if (disposing)
        {
            foreach (var slot in _slots)
            {
                try
                {
                    slot.Work?.Wait(CancellationToken.None);
                }
                catch (AggregateException)
                {
                    // Already reported by the call that failed, or not wanted any more.
                }
                slot.Work = null;
            }
            if (!_leaveOpen)
                _output.Dispose();
        }
        _finished = true;
        base.Dispose(disposing);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_finished;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>Bits, most significant first, into a growing buffer of whole bytes plus up to 31 pending bits.</summary>
internal sealed class Bzip2Bits
{
    private byte[] _buffer = new byte[1 << 16];
    private int _bytes;
    private ulong _pending;
    private int _pendingBits;

    /// <summary>Whole bytes written (after <see cref="Finish"/>, the last one may be partly used).</summary>
    public int ByteCount => _bytes;

    /// <summary>Bits used in the last byte after <see cref="Finish"/> (8 when it is full).</summary>
    public int LastByteBits { get; private set; } = 8;

    public void Clear()
    {
        _bytes = 0;
        _pending = 0;
        _pendingBits = 0;
        LastByteBits = 8;
    }

    /// <summary>Writes the low <paramref name="count"/> (0 … 32) bits of <paramref name="value"/>, which has no others set.</summary>
    public void Write(int count, uint value)
    {
        _pending = (_pending << count) | value;
        _pendingBits += count;
        if (_pendingBits < 32)
            return;
        _pendingBits -= 32;
        var word = (uint)(_pending >> _pendingBits);
        if (_bytes + 4 > _buffer.Length)
            Array.Resize(ref _buffer, _buffer.Length * 2);
        _buffer[_bytes] = (byte)(word >> 24);
        _buffer[_bytes + 1] = (byte)(word >> 16);
        _buffer[_bytes + 2] = (byte)(word >> 8);
        _buffer[_bytes + 3] = (byte)word;
        _bytes += 4;
    }

    /// <summary>Moves the pending bits into the buffer, the last byte filled up with zeros.</summary>
    public void Finish()
    {
        if (_bytes + 4 > _buffer.Length)
            Array.Resize(ref _buffer, _buffer.Length * 2);
        while (_pendingBits >= 8)
        {
            _pendingBits -= 8;
            _buffer[_bytes++] = (byte)(_pending >> _pendingBits);
        }
        LastByteBits = 8;
        if (_pendingBits > 0)
        {
            _buffer[_bytes++] = (byte)(_pending << (8 - _pendingBits));
            LastByteBits = _pendingBits;
        }
        _pendingBits = 0;
    }

    /// <summary>Writes the bits of <paramref name="other"/> (finished) after these.</summary>
    public void Append(Bzip2Bits other)
    {
        var data = other._buffer;
        var full = other.LastByteBits == 8 ? other._bytes : other._bytes - 1;
        var i = 0;
        for (; i + 4 <= full; i += 4)
            Write(32, (uint)(data[i] << 24 | data[i + 1] << 16 | data[i + 2] << 8 | data[i + 3]));
        for (; i < full; i++)
            Write(8, data[i]);
        if (full < other._bytes)
            Write(other.LastByteBits, (uint)data[full] >> (8 - other.LastByteBits));
    }

    /// <summary>Writes the whole bytes to <paramref name="output"/> and keeps the pending bits.</summary>
    public void DrainTo(Stream output)
    {
        output.Write(_buffer, 0, _bytes);
        _bytes = 0;
    }
}
