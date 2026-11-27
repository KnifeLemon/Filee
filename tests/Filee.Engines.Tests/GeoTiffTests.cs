// High-depth GeoTIFF rasters (#155): reading 16/32-bit integer and 32/64-bit float GeoTIFF bands into linearly
// stretched grayscale or RGB pictures, excluding GDAL_NODATA and NaN cells, supporting LZW/Deflate/PackBits,
// predictors 2 and 3, big-endian tiles with padded edges, planar RGB, while leaving ordinary TIFFs on ImageMagick
// and refusing corrupt high-depth GeoTIFFs without large allocations.

using System.Buffers.Binary;
using System.IO.Compression;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Magick;
using ImageMagick;

namespace Filee.Engines.Tests;

public class GeoTiffTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly TiffTag[] BasicGeoTags =
    [
        TiffTag.Doubles(Hgt.ModelPixelScaleTag, 0.001, 0.001, 0.0),
        TiffTag.Doubles(Hgt.ModelTiepointTag, 0.0, 0.0, 0.0, -122.0, 38.0, 0.0),
    ];

    private string NewTiffPath(string name) => Path.Combine(fx.NewFolder(), name);

    private static void AssertDone(ConversionJob job) =>
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));

    [Fact]
    public void Float32_single_band_geotiff_stretches_linearly_to_grayscale()
    {
        // Values in 100..3000 with midpoint 1550 -> 0, ~128, 255.
        float[] plane =
        [
            100f, 825f, 1550f,
            2275f, 3000f, 1550f,
            100f, 3000f, 1550f,
        ];
        var path = NewTiffPath("elevation_f32.tif");
        TiffWriter.Write(path, [plane], 3, 3, TiffSampleKind.Float32, TiffCompression.None, 72,
            TestContext.Current.CancellationToken, BasicGeoTags);

        using var image = GeoTiff.TryRead(path, TestContext.Current.CancellationToken);
        Assert.NotNull(image);
        Assert.Equal((3u, 3u), (image!.Width, image.Height));
        Assert.Equal(ColorType.Grayscale, image.ColorType);

        var pixels = image.GetPixels();
        Assert.Equal(0, pixels.GetPixel(0, 0).GetChannel(0));
        Assert.InRange(pixels.GetPixel(1, 0).GetChannel(0), 63, 65);
        Assert.InRange(pixels.GetPixel(2, 0).GetChannel(0), 127, 128);
        Assert.InRange(pixels.GetPixel(0, 1).GetChannel(0), 190, 192);
        Assert.Equal(255, pixels.GetPixel(1, 1).GetChannel(0));
    }

    [Fact]
    public void Gdal_nodata_and_nan_cells_are_black_and_excluded_from_stretch()
    {
        float[] plane =
        [
            -9999f, float.NaN, float.PositiveInfinity,
            100f, 200f, 300f,
            float.NegativeInfinity, -9999f, 200f,
        ];
        TiffTag[] tags =
        [
            .. BasicGeoTags,
            TiffTag.Ascii(Hgt.GdalNoDataTag, "-9999"),
        ];
        var path = NewTiffPath("nodata_f32.tif");
        TiffWriter.Write(path, [plane], 3, 3, TiffSampleKind.Float32, TiffCompression.None, 72,
            TestContext.Current.CancellationToken, tags);

        using var image = GeoTiff.TryRead(path, TestContext.Current.CancellationToken);
        Assert.NotNull(image);
        var pixels = image!.GetPixels();
        Assert.Equal(0, pixels.GetPixel(0, 0).GetChannel(0));   // GDAL_NODATA -> black
        Assert.Equal(0, pixels.GetPixel(1, 0).GetChannel(0));   // NaN -> black
        Assert.Equal(0, pixels.GetPixel(2, 0).GetChannel(0));   // +Inf -> black
        Assert.Equal(0, pixels.GetPixel(0, 2).GetChannel(0));   // -Inf -> black
        Assert.Equal(0, pixels.GetPixel(0, 1).GetChannel(0));   // min valid (100) -> 0
        Assert.InRange(pixels.GetPixel(1, 1).GetChannel(0), 127, 128); // mid valid (200) -> ~128
        Assert.Equal(255, pixels.GetPixel(2, 1).GetChannel(0)); // max valid (300) -> 255
    }

    [Fact]
    public void Int16_geotiff_with_lzw_and_predictor_2_decodes_to_expected_greys()
    {
        // 64×64 signed 16-bit grid exercises LZW code width transitions past 9/10/11/12 bits and predictor 2.
        const int side = 64;
        var samples = new short[side * side];
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
                samples[y * side + x] = (short)(-1000 + (y * side + x) * 2);
        }
        samples[0] = -32768; // nodata
        samples[1] = -1000;  // min valid
        samples[2] = 3095;   // ~midpoint of -1000..7190
        samples[^1] = 7190;  // max valid

        TiffTag[] tags =
        [
            .. BasicGeoTags,
            TiffTag.Ascii(Hgt.GdalNoDataTag, "-32768"),
        ];
        var path = NewTiffPath("int16_lzw.tif");
        TiffWriter.Write(path, samples, side, side, TiffCompression.Lzw, 72, tags, TestContext.Current.CancellationToken);

        using var image = GeoTiff.TryRead(path, TestContext.Current.CancellationToken);
        Assert.NotNull(image);
        Assert.Equal((64u, 64u), (image!.Width, image.Height));
        Assert.Equal(ColorType.Grayscale, image.ColorType);

        var pixels = image.GetPixels();
        Assert.Equal(0, pixels.GetPixel(0, 0).GetChannel(0)); // nodata -> black
        Assert.Equal(0, pixels.GetPixel(1, 0).GetChannel(0)); // min -> 0
        Assert.InRange(pixels.GetPixel(2, 0).GetChannel(0), 127, 128); // midpoint -> ~128
        Assert.Equal(255, pixels.GetPixel(side - 1, side - 1).GetChannel(0)); // max -> 255
    }

    [Fact]
    public void Uint16_geotiff_with_lzw_and_predictor_2_decodes_to_expected_greys()
    {
        const int width = 32, height = 32;
        var plane = new float[width * height];
        for (var i = 0; i < plane.Length; i++)
            plane[i] = 500 + i * 10; // 500..10730
        var path = NewTiffPath("uint16_lzw.tif");
        TiffWriter.Write(path, [plane], width, height, TiffSampleKind.UInt16, TiffCompression.Lzw, 72,
            TestContext.Current.CancellationToken, BasicGeoTags);

        using var image = GeoTiff.TryRead(path, TestContext.Current.CancellationToken);
        Assert.NotNull(image);
        var pixels = image!.GetPixels();
        Assert.Equal(0, pixels.GetPixel(0, 0).GetChannel(0));
        Assert.InRange(pixels.GetPixel(0, height / 2).GetChannel(0), 127, 128);
        Assert.Equal(255, pixels.GetPixel(width - 1, height - 1).GetChannel(0));
    }

    [Theory]
    [InlineData((ushort)1)] // uncompressed
    [InlineData((ushort)8)] // Deflate
    public void Big_endian_tiled_uint16_geotiff_with_padded_edge_tiles_decodes_correctly(ushort compression)
    {
        // 20×20 image with 16×16 tiles -> 2×2 tiles; right and bottom edges are padded with extreme sentinel 65535
        // which must be ignored so the valid 1000..2000 range stretches cleanly to 0..255.
        const int width = 20, height = 20, tileW = 16, tileH = 16;
        var tiles = new List<byte[]>();
        for (var ty = 0; ty < 2; ty++)
        {
            for (var tx = 0; tx < 2; tx++)
            {
                var raw = new byte[tileW * tileH * 2];
                for (var r = 0; r < tileH; r++)
                {
                    var y = ty * tileH + r;
                    for (var c = 0; c < tileW; c++)
                    {
                        var x = tx * tileW + c;
                        ushort val;
                        if (x >= width || y >= height)
                            val = 65535; // edge tile padding; must not affect min/max
                        else if (x == 0 && y == 0)
                            val = 1000;
                        else if (x == 19 && y == 19)
                            val = 2000;
                        else
                            val = 1500;
                        BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan((r * tileW + c) * 2, 2), val);
                    }
                }
                tiles.Add(compression == 8 ? CompressDeflate(raw) : raw);
            }
        }

        var path = NewTiffPath($"tiled_be_{compression}.tif");
        File.WriteAllBytes(path, BuildRawTiff(
            bigEndian: true,
            width: width,
            height: height,
            samplesPerPixel: 1,
            bitsPerSample: 16,
            sampleFormat: 1,
            compression: compression,
            photometric: 1,
            planarConfig: 1,
            predictor: 1,
            chunks: tiles,
            tiled: true,
            tileWidth: tileW,
            tileLength: tileH,
            includeGeoTag: true));

        using var image = GeoTiff.TryRead(path, TestContext.Current.CancellationToken);
        Assert.NotNull(image);
        Assert.Equal((20u, 20u), (image!.Width, image.Height));
        Assert.Equal(ColorType.Grayscale, image.ColorType);

        var pixels = image.GetPixels();
        Assert.Equal(0, pixels.GetPixel(0, 0).GetChannel(0));
        Assert.InRange(pixels.GetPixel(10, 10).GetChannel(0), 127, 128);
        Assert.InRange(pixels.GetPixel(19, 5).GetChannel(0), 127, 128);  // inside right edge tile
        Assert.InRange(pixels.GetPixel(5, 19).GetChannel(0), 127, 128);  // inside bottom edge tile
        Assert.Equal(255, pixels.GetPixel(19, 19).GetChannel(0));        // bottom-right corner tile
    }

    [Fact]
    public void Planar_configuration_2_three_band_geotiff_renders_rgb_with_per_band_stretch()
    {
        // 4×2 3-band UInt16 GeoTIFF stored as 3 separate planes, each with its own value range.
        const int width = 4, height = 2;
        ushort[] rVals = [1000, 1500, 2000, 1000, 2000, 1500, 1000, 2000]; // 1000..2000
        ushort[] gVals = [9000, 7000, 5000, 9000, 5000, 7000, 9000, 5000]; // 5000..9000
        ushort[] bVals = [200, 200, 400, 0, 0, 400, 200, 200];             // 0..400

        static byte[] ToLittleEndianBytes(ushort[] values)
        {
            var bytes = new byte[values.Length * 2];
            for (var i = 0; i < values.Length; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), values[i]);
            return bytes;
        }

        var path = NewTiffPath("planar_rgb.tif");
        File.WriteAllBytes(path, BuildRawTiff(
            bigEndian: false,
            width: width,
            height: height,
            samplesPerPixel: 3,
            bitsPerSample: 16,
            sampleFormat: 1,
            compression: 1,
            photometric: 2,
            planarConfig: 2,
            predictor: 1,
            chunks: [ToLittleEndianBytes(rVals), ToLittleEndianBytes(gVals), ToLittleEndianBytes(bVals)],
            tiled: false,
            rowsPerStrip: height,
            includeGeoTag: true));

        using var image = GeoTiff.TryRead(path, TestContext.Current.CancellationToken);
        Assert.NotNull(image);
        Assert.Equal((4u, 2u), (image!.Width, image.Height));

        var pixels = image.GetPixels();
        // Pixel (0, 0): R=1000 (0), G=9000 (255), B=200 (~128)
        var p00 = pixels.GetPixel(0, 0);
        Assert.Equal(0, p00.GetChannel(0));
        Assert.Equal(255, p00.GetChannel(1));
        Assert.InRange(p00.GetChannel(2), 127, 128);

        // Pixel (2, 0): R=2000 (255), G=5000 (0), B=400 (255)
        var p20 = pixels.GetPixel(2, 0);
        Assert.Equal(255, p20.GetChannel(0));
        Assert.Equal(0, p20.GetChannel(1));
        Assert.Equal(255, p20.GetChannel(2));

        // Pixel (3, 0): R=1000 (0), G=9000 (255), B=0 (0)
        var p30 = pixels.GetPixel(3, 0);
        Assert.Equal(0, p30.GetChannel(0));
        Assert.Equal(255, p30.GetChannel(1));
        Assert.Equal(0, p30.GetChannel(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Float32_geotiff_with_deflate_and_predictor_3_decodes_correctly(bool bigEndian)
    {
        const int width = 4, height = 2;
        float[] values = [10f, 30f, 50f, 90f, 10f, 50f, 90f, 50f];
        var encoded = new byte[values.Length * 4];
        Span<byte> sample = stackalloc byte[4];
        for (var r = 0; r < height; r++)
        {
            var row = encoded.AsSpan(r * width * 4, width * 4);
            // Shuffle into MSB-first byte planes (Adobe TIFF Tech Note 3).
            for (var x = 0; x < width; x++)
            {
                BinaryPrimitives.WriteSingleBigEndian(sample, values[r * width + x]);
                for (var b = 0; b < 4; b++)
                    row[b * width + x] = sample[b];
            }
            // Horizontal byte differencing with stride = 1.
            for (var i = row.Length - 1; i >= 1; i--)
                row[i] = unchecked((byte)(row[i] - row[i - 1]));
        }

        var path = NewTiffPath($"f32_pred3_{(bigEndian ? "be" : "le")}.tif");
        File.WriteAllBytes(path, BuildRawTiff(
            bigEndian: bigEndian,
            width: width,
            height: height,
            samplesPerPixel: 1,
            bitsPerSample: 32,
            sampleFormat: 3,
            compression: 8,
            photometric: 1,
            planarConfig: 1,
            predictor: 3,
            chunks: [CompressDeflate(encoded)],
            tiled: false,
            rowsPerStrip: height,
            includeGeoTag: true));

        using var image = GeoTiff.TryRead(path, TestContext.Current.CancellationToken);
        Assert.NotNull(image);
        var pixels = image!.GetPixels();
        Assert.Equal(0, pixels.GetPixel(0, 0).GetChannel(0));         // 10 -> 0
        Assert.InRange(pixels.GetPixel(2, 0).GetChannel(0), 127, 128); // 50 -> 128
        Assert.Equal(255, pixels.GetPixel(3, 0).GetChannel(0));       // 90 -> 255
    }

    [Fact]
    public void Float32_chunky_geotiff_with_predictor_2_decodes_correctly()
    {
        // libtiff / GDAL can write Predictor 2 (horizontal differencing) on floating-point samples by differencing
        // raw UInt32 bit patterns per channel along each row.
        const int width = 3, height = 2, samplesPerPixel = 3;
        float[,] r = { { 10f, 30f, 50f }, { 50f, 30f, 10f } };
        float[,] g = { { 200f, 150f, 100f }, { 100f, 150f, 200f } };
        float[,] b = { { 0f, 500f, 1000f }, { 1000f, 500f, 0f } };

        var raw = new byte[width * height * samplesPerPixel * 4];
        Span<uint> prev = stackalloc uint[samplesPerPixel];
        Span<float> pixel = stackalloc float[samplesPerPixel];
        for (var y = 0; y < height; y++)
        {
            prev.Clear();
            for (var x = 0; x < width; x++)
            {
                pixel[0] = r[y, x];
                pixel[1] = g[y, x];
                pixel[2] = b[y, x];
                for (var c = 0; c < samplesPerPixel; c++)
                {
                    var bits = BitConverter.SingleToUInt32Bits(pixel[c]);
                    var diff = unchecked(bits - prev[c]);
                    prev[c] = bits;
                    var offset = ((y * width + x) * samplesPerPixel + c) * 4;
                    BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(offset, 4), diff);
                }
            }
        }

        var path = NewTiffPath("f32_chunky_pred2.tif");
        File.WriteAllBytes(path, BuildRawTiff(
            bigEndian: false,
            width: width,
            height: height,
            samplesPerPixel: samplesPerPixel,
            bitsPerSample: 32,
            sampleFormat: 3,
            compression: 8,
            photometric: 2,
            planarConfig: 1,
            predictor: 2,
            chunks: [CompressDeflate(raw)],
            tiled: false,
            rowsPerStrip: height,
            includeGeoTag: true));

        using var image = GeoTiff.TryRead(path, TestContext.Current.CancellationToken);
        Assert.NotNull(image);
        Assert.Equal((3u, 2u), (image!.Width, image.Height));

        var pixels = image.GetPixels();
        var p00 = pixels.GetPixel(0, 0);
        Assert.Equal(0, p00.GetChannel(0));   // R=10 -> 0
        Assert.Equal(255, p00.GetChannel(1)); // G=200 -> 255
        Assert.Equal(0, p00.GetChannel(2));   // B=0 -> 0

        var p10 = pixels.GetPixel(1, 0);
        Assert.InRange(p10.GetChannel(0), 127, 128); // R=30 -> ~128
        Assert.InRange(p10.GetChannel(1), 127, 128); // G=150 -> ~128
        Assert.InRange(p10.GetChannel(2), 127, 128); // B=500 -> ~128

        var p20 = pixels.GetPixel(2, 0);
        Assert.Equal(255, p20.GetChannel(0)); // R=50 -> 255
        Assert.Equal(0, p20.GetChannel(1));   // G=100 -> 0
        Assert.Equal(255, p20.GetChannel(2)); // B=1000 -> 255
    }

    [Fact]
    public void Packbits_compressed_uint16_geotiff_decodes_correctly()
    {
        const int width = 4, height = 2;
        ushort[] values = [100, 100, 100, 300, 200, 200, 300, 100];
        var raw = new byte[values.Length * 2];
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(i * 2, 2), values[i]);

        // PackBits: literal run of all 16 bytes (header = 15, followed by 16 literal bytes).
        var packed = new byte[1 + raw.Length];
        packed[0] = (byte)(raw.Length - 1);
        raw.CopyTo(packed, 1);

        var path = NewTiffPath("packbits_u16.tif");
        File.WriteAllBytes(path, BuildRawTiff(
            bigEndian: false,
            width: width,
            height: height,
            samplesPerPixel: 1,
            bitsPerSample: 16,
            sampleFormat: 1,
            compression: 32773,
            photometric: 1,
            planarConfig: 1,
            predictor: 1,
            chunks: [packed],
            tiled: false,
            rowsPerStrip: height,
            includeGeoTag: true));

        using var image = GeoTiff.TryRead(path, TestContext.Current.CancellationToken);
        Assert.NotNull(image);
        var pixels = image!.GetPixels();
        Assert.Equal(0, pixels.GetPixel(0, 0).GetChannel(0));         // 100 -> 0
        Assert.InRange(pixels.GetPixel(0, 1).GetChannel(0), 127, 128); // 200 -> 128
        Assert.Equal(255, pixels.GetPixel(3, 0).GetChannel(0));       // 300 -> 255
    }

    [Fact]
    public void Ordinary_8_bit_tiff_and_16_bit_tiff_without_geo_tags_return_null()
    {
        var ct = TestContext.Current.CancellationToken;

        // 1. Ordinary 8-bit unsigned TIFF (no geo tags).
        var plain8 = NewTiffPath("plain_8bit.tif");
        TiffWriter.Write(plain8, [[10f, 20f, 30f, 40f]], 2, 2, TiffSampleKind.UInt8, TiffCompression.None, 72, ct);
        Assert.Null(GeoTiff.TryRead(plain8, ct));

        // 2. 8-bit unsigned GeoTIFF (orthophoto — must stay on ImageMagick).
        var geo8 = NewTiffPath("geo_8bit.tif");
        TiffWriter.Write(geo8, [[10f, 20f, 30f, 40f]], 2, 2, TiffSampleKind.UInt8, TiffCompression.None, 72, ct, BasicGeoTags);
        Assert.Null(GeoTiff.TryRead(geo8, ct));

        // 3. 16-bit TIFF with NO geo tags.
        var plain16 = NewTiffPath("plain_16bit.tif");
        TiffWriter.Write(plain16, [[1000f, 2000f, 3000f, 4000f]], 2, 2, TiffSampleKind.UInt16, TiffCompression.None, 72, ct);
        Assert.Null(GeoTiff.TryRead(plain16, ct));

        // 4. BigTIFF header (version 0x2B / 43).
        var bigTiff = NewTiffPath("big.tif");
        File.WriteAllBytes(bigTiff, [(byte)'I', (byte)'I', 0x2B, 0x00, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        Assert.Null(GeoTiff.TryRead(bigTiff, ct));

        // 5. Palette photometric (3) 16-bit GeoTIFF.
        var paletteGeo = NewTiffPath("palette_geo.tif");
        File.WriteAllBytes(paletteGeo, BuildRawTiff(
            bigEndian: false, width: 2, height: 2, samplesPerPixel: 1, bitsPerSample: 16, sampleFormat: 1,
            compression: 1, photometric: 3, planarConfig: 1, predictor: 1,
            chunks: [new byte[8]], tiled: false, rowsPerStrip: 2, includeGeoTag: true));
        Assert.Null(GeoTiff.TryRead(paletteGeo, ct));
    }

    [Fact]
    public void Corrupt_geotiff_with_strip_offset_past_eof_is_refused()
    {
        var path = NewTiffPath("bad_offset.tif");
        File.WriteAllBytes(path, BuildRawTiff(
            bigEndian: false,
            width: 4,
            height: 4,
            samplesPerPixel: 1,
            bitsPerSample: 16,
            sampleFormat: 1,
            compression: 1,
            photometric: 1,
            planarConfig: 1,
            predictor: 1,
            chunks: [new byte[32]],
            tiled: false,
            rowsPerStrip: 4,
            includeGeoTag: true,
            overrideChunkOffset: 999_999));

        var ex = Assert.Throws<ConversionRefusedException>(() => GeoTiff.TryRead(path, TestContext.Current.CancellationToken));
        Assert.Equal("error.unsupported_source", ex.ErrorKey);
    }

    [Theory]
    [InlineData(9_000, 9_000)]     // 81M pixels: exceeds MaxPixels (64M)
    [InlineData(100_000, 100_000)] // 10B pixels: exceeds MaxPixels and MaxSamples
    public void Corrupt_geotiff_with_huge_dimensions_is_refused_without_large_allocation(int width, int height)
    {
        var path = NewTiffPath($"huge_dims_{width}x{height}.tif");
        File.WriteAllBytes(path, BuildRawTiff(
            bigEndian: false,
            width: width,
            height: height,
            samplesPerPixel: 1,
            bitsPerSample: 32,
            sampleFormat: 3,
            compression: 1,
            photometric: 1,
            planarConfig: 1,
            predictor: 1,
            chunks: [new byte[16]],
            tiled: false,
            rowsPerStrip: height,
            includeGeoTag: true));

        var ex = Assert.Throws<ConversionRefusedException>(() => GeoTiff.TryRead(path, TestContext.Current.CancellationToken));
        Assert.Equal("error.unsupported_source", ex.ErrorKey);
    }

    [Fact]
    public void Flat_and_all_nodata_geotiffs_render_mid_grey_and_black()
    {
        var ct = TestContext.Current.CancellationToken;
        var flatPath = NewTiffPath("flat.tif");
        TiffWriter.Write(flatPath, [[500f, 500f, 500f, 500f]], 2, 2, TiffSampleKind.Float32, TiffCompression.None, 72, ct, BasicGeoTags);
        using (var flat = GeoTiff.TryRead(flatPath, ct))
        {
            Assert.NotNull(flat);
            Assert.Equal(128, flat!.GetPixels().GetPixel(0, 0).GetChannel(0));
        }

        TiffTag[] noDataTags = [.. BasicGeoTags, TiffTag.Ascii(Hgt.GdalNoDataTag, "-9999")];
        var emptyPath = NewTiffPath("all_nodata.tif");
        TiffWriter.Write(emptyPath, [[-9999f, float.NaN, -9999f, float.NaN]], 2, 2, TiffSampleKind.Float32, TiffCompression.None, 72, ct, noDataTags);
        using (var empty = GeoTiff.TryRead(emptyPath, ct))
        {
            Assert.NotNull(empty);
            Assert.Equal(0, empty!.GetPixels().GetPixel(0, 0).GetChannel(0));
        }
    }

    [Fact]
    public async Task Float_geotiff_converts_to_stretched_png_through_converter()
    {
        float[] plane =
        [
            -9999f, 100f, 1550f,
            3000f, 1550f, 100f,
            3000f, float.NaN, 1550f,
        ];
        TiffTag[] tags =
        [
            .. BasicGeoTags,
            TiffTag.Ascii(Hgt.GdalNoDataTag, "-9999"),
        ];
        var source = NewTiffPath("dem_float.tif");
        TiffWriter.Write(source, [plane], 3, 3, TiffSampleKind.Float32, TiffCompression.None, 72,
            TestContext.Current.CancellationToken, tags);

        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "png" });
        AssertDone(job);
        var png = job.Outputs.Single();

        using var image = new MagickImage(png);
        Assert.Equal((3u, 3u), (image.Width, image.Height));
        Assert.Equal(ColorType.Grayscale, image.ColorType);
        var pixels = image.GetPixels();
        Assert.Equal(0, pixels.GetPixel(0, 0).GetChannel(0));         // nodata -> black
        Assert.Equal(0, pixels.GetPixel(1, 0).GetChannel(0));         // min (100) -> 0
        Assert.InRange(pixels.GetPixel(2, 0).GetChannel(0), 127, 128); // mid (1550) -> ~128
        Assert.Equal(255, pixels.GetPixel(0, 1).GetChannel(0));       // max (3000) -> 255
        Assert.Equal(0, pixels.GetPixel(1, 2).GetChannel(0));         // NaN -> black
    }

    private static byte[] CompressDeflate(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data);
        return ms.ToArray();
    }

    /// <summary>Builds a minimal classic little- or big-endian TIFF/GeoTIFF for testing edge layouts and corrupt headers.</summary>
    private static byte[] BuildRawTiff(
        bool bigEndian,
        int width,
        int height,
        ushort samplesPerPixel,
        ushort bitsPerSample,
        ushort sampleFormat,
        ushort compression,
        ushort photometric,
        ushort planarConfig,
        ushort predictor,
        IReadOnlyList<byte[]> chunks,
        bool tiled,
        int rowsPerStrip = 0,
        int tileWidth = 0,
        int tileLength = 0,
        bool includeGeoTag = true,
        uint? overrideChunkOffset = null)
    {
        byte[] U16(params ushort[] values)
        {
            var bytes = new byte[values.Length * 2];
            for (var i = 0; i < values.Length; i++)
            {
                if (bigEndian)
                    BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2, 2), values[i]);
                else
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), values[i]);
            }
            return bytes;
        }

        byte[] U32(params uint[] values)
        {
            var bytes = new byte[values.Length * 4];
            for (var i = 0; i < values.Length; i++)
            {
                if (bigEndian)
                    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4, 4), values[i]);
                else
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4, 4), values[i]);
            }
            return bytes;
        }

        byte[] F64(params double[] values)
        {
            var bytes = new byte[values.Length * 8];
            for (var i = 0; i < values.Length; i++)
            {
                if (bigEndian)
                    BinaryPrimitives.WriteDoubleBigEndian(bytes.AsSpan(i * 8, 8), values[i]);
                else
                    BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(i * 8, 8), values[i]);
            }
            return bytes;
        }

        using var ms = new MemoryStream();
        ms.Write(bigEndian ? "MM"u8 : "II"u8);
        ms.Write(U16(42));
        ms.Write(new byte[4]); // IFD offset placeholder

        var offsets = new uint[chunks.Count];
        var counts = new uint[chunks.Count];
        for (var i = 0; i < chunks.Count; i++)
        {
            offsets[i] = overrideChunkOffset ?? (uint)ms.Position;
            counts[i] = (uint)chunks[i].Length;
            ms.Write(chunks[i]);
            if ((ms.Position & 1) != 0)
                ms.WriteByte(0);
        }

        var tags = new List<(ushort Tag, ushort Type, uint Count, byte[] Data)>
        {
            (256, 4, 1, U32((uint)width)),
            (257, 4, 1, U32((uint)height)),
            (258, 3, samplesPerPixel, U16([.. Enumerable.Repeat(bitsPerSample, samplesPerPixel)])),
            (259, 3, 1, U16(compression)),
            (262, 3, 1, U16(photometric)),
            (277, 3, 1, U16(samplesPerPixel)),
            (284, 3, 1, U16(planarConfig)),
            (317, 3, 1, U16(predictor)),
            (339, 3, samplesPerPixel, U16([.. Enumerable.Repeat(sampleFormat, samplesPerPixel)])),
        };
        if (tiled)
        {
            tags.Add((322, 4, 1, U32((uint)tileWidth)));
            tags.Add((323, 4, 1, U32((uint)tileLength)));
            tags.Add((324, 4, (uint)offsets.Length, U32(offsets)));
            tags.Add((325, 4, (uint)counts.Length, U32(counts)));
        }
        else
        {
            tags.Add((273, 4, (uint)offsets.Length, U32(offsets)));
            tags.Add((278, 4, 1, U32((uint)rowsPerStrip)));
            tags.Add((279, 4, (uint)counts.Length, U32(counts)));
        }
        if (includeGeoTag)
            tags.Add((33550, 12, 3, F64(0.001, 0.001, 0.0)));

        tags.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        var ifdPos = (uint)ms.Position;
        var extraBase = ifdPos + 2u + (uint)tags.Count * 12u + 4u;
        using var dir = new MemoryStream();
        using var extra = new MemoryStream();
        dir.Write(U16((ushort)tags.Count));
        foreach (var (tag, type, count, data) in tags)
        {
            dir.Write(U16(tag));
            dir.Write(U16(type));
            dir.Write(U32(count));
            if (data.Length <= 4)
            {
                dir.Write(data);
                if (data.Length < 4)
                    dir.Write(new byte[4 - data.Length]);
            }
            else
            {
                dir.Write(U32(extraBase + (uint)extra.Length));
                extra.Write(data);
                if ((extra.Length & 1) != 0)
                    extra.WriteByte(0);
            }
        }
        dir.Write(new byte[4]); // next IFD = 0
        dir.WriteTo(ms);
        extra.WriteTo(ms);

        var result = ms.ToArray();
        U32(ifdPos).CopyTo(result, 4);
        return result;
    }
}
