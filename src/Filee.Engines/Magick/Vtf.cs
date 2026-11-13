// Reads Valve's texture file (.vtf), the texture format of the Source engine (Garry's Mod, Counter-Strike: Source,
// Team Fortress 2, Half-Life 2 mods): versions 7.0 to 7.5, every pixel format VTFLib knows except the palette one (P8,
// which Source never shipped). The largest mipmap of every frame is read (an animated texture's frames, or one
// picture); of a cube map its first face, of a volume texture its first slice. DXT blocks are decoded here, so no DLL
// such as VTFLib is needed. VtfWriter writes the format.

using System.Buffers.Binary;
using ImageMagick;

namespace Filee.Engines.Magick;

internal static class Vtf
{
    /// <summary>VTF image formats (VTFLib's VTFImageFormat), as stored in the header.</summary>
    internal enum Format
    {
        Rgba8888 = 0,
        Abgr8888 = 1,
        Rgb888 = 2,
        Bgr888 = 3,
        Rgb565 = 4,
        I8 = 5,
        Ia88 = 6,
        P8 = 7,
        A8 = 8,
        Rgb888Bluescreen = 9,
        Bgr888Bluescreen = 10,
        Argb8888 = 11,
        Bgra8888 = 12,
        Dxt1 = 13,
        Dxt3 = 14,
        Dxt5 = 15,
        Bgrx8888 = 16,
        Bgr565 = 17,
        Bgrx5551 = 18,
        Bgra4444 = 19,
        Dxt1OneBitAlpha = 20,
        Bgra5551 = 21,
        Uv88 = 22,
        Uvwq8888 = 23,
        Rgba16161616F = 24,
        Rgba16161616 = 25,
        Uvlx8888 = 26,
    }

    /// <summary>"No image": a file without a thumbnail stores this as the low-resolution format.</summary>
    private const uint NoFormat = 0xFFFF_FFFF;

    /// <summary>TEXTUREFLAGS_ENVMAP: the texture is a cube map.</summary>
    private const uint EnvironmentMap = 0x4000;

    /// <summary>The resource (7.3 and later) holding the full-size image data: "\x30\0\0".</summary>
    private const uint HighResResource = 0x30;

    /// <summary>Resource flag: the entry holds its value itself and points to no data.</summary>
    private const byte NoDataFlag = 0x02;

    /// <summary>Most pixels Filee decodes from one texture, over all frames (a 4096 × 4096 texture with 64 frames).</summary>
    private const long MaxPixels = 4096L * 4096 * 64;

    /// <summary>What the header says about a texture.</summary>
    internal sealed record Info(int Major, int Minor, int Width, int Height, uint Flags, int Frames, Format Format,
        int Mipmaps, int Faces, int Depth, long DataOffset);

    /// <summary>The frames of the texture at full size, one image each.</summary>
    public static MagickImageCollection Read(string path)
    {
        var data = File.ReadAllBytes(path);
        var info = ReadInfo(data);
        var images = new MagickImageCollection();
        try
        {
            foreach (var rgba in DecodeFrames(data, info))
            {
                var image = new MagickImage();
                image.ReadPixels(rgba, new PixelReadSettings((uint)info.Width, (uint)info.Height, StorageType.Char, PixelMapping.RGBA));
                if (info.Frames > 1)
                    image.AnimationDelay = 10; // Source plays animated textures at the rate the material sets; 10 fps is common
                images.Add(image);
            }
        }
        catch
        {
            images.Dispose();
            throw;
        }
        return images;
    }

    /// <summary>Reads the header, and from version 7.3 on its resource list, of a VTF file.</summary>
    internal static Info ReadInfo(ReadOnlySpan<byte> data)
    {
        if (data.Length < 64 || !data[..4].SequenceEqual("VTF\0"u8))
            throw new InvalidDataException("This is not a Valve texture (VTF) file.");
        var major = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        var minor = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        if (major != 7 || minor is < 0 or > 5)
            throw new InvalidDataException($"VTF version {major}.{minor} is not supported (7.0 to 7.5 are).");

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        int width = BinaryPrimitives.ReadUInt16LittleEndian(data[16..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data[18..]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
        int frames = BinaryPrimitives.ReadUInt16LittleEndian(data[24..]);
        int firstFrame = BinaryPrimitives.ReadUInt16LittleEndian(data[26..]);
        var format = BinaryPrimitives.ReadUInt32LittleEndian(data[52..]);
        int mipmaps = data[56];
        var lowFormat = BinaryPrimitives.ReadUInt32LittleEndian(data[57..]);
        int lowWidth = data[61];
        int lowHeight = data[62];
        var depth = minor >= 2 && data.Length >= 65 ? BinaryPrimitives.ReadUInt16LittleEndian(data[63..]) : 1;

        if (width == 0 || height == 0)
            throw new InvalidDataException("The VTF file has no pixels.");
        if (format > (uint)Format.Uvlx8888)
            throw new InvalidDataException($"The VTF file uses an unknown image format ({format}).");
        if ((Format)format == Format.P8)
            throw new InvalidDataException("The VTF file uses the palette format P8, which Filee can't read.");
        frames = Math.Max(1, frames);
        mipmaps = Math.Max(1, mipmaps);
        depth = Math.Max(1, (int)depth);
        // A cube map has six faces; before 7.5 a seventh, the sphere map, unless the first frame is -1.
        var faces = (flags & EnvironmentMap) == 0 ? 1 : minor < 5 && firstFrame != 0xFFFF ? 7 : 6;
        if ((long)width * height * frames > MaxPixels)
            throw new InvalidDataException($"The texture is too large to read ({width} × {height}, {frames} frames).");

        long offset = -1;
        if (minor >= 3)
        {
            var resources = data.Length >= 72 ? BinaryPrimitives.ReadUInt32LittleEndian(data[68..]) : 0;
            for (var i = 0; i < resources && 80 + (i + 1) * 8 <= data.Length; i++)
            {
                var entry = data.Slice(80 + i * 8, 8);
                var tag = entry[0] | entry[1] << 8 | entry[2] << 16;
                if (tag == HighResResource && (entry[3] & NoDataFlag) == 0)
                    offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            }
            if (offset < 0)
                throw new InvalidDataException("The VTF file has no image data.");
        }
        else
        {
            var thumbnail = lowFormat == NoFormat || lowFormat > (uint)Format.Uvlx8888 || lowWidth == 0 || lowHeight == 0
                ? 0 : DataSize((Format)lowFormat, lowWidth, lowHeight);
            offset = headerSize + thumbnail;
        }
        return new Info(major, minor, width, height, flags, frames, (Format)format, mipmaps, faces, depth, offset);
    }

    /// <summary>Bytes of one image of <paramref name="format"/> at <paramref name="width"/> × <paramref name="height"/>.</summary>
    internal static int DataSize(Format format, int width, int height) => format switch
    {
        Format.Dxt1 or Format.Dxt1OneBitAlpha => Blocks(width) * Blocks(height) * 8,
        Format.Dxt3 or Format.Dxt5 => Blocks(width) * Blocks(height) * 16,
        _ => width * height * BytesPerPixel(format),
    };

    private static int Blocks(int side) => Math.Max(1, (side + 3) / 4);

    private static int BytesPerPixel(Format format) => format switch
    {
        Format.I8 or Format.A8 or Format.P8 => 1,
        Format.Rgb565 or Format.Ia88 or Format.Bgr565 or Format.Bgrx5551 or Format.Bgra4444 or Format.Bgra5551 or Format.Uv88 => 2,
        Format.Rgb888 or Format.Bgr888 or Format.Rgb888Bluescreen or Format.Bgr888Bluescreen => 3,
        Format.Rgba16161616F or Format.Rgba16161616 => 8,
        _ => 4,
    };

    /// <summary>
    /// The RGBA pixels of each frame's largest mipmap (first face, first slice). The data holds the smallest mipmap
    /// first; within a mipmap, frame after frame, each with its faces, each face with its slices.
    /// </summary>
    internal static List<byte[]> DecodeFrames(byte[] data, Info info)
    {
        var offset = info.DataOffset;
        for (var mipmap = info.Mipmaps - 1; mipmap >= 1; mipmap--)
        {
            var size = DataSize(info.Format, Math.Max(1, info.Width >> mipmap), Math.Max(1, info.Height >> mipmap));
            offset += (long)size * info.Frames * info.Faces * Math.Max(1, info.Depth >> mipmap);
        }

        var imageSize = DataSize(info.Format, info.Width, info.Height);
        var frames = new List<byte[]>(info.Frames);
        for (var frame = 0; frame < info.Frames; frame++)
        {
            var start = offset + (long)imageSize * info.Faces * info.Depth * frame;
            if (start < 0 || start + imageSize > data.Length)
            {
                if (frame == 0)
                    throw new InvalidDataException("The VTF file is cut off: its image data is missing.");
                break; // keep the frames that are there
            }
            frames.Add(Decode(data.AsSpan((int)start, imageSize), info.Format, info.Width, info.Height));
        }
        return frames;
    }

    /// <summary>Decodes one image to RGBA, 8 bits per channel, top row first.</summary>
    internal static byte[] Decode(ReadOnlySpan<byte> source, Format format, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        switch (format)
        {
            case Format.Dxt1:
            case Format.Dxt1OneBitAlpha:
            case Format.Dxt3:
            case Format.Dxt5:
                DecodeBlocks(source, format, width, height, rgba);
                return rgba;
        }

        var bytes = BytesPerPixel(format);
        for (var i = 0; i < width * height; i++)
        {
            var p = source.Slice(i * bytes, bytes);
            var o = i * 4;
            var v = bytes == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(p) : 0;
            byte r, g, b, a = 255;
            switch (format)
            {
                case Format.Rgba8888:
                case Format.Uvwq8888:
                case Format.Uvlx8888:
                    (r, g, b, a) = (p[0], p[1], p[2], p[3]);
                    break;
                case Format.Abgr8888:
                    (a, b, g, r) = (p[0], p[1], p[2], p[3]);
                    break;
                case Format.Argb8888:
                    (a, r, g, b) = (p[0], p[1], p[2], p[3]);
                    break;
                case Format.Bgra8888:
                    (b, g, r, a) = (p[0], p[1], p[2], p[3]);
                    break;
                case Format.Bgrx8888:
                    (b, g, r) = (p[0], p[1], p[2]);
                    break;
                case Format.Rgb888:
                case Format.Rgb888Bluescreen:
                    (r, g, b) = (p[0], p[1], p[2]);
                    break;
                case Format.Bgr888:
                case Format.Bgr888Bluescreen:
                    (b, g, r) = (p[0], p[1], p[2]);
                    break;
                case Format.I8:
                    r = g = b = p[0];
                    break;
                case Format.Ia88:
                    r = g = b = p[0];
                    a = p[1];
                    break;
                case Format.A8:
                    r = g = b = 0;
                    a = p[0];
                    break;
                case Format.Uv88:
                    (r, g, b) = (p[0], p[1], (byte)0);
                    break;
                case Format.Rgb565:
                    (r, g, b) = (Expand5(v), Expand6(v >> 5), Expand5(v >> 11));
                    break;
                case Format.Bgr565:
                    (b, g, r) = (Expand5(v), Expand6(v >> 5), Expand5(v >> 11));
                    break;
                case Format.Bgrx5551:
                    (b, g, r) = (Expand5(v), Expand5(v >> 5), Expand5(v >> 10));
                    break;
                case Format.Bgra5551:
                    (b, g, r) = (Expand5(v), Expand5(v >> 5), Expand5(v >> 10));
                    a = (v & 0x8000) != 0 ? (byte)255 : (byte)0;
                    break;
                case Format.Bgra4444:
                    (b, g, r, a) = (Nibble(v), Nibble(v >> 4), Nibble(v >> 8), Nibble(v >> 12));
                    break;
                case Format.Rgba16161616:
                    (r, g, b, a) = (Word(p), Word(p[2..]), Word(p[4..]), Word(p[6..]));
                    break;
                case Format.Rgba16161616F:
                    (r, g, b, a) = (Half(p), Half(p[2..]), Half(p[4..]), Half(p[6..]));
                    break;
                default:
                    throw new InvalidDataException($"The VTF image format {format} can't be read.");
            }
            // The blue-screen formats use pure blue for transparency.
            if (format is Format.Rgb888Bluescreen or Format.Bgr888Bluescreen && r == 0 && g == 0 && b == 255)
                (r, g, b, a) = ((byte)0, (byte)0, (byte)0, (byte)0);
            rgba[o] = r;
            rgba[o + 1] = g;
            rgba[o + 2] = b;
            rgba[o + 3] = a;
        }
        return rgba;
    }

    private static byte Expand5(int value)
    {
        value &= 0x1F;
        return (byte)(value << 3 | value >> 2);
    }

    private static byte Expand6(int value)
    {
        value &= 0x3F;
        return (byte)(value << 2 | value >> 4);
    }

    private static byte Nibble(int value) => (byte)((value & 0xF) * 17);

    private static byte Word(ReadOnlySpan<byte> p) => (byte)(BinaryPrimitives.ReadUInt16LittleEndian(p) / 257);

    /// <summary>A half-precision channel (HDR), clamped to 0–1.</summary>
    private static byte Half(ReadOnlySpan<byte> p)
    {
        var value = (float)BinaryPrimitives.ReadHalfLittleEndian(p);
        return float.IsNaN(value) ? (byte)0 : (byte)Math.Round(Math.Clamp(value, 0f, 1f) * 255);
    }

    /// <summary>Decodes DXT1, DXT3 and DXT5 (BC1 to BC3) blocks of 4 × 4 pixels.</summary>
    private static void DecodeBlocks(ReadOnlySpan<byte> source, Format format, int width, int height, byte[] rgba)
    {
        var blockSize = format is Format.Dxt3 or Format.Dxt5 ? 16 : 8;
        Span<byte> colours = stackalloc byte[16];
        Span<byte> alphas = stackalloc byte[8];
        var block = 0;
        for (var by = 0; by < Blocks(height); by++)
        {
            for (var bx = 0; bx < Blocks(width); bx++, block++)
            {
                var data = source.Slice(block * blockSize, blockSize);
                var colour = blockSize == 16 ? data[8..] : data;
                // DXT1 has a three-colour mode with transparent black; DXT3 and DXT5 always use four colours.
                Palette(colour, colours, threeColourMode: blockSize == 8);
                var indices = BinaryPrimitives.ReadUInt32LittleEndian(colour[4..]);
                if (format == Format.Dxt5)
                    AlphaPalette(data[0], data[1], alphas);
                var alphaBits = format == Format.Dxt5
                    ? data[2] | (ulong)data[3] << 8 | (ulong)data[4] << 16 | (ulong)data[5] << 24 | (ulong)data[6] << 32 | (ulong)data[7] << 40
                    : 0UL;

                for (var py = 0; py < 4; py++)
                {
                    var y = by * 4 + py;
                    if (y >= height)
                        break;
                    for (var px = 0; px < 4; px++)
                    {
                        var x = bx * 4 + px;
                        if (x >= width)
                            continue;
                        var pixel = py * 4 + px;
                        var index = (int)(indices >> (pixel * 2) & 3);
                        var o = (y * width + x) * 4;
                        rgba[o] = colours[index * 4];
                        rgba[o + 1] = colours[index * 4 + 1];
                        rgba[o + 2] = colours[index * 4 + 2];
                        rgba[o + 3] = format switch
                        {
                            Format.Dxt3 => (byte)((data[pixel / 2] >> (pixel % 2 * 4) & 0xF) * 17),
                            Format.Dxt5 => alphas[(int)(alphaBits >> (pixel * 3) & 7)],
                            _ => colours[index * 4 + 3],
                        };
                    }
                }
            }
        }
    }

    /// <summary>The four RGBA colours of a DXT colour block.</summary>
    private static void Palette(ReadOnlySpan<byte> block, Span<byte> colours, bool threeColourMode)
    {
        int c0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
        int c1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
        var fourColours = c0 > c1 || !threeColourMode;
        SetColour(colours, 0, Expand5(c0 >> 11), Expand6(c0 >> 5), Expand5(c0), 255);
        SetColour(colours, 1, Expand5(c1 >> 11), Expand6(c1 >> 5), Expand5(c1), 255);
        for (var channel = 0; channel < 3; channel++)
        {
            int first = colours[channel], second = colours[4 + channel];
            colours[8 + channel] = (byte)(fourColours ? (2 * first + second + 1) / 3 : (first + second) / 2);
            colours[12 + channel] = (byte)(fourColours ? (first + 2 * second + 1) / 3 : 0);
        }
        colours[11] = 255;
        // In the three-colour mode the fourth colour is transparent black.
        colours[15] = fourColours ? (byte)255 : (byte)0;
    }

    private static void SetColour(Span<byte> colours, int index, byte r, byte g, byte b, byte a)
    {
        colours[index * 4] = r;
        colours[index * 4 + 1] = g;
        colours[index * 4 + 2] = b;
        colours[index * 4 + 3] = a;
    }

    /// <summary>The eight alpha values of a DXT5 alpha block.</summary>
    private static void AlphaPalette(byte a0, byte a1, Span<byte> alphas)
    {
        alphas[0] = a0;
        alphas[1] = a1;
        if (a0 > a1)
        {
            for (var i = 1; i < 7; i++)
                alphas[i + 1] = (byte)(((7 - i) * a0 + i * a1 + 3) / 7);
        }
        else
        {
            for (var i = 1; i < 5; i++)
                alphas[i + 1] = (byte)(((5 - i) * a0 + i * a1 + 2) / 5);
            alphas[6] = 0;
            alphas[7] = 255;
        }
    }
}
