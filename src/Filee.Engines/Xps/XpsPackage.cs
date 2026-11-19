// The OPC package of an XPS or OpenXPS document (Microsoft XPS 1.0, ECMA-388), read defensively through
// DocumentPackage: part names are looked up inside the package and never used for files, references are resolved
// against the referring part and can't leave the package, and every read counts against DocumentLimits (part and
// package sizes counted while unpacking, the inflate ratio, XML without DTDs, depth and element limits).
//
// Part names are URIs: "%20" in a reference names the part "a b", and ZIP items may store either form. A part may be
// split into interleaved pieces ("a.fpage/[0].piece" … "a.fpage/[3].last.piece"), which are joined in order; a part
// whose pieces don't run from [0] to a last piece without gaps is damaged and counts as missing.
//
// Embedded fonts may be obfuscated (.odttf, both specs): the first 32 bytes are XORed with the GUID that names the part,
// its 16 bytes taken from the GUID's text in reverse order.

using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Filee.Engines.Documents;
using Filee.Engines.Infrastructure;

namespace Filee.Engines.Xps;

/// <summary>An XPS / OpenXPS package opened for reading.</summary>
internal sealed partial class XpsPackage : IDisposable
{
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    private readonly DocumentPackage _package;

    /// <summary>The ZIP items of each part (one, or its pieces in order), by unescaped part name without the leading "/".</summary>
    private readonly Dictionary<string, ZipArchiveEntry[]> _parts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>XML elements of joined pieces, which DocumentPackage doesn't see.</summary>
    private int _pieceElements;

    private XpsPackage(DocumentPackage package)
    {
        _package = package;
        var pieces = new Dictionary<string, SortedDictionary<int, (ZipArchiveEntry Entry, bool Last)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in package.Entries)
        {
            if (entry.FullName.EndsWith('/'))
                continue;
            var name = Unescape(entry.FullName.Replace('\\', '/').TrimStart('/'));
            if (PiecePattern().Match(name) is { Success: true } piece)
            {
                if (!int.TryParse(piece.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                    continue;
                if (!pieces.TryGetValue(piece.Groups[1].Value, out var list))
                    pieces[piece.Groups[1].Value] = list = [];
                list.TryAdd(index, (entry, piece.Groups[3].Success));
                continue;
            }
            // Of two items with one name the first wins.
            _parts.TryAdd(name, [entry]);
        }
        foreach (var (name, list) in pieces)
        {
            // [0] to [n].last without gaps; anything else is a damaged part.
            var ordered = list.ToList();
            var complete = ordered[^1].Value.Last && ordered.Select((p, i) => p.Key == i && (p.Value.Last == (i == ordered.Count - 1))).All(ok => ok);
            if (complete)
                _parts.TryAdd(name, [.. ordered.Select(p => p.Value.Entry)]);
        }
    }

    [GeneratedRegex(@"^(.+)/\[(\d{1,9})\](\.last)?\.piece$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PiecePattern();

    public static XpsPackage Open(string path, DocumentLimits limits) =>
        new(DocumentPackage.Open(path, limits, NotAPackage));

    public static XpsPackage Open(Stream stream, DocumentLimits limits) =>
        new(DocumentPackage.Open(stream, limits, NotAPackage));

    private static InvalidDataException NotAPackage() =>
        new("The file is not an XPS document (it is not a ZIP package). It may be damaged.");

    public DocumentLimits Limits => _package.Limits;

    public void Dispose() => _package.Dispose();

    /// <summary>True when the package has a part of that (resolved) name.</summary>
    public bool Has(string? name) => name is not null && _parts.ContainsKey(name);

    /// <summary>Names of all parts, resolved.</summary>
    public IEnumerable<string> Names => _parts.Keys;

    /// <summary>
    /// A reference as a part name: relative to the folder of <paramref name="from"/> (a part name), or to the package
    /// root when it starts with "/". Fragments and queries are dropped, "." and ".." applied, escapes undone. Null when
    /// the reference is empty, leaves the package, or is a URL or drive path.
    /// </summary>
    public static string? Resolve(string from, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;
        reference = reference.Trim();
        var cut = reference.IndexOfAny(['#', '?']);
        if (cut >= 0)
            reference = reference[..cut];
        reference = Unescape(reference).Replace('\\', '/');
        if (reference.Length == 0 || reference.Contains(':'))
            return null;
        var parts = new List<string>();
        if (!reference.StartsWith('/'))
        {
            var folder = from.LastIndexOf('/');
            if (folder > 0)
                parts.AddRange(from[..folder].Split('/', StringSplitOptions.RemoveEmptyEntries));
        }
        foreach (var segment in reference.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;
            if (segment == "..")
            {
                if (parts.Count == 0)
                    return null;
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(segment);
        }
        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    /// <summary>The fragment of a reference ("1" of "font.ttc#1"), or null.</summary>
    public static string? Fragment(string? reference)
    {
        var hash = reference?.IndexOf('#') ?? -1;
        return hash < 0 ? null : Unescape(reference![(hash + 1)..]);
    }

    private static string Unescape(string text)
    {
        try
        {
            return text.Contains('%') ? Uri.UnescapeDataString(text) : text;
        }
        catch (UriFormatException)
        {
            return text;
        }
    }

    /// <summary>The root element of an XML part; null when the package has no such part.</summary>
    public XElement? Xml(string? name)
    {
        if (name is null || !_parts.TryGetValue(name, out var entries))
            return null;
        try
        {
            if (entries.Length == 1)
                return _package.LoadXml(entries[0], preserveWhitespace: false).Root;
            var bytes = Join(entries, Limits.MaxPartBytes) ?? throw PartTooLarge(name);
            var limits = Limits.Xml(_package.ElementsRead + _pieceElements, preserveWhitespace: false);
            var document = SafeXml.Load(new MemoryStream(bytes, writable: false), limits,
                message => DocumentLimits.Exceeded($"{name}: {message}"), out var elements);
            _pieceElements += elements;
            return document.Root;
        }
        catch (Exception ex) when (ex is XmlException or System.Text.DecoderFallbackException or ArgumentException)
        {
            throw new InvalidDataException($"The XPS part {name} is not valid XML. The file may be damaged. {ex.Message}", ex);
        }
    }

    /// <summary>The bytes of a part; null when it is missing or larger than <paramref name="max"/>.</summary>
    public byte[]? Bytes(string? name, long max)
    {
        if (name is null || !_parts.TryGetValue(name, out var entries))
            return null;
        return entries.Length == 1 ? _package.ReadBytes(entries[0], max) : Join(entries, max);
    }

    /// <summary>The pieces of a part joined; null when they hold more than <paramref name="max"/>.</summary>
    private byte[]? Join(ZipArchiveEntry[] entries, long max)
    {
        using var joined = new MemoryStream();
        foreach (var entry in entries)
        {
            if (_package.ReadBytes(entry, max - joined.Length) is not { } bytes)
                return null;
            joined.Write(bytes);
        }
        return joined.ToArray();
    }

    private Exception PartTooLarge(string name) =>
        DocumentLimits.Exceeded($"The part {name} is larger than {DocumentLimits.Megabytes(Limits.MaxPartBytes)}.");

    /// <summary>The targets of the package's own relationships of a type ending in <paramref name="typeSuffix"/>.</summary>
    public IEnumerable<string> RootRelationships(string typeSuffix)
    {
        var rels = Xml("_rels/.rels");
        if (rels is null)
            yield break;
        foreach (var relationship in rels.Elements(Relationships + "Relationship"))
        {
            if ((string?)relationship.Attribute("TargetMode") is { } mode && mode.Equals("External", StringComparison.OrdinalIgnoreCase))
                continue;
            if (((string?)relationship.Attribute("Type"))?.EndsWith(typeSuffix, StringComparison.OrdinalIgnoreCase) != true)
                continue;
            if (Resolve("", (string?)relationship.Attribute("Target")) is { } target)
                yield return target;
        }
    }

    /// <summary>
    /// The font part's bytes, with the obfuscation of an .odttf part undone; null when the part is missing, too large
    /// or obfuscated under a name that is no GUID.
    /// </summary>
    public byte[]? Font(string name)
    {
        var bytes = Bytes(name, Limits.MaxPartBytes);
        if (bytes is null || !name.EndsWith(".odttf", StringComparison.OrdinalIgnoreCase))
            return bytes;
        var file = name[(name.LastIndexOf('/') + 1)..^".odttf".Length];
        return Deobfuscate(bytes, file) ? bytes : null;
    }

    /// <summary>XORs the first 32 bytes with the GUID <paramref name="guid"/>; false when it is no GUID or the font is too short.</summary>
    internal static bool Deobfuscate(byte[] font, string guid)
    {
        if (font.Length < 32 || !Guid.TryParse(guid, out var parsed))
            return false;
        var hex = parsed.ToString("N", CultureInfo.InvariantCulture);
        Span<byte> key = stackalloc byte[16];
        for (var i = 0; i < 16; i++)
            key[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        for (var i = 0; i < 32; i++)
            font[i] ^= key[15 - i % 16];
        return true;
    }
}
