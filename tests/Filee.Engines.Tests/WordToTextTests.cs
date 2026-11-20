// Word (DOCX, DOC) and ODT → TXT on the built-in route, as RTF → TXT (#1204, RtfToBuiltInTextTests): the document's
// own paragraphs a line each, with their leading spaces, tabs, runs of spaces and empty paragraphs, list numbers (and
// the tab after them, as Word and LibreOffice write it) and table rows on lines of their own, page numbers as the file
// showed them and a paragraph that holds only a page break as an empty line. Before, with the Filee renderer on, the text came from the renderer's PDF. The
// routes with LibreOffice are in FileeRendererConverterTests. The documents are built here.

using Filee.Core.Conversion;
using Filee.Core.Settings;
using Filee.Engines.Hwp;
using Filee.Engines.Hwp.Hwpx;
using Filee.Engines.Infrastructure;
using static Filee.Engines.Tests.DocxBuilder;

namespace Filee.Engines.Tests;

[Collection("EngineEnvironment")] // the Filee renderer is on, as in the app, so the route through a PDF is there to lose
public sealed class WordToBuiltInTextTests(EngineFixture fx) : IClassFixture<EngineFixture>, IDisposable
{
    /// <summary>What every document here comes out as.</summary>
    private static readonly string[] Expected =
    [
        "Notes on the build",
        "", // the empty paragraph, and no empty line between the others
        "Steps:",
        "1.\tInstall the tools", // a tab after the number, as Word and LibreOffice write it
        "2.\tRun the build",
        "    •\ton Windows",
        "int main(void) {",
        "    int total = 0;  // two spaces",
        "\tfor (int i = 0; i < 3; i++)",
        "        total += i;",
        "    return total;",
        "}",
        "first", // a line break
        "second",
        "Name\tValue",
        "Width\t120",
        "End.",
        "",
    ];

    public void Dispose()
    {
        EngineEnvironment.FileeRenderer = false;
        fx.Catalog.Refresh();
    }

    private async Task<string> ConvertAsync(string source, string format)
    {
        EngineEnvironment.FileeRenderer = true;
        var catalog = new ConverterCatalog(fx.Converters.Where(c => c.Id is not ("libreoffice" or "pandoc")))
        {
            Priority = new AppSettings().EnginePriority,
        };
        // Not the renderer's PDF and the PDF's text, which wrapped long lines and lost the indentation.
        Assert.Equal(HwpTextConverter.EngineId, Assert.Single(catalog.CreatePlanner().Plan(format, "txt")!.Steps).Converter.Id);

        var output = await MarkupTestKit.ConvertAsync(catalog, source, "txt");
        return (await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken)).TrimStart('﻿').ReplaceLineEndings("\n");
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("docm")]
    [InlineData("dotx")]
    public async Task Docx_comes_out_a_line_per_paragraph(string format)
    {
        var docx = new DocxBuilder
        {
            Numbering = """
                <w:abstractNum w:abstractNumId="1">
                  <w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1."/><w:pPr><w:ind w:left="720" w:hanging="360"/></w:pPr></w:lvl>
                  <w:lvl w:ilvl="1"><w:numFmt w:val="bullet"/><w:lvlText w:val="&#xF0B7;"/><w:rPr><w:rFonts w:ascii="Symbol" w:hAnsi="Symbol"/></w:rPr><w:pPr><w:ind w:left="1440" w:hanging="360"/></w:pPr></w:lvl>
                </w:abstractNum>
                <w:num w:numId="1"><w:abstractNumId w:val="1"/></w:num>
                """,
        };
        static string Item(string text, int level) => P(R(text), $"<w:numPr><w:ilvl w:val=\"{level}\"/><w:numId w:val=\"1\"/></w:numPr>");
        static string Cell(string text) => $"<w:tc><w:tcPr><w:tcW w:w=\"2000\" w:type=\"dxa\"/></w:tcPr>{P(R(text))}</w:tc>";
        docx.Paragraph(P(R("Notes on the build"), "<w:pStyle w:val=\"Heading1\"/>"))
            .Paragraph(P(""))
            .Paragraph(P(R("Steps:")))
            .Paragraph(Item("Install the tools", 0))
            .Paragraph(Item("Run the build", 0))
            .Paragraph(Item("on Windows", 1))
            .Paragraph(P(R("int main(void) {"), "<w:ind w:left=\"720\"/>")) // the indentation of the paragraph isn't text
            .Paragraph(P(R("    int total = 0;") + R("  // two spaces", "<w:color w:val=\"008000\"/>")))
            .Paragraph(P("<w:r><w:tab/></w:r>" + R("for (int i = 0; i < 3; i++)")))
            .Paragraph(P(R("        total += i;")))
            .Paragraph(P(R("    return total;")))
            .Paragraph(P(R("}")))
            .Paragraph(P(R("first") + "<w:r><w:br/></w:r>" + R("second")))
            .Paragraph("<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"2000\"/><w:gridCol w:w=\"2000\"/></w:tblGrid>" +
                       $"<w:tr>{Cell("Name")}{Cell("Value")}</w:tr><w:tr>{Cell("Width")}{Cell("120")}</w:tr></w:tbl>")
            .Paragraph(P(R("End.")))
            .Paragraph(P("")); // Word's last empty paragraph: no empty line at the end
        var source = docx.Save(Path.Combine(fx.NewFolder(), "notes." + format));

        Assert.Equal(string.Join("\n", Expected), await ConvertAsync(source, format));
    }

    [Fact]
    public async Task Odt_comes_out_a_line_per_paragraph()
    {
        var odt = new OdtBuilder
        {
            ContentStyles = """
                <text:list-style style:name="L1">
                  <text:list-level-style-number text:level="1" style:num-suffix="." style:num-format="1"/>
                  <text:list-level-style-bullet text:level="2" text:bullet-char="•"/>
                </text:list-style>
                """,
            Body = """
                <text:h text:outline-level="1">Notes on the build</text:h>
                <text:p/>
                <text:p>Steps:</text:p>
                <text:list text:style-name="L1">
                  <text:list-item><text:p>Install the tools</text:p></text:list-item>
                  <text:list-item><text:p>Run the build</text:p>
                    <text:list><text:list-item><text:p>on Windows</text:p></text:list-item></text:list>
                  </text:list-item>
                </text:list>
                <text:p>int main(void) {</text:p>
                <text:p><text:s text:c="4"/>int total = 0; <text:s/>// two spaces</text:p>
                <text:p><text:tab/>for (int i = 0; i &lt; 3; i++)</text:p>
                <text:p><text:s text:c="8"/>total += i;</text:p>
                <text:p><text:s text:c="4"/>return total;</text:p>
                <text:p>}</text:p>
                <text:p>first<text:line-break/>second</text:p>
                <table:table>
                  <table:table-column table:number-columns-repeated="2"/>
                  <table:table-row><table:table-cell><text:p>Name</text:p></table:table-cell><table:table-cell><text:p>Value</text:p></table:table-cell></table:table-row>
                  <table:table-row><table:table-cell><text:p>Width</text:p></table:table-cell><table:table-cell><text:p>120</text:p></table:table-cell></table:table-row>
                </table:table>
                <text:p>End.</text:p>
                """,
        };
        var source = odt.Save(Path.Combine(fx.NewFolder(), "notes.odt"));

        Assert.Equal(string.Join("\n", Expected), await ConvertAsync(source, "odt"));
    }

    [Fact]
    public async Task Doc_comes_out_a_line_per_paragraph()
    {
        var doc = new DocBuilder();
        var numbered = doc.List(100, [new DocBuilder.Level(0, "\u0000.", 1, DocBuilder.Sprm(0x845E, (short)720)),
            new DocBuilder.Level(23, "", 1, DocBuilder.Sprm(0x845E, (short)1440))]);
        byte[] Item(int level) => [.. DocBuilder.Sprm(0x460B, (short)numbered), .. DocBuilder.Sprm(0x260A, (byte)level)];
        var tap = (byte[])[.. DocBuilder.DefTable([0, 2000, 4000])];
        doc.Paragraph(DocBuilder.R("Notes on the build"))
            .Paragraph()
            .Paragraph(DocBuilder.R("Steps:"))
            .Paragraph(0, Item(0), DocBuilder.R("Install the tools"))
            .Paragraph(0, Item(0), DocBuilder.R("Run the build"))
            .Paragraph(0, Item(1), DocBuilder.R("on Windows"))
            .Paragraph(DocBuilder.R("int main(void) {"))
            .Paragraph(DocBuilder.R("    int total = 0;  // two spaces"))
            .Paragraph(DocBuilder.R("\tfor (int i = 0; i < 3; i++)"))
            .Paragraph(DocBuilder.R("        total += i;"))
            .Paragraph(DocBuilder.R("    return total;"))
            .Paragraph(DocBuilder.R("}"))
            .Paragraph(DocBuilder.R("first\vsecond")) // Shift+Enter
            .Row(tap, [DocBuilder.R("Name")], [DocBuilder.R("Value")])
            .Row(tap, [DocBuilder.R("Width")], [DocBuilder.R("120")])
            .Paragraph(DocBuilder.R("End."));
        var source = Path.Combine(fx.NewFolder(), "notes.doc");
        await File.WriteAllBytesAsync(source, doc.Build(), TestContext.Current.CancellationToken);

        Assert.Equal(string.Join("\n", Expected), await ConvertAsync(source, "doc"));
    }

    [Fact]
    public async Task Page_fields_write_the_number_the_file_showed()
    {
        // LibreOffice writes the number too; before, the field came out empty ("Page  of ").
        var docx = new DocxBuilder()
            .Paragraph(P(R("Page ") + Field("PAGE", "3") + R(" of ") + Field("NUMPAGES", "7")))
            .Paragraph(P(R("Simple ") + "<w:fldSimple w:instr=\" PAGE \">" + R("iv") + "</w:fldSimple>"))
            .Save(Path.Combine(fx.NewFolder(), "pages.docx"));
        Assert.Equal("Page 3 of 7\nSimple iv\n", await ConvertAsync(docx, "docx"));

        var odt = new OdtBuilder
        {
            Body = """
                <text:p>Page <text:page-number text:select-page="current">3</text:page-number> of <text:page-count>7</text:page-count></text:p>
                <text:p>Placeholder <text:page-number>&lt;number&gt;</text:page-number></text:p>
                """,
        }.Save(Path.Combine(fx.NewFolder(), "pages.odt"));
        // A field without a number of its own (Impress's placeholder) is counted from the page breaks.
        Assert.Equal("Page 3 of 7\nPlaceholder 1\n", await ConvertAsync(odt, "odt"));
    }

    [Fact]
    public void A_page_field_without_a_number_counts_the_page_breaks_and_sections()
    {
        var document = new HDocument();
        var first = new HSection();
        first.Blocks.Add(Paragraph("a", new HField(HFieldKind.PageNumber, default)));
        var broken = Paragraph("b", new HField(HFieldKind.PageNumber, default), new HText(" of ", default), new HField(HFieldKind.TotalPages, default));
        broken.PageBreakBefore = true;
        first.Blocks.Add(broken);
        var second = new HSection { StartPageNumber = 10 };
        second.Blocks.Add(Paragraph("c", new HField(HFieldKind.PageNumber, default) { NumberFormat = "ROMAN_SMALL" }));
        document.Sections.AddRange([first, second]);

        Assert.Equal("a1\nb2 of 3\ncx\n", PlainTextWriter.WriteLines(document).ReplaceLineEndings("\n"));
        Assert.Equal("a1\n\nb2 of 3\n\ncx\n", PlainTextWriter.Write(document).ReplaceLineEndings("\n"));

        static HParagraph Paragraph(string text, params HInline[] inlines)
        {
            var paragraph = new HParagraph();
            paragraph.Inlines.Add(new HText(text, default));
            paragraph.Inlines.AddRange(inlines);
            return paragraph;
        }
    }

    [Fact]
    public async Task A_paragraph_that_holds_only_a_page_break_is_an_empty_line()
    {
        // Word keeps the paragraph's mark on the old page and the layout leaves it out, but its plain text has the line,
        // as LibreOffice's has. No form feed: neither writes one.
        const string PageBreak = "<w:r><w:br w:type=\"page\"/></w:r>";
        var docx = new DocxBuilder()
            .Paragraph(P(R("before")))
            .Paragraph(P(PageBreak))
            .Paragraph(P(R("after")))
            .Paragraph(P(R("end of a page") + PageBreak)) // the break ends the paragraph: its mark is an empty line too
            .Paragraph(P(R("next")))
            .Paragraph(P(PageBreak))
            .Paragraph(P(PageBreak))
            .Paragraph(P(R("two pages on")))
            .Paragraph(P(R("in the") + PageBreak + R("middle"))) // no empty line
            .Save(Path.Combine(fx.NewFolder(), "breaks.docx"));
        var text = await ConvertAsync(docx, "docx");

        Assert.Equal("before\n\nafter\nend of a page\n\nnext\n\n\ntwo pages on\nin the\nmiddle\n", text);
        Assert.DoesNotContain('\f', text);
        // The layout's model is as it was: the break paragraphs are left out, the next paragraph starts the page.
        var model = Hwp.Hwpx.Docx.DocxReader.Read(docx, fx.NewFolder());
        var paragraphs = model.Sections.Single().Blocks.OfType<HParagraph>().ToList();
        Assert.Equal(["before", "after", "end of a page", "next", "two pages on", "in the", "middle"],
            paragraphs.Select(p => string.Concat(p.Inlines.OfType<HText>().Select(t => t.Text))));
        Assert.Equal([0, 1, 0, 1, 2, 0, 0], paragraphs.Select(p => p.BreakParagraphsBefore));
    }

    [Fact]
    public async Task A_list_number_is_followed_by_what_its_word_level_asks_for()
    {
        var docx = new DocxBuilder
        {
            Numbering = """
                <w:abstractNum w:abstractNumId="1">
                  <w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:suff w:val="space"/><w:lvlText w:val="%1."/></w:lvl>
                  <w:lvl w:ilvl="1"><w:start w:val="1"/><w:numFmt w:val="lowerLetter"/><w:suff w:val="nothing"/><w:lvlText w:val="(%2)"/></w:lvl>
                </w:abstractNum>
                <w:num w:numId="1"><w:abstractNumId w:val="1"/></w:num>
                """,
        };
        static string Item(string text, int level) => P(R(text), $"<w:numPr><w:ilvl w:val=\"{level}\"/><w:numId w:val=\"1\"/></w:numPr>");
        docx.Paragraph(Item("spaced", 0)).Paragraph(Item("tight", 1)).Paragraph(P(R("line") + "<w:r><w:br/></w:r>" + R("break"),
            "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr>"));
        var source = docx.Save(Path.Combine(fx.NewFolder(), "suffix.docx"));

        Assert.Equal("1. spaced\n    (a)tight\n2. line\n   break\n", await ConvertAsync(source, "docx"));
    }

    [Fact]
    public void A_line_break_in_a_list_item_lines_up_under_its_text()
    {
        var numbering = new HNumbering { Levels = { new HNumberingLevel("DIGIT", "^1.", 1, Bullet: false) } };
        var item = new HParagraph { List = new HListRef(numbering, 0, Numbered: true) };
        item.Inlines.AddRange([new HText("first", default), new HLineBreak(default), new HText("second", default)]);
        var document = new HDocument();
        document.Sections.Add(new HSection { Blocks = { item } });

        // The tab under the number's tab reaches the same tab stop in an editor.
        Assert.Equal("1.\tfirst\n  \tsecond\n", PlainTextWriter.WriteLines(document).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_table_in_a_cell_keeps_its_text_in_the_cell()
    {
        var inner = new HTable();
        var innerRow = new HRow();
        innerRow.Cells.AddRange([Cell(Paragraph("inner a")), Cell(Paragraph("inner b"))]);
        inner.Rows.Add(innerRow);
        var outer = new HTable();
        var row = new HRow();
        var nested = Cell(Paragraph("before"));
        nested.Blocks.Add(inner);
        row.Cells.AddRange([Cell(Paragraph("left")), nested]);
        outer.Rows.Add(row);
        var document = new HDocument();
        document.Sections.Add(new HSection { Blocks = { outer } });

        Assert.Equal("left\tbefore inner a inner b\n", PlainTextWriter.WriteLines(document).ReplaceLineEndings("\n"));
        Assert.Equal("left\tbefore inner a inner b\n", PlainTextWriter.Write(document).ReplaceLineEndings("\n"));

        static HParagraph Paragraph(string text)
        {
            var paragraph = new HParagraph();
            paragraph.Inlines.Add(new HText(text, default));
            return paragraph;
        }

        static HCell Cell(HParagraph paragraph)
        {
            var cell = new HCell();
            cell.Blocks.Add(paragraph);
            return cell;
        }
    }
}
