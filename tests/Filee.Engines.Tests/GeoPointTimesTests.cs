using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Filee.Engines.Geo;

namespace Filee.Engines.Tests;

public class GeoPointTimesTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly DateTime First = new(2026, 10, 24, 1, 2, 3, DateTimeKind.Utc);
    private static readonly DateTime Last = First.AddMinutes(2);
    private static readonly Coord[] AllHeights = [new(10, 20, 100), new(11, 21, 200), new(12, 22, 300)];
    private static readonly Coord[] PartialHeights = [AllHeights[0], new(11, 21), AllHeights[2]];

    private static GeoDataset Dataset(IReadOnlyList<Coord> points, IReadOnlyList<DateTime?>? times, bool sparseHeights = true)
    {
        var dataset = new GeoDataset { Name = "ride", SparseHeights = sparseHeights };
        dataset.Features.Add(new GeoFeature(new GeoLineString(points, times), [new("type", "running")]));
        return dataset;
    }

    private static void SamePoints(GeoDataset dataset, IReadOnlyList<Coord> points)
    {
        var line = Assert.IsType<GeoLineString>(Assert.Single(dataset.Features).Geometry);
        Assert.Equal(points.ToArray(), line.Points.ToArray());
    }

    [Fact]
    public void Mapping_a_line_preserves_the_time_list()
    {
        DateTime?[] times = [First, null, Last];
        var line = new GeoLineString(PartialHeights, times);
        var mapped = Assert.IsType<GeoLineString>(line.Map(p => p with { X = p.X + 1 }));
        Assert.Same(times, mapped.Times);
        Assert.Equal([11.0, 12.0, 13.0], mapped.Points.Select(p => p.X).ToArray());
        Assert.Null(new GeoLineString(PartialHeights).Times);
        Assert.False(new GeoDataset().SparseHeights);
    }

    [Theory]
    [InlineData("all", false)]
    [InlineData("partial", false)]
    [InlineData("none", false)]
    [InlineData("all", true)]
    [InlineData("partial", true)]
    [InlineData("none", true)]
    public void Writers_keep_times_and_sparse_heights_in_their_own_forms(string timeCase, bool partialHeight)
    {
        DateTime?[]? times = timeCase switch
        {
            "all" => [First, First.AddMinutes(1), Last],
            "partial" => [First, null, Last],
            _ => null,
        };
        var points = partialHeight ? PartialHeights : AllHeights;
        var dataset = Dataset(points, times);
        var dir = fx.NewFolder();
        var gpx = Path.Combine(dir, "ride.gpx");
        var kml = Path.Combine(dir, "ride.kml");
        var kmz = Path.Combine(dir, "ride.kmz");
        var geojson = Path.Combine(dir, "ride.geojson");
        Assert.False(GpxFormat.Write(dataset, gpx, CancellationToken.None));
        KmlFormat.Write(dataset, kml, zipped: false, CancellationToken.None);
        KmlFormat.Write(dataset, kmz, zipped: true, CancellationToken.None);
        GeoJsonFormat.Write(dataset, geojson, CancellationToken.None);

        var trackPoints = XDocument.Load(gpx).Descendants().Where(e => e.Name.LocalName == "trkpt").ToArray();
        Assert.Equal(points.Length, trackPoints.Length);
        for (var i = 0; i < points.Length; i++)
        {
            Assert.Equal(points[i].HasZ ? points[i].Z.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                trackPoints[i].Elements().FirstOrDefault(e => e.Name.LocalName == "ele")?.Value);
            Assert.Equal(times?[i]?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                trackPoints[i].Elements().FirstOrDefault(e => e.Name.LocalName == "time")?.Value);
            Assert.Equal(new[] { "ele", "time" }.Where((_, n) => n == 0 ? points[i].HasZ : times?[i] is not null),
                trackPoints[i].Elements().Select(e => e.Name.LocalName));
        }
        SamePoints(GpxFormat.Read(gpx, CancellationToken.None), points);

        using (var archive = ZipFile.OpenRead(kmz))
        using (var zipped = archive.GetEntry("doc.kml")!.Open())
        using (var copy = new MemoryStream())
        {
            zipped.CopyTo(copy);
            Assert.Equal(File.ReadAllBytes(kml), copy.ToArray());
        }
        var xml = XDocument.Load(kml);
        var isTrack = timeCase == "all" && !partialHeight;
        Assert.Equal(isTrack ? 1 : 0, xml.Descendants().Count(e => e.Name.LocalName == "Track"));
        var when = xml.Descendants().Where(e => e.Name.LocalName == "when").Select(e => e.Value).ToArray();
        var coords = xml.Descendants().Where(e => e.Name.LocalName == "coord").Select(e => e.Value).ToArray();
        if (isTrack)
        {
            Assert.Equal(times!.Select(t => t!.Value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")), when);
            Assert.Equal(["10 20 100", "11 21 200", "12 22 300"], coords);
        }
        else
        {
            Assert.Empty(when);
            Assert.Empty(coords);
            Assert.Equal(partialHeight ? "10,20,100 11,21 12,22,300" : "10,20,100 11,21,200 12,22,300",
                xml.Descendants().Single(e => e.Name.LocalName == "coordinates").Value);
        }
        var coordTimes = xml.Descendants().SingleOrDefault(e => e.Name.LocalName == "Data"
            && (string?)e.Attribute("name") == "coordTimes");
        Assert.Equal(times is not null && !isTrack, coordTimes is not null);
        if (coordTimes is not null)
            Assert.Equal(JsonSerializer.Serialize(times!.Select(t => t?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"))),
                coordTimes.Descendants().Single(e => e.Name.LocalName == "value").Value);
        SamePoints(KmlFormat.Read(kml, zipped: false, CancellationToken.None), points);
        SamePoints(KmlFormat.Read(kmz, zipped: true, CancellationToken.None), points);

        using var json = JsonDocument.Parse(File.ReadAllBytes(geojson));
        var feature = json.RootElement.GetProperty("features")[0];
        var properties = feature.GetProperty("properties");
        string[] propertyNames = times is null ? ["type"] : ["type", "coordTimes"];
        Assert.Equal(propertyNames, properties.EnumerateObject().Select(p => p.Name).ToArray());
        if (times is not null)
        {
            var actualTimes = properties.GetProperty("coordTimes");
            Assert.Equal(3, actualTimes.GetArrayLength());
            for (var i = 0; i < times.Length; i++)
                Assert.Equal(times[i]?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), actualTimes[i].ValueKind == JsonValueKind.Null
                    ? null : actualTimes[i].GetString());
        }
        var positions = feature.GetProperty("geometry").GetProperty("coordinates");
        for (var i = 0; i < points.Length; i++)
            Assert.Equal(points[i].HasZ ? 3 : 2, positions[i].GetArrayLength());
        SamePoints(GeoJsonFormat.Read(geojson, CancellationToken.None), points);
    }

    [Fact]
    public void Drop_heights_writes_2d_point_and_polyline_records()
    {
        var dir = fx.NewFolder();
        var dataset = Dataset(PartialHeights, [First, null, Last]);
        dataset.Features.Add(new GeoFeature(new GeoPoint(new Coord(14, 24, 40)), []));
        foreach (var group in ShapefileFormat.Plan(dataset))
        {
            var path = Path.Combine(dir, group.Kind + ".shp");
            ShapefileFormat.Write(group, path, CancellationToken.None, dropHeights: true);
            var bytes = File.ReadAllBytes(path);
            var expectedType = group.Kind == ShapeKind.Point ? 1 : 3;
            Assert.Equal(expectedType, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(32)));
            Assert.Equal(expectedType, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(108)));
            var restored = ShapefileFormat.Read(path, CancellationToken.None);
            var input = group.Records.Single().Geometry!.Coordinates().ToArray();
            var output = restored.Features.Single().Geometry!.Coordinates().ToArray();
            Assert.Equal(input.Select(p => p with { Z = double.NaN }).ToArray(), output);
        }
    }

    [Fact]
    public void Default_writer_behavior_keeps_zero_filled_heights_and_existing_coordTimes()
    {
        var dataset = Dataset(PartialHeights, null, sparseHeights: false);
        dataset.Features[0].Properties.Add(new("coordTimes", """["old"]"""));
        var dir = fx.NewFolder();
        var kml = Path.Combine(dir, "old.kml");
        var json = Path.Combine(dir, "old.geojson");
        var shp = Path.Combine(dir, "old.shp");
        KmlFormat.Write(dataset, kml, zipped: false, CancellationToken.None);
        GeoJsonFormat.Write(dataset, json, CancellationToken.None);
        ShapefileFormat.Write(ShapefileFormat.Plan(dataset).Single(), shp, CancellationToken.None);
        Assert.Contains("11,21,0", File.ReadAllText(kml));
        Assert.Contains("""[11,21,0]""", File.ReadAllText(json));
        using var doc = JsonDocument.Parse(File.ReadAllBytes(json));
        Assert.Equal("""["old"]""", doc.RootElement.GetProperty("features")[0]
            .GetProperty("properties").GetProperty("coordTimes").GetString());
        Assert.Equal(13, BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(shp).AsSpan(32)));
        var restored = Assert.IsType<GeoLineString>(ShapefileFormat.Read(shp, CancellationToken.None).Features.Single().Geometry);
        Assert.Equal(0, restored.Points[1].Z);
    }

    [Fact]
    public void Missing_heights_stay_2d_and_generated_times_replace_old_attribute()
    {
        var dataset = Dataset([new(10, 20), new(11, 21)], [First, null]);
        dataset.Features[0].Properties.Add(new("coordTimes", """["old"]"""));
        var dir = fx.NewFolder();
        var kml = Path.Combine(dir, "plain.kml");
        var json = Path.Combine(dir, "plain.geojson");
        KmlFormat.Write(dataset, kml, zipped: false, CancellationToken.None);
        GeoJsonFormat.Write(dataset, json, CancellationToken.None);
        var xml = XDocument.Load(kml);
        Assert.Equal("10,20 11,21", xml.Descendants().Single(e => e.Name.LocalName == "coordinates").Value);
        Assert.Equal("""["2026-10-24T01:02:03Z",null]""", xml.Descendants()
            .Single(e => e.Name.LocalName == "Data" && (string?)e.Attribute("name") == "coordTimes").Value);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(json));
        var feature = doc.RootElement.GetProperty("features")[0];
        Assert.Equal("""["2026-10-24T01:02:03Z",null]""",
            feature.GetProperty("properties").GetProperty("coordTimes").GetRawText());
        Assert.All(feature.GetProperty("geometry").GetProperty("coordinates").EnumerateArray(),
            position => Assert.Equal(2, position.GetArrayLength()));
        SamePoints(KmlFormat.Read(kml, zipped: false, CancellationToken.None),
            [new Coord(10, 20), new Coord(11, 21)]);
    }
}
