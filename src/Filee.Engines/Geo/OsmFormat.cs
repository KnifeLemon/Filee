// OpenStreetMap XML (.osm) in, read only. Tagged nodes become Points, tagged ways LineStrings, or Polygons when they
// are closed and tagged as an area, and multipolygon and boundary relations Polygons or MultiPolygons with their holes.
// Every feature carries "osm_type" (node, way or relation), "osm_id" and its tags as text. Untagged nodes and ways are
// only the corners of other features. Editing data (user, version, changeset, timestamp) is left out. A way or a
// relation whose nodes or member ways aren't in the file (a clipped extract) is left out and the dataset says so. The
// file is streamed: only node positions, way node lists and the kept tags are held in memory. OSM is WGS 84.

using System.Globalization;
using System.Xml;
using Filee.Core.Conversion;

namespace Filee.Engines.Geo;

internal static class OsmFormat
{
    /// <summary>The largest .osm read: a city extract is tens of MB, a country needs the binary .osm.pbf anyway.</summary>
    internal const long MaxFileBytes = 256L << 20;

    /// <summary>How many tags a file may hold. Each kept tag costs about 60 bytes, so this bounds memory near 500 MB.</summary>
    internal const int MaxTags = 8_000_000;

    /// <summary>OSM stores positions as whole numbers of 1e-7 degrees, so two of them fit in one long without loss.</summary>
    private const double Scale = 1e7;

    // Keys whose closed ways are areas whatever the value, after the area rules the OpenStreetMap editors use; the values
    // listed with a key stay lines (a coastline or a cliff drawn round an island is still a line).
    private static readonly Dictionary<string, string[]> AreaKeys = new(StringComparer.Ordinal)
    {
        ["building"] = [],
        ["building:part"] = [],
        ["landuse"] = [],
        ["amenity"] = [],
        ["shop"] = [],
        ["office"] = [],
        ["craft"] = [],
        ["tourism"] = [],
        ["historic"] = [],
        ["place"] = [],
        ["military"] = [],
        ["water"] = [],
        ["wetland"] = [],
        ["landcover"] = [],
        ["area:highway"] = [],
        ["leisure"] = ["slipway", "track"],
        ["natural"] = ["coastline", "cliff", "ridge", "arete", "tree_row", "valley", "gorge", "earth_bank"],
        ["man_made"] = ["pipeline", "embankment", "breakwater", "groyne", "cutline", "dyke", "goods_conveyor"],
        ["aeroway"] = ["runway", "taxiway", "jet_bridge", "parking_position", "holding_position"],
    };

    // Keys whose closed ways are areas only with these values: a closed highway is a roundabout, a closed waterway a
    // canal loop.
    private static readonly Dictionary<string, string[]> AreaValues = new(StringComparer.Ordinal)
    {
        ["waterway"] = ["riverbank", "dock", "boatyard"],
        ["power"] = ["plant", "substation", "generator"],
        ["railway"] = ["platform", "station"],
        ["highway"] = ["rest_area", "services", "platform"],
        ["public_transport"] = ["platform", "station"],
    };

    private sealed record Way(long Id, long[] Nodes, List<KeyValuePair<string, object?>> Tags);

    private readonly record struct Member(string Type, long Ref, string Role);

    private sealed record Relation(long Id, List<Member> Members, List<KeyValuePair<string, object?>> Tags);

    public static GeoDataset Read(string path, CancellationToken cancellationToken, long maxBytes = MaxFileBytes, int maxTags = MaxTags)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > maxBytes)
            throw new ConversionRefusedException("error.osm_too_large", MaxFileBytes / (1 << 20) + " MB");
        if (IsPbf(stream))
            throw new InvalidDataException("This is an OpenStreetMap PBF file (the binary download); only the XML .osm is supported.");

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            CheckCharacters = false,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            IgnoreProcessingInstructions = true,
        };
        var dataset = new GeoDataset { Name = Path.GetFileNameWithoutExtension(path) };
        var nodes = new Dictionary<long, long>();
        var ways = new List<Way>();
        var relations = new List<Relation>();
        var children = new ElementReader(maxTags);
        try
        {
            using var xml = XmlReader.Create(stream, settings);
            xml.MoveToContent();
            if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "osm")
                throw new InvalidDataException("The file is XML but not OpenStreetMap: the root element is not <osm>.");
            if (xml.IsEmptyElement)
                return dataset;
            xml.Read();
            var count = 0;
            while (!xml.EOF && !(xml.NodeType == XmlNodeType.EndElement && xml.Depth == 0))
            {
                if (xml.NodeType != XmlNodeType.Element)
                {
                    xml.Read();
                    continue;
                }
                if (++count % 4096 == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var kind = xml.LocalName;
                if (kind is not ("node" or "way" or "relation"))
                {
                    xml.Skip();
                    continue;
                }
                var id = Id(xml.GetAttribute("id"), kind);
                // JOSM keeps deleted objects in the file until they are uploaded; history files keep old versions.
                var gone = xml.GetAttribute("action") == "delete" || xml.GetAttribute("visible") == "false";
                var position = gone || kind != "node" ? 0 : Position(xml.GetAttribute("lat"), xml.GetAttribute("lon"), "node");
                children.Read(xml, nodes);
                if (gone)
                    continue;
                switch (kind)
                {
                    case "node":
                        nodes[id] = position;
                        if (children.Tags.Count > 0)
                            dataset.Features.Add(new GeoFeature(new GeoPoint(ToCoord(position)), Properties("node", id, children.Tags)));
                        break;
                    case "way":
                        ways.Add(new Way(id, [.. children.Nodes], [.. children.Tags]));
                        break;
                    default:
                        relations.Add(new Relation(id, [.. children.Members], [.. children.Tags]));
                        break;
                }
            }
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"The file is not valid XML ({ex.Message})", ex);
        }

        var missing = false;
        foreach (var way in ways)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (way.Tags.Count == 0)
                continue;
            if (Line(way.Nodes, nodes) is not { } line)
            {
                missing = true;
                continue;
            }
            if (line.Count < 2)
                continue;
            GeoGeometry geometry = way.Nodes.Length >= 4 && way.Nodes[0] == way.Nodes[^1] && IsArea(way.Tags)
                && GeoRings.Close(line) is { } ring ? new GeoPolygon([ring]) : new GeoLineString(line);
            dataset.Features.Add(new GeoFeature(geometry, Properties("way", way.Id, way.Tags)));
        }

        if (relations.Count > 0)
        {
            var wayIndex = new Dictionary<long, int>(ways.Count);
            for (var i = 0; i < ways.Count; i++)
                wayIndex[ways[i].Id] = i;
            foreach (var relation in relations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Find(relation.Tags, "type") is not ("multipolygon" or "boundary"))
                    continue;
                if (Area(relation, ways, wayIndex, nodes, cancellationToken, ref missing) is { } geometry)
                    dataset.Features.Add(new GeoFeature(geometry, Properties("relation", relation.Id, relation.Tags)));
            }
        }
        dataset.PartsMissing = missing;
        return dataset;
    }

    /// <summary>True if the file starts like an .osm.pbf: a big-endian header length and the "OSMHeader" block type.</summary>
    private static bool IsPbf(Stream stream)
    {
        Span<byte> head = stackalloc byte[16];
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        stream.Position = 0;
        return read >= 15 && head[0] == 0 && head[4] == 0x0A && head[5] == 9 && head[6..15].SequenceEqual("OSMHeader"u8);
    }

    /// <summary>
    /// The polygons of a multipolygon or boundary relation: its outer and inner member ways joined end to end into
    /// rings, each hole put into the outer ring around it. When a member way or one of its nodes isn't in the file, or
    /// the ways don't close into rings, the relation gives nothing and <paramref name="missing"/> is set: part of an area
    /// would draw the wrong shape. A relation without outer ways (only label nodes, say) gives nothing.
    /// </summary>
    private static GeoGeometry? Area(Relation relation, List<Way> ways, Dictionary<long, int> wayIndex, Dictionary<long, long> nodes,
        CancellationToken cancellationToken, ref bool missing)
    {
        var outer = new List<long[]>();
        var inner = new List<long[]>();
        foreach (var member in relation.Members)
        {
            if (member.Type != "way" || member.Role is not ("outer" or "inner" or ""))
                continue;
            if (!wayIndex.TryGetValue(member.Ref, out var index))
            {
                missing = true;
                return null;
            }
            (member.Role == "inner" ? inner : outer).Add(ways[index].Nodes);
        }
        if (outer.Count == 0)
            return null;
        if (Join(outer) is not { } outerRings || Join(inner) is not { } innerRings)
        {
            missing = true;
            return null;
        }
        // Outer rings clockwise and holes counter-clockwise, as the Shapefile reader expects when it sorts holes into
        // the rings around them.
        var rings = new List<List<Coord>>(outerRings.Count + innerRings.Count);
        foreach (var (ids, isOuter) in outerRings.Select(r => (r, true)).Concat(innerRings.Select(r => (r, false))))
        {
            if (Line(ids, nodes) is not { } line)
            {
                missing = true;
                return null;
            }
            rings.Add([.. GeoRings.Oriented(line, counterClockwise: !isOuter)]);
        }
        return ShapefileFormat.Polygons(rings, cancellationToken);
    }

    /// <summary>
    /// Joins ways that share end nodes into closed rings of node ids, turning a way round where it runs the other way;
    /// null if a ring can't be closed. Ways are found by their end nodes, so a relation of thousands of ways joins in
    /// linear time.
    /// </summary>
    private static List<List<long>>? Join(List<long[]> ways)
    {
        var rings = new List<List<long>>();
        var used = new bool[ways.Count];
        var byEnd = new Dictionary<long, List<int>>();
        for (var i = 0; i < ways.Count; i++)
        {
            if (ways[i].Length < 2)
            {
                used[i] = true;
                continue;
            }
            Add(byEnd, ways[i][0], i);
            Add(byEnd, ways[i][^1], i);
        }
        for (var i = 0; i < ways.Count; i++)
        {
            if (used[i])
                continue;
            used[i] = true;
            var ring = new List<long>(ways[i]);
            while (ring[0] != ring[^1])
            {
                var end = ring[^1];
                var next = Take(byEnd, end, used);
                if (next < 0)
                    return null;
                used[next] = true;
                var way = ways[next];
                if (way[0] == end)
                    ring.AddRange(way.AsSpan(1));
                else
                {
                    for (var n = way.Length - 2; n >= 0; n--)
                        ring.Add(way[n]);
                }
            }
            rings.Add(ring);
        }
        return rings;

        static void Add(Dictionary<long, List<int>> byEnd, long node, int way)
        {
            if (!byEnd.TryGetValue(node, out var list))
                byEnd[node] = list = [];
            list.Add(way);
        }

        static int Take(Dictionary<long, List<int>> byEnd, long node, bool[] used)
        {
            if (!byEnd.TryGetValue(node, out var list))
                return -1;
            while (list.Count > 0)
            {
                var way = list[^1];
                list.RemoveAt(list.Count - 1);
                if (!used[way])
                    return way;
            }
            return -1;
        }
    }

    /// <summary>The positions of <paramref name="ids"/>, or null if one isn't in the file.</summary>
    private static List<Coord>? Line(IReadOnlyList<long> ids, Dictionary<long, long> nodes)
    {
        var line = new List<Coord>(ids.Count);
        foreach (var id in ids)
        {
            if (!nodes.TryGetValue(id, out var position))
                return null;
            line.Add(ToCoord(position));
        }
        return line;
    }

    private static bool IsArea(List<KeyValuePair<string, object?>> tags)
    {
        switch (Find(tags, "area"))
        {
            case "yes":
                return true;
            case "no":
                return false;
        }
        foreach (var (key, value) in tags)
        {
            if (value is not string text || text == "no")
                continue;
            if (AreaKeys.TryGetValue(key, out var lines) && !lines.Contains(text))
                return true;
            if (AreaValues.TryGetValue(key, out var areas) && areas.Contains(text))
                return true;
        }
        return false;
    }

    private static string? Find(List<KeyValuePair<string, object?>> tags, string key)
    {
        foreach (var (k, v) in tags)
        {
            if (k == key)
                return v as string;
        }
        return null;
    }

    private static List<KeyValuePair<string, object?>> Properties(string type, long id, List<KeyValuePair<string, object?>> tags)
    {
        var properties = new List<KeyValuePair<string, object?>>(tags.Count + 2) { new("osm_type", type), new("osm_id", id) };
        properties.AddRange(tags);
        return properties;
    }

    private static long Id(string? text, string kind) =>
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id)
            ? id : throw new InvalidDataException($"An OpenStreetMap <{kind}> has no valid id.");

    /// <summary>A position packed as latitude and longitude in 1e-7 degrees, or an error naming <paramref name="kind"/>.</summary>
    private static long Position(string? latText, string? lonText, string kind)
    {
        if (!GeoXml.TryNumber(latText ?? "", out var lat) || !GeoXml.TryNumber(lonText ?? "", out var lon))
            throw new InvalidDataException($"An OpenStreetMap <{kind}> has no valid lat and lon.");
        if (lat is < -90 or > 90 || lon is < -180 or > 180)
            throw new InvalidDataException($"An OpenStreetMap <{kind}> is outside the Earth (lat {lat}, lon {lon}).");
        return (long)(int)Math.Round(lat * Scale) << 32 | (uint)(int)Math.Round(lon * Scale);
    }

    private static Coord ToCoord(long position) => new((int)position / Scale, (int)(position >> 32) / Scale);

    /// <summary>
    /// Reads the children of one node, way or relation (tags, node references, members) into lists that are reused for
    /// the next one, and counts the tags against the file's limit. Repeated tag keys and values share one string.
    /// </summary>
    private sealed class ElementReader(int maxTags)
    {
        private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
        private int _tags;

        public List<KeyValuePair<string, object?>> Tags { get; } = [];
        public List<long> Nodes { get; } = [];
        public List<Member> Members { get; } = [];

        public void Read(XmlReader xml, Dictionary<long, long> nodes)
        {
            Tags.Clear();
            Nodes.Clear();
            Members.Clear();
            _keys.Clear();
            if (xml.IsEmptyElement)
            {
                xml.Read();
                return;
            }
            var depth = xml.Depth;
            xml.Read();
            while (!xml.EOF && !(xml.NodeType == XmlNodeType.EndElement && xml.Depth == depth))
            {
                if (xml.NodeType != XmlNodeType.Element || xml.Depth != depth + 1)
                {
                    xml.Read();
                    continue;
                }
                switch (xml.LocalName)
                {
                    case "tag":
                        if (xml.GetAttribute("k") is { Length: > 0 } key && _keys.Add(key))
                        {
                            if (++_tags > maxTags)
                                throw new ConversionRefusedException("error.osm_too_large", MaxTags.ToString(CultureInfo.InvariantCulture) + " tags");
                            Tags.Add(new(Shared(key), Shared(xml.GetAttribute("v") ?? "")));
                        }
                        break;
                    case "nd":
                        var node = Id(xml.GetAttribute("ref"), "nd");
                        Nodes.Add(node);
                        // Overpass "out geom" puts each node's position on the way instead of in a <node> of its own.
                        if (xml.GetAttribute("lat") is { } lat && xml.GetAttribute("lon") is { } lon)
                            nodes.TryAdd(node, Position(lat, lon, "nd"));
                        break;
                    case "member":
                        Members.Add(new Member(xml.GetAttribute("type") ?? "", Id(xml.GetAttribute("ref"), "member"), xml.GetAttribute("role") ?? ""));
                        break;
                }
                xml.Skip();
            }
            xml.Read();
        }

        private string Shared(string text)
        {
            if (_strings.TryGetValue(text, out var shared))
                return shared;
            // Names and addresses rarely repeat: only short texts are kept for sharing, so the table stays small.
            if (text.Length <= 32 && _strings.Count < 1 << 20)
                _strings[text] = text;
            return text;
        }
    }
}
