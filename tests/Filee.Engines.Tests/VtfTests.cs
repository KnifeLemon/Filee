// Valve textures (.vtf), the Source engine's format (Garry's Mod and others): pictures become a version 7.2 texture with
// power-of-two sides, every mipmap and a thumbnail, DXT1 when opaque and DXT5 with transparency; an animation becomes an
// animated texture. The other way, VTFs of every version Filee reads become pictures again. The test textures are
// built here byte by byte.

using System.Buffers.Binary;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Engines.Magick;
using ImageMagick;

namespace Filee.Engines.Tests;

public class VtfTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static string Errors(ConversionJob job) =>
        string.Join("; ", job.Files.Select(f => $"{f.ErrorKey}: {f.ErrorDetail}"));

    private async Task<string> ConvertAsync(string source, string target)
    {
        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = target });
        Assert.True(job.State == JobState.Completed, Errors(job));
        return Assert.Single(job.Outputs);
    }

    /// <summary>
    /// A PNG of <paramref name="width"/> × <paramref name="height"/>: its left half one RGBA colour, its right half another.
    /// </summary>
    private string TwoColours(string name, int width, int height, byte[] left, byte[] right)
    {
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
            (i % width < width / 2 ? left : right).CopyTo(rgba, i * 4);
        using var image = new MagickImage();
        image.ReadPixels(rgba, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGBA));
        var path = Path.Combine(fx.NewFolder(), name);
        image.Write(path, MagickFormat.Png32);
        return path;
    }

    private static void AssertNear(byte[] rgba, int width, int x, int y, int r, int g, int b, int a, int tolerance = 12)
    {
        var o = (y * width + x) * 4;
        Assert.InRange(rgba[o], r - tolerance, r + tolerance);
        Assert.InRange(rgba[o + 1], g - tolerance, g + tolerance);
        Assert.InRange(rgba[o + 2], b - tolerance, b + tolerance);
        Assert.InRange(rgba[o + 3], a - tolerance, a + tolerance);
    }

    /// <summary>Header, thumbnail and every mipmap: nothing more, nothing less.</summary>
    private static long ExpectedLength(Vtf.Info info)
    {
        var (lowWidth, lowHeight) = VtfWriter.LowResSize(info.Width, info.Height);
        long length = 80 + Vtf.DataSize(Vtf.Format.Dxt1, lowWidth, lowHeight);
        for (var mipmap = 0; mipmap < info.Mipmaps; mipmap++)
            length += (long)Vtf.DataSize(info.Format, Math.Max(1, info.Width >> mipmap), Math.Max(1, info.Height >> mipmap)) * info.Frames;
        return length;
    }

    [Fact]
    public async Task An_opaque_picture_becomes_a_dxt1_texture_with_power_of_two_sides()
    {
        var png = TwoColours("brick.png", 100, 60, [200, 40, 40, 255], [30, 90, 220, 255]);
        var output = await ConvertAsync(png, "vtf");
        Assert.Equal("brick.vtf", Path.GetFileName(output));
        Assert.Equal("vtf", FormatRegistry.Detect(output)!.Id);

        var bytes = File.ReadAllBytes(output);
        var info = Vtf.ReadInfo(bytes);
        Assert.Equal((7, 2), (info.Major, info.Minor));
        Assert.Equal((128, 64), (info.Width, info.Height));
        Assert.Equal(Vtf.Format.Dxt1, info.Format);
        Assert.Equal(8, info.Mipmaps); // 128, 64, 32, 16, 8, 4, 2, 1
        Assert.Equal(1, info.Frames);
        Assert.Equal(0u, info.Flags);
        Assert.Equal(ExpectedLength(info), bytes.Length);

        var rgba = Assert.Single(Vtf.DecodeFrames(bytes, info));
        AssertNear(rgba, 128, 10, 30, 200, 40, 40, 255);
        AssertNear(rgba, 128, 117, 30, 30, 90, 220, 255);
    }

    [Fact]
    public async Task A_transparent_picture_becomes_a_dxt5_texture_with_its_alpha()
    {
        var png = TwoColours("glass.png", 64, 64, [0, 0, 0, 0], [20, 200, 60, 255]);
        var bytes = File.ReadAllBytes(await ConvertAsync(png, "vtf"));
        var info = Vtf.ReadInfo(bytes);
        Assert.Equal((64, 64), (info.Width, info.Height));
        Assert.Equal(Vtf.Format.Dxt5, info.Format);
        Assert.Equal(0x2000u, info.Flags); // eight-bit alpha
        Assert.Equal(7, info.Mipmaps);
        Assert.Equal(ExpectedLength(info), bytes.Length);

        var rgba = Assert.Single(Vtf.DecodeFrames(bytes, info));
        Assert.InRange(rgba[(32 * 64 + 8) * 4 + 3], 0, 8);
        AssertNear(rgba, 64, 56, 32, 20, 200, 60, 255);
    }

    [Fact]
    public async Task An_animated_gif_becomes_an_animated_texture()
    {
        var path = Path.Combine(fx.NewFolder(), "spray.gif");
        using (var gif = new MagickImageCollection())
        {
            foreach (var colour in new[] { MagickColors.Red, MagickColors.Lime, MagickColors.Blue })
                gif.Add(new MagickImage(colour, 32, 32) { AnimationDelay = 10 });
            gif.Write(path, MagickFormat.Gif);
        }
        var bytes = File.ReadAllBytes(await ConvertAsync(path, "vtf"));
        var info = Vtf.ReadInfo(bytes);
        Assert.Equal(3, info.Frames);
        Assert.Equal((32, 32), (info.Width, info.Height));
        Assert.Equal(ExpectedLength(info), bytes.Length);

        var frames = Vtf.DecodeFrames(bytes, info);
        Assert.Equal(3, frames.Count);
        AssertNear(frames[0], 32, 16, 16, 255, 0, 0, 255);
        AssertNear(frames[1], 32, 16, 16, 0, 255, 0, 255);
        AssertNear(frames[2], 32, 16, 16, 0, 0, 255, 255);
    }

    [Fact]
    public async Task A_texture_comes_back_as_a_picture()
    {
        var png = TwoColours("metal.png", 256, 256, [250, 200, 20, 255], [40, 40, 40, 255]);
        var vtf = await ConvertAsync(png, "vtf");
        var back = await ConvertAsync(vtf, "png");
        using var image = new MagickImage(back);
        Assert.Equal((256u, 256u), (image.Width, image.Height));
        var rgba = image.GetPixels().ToByteArray(PixelMapping.RGBA)!;
        AssertNear(rgba, 256, 40, 128, 250, 200, 20, 255);
        AssertNear(rgba, 256, 215, 128, 40, 40, 40, 255);
    }

    /// <summary>
    /// A texture of 2 × 2 pixels in an uncompressed format, with a 1 × 1 mipmap before it and no thumbnail: version 7.2
    /// with the data after the header, or 7.5 with a resource list pointing to it.
    /// </summary>
    private static byte[] BuildTexture(int minor, Vtf.Format format, byte[] mip1, byte[] mip0, int frames = 1)
    {
        var headerSize = minor >= 3 ? 96 : 80;
        var header = new byte[headerSize];
        "VTF\0"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)minor);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)headerSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(16), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(18), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(24), (ushort)frames);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(52), (uint)format);
        header[56] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(57), 0xFFFF_FFFF); // no thumbnail
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(63), 1);
        if (minor >= 3)
        {
            // Two resources: the (absent) thumbnail and the image data right after the header.
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(68), 2);
            header[80] = 0x01;
            header[83] = 0x02;
            header[88] = 0x30;
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(92), (uint)headerSize);
        }
        var data = new List<byte>(header);
        for (var i = 0; i < frames; i++)
            data.AddRange(mip1);
        for (var i = 0; i < frames; i++)
            data.AddRange(mip0);
        return [.. data];
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public async Task A_bgra_texture_converts_to_png_pixel_for_pixel(int minor)
    {
        // Red, green / blue, half-transparent white; BGRA in the file.
        byte[] mip0 = [0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 128];
        var path = Path.Combine(fx.NewFolder(), "tiles.vtf");
        File.WriteAllBytes(path, BuildTexture(minor, Vtf.Format.Bgra8888, [9, 9, 9, 255], mip0));

        using var png = new MagickImage(await ConvertAsync(path, "png"));
        Assert.Equal((2u, 2u), (png.Width, png.Height));
        var rgba = png.GetPixels().ToByteArray(PixelMapping.RGBA)!;
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 128 }, rgba);
    }

    [Fact]
    public void The_frames_of_a_texture_are_read_from_its_largest_mipmap()
    {
        byte[] mip0A = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        byte[] mip0B = [.. mip0A.Select(b => (byte)(b + 100))];
        var bytes = BuildTexture(2, Vtf.Format.Rgba8888, [0, 0, 0, 0], mip0A, frames: 2);
        // BuildTexture repeats one mip0; put the second frame's own pixels in place.
        mip0B.CopyTo(bytes, bytes.Length - 16);
        var info = Vtf.ReadInfo(bytes);
        var frames = Vtf.DecodeFrames(bytes, info);
        Assert.Equal(2, frames.Count);
        Assert.Equal(mip0A, frames[0]);
        Assert.Equal(mip0B, frames[1]);
    }

    [Fact]
    public void Dxt_blocks_decode_as_direct3d_does()
    {
        // One DXT1 block: colour 0 pure red (0xF800), colour 1 pure blue (0x001F), c0 > c1 so four colours; the indices
        // pick red, blue, the two-thirds mix and the one-third mix in the first row, red elsewhere.
        byte[] dxt1 = [0x00, 0xF8, 0x1F, 0x00, 0b11_10_01_00, 0, 0, 0];
        var rgba = Vtf.Decode(dxt1, Vtf.Format.Dxt1, 4, 4);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, rgba[..4]);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, rgba[4..8]);
        Assert.Equal(new byte[] { 170, 0, 85, 255 }, rgba[8..12]);
        Assert.Equal(new byte[] { 85, 0, 170, 255 }, rgba[12..16]);

        // Three-colour mode (c0 <= c1): index 3 is transparent black.
        byte[] punch = [0x1F, 0x00, 0x00, 0xF8, 0b11_11_11_11, 0, 0, 0];
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Vtf.Decode(punch, Vtf.Format.Dxt1, 4, 4)[..4]);

        // DXT5: alpha endpoints 255 and 0, the first pixel index 1 (= 0), the rest index 0 (= 255).
        byte[] dxt5 = [255, 0, 0b001, 0, 0, 0, 0, 0, .. dxt1[..4], 0, 0, 0, 0];
        var five = Vtf.Decode(dxt5, Vtf.Format.Dxt5, 4, 4);
        Assert.Equal(new byte[] { 255, 0, 0, 0 }, five[..4]);
        Assert.Equal(255, five[7]);
    }

    [Fact]
    public void Texture_sides_are_rounded_to_the_nearest_power_of_two()
    {
        Assert.Equal((128, 64), VtfWriter.TextureSize(100, 60));
        Assert.Equal((512, 1024), VtfWriter.TextureSize(600, 1000));
        Assert.Equal((1, 4096), VtfWriter.TextureSize(1, 10_000));
        Assert.Equal((16, 8), VtfWriter.LowResSize(128, 64));
        Assert.Equal((4, 1), VtfWriter.LowResSize(4, 1));
        Assert.Equal(13, VtfWriter.MipmapCount(4096, 16));
    }

    /// <summary>A version 7.2 header without a thumbnail, followed by <paramref name="data"/> bytes of image data.</summary>
    private static byte[] Header(int width, int height, Vtf.Format format, int mipmaps = 1, int frames = 1, int data = 0)
    {
        var bytes = new byte[80 + data];
        "VTF\0"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 80);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(24), (ushort)frames);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(52), (uint)format);
        bytes[56] = (byte)mipmaps;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(57), 0xFFFF_FFFF);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(63), 1);
        return bytes;
    }

    [Theory]
    [InlineData(65535, 16384, 1)] // a side over the limit; its RGBA8888 size doesn't fit an int
    [InlineData(16384, 16384, 2)] // each side allowed, the pixels over the budget
    public void A_header_claiming_a_huge_texture_is_refused_before_anything_is_allocated(int width, int height, int frames)
    {
        var bytes = Header(width, height, Vtf.Format.Rgba8888, frames: frames, data: 20);
        Assert.Equal(100, bytes.Length);
        var error = Assert.Throws<InvalidDataException>(() => Vtf.ReadInfo(bytes));
        Assert.Contains("too large", error.Message);
    }

    [Fact]
    public void A_texture_within_the_budget_but_without_its_data_is_cut_off()
    {
        // 4096 × 4096 RGBA8888 claims 64 MB; the file holds 20 bytes.
        var error = Assert.Throws<InvalidDataException>(() => Vtf.ReadInfo(Header(4096, 4096, Vtf.Format.Rgba8888, data: 20)));
        Assert.Contains("cut off", error.Message);
    }

    [Fact]
    public async Task A_truncated_texture_fails_with_a_clear_error()
    {
        // 4 × 4 DXT1, one mipmap: 8 bytes of data, of which the file holds 5.
        var path = Path.Combine(fx.NewFolder(), "broken.vtf");
        File.WriteAllBytes(path, Header(4, 4, Vtf.Format.Dxt1, data: 5));
        Assert.Throws<InvalidDataException>(() => Vtf.ReadInfo(File.ReadAllBytes(path)));
        var job = await fx.ConvertAsync([path], new Preset { TargetFormat = "png" });
        Assert.Equal(JobState.Failed, job.State);
        Assert.Empty(job.Outputs);
    }

    [Theory]
    [InlineData(2, 2, 3)] // 2 × 2 has two mipmaps
    [InlineData(64, 64, 34)] // would shift by 33, which wraps to 1
    public void More_mipmaps_than_the_sides_allow_are_refused(int width, int height, int mipmaps)
    {
        var error = Assert.Throws<InvalidDataException>(() => Vtf.ReadInfo(Header(width, height, Vtf.Format.Rgba8888, mipmaps, data: 100_000)));
        Assert.Contains("mipmaps", error.Message);
    }

    [Fact]
    public void The_writer_keeps_to_the_budget_the_reader_reads()
    {
        Assert.True(Vtf.FitsBudget(VtfWriter.MaxSide, VtfWriter.MaxSide, 16));
        Assert.False(Vtf.FitsBudget(VtfWriter.MaxSide, VtfWriter.MaxSide, 17));
        Assert.True(Vtf.FitsBudget(Vtf.MaxSide, Vtf.MaxSide, 1));
        Assert.Equal(2, Vtf.MaxMipmaps(2, 2));
        Assert.Equal(13, Vtf.MaxMipmaps(4096, 1));
        Assert.Throws<InvalidDataException>(() => Vtf.Decode(new byte[7], Vtf.Format.Dxt1, 4, 4));
    }

    [Fact]
    public void A_file_that_is_no_texture_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => Vtf.ReadInfo(new byte[100]));
        var p8 = BuildTexture(2, Vtf.Format.P8, [0], [0, 0, 0, 0]);
        Assert.Throws<InvalidDataException>(() => Vtf.ReadInfo(p8));
    }
}
