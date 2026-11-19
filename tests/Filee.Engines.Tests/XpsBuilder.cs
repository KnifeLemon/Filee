// Writes XPS and OpenXPS packages in code for the XPS reader's tests: the package relationships, a fixed document
// sequence with one fixed document, its pages (FixedPage markup given by the test), fonts (plain or obfuscated as
// .odttf), pictures and other parts. Pages can be written as interleaved pieces.

using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Filee.Engines.Tests;

internal sealed class XpsBuilder
{
    public const string XpsNamespace = "http://schemas.microsoft.com/xps/2005/06";
    public const string OpenXpsNamespace = "http://schemas.openxps.org/oxps/v1.0";

    private readonly List<(string Body, double Width, double Height)> _pages = [];
    private readonly Dictionary<string, byte[]> _parts = new(StringComparer.Ordinal);
    private int _resources;

    /// <summary>Writes the OpenXPS (ECMA-388) namespaces instead of XPS 1.0's.</summary>
    public bool OpenXps { get; init; }

    /// <summary>Writes each page as this many interleaved pieces (0 = whole).</summary>
    public int Pieces { get; init; }

    public string? Title { get; init; }

    /// <summary>LinkTarget names of the first page.</summary>
    public List<string> LinkTargets { get; } = [];

    public string Namespace => OpenXps ? OpenXpsNamespace : XpsNamespace;

    private string RelationshipBase => OpenXps ? "http://schemas.openxps.org/oxps/v1.0" : "http://schemas.microsoft.com/xps/2005/06";

    /// <summary>Adds a page whose FixedPage holds <paramref name="body"/> (in the page's default namespace).</summary>
    public XpsBuilder Page(string body, double width = 816, double height = 1056)
    {
        _pages.Add((body, width, height));
        return this;
    }

    /// <summary>Adds a part; returns its absolute name ("/Resources/…").</summary>
    public string Part(string name, byte[] data)
    {
        _parts[name.TrimStart('/')] = data;
        return "/" + name.TrimStart('/');
    }

    /// <summary>Adds a picture; returns its part name.</summary>
    public string Image(byte[] data, string extension) => Part($"Resources/Images/image{++_resources}.{extension}", data);

    /// <summary>Adds a font, obfuscated as an .odttf part under a new GUID when <paramref name="obfuscate"/>; returns its part name.</summary>
    public string Font(byte[] font, bool obfuscate = true)
    {
        if (!obfuscate)
            return Part($"Resources/Fonts/font{++_resources}.ttf", font);
        var guid = Guid.NewGuid();
        var hex = guid.ToString("N", CultureInfo.InvariantCulture);
        var key = new byte[16];
        for (var i = 0; i < 16; i++)
            key[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var bytes = (byte[])font.Clone();
        for (var i = 0; i < 32; i++)
            bytes[i] ^= key[15 - i % 16];
        return Part($"Resources/Fonts/{guid.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant()}.odttf", bytes);
    }

    /// <summary>Adds a remote resource dictionary holding <paramref name="entries"/>; returns its part name.</summary>
    public string Dictionary(string entries) =>
        Part($"Resources/dictionary{++_resources}.dict", Encoding.UTF8.GetBytes(
            $"<ResourceDictionary xmlns='{Namespace}' xmlns:x='{KeyNamespace}'>{entries}</ResourceDictionary>"));

    /// <summary>The namespace of x:Key in resource dictionaries.</summary>
    public string KeyNamespace => OpenXps ? "http://schemas.openxps.org/oxps/v1.0/resourcedictionary-key" : "http://schemas.microsoft.com/xps/2005/06/resourcedictionary-key";

    public byte[] ToBytes()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "[Content_Types].xml",
                "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'>" +
                "<Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/>" +
                "<Default Extension='fdseq' ContentType='application/vnd.ms-package.xps-fixeddocumentsequence+xml'/>" +
                "<Default Extension='fdoc' ContentType='application/vnd.ms-package.xps-fixeddocument+xml'/>" +
                "<Default Extension='fpage' ContentType='application/vnd.ms-package.xps-fixedpage+xml'/>" +
                "<Default Extension='odttf' ContentType='application/vnd.ms-package.obfuscated-opentype'/>" +
                "<Default Extension='png' ContentType='image/png'/><Default Extension='jpg' ContentType='image/jpeg'/></Types>");
            var core = Title is null ? "" :
                "<Relationship Id='R1' Type='http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties' Target='/docProps/core.xml'/>";
            Write(zip, "_rels/.rels",
                "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'>" +
                $"<Relationship Id='R0' Type='{RelationshipBase}/fixedrepresentation' Target='/FixedDocumentSequence.fdseq'/>{core}</Relationships>");
            if (Title is not null)
            {
                Write(zip, "docProps/core.xml",
                    "<cp:coreProperties xmlns:cp='http://schemas.openxmlformats.org/package/2006/metadata/core-properties' xmlns:dc='http://purl.org/dc/elements/1.1/'>" +
                    $"<dc:title>{Title}</dc:title></cp:coreProperties>");
            }
            Write(zip, "FixedDocumentSequence.fdseq",
                $"<FixedDocumentSequence xmlns='{Namespace}'><DocumentReference Source='Documents/1/FixedDocument.fdoc'/></FixedDocumentSequence>");
            var document = new StringBuilder($"<FixedDocument xmlns='{Namespace}'>");
            for (var i = 0; i < _pages.Count; i++)
            {
                document.Append(CultureInfo.InvariantCulture, $"<PageContent Source='Pages/{i + 1}.fpage'>");
                if (i == 0 && LinkTargets.Count > 0)
                    document.Append("<PageContent.LinkTargets>").Append(string.Concat(LinkTargets.Select(t => $"<LinkTarget Name='{t}'/>"))).Append("</PageContent.LinkTargets>");
                document.Append("</PageContent>");
            }
            Write(zip, "Documents/1/FixedDocument.fdoc", document.Append("</FixedDocument>").ToString());
            for (var i = 0; i < _pages.Count; i++)
            {
                var (body, width, height) = _pages[i];
                var page = string.Create(CultureInfo.InvariantCulture,
                    $"<FixedPage xmlns='{Namespace}' xmlns:x='{KeyNamespace}' Width='{width}' Height='{height}' xml:lang='en-US'>{body}</FixedPage>");
                var name = $"Documents/1/Pages/{i + 1}.fpage";
                if (Pieces <= 1)
                {
                    Write(zip, name, page);
                    continue;
                }
                var bytes = Encoding.UTF8.GetBytes(page);
                var size = (bytes.Length + Pieces - 1) / Pieces;
                for (var p = 0; p < Pieces; p++)
                {
                    var piece = bytes.AsSpan(Math.Min(p * size, bytes.Length), Math.Max(0, Math.Min(size, bytes.Length - p * size)));
                    var entry = zip.CreateEntry($"{name}/[{p}]{(p == Pieces - 1 ? ".last" : "")}.piece");
                    using var stream = entry.Open();
                    stream.Write(piece);
                }
            }
            foreach (var (name, data) in _parts)
            {
                var entry = zip.CreateEntry(name);
                using var stream = entry.Open();
                stream.Write(data);
            }
        }
        return buffer.ToArray();
    }

    public string Save(string path)
    {
        File.WriteAllBytes(path, ToBytes());
        return path;
    }

    private static void Write(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(text));
    }
}
