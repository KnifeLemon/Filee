// The forms one OpenDocument type comes in (OpenDocument 1.3 Part 2 and Part 3): the package (.odt, .ods, .odp,
// .odg), the template (.ott, .ots, .otp, .otg: the same package with the "-template" media type) and the flat XML file
// (.fodt, .fods, .fodp, .fodg: one office:document with every part and the pictures in it, see OdfFlat). One form turns
// into another of its type without reading the document: a template is the package with its media type changed in
// "mimetype" and the manifest, a flat file is the package's parts put together or taken apart. Everything else in the
// file (macros, settings, thumbnails, objects) stays as it is.
//
// The readers of the package form read the other forms through AsPackage: a template as it is (they take the template
// media type), a flat file as the package it splits into, in a temporary file beside the job's pictures (a template
// package when the flat file says it is a template). Converting a flat file to another form writes the form asked for,
// whatever media type the flat file declares of its type.

using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using OdfNames = Filee.Engines.Hwp.Hwpx.Odt.Odf;

namespace Filee.Engines.Odf;

/// <summary>The form of an OpenDocument file.</summary>
internal enum OdfForm
{
    Package,
    Template,
    Flat,
}

/// <summary>One form of an OpenDocument type: its format id, the format id of its package form, and its media type.</summary>
internal sealed record OdfVariant(string Format, string Family, OdfForm Form, string MimeType, string Kind);

internal static class OdfVariants
{
    public const string SpreadsheetMimeType = "application/vnd.oasis.opendocument.spreadsheet";
    public const string SpreadsheetTemplateMimeType = "application/vnd.oasis.opendocument.spreadsheet-template";

    /// <summary>Every form of every type, the package form first.</summary>
    public static IReadOnlyList<OdfVariant> All { get; } =
    [
        new("odt", "odt", OdfForm.Package, OdfNames.MimeType, "an OpenDocument text"),
        new("ott", "odt", OdfForm.Template, OdfNames.TemplateMimeType, "an OpenDocument text"),
        new("fodt", "odt", OdfForm.Flat, OdfNames.MimeType, "an OpenDocument text"),
        new("ods", "ods", OdfForm.Package, SpreadsheetMimeType, "an OpenDocument spreadsheet"),
        new("ots", "ods", OdfForm.Template, SpreadsheetTemplateMimeType, "an OpenDocument spreadsheet"),
        new("fods", "ods", OdfForm.Flat, SpreadsheetMimeType, "an OpenDocument spreadsheet"),
        new("odp", "odp", OdfForm.Package, OdfNames.PresentationMimeType, "an OpenDocument presentation"),
        new("otp", "odp", OdfForm.Template, OdfNames.PresentationTemplateMimeType, "an OpenDocument presentation"),
        new("fodp", "odp", OdfForm.Flat, OdfNames.PresentationMimeType, "an OpenDocument presentation"),
        new("odg", "odg", OdfForm.Package, OdfNames.GraphicsMimeType, "an OpenDocument drawing"),
        new("otg", "odg", OdfForm.Template, OdfNames.GraphicsTemplateMimeType, "an OpenDocument drawing"),
        new("fodg", "odg", OdfForm.Flat, OdfNames.GraphicsMimeType, "an OpenDocument drawing"),
    ];

    /// <summary>The form of format id <paramref name="format"/>, or null for a format that is none.</summary>
    public static OdfVariant? Find(string format) =>
        All.FirstOrDefault(v => string.Equals(v.Format, format, StringComparison.OrdinalIgnoreCase));

    /// <summary>The media types a file of the type of <paramref name="variant"/> may say it has (document and template).</summary>
    private static IEnumerable<string> MimeTypes(OdfVariant variant) => All.Where(v => v.Family == variant.Family).Select(v => v.MimeType);

    /// <summary>
    /// Writes <paramref name="input"/> (format <paramref name="from"/>) as <paramref name="output"/> in form
    /// <paramref name="to"/> of the same type, replacing it at the end.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is another type, damaged or encrypted.</exception>
    /// <exception cref="Core.Conversion.ConversionRefusedException">The file is past the document limits.</exception>
    public static void Convert(string input, string from, string output, string to, DocumentLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= DocumentLimits.Default;
        var source = Find(from) ?? throw new ArgumentException($"{from} is no OpenDocument format.", nameof(from));
        var target = Find(to) ?? throw new ArgumentException($"{to} is no OpenDocument format.", nameof(to));
        if (source.Family != target.Family || source.Form == target.Form)
            throw new ArgumentException($"{from} doesn't become {to}.", nameof(to));
        if (source.Form == OdfForm.Flat)
        {
            OdfFlat.ToPackage(input, output, target.MimeType, [.. MimeTypes(source)], source.Kind, limits, cancellationToken);
            return;
        }
        if (target.Form == OdfForm.Flat)
            OdfFlat.ToFlat(input, output, target.MimeType, [.. MimeTypes(source)], source.Kind, limits, cancellationToken);
        else
            Retype(input, output, target.MimeType, [.. MimeTypes(source)], source.Kind, limits, cancellationToken);
    }

    /// <summary>
    /// The package a reader of the package form reads for <paramref name="path"/>: the file itself for a package or a
    /// template, a new package in <paramref name="folder"/> for a flat file (removed when the result is disposed).
    /// </summary>
    public static PackageFile AsPackage(string path, string format, string folder, DocumentLimits? limits = null)
    {
        var variant = Find(format) ?? throw new ArgumentException($"{format} is no OpenDocument format.", nameof(format));
        if (variant.Form != OdfForm.Flat)
            return new PackageFile(path, temporary: false);
        Directory.CreateDirectory(folder);
        var package = Path.Combine(folder, $"flat-{Guid.NewGuid():N}.{variant.Family}");
        try
        {
            // A flat file that says it is a template is read as one (the readers take both media types).
            var template = All.First(v => v.Family == variant.Family && v.Form == OdfForm.Template).MimeType;
            OdfFlat.ToPackage(path, package, variant.MimeType, [.. MimeTypes(variant)], variant.Kind, limits ?? DocumentLimits.Default, CancellationToken.None,
                template);
        }
        catch
        {
            WriterTempFile.Discard(package);
            throw;
        }
        return new PackageFile(package, temporary: true);
    }

    /// <summary>Reads <paramref name="path"/> with <paramref name="read"/>, a reader of the package form.</summary>
    public static T Read<T>(string path, string format, string folder, Func<string, T> read)
    {
        using var package = AsPackage(path, format, folder);
        return read(package.Path);
    }

    /// <summary>A package on disk; a temporary one is removed on dispose.</summary>
    internal sealed class PackageFile(string path, bool temporary) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            if (temporary)
                WriterTempFile.Discard(Path);
        }
    }

    // ───────────────────────── Package ↔ template ─────────────────────────

    /// <summary>
    /// Copies the package with <paramref name="mimeType"/> in "mimetype" (first, stored) and on the manifest's root
    /// entry; every other part is copied as it is, read through the document limits.
    /// </summary>
    private static void Retype(string input, string output, string mimeType, IReadOnlyCollection<string> accepted, string kind,
        DocumentLimits limits, CancellationToken cancellationToken)
    {
        using var package = OpenPackage(input, accepted, kind, limits);
        var manifest = Manifest(package, mimeType);
        WriterTempFile.Write(output, file =>
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
            using (var writer = new StreamWriter(zip.CreateEntry("mimetype", CompressionLevel.NoCompression).Open(), new System.Text.UTF8Encoding(false)))
                writer.Write(mimeType);
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mimetype", ManifestPath };
            foreach (var entry in package.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = entry.FullName;
                if (name.EndsWith('/') || name.EndsWith('\\') || !written.Add(name))
                    continue;
                // Pictures stored as they are (PNG, JPEG) stay stored: deflating them again gains nothing.
                var level = entry.CompressedLength >= entry.Length ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                var copy = zip.CreateEntry(name, level);
                if (entry.LastWriteTime.Year is >= 1980 and <= 2107)
                    copy.LastWriteTime = entry.LastWriteTime;
                using var source = package.Open(entry);
                using var target = copy.Open();
                source.CopyTo(target);
            }
            using var part = zip.CreateEntry(ManifestPath, CompressionLevel.Optimal).Open();
            using var xml = XmlWriter.Create(part, new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false) });
            manifest.Save(xml);
        });
    }

    internal const string ManifestPath = "META-INF/manifest.xml";

    /// <summary>
    /// Opens a package of the type named by <paramref name="kind"/>: one whose "mimetype" is another type's, or whose
    /// parts are encrypted, is refused with its reason.
    /// </summary>
    internal static DocumentPackage OpenPackage(string path, IReadOnlyCollection<string> accepted, string kind, DocumentLimits limits)
    {
        var package = DocumentPackage.Open(path, limits,
            () => new InvalidDataException($"The file is not {kind} (it is not a ZIP package). It may be damaged."));
        try
        {
            if (package.GetEntry("mimetype") is { } mimetype && package.ReadBytes(mimetype, 256) is { } bytes)
            {
                var type = System.Text.Encoding.ASCII.GetString(bytes).Trim();
                if (type.Length > 0 && !accepted.Contains(type))
                    throw new InvalidDataException($"The file is not {kind} ({type}).");
            }
            if (package.GetEntry("content.xml") is null)
                throw new InvalidDataException($"The file is not {kind} (no content.xml).");
            if (LoadManifest(package)?.Descendants(OdfNames.Manifest + "encryption-data").Any() == true)
                throw new InvalidDataException("The file is protected with a password (encrypted). Open it in LibreOffice, remove the password (File → Properties → Security), save it and convert it again.");
            return package;
        }
        catch
        {
            package.Dispose();
            throw;
        }
    }

    /// <summary>The manifest, or null when the package has none; a damaged one is invalid data.</summary>
    internal static XDocument? LoadManifest(DocumentPackage package)
    {
        if (package.GetEntry(ManifestPath) is not { } entry)
            return null;
        try
        {
            return package.LoadXml(entry, preserveWhitespace: false);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"The file cannot be read ({ManifestPath} is not valid XML). It may be damaged. {ex.Message}", ex);
        }
    }

    /// <summary>The package's manifest with <paramref name="mimeType"/> on its root entry; a new one when it has none.</summary>
    private static XDocument Manifest(DocumentPackage package, string mimeType)
    {
        var m = OdfNames.Manifest;
        var manifest = LoadManifest(package) is { Root: { } root } loaded && root.Name == m + "manifest"
            ? loaded
            : new XDocument(new XElement(m + "manifest", new XAttribute(XNamespace.Xmlns + "manifest", m.NamespaceName),
                new XAttribute(m + "version", OdfNames.Version)));
        manifest.Declaration = new XDeclaration("1.0", "UTF-8", null);
        var entry = manifest.Root!.Elements(m + "file-entry").FirstOrDefault(e => (string?)e.Attribute(m + "full-path") == "/");
        if (entry is null)
        {
            entry = new XElement(m + "file-entry", new XAttribute(m + "full-path", "/"));
            manifest.Root.AddFirst(entry);
        }
        entry.SetAttributeValue(m + "media-type", mimeType);
        return manifest;
    }
}
