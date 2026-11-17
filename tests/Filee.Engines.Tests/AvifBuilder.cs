// Builds AVIF files of several pictures in memory, no image files are checked into the repository. ImageMagick encodes
// each picture as a one-picture AVIF; its coded data and av1C box are put into a new file as items next to each other.

using System.Buffers.Binary;
using System.Text;
using ImageMagick;

namespace Filee.Engines.Tests;

internal static class AvifBuilder
{
    /// <summary>One item of the file: a picture of one colour. The first item is the primary one.</summary>
    public readonly record struct Picture(MagickColor Colour, uint Width, uint Height);

    /// <summary>An AVIF with every picture as a visible item, and no tracks: not a sequence, whatever <paramref name="brand"/> says.</summary>
    public static byte[] Items(string brand, params Picture[] pictures)
    {
        var coded = pictures.Select(Encode).ToList();
        var ftyp = Box("ftyp", Ascii(brand), [0, 0, 0, 0], Ascii("mif1"), Ascii("avif"), Ascii("miaf"));
        // Offsets depend only on the length of the meta box, which the offsets don't change.
        var meta = Meta(coded, 0);
        var offset = ftyp.Length + meta.Length + 8;
        return [.. ftyp, .. Meta(coded, offset), .. Box("mdat", [.. coded.SelectMany(c => c.Data)])];
    }

    /// <summary>The picture's av1C box and coded data, out of a one-picture AVIF written by ImageMagick.</summary>
    private static (byte[] Av1C, byte[] Data, uint Width, uint Height) Encode(Picture picture)
    {
        using var image = new MagickImage(picture.Colour, picture.Width, picture.Height);
        image.Quality = 90;
        image.Strip();
        image.Alpha(AlphaAction.Off);
        var file = image.ToByteArray(MagickFormat.Avif);
        var meta = Find(file, 0, file.Length, "meta");
        var iprp = Find(file, meta.Start + 4, meta.End, "iprp"); // meta is a full box
        var ipco = Find(file, iprp.Start, iprp.End, "ipco");
        var av1C = Find(file, ipco.Start, ipco.End, "av1C");
        var mdat = Find(file, 0, file.Length, "mdat");
        return (file[(av1C.Start - 8)..av1C.End], file[mdat.Start..mdat.End], picture.Width, picture.Height);
    }

    private static byte[] Meta(List<(byte[] Av1C, byte[] Data, uint Width, uint Height)> coded, int dataOffset)
    {
        var count = U16(coded.Count);
        var locations = new List<byte>();
        var infos = new List<byte>();
        var properties = new List<byte>();
        var associations = new List<byte>();
        for (var i = 0; i < coded.Count; i++)
        {
            var id = U16(i + 1);
            locations.AddRange([.. id, 0, 0, 0, 1, .. U32(dataOffset), .. U32(coded[i].Data.Length)]);
            dataOffset += coded[i].Data.Length;
            infos.AddRange(Full("infe", 2, [.. id, 0, 0, .. Ascii("av01"), 0]));
            properties.AddRange(coded[i].Av1C);
            properties.AddRange(Full("ispe", 0, [.. U32((int)coded[i].Width), .. U32((int)coded[i].Height)]));
            // The av1C of the item is essential, its size not (property indexes count from 1).
            associations.AddRange([.. id, 2, (byte)(0x80 | (i * 2 + 1)), (byte)(i * 2 + 2)]);
        }
        return Full("meta", 0,
            Full("hdlr", 0, [0, 0, 0, 0, .. Ascii("pict"), .. new byte[12], 0]),
            Full("pitm", 0, U16(1)),
            // Offsets and lengths of 4 bytes, no base offset.
            Full("iloc", 0, [0x44, 0x00, .. count, .. locations]),
            Full("iinf", 0, [.. count, .. infos]),
            Box("iprp", Box("ipco", [.. properties]), Full("ipma", 0, [.. U32(coded.Count), .. associations])));
    }

    /// <summary>The contents of the first box of <paramref name="type"/> between two positions.</summary>
    private static (int Start, int End) Find(byte[] file, int start, int end, string type)
    {
        for (var position = start; position + 8 <= end;)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(position));
            if (Encoding.ASCII.GetString(file, position + 4, 4) == type)
                return (position + 8, position + size);
            position += size;
        }
        throw new InvalidDataException($"No {type} box.");
    }

    private static byte[] Box(string type, params byte[][] parts)
    {
        var length = 8 + parts.Sum(p => p.Length);
        return [.. U32(length), .. Ascii(type), .. parts.SelectMany(p => p)];
    }

    private static byte[] Full(string type, byte version, params byte[][] parts) => Box(type, [[version, 0, 0, 0], .. parts]);

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] U16(int value) => [(byte)(value >> 8), (byte)value];

    private static byte[] U32(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
}
