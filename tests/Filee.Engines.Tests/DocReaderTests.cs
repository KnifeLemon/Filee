// Word 97–2003 (DOC) → document model (Hwp/Hwpx/Doc): DOC files built in code (DocBuilder) check what the reader
// takes from each structure (pieces, FKPs, styles, sections, headers, notes, lists, tables, fields, bookmarks,
// pictures, text boxes), the files Filee refuses or hands to other readers, and the routes that no longer need
// LibreOffice. Hostile files are in DocumentHostileInputTests, mutated ones in DocumentFuzzTests; DocFidelityTests
// compares DOC files LibreOffice saves with the DOCX they came from.

using System.IO.Compression;
using System.Xml.Linq;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Hwp.Hwpx.Doc;
using static Filee.Engines.Tests.DocBuilder;

namespace Filee.Engines.Tests;

public class DocReaderTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    private string Write(DocBuilder builder, string name = "built.doc")
    {
        var path = Path.Combine(fx.NewFolder(), name);
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    private static HDocument Read(string path) => DocReader.Read(path, Path.Combine(Path.GetDirectoryName(path)!, "media"));

    private static List<HParagraph> Paragraphs(HDocument document) => document.Sections.SelectMany(s => s.Blocks).OfType<HParagraph>().ToList();

    private static string Text(HParagraph paragraph) => string.Concat(paragraph.Inlines.Select(i => i switch
    {
        HText t => t.Text,
        HTab => "\t",
        HLineBreak => "\n",
        HLink link => string.Concat(link.Content.OfType<HText>().Select(t => t.Text)),
        HField { Kind: HFieldKind.PageNumber } => "#",
        _ => "",
    }));

    /// <summary>The document.xml DocToDocx writes for <paramref name="path"/> (to see what DocxReader is given).</summary>
    private XDocument Package(string path, string part = "word/document.xml")
    {
        var docx = Path.Combine(fx.NewFolder(), "package.docx");
        DocReader.ToDocx(path, docx);
        using var zip = ZipFile.OpenRead(docx);
        using var stream = zip.GetEntry(part)!.Open();
        return XDocument.Load(stream);
    }

    [Fact]
    public void Reads_paragraphs_with_character_and_paragraph_properties()
    {
        var builder = new DocBuilder { Title = "제목 있는 문서" };
        builder.Paragraph(1, [], R("Heading"))
            .Paragraph(0, [.. Sprm(0x2403, (byte)1), .. Sprm(0xA413, (short)120), .. Sprm(0x6412, (short)360, (short)1)],
                R("Plain "), R("bold", Sprm(0x0835, (byte)1)), R(" and "), R("red italic", Sprm(0x0836, (byte)1), Sprm(0x2A42, (byte)6)), R(" 한글"))
            .Paragraph(0, [.. Sprm(0x840F, (short)720), .. Sprm(0x8411, (short)-360)], R("Arial 14", Sprm(0x4A4F, (ushort)2), Sprm(0x4A43, (ushort)28)), R("\ttab\vline"));
        var document = Read(Write(builder));

        Assert.Equal("제목 있는 문서", document.Title);
        var paragraphs = Paragraphs(document);
        Assert.Equal(["Heading", "Plain bold and red italic 한글", "Arial 14\ttab\nline"], paragraphs.Select(Text));
        Assert.Equal(1, paragraphs[0].HeadingLevel);
        Assert.True(((HText)paragraphs[0].Inlines[0]).Format.Bold);
        Assert.Equal(1600, ((HText)paragraphs[0].Inlines[0]).Format.Size);

        var second = paragraphs[1];
        Assert.Equal(HAlign.Center, second.Format.Align);
        Assert.Equal(HwpxUnits.FromTwips(120), second.Format.Before);
        Assert.Equal(new HLineSpacing(HLineSpacingKind.Percent, 195), second.Format.LineSpacing);
        var runs = second.Inlines.OfType<HText>().ToList();
        Assert.False(runs[0].Format.Bold);
        Assert.True(runs[1].Format.Bold);
        Assert.Equal("#FF0000", runs[3].Format.Color);
        Assert.True(runs[3].Format.Italic);
        Assert.Equal("Times New Roman", runs[0].Format.Font);

        var third = paragraphs[2];
        Assert.Equal(HwpxUnits.FromTwips(720), third.Format.Left);
        Assert.Equal(HwpxUnits.FromTwips(-360), third.Format.FirstLine);
        var arial = (HText)third.Inlines[0];
        Assert.Equal("Arial", arial.Format.Font);
        Assert.Equal(1400, arial.Format.Size);
    }

    [Fact]
    public void Reads_8_bit_and_unicode_pieces()
    {
        var builder = new DocBuilder { Compressed = true };
        builder.Paragraph(R("Café “quoted” — 한국어 text €"), R(" bold", Sprm(0x0835, (byte)1)));
        var path = Write(builder);
        var paragraphs = Paragraphs(Read(path));
        Assert.Equal("Café “quoted” — 한국어 text € bold", Text(paragraphs[0]));
        Assert.True(((HText)paragraphs[0].Inlines[^1]).Format.Bold);
    }

    /// <summary>A table: cell boundaries, a horizontal merge, a vertical merge, shading, borders, a header row and a nested table.</summary>
    [Fact]
    public void Reads_tables()
    {
        var builder = new DocBuilder();
        var single = Brc80(4, 1);
        var borders = Sprm(0xD605, single, single, single, single, single, single);
        var first = (byte[])[.. DefTable([-108, 1892, 3892, 5892], [1, 2, 3 << 5]), .. Sprm(0x9602, (short)108), .. borders, .. Sprm(0x3404, (byte)1),
            .. Sprm(0xD612, (byte[])[0, 0, 0, 0xFF, 0xFF, 0xFF, 0x00, 0, 0, 0])];
        var second = (byte[])[.. DefTable([-108, 1892, 3892, 5892], [0, 0, 1 << 5]), .. Sprm(0x9602, (short)108), .. borders, .. Sprm(0x9407, (short)-400)];
        builder.Paragraph(R("before"))
            .Row(first, [R("merged")], [], [R("tall")])
            .NestedRow(2, [.. DefTable([0, 1000]), .. Sprm(0x9602, (short)50)], [R("inner")])
            .Row(second, [R("a")], [R("b")], [])
            .Paragraph(R("after"));
        var document = Read(Write(builder));
        var blocks = document.Sections[0].Blocks;
        Assert.IsType<HParagraph>(blocks[0]);
        var table = Assert.IsType<HTable>(blocks[1]);
        Assert.IsType<HParagraph>(blocks[2]);
        Assert.Equal([2000, 2000, 2000], table.ColumnWidths!.Select(w => w / 5));
        Assert.True(table.Rows[0].Header);
        var merged = table.Rows[0].Cells[0];
        Assert.Equal(2, merged.ColSpan);
        Assert.Equal("#FFFF00", merged.Fill);
        var tall = table.Rows[0].Cells[1];
        Assert.Equal(2, tall.RowSpan);
        Assert.Equal(HBorderStyle.Solid, tall.Borders!.Value.Top.Style);
        Assert.Equal(HwpxUnits.FromTwips(400), table.Rows[1].Height);
        // The nested table is in the first cell of the second row.
        var inner = Assert.IsType<HTable>(table.Rows[1].Cells[0].Blocks.First(b => b is HTable));
        Assert.Equal("inner", Text((HParagraph)inner.Rows[0].Cells[0].Blocks[0]));
        Assert.Equal("b", Text((HParagraph)table.Rows[1].Cells[1].Blocks[0]));
    }

    [Fact]
    public void Reads_lists()
    {
        var builder = new DocBuilder();
        var numbered = builder.List(100, [new Level(0, "\u0000.", 1, Sprm(0x845E, (short)720)), new Level(4, "\u0000.\u0001)", 1, Sprm(0x845E, (short)1440))]);
        var bullets = builder.List(200, [new Level(23, "", 1, Sprm(0x845E, (short)720), Sprm(0x4A4F, (ushort)1))], simple: true);
        var restarted = builder.Instance(100, (0, 5));
        builder.Paragraph(0, [.. Sprm(0x460B, (short)numbered)], R("one"))
            .Paragraph(0, [.. Sprm(0x460B, (short)numbered), .. Sprm(0x260A, (byte)1)], R("one a"))
            .Paragraph(0, [.. Sprm(0x460B, (short)bullets)], R("bullet"))
            .Paragraph(0, [.. Sprm(0x460B, (short)restarted)], R("five"));
        var paragraphs = Paragraphs(Read(Write(builder)));
        var first = paragraphs[0].List!;
        Assert.Equal("^1.", first.Numbering.Levels[0].Text);
        Assert.Equal("^1.^2)", first.Numbering.Levels[1].Text);
        Assert.Equal("LATIN_SMALL", first.Numbering.Levels[1].Format);
        Assert.Equal(1, paragraphs[1].List!.Level);
        Assert.Same(first.Numbering, paragraphs[1].List!.Numbering);
        Assert.Equal(HwpxUnits.FromTwips(720), paragraphs[0].Format.Left);
        Assert.Equal(HwpxUnits.FromTwips(1440), paragraphs[1].Format.Left);
        var bullet = paragraphs[2].List!.Numbering.Levels[0];
        Assert.True(bullet.Bullet);
        Assert.Equal("•", bullet.Text);
        Assert.Equal(5, paragraphs[3].List!.Numbering.Levels[0].Start);
    }

    [Fact]
    public void Reads_sections_headers_footers_and_page_numbers()
    {
        var landscape = (byte[])[.. Sprm(0xB01F, (ushort)16838), .. Sprm(0xB020, (ushort)11906), .. Sprm(0x301D, (byte)2),
            .. Sprm(0xB021, (ushort)1134), .. Sprm(0xB022, (ushort)1134), .. Sprm(0x9023, (short)1418), .. Sprm(0x9024, (short)1418),
            .. Sprm(0x500B, (ushort)1), .. Sprm(0x900C, (ushort)567), .. Sprm(0x3019, (byte)1), .. Sprm(0x300A, (byte)1),
            .. Sprm(0x3011, (byte)1), .. Sprm(0x501C, (ushort)5)];
        var builder = new DocBuilder { LastSection = Sprm(0x3009, (byte)2) };
        builder.Paragraph(R("first section"))
            .EndSection(landscape, R("end of first"))
            .Paragraph(R("second section"))
            .Header(0, 1, R("odd header "), R("\u0013 PAGE \u00141\u0015"))
            .Header(0, 3, R("footer"))
            .Header(0, 4, R(""));
        var document = Read(Write(builder));
        Assert.Equal(2, document.Sections.Count);
        var page = document.Sections[0].Page!;
        Assert.True(page.Landscape);
        Assert.Equal(HwpxUnits.FromTwips(16838), page.Width);
        Assert.Equal(HwpxUnits.FromTwips(1134), page.Left);
        Assert.Equal(new HColumns(2, HwpxUnits.FromTwips(567), true), document.Sections[0].Columns);
        Assert.Equal(5, document.Sections[0].StartPageNumber);
        Assert.True(document.Sections[0].HideFirstHeader);
        var header = Assert.Single(document.Sections[0].Headers);
        Assert.Equal("odd header #", Text((HParagraph)header.Blocks[0]));
        Assert.Equal("footer", Text((HParagraph)Assert.Single(document.Sections[0].Footers).Blocks[0]));
        // The second section has no header stories of its own: it keeps the first's.
        Assert.Equal("odd header #", Text((HParagraph)Assert.Single(document.Sections[1].Headers).Blocks[0]));
        Assert.Equal(["first section", "end of first"], document.Sections[0].Blocks.OfType<HParagraph>().Select(Text));
        Assert.Equal(HwpxUnits.FromTwips(12240), document.Sections[1].Page!.Width);
    }

    [Fact]
    public void Reads_notes_fields_bookmarks_and_hyperlinks()
    {
        var builder = new DocBuilder();
        builder.Bookmark("target", 4)
            .Paragraph(R("Text"), builder.Footnote("a footnote"), R(" and "), builder.Footnote("an endnote", endnote: true))
            .Paragraph(R("\u0013 HYPERLINK \"https://example.invalid/\" \u0014link text\u0015 and \u0013 HYPERLINK \\l \"target\" \u0014back\u0015"));
        var document = Read(Write(builder));
        var paragraphs = Paragraphs(document);
        Assert.Equal("target", paragraphs[0].Inlines.OfType<HBookmark>().Single().Name);
        var notes = paragraphs[0].Inlines.OfType<HNote>().ToList();
        Assert.Equal([false, true], notes.Select(n => n.Endnote));
        Assert.Equal("a footnote", Text((HParagraph)notes[0].Blocks[0]));
        Assert.Equal("an endnote", Text((HParagraph)notes[1].Blocks[0]));
        var links = paragraphs[1].Inlines.OfType<HLink>().ToList();
        Assert.Equal(["https://example.invalid/", "#target"], links.Select(l => l.Target));
        Assert.Equal("link text and back", Text(paragraphs[1]));
    }

    [Fact]
    public void Reads_inline_and_floating_pictures_and_text_boxes()
    {
        var png = DocumentFuzzTests.TinyPng(4, 3);
        var builder = new DocBuilder();
        builder.Paragraph(R("picture "), builder.Picture(png, 1440, 1080), R(" box"), builder.TextBox(2049, "inside the box"),
            builder.FloatingPicture(2050, png, 100, 200, 720, 540));
        var paragraph = Paragraphs(Read(Write(builder)))[0];
        var images = paragraph.Inlines.OfType<HImage>().ToList();
        Assert.Equal(2, images.Count);
        Assert.Equal(png, File.ReadAllBytes(images[0].Path));
        Assert.Equal(HwpxUnits.FromTwips(1440), images[0].Width);
        Assert.Null(images[0].Anchor);
        Assert.Equal("PARA", images[1].Anchor!.VerticalRelativeTo);
        Assert.Equal(HwpxUnits.FromTwips(200), images[1].Anchor!.VerticalOffset);
        Assert.Equal(HwpxUnits.FromTwips(720), images[1].Width);
        var box = paragraph.Inlines.OfType<HTextBox>().Single();
        Assert.Equal("inside the box", Text((HParagraph)box.Blocks[0]));
        Assert.Equal("#DDEEFF", box.Fill);
        Assert.Equal("PAPER", box.Anchor!.HorizontalRelativeTo);
    }

    [Fact]
    public void Writes_styles_with_their_base_styles()
    {
        var builder = new DocBuilder();
        builder.Styles.Add(new Style("Quote", 0xFFE, 1, 1, Sprm(0x2403, (byte)2), Sprm(0x0836, (byte)1)));
        builder.Styles.Add(new Style("Emphasis", 88, 2, 10, [], Sprm(0x0836, (byte)0)));
        var quote = builder.Styles.Count - 2;
        var emphasis = builder.Styles.Count - 1;
        builder.Paragraph(quote, [], R("quoted "), R("upright", Sprm(0x4A30, (ushort)emphasis)), R(" not bold", Sprm(0x0835, (byte)0x81)));
        var path = Write(builder);
        var styles = Package(path, "word/styles.xml");
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var quoteStyle = styles.Descendants(w + "style").Single(s => (string?)s.Element(w + "name")?.Attribute(w + "val") == "Quote");
        Assert.Equal("heading1", (string?)quoteStyle.Element(w + "basedOn")?.Attribute(w + "val"));
        var paragraph = Paragraphs(Read(path))[0];
        Assert.Equal(1, paragraph.HeadingLevel); // the outline level of heading 1, through basedOn
        Assert.Equal(HAlign.Right, paragraph.Format.Align);
        var runs = paragraph.Inlines.OfType<HText>().ToList();
        Assert.True(runs[0].Format.Bold); // heading 1's
        Assert.True(runs[0].Format.Italic);
        Assert.False(runs[1].Format.Italic); // the character style's
        Assert.False(runs[2].Format.Bold); // 0x81: the opposite of the paragraph style's
    }

    // ───────────────────────── Routes ─────────────────────────

    [Theory]
    [InlineData("doc")]
    [InlineData("dot")]
    [InlineData("wps")]
    public void Word_97_files_take_the_built_in_reader_even_with_libreoffice(string format)
    {
        var planner = fx.Catalog.CreatePlanner(["libreoffice", "rhwp"]);
        foreach (var target in new[] { "pdf", "docx", "odt", "hwpx", "txt", "md" })
        {
            var route = planner.Plan(format, target);
            Assert.NotNull(route);
            Assert.DoesNotContain(route.Steps, s => s.Converter.Id == "libreoffice");
            // The first step checks the file (Word 6.0 / 95 and Works files go to LibreOffice instead).
            Assert.True(route.Steps[0].Checked, $"{format} → {target}");
            Assert.Contains(planner.Plan(format, target, withoutChecked: true)!.Steps, s => s.Converter.Id == "libreoffice");
        }
        // HTML: as DOCX goes (LibreOffice's own export when it is installed).
        Assert.Equal(planner.Plan("docx", "html")!.Steps.Select(s => s.Converter.Id), planner.Plan(format, "html")!.Steps.Select(s => s.Converter.Id));
    }

    [Fact]
    public void Word_95_files_are_named_by_every_engine_that_reads_doc()
    {
        var builder = new DocBuilder { NFib = 0x0065 };
        builder.Paragraph(R("old"));
        var path = Write(builder);
        foreach (var engine in fx.Converters.OfType<Core.Conversion.ICapabilityCheck>().Where(c => c is not Typesetting.FileeRendererConverter))
            Assert.Equal("a Word 6.0 / 95 document", engine.FirstUnsupported(path, "doc"));
        var renderer = fx.Converters.OfType<Typesetting.FileeRendererConverter>().Single();
        Assert.Equal("a Word 6.0 / 95 document", renderer.FirstUnsupported(path, "doc"));

        var current = new DocBuilder();
        current.Paragraph(R("new"));
        Assert.Null(renderer.FirstUnsupported(Write(current), "doc"));
    }

    [Fact]
    public async Task Converts_doc_to_docx_and_hwpx_without_libreoffice()
    {
        var builder = new DocBuilder();
        builder.Paragraph(1, [], R("문서 제목"))
            .Paragraph(R("본문 "), R("굵게", Sprm(0x0835, (byte)1)))
            .Row([.. DefTable([-108, 2000, 4000]), .. Sprm(0x9602, (short)108)], [R("칸 1")], [R("칸 2")]);
        var path = Write(builder, "변환.doc");
        foreach (var target in new[] { "docx", "hwpx", "txt" })
        {
            var job = await fx.ConvertAsync([path], new Core.Presets.Preset { TargetFormat = target });
            Assert.True(job.State == Core.Conversion.JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
            var output = Assert.Single(job.Outputs);
            var back = target switch
            {
                "docx" => Hwp.Hwpx.Docx.DocxReader.Read(output, Path.Combine(fx.NewFolder(), "media")),
                "hwpx" => HwpxReader.Read(output, Path.Combine(fx.NewFolder(), "media")),
                _ => DocumentReaders.TextDocument(File.ReadAllBytes(output)),
            };
            var text = string.Join("\n", back.Sections.SelectMany(section => HDocumentWalker.InlineLists(section.Blocks)).Select(HDocumentWalker.Text));
            Assert.Contains("문서 제목", text, StringComparison.Ordinal);
            Assert.Contains("칸 2", text, StringComparison.Ordinal);
        }
    }

    // ───────────────────────── Files the reader refuses or hands on ─────────────────────────

    [Theory]
    [InlineData((ushort)0x0100, "password")]
    [InlineData((ushort)0x8100, "password")]
    public void Refuses_encrypted_documents(ushort flags, string message)
    {
        var builder = new DocBuilder { ExtraFlags = flags };
        builder.Paragraph(R("secret"));
        var path = Write(builder);
        Assert.Contains(message, Assert.Throws<InvalidDataException>(() => Read(path)).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(DocReader.Unsupported(path)); // LibreOffice can't open it without the password either
    }

    [Fact]
    public void Refuses_word_95_and_names_it_for_the_planner()
    {
        var builder = new DocBuilder { NFib = 0x0068 };
        builder.Paragraph(R("old"));
        var path = Write(builder);
        Assert.Contains("Word 6.0 / 95", Assert.Throws<InvalidDataException>(() => Read(path)).Message, StringComparison.Ordinal);
        Assert.Equal("a Word 6.0 / 95 document", DocReader.Unsupported(path));
    }

    [Fact]
    public void Names_works_documents_for_the_planner()
    {
        var path = Path.Combine(fx.NewFolder(), "works.wps");
        File.WriteAllBytes(path, HwpBuilder.Cfb([("CONTENTS", new byte[64]), ("MatOST", new byte[16])]));
        Assert.Equal("a Microsoft Works document", DocReader.Unsupported(path));
        Assert.Contains("Works", Assert.Throws<InvalidDataException>(() => Read(path)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_rtf_and_docx_with_a_doc_name()
    {
        var folder = fx.NewFolder();
        var rtf = Path.Combine(folder, "saved.doc");
        File.WriteAllText(rtf, @"{\rtf1\ansi {\b bold} plain\par}");
        Assert.Equal("bold plain", Text(Paragraphs(Read(rtf))[0]).Trim());
        var docx = Path.Combine(folder, "renamed.doc");
        File.Copy(DocumentFuzzTests.RichDocx(folder), docx);
        Assert.Contains(Paragraphs(Read(docx)), p => Text(p) == "제목");
        Assert.Null(DocReader.Unsupported(docx));
    }
}
