// Creates FON samples in memory; no font files are checked into the repository.

using System.Numerics;

namespace Filee.Engines.Tests;

internal static class FonBuilder
{
    private static void Put(List<byte> data, int offset, ushort value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
    }

    private static void Put(List<byte> data, int offset, uint value)
    {
        Put(data, offset, (ushort)value);
        Put(data, offset + 2, (ushort)(value >> 16));
    }

    private static byte[] Fnt(int version, int height = 13, bool fixedWidth = true, int first = 32,
        int last = 127, int? flags = null, int weight = 400, bool italic = false, string faceName = "Fixed")
    {
        var count = last - first + 1;
        var entrySize = version == 2 ? 4 : flags is null ? 6 : 8 + (BitOperations.Log2((uint)flags.Value) - 2) * 2;
        var table = version == 2 ? 0x76 : 0x94;
        var face = table + (count + 1) * entrySize;
        var bits = face + 6;
        var data = new List<byte>(new byte[bits]);
        Put(data, 0, (ushort)(version << 8));
        Put(data, 0x42, (ushort)0);
        Put(data, 0x4A, (ushort)Math.Min(height, 10));
        Put(data, 0x4E, (ushort)2);
        Put(data, 0x53, (ushort)weight);
        data[0x50] = italic ? (byte)1 : (byte)0;
        data[0x55] = 0;
        Put(data, 0x56, (ushort)(fixedWidth ? 8 : 0));
        Put(data, 0x58, (ushort)height);
        Put(data, 0x5D, (ushort)9);
        data[0x5F] = (byte)first;
        data[0x60] = (byte)last;
        Put(data, 0x69, (uint)face);
        Put(data, 0x71, (uint)bits);
        if (version == 3)
        {
            Put(data, 0x76, (uint)(flags ?? (fixedWidth ? 1 : 2)));
            if (flags is not null)
            {
                Put(data, 0x7A, (ushort)(flags + 1));
                Put(data, 0x7C, (ushort)(flags + 2));
                Put(data, 0x7E, (ushort)(flags + 3));
            }
        }
        for (var i = 0; i < 6; i++)
            data[face + i] = i < faceName.Length ? (byte)faceName[i] : (byte)0;

        for (var index = 0; index < count; index++)
        {
            var code = first + index;
            var width = flags is not null ? flags.Value + index % 3 :
                fixedWidth ? 8 : code == 32 ? 0 : code % 2 == 1 ? 5 : 9;
            var entry = table + index * entrySize;
            Put(data, entry, (ushort)width);
            if (flags is not null)
            {
                Put(data, entry + 2, (ushort)(flags + index % 3));
                Put(data, entry + 4, (uint)data.Count);
            }
            else if (version == 2)
                Put(data, entry + 2, (ushort)data.Count);
            else
                Put(data, entry + 2, (uint)data.Count);

            // Pixel art is independent of the reader: a single pixel, diagonal, and hollow box.
            for (var column = 0; column < (width + 7) / 8; column++)
                for (var y = 0; y < height; y++)
                {
                    byte value = 0;
                    for (var bit = 0; bit < 8; bit++)
                    {
                        var x = column * 8 + bit;
                        if (x >= width || code == 32)
                            continue;
                        var rows = Math.Min(height, 7);
                        var lit = code == 33 ? x == 2 && y == 0 :
                            code == 34 ? x == 2 + y % Math.Max(1, width - 2) :
                            x >= 2 && y < rows && (x == 2 || x == width - 1 || y == 0 || y == rows - 1);
                        if (lit)
                            value |= (byte)(0x80 >> bit);
                    }
                    data.Add(value);
                }
        }
        var sentinel = table + count * entrySize;
        Put(data, sentinel, (ushort)0);
        if (flags is not null)
            Put(data, sentinel + 4, (uint)data.Count);
        else if (version == 2)
            Put(data, sentinel + 2, (ushort)data.Count);
        else
            Put(data, sentinel + 2, (uint)data.Count);
        Put(data, 2, (uint)data.Count);
        return [.. data];
    }

    /// <summary>Writes NE FONTDIR and RT_FONT entries with an optional resource alignment shift.</summary>
    public static byte[] Container(byte[][] resources, int alignmentShift = 0)
    {
        const int ne = 0x40, table = ne + 0x40;
        var payload = table + 2 + 8 + 12 + 8 + 12 * resources.Length + 2;
        var data = new List<byte>(new byte[payload]);
        data[0] = (byte)'M';
        data[1] = (byte)'Z';
        Put(data, 0x3C, (uint)ne);
        data[ne] = (byte)'N';
        data[ne + 1] = (byte)'E';
        Put(data, ne + 0x24, (ushort)(table - ne));
        Put(data, ne + 0x26, (ushort)(payload - ne));
        Put(data, table, (ushort)alignmentShift);
        Put(data, table + 2, (ushort)0x8007);
        Put(data, table + 4, (ushort)1);
        var unit = 1 << alignmentShift;
        // The one-byte FONTDIR, like every resource, starts on an alignment unit.
        while (data.Count % unit != 0)
            data.Add(0);
        Put(data, table + 10, (ushort)(data.Count / unit));
        Put(data, table + 12, (ushort)1);
        data.Add(0);
        var fontType = table + 22;
        Put(data, fontType, (ushort)0x8008);
        Put(data, fontType + 2, (ushort)resources.Length);
        for (var index = 0; index < resources.Length; index++)
        {
            while (data.Count % unit != 0)
                data.Add(0);
            var resource = resources[index];
            var offset = data.Count / unit;
            var length = (resource.Length + unit - 1) / unit;
            if (offset > ushort.MaxValue || length > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(resources), "NE resource exceeds WORD range.");
            var entry = fontType + 8 + index * 12;
            Put(data, entry, (ushort)offset);
            Put(data, entry + 2, (ushort)length);
            Put(data, entry + 6, (ushort)(0x8001 + index));
            data.AddRange(resource);
            while (data.Count % unit != 0)
                data.Add(0);
        }
        return [.. data];
    }

    /// <summary>Builds the fixed-width FNT v2 sample.</summary>
    public static byte[] V2Fixed() => Container([Fnt(2)]);
    /// <summary>Builds the proportional FNT v2 sample with control characters.</summary>
    public static byte[] V2Proportional() => Container([Fnt(2, fixedWidth: false, first: 0)]);
    /// <summary>Builds the fixed-width FNT v3 sample.</summary>
    public static byte[] V3Fixed() => Container([Fnt(3)]);
    /// <summary>Builds the proportional FNT v3 sample.</summary>
    public static byte[] V3Proportional() => Container([Fnt(3, fixedWidth: false)]);
    /// <summary>Builds three strikes, two at the same height.</summary>
    public static byte[] Multi() => Container([Fnt(2), Fnt(3), Fnt(3, 14)]);
    /// <summary>Builds mixed styles and duplicate style strikes at two heights.</summary>
    public static byte[] MixedStyles() => Container([
        Fnt(3, faceName: "Name"), Fnt(3, weight: 700, faceName: "Name"), Fnt(3, italic: true, faceName: "Name"),
        Fnt(3, weight: 700, italic: true, faceName: "Name"), Fnt(3, weight: 600, faceName: "Name"),
        Fnt(3, weight: 600, italic: true, faceName: "Name"), Fnt(3, faceName: "Name"),
        Fnt(3, weight: 700, faceName: "Name"), Fnt(3, 14, faceName: "Name")
    ]);
    /// <summary>Builds the one- and 256-pixel strikes.</summary>
    public static byte[] Edge() => Container([Fnt(3, 1, first: 32, last: 34), Fnt(3, 256, first: 32, last: 34)]);
    /// <summary>Builds a v3 resource with the given unsupported glyph table flags.</summary>
    public static byte[] V3Flags(int flag) => Container([Fnt(3, flags: flag)]);

    /// <summary>Returns one of the twelve named synthetic samples.</summary>
    public static byte[] Sample(string name) => name switch
    {
        "Fon-v2-fixed.fon" => V2Fixed(),
        "Fon-v2-proportional.fon" => V2Proportional(),
        "Fon-v3-fixed.fon" => V3Fixed(),
        "Fon-v3-proportional.fon" => V3Proportional(),
        "Fon-multi.fon" => Multi(),
        "Fon-edge.fon" => Edge(),
        "Fon-v3-flags-04.fon" => V3Flags(0x04),
        "Fon-v3-flags-08.fon" => V3Flags(0x08),
        "Fon-v3-flags-10.fon" => V3Flags(0x10),
        "Fon-v3-flags-20.fon" => V3Flags(0x20),
        "Fon-v3-flags-40.fon" => V3Flags(0x40),
        "Fon-v3-flags-80.fon" => V3Flags(0x80),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };
}
