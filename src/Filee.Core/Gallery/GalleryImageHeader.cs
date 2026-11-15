// Reads what a picture of a shared preset is from its first bytes: PNG, JPEG or WebP, its size, and whether it moves
// (APNG, animated WebP). Read before anything decodes the picture, so a file claiming 50,000 x 50,000 pixels is refused
// without being decoded. Every offset is checked against the length: the file comes from anyone.

using System.Buffers.Binary;

namespace Filee.Core.Gallery;

/// <summary>What <see cref="GalleryImageHeader.Read"/> found: the format ("png", "jpeg", "webp"), size and motion.</summary>
public readonly record struct GalleryImageInfo(string Format, int Width, int Height, bool Animated);

/// <summary>Reads the header of a PNG, JPEG or WebP picture.</summary>
public static class GalleryImageHeader
{
    /// <summary>The picture's format, size and whether it is animated; null when it is none of the three or broken.</summary>
    public static GalleryImageInfo? Read(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 24 && data[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return Png(data);
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return Jpeg(data);
        if (data.Length >= 30 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
            return WebP(data);
        return null;
    }

    private static GalleryImageInfo? Png(ReadOnlySpan<byte> data)
    {
        if (!data.Slice(12, 4).SequenceEqual("IHDR"u8))
            return null;
        var width = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
        if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue)
            return null;
        // An APNG says so with an acTL chunk before its first IDAT.
        var animated = false;
        var at = 8;
        while (at + 8 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(at, 4));
            var type = data.Slice(at + 4, 4);
            if (type.SequenceEqual("acTL"u8))
            {
                animated = true;
                break;
            }
            if (type.SequenceEqual("IDAT"u8) || length > int.MaxValue - 12)
                break;
            at += 12 + (int)length;
        }
        return new GalleryImageInfo("png", (int)width, (int)height, animated);
    }

    private static GalleryImageInfo? Jpeg(ReadOnlySpan<byte> data)
    {
        var at = 2;
        while (at + 4 <= data.Length)
        {
            if (data[at] != 0xFF)
                return null;
            var marker = data[at + 1];
            if (marker == 0xFF)
            {
                at++; // fill byte
                continue;
            }
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                at += 2;
                continue;
            }
            var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(at + 2, 2));
            if (length < 2)
                return null;
            // Start of frame: every SOFn but DHT (C4), JPG (C8) and DAC (CC).
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (at + 9 > data.Length)
                    return null;
                var height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(at + 5, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(at + 7, 2));
                return width == 0 || height == 0 ? null : new GalleryImageInfo("jpeg", width, height, false);
            }
            if (marker is 0xD9 or 0xDA)
                return null; // the end, or the image data, before any frame header
            at += 2 + length;
        }
        return null;
    }

    private static GalleryImageInfo? WebP(ReadOnlySpan<byte> data)
    {
        int? width = null, height = null;
        var animated = false;
        var at = 12;
        while (at + 8 <= data.Length)
        {
            var type = data.Slice(at, 4);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(at + 4, 4));
            var body = at + 8;
            if (length > int.MaxValue - 9)
                return null;
            var available = Math.Min((int)length, data.Length - body);
            var chunk = data.Slice(body, available);
            if (type.SequenceEqual("VP8X"u8) && chunk.Length >= 10)
            {
                animated |= (chunk[0] & 0x02) != 0;
                width = 1 + (chunk[4] | chunk[5] << 8 | chunk[6] << 16);
                height = 1 + (chunk[7] | chunk[8] << 8 | chunk[9] << 16);
            }
            else if (type.SequenceEqual("ANIM"u8) || type.SequenceEqual("ANMF"u8))
            {
                animated = true;
            }
            else if (type.SequenceEqual("VP8 "u8) && width is null && chunk.Length >= 10)
            {
                // Frame tag (3 bytes), start code 9D 01 2A, then 14-bit width and height.
                if (chunk[3] != 0x9D || chunk[4] != 0x01 || chunk[5] != 0x2A)
                    return null;
                width = BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(6, 2)) & 0x3FFF;
                height = BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(8, 2)) & 0x3FFF;
            }
            else if (type.SequenceEqual("VP8L"u8) && width is null && chunk.Length >= 5)
            {
                if (chunk[0] != 0x2F)
                    return null;
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(1, 4));
                width = 1 + (int)(bits & 0x3FFF);
                height = 1 + (int)((bits >> 14) & 0x3FFF);
            }
            // Chunks are padded to an even length.
            at = body + (int)length + (int)(length & 1);
        }
        return width is > 0 && height is > 0 ? new GalleryImageInfo("webp", width.Value, height.Value, animated) : null;
    }
}

/// <summary>What decoding a picture fully gave (<see cref="IGalleryImageDecoder"/>).</summary>
public readonly record struct GalleryImageDecode(int Width, int Height, int Frames, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>
/// Decodes a picture completely, to tell that it isn't broken past its header. The command line gives one built on
/// the image library Filee converts with; without one the folder check reads the headers only.
/// </summary>
public interface IGalleryImageDecoder
{
    GalleryImageDecode Decode(byte[] data);
}
