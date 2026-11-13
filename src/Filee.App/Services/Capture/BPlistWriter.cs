using System.Buffers.Binary;
using System.Text;

namespace Filee.App.Services.Capture;

/// <summary>
/// Lightweight binary property list (bplist00) serializer for Apple AirPlay handshake responses.
/// </summary>
public sealed class BPlistWriter
{
    private readonly List<byte[]> _objects = [];

    public int AddString(string str)
    {
        var bytes = Encoding.UTF8.GetBytes(str);
        byte[] obj;
        if (bytes.Length < 15)
        {
            obj = new byte[1 + bytes.Length];
            obj[0] = (byte)(0x50 | bytes.Length);
            Buffer.BlockCopy(bytes, 0, obj, 1, bytes.Length);
        }
        else
        {
            var lenObj = EncodeInteger(bytes.Length);
            obj = new byte[1 + lenObj.Length + bytes.Length];
            obj[0] = 0x5F;
            Buffer.BlockCopy(lenObj, 0, obj, 1, lenObj.Length);
            Buffer.BlockCopy(bytes, 0, obj, 1 + lenObj.Length, bytes.Length);
        }
        _objects.Add(obj);
        return _objects.Count - 1;
    }

    public int AddUint(ulong val)
    {
        _objects.Add(EncodeInteger(val));
        return _objects.Count - 1;
    }

    public int AddBool(bool val)
    {
        _objects.Add([val ? (byte)0x09 : (byte)0x08]);
        return _objects.Count - 1;
    }

    public int AddReal(double val)
    {
        var obj = new byte[9];
        obj[0] = 0x23; // 8-byte float
        var bits = BitConverter.DoubleToUInt64Bits(val);
        BinaryPrimitives.WriteUInt64BigEndian(obj.AsSpan(1), bits);
        _objects.Add(obj);
        return _objects.Count - 1;
    }

    public int AddData(byte[] data)
    {
        byte[] obj;
        if (data.Length < 15)
        {
            obj = new byte[1 + data.Length];
            obj[0] = (byte)(0x40 | data.Length);
            Buffer.BlockCopy(data, 0, obj, 1, data.Length);
        }
        else
        {
            var lenObj = EncodeInteger(data.Length);
            obj = new byte[1 + lenObj.Length + data.Length];
            obj[0] = 0x4F;
            Buffer.BlockCopy(lenObj, 0, obj, 1, lenObj.Length);
            Buffer.BlockCopy(data, 0, obj, 1 + lenObj.Length, data.Length);
        }
        _objects.Add(obj);
        return _objects.Count - 1;
    }

    public int AddArray(IReadOnlyList<int> elementRefs)
    {
        var count = elementRefs.Count;
        byte[] obj;
        int headerLen;
        if (count < 15)
        {
            obj = new byte[1 + count];
            obj[0] = (byte)(0xA0 | count);
            headerLen = 1;
        }
        else
        {
            var lenObj = EncodeInteger(count);
            obj = new byte[1 + lenObj.Length + count];
            obj[0] = 0xAF;
            Buffer.BlockCopy(lenObj, 0, obj, 1, lenObj.Length);
            headerLen = 1 + lenObj.Length;
        }

        for (int i = 0; i < count; i++)
        {
            obj[headerLen + i] = (byte)elementRefs[i];
        }

        _objects.Add(obj);
        return _objects.Count - 1;
    }

    public int AddDict(IReadOnlyList<KeyValuePair<int, int>> entries)
    {
        var count = entries.Count;
        byte[] obj;
        int headerLen;
        if (count < 15)
        {
            obj = new byte[1 + count * 2];
            obj[0] = (byte)(0xD0 | count);
            headerLen = 1;
        }
        else
        {
            var lenObj = EncodeInteger(count);
            obj = new byte[1 + lenObj.Length + count * 2];
            obj[0] = 0xDF;
            Buffer.BlockCopy(lenObj, 0, obj, 1, lenObj.Length);
            headerLen = 1 + lenObj.Length;
        }

        for (int i = 0; i < count; i++)
        {
            obj[headerLen + i] = (byte)entries[i].Key;
            obj[headerLen + count + i] = (byte)entries[i].Value;
        }

        _objects.Add(obj);
        return _objects.Count - 1;
    }

    public byte[] Build(int rootIndex)
    {
        using var ms = new MemoryStream();

        // 1. Header "bplist00"
        ms.Write("bplist00"u8);

        // 2. Objects and offsets
        var offsets = new List<long>(_objects.Count);
        foreach (var obj in _objects)
        {
            offsets.Add(ms.Position);
            ms.Write(obj, 0, obj.Length);
        }

        long offsetTableStart = ms.Position;
        byte offsetSize = (byte)(offsetTableStart <= 0xFF ? 1 : offsetTableStart <= 0xFFFF ? 2 : 4);

        // 3. Offset table
        var buf4 = new byte[4];
        foreach (var off in offsets)
        {
            if (offsetSize == 1)
            {
                ms.WriteByte((byte)off);
            }
            else if (offsetSize == 2)
            {
                BinaryPrimitives.WriteUInt16BigEndian(buf4, (ushort)off);
                ms.Write(buf4, 0, 2);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(buf4, (uint)off);
                ms.Write(buf4, 0, 4);
            }
        }

        // 4. Trailer (32 bytes)
        var trailer = new byte[32];
        trailer[6] = offsetSize;
        trailer[7] = 1; // object ref size = 1 byte
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(8), (ulong)_objects.Count);
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(16), (ulong)rootIndex);
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(24), (ulong)offsetTableStart);

        ms.Write(trailer, 0, 32);
        return ms.ToArray();
    }

    private static byte[] EncodeInteger(long val) => EncodeInteger((ulong)val);

    private static byte[] EncodeInteger(ulong val)
    {
        if (val <= 0xFF)
        {
            return [0x10, (byte)val];
        }
        if (val <= 0xFFFF)
        {
            var res = new byte[3];
            res[0] = 0x11;
            BinaryPrimitives.WriteUInt16BigEndian(res.AsSpan(1), (ushort)val);
            return res;
        }
        if (val <= 0xFFFFFFFF)
        {
            var res = new byte[5];
            res[0] = 0x12;
            BinaryPrimitives.WriteUInt32BigEndian(res.AsSpan(1), (uint)val);
            return res;
        }
        else
        {
            var res = new byte[9];
            res[0] = 0x13;
            BinaryPrimitives.WriteUInt64BigEndian(res.AsSpan(1), val);
            return res;
        }
    }
}
