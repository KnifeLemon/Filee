// CRC-32 (IEEE 802.3, as in ZIP, gzip, ALZ, EGG and lzip) for checking what Filee's own archive readers unpack.
// System.IO.Hashing computes it with the CPU's carry-less multiply or CRC instructions where there are any (x64,
// ARM64) and with lookup tables elsewhere; this class only keeps the small API the readers and writers use.

namespace Filee.Engines.Archives;

/// <summary>Incremental CRC-32 with the reflected polynomial 0xEDB88320.</summary>
internal sealed class Crc32
{
    private readonly System.IO.Hashing.Crc32 _crc = new();

    /// <summary>The checksum of everything passed to <see cref="Append"/> so far.</summary>
    public uint Value => _crc.GetCurrentHashAsUInt32();

    public void Append(ReadOnlySpan<byte> data) => _crc.Append(data);

    /// <summary>CRC-32 of <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => System.IO.Hashing.Crc32.HashToUInt32(data);
}
