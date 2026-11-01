// The Palm database (PDB) container of MOBI, AZW, AZW3, AZW4 and PRC books: a 78-byte header with the type and
// creator ("BOOKMOBI", "TEXtREAd"), then a table of record offsets. Written from the PalmOS database format
// description; all numbers are big-endian.

using System.Buffers.Binary;
using System.Text;

namespace Filee.Engines.Ebooks;

internal sealed class PalmDatabase
{
    private readonly byte[] _data;
    private readonly int[] _offsets;

    private PalmDatabase(byte[] data, string name, string typeCreator, int[] offsets)
    {
        _data = data;
        Name = name;
        TypeCreator = typeCreator;
        _offsets = offsets;
    }

    /// <summary>Database name from the header (a short title, often truncated).</summary>
    public string Name { get; }

    /// <summary>Type and creator, e.g. "BOOKMOBI" (Mobipocket / Kindle) or "TEXtREAd" (PalmDOC).</summary>
    public string TypeCreator { get; }

    public int Count => _offsets.Length;

    public static PalmDatabase Open(byte[] data)
    {
        if (data.Length < 78)
            throw new InvalidDataException("The file is too short to be an e-book.");
        var count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(76));
        if (count == 0 || 78 + count * 8 > data.Length)
            throw new InvalidDataException("The e-book's record table is damaged.");
        var offsets = new int[count];
        for (var i = 0; i < count; i++)
        {
            var offset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(78 + i * 8));
            if (offset > data.Length || (i > 0 && offset < offsets[i - 1]))
                throw new InvalidDataException("The e-book's record table is damaged.");
            offsets[i] = (int)offset;
        }
        var name = Encoding.Latin1.GetString(data, 0, 32).TrimEnd('\0');
        return new PalmDatabase(data, name, Encoding.ASCII.GetString(data, 60, 8), offsets);
    }

    /// <summary>Record <paramref name="index"/> (empty when out of range).</summary>
    public ReadOnlySpan<byte> Record(int index)
    {
        if (index < 0 || index >= _offsets.Length)
            return [];
        var end = index + 1 < _offsets.Length ? _offsets[index + 1] : _data.Length;
        return _data.AsSpan(_offsets[index], Math.Max(0, end - _offsets[index]));
    }

    public byte[] RecordArray(int index) => Record(index).ToArray();

    // Big-endian helpers with bounds checks: damaged books must fail with a clear error, not an exception deep inside.

    public static int U16(ReadOnlySpan<byte> data, int offset) =>
        offset >= 0 && offset + 2 <= data.Length ? BinaryPrimitives.ReadUInt16BigEndian(data[offset..]) : 0;

    /// <summary>A 32-bit field; 0xFFFFFFFF ("none" in MOBI headers) becomes -1.</summary>
    public static long U32(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset + 4 > data.Length)
            return -1;
        var value = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        return value == uint.MaxValue ? -1 : value;
    }
}

/// <summary>PalmDOC compression (a simple LZ77 variant used by MOBI and PalmDOC text records).</summary>
internal static class PalmDocCompression
{
    /// <summary>
    /// Compresses one text record (at most 4,096 bytes): back-references of 3 to 10 bytes up to 2,047 bytes back
    /// (found through a hash of the next three bytes), a space and an ASCII letter as one byte, literal bytes, and runs
    /// of up to eight bytes that need escaping (1 to 8, 0x80 and above).
    /// </summary>
    public static byte[] Compress(ReadOnlySpan<byte> input)
    {
        var output = new List<byte>(input.Length);
        var heads = new int[4096];
        Array.Fill(heads, -1);
        var previous = new int[input.Length];
        int Hash(ReadOnlySpan<byte> data, int at) => ((data[at] << 7) ^ (data[at + 1] << 3) ^ data[at + 2]) & 4095;
        void Insert(ReadOnlySpan<byte> data, int at)
        {
            if (at + 3 > data.Length)
                return;
            var hash = Hash(data, at);
            previous[at] = heads[hash];
            heads[hash] = at;
        }

        for (var i = 0; i < input.Length;)
        {
            var (bestLength, bestDistance) = (0, 0);
            if (i + 3 <= input.Length)
            {
                var candidate = heads[Hash(input, i)];
                for (var steps = 0; candidate >= 0 && i - candidate <= 2047 && steps < 64; steps++)
                {
                    var length = 0;
                    while (length < 10 && i + length < input.Length && input[candidate + length] == input[i + length])
                        length++;
                    if (length > bestLength)
                    {
                        (bestLength, bestDistance) = (length, i - candidate);
                        if (length == 10)
                            break;
                    }
                    candidate = previous[candidate];
                }
            }
            if (bestLength >= 3)
            {
                var pair = 0x8000 | (bestDistance << 3) | (bestLength - 3);
                output.Add((byte)(pair >> 8));
                output.Add((byte)pair);
                for (var k = 0; k < bestLength; k++)
                    Insert(input, i + k);
                i += bestLength;
                continue;
            }
            var c = input[i];
            if (c == ' ' && i + 1 < input.Length && input[i + 1] is >= 0x40 and <= 0x7F)
            {
                output.Add((byte)(input[i + 1] ^ 0x80));
                Insert(input, i);
                Insert(input, i + 1);
                i += 2;
            }
            else if (c is 0 or (>= 0x09 and <= 0x7F))
            {
                output.Add(c);
                Insert(input, i);
                i++;
            }
            else
            {
                var start = output.Count;
                output.Add(0);
                var count = 0;
                while (i < input.Length && count < 8 && input[i] is (>= 1 and <= 8) or >= 0x80)
                {
                    output.Add(input[i]);
                    Insert(input, i);
                    i++;
                    count++;
                }
                output[start] = (byte)count;
            }
        }
        return [.. output];
    }

    /// <summary>Decompresses one text record.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> input)
    {
        var output = new List<byte>(input.Length * 2);
        for (var i = 0; i < input.Length;)
        {
            var c = input[i++];
            if (c is >= 1 and <= 8)
            {
                // 1..8: that many literal bytes follow.
                for (var n = 0; n < c && i < input.Length; n++)
                    output.Add(input[i++]);
            }
            else if (c < 0x80)
            {
                output.Add(c); // 0 and 9..0x7F: the byte itself
            }
            else if (c >= 0xC0)
            {
                output.Add((byte)' '); // a space followed by an ASCII character
                output.Add((byte)(c ^ 0x80));
            }
            else if (i < input.Length)
            {
                // 0x80..0xBF: two bytes = 11-bit distance back and a length of 3..10.
                var pair = (c << 8) | input[i++];
                var distance = (pair >> 3) & 0x7FF;
                var length = (pair & 7) + 3;
                if (distance == 0 || distance > output.Count)
                    continue; // damaged: skip rather than fail the whole book
                var start = output.Count - distance;
                for (var n = 0; n < length; n++)
                    output.Add(output[start + n]);
            }
        }
        return [.. output];
    }
}
