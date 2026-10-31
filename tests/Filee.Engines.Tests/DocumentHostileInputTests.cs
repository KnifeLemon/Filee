// Hostile HWPX and DOCX packages and HWP 5.0 compound files for the document readers and the limits they share
// (Documents/DocumentLimits): ZIP and deflate bombs, XML nested or repeated far beyond real documents, DTDs, tables,
// groups, notes and fields nested without end, pictures that claim gigapixels, sector chains and directories that loop
// or leave the file, records cut short, and the inputs the fuzzer (DocumentFuzzTests) found. Each must be refused or read
// within the limits, quickly and on the smallest stack a conversion runs on. The layout loop guards are tested here too.

using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Binary;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Infrastructure;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

public class DocumentHostileInputTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string HwpNamespaces =
        "xmlns:hp=\"http://www.hancom.co.kr/hwpml/2011/paragraph\" xmlns:hs=\"http://www.hancom.co.kr/hwpml/2011/section\" xmlns:hc=\"http://www.hancom.co.kr/hwpml/2011/core\"";

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>
    /// Runs <paramref name="action"/> on a thread with a 1 MB stack (the smallest a conversion gets) and returns what
    /// it threw; fails when it takes longer than <paramref name="seconds"/>.
    /// </summary>
    private static Exception? OnSmallStack(Action action, int seconds = 60)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }, 1024 * 1024);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(seconds)), $"Still running after {seconds} s");
        return error;
    }

    private static ConversionRefusedException Refused(Action action)
    {
        var refusal = Assert.IsType<ConversionRefusedException>(OnSmallStack(action));
        Assert.Equal(DocumentLimits.ErrorKey, refusal.ErrorKey);
        return refusal;
    }

    private static void Reads(Action action) => Assert.Null(OnSmallStack(action));

    /// <summary>A copy of a package with some parts replaced (written by the action) or added.</summary>
    private static string Rewrite(string source, string target, Dictionary<string, Action<Stream>> parts)
    {
        using (var input = ZipFile.OpenRead(source))
        using (var output = ZipFile.Open(target, ZipArchiveMode.Create))
        {
            foreach (var entry in input.Entries)
            {
                if (parts.ContainsKey(entry.FullName))
                    continue;
                using var from = entry.Open();
                using var to = output.CreateEntry(entry.FullName).Open();
                from.CopyTo(to);
            }
            foreach (var (name, write) in parts)
            {
                using var to = output.CreateEntry(name).Open();
                write(to);
            }
        }
        return target;
    }

    private static Action<Stream> Text(string text) => stream => stream.Write(Encoding.UTF8.GetBytes(text));

    private string Docx(string body, Action<DocxBuilder>? setup = null)
    {
        var builder = new DocxBuilder();
        builder.Paragraph(body);
        setup?.Invoke(builder);
        return builder.Save(Path.Combine(fx.NewFolder(), "input.docx"));
    }

    /// <summary>An HWPX package written by Filee whose section0.xml holds <paramref name="paragraphs"/>.</summary>
    private string Hwpx(string paragraphs, Dictionary<string, Action<Stream>>? more = null)
    {
        var folder = fx.NewFolder();
        var document = new HDocument();
        document.Sections.Add(new HSection { Blocks = { Paragraph("seed") } });
        var seed = Path.Combine(folder, "seed.hwpx");
        HwpxWriter.Write(document, seed);
        var parts = new Dictionary<string, Action<Stream>>(more ?? [])
        {
            ["Contents/section0.xml"] = Text($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><hs:sec {HwpNamespaces}>{paragraphs}</hs:sec>"),
        };
        return Rewrite(seed, Path.Combine(folder, "input.hwpx"), parts);
    }

    private static string Hp(string runContent) => $"<hp:p paraPrIDRef=\"0\" styleIDRef=\"0\"><hp:run charPrIDRef=\"0\">{runContent}</hp:run></hp:p>";

    private static HParagraph Paragraph(string text)
    {
        var paragraph = new HParagraph();
        paragraph.Inlines.Add(new HText(text, default));
        return paragraph;
    }

    /// <summary>A table with one cell, nested <paramref name="levels"/> deep, the innermost holding "inside".</summary>
    private static HTable NestedTables(int levels)
    {
        HBlock content = Paragraph("inside");
        HTable? table = null;
        for (var i = 0; i < levels; i++)
        {
            table = new HTable { ColumnCount = 1 };
            var cell = new HCell();
            cell.Blocks.Add(content);
            var row = new HRow();
            row.Cells.Add(cell);
            table.Rows.Add(row);
            content = table;
        }
        return table!;
    }

    private string Media() => Path.Combine(fx.NewFolder(), "media");

    private static int Depth(IEnumerable<HBlock> blocks) =>
        blocks.OfType<HTable>().Select(t => 1 + t.Rows.SelectMany(r => r.Cells).Select(c => Depth(c.Blocks)).DefaultIfEmpty(0).Max()).DefaultIfEmpty(0).Max();

    // ───────────────────────── The limits ─────────────────────────

    [Fact]
    public void The_limits_are_the_planned_ones()
    {
        var limits = DocumentLimits.Default;
        Assert.Equal(64L << 20, limits.MaxPartBytes);
        Assert.Equal(1L << 30, limits.MaxPackageBytes);
        Assert.Equal(32, limits.MaxNestingDepth);
        Assert.Equal(20_000, limits.MaxPages);
        Assert.Equal(16_384L * 16_384, limits.MaxPicturePixels);
        Assert.True(limits.MaxXmlDepth >= limits.MaxNestingDepth * 8, "tables nested to the limit must fit in the XML depth");
    }

    // ───────────────────────── ZIP bombs ─────────────────────────

    [Fact]
    public void A_part_larger_than_64_MB_is_refused_before_it_is_unpacked()
    {
        var path = Rewrite(Docx(P(R("x"))), Path.Combine(fx.NewFolder(), "big.docx"), new()
        {
            ["word/header9.xml"] = stream =>
            {
                var chunk = new byte[1 << 20];
                Array.Fill(chunk, (byte)' ');
                for (var i = 0; i < 65; i++)
                    stream.Write(chunk);
            },
        });
        using var package = DocumentPackage.Open(path);
        var entry = package.GetEntry("word/header9.xml")!;
        var refusal = Assert.Throws<ConversionRefusedException>(() => package.Open(entry));
        Assert.Contains("more than 64 MB", refusal.Detail);
        Assert.Equal(0, package.BytesRead);
    }

    [Fact]
    public void A_part_that_inflates_like_a_bomb_is_refused_while_it_is_unpacked()
    {
        // 6 MB of spaces packs into a few kilobytes: far beyond what any word processor's XML compresses to.
        var body = "<w:p><w:r><w:t>" + new string(' ', 6 << 20) + "</w:t></w:r></w:p>";
        var refusal = Refused(() => DocxReader.Read(Docx(body), Media()));
        Assert.Contains("times its packed size", refusal.Detail);
    }

    [Fact]
    public void A_part_whose_header_understates_its_size_is_counted_as_it_unpacks()
    {
        var docx = Docx(P(R(new string('x', 2 << 20))));
        var bytes = File.ReadAllBytes(docx);
        UnderstateSize(bytes, "word/document.xml", 100);
        var lying = Path.Combine(fx.NewFolder(), "lying.docx");
        File.WriteAllBytes(lying, bytes);
        // What .NET makes of the size is its business; Filee must not take 2 MB for a part of 1 MB at the most.
        var error = OnSmallStack(() => DocxReader.Read(lying, Media(), DocumentLimits.Default with { MaxPartBytes = 1 << 20 }));
        Assert.True(error is InvalidDataException or ConversionRefusedException, error?.ToString() ?? "read");
    }

    /// <summary>Writes <paramref name="size"/> as the unpacked size of <paramref name="name"/> in its local and central headers.</summary>
    private static void UnderstateSize(byte[] zip, string name, uint size)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        for (var i = 0; i + 46 < zip.Length; i++)
        {
            var signature = BitConverter.ToUInt32(zip, i);
            if (signature == 0x04034B50 && zip.AsSpan(i + 30, nameBytes.Length).SequenceEqual(nameBytes))
                BitConverter.TryWriteBytes(zip.AsSpan(i + 22), size);
            else if (signature == 0x02014B50 && zip.AsSpan(i + 46, nameBytes.Length).SequenceEqual(nameBytes))
                BitConverter.TryWriteBytes(zip.AsSpan(i + 24), size);
        }
    }

    [Fact]
    public void A_package_whose_parts_together_pass_the_limit_is_refused()
    {
        var limits = DocumentLimits.Default with { MaxPackageBytes = 3 << 20 };
        var docx = Docx(P(R("x")), builder =>
        {
            var references = string.Concat(Enumerable.Range(0, 4).Select(i =>
                $"<w:headerReference w:type=\"{(i % 2 == 0 ? "default" : "even")}\" r:id=\"{builder.Header(P(R(new string('x', 1 << 20))))}\"/>"));
            builder.SectionProperties = references + DefaultPage;
        });
        var refusal = Refused(() => DocxReader.Read(docx, Media(), limits));
        Assert.Contains("The package unpacks to more than 3 MB", refusal.Detail);
        Reads(() => DocxReader.Read(docx, Media()));
    }

    [Fact]
    public void A_package_of_too_many_parts_is_refused()
    {
        var docx = Docx(P(R("x")));
        var refusal = Refused(() => DocxReader.Read(docx, Media(), DocumentLimits.Default with { MaxPackageEntries = 3 }));
        Assert.Contains("more than 3 parts", refusal.Detail);
    }

    // ───────────────────────── XML ─────────────────────────

    [Theory]
    [InlineData("docx")]
    [InlineData("hwpx")]
    public void Xml_nested_a_hundred_thousand_levels_deep_is_refused_at_once(string format)
    {
        var nested = string.Concat(Enumerable.Repeat("<x>", 100_000)) + string.Concat(Enumerable.Repeat("</x>", 100_000));
        var refusal = format == "docx"
            ? Refused(() => DocxReader.Read(Docx(P("<w:r>" + nested + "</w:r>")), Media()))
            : Refused(() => HwpxReader.Read(Hwpx(Hp(nested)), Media()));
        Assert.Contains("512 levels deep", refusal.Detail);
    }

    [Fact]
    public void Xml_of_countless_tiny_elements_is_refused_before_it_fills_the_memory()
    {
        var limits = DocumentLimits.Default with { MaxXmlElements = 10_000 };
        var docx = Docx(P(string.Concat(Enumerable.Repeat("<w:r/>", 20_000))));
        var refusal = Refused(() => DocxReader.Read(docx, Media(), limits));
        Assert.Contains("more than 10,000 elements", refusal.Detail);
        Reads(() => DocxReader.Read(docx, Media()));
    }

    [Theory]
    [InlineData("<!DOCTYPE w:document [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\"><!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\"><!ENTITY d \"&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;\">]>", "&d;")]
    [InlineData("<!DOCTYPE w:document [<!ENTITY secret SYSTEM \"file:///C:/Windows/win.ini\">]>", "&secret;")]
    [InlineData("<!DOCTYPE w:document SYSTEM \"http://example.invalid/word.dtd\">", "x")]
    public void A_document_with_a_DTD_is_refused_without_expanding_or_fetching_anything(string doctype, string text)
    {
        var docx = Docx(P(R("x")));
        var path = Rewrite(docx, Path.Combine(fx.NewFolder(), "dtd.docx"), new()
        {
            ["word/document.xml"] = Text($"<?xml version=\"1.0\" encoding=\"UTF-8\"?>{doctype}<w:document {Namespaces}><w:body><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:body></w:document>"),
        });
        var error = Assert.IsType<InvalidDataException>(OnSmallStack(() => DocxReader.Read(path, Media())));
        Assert.Contains("DTD", error.Message);
    }

    [Fact]
    public void Geo_xml_still_reads_through_the_shared_loader()
    {
        using var stream = new MemoryStream("<a xmlns:p=\"urn:p\"><p:b>1 &amp; 2</p:b><![CDATA[x]]></a>"u8.ToArray());
        var root = Filee.Engines.Geo.GeoXml.Load(stream).Root!;
        Assert.Equal("1 & 2", root.Element(XmlNamespace("urn:p") + "b")!.Value);
        Assert.Empty(root.Attributes()); // namespace declarations are not kept for the geospatial formats
    }

    private static System.Xml.Linq.XNamespace XmlNamespace(string uri) => System.Xml.Linq.XNamespace.Get(uri);

    [Fact]
    public void The_shared_loader_keeps_what_the_readers_look_up_like_XDocument_Load()
    {
        const string xml = "<?xml version=\"1.0\"?><r xmlns=\"urn:d\" xmlns:p=\"urn:p\" p:a=\"1\"><t xml:space=\"preserve\"> a <![CDATA[<b>]]> c </t><!-- note --><p:e/>\n</r>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var safe = SafeXml.Load(stream, new XmlLimits(10, 100, 10_000));
        var reference = System.Xml.Linq.XDocument.Parse(xml, System.Xml.Linq.LoadOptions.PreserveWhitespace);
        Assert.Equal(reference.Root!.Value, safe.Root!.Value);
        Assert.Equal("urn:p", safe.Root.GetNamespaceOfPrefix("p")?.NamespaceName);
        Assert.Equal(reference.Root.Attributes().Select(a => a.ToString()), safe.Root.Attributes().Select(a => a.ToString()));
        Assert.Equal(reference.Root.Elements().Select(e => e.Name), safe.Root.Elements().Select(e => e.Name));
    }

    // ───────────────────────── Nesting ─────────────────────────

    [Fact]
    public void Hwpx_tables_nested_to_the_limit_are_read_and_one_deeper_refused()
    {
        string Write(int levels)
        {
            var document = new HDocument();
            document.Sections.Add(new HSection { Blocks = { NestedTables(levels) } });
            var path = Path.Combine(fx.NewFolder(), $"nested-{levels}.hwpx");
            HwpxWriter.Write(document, path);
            return path;
        }
        HDocument? read = null;
        Reads(() => read = HwpxReader.Read(Write(32), Media()));
        Assert.Equal(32, Depth(read!.Sections[0].Blocks));
        var refusal = Refused(() => HwpxReader.ReadForLayout(Write(33), Media()));
        Assert.Contains("nested more than 32 levels deep", refusal.Detail);
    }

    [Fact]
    public void Docx_tables_nested_to_the_limit_are_read_and_one_deeper_refused()
    {
        string Write(int levels)
        {
            var document = new HDocument();
            document.Sections.Add(new HSection { Blocks = { NestedTables(levels) } });
            var path = Path.Combine(fx.NewFolder(), $"nested-{levels}.docx");
            DocxWriter.Write(document, path);
            return path;
        }
        HDocument? read = null;
        Reads(() => read = DocxReader.Read(Write(32), Media()));
        Assert.Equal(32, Depth(read!.Sections[0].Blocks));
        Refused(() => DocxReader.Read(Write(33), Media()));
    }

    [Fact]
    public void Groups_nested_without_end_are_refused()
    {
        var hwpx = Hwpx(Hp(string.Concat(Enumerable.Repeat("<hp:container>", 60)) + "<hp:rect/>" + string.Concat(Enumerable.Repeat("</hp:container>", 60))));
        Refused(() => HwpxReader.Read(hwpx, Media()));

        var group = string.Concat(Enumerable.Repeat("<wpg:grpSp>", 60)) + "<wps:wsp><wps:bodyPr/></wps:wsp>" + string.Concat(Enumerable.Repeat("</wpg:grpSp>", 60));
        var docx = Docx(P(Anchor("http://schemas.microsoft.com/office/word/2010/wordprocessingGroup", "<wpg:wgp>" + group + "</wpg:wgp>", 914400, 914400,
            "<wp:positionH relativeFrom=\"page\"><wp:posOffset>0</wp:posOffset></wp:positionH>", "<wp:positionV relativeFrom=\"page\"><wp:posOffset>0</wp:posOffset></wp:positionV>")));
        Refused(() => DocxReader.Read(docx, Media()));

        var vml = Docx(P("<w:r><w:pict>" + string.Concat(Enumerable.Repeat("<v:group>", 60)) + "<v:rect style=\"width:10pt;height:10pt\"/>" + string.Concat(Enumerable.Repeat("</v:group>", 60)) + "</w:pict></w:r>"));
        Refused(() => DocxReader.Read(vml, Media()));
    }

    [Fact]
    public void A_footnote_that_refers_to_itself_is_read_once()
    {
        // Before, reading the note read its reference, which read the note again until the stack overflowed.
        var docx = Docx(P(R("본문") + "<w:r><w:footnoteReference w:id=\"1\"/></w:r>"), builder =>
            builder.Footnotes("<w:footnote w:id=\"1\"><w:p>" + R("각주") + "<w:r><w:footnoteReference w:id=\"1\"/></w:r></w:p></w:footnote>"));
        HDocument? read = null;
        Reads(() => read = DocxReader.Read(docx, Media()));
        var note = Assert.Single(read!.Sections[0].Blocks.OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HNote>());
        Assert.Empty(note.Blocks.OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HNote>());
    }

    [Fact]
    public void Fields_that_never_end_are_refused_instead_of_slowing_every_run()
    {
        var docx = Docx(P(string.Concat(Enumerable.Repeat("<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>", 100_000)) + R("x")));
        var refusal = Refused(() => DocxReader.Read(docx, Media()));
        Assert.Contains("Fields are nested", refusal.Detail);

        // Every paragraph reopened the links of every open HWPX field: links nested as deep as the fields.
        const string Begin = "<hp:ctrl><hp:fieldBegin type=\"HYPERLINK\"><hp:parameters><hp:stringParam name=\"Command\">https\\://example.invalid;1;0;0;</hp:stringParam></hp:parameters></hp:fieldBegin></hp:ctrl>";
        var hwpx = Hwpx(Hp(string.Concat(Enumerable.Repeat(Begin, 1000))) + Hp("<hp:t>x</hp:t>"));
        Refused(() => HwpxReader.Read(hwpx, Media()));
    }

    [Fact]
    public void A_style_chain_of_a_hundred_thousand_styles_is_read_without_overflowing_the_stack()
    {
        var styles = new StringBuilder();
        for (var i = 0; i < 100_000; i++)
            styles.Append($"<w:style w:type=\"paragraph\" w:styleId=\"S{i}\"><w:basedOn w:val=\"S{i + 1}\"/><w:pPr><w:jc w:val=\"center\"/></w:pPr></w:style>");
        var docx = Docx(P(R("x"), "<w:pStyle w:val=\"S0\"/>"), builder => builder.Styles += styles.ToString());
        HDocument? read = null;
        Reads(() => read = DocxReader.Read(docx, Media()));
        Assert.Equal(HAlign.Center, read!.Sections[0].Blocks.OfType<HParagraph>().First().Format.Align);
    }

    // ───────────────────────── Counts ─────────────────────────

    [Fact]
    public void Paragraphs_and_objects_beyond_the_limits_are_refused()
    {
        var limits = DocumentLimits.Default with { MaxParagraphs = 100, MaxObjects = 10 };
        var paragraphs = Docx(string.Concat(Enumerable.Repeat(P(R("x")), 101)));
        Assert.Contains("more than 100 paragraphs", Refused(() => DocxReader.Read(paragraphs, Media(), limits)).Detail);
        var hwpxParagraphs = Hwpx(string.Concat(Enumerable.Repeat(Hp("<hp:t>x</hp:t>"), 101)));
        Assert.Contains("more than 100 paragraphs", Refused(() => HwpxReader.Read(hwpxParagraphs, Media(), null, limits)).Detail);

        var shapes = Hwpx(Hp(string.Concat(Enumerable.Repeat("<hp:rect/>", 11))));
        Assert.Contains("more than 10 tables, pictures and drawing objects", Refused(() => HwpxReader.Read(shapes, Media(), null, limits)).Detail);
        Reads(() => HwpxReader.Read(shapes, Media()));
    }

    // ───────────────────────── Pictures ─────────────────────────

    /// <summary>A PNG of 4 × 3 pixels whose header claims <paramref name="width"/> × <paramref name="height"/>.</summary>
    private static byte[] ClaimingPng(int width, int height)
    {
        var png = DocumentFuzzTests.TinyPng(4, 3);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), height);
        return png;
    }

    [Fact]
    public void A_picture_that_claims_ten_gigapixels_is_left_out_and_the_document_still_reads()
    {
        var huge = new DocxBuilder();
        var big = huge.Image(ClaimingPng(100_000, 100_000));
        var small = huge.Image(DocumentFuzzTests.TinyPng(4, 3));
        huge.Paragraph(P(InlinePicture(big, 914400, 914400) + InlinePicture(small, 914400, 914400) + R("글")));
        var docx = huge.Save(Path.Combine(fx.NewFolder(), "huge.docx"));
        HDocument? read = null;
        var media = Media();
        Reads(() => read = DocxReader.Read(docx, media));
        var image = Assert.Single(read!.Sections[0].Blocks.OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HImage>());
        Assert.Equal((4, 3), PictureSize.Read(image.Path));
        Assert.Single(Directory.GetFiles(media)); // the huge one was not kept on disk either

        // HWPX: the same picture through the HWPX writer, then its header made huge.
        var document = new HDocument();
        var paragraph = new HParagraph();
        var picture = Path.Combine(fx.NewFolder(), "p.png");
        File.WriteAllBytes(picture, DocumentFuzzTests.TinyPng(4, 3));
        paragraph.Inlines.Add(new HImage(picture) { Width = 1000, Height = 750 });
        document.Sections.Add(new HSection { Blocks = { paragraph } });
        var seed = Path.Combine(fx.NewFolder(), "picture.hwpx");
        HwpxWriter.Write(document, seed);
        string binData;
        using (var zip = ZipFile.OpenRead(seed))
            binData = zip.Entries.Single(e => e.FullName.StartsWith("BinData/", StringComparison.Ordinal)).FullName;
        var hwpx = Rewrite(seed, Path.Combine(fx.NewFolder(), "huge.hwpx"), new() { [binData] = s => s.Write(ClaimingPng(100_000, 100_000)) });
        Reads(() => read = HwpxReader.Read(hwpx, Media()));
        Assert.Empty(read!.Sections[0].Blocks.OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HImage>());
    }

    [Fact]
    public void Picture_sizes_are_read_from_the_headers_of_every_common_format()
    {
        (int, int)? Size(byte[] bytes) => PictureSize.Read(new MemoryStream(bytes));
        Assert.Equal((4, 3), Size(DocumentFuzzTests.TinyPng(4, 3)));
        Assert.Equal((100_000, 70_000), Size(ClaimingPng(100_000, 70_000)));
        Assert.Equal((640, 480), Size([.. "GIF89a"u8, 0x80, 0x02, 0xE0, 0x01, 0, 0, 0]));
        // BMP: BITMAPINFOHEADER with a negative (top-down) height.
        var bmp = new byte[54];
        "BM"u8.CopyTo(bmp);
        BitConverter.TryWriteBytes(bmp.AsSpan(14), 40);
        BitConverter.TryWriteBytes(bmp.AsSpan(18), 300);
        BitConverter.TryWriteBytes(bmp.AsSpan(22), -200);
        Assert.Equal((300, 200), Size(bmp));
        // JPEG: an APP0 segment, then the frame header (SOF0) with height 1080 and width 1920.
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, .. new byte[14], 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x04, 0x38, 0x07, 0x80, 0x03, .. new byte[9], 0xFF, 0xD9];
        Assert.Equal((1920, 1080), Size(jpeg));
        // WebP: VP8X with a canvas of 5000 × 4000.
        var webp = new byte[30];
        "RIFF"u8.CopyTo(webp);
        "WEBPVP8X"u8.CopyTo(webp.AsSpan(8));
        webp[24] = (5000 - 1) & 0xFF;
        webp[25] = (5000 - 1) >> 8;
        webp[27] = (4000 - 1) & 0xFF;
        webp[28] = (4000 - 1) >> 8;
        Assert.Equal((5000, 4000), Size(webp));
        // Unknown, cut short or endless headers give no size.
        Assert.Null(Size("%PDF-1.7"u8.ToArray()));
        Assert.Null(Size([0x89, (byte)'P', (byte)'N', (byte)'G']));
        Assert.Null(Size([0xFF, 0xD8, .. Enumerable.Repeat<byte>(0xFF, 5000)]));
    }

    [Fact]
    public void Part_names_never_become_file_names()
    {
        Assert.Equal(".png", DocumentPackage.SafeExtension("word/media/image1.PNG"));
        Assert.Equal("", DocumentPackage.SafeExtension("media/image.png:stream"));
        Assert.Equal("", DocumentPackage.SafeExtension("../../evil"));
        Assert.Equal("", DocumentPackage.SafeExtension("x.p/n\\g"));
        Assert.Equal("", DocumentPackage.SafeExtension("x." + new string('a', 9)));
        Assert.Equal("", DocumentPackage.SafeExtension("noextension"));

        // A relationship target with a stream name and a path still lands as docx-media-1 in the media folder.
        var builder = new DocxBuilder();
        var id = builder.Image(DocumentFuzzTests.TinyPng(4, 3));
        builder.Paragraph(P(InlinePicture(id, 914400, 914400)));
        var docx = builder.Save(Path.Combine(fx.NewFolder(), "names.docx"));
        var renamed = Path.Combine(fx.NewFolder(), "renamed.docx");
        using (var input = ZipFile.OpenRead(docx))
        using (var output = ZipFile.Open(renamed, ZipArchiveMode.Create))
        {
            foreach (var entry in input.Entries)
            {
                var name = entry.FullName == "word/media/image1.png" ? "word/media/image1.png:evil" : entry.FullName;
                using var from = entry.Open();
                var text = name.EndsWith(".rels", StringComparison.Ordinal) ? new StreamReader(from).ReadToEnd().Replace("media/image1.png", "media/image1.png:evil", StringComparison.Ordinal) : null;
                using var to = output.CreateEntry(name).Open();
                if (text is null)
                    from.CopyTo(to);
                else
                    to.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        var media = Media();
        Reads(() => DocxReader.Read(renamed, media));
        Assert.Equal(["docx-media-1"], Directory.GetFiles(media).Select(Path.GetFileName));
    }

    [Fact]
    public void Embedded_fonts_are_not_read()
    {
        var docx = Docx(P(R("x", "<w:rFonts w:ascii=\"Hostile\"/>")));
        var withFont = Rewrite(docx, Path.Combine(fx.NewFolder(), "font.docx"), new()
        {
            ["word/fonts/font1.odttf"] = s => s.Write(new byte[4096]),
            ["word/fontTable.xml"] = Text($"<w:fonts {Namespaces}><w:font w:name=\"Hostile\"><w:embedRegular r:id=\"rIdFont\" w:fontKey=\"{{00000000-0000-0000-0000-000000000000}}\"/></w:font></w:fonts>"),
        });
        var media = Media();
        Reads(() => DocxReader.Read(withFont, media));
        Assert.False(Directory.Exists(media) && Directory.EnumerateFiles(media).Any());
    }

    // ───────────────────────── HWP 5.0 ─────────────────────────

    private string Hwp(HwpBuilder builder)
    {
        var path = Path.Combine(fx.NewFolder(), "hostile.hwp");
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    /// <summary>A plain HWP whose section stream spans several sectors (a long paragraph).</summary>
    private static HwpBuilder LongHwp() => new HwpBuilder().PlainDocInfo().SectionStart().Paragraph(new string('가', 1500));

    private string Damaged(HwpBuilder builder, DocumentLimits? limits = null) =>
        Assert.IsType<InvalidDataException>(OnSmallStack(() => HwpReader.Read(Hwp(builder), Media(), null, limits))).Message;

    [Fact]
    public void Hwp_sector_chains_that_loop_or_leave_the_file_are_refused()
    {
        const string Section = "BodyText/Section0";
        var loop = LongHwp();
        loop.EditFat = (fat, starts) => fat[starts[Section] + 2] = starts[Section];
        Assert.Contains("loops", Damaged(loop));

        var outside = LongHwp();
        outside.EditFat = (fat, starts) => fat[starts[Section] + 1] = 0x00FFFFFF;
        Assert.Contains("points outside the file", Damaged(outside));

        // A stream that claims more than its chain holds.
        var cutShort = LongHwp();
        cutShort.DeclaredSizes[Section] = 64 * 1024;
        Assert.Contains("cut short", Damaged(cutShort));
    }

    [Fact]
    public void Hwp_directories_that_loop_or_point_outside_are_refused()
    {
        var loop = LongHwp();
        // The first member of the root names itself as its right sibling.
        loop.EditDirectory = directory => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(directory.AsSpan(128 + 72), 1);
        Assert.Contains("loops", Damaged(loop));

        var outside = LongHwp();
        outside.EditDirectory = directory => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(directory.AsSpan(128 + 72), 5000);
        Assert.Contains("outside the directory", Damaged(outside));
    }

    [Fact]
    public void Hwp_streams_beyond_the_limits_are_refused()
    {
        var huge = LongHwp();
        huge.DeclaredSizes["BodyText/Section0"] = 0xF0000000;
        Refused(() => HwpReader.Read(Hwp(huge), Media()));

        // Deflated zeros: a few KB that unpack to megabytes.
        var bomb = new HwpBuilder { Flags = 1 }.PlainDocInfo();
        bomb.SectionBytes = HwpBuilder.Pack(new byte[8 << 20], compress: true);
        var refusal = Refused(() => HwpReader.Read(Hwp(bomb), Media(), null, DocumentLimits.Default with { InflateRatioFloor = 1 << 20 }));
        Assert.Contains("times its packed size", refusal.Detail);

        var limits = DocumentLimits.Default with { MaxParagraphs = 100 };
        var paragraphs = new HwpBuilder().PlainDocInfo().SectionStart();
        for (var i = 0; i < 120; i++)
            paragraphs.Paragraph("문단");
        Assert.Contains("more than 100 paragraphs", Refused(() => HwpReader.Read(Hwp(paragraphs), Media(), null, limits)).Detail);
    }

    [Fact]
    public void Hwp_records_cut_cutShortor_badly_packed_are_refused()
    {
        var header = new HwpBuilder().PlainDocInfo();
        header.SectionBytes = [0x42, 0x00];
        Assert.Contains("record header is cut short", Damaged(header));

        var record = new HwpBuilder().PlainDocInfo();
        record.SectionBytes = HwpBuilder.Record(67, 0, new byte[100])[..40];
        Assert.Contains("longer than its stream", Damaged(record));

        var packing = new HwpBuilder { Flags = 1 }.PlainDocInfo();
        packing.SectionBytes = [0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x13, 0x37];
        Assert.Contains("can't be unpacked", Damaged(packing));
    }

    [Fact]
    public void Hwp_paragraph_lists_nested_without_end_are_refused_on_a_small_stack()
    {
        // Hidden comments (숨은 설명) inside hidden comments: each a control with a paragraph list.
        var builder = new HwpBuilder().PlainDocInfo().SectionStart();
        void Nest(int depth, int level)
        {
            var text = depth == 0 ? System.Text.Encoding.Unicode.GetBytes("끝\r") : HwpBuilder.Bytes(HwpBuilder.Control(15, "tcmt"), (ushort)13);
            builder.Section.Add(HwpBuilder.Record(66, level, HwpBuilder.Bytes(text.Length / 2, 0, (ushort)0, (byte)0, (byte)0, (ushort)1, (ushort)0, (ushort)0, 0, (ushort)0)));
            builder.Section.Add(HwpBuilder.Record(67, level + 1, text));
            builder.Section.Add(HwpBuilder.Record(68, level + 1, HwpBuilder.Bytes(0, 0)));
            if (depth == 0)
                return;
            builder.Section.Add(HwpBuilder.Record(71, level + 1, HwpBuilder.Bytes(HwpBuilder.Id("tcmt"))));
            builder.Section.Add(HwpBuilder.Record(72, level + 2, HwpBuilder.Bytes(1, 0)));
            Nest(depth - 1, level + 2);
        }
        Nest(200, 0);
        Assert.Contains("nested more than", Refused(() => HwpReader.Read(Hwp(builder), Media())).Detail);
    }

    // ───────────────────────── What the fuzzer found ─────────────────────────

    [Fact]
    public void Fuzz_finding_two_numberings_with_one_id_read_with_the_first()
    {
        // ArgumentException: an item with the same key has already been added (DocxReader.LoadNumbering).
        var docx = Docx(P(R("항목"), "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr>"), builder => builder.Numbering =
            "<w:abstractNum w:abstractNumId=\"0\"><w:lvl w:ilvl=\"0\"><w:numFmt w:val=\"decimal\"/><w:lvlText w:val=\"%1)\"/></w:lvl></w:abstractNum>" +
            "<w:abstractNum w:abstractNumId=\"0\"><w:lvl w:ilvl=\"0\"><w:numFmt w:val=\"bullet\"/><w:lvlText w:val=\"*\"/></w:lvl></w:abstractNum>" +
            "<w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num>");
        HDocument? read = null;
        Reads(() => read = DocxReader.Read(docx, Media()));
        var list = read!.Sections[0].Blocks.OfType<HParagraph>().First().List;
        Assert.Equal("^1)", list!.Numbering.Levels[0].Text);
    }

    [Theory]
    [InlineData("-1e308")]
    [InlineData("-2147483648")]
    [InlineData("-9223372036854775808")]
    public void Fuzz_finding_page_sizes_at_the_end_of_the_number_range_read(string value)
    {
        // OverflowException: negating the minimum value of a twos complement number (Math.Abs in DocxReader.Page).
        var docx = Docx(P(R("x")), builder => builder.SectionProperties =
            $"<w:pgSz w:w=\"{value}\" w:h=\"16838\"/><w:pgMar w:top=\"{value}\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/>");
        Reads(() => DocxReader.Read(docx, Media()));
        // VML line ends at both ends of the range (Math.Abs of their difference).
        var line = Docx(P("<w:r><w:pict><v:line from=\"-99999999999in,0\" to=\"0,0\"/></w:pict></w:r>"));
        Reads(() => DocxReader.Read(line, Media()));
    }

    [Fact]
    public void Fuzz_finding_damaged_xml_in_a_docx_is_invalid_data()
    {
        // XmlException escaped DocxReader for every part (document, relationships, core properties).
        foreach (var part in new[] { "word/document.xml", "word/_rels/document.xml.rels", "docProps/core.xml" })
        {
            var path = Rewrite(Docx(P(R("x"))), Path.Combine(fx.NewFolder(), "damaged.docx"), new() { [part] = Text("<a><b></a>") });
            var error = Assert.IsType<InvalidDataException>(OnSmallStack(() => DocxReader.Read(path, Media())));
            Assert.Contains(part, error.Message);
        }
    }

    [Theory]
    [InlineData("colCnt=\"2147483647\"", "colAddr=\"0\"", "colSpan=\"1\"")] // fuzzer: OutOfMemoryException, array dimensions exceeded
    [InlineData("colCnt=\"2\"", "colAddr=\"2147483647\"", "colSpan=\"2147483647\"")] // fuzzer: ArgumentOutOfRangeException in ColumnWidths
    [InlineData("colCnt=\"3\"", "colAddr=\"0\"", "colSpan=\"3\"")] // found with them: the widths of the cells below overflowed their sum
    public void Fuzz_finding_hwpx_tables_with_column_numbers_at_the_end_of_the_range_read(string count, string address, string span)
    {
        static string Cell(string text, int column, int row, string width) =>
            $"<hp:tc><hp:subList><hp:p><hp:run><hp:t>{text}</hp:t></hp:run></hp:p></hp:subList><hp:cellAddr colAddr=\"{column}\" rowAddr=\"{row}\"/>" +
            $"<hp:cellSpan colSpan=\"1\" rowSpan=\"1\"/><hp:cellSz width=\"{width}\" height=\"1000\"/></hp:tc>";
        var cell = $"<hp:tc><hp:subList><hp:p><hp:run><hp:t>칸</hp:t></hp:run></hp:p></hp:subList><hp:cellAddr {address} rowAddr=\"0\"/>" +
                   $"<hp:cellSpan {span} rowSpan=\"2147483647\"/><hp:cellSz width=\"2147483647\" height=\"1000\"/></hp:tc>";
        var below = Cell("둘", 0, 1, "2147483647") + Cell("셋", 1, 1, "2147483647");
        var hwpx = Hwpx(Hp($"<hp:tbl {count} rowCnt=\"2\"><hp:tr>{cell}</hp:tr><hp:tr>{below}</hp:tr></hp:tbl>"));
        HDocument? read = null;
        Reads(() => read = HwpxReader.Read(hwpx, Media()));
        var table = read!.Sections[0].Blocks.OfType<HTable>().Single();
        Assert.InRange(table.ColumnCount, 1, DocumentLimits.Default.MaxTableColumns);
        Assert.Equal(2, table.Rows[0].Cells[0].RowSpan); // no further than the last row
    }

    // ───────────────────────── Layout guards ─────────────────────────

    [Fact]
    public void The_layout_stops_at_the_page_limit_and_after_too_many_empty_pages()
    {
        var limits = DocumentLimits.Default with { MaxPages = 5, MaxConsecutiveEmptyPages = 2 };
        var guard = new LayoutGuard(limits, CancellationToken.None);
        for (var i = 0; i < 5; i++)
            guard.PageFinished(empty: false);
        Assert.Contains("more than 5 pages", Assert.Throws<ConversionRefusedException>(() => guard.PageFinished(empty: false)).Detail);

        var stuck = new LayoutGuard(limits with { MaxPages = 100 }, CancellationToken.None);
        stuck.PageFinished(empty: true);
        stuck.PageFinished(empty: true);
        stuck.PageFinished(empty: false); // something placed: the count starts again
        stuck.PageFinished(empty: true);
        stuck.PageFinished(empty: true);
        Assert.Contains("more than 2 empty pages in a row", Assert.Throws<ConversionRefusedException>(() => stuck.PageFinished(empty: true)).Detail);
    }

    [Fact]
    public void Every_paragraph_checks_cancellation_and_the_time_budget()
    {
        var elapsed = TimeSpan.Zero;
        var guard = new LayoutGuard(DocumentLimits.Default with { LayoutTimeBudget = TimeSpan.FromMinutes(1) }, CancellationToken.None, () => elapsed);
        guard.Paragraph();
        elapsed = TimeSpan.FromSeconds(61);
        var refusal = Assert.Throws<ConversionRefusedException>(guard.Paragraph);
        Assert.Equal(DocumentLimits.ErrorKey, refusal.ErrorKey);
        Assert.Contains("longer than 1 minute.", refusal.Detail);

        using var cancelled = new CancellationTokenSource();
        var cancellable = new LayoutGuard(DocumentLimits.Default, cancelled.Token);
        cancellable.Paragraph();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(cancellable.Paragraph);
    }

    [Theory]
    [InlineData(100, 50, 200, 300, 100, 50, false)] // fits
    [InlineData(500, 50, 200, 300, 200, 50, true)] // too wide: clipped to the column
    [InlineData(100, 9e9, 200, 300, 100, 300, true)] // taller than any page: clipped and placed, not moved on
    [InlineData(double.NaN, double.PositiveInfinity, 200, 300, 0, 0, false)] // sizes that are no numbers
    [InlineData(-5, 10, 200, 300, 0, 10, false)]
    public void An_object_that_fits_no_page_is_clipped_and_placed(double width, double height, double areaWidth, double areaHeight,
        double placedWidth, double placedHeight, bool clipped)
    {
        Assert.Equal((placedWidth, placedHeight, clipped), LayoutGuard.Fit(width, height, areaWidth, areaHeight));
    }
}
