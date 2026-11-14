// The LZMA range decoder (lzma-specification.txt, "The range decoder"): a 32-bit range and code, adaptive 11-bit
// probabilities that move by 1/32 of the distance to the bound after each bit, and "direct" bits of probability 1/2.
// Input comes from a byte array: one whole LZMA2 chunk, or a buffer that is refilled from a stream for raw LZMA.

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
    private uint _range;
    private uint _code;

    /// <summary>Bytes of the current buffer consumed so far.</summary>
    public int Position => _position;

    /// <summary>True when the coder ended cleanly: the encoder's flush leaves the code at zero.</summary>
    public bool IsFinishedOk => _code == 0;

    /// <summary>Starts decoding <paramref name="length"/> bytes of <paramref name="buffer"/>; reading past them is damage.</summary>
    public void Start(byte[] buffer, int length)
    {
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
    internal uint NextByte()
    {
        if (_position == _end)
            Refill();
        return _buffer[_position++];
    }

    private void Refill()
    {
        if (_source is null || _buffer.Length == 0)
            throw new InvalidDataException("the LZMA data ends too early");
        var read = _source.Read(_buffer, 0, _buffer.Length);
        if (read <= 0)
            throw new InvalidDataException("the LZMA data ends too early");
        _position = 0;
        _end = read;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Normalize()
    {
        if (_range < RangeCoder.TopValue)
        {
            _range <<= 8;
            _code = _code << 8 | NextByte();
        }
    }

    /// <summary>
    /// Hands range and code to a caller that keeps them in locals over many bits (<see cref="Bit"/>); it gives them back
    /// with <see cref="Store"/> before calling any other method here.
    /// </summary>
    public void Load(out uint range, out uint code)
    {
        range = _range;
        code = _code;
    }

    /// <summary>Takes range and code back from <see cref="Load"/>.</summary>
    public void Store(uint range, uint code)
    {
        _range = range;
        _code = code;
    }

    /// <summary>
    /// <see cref="DecodeBit"/> on range and code held in the caller's locals, which the JIT keeps in registers: the
    /// decoder's single-bit decisions (match or literal, which repeat) run through here.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Bit(ref uint range, ref uint code, ref ushort probability, RangeDecoder rc)
    {
        var p = probability;
        var bound = (range >> RangeCoder.ProbabilityBits) * p;
        int bit;
        if (code < bound)
        {
            range = bound;
            probability = (ushort)(p + ((RangeCoder.ProbabilityOne - p) >> RangeCoder.MoveBits));
            bit = 0;
        }
        else
        {
            range -= bound;
            code -= bound;
            probability = (ushort)(p - (p >> RangeCoder.MoveBits));
            bit = 1;
        }
        if (range < RangeCoder.TopValue)
        {
            range <<= 8;
            code = code << 8 | rc.NextByte();
        }
        return bit;
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
        Normalize();
        return bit;
    }

    /// <summary><paramref name="count"/> bits of probability one half, most significant first.</summary>
    public uint DecodeDirectBits(int count)
    {
        uint result = 0;
        for (; count > 0; count--)
        {
            _range >>= 1;
            _code -= _range;
            var t = 0u - (_code >> 31); // all ones when the subtraction went below zero (bit 0)
            _code += _range & t;
            if (_code == _range)
                throw new InvalidDataException("the LZMA data is damaged");
            Normalize();
            result = (result << 1) + (t + 1);
        }
        return result;
    }

    /// <summary>
    /// A <paramref name="bits"/>-bit symbol from a binary tree of probabilities (index 1 is the root). Range and code
    /// stay in locals for the whole symbol (the hot path of literals and lengths), not in the fields for every bit.
    /// </summary>
    public uint DecodeTree(Span<ushort> probabilities, int bits)
    {
        var range = _range;
        var code = _code;
        uint m = 1;
        var end = 1u << bits;
        while (m < end)
        {
            ref var probability = ref probabilities[(int)m];
            var p = probability;
            var bound = (range >> RangeCoder.ProbabilityBits) * p;
            if (code < bound)
            {
                range = bound;
                probability = (ushort)(p + ((RangeCoder.ProbabilityOne - p) >> RangeCoder.MoveBits));
                m <<= 1;
            }
            else
            {
                range -= bound;
                code -= bound;
                probability = (ushort)(p - (p >> RangeCoder.MoveBits));
                m = (m << 1) + 1;
            }
            if (range < RangeCoder.TopValue)
            {
                range <<= 8;
                code = code << 8 | NextByte();
            }
        }
        _range = range;
        _code = code;
        return m - end;
    }

    /// <summary>
    /// A literal after a match (the LZMA spec's "matched literal"): while its bits equal those of
    /// <paramref name="matchByte"/>, the probabilities for that bit value are used; after the first difference, the
    /// plain ones. <paramref name="probabilities"/> are the 0x300 of the literal's context.
    /// </summary>
    public uint DecodeMatchedLiteral(Span<ushort> probabilities, uint matchByte)
    {
        var range = _range;
        var code = _code;
        uint symbol = 1;
        var offset = 0x100u;
        while (symbol < 0x100)
        {
            matchByte <<= 1;
            var index = offset + (matchByte & offset) + symbol;
            ref var probability = ref probabilities[(int)index];
            var p = probability;
            var bound = (range >> RangeCoder.ProbabilityBits) * p;
            if (code < bound)
            {
                range = bound;
                probability = (ushort)(p + ((RangeCoder.ProbabilityOne - p) >> RangeCoder.MoveBits));
                symbol <<= 1;
                offset &= ~matchByte; // a 0 where the match byte has a 1: plain probabilities from here on
            }
            else
            {
                range -= bound;
                code -= bound;
                probability = (ushort)(p - (p >> RangeCoder.MoveBits));
                symbol = (symbol << 1) + 1;
                offset &= matchByte;
            }
            if (range < RangeCoder.TopValue)
            {
                range <<= 8;
                code = code << 8 | NextByte();
            }
        }
        _range = range;
        _code = code;
        return symbol - 0x100;
    }

    /// <summary>Like <see cref="DecodeTree"/>, least significant bit first.</summary>
    public uint DecodeReverseTree(Span<ushort> probabilities, int bits)
    {
        uint m = 1;
        uint symbol = 0;
        for (var i = 0; i < bits; i++)
        {
            var bit = (uint)DecodeBit(ref probabilities[(int)m]);
            m = (m << 1) + bit;
            symbol |= bit << i;
        }
        return symbol;
    }
}
