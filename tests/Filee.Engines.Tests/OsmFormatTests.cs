// OpenStreetMap XML (#713) through the geospatial engine: tagged nodes, ways and multipolygon relations in the shapes the
// openstreetmap.org export, JOSM and Overpass write them, clipped extracts, and files that must be refused (a DTD, the
// binary .osm.pbf, too many bytes or tags). The files are written by the tests.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Engines.Geo;

namespace Filee.Engines.Tests;

public class OsmFormatTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private string Osm(string body, string name = "map.osm") =>
        GeoFixtures.WriteText(fx, name, $"""<?xml version="1.0" encoding="UTF-8"?><osm version="0.6" generator="test">{body}</osm>""");

    private async Task<JsonElement[]> FeaturesAsync(string input)
    {
        using var doc = GeoFixtures.ReadJson((await fx.ConvertOkAsync(input, "geojson")).Single());
        return [.. doc.RootElement.GetProperty("features").EnumerateArray().Select(f => f.Clone())];
    }

    private static string Type(JsonElement feature) => feature.GetProperty("geometry").GetProperty("type").GetString()!;

    private static string? Text(JsonElement feature, string name) =>
        feature.GetProperty("properties").TryGetProperty(name, out var value) ? value.ToString() : null;

    private static JsonElement ById(JsonElement[] features, long id) =>
        features.Single(f => f.GetProperty("properties").GetProperty("osm_id").GetInt64() == id);

    // Four corners of a square, a fifth node off to the side and a tagged café.
    private const string Corners = """
        <bounds minlat="47.0" minlon="8.0" maxlat="47.1" maxlon="8.1"/>
        <node id="1" lat="47.0" lon="8.0" version="3" user="someone" uid="7" changeset="99" timestamp="2024-01-01T00:00:00Z"/>
        <node id="2" lat="47.0" lon="8.01"/>
        <node id="3" lat="47.01" lon="8.01"/>
        <node id="4" lat="47.01" lon="8.0"/>
        <node id="5" lat="47.02" lon="8.02"/>
        <node id="6" lat="47.005" lon="8.005" user="someone"><tag k="amenity" v="cafe"/><tag k="name" v="Café Zürich"/></node>
        """;

    [Fact]
    public async Task Tagged_nodes_ways_and_areas_become_points_lines_and_polygons_with_their_tags()
    {
        var input = Osm(Corners + """
            <way id="10"><nd ref="1"/><nd ref="2"/><nd ref="3"/><nd ref="4"/><nd ref="1"/><tag k="building" v="yes"/></way>
            <way id="11"><nd ref="1"/><nd ref="5"/><tag k="highway" v="residential"/><tag k="name" v="Bahnhofstrasse"/></way>
            <way id="12"><nd ref="1"/><nd ref="2"/><nd ref="3"/><nd ref="1"/><tag k="highway" v="primary"/><tag k="junction" v="roundabout"/></way>
            <way id="13"><nd ref="2"/><nd ref="3"/><nd ref="4"/><nd ref="2"/><tag k="natural" v="coastline"/></way>
            <way id="14"><nd ref="1"/><nd ref="3"/><nd ref="4"/><nd ref="1"/><tag k="highway" v="pedestrian"/><tag k="area" v="yes"/></way>
            <way id="15"><nd ref="1"/><nd ref="2"/><nd ref="4"/><nd ref="1"/><tag k="building" v="roof"/><tag k="area" v="no"/></way>
            <way id="16"><nd ref="3"/><nd ref="5"/></way>
            """);

        var features = await FeaturesAsync(input);

        // The café, then the ways in file order; the untagged way and the untagged nodes are no features.
        Assert.Equal([6L, 10, 11, 12, 13, 14, 15], features.Select(f => f.GetProperty("properties").GetProperty("osm_id").GetInt64()));
        var cafe = features[0];
        Assert.Equal("Point", Type(cafe));
        Assert.Equal([8.005, 47.005], GeoFixtures.Position(cafe.GetProperty("geometry")));
        Assert.Equal(["osm_type", "osm_id", "amenity", "name"], cafe.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Equal("node", Text(cafe, "osm_type"));
        Assert.Equal("Café Zürich", Text(cafe, "name"));
        Assert.Null(Text(cafe, "user"));

        Assert.Equal("Polygon", Type(ById(features, 10)));
        Assert.Equal("way", Text(ById(features, 10), "osm_type"));
        Assert.Equal(5, ById(features, 10).GetProperty("geometry").GetProperty("coordinates")[0].GetArrayLength());
        Assert.Equal("LineString", Type(ById(features, 11)));
        Assert.Equal("Bahnhofstrasse", Text(ById(features, 11), "name"));
        Assert.Equal("LineString", Type(ById(features, 12))); // a roundabout is a closed road, not an area
        Assert.Equal("LineString", Type(ById(features, 13))); // a coastline round an island stays a line
        Assert.Equal("Polygon", Type(ById(features, 14))); // area=yes
        Assert.Equal("LineString", Type(ById(features, 15))); // area=no wins over building
    }

    [Fact]
    public async Task Ways_whose_nodes_are_not_in_a_clipped_extract_are_left_out_with_a_notice()
    {
        var input = Osm(Corners + """
            <way id="20"><nd ref="1"/><nd ref="2"/><tag k="highway" v="service"/></way>
            <way id="21"><nd ref="2"/><nd ref="999"/><tag k="highway" v="service"/></way>
            """);

        var job = await fx.ConvertAsync(input, "geojson");

        Assert.True(job.State == JobState.Completed, GeoFixtures.Describe(job));
        Assert.Equal("notice.osm_incomplete", job.Files.Single().NoticeKey);
        using var doc = GeoFixtures.ReadJson(job.Outputs.Single());
        var ids = doc.RootElement.GetProperty("features").EnumerateArray().Select(f => f.GetProperty("properties").GetProperty("osm_id").GetInt64());
        Assert.Equal([6L, 20], ids);
    }

    [Fact]
    public async Task A_complete_extract_has_no_notice()
    {
        var job = await fx.ConvertAsync(Osm(Corners), "geojson");
        Assert.Equal(JobState.Completed, job.State);
        Assert.Null(job.Files.Single().NoticeKey);
    }

    // A lake whose shore is two ways (the second drawn the other way round) with an island in it.
    private const string Lake = """
        <node id="1" lat="0" lon="0"/><node id="2" lat="0" lon="4"/><node id="3" lat="4" lon="4"/><node id="4" lat="4" lon="0"/>
        <node id="5" lat="1" lon="1"/><node id="6" lat="1" lon="2"/><node id="7" lat="2" lon="2"/><node id="8" lat="2" lon="1"/>
        <way id="30"><nd ref="1"/><nd ref="2"/><nd ref="3"/></way>
        <way id="31"><nd ref="1"/><nd ref="4"/><nd ref="3"/></way>
        <way id="32"><nd ref="5"/><nd ref="6"/><nd ref="7"/><nd ref="8"/><nd ref="5"/></way>
        """;

    [Fact]
    public async Task A_multipolygon_relation_joins_its_outer_ways_into_a_ring_and_keeps_its_hole()
    {
        var input = Osm(Lake + """
            <relation id="40">
              <member type="way" ref="30" role="outer"/><member type="way" ref="32" role="inner"/><member type="way" ref="31" role="outer"/>
              <member type="node" ref="1" role="label"/>
              <tag k="type" v="multipolygon"/><tag k="natural" v="water"/><tag k="name" v="Lac"/>
            </relation>
            """);

        var lake = Assert.Single(await FeaturesAsync(input));

        Assert.Equal("Polygon", Type(lake));
        Assert.Equal("relation", Text(lake, "osm_type"));
        Assert.Equal("Lac", Text(lake, "name"));
        var rings = lake.GetProperty("geometry").GetProperty("coordinates");
        Assert.Equal(2, rings.GetArrayLength());
        Assert.Equal(5, rings[0].GetArrayLength());
        Assert.Equal(5, rings[1].GetArrayLength());
        Assert.Equal(4.0, rings[0].EnumerateArray().Max(p => p[0].GetDouble()));
        Assert.Equal(2.0, rings[1].EnumerateArray().Max(p => p[0].GetDouble()));
    }

    [Fact]
    public async Task Separate_outer_rings_make_a_multipolygon_and_a_boundary_relation_is_an_area_too()
    {
        var input = Osm(Lake + """
            <relation id="41">
              <member type="way" ref="30" role="outer"/><member type="way" ref="31" role="outer"/><member type="way" ref="32" role=""/>
              <tag k="type" v="boundary"/><tag k="boundary" v="administrative"/>
            </relation>
            <relation id="42"><member type="way" ref="30" role=""/><tag k="type" v="route"/><tag k="route" v="bus"/></relation>
            """);

        var area = Assert.Single(await FeaturesAsync(input)); // the bus route is no area and is left out

        Assert.Equal("MultiPolygon", Type(area));
        Assert.Equal(2, area.GetProperty("geometry").GetProperty("coordinates").GetArrayLength());
    }

    [Theory]
    [InlineData("""<member type="way" ref="30" role="outer"/><member type="way" ref="31" role="outer"/><member type="way" ref="77" role="inner"/>""")]
    [InlineData("""<member type="way" ref="30" role="outer"/>""")]
    public async Task A_relation_with_a_member_way_missing_or_a_ring_left_open_is_left_out_with_a_notice(string members)
    {
        var input = Osm(Lake + $"""
            <node id="9" lat="3" lon="3"><tag k="name" v="Pier"/></node>
            <relation id="43">{members}<tag k="type" v="multipolygon"/><tag k="landuse" v="forest"/></relation>
            """);

        var job = await fx.ConvertAsync(input, "geojson");

        Assert.True(job.State == JobState.Completed, GeoFixtures.Describe(job));
        Assert.Equal("notice.osm_incomplete", job.Files.Single().NoticeKey);
        using var doc = GeoFixtures.ReadJson(job.Outputs.Single());
        Assert.Equal("Pier", doc.RootElement.GetProperty("features").EnumerateArray().Single().GetProperty("properties").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Josm_deleted_objects_and_old_history_versions_are_skipped()
    {
        var input = Osm(Corners + """
            <node id="7" action="delete" lat="47.0" lon="8.0"><tag k="shop" v="bakery"/></node>
            <node id="8" visible="false"><tag k="shop" v="florist"/></node>
            <node id="-1" action="modify" lat="47.0" lon="8.0"><tag k="shop" v="butcher"/></node>
            <way id="50" action="delete"><nd ref="1"/><nd ref="2"/><tag k="highway" v="track"/></way>
            """);

        var features = await FeaturesAsync(input);

        Assert.Equal(["cafe", null], features.Select(f => Text(f, "amenity")));
        Assert.Equal("butcher", Text(ById(features, -1), "shop"));
    }

    [Fact]
    public async Task Overpass_out_geom_positions_on_the_way_nodes_are_used()
    {
        var input = Osm("""
            <way id="60"><nd ref="1" lat="50.0" lon="10.0"/><nd ref="2" lat="50.1" lon="10.2"/><tag k="waterway" v="stream"/></way>
            """);

        var stream = Assert.Single(await FeaturesAsync(input));

        Assert.Equal("LineString", Type(stream));
        Assert.Equal(10.2, stream.GetProperty("geometry").GetProperty("coordinates")[1][0].GetDouble());
    }

    [Fact]
    public async Task Osm_converts_to_kml_kmz_shapefile_and_csv()
    {
        var input = Osm(Corners + """
            <way id="10"><nd ref="1"/><nd ref="2"/><nd ref="3"/><nd ref="4"/><nd ref="1"/><tag k="building" v="yes"/></way>
            <way id="11"><nd ref="1"/><nd ref="5"/><tag k="highway" v="residential"/></way>
            """);

        var kml = XDocument.Load((await fx.ConvertOkAsync(input, "kml")).Single());
        var placemarks = kml.Descendants().Where(e => e.Name.LocalName == "Placemark").ToList();
        Assert.Equal(3, placemarks.Count);
        Assert.Equal("Café Zürich", placemarks[0].Elements().Single(e => e.Name.LocalName == "name").Value);

        Assert.Single(await fx.ConvertOkAsync(input, "kmz"));

        var shapefiles = await fx.ConvertOkAsync(input, "shp");
        Assert.Equal(3, shapefiles.Count(p => p.EndsWith(".shp", StringComparison.Ordinal))); // points, lines, polygons

        var csv = File.ReadAllLines((await fx.ConvertOkAsync(input, "csv")).Single());
        Assert.StartsWith("wkt,osm_type,osm_id", csv[0]);
        Assert.Equal(4, csv.Length);
    }

    [Fact]
    public async Task A_file_with_only_untagged_data_has_no_features()
    {
        var (key, _) = await fx.ConvertFailsAsync(Osm("""<node id="1" lat="1" lon="2"/><way id="2"><nd ref="1"/></way>"""), "geojson");
        Assert.Equal("error.geo_empty", key);
    }

    // ---- Files that are refused ----

    [Fact]
    public async Task A_dtd_with_an_external_entity_is_refused_without_reading_it()
    {
        var secret = GeoFixtures.WriteText(fx, "secret.txt", "do not read");
        var xml = $"""<?xml version="1.0"?><!DOCTYPE osm [<!ENTITY x SYSTEM "{new Uri(secret).AbsoluteUri}">]><osm><node id="1" lat="1" lon="2"><tag k="name" v="&x;"/></node></osm>""";

        var (key, detail) = await fx.ConvertFailsAsync(GeoFixtures.WriteText(fx, "dtd.osm", xml), "geojson");

        Assert.Equal("error.conversion_failed", key);
        Assert.Contains("DTD", detail);
    }

    [Fact]
    public async Task Other_xml_and_the_binary_pbf_are_refused_with_what_they_are()
    {
        var (_, notOsm) = await fx.ConvertFailsAsync(GeoFixtures.WriteText(fx, "change.osm", "<osmChange><create/></osmChange>"), "geojson");
        Assert.Contains("not OpenStreetMap", notOsm);

        // A .osm.pbf starts with the length of its first block header and the block type "OSMHeader".
        var pbf = Path.Combine(fx.NewFolder(), "extract.osm");
        File.WriteAllBytes(pbf, [0, 0, 0, 14, 0x0A, 9, .. "OSMHeader"u8, 0x18, 0x7C, 0, 0]);
        var (_, binary) = await fx.ConvertFailsAsync(pbf, "geojson");
        Assert.Contains("PBF", binary);
    }

    [Fact]
    public async Task A_node_outside_the_earth_is_refused()
    {
        var (key, detail) = await fx.ConvertFailsAsync(Osm("""<node id="1" lat="91" lon="0"><tag k="name" v="x"/></node>"""), "geojson");
        Assert.Equal("error.conversion_failed", key);
        Assert.Contains("outside the Earth", detail);
    }

    [Fact]
    public void Configurable_byte_and_tag_limits_refuse_only_excess()
    {
        var path = Osm("""<node id="1" lat="1" lon="2"><tag k="a" v="1"/><tag k="b" v="2"/><tag k="a" v="3"/></node><node id="2" lat="1" lon="2"><tag k="c" v="4"/></node>""");
        var length = new FileInfo(path).Length;

        Assert.Equal(2, OsmFormat.Read(path, CancellationToken.None, maxBytes: length, maxTags: 3).Features.Count);
        var bytes = Assert.Throws<ConversionRefusedException>(() => OsmFormat.Read(path, CancellationToken.None, maxBytes: length - 1));
        Assert.Equal(("error.osm_too_large", "256 MB"), (bytes.ErrorKey, bytes.Detail));
        // A repeated key counts once: it is kept once.
        var tags = Assert.Throws<ConversionRefusedException>(() => OsmFormat.Read(path, CancellationToken.None, maxTags: 2));
        Assert.Equal(("error.osm_too_large", "8000000 tags"), (tags.ErrorKey, tags.Detail));
    }

    [Fact]
    public void A_relation_of_thousands_of_shuffled_ways_joins_in_linear_time()
    {
        // One ring of 20,000 nodes cut into 10,000 ways of two segments, listed in random order and half of them reversed.
        const int ways = 10_000;
        var text = new StringBuilder("""<?xml version="1.0"?><osm>""");
        for (var i = 0; i < 2 * ways; i++)
        {
            var angle = 2 * Math.PI * i / (2 * ways);
            text.Append(CultureInfo.InvariantCulture, $"""<node id="{i + 1}" lat="{Math.Sin(angle):F7}" lon="{Math.Cos(angle):F7}"/>""");
        }
        var random = new Random(713);
        var order = Enumerable.Range(0, ways).OrderBy(_ => random.Next()).ToArray();
        for (var w = 0; w < ways; w++)
        {
            long[] nodes = [2 * w + 1, 2 * w + 2, (2 * w + 2) % (2 * ways) + 1];
            if (w % 2 == 1)
                Array.Reverse(nodes);
            text.Append($"""<way id="{w + 1}">{string.Concat(nodes.Select(n => $"<nd ref=\"{n}\"/>"))}</way>""");
        }
        text.Append("""<relation id="1">""");
        foreach (var w in order)
            text.Append($"""<member type="way" ref="{w + 1}" role="outer"/>""");
        text.Append("""<tag k="type" v="multipolygon"/><tag k="landuse" v="meadow"/></relation></osm>""");
        var path = GeoFixtures.WriteText(fx, "ring.osm", text.ToString());

        var clock = Stopwatch.StartNew();
        var dataset = OsmFormat.Read(path, CancellationToken.None);
        clock.Stop();

        var polygon = Assert.IsType<GeoPolygon>(Assert.Single(dataset.Features).Geometry);
        Assert.Equal(2 * ways + 1, Assert.Single(polygon.Rings).Count);
        Assert.False(dataset.PartsMissing);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"{clock.Elapsed} to join {ways} ways");
    }
}
