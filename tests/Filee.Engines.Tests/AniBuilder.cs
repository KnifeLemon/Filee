// Builds Windows animated cursors (.ani) as cursor editors write them: a RIFF "ACON" file with its "anih" header, optional
// "rate" and "seq " chunks, the frames as whole .cur files in a "LIST fram", and a "LIST INFO" with a title and author.
// The pictures of the frames are written by ImageMagick, the .cur files around them here (ImageMagick writes a "CUR" as an
// icon, one size per file, without a hotspot).

using System.Buffers.Binary;
using System.Text;
using ImageMagick;

namespace Filee.Engines.Tests;

internal sealed class AniBuilder
{
    /// <summary>The frames: each a whole .cur (or .ico) file.</summary>
    public List<byte[]> Frames { get; } = [];

    /// <summary>Which frame each step shows; null writes no "seq " chunk.</summary>
    public uint[]? Sequence { get; set; }

    /// <summary>Each step's time in jiffies (1/60 s); null writes no "rate" chunk.</summary>
    public uint[]? Rates { get; set; }

    /// <summary>The default time of a step, in jiffies.</summary>
    public uint DefaultJiffies { get; set; } = 6;

    /// <summary>AF_ICON: the frames are .ico/.cur files. Without it they would be raw bitmaps.</summary>
    public bool IconFrames { get; set; } = true;

    /// <summary>
    /// A .cur file holding one size per entry of <paramref name="sizes"/>: a square of that colour and side, with that
    /// hotspot.
    /// </summary>
    public static byte[] Cursor(params (IMagickColor<byte> Colour, uint Side, ushort HotspotX, ushort HotspotY)[] sizes)
    {
        // ImageMagick writes one size per file: each size's picture (a BMP without its file header) is taken from an
        // .ico of its own and put in one directory.
        var pictures = new List<(byte[] Entry, byte[] Data)>();
        foreach (var size in sizes)
        {
            using var image = new MagickImage(size.Colour, size.Side, size.Side);
            image.Alpha(AlphaAction.Set);
            // A transparent corner, so transparency is checked too.
            using (var pixels = image.GetPixels())
                pixels.SetArea(0, 0, 2, 2, new byte[2 * 2 * image.ChannelCount]);
            var ico = image.ToByteArray(MagickFormat.Ico);
            var length = BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(6 + 8));
            var offset = BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(6 + 12));
            pictures.Add((ico[6..22], ico[offset..(offset + length)]));
        }
        var file = new byte[6 + 16 * pictures.Count + pictures.Sum(p => p.Data.Length)];
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(2), 2); // a cursor
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(4), (ushort)pictures.Count);
        var at = 6 + 16 * pictures.Count;
        for (var i = 0; i < pictures.Count; i++)
        {
            var entry = file.AsSpan(6 + i * 16, 16);
            pictures[i].Entry.CopyTo(entry);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], sizes[i].HotspotX);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], sizes[i].HotspotY);
            BinaryPrimitives.WriteInt32LittleEndian(entry[12..], at);
            pictures[i].Data.CopyTo(file, at);
            at += pictures[i].Data.Length;
        }
        return file;
    }

    /// <summary>A one-size cursor of <paramref name="colour"/>, <paramref name="side"/> px, hotspot at the top left.</summary>
    public AniBuilder Frame(IMagickColor<byte> colour, uint side = 32)
    {
        Frames.Add(Cursor((colour, side, 0, 0)));
        return this;
    }

    public byte[] Build()
    {
        var body = new MemoryStream();
        body.Write("ACON"u8);
        var steps = (uint)(Sequence?.Length ?? Frames.Count);
        var header = new byte[36];
        uint[] fields = [36, (uint)Frames.Count, steps, 0, 0, 0, 0, DefaultJiffies, (IconFrames ? 1u : 0) | (Sequence is null ? 0 : 2u)];
        for (var i = 0; i < fields.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(i * 4), fields[i]);
        Chunk(body, "anih", header);
        if (Rates is not null)
            Chunk(body, "rate", Numbers(Rates));
        if (Sequence is not null)
            Chunk(body, "seq ", Numbers(Sequence));

        var info = new MemoryStream();
        info.Write("INFO"u8);
        Chunk(info, "INAM", Encoding.ASCII.GetBytes("Busy\0")); // odd size: padded
        Chunk(info, "IART", Encoding.ASCII.GetBytes("Filee tests\0"));
        Chunk(body, "LIST", info.ToArray());

        var frames = new MemoryStream();
        frames.Write("fram"u8);
        foreach (var frame in Frames)
            Chunk(frames, "icon", frame);
        Chunk(body, "LIST", frames.ToArray());

        var file = new MemoryStream();
        file.Write("RIFF"u8);
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)body.Length);
        file.Write(size);
        body.Position = 0;
        body.CopyTo(file);
        return file.ToArray();
    }

    private static byte[] Numbers(uint[] numbers)
    {
        var data = new byte[numbers.Length * 4];
        for (var i = 0; i < numbers.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 4), numbers[i]);
        return data;
    }

    private static void Chunk(Stream stream, string id, byte[] data)
    {
        stream.Write(Encoding.ASCII.GetBytes(id));
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)data.Length);
        stream.Write(size);
        stream.Write(data);
        if (data.Length % 2 == 1)
            stream.WriteByte(0);
    }
}
