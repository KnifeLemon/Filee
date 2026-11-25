// Blizzard textures (.blp, #644): Warcraft III (BLP1: palette and 3-/4-channel JPEG) and World of Warcraft (BLP2:
// palette, DXT1/DXT3/DXT5 and uncompressed BGRA), read only. Every test texture is built in memory byte by byte.

using System.Buffers.Binary;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Magick;
using ImageMagick;

namespace Filee.Engines.Tests;

public class BlpTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static string Errors(ConversionJob job) =>
        string.Join("; ", job.Files.Select(f => $"{f.ErrorKey}: {f.ErrorDetail}"));

    private async Task<string> ConvertAsync(string source, string target)
    {
        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, Errors(job));
        return Assert.Single(job.Outputs);
    }

    private static void AssertNear(byte[] rgba, int width, int x, int y, int r, int g, int b, int a, int tolerance = 12)
    {
        var o = (y * width + x) * 4;
        Assert.InRange(rgba[o], Math.Max(0, r - tolerance), Math.Min(255, r + tolerance));
        Assert.InRange(rgba[o + 1], Math.Max(0, g - tolerance), Math.Min(255, g + tolerance));
        Assert.InRange(rgba[o + 2], Math.Max(0, b - tolerance), Math.Min(255, b + tolerance));
        Assert.InRange(rgba[o + 3], Math.Max(0, a - tolerance), Math.Min(255, a + tolerance));
    }

    private static byte[] MakePalette(params (byte R, byte G, byte B)[] entries)
    {
        var palette = new byte[256 * 4];
        for (var i = 0; i < entries.Length; i++)
        {
            palette[i * 4] = entries[i].B;
            palette[i * 4 + 1] = entries[i].G;
            palette[i * 4 + 2] = entries[i].R;
            palette[i * 4 + 3] = 0xAA; // unused in file
        }
        return palette;
    }

    private static byte[] BuildBlp1Palette(int width, int height, uint alphaBits, byte[] palette, byte[] mip0)
    {
        const int headerSize = 156;
        var mipOffset = (uint)(headerSize + palette.Length);
        var bytes = new byte[headerSize + palette.Length + mip0.Length];
        "BLP1"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1); // palette
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), alphaBits);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), mipOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(92), (uint)mip0.Length);
        palette.CopyTo(bytes, headerSize);
        mip0.CopyTo(bytes, (int)mipOffset);
        return bytes;
    }

    private static byte[] BuildBlp1Jpeg(int width, int height, uint alphaBits, byte[] jpeg, int splitAt)
    {
        const int headerSize = 156;
        var sharedHeader = jpeg[..splitAt];
        var mip0 = jpeg[splitAt..];
        var mipOffset = (uint)(headerSize + 4 + sharedHeader.Length);
        var bytes = new byte[mipOffset + mip0.Length];
        "BLP1"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0); // JPEG
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), alphaBits);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), mipOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(92), (uint)mip0.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(headerSize), (uint)sharedHeader.Length);
        sharedHeader.CopyTo(bytes, headerSize + 4);
        mip0.CopyTo(bytes, (int)mipOffset);
        return bytes;
    }

    private static byte[] BuildBlp2(int width, int height, byte encoding, byte alphaDepth, byte alphaType, byte[] mip0, byte[]? palette = null)
    {
        const int headerSize = 148;
        const int totalHeader = headerSize + 256 * 4;
        var bytes = new byte[totalHeader + mip0.Length];
        "BLP2"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1); // type
        bytes[8] = encoding;
        bytes[9] = alphaDepth;
        bytes[10] = alphaType;
        bytes[11] = 0; // hasMipmaps
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), totalHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(84), (uint)mip0.Length);
        palette?.CopyTo(bytes, headerSize);
        mip0.CopyTo(bytes, totalHeader);
        return bytes;
    }

    [Fact]
    public void Blp1_palette_decodes_exact_colours_for_all_alpha_depths()
    {
        // 8 × 1 strip: 8 pixels so every packing width (8, 4, 1, 0 bits) exercises full bytes.
        var palette = MakePalette((255, 10, 20), (30, 200, 40), (50, 60, 240), (220, 210, 100));
        byte[] indices = [0, 1, 2, 3, 3, 2, 1, 0];

        // alphaBits = 8
        byte[] alpha8 = [0, 32, 64, 96, 128, 160, 200, 255];
        var (w8, h8, rgba8) = Blp.Decode(BuildBlp1Palette(8, 1, 8, palette, [.. indices, .. alpha8]));
        Assert.Equal((8, 1), (w8, h8));
        Assert.Equal(new byte[] { 255, 10, 20, 0 }, rgba8[0..4]);
        Assert.Equal(new byte[] { 30, 200, 40, 32 }, rgba8[4..8]);
        Assert.Equal(new byte[] { 50, 60, 240, 64 }, rgba8[8..12]);
        Assert.Equal(new byte[] { 220, 210, 100, 96 }, rgba8[12..16]);
        Assert.Equal(new byte[] { 255, 10, 20, 255 }, rgba8[28..32]);

        // alphaBits = 4: low nibble first, value × 17
        byte[] alpha4 = [0xF1, 0xA5, 0x30, 0x8C]; // nibbles: 1, 15, 5, 10, 0, 3, 12, 8
        var (_, _, rgba4) = Blp.Decode(BuildBlp1Palette(8, 1, 4, palette, [.. indices, .. alpha4]));
        byte[] expectedAlpha4 = [17, 255, 85, 170, 0, 51, 204, 136];
        for (var i = 0; i < 8; i++)
            Assert.Equal(expectedAlpha4[i], rgba4[i * 4 + 3]);

        // alphaBits = 1: lowest bit first, 0 or 255
        byte[] alpha1 = [0b1010_0101]; // pixels 0,2,5,7 opaque; 1,3,4,6 transparent
        var (_, _, rgba1) = Blp.Decode(BuildBlp1Palette(8, 1, 1, palette, [.. indices, .. alpha1]));
        byte[] expectedAlpha1 = [255, 0, 255, 0, 0, 255, 0, 255];
        for (var i = 0; i < 8; i++)
            Assert.Equal(expectedAlpha1[i], rgba1[i * 4 + 3]);

        // alphaBits = 0: opaque
        var (_, _, rgba0) = Blp.Decode(BuildBlp1Palette(8, 1, 0, palette, indices));
        Assert.Equal(new byte[] { 255, 10, 20, 255 }, rgba0[0..4]);
        Assert.Equal(new byte[] { 50, 60, 240, 255 }, rgba0[8..12]);
        for (var i = 0; i < 8; i++)
            Assert.Equal(255, rgba0[i * 4 + 3]);
    }

    [Fact]
    public void Blp1_jpeg_3_channel_swaps_red_and_blue_back()
    {
        // Desired final colour: R = 210, G = 90, B = 30. BLP1 stores blue first, so encode (30, 90, 210) in RGB JPEG.
        byte[] jpeg;
        using (var src = new MagickImage(MagickColor.FromRgb(30, 90, 210), 8, 8))
        {
            src.Quality = 100;
            jpeg = src.ToByteArray(MagickFormat.Jpeg);
        }

        // Split at an arbitrary byte into shared header + mip 0 data.
        var blp = BuildBlp1Jpeg(8, 8, alphaBits: 0, jpeg, splitAt: jpeg.Length / 3);
        var (w, h, rgba) = Blp.Decode(blp);
        Assert.Equal((8, 8), (w, h));
        AssertNear(rgba, 8, 4, 4, r: 210, g: 90, b: 30, a: 255);
    }

    [Fact]
    public void Blp1_jpeg_4_channel_reads_the_stored_samples_as_bgra()
    {
        // The JPEG must store B = 30, G = 160, R = 220, A = 110. Magick.NET writes a CMYK JPEG Adobe-style, inverting
        // every channel on the way out, so it is handed 255 - sample to leave the plain samples in the file.
        var cmyk = new byte[8 * 8 * 4];
        for (var i = 0; i < 8 * 8; i++)
        {
            cmyk[i * 4] = 255 - 30;      // B
            cmyk[i * 4 + 1] = 255 - 160; // G
            cmyk[i * 4 + 2] = 255 - 220; // R
            cmyk[i * 4 + 3] = 255 - 110; // A
        }
        byte[] jpeg;
        using (var src = new MagickImage())
        {
            src.ReadPixels(cmyk, new PixelReadSettings(8, 8, StorageType.Char, "CMYK"));
            src.ColorSpace = ColorSpace.CMYK;
            src.Quality = 100;
            jpeg = src.ToByteArray(MagickFormat.Jpeg);
        }

        var blpWithAlpha = BuildBlp1Jpeg(8, 8, alphaBits: 8, jpeg, splitAt: 100);
        var (w, h, rgba) = Blp.Decode(blpWithAlpha);
        Assert.Equal((8, 8), (w, h));
        AssertNear(rgba, 8, 4, 4, r: 220, g: 160, b: 30, a: 110);

        // When alphaBits is 0 the result is opaque regardless of the 4th channel.
        var blpOpaque = BuildBlp1Jpeg(8, 8, alphaBits: 0, jpeg, splitAt: 100);
        var (_, _, opaqueRgba) = Blp.Decode(blpOpaque);
        AssertNear(opaqueRgba, 8, 4, 4, r: 220, g: 160, b: 30, a: 255);
    }

    [Fact]
    public void Blp2_dxt1_dxt3_and_dxt5_decode_correctly()
    {
        // DXT1 four-colour block: c0 = pure red (0xF800), c1 = pure blue (0x001F), first row = 0, 1, 2, 3.
        byte[] dxt1 = [0x00, 0xF8, 0x1F, 0x00, 0b11_10_01_00, 0, 0, 0];
        var (_, _, rgba1) = Blp.Decode(BuildBlp2(4, 4, encoding: 2, alphaDepth: 0, alphaType: 0, dxt1));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, rgba1[0..4]);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, rgba1[4..8]);
        Assert.Equal(new byte[] { 170, 0, 85, 255 }, rgba1[8..12]);
        Assert.Equal(new byte[] { 85, 0, 170, 255 }, rgba1[12..16]);

        // DXT1 three-colour punch-through block (c0 <= c1): transparent when alphaDepth == 1, opaque when alphaDepth == 0.
        byte[] punch = [0x1F, 0x00, 0x00, 0xF8, 0b11_11_11_11, 0, 0, 0];
        var (_, _, punch1) = Blp.Decode(BuildBlp2(4, 4, encoding: 2, alphaDepth: 1, alphaType: 0, punch));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, punch1[0..4]);
        var (_, _, punchOpaque) = Blp.Decode(BuildBlp2(4, 4, encoding: 2, alphaDepth: 0, alphaType: 0, punch));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, punchOpaque[0..4]);

        // DXT3: explicit 4-bit alpha per pixel (pixel 0 = 0x3 -> 51, pixel 1 = 0xC -> 204), colour = red.
        byte[] dxt3 = [0xC3, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0xF8, 0x1F, 0x00, 0, 0, 0, 0];
        var (_, _, rgba3) = Blp.Decode(BuildBlp2(4, 4, encoding: 2, alphaDepth: 8, alphaType: 1, dxt3));
        Assert.Equal(new byte[] { 255, 0, 0, 51 }, rgba3[0..4]);
        Assert.Equal(new byte[] { 255, 0, 0, 204 }, rgba3[4..8]);

        // DXT3 selected by alphaDepth == 4 as well.
        var (_, _, rgba3Depth4) = Blp.Decode(BuildBlp2(4, 4, encoding: 2, alphaDepth: 4, alphaType: 0, dxt3));
        Assert.Equal(new byte[] { 255, 0, 0, 51 }, rgba3Depth4[0..4]);

        // DXT5: interpolated alpha endpoints 255 and 0, pixel 0 picks index 1 (= 0), pixel 1 picks index 0 (= 255).
        byte[] dxt5 = [255, 0, 0b001, 0, 0, 0, 0, 0, 0x00, 0xF8, 0x1F, 0x00, 0, 0, 0, 0];
        var (_, _, rgba5) = Blp.Decode(BuildBlp2(4, 4, encoding: 2, alphaDepth: 8, alphaType: 7, dxt5));
        Assert.Equal(new byte[] { 255, 0, 0, 0 }, rgba5[0..4]);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, rgba5[4..8]);
    }

    [Fact]
    public void Blp2_palette_and_uncompressed_bgra_decode_correctly()
    {
        // BLP2 palette with 4-bit alpha (2 × 2)
        var palette = MakePalette((10, 20, 30), (40, 50, 60));
        byte[] mipPalette = [0, 1, 1, 0, 0xF2, 0x08]; // alphas: 2*17=34, 15*17=255, 8*17=136, 0
        var (_, _, palRgba) = Blp.Decode(BuildBlp2(2, 2, encoding: 1, alphaDepth: 4, alphaType: 0, mipPalette, palette));
        Assert.Equal(new byte[] { 10, 20, 30, 34 }, palRgba[0..4]);
        Assert.Equal(new byte[] { 40, 50, 60, 255 }, palRgba[4..8]);
        Assert.Equal(new byte[] { 40, 50, 60, 136 }, palRgba[8..12]);
        Assert.Equal(new byte[] { 10, 20, 30, 0 }, palRgba[12..16]);

        // BLP2 uncompressed BGRA (2 × 2): red, green, blue, half-transparent white
        byte[] bgra = [0, 0, 255, 255, 0, 255, 0, 200, 255, 0, 0, 100, 255, 255, 255, 64];
        var (_, _, bgraRgba) = Blp.Decode(BuildBlp2(2, 2, encoding: 3, alphaDepth: 8, alphaType: 0, bgra));
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 255, 0, 200, 0, 0, 255, 100, 255, 255, 255, 64 }, bgraRgba);

        // alphaDepth = 0 forces opaque even on BGRA
        var (_, _, bgraOpaque) = Blp.Decode(BuildBlp2(2, 2, encoding: 3, alphaDepth: 0, alphaType: 0, bgra));
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255 }, bgraOpaque);
    }

    [Fact]
    public async Task A_blp_converts_to_png_through_the_converter()
    {
        byte[] bgra = [0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 128];
        var path = Path.Combine(fx.NewFolder(), "spell.blp");
        File.WriteAllBytes(path, BuildBlp2(2, 2, encoding: 3, alphaDepth: 8, alphaType: 0, bgra));

        using var png = new MagickImage(await ConvertAsync(path, "png"));
        Assert.Equal((2u, 2u), (png.Width, png.Height));
        var rgba = png.GetPixels().ToByteArray(PixelMapping.RGBA)!;
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 128 }, rgba);
    }

    private static ConversionRefusedException AssertRefused(byte[] data)
    {
        var ex = Assert.Throws<ConversionRefusedException>(() => Blp.Decode(data));
        Assert.Equal("error.unsupported_source", ex.ErrorKey);
        return ex;
    }

    [Fact]
    public void Invalid_headers_and_blp0_are_refused()
    {
        // BLP0 (Warcraft III beta, external mipmaps)
        var blp0 = new byte[156];
        "BLP0"u8.CopyTo(blp0);
        AssertRefused(blp0);

        // Bad magic and short files
        AssertRefused([]);
        AssertRefused("NOPE"u8.ToArray());
        AssertRefused(new byte[160]);

        // Zero width or height, and over 65535
        var zeroWidth = BuildBlp2(0, 4, encoding: 3, alphaDepth: 8, alphaType: 0, new byte[16]);
        AssertRefused(zeroWidth);
        var tooWide = BuildBlp2(65_536, 1, encoding: 3, alphaDepth: 8, alphaType: 0, new byte[16]);
        AssertRefused(tooWide);

        // Over pixel budget (20,000 × 20,000 = 400M > 256M) refused before allocating
        var overBudget = BuildBlp2(20_000, 20_000, encoding: 3, alphaDepth: 8, alphaType: 0, new byte[16]);
        AssertRefused(overBudget);
        Assert.False(Blp.FitsBudget(20_000, 20_000));
        Assert.True(Blp.FitsBudget(16_384, 16_384));
    }

    [Fact]
    public async Task Truncated_and_out_of_bounds_files_are_refused()
    {
        // Offset past end of file
        var badOffset = BuildBlp2(4, 4, encoding: 2, alphaDepth: 0, alphaType: 0, new byte[8]);
        BinaryPrimitives.WriteUInt32LittleEndian(badOffset.AsSpan(20), (uint)(badOffset.Length + 10));
        AssertRefused(badOffset);

        // Truncated mip data (4 × 4 DXT1 needs 8 bytes; give 5)
        var truncatedDxt = BuildBlp2(4, 4, encoding: 2, alphaDepth: 0, alphaType: 0, new byte[5]);
        AssertRefused(truncatedDxt);

        // Truncated palette mip data (4 × 4 with 8-bit alpha needs 32 bytes; give 20)
        var truncatedPalette = BuildBlp1Palette(4, 4, 8, MakePalette((1, 2, 3)), new byte[20]);
        AssertRefused(truncatedPalette);

        // JPEG header size past the end of the file
        var badJpegHeader = new byte[164];
        "BLP1"u8.CopyTo(badJpegHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(badJpegHeader.AsSpan(4), 0); // JPEG
        BinaryPrimitives.WriteUInt32LittleEndian(badJpegHeader.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(badJpegHeader.AsSpan(16), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(badJpegHeader.AsSpan(28), 160);
        BinaryPrimitives.WriteUInt32LittleEndian(badJpegHeader.AsSpan(92), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(badJpegHeader.AsSpan(156), 9999);
        AssertRefused(badJpegHeader);

        // Invalid JPEG stream
        var corruptJpeg = BuildBlp1Jpeg(4, 4, 0, [0x01, 0x02, 0x03, 0x04, 0x05, 0x06], splitAt: 2);
        AssertRefused(corruptJpeg);

        // Through the converter, a refused file fails cleanly with error.unsupported_source.
        var path = Path.Combine(fx.NewFolder(), "broken.blp");
        File.WriteAllBytes(path, truncatedDxt);
        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "png" });
        Assert.Equal(JobState.Failed, job.State);
        Assert.Empty(job.Outputs);
        Assert.Equal("error.unsupported_source", Assert.Single(job.Files).ErrorKey);
    }
}
