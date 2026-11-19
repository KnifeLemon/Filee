// Word (DOCX, DOC) and ODT → TXT on the built-in route, as RTF → TXT (#1204, RtfToBuiltInTextTests): the document's
// own paragraphs a line each, with their leading spaces, tabs, runs of spaces and empty paragraphs, list numbers and
// table rows on lines of their own. Before, with the Filee renderer on, the text came from the renderer's PDF. The
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
        "1. Install the tools",
        "2. Run the build",
        "    • on Windows",
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
