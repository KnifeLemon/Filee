// The LZMA range encoder, the mirror of RangeDecoder: a 33-bit low end (the extra bit is a carry into bytes already
// produced), a 32-bit range, and a byte held back plus a count of 0xFF bytes after it, because a later carry can still
// change them. Output goes to a byte array that holds one LZMA2 chunk.
//
// The multi-bit codings (trees, literals, direct bits) keep the low end and the range in locals for all their bits
// and code each bit without a branch on its value (the bits of a literal or a distance are close to random): a mask
// of the bit picks the new range and what the low end gains, as in the decoder.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Filee.Engines.Archives.Lzma;

/// <summary>Encodes bits into range-coded output.</summary>
internal sealed class RangeEncoder(int capacity)
{
    private readonly byte[] _buffer = new byte[capacity];
    private int _count;
    private ulong _low;
    private uint _range = 0xFFFFFFFF;
    private byte _cache;
    private long _cacheSize = 1;

    /// <summary>The bytes written so far (all of them after <see cref="Flush"/>).</summary>
    public ReadOnlySpan<byte> Output => _buffer.AsSpan(0, _count);

    /// <summary>The output size if <see cref="Flush"/> were called now.</summary>
    public long FlushedSize => _count + _cacheSize + 4;

    /// <summary>Starts a new range coder stream (each LZMA2 chunk has its own).</summary>
    public void Reset()
    {
        _count = 0;
        _low = 0;
        _range = 0xFFFFFFFF;
        _cache = 0;
        _cacheSize = 1;
    }

    /// <summary>Writes out everything still held in the coder.</summary>
    public void Flush()
    {
        for (var i = 0; i < 5; i++)
            ShiftLow();
    }

    private void ShiftLow() => _low = ShiftLow(_low);

    /// <summary>Moves the top byte of <paramref name="low"/> out (held back while a carry may still reach it); returns the new low end.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private ulong ShiftLow(ulong low)
    {
        if ((uint)low < 0xFF000000u || (low >> 32) != 0)
        {
            var carry = (byte)(low >> 32);
            var value = _cache;
            do
            {
                _buffer[_count++] = (byte)(value + carry);
                value = 0xFF;
            } while (--_cacheSize != 0);
            _cache = (byte)((uint)low >> 24);
        }
        _cacheSize++;
        return (low & 0x00FFFFFF) << 8;
    }

    /// <summary>One bit with the adaptive probability <paramref name="probability"/>, on the caller's locals, without a branch on the bit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Bit(ref ushort probability, uint bit, ref ulong low, ref uint range)
    {
        uint p = probability;
        var bound = (range >> RangeCoder.ProbabilityBits) * p;
        var one = 0 - bit;
        low += bound & one;
        range = bound + ((range - 2 * bound) & one);
        // p + ((2048 − p) >> 5) for a 0 and p − (p >> 5) for a 1, as in the decoder.
        probability = (ushort)(p - (uint)((int)(p - (~one & (RangeCoder.ProbabilityOne - (1 << RangeCoder.MoveBits) + 1))) >> RangeCoder.MoveBits));
        if (range < RangeCoder.TopValue)
        {
            range <<= 8;
            low = ShiftLow(low);
        }
    }

    /// <summary>A literal (8 bits through the tree at <paramref name="probabilities"/>, most significant first).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EncodeLiteral(ref ushort probabilities, uint symbol)
    {
        var low = _low;
        var range = _range;
        uint m = 1;
        for (var i = 7; i >= 0; i--)
        {
            var bit = (symbol >> i) & 1;
            Bit(ref Unsafe.Add(ref probabilities, m), bit, ref low, ref range);
            m = (m << 1) | bit;
        }
        _low = low;
        _range = range;
    }

    /// <summary>
    /// A literal after a match: while its bits agree with <paramref name="matchByte"/>'s, the probabilities picked by
    /// that byte's bit (0x100 or 0x200 on) are used, after the first difference the plain ones.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EncodeMatchedLiteral(ref ushort probabilities, uint symbol, uint matchByte)
    {
        var low = _low;
        var range = _range;
        uint m = 1;
        var offset = 0x100u; // 0x100 while the bits agree, 0 after
        for (var i = 7; i >= 0; i--)
        {
            matchByte <<= 1;
            var matchBit = matchByte & 0x100 & offset; // 0x100 or 0
            var bit = (symbol >> i) & 1;
            Bit(ref Unsafe.Add(ref probabilities, offset + matchBit + m), bit, ref low, ref range);
            m = (m << 1) | bit;
            // The bits still agree when this one equals the match byte's.
            offset &= ((matchBit >> 8) ^ bit) - 1;
        }
        _low = low;
        _range = range;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EncodeBit(ref ushort probability, int bit)
    {
        var p = probability;
        var bound = (_range >> RangeCoder.ProbabilityBits) * p;
        if (bit == 0)
        {
            _range = bound;
            probability = (ushort)(p + ((RangeCoder.ProbabilityOne - p) >> RangeCoder.MoveBits));
        }
        else
        {
            _low += bound;
            _range -= bound;
            probability = (ushort)(p - (p >> RangeCoder.MoveBits));
        }
        while (_range < RangeCoder.TopValue)
        {
            _range <<= 8;
            ShiftLow();
        }
    }

    /// <summary>The low <paramref name="count"/> bits of <paramref name="value"/> at probability one half, most significant first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void EncodeDirectBits(uint value, int count)
    {
        var low = _low;
        var range = _range;
        while (count-- > 0)
        {
            range >>= 1;
            low += range & (0 - ((value >> count) & 1));
            if (range < RangeCoder.TopValue)
            {
                range <<= 8;
                low = ShiftLow(low);
            }
        }
        _low = low;
        _range = range;
    }

    /// <summary>A <paramref name="bits"/>-bit symbol through a binary tree of probabilities, most significant bit first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void EncodeTree(Span<ushort> probabilities, int bits, uint symbol)
    {
        if (probabilities.Length < 1 << bits)
            throw new ArgumentOutOfRangeException(nameof(probabilities));
        ref var probs = ref MemoryMarshal.GetReference(probabilities);
        var low = _low;
        var range = _range;
        uint m = 1;
        for (var i = bits - 1; i >= 0; i--)
        {
            var bit = (symbol >> i) & 1;
            Bit(ref Unsafe.Add(ref probs, m), bit, ref low, ref range);
            m = (m << 1) | bit;
        }
        _low = low;
        _range = range;
    }

    /// <summary>Like <see cref="EncodeTree"/>, least significant bit first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void EncodeReverseTree(Span<ushort> probabilities, int bits, uint symbol)
    {
        if (probabilities.Length < 1 << bits)
            throw new ArgumentOutOfRangeException(nameof(probabilities));
        ref var probs = ref MemoryMarshal.GetReference(probabilities);
        var low = _low;
        var range = _range;
        uint m = 1;
        for (var i = 0; i < bits; i++)
        {
            var bit = symbol & 1;
            symbol >>= 1;
            Bit(ref Unsafe.Add(ref probs, m), bit, ref low, ref range);
            m = (m << 1) | bit;
        }
        _low = low;
        _range = range;
    }
}

/// <summary>
/// Approximate cost of coding a bit, in 1/16 bit: −log2 of the probability of that bit, looked up by the top 7 bits
/// of the probability. Used by the encoder to compare a literal, a short repeat or a match.
/// </summary>
internal static class BitPrice
{
    public const int Shift = 4;
    private static readonly int[] Prices = CreatePrices();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Of(ushort probability, int bit) =>
        Prices[(bit == 0 ? probability : RangeCoder.ProbabilityOne - probability) >> Shift];

    public static int Tree(ReadOnlySpan<ushort> probabilities, int bits, uint symbol)
    {
        var price = 0;
        uint m = 1;
        for (var i = bits - 1; i >= 0; i--)
        {
            var bit = (int)((symbol >> i) & 1);
            price += Of(probabilities[(int)m], bit);
            m = (m << 1) | (uint)bit;
        }
        return price;
    }

    public static int ReverseTree(ReadOnlySpan<ushort> probabilities, int bits, uint symbol)
    {
        var price = 0;
        uint m = 1;
        for (var i = 0; i < bits; i++)
        {
            var bit = (int)(symbol & 1);
            symbol >>= 1;
            price += Of(probabilities[(int)m], bit);
            m = (m << 1) | (uint)bit;
        }
        return price;
    }

    private static int[] CreatePrices()
    {
        var prices = new int[(RangeCoder.ProbabilityOne >> Shift) + 1];
        for (var i = 1; i < prices.Length; i++)
        {
            var probability = (i << Shift) / (double)RangeCoder.ProbabilityOne;
            prices[i] = (int)Math.Round(-Math.Log2(Math.Min(1, probability)) * 16);
        }
        prices[0] = prices[1] + 16;
        return prices;
    }
}
