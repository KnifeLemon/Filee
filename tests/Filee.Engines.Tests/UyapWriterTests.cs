// The built-in UYAP writer (Uyap/UyapWriter, #1546, #1633): a model built in code with what a UYAP document holds
// (page setup, header and footer, paragraph and character formatting, tab stops, lists, tables with merged and shaded
// cells, pictures, page breaks) is written as .udf and read back by Filee's UYAP reader; content.xml is well-formed XML
// laid out as the editor writes it (one CDATA text, every element's offsets covering it in order); text XML can't hold
// is made safe without shifting offsets; documents go to UDF through the writer, and UDF to XLSX (pictures: UyapTests); and
// models past the document limits are refused without leaving a file.

using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;
using Filee.Engines.Documents;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Uyap;
using ImageMagick;

namespace Filee.Engines.Tests;

public class UyapWriterTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>Writes <paramref name="document"/>, checks its content.xml and reads it back with Filee's UYAP reader.</summary>
    private (HDocument Document, XDocument Xml, string Text) RoundTrip(HDocument document, DocumentLimits? limits = null)
    {
        var folder = fx.NewFolder();
        var path = Path.Combine(folder, "out.udf");
        UyapWriter.Write(document, path, limits ?? DocumentLimits.Default, TestContext.Current.CancellationToken);
        Assert.Single(Directory.GetFiles(folder)); // no temporary file left beside it
        var (xml, text) = AssertValidUdf(path);
        return (UyapReader.Read(path, Path.Combine(folder, "media")), xml, text);
    }

    /// <summary>
    /// A ZIP with one content.xml that is well-formed XML 1.0 (characters checked) with a template root, one content
    /// text, the page format and elements whose runs, pictures and page breaks cover the text from its start to its end,
    /// one after the other, as the editor writes them.
    /// </summary>
    internal static (XDocument Xml, string Text) AssertValidUdf(string path)
    {
        Assert.True(UyapReader.IsUyap(path));
        using var zip = ZipFile.OpenRead(path);
        var entry = Assert.Single(zip.Entries);
        Assert.Equal("content.xml", entry.FullName);
        XDocument xml;
        using (var stream = entry.Open())
        using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, CheckCharacters = true }))
            xml = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        var root = xml.Root!;
        Assert.Equal("template", root.Name.LocalName);
        Assert.Equal("1.8", root.Attribute("format_id")?.Value);
        var text = root.Element("content")!.Value;
        Assert.NotNull(root.Element("properties")?.Element("pageFormat"));
        Assert.Equal("hvl-default", root.Element("elements")?.Attribute("resolver")?.Value);
        Assert.Contains(root.Element("styles")!.Elements("style"), s => s.Attribute("name")?.Value == "hvl-default");

        var next = 0;
        foreach (var run in root.Element("elements")!.Descendants().Where(e => e.Attribute("startOffset") is not null))
        {
            var start = int.Parse(run.Attribute("startOffset")!.Value, System.Globalization.CultureInfo.InvariantCulture);
            var length = int.Parse(run.Attribute("length")!.Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(start == next, $"A run starts at {start}, the text before it ends at {next}.");
            Assert.True(length > 0, "An empty run.");
            next = start + length;
            if (run.Name.LocalName == "image")
                Assert.Equal("\uFFFC", text.Substring(start, length));
        }
        // The editor's text ends with one "\n" more than its elements cover.
        Assert.Equal(text.Length - 1, next);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        // Every paragraph ends with its "\n".
        foreach (var paragraph in root.Descendants("paragraph"))
        {
            var last = paragraph.Elements().Last(e => e.Attribute("startOffset") is not null);
            var end = int.Parse(last.Attribute("startOffset")!.Value, System.Globalization.CultureInfo.InvariantCulture)
                      + int.Parse(last.Attribute("length")!.Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal('\n', text[end - 1]);
        }
        return (xml, text);
    }

    private string Picture(MagickColor color, uint width = 40, uint height = 30, string extension = "png")
    {
        var path = Path.Combine(fx.NewFolder(), $"picture.{extension}");
        if (extension == "png")
        {
            File.WriteAllBytes(path, EbookBuilders.Png(width, height, color));
        }
        else
        {
            using var image = new MagickImage(color, width, height);
            image.Write(path, extension == "gif" ? MagickFormat.Gif : MagickFormat.Bmp);
        }
        return path;
    }

    private static HParagraph Para(params HInline[] inlines)
    {
        var paragraph = new HParagraph();
        paragraph.Inlines.AddRange(inlines);
        return paragraph;
    }

    private static HParagraph Para(string text, HCharFormat format = default) => Para(new HText(text, format));

    private static HCell Cell(string text, Action<HCell>? setup = null)
    {
        var cell = new HCell();
        cell.Blocks.Add(Para(text));
        setup?.Invoke(cell);
        return cell;
    }

    private static string Text(HParagraph paragraph) => string.Concat(paragraph.Inlines.Select(i => i switch
    {
        HText t => t.Text,
        HTab => "\t",
        HLineBreak => "\n",
        _ => "",
    }));

    private static List<HParagraph> Paragraphs(HDocument document) => document.Sections.Single().Blocks.OfType<HParagraph>().ToList();

    // ───────────────────────── Round trip ─────────────────────────

    [Fact]
    public void Paragraphs_runs_page_header_and_footer_come_back()
    {
        var document = new HDocument();
        var section = new HSection { Page = new HPage(59528, 84189, 7000, 5000, 2000, 1800, 2500, 2200, 0) };
        document.Sections.Add(section);
        var header = new HHeaderFooter(HPageType.Both);
        header.Blocks.Add(new HParagraph { Format = new HParaFormat(Align: HAlign.Right), Inlines = { new HText("Header text", new HCharFormat(Italic: true)) } });
        section.Headers.Add(header);
        var footer = new HHeaderFooter(HPageType.Both);
        footer.Blocks.Add(new HParagraph { Format = new HParaFormat(Align: HAlign.Center), Inlines = { new HText("Footer text", default) } });
        section.Footers.Add(footer);

        var blocks = section.Blocks;
        blocks.Add(new HParagraph
        {
            Format = new HParaFormat(Align: HAlign.Center),
            Inlines = { new HText("Sample Court Office", new HCharFormat(Bold: true, Size: 1400, Font: "Calibri")) },
        });
        blocks.Add(Para(
            new HText("Plain ", default),
            new HText("bold", new HCharFormat(Bold: true)),
            new HText(" italic", new HCharFormat(Italic: true, Size: 1050)),
            new HText(" under", new HCharFormat(Underline: true, Color: "#C00000")),
            new HText(" struck", new HCharFormat(Strike: true, Shade: "#FFFF00")),
            new HText("up", new HCharFormat(Superscript: true)),
            new HLineBreak(default),
            new HText("after a break", default),
            new HTab(default),
            new HText("after a tab", default)));
        blocks.Add(new HParagraph
        {
            Format = new HParaFormat(Align: HAlign.Justify, Left: 4700, Right: 1000, FirstLine: -2200, Before: 600, After: 300),
            Inlines = { new HText("1.", default), new HTab(default), new HText("A hanging item.", default) },
        });
        blocks.Add(new HParagraph
        {
            Format = new HParaFormat(Tabs: [new HTabStop(5000, HTabKind.Center, "NONE"), new HTabStop(10000, HTabKind.Right, "DOT"), new HTabStop(15000, HTabKind.Decimal, "NONE")]),
            Inlines = { new HText("Tabs", default), new HTab(default), new HText("centre", default) },
        });
        blocks.Add(new HParagraph
        {
            Format = new HParaFormat(LineSpacing: new HLineSpacing(HLineSpacingKind.Percent, 195)),
            Inlines = { new HText("One and a half lines", new HCharFormat(Font: "Calibri")) },
        });
        blocks.Add(new HParagraph { MarkFormat = new HCharFormat(Size: 2000) }); // an empty paragraph keeps its mark's size
        blocks.Add(new HParagraph { PageBreakBefore = true, Inlines = { new HText("On a new page", default) } });

        var (read, xml, text) = RoundTrip(document);

        var readSection = Assert.Single(read.Sections);
        Assert.Equal(section.Page, readSection.Page);
        Assert.Equal("Header text", Text(Assert.IsType<HParagraph>(Assert.Single(Assert.Single(readSection.Headers).Blocks))));
        Assert.Equal(HAlign.Right, Assert.IsType<HParagraph>(readSection.Headers[0].Blocks[0]).Format.Align);
        Assert.Equal("Footer text", Text(Assert.IsType<HParagraph>(Assert.Single(Assert.Single(readSection.Footers).Blocks))));
        // Header, body, footer: the text in that order.
        Assert.StartsWith("Header text\n", text, StringComparison.Ordinal);
        Assert.EndsWith("Footer text\n\n", text, StringComparison.Ordinal);

        var paragraphs = Paragraphs(read);
        Assert.Equal(7, paragraphs.Count);
        var title = paragraphs[0];
        Assert.Equal(HAlign.Center, title.Format.Align);
        var titleRun = Assert.IsType<HText>(Assert.Single(title.Inlines));
        Assert.Equal("Sample Court Office", titleRun.Text);
        Assert.Equal((true, 1400, "Calibri"), (titleRun.Format.Bold, titleRun.Format.Size, titleRun.Format.Font));

        var runs = paragraphs[1].Inlines;
        Assert.Equal("Plain bold italic under struckup\nafter a break\tafter a tab", Text(paragraphs[1]));
        Assert.Equal("Times New Roman", ((HText)runs[0]).Format.Font); // unset: the editor's body style
        Assert.True(((HText)runs[1]).Format.Bold);
        Assert.Equal((true, 1050), (((HText)runs[2]).Format.Italic, ((HText)runs[2]).Format.Size));
        Assert.Equal((true, "#C00000"), (((HText)runs[3]).Format.Underline, ((HText)runs[3]).Format.Color));
        Assert.Equal((true, "#FFFF00"), (((HText)runs[4]).Format.Strike, ((HText)runs[4]).Format.Shade));
        Assert.True(((HText)runs[5]).Format.Superscript);
        Assert.IsType<HLineBreak>(runs[6]);

        var item = paragraphs[2].Format;
        Assert.Equal((HAlign.Justify, 4700, 1000, -2200, 600, 300), (item.Align!.Value, item.Left!.Value, item.Right!.Value, item.FirstLine!.Value, item.Before!.Value, item.After!.Value));
        // A hanging paragraph has the editor's stop at the indent, as its own files have.
        Assert.Equal("0.0:0:0", xml.Root!.Element("elements")!.Elements("paragraph").ElementAt(2).Attribute("TabSet")?.Value);

        Assert.Equal([new HTabStop(5000, HTabKind.Center, "NONE"), new HTabStop(10000, HTabKind.Right, "DOT"), new HTabStop(15000, HTabKind.Decimal, "NONE")],
            paragraphs[3].Format.Tabs!);
        // 195 % in 한글's terms is a line and a half: LineSpacing 0.5, read back as Java's 1.5 × 15 pt Calibri line.
        Assert.Equal(new HLineSpacing(HLineSpacingKind.AtLeast, 2250), paragraphs[4].Format.LineSpacing);
        Assert.Empty(paragraphs[5].Inlines);
        Assert.Equal(2000, paragraphs[5].MarkFormat.Size);
        Assert.True(paragraphs[6].PageBreakBefore);
        Assert.NotNull(xml.Root!.Element("elements")!.Element("page-break")?.Element("paragraph"));
    }

    [Fact]
    public void Lists_tables_and_pictures_come_back()
    {
        var document = new HDocument();
        var section = new HSection();
        document.Sections.Add(section);
        var numbers = new HNumbering();
        numbers.Levels.Add(new HNumberingLevel("DIGIT", "^1.", 1, Bullet: false));
        numbers.Levels.Add(new HNumberingLevel("LATIN_SMALL", "^2)", 1, Bullet: false));
        var bullets = new HNumbering();
        bullets.Levels.Add(new HNumberingLevel("DIGIT", "▪", 1, Bullet: true));
        section.Blocks.Add(new HParagraph { List = new HListRef(numbers, 0, true), Inlines = { new HText("First", default) } });
        section.Blocks.Add(new HParagraph { List = new HListRef(numbers, 1, true), Inlines = { new HText("Nested", default) } });
        section.Blocks.Add(new HParagraph { List = new HListRef(numbers, 0, true), Inlines = { new HText("Second", default) } });
        section.Blocks.Add(new HParagraph { List = new HListRef(bullets, 0, true), Inlines = { new HText("A bullet", default) } });

        var table = new HTable { ColumnCount = 3, ColumnWidths = [10000, 15000, 20000] };
        var head = new HRow { Header = true, Height = 1500 };
        head.Cells.Add(Cell("Wide head", c =>
        {
            c.ColSpan = 2;
            c.Fill = "#DDEEFF";
        }));
        head.Cells.Add(Cell("Tall", c =>
        {
            c.RowSpan = 2;
            c.VerticalAlign = HVerticalAlign.Center;
        }));
        table.Rows.Add(head);
        var middle = new HRow();
        middle.Cells.Add(Cell("A2", c => c.Borders = HBorders.Empty));
        middle.Cells.Add(Cell("B2", c => c.VerticalAlign = HVerticalAlign.Bottom));
        table.Rows.Add(middle);
        section.Blocks.Add(table);
        section.Blocks.Add(Para(new HText("Logo: ", default), new HImage(Picture(MagickColors.Teal)) { Width = 4000, Height = 3000 },
            new HImage(Picture(MagickColors.Red, extension: "gif")), new HText(" end", default)));

        var (read, xml, _) = RoundTrip(document);

        var blocks = read.Sections.Single().Blocks;
        var lists = blocks.Take(4).Cast<HParagraph>().Select(p => p.List!).ToList();
        Assert.Same(lists[0].Numbering, lists[1].Numbering);
        Assert.Same(lists[0].Numbering, lists[2].Numbering);
        Assert.NotSame(lists[0].Numbering, lists[3].Numbering);
        Assert.Equal([0, 1, 0, 0], lists.Select(l => l.Level));
        Assert.Equal(new HNumberingLevel("DIGIT", "^1.", 1, false), lists[0].Numbering.Levels[0]);
        Assert.Equal(new HNumberingLevel("LATIN_SMALL", "^2)", 1, false), lists[0].Numbering.Levels[1]);
        Assert.Equal(new HNumberingLevel("DIGIT", "▪", 1, true), lists[3].Numbering.Levels[0]);
        var listXml = xml.Root!.Element("elements")!.Elements("paragraph").ToList();
        Assert.Equal("NUMBER_TYPE_CHAR_SMALL_PARENTHESIS", listXml[1].Attribute("NumberType")?.Value);
        Assert.Equal("BULLET_TYPE_RECTANGLE", listXml[3].Attribute("BulletType")?.Value);

        var readTable = Assert.IsType<HTable>(blocks[4]);
        Assert.Equal(3, readTable.ColumnCount);
        Assert.Equal([10000 / 45000.0, 15000 / 45000.0, 20000 / 45000.0], readTable.RelativeWidths!.Select(w => Math.Round(w, 4)), (a, b) => Math.Abs(a - b) < 0.001);
        Assert.True(readTable.Rows[0].Header);
        Assert.Equal(1500, readTable.Rows[0].Height);
        var wide = readTable.Rows[0].Cells[0];
        Assert.Equal((2, "#DDEEFF"), (wide.ColSpan, wide.Fill));
        var tall = readTable.Rows[0].Cells[1];
        Assert.Equal(HVerticalAlign.Center, tall.VerticalAlign);
        // The cell over two rows: no bottom border, and under it a cell of its own without the top one.
        Assert.Equal(HBorderStyle.None, tall.Borders!.Value.Bottom.Style);
        Assert.Equal(HBorderStyle.Solid, tall.Borders!.Value.Top.Style);
        Assert.Equal(["A2", "B2", ""], readTable.Rows[1].Cells.Select(c => Text((HParagraph)c.Blocks[0])));
        Assert.Equal(HBorders.Empty, readTable.Rows[1].Cells[0].Borders);
        Assert.Equal(HVerticalAlign.Bottom, readTable.Rows[1].Cells[1].VerticalAlign);
        var under = readTable.Rows[1].Cells[2].Borders!.Value;
        Assert.Equal((HBorderStyle.None, HBorderStyle.Solid), (under.Top.Style, under.Bottom.Style));
        Assert.Equal("rowName", xml.Root!.Descendants("row").First().Attributes().First().Name.LocalName);

        var pictured = Assert.IsType<HParagraph>(blocks[5]);
        var images = pictured.Inlines.OfType<HImage>().ToList();
        Assert.Equal(2, images.Count);
        Assert.Equal((4000, 3000), (images[0].Width, images[0].Height));
        Assert.True(File.Exists(images[1].Path));
        Assert.Equal(".png", Path.GetExtension(images[1].Path)); // the GIF became a PNG
        Assert.Equal("Logo:  end", Text(pictured));
    }

    [Fact]
    public void Sections_notes_text_boxes_and_links_flatten_into_the_one_story()
    {
        var document = new HDocument();
        var first = new HSection { Page = new HPage(84189, 59528, 4000, 4000, 2000, 2000, 2000, 2000, 0) };
        var note = new HNote(false);
        note.Blocks.Add(Para("The note's text."));
        var link = new HLink("https://example.com");
        link.Content.Add(new HText("a link", new HCharFormat(Underline: true)));
        var box = new HTextBox { Width = 5000, Height = 2000 };
        box.Blocks.Add(Para("In a box"));
        first.Blocks.Add(Para(new HBookmark("b"), new HText("Text", default), note, new HText(" and ", default), link, box));
        var second = new HSection();
        second.Blocks.Add(Para("Second section"));
        document.Sections.Add(first);
        document.Sections.Add(second);

        var (read, _, _) = RoundTrip(document);

        Assert.True(read.Sections.Single().Page!.Landscape);
        var paragraphs = Paragraphs(read);
        Assert.Equal(["Text1 and a link", "In a box", "1 The note's text.", "Second section"], paragraphs.Select(Text));
        Assert.True(((HText)paragraphs[0].Inlines[1]).Format.Superscript);
        Assert.True(paragraphs[3].PageBreakBefore);
    }

    [Fact]
    public void Text_xml_cannot_hold_is_made_safe_without_moving_the_runs()
    {
        var document = new HDocument();
        var section = new HSection();
        document.Sections.Add(section);
        section.Blocks.Add(Para(new HText("a]]>b\r\0c\uD800d", default), new HText("😀 bold ]]", new HCharFormat(Bold: true)),
            new HText(">tail & <x>", default)));
        section.Blocks.Add(Para("Türkçe metin: ğüşıöç İ", new HCharFormat(Font: "Times New \"Roman\" <&>")));

        var (read, _, text) = RoundTrip(document);

        Assert.Contains("a]]>b", text, StringComparison.Ordinal);
        var paragraphs = Paragraphs(read);
        Assert.Equal("a]]>bc\uFFFDd😀 bold ]]>tail & <x>", Text(paragraphs[0]));
        Assert.True(((HText)paragraphs[0].Inlines[1]).Format.Bold);
        Assert.Equal("😀 bold ]]", ((HText)paragraphs[0].Inlines[1]).Text);
        Assert.Equal("Türkçe metin: ğüşıöç İ", Text(paragraphs[1]));
        Assert.Equal("Times New \"Roman\" <&>", ((HText)paragraphs[1].Inlines[0]).Format.Font);
    }

    [Fact]
    public void An_empty_model_still_makes_a_document_with_a_paragraph()
    {
        var (read, _, text) = RoundTrip(new HDocument());
        Assert.Equal("\n\n", text);
        Assert.Empty(Assert.IsType<HParagraph>(Assert.Single(read.Sections.Single().Blocks)).Inlines);
        Assert.Equal(new HPage(59528, 84189, 4253, 4253, 2000, 2000, 2253, 2253, 0), read.Sections.Single().Page);
    }

    // ───────────────────────── Routes ─────────────────────────

    [Fact]
    public void Documents_go_to_udf_through_the_writer_and_udf_reaches_sheets()
    {
        var planner = fx.Catalog.CreatePlanner();
        foreach (var from in new[] { "docx", "odt", "rtf", "hwp", "hwpx", "md", "html", "txt", "doc", "pdf" })
            Assert.Equal("udf-writer", Assert.Single(planner.Plan(from, "udf")!.Steps).Converter.Id);
        Assert.Null(planner.Plan("udf", "udf"));
        foreach (var target in new[] { "xlsx", "ods", "csv" })
            Assert.Equal("document-tables", Assert.Single(planner.Plan("udf", target)!.Steps).Converter.Id);
        // A document written as UDF on the way never unpacks: Extract takes only a .udf itself.
        foreach (var from in new[] { "md", "docx", "pdf" })
            Assert.DoesNotContain(planner.Plan(from, Filee.Core.Formats.FormatRegistry.Folder)?.Steps ?? [], s => s.Converter.Id == "udf-writer");
        Assert.True(Filee.Core.Formats.FormatRegistry.FindById("udf")!.Writable);
    }

    [Fact]
    public async Task Markdown_becomes_a_udf_and_a_udf_with_a_table_becomes_a_workbook()
    {
        var folder = fx.NewFolder();
        var markdown = Path.Combine(folder, "petition.md");
        await File.WriteAllTextAsync(markdown, "# Dilekçe\n\nMetin **kalın** ve *italik*.\n\n| Ad | Tutar |\n|---|---|\n| Vekalet | 81.388,81 |\n| Masraf | 120 |\n",
            TestContext.Current.CancellationToken);

        var job = await fx.ConvertAsync([markdown], new Preset { TargetFormat = "udf" });
        Assert.True(job.State == JobState.Completed, string.Join("; ", job.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        var udf = Assert.Single(job.Outputs);
        AssertValidUdf(udf);
        var read = UyapReader.Read(udf, Path.Combine(folder, "media"));
        var heading = read.Sections[0].Blocks.OfType<HParagraph>().First();
        Assert.Equal("Dilekçe", Text(heading));
        Assert.True(heading.Inlines.OfType<HText>().All(t => t.Format.Bold == true));

        var sheets = await fx.ConvertAsync([udf], new Preset { TargetFormat = "xlsx" });
        Assert.True(sheets.State == JobState.Completed, string.Join("; ", sheets.Files.Select(f => $"{f.ErrorKey} {f.ErrorDetail}")));
        using var workbook = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(Assert.Single(sheets.Outputs), false);
        Assert.Single(workbook.WorkbookPart!.Workbook!.Sheets!.ChildElements);
    }

    // ───────────────────────── Limits ─────────────────────────

    [Fact]
    public void A_part_beyond_the_size_limit_is_refused_without_leaving_anything()
    {
        var document = new HDocument();
        var section = new HSection();
        for (var i = 0; i < 2000; i++)
            section.Blocks.Add(Para($"Paragraph {i} with some text to fill the file."));
        document.Sections.Add(section);
        var folder = fx.NewFolder();

        var refused = Assert.Throws<ConversionRefusedException>(() =>
            UyapWriter.Write(document, Path.Combine(folder, "big.udf"), new DocumentLimits { MaxPartBytes = 20_000 }, TestContext.Current.CancellationToken));
        Assert.Equal(DocumentLimits.ErrorKey, refused.ErrorKey);
        Assert.Throws<ConversionRefusedException>(() =>
            UyapWriter.Write(document, Path.Combine(folder, "many.udf"), new DocumentLimits { MaxXmlElements = 1000 }, TestContext.Current.CancellationToken));
        Assert.Throws<ConversionRefusedException>(() =>
            UyapWriter.Write(document, Path.Combine(folder, "long.udf"), new DocumentLimits { MaxParagraphs = 100 }, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public void A_part_that_would_pass_the_inflate_ratio_is_stored()
    {
        var document = new HDocument();
        var section = new HSection();
        for (var i = 0; i < 3000; i++)
            section.Blocks.Add(Para("Same text, again and again."));
        document.Sections.Add(section);
        var path = Path.Combine(fx.NewFolder(), "same.udf");

        UyapWriter.Write(document, path, new DocumentLimits { InflateRatioFloor = 10_000, MaxInflateRatio = 3 }, TestContext.Current.CancellationToken);

        using var zip = ZipFile.OpenRead(path);
        var entry = Assert.Single(zip.Entries);
        Assert.Equal(entry.Length, entry.CompressedLength);
        Assert.Equal(3000, UyapReader.Read(path, fx.NewFolder()).Sections[0].Blocks.Count);
    }

    [Fact]
    public void Hostile_models_are_refused_or_written_quickly()
    {
        // Tables 5,000 deep are opened up into paragraphs past the nesting limit.
        var deep = new HDocument();
        var section = new HSection();
        deep.Sections.Add(section);
        var blocks = section.Blocks;
        for (var i = 0; i < 5000; i++)
        {
            var table = new HTable { ColumnCount = 1 };
            var row = new HRow();
            var cell = new HCell();
            cell.Blocks.Add(Para($"level {i}"));
            row.Cells.Add(cell);
            table.Rows.Add(row);
            blocks.Add(table);
            blocks = cell.Blocks;
        }
        var started = System.Diagnostics.Stopwatch.StartNew();
        var (read, _, text) = RoundTrip(deep);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"Took {started.Elapsed}.");
        Assert.NotEmpty(read.Sections[0].Blocks);
        Assert.Contains("level 4999\n", text, StringComparison.Ordinal);

        // A cell spanning 16,384 columns over 1,000 rows passes the table cell limit.
        var wide = new HDocument();
        var wideSection = new HSection();
        var huge = new HTable { ColumnCount = 16_384 };
        for (var r = 0; r < 1000; r++)
        {
            var row = new HRow();
            row.Cells.Add(new HCell { ColSpan = 16_384 });
            huge.Rows.Add(row);
        }
        wideSection.Blocks.Add(huge);
        wide.Sections.Add(wideSection);
        var folder = fx.NewFolder();
        Assert.Throws<ConversionRefusedException>(() => UyapWriter.Write(wide, Path.Combine(folder, "wide.udf"), TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(folder));

        // A picture whose header claims ten gigapixels is left out.
        var claiming = DocumentFuzzTests.TinyPng(4, 3);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(claiming.AsSpan(16), 100_000);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(claiming.AsSpan(20), 100_000);
        var picture = Path.Combine(fx.NewFolder(), "huge.png");
        File.WriteAllBytes(picture, claiming);
        var pictured = new HDocument();
        var pictureSection = new HSection();
        pictureSection.Blocks.Add(Para(new HText("x", default), new HImage(picture), new HImage(Path.Combine(folder, "missing.png"))));
        pictured.Sections.Add(pictureSection);
        var (_, xml, _) = RoundTrip(pictured);
        Assert.Empty(xml.Descendants("image"));
    }

    [Fact]
    public void A_cancelled_write_leaves_no_file()
    {
        var folder = fx.NewFolder();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var document = new HDocument();
        var section = new HSection();
        section.Blocks.Add(Para("text"));
        document.Sections.Add(section);

        Assert.ThrowsAny<OperationCanceledException>(() => UyapWriter.Write(document, Path.Combine(folder, "out.udf"), cancelled.Token));

        Assert.Empty(Directory.GetFiles(folder));
    }
}
