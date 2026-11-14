// The LZMA range encoder, the mirror of RangeDecoder: a 33-bit low end (the extra bit is a carry into bytes already
// produced), a 32-bit range, and a byte held back plus a count of 0xFF bytes after it, because a later carry can still
// change them. Output goes to a byte array that holds one LZMA2 chunk.

using System.Runtime.CompilerServices;

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

    private void ShiftLow()
    {
        if ((uint)_low < 0xFF000000u || (_low >> 32) != 0)
        {
            var carry = (byte)(_low >> 32);
            var value = _cache;
            do
            {
                _buffer[_count++] = (byte)(value + carry);
                value = 0xFF;
            } while (--_cacheSize != 0);
            _cache = (byte)((uint)_low >> 24);
        }
        _cacheSize++;
        _low = (_low & 0x00FFFFFF) << 8;
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
    public void EncodeDirectBits(uint value, int count)
    {
        while (count-- > 0)
        {
            _range >>= 1;
            if (((value >> count) & 1) != 0)
                _low += _range;
            while (_range < RangeCoder.TopValue)
            {
                _range <<= 8;
                ShiftLow();
            }
        }
    }

    /// <summary>A <paramref name="bits"/>-bit symbol through a binary tree of probabilities, most significant bit first.</summary>
    public void EncodeTree(Span<ushort> probabilities, int bits, uint symbol)
    {
        uint m = 1;
        for (var i = bits - 1; i >= 0; i--)
        {
            var bit = (int)((symbol >> i) & 1);
            EncodeBit(ref probabilities[(int)m], bit);
            m = (m << 1) | (uint)bit;
        }
    }

    /// <summary>Like <see cref="EncodeTree"/>, least significant bit first.</summary>
    public void EncodeReverseTree(Span<ushort> probabilities, int bits, uint symbol)
    {
        uint m = 1;
        for (var i = 0; i < bits; i++)
        {
            var bit = (int)(symbol & 1);
            symbol >>= 1;
            EncodeBit(ref probabilities[(int)m], bit);
            m = (m << 1) | (uint)bit;
        }
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
