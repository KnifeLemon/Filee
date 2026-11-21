// Filee's own JPEG XR (HD Photo, .jxr / .wdp / .hdp) decoder, written from ITU-T T.832 (06/2019): the tag-based file
// (Annex A) and its codestream, in spatial and frequency mode, tiled or not, with hard or soft tiles, every overlap
// mode, every band combination, and the internal colour formats YONLY, YUV 4:2:0 / 4:2:2 / 4:4:4, YUVK and N-component;
// an alpha plane inside the codestream or as a second codestream of its own (planar alpha).
//
// The decoded picture is 8-bit BGRA: grey, RGB and CMYK output formats of 1, 5, 6, 8, 10 and 16 bits are scaled to
// 8 bits; half-float, float, fixed-point and RGBE ones (scRGB, linear light) are clipped to 0–1 and given the sRGB
// curve (no tone mapping); CMYK is turned into RGB without a profile; YUV output formats are read as the colour they
// code. Pre-multiplied alpha is divided out. The file's ICC profile is handed on, and its resolution, and the
// orientation its tags or codestream ask for is applied.
//
// Left out: N-component pictures of more than four components other than CMYK with alpha (8 at most are read), and
// pictures of more than MaxSamples samples. A damaged file throws InvalidDataException; one of a later codestream
// version NotSupportedException.

using System.Buffers.Binary;

namespace Filee.Engines.JpegXr;

/// <summary>A decoded JPEG XR picture: 8-bit B, G, R, A per pixel, alpha not pre-multiplied.</summary>
internal sealed record JpegXrImage(int Width, int Height, byte[] Bgra, bool HasAlpha, double DpiX, double DpiY, byte[]? IccProfile);

internal static class JpegXrDecoder
{
    /// <summary>Samples of all components and planes the decoder makes room for (four bytes each).</summary>
    public const long MaxSamples = 1L << 27;

    /// <summary>"II" 0xBC: the tag-based JPEG XR file of Annex A.</summary>
    public static bool IsJpegXr(ReadOnlySpan<byte> data) => data is [0x49, 0x49, 0xBC, _, ..];

    /// <summary>Decodes a JPEG XR file; <paramref name="maxPixels"/> bounds the picture's size.</summary>
    public static JpegXrImage Decode(byte[] file, long maxPixels)
    {
        if (!IsJpegXr(file) || file.Length < 8)
            throw new InvalidDataException("The file is not a JPEG XR picture.");
        var ifd = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));
        var tags = Tags(file, ifd);
        if (!tags.TryGetValue(0xBCC0, out var imageOffset) || !tags.TryGetValue(0xBCC1, out var imageBytes))
            throw new InvalidDataException("The JPEG XR file doesn't say where its picture is.");
        var image = Codestream(file, imageOffset, imageBytes, maxPixels);
        Plane? alpha = null;
        if (image.Alpha is null && tags.TryGetValue(0xBCC2, out var alphaOffset) && tags.TryGetValue(0xBCC3, out var alphaBytes) && alphaBytes > 0)
            alpha = Codestream(file, alphaOffset, alphaBytes, maxPixels).Primary;
        alpha ??= image.Alpha;
        var format = tags.TryGetValue(0xBC01, out var pixelFormat) ? (int)pixelFormat : -1;
        var premultiplied = image.Header.Premultiplied || format is 0x10 or 0x17 or 0x1A;
        var (width, height) = (image.Header.Width, image.Header.Height);
        if (alpha is not null && (alpha.Width != width || alpha.Height != height))
            alpha = null;
        var bgra = JpegXrOutput.Bgra(image.Header, image.Primary, alpha, premultiplied);
        var orientation = tags.TryGetValue(0xBC02, out var primary) ? (int)Math.Min(primary, 7) : image.Header.Orientation;
        (bgra, width, height) = Orient(bgra, width, height, orientation);
        var dpiX = Resolution(tags, 0xBC82);
        var dpiY = Resolution(tags, 0xBC83);
        byte[]? icc = null;
        if (Entry(file, ifd, 0x8773) is var (iccOffset, iccLength) && iccLength is > 0 and < 64 * 1024 * 1024 && iccOffset + iccLength <= (ulong)file.Length)
            icc = file.AsSpan((int)iccOffset, (int)iccLength).ToArray();
        return new JpegXrImage(width, height, bgra, alpha is not null, dpiX ?? dpiY ?? 96, dpiY ?? dpiX ?? 96, icc);
    }

    /// <summary>A decoded plane's samples after output formatting (see <see cref="JpegXrOutput"/>).</summary>
    internal sealed record Plane(JpegXrPlane Coded, int Width, int Height);

    private sealed record Coded(JpegXrHeader Header, Plane Primary, Plane? Alpha);

    /// <summary>Parses and reconstructs one codestream (the picture, or a planar alpha).</summary>
    private static Coded Codestream(byte[] file, ulong offset, ulong length, long maxPixels)
    {
        if (offset >= (ulong)file.Length || length == 0)
            throw new InvalidDataException("The JPEG XR file's picture lies outside it.");
        var end = (int)Math.Min((ulong)file.Length, offset + length);
        var bits = new JpegXrBits(file, (int)offset, end);
        var header = JpegXrHeader.Read(bits, maxPixels);
        var primary = new JpegXrPlane(header, alpha: false);
        primary.ReadHeader(bits);
        JpegXrPlane? alpha = null;
        if (header.AlphaPlane)
        {
            alpha = new JpegXrPlane(header, alpha: true);
            alpha.ReadHeader(bits);
        }
        var samples = (long)header.ExtendedWidth * header.ExtendedHeight * (primary.Components + (alpha is null ? 0 : 1));
        if (samples > MaxSamples)
            throw JpegXrHeader.TooLarge();
        // Every macroblock costs a few bits at the least (its DC flags): a short file can't claim a huge picture, and
        // the decoder doesn't make room for one.
        var macroblocks = (long)header.MbWidth * header.MbHeight * (alpha is null ? 1 : 2);
        if ((long)(end - bits.BytePosition) * 8 < macroblocks)
            throw new InvalidDataException("The JPEG XR picture's data is too short for its size: the file is damaged.");
        var tiles = new JpegXrTiles(header, primary, alpha, bits);
        tiles.Read();
        primary.Reconstruct();
        alpha?.Reconstruct();
        return new Coded(header, new Plane(primary, header.Width, header.Height), alpha is null ? null : new Plane(alpha, header.Width, header.Height));
    }

    /// <summary>The first IFD's entries whose value is a number (BYTE, USHORT, ULONG), by tag.</summary>
    private static Dictionary<int, ulong> Tags(byte[] file, uint ifd)
    {
        var tags = new Dictionary<int, ulong>();
        if (ifd > file.Length - 2)
            throw new InvalidDataException("The JPEG XR file's directory lies outside it.");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan((int)ifd));
        for (var i = 0; i < count; i++)
        {
            var at = (int)ifd + 2 + i * 12;
            if (at + 12 > file.Length)
                throw new InvalidDataException("The JPEG XR file's directory is cut off.");
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at));
            var type = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + 2));
            var elements = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 4));
            var value = file.AsSpan(at + 8, 4);
            switch (type)
            {
                case 1 when tag == 0xBC01 && elements == 16:
                    {
                        // PIXEL_FORMAT: a GUID elsewhere in the file; its last byte tells the formats apart.
                        var offset = BinaryPrimitives.ReadUInt32LittleEndian(value);
                        if (offset <= file.Length - 16)
                            tags[tag] = file[offset + 15];
                        break;
                    }
                case 1 when elements == 1:
                    tags[tag] = value[0];
                    break;
                case 3 when elements == 1:
                    tags[tag] = BinaryPrimitives.ReadUInt16LittleEndian(value);
                    break;
                case 4 when elements == 1:
                    tags[tag] = BinaryPrimitives.ReadUInt32LittleEndian(value);
                    break;
                case 11 when elements == 1:
                    tags[tag] = BinaryPrimitives.ReadUInt32LittleEndian(value);
                    break;
            }
        }
        return tags;
    }

    /// <summary>The data of tag <paramref name="tag"/> stored elsewhere in the file (its offset and length).</summary>
    private static (ulong Offset, ulong Length)? Entry(byte[] file, uint ifd, int tag)
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan((int)ifd));
        for (var i = 0; i < count; i++)
        {
            var at = (int)ifd + 2 + i * 12;
            if (at + 12 > file.Length)
                return null;
            if (BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at)) != tag)
                continue;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 4));
            return length <= 4 ? null : (BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 8)), length);
        }
        return null;
    }

    /// <summary>WIDTH_RESOLUTION / HEIGHT_RESOLUTION in dots per inch; null when absent or unusable.</summary>
    private static double? Resolution(Dictionary<int, ulong> tags, int tag) =>
        tags.TryGetValue(tag, out var bits) && BitConverter.UInt32BitsToSingle((uint)bits) is var dpi && float.IsFinite(dpi) && dpi is > 1 and < 100_000
            ? dpi
            : null;

    /// <summary>Applies SPATIAL_XFRM (Table 21: 90° clockwise turn, then horizontal and vertical flips).</summary>
    private static (byte[] Bgra, int Width, int Height) Orient(byte[] bgra, int width, int height, int orientation)
    {
        if (orientation == 0)
            return (bgra, width, height);
        var turn = orientation >= 4;
        var flipH = orientation is 2 or 3 or 6 or 7;
        var flipV = orientation is 1 or 3 or 5 or 7;
        var (w, h) = turn ? (height, width) : (width, height);
        var output = new byte[bgra.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // Turned 90° clockwise: (x, y) → (height − 1 − y, x).
                var (tx, ty) = turn ? (height - 1 - y, x) : (x, y);
                if (flipH)
                    tx = w - 1 - tx;
                if (flipV)
                    ty = h - 1 - ty;
                Buffer.BlockCopy(bgra, (y * width + x) * 4, output, (ty * w + tx) * 4, 4);
            }
        }
        return (output, w, h);
    }
}
