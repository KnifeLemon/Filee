// SRTM .hgt elevation tiles (#1793): reading big-endian signed 16-bit square grids into signed 16-bit GeoTIFFs
// (with GDAL_NODATA and WGS 84 GeoTIFF placement when the file name encodes a tile corner) and stretched grayscale PNGs,
// plus refusals for malformed inputs without harming an existing output file.

using System.Buffers.Binary;
using System.Text;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Magick;
using ImageMagick;

namespace Filee.Engines.Tests;

public class HgtTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private string SaveHgt(short[] samples, string name)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(i * 2, 2), samples[i]);
        return SaveRaw(bytes, name);
    }

    private string SaveRaw(byte[] bytes, string name)
    {
        var path = Path.Combine(fx.NewFolder(), name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void AssertDone(ConversionJob job) =>
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));

    private async Task<string> ConvertAsync(string source, string target, Action<Preset>? configure = null)
    {
        var preset = new Preset { TargetFormat = target };
        configure?.Invoke(preset);
        var job = await fx.ConvertAsync([source], preset);
        AssertDone(job);
        return job.Outputs.Single();
    }

    [Fact]
    public async Task North_west_tile_writes_signed_16_bit_geotiff_with_nodata_and_wgs84_tags()
    {
        // 3×3 grid with positive, zero, negative and void (-32768) elevations.
        short[] grid =
        [
            -32768, -420, 0,
            15, 1024, 32767,
            -1, 250, -32768,
        ];
        var source = SaveHgt(grid, "N37W122.hgt");

        var tiff = await ConvertAsync(source, "tiff", p => p.Image.TiffCompression = TiffCompression.None);

        var probe = GeoTiffProbe.Read(tiff);
        Assert.Equal((3, 3, 16, 2, 1), (probe.Width, probe.Height, probe.BitsPerSample, probe.SampleFormat, probe.SamplesPerPixel));
        Assert.Equal(grid, probe.Int16Samples);
        Assert.Equal("-32768", probe.GdalNoData);

        Assert.NotNull(probe.ModelPixelScale);
        Assert.Equal(3, probe.ModelPixelScale!.Length);
        Assert.Equal(0.5, probe.ModelPixelScale[0], 12); // 1 / (3 - 1)
        Assert.Equal(0.5, probe.ModelPixelScale[1], 12);
        Assert.Equal(0.0, probe.ModelPixelScale[2], 12);

        Assert.NotNull(probe.ModelTiepoint);
        Assert.Equal([0.0, 0.0, 0.0, -122.0, 38.0, 0.0], probe.ModelTiepoint);

        Assert.NotNull(probe.GeoKeyDirectory);
        Assert.Equal(new ushort[]
        {
            1, 1, 0, 3,
            1024, 0, 1, 2,
            1025, 0, 1, 2,
            2048, 0, 1, 4326,
        }, probe.GeoKeyDirectory);
    }

    [Fact]
    public async Task South_east_lowercase_tile_writes_expected_tiepoint_and_pixel_scale()
    {
        // 5×5 grid -> spacing 1 / 4 = 0.25 degrees; S12E045 -> SW (-12, +45), NW tiepoint (45, -11).
        var grid = Enumerable.Range(0, 25).Select(i => (short)(i * 100 - 500)).ToArray();
        grid[12] = Hgt.Void;
        var source = SaveHgt(grid, "s12e045.hgt");

        var tiff = await ConvertAsync(source, "tiff", p => p.Image.TiffCompression = TiffCompression.None);

        var probe = GeoTiffProbe.Read(tiff);
        Assert.Equal((5, 5, 16, 2, 1), (probe.Width, probe.Height, probe.BitsPerSample, probe.SampleFormat, probe.SamplesPerPixel));
        Assert.Equal(grid, probe.Int16Samples);
        Assert.Equal("-32768", probe.GdalNoData);
        Assert.NotNull(probe.ModelPixelScale);
        Assert.Equal([0.25, 0.25, 0.0], probe.ModelPixelScale!);
        Assert.NotNull(probe.ModelTiepoint);
        Assert.Equal([0.0, 0.0, 0.0, 45.0, -11.0, 0.0], probe.ModelTiepoint!);
        Assert.NotNull(probe.GeoKeyDirectory);
    }

    [Theory]
    [InlineData("terrain.hgt")]
    [InlineData("N95W122.hgt")]
    [InlineData("N37W181.hgt")]
    public async Task Unparsable_or_out_of_range_name_writes_plain_signed_16_bit_tiff_without_geo_tags(string fileName)
    {
        short[] grid = [-50, 0, 50, Hgt.Void];
        var source = SaveHgt(grid, fileName);

        var tiff = await ConvertAsync(source, "tiff", p => p.Image.TiffCompression = TiffCompression.None);

        var probe = GeoTiffProbe.Read(tiff);
        Assert.Equal((2, 2, 16, 2, 1), (probe.Width, probe.Height, probe.BitsPerSample, probe.SampleFormat, probe.SamplesPerPixel));
        Assert.Equal(grid, probe.Int16Samples);
        Assert.Equal("-32768", probe.GdalNoData);
        Assert.Null(probe.ModelPixelScale);
        Assert.Null(probe.ModelTiepoint);
        Assert.Null(probe.GeoKeyDirectory);
    }

    [Theory]
    [InlineData(TiffCompression.Lzw)]
    [InlineData(TiffCompression.Zip)]
    public async Task Compressed_geotiff_keeps_tags_and_opens_in_libtiff(TiffCompression compression)
    {
        var grid = new short[16 * 16];
        for (var i = 0; i < grid.Length; i++)
            grid[i] = i == 0 ? Hgt.Void : (short)(i * 20 - 1000);
        var source = SaveHgt(grid, "N45E007.hgt");

        var tiff = await ConvertAsync(source, "tiff", p => p.Image.TiffCompression = compression);

        var probe = GeoTiffProbe.Read(tiff);
        Assert.Equal((16, 16, 16, 2, 1), (probe.Width, probe.Height, probe.BitsPerSample, probe.SampleFormat, probe.SamplesPerPixel));
        Assert.Equal("-32768", probe.GdalNoData);
        Assert.NotNull(probe.ModelTiepoint);
        Assert.Equal([0.0, 0.0, 0.0, 7.0, 46.0, 0.0], probe.ModelTiepoint!);
        Assert.NotNull(probe.GeoKeyDirectory);

        using var image = new MagickImage(tiff);
        Assert.Equal((16u, 16u, 16u), (image.Width, image.Height, image.Depth));
    }

    [Fact]
    public async Task Hgt_converts_to_stretched_grayscale_png_with_voids_black()
    {
        short[] grid =
        [
            Hgt.Void, -100, 0,
            100, 200, 300,
            -100, 100, 300,
        ];
        var source = SaveHgt(grid, "N37W122.hgt");

        var png = await ConvertAsync(source, "png");

        using var image = new MagickImage(png);
        Assert.Equal((3u, 3u), (image.Width, image.Height));
        Assert.Equal(ColorType.Grayscale, image.ColorType);
        var pixels = image.GetPixels();
        Assert.Equal(0, pixels.GetPixel(0, 0).GetChannel(0));   // void -> black
        Assert.Equal(0, pixels.GetPixel(1, 0).GetChannel(0));   // min (-100) -> 0
        Assert.Equal(255, pixels.GetPixel(2, 1).GetChannel(0)); // max (300) -> 255
        Assert.InRange(pixels.GetPixel(0, 1).GetChannel(0), 127, 128); // midpoint (100) -> ~128
    }

    [Fact]
    public void Route_planner_uses_direct_edge_for_tiff_and_stretched_picture_for_pdf()
    {
        var planner = fx.Catalog.CreatePlanner();

        Assert.Single(planner.Plan("hgt", "tiff")!.Steps);
        Assert.Single(planner.Plan("hgt", "png")!.Steps);
        Assert.DoesNotContain(planner.Plan("hgt", "pdf")!.Steps, s => s.To == "tiff");
    }

    [Theory]
    [InlineData(7)]  // odd byte count
    [InlineData(10)] // 5 samples, not a square
    [InlineData(2)]  // 1×1, below minimum side 2
    public async Task Invalid_file_sizes_are_refused_with_unsupported_source(int byteCount)
    {
        var source = SaveRaw(new byte[byteCount], "N37W122.hgt");

        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "tiff" });

        Assert.Equal(JobState.Failed, job.State);
        var result = Assert.Single(job.Files);
        Assert.Equal("error.unsupported_source", result.ErrorKey);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorDetail));
    }

    [Fact]
    public async Task Empty_file_is_refused_by_queue_and_reader()
    {
        var source = SaveRaw([], "N37W122.hgt");

        var job = await fx.ConvertAsync([source], new Preset { TargetFormat = "tiff" });

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("error.empty_file", Assert.Single(job.Files).ErrorKey);

        var ex = Assert.Throws<ConversionRefusedException>(() => Hgt.Read(source, TestContext.Current.CancellationToken));
        Assert.Equal("error.unsupported_source", ex.ErrorKey);
    }

    [Fact]
    public async Task Existing_output_file_survives_a_refused_conversion()
    {
        var folder = fx.NewFolder();
        var bad = Path.Combine(folder, "N37W122.hgt");
        File.WriteAllBytes(bad, new byte[7]);
        var existing = Path.Combine(folder, "N37W122.tiff");
        var sentinel = "keep-this-existing-tiff"u8.ToArray();
        File.WriteAllBytes(existing, sentinel);

        var preset = new Preset
        {
            TargetFormat = "tiff",
            Output = { Conflict = ConflictPolicy.Overwrite },
        };
        var job = await fx.ConvertAsync([bad], preset);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("error.unsupported_source", Assert.Single(job.Files).ErrorKey);
        Assert.True(File.Exists(existing));
        Assert.Equal(sentinel, File.ReadAllBytes(existing));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

    /// <summary>Minimal little-endian TIFF/GeoTIFF IFD reader for asserting tags and uncompressed signed 16-bit samples.</summary>
    private sealed record GeoTiffProbe(
        int Width,
        int Height,
        int BitsPerSample,
        int SampleFormat,
        int SamplesPerPixel,
        short[] Int16Samples,
        string? GdalNoData,
        double[]? ModelPixelScale,
        double[]? ModelTiepoint,
        ushort[]? GeoKeyDirectory)
    {
        public static GeoTiffProbe Read(string path)
        {
            var file = File.ReadAllBytes(path);
            Assert.Equal("II*\0", Encoding.ASCII.GetString(file, 0, 4));
            var ifd = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4, 4));
            var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(ifd, 2));

            var entries = new Dictionary<ushort, (ushort Type, int Count, byte[] Data)>();
            var previousTag = -1;
            for (var i = 0; i < entryCount; i++)
            {
                var entry = file.AsSpan(ifd + 2 + i * 12, 12);
                var tag = BinaryPrimitives.ReadUInt16LittleEndian(entry);
                var type = BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]);
                var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
                Assert.True(tag > previousTag, $"IFD tags must be strictly ascending: {tag} after {previousTag}");
                previousTag = tag;

                var unitSize = type switch
                {
                    1 or 2 => 1,
                    3 => 2,
                    4 => 4,
                    5 or 12 => 8,
                    _ => throw new InvalidDataException($"Unexpected TIFF type {type} on tag {tag}."),
                };
                var byteCount = count * unitSize;
                var raw = byteCount <= 4
                    ? entry.Slice(8, byteCount).ToArray()
                    : file.AsSpan((int)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]), byteCount).ToArray();
                entries[tag] = (type, count, raw);
            }

            uint ReadScalar(ushort tag) => entries[tag].Type == 3
                ? BinaryPrimitives.ReadUInt16LittleEndian(entries[tag].Data)
                : BinaryPrimitives.ReadUInt32LittleEndian(entries[tag].Data);

            uint[] ReadUInt32Array(ushort tag)
            {
                var (type, count, data) = entries[tag];
                var result = new uint[count];
                for (var i = 0; i < count; i++)
                    result[i] = type == 3
                        ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i * 2, 2))
                        : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 4, 4));
                return result;
            }

            double[]? ReadDoubles(ushort tag)
            {
                if (!entries.TryGetValue(tag, out var e))
                    return null;
                Assert.Equal(12, e.Type);
                var result = new double[e.Count];
                for (var i = 0; i < e.Count; i++)
                    result[i] = BinaryPrimitives.ReadDoubleLittleEndian(e.Data.AsSpan(i * 8, 8));
                return result;
            }

            ushort[]? ReadShorts(ushort tag)
            {
                if (!entries.TryGetValue(tag, out var e))
                    return null;
                Assert.Equal(3, e.Type);
                var result = new ushort[e.Count];
                for (var i = 0; i < e.Count; i++)
                    result[i] = BinaryPrimitives.ReadUInt16LittleEndian(e.Data.AsSpan(i * 2, 2));
                return result;
            }

            string? ReadAscii(ushort tag)
            {
                if (!entries.TryGetValue(tag, out var e))
                    return null;
                Assert.Equal(2, e.Type);
                Assert.Equal(0, e.Data[^1]);
                return Encoding.ASCII.GetString(e.Data, 0, e.Data.Length - 1);
            }

            var width = (int)ReadScalar(256);
            var height = (int)ReadScalar(257);
            var bits = (int)ReadScalar(258);
            var compression = (int)ReadScalar(259);
            var samplesPerPixel = (int)ReadScalar(277);
            var sampleFormat = entries.ContainsKey(339) ? (int)ReadScalar(339) : 1;

            var int16Samples = new List<short>();
            if (compression == 1 && bits == 16)
            {
                var offsets = ReadUInt32Array(273);
                var byteCounts = ReadUInt32Array(279);
                for (var s = 0; s < offsets.Length; s++)
                {
                    var strip = file.AsSpan((int)offsets[s], (int)byteCounts[s]);
                    for (var i = 0; i < strip.Length; i += 2)
                        int16Samples.Add(BinaryPrimitives.ReadInt16LittleEndian(strip.Slice(i, 2)));
                }
            }

            return new GeoTiffProbe(
                width,
                height,
                bits,
                sampleFormat,
                samplesPerPixel,
                [.. int16Samples],
                ReadAscii(Hgt.GdalNoDataTag),
                ReadDoubles(Hgt.ModelPixelScaleTag),
                ReadDoubles(Hgt.ModelTiepointTag),
                ReadShorts(Hgt.GeoKeyDirectoryTag));
        }
    }
}
