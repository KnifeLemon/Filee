// INDX records of Kindle books, the writing side of MobiIndex: a primary record with the tag table (TAGX) and the
// last key of each data record, data records whose entries are a key, one control byte and the tag values as forward
// variable-width numbers (located through an IDXT table of offsets), and CNCX records with the strings the entries
// point at. Written from the MobileRead wiki's description of the format; MobiIndex reads what this writes.

using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Filee.Engines.Ebooks;

/// <summary>One tag of an index: its number, values per group and its bits in the control byte.</summary>
internal readonly record struct IndexTag(int Tag, int ValuesPerEntry, int Mask);

/// <summary>An index entry: a key and the values of its tags (a multiple of the tag's values per group).</summary>
internal sealed record IndexEntry(string Key, IReadOnlyDictionary<int, long[]> Values);

internal static class MobiIndexWriter
{
    private const int HeaderLength = 0xC0;

    /// <summary>Data records stop growing here, so the 16-bit IDXT offsets always fit.</summary>
    private const int MaxRecord = 0xF000;

    /// <summary>The records of an index: primary, data records, then the CNCX records of <paramref name="strings"/>.</summary>
    public static List<byte[]> Write(IReadOnlyList<IndexTag> tags, IReadOnlyList<IndexEntry> entries, CncxWriter? strings = null)
    {
        var data = new List<(byte[] Record, string LastKey, int Count)>();
        var body = new MemoryStream();
        var offsets = new List<int>();
        string? lastKey = null;
        void Flush()
        {
            if (offsets.Count == 0)
                return;
            data.Add((DataRecord(body.ToArray(), offsets), lastKey!, offsets.Count));
            body = new MemoryStream();
            offsets = [];
        }
        foreach (var entry in entries)
        {
            var encoded = Entry(entry, tags);
            if (HeaderLength + body.Length + encoded.Length + 8 + (offsets.Count + 1) * 2 > MaxRecord)
                Flush();
            offsets.Add(HeaderLength + (int)body.Length);
            body.Write(encoded);
            lastKey = entry.Key;
        }
        Flush();

        var cncx = strings?.Records() ?? [];
        var records = new List<byte[]> { Primary(tags, data, entries.Count, cncx.Count) };
        records.AddRange(data.Select(d => d.Record));
        records.AddRange(cncx);
        return records;
    }

    private static byte[] Entry(IndexEntry entry, IReadOnlyList<IndexTag> tags)
    {
        var output = new MemoryStream();
        var key = Encoding.ASCII.GetBytes(entry.Key);
        output.WriteByte((byte)key.Length);
        output.Write(key);
        var control = 0;
        var values = new MemoryStream();
        foreach (var tag in tags)
        {
            if (!entry.Values.TryGetValue(tag.Tag, out var tagValues) || tagValues.Length == 0)
                continue;
            var groups = tagValues.Length / tag.ValuesPerEntry;
            var shift = BitOperations.TrailingZeroCount(tag.Mask);
            if (groups << shift > tag.Mask || BitOperations.PopCount((uint)tag.Mask) == 1 && groups != 1 || groups << shift == tag.Mask && BitOperations.PopCount((uint)tag.Mask) > 1)
                throw new InvalidOperationException($"Tag {tag.Tag} cannot hold {groups} value groups.");
            control |= groups << shift;
            foreach (var value in tagValues)
                values.Write(Varint(value));
        }
        output.WriteByte((byte)control);
        values.WriteTo(output);
        return output.ToArray();
    }

    /// <summary>A forward variable-width number: 7 bits per byte, most significant first, the last byte has the high bit set.</summary>
    internal static byte[] Varint(long value)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value));
        var bytes = new List<byte> { (byte)(0x80 | (value & 0x7F)) };
        for (value >>= 7; value > 0; value >>= 7)
            bytes.Insert(0, (byte)(value & 0x7F));
        return [.. bytes];
    }

    private static byte[] DataRecord(byte[] body, List<int> offsets)
    {
        var record = new MemoryStream();
        var header = new byte[HeaderLength];
        "INDX"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), HeaderLength);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), 1);
        var idxt = HeaderLength + body.Length;
        idxt += idxt % 2;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), (uint)idxt);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), (uint)offsets.Count);
        for (var o = 28; o < 36; o += 4)
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(o), uint.MaxValue);
        record.Write(header);
        record.Write(body);
        if (record.Length % 2 == 1)
            record.WriteByte(0);
        record.Write("IDXT"u8);
        foreach (var offset in offsets)
            record.Write([(byte)(offset >> 8), (byte)offset]);
        Pad(record);
        return record.ToArray();
    }

    private static byte[] Primary(IReadOnlyList<IndexTag> tags, List<(byte[] Record, string LastKey, int Count)> data, int total, int cncxCount)
    {
        var record = new MemoryStream();
        var header = new byte[HeaderLength];
        "INDX"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), HeaderLength);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), (uint)data.Count);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(28), 65001);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(32), uint.MaxValue);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(36), (uint)total);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(52), (uint)cncxCount);
        record.Write(header);

        // TAGX: the tags with one control byte, then the end marker.
        record.Write("TAGX"u8);
        record.Write(Be32(12 + 4 * (tags.Count + 1)));
        record.Write(Be32(1));
        foreach (var tag in tags)
            record.Write([(byte)tag.Tag, (byte)tag.ValuesPerEntry, (byte)tag.Mask, 0]);
        record.Write([0, 0, 0, 1]);

        // The last key and entry count of each data record.
        var offsets = new List<int>();
        foreach (var (_, lastKey, count) in data)
        {
            offsets.Add((int)record.Length);
            var key = Encoding.ASCII.GetBytes(lastKey);
            record.WriteByte((byte)key.Length);
            record.Write(key);
            record.Write([(byte)(count >> 8), (byte)count]);
        }
        var idxt = (int)record.Length;
        if (idxt % 2 == 1)
        {
            record.WriteByte(0);
            idxt++;
        }
        var bytes = record.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), (uint)idxt);
        record = new MemoryStream();
        record.Write(bytes);
        record.Write("IDXT"u8);
        foreach (var offset in offsets)
            record.Write([(byte)(offset >> 8), (byte)offset]);
        Pad(record);
        return record.ToArray();
    }

    private static void Pad(MemoryStream stream)
    {
        while (stream.Length % 4 != 0)
            stream.WriteByte(0);
    }

    private static byte[] Be32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }
}

/// <summary>The strings of an index (labels, selectors) in CNCX records; an offset is record × 0x10000 + position.</summary>
internal sealed class CncxWriter
{
    private const int MaxRecord = 0xFFF0;
    private readonly List<MemoryStream> _records = [new MemoryStream()];
    private readonly Dictionary<string, long> _known = new(StringComparer.Ordinal);

    public long Add(string text)
    {
        if (_known.TryGetValue(text, out var known))
            return known;
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 500)
            bytes = Encoding.UTF8.GetBytes(text[..Math.Min(text.Length, 120)]);
        var length = MobiIndexWriter.Varint(bytes.Length);
        if (_records[^1].Length + length.Length + bytes.Length > MaxRecord)
            _records.Add(new MemoryStream());
        var offset = (_records.Count - 1) * 0x10000L + _records[^1].Length;
        _records[^1].Write(length);
        _records[^1].Write(bytes);
        _known[text] = offset;
        return offset;
    }

    public List<byte[]> Records()
    {
        if (_records.Count == 1 && _records[0].Length == 0)
            return [];
        return _records.Select(r =>
        {
            while (r.Length % 4 != 0)
                r.WriteByte(0);
            return r.ToArray();
        }).ToList();
    }
}
