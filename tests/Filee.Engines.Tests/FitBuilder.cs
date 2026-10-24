// Creates FIT protocol samples in memory; no device recordings are checked into the repository.

using System.Buffers.Binary;
using Filee.Engines.Geo;

namespace Filee.Engines.Tests;

internal sealed class FitBuilder
{
    private readonly List<byte> data = [];
    private readonly Dictionary<int, (FitField[] Fields, int[] DeveloperSizes)> definitions = [];

    internal readonly record struct FitField(byte Number, byte Type, byte[] Value);
    internal readonly record struct DeveloperField(byte Number, byte[] Value, byte Index = 0);

    public static FitField U8(byte number, byte value, byte type = 0) => new(number, type, [value]);
    public static FitField U16(byte number, ushort value, bool big = false) => new(number, 0x84, Bytes(value, big));
    public static FitField U32(byte number, uint value, bool big = false) => new(number, 0x86, Bytes(value, big));
    public static FitField S32(byte number, int value, bool big = false) => new(number, 0x85, Bytes((uint)value, big));

    private static byte[] Bytes(ushort value, bool big)
    {
        var bytes = new byte[2];
        if (big) BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        else BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Bytes(uint value, bool big)
    {
        var bytes = new byte[4];
        if (big) BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        else BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    /// <summary>Defines a local message, including optional developer fields which the reader must skip.</summary>
    public FitBuilder Define(int local, ushort message, bool big = false, byte architecture = 0,
        DeveloperField[]? developer = null, params FitField[] fields)
    {
        data.Add((byte)(0x40 | local | (developer is { Length: > 0 } ? 0x20 : 0)));
        data.Add(0);
        data.Add(big ? (byte)1 : architecture);
        data.AddRange(Bytes(message, big));
        data.Add((byte)fields.Length);
        foreach (var field in fields)
        {
            data.Add(field.Number);
            data.Add((byte)field.Value.Length);
            data.Add(field.Type);
        }
        if (developer is { Length: > 0 })
        {
            data.Add((byte)developer.Length);
            foreach (var field in developer)
            {
                data.Add(field.Number);
                data.Add((byte)field.Value.Length);
                data.Add(field.Index);
            }
        }
        definitions[local] = (fields, developer?.Select(f => f.Value.Length).ToArray() ?? []);
        return this;
    }

    /// <summary>Writes a data message in definition order, including the developer field payloads.</summary>
    public FitBuilder Message(int local, FitField[] fields, params DeveloperField[] developer) =>
        AddMessage((byte)local, local, fields, developer);

    public FitBuilder Compressed(int local, byte offset, FitField[] fields, params DeveloperField[] developer) =>
        AddMessage((byte)(0x80 | (local << 5) | (offset & 31)), local, fields, developer);

    private FitBuilder AddMessage(byte header, int local, FitField[] fields, DeveloperField[] developer)
    {
        var definition = definitions[local];
        if (definition.Fields.Length != fields.Length || definition.DeveloperSizes.Length != developer.Length)
            throw new ArgumentException("The message must match its definition.");
        data.Add(header);
        for (var i = 0; i < fields.Length; i++)
        {
            if (fields[i].Value.Length != definition.Fields[i].Value.Length)
                throw new ArgumentException("The field must match its definition's size.");
            data.AddRange(fields[i].Value);
        }
        for (var i = 0; i < developer.Length; i++)
        {
            if (developer[i].Value.Length != definition.DeveloperSizes[i])
                throw new ArgumentException("The developer field must match its definition's size.");
            data.AddRange(developer[i].Value);
        }
        return this;
    }

    public FitBuilder FileId(byte type = 4, int local = 15)
    {
        var field = U8(0, type);
        return Define(local, 0, fields: [field]).Message(local, [field]);
    }

    public FitBuilder Session(uint start, byte sport, int local = 2, bool big = false)
    {
        var fields = new[] { U32(2, start, big), U8(5, sport) };
        return Define(local, 18, big: big, fields: fields).Message(local, fields);
    }

    /// <summary>Builds a complete 12- or 14-byte header with optional zero header CRC and a valid file CRC.</summary>
    public byte[] Build(int headerSize = 14, bool headerCrc = true)
    {
        if (headerSize is not (12 or 14))
            throw new ArgumentOutOfRangeException(nameof(headerSize));
        var bytes = new byte[headerSize + data.Count + 2];
        bytes[0] = (byte)headerSize;
        bytes[1] = 0x10;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)data.Count);
        ".FIT"u8.CopyTo(bytes.AsSpan(8));
        if (headerSize == 14 && headerCrc)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), FitFormat.FitCrc(bytes.AsSpan(0, 12)));
        data.CopyTo(bytes, headerSize);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(headerSize + data.Count), FitFormat.FitCrc(bytes.AsSpan(0, headerSize + data.Count)));
        return bytes;
    }

    public static byte[] FlipHeaderCrc(byte[] bytes)
    {
        var copy = (byte[])bytes.Clone();
        copy[12] ^= 1;
        return copy;
    }

    public static byte[] FlipFileCrc(byte[] bytes)
    {
        var copy = (byte[])bytes.Clone();
        copy[^1] ^= 1;
        return copy;
    }

    public static byte[] CutShort(byte[] bytes) => bytes[..^1];
    public static byte[] ExtraBytes(byte[] bytes) => [.. bytes, 0x42];
    public static byte[] Chained(byte[] bytes) => [.. bytes, .. bytes];

    /// <summary>A valid CRC around malformed data, to exercise structural refusals rather than the CRC gate.</summary>
    public static byte[] RawData(params byte[] payload)
    {
        var bytes = new byte[14 + payload.Length + 2];
        bytes[0] = 14;
        bytes[1] = 0x10;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)payload.Length);
        ".FIT"u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), FitFormat.FitCrc(bytes.AsSpan(0, 12)));
        payload.CopyTo(bytes, 14);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14 + payload.Length), FitFormat.FitCrc(bytes.AsSpan(0, 14 + payload.Length)));
        return bytes;
    }

    public static byte[] InvalidArchitecture() => RawData(0x40, 0, 2, 0, 0, 0);
    public static byte[] UndefinedLocal(int local = 3) => RawData((byte)local);

    public static string Save(string folder, byte[] bytes, string name = "sample.fit")
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
