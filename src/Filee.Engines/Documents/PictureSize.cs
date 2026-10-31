// The pixel size a picture's header claims, read without decoding it, so a picture that would take gigabytes once
// decoded (a few kilobytes of PNG can claim 100,000 × 100,000 pixels) is left out before anything decodes it. PNG,
// JPEG, GIF, BMP and WebP, and EMF and WMF (their picture frame at 96 dpi); other formats (TIFF, SVG) give no size here
// and are checked where they are drawn.

using System.Buffers.Binary;

namespace Filee.Engines.Documents;

/// <summary>Reads the width and height from a picture's header.</summary>
internal static class PictureSize
{
    /// <summary>JPEG segments looked at before the frame header (files have a dozen).</summary>
    private const int MaxJpegSegments = 1000;

    /// <summary>The size in pixels; null for formats it does not know and headers it can't read.</summary>
    public static (int Width, int Height)? Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc cref="Read(string)"/>
    public static (int Width, int Height)? Read(Stream stream)
    {
        Span<byte> head = stackalloc byte[44];
        var length = ReadUpTo(stream, head);
        head = head[..length];
        if (head is [0x89, (byte)'P', (byte)'N', (byte)'G', ..] && head.Length >= 24)
            return Size(BinaryPrimitives.ReadUInt32BigEndian(head[16..]), BinaryPrimitives.ReadUInt32BigEndian(head[20..]));
        if (head is [(byte)'G', (byte)'I', (byte)'F', (byte)'8', ..] && head.Length >= 10)
            return Size(BinaryPrimitives.ReadUInt16LittleEndian(head[6..]), BinaryPrimitives.ReadUInt16LittleEndian(head[8..]));
        if (head is [(byte)'B', (byte)'M', ..] && head.Length >= 26)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(head[14..]);
            return header == 12
                ? Size(BinaryPrimitives.ReadUInt16LittleEndian(head[18..]), BinaryPrimitives.ReadUInt16LittleEndian(head[20..]))
                : Size((uint)Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(head[18..])), (uint)Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(head[22..])));
        }
        if (head is [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] && head.Length >= 30)
            return WebP(head);
        if (head is [0xFF, 0xD8, ..])
            return Jpeg(stream, head);
        return Metafile(head);
    }

    /// <summary>
    /// An EMF's frame (0.01 mm) or a placeable WMF's box (in its units per inch) as pixels at 96 dpi, the size the
    /// metafile renderer draws it at by default.
    /// </summary>
    private static (int Width, int Height)? Metafile(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 44 && BinaryPrimitives.ReadUInt32LittleEndian(head) == 1 && BinaryPrimitives.ReadUInt32LittleEndian(head[40..]) == 0x464D4520)
        {
            long width = (long)BinaryPrimitives.ReadInt32LittleEndian(head[32..]) - BinaryPrimitives.ReadInt32LittleEndian(head[24..]);
            long height = (long)BinaryPrimitives.ReadInt32LittleEndian(head[36..]) - BinaryPrimitives.ReadInt32LittleEndian(head[28..]);
            return width > 0 && height > 0 ? Size((uint)Math.Min(uint.MaxValue, Math.Ceiling(width * 96 / 2540.0)), (uint)Math.Min(uint.MaxValue, Math.Ceiling(height * 96 / 2540.0))) : null;
        }
        if (head.Length >= 16 && BinaryPrimitives.ReadUInt32LittleEndian(head) == 0x9AC6CDD7)
        {
            var width = Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(head[10..]) - BinaryPrimitives.ReadInt16LittleEndian(head[6..]));
            var height = Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(head[12..]) - BinaryPrimitives.ReadInt16LittleEndian(head[8..]));
            var inch = BinaryPrimitives.ReadUInt16LittleEndian(head[14..]);
            return inch > 0 ? Size((uint)Math.Ceiling(width * 96.0 / inch), (uint)Math.Ceiling(height * 96.0 / inch)) : null;
        }
        return null;
    }

    private static (int Width, int Height)? WebP(ReadOnlySpan<byte> head)
    {
        var chunk = head.Slice(12, 4);
        if (chunk.SequenceEqual("VP8X"u8))
        {
            var width = head[24] | head[25] << 8 | head[26] << 16;
            var height = head[27] | head[28] << 8 | head[29] << 16;
            return Size((uint)width + 1, (uint)height + 1);
        }
        if (chunk.SequenceEqual("VP8L"u8) && head[20] == 0x2F)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(head[21..]);
            return Size((bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1);
        }
        if (chunk.SequenceEqual("VP8 "u8))
            return Size(BinaryPrimitives.ReadUInt16LittleEndian(head[26..]) & 0x3FFFu, BinaryPrimitives.ReadUInt16LittleEndian(head[28..]) & 0x3FFFu);
        return null;
    }

    /// <summary>The first frame header (SOFn) after the segments before it.</summary>
    private static (int Width, int Height)? Jpeg(Stream stream, ReadOnlySpan<byte> head)
    {
        // Continue from byte 2 of the file: the head was read already.
        var buffered = new Queue<byte>(head[2..].ToArray());
        int Next()
        {
            if (buffered.Count > 0)
                return buffered.Dequeue();
            return stream.ReadByte();
        }

        Span<byte> frame = stackalloc byte[5];
        for (var segment = 0; segment < MaxJpegSegments; segment++)
        {
            var marker = Next();
            if (marker < 0)
                return null;
            if (marker != 0xFF)
                return null;
            var code = Next();
            while (code == 0xFF)
                code = Next();
            if (code < 0 || code is 0xD9 or 0xDA)
                return null; // end of image or scan data before any frame header
            if (code is 0x01 or (>= 0xD0 and <= 0xD7))
                continue; // markers without a length
            var high = Next();
            var low = Next();
            if (high < 0 || low < 0)
                return null;
            var length = high << 8 | low;
            if (length < 2)
                return null;
            if (code is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                for (var i = 0; i < frame.Length; i++)
                {
                    var b = Next();
                    if (b < 0)
                        return null;
                    frame[i] = (byte)b;
                }
                return Size(BinaryPrimitives.ReadUInt16BigEndian(frame[3..]), BinaryPrimitives.ReadUInt16BigEndian(frame[1..]));
            }
            var skip = length - 2;
            for (; skip > 0 && buffered.Count > 0; skip--)
                buffered.Dequeue();
            if (stream.CanSeek)
            {
                if (stream.Seek(skip, SeekOrigin.Current) > stream.Length)
                    return null;
                continue;
            }
            for (; skip > 0; skip--)
            {
                if (Next() < 0)
                    return null;
            }
        }
        return null;
    }

    private static (int Width, int Height)? Size(uint width, uint height) =>
        width == 0 || height == 0 ? null : ((int)Math.Min(width, int.MaxValue), (int)Math.Min(height, int.MaxValue));

    private static int ReadUpTo(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        int read;
        while (total < buffer.Length && (read = stream.Read(buffer[total..])) > 0)
            total += read;
        return total;
    }
}
