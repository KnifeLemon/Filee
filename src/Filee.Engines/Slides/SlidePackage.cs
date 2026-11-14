// The parts of an Office Open XML package as the slide reader reads them: XML parts through DocumentPackage (sizes,
// inflate ratio, XML depth and element counts limited), relationships resolved to part names, binary parts (pictures)
// up to the part limit. Damaged XML is invalid data naming the part. A PowerPoint 97–2003 deck (a compound file) is
// opened as the PPTX package it translates to (Office/Ppt), in memory.

using System.Xml;
using System.Xml.Linq;
using Filee.Engines.Documents;
using Filee.Engines.Office.Ppt;

namespace Filee.Engines.Slides;

/// <summary>A relationship of a part; <see cref="Target"/> is a part name unless it is external.</summary>
internal readonly record struct PartRelationship(string Type, string Target, bool External);

internal sealed class SlidePackage : IDisposable
{
    private static readonly XNamespace PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace OfficeRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private readonly DocumentPackage _package;
    private readonly Dictionary<string, XElement?> _parts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, PartRelationship>> _relationships = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]?> _binaries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _diagramDrawings = new(StringComparer.OrdinalIgnoreCase);

    private SlidePackage(DocumentPackage package) => _package = package;

    public static SlidePackage Open(string path, DocumentLimits? limits = null)
    {
        static Exception NotAPackage() => new InvalidDataException("The file is not a PowerPoint presentation (not a ZIP package). It may be damaged.");
        if (!CompoundFile.HasSignature(path))
            return new(DocumentPackage.Open(path, limits, NotAPackage));
        var package = PptReader.ToPptx(path, limits ?? DocumentLimits.Default, out var dropped);
        return new(DocumentPackage.Open(package, limits, NotAPackage)) { Dropped = dropped };
    }

    /// <summary>For a PowerPoint 97–2003 deck: what its translation left out that would show on a slide.</summary>
    public string? Dropped { get; private init; }

    public DocumentLimits Limits => _package.Limits;

    public void Dispose() => _package.Dispose();

    /// <summary>The part named by the package's officeDocument relationship.</summary>
    public string? MainPart => Relationships("").Values.FirstOrDefault(r => r.Type.EndsWith("/officeDocument", StringComparison.Ordinal)).Target;

    /// <summary>The root element of an XML part; null when there is no such part.</summary>
    public XElement? Part(string? name)
    {
        if (name is null)
            return null;
        if (_parts.TryGetValue(name, out var cached))
            return cached;
        XElement? root = null;
        if (_package.GetEntry(name) is { } entry)
        {
            try
            {
                root = _package.LoadXml(entry).Root;
            }
            catch (XmlException ex)
            {
                throw new InvalidDataException($"The part {name} of the presentation is damaged ({ex.Message}).", ex);
            }
        }
        _parts[name] = root;
        return root;
    }

    /// <summary>The bytes of a binary part (a picture); null when it is missing or larger than the part limit.</summary>
    public byte[]? Bytes(string? name)
    {
        if (name is null)
            return null;
        if (_binaries.TryGetValue(name, out var cached))
            return cached;
        var bytes = _package.GetEntry(name) is { } entry ? _package.ReadBytes(entry, _package.Limits.MaxPartBytes) : null;
        _binaries[name] = bytes;
        return bytes;
    }

    /// <summary>The relationships of a part ("" = the package's own), by id.</summary>
    public Dictionary<string, PartRelationship> Relationships(string part)
    {
        if (_relationships.TryGetValue(part, out var cached))
            return cached;
        var folder = part.Contains('/') ? part[..part.LastIndexOf('/')] : "";
        var name = part.Length == 0 ? "_rels/.rels" : $"{folder}/_rels/{part[(part.LastIndexOf('/') + 1)..]}.rels".TrimStart('/');
        var result = new Dictionary<string, PartRelationship>(StringComparer.Ordinal);
        foreach (var relationship in Part(name)?.Elements(PackageRelationships + "Relationship") ?? [])
        {
            var target = (string?)relationship.Attribute("Target") ?? "";
            var external = (string?)relationship.Attribute("TargetMode") == "External";
            result[(string?)relationship.Attribute("Id") ?? ""] = new PartRelationship((string?)relationship.Attribute("Type") ?? "",
                external ? target : Resolve(folder, target), external);
        }
        _relationships[part] = result;
        return result;
    }

    /// <summary>The part relationship <paramref name="id"/> of <paramref name="part"/> points to; null for none or an external one.</summary>
    public string? Target(string part, string? id) =>
        id is not null && Relationships(part).TryGetValue(id, out var relationship) && !relationship.External ? relationship.Target : null;

    /// <summary>The external target (a web address) of relationship <paramref name="id"/>.</summary>
    public string? ExternalTarget(string part, string? id) =>
        id is not null && Relationships(part).TryGetValue(id, out var relationship) && relationship.External ? relationship.Target : null;

    /// <summary>The first target of a relationship type, matched on the end of the type (e.g. "/slideLayout").</summary>
    public string? TargetOfType(string part, string typeSuffix) =>
        Relationships(part).Values.FirstOrDefault(r => !r.External && r.Type.EndsWith(typeSuffix, StringComparison.Ordinal)).Target;

    /// <summary>
    /// The drawing PowerPoint stores with a SmartArt frame of <paramref name="part"/> (a:graphicData): the data part
    /// (relIds r:dm) names it in its dsp:dataModelExt, by a relationship of <paramref name="part"/>. Null when there is none.
    /// </summary>
    public string? DiagramDrawing(string part, XElement graphicData)
    {
        var relIds = graphicData.Descendants().FirstOrDefault(e => e.Name.LocalName == "relIds");
        var dataPart = Target(part, (string?)relIds?.Attribute(OfficeRelationships + "dm"));
        if (dataPart is null)
            return null;
        // Many frames can share one data part: it is searched once.
        if (!_diagramDrawings.TryGetValue(dataPart, out var drawingId))
        {
            drawingId = (string?)Part(dataPart)?.Descendants().FirstOrDefault(e => e.Name.LocalName == "dataModelExt")?.Attribute("relId");
            _diagramDrawings[dataPart] = drawingId;
        }
        return Target(part, drawingId);
    }

    private static string Resolve(string folder, string target)
    {
        if (target.StartsWith('/'))
            return target.TrimStart('/');
        var parts = new List<string>(folder.Length == 0 ? [] : folder.Split('/'));
        foreach (var segment in Uri.UnescapeDataString(target).Split('/'))
        {
            if (segment == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
            }
            else if (segment is not ("." or ""))
            {
                parts.Add(segment);
            }
        }
        return string.Join('/', parts);
    }
}
