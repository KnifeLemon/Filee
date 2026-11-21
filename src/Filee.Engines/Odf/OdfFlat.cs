// Flat OpenDocument files (.fodt, .fods, .fodp, .fodg; OpenDocument 1.3 Part 3, 2.2) to and from packages. A flat
// file is one office:document holding what the package keeps in its parts: office:meta (meta.xml), office:settings
// (settings.xml), office:scripts, office:font-face-decls and office:automatic-styles (content.xml and styles.xml),
// office:styles and office:master-styles (styles.xml) and office:body (content.xml). Pictures are base64 in
// office:binary-data where the package has an xlink:href to a part (embedded fonts too, in svg:font-face-uri), and an
// embedded object (a chart) is an office:document of its own inside draw:object where the package has a folder
// ("Object 1/"), and a formula (a Math object) its math:math element.
//
// Taking a flat file apart, the automatic styles go into both content.xml and styles.xml (the master pages use them
// too) and each picture becomes a part under Pictures/, each font under Fonts/ (one part for one stored more than
// once); office:binary-data in an element that has no part in a package (an extension's) stays where it is. A flat
// file that says it is a template becomes a template package when it is read (OdfVariants.AsPackage). Putting a
// package together, the automatic styles of styles.xml that have the name of one of the same family in content.xml
// are renamed, with what refers to them in styles.xml's automatic styles and master pages (a reference names a style
// of the families its attribute can name; an automatic style's parent is always a common style, never renamed).
//
// Limits: a flat file is read through SafeXml within the document limits' depth and element counts and at most
// FlatPartFactor times what one package part may hold (as the ODP / ODG reader reads one, Odf/Draw/OdfDrawPackage); a
// picture decodes to at most a part's size, all of them to at most the package size, in at most as many parts as a
// package may have. A package is read through DocumentPackage, and a flat file made from it may hold at most what a flat
// file may be read with: one picture referred to again and again is stored each time, and that can't grow without end.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;
using Filee.Engines.Metafiles;
using OdfNames = Filee.Engines.Hwp.Hwpx.Odt.Odf;

namespace Filee.Engines.Odf;

internal static class OdfFlat
{
    /// <summary>A flat file may be this many times a package part: it holds every part and the pictures in base64.</summary>
    internal const int FlatPartFactor = 4;

    /// <summary>Objects inside objects followed at most.</summary>
    private const int MaxObjectDepth = 8;

    private static readonly XNamespace Office = OdfNames.Office;
    private static readonly XNamespace Style = OdfNames.Style;
    private static readonly XNamespace Text = OdfNames.Text;
    private static readonly XNamespace Draw = OdfNames.Draw;
    private static readonly XNamespace XLink = OdfNames.XLink;
    private static readonly XNamespace Manifest = OdfNames.Manifest;
    private static readonly XNamespace Svg = OdfNames.Svg;
    private static readonly XNamespace Presentation = OdfNames.Presentation;
    private static readonly XNamespace Table = OdfNames.Table;
    private static readonly XNamespace Chart = OdfNames.Chart;
    private static readonly XNamespace Number = "urn:oasis:names:tc:opendocument:xmlns:datastyle:1.0";
    private static readonly XNamespace MathMl = OdfNames.MathMl;

    /// <summary>The elements whose picture (OLE object, font) is a part in a package and office:binary-data in a flat file.</summary>
    private static readonly HashSet<XName> Binaries =
    [
        Draw + "image", Draw + "fill-image", Draw + "object-ole", Style + "background-image", Text + "list-level-style-image", Svg + "font-face-uri",
    ];

    /// <summary>The most a flat file may be, read or written, within <paramref name="limits"/>.</summary>
    internal static long MaxBytes(DocumentLimits limits) => limits.MaxPartBytes * FlatPartFactor;

    // ───────────────────────── Flat → package ─────────────────────────

    /// <summary>Writes the flat file <paramref name="input"/> as the package <paramref name="output"/> with <paramref name="mimeType"/>.</summary>
    /// <param name="accepted">The media types the flat file may say it has.</param>
    /// <param name="kind">The type for messages ("an OpenDocument text").</param>
    /// <param name="template">
    /// The template media type of the type: written instead of <paramref name="mimeType"/> when the flat file says it
    /// is a template; null to write <paramref name="mimeType"/> whatever it says.
    /// </param>
    public static void ToPackage(string input, string output, string mimeType, IReadOnlyCollection<string> accepted, string kind,
        DocumentLimits limits, CancellationToken cancellationToken, string? template = null)
    {
        var document = Load(input, kind, limits);
        if ((string?)document.Attribute(Office + "mimetype") is { Length: > 0 } type && !accepted.Contains(type.Trim()))
            throw new InvalidDataException($"The file is not {kind} ({type.Trim()}).");
        if (template is not null && string.Equals(((string?)document.Attribute(Office + "mimetype"))?.Trim(), template, StringComparison.Ordinal))
            mimeType = template;
        var split = new Splitter(limits, cancellationToken);
        split.Split(document, "", mimeType, 0);
        OdfPackageWriter.Write(output, mimeType, (string?)document.Attribute(Office + "version") ?? OdfNames.Version, split.Parts, cancellationToken);
    }

    /// <summary>The office:document of a flat file, read within the limits.</summary>
    internal static XElement Load(string path, string kind, DocumentLimits limits)
    {
        var maxBytes = MaxBytes(limits);
        if (new FileInfo(path).Length > maxBytes)
            throw DocumentLimits.TooLarge($"The file is larger than {DocumentLimits.Megabytes(maxBytes)}.");
        XDocument document;
        using (var stream = File.OpenRead(path))
        {
            try
            {
                document = SafeXml.Load(stream, new XmlLimits(limits.MaxXmlDepth, limits.MaxXmlElements, maxBytes), DocumentLimits.Exceeded, DocumentLimits.TooLarge);
            }
            catch (Exception ex) when (ex is XmlException or DecoderFallbackException or ArgumentException)
            {
                throw new InvalidDataException($"The file is not valid flat OpenDocument XML. It may be damaged. {ex.Message}", ex);
            }
        }
        if (document.Root is not { } root || root.Name != Office + "document")
            throw new InvalidDataException($"The file is not {kind} (no office:document).");
        return root;
    }

    /// <summary>Takes office:document elements apart into the parts of a package.</summary>
    private sealed class Splitter(DocumentLimits limits, CancellationToken cancellationToken)
    {
        public List<OdfPart> Parts { get; } = [];

        /// <summary>The part of each picture by its bytes' hash and folder: a picture stored again and again is one part.</summary>
        private readonly Dictionary<string, string> _stored = new(StringComparer.Ordinal);

        private long _bytes;
        private int _objects;

        public void Split(XElement document, string folder, string mimeType, int depth)
        {
            Walk(document, folder, depth);
            XElement Root(string name, params XName[] children)
            {
                var root = new XElement(Office + name, document.Attributes().Where(a => a.IsNamespaceDeclaration));
                if (document.Attribute(Office + "version") is { } version)
                    root.Add(new XAttribute(version));
                foreach (var child in children)
                {
                    if (document.Element(child) is not { } element)
                        continue;
                    // Font faces and automatic styles go into both content.xml and styles.xml; the rest moves.
                    if (child == Office + "font-face-decls" || child == Office + "automatic-styles")
                    {
                        root.Add(new XElement(element));
                    }
                    else
                    {
                        element.Remove();
                        root.Add(element);
                    }
                }
                return root;
            }
            Add(folder + "content.xml", Root("document-content", Office + "scripts", Office + "font-face-decls", Office + "automatic-styles", Office + "body"));
            Add(folder + "styles.xml", Root("document-styles", Office + "font-face-decls", Office + "styles", Office + "automatic-styles", Office + "master-styles"));
            if (document.Element(Office + "meta") is not null)
                Add(folder + "meta.xml", Root("document-meta", Office + "meta"));
            if (document.Element(Office + "settings") is not null)
                Add(folder + "settings.xml", Root("document-settings", Office + "settings"));
            if (folder.Length > 0)
                Parts.Add(new OdfPart(folder, mimeType));
        }

        private void Add(string path, XElement root)
        {
            using var stream = new MemoryStream();
            using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
                new XDocument(new XDeclaration("1.0", "UTF-8", null), root).Save(writer);
            Parts.Add(new OdfPart(path, "text/xml", Bytes: stream.ToArray()));
        }

        /// <summary>
        /// Turns office:binary-data into parts and embedded office:documents into folders, below
        /// <paramref name="element"/> but not inside the documents of its objects (those are split on their own).
        /// </summary>
        private void Walk(XElement element, string folder, int depth)
        {
            foreach (var child in element.Elements().ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (child.Name == Office + "binary-data")
                {
                    Picture(child, folder);
                    continue;
                }
                if (child.Name == Draw + "object" && child.Element(Office + "document") is { } inner)
                {
                    Object(child, inner, folder, depth);
                    continue;
                }
                if (child.Name == Draw + "object" && child.Element(MathMl + "math") is { } math)
                {
                    Formula(child, math, folder);
                    continue;
                }
                if (child.Name != Office + "document")
                    Walk(child, folder, depth);
            }
        }

        private void Picture(XElement data, string folder)
        {
            var owner = data.Parent!;
            // Data in an element that has no part in a package (an extension's) stays in it.
            if (!Binaries.Contains(owner.Name))
                return;
            data.Remove();
            var text = data.Value;
            if (text.Length / 4L * 3 > limits.MaxPartBytes)
                throw DocumentLimits.TooLarge($"A picture stored in the file is larger than {DocumentLimits.Megabytes(limits.MaxPartBytes)}.");
            byte[] bytes;
            try
            {
                bytes = System.Convert.FromBase64String(text.Trim());
            }
            catch (FormatException)
            {
                return; // not base64: the picture is left out, as the readers leave it out
            }
            _bytes += bytes.Length;
            if (_bytes > limits.MaxPackageBytes)
                throw DocumentLimits.TooLarge($"The pictures stored in the file are larger than {DocumentLimits.Megabytes(limits.MaxPackageBytes)} together.");
            var key = folder + ":" + System.Convert.ToHexString(SHA256.HashData(bytes));
            if (_stored.TryGetValue(key, out var known))
            {
                Link(owner, known);
                return;
            }
            if (Parts.Count >= limits.MaxPackageEntries)
                throw DocumentLimits.TooLarge($"The file holds more than {DocumentLimits.Count(limits.MaxPackageEntries)} pictures and objects.");
            var font = owner.Name == Svg + "font-face-uri";
            var (extension, mediaType) = owner.Name == Draw + "object-ole" ? ("", "application/vnd.sun.star.oleobject")
                : font ? FontKind(bytes)
                : Kind(bytes);
            var name = (owner.Name == Draw + "object-ole" ? "OleObject" : font ? "Fonts/font" : "Pictures/image")
                       + (Parts.Count + 1).ToString(CultureInfo.InvariantCulture) + extension;
            Parts.Add(new OdfPart(folder + name, mediaType, Bytes: bytes));
            _stored[key] = name;
            Link(owner, name);
        }

        private void Object(XElement frame, XElement inner, string folder, int depth)
        {
            inner.Remove();
            if (depth >= MaxObjectDepth)
                return;
            if (++_objects > limits.MaxObjects)
                throw DocumentLimits.Exceeded($"The file holds more than {DocumentLimits.Count(limits.MaxObjects)} objects.");
            var name = "Object " + _objects.ToString(CultureInfo.InvariantCulture);
            var type = ((string?)inner.Attribute(Office + "mimetype"))?.Trim() is { Length: > 0 } declared ? declared : "application/vnd.oasis.opendocument.chart";
            Split(inner, folder + name + "/", type, depth + 1);
            Link(frame, "./" + name);
        }

        /// <summary>A formula (math:math in draw:object) as a folder of its own with the MathML as its content.xml.</summary>
        private void Formula(XElement frame, XElement math, string folder)
        {
            math.Remove();
            if (++_objects > limits.MaxObjects)
                throw DocumentLimits.Exceeded($"The file holds more than {DocumentLimits.Count(limits.MaxObjects)} objects.");
            var name = "Object " + _objects.ToString(CultureInfo.InvariantCulture);
            Add(folder + name + "/content.xml", math);
            Parts.Add(new OdfPart(folder + name + "/", OdfNames.FormulaMimeType));
            Link(frame, "./" + name);
        }

        private static void Link(XElement element, string href)
        {
            element.SetAttributeValue(XLink + "href", href);
            element.SetAttributeValue(XLink + "type", "simple");
            element.SetAttributeValue(XLink + "show", "embed");
            element.SetAttributeValue(XLink + "actuate", "onLoad");
        }
    }

    /// <summary>The extension and media type of an embedded font, by its signature (as LibreOffice names them).</summary>
    internal static (string Extension, string MediaType) FontKind(byte[] data)
    {
        var span = data.AsSpan();
        if (span.StartsWith("OTTO"u8))
            return (".otf", "application/vnd.ms-opentype");
        if (span.StartsWith("ttcf"u8))
            return (".ttc", "application/x-font-ttf");
        if (span.StartsWith("wOFF"u8) || span.StartsWith("wOF2"u8))
            return (".woff", "application/font-woff");
        return (".ttf", "application/x-font-ttf");
    }

    /// <summary>The extension and media type of a picture, by its signature.</summary>
    internal static (string Extension, string MediaType) Kind(byte[] data)
    {
        var span = data.AsSpan();
        if (span.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']))
            return (".png", "image/png");
        if (span.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
            return (".jpg", "image/jpeg");
        if (span.StartsWith("GIF8"u8))
            return (".gif", "image/gif");
        if (span.StartsWith("BM"u8))
            return (".bmp", "image/bmp");
        if (span.StartsWith("II*\0"u8) || span.StartsWith("MM\0*"u8))
            return (".tif", "image/tiff");
        if (span.Length >= 12 && span.StartsWith("RIFF"u8) && span[8..12].SequenceEqual("WEBP"u8))
            return (".webp", "image/webp");
        switch (Metafile.Detect(span[..Math.Min(span.Length, 64)]))
        {
            case MetafileFormat.Emf:
                return (".emf", "image/x-emf");
            case MetafileFormat.Wmf:
                return (".wmf", "image/x-wmf");
        }
        var head = Encoding.UTF8.GetString(span[..Math.Min(span.Length, 1024)]);
        if (head.Contains("<svg", StringComparison.Ordinal))
            return (".svg", "image/svg+xml");
        return (".bin", "application/octet-stream");
    }

    // ───────────────────────── Package → flat ─────────────────────────

    /// <summary>Writes the package <paramref name="input"/> as the flat file <paramref name="output"/> with <paramref name="mimeType"/>.</summary>
    /// <param name="accepted">The media types the package may say it has.</param>
    /// <param name="kind">The type for messages ("an OpenDocument text").</param>
    public static void ToFlat(string input, string output, string mimeType, IReadOnlyCollection<string> accepted, string kind,
        DocumentLimits limits, CancellationToken cancellationToken)
    {
        using var package = OdfVariants.OpenPackage(input, accepted, kind, limits);
        var joiner = new Joiner(package, limits, cancellationToken);
        var document = joiner.Join("", mimeType, 0) ?? throw new InvalidDataException($"The file is not {kind} (no content.xml).");
        WriterTempFile.Write(output, file =>
        {
            using var writer = XmlWriter.Create(file, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
            new XDocument(new XDeclaration("1.0", "UTF-8", null), document).Save(writer);
        });
    }

    /// <summary>Puts the parts of a package (and of its objects) together into office:document elements.</summary>
    private sealed class Joiner(DocumentPackage package, DocumentLimits limits, CancellationToken cancellationToken)
    {
        private readonly Dictionary<string, byte[]> _pictures = new(StringComparer.Ordinal);
        private XElement? _manifest;

        /// <summary>What the flat file holds so far: the XML parts as they are, the pictures in base64.</summary>
        private long _stored;

        /// <summary>The document of the package's folder <paramref name="folder"/> ("" or "Object 1/"); null when it has no content.xml.</summary>
        public XElement? Join(string folder, string mimeType, int depth)
        {
            var content = Load(folder + "content.xml");
            if (content is null)
                return null;
            // A formula is its math:math element itself in a flat file (draw:object holds it), not an office:document.
            if (content.Name == MathMl + "math")
            {
                content.Remove();
                return content;
            }
            var styles = Load(folder + "styles.xml");
            var meta = Load(folder + "meta.xml");
            var settings = Load(folder + "settings.xml");

            var document = new XElement(Office + "document");
            foreach (var part in new[] { content, styles, meta, settings })
            {
                foreach (var declaration in part?.Attributes().Where(a => a.IsNamespaceDeclaration) ?? [])
                {
                    if (document.Attribute(declaration.Name) is null)
                        document.Add(new XAttribute(declaration));
                }
            }
            document.SetAttributeValue(Office + "version", (string?)content.Attribute(Office + "version") ?? (string?)styles?.Attribute(Office + "version") ?? OdfNames.Version);
            document.SetAttributeValue(Office + "mimetype", mimeType);

            Add(document, meta?.Element(Office + "meta"));
            Add(document, settings?.Element(Office + "settings"));
            Add(document, content.Element(Office + "scripts"));
            Add(document, Merged(Office + "font-face-decls", content.Element(Office + "font-face-decls"), styles?.Element(Office + "font-face-decls"), [], master: null));
            Add(document, styles?.Element(Office + "styles"));
            var renames = new Dictionary<(string Family, string Name), string>();
            Add(document, Merged(Office + "automatic-styles", content.Element(Office + "automatic-styles"), styles?.Element(Office + "automatic-styles"),
                renames, styles?.Element(Office + "master-styles")));
            Add(document, styles?.Element(Office + "master-styles"));
            Add(document, content.Element(Office + "body"));
            Embed(document, folder, depth);
            return document;
        }

        private static void Add(XElement document, XElement? child)
        {
            if (child is null)
                return;
            if (child.Parent is not null)
                child.Remove();
            document.Add(child);
        }

        /// <summary>
        /// One element with the children of <paramref name="first"/> and <paramref name="second"/>. A child of the second
        /// with the family and style:name of one of the first is renamed, and so is what refers to it in the second and
        /// in <paramref name="master"/>; for font faces, the first one of a name is kept.
        /// </summary>
        private static XElement? Merged(XName name, XElement? first, XElement? second, Dictionary<(string Family, string Name), string> renames,
            XElement? master)
        {
            if (first is null && second is null)
                return null;
            var merged = new XElement(name);
            var fonts = name == Office + "font-face-decls";
            var names = new HashSet<(string, string)>();
            foreach (var child in first?.Elements().ToList() ?? [])
            {
                if ((string?)child.Attribute(Style + "name") is { } styleName)
                    names.Add((fonts ? "" : Family(child), styleName));
                child.Remove();
                merged.Add(child);
            }
            var all = new HashSet<(string, string)>(names);
            foreach (var child in second?.Elements() ?? [])
            {
                if ((string?)child.Attribute(Style + "name") is { } styleName)
                    all.Add((fonts ? "" : Family(child), styleName));
            }
            if (!fonts)
            {
                foreach (var child in second?.Elements() ?? [])
                {
                    if ((string?)child.Attribute(Style + "name") is not { } styleName)
                        continue;
                    var key = (Family(child), styleName);
                    if (!names.Contains(key) || renames.ContainsKey(key))
                        continue;
                    var renamed = "M" + styleName;
                    for (var i = 2; all.Contains((key.Item1, renamed)); i++)
                        renamed = "M" + styleName + "_" + i.ToString(CultureInfo.InvariantCulture);
                    all.Add((key.Item1, renamed));
                    renames[key] = renamed;
                }
            }
            if (renames.Count > 0 && second is not null)
            {
                foreach (var child in second.Elements())
                {
                    if (child.Attribute(Style + "name") is { } own && renames.TryGetValue((Family(child), own.Value), out var renamed))
                        own.Value = renamed;
                }
                foreach (var element in second.Descendants().Concat(master?.DescendantsAndSelf() ?? []))
                {
                    foreach (var attribute in element.Attributes())
                    {
                        if (IsStyleReference(attribute.Name) && Renamed(attribute, renames) is { } renamed)
                            attribute.Value = renamed;
                    }
                }
            }
            foreach (var child in second?.Elements().ToList() ?? [])
            {
                if (fonts && (string?)child.Attribute(Style + "name") is { } font && names.Contains(("", font)))
                    continue;
                child.Remove();
                merged.Add(child);
            }
            return merged;
        }

        /// <summary>The family a style's name is unique in: style:family, else the kind of element (page layouts, lists, data styles).</summary>
        private static string Family(XElement style)
        {
            if (style.Name == Style + "style" || style.Name == Style + "default-style")
                return (string?)style.Attribute(Style + "family") ?? "";
            if (style.Name == Text + "list-style")
                return "list";
            if (style.Name.Namespace == Number)
                return "data";
            return style.Name.LocalName;
        }

        /// <summary>The new name of the style a reference names, or null: the renamed style of a family the attribute can name.</summary>
        private static string? Renamed(XAttribute attribute, Dictionary<(string Family, string Name), string> renames)
        {
            var local = attribute.Name.LocalName;
            // An automatic style's parent is a common style, never one of the automatic ones renamed.
            if (local == "parent-style-name")
                return null;
            var owner = attribute.Parent!.Name.LocalName;
            var space = attribute.Name.Namespace;
            string[]? families = local switch
            {
                "page-layout-name" => ["page-layout"],
                "list-style-name" => ["list"],
                "data-style-name" or "percentage-data-style-name" => ["data"],
                "text-style-name" => ["text", "paragraph"],
                "style-name" when space == Draw => owner is "page" or "master-page" ? ["drawing-page"] : ["graphic"],
                "style-name" when space == Presentation => ["presentation"],
                "style-name" when space == Chart => ["chart"],
                "style-name" when space == Table => [owner is "table" or "table-column" or "table-row" ? owner : "table-cell"],
                "style-name" when space == Text => owner switch
                {
                    "p" or "h" => ["paragraph"],
                    "list" or "list-item" or "numbered-paragraph" => ["list"],
                    "section" => ["section"],
                    "ruby" => ["ruby"],
                    _ => ["text"],
                },
                _ => null,
            };
            if (families is null)
            {
                // A reference of no known family: renamed when one style of that name was.
                var matches = renames.Where(r => r.Key.Name == attribute.Value).Select(r => r.Value).Distinct().ToList();
                return matches.Count == 1 ? matches[0] : null;
            }
            foreach (var family in families)
            {
                if (renames.TryGetValue((family, attribute.Value), out var renamed))
                    return renamed;
            }
            return null;
        }

        /// <summary>An attribute that names a style: text:style-name, style:parent-style-name, style:page-layout-name and the like.</summary>
        private static bool IsStyleReference(XName name) =>
            !name.NamespaceName.Equals(XNamespace.Xmlns.NamespaceName, StringComparison.Ordinal)
            && (name.LocalName.EndsWith("style-name", StringComparison.Ordinal) || name.LocalName == "page-layout-name");

        /// <summary>Replaces links to pictures and objects inside the package with the pictures and objects themselves.</summary>
        private void Embed(XElement document, string folder, int depth)
        {
            foreach (var element in document.Descendants().ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (element.Name == Draw + "object")
                {
                    if (Internal(element) is { } objectFolder && depth < MaxObjectDepth)
                    {
                        var inner = Join(folder + objectFolder.TrimEnd('/') + "/", MediaType(folder + objectFolder.TrimEnd('/') + "/") ?? "application/vnd.oasis.opendocument.chart", depth + 1);
                        if (inner is not null)
                        {
                            Unlink(element);
                            element.AddFirst(inner);
                        }
                    }
                    continue;
                }
                if (!Binaries.Contains(element.Name) || Internal(element) is not { } path || Picture(folder + path) is not { } bytes)
                    continue;
                Count((bytes.Length + 2L) / 3 * 4);
                Unlink(element);
                element.AddFirst(new XElement(Office + "binary-data", System.Convert.ToBase64String(bytes)));
            }
        }

        private static string? Internal(XElement element) => Filee.Engines.Odf.Draw.OdfDrawPackage.InternalPath((string?)element.Attribute(XLink + "href"));

        private static void Unlink(XElement element)
        {
            foreach (var name in new[] { "href", "type", "show", "actuate" })
                element.Attribute(XLink + name)?.Remove();
        }

        private byte[]? Picture(string path)
        {
            if (_pictures.TryGetValue(path, out var known))
                return known;
            if (package.GetEntry(path) is not { } entry)
                return null;
            var bytes = package.ReadBytes(entry, limits.MaxPartBytes)
                        ?? throw DocumentLimits.TooLarge($"The part {path} of the package is larger than {DocumentLimits.Megabytes(limits.MaxPartBytes)}.");
            _pictures[path] = bytes;
            return bytes;
        }

        /// <summary>The media type the manifest gives a folder of the package ("Object 1/"), or null.</summary>
        private string? MediaType(string folder)
        {
            _manifest ??= OdfVariants.LoadManifest(package)?.Root ?? new XElement(Manifest + "manifest");
            return _manifest.Elements(Manifest + "file-entry")
                .FirstOrDefault(e => string.Equals(((string?)e.Attribute(Manifest + "full-path"))?.TrimEnd('/'), folder.TrimEnd('/'), StringComparison.Ordinal))
                ?.Attribute(Manifest + "media-type")?.Value is { Length: > 0 } type ? type : null;
        }

        private void Count(long bytes)
        {
            _stored += bytes;
            if (_stored > MaxBytes(limits))
                throw DocumentLimits.TooLarge($"The flat file would be larger than {DocumentLimits.Megabytes(MaxBytes(limits))}.");
        }

        private XElement? Load(string name)
        {
            if (package.GetEntry(name) is not { } entry)
                return null;
            Count(entry.Length);
            try
            {
                return package.LoadXml(entry, preserveWhitespace: true).Root;
            }
            catch (XmlException ex)
            {
                throw new InvalidDataException($"The file cannot be read ({name} is not valid XML). It may be damaged. {ex.Message}", ex);
            }
        }
    }
}
