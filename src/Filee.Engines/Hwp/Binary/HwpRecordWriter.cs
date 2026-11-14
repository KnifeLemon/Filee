// Records of an HWP 5.0 stream (DocInfo, BodyText/SectionN) as HwpFile reads them: a 32-bit header with the tag in bits
// 0-9, the level in bits 10-19 and the size in bits 20-31 (0xFFF and a 32-bit size after it for 4095 bytes and more),
// then the record's little-endian fields.

using System.Buffers.Binary;
using System.Text;

namespace Filee.Engines.Hwp.Binary;

/// <summary>The fields of one record, written in order.</summary>
internal sealed class RecordData
{
    private readonly MemoryStream _bytes = new();

    public int Length => (int)_bytes.Length;

    public RecordData U8(int value)
    {
        _bytes.WriteByte((byte)value);
        return this;
    }

    public RecordData I8(int value) => U8((sbyte)Math.Clamp(value, sbyte.MinValue, sbyte.MaxValue));

    public RecordData U16(int value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)value);
        _bytes.Write(b);
        return this;
    }

    public RecordData I16(int value) => U16((short)Math.Clamp(value, short.MinValue, short.MaxValue));

    public RecordData U32(uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        _bytes.Write(b);
        return this;
    }

    public RecordData I32(int value) => U32((uint)value);

    public RecordData F64(double value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(b, value);
        _bytes.Write(b);
        return this;
    }

    /// <summary>A string: a WORD length, then that many UTF-16 code units.</summary>
    public RecordData Text(string? text)
    {
        text ??= "";
        if (text.Length > ushort.MaxValue)
            text = text[..ushort.MaxValue];
        U16(text.Length);
        return Chars(text);
    }

    /// <summary>UTF-16 code units without a length.</summary>
    public RecordData Chars(string text)
    {
        _bytes.Write(Encoding.Unicode.GetBytes(text));
        return this;
    }

    public RecordData Bytes(ReadOnlySpan<byte> bytes)
    {
        _bytes.Write(bytes);
        return this;
    }

    public RecordData Zeros(int count)
    {
        for (var i = 0; i < count; i++)
            _bytes.WriteByte(0);
        return this;
    }

    public byte[] ToArray() => _bytes.ToArray();
}

/// <summary>The records of one stream, in order.</summary>
internal sealed class RecordStream
{
    private readonly MemoryStream _bytes = new();

    public int Count { get; private set; }

    public void Add(int tag, int level, RecordData data) => Add(tag, level, data.ToArray());

    public void Add(int tag, int level, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[4];
        var size = data.Length >= 0xFFF ? 0xFFFu : (uint)data.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)tag | ((uint)Math.Clamp(level, 0, 0x3FF) << 10) | (size << 20));
        _bytes.Write(header);
        if (size == 0xFFF)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)data.Length);
            _bytes.Write(header);
        }
        _bytes.Write(data);
        Count++;
    }

    public byte[] ToArray() => _bytes.ToArray();
}
