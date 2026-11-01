// Minimal reader for Office Open XML packages (.xlsx, .pptx): parts as XML, relationships resolved to zip paths.

using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Filee.Engines.Documents;

namespace Filee.Engines.Office.Ooxml;

/// <summary>A relationship of a package part; <see cref="Target"/> is a zip path unless it is external.</summary>
internal readonly record struct OpcRelationship(string Type, string Target, bool External);

/// <summary>An open OPC zip package. Parts and relationships are cached.</summary>
internal sealed class OpcPackage : IDisposable
{
    private static readonly XNamespace PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    private readonly ZipArchive? _zip;
    private readonly DocumentPackage? _limited;
    private readonly Dictionary<string, XDocument?> _parts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, OpcRelationship>> _relationships = new(StringComparer.OrdinalIgnoreCase);

    public OpcPackage(string path) => _zip = ZipFile.OpenRead(path);

    /// <summary>
    /// A package read within <paramref name="limits"/> (part and package sizes, the inflate ratio, XML depth and
    /// elements); a part that isn't well-formed XML is <see cref="InvalidDataException"/>.
    /// </summary>
    public OpcPackage(string path, DocumentLimits limits) =>
        _limited = DocumentPackage.Open(path, limits, () => new InvalidDataException("The file is not an Office Open XML package (no ZIP). It may be damaged."));

    private ZipArchiveEntry? Entry(string path) => _limited is not null ? _limited.GetEntry(path) : _zip!.GetEntry(path);

    private XDocument Load(ZipArchiveEntry entry)
    {
        if (_limited is null)
        {
            using var stream = entry.Open();
            return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }
        try
        {
            return _limited.LoadXml(entry);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"The part {entry.FullName} is not well-formed XML ({ex.Message}).", ex);
        }
    }

    /// <summary>The bytes of a binary part (a picture); null when it doesn't exist or holds more than <paramref name="max"/>.</summary>
    public byte[]? Bytes(string partPath, long max)
    {
        if (Entry(partPath) is not { } entry)
            return null;
        if (_limited is not null)
            return _limited.ReadBytes(entry, max);
        if (entry.Length > max)
            return null;
        using var stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.Length > max ? null : copy.ToArray();
    }

    /// <summary>The main part (target of the package's officeDocument relationship), e.g. xl/workbook.xml.</summary>
    public string? MainPartPath =>
        Relationships("").Values.FirstOrDefault(r => r.Type.EndsWith("/officeDocument", StringComparison.Ordinal)).Target;

    /// <summary>A part as XML (whitespace preserved), or null when it doesn't exist.</summary>
    public XDocument? Part(string path)
    {
        if (_parts.TryGetValue(path, out var cached))
            return cached;
        XDocument? xml = null;
        if (Entry(path) is { } entry)
            xml = Load(entry);
        _parts[path] = xml;
        return xml;
    }

    /// <summary>Relationships of a part ("" = the package), by id.</summary>
    public Dictionary<string, OpcRelationship> Relationships(string partPath)
    {
        if (_relationships.TryGetValue(partPath, out var cached))
            return cached;
        var folder = Folder(partPath);
        var relsPath = partPath.Length == 0 ? "_rels/.rels" : $"{folder}/_rels/{partPath[(partPath.LastIndexOf('/') + 1)..]}.rels".TrimStart('/');
        var result = new Dictionary<string, OpcRelationship>();
        if (Entry(relsPath) is { } entry)
        {
            foreach (var rel in Load(entry).Root?.Elements(PackageRelationships + "Relationship") ?? [])
            {
                var target = (string?)rel.Attribute("Target") ?? "";
                var external = (string?)rel.Attribute("TargetMode") == "External";
                result[(string?)rel.Attribute("Id") ?? ""] = new OpcRelationship(
                    (string?)rel.Attribute("Type") ?? "", external ? target : ResolvePath(folder, target), external);
            }
        }
        _relationships[partPath] = result;
        return result;
    }

    /// <summary>Zip path of the part that relationship <paramref name="id"/> of <paramref name="partPath"/> points to.</summary>
    public string? Target(string partPath, string? id) =>
        id is not null && Relationships(partPath).TryGetValue(id, out var rel) && !rel.External ? rel.Target : null;

    /// <summary>The first relationship target of a type (matched on the end of the type URI, e.g. "/theme").</summary>
    public string? TargetOfType(string partPath, string typeSuffix) =>
        Relationships(partPath).Values.FirstOrDefault(r => !r.External && r.Type.EndsWith(typeSuffix, StringComparison.Ordinal)).Target;

    /// <summary>Copies a binary part (a picture) to <paramref name="folder"/>; returns the file path, or null.</summary>
    public string? Extract(string partPath, string folder)
    {
        if (Entry(partPath) is not { } entry)
            return null;
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{Guid.NewGuid():N}{Path.GetExtension(partPath)}");
        entry.ExtractToFile(file);
        return file;
    }

    public void Dispose()
    {
        _zip?.Dispose();
        _limited?.Dispose();
    }

    private static string Folder(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    private static string ResolvePath(string folder, string target)
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
