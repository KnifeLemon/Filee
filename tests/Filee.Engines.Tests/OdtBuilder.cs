// Builds ODT packages in code for the tests (no sample files in the repository): content.xml, styles.xml, meta.xml
// and the manifest from XML fragments, pictures and other parts from bytes.

using System.IO.Compression;
using System.Text;

namespace Filee.Engines.Tests;

internal sealed class OdtBuilder
{
    public const string Namespaces =
        "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" " +
        "xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" " +
        "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" " +
        "xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" " +
        "xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" " +
        "xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\" " +
        "xmlns:xlink=\"http://www.w3.org/1999/xlink\" " +
        "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
        "xmlns:meta=\"urn:oasis:names:tc:opendocument:xmlns:meta:1.0\" " +
        "xmlns:svg=\"urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0\"";

    /// <summary>Children of office:text.</summary>
    public string Body { get; set; } = "";

    /// <summary>office:automatic-styles of content.xml.</summary>
    public string ContentStyles { get; set; } = "";

    /// <summary>office:styles of styles.xml.</summary>
    public string Styles { get; set; } = "";

    /// <summary>office:automatic-styles of styles.xml (page layouts, styles of headers and footers).</summary>
    public string StylesAutomatic { get; set; } = "";

    /// <summary>office:master-styles of styles.xml.</summary>
    public string MasterStyles { get; set; } = "";

    public string? Title { get; set; }

    public string MimeType { get; set; } = "application/vnd.oasis.opendocument.text";

    /// <summary>More manifest:file-entry elements (encryption data, ...).</summary>
    public string ManifestEntries { get; set; } = "";

    /// <summary>content.xml as it is, instead of the one made of the fragments.</summary>
    public byte[]? RawContent { get; set; }

    /// <summary>Other parts of the package, by path.</summary>
    public Dictionary<string, byte[]> Parts { get; } = [];

    public string Content() =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-content {Namespaces} office:version=\"1.3\">" +
        $"<office:automatic-styles>{ContentStyles}</office:automatic-styles><office:body><office:text>{Body}</office:text></office:body></office:document-content>";

    public string Save(string path)
    {
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(zip, "mimetype", Encoding.ASCII.GetBytes(MimeType), CompressionLevel.NoCompression);
            Add(zip, "content.xml", RawContent ?? Encoding.UTF8.GetBytes(Content()));
            Add(zip, "styles.xml", Encoding.UTF8.GetBytes(
                $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-styles {Namespaces} office:version=\"1.3\">" +
                $"<office:styles>{Styles}</office:styles><office:automatic-styles>{StylesAutomatic}</office:automatic-styles>" +
                $"<office:master-styles>{MasterStyles}</office:master-styles></office:document-styles>"));
            Add(zip, "meta.xml", Encoding.UTF8.GetBytes(
                $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-meta {Namespaces} office:version=\"1.3\"><office:meta>" +
                (Title is null ? "" : $"<dc:title>{Title}</dc:title>") + "</office:meta></office:document-meta>"));
            foreach (var (name, bytes) in Parts)
                Add(zip, name, bytes);
            Add(zip, "META-INF/manifest.xml", Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.3\">" +
                $"<manifest:file-entry manifest:full-path=\"/\" manifest:media-type=\"{MimeType}\"/>" +
                "<manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/>" +
                ManifestEntries + "</manifest:manifest>"));
        }
        return path;
    }

    private static void Add(ZipArchive zip, string name, byte[] bytes, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var stream = zip.CreateEntry(name, level).Open();
        stream.Write(bytes);
    }
}
