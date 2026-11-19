// CRC-64 as the .xz format uses it (ECMA-182 polynomial, reflected: 0xC96C5795D7870F42, initial and final value all
// ones). System.IO.Hashing's Crc64 is the unreflected variant, so it does not give the same value.
//
// Long inputs are folded 16 bytes at a time with the processor's carry-less multiply (x64 PCLMULQDQ), the way CRCs are
// usually computed at speed: in the reflected bit order a 16-byte piece stands for a polynomial of degree below 128
// (the first byte's lowest bit the highest power), and the piece so far times x^128, plus the next piece, is the same
// modulo the polynomial as its low half times (x^192 mod P) and its high half times (x^128 mod P), plus the next
// piece. A carry-less product of two reflected 64-bit values comes out one place short of the 128-bit order, so the
// constants used are those powers divided by x: x^191 and x^127 mod P. What is left at the end (one 16-byte piece,
// whose CRC from a zero register is the folded state's, and the last 0 … 15 bytes) goes through the tables. The
// constants are worked out once from the polynomial. Elsewhere (and for short inputs) the tables do all of it.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Filee.Engines.Archives.Lzma;

/// <summary>Incremental CRC-64 of the .xz check field.</summary>
internal sealed class Crc64
{
    private const ulong Polynomial = 0xC96C5795D7870F42;

    /// <summary>Inputs shorter than this go through the tables only.</summary>
    private const int FoldFrom = 64;

    // Four tables, so four bytes are folded in per step.
    private static readonly ulong[][] Tables = CreateTables();

    // x^191 mod P (for the low half) and x^127 mod P (for the high half), reflected.
    private static readonly Vector128<ulong> FoldConstants = Vector128.Create(PowerOfX(191), PowerOfX(127));

    private ulong _crc = ulong.MaxValue;

    /// <summary>The checksum of everything passed to <see cref="Append"/> so far.</summary>
    public ulong Value => ~_crc;

    public void Append(ReadOnlySpan<byte> data)
    {
        var crc = _crc;
        if (data.Length >= FoldFrom && Pclmulqdq.IsSupported)
        {
            var whole = data.Length & ~15;
            crc = Fold(crc, data[..whole]);
            data = data[whole..];
        }
        _crc = Update(crc, data);
    }

    /// <summary>CRC-64 of <paramref name="data"/>.</summary>
    public static ulong Compute(ReadOnlySpan<byte> data)
    {
        var crc = new Crc64();
        crc.Append(data);
        return crc.Value;
    }

    /// <summary>The register after <paramref name="data"/> (a multiple of 16 bytes, 32 or more), by folding.</summary>
    private static ulong Fold(ulong crc, ReadOnlySpan<byte> data)
    {
        ref var at = ref MemoryMarshal.GetReference(data);
        // The register so far goes into the first eight bytes (the same as starting from zero with them changed).
        var state = Vector128.LoadUnsafe(ref at) ^ Vector128.CreateScalar(crc).AsByte();
        var constants = FoldConstants;
        for (var offset = 16; offset < data.Length; offset += 16)
        {
            var low = Pclmulqdq.CarrylessMultiply(state.AsUInt64(), constants, 0x00);
            var high = Pclmulqdq.CarrylessMultiply(state.AsUInt64(), constants, 0x11);
            state = (low ^ high).AsByte() ^ Vector128.LoadUnsafe(ref at, (nuint)offset);
        }
        Span<byte> last = stackalloc byte[16];
        state.CopyTo(last);
        return Update(0, last);
    }

    /// <summary>The register after <paramref name="data"/>, four bytes per step through the tables.</summary>
    private static ulong Update(ulong crc, ReadOnlySpan<byte> data)
    {
        var (t0, t1, t2, t3) = (Tables[0], Tables[1], Tables[2], Tables[3]);
        var i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            var word = (uint)crc ^ BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
            crc = t3[word & 0xFF] ^ t2[(word >> 8) & 0xFF] ^ t1[(word >> 16) & 0xFF] ^ t0[word >> 24] ^ (crc >> 32);
        }
        for (; i < data.Length; i++)
            crc = t0[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    /// <summary>x^<paramref name="n"/> mod P, reflected (bit i stands for x^(63 − i)).</summary>
    private static ulong PowerOfX(int n)
    {
        var value = 1UL << 63; // x^0
        for (var i = 0; i < n; i++)
            value = MultiplyByX(value);
        return value;
    }

    /// <summary>Times x, mod P, in the reflected order: x^63 (bit 0) becomes x^64, which is P less its top term.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong MultiplyByX(ulong value) => (value >> 1) ^ ((value & 1) != 0 ? Polynomial : 0);

    private static ulong[][] CreateTables()
    {
        var tables = new ulong[4][];
        tables[0] = new ulong[256];
        for (ulong i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? Polynomial ^ (c >> 1) : c >> 1;
            tables[0][i] = c;
        }
        // Table n: the CRC of a byte followed by n zero bytes.
        for (var n = 1; n < 4; n++)
        {
            tables[n] = new ulong[256];
            for (var i = 0; i < 256; i++)
                tables[n][i] = tables[0][tables[n - 1][i] & 0xFF] ^ (tables[n - 1][i] >> 8);
        }
        return tables;
    }
}
