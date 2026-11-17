// Tells an animated AVIF from every other AVIF by the file itself. ImageMagick reads both as a list of frames, but only
// an image sequence is an animation: the items of any other AVIF (a burst, a gain map, pictures of their own sizes) are
// separate pictures.

using System.Buffers.Binary;

namespace Filee.Engines.Magick;

/// <summary>Recognizes AVIF image sequences.</summary>
internal static class AvifSequence
{
    /// <summary>The format id frames of an AVIF image sequence are read as, as a .png with an acTL chunk is read as "apng".</summary>
    public const string Format = "avis";

    /// <summary>The format id to read <paramref name="path"/> of <paramref name="format"/> as: <see cref="Format"/> for a sequence.</summary>
    public static string SourceFormat(string format, string path) =>
        format == "avif" && IsSequence(path) ? Format : format;

    /// <summary>
    /// True when <paramref name="path"/> has the major brand "avis" and the "moov" box with its tracks. The major brand is
    /// what decides for players too: browsers show a file with the brand "avif" as one picture, whatever else it holds.
    /// </summary>
    public static bool IsSequence(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> box = stackalloc byte[12];
            if (stream.ReadAtLeast(box, 12, throwOnEndOfStream: false) < 12
                || !box[4..8].SequenceEqual("ftyp"u8) || !box[8..12].SequenceEqual("avis"u8))
                return false;

            stream.Position = 0;
            while (stream.ReadAtLeast(box[..8], 8, throwOnEndOfStream: false) == 8)
            {
                if (box[4..8].SequenceEqual("moov"u8))
                    return true;
                long size = BinaryPrimitives.ReadUInt32BigEndian(box);
                var header = 8;
                if (size == 1)
                {
                    if (stream.ReadAtLeast(box[..8], 8, throwOnEndOfStream: false) < 8)
                        return false;
                    size = BinaryPrimitives.ReadInt64BigEndian(box);
                    header = 16;
                }
                // Size 0 runs to the end of the file; anything shorter than its own header is broken.
                if (size < header)
                    return false;
                stream.Seek(size - header, SeekOrigin.Current);
            }
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
