// Builds OpenDocument presentations and drawings in code for the tests (no sample files in the repository): a package
// (.odp / .odg: content.xml, styles.xml, meta.xml, the manifest and other parts from bytes) or one flat XML file
// (.fodp / .fodg), from XML fragments. Without master styles of its own it has one master page, "Default", on a
// 28 × 15.75 cm page (presentations) or an A4 one (drawings), with a white background.

using System.IO.Compression;
using System.Text;

namespace Filee.Engines.Tests;

internal sealed class OdpBuilder
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
        "xmlns:svg=\"urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0\" " +
        "xmlns:presentation=\"urn:oasis:names:tc:opendocument:xmlns:presentation:1.0\" " +
        "xmlns:dr3d=\"urn:oasis:names:tc:opendocument:xmlns:dr3d:1.0\" " +
        "xmlns:chart=\"urn:oasis:names:tc:opendocument:xmlns:chart:1.0\"";

    /// <summary>A drawing (office:drawing, Draw) instead of a presentation.</summary>
    public bool Drawing { get; init; }

    /// <summary>Children of office:presentation / office:drawing: draw:page elements (and footer declarations).</summary>
    public string Pages { get; set; } = "";

    /// <summary>office:automatic-styles of content.xml.</summary>
    public string ContentStyles { get; set; } = "";

    /// <summary>office:styles of styles.xml (default and named styles, gradients, markers, ...).</summary>
    public string Styles { get; set; } = "";

    /// <summary>office:automatic-styles of styles.xml (page layouts, styles of master pages).</summary>
    public string StylesAutomatic { get; set; } = "";

    /// <summary>office:master-styles of styles.xml; empty = one master page "Default" with its page layout.</summary>
    public string MasterStyles { get; set; } = "";

    public string? Title { get; set; }

    public string MimeType => Drawing ? "application/vnd.oasis.opendocument.graphics" : "application/vnd.oasis.opendocument.presentation";

    /// <summary>Other parts of the package, by path.</summary>
    public Dictionary<string, byte[]> Parts { get; } = [];

    /// <summary>The page layout and master page used when <see cref="MasterStyles"/> is empty.</summary>
    private string DefaultLayout => Drawing
        ? "<style:page-layout style:name=\"PM1\"><style:page-layout-properties fo:page-width=\"21cm\" fo:page-height=\"29.7cm\"/></style:page-layout>"
        : "<style:page-layout style:name=\"PM1\"><style:page-layout-properties fo:page-width=\"28cm\" fo:page-height=\"15.75cm\"/></style:page-layout>";

    private string Masters => MasterStyles.Length > 0 ? MasterStyles : "<style:master-page style:name=\"Default\" style:page-layout-name=\"PM1\"/>";

    private string Body => Drawing ? $"<office:drawing>{Pages}</office:drawing>" : $"<office:presentation>{Pages}</office:presentation>";

    public string Content() =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-content {Namespaces} office:version=\"1.3\">" +
        $"<office:automatic-styles>{ContentStyles}</office:automatic-styles><office:body>{Body}</office:body></office:document-content>";

    public string StylesPart() =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-styles {Namespaces} office:version=\"1.3\">" +
        $"<office:styles>{Styles}</office:styles><office:automatic-styles>{DefaultLayout}{StylesAutomatic}</office:automatic-styles>" +
        $"<office:master-styles>{Masters}</office:master-styles></office:document-styles>";

    /// <summary>The flat XML file: everything in one office:document (pictures must be office:binary-data).</summary>
    public string Flat() =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document {Namespaces} office:version=\"1.3\" office:mimetype=\"{MimeType}\">" +
        "<office:meta>" + (Title is null ? "" : $"<dc:title>{Title}</dc:title>") + "</office:meta>" +
        $"<office:styles>{Styles}</office:styles><office:automatic-styles>{DefaultLayout}{StylesAutomatic}{ContentStyles}</office:automatic-styles>" +
        $"<office:master-styles>{Masters}</office:master-styles><office:body>{Body}</office:body></office:document>";

    public string Save(string path)
    {
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(zip, "mimetype", Encoding.ASCII.GetBytes(MimeType), CompressionLevel.NoCompression);
            Add(zip, "content.xml", Encoding.UTF8.GetBytes(Content()));
            Add(zip, "styles.xml", Encoding.UTF8.GetBytes(StylesPart()));
            Add(zip, "meta.xml", Encoding.UTF8.GetBytes(
                $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-meta {Namespaces} office:version=\"1.3\"><office:meta>" +
                (Title is null ? "" : $"<dc:title>{Title}</dc:title>") + "</office:meta></office:document-meta>"));
            foreach (var (name, bytes) in Parts)
                Add(zip, name, bytes);
            Add(zip, "META-INF/manifest.xml", Encoding.UTF8.GetBytes(Manifest()));
        }
        return path;
    }

    /// <summary>
    /// META-INF/manifest.xml: the package itself with its version, then every part. LibreOffice refuses an ODF 1.2+
    /// package with a part the manifest doesn't list ("source file could not be loaded").
    /// </summary>
    public string Manifest()
    {
        static string Entry(string path, string type) =>
            $"<manifest:file-entry manifest:full-path=\"{System.Security.SecurityElement.Escape(path)}\" manifest:media-type=\"{type}\"/>";
        var parts = new StringBuilder();
        foreach (var (name, bytes) in Parts)
        {
            // An embedded object (Object 1/content.xml) is a sub-document: its folder is listed with its own type.
            if (name.EndsWith("/content.xml", StringComparison.Ordinal))
            {
                var kind = Encoding.UTF8.GetString(bytes).Contains("<office:chart", StringComparison.Ordinal) ? "chart" : "graphics";
                parts.Append(Entry(name[..(name.Length - "content.xml".Length)], $"application/vnd.oasis.opendocument.{kind}"));
            }
            parts.Append(Entry(name, MediaType(name)));
        }
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?><manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.3\">" +
               $"<manifest:file-entry manifest:full-path=\"/\" manifest:version=\"1.3\" manifest:media-type=\"{MimeType}\"/>" +
               Entry("content.xml", "text/xml") + Entry("styles.xml", "text/xml") + Entry("meta.xml", "text/xml") + parts + "</manifest:manifest>";
    }

    private static string MediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".bmp" => "image/bmp",
        ".wmf" => "image/x-wmf",
        ".emf" => "image/x-emf",
        ".xml" => "text/xml",
        _ => "application/octet-stream",
    };

    public string SaveFlat(string path)
    {
        File.WriteAllText(path, Flat(), new UTF8Encoding(false));
        return path;
    }

    private static void Add(ZipArchive zip, string name, byte[] bytes, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var stream = zip.CreateEntry(name, level).Open();
        stream.Write(bytes);
    }

    // ───────────────────────── Fragments ─────────────────────────

    /// <summary>A page on master "Default".</summary>
    public static string Page(string content, string? style = null) =>
        $"<draw:page draw:name=\"page\" draw:master-page-name=\"Default\"{(style is null ? "" : $" draw:style-name=\"{style}\"")}>{content}</draw:page>";

    /// <summary>A graphic style with graphic properties (and text properties).</summary>
    public static string GraphicStyle(string name, string graphic, string text = "", string family = "graphic") =>
        $"<style:style style:name=\"{name}\" style:family=\"{family}\"><style:graphic-properties {graphic}/>" +
        (text.Length > 0 ? $"<style:text-properties {text}/>" : "") + "</style:style>";

    /// <summary>A picture of one colour as PNG bytes.</summary>
    public static byte[] Png(int width, int height, SkiaSharp.SKColor color)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        bitmap.Erase(color);
        using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
