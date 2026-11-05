// The copy LibreOffice gets has no links to content outside the document (LinkedContent); the user's file stays as
// it is. No LibreOffice needed: LibreOfficeProfileTests convert such documents for real.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Filee.Core.Conversion;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office;

namespace Filee.Engines.Tests;

public class LibreOfficeLinkedContentTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    [Fact]
    public void Office_open_xml_loses_its_external_relationships_but_keeps_hyperlinks()
    {
        var folder = fx.NewFolder();
        var docx = Path.Combine(folder, "links.docx");
        Package(docx, new()
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>",
            ["word/document.xml"] = "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"/>",
            ["word/_rels/document.xml.rels"] = """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/image1.png"/>
                <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="file:///C:/Users/someone/Pictures/id-card.jpg" TargetMode="External"/>
                <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="\\127.0.0.1\nonexistent\x.png" TargetMode="External"/>
                <Relationship Id="rId4" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/oleObject" Target="../outside/sheet.xlsx" TargetMode="external"/>
                <Relationship Id="rId5" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink" Target="https://filee.sh/" TargetMode="External"/>
                </Relationships>
                """,
            ["word/_rels/settings.xml.rels"] = """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/attachedTemplate" Target="http://127.0.0.1:9/t.dotm" TargetMode="External"/>
                </Relationships>
                """,
            ["word/media/image1.png"] = "picture",
        });
        var original = Hash(docx);

        var copy = LinkedContent.Strip(docx, "docx", folder);

        Assert.NotNull(copy);
        Assert.Equal("links.docx", Path.GetFileName(copy));
        Assert.NotEqual(folder, Path.GetDirectoryName(copy));
        Assert.Equal(original, Hash(docx));
        var parts = Read(copy);
        Assert.Contains("rId1", parts["word/_rels/document.xml.rels"]);
        Assert.Contains("https://filee.sh/", parts["word/_rels/document.xml.rels"]);
        Assert.DoesNotContain("id-card", parts["word/_rels/document.xml.rels"]);
        Assert.DoesNotContain("127.0.0.1", parts["word/_rels/document.xml.rels"]);
        Assert.DoesNotContain("outside", parts["word/_rels/document.xml.rels"]);
        Assert.DoesNotContain("attachedTemplate", parts["word/_rels/settings.xml.rels"]);
        Assert.Equal("picture", parts["word/media/image1.png"]);
        Assert.Equal(5, parts.Count);
    }

    [Fact]
    public void Document_without_links_is_used_as_it_is()
    {
        var folder = fx.NewFolder();
        var docx = Path.Combine(folder, "plain.docx");
        Package(docx, new()
        {
            ["[Content_Types].xml"] = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>",
            ["word/_rels/document.xml.rels"] = """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink" Target="https://filee.sh/" TargetMode="External"/>
                </Relationships>
                """,
        });
        var rtf = Path.Combine(folder, "plain.rtf");
        File.WriteAllText(rtf, @"{\rtf1\ansi Hello {\field{\*\fldinst PAGE}{\fldrslt 1}}\par}");
        var doc = Path.Combine(folder, "old.doc");
        File.WriteAllBytes(doc, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0]);

        Assert.Null(LinkedContent.Strip(docx, "docx", folder));
        Assert.Null(LinkedContent.Strip(rtf, "rtf", folder));
        Assert.Null(LinkedContent.Strip(doc, "doc", folder));
    }

    private const string OdfNamespaces =
        "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" " +
        "xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" xmlns:script=\"urn:oasis:names:tc:opendocument:xmlns:script:1.0\" " +
        "xmlns:xlink=\"http://www.w3.org/1999/xlink\"";

    [Fact]
    public void OpenDocument_loses_addresses_outside_the_package()
    {
        var folder = fx.NewFolder();
        var odt = Path.Combine(folder, "links.odt");
        Package(odt, new()
        {
            ["mimetype"] = "application/vnd.oasis.opendocument.text",
            ["content.xml"] = $"""
                <office:document-content {OdfNamespaces}>
                <office:scripts><office:event-listeners><script:event-listener script:event-name="dom:load" xlink:href="vnd.sun.star.script:Standard.Module1.Run?language=Basic&amp;location=document"/></office:event-listeners></office:scripts>
                <office:body><office:text>
                <text:p><draw:frame><draw:image xlink:href="Pictures/inside.png"/></draw:frame></text:p>
                <text:p><draw:frame><draw:image xlink:href="./Object 1"/></draw:frame></text:p>
                <text:p><draw:frame><draw:image xlink:href="../outside/secret.png"/></draw:frame></text:p>
                <text:p><draw:frame><draw:image xlink:href="next-to-it.png"/></draw:frame></text:p>
                <text:p><draw:frame><draw:image xlink:href="file:///C:/Users/someone/secret.png"/></draw:frame></text:p>
                <text:p><draw:frame><draw:image xlink:href="\\127.0.0.1\share\x.png"/></draw:frame></text:p>
                <text:section text:name="S"><text:section-source xlink:href="http://127.0.0.1:9/s.odt"/></text:section>
                <text:p><text:a xlink:href="https://filee.sh/">site</text:a></text:p>
                </office:text></office:body></office:document-content>
                """,
            ["styles.xml"] = $"""<office:document-styles {OdfNamespaces}><office:styles><draw:fill-image draw:name="F" xlink:href="https://example.test/fill.png"/></office:styles></office:document-styles>""",
            ["Pictures/inside.png"] = "picture",
            ["Object 1/content.xml"] = $"<office:document-content {OdfNamespaces}/>",
        });

        var copy = LinkedContent.Strip(odt, "odt", folder);

        Assert.NotNull(copy);
        var parts = Read(copy);
        var content = parts["content.xml"];
        Assert.Contains("Pictures/inside.png", content);
        Assert.Contains("./Object 1", content);
        Assert.Contains("vnd.sun.star.script:Standard.Module1.Run", content);
        Assert.Contains("https://filee.sh/", content);
        foreach (var gone in new[] { "secret.png", "next-to-it.png", "127.0.0.1" })
            Assert.DoesNotContain(gone, content);
        Assert.DoesNotContain("example.test", parts["styles.xml"]);
        Assert.Equal("application/vnd.oasis.opendocument.text", parts["mimetype"]);
        using var zip = ZipFile.OpenRead(copy);
        Assert.Equal("mimetype", zip.Entries[0].FullName); // still first and stored, the OpenDocument signature
        Assert.Equal(zip.Entries[0].Length, zip.Entries[0].CompressedLength);
    }

    [Fact]
    public void Flat_opendocument_has_no_package_so_every_address_is_outside()
    {
        var folder = fx.NewFolder();
        var fodt = Path.Combine(folder, "links.fodt");
        File.WriteAllText(fodt, $"""
            <office:document {OdfNamespaces} office:mimetype="application/vnd.oasis.opendocument.text"><office:body><office:text>
            <text:p><draw:frame><draw:image xlink:href="Pictures/x.png"/></draw:frame><draw:frame><draw:image><office:binary-data>AAAA</office:binary-data></draw:image></draw:frame></text:p>
            </office:text></office:body></office:document>
            """);

        var copy = LinkedContent.Strip(fodt, "fodt", folder);

        Assert.NotNull(copy);
        var text = File.ReadAllText(copy);
        Assert.DoesNotContain("Pictures/x.png", text);
        Assert.Contains("<office:binary-data>AAAA</office:binary-data>", text);
    }

    [Fact]
    public void Html_keeps_only_data_pictures_and_hyperlinks()
    {
        var folder = fx.NewFolder();
        var html = Path.Combine(folder, "page.html");
        File.WriteAllText(html, """
            <html><head><base href="file://127.0.0.1/share/"><link rel="stylesheet" href="../outside/site.css">
            <style>@import "http://127.0.0.1:9/a.css"; body { background: url('file:///C:/secret.png') } p { background: url(data:image/png;base64,AAAA) }</style></head>
            <body background="\\127.0.0.1\share\b.png">
            <img src="../outside/secret.png" alt="a"><img SRC='http://127.0.0.1:9/x.png'><img src=&#x66;ile:///C:/x.png>
            <img src="data:image/png;base64,AAAA" srcset="big.png 2x"><iframe src="other.html"></iframe>
            <p style="background-image: url(&quot;http://127.0.0.1:9/p.png&quot;)">text</p>
            <a href="https://filee.sh/">site</a></body></html>
            """);

        var copy = LinkedContent.Strip(html, "html", folder);

        Assert.NotNull(copy);
        var text = File.ReadAllText(copy);
        foreach (var gone in new[] { "127.0.0.1", "outside", "secret", "x.png", "big.png", "other.html", "<base href" })
            Assert.DoesNotContain(gone, text);
        Assert.Contains("src=\"data:image/png;base64,AAAA\"", text);
        Assert.Contains("url(data:image/png;base64,AAAA)", text);
        Assert.Contains("<a href=\"https://filee.sh/\">site</a>", text);
        Assert.Contains("alt=\"a\"", text);
    }

    [Fact]
    public void Html_pictures_inside_the_page_folder_go_into_the_copy()
    {
        var folder = fx.NewFolder();
        var outside = fx.NewFolder();
        Directory.CreateDirectory(Path.Combine(folder, "img"));
        File.WriteAllBytes(Path.Combine(folder, "img", "beside.png"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(folder, "그림.png"), [4, 5, 6]);
        File.WriteAllBytes(Path.Combine(folder, "drawing.svg"), [7]);
        File.WriteAllBytes(Path.Combine(outside, "secret.png"), [8, 9]);
        var secretUrl = new Uri(Path.Combine(outside, "secret.png")).AbsoluteUri;
        var html = Path.Combine(folder, "page.html");
        File.WriteAllText(html, $"""
            <html><body><img src="img/beside.png" alt="a"><img src="그림.png"><img src="drawing.svg">
            <img src="../{Path.GetFileName(outside)}/secret.png"><img src="{secretUrl}"><img src="\\127.0.0.1\share\x.png"></body></html>
            """, new UTF8Encoding(false));

        var text = File.ReadAllText(LinkedContent.Strip(html, "html", folder)!);

        Assert.Contains($"src=\"data:image/png;base64,{Convert.ToBase64String([1, 2, 3])}\" alt=\"a\"", text);
        Assert.Contains($"src=\"data:image/png;base64,{Convert.ToBase64String([4, 5, 6])}\"", text);
        Assert.Equal(2, Regex.Matches(text, "data:").Count); // not the drawing, the picture outside or the share
        Assert.Equal(6, Regex.Matches(text, "<img").Count);
        foreach (var gone in new[] { "drawing", "secret", "127.0.0.1" })
            Assert.DoesNotContain(gone, text);
    }

    [Fact]
    public void A_page_made_from_a_document_may_use_the_pictures_beside_that_document()
    {
        var folder = fx.NewFolder();
        var work = fx.NewFolder();
        var picture = Path.Combine(folder, "beside.png");
        File.WriteAllBytes(picture, [1, 2, 3]);
        var page = Path.Combine(work, "notes.html");
        File.WriteAllText(page, $"""<html><body><img src="{new Uri(picture).AbsoluteUri}"></body></html>""");

        var alone = File.ReadAllText(LinkedContent.Strip(page, "html", work)!);
        var fromMarkdown = File.ReadAllText(LinkedContent.Strip(page, "html", work, Path.Combine(folder, "notes.md"))!);

        Assert.DoesNotContain("src=", alone);
        Assert.Contains($"src=\"data:image/png;base64,{Convert.ToBase64String([1, 2, 3])}\"", fromMarkdown);
    }

    [Fact]
    public void Parts_over_the_xml_limits_are_refused_before_they_are_loaded()
    {
        var folder = fx.NewFolder();
        var deep = string.Concat(Enumerable.Repeat("<a>", BoundedXml.MaxElementDepth + 1)) + string.Concat(Enumerable.Repeat("</a>", BoundedXml.MaxElementDepth + 1));
        var docx = Path.Combine(folder, "deep.docx");
        Package(docx, new() { ["_rels/.rels"] = deep });
        var odt = Path.Combine(folder, "deep.odt");
        Package(odt, new() { ["mimetype"] = "application/vnd.oasis.opendocument.text", ["content.xml"] = deep });
        var flat = Path.Combine(folder, "deep.fodt");
        File.WriteAllText(flat, deep);

        foreach (var (input, from) in new[] { (docx, "docx"), (odt, "odt"), (flat, "fodt") })
        {
            var refused = Assert.Throws<ConversionRefusedException>(() => LinkedContent.Strip(input, from, folder));
            Assert.Equal("error.document_too_large", refused.ErrorKey);
        }
    }

    [Fact]
    public void Rtf_include_picture_fields_are_renamed_and_their_result_stays()
    {
        var folder = fx.NewFolder();
        var rtf = Path.Combine(folder, "pictures.rtf");
        File.WriteAllText(rtf, """
            {\rtf1\ansi INCLUDEPICTURE in the text\par
            {\field{\*\fldinst {\b INCLUDEPICTURE "\\\\127.0.0.1\\share\\x.png" \\d}}{\fldrslt cached}}\par
            {\field{\*\fldinst includepicture "../outside/secret.png"}{\fldrslt}}\par}
            """, Encoding.Latin1);

        var copy = LinkedContent.Strip(rtf, "rtf", folder);

        Assert.NotNull(copy);
        var text = File.ReadAllText(copy, Encoding.Latin1);
        Assert.StartsWith(@"{\rtf1\ansi INCLUDEPICTURE in the text\par", text);
        Assert.Single(Regex.Matches(text, "INCLUDEPICTURE", RegexOptions.IgnoreCase)); // the one in the text
        Assert.Contains("{\\fldrslt cached}", text);
        Assert.Contains("FILEEREMOVED", text);
    }

    private static void Package(string path, Dictionary<string, string> parts)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in parts)
        {
            var level = name == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
            using var writer = new StreamWriter(zip.CreateEntry(name, level).Open(), new UTF8Encoding(false));
            writer.Write(text);
        }
    }

    private static Dictionary<string, string> Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.ToDictionary(e => e.FullName, e => new StreamReader(e.Open()).ReadToEnd());
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
