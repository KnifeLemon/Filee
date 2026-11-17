// Documents nested far deeper than any real one (thousands of tables in table cells, groups in groups, links in links,
// a note that refers to itself) and packages whose XML unpacks to more than Filee reads. A stack overflow cannot be
// caught and ended the whole app, with every watch folder; these must give a document with the deep part as plain
// text, or a clear refusal (error.document_too_large). Every input is built here.

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Filee.Core.Conversion;
using Filee.Engines.Ebooks;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Docx;
using Filee.Engines.Hwp.Hwpx.Pptx;
using Filee.Engines.Infrastructure;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

public class NestingLimitTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private const string TooLarge = "error.document_too_large";

    private static string Repeat(string text, int count) => string.Concat(Enumerable.Repeat(text, count));

    /// <summary>One-cell tables, each in the cell of the one before: every cell starts with "level n", the innermost holds "deepest".</summary>
    private static string NestedTables(int levels)
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= levels; i++)
            sb.Append("<w:tbl><w:tr><w:tc>").Append(P(R($"level {i}")));
        sb.Append(P(R("deepest")));
        sb.Append(Repeat("</w:tc></w:tr></w:tbl>", levels));
        return sb.Append(P(R("after"))).ToString();
    }

    private string SaveDocx(DocxBuilder docx, string name) => docx.Save(Path.Combine(fx.NewFolder(), name + ".docx"));

    private static List<string> Texts(HDocument document) =>
        [.. document.Sections.SelectMany(s => HDocumentWalker.Inlines(s.Blocks)).OfType<HText>().Select(t => t.Text)];

    /// <summary>Containers (cells, notes, text boxes, links) around the deepest content, counted without recursion.</summary>
    private static int Depth(HDocument document)
    {
        var deepest = 0;
        var pending = new Stack<(object Item, int Depth)>();
        foreach (var block in document.Sections.SelectMany(s => s.Blocks))
            pending.Push((block, 0));
        while (pending.TryPop(out var entry))
        {
            var (item, depth) = entry;
            deepest = Math.Max(deepest, depth);
            IEnumerable<object> children = item switch
            {
                HParagraph paragraph => paragraph.Inlines,
                HTable table => table.Rows.SelectMany(r => r.Cells).SelectMany(c => c.Blocks).Concat(table.Caption),
                HLink link => link.Content,
                HNote note => note.Blocks,
                HTextBox box => box.Blocks,
                _ => [],
            };
            var inner = item is HTable or HLink or HNote or HTextBox ? depth + 1 : depth;
            foreach (var child in children)
                pending.Push((child, inner));
        }
        return deepest;
    }

    /// <summary>Writes the document in every format the model goes to: none of the writers may overflow the stack.</summary>
    private void WriteAll(HDocument document)
    {
        var folder = fx.NewFolder();
        HwpxWriter.Write(document, Path.Combine(folder, "out.hwpx"));
        DocxWriter.Write(document, Path.Combine(folder, "out.docx"));
        Assert.NotEmpty(XhtmlWriter.Write(document, new XhtmlOptions { ImageSource = _ => null }));
        Assert.NotEmpty(MarkdownWriter.Write(document, _ => null));
        Assert.NotEmpty(PlainTextWriter.Write(document));
        Assert.True(new FileInfo(Path.Combine(folder, "out.hwpx")).Length > 0);
        Assert.True(new FileInfo(Path.Combine(folder, "out.docx")).Length > 0);
    }

    // ───────────────────────── DOCX ─────────────────────────

    [Fact]
    public void Tables_nested_past_the_limit_keep_their_text_as_plain_paragraphs()
    {
        var docx = new DocxBuilder();
        docx.Paragraph(NestedTables(300));
        var document = DocxReader.Read(SaveDocx(docx, "tables"), Path.Combine(fx.NewFolder(), "media"));

        Assert.InRange(Depth(document), Nesting.MaxDepth, Nesting.MaxDepth + 1);
        var texts = Texts(document);
        foreach (var expected in new[] { "level 1", "level 32", "level 33", "level 300", "deepest", "after" })
            Assert.Contains(expected, texts);
        WriteAll(document);
    }

    [Fact]
    public async Task Five_thousand_nested_tables_are_refused_not_a_stack_overflow()
    {
        var docx = new DocxBuilder();
        docx.Paragraph(NestedTables(5_000));
        var path = SaveDocx(docx, "five-thousand");

        var error = Assert.Throws<ConversionRefusedException>(() => DocxReader.Read(path, Path.Combine(fx.NewFolder(), "media")));
        Assert.Equal(TooLarge, error.ErrorKey);
        Assert.Contains("word/document.xml", error.Detail);
        Assert.Contains("1000 levels", error.Detail);

        var (key, detail) = await fx.ConvertFailsAsync(path, "hwpx");
        Assert.Equal(TooLarge, key);
        Assert.Contains("levels", detail);
    }

    [Fact]
    public void Text_boxes_nested_past_the_limit_keep_their_text()
    {
        var content = new StringBuilder();
        const int levels = 60;
        for (var i = 1; i <= levels; i++)
        {
            content.Append(P(R($"box {i}"))).Append("<w:p><w:r><w:drawing><wp:inline><wp:extent cx=\"914400\" cy=\"457200\"/><wp:docPr id=\"1\" name=\"b\"/>")
                .Append($"<a:graphic><a:graphicData uri=\"{ShapeUri}\"><wps:wsp><wps:spPr><a:prstGeom prst=\"rect\"/></wps:spPr><wps:txbx><w:txbxContent>");
        }
        content.Append(P(R("innermost box")));
        content.Append(Repeat("</w:txbxContent></wps:txbx><wps:bodyPr/></wps:wsp></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>", levels));
        var docx = new DocxBuilder();
        docx.Paragraph(content.ToString());
        var document = DocxReader.Read(SaveDocx(docx, "boxes"), Path.Combine(fx.NewFolder(), "media"));

        Assert.InRange(Depth(document), Nesting.MaxDepth, Nesting.MaxDepth + 1);
        var texts = Texts(document);
        foreach (var expected in new[] { "box 1", "box 33", "box 60", "innermost box" })
            Assert.Contains(expected, texts);
        WriteAll(document);
    }

    [Fact]
    public void A_footnote_that_refers_to_itself_is_read_once()
    {
        var docx = new DocxBuilder();
        var reference = "<w:r><w:footnoteReference w:id=\"1\"/></w:r>";
        docx.Footnotes($"<w:footnote w:id=\"1\">{P(R("note text") + reference + reference)}</w:footnote>");
        docx.Paragraph(P(R("body") + reference));
        var document = DocxReader.Read(SaveDocx(docx, "self-note"), Path.Combine(fx.NewFolder(), "media"));

        var note = Assert.Single(document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HNote>());
        Assert.Empty(HDocumentWalker.Inlines(note.Blocks).OfType<HNote>());
        Assert.Contains("note text", Texts(document));
    }

    [Fact]
    public void Hyperlink_fields_begun_without_end_do_not_pile_up()
    {
        var field = "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> HYPERLINK \"https://example.com/\" </w:instrText></w:r>" +
                    "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>" + R("t");
        var docx = new DocxBuilder();
        docx.Paragraph(P(Repeat(field, 10_000)));
        var watch = Stopwatch.StartNew();
        var document = DocxReader.Read(SaveDocx(docx, "fields"), Path.Combine(fx.NewFolder(), "media"));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
        Assert.InRange(Depth(document), 1, Nesting.MaxDepth);
        Assert.Equal(10_000, Texts(document).Count(t => t == "t"));
        WriteAll(document);
    }

    [Fact]
    public void A_chain_of_a_hundred_thousand_based_on_styles_is_followed_only_so_far()
    {
        var docx = new DocxBuilder();
        var styles = new StringBuilder(docx.Styles);
        for (var i = 0; i < 100_000; i++)
            styles.Append($"<w:style w:type=\"paragraph\" w:styleId=\"s{i}\"><w:name w:val=\"s{i}\"/><w:basedOn w:val=\"s{i + 1}\"/></w:style>");
        docx.Styles = styles.ToString();
        docx.Paragraph(P(R("styled"), "<w:pStyle w:val=\"s0\"/>"));
        // Followed one style at a time, the chain would need more stack than even the readers' large one has.
        var document = DocxReader.Read(SaveDocx(docx, "styles"), Path.Combine(fx.NewFolder(), "media"));

        Assert.Contains("styled", Texts(document));
    }

    [Fact]
    public void An_equation_nested_past_the_limit_becomes_its_text()
    {
        const int levels = 300;
        var math = "<m:oMath>" + Repeat("<m:f><m:num>", levels) + "<m:r><m:t>x</m:t></m:r>" +
                   Repeat("</m:num><m:den><m:r><m:t>y</m:t></m:r></m:den></m:f>", levels) + "</m:oMath>";
        var docx = new DocxBuilder();
        docx.Paragraph($"<w:p>{math}</w:p>");
        var document = DocxReader.Read(SaveDocx(docx, "equation"), Path.Combine(fx.NewFolder(), "media"));

        var equation = Assert.Single(document.Sections.SelectMany(s => HDocumentWalker.Inlines(s.Blocks)).OfType<HEquation>());
        Assert.Equal(1 + levels, equation.Text.Count(c => c is 'x' or 'y'));
        WriteAll(document);
    }

    // ───────────────────────── PPTX ─────────────────────────

    /// <summary>Groups in groups, each holding a text box "level n" next to the group it contains.</summary>
    private static string NestedGroups(PptxBuilder pptx, int levels)
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= levels; i++)
        {
            sb.Append($"<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"{100_000 + i}\" name=\"g\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>");
            if (i % 100 == 1 || i == levels)
                sb.Append(pptx.Shape("rect", 0, 0, 914400, 457200, "#336699", $"level {i}"));
        }
        return sb.Append(Repeat("</p:grpSp>", levels)).ToString();
    }

    [Fact]
    public void Groups_nested_hundreds_deep_are_all_read()
    {
        var pptx = new PptxBuilder();
        pptx.Slide(NestedGroups(pptx, 900));
        var document = PptxReader.Read(pptx.Save(Path.Combine(fx.NewFolder(), "groups.pptx")), Path.Combine(fx.NewFolder(), "media"));

        var texts = Texts(document);
        foreach (var expected in new[] { "level 1", "level 101", "level 801", "level 900" })
            Assert.Contains(expected, texts);
        WriteAll(document);
    }

    [Fact]
    public void Five_thousand_nested_groups_are_refused_not_a_stack_overflow()
    {
        var pptx = new PptxBuilder();
        pptx.Slide(NestedGroups(pptx, 5_000));
        var path = pptx.Save(Path.Combine(fx.NewFolder(), "groups.pptx"));

        var error = Assert.Throws<ConversionRefusedException>(() => PptxReader.Read(path, Path.Combine(fx.NewFolder(), "media")));
        Assert.Equal(TooLarge, error.ErrorKey);
        Assert.Contains("ppt/slides/slide1.xml", error.Detail);
    }

    // ───────────────────────── HWPX ─────────────────────────

    /// <summary>An HWPX from the writer whose paragraph "MARK" is replaced by <paramref name="levels"/> tables nested in each other's cell.</summary>
    private string NestedHwpxTables(int levels)
    {
        var document = new HDocument();
        var section = new HSection();
        var paragraph = new HParagraph();
        paragraph.Inlines.Add(new HText("MARK", default));
        section.Blocks.Add(paragraph);
        document.Sections.Add(section);
        var path = Path.Combine(fx.NewFolder(), "nested.hwpx");
        HwpxWriter.Write(document, path);

        var tables = new StringBuilder();
        for (var i = 1; i <= levels; i++)
            tables.Append($"<hp:tbl rowCnt=\"1\" colCnt=\"1\"><hp:tr><hp:tc><hp:subList><hp:p><hp:run><hp:t>level {i}</hp:t></hp:run></hp:p><hp:p><hp:run>");
        tables.Append("<hp:t>deepest</hp:t>").Append(Repeat("</hp:run></hp:p></hp:subList></hp:tc></hp:tr></hp:tbl>", levels));
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.GetEntry("Contents/section0.xml")!;
        string xml;
        using (var reader = new StreamReader(entry.Open()))
            xml = reader.ReadToEnd();
        Assert.Contains("<hp:t>MARK</hp:t>", xml);
        xml = xml.Replace("<hp:t>MARK</hp:t>", tables + "<hp:t>after</hp:t>", StringComparison.Ordinal);
        entry.Delete();
        using (var writer = new StreamWriter(zip.CreateEntry("Contents/section0.xml").Open(), new UTF8Encoding(false)))
            writer.Write(xml);
        return path;
    }

    [Fact]
    public void Hwpx_tables_nested_past_the_limit_keep_their_text()
    {
        var document = HwpxReader.Read(NestedHwpxTables(150), Path.Combine(fx.NewFolder(), "media"));

        Assert.InRange(Depth(document), Nesting.MaxDepth, Nesting.MaxDepth + 1);
        var texts = Texts(document);
        foreach (var expected in new[] { "level 1", "level 33", "level 150", "deepest", "after" })
            Assert.Contains(expected, texts);
        WriteAll(document);
    }

    [Fact]
    public void Five_thousand_nested_hwpx_tables_are_refused()
    {
        var error = Assert.Throws<ConversionRefusedException>(() => HwpxReader.Read(NestedHwpxTables(5_000), Path.Combine(fx.NewFolder(), "media")));
        Assert.Equal(TooLarge, error.ErrorKey);
        Assert.Contains("Contents/section0.xml", error.Detail);
    }

    // ───────────────────────── Writers ─────────────────────────

    [Fact]
    public void Writers_open_up_a_model_nested_thousands_deep()
    {
        // Built in code, as a reader without its own limit (Pandoc's AST, the PDF reader) could hand it over.
        const int levels = 5_000;
        var document = new HDocument();
        var section = new HSection();
        document.Sections.Add(section);
        var blocks = section.Blocks;
        for (var i = 0; i < levels; i++)
        {
            var table = new HTable { ColumnCount = 1 };
            var row = new HRow();
            var cell = new HCell { Column = 0, ColSpan = 1 };
            row.Cells.Add(cell);
            table.Rows.Add(row);
            blocks.Add(table);
            blocks = cell.Blocks;
        }
        var paragraph = new HParagraph();
        blocks.Add(paragraph);
        var inlines = paragraph.Inlines;
        for (var i = 0; i < levels; i++)
        {
            var link = new HLink("https://example.com/" + i);
            inlines.Add(link);
            inlines = link.Content;
        }
        inlines.Add(new HText("deepest", default));

        WriteAll(document);

        Assert.InRange(Depth(document), Nesting.MaxDepth, Nesting.MaxDepth + 1);
        Assert.Contains("deepest", Texts(document));
    }

    [Fact]
    public void A_model_within_the_limit_is_left_as_it_is()
    {
        var docx = new DocxBuilder();
        docx.Paragraph(NestedTables(5));
        var document = DocxReader.Read(SaveDocx(docx, "five"), Path.Combine(fx.NewFolder(), "media"));
        var before = HDocumentAssert.Dump(document, layout: true);

        Nesting.Limit(document);

        Assert.Equal(before, HDocumentAssert.Dump(document, layout: true));
        Assert.Equal(5, Depth(document));
    }

    // ───────────────────────── XML size ─────────────────────────

    [Fact]
    public void An_xml_part_unpacking_past_the_limit_is_refused_before_it_is_read()
    {
        var path = Path.Combine(fx.NewFolder(), "bomb.docx");
        var docx = new DocxBuilder();
        docx.Paragraph(P(R("x")));
        docx.Save(path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            zip.GetEntry("word/document.xml")!.Delete();
            using var stream = zip.CreateEntry("word/document.xml", CompressionLevel.Fastest).Open();
            var spaces = new byte[1024 * 1024];
            Array.Fill(spaces, (byte)' ');
            stream.Write(Encoding.UTF8.GetBytes($"<w:document {Namespaces}><w:body>"));
            for (var i = 0; i * spaces.Length <= BoundedXml.MaxPartBytes; i++)
                stream.Write(spaces);
            stream.Write(Encoding.UTF8.GetBytes("</w:body></w:document>"));
        }
        Assert.InRange(new FileInfo(path).Length, 1, 4 * 1024 * 1024); // a small file that unpacks to 257 MB

        var error = Assert.Throws<ConversionRefusedException>(() => DocxReader.Read(path, Path.Combine(fx.NewFolder(), "media")));
        Assert.Equal(TooLarge, error.ErrorKey);
        Assert.Equal("word/document.xml: over 256 MB", error.Detail);
    }

    [Fact]
    public void Parts_are_counted_together_against_the_package_limit()
    {
        var path = Path.Combine(fx.NewFolder(), "parts.zip");
        var part = "<a>" + new string('x', 800) + "</a>";
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            EbookBuilders.Entry(zip, "one.xml", part);
            EbookBuilders.Entry(zip, "two.xml", part);
            EbookBuilders.Entry(zip, "big.xml", part + part);
        }
        using var read = ZipFile.OpenRead(path);
        var xml = new BoundedXml(maxPart: 1000, maxPackage: 1500);
        Assert.Equal(800, xml.Load(read.GetEntry("one.xml")!).Root!.Value.Length);
        var total = Assert.Throws<ConversionRefusedException>(() => xml.Load(read.GetEntry("two.xml")!));
        Assert.Equal("two.xml: the package unpacks to over 1500 bytes", total.Detail);
        var single = Assert.Throws<ConversionRefusedException>(() => new BoundedXml(1000, 10_000).Load(read.GetEntry("big.xml")!));
        Assert.Equal("big.xml: over 1000 bytes", single.Detail);
    }

    [Fact]
    public void Xml_parts_with_a_dtd_are_refused_and_whitespace_follows_the_options()
    {
        var path = Path.Combine(fx.NewFolder(), "dtd.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            EbookBuilders.Entry(zip, "dtd.xml", "<!DOCTYPE a [<!ENTITY e \"boom\">]><a>&e;</a>");
            EbookBuilders.Entry(zip, "space.xml", "<a> <b/> </a>");
        }
        using var read = ZipFile.OpenRead(path);
        Assert.Throws<System.Xml.XmlException>(() => BoundedXml.Part(read.GetEntry("dtd.xml")!));
        Assert.Empty(BoundedXml.Part(read.GetEntry("space.xml")!).Root!.Nodes().OfType<System.Xml.Linq.XText>());
        Assert.Equal(2, BoundedXml.Part(read.GetEntry("space.xml")!, System.Xml.Linq.LoadOptions.PreserveWhitespace).Root!.Nodes().OfType<System.Xml.Linq.XText>().Count());
    }

    // ───────────────────────── E-books ─────────────────────────

    [Fact]
    public void An_epub_entry_past_the_limit_is_refused_while_it_is_unpacked()
    {
        var path = Path.Combine(fx.NewFolder(), "book.epub");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            EbookBuilders.Entry(zip, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            EbookBuilders.Entry(zip, "OEBPS/small.xhtml", new string('a', 900));
            EbookBuilders.Entry(zip, "OEBPS/also-small.xhtml", new string('b', 900));
            EbookBuilders.Entry(zip, "OEBPS/big.xhtml", new string('c', 5000));
        }
        using var read = ZipFile.OpenRead(path);

        var entry = Assert.Throws<ConversionRefusedException>(() => EbookFiles.Extract(read, fx.NewFolder(), maxEntryBytes: 1000, maxTotalBytes: 100_000));
        Assert.Equal(TooLarge, entry.ErrorKey);
        Assert.Equal("OEBPS/big.xhtml: over 1000 bytes", entry.Detail);

        var total = Assert.Throws<ConversionRefusedException>(() => EbookFiles.Extract(read, fx.NewFolder(), maxEntryBytes: 10_000, maxTotalBytes: 1500));
        Assert.Equal("OEBPS/also-small.xhtml: the book unpacks to over 1500 bytes", total.Detail);

        var folder = fx.NewFolder();
        EbookFiles.Extract(read, folder, maxEntryBytes: 10_000, maxTotalBytes: 100_000);
        Assert.Equal(5000, new FileInfo(Path.Combine(folder, "OEBPS", "big.xhtml")).Length);
    }

    [Fact]
    public void An_fb2_note_that_links_to_itself_is_read_once()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <FictionBook xmlns="http://www.gribuser.ru/xml/fictionbook/2.0" xmlns:l="http://www.w3.org/1999/xlink">
             <description><title-info><book-title>Notes</book-title></title-info></description>
             <body><section><p>Body<a l:href="#n1" type="note">[1]</a></p></section></body>
             <body name="notes"><section id="n1"><p>Note<a l:href="#n1">[1]</a><a l:href="#n1">[1]</a></p></section></body>
            </FictionBook>
            """;
        var path = Path.Combine(fx.NewFolder(), "notes.fb2");
        File.WriteAllText(path, xml);
        var document = Fb2Reader.Read(path, fx.NewFolder());

        var note = Assert.Single(document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().SelectMany(p => p.Inlines).OfType<HNote>());
        Assert.Empty(HDocumentWalker.Inlines(note.Blocks).OfType<HNote>());
    }

    [Fact]
    public void Fb2_sections_nested_past_the_xml_limit_are_refused()
    {
        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><FictionBook xmlns=\"http://www.gribuser.ru/xml/fictionbook/2.0\"><body>" +
                  Repeat("<section>", 5_000) + "<p>deep</p>" + Repeat("</section>", 5_000) + "</body></FictionBook>";
        var path = Path.Combine(fx.NewFolder(), "deep.fb2");
        File.WriteAllText(path, xml);

        var error = Assert.Throws<ConversionRefusedException>(() => Fb2Reader.Read(path, fx.NewFolder()));
        Assert.Equal(TooLarge, error.ErrorKey);
    }

    // ───────────────────────── Markdown ─────────────────────────

    [Theory]
    [InlineData(">")]
    [InlineData("- ")]
    public void Markdown_nested_thousands_deep_is_refused_by_markdig_itself(string marker)
    {
        // Markdig has a depth limit of its own; the reader relies on it.
        var deep = Assert.Throws<ArgumentException>(() => MarkdownReader.Read(Repeat(marker, 20_000) + "deep\n", fx.NewFolder()));
        Assert.Contains("too deeply nested", deep.Message);

        var document = MarkdownReader.Read(Repeat(marker, 40) + "deep\n", fx.NewFolder());
        Assert.Contains(Texts(document), t => t.Contains("deep", StringComparison.Ordinal));
        WriteAll(document);
    }
}
