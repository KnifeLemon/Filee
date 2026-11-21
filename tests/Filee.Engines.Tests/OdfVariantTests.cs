// The OpenDocument templates and flat files without LibreOffice (Odf/OdfVariants, Odf/OdfFlat, OdfVariantConverter):
// packages turned into templates and flat files and back keep their parts, the readers read every form, the routes
// take the built-in engines, and hostile flat files and packages are refused through the document limits in time.
// Every input is built in code.

using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Odt;
using Filee.Engines.Infrastructure;
using Filee.Engines.Odf;
using Filee.Engines.Odf.Draw;
using Filee.Engines.Office;
using Filee.Engines.Office.Sheets;

namespace Filee.Engines.Tests;

public class OdfVariantTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";
    private static readonly XNamespace Manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
    private static readonly XNamespace Svg = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Odt(OdtBuilder? builder = null) => (builder ?? OdtTests.DocumentBuilder()).Save(Path.Combine(fx.NewFolder(), "doc.odt"));

    private static string Convert(string input, string from, string to, DocumentLimits? limits = null)
    {
        var output = Path.Combine(Path.GetDirectoryName(input)!, $"out-{Guid.NewGuid():N}.{to}");
        OdfVariants.Convert(input, from, output, to, limits, Token);
        return output;
    }

    private static string Text(HDocument document) => PlainTextWriter.Write(document);

    private string Media() => Path.Combine(fx.NewFolder(), "media");

    private static string Mimetype(string package)
    {
        using var zip = ZipFile.OpenRead(package);
        using var reader = new StreamReader(zip.GetEntry("mimetype")!.Open());
        return reader.ReadToEnd();
    }

    private static byte[] Part(string package, string name)
    {
        using var zip = ZipFile.OpenRead(package);
        using var stream = zip.GetEntry(name)!.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    // ───────────────────────── Text ─────────────────────────

    [Fact]
    public void A_text_becomes_a_flat_file_and_back_with_its_text_and_pictures()
    {
        var odt = Odt();
        var flat = Convert(odt, "odt", "fodt");

        var root = XDocument.Load(flat).Root!;
        Assert.Equal(Office + "document", root.Name);
        Assert.Equal("application/vnd.oasis.opendocument.text", (string?)root.Attribute(Office + "mimetype"));
        Assert.Equal(["meta", "styles", "automatic-styles", "master-styles", "body"], root.Elements().Select(e => e.Name.LocalName));
        // The picture is in the XML, not a link to a part.
        Assert.NotEmpty(root.Descendants(Draw + "image").Elements(Office + "binary-data"));
        Assert.DoesNotContain(root.Descendants(Draw + "image"), e => ((string?)e.Attribute(XLink + "href"))?.StartsWith("Pictures/", StringComparison.Ordinal) == true);

        var expectedMedia = Media();
        var expected = OdtReader.Read(odt, expectedMedia);
        var media = Media();
        var fromFlat = OdtReader.Read(Convert(flat, "fodt", "odt"), media);
        Assert.Equal(Text(expected), Text(fromFlat));
        Assert.Equal(expected.Title, fromFlat.Title);
        Assert.Equal(expected.Sections.Count, fromFlat.Sections.Count);
        // The pictures, back out of the XML.
        Assert.Equal(Directory.GetFiles(expectedMedia).Select(File.ReadAllBytes).Select(b => b.Length).Order(),
            Directory.GetFiles(media).Select(File.ReadAllBytes).Select(b => b.Length).Order());
    }

    [Fact]
    public void A_text_becomes_a_template_with_only_its_media_type_changed()
    {
        var odt = Odt();
        var ott = Convert(odt, "odt", "ott");

        Assert.Equal("application/vnd.oasis.opendocument.text-template", Mimetype(ott));
        using (var zip = ZipFile.OpenRead(ott))
        {
            Assert.Equal("mimetype", zip.Entries[0].FullName);
            Assert.Equal(zip.Entries[0].Length, zip.Entries[0].CompressedLength); // stored
        }
        var manifest = XDocument.Parse(Encoding.UTF8.GetString(Part(ott, "META-INF/manifest.xml"))).Root!;
        Assert.Equal("application/vnd.oasis.opendocument.text-template",
            (string?)manifest.Elements(Manifest + "file-entry").Single(e => (string?)e.Attribute(Manifest + "full-path") == "/").Attribute(Manifest + "media-type"));
        foreach (var name in new[] { "content.xml", "styles.xml", "meta.xml", "Pictures/chart.png" })
            Assert.Equal(Part(odt, name), Part(ott, name));

        var back = Convert(ott, "ott", "odt");
        Assert.Equal("application/vnd.oasis.opendocument.text", Mimetype(back));
        Assert.Equal(Text(OdtReader.Read(odt, Media())), Text(OdtReader.Read(back, Media())));
    }

    [Fact]
    public async Task The_document_readers_read_the_template_and_the_flat_file()
    {
        var odt = Odt();
        var expected = Text(await DocumentReaders.ReadAsync(odt, "odt", fx.NewFolder(), Token));
        var ott = Convert(odt, "odt", "ott");
        var fodt = Convert(odt, "odt", "fodt");
        var work = fx.NewFolder();

        Assert.Equal(expected, Text(await DocumentReaders.ReadAsync(ott, "ott", fx.NewFolder(), Token)));
        Assert.Equal(expected, Text(await DocumentReaders.ReadAsync(fodt, "fodt", work, Token)));
        // The package the flat file was read as is gone; only the pictures stay with the job.
        Assert.DoesNotContain(Directory.GetFiles(work), f => f.EndsWith(".odt", StringComparison.Ordinal));
        Assert.Null(new Typesetting.FileeRendererConverter(() => false).FirstUnsupported(fodt, "fodt"));
    }

    [Fact]
    public void Automatic_styles_of_both_parts_keep_apart_in_the_flat_file()
    {
        // P1 is a content style and a header style: the header's one is renamed, with the header's reference.
        var builder = new OdtBuilder
        {
            ContentStyles = "<style:style style:name=\"P1\" style:family=\"paragraph\"><style:text-properties fo:color=\"#ff0000\"/></style:style>",
            StylesAutomatic = "<style:page-layout style:name=\"PM1\"><style:page-layout-properties fo:page-width=\"21cm\" fo:page-height=\"29.7cm\"/></style:page-layout>" +
                              "<style:style style:name=\"P1\" style:family=\"paragraph\"><style:text-properties fo:color=\"#0000ff\"/></style:style>",
            MasterStyles = "<style:master-page style:name=\"Standard\" style:page-layout-name=\"PM1\"><style:header><text:p text:style-name=\"P1\">head</text:p></style:header></style:master-page>",
            Body = "<text:p text:style-name=\"P1\">body</text:p>",
        };
        var root = XDocument.Load(Convert(Odt(builder), "odt", "fodt")).Root!;
        var automatic = root.Element(Office + "automatic-styles")!.Elements().Select(e => (string?)e.Attribute(Style + "name")).ToList();
        Assert.Equal(["P1", "PM1", "MP1"], automatic);
        var header = root.Element(Office + "master-styles")!.Descendants().Single(e => e.Name.LocalName == "p");
        Assert.Equal("MP1", (string?)header.Attribute("{urn:oasis:names:tc:opendocument:xmlns:text:1.0}style-name"));
        Assert.Equal("P1", (string?)root.Element(Office + "body")!.Descendants().Single(e => e.Name.LocalName == "p").Attribute("{urn:oasis:names:tc:opendocument:xmlns:text:1.0}style-name"));
    }

    [Fact]
    public void Renaming_a_clashing_automatic_style_keeps_to_its_family_and_leaves_parents_alone()
    {
        // P1 clashes in the paragraph family (renamed), T1 is a text style in content.xml and a paragraph style in
        // styles.xml (no clash), and Head is a common style an automatic style of styles.xml inherits from while an
        // automatic one of content.xml has its name too.
        var builder = new OdtBuilder
        {
            ContentStyles = "<style:style style:name=\"P1\" style:family=\"paragraph\"/>" +
                            "<style:style style:name=\"T1\" style:family=\"text\"><style:text-properties fo:color=\"#ff0000\"/></style:style>" +
                            "<style:style style:name=\"Head\" style:family=\"paragraph\"/>",
            Styles = "<style:style style:name=\"Head\" style:family=\"paragraph\"><style:text-properties fo:font-size=\"20pt\"/></style:style>",
            StylesAutomatic = "<style:page-layout style:name=\"PM1\"><style:page-layout-properties fo:page-width=\"21cm\" fo:page-height=\"29.7cm\"/></style:page-layout>" +
                              "<style:style style:name=\"P1\" style:family=\"paragraph\"/>" +
                              "<style:style style:name=\"T1\" style:family=\"paragraph\"/>" +
                              "<style:style style:name=\"MP2\" style:family=\"paragraph\" style:parent-style-name=\"Head\"/>" +
                              "<style:style style:name=\"Head\" style:family=\"paragraph\"/>",
            MasterStyles = "<style:master-page style:name=\"Standard\" style:page-layout-name=\"PM1\"><style:header>" +
                           "<text:p text:style-name=\"P1\">one</text:p><text:p text:style-name=\"T1\">two</text:p><text:p text:style-name=\"MP2\">three</text:p>" +
                           "</style:header></style:master-page>",
            Body = "<text:p text:style-name=\"P1\">body</text:p>",
        };
        var root = XDocument.Load(Convert(Odt(builder), "odt", "fodt")).Root!;
        var automatic = root.Element(Office + "automatic-styles")!.Elements().Select(e => (string?)e.Attribute(Style + "name")).ToList();
        Assert.Equal(["P1", "T1", "Head", "PM1", "MP1", "T1", "MP2", "MHead"], automatic);
        var header = root.Element(Office + "master-styles")!.Descendants().Where(e => e.Name.LocalName == "p")
            .Select(e => (string?)e.Attribute("{urn:oasis:names:tc:opendocument:xmlns:text:1.0}style-name")).ToList();
        Assert.Equal(["MP1", "T1", "MP2"], header);
        // The parent is the common style, not the renamed automatic one.
        var inherits = root.Element(Office + "automatic-styles")!.Elements().Single(e => (string?)e.Attribute(Style + "name") == "MP2");
        Assert.Equal("Head", (string?)inherits.Attribute(Style + "parent-style-name"));
    }

    [Fact]
    public void A_flat_file_keeps_embedded_fonts_and_binary_data_of_elements_it_does_not_know()
    {
        var font = new byte[] { 0, 1, 0, 0, 0, 4, 0, 0, 1, 2, 3, 4 };
        var path = Path.Combine(fx.NewFolder(), "fonts.fodt");
        File.WriteAllText(path, $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document {Namespaces} xmlns:ext=\"urn:example:extension\" office:version=\"1.3\" " +
                                "office:mimetype=\"application/vnd.oasis.opendocument.text\">" +
                                "<office:font-face-decls><style:font-face style:name=\"Embedded\" svg:font-family=\"Embedded\"><svg:font-face-src>" +
                                $"<svg:font-face-uri><office:binary-data>{System.Convert.ToBase64String(font)}</office:binary-data></svg:font-face-uri>" +
                                "</svg:font-face-src></style:font-face></office:font-face-decls>" +
                                "<office:body><office:text><text:p>kept</text:p>" +
                                "<ext:blob><office:binary-data>AAECAw==</office:binary-data></ext:blob></office:text></office:body></office:document>");

        var odt = Convert(path, "fodt", "odt");
        var manifest = XDocument.Parse(Encoding.UTF8.GetString(Part(odt, "META-INF/manifest.xml"))).Root!;
        var fontEntry = manifest.Elements(Manifest + "file-entry").Single(e => ((string?)e.Attribute(Manifest + "full-path"))!.StartsWith("Fonts/", StringComparison.Ordinal));
        Assert.Equal("application/x-font-ttf", (string?)fontEntry.Attribute(Manifest + "media-type"));
        var fontPath = (string)fontEntry.Attribute(Manifest + "full-path")!;
        Assert.Equal(font, Part(odt, fontPath));
        foreach (var name in new[] { "content.xml", "styles.xml" })
        {
            var uri = XDocument.Parse(Encoding.UTF8.GetString(Part(odt, name))).Root!.Descendants(Svg + "font-face-uri").Single();
            Assert.Equal(fontPath, (string?)uri.Attribute(XLink + "href"));
        }
        // The extension's data stays where it was, in content.xml.
        var content = XDocument.Parse(Encoding.UTF8.GetString(Part(odt, "content.xml"))).Root!;
        Assert.Equal("AAECAw==", content.Descendants(XName.Get("blob", "urn:example:extension")).Single().Element(Office + "binary-data")!.Value);
        Assert.Contains("kept", Text(OdtReader.Read(odt, Media())), StringComparison.Ordinal);

        // And back into a flat file, the font in it again.
        var back = XDocument.Load(Convert(odt, "odt", "fodt")).Root!;
        Assert.Equal(System.Convert.ToBase64String(font), back.Descendants(Svg + "font-face-uri").Single().Element(Office + "binary-data")!.Value);
    }

    [Fact]
    public void A_flat_file_that_says_it_is_a_template_is_read_as_a_template()
    {
        var flat = Flat("<text:p>template text</text:p>", "application/vnd.oasis.opendocument.text-template", "template.fodt");
        using (var package = OdfVariants.AsPackage(flat, "fodt", fx.NewFolder()))
        {
            Assert.Equal("application/vnd.oasis.opendocument.text-template", Mimetype(package.Path));
            Assert.Contains("template text", Text(OdtReader.Read(package.Path, Media())), StringComparison.Ordinal);
        }
        // Converted, it becomes the form asked for.
        Assert.Equal("application/vnd.oasis.opendocument.text", Mimetype(Convert(flat, "fodt", "odt")));
        Assert.Equal("application/vnd.oasis.opendocument.text-template", Mimetype(Convert(flat, "fodt", "ott")));
        // A plain flat file stays a document.
        using var plain = OdfVariants.AsPackage(Flat("<text:p>x</text:p>"), "fodt", fx.NewFolder());
        Assert.Equal("application/vnd.oasis.opendocument.text", Mimetype(plain.Path));
    }

    [Fact]
    public void An_embedded_chart_goes_into_the_flat_file_and_out_again()
    {
        var builder = new OdtBuilder
        {
            Body = "<text:p><draw:frame text:anchor-type=\"as-char\" svg:width=\"8cm\" svg:height=\"5cm\"><draw:object xlink:href=\"./Object 1\"/>" +
                   "<draw:image xlink:href=\"./ObjectReplacements/Object 1\"/></draw:frame></text:p>",
        };
        builder.Parts["Object 1/content.xml"] = Encoding.UTF8.GetBytes(OdfChartBuilder.Columns());
        builder.Parts["ObjectReplacements/Object 1"] = DocumentFuzzTests.TinyPng(4, 3);
        var odt = Odt(builder);

        var flat = Convert(odt, "odt", "fodt");
        var inner = XDocument.Load(flat).Root!.Descendants(Draw + "object").Single().Element(Office + "document");
        Assert.NotNull(inner);
        Assert.NotNull(inner.Element(Office + "body")?.Element(Office + "chart"));

        var back = Convert(flat, "fodt", "odt");
        using var zip = ZipFile.OpenRead(back);
        Assert.NotNull(zip.GetEntry("Object 1/content.xml"));
        var manifest = XDocument.Load(zip.GetEntry("META-INF/manifest.xml")!.Open()).Root!;
        Assert.Contains(manifest.Elements(Manifest + "file-entry"), e => (string?)e.Attribute(Manifest + "full-path") == "Object 1/"
            && (string?)e.Attribute(Manifest + "media-type") == "application/vnd.oasis.opendocument.chart");
        Assert.Null(new Typesetting.FileeRendererConverter(() => false).FirstUnsupported(back, "odt"));
    }

    // ───────────────────────── Spreadsheets, presentations, drawings ─────────────────────────

    private string Ods()
    {
        var book = new Workbook();
        var sheet = new Worksheet("Data");
        sheet.Set(0, 0, new SheetCell("Flat cell", CellStyle.Default));
        sheet.Set(0, 1, new SheetCell("42", CellStyle.Default, IsNumber: true) { Value = 42 });
        book.Sheets.Add(sheet);
        var path = Path.Combine(fx.NewFolder(), "sheet.ods");
        OdsWriter.Write(book, path);
        return path;
    }

    [Theory]
    [InlineData("ots")]
    [InlineData("fods")]
    public void A_spreadsheet_template_and_flat_file_read_as_the_spreadsheet(string form)
    {
        var ods = Ods();
        var variant = Convert(ods, "ods", form);

        var book = SpreadsheetConverter.Read(variant, form);
        Assert.Equal("Data", book.Sheets.Single().Name);
        Assert.Equal("Flat cell", book.Sheets[0].Get(0, 0)?.Text);
        Assert.Equal("42", book.Sheets[0].Get(0, 1)?.Text);
        Assert.Null(SheetCapabilities.FirstUnsupported(variant, form));
        Assert.Equal("Flat cell", SpreadsheetConverter.Read(Convert(variant, form, "ods"), "ods").Sheets[0].Get(0, 0)?.Text);
    }

    [Theory]
    [InlineData("otp")]
    [InlineData("fodp")]
    public void A_presentation_template_and_flat_file_keep_their_slides(string form)
    {
        var odp = OdfDrawFuzzTests.Costly().Save(Path.Combine(fx.NewFolder(), "deck.odp"));
        var expected = OdfDrawReader.Read(odp, cancellationToken: Token);

        var variant = Convert(odp, "odp", form);
        var deck = OdfDrawReader.Read(variant, cancellationToken: Token);
        Assert.Equal(expected.Slides.Count, deck.Slides.Count);
        Assert.Equal(expected.Slides.Select(s => s.Elements.Count), deck.Slides.Select(s => s.Elements.Count));
        var back = OdfDrawReader.Read(Convert(variant, form, "odp"), cancellationToken: Token);
        Assert.Equal(expected.Slides.Select(s => s.Elements.Count), back.Slides.Select(s => s.Elements.Count));
    }

    [Fact]
    public void A_flat_drawing_becomes_a_drawing_template()
    {
        var flat = new OdpBuilder { Drawing = true, Pages = "<draw:page draw:name=\"p\"><draw:rect svg:x=\"1cm\" svg:y=\"1cm\" svg:width=\"4cm\" svg:height=\"2cm\"/></draw:page>" }
            .SaveFlat(Path.Combine(fx.NewFolder(), "shape.fodg"));
        var otg = Convert(flat, "fodg", "otg");
        Assert.Equal("application/vnd.oasis.opendocument.graphics-template", Mimetype(otg));
        Assert.Single(OdfDrawReader.Read(otg, cancellationToken: Token).Slides);
    }

    [Fact]
    public async Task A_pptx_converts_to_a_presentation_template_and_a_flat_presentation()
    {
        var pptx = OdpWriterTests.Sample().Save(Path.Combine(fx.NewFolder(), "deck.pptx"));
        foreach (var form in new[] { "otp", "fodp" })
        {
            var job = await fx.ConvertAsync([pptx], new Preset { TargetFormat = form });
            Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
            Assert.Equal(3, OdfDrawReader.Read(job.Outputs.Single(), cancellationToken: Token).Slides.Count);
        }
    }

    [Fact]
    public async Task A_word_document_converts_to_a_text_template_and_a_flat_text()
    {
        var odt = Odt();
        var expected = Text(OdtReader.Read(odt, Media())).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        var docx = Path.Combine(fx.NewFolder(), "doc.docx");
        var toDocx = await fx.ConvertAsync([odt], new Preset { TargetFormat = "docx" });
        File.Copy(toDocx.Outputs.Single(), docx);
        foreach (var form in new[] { "ott", "fodt" })
        {
            var job = await fx.ConvertAsync([docx], new Preset { TargetFormat = form });
            Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
            var document = await DocumentReaders.ReadAsync(job.Outputs.Single(), form, fx.NewFolder(), Token);
            Assert.Contains(expected, Text(document), StringComparison.Ordinal);
        }
    }

    // ───────────────────────── Hostile files ─────────────────────────

    private static readonly string Namespaces = OdtBuilder.Namespaces;

    private string Flat(string body, string mimetype = "application/vnd.oasis.opendocument.text", string name = "hostile.fodt")
    {
        var path = Path.Combine(fx.NewFolder(), name);
        File.WriteAllText(path, $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document {Namespaces} office:version=\"1.3\" office:mimetype=\"{mimetype}\">" +
                                $"<office:body><office:text>{body}</office:text></office:body></office:document>");
        return path;
    }

    private static string Picture(byte[] bytes) =>
        $"<text:p><draw:frame text:anchor-type=\"as-char\" svg:width=\"1cm\" svg:height=\"1cm\"><draw:image><office:binary-data>{System.Convert.ToBase64String(bytes)}</office:binary-data></draw:image></draw:frame></text:p>";

    private static ConversionRefusedException RefusedQuickly(Action action, string key = DocumentLimits.ErrorKey)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var refusal = Assert.Throws<ConversionRefusedException>(action);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
        Assert.Equal(key, refusal.ErrorKey);
        return refusal;
    }

    private static ConversionRefusedException TooLargeQuickly(Action action) => RefusedQuickly(action, DocumentLimits.TooLargeKey);

    [Fact]
    public void A_flat_file_past_its_size_is_refused_before_it_is_read()
    {
        var limits = DocumentLimits.Default with { MaxPartBytes = 1024 };
        var flat = Flat(string.Concat(Enumerable.Repeat("<text:p>words words words</text:p>", 500)));
        Assert.Contains("larger than", TooLargeQuickly(() => Convert(flat, "fodt", "odt", limits)).Detail);
    }

    [Fact]
    public void Pictures_in_a_flat_file_count_against_the_part_package_and_entry_limits()
    {
        // One picture larger than a part may be.
        var limits = DocumentLimits.Default with { MaxPartBytes = 64 * 1024 };
        var big = Flat(Picture([.. DocumentFuzzTests.TinyPng(2, 2), .. new byte[100 * 1024]]));
        Assert.Contains("A picture stored in the file", TooLargeQuickly(() => Convert(big, "fodt", "odt", limits)).Detail);

        // More pictures than a package may have parts.
        var many = Flat(string.Concat(Enumerable.Range(0, 40).Select(i => Picture([.. DocumentFuzzTests.TinyPng(2, 2), (byte)i]))));
        Assert.Contains("pictures and objects", TooLargeQuickly(() => Convert(many, "fodt", "odt", DocumentLimits.Default with { MaxPackageEntries = 10 })).Detail);

        // Together more than a package may unpack to.
        var sum = Flat(string.Concat(Enumerable.Repeat(Picture([.. DocumentFuzzTests.TinyPng(2, 2), .. new byte[20 * 1024]]), 10)));
        Assert.Contains("together", TooLargeQuickly(() => Convert(sum, "fodt", "odt", DocumentLimits.Default with { MaxPackageBytes = 100 * 1024 })).Detail);

        // Not base64: the picture is left out, the text converts.
        var broken = Flat("<text:p>kept</text:p><text:p><draw:frame><draw:image><office:binary-data>@@not base64@@</office:binary-data></draw:image></draw:frame></text:p>");
        Assert.Contains("kept", Text(OdtReader.Read(Convert(broken, "fodt", "odt"), Media())), StringComparison.Ordinal);
    }

    [Fact]
    public void Deep_and_large_flat_xml_is_refused_by_the_xml_limits()
    {
        var deep = Flat("<text:p>" + string.Concat(Enumerable.Repeat("<text:span>", 200)) + "x" + string.Concat(Enumerable.Repeat("</text:span>", 200)) + "</text:p>");
        RefusedQuickly(() => Convert(deep, "fodt", "odt", DocumentLimits.Default with { MaxXmlDepth = 64 }));
        var wide = Flat(string.Concat(Enumerable.Repeat("<text:p/>", 5000)));
        RefusedQuickly(() => Convert(wide, "fodt", "odt", DocumentLimits.Default with { MaxXmlElements = 1000 }));
    }

    [Fact]
    public void Objects_nested_without_end_stop_at_the_object_depth()
    {
        var inner = "<text:p>bottom</text:p>";
        for (var i = 0; i < 40; i++)
            inner = $"<text:p><draw:frame><draw:object><office:document office:mimetype=\"application/vnd.oasis.opendocument.text\"><office:body><office:text>{inner}</office:text></office:body></office:document></draw:object></draw:frame></text:p>";
        var odt = Convert(Flat(inner), "fodt", "odt");
        using var zip = ZipFile.OpenRead(odt);
        Assert.InRange(zip.Entries.Count(e => e.FullName.EndsWith("/content.xml", StringComparison.Ordinal)), 1, 8);
    }

    [Fact]
    public void A_picture_used_again_and_again_cant_make_the_flat_file_grow_without_end()
    {
        var builder = new OdtBuilder { Body = string.Concat(Enumerable.Repeat("<text:p><draw:frame><draw:image xlink:href=\"Pictures/p.png\"/></draw:frame></text:p>", 500)) };
        builder.Parts["Pictures/p.png"] = [.. DocumentFuzzTests.TinyPng(2, 2), .. new byte[40 * 1024]];
        var odt = Odt(builder);
        var limits = DocumentLimits.Default with { MaxPartBytes = 1024 * 1024 };
        Assert.Contains("flat file would be larger", TooLargeQuickly(() => Convert(odt, "odt", "fodt", limits)).Detail);
    }

    [Fact]
    public void Another_type_and_encrypted_packages_are_refused_with_their_reason()
    {
        var sheet = Flat("<text:p>x</text:p>", "application/vnd.oasis.opendocument.spreadsheet");
        Assert.Contains("not an OpenDocument text", Assert.Throws<InvalidDataException>(() => Convert(sheet, "fodt", "odt")).Message);
        Assert.Contains("not an OpenDocument spreadsheet", Assert.Throws<InvalidDataException>(() => Convert(Odt(), "ods", "ots")).Message);
        var notXml = Path.Combine(fx.NewFolder(), "bad.fodt");
        File.WriteAllText(notXml, "<office:document");
        Assert.Throws<InvalidDataException>(() => Convert(notXml, "fodt", "odt"));

        var encrypted = new OdtBuilder { ManifestEntries = "<manifest:file-entry manifest:full-path=\"content.xml\"><manifest:encryption-data/></manifest:file-entry>" };
        Assert.Contains("password", Assert.Throws<InvalidDataException>(() => Convert(Odt(encrypted), "odt", "fodt")).Message);
    }
}

[Collection("EngineEnvironment")] // the Filee renderer's switch is process-wide
public sealed class OdfVariantRouteTests(EngineFixture fx) : IClassFixture<EngineFixture>, IDisposable
{
    public void Dispose()
    {
        EngineEnvironment.FileeRenderer = false;
        fx.Catalog.Refresh();
    }

    [Fact]
    public void Routes_take_the_built_in_engines_before_libreoffice()
    {
        EngineEnvironment.FileeRenderer = true;
        fx.Catalog.Refresh();
        var planner = fx.Catalog.CreatePlanner(["libreoffice"]);
        string[] Engines(string from, string to) => [.. planner.Plan(from, to)!.Steps.Select(s => s.Converter.Id)];

        Assert.Equal(["odf-variants"], Engines("odt", "ott"));
        Assert.Equal(["odf-variants"], Engines("odt", "fodt"));
        Assert.Equal(["odf-variants"], Engines("fodt", "ott"));
        Assert.Equal(["odf-variants"], Engines("ods", "fods"));
        Assert.Equal(["odf-variants"], Engines("otp", "odp"));
        Assert.Equal(["odf-variants"], Engines("odg", "fodg"));
        Assert.Equal(["odt-writer", "odf-variants"], Engines("docx", "ott"));
        Assert.Equal(["odt-writer", "odf-variants"], Engines("md", "fodt"));
        Assert.Equal(["spreadsheet", "odf-variants"], Engines("xlsx", "ots"));
        Assert.Equal(["odp-writer"], Engines("pptx", "otp"));
        Assert.Equal(["odp-writer"], Engines("pptx", "fodp"));
        Assert.Equal(["layout"], Engines("fodt", "pdf"));
        Assert.Equal(["layout"], Engines("ott", "pdf"));
        Assert.Equal(["docx-writer"], Engines("fodt", "docx"));
        Assert.Equal(["sheet-print"], Engines("fods", "pdf"));
        Assert.Equal(["sheet-print"], Engines("ots", "pdf"));
        Assert.Equal(["spreadsheet"], Engines("fods", "xlsx"));
        Assert.Equal(["drawing"], Engines("otg", "pdf"));

        // The forms of one type only, never one flat file to another.
        var variants = fx.Converters.Single(c => c.Id == "odf-variants");
        Assert.Equal(24, variants.Edges.Count);
        Assert.All(variants.Edges, e => Assert.Equal(OdfVariants.Find(e.From)!.Family, OdfVariants.Find(e.To)!.Family));
        Assert.All(variants.Edges, e => Assert.True(Core.Formats.FormatRegistry.Get(e.To).Writable, e.To));
    }
}
