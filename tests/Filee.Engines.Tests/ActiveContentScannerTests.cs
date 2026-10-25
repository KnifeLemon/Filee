// ActiveContentScanner on documents built here: a DOCM with a real VBA project (compound file, compressed per MS-OVBA)
// whose module starts by itself, external relationships and DDE fields, OpenDocument Basic libraries and "on load"
// events, an Excel 4.0 macro sheet in an XLS, and an RTF with an embedded object. Clean documents report nothing.

using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Office;
using OpenMcdf;

namespace Filee.Engines.Tests;

public class ActiveContentScannerTests
{
    private readonly ActiveContentScanner _scanner = new();

    [Fact]
    public void A_plain_document_carries_nothing()
    {
        var path = Zip("plain.docx", Ooxml(document: Paragraph("Hello")));

        Assert.Null(_scanner.Scan(path, "docx"));
    }

    [Fact]
    public void A_vba_project_names_its_modules_and_the_ones_that_start_by_themselves()
    {
        var parts = Ooxml(document: Paragraph("Hello"), macroPart: true);
        var path = Zip("macro.docm", parts, ("word/vbaProject.bin", VbaProject(
            ("ThisDocument", "Attribute VB_Name = \"ThisDocument\"\r\nSub Document_Open()\r\n  MsgBox \"Hello\"\r\nEnd Sub\r\n"),
            ("Module1", "Attribute VB_Name = \"Module1\"\r\nSub Helper()\r\nEnd Sub\r\n"))));

        var report = _scanner.Scan(path, "docm")!;

        Assert.Contains(report.Findings, f => f is { Kind: ActiveContentKind.Macro, Name: "ThisDocument" });
        Assert.Contains(report.Findings, f => f is { Kind: ActiveContentKind.Macro, Name: "Module1" });
        Assert.Contains(report.Findings, f => f is { Kind: ActiveContentKind.AutoRunMacro, Name: "Document_Open", Detail: "ThisDocument" });
        Assert.DoesNotContain(report.Findings, f => f is { Kind: ActiveContentKind.AutoRunMacro, Detail: "Module1" });
        Assert.True(report.HasCode);
    }

    [Fact]
    public void A_remote_template_and_a_dde_field_are_found_but_hyperlinks_are_not()
    {
        // A harmless DDE link to a workbook: the field type is what counts. (A command line in it makes Windows
        // Defender remove the test file as Exploit:O97M/DDEDownloader.)
        var parts = Ooxml(document: Paragraph("Hello") +
            "<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> DDEAUTO Excel \"Budget.xlsx\" \"R1C1\" </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>");
        parts["word/_rels/settings.xml.rels"] = Rels(
            ("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/attachedTemplate", "https://attacker.invalid/t.dotm", true));
        parts["word/_rels/document.xml.rels"] = Rels(
            ("rId9", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink", "https://example.com", true));
        var path = Zip("links.docx", parts);

        var report = _scanner.Scan(path, "docx")!;

        Assert.Contains(report.Findings, f => f is { Kind: ActiveContentKind.ExternalLink, Name: "attachedTemplate", Detail: "https://attacker.invalid/t.dotm" });
        Assert.Contains(report.Findings, f => f is { Kind: ActiveContentKind.Dde, Name: "DDEAUTO" });
        Assert.DoesNotContain(report.Findings, f => f.Detail == "https://example.com");
        Assert.False(report.HasCode);
    }

    [Fact]
    public void An_opendocument_basic_library_and_an_on_load_event_are_found()
    {
        var content = "<?xml version=\"1.0\"?><office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" " +
                      "xmlns:script=\"urn:oasis:names:tc:opendocument:xmlns:script:1.0\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" " +
                      "xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\"><office:scripts><office:event-listeners>" +
                      "<script:event-listener script:language=\"ooo:script\" script:event-name=\"dom:load\" xlink:href=\"vnd.sun.star.script:Standard.Module1.Main?language=Basic&amp;location=document\"/>" +
                      "</office:event-listeners></office:scripts><office:body><office:text>" +
                      "<draw:frame><draw:image xlink:href=\"http://tracker.invalid/pixel.png\"/></draw:frame>" +
                      "<draw:frame><draw:image xlink:href=\"Pictures/inside.png\"/></draw:frame>" +
                      "</office:text></office:body></office:document-content>";
        var path = Zip("macro.odt", new Dictionary<string, string>
        {
            ["mimetype"] = "application/vnd.oasis.opendocument.text",
            ["content.xml"] = content,
            ["META-INF/manifest.xml"] = "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\"/>",
            ["Basic/script-lc.xml"] = "<x/>",
            ["Basic/Standard/script-lb.xml"] = "<x/>",
            ["Basic/Standard/Module1.xml"] = "<script:module/>",
        });

        var report = _scanner.Scan(path, "odt")!;

        Assert.Contains(report.Findings, f => f is { Kind: ActiveContentKind.Macro, Name: "Standard.Module1" });
        Assert.Contains(report.Findings, f => f is { Kind: ActiveContentKind.AutoRunMacro, Name: "dom:load" });
        Assert.Contains(report.Findings, f => f is { Kind: ActiveContentKind.ExternalLink, Detail: "http://tracker.invalid/pixel.png" });
        Assert.DoesNotContain(report.Findings, f => f.Detail == "Pictures/inside.png");
    }

    [Fact]
    public void An_excel_4_macro_sheet_is_found_in_an_xls()
    {
        var path = Path.Combine(Folder(), "xlm.xls");
        using (var root = RootStorage.Create(path, OpenMcdf.Version.V3, StorageModeFlags.None))
        using (var workbook = root.CreateStream("Workbook"))
        {
            var records = new MemoryStream();
            Record(records, 0x0809, new byte[16]);                                       // BOF
            Record(records, 0x0085, [0, 0, 0, 0, 0, 0x01, 6, 0, .. "Auto_O"u8.ToArray()]); // BOUNDSHEET: macro sheet
            Record(records, 0x0085, [0, 0, 0, 0, 0, 0x00, 6, 0, .. "Sheet1"u8.ToArray()]); // BOUNDSHEET: worksheet
            Record(records, 0x000A, []);                                                 // EOF
            workbook.Write(records.ToArray());
        }

        var report = _scanner.Scan(path, "xls")!;

        var sheet = Assert.Single(report.Findings);
        Assert.Equal(ActiveContentKind.Macro, sheet.Kind);
        Assert.Equal("Auto_O", sheet.Detail);
    }

    [Fact]
    public void An_rtf_embedded_object_is_found()
    {
        var path = Path.Combine(Folder(), "object.rtf");
        File.WriteAllText(path, @"{\rtf1\ansi{\object\objemb\objupdate{\*\objclass Equation.3}{\*\objdata 0105000002000000}}}");

        var report = _scanner.Scan(path, "rtf")!;

        Assert.Contains(report.Findings, f => f is { Kind: ActiveContentKind.EmbeddedObject, Detail: "Equation.3" });
        Assert.Contains(report.Findings, f => f.Kind == ActiveContentKind.ExternalLink);
    }

    [Theory]
    [InlineData("http://example.com/a.png", true)]
    [InlineData("file:///C:/secret.txt", true)]
    [InlineData(@"\\server\share\x.png", true)]
    [InlineData("../outside.png", true)]
    [InlineData(@"C:\Users\x.png", true)]
    [InlineData("Pictures/inside.png", false)]
    [InlineData("./Object 1", false)]
    [InlineData("data:image/png;base64,AAAA", false)]
    [InlineData("vnd.sun.star.GraphicObject:1000", false)]
    public void Outside_addresses(string href, bool outside) => Assert.Equal(outside, ActiveContentScanner.IsOutside(href));

    [Fact]
    public void Copy_tokens_decompress_per_ms_ovba()
    {
        // "abc" as literals, then a copy token (offset 3, length 6): "abcabcabc".
        byte[] container = [0x01, 0x05, 0xB0, 0x08, (byte)'a', (byte)'b', (byte)'c', 0x03, 0x20];

        Assert.Equal("abcabcabc", Encoding.ASCII.GetString(ActiveContentScanner.Decompress(container, 0)));
    }

    // ───────────────────────── Builders ─────────────────────────

    private static string Folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "filee-active-content", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string Paragraph(string text) => $"<w:p><w:r><w:t>{text}</w:t></w:r></w:p>";

    private static Dictionary<string, string> Ooxml(string document, bool macroPart = false) => new()
    {
        ["[Content_Types].xml"] = "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                                  "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                                  "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                                  "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.ms-word.document.macroEnabled.main+xml\"/>" +
                                  (macroPart ? "<Override PartName=\"/word/vbaProject.bin\" ContentType=\"application/vnd.ms-office.vbaProject\"/>" : "") +
                                  "</Types>",
        ["_rels/.rels"] = Rels(("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "word/document.xml", false)),
        ["word/document.xml"] = "<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
                                document + "</w:body></w:document>",
    };

    private static string Rels(params (string Id, string Type, string Target, bool External)[] relationships) =>
        "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        string.Concat(relationships.Select(r =>
            $"<Relationship Id=\"{r.Id}\" Type=\"{r.Type}\" Target=\"{r.Target}\"{(r.External ? " TargetMode=\"External\"" : "")}/>")) +
        "</Relationships>";

    private static string Zip(string name, IReadOnlyDictionary<string, string> parts, params (string Name, byte[] Data)[] binary)
    {
        var path = Path.Combine(Folder(), name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (part, text) in parts)
        {
            using var writer = new StreamWriter(zip.CreateEntry(part).Open(), new UTF8Encoding(false));
            writer.Write(text);
        }
        foreach (var (part, data) in binary)
        {
            using var stream = zip.CreateEntry(part).Open();
            stream.Write(data);
        }
        return path;
    }

    /// <summary>A vbaProject.bin: a "VBA" storage with the "dir" stream and one stream per module (MS-OVBA).</summary>
    private static byte[] VbaProject(params (string Name, string Source)[] modules)
    {
        var dir = new MemoryStream();
        // PROJECTVERSION: its size field says 4, but 6 bytes follow (the reader must not trip over it).
        DirRecord(dir, 0x0009, [], sizeOverride: 4);
        dir.Write(new byte[6]);
        const int cacheSize = 12; // stand-in for the p-code ("performance cache") before the source
        foreach (var (name, _) in modules)
        {
            DirRecord(dir, 0x0019, Encoding.ASCII.GetBytes(name));
            DirRecord(dir, 0x001A, Encoding.ASCII.GetBytes(name));
            DirRecord(dir, 0x0032, Encoding.Unicode.GetBytes(name));
            DirRecord(dir, 0x0031, BitConverter.GetBytes(cacheSize));
            DirRecord(dir, 0x002B, []);
        }

        var buffer = new MemoryStream();
        using (var root = RootStorage.Create(buffer, OpenMcdf.Version.V3, StorageModeFlags.LeaveOpen))
        {
            var vba = root.CreateStorage("VBA");
            using (var stream = vba.CreateStream("dir"))
                stream.Write(Compress(dir.ToArray()));
            foreach (var (name, source) in modules)
            {
                using var stream = vba.CreateStream(name);
                stream.Write(new byte[cacheSize]);
                stream.Write(Compress(Encoding.ASCII.GetBytes(source)));
            }
        }
        return buffer.ToArray();
    }

    private static void DirRecord(Stream dir, ushort id, byte[] data, uint? sizeOverride = null)
    {
        dir.Write(BitConverter.GetBytes(id));
        dir.Write(BitConverter.GetBytes(sizeOverride ?? (uint)data.Length));
        dir.Write(data);
    }

    private static void Record(Stream stream, ushort type, byte[] data)
    {
        stream.Write(BitConverter.GetBytes(type));
        stream.Write(BitConverter.GetBytes((ushort)data.Length));
        stream.Write(data);
    }

    /// <summary>MS-OVBA compression with literals only (valid, just not smaller): enough for small test data.</summary>
    private static byte[] Compress(byte[] data)
    {
        var output = new List<byte> { 0x01 };
        for (var start = 0; start < data.Length; start += 3000)
        {
            var chunk = data.Skip(start).Take(3000).ToArray();
            var payload = new List<byte>();
            for (var i = 0; i < chunk.Length; i += 8)
            {
                payload.Add(0x00); // eight literals follow
                payload.AddRange(chunk.Skip(i).Take(8));
            }
            var header = (ushort)(0xB000 | (payload.Count + 2 - 3));
            output.AddRange(BitConverter.GetBytes(header));
            output.AddRange(payload);
        }
        return [.. output];
    }
}
