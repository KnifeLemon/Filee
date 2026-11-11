// The layer records of a PSD or PSB (Adobe's layer and mask information section), read directly: each layer's name and
// where its flags byte is. ImageMagick reads a Unicode name ("luni") as ASCII only and a name in the Pascal field as
// UTF-8, though Photoshop writes it in Mac Roman, so "Arrière-plan" comes back damaged; Filee reads both itself. Used
// when a PSD becomes a Paint.NET file (PaintNetWriter) and to set the flags ImageMagick writes wrong (PaintNet).

using System.Buffers.Binary;
using System.Text;

namespace Filee.Engines.Magick;

internal static class PsdLayerRecords
{
    /// <summary>A layer record: the offset of its flags byte in the file, and its name.</summary>
    internal sealed record Record(long FlagsOffset, string Name);

    /// <summary>Additional information whose length is 8 bytes in a PSB (4 in a PSD), from Adobe's file format spec.</summary>
    private static readonly HashSet<string> LongKeysInPsb =
        ["LMsk", "Lr16", "Lr32", "Layr", "Mt16", "Mt32", "Mtrn", "Alph", "FMsk", "lnk2", "FEid", "FXid", "PxSD"];

    static PsdLayerRecords() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The layer records bottom first, or null when the file is not laid out as a PSD/PSB should be.</summary>
    public static List<Record>? Read(Stream file, bool psb)
    {
        try
        {
            return ReadRecords(file, psb);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    private static List<Record>? ReadRecords(Stream file, bool psb)
    {
        var reader = new BinaryReader(file, Encoding.ASCII, leaveOpen: true);
        uint UInt32() => BinaryPrimitives.ReadUInt32BigEndian(reader.ReadBytes(4));
        void Skip(long length) => file.Position += length;

        file.Position = 0;
        if (!reader.ReadBytes(4).AsSpan().SequenceEqual("8BPS"u8))
            return null;
        file.Position = 26;                     // file header
        Skip(UInt32());                         // colour mode data
        Skip(UInt32());                         // image resources
        Skip(psb ? 16 : 8);                     // layer and mask information length, layer information length
        var count = Math.Abs(BinaryPrimitives.ReadInt16BigEndian(reader.ReadBytes(2)));
        var records = new List<Record>(count);
        for (var i = 0; i < count; i++)
        {
            Skip(16);                           // bounds
            var channels = BinaryPrimitives.ReadUInt16BigEndian(reader.ReadBytes(2));
            Skip(channels * (psb ? 10 : 6));
            if (!reader.ReadBytes(4).AsSpan().SequenceEqual("8BIM"u8))
                return null;
            Skip(6);                            // blend mode, opacity, clipping
            var flags = file.Position;
            Skip(2);                            // flags, filler
            var extraLength = UInt32();
            var end = file.Position + extraLength;
            if (end > file.Length)
                return null;
            Skip(UInt32());                     // layer mask data
            Skip(UInt32());                     // blending ranges
            var nameLength = reader.ReadByte();
            var name = Encoding.GetEncoding(10000).GetString(reader.ReadBytes(nameLength)); // Mac Roman
            Skip((4 - (nameLength + 1) % 4) % 4);
            while (file.Position + 12 <= end)
            {
                var signature = Encoding.ASCII.GetString(reader.ReadBytes(4));
                if (signature is not ("8BIM" or "8B64"))
                    break;
                var key = Encoding.ASCII.GetString(reader.ReadBytes(4));
                var length = psb && LongKeysInPsb.Contains(key)
                    ? (long)BinaryPrimitives.ReadUInt64BigEndian(reader.ReadBytes(8))
                    : UInt32();
                if (length < 0 || file.Position + length > end)
                    break;
                if (key == "luni" && length >= 4)
                {
                    var characters = UInt32();
                    if (characters <= (length - 4) / 2)
                        name = Encoding.BigEndianUnicode.GetString(reader.ReadBytes((int)characters * 2)).TrimEnd('\0');
                    break;
                }
                Skip(length);
            }
            file.Position = end;
            records.Add(new Record(flags, name));
        }
        return records;
    }
}
