// Constants and small codings of The .xz File Format 1.2.1 shared by XzReader and XzWriter.

namespace Filee.Engines.Archives.Lzma;

internal static class XzFormat
{
    public static readonly byte[] HeaderMagic = [0xFD, (byte)'7', (byte)'z', (byte)'X', (byte)'Z', 0x00];
    public static readonly byte[] FooterMagic = [(byte)'Y', (byte)'Z'];

    public const long Lzma2FilterId = 0x21;

    public const int CheckNone = 0;
    public const int CheckCrc32 = 1;
    public const int CheckCrc64 = 4;
    public const int CheckSha256 = 10;

    /// <summary>Bytes of the check field for a check type (0 … 15): 0, 4, 8, 16, 32 or 64.</summary>
    public static int CheckSize(int type) => type == 0 ? 0 : 4 << ((type - 1) / 3);

    /// <summary>
    /// The dictionary size an LZMA2 properties byte stands for: 2^(n/2 + 12) for even n, 3 × 2^((n-1)/2 + 11) for odd n,
    /// 40 for 4 GiB − 1. Null for values past 40 or with the reserved high bits set.
    /// </summary>
    public static uint? DictionaryFromProperty(byte value)
    {
        if (value > 40)
            return null;
        if (value == 40)
            return uint.MaxValue;
        return (2u | (value & 1u)) << (value / 2 + 11);
    }

    /// <summary>The properties byte of the smallest LZMA2 dictionary size that is at least <paramref name="dictionary"/>.</summary>
    public static byte PropertyFor(uint dictionary)
    {
        for (byte value = 0; value < 40; value++)
            if (DictionaryFromProperty(value) >= dictionary)
                return value;
        return 40;
    }

    /// <summary>Writes a .xz variable-length integer (7 bits per byte, low first); returns the bytes used.</summary>
    public static int WriteVarint(Span<byte> target, ulong value)
    {
        var i = 0;
        while (value >= 0x80)
        {
            target[i++] = (byte)(value | 0x80);
            value >>= 7;
        }
        target[i++] = (byte)value;
        return i;
    }
}
