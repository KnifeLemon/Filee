// An OpenDocument presentation or drawing as the draw reader reads it: a package (.odp, .otp, .odg; content.xml,
// styles.xml and pictures as parts, OpenDocument 1.3 Part 2) or one flat XML file (.fodp, .fodg; office:document with
// the styles in it and pictures as office:binary-data). Parts go through DocumentPackage (part, package and inflate
// limits, XML depth and element counts); a flat file through SafeXml with the same depth and element counts and a size
// limit of its own. Pictures stored in the XML are decoded once each, counted with the unpacked parts against what one
// document may unpack to. Encrypted packages and other OpenDocument types are refused with their reason.

using System.Xml;
using System.Xml.Linq;
using Filee.Engines.Documents;
using Filee.Engines.Infrastructure;
using OdfNames = Filee.Engines.Hwp.Hwpx.Odt.Odf;

namespace Filee.Engines.Odf.Draw;

internal sealed class OdfDrawPackage : IDisposable
{
    /// <summary>A flat file may be this many times a package part: it holds every part and the pictures in base64.</summary>
    private const int FlatPartFactor = 4;

    private static readonly XNamespace Office = OdfNames.Office;
    private static readonly XNamespace Manifest = OdfNames.Manifest;

    private readonly DocumentPackage? _package;
    private readonly Dictionary<string, byte[]?> _parts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement?> _xmlParts = new(StringComparer.Ordinal);
    private readonly Dictionary<XElement, byte[]?> _inline = new(ReferenceEqualityComparer.Instance);
    private long _inlineBytes;

    private OdfDrawPackage(DocumentPackage? package, DocumentLimits limits, XElement content, XElement? styles)
    {
        _package = package;
        Limits = limits;
        Content = content;
        Styles = styles;
    }

    public DocumentLimits Limits { get; }

    /// <summary>office:document-content, or a flat file's office:document.</summary>
    public XElement Content { get; }

    /// <summary>office:document-styles, or a flat file's office:document (the same element as <see cref="Content"/>); null when missing.</summary>
    public XElement? Styles { get; }

    /// <summary>office:document-meta, or a flat file's office:document; null when missing.</summary>
    public XElement? Meta { get; private init; }

    /// <summary>The document's title (dc:title), or null.</summary>
    public string? Title =>
        Meta?.Element(Office + "meta")?.Element(OdfNames.Dc + "title")?.Value is { } title && title.Trim().Length > 0 ? title.Trim() : null;

    /// <summary>A flat XML file: styles and content in one document, automatic styles shared.</summary>
    public bool Flat => _package is null;

    public void Dispose() => _package?.Dispose();

    /// <summary>Opens <paramref name="path"/>: a ZIP package by its signature, else a flat XML file.</summary>
    public static OdfDrawPackage Open(string path, DocumentLimits? limits = null)
    {
        limits ??= DocumentLimits.Default;
        using (var probe = File.OpenRead(path))
        {
            Span<byte> head = stackalloc byte[2];
            if (probe.ReadAtLeast(head, 2, throwOnEndOfStream: false) == 2 && head[0] == (byte)'P' && head[1] == (byte)'K')
                return OpenPackage(path, limits);
        }
        return OpenFlat(path, limits);
    }

    private static OdfDrawPackage OpenPackage(string path, DocumentLimits limits)
    {
        var package = DocumentPackage.Open(path, limits,
            () => new InvalidDataException("The file is not an OpenDocument presentation or drawing (not a ZIP package). It may be damaged."));
        try
        {
            if (package.GetEntry("mimetype") is { } mimetype && package.ReadBytes(mimetype, 256) is { } bytes)
                CheckType(System.Text.Encoding.ASCII.GetString(bytes).Trim());
            // Encrypted packages keep a readable manifest that says how each part is encrypted; the parts are not XML.
            if (Load(package, "META-INF/manifest.xml")?.Descendants(Manifest + "encryption-data").Any() == true)
                throw new InvalidDataException("The file is protected with a password (encrypted). Open it in LibreOffice, remove the password (File → Properties → Security), save it and convert it again.");
            var content = Load(package, "content.xml")
                          ?? throw new InvalidDataException("The file is not an OpenDocument presentation or drawing (no content.xml).");
            return new OdfDrawPackage(package, limits, content, Load(package, "styles.xml")) { Meta = Load(package, "meta.xml") };
        }
        catch
        {
            package.Dispose();
            throw;
        }
    }

    private static OdfDrawPackage OpenFlat(string path, DocumentLimits limits)
    {
        var maxBytes = limits.MaxPartBytes * FlatPartFactor;
        if (new FileInfo(path).Length > maxBytes)
            throw DocumentLimits.Exceeded($"The file is larger than {DocumentLimits.Megabytes(maxBytes)}.");
        XDocument document;
        using (var stream = File.OpenRead(path))
        {
            try
            {
                document = SafeXml.Load(stream, new XmlLimits(limits.MaxXmlDepth, limits.MaxXmlElements, maxBytes), DocumentLimits.Exceeded);
            }
            catch (Exception ex) when (ex is XmlException or System.Text.DecoderFallbackException or ArgumentException)
            {
                throw new InvalidDataException($"The file is neither an OpenDocument package nor valid flat OpenDocument XML. It may be damaged. {ex.Message}", ex);
            }
        }
        var root = document.Root;
        if (root is null || root.Name != Office + "document")
            throw new InvalidDataException("The file is not an OpenDocument presentation or drawing (no office:document).");
        if ((string?)root.Attribute(Office + "mimetype") is { Length: > 0 } type)
            CheckType(type);
        return new OdfDrawPackage(null, limits, root, root) { Meta = root };
    }

    private static void CheckType(string type)
    {
        if (type.Length > 0 && type is not (OdfNames.PresentationMimeType or OdfNames.PresentationTemplateMimeType or OdfNames.GraphicsMimeType
                or OdfNames.GraphicsTemplateMimeType))
            throw new InvalidDataException($"The file is not an OpenDocument presentation or drawing ({type}).");
    }

    private static XElement? Load(DocumentPackage package, string name)
    {
        if (package.GetEntry(name) is not { } entry)
            return null;
        try
        {
            return package.LoadXml(entry, preserveWhitespace: true).Root;
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"The file cannot be read ({name} is not valid XML). It may be encrypted or damaged. {ex.Message}", ex);
        }
    }

    /// <summary>A package path from an xlink:href ("Pictures/x.png", "./Object 1"); null for links outside the package.</summary>
    public static string? InternalPath(string? href)
    {
        var path = href?.Trim();
        if (path is null)
            return null;
        if (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        if (path.Length == 0 || path.Contains("://", StringComparison.Ordinal) || path.StartsWith('/') || path.Contains(':')
            || path.Split('/').Any(segment => segment is ".." or "."))
            return null;
        return path;
    }

    /// <summary>The bytes of a part named by an xlink:href (a picture), up to the part limit; null when missing, outside or too large.</summary>
    public byte[]? Bytes(string? href)
    {
        if (_package is null || InternalPath(href) is not { } path)
            return null;
        if (_parts.TryGetValue(path, out var cached))
            return cached;
        var bytes = _package.GetEntry(path) is { } entry ? _package.ReadBytes(entry, Limits.MaxPartBytes) : null;
        _parts[path] = bytes;
        return bytes;
    }

    /// <summary>The root of an XML part named by an xlink:href and a part name in it ("Object 1" + "content.xml"); null when missing.</summary>
    public XElement? XmlPart(string? href, string name)
    {
        if (_package is null || InternalPath(href) is not { } folder)
            return null;
        var path = folder.TrimEnd('/') + "/" + name;
        if (!_xmlParts.TryGetValue(path, out var root))
            _xmlParts[path] = root = Load(_package, path);
        return root;
    }

    /// <summary>
    /// The bytes of a picture stored in the XML (office:binary-data), decoded once per element; null when it is no
    /// base64 or larger than a part may be. Decoded bytes count with the parts read against the package limit.
    /// </summary>
    public byte[]? Inline(XElement data)
    {
        if (_inline.TryGetValue(data, out var cached))
            return cached;
        byte[]? bytes = null;
        var text = data.Value;
        if (text.Length / 4L * 3 <= Limits.MaxPartBytes)
        {
            try
            {
                bytes = Convert.FromBase64String(text.Trim());
            }
            catch (FormatException)
            {
                bytes = null;
            }
        }
        if (bytes is not null)
        {
            _inlineBytes += bytes.Length;
            if ((_package?.BytesRead ?? 0) + _inlineBytes > Limits.MaxPackageBytes)
                throw DocumentLimits.Exceeded($"The document and the pictures stored in its XML unpack to more than {DocumentLimits.Megabytes(Limits.MaxPackageBytes)}.");
        }
        _inline[data] = bytes;
        return bytes;
    }
}
