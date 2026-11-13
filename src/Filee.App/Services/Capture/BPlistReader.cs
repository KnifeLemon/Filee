using System.Buffers.Binary;
using System.Text;

namespace Filee.App.Services.Capture;

public sealed class BPlistReader
{
    private byte[] _data = [];
    private readonly List<long> _offsets = [];
    private byte _offsetSize;
    private byte _refSize;
    private ulong _numObjects;
    private ulong _rootObject;
    private ulong _offsetTableOffset;

    public bool Parse(byte[] data)
    {
        if (data.Length < 40) return false;
        if (data[0] != 'b' || data[1] != 'p' || data[2] != 'l' || data[3] != 'i' ||
            data[4] != 's' || data[5] != 't' || data[6] != '0' || data[7] != '0')
        {
            return false;
        }

        _data = data;
        int trailerStart = data.Length - 32;
        _offsetSize = data[trailerStart + 6];
        _refSize = data[trailerStart + 7];
        _numObjects = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(trailerStart + 8));
        _rootObject = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(trailerStart + 16));
        _offsetTableOffset = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(trailerStart + 24));

        if (_offsetSize == 0 || _offsetSize > 8 || _refSize == 0 || _refSize > 8) return false;
        if (_numObjects == 0 || _numObjects > 10000) return false;
        if (_rootObject >= _numObjects) return false;
        if (_offsetTableOffset + _numObjects * _offsetSize > (ulong)trailerStart) return false;

        _offsets.Clear();
        for (ulong i = 0; i < _numObjects; i++)
        {
            long off = ReadBe((long)(_offsetTableOffset + i * _offsetSize), _offsetSize);
            if (off >= data.Length) return false;
            _offsets.Add(off);
        }

        return true;
    }

    private long ReadBe(long offset, int bytes)
    {
        if (offset + bytes > _data.Length) return 0;
        long val = 0;
        for (int i = 0; i < bytes; i++)
        {
            val = (val << 8) | _data[offset + i];
        }
        return val;
    }

    private (ulong count, int extra) DecodeCount(long offset)
    {
        if (offset >= _data.Length) return (0, 0);
        ulong count = (ulong)(_data[offset] & 0x0F);
        int extra = 0;
        if (count == 0x0F)
        {
            if (offset + 2 >= _data.Length) return (0, 0);
            byte intMarker = _data[offset + 1];
            int intBytes = 1 << (intMarker & 0x0F);
            if (offset + 2 + intBytes > _data.Length) return (0, 0);
            count = (ulong)ReadBe(offset + 2, intBytes);
            extra = 1 + intBytes;
        }
        return (count, extra);
    }

    private string ReadStringObject(ulong objIdx)
    {
        if (objIdx >= (ulong)_offsets.Count) return "";
        long off = _offsets[(int)objIdx];
        if (off >= _data.Length) return "";

        byte marker = _data[off];
        int type = marker >> 4;
        if (type != 0x5 && type != 0x6) return "";

        var (count, extra) = DecodeCount(off);
        long dataStart = off + 1 + extra;
        if (type == 0x5)
        {
            if (count > 65536 || dataStart + (long)count > _data.Length) return "";
            return Encoding.ASCII.GetString(_data, (int)dataStart, (int)count);
        }

        if (count > 65536 || dataStart + (long)count * 2 > _data.Length) return "";
        return Encoding.BigEndianUnicode.GetString(_data, (int)dataStart, (int)count * 2);
    }

    private byte[] ReadDataObject(ulong objIdx)
    {
        if (objIdx >= (ulong)_offsets.Count) return [];
        long off = _offsets[(int)objIdx];
        if (off >= _data.Length) return [];

        byte marker = _data[off];
        if ((marker >> 4) != 0x4) return [];

        var (count, extra) = DecodeCount(off);
        long dataStart = off + 1 + extra;
        if (count > 10 * 1024 * 1024 || dataStart + (long)count > _data.Length) return [];

        var res = new byte[count];
        Buffer.BlockCopy(_data, (int)dataStart, res, 0, (int)count);
        return res;
    }

    private ulong ReadUintObject(ulong objIdx)
    {
        if (objIdx >= (ulong)_offsets.Count) return 0;
        long off = _offsets[(int)objIdx];
        if (off >= _data.Length) return 0;

        byte marker = _data[off];
        if ((marker >> 4) != 0x1) return 0;

        int byteCount = 1 << (marker & 0x0F);
        if (off + 1 + byteCount > _data.Length) return 0;
        return (ulong)ReadBe(off + 1, byteCount);
    }

    private (List<ulong> keys, List<ulong> vals) ReadDict(ulong objIdx)
    {
        var keys = new List<ulong>();
        var vals = new List<ulong>();
        if (objIdx >= (ulong)_offsets.Count) return (keys, vals);
        long off = _offsets[(int)objIdx];
        if (off >= _data.Length) return (keys, vals);

        byte marker = _data[off];
        if ((marker >> 4) != 0xD) return (keys, vals);

        var (count, extra) = DecodeCount(off);
        long refsStart = off + 1 + extra;
        if (count > 4096 || refsStart + (long)count * 2 * _refSize > _data.Length) return (keys, vals);

        for (ulong i = 0; i < count; i++)
        {
            keys.Add((ulong)ReadBe(refsStart + (long)i * _refSize, _refSize));
        }
        for (ulong i = 0; i < count; i++)
        {
            vals.Add((ulong)ReadBe(refsStart + ((long)count + (long)i) * _refSize, _refSize));
        }
        return (keys, vals);
    }

    public bool TryGetData(string key, out byte[] data)
    {
        var (keys, vals) = ReadDict(_rootObject);
        for (int i = 0; i < keys.Count; i++)
        {
            if (ReadStringObject(keys[i]) == key)
            {
                data = ReadDataObject(vals[i]);
                return true;
            }
        }
        data = [];
        return false;
    }

    public bool TryGetUint(string key, out ulong val)
    {
        var (keys, vals) = ReadDict(_rootObject);
        for (int i = 0; i < keys.Count; i++)
        {
            if (ReadStringObject(keys[i]) == key)
            {
                val = ReadUintObject(vals[i]);
                return true;
            }
        }
        val = 0;
        return false;
    }

    public bool HasKey(string key)
    {
        var (keys, _) = ReadDict(_rootObject);
        for (int i = 0; i < keys.Count; i++)
        {
            if (ReadStringObject(keys[i]) == key) return true;
        }
        return false;
    }

    public bool TryGetStreamConnectionId(out ulong connId)
    {
        connId = 0;
        var (keys, vals) = ReadDict(_rootObject);
        for (int i = 0; i < keys.Count; i++)
        {
            if (ReadStringObject(keys[i]) == "streams")
            {
                ulong streamsArrayIdx = vals[i];
                if (streamsArrayIdx < (ulong)_offsets.Count)
                {
                    long off = _offsets[(int)streamsArrayIdx];
                    if (off < _data.Length && (_data[off] >> 4) == 0xA)
                    {
                        var (count, extra) = DecodeCount(off);
                        long refsStart = off + 1 + extra;
                        for (ulong j = 0; j < count; j++)
                        {
                            ulong streamDictIdx = (ulong)ReadBe(refsStart + (long)j * _refSize, _refSize);
                            var (sKeys, sVals) = ReadDict(streamDictIdx);
                            for (int k = 0; k < sKeys.Count; k++)
                            {
                                if (ReadStringObject(sKeys[k]) == "streamConnectionID")
                                {
                                    connId = ReadUintObject(sVals[k]);
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
        }
        return false;
    }
}
