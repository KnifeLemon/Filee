// Writes Valve's texture file (.vtf), the texture format of the Source engine (Garry's Mod, Counter-Strike: Source,
// Team Fortress 2, Half-Life 2 mods), as VTFEdit does by default: version 7.2, the sides rounded to the nearest power of
// two, every mipmap down to 1 × 1, DXT1 for an opaque picture and DXT5 for one with transparency, and a DXT1 thumbnail
// of at most 16 × 16. ImageMagick resizes and compresses the DXT blocks (its DDS writer, without a header); several
// frames (an animated GIF, the pages of a TIFF) become the frames of one animated texture. See Vtf for the reader.

using System.Buffers.Binary;
using ImageMagick;

namespace Filee.Engines.Magick;

internal static class VtfWriter
{
    /// <summary>The longest side the Source engine loads.</summary>
    public const int MaxSide = 4096;

    /// <summary>Size of a version 7.2 header, as VTFLib writes it (65 bytes of fields, padded to 16).</summary>
    private const int HeaderSize = 80;

    /// <summary>The thumbnail Source keeps of every texture is at most this many pixels on a side.</summary>
    private const int LowResSide = 16;

    /// <summary>A DDS file as ImageMagick writes it: "DDS " and a 124-byte header before the blocks.</summary>
    private const int DdsHeaderSize = 128;

    /// <summary>TEXTUREFLAGS_EIGHTBITALPHA: the texture has an alpha channel of more than one bit.</summary>
    private const uint EightBitAlpha = 0x2000;

    /// <summary>
    /// Writes <paramref name="frames"/> as one texture: a still picture for one frame, an animated texture for several,
    /// every frame at the size of the first.
    /// </summary>
    public static void Write(IReadOnlyList<IMagickImage<byte>> frames, string path)
    {
        if (frames.Count == 0)
            throw new ArgumentException("A texture needs at least one picture.", nameof(frames));
        if (frames.Count > ushort.MaxValue)
            throw new InvalidDataException($"A texture holds at most {ushort.MaxValue} frames.");

        var (width, height) = TextureSize((int)frames[0].Width, (int)frames[0].Height);
        var transparent = frames.Any(frame => !frame.IsOpaque);
        var format = transparent ? Vtf.Format.Dxt5 : Vtf.Format.Dxt1;
        var mipmaps = MipmapCount(width, height);
        var (lowWidth, lowHeight) = LowResSize(width, height);

        // Every mipmap is made from the full-size texture, not from the one before it, so no blur adds up.
        var textures = new List<IMagickImage<byte>>(frames.Count);
        try
        {
            foreach (var frame in frames)
            {
                var texture = frame.Clone();
                textures.Add(texture);
                texture.Strip();
                Resize(texture, width, height);
            }

            using var stream = File.Create(path);
            stream.Write(Header(width, height, frames.Count, transparent ? EightBitAlpha : 0, Reflectivity(textures[0]),
                format, mipmaps, lowWidth, lowHeight));
            stream.Write(Compress(textures[0], lowWidth, lowHeight, dxt5: false));
            // Smallest mipmap first; in each mipmap every frame in order.
            for (var mipmap = mipmaps - 1; mipmap >= 0; mipmap--)
            {
                var mipWidth = Math.Max(1, width >> mipmap);
                var mipHeight = Math.Max(1, height >> mipmap);
                foreach (var texture in textures)
                    stream.Write(Compress(texture, mipWidth, mipHeight, transparent));
            }
        }
        finally
        {
            foreach (var texture in textures)
                texture.Dispose();
        }
    }

    /// <summary>
    /// The texture's size: each side rounded to the nearest power of two (Source draws other sizes wrong or not at all),
    /// at most <see cref="MaxSide"/>.
    /// </summary>
    internal static (int Width, int Height) TextureSize(int width, int height) => (PowerOfTwo(width), PowerOfTwo(height));

    private static int PowerOfTwo(int side)
    {
        var exponent = (int)Math.Round(Math.Log2(Math.Max(1, side)), MidpointRounding.AwayFromZero);
        return Math.Min(MaxSide, 1 << Math.Max(0, exponent));
    }

    /// <summary>Every mipmap down to 1 × 1: one more than the times the longer side halves.</summary>
    internal static int MipmapCount(int width, int height) => 1 + (int)Math.Log2(Math.Max(width, height));

    /// <summary>The largest mipmap that fits <see cref="LowResSide"/>, as VTFLib chooses the thumbnail.</summary>
    internal static (int Width, int Height) LowResSize(int width, int height)
    {
        while (width > LowResSide || height > LowResSide)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }
        return (width, height);
    }

    private static void Resize(IMagickImage<byte> image, int width, int height)
    {
        if (image.Width == width && image.Height == height)
            return;
        image.FilterType = FilterType.Lanczos;
        image.Resize(new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = true });
    }

    /// <summary>
    /// The DXT blocks of <paramref name="texture"/> at <paramref name="width"/> × <paramref name="height"/>: ImageMagick's
    /// DDS writer without mipmaps, with its header cut off.
    /// </summary>
    private static byte[] Compress(IMagickImage<byte> texture, int width, int height, bool dxt5)
    {
        using var level = texture.Clone();
        Resize(level, width, height);
        level.Alpha(dxt5 ? AlphaAction.Set : AlphaAction.Off);
        level.Settings.SetDefine(MagickFormat.Dds, "compression", dxt5 ? "dxt5" : "dxt1");
        level.Settings.SetDefine(MagickFormat.Dds, "mipmaps", "0");
        level.Settings.SetDefine(MagickFormat.Dds, "cluster-fit", "true");
        var dds = level.ToByteArray(MagickFormat.Dds);
        var size = Vtf.DataSize(dxt5 ? Vtf.Format.Dxt5 : Vtf.Format.Dxt1, width, height);
        if (dds.Length != DdsHeaderSize + size)
            throw new InvalidDataException($"ImageMagick wrote {dds.Length} bytes of DDS for a {width} × {height} texture, not {DdsHeaderSize + size}.");
        return dds.AsSpan(DdsHeaderSize, size).ToArray();
    }

    /// <summary>
    /// The average colour of the texture in linear light, which Source uses for radiosity (light bounced off the
    /// surface), as VTFLib computes it.
    /// </summary>
    internal static (float R, float G, float B) Reflectivity(IMagickImage<byte> texture)
    {
        var rgba = texture.GetPixels().ToByteArray(PixelMapping.RGBA)
            ?? throw new InvalidOperationException("The texture's pixels could not be read.");
        Span<double> linear = stackalloc double[256];
        for (var i = 0; i < 256; i++)
            linear[i] = Math.Pow(i / 255.0, 2.2);
        double r = 0, g = 0, b = 0;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            r += linear[rgba[i]];
            g += linear[rgba[i + 1]];
            b += linear[rgba[i + 2]];
        }
        var pixels = Math.Max(1, rgba.Length / 4);
        return ((float)(r / pixels), (float)(g / pixels), (float)(b / pixels));
    }

    /// <summary>The 80-byte header of a version 7.2 file (packed fields, as Valve's and VTFLib's readers expect).</summary>
    private static byte[] Header(int width, int height, int frames, uint flags, (float R, float G, float B) reflectivity,
        Vtf.Format format, int mipmaps, int lowWidth, int lowHeight)
    {
        var header = new byte[HeaderSize];
        var span = header.AsSpan();
        "VTF\0"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 7);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], HeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(span[16..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(span[18..], (ushort)height);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], flags);
        BinaryPrimitives.WriteUInt16LittleEndian(span[24..], (ushort)frames);
        BinaryPrimitives.WriteUInt16LittleEndian(span[26..], 0); // first frame
        BinaryPrimitives.WriteSingleLittleEndian(span[32..], reflectivity.R);
        BinaryPrimitives.WriteSingleLittleEndian(span[36..], reflectivity.G);
        BinaryPrimitives.WriteSingleLittleEndian(span[40..], reflectivity.B);
        BinaryPrimitives.WriteSingleLittleEndian(span[48..], 1f); // bump map scale
        BinaryPrimitives.WriteUInt32LittleEndian(span[52..], (uint)format);
        span[56] = (byte)mipmaps;
        BinaryPrimitives.WriteUInt32LittleEndian(span[57..], (uint)Vtf.Format.Dxt1);
        span[61] = (byte)lowWidth;
        span[62] = (byte)lowHeight;
        BinaryPrimitives.WriteUInt16LittleEndian(span[63..], 1); // depth
        return header;
    }
}
