// BZZ, DjVu's general-purpose compression (DjVu v3 specification, "BZZ"): the data in blocks of at most 4 MB, each a
// Burrows-Wheeler transform whose output is coded as move-to-front ranks with the ZP-coder. TXTz (hidden text), the
// colour indices of FGbz, the DIRM directory's tables, ANTz and NAVM are BZZ streams.
//
// A block is its size (24 raw bits; 0 ends the stream), the speed of the rank estimator (0-2), then one rank per byte
// (the end-of-block marker is the 256th rank). The contexts of the rank decisions carry on from block to block; the
// move-to-front list and its frequencies start again in each one.

namespace Filee.Engines.Djvu;

/// <summary>Unpacks BZZ streams.</summary>
internal static class BzzDecoder
{
    /// <summary>Largest block the format allows.</summary>
    private const int MaxBlock = 4096 * 1024;

    private const int FrequencyCount = 4;
    private const int ContextIds = 3;

    /// <summary>Unpacks <paramref name="data"/>; refuses one that unpacks to more than <paramref name="maxBytes"/>.</summary>
    public static byte[] Decode(byte[] data, long maxBytes, CancellationToken cancellationToken = default) =>
        Decode(data, 0, data.Length, maxBytes, cancellationToken);

    public static byte[] Decode(byte[] data, int offset, int count, long maxBytes, CancellationToken cancellationToken = default)
    {
        var zp = new ZpDecoder(data, offset, count);
        var contexts = new byte[300];
        using var output = new MemoryStream();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var size = DecodeRaw(zp, 24);
            if (size == 0)
                break;
            if (size > MaxBlock)
                throw new InvalidDataException("A BZZ block is larger than the format allows.");
            if (output.Length + size - 1 > maxBytes)
                throw Documents.DocumentLimits.Exceeded($"A compressed part of the DjVu file unpacks to more than {Documents.DocumentLimits.Megabytes(maxBytes)}.");
            var block = DecodeBlock(zp, contexts, size, cancellationToken);
            output.Write(block);
        }
        return output.ToArray();
    }

    private static int DecodeRaw(ZpDecoder zp, int bits)
    {
        var n = 1;
        var m = 1 << bits;
        while (n < m)
            n = (n << 1) | zp.DecodeRaw();
        return n - m;
    }

    /// <summary>A binary number of <paramref name="bits"/> bits, a context per node of its tree from <paramref name="first"/>.</summary>
    private static int DecodeBinary(ZpDecoder zp, byte[] contexts, int first, int bits)
    {
        var n = 1;
        var m = 1 << bits;
        while (n < m)
            n = (n << 1) | zp.Decode(ref contexts[first + n - 1]);
        return n - m;
    }

    /// <summary>One block, its marker removed and the transform undone.</summary>
    private static byte[] DecodeBlock(ZpDecoder zp, byte[] contexts, int size, CancellationToken cancellationToken)
    {
        var data = new byte[size];
        var fshift = 0;
        if (zp.DecodeRaw() != 0)
        {
            fshift++;
            if (zp.DecodeRaw() != 0)
                fshift++;
        }
        var mtf = new byte[256];
        for (var i = 0; i < 256; i++)
            mtf[i] = (byte)i;
        var freq = new uint[FrequencyCount];
        uint fadd = 4;
        var mtfno = 3;
        var marker = -1;
        for (var i = 0; i < size; i++)
        {
            if ((i & 0xFFFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var ctxid = Math.Min(ContextIds - 1, mtfno);
            if (zp.Decode(ref contexts[ctxid]) != 0)
                mtfno = 0;
            else if (zp.Decode(ref contexts[ContextIds + ctxid]) != 0)
                mtfno = 1;
            else
            {
                mtfno = -1;
                // Ranks 2-3, 4-7, ... 128-255: a flag, then the rank's low bits as a binary number.
                var at = 2 * ContextIds;
                for (var bits = 1; bits <= 7; bits++)
                {
                    if (zp.Decode(ref contexts[at]) != 0)
                    {
                        mtfno = (1 << bits) + DecodeBinary(zp, contexts, at + 1, bits);
                        break;
                    }
                    at += 1 << bits;
                }
                if (mtfno < 0)
                {
                    mtfno = 256;
                    if (marker >= 0)
                        throw new InvalidDataException("A BZZ block has two end markers.");
                    marker = i;
                    continue;
                }
            }
            var c = mtf[mtfno];
            data[i] = c;
            // The rank list is ordered by how often each byte came lately, not strictly move-to-front.
            fadd += fadd >> fshift;
            if (fadd > 0x10000000)
            {
                fadd >>= 24;
                for (var k = 0; k < FrequencyCount; k++)
                    freq[k] >>= 24;
            }
            var fc = fadd;
            if (mtfno < FrequencyCount)
                fc += freq[mtfno];
            int at2;
            for (at2 = mtfno; at2 >= FrequencyCount; at2--)
                mtf[at2] = mtf[at2 - 1];
            for (; at2 > 0 && fc >= freq[at2 - 1]; at2--)
            {
                mtf[at2] = mtf[at2 - 1];
                freq[at2] = freq[at2 - 1];
            }
            mtf[at2] = c;
            freq[at2] = fc;
        }
        if (marker < 1 || marker >= size)
            throw new InvalidDataException("A BZZ block's end marker is missing or misplaced.");
        return Untransform(data, size, marker);
    }

    /// <summary>Undoes the Burrows-Wheeler transform of a block whose marker is at <paramref name="marker"/>.</summary>
    private static byte[] Untransform(byte[] data, int size, int marker)
    {
        var position = new int[size];
        var count = new int[256];
        for (var i = 0; i < size; i++)
        {
            if (i == marker)
                continue;
            var c = data[i];
            position[i] = (c << 24) | (count[c] & 0xFFFFFF);
            count[c]++;
        }
        // Row 0 is the rotation that starts with the marker, which sorts before every byte.
        var last = 1;
        for (var i = 0; i < 256; i++)
        {
            var n = count[i];
            count[i] = last;
            last += n;
        }
        var result = new byte[size - 1];
        var row = 0;
        for (var at = size - 2; at >= 0; at--)
        {
            var n = position[row];
            var c = (byte)((uint)n >> 24);
            result[at] = c;
            row = count[c] + (n & 0xFFFFFF);
            if ((uint)row >= (uint)size)
                throw new InvalidDataException("A BZZ block is damaged.");
        }
        if (row != marker)
            throw new InvalidDataException("A BZZ block is damaged.");
        return result;
    }
}
