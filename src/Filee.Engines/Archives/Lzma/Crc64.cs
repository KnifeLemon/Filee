// CRC-64 as the .xz format uses it (ECMA-182 polynomial, reflected: 0xC96C5795D7870F42, initial and final value all
// ones). System.IO.Hashing's Crc64 is the unreflected variant, so it does not give the same value.

namespace Filee.Engines.Archives.Lzma;

/// <summary>Incremental CRC-64 of the .xz check field.</summary>
internal sealed class Crc64
{
    private const ulong Polynomial = 0xC96C5795D7870F42;

    // Four tables, so four bytes are folded in per step.
    private static readonly ulong[][] Tables = CreateTables();

    private ulong _crc = ulong.MaxValue;

    /// <summary>The checksum of everything passed to <see cref="Append"/> so far.</summary>
    public ulong Value => ~_crc;

    public void Append(ReadOnlySpan<byte> data)
    {
        var crc = _crc;
        var (t0, t1, t2, t3) = (Tables[0], Tables[1], Tables[2], Tables[3]);
        var i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            var word = (uint)crc ^ (data[i] | (uint)data[i + 1] << 8 | (uint)data[i + 2] << 16 | (uint)data[i + 3] << 24);
            crc = t3[word & 0xFF] ^ t2[(word >> 8) & 0xFF] ^ t1[(word >> 16) & 0xFF] ^ t0[word >> 24] ^ (crc >> 32);
        }
        for (; i < data.Length; i++)
            crc = t0[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        _crc = crc;
    }

    /// <summary>CRC-64 of <paramref name="data"/>.</summary>
    public static ulong Compute(ReadOnlySpan<byte> data)
    {
        var crc = new Crc64();
        crc.Append(data);
        return crc.Value;
    }

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
