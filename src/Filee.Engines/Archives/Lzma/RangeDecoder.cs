// The LZMA range decoder (lzma-specification.txt, "The range decoder"): a 32-bit range and code, adaptive 11-bit
// probabilities that move by 1/32 of the distance to the bound after each bit, and "direct" bits of probability 1/2.
// Input comes from a byte array: one whole LZMA2 chunk, or a buffer that is refilled from a stream for raw LZMA.
//
// LzmaDecoder doesn't call in here per bit: it takes range, code and the input position into locals for a whole run
// of symbols (Take / Put back) and decodes the bits itself; only a refill comes back here. BCJ2 uses DecodeBit.

using System.Runtime.CompilerServices;

namespace Filee.Engines.Archives.Lzma;

/// <summary>Shared constants of the LZMA range coder.</summary>
internal static class RangeCoder
{
    public const int ProbabilityBits = 11;
    public const ushort ProbabilityOne = 1 << ProbabilityBits;
    public const ushort ProbabilityInit = ProbabilityOne / 2;
    public const int MoveBits = 5;
    public const uint TopValue = 1 << 24;

    /// <summary>Sets every probability to one half.</summary>
    public static void Reset(Span<ushort> probabilities) => probabilities.Fill(ProbabilityInit);
}

/// <summary>Decodes bits from range-coded input.</summary>
internal sealed class RangeDecoder
{
    private byte[] _buffer = [];
    private int _position;
    private int _end;
    private Stream? _source;
    private bool _sourceEnded;
    private uint _range;
    private uint _code;

    /// <summary>Bytes of the current buffer consumed so far.</summary>
    public int Position => _position;

    /// <summary>True when the coder ended cleanly: the encoder's flush leaves the code at zero.</summary>
    public bool IsFinishedOk => _code == 0;

    /// <summary>The input buffer; bytes before <see cref="End"/> are valid (the buffer itself never changes on refill).</summary>
    internal byte[] Buffer => _buffer;

    /// <summary>The end of the valid input in <see cref="Buffer"/>.</summary>
    internal int End => _end;

    /// <summary>Starts decoding <paramref name="length"/> bytes of <paramref name="buffer"/>; reading past them is damage.</summary>
    public void Start(byte[] buffer, int length)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)length, (uint)buffer.Length, nameof(length));
        _buffer = buffer;
        _position = 0;
        _end = length;
        _source = null;
        Init();
    }

    /// <summary>Starts decoding from <paramref name="source"/>, read through <paramref name="buffer"/>.</summary>
    public void Start(Stream source, byte[] buffer)
    {
        _buffer = buffer;
        _position = 0;
        _end = 0;
        _source = source;
        _sourceEnded = false;
        Init();
    }

    private void Init()
    {
        // The encoder's first byte is always 0; a code equal to the range can't come from an encoder.
        if (NextByte() != 0)
            throw new InvalidDataException("the LZMA data does not start like a range coder stream");
        _range = 0xFFFFFFFF;
        _code = 0;
        for (var i = 0; i < 4; i++)
            _code = _code << 8 | NextByte();
        if (_code == _range)
            throw new InvalidDataException("the LZMA data is damaged");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint NextByte()
    {
        if (_position == _end)
            Refill();
        return _buffer[_position++];
    }

    /// <summary>
    /// Reads the next block of input into <see cref="Buffer"/> from its start and returns the new <see cref="End"/>
    /// (the position goes back to 0); the input ending, or a whole chunk already used up, is damage.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal int Refill()
    {
        if (_source is null || _sourceEnded || _buffer.Length == 0)
            throw new InvalidDataException("the LZMA data ends too early");
        var read = _source.Read(_buffer, 0, _buffer.Length);
        if (read <= 0)
            throw new InvalidDataException("the LZMA data ends too early");
        // LzmaDecoder reads the buffer without bounds checks up to End: never trust a stream for more than was asked.
        if (read > _buffer.Length)
            throw new InvalidOperationException("a stream returned more bytes than were asked for");
        _position = 0;
        _end = read;
        return read;
    }

    /// <summary>
    /// Makes at least <paramref name="wanted"/> unread bytes available when the input is a stream that still has them:
    /// the unread rest moves to the start of the buffer and the stream fills the space after it. Returns the unread
    /// bytes now in the buffer (fewer at the end of the stream, or of a whole chunk).
    /// </summary>
    internal int TopUp(int wanted)
    {
        var left = _end - _position;
        if (left >= wanted || _source is null || _sourceEnded || _buffer.Length < wanted)
            return left;
        System.Buffer.BlockCopy(_buffer, _position, _buffer, 0, left);
        _position = 0;
        _end = left;
        while (_end < wanted)
        {
            var read = _source.Read(_buffer, _end, _buffer.Length - _end);
            if (read <= 0)
            {
                _sourceEnded = true;
                break;
            }
            if (read > _buffer.Length - _end)
                throw new InvalidOperationException("a stream returned more bytes than were asked for");
            _end += read;
        }
        return _end - _position;
    }

    /// <summary>Hands range, code and the input position to a decoder that keeps them in locals over many symbols.</summary>
    internal void Take(out uint range, out uint code, out int position)
    {
        range = _range;
        code = _code;
        position = _position;
    }

    /// <summary>Takes back what <see cref="Take"/> handed out.</summary>
    internal void Put(uint range, uint code, int position)
    {
        _range = range;
        _code = code;
        _position = position;
    }

    /// <summary>One bit with the adaptive probability <paramref name="probability"/> (of a zero).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int DecodeBit(ref ushort probability)
    {
        var p = probability;
        var bound = (_range >> RangeCoder.ProbabilityBits) * p;
        int bit;
        if (_code < bound)
        {
            _range = bound;
            probability = (ushort)(p + ((RangeCoder.ProbabilityOne - p) >> RangeCoder.MoveBits));
            bit = 0;
        }
        else
        {
            _range -= bound;
            _code -= bound;
            probability = (ushort)(p - (p >> RangeCoder.MoveBits));
            bit = 1;
        }
        if (_range < RangeCoder.TopValue)
        {
            _range <<= 8;
            _code = _code << 8 | NextByte();
        }
        return bit;
    }
}
