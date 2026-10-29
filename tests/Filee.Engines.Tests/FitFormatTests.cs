using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Formats;
using Filee.Engines.Geo;

namespace Filee.Engines.Tests;

public class FitFormatTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const uint First = 0x1000003B;
    private static DateTime Utc(uint seconds) => DateTime.UnixEpoch.AddSeconds(seconds + 631065600L);
    private static string Stamp(uint seconds) => Utc(seconds).ToString("yyyy-MM-ddTHH:mm:ssZ");

    private string Save(FitBuilder builder) => FitBuilder.Save(fx.NewFolder(), builder.Build(), "ride.fit");

    private string Track(params (uint? Time, int? Lat, int? Lon, ushort? Height)[] records) =>
        Save(Append(new FitBuilder().FileId(), records));

    private static FitBuilder Append(FitBuilder builder, params (uint? Time, int? Lat, int? Lon, ushort? Height)[] records)
    {
        foreach (var (time, lat, lon, height) in records)
        {
            var fields = new List<FitBuilder.FitField>();
            if (time is { } t) fields.Add(FitBuilder.U32(253, t));
            if (lat is { } y) fields.Add(FitBuilder.S32(0, y));
            if (lon is { } x) fields.Add(FitBuilder.S32(1, x));
            if (height is { } z) fields.Add(FitBuilder.U16(2, z));
            builder.Define(0, 20, fields: [.. fields]).Message(0, [.. fields]);
        }
        return builder;
    }

    private static (uint?, int?, int?, ushort?)[] Three(bool times = true, bool heights = true) =>
    [
        (times ? First : null, 0x20000000, 0x40000000, heights ? (ushort)3000 : null),
        (times ? First + 1 : null, 0x30000000, 0x20000000, heights ? (ushort)3010 : null),
        (times ? First + 2 : null, 0x10000000, 0x30000000, heights ? (ushort)3020 : null),
    ];

    private static XElement[] Elements(string path, string name) =>
        [.. XDocument.Load(path).Descendants().Where(e => e.Name.LocalName == name)];

    private static string? Child(XElement element, string name) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    private static JsonElement Feature(JsonDocument document) =>
        document.RootElement.GetProperty("features")[0];

    /// <summary>Converts the FIT file to CSV, XLSX and JSON, checks that the XLSX (read back as CSV) and the JSON hold
    /// the same rows and values as the CSV, and returns the CSV lines.</summary>
    private async Task<string[]> SameTables(string input)
    {
        var csv = File.ReadAllLines((await fx.ConvertOkAsync(input, "csv")).Single());
        var xlsx = (await fx.ConvertOkAsync(input, "xlsx")).Single();
        Assert.Equal(csv, File.ReadAllLines((await fx.ConvertOkAsync(xlsx, "csv")).Single()));
        using var json = GeoFixtures.ReadJson((await fx.ConvertOkAsync(input, "json")).Single());
        var header = csv[0].Split(',');
        Assert.Equal(csv[1..], json.RootElement.EnumerateArray().Select(row => string.Join(",", header.Select(column =>
            row.GetProperty(column) is { ValueKind: JsonValueKind.String } text ? text.GetString() : row.GetProperty(column).GetRawText()))));
        return csv;
    }

    [Fact]
    public async Task Fit_track_to_gpx_keeps_order_coordinates_heights_and_point_times()
    {
        var gpx = (await fx.ConvertOkAsync(Track(Three()), "gpx")).Single();
        var points = Elements(gpx, "trkpt");
        Assert.Single(Elements(gpx, "trk"));
        Assert.Equal(3, points.Length);
        Assert.Equal(["45", "67.5", "22.5"], points.Select(p => (string?)p.Attribute("lat")));
        Assert.Equal(["90", "45", "67.5"], points.Select(p => (string?)p.Attribute("lon")));
        Assert.Equal(["100", "102", "104"], points.Select(p => Child(p, "ele")));
        Assert.Equal([Stamp(First), Stamp(First + 1), Stamp(First + 2)], points.Select(p => Child(p, "time")));
        Assert.Equal([45.0, 67.5, 22.5], Assert.IsType<GeoLineString>(
            Assert.Single(GpxFormat.Read(gpx, CancellationToken.None).Features).Geometry).Points.Select(p => p.Y));
    }

    [Fact]
    public async Task Gpx_reports_the_start_only_when_no_point_keeps_a_time()
    {
        var firstUntimed = Three();
        firstUntimed[0].Item1 = null;
        foreach (var (start, records) in new (uint?, (uint?, int?, int?, ushort?)[])[]
        {
            (null, Three()), (First - 10, Three()), (First, Three()), (First - 10, firstUntimed),
        })
        {
            var builder = new FitBuilder().FileId();
            if (start is { } t) builder.Session(t, 1);
            var input = Save(Append(builder, records));
            var job = await fx.ConvertAsync(input, "gpx");
            Assert.Null(job.Files.Single().NoticeKey);
        }
        var withoutTimes = Save(Append(new FitBuilder().FileId().Session(First, 1), Three(times: false)));
        Assert.Equal("notice.geo_gpx_lossy", (await fx.ConvertAsync(withoutTimes, "gpx")).Files.Single().NoticeKey);
        var point = (await fx.ConvertOkAsync(Track((First, 0x20000000, 0x40000000, 3000)), "gpx")).Single();
        Assert.Equal(Stamp(First), Child(Assert.Single(Elements(point, "wpt")), "time"));
    }

    [Fact]
    public async Task Non_finite_float_values_are_left_out_like_invalid_ones()
    {
        static FitBuilder.FitField F32(byte number, float value) => new(number, 0x88, BitConverter.GetBytes(value));
        static FitBuilder.FitField F64(byte number, double value) => new(number, 0x89, BitConverter.GetBytes(value));
        FitBuilder.FitField[][] records =
        [
            [FitBuilder.U32(253, First), FitBuilder.S32(0, 0x20000000), FitBuilder.S32(1, 0x40000000), FitBuilder.U16(2, 3000)],
            [FitBuilder.U32(253, First + 1), F32(0, float.NaN), FitBuilder.S32(1, 0x20000000), F64(2, double.PositiveInfinity)],
            [FitBuilder.U32(253, First + 2), FitBuilder.S32(0, 0x10000000), F64(1, double.NegativeInfinity)],
            [FitBuilder.U32(253, First + 3), FitBuilder.S32(0, 0x30000000), FitBuilder.S32(1, 0x30000000), F32(2, float.PositiveInfinity)],
        ];
        var builder = new FitBuilder().FileId();
        foreach (var fields in records)
            builder.Define(0, 20, fields: fields).Message(0, fields);
        var input = Save(builder);

        var gpx = (await fx.ConvertOkAsync(input, "gpx")).Single();
        Assert.DoesNotContain("NaN", File.ReadAllText(gpx));
        Assert.DoesNotContain("Infinity", File.ReadAllText(gpx));
        var points = Elements(gpx, "trkpt");
        Assert.Equal(["45", "67.5"], points.Select(p => (string?)p.Attribute("lat")));
        Assert.Equal(["90", "67.5"], points.Select(p => (string?)p.Attribute("lon")));
        Assert.Equal(["100", null], points.Select(p => Child(p, "ele")));
        Assert.Equal(
        [
            "time,longitude,latitude,elevation,heart_rate,cadence,power,speed,distance",
            $"{Stamp(First)},90,45,100,,,,,",
            $"{Stamp(First + 1)},,,,,,,,",
            $"{Stamp(First + 2)},,,,,,,,",
            $"{Stamp(First + 3)},67.5,67.5,,,,,,",
        ], File.ReadAllLines((await fx.ConvertOkAsync(input, "csv")).Single()));
    }

    [Fact]
    public async Task Values_past_their_profile_range_are_left_out_whatever_their_base_type()
    {
        // A float or wider integer can carry values far past what the profile allows; those are dropped.
        FitBuilder.FitField[] wild =
        [
            FitBuilder.U32(253, First), FitBuilder.S32(0, 0x20000000), FitBuilder.S32(1, 0x40000000),
            new(2, 0x88, BitConverter.GetBytes(3e38f)), FitBuilder.U32(3, 4_000_000_000), new(4, 0x88, BitConverter.GetBytes(1e30f)),
        ];
        // FIT reads a value in the base type its definition gives, so a uint16 heart rate or a uint32 height is valid.
        // A negative sint8 cadence is past the uint8 range.
        FitBuilder.FitField[] wide =
        [
            FitBuilder.U32(253, First + 1), FitBuilder.U32(2, 3000), FitBuilder.U16(3, 150), new(4, 0x01, [unchecked((byte)-5)]),
        ];
        // A fraction is not a valid integer value, nor is the sint32 invalid longitude written in a wider type; a whole
        // float cadence and a sint32 power are valid.
        FitBuilder.FitField[] mixed =
        [
            FitBuilder.U32(253, First + 2), FitBuilder.S32(0, 0x20000000), new(1, 0x8E, BitConverter.GetBytes((long)int.MaxValue)),
            new(3, 0x88, BitConverter.GetBytes(72.5f)), new(4, 0x88, BitConverter.GetBytes(90f)), new(7, 0x85, BitConverter.GetBytes(300)),
        ];
        var input = Save(new FitBuilder().FileId().Define(0, 20, fields: wild).Message(0, wild)
            .Define(1, 20, fields: wide).Message(1, wide).Define(2, 20, fields: mixed).Message(2, mixed));

        var gpx = (await fx.ConvertOkAsync(input, "gpx")).Single();
        Assert.Null(Child(Assert.Single(Elements(gpx, "wpt")), "ele"));
        Assert.Equal(
        [
            "time,longitude,latitude,elevation,heart_rate,cadence,power,speed,distance",
            $"{Stamp(First)},90,45,,,,,,",
            $"{Stamp(First + 1)},,,100,150,,,,",
            $"{Stamp(First + 2)},,,,,90,300,,",
        ], File.ReadAllLines((await fx.ConvertOkAsync(input, "csv")).Single()));

        // A file type of 4.5 is not activity (4) cut short: the file says no valid type.
        FitBuilder.FitField[] halfType = [new(0, 0x88, BitConverter.GetBytes(4.5f))];
        var refused = Assert.Throws<ConversionRefusedException>(() => FitFormat.Read(
            Save(new FitBuilder().Define(15, 0, fields: halfType).Message(15, halfType).Define(0, 20, fields: wide).Message(0, wide)),
            CancellationToken.None));
        Assert.Equal("error.fit_no_file_id", refused.ErrorKey);
    }

    [Fact]
    public async Task Single_position_is_a_point_in_each_spatial_format()
    {
        foreach (var height in new ushort?[] { 3000, null })
        {
            var input = Track((First, 0x20000000, 0x40000000, height));
            var gpx = (await fx.ConvertOkAsync(input, "gpx")).Single();
            Assert.Single(Elements(gpx, "wpt"));
            var kml = (await fx.ConvertOkAsync(input, "kml")).Single();
            Assert.Single(Elements(kml, "Point"));
            using var json = GeoFixtures.ReadJson((await fx.ConvertOkAsync(input, "geojson")).Single());
            Assert.Equal("Point", Feature(json).GetProperty("geometry").GetProperty("type").GetString());
            Assert.Equal(Stamp(First), Feature(json).GetProperty("properties").GetProperty("time").GetString());
            var shp = (await fx.ConvertOkAsync(input, "shp")).Single(p => p.EndsWith(".shp", StringComparison.Ordinal));
            Assert.Equal(height is null ? 1 : 11, BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(shp).AsSpan(32)));
            var position = Assert.IsType<GeoPoint>(Assert.Single(ShapefileFormat.Read(shp, CancellationToken.None).Features).Geometry).Position;
            Assert.Equal(90, position.X);
            Assert.Equal(45, position.Y);
            Assert.Equal(height is null ? double.NaN : 100, position.Z);
        }
    }

    [Fact]
    public async Task No_positions_refuses_spatial_outputs_but_keeps_tabular_records()
    {
        var input = Track((First, null, null, null), (First + 1, null, null, 3000));
        foreach (var target in new[] { "gpx", "kml", "kmz", "geojson", "shp" })
        {
            Assert.Equal("error.fit_no_positions", (await fx.ConvertFailsAsync(input, target)).Key);
            Assert.Equal([input], Directory.GetFiles(Path.GetDirectoryName(input)!));
        }
        Assert.Equal(["time,longitude,latitude,elevation,heart_rate,cadence,power,speed,distance",
            $"{Stamp(First)},,,,,,,,", $"{Stamp(First + 1)},,,100,,,,,"], await SameTables(input));
    }

    [Fact]
    public async Task Kml_tracks_and_kmz_payload_keep_all_point_times()
    {
        var input = Track(Three());
        var kml = (await fx.ConvertOkAsync(input, "kml")).Single();
        var xml = XDocument.Load(kml);
        Assert.Single(xml.Descendants(), e => e.Name.LocalName == "Track");
        Assert.Equal([Stamp(First), Stamp(First + 1), Stamp(First + 2)],
            xml.Descendants().Where(e => e.Name.LocalName == "when").Select(e => e.Value));
        Assert.Equal(["90 45 100", "45 67.5 102", "67.5 22.5 104"],
            xml.Descendants().Where(e => e.Name.LocalName == "coord").Select(e => e.Value));
        Assert.Equal(3, Assert.IsType<GeoLineString>(Assert.Single(KmlFormat.Read(kml, false, CancellationToken.None).Features).Geometry).Points.Count);
        using var archive = ZipFile.OpenRead((await fx.ConvertOkAsync(input, "kmz")).Single());
        using var stream = archive.GetEntry("doc.kml")!.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        Assert.Equal(File.ReadAllBytes(kml), copy.ToArray());
    }

    [Fact]
    public async Task Geojson_times_follow_each_line_position_and_survive_reading()
    {
        var output = (await fx.ConvertOkAsync(Track(Three()), "geojson")).Single();
        using var doc = GeoFixtures.ReadJson(output);
        var times = Feature(doc).GetProperty("properties").GetProperty("coordTimes");
        Assert.Equal([Stamp(First), Stamp(First + 1), Stamp(First + 2)], times.EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(3, Feature(doc).GetProperty("geometry").GetProperty("coordinates").GetArrayLength());
        Assert.Equal(times.GetRawText(), Assert.Single(GeoJsonFormat.Read(output, CancellationToken.None).Features).FindText("coordTimes"));
    }

    [Fact]
    public async Task Partly_missing_times_keep_every_position_without_inventing_times()
    {
        var first = new[] { FitBuilder.S32(0, 0x20000000), FitBuilder.S32(1, 0x40000000), FitBuilder.U16(2, 3000) };
        var invalid = new[] { FitBuilder.U32(253, uint.MaxValue), FitBuilder.S32(0, 0x30000000),
            FitBuilder.S32(1, 0x20000000), FitBuilder.U16(2, 3010) };
        var last = new[] { FitBuilder.U32(253, First + 2), FitBuilder.S32(0, 0x10000000),
            FitBuilder.S32(1, 0x30000000), FitBuilder.U16(2, 3020) };
        var input = Save(new FitBuilder().FileId().Define(1, 20, fields: first).Compressed(1, 0, first)
            .Define(0, 20, fields: invalid).Message(0, invalid).Message(0, last));
        var gpx = (await fx.ConvertOkAsync(input, "gpx")).Single();
        Assert.Equal([null, null, Stamp(First + 2)], Elements(gpx, "trkpt").Select(p => Child(p, "time")));
        var kml = (await fx.ConvertOkAsync(input, "kml")).Single();
        Assert.Empty(Elements(kml, "Track"));
        Assert.Equal(3, Assert.IsType<GeoLineString>(Assert.Single(KmlFormat.Read(kml, false, CancellationToken.None).Features).Geometry).Points.Count);
        Assert.Equal($"[null,null,\"{Stamp(First + 2)}\"]", Assert.Single(Elements(kml, "Data"),
            data => (string?)data.Attribute("name") == "coordTimes").Value);
        using var json = GeoFixtures.ReadJson((await fx.ConvertOkAsync(input, "geojson")).Single());
        var times = Feature(json).GetProperty("properties").GetProperty("coordTimes");
        Assert.Equal(JsonValueKind.Null, times[0].ValueKind);
        Assert.Equal(JsonValueKind.Null, times[1].ValueKind);
        Assert.Equal(3, times.GetArrayLength());
    }

    [Fact]
    public async Task Completely_missing_times_leave_tracks_without_time_attributes()
    {
        var input = Track(Three(times: false));
        Assert.Empty(Elements((await fx.ConvertOkAsync(input, "gpx")).Single(), "time"));
        var kml = (await fx.ConvertOkAsync(input, "kml")).Single();
        Assert.Empty(Elements(kml, "Track"));
        Assert.Empty(Elements(kml, "Data"));
        Assert.Single(Elements(kml, "LineString"));
        using var json = GeoFixtures.ReadJson((await fx.ConvertOkAsync(input, "geojson")).Single());
        Assert.False(Feature(json).GetProperty("properties").TryGetProperty("coordTimes", out _));
        Assert.Equal(3, Feature(json).GetProperty("geometry").GetProperty("coordinates").GetArrayLength());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sparse_heights_are_never_fabricated_and_shapefile_drops_them_with_notice(bool times)
    {
        var records = Three(times);
        records[1].Item4 = null;
        var input = Track(records);
        var gpx = (await fx.ConvertOkAsync(input, "gpx")).Single();
        Assert.Equal(["100", null, "104"], Elements(gpx, "trkpt").Select(p => Child(p, "ele")));
        var kml = (await fx.ConvertOkAsync(input, "kml")).Single();
        Assert.Equal("90,45,100 45,67.5 67.5,22.5,104", Assert.Single(Elements(kml, "coordinates")).Value);
        Assert.Empty(Elements(kml, "Track"));
        Assert.Equal(times, Elements(kml, "Data").Any(data => (string?)data.Attribute("name") == "coordTimes"));
        using var json = GeoFixtures.ReadJson((await fx.ConvertOkAsync(input, "geojson")).Single());
        Assert.Equal([3, 2, 3], Feature(json).GetProperty("geometry").GetProperty("coordinates")
            .EnumerateArray().Select(position => position.GetArrayLength()));
        var csv = File.ReadAllLines((await fx.ConvertOkAsync(input, "csv")).Single());
        Assert.Equal("", csv[2].Split(',')[3]);
        var job = await fx.ConvertAsync(input, "shp");
        Assert.Equal("notice.geo_heights_dropped", job.Files.Single().NoticeKey);
        var shp = job.Outputs.Single(p => p.EndsWith(".shp", StringComparison.Ordinal));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(shp).AsSpan(32)));
        Assert.Equal([90.0, 45, 67.5], Assert.IsType<GeoLineString>(
            Assert.Single(ShapefileFormat.Read(shp, CancellationToken.None).Features).Geometry).Points.Select(p => p.X));
        var full = await fx.ConvertAsync(Track(Three(times)), "shp");
        Assert.NotEqual("notice.geo_heights_dropped", full.Files.Single().NoticeKey);
        Assert.Equal(13, BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(
            full.Outputs.Single(p => p.EndsWith(".shp", StringComparison.Ordinal))).AsSpan(32)));
    }

    [Fact]
    public async Task Fit_csv_has_fixed_columns_units_and_skips_empty_records()
    {
        var fields = new[] { FitBuilder.U32(253, First), FitBuilder.S32(0, 0x20000000),
            FitBuilder.S32(1, 0x40000000), FitBuilder.U16(2, 3000), FitBuilder.U8(3, 72),
            FitBuilder.U8(4, 88), FitBuilder.U16(7, 210), FitBuilder.U16(6, 4321), FitBuilder.U32(5, 12345) };
        var builder = new FitBuilder().FileId().Define(0, 20, fields: fields).Message(0, fields)
            .Define(3, 132, fields: [FitBuilder.U8(3, 99)]).Message(3, [FitBuilder.U8(3, 99)]);
        var input = Save(Append(builder, (null, null, null, 3000), (null, null, null, null)));
        var csv = File.ReadAllText((await fx.ConvertOkAsync(input, "csv")).Single());
        Assert.StartsWith("time,longitude,latitude,elevation,heart_rate,cadence,power,speed,distance\r\n", csv);
        Assert.Contains($"{Stamp(First)},90,45,100,72,88,210,4.321,123.45\r\n", csv);
        Assert.Equal(2, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain(",99,", csv);
        var noRecords = Track((null, null, null, 3000), (null, null, null, null));
        foreach (var target in new[] { "csv", "xlsx", "json" })
            Assert.Equal("error.fit_no_records", (await fx.ConvertFailsAsync(noRecords, target)).Key);
        Assert.Equal([noRecords], Directory.GetFiles(Path.GetDirectoryName(noRecords)!));
    }

    [Fact]
    public async Task Csv_xlsx_and_json_contain_the_same_records_including_missing_locations()
    {
        var fields = new[] { FitBuilder.U32(253, First), FitBuilder.S32(0, 0x20000000), FitBuilder.S32(1, 0x40000000) };
        var missing = new[] { FitBuilder.U32(253, First + 1), FitBuilder.S32(0, int.MaxValue), FitBuilder.S32(1, 0x40000000) };
        var input = Save(new FitBuilder().FileId().Define(0, 20, fields: fields).Message(0, fields).Message(0, missing));
        var csv = await SameTables(input);
        Assert.Equal(3, csv.Length);
        Assert.StartsWith($"{Stamp(First)},90,45,", csv[1]);
        Assert.StartsWith(Stamp(First + 1) + ",,,,", csv[2]);
    }

    [Fact]
    public async Task Header_endianness_compressed_time_and_developer_fields_keep_output_equal()
    {
        var ordinary = new[] { FitBuilder.U32(253, First), FitBuilder.S32(0, 0x20000000), FitBuilder.S32(1, 0x40000000) };
        var baseline = new FitBuilder().FileId().Define(0, 20, fields: ordinary).Message(0, ordinary);
        var expectedCsv = File.ReadAllBytes((await fx.ConvertOkAsync(Save(baseline), "csv")).Single());
        var expectedGpx = File.ReadAllBytes((await fx.ConvertOkAsync(Save(baseline), "gpx")).Single());
        foreach (var (headerSize, crc, big, developer) in new[]
        {
            (12, false, false, false), (14, false, false, false), (14, true, true, false),
            (14, true, false, true),
        })
        {
            var fields = new[] { FitBuilder.U32(253, First, big), FitBuilder.S32(0, 0x20000000, big),
                FitBuilder.S32(1, 0x40000000, big) };
            var sample = new FitBuilder().FileId().Define(0, 20, big: big,
                developer: developer ? [new(9, [1])] : null, fields: fields)
                .Message(0, fields, developer ? [new(9, [1])] : []);
            var input = FitBuilder.Save(fx.NewFolder(), sample.Build(headerSize, crc), "ride.fit");
            Assert.Equal(expectedCsv, File.ReadAllBytes((await fx.ConvertOkAsync(input, "csv")).Single()));
            Assert.Equal(expectedGpx, File.ReadAllBytes((await fx.ConvertOkAsync(input, "gpx")).Single()));
        }
        var position = new[] { FitBuilder.S32(0, 0x20000000), FitBuilder.S32(1, 0x40000000) };
        var compressed = Save(new FitBuilder().FileId().Define(0, 20, fields: ordinary).Message(0, ordinary)
            .Define(1, 20, fields: position).Compressed(1, 0x1C, position));
        Assert.Equal([Stamp(First), Stamp(First + 1)], Elements(
            (await fx.ConvertOkAsync(compressed, "gpx")).Single(), "trkpt").Select(p => Child(p, "time")));
        FitBuilder.FitField[] ordinaryValues = [.. ordinary, FitBuilder.U16(2, 3000), FitBuilder.U16(6, 4321)];
        FitBuilder.FitField[] enhancedValues = [.. ordinary, FitBuilder.U32(78, 3000), FitBuilder.U32(73, 4321)];
        var ordinaryInput = Save(new FitBuilder().FileId().Define(0, 20, fields: ordinaryValues).Message(0, ordinaryValues));
        var enhancedInput = Save(new FitBuilder().FileId().Define(0, 20, fields: enhancedValues).Message(0, enhancedValues));
        foreach (var target in new[] { "csv", "gpx" })
            Assert.Equal(File.ReadAllBytes((await fx.ConvertOkAsync(ordinaryInput, target)).Single()),
                File.ReadAllBytes((await fx.ConvertOkAsync(enhancedInput, target)).Single()));
    }

    [Fact]
    public void Planner_uses_every_real_engine_to_match_gpx_reachability()
    {
        var planner = fx.Catalog.CreatePlanner();
        foreach (var target in FormatRegistry.Known.Where(format => format.Writable))
            Assert.Equal(target.Id == "gpx" || planner.Plan("gpx", target.Id) is not null,
                planner.Plan("fit", target.Id) is not null);
    }

    [Fact]
    public async Task Session_sport_and_earliest_start_are_feature_properties()
    {
        var records = Three();
        var input = Save(Append(new FitBuilder().FileId().Session(First - 5, 1)
            .Session(First - 10, 2).Session(First - 3, 1), records));
        using var doc = GeoFixtures.ReadJson((await fx.ConvertOkAsync(input, "geojson")).Single());
        var properties = Feature(doc).GetProperty("properties");
        Assert.Equal(["type", "time", "coordTimes"], properties.EnumerateObject().Select(p => p.Name));
        Assert.Equal("running, cycling", properties.GetProperty("type").GetString());
        Assert.Equal(Stamp(First - 10), properties.GetProperty("time").GetString());
        var noSession = Track(records);
        using var plain = GeoFixtures.ReadJson((await fx.ConvertOkAsync(noSession, "geojson")).Single());
        Assert.Equal(Stamp(First), Feature(plain).GetProperty("properties").GetProperty("time").GetString());
        using var timeless = GeoFixtures.ReadJson((await fx.ConvertOkAsync(Track(Three(times: false)), "geojson")).Single());
        Assert.False(Feature(timeless).GetProperty("properties").TryGetProperty("time", out _));
        using var unknown = GeoFixtures.ReadJson((await fx.ConvertOkAsync(Save(Append(
            new FitBuilder().FileId().Session(First, 200), records)), "geojson")).Single());
        Assert.Equal("200", Feature(unknown).GetProperty("properties").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Relative_timestamps_remain_empty_in_csv_and_gpx()
    {
        var input = Track((0x0FFFFFFF, 0x20000000, 0x40000000, 3000));
        Assert.StartsWith("time,longitude", File.ReadAllText((await fx.ConvertOkAsync(input, "csv")).Single()));
        Assert.StartsWith(",90,45", File.ReadAllLines((await fx.ConvertOkAsync(input, "csv")).Single())[1]);
        Assert.Empty(Elements((await fx.ConvertOkAsync(input, "gpx")).Single(), "time"));
        var position = new[] { FitBuilder.S32(0, 0x30000000), FitBuilder.S32(1, 0x40000000) };
        var relative = new[] { FitBuilder.U32(253, 0x0FFFFFFF), FitBuilder.S32(0, 0x20000000),
            FitBuilder.S32(1, 0x40000000) };
        var unknown = Save(new FitBuilder().FileId().Define(0, 20, fields: relative).Message(0, relative)
            .Define(1, 20, fields: position).Compressed(1, 0, position));
        Assert.Empty(Elements((await fx.ConvertOkAsync(unknown, "gpx")).Single(), "time"));
        var absolute = relative.Select(field => field.Number == 253 ? FitBuilder.U32(253, First) : field).ToArray();
        var following = Save(new FitBuilder().FileId().Define(0, 20, fields: absolute).Message(0, absolute)
            .Message(0, relative).Define(1, 20, fields: position).Compressed(1, 0x1D, position));
        Assert.Equal([Stamp(First), null, Stamp(First + 2)],
            Elements((await fx.ConvertOkAsync(following, "gpx")).Single(), "trkpt").Select(p => Child(p, "time")));
    }

    [Fact]
    public async Task Existing_geo_inputs_do_not_gain_fit_times_or_sparse_height_rules()
    {
        const string mixed = """{"type":"LineString","coordinates":[[10,20,100],[11,21],[12,22,300]]}""";
        var input = GeoFixtures.WriteText(fx, "mixed.geojson", mixed);
        var job = await fx.ConvertAsync(input, "shp");
        Assert.NotEqual("notice.geo_heights_dropped", job.Files.Single().NoticeKey);
        Assert.Equal(13, BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(
            job.Outputs.Single(p => p.EndsWith(".shp", StringComparison.Ordinal))).AsSpan(32)));
        Assert.Contains("11,21,0", File.ReadAllText((await fx.ConvertOkAsync(input, "kml")).Single()));
        using (var doc = GeoFixtures.ReadJson((await fx.ConvertOkAsync(input, "geojson")).Single()))
            Assert.Equal(0, Feature(doc).GetProperty("geometry").GetProperty("coordinates")[1][2].GetDouble());
        const string gpx = """<gpx><trk><trkseg><trkpt lat="20" lon="10"><time>2026-10-24T00:00:00Z</time></trkpt><trkpt lat="21" lon="11"><time>2026-10-24T00:01:00Z</time></trkpt></trkseg></trk></gpx>""";
        var old = GeoFixtures.WriteText(fx, "old.gpx", gpx);
        Assert.Empty(Elements((await fx.ConvertOkAsync(old, "kml")).Single(), "Track"));
        using var result = GeoFixtures.ReadJson((await fx.ConvertOkAsync(old, "geojson")).Single());
        Assert.False(Feature(result).GetProperty("properties").TryGetProperty("coordTimes", out _));
        var rewritten = Path.Combine(fx.NewFolder(), "rewritten.gpx");
        GpxFormat.Write(GpxFormat.Read(old, CancellationToken.None), rewritten, CancellationToken.None);
        Assert.Empty(Elements(rewritten, "time"));
        var storedTimes = GeoFixtures.WriteText(fx, "stored.geojson",
            """{"type":"Feature","properties":{"coordTimes":["2026-10-24T00:00:00Z",null]},"geometry":{"type":"LineString","coordinates":[[10,20],[11,21]]}}""");
        using var preserved = GeoFixtures.ReadJson((await fx.ConvertOkAsync(storedTimes, "geojson")).Single());
        Assert.Equal("""["2026-10-24T00:00:00Z",null]""",
            Feature(preserved).GetProperty("properties").GetProperty("coordTimes").GetString());
        var kept = (await fx.ConvertOkAsync(storedTimes, "kml")).Single();
        Assert.Equal("""["2026-10-24T00:00:00Z",null]""", Assert.Single(Elements(kept, "Data"),
            data => (string?)data.Attribute("name") == "coordTimes").Value);
        Assert.Empty(Elements(kept, "Track"));
    }
}
