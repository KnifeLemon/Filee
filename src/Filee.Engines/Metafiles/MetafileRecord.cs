// One record of an EMF or WMF file as the players read it: little-endian fields at byte offsets from the start of the
// record, each read checked against the record's length. A field outside the record means the file is damaged, and
// the error names the record, its type and where it starts, so a report can say which record was wrong.

using System.Buffers.Binary;

namespace Filee.Engines.Metafiles;

/// <summary>The bytes of one record and where it came from.</summary>
internal readonly ref struct MetafileRecord
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly string _format;

    /// <param name="data">The whole record, its type and size fields included.</param>
    /// <param name="format">"EMF" or "WMF", for messages.</param>
    /// <param name="type">The record type (EMF) or function (WMF).</param>
    /// <param name="index">The record's number in the file, from 0 (the header).</param>
    /// <param name="offset">Where the record starts in the file.</param>
    public MetafileRecord(ReadOnlySpan<byte> data, string format, int type, int index, long offset)
    {
        _data = data;
        _format = format;
        Type = type;
        Index = index;
        Offset = offset;
    }

    public int Type { get; }

    public int Index { get; }

    public long Offset { get; }

    public int Length => _data.Length;

    public int I32(int at) => BinaryPrimitives.ReadInt32LittleEndian(Need(at, 4));

    public uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(Need(at, 4));

    public short I16(int at) => BinaryPrimitives.ReadInt16LittleEndian(Need(at, 2));

    public ushort U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(Need(at, 2));

    public byte U8(int at) => Need(at, 1)[0];

    public float F32(int at) => BinaryPrimitives.ReadSingleLittleEndian(Need(at, 4));

    /// <summary><paramref name="length"/> bytes from <paramref name="at"/>.</summary>
    public ReadOnlySpan<byte> Bytes(int at, long length)
    {
        if (at < 0 || length < 0 || at + length > _data.Length)
            throw Damaged($"holds {length:N0} bytes at {at}, more than its {_data.Length:N0} bytes");
        return _data.Slice(at, (int)length);
    }

    /// <summary>The bytes an offset / size pair of the record points to (EMF bitmaps and strings).</summary>
    public ReadOnlySpan<byte> At(uint offset, uint size)
    {
        if (offset > _data.Length || size > _data.Length - offset)
            throw Damaged($"points to {size:N0} bytes at {offset:N0}, outside its {_data.Length:N0} bytes");
        return _data.Slice((int)offset, (int)size);
    }

    /// <summary>Checks that <paramref name="count"/> items of <paramref name="size"/> bytes fit from <paramref name="at"/>.</summary>
    public int Count(long count, int size, int at)
    {
        if (count < 0 || at > _data.Length || count * size > _data.Length - at)
            throw Damaged($"claims {count:N0} items, more than its {_data.Length:N0} bytes hold");
        return (int)count;
    }

    /// <summary>The error for this record: "The EMF file is damaged: record 12 (type 84 at byte 3,456) …".</summary>
    public InvalidDataException Damaged(string what) =>
        new($"The {_format} file is damaged: record {Index} (type 0x{Type:X}, at byte {Offset:N0}) {what}.");

    private ReadOnlySpan<byte> Need(int at, int size)
    {
        if (at < 0 || at + size > _data.Length)
            throw Damaged($"is {_data.Length} bytes long, too short for its fields");
        return _data.Slice(at, size);
    }
}
