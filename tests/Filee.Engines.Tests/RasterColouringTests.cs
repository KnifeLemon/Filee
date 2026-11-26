// Colour-mapped PNG previews of single-band GeoTIFF and SRTM .hgt rasters (#155): Terrain and Viridis ramps,
// transparent no-data/NaN/void pixels, 8-bit unsigned single-band GeoTIFFs, flat and all-nodata grids, refusals on
// multi-band/unsupported sources without harming existing outputs, and regression check for plain grey PNG output.

using System.Buffers.Binary;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Magick;
using ImageMagick;

namespace Filee.Engines.Tests;

public class RasterColouringTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly TiffTag[] BasicGeoTags =
    [
        TiffTag.Doubles(Hgt.ModelPixelScaleTag, 0.001, 0.001, 0.0),
        TiffTag.Doubles(Hgt.ModelTiepointTag, 0.0, 0.0, 0.0, -122.0, 38.0, 0.0),
    ];

    private string NewPath(string name) => Path.Combine(fx.NewFolder(), name);

    private static void AssertDone(ConversionJob job) =>
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));

    private static Preset ColourPreset(RasterColours ramp, ConflictPolicy conflict = ConflictPolicy.Rename) => new()
    {
        TargetFormat = "png",
        Image = { RasterColours = ramp },
        Output = { FileNamePattern = "{name}_colours", Conflict = conflict },
    };

    private static (byte R, byte G, byte B, byte A) PixelAt(MagickImage image, int x, int y)
    {
        var rgba = image.GetPixels().ToByteArray(PixelMapping.RGBA)!;
        var offset = (y * (int)image.Width + x) * 4;
        return (rgba[offset], rgba[offset + 1], rgba[offset + 2], rgba[offset + 3]);
    }

    private static void AssertColourNear((byte R, byte G, byte B, byte A) actual, byte r, byte g, byte b, byte a, int tolerance = 2)
    {
        Assert.InRange(actual.R, Math.Max(0, r - tolerance), Math.Min(255, r + tolerance));
        Assert.InRange(actual.G, Math.Max(0, g - tolerance), Math.Min(255, g + tolerance));
        Assert.InRange(actual.B, Math.Max(0, b - tolerance), Math.Min(255, b + tolerance));
        Assert.Equal(a, actual.A);
    }

    [Fact]
    public async Task Int16_geotiff_with_nodata_renders_terrain_ramp_and_transparent_nodata()
    {
        short[] samples =
        [
            -32768, 100, 550,
            1000, 100, 1000,
        ];
        TiffTag[] tags = [.. BasicGeoTags, TiffTag.Ascii(Hgt.GdalNoDataTag, "-32768")];
        var source = NewPath("dem_i16.tif");
        TiffWriter.Write(source, samples, 3, 2, TiffCompression.None, 72, tags, TestContext.Current.CancellationToken);

        var job = await fx.ConvertAsync([source], ColourPreset(RasterColours.Terrain));
        AssertDone(job);

        using var image = new MagickImage(job.Outputs.Single());
        Assert.Equal((3u, 2u), (image.Width, image.Height));
        Assert.Equal(0, PixelAt(image, 0, 0).A);                        // nodata -> transparent
        AssertColourNear(PixelAt(image, 1, 0), 0x00, 0x61, 0x30, 255);  // min -> Terrain stop 0 (#006130)
        AssertColourNear(PixelAt(image, 2, 0), 0xE8, 0xD5, 0x7A, 255);  // mid -> Terrain stop 2 (#E8D57A)
        AssertColourNear(PixelAt(image, 0, 1), 0xFF, 0xFF, 0xFF, 255);  // max -> Terrain stop 4 (#FFFFFF)
        Assert.Equal(255, PixelAt(image, 1, 1).A);
        Assert.Equal(255, PixelAt(image, 2, 1).A);
    }

    [Fact]
    public async Task Float32_geotiff_with_nan_renders_viridis_endpoints_and_midpoint()
    {
        float[] plane =
        [
            float.NaN, -1.0f, 0.0f,
            1.0f, float.PositiveInfinity, 0.0f,
        ];
        var source = NewPath("ndvi_f32.tif");
        TiffWriter.Write(source, [plane], 3, 2, TiffSampleKind.Float32, TiffCompression.None, 72,
            TestContext.Current.CancellationToken, BasicGeoTags);

        var job = await fx.ConvertAsync([source], ColourPreset(RasterColours.Viridis));
        AssertDone(job);

        using var image = new MagickImage(job.Outputs.Single());
        Assert.Equal(0, PixelAt(image, 0, 0).A);                        // NaN -> transparent
        Assert.Equal(0, PixelAt(image, 1, 1).A);                        // +Inf -> transparent
        AssertColourNear(PixelAt(image, 1, 0), 0x44, 0x01, 0x54, 255);  // min (-1) -> Viridis stop 0 (#440154)
        AssertColourNear(PixelAt(image, 2, 0), 0x21, 0x91, 0x8C, 255);  // mid (0) -> Viridis stop 2 (#21918C)
        AssertColourNear(PixelAt(image, 0, 1), 0xFD, 0xE7, 0x25, 255);  // max (1) -> Viridis stop 4 (#FDE725)
    }

    [Fact]
    public async Task Eight_bit_unsigned_single_band_geotiff_gets_colours_while_plain_png_stays_on_magick()
    {
        float[] plane = [10f, 30f, 50f, 50f];
        var source = NewPath("classes_u8.tif");
        TiffWriter.Write(source, [plane], 2, 2, TiffSampleKind.UInt8, TiffCompression.None, 72,
            TestContext.Current.CancellationToken, BasicGeoTags);

        // Plain conversion still skips GeoTiff.TryRead and keeps the unstretched 8-bit grey values.
        Assert.Null(GeoTiff.TryRead(source, TestContext.Current.CancellationToken));
        var plainJob = await fx.ConvertAsync([source], new Preset { TargetFormat = "png" });
        AssertDone(plainJob);
        using (var plain = new MagickImage(plainJob.Outputs.Single()))
        {
            Assert.Equal(10, PixelAt(plain, 0, 0).R);
            Assert.Equal(50, PixelAt(plain, 0, 1).R);
        }

        // Colour-mapped preview accepts 8-bit unsigned single-band GeoTIFFs.
        var colouredJob = await fx.ConvertAsync([source], ColourPreset(RasterColours.Viridis));
        AssertDone(colouredJob);
        using var coloured = new MagickImage(colouredJob.Outputs.Single());
        AssertColourNear(PixelAt(coloured, 0, 0), 0x44, 0x01, 0x54, 255); // min (10)
        AssertColourNear(PixelAt(coloured, 1, 0), 0x21, 0x91, 0x8C, 255); // mid (30)
        AssertColourNear(PixelAt(coloured, 0, 1), 0xFD, 0xE7, 0x25, 255); // max (50)
    }

    [Fact]
    public async Task Flat_raster_uses_mid_colour_and_all_nodata_is_all_transparent()
    {
        var ct = TestContext.Current.CancellationToken;
        var flatSource = NewPath("flat.tif");
        TiffWriter.Write(flatSource, [[420f, 420f, 420f, 420f]], 2, 2, TiffSampleKind.Float32, TiffCompression.None, 72, ct, BasicGeoTags);

        var flatJob = await fx.ConvertAsync([flatSource], ColourPreset(RasterColours.Terrain));
        AssertDone(flatJob);
        using (var flat = new MagickImage(flatJob.Outputs.Single()))
        {
            for (var y = 0; y < 2; y++)
                for (var x = 0; x < 2; x++)
                    AssertColourNear(PixelAt(flat, x, y), 0xE8, 0xD5, 0x7A, 255); // t = 0.5
        }

        TiffTag[] noDataTags = [.. BasicGeoTags, TiffTag.Ascii(Hgt.GdalNoDataTag, "-9999")];
        var emptySource = NewPath("all_nodata.tif");
        TiffWriter.Write(emptySource, [[-9999f, float.NaN, -9999f, float.NaN]], 2, 2, TiffSampleKind.Float32, TiffCompression.None, 72, ct, noDataTags);

        var emptyJob = await fx.ConvertAsync([emptySource], ColourPreset(RasterColours.Viridis));
        AssertDone(emptyJob);
        using var empty = new MagickImage(emptyJob.Outputs.Single());
        for (var y = 0; y < 2; y++)
            for (var x = 0; x < 2; x++)
                Assert.Equal(0, PixelAt(empty, x, y).A);
    }

    [Fact]
    public async Task Hgt_tile_with_void_renders_coloured_png_with_transparent_void()
    {
        short[] samples =
        [
            Hgt.Void, -100,
            100, 300,
        ];
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(i * 2, 2), samples[i]);
        var source = NewPath("N37W122.hgt");
        File.WriteAllBytes(source, bytes);

        var job = await fx.ConvertAsync([source], ColourPreset(RasterColours.Terrain));
        AssertDone(job);

        using var image = new MagickImage(job.Outputs.Single());
        Assert.Equal((2u, 2u), (image.Width, image.Height));
        Assert.Equal(0, PixelAt(image, 0, 0).A);                        // void -> transparent
        AssertColourNear(PixelAt(image, 1, 0), 0x00, 0x61, 0x30, 255);  // min (-100)
        AssertColourNear(PixelAt(image, 0, 1), 0xE8, 0xD5, 0x7A, 255);  // mid (100)
        AssertColourNear(PixelAt(image, 1, 1), 0xFF, 0xFF, 0xFF, 255);  // max (300)
    }

    [Fact]
    public async Task Three_band_geotiff_and_jpeg_source_are_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        float[] r = [100f, 200f, 300f, 400f];
        float[] g = [10f, 20f, 30f, 40f];
        float[] b = [1f, 2f, 3f, 4f];
        var rgbGeo = NewPath("rgb_geotiff.tif");
        TiffWriter.Write(rgbGeo, [r, g, b], 2, 2, TiffSampleKind.UInt16, TiffCompression.None, 72, ct, BasicGeoTags);

        var rgbJob = await fx.ConvertAsync([rgbGeo], ColourPreset(RasterColours.Terrain));
        Assert.Equal(JobState.Failed, rgbJob.State);
        Assert.Equal("error.unsupported_source", Assert.Single(rgbJob.Files).ErrorKey);

        var jpg = EngineFixture.MakeImage(fx.NewFolder(), "photo.jpg", MagickFormat.Jpeg);
        var jpgJob = await fx.ConvertAsync([jpg], ColourPreset(RasterColours.Viridis));
        Assert.Equal(JobState.Failed, jpgJob.State);
        Assert.Equal("error.unsupported_source", Assert.Single(jpgJob.Files).ErrorKey);

        // An unreadable/unsupported TIFF (JPEG-compressed non-GeoTIFF) is also refused.
        var jpegTiff = NewPath("jpeg_compressed.tif");
        using (var img = new MagickImage(MagickColors.Red, 8, 8))
        {
            img.Settings.Compression = CompressionMethod.JPEG;
            img.Write(jpegTiff, MagickFormat.Tiff);
        }
        var jpegTiffJob = await fx.ConvertAsync([jpegTiff], ColourPreset(RasterColours.Terrain));
        Assert.Equal(JobState.Failed, jpegTiffJob.State);
        Assert.Equal("error.unsupported_source", Assert.Single(jpegTiffJob.Files).ErrorKey);
    }

    [Fact]
    public async Task Existing_output_file_survives_a_refused_colour_preview()
    {
        var folder = fx.NewFolder();
        var rgbGeo = Path.Combine(folder, "scene.tif");
        TiffWriter.Write(rgbGeo, [[1f, 2f, 3f, 4f], [5f, 6f, 7f, 8f], [9f, 10f, 11f, 12f]], 2, 2,
            TiffSampleKind.Float32, TiffCompression.None, 72, TestContext.Current.CancellationToken, BasicGeoTags);

        var existing = Path.Combine(folder, "scene_colours.png");
        var sentinel = "keep-this-existing-png"u8.ToArray();
        File.WriteAllBytes(existing, sentinel);

        var job = await fx.ConvertAsync([rgbGeo], ColourPreset(RasterColours.Terrain, ConflictPolicy.Overwrite));

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("error.unsupported_source", Assert.Single(job.Files).ErrorKey);
        Assert.True(File.Exists(existing));
        Assert.Equal(sentinel, File.ReadAllBytes(existing));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

    [Fact]
    public async Task Normal_png_preset_on_geotiff_still_renders_greyscale()
    {
        float[] plane = [100f, 1550f, 3000f, 1550f];
        var source = NewPath("dem_grey.tif");
        TiffWriter.Write(source, [plane], 2, 2, TiffSampleKind.Float32, TiffCompression.None, 72,
            TestContext.Current.CancellationToken, BasicGeoTags);

        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "png" });
        AssertDone(job);

        using var image = new MagickImage(job.Outputs.Single());
        Assert.Equal(ColorType.Grayscale, image.ColorType);
        var min = PixelAt(image, 0, 0);
        var mid = PixelAt(image, 1, 0);
        var max = PixelAt(image, 0, 1);
        Assert.Equal((0, 0, 0), (min.R, min.G, min.B));
        Assert.InRange(mid.R, 127, 128);
        Assert.Equal((mid.R, mid.R), (mid.G, mid.B));
        Assert.Equal((255, 255, 255), (max.R, max.G, max.B));
    }
}
