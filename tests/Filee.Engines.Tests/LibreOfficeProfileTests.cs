// LibreOffice's worker profiles: no macros, no link updates. Documents that point outside (an attached template, a DDE
// link, a linked picture, a picture file next to the document) still convert, LibreOffice doesn't connect to what they
// point at, and no outside picture ends up in the result. The address is a listener on the loopback interface that only
// this test knows.

using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Office;
using ImageMagick;
using UglyToad.PdfPig;

namespace Filee.Engines.Tests;

public class LibreOfficeProfileTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly XNamespace Oor = "http://openoffice.org/2001/registry";

    private IConverter LibreOffice => fx.Converters.Single(c => c.Id == "libreoffice");

    // ───────── The profile's configuration ─────────

    [Fact]
    public void New_profile_gets_every_setting()
    {
        var profile = fx.NewFolder();

        LibreOfficeProfile.Apply(profile);

        var values = Values(profile);
        Assert.Equal("3", values[("/org.openoffice.Office.Common/Security/Scripting", "MacroSecurityLevel")]);
        Assert.Equal("true", values[("/org.openoffice.Office.Common/Security/Scripting", "DisableMacrosExecution")]);
        Assert.Equal("true", values[("/org.openoffice.Office.Common/Security/Scripting", "BlockUntrustedRefererLinks")]);
        Assert.Equal("", values[("/org.openoffice.Office.Common/Security/Scripting", "SecureURL")]);
        Assert.Equal("2", values[("/org.openoffice.Office.Writer/Content/Update", "Link")]); // never
        Assert.Equal("1", values[("/org.openoffice.Office.Calc/Content/Update", "Link")]); // never
        Assert.Equal("false", values[("/org.openoffice.Office.Writer/Filter/Import/VBA", "Executable")]);
        Assert.Equal("false", values[("/org.openoffice.Office.Calc/Filter/Import/VBA", "Executable")]);
    }

    [Fact]
    public void Settings_replace_other_values_and_keep_the_rest()
    {
        var profile = fx.NewFolder();
        var file = LibreOfficeProfile.ConfigurationFile(profile);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """
            <?xml version="1.0" encoding="UTF-8"?>
            <oor:items xmlns:oor="http://openoffice.org/2001/registry" xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
            <item oor:path="/org.openoffice.Office.Common/Misc"><prop oor:name="UseOpenCL" oor:op="fuse"><value>false</value></prop></item>
            <item oor:path="/org.openoffice.Office.Common/Security/Scripting"><prop oor:name="MacroSecurityLevel" oor:op="fuse"><value>0</value></prop></item>
            <item oor:path="/org.openoffice.Office.Common/Security/Scripting"><prop oor:name="SecureURL" oor:op="fuse" oor:type="oor:string-list"><value><it>file:///</it></value></prop></item>
            <item oor:path="/org.openoffice.Office.Writer/Content/Update"><prop oor:name="Link" oor:op="fuse"><value>0</value></prop></item>
            </oor:items>
            """);

        LibreOfficeProfile.Apply(profile);

        var values = Values(profile);
        Assert.Equal("false", values[("/org.openoffice.Office.Common/Misc", "UseOpenCL")]);
        Assert.Equal("3", values[("/org.openoffice.Office.Common/Security/Scripting", "MacroSecurityLevel")]);
        Assert.Equal("", values[("/org.openoffice.Office.Common/Security/Scripting", "SecureURL")]);
        Assert.Equal("2", values[("/org.openoffice.Office.Writer/Content/Update", "Link")]);
        Assert.Equal(LibreOfficeProfile.Settings.Length + 1, Items(profile).Count());
    }

    [Fact]
    public void Profile_that_has_the_settings_is_left_as_it_is()
    {
        var profile = fx.NewFolder();
        LibreOfficeProfile.Apply(profile);
        var file = LibreOfficeProfile.ConfigurationFile(profile);
        var written = File.GetLastWriteTimeUtc(file);
        File.SetLastWriteTimeUtc(file, written.AddMinutes(-5));

        LibreOfficeProfile.Apply(profile);

        Assert.Equal(written.AddMinutes(-5), File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void Unreadable_configuration_is_started_over()
    {
        var profile = fx.NewFolder();
        var file = LibreOfficeProfile.ConfigurationFile(profile);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "<oor:items cut off");

        LibreOfficeProfile.Apply(profile);

        Assert.Equal("true", Values(profile)[("/org.openoffice.Office.Common/Security/Scripting", "DisableMacrosExecution")]);
    }

    private static IEnumerable<XElement> Items(string profile) =>
        XDocument.Load(LibreOfficeProfile.ConfigurationFile(profile)).Root!.Elements("item");

    private static Dictionary<(string Path, string Name), string> Values(string profile) =>
        Items(profile).ToDictionary(
            item => ((string)item.Attribute(Oor + "path")!, (string)item.Element("prop")!.Attribute(Oor + "name")!),
            item => item.Element("prop")!.Element("value")!.Value);

    // ───────── Macros ─────────

    [Fact]
    public void Macros_are_found_where_the_formats_keep_them()
    {
        var folder = fx.NewFolder();
        string Package(string name, params string[] entries)
        {
            var path = Path.Combine(folder, name);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var entry in entries)
                using (var writer = new StreamWriter(zip.CreateEntry(entry).Open()))
                    writer.Write("x");
            return path;
        }
        string Compound(string name, string text)
        {
            var path = Path.Combine(folder, name);
            File.WriteAllBytes(path, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, .. new byte[3000], .. Encoding.Unicode.GetBytes(text)]);
            return path;
        }

        Assert.True(OfficeMacros.In(Package("macro.docm", "[Content_Types].xml", "word/document.xml", "word/vbaProject.bin")));
        Assert.True(OfficeMacros.In(Package("macro.xlsm", "[Content_Types].xml", "xl/vbaProject.bin")));
        Assert.True(OfficeMacros.In(Package("macro.odt", "mimetype", "content.xml", "Basic/script-lc.xml", "Basic/Standard/script-lb.xml", "Basic/Standard/Module1.xml")));
        Assert.True(OfficeMacros.In(Package("script.ods", "mimetype", "content.xml", "Scripts/python/run.py")));
        Assert.True(OfficeMacros.In(Compound("macro.xls", "_VBA_PROJECT_CUR")));
        Assert.False(OfficeMacros.In(Package("plain.docx", "[Content_Types].xml", "word/document.xml")));
        Assert.False(OfficeMacros.In(Package("plain.odt", "mimetype", "content.xml", "Basic/script-lc.xml")));
        Assert.False(OfficeMacros.In(Compound("plain.doc", "WordDocument")));
        Assert.False(OfficeMacros.In(Path.Combine(folder, "missing.doc")));
    }

    [Fact]
    public async Task Document_with_macros_converts_and_says_they_were_not_run()
    {
        SkipUnlessInstalled();
        var folder = fx.NewFolder();
        // A Basic module bound to the document's open event: it would write a file if it ran.
        var marker = Path.Combine(folder, "ran.txt");
        var odt = Path.Combine(folder, "macro.odt");
        using (var zip = ZipFile.Open(odt, ZipArchiveMode.Create))
        {
            Entry(zip, "mimetype", "application/vnd.oasis.opendocument.text", CompressionLevel.NoCompression);
            Entry(zip, "META-INF/manifest.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0" manifest:version="1.3">
                <manifest:file-entry manifest:full-path="/" manifest:media-type="application/vnd.oasis.opendocument.text"/>
                <manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml"/>
                <manifest:file-entry manifest:full-path="Basic/script-lc.xml" manifest:media-type="text/xml"/>
                <manifest:file-entry manifest:full-path="Basic/Standard/script-lb.xml" manifest:media-type="text/xml"/>
                <manifest:file-entry manifest:full-path="Basic/Standard/Module1.xml" manifest:media-type="text/xml"/>
                </manifest:manifest>
                """);
            Entry(zip, "content.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:script="urn:oasis:names:tc:opendocument:xmlns:script:1.0" xmlns:xlink="http://www.w3.org/1999/xlink" office:version="1.3">
                <office:scripts><office:event-listeners>
                <script:event-listener script:language="ooo:script" script:event-name="dom:load" xlink:href="vnd.sun.star.script:Standard.Module1.Run?language=Basic&amp;location=document"/>
                </office:event-listeners></office:scripts>
                <office:body><office:text><text:p>Hello macro</text:p></office:text></office:body></office:document-content>
                """);
            Entry(zip, "Basic/script-lc.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <library:libraries xmlns:library="http://openoffice.org/2000/library" xmlns:xlink="http://www.w3.org/1999/xlink">
                <library:library library:name="Standard" xlink:href="vnd.sun.star.expand:$(ODF_DOCUMENT)/Basic/Standard/script-lb.xml/" xlink:type="simple" library:link="false"/>
                </library:libraries>
                """);
            Entry(zip, "Basic/Standard/script-lb.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <library:library xmlns:library="http://openoffice.org/2000/library" library:name="Standard" library:readonly="false" library:passwordprotected="false">
                <library:element library:name="Module1"/>
                </library:library>
                """);
            Entry(zip, "Basic/Standard/Module1.xml", $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <script:module xmlns:script="http://openoffice.org/2000/script" script:name="Module1" script:language="StarBasic">Sub Run
                  n = FreeFile : Open "{marker}" For Output As #n : Print #n, "ran" : Close #n
                End Sub
                </script:module>
                """);
        }

        var (pdf, notice) = await ConvertAsync(odt, "odt", "pdf");

        using (var document = PdfDocument.Open(pdf))
            Assert.Contains("Hello macro", document.GetPage(1).Text);
        Assert.False(File.Exists(marker), "the document's macro ran");
        Assert.Equal("notice.macros_not_run", notice);
    }

    // ───────── Links ─────────

    [Fact]
    public async Task Docx_with_an_external_template_dde_field_and_linked_picture_converts_without_connecting()
    {
        SkipUnlessInstalled();
        using var listener = new Listener();
        var url = $"http://127.0.0.1:{listener.Port}";
        var docx = Path.Combine(fx.NewFolder(), "links.docx");
        using (var zip = ZipFile.Open(docx, ZipArchiveMode.Create))
        {
            Entry(zip, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                <Default Extension="xml" ContentType="application/xml"/>
                <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
                <Override PartName="/word/settings.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml"/>
                </Types>
                """);
            Entry(zip, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
                </Relationships>
                """);
            Entry(zip, "word/_rels/document.xml.rels", $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings" Target="settings.xml"/>
                <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="{url}/picture.png" TargetMode="External"/>
                </Relationships>
                """);
            Entry(zip, "word/_rels/settings.xml.rels", $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/attachedTemplate" Target="{url}/template.dotm" TargetMode="External"/>
                </Relationships>
                """);
            Entry(zip, "word/settings.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <w:settings xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                <w:attachedTemplate r:id="rId1"/><w:linkStyles/>
                </w:settings>
                """);
            Entry(zip, "word/document.xml", $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"
                  xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                  xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture">
                <w:body>
                <w:p><w:r><w:t>Hello links</w:t></w:r></w:p>
                <w:p><w:r><w:fldChar w:fldCharType="begin"/></w:r><w:r><w:instrText xml:space="preserve"> DDEAUTO soffice "{url}/data.ods" "Sheet1.A1" </w:instrText></w:r>
                <w:r><w:fldChar w:fldCharType="separate"/></w:r><w:r><w:t>cached value</w:t></w:r><w:r><w:fldChar w:fldCharType="end"/></w:r></w:p>
                <w:p><w:r><w:drawing><wp:inline><wp:extent cx="914400" cy="914400"/><wp:docPr id="1" name="Picture 1"/>
                <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic>
                <pic:nvPicPr><pic:cNvPr id="1" name="picture.png"/><pic:cNvPicPr/></pic:nvPicPr>
                <pic:blipFill><a:blip r:link="rId2"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>
                <pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="914400" cy="914400"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr>
                </pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>
                </w:body></w:document>
                """);
        }

        var (pdf, _) = await ConvertAsync(docx, "docx", "pdf");

        using (var document = PdfDocument.Open(pdf))
            Assert.Contains("Hello links", document.GetPage(1).Text);
        Assert.Equal(0, listener.Connections());
    }

    [Fact]
    public async Task OpenDocument_with_a_dde_link_and_linked_picture_converts_without_connecting()
    {
        SkipUnlessInstalled();
        using var listener = new Listener();
        var url = $"http://127.0.0.1:{listener.Port}";
        var fodt = Path.Combine(fx.NewFolder(), "links.fodt");
        await File.WriteAllTextAsync(fodt, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0"
              xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0" xmlns:svg="urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0"
              xmlns:xlink="http://www.w3.org/1999/xlink" office:version="1.3" office:mimetype="application/vnd.oasis.opendocument.text">
            <office:body><office:text>
            <text:dde-connection-decls>
            <text:dde-connection-decl office:name="Data" office:dde-application="soffice" office:dde-topic="{url}/data.ods" office:dde-item="Sheet1.A1" office:automatic-update="true"/>
            </text:dde-connection-decls>
            <text:p>Hello DDE</text:p>
            <text:p><text:dde-connection text:connection-name="Data">cached value</text:dde-connection></text:p>
            <text:p><draw:frame draw:name="Picture" text:anchor-type="as-char" svg:width="2cm" svg:height="2cm">
            <draw:image xlink:href="{url}/picture.png" xlink:type="simple" xlink:show="embed" xlink:actuate="onLoad"/></draw:frame></text:p>
            <text:section text:name="Included"><text:section-source xlink:href="{url}/section.odt" xlink:type="simple"/><text:p>cached section</text:p></text:section>
            </office:text></office:body></office:document>
            """, TestContext.Current.CancellationToken);

        var (pdf, _) = await ConvertAsync(fodt, "fodt", "pdf");

        using (var document = PdfDocument.Open(pdf))
            Assert.Contains("Hello DDE", document.GetPage(1).Text);
        Assert.Equal(0, listener.Connections());
    }

    [Fact]
    public async Task Linked_pictures_outside_the_document_stay_out_of_the_result()
    {
        SkipUnlessInstalled();
        var root = fx.NewFolder();
        var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        var secret = EngineFixture.MakeImage(outside, "secret.png", MagickFormat.Png);
        var secretUrl = new Uri(secret).AbsoluteUri;
        var folder = Directory.CreateDirectory(Path.Combine(root, "inbox")).FullName;

        // Word: the picture as an absolute file URL, a path relative to the document, and a share.
        var docx = Path.Combine(folder, "pictures.docx");
        using (var zip = ZipFile.Open(docx, ZipArchiveMode.Create))
        {
            Entry(zip, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                <Default Extension="xml" ContentType="application/xml"/>
                <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
                </Types>
                """);
            Entry(zip, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
                </Relationships>
                """);
            Entry(zip, "word/_rels/document.xml.rels", $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="{secretUrl}" TargetMode="External"/>
                <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="../outside/secret.png" TargetMode="External"/>
                <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="\\127.0.0.1\nonexistent\x.png" TargetMode="External"/>
                </Relationships>
                """);
            Entry(zip, "word/document.xml", $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"
                  xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                  xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture">
                <w:body><w:p><w:r><w:t>Hello pictures</w:t></w:r></w:p>{LinkedPicture("rId1", 1)}{LinkedPicture("rId2", 2)}{LinkedPicture("rId3", 3)}</w:body></w:document>
                """);
        }

        // OpenDocument: a package whose pictures lie outside it.
        var odt = Path.Combine(folder, "pictures.odt");
        using (var zip = ZipFile.Open(odt, ZipArchiveMode.Create))
        {
            Entry(zip, "mimetype", "application/vnd.oasis.opendocument.text", CompressionLevel.NoCompression);
            Entry(zip, "META-INF/manifest.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0" manifest:version="1.3">
                <manifest:file-entry manifest:full-path="/" manifest:media-type="application/vnd.oasis.opendocument.text"/>
                <manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml"/>
                </manifest:manifest>
                """);
            Entry(zip, "content.xml", $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0"
                  xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0" xmlns:svg="urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0"
                  xmlns:xlink="http://www.w3.org/1999/xlink" office:version="1.3">
                <office:body><office:text><text:p>Hello pictures</text:p>
                <text:p><draw:frame draw:name="A" text:anchor-type="as-char" svg:width="2cm" svg:height="2cm"><draw:image xlink:href="../outside/secret.png" xlink:type="simple" xlink:show="embed" xlink:actuate="onLoad"/></draw:frame></text:p>
                <text:p><draw:frame draw:name="B" text:anchor-type="as-char" svg:width="2cm" svg:height="2cm"><draw:image xlink:href="{secretUrl}" xlink:type="simple" xlink:show="embed" xlink:actuate="onLoad"/></draw:frame></text:p>
                </office:text></office:body></office:document-content>
                """);
        }

        // RTF: LibreOffice's RTF reader loads an INCLUDEPICTURE field's file by itself, whatever the profile says.
        var rtf = Path.Combine(folder, "pictures.rtf");
        var escaped = secret.Replace(@"\", @"\\");
        await File.WriteAllTextAsync(rtf, @"{\rtf1\ansi Hello pictures\par {\field{\*\fldinst INCLUDEPICTURE """ + escaped + @""" \\d}{\fldrslt }}\par}",
            Encoding.ASCII, TestContext.Current.CancellationToken);

        // HTML: pictures beside the page's folder.
        var html = Path.Combine(folder, "pictures.html");
        await File.WriteAllTextAsync(html, $"""<html><body><p>Hello pictures</p><img src="../outside/secret.png" width="64" height="64"><img src="{secretUrl}"></body></html>""",
            TestContext.Current.CancellationToken);

        foreach (var (input, from) in new[] { (docx, "docx"), (odt, "odt"), (rtf, "rtf"), (html, "html") })
        {
            var (pdf, _) = await ConvertAsync(input, from, "pdf");
            using var document = PdfDocument.Open(pdf);
            Assert.Contains("Hello pictures", document.GetPage(1).Text);
            Assert.True(document.GetPages().All(p => !p.GetImages().Any()), $"{from}: the picture outside the document is in the result");
        }
    }

    [Fact]
    public async Task A_picture_inside_the_page_folder_is_in_the_result_and_one_outside_is_not()
    {
        SkipUnlessInstalled();
        var root = fx.NewFolder();
        var folder = Directory.CreateDirectory(Path.Combine(root, "page")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        using (var image = new MagickImage(MagickColors.Red, 64, 64))
        {
            image.Write(Path.Combine(folder, "beside.png"));
            image.Write(Path.Combine(outside, "secret.png"));
        }
        var html = Path.Combine(folder, "pictures.html");
        await File.WriteAllTextAsync(html, """<html><body><p>Hello pictures</p><img src="beside.png" width="64" height="64"><img src="../outside/secret.png" width="64" height="64"></body></html>""",
            TestContext.Current.CancellationToken);

        var (pdf, _) = await ConvertAsync(html, "html", "pdf");

        using var document = PdfDocument.Open(pdf);
        Assert.Single(document.GetPages().SelectMany(p => p.GetImages()));
    }

    private static string LinkedPicture(string relationship, int id) => $"""
        <w:p><w:r><w:drawing><wp:inline><wp:extent cx="914400" cy="914400"/><wp:docPr id="{id}" name="Picture {id}"/>
        <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic>
        <pic:nvPicPr><pic:cNvPr id="{id}" name="picture.png"/><pic:cNvPicPr/></pic:nvPicPr>
        <pic:blipFill><a:blip r:link="{relationship}"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>
        <pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="914400" cy="914400"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr>
        </pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>
        """;

    // ───────── Helpers ─────────

    private void SkipUnlessInstalled() =>
        Assert.SkipUnless(fx.Catalog.StatusOf(LibreOffice).IsAvailable, "LibreOffice not installed");

    /// <summary>Converts with LibreOffice itself (not the cheapest route), one at a time like the other LibreOffice tests.</summary>
    private async Task<(string Output, string? Notice)> ConvertAsync(string input, string from, string to)
    {
        var folder = Path.GetDirectoryName(input)!;
        var output = Path.Combine(folder, Path.GetFileNameWithoutExtension(input) + "-out." + to);
        string? notice = null;
        await HwpxAssert.LibreOfficeGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            var step = new ConversionStep(input, from, to, new Preset { TargetFormat = to }, new HwpxAssert.FixedPath(output), folder,
                Notice: key => notice = key);
            var written = await LibreOffice.ConvertAsync(step, null, TestContext.Current.CancellationToken);
            return (written.Single(), notice);
        }
        finally
        {
            HwpxAssert.LibreOfficeGate.Release();
        }
    }

    private static void Entry(ZipArchive zip, string name, string text, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, level).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    /// <summary>Counts every connection to a loopback port; nothing is ever answered.</summary>
    private sealed class Listener : IDisposable
    {
        private readonly TcpListener _tcp = new(IPAddress.Loopback, 0);
        private int _accepted;

        public Listener()
        {
            _tcp.Start();
            Port = ((IPEndPoint)_tcp.LocalEndpoint).Port;
            _ = AcceptAsync();
        }

        public int Port { get; }

        /// <summary>Connections so far, including any still waiting to be accepted.</summary>
        public int Connections() => Volatile.Read(ref _accepted) + (_tcp.Pending() ? 1 : 0);

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await _tcp.AcceptTcpClientAsync();
                    Interlocked.Increment(ref _accepted);
                }
            }
            catch (Exception e) when (e is ObjectDisposedException or SocketException or InvalidOperationException)
            {
                // Stopped.
            }
        }

        public void Dispose() => _tcp.Stop();
    }
}
